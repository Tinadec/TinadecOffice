# TinadecApp Desktop 与 TinadecCore 治理交付清单

> 状态：当前 `/api/v1` 桌面落地契约
> 适用：`apps/desktop`（Electron + Vue）
> 权威来源：`docs/tinadec-core-product-definition.zh-CN.md`
> 本轮范围：只维护 Core-to-App 契约和后续落地清单，不继续修改 Vue 组件

本文是 TinadecCore 能力落到 TinadecApp Desktop 的工作清单。它描述页面、请求方向、状态和验收条件，不复制 Core 的业务规则。Desktop 只保存窗口布局、主题和临时视图状态；租户主体、权限、审批、租约、快照、工具结果和审计事实始终来自 Core。

## 1. 固定边界

- 所有公开 HTTP、SSE 和 WebSocket 接口都使用 `/api/v1`。没有 `/api/v2`、旧别名、迁移路由或弃用周期；破坏性变化直接更新 v1、客户端类型、快照和本文。
- Desktop 默认调用 Gateway；也允许部署配置直连 Core。Gateway 是无状态薄代理，不在 Desktop 与 Core 之间复制审批、PDP、nonce 或快照状态。
- 用户只读查询可以使用 `/api/v1/code/tools/*` 或 `/api/v1/tool-runtime/*` 直连传输面。它们是正式用户工具传输面，不是兼容路由。
- 用户写操作使用 Core UserToolAction；智能体写操作使用 `/api/v1/runs/{runId}/tools/{toolId}/execute`。Desktop 不调用 `/api/v1/approvals` 创建审批，不生成参数哈希或 nonce。
- Electron/Vue 组件使用 TinadecUI 现有控件、surface token 和布局引擎。后续客户端实现由负责 Desktop 的 Agent 直接完成，不依赖外部 UI 代码生成命令。

## 2. 页面与职责

| 页面/区域 | 读取 | 写入 | 必须展示 |
| --- | --- | --- | --- |
| Home 会话与会议入口 | sessions、messages、runs、events、orchestration | interactions、run control | meeting 正式答复、run 状态、监督结果、等待原因 |
| Approval/治理面板 | approvals 投影、permission requests、user tool actions | permission decision、approval decision、resume | 请求主体/工具/资源/风险、授权与动作审批的分离状态 |
| Code Editor | `read_file`、diff、文件树直连查询 | `write_file` UserToolAction | snapshot、参数冲突、动作状态、结果未知 |
| Git Changes/CommitPanel | git status/diff/log/branch 查询 | stage、unstage、commit、push、checkout、branch、worktree、merge、rebase、conflict resolve UserToolAction | 写前快照、审批链、恢复入口、不可逆标记 |
| Tool Catalog | Core/Tool Provider manifest、search | 无 | provider、schema、风险和是否需要审批，以 Core 返回值为准 |
| Workspace Snapshot | snapshots、恢复结果 | restore、snapshot override | Git/非 Git、HEAD/index/worktree hash、冲突和恢复计划 |
| Agent Center | Agent/Mode/Prompt/Policy 版本与 topology | 草稿、发布、归档、委托和租约控制 | `operation`/`execution` 双泳道、版本 hash、ETag 冲突 |

## 3. Core API 映射

### 3.1 UserToolAction

```text
GET  /api/v1/user/tool-actions?status=...
POST /api/v1/user/tool-actions
GET  /api/v1/user/tool-actions/{id}
POST /api/v1/user/tool-actions/{id}/resume
POST /api/v1/user/tool-actions/{id}/snapshot-override
```

创建请求只提交 `project_id`、`tool_id`、`params` 和可选 `idempotency_key`。主体、租户、工作区、风险、参数哈希、快照、授权决定和审批均由 Core 生成。响应包含稳定的 `audit_reference`、`snapshot_override`、可选 `snapshot_override_reason`、`non_reversible` 和可选 `compensation_guidance`，但永远不包含 nonce、受保护租约材料或内部 secret reference。

Desktop API 封装应至少提供：

- `createUserToolAction`、`getUserToolAction`、`listUserToolActions`；
- `resumeUserToolAction`、`overrideUserToolActionSnapshot`；
- `listPermissionRequests`、`getPermissionRequest`、`decidePermissionRequest`；
- `listApprovals`、`decideApproval`（只作为 Core 持久投影/决定入口）。

### 3.2 治理与快照

