# Home / 会话、对话与投递：功能与完成情况

模块ID：`APP-HOME` · 清点日期：2026-10-05 · 基线：b6115e6 + 当前工作树。

**审计阶段：初始源码清点；逐功能审计未完成。** 不报告完成百分比，也不把历史全量绿或源码存在折算为功能完成。

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| APP-HOME-F001 | 项目、会话、附件及 queued/parallel/insert 消息投递协调 | 源码可见 | 本轮静态核对；未做功能验收 | 发送前记录权限；队列晋升保留模式、模型、权限和附件。本轮仅静态核对源码，未运行业务测试或产品验收。 | [apps/desktop/src/controllers/HomeController.ts](../../../../../apps/desktop/src/controllers/HomeController.ts) |
| APP-HOME-F002 | 按 run 归属的活动、首轮 live block、审批与监督裁决入口 | 源码可见 | 本轮静态核对；未做功能验收 | 平面模式已由 APP-HOME-F008 的回合结构取代（`LiveTurnBlock`/`MessageList` 于 2026-10-13 删除），空间模式仍用 `useAgentActivity`；真实并行与三个监督决策未验收。 | [apps/desktop/src/controllers/HomeController.ts](../../../../../apps/desktop/src/controllers/HomeController.ts)<br>[apps/desktop/src/components/conversation/ConversationTurn.vue](../../../../../apps/desktop/src/components/conversation/ConversationTurn.vue) |
| APP-HOME-F003 | Home 初始布局就绪与 splash 入场同步 | 源码可见 | 本轮静态核对；未做功能验收 | 最新修复已有历史专项验证；本轮未重跑，不重复登记为未修缺陷。 | [apps/desktop/src/pages/HomePage.vue](../../../../../apps/desktop/src/pages/HomePage.vue)<br>[apps/desktop/src/App.vue](../../../../../apps/desktop/src/App.vue)<br>[docs/home-entry-motion-2026-10-05.zh-CN.md](../../../../home-entry-motion-2026-10-05.zh-CN.md) |
| APP-HOME-F004 | AI历史/流式正文的Markdown显示、岛屿卡片与扩展语法 | 已验收 | 2026-10-08：Electron 夹具 passed（真实剪贴板、MathML、Mermaid、三宽度）、定向 23、Desktop 全量 1028 passed/14 skipped、类型检查通过；生产 vite build 本轮未取证 | 岛屿与卡片细节、代码高亮与复制、公式、脚注、提示块、标题锚点、Mermaid 均已接通；非真实模型或完整 App E2E，外链打开仍未处理，Mermaid 未 Worker 化。 | [APP-HOME-103](TODO.md#app-home-103)<br>[APP-HOME-105](TODO.md#app-home-105)<br>[扩展语法报告](../../../../../.tinadec_dev/reports/2026-10-08-markdown-extended-syntax.zh-CN.md) |

## 2026-10-08 输入框命令面板

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| APP-HOME-F005 | +与/统一命令面板及会话运行设置 | 已验收 | Desktop1024/14 skipped、native107、Gateway80、Core存储19与空间脚本API14、真实SFC/Electron与类型/构建 | 本地功能验收；非完整Home审计、外部模型/PG实机或平台安装包验收 | [APP-HOME-104](TODO.md#app-home-104)、[实施记录](../../../../../.tinadec_dev/reports/2026-10-08-command-panel.zh-CN.md) |

2026-10-08命令面板细节修订：选择器图标与权限风险色、紧凑布局、150ms切页过渡、选项/Enter自动关闭和点击外部关闭已修；109项定向、类型检查与实际SFC浏览器验证通过。见[细节修复记录](../../../../../.tinadec_dev/reports/2026-10-08-command-panel-polish.zh-CN.md)。

## 2026-10-09 项目选择器与展开指示

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| APP-HOME-F006 | Composer 项目长列表可访问性及模式/权限/项目展开指示 | 已验收 | 2026-10-09 Windows1169×719真实主页面及45项SFC夹具：滚轮、末项/自由对话/新建键盘访问、350px窄容器、固定页脚、隐藏滚条和 reduced-motion通过；组件专项74项、统一12文件236项无skip及类型检查通过，重复回归不相加 | 原生单滚动列表代替该处 UiScrollArea；模式/权限箭头与 aria-expanded 同读实际 panel.page，项目同读 showProjectDropdown，160ms变换。45伪项目只调用隔离夹具回调，不登记真实项目。限普通Windows浏览器UI/滚轮/键盘，不含触屏硬件、Linux/macOS、完整Home及欢迎页附件；最后生产构建结果由统一报告记录。 | [APP-HOME-106](TODO.md#app-home-106)<br>[本轮报告](../../../../../.tinadec_dev/reports/2026-10-09-ui-comments-2.zh-CN.md)<br>[项目选择器](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/comment-project-picker.md)<br>[指示器与74项回归](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/composer-arrows.md) |

## 2026-10-10 侧边栏与多文件夹工作区

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| APP-HOME-F007 | 工作区标题、纯折叠项目行、近期会话/手动排序及多源目录创建、编辑、授权冻结 | 源码可见 | Desktop62/62与类型，Windows Core62/1明确skip、Tools40/40、资源34/34、Gateway6/6；Electron43.3.0 原生目录多选与真实隔离 Core/Gateway；Linux Core28/28、内核Tools28/28，PostgreSQL18.6空库/既有扩展实测 | Windows 低权限 Shell、macOS与本轮 CI 待验收；不是完整 App、真实模型或安装包验收，平台返回型用例不当作 OS 证据 | [APP-HOME-107](TODO.md#app-home-107)、[契约](../../../../workspaces.zh-CN.md)、[报告](../../../../../.tinadec_dev/reports/2026-10-10-sidebar-workspaces.zh-CN.md) |

## 2026-10-13 平面对话流 v2

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| APP-HOME-F008 | 平面对话流回合结构：用户消息 → 工作区（推理、分层工具行、子智能体、系统行；运行中展开、落定收成摘要）→ 不折叠的回答 → 脚注；审批常显；事件溯源数据层与有界结果预览 | 源码可见 | Linux：Desktop 全量 vitest、`vue-tsc`、TinadecUI、Gateway、Core 定向/全量（既有 Linux 失败另列）；无头 Chromium 截图矩阵与减弱动效计算样式（代替计划的实机截图与 `vue-mcp`）；首版漏做的重试、日期分隔、未读回合数、切换骨架、groupText、shimmer-once 已补齐并有测试 | Electron 实机、真实模型 Team 链路、Windows/macOS 未验收；未读回合数的真实滚动只能实机看；空间模式未迁移（本轮约定） | [APP-HOME-108](TODO.md#app-home-108)、[报告](../../../../../.tinadec_dev/reports/2026-10-13-flat-conversation-p0.zh-CN.md)、[截图证据](../../../../../.tinadec_dev/evidence/2026-10-13-flat-conversation/README.md) |

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

## 2026-10-10 接口回归跟进

APP-HOME-F007 同任务接通宿主准入与只读恢复、请求固定scope、跨作用域会话读取及错误出口。项目/doctor/readiness独立更新，损坏登记不阻断正常项目，归档/回收站历史不再只读user库。活跃流保留原游标与正文，运行清单读取失败保持原事实并显示恢复失败，不当作空清单删除句柄。组件回归与隔离真实服务、Electron证据见 [接口回归报告](../../../../../.tinadec_dev/reports/2026-10-10-interface-regression.zh-CN.md)；APP-HOME-107保持待验收，整体App和非Windows平台边界独立。

宿主 IPC 跟进：缺 handler 或旧 preload 缺方法立即撤权为 desktop_restart_required，不再向三项初始读取发重复后端错误通知。共享 Banner 提示整链开发重启或生产桌面重启；保留已有事实、不重放写。当前专项回归与 Windows Electron 契约夹具边界见 [IPC 修复报告](../../../../../.tinadec_dev/reports/2026-10-10-host-ipc-mismatch.zh-CN.md)，不提升整体任务完成状态。
