# TinadecOffice 架构证据索引

配套 `tinadecoffice.structurizr.dsl`、`core-module-dependencies.dot`、`runtime-process-topology.dot`、`run-interaction-flow.dot`。
每条断言给出 `path:line`；行号取自 2026-08-27 的工作树（`main`，最近提交 `f9c44c0`）。

## 置信度定义

| 级别 | 含义 | 判定方式 |
| --- | --- | --- |
| high | 源码/配置直接可读的事实（组件存在、引用方向、路由注册、迁移存在性） | 读文件或 grep 计数 |
| medium | 从结构推断但未观察到运行时的行为（顺序、并发语义、降级路径意图） | 读实现体 + 注释 + 调用点 |
| low | 只有文档或消费者侧声明支持，生产者侧未证实 | 文档与代码不一致时的文档侧 |
| unknown | 目标态、缺失实现或未在本仓库落地的依赖 | 明确标 `Target` / 虚线 |

本轮全部为**静态证据**：本机无 graphviz / java / structurizr-cli / mmdc（`dot`、`java`、`mmdc` 均 command-not-found），因此没有任何视图做过渲染确认；`.dot` 与 `.dsl` 只做过语法自查（无 `<-` 反向边、无自环、无悬空标识符、`#` 注释、`style="rounded,dashed"` 引号形式）。

## 1. 计数事实（所有视图的 label 依赖这些数字）

| 事实 | 值 | 证据 | 置信度 |
| --- | --- | --- | --- |
| Core 源项目 / 测试项目 | 21 / 4 | `TinadecCore/TinadecCore.slnx`（完整集）；根 `TinadecOffice.slnx` 只取子集，故意省略 Tenancy、AgentConfiguration、Tools、Storage.Migrations.* | high |
| 模块→模块 `ProjectReference` | **0 条** | 逐 csproj 抽取引用：所有业务模块只引用 `Abstractions`（部分加 `Contracts`/`Strategies`/`Persistence`），仅 `Runtime` 聚合 18 个、`Api`→`Runtime` | high |
| DbContext 数量 | 9 | `Memory/MemoryDbContext.cs:16`、`Lifecycle/LifecycleDbContext.cs:5`、`Tenancy/TenancyDbContext.cs:5`、`Models/ModelControlDbContext.cs:5`、`AgentConfiguration/AgentConfigurationDbContext.cs:17`、`Prompts/PromptControlDbContext.cs:5`、`Skills/IntegrationDbContext.cs:5`、`Governance/GovernanceDbContext.cs:5`、`DmaEA/AgentControlDbContext.cs:5` | high |
| Abstractions 端口文件 | 23 | `TinadecCore/Abstractions/Ports/*.cs` | high |
| Core HTTP 路由 | ≈170（13 个端点文件） | AgentConfiguration 29 / ControlPlane 28 / Storage 14 / Governance 11 / Dmaea 10 / MemoryReview 8 / UserToolAction 6 / WorkspaceSnapshot 5 / AgentPack 4 / Evolution 4 / Interactions 4 / ModelAgentControl 3 / Stub 44；`Api/Program.cs:260-272` 13 次 `Map*Endpoints()` | high（逐文件计数） |
| Core 结构化 501 | 25 处（`statusCode: 501`） | `Api/Endpoints/StubEndpoints.cs` 18 处 + `Api/Endpoints/ControlPlaneEndpoints.cs:27,33,35,36,39,40,41` 7 处；其它端点文件 0 处 | high |
| Gateway 自建 501 | **0 处** | `grep -c "501" TinadecGateway/src/index.ts` = 0；501 全部来自 Core，Gateway 只转发 | high（并据此改掉了旧断言） |
| Gateway 路由 | ≈200 声明 / 223 唯一 OpenAPI path，其中 47 条带显式 501 响应样例 | `TinadecGateway/src/index.ts` 单文件 2052 行链式应用；`src/generated/openapi.external.json` | high（数字）/medium（"实际启用 150–160"来自 `apps/desktop/src/api.ts:794` 启发式注释） |
| Gateway `.ws()` | 3 条且为死桩 | `src/index.ts:1924-1956`：只 `pub/sub`，算出的 `targetUrl` 丢弃；`createWsProxyHandlers` 在 `src/websocket.ts` 内无任何调用点 | high |
| TinadecTools 工具 | 46 个 `[ToolFunction]` id（43 字面量 + 3 个 `TOOL_ID` 常量：`Tools/Git/GitFileHistoryTool.cs:78`、`GitLogDetailTool.cs:75`、`GitLogListTool.cs:55`） | `grep -rn "ToolFunction(" TinadecTools --include=*.cs \| grep -v Generators` = 46 | high |
| 组合根顺序 | 13 个 registrar：Tenancy→AgentConfiguration→VectorStore→Lifecycle→Governance→Models→Context→Prompts→Memory→Skills→LoopGuard→Tools→DmaEA | `Runtime/TinadecCoreServiceCollectionExtensions.cs:36-48` | high |
| 组合根后置 Replace/AddSingleton | `IAuthorizationContextResolver→CoreAuthorizationContextResolver`（Replace）、`IFormalModeResolver→FormalModeResolver`、`IAgentModelResolver→AgentModelResolver`、`UserToolActionService` 三重身份、`UserToolActionRecoveryHostedService`、`ToolDispatchOptions` 从 TOML 重绑 | 同文件 `:53-75` | high |

