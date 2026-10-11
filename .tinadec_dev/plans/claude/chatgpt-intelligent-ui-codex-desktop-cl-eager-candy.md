# 平面对话流重构

## Context

平面模式对话流（`ChatPanel.vue` → `MessageList.vue` → `MessageItem.vue` → `chat/TurnTimeline.vue`）
把运行活动、工具调用、待办压进同一个折叠块，三种状态都读不出来：

- **跑的时候看不见在干什么。** `TurnTimeline.vue:18` `expanded = ref(false)`，无自动展开。
- **推理链已经有了却被藏起来。** `useAgentActivity.ts:900` 把 `channel === 'reasoning'` 的
  `model.output.delta` 累进 `type: 'reasoning'` 的 `ThinkingStep.description`，而
  `ThinkingProcess.vue:95` 只渲染 `.slice(-160)` 尾部预览。**思维链回传了，UI 丢掉了。**
- **多智能体看不见。** `useAgentActivity.ts:744-788` 明确在记 `task.dispatched`/`task_wait_started`
  的 `dispatched_by`/`agent`/`handle`，但渲染出来只是一个混在推理里的 `type: 'dispatch'` 步骤。
  谁派给谁、几个子智能体在并行，界面上读不出来。
- **滚动是自己写的一套。** `MessageList.vue:28-50` 用 `distance < 40` 判贴底再写 `scrollTop`。
  没有回合锚定、上溯保位、跳转、未读数。
- **待办表面有三套。** 折叠外的 `pendingCalls` 循环、折叠内 `detailItems`（排除
  `waiting_approval`）、`supervision-review` 块 —— 前两者渲染同一个 `ToolCallCard`。

目标：改成**回合**结构 —— 运行中展开并显示真实推理链与子智能体，落答后收成一行摘要，回答正文永远在
折叠之外，要人处理的待办永远可见。

---

## 参考来源与各自贡献

### 1. shadcn-vue `message-scroller`（已读源码，MIT，本地）

