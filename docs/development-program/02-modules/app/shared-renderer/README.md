# 共享渲染层 / 路由与 API

## 2026-10-11 JSON读取与恢复所有权

`backendRequest.ts`统一api.ts/生成客户端的JSON传输，仅GET/HEAD网络与正文中断在250/750ms内再试，合计最多三次；每次重验宿主，作用域固定。HTTP/JSON错误与写不重放，写回执丢失明确结果未知。`useRecoverableRead`由页面持有单飞、取消、已加载状态与迟到保护。SSE/二进制仍使用原模块。[错误契约](../../../../error-contract.zh-CN.md)、[证据](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)，任务[APP-HOME-107](../home/TODO.md#app-home-107)。

模块ID：`APP-RENDERER` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本模块目前处于**初始源码清点**，还未完成逐功能审计；下列介绍继承总图中已核对的职责，初始状态区分源码能力、范围与缺口。

## 2026-10-10 多文件夹工作区

UIE NavCard 继续作为侧边栏宿主，业务交互由 AppSidebar/HomeController 承担。工作区行只改变列表显示，不切换 UIE 布局；现有平面/空间分类保持一致。WorkspaceEditorDialog 复用 ReKa、Ui 组件与全局材质。

本专项统一由 [APP-HOME-107](../../app/home/TODO.md#app-home-107) 记账，APP-RENDERER 保留本模块整体审计；交互与存储契约见 [workspaces.zh-CN.md](../../../../workspaces.zh-CN.md)，本轮证据与平台边界见 [实施报告](../../../../../.tinadec_dev/reports/2026-10-10-sidebar-workspaces.zh-CN.md)。

## 文档入口

- [模块架构与边界](ARCHITECTURE.md) · [模块SVG](architecture.svg)
- [功能与完成情况](STATUS.md) · [本模块TODO](TODO.md)
- [全部模块](../../../MODULE-INDEX.md) · [总TODO](../../../01-program/MASTER-TODO.md)

## 功能介绍

2026-10-09入口收口：[APP-RENDERER-106](TODO.md#app-renderer-106) 删除指挥中心侧栏/命令面板首页导航，旧 `/workbench` 深链重定向 `/space`。Debug Studio默认隐藏，关于开关与路由共用本机偏好；关闭时移出已打开调试页面，宿主另复核可信主窗口和实际配置。治理/恢复/记忆等服务及运行事实仍由各模块负责。验收范围见[本轮报告](../../../../../.tinadec_dev/reports/2026-10-09-ui-comments-2.zh-CN.md)。

### 共享渲染层 / App.vue + Router + API

组合页面、状态、路由、通知与 API 客户端，展示 Core 返回的状态。

- Vue / TypeScript / Pinia / i18n / Monaco / xterm
- 生成的 OpenAPI 类型 + HTTP 请求 + SSE 活动订阅

### 搜索浮窗遮罩与全局材质

[APP-RENDERER-107](TODO.md#app-renderer-107) / APP-RENDERER-F006补齐窗口级搜索呈现：打开时原生模态遮罩使用独立6px背景模糊；面板及内部活动表面复用全局opaque/translucent/blur材质、opacity与blur设置。全屏是同一个原生dialog的尺寸切换，材质与遮罩契约共用，不引入第二份搜索偏好或业务搜索入口。

2026-10-09 Windows1169×719实页与4文件50项定向回归、类型检查通过：opaque保持遮罩6px模糊，translucent46%背景与0.82活动行透明度、blur14px与0.62活动行透明度正确，全屏仍保留全局材质；Escape关闭后无modal且App根filter为none。用户原opaque/80/8设置已恢复。本轮未重跑全产品构建、安装器或其他平台，不扩大既有搜索目录，也不完成模块整体审计。[专项报告](../../../../../.tinadec_dev/reports/2026-10-09-search-material.zh-CN.md)、[源码核查](../../../../../.tinadec_dev/evidence/2026-10-09-search-material/command-palette-material-audit.md)。

### 通知、预览链接与显示模式菜单（Comment3/4/5）

聚合堆叠列表和通知中心active列表标题使用纯文本，左侧等级图标表达消息级别；不可由用户关闭的source-owned通知/运行任务在右侧保留Pin和生命周期提示。胶囊、展开卡片与详情各自保留其持久状态上下文。Comment3删除两个列表标题前重复Pin，不改变通知关闭权限、持久策略或GraphSeedPack错误恢复。

PreviewBrowserPanel快速链接名称在上、地址在下，采用图标列与`minmax(0,1fr)`文本列，文本可收缩，地址`overflow-wrap:anywhere`，在窄面板内保持实际可点击内容而不横向撑开。原导航事件和URL保持来源。

AppSidebar“切换显示模式”按钮只打开真实原生popover菜单；160ms进入/退出随popover状态变化，display/overlay allow-discrete保留关闭过程，reduced-motion禁过渡。原生invoker支持Escape/轻触关闭，aria-expanded由beforetoggle更新，当前选项autofocus，选择后立即恢复按钮焦点并发change-view；不为按钮装饰旋转，也不引入页面外层Transition。

本轮仅修叶层呈现。通知46通过/14既有Island skip、AppSidebar12通过与类型检查已记录，实际页面观测和最终窄宽取证由主任务对账；APP-RENDERER-105保持待验收。范围与证据见 [任务](TODO.md#app-renderer-105)、[主报告](../../../../../.tinadec_dev/reports/2026-10-09-ui-comments.zh-CN.md)，不代替模块整体审计或完整App/安装器验收。

### 客户端与 Core 的责任交界

四产品可独立版本化和组合。当前桌面/Web 渲染层经 Gateway 使用 Core，不意味着所有产品必须捆绑安装。

- App 表达意图、展示事实；Core 裁决执行和授权
- 独立产品可替换；图上箭头表示当前 Office 集成路径

## 源码入口

- [apps/desktop/src/main.ts](../../../../../apps/desktop/src/main.ts)
- [docs/tinadec-core-product-definition.zh-CN.md:97](../../../../tinadec-core-product-definition.zh-CN.md#L97)
- [apps/desktop/src/App.vue](../../../../../apps/desktop/src/App.vue)
- [apps/desktop/src/router.ts](../../../../../apps/desktop/src/router.ts)
- [apps/desktop/src/api.ts](../../../../../apps/desktop/src/api.ts)

## 相关模块

- [Home / 会话、对话与投递](../home/README.md)
- [Code / 编程工作台](../code/README.md)
- [Workbench / 治理与数据页面](../data-pages/README.md)
- [Settings / 配置中心](../settings/README.md)
- [Market / 市场](../market/README.md)
- [Debug Studio / 调试界面](../debug-studio/README.md)
- [TinadecUI / UIE Engine](../uie-engine/README.md)
- [Gateway / HTTP、认证与上下文](../../gateway/http-auth/README.md)

## 本模块的开发工作方式

先拆分STATUS中的功能、核对实际行为并定位缺口，再逐项执行TODO。每次实现同步功能状态、模块图和证据；目标行为与验收边界写清后才进入该任务的实施。核查任务完成不代表该模块所有能力已经完成。
