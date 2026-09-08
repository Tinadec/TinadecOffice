# TinadecOffice 双层智能体：嵌套任务规划与完成钩子实施计划

## 1. 目标与非目标

### 目标

在不引入第三个业务层的前提下，支持以下可恢复流程：

```text
operation.meeting
  ├─ execution.planner A
  │    └─ development worker(s)
  └─ execution.planner B
       ├─ 等待 development 完成事件
       ├─ 校验任务状态和证据
       ├─ test worker(s)
       └─ commit worker（受测试成功和审批约束）
```

具体要求：

- 用户提交任务后可断开客户端，run 在 Core 中继续运行。
- 用户可在原 run 运行期间追加“完成后测试并提交”约束。
- planner B 必须属于 `execution` 层，而不是新增 `planning`/第三层。
- 开发完成后，测试和提交规划可被同一事件唤醒；测试与提交可并行，但提交必须额外依赖测试成功。
- 重启、重复事件、模型重试、审批等待都不能导致重复执行或越权写入。

### 非目标

- 不把 Gateway 或 Desktop 变成调度器。
- 不允许子 planner 获得 `direct_user_output`、更高权限或直接修改正式长期记忆。
- 不在本阶段实现任意脚本式工作流语言；先实现受约束的声明式 hook 和条件。
- 不绕过现有 Tool Dispatcher、workspace snapshot、PDP 和 Git 审批链。

## 2. 当前基线与约束

现有 `FullDuplexRunEngine` 已提供持久 run、任务 DAG、依赖解析、ready task 并行、worker assignment、context revision、checkpoint 恢复和唯一 meeting 用户出口。默认运行预算位于 `DmaEA/Configuration/default-agent-runtime.toml`：

- `max_depth = 2`
- `max_agents_per_run = 8`
- `max_parallel_workers = 4`
- `max_active_runs_per_session = 2`

当前 worker 创建路径 `GetOrCreateWorkerAsync` 将 parent 固定为 planner，尚无 planner 子实例；通用 scheduling 写路径仍未完成。文档中描述的 `OperationalTriggers.cs` 在当前源码树未发现，不能假定已有通用消息钩子。

实现必须继续遵守：Core 是唯一业务状态权威；所有公共接口保持 `/api/v1`；新事件、DTO、配置只使用 `operation`/`execution` 术语。

## 3. 目标架构

### 3.1 统一为“编排实例 + 任务节点”

扩展现有 agent instance/lineage 模型，使 planner 和 worker 都是 execution 层的 `orchestration_instance`：

- `kind`: `planner | worker`
- `parent_instance_id`
- `depth`
- `task_scope_id`
- 冻结的 AgentVersion、PromptVersion、ModeVersion、model binding、tool manifest hash
- `budget`、`lease`、`status`

meeting 仍是 operation 层根实例；planner B 的 parent 可以是 meeting 或 planner A，但其 `layer` 必须为 `execution`，并且不能产生用户答复。

### 3.2 声明式 Hook

新增 Core-owned `task_hooks`（名称可调整）概念：

- `hook_id`
- `run_id`
- `source_task_key` 或事件过滤器
- `event_type`（首批 `task.completed`、`task.failed`、`run.finalized`）
- `condition`（首批状态、结果状态、依赖完成、证据存在）
- `action`（`start_planner`、`release_tasks`、`request_approval`）
- `target_template`（planner/worker 角色、任务模板、成功标准）
- `idempotency_key`
- `status`: `armed | fired | waiting_condition | completed | failed | cancelled`
- `created_event_seq`、`fired_event_seq`、`last_error`

Hook 只由 Core 解释和执行；模型只能提出 hook 请求，不能直接注册任意回调地址或绕过权限。

### 3.3 事件与条件门

任务完成必须先写入事件和 checkpoint，再由异步 `HookDispatcher` 消费。处理顺序：

1. 按事件序号读取未处理事件。
2. 以数据库 CAS 将 hook 从 `armed` 置为 `firing`。
3. 重新读取源任务、结果证据、context revision 和依赖状态。
4. 条件不满足则置 `waiting_condition`，等待后续事件；满足则创建 planner/任务节点。
5. 持久化 assignment、lineage、hook fired event 和新的 checkpoint。
6. 由 scheduler 唤醒 run；重复事件通过 `idempotency_key` 返回已有结果。

