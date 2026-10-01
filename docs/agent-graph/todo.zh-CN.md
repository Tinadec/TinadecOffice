# 双层智能体架构：施工清单

> 方向见 `architecture.zh-CN.md`，细节见 `implementation.zh-CN.md`。
> 状态标记：`[ ]` 未开始 · `[~]` 进行中 · `[x]` 完成 · `[!]` 被阻塞。
> 每项必须写清楚：做什么 / 为什么 / 碰哪些文件 / 验收标准。**没写验收标准的不开工。**
> 2026-10-01 亲自复查：以下若干完成标记已重新打开。阶段测试通过仅说明当时的切片可用，不代表目标架构整体闭环。问题、复现与验收见 [复查报告](review-2026-10-01.zh-CN.md)。

---

## 第一期：地基（先做，两个并行推进）

第一期的全部工作都是"让后面能长出来"的地基。**P1-A 与 P1-B 互不依赖，同时做。**

### P1-A 资源账本（Resource Ledger）

- [x] **A1. 建表与迁移**
  - 做什么：新增 `resource_leases` 表（字段见实现文档 §2.1），SQLite 与 PostgreSQL 同批。
  - 为什么：冲突治理、worktree 管家、环境管家**全部**依赖"谁正在用什么"这一份事实。没有它，审查智能体看到的是一片黑。
  - 文件：`TinadecCore/Storage.Migrations.Sqlite/`、`TinadecCore/Storage.Migrations.PostgreSql/`、新 DbContext。
  - 验收：`dotnet test TinadecCore/tests/TinadecCore.AgentFramework.Tests` 通过；两库各有一条 schema 对账测试（参照现有 `DmaeaGraphSchemaReconciliationTests`）。

- [x] **A2. 租约服务（纯函数部分先行）**
  - 做什么：`IResourceLeaseService`：`AcquireAsync` / `ReleaseAsync` / `FindConflictsAsync`；冲突判定写成**纯函数**（同 key + exclusive + active + 不同 RunId → 冲突）并先写单测。
  - 为什么：纯函数能直接单测，负例（共享读不冲突、同 run 不冲突、已释放不冲突）比集成测试可靠得多。
  - 验收：冲突判定单测覆盖 4 个正例 + 4 个负例；`tinadec` 全量 `AgentFramework.Tests` 通过。

- [x] **A3. 接到工具执行前後**（2026-09-29 按 architecture §7.4 收窄）
  - 做什么：写文件类工具执行前按路径取租约作兜底；**`shell` 不再取工作区租约**；任务/ run 终态批量释放。
  - 为什么：账本要真，就不能靠人手工记；但按命令锁 shell 只会把并行变串行，三家同类产品都不这么做。
  - 文件：`TinadecCore/DmaEA/FullDuplexRunEngine.cs`（工具循环）、`TinadecCore/Tools/ToolDispatcher.cs`、`Abstractions/Ports/ResourceClaimResolver.cs`。
  - 验收：跑一个写文件的 run，结束后 `resource_leases` 里该 run 的行全部 `released`；同一路径两个 run 并发时第二条被拦并留下记录；两个 run 在同一工作区跑 shell 互不阻塞。

- [x] **A5. 账本的一致性修复**（第一批自查出的 bug）
  - 做什么：冲突查询覆盖大小写不同的写法与上级目录；并发取同一资源靠唯一约束裁决（补索引）；删除或实现"租约会过期"。
  - 为什么：今天 SQLite 默认区分大小写、只按精确键查；两个 run 同时取同一路径可能都成功；注释写"由唯一索引裁决"但索引不存在。
  - 验收：大小写不同与上级目录两种写法各有一条冲突测试；两个并发取同一路径只有一个成功。

- [x] **A6. 派发时的写入范围**（implementation §15；跨 run 与同 run 不同任务均按任务所有权检测重叠，复查 F4 已修）
  - 做什么：`task_dispatch` 与规划任务新增可选 `write_scope`；派发即取独占租约，重叠即拒并列出持有者；任务结束用独立 git 索引做事后核对。
  - 为什么：让冲突在任何命令执行之前可见；Codex 把"写入集不相交"写在提示词里，我们把它变成能检查的规则。
  - 验收：两个写入范围重叠的派发，第二个被拒且错误文案给出持有者；缺省不写 `write_scope` 时行为与今天一致；超出范围的改动在收尾证据里被标出。

- [x] **A4. 冲突事件**
  - 做什么：冲突时发 `lease.conflict` 事件（含两边 run / 实例 / 路径）。
  - 为什么：这是审查智能体与冲突治理的**输入**；没有事件，订阅式唤醒就无从触发。
  - 验收：事件可被 `ReplayEventsAsync` 读回；字段足够定位两边当事人。

### P1-B 订阅式唤醒（替换写死的角色名 switch）

- [!] **B1. 建 `governance_wakes` 表**（2026-09-29 被取代：改用 TinaChat 的 `ChatWake`，见 implementation §2.2；待删除 `agent_graph_wakes` 及配套服务）
  - 做什么：镜像 `ChatWake`（`TinadecCore/TinaChat/TinaChatDbContext.cs:240`）建表：status / attempts / available_at / CAS 抢单。
  - 为什么：唤醒需要"至少一次、可重试、可关闭"的队列语义，聊天唤醒已经验证过这套形状，直接复用。
  - 验收：两库迁移 + 抢单 CAS 单测（两个并发抢单只有一个成功）。

- [x] **B1'. 删除自建唤醒队列**
  - 做什么：删除 `agent_graph_wakes` 表、`GovernanceWakeQueue` / `GovernanceWakeService` / `GovernanceWakeStore` / `GovernanceWakeOptions` 及注册；主题常量保留给订阅解析用。
  - 为什么：治理角色是组织成员，提醒就是投进收件箱的消息；两套队列等于两套系统。
  - 验收：`AgentFramework` 与 `Api` 全绿；`FullCompositionRegistersAllModules` 的断言随之调整。

- [x] **B2. 主题表与订阅解析**
  - 做什么：定义固定的主题枚举（实现文档 §3.1）；在 `Runtime/FormalModeResolver.cs` 读关系文件时收集 `subscriptions`（可选字段），冻结进 `FrozenRunConfigurationV1`（可选，缺席不改字节）。
  - 为什么：主题是引擎的词汇表，必须少而固定；订阅才是自由的。
  - 验收：带 `subscriptions` 的包解析后能列出订阅者；**不带该字段的旧包解析结果与今天完全一致**（回归测试钉死）。
  - 注意：新增字段必须 `JsonIgnore(WhenWritingNull)`，否则旧包 digest 漂移。

