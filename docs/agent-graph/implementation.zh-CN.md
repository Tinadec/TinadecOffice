# 双层智能体架构：实现细节

> 方向见 `architecture.zh-CN.md`，施工顺序见 `todo.zh-CN.md`。
> 本文写"怎么做"，每条都指到现有代码或明确的新增点。改代码前请先读对应源码核实。

---

> 2026-10-01 复查：下文“已实现”指当时的机制切片。可靠唤醒、实际调用者派发边界、动态模板提示词、完整可见性、执行真并行和资源绑定尚有缺口；当前验收与修复顺序以 [复查报告](review-2026-10-01.zh-CN.md) 及 todo 的 N1–N9 为准。尤其不能把输入清空称为可靠消费、把登记租约称为已隔离执行。

## 1. 总原则

**拓扑自由，动词固定，动作带电，计数在引擎。**

落到代码上的四条纪律：

1. **不写死"谁在什么时候被唤醒"**：唤醒靠**订阅**，不靠角色名 `switch`。
2. **不写死"谁能派给谁"**：派发范围来自描述文件，引擎在**调用当下**校验。
3. **计数一律在引擎**：深度、实例数、并发、唤醒次数、自动批准次数，AI 只能在上限内自由。
4. **新增字段一律可选**：`JsonIgnore(WhenWritingNull/WhenWritingDefault)`，不得改变既有冻结字节（否则 digest 漂移）。

---

## 2. 数据模型（新增表）

两库同批：`TinadecCore/Storage.Migrations.Sqlite/` 与 `TinadecCore/Storage.Migrations.PostgreSql/`。
命名沿用现有风格（如 `UserToolActionsAndNonce.cs`、`WorkspaceSnapshotBindings.cs`）。

### 2.1 资源账本 `resource_leases`

"谁正在用什么"的单一事实源。审查智能体的冲突治理、worktree 管家、环境管家都读它。

```csharp
public sealed class ResourceLeaseRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? RunId { get; set; }            // 哪条 run 拿着
    public Guid? TaskId { get; set; }
    public Guid? AgentInstanceId { get; set; }
    public string Kind { get; set; } = "";      // path | worktree | environment | terminal | git_branch
    public string ResourceKey { get; set; } = ""; // 规范化后的绝对路径 / worktree 名 / 环境 id
    public bool Exclusive { get; set; }         // true = 独占，false = 共享读
    public string Status { get; set; } = "active"; // active | released | expired
    public string Reason { get; set; } = "";
    public long Revision { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}
```

**写路径（2026-09-29 按 architecture §7.4 收窄）**：账本只记两类事实——
1. **物理独占的资源**：worktree（一个 worktree 同一时间只有一条写入 run）、环境、终端；
2. **派发时声明的写入范围**（§15）：任务声明要写哪些路径，派发即取租约，任务终态释放。

**shell 不再按命令取租约**（第一批代码里 `shell` → 工作区根的做法撤销：它会把同一工作区的并行变成串行，而三家同类产品都不这么做）。单个写文件工具仍可按路径取租约作为兜底。

冲突判定：同一资源、或**一方是另一方的上级目录**，且任一方独占、状态 active、不同 run → 冲突。比较前把相对路径按工作区根转成绝对路径、统一大小写。
**释放**：任务终态、run 终态、进程退出时按 run 批量释放（引擎已有 run 终态钩子）。
**索引**：`(WorkspaceId, ResourceKey, Status)`；并发取同一资源必须靠**唯一约束**裁决（第一批代码的注释说"由唯一索引裁决"，但索引并未建，待补）。**没有过期时间**：第一批代码的"租约会过期"注释不成立，要么实现、要么删除。

### 2.2 治理订阅 `governance_wakes`（已被取代，待删除）

> **2026-09-29 决定**：不另建唤醒队列，直接用 TinaChat 的 `ChatWake`。治理角色是组织成员（architecture §9.1），提醒就是投进它收件箱的一条消息；`ChatWake` 已经把"与消息同一事务写入、等待期间合并、限速不丢、CAS 防重复领取"做对了，而第一批的 `agent_graph_wakes` 还带着 SQLite 不支持的时间比较。下面的草案只作历史记录。

订阅式唤醒的调度骨架。**直接镜像 `ChatWake`**（`TinadecCore/TinaChat/TinaChatDbContext.cs:240`）：它有 `Status`(pending/running)、`Attempts`、`AvailableAt`、CAS 抢单，正是需要的形状。

