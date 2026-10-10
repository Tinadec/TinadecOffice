# 共享渲染层 / 路由与 API：功能与完成情况

2026-10-11读取恢复专项的实现与当前验证在[报告](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)，唯一任务[APP-HOME-107](../home/TODO.md#app-home-107)。JSON传输网络预算、取消/撤权、写不重放与Settings代次/单飞有定向回归；不把这项扩展成SSE、全部页面或完整平台验收。

模块ID：`APP-RENDERER` · 更新日期：2026-10-09 · Comment3/4/5专项基线：d5e6c8d8 + 当前工作树；F001保留初始清点，F002/F003保留各历史专项范围。

**审计阶段：初始源码清点；逐功能审计未完成。** 不报告完成百分比，也不把历史全量绿或源码存在折算为功能完成。

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| APP-RENDERER-F006 | 搜索模态背景模糊与全局面板材质 | 已验收 | 2026-10-09 Windows1169×719实页：opaque/translucent46%/blur14px、活动行透明度、全屏材质及Escape关闭；4文件50项定向回归和类型检查通过 | 原生遮罩独立6px模糊，dialog直接复用全局材质style/data，活动行surface-hover；用户原opaque/80/8已恢复。未重跑全产品构建/安装器，非Linux/macOS验收；无搜索专用偏好、不改变搜索范围或整体模块状态。 | [APP-RENDERER-107](TODO.md#app-renderer-107)<br>[专项报告](../../../../../.tinadec_dev/reports/2026-10-09-search-material.zh-CN.md)<br>[源码核查](../../../../../.tinadec_dev/evidence/2026-10-09-search-material/command-palette-material-audit.md)<br>[定向回归](../../../../../.tinadec_dev/evidence/2026-10-09-search-material/targeted-tests.log)<br>[浏览器记录](../../../../../.tinadec_dev/evidence/2026-10-09-search-material/browser-checks.json) |
| APP-RENDERER-F005 | 空间入口收口与默认关闭的开发者路由 | 已验收 | 2026-10-09 Windows1169×719实页/真实router测试；12文件236项定向通过、类型检查 | 指挥中心侧栏和命令面板入口删除，旧链接转空间；调试入口和深链受同一本机偏好控制。仅导航专项，非完整空间/专用调试后端/平台安装包验收 | [APP-RENDERER-106](TODO.md#app-renderer-106)<br>[本轮报告](../../../../../.tinadec_dev/reports/2026-10-09-ui-comments-2.zh-CN.md) |
| APP-RENDERER-F001 | App、路由、API、状态与通知组合层 | 源码可见 | 本轮静态核对；未做功能验收 | 展示 Core 返回事实；Home/Market 页面 ready 控制 splash；连接重试不重挂主页面。 | [apps/desktop/src/App.vue](../../../../../apps/desktop/src/App.vue)<br>[apps/desktop/src/router.ts](../../../../../apps/desktop/src/router.ts)<br>[apps/desktop/src/api.ts](../../../../../apps/desktop/src/api.ts) |
| APP-RENDERER-F004 | 通知图标、预览链接与显示模式菜单叶层UI | 已验收 | 2026-10-09 Windows1169×719实页双列表Pin、236/225px链接无溢出、菜单退出中间帧/reduced-motion/焦点；通知46passed/14既有skip、AppSidebar12passed、类型与独立Vite构建通过 | Comment3去前Pin保留右侧；Comment4完整URL上下排列且窄宽换行；Comment5原生popover160ms开合。不宣称完整App/安装器或Linux/macOS GUI验收；Island14例既有skip由实际页面另取证。 | [APP-RENDERER-105](TODO.md#app-renderer-105)<br>[主报告](../../../../../.tinadec_dev/reports/2026-10-09-ui-comments.zh-CN.md)<br>[Comment3证据](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments/comment3-notification-pin.md)<br>[Comment5证据](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments/comment-5-view-menu.md) |
| APP-RENDERER-F002 | 分类搜索浮窗与沉浸式窗口控制 | 已验收 | 2026-10-06 Windows组件与自动测试专项：窗口/设置82/82；本轮整合911 passed/14 skipped、native/scripts107/107 | 点击搜索直接打开默认760×590自适应浮窗，可选全屏；10类＋全部、分类/结果图标、每组4项预览、展开/收起与文字省略。窗口控制与搜索入口为ghost纯图标，无背景/阴影，保留焦点与no-drag。范围是当前授权目录；会话按标题与项目名匹配，不检索历史消息全文或全磁盘文件名。最终定向48/48、类型/构建和真实Electron浮窗专项通过；模块整体审计未完成，取消错误与窄宽发送任务未关闭。 | [APP-RENDERER-103](TODO.md#app-renderer-103)<br>[CommandPalette.vue](../../../../../apps/desktop/src/components/CommandPalette.vue)<br>[spotlight.ts](../../../../../apps/desktop/src/lib/spotlight.ts)<br>[pageRequests.ts](../../../../../apps/desktop/src/lib/pageRequests.ts)<br>[CommandPalette.test.ts](../../../../../apps/desktop/src/components/CommandPalette.test.ts)<br>[spotlight.test.ts](../../../../../apps/desktop/src/lib/spotlight.test.ts)<br>[AppHeader.test.ts](../../../../../apps/desktop/src/components/AppHeader.test.ts) |
| APP-RENDERER-F003 | 会话级空间模式与运行事实投影 | 部分实现 | 命令开关专项Desktop1024/14 skipped、Core空间脚本API14、Electron面板/审批全文夹具 | 开关组合与冻结投影已实现；完整工作产物、外部模型端到端和平台验收仍在APP-RENDERER-104 | [首批报告](../../../../../.tinadec_dev/reports/2026-10-06-spatial-mode-first-slice.zh-CN.md)、[命令面板专项](../../../../../.tinadec_dev/reports/2026-10-08-command-panel.zh-CN.md) |

## 2026-10-06 搜索专项范围

本轮完整证据：[搜索浮窗与窗口控制报告](../../../../../.tinadec_dev/reports/2026-10-06-search-and-window-chrome.zh-CN.md)。专项完成不代表模块整体已验收。

补充交互：分类横向滚动指示器已隐藏但仍可滚动；右侧 UIE 标签中键关闭属于同一渲染交互专项，固定 Home 标签不关闭。

- 可搜索命令、活跃项目、全项目活跃会话、模型提供方/配置模型、智能体、模式、提示词片段、工具、设置与当前工作区文件内容。提示词读取实际片段目录；工具匹配交给 Core 搜索接口。
- 各远端来源独立读取，5秒超时并可取消；一个来源失败仍展示其他来源结果与失败提示。当前工作区内容走受治理的 `grepContent`，最多100条匹配行，按文件去重并展示服务端截断提示，不能把行上限描述为100个文件。
- 点击/Enter共用结果动作，通过一次性页面请求定位 Home 项目/会话、Code 文件和 Settings 对应配置项；不再在顶栏展开第二个搜索输入。
- 上述专项验收不代表历史消息全文搜索、全磁盘索引、全部业务链或其他平台交互已完成。

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

## 2026-10-10 工作区专项对照

本模块对多文件夹工作区的改动及源码入口见 [README](README.md)。统一功能与任务由 [APP-HOME-F007](../../app/home/STATUS.md) / [APP-HOME-107](../../app/home/TODO.md#app-home-107) 持有；当前专项验证与未验收平台分别见 [本轮报告](../../../../../.tinadec_dev/reports/2026-10-10-sidebar-workspaces.zh-CN.md)。本模块整体初始审计不因专项测试通过改为已完成。

[本模块TODO](TODO.md) · [功能分析模板](../../../05-templates/FEATURE.md)

## 2026-10-07 目标工作簇专项

第一阶段实现已接手：Desktop969/14 skipped、UIE156、类型/构建和三宽度Electron夹具验证；完整空间模式仍部分实现，真实模型/平台与工具归属证据后续验收。[范围与证据](../../../../../.tinadec_dev/reports/2026-10-07-space-clusters-handoff.zh-CN.md)。

2026-10-07布局与路由计算专项：UIE187/187、Desktop974/14 skipped、类型检查及离线示例通过；既有Electron渲染器无响应，本轮不宣称实际拖动视觉验收。[证据](../../../../../.tinadec_dev/reports/2026-10-07-space-layout-routing.zh-CN.md)。

## 2026-10-10 接口回归专项

useConnection 区分公开健康和可信宿主认证；全局提示展示预览、暂不可用与身份拒绝。两 API wrapper 先捕获请求 scope 再等待认证；搜索与 Debug 历史选择复用跨作用域读取器，按 scope+id 去重。

统一范围、验收和边界由 [APP-HOME-107](../home/TODO.md#app-home-107) 与 [接口回归报告](../../../../../.tinadec_dev/reports/2026-10-10-interface-regression.zh-CN.md) 持有。源码/组件回归、Windows 隔离 Electron 与真实 Core/Gateway 分别记录；完整 App、安装器和非 Windows 平台不因此标完成。

宿主 IPC 跟进：restart_required 为独立本地状态，业务请求保存 ApiError 不再误包装 Cannot connect backend；版本不一致停止无效轮询，较新宿主广播优先于迟到读取/手动重试。共享 Banner 使用现有材质：DEV 只有整链启动指引，生产单飞重启及失败指引。定向测试与 Windows 真实 Electron 组件证据见 [IPC 修复报告](../../../../../.tinadec_dev/reports/2026-10-10-host-ipc-mismatch.zh-CN.md)；完整渲染模块审计独立。
