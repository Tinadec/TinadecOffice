> **⚠️ 已归档规格（未实现或已过时）**
> 本文描述的功能多数**在代码中不存在**（例如 .trae/specs 中标注"已完成 ✅"的能力无任何源码/测试对应）。仅作历史记录，请勿据此判断当前能力。

# 智能体中心重设计 — 对标模型中心 · 实施计划

**Branch:** `codex/DmaEA`  
**Date:** 2026-08-28  
**Author:** internal-model  
**Status:** Draft — 待 Q1-Q4 确认后进入并行实现  
**Scope:** `apps/desktop/src/pages/AgentCenterPage.vue` + 相关子组件/样式/i18n；不含 `TinadecCore` / `TinadecGateway` 合约变更

---

## 1. Context

### 1.1 背景
- 需求：参考模型中心的设置界面，设计智能体中心。智能体中心 5 模块：**智能体配置、智能体运行图、提示词引擎（原设置页“提示词工程 + 提示词上下文”合并）、智能体演化（原“智能体进化”）、智能体信息**。
- 约束：四层架构不变（Core 唯一真源，Desktop 只读 Gateway），提示词引擎与进化能力从 `SettingsPage.vue` 迁入智能体中心，`AGENTS.md` 要求统一术语 `operation/execution`。
- 现状：`AgentCenterPage.vue:1-409` 已有 5 Tab 原型（`agents|modes|prompts|candidates|instances`），但与模型中心的 **Command Bar → Overview Receipt → 3 栏 Workbench** 信息架构差距大（见 §3 审计）。

### 1.2 目标
- 智能体中心达到模型中心同等的**信息层级、检索效率、健康可观测性、操作一致性**。
- 5 模块各自具备完整的 **搜索/过滤/排序/空状态/诊断** 闭环，复用 `settings.css` 与 `components/ui/*`，不引入新依赖。
- 消除当前表单粗糙、全局单选错位、`window.prompt` 断裂、版本不可视等缺陷。

### 1.3 非目标
- 不新增 Core/Gateway DTO 或持久化逻辑；不改 `router.ts` 路由结构（仍为 `/agent-center` 单页，内部 Rail 切换）。
- 不引入 Pinia/新状态库；沿用 `ref/computed` + `api.ts` `request()`。
- 不重做 `TinadecUIE` 布局引擎。

---

## 2. 对标物 — 模型中心模式清单

> 详见 `SettingsPage.vue:1949-2600` 与 `settings.css:1500-2000`。提取可复用的 10 个模式。

| # | 模式 | 在模型中心的体现 | 文件定位 | 复用优先级 |
|---|------|------------------|----------|------------|
| M1 | **Command Bar** | `center-command-bar`：kicker + h2 + subtitle + 右侧 `Add Provider / Refresh` | `SettingsPage.vue:1950` | ★★★ |
| M2 | **Overview Receipt** | `center-overview-receipt` 3 格：`provider_crud` 可写/只读、`catalog_mode`、`live_discovery` | `:1969` | ★★★ |
| M3 | **Workbench 三栏** | `aside.center-inspector`（健康+详情） + `aside.center-resource-navigation`（资源分类 Tab 带计数） + `main.center-resource-stage` | `:2017` | ★★★ |
| M4 | **Health 概览** | `model-health-overview`：4 指标格 + alert 条 | `:2027` | ★★☆ |
| M5 | **Diagnostics 折叠** | `details.model-diagnostics`：receipt id / blocked routes / catalog warnings / design_notes | `:2078` | ★★☆ |
| M6 | **Resource Rail** | 带 `UiBadge` 计数的垂直 Tab（api/models/cli/acp） | `:2202` | ★★★ |
| M7 | **Stage 工具** | API：搜索框 + `all/issues/configured/available` pill + 表格；Models：按 provider 分组；CLI：自发现面板 `found/configured/missing` + quick-connect | `:2241/2218/2288` | ★★★ |
| M8 | **可展开详情** | `provider-detail-panel`：brand icon + driver + grid + actions | `:2135` | ★★☆ |
| M9 | **空/加载态** | `UiSkeleton` + `center-empty-state` + `center-loading-state` | `:2000` | ★★☆ |
| M10| **品牌图标** | `.provider-brand-icon` 24px/32px + `v-html` SVG | `:2129` | ★☆☆ |

