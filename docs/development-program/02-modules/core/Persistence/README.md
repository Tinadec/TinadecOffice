# Persistence · 公共存储适配

2026-10-11当前配置投影按内容摘要复用已验证并成功应用文档，每次接入仍读源内容；同mtime/长度修改、失效、错误和跨scope规则有回归。公开配置和准入仍校验。[配置契约](CONFIGURATION-FILES.md)、[APP-HOME-107跟进证据](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)；本模块整体审计与其他平台保持独立。

模块ID：`CORE-PERSISTENCE` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本模块目前处于**初始源码清点**，还未完成逐功能审计；下列介绍继承总图中已核对的职责，初始状态区分源码能力、范围与缺口。

## 2026-10-10 多文件夹工作区

project.toml 新增 workspace 名称、稳定 roots ID/路径、primary_root_id、图标与颜色，旧单目录按单源只读。配置摘要、作用域门与文件租约共同控制原子保存，保留未知字段与注释；用户根宿主登记保存当前及历史目录授权。创建只初始化首选主要目录的一份存储，更换主要目录不移动存储。

PostgreSQL 的 EF 与原始向量连接固定同一作用域 schema，显式引用 pgvector 的实际扩展命名空间；新扩展放 public，既有扩展不自动迁移。历史版本的时间字段按 PostgreSQL 微秒保存精度比较，TOML 原字节及其他不可变字段保持严格校验。删除预览和执行拒绝包含数据库级扩展的作用域 schema，防止 CASCADE 破坏其他工作区。真实空库与已有独立扩展 namespace 的隔离、同名不同维度向量、配置重读、历史修改拒绝和重启均有本轮证据。

本专项统一由 [APP-HOME-107](../../app/home/TODO.md#app-home-107) 记账，CORE-PERSISTENCE 保留本模块整体审计；交互与存储契约见 [workspaces.zh-CN.md](../../../../workspaces.zh-CN.md)，本轮证据与平台边界见 [实施报告](../../../../../.tinadec_dev/reports/2026-10-10-sidebar-workspaces.zh-CN.md)。

## 文档入口

2026-10-09：存储与配置重构已加入作用域路径、独立数据库/schema、TOML 文档权威、投影和历史版本边界。各平台与真实数据库的验收单独记账，见 [本轮报告](../../../../../.tinadec_dev/reports/2026-10-09-storage-reconstruction.zh-CN.md) 和 [X-DATA-104](../../cross-cutting/data-security/TODO.md#x-data-104)。

- [模块架构与边界](ARCHITECTURE.md) · [模块SVG](architecture.svg)
- [配置文件权威、投影和历史事实](CONFIGURATION-FILES.md)
- [功能与完成情况](STATUS.md) · [本模块TODO](TODO.md)
- [全部模块](../../../MODULE-INDEX.md) · [总TODO](../../../01-program/MASTER-TODO.md)

## 功能介绍

### Persistence · 公共存储适配

公共数据库配置、内容路径/原子写、密钥引用与 nonce 存储；领域各自拥有 DbContext，当前共有 11 个。

- EF Core 配置 / StoragePaths / ContentStore
- SecretStore / nonce / 原子文件写入

## 源码入口

- [TinadecCore/Persistence/ServiceCollectionExtensions.cs](../../../../../TinadecCore/Persistence/ServiceCollectionExtensions.cs)

## 相关模块

- [数据、安全与持久化验收](../../cross-cutting/data-security/README.md)

## 本模块的开发工作方式

先拆分STATUS中的功能、核对实际行为并定位缺口，再逐项执行TODO。每次实现同步功能状态、模块图和证据；目标行为与验收边界写清后才进入该任务的实施。核查任务完成不代表该模块所有能力已经完成。
