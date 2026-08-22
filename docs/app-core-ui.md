# TinadecApp Desktop 与 TinadecCore 落地规格

> 文档状态：Desktop/Core 当前实施基线
>
> 适用范围：`apps/desktop`（Electron + Vue 3 + TinadecUI）
>
> 权威来源：`docs/tinadec-core-product-definition.zh-CN.md`
>
> 维护规则：本文件描述 Desktop 需要落地的产品行为；Core 的持久状态和治理规则仍以源码、Core Contracts 与 Core OpenAPI 为准。

本文是 TinadecApp Desktop 开发者的交接文件。它把 TinadecCore 的能力转换为页面、客户端方法、状态显示、交互流程和验收用例。下一位 Agent 开始工作时，应先阅读本文件和 `apps/desktop/AGENTS.md`，再核对当前 `/api/v1` OpenAPI 与实现。不要根据历史页面猜测接口，也不要在 Desktop 复制 Core 的业务事实。

## 1. 不可变边界

### 1.1 产品调用链

```text
TinadecApp Desktop -> TinadecGateway（可选、无状态） -> TinadecCore -> Tool Provider
                                      \-> Core/Tool Provider 实时工具清单
```

- Desktop 默认调用 Gateway；部署配置可以让 Desktop 直连 Core。
- Gateway 只是北向协议、身份、限流和流转发层，不保存 run、权限、审批、租约、快照、模型配置或工具结果，不计算 PDP，不批准工具动作。
- Core 是租户、工作区、主体、会话、run、Agent Version、Mode Version、Prompt Version、Policy、权限、审批、租约、快照、恢复和审计的唯一事实源。
- TinadecTool/Tool Provider 执行工具并返回结构化结果。Desktop 不启动 TinadecTools 子进程，不解析工具风险，不拼接 Git shell 命令。
- `TinadecTools.Generators` 是 TinadecTool 的 Analyzer/Source Generator 内部实现，不是 Desktop 的运行时依赖。

### 1.2 API 版本政策

- 所有公开接口永久使用 `/api/v1`。没有 `/api/v2`、旧别名、迁移路由或弃用周期。
- 重大破坏性调整直接更新 v1、客户端类型、Gateway OpenAPI 快照和本文件。不要为了“兼容旧版本”新增第二套请求或状态。
- 正式用户工具传输面保留并继续使用：
  - `/api/v1/code/tools`
  - `/api/v1/code/tools/{toolId}/execute`
  - `/api/v1/tool-runtime/health`
  - `/api/v1/tool-runtime/manifest`
  - `/api/v1/tool-runtime/tools`
  - `/api/v1/tool-runtime/tools/{toolId}/execute`
- 只读查询可以走用户直连工具传输面；任何改变工作区、Git 引用或外部系统的动作都必须走 Core UserToolAction。
- Agent 工具调用走 `/api/v1/runs/{runId}/tools/{toolId}/execute`，不能由 Desktop 冒充 Agent 或伪造 run。

### 1.3 Desktop 不得成为事实源

Desktop 可以保存窗口布局、当前项目/会话选择、筛选条件、光标、未发送草稿和短暂 loading 状态，但不得持久化或自行推断：

- principal、tenant、workspace、risk、parameters hash、Policy、PermissionRequest、AuthorizationDecision；
- CapabilityGrant、ApprovalDelegation、CapabilityLease、ActionApproval 或任何 nonce；
- Agent/Mode/Prompt 的发布状态、版本 hash、run 冻结配置；
- snapshot 内容、Git HEAD/index/worktree 事实、工具结果、审计结论；
- “批准即成功”“断线即失败”“模型输出即授权”等业务事实。

## 2. 当前实现基线

下表中的状态是交接时的事实，不代表目标能力全部已经完成。

| 标记 | 含义 |
| --- | --- |
| `已实现` | Core/Gateway 接口和主要 Desktop 入口已经存在，下一位 Agent 负责校准 DTO、状态和测试。 |
| `部分实现` | 有接口或页面基础，但存在明确缺口，不能在 UI 中宣称完整能力。 |
| `占位` | 当前返回空集合、501 或 capability unavailable；页面只能显示未启用状态，不能伪造成功。 |
| `规划` | 产品要求已经确定，等待 Core 契约或后续阶段。 |

当前基线：

- Core：MAF 1.18.0 适配层、DmaEA 双层运行、会话/run、Agent/Mode/Prompt 正式配置、UserToolAction、Governance nonce、Tool Provider、Git/文件快照和恢复决定已存在；多实例全租户恢复、完整监督复核 API、完整演化发布流水线仍未完成。
- Gateway：无状态薄代理已覆盖主要 Core API，并保留用户工具直连传输面；OpenAPI、SSE 事件类型和部分治理 GET 仍需补齐。
- Desktop：Home、Workbench、Code、Agent Center、Settings、Market、Debug Studio、Detached Panel、Pet 和 Library 路由已存在；Git/文件写操作正在迁移到 UserToolAction，四个在途文件不得被接手 Agent 覆盖。
- Desktop 全量测试目前可能包含既有 Vue/happy-dom Transition 环境失败；治理交互测试必须单独运行并单独报告。
- Core OpenAPI 快照当前有 147 个 path、165 个 operation，但响应 schema 基本缺失：只有 UserToolAction 的 6 个 operation（9 个 response content）声明了 schema，3 个真实 SSE 也没有声明 `text/event-stream`。它目前只能用于路由发现，不能单独作为生成 Desktop 类型的完整事实源。
- Core 仍存在同方法同模板的活动重复路由：`GET /agents` 对应正式 `AgentDefinition` 与旧 `AgentProfile` 两套事实，`/agent-candidates` 的查询、promote、reject 也由两组 endpoint 重复注册。在 Core 收口前，Desktop 不应接入这些歧义入口。

## 3. Desktop 信息架构

### 3.1 页面总表

