# APP-HOME-108 平面对话流 v2：P0–P7 实施与验收报告

日期：2026-10-13 · 分支：`feat/flat-conversation-v2`（基于 `ab42b02`）· 环境：Linux 开发容器（2 核 / 5 GB，.NET 10.0.401、Bun 1.4.3、Node 24.21）。计划见 [`plans/claude/2026-10-13-flat-conversation-v2.md`](../plans/claude/2026-10-13-flat-conversation-v2.md)，任务见 `docs/development-program/02-modules/app/home/TODO.md#app-home-108`。

## 结论

平面模式对话流按计划 P0–P7 实施，分支 `feat/flat-conversation-v2`：回合结构（用户消息 → 工作区 → 不折叠的回答 → 脚注）、审批常显、子智能体卡与右栏面板、事件溯源数据层、有界结果预览端点。旧的 `MessageList/MessageItem/LiveTurnBlock` 已删除；空间模式按约定未迁移。

首版报告曾写「全部落地」，不准确：逐条对照计划后有 6 项细节当时没有做——失败/停止回合的「重试」、跨天日期分隔、跳底按钮的未读回合数、会话切换正文骨架（200/500）、`groupText`（≤150 字的中间回答压成两行）、`.shimmer-once`（只在刚完成时扫一次）。这 6 项已在后续提交补齐，各带测试，见「补齐」一节。

与计划验证方式的偏差：计划要求 `npm run dev` 实机截图并用 `vue-mcp` 查组件树。本机没有 Electron 二进制和显示，截图改为无头 Chromium 对 Vite 渲染端（无 Core/Gateway）；「折叠后无活动 DOM」改由 DOM 测试断言（reka 保留隐藏的外层元素、卸载其子节点），没有用 `vue-mcp`。Electron 实机、真实模型 Team 链路与 Windows/macOS 仍待验收（见末节）。

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

## P2 改了什么

- **原语移植**：`apps/desktop/src/components/ui/{marker,message,bubble,attachment,spinner,message-scroller,collapsible}/` 移植自 `unovue/shadcn-vue` `registry/bases/reka/ui`（`b251d9f`，MIT，已记入 `NOTICE`）。组件文件保留上游 `cn-*` 类名以便对照；样式统一在 `components/ui/conversation-primitives.css`，用本项目的 surface/text/accent token 重写（不依赖 nova 调色板与 `dark:` 变体）：只有附件是卡片（`--surface-section` + `--border-card` + 10px，无阴影），用户气泡为 tinted 软底无边框，正文 13px / 活动 12px / meta 11px。
- **改动点**：`Spinner` 直接用 lucide `Loader2Icon`（`role=status`，减弱动效改脉冲）；`AttachmentAction` 与 `MessageScrollerButton` 不再依赖 registry Button（本项目无 `icon-xs/icon-sm`），改为原生按钮；修正上游 `AttachmentTrigger.vue` 未导入 `Primitive`。`reka-ui@2.11.0` 显式加入 Desktop 依赖（此前只是被 TinadecUI 提升到根 `node_modules`）。旧 `ui/collapsible.vue` 与 `UiCollapsible` 导出保留给空间模式。
- **scroller 测量**：上游 `measureContentHeight` 每次提交测所有行，`capturePrependAnchor` 每次滚动与内容变化从第一行逐行测到可见行（未订阅可见性时没有 IntersectionObserver）。改为：内容高度只测最后一行；当前锚点只测可见锚点（可见区之前的锚点无需读布局）；首个可见行二分查找。stubbed 布局实测一次滚动+追加：上游 200 行 1600 次、1000 行 1997 次行测量；本分支均 <50（测试在上游实现上变红）。
- **composables**：`useDelayedLoading`（150ms 才出现、至少停 400ms，恢复工作时不闪）、`useElapsed`（共享 1s ticker，无订阅即停）、`useSmoothText`（约 90 字/秒；落后超过 0.3s 时按当时积压定速追平，结束时 0.12s 冲刷；历史首帧完整；只控节奏，不碰 MarkdownRender）、`useToolPreview`（展开时惰性取、仅终态、按 execution 缓存与并发合并、失败可重试）、`useAgentDirectory`（读一次 `/api/v1/agents`，失败退回 slug、下次重试，`handle#n` 保留序号）。
- **shimmer**：`.chat-shimmer` 泛化为基于 `currentColor` 的 `.shimmer`（2s）与单次收尾的 `.shimmer-once`；`.chat-shimmer` 保留原配色与 2.4s 作为别名，空间模式不变；减弱动效下全部静止为实色。

