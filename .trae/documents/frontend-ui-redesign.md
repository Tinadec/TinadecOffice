> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# TinadecCode 前端 UI 改造计划

## 概述

将 TinadecCode 桌面应用的前端 UI 改造为接近 OpenAI Codex、Cursor 3.0 和 Trae Solo 的设计风格，同时引入图标系统优化、中文/英文双语支持、暗色主题优先的完整主题系统。

**范围**：仅前端 UI 层（`apps/desktop/src/`），不修改后端代码。

## 设计参考

| 参考应用 | 借鉴要点 |
|----------|----------|
| OpenAI Codex Desktop | 标题栏导航图标、线程/项目/评审三栏结构、全屏设置页、暗色主题、Skills 管理 |
| Cursor 3.0 (Glass) | 代理优先工作空间、标题栏左上角导航、并行代理标签、暗色主题 |
| Trae Solo | 会话侧边栏、聊天焦点布局、暗色主题、状态指示器 |

## 布局设计

### 主界面布局

```
┌──────────────────────────────────────────────────────────────┐
│ [◉ TinadecCode] [💬] [📁] [⚙️]          [● 已连接] [中/EN] │  ← 标题栏
├────────────┬─────────────────────────────────┬───────────────┤
│            │                                 │               │
│  会话列表   │       对话 / 聊天区域            │   上下文面板   │
│  &         │                                 │   (标签页)     │
│  项目列表   │                                 │   ├ 审批       │
│            │                                 │   ├ Diff      │
│            │                                 │   ├ 事件       │
│            │                                 │   └ 诊断       │
│            ├─────────────────────────────────┤               │
│            │  输入框 / Composer              │               │
├────────────┴─────────────────────────────────┴───────────────┤
```

### 设置页面布局（全屏切换）

```
┌──────────────────────────────────────────────────────────────┐
│ [← 返回]  设置                                    [中/EN]    │
├────────────┬─────────────────────────────────────────────────┤
│            │                                                 │
│  模型配置   │  模型配置内容区域                                │
│  外观       │                                                 │
│  语言       │  外观设置内容区域                                │
│  API 文档   │                                                 │
│  关于       │  语言设置内容区域                                │
│            │                                                 │
└────────────┴─────────────────────────────────────────────────┘
```

## 主题系统

### 暗色主题（默认）

```css
:root[data-theme="dark"] {
  --bg-primary: #0d1117;
  --bg-secondary: #161b22;
  --bg-tertiary: #21262d;
  --bg-overlay: #1c2128;
  --border-default: #30363d;
  --border-muted: #21262d;
  --text-primary: #e6edf3;
  --text-secondary: #8b949e;
  --text-muted: #6e7681;
  --accent-primary: #58a6ff;
  --accent-success: #3fb950;
  --accent-warning: #d29922;
  --accent-danger: #f85149;
  --accent-brand: #39d353;
}
```

### 亮色主题

```css
:root[data-theme="light"] {
  --bg-primary: #ffffff;
  --bg-secondary: #f6f8fa;
  --bg-tertiary: #f0f2f5;
  --bg-overlay: #ffffff;
  --border-default: #d0d7de;
  --border-muted: #e8ecf0;
  --text-primary: #1f2328;
  --text-secondary: #656d76;
  --text-muted: #8c959f;
  --accent-primary: #0969da;
  --accent-success: #1a7f37;
  --accent-warning: #9a6700;
  --accent-danger: #cf222e;
  --accent-brand: #1f6feb;
}
```

## 组件架构

### 文件结构

```
apps/desktop/src/
├── App.vue                    # 主壳组件（布局 + 路由视图）
├── main.ts                    # 入口（挂载 Vue Router + i18n）
├── api.ts                     # API 客户端（保持不变）
├── format.ts                  # 格式工具（保持不变）
├── styles.css                 # 全局样式 → 重构为 CSS 自定义属性
├── locales/                   # i18n 语言文件
│   ├── zh-CN.ts               # 中文翻译
│   └── en.ts                  # 英文翻译
├── i18n.ts                    # vue-i18n 配置
├── router.ts                  # Vue Router 配置
├── composables/               # 可复用组合式函数
│   └── useTheme.ts            # 主题切换逻辑
├── components/                # UI 组件
│   ├── AppHeader.vue          # 标题栏（品牌 + 导航图标 + 状态）
│   ├── AppSidebar.vue         # 左侧面板（项目 + 会话列表）
│   ├── ChatPanel.vue          # 中间对话区域
│   ├── ChatHeader.vue         # 对话区头部（标题 + 状态）
│   ├── MessageList.vue        # 消息流
│   ├── MessageItem.vue        # 单条消息
│   ├── ComposerBar.vue        # 输入框区域
│   ├── ContextPanel.vue       # 右侧上下文面板（标签页容器）
│   ├── ApprovalTab.vue        # 审批标签页
│   ├── DiffTab.vue            # Diff 标签页
│   ├── EventsTab.vue          # 事件标签页
│   ├── DoctorTab.vue          # 诊断标签页
│   └── StatusPill.vue         # 状态标签组件
├── pages/                     # 页面级组件
│   └── SettingsPage.vue       # 设置页面（全屏）
└── env.d.ts                   # 类型声明
```