- [!] **B3. `GovernanceWakeService`**（2026-09-29 被取代：排空由现有 `TinaChatWakeService` 负责，见 O5）
  - 做什么：`BackgroundService`，镜像 `TinadecCore/Runtime/TinaChatWakeService.cs`：定时扫 + CAS + 失败重试 + 开关（`WakeDrainEnabled` 同款）。
  - 为什么：需要一个统一的调度器，把"事实 → 唤醒谁"跑起来。
  - 验收：注入一个假订阅者，触发一次主题，订阅者被唤醒一次；关掉开关后队列不丢。

- [x] **B4. 替换 `OperationalTriggers`**（旧四角色的同步路径；新治理角色经组织收件箱唤醒，见 O5）
  - 做什么：`TinadecCore/DmaEA/Operations/OperationalTriggers.cs` 的角色名 `switch` 改为"按订阅匹配"；无 `subscriptions` 的旧包**按今天的四个角色名推导等价订阅**。
  - 为什么：这是"角色由描述文件激活"的核心一刀。改完，新增一个治理角色不需要动引擎代码。
  - 验收：现有四个运维角色的行为在旧包下**逐一回归不变**；新声明的角色能被唤醒（新测试）。
  - 风险：这一刀会动到既有触发语义，必须先把 B2 的兼容回归跑绿。

### P1-C 描述文件的派发闸（现成字段，当前是空的）

- [x] **C1. `allowed_dispatch_targets` 生效（三个校验点）**（调用者实例与动态模板均冻结目标包络，task_dispatch 调用期按当前实例收窄，复查 F2 已修）
  - 做什么：规划期（`FullDuplexRunEngine.ValidateAndMaterializeGraph`，`FullDuplexRunEngine.cs:1240`）、调用期（`Tools/ToolDispatcher.cs:764` 的 `ExecuteTaskDispatchToolAsync`）、选人期（`ResolveRequestedWorker`）三处按该字段校验。
  - 为什么：**今天我 grep 过全 Core，这个字段没有任何消费者**——文档里写"我允许派给谁"完全没人看。这直接违背"拓扑自由"。
  - 验收：三种负例（规划期写了不许派的目标 / 调用期点名不许派的目标 / 越级派发）各自被拒且错误文案列出合法目标；缺省不写该字段时行为与今天一致。

- [x] **C2. 深度与预算对引擎路径生效**
  - 做什么：`CreateRootAsync` 也做深度与实例数检查；`RuntimeAgentSeed` 传递 `ParentInstanceId` / `GenerationDepth`。
  - 为什么：今天深度只在 `AgentInstanceService.cs:217` 的 `SpawnAsync` 生效，而引擎用 `CreateRootAsync` 创建 worker（`FullDuplexRunEngine.cs:2206` 等），**绕过了检查**。上限不生效，"不能太深"就只是一句愿望。
  - 验收：构造超过 `SpawnPolicy.MaxDepth` 的派发链，第 N+1 层被拒并留下事件；实例数超 `MaxAgentsPerRun` 同样被拒。

---

## 第二期：组织与治理角色

### P2-O TinaChat 组织（architecture §9，implementation §14）

治理角色要成为常驻节点，先得有"组织成员 + 收件箱"。这一组排在治理角色之前。

- [x] **O1. 组织与会话绑定**：`ChatOrganization`（一会话一组织）；包里的角色实例化时自动成为组织成员（参与者带 `AgentInstanceId` 与句柄）；用户本人以成员身份加入。验收：新会话自动有组织；派出的 `search#1` 在组织成员里可见，结束后显示离线且历史可追溯。
- [x] **O2. 通讯录**：`ChatContact` + 按图推导的默认边 + 运行时加联系人（策略写在模式里）。验收：兄弟节点默认互为联系人；非联系人私聊被拒且提示如何申请。
- [x] **O3. 会议室类型与公告板**：放开 `lobby` / `plan` / `adhoc` / `board`；计划室随派发自动建；公告板只有治理层与用户可发。验收：派出计划智能体时自动出现计划室；执行者在公告板发帖被拒。
- [x] **O4. 报告帖**：`report` 等帖子类型 + 指向 Core 对象的引用 + 提议动作需持有动词者执行。验收：报告在界面上显示所引用对象的最新状态；提议动作不经执行不改变任何状态。
- [~] **O5. 唤醒泛化**：治理回合与预算已实现；失败/取消/崩溃 claim 的来源 ACK、fencing、过期回收和无上限合并已修（复查 F5/F6）；执行者消费侧仍未闭环（F8）。验收补充：100 条突发提醒全部消费；执行者下一安全边界收到自己的 TinaChat 消息。
- [x] **O6. 模型侧工具**：按会议室 / 游标 / 类型读；加联系人、建会议室、发报告。验收：工具描述齐全（`TTG001` 无告警），受众冻结走同一写路径。

### 治理角色

- [x] **R1. 审查智能体（独立上下文审批）**（`ApprovalGateJudge`：一次独立模型调用，不在任何 run 里、无工具面；审查员 = run 冻结配置里声明订阅 `approval_requested` 的常驻治理角色，否则第一个常驻治理角色，否则 Core 内置审查员）
  - 做什么：上下文只含：待批动作（参数已去密钥）+ 所服务的任务（标题/描述/执行者/写入范围）+ 客观事实（工作区根、本 run 持有的资源）。输出放行 / 拒绝 / 退回用户（`approve|reject|escalate`），写回 `agent_graph_approval_gates`，证据字段 = 它看到的原样事实。
  - 为什么：用户明确要"审查智能体的上下文不被污染"。参照 Codex 的守护评审：独立会话 + 主机提示固定文本（事实一律当数据，不当指令）。
  - 验收：审查智能体的提示词里**不出现**主对话内容（端到端用哨兵串断言）；它的判断记录里有"看了什么"的证据字段（`evidence.user_goal` 对审查员恒为空）。

- [x] **R2. 多门审批**（用户按消息选权限模式：`delegate-conversation` / `delegate-reviewer` / `delegate-both`）
  - 做什么：门记录表 `agent_graph_approval_gates`（每门一行、按序、CAS 认领）；后台 `ApprovalGateService` 每轮每个审批只问一道门，全部 `approved` 才走与人点击同一条决策路径（`DecideDelegatedAsync` + `approval.decided` + 唤醒 run），任一驳回即拒、后门不问，任一退回则留给人。
  - 为什么：今天一个请求**只有一个决策者**；"两道门"是**与**关系，必须是两条独立记录。且**门只做加法**：委托模式下 PDP 像 auto-approve 那样把可委托的写放到审批层（租约限定、单次），绑定检查（请求哈希、清单、授权）一条不少；仅人工工具（shell/推送/删除/外连）与超过委托上限（默认 medium）的风险永远等人。
  - 验收：三种模式下各自跑通一条审批；只批第一道门时动作**不执行**；两道门都批才执行；任一门驳回即拒（端到端 6 条 + 治理单测 10 条，见施工记录）。