`C:\git\agent\shadcn-vue\apps\v4\registry\bases\reka\ui\message-scroller\`。
滚动引擎，除 `MessageScrollerButton` 外只依赖 `cn`，零新依赖。已按它的契约建好
`components/ui/messageScroller/`（见「设计 4」）。

### 2. hextaui（本地 clone 是 React，**读网页拿规格**）

AI 块在付费私有 registry。以下是从公开 block 页读到的**具体行为规格**，不是印象：

**Thinking**
- 推理轨迹可展开读全文；正文是粗体小标题 + 段落；「**半打出的标题会被压住，等它闭合才显示**」
  （`marked` 流式渲染要照做，否则标题会逐字跳）
- 顶部实时行：小球 + 当前步骤的白话（「Searching the web」）+ 计时器
- 工具步骤：运行时 spinner + 现在时标签，跑完**改成过去式**（「Searched for X」）
- `defaultOpen` 流式中默认展开；**`autoCollapse` 默认 true，流结束才收起，且「除非有人自己开合过」**
- 收起后文案：`Thought for 12s`

**Tool Calls**
- 一行一步，**读起来像句子**：「Read components/search.tsx」「Searched for useResults, 3 results」
- `group`（默认 true）：**连续跑的读/搜/查折成一句摘要**「Explored 6 files」
- **「会改东西的、要决策的、失败的，各自独占一行」**，运行时带计时器
- 展开一步给真内容：文件、diff、终端、结果
- 审批：**Allow / Deny / Always allow / Tell it what to do instead**（redirect 把用户的话当理由回传，
  ⌘↵ 提交 / ⌘⌫ 取消）
- `kind`：`read | search | list | edit | write | run | web | fetch | ask | other` → 决定图标与动词
- 键盘：Enter/Space 开合一步或一个折叠组

**Agent Todos**
- 状态：`backlog | pending | in_progress | completed | failed | cancelled`（pending 显示为「To do」）
- 一行一步，进行中的写全（「Running the tests」），时长「以秒存下来，供历史用」
- **失败步骤把原因留在视野里**
- `foldAfter`（默认 6）：**一长串已完成的折起来，让当前工作留在屏上**
- 计划被改写时：新步骤高亮、删掉的折叠消失、**一句短note说明改了什么**
- `details` slot 开在步骤下面，通常就是 Tool Calls
- `AgentTodosStatus`：当前步骤 + 计数，一行贴在输入框之上
- `AgentTodosReview`：跑之前可编辑/重排/增删，⌘↵ 开始

### 3. ChatGPT Intelligent UI（已查证契约）

不是形容词，是 Apps SDK 插件 UI。工具返回 `_meta.ui.resourceUri` → `text/html;profile=mcp-app`
资源 → **沙箱 iframe** → `postMessage` 上的 JSON-RPC 桥（`ui/initialize`、
`ui/notifications/tool-result`、`tools/call`、`ui/message`）。

| 形态 | 用途 | 硬约束 |
|---|---|---|
| inline card | 默认，在模型回答之前 | **最多两个动作**；不许卡内导航/多视图；不许嵌套滚动；不许重复输入 |
| inline carousel | 并列候选 | 3–8 项、必须有图、元数据 2–3 行 |
| fullscreen | 沉浸多步 | 输入框保持浮在上层；聊天上下文收进 sheet |
| PiP | 并行活动 | 必须响应输入框 |

视觉：只用系统色与系统字体（**禁自定义字体**）、图标单色描边、不自己放 logo、WCAG AA。

### 4. Qoder（本机 `C:\Program Files\Qoder IDE`，VS Code fork）

它的 agent 叫 lingma/Quest。从 bundle 里读到的**组件名**（这是多智能体 UI 的直接证据）：

`SubAgentCard`、`SubAgentCompactCard`、`SubAgentStatusBadge`、`SubAgentProgress`、
`SubAgentBlocks`、`SubAgentToolCall`、`SubAgentEditor`、`AgentTaskTitle`、
`SubAgentsBySession`、`SubAgentStateChanged`

以及角色/形态文案：`Lead Agent`、`Plan Agent`、`Browser Agent`、`Computer Use Agent`、
`User Agent`、`Project Agent`、`Side Task`、`Ask in Side Task`、`Task Understanding`、
`Plan and Action`、`Current Plan`、`Task Report`、`In Progress`。

**它的模型**：主智能体是 Lead Agent，派出去的是有独立上下文/工具/提示词的 SubAgent，
每个 SubAgent 有**自己的卡片、状态徽章和进度**，而不是折进主线。还有「跑之前先评审计划」
（`Plan and Action`）与「把计划本身当任务」（Plan Mode）。

### 5. reka-ui（未读，列为待用）

折叠/手风琴无头原语。我们现在用自写的 `ui/collapsible.vue`（`v-if`，无过渡）。

---

## 设计

### 1. 回合模型（新增 `lib/conversationTurns.ts`，纯函数）

**用户消息不带 `run_id`**（`FullDuplexRunCoordinator.cs:223` 只传 `clientMessageId`；只有 assistant
带，`FullDuplexRunEngine.cs:4933`），不能按 run_id 配对。按 Core 维护的 `sequence` 走：

```
遇到 user 消息                        → 开新回合
遇到带 run_id 的 assistant/tool_evidence → 归入当前回合
再遇到 user                           → 关上一个
收尾：turnActivities/streamingReplies 里没被认领的 runId → 追加为尾部「进行中回合」
```

```ts
interface ConversationTurn {
  key: string
  userMessage: MessageDto | null
  answer: MessageDto | null
  activity: TurnActivity | null
  streamingText: string | null
  status: 'running' | 'settled'
}
```

### 2. 展开模型：`defaultOpen` + `autoCollapse`，用户开合过就不再自动

照 hextaui 的 Reasoning 契约，不自己发明：

```ts
const userToggled = ref(false)
const open = computed(() => userToggled.value ? manual.value : !settled.value)
```

- 流式中：展开
- 流结束：自动收起成 `活动 · 12 项 · 2 个文件`（**除非用户点过**）
- 收起/展开复用 `.chat-collapse`（`styles.css:4589` 的 `grid-template-rows 0fr↔1fr`）

### 3. 工具调用分层（本次最重要的行为改动）

照 hextaui Tool Calls 的三条规则：

| 类别 | 判定（用已有的 `toolKindOf()`，`lib/toolPresentation.ts:216`） | 渲染 |
|---|---|---|
| 读/列表/搜索 | `read` `list` `search` | **连续的一段折成一句**「读取 6 个文件」 |
| 改/跑/Git/MCP | `write` `shell` `git` `mcp` `sandbox` | **各自独占一行**，运行时计时器 |
| 审批 | `status === 'waiting_approval'` | **永远在折叠之外** |
| 失败 | `status === 'failed'` | 保留，原因留在视野里 |

每行读起来像句子：`读取 src/graph.ts` / `搜索 useResults，3 处` / `运行 npm test`，
完成后动词改过去式。`kind` 决定图标与动词，两端都有既有表（`toolIconOf` / `toolKindOf`）。

**审批四选项**（照 hextaui）：批准 / 拒绝 / 始终允许 / **改为这样做**（把用户输入当理由回传，
走已有的 `createInteraction` + `dispatch_mode: 'insert'`）。

**诚实边界**：不显示 `+48/-6`。`ToolExecutionTimelineItemDto` 只有
`summary`/`evidence`/`risk`/`duration_ms`，整条链路没有 diff 行数。要显示只能另开契约改动。

### 4. 多智能体：子智能体卡（Qoder 模型）

我们的数据已经够了 —— `useAgentActivity.ts:744-788` 在记
`task.dispatched`（`dispatched_by` / `agent` / `title` / `follow_up_of`）、
`task.wait_started`（`handle` / `awaited`）、`task.dispatch_rejected`、`plan_update`。

新增 `SubAgentCard.vue`（照 Qoder 的 `SubAgentCard` + `SubAgentCompactCard` 两档）：

```
你：重构任务图构建逻辑