| 路由 | 页面 | Core 角色 | 当前状态 | 下一步重点 |
| --- | --- | --- | --- | --- |
| `/` | Home | meeting 入口、项目/会话、消息和队列 | 已实现 | 统一 interaction/SSE、显示等待和监督状态 |
| `/workbench` | Workbench | run、task graph、worker、supervision、context | 已实现 | 补扩展 SSE、run 状态和用户复核入口 |
| `/code-editor` | Code | 文件读写、patch、工具结果 | 部分实现 | 所有写操作统一 UserToolAction store |
| `/agent-center` | Agent Center | Agent/Mode/Prompt/Evolution/实例 | 部分实现 | 版本、ETag、双泳道和候选流水线状态 |
| `/settings` | Settings | model provider/route、工具诊断、系统健康 | 部分实现 | 明确占位端点，禁止把 readiness 当连接成功 |
| `/market` | Market | extension/MCP/ACP 市场入口 | 占位/部分实现 | 等 Core extension 和 provider 契约后再开放写操作 |
| `/debug-studio` | Debug Studio | 诊断、事件、模拟和 trace | 占位/部分实现 | 所有模拟接口按 501 显示，不写入生产事实 |
| `/panel` | Detached Panel | Git、Approval、Events、Doctor、Orchestration、Terminal | 已实现 | 与主窗口共享 Core 投影，不复制审批状态 |
| `/pet` | Desktop Pet | 纯客户端体验 | 已实现 | 不与 Core 治理耦合 |
| `/library` | TinadecUI Library | 纯组件预览 | 已实现 | 保持 UI 组件和 token 示例 |

### 3.2 页面通用布局

- 使用现有 TinadecUI/UieCanvas/UieColumn/UieShell、`components/ui` 和 CSS token；不要重新引入一套卡片、颜色或布局系统。
- 页面结构优先使用“标题/当前作用域/主内容/状态栏/详情抽屉”五段式；高频操作使用图标按钮并提供 tooltip。
- 所有异步页面都有 loading、empty、error、stale 和 retry 状态。网络断开不得清空最后一份 Core 投影。
- 状态标签使用 Core 原始 `snake_case` 值映射为本地化文案；本地化文案不能改变状态语义。
- `action.id`、`run_id`、`session_id`、`snapshot_id`、`agent_version_id` 是详情跳转的稳定身份；不要使用数组下标或 permission id 作为主键。

## 4. 页面功能清单

### 4.1 Home：会议入口

**目标**：用户只与 meeting 入口交互，Core 再把任务派发到治理层和执行层。只有 meeting 可以产生正式用户答复。

**读取**：

- `GET /api/v1/projects`
- `GET /api/v1/sessions?project_id=...`
- `GET /api/v1/sessions/{sessionId}/messages`
- `GET /api/v1/sessions/{sessionId}/runs`
- `GET /api/v1/sessions/{sessionId}/orchestration`
- `GET /api/v1/approvals?session_id=...`（仅投影）
- `GET /api/v1/events?session_id=...&after_seq=...`
- `GET /api/v1/application-modes`、`GET /api/v1/agent-modes`

**写入**：

- `POST /api/v1/projects`
- `POST /api/v1/sessions`
- `PATCH /api/v1/sessions/{sessionId}`
- `POST /api/v1/sessions/{sessionId}/interactions`
- `POST /api/v1/sessions/{sessionId}/interactions/{interactionId}/cancel`
- `POST /api/v1/sessions/{sessionId}/interactions/{interactionId}/reassign`
- `POST /api/v1/runs/{runId}/control`（pause/resume/cancel）

**交互要求**：

1. 发送消息先生成客户端幂等 id，再调用 `interactions`；不要先写一条“已成功”的 assistant message。
2. `dispatch_mode` 只能是 `queued`、`insert`、`parallel`；`insert` 必须带 `target_run_id`。
3. 返回 `queued` 且没有 `run_id` 时，只能显示本窗口临时队列项；不要创建假的 run，也不要宣称它可跨重启恢复。
4. `insert` 返回 `context_conflict` 时显示当前 revision 与用户输入，要求用户重新读取后再发。
5. run 控制只显示适用于当前状态的按钮；控制结果必须以 Core 投影为准。
6. assistant 正式答复只接受 meeting 的 `delta/done` 或持久消息；worker 输出显示为证据/进度，不能冒充正式答复。

当前 HomeController 在 `interactions` 失败时仍回退 `/sessions/{id}/invoke-stream`，甚至可能直接 `POST messages`。这违反“破坏性变更直接更新 v1、不维护旧路径”的当前政策。下一次 Home 改造应删除回退，以 `POST interactions` + `GET runs/{runId}/stream` + 持久投影作为唯一主路径；失败必须可见，不能降级成一条没有 run 的普通消息。

当前 `queued` 且没有 `run_id` 的 interaction 只是响应内瞬时对象，没有持久记录；`GET /sessions/{id}/interactions` 只能从已有 run 重建，Gateway 也尚未代理这个 list。Desktop 可以在本窗口临时显示队列项，但在 Core 增加耐久 interaction queue 之前不得宣称跨重启可恢复。

`queued` 与 `parallel` 当前最终调用同一个 run admission 流程，`parallel` 主要改变响应和 stream 标签，并不构成独立的并行调度保证。`reassign` 当前只追加 `interaction.reassigned` 事件，不会把任务重新排队，也不会注入目标 run。三项都必须标为“部分实现”。

项目和会话目前只有 list/create，session 另有 patch；尚无 project/session 单项读取、删除和完整归档 API。页面不能用列表缓存模拟资源详情或删除成功。

### 4.2 Workbench：运行态和证据

**读取**：

- `GET /api/v1/runs/{runId}/stream?after_seq=...`
- `GET /api/v1/runs/{runId}/orchestration`
- `GET /api/v1/runs/{runId}/agent-lineage`
- `GET /api/v1/sessions/{sessionId}/task-nodes`
- `GET /api/v1/sessions/{sessionId}/tool-executions`
- `GET /api/v1/sessions/{sessionId}/context-versions?run_id=...`
- `GET /api/v1/sessions/{sessionId}/context-packs`（恒空占位）
- `GET /api/v1/sessions/{sessionId}/supervision-findings`（恒空占位）

**必须展示**：

- run 状态、run id、mode/version/hash、context revision、tool manifest hash；
- operation 治理层与 execution 执行层的双泳道；
- task planner、worker、父子 lineage、generation depth、allowed tools；
- task 节点状态、依赖、风险、成功标准和 step result；
- supervision 的 pass/revise/escalate、原因、revision round；
- context version、base revision、冲突和压缩事件；
- tool execution 的 prepare/authorized/approval/running/completed/blocked/outcome_unknown。

**不可做**：