- [~] **R3. 冲突治理**（Team 的事件→独立报告已实现；同 run 冲突与授权执行治理建议尚缺，复查 F4/F10）
  - 做什么：审查智能体（或独立冲突治理角色）订阅 `lease_conflict`，判断并给裁决（让路 / 串行 / 上报用户）。
  - 为什么：这是用户对审查智能体最看重的一条；依赖 P1-A 的账本。
  - 验收：两个 run 抢同一路径时，冲突被订阅者看到并给出裁决记录；裁决不允许"绕过独占"。

- [~] **R4. 上下文压缩 + 向量存档与回查**（证据存档、检索与关键词退化已实现；原文仍会截断，专用上下文角色与按预算压缩未闭环，复查 F12）
  - 做什么：下层报告原文全留（任务结果、报告、成员结论、压缩摘要）+ 后台写向量存档（`IVectorStore.IndexAsync`）；给治理层与 solo 主人 `recall_evidence` 回查工具（向量 + 关键词倒数排名融合）；会话主人也能在组织面板"证据"页检索。
  - 为什么：向量库已建好但**生产代码零调用方**；长期记忆还在用关键词打分。回查能力是"几十份报告"能被人和 AI 消化的前提。
  - 验收：存档后可回查命中；**未配置嵌入时自动退化到关键词**（不许变成"没结果"）——`EvidenceArchiveTests` 三条（无模型退化关键词仍命中、有模型语义与关键词融合且语义独有命中可见、模型不可用时排队待补且回查仍是关键词）+ Team 端到端里审查员回查到任务原话。

- [~] **R5. worktree 管家**（创建/删除记账已实现；指派执行者、工作区 rebinding、合并验收未闭环，复查 F11）
  - 做什么：创建/回收时写资源租约、指派到 run（记创建者实例）、回收释放；界面可看"谁在哪个 worktree"（拓扑的租约与持有者）。
  - 为什么：工具已有（`git_worktree_create` / `git_worktree_remove`，都需审批），缺的是账本与指派。
  - 验收：同一 worktree 不会被两个实例同时独占（别的 run 在其中声明即被拒并点名持有者）；回收后租约 `released`（端到端 `WorktreeSteward_…` 断言 `worktree.released {released:1}` 且无残留指派）。

---

## 第三期：并行与投递

- [x] **D1. 排队语义分明**（`SESSION_BUSY` 准入 + 单主人队列 + 队头放行、其余挪到下一条 run 后面；Desktop 排队卡片接 Core 队列）
  - 做什么：`dispatch_mode=queued` 时**不并发**——会话已有活跃 run 就一律进队列（复用 directive + `interaction.queued_deferred` 路径，`FullDuplexRunEngine.Lanes.cs:1061`）。
  - 为什么：今天活跃 run 上限默认 2，未满时"排队"与"并行"走同一条路（`InteractionsEndpoints.cs:324`、`FullDuplexRunCoordinator.cs:184`），语义是混的。
  - 验收：会话有活跃 run 时再发一条 `queued`，**不产生第二条 run**；当前 run 终态后队列自动放行。

- [x] **D2. 硬插入**（`interrupt: true`：切断在途模型调用并重做、未开始的工具调用跳过、正在跑的工具调用跑完；被打断的一轮记在任务上给 worker 看；顺带修了"插入写了补丁却没有任何模型读得到"）
  - 做什么：在软插入（安全边界生效）之外，新增硬插入：取消当前模型调用或工具调用后立即生效。参照 Codex：**队列里已有待处理输入时中断主动让步**；中断与正在执行的任务一起收尾。
  - 为什么：用户明确要"像 Codex 那样终止当前消息发送，直接插入"。
  - 验收：硬插入后新消息在下一轮模型调用里可见；被打断的那一轮有可读的收尾记录（模型知道被打断过）。

- [~] **D3. 并行 = 同上下文多实例**（会话事实共享与实例池已实现；同一 run 的独立工具任务已用私有 checkpoint 快照并发推进，执行子 run 与共享计划/决定尚未交付，复查 F3/F10）
  - 做什么：会话级"并行"开一条新 run 并共享上下文；启用 checkpoint 里**已存在但从未写入**的 `ConversationIdentityInstanceId` / `InstancePoolIds` / `MainInstanceId`（`FullDuplexRunEngine.cs:6145-6147`，`RunFreezeGate.ValidatePool` 目前只在测试里被调用）。
  - 为什么：这是本架构的差异点；地基（字段、校验）已经在，只是没接线。
  - 验收：并行开两条 run，共享同一份上下文版本；一条改上下文另一条能看到（带版本号）；冲突走拒绝/合并而不是覆盖。

- [~] **D4. run 图呈现**（已有 run/任务/实例/租约列表；未建父子 run 关系，也未画可见/通讯/治理/依赖等图边，复查 F3/F12）
  - 做什么：`GET /api/v1/sessions/{id}/topology` 返回 run 树 + 各自状态 + 资源占用；Desktop 出视图。
  - 为什么：图工程思维需要看得见；这也是图模式/空间模式的底图。
  - 验收：父 run 与子 run 的关系、状态、租约在界面可读。

---

## 第四期：进阶

- [~] **E1. 环境管家**（登记、空位记账、工具与界面已实现；实际执行目标未绑定环境，复查 F11）
- [!] **E2 / E3**（公告板、公共讨论室、智能体之间的自由对话）已并入第二期 P2-O（O3 / O5）。
- [x] **E6. 对话身份合一**（2026-09-30 第十四批）：种子包 3.0.0 七模式全部由 `meeting` 对话（其定义携带全部工具作天花板，不需要工具的模式在绑定上全关；档位从有效工具推导）。`solo_master` 模板保留发布供存量会话恢复，无模式再引用。准入路径：会话身份与所选模式身份不一致且无活跃 run 时一次性迁移（`ProjectSessionStore.MigrateConversationIdentityAsync`，与 `MigrateSessionAsync` 同一把会话锁），有活跃 run 维持 `conversation_identity_locked_mismatch` 拒绝。验收：同一会话 Solo↔Team 互切不触碰身份；旧身份空闲迁移、忙碌拒绝各一条端到端。
- [x] **E7. 审批规则（前缀放行 + 按会话 shell 委托）**（architecture §7.4 第 6、7 条，2026-09-30 第十二批）：表 `agent_graph_approval_rules`；`IApprovalRules` + REST；PDP 在 ask 族的最后一环（`approval_rule_released`）；计用只在铸刻消费时一次；`delegate_tool` 勾选把 shell 交给门。**shell 进沙箱（第 5 条）拆为独立项**：本机 `TinadecSandbox` 账户未初始化、初始化要 UAC，无法在此实机验证；路线已定（runner 协议补输出流 + 一次性调用走沙箱、long_lived 例外），需一次有管理员权限的实机验证后落地。另：每个 shell 批准时"总是允许此前缀"的界面勾选未做（REST 已可用）。
- [~] **E4. 项目级委员长**（2026-09-30 第一刀：**先可见、后授权**）：`GET /api/v1/projects/{id}/overview`（项目自己的会话、未终态 run、未处理报告、被占用资源的汇总读模型；含驻留 run，外会话泄漏为零）+ Gateway 透传。**后续项**（设计决定，不在本批）：项目级治理角色本体、跨会话写权限、桌面端页面；按用户此前的拍板，委员长现在以会话为单位，项目级做成单独功能。
- [x] **E5. 治理层可见性的按身份配置**（开关、通知和 run-scoped topology 的租约/执行成员收窄已实现，复查 F7 已修）：`ChatParticipant.VisibilityScope`（null=全通/"own"）；用户经 `PATCH …/organization/members/{participantId}` 设置，Desktop 有切换。

