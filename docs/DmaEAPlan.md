# TinadecOffice 双层全双工自主智能体落地计划

## 核心理念

- 正式采用“运营层 + 执行层”：运营层负责会议入口、全局协调、上下文、监督和经验提炼；执行层负责任务规划、动态创建 worker、工具执行和证据交付。
- 每条用户消息都进入双层运行框架，但由应用模式、智能体模式、用户意图和当前任务状态决定实际激活哪些智能体。
- 会议智能体是唯一用户入口。其他智能体不能直接回复用户、写正式长期记忆或绕过 Core 调用工具。
- 智能体自创建是 Core 编排能力，不是普通工具：授权协调者可创建运行期子智能体，记录父子 lineage、上下文切片、权限、预算和生命周期。
- 运行期生成的智能体默认随任务释放；有复用价值时生成候选，人工评测和晋升后才成为永久版本化 profile。
- 会话历史和运行摘要自动参与当前会话；跨会话记忆先成为候选，审核后才进入长期检索。
- 全双工意味着后台任务运行期间，用户仍可询问状态、补充或修改目标、创建新任务、暂停、恢复和取消；所有写入通过 `context_revision` 防止过期结果覆盖新目标。
- Core 始终是会话、任务、智能体、记忆、审批、工具和事件的唯一状态权威。

## Core 配置与状态模型

- 增加带完整注释的默认 TOML，覆盖应用模式、六种智能体模式、profile 绑定、角色、模型路由、工具范围、调度、生成预算、上下文、监督、记忆和失败策略。
- TOML 提供只读内置基线，数据库保存工作区覆盖、人工晋升和不可变版本；优先级为 TOML 基线 → 工作区版本，run 创建时冻结最终配置和哈希。
- 配置校验成功后热重载，只影响新 run；无效修改保留上一有效版本并写入 readiness 诊断。
- 默认 `conversation` 开放 `plan/spec/ask/vibe/auto/agent`，默认 `auto`；`space` 仅开放 `agent`，绑定 `space.full_duplex`。模式集合仍可由 TOML 调整，Desktop 的 `im/hub` 作为兼容别名。
- 默认生成预算写入 TOML：深度 2、每 run 最多 8 个生成实例、并发 worker 4、同会话活跃 run 2；全部可配置。
- 将 `planning` 层数据迁移并规范化为 `operation`，读写兼容旧值但新 API 只返回正式值。
- 增加消息/turn、上下文快照与补丁、agent instance/spawn、agent candidate、memory candidate/item/version、tool execution 和配置绑定投影。
- 大正文继续存入不可变 ContentStore，关系库只保存索引、状态、哈希和引用；SQLite/PostgreSQL 同步增加迁移。
- 幂等导入现有会话 JSON 历史，不删除旧文件；导入成功后切换到并发安全的关系索引和内容存储。

## 全双工运行时

- 用持久化后台 run coordinator 解耦 HTTP 连接和任务生命周期；客户端断开只停止流读取，不取消后台任务。
- 会议智能体将消息分类为普通问答、状态查询、补充、目标调整、控制命令或新任务，并绑定或创建相应 run。
- 状态机覆盖 `understanding/executing/replanning/awaiting_approval/paused/reviewing/completed/failed/cancelled`，支持重启恢复。
- 执行层任务规划智能体生成依赖图，调度就绪节点并汇总证据；监督智能体在最终回复前返回 `pass/revise/escalate`，默认最多修正两轮。
- 新增 Core-owned spawn 服务。创建请求必须声明目标、成功标准、父实例、上下文选择器、模型、工具、资源权限和预算；子智能体不得扩大父级权限或跨越层级边界。
- 会议/任务规划及具有 `agent.spawn` 能力的实例可创建子智能体；普通 worker 只能提交 spawn 请求。生成实例不能获得 `direct_user_output`、正式记忆写入或晋升权限。
- 实现 ContextProvider、PromptAssembler 和 MemoryStore：按“架构基线 → 模式 profile → agent profile → 任务上下文 → 运行约束”确定性组装，并记录来源、版本、token 使用和警告。
- 上下文包组合当前消息、结构化会话状态、近期历史、任务状态、已审核长期记忆、工具/技能能力；只在内容存储保留完整正文，事件只记录引用、计数和摘要。
- 使用真实模型流输出会议智能体回复，Core 创建并完成 assistant message；失败必须持久化失败状态和安全错误，不能伪造 `done`。