## 4. 分阶段实施

### Phase 0：契约冻结与 ADR（1–2 天）

- 建立本计划对应 ADR，明确“execution 内嵌套 planner，不新增第三层”。
- 固化状态机、事件名、幂等语义、错误码和并发上限。
- 决定 hook 是 run-scoped 还是 session-scoped；首版只允许 run-scoped。
- 更新 `docs/architecture.md`、中文产品定义和 OpenAPI snapshot。

验收：架构评审通过；不存在 `planning` 新写入；所有新增 DTO 使用 snake_case。

### Phase 1：领域模型与持久化（3–5 天）

- 在 Lifecycle/Control DbContext 增加 orchestration instance、task hook、hook delivery、scheduler lease 表。
- 增加不可变 event payload schema 和唯一索引：`(run_id, idempotency_key)`、`(event_seq, hook_id)`。
- 扩展 `FullDuplexCheckpointV1` 保存 planner 树、hook 状态、等待原因和 context revision。
- 为旧 checkpoint 提供只读兼容反序列化；写回时转换为 canonical execution 结构。
- 新增 `IOrchestrationInstanceStore`、`ITaskHookStore`、`IEventConsumerCheckpointStore`。

验收：SQLite/PostgreSQL migration、并发 CAS、重启恢复和旧 checkpoint 测试通过。

### Phase 2：持久调度器与租约（3–5 天）

- 实现 Core hosted `ExecutionScheduler`，替换 scheduling 写路径的 501。
- 支持 ready queue、优先级、`max_parallel_workers`、每 run/每 session 限流、租约 TTL、重试退避。
- 把现有 `ExecuteReadyTasksAsync` 拆成“入队/领取/执行/提交结果”四步，避免长事务。
- 统一 crash recovery：过期 lease → `retryable`；未知外部副作用 → `outcome_unknown`，不得自动重放。
- 保留 workspace 写入串行策略；只读任务允许并行。

验收：4 个并行 worker、租约过期接管、进程重启、取消/暂停/恢复、重复领取测试通过。

### Phase 3：嵌套 planner（4–6 天）

- 将 `GetOrCreateWorkerAsync` 抽象为 `GetOrCreateExecutionInstanceAsync`。
- 新增 planner spawn 请求和模板校验：只能使用冻结 roster 中的 `task_planner` 或受批准的 execution planner profile。
- 扩展 `AgentSpawnLimits`：深度、总实例数、并行 planner 数、单 planner 任务数；首版建议深度仍为 2，但允许 meeting → planner B → worker，禁止 planner 无限递归。
- planner B 的输入必须包含源任务的结构化证据和只读 context snapshot，不直接共享可变 checkpoint。
- planner 输出仍经过 `ValidateAndMaterializeGraph`；跨 planner 依赖转换为显式 `task_gate`。

验收：planner B 可持久化创建；parent/child lineage 可查询；超预算、错误层级、未绑定版本均 fail closed。

### Phase 4：任务完成 Hook 与条件门（4–6 天）

- 实现 `HookDispatcher`、事件消费 watermark、CAS firing、重试和 dead-letter 状态。
- 首批条件：`source_task.status == completed`、`result_status == succeeded`、指定依赖全部 completed、证据字段非空。
- 首批动作：启动 execution planner、释放已有 pending 节点；暂不支持任意 HTTP/webhook。
- 为用户追加指令编译 hook：`完成后测试并提交` 生成 `development.completed → test` 与 `test.succeeded → commit` 两道 gate。
- 所有 hook 触发写入 `hook.armed`、`hook.fired`、`hook.skipped` 或 `hook.failed` 事件。

验收：开发完成事件只触发一次；条件未满足不会启动 worker；事件乱序/重复/重启后最终状态正确。

### Phase 5：测试—提交安全链（3–4 天）

- 将 test 和 commit 任务建模为独立节点；commit 依赖 test 成功，而不是仅依赖 development 完成。
- 测试 worker 默认只读工具；测试结果必须包含 exit code、摘要、日志引用和 workspace snapshot hash。
- commit worker 只能调用 Core-governed Git mutation tool；提交前重新校验 snapshot、manifest、参数和 lease。
- 根据策略决定提交审批：默认 `awaiting_approval`；允许 workspace policy 明确配置无人值守，但仍保留审计和不可逆标记。
- commit 失败或测试失败时，禁止自动重试远程 push；进入 `awaiting_user` 或 `outcome_unknown`。

