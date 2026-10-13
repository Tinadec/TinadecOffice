# APP-HOME-108 平面对话流 v2：P0 后端增量与 P1 数据层

日期：2026-10-13 · 分支：`feat/flat-conversation-v2`（基于 `ab42b02`）· 环境：Linux 开发容器（2 核 / 5 GB，.NET 10.0.401、Bun 1.4.3、Node 24.21）。计划见 [`plans/claude/2026-10-13-flat-conversation-v2.md`](../plans/claude/2026-10-13-flat-conversation-v2.md)，任务见 `docs/development-program/02-modules/app/home/TODO.md#app-home-108`。

## P0 改了什么

**B1 工具行句子数据**

- `ToolDispatcher`：`tool.execution.requested` 现在在执行记录落库后、`AuthorizeAsync` 之前发出。原先被 PDP 驻留、拦截或拒绝的调用会提前返回，事件日志里根本没有 requested 行；基线实测该写入调用的时间线 `event_types` 首项是 `approval.requested`，修复后首项是 `tool.execution.requested`。载荷新增 `arguments_summary / resource_path / command / cwd / query`，全部来自 `ApprovalEvidenceProjector`（与审批同一份脱敏投影；`query` 由新增的 `QueryOf` 从已脱敏截断的 evidence 中读 `pattern/query/q`）。
- `BuildToolExecutionTimeline` 透传上述字段及 `task_id / agent_instance_id / error_category`（失败事件实际写的是 `category`，两种都读）。
- `GET /api/v1/sessions/{id}/tool-executions`：`run_id`（兼容 `runId`）经新的 `StorageLifecycleService.ReplayRunEventsAsync` 在事件索引查询里过滤，不再物化整会话日志；`limit` 取最新 N 条、仍按序，最大 1000；非法值 400。Gateway 原先把 `run_id` 改名为 Core 从不读取的 `runId` 转发，已改正。
- 影响面：Desktop `HomeController` 与 `DetachedPanelPage` 一直传 `limit: 12`，但 Core 过去忽略它；为不悄悄截断工具统计面板的总数，这两处改为不传 limit，保持既有实际行为。

**B2 有界结果预览**

- 新纯函数 `Abstractions/Ports/ToolResultPreviewProjector.cs` + `Contracts/Dtos/ToolExecutionPreviewDtos.cs` + `GET /api/v1/tool-executions/{executionId:guid}/preview?max_bytes=`。
- 预览类型：`file_lines`（≤60 行×400 字，行记录成员大小写无关）、`search_matches`（≤40 行）、`directory`（≤80 项）、`command_output`（stdout/stderr 尾部各 ≤8 KiB + exit_code/timed_out）、`text`（git_diff 拼接，≤8 KiB，`language: diff`）、`json`（深度 ≤6、数组 ≤20、字符串 ≤400）、`withheld`。工具自报 `success:false` 时按 json 原样显示其错误。
- 安全审查清单逐项落点：
  - 只读、作用域：经 `IToolExecutionCoordinator.FindAsync`（tenant+workspace 过滤）；把执行行改到别的 workspace 后返回与不存在完全相同的 404 problem（`tool_execution_not_found`，detail 相同）——`ToolExecutionPreviewEndpointTests.ExecutionOfAnotherWorkspace_AnswersTheSame404AsAMissingOne`。
  - 敏感键：与审批共用 `ApprovalEvidenceProjector.IsSecretKey`，json 预览任意深度直接丢弃键。
  - 敏感路径（`.env*`、`*.pem/key/pfx/p12/kdbx`、`id_rsa*/id_ed25519*`、`.npmrc/.pypirc`、`credentials*`）：读取返回 `withheld`；搜索结果逐行丢弃；diff 任一头部命中则整体 `withheld`。
  - `max_bytes` 夹在 1–64 KiB、默认 16 KiB；整份响应共用一个 UTF-8 字节预算，截断不拆代理对。
  - 不返回参数原文，只返回 evidence；只出 `safe_error_message`；未终态 `preview: null`（强制写出，因全局选项会省略 null）。
  - 不缓存：Core 与 Gateway 成功与失败响应都 `cache-control: no-store`，Gateway 纯透传。
- Gateway 薄代理 + `externalDtoOpenApi.ts` 文档 DTO；`openapi.core.json`（+273 行，纯新增）、`openapi.external.json`（+229 行，纯新增）、`apps/desktop/src/generated/schema.d.ts`（+63 行）同步；`check:drift` 退出 0。
- 新预览端点第一次使用就定位了下文那个既有失败的原因，并被加进该用例的超时诊断。

## P1 改了什么

`apps/desktop/src/lib/conversation/`：