## 2026-10-01 复查后新增工作

- [x] **N1. 冻结并传递动态模板职责**（F1）：`FormalModeResolver` → `DeclaredSpawnableTemplate` → `FrozenSpawnableTemplate` → worker 已保存系统提示词和目标包络；选人回归验证提示词到达 worker definition。
- [x] **N2. 可靠唤醒消费与背压**（F5/F6）：待处理/领取中输入分离，成功 ACK，claim token + 期限回收，来源不再静默截断；新增模型失败和过期 claim 回归。执行者消费侧仍由 N5 覆盖。
- [x] **N3. 调用者派发闸与完整可见性**（F2/F7）：调用者 instance 的冻结目标包络接入 task_dispatch，run-scoped topology 收窄租约和执行成员；recall 复用同一 run 过滤。
- [~] **N4. 执行并发容器与资源所有权**（F3/F4）：同 run 独立工具任务已用私有 checkpoint 快照并发推进，任务级写冲突已拒绝；执行子 run、显式父子资源授权、完整审批/取消/恢复隔离仍待做。
- [~] **N5. 执行者通讯与治理动作**（F8/F10）：执行者被点名后已从 TinaChat durable wake 进入其 run-scoped context patch，并重新排入原 run；治理报告现在可通过统一动作入口执行 pause/resume/stop run，并在报告 CAS 成功后记录审计。成员独立持久状态、改派、串行化、worktree/环境指派和实际执行者回复闭环仍待做。
- [~] **N6. 规模、公平性与用量**（F9/F10/F12）：审批候选已按稳定游标跨窗口分页，前部终止门不会挡住后续；拓扑/组织/项目续页、成员/根 run/组织用量和数百成员压测仍待做。
- [~] **N7. shell 沙箱与资源到执行的绑定**（F11，原 E7 未独立列出的部分）：一次性 shell 已接平台沙箱入口，未实现流式沙箱时长驻终端显式拒绝；worktree/环境到实际 provider 的绑定和 Desktop 前缀规则入口仍待做。验收：假平台后端端到端 + 支持平台实机测试；不支持平台行为显式；远程/本地环境执行证据能定位真实目标。
- [ ] **N8. 全文与上下文角色闭环**（F12）：保留全文 content reference，摘要与原文分开，专用压缩角色按预算触发。验收：超长报告可回查完整原文；对话与下层报告按权限压缩；配置嵌入/模型故障不影响全文可读。
- [ ] **N9. 外部验收与遗留问题**：真实模型、PostgreSQL、跨平台/多宿主、真实 Electron 路径；本轮 Core API 全量失败的排队归属和 run-scoped 审批例先取证，再判定原因。早期剩余 i18n/CSS 清理保持独立项，不把它们混入图工程完成统计。

---

## 每项的收尾动作（不许跳过）

1. 跑受影响测试：`AgentFramework` → `Api` → Desktop（`npx vitest run`）→ 类型检查。
2. 同步文档：本清单勾选 + `implementation.zh-CN.md` 如与实现不符则改它。
3. 改到 pack 或工具面时，按实现文档 §12 的**五步改包纪律**走完。
4. 在 `TinadecCore/AGENTS.md` / `apps/desktop/AGENTS.md` 记一条（改了什么、为什么、测试结果）。
5. **按逻辑批次提交**：用户已明确授权每个阶段留提交；提交前记录验证结果，产品修复与文档同步放在同一工作项。

---

## 已知坑（施工时别踩）

- 写测试：harness 会折叠 heredoc 里的反斜杠，含 `\n`/`\u`/`\"` 的代码用 Write/Edit 工具写。
- 对话身份自 E6 只有一个（`meeting`）：模式互切不再触碰身份。仅存量 `solo_master` 会话会在准入时迁移——必须是无消息（采用首句身份）或无活跃 run（一次性迁移），否则 `conversation_identity_locked_mismatch`。
- 旧包兼容：任何新增字段必须可选，否则 digest 漂移 → 409 不可变纪律。
- 既有失败（非本次引入）：`TinaChatTests.AcceptedHandoff…`；`FullDuplexEndpointTests.RunTerminal_ExecutesQueuedInteraction…` 与 `FullDuplexEndpointTests.InvokeStream_TransientModelOutage…` 在全量并发下偶发（后者依赖重试退避计时，单独跑通过）。
- PowerShell：用 `scripts/setup-dotnet-env.ps1`，`--verbosity` 而不是 `-v`。


---

## 施工记录（按时间倒序，每条写清证据）

### 2026-09-30 第十五批（E4 第一刀）：项目级指挥台读模型

- **先可见、后授权**：`GET /api/v1/projects/{projectId}/overview` 汇总项目的会话数、全部未终态 run（含等待决定的驻留 run）、未处理报告（50 上限并报告截断）、被占用资源（按会话归属过滤，外会话为 0）；没有新写权限——项目级委员长的写面是独立设计决定，按用户此前的拍板（委员长以会话为单位，项目级做成单独功能）留下后续。
- **测试**：`ProjectOverviewTests` 2/2（聚合 + 他会话租约不漏 + 未知项目 404）；OpenAPI 快照再生；Gateway 透传并 75/75。

### 2026-09-30 第十四批（E6）：对话身份合一

