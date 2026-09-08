> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# Core 运行时收尾实施计划（第 3+4+5 步）

## 摘要

按 `docs/DmaEAPlan.md` 交付顺序完成 Core 侧剩余工作：第 3 步收尾（依赖图调度、重启恢复、六类消息分类、上下文包补齐）、第 4 步全部（TinadecTools 子进程适配、工具注册表、完整 worker 工具调用循环、审批暂停/恢复、审计）、第 5 步收尾（记忆向量索引、替代/反馈/审计、进化生成）。不含 Gateway/Desktop 接入（第 6 步）和 withdocs 文档项目。同步更新滞后的 AGENTS.md。

范围依据：2026-08-18 全仓核查结论（第 1/2 步约 85%/80%，第 3 步约 75%，第 4 步约 10%，第 5 步约 55%）。

## 现状关键事实（已核实）

- `FullDuplexRunCoordinator` 已实现后台 run、流式 delta、监督两轮、pause/resume/cancel；缺口：无依赖图调度（`Parallel.ForEachAsync` 无序并发，`FullDuplexRunCoordinator.cs:237`）、无重启恢复、`ClassifyTurn` 仅 3 类（`:415`）。
- `ToolExecutionRecord` 实体/表/索引已存在（`LifecycleDbContext.cs:159`）但无任何服务写入；`ILifecycleManager` 无工具执行方法。
- `ApprovalRequestRecord` 已有 RunId/TaskId/ToolId/RequestHash/ExpiresAt/ConsumedByExecutionId 字段；`ControlPlaneService.DecideApproval` 未设置消费标记（一次性消费未强制）；审批端点用 `JsonElement` 内联，无 `GET /api/v1/approvals/{id}`。
- 架构测试（`ArchitectureTests.cs:102-118`）仅禁止 Core 程序集引用 TinadecTools，不禁止 `System.Diagnostics.Process` 启动子进程。
- TinadecTools 行式 JSON 协议就绪（`Program.cs:26-60`、`ToolCalling.cs`），但无 manifest/握手能力；`ToolRegistry.Handlers` 为 private。
- `/api/v1/tools`、`/api/v1/tools/search` 返回空数组，`/api/v1/runs/{runId}/tools/{toolId}/execute` 返回 501（`StubEndpoints.cs:111-117`）；`/api/v1/agent-evolution/generate|promote|reject` 501（`:157-159`）。
- 测试基建：`FullDuplexFactory` 通过替换 `IAgentChatClientFactory` 注入 `ScriptedChatClient`（`FullDuplexEndpointTests.cs:343-370`），现有 11 个端到端测试。
- 记忆：候选隔离/晋升/拒绝/撤销已实现；缺 kind 枚举校验（`MemoryModuleRegistrar.cs:274`）、agent 作用域检索（`:85-87`）、晋升向量索引、替代（`SupersededById` 从未赋值）、使用反馈、审计事件。
- Core 无任何 hosted service 先例；模块注册集中在 `TinadecCoreServiceCollectionExtensions.cs:25-43`。

## 设计决策（已定）