**视觉 token**（`settings.css`）：`--surface-section/raised/chrome/input`、`--border-muted/default`、`--text-primary/secondary/muted`、`--accent-*`、`--material-filter-*`、卡片无边框靠 `background` 区分、表头 `surface-chrome`、行悬停 `surface-hover`、选中 `surface-selected`。

---

## 3. 现状审计 — 5 Tab 逐项（证据定位）

### 3.1 智能体 `agents`（`AgentCenterPage.vue:210-260`）

| 维度 | 现状 | 证据 |
|------|------|------|
| 卡片 | `card-grid` + `UiCard`，每卡 4 按钮无主次，`allowed_tools` 逗号拼接溢出，`status` 同色 | `:219-231` |
| 表单 | `allowed_tools/capabilities` 纯逗号输入，无校验；`etag` 可编辑 | `:246-247,250` |
| 检索 | 无搜索/过滤/排序 | — |
| 版本 | `JSON.stringify(v).slice(0,180)` 不可读 | `:235` |
| Inspector | 无 | — |

### 3.2 智能体模式 `modes`（`:263-305` + `AgentModeCanvas.vue:1-120`）

| 维度 | 现状 | 证据 |
|------|------|------|
| 画布 | 420px、双泳道浅灰区分，无列标题吸顶/图标/端口提示 | `canvas:108` |
| 节点 | 硬编码 `firstAgent`、随机位置，无拖入 | `:114-117` |
| 边 | 仅创建，无删除/标签编辑/校验 | `canvas:49-53` |
| 选择器 | 原生 `select`，与 Rail 计数 Tab 差距大 | `:276` |
| 版本 | `listAgentModeVersions` API 已有，未暴露 | `api.ts:1316` |
| 覆写 | 仅 `agent_id+label`，缺 `lane/role/prompt/model` 覆盖 | `:290-304` |

### 3.3 提示词引擎 `prompts`（`:307-331`）

| 维度 | 现状 |
|------|------|
| 合并 | `PromptEngineeringPanel` 的版本/生效/信号/A-B/回滚、`promptContext` 的 `previewPromptContext` 均未迁入；仅 `name/desc/scope` |
| 过滤 | 无 `scope/category/target_agent_id/enabled` 过滤，无 `is_builtin/priority` |
| 画布 | “复用模式页基建”仅文字，无实现 |

### 3.4 候选智能体 `candidates`（`:334-351`）

| 维度 | 现状 |
|------|------|
| 选择 | `candidateModePick` 全局单选，错位（应行级） |
| 信息 | 无 `confidence_score/observed_patterns/evaluation_notes/generated_by` |
| 生成 | 无 `generateEvolutionProposals(session_id+lookback)` 入口 |
| 交互 | 晋升/拒绝无确认、无原因输入 |

### 3.5 运行实例 `instances`（`:353-372`）

| 维度 | 现状 |
|------|------|
| 筛选 | 仅 `run_id` 文本框 + 3s 轮询，无会话/状态/分页 |
| 改派 | `window.prompt` 收集 `session_id/interaction_id`，体验断裂 |
| 控制 | `pause/resume/cancel` 三按钮无确认、无 loading、无色区 |

### 3.6 整体

- 5 个 pill `border-radius:999px` 与模型中心垂直 Rail 不统一；无 Command Bar / Overview Receipt / Health / Diagnostics 顶层框架。

---

## 4. 架构设计 — 对标模型中心的智能体中心

### 4.1 页面框架（复用 M1-M3）

