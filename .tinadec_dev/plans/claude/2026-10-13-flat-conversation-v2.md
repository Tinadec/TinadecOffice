# 平面模式对话流重构 v2（从 main 重新开始）

## Context

平面对话流现为 `ChatCard → ChatPanel → MessageList → MessageItem / LiveTurnBlock → TurnTimeline → ThinkingProcess / ToolCallCard`，问题都能指到源码：

- **思维链被丢掉**：`useAgentActivity.ts:900` 已累积 `model.output.delta(channel=reasoning)`，`ThinkingProcess.vue:95` 只显示尾部 160 字。
- **工具行读不出在干什么**：`BuildToolExecutionTimeline`（`DmaeaEndpoints.cs:744`）只从事件折叠，`summary` 恒空；脱敏参数投影 `ToolExecutionRecord.ArgumentsDigest` 只给审批用；结果从不出 Core。
- **全部活动压进一个默认收起的折叠**（`TurnTimeline.vue:18`），审批、监督各自另渲一份。
- **多智能体不可见**：`task.dispatched / worker.assigned / plan.updated / model.output(handle,task_id) / tool.execution.requested(task_id)` 都带归属，最后只成了推理里一条 `dispatch` 步骤。
- **用户消息与 run 靠猜配对**：`Run.trigger_message_id` 早已返回（`StorageEndpoints.cs:592`、Gateway `runMapper.ts`），`HomeController` 只留 `{id,status}`。
- **性能隐患**：每个 `tool.*/approval.*/step.*/task*` 事件都触发一次 `listToolExecutions`（`useAgentActivity.ts:1157`），而 Core 该端点忽略 `run_id/limit`、每次重放整个会话日志（`DmaeaEndpoints.cs:238`）；打开会话时 SSE 从 0 重放、逐事件复制数组。
- **硬编码中文**：活动标题在 composable 里拼中文，英文界面也是中文。

PR #43 已判定失败，不复用其代码；只吸取其报告记录的教训：范围失控、折叠内容全量挂载（200 思考 + 20 工具 → 3830 DOM）、逐事件复制（2000 条历史 ~7.2s）、滚动引擎每次测全部行、对话流内嵌 xterm 引发 `dimensions` 崩溃。

**目标**：回合结构——用户消息 → 工作区（思维链 + 分层工具调用 + 子智能体 + 系统事件行；运行中展开、完成收成一行摘要）→ 回答正文（永不折叠、平滑流式）→ 脚注；要人处理的审批永远可见。

## 已拍板

- 后端：**最小增量 + 有界结果预览端点**（需安全审查）。
- 子智能体：**两者都要**——对话流内紧凑卡就地展开为默认，另有「在面板打开」进右栏新卡。
- 组件底座：**移植 shadcn-vue 对话原语 + reka-ui**。
- 空间模式：**本轮只改平面**；`SpatialWorkCard/TurnTimeline/ToolCallCard/useAgentActivity` 保持原样（只做一处性能合并，见 P6）。

## 参考与取用

