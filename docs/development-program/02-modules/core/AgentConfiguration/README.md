# AgentConfiguration

2026-10-11目录性能专项：HTTP列表过滤后一次批量模型预览，草稿/published同逻辑节点键去重；策略优先级和公开DTO不改变。Runtime的请求内复用与Persistence已应用摘要减少重复配置读取；写/安装/准入继续原契约。[报告](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)区分真实HTTP和完整产品/平台边界，唯一任务[APP-HOME-107](../../app/home/TODO.md#app-home-107)。

模块ID：`CORE-AGENT-CONFIG` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本模块已完成配置文件权威、SQL 编辑投影和运行冻结链路的拆分，Windows/SQLite 及 Linux/真实 PostgreSQL 的定向验证均有本轮证据；包生命周期的完整审计、macOS 和发布产品验收仍未完成，范围分别记录在 STATUS 和 TODO。

## 文档入口

- [模块架构与边界](ARCHITECTURE.md) · [模块SVG](architecture.svg)
- [功能与完成情况](STATUS.md) · [本模块TODO](TODO.md)
- [Scope 配置文件契约](../Persistence/CONFIGURATION-FILES.md)
- [全部模块](../../../MODULE-INDEX.md) · [总TODO](../../../01-program/MASTER-TODO.md)

## 功能介绍

### AgentConfiguration

管理智能体与模式的版本、包归属与启禁、安装预览和清理；正式版本以不可变快照参与运行。

- agent / mode / pack 生命周期
- 草稿→发布版本→run 冻结
- 当前编辑配置保存到本 scope TOML，SQL 提供可重建编辑投影和不可变历史版本事实
- 新 run 编译文件、校验可见版本与实际存储连接，冻结前复核配置摘要

## 源码入口

- [TinadecCore/AgentConfiguration/AgentConfigurationModuleRegistrar.cs](../../../../../TinadecCore/AgentConfiguration/AgentConfigurationModuleRegistrar.cs)
- [TinadecCore/Runtime/FormalModeResolver.cs](../../../../../TinadecCore/Runtime/FormalModeResolver.cs)

## 相关模块

- [Runtime · 唯一组合根](../Runtime/README.md)
- [Models · 模型与 Harness](../Models/README.md)

## 本模块的开发工作方式

先拆分STATUS中的功能、核对实际行为并定位缺口，再逐项执行TODO。每次实现同步功能状态、模块图和证据；目标行为与验收边界写清后才进入该任务的实施。核查任务完成不代表该模块所有能力已经完成。