▾ 活动                                            ← 父回合，运行中展开
   🧠 思考 · 推理链正文…                           ← §5
   ▸ 读取 6 个文件 · 搜索 2 次                      ← 折成一句（§3）
   ▸ 运行 npm test                    ✓ 1.2s      ← 独占一行
   ┌ search#1 ─ 搜索专家 ──────────── ● 进行中 ┐   ← 子智能体卡
   │ 搜索 useResults                      ✓     │
   │ 读取 src/graph.ts                    ✓     │
   │ 汇总中…（2/3）                             │
   └────────────────────────────────────────────┘
   ┌ global_engineering#1 ─ 工程 ──── ⏸ 待批准 ┐
   │ write_file src/graph.ts                    │
   │ [批准] [拒绝] [始终允许] [改为这样做]        │
   └────────────────────────────────────────────┘

已重构完成，要点如下：…                              ← 回答永远在外
```

- 子智能体按 `handle`（`search#1`）唯一，**并行多个各自一张卡**
- 卡头：handle + 角色 + 状态徽章（运行中/待批准/完成/失败）
- 卡体：该子智能体自己的 `plan_update` 步骤 + 它名下的工具调用（按 `handle` 归集）
- 折叠后：`search#1 搜索 useResults, 读取 src/graph.ts`（`SubAgentCompactCard`）
- `plan_update` 照 hextaui Agent Todos：`foldAfter` 折掉已完成的一长串，
  **计划被改写时新步骤高亮 + 一句 note 说明改了什么**

### 5. 推理链渲染成真内容

`type: 'reasoning'` 的 step 有完整 `description`，从 `.slice(-160)` 预览改成可展开正文块：

- 流式中：`.chat-shimmer` 扫光 + 计时器 + 白话当前步骤（「正在搜索」）
- **半打出的 Markdown 标题压住**，闭合才显示（否则标题逐字跳）
- 结束后：`Thought for 12s` 一行

### 6. 滚动引擎替掉手写跟随（已建）

`apps/desktop/src/components/ui/messageScroller/`，相对上游 registry：

- 6 个组件合成 3 个 DOM 元素
- 类名换我们的 token（`--surface-section`/`--border-muted`/`--shadow-panel`）
- **去掉 `content-visibility: auto`**：引擎 `measureContentHeight` 对每行取
  `getBoundingClientRect().bottom`，离屏行会报估算值让 `scrollable.end` 说谎。已用 `ponytail:`
  注释记下天花板与升级路径。

结构：

```
MessageScrollerProvider(auto-scroll, default-scroll-position="last-anchor")
└ MessageScroller
  ├ MessageScrollerViewport          ← 原生滚动容器，role=region
  │ └ MessageScrollerContent         ← 承接原 role="log" aria-live
  │   └ MessageScrollerItem × 回合    ← :message-id="turn.key" :scroll-anchor="true"
  └ MessageScrollerButton            ← 未读数
```

### 7. 三种模板由用户选（已拍板：会话级模板切换）

会话级设置 `transcript_template`，复用已有的 per-session settings + `settings_revision` 通道
（`HomeController` 已经在管 `permission_mode`/`mode_version_id`）：

| 模板 | 形态 |
|---|---|
| 简洁 | 预算子风格；活动一律收起，只留摘要行 + 待办 |
| 标准 | 本方案的默认：运行中展开、落答收起、子智能体卡、分层工具行 |
| 详尽 | 全部展开，推理链与每个子智能体的每一步都平铺 |

入口放设置页「外观」下，与现有材质/主题同列。

## 工具结果渲染：档 B（受限富组件，已拍板）

ChatGPT 那套的核心是**工具返回 HTML 在沙箱 iframe 里跑**，那要开 iframe sandbox + CSP +
自定义桥。你们刚在 issue #33 把 `webSecurity` 收回来、打包态改 `app://bundle`、本地媒体改
`tinadec-media://` —— **这是安全边界决策**。

**拍板结果：档 B。Core 继续给结构化结果，Desktop 用固定组件表渲染，模型不产 HTML。**
安全上等价于现在（不引入任何新的执行面），只是把工具结果从一块 `<pre>` 换成按类型分发的固定视图。