| 来源 | 取什么 |
|---|---|
| hextaui Thinking | 状态行（指示器 + 白话步骤 + 计时）；流式中默认展开，结束收成「思考了 12 秒」，**用户手动开合过就不再自动**；粗体行成小节标题，**半截标题压到闭合再显示**；不用光球（vibe-hub） |
| hextaui Tool Calls | 一步一行像句子；**连续读/搜/列折成「已探索 6 个文件」**；改东西/要决策/失败的独占一行带实时计时；kind→图标+动词（进行时/过去式）；审批四选 + ⌘↵/⌘⌫/Esc；needs-you 才进 live region |
| hextaui Agent Todos / Streaming / Chat Thread | 待办状态与 6 条后折叠、改写高亮 + 一句说明、输入框上方状态胶囊；平滑揭示 90 字/秒、0.3s 追平、0.12s 冲刷、历史首帧完整；新回合锚顶、阅读不抢滚动；悬停出复制/编辑/时间 |
| hextaui Spinner/useDelayedLoading/Shimmer/Motion | 150ms 才出现、至少停 400ms；tick/ring；shimmer 2s、`once` 收尾；`cubic-bezier(.23,1,.32,1)`，enter 200/exit 150/morph 300；减弱动效=第二套设计 |
| shadcn-vue `../shadcn-vue/apps/v4/registry/bases/reka/ui` | `marker`、`message`、`bubble`、`attachment`（`idle/uploading/processing/error/done`）、`spinner`、`message-scroller`（turn 锚定、跟随、`last-anchor`、未读按钮）；lucide；base-nova |
| reka-ui 2.11（仓库已装） | `CollapsibleRoot/Trigger/Content` + `--reka-collapsible-content-height` 可中途反转；隐藏即卸载 |
| Codex（本机 `../codex/codex-rs/tui`） | `Exploring/Explored` + `└ Read a, b / Search q in p / List path`（`exec_cell/render.rs:313`）；`Running/Ran` 截断 `… +N 行`、圆点按退出码着色；`Edited`；`Updated Plan`；回答后 `Worked for 2m 5s • 时间`（`history_cell/separators.rs`）；`Context compacted`、`■ Conversation interrupted` |
| Claude Desktop（本机 ion-dist i18n） | `Thought for {s}s`、`Read · {n}`、`Ran terminal`、`Edited`、`{n} agents working…`、`Message from subagent`、`Compacted conversation`、`Stopped`、`Running tools…`、工具审批卡 |
| Qoder（本机解包 `agents-window.desktop.main.js`） | 分组器默认 `groupThinking/groupToolCalls/groupReadFile/groupSearchTools/groupTodos/groupText(≤150 字 2 行)`、`autoExpandWhileExploring`、`autoCollapseOnComplete`；**每个请求最后一条回答不折叠**；纯思考组标题「Thought for Ns」；files/searches/edits/terminals 计数；连续派发成 task-group；紧凑卡（16px 头像 + 名称 + 状态徽标 + L 形连接线任务行，进行中名称 shimmer，完成 .85、取消 .6）；6 辐波浪 loader；子智能体可在独立视图打开 |
| vibe-hub `/anti-ai-flavor`、`/chat-ui` | 禁盒中盒、处处分割线、彩色左竖条、emoji 图标、蓝紫光球、到处动画、只有图标的按钮、同一动作多种叫法；发送中/生成中/完成/失败齐全，失败保留原文可重试 |

## 目标形态

```
                                   ┌ 用户气泡（tinted 软底，无边框） ┐   悬停：复制 · 编辑 · 时间
                                   └ 附件卡（done；图片缩略，文件名保扩展名截断）
● 正在搜索 useResults · 12s                      ← 运行中状态行（role=status，spinner+shimmer+计时）
› 已工作 1 分 20 秒 · 探索 6 个文件 · 运行 2 条命令   ← 完成后一行摘要（可展开）
   › 思考了 4 秒                                  ← ReasoningBlock
   › 已探索 6 个文件 · 2 次搜索                   ← ActivityGroup（└ 读取 a.ts, b.ts / 搜索 x 于 src）
   • 运行 npm test                  ✓ 1.2s        ← ToolRow（展开：输出预览 / 在终端面板打开）
   ┌ search#1 · 搜索 ············· ● 进行中 ⤢ ┐  ← SubAgentCard（就地展开；⤢ 在面板打开）
   │ └ 调研 useResults 的调用方                  │
   │   正在读取 src/graph.ts                     │
   └─────────────────────────────────────────────┘
   ─── 上下文已压缩 ───                           ← Marker separator
运行 `git push origin main`？  search#1 请求       ← 审批卡（永远在折叠外，含子智能体的）
[批准] [本次运行都批准] [拒绝] [改为这样做…]
回答正文（MarkdownRender，平滑流式 + 光标；永不折叠）
复制 · 14:32 · 已工作 1 分 20 秒 · 工具 12 次      ← 脚注（最后一条常显，其余悬停）
```