```text
GET  /api/v1/governance/permission-requests
GET  /api/v1/governance/permission-requests/{id}
POST /api/v1/governance/permission-requests/{id}/decision
POST /api/v1/governance/policy-bundles
POST /api/v1/governance/capability-grants
POST /api/v1/governance/approval-delegations
POST /api/v1/governance/capability-grants/{id}/revoke
POST /api/v1/governance/approval-delegations/{id}/revoke
POST /api/v1/governance/capability-leases/{id}/revoke

GET  /api/v1/projects/{projectId}/snapshots
GET  /api/v1/workspace-snapshots/{snapshotId}
POST /api/v1/workspace-snapshots/{snapshotId}/restore
```

`CapabilityLease`、`ActionApproval` 的 nonce 只在 Core 内部消费；UI 只显示公开 id、状态、TTL、次数和 reason code。

### 3.3 用户直连查询

```text
GET/POST /api/v1/code/tools/{toolId}/execute
GET       /api/v1/tool-runtime/health
GET       /api/v1/tool-runtime/manifest
GET       /api/v1/tool-runtime/tools
POST      /api/v1/tool-runtime/tools/{toolId}/execute
```

查询和 provider 传输错误原样显示为上下文错误。只读查询不得被 UI 改写为 UserToolAction；任何会修改工作区或外部状态的动作必须使用 Core UserToolAction。

## 4. UserToolAction 状态机

```text
requested
  -> snapshot_required       高风险写操作等待快照
  -> awaiting_delegate       委托包络内等待审批代理协调
  -> awaiting_user            超出委托范围，等待当前用户
  -> awaiting_approval        权限已获准，等待单次动作审批
  -> running
  -> completed

任意阶段 -> blocked           拒绝、过期、撤销、参数/manifest/快照漂移
running   -> outcome_unknown   外部副作用已发出但进程结果未知
```

UI 映射要求：

- `snapshot_required`：显示捕获中或失败原因；快照失败只能提供当前用户一次性 override，且必须标记 `non_reversible`。
- `awaiting_delegate`：显示正在进行委托范围校验，不提供伪造的“已批准”按钮。
- `awaiting_user`：显示资源、动作、风险、范围和过期时间；决定后刷新同一个 action。
- `awaiting_approval`：显示动作审批与权限决定是两道门；批准后调用 `resume`。
- `running`：显示不可重复提交的进行中状态。
- `completed`：显示结构化结果、审计引用；`non_reversible=true` 时持续显示补偿建议，不能显示“可一键回滚”。
- `blocked`：显示稳定 reason code，不重试同一个 idempotency key 以绕过拒绝。
- `outcome_unknown`：禁止自动重放；引导用户先查看当前工作区、快照和恢复决定。

所有写请求必须使用稳定幂等键。幂等键只能表达 UI 重试身份，不能携带批准事实、nonce 或客户端计算的风险。

## 5. Git Desktop 交付清单

### 查询

Git status、diff、log、branch、worktree、conflict preview 继续走用户直连工具传输面。查询结果可用于预览，但不能被 Desktop 当作审批事实。

### 写操作

下列动作统一调用 `createUserToolActionForPath` 或等价 Core client：

- `git_stage`、`git_unstage`；
- `git_commit`、`git_push`、`git_fetch`、`git_pull`；
- `git_checkout`、`git_branch_create/delete/rename`；
- `git_worktree_create/remove`；
- `git_merge`、`git_rebase`、`git_conflict_resolve`。

CommitPanel 和 `useGitOperation` 不得调用 `createApproval` 或直接执行 Gateway 工具写路由。动作参数中需要包含仓库路径、目标 ref/path、确认字段和可重试的幂等键；参数规范化由 Core/Tool Provider 完成。

每个语义不同的 Git 命令都是一个新 UserToolAction。特别是 `git_rebase start`、`continue`、`skip` 和 `abort` 必须分别创建动作，分别冻结参数、捕获快照并经过治理；禁止用 `resume` 把原 `start` 动作改造成后续命令。`resume` 只推进同一动作已经持久化的状态，绝不改变其 tool id 或 params。

高风险 Git 写操作创建权限请求或 ActionApproval 前必须捕获 Workspace Snapshot。快照 id/hash 绑定 action；恢复或 resume 时若 HEAD、index、worktree、manifest 或参数发生变化，Core 必须阻断并要求重新创建动作。

### 5.1 Git 参数契约

所有 Git 工具都必须提交 `repository_path`，值为当前项目的规范化仓库根路径。Desktop 不提交 `risk`、`approved`、`parameters_hash`、主体或快照 hash。确认字段取自实时 Tool Provider manifest，值必须是非空字符串，不能发送布尔值。当前内置工具映射如下：

