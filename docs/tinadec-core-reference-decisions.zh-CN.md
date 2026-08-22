# TinadecCore 参考项目调研与设计决策

> 调研日期：2026-08-22
> 范围：工作区内 `agent-framework`、`lindexi-agent`、`traycer`、`dify`、`opencode`、`langchain`、`langgraph`、`t3code`、`PlanWeave`、`grok-build` 只读源码
> 结论去向：[TinadecCore 产品定义与 DmaEA 架构基线](tinadec-core-product-definition.zh-CN.md)

本文记录 TinadecCore 产品定义背后的证据和取舍。它不是项目功能榜单，也不表示要把参考项目拼接进 TinadecCore。采用项必须符合 Core 状态权威、DmaEA 双层治理、最小权限和独立产品边界。

用户原始材料中的 `langchian` 按 `langchain` 理解，`t3 code` 按工作区的 `t3code` 理解。

源码结论基于下列本地检出版本；`lindexi-agent` 目录没有 Git 元数据，因此只能标记为本次调研时的工作区快照。

| 仓库 | 分支 | 修订 |
| --- | --- | --- |
| `agent-framework` | `main` | `cabb21a292cd` |
| `lindexi-agent` | 不可判定 | 2026-08-22 工作区快照 |
| `traycer` | `main` | `c4ed8cfd51b2` |
| `dify` | `main` | `785ca4b915da` |
| `opencode` | `dev` | `1b937c860b6f` |
| `langchain` | `master` | `2a91b2f5841e` |
| `langgraph` | `main` | `f09cfe8ffc1e` |
| `PlanWeave` | `main` | `220a2f8d0f04` |
| `grok-build` | `main` | `19d42e35c07a` |
| `t3code` | `main` | `be7d35aaeb49` |

## 1. 总体结论

跨项目反复出现、且适合 TinadecCore 的机制是：

1. 不可变、版本化的 Agent 定义，把模型、Prompt、工具、技能和能力策略放在同一发布快照中。
2. 治理拓扑与执行 DAG 分离；入口协调者不应同时承担所有执行和审查工作。
3. 权限声明与角色声明分离，子级权限受不可突破的委托上限约束。
4. 审批是持久业务对象，不是内存回调或聊天中的一句“同意”。
5. 追加事件、幂等命令、连续序号、checkpoint 和 cursor 共同支撑长运行与断线恢复。
6. 对话上下文、运行状态和文件/VCS 状态分别快照，再用恢复点关联。
7. 压缩前必须闭合工具调用；先清理可重取的大结果，再做增量摘要。
8. 任务依赖、claim、review gate、反馈回路和并发限制必须显式化。
9. 简单问答与复杂多智能体模式应共享一个内核，只改变激活拓扑。

明确不采用的方向是：

- 不让所有治理智能体持续加入同一群聊。
- 不把运行时 checkpoint 当作文件回滚。
- 不采用“子 Agent 只继承父级 deny”的宽松委托模型。
- 不允许普通审批 hook 失败后自动放行写操作。
- 不在 MVP 复制云同步、跨 Host 协议矩阵、内容寻址分片或完整多服务平台。
- 不把针对软件工程的庞大文件图模型硬编码成通用 Core 领域模型。

## 2. 决策映射

