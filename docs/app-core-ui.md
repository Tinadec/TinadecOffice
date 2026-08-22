# TinadecApp Desktop 与 TinadecCore 治理交付清单

> 状态：当前 `/api/v1` 桌面落地契约
> 适用：`apps/desktop`（Electron + Vue）
> 权威来源：`docs/tinadec-core-product-definition.zh-CN.md`

本文是 TinadecCore 能力落到 TinadecApp Desktop 的工作清单。它描述页面、请求方向、状态和验收条件，不复制 Core 的业务规则。Desktop 只保存窗口布局、主题和临时视图状态；租户主体、权限、审批、租约、快照、工具结果和审计事实始终来自 Core。

## 1. 固定边界

- 所有公开 HTTP、SSE 和 WebSocket 接口都使用 `/api/v1`。没有 `/api/v2`、旧别名、迁移路由或弃用周期；破坏性变化直接更新 v1、客户端类型、快照和本文。
- Desktop 默认调用 Gateway；也允许部署配置直连 Core。Gateway 是无状态薄代理，不在 Desktop 与 Core 之间复制审批、PDP、nonce 或快照状态。
- 用户只读查询可以使用 `/api/v1/code/tools/*` 或 `/api/v1/tool-runtime/*` 直连传输面。它们是正式用户工具传输面，不是兼容路由。
- 用户写操作使用 Core UserToolAction；智能体写操作使用 `/api/v1/runs/{runId}/tools/{toolId}/execute`。Desktop 不调用 `/api/v1/approvals` 创建审批，不生成参数哈希或 nonce。
- Electron/Vue 组件使用 TinadecUI 现有控件、surface token 和布局引擎。视觉改动前运行 `opencode run -m oxa/stealth/ox-alpha "..."`，保持现有 TinadecUI 风格。

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

创建请求只提交 `project_id`、`tool_id`、`params` 和可选 `idempotency_key`。主体、租户、工作区、风险、参数哈希、快照、授权决定和审批均由 Core 生成。响应包含稳定的 `audit_reference`，但永远不包含 nonce、受保护租约材料或内部 secret reference。

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
- `completed`：显示结构化结果和审计引用。
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

高风险 Git 写操作创建权限请求或 ActionApproval 前必须捕获 Workspace Snapshot。快照 id/hash 绑定 action；恢复或 resume 时若 HEAD、index、worktree、manifest 或参数发生变化，Core 必须阻断并要求重新创建动作。

## 6. 审批交互

审批面板同时读取两种 Core 投影：

1. `PermissionRequest`：能力是否可以授予；决定入口为治理 decision API。
2. `ActionApproval`：某一次规范化参数动作是否可以消费；决定入口为 `/api/v1/approvals/{id}/decision`。

二者可以在一个 UI 卡片中串联显示，但不能合并为一个本地状态。审批完成后 UI 必须重新读取 action，不从按钮结果推断执行成功。任何代理决定都必须显示 decision source、范围、TTL 和 reason code。

## 7. 错误、断线与恢复

- `401/403`：显示身份或权限范围错误，不自动扩大权限。
- `409`：显示参数、manifest、ETag、上下文或快照冲突，要求重新读取后再提交新动作。
- `412`：显示版本过期，刷新 Agent/Mode/Policy 后重试。
- `422`：显示 Core 的结构化校验错误，不在 Desktop 猜测字段。
- `429`：保持动作 id 和状态，按 Retry-After 重新查询，不重复创建。
- `502/503`：保留当前 action、run 或 snapshot 状态；恢复连接后查询 Core，不把断线当成失败。
- `outcome_unknown`：只允许进入恢复决定流程；不得自动调用 `resume` 反复执行。

SSE 断开只停止渲染器读取，不取消 Core run 或 user action。重新连接后按 cursor/事件序号去重，并以 Core 的持久投影校正 UI。

## 8. 验收条件

- Desktop 源码中不存在生产 `createApproval` 或 `createShellApproval` 调用。
- Git 写操作的网络请求只指向 `/api/v1/user/tool-actions`、resume、snapshot override 和治理决定接口。
- Core 响应、Gateway 响应、OpenAPI 和日志中不存在明文 nonce、nonce secret reference 或内部租约材料。
- 快照失败时不会调用 Tool Provider；override 只能由当前用户提交并留下不可逆审计标记。
- 拒绝、撤销、过期、参数篡改、重复消费和跨租户请求均无外部副作用。
- Gateway 测试证明它不保存 Core 状态、不计算 PDP、不批准动作，并继续原样转发 `/api/v1/code/tools/*` 与 `/api/v1/tool-runtime/*`。
- Desktop typecheck、定向 user action 测试、Git 面板交互测试通过；完整套件中的历史 Vue/happy-dom 环境失败必须单独标注，不能归因于 Core 治理功能。

## 9. 阶段提交与工具

每个阶段独立提交，建议提交主题固定为：

1. `feat(core): close governance nonce and user tool actions`
2. `feat(core): add git workspace snapshot provider and prewrite guard`
3. `feat(core): seed git steward and worker git topology`
4. `refactor(gateway): normalize tool catalog and core governance proxy`
5. `feat(desktop): route git mutations through core governance`
6. `docs(core): publish app-core-ui and v1 governance contract`

Electron/Vue 阶段的设计输入使用 `opencode run -m oxa/stealth/ox-alpha`，但生成的 UI 代码必须遵循 TinadecUI 控件、布局和状态事实源约束。
