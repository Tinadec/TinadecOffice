# Lifecycle · 运行事实与恢复

模块ID：`CORE-LIFECYCLE` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本模块目前处于**初始源码清点**，还未完成逐功能审计；下列介绍继承总图中已核对的职责，初始状态区分源码能力、范围与缺口。

## 2026-10-12 活动与增量事件读取

StorageLifecycleService按请求run及活跃run筛选活动，SQL约束保留tenant/workspace/session。日志物化按run复用文件句柄，工具时间线run_id/limit实际生效。live-only SSE建立每run水位后发送stream.ready，只查水位变化run的新事件；历史撤回发无业务cursor的history.changed。统一任务[APP-HOME-108](../../app/home/TODO.md#app-home-108)，[报告](../../../../../.tinadec_dev/reports/2026-10-12-transcript-stability.zh-CN.md)记录相关事件/跨窗口与隔离运行证据。

## 文档入口

- [模块架构与边界](ARCHITECTURE.md) · [模块SVG](architecture.svg)
- [功能与完成情况](STATUS.md) · [本模块TODO](TODO.md)
- [全部模块](../../../MODULE-INDEX.md) · [总TODO](../../../01-program/MASTER-TODO.md)

## 功能介绍

### Lifecycle · 运行事实与恢复

保存运行事实、事件与控制记录，支撑SSE回放和恢复判断；turn表属于Memory。父子run存储/级联控制基础已有，完整执行子run与恢复计划UX仍未闭环。

- run / tool execution / approval / event
- checkpoint / stream / 审计 / 工作区快照

## 源码入口

- [TinadecCore/Lifecycle/LifecycleModuleRegistrar.cs](../../../../../TinadecCore/Lifecycle/LifecycleModuleRegistrar.cs)
- [TinadecCore/Lifecycle/LifecycleModuleRegistrar.cs:81](../../../../../TinadecCore/Lifecycle/LifecycleModuleRegistrar.cs#L81)
- [TinadecCore/Lifecycle/StorageLifecycleService.cs:356](../../../../../TinadecCore/Lifecycle/StorageLifecycleService.cs#L356)

## 相关模块

- [DmaEA / 双层调用与持久运行引擎](../DmaEA/README.md)
- [Persistence · 公共存储适配](../Persistence/README.md)
- [数据、安全与持久化验收](../../cross-cutting/data-security/README.md)
- [Gateway / SSE、附件、日志与取消](../../gateway/streaming/README.md)

## 本模块的开发工作方式

先拆分STATUS中的功能、核对实际行为并定位缺口，再逐项执行TODO。每次实现同步功能状态、模块图和证据；目标行为与验收边界写清后才进入该任务的实施。核查任务完成不代表该模块所有能力已经完成。
