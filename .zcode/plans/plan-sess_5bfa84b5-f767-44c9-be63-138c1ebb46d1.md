> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# DmaEA 全双工运行时落地：Core HTTP 接线 + 监督闭环 + 状态直答 + E2E 测试

依据：`C:\git\agent\withdocs`（双层智能体架构、全双工智能体配置文档）+ `DmaEAPlan.md` + 已核验的 working tree 现状（coordinator/spawn/记忆候选等服务层已实现，缺 HTTP 面、真实监督、测试）。

## Milestone 1 — Core HTTP 接线（消除 Gateway 代理 404）
- `DmaeaEndpoints.cs` 新增：`POST runs/{runId}/control`（pause/resume/cancel）、`GET runs/{runId}/orchestration`（run 范围投影）、`GET runs/{runId}/agent-lineage`、`GET sessions/{id}/context-versions`、`GET application-modes`、`GET agent-modes?application_mode=`（TOML 驱动，替换硬编码 stub，保留旧 DTO 字段兼容 Desktop）
- 新建 `MemoryReviewEndpoints.cs`：`GET memory-candidates` + promote/reject、`GET agent-candidates` + promote/reject（走 `ILongTermMemoryService` / `IAgentInstanceService`）
- `ControlPlaneEndpoints.cs` 删除被替换的 `agent-candidates`/`agent-modes` stub（防重复路由启动冲突）
- `Program.cs` 挂载新端点；`/api/v1/readiness` 增加 `agent_runtime` 配置诊断段

## Milestone 2 — 真实监督闭环（withdocs pass/revise/escalate）
- 新建 `SupervisionAgent.cs`：模型评审任务+证据 → JSON `{decision, reasons, revise_task_indexes}`；失败/不可用 → escalate（不伪造 pass）
- `FullDuplexRunCoordinator`：`supervision.requested/completed` 事件 + revise 重执行循环（≤ max_revision_rounds=2）+ escalate 终止并要求会议智能体向用户显式说明
- 取消语义：cancel 发 `kind:done, finish_reason:"cancelled"`（不再走 error）；新增 `task.accepted` 事件；不伪造 `context.compacted`/`memory.candidate_created`

## Milestone 3 — target_run_id 状态查询直答
- `SubmitAsync`：`target_run_id` + 活跃 run + status_query → 不新建 run，绑定 turn → 组装 run 状态 → 会议智能体流式直答 → 持久化 assistant → done

## Milestone 4 — 测试
- 引入 `IAgentChatClientFactory` port（默认= `PlanningAgent.DefaultChatClient`），全链路经它取 client；测试注入假 `IChatResolver`+假 `IChatClient`
- 新建 `FullDuplexEndpointTests`：成功路径（ack→delta→done、单条 assistant、实例 released）、幂等 client_message_id、409 上下文冲突/活跃 run 限流、pause/resume/cancel、监督 revise→pass 与 escalate 脚本、全部新端点契约、memory/agent 候选晋升流程
- SQLite/PostgreSQL 契约测试补新表断言；既有测试保持全绿

## Milestone 5 — 文档对齐
- 修正根与 TinadecCore `AGENTS.md`（当前"尚未完成"描述已失真）+ 刷新 metadata；`docs/architecture.md` 补状态机/端点/事件映射

## 验证
- `setup-dotnet-env.ps1 build/test TinadecCore/TinadecCore.slnx`
- `TinadecGateway` test + build（Gateway 改动已在 tree）
- 不执行 git commit（Mimosa 门禁历史 open high；由你决定提交时机）

## 明确不做（后续工作项）
TinadecTools 子进程+审批门（第 4 步）、run 内进化智能体候选生成（第 5 步）、Desktop UI（第 6 步）、上下文压缩智能体、embedding 检索、usage 流块