| 决策点 | 结论 |
|---|---|
| Core 与 TinadecTools 集成方式 | exe 子进程 + 行式 JSON，零程序集引用（满足架构测试）；Core 侧自建 wire DTO（snake_case） |
| manifest 握手 | TinadecTools 主循环内联处理保留 `tool_id = "#manifest"`，返回 `{protocol_version, tools:[{id,description,requires_approval}]}`；Core 启动/定期刷新缓存 |
| exe 路径配置 | appsettings 新增 `TinadecTools` 节（ExecutablePath、StartupTimeoutSeconds、ManifestRefreshMinutes、DefaultWorkspaceRoot）；TOML `[tools]` 只管策略不变 |
| 进程生命周期 | 每个工作区根目录一个进程，懒启动；崩溃后下次调用重启；按 `worker_retry_limit` 重试；进程退出时在途调用得到结构化 `process_exit` 失败 |
| 测试策略 | `IToolProcessManager` 为 DI seam，测试注入脚本化 fake（不发布真实 exe）；真实进程仅手动冒烟 |
| 审批门控 | `IToolApprovalGate` 单例 TCS 注册表；工具需审批 → 创建审批记录 → run 置 `awaiting_approval` → `DecideApproval` 释放门 → 校验 run/task/tool/参数哈希一致 → 消费时原子设置 `ConsumedByExecutionId`（二次消费 409） |
| worker 工具循环 | ExecutionAgent 每轮可输出 `{"tool_calls":[{tool_id,params}]}` JSON，coordinator 分派回填后进入下一轮；上限 `max_tool_rounds`（TOML 新键，默认 4）；超限强制收敛为文本结论 |
| 依赖图调度 | 标题→任务映射 + Kahn 拓扑分波执行；未知依赖丢弃并写警告事件；环检测失败退化为全并行 + 警告；失败任务的依赖方标记 `blocked`；被 revise 任务的依赖方一并重置 |
| 重启恢复 | 孤儿清理语义：启动时扫描非终态 run → `failed` + `run.recovered` 事件（reason=host_restart）+ 未完成 turn 置 failed + 取消残留审批门；不做执行续跑 |
| 消息分类 | 确定性关键词规则（可测试），顺序：control_command → status_query → goal_adjustment → supplement → question → targeted_message/new_task；question 无活跃 run 时走轻量 run（仅会议直答，无任务图） |
| usage 流块 | done 前发射 `usage` chunk，token 为估算值（`estimated:true`），不侵入 IAgentChatClient 抽象 |
| 向量索引 | 晋升时若 VectorStore 已配置则 `IndexAsync`，未配置降级 + `memory.vector.skipped` 警告事件；检索同理合并 |
| 进化生成 | experience_curator 用 LLM 从已完成 run 事件提炼记忆候选 + 智能体候选（JSON 协议解析，失败降级为空结果 + 事件），入库走现有 CreateCandidateAsync |
| 明确不做 | Gateway/Desktop（第 6 步）、DB 工作区覆盖优先级合并（第 2 步遗留）、`POST /api/v1/tools/shell` 保持 501（旧契约）、执行级重启续跑、真实 OpenAI usage 精确值 |

## 变更清单

### WP1 — Lifecycle 端口扩展 + 重启恢复

1. [ILifecycleManager.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/Abstractions/Ports/ILifecycleManager.cs) 新增端口方法：
   - `StartToolExecutionAsync(ToolExecutionStart)` / `CompleteToolExecutionAsync(executionId, resultRef, resultHash, resultLength)` / `FailToolExecutionAsync(executionId, errorCategory, safeMessage)`
   - `ListNonTerminalRunsAsync(ct)`（恢复扫描用）
2. [StorageLifecycleService.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/Lifecycle/StorageLifecycleService.cs) 实现上述方法：写 `tool_executions`（ParametersReference 走 IContentStore，ParametersHash=规范 JSON SHA-256）；非终态查询按 `RunStatusMachine` 终态集合取反。
3. 新建 `TinadecCore/Lifecycle/RunRecoveryHostedService.cs`（BackgroundService）：启动扫描 → 逐 run `SetRunStatusAsync("failed")` + `AppendEventAsync("run.recovered", {reason:"host_restart"})` → `CompleteTurnAsync(..., "failed")` → 通知 `IToolApprovalGate` 取消残留门。在 `LifecycleModuleRegistrar.cs` `AddHostedService` 注册（首个 hosted service 先例）。

### WP2 — TinadecTools manifest 能力

