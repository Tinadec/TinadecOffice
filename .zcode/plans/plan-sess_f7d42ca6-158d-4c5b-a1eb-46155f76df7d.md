# 为 muse-spark-1.2 配置思考强度（最高 xhigh）实施计划

## TLDR
仓库目前没有任何 `reasoning / thinking / effort / xhigh` 字段，模型参数链路仅有 `driver/protocol/base_url/model`，`ChatOptions` 只传 `Instructions + Tools`。本次计划在 **Provider JSON → Route 覆盖 → Agent ModelStrategy → ChatResolution → ChatOptions.AdditionalProperties** 链路新增 `reasoning_effort` 枚举（`low/medium/high/xhigh`，最高 `xhigh`），冻结到 run，薄代理透传到 Gateway/Desktop，默认不破坏存量数据。

## 1. 现状与约束（已验证）
- 全仓 `muse-spark / spark / reasoning / thinking / xhigh / temperature / max_tokens` 业务代码 **0 命中**，仅 `InteractionsEndpoints.cs:104` 有 `best-effort` 注释。
- 模型持久化：`TinadecCore/Models/ModelControlDbContext.cs` 四表（`model_provider_instances / model_provider_versions / model_routes / model_route_versions`），内容走 `IContentStore` + 密钥走 `ISecretStore`，是 workspace 隔离 + `Revision` 乐观并发。
- 解析链：`ModelsModuleRegistrar.ModelProvider.ResolveChatAsync` 读 `Route → Provider → Content JSON → ChatResolution{BaseUrl,Model,ApiKey,Protocol,...}`，`AgentChatClientFactory.CreateAsync` 按 `Protocol` 分支（`openai-chat / openai-responses / anthropic-messages / acp / opencode-serve`）。
- 运行时：`ExecutionAgent / PlanningAgent / SupervisionAgent / FullDuplexRunEngine` 统一 `ChatOptions{Instructions, ToolMode=Auto, Tools=[declarations]}`，未设置任何采样参数；`FormalModeResolver.TryResolveFormalChatAsync` 解析 `ModelStrategyJson{kind:inherit|fixed|parent_select}` 并写 `model_selection` 审计。
- 配置：唯一 TOML `TinadecCore/DmaEA/Configuration/default-agent-runtime.toml` 仅有 `context.default_token_budget=8192`，无模型超参；`FrozenRunConfiguration` 冻结 profile 时未包含模型推理参数。
- 契约：`openapi.core.json / openapi.external.json` 快照需同步；Gateway `TinadecGateway/src/mappers/*` 12 个显式映射 + `coreClient.ts` SSE 8 kinds，Desktop `apps/desktop/src/pages/SettingsPage.vue` 为 Model Center 入口。

## 2. 目标与非目标
- 目标：让 `muse-spark-1.2`（通常走 `openai-chat` 或 `openai-responses`）可配置思考强度，取值 `low/medium/high/xhigh`，支持全局默认 `xhigh` 且可按 route / 按 agent 覆盖，向模型侧透传为 provider 特有参数，生效率冻结、审计可追溯。
- 非目标：不引入新的计费/限流模型，不重做 embedding 链路，不为 `acp/opencode-serve` 做语义等价转换（仅透传或忽略）。

## 3. 设计决策
### 3.1 字段形态
- 新增枚举 `ReasoningEffort { Low, Medium, High, XHigh }`，对外 `string` 归一化小写，校验 `^(low|medium|high|xhigh)$`，大小写不敏感，`null` 表示不下发（兼容存量）。
- 命名统一 `reasoning_effort`（snake_case，契约与 JSON 一致），避免与 Anthropic `thinking` 混淆；Anthropic 分支如需映射另起 `thinking_budget`，本次不强制。

