# Home / 会话、对话与投递

模块ID：`APP-HOME` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本模块目前处于**初始源码清点**，还未完成逐功能审计；下列介绍继承总图中已核对的职责，初始状态区分源码能力、范围与缺口。

## 文档入口

- [模块架构与边界](ARCHITECTURE.md) · [模块SVG](architecture.svg)
- [功能与完成情况](STATUS.md) · [本模块TODO](TODO.md)
- [全部模块](../../../MODULE-INDEX.md) · [总TODO](../../../01-program/MASTER-TODO.md)

## 功能介绍

### Home / 对话与执行过程

发送用户意图，展示按 run 归属的模型流、执行活动与可裁决审批；HomeController 负责前端交互协调。

- 会话、附件、排队/并行/插入
- 活动时间线、工具、审批

### 2026-10-09 项目选择器与展开指示专项

[APP-HOME-106](TODO.md#app-home-106) / [APP-HOME-F006](STATUS.md)：ComposerBar 的项目菜单使用一个原生滚动列表，替换该处高度关系不成立的 UiScrollArea，隐藏滚动条但保留滚动和键盘访问。打开聚焦当前项，Arrow/Home/End 可到达末项与新建入口，选择或 Escape 恢复触发器焦点。通用 UiScrollArea 仍由其它使用方保留。

模式与权限入口使用 ComposerCommandPanel 的实际当前页，同时决定箭头方向与 aria-expanded；返回根页时两入口收起，切页时仅对应入口展开。项目入口由实际 showProjectDropdown 控制同样的指示，三者均使用160ms transform过渡，并在 reduced-motion 下关闭过渡。

该专项已在2026-10-09 Windows 1169×719真实主页面及45项SFC隔离夹具验收：原生滚轮滚动只移动列表，末项/自由对话/新建入口均可键盘访问；350px容器无横向溢出，页脚固定、滚动条隐藏。真实主页面的模式与权限箭头随当前页旋转和复位，三入口 reduced-motion 下关闭过渡。74项组件专项回归及统一12文件236项回归、类型检查通过，重复运行同一测试不累计。

45个伪项目夹具拒绝实际 API，仅记录预览回调，不登记真实项目。该结论限普通Windows浏览器UI、滚轮与键盘，不包含触屏硬件、Linux/macOS、完整 Home、消息投递或欢迎页附件验收。统一报告：[本轮UI标注](../../../../../.tinadec_dev/reports/2026-10-09-ui-comments-2.zh-CN.md)；实现证据：[项目选择器](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/comment-project-picker.md)、[展开指示器](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/composer-arrows.md)。

### 2026-10-10 工作区交互

侧边栏“工作区”标题控制整组，行只折叠对话列表；加号新对话、子项打开、菜单与拖动互不触发折叠。自由对话与项目工作区共享统一手动顺序（可拖动及键盘重排，无手动顺序时自由对话默认在前）、最近五条与当前旧对话、统一滚动和分作用域加载状态由 AppSidebar/useWorkspaceList 管理。HomeController 维护新对话上下文及固定作用域，统一 WorkspaceEditorDialog 创建、明确打开和条件编辑多文件夹工作区。

源目录集合、主要目录、图标与颜色以原存储根 project.toml 为权威；宿主授权、新运行冻结和工具隔离由 Core/Tools 管理。本机列表偏好不构成授权。正式规则见[工作区契约](../../../../workspaces.zh-CN.md)，唯一任务为[APP-HOME-107](TODO.md#app-home-107)，验收边界见[报告](../../../../../.tinadec_dev/reports/2026-10-10-sidebar-workspaces.zh-CN.md)。

## 源码入口

- [apps/desktop/src/controllers/HomeController.ts:22](../../../../../apps/desktop/src/controllers/HomeController.ts#L22)
- [apps/desktop/src/controllers/HomeController.ts](../../../../../apps/desktop/src/controllers/HomeController.ts)
- [apps/desktop/src/components/conversation/ConversationView.vue](../../../../../apps/desktop/src/components/conversation/ConversationView.vue)（平面对话流，取代已删除的 `MessageList`/`LiveTurnBlock`，APP-HOME-108）
- [apps/desktop/src/pages/HomePage.vue](../../../../../apps/desktop/src/pages/HomePage.vue)
- [apps/desktop/src/App.vue](../../../../../apps/desktop/src/App.vue)
- [apps/desktop/src/components/ComposerBar.vue](../../../../../apps/desktop/src/components/ComposerBar.vue)
- [apps/desktop/src/components/ComposerCommandPanel.vue](../../../../../apps/desktop/src/components/ComposerCommandPanel.vue)
- [docs/home-entry-motion-2026-10-05.zh-CN.md](../../../../home-entry-motion-2026-10-05.zh-CN.md)

## 相关模块

- [DmaEA / 双层调用与持久运行引擎](../../core/DmaEA/README.md)
- [Governance · 授权与审批](../../core/Governance/README.md)

## 本模块的开发工作方式

先拆分STATUS中的功能、核对实际行为并定位缺口，再逐项执行TODO。每次实现同步功能状态、模块图和证据；目标行为与验收边界写清后才进入该任务的实施。核查任务完成不代表该模块所有能力已经完成。