```
AgentCenterPage.vue
├─ .center-command-bar                          // M1
│   ├─ 左：kicker "智能体中心" + h2 + subtitle
│   └─ 右：[新建智能体] [新建模式] [刷新]       // 对标 Add Provider / Refresh
├─ .center-overview-receipt                      // M2 — 3 格
│   ├─ 智能体配置：{published}/{total} + 可写标识
│   ├─ 模式发布：已发布/草稿计数
│   └─ 演化：proposed 候选数 + 待处理
├─ .center-workbench                            // M3 — 3 栏 grid
│   ├─ aside.center-inspector                  // M4+M5：健康 + 选中详情 + 诊断折叠
│   ├─ aside.center-resource-navigation        // M6：Rail 5 项带计数 Badge
│   └─ main.center-resource-stage              // M7：按 Rail 选中渲染 5 模块之一
└─ (可选) .agent-center-diagnostics            // M5 折叠
```

**Rail 5 项**（对标 `modelCenterSections`）：

| key | label | count 来源 | 图标 |
|-----|-------|------------|------|
| `config` | 智能体配置 | `agents.length` | `Bot` |
| `topology` | 运行图 | `modes.length` | `Workflow` |
| `prompt` | 提示词引擎 | `pipelines.length` | `FileText` |
| `evolution` | 智能体演化 | `candidates.filter(s==='proposed').length` | `Dna` |
| `info` | 智能体信息 | `runtimeInstances.length` | `Info` |

单选 Rail 驱动 Stage 切换（与模型中心一致）。计数用 `UiBadge variant="secondary"`。

**样式复用**：直接复用 `settings.css` 的 `.center-*`、`.model-health-*`、`.model-diagnostics*` 类，仅新增 `.agent-center-*` 覆盖差异（保持与模型中心同色/同圆角/同间距）。

### 4.2 模块 A — 智能体配置（对标 M7 API 段 + M8 详情）

**工具栏**（复用 `model-provider-toolbar`）：
- 搜索框：`UiInput` + `Search` 图标，匹配 `name/agent_type/description`。
- Pill 组 1：`all | operation | execution`（`agentLayerLabel`）。
- Pill 组 2：`all | draft | published | archived`（`status`）。
- 计数：`显示 {visible}/{total}`（`--text-muted 10px`）。

**列表**：复用 `model-provider-table` 表格形态（5 列：智能体 | 类型 | 工具数 | 状态 | 操作），支持展开行。
- 表格列：智能体（`Bot` 图标 + name + layer Badge） | `agent_type`（`agentTypeLabel`） | 工具数（`allowed_tools.length`） | 状态（`readinessVariant` 映射 draft→secondary/published→default/archived→outline） | 操作（编辑/发布/归档/版本）。
- 卡片视图（可选）：保留 `card-grid` 作为表格的响应式回退（`<900px` 单列，沿用 `settings.css:1977` 断点）。

**可展开详情**（复用 `provider-detail-panel`）：
- `capabilities` tag 行、`allowed_tools` tag 行、`system_prompt` 折叠（`UiCollapsible`）、`revision/etag` 只读、`updated_at`、`version timeline`（`listAgentVersions` 按 `version desc`，`is_active` 高亮）。

**表单抽屉**（`UiSheet` 或 `UiCard` drawer）：
- `name` `UiInput` + `layer` `UiSelect(operation/execution)` + `agent_type` `UiSelect`（枚举 `meeting/context-compressor/...` 复用 `agentTypeLabel`）。
- `allowed_tools` 改为带搜索的 **tag 选择器**（复用 `toolCatalog.ts:manifestTools` + `filteredAgentTools` 逻辑，不再逗号输入）。
- `capabilities` tag 输入、`system_prompt` `UiTextarea:rows=4` 带字符统计、`enabled` `UiSwitch`。
- 保存时 `If-Match: etag`（`api.updateAgentDraft` 已支持），`etag` 输入框移除，仅回显。

### 4.3 模块 B — 智能体运行图（对标 M4 健康 + M6 Rail + VueFlow）

**Inspector 健康**（复用 `model-health-overview`）：
- 指标格：`nodes / edges / 未绑定节点 / 跨层复用警告 / 空生效工具`（4 格，`model-health-metrics` 样式）。
- 警告条：`CROSS_LAYER_REUSE` / `EMPTY_EFFECTIVE_TOOLS`（`model-health-alert` 红色）。

