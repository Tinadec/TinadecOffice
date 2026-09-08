> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# 修复模型中心"添加"按钮不弹出面板的问题

## 根因（已确认）

1. **主因**：`apps/desktop/src/pages/SettingsPage.vue:1688` 命令栏"添加"按钮绑定 `focusModelProviderList('available')`（875 行），该函数只切换到供应商标签并滚动/聚焦列表，**从不调用 `openAddModal()` / 从不设置 `showModal = true`** —— 点击它永远不显示面板。
2. **叠加因素**：真正能打开模态框的按钮（供应商卡片 1981 行、API 表行 2178/2195 行）全部由 `modelCenterOverview.suppliers` 渲染。Core 未启动时 Gateway `/api/v1/model-center/overview` 返回 502 `CORE_UNREACHABLE`（无降级数据），供应商区只剩空态 → 页面上没有任何能打开面板的按钮，形成死路。

已排除：CSS 裁剪（模态框为 fixed/z-index:100，无 transform 祖先）、JS 抛错（`openAddModal` 有 `PROVIDER_TEMPLATES[0]` 静态兜底）、UIE 分屏栈影响（设置页不在 featureCatalog 中）。

## 修复方案（最小改动，仅 `apps/desktop/src/pages/SettingsPage.vue`）

保留"先跳转供应商列表选卡"的现有流程，但当供应商目录为空时兜底直接打开模态框，消除死路：

1. 在 `focusModelProviderList`（875-882 行）附近新增 `handleAddProviderClick()`：
   ```ts
   function handleAddProviderClick() {
     if ((modelCenterOverview.value?.suppliers.length ?? 0) === 0) {
       openAddModal()
       return
     }
     focusModelProviderList('available')
   }
   ```
   无参 `openAddModal()` 已有兜底链 `suppliers[0]` → `PROVIDER_TEMPLATES[0]`（providerTemplates.ts:68 静态列表），Core 不可用时也能弹出表单；保存失败会走现有 `saveProvider` 的 `notify.error` 提示（合理——保存本就必须经 Gateway 透传 Core）。
2. 命令栏按钮（1688 行）`@click` 改为 `handleAddProviderClick`。

## 验证

- `npm --prefix apps/desktop test`（现有 Vitest 套件）
- 手动验证两条路径：Core 停止时点"添加"→ 模态框弹出（静态默认模板）；Core 运行时点"添加"→ 仍跳转供应商列表（行为不变）
- 本改动为 UI 交互微调，不涉及模块边界/端口/契约，AGENTS.md 无需更新