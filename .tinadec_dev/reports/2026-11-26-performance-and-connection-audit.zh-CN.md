# 全链路性能与连接问题实地排查报告（2026-11-26）

> 基线：分支 `main` @ `de0f0ec7`（干净工作树）。
> 本报告记录对本机真实运行态（`TinadecCore.Api` PID 9380、`TinadecGateway` PID 34324、`Electron` PID 35324、`Vite` PID 41672）与全仓源码的实地取证、性能画像和根因定位。

---

## 一、实地观测基线（修复前）

### 1.1 运行态资源与延迟实测

1. **进程空闲 CPU 与日志异常**：
   - `TinadecCore.Api` (PID 9380) 在无任何人工操作、无活跃模型 run 的空闲状态下，CPU 时间在 8 秒内增长 2.9s、398 秒内增长 107s，**持续占用 ~20%–36% 单核 CPU**。
   - 同时发现本机存在一个 4 天前遗留的旧进程 `TinadecCore.Api` (PID 21500，已累计 7,215s CPU)。
   - 两个已挂载项目作用域的日志目录爆满且正在持续高速写入轮转：
     - `C:\git\agent\TinadecOffice\.tinadec\logs\`：20 个 × 10 MiB 轮转文件（约 **200 MiB**），内容几乎全部是 `Microsoft.EntityFrameworkCore.Database.Command: Executed DbCommand`。
     - `C:\git\agent\RouterAPI\.tinadec\logs\`：20 个 × 10 MiB 轮转文件（约 **200 MiB**），同上。
   - 用户级 `C:\Users\lincu\.tinadec\logs\core.log` 记录：
     - 启动耗时：迁移完成后 `18:10:44.325` → 开始监听 `18:11:35.925`，**冷启动耗时 51.6 秒**。
     - Kestrel 线程池饥饿告警：`As of "11/25/2026 17:18:17 +00:00", the heartbeat has been running for "00:00:01.5617004" which is longer than "00:00:01". This could be caused by thread pool starvation.`
     - `UiPreferencesEndpoints` SSE 断开未捕获异常（高频出现）：`System.NotSupportedException: Specified method is not supported. at TinadecCore.Runtime.UiPreferencesService.Subscribe(CancellationToken ct)+IAsyncDisposable.DisposeAsync() at UiPreferencesEndpoints.cs:137`。

2. **真实本机端点墙钟耗时（`127.0.0.1:48730`）**：

   | 端点 | 首次耗时 | 复测耗时 | Gateway `Server-Timing` |
   |---|---:|---:|---|
   | `GET /api/v1/health` | 637 ms | 25 ms | `total;dur=3` |
   | `GET /api/v1/projects` | 289 ms | 39 ms | `total;dur=32.6` |
   | `GET /api/v1/sessions` | 1,902 ms | **4,483 ms** | `total;dur=4448.3` |
   | `GET /api/v1/agents` | 1,747 ms | **2,365 ms** | `total;dur=2336.5` |
   | `GET /api/v1/model-center/snapshot` | 2,271 ms | **5,120 ms** | `providers=1715, catalog-readiness=4673, model-readiness=4818.4, readiness=5097.9, total=5102.7` |
   | `GET /api/v1/companions` | 498 ms | — | — |

3. **Electron 主进程与 Gateway 日志证据**：
   - `electron.log` 多次记录：`host_identity_unavailable`（启动约 29 秒后触发；而 Core 51 秒后才就绪）与 `Error occurred in handler for 'tinadec:host-status': Error: Host status requires a trusted host page.`。
   - `gateway.log` 记录未处理的 `DOMException { name: "AbortError", message: "The connection was closed." }`。

4. **5 秒 `dotnet-trace` 真实 CPU/时间剖析（空闲态 PID 9380）**：
   - `ConfigurationProjectionCoordinator.AttachAsync`：**12,865.3 ms**（多线程并发等待累计）
   - `ScopeConfigurationDocuments.ReadCoreAsync`：**9,939.2 ms**
   - `SqliteConnectionPragmaInterceptor.ApplyPragmasAsync`：**6,579.0 ms**
   - `ProjectBoardEndpoints.GetGlobalBoardSummaryAsync`：**4,278.0 ms**
   - `ScopeDiagnosticLoggerProvider.Write`：**3,918.7 ms**（其中 EF `EventDefinition` 占 2,227.2 ms）
   - `StorageMaintenanceService.OwnedFiles`：**2,506.4 ms**
   - `ToolSkillResourceService.ListAsync`：**2,324.7 ms**
   - `TinaChatService.ProcessPendingWakesAsync`：**2,211.1 ms**

---

## 二、已定位的性能与连接问题清单

### P0-1：项目作用域 Logger 无 EF Core 级别过滤 + 每条日志递归扫目录

- **位置**：
  - `TinadecCore/Runtime/StorageScopeRegistry.cs:445`
  - `TinadecCore/Runtime/ScopeDiagnosticLoggerProvider.cs:33-56`
- **根因**：
  1. `MountAsync` 为项目作用域构建 `LoggerFactory` 时只调用了 `builder.AddProvider(new ScopeDiagnosticLoggerProvider(scope))`，**未加载主机 `Logging` 配置，也未过滤 `Microsoft.EntityFrameworkCore`（默认 `Information`）**。于是每 100ms–2s 跑一次的后台轮询 SQL 全部以 `Information` 写入 `<project>/.tinadec/logs/core.log`。
  2. `ScopeDiagnosticLoggerProvider.Write` 对每一条日志都在 `lock (_gate)` 内执行 `RejectLinks`（逐级父目录 stat）+ 同步开闭 `FileStream` + 调用 `StorageMaintenanceService.OwnedFiles(_scope.Root, _scope.Logs)` 递归枚举整个日志目录并逐文件 `File.GetAttributes` 与 `new FileInfo(entry).Length`。
- **影响**：空载即消耗 ~20–36% CPU、不断刷爆 200 MiB/作用域 的磁盘日志、同步锁阻塞 ThreadPool 引发 Kestrel 线程池饥饿。

### P0-2：配置投影每次创建 DbContext 重复校验 Schema、全量读算 SHA-256、验证器 O(N²) 同步阻塞

- **位置**：
  - `TinadecCore/Persistence/Configuration/ConfigurationProjectionCoordinator.cs`
  - `TinadecCore/Persistence/Configuration/ScopeConfigurationDocuments.cs`
  - `TinadecCore/Persistence/Configuration/ConfigurationProjectionDocumentValidator.cs`
- **根因**：
  1. `ConfigurationProjectionDbContextFactory.CreateDbContextAsync` 每次打开配置 DbContext 都进入 `AttachAsync` 并争抢同一把 `SemaphoreSlim _gate(1, 1)`，所有只读接口（`model-center/snapshot`、`agents`、`sessions` 等）全部串行排队。
  2. `AttachAsync` 每次都无条件调用 `DbContextSchemaBootstrapper.EnsureColumnsAsync(db)`（执行 `sqlite_master JOIN pragma_table_info(m.name)` 全库表结构扫描）和 `TablesExistAsync`（按实体循环查 `sqlite_master`），未按 `(coordinator, dbContextType)` 缓存已结算状态。
  3. `ReadForProjectionAsync` 每次都全量 `File.ReadAllTextAsync` + 计算整个 `.toml` 文件 SHA-256，未先基于文件元数据（`Length` / `LastWriteTimeUtc` 或 watcher 失效标记）做快速命中。
  4. `ConfigurationProjectionDocumentValidator.Validate(text)`（在 `ReadAsync` / `CompileAsync` / `VerifyAsync` 上运行）存在严重放大：
     - 用 `.GetAwaiter().GetResult()` 同步阻塞 ThreadPool 线程；
     - `previous.FirstOrDefault(r => EntityKey(entity, r) == key)` 对历史表执行 **O(N²) 线性扫描与反射主键拼接**（实测 user 库有 647 条 `agent_versions`、257 条 `mode_versions`、426 条 `prompt_versions`），叠加逐行 `JsonDocument.Parse` 与 `JsonElement.DeepEquals`，直接把 Core 冷启动与 `VerifyAsync` 拖慢数十秒。

### P0-3：`UiPreferencesEndpoints` SSE 断开必抛 `System.NotSupportedException`

- **位置**：`TinadecCore/AspNetCore/Endpoints/UiPreferencesEndpoints.cs:128-169`
- **根因**：`StreamPreferencesEventsAsync` 启动 `pendingMoveNext = enumerator.MoveNextAsync().AsTask()` 后进入 `Task.WhenAny(pendingMoveNext, delayTask)`；一旦在心跳超时分支或异常/取消分支提前离开 `try` 块，`pendingMoveNext` 仍处于挂起运行态，此时 `await using var enumerator` 触发 C# async iterator 的 `DisposeAsync()` 就会按语言规范抛出 `System.NotSupportedException: Specified method is not supported.`。

### P0-4：`hostConnection.cjs` 重试耗尽后永久卡在 `host_unavailable`（连接无法自愈）

- **位置**：
  - `apps/desktop/electron/hostConnection.cjs:3,59`
  - `apps/desktop/src/composables/useConnection.ts:120-164`
- **根因**：
  1. `hostConnection.cjs` 的 `retryDelaysMs = [1_000, 2_000, 5_000, 10_000]` 总窗口仅约 18 秒；当 Core 冷启动超过 ~20–30 秒时，重试次数耗尽后彻底停止调度后续探测，状态永久停留在 `unavailable`。
  2. 渲染层 `useConnection.ts` 的 `startHealthWatch()` 每 6 秒仅调用 `readHostStatus()` 读取主进程静态缓存快照，并不触发重新校验；因此即便 Core 稍后就绪，Desktop 依然永久停留在断连错误横幅，只能手动点重试。

### P1-5：热接口全量扇出与重复计算（`/board/summary`、`/model-center/snapshot`、`/agents`、`/sessions`）

- **位置**：
  - `TinadecCore/AspNetCore/Endpoints/ProjectBoardEndpoints.cs:778-840` & `apps/desktop/src/components/AppSidebar.vue:582-630`
  - `TinadecCore/AspNetCore/Endpoints/StubEndpoints.cs:61,130` & `TinadecCore/Runtime/ReadinessService.cs:105-202,583-604`
  - `TinadecCore/AspNetCore/ControlPlaneService.cs:149-165,211`
  - `TinadecCore/AspNetCore/Endpoints/AgentConfigurationEndpoints.cs:229-231`
  - `TinadecCore/AspNetCore/Endpoints/StorageEndpoints.cs:134-147`
- **根因**：
  1. `CollectGlobalProjectsAndTasksAsync` 对每个 scope 内的每个 active project **串行 N+1 循环**打开 DbContext 并查询任务（实测 user 库因历史测试残余有 209 个项目，单次 `/api/v1/board/summary` 耗时 **4.28 秒**），且 `AppSidebar.vue` 初始化既调一次 `getGlobalBoardSummary()`，又在 `board/events` 收到初始 `snapshot`（无 `summary`）时再调一次。
  2. `GET /api/v1/model-center/snapshot` 并行请求 `/readiness`、`/model-readiness`、`/model-catalog-readiness`，导致 `ReadinessService.GetReceiptAsync` 被并发执行 **3 次**：每次都在磁盘/内容库执行 `PutAsync(WriteThrough) → OpenReadAsync → DeleteAsync` 探测，且并发抢 `_gate`。此外 `ReadinessService.ProbeExecutablePath` 仍硬编码 `"net10.0"`，而实际已升级到 `net11.0`。
  3. `ControlPlaneService.ListProviders` 先查 provider 列表，再为每一行各自再开一次 `ModelControlDbContext`（N+1 + 抢 `_gate`），并且同步阻塞调用 `_secrets.ExistsAsync(...).GetAwaiter().GetResult()`。
  4. `AgentConfigurationEndpoints.ListAgents` 全表拉取所有 `succeeded` `ModelInvocations` 到内存后再分组取最新一条。
  5. `StorageEndpoints.GetSessions` 串行遍历 `registry.List()` 各个作用域。

### P1-6：Gateway SSE 与流式取消异常处理不完整

- **位置**：
  - `TinadecGateway/src/coreClient.ts:27-35`
  - `TinadecGateway/src/streaming.ts:28-46,80-141`
  - `TinadecGateway/src/index.ts:357-389`
- **核实说明**：经代码核对，`projectBoardRoutes.ts` 与 `liveExplainerRoutes.ts` 均已调用 `keepStreamOpen(context.server, request)`；真正导致 `gateway.log` 出现未捕获 `AbortError` 的根因在于流式取消清理链路：
  1. `wrapSseWithKeepalive` 的 `cleanup()` 调用 `void reader?.cancel()` 未挂 `.catch(() => {})`，在 Bun Windows 下中止已关闭的流会产生未处理的 `Promise` rejection（`DOMException { name: "AbortError" }`）；且 `reader.read()` 因客户端断开抛出 `AbortError` 时调用了 `controller.error(err)` 而非干净 `controller.close()`。
  2. `proxyStream` 未调用 `rethrowCancellation`，客户端主动中止时仍会伪造 502/503 上游不可达响应。
  3. Gateway `.onError` 在 `request.signal.aborted` 或取消错误到达时仍按 `500 internal_error` 响应。

### P1-7：Electron 主进程日志同步递归扫目录 & Vite 未忽略 `.runtime-cache`

- **位置**：
  - `apps/desktop/electron/logSink.cjs:50-65`
  - `apps/desktop/vite.config.ts:53`
- **根因**：
  1. `logSink.cjs` 每次 `write()` 均同步 `stat/appendFileSync` 并递归遍历 `logs/` 目录执行 `statSync` 检查总预算。
  2. `apps/desktop/vite.config.ts` 的 `server.watch.ignored` 缺少 `'**/.runtime-cache/**'`。

### P1-8：前端 `useRecentActivity.ts` 多项目作用域串 scope

- **位置**：`apps/desktop/src/composables/useRecentActivity.ts:79-88`
- **根因**：通过 `scopedApi(api, () => storage)` 强制使用 `selectedStorage.value` 查询所有候选会话的 `listRuns`，导致非当前项目作用域的会话发送错误的 `x-tinadec-storage-id`，在多项目活动视图下请求失败。

### P1-9（实测追加定位）：内容库重复磁盘落盘、已释放 Logger 重开句柄、`GET /api/v1/sessions` 单库锁死拖垮全局

- **位置**：
  - `TinadecCore/Persistence/LocalFileContentStore.cs:14-60` & `ConfigurationProjectionCoordinator.cs` (`MaterializeContentAsync`)
  - `TinadecCore/Runtime/ScopeDiagnosticLoggerProvider.cs` & `TinadecCore/Runtime/StorageScopeInitializer.cs:169`
  - `TinadecCore/AspNetCore/Endpoints/StorageEndpoints.cs:134-160`
- **根因**：
  1. `ApplyProjectionAsync` → `MaterializeContentAsync` 与 `LocalFileContentStore.PutAsync` 在目标内容寻址文件（`content/.../<sha256>`）已存在于磁盘时，仍每次分配流并以 `FileOptions.WriteThrough + Flush(flushToDisk: true)` 写入临时文件，最后发现目标已存在再丢弃。
  2. `ScopeDiagnosticLoggerProvider.Dispose()` 后若有后台线程迟到写入，`_stream is null` 会重新打开 `core.log` 且不再被释放，导致 Windows 下关闭/移动作用域后 `StorageScopeInitializer.CopyOwnedTreeAsync`（且使用严格 `FileShare.Read` 的 `File.OpenRead`）抛 `IOException` 共享冲突。
  3. `GET /api/v1/sessions` 聚合遍历所有作用域时未像 `ProjectBoardEndpoints` 那样隔离非 `"user"` 项目作用域的 `StorageScopeRowError`（如目录不可用或 `host.lock` 被外部进程占用），导致单个项目作用域不可用即引发全局 `500 internal_error`。

---

## 三、修复方案与落地改动

1. **日志分级过滤与句柄/预算优化**
   - `TinadecCore/Runtime/StorageScopeRegistry.cs`：在项目作用域 `LoggerFactory` 中默认过滤 `Microsoft.AspNetCore >= Warning`、`Microsoft.EntityFrameworkCore >= Warning`，并绑定主机 `Logging:LogLevel` 配置节。
   - `TinadecCore/Runtime/ScopeDiagnosticLoggerProvider.cs`：复用长驻 `_stream`、内存跟踪 `_currentBytes` 与 `_knownTotalBytes`，仅在首写、轮转或已知总量超出配额时扫描目录；增加 `_disposed` 守卫防止 `Dispose()` 后迟到日志重开文件句柄。
   - `TinadecCore/Runtime/StorageScopeInitializer.cs`：`CopyOwnedTreeAsync` 改用 `FileShare.ReadWrite | FileShare.Delete` 打开只读源流。
   - `apps/desktop/electron/logSink.cjs`：按写入字节阈值 `Math.min(maxFileBytes, 256 * 1024)` 与文件轮转事件节流 `enforceBudget()`。

2. **配置投影、内容寻址与历史版本校验优化**
   - `TinadecCore/Persistence/Configuration/ConfigurationProjectionDocumentValidator.cs`：将历史表 `previous.FirstOrDefault(...)` O(N²) 查找重构为每实体一次预构建 `Dictionary<string, object>` 的 O(1) 查找。
   - `TinadecCore/Persistence/Configuration/ScopeConfigurationDocuments.cs`：新增 `IsProjectionUnchangedAsync` 与基于 `SHA256.HashDataAsync(FileStream)` 的零解码字节校验路径。
   - `TinadecCore/Persistence/Configuration/ConfigurationProjectionCoordinator.cs`：引入 `_schemaSettled` 与无锁读取的 `ConcurrentDictionary _projectedDocuments` 乐观快路径（Schema 已结算且文档 SHA-256 未变时直接挂接并跳过 `_gate` 与重复 SQLite `EnsureColumnsAsync`/`TablesExistAsync`/TOML 解析）；`CompileAsync` / `VerifyAsync` 复用已投影文档避免重复全库验证；`MaterializeContentAsync` 在哈希已匹配且目标 blob 已存在时跳过 `PutAsync`。
   - `TinadecCore/Persistence/LocalFileContentStore.cs`：对可 Seek 流先计算 SHA-256 并检查目标内容寻址路径是否已存在，命中时跳过临时文件写入与 `WriteThrough`。

3. **接口扇出、并发与 SSE/连接自愈优化**
   - `TinadecCore/AspNetCore/Endpoints/UiPreferencesEndpoints.cs`：引入 `enumeratorCts`，在 `finally` 中先取消并安全等待未决的 `pendingMoveNext`，再调用 `enumerator.DisposeAsync()`，根除 SSE 断开 `NotSupportedException`。
   - `TinadecCore/Runtime/ReadinessService.cs`：`GetReceiptAsync` 对同租户/工作区并发请求进行单飞合流（coalesce），5 项子检查 `Task.WhenAll` 并行执行，`ProbeExecutablePath` 同时探测 `net11.0` 与 `net10.0`。
   - `TinadecCore/AspNetCore/ControlPlaneService.cs`：`ListProviders` 批量查询 `ProviderVersions`，全部改为异步 `await _secrets.ExistsAsync(..., ct)` 消除 `.GetAwaiter().GetResult()` 同步阻塞。
   - `TinadecCore/AspNetCore/Endpoints/AgentConfigurationEndpoints.cs`：`ListAgents` 按需投影 `ModeVersions`/`AgentVersions` 轻量 ID/Version 字段（跳过数百条大 JSON 反序列化），按可见 agent ID 轻量检索最新 `ModelInvocation` ID 后再回表，并预分组 `ModeNodes`。
   - `TinadecCore/Runtime/ProjectBoard/ProjectBoardCoordinator.cs` & `TinadecCore/AspNetCore/Endpoints/ProjectBoardEndpoints.cs`：新增批量 `ListTasksForProjectsAsync` 取代 N+1 按项目查询，并使用有界并发（并行度 4）跨作用域采集后按优先级确定性归并；`apps/desktop/src/components/AppSidebar.vue` 跳过连接初帧空 `summary` 的重复 HTTP 拉取。
   - `TinadecCore/AspNetCore/Endpoints/StorageEndpoints.cs`：`GET /api/v1/sessions` 采用有界并发（并行度 4）跨作用域读取，并在全局聚合时隔离非 `"user"` 项目作用域的 `StorageScopeRowError`。
   - `TinadecGateway/src/coreClient.ts`、`streaming.ts`、`index.ts`：统一 `isCancellationError`/`rethrowCancellation`；`wrapSseWithKeepalive` 安全吞掉 `reader.cancel()` 异步拒绝对话并以干净 `close()` 结束中止读取；`.onError` 对已中止请求直接返回 499 避免误报 500。
   - `apps/desktop/electron/main.cjs` & `src/composables/useConnection.ts`：主进程 `retryDelaysMs` 扩展至 `[1s, 2s, 5s, 10s, 15s, 20s, 30s]`（dev `verificationTimeoutMs` 调至 20s）；渲染层 `startHealthWatch` 定期巡检到已处于 `unavailable` 时自动触发 `retryHostStatus` 实现自愈。
   - `apps/desktop/src/composables/useRecentActivity.ts` & `apps/desktop/vite.config.ts`：`useRecentActivity` 按 `row.session.storage_id || storage` 绑定作用域；Vite `server.watch.ignored` 增加 `**/.runtime-cache/**`。

---

## 四、修复后实测与测试验证记录

### 4.1 独立联调实测（隔离端口 Core `48931` + Gateway `48930`，使用真实 209 项目 / 199 会话 / 647 Agent 版本库在线一致备份）

| 指标 / 端点 | 修复前实测 | 修复后实测 | 变化 |
|---|---:|---:|---|
| `GET /api/v1/sessions`（199 会话，且含外部锁冲突作用域隔离） | 1,902 ms / 4,483 ms | 冷 **195.1 ms** / 热 **58.4–81.3 ms** (`200`, 125.9 KB) | **约 23x–55x 提速**，且不再因单项目 `host.lock` 冲突 500 |
| `GET /api/v1/board/summary`（209 项目全库汇总） | ~4,278 ms（5s trace 累计 CPU） | 冷 **114.7 ms** / 热 **62.7–65.0 ms** (`200`, 44.1 KB) | **约 65x 提速** |
| `GET /api/v1/model-center/snapshot` | 2,271 ms / 5,120 ms（`providers=1715ms, readiness=5098ms`） | 冷 3,378.1 ms / 热 **304.8–508.5 ms**（`providers=106.1ms`） | **热态约 10x 提速** |
| `GET /api/v1/agents` | 1,747 ms / 2,365 ms | 冷 1,611.7 ms / 热 **811.2–820.7 ms** (`200`, 178.5 KB) | **热态约 2.1x–2.9x 提速** |
| `GET /api/v1/readiness`（3 并发突发） | — | **435.2 ms** 完成 3 并发（均 `200`）；单次热态 **247.5–321.9 ms** | 单飞合流 + 子项并行生效 |
| 8 秒空载 CPU / 日志增量 | 20%–36% CPU；每作用域 200 MiB `Executed DbCommand` | 8s 累计 CPU 2.453s（整机 ~3.83%）；user 与 project `core.log` 增量均为 **0 字节** | 彻底消除 EF SQL 刷屏与持续空转 |
| SSE 断开（`/preferences/events`、`/board/events`） | `core.log` 频繁 `NotSupportedException`；`gateway.log` 未处理 `AbortError` | `NOT_SUPPORTED_EXC=0`、`EF_DB_CMD_COUNT=0`、`GW_STDERR=EMPTY`、`CORE_STDERR=EMPTY` | 异常清零 |

> **诚实说明（仍待继续优化项）**：在独立测试端口上使用 `dotnet exec` 拉起携带 209 项目与 647 Agent 版本的完整测试快照时，`CORE_STARTUP_MS` 仍为 `105,618 ms`（其中 `StorageMigrationRunner` 后 `CompanionParticipantReconciler` 至 `Now listening` 耗时 `63.3s`），说明冷启动重投影/托管初始化仍有进一步剖析空间；现阶段桌面端通过扩展重试窗口（83s + 20s 超时）与 `useConnection` 周期自愈兜底确保不会卡死在不可用横幅。

### 4.2 自动化测试结果

- `bun run --cwd TinadecGateway test`：**134 pass, 0 fail**（20 files，含新增 `streaming.ts` 取消与 `AbortError` 防护测试）。
- `bun run --cwd apps/TinadecUI test`：**221 pass, 0 fail**（21 files）。
- `bun run --cwd apps/desktop typecheck` & `bun run --cwd apps/desktop build`：**通过（exit 0）**。
- `bun run --cwd apps/desktop test`：Vitest **116 files / 966 pass, 0 fail** + Node electron/scripts **287 pass, 0 fail**（含新增 `useConnection` 自动重连与 `useRecentActivity` 跨作用域回归测试）。
- `TinadecCore.Context.Tests`：**5 pass, 0 fail**；`TinadecCore.Architecture.Tests`：**12 pass, 0 fail**；`TinadecCore.Governance.Tests`：**94 pass, 0 fail**；`TinadecCore.AgentFramework.Tests`：末次并发跑 **717 pass, 2 fail**（`HeadlessCliChatClientTests.ProcessRunner_CancellationKillsTheChild` 临时目录 25s 占用超时 与 `ProjectBoardEngineTests.Autopilot_EnforcesMaxConcurrency` 第 1222 行并发竞态），两项单独隔离复测均 **2 pass, 0 fail**（此前全量并发曾 719 pass, 0 fail）。
- `TinadecCore.Api.Tests`（定向验证，因运行中旧 `TinadecCore.Api` PID 9380 锁定 `TinadecCore/Api/bin`，将更新的 `Persistence`/`Runtime`/`AspNetCore` 程序集同步至测试目录执行）：
  - `ContentTemporaryCleanupTests`（2 项）+ `ConfigurationProjectionMigrationOrderTests`（2 项）+ `ControlPlaneConfigGuardTests`（19 项）：**23 pass, 0 fail**（与 `StorageScopeApiTests` 2 项合并跑 25/25 通过）。
  - `ProjectBoardEndpointsTests`：**5 pass, 0 fail**；`StorageScopeApiTests`：**23 pass, 0 fail**（`Move_RebindsHostIdentity` 修复后单独复测通过；`ScopeListing` 与 `ProjectRequests_AreIsolated` 在最后一次编辑后再次复测通过）。
  - `UiPreferencesTests`（5 项）+ `ScopeLoggingTests`（2 项，含新增 `DisposedProviderDoesNotReopenLogFileOnLateWrite`）+ `ApiEndpointTests`（7 项）+ `AuthHostGateTests`（22 项）：**36 pass, 0 fail**。