| TinadecCore 决策 | 主要证据来源 | 采用方式 |
| --- | --- | --- |
| Agent 独立模型/Prompt/工具/记忆 | lindexi-agent、OpenCode、Grok Build | 版本化 `AgentDefinition`，运行时使用不可变 `AgentVersion` |
| 治理层 + 执行层 | PlanWeave、Dify、LangGraph、lindexi-agent | meeting 负责入口和粗计划，task planner 负责执行 DAG，review 独立 |
| 权限求交与委托上限 | Traycer、OpenCode、Grok Build | 角色不授予权限；组织/用户/父级/Agent/Mode/Run/Manifest 求交 |
| 持久审批与恢复 | LangGraph、LangChain、Dify、OpenCode | `ApprovalRequest` + 决策 + 参数哈希 + TTL + 一次性消费 |
| append-only 事件与 cursor | Traycer、Dify、LangGraph、lindexi-agent | sequence、幂等 command、snapshot 后 replay/follow |
| 三类快照 | lindexi-agent、LangGraph、OpenCode、Grok Build、t3code | Conversation、Run、Workspace 独立，由 RestorePoint 关联 |
| 分层上下文压缩 | Dify、LangChain、lindexi-agent、Grok Build | 未闭合工具调用保护、旧结果清理、增量摘要、长期记忆召回 |
| 显式任务图和 review gate | PlanWeave、LangGraph | task DAG、claim receipt、`needs_changes` 回流、并发上限 |
| Agent 演化候选 | OpenCode、Grok Build、Dify | 临时实例只生成候选，验证和晋升产生新不可变版本 |
| 控制面/运行面分离 | Dify、Traycer、MAF | 配置与发布属控制面；run、租约、checkpoint 属运行面 |

## 3. Microsoft Agent Framework：技术底座

### 证据

- `agent-framework/dotnet/src/Microsoft.Agents.AI.Abstractions/AIAgent.cs`、`AgentSession.cs` 和 `AIContextProvider.cs`：Agent、会话状态和上下文扩展的基础抽象。
- `agent-framework/dotnet/src/Microsoft.Agents.AI/ChatClient/ChatClientAgent.cs`：在 `IChatClient` 上实现通用 Agent 调用和流式响应。
- `agent-framework/dotnet/src/Microsoft.Agents.AI.Workflows/WorkflowBuilder.cs`、`Checkpointing/ICheckpointStore.cs` 和 `WorkflowSession.cs`：工作流构建、checkpoint 存储和可恢复外部请求。
- `agent-framework/dotnet/src/Microsoft.Agents.AI/ChatClient/ApprovalResponseBindingChatClient.cs`：把审批响应重新绑定到 session 中已知请求，丢弃无对应请求的伪造响应。
- `agent-framework/dotnet/src/Microsoft.Agents.AI/Skills/AgentSkillsProvider.cs`：Skill provider 扩展点。
- `agent-framework/dotnet/src/Microsoft.Agents.AI/OpenTelemetryAgent.cs` 与 `Microsoft.Agents.AI.Workflows/OpenTelemetryWorkflowBuilderExtensions.cs`：Agent/Workflow 的 OpenTelemetry 包装。
- `agent-framework/docs/decisions/0006-userapproval.md`：远程审批不能只依赖调用栈内 callback，应使用 request/response 内容并允许暂停后恢复。
- `agent-framework/docs/decisions/0019-python-context-compaction-strategy.md` 与 `0022-chat-history-persistence-consistency.md`：压缩和历史持久化需要明确一致性边界。

### 可借鉴

- 使用 MAF 的 Agent 和模型抽象承接 provider 差异，不在 DmaEA 中重写模型客户端。
- 使用 Workflow/Executor、事件和 checkpoint 原语承接单次执行图；DmaEA 在其上增加双层拓扑、权限、任务和产品状态机。
- 使用 session、context provider、history/memory provider、middleware、skills 和 OpenTelemetry 集成点。
- 保持 MAF 对象只在 DmaEA 内部适配器内，HTTP DTO、领域事件、checkpoint 和持久化契约使用 Tinadec 自有稳定类型。
- 使用 MAF 1.18 的原子工具组压缩语义，确保 function call 与对应 result 在压缩时不可拆分。
- 使用 MAF OpenTelemetry 包装器提供非敏感运行结构；默认不记录 Prompt、用户内容、工具参数或结果。
- 将 `UsageDetails` 立即归一化为 provider-neutral 的 Tinadec usage 契约，不持久化 provider/MAF 专有 usage 对象。

### TinadecCore 决策

