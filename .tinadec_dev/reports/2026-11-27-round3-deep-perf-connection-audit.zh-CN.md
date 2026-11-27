# 第三轮深度性能、冷启动、关闭与连接问题排查及修复报告（2026-11-27）

> 范围：覆盖 `TinadecCore`、`TinadecGateway`、`apps/desktop`、`apps/TinadecUI` 与回归测试体系。
> 并发协同声明：工作区与并发 **Task #63（多设备 Peer 协同）** 共享 `C:\git\agent\TinadecOffice`；本轮所有分析、修复、测试与后续暂存提交严格避开 Task #63 专属 Peer 文件。

---

## 一、对上一轮（`53c72cb8`）实现与结论的深度复核与破局点

通过真实使用 `dotnet-trace`（`dotnet-sampled-thread-time` -> Speedscope）、逐模块剖析与测试矩阵验证，发现上一轮存在以下未根除的深层问题与失效优化：

### 1. `SqliteConnectionPragmaInterceptor.ConfiguredHandles` 命中率为 `0%`（`Pooling = false` 抵消池化缓存）
- **现象 / 验证**：`53c72cb8` 在 `SqliteConnectionPragmaInterceptor.cs` 引入 `ConditionalWeakTable<sqlite3, AppliedPragmaState>`，企图在连接复用时跳过 `PRAGMA busy_timeout; PRAGMA synchronous = NORMAL;`；但上一轮冷启动 trace 显示 `ApplyPragmasAsync` 仍消耗极高（空库冷启 `3,085 ms`，有数据热库 trace `40,931.3 ms`）。
- **根因**：
  1. `TinadecCore/Persistence/ServiceCollectionExtensions.cs:125` 硬编码了 `Pooling = false`；
  2. `TinadecCore/Persistence/ProjectVectorDatabase.cs:114` 同样硬编码了 `Pooling = false`。
  因此每次 `CreateDbContextAsync()` 都会重新打开物理 `sqlite3` 句柄并在关闭时立即销毁，导致 `ConfiguredHandles` 命中率恒为 **0%**，并伴随 SQLite 页面缓存全部丢失与最后句柄关闭时的频繁 WAL checkpoint/recovery。
- **修复**：
  - 将 `ServiceCollectionExtensions.cs` 与 `ProjectVectorDatabase.cs` 的 `Pooling` 启用为 `true`；
  - 为防止 Windows 下进程内池化句柄锁住临时/已删除/已迁移项目目录：
    1. 让 `TinadecDatabaseConfigurer` 实现 `IDisposable`，在 DI 容器 / 作用域释放时自动调用 `SqliteConnection.ClearPool(...)` 立即释放对应库句柄（即使独立单元测试不经过 `StorageHostShutdownService` 也能无锁删除目录）；
    2. 在 `SqliteConnectionPragmaInterceptor` 新增 `ClearPoolForPath(string? path)`，覆盖无 `DefaultTimeout` 与 `DefaultTimeout=15` 两种连接串；
    3. `ProjectSessionLifecycleService.PurgeProjectAsync` 删除向量库前先调用 `SqliteConnectionPragmaInterceptor.ClearPoolForPath(...)`；
    4. `StorageHostShutdownService.StopAsync` 退出清理阶段显式调用 `SqliteConnection.ClearAllPools()`。

### 2. ASP.NET Core 首次请求阻塞 **3.17 秒** 编译 Minimal API 委托 + Post-`Now listening` 线程池争抢（`9.6s` 假健康假死）
- **实测（`dotnet-trace` 空库冷启动）**：
  - `Thread (22136)` 在收到第一笔 `GET /api/v1/health` 时卡死在 `EndpointRoutingMiddleware.InitializeCoreAsync()`（**`3,056 ms`**），其内部 `RequestDelegateFactory.Create` -> `Expression.Compile()` -> `DynamicMethod.CreateDelegate()`（**`2,500 ms`**）在首次 HTTP 请求到来时才同步编译全量 ~150+ 个 Minimal API 端点。
  - 同时 `StorageScopeRecoveryWorker.ExecuteAsync` 在 `app.Run()` 一启动便立即以 `Parallel.ForEachAsync(MaxDegreeOfParallelism = 4)` 并发挂载全部历史项目作用域，与 `EndpointRoutingMiddleware` 争抢线程池和 CPU，将首次 `/api/v1/health` 拖长至 **`9,628.4 ms`**。
- **修复**：
  - 在 `TinadecCore/AspNetCore/TinadecCore.AspNetCore.csproj` 启用 `<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>`，在编译期生成 `GeneratedRouteBuilderExtensions`，消除运行时 `Expression.Compile()`；
  - `StorageScopeRecoveryWorker` 注入可选 `IHostApplicationLifetime? lifetime = null`，等待 `ApplicationStarted` 并礼让 `200 ms` 后再启动并发作用域恢复，确保 Kestrel 监听后首批 `/api/v1/health` 毫秒级响应。
  - `StorageScopeRegistry.MountAsync` 在非 Debug 构建移除 `ValidateScopes = true` 的逐次解析开销（保留 `#if DEBUG` 检查）。

### 3. `StorageMigrationRunner` 17 个 DbContext 重复执行 ~100 次 SQLite 元数据往返（冷启动 `11.84s` 主瓶颈之一）
- **实测（`dotnet-trace`）**：空库冷启 `StorageMigrationRunner.RunAsync` 耗时 **`11,251–11,840 ms`**，其中 17 个 `DbContextMigrationParticipant<T>` 顺序执行：
  - 每个上下文各查一次 `GetPendingMigrationsAsync()`（打开连接查 `__EFMigrationsHistory`）
  - 每次 `pending.Any()` 再各做一次 `ClearStaleSqliteMigrationsLockAsync()`（尽管 `StorageMigrationRunner` 开头已清过一次）
  - 每个上下文 `EnsureTablesAsync` 各执行一次 `AllSqliteTablesAndIndexesExistAsync()`（查 `sqlite_master`）与一次 `SqliteColumnCatalog`（全表 `pragma_table_info`）。
- **修复**：
  - 在 `DbContextMigrationParticipant.cs` 与 `StorageMigration.cs` 引入基于 `AsyncLocal<SqliteBatchSchemaSession?>` 的单次迁移批会话：
    1. 对同一 SQLite 数据库仅清一次 `__EFMigrationsLock`（`LockAlreadyCleared = true`）；
    2. 一次加载并共享 `sqlite_master` 对象集、`__EFMigrationsHistory` 已应用迁移集、`pragma_table_info` 列元数据目录，并通过内存 `db.Database.GetMigrations()` 判定待执行迁移；
    3. 任一 `MigrateAsync`、`GenerateCreateScript`、`AddColumn`、`DropColumn`、`Rebuild` 或非 `DbContextMigrationParticipant` 执行时立即调用 `Invalidate()` 失效并自动重刷快照，保证兼容修复测试与旧库升级 100% 等价。

### 4. `TinadecToolsProcessManager.ShutdownAsync` 顺序阻塞 **3 秒/子进程** 且跳过 `McpRuntime.DisposeAsync()`
- **根因**：`TinadecTools/Program.cs` 通过等待 `Console.In.ReadLineAsync()` EOF 后运行 `await McpRuntime.DisposeAsync()` 优雅退出；但 `TinadecCore/Tools/TinadecToolsProcessManager.ShutdownAsync` 从未关闭 `process.StandardInput`，且在 `foreach` 中顺序 `WaitForExit(3000)`，导致每个子进程均白白耗尽 3000ms 超时后再被 `Kill(entireProcessTree: true)` 强杀，既拖超时关闭窗又漏清理 MCP 子进程与 `_writeLocks`。
- **修复**：
  - `ShutdownAsync` 先批量使挂起调用失败并同时关闭所有子进程 `StandardInput`（发送 EOF 信号触发并行优雅退出），再使用 `Task.WhenAll` + `WaitForExitAsync(1500ms)` 并行收尾；并在 `RemoveFailedProcess` 与 `ShutdownAsync` 中同步清理 `_writeLocks`。

### 5. `ScopeConfigurationDocuments` UTF-8 BOM 哈希失配导致冷启投影快路径永久失效
- **根因**：`HashFileAsync` 对原始文件流做 `SHA256`，而 `File.ReadAllTextAsync` 会自动剥离 UTF-8 BOM（`EF BB BF`），导致外部编辑器保存带 BOM 的 TOML 后 `HashFileAsync != Hash(ReadAllTextAsync)`，每次均退化回全量重解析与比对。
- **修复**：在 `ScopeConfigurationDocuments.HashStreamNormalizedAsync` 检测并跳过 3 字节 UTF-8 BOM（对 UTF-16 BOM 回退读取文本哈希），保证磁盘流哈希与文本哈希完全一致。