- **包 3.0.0**：`meeting` 定义携带 solo_master 的 37 件工具为天花板；Solo/Plan 的对话节点与绑定改指 `agent:meeting`；Team/Review/Spec/Graph/Workflow 的对话绑定把 37 件工具逐个关掉（档位由有效工具推导，不从 slug）；`solo_master` 模板保留发布（描述注明存量会话用）。
- **Core 准入**：所选模式身份与会话身份不一致时——空会话照旧采用首句身份；有历史且无活跃 run 则一次性迁移（`MigrateConversationIdentityAsync`，与 `MigrateSessionAsync` 同一把会话锁）；有活跃 run 维持 409（名册按旧身份冻结）。拒绝文案照旧中文可执行说明。
- **测试坑（自食其果过一次）**：要构造"旧身份 + 活跃 run"，必须先让 run 在新身份下驻留，再改会话 slug——先改 slug 的话，启动那条 run 的交互自己就会触发空闲迁移。
- **测试**：`ModesShareOneConversationIdentity_SwitchingNeverTouchesIt`（Team↔Solo 互切不动身份）、`LegacyIdentity_MigratesWhileIdle_AndRefusesOnlyWhileARunIsActive`（空闲迁移 + 忙碌 409 中文说明）、Solo/Plan/Review/Spec 端到端断言改指 `meeting` 全过；AgentFramework 421/421；包测试 9/9（digest `97cb5e57…91aa1`）。
- **诚实边界**：桌面模式切换界面无改动（不需要）；组织成员里旧会话的 conversation 成员 slug 不随迁移改名（只是显示）。全量 Core Api 本轮未跑，最终一轮见下。

### 2026-09-30 第十三批（E5）：治理层可见性按身份配置

- **语义**：`down`（默认，治理层/对话身份看全局）与 `own`（只见自己的 run）。对**常驻治理成员**，`CurrentRunId` 只是上一条事实的 run，没有"自己的 run"可言——设为 `own` = 完全静音（不唤醒、不排队，muted 通知也不改写 `CurrentRunId`）；对**执行者**，收窄为它的 run。这是初实现时差点踩进去的坑：通知自己改写 `CurrentRunId`，用"`CurrentRunId != 事实 run` 过滤"是循环的。
- **执行点**：`NotifyAsync`（静音）；`ExecuteGraphViewAsync`/`ExecuteRecallEvidenceAsync`（run 内调用，强制 run 过滤或空结果+说明）；`TinaChatMemberTurnRunner` 的 graph/recall（同样需要 know-member）。房间消息不受影响（成员资格本来管着）。
- **用户面**：`PATCH /api/v1/sessions/{id}/organization/members/{participantId}`（owner/host 不可改，"down" 存 null 保持"默认即无设置"）；Gateway 组织合约投影自动带上（路径数 9→10，测试钉子同步）；Desktop 组织面板成员行对治理/对话/执行者显示切换，恢复默认发 null。
- **测试**：`OrganizationTests` +1（受限→静音→恢复全程 + 执行者收窄 + owner/host 拒改）；Core Api 回归（ToolChain/Org/Rules）在收尾验证里跑；Desktop `OrganizationPanel.test.ts` +1（治理行有开关、人类行没有、发 null 恢复）31/31，`vue-tsc` 0 错；Gateway 75/75；契约三件套再生零漂移。
- **诚实边界**：受限成员在房间里的消息仍然看得到（设计如此，只限 run 内部数据）；`graph_view` 的收窄是 run 级，没有任务级。

### 2026-09-30 第十二批（E7）：审批规则最后一环 + 提交整理

- **E7 收尾**：补了 ask 族的最后一环——此前前缀规则只在到达审批层后生效（auto/full-access/委托），默认 ask 模式的 shell 仍停在 PDP 等人点。现在 dispatcher 授权前按冻结参数匹配规则并把 `CommandRuleId` 带给 PDP（`ToolAuthorizationCommand`/`PermissionRequestCommand` 尾部可选字段），PDP 复核（`VerifyCommandRuleAsync`：范围按 run 自己的会话、类型、工具，不信调用方）后以 `approval_rule_released` 放行，决策文案如实写"人写规则时给过的批准"。审批层仍先 `HonorCommandPrefixAsync` → `MintApprovalFromRuleAsync`，审计 source=`approval_rule:{id}`。
- **计用语义**：匹配与复核都不计用（一次调用会经过多处匹配），只在铸刻消费时记一次（`IApprovalRules.RecordUseAsync`，铸刻成功后 best-effort）。
- **2026-10-02 复查纠正**：当时解除 `delegate_tool` 的 shell/command_run 限制是引入缺陷，并非修复。现恢复具体会话的命令工具准入，PDP/门服务统一重查风险上限，旧宽泛规则不能放行。重复检测（409）、null 会话只见工作区级前缀规则、消费时单次计用仍保留。
- **测试**：`ToolChainEndpointTests.ApprovalRules.cs` 3 条端到端（ask 模式下被规则覆盖的 shell 不点任何人就完成且审计 source=approval_rule；未覆盖的命令照样驻留；`delegate_tool` 勾选后审查门裁决 shell）+ `ApprovalRuleServiceTests` 3 条服务级（含新计用语义与冲突 409）。6/6 通过。
- **诚实边界**：聚合后的 Core Api 全量本轮未重跑（上一轮的 580 全量在这些改动之前）；shell 进沙箱拆为独立项（见第四期 E7 备注）。

### 2026-09-30 第十一批（E1）：环境管家

- **登记归用户、取还归智能体、占用归账本**：不另造占用表——一个空位就是一条 `environment` 租约，run 终态自动归还。
- **安全**：连接信息拒收一切像凭据的字段，只收 `secret_ref`；三件工具只作用于调用者自己的 run。
- **包 2.16.0**：solo_master 可取还环境（Plan 只读），治理审查可看。
- **测试**：Core 5 条（含 solo_master 经包取还环境的端到端）；Desktop 组织页 +3、包与工具镜像随包更新、DTO 镜像 +4；Gateway 契约 9 路径 / 11 操作。

### 2026-09-30 第十批（D3）：并行 run 共享一份上下文

- **做法**：不另造共享存储——会话补丁账本本来就带版本号与仲裁。worker 事实从"只有本 run 读"改成"会话全部 run 读"，每条标来源 run 与版本号；用户插入仍只给目标 run。
- **实例池**：`ConversationIdentityInstanceId` / `MainInstanceId` / `InstancePoolIds` 第一次被写入，`ValidatePool` 第一次在生产路径上运行。
- **并发缺陷**：内容存储先查后移的竞争（两条并行准入冻结同样内容 → 500），已按内容寻址语义修正。
- **测试**：Core 端到端 +1。

### 2026-09-30 第九批（D2）：插入真的被读到，硬插入真的打断