| 工具 | 关键参数 | 显式确认字段 |
| --- | --- | --- |
| `git_stage` / `git_unstage` | `repository_path`, `paths` | 无；仍需 Core 治理 |
| `git_commit` | `repository_path`, `message`, `include_all`, `commit_staged_only` | `confirm_commit` |
| `git_push` | `repository_path`, `remote`, `branch`, `set_upstream` | `confirm_push` |
| `git_fetch` / `git_pull` | `repository_path`, `remote`, `branch` | `confirm_fetch` / `confirm_pull` |
| `git_checkout` | `repository_path`, `branch` | `confirm_checkout` |
| `git_branch_create/delete/rename` | `repository_path` 和目标分支字段 | 对应 `confirm_branch_*` |
| `git_worktree_create/remove` | `repository_path`, `path`, 可选 `branch` | 对应 `confirm_worktree_*` |
| `git_merge` | `repository_path`, `branch` | `confirm_merge` |
| `git_rebase` | `repository_path`, `action`, start 时的目标分支 | `confirm_rebase` |
| `git_conflict_resolve` | `repository_path`, `path`, `strategy` | `confirm_resolve` |

`git_push` 请求中不要添加 Tool Provider schema 未声明的 `action: "push"`。Core 会在入场时按冻结 schema 拒绝未知字段。`git_push` 的公开 action 投影必须显示 `non_reversible=true` 和 `compensation_guidance`；本地 Workspace Snapshot 不能伪装成远程 ref 的回滚能力。

### 5.2 幂等与动作身份

- 幂等键由“项目 + tool id + 规范化参数 + 用户触发意图”生成稳定 hash，最大 256 字符。
- 同一按钮因网络重试应复用键；参数、目标分支、提交消息或 rebase action 改变时必须生成新键。
- 前端状态表使用 `action.id` 作为主键；`permission_request_id` 和 `action_approval_id` 只是阶段引用，会在同一 action 生命周期内先后出现。
- 相同幂等键配不同参数收到冲突时，UI 必须报错，不能静默生成随机键绕过 Core。

### 5.3 两阶段推进算法

```text
create UserToolAction
  -> 保存 action.id 并渲染 Core status
  -> awaiting_user: 决定 PermissionRequest
  -> GET action.id，直到出现 action_approval_id 或终态
  -> awaiting_approval: 决定 ActionApproval
  -> GET action.id / POST resume，直到 completed、blocked、failed 或 outcome_unknown
```

`snapshot_required`、`requested`、`running` 和 `outcome_unknown` 都不是可审批记录，不能投影成带“批准/拒绝”按钮的普通 pending approval。轮询采用有上限的退避并在组件卸载、项目切换或终态时停止；Gateway/SSE 重连后始终用 GET action 投影校正本地状态。

## 6. 审批交互

审批面板同时读取两种 Core 投影：

1. `PermissionRequest`：能力是否可以授予；决定入口为治理 decision API。
2. `ActionApproval`：某一次规范化参数动作是否可以消费；决定入口为 `/api/v1/approvals/{id}/decision`。

二者可以在一个 UI 卡片中串联显示，但不能合并为一个本地状态。PermissionRequest 决定完成后，Core 可能为同一 action 新建一个不同 id 的 ActionApproval；UI 必须以 `action.id` 为稳定身份重新读取 action，再切换到新的 `action_approval_id`。不得继续用旧 `permission_request_id` 查找本地卡片，也不得从决定按钮结果推断工具执行成功。任何代理决定都必须显示 decision source、范围、TTL 和 reason code。

## 7. 错误、断线与恢复

- `401/403`：显示身份或权限范围错误，不自动扩大权限。
- `409`：显示参数、manifest、ETag、上下文或快照冲突，要求重新读取后再提交新动作。
- `412`：显示版本过期，刷新 Agent/Mode/Policy 后重试。
- `422`：显示 Core 的结构化校验错误，不在 Desktop 猜测字段。
- `429`：保持动作 id 和状态，按 Retry-After 重新查询，不重复创建。
- `502/503`：保留当前 action、run 或 snapshot 状态；恢复连接后查询 Core，不把断线当成失败。
- `outcome_unknown`：只允许进入恢复决定流程；不得自动调用 `resume` 反复执行。

当前 Core 只有 Agent ToolExecution 的 `/api/v1/tool-executions/{id}/recovery-decision`；UserToolAction 尚无公开 recovery-decision API。因此 Desktop 对用户动作的 `outcome_unknown` 现阶段必须只读展示，提供 status/diff/snapshot 检查入口，不得错误调用 Agent execution API。待 Core 增加 UserToolAction 恢复决定契约后，再实现“确认已完成、执行补偿、标记失败”等按钮，并同步更新本文和 `/api/v1` OpenAPI。

SSE 断开只停止渲染器读取，不取消 Core run 或 user action。重新连接后按 cursor/事件序号去重，并以 Core 的持久投影校正 UI。

## 8. 验收条件

