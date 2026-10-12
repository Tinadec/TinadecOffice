# Memory · 会话与长期记忆

模块ID：`CORE-MEMORY` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本模块目前处于**初始源码清点**，还未完成逐功能审计；下列介绍继承总图中已核对的职责，初始状态区分源码能力、范围与缺口。

## 2026-10-12 消息分页

ProjectSessionStore保留数组读取，并新增按scope/session/sequence的排他游标分页：SQL Take101，仅物化100条正文，再升序返回。页内附件使用SQL MessageIds筛选。revert_epoch从已撤回行数派生，读前/后检查；普通追加保持旧游标可用，首页最多重读一次。统一任务[APP-HOME-108](../../app/home/TODO.md#app-home-108)，[证据报告](../../../../../.tinadec_dev/reports/2026-10-12-transcript-stability.zh-CN.md)记录SQLite及真实HTTP范围，不推算整体记忆完成度。

## 文档入口

- [模块架构与边界](ARCHITECTURE.md) · [模块SVG](architecture.svg)
- [功能与完成情况](STATUS.md) · [本模块TODO](TODO.md)
- [全部模块](../../../MODULE-INDEX.md) · [总TODO](../../../01-program/MASTER-TODO.md)

## 功能介绍

### Memory · 会话与长期记忆

保存会话消息与长期记忆，和Context本轮输入组装不同。已审核记忆当前按关键词评分检索，没有接通向量检索；证据archive的混合检索在AgentGraph。

- session / message / turn / context / memory
- 候选审查、关键词排序与正文引用

## 源码入口

- [TinadecCore/Memory/MemoryModuleRegistrar.cs:43](../../../../../TinadecCore/Memory/MemoryModuleRegistrar.cs#L43)
- [TinadecCore/Memory/MemoryModuleRegistrar.cs](../../../../../TinadecCore/Memory/MemoryModuleRegistrar.cs)
- [TinadecCore/Memory/ProjectSessionStore.cs](../../../../../TinadecCore/Memory/ProjectSessionStore.cs)
- [TinadecCore/Memory/MessageAttachmentStore.cs](../../../../../TinadecCore/Memory/MessageAttachmentStore.cs)
- [TinadecCore/Memory/MemoryModuleRegistrar.cs:107](../../../../../TinadecCore/Memory/MemoryModuleRegistrar.cs#L107)

## 相关模块

- [数据、安全与持久化验收](../../cross-cutting/data-security/README.md)
- [Strategies · F# 纯策略](../Strategies/README.md)
- [AgentGraph · 图与资源](../AgentGraph/README.md)

## 本模块的开发工作方式

先拆分STATUS中的功能、核对实际行为并定位缺口，再逐项执行TODO。每次实现同步功能状态、模块图和证据；目标行为与验收边界写清后才进入该任务的实施。核查任务完成不代表该模块所有能力已经完成。
