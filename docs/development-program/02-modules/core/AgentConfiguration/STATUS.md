# AgentConfiguration：功能与完成情况

2026-10-11目录性能接口回归跟进：当前配置副本20个published Agent、14模式、67节点，修复后Gateway列表1138ms、20份详情全部200；用户原配置不变。Core60项定向覆盖AgentRuntimeBinding/ConfigurationDocument/GraphSeed/AgentPackEndpoint，批量语义、错误、更新、取消及同键节点含在其中。[证据与限制](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)，任务[APP-HOME-107](../../app/home/TODO.md#app-home-107)；不继承为全平台或包服务全量验收。

模块ID：`CORE-AGENT-CONFIG` · 更新日期：2026-10-09 · 基线：b6115e6 + 当前工作树。

**审计阶段：配置存储链路已拆分并完成 Windows/SQLite、Linux/真实 PostgreSQL 定向验证；包服务全量审计、macOS 和完整发布产物验收仍未完成。** 不报告完成百分比，也不把源码存在折算为功能完成。

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| CORE-AGENT-CONFIG-F001 | 智能体/模式配置与包服务 | 源码可见 | 本轮静态核对；未做功能验收 | 拥有配置版本和包服务；正式解析按运行需要冻结配置，源码注册不能代表所有包安装/清理路径已完成验收。 | [TinadecCore/AgentConfiguration/AgentConfigurationModuleRegistrar.cs](../../../../../TinadecCore/AgentConfiguration/AgentConfigurationModuleRegistrar.cs)<br>[TinadecCore/Runtime/FormalModeResolver.cs](../../../../../TinadecCore/Runtime/FormalModeResolver.cs) |
| CORE-AGENT-CONFIG-F002 | Scope TOML 配置编辑权威与 SQL 投影 | 已验收 | 2026-10-09，Windows x64 / .NET 10 / 临时 SQLite 19/19；Fedora 44 / SDK 10.0.300 配置与真实 PG 20/20，原生绑定补正后均无 skip，限本文范围 | `config/agents.toml` 等当前配置由文件唯一决定；GUI EF 写入先原子 CAS 保存 TOML，再写 SQL。活动 MCP/Skills 绑定明确为 inherit/all/none/selected，拒绝 JSON null 哨兵与旧数组形状；可选工具值使用命名 mode。失效摘要、非法状态/ids 不覆盖旧文件；HTTP/Desktop 端到端另行验收。 | [ConfigurationProjectionCoordinator](../../../../../TinadecCore/Persistence/Configuration/ConfigurationProjectionCoordinator.cs)<br>[ConfigurationTomlValues](../../../../../TinadecCore/Persistence/Configuration/ConfigurationTomlValues.cs)<br>[原生绑定最终证据](../../../../../.tinadec_dev/reports/2026-10-09-native-toml-bindings.zh-CN.md) |
| CORE-AGENT-CONFIG-F003 | 当前配置与不可变历史版本分离 | 已验收 | 同上；历史退休与包正文独立重建测试，限本文范围 | TOML 删除旧版本不会删除 SQL 历史事实；新 default/current 绑定必须引用文件内可见版本。嵌入配置/manifest 正文在目标 scope 重新物化，不依赖用户 scope blob。旧运行继续使用其冻结引用。 | [ConfigurationLiveSourceValidation](../../../../../TinadecCore/Persistence/Configuration/ConfigurationLiveSourceValidation.cs)<br>[配置文件契约](../Persistence/CONFIGURATION-FILES.md) |
| CORE-AGENT-CONFIG-F004 | 新运行配置编译与准入摘要冻结 | 已验收 | 同上；编译摘要变更、连接切换阻准入测试，限本文范围 | 编译九份 scope TOML 并校验当前数据库 backend/ref；freeze 前复核摘要。文件非法或 storage 与当前连接不一致时拒绝新 run；Host 显式 apply/reopen 后再准入。 | [FrozenRunConfiguration](../../../../../TinadecCore/DmaEA/FrozenRunConfiguration.cs)<br>[HostConfigurationDocumentValidator](../../../../../TinadecCore/Persistence/Configuration/HostConfigurationDocumentValidator.cs) |
| CORE-AGENT-CONFIG-F005 | PostgreSQL schema 和 Linux 配置存储契约 | 已验收 | 2026-10-09，Fedora 44 WSL2 x64 / SDK 10.0.300 / PostgreSQL 18.6 隔离数据库，22/22 定向测试；增强 PG 删除预览另 1/1；原生 TOML 差分后 19 配置 + 1 PG = 20/20，均无 skip | 十二 DbContext 双随机 schema、相同 ID 独立、TOML 投影、rollback、用户根身份稳定/并发隔离已执行；删除预览后 PG 插入/修改须拒绝且保留事实，未变行摘要稳定。缺少显式 PG 环境仍 skip。限存储契约与定向运行，macOS 和完整桌面/发布产品未验收。 | [PostgreSqlScopeValidationTests](../../../../../TinadecCore/tests/TinadecCore.Api.Tests/PostgreSqlScopeValidationTests.cs)<br>[Linux 实测报告](../../../../../.tinadec_dev/reports/2026-10-09-linux-postgresql-validation.zh-CN.md) |

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

[本模块TODO](TODO.md) · [功能分析模板](../../../05-templates/FEATURE.md)