- **发现**：软插入写的补丁从没进过任何提示词（上下文只读会话消息，插入不发消息）；worker 的 `CONTEXT_PATCH` 同样没人读。先修这个，硬插入才有意义。
- **硬插入的边界**：切模型调用、跳过未开始的工具调用；**不切正在执行的工具调用**（改动型调用中断 = 结果未知 = 停给人，违背转向）。信号进程内、请求挂起到引擎接手，别的主机上退化为软插入。
- **收尾记录**：worker 在原位置读到"[用户打断]"+ 半截输出 + 新指示；其他步骤整步重做（`run.interrupted`）。
- **回归**：D1 之后全量 API 567/569，两处失败都是测试自身——TinaChat 测试替身只拦了非流式调用（run 的智能体走流式），AskMode 第二条消息该是 `parallel`；均已修。
- **测试**：Core 新增 5 条端到端 + 4 条单元；AgentFramework 395、Governance 54 全过；Desktop 87 文件 797 例通过、`vue-tsc` 0 错。

### 2026-09-29 第八批（D1）：排队就是排在后面

- **语义**：`queued` = 等会话手上的活干完；`parallel` = 同时开，只受活跃 run 上限约束；插入不变（D2 另做）。停在人工决定上的 run 也要等——它占的是"会话在做的事"，不是算力。
- **实现**：准入 `SESSION_BUSY` → 接口落排队（主人是已持有队列的活跃 run，否则最新活跃 run）；run 终态只放行队头，其余按原顺序挪到新 run 后面（或挪到仍在跑的别的 run 后面）；`cancel` 可把仍在排队的消息出队。
- **Desktop**：排队卡片第一次真的对上了 Core 的队列（以前只在"没有 run_id"时显示，而接口总会带回 run_id，卡片从没出现过，还把它等的那条 run 标成了 queued）；卡片操作先出队，"并行"复用原 id 不重复发。
- **测试**：Core 端到端 1 条新增 + 2 条改为显式 `parallel`；Desktop 86 文件 796 例通过、`vue-tsc` 0 错。

### 2026-09-29 第七批（R5）：worktree 管家

- **修了一个会咬人的旧行为**：任何工具名含 worktree 的调用（包括只读的 `git_worktree_list`）都会独占整个仓库根，而账本按包含关系判冲突——等于和仓库里每个 worktree 都冲突。现在只有创建/删除声明，且只声明那一个 worktree（省略 path 时按工具规则推出默认位置）。
- **指派**：创建成功 → 本 run 的 `assignment` 租约（无 TaskId，活过调用与任务）；删除成功 → 释放；run 终态兜底。事件 `worktree.assigned` / `worktree.released`。
- **种子包 2.15.0**：`solo_master` 可开/收 worktree（Plan 关掉）。
- **测试**：`ResourceClaimResolverTests` 24/24（+2）；端到端 1 条；ToolChain + 种子包 + 证据 + 组织 48/48；Desktop 种子包 + toolPresentation 38/38。

### 2026-09-29 第六批（R4）：证据存档与回查

- **存什么**：任务终态（全文：状态、简报、执行者、写入范围、结果、证据、判据；每次尝试一份）、压缩摘要、组织报告、常驻成员结论，一律原文（≤64k）进 `agent_graph_evidence`，同一来源只留一条。
- **怎么查**：`recall_evidence`（Core 虚拟工具，会话取调用方自己的）= 关键词半边（中文双字切分、路径/句柄整体）+ 语义半边（有嵌入模型时），倒数排名融合；语义半边任何失败都退回关键词并说明原因。人用 `GET /api/v1/sessions/{id}/evidence`（Gateway 透传、Desktop 组织面板"证据"页）。
- **索引**：后台 `EvidenceIndexService` 批量写 `evidence:{sessionId}` 命名空间；无模型整批标 `unavailable` 一小时后再试（之后配模型会补齐），其他失败退避 6 次放弃；无项目的会话只走关键词。
- **种子包 2.14.0**：`governance_reviewer`、`solo_master` 拿到 `recall_evidence`，提示词各加一句。
- **测试**：`EvidenceArchiveTests` 3/3（含 HTTP）、`EvidenceTermsTests` 3/3、Team 组织端到端（审查员 `recall_evidence` 取回 `global_engineering#1` 的任务原话、报告本身也进存档）；种子包 17/17、toolPresentation 21/21、组织面板 + 证据检索 + api 36/36；Gateway 75/75（契约 7 路径 / 8 操作）。
- **未做**：压缩角色没有拆成会话 / run / 任务三层摘要；没有接真实嵌入模型实测召回质量。

### 2026-09-29 第五批（R1/R2）：委托审批门 + 组织面板收口

- **用户怎么选**：权限模式多三档，按消息选——`delegate-conversation`（对话身份结合用户目标审批）、`delegate-reviewer`（审查员在独立上下文审批）、`delegate-both`（先审查员后对话身份，都批才执行）。冻结进 run，和 ask / auto-approve / full-access 同一个位置。
- **PDP**：委托模式下可委托的写（非人工工具、风险 ≤ `DelegatedApprovalRiskMax`=medium、非 Core 虚拟工具）以 `delegated_gate_release` 放到审批层——与 auto-approve 同形：边界规则先过、租约单次、审批仍按请求哈希消费一次。读照旧按 ask 放行；人工工具与高于上限的风险（如 `git_commit` 的 high）仍停在 PDP 等人。这一步是必须的：治理层禁止祖先/后代智能体互批（`self_approval_forbidden`），对话身份不能走现成的委托记录。
- **审批门服务**（`Runtime/ApprovalGateService.cs`，后台轮询 + `RunPassAsync` 供测试）：只看"委托模式、未终态 run、kind=tool、非用户动作"的待批；每轮每个审批问一道门；门行 CAS 认领（`ClaimedAtUnixMs`，5 分钟失效重认领）；每 run 至多 200 次门决定，超了交还给人；人先点了则门记 `superseded`。结果走 `IToolApprovalCoordinator.DecideDelegatedAsync`（决策行不记人类主体）+ `approval.decided`（`decided_by=delegated_gates`）+ 唤醒 run；每道门另记 `approval.gate_decided`。
- **审批门判断**（`DmaEA/ApprovalGateJudge.cs`）：一次模型调用；参数经 `ApprovalEvidenceProjector.RedactSecrets` 去密钥（任意深度，正文保留可读）；对话身份自己发起的调用（solo 主人）一律退回；答不清、无路由、超时都退回，从不默认放行。
- **接口**：`GET /api/v1/approvals/{approvalId}/gates`（读取时按审批本身的状态解析：人先决定则显示 `superseded`）；Gateway 由组织合约脚本一并投影与透传；顺手删掉了 index.ts 里一套重复的组织路由（它把 `GET …/organization` 的查询串吞掉了，新路由测试抓到的）。
- **Desktop**：权限选择器三档新选项（悬停说明"shell、推送、删除、提交这类高风险动作仍由你本人决定"）；待批卡片（聊天里的工具卡、审批页）显示各门进度，只在有门在判时轮询；时间线记门的决定。组织面板（子智能体完成）：成员 / 会议室 / 报告 / 拓扑，可分离窗口。
- **测试**：Api 端到端 6/6（Team 双门逐轮、驳回不问后门、两种单门、退回后人仍能批、shell 不开门）；治理单测 +11（三模式放行、人工工具/high/critical/虚拟工具不放行、读照旧）；投影器 +1（去密钥）；Api 相关子集 99/102（1 条基线 `TinaChatTests.AcceptedHandoff…`，快照首跑再生，`VibePack_Run…` 并发下偶发、单跑与整类 34/34 通过）；Gateway 75/75；Desktop 85 文件 790 条全过，`vue-tsc` 0 错误（顺手补了 `AgentPackNodeRelationship.subscriptions` 类型）。
- **未做**：没接真实模型跑门；审查员与对话身份的门提示词未经真实模型评测。