## P3 改了什么

- **数据接线**：`HomeController` 的 run 列表保留 `trigger_message_id / created_at / updated_at`（此前只留 `{id,status}`，配对只能靠猜）；手写镜像 `OrchestrationRunDto` 补 `trigger_message_id`（Gateway `runMapper` 早已透传）。`useConversationFeed` 在一个与窗口同寿命的独立 effect scope 中挂载并导出为 `homeController.conversationFeed`。`turns.ts`：刚准入、只知道 id 的 run 归入最新回合，而不是因时间缺失被排到最前。
- **组件骨架**（`components/conversation/`）：`ConversationView`（P2 scroller，每回合一行，用户消息为锚点，跳底按钮）、`ConversationTurn`、`UserMessage`（tinted 气泡、悬停复制/编辑/时间、附件改用 attachment 原语且保持网关按 id 路由、svg 不作缩略图、不出存储路径）、`TurnActivity`（骨架：组/工具句子/推理/等待/计划/子智能体/marker 各一行）、`ApprovalCard`（流外常显、显示 Core evidence 的命令/目录/目标与请求者；对 Core 已列出但 feed 尚未见到的待审批也照样显示）、`AssistantAnswer`（永不折叠，流式经 `useSmoothText`，脚注复制·时间·工作时长·工具次数）、`TurnMarker`（失败原因写成文字）。共享样式 `conversation.css`。
- **监督裁决**：从 `TurnTimeline` 原样抽成 `SupervisionGate`，空间模式与平面 feed 共用；`continue→resume`、`correct` 走 insert 不变。升级原因与选项仍由 `useAgentActivity` 提供（空间模式仍在用）。
- **接入**：`ChatPanel` 新增可选 `conversation` 输入，有则渲染 `ConversationView`；`ChatCard` 从控制器提供。Debug Studio 画廊未提供时仍走 `MessageList`，P7 迁移画廊后删除回退。`MessageScrollerViewport` 的可访问名改为参数（上游硬编码英文 "Messages"）。
- **i18n**：新增 `conversation.*` 中英文案；`components/conversation/**` 纳入 `i18nParity`；动态拼接的 marker 类型与子智能体状态另有双语存在性断言。

## P4 改了什么

- **展开模型**：`useAutoDisclosure`——`open = 用户已开合 ? 用户选择 : live`；reka collapsible 收起时卸载内容（保留隐藏的外层元素）。
- **按 run 的工作区** `TurnWork`：运行中展开，顶部状态行（spinner + 当前步骤 shimmer + 计时，`role=status`，回合唯一的 polite 播报）；落定后收成一行摘要「已工作 1 分 20 秒 · 2 个文件 · 编辑 1 处 · 运行 1 条命令」；审批与失败原因不在折叠内。
- **推理块** `ReasoningBlock`：完整推理而非尾部 160 字；粗体行成小节，最新闭合标题作状态行，半截标题压到闭合；思考中展开、结束收成「思考了 12 秒」；超长限高渐隐 + 展开全部。
- **探索组** `ActivityGroup`：连续读/搜/列/思考/等待成一行「已探索 2 个文件 · 1 次搜索 · 1 个目录」，活回合末尾组展开；组内 Codex 式紧凑行，连续读取合并为「读取 a.ts, b.ts」。
- **工具行** `ToolRow` + `ToolPreview`：一行句子 + 运行计时 / ✓ 时长 / ✗ 原因（退出码或错误类别）；展开显示脱敏参数、shell 实时尾部（来自事件，对话流内不挂 xterm）、改动文件、B2 预览（展开且终态才取，按 execution 缓存）；shell 可「在终端面板打开」，复用 `openAgentTerminal` 并以本行尾部作初始回放。reducer 新增 `terminalSessionId`。
- **审批卡** `ApprovalCard`：批准、本次运行都批准（`scope:'run'`）、拒绝、改为这样做（拒绝 + `insert` 进该 run）、shell 的「始终允许以 X 开头的命令」（`command_prefix` 规则后批准本次）；⌘/Ctrl+Enter 批准、⌘/Ctrl+Backspace 拒绝、Esc 关闭改写框；按钮进行中用延迟 spinner；保留 `ApprovalGateStatus`。决定直接经 `homeController`，不再经 `ChatPanel` 事件。
- **待办** `AgentTodos`：6 条后折叠，与上一版相比新增/改写的步骤高亮并显示说明。

## P5 改了什么

- **对话流内** `SubAgentCard`：紧凑卡（卡片样式之一）显示 `handle · 显示名`、状态徽标（等待/进行中/待审批/完成/失败/受阻/已取消，完成 .85、取消 .6 透明度）、L 形连接线任务标题、进行中时最新一步（shimmer）；点击就地展开它自己的分组活动（同一套 `TurnActivity`）与 `worker.completed` 摘要（Markdown）；⤢「在面板打开」。它的审批仍由回合提出（不在卡内）。
- `AgentGroup`：连续派发成组，≥2 个运行中时组头「2 个智能体工作中…」。
- **右栏** `SubAgentsCard`（TinadecUI `cards/index.ts` 注册 `subAgents`：singleton、可移动、可关闭、本轮不可分离；`featureCatalog.ts` 加入口）内含 Desktop 的 `SubAgentsPanel`：本会话所有 run 的子智能体名册（运行中在前，listbox 键盘可选）+ 选中者详情（同一 `SubAgentCard`，展开、无 ⤢），读同一个 `conversationFeed`。选择保存在 `lib/conversation/selection.ts`，⤢ 写入后 `openCard subAgents`；未选时默认第一个。
- 顺带：`HomeGitPlacement.test.ts` 的网格卡片数改为相对目录长度（Git 由状态卡替代），不再写死；目录测试改为 10 项。

## P6 改了什么

- **发送框附件**：chip 改用 attachment 原语五态。`pendingAttachments` 的 `uploading/ready/failed` 经 `attachmentDisplayState` 映射为 `uploading/done/error`；选择文件后若还在为草稿会话建会话，先以 `idle` 显示所选文件。图片上传中显示本地预览（`URL.createObjectURL`，移除/发送/换会话时逐一 `revokeObjectURL`），未完成时变暗；svg 与非图片从不预览。失败原因写在描述里（文字，不只靠颜色）。文件名经 `fileNameParts` 截断主干、保留扩展名，转录中的附件卡同样处理。chip 内 spinner 为 `decorative`（条带本身是 polite live region）。原有附件安全测试与 Send 规则不变。
- **`AgentTodosStatus`**：输入框上方胶囊「计划 1/3 · 编写测试」，取最新活动 run 自身的计划（无则取子智能体的），点击原位展开清单，run 落定即消失。
- **旧路径性能**（空间模式仍用的 `useAgentActivity`）：工具时间线读取按 run 合并——首个 tool/approval/task 事件在 300ms 后读一次，窗口内后续事件搭车，持续事件流不会被无限推迟；初始重放期间不发任何请求，只记下有工具事件的 run，Core 重放后的 heartbeat（无则 2s 兜底）到达后每个 run 读一次；切会话/卸载清掉待发读取。测试：重放 40 个工具事件横跨 2 个 run → 重放期 0 次、之后 2 次（旧路径为每事件 1 次，即 40 次）；关掉重放闸门后对应两条测试变红。显式 `refreshToolExecutions()` 仍立即读取。

