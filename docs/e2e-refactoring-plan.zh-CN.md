# TinadecOffice 端到端落地重构计划（修订版 v2）

- 版本：v2（2026-09-05）；执行状态更新：v2.4（2026-09-06，见文末"执行状态"）
- 取代：Downloads 版《TinadecOffice 端到端落地重构计划》（下称 v1）。v1 的阶段框架与验收命令继续有效，本文按 2026-09-05 代码核查事实收窄范围、修正过时判断。
- 证据基线：TinadecOffice 工作树现状（下文 `file:line` 均相对仓库根）；参考项目取自本地克隆 `C:\git\agent\{opencode, t3code, better-harness, agent-framework}`，行号对应克隆时点。
- 边界不变：MAF（agent-framework）继续作为运行时；`TinadecOfficePi` 仅作架构参考文档，不进入运行时链路；不清理用户本地文件 `.claude/settings.local.json`。

## 0. 结论摘要

1. 四个交界面都不是空壳：运行引擎、审批与 manifest hash、持久化队列、共享流客户端、SecretStore、Agent Pack 安装流均已建成。真正的"最后一步"是**收敛一致性**（状态机收口、错误分类、词表单一出处）和**让可用性可见**（readiness 四态、连通性探针、e2e 验收）。
2. v1 有 6 项判断已过时（§1.1），另有一批真实缺口 v1 未覆盖或低估（§1.2）。本 v2 逐项修正。
3. 交界面一的第一刀仍是**日志旁路**：`FullDuplexRunEngine.cs:2210` 在监督重规划失败回退路径上直呼 `_logger?.LogWarning`，日志 provider 抛异常会把可恢复的运行直接打成 error；TryLog* 安全日志目前只是该文件的私有方法，另有 8 文件 9 处运行时路径直接调用。
4. 状态机的真实缺口不是"没有状态机"，而是**不收口**：无版本/条件更新、`CompleteRunAsync` 绕过状态机、F# `StateTransition.fs` 是与 C# 规则冲突的死代码、零直接单测。
5. 交界面二的缺口在**协议面**：interaction receipt 缺 4 个字段；run stream 无 `event:` 类型行、无 `occurred_at`、无心跳；Gateway OpenAPI 虚标 Core 不产生的事件种类（ghost API）；Core invoke-stream 与 Gateway 悬空代理仍未退役。
6. 交界面三的缺口是**可用性不可见**：无模型连通性探针（key 是否真可用只有运行时才知道）；readiness 只有 ready|warning 两态且不含模型项；TinadecTools.exe 需先手动构建否则 503；无 e2e 脚本、无本地模型 fixture。
7. 交界面四的缺口是**词表与白名单**：run 状态 12（Gateway）/10（Desktop generated）/13（Core ToolDispatchStatus）三套并存且 `normalizeRunStatus` 是恒等函数；Gateway mapper 白名单丢字段并推导默认值；api.ts 2364 行手写 DTO；OpenAPI drift 门当前处于失败状态（`core-openapi.log`）。
8. 借鉴优先级：交界面二取 opencode durable seq + t3code afterSequence 续传；交界面一取 opencode fromError 单点分类 + MAF `Lost` 终态 + better-harness 生命周期不变量；交界面三取 t3code 三维探测快照 + opencode "错误信息给可执行修复命令"；交界面四取 t3code epoch 去重 + 三方 OpenAPI 生成管线。
9. 阶段框架保留 0-6；阶段 4 从"建设"改为"补验收 + 写操作 E2E"，新增 RecoveryCoordinator 统一 4 套恢复规则（§8）。
10. 验收命令不变：`npm run restore:dotnet` / `npm run build` / `npm test` / `dotnet test TinadecCore/TinadecCore.slnx` / `npm run e2e:local` / `npm run e2e:real`（仅显式配置真实 key 时）。

## 1. 与 v1 计划的事实差异

### 1.1 v1 判断已过时（相应条目删除或改写）

| # | v1 判断 | 核查事实（2026-09-05） | v2 处理 |
|---|---|---|---|
| 1 | "无 `run_id` 的 queued interaction 可能只留在内存" | 已持久化到 `RunDirectives` 表并写 `run.queued` 事件，幂等键 `session:{sid}:queued:{clientMessageId}` 支持重放（`TinadecCore/AspNetCore/Endpoints/InteractionsEndpoints.cs:187-215`），有集成测试（`FullDuplexEndpointTests.cs:1148`） | 删除该风险项；改为补 receipt 字段（§4.3） |
| 2 | "提取共享的 RunStreamClient"（作为待办） | 已存在：`apps/desktop/src/composables/useRunStream.ts:85-217` 的 `createRunStream` 含指数退避重连（500ms→30s 封顶）、`after_seq`+`Last-Event-ID` 续传、去重、终态停止；`HomeController.ts:207` 已按活动 run 持有 handle | 改为"退役 `stores/run.ts` 的 invoke-stream 旧路径"（§6.3） |
| 3 | "Gateway 的 `/api/v1/code/tools/*` 默认指向 48732" | `/api/v1/code/tools` 与 `POST /api/v1/code/tools/:toolId/execute` 已改道 Core，注释明言不再咨询旧 runtime URL（`TinadecGateway/src/index.ts:1035-1046,1079-1091`） | 范围收窄为 `/api/v1/tool-runtime/health|manifest|tools` 三条路由（`config.ts:72` 默认 48732，仓库内无进程监听，`toolRuntimeClient.ts:67-92` 恒 502） |
| 4 | "`awaiting_approval`/`awaiting_user`/`awaiting_delegate` 可能因重启被误判失败" | 两套扫描都显式排除三个 awaiting 状态：启动恢复 `RunRecoveryHostedService.cs:37-43`、lease 扫描 `StorageLifecycleService.cs:347-362`；`RunAwaitingExternalDecisionException` 保留 checkpoint 并释放 lease（`FullDuplexRunEngine.cs:315-320`） | 风险降级；真实缺口是 4 套恢复规则并存、无统一 RecoveryCoordinator（§4.1 第 9 条） |
| 5 | "审批请求持久化、参数 hash、manifest 冻结"（作为待办建设） | 已大部建成：审批协调器有 `approval_binding_mismatch` 等完整错误分类（`ToolApprovalCoordinator.cs:538-630`）、park 过期与恢复审计；`write_file` 覆盖需匹配 `file_hash` 否则 REJECT（`TinadecTools/Tools/FileRW/FileSystemTools.cs:123-176`）；mutating 超时/进程退出 → `outcome_unknown` 并暂停 lane（`ToolDispatcher.cs:429-437`） | 阶段 4 从建设改为"补验收 + 写操作 E2E"（§8 阶段 4） |
| 6 | "幂等键：重复 `client_message_id` 不生成第二条"（作为待办） | coordinator 层已实现：同内容重试返回原 run（`Existing:true`），异内容 409 `IDEMPOTENCY_KEY_REUSE`（`FullDuplexRunCoordinator.cs:103-132`，并发竞态兜底 :186-199） | 保留为回归测试项；新增工作只是把幂等语义暴露到 receipt |

### 1.2 v1 未覆盖或低估的真实缺口

- **交界面一**：
  - `FullDuplexRunEngine.cs:2210` 直呼日志（P0 仍在）；TryLog* 是 `FullDuplexRunEngine.cs:343-359` 的私有方法，仅覆盖本文件；另有 8 文件 9 处直接调用：`RunRecoveryHostedService.cs:58,72`、`UserToolActionRecoveryHostedService.cs:32`、`ToolDispatcher.cs:422`、`ToolEventPump.cs:52`、`TinadecToolsProcessManager.cs:312`、`DatabaseReadiness.cs:65`、`AcpChatClient.cs:179`、`AgentRuntimeConfiguration.cs:326`。
  - `RunStatusMachine`（`StorageLifecycleService.cs:1147-1177`）无版本号/条件更新（RunRecord 无 RowVersion，`LifecycleDbContext.cs:232-268`）；`CompleteRunAsync`（:159-168）绕过状态机直接置 completed 且重复调用会重写 `CompletedAt`。
  - F# `Strategies/StateTransition.fs:10-46` 是死代码（仅 `FSharpInteropTests.cs:58-75` 引用），且允许终态复活（`"failed"→"pending"`、`"cancelled"→"pending"`，fs:39-40）、完全缺 awaiting_* 规则、`_, "paused"` 通配允许终态进 paused——与 C# 表冲突。
  - 业务模块大量字符串比较状态：`StorageLifecycleService.cs:178,336,355-357`、`ToolApprovalCoordinator.cs:523,824,833,960-982`、`InteractionsEndpoints.cs:195`、`FullDuplexRunEngine.cs:520,851,1183-1184,1922`；同时存在 `RunStatus` 枚举双轨（`FullDuplexRunCoordinator.cs:636`）。
  - 错误分类是散落字符串字面量，无统一 taxonomy，缺 config/model/protocol 三类（§3.1 第 4 条）。
  - RunStream 侧有幂等键（`StorageLifecycleService.cs:692-699`；done/error/cancelled 键 `FullDuplexRunEngine.cs:2649,2670,3130`），但 `AppendEventAsync` 事件侧没有——重复调用会重复追加 `run.failed`/`task.cancelled` 事件。
  - `RunStatusMachine` 无任何直接单测；TryLog 安全日志零测试。
