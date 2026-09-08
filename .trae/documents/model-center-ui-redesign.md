> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# 模型中心界面改造计划

## 现状问题

1. **CSS 样式缺失**：`SettingsPage.vue` 中使用了 20+ 个 `model-*` CSS 类（如 `model-center-heading`、`model-route-panel`、`model-provider-card` 等），但 `styles.css` 中没有任何对应样式定义，界面完全无样式
2. **英文 i18n 缺失**：`locales/en.ts` 中缺少模型中心相关的所有翻译（modelCenter、provider、driver、connectionKind 等），英文界面会显示 key 名
3. **界面繁杂**：当前模型中心将供应商列表和编辑器并排显示，信息密度高但不直观
4. **缺少厂商品牌识别**：12 个供应商模板（OpenAI、DeepSeek、OpenRouter、Groq、Together AI、Fireworks、Ollama、vLLM、SGLang、Codex CLI、Claude CLI、Cursor ACP、OpenCode）没有视觉区分

## 改造目标

1. **直观展示已添加的供应商**：卡片式布局，一眼看到所有已配置的供应商实例及其状态
2. **不繁杂**：信息层次清晰，核心信息优先展示，详情按需展开
3. **美观**：参考 Codex/Cursor/Trae Solo 的暗色主题设计风格
4. **支持多厂商 API**：为每个供应商模板提供品牌色和图标区分

## 设计方案

### 布局重构

```
┌──────────────────────────────────────────────────────────────┐
│ [← 返回]  设置                                    [中/EN]    │
├────────────┬─────────────────────────────────────────────────┤
│            │                                                 │
│  模型中心   │  ┌─ 聊天默认路由 ─────────────────────────────┐ │
│  外观       │  │ 🤖 DeepSeek · deepseek-chat    [设为默认]  │ │
│  语言       │  └───────────────────────────────────────────┘ │
│  API 文档   │                                                 │
│  关于       │  ── 已添加的供应商 ──────────────── [+ 添加] ── │
│            │                                                 │
│            │  ┌──────────┐ ┌──────────┐ ┌──────────┐        │
│            │  │ 🟢 OpenAI │ │ 🟢 DeepSeek│ │ 🟡 Ollama │        │
│            │  │ API Key   │ │ API Key   │ │ 本地服务  │        │
│            │  │ gpt-5.4   │ │ deepseek  │ │ llama3.2 │        │
│            │  └──────────┘ └──────────┘ └──────────┘        │
│            │                                                 │
│            │  ── 可添加的供应商 ─────────────────────────────  │
│            │                                                 │
│            │  ┌──────────┐ ┌──────────┐ ┌──────────┐        │
│            │  │ OpenRouter│ │   Groq    │ │ Together │        │
│            │  │  + 添加   │ │  + 添加   │ │  + 添加  │        │
│            │  └──────────┘ └──────────┘ └──────────┘        │
│            │  ┌──────────┐ ┌──────────┐ ┌──────────┐        │
│            │  │ Fireworks │ │  vLLM    │ │  SGLang  │        │
│            │  │  + 添加   │ │  + 添加   │ │  + 添加  │        │
│            │  └──────────┘ └──────────┘ └──────────┘        │
│            │  ┌──────────┐ ┌──────────┐ ┌──────────┐        │
│            │  │ Codex CLI │ │Claude CLI│ │Cursor ACP│        │
│            │  │  + 添加   │ │  + 添加   │ │  + 添加  │        │
│            │  └──────────┘ └──────────┘ └──────────┘        │
│            │                                                 │
└────────────┴─────────────────────────────────────────────────┘
```

### 添加/编辑供应商弹窗

点击"添加"或已有供应商卡片时，弹出模态编辑面板：

```
┌─────────────────────────────────────────┐
│  编辑供应商 - DeepSeek            [✕]   │
├─────────────────────────────────────────┤
│                                         │
│  驱动        [DeepSeek          ▼]      │
│  接入方式    [API Key          ▼]       │
│  显示名称    [DeepSeek               ]  │
│  基础 URL    [https://api.deepseek.com] │
│  模型        [deepseek-chat          ]  │
│  API 密钥    [••••••••••••]             │
│                                         │
│  ☑ 启用                                 │
│                                         │
│  能力: chat · streaming · reasoning     │
│                                         │
│  状态: 🟢 就绪                          │
│                                         │
│           [取消]  [保存]                 │
└─────────────────────────────────────────┘
```