### 2026-09-29 第四批（C）：会话 = TinaChat 组织

- **组织本体**：一会话一组织（`ChatOrganization`）；入组幂等（`(OrganizationId, MemberKey)` 唯一），对话身份、声明订阅的常驻治理角色、每个派出的执行者自动成为成员（执行者用 run 句柄如 `search#1`）；实例 → 成员由 `ChatInstanceBinding` 决定，solo 主人的各任务实例共用一个对话成员；执行者任务结束即离线，历史保留。
- **通讯录**：默认边按图现算、不落行（派发者↔被派者、同派发者的兄弟、治理层/对话身份/用户与所有人）；其余要申请并经对方同意（`ChatContact`）。
- **会议室**：大厅与公告板只放看全局的角色；每个派发者一间计划室（≤64 人，满了开下一间）；临时会议室与私聊；公告板只有用户/对话身份/治理层能发。
- **规模形状**：会议室帖只冻结"发送者 + 被提及者"的受众，其余按游标读；公告板帖只写一行受众；通知按主题 + run + 30 秒去重；唤醒到期判定走 SQL 索引（`DueAtUnixMs`）；并行排空；每成员 12 次/小时、每组织 60 次/小时，超了推迟不丢。
- **报告**：`ChatReport`（关于对象、类型、严重度、提议动作、证据），决定一次、带版本号；读取时附所引用对象的当前状态（租约/路径直接问账本）。warning/blocking 报告进用户与对话身份的收件箱，并以 `[organization_reports]` 证据进对话身份的上下文（协调者模式里对话身份不能持工具）。
- **唤醒泛化**：常驻成员被通知后，用**它自己冻结的**提示词、模型计划与声明工具跑一轮（`TinaChatMemberTurnRunner`，≤6 轮），不在任何执行者的 run 里；结束后在引发事实的 run 上记 `governance.member_turn`。
- **事实来源**：引擎（任务图、任务终态含失败、run 收尾、选人失败、写入范围冲突）与工具层（工具调用冲突、派发写入范围冲突、事后越界/冲突、审批停靠）都按冻结配置里的声明订阅者发通知。
- **工具**：`org_directory/org_read/org_send/org_report/org_decide_report/org_contact/org_room` + `graph_view`。
- **接口**：`/sessions/{id}/topology`、`/sessions/{id}/organization`（成员、会议室、消息、发帖、报告、决定）；Gateway 从 Core 快照投影合约并透明转发；OpenAPI 快照与 Desktop `schema.d.ts` 已再生。
- **种子包 2.13.0**：默认模式 Team；Team 常驻 `governance_reviewer`；执行者与 solo 主人拿到组织工具；代码评审故意不给。
- **测试**：`OrganizationTests` 8/8；端到端 `TeamRun_EnrolsItsOrganization_AndALeaseConflictWakesTheGovernanceReviewerWhoReports`；AgentFramework 389/389；Api 相关子集 121/122（唯一失败为基线既有的 `TinaChatTests.AcceptedHandoff…`）；Gateway 73/73；Desktop 种子包测试 9/9、toolPresentation 21/21。
- **未做**：没有接真实模型跑治理审查；Desktop 组织面板在做；全量 Core 测试未跑。

### 2026-09-29 第三批（A）：账本收口 + 写入范围 + 派发闸

- **删**：`agent_graph_wakes` 及 `GovernanceWakeQueue/Service/Store/Options/Topics`、`IGovernanceWakeQueue`；主题词汇只留 `Abstractions/Ports/GovernanceTopics.cs`。
- **账本**（`AgentGraph/ResourceLeaseService.cs`）：新增 `KeyNorm`（规范+小写）与 `Purpose`（tool / write_scope / assignment）两列，删 `ExpiresAt`；重叠查询在 SQL 里走索引（候选键及其各级上级目录 `IN`、下级目录前缀 `StartsWith`）；取租约在可串行化事务里读-判-写，争用重试 4 次（8 路并发只有 1 个成功，有测试）；冲突时问 `IRunLivenessProbe`，持有者 run 已终态就回收其全部租约再重试（崩溃主机留下的租约不再永久挡路）。新增 `ProbeAsync`（只读探测）与 `ListTaskAsync`。
- **shell 不再占租约**：`ResourceClaimResolver` 只给点名文件的工具与 worktree 工具生成声明；命令与 git 变更一律不声明。
- **写入范围**：`task_dispatch.write_scope` 在调用当下独占预留（重叠即拒，错误里点名持有者），规划任务的 `write_scope` 在任务开始时预留（失败即以 `resource_conflict` 失败该任务，不原地空转）；子任务的 task id 就是 dispatch_id（由 run + tool call 确定性派生，顺带修掉"重放拿到不存在的 dispatch_id"的旧缺陷），任务终态统一释放。worker 简报里写明自己的写入范围。
- **事后核对**：没有点名文件的写入类调用（shell、command_run、git 变更）完成后，拿写前快照比当前工作区，发 `workspace.changes_observed`；超出本任务写入范围的发 `scope.violation`；落进别的 run 持有范围的发 `lease.conflict`（`detected_at=after_the_fact`）。最多发布/探测 50 条路径，任何失败都不影响调用结果。
- **冲突事件**：`lease.conflict` 统一格式（`ResourceLeaseMessages`），三处来源：tool_call / write_scope / after_the_fact。
- **派发闸 C1**：关系文件的 `allowed_dispatch_targets` 冻结进 `RuntimeAgentDefinition.AllowedDispatchTargets`（缺省为 null=旧行为，空数组=谁都不能派），在冻结名册时一次收窄——规划名册、solo 名册段、`task_dispatch` 调用期校验同源；`ResolveRequestedWorker` 做最后兜底。
- **测试**：`ResourceLedgerTests` 6 例（跨上下级与大小写的重叠、幂等重入、并发只赢一个、回收死持有者、活持有者不回收且探测不写、按任务释放）；`ResourceLeasePolicyTests`/`ResourceClaimResolverTests` 22 例；端到端 `WriteScope_RefusesAnOverlapAtDispatch_AndAfterTheFactCatchesAWriteOutsideIt`。AgentFramework 389/389；ToolChain/ToolDispatcherResilience/ResourceLedger/ApiEndpoint 63/63。


