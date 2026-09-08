> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# Desktop UI 重新设计计划 V2

## 用户反馈修正

1. **"理解这个项目，分析并修复环境"是标题，放在标题栏** — 当前 ChatHeader 显示会话标题，需要改为显示当前任务/会话标题作为页面标题
2. **标题栏与内容区域没有明确界限** — AppHeader（窗口控制栏）与内容之间不需要边框分隔，融为一体
3. **右侧面板变成浮动窗** — 不是侧边栏，是圆角矩形浮动面板，有折叠按钮，折叠后是一根更长的圆角矩形
4. **"智能体验"改为"智能体配置"** — 点击后跳转到设置页面的智能体中心

## 草图详细分析

```
┌─────────────────────────────────────────────────────────────┐
│  Tinadec                              [_] [□] [X]           │  ← AppHeader（无下边框，与内容融合）
├──────────┬────────────────────────────────┬─────────────────┤
│          │ 理解这个项目，分析并修复环境      │  > □终端 4 Git 审批 ⊖  │  ← ChatHeader（会话标题）│ 右侧面板顶部工具栏
│  Tinadec │                                │                 │
│  □ 新对话 │  [📋] [0]  [分析项目。]         │                 │  ← 用户消息带复制按钮和计数
│  🏪 市场  │                                │   ✓ 无需要批准的动作   │
│  ⚡ 指挥中心│  好的，我现在就开始理解该项目    │                 │  ← AI 大标题回复
│          │  🔍 查找 *.cs ...               │   目前为完全访问编辑模式>│
│  项目     │  📁 分析 main.cs                │                 │  ← 工具调用列表
│  ▼ Tinadec│  🌐 搜索网页 [图] [图]           │                 │
│    理解这  │  □ 执行 powershell              │                 │
│  □ landll  │  ⚡ 计划模式 start               │                 │
│          │  📝 规划中)                     │                 │  ← 状态指示
│  ⚙ 设置   │  我现在开始分析 [思考中]         │                 │  ← Thinking 指示器
│          │                                │                 │
│          │  ┌──────────────────────────┐   │                 │
│          │  │ 继续追问                   │   │                 │  ← 输入框
│          │  │ + ⚡Auto     智能体配置 >   │   │                 │  ← 底部工具栏
│          │  └──────────────────────────┘   │                 │
└──────────┴────────────────────────────────┴─────────────────┘
```

## 实施步骤

### Phase 1: AppHeader 去边框化
**文件**: `AppHeader.vue`, `styles.css`
- 移除 `.topbar` 的 `border-bottom: 1px solid var(--border-default)`
- 标题栏与下方内容区域无缝融合

### Phase 2: ChatHeader 改为会话标题栏
**文件**: `ChatHeader.vue`, `styles.css`
- 显示当前会话标题（大号字体，居中或左对齐）
- 移除 StatusPill（状态信息移到其他地方）
- 项目路径以小字显示在标题下方或省略
- 样式：无背景色，无边框，纯文字标题

### Phase 3: 右侧面板改为浮动面板
**文件**: `ContextPanel.vue`, `styles.css`
- 改为 `position: fixed` 或绝对定位浮动在右侧
- 添加圆角（12px-16px）
- 添加阴影 `box-shadow: var(--shadow-panel)`
- 与边缘有间隙（margin: 8px-12px）
- 保留折叠功能，折叠后是更窄的圆角矩形
- 顶部工具栏：折叠按钮、终端、Git、审批、更多（图标按钮）

### Phase 4: 消息列表重新设计
**文件**: `MessageItem.vue`, `MessageList.vue`, `styles.css`
- **用户消息**：
  - 右上角显示复制按钮（📋）和计数徽章（0）
  - 使用 shadcn UiBadge 组件
- **AI 消息**：
  - 大标题文字（20px）
  - 工具调用列表（带图标）
  - 状态指示（"规划中)"）
  - Thinking 状态（带旋转动画）

### Phase 5: ComposerBar 优化
**文件**: `ComposerBar.vue`, `styles.css`
- 输入框保持圆角设计
- 底部工具栏：+ 按钮、ModeSelector（⚡Auto）、PermissionSelector、"智能体配置"按钮
- "智能体配置"点击后 `router.push('/settings')` 跳转到设置页

### Phase 6: WelcomeScreen 优化
**文件**: `WelcomeScreen.vue`, `styles.css`
- 保持居中大对话框
- 底部工具栏同步更新（智能体配置按钮）

### Phase 7: 全局样式统一
**文件**: `styles.css`
- 消息气泡样式优化
- 工具调用列表样式
- 浮动面板样式
- 标题栏去边框

### Phase 8: 类型检查
- 运行 `vue-tsc --noEmit`

## 文件变更清单

### 修改文件
1. `AppHeader.vue` — 移除下边框
2. `ChatHeader.vue` — 改为纯标题显示
3. `ContextPanel.vue` — 改为浮动面板
4. `MessageItem.vue` — 重新设计消息项
5. `MessageList.vue` — 优化消息列表
6. `ComposerBar.vue` — 添加智能体配置按钮
7. `WelcomeScreen.vue` — 同步更新工具栏
8. `styles.css` — 全局样式更新

### 新增文件
1. `ToolCallItem.vue` — 工具调用项组件
2. `ThinkingIndicator.vue` — 思考中指示器

## 设计规范

### 浮动面板
- position: fixed
- right: 12px, top: 60px, bottom: 12px
- width: 320px (展开) / 48px (折叠)
- border-radius: 12px
- background: var(--bg-secondary)
- box-shadow: var(--shadow-panel)
- border: 1px solid var(--border-muted)

### 消息样式
- 用户消息：右侧对齐，带操作按钮
- AI 消息：左侧对齐，大标题 + 工具列表
- 工具调用项：图标 + 文字，悬停高亮

### 标题栏
- AppHeader：无下边框，与内容融合
- ChatHeader：大号标题文字，无背景