### 组件职责

| 组件 | 职责 | 关键交互 |
|------|------|----------|
| `AppHeader` | 标题栏：品牌标识、导航图标（聊天/项目/设置）、连接状态、语言切换 | 点击导航图标切换视图/路由 |
| `AppSidebar` | 左侧面板：项目列表 + 会话列表，可折叠 | 选择项目/会话，新建项目/会话 |
| `ChatPanel` | 中间对话区容器 | 组合 ChatHeader + MessageList + ComposerBar |
| `ChatHeader` | 对话区头部：当前会话标题、项目路径、状态标签 | 显示当前会话状态 |
| `MessageList` | 消息流：渲染消息列表，自动滚动到底部 | 接收 SSE 事件更新 |
| `MessageItem` | 单条消息：用户/助手头像、消息内容 | 区分 user/assistant 样式 |
| `ComposerBar` | 输入区域：文本框 + 发送按钮 | 发送消息，自动创建会话 |
| `ContextPanel` | 右侧面板：标签页容器（审批/Diff/事件/诊断） | 标签页切换 |
| `ApprovalTab` | 审批管理：待审批列表 + 批准/拒绝操作 | 审批决策 |
| `DiffTab.vue` | Diff 展示：工作区变更预览 | 占位，未来集成 Monaco Diff |
| `EventsTab` | 事件流：最近运行事件列表 | 实时更新 |
| `DoctorTab` | 诊断检查：系统健康状态 | 显示各检查项状态 |
| `StatusPill` | 状态标签：ok/warn/danger/neutral | 复用状态显示 |
| `SettingsPage` | 全屏设置页：模型/外观/语言/API文档/关于 | 路由切换进入 |

## 路由设计

```
/               → 主界面（ChatPanel + Sidebar + ContextPanel）
/settings       → 设置页面（全屏）
/settings/model → 设置页面 - 模型配置
/settings/appearance → 设置页面 - 外观
/settings/language → 设置页面 - 语言
/settings/api-docs → 设置页面 - API 文档
/settings/about → 设置页面 - 关于
```

## i18n 设计

### 语言文件结构

```typescript
// locales/zh-CN.ts
export default {
  app: {
    name: 'TinadecCode',
    connected: '已连接',
    disconnected: '未连接',
  },
  nav: {
    chat: '聊天',
    projects: '项目',
    settings: '设置',
  },
  sidebar: {
    projects: '项目',
    sessions: '会话',
    openProject: '打开项目',
    newSession: '新建会话',
  },
  chat: {
    title: 'Tinadec 会话',
    noProject: '未打开项目',
    placeholder: '让 TinadecCode 检查、规划或修改此项目...',
    send: '发送',
    ready: '准备接收第一个任务。',
  },
  context: {
    approval: '审批',
    diff: '差异',
    events: '事件',
    doctor: '诊断',
    noApprovals: '暂无待审批项',
    diffPlaceholder: '工作区差异将显示在此处。',
  },
  settings: {
    title: '设置',
    back: '返回',
    model: '模型配置',
    appearance: '外观',
    language: '语言',
    apiDocs: 'API 文档',
    about: '关于',
    baseUrl: '基础 URL',
    model: '模型',
    apiKey: 'API 密钥',
    apiKeyStored: '已存储',
    apiKeyNotSet: '未设置',
    save: '保存',
    theme: '主题',
    dark: '暗色',
    light: '亮色',
    system: '跟随系统',
  },
  approval: {
    approve: '批准',
    reject: '拒绝',
    request: '请求审批',
  },
  doctor: {
    title: '诊断',
  },
}
```

```typescript
// locales/en.ts
export default {
  app: {
    name: 'TinadecCode',
    connected: 'Connected',
    disconnected: 'Disconnected',
  },
  nav: {
    chat: 'Chat',
    projects: 'Projects',
    settings: 'Settings',
  },
  // ... (same structure, English values)
}
```