回合阶段 `sending → running → awaiting → settled | failed | cancelled`；失败/停止以 Marker 行写明原因，「重试」把原文回填输入框（沿用 `editAndResend` 不自动重发的口径）。

## 架构

### 数据层 `src/lib/conversation/`（纯函数，全单测）

- **`reducer.ts`（事件溯源）**：只吃会话 SSE，不再按事件拉 REST。每 run 一份可变记录 + `version`，`shallowRef` 发布；事件入队、`requestAnimationFrame` 批量应用（初始重放一次发布）；按 `run_id+seq+type` 去重，seq 按 run 分配、跨 run 不比较。
  - entry：`reasoning`（response_id、text、起止、handle）、`tool`（execution_id、tool_id、kind、status、risk、approval_id、subject、output 尾部 ≤200 行环形缓冲、exit_code、changed_paths、error_category、起止）、`dispatch`、`wait`、`plan`（按 task 原位替换并保留上一版）、`marker`、`supervision`。
  - `tasks: Map<task_id, SubAgent>`：`worker.assigned`（task_id/task_key/handle/agent_slug/reason）+ `task.dispatched`（task_key/title/agent/dispatched_by/follow_up_of）+ `worker.completed|failed|blocked`（summary）+ `task.wait_*`；`reason === 'solo_master'` 的任务是主智能体本身，其 entry 平铺在回合层。
  - 工具状态复刻 Core 折叠规则（requested→awaiting_approval→running/failed→completed/outcome_unknown；`governance.permission_decided` 经 `permission_request_id` 关联）；shell 命令/输出/退出码取 `terminal.command/stdout/exit`；`workspace.changes_observed` 补改动路径；subject 取 B1 新字段。
  - 只产出结构化数据与文案 key，**文案只在组件里 `t()`**。
- **`grouping.ts`**（Qoder `executeGrouping` 的 Vue 版）：quiet = 推理、read/list/search、wait；loud = 其余工具、失败工具、派发、计划、监督、marker；待审批工具提为顶层 approval 节点。连续 quiet 成 `ActivityGroup`（files/searches/lists 计数、思考毫秒、`exploring`=未结束回合最后一组且有运行中条目、纯思考组）；单个 quiet 不成组；连续派发成 `AgentGroup`；子智能体内部递归同一规则。
- **`turns.ts`**：messages + runs（完整 DTO）+ run 活动 + 流式文本 → `ConversationTurn[]`；按 `trigger_message_id` 配对，回答按 `message.run_id` 归位，无触发消息的 run 挂到创建时刻前最近回合否则自成回合，`pending-` 为 `sending`。
- **`toolSentence.ts`**：kind→图标+动词（进行时/过去式，中英）+ subject，复用 `lib/toolPresentation.ts` 的 `toolKindOf/toolIconOf`。
- **`format.ts`**：时长（1 分 20 秒 / 1m 20s / <1 秒）、跨天相对日期（今天/昨天/星期X）。
- **`fixtures/`**：录制事件序列（简单问答、探索+编辑+shell、待审批、并行子智能体、失败、打断、压缩），测试与 Debug Studio 共用。

### Composables

`useConversationFeed`（挂在 `HomeController`，复用 `sessionEventBus` 同一连接；切会话清空并使旧批次失效；`sessionEventBus` 增 `heartbeat` 监听作为初始重放结束信号）、`useDelayedLoading`（150/400）、`useElapsed`（共享 1s ticker）、`useSmoothText`（只做节奏，不碰 MarkdownRender 解析/消毒）、`useToolPreview`（展开时惰性取、按 execution 缓存、终态才取）、`useAgentDirectory`（slug→显示名，一次读取、失败退回 slug）。

