# TinadecOffice 架构理解（当前态）

生成：2026-08-27 · 方法：`architecture-visualization` 包的 `explore → system-modeler`（`c4model` + `graphviz` 为工件源）
证据：仓库静态源码/配置/文档，逐条见 `tinadecoffice.evidence.md`。基线提交 `f9c44c0`（`main`）。

- **目的**：让一个要改这个系统的人（或智能体）能在不读 2052 行 Gateway 与 1889 行 run 引擎的前提下，说清"四产品各管什么、一次交互经过谁、状态落在哪里、什么必须经审批、哪些还是桩"。
- **读者**：Core 平台开发、Desktop/Web 前端、工具层维护者、架构评审。
- **范围**：当前态为主。目标态（独立 Tool Runtime 服务、嵌入式 NuGet、SDK/CLI 交付）只以 `Target`/虚线节点出现，不与现状混谈。
- **不覆盖**：风险评级与整改路线（属 `risk-quality-reviewer` / `evolution-planner`），密钥细节，UI 视觉规范。

## 1. 一句话结论

TinadecOffice 是**四个可独立版本化产品的仓库内集成体**：`TinadecApp`（Electron/Web 客户端）→ `TinadecGateway`（Bun+Elysia 无状态门面）→ `TinadecCore`（.NET 10 + MAF 1.18 的治理与运行时权威，单体但模块零互引）→ `TinadecTool`（`TinadecTools` 审批感知子进程）。
真正的架构主张只有一句：**Core 是唯一状态与授权权威，其它三者只搬运字节和渲染**。仓库里这句话基本成立，但有四处可命名的弯曲（见 §5）。

## 2. 产品边界与责任人语义

| 产品 | 权威拥有 | 明确不拥有 | 仓库映射 |
| --- | --- | --- | --- |
| TinadecCore | 租户/工作区/主体、会话/消息/run/turn/task、发布态不可变配置、Pack 安装真相、权限请求/能力租约/审批、工具 manifest 冻结副本、上下文与记忆、快照元数据与恢复计划、事件/trace/成本/演化审计 | UI、通用网关、具体工具实现 | `TinadecCore/`（21 源 + 4 测试项目） |
| TinadecTool | 工具自身校验、执行与结构化结果、Windows 沙箱账户与 ACL | 任务编排、会话状态、最终授权决定 | `TinadecTools/` + `TinadecTools.Generators/` |
| TinadecGateway | 对外契约形状（snake_case、`X-Request-Id`、ProblemDetails）、CORS/Swagger、协议适配与流转发 | Core 业务状态、智能体决策、工具策略 | `TinadecGateway/` |
| TinadecApp | 交互体验与本地偏好（窗口/主题/pet）、随包分发的 Agent Pack 内容制品 | 编排真相、密钥、审批策略、Pack 安装状态、工具执行 | `apps/desktop`、`apps/web`、`apps/TinadecUI` |

"可单独使用"的准确含义：四者可独立安装/替换，但 App 与 Gateway 仍需一个符合当前契约的上游；Core 与 Tool 才提供独立运行价值（`docs/tinadec-core-product-definition.zh-CN.md:103`）。

## 3. Core 的模块形态：一个"只认端口"的单体

- **依赖方向单一**：所有业务模块只引用 `Abstractions`（部分再加 `Contracts`/`Strategies`/`Persistence`），**模块与模块之间零 `ProjectReference`**；只有 `Runtime` 聚合 18 个项目、`Api` 只引用 `Runtime`。组合根顺序：Tenancy→AgentConfiguration→VectorStore→Lifecycle→Governance→Models→Context→Prompts→Memory→Skills→LoopGuard→Tools→DmaEA（`TinadecCoreServiceCollectionExtensions.cs:36-48`），随后在 `:53-75` 用 `Replace`/`AddSingleton` 补上 Core 侧的授权上下文解析、正式模式解析、逐 Agent 模型解析、UserToolAction 与从 TOML 重绑的 `ToolDispatchOptions`——Governance 先注册以保持可独立打包，其 fail-closed 占位只在完整运行时被替换。
- **实现藏在 registrar 里**：每个模块的对外面是 `*ModuleRegistrar.cs` 内的 `internal` 实现（如 `CoreVectorStore : IVectorStore`、`ModelProvider : IModelProvider, IChatResolver`、`ContextProvider`、`PromptAssembler`、`LoopGuardEvaluator`、`MemoryStore`、`LifecycleManager`），跨模块协作只能经 23 个端口文件声明的接口。
- **9 个 DbContext，两类真相**：`MemoryDbContext`（会话/消息/turn/上下文版本/记忆）、`LifecycleDbContext`（run/task/step/event 与 `event_index`/`control_event_index`/`run_stream`）、`TenancyDbContext`、`ModelControlDbContext`（provider/路由/不可变配置版本）、`AgentConfigurationDbContext`（draft/发布版本/Pack 安装）、`PromptControlDbContext`、`Skills/IntegrationDbContext`、`GovernanceDbContext`（角色/能力授予/租约/审批）、`DmaEA/AgentControlDbContext`（`agent_instances`/`agent_candidates`/`runtime_profile_overrides`/`model_invocations`）。
- **关系投影 + 文件正文**：正文不进关系库。`data/` 下 `sessions/ tasks/ events/ artifacts/ content/ vectors/` 六种 journal 目录由 `PersistencePaths` 集中管理，内容经 `IContentStore` 原子写 + SHA-256 引用；密钥只存 SecretStore 引用（Windows DPAPI），事件流里只有 id/计数/警告。
- **可验证的边界**：`tests/TinadecCore.Architecture.Tests/ArchitectureTests.cs` 11 处断言把"零互引 + 只向 Persistence"变成测试。