- 不把“模型返回了 JSON”显示成授权或监督通过。
- 不在 Desktop 生成或修改 run frozen configuration。
- 不把 `completed_with_escalation` 当成合法终态；监督 escalate 必须停在 `awaiting_user`，等待用户继续、修正或取消。
- 不把 orchestration 当作完整独立资源。当前 session/run orchestration、task nodes 和 session tool executions 主要由事件回放拼接；`graph` 固定为 `null`、`assignments` 固定为空，session tool executions 也不是持久 `ToolExecution` 表的直接查询。UI 必须标注“事件投影”，缺字段时显示不可用而不是补假数据。

### 4.3 Code：代码和文件工作区

**只读路径**：

- 文件树、文件内容、glob、grep、Git status/diff/log/branch/worktree 查询走 `/api/v1/code/tools/*` 或 `/api/v1/tool-runtime/*`。
- 工具 schema、风险、变更性、确认字段从实时 Core/Tool Provider manifest 读取。

**写路径**：

- `write_file`、patch apply、文件创建/删除、命令执行全部调用 `POST /api/v1/user/tool-actions`。
- action 创建只提交 `project_id`、`tool_id`、`params`、`idempotency_key`；不要提交主体、risk、hash、approval 或 snapshot 事实。
- CodeEditor、PatchPreview、FileTree、shell 和 Home command runner 共用同一个 UserToolAction store。

**UI 要求**：

- 未授权或快照等待显示在当前文件/patch 旁，不跳转到空白页面。
- `snapshot_required`、`awaiting_user`、`awaiting_approval` 和 `running` 显示当前 action 的稳定 id 和可恢复提示。
- `blocked` 显示 reason code；`outcome_unknown` 进入只读恢复页面，不显示“再次执行”。
- 同一 action 的 permission 和 action approval 可串联展示，但不能合并为一个本地 approval 对象。

### 4.4 Git Changes / CommitPanel

**只读查询**：`git_status`、`git_diff`、`git_log`、branch、worktree、conflict preview 使用用户直连工具传输面。

**必须使用 UserToolAction 的写操作**：

- `git_stage`、`git_unstage`
- `git_commit`、`git_push`、`git_fetch`、`git_pull`
- `git_checkout`、branch create/delete/rename
- worktree create/remove
- `git_merge`
- `git_rebase` 的 `start`、`continue`、`skip`、`abort`
- `git_conflict_resolve`

**规则**：

- 每个语义动作创建一个新 action；rebase 四个子命令不能通过 resume 改造同一个 start action。
- 所有 Git mutation 参数都包含规范化 `repository_path`。
- `confirm_commit`、`confirm_push` 等确认值必须是 manifest schema 声明的非空字符串，不能发送布尔值。
- `git_push` 持续显示 `non_reversible=true` 和 Core 提供的补偿建议；本地快照不能被描述为远程 push 的回滚。
- CommitPanel 和 `useGitOperation` 不得调用 `createApproval`、`createShellApproval` 或直接调用工具写路由。

Git mutation 的当前参数基线如下；实际字段和确认语义仍以本次 run 冻结的 manifest 为准：

| 工具 | 关键参数 | 确认字段 |
| --- | --- | --- |
| `git_stage` / `git_unstage` | `repository_path`, `paths`, 可选 `patch` | 无显式确认字段，但仍需 Core 治理 |
| `git_commit` | `repository_path`, `message`, `include_all`, `commit_staged_only`, 可选 `paths` | `confirm_commit` |
| `git_fetch` / `git_pull` / `git_push` | `repository_path`, `remote`, `branch`, `set_upstream`, `prune` | 对应 `confirm_fetch` / `confirm_pull` / `confirm_push` |
| `git_checkout` / branch create/delete/rename | `repository_path`, `branch`, 可选 `new_name`, `force` | 对应 `confirm_checkout` / `confirm_branch_create` / `confirm_branch_delete` / `confirm_branch_rename` |
| `git_worktree_create` / `git_worktree_remove` | `repository_path`, `path`, 可选 `branch`, `start_ref`, `force` | `confirm_worktree_create` / `confirm_worktree_remove` |
| `git_merge` / `git_rebase` | `repository_path`, `operation`, `branch`, 可选 `strategy` | `confirm_merge` / `confirm_rebase` |
| `git_conflict_resolve` | `repository_path`, `path`, `strategy` | `confirm_resolve` |

`git_rebase` 的 `operation` 由当前 Tool Provider 定义；Desktop 应把 start/continue/skip/abort 当作四个不同 action，不要擅自添加未在 schema 中声明的顶层 `action` 字段。所有路径、branch、remote、message 和确认值都进入 Core 参数规范化与 ActionApproval 绑定。

### 4.5 Approval / Governance：审批与权限

审批面板必须把三个状态机分开：

| 状态机 | 事实 | 读取 | 决定 |
| --- | --- | --- | --- |
| Permission | 能力是否可以授予 | `/api/v1/governance/permission-requests` | `/api/v1/governance/permission-requests/{id}/decision` |
| Action approval | 某一次参数化动作是否可以消费 | `/api/v1/approvals`、`/api/v1/approvals/{id}` | `/api/v1/approvals/{id}/decision` |
| Supervision | 质量是否通过、修改或升级 | run orchestration/events/findings | 当前 Core 通过 interaction/context correction 或 run control 暂停；完整 user-review API 待补 |

PermissionRequest 权威状态：`pending`、`evaluating`、`awaiting_delegate`、`awaiting_user`、`granted`、`denied`、`expired`、`cancelled`。

审批卡片至少显示：主体（由 Core 返回）、tenant/workspace/project、agent/run/task（适用时）、capability/action/resource、risk、scope、TTL、max uses、decision source、reason code、action id 和下一步。

`/api/v1/approvals` 当前可能返回 ActionApproval 和 PermissionRequest 的异构投影。按 `kind` 分流；不要把 `status=pending` 当作两道门已经合并。

### 4.6 Workspace Snapshot：快照和恢复

**接口**：

```text
POST /api/v1/projects/{projectId}/snapshots
GET  /api/v1/projects/{projectId}/snapshots
GET  /api/v1/workspace-snapshots/{snapshotId}
POST /api/v1/workspace-snapshots/{snapshotId}/restore
POST /api/v1/user/tool-actions/{actionId}/snapshot-override
```

**展示**：snapshot id、kind、is_git、workspace hash、content hash、file count、base snapshot、created_at、冲突列表和 restore 结果。

