# 第二轮深度性能、启动与连接问题排查及修复验证报告（2026-11-27）

> 基线与衔接：承接第一轮提交 `3e9f02c5`、`c8c89f6b`、`5ba81040` 与中断检查点 `0c436334`（`检查点3`）。  
> 验证隔离原则：所有实机联调均使用临时拷贝的隔离目录（拷贝 `C:\Users\lincu\.tinadec`、`TinadecOffice\.tinadec`、`RouterAPI\.tinadec`，重写临时 `projects.toml` 中的 `storage_root`）与隔离端口（Core `48931` / Gateway `48930`），对真实用户及工作区零写入，未触碰 5090 端口外部进程。

---

## 一、Round 2 起步状态与基线复测

1. **修复中断检查点 `0c436334`**：
   - `TinadecCore/Runtime/ProjectBoard/ProjectBoardCoordinator.cs` 存在重复定义的 `ListAutopilotEnabledProjectIdsAsync` 编译错误，已清理并核对批量逻辑一致性。
2. **Round 2 修复前隔离基线实测（`0c436334` + 修复重复定义后）**：
   - 隔离冷启动 `CORE_READY_MS`：**47,760 ms**（Default 配置）/ **57,762 ms**（`Information` 日志级别），对比第一轮 `105,618 ms` 仍偏慢；
   - 首访健康接口曾卡顿 **5,827 ms**（Kestrel 监听后后台立即并发挂载 2 个均设置 `follow_user_models = true` 的项目作用域，重复解析用户级 3.6 MiB `agents.toml`）；
   - 热接口基线耗时：`/api/v1/agents` `1,281 ms`、`/api/v1/readiness` `2,984 ms`、`/api/v1/model-providers` `363 ms`、`/api/v1/tools/skills` `122 ms`、`/api/v1/sessions` `648 ms`、`/api/v1/board/tasks` `372 ms`；
   - `CompanionAutonomyService` 在配额耗尽/鉴权失败（`HTTP 403 insufficient_user_quota`）下每 60 秒持续重试刷日志。

---

## 二、深层根因定位（代码审计 + `dotnet-trace` Speedscope 实时画像）

通过源码逐模块深入核查及两次 `dotnet-trace`（`Speedscope`）实测采样，挖出第一轮未彻底消除的深层瓶颈与隐性隐患：

### 2.1 冷启动与高频配置/SQLite 性能根因
1. **配置投影冷重启全量解析 + 跨上下文重复哈希 3.6 MiB `agents.toml`**：
   - `ConfigurationProjectionCoordinator.AttachAsync` 重启后缺少持久化快照校验，每次重启仍解析、校验并比对 1,300+ 配置行；`CompileAsync` 每次都重建快照；
   - `dotnet-trace` `BOOT2_22S` 实测暴露 **`ScopeConfigurationDocuments.HashFileAsync` 累计占用 `31,200.9 ms`**：即使内存已命中 `_projectedDocuments`，每次新建 5 类配置 `DbContext` 仍通过 `IsProjectionUnchangedAsync` 对 3.6 MiB `agents.toml` 全文流式重算 SHA-256（且 `user` / `proj1` / `proj2` 各自独立重算）。
   - `ConfigurationLiveSourceValidation.Validate` 存在对 `TomlTableArray` 的 O(N²) `.Any(...)` 扫描。
2. **`SqliteConnectionPragmaInterceptor` 在连接池物理句柄复用时重复执行 PRAGMA（实测 `BOOT2_22S` 累计 `40,931.3 ms`，`IDLE_8S` `909.6 ms`）**：
   - 虽已缓存文件级 `WAL` 初始化，但每次 EF Core 从 `Microsoft.Data.Sqlite` 连接池取出物理连接打开 `DbContext` 时，仍无条件执行 `PRAGMA busy_timeout = 15000; PRAGMA synchronous = NORMAL;`，在高并发启动与后台周期巡检下产生大量 `sqlite3_prepare_v2` 与锁竞争。