- **交界面二**：
  - receipt 缺 `client_message_id / context_revision / stream_cursor / correlation_id`（现返回 `interaction_id/session_id/run_id/turn_id/dispatch_mode/status/mode_version_id/meeting_model_override`，`InteractionsEndpoints.cs:231`）。
  - run stream 信封无 `event:` 类型行、无 `occurred_at`、无独立 `interaction_id`（`AspNetCore/Endpoints/DmaeaEndpoints.cs:604-607,111-124`）；run stream 无心跳（15s 心跳只在 `/api/v1/events`，`StorageEndpoints.cs:360`）。
  - Gateway OpenAPI 虚标 Core 不产生的 kinds（`ack/heartbeat/task_node_update/supervision_update/context_version_update`）与 `occurred_at/payload` 字段（`TinadecGateway/src/index.ts:563,618,789`）——ghost API；desktop 旧客户端按虚标字段解析（`api.ts:2200,2244`），`sseMapper.ts:24` 以 `new Date()` 兜底。
  - Core `POST /sessions/{sessionId}/invoke-stream` 仍在（`TinadecCore/AspNetCore/Endpoints/DmaeaEndpoints.cs:26-84`）；Gateway `GET /sessions/:sessionId/interactions/:interactionId/stream` 悬空代理仍在（`index.ts:1759-1782`，Core 无对应路由，必 404）。
  - insert/steering 分支的 `interaction_id` 是临时 `Guid.NewGuid()`，不落库为交互实体（`InteractionsEndpoints.cs:152-160`）。
- **交界面三**：
  - 无任何模型连通性预检/真实调用探针：`CheckReadinessAsync` 只做路由解析（`ModelsModuleRegistrar.cs:70-90`）；"真实调用成功"只在 run 时发生（`IAgentChatClientFactory.cs:82-89`）。
  - Core readiness 只有两态 `ready|warning`（`CoreDiagnosticsEndpoints.cs:113`），`FrameworkReady` 恒 true（:9/:119），不含模型项；`/api/v1/model-catalog-readiness` 硬编码 0/warning（`StubEndpoints.cs:75-89`）；`/api/v1/doctor` 返回空 checks（:34-39）。
  - TinadecTools.exe 需先 `dotnet build` 才能被自动探测到（`TinadecToolsProcessManager.cs:41-81`），`npm run dev` 不构建它。
  - 无 e2e-local-loop 脚本、无本地 OpenAI-compatible fixture；`dev.mjs:31-56` 只等 Vite 5173，不等 Core/Gateway。
- **交界面四**：
  - run 状态词表三套并存：Gateway `runMapper.ts:41-43` Set 有 12 个状态（注释却写 10-state validator，`normalizeRunStatus` 是恒等函数 :44-48）；Desktop `generated/client.ts:13-26` 只有 10 个；Core `ToolDispatchStatus` 13 个常量（`IToolDispatcher.cs:67-82`，属工具域但加剧混乱）；HomeController/`ChatHeader.vue:17` 又自造 `running/ready/pending/queued` 词表（`HomeController.ts:80`）。
  - Gateway mapper 白名单丢字段 + 推导默认值：`runMapper.ts:25` status 缺失填 `'unknown'`；`sessionMapper.ts:27-42` `project_id` 缺失填 `''`、`lifecycle_status` 非 archived/trashed 一律推导 `'active'`、`meeting_model_override` 缺 provider_instance_id 整段丢弃；`projectMapper.ts:32-43` 同样推导 active；`orchestrationMapper.ts:5-19` 重建对象丢掉白名单外全部键；`errorMapper.ts:24-29` 把多种 NOT_FOUND 压成 `run_not_found`。
  - api.ts 手写 2364 行、128 个 interface（注释自认 generated client 才是 canonical，`HomeController.ts:21`）；`stores/run.ts:85` 仍实现 `POST /invoke-stream`、无重连（:143-147）、去重 Set 无上限（:38-44）。
  - OpenAPI drift 门当前失败：`core-openapi.log:18-24` 记录 `CoreOpenApiSnapshotTests` 快照漂移未收敛。

## 2. 判断标准（继承 v1，新增两条）

v1 的九条验收条件全部保留：一条命令启动且失败原因明确；fixture 与真实 key 双闭环；`POST /interactions` 返回持久化接收回执且结果只经 run stream；全链路可重放记录；刷新/重启/断线不制造重复；副作用恰好一次；三端状态一致；未实现显式 `unavailable/blocked`；MAF 运行时不变。

新增：

11. **run 状态词表单一出处**：12 态词表只在共享 Contracts 层定义一次，Core/Gateway/Desktop 全部引用或生成自它；任何组件不得自造状态名。
12. **OpenAPI 不得虚标**：对外 spec 中声明的每个事件 kind 与字段必须在 Core 有产生点；drift 门必须保持绿色。

## 3. 交界面一：错误与状态机一致性

### 3.1 代码现状

1. 12 态状态机已存在且规则合理：`planning, understanding, executing, replanning, awaiting_approval, awaiting_delegate, awaiting_user, paused, reviewing, completed, failed, cancelled`（`StorageLifecycleService.cs:1149-1152`）；`CanTransition` 单方法 switch（:1156-1176）；同状态幂等 true；终态一律 false；`SetRunStatusAsync`（:181-193）校验非法迁移抛 `InvalidOperationException` 并在终态写 `CompletedAt`。
2. 但并发与收口缺失：状态变更是读-改-写，无版本号；对照之下 checkpoint 走 `CheckpointRevision` CAS（:490-497）、lease 走条件 CAS（:586-608）——即仓库内已有条件更新的先例可循。`CompleteRunAsync`（:159-168）绕过状态机，且 :191 显示 `SetRunStatusAsync` 写的 `CompletedAt` 会被它重写。
3. F# `StateTransition.fs` 与 C# 表冲突（终态可复活、缺 awaiting_*、legacy 别名 pending/running）且是死代码。
4. 错误分类现状：散落字符串——审批类 `approval_missing/approval_expired/approval_binding_mismatch/not_approved/approval_consumed_without_outcome`（`ToolApprovalCoordinator.cs:538,551,588,607,618,630`）、工具类 `timeout/process_exit/tool_error`（`ToolDispatcher.cs:748-754`）与 `tool_prepare_failed/manifest_changed/blocked/already_running`、运行时类 `worker_unavailable/runtime`（`FullDuplexRunEngine.cs:332`）、恢复类 `recovery_failed/outcome_unknown/snapshot_failed/snapshot_override/recovery_marked_failed`（`UserToolActionService.cs:206,285-292`）。**无 config/model/protocol 三类**。
5. run_failed 持久化已带 `error_category`（`FullDuplexRunEngine.cs:3115-3131`：SetRunStatus(failed) + CompleteTurn + `run.failed` 事件 + RunStream error chunk）；RunStream 终态有幂等键，事件侧没有。
6. checkpoint 已有 CAS + 幂等键 + 内容哈希（`StorageLifecycleService.cs:451-521`，冲突抛 `RunCheckpointConflictException` :513-520）；恢复审计已存在（`run.recovered` reason=host_restart、`approval.park_expired`、`approval.auto_decided`）。
7. 测试现状：重规划回退有测试（`FullDuplexEndpointTests.cs:1758` `Supervision_Revise_PlannerGarbage_FallsBackToRetry` 等 5 个监督用例）；awaiting_* 有间接覆盖；状态机直接单测与安全日志测试为零。

### 3.2 可复用模式对照