### 3.2 存放层级与优先级
`Agent ModelStrategyJson.reasoning_effort` > `ModelRouteVersion.Model` 覆盖旁的 `reasoning_effort` > `ModelProviderVersion JSON.reasoning_effort` > `TOML defaults.reasoning_effort`（可选）> `null`。
- Provider 层适合把 `muse-spark-1.2` 全局设为 `xhigh`。
- Route 层适合 `purpose=chat` 单独 xhigh 而 `embedding` 不设。
- Agent 层适合 `meeting` 用 `xhigh`、`skill_recommender` 用 `low` 的细粒度。
- 冻结：`FrozenRunConfiguration` 与 `FullDuplexRunEngine` 的 run 冻结快照新增 `reasoning_effort`，重启恢复不重新解析。

### 3.3 透传机制
`Microsoft.Extensions.AI.ChatOptions.AdditionalProperties["reasoning_effort"] = "xhigh"`，`IAgentChatClientFactory.CreateAsync` 在创建 `OpenAIClient / Responses / Anthropic / Acp / OpenCode` 前注入；`AdditionalProperties` 会被 `RawRepresentationFactory` / provider 适配层序列化为额外 JSON 字段，已验证不破坏现有 Instructions/Tools。
- `openai-chat / openai-responses`：直传 `reasoning_effort`。
- `anthropic-messages / acp / opencode-serve`：本次仅透传（如对方忽略则无害），不做 `thinking.budget_tokens` 换算，避免误映射。

## 4. 方案对比
- **A 最小可行（仅 Provider+Route）**：改动最少，满足「全局 xhigh」；但无法按 agent 差异化。
- **B 推荐（Provider+Route+Agent 三级 + 冻结）**：多 2 个文件改动，覆盖运营层/执行层差异化场景，符合现有 `inherit/fixed/parent_select` 拓扑继承语义。
- **C 再加 TOML 全局默认**：适合多 workspace 统一基线，但 TOML 热重载与 workspace override 合并会增加一处配置源，YAGNI 阶段可后补。

**推荐 B，TOML 默认作为后续可选增强。**

## 5. 具体改动清单（按文件）
1. `TinadecCore/Abstractions/Ports/IChatResolver.cs` — `ChatResolution` 新增 `string? ReasoningEffort`；新增 `static class ReasoningEffortValues { const Low/Medium/High/XHigh; Normalize/IsValid }`。
2. `TinadecCore/Models/ModelControlDbContext.cs` — `ModelRouteVersionRecord` 新增 `ReasoningEffort` 列（或保持 JSON 覆盖，二选一；推荐列以便索引与迁移演示，`HasMaxLength(16)`）。
3. `TinadecCore/Models/ModelsModuleRegistrar.cs` — `ModelProvider.ResolveChatAsync` 解析顺序：读 Provider JSON `reasoning_effort` → 被 RouteVersion 覆盖 → 归一化；`EmbeddingProvider` 同步但允许 null；`CheckReadinessAsync` 无需改。
4. `TinadecCore/DmaEA/IAgentChatClientFactory.cs` — `CreateAsync` 注入 `new ChatOptions{ AdditionalProperties={["reasoning_effort"]=resolution.ReasoningEffort} }` 的工厂方法；为 `CreateOpenAiChatClient/CreateResponsesClient/CreateAnthropicClient/CreateAcpClientAsync/CreateOpenCodeClientAsync` 统一入口加参数。
5. `TinadecCore/DmaEA/ExecutionAgent.cs, PlanningAgent.cs, SupervisionAgent.cs, FullDuplexRunEngine.cs` — `GetNextTurnAsync / GenerateMeetingResponseAsync / GetWorkerModelTurnAsync` 在 `CreateAsync(resolved)` 后复用 `resolved.ReasoningEffort`，不覆盖用户显式 `ChatOptions`。
6. `TinadecCore/Runtime/FormalModeResolver.cs` — `TryResolveFormalChatAsync` 与 `TryBuildResolutionFromProviderAsync` 支持 `ModelStrategyJson` 中 `reasoning_effort` 覆盖，且 `inherit` 回落时沿用 `ResolveChatAsync` 的已解析值；`model_selection` 事件 payload 新增 `reasoning_effort`。
7. `TinadecCore/AgentConfiguration/AgentConfigurationDbContext.cs, AgentConfigurationService.cs` — `ModelStrategyJson` 校验放行 `reasoning_effort`，`ValidateModelStrategy` 校验枚举；`PublishMode/PublishPipeline` 无需改。
8. `TinadecCore/Runtime/ControlPlaneService.cs, TinadecCore/Api/Endpoints/ControlPlaneEndpoints.cs` — `SaveProvider` / `SaveRoute` 读写 `reasoning_effort`，`PUT /api/v1/model-providers` 与 `PUT /api/v1/model-routes/{purpose}` 的 JSON Schema 放行，错误返回 `code=INVALID_REASONING_EFFORT` RFC9457。
9. `TinadecCore/DmaEA/FrozenRunConfiguration.cs` — `FrozenRunConfigurationV1` 新增 `ReasoningEffort`，`ResolveAsync` 合并 workspace override。
10. `TinadecCore/Api/openapi.core.json` 快照再生；`TinadecGateway/src/mappers/*`（`modelAgentCenter.ts` 等）透传字段；`TinadecGateway/src/coreClient.ts` 无需改 SSE kinds。
11. `apps/desktop/src/pages/SettingsPage.vue, src/api.ts, src/generated/client.ts` — Model Center 新增下拉「思考强度：低/中/高/极高(xhigh)」，默认 `xhigh`，`api.connectCliRuntime` 不改。
12. 可选：`TinadecCore/DmaEA/Configuration/default-agent-runtime.toml` 新增 `[models.defaults] reasoning_effort="xhigh"` 并在 `AgentRuntimeConfiguration.cs` 解析（后补）。