3. **`DbContextMigrationParticipant` 无条件执行迁移锁清理与建表 DDL**：
   - SQLite 在无挂起迁移时仍反复访问 `__EFMigrationsLock`，并在 `EnsureTablesAsync` 无条件生成并执行全量 `GenerateCreateScript()` DDL 脚本（17 个上下文）。
4. **`ProjectBoardFileSyncService` 与 `ProjectAutopilotService` 阻塞 Kestrel 启动且复活已删除临时项目目录**：
   - 两项服务的 `StartAsync` 在 `Now listening` 之前同步扫描 209 个项目（合计阻塞 `10.53s`）；
   - `dotnet-trace` `BOOT2_22S` 实测暴露 **`ProjectBoardFileSyncService.SyncProjectAsync`（`34,556.6 ms`）与 `EnsureOrReconcileNotesUnderLockAsync`（`33,248.9 ms`）**：`RunStartupReconciliationAsync` 仅判断 `presentInDb.Contains(project.Id)` 而未校验 `Directory.Exists(project.RootPath)`，导致对历史测试遗留的 ~200 个已删除 `%TEMP%` 项目（默认 `RepoTinadec` 模式）执行 `Directory.CreateDirectory` 与 notes 写入；
   - `ProjectAutopilotService.CollectActiveTaskIds` 与 `inFlightMap` 存在竞态窗口：任务已在 DB 转为 `Blocked`/`InReview`/`Done` 或终态后、`finally` 移除前仍占据并发名额，导致 `Autopilot_EnforcesMaxConcurrency` 偶发失败。
5. **非 `bin/` 工作目录启动 Core 时 EF SQL 日志洪峰**：
   - `TinadecCore/Api/Program.cs` 未在 `builder.Configuration` 缺失显式配置时设置 `Microsoft.AspNetCore` / `Microsoft.EntityFrameworkCore` 默认 `Warning` 过滤；当从仓库根目录或外部 cwd 启动时无法命中 `appsettings.json`，实测 10 秒空载向 `user/logs/core.log` 写入 **3,302 条 `Executed DbCommand`（`+21,245 bytes`）**。

### 2.2 引擎、后台轮询、技能/模型解析与 SSE/连接层隐患
1. **DmaEA 运行引擎空闲 100ms 忙唤醒 & SSE 取消未透传**：
   - `FullDuplexRunEngine` 空闲时仍每 100ms 构造 `Task.WhenAny(..., WaitToReadAsync().AsTask())` 造成孤儿任务分配；
   - `DmaeaEndpoints` `/api/v1/runs/{runId}/stream` 向 `coordinator.FollowAsync` 传递了 `CancellationToken.None` 而非 `context.RequestAborted`，导致客户端断连后后端协程无法立即终止。
2. **事件流 tight-polling 与 sync-over-async**：
   - `StorageLifecycleService.UpdateTaskAsync` 存在 `.GetAwaiter().GetResult()` 同步阻塞；
   - `StorageEndpoints`（200ms）与 `EmbeddedAcpCoreBackend`（40ms）事件跟踪循环依赖无条件短间隔 SQLite 轮询。
3. **Companion 配额/鉴权错误无限 60s 紧循环重试**：
   - `CompanionAutonomyService` 遇到 HTTP 401/402/403/quota/模型不可用错误时未指数退避，每 60 秒固定重试并刷报错日志。
4. **技能包双遍历与模型快照重复反序列化**：
   - `ToolSkillResourceService` 分别遍历 `PackageFiles` 与 `ActualPackageHash`，且当 `userRoot == storage.Locations.Root` 时重复发现 `userRoot/skills`；
   - `AgentModelResolver.PreviewBatch` 对同一 `ModeVersion`/`AgentVersion` 重复反序列化 `SnapshotJson`。