### 供应商品牌色

| 供应商 | 品牌色（暗色主题） | 品牌色（亮色主题） |
|--------|-------------------|-------------------|
| OpenAI Compatible | `#10a37f` | `#10a37f` |
| DeepSeek | `#4d6bfe` | `#4d6bfe` |
| OpenRouter | `#6366f1` | `#6366f1` |
| Groq | `#f55036` | `#f55036` |
| Together AI | `#0081f1` | `#0081f1` |
| Fireworks AI | `#ff6b35` | `#ff6b35` |
| Ollama | `#c8a87c` | `#8b6914` |
| vLLM | `#7c3aed` | `#7c3aed` |
| SGLang | `#f59e0b` | `#d97706` |
| Codex CLI | `#10a37f` | `#10a37f` |
| Claude CLI | `#d97706` | `#b45309` |
| Cursor ACP | `#6366f1` | `#4f46e5` |
| OpenCode | `#22d3ee` | `#0891b2` |

## 实施步骤

### 步骤 1：补充英文 i18n 翻译

在 `locales/en.ts` 的 `settings` 对象中补充所有缺失的模型中心翻译 key：
- `modelCenter`, `modelCenterSubtitle`, `refresh`, `chatRoute`
- `providerInstances`, `newProvider`, `editProvider`
- `noProvider`, `noModel`, `driver`, `connectionKind`
- `connectionKindApiKey`, `connectionKindCli`, `connectionKindLocal`
- `displayName`, `enabled`, `binaryPath`, `homePath`
- `serverUrl`, `launchArgs`, `setChatDefault`
- `statusReady`, `statusNeedsKey`, `statusDisabled`, `statusNotConfigured`
- `addProvider`, `editProviderTitle`, `cancel`, `capabilities`
- `availableProviders`, `addedProviders`

### 步骤 2：补充中文 i18n 翻译

在 `locales/zh-CN.ts` 中补充新增的 key：
- `addProvider`, `editProviderTitle`, `cancel`, `capabilities`
- `availableProviders`, `addedProviders`

### 步骤 3：添加模型中心 CSS 样式

在 `styles.css` 中添加以下样式组：

1. **`.model-center-heading`** — 标题区（标题 + 副标题 + 刷新按钮）
2. **`.model-route-panel`** — 聊天默认路由横幅
3. **`.model-provider-grid`** — 供应商卡片网格（已添加 + 可添加）
4. **`.model-provider-card`** — 供应商卡片（品牌色左边框 + 状态点 + 名称 + 类型 + 模型）
5. **`.model-provider-card.add`** — 可添加供应商的虚线卡片
6. **`.model-status-chip`** — 状态标签（ready/needs_key/disabled/not_configured）
7. **`.model-section-header`** — 区域标题（标题 + 操作按钮）
8. **`.model-provider-modal`** — 模态编辑面板遮罩
9. **`.model-provider-modal-content`** — 模态编辑面板内容
10. **`.model-form-grid`** — 表单双列网格
11. **`.model-capability-row`** — 能力标签行
12. **`.model-provider-note`** — 状态提示行
13. **`.settings-checkbox`** — 复选框标签
14. **`select`** — 下拉选择框样式

### 步骤 4：重构 SettingsPage.vue 模型中心模板

将当前的"列表 + 编辑器并排"布局改为：

1. **顶部**：聊天默认路由横幅（当前默认供应商 + 设为默认按钮）
2. **中部**：已添加的供应商卡片网格（每个卡片显示品牌色、名称、类型、模型、状态）
3. **底部**：可添加的供应商卡片网格（虚线边框，点击弹出编辑模态框）
4. **模态框**：点击已有供应商卡片或"添加"按钮时弹出编辑面板

### 步骤 5：添加供应商品牌色映射

在 `SettingsPage.vue` 的 `<script>` 中添加 `brandColor(driver)` 函数，返回对应供应商的品牌色，用于卡片的左边框和图标着色。

### 步骤 6：验证

- TypeScript 类型检查通过
- Vite 构建通过
- 暗色/亮色主题下模型中心界面正常显示
- 中英文切换正常
- 添加/编辑/保存供应商功能正常
