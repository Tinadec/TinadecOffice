> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# 智能体中心重构计划：模型选择修复 + Canvas 可视化关系图

## 问题分析

### 问题1：模型选择功能不工作
当前 `openAgentConfig()` 函数（SettingsPage.vue L301-308）的逻辑：
- 点击三个点按钮 → 设置 `configuringAgentId` → 展开下方 `agent-detail-panel`
- `agent-detail-panel` 中有 Provider 下拉框和 Model 输入框
- **问题**：`agentRouteProviderId` 初始化时如果 `providers` 列表为空或找不到匹配的 route，会设为空字符串，导致下拉框无法选中任何值
- `saveAgentRoute()` 依赖 `agentRouteProviderId` 非空才能保存

**修复方向**：确保 `openAgentConfig` 正确回填当前 route 的 provider 和 model，并在 providers 列表加载完成后正确匹配。

### 问题2：缺少智能体关系可视化
用户要求用 Canvas 画布呈现智能体之间的层级关系：
- **会议智能体（Chair）** 在中心/顶部，负责拉起所有执行智能体
- **规划智能体（Planner）** 与会议智能体交流、沟通并监督执行智能体
- **执行智能体（Executor）** 被会议智能体调度，被规划智能体监督
- 需要用连线+箭头展示这些关系

## 实施步骤

### 步骤1：修复模型选择功能
**文件**：`SettingsPage.vue`

1. 修复 `openAgentConfig()` 函数：
   - 当 `agentRoute(agent)` 返回 null（无匹配 route）时，默认选中第一个 provider
   - 当 `providers` 列表为空时，显示提示信息而非空下拉框
   - 确保 `agentRouteModel` 正确回填当前 route 的 model 值

2. 修复 `saveAgentRoute()` 函数：
   - 保存成功后刷新 `routes` 列表
   - 保存后重新回填表单值，确保 UI 与后端状态一致

3. 改善 `agent-detail-panel` 的 Provider 下拉框：
   - 当没有可用 provider 时显示禁用状态和提示文字
   - Provider 选项中显示连接状态标识

### 步骤2：创建 AgentTopologyCanvas 组件
**新文件**：`src/components/AgentTopologyCanvas.vue`

使用原生 HTML5 Canvas API（无需引入额外依赖）绘制智能体拓扑关系图：

1. **节点布局**：
   - 顶部中心：会议智能体（Chair）— 最大节点，品牌色高亮
   - 中层左右：规划智能体（Planner）— 中等节点
   - 底层：执行智能体（Executor）— 较小节点
   - 底部：进化候选（Candidate）— 虚线边框

2. **连线关系**：
   - Chair → Executor：实线箭头（调度/拉起）
   - Planner ↔ Chair：双向箭头（沟通/监督）
   - Planner → Executor：虚线箭头（监督）
   - Candidate → 对应层级：虚线连接

3. **交互**：
   - 鼠标悬停节点显示详细信息（tooltip）
   - 点击节点打开对应智能体配置面板
   - 节点拖拽调整位置
   - 缩放和平移画布

4. **视觉设计**：
   - 暗色模式适配：深色背景 + 发光节点
   - 节点颜色区分层级（规划层=绿色系、执行层=蓝色系、进化=紫色系）
   - 连线带动画效果（流动粒子表示数据流向）
   - 选中节点高亮边框

### 步骤3：集成 Canvas 到智能体中心
**文件**：`SettingsPage.vue`

1. 在智能体中心顶部（标题和刷新按钮下方）添加 Canvas 区域
2. 添加视图切换按钮：列表视图 / 拓扑视图
3. Canvas 组件接收 `agents`、`routes`、`providers` props
4. 点击 Canvas 节点时触发 `openAgentConfig()`

### 步骤4：添加 i18n 翻译
**文件**：`zh-CN.ts`、`en.ts`

新增翻译键：
- `settings.topologyView` / `settings.listView` — 视图切换
- `settings.agentTopology` — 拓扑图标题
- `settings.noProvidersHint` — 无可用模型实例提示
- `settings.clickToConfig` — 点击配置提示
- `settings.chairAgentDesc` — 会议智能体描述
- `settings.plannerAgentDesc` — 规划智能体描述
- `settings.executorAgentDesc` — 执行智能体描述

### 步骤5：添加 CSS 样式
**文件**：`styles.css`

1. `.agent-topology-container` — Canvas 容器样式
2. `.agent-view-toggle` — 视图切换按钮组样式
3. `.agent-topology-tooltip` — 悬停提示样式
4. 暗色/亮色模式适配

### 步骤6：验证构建
- `vue-tsc --noEmit` 类型检查
- `vite build` 构建验证

## 技术选型

- **Canvas 绘图**：使用原生 HTML5 Canvas API，无需引入额外 npm 依赖
- **动画**：使用 `requestAnimationFrame` 实现连线流动粒子效果
- **交互**：原生 Canvas 事件处理（mousedown/mousemove/mouseup/click）
- **响应式**：使用 `ResizeObserver` 监听容器尺寸变化，自动调整 Canvas 大小

## 节点布局算法

```
         ┌──────────────┐
         │  Chair Agent  │  ← 顶部中心
         └──────┬───────┘
                │ 调度
        ┌───────┼───────┐
        │       │       │
   ┌────┴──┐  ┌─┴──┐ ┌─┴────┐
   │Planner│  │Plan│ │Planr │  ← 中层
   └───┬───┘  └────┘ └──┬───┘
       │ 监督              │
   ┌───┴──────────────────┴───┐
   │                          │
 ┌─┴──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐ │
 │Exec│ │Ex│ │Ex│ │Ex│ │Ex│ │  ← 底层
 └────┘ └──┘ └──┘ └──┘ └──┘ │
   ┊ 虚线连接进化候选          │
```

## 预计修改文件

| 文件 | 操作 | 说明 |
|------|------|------|
| `SettingsPage.vue` | 修改 | 修复模型选择 + 集成 Canvas + 视图切换 |
| `AgentTopologyCanvas.vue` | 新建 | Canvas 拓扑图组件 |
| `styles.css` | 修改 | 添加 Canvas 相关样式 |
| `zh-CN.ts` | 修改 | 添加中文翻译 |
| `en.ts` | 修改 | 添加英文翻译 |
