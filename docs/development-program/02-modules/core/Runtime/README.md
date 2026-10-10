# Runtime · 唯一组合根

2026-10-11 `AgentModelResolver.PreviewBatchAsync`在同tenant/workspace读取绑定/版本集合，仅当前请求复用相同FrozenModelPlan和解析。单项Preview、策略优先级、运行Freeze/Invocation均保持原语义；已知配置失败局部不可用，取消/数据库/未知异常传播，不跨作用域/请求缓存。APP-HOME-107性能跟进及WindowsSQLite/真实HTTP证据见[报告](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)，Runtime整体审计另行保留。

模块ID：`CORE-RUNTIME` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本模块目前处于**初始源码清点**，还未完成逐功能审计；下列介绍继承总图中已核对的职责，初始状态区分源码能力、范围与缺口。

## 文档入口

2026-10-09：Runtime 以宿主登记管理独立 scope 服务图，负责项目初始化、恢复、自由会话转移、永久删除参与者及维护租约；不通过全局“当前项目”改写连接。见 [本轮对账](../../../../../.tinadec_dev/reports/2026-10-09-storage-reconstruction.zh-CN.md)，剩余平台与真实模型场景统一在 [X-DATA-104](../../cross-cutting/data-security/TODO.md#x-data-104) 跟踪。

- [模块架构与边界](ARCHITECTURE.md) · [模块SVG](architecture.svg)
- [功能与完成情况](STATUS.md) · [本模块TODO](TODO.md)
- [全部模块](../../../MODULE-INDEX.md) · [总TODO](../../../01-program/MASTER-TODO.md)

## 功能介绍

### Runtime · 唯一组合根

负责全模块装配和跨模块适配：身份边界、正式模式、模型策略、用户工具动作、恢复、就绪探针。可选择性装配模块。

- DI 装配 15 个 registrar / 恢复协调
- 模型解析、委托审批、组织唤醒、拓扑

## 源码入口

- [TinadecCore/Runtime/TinadecCoreServiceCollectionExtensions.cs:32](../../../../../TinadecCore/Runtime/TinadecCoreServiceCollectionExtensions.cs#L32)
- [TinadecCore/Runtime/GovernanceActionExecutor.cs:39](../../../../../TinadecCore/Runtime/GovernanceActionExecutor.cs#L39)
- [TinadecCore/Runtime/RecoveryCoordinator.cs](../../../../../TinadecCore/Runtime/RecoveryCoordinator.cs)

## 相关模块

- [Governance · 授权与审批](../Governance/README.md)
- [AgentConfiguration](../AgentConfiguration/README.md)
- [TinaChat · 会话组织通信](../TinaChat/README.md)
- [AgentGraph · 图与资源](../AgentGraph/README.md)

## 本模块的开发工作方式

先拆分STATUS中的功能、核对实际行为并定位缺口，再逐项执行TODO。每次实现同步功能状态、模块图和证据；目标行为与验收边界写清后才进入该任务的实施。核查任务完成不代表该模块所有能力已经完成。