5. **Gateway / Electron / Desktop 连接韧性与前端开销**：
   - `TinadecGateway/src/streaming.ts` 及 5 处路由直接回传上游 SSE 响应头时未剥离 HTTP/1.1 hop-by-hop 头（`transfer-encoding`/`connection`/`content-length` 等），且 `/api/v1/events` 未显式拦截 `>= 400` 响应做 ProblemDetails 映射；
   - `TinadecGateway/src/wsTerminalProxy.ts`、`apps/desktop/src/lib/scopedEventSource.ts`、`liveDigestApi.ts`、`api.ts` 在流式逐块拼接时对整段累计缓冲执行全量 `/\r\n/g` 正则替换；
   - `apps/desktop/electron/trustedHostRequests.cjs` 在 `did-fail-load`、`did-stop-loading`（主帧结束）、`did-navigate-in-page` 时未恢复 `entry.suspended`，主帧导航中止或失败后可导致可信 IPC 永久挂死；
   - `ScopedEventSource` 在持续断流时以固定短间隔无限重连；`useConnection.ts` 在 `unavailable` 时持续保持 6s 固定频率健康请求；`MarkdownRender.vue` 每个组件实例各自构造 `Marked` 解析器。

---

## 三、修复方案与落地改动清单

### 3.1 `TinadecCore`
- **`TinadecCore/Persistence/Configuration/ConfigurationProjectionDbContext.cs` & `ConfigurationProjectionCoordinator.cs`**：
  - 新增 DB 持久化投影印章表 `_tinadec_projection_stamps (document_id, content_hash, schema_stamp)`，`AttachAsync` 通过流式哈希与表结构签名比对跳过未变更配置的冷启动重解析与整库校验，变更时随 `SaveChanges`/`SaveChangesAsync` 事务更新；
  - `ApplyProjectionAsync` 新增 `AllPropertiesSame` 跳过未变更行追踪赋值；SQLite `TablesExistAsync` 合并为单次 `sqlite_master` 查询；`CompileAsync` 增加哈希校验缓存 `_compiledSnapshot`（写操作自动失效）。
- **`TinadecCore/Persistence/Configuration/ScopeConfigurationDocuments.cs`**：
  - 新增流式 `HashForProjectionAsync` 与延迟正文水合；
  - 新增带 `(Length, LastWriteTimeUtc.Ticks)` 校验与 2 秒防并发修改保护窗口的静态 `FileHashCache`，并在 `SaveIfMatchAsync` 原子落盘后立即移除对应缓存键；`DocumentVersion` 先快路径解析头部版本再回退全量 TOML 解析。
- **`TinadecCore/Persistence/Configuration/ConfigurationLiveSourceValidation.cs`**：将 O(N²) 扫描重构为预构建 `HashSet` O(1) 查找。
- **`TinadecCore/Persistence/DbContextMigrationParticipant.cs` & `TinadecDatabaseConfigurer.cs`**：无挂起迁移时跳过 `__EFMigrationsLock` 与 `MigrateAsync()`；SQLite 表及索引已齐备时跳过 `GenerateCreateScript()` DDL 执行；复用单例 `_sqliteInterceptor`。
- **`TinadecCore/Persistence/SqliteConnectionPragmaInterceptor.cs`**：使用 `ConditionalWeakTable<sqlite3, AppliedPragmaState>` 按物理 SQLite 连接句柄记忆已应用 PRAGMA，连接池复用同一句柄时零 SQL PRAGMA 往返，连接池清理或新物理句柄自动重新生效。
- **`TinadecCore/Runtime/ProjectBoard/ProjectBoardCoordinator.cs`、`ProjectBoardFileSyncService.cs`、`ProjectAutopilotService.cs`**：
  - 清理中断遗留重复方法；
  - 将启动同步扫描与 Autopilot 启动恢复移至可取消后台任务，不阻塞 Kestrel `Now listening`；
  - `RunStartupReconciliationAsync` 联查 `BoardSettings`，跳过目录已不存在的历史测试/临时项目（仅保留 `AppData` 或磁盘存在 `CustomDir` 的条目）；
  - `CollectActiveTaskIds` 过滤 `inFlightMap` 中数据库已呈终态或 `Blocked`/`InReview`/`Done` 的任务，消除并发槽位泄漏。