| 参考模式 | 出处 | 机制 | 借鉴方式 |
|---|---|---|---|
| 单点错误分类 + 重试策略解耦 | opencode `packages/opencode/src/session/message-v2.ts:606-734`（fromError 把 Abort/Auth/API(isRetryable)/ContextOverflow/Unknown 五类归一）；`session/retry.ts:33-98,183-207`（retry-after 优先、overflow 永不重试、5xx 强制可重试、attempt/next 写入消息元数据） | 一处分类、全链路复用；重试决策不散落在业务里 | 建 `RunErrorTaxonomy` 单点：`config/model/tool/approval/recovery/protocol/cancelled/unknown` 八类 + `IsRetryable`；`FailRunAsync` 只经它分类 |
| 纯 decider 幂等 re-settle | t3code `apps/server/src/orchestration/decider.ts:491-509`（对已 settled 线程重复 settle：重发事件但保留原 settledAt，幂等不抖动） | 终态重复请求保留首次事实 | `CompleteRunAsync` 收口进状态机：终态重复调用返回原 `CompletedAt`，不重写 |
| 生命周期不变量由 emitter 强制 | better-harness `packages/harness/src/exec/events.ts:27-40`（四条不变量写进类型文档）、`:86-176`（越相调用忽略而非抛错）、`:187-193`（listener 异常吞掉——观察者不能杀死执行） | 状态机的合法迁移写成一个单点守卫类 | `RunStatusMachine` 迁移表移入共享 Contracts 层；`AppendEventAsync` 增加终态幂等键时复用"单点守卫 + 审计"形态 |
| `Lost` 第四终态 | MAF `dotnet/src/Microsoft.Agents.AI/Harness/BackgroundAgents/BackgroundTaskStatus.cs:20-33`（Running\|Completed\|Failed\|Lost，Lost=重启后终态不可判定） | 显式承认"既非成功也非失败" | 恢复语义中给"checkpoint 无效/结果未知"一个显式落点（映射到现有 `outcome_unknown` 与 failed+recovery 分类），避免硬造成功或失败 |
| 错误以事件传播 | MAF `Workflows/ExecutorFailedEvent.cs:12`、`WorkflowErrorEvent.cs:13`（异常作为流事件而非中断） | 错误是可重放事实 | 已对齐（`run.failed` 事件 + error_category）；补事件幂等即可 |

### 3.3 工作项

1. 【P0】把 TryLog* 提炼为共享诊断扩展（如 `Diagnostics/SafeLog.cs`），覆盖 §1.2 列出的 9 处直接调用；测试环境默认只启用 Console provider，Event Log 必须显式配置。
2. 【P0】为"监督重规划失败回退"增加回归测试：注入会抛异常的 `ILoggerProvider`，断言运行仍能回退重试并 `done`。
3. 【P1】状态机收口：迁移表移入共享 Contracts/Abstractions 层（C# 生命周期服务与 F# 策略层共用）；`CompleteRunAsync` 改走 `SetRunStatusAsync` 并幂等；状态变更增加版本号/条件更新（复用 checkpoint CAS 先例）。
4. 【P1】F# `StateTransition.fs` 处置：改为调用共享规则（或删除死代码并保留 interop 测试语义），终态复活规则必须消失。
5. 【P1】`RunErrorTaxonomy` 枚举 + `error_category` 全量收编（审批/工具/运行时/恢复字符串 → 枚举值），补 config/model/protocol 三类；`run_failed` problem details 引用枚举。
6. 【P1】`AppendEventAsync` 增加幂等键（对齐 RunStream 侧先例），保证 `run.completed/run.failed/run.cancelled` 事件只持久化一次。
7. 【P2】清理业务模块字符串状态比较，统一走状态机谓词。

## 4. 交界面二：交互提交与唯一流

### 4.1 代码现状

1. 提交入口 `POST /api/v1/sessions/{sessionId}/interactions`（`InteractionsEndpoints.cs:18`，handler :25-232）已支持 `client_message_id / agent_mode / dispatch_mode(queued|insert|parallel) / target_run_id / meeting_model_override / expected_context_revision`；幂等在 coordinator 层完成（§1.1 第 6 条）。
2. queued 持久化已完成（`RunDirectives` 表 + `run.queued` 事件 + 幂等重放，:187-215）。
3. receipt 不完整（缺 4 字段，§1.2）；insert/steering 分支 `interaction_id` 为临时值不落库。
4. run stream `GET /api/v1/runs/{runId}/stream`（`AspNetCore/Endpoints/DmaeaEndpoints.cs:86-137`）已支持 `after_seq` + `Last-Event-ID`（取 max，:96-97）、非法 cursor 400 `INVALID_STREAM_CURSOR`、从 RunStream 表按 `Sequence > cursor` 重放（`FullDuplexRunCoordinator.cs:484-518`）、终态 2s 宽限防丢终态对（:505-514）；序号由 `RunStreamCursors` 单调分配无缺口（`StorageLifecycleService.cs:701-724`）。
5. 信封缺陷：无 `event:` 类型行、无 `occurred_at`、无 interaction_id；实际 kinds 只有 `delta/done/error/control/queued/assigned/steering/context_conflict`，Gateway OpenAPI 却虚标 5 个 ghost kinds（§1.2）。
6. 无心跳（run stream）；`/api/v1/events` feed 有 15s 心跳与 EventEnvelope（`StorageEndpoints.cs:199-277,360-370`）。
7. 遗留路径三处并存：Core invoke-stream（`DmaeaEndpoints.cs:26-84`）、Gateway 悬空 interactions stream 代理（`index.ts:1759-1782`）、Desktop `stores/run.ts` invoke-stream 客户端（:85）。
8. Desktop 已有唯一健康流客户端 `useRunStream.ts`（重连/续传/去重/终态停止），HomeController 已使用；`stores/run.ts` 与之并存构成双 runs 记账。
9. 恢复规则 4 套并存、无统一 RecoveryCoordinator：RunRecoveryHostedService（启动一次性，`RunRecoveryHostedService.cs:14-75`）、引擎 lease 扫描（每 2s，`FullDuplexRunEngine.cs:110-128`）、UserToolActionRecoveryHostedService（`Runtime/UserToolActionRecoveryHostedService.cs:14-35`）、ToolApprovalCoordinator park 过期（`:571,735`）。

### 4.2 可复用模式对照

| 参考模式 | 出处 | 机制 | 借鉴方式 |
|---|---|---|---|
| durable per-aggregate seq 单事务提交 | opencode `packages/core/src/event.ts:205-367`（commitDurableEvent：单事务内 seq 单调校验 + 投影 + 落表）；`:262-290` 重放幂等（`seq <= latest` 且内容 deep-equal → 吞掉，否则 die "Replay diverged"）；`:294-302` seq 必须恰好 latest+1 | 事件只持久化一次、seq 严格单调、重放可验证 | run stream 幂等键已对齐一半；`AppendEventAsync` 补齐同款幂等语义，并在恢复/重放路径做 deep-equal 校验而非静默跳过 |
| afterSequence + completionMarker 续传协议 | t3code `packages/contracts/src/orchestration.ts:573-613`（订阅带 afterSequence 从该 seq 重放后转实时）、`requestCompletionMarker`（catch-up 与 live 之间插入显式标记防越界）、`:626-665` per-thread 水位线 | 比 SSE Last-Event-ID 更完备的续传设计 | run stream 已有 after_seq+Last-Event-ID；补"重放完成标记"（一个显式 catch-up-boundary 事件），前端凭它切换"补历史/实时"两态渲染 |
| 重放上限降级快照 | t3code `apps/server/src/ws.ts:318-328`（落后超阈值直接发新快照代替逐事件重放，注释明言曾 OOM）；`:773-836` 合并窗口与 marker 分段排队 | 服务端自保护 | Desktop 断线重连若 cursor 落后过多（如 > N 千事件），Core 返回 `projection_reload` 提示，前端改拉 session projection 而非逐事件追赶 |
| commandId = correlationId | t3code `packages/contracts/src/baseSchemas.ts:61-64`、`orchestration.ts:176`（回执事件回带 correlationId，天然幂等关联） | 提交回执与事件流同键贯穿 | receipt 增加 `correlation_id`（= client_message_id 派生），run stream 事件透传，三端可关联一次提交 |
| eager 订阅 + 心跳 + disposed 帧 | opencode `handlers/event.ts:29-33`（订阅先于 HTTP body 启动注册，杜绝首事件丢失）、`:59-66`（10s 心跳 + 显式 disposed 终止帧） | 连接生命周期显式化 | run stream 增加 heartbeat kind（复用 `/api/v1/events` 的 15s 先例）；SSE 关闭发终态帧 |
| 审批批处理状态机 | MAF `ToolApprovalState.cs:13-65`（standing rules / surfaced 响应只认 surfaced ID / queued 逐个抛出 / collected 攒批）、`ToolApprovalAgent.cs:55`（自动审批迭代上限 40 防死循环）；opencode `permission/index.ts:109-166`（always 追加规则并自动放行同会话新变为允许的 pending；reject 级联拒绝） | surfaced ID 绑定防伪造响应；批量收集防规则竞态 | 已建审批链路对齐 surfaced-ID 语义（`approval_binding_mismatch`）；补"同批 pending 的 always 放行/级联拒绝"与自动审批迭代上限 |