## 2. 节点（`N:*`）

标识符与 `.dsl` / `.dot` 中的节点同名。

| ID | 名称 | 视图 | 置信度 | 证据 |
| --- | --- | --- | --- | --- |
| N:operator | 桌面/工作区操作者 | L1, flows | high | `Api/appsettings.json:24-30` 固定 dev 身份三元组 `desktop@local/default`；`Program.cs` 无认证中间件 |
| N:packAuthor | Pack 作者 | L1 | medium | `AgentPackEndpoints.cs` 4 路由 + `docs/tinadec-core-product-definition.zh-CN.md:125`（内容归 App，安装真相归 Core） |
| N:dotnetHost | .NET 宿主（嵌入） | L1 [Target] | low | `Contracts`/`Abstractions`/`Runtime` `IsPackable=true` 已证实；本机 NuGet 目录无已装 `TinadecCore.*` 包 → 未发布 |
| N:app.renderer | Desktop 渲染层 | L2, runtime | high | `apps/desktop/src/main.ts:1-45` |
| N:app.mainProc / preload / localState | Electron 主进程 / 预加载 / userData | L2, runtime | high | `electron/main.cjs:40,126-130,208,262,326,501-544`、`gatewaySettings.cjs:22-99` |
| N:app.webShim | Web 平台垫片 | L2 | high | `apps/web/vite.config.ts:77-88` 6 条 alias、`:80-84` `/gateway`→48730 |
| N:app.uie | 共享 UI 组件库 | L2 | high | `apps/TinadecUI/src/index.ts`（vite alias `@tinadec/ui`） |
| N:app.officePack | Office Agent Pack v0.2.1 | L2 | high | `apps/desktop/src/agentPacks/OfficeAgentPack/manifest.json`，digest `8110547a…962e`，14 Agent + 7 Mode，治理角色 `tool_scope: []`（`AGENTS.md` 2026-08-25/26 与 2026-08-31 条目） |
| N:gateway.routes / mapping / upstream | Elysia 路由 / 映射 / 上游客户端 | L2, runtime | high | `src/index.ts:1-23,1960-2043`、`src/mappers/*` 12 文件、`src/coreClient.ts`、`src/toolRuntimeClient.ts` |
| N:core.apiHost | TinadecCore.Api（:48731） | L2, L3, runtime | high | `Api/Program.cs:1-282`、`Api/Properties/launchSettings.json:8` |
| N:core.runtime | Runtime 组合根 | L3, deps | high | `Runtime/TinadecCoreServiceCollectionExtensions.cs`、`Runtime/TinadecCoreBuilder.cs:11-30` |
| N:core.foundation | Contracts + Abstractions | L3, deps | high | 两个 csproj 无任何框架引用；`Strategies` 为 net10.0 F# |
| N:core.dmaea / modelCtl / toolsCtl / governance / lifecycle / agentCfg / memory / tenancy / thinMods | 业务模块组 | L3, deps | high | 各模块 csproj + `*ModuleRegistrar.cs` |
| N:core.persistence | Persistence（9 上下文宿主 + provider 工厂 + 迁移编排） | L3, deps, data | high | `Persistence/DbContextMigrationParticipant.cs:12-24`、`Persistence/StorageMigration.cs:43` |
| N:core.migrations | SQLite / PostgreSQL 迁移参与者 | L3, deps | high | 两个 Storage.Migrations.* 项目；CI 用 `pgvector/pgvector:pg16` |
| N:core.sqlite / postgres / fileStore / vectorStore | 数据面 | data, runtime | high | `Api/appsettings.json:10-23`、`Persistence/LocalFileContentStore.cs`、`Persistence/PersistencePaths.cs`、`VectorStore/*` |
| N:tool.toolHost / generator / sandbox / mcpConfig | 工具层 | L2, L3, runtime | high | `TinadecTools/Program.cs:28-31,54-80,105-129,131-201,203-293`、`TinadecTools.Generators/ToolFunctionGenerator.cs`、`TinadecTools/Runtime/Sandbox/*.cs`、`TinadecTools/Tools/Mcp/*` |
| N:llmApi | 远端模型 API | L1, flows | high | `Models/ModelsModuleRegistrar.cs:47`（`ModelProvider : IModelProvider, IChatResolver`）、协议族见 `DmaEA/IAgentChatClientFactory.cs` |
| N:cliRuntimes | 本机 CLI（ACP / opencode serve） | L1, runtime | high | `DmaEA/CliRuntime`、`GET /api/v1/model-providers/cli/discover`、`POST …/cli/connect` |
| N:workspaceFs / gitRemote / mcpServers | 工作对象与外部系统 | L1, runtime | high | `TinadecTools/Tools/FileSystem/*`、`Tools/Git/*`、`Tools/Mcp/*` |
| N:toolRuntime | Tool Runtime 服务 :48732 | L1, runtime [Target] | **unknown** | Gateway 有完整转发（`toolRuntimeClient.ts:1-51`、`config.ts:72`），但全仓库无 `:48732` 监听实现（`grep "48732"` 命中 `apps/TinadecUI/package.json:10` 的 `--port 48732` 是 Vite 端口复用，不是该服务）；`docs/web-client.md` 阶段 2 列为缺口 |