验收：测试失败不提交、审批拒绝不提交、snapshot 漂移阻断提交、重复事件不产生重复 commit。

### Phase 6：全双工交互与 API/UI（3–5 天）

- 扩展 `POST /api/v1/sessions/{id}/interactions` 的 goal adjustment/supplement 编译逻辑，返回新增 hook/planner 的稳定 ID。
- 增加只读查询：run orchestration tree、task hooks、scheduler leases、hook deliveries。
- SSE 增加 `planner.created`、`hook.armed`、`hook.fired`、`gate.waiting`、`scheduler.retrying` 事件。
- Desktop 只展示 Core 投影：等待原因、依赖关系、审批入口和证据，不在前端自行判断完成。
- 断线重连使用已有 replay/follow 机制；客户端不负责唤醒 run。

验收：用户离开后关闭 Desktop，后台完成开发→测试→审批→提交；重新打开可完整回放。

### Phase 7：灰度、观测与文档（2–3 天）

- 通过 runtime profile feature flag 开启 nested planner/hooks，默认关闭。
- 增加指标：hook latency、duplicate suppression、lease recovery、planner depth、blocked duration、approval wait。
- 增加 Debug Studio replay 数据；敏感 prompt/tool 参数继续默认不导出。
- 更新 readiness，明确 scheduler/hook 能力状态，移除对应 501 误报。

## 5. 推荐的首版任务编译结果

用户追加“功能完成后测试并提交”时，不直接让模型维护隐式消息钩子，而编译为：

```text
development (已有任务)
  └─ gate: status=completed && result_status=succeeded && evidence.present
       └─ test (execution worker)
            └─ gate: status=completed && result_status=succeeded
                 └─ commit (execution worker, approval required)
```

如果确实需要独立 planner B，则由同一条 gate 触发 `start_planner`，planner B 生成 `test` 与 `commit` 两节点；Core 仍强制注入上述依赖，不能完全信任 planner 输出。

## 6. 主要风险与控制

| 风险 | 控制措施 |
|---|---|
| 重复事件导致重复测试/提交 | 唯一幂等键 + hook CAS + action idempotency |
| planner 子树无限扩张 | 深度/总实例/并行数/预算硬限制 |
| 测试和提交竞态 | commit 显式依赖 test 成功；workspace 写锁 |
| Git 状态变化 | 提交前重新 snapshot，漂移则阻断 |
| 用户离开导致任务丢失 | durable queue + lease checkpoint + replay |
| 模型伪造“已完成” | 只接受 Core task/result 状态和结构化证据 |
| 子 agent 越权 | 冻结版本、工具 manifest、继承权限、无 direct_user_output |
| 外部副作用未知 | `outcome_unknown`，人工恢复决策，不自动重放 |
| 文档与代码不一致 | 每个 phase 同步更新 docs、OpenAPI snapshot、readiness 和测试 |

## 7. 最小可交付版本（MVP）

建议先交付 Phase 1–2 + Phase 4 的“单 planner DAG hook”子集，再实施 Phase 3 的嵌套 planner：

1. 持久 task gate 和 scheduler。
2. `development → test → commit` 的自动推进。
3. 测试成功后的提交审批。
4. 完整恢复、幂等和审计。

这样可以先满足用户场景的业务结果，同时保持双层架构；嵌套 planner 作为后续增强，不会阻塞首版可用性。

## 8. 端到端验收场景

1. 用户提交功能开发；meeting 创建 run，planner 创建 development DAG。
2. 用户追加测试/提交要求并断开客户端。
3. Core 持久化 hook 和 gate；development worker 完成并写入证据。
4. HookDispatcher 只触发一次；test worker 执行并返回成功/失败证据。
5. 仅当 test 成功时释放 commit；commit 进入审批。
6. 用户批准后完成 commit；Git snapshot/hash、commit id、审批和工具 receipt 全部可审计。
7. 在每个步骤前杀掉 Core 进程并重启，最终结果与不中断运行一致。
8. 重放 run timeline，meeting 生成唯一用户可见总结；其它 agent 无用户直出事件。