**当前边界**：公开 DTO 目前只返回快照概要，不返回 Git HEAD、branch、index/tree、worktree、binary patch 和 conflict path 明细。Desktop 不读取 `content_reference` 绕过 Core；要展示这些字段，先补 Core 的安全 DTO。

**恢复流程**：

1. 用户先查看 snapshot 详情和当前工作区状态。
2. restore 请求带幂等 key 和可选 expected workspace hash。
3. 409 显示服务端 conflicts，不自动覆盖。
4. restore 结果按 `status`、`applied_file_count`、`workspace_hash` 展示。
5. 当前 restore 尚未自动进入 UserToolAction 权限/动作审批闭环，UI 必须把它标为显式用户恢复命令；产品若要求治理恢复，先补 Core 状态机。

### 4.7 Agent Center：配置、拓扑和演化

#### 配置对象

- `AgentDefinition/Version`：角色、layer、能力、model strategy、tool scope、发布版本和 content hash。
- `AgentMode/Version`：operation/execution 双泳道、nodes、edges、layout、默认拓扑。
- `PromptPipeline/Version`：模板、变量、condition、stage、context trim、assemble 图；发布前必须有 template 和 assemble，不能有环。
- `WorkspaceDefaults`：默认 agent/mode/prompt 的引用，发布前引用必须指向已发布版本。
- `AgentRuntimeInstance`：run 内实例；展示 parent、generation depth、AgentVersion 和 frozen hash，不允许修改已运行实例。

#### 默认 Git 拓扑

当前 TOML 与 DevSeed 已同时提供以下两个正式角色，Desktop 的 Agent Center 必须按职责拆开展示，不能因为它们都与 Git 有关就合并成一个“Git Agent”：

- 治理层 `git_steward`：审阅 diff、组织提交计划、提出审批建议；默认工具集合为空，不直接 stage、commit、push 或改写历史。
- 执行层 `worker.git`：持有 Git 专业提示词和 Git 工具范围，负责执行 Core 已授权的 `status/diff/stage/unstage/commit/push/fetch/pull/branch/checkout/worktree/merge/rebase/conflict` 动作。

Agent Center 应显示每个角色的 layer、已发布 AgentVersion、内容 hash、model strategy、prompt profile、声明工具、Mode 节点工具范围和当前 Tool Provider manifest 三者的有效交集。`git_steward` 没有直接工具是设计结果，不是配置错误；`worker.git` 的某个工具不在实时 manifest 时应显示“当前 provider 不可用”，不能由 Desktop 补回。用户在 Git 面板发起的写操作仍是当前用户的 UserToolAction，不冒充 `worker.git`，也不创建伪造 run。

#### API

```text
GET/POST /api/v1/agents
GET      /api/v1/agents/{id}
PUT      /api/v1/agents/{id}/draft
POST     /api/v1/agents/{id}/publish
POST     /api/v1/agents/{id}/archive
GET      /api/v1/agents/{id}/versions

GET/POST /api/v1/agent-modes
GET      /api/v1/agent-modes/{id}
PUT      /api/v1/agent-modes/{id}/draft
POST     /api/v1/agent-modes/{id}/publish
POST     /api/v1/agent-modes/{id}/archive
GET      /api/v1/agent-modes/{id}/versions

GET/POST /api/v1/prompt-pipelines
GET      /api/v1/prompt-pipelines/{id}
PUT      /api/v1/prompt-pipelines/{id}/draft
POST     /api/v1/prompt-pipelines/{id}/publish
POST     /api/v1/prompt-pipelines/{id}/archive
GET      /api/v1/prompt-pipelines/{id}/versions

GET      /api/v1/workspace-defaults
PUT      /api/v1/workspace-defaults/draft
POST     /api/v1/workspace-defaults/publish
POST     /api/v1/workspace-defaults/archive
```

**UI 规则**：

- 目标行为是所有草稿更新和发布请求发送 `If-Match`，412 显示版本冲突并重新读取；当前只有 `GET /workspace-defaults` 稳定返回 ETag，Agent/Mode/Pipeline GET 不返回 ETag，且写入端 `If-Match` 可以省略。因此 Desktop 接手时应先把响应 `revision` 当作显示信息，不能假定已有强制 ETag 并发保护；强制契约要等 Core 收口后再打开编辑发布。
- 展示 `operation`/`execution`，不要在新 UI 使用 `planning` 作为层名称。
- 发布后版本不可变；已有 run 继续使用 frozen Agent/Mode/Prompt，不能因热更新漂移。
- Mode canvas 允许拖拽，但发布前由 Core 校验层、节点引用、工具交集、Prompt 图和 meeting 唯一性。
- Core 只内置最小治理/通用角色；专业 Agent Pack 以后由签名本地导入，不在 Desktop 里直接写入 profile。
- Desktop 不再调用旧的 flat `PUT /agents/{id}` 或 `PUT /agents/{id}/mode` 保存配置；正式写路径只使用 draft/publish/archive 和不可变 version 契约。

#### 演化候选

```text
GET  /api/v1/agent-evolution/proposals
POST /api/v1/agent-evolution/generate
POST /api/v1/agent-evolution/proposals/{id}/reject
POST /api/v1/agent-evolution/proposals/{id}/promote
```

当前 promote 必须 fail-closed，返回 `candidate_pipeline_required`。Desktop 只能展示候选，后续阶段要展示脱敏、评测、审核、发布、canary、激活和自动撤销的每一步；不能把一次 promote 请求当作发布成功。

### 4.8 Settings：模型、工具和健康

**模型中心**：

- provider 模板：`GET /api/v1/model-provider-templates`
- provider CRUD：`GET/POST/PUT/DELETE /api/v1/model-providers...`
- provider model refresh：`POST /api/v1/model-providers/{id}/models/refresh`
- route：`GET /api/v1/model-routes`、`PUT /api/v1/model-routes/{purpose}`
- CLI：`GET /api/v1/model-providers/cli/discover`、`POST /api/v1/model-providers/cli/connect`
- readiness：`GET /api/v1/model-readiness`、`GET /api/v1/model-catalog-readiness`

连接、保存和启用是不同状态。CLI discover 的 `found` 不等于 provider 已配置，provider enabled 不等于 route 已绑定，route ready 才能让 run 取得模型。API key 只提交到 Core/SecretStore，不显示明文。

当前 `GET /model-readiness`、`GET /model-catalog-readiness` 仍是 skeleton warning 投影，不会读取真实 provider/route；Settings 只能把它们标为占位诊断。真实配置列表来自 provider/route API，不能用 readiness 的零计数覆盖真实数据。