### 4.3 工作项

1. 【P0】receipt 补全：`POST /interactions` 响应增加 `client_message_id / context_revision / stream_cursor / correlation_id`，保留现有字段；insert/steering 分支的 `interaction_id` 落库或显式标注为 ephemeral。
2. 【P0】run stream 信封升级：增加 `event:` 类型行与 `occurred_at`（`EventEnvelope.cs:9-32` 已有 timestamp 先例）；增加 heartbeat kind；`data` 字段与 Gateway OpenAPI 对齐（消除 ghost kinds，见交界面四工作项）。
3. 【P1】重放完成标记：run stream 在 catch-up 结束、转实时前发一个显式 boundary 事件；前端凭它区分补历史/实时。
4. 【P1】退役遗留路径：删除 Gateway 悬空 `interactions/{id}/stream` 代理；Core invoke-stream 保留为显式兼容适配器一个版本周期后删除；Desktop `stores/run.ts` 的 invoke 路径改投 `createRunStream` 后整体退役。
5. 【P1】统一 RecoveryCoordinator：单一服务聚合"启动孤儿扫描 / lease 扫描 / user-tool 恢复 / park 过期"四套规则，统一审计事件与日志通道；awaiting_* 保护与 30s admission grace 语义保持不变。
6. 【P2】Desktop 重连降级：cursor 落后过多时按 Core `projection_reload` 提示改拉 projection。

## 5. 交界面三：干净环境的模型/工具可用性

### 5.1 代码现状

1. DevSeed 语义正确：只 seed provider（无 key，`SecretReference` 占位，`DevSeed.cs:43`）、chat 路由、Agent 目录与默认模式（`bootstrap-agent-directory.toml:11-12`）；类注释明言不伪造可用性（:13-19）。首跑缺口不是"假成功"而是"用户不知道下一步"。
2. 密钥设施已齐：`ISecretStore` 双实现（Windows DPAPI `ProtectedFileSecretStore` / `EnvironmentSecretStore`，`ISecretStore.cs:4-64`，按 OS 注册 `ServiceCollectionExtensions.cs:50-52`）；模型中心录入/清除 key 走 `ControlPlaneService.cs:349`，响应前 `RemoveSecret`。
3. 四层区分已可表达三层：provider 存在/启用（`provider_disabled`/`provider_version_missing`，`AgentModelResolver.cs:293-296`）→ key 存在（`credential_missing`，:314-316）→ 路由可用（`ChatResolution.IsAvailable`，`IModelProvider.cs:65-90`）；第四层"真实调用成功"无探针。
4. readiness 碎片化：Core `/api/v1/readiness` 两态且不含模型（`CoreDiagnosticsEndpoints.cs:94-146`）；`/model-readiness` 只解析路由（`StubEndpoints.cs:41-73`）；`/model-catalog-readiness` 硬编码（:75-89）；`/doctor` 空 checks（:34-39）；只有 `/tool-layer-readiness` 是真的（拉 TinadecTools manifest 报 unresolved_tools，:91-159）。
5. Agent Pack 安装流已完整（manifest sha256 envelope、Preview/Apply、If-Match revision、Idempotency-Key 重放、workspace_defaults 落地与审计，`AgentPackService.cs:234-489,889-945`）；桌面侧有 bootstrap（`officeAgentPackBootstrap.ts`）。
6. 工具链已完整：`IToolProvider/IToolProcessManager/IToolRegistry/IToolDispatcher/IToolExecutionCoordinator`（`IToolDispatcher.cs:11-230`）、DirectToolEndpoints（读直调、mutating 202 blocked 指向 UserToolAction，`DirectToolEndpoints.cs:16-231`）、TinadecTools stdio 子进程（manifest hash 握手 `ToolCalling.cs:74-131`、进程崩溃 FailPending+自动重启 `TinadecToolsProcessManager.cs:339-383`、调用超时 :150-173、写串行化与 pre-write 快照 `ToolDispatcher.cs:337-355,457-467`）。
7. Gateway：`/api/v1/code/tools` 已改道 Core（§1.1 第 3 条）；`/api/v1/tool-runtime/health|manifest|tools` 仍指 48732（`config.ts:72`），恒 502 `TOOL_RUNTIME_UNREACHABLE`（`toolRuntimeClient.ts:67-92`）；POST execute 实际已转 Core（`index.ts:2164-2174`）。
8. 启动体验：`npm run dev` 三组件并发起、无 readiness 等待；TinadecTools 需预构建；无 fixture、无 e2e 脚本。

### 5.2 可复用模式对照

| 参考模式 | 出处 | 机制 | 借鉴方式 |
|---|---|---|---|
| 三维探测快照 | t3code `apps/server/src/provider/providerSnapshot.ts:22-51,243-246`（installed/status/auth 三维 + 注释明言 Windows 首跑慢所以缓存）；不可用降级快照 `unavailableProviderSnapshot.ts` | 可用性是三维快照而非布尔 | 模型 readiness receipt 建模为 `provider_registered / secret_present / route_resolvable / probe_ok` 四维（对齐已有三层错误码 + 新增探针层），并缓存探测结果 |
| 错误信息给可执行修复命令 | opencode `provider.ts:784-792`（"Set with: export X=..."）；auth.json 0o600 + env 覆盖（`auth/index.ts:10,59-63,79`） | 每个 blocked 项给下一步动作 | readiness 每项带 `action` 字段（如 "在模型中心为 provider {id} 录入 API key" 或 `dotnet build TinadecTools`）；真实 key 只进 SecretStore/env，不进 renderer/日志/API 响应（已满足，保持） |
| 能力矩阵 + 显式拒绝码 + 最廉价拒绝前置 | better-harness `docs/docs/hosts/adapter-matrix.md`、`executor.ts:109-126`（preflight 最便宜的拒绝放最前） | 能力矩阵文档与运行时拒绝码同源 | readiness 检查项按成本排序（DB probe → TOML → 模型路由 → 探针 → 工具 manifest）；`docs/startup.md` 的就绪项与 readiness 项一一对应 |
| 初始化失败类型化 | MAF `python/.../exceptions.py:120-121` IntegrationInitializationError、`:85-86` ChatClientInvalidAuthException | 初始化失败是独立错误类 | `RunErrorTaxonomy.config/model` 两类覆盖探针与运行时的初始化失败（呼应交界面一） |

### 5.3 工作项

1. 【P0】readiness 收敛为统一 receipt：`GET /api/v1/readiness` 返回总状态 `ready | degraded | blocked` + 检查项数组（数据库迁移、Core 存储、Agent Pack、默认模式、模型 provider、secret、模型路由、模型探针、工具 provider、manifest hash、Gateway 连通性），每项含 `status/reason/checked_at/action`；废弃/合并 model-readiness、model-catalog-readiness、doctor 三个碎片端点；`FrameworkReady` 恒 true 的行为删除。
2. 【P0】模型连通性探针：服务端用已存 key 发一次最小真实请求（1 token 上限），结果写入 readiness 探针项；错误信息含可操作修复建议；本地 fixture 模式下探针打 fixture。
3. 【P1】`scripts/e2e-local-loop.ps1`：启动 fixture（本地确定性 OpenAI-compatible server，覆盖 planner/executor/supervisor/meeting 四类响应）→ Core → Gateway，等待 readiness，初始化项目/会话/Agent Pack/模型配置，提交交互，断言 receipt、run stream 序列与最终 projection；`npm run e2e:local` 挂接。
4. 【P1】`scripts/e2e-real-model.ps1`：仅显式设置真实 API key/base URL/model 时执行，默认不进 CI。
5. 【P1】dev 体验：`npm run dev` 增加 readiness 等待与失败原因输出；dev 前置构建 TinadecTools（或 readiness 将 `tool_provider_unavailable` 的 action 指向构建命令）；Gateway 三条 `/tool-runtime/*` 路由默认改指 Core 或删除（独立工具运行时仅在显式配置 URL 且 readiness 通过时启用）。
6. 【P2】`docs/startup.md` 与实际端口（48730/48731）、种子数据、readiness、关闭流程对齐。

## 6. 交界面四：Gateway/Desktop 对 Core 的完整投影

### 6.1 代码现状