- MAF 是技术依赖，不是产品状态权威。
- MAF session/checkpoint 作为 `RunCheckpoint` 的不透明组成，Core 仍负责 run 状态、事件水位、租约、权限、审批、工具 receipt 和恢复判定。
- MAF 包版本集中固定，升级必须经过 Agent、Workflow、Session/Checkpoint、Tool 和 Telemetry 兼容测试。
- TinadecCore 统一锁定 MAF `1.18.0` 包；后续 `1.18.x` 是否可用必须由 DmaEA 适配器兼容测试决定，上游主线只作演进参考。
- MAF 自动审批最大迭代数只是 Core 工具轮次预算的上界，不产生 grant，不代表动作已批准，也不替代 Core 的持久审批/派发流程。

### 不照搬

- 不把 MAF 示例中的内存状态或进程内回调当作生产持久性方案。
- 不让 provider/MAF 类型泄漏到公开 API，避免上游升级直接破坏产品契约。
- 不开启默认敏感遥测，也不让 MAF 自动工具循环直接执行有副作用工具。

## 4. lindexi-agent

### 证据

- `lindexi-agent/SemanticKernelSamples/AgentLib/AgentLib.ChatRoom/README.md`：角色拥有独立模型、Prompt、技能、工具、记忆，以及公开/私有历史。
- `lindexi-agent/SemanticKernelSamples/AgentLib/AgentLib.ChatRoom/Coordination/ChatRoomCoordinator.cs`：单写者命令循环、幂等回执、连续事件序号、待审批项和角色 checkpoint。
- `lindexi-agent/SemanticKernelSamples/AgentLib/AgentLib.ChatRoom/Domain/ChatRoomRoleDefinition.cs`：稳定角色身份包含 `Incarnation`，配置包含递增 `RuntimeVersion`。
- `lindexi-agent/SemanticKernelSamples/AgentLib/AgentLib.ChatRoom/Domain/ChatRoomSnapshot.cs`：checkpoint 记录运行版本、房间修订和消息消费水位。
- `lindexi-agent/SemanticKernelSamples/AgentLib/AgentLib/Tools/HumanApprovalTool.cs`：在工具执行前包装人工审批。
- `lindexi-agent/SemanticKernelSamples/AgentLib/AgentLib/ToolCallAwareChatReducer.cs`：未完成工具调用时禁止上下文压缩。
- `lindexi-agent/SemanticKernelSamples/AgentLib/AgentLib.Coding/CodingWorkspaceTransaction.cs`：工作区切换采用 prepare/apply/publish/commit/rollback。

### 吸收

- 专业角色的模型、Prompt、工具、技能和历史隔离。
- 单聚合有序写、幂等命令和 checkpoint 消费水位。
- 工具调用配对保护和工作区事务阶段。

### 不照搬

- 示例中的无界 Channel、内存 pending approval 和单进程假设不能直接进入生产设计。
- ChatRoom 只是协作表面；TinadecCore 不以所有角色持续群聊作为默认执行模型。

## 5. Traycer

### 证据

- `traycer/README.md`：持久 Agent 会话、跨模型统一上下文、A2A 和共享工作区。
- `traycer/protocol/README.md`：npm 包版本、逐 RPC 方法版本和持久化记录版本相互分离。
- `traycer/protocol/src/persistence/COMPATIBILITY.md`：持久化兼容、未知字段保留和内容寻址分片。
- `traycer/protocol/src/host/epic/communication-graph.ts`：A2A 追加事件、cursor 恢复和 snapshot 后接 event。
- `traycer/protocol/src/persistence/epic/role-claims.ts`：职责声明不授予权限。
- `traycer/docs/DEVELOPMENT.md`：客户端、CLI、Host 和协议边界。
- `traycer/protocol/src/framework/versioned-rpc.ts`：RPC 加法兼容和升级/降级路径校验。

### 吸收

- API、事件、持久化和包分别版本化。
- snapshot + cursor + event 的恢复协议。
- 角色、职责、能力与 ACL 分离。

### 不照搬

- MVP 不复制云同步、跨 Host 兼容矩阵和完整内容寻址分片体系。
- A2A 消息不能绕过 Core 的 run/task/permission 绑定。

## 6. Dify

### 证据