## 4. 业务与生命周期语义（改这里最容易踩坑）

1. **两层命名已收敛**：治理层 `operation`、执行层 `execution`。`planning` 只作为旧输入迁移期接受；`RunStatus` 已无 `finalizing`。
2. **run 状态**：枚举 12 值（含 `AwaitingApproval`/`AwaitingDelegate`/`AwaitingUser`）。同时存在一份 F# 纯策略 `StateTransition.fs`，它只覆盖 10 个规范态与 `pending/running` 别名，**不含 `awaiting_user`/`awaiting_delegate`**，也未见 run 引擎调用。⇒ 事实上的 run 状态权威是 `RunStatus` 字符串 + 持久层比较逻辑，`validateTransition` 目前是独立可复用策略而非单一真相。这一点在视图中降级为"两套并存"。
3. **一次交互（14 步）的关键非直觉事实**：`POST /sessions/{id}/interactions` **返回 201 JSON 而不是 SSE**；要看流必须另开 `GET …/runs/{runId}/stream`，或由 `invoke-stream` 走请求内流。受理时写库（幂等键 = client message id、`context_revision` 冲突→409、活跃 run 上限），并**冻结**运行配置：策略快照 + 关系化 roster + 逐 Agent 模型计划 + 工具 manifest 哈希，此后 run 内不再变更。
4. **恢复有两套**：run 引擎每 2s 扫 `ListLeaseEligibleRunsAsync`（30s 租约、准入宽限期、排除 `awaiting_*`）重排队；`RunRecoveryHostedService` 只在启动时把孤儿 run 标 `failed` 并写 `run.recovered`，**不续跑**。等审批/等委托/等用户的 run 在两条路径上都被明确豁免，这是"重启不丢审批"的设计落点。
5. **审批与确认是 AND 关系**：任何 mutating 动作必须过 Core 的 PDP/租约/一次性 `ActionApproval`；工具层再叠 `confirm_*` 字面量闸门（`ToolConfirmations.Require`）。写操作前 `WorkspaceSnapshotService` 捕获文件系统/Git 快照，失败即阻塞。
6. **用户直连与智能体工具执行是两条路径**（产品定义 §3.3）：用户动作走 `POST /api/v1/user/tool-actions`（Core 建 UserToolAction、快照、PDP、审计）；智能体走 `POST /api/v1/runs/{runId}/tools/{toolId}/execute`（Core 冻结配置 + 调 Tool Provider）；而 `/api/v1/code/tools/{id}/execute` 与 `/api/v1/tool-runtime/*` 是**刻意保留的无状态传输面**，不是旧路由。
7. **发布态不可变 + ETag**：Agent/Mode/Prompt 的 draft 与 published 版本分离，`If-Match` 并发控制；Pack 走 preview→install，preview 15 分钟过期、`If-Match` 必须是 preview 返回的 etag、`Idempotency-Key` 必填且幂等键只对相同 digest 复用。内容制品归 App，安装真相与托管只读归 Core。
8. **模型策略只有三种**：`inherit | route | fixed`，统一经 `IAgentModelResolver`，并逐回合写 `model_invocations` 归因（call_id/attempt、strategy_source、fallback_position、token usage）。缺密钥/缺路由时 run 干净失败，绝不伪造成功。

## 5. 四处"薄代理"命题的真实弯曲

都不是缺陷声明，而是需要在改代码时知道的边界（细节与行号在 evidence §6）：

1. **404→归属派生**：Gateway 找不到 session 时会从 run/session 列表反查 owner，并以 `derived_session_owner` 伪造 `x-tinadec-principal` 再试一次。
2. **白名单投影会静默丢字段**：Core 新增而映射器未跟进的字段到不了前端（`tool_executions` 只保 4 个工具目录字段，`upstream_stream_configured` 被硬编码 `true`，`GET /model-settings/feature-flags` 返回字面 `{}`，4 个 `PUT /model-settings/*` 接受并丢弃写入）。这是"契约漂移"最可能出现的位置。
3. **`/events` 是渲染层私有 camelCase 重写**，打破了 snake_case 单一规则的例外。
4. **用户直连工具执行当前不可用**：`/api/v1/code/tools/*`、`/api/v1/tool-runtime/*` 的执行会 1:1 转发到 `:48732`，而仓库里没有这个服务。目录读的是 Core，执行落空——视图中以 `unknown`/虚线表达，未画成已验证的边。

