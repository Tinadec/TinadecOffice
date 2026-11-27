# 第四轮深度性能、执行引擎与全栈连接问题排查及修复报告（2026-11-27）

> 范围：深入 `TinadecCore/DmaEA` 执行引擎、`TinadecCore/Lifecycle` 流式持久化、`TinadecCore/Models` & `Abstractions` HTTP 连接池、`TinadecCore/Persistence` 配置投影、`TinadecGateway` 传输/终端代理，以及 `apps/desktop` / `apps/TinadecUI` 前端 SSE 并发与轮询治理。
> 并发协同声明：本轮代码、验证与提交流程严格与同工作区并行的 **Task #63（多设备 Peer 协同）** 隔离；共享文件 `TinadecCore/Models/Harness/AgentChatClientFactory.cs` 仅合入并分块暂存本轮 HTTP 连接池区块，完整保留 Task #63 的 `PeerProxy` 分支及其他 28 个 Peer 专属文件。

---

## 一、深度排查发现的潜在性能与连接问题

### 1. DmaEA 执行引擎流式投影自旋、多路唤醒丢失与内存泄漏
- **根因定位**：
  1. `TinadecCore/DmaEA/RunStreamProjection.cs`：原实现用无上限 `SemaphoreSlim Published = new(0)` 随每个 token chunk 递增 `Release()`，但消费者 `Since()` 按批消费却不消耗信号量计数。高速输出时信号量累积数百至数千计数，导致消费端追平游标进入 `WaitForFramesAsync()` 后在 `while (true)` 内同步空转唤醒同样次数并反复加锁；同时 `Seal(runId)` 仅调用一次 `Release()`，多路并发订阅者（主窗、轨迹、LiveDigest）仅能唤醒其中 1 个；且 `Forget(runId)` 在全仓从未被调用，导致进程生命周期内每轮最多 4,096 帧常驻内存无法释放。
  2. `TinadecCore/DmaEA/FullDuplexRunCoordinator.cs`：`FollowAsync` 在消费 `live` 增量帧期间仍对每一批输出无条件查询一次 SQLite `GetRunStateAsync()`，在高频流式输出期放大数据库只读压力。
  3. `TinadecCore/Lifecycle/LifecycleModuleRegistrar.cs` & `StorageLifecycleService.cs`：缺少 `IRunStreamPublicationSource` 桥接，绕过引擎直接写入 `AppendRunStreamAsync` 的控制帧需等待轮询周期才可被流消费端观测。

### 2. 流式持久写放大与配置投影冗余 I/O
- **根因定位**：
  1. `TinadecCore/Lifecycle/StorageLifecycleService.cs` (`AppendRunStreamAsync`)：每次 100ms 流块落盘先后打开 `metadataDb` 与重试事务 `db` 并分别执行两次 `SaveChangesAsync()`，同时 `LocalFileContentStore.PutAsync` / `ScopeConfigurationDocuments` / `ScopeConfigurationInitialization` 对临时文件与标志位使用同步 `WriteThrough + Flush(flushToDisk: true)` 放大硬盘 IOPS 阻塞（`LocalFileContentStore` 改用 `ArrayPool<byte>.Shared` 复用 80 KiB 缓冲并移除每块强刷阻塞）。
  2. `TinadecCore/Persistence/Configuration/ConfigurationProjectionCoordinator.cs` & `ScopeConfigurationDocuments.cs`：在全套源码已结算且文档内容哈希未变化时仍会打开原始 `DbContext` 并重复验证 `__compile` 快照。
  3. 热路径 JSON 序列化与结构键分配：`AgentModelResolver` 每次预览使用 `JsonSerializer.Serialize` 拼接字典键；`AgentConfigurationEndpoints.cs`、`EvolutionEndpoints.cs`、`DmaeaEndpoints.cs`、`TinaChatIntentInterpreter.cs`、`CoreAuthorizationContextResolver.cs`、`SessionScopeTransferService.cs`、`ToolInvocationScopeResolver.cs` 在循环/请求路径重复分配 `new JsonSerializerOptions(...)`。

