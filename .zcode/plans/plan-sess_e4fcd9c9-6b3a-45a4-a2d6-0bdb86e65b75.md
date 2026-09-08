> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# 模式版本条合并进常驻发送框

## 现状
- docked 态发送框上方悬浮着 `SessionModeStrip`（模式版本原生 select：跟随默认 + 已发布 ModeVersion 列表，选中即写入 `modeVersion_id`，随下次发送持久化到会话）。
- 它是发送框之外的独立条，破坏"一个发送框"的整体感。

## 改动（4 个文件 + 1 个删除）

### 1. `ComposerBar.vue` — 吸收模式版本选择器
- 工具栏左侧插入紧凑版原生 select（位于模式选择器之后）：选项不变（跟随默认 + `listAgentModeTopologies()` 的已发布拓扑），挂载时加载一次。
- 新增 emit `update:modeVersionId`；`modeVersionId` 仍由 ChatPanel 持有（会话切换时重置），组件纯展示。
- 样式：透明背景、hover 高亮，与工具栏其他触发器一致；`title="模式版本"` 提示；窄屏收窄 max-width。hero 态同样显示（默认"跟随默认"，无害）。

### 2. `ChatPanel.vue` — 移除 SessionModeStrip
- 删除该组件的 import、模板节点和 `onStripModeChange`；把 `modeVersionId` 以 props/emit 直连 ComposerBar。
- 删除已无引用的 `SessionModeStrip.vue` 文件。

### 3. 测试
- `ComposerBar.test.ts`：mock `@/api`（listAgentModeTopologies），新增用例：渲染模式版本 select、change 时 emit `update:modeVersionId`。
- `ChatPanel.test.ts`：移除 SessionModeStrip stub。

### 4. 文档补账（上轮被权限拦截的两项一并完成）
- `apps/desktop/AGENTS.md` + 根 `AGENTS.md`：常驻发送框架构条目 + 本轮模式版本合并 + 元数据刷新。

## 验证
- vitest 全量 + vue-tsc（改动文件 0 错误）。
- 重启 Core/Gateway/Vite，浏览器实测：docked 态工具栏出现模式版本选择器、选择后发下一条消息携带该 `mode_version_id`、hero 态默认"跟随默认"。