另有两处仓库级事实值得记住：Core `Program.cs` **没有认证中间件**（身份来自 `appsettings.json` 固定 dev 三元组，JWT 校验只在 Gateway）⇒ Core 不可直接暴露公网；`StubEndpoints.cs` 的 44 条桩里 `sessions/{id}/context` 可能与 `ControlPlaneEndpoints.cs` 的已实现路由重名，遮蔽方向尚未运行时验证。

## 6. 运行时与数据拓扑要点

- 三个预留端口：Gateway `48730`、Core `48731`、Vite `5173`（Web 构建 `5174`）。Core 端口来自 `Api/Properties/launchSettings.json:8`，不是代码硬编码。
- Core 是**父进程与生命周期 owner**：按 workspace 根拉起并复用 `TinadecTools` 子进程（首次调用才启动，`StartAsync` 是 no-op）、`command_run` 落到本地 `TinadecSandbox` 账户（临时 ACL、Job Object 超时上限 30 分钟）、MCP server 子进程由工具层直接 spawn、CLI 运行时服务（ACP / `opencode serve`）由 `CliProcessManager` 拉起并只监听回环随机端口。
- 默认 SQLite 单文件 `data/tinadec.db`；PostgreSQL 需显式 `TinadecPersistence:ApplyMigrationsOnStartup=true` 以避开多实例竞态。向量：SQLite 每 project 一个 `sqlite-vec` 库，PG 走 `pgvector`。
- 桌面端另有 5 个进程（main + editor/preview/feature/search 独立渲染窗口）与 CDP `9222`；Monaco 走 `worker-proxy` 独立进程。

## 7. 读图顺序

| 视图 | 文件 | 回答什么 |
| --- | --- | --- |
| L0/L1 产品族全景 | `tinadecoffice.structurizr.dsl` → view `L0-L1 产品族全景` | 四产品与外部系统边界；默认组合 ≠ 唯一组合 |
| L1 上下文 | 同上 `CoreContext` | Core 直接面对的使用者与被替掉的 Tool Runtime |
| L2 容器 | 同上 `TinadecApp 容器` / `TinadecGateway 容器` / `TinadecCore 容器` / `TinadecTool 容器` | 每个产品的可独立交付单元与本地状态 |
| L3 组件 | 同上 `ApiComponents` / `DmaeaComponents` | Api 端点组（含仍是 501 的部分）与双层运行时内部 |
| 依赖密度 | `core-module-dependencies.dot` | 21+4 项目引用图：看清"业务模块彼此零引用"这一事实 |
| 进程拓扑 | `runtime-process-topology.dot` | 谁在什么端口、谁 spawn 谁、哪些文件与 DB 被写、哪里是死桩 |
| 关键路径 | `run-interaction-flow.dot` | 一次交互 14 步 + 模型/工具两条支路 + 事件落盘与 SSE 回放 |

Qoder 里：`.dot` 用 Graphviz 格式查看器可直接预览（Canvas 需本机 graphviz 计算布局，本机未装）；`.dsl` 用 Structurizr DSL 查看器；导出 SVG/PNG 同样需要本机 `dot`。

## 8. 假设与未验证项（不要当事实用）

1. 全部结论来自静态阅读，**没有做过任何渲染确认或端到端运行验证**；路由计数为声明数（正则），OpenAPI 唯一 path 数为 223。
2 "实际启用能力约 150–160"来自 `apps/desktop/src/api.ts:794` 的启发式注释，非本轮复核。
3. `Stub` 与 `ControlPlane` 的路由遮蔽方向需运行时验证。
4. Tool Runtime `:48732` 是否存在于其它仓库/部署环境：未知。
5. `IsPackable=true` 只证明打包边界，不等于已发布；本机 NuGet 无 `TinadecCore.*` 包。
6. MAF 1.18 具体 API 行为未反编译证实；`docs/tinadec-core-reference-decisions.zh-CN.md` 仍是 1.17.0（文档滞后）。

## 9. 若要把这张图变成"可依赖的活文档"，下一步验证任务

- 起 Core + Gateway 打一次真实 `POST /interactions` → `GET …/stream`，用事件序列证实 ⑦/⑬/⑭ 与 `awaiting_*` 豁免（一次调用即可覆盖三条 medium 断言）。
- 抓 `/api/v1/sessions` 404 路径，确认 `derived_session_owner` 是否真的会命中（E:gw→core 的例外分支）。
- 决定 `StateTransition.fs` 与 `RunStatus` 谁是唯一真相，并把另一侧改成从属或补齐 `awaiting_user`/`awaiting_delegate` 边。
- 用 `dot -Tsvg` 与 structurizr-cli 导出一次，补齐"渲染可核对"这一层证据（需要装 graphviz）。
