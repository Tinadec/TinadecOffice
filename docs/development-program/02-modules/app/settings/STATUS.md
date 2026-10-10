# Settings / 配置中心：功能与完成情况

## 2026-10-11 接口回归跟进

目录非空与加载失败已分离；首次/恢复/手动读取合并并可取消，详情局部失败不阻断目录也不能编辑空定义，刷新保护未保存草稿。当前源码、89项Desktop定向、类型、真实配置副本HTTP及Windows Electron边界见[本轮报告](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)，功能/任务仍由[APP-HOME-107](../home/TODO.md#app-home-107)持有；不增加第二套完成记录，不将Settings整体标完成。

模块ID：`APP-SETTINGS` · 清点日期：2026-10-05 · 基线：b6115e6 + 当前工作树。

最近专项：2026-10-09，d5e6c8d8 + 当前工作树；仅 Debug Studio 本机开关。

**审计阶段：初始源码清点；逐功能审计未完成。** 不报告完成百分比，也不把历史全量绿或源码存在折算为功能完成。

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| APP-SETTINGS-F001 | 版本化模型、agent、mode、prompt 与集成配置界面 | 源码可见 | 本轮静态核对；未做功能验收 | 模型参数按 provider+model 保存并传 If-Match；保存失败保留编辑区，刷新失败保留旧列表；最新已修项不重复登记为缺口。 | [apps/desktop/src/pages/SettingsPage.vue](../../../../../apps/desktop/src/pages/SettingsPage.vue)<br>[apps/desktop/src/settings/sections/AgentPacksPanel.vue](../../../../../apps/desktop/src/settings/sections/AgentPacksPanel.vue)<br>[apps/desktop/src/api.ts](../../../../../apps/desktop/src/api.ts) |

## 2026-10-08 工具配置专项

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| APP-SETTINGS-F002 | 九标签工具设置、严格 JSON、资源与 Agent 绑定 | 已验收 | 1068 组件通过、最后总览17定向、类型与生产构建、真实三类 Agent Desktop及生产Schema Worker | Windows隔离Core/Tools/MCP/Skills实际走查；PostgreSQL仅编译，Linux/macOS未实机；对应APP-SETTINGS-102 | [专项报告](../../../../../.tinadec_dev/reports/2026-10-08-tools-settings.zh-CN.md) |

| APP-SETTINGS-F003 | Skills 包安装与资源管理（含本地导入审批回执） | 已验收 | 设置 160/160、Desktop 全量 1091 passed/14 skipped、Gateway 88/88、类型检查与生产构建通过 | PostgreSQL/非 Windows 未实机；真实 Core/市场运行见 Core 证据 | [专项报告](../../../../../.tinadec_dev/reports/2026-10-09-skills-management.zh-CN.md) |

## 2026-10-09 Debug Studio 本机开关

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| APP-SETTINGS-F004 | 关于页默认关闭的 Debug Studio 开关、本机 TOML 保存与跨窗刷新 | 已验收 | Windows 浏览器 preview；Renderer19/19；宿主 VM/Node 配置/IPC10/10、相关回归23/23（含前述10项，不重复合计） | 2026-10-09，d5e6c8d8 + 工作树；源为 bootstrap desktop.toml，默认~/.tinadec/config/desktop.toml；特殊有效形状保存保字段但可能丢注释。尚未真实 native 窗口/安装包验证；仅对应APP-SETTINGS-104，001/101保持原状态。 | [本轮报告](../../../../../.tinadec_dev/reports/2026-10-09-ui-comments-2.zh-CN.md)<br>[Renderer证据](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/debug-preference-tests.md)<br>[宿主证据](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/debug-studio-host.md) |

## 状态词汇

- 待核查：尚不能判断是否实现或缺失。
- 源码可见：找到实现路径，仍需验证真实行为。
- 部分实现：已确认目标的一部分存在，剩余范围明确。
- 缺口已确认：当前源码或复现证明缺失；目标与验收见TODO。
- 范围边界：当前平台/产品有意不提供的能力，是否扩展另作范围决策。
- 已验收：有与目标范围相符的运行/测试证据和结果，必须注明提交/环境/日期。
- 不适用：写明原因，不算完成也不算缺陷。

## 下一轮逐功能分析

将聚合行拆成可验收功能，保留旧Feature ID或明确替代关系；为每项记录入口、预期行为、实际行为、成功/失败/权限/取消/恢复场景、对应Task ID。历史报告只写“历史验证，本轮未重跑”。

[本模块TODO](TODO.md) · [功能分析模板](../../../05-templates/FEATURE.md)

## 2026-10-10 接口回归专项

归档/回收站读取用户与所有登记作用域，局部失败保留原记录与诊断。工作区读重试保留草稿，存储损坏项保留取消登记出口。AgentPack inventory 刷新失败保留已有记录，恢复连接只刷新读取。存储维护预览、确认和完成回执按代次与作用域隔离，断线清除预览，不能将旧回执展示在新作用域。

统一范围、验收和边界由 [APP-HOME-107](../home/TODO.md#app-home-107) 与 [接口回归报告](../../../../../.tinadec_dev/reports/2026-10-10-interface-regression.zh-CN.md) 持有。源码/组件回归、Windows 隔离 Electron 与真实 Core/Gateway 分别记录；完整 App、安装器和非 Windows 平台不因此标完成。