## P7 改了什么

- **i18n**：新组件中的 spinner 一律 `decorative`（旁边总有说明文字，默认英文 `aria-label="Loading"` 会被重复且错语种播报）；等待名单分隔符 `conversation.listSeparator`（中「、」英「, 」）；终端标签标题本地化；`components/conversation/**` 在 `i18nParity`，动态拼接的 marker/状态另有存在性断言。
- **减弱动效**：跳底按钮在 `prefers-reduced-motion` 下瞬时滚动且关闭过渡；折叠、chevron、shimmer、光标、悬停揭示均静止。无头 Chromium 计算样式实测：常规 `shimmer-sweep` / `spin`，减弱动效下 `none` / `pulse` / 折叠 `none`。
- **窄宽**：`chat-narrow`（<560px）/`chat-ultra`（<400px）下内容边距收紧、用户气泡 92%/100%、审批事实单列、子智能体最新步骤隐藏、计划胶囊边距收紧；380px 实拍确认类名生效与换行。
- **Debug Studio**：「对话消息流」改为 fixtures 场景选择器 + 真实 `ConversationView`（与数据层测试同一批事件）；「聊天面板」以 fixture feed 渲染真实 `ChatPanel`。`ChatPanel` 的 `conversation` 输入变为必填，删除 `MessageList` 回退与单回合活动/流式/审批事件属性，`ChatCard` 同步。
- **删除**：`MessageList.vue`、`MessageItem.vue`、`chat/LiveTurnBlock.vue` 与 `MessageList.test.ts`（行为已于 P3 迁入 `ConversationView.test.ts`）；开发工程中指向它们的链接与初始清点证据改指继任组件。
- **截图矩阵发现并修复的两个缺陷**：
  1. shimmer 文本在动画大部分时间不可见——文字本身透明、由 200% 宽渐变着色，旧关键帧 `-200% → 200%` 把渐变移出文字框；改为 `100% → 0%`。空间模式沿用的 `.chat-shimmer` 同样受影响，一并修复。新增 `conversationCss.test.ts` 钉住关键帧位置范围。
  2. 同一回合摘要写「已工作 3 秒」、回答脚注写「已工作 5 秒」——脚注按用户消息时间计；改为与摘要同一跨度（首尾活动），新增断言。
- **构建**：`vite build` 在 `NODE_OPTIONS=--max-old-space-size=4096` 下 57 秒完成（退出 0）。

## 补齐（首版遗漏的计划细节）