1. 生成管线三层已齐但门是红的：Core `/openapi/core.json` + 快照测试 + `git diff --exit-code` 门（`CoreOpenApiSnapshotTests.cs:108-125`）；Gateway external 快照（`openapi.snapshot.test.ts:125-141`）；Desktop `generate:client`（openapi-typescript）+ `check:drift`；当前 Core 快照漂移失败（`core-openapi.log:18-24`）。
2. 词表三套并存（§1.2）；`normalizeRunStatus` 恒等函数;`statusPresentation.ts:19-30` RUN_TONES 仅 10 态。
3. Gateway mapper 白名单丢字段 + 推导默认值（§1.2 详列）；`agentsMapper` 纯透传、`readinessMapper` 脱敏透传是两个正确先例。
4. Desktop 双流路径与双 runs 记账并存（`HomeController` 用 `useRunStream` + 自维护 runs ref + 自维护 EventSource；`stores/run.ts` 独立 invoke/去重/状态映射，无重连）。
5. optimistic message 已有对账雏形：`pending-{client_message_id}`（`HomeController.ts:368`）、未回声 pending 保留防闪烁（:189-192）。
6. 不可用展示已有实例：发送失败分类（`HomeController.ts:393-400`）、模型中心 blocked/warning 排序（`modelCenterView.ts:32-36`）、`outcome_unknown` 专属恢复页（`RecoveryCheckPage.vue:10,26`）。

### 6.2 可复用模式对照

| 参考模式 | 出处 | 机制 | 借鉴方式 |
|---|---|---|---|
| 客户端 seq 单调去重 + epoch 防"历史复活" | t3code `packages/client-runtime/src/state/threads.ts:333-345`（snapshot → epoch++ 丢弃竞态旧页；`event.sequence <= lastSequence` 直接丢弃）、`:485-510`（分页 epoch+水位线 TOCTOU 检查）、`:619-633`（能力门控 afterSequence resume，否则全量快照 fallback） | 客户端是投影不是事实源；旧页不得复活 | `useRunStream` 增加 lastSeq 单调丢弃（现 dedup 是 Set 无水位线）；`stores/run.ts` 退役后 runs 记账单一化 |
| OpenAPI → SDK 生成，多端同源 | opencode `packages/protocol/src` + `packages/sdk/js/src/gen`（OpenAPI 生成 SDK，TUI/Web 同源消费） | 类型唯一出处 | 保持现有三方管线；api.ts 按领域迁移到 generated 类型，手写 DTO 只留本地 UI 类型 |
| contracts 单一来源 | t3code `packages/contracts`（所有跨线类型唯一出处，AGENTS.md 明文条款） | 词表/事件/命令集中一处 | 12 态 run 词表放共享 Contracts 层；Gateway OpenAPI 的 run 状态枚举、事件 kind 枚举从它生成，Desktop generated client 随之生成 |
| ContinuationToken 贯穿 | MAF `ChatClientAgent.cs:247-372`（增量流逐个透传 token、聚合收敛进 response） | 客户端凭 token 恢复服务端运行 | receipt 的 `stream_cursor` 即轻量 continuation token；Desktop 刷新后凭它恢复 run stream（现 useRunStream 已支持，补 receipt 字段即闭环） |
| 未知事件必须保留 | v1 已定；t3code/opencode 客户端均不丢弃未知类型 | parser 前向兼容 | Desktop parser 对未知 kind 保留并记录（现旧 parser 按虚标字段解析，重写时落实） |

### 6.3 工作项

1. 【P0】收敛 drift 门：先跑 `dotnet test` 刷新 Core 快照并 review diff，恢复绿色；CI 保持 `git diff --exit-code` 三层门。
2. 【P0】run 状态词表单一出处：12 态枚举进共享 Contracts 层；Gateway OpenAPI、Desktop generated client 从它生成；删除 `normalizeRunStatus` 恒等函数与 HomeController 自造词表（`['running','ready','pending','queued']`）。
3. 【P0】Gateway OpenAPI 去虚标：删除 Core 不产生的 5 个 ghost kinds 与 `occurred_at/payload` 虚标字段，改为 §4.3 升级后的真实信封；新增"spec 声明的 kind 必须有 Core 产生点"合约测试。
4. 【P1】mapper 规则改写：白名单 → 显式映射或全量透传；禁止推导默认值（`lifecycle_status` 缺失必须原样透传，缺失即字段缺失）；错误码映射表补全 NOT_FOUND 家族，不得统一压成 `run_not_found`。
5. 【P1】Desktop 投影归并：`stores/run.ts` 退役，runs 记账单点化（HomeController 或 Pinia 二选一）；HomeController 状态归并为 interaction / run / approval / projection 四类；`useRunStream` 补 lastSeq 单调丢弃与 catch-up boundary 两态渲染。
6. 【P1】api.ts 按领域迁移 generated 类型（先 run/approval/readiness 三个高危域），手写 DTO 只保留本地 UI 类型。
7. 【P2】`createUserToolActionForPath` 改用稳定 project id / workspace id，消除路径推导歧义（v1 遗留项，保持）。

## 7. 模式借鉴矩阵（四接口 × 四仓库）

| 接口 | opencode | t3code | better-harness | MAF |
|---|---|---|---|---|
| 一 状态机/错误 | fromError 单点分类；retry 策略解耦；终态即删除 | 纯 decider 幂等 re-settle；命令前置不变量集中校验 | 生命周期不变量由 emitter 强制；观察者异常吞掉 | `Lost` 第四终态；错误以事件传播 |
| 二 提交/唯一流 | durable seq 单事务 + 重放幂等校验；eager 订阅 + 心跳 | afterSequence + completionMarker；重放上限降级快照；commandId=correlationId | RuntimeReceipt（宿主实际确认了什么） | superstep 边界 checkpoint + ResumeAsync；RequestPort RequestId；审批批处理状态机 |
| 三 可用性 | auth.json 0o600 + env 注入；missing key 给可执行修复命令 | installed/status/auth 三维探测快照 + 首跑缓存 + 降级快照 | 能力矩阵文档 + 显式拒绝码 + 最廉价拒绝前置 | 初始化失败类型化（弱对应） |
| 四 投影 | OpenAPI→SDK 生成多端同源；分页游标 | 客户端 seq 单调去重 + epoch 防历史复活；contracts 单一来源 | 工具结果有界保留（truncated/originalBytes） | ContinuationToken 贯穿增量/聚合/恢复 |

## 8. 分阶段实施计划（修订版）

每阶段保持可运行、可独立提交；验收判据全部可机检。

### 阶段 0：可重复基线（新增：先收敛 drift 门）

- 动作：收敛 OpenAPI 快照漂移恢复三层绿色 drift 门；统一 correlation/run/interaction/request id 日志字段；`RunErrorTaxonomy` 骨架；测试环境关闭 Event Log provider；Core host 测试按数据库/端口/工具进程隔离并固定临时目录；建立"闭环验收记录"格式（启动日志、readiness、receipt、事件序列、工具结果、最终 projection）。
- 涉及：`CoreOpenApiSnapshotTests`、Gateway/Desktop 快照、日志扩展骨架、测试基础设施。
- 验收：`dotnet test` 全绿且 drift 门通过；任意 provider 抛异常时运行结果不变（回归测试）。

### 阶段 1：Core 运行正确性

- 动作：§3.3 工作项 1-6（安全日志共享扩展 + 9 处迁移 + 回归测试；状态机共享 Contracts 层 + CompleteRunAsync 收口 + 版本 CAS；F# 死代码处置；错误分类枚举收编；AppendEventAsync 幂等键）。
- 涉及：`FullDuplexRunEngine.cs`、`StorageLifecycleService.cs`、`StateTransition.fs`、`ToolApprovalCoordinator.cs`、共享 Contracts 层（新）。
- 验收：状态机直接单测（全迁移/非法迁移/并发版本冲突/终态幂等）全绿；监督重规划日志异常回归测试绿；`run.completed/failed/cancelled` 事件重复触发只落一条。

### 阶段 2：交互与流协议统一

- 动作：§4.3 工作项 1-4（receipt 补全；信封 event:/occurred_at/heartbeat；catch-up boundary；悬空路由与 invoke-stream 退役计划）。
- 涉及：`InteractionsEndpoints.cs`、`DmaeaEndpoints.cs`、`FullDuplexRunCoordinator.cs`、Gateway index.ts/sseMapper、Desktop parser。
- 验收：Gateway 合约测试断言 receipt 字段完整性、SSE headers/cursor、problem details 透传；事件序号无重复无缺口；重连不重复 delta。

### 阶段 3：模型配置、Agent Pack 与启动体验

- 动作：§5.3 工作项 1-4（统一 readiness receipt 四态；模型探针；本地 fixture；e2e-local-loop / e2e-real 脚本）+ dev readiness 等待。
- 涉及：`CoreDiagnosticsEndpoints.cs`、`StubEndpoints.cs`、`ModelsModuleRegistrar.cs`（探针）、`scripts/`（新增）、`package.json`。
- 验收：干净环境 `npm run e2e:local` 一条命令走完 fixture 闭环并产出验收记录；无 key 时 readiness=`blocked` 且提交被明确拒绝，action 字段给出修复命令。