### 2026-09-29 第二批：模式好懂、好用（预设模板）

**决定**（用户确认）：会话 = TinaChat 组织；兄弟节点可直连但只经 TinaChat；shell 按 architecture §7.4；开销交给用户自定义 + 预设模板；模式用熟悉的词命名。

**种子包 2.12.0**（digest `3dcef891…3280`）：四个旧模式改名改描述（行为不变）——Team / Graph / Workflow / Solo；新增三个模式——**Plan**（`solo_master` + 写工具与 TinaChat 工具全部关闭，只能派 `search`）、**Review**（`meeting` + 新的只读 `reviewer` 执行者）、**Spec**（`meeting` + search/global_engineering，三份规格逐份确认后落盘）；新增 `reviewer` 智能体与 plan/review/spec 三条管线；管线显示名随模式改名。

**修掉的问题**：
- 聊天框的模式下拉从未显示过描述：Core 发 `description`，Desktop 读的是从不存在的 `summary`；设置里复制模式发 `summary` 也被 Core 忽略，描述丢失。
- 七个模式同一个图标：新增 `lib/modePresentation.ts`（按 slug 给图标与排序，测试双向钉死到清单）。
- **新聊天选了 Solo 发第一句就失败**：懒创建会话时没带所选模式，会话锁在默认的 `meeting` 身份上。修两处：Desktop 创建会话带上所选模式；Core 空会话采用第一条消息所用模式的身份（`AdoptConversationIdentityIfEmptyAsync`）。
- 跨身份切模式的报错是带内部 slug 的英文：改为中文说明；原文移到 `diagnostic`（Gateway 优先取 `detail`，原文放 `detail` 会盖掉说明），`mode_unavailable` 同一问题一并修。

**测试**：Core 新增 4 条端到端（空会话采用身份并锁定；Plan 不提供任何写工具且可在同会话交给 Solo；Review 每个角度一个独立只读评审；Spec 的管线到达规划器且可先提问）——空会话那条先在去掉修复时确认会失败再恢复。Core 全量：Governance 44/44、Architecture 17/17、AgentFramework 384/385、Api 538/542。失败逐条：`ResourceLeasePolicyTests.DifferentKeysAndDifferentKindsNeverCollide`（第一批半成品改了冲突语义、测试未跟上，随 A3/A5 处理）；`ApiEndpointTests` 两条模块数断言（第一批新增 AgentGraph 模块后只改了 AgentFramework 那一处，本批已改为 15 并单独跑通）；`TinaChatTests.AcceptedHandoff…`（基线 HEAD 就失败）；`FullDuplexEndpointTests.InvokeStream_TransientModelOutage…`（全量并发下 38 秒超时失败，单独跑 8 秒通过，依赖重试退避计时，记为负载下偶发）。Desktop `vitest` 768 通过 / 14 跳过、`vue-tsc` 通过；浏览器里用真实清单数据核对了模式下拉（七个图标、两行描述、排序、选中后触发器图标）。

**没有验证的**：没有接真实模型跑这三个新模式；提示词只经过静态测试与脚本化引擎测试。Review 的"几名评审"在同一 run 里仍是依次执行（上下文彼此独立，但不是同时跑）。

### 2026-09-28 第一批：P1-A / P1-B / P1-C2 地基

**新增模块** `TinadecCore/AgentGraph/`（`TinadecCore.AgentGraph.csproj`，已入 `TinadecCore.slnx`、`Runtime` 项目引用与 DI 注册）：
`AgentGraphDbContext`（三张表：`agent_graph_resource_leases` / `agent_graph_wakes` / `agent_graph_approval_gates`）、
`ResourceLeaseService`、`GovernanceWakeQueue`、`GovernanceWakeService` + `GovernanceWakeStore`、`GovernanceWakeOptions`（**默认关闭**）。

**纯函数（放 Abstractions，无 EF 依赖）**：`Abstractions/Ports/IResourceLeaseService.cs`（端口 + `ResourceLeasePolicy` 冲突判定）、
`Abstractions/Ports/ResourceClaimResolver.cs`（工具调用 → 资源声明）、`Abstractions/Ports/IGovernanceWakeQueue.cs`（唤醒端口）。

**接线**：
- `Tools/ToolDispatcher.cs`：`PrepareAsync` 在解出工具与参数后、执行前取租约；冲突时以 `RunErrorTaxonomy.ResourceConflict` 回可纠正错误（列出当前持有者）；ctor 新增可选 `IResourceLeaseService?`。
- `DmaEA/FullDuplexRunEngine.cs`：字段 `_resourceLeases`（可选注入）；任务终态释放该任务的租约；run 终态走 `ReleaseRunLeasesAsync` 兜底（释放失败只记警告，不让已完成的 run 变失败）。
- `Abstractions/RunStatus/RunErrorTaxonomy.cs`：新增 `ResourceConflict`。
- `C2` 由子代理完成：`RuntimeAgentSeed` 增 `ParentInstanceId`/`GenerationDepth`，`CreateRootAsync` 落库；`GetOrCreateWorkerAsync` 按 `DispatchedByTaskId` 推导深度并 `EnsureGraphWorkerDepth` 强制；**预算保持 run 级不合并**（原文档 §4.1 的说法已修正）。

**测试**：`ResourceLeasePolicyTests`（10 例）、`ResourceClaimResolverTests`（8 例）、`WorkerDepthLineageTests`（8 例）。
`AgentFramework` **385/385 通过**；`ToolDispatcherResilienceTests|FullDuplexEndpointTests` **71/71 通过**。
`FullCompositionRegistersAllModules` 的模块数断言由 14 改为 15（新增模块），已加 `agent_graph` id 断言。

**尚未完成（诚实）**：
- A3 只覆盖「执行前取租约 + 终态释放」，**没有端到端测试证明冲突真的被拦下**；`shell` 声明整个工作区，会串行化同一工作区的两条 run（当前是刻意的粗粒度，待评估）。
- A4 冲突事件未发，P1-B 的 `lease_conflict` 主题因此还没有发射点。
- B2 只建了主题表，**订阅解析（关系文件 `subscriptions` 字段 → 冻结进配置）未做**；B4 替换 `OperationalTriggers` 的角色名 switch **未做**。
- B3 的 `GovernanceWakeService` 已可运行，但**没有注册 `IGovernanceWakeProcessor`**，所以唤醒只会排队、不会真的叫醒任何角色（这是有意为之：先让队列可见）。
- C1 `allowed_dispatch_targets` 接闸**未做**。