**主舞台**：
- 顶部：模式列表改为 Rail 风格（带 `status` Badge + `revision` + 搜索），而非单一下拉（`SettingsPage:276` 的原生 select 淘汰）。
- 画布升级（基于 `AgentModeCanvas.vue:74-114`）：
  - **列标题吸顶**：`operation` / `execution` 带图标（`Workflow`/`Cpu` 沿用 `AgentTopologyCanvas`）+ 节点计数。
  - **节点**：`type: custom` 卡片，`agent.name + role + 能力缩略 + 工具数`，选中高亮与 Inspector 联动。
  - **边**：选中/删除/标签编辑，校验循环与孤岛，连线时校验 `lane` 约束。
  - **工具栏**：`添加节点`（从配置拖拽或下拉选智能体）+ `保存草稿` + `发布` + `版本历史` 抽屉（`listAgentModeVersions`）。
- **节点覆写 Sheet**：`UiSheet`，字段 `agent_id`（带搜索选择器）+ `label` + `lane` 只读 + `提示词覆写` + `模型策略覆写`（inherit/fixed/parent_select）+ `工具覆写` tag。

### 4.4 模块 C — 提示词引擎（组合 PromptEngineeringPanel + promptContext）

**直接复用** `PromptEngineeringPanel.vue:237-530` 的 3 区布局：

| 区 | 内容 | 复用来源 |
|----|------|----------|
| 左列 | fragment 列表（搜索 + `scope/category/target_agent_id/enabled` 过滤 + `is_builtin` 标识 + `priority` 排序） | `pe-fragment-list` |
| 中列 | 详情卡（`key/scope/category/enabled`）+ Current Content + Version History（`change_summary/changed_fields/active` + rollback）+ Record Signal + A/B Compare | `pe-detail*` |
| 底部 | Effectiveness Overview（分数条 + 信号计数） | `pe-overview-*` |

**新增 — 上下文预览**（Settings 的 `previewPromptContext`）：
- 入口：`agent_id / mode / session_id / run_id / user_content` 5 输入 + `Preview` 按钮。
- 返回：`fragments / context_pack_ids / estimated_tokens / system_prompt / warnings`，用 `provider-detail-grid` 样式展示。

**数据**：`api.listPromptFragments / listPromptFragmentVersions / getPromptFragmentEffectiveness / previewPromptContext` 已具备。

### 4.5 模块 D — 智能体演化（对标 CLI Discovery + AgentEvolutionPanel）

**参照**：`cli-discovery-panel`（扫描+候选卡 `found/configured/missing`）+ `AgentEvolutionPanel:219-430` 的生成/列表/详情/晋升模态。

- **生成卡**：`Session ID` + `Lookback Events` + `Generate Proposals` 按钮（`evolution-generate-card`）。
- **候选网格**：`repeat(auto-fill, minmax(280px,1fr))`，卡片含 `layer icon + name + agent_type + confidence Badge(confidenceVariant) + description 2 行截断 + status + generated_by`，点击选中。
- **详情面板**：`observed_patterns / suggested_tools / evaluation_notes` 分区，`Proposed/Evaluating` 时显示 `Promote to Agent` + `Reject(reason)`。
- **晋升模态**：`agent_id / mode / model_route_purpose / allowed_tools tag / capabilities tag / system_prompt`（`evolution-promote-modal`）。
- **关键修复**：`target_mode_draft_id` 改为**行级/模态内**选择，移除全局 `candidateModePick`（`AgentCenterPage.vue:157` 缺陷）。

### 4.6 模块 E — 智能体信息（对标 Inspector + Health）

**参照**：`center-inspector` + `model-health-overview`：