```csharp
public sealed class GovernanceWakeRecord
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? SessionId { get; set; }
    public string Topic { get; set; } = "";        // 订阅主题，见 3.1
    public string SubjectJson { get; set; } = "{}"; // 唤醒时要看的对象（run_id / lease_id / action_id）
    public Guid SubscriberAgentInstanceId { get; set; }
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

### 2.3 多门审批 `approval_gates`（已落地为 `agent_graph_approval_gates`，见 §6）

今天的审批只有一个决策者。两道门需要"一个请求多条门记录"。落地时多了 `RunId`、`DeciderAgent`、`ToolId`、`ClaimedAtUnixMs`，状态多了 `evaluating | escalated | superseded`；`PermissionRequestId` 列实际存的是被决定的**工具审批** id（审批卡上的 id）。

```csharp
public sealed class ApprovalGateRecord
{
    public Guid Id { get; set; }
    public Guid PermissionRequestId { get; set; }  // 指向现有 PermissionRequestRecord
    public int GateIndex { get; set; }             // 0 = 第一道
    public string GateKind { get; set; } = "";     // human | conversation_identity | reviewer_agent
    public Guid? DeciderAgentInstanceId { get; set; }
    public string Status { get; set; } = "pending"; // pending | approved | rejected | skipped
    public string? Reason { get; set; }
    public string ContextEvidenceJson { get; set; } = "{}"; // 这一道门看了什么
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
```

**放行条件**：请求的所有门都 `approved` 才放行；任一门 `rejected` 即拒。**不得**让"上了门"变成绕过 PDP 的路径——门只做加法，binding/boundary 检查照旧（参照 `GovernanceService.cs` 里 auto-policy 的注释：Returning Approved DOES NOT skip any binding check）。

---

## 3. 订阅式唤醒（替换写死的角色名 switch）

**要替换的东西**：`TinadecCore/DmaEA/Operations/OperationalTriggers.cs` 的 `Resolve()`——它拿角色名字符串去 `switch`（`context_compressor` / `capability_advisor` / `evolution` / `git_steward`），只在四个固定时机匹配。

### 3.1 主题表（引擎侧固定的一小撮事实类型）

主题是引擎的词汇表，**少而固定**：

| 主题 | 触发时机 | 典型订阅者 |
|---|---|---|
| `task_graph_created` | 任务图建成 | 能力建议 |
| `task_completed` | 任务到终态 | 上下文压缩 |
| `run_finalized` | run 收尾 | Git 提交、经验整理 |
| `capability_missing` | 选人失败 | 能力建议 |
| `approval_requested` | 有动作待批 | 审查智能体 |
| `lease_conflict` | 资源冲突 | 冲突治理 / 审查智能体 |
| `context_over_budget` | 上下文超预算 | 上下文压缩 |
| `worktree_requested` | 需要新 worktree | worktree 管家 |

### 3.2 订阅声明（描述文件侧）

角色在它的描述文件里声明订阅。建议放在节点关系文件的扩展字段（保持五字段必填不变，新增可选字段）：

```json
{ "subscriptions": ["approval_requested", "lease_conflict"] }
```

**解析**：`Runtime/FormalModeResolver.cs` 读关系文件时收集 `subscriptions` → 冻结进 `FrozenRunConfigurationV1`（新字段可选）→ 引擎在对应时机查"哪些实例订阅了这个主题" → 写 `governance_wakes` 行。

**兜底**：没有 `subscriptions` 字段的旧包，按今天的四个角色名**推导**出等价订阅（迁移兼容），不改变既有行为。

### 3.3 调度器

~~新增 `GovernanceWakeService : BackgroundService`~~（2026-09-29 取消，见 §2.2）。事实发生时，引擎给订阅者在组织里的收件箱投一条提醒（`ChatWake`），由现有的 `TinaChatWakeService` 排空；排空时不再只起草简报，而是让该成员用**自己的上下文和工具**跑一轮（§14.3）。

**每次唤醒都要计费到账本**：唤醒次数计入 §4 的引擎计数，超出即不再唤醒（防 A 唤醒 B、B 唤醒 A）。

---

## 4. 引擎计数（不可由 AI 决定）

现成的位置：`TinadecCore/DmaEA/AgentRuntimeConfiguration.cs:13` 的 `SpawnPolicy(MaxDepth, MaxAgentsPerRun, MaxParallelWorkers)`，默认 `(2, 16, 4)`（`Configuration/default-agent-runtime.toml`）。

### 4.1 补洞：深度对引擎派出的 worker 不生效

**现状**：深度检查在 `AgentInstanceService.cs:217`（`SpawnAsync` 路径），而引擎用 `CreateRootAsync` 创建 worker（`FullDuplexRunEngine.cs:2206`、`:4392`、`:4458`、`:5260`，lane 路径 `FullDuplexRunEngine.Lanes.cs:1397`），**绕过深度检查**。实例预算在 `FullDuplexRunEngine.cs:2132` 的 `EnsureGraphWorkerBudget` 有拦。

**改法**：在 `CreateRootAsync` 也做深度与实例数检查（把 `EnsureGraphWorkerBudget` 与深度检查提到一个共用入口），并在 `RuntimeAgentSeed` 里传递 `ParentInstanceId` 与 `GenerationDepth`。

### 4.2 新增计数

| 计数 | 位置 | 默认 |
|---|---|---|
| 唤醒次数 / 会话 / 小时 | 新 `GovernanceWakeService` | 待定，建议 60 |
| 自动批准次数 / run | 已有（`AutoApproveOptions.AutoApproveMaxPerRun`，默认 5） | 沿用 |
| 派发深度 | §4.1 | `SpawnPolicy.MaxDepth` |
| 并行实例数（含治理层） | 新 | `MaxParallelWorkers` |

---

## 5. 描述文件的派发闸（当前是空的）

**现状**：关系文件五个字段都校验，但**只有 `agent_types` 被消费**（`Runtime/FormalModeResolver.cs:521-527` 编译成 spawn 白名单）；`allowed_dispatch_targets` **全文没有消费者**。

**改法**：让 `allowed_dispatch_targets` 在三个校验点生效（与今天 `agent_types` 同一条路径）：

1. **规划期**：`FullDuplexRunEngine.ValidateAndMaterializeGraph`（`FullDuplexRunEngine.cs:1240`）——`assignee` 不在允许集合里即抛 `InvalidTaskGraphException`，文案列出合法目标。
2. **调用期**：`TinadecCore/Tools/ToolDispatcher.cs` 的 `ExecuteTaskDispatchToolAsync`（`:764` 起）——`agent` 不在允许集合里即回可纠正错误（今天是拿完整名册校验，改为拿"该实例被允许派发的子集"校验）。
3. **选人期**：`FullDuplexRunEngine.ResolveRequestedWorker`——最后一道兜底。

**语义**：`allowed_dispatch_targets` 缺省 = 沿用今天的档位语义（声明边目标 ∪ spawn 白名单），不改变旧包行为。

---

## 6. 多门审批（2026-09-29 已实现：委托审批门）

**落地与原计划的两处不同**，都是被现状逼出来的：

1. **门挂在审批层，不挂在 PDP。** 治理层禁止祖先/后代智能体互批（`self_approval_forbidden`），委托模式由 PDP 将可委托的写以 `delegated_gate_release` 放到工具审批层，再由独立门裁决。PDP 和门服务均调用 `DelegatedApprovalRules.Delegable`：排除 Core 虚拟工具、人工专属工具与 `elevated/high/critical`、未知风险，并遵守 `DelegatedApprovalRiskMax` 更窄上限。唯一的工具例外是具体会话登记的低/中风险 shell/command_run；读取旧规则时也检查工具、会话和风险。规则命中不能作为跳过该判定的返回分支。
2. **配置面是权限模式，不是 workspace 配置。** 用户按消息选 `delegate-conversation` / `delegate-reviewer` / `delegate-both`（`ApprovalDelegationModes`），冻结进 run，和 ask / auto-approve / full-access 同一个位置——它们本来就是同一句话："这条消息把多少审批工作交出去"。

**门的顺序**：`delegate-both` 先审查员后对话身份（独立的那一眼先看；它驳回就省掉第二次调用）。

**门服务**（`Runtime/ApprovalGateService.cs`，`BackgroundService` + `IApprovalGateLedger`）：

- 候选：委托模式、未终态 run、`kind=tool`、非用户动作、状态 pending 的审批（窗口 512，按创建时间在内存排序——SQLite 不能按 `DateTimeOffset` 排序）；已驳回/退回/被取代的审批不再碰。
- 每轮每个审批只问**一道**门；有进展时 50ms 后再跑一轮，没有进展按间隔（默认 2s）。并行度默认 4。
- 门行 CAS 认领（`Status pending→evaluating` + `ClaimedAtUnixMs`），认领超过 `ClaimLapseSeconds`（默认 300）视为宿主已死、可重认领；记录结论时再 CAS 一次，认领被别人接走则丢弃自己的结论。
- 每 run 门决定上限 `MaxGateDecisionsPerRun`（默认 200），超了把后续审批交还给人。
- 结果只走人点击的那条路：`IToolApprovalCoordinator.DecideDelegatedAsync`（与 `DecideAsync` 同一转移，决策行 `DecidedByPrincipalId = Guid.Empty`）+ `approval.decided`（`decided_by=delegated_gates`）+ `IFullDuplexRunEngine.EnqueueAsync`。人先点了（`InvalidOperationException`）则把未决门记 `superseded`。
- 每道门的结论另记 `approval.gate_decided`（门序号、类型、决策者、理由）。

**判断**（`DmaEA/ApprovalGateJudge.cs`，`IApprovalGateJudge`）：一次独立模型调用，不在任何 run 里、无工具面、`ToolMode=None`、90 秒超时。

- 审查员 = 冻结配置里声明订阅 `approval_requested` 的常驻治理角色 → 否则第一个常驻治理角色 → 否则 Core 内置审查员（`core_reviewer`）；永远不是发起这次调用的执行者本身。用它自己的提示词与模型计划（`ModelInvocationChatFactory`）。
- 看到的事实（`ApprovalGateQuestion`，蛇形 JSON，原样作为证据存进门行）：工具、风险、参数（`ApprovalEvidenceProjector.RedactSecrets`：密钥键任意深度替换，正文保留可读，截 6000 字）、任务标题/描述/执行者/写入范围、工作区根、本 run 持有的资源（≤24 条）、权限模式；**只有对话身份的门**多一项 `user_goal`（截 3000 字）。
- 输出协议：一个 JSON `{"decision":"approve|reject|escalate","rationale":"…"}`；解析不出、无路由、超时、模型报错一律 `escalate`。对话身份自己发起的调用（solo 主人自己干活）直接退回：决定者不批自己的动作。

**读侧**：`GET /api/v1/approvals/{approvalId}/gates` → `ApprovalGatesDto`（`evaluating | approved | rejected | escalated | superseded`）。按审批本身的当前状态解析：人先决定了，未决门显示 `superseded`，门结论与审批结论相反也显示 `superseded`。

**测试**：`ToolChainEndpointTests.ApprovalGates.cs`（端到端 6 条）、`AutoApprovePolicyTests`（委托放行 10 条）、`ApprovalEvidenceProjectorTests.RedactSecrets_…`。

---

## 7. 治理层可见性（跨 run）

**现状**：治理智能体拿不到下层实时状态（只看到会话历史与收尾证据）。

**改法**：

1. 新增只读投影端点 `GET /api/v1/sessions/{sessionId}/topology`：返回**run 树**（run → 实例 → 任务 → 资源租约）+ 各自状态，数据来自 checkpoint + 实例表 + `resource_leases`。已有近似物：`/api/v1/sessions/{id}/orchestration`（`DmaeaEndpoints.cs:94`）与 `/api/v1/runs/{id}/orchestration`（`:303`），扩成会话级树。
2. 新增治理工具（Core 虚拟工具，引擎执行）：`list_lower_activity`（看下层在干什么）——**默认所有治理层实例持有**，可由用户按身份关闭。
3. **对话身份默认可见全部**（含审查智能体的判断记录）。

---

## 8. 三种投递

### 8.1 排队

**现状缺陷**：会话活跃 run 上限默认 2，未满时"排队"与"并行"同路（`InteractionsEndpoints.cs:324`、`FullDuplexRunCoordinator.cs:184`）。

**改法**：`dispatch_mode=queued` 时**不并发**——若会话已有活跃 run，一律进队列（复用现有的 directive 行 + `interaction.queued_deferred` 路径，`FullDuplexRunEngine.Lanes.cs:1061`），等当前 run 终态由 run-terminal 钩子放行。

**已实现（2026-09-29）**：

- **准入**：`FullDuplexInvocation.QueueBehindActiveRun`（接口 `queued` 置真）。会话有任何未终态 run（含停在人工决定上的——容量上限不数它们，排队要等它们）时，新任务拒以 `SESSION_BUSY`，接口把消息落 directive + 用户消息 + `run.queued`，返回 201 `status=queued`、`run_id`=它等的那条 run、无 `turn_id`。`parallel` 不变，只受活跃 run 上限约束。
- **队列只有一个主人**：接口挑"已经持有待放行排队消息的活跃 run"，否则最新的活跃 run（`QueueOwnerAsync`），所以新消息不会插到另一条 run 手里更早的消息前面。
- **按序放行**：run 终态时只放行队头（`QueueBehindActiveRun` 照样为真）；成功则其余整体挪到新 run 后面（`ILifecycleManager.RequeueRunDirectivesAsync`，`CreatedAt` 不动所以顺序不变，发 `interaction.queue_moved`，新主人上重发 `run.queued`）；会话里仍有别的 run 在跑（例如一条并行 run）则整队挪到它后面等；无法放行则原地留待修复扫描，后面的不许越过它。
- **出队**：`POST …/interactions/{directiveId}/cancel` 对仍在排队的消息把 directive 记 `cancelled`（`interaction.queue_cancelled`），原话留在会话里。
- **Desktop**：据"无 `turn_id`"识别排队响应，显示排队卡片、不再把它等的 run 标成 queued；Core 放行/出队/无法读取时卡片消失并跟上新 run；卡片上的忽略/编辑/转向/并行先在 Core 出队，并行复用原 `client_message_id`（不重复发同一句话）。
- **测试**：`FullDuplexEndpointTests.Queued_NeverRunsAlongsideTheActiveRun_AndTheQueueDrainsInOrder`（两条排队不产生第二条 run；三条 run 严格先后、按发送顺序；`interaction.queue_moved` 指向第二条）；原来靠默认 `queued` 并发起两条 run 的两条测试改为显式 `parallel`（否则后到的一条会排到先到的后面）；Desktop `HomeController.test.ts` +3。

### 8.2 插入

**现状**：写上下文补丁，等安全边界生效（`InteractionsEndpoints.cs:266-297`）。

**改法**：

- **软插入**（保留）：安全边界生效。
- **硬插入**（新增）：取消当前模型调用或当前工具调用后立即生效。参照 Codex：**队列里已有待处理输入时，中断主动让步**；中断要与正在执行的任务一起收尾。
- 补丁的版本校验沿用（`expected_context_revision` → 冲突返回 409）。

**已实现（2026-09-30）**：

- **先修了软插入本身**：插入写的是 run 作用域的 `supplement` 补丁，而上下文只从会话消息构建——插入不追加消息，所以此前**没有任何模型读得到它**（引擎只推进了 context_revision）。现在 `ContextProvider` 把 run 最近的补丁拼成两段证据，排在任务之后、历史之前：`run_steering`（无作者实例 = 用户插入，注明"以它为准"）和 `run_recorded_facts`（worker 的 `CONTEXT_PATCH`，此前同样没人读）。读取走新端口 `IConversationStore.ListRecentContextPatchesAsync`（只取最近 16 条，补丁体不可变、进程内缓存）。
- **硬插入**：`POST …/interactions` 的 `dispatch_mode=insert` 加 `interrupt: true`（其他投递带它 400）。补丁先持久化，再发进程内信号 `IRunInterrupts.Request`（`RunInterruptRegistry`，与在途工具取消注册表同形；请求挂起到引擎"接手"为止，挂起期间新开始的模型调用也会被切断，因为它可能是在补丁之前拼的提示词）。
- **切断什么**：只切模型调用（`ModelInvocationChatFactory` 的 `interrupts` 参数，模型账本记 `cancelled / interrupted`，输出帧记 `model.output.interrupted`，流式回答被撤回）。**正在执行的工具调用不切**——改动型调用会变成 outcome_unknown 把 run 停给人，与"转向"正相反；它跑完后，模型要的、还没开始的调用记 `not_run` 结果（不算错误，循环守卫不计），worker 被告知。运营类智能体（observeOutput: false）不可切。
- **被打断的一轮怎么收尾**：worker 工具循环内——记 `WorkerInterruption`（在第几个工具轮之后、它已说出的半截、用户的话、跳过几个调用）到任务上，下一轮模型调用在原位置看到"[用户打断]"说明 + 新指示，发 `worker.interrupted`。其他步骤（规划、监督、会议回答）——`RunInterruptedException` 结束本趟，发 `run.interrupted`，立即重新入队，从上一个检查点重做这一步（与租约恢复同一条路），新提示词已含插入内容。
- **别的主机**：信号是进程内的；run 在别处跑时它下一次模型调用照样读到插入（退化为软插入），不丢。
- **Desktop**：排队卡片新增"打断并引导"（与"引导"并列）；`api.createInteraction` 带 `interrupt`；新事件名进 `coreEventTypes`，思考步骤把 interrupted 当"结束"而不是"失败"。
- **测试**：`ToolChainEndpointTests.Interrupts.cs`（模型调用被切断后下一轮读到说明与插入；工具调用中插入：该调用跑完、后一个不执行）、`FullDuplexEndpointTests.Steering.cs`（软插入下一次模型调用可见；规划中硬插入重做规划且规划提示词含插入；`interrupt` 只能配 insert）、`RunInterruptsTests`（注册表 + 打断记录在记录里的位置）、Desktop `HomeController.test.ts` +1。

### 8.3 并行

**改法**：在**同一会话**开**一条新 run**，共享同一份上下文；上下文同步靠现有带版本号补丁。**不**改 run 内单写者模型。

**多实例共享上下文的现状**：`FullDuplexCheckpointV1` 有 `ConversationIdentityInstanceId` / `InstancePoolIds` / `MainInstanceId`（`FullDuplexRunEngine.cs:6145-6147`），但**没有任何代码写入**，`RunFreezeGate.ValidatePool` 只在测试里调用。第三期再启用。

**已实现（2026-09-30）**：

- **共享的是什么**：会话上下文就是会话里的补丁账本。worker 记下的事实（`CONTEXT_PATCH`）现在对**会话里所有 run** 可见：`ContextProvider` 的 `run_recorded_facts` 取会话最近 16 条补丁（`ListRecentContextPatchesAsync(sessionId, null, …)`），每条标 `[this run · rev N]` 或 `[run xxxxxxxx · rev N]`，并注明"并行 run 的事实是共享上下文，不是让你重做它的活"。用户插入（`run_steering`）仍只对目标 run。
- **冲突**：写补丁时基于的版本已被别人推进，就记 `stale`、不应用（沿用既有仲裁）——所以并行 run 之间从不互相覆盖，展示出来的每一条都是当时对得上号的。
- **实例池接线**：规划时 `JoinConversationPoolAsync` 一次性写入：本 run 的对话身份实例 = `ConversationIdentityInstanceId` = `MainInstanceId`，`InstancePoolIds` = 它 + 会话里其他未终态 run 的对话身份实例（最多 16 个）；池里不止自己时发 `conversation.pool_joined`。恢复时 `LoadOrCreateCheckpointAsync` 调 `RunFreezeGate.ValidatePool`，形状不对即以其代码失败（fail closed）。池是"规划时谁在旁边"的记录，实时画面看会话拓扑。
- **顺带修掉的并发缺陷**：`LocalFileContentStore` 先查后移，两次并行准入冻结出相同内容时第二次 `File.Move` 抛 `IOException`（准入 500）。内容寻址，同哈希即同内容，输掉竞争等于已写入，现在按成功处理。
- **测试**：`FullDuplexEndpointTests.Parallel.cs`（两条并行 run：B 的事实进入 A 的会议回答并标出 B 与版本号；A 基于旧版本的事实被拒为 stale；两边池与主实例正确）。

---

## 9. 上下文压缩与向量回查（2026-09-29 已实现：证据存档）

**落地**（`AgentGraph/EvidenceArchiveService.cs`，端口 `IEvidenceArchive` 在 `Abstractions/Ports/IEvidenceArchive.cs`）：

1. **原文全留**：表 `agent_graph_evidence`，一行一份证据（`task_result` / `report` / `member_turn` / `summary`），正文原样保存（上限 `TinadecEvidence:MaxContentChars`，默认 64k，超出标注截断）。`(SessionId, SourceKey)` 唯一，重复写入保留原条。写入点：
   - 引擎 `ApplyTaskResultAsync` 的任务终态：状态、任务、简报、执行者、写入范围、结果全文、证据、逐条判据（`TaskEvidenceText`），按尝试次数各留一份（`task:{id}:{attempt}`）；
   - 压缩角色 `DispatchContextCompressionAsync`：摘要本身（上层）与它所摘的下层并列存档（`summary:{patchId}`）；
   - 组织 `org_report`：报告提交后（事务外、尽力而为）存档全文（`report:{messageId}`）；
   - 常驻成员一轮结束后的结论（`member-turn:{turnKey}`；沉默不存）。
2. **向量是派生视图**：`EvidenceIndexService`（后台，批量 16，间隔 10 秒）把待索引的行经 `IVectorStore.IndexAsync` 写进命名空间 `evidence:{sessionId}`（需会话有项目；无项目的行标 `not_applicable`）。没有嵌入模型时整批标 `unavailable`、60 分钟后再试（之后配了模型会补齐存量）；其他失败退避重试，6 次后放弃。
3. **回查工具** `recall_evidence`（Core 虚拟工具，声明在 `OrganizationToolCatalog`，无审批）：派发器（执行者）与成员轮次（常驻治理角色）两处执行，**会话取调用方自己的**，不接受参数指定。关键词半边永远在（`EvidenceTerms`：拉丁词小写、路径与句柄整体、中文按双字切分；SQL 先按词粗筛再在内存计分，窗口 400 行）；语义半边能答就答（`IVectorStore.SearchAsync`），两边按倒数排名融合（k=60），命中标 `semantic|keyword|both`。语义半边**任何**失败都退回关键词并在 `note` 里说明，从不因为没配嵌入而"没结果"。
4. **人也能查**：`GET /api/v1/sessions/{sessionId}/evidence?q=&kinds=&run_id=&limit=`（按调用方租户限定），Gateway 由组织合约一并透传；Desktop 组织面板新增"证据"页。
5. **种子包 2.14.0**：`governance_reviewer` 与 `solo_master` 拿到 `recall_evidence`（Plan 保留，它是读）。

**未做（原计划第 2 条）**：压缩角色仍是一份会话级摘要，没有拆成会话 / run / 任务三层；分层目前靠"摘要 + 可回查的原文"两层实现。

---

## 10. 资源管家

**worktree 管家（2026-09-29 已实现）**：工具仍是 `git_worktree_create` / `git_worktree_remove`（都需审批，`remove` 是仅人工工具；`TinadecTools/Tools/Git/GitWorktreeMutationTools.cs`）。账本侧（`ToolDispatcher.StewardWorktreeAsync`）：

- **精确声明**：`ResourceClaimResolver` 只让这两个变更工具声明 worktree，且声明的就是那个 worktree（`WorktreeTools.TargetOf`：省略 `path` 时按工具自己的规则取 `{repository}/.tinadec/worktrees/{branch-slug}`）；`git_worktree_list` 是读，什么都不声明。之前任何名字含 worktree 的工具都会独占整个仓库根——按包含关系它和仓库里每个 worktree 都冲突。
- **创建即指派**：创建成功后，以工具结果里的 `path` 为准，给**本 run** 写一条 `assignment` 租约（worktree 类、独占、`TaskId` 为空、记 `AgentInstanceId`），所以它比这次调用、这个任务活得长；同 run 的子任务在里面声明写入范围不冲突，别的 run 在里面写会被拒并点名持有者。发 `worktree.assigned`。
- **回收即释放**：删除成功后释放本 run 对该 worktree 的指派（别的 run 持有时删除调用本身在执行前就被拒了），发 `worktree.released {released}`；run 终态兜底释放剩下的。
- **看得见**：拓扑视图（`graph_view`、组织面板"拓扑"页）列出租约、用途与持有者，即"谁在哪个 worktree"。
- **种子包 2.15.0**：`solo_master` 拿到两个 worktree 工具（Plan 关掉），提示词写明"并行改同一仓库就先开 worktree、写进子任务 write_scope、用完回收"。
- **测试**：`ResourceClaimResolverTests` +2、端到端 `WorktreeSteward_AssignsTheCreatedWorktreeToItsRun_AndRemovalReleasesIt`（创建后、删除前观察到 run 持有且别的 run 在其中被拒；删除后释放）。

**环境管家**：**无现状**。最小可用形态：一张 `environments` 表（kind: local/cloud/remote/terminal/test，连接信息，状态）+ 租约复用 `resource_leases(kind=environment)`。分配由管家角色决定，占用由引擎记账。

**已实现（2026-09-30）**：

- **登记**：表 `agent_graph_environments`（工作区内 `key` 唯一；`kind`、显示名、说明、`connection` JSON、`capacity` 1–64、`status` available/disabled）。`connection` 只描述怎么连，**不许带凭据**：任何层级出现 password/secret/token/api_key/private_key/credential/cookie 之类字段即 400 `environment_connection_invalid`，凭据只能以 `secret_ref`（密钥库里的名字）出现。
- **占用 = 资源账本**：一个空位是一条独占 `environment` 租约，键 `<key>#<slot>`，purpose `assignment`，归 run 持有——账本的串行化保证同一空位同一时刻只有一个 run；run 终态自动释放，崩溃的 run 不会扣着环境。停用只拦新分配，不收回已给出的空位（收回是用户的取消）。
- **智能体的动词**（Core 虚拟工具，免审批，只改 Core 状态）：`environment_list`（有什么、怎么连、谁占着哪个空位）、`environment_acquire`（按名字或按类型；按类型取最空的；已持有再取返回同一空位；被拒不是失败——结果写明谁占着）、`environment_release`。run 与会话取自调用范围，不是参数，执行者只能替自己的 run 取还。事件 `environment.assigned` / `environment.released`。
- **接口**：`GET/POST /api/v1/environments`、`PATCH /api/v1/environments/{id}`（`IEnvironmentRegistry`）；Gateway 经组织契约投影原样透传（含 PATCH）。
- **包**：GraphSeedPack 2.16.0——solo_master 拿到三件工具与一句用法说明，Plan 模式关掉 acquire/release（只读）；治理审查只拿 `environment_list`。
- **Desktop**：组织卡片新增"环境"页：列表（类型、空闲/容量、谁占着，本会话标出）、停用/启用、登记表单（连接信息须是 JSON 对象）。
- **测试**：Core `ToolChainEndpointTests.Environments.cs`（solo_master 经真实包 list → acquire → release，期间空位归该 run）、`EnvironmentRegistryTests`（空位单持有、拒绝写明持有者、归还即空、停用不分配、接口登记/重复 409/凭据 400/更新）；Desktop `EnvironmentList.test.ts`、包测试、`toolPresentation` 镜像。

---

## 11. Desktop 呈现

- **三种投递**：`ComposerBar.vue` 已有派发菜单（`ComposerBar.vue:718` 附近），补硬插入与"排队 vs 并行"的明确区分。
- **run 树**：新增会话级第 10 §7.1 的 topology 视图（多实例、各任务、资源占用）。
- **审批门**：审批卡显示"几道门、每道门谁批的、各自理由"。
- **上下文压缩/回查**：显示向量存档条目与回查命中。
- **治理层思维流**：已经能看到 `dispatch`/`wait`/`plan` 步骤（本轮刚接），继续加治理角色唤醒与判断。

---

## 12. 改包纪律（每次改 `GraphSeedPack` 必做）

1. `GraphSeedPack/index.ts` 的 `GRAPH_SEED_PACK_VERSION` + `GRAPH_SEED_PACK_DIGEST`（digest 从测试失败信息里抄实际值）。
2. `apps/desktop/src/agentPacks/GraphSeedPack/GraphSeedPack.test.ts` 的断言。
3. Core 侧假工具清单 `TinadecCore.Api.Tests/ToolChainEndpointTests.CreateManifest`，否则准入报 `spawnable_template_tools_unauthorized`。
4. 新工具必须有 `Description`（生成器 `TTG001` 告警）。
5. 新增 Core 虚拟工具：`CoreVirtualToolPolicy` 加 id + `Is*`，`ToolManifestSnapshotResolver.VirtualEntry` 加分支，`ToolDispatcher` 加拦截/执行，Desktop `lib/toolPresentation.ts` 的 `CORE_VIRTUAL_TOOL_IDS` 加条目。
6. 新增或改名模式：Desktop `lib/modePresentation.ts` 的图标/排序表按 slug 加条目（`modePresentation.test.ts` 双向钉死表与清单一致）；模式描述 ≤ 50 字（选择器显示两行）。
7. 跑 Core 的 `GraphSeedPackClosureTests`（DTO 闭合 + digest 一致）与 `ToolChainEndpointTests`（真实安装整包，新模式在这里过发布闸）。

---

## 13. 测试策略

- **引擎单测**：`TinadecCore.AgentFramework.Tests`——订阅解析、计数上限、派发闸、账本冲突判定（纯函数优先）。
- **端到端**：`TinadecCore.Api.Tests/ToolChainEndpointTests.cs` 的 scripted 夹具（`ToolScriptedClient` 支持 `WhenWorkerTurns` 逐轮脚本），跑"委员长派计划 → 计划派执行 → 冲突 → 审查介入"的整链。
- **注意**：对话身份在会话说出第一句话后锁定。空会话的第一条消息用哪个模式，就采用那个模式的对话身份（`ProjectSessionStore.AdoptConversationIdentityIfEmptyAsync`，2026-09-29）；已有消息的会话跨对话身份切模式会 `conversation_identity_locked_mismatch`（返回给用户的是中文说明，原文在 `diagnostic` 字段）。
- **已知 flake**：`TinaChatTests.AcceptedHandoff…` 在基线 HEAD 就失败；`FullDuplexEndpointTests.RunTerminal_ExecutesQueuedInteraction…` 在全量并发下偶发。
- **写测试的坑**：harness 会把 bash heredoc 里的反斜杠折叠，含 `\n` / `\u` / `\"` 的代码用 Write/Edit 工具写，不要用内联 python。

---

## 14. TinaChat 组织（architecture §9.1，2026-09-29 已实现）

### 14.1 数据模型（实际落地）

在现有 TinaChat 表上加一层"组织"，不另起炉灶。TinaChat 的表由 bootstrap 维护（补表、补列），新列都可空或有默认值，旧库直接升级。

| 表 / 列 | 字段要点 | 说明 |
|---|---|---|
| `tina_chat_organizations`（新） | `SessionId`（每租户唯一）、`Status`（active / archived）、`HostParticipantId`、`HumanParticipantId`、`LobbyConversationId`、`BoardConversationId`、`TurnWindowStartedAt`/`TurnsInWindow` | 一会话一组织；会话归档或进回收站时组织只读，恢复即可写 |
| `tina_chat_participants`（扩列） | `OrganizationId`、`MemberKey`（`user` / `host` / `conversation` / `governance:{slug}` / `instance:{id}`，与组织一起唯一）、`OrgRole`、`AgentSlug`、`ParentParticipantId`（派发者）、`Presence`、`CurrentRunId`、`TurnWindowStartedAt`/`TurnsInWindow` | 组织成员是会话级身份，不进工作区通讯录（`DiscoverAsync` 过滤掉）；显示名是 run 句柄（`search#1`），模型按显示名称呼 |
| `tina_chat_instance_bindings`（新） | `InstanceId`（主键）→ `ParticipantId`、`RunId`、`TaskId` | 实例以哪个成员说话；solo 主人的每个任务实例都绑到同一个对话成员 |
| `tina_chat_contacts`（新） | `(OwnerId, ContactId)`、`Status`（requested / active / declined） | 只存经同意的额外联系人；默认联系人按图现算，不落行 |
| `tina_chat_reports`（新） | `MessageId`（主键，指向报告帖）、`ReportKind`、`Severity`、`Status`（open / acted / dismissed / superseded）、`SubjectKind` + `SubjectId`、`ProposedVerb` + `ProposedArgs`、`Finding`、`EvidenceJson`、`SupersedesMessageId`、决定人/说明/时间、`Revision` | 帖子承载可读正文与冻结受众，报告行承载可筛选、可决定的结构 |
| `tina_chat_conversations`（扩列） | `OrganizationId`、`PlanOwnerId`；`Kind` 新增 `lobby` / `board` / `plan` / `adhoc` | 组织的房间都由 host 创建（`ClientRequestId` 按组织确定），所以同一对成员只有一个私聊 |
| `tina_chat_wakes`（扩列） | `DueAtUnixMs` | 到期判定走 `(Status, DueAtUnixMs)` 索引：SQLite 不能在服务端比较 DateTimeOffset，逐行扫描撑不住几百个被推迟的成员 |

### 14.2 默认通讯录与可见性

- **联系人**（`AreContactsAsync`）：派发者 ↔ 被派者、同一派发者下的兄弟、治理层 / 对话身份 / 用户 ↔ 所有人，按图现算；其余要 `org_contact action=request`，对方 `accept` 后才能私聊。向已离线的执行者发私信会被拒（它不会再读）。
- **房间**：大厅与公告板的成员只有 user、host、对话身份、治理层；执行者进派发者的计划室（每间 ≤ 64 人，满了自动开下一间）；`plan` 指"你被派进去的那间"（没人派你时才是你自己的那间）。
- **读**：房间成员读自己的房间；所有人读公告板；看全局的角色（user、对话身份、治理层）读组织里所有房间与私聊。机密消息仍然只给受众里的人。
- **写**：只能在自己是成员的房间发；公告板只有看全局的角色能发。会议室帖只冻结"发送者 + 被提及者"的受众（提及者进收件箱、治理成员被唤醒），其余人按游标读——一帖 O(提及数) 行，而不是 O(成员数)。

### 14.3 唤醒泛化（常驻成员）

- **谁被唤醒**：解释者（起草简报，旧行为）与组织里的**治理成员**。治理成员只因"递给它的东西"被唤醒：host 的通知、私信、提及。房间里的普通发言不唤醒任何人。
- **通知**：引擎与工具层的事实按冻结配置里的**声明订阅者**（关系文件 `subscriptions`，排除内建四角色与对话身份）投进该成员与 host 的提醒私聊；同一主题 + run + 30 秒只发一条。
- **一轮**：`TinaChatMemberTurnRunner`（DmaEA）读引发事实的那个 run 的冻结配置，取该角色的提示词、模型计划与声明工具（与组织工具集取交集，外加 `graph_view`），跑 ≤ 6 轮；模型调用记在那个 run 上；结束后在那个 run 上记 `governance.member_turn`。它不在任何执行者的 run 里，执行者不等它。
- **预算**：每成员每小时 12 次、每组织每小时 60 次（`TinadecTinaChat:MemberTurnsPerHour` / `OrganizationTurnsPerHour`），固定一小时窗口；超了把唤醒推迟到窗口结束，来源消息保留、合并，不丢。检查与扣减和取走来源在同一个 Serializable 写里，两个宿主不会花掉同一个名额。
- **排空**：一次排空里的多个唤醒并行执行（`WakeParallelism`，默认 4）。

### 14.4 模型侧工具

`org_directory`（成员、可读房间、联系人申请）、`org_read`（房间 / 大厅 / 公告板 / 计划室 / 收件箱 / 私聊，按游标、按类型）、`org_send`（房间或私聊，可提及）、`org_report`（报告）、`org_decide_report`（带版本号，只有用户、对话身份与作者能决定）、`org_contact`、`org_room`，以及 `graph_view`。声明表只有一份（`Abstractions/Ports/OrganizationToolCatalog.cs`），工具清单、常驻成员的工具面与 `graph_view` 共用。

### 14.5 对话身份读报告

协调者模式（Team / Review / Spec / Graph / Workflow）里对话身份不能持工具——持工具即派生成 solo 档位。所以未处理的 warning / blocking 报告以 `[organization_reports]` 证据进它的上下文，规划与最终作答都能看到；solo 主人另有 `graph_view` / `org_read` / `org_decide_report` 工具。

---

## 15. 派发时的写入范围（architecture §7.4）

1. `task_dispatch` 与规划任务新增可选 `write_scope`：路径前缀列表或一个 worktree。缺省 = 该执行者的资源包络（与今天一致），不改变旧包行为。
2. 派发即按范围取租约（独占）；范围重叠（相同或上下级目录）即拒绝，返回可纠正错误，列出当前持有者与其任务，让派发者改派到新 worktree 或改为依赖。
3. 执行期的写工具仍受资源包络约束；`write_scope` 是**额外**的收窄，不放宽任何东西。
4. **事后核对**：任务开始与结束时用独立 git 索引（`GIT_INDEX_FILE` 指向临时文件、对象写入临时库）拍快照并比较，不碰真实仓库的索引；改动超出 `write_scope` 时，给审查智能体发 `lease_conflict` 类提醒，并在任务收尾证据里写明。
