# Gateway / SSE、附件、日志与取消：功能与完成情况

模块ID：`GW-STREAMING` · 清点日期：2026-10-05 · 基线：b6115e6 + 当前工作树。

**审计阶段：初始源码清点；逐功能审计未完成。** 不报告完成百分比，也不把历史全量绿或源码存在折算为功能完成。

| Feature ID | 功能/能力 | 实现判断 | 本轮验证层级 | 边界与剩余问题 | 证据 |
| --- | --- | --- | --- | --- | --- |
| GW-STREAMING-F001 | SSE、raw、日志和附件流的取消信号传播 | 部分实现 | Windows/Bun1.3.14真实socket对照、原生Response取消与路由定向 | 本轮修复chunked头触发Elysia二次包装；小夹具未复现真实Gateway的Bun弱引用断言，完整应用及其他平台独立。历史200/200上游关闭与1000次中断未重跑。 | [TinadecGateway/src/streamResponse.ts](../../../../../TinadecGateway/src/streamResponse.ts)<br>[本轮证据](../../../../../.tinadec_dev/evidence/2026-10-12-transcript-stability/gateway/)<br>[TinadecGateway/src/index.ts](../../../../../TinadecGateway/src/index.ts) |

## 2026-10-12 快照控制帧

live-only参数及无id的ready/history控制帧透明转发已实现，Gateway最终完整113项通过；客户端重连补快照与水位去重由Desktop/Core验收。统一[APP-HOME-108](../../app/home/TODO.md#app-home-108)，[报告](../../../../../.tinadec_dev/reports/2026-10-12-transcript-stability.zh-CN.md)保留真实与替身边界，不重标既有取消压力验收。

真实应用切会话时Bun1.3.14进程出现RequestContext.onAbort→响应弱引用断言，前端无未捕获异常、RSS25.76MiB。源码确认Elysia1.4.29在上游transfer-encoding=chunked且无content-length时重新包装Response。共享助手去除标准hop-by-hop及Connection声明头，Core/Tool Runtime SSE和stream统一使用，run/events/logs/附件路由返回原生Response，保留业务头和错误传播。隔离真实socket每模式100次：原包装96次AbortError，手工清理、生产助手、原生Bun各0；均未panic，不能据此宣布真实断言已消除。新增6项验证有限响应元数据、未结束流即时读取/取消/正文失败、全部流代理与5条路由，以及generic/Tool Runtime取消与502区分、携hostheader的manual redirect；全量113/113。证据脚本与JSON见上表，不含凭据或原始服务日志。

最终TypeScript类型门禁已通过：25处测试fake fetch的Bun preconnect结构强转改为unknown中转，3处可空fixture显式assert，未改产品逻辑或tsconfig；完整113项再次通过。历史28诊断保留于证据，不作为当前失败。

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