### 语言切换

- 标题栏右侧提供语言切换按钮（中/EN）
- 设置页面中提供语言选择
- 语言偏好持久化到 localStorage

## Elysia 能力利用

### 1. 偏好设置存储（前端优先）

用户偏好（主题、语言等）优先存储在 localStorage，确保不依赖后端即可工作。

未来增强：在 Elysia 网关新增 `/api/v1/preferences` 端点，实现跨设备/跨会话的设置同步。当前阶段仅预留接口定义，不实现后端端点。

### 2. Swagger 文档集成

在设置页面的"API 文档"标签中，通过 iframe 嵌入 Elysia Swagger UI（`http://127.0.0.1:48730/docs`），让用户在应用内直接查看 API 文档。

### 3. SSE 事件增强

利用现有 SSE 事件流，在设置页面中展示实时事件状态，增强用户对系统运行状态的感知。

## 新增依赖

| 依赖 | 用途 | 版本 |
|------|------|------|
| `vue-router` | 路由管理（主界面 ↔ 设置页切换） | ^4.x |
| `vue-i18n` | 国际化（中英双语） | ^10.x |
| `@vueuse/core` | 实用组合式函数（useDark, useStorage 等） | ^12.x |

**不引入**：Pinia（当前状态管理复杂度不高，用 Vue 3 reactive/ref 即可）、UI 组件库（保持轻量自定义）。

## 实施步骤

### 阶段 1：基础设施（预计 3 步）

1. **安装新依赖**：`vue-router`、`vue-i18n`、`@vueuse/core`
2. **创建 i18n 配置**：`i18n.ts` + `locales/zh-CN.ts` + `locales/en.ts`
3. **创建路由配置**：`router.ts`，定义 `/` 和 `/settings` 路由

### 阶段 2：主题系统（预计 2 步）

4. **重构 `styles.css`**：将所有硬编码颜色替换为 CSS 自定义属性，定义暗色/亮色主题变量
5. **创建 `useTheme.ts`**：主题切换逻辑，持久化到 localStorage

### 阶段 3：组件拆分（预计 8 步）

6. **创建 `AppHeader.vue`**：标题栏，品牌 + 导航图标 + 状态 + 语言切换
7. **创建 `AppSidebar.vue`**：左侧面板，项目列表 + 会话列表
8. **创建 `ChatPanel.vue` + `ChatHeader.vue`**：对话区容器和头部
9. **创建 `MessageList.vue` + `MessageItem.vue`**：消息流和单条消息
10. **创建 `ComposerBar.vue`**：输入框区域
11. **创建 `ContextPanel.vue`**：右侧上下文面板（标签页容器）
12. **创建 `ApprovalTab.vue` + `DiffTab.vue` + `EventsTab.vue` + `DoctorTab.vue`**：右侧面板各标签页
13. **创建 `StatusPill.vue`**：复用状态标签

### 阶段 4：设置页面（预计 2 步）

14. **创建 `SettingsPage.vue`**：全屏设置页面，左侧导航 + 右侧内容
15. **集成 Elysia Swagger**：在设置页 API 文档标签中嵌入 Swagger UI

### 阶段 5：主壳重构（预计 2 步）

16. **重构 `App.vue`**：用新组件替换原有模板，接入路由和 i18n
17. **重构 `main.ts`**：挂载 Vue Router 和 vue-i18n

### 阶段 6：验证与收尾（预计 2 步）

18. **功能验证**：确保所有现有功能正常工作（项目/会话/消息/审批/事件/诊断）
19. **视觉验证**：暗色/亮色主题切换、中英文切换、设置页面导航

## 关键设计决策

1. **导航图标放在标题栏**：参考 Codex/Cursor 3.0，导航图标（聊天、项目、设置）放在标题栏左上角品牌标识旁边，而非独立侧边栏
2. **暗色主题优先**：默认暗色，可在设置中切换亮色
3. **全屏设置页**：参考 Codex，设置页面全屏展示，通过路由切换
4. **右侧面板标签页化**：将当前右侧面板的多个 section 改为标签页切换，减少视觉噪音
5. **不引入 UI 组件库**：保持轻量，自定义 CSS 更贴合 Codex/Cursor 风格
6. **不引入 Pinia**：当前状态复杂度不高，Vue 3 原生响应式足够
7. **i18n 用 vue-i18n**：正规化多语言支持，未来可轻松扩展更多语言