### UI 原语（移植 shadcn-vue，样式换我们的 token）

`src/components/ui/{marker,message,bubble,attachment,spinner,message-scroller,collapsible}/`；`reka-ui@2.11.0` 补进 `apps/desktop/package.json`。保留旧 `ui/collapsible.vue` 导出给空间模式的 `ToolCallCard`。`message-scroller` 移植时**只测量最后一行与锚点**、rAF 合并；`.chat-shimmer` 泛化为 currentColor 的 `.shimmer` + `.shimmer-once`（保留旧类名别名）。

### 对话组件 `src/components/conversation/`

`ConversationView`（替换 ChatPanel 里的 MessageList）、`ConversationTurn`、`UserMessage`、`TurnActivity`、`ReasoningBlock`、`ActivityGroup`、`ToolRow`、`ToolPreview*`（file/search/directory/command/text/json/withheld）、`ApprovalCard`、`AgentTodos` + `AgentTodosStatus`、`SubAgentCard`、`AgentGroup`、`TurnMarker`、`AssistantAnswer`、`SupervisionGate`（迁移现有决策，保留 `continue→resume`、`correct` 走 insert）。TinadecUI 新增右栏卡 `SubAgentsCard`（`cards/index.ts` 注册 `subAgents`，singleton、可移动、本轮不可分离；`featureCatalog.ts` 加入口）。

关键行为：
- **展开模型**：`open = userToggled ? manual : live`；收起即卸载（reka）。
- **推理块**：最新闭合粗体标题作状态行文案；超长限高渐隐 + 「展开全部」。
- **工具行**：运行中计时、完成 `✓ 1.2s` / `✗ 原因`；展开：脱敏参数、结果预览（P0 端点）、shell 实时尾部（事件）、改动文件；**对话流内不挂 xterm**，「在终端面板打开」复用 `useTerminal.openAgentTerminal`。
- **审批卡**：读 `homeController.approvals` 的 `command/cwd/resource_path/arguments`；批准=`decideApproval(id,'approved')`，本次运行都批准=`scope:'run'`，拒绝=`'rejected'`，改为这样做=拒绝 + `createInteraction({dispatch_mode:'insert', target_run_id, content})`；shell 额外「始终允许以 `npm` 开头的命令」=`createApprovalRule({kind:'command_prefix'})`；保留 `ApprovalGateStatus`；按钮进行中用延迟 spinner；子智能体的审批也提到回合层并注明请求者。
- **子智能体**：紧凑卡 = 图标 + `handle · 显示名` + 状态徽标（等待/进行中/待审批/完成/失败/受阻/取消）+ L 形连接线任务标题 + 进行中最新一步（shimmer）；点击就地展开其分组活动与 `worker.completed` 摘要（Markdown）；⤢ 写入 `conversationSelection` 并 `uie.bus.dispatch(openCard subAgents)`。右栏卡：本会话子智能体名册（运行中在前）+ 选中者详情（同一组件、同一 feed）。≥2 个运行中时组头「2 个智能体工作中…」；`task.wait_started` 显示「等待 search#1、global_engineering#1」。
- **Marker 系统事件行**：`context.compacted`、`run.interrupted/interaction.interrupt_requested`、`run.model_retry_scheduled/recovered`、`run.recovered`、`session.workspace_bound`、`meeting.response_fallback`、`supervision.completed(revise/escalate)`、停止/取消、`run.failed`（原因不只靠颜色）、跨天日期分隔；进行中用 `role=status` + Spinner。新订阅的事件名先进 `events/coreEventTypes.ts`。
- **附件状态机**：`ui/attachment` 五态。发送框 `pendingAttachments` 的 `uploading/ready/failed` → `uploading/done/error`，会话准备中 → `idle`，失败原因写进描述，图片本地预览上传中变暗；转录 `done` 卡，`AttachmentTrigger` 走既有 Gateway 路由，**svg 仍不作缩略图、不显示存储路径**（保留现有安全测试）。
- **延迟加载与 spinner**：发送中、会话切换正文骨架（200/500）、工具运行图标、审批按钮、预览加载、子智能体徽标统一走 `useDelayedLoading`；占位不跳布局；减弱动效时脉冲替代旋转。
- **滚动**：用户回合 `scroll-anchor`（保留上一回合 64px）、贴底跟随、上翻不抢、跳底按钮带未读回合数、打开会话定位 `last-anchor`。
- **无障碍**：viewport `role=region`、内容 `role=log`、流式 `aria-busy`；折叠触发器 `aria-expanded/controls`、Enter/Space；live region 只播报需要人处理的事件。