- **健康概览**：指标格 `总智能体 / 已启用 / 草稿待发布 / 候选待处理`（`model-health-metrics` 3-4 列），警告条展示 `CROSS_LAYER_REUSE / EMPTY_EFFECTIVE_TOOLS`。
- **只读绑定表**：当前有效运行来源（若保留 `agent-center/overview` 的 `runtime_binding`）或 `harness/manifest` 的 `agent_layers` 摘要。
- **就绪回执**：`receipt_id / generated_at / providers/routes` 列表（`model-readiness` 表格形态转 `agent readiness`）。
- **诊断折叠**：`CenterDiagnosticDto` 列表（`model-diagnostics-grid`）。

---

## 5. 共享改进（跨 5 模块）

| 改进 | 模型中心先例 | 落点 |
|------|-------------|------|
| 统一搜索 + pill 过滤 + 计数 | `model-provider-toolbar` | 各 Stage 工具栏 |
| 健康指标 + 警告条 | `model-health-overview` | Overview Receipt + Inspector |
| 可折叠诊断 | `details.model-diagnostics` | 底部 `agent-center-diagnostics` |
| 骨架与空状态 | `UiSkeleton` + `center-empty-state` | 各 Stage loading/empty 插槽 |
| 可展开详情 | `provider-detail-panel` 插入行下方 | 智能体/模式/提示词行展开 |
| 计数 Badge | `modelCenterSections[].count` | Rail 5 项右侧 Badge |
| 响应式 | `@media (max-width: 900px)` 单列 | 复用同一断点 |
| 通知 | `useNotifications` | 保持 `notify/status/confirm` |

---

## 6. 数据与 API 映射

| UI 模块 | 读取 API | 写入 API | 现有覆盖 |
|---------|----------|----------|----------|
| 配置 | `listAgentDefinitions` | `createAgentDraft / updateAgentDraft / publishAgent / archiveAgent / listAgentVersions` | 已有，需补搜索/过滤前端 |
| 运行图 | `listAgentModeTopologies / getAgentModeTopology / listAgentModeVersions` | `createAgentModeDraft / updateAgentModeDraft / publishAgentMode / archiveAgentMode` | 已有，需补版本抽屉 |
| 提示词 | `listPromptPipelines / getPromptPipeline / listPromptPipelineVersions / listPromptFragments / previewPromptContext` | `createPromptPipelineDraft / updatePromptPipelineDraft / publishPromptPipeline` | 需合并 `PromptEngineeringPanel` 的 `listPromptFragments` 族 |
| 演化 | `listCandidates / listEvolutionProposals / generateEvolutionProposals` | `promoteCandidate / rejectCandidate / promoteAgentCandidate` | 已有，需补生成卡 |
| 信息 | `getAgentCenterOverview / getToolLayerReadiness / getHarnessManifest / listRuntimeInstances` | — | 已有 |
| 实例 | `listRuntimeInstances(run_id?)` + `generatedApi.controlRun` | `reassignInteraction / cancelInteraction` | 需替换 `window.prompt` 为表单 |

> 注意：`getAgentCenterOverview` 为 `@deprecated`（`api.ts:1293`），但仍是当前聚合入口。智能体中心应逐步直连新 5-tab 端点，保留 overview 仅作信息模块的兼容回退。

---

## 7. 实施计划（分阶段，可并行子智能体）

### Phase 0 — 基座（1 人日）
- [ ] `AgentCenterPage.vue` 骨架改为 `center-command-bar + overview-receipt + workbench(Inspector|Rail|Stage)`，移除现有 5 pill
- [ ] `activeTab: TabKey` 改为 `activeSection: 'config'|'topology'|'prompt'|'evolution'|'info'`，Rail 单选驱动 Stage
- [ ] 抽 `useAgentCenter` composable（可选）：`agents/modes/pipelines/candidates/instances/health` 聚合，避免单文件 400+ 行继续膨胀
- [ ] `settings.css` 新增 `.agent-center-*` 覆盖，复用 `.center-*` / `.model-health-*` / `.model-diagnostics*`

**验收**：页面呈现模型中心同款 3 栏，Rail 计数正确，Stage 切换无闪烁。

### Phase 1 — 智能体配置（2 人日，可与 Phase 2 并行）
- [ ] 工具栏：搜索 + `operation/execution` + `draft/published/archived` 双 pill + 计数
- [ ] 表格 + 可展开详情（含 `capabilities/allowed_tools/system_prompt/version timeline`）
- [ ] 表单 tag 化（`allowed_tools` 搜索选择器，`capabilities` tag 输入），移除可编辑 `etag`
- [ ] 空/加载态、`UiSkeleton`、`center-empty-state`

**验收**：100 个智能体下搜索 <100ms，`allowed_tools` 不再溢出，`etag` 冲突提示正确。

### Phase 2 — 运行图（2 人日，可与 Phase 1 并行）
- [ ] `AgentModeCanvas.vue` 升级：列标题吸顶、节点 custom 卡、边编辑/删除、校验（循环/孤岛/跨泳道）
- [ ] Inspector 健康指标 + 警告条
- [ ] 模式列表 Rail 化 + 搜索，节点覆写 `UiSheet`（`agent_id/label/lane/提示词/模型/工具`）
- [ ] 版本历史抽屉（`listAgentModeVersions`）

**验收**：拖拽/连线/保存/发布闭环，`publishMode` 413 校验可见，未绑定节点高亮。

### Phase 3 — 提示词引擎（1.5 人日）
- [ ] 迁入 `PromptEngineeringPanel` 的 fragment 列表/详情/版本/信号/A-B/生效总览（样式 `pe-*` 复用或重命名为 `prompt-*`）
- [ ] 合并 `previewPromptContext` 预览区（5 输入 + 返回展示）
- [ ] 过滤：`scope/category/target_agent_id/enabled` + `is_builtin/priority` 排序

**验收**：`listPromptFragments` 与 `previewPromptContext` 在同一 Stage 可操作，`is_builtin` 不可编辑。

### Phase 4 — 演化（1 人日）
- [ ] 生成卡（`session_id + lookback + Generate`）
- [ ] 候选网格 + 详情 + 晋升模态（行级 `target_mode_draft_id`）
- [ ] `rejectCandidate` 原因输入 + `confirm` 二次确认
- [ ] 移除全局 `candidateModePick`，`window.prompt` 清理

**验收**：`proposed → promoted/rejected` 状态流转可见，`confidence`  Badge 正确。

### Phase 5 — 信息 + 实例（1 人日）
- [ ] 健康概览（`total/enabled/draft/proposed` 4 格 + 警告条）
- [ ] 就绪回执 / 诊断折叠
- [ ] 运行实例：会话/状态筛选、分页、`reassign` 表单化、`pause/resume/cancel` 带 `confirm` + loading

**验收**：`instances` 无 `window.prompt`，轮询可启停，控制操作有二次确认。

### Phase 6 — 抛光（0.5 人日）
- [ ] `zh-CN.ts#settings` 补 `agentCenter*` key（`agentCenterTopologyHint` 等）
- [ ] 响应式（`<900px` 单列）、键盘可达性（`aria-selected/aria-pressed`）、空状态插画
- [ ] `settings.css` 清理：移除未用 `.agent-card` 旧样式，统一 `--surface-*` token

**总计**：约 8-9 人日，Phase 1/2 可并行，Phase 3/4 可并行。

---

## 8. 风险与对策

| 风险 | 影响 | 对策 |
|------|------|------|
| `AgentModeCanvas` 自定义节点引入回归 | 画布交互失效 | 保留 `AgentModeCanvas.vue` 为独立组件，新增 `AgentModeNodeCard.vue`，旧逻辑分支保留 1 轮 |
| `getAgentCenterOverview` 废弃后数据缺口 | 信息模块无数据 | 信息模块优先直连新端点，overview 仅作回退；`catch` 中降级 |
| 提示词引擎两套 API（`prompt-pipelines` vs `prompt-fragments`）语义混淆 | 表单字段错位 | 以 `PromptPipelineDto` 为主，`PromptFragmentDto` 仅在预览/生效模块引用，文档中明确映射 |
| 样式与模型中心漂移 | 视觉不一致 | 强制复用 `.center-*` / `.model-health-*` 类，不新增色板；Code Review 中以 `rg "var\(--"` 校验 token 偏离 |
| 单文件膨胀 | 可维护性 | Phase 0 抽 `useAgentCenter` 或按模块拆 `components/agent-center/*` |