### 阶段 4：工具、审批与恢复闭环（建设→验收）

- 动作：§4.3 工作项 5（RecoveryCoordinator）；写操作 E2E：真实 TinadecTools `write_file` + 审批 + 断言文件内容、事件数量、审计与 Core 重启后恢复行为；manifest 冻结/失配拒用的合约测试；`/tool-runtime/*` 路由处置。
- 涉及：新 `RecoveryCoordinator`、`ToolDispatcher.cs`、`TinadecToolsProcessManager.cs`、Gateway 工具路由。
- 验收：写副作用恰好一次（文件内容与审计一致）；Core 重启后活动 run 恢复、审批状态不变、awaiting_* 不被误判；未知结果不自动重放（`outcome_unknown` 路径测试）。

### 阶段 5：Gateway、代码生成与 Desktop 投影

- 动作：§6.3 工作项 2-7（词表单一出处；去虚标 + ghost kinds 合约测试；mapper 规则；Desktop 归并与 api.ts 迁移）。
- 验收：Desktop 全部 12 态可显示；run stream 与 event feed 不重复渲染；刷新后恢复活动 run/审批/最终 projection；spec 声明的 kind 全部有产生点。

### 阶段 6：主闭环之外（不变）

501 能力（scheduling、市场、扩展、MCP、ACP、debug、prompt/model-settings）、多租户与 OIDC、PostgreSQL 生产配置、发布工程、第二 provider/ToolProvider、Agent Pack 签名与灰度；重新核对 `docs/tinadec-four-product-roadmap.zh-CN.md` 删除过期声明。

## 9. 测试与验收矩阵（修订）

单元测试（新增/强化）：

- 安全日志：任意 `ILoggerProvider` 抛异常，运行状态与返回值不变。
- 状态机：12 态全合法迁移、非法迁移、并发版本冲突、重复请求幂等、终态不可复活。
- 错误分类：`RunErrorTaxonomy` 覆盖既有全部字符串字面量 + config/model/protocol 三类映射。
- 事件幂等：终态事件重复触发只落一条；重放 deep-equal 校验。
- 审批：错误 run/task、参数 hash 不匹配、过期、重复消费全部拒绝（已有，保持）；新增同批 always 放行与级联拒绝。
- SSE parser：多行 data、空行、heartbeat、重复 seq、缺失 seq、`Last-Event-ID`、未知 kind 保留。

Core 集成测试：

- SQLite 空库迁移；无 key readiness=blocked 且提交拒绝；fixture 四类响应闭环；manifest 失配拒用旧工具；Core 重启恢复活动 run 且审批不变；写操作未知结果不重放。

Gateway 合约测试：

- receipt 字段完整；SSE 状态码/headers/cursor/problem details 透传；不生成 Core 没有的字段（含 ghost kinds 反向断言）；Core 不可达/工具不可达/模型不可用/501 统一错误结构；request id、principal、last-event-id 链路可追踪。

Desktop 测试：

- receipt→run stream 连接；双 feed 不重复渲染；断线重连不重复 delta；刷新恢复活动 run/审批/projection；失败可重试无永久 pending；12 态全部可显示。

端到端验收：`npm run restore:dotnet` → `npm run build` → `npm test` → `dotnet test TinadecCore/TinadecCore.slnx` → `npm run e2e:local`（→ `npm run e2e:real` 仅显式配置时）。必查：序号无重复无缺口；一次请求一条用户消息 + 一条最终会议输出；副作用恰好一次且与审计一致；三端最终状态一致；Core/Gateway 重启与浏览器刷新后闭环可继续；CI 默认不调真实外部模型。

## 10. 提交顺序与风险控制

按序提交，每步可运行（✓=核查确认已存在，无需重做）：

1. 安全日志、Event Log 配置、监督重规划回归测试（§8 阶段 0/1）。
2. 共享状态机（Contracts 层）、状态持久化 CAS、RecoveryCoordinator（阶段 1/4）。
3. interaction receipt（补字段即可，队列持久化 ✓、幂等 ✓）与唯一 run stream（信封升级，重连/续传 ✓ 已有）。
4. Gateway 悬空路由清理与 SSE 合约（`/code/tools` 改道 ✓；`/tool-runtime/*` 与悬空代理待清）。
5. readiness 统一 receipt、SecretStore（✓）、Agent Pack 就绪项（✓ 大部）、本地 fixture。
6. 工具 provider、审批、manifest、写操作与重启恢复（建设 ✓ 大部；E2E 与 RecoveryCoordinator 待做）。
7. Desktop stream client（共享客户端 ✓ 已有）、reducer 归并、generated DTO 迁移、projection。
8. 产品扩展、认证、多租户、发布工程（阶段 6）。

风险控制：每阶段更新对应文档与验收记录，不能只更新架构图；默认假设继承 v1——SQLite 继续作为开发默认存储，Gateway 继续是 Desktop 唯一入口，第一阶段只保主闭环。

## 11. 执行状态（v2.1，2026-09-06）

主闭环已落地：`powershell scripts/e2e-local-loop.ps1`（或 `npm run e2e:local`）13 步全部 PASS，
验收记录写入 `tmp/e2e-local-loop/<时间戳>/acceptance.json`（含 readiness receipt、run stream 全量重放、最终 projection）。
真实 key 验收走 `npm run e2e:real` 路径时复用同一脚本骨架（尚未单独成文， fixture 替换为真实 provider 配置即可）。

已完成（含验证方式）：

| 工作项 | 结果 | 验证 |
|---|---|---|
| §3.3-1/2 安全日志：`Abstractions/Diagnostics/SafeLogExtensions.cs` 新增；引擎私有 TryLog* 删除，8 文件 9 处直呼 `_logger` 全部迁移；`Api/Program.cs` 默认仅 Console/Debug，EventLog 改为 `Logging:EventLog:Enabled` 显式开启 | ✅ | `SafeLoggingTests`（2）+ `Supervision_Revise_PlannerGarbage_StillCompletesWhenLoggingProviderThrows`（注入抛异常 provider，运行仍 done） |
| §3.3-3 终态幂等：`CompleteRunAsync` 终态守卫（首个终态事实获胜，不重写 CompletedAt）；`SetRunStatusAsync` 终态 `CompletedAt ??=` | ✅ | `LifecycleManagementApiTests` 终态幂等 2 测试 |
| §4.3-1 receipt 补全：`client_message_id / context_revision / stream_cursor / correlation_id` 四字段，覆盖 admission/queued/steering/replay 四个响应点；幂等重放返回同一 run | ✅ | `Interactions_AdmissionReceipt_*` + queued receipt 断言 |
| §6.3-1/2 drift 门收敛 + 词表：Core/Gateway OpenAPI 快照刷新恢复绿色；Gateway 悬空 `interactions/{id}/stream` 代理删除（测试改为断言 404 且不触达 Core）；`/tool-runtime/health\|tools` 未配置独立运行时默认改道 Core（`tool-layer-readiness` / `tools`），manifest 显式 501 `tool_runtime_not_configured`；`TINADEC_TOOL_RUNTIME_URL` 从静默 48732 默认改为空=Core 权威 | ✅ | Gateway `bun test` 44/44 |
| §5.3-1/2 readiness 收口（并行会话完成的主体，本次接手修复）：统一 receipt 十项（database/core_storage/agent_pack/default_mode/model_provider/model_secret/model_route/model_probe/tool_provider/manifest_hash）+ `POST /api/v1/model-probe`（1-token、10s 超时、60s 缓存）；修复 `ReadinessService` SQLite `DateTimeOffset ORDER BY` NotSupportedException（改内存排序）；修复 legacy `model-readiness` shim 未沿用"探针未跑不降级"例外；旧 readiness 测试按新契约重写 | ✅ | Api.Tests readiness 家族 4/4 |
| §5.3-3 e2e 本地闭环：`scripts/e2e-local-loop.ps1`（fixture→Core→readiness→provider/路由/探针→Agent Pack 规范化 sha256 安装→项目/会话→交互 receipt→终态→流重放 seq 单调+恰好一个 done→幂等→最终 projection→验收记录）；`scripts/model-fixture.mjs`（确定性 OpenAI-compatible，角色路由+流式）；`scripts/canonical-digest.mjs`（与 Core 一致的规范形摘要） | ✅ | 13/13 PASS（fixture 修复 planner 任务图 required_capabilities 为空后） |

关键教训（供后续阶段复用）：

