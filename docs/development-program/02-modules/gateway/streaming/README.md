# Gateway / SSE、附件、日志与取消

模块ID：`GW-STREAMING` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本模块目前处于**初始源码清点**，还未完成逐功能审计；下列介绍继承总图中已核对的职责，初始状态区分源码能力、范围与缺口。

## 2026-10-12 live-only会话流

events透传replay=false和Core的stream.ready/history.changed控制帧，继续使用现有SSE取消与cursor代理。快照屏障、run+seq去重由Desktop负责，水位查询与历史epoch由Core负责。统一任务[APP-HOME-108](../../app/home/TODO.md#app-home-108)，[报告](../../../../../.tinadec_dev/reports/2026-10-12-transcript-stability.zh-CN.md)给出回归和实际HTTP范围。

## 文档入口

- [模块架构与边界](ARCHITECTURE.md) · [模块SVG](architecture.svg)
- [功能与完成情况](STATUS.md) · [本模块TODO](TODO.md)
- [全部模块](../../../MODULE-INDEX.md) · [总TODO](../../../01-program/MASTER-TODO.md)

## 功能介绍

### streaming / 长连接

支持流式响应透传与取消，不在网关复制运行状态。模型预览/最终回答、工具活动在 Core 产生。

- SSE 与日志/附件响应体转发
- 入站 AbortSignal 传到上游
- 客户端断开 → 中止上游读取

## 源码入口

- [TinadecGateway/src/streaming.ts](../../../../../TinadecGateway/src/streaming.ts)
- [TinadecGateway/src/coreClient.ts](../../../../../TinadecGateway/src/coreClient.ts)
- [TinadecGateway/src/index.ts](../../../../../TinadecGateway/src/index.ts)

## 相关模块

- [Gateway / 项目、会话与运行控制](../session-control/README.md)
- [Lifecycle · 运行事实与恢复](../../core/Lifecycle/README.md)

## 本模块的开发工作方式

先拆分STATUS中的功能、核对实际行为并定位缺口，再逐项执行TODO。每次实现同步功能状态、模块图和证据；目标行为与验收边界写清后才进入该任务的实施。核查任务完成不代表该模块所有能力已经完成。
