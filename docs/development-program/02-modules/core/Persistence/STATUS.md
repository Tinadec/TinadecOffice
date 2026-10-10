# Persistence · 公共存储适配：功能与完成情况

2026-10-11投影摘要复用专项：Windows隔离SQLite21项ConfigurationDocument包含源编辑检测、失败不缓存、写入失效和scope隔离；公开Read/Compile/Verify继续校验。真实配置副本HTTP与其他回归见[报告](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)，唯一接口跟进任务[APP-HOME-107](../../app/home/TODO.md#app-home-107)。既有存储全平台/整体状态不改变。

模块ID：`CORE-PERSISTENCE` · 清点日期：2026-10-05 · 基线：b6115e6 + 当前工作树。

**审计阶段：初始源码清点；逐功能审计未完成。** 不报告完成百分比，也不把历史全量绿或源码存在折算为功能完成。

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| CORE-PERSISTENCE-F001 | 公共数据库与内容/密钥/nonce 适配 | 源码可见 | 本轮静态核对；未做功能验收 | 注册数据库配置、内容存储、SQLite/PostgreSQL 项目向量库、secret、nonce 和迁移调度；领域各自拥有 DbContext。 | [TinadecCore/Persistence/ServiceCollectionExtensions.cs](../../../../../TinadecCore/Persistence/ServiceCollectionExtensions.cs) |
| CORE-PERSISTENCE-F002 | 不可变作用域路径与数据库隔离 | 部分实现 | Windows最终作用域/日志/向量15/15；Linux真实PG十二context/双schema | 解析无创建副作用；SQLite独立数据库及句柄释放；用户持久UUID、PG schema/model-cache/migration-history隔离实际验证，完整业务/销毁/导出仍待验收。 | [StorageScopePaths](../../../../../TinadecCore/Persistence/StorageScopePaths.cs)<br>[验收账本](../../../../../.tinadec_dev/evidence/2026-10-09-storage/VALIDATION.md) |
| CORE-PERSISTENCE-F003 | 配置文档权威、投影重建及内容流租约 | 部分实现 | TOML/CAS/注释/冻结与真实文件测试；GraphSeed过滤唯一索引专项 | live 投影可重建，历史版本与运行事实保留；唯一性遵守draft/未删除条件，未知谓词拒绝；活动提示词版本用真实pipeline键校验；内容不是缓存，完整跨平台仍待验收。 | [配置模块说明](CONFIGURATION-FILES.md)<br>[GraphSeed修复](../../../../../.tinadec_dev/reports/2026-10-09-graphseed-install-fix.zh-CN.md)<br>[资源报告](../../../../../.tinadec_dev/reports/2026-10-09-storage-resources.zh-CN.md) |

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