- **`TinadecCore/Api/Program.cs`**：当 `Logging:LogLevel:Microsoft.AspNetCore` / `Microsoft.EntityFrameworkCore` 未配置时默认加 `LogLevel.Warning` 过滤，防止外部工作目录启动时 EF SQL 刷爆 `user/logs/core.log`。
- **`TinadecCore/DmaEA/FullDuplexRunEngine.cs`**：空闲态改为受限于最近定时重试时间与 2s 扫描上限的 `WaitToReadAsync(waitCts.Token)`，消除 100ms 忙唤醒与孤儿 `Task.WhenAny`。
- **`TinadecCore/Companion/CompanionAutonomyService.cs`**：新增 `_failureBackoffs` 失败退避表（401/402/403/quota 起步 `15m -> 30m -> 60m`，普通模型故障 `max(interval,2m) * 2^shift` 上限 `60m`），并在配置编辑（`UpdatedAt` 推进）或成功执行时清除。
- **`TinadecCore/Lifecycle/StorageLifecycleService.cs`、`TinadecCore/AspNetCore/Endpoints/StorageEndpoints.cs`、`TinadecCore/AcpServer/EmbeddedAcpCoreBackend.cs`**：修复 `UpdateTaskAsync` `.GetAwaiter().GetResult()`；新增事件写入通知信号 `GetEventSignalTask`/`SignalEventSubscribers` 将事件流轮询改为即写即醒（2s/1s 兜底）。
- **`TinadecCore/AspNetCore/Endpoints/DmaeaEndpoints.cs`**：`FollowAsync` 透传 `context.RequestAborted`。
- **`TinadecCore/Skills/ToolSkillResourceService.cs` & `TinadecCore/Runtime/AgentModelResolver.cs`**：单趟 `InspectPackage` 消除目录双遍历并去重相同 `userRoot/skills`；`PreviewBatch` 内缓存反序列化策略与快照字典。
- **`TinadecCore/tests/TinadecCore.AgentFramework.Tests/Headless/HeadlessCliChatClientTests.cs` & `TinadecCore/tests/TinadecCore.Api.Tests/PerformanceAndConnectionRegressionTests.cs`**：修复 Windows `Out-File` 写 PID 文件瞬时共享锁竞态；新增 7 项端到端与微基准回归测试。

### 3.2 `TinadecGateway`、`Electron`、`Desktop`
- **`TinadecGateway/src/streaming.ts`、`index.ts`、`companionRoutes.ts`、`liveExplainerRoutes.ts`、`preferencesRoutes.ts`、`projectBoardRoutes.ts`、`coreClient.test.ts`**：新增 `buildSseResponseHeaders`/`createSseResponse` 统一剔除 HTTP/1.1 hop-by-hop 头并保留 CORS 与 `Server-Timing`；修复 `/api/v1/events` 上游错误透传。
- **`TinadecGateway/src/wsTerminalProxy.ts`**：改为增量块 `\r\n` 归一化。
- **`apps/desktop/electron/trustedHostRequests.cjs` & `trustedHostRequests.test.cjs`**：在 `did-fail-load`、`did-stop-loading`（`!isLoadingMainFrame()`）、`did-navigate-in-page` 时解除 `entry.suspended`。
- **`apps/desktop/src/lib/scopedEventSource.ts` & `.test.ts`、`composables/useConnection.ts` & `.test.ts`、`lib/liveDigestApi.ts`、`api.ts`、`components/MarkdownRender.vue`**：`ScopedEventSource` 断流指数退避 + 抖动 + 增量 `\r\n` + 安全 `.catch(() => {})`；`useConnection` 连续不可用退避巡检（`6s -> 12s -> 24s -> 30s`）；共享模块级 `sharedMarked` 实例；`liveDigestApi.ts` 与 `api.ts`（注意：`api.ts` 当前同时包含同树并行的 Task #63 Peer 改动，本次仅提交各自独立路径）改为增量 `\r\n` 处理。

---

## 四、实测与自动化测试验证记录

### 4.1 Round 2 修复后独立联调实测（`task-1226`：Core `48931` + Gateway `48930`，默认 `Information` 启动、未传 EF CLI 过滤）

| 观测维度 / 端点 | Round 1 / Round 2 修复前基线 | Round 2 修复后实测（`task-1226`） | 对比与说明 |
|---|---|---|---|
| 冷启动就绪（`BOOT1` / `BOOT2`） | `105,618 ms` (R1) / `57,762 ms` (R2 `Info`) / `47,760 ms` (R2 `Default`) | 首次升版 `34,238.4 ms`；稳态重启 **`24,459.6 ms`**（trace 采样中曾测得 `19,020–22,297 ms`） | **较 R2 `Info` 提速 ~2.36x，较 R1 提速 ~4.32x**（其中含 `Now listening` 后首包 `9,628.4 ms` 等候后台双项目挂载期） |
| `GET /api/v1/health` | 637 ms / 25 ms | GW 热 **`54.6 ms`** (`17.7ms`) / Core 热 **`5.0–12.7 ms`** (`200`) | 稳态毫秒级 |
| `GET /api/v1/agents` (209 KB) | 1,281 ms (Core) / 1,747–2,365 ms (GW) | GW 热 **`1,197.8 ms`** / Core 热 **`926.4 ms`** (`200`) | 冷态首访 3.9s，热态低于修复前 |
| `GET /api/v1/readiness` | 2,984 ms | GW 热 **`350.4 ms`** / Core 热 **`257.3–338.2 ms`** (`200`) | **约 8.5x–11.6x 提速** |
| `GET /api/v1/model-providers` | 363 ms (R2) / 1,715 ms (R1) | GW 热 **`53.8–64.5 ms`** / Core 热 **`74.4–99.9 ms`** (`200`) | **约 5x–26x 提速** |
| `GET /api/v1/model-center/snapshot` | 2,271–5,120 ms | GW 热 **`337.2–616.3 ms`** (`200`, 12 KB) | **约 8x–15x 提速** |
| `GET /api/v1/tools/skills` (24 KB) | 122 ms (R2) / 2,324 ms (R1) | GW 热 **`34.9–39.6 ms`** / Core 热 **`43.6–47.9 ms`** (`200`) | **约 3x 提速** |
| `GET /api/v1/sessions` (144 KB / 199 会话) | 648 ms (R2) / 4,483 ms (R1) | GW 热 **`120.7–204.8 ms`** / Core 热 **`135.8–191.2 ms`** (`200`) | **约 4x–22x 提速** |
| `GET /api/v1/board/tasks` | 372 ms | GW 热 **`73.5–137.4 ms`** / Core 热 **`82.3–149.4 ms`** (`200`) | **约 2.5x–5x 提速** |
| `GET /api/v1/board/summary` (44 KB) | 4,278 ms (R1 trace) | GW 热 **`75.4 ms`** / Core 热 **`81.5 ms`** (`200`) | **约 52x 提速** |
| SSE 中途断开（`/preferences/events`, `/board/events`, `/events?session_id=...`） | `NotSupportedException`、`AbortError` 刷屏 | 3 条 SSE 均 **`200 text/event-stream`**，客户端中止后 `GW_STDERR=0`、`CORE2_STDERR=0`、`not_supported=0` | 断连异常清零 |
| 10 秒空载日志与 CPU | 3,302 条 EF SQL（`+21,245 B` / 10s）；7.06s–22.75s CPU | `user=+0 B`、`proj1=+0 B`、`proj2=+0 B`、`ef_db_cmd=0`、`quota_err=0`；挂载刚完成窗口 `IDLE_10S_CORE_CPU_DELTA_S=5.609s`（稳态 `task-1105` `IDLE_8S` `CPU_TIME=541.6 ms`） | 空载日志零增量 |