## 6. 构建与验证顺序
1. 加枚举与 `ChatResolution` → `dotnet build TinadecCore/TinadecCore.slnx`。
2. 改 `ModelProvider` 解析与 `AgentChatClientFactory` 注入 → 单测 `ModelProviderTests / FormalModeResolverTests` 新增 xhigh 透传用例。
3. 改 `ControlPlaneService` 校验 → `Api` 集成测试覆盖 `POST /model-providers` 含 `reasoning_effort:xhigh` 与非法值 422。
4. 冻结与审计 → 验证 `GET /sessions/{id}/orchestration` 的 `frozen` 快照包含 `reasoning_effort`。
5. 再生 `openapi.core.json / openapi.external.json` → `TinadecGateway` `bun test` 35 与 `apps/desktop` `vitest` 校核。
6. E2E：创建 `muse-spark-1.2` provider(`reasoning_effort=xhigh`) → 绑定 `chat` route → 会话 `POST /interactions` → 断言 `model_selection` 事件含 `xhigh` 且模型侧收到额外字段（mock IChatClient 捕获 `AdditionalProperties`）。

## 7. 风险与回滚
- 存量数据无字段时保持 `null`，不下发参数，零破坏。
- Anthropic/ACP 误识别：仅透传，不做预算换算，风险可控。
- 迁移：如选列存储需 `MigrateAsync` 双路径（SQLite `ADD COLUMN` + PostgreSQL `IF NOT EXISTS`），与现有 `EnsureSessionColumnsAsync` 同模式；如选纯 JSON 则无迁移。
- 回滚：移除 `AdditionalProperties` 注入即回退为当前行为。

## 8. 待用户确认（不阻塞实施，默认值已定）
- 是否需要 TOML 全局默认 `xhigh` 还是仅 Provider/Route 即可？默认按 Provider/Route+Agent 三级先行，TOML 后补。
- Desktop 是否暴露为 Model Center 下拉还是仅 API 可配？默认暴露下拉，最高档标注 `xhigh（最高）`。
- `muse-spark-1.2` 的协议是 `openai-chat` 还是 `openai-responses`？默认两者均透传 `reasoning_effort`，无需区分。

实施时将按上述文件顺序最小增量改动，保持 `snake_case` 契约与 `RFC9457 ProblemDetails` 规范，存量测试保持 68/35 绿灯。