1. [ToolRegistry.cs](file:///c:/git/agent/TinadecOffice/TinadecTools/Abstractions/ToolRegistry.cs)：Register 时记录 `{id, description, requires_approval}` 描述符，新增 `public static IReadOnlyList<ToolDescriptor> ListTools()`（生成器注册路径与手动注册路径都覆盖）。
2. [Program.cs](file:///c:/git/agent/TinadecOffice/TinadecTools/Program.cs)：主循环分派前拦截 `tool_id == "#manifest"`，用现有 `ToolCallJsonContext` 序列化返回 `{protocol_version:1, tools:[...]}`；保持单行输出协议不变。
3. [TinadecTools.Tests] 补 manifest 行协议测试（现有 node/xUnit 测试体系内）。

### WP3 — Core Tools 模块（新项目 `TinadecCore/Tools/`）

1. 新建 `TinadecCore/Tools/TinadecCore.Tools.csproj`（net10.0，引用 Abstractions + Contracts + Persistence.Abstractions 所需最小集；**禁止引用 TinadecTools**）。加入 `TinadecCore.slnx` 与根 `TinadecOffice.slnx`，并加入 [ArchitectureTests.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/tests/TinadecCore.Architecture.Tests/ArchitectureTests.cs) 模块清单（行 24-30）。
2. Abstractions 新建端口（单文件 `IToolDispatcher.cs` 汇总）：
   - `IToolProcessManager`：`EnsureStartedAsync(workspaceRoot)`、`CallAsync(workspaceRoot, ToolWireRequest, timeout, ct)`、`GetManifestAsync(workspaceRoot, ct)`、`ShutdownAsync()`
   - `IToolRegistry`：`ListToolsAsync()`、`SearchToolsAsync(query)`、`FindToolAsync(toolId)`
   - `IToolDispatcher`：`ExecuteAsync(ToolDispatchRequest)` → `ToolDispatchResult`（含 status: completed/not_approved/rejected/timeout/process_exit/blocked）
   - `IToolApprovalGate`：`WaitAsync(approvalId, ct)`、`Resolve(approvalId, decision)`、`Cancel(approvalId)`
3. Contracts 新建 `ToolExecutionDtos.cs`（wire DTO + descriptor + dispatch request/result，全部 snake_case）。
4. `ToolsModuleRegistrar.cs`：注册 `TinadecToolsProcessManager`（hosted）、`CoreToolRegistry`、`ToolDispatcher`、`PendingApprovalGate`；模块顺序插在 DmaEA 之前（[TinadecCoreServiceCollectionExtensions.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/Runtime/TinadecCoreServiceCollectionExtensions.cs)）。
5. `TinadecToolsProcessManager.cs`：按 workspaceRoot 字典管理进程；`ProcessStartInfo` 重定向 stdio（UTF-8）；单写锁 + `Interlocked` call_id 关联应答；逐行读循环；调用超时用 CTS（默认取 TOML `tools.default_timeout_seconds`）；进程退出 → 在途调用结构化失败；下次调用自动重启；启动后拉取并缓存 manifest（`#manifest`）。
6. `ToolDispatcher.cs` 主流程：registry 查 descriptor（未知工具 → blocked/unknown_tool）→ 校验 agent 实例 AllowedTools ⊇ toolId → `StartToolExecutionAsync` → 若 `requires_approval && policy.MutationRequiresApproval`：经 ControlPlaneService 创建审批（绑定 run/task/tool/参数哈希）→ run 置 `awaiting_approval` + `approval.requested` 事件 + feed chunk → `gate.WaitAsync` → 批准后 `ConsumeApprovalAsync`（原子一次性）才发 `approved:true`；拒绝/过期 → not_approved → 写工作区互斥（`SemaphoreSlim` per workspaceRoot，policy.SerializeWorkspaceWrites 且工具为写）→ `CallAsync` → 持久化结果（成功/失败均写 ContentStore 引用 + hash）→ `tool.execution.completed|failed` 事件；进程崩溃/超时按 `worker_retry_limit` 重试。
7. [appsettings.json](file:///c:/git/agent/TinadecOffice/TinadecCore/Api/appsettings.json) 新增 `TinadecTools` 节；`default-agent-runtime.toml` `[tools]` 新增 `max_tool_rounds = 4` 并扩展 `ToolRuntimePolicy`（[AgentRuntimeConfiguration.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/DmaEA/Configuration/AgentRuntimeConfiguration.cs) 行 21 record）。

### WP4 — 审批契约固化 + 一次性消费

1. Contracts 新建 `ApprovalDtos.cs`：`ApprovalCreateRequest{session_id,run_id?,task_id?,agent_instance_id?,kind,tool_id,parameters,summary}`、`ApprovalDecisionRequest{decision,reason?}`、`ApprovalResponse`（服务端计算 request_hash，不再信任客户端传入）。
2. [ControlPlaneService.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/Runtime/ControlPlaneService.cs)：`CreateApproval` 改强类型并落 run/task/tool/params 哈希绑定；`DecideApproval` 决策后调用 `IToolApprovalGate.Resolve`；新增 `ConsumeApprovalAsync(approvalId, executionId)`（校验 approved + 未消费 + 未过期 + 哈希匹配，CAS 设置 `ConsumedByExecutionId`，重复消费抛 Conflict）；新增 `GetApprovalAsync`。
3. [ControlPlaneEndpoints.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/Api/Endpoints/ControlPlaneEndpoints.cs)：换 typed DTO，新增 `GET /api/v1/approvals/{id}`。

### WP5 — Coordinator 集成（调度 + 工具循环 + 分类 + usage + 上下文）

全部改动集中在 [FullDuplexRunCoordinator.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/DmaEA/FullDuplexRunCoordinator.cs)、[ExecutionAgent.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/DmaEA/ExecutionAgent.cs)、[ContextModuleRegistrar.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/Context/ContextModuleRegistrar.cs)：

1. **依赖图调度**：`PlanAsync` 后构建标题索引 + Kahn 分波；`pending` 波次选择替换现 `Parallel.ForEachAsync` 输入；未知依赖/环写 `task.graph.warning` 事件并退化为全并行；失败任务依赖方置 `blocked`；revise 重置被改任务的依赖方。`task_graph.created` 事件 payload 增加波次结构。
2. **worker 工具循环**：ExecutionAgent 系统提示增加工具协议（输出 `{"tool_calls":[...]}` 或最终文本）；解析器仿 `PlanningAgent.TryParseTasks` 花括号截取；每轮工具调用走 `IToolDispatcher.ExecuteAsync`（带 runId/taskId/agentInstanceId）；工具结果累积进下一轮 prompt；`max_tool_rounds` 超限强制文本收敛；审批等待期间 run 停在 `awaiting_approval`，门释放后恢复 `executing`；每轮发布 `tool` 进度 feed chunk + lifecycle 事件。
3. **六类分类**：`ClassifyTurn` 扩展为有序规则（control_command：`/pause|/resume|/cancel` 或 暂停/恢复/取消 前缀且有活跃 run；status_query：含 状态/进度/status/progress；goal_adjustment：改成/改为/目标调整/instead；supplement：补充/另外/additionally 且有活跃 run；question：?/？结尾或疑问词开头且无活跃 run；其余维持 targeted_message/new_task）。control_command → 转发 `ControlAsync`；goal_adjustment/supplement → 新增 `SupplementAsync`（追加 context patch + `context.patched` 事件 + 触发 replan 轮）；question 无活跃 run → 轻量 run（understanding → 会议直答流式 → completed，无任务图）。
4. **usage chunk**：`done` 前发布 `kind:"usage"` chunk（prompt/completion tokens 估算 + `estimated:true`）。
5. **上下文包补齐**：`ContextModuleRegistrar` 新增 `task_state`（当前 run 任务快照摘要）与 `tool_capabilities`（registry manifest 的 id/description/requires_approval 列表）两类 evidence。

### WP6 — 记忆体系收尾

[MemoryModuleRegistrar.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/Memory/MemoryModuleRegistrar.cs)：

1. `ValidateProposal`：Kind 限定七类枚举（fact/preference/decision/success_pattern/failure_pattern/task_template/supervision_rule）。
2. `RetrieveAsync` 补 agent 作用域；VectorStore 已配置时合并向量命中（未配置跳过）。
3. 晋升：`promoted` 分支调用 `IVectorStore.IndexAsync`（未配置 → `memory.vector.skipped` 警告事件）；新增 supersede 流程（晋升时指定 `supersedes_item_id` → 旧 item `status=superseded` + `SupersededById` 赋值）。
4. 新增 `RecordFeedbackAsync(itemId, useful, note)`：计数 + `memory.feedback.recorded` 审计事件。
5. decide/revoke/promote/supersede 全部追加 lifecycle 审计事件（`memory.candidate.promoted|rejected`、`memory.item.revoked|superseded`）。
6. [MemoryReviewEndpoints.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/Api/Endpoints/MemoryReviewEndpoints.cs)：新增 `POST /api/v1/memory/{id}/feedback`、晋升请求支持 `supersedes_item_id`。

### WP7 — 进化生成 + 旧端点复活

1. 新建 `EvolutionEndpoints.cs`：`POST /api/v1/agent-evolution/generate`（入参 session_id 或 run_id）→ 取已完成 run 的事件回放（`ReplayEventsAsync`）→ experience_curator 提示词（经 `IAgentChatClientFactory`）→ 解析 JSON 候选（memory: kind/title/content/evidence/applicability/expiry_condition/confidence；agent: role/goal/success_criteria/confidence）→ 批量 `CreateCandidateAsync`；解析失败降级空结果 + `evolution.generate.failed` 事件。
2. `/api/v1/agent-evolution/proposals/{candidateId}/promote|reject`（[StubEndpoints.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/Api/Endpoints/StubEndpoints.cs) 行 157-159 删除）→ 委托 `IAgentInstanceService` 现有 decide/promote；`/api/v1/tools`、`/api/v1/tools/search`、`/api/v1/runs/{runId}/tools/{toolId}/execute`（行 111-117 删除）→ 新建 `ToolEndpoints.cs` 实装（list/search 走 registry；execute 走 dispatcher，需 run 存在且非终态）。
3. 旧 orchestrator `planning` 规范化：[DmaEAModuleRegistrar.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/DmaEA/DmaEAModuleRegistrar.cs) 行 98 改为经 `NormalizeLayer` 兼容读取 operation。

### WP8 — 测试（随各 WP 同步编写）

`TinadecCore.Api.Tests`（扩展 FullDuplexFactory：注册 fake `IToolProcessManager`（脚本化 manifest/应答/崩溃/超时）+ ScriptedChatClient 增加 tool_calls 轮次脚本）：

1. E2E 验收（计划文档场景）：invoke-stream → worker 触发需审批工具 → run `awaiting_approval` → `POST /approvals/{id}/decision` approved → 恢复执行 → `done` + 仅 1 条 assistant 消息 + tool_executions 落库。
2. 审批篡改/复用：参数哈希不匹配 → 拒绝执行；同审批二次消费 → 409；过期 → 409；跨 run 复用 → 拒绝。
3. 工具失败面：进程崩溃/超时 → 结构化失败 + 按 retry_limit 重试；未批准工具直接 not_approved。
4. 依赖图：A→B→C 顺序事件断言；C 依赖失败 → blocked；环 → 退化并行 + 警告事件。
5. 重启恢复：预置非终态 run 行 → 新宿主启动 → failed + `run.recovered` + turn failed。
6. 分类：status/control/supplement/goal_adjustment/question 各一路径断言。
7. registry list/search（fake manifest）；usage chunk 存在且 estimated。
8. 记忆：promote → fake IVectorStore.IndexAsync 被调；supersede 链；feedback 端点；agent 作用域检索；kind 非法 400。
9. 进化生成：脚本 chat → 候选创建且不可检索。
10. 回归：现有 11 个 FullDuplex 测试 + Architecture 测试（新模块入清单且不引用 TinadecTools）+ TinadecTools.Tests manifest 测试。

### WP9 — AGENTS.md 同步（收尾）

按 AI MAINTENANCE PROTOCOL 更新（引用本轮源码证据，不含 withdocs）：
- 根 [AGENTS.md](file:///c:/git/agent/TinadecOffice/AGENTS.md) CURRENT REBUILD STATE：移除"全双工/spawn/记忆晋升未实现"的过时表述，写入第 3/4/5 步完成状态与遗留（DB 工作区覆盖、执行级续跑、Gateway/Desktop 未接）。
- `TinadecCore/AGENTS.md`：同步 NOT IMPLEMENTED 清单、新 Tools 模块、hosted service、审批消费语义、端点现状。
- 两处元数据（Last Updated/By/Verified Commit/Branch）刷新。

## 实施顺序

WP1 → WP2 → WP3 → WP4 → WP5 → WP6 → WP7 →（WP8 随写）→ WP9。
WP3 依赖 WP1（tool_executions 持久化）与 WP2（manifest）；WP5 依赖 WP3/WP4；WP7 依赖 WP3。

## 验证步骤

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 build TinadecCore/TinadecCore.slnx --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 test TinadecCore/TinadecCore.slnx --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 test tests/TinadecTools.Tests/TinadecTools.Tests.csproj -v minimal
```

验收 = WP8 全部测试通过 + 现有测试零回归 + 架构测试通过。

## 风险与回退

- 子进程 stdio 死锁：单写锁 + 行读循环 + 超时 CTS；测试覆盖崩溃/超时路径。
- 分类关键词误判：确定性规则优先可测试性，误判仅影响路由不影响安全（审批门独立）；后续可换 LLM 分类。
- RunRecoveryHostedService 与多实例部署：仅单节点语义（与内存 feed 一致），文档注明。
