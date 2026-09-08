> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# 智能体中心 (Agent Center) 界面改进方案 — 对齐模型中心设计规范

## 1. 现状问题与改进目标

通过对比 `SettingsPage.vue` 中模型中心（Model Center）的设计规范与现有智能体中心（Agent Center）的实现，梳理出以下核心差距与重构目标：

1. **三栏工作台与动态检查器（Inspector）脱节**：
   - 模型中心无论切换哪个资源 Tab，左侧 Inspector 始终联动显示当前选中项详情、健康检查卡片与诊断抽屉。
   - 智能体中心目前 Inspector 只在部分 Tab 工作，切换到 Prompt/Evolution 时大量留白或显示静态占位。
2. **表单与弹窗交互范式不统一**：
   - 存在内联展开卡片（Agent 新建）、固定遮罩 Modal（Evolution 晋升、Prompt 版本）和 `UiSheet`（Mode 节点）混用问题。
   - 目标：全面统一为右侧滑出抽屉（`UiSheet`）或标准化对话框（`UiDialog`）。
3. **筛选工具栏与控件风格不一致**：
   - 模型中心使用统一药丸按钮组（`role="group"`）和图标搜索条；智能体中心混用了原生 `<select class="settings-select">` 与零散输入框。
4. **缺少国际化（i18n）**：
   - 智能体中心存在大量硬编码中文与中英混杂文案，需要全量接入 `zh-CN.ts` 与 `en.ts`。
5. **视觉 Token 与主题色规范化**：
   - 清理内联 RGBA/Hex 硬编码颜色，对齐 `--surface-section`、`--surface-raised`、`--accent-*` 等全局面板材质系统。
6. **空状态与骨架屏补全**：
   - 补齐各 Tab 的加载态 `UiSkeleton` 与带有操作引导的 `.center-empty-state`。

---

## 2. 详细设计与实现计划

### 步骤 1：补齐国际化语言包 (`zh-CN.ts` & `en.ts`)
- 在 `apps/desktop/src/locales/zh-CN.ts` 与 `en.ts` 中新增 `agentCenter` 模块字典：
  - `kicker`、`title`、`subtitle`、`refresh`、`create` 等通用命令条文案。
  - `receipts`（配置库读写状态、拓扑模式激活数、运行时实例概况）。
  - `tabs`（智能体定义 `agents`、运行拓扑 `topology`、提示词引擎 `prompt`、演化提案 `evolution`、运行时信息 `info`）。
  - 各资源下的筛选标签、表单字段、验证提示、空状态文案与 Inspector 诊断字段。

### 步骤 2：重构顶部 Command Bar 与 3 格状态回执卡（Overview Receipts）
- **Header 命令栏**：
  - 左侧：上标 Kicker（`AGENT / 智能体`）、H2 标题与副标题。
  - 右侧：主操作按钮组（如根据当前 Tab 智能显示「新建智能体」/「新建拓扑」/「生成提案」）+ 带 Loading 动画的刷新按钮。
- **3 格状态回执卡**：
  - 卡片 1：**智能体配置库**（已发布数量 / 草稿数 / 读写状态）。
  - 卡片 2：**运行拓扑模式**（已发布 Mode 数量 / 当前会话生效拓扑）。
  - 卡片 3：**运行时调度**（活跃实例数 / 演化候选提案数 / 执行引擎状态）。

### 步骤 3：统一三栏工作台与全局自适应 Inspector
- **左侧 Inspector 改造**：
  - **顶部指标看板**：4 格概览指标（发布总数、活跃模式、候选提案、在线实例），状态异常时触发 `.attention` 强调色。
  - **自适应详情卡片（Dynamic Inspector Card）**：
    - 选中 Agent：显示头像/图标、Layer 标签（Operation/Execution）、类型、工具授权数、Capabilities 徽章、Revision 版本。
    - 选中 Mode：显示模式 ID、节点数、边连线数、默认模型。
    - 选中 Prompt 片段/管线：显示 Fragment 作用域、Token 估算、效能评级。
    - 选中 Evolution 提案：显示观察模式、推荐工具列表、置信度。
    - 选中 Runtime 实例：显示 Run ID、当前状态（running/blocked/idle）、心跳时间。
  - **诊断折叠抽屉（Diagnostics Collapsible）**：展示 Manifest 工具摘要、冲突版本检测与内核治理规则提示。

### 步骤 4：标准化中间 Rail 导航栏与 Stage 筛选工具栏
- **Resource Rail 导航栏**：
  - 5 个垂直药丸 Tab：图标 + 标题 + 数量 Badge（`UiBadge variant="secondary"`），选中项带有主题色边框与发光投影。
- **Stage 筛选工具栏（`.model-provider-toolbar`）**：
  - 将所有原生 `<select>` 替换为统一药丸过滤按钮组（Layer 过滤：全部/运营层/执行层；状态过滤：全部/草稿/已发布/已归档）。
  - 规范化搜索框（带清空按钮和前置图标）与右侧数量统计显示（`匹配 X / 总共 Y`）。

### 步骤 5：收敛表单与模态交互为 `UiSheet` / `UiDialog`
- **Agent 编辑抽屉**：由原来的表格下方展开改为右侧滑出 `UiSheet`，支持 Manifest 工具快速点选器、能力标签增删、System Prompt 高度自适应编辑。
- **Evolution 晋升模态**：使用 `UiDialog` 规范化弹窗，提供智能体名称、描述、授权工具预览与一键创建发布流程。
- **Prompt 新版本创建**：统一使用规范化的弹出对话框。

### 步骤 6：视觉规范与空状态/骨架屏优化
- 清理 `AgentCenterPage.vue` 中的内联 RGBA/Hex 样式，统一迁移使用 `settings.css` 类名与 CSS 变量。
- 在数据加载中显示 4 行卡片骨架屏（`UiSkeleton`）。
- 在数据为空或筛选无结果时展示居中空状态卡片（`.center-empty-state`），包含大图标、友好提示与「新建」/「重置筛选」引导按钮。

---

## 3. 受影响文件清单
1. `apps/desktop/src/pages/AgentCenterPage.vue` — 主页面结构与样式重构。
2. `apps/desktop/src/locales/zh-CN.ts` & `apps/desktop/src/locales/en.ts` — 补齐完整的智能体中心中英文字典。
3. `apps/desktop/src/components/canvas/AgentModeCanvas.vue` & `PromptPipelineCanvas.vue` — 确保画布容器样式与主题 Token 严格协调。

---

## 4. 验证方式
1. **界面检查**：启动桌面端，检查 5 个 Tab（智能体定义、拓扑画布、提示词管线、演化提案、运行时实例）的视觉效果、选中项与左侧 Inspector 联动、抽屉与弹窗过渡。
2. **主题与响应式测试**：切换浅色/深色主题及不同的面板材质（Glass/Solid），确认无颜色断层或对比度异常。
3. **国际化切换测试**：切换中英文语言，确认所有文案均正确翻译且无截断。
4. **自动化测试**：运行 `npm --prefix apps/desktop test` 确保无前端回归。