Desktop 仍调用已删除的 `/model-center/overview`、`/agent-center/overview` 和 `/agents/{id}/runtime-binding`；Gateway 对这些入口明确返回 404。下一次 Settings 改造应删除这些 client 和 UI 分支，直接组合 provider/route、正式 Agent/Mode/Prompt 和 runtime instance API。Gateway 中残留的 `/model-center/provider-instances/{id}/models/refresh` 也属于待删除别名，canonical 路径是 `/model-providers/{id}/models/refresh`。

**工具中心**：

- `GET /api/v1/tools`、`GET /api/v1/tools/search`
- `GET /api/v1/tool-layer-readiness`
- `GET /api/v1/tool-runtime/manifest`

工具目录只展示 Core/Tool Provider 返回的 descriptor、schema、risk、mutates、approval metadata、manifest hash 和 provider；不在 Desktop 维护第二份工具规格。

**健康诊断**：

- `GET /api/v1/health`
- `GET /api/v1/readiness`
- `GET /api/v1/doctor`

诊断页面按 component、status、message、last checked 展示。`501`/`capability_unavailable` 是未启用能力，不是空成功。

PromptPipeline 是 DmaEA 正式提示词配置。旧 `prompt-fragments` 页面只保留为实验性内容库，其中 clone/version/rollback/effectiveness signal/compare/context preview 多数仍是空投影或 501；不要把它与 PromptPipeline version 混成同一个发布事实，也不要继续基于占位响应扩展正式配置 UI。

### 4.9 Market、MCP、ACP 和 Debug Studio

这些页面必须按 Core 实际状态显示：

- Market extension source/catalog/install 当前有占位端点，501 显示“未启用”，不创建本地安装记录。
- MCP server/tool/reload 当前是占位；不在 Gateway 或 Desktop 自行连接 MCP。
- ACP `permission.request` 本阶段继续 fail-closed；不能把 ACP 请求当成已授权。
- Debug traces/spans/metrics/processes/simulate/breakpoints 当前多为占位；只允许测试环境使用，生产页面不得伪造 trace 或 tool result。

## 5. API 与客户端契约

### 5.1 UserToolAction

```text
GET  /api/v1/user/tool-actions?status=...
POST /api/v1/user/tool-actions
GET  /api/v1/user/tool-actions/{id}
POST /api/v1/user/tool-actions/{id}/resume
POST /api/v1/user/tool-actions/{id}/snapshot-override
POST /api/v1/user/tool-actions/{id}/recovery-decision
```

创建请求只提交：

```json
{
  "project_id": "...",
  "tool_id": "git_commit",
  "params": {},
  "idempotency_key": "..."
}
```

响应字段的 Desktop DTO 必须至少包含：`id`、`audit_reference`、`project_id`、`tool_id`、`status`、`risk`、`mutates_workspace`、`requires_approval`、`permission_request_id`、`authorization_decision_id`、`action_approval_id`、`snapshot_id`、`snapshot_hash`、`snapshot_override`、`snapshot_override_reason`、`non_reversible`、`compensation_guidance`、`recovery_decision`、`recovery_reason`、`recovered_at`、`result`、`error_category`、`message`、`created_at`、`updated_at`、`completed_at`。

禁止出现在 DTO、事件、日志、OpenAPI 或 Gateway 响应中的字段：nonce、nonce hash、protected nonce material、lease secret、ActionApproval secret、内部 secret reference。

Desktop client 最少实现：

- `createUserToolAction`
- `listUserToolActions`
- `getUserToolAction`
- `resumeUserToolAction`
- `overrideUserToolActionSnapshot`
- `decideUserToolActionRecovery`
- `listPermissionRequests`、`getPermissionRequest`、`decidePermissionRequest`
- `listApprovals`、`getApproval`、`decideApproval`

Action approval decision 的响应可能是普通 approval 投影，也可能是 `{ id, user_tool_action_id, status, action }` 包装对象。不要把它强制解析为单一 `ApprovalDto`；决定完成后立即按原 `action.id` GET。

当前 Desktop 契约漂移，P0 必须逐项处理：

- `apps/desktop/src/api.ts` 的 `UserToolActionDto` 尚缺 `audit_reference`、snapshot override、不可逆和 recovery 字段，也缺 recovery decision client。
- Desktop `ApprovalDto` 仍含 Core 当前投影没有的 `command`、`cwd`、`governance_status`，并缺 project/run/task/agent/execution/tool/risk/request hash/decision reason/consume/expiry 等字段；应拆成公开 approval DTO 和 UI view model。
- `decideApproval` 需要支持可选 reason，且调用方不能从决定响应推断工具已经完成。
- snapshot client 尚缺手工 create、明确的 restore result DTO，以及 `content_reference`、`content_length`、`base_snapshot_id` 等公开概要字段。
- `GET /api/v1/user/tool-actions` 当前只有 status 过滤，最多返回最近 200 条，没有 project/cursor/pagination；UI 可以按响应内 project 过滤当前视图，但不能把它叫作完整审计历史。
- `createUserToolActionForPath` 通过去尾分隔符和全量小写反查项目，在大小写敏感文件系统、符号链接和非规范路径下不可靠。长期接口必须持有 Core `project_id`，path helper 只作为过渡适配器。

### 5.2 Governance DTO

`PermissionRequest` 和 `ActionApproval` 是两个不同 DTO。Desktop 不得继续使用旧的 `CreateCapabilityGrantInput`（包含 `evidence/data/requires_approval` 的旧形状）直接写 Core。当前 Core 的治理写入口为：

```text
POST /api/v1/governance/policy-bundles
POST /api/v1/governance/policy-bundles/{id}/versions
POST /api/v1/governance/policy-bundles/{id}/archive
POST /api/v1/governance/capability-grants
POST /api/v1/governance/capability-grants/{id}/revoke
POST /api/v1/governance/approval-delegations
POST /api/v1/governance/approval-delegations/{id}/revoke
POST /api/v1/governance/capability-leases/{id}/revoke
```

当前缺少 PolicyBundle、CapabilityGrant、ApprovalDelegation、CapabilityLease 的 list/detail GET。Agent Center 在 Core 补齐之前不得显示“完整治理资源管理表”，只能显示创建/撤销结果或明确未提供历史读取。

### 5.3 SSE 与事件流