## 3. 边（`E:*`）

| ID | 边 | 置信度 | 证据 |
| --- | --- | --- | --- |
| E:app→gw | 渲染层只talk Gateway，基址解析 `TINADEC_RESOLVED_GATEWAY_URL → VITE_ → TINADEC_ → 127.0.0.1:48730` | high | `src/transport/index.ts:52-74`、`src/apiBaseUrl.ts:22-39`；`electron/main.cjs:501-544` 仅在显式 server 模式注入 env |
| E:gw→core | 转发 `/api/v1/*`，注入 `x-request-id` 与固定 `x-tinadec-principal: dev@local` | high | `src/index.ts:126-135`（`requestId`、本地身份）、`src/coreClient.ts:44-56,98-140` |
| E:gw→toolRuntime | 用户直连工具传输（`/api/v1/code/tools/*`、`/api/v1/tool-runtime/*`） | **medium** | `src/toolRuntimeClient.ts:13-51` + `src/codeTools.ts:53-86,93-125`：目录从 Core `GET /tool-catalog` 读（`index.ts:903,949`），执行则 `toolRuntimeUrl(...)` 直连——目标服务不存在，路径实际不可用 |
| E:core→tool | 按 workspace 根启动/复用一个子进程，BOM-free UTF-8 行分隔 JSON | high | `Tools/TinadecToolsProcessManager.cs:25` 及 `StartAsync` 为 no-op、首次调用才拉起（medium：no-op 语义读自实现体） |
| E:dmaea→governance | 执行前 PDP + lease | high | `Abstractions/Ports/IAuthorizationService.cs`、`Governance/GovernanceService.cs:37`（`FailClosedAuthorizationContextResolver`） |
| E:tools→approval | 一次性 ActionApproval + manifest 哈希校验 | high | `Lifecycle/ToolApprovalCoordinator.cs:16`、`Tools/ToolManifestSnapshotResolver.cs:13`、`ToolInvocationScopeResolver` |
| E:runtime→persistence | 唯一共享基础设施依赖方向 | high | csproj 引用图：业务模块 → `Persistence`；模块彼此 0 引用（ Architecture.Tests 断言：`tests/TinadecCore.Architecture.Tests/ArchitectureTests.cs:42,54,75,105,123,134,157,187,202,215,227`） |
| E:persistence→sqlite/pg | `SELECT 1` 就绪探测 + EF 读写；PG 需显式 `ApplyMigrationsOnStartup` | high/medium | `Api/appsettings.json:13-23`（默认 Sqlite）；探测与后置列对账读自 `Persistence/*`（medium） |
| E:vector→sqlite-vec | 每 project 独立 `.vec.db`，路径 `data/vectors/tenants/{t}/{w}/{p}.db` | high | `VectorStore/*` + `AGENTS.md` 条目 |
| E:secret→dpapi | `ProtectedFileSecretStore` Windows CurrentUser DPAPI | high | `Persistence/ProtectedFileSecretStore.cs` |