### 视觉规则

中间列透明沉浸（`docs/uie-visual-layer-spec.md`）；活动行无边框无底色，状态只靠字形颜色；**卡片只给审批/子智能体/附件/代码岛**（`--surface-section` + `--border-card` + 10px 圆角，不叠阴影，不盒中盒）；正文 13px、活动 12px、meta 11px `tabular-nums`；lucide 14px；组内缩进 20px + 1px 引导线；不用彩色左竖条/光球/emoji；动作文案全局统一（批准/拒绝）。

## 后端（P0）

**B1 工具行句子数据（加法）**
- `TinadecCore/Tools/ToolDispatcher.cs`：`tool.execution.requested` 载荷加 `arguments_summary / resource_path / command / cwd`（`ApprovalEvidenceProjector.Project(toolId, execution.ParametersJson)`，与审批同一脱敏投影）。
- `AspNetCore/Endpoints/DmaeaEndpoints.cs`：`BuildToolExecutionTimeline` 透传上述字段与 `task_id / agent_instance_id / error_category`；`/sessions/{id}/tool-executions` 真正按 `run_id`（兼容 `runId`）与 `limit` 过滤。
- Gateway `src/index.ts` 该路由改发 `run_id`；Desktop `ToolExecutionTimelineItemDto` 加可选字段。

**B2 有界结果预览**
- 新 `Abstractions/Ports/ToolResultPreviewProjector.cs`（纯函数，与 `ApprovalEvidenceProjector` 共用敏感键表）+ `Contracts/Dtos/ToolExecutionPreviewDtos.cs` + `GET /api/v1/tool-executions/{id:guid}/preview?max_bytes=`（经 `IToolExecutionCoordinator.FindAsync`，租户/工作区作用域）。
- 返回 `{execution_id, run_id, task_id, tool_id, status, tool_success, error_category, safe_error_message, arguments, result_length, preview, truncated}`；`preview.kind`：`file_lines`(read_file，≤60 行×400 字，`all_contents[].content` 大小写无关解析，参照 `MarketInstallService.LineText`) / `search_matches`(file_search，≤40 行) / `directory`(ls，≤80 项) / `command_output`(shell/command_run，stdout/stderr 尾部各 ≤8KiB 与 exit_code/timed_out) / `text`(git_diff 等，≤8KiB，带 `language`) / `json`(兜底：深度≤6、数组≤20、字符串≤400) / `withheld`；未终态返回 `preview:null`。
- **安全审查清单**：只读、作用域外 404 不泄露存在性；任意深度丢弃敏感键；敏感路径（`.env*`、`*.pem/*.key/*.pfx/*.p12`、`id_rsa*/id_ed25519*`、`.npmrc/.pypirc`、`credentials*`、`*.kdbx`）返回 `withheld`；`max_bytes` 夹在 1–64KiB、默认 16KiB；不返回参数原文（只返回 evidence）；不缓存到 Gateway；错误只出 `safe_error_message`。
- Gateway 新增薄代理 `GET /api/v1/tool-executions/:executionId/preview`（problem+json 映射）+ `externalDtoOpenApi.ts` 文档 DTO；重生成 `openapi.core.json`、`openapi.external.json`、`apps/desktop/src/generated/schema.d.ts`（`npm run generate:client -w @tinadec/desktop`，`check:drift` 必须干净）。