- **重试**：最后一个回合失败或被停止时，在失败/停止行下给出「重试」。它沿用编辑重发：`edit` 事件带原消息 id 与原文，`editAndResend` 截断到该消息并把原文放回输入框，不自动重发。只给最后一个回合（更早的回合重试会截掉其后的历史），也不给 Core 尚未确认的 `pending-` 乐观行。
- **跨天日期分隔**：相邻两个回合跨越本地日期时，后一回合上方画一条 `Marker variant="separator"` 日期线（`dayLabel`：今天/昨天/星期X/月日，跨年带年份），第一个回合上方不画；放在回合行内，滚动锚点仍是该回合。
- **未读回合数**：新组件 `JumpToLatest` 在 scroller provider 内读 `scrollable.end`，只计读者上翻期间追加在下方的回合；首次加载的历史、换成另一会话的列表、自己发送的 `pending-` 回合、乐观行换成 Core id 都不计；回到底部清零。按钮有数时变成胶囊「↓ 2 条新消息」，可访问名同步。
- **会话切换骨架**：`HomeController.transcriptLoading`——选中会话且本地无消息时置真，只由最新一次读取结束时清除（旧读取晚到不清）。`ChatPanel` 加载期间不回欢迎页；`ConversationView` 用 `useDelayedLoading(200/500)` 显示回合形状的骨架（右侧气泡 + 两行），覆盖在视口上（scroller 内容区占满视口高度，放在其后会被挤出可见区）。已知取舍：一个确实为空的既有会话，加载期间先停在对话布局，加载完再升回欢迎页。
- **groupText**：同一回合有多条回答时，非最后一条且 ≤150 字（按字符、去首尾空白）的回答显示为两行截断的弱化小字，悬停 `title` 看全文，不带脚注；最后一条永不压缩（`isCompactAnswer`）。
- **shimmer-once**：`useJustSettled` 只在 live → 完成的那一刻为真；推理块的「思考了 N 秒」与工作区摘要在本页看着完成时扫一次，挂载时已完成的历史不扫，减弱动效下不动。
- **图片与流程图**（计划未列，回答沿用既有 `MarkdownRender`/`MarkdownDiagram`）：新增场景 `rich-answer` 实拍后发现两处问题并修复——① `MarkdownDiagram` 的 `diagramCounter` 声明在每个实例的 setup 里，页面上每张图都以 `md-diagram-1` 渲染，mermaid 按 id 限定样式与临时节点，同一回答里两张图会画到一起（PR #42 起即存在，平面流里一条回答带两张图就会触发）；改为模块级计数，mermaid 的加载、全局配置与 render 串行共享。② groupText 会把只有一张短流程图的中间回答压成两行小字；现在只压纯文字，代码块、图片、表格、公式不压。边标签底色改用卡片表面色（base 主题原为饱和色块）。Markdown 图片按列宽缩放、无溢出。窄列下流程图只缩到原始宽度的 80%（标签约 9.6px），再窄就横向滚动并从左边缘开始，不再缩成看不清的小字（`harness/narrow.mjs`：1169 不滚动，520/380 滚动）。
- Debug Studio「对话消息流」新增场景 `next-day-stopped`（昨天的回合两次作答、今天的回合被停止）与「加载中」开关。

## 视觉验收（无头 Chromium）