## 4. 交互流（`flow:*`，对应 `run-interaction-flow.dot`）

| 步骤 | 断言 | 置信度 | 证据 |
| --- | --- | --- | --- |
| ①-② | 发送框模式 `plan/spec/ask/vibe/auto/agent` → `createInteraction()`；`queued\|insert\|parallel` | high | `apps/desktop/src/pages/HomePage.vue` + `composables/useHome/composerRun.ts`、`src/api.ts` |
| ③ | Gateway 对 `agent_mode`/`permission_mode` 只做枚举校验后原样转发 | high | `src/index.ts:1606` 附近 interactions 路由 |
| ④ | Core 解析顺序：显式 `mode_version_id` > `conversation.{agent_mode}` 已发布版本 > 会话既有默认，并持久化到 session | medium | `Api/Endpoints/InteractionsEndpoints.cs:120` + `AGENTS.md` 2026-08-25 条目 |
| ⑤ | `FullDuplexRunCoordinator.SubmitAsync`：client message id 幂等、`context_revision` 冲突→409、活跃 run 上限 | high/medium | `DmaEA/FullDuplexRunCoordinator.cs:72`（语义读自实现体） |
| ⑥ | 冻结运行配置 = 策略快照 + 关系化 roster + 逐 Agent 模型计划 + 工具 manifest 哈希 | high | `DmaEA/FrozenRunConfiguration.cs`、`Runtime/FormalModeResolver.cs`、`Runtime/AgentModelResolver.cs`、`Tools/ToolManifestSnapshotResolver.cs:13` |
| ⑦ | **`POST /interactions` 返回 201 JSON，不是 SSE** | high | `InteractionsEndpoints.cs`；`AGENTS.md` 同条；事件流须另开 `GET /sessions/{id}/runs/{runId}/stream`（`DmaeaEndpoints.cs:190,199`） |
| ⑧ | `EnqueueAsync → TryAcquireRunLeaseAsync → run_checkpoints`；租约 30s、扫描 2s、队列轮询 100ms | high | `DmaEA/FullDuplexRunEngine.cs:20,25,26,73,136,162` |
| ⑨-⑫ | operation（协调+规划）→ execution（spawn/lineage+预算）→ supervision → meeting 定稿；只有 meeting 直接回答用户 | high/medium | `FullDuplexRunEngine.cs`（1889 行）、`AgentInstanceService.cs`、`SupervisionAgent.cs`；分层语义另见 `DmaEA/Configuration/default-agent-runtime.toml` |
| ⑪ 分支 | 需要人 → `awaiting_user`，引擎抛 `RunAwaitingExternalDecisionException` 并释放租约 | high | `FullDuplexRunEngine.cs:1887`；`Abstractions/Ports/ILifecycleManager.cs:210-224` 枚举含 `AwaitingApproval/AwaitingDelegate/AwaitingUser` |
| ⑬ | 事件落 `events/{runId}.events.jsonl` + `event_index`/`control_event_index` 字节偏移；`run_stream` + `run_stream_cursors` | high | `Lifecycle/StorageLifecycleService.cs:295-313`（物化按偏移随机读）、`:813-817` |
| ⑭ | `FollowEventsAsync` 2s 水位 → Gateway `proxySse`（`Last-Event-ID`→`after_seq`，不缓冲）→ `useRunStream` 按 `run_id+seq` 去重 | high | `DmaeaEndpoints.cs:196`、`coreClient.ts:162-202`、`transport/index.ts` |
| 工具支路 | `ToolDispatcher.PrepareAsync` 先落 `tool_executions` → 快照 → PDP → lease → 审批 → 暂停；批准经 `ControlPlaneService.DecideApproval` → `engine.EnqueueAsync(runId)` 唤醒同一 run → `ResumeAsync` → 子进程 | high/medium | `Tools/ToolDispatcher.cs:25`、`Runtime/ControlPlaneService.cs:25-45`、`Lifecycle/RunRecoveryHostedService.cs` 注释与 `AGENTS.md` 2026-08-18 条目 |
| 模型支路 | `IAgentModelResolver`（`inherit\|route\|fixed`）→ `ModelProvider.ResolveChatAsync` → 协议工厂 → 写 `model_invocations` 归因行；缺密钥即失败，绝不伪造成功 | high | `Runtime/AgentModelResolver.cs:21-33`、`Models/ModelsModuleRegistrar.cs:47`、`DmaEA/ModelInvocationChatFactory.cs`、`DmaEA/AgentControlDbContext.cs:5` |
| 恢复语义 | 两个机制并存：引擎每 2s 扫 `ListLeaseEligibleRunsAsync` 重排非终态且非 `awaiting_*`、过准入宽限期的 run；`RunRecoveryHostedService` 只在启动时把孤儿 run 标 `failed` 并写 `run.recovered`（不续跑） | high | `StorageLifecycleService.cs:344-353`、`RunRecoveryHostedService.cs:28-46` |