Core 当前有两种不能混读的 SSE 协议：

**Run stream：** `POST /api/v1/sessions/{sessionId}/invoke-stream` 和 `GET /api/v1/runs/{runId}/stream` 实际只写 `id` 与 `data`，没有 SSE `event` 行，也没有 heartbeat。`data` 是匿名 JSON 投影：

```json
{
  "run_id": "...",
  "session_id": "...",
  "turn_id": "...",
  "message_id": "...",
  "seq": 1,
  "purpose": "dual_layer",
  "kind": "ack|delta|done|error|queued|assigned|steering|context_conflict|model_selection|ephemeral_agent|control",
  "delta": null,
  "usage": null,
  "finish_reason": null,
  "error_category": null,
  "safe_error_message": null
}
```

`session_id` 只在 `invoke-stream` 响应中出现；run stream 依赖 URL 中的 run id。当前 run stream 已见基础 kind 为 `ack`、`delta`、`done`、`error`，以及 `queued`、`assigned`、`steering`、`context_conflict`、`model_selection`、`ephemeral_agent`、`control` 扩展 kind。未知 kind 必须原样保留，不能静默丢弃。

**Event feed：** `GET /api/v1/events` 才写 `event: {event_type}`，data 为 `EventEnvelope`（含 `version`、`event_id`、`event_type`、`timestamp`、session/run 标识和 payload），初始回放及后续 follow 每 15 秒写 `event: heartbeat`。它和 run stream 的 `kind` 不是同一字段，Desktop 必须使用两套 parser/adapter。

当前 OpenAPI 快照没有为上述三个 SSE 成功响应声明 `text/event-stream`，Gateway 外部 OpenAPI、SSE mapper 和 Desktop `generated/client.ts` 仍需补齐；在补齐前不能从 OpenAPI 自动推断消息 schema。

Desktop `useRunStream` 要求：

- run stream 以 `run_id + seq` 去重；event feed 以 `event_id`（必要时结合 run/sequence）去重。event-feed heartbeat 只更新连接健康，不追加 assistant 文本；run stream 没有 heartbeat。
- 重连发送 `Last-Event-ID` 或 `after_seq`，先 replay 再 follow。
- SSE 断开不取消 run/action；恢复后 GET Core 投影校正本地 store。
- `delta` 只追加 meeting 正式文本；其它事件更新 task/worker/supervision/context/action store。
- stream 终止后仍需 GET run/orchestration/action，不能仅凭最后一个 SSE 事件定终态。

当前 Desktop `generated/client.ts` 的 `RunStatus` 还缺 `awaiting_delegate`、`awaiting_user`；SSE 类型还缺 Core 扩展事件。先更新 Core/Gateway OpenAPI 和生成客户端，再让 Workbench/Home 使用这些状态。不要仅在单个 Vue 组件中追加字符串特判。

当前 Core OpenAPI 对多数成功响应只声明状态码，没有稳定 response schema；三个 SSE 入口也没有完整声明 `text/event-stream`。在补齐 schema、event union 和媒体类型以前，不要仅凭现有快照重新生成并覆盖手写 DTO。生成顺序必须是 Core 契约与 OpenAPI -> Gateway 映射与外部 OpenAPI -> Desktop client。

### 5.4 直连工具参数

直连工具请求的顶层 `approval`、`confirmation`、`source` 不是授权事实。只读工具可以提交 Tool Provider schema 要求的查询参数；需要确认的 mutation 参数必须通过 UserToolAction，由 Core 按冻结 manifest 校验。Desktop 不生成参数 hash，不发送风险或批准字段。

## 6. 状态机与用户流程

### 6.1 Run

```text
planning -> understanding -> executing -> reviewing -> completed
                         \-> replanning -> executing
                         \-> awaiting_approval -> executing
                         \-> awaiting_delegate -> executing
                         \-> awaiting_user -> executing
                         \-> paused -> executing
任意非终态 -> failed / cancelled
```

真实 Core run 状态：`planning`、`understanding`、`executing`、`replanning`、`awaiting_approval`、`awaiting_delegate`、`awaiting_user`、`paused`、`reviewing`、`completed`、`failed`、`cancelled`。

### 6.2 UserToolAction

```text
requested -> snapshot_required -> awaiting_delegate -> awaiting_user
                                      \-> awaiting_approval -> running -> completed
任意阶段 -> blocked
running -> outcome_unknown
outcome_unknown -> completed  (mark_completed)
outcome_unknown -> failed     (mark_failed)
```

- `snapshot_required` 只表示高风险写前快照尚未成为可信事实，不显示审批按钮。
- 当前同步创建路径若快照失败可能直接得到 `blocked + error_category=snapshot_failed`；只有该组合才可展示当前用户一次性 snapshot override。
- `authorized` 是 Core 内部过渡态，Desktop 不将其作为产品状态；遇到未知非终态只显示“正在推进”并重新 GET。
- `outcome_unknown` 禁止自动重放。用户必须先检查工作区、快照和外部效果，再提交 recovery decision；该决定不会重放 Tool Provider。
- `failed` 是终态；人工 `mark_failed` 不等于工具返回了结构化失败结果。

### 6.3 Permission 与 ActionApproval 两阶段

```text
create action
  -> PermissionRequest awaiting_user/delegate
  -> permission decision
  -> GET action.id
  -> ActionApproval awaiting_approval
  -> approval decision
  -> GET action.id / POST resume
  -> running / completed / blocked / outcome_unknown
```

用户界面可以串成一张时间线，但必须保留两个 id、两个状态和两个决定原因。审批代理只能在用户预授予的委托包络内行动；超出范围升级用户，超过硬权限上限直接拒绝。

### 6.4 监督复核

监督结果：`pass`、`revise`、`escalate`。

- pass：进入 meeting finalization。
- revise：run 进入 replanning，显示监督原因和修订轮次。
- escalate：run 必须保持 `awaiting_user`，显示原因和证据；用户只能选择继续/修正/取消。不得显示 `completed_with_escalation`。
- 当前 Core 尚未提供完整独立 review decision endpoint，Desktop 不能伪造该 API。暂时通过 interaction/context correction 和 run control 表达用户决定，并在页面上标注“当前实现”。

## 7. 标准用户流程

### 7.1 发起任务