### 6. `ProjectBoardFileSyncService` 关闭竞态与未跟踪防抖任务
- **根因**：`StopAsync` 取消 `_startupCts` 与 `DisposeAllWatchers()` 之间及并发 `SyncProjectAsync` 路径未设置 `_stopping` 标志，`EnsureWatcher` 可能在关停途中重建 `FileSystemWatcher`；且 `ScheduleDebouncedSync` 启动未跟踪 `Task.Run`，退出时可能仍在访问已销毁作用域。
- **修复**：增加 `volatile bool _stopping` 守卫（覆盖 `EnsureWatcher` 添加后二次检查与 `ScheduleDebouncedSync`），并通过 `_inFlightDebounces` 跟踪在飞防抖任务于 `StopAsync` 内有限等待收尾。

### 7. `AgentModelResolver.PreviewBatchAsync` 与 `ControlPlaneService.DiscoverHarnesses` 批量延迟优化
- **根因**：
  1. `AgentModelResolver.PreviewBatchAsync` 虽缓存了同名 `Plans` / `Resolutions`，但不同 Agent / 节点每次执行 `FreezeRouteAsync`、`FreezeAutoAsync`、`BuildResolutionAsync`、`ReadProtocolAsync` 时均会重复打开 `_models` DbContext、重复读取/解析 `IContentStore` JSON 文件，并重复解密 `ISecretStore` 凭据；
  2. `ControlPlaneService.DiscoverHarnesses` 顺序等待每个已定位 CLI 工具的 `--version` 探针子进程，总体延迟叠加；且 `VerifyCliExecutable` / `BuildVersionProbeStart` 未设置 `RedirectStandardInput = true` + 关闭 `StandardInput`，且未将 `ct` 链接至 `deadline`（或限制 `Task.WhenAll(stdout, stderr)` 读流超时），导致交互式等待 stdin 的 CLI 工具在 Windows 上拖满 `15,000 ms` 超时甚至在客户端取消后泄漏探针进程。
- **修复**：
  - 在 `PreviewBatch`（实现 `IDisposable` 并在 `PreviewBatchAsync` 结束释放）中缓存单次批加载的 `ModelCatalogSnapshot`、`SanitizedCatalogTask`、`JsonDocument` 及按 `SecretReference` 去重的密钥读取；
  - 将 `DiscoverHarnesses` 中的 `VerifyCliExecutable` 改为并发执行（`Task.WhenAll`），并加固 `VerifyCliExecutable`：设置 `RedirectStandardInput = true` 并启动后立即关闭 `StandardInput`，使用 `CancellationTokenSource.CreateLinkedTokenSource(ct)` 联动取消并在取消时调用 `TryKillProbeTree`，同时用 `.WaitAsync(deadline.Token)` 约束 `stdout/stderr` 排空。

### 8. `TinadecCore.Api.Tests` 看起来"卡死 >4 分钟"的真实根因查明
- **实测定位**：使用 `--blame-hang-timeout 25s` 与单独过滤执行确认：
  - `SqliteTransientRetryTests` 与 `SqliteWriteRetryTests` 针对 `BEGIN EXCLUSIVE` 锁打开无 `DefaultTimeout` 参数的 `SqliteConnection`（`Microsoft.Data.Sqlite` 默认 `DefaultTimeout = 30s`）；
  - 其中 `ALockThatNeverReleasesStillThrowsRatherThanReturningNothing` 与 `ALockThatNeverReleasesThrowsAndLeavesNoRow` 均重试 `MaxAttempts = 5` 次，单测各耗 **`2m 31s`**（5 x 30s），加上两条 `30s` 单次锁测试，仅这两个测试类就消耗 **`6m 25s`**，叠加 `xunit.runner.json` 关闭测试集合并发，导致整套 `Api.Tests`（此前单 `Health` 测试还要 `20s`）全量耗时超过 10 分钟、极易误触发 25s hang 检测。