- Desktop 源码中不存在生产 `createApproval` 或 `createShellApproval` 调用。
- Git 写操作的网络请求只指向 `/api/v1/user/tool-actions`、resume、snapshot override 和治理决定接口。
- Core 响应、Gateway 响应、OpenAPI 和日志中不存在明文 nonce、nonce secret reference 或内部租约材料。
- 快照失败时不会调用 Tool Provider；override 只能由当前用户提交并留下不可逆审计标记。
- 拒绝、撤销、过期、参数篡改、重复消费和跨租户请求均无外部副作用。
- Gateway 测试证明它不保存 Core 状态、不计算 PDP、不批准动作，并继续原样转发 `/api/v1/code/tools/*` 与 `/api/v1/tool-runtime/*`。
- Desktop typecheck、定向 user action 测试、Git 面板交互测试通过；完整套件中的历史 Vue/happy-dom 环境失败必须单独标注，不能归因于 Core 治理功能。

### 8.1 Desktop 后续 P0 测试场景

1. PermissionRequest 批准后重新读取相同 `action.id`，界面切换到新 `action_approval_id`，旧权限卡不残留为 pending。
2. `git_commit`、`git_push` 请求都含 `repository_path` 和 manifest 声明的非空字符串确认字段；没有布尔确认值或额外 `action` 字段。
3. `git_rebase start/continue/skip/abort` 产生四个不同 action/idempotency key，任何后续命令都不 resume 原 start 动作。
4. `snapshot_required`、`running`、`outcome_unknown` 不显示批准按钮；`outcome_unknown` 不发生自动重放。
5. `git_push` 显示不可逆标记和补偿建议；snapshot override 显示用户原因并持续保留不可逆警告。
6. 决策后断线、Gateway 503 或 Desktop 重启时，按 action id 恢复投影，不重复创建、不重复批准、不重复调用工具。
7. 切换项目后停止旧项目轮询，旧 action 不进入新项目的 Approval 或 CommitPanel。

## 9. 实现状态与交接顺序

### Core 已提供

- UserToolAction create/list/detail/resume/snapshot-override、稳定审计引用和 tenant/workspace 隔离；
- 冻结 Tool Provider v2 manifest 与 descriptor，参数/schema/确认字段校验；
- PermissionRequest、CapabilityLease、ActionApproval、内部 nonce 保护和一次消费；
- 高风险写前 Git/文件系统快照、manifest/参数/快照漂移 fail-closed；
- 启动恢复：等待态可重放唤醒，孤立 `running` 转 `outcome_unknown`；
- `non_reversible` 与 `compensation_guidance` 公开投影。

当前启动恢复只扫描宿主 `ITenantContextAccessor.Current` 对应的 tenant/workspace。这符合本地单工作区部署；OIDC 多租户和多实例版本必须引入全租户恢复调度与分布式 claim 后，才能宣称云端全域恢复完成。

### Desktop 当前已有基础但仍需复核

- `apps/desktop/src/api.ts` 已有 UserToolAction、permission、approval 和 snapshot 基础 client；
- Git、文件编辑和 shell 的部分写路径已经改用 UserToolAction；
- `CommitPanel.vue` 与 `useGitOperation.ts` 仍有重复状态编排，后续应抽成单一 composable/store；
- 当前工作树中有参数确认字段、仓库路径和投影 helper 的在途改动，接手 Agent 必须先审查而非覆盖。

### Desktop 后续实施顺序

1. 更新 `UserToolActionDto`，加入 `non_reversible`、`compensation_guidance`，并以 action id 建立统一 store。
2. 抽取统一的 create/poll/decide/resume 流程，修复 PermissionRequest 到 ActionApproval 的两阶段切换。
3. 按 5.1 校验全部 Git mutation 参数，并将每个 rebase 子命令拆成新 action。
4. 让 CommitPanel、Git 面板、CodeEditor、PatchPreview、FileTree 和 shell 共用状态组件；移除所有生产 `createApproval` 写入路径。
5. 实现 snapshot override 确认对话框、不可逆警告和 `outcome_unknown` 只读恢复页。
6. 完成 8.1 的组件/交互测试，再运行 typecheck 和 Desktop 全套测试。

## 10. 阶段提交

每个阶段独立提交，建议提交主题固定为：

1. `feat(core): close governance nonce and user tool actions`
2. `feat(core): add git workspace snapshot provider and prewrite guard`
3. `feat(core): seed git steward and worker git topology`
4. `refactor(gateway): normalize tool catalog and core governance proxy`
5. `feat(desktop): route git mutations through core governance`
6. `docs(core): publish app-core-ui and v1 governance contract`

Desktop 接手 Agent 应按上述职责拆分提交，不把 Core 契约、Vue 状态编排和纯视觉修改混入同一个提交。