1. **PowerShell 函数名大小写不敏感会遮蔽原生可执行文件**——脚本内 `function Node` 吞掉了 `& node`，digest 变成 Hashtable 再被序列化成 JSON 对象（即首次 install-preview 400 的真正根因）。
2. **本机环境下 `TINADEC_PERSISTENCE__*` 环境变量与 `cmd set` 均未生效，命令行参数 `--TinadecPersistence:Sqlite:DatabasePath=...` 可靠**——隔离临时库一律用命令行参数直启 dll。
3. PS 5.1 无 BOM UTF-8 脚本会被当 ANSI 读取，中文注释破坏解析；`Out-File -Encoding utf8` 写 JSON 带 BOM，node `JSON.parse` 拒绝——`canonical-digest.mjs` 已做剥离。
4. Governance 是真实生效的：fixture planner 声明 `required_capabilities:["summary"]` 而冻结名册无人可满足时，运行按 `worker_unavailable` 明确失败——e2e fixture 因此改为空能力声明。

待办（下一批）：

- §3.3-5/6 错误分类枚举收编 + `AppendEventAsync` 幂等键；§3.3-7 字符串状态比较清理。
- §4.3-2 run stream 信封补 `event:` 类型行 / `occurred_at` / heartbeat；§4.3-3 catch-up boundary。
- §4.3-4 Core invoke-stream 退役（一个版本周期后删除）；§4.3-5 RecoveryCoordinator 统一四套恢复规则。
- §5.3-5/6 dev readiness 等待、`docs/startup.md` 对齐；`e2e:real` 脚本成文。
- §6.3-5/6 Desktop `stores/run.ts` 退役与 api.ts 迁移；§6.3-3 Gateway OpenAPI ghost kinds 清理（随 §4.3-2 信封升级一并做）。

### v2.2（2026-09-06 第二批：阶段 0/1/2 收口 + e2e 走 Gateway）

| 工作项 | 结果 | 验证 |
|---|---|---|
| §阶段0 测试隔离：Api.Tests 新增 `xunit.runner.json`（parallelizeTestCollections=false）——WebApplicationFactory 宿主并发争抢 CPU/工具子进程导致时序抖动，串行后全量 370/370 零抖动（此前两轮全量各随机红 1 个不同用例） | ✅ | Core 全套 370/370（Governance 34 + AgentFramework 106 + Architecture 11 + Api.Tests 219） |
| §3.3-3 状态机共享层：`RunStatusMachine`（12 态词表 + `KnownStates` + 迁移表）从 `StorageLifecycleService` 迁入 `TinadecCore.Abstractions/RunStatus/`；F# `StateTransition.fs` 死代码改为委托共享机（终态复活/legacy 别名规则消失）；新增全表直接单测（词表、终态封闭、同态幂等、终态不可复活、逐行迁移表对拍） | ✅ | `RunStatusMachineTests` 6/6 + `FSharpInteropTests` 8/8 |
| §3.3-5 错误分类单点：`Abstractions/RunStatus/RunErrorTaxonomy.cs`——收编既有 19 个散落分类字面量为常量，新增 `config/model/protocol` 三类；引擎 failureCode、ToolDispatcher WireErrorCategory/FailAsync、ToolApprovalCoordinator 审批类、UserToolActionService 恢复类全部改引常量（值不变，语义单点化） | ✅ | 编译 0 错误；既有审批/工具用例不变绿 |
| §3.3-6 事件幂等键：`AppendEventAsync` 四层签名（ILifecycleManager/LifecycleModuleRegistrar/StorageLifecycleService/引擎 helper）新增 `idempotencyKey`；`event_index` 新增 `idempotency_key` 列 + `(run_id, idempotency_key)` 唯一索引（SQLite/PostgreSQL 双端迁移 `202609060001_EventIdempotencyKey`）；`run.failed/task.cancelled/run.recovered` 键为 `run:{id}:event:{type}`，重复触发只落一条 | ✅ | `AppendEvent_WithIdempotencyKey_PersistsExactlyOnce`（键控重放同 seq；无键行为不变） |
| §4.3-2 run stream 信封升级：`id === seq` 之外新增 `event: {kind}` 行与 `occurred_at`（取 journal `CreatedAt`，重放保真）；空闲 15s 心跳以 SSE 注释 `: heartbeat` 发出（`FollowAsync` 产出、端点渲染为注释）——不推进客户端 cursor，所有解析器按规范忽略；桌面 `useRunStream` 解析器原生兼容（无改动） | ✅ | e2e run-stream.txt 实证 `id:/event:/data:` 三行结构 |
| §4.3-4（部分）invoke-stream 标记废弃：Core 路由注释 + Gateway OpenAPI summary 标 `[Deprecated compat adapter]`；物理删除待 Desktop `stores/run.ts` 退役（§6.3-5） | ✅ | OpenAPI 快照重生成 |
| §6.3-3 ghost kinds 清理：Gateway 三处 SSE OpenAPI 描述改为 Core 实际产生集合（ack/queued/assigned/steering/context_conflict/control/ephemeral_agent/delta/done/error + 注释心跳），删除虚标 `task_node_update/supervision_update/context_version_update`；`/api/v1/events` 描述改为 EventEnvelope 真实契约 | ✅ | 快照两轮重生成后 44/44 |
| §6.3-2 词表单点化（Gateway 侧）：新增 `TinadecGateway/src/contracts/runStatuses.ts`（12 态唯一出处，指认 Core `RunStatusMachine` 为 source of record）；`runMapper` 弃用恒等函数，未知状态显式 `unknown`；`errorMapper` 停用 NOT_FOUND 家族压缩（`session_not_found/project_not_found/tool_execution_not_found/not_found` 保留原语义），新增 `tool_provider_unavailable/tool_runtime_not_configured` 透传 | ✅ | Gateway 44/44 两轮 |
| §阶段3 e2e 改走 Gateway：脚本新增 Gateway 启动/等待/清理与 `-DirectCore` 调试开关，15 步全部经 48730 验证（SSE 经 Gateway 代理、错误映射、readiness 脱敏透传）；补 Gateway 缺失的 `POST /api/v1/model-probe` 代理路由 | ✅ | 15/15 PASS（acceptance.json via=gateway） |

v2.2 关键教训：

5. **e2e 直连 Core 不算验收入口**——走 Gateway 立即暴露两处合约缺口（Gateway 缺 model-probe 代理路由；interactions 前置校验比 Core 严，`dispatch_mode` 缺省不同）。代理链路是产品真实路径，验收必须经它。
6. Gateway 对 Core 合约的"更严校验"（interactions `dispatch_mode` 必填 vs Core 缺省 queued）应收敛为一致——记入 §6.3 mapper 剩余项。

剩余待办（唯一关键批次 + 尾部清理）：

- ~~§4.3-5 RecoveryCoordinator~~（v2.3 完成，见下）
- ~~写操作/重启恢复 E2E~~（v2.3 完成，见下）
- ~~§6.3-5/6 Desktop stores/run.ts 退役、invoke-stream 物理删除、HomeController 词表清理~~（v2.3 完成，见下）
- §5.3-5/6 dev readiness 等待、`docs/startup.md` 对齐、`e2e:real` 成文；§3.3-7 字符串状态比较清理；§4.3-3 catch-up boundary。

### v2.3（2026-09-06 第三批：恢复统一 + 写操作/重启 E2E + invoke-stream 退役）

| 工作项 | 结果 | 验证 |
|---|---|---|
| §4.3-5 RecoveryPolicy：`Abstractions/RunStatus/RecoveryPolicy.cs` 单一策略层——awaiting_* 决策态保护（string + enum 双形态）、30s admission grace、终态封闭；启动扫描与引擎 lease 扫描共用（`ListLeaseEligibleRunsAsync` 删除本地副本常量） | ✅ | `RecoveryCoordinator_FailsOrphans_ProtectsAwaiting_AndIsIdempotent` |
| §4.3-5 RecoveryCoordinator：`Runtime/RecoveryCoordinator.cs` 单一启动编排点——Pass1 孤儿运行扫描（run.recovered 幂等审计）+ Pass2 用户工具动作恢复，顺序确定、单一审计/日志面；`RunRecoveryHostedService`/`UserToolActionRecoveryHostedService` 退役删除；**关键语义修复**：有 checkpoint 的非 awaiting 运行不再被启动扫描置 failed（原两套规则互相竞争——启动扫描会把引擎可续跑的运行杀掉），改为交给引擎 lease 续跑 | ✅ | 恢复测试 + e2e 步骤 17-19（kill→重启→续跑 completed） |
| §3.3-3 引擎续跑补偿：`ExecuteRunAsync` 获得租约后把遗留 running（且无 PendingToolExecutionId）任务复位为 pending 并落 `recovery.interrupted_tasks` 审计——修复"崩溃遗留 running 任务导致 No dependency-ready task remains → 运行误判 invalid_task_graph"；对齐 opencode 悬挂 tool_use 补偿模式 | ✅ | e2e 重启续跑步骤（中断任务重执行、恰好一个 done） |
| §4.3-4 invoke-stream 物理退役：Core 删除 `POST /sessions/{id}/invoke-stream` 端点与 `InvokeStreamRequest` DTO；Gateway 删除代理路由与 `invokeStreamMapper`；Desktop `api.invokeStreamWithAdmission` 重写为 interactions+run-stream 内部适配器（调用契约不变，AI 提交信息/变更分析功能无感）；`stores/run.ts` 退役 invoke 路径；四个流式提交测试文件迁移到 admission+replay 协议 | ✅ | Core/Gateway/Desktop 套件全绿；全仓 grep 无运行时残留 |
| Interactions 补齐 admission 面：接收可选 `permission_mode`/`application_mode`（原 invoke-stream 字段）——否则 unattended（auto/full 权限策略）迁移后会被降级为 default 而误入 awaiting_user | ✅ | `UnattendedLaneEndToEndTests` 4/4 |
| 写操作/重启恢复 E2E（e2e-local-loop 步骤 12-14）：真实 TinadecTools `write_file` 动作 → awaiting_user → 审批决策（`POST /approvals/{id}/decision`）→ resume 执行 → completed；断言文件存在且内容一致（恰好一次副作用）、同 idempotency_key 重放返回同一动作；运行中 kill Core（executing 态）→ 重启 → 引擎 lease 续跑 → completed，流重放恰好一个 done、消息投影无重复 | ✅ | 19/19 PASS（acceptance.json via=gateway） |
| Desktop 收口：HomeController 活动运行过滤改用 12 态词表口径（对齐 Core CountActiveRunsAsync），删除自造 running/ready/pending/queued 词表；`generated/schema.d.ts` 随外部快照再生成（invoke-stream 从生成类型中消失） | ✅ | Desktop 测试 24/24 |

v2.3 关键教训：

7. **"启动孤儿扫描"与"引擎 lease 续跑"必须按 checkpoint 存在性分工**——否则重启恢复的两个规则集互相竞争，可续跑的运行被误判 failed（这正是 v1/§1.2 记录的"规则不统一"的实际形态）。
8. **退役一条协议线之前，先盘点它的全部调用方与字段面**——interactions 缺 `permission_mode` 会让 unattended 策略静默降级；这类缺口只在迁移真实消费者时暴露。

### v2.4（2026-09-06 第四批：智能体配置体验改造）

用户反馈：设置面板杂乱、"给智能体配置模型就报错"、智能体显示代号而非名字、工具配置无从下手。调研证实三类根因（见 §11 v2.4 前的调研结论）：① 配模型走 draft/publish 两次写 + If-Match，且 pack 管理智能体直接 409 `managed_resource_read_only`；② 模型下拉数据加载失败被静默吞掉；③ 编排投影无 display_name（assignments 恒空）。

| 工作项 | 结果 | 验证 |
|---|---|---|
| A. 运行时绑定机制：新表 `agent_runtime_bindings`（SQLite/PostgreSQL 迁移 `202609060002_AgentRuntimeBindings`）+ `PUT /api/v1/agents/{id}/runtime-binding`（inherit/fixed 两态，managed 智能体可写，校验 provider 存在/启用）；`GET /api/v1/agents` 投影新增 `model_binding` | ✅ | live 实测：对 pack 管理的 `meeting` 智能体设 fixed 绑定成功（原必 409） |
| A. 冻结链接入：`FormalModeResolver.ResolveRosterAsync` 中 user_binding > mode_node_override > agent_version.model_strategy；`tool_scope_override` 覆盖 agent 工具范围 | ✅ | 构建通过；roster 解析单测待补（登记） |
| B. 显示名全链路：`orchestration` 投影的 `nodes`/`agent_instances` 增加 `agent_display_name`，`assignments` 从实例数据填充（不再恒空）；前端卡片标题改用 `agent.name || 类型文案`，GUID 从卡片面移除 | ✅ | 编译 + 套件绿 |
| C. 错误可见化：ComposerBar 渲染 `invokeError`（此前写而不显）；HomeController 移除恒空 stub `GET /model-settings` 调用；头部模型名改由 readiness receipt 供给 | ✅ | Desktop 346 通过 |
| D. 模型服务页骨架：模型行新增"设为默认模型"按钮（后台写 chat 路由，隐藏路由概念）；i18n zh/en 双语补齐 | ✅ | i18n parity 测试绿 |
| 事故与修复：v2.4 前的一次 e2e 运行因数据库隔离缺陷把 fixture provider 写入用户开发库并改绑 chat 路由 → 用户配了模型仍无法对话。已改绑回用户模型（真实探针 1.3s 通过）、删除残留 provider；e2e 隔离已修复（命令行参数临时库），防复发 | ✅ | live readiness=ready，model 四层全绿 |

v2.4 关键教训：

9. **e2e 必须先验证数据库隔离再跑**——一次污染抵得上几十个绿色测试的信任。
10. **配置 UI 的报错大多不是 bug 而是合约设计**（managed 只读 + draft/publish + If-Match），修交互必须同时修合约（绑定记录与定义编辑分离）。

### v2.5（2026-09-06 第五批：模式选择器重做，OpenCode 式选择逻辑）

用户反馈：对话框左下角模式下拉"默认选择有时指向一个不存在的模式""完全不可用，还会报错"。排查证实三类根因：① **类型混淆**——前端把 AgentMode.id 当 mode_version_id 提交，Core 预校验 400/500；② **draft 混入可选列表**——服务端把智能体中心画布的 draft 行一并下发，前端不过滤；③ **裸 500**——会话绑定的 mode_version 被 archive 后，提交交互直接 500 而非可恢复错误。

| 工作项 | 结果 | 验证 |
|---|---|---|
| A. Core ListModes 投影：每行携带 `latest_published_mode_version_id` + `application_mode`（由 conversation.* slug 推导）；默认返回全量行（pack 花名册合约依赖完整清单，含 archived bootstrap 行），`?status=published` 显式过滤 | ✅ | live：8 行全量（7 published + 1 draft），published 全带版本 id |
| A. Core 交互边界：会话绑定的 mode_version 已不可用时 409 `mode_unavailable`（人话文案），不再裸 500；Gateway errorMapper 收编该码 | ✅ | 单测覆盖 |
| B. Desktop ModeSelector 重做（OpenCode 式）：跟随默认 + 六词枚举 + 拓扑版本三分组；客户端 published 过滤（`status==='published'` 且有版本 id）；`selectionStale` 自愈（选中的版本消失 → 触发器显 ⚠ 并自动回退跟随默认）；打开列表时刷新；选中前验真（列表外项不生效） | ✅ | ComposerBar 10/10；live 每模式可选 |
| C. 测试收口：AgentModeListTests（published 过滤 + 版本 id 必在 + 随机 mode_version_id 提交 400）、Gateway 404 幽灵路由测试改为 runtime-binding 真代理断言 | ✅ | Core 223/223、Desktop 347/347、Gateway 44/44 |
| 验证 | ✅ | 重启 Core+Gateway；readiness 全绿；真实探针 4.1s 通过（模型端点可用） |

v2.5 关键教训：

11. **服务端默认过滤要对照既有合约**——ListModes"默认只返回 published"一改就把 OfficePack 花名册测试打红（14→7）：pack 花名册依赖全量行，过滤职责归客户端。改共享端点的默认行为前先问"谁依赖全量"。
12. **前端下拉提交的 id 必须来自它声称的实体**——AgentMode.id ≠ ModeVersion.id，类型混淆靠服务端投影字段（latest_published_mode_version_id）根治，而非客户端拼凑。

v2.5 之后剩余（登记）：

- AgentEditorDrawer 组件化重构（v2.4 登记，未做）。
- 工具覆盖 UI（绑定端点已支持 `tool_scope` 覆盖，前端开关矩阵待做）。
- 模型服务页 CLI/ACP tab 的两段式改造与"开发者详情"折叠区收口。
- resolve roster 的 user_binding 优先级单测补齐。

v2.3 之后剩余（全部为尾部清理，非闭环阻塞项）：

- §6.3-6 api.ts 剩余手写 DTO 按领域迁移 generated 类型（run/approval/readiness 高危域已可用 generated，其余按需推进）。
- §5.3-5/6 dev readiness 等待、`docs/startup.md` 对齐、`e2e:real` 脚本成文。
- §3.3-7 业务模块字符串状态比较清理；§4.3-3 catch-up boundary 标记。