只启动 Vite 渲染端（无 Electron/Core/Gateway），以桩 `window.tinadec.getAppConfig` 打开 Debug Studio。矩阵：7 个场景 × 1169/800/520/380 × 暗/亮 × 毛玻璃/不透明（关键场景），另含已落定回合展开态、`ChatPanel` 1169/380。截图、harness 脚本与计算样式见 [`evidence/2026-10-13-flat-conversation/`](../evidence/2026-10-13-flat-conversation/README.md)。harness 仅为环境隐藏了无后端时出现的通知岛与宿主不一致横幅，并以「先宽后窄」到达窄宽（Debug Studio 自身标签栏在 ~800px 下隐藏「预览」）。

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
| P2 原语组件测试（Attachment 五态、Spinner 延迟/最短显示、Marker、Collapsible、Message/Bubble） | 12/12 |
| P2 scroller 测试（跟随、上翻不抢、锚定、200/1000 行测量上限） | 5/5；替换回上游 `useMessageScroller.ts` 时测量用例 2 例变红 |
| P2 composable 测试 | 10/10 |
| Desktop 全量 vitest（P2 后） | 1378 通过 / 14 skip（既有 `NotificationIslandHost`），151 文件；`vue-tsc` 0 错误；Tailwind 编译 `styles.css` 产出新规则 |
| P3 `ConversationView.test.ts`（迁移 MessageList 行为 + 子智能体审批提升、失败原因、流式、动态文案） | 17/17 |
| Desktop 全量 vitest（P3 后） | 1396 通过 / 14 skip，152 文件；`vue-tsc` 0 错误（含 TinadecUI 源） |
| TinadecUI vitest | 187/187 |
| P4 `ConversationActivity.test.ts`（运行中展开/落定收起/手动后不再自动、读搜折叠写入独占、审批在折叠外含子智能体、推理块、预览惰性且仅终态、失败原因、审批四种动作与快捷键、待办）+ reasoning/disclosure 单测 | 全部通过；conversation 相关 85/85 |
| Desktop 全量 vitest（P4 后） | 1413 通过 / 14 skip，154 文件；`vue-tsc` 0 错误；TinadecUI 187/187；Tailwind 编译新规则 |
| P5 `SubAgents.test.ts`（并行两卡一组、组头、就地展开活动与摘要、审批不在卡内、⤢ 选中并打开卡片、面板显示同一选中项、运行中在前、默认首个、空态） | 6/6 |
| Desktop 全量 vitest（P5 后） | 1419 通过 / 14 skip，155 文件；`vue-tsc` 0 错误；TinadecUI 187/187 |
| P6 定向（`pendingAttachments` 45、`ComposerBar` 77、`useAgentActivity` 22、`AgentTodosStatus` 3） | 全部通过 |
| Desktop 全量 vitest（P6 后） | 1440 通过 / 14 skip，156 文件；`vue-tsc` 0 错误；TinadecUI 187/187 |
| Desktop 全量 vitest（P7 后，删除旧组件测试 15 例、新增 5 例） | 1430 通过 / 14 skip，156 文件；`vue-tsc` 0 错误；TinadecUI 187/187 |
| `vite build`（4 GB 堆） | 57 秒，退出 0 |
| 补齐：`ConversationView.test.ts`（重试只在最后的失败/停止回合且发出原文、日期线位置、中间回答压缩、骨架 200ms 出现/真实回合立即替换/空会话保持 500ms）、`JumpToLatest.test.ts`（上翻计数、贴底不计、历史/换会话/自己发送/换 id 不计、回底清零）、`ChatPanel.test.ts`（加载中不回欢迎页）、`HomeController.test.ts`（旧读取晚到不清加载态；去掉该判断时用例变红）、`grouping.test.ts`（`isCompactAnswer`）、`useJustSettled.test.ts`、`ConversationActivity.test.ts`（完成时 `shimmer-once`、历史无） | 全部通过 |
| Desktop 全量 vitest（补齐后） | 1458 例：1444 通过 / 14 skip，159 文件（158 通过、1 skip）；`vue-tsc` 0 错误（含 TinadecUI 源）；TinadecUI 187/187 |
| 补齐截图（`next-day-stopped`、「加载中」，1169/380/300） | 日期线 1 条、压缩回答 2 行截断带省略号、重试 1 个、无横向溢出、骨架可见、0 页面错误 |

## 既有失败与边界

- `ToolChainEndpointTests.WorkerWriteFile_RequestsApproval_ApprovedThenExecuted_RunCompletes` 等在未改动的 `ab42b02` 上同样失败（`git worktree` 基线实测），`main` 的 posix-core CI 也红。新预览端点给出原因：工具子进程报 `Only paths inside the authorized source folders () may be written`——Linux 上 `ToolExecutionContext.WorkspaceRoots` 为空、可写根又不是工作区。属多文件夹工作区链路（APP-HOME-107），本轮不修；P0 的新断言另以驻留审批时刻的独立用例覆盖。
- `ChatPanel.test.ts` 运行时打印的 `ECONNREFUSED 127.0.0.1:48730` 来自未打桩的真实 `ComposerBar`，改动前的提交上同样出现 16 次，测试本身通过。
- **待验收**：Electron 实机窗口（本机无 Electron 二进制与显示）、真实模型下 Team 模式派发子智能体 + shell 审批的完整链路、Windows/macOS 渲染与字体。建议在本地 `npm run dev` 后打开 Debug Studio「对话消息流」逐场景过一遍，再跑一次真实会话。
- 会话切换骨架的取舍：确实为空的既有会话在加载期间先停在对话布局（显示骨架），加载完成后再升回欢迎页；有历史的会话不再闪一次欢迎页。
- 未读回合数在 happy-dom 中无法测量滚动，组件测试对 `scrollable` 打桩；真实滚动下的计数只在实机验收时能看到。
- 未在本轮：空间模式迁移、消息分页、编辑的 +/− 行数与改动审阅卡、HTML widget、SSE 重放屏障协议（计划「不在本轮」）。