## 5. 状态机与契约

- `RunStatus` 枚举 12 值：`Planning, Understanding, Executing, Replanning, AwaitingApproval, AwaitingDelegate, AwaitingUser, Paused, Reviewing, Completed, Failed, Cancelled`（`Abstractions/Ports/ILifecycleManager.cs:210-224`）。`finalizing` 不存在。
- F# 纯策略 `Strategies/StateTransition.fs:10-46` 只枚举 10 个规范态 + `pending/running` 兼容别名，外加三条万能边（任意态→`paused`/`failed`/`cancelled`）。**它不包含 `awaiting_user` / `awaiting_delegate`**，也没有 `planning` 之外进入 `awaiting_*` 的边。
- 两者关系（medium）：持久 run 状态实际以 `RunStatus` 字符串为准（`StorageLifecycleService` 直接比较 `"awaiting_user"` 等），`validateTransition` 是独立可复用策略，未见 run 引擎调用它 ⇒ `run-interaction-flow.dot` 用 `awaiting_user`/`replanning` 标签是对的，但"10 态单一真相"这一说法过强，已在视图与理解文档降级为"两套并存"。

## 6. 已知桩与缺口（图中以 `unknown`/`Target`/红色虚线表达）

| 项 | 观察 | 置信度 |
| --- | --- | --- |
| SkillProvider 空桩 | `Skills/SkillsModuleRegistrar.cs:38-52` 两个方法分别返回 `Array.Empty` / `null` | high |
| `ToolHandlerBase` 无实现 | `grep ": ToolHandlerBase" TinadecTools` = 0，抽象基类与 `IToolHandler`（`ToolFunctionGenerator.cs:19` 注释）目前只服务静态注册表 | high |
| Core 侧 MCP 直通死代码 | `Tools/McpAgentToolProvider.cs:18,:83` 自述"当前无 DI 注册点，保留待 MCP 端点接入"；`StubEndpoints.cs` 中 `/api/v1/mcp/servers` 等仍是 501 | high |
| `PromptControlDbContext` / `IntegrationDbContext` 无迁移 | 两个 `Storage.Migrations.*` 项目里无对应 participant，靠 `DbContextSchemaBootstrapper.EnsureTablesAsync`（`GenerateCreateScript` + `IF NOT EXISTS`）兜底 | high |
| 租户隔离只在应用层 | 全仓库无 `HasQueryFilter` | high |
| Core 无认证中间件 | `Program.cs` 全文无 `UseAuthentication`/`UseAuthorization`；身份来自 `appsettings.json:24-30` dev 三元组 ⇒ Core 不可直接暴露公网（`AGENTS.md` Gateway 条目同此结论） | high |
| Stub 与 ControlPlane 路由遮蔽 | `StubEndpoints.cs:225-226` 的 `/api/v1/sessions/{id}/context` 可能与已实现的 `ControlPlaneEndpoints.cs:11,18` 重复；ASP.NET 按注册顺序取首个匹配，而 `MapStubEndpoints()` 在 `Program.cs:265` 晚于 `MapControlPlaneEndpoints()` ⇒ 需运行时验证，未在视图里画成事实 | unknown |
| 会话 404 回退会派生租户 | `src/index.ts` `deriveSessionOwner` 从 run/session 列表反查 `tenant:subject` 并伪造 `x-tinadec-principal`，是"无状态"命题上的一处真实弯曲 | high |
| Desktop 有损投影 | `GET /events` 经 `eventsMapper` camelCase 重写且硬编码 `cud_type`；`/model-settings` 合成空壳、`GET /model-settings/feature-flags` 返回字面 `{}`；4 个 `PUT /model-settings/*` 静默丢弃写入字段 | high |
| `/api/v1/permissions/*` | 仅 `POST …/preview` 真实转发，其余 CRUD 为本地 501（`index.ts:1152-1184`）——"仅代理 Core governance"的说法对该前缀只在 preview 上成立 | high |
| Web 端 MCP 文档漂移 | `apps/web/docs/web-client.md:229,236` 宣称 `mcp.ts` 已就绪，实际 `src/platform/desktop-shim.ts:217-222` 返回固定 `status:'error'`；`apps/desktop/src/mcp.ts:13` 注释同样陈旧 | high |
| 未跟踪构建产物 | `apps/manger`、`apps/tui` 是 Go 编译输出，非源码成员 | high |

## 7. 阅读顺序与再生成

1. 先读 `tinadecoffice.architecture-understanding.md`（边界、所有权、读图顺序）。
2. 本文件用于核对任何一条断言的出处。
3. 视图：`tinadecoffice.structurizr.dsl`（L0–L3）→ `core-module-dependencies.dot`（依赖密度）→ `runtime-process-topology.dot`（进程/端口/落盘）→ `run-interaction-flow.dot`（一次交互）。

再生成核对命令（全部只读）：

```bash
cd C:/git/agent/TinadecOffice
# 模块引用图（替换 DOT 的边集）
grep -H "ProjectReference" TinadecCore/*/*.csproj
# 路由与 501 计数
grep -rhoE '"(GET|POST|PUT|DELETE|PATCH) /api/v1/[^"]*"' TinadecCore/Api/Endpoints | sort | uniq -c | wc -l
grep -rc "statusCode: 501" TinadecCore/Api/Endpoints/*.cs
# 工具数
grep -rn "ToolFunction(" TinadecTools --include=*.cs | grep -v Generators | wc -l
# DbContext 清单
grep -rn "class .*DbContext : DbContext" TinadecCore --include=*.cs
```