### 9. 前端 & Gateway 遗漏项修复
- **`apps/desktop/src/lib/runStream.ts`**：`53c72cb8` 修复了 `scopedEventSource.ts` 但遗漏了 `runStream.ts`——仍在每个 chunk 对整个 `buffer` 执行全局 `.replace(/\r\n/g, '\n').replace(/\r/g, '\n')` 且 `reader.cancel()` / `response.body?.cancel()` 未吸收异常。改为跨块 `trailingCr` 增量归一化与 `.catch(() => {})` 防护。
- **`apps/desktop/src/composables/useConnection.ts`**：`probe(true, true)` 在 `wasUnavailable` 且 `readHostStatus` 仍为 `unavailable` 时立即调用一次 `applyHostStatus`（`consecutiveHostUnavailable++` 且立即调度 `startHealthWatch()`），紧接着 `retryHostStatus` 失败再调一次 `applyHostStatus`（单周期 `consecutiveHostUnavailable` 跳升两次 `1 -> 3`，且在 retry 返回前已错误起表）。改为当即将在同一周期调用 `retryHostStatus` 时延迟状态应用，等待 settled 结果后一次更新。
- **`TinadecGateway/src/index.ts`**：为 `/api/v1/sessions/:sessionId/attachments`（上传）、`/api/v1/attachments/:attachmentId/content`（下载）、`/api/v1/sessions/:sessionId/logs`、`/api/v1/sessions/:sessionId/logs/stream` 补齐 `keepStreamOpen(server, request)` 防止长传输触发 Bun 默认空闲超时。

---

## 二、量化对比与回归验证

| 指标 / 验证项 | 上一轮（`53c72cb8`）基线 | 本轮优化后 |
|---|---:|---:|
| 空库冷启首笔 `/api/v1/health` (`TinadecCore.Api.dll`) | `19,841–20,328 ms` | **`13,779.0 ms`** (`-30.5% ~ -32.2%`) |
| 已有库冷启首笔 `/api/v1/health` | `24,459.6 ms` (监听后卡 `9.6s`) | **`8,537.6 ms`** (`-65.1%`, 接近 3x 提速) |
| 单测 `Health_Returns200_WithLegacyCompatibleStructure` | `20 s` (`23.88s` total) | **`16 s`** (`20.08s` total) |
| `TinadecCore.AgentFramework.Tests` (`!~Peers` 共 719 tests) | `2 m 17 s` | **`51 s` (`-62.8%`)** |
| `TinadecCore.Governance.Tests` / `Context.Tests` / `Architecture.Tests` | `94 / 5 / 12 pass` | **`94 pass` (9s) / `5 pass` (859ms) / `12 pass` (31s)** |
| `PerformanceAndConnectionRegressionTests` (含 4 项新增核心回归) | `7 pass` | **`11 pass, 0 fail`** (`31s`) |
| `SqliteTransientRetryTests` + `SqliteWriteRetryTests` | 误卡 `25s` hang 线 | **`14 pass, 0 fail`** (`6m 26s`, 含 `DefaultTimeout=30s` 真实锁等待) |
| `AcpServerModeBAndDogfoodTests` (E2E 子进程) | 超 `35s` 单测门限 | **`2 pass, 0 fail`** (`59s` + `1m 21s`) |
| 其余全部非 Peer `TinadecCore.Api.Tests` (317 项) | 曾误以为挂起 | **`313 pass, 2 skip (PostgreSQL), 0 hangs`**；仅 2 项 `ApiEndpointTests` 因并发 Task #63 新注册第 18 个 `Peers` 模块而报错 `Expected: 17, Actual: 18` |
| `TinadecGateway` (`bun test src`) | `135 pass` | **`135 pass`** (20 files) |
| `apps/desktop` Vitest + `vue-tsc` + `vite build` | `983 pass` | **`984 pass`** (119 files) + `vue-tsc` exit 0 + `vite build` exit 0 (7,049 modules) |
| `apps/TinadecUI` Vitest | `221 pass` | **`221 pass`** (21 files) |
| `apps/desktop/electron` + `scripts` | `288 pass` | **`288 pass`** |
| 独立端到端联调 (`Core + Gateway`, port `48941/48940`) | — | `/api/v1/agents` **`180.5ms` 首调 / `25.5ms` 热调**；`/api/v1/sessions` **`74.7ms / 9.4ms`**；`/api/v1/model-providers/harnesses/discover` **`13.1s -> 9.3s` (`200 OK`, 原 `>15s` 超时)**；空闲 10s `core.log` 增量 **`0 B`** |