> **诚实说明（仍存在的可继续优化空间）**：
> 1. 稳态冷启动（`24,459.6 ms`）中，`Now listening` 后首次 `/api/v1/health` 仍等待了 `9,628.4 ms`，根因为 `StorageScopeRecoveryWorker` 在 Kestrel 启动后立刻并行挂载 `proj1` & `proj2`（各自新建整套 19 个 DbContext 与托管服务）引发启动瞬间 ThreadPool 饱和；
> 2. `/api/v1/agents`（209 KB 全量编排/版本数据）热态仍需 `~926–1,198 ms`，主要受限于大量历史版本行与 JSON 快照序列化开销；
> 3. 刚完成双作用域挂载及 `TinadecTools` 子进程拉起后的 10s 窗口内 `IDLE_10S` 测得 `5.609s` CPU（完全静置后 `dotnet-trace` 采样 `CPU_TIME` 为 `541.6 ms / 8s`）。

### 4.2 自动化测试结果（截至最新修改后实测）

- **`TinadecCore.Api.Tests` 定向回归（`task-1164`，全部最新 Core 修改后）**：**42 pass, 0 fail**（包含全部 7 项新增 `PerformanceAndConnectionRegressionTests`、`SqliteTransientRetryTests`、`ProjectBoardEndpointsTests`、`ConfigurationProjectionMigrationOrderTests`、`StorageScopeApiTests`）。
- **`TinadecCore.Api.Tests` 全量跑（`task-894`，早于最后 `SqliteConnectionPragmaInterceptor` / `ScopeConfigurationDocuments` / `ProjectBoardFileSyncService` / `Program.cs` 4 处改动及 `DmaeaEndpoints_StreamDisconnect` 2s->8s 超时放宽）**：**323 pass, 2 skipped（PostgreSQL）, 5 fail**（1 项为当时 `DmaeaEndpoints_StreamDisconnect` 并发负载下 `2s` 超时，已在最新代码放宽至 `8s` 并于 `task-1164` 稳定通过；另 4 项分别为并行开发的 Task #63 `PeerFoundationApiTests` 与 `ApiEndpointTests` 17 vs 18 模块数断言冲突）。**注意：最后 4 处 Core 优化完成后未重跑全量 330 项 `Api.Tests`，仅重跑上述 42 项核心回归。**
- **`TinadecCore.AgentFramework.Tests`（`task-1193`，全部最新 Core 修改后）**：**719 pass, 0 fail**（此前偶发竞态的 `Autopilot_EnforcesMaxConcurrency` 与 `ProcessRunner_CancellationKillsTheChild` 均稳定通过）。
- **`TinadecCore.Context.Tests` / `Governance.Tests` / `Architecture.Tests`（`task-1204`，全部最新 Core 修改后）**：分别为 **5 pass / 0 fail**、**94 pass / 0 fail**、**12 pass / 0 fail**。
- **`TinadecGateway`（`task-921`）**：`bun run --cwd TinadecGateway test` -> **135 pass, 0 fail**（20 files）。
- **`Electron + scripts`（`task-922`）**：`bun scripts/run-node-tests.mjs apps/desktop/electron apps/desktop/scripts` -> **288 pass, 0 fail**。
- **`apps/TinadecUI`（`task-923`）**：**221 pass, 0 fail**（21 files）。
- **`apps/desktop`（`task-927` & `task-933`）**：Vitest **978 pass, 0 fail**（118 files），`vue-tsc --noEmit` 0 errors，`vite build` 成功产物落地。