- `dify/dify-agent/docs/dify-agent/concepts/run-lifecycle/index.md`：Agent run 与 Workflow run 分离；HITL 结束当前 Agent run、暂停外层 Workflow，恢复时从 snapshot 新建 run。
- `dify/dify-agent/docs/dify-agent/concepts/runtime-resources/index.md`：Home Snapshot、Workspace、Execution Binding、RuntimeLease 四类资源，以及控制面/运行面分离。
- `dify/dify-agent/src/dify_agent/runtime/event_sink.py`：非终态事件追加，终态事件与状态通过 CAS 原子提交。
- `dify/dify-agent/src/dify_agent/runtime/compaction.py`：先清理旧工具结果，再做增量摘要。
- `dify/api/models/agent.py`：可编辑 draft、不可变 config snapshot 和 revision 审计边。
- `dify/api/services/workflow_event_snapshot_service.py`：重连先重放持久快照，再接实时事件。
- `dify/api/services/agent/dsl_entities.py`：可移植 Agent 包剥离凭据、密钥和文件定位符。

### 吸收

- 控制面版本和运行面租约分离。
- 终态与状态 CAS、恢复时创建清晰的新执行边界。
- 配置导出无秘密、无机器路径。

### 不照搬

- 不复制 Dify 的完整多服务平台、应用编排 UI 和云平台部署复杂度。
- TinadecCore 的双层治理不等同于 Dify Workflow 节点分类。

## 7. OpenCode

### 证据

- `opencode/packages/opencode/src/agent/agent.ts`：Agent 配置包含 mode、模型、Prompt、工具权限和步数；内置 build/plan/explore/compaction/title/summary 角色；可根据描述生成 Agent 配置。
- `opencode/packages/opencode/src/permission/index.ts`：`allow/ask/deny` 规则，Asked/Replied 事件，以及 once/always/reject 决策。
- `opencode/packages/opencode/src/agent/subagent-permissions.ts`：子 Agent 权限继承规则和默认递归限制。
- `opencode/packages/opencode/src/tool/task.ts`：子任务深度限制、独立会话、可恢复 task id 和后台执行。
- `opencode/packages/opencode/src/session/revert.ts` 与 `opencode/packages/opencode/src/snapshot/index.ts`：会话回退、代码快照恢复和 diff 协调。

### 吸收

- Agent 配置和运行预算放在同一清晰表面。
- `allow/ask/deny` 与持久权限交互事件。
- 子任务深度、独立上下文、后台运行和恢复标识。
- 生成 Agent 草稿的产品体验。

### 不照搬

- 不采用“子 Agent 主要继承父级 deny”的语义；TinadecCore 使用父级 delegation ceiling 与多级 grant 求交。
- `always` 必须有明确作用域、期限和撤销，不提供无边界永久批准。
- 会话 revert 与工作区 snapshot 不能合并为一个模糊承诺。

## 8. LangChain

### 证据

- `langchain/libs/langchain_v1/langchain/agents/factory.py`：Agent 工厂将 middleware、tool、checkpointer 和 store 组合为图。
- `langchain/libs/langchain_v1/langchain/agents/middleware/human_in_the_loop.py`：按工具配置 `approve/edit/reject/respond`；未配置工具默认自动批准。
- `langchain/libs/langchain_v1/langchain/agents/middleware/model_fallback.py`、`tool_selection.py`、`context_editing.py`、`summarization.py`、`tool_call_limit.py`：模型回退、工具筛选、上下文编辑、总结和调用限制是独立 middleware。
- `langchain/libs/core/langchain_core/runnables/config.py`：tags、metadata、callbacks、并发和递归限制向子调用继承。

### 吸收

- 将模型回退、工具筛选、上下文、总结、限额和 HITL 做成可组合但可观测的策略环节。
- 运行 tags、metadata、预算和 trace 上下文向子调用传播。

### 不照搬

- 未显式配置不应等同于高风险工具自动批准；TinadecCore 必须有风险默认值和 fail-closed 规则。
- middleware 链不能取代领域状态机、持久审批和审计对象。

## 9. LangGraph

### 证据