### 3. 模型 / CLI / ACP 外部 HTTP 连接池缺失、代理延迟与 100s 截断隐患
- **根因定位**：
  1. `TinadecCore/Abstractions/TinadecBranding.cs` & `TinadecCore/Models/Harness/AgentChatClientFactory.cs`：`CreateClient()` 每次调用新建裸 `HttpClient`（无共享 `SocketsHttpHandler` 连接池与 DNS 刷新）；`CreateBrandedOpenAiClient` 未注入共享 `HttpClientPipelineTransport`，`CreateAnthropicClient` 创建独立未释放的 `HttpClient` 且沿用默认 100 秒 `Timeout`（长推理/长回复超 100s 即被客户端强行中止，且高频轮次触发重复 TLS 握手与端口耗尽）。
  2. `TinadecCore/Models/Harness/OpenCodeChatClient.cs`：新建裸 `HttpClient` 未配置 `UseProxy = false`（Windows 下环回地址触发系统代理探测阻塞）、未设置 `Timeout.InfiniteTimeSpan`（超过 100s 长轮次必断流），且 `Dispose()` 中同步 `.PostAsync(.../abort).GetAwaiter().GetResult()` 无超时上限，服务卡顿时会拖死释放线程。
  3. `TinadecCore/AcpServer/RemoteHttpAcpCoreBackend.cs`：`HttpClient` 未显式设置 `UseProxy = false`、`PooledConnectionLifetime` 与 `ConnectTimeout`。
  4. `TinadecCore/AspNetCore/ControlPlaneService.cs`：`RefreshProviderModels` 使用独立 `HttpClient` 且 `catch (OperationCanceledException)` 未区分调用方取消与 HTTP 10s 超时，调用端断开时误报 `502 MODEL_DISCOVERY_TIMEOUT`；`ListRoutes` 逐条 N+1 查询 `RouteVersions` 与 `RouteCandidates`。

### 4. Gateway、Electron 与前端 SSE 6 连接并发上限冲突及状态抖动
- **根因定位**：
  1. **Chromium 同源 HTTP/1.1 6 连接池耗尽风险**：子窗口同样开启 `/api/v1/preferences/events` SSE 长连接（`App.vue` + `useUiPreferences.ts`），终态运行仍维持 `/live-digest/stream` SSE（`ChatCard.vue` + `LiveDigestHeartbeatStrip.vue`），叠加多个前端组件并发发起相同的 `git_worktree_manager` / `topology` 查询（`useSessionWorktrees.ts`），在 Electron 单会话下易挤占 `127.0.0.1:48730` 的 6 个 HTTP/1.1 并发连接槽，阻塞后续 REST 请求甚至触发 `ERR_ABORTED`。
  2. **SSE 读取器实现分叉与资源清理遗漏**：`apps/desktop/src/api.ts`（`openBoardEventStream`）、`liveDigestApi.ts` 与 `scopedEventSource.ts` 各自维护一套 SSE 解码循环，对分块边界拆断的 `\r\n` / 孤立 `\r`、尾包刷新及 `abort` 监听解绑存在不一致。
  3. **Gateway 取消信号、监听泄漏与终端换行 split**：`TinadecGateway/src/index.ts` 两处工具执行 POST 路由未向 Core 转发 `request.signal`；`streaming.ts` `wrapSseWithKeepalive` 正常结束时未移除 `signal` 上的 `abort` 监听器；`wsTerminalProxy.ts` 的终端流与 SSE 桥接在 Windows 跨块边界遇到拆分 `\r\n` 会错位输出 `\r\r\n` 且未在关闭时取消读锁。
  4. **Electron Host 连接状态抖动与无鉴权窗口无效轮询**：`apps/desktop/electron/hostConnection.cjs` 在已处于 `unavailable` 期间后台重试仍每次先广播 `checking` 再广播 `unavailable` 引发渲染层状态闪烁；`GovernanceBoardContent.vue` 与 `OrganizationPanel.vue` 在后端未就绪时仍立即发请求与空轮询。

---

## 二、第 4 轮落地修复清单