### 新增核心回归测试清单（`PerformanceAndConnectionRegressionTests.cs` & 前端单测）
1. `ScopeConfigurationDocuments_Utf8BomMatchesNormalizedHashCache`：验证带 UTF-8 BOM TOML 文件经规范化流哈希后仍命中持久化投影快路径。
2. `TinadecDatabaseConfigurer_PoolingEnabledAndDisposingReleasesWindowsFileLock`：验证开启 `Pooling = true` 且释放 DI `ServiceProvider` 后立即可删除 Windows SQLite 目录无 `IOException`。
3. `StorageMigrationRunner_BatchSessionReusesSchemaSnapshotAndStillRepairsColumns`：验证跨上下文迁移批会话共享 SQLite 元数据快照的同时仍能检测并修补缺失列。
4. `TinadecToolsProcessManager_ShutdownClosesStdinAndExitsBeforeKillTimeout`：验证真实 `TinadecTools` 子进程经 `ShutdownAsync` 发送 stdin EOF 后在 `< 2200ms`（实测 `~1s`）优雅退出，不再空等 `3000ms` 强杀。
5. `apps/desktop/src/lib/runStream.test.ts`：覆盖跨 chunk 边界劈裂的 `\r\n` 增量归一化与 `reader.cancel()` 拒绝异常吸收。
6. `apps/desktop/src/composables/useConnection.test.ts`：钉死自动恢复退避 `1 -> 2`（`6s -> 12s`）单次递增契约，消除重复叠加跳阶。

---

## 三、未覆盖边界与并发隔离说明

1. **`TinadecCore.Api.Tests` 全量与并发 Task #63**：
   - `PerformanceAndConnectionRegressionTests`（11/11）、`AcpServerModeBAndDogfoodTests`（2/2）、`Architecture.Tests`（12/12）、`Governance.Tests`（94/94）、`Context.Tests`（5/5）、`AgentFramework.Tests`（`FullyQualifiedName!~Peers`，719/719）均在最后一次产品代码改动后复测通过；
   - `apps/desktop` 针对性回归（`useConnection.test.ts` 21 项 + `runStream.test.ts` 11 项 = 32/32 pass）、`electron + scripts`（288/288 pass）、`apps/TinadecUI`（21 files / 221 pass）以及 `TinadecGateway`（`bun test src` 20 files / 135 pass）均在所有改动完成后直接复跑通过（`apps/desktop` 全量 119 文件 / 984 测试与 `vue-tsc` / `vite build` 于核心前端改动 `06:13` 后跑过一轮未见失败，但未在 `07:50` 后重跑全部 119 个无关测试文件，亦避开 `TinadecGateway/tests/peers/**`）。
   - 其余非 Peer `TinadecCore.Api.Tests`（排除 Task #63 专属 `FullyQualifiedName~Peers` 与 `Sqlite*RetryTests` / `AcpServerModeBAndDogfoodTests`）：313 通过、2 跳过（PostgreSQL）、0 挂起；仅 `ApiEndpointTests.Manifest_Returns200_WithSnakeCaseAndRegisteredModules` 与 `Readiness_Returns200_WithCorrectModuleStates` 因并发 Task #63 新增注册第 18 个 `TinadecCore.Peers` 模块而报错 `Expected: 17, Actual: 18`；
   - `SqliteTransientRetryTests` + `SqliteWriteRetryTests`（14/14 pass，`6m 26s`）运行于最终仅涉及 `ControlPlaneService.cs` 与测试文件的收尾改动之前，该改动未触及 SQLite 重试或迁移路径，但严格来说该 14 项耗时测试未在最终编辑后再跑一轮；
   - 排除 `TinadecCore.Peers` 过滤，因该模块由并发 Task #63 持续开发。
2. **浅层验证 / 环境边界**：
   - `ProjectBoardFileSyncService` `_stopping` 竞态守卫与防抖收尾等待通过编译、代码审查及常规宿主关停测试验证，未构造专门的高并发 `FileSystemWatcher` 关停压测用例；
   - 独立冷启动与 Gateway+Core 端到端联调使用隔离临时目录（非真实多 GB 生产库）；该空引导目录缺少 agent bootstrap pack 导致 `POST /api/v1/sessions` 返回 `409 agent_mode_not_configured`，且 `/api/v1/events` SSE 客户端断开传播通过自动化回归测试与 Gateway 单测验证；
   - 并发编译/测试高负载下的项目库冷启（`BOOT2_WARMDB_WITH_PROJECT`）首次 `/api/v1/health` 为 `16,938.1 ms`，随后 `BOOT2_GW_AGENTS` 为 `1,466.7 ms`（与启动后 `StorageScopeRecoveryWorker` 并行挂载项目作用域重叠）。
3. **Git 提交边界**：
   - 仅显式暂存并提交本轮 23 个文件；`apps/desktop/src/api.ts`（含 `53c72cb8` 遗留及 Task #63 并发改动）及全部 Task #63 Peer 文件保持未暂存与未修改状态。