---

## 9. 待确认问题（需用户定夺）

| # | 问题 | 选项 | 建议 |
|---|------|------|------|
| Q1 | **Rail 交互**：是否“单选 Rail = 单模块独占主舞台”，还是“配置+运行图”同屏分栏？ | A 单选独占 / B 同屏分栏 | **A**（与模型中心一致，认知成本低） |
| Q2 | **提示词画布**：是否在本次一并引入 `PromptPipeline` 可视化流水线（Vue Flow）？ | A 本次引入 / B 列表+版本先行，画布下一轮 | **B**（更快交付，样式已就绪） |
| Q3 | **运行实例归属**：实时控制（pause/resume/cancel/改派）是否收敛到 Workbench/发送框，智能体中心仅追溯？ | A 智能体中心仅追溯 / B 保留控制 | **A**（避免与 `WorkbenchPage` 重复） |
| Q4 | **术语**：是否本次统一 `planning → operation`（`normalizeAgentLayer` 已兼容）并收敛 `agent_type` 枚举展示？ | A 本次统一 / B 保留兼容 | **A**（`AGENTS.md` 已要求） |

> 请对 Q1-Q4 给出选择（可多选或补充），确认后即可按 Phase 0→6 并行推进。

---

## 10. 验证清单

### 10.1 自动化
```bash
npm run test -w @tinadec/desktop   # Vitest 布局/过滤/版本回退单测
npm run build -w @tinadec/desktop  # Vite 构建（Tailwind / VueFlow 样式无丢失）
```

### 10.2 手工
- [ ] 模型中心与智能体中心的 Command Bar / Receipt / Workbench 3 栏在 1280px 与 900px 下对齐
- [ ] 各 Stage 的搜索 + pill 过滤 + 计数 + 空状态 + 骨架均可操作
- [ ] 运行图：拖拽/连线/删除/保存/发布/版本回退/校验警告闭环
- [ ] 提示词：版本创建/回滚/信号/A-B/预览 5 动作均有 `notify` 反馈
- [ ] 演化：生成→选中→晋升/拒绝（含原因）→ 候选数 Badge 更新
- [ ] 实例：`window.prompt` 已移除，改派为表单，控制有二次确认
- [ ] `zh-CN` / `en` 切换无缺 key

---

## 11. 附录 — 关键文件索引

| 文件 | 行号 | 作用 |
|------|------|------|
| `apps/desktop/src/pages/SettingsPage.vue` | 1949-2600 | 模型中心 Command/Receipt/Workbench/Health/Diagnostics 范本 |
| `apps/desktop/src/pages/AgentCenterPage.vue` | 1-409 | 现状基座 |
| `apps/desktop/src/components/canvas/AgentModeCanvas.vue` | 1-120 | 画布基座 |
| `apps/desktop/src/components/AgentTopologyCanvas.vue` | 1-129 | 拓扑分层展示参考 |
| `apps/desktop/src/components/PromptEngineeringPanel.vue` | 1-988 | 提示词版本/生效/A-B 范本 |
| `apps/desktop/src/components/AgentEvolutionPanel.vue` | 1-728 | 演化生成/晋升范本 |
| `apps/desktop/src/api.ts` | 632-760 | 5-tab DTO 与端点 |
| `apps/desktop/src/settings/settings.css` | 1500-2000 | 中心样式 token |
| `apps/desktop/src/locales/zh-CN.ts` | 308-500 | `settings.*` 文案 |

---

## 12. 下一步

1. 用户确认 Q1-Q4（或补充约束）。
2. 按 Phase 0 起推进，Phase 1/2、3/4 并行子智能体；主控负责总览、审计、测试与冲突纠偏。
3. 完成后更新 `AGENTS.md` 的智能体中心章节与 `docs/architecture.md` 的 Desktop 面板描述。