1. **核心引擎与持久化（`TinadecCore`）**：
   - [RunStreamProjection.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/DmaEA/RunStreamProjection.cs) & [FullDuplexRunEngine.ReviewAndCloseout.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/DmaEA/FullDuplexRunEngine.ReviewAndCloseout.cs)：以 `TaskCompletionSource<bool>` 原子信号替换无上限 `SemaphoreSlim`，单次 `Publish`/`Seal` 精确 O(1) 唤醒全部等待方并消除追平自旋；新增终态 15s 延迟回收 + `MaxTrackedRuns = 256` 淘汰，并清理已完结 `_terminalFinalizationGates`（超过 128 条时修剪空闲条目）。
   - [IRunStreamPublicationSource.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/Abstractions/Ports/IRunStreamPublicationSource.cs)、[LifecycleModuleRegistrar.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/Lifecycle/LifecycleModuleRegistrar.cs)、[StorageLifecycleService.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/Lifecycle/StorageLifecycleService.cs)、[FullDuplexRunCoordinator.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/DmaEA/FullDuplexRunCoordinator.cs)：引入持久化流提交实时订阅桥接；`AppendRunStreamAsync` 合并同一事务内两次 `SaveChangesAsync()` 为一次并及时释放只读 `metadataDb`；`FollowAsync` 在 `live` 非终态帧期间改为受 `LiveStateCheckInterval`（2s）与终态/空批约束按需查询 `GetRunStateAsync`，延迟终态帧改用 `25ms` 精细退避。
   - [TinadecBranding.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/Abstractions/TinadecBranding.cs)、[AgentChatClientFactory.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/Models/Harness/AgentChatClientFactory.cs)（仅 HTTP 传输层块）、[OpenCodeChatClient.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/Models/Harness/OpenCodeChatClient.cs)、[RemoteHttpAcpCoreBackend.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/AcpServer/RemoteHttpAcpCoreBackend.cs)、[ControlPlaneService.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/AspNetCore/ControlPlaneService.cs)：提供共享池化 `SocketsHttpHandler`（外部 15s 连接超时 / 环回 `UseProxy = false` 10s 连接超时 / `PooledConnectionLifetime = 2m` / `MaxConnectionsPerServer = 64`）与无限单调请求超时的 `CreateLongLivedClient`；接入 OpenAI `HttpClientPipelineTransport`、Anthropic 与 `OpenCodeChatClient`（并为 `Dispose()` abort 加 2s 上限）；批量化 `ControlPlaneService.ListRoutes` 并正确向上传播 `RefreshProviderModels` 调用端取消。
   - [ConfigurationProjectionCoordinator.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/Persistence/Configuration/ConfigurationProjectionCoordinator.cs)、[ScopeConfigurationDocuments.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/Persistence/Configuration/ScopeConfigurationDocuments.cs)、[LocalFileContentStore.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/Persistence/LocalFileContentStore.cs)、[AgentModelResolver.cs](file:///C:/git/agent/TinadecOffice/TinadecCore/Runtime/AgentModelResolver.cs) 及 7 处静态 `JsonSerializerOptions` 复用：增强全结算快速路径与 `__compile` 印章复用，以 `PlanCacheKey` / `ResolutionCacheKey` 结构体键取代热路径 JSON 字符串序列化。

2. **Gateway、Electron 与前端（`TinadecGateway` / `apps/desktop` / `apps/TinadecUI`）**：
   - [sseStream.ts](file:///C:/git/agent/TinadecOffice/apps/desktop/src/lib/sseStream.ts)、[api.ts](file:///C:/git/agent/TinadecOffice/apps/desktop/src/api.ts)、[liveDigestApi.ts](file:///C:/git/agent/TinadecOffice/apps/desktop/src/lib/liveDigestApi.ts)、[scopedEventSource.ts](file:///C:/git/agent/TinadecOffice/apps/desktop/src/lib/scopedEventSource.ts)：抽取统一的 `consumeSseResponseBody` 处理跨块 `\r\n` / 孤立 `\r`、尾块刷新、`abort` 监听解绑及 `reader.cancel().catch()`。
   - [App.vue](file:///C:/git/agent/TinadecOffice/apps/desktop/src/App.vue)、[useUiPreferences.ts](file:///C:/git/agent/TinadecOffice/apps/desktop/src/composables/useUiPreferences.ts)、[ChatCard.vue](file:///C:/git/agent/TinadecOffice/apps/TinadecUI/src/components/cards/home/ChatCard.vue)、[LiveDigestHeartbeatStrip.vue](file:///C:/git/agent/TinadecOffice/apps/desktop/src/components/liveDigest/LiveDigestHeartbeatStrip.vue)、[useSessionWorktrees.ts](file:///C:/git/agent/TinadecOffice/apps/desktop/src/composables/useSessionWorktrees.ts)、[GovernanceBoardContent.vue](file:///C:/git/agent/TinadecOffice/apps/desktop/src/components/governance/GovernanceBoardContent.vue)、[OrganizationPanel.vue](file:///C:/git/agent/TinadecOffice/apps/desktop/src/components/organization/OrganizationPanel.vue)：子窗口改经 `BroadcastChannel` 同步偏好而不再多开 `/preferences/events` SSE；终态运行自动关闭 `LiveDigest` SSE；`useSessionWorktrees` 对同键 `worktrees` / `topology` / `diff_preview` 进行 in-flight 并发合流；未就绪阶段暂停轮询并在 `canAccessBackend` 转真时统一刷新。
   - [hostConnection.cjs](file:///C:/git/agent/TinadecOffice/apps/desktop/electron/hostConnection.cjs)、[TinadecGateway/src/index.ts](file:///C:/git/agent/TinadecOffice/TinadecGateway/src/index.ts)、[streaming.ts](file:///C:/git/agent/TinadecOffice/TinadecGateway/src/streaming.ts)、[wsTerminalProxy.ts](file:///C:/git/agent/TinadecOffice/TinadecGateway/src/wsTerminalProxy.ts)：`hostConnection` 后台自动重连期间保持 `unavailable` 状态不闪烁；Gateway 工具 POST 透传 `request.signal`、正常结束移除 `abort` 监听，并修复终端代理跨块 `\r\n` 错位及 reader 取消清理。

---

## 三、实测基准与回归验证记录

### 3.1 隔离实机基准（基于用户库离线一致性快照，647 Agent 版本 / 174 KB agents 响应）
- **空库启动（`task-705`）**：冷启首笔 `/api/v1/health` `20,904.7 ms`（热态 `84.9 ms`）；`/api/v1/agents` 冷 `273 ms` / 热 `23.8 ms`；`ERROR_LOG_LINES = 0`。
- **满载真实库快照（`task-717`，连续两轮冷启 Run 1 / Run 2）**：
  - `GET /api/v1/health`：Run 1 冷 `41,016.5 ms`（含首次冷预热）→ Run 2 稳态冷启 **`14,831.7 ms`**，热态 **`2.1 ms`**；
  - `GET /api/v1/agents`（174,344 B）：Run 1 热 **`199.8 ms`** / Run 2 热 **`108.3 ms`**（对比第 2 轮基线 `~926 ms`，热态再提速 **4.6x–8.5x**）；
  - `GET /api/v1/readiness`：Run 1 热 **`51.5 ms`** / Run 2 热 **`175.6 ms`**；
  - `GET /api/v1/sessions`（120,674 B）：Run 1 热 **`20.8 ms`** / Run 2 热 **`66.5 ms`**；
  - `GET /api/v1/model-routes`：Run 1 热 **`15.1 ms`** / Run 2 热 **`29.8 ms`**；
  - 运行日志错误行数（`RUN1_ERROR_LOG_LINES` / `RUN2_ERROR_LOG_LINES`）：**均为 `0`**。

### 3.2 自动化定向回归验证（全量第 4 轮改动后实测通过）
- **`TinadecCore.AgentFramework.Tests`**（`RunStreamProjectionTests|ConnectionLifecycleHardeningTests`）：**15 pass, 0 fail**（2s，覆盖无信号自旋、多订阅者唤醒、封存后驱逐、池化 `HttpClient` 复用、`OpenCodeChatClient.Dispose()` 有界超时、`FollowAsync` 跳过实时帧每批 DB 读取）；
- **`TinadecCore.Api.Tests`**（`ConnectionLifecycleRegressionTests|PerformanceAndConnectionRegressionTests`）：**14 pass, 0 fail**（40s，含 `RefreshProviderModels` 取消透传、`ListRoutes` 批查询、`AppendRunStreamAsync` 直写投影联动）；
- **`TinadecGateway`**（`bun test src` across 21 files）：**138 pass, 0 fail**（3.17s，含新增 `streaming.test.ts` abort 监听释放与终端 Split CRLF 回归）；
- **`apps/desktop/electron/hostConnection.test.cjs`**：**9 pass, 0 fail**（覆盖后台不可用静默重连不闪烁）；
- **`apps/desktop` TypeScript & 定向 Vitest**：`bunx vue-tsc --noEmit` 退出码 `0`；5 个回归套件（`sseStream.test.ts`、`useConnection.test.ts`、`runStream.test.ts`、`scopedEventSource.test.ts`、`useUiPreferences.test.ts`）**52 pass, 0 fail**；
- **`apps/TinadecUI` Vitest**（21 files）：**221 pass, 0 fail**（5.27s）。
