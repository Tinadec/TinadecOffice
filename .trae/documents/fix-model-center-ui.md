> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# 模型中心 UI 修复与增强计划

## 问题诊断

通过代码审查，识别出以下具体问题：

### 问题 1：已添加的模型卡片点开后没有实质内容
**根因**：[SettingsPage.vue:469-484](file:///d:/github/TinadecCode/apps/desktop/src/pages/SettingsPage.vue#L469) 中，点击已添加的 provider 卡片调用 `openEditModal(provider)`，打开的是模态框。但模态框的"详情"体验很差——它只是一个表单，没有展示 provider 的运行状态、连接测试、使用统计等信息。用户期望点开卡片能看到丰富的管理界面，但实际只有几个输入框。

### 问题 2：模板添加模型不够直观
**根因**：模板卡片（[SettingsPage.vue:495-511](file:///d:/github/TinadecCode/apps/desktop/src/pages/SettingsPage.vue#L495)）只有文字 + Plus/CheckCircle2 图标，没有品牌 logo。20 个模板全部是纯文字卡片，视觉上难以区分。

### 问题 3：卡片缺少品牌图标
**根因**：[providerTemplates.ts](file:///d:/github/TinadecCode/apps/desktop/src/providerTemplates.ts) 定义了 `brand_color` 和 `brand_bg`，但没有 `icon` 或 `logo_url` 字段。卡片渲染时（[SettingsPage.vue:476-478](file:///d:/github/TinadecCode/apps/desktop/src/pages/SettingsPage.vue#L476)）只有文字，没有品牌标识图标。

## 实施步骤

### Step 1：为 ProviderTemplate 添加图标字段

在 `providerTemplates.ts` 中为每个模板添加 `icon` 字段，使用 SVG 字符串内联品牌 logo。

**修改文件**：
- `apps/desktop/src/providerTemplates.ts`

**具体操作**：
1. 在 `ProviderTemplate` 接口中添加 `icon: string` 字段（SVG 字符串）
2. 为 20 个模板逐一添加品牌 SVG 图标：
   - **云端 API**：OpenAI、Anthropic、Google Gemini、DeepSeek、OpenRouter、Groq、Together AI、Fireworks、xAI、Mistral、Cohere、Qwen、Azure OpenAI、AWS Bedrock、GitHub Copilot
   - **本地服务**：Ollama、vLLM、SGLang、LM Studio
   - **Agent CLI**：Codex CLI、Claude CLI、Cursor ACP、OpenCode
   - **自定义**：Custom

图标来源策略：使用简化的 SVG 路径，基于各品牌官方 logo 的简化版本（单色适配暗色/亮色主题）。

### Step 2：在模板卡片和已添加卡片中渲染图标

**修改文件**：
- `apps/desktop/src/pages/SettingsPage.vue`
- `apps/desktop/src/styles.css`

**具体操作**：
1. 模板卡片（`.model-provider-card.add`）中，在 `.add-label` 内添加图标渲染：
   ```html
   <span class="provider-brand-icon" v-html="template.icon"></span>
   ```
2. 已添加的 provider 卡片（`.model-provider-card`）中，在 `.model-provider-card-main` 前添加图标：
   ```html
   <span class="provider-brand-icon" v-html="findTemplate(provider.driver)?.icon ?? ''"></span>
   ```
3. 添加 CSS 样式 `.provider-brand-icon`：
   - 固定尺寸 28x28px
   - 圆角 6px
   - 居中对齐
   - SVG 内 `currentColor` 继承主题色

### Step 3：增强已添加 provider 卡片的详情面板

**修改文件**：
- `apps/desktop/src/pages/SettingsPage.vue`
- `apps/desktop/src/styles.css`

**具体操作**：
1. 将 `openEditModal(provider)` 改为展开一个内联详情面板（类似 Agent 中心的 `agent-detail-panel` 模式），而非弹出模态框
2. 详情面板内容包含：
   - **头部**：品牌图标 + 名称 + 状态 badge + 编辑/删除按钮
   - **连接信息**：Base URL、Model、连接方式（只读展示）
   - **API Key 状态**：已存储/未设置（不显示密钥值）
   - **能力标签**：capabilities 列表
   - **编辑按钮**：点击才打开编辑模态框
3. 添加 `selectedProviderDetailId` ref 控制展开/收起
4. 添加删除 provider 的功能（当前缺失）

### Step 4：优化模板添加流程的直观性

**修改文件**：
- `apps/desktop/src/pages/SettingsPage.vue`
- `apps/desktop/src/styles.css`

**具体操作**：
1. 模板卡片增加更丰富的视觉层次：
   - 品牌图标（Step 2 已添加）
   - 连接方式小标签（API Key / CLI / 本地）
   - 已添加状态更明显（当前仅 opacity: 0.5 + CheckCircle2）
2. 点击已添加的模板卡片时，跳转到该 provider 的详情面板而非忽略
3. 模板分类标题区域添加简短说明文字

### Step 5：添加删除 Provider 功能

**修改文件**：
- `apps/desktop/src/pages/SettingsPage.vue`
- `apps/desktop/src/api.ts`

**具体操作**：
1. 在 `api.ts` 中添加 `deleteModelProvider` 方法（调用 `DELETE /api/v1/model-providers/:id`）
2. 在详情面板中添加删除按钮（带确认）
3. 删除后刷新 provider 列表

## 文件修改清单

| 文件 | 修改类型 | 说明 |
|------|---------|------|
| `apps/desktop/src/providerTemplates.ts` | 修改 | 添加 `icon` 字段和 20 个品牌 SVG |
| `apps/desktop/src/pages/SettingsPage.vue` | 修改 | 图标渲染、详情面板、删除功能、模板交互优化 |
| `apps/desktop/src/styles.css` | 修改 | 图标样式、详情面板样式、模板卡片增强样式 |
| `apps/desktop/src/api.ts` | 修改 | 添加 `deleteModelProvider` 方法 |

## 不修改的文件

- 后端 API（`DELETE /api/v1/model-providers/:id` 端点已存在于 Core 的 Program.cs 中）
- i18n 文件（已有足够的翻译 key，新增少量即可）
- `ModelProviderCatalog.cs`（后端模板数据不需要图标）