`ToolCallCard` 的展开区按 `toolKindOf()` 分发：

| kind | 视图 |
|---|---|
| `write` / `edit` | diff 视图（unified） |
| `read` / `list` / `search` | 文件路径列表 / 结果列表 |
| `shell` | 已有的 `TerminalCallBlock` |
| `git` | 已有的 git 视图（`DiffViewer`/`GitChangesView` 可复用） |
| `mcp` / `web` | 结果列表 + 来源链接 |
| 其它 | 现在的 `evidence` 列表 |

**档 C 不做**，但分发点是按 kind 表的，将来要加 HTML 视图只加一行映射，不动结构。

**依赖**：档 B 需要先确认 Core 侧工具结果到底给了什么结构（`ToolExecutionTimelineItemDto` 现在只有
`summary`/`evidence`/`risk`/`duration_ms`）。第 5 批开工前先读一遍真实结果负载；如果只有自由文本，
先上「有结构就用结构、没有就退回现有 `evidence`」的降级，**不编数据**。

---

## 改动文件

**新增**
- `lib/conversationTurns.ts` + `.test.ts` — 回合与子智能体归集（纯函数）
- `components/ui/messageScroller/*` — 已建
- `components/chat/SubAgentCard.vue` + `.test.ts`
- `components/chat/ToolGroupSummary.vue` — 折成一句的读/搜组
- `lib/toolSentence.ts` + `.test.ts` — `kind` → 动词/图标/句子
- `composables/useDelayedLoading.ts` — 延迟出现 + 最短显示

**改**
- `components/MessageList.vue` — scroller 结构 + 按回合渲染
- `components/chat/TurnTimeline.vue` — 展开模型 + 摘要行 + 工具分层 + 待办收敛 + 子智能体卡
- `components/chat/ToolCallCard.vue` — 一行一句 + 四选项审批 + 按 kind 的展开视图
- `components/chat/ThinkingProcess.vue` — 推理正文取代 160 字预览
- `components/chat/AttachmentStrip.vue`（或 MessageItem 内）— 附件状态机
- `components/MessageItem.vue` — 收 `turn` 而非散装 props
- `components/chat/LiveTurnBlock.vue` — 并入回合渲染后删除
- `components/ChatPanel.vue` — 三个 computed 合成一个 `turns`
- `styles.css` — `.message-stream-*` → `.message-scroller-*`；窄/超窄两组同步
- `locales/zh-CN.ts`、`en.ts`、`i18nParity.test.ts`

## 不在这一轮

- 后端契约改动（没有 diff 行数就不显示 diff）
- `ComposerBar` / 发送区
- HTML 沙箱（档 C）
- `SpatialWorkCard` 复用 `TurnTimeline`，它会跟着拿到新模型；不改调用语义但要跑它的测试

## 验证

1. **纯函数**：`conversationTurns` 覆盖空数组、只有用户消息、assistant 无 run_id、两个并行 run、
   进行中无 answer、顺序与 run 顺序不一致、两个子智能体并行、子智能体失败。
   `toolSentence` 覆盖每个 `kind` 的动词与图标。
2. **组件**：`MessageList.test.ts` 现有 5 条 turn-anchoring 用例改回合形状后仍过 —— 尤其
   「首轮 run 无 assistant 消息时仍出工具活动与审批入口」（`:86`）与「活动只挂到作答的那条消息」
   （`:113`）。新增：运行中默认展开、落答收起、**用户开合过则不再自动**、待办在折叠外、
   读/搜折成一句而写入独占一行、推理正文可见、两个子智能体各出一张卡。
   `TurnTimeline.test.ts` 与 `SpatialWorkCard.test.ts` 同步。
3. **类型/构建**：`npm --prefix apps/desktop run typecheck`（不新增错误）、`vite build`。
4. **真机滚动**：贴底时流式跟随、往上滚后新 delta 不拉人、发新消息把回合锚到顶部并留上一回合一角、
   未读数出现且点击回底。
5. **无障碍**：`role="log"` 位置、折叠用 `inert` 而非 `display:none`（内容仍可被搜索找到）、
   按钮 `inert`/`tabindex=-1`、Enter/Space 开合、`prefers-reduced-motion` 下不动画。

## 顺序

1. 纯函数：`conversationTurns` + `toolSentence`（可单测，不碰 UI）
2. 滚动容器换掉（视觉不变）
3. 回合展开模型 + 推理链 + 工具分层 + 待办收敛（动视觉）
4. 子智能体卡（多智能体可见性）
5. 附件状态机 / 延迟加载 / Marker 系统事件行 / 空态
6. 模板切换（暂定）
7. 真机走查

每批跑定向 + typecheck，最后一起走真机。