## 阶段与退出条件

| 阶段 | 内容 | 退出条件 |
|---|---|---|
| P0 | B1 + B2 | Core 定向（新 `ToolResultPreviewProjectorTests`、`ToolExecutionPreviewEndpointTests`，`ToolChainEndpointTests` 断言新载荷与 run_id 过滤）、Gateway 路由测试与 `bun test`、三份契约同步、`check:drift` 干净 |
| P1 | 数据层 + fixtures + `useConversationFeed` | reducer/grouping/turns/toolSentence/format 单测；2000 条事件 hydrate 一次发布、可测时间 < 300ms（fixture 基准） |
| P2 | UI 原语 + composables | 组件测试：Attachment 五态、Spinner 延迟/最短显示（假时钟）、Marker 角色、Collapsible 卸载、scroller 锚定/跟随 |
| P3 | `ConversationView` 骨架接入 `ChatPanel` | 迁移 `MessageList.test.ts` 全部行为（首轮无回答仍有审批、活动只挂作答回合、编辑重发、附件安全规则）；类型检查 |
| P4 | 推理块、分层工具行、审批卡、待办、监督、工具预览 | 运行中展开/落答收起/手动开合后不再自动、审批在折叠外、读搜折叠写入独占、预览惰性加载 |
| P5 | 子智能体卡 + AgentGroup + 右栏 `SubAgentsCard` | 并行两子智能体各一卡、子智能体审批提到回合层、面板打开同一选中项 |
| P6 | 发送框附件状态机、`AgentTodosStatus`、`useAgentActivity.refreshToolExecutions` 按 run 300ms 尾随合并且重放期不发 | 现有 `ComposerBar.test.ts`/`useAgentActivity.test.ts` 通过；重放期 REST 调用为 0 |
| P7 | 收尾 | i18n 双语与 `i18nParity.test.ts` 纳入 `components/conversation/**`、减弱动效、窄/超窄、Debug Studio 预览换新组件、删除无引用的 `MessageList/MessageItem/LiveTurnBlock`、文档与报告 |

## 验证

- 每阶段：`npm --prefix apps/desktop test` 定向 + `npm --prefix apps/desktop run typecheck`；P0 另跑 Core 定向（`node scripts/dotnet.mjs test TinadecCore/TinadecCore.slnx --filter ...`，先停 dev 进程）、`npm --prefix TinadecGateway test`、`npm run check:drift -w @tinadec/desktop`。
- 收尾：Desktop 全量 vitest、`vite build`（必要时 `NODE_OPTIONS=--max-old-space-size=8192`）、Gateway 全量。
- 视觉：`npm run dev` 实机，在「关于→开发者工具」开 Debug Studio，用「对话消息流」预览跑全部 fixtures；1169/800/520/380px、亮/暗、毛玻璃/不透明、减弱动效各截图；`vue-mcp` 查组件树与折叠后无活动 DOM。
- 实链：有模型凭据时在 Team 模式跑一次会派发子智能体、需要 shell 审批的任务，核对自动展开/收起、审批卡、子智能体卡与面板、预览加载；无凭据则在报告里如实标「未验证」。
- 产出：计划副本进 `.tinadec_dev/plans/claude/`，验收报告进 `.tinadec_dev/reports/`；更新 `apps/desktop/AGENTS.md`、`TinadecCore/AGENTS.md`、`TinadecGateway/AGENTS.md`、根 `AGENTS.md` 元数据与 `docs/development-program/02-modules/app/home` 的 STATUS/TODO（新任务号）。

## 不在本轮

空间模式迁移；消息分页；编辑的 +/− 行数与改动审阅卡（需工作区快照 diff 契约）；HTML widget；对话模板/改模型提示词；xterm 升级；SSE 重放屏障协议。
