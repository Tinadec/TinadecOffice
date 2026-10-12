# Gateway / 项目、会话与运行控制

模块ID：`GW-SESSIONS` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本模块目前处于**初始源码清点**，还未完成逐功能审计；下列介绍继承总图中已核对的职责，初始状态区分源码能力、范围与缺口。

## 2026-10-10 多文件夹工作区

项目 DTO 增加多源目录、主要目录、图标、颜色与配置摘要。工作区 preview/read/edit 维持薄代理，透传 If-Match/ETag、结构化错误和固定 storage header；OpenAPI 快照及生成 Desktop schema 同步。Gateway 不拥有目录授权或配置的第二份编辑来源。

本专项统一由 [APP-HOME-107](../../app/home/TODO.md#app-home-107) 记账，GATEWAY-SESSIONS 保留本模块整体审计；交互与存储契约见 [workspaces.zh-CN.md](../../../../workspaces.zh-CN.md)，本轮证据与平台边界见 [实施报告](../../../../../.tinadec_dev/reports/2026-10-10-sidebar-workspaces.zh-CN.md)。

## 2026-10-12 对话页与活动

messages/page、activity和消息sequence公开契约与Core同步，Gateway只转发游标/epoch/run_ids，不维护第二份状态。原消息数组接口保留；公开OpenAPI及Desktop生成类型同步。统一任务[APP-HOME-108](../../app/home/TODO.md#app-home-108)，完整Gateway107项与HTTP边界见[报告](../../../../../.tinadec_dev/reports/2026-10-12-transcript-stability.zh-CN.md)。

## 文档入口

- [模块架构与边界](ARCHITECTURE.md) · [模块SVG](architecture.svg)
- [功能与完成情况](STATUS.md) · [本模块TODO](TODO.md)
- [全部模块](../../../MODULE-INDEX.md) · [总TODO](../../../01-program/MASTER-TODO.md)

## 功能介绍

### 项目 / 会话 / run

会话与运行控制 API 代理 Core。interaction 受理返回 JSON；客户端另订阅 run SSE。

- projects / sessions / interactions
- runs / stream / events / attachments
- 取消、暂停、恢复、上下文补丁

## 源码入口

- [TinadecGateway/src/index.ts](../../../../../TinadecGateway/src/index.ts)
- [TinadecGateway/src/coreClient.ts](../../../../../TinadecGateway/src/coreClient.ts)

## 相关模块

- [DmaEA / 双层调用与持久运行引擎](../../core/DmaEA/README.md)
- [Lifecycle · 运行事实与恢复](../../core/Lifecycle/README.md)
- [Context · 本轮输入与补丁](../../core/Context/README.md)

## 本模块的开发工作方式

先拆分STATUS中的功能、核对实际行为并定位缺口，再逐项执行TODO。每次实现同步功能状态、模块图和证据；目标行为与验收边界写清后才进入该任务的实施。核查任务完成不代表该模块所有能力已经完成。
