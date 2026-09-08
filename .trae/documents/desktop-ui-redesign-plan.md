> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# Desktop UI 重新设计计划

## 目标
根据用户手绘草图，将 Tinadec Desktop 的 UI 重新设计为现代 AI 对话风格，使用 shadcn/ui 组件体系。

## 草图分析

### 整体布局（三栏式）
```
┌─────────────────────────────────────────────────────────────┐
│  [Tinadec]                              [_] [□] [X]        │ ← 标题栏（最小化/最大化/关闭在右侧）
├──────────┬───────────────────────────────┬──────────────────┤
│          │  理解这个项目，分析并修复环境   │  > □终端 4 Git  审批  ⊖  │ ← 右侧面板标题栏（带图标按钮）
│  Tinadec │                              │                  │
│  □ 新对话 │  [📋] [0]  [分析项目。]      │                  │
│  🏪 市场  │                              │   ✓ 无需要批准的动作    │
│  ⚡ 指挥中心│  好的，我现在就开始理解该项目   │                  │
│          │  🔍 查找 *.cs ...              │   目前为完全访问编辑模式 > │
│  项目     │  📁 分析 main.cs               │                  │
│  ▼ Tinadec│  🌐 搜索网页  [图] [图]         │                  │
│    理解这  │  □ 执行 powershell             │                  │
│  □ landll  │  ⚡ 计划模式 start              │                  │
│          │  📝 规划中)                    │                  │
│  ⚙ 设置   │  我现在开始分析 [思考中]        │                  │
│          │                              │                  │
│          │  ┌──────────────────────────┐  │                  │
│          │  │  继续追问                  │  │                  │
│          │  │  + ⚡Auto          智能体验> │                  │
│          │  └──────────────────────────┘  │                  │
└──────────┴───────────────────────────────┴──────────────────┘
```

### 关键设计元素

1. **标题栏 (AppHeader)**
   - 左侧：Tinadec 品牌标识（带 Diamond 图标）
   - 右侧：窗口控制按钮（最小化 _ / 最大化 □ / 关闭 X）
   - 当前已有，位置正确

2. **左侧边栏 (AppSidebar)**
   - 顶部导航：Tinadec（品牌）、新对话、市场、指挥中心
   - 中间：项目列表（可展开/折叠）
   - 底部：设置按钮
   - 当前已有，需要微调样式

3. **中间主区域 - 对话界面**
   - **顶部**：显示当前任务标题（如"理解这个项目，分析并修复环境"）
   - **用户消息**：带图标 + 消息内容，右上角有操作按钮（📋复制、0 计数）
   - **AI 回复区域**：
     - 大标题文字（"好的，我现在就开始理解该项目"）
     - 工具调用列表（🔍 查找、📁 分析、🌐 搜索网页、□ 执行 powershell、⚡ 计划模式）
     - 状态指示（"规划中)"）
     - Thinking 状态（"我现在开始分析 [思考中]"）
   - **底部输入框**：
     - 圆角大输入框
     - 左侧 + 按钮
     - 模式选择（⚡Auto 下拉）
     - 右侧"智能体验"按钮
     - 发送按钮（在输入框右侧或下方）

4. **右侧面板 (ContextPanel)**
   - 顶部工具栏：> 展开/折叠、□ 终端、4 Git、审批、⊖ 更多
   - 内容区域：
     - ✓ 无需要批准的动作
     - 目前为完全访问编辑模式 >
   - 当前是 Tab 切换形式，需要改为顶部图标工具栏 + 内容区域

## 实施步骤

### Phase 1: 消息列表与消息项重新设计

#### 1.1 重新设计 MessageItem.vue
- 用户消息：右侧对齐，带复制按钮和计数徽章
- AI 消息：左侧对齐，大标题风格，支持工具调用列表显示
- 使用 shadcn Badge、Button 组件

#### 1.2 重新设计 MessageList.vue
- 支持滚动区域
- 空状态显示
- 使用 shadcn ScrollArea

### Phase 2: 对话头部重新设计

#### 2.1 重新设计 ChatHeader.vue
- 显示当前会话标题（大号字体）
- 显示项目路径（小号次要文字）
- 移除 StatusPill（移到其他地方或简化）
- 使用 shadcn 语义化样式

### Phase 3: 右侧面板重新设计

#### 3.1 重新设计 ContextPanel.vue
- 顶部改为图标工具栏（终端、Git、审批、更多）
- 使用 shadcn ToggleGroup 或自定义图标按钮组
- 内容区域根据选中工具显示不同内容
- 当前审批内容显示为卡片形式

#### 3.2 更新 ApprovalTab.vue（如有需要）
- 适配新的卡片风格

### Phase 4: ComposerBar 输入框优化

#### 4.1 优化 ComposerBar.vue
- 保持现有圆角输入框设计
- 底部工具栏：+ 按钮、ModeSelector、PermissionSelector、智能体验按钮
- 使用 shadcn 组件替换自定义样式

### Phase 5: WelcomeScreen 优化

#### 5.1 优化 WelcomeScreen.vue
- 保持居中大对话框设计
- 标题和终端按钮并排
- 输入框和工具栏样式统一

### Phase 6: 全局样式统一

#### 6.1 更新 styles.css
- 统一使用 shadcn CSS 变量
- 消息气泡样式
- 工具调用列表样式
- 响应式适配

### Phase 7: 类型检查与验证

#### 7.1 运行 vue-tsc
- 确保所有类型正确
- 修复任何类型错误

## 文件变更清单

### 修改的文件
1. `apps/desktop/src/components/MessageItem.vue` - 重新设计消息项
2. `apps/desktop/src/components/MessageList.vue` - 优化消息列表
3. `apps/desktop/src/components/ChatHeader.vue` - 重新设计头部
4. `apps/desktop/src/components/ContextPanel.vue` - 重新设计右侧面板
5. `apps/desktop/src/components/ComposerBar.vue` - 优化输入框
6. `apps/desktop/src/components/WelcomeScreen.vue` - 优化欢迎屏
7. `apps/desktop/src/styles.css` - 更新全局样式

### 可能新增的文件
1. `apps/desktop/src/components/ToolCallItem.vue` - 工具调用项组件（AI 消息中的工具列表）
2. `apps/desktop/src/components/ThinkingIndicator.vue` - 思考中指示器

## 设计规范

### 颜色
- 使用现有 CSS 变量体系
- 主背景：var(--bg-primary)
- 次背景：var(--bg-secondary)
- 用户消息背景：var(--bg-user-msg)
- AI 消息背景：var(--bg-assistant-msg)
- 主文字：var(--text-primary)
- 次文字：var(--text-secondary)
- 品牌色：var(--text-brand)

### 圆角
- 大对话框：20px
- 输入框：16px
- 按钮/标签：6px-8px
- 消息气泡：12px

### 字体大小
- 标题：24px (欢迎屏) / 16px (对话头部)
- AI 大标题：20px
- 正文：14px
- 工具调用项：13px
- 标签/徽章：11px-12px