1. 选择 project/session，读取 mode defaults。
2. 生成 `client_message_id`，提交 interaction。
3. 保存 response 的 `interaction_id/run_id/turn_id`，开始 SSE replay/follow。
4. 处理 `queued/assigned/model_selection`，再显示执行层进度。
5. 完成后重新读取 messages、run、orchestration 和 supervision。

### 7.2 用户 Git commit

1. 直连读取 status/diff，用户编辑 commit message。
2. 读取实时 manifest，校验 `confirm_commit` schema。
3. 通过当前 `project_id` 创建 `git_commit` UserToolAction。
4. `snapshot_required`/`awaiting_user`/`awaiting_approval` 按 action id 刷新。
5. 权限决定后重新 GET action；若产生 ActionApproval，显示第二道门。
6. 审批决定后调用 `resume`，只允许 Core 调用 Tool Provider。
7. `completed` 显示 result/audit；`blocked` 显示 reason；`outcome_unknown` 进入恢复页面。

### 7.3 断线、重启和恢复

1. 保留本地 action/run id 和最后 cursor，但不把它们标为失败。
2. 重新连 Gateway，GET action/run/orchestration，恢复 SSE。
3. 对等待态可以继续查询和决定；对 running/outcome_unknown 不自动 resume。
4. `outcome_unknown` 用户查看 status/diff/snapshot 后只能 mark_completed 或 mark_failed；再次执行必须新建 action、新快照、新授权。

### 7.4 Agent Mode 发布

1. 读取 agent/mode/prompt 草稿和 ETag。
2. 在双泳道画布编辑节点、边、tool scope、model strategy 和 prompt graph。
3. PUT draft 携带 `If-Match`，412 时刷新并提示冲突。
4. 调用 publish；Core 执行 meeting 唯一性、层、工具交集、Prompt 图、引用版本和模型策略校验。
5. 展示 immutable version/content hash；新的 run 才能选择新版本，已有 run 不漂移。

### 7.5 演化候选

1. evolution agent 生成 proposal。
2. Desktop 显示来源 run/instance、候选 layer/role、置信度、建议工具和审查状态。
3. 依次展示脱敏、评测、人工审核、发布、canary、激活；任一步失败显示原因。
4. 在完整流水线实现前，promote 只显示 `candidate_pipeline_required`，不得把冲突响应渲染为发布成功。

## 8. 错误、断线和空态

| HTTP/代码 | Desktop 行为 |
| --- | --- |
| 400 / `invalid_request` | 标记具体字段，保留用户输入，不生成新 action。 |
| 401/403 | 显示身份、租户或权限范围错误；不自动扩大权限。 |
| 404 | 显示资源不存在并刷新当前列表；不回退到旧路由。 |
| 409 / `conflict`、`context_conflict`、`workspace_conflict` | 显示两侧 hash/revision/conflicts，要求重新读取；不要静默覆盖。 |
| 412 | ETag/版本过期，刷新 draft 后让用户合并。 |
| 422 | 显示 Core schema 校验错误，不在客户端猜字段。 |
| 429 | 按 Retry-After 查询同一 id/action，不重复创建。 |
| 501 / `capability_unavailable` / `NOT_IMPLEMENTED` | 显示“当前部署未启用”，不要显示空成功。 |
| 502/503/网络错误 | 保留最后 Core 投影，显示连接恢复中；不把断线当作工具失败。 |
| `snapshot_failed` | 阻断 provider；仅当前用户可提交一次性 override，并显示不可逆审计标记。 |
| `outcome_unknown` | 只进入恢复决定页面，禁止自动重放。 |

## 9. Desktop 工程落地清单

### P0：先让治理闭环可用

- [ ] 统一 `UserToolActionDto`，补齐审计、snapshot override、不可逆和 recovery 字段。
- [ ] 新增 recovery decision client；按 `action.id` 建立 Pinia/store 单一身份。
- [ ] 统一 create/list/detail/poll/permission-decision/approval-decision/resume 流程。
- [ ] 修复 PermissionRequest -> ActionApproval 两阶段刷新；处理 approval 包装响应。
- [ ] CommitPanel、`useGitOperation`、CodeEditor、PatchPreview、FileTree、shell 全部移除生产 `createApproval` 写路径。
- [ ] 所有 Git mutation 使用当前 `project_id`、`repository_path`、manifest 确认字段和稳定幂等 key。
- [ ] rebase start/continue/skip/abort 分成独立 action。
- [ ] 实现 snapshot_required、awaiting_user、awaiting_approval、running、completed、blocked、outcome_unknown、failed 的状态组件。
- [ ] 实现 recovery 只读检查页和 snapshot override 对话框。
- [ ] SSE 扩展事件不丢失，Gateway/OpenAPI/生成客户端类型同步。

### P1：补齐 Core 功能可见性

- [ ] Home interaction queue、insert steering、parallel assignment 和 model selection 展示。
- [ ] Workbench 双泳道、lineage、task evidence、context revisions、supervision review 展示。
- [ ] Agent Center Agent/Mode/Prompt 的 draft/ETag/publish/archive/version timeline。
- [ ] Agent Center tool intersection、model strategy（inherit/fixed/parent_select）和 workspace defaults。
- [ ] Tool Catalog 展示实时 manifest hash、provider、schema、risk、approval metadata。
- [ ] Workspace Snapshot list/detail/restore；只展示 Core 公开字段。
- [ ] Settings model provider、CLI connect、route binding、readiness 和 secret 状态。
- [ ] Approval 页面以 PermissionRequest、ActionApproval、Supervision 三栏分离展示。
- [ ] Detached Panel 与主窗口共享 Core 投影，项目切换时停止旧轮询。

### P2：在 Core 契约完成后开放

- [ ] PolicyBundle/CapabilityGrant/ApprovalDelegation/Lease list/detail 管理。
- [ ] 独立 supervision user-review decision API（continue/correct/cancel）。
- [ ] Git snapshot detail DTO（HEAD/branch/index/tree/worktree/patch/conflict paths）。
- [ ] Workspace restore 纳入 UserToolAction 治理闭环（如果产品策略要求）。
- [ ] Agent Pack 信任库、签名导入、脱敏/评测/canary/激活/撤销流水线。
- [ ] PostgreSQL、多实例恢复扫描、远程 Tool Provider 和 OIDC 的 Desktop 诊断。
- [ ] ACP `permission.request` 接入同一治理状态机；在此之前保持 fail-closed。

## 10. Core/Gateway 需要先补的契约

Desktop Agent 遇到以下缺口时应停止猜测接口，先补 Core/Gateway 和 OpenAPI：