- `langgraph/README.md`：长运行、有状态、持久执行和 HITL 是核心定位。
- `langgraph/libs/langgraph/langgraph/types.py`：durability 分 `sync/async/exit`，流支持 values、updates、checkpoints、tasks 和 debug；`interrupt()` 与 `Command(resume=...)` 形成可恢复人工介入。
- `langgraph/libs/checkpoint/langgraph/checkpoint/base/__init__.py`：checkpoint 含 `thread_id`、`checkpoint_id`、`parent_config`，并支持中间 writes。
- `langgraph/libs/langgraph/langgraph/pregel/main.py`：状态历史、状态更新和子图命名空间。

### 吸收

- 可配置持久性等级、可恢复 interrupt/resume 和状态历史。
- 子图命名空间与 checkpoint 父链可用于 run/task 的恢复参考。

### 不照搬

- LangGraph checkpoint 不代表工作区或 Git 快照。
- 任意 state update 必须经过 TinadecCore 的领域命令、revision 和权限检查。

## 10. PlanWeave

### 证据

- `PlanWeave/packages/runtime/src/types/manifest.ts`：Task/Block DAG、implementation/review 类型、依赖、执行器、并发和反馈循环是显式 manifest。
- `PlanWeave/packages/runtime/src/types/state.ts`：状态区分 ready、in_progress、completed、needs_changes、blocked、diverged。
- `PlanWeave/packages/runtime/src/taskManager/claimReadiness.ts`：claim 同时考虑依赖、并发、冲突、反馈和 review 控制点。
- `PlanWeave/skills/plan-coordinator/SKILL.md`：review 是一级工作，`needs_changes` 回流实现；协调器不得兼任执行或审查。
- `PlanWeave/packages/agent-host-protocol/src/executionEnvelope.ts`：远程执行 envelope 内容寻址，仅携逻辑工作区和能力要求，不携 cwd/env/credential。
- `PlanWeave/packages/runtime/src/types/manifest.ts`：本地 executable review hook 需要 `trusted-local`。

### 吸收

- 任务依赖、claim receipt、并发、冲突、review gate 和反馈回路显式化。
- 协调、实现和审查责任分离。
- 未来远程执行只传逻辑资源引用和能力要求，不泄漏机器环境。

### 不照搬

- 不把软件开发专用的完整文件图和 Block 体系写死在通用 Core 中。
- 任意本地 executable hook 都必须通过 Tool 信任与审批边界。

## 11. Grok Build

### 证据

- `grok-build/crates/codegen/xai-grok-pager/docs/user-guide/16-subagents.md`：Agent 与 persona 分层；子 Agent 有独立上下文、模型、工具、I/O contract、恢复和 worktree 隔离。
- `grok-build/crates/codegen/xai-grok-pager/docs/user-guide/22-permissions-and-safety.md`：权限按 hook、deny/ask/allow、remembered grant、只读白名单和 mode 评估。
- `grok-build/crates/codegen/xai-grok-pager/docs/user-guide/05-configuration.md`：组织 requirements 可以钳制用户配置；环境 overlay 只允许软配置。
- `grok-build/crates/codegen/xai-grok-pager/docs/user-guide/09-plugins.md`：插件启用和代码信任分离；插件 Agent 不能声明 hooks/MCP 或 bypass 权限。
- `grok-build/crates/codegen/xai-grok-pager/docs/user-guide/18-sandbox.md`：sandbox 与权限是两层防护，session 固定 sandbox profile。
- `grok-build/crates/codegen/xai-grok-pager/docs/user-guide/13-memory.md`：memory 分 global/workspace/session，支持 FTS/vector、衰减和压缩后召回。
- `grok-build/crates/codegen/xai-grok-shell/src/session/goal_orchestrator.rs`：目标模式持久 phase/status/token/worker/verification 状态，并区分持久事件与高频瞬态事件。

### 吸收

- Agent 定义和 persona/Prompt 分层，子 Agent 有明确 I/O contract 和隔离环境。
- 组织硬上限、用户软配置、session 固定 sandbox profile。
- 插件启用、代码信任和运行权限三者分离。
- 高频瞬态流与必须持久的领域事件分级。

### 不照搬