- `reducer.ts`：只吃会话 SSE；每 run 可变记录 + `version`；按 run 内 `seq+type` 去重；工具状态复刻 Core 折叠（含 PDP 驻留按 `permission_request_id` 关联、可重试失败保持运行、拒绝失败）；shell 输出 ≤200 行环形尾部并跨分块拼行；`workspace.changes_observed` 改动路径；子智能体目录由 `task.dispatched + worker.assigned + worker.*` 组成，`dispatch_id` 被 Core 换成新 Guid 时按 `task_key` 认领；`solo_master` 视为回合本身；计划原位替换并保留上一版；中断请求与中断按 `patch_id` 合并为一条 marker。载荷键依据 Core 发射点逐一核对。
- `grouping.ts`：连续 quiet（推理、未失败的读/列/搜、等待）≥2 成组并计 files/searches/lists/thoughts；失败的读是 loud；待审批工具提出流外（含子智能体的，注明请求者）；连续派发合成 agent 组并递归；监督只显示 revise/escalate；活回合最后一组 `exploring`。
- `turns.ts`：按 `trigger_message_id` 配对、回答按 `run_id` 归位、无触发的 run 归到其之前最近开始的回合、阶段 `sending/running/awaiting/settled/failed/cancelled`。
- `toolSentence.ts`（复用 `toolKindOf/toolIconOf`，只出 i18n key；`conversation.tool.*` 中英已加入 locale）、`format.ts`（时长、钟点、跨天标签）、`fixtures/`（七个场景，按 Core 真实载荷键构造，非录制；供测试与后续 Debug Studio 共用）。
- `composables/useConversationFeed.ts`：复用 `sessionEventBus` 同一连接；初始重放期间不发布，Core 重放后写出的 `event: heartbeat` 到达时一次发布（2 s 兜底）；切会话重置并丢弃旧批次。`api.connectEvents` 与 `sessionEventBus` 新增 `onHeartbeat`（此前该命名帧没有监听器，被浏览器直接丢弃）。尚未挂到 `HomeController`，留待 P3 有读者时挂载。

## 验证（本机实测）

| 范围 | 结果 |
| --- | --- |
| `ToolResultPreviewProjectorTests` + `ApprovalEvidenceProjectorTests`（含 `QueryOf`） | 54/54 |
| `ToolExecutionPreviewEndpointTests` | 4/4 |
| `ToolChainEndpointTests.ParkedWriteCall_IsAnnouncedWithSentenceData_AndTheTimelineFiltersByRun`（真实 TinadecTools 子进程） | 通过；该用例在基线上必红（requested 行不存在） |
| `CoreOpenApiSnapshotTests` | 2/2 |
| Core Architecture / Governance | 18/18、92/92 |
| Core AgentFramework 全量 | 659/672，4 失败 + 9 skip；失败均为 `Acp*`、`FormalModeSnapshotTests.ResolveRoster_*`，基线同样失败，`main` 的 posix-core CI 亦红；`AcpProtocolTests.Cancel_*` 两次运行结果不同（不稳定） |
| Core Api 全量（加 `QueryOf` 之前的构建） | 849 例，836 通过 / 13 失败。其中 11 例与 `main` 的 posix-core CI 失败同名（`ApprovalFlowTests.UserToolAction_*`×2、`UnattendedEndToEndTests.*GitCommit*`×3、`ToolChainEndpointTests.WorkerWriteFile_*`/`GitSteward_*`、`AcpHostRegistrationTests`、`CliDiscoveryTests`、`FullDuplexEndpointTests.InvokeStream_SendsTheSkillIndex*`、`LifecycleManagementApiTests.Session_CanBeCreatedWithoutProject*`）；`TinadecToolsProcessTests.CallAsync_ShellTool_ResultUsesSnakeCaseWireKeys` 在基线上同样失败；`ToolChainEndpointTests.FailedToolDispatch_*` 全量中 30 s 等待超时，单独在本分支 3/3 通过（7 s / 36 s / 7 s），基线单跑亦耗时 35 s——负载下的慢路径，基线可复现，不判为回归 |
| `QueryOf` 构建后定向复跑（预览端点、驻留审批用例、快照、`DmaeaEndpointTests`） | 13/13 |
| Gateway `bun test src` | 105/105 |
| Desktop 定向（api、HomeController、useAgentActivity×2、sessionEventBus、conversation、useConversationFeed、locales） | 全部通过；`vue-tsc --noEmit` 0 错误；`check:drift` 退出 0 |
| hydrate 基准（≥2000 事件一批） | 单次发布，整例含构造 6 ms |

## 既有失败与边界

- `ToolChainEndpointTests.WorkerWriteFile_RequestsApproval_ApprovedThenExecuted_RunCompletes` 在未改动的 `ab42b02` 上同样失败（`git worktree` 基线实测），`main` 的 posix-core CI 也红。新预览端点给出原因：工具子进程报 `Only paths inside the authorized source folders () may be written`——Linux 上 `ToolExecutionContext.WorkspaceRoots` 为空、可写根又不是工作区。属多文件夹工作区链路（APP-HOME-107），本轮不修；P0 的新断言因此另以驻留审批时刻的独立用例覆盖，原用例里的新断言只在其能跑完的平台上执行。
- 未在 Windows 实机、真实模型或 Electron 中验证；UI 阶段 P2–P7 尚未开始。