## 工具、审批与记忆

- Core 直接管理按项目根目录启动的 TinadecTools 子进程，通过现有行式 JSON 协议调用；增加协议版本/manifest 握手、工具参数 schema、风险和审批元数据。
- Core 成为唯一规范化工具注册表。Gateway 删除重复的工具策略和执行判断，只代理 Core。
- 工具调用先绑定 run、task、agent、参数哈希和权限范围；需要审批时将任务置为 `awaiting_approval`，审批必须匹配租户、run、task、tool、参数哈希、有效期且只能消费一次。
- TinadecTools 只有在 Core 验证审批后才收到 `approved=true`；工具自身的 `confirm_*` 二次确认继续保留。
- 工具进程崩溃、超时和取消产生结构化结果并可按 profile 重试；同工作区写操作串行，安全只读调用可受配置并行。
- 长期记忆支持主体、工作区、项目、智能体四种作用域，以及事实、偏好、决策、成功模式、失败模式、任务模板和监督规则等类型。
- 进化智能体只创建带来源、证据、适用条件、失效条件和置信度的记忆/智能体候选；候选不会进入运行检索。
- 人工晋升创建不可变正式版本并建立向量索引；拒绝、撤销、替代和使用反馈全部留审计事件。长期记忆审核与工具审批使用独立状态机。

## API 与产品接入

- 扩展 `invoke-stream` 请求：`content`、`client_message_id`、`application_mode`、`agent_mode`、`permission_mode`、可选 `target_run_id` 和 `expected_context_revision`。
- 流块增加 `turn_id/message_id/seq`，支持 `ack/delta/usage/done/error`；运行进度、agent 创建、工具、审批和监督走可重放且可实时跟随的 session event SSE。
- 新增 run 控制、按 run 查询 orchestration、agent lineage、上下文版本、记忆候选/正式记忆及晋升/拒绝接口；保留现有 session 最新快照接口作兼容视图。
- `POST /messages` 标记为存储兼容接口，Gateway/Desktop 的正常发送统一改用 `invoke-stream`，避免重复写用户消息。
- Core 完成后再接 Gateway/Desktop：修复真实流式对话、assistant 消息、停止/恢复/取消、多活跃任务、审批恢复、agent lineage 和记忆/智能体候选审核界面。
- Desktop 从 Core 获取可用应用模式和智能体模式，不再硬编码六模式；空间模式默认只展示 `agent`。UI 复用现有 shadcn-vue 组件、材质令牌和通知生命周期。
- 将 `withdocs` 的理念整理进仓库中英文产品/架构文档，并同步更新根、Core、Gateway、Desktop 的 `AGENTS.md` 元数据和真实能力状态。

## 交付顺序

1. 固化双层术语、TOML schema、事件与 HTTP 契约。
2. 完成 Core 存储迁移、配置解析/热重载、消息/上下文/agent/memory 状态模型。
3. 完成全双工 coordinator、真实流式模型、动态 agent 创建、监督与恢复状态机。
4. 完成 TinadecTools 子进程适配、工具注册、审批暂停/恢复和审计。
5. 完成记忆/智能体候选生成、评测、人工晋升和检索注入。
6. 将 Gateway 收缩为薄代理并完成 Desktop 对话、控制、审阅和可视化接入。

## 验收与测试

- Core 端到端测试应能从 HTTP 创建项目/会话，启动全双工 run，动态创建 worker，触发工具审批，批准后恢复，流式返回并只持久化一条 assistant 消息。
- 覆盖并发消息、多活跃 run 限流、目标调整重规划、暂停/恢复/取消、断线重连、重启恢复、幂等消息和过期 context patch。
- 覆盖 spawn 未授权、权限升级、预算/深度超限、lineage、释放和候选晋升。
- 覆盖候选记忆晋升前不可检索、作用域与租户隔离、晋升后向量/无 embedding 降级检索、撤销与替代。
- 覆盖审批参数篡改、重复消费、过期、跨租户/跨任务复用、工具进程崩溃和超时。
- SQLite 与 PostgreSQL 运行同一存储契约测试；保持 snake_case、秘密不出响应、完整 prompt 不进入事件。
- Gateway 增加纯代理契约测试；Desktop 增加模式解析、流式消息、任务控制和候选审核测试，并执行构建与关键视口视觉检查。