1. 治理资源缺少 list/detail GET，无法构建完整权限管理表。
2. Gateway/OpenAPI/SSE mapper 与 Core 扩展事件类型不一致。
3. Workspace Snapshot 未公开安全 Git 明细，不能让 Desktop 直接读内容引用。
4. Workspace restore 是否要经过 UserToolAction 尚未定案。
5. supervision escalate 缺独立用户决定 endpoint。
6. UserToolAction 列表缺 project/cursor/pagination，不能宣称完整审计历史。
7. Desktop path helper 通过大小写和去尾分隔符反查 project，不适合作为长期跨平台契约，应改为显式 project id。
8. Desktop 旧治理输入 DTO 与 Core 当前 subject/claim/scope/expiry/uses/parent grant 契约不一致，必须先替换。
9. 无 run 的 queued interaction 尚未持久化，且 Gateway 缺 `GET /sessions/{id}/interactions` 代理；需要耐久队列后才能提供跨重启编辑、改派和取消。
10. Gateway 当前代理 `/sessions/{sessionId}/interactions/{interactionId}/stream`，但 Core 没有该 endpoint。获得 `run_id` 后应以 `/runs/{runId}/stream` 为唯一流；删除或实现悬空代理时直接更新 v1，不保留别名。
11. Core 同时还映射了旧 flat Agent control-plane 写入口；正式配置服务完成后应删除冲突入口，并同步 Gateway/Desktop，只保留版本化 draft/publish/archive 契约。
12. Desktop Settings 仍依赖已经 404 的 model-center/agent-center overview 和 runtime-binding；应迁移到正式 provider/route/configuration API，并删除 Gateway 的 model-center refresh 别名。
13. model readiness/catalog readiness 与 prompt fragment 高级操作仍为 skeleton/501；需要真实 Core 服务后再开放，不在 Desktop 根据空数据生成“健康”或“发布成功”。
14. Core 当前同时注册了两组 `GET /agents`，请求可能发生 ambiguous match；`agent-candidates` 也存在重复/重叠模板。先收口为版本化配置与候选流水线的唯一 v1 路由，再接 Agent Center，不能在 Desktop 用重试或另一条别名掩盖服务端冲突。
15. Core 的 run stream、legacy invoke stream 和全局 events 实际返回 SSE，但 OpenAPI 未完整声明 `text/event-stream` 及事件 schema；修复事实源后再生成 Gateway/Desktop 类型。

## 11. 测试与验收矩阵

### 11.1 Desktop 必测

- [ ] typecheck 通过。
- [ ] UserToolAction focused tests 覆盖 create、permission pending、action approval pending、resume、blocked、snapshot override、outcome_unknown、recovery decision。
- [ ] Permission 批准后同一个 `action.id` 产生新的 `action_approval_id`，旧 permission 卡不残留为 pending。
- [ ] Git commit/push 参数包含 `repository_path` 和 manifest 声明的字符串确认字段；不含客户端 risk/hash/approved/额外 action 字段。
- [ ] Git rebase 四个子命令生成不同 action/idempotency key。
- [ ] 快照失败不调用 provider；override 只允许当前用户且保留 `non_reversible`。
- [ ] 拒绝、撤销、过期、参数篡改、manifest 漂移、重复消费、并发点击和跨项目隔离没有外部副作用。
- [ ] 断线、Gateway 503、Desktop 重启按 id/cursor 恢复，不重复创建、批准或执行。
- [ ] `outcome_unknown` 不自动重放，人工 mark_completed 不伪造工具 result。
- [ ] SSE replay/follow 按 `run_id + seq` 去重，heartbeat 不污染消息文本，扩展事件进入正确 store。

### 11.2 Core/Gateway 联合验收

- [ ] `dotnet build TinadecCore/TinadecCore.slnx --no-restore`
- [ ] `dotnet test TinadecCore/TinadecCore.slnx --no-build`
- [ ] TinadecTools manifest/schema/risk/approval metadata 契约测试。
- [ ] Gateway 目录来自 Core/Tool Provider，不持有状态，不自行授权，工具直连路径继续透传。
- [ ] SQLite 与 PostgreSQL 运行同一治理契约测试；本地和远程 Tool Provider 运行同一 provider 契约测试。
- [ ] `bun test`
- [ ] `npm run typecheck -w @tinadec/desktop`
- [ ] `npm run test -w @tinadec/desktop`
- [ ] `git diff --check`

## 12. 接手顺序与提交纪律

### 推荐接手顺序

1. 读取本文、`apps/desktop/AGENTS.md`、Core/Gateway AGENTS 和当前 git status。
2. 先核对 `/api/v1` OpenAPI、`apps/desktop/src/api.ts`、`generated/client.ts` 与页面实际请求。
3. 完成 P0 的统一 action store、DTO、审批两阶段和 SSE 类型。
4. 再完成 Git/Code 写操作 UI 和测试。
5. 再做 Home/Workbench/Agent Center/Settings 的可见性和错误态。
6. Core 契约补齐后才做 P1/P2 页面，不在 Desktop 先发明接口。

### 阶段提交建议

每个阶段单独提交，提交内容只覆盖一个边界：

1. `feat(desktop): unify governed user action client state`
2. `feat(desktop): complete git and code governance interactions`
3. `feat(desktop): surface run supervision and recovery states`
4. `feat(desktop): complete agent center versioned configuration views`
5. `feat(desktop): align sse and gateway api contracts`
6. `docs(core): publish app-core-ui desktop delivery baseline`

不要把纯视觉调整、Core API 变更、Gateway 代理和 Desktop 状态机混在同一个提交。当前四个未提交文件属于既有 Desktop 在途工作，接手时先审查 diff，不能覆盖或擅自回滚。

## 13. 事实源更新规则

- API 路径、字段和状态发生变化时，先更新 Core Contracts/OpenAPI，再更新 Gateway mapper/OpenAPI，再更新 Desktop client/types，最后更新本文件。
- 破坏性变化仍留在 `/api/v1`，不要创建 v2 或兼容别名。
- 文档中的“已实现”必须能由源码或测试证明；501、空集合和 capability unavailable 必须标为占位。
- 新增页面功能必须同时写出：读取接口、写入接口、状态机、错误态、重连行为和验收测试。
- 本文不记录 nonce、密钥、内部 secret reference 或任何可用于绕过 Core 治理的材料。
