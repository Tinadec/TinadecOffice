# Memory · 会话与长期记忆：功能与完成情况

模块ID：`CORE-MEMORY` · 清点日期：2026-10-05 · 基线：b6115e6 + 当前工作树。

**审计阶段：初始源码清点；逐功能审计未完成。** 不报告完成百分比，也不把历史全量绿或源码存在折算为功能完成。

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| CORE-MEMORY-F001 | 会话事实、附件与长期记忆审核 | 源码可见 | 本轮静态核对；未做功能验收 | 保存会话/消息/turn 与附件；长期记忆候选可采纳和撤销。存储存在不代表真实场景与跨租户验收完成。 | [TinadecCore/Memory/MemoryModuleRegistrar.cs](../../../../../TinadecCore/Memory/MemoryModuleRegistrar.cs)<br>[TinadecCore/Memory/ProjectSessionStore.cs](../../../../../TinadecCore/Memory/ProjectSessionStore.cs)<br>[TinadecCore/Memory/MessageAttachmentStore.cs](../../../../../TinadecCore/Memory/MessageAttachmentStore.cs) |
| CORE-MEMORY-F002 | 已采纳记忆当前按关键词评分检索 | 源码可见 | 本轮静态核对；未做功能验收 | 未接 VectorStore；语义检索是否属于必需产品行为需先决定，不能自动创造实现任务。 | [TinadecCore/Memory/MemoryModuleRegistrar.cs:107](../../../../../TinadecCore/Memory/MemoryModuleRegistrar.cs#L107) |
| CORE-MEMORY-F003 | 创建时固定平面/空间会话类型 | 已验收 | Core定向14/14（SQLite HTTP） | 历史null投影flat；未验证PostgreSQL | [报告](../../../../../.tinadec_dev/reports/2026-10-06-session-view-isolation.zh-CN.md) |
| CORE-MEMORY-F004 | scope 内会话事实与宿主协调项目转移 | 部分实现 | SQLite双scope与迁移定向 | ProjectId稳定；副本宿主ID独立；迁移排空已受理队列，后续运行使用目标配置；关联事实删除由Runtime组合。 | [报告](../../../../../.tinadec_dev/reports/2026-10-09-storage-reconstruction.zh-CN.md)<br>[ProjectSessionStore](../../../../../TinadecCore/Memory/ProjectSessionStore.cs) |

## 2026-10-12 对话流读取专项

消息keyset SQL分页、公开sequence、页内附件和派生revert_epoch已实现。10000条历史测试把旧9900正文指向缺失对象，最近100页仍200；追加不失效、撤回冲突与并发读只重试一次由API定向验收。唯一任务[APP-HOME-108](../../app/home/TODO.md#app-home-108)，[报告](../../../../../.tinadec_dev/reports/2026-10-12-transcript-stability.zh-CN.md)区分SQLite/HTTP/平台范围；不改变F001整体审计结论。

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