- 不把“始终允许所有会话”作为突出默认选项。
- 安全审批 hook 超时或崩溃不得 fail open。
- 对话 rewind 不能被宣传为文件或外部副作用回退。

## 12. t3code

### 证据

- `t3code/docs/internals/overview.md`：Server 是 Agent session、workspace 和 VCS 的执行边界；客户端通过认证的 Effect RPC WebSocket 访问，方法再按 scope 授权。
- 同一文档说明 `OrchestrationEngine` 使用单 worker 串行命令、持久 command receipt、纯 decider，并在一个 SQL 事务中追加事件、更新内存/持久投影和写 receipt。
- `t3code/packages/contracts/src/orchestration.ts`：shell/thread snapshot 带 `snapshotSequence`，订阅可用 `afterSequence` 先补事件再跟随实时流。
- `t3code/apps/server/src/utils/subscribeBeforeSnapshot.ts`：通过先建立订阅与互斥边界避免 snapshot/live event 之间的竞态窗口。
- `t3code/docs/internals/overview.md` 与 `apps/server/src/orchestration/Layers/CheckpointReactor.ts`：每个 turn 以隐藏 Git ref 工作区 checkpoint 包围，并协调工作区与 provider conversation 回退。
- `t3code/docs/user/permission-modes.md`：权限模式在 provider 能力上做映射；Supervised、Auto-accept edits、Auto 和 Full access 的实际执行依赖下游 Agent CLI。

### 吸收

- Server 状态权威、typed RPC、逐方法 scope 和客户端共享非视觉 runtime。
- 单写者命令、持久幂等 receipt、事件与投影同事务提交。
- snapshot sequence、补偿重放和 live subscription 的无缝衔接。
- provider adapter registry 统一 Codex、Claude、Cursor、Grok 和 OpenCode 等异构运行时。

### 不照搬

- t3code 是外部 Agent harness 的控制面；TinadecCore 不能把 provider CLI 当成自己的状态、权限或审批权威。
- 不采用 Full access 作为默认权限模式；工作区或 sandbox 可丢弃也不能替代 Core 的风险策略。
- Git checkpoint 是 WorkspaceSnapshot 的一种实现，不等于 ConversationCheckpoint 或 RunCheckpoint。
- t3code 文档明确 runtime receipt 只用于测试；TinadecCore 的生产恢复不能依赖测试型内存 receipt bus。

## 13. 综合技术取舍

### 13.1 采用

- 本地优先的模块化单体，先建立正确领域边界，再替换远程端口。
- 单 writer/乐观并发保护聚合，事件 append-only，终态与状态原子提交。
- run 冻结配置和 tool manifest，配置新版本只影响新 run。
- 三类快照和明确的副作用 receipt/补偿语义。
- 低风险自动化 + 有界代理审批 + 超范围用户升级。
- 候选、评测、晋升和 canary，而非运行中直接自我修改。

### 13.2 延后

- 跨设备同步和内容寻址分片网络。
- 远程 Host 与复杂协议协商。
- 多区域调度、插件市场和组织级分发。
- 完整长期记忆衰减/向量治理和自动配置优化。

### 13.3 拒绝

- 把角色描述当权限。
- 把模型输出当审批结果。
- 子 Agent 默认获得父级未禁止的一切。
- 写操作在安全组件故障时自动放行。
- 用一个 snapshot 名词同时承诺对话、运行、文件和外部世界回退。
- 让 Gateway、App 或 Tool 成为第二个状态权威。

## 14. 分阶段落地建议

| 阶段 | 范围 |
| --- | --- |
| **MVP** | 本地单进程 MAF、DmaEA 双层拓扑、版本化 Agent/Mode/Prompt、任务 DAG、权限租约和人工审批、事件日志、三类快照契约、基础观测 |
| **第二阶段** | 有界代理审批、候选生成与评测、Agent/Prompt/Memory 晋升、插件/技能信任、长期记忆 |
| **后续** | 远程 Host、协议版本协商、多租户强化、跨设备协作、组织策略和插件市场 |

任何阶段都不应以牺牲四个不变量换取功能速度：权限不可扩大、配置可复现、事件可审计、外部副作用不重复。
