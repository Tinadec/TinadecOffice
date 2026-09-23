# GATEWAY KNOWLEDGE

**Last Updated:** 2026-09-23
**Last Updated By:** 市场安装面从"Core 只有读"变成"Core 有写口而网关仍然只是代理"：新增四条同名纯代理（`POST /market/catalog/{id}/install-preview`、`POST /market/installations/{id}/uninstall-preview`、`POST /market/install-proposals/{id}/apply`、`GET /market/installations`）、七个错误码进 `ALLOWED_CODES`白名单、四个 externalJsonResponse 类型化 200。关键仍是**语义而不是转发**：`market_install_proposal_stale` 必须是 412 而不是被压成 `conflict`——412 告诉客户端"重新预览"，`conflict` 告诉它"过一会儿重试同一件事"，而一件永远不会成功的重试是网关能造成的最安静的错误。外部契约的 market 路径集合现在用 deep-equal 钉死（M1 的教训是五条幻影路由活在 Core 从未实现的契约里，">= N 条"的断言抓不住第 N+1 条）。上一批是市场读面从"代理到 Core 桩"变成代理到真实现（六条 + 五个类型化 200），并修掉网关自己一条静默缺陷：catalog 代理曾发 `query`/`sourceId`，Core 读的是 `q`/`source_id`，两个名字都不报错都只是没人读，于是搜索与按源筛选穿过网关时静默失效。再上一批是 TinaChat 契约投影实际为 17 路径/24 操作，代理测试改为精确计数钉住
**Last Verified Commit:** `e0c0e08` 之后的工作树：`bun test src` `BUN_EXIT=0`、**72 pass / 0 fail**（11 文件；`src/marketProxy.test.ts` 9 例，含"412 过期提案保住自己的码"与"market 路径集合与 Core 实现的恰好一致"两例）。`tests/__snapshots__/openapi.external.json` 随本批再生成（+319/−1），外部快照后须再跑 `npm run generate:client -w @tinadec/desktop`（本批 `schema.d.ts` +128/0，`GEN_EXIT=0`）。Core 同批整解 `GATE_EXIT=0`、`Api.Tests` 507/507。上一批是 M1 市场读面（70/70）。
**Branch:** Everything-changed

## OVERVIEW
独立 Bun 包，薄代理 BFF/API 层。使用 Bun 运行时，拥有独立的 `bun.lock`、启动、测试和部署流程，脱离 Electron 与根 npm workspace。

Gateway 自身不执行文件、Git、Shell、PTY 或 MCP 操作，只负责：
- **鉴权**：云端模式支持 API Key / JWT / 租户上下文
- **BFF 组合**：Model/Agent Center 聚合视图
- **协议转换**：HTTP/JSON、SSE、WebSocket、流式 HTTP
- **流式转发**：代理到 Core 和 Tool Runtime

## ARCHITECTURE

### 协议分层
| 协议 | 用途 | 实现位置 |
|------|------|----------|
| HTTP/JSON | 普通命令与查询 | `index.ts` 路由 + `coreClient.ts` / `toolRuntimeClient.ts` |
| SSE | 统一事件流 | `index.ts` SSE 路由 + `proxySse()` |
| WebSocket | 终端、调试、协作 | `index.ts` `.ws()` 路由 + `websocket.ts` |
| 流式 HTTP | 大文件与日志 | `index.ts` 流式路由 + `streaming.ts` |

### 部署模式
| 模式 | 监听地址 | 认证 | 租户 | 反向代理 | 横向扩展 |
|------|----------|------|------|----------|----------|
| 本地（默认） | `127.0.0.1` | 无 | 无 | 无 | 单实例 |
| 云端 | `0.0.0.0` | API Key / JWT | 支持 | 信任 X-Forwarded-* | 无状态代理 |

### 连接拓扑
```
Desktop ──HTTP/SSE/WS──> Gateway ──HTTP/SSE/WS──> Core
                         Gateway ──HTTP/SSE/WS──> Tool Runtime（用户工具传输面）
                         Core ←──HTTP──→ Tool Runtime
```

Gateway 是北向无状态门面。用户在 Desktop 触发的工具请求可以通过 Gateway 传输到 Tool Runtime；Gateway 不读取审批、不计算参数哈希、不选择工具、不保存状态。智能体的治理执行仍走 Core 的 run-scoped 工具路径。`/api/v1` 是当前唯一公开 HTTP API 前缀；没有 `/v2`、legacy 或兼容别名，破坏性变更直接更新 v1 并同步文档。

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| 运行配置 | `src/config.ts` | 部署模式、端口、Core/Tool Runtime URL、认证、CORS |
| 服务器路由 | `src/index.ts` | Elysia app，CORS，认证中间件，`/api/v1/*`，WebSocket，流式 |
| Core 代理 | `src/coreClient.ts` | `coreUrl()`，JSON 代理，SSE 代理，流式代理 |
| TinaChat 代理 | `src/tinaChatRoutes.ts`, `src/contracts/tina-chat.openapi.json` | 17 路径/24 操作来自 Core 生成契约；包含 observer 管理员读取，保留 Cache-Control/正文/query/header/状态，不计算通信权限 |
| Tool Runtime 代理 | `src/toolRuntimeClient.ts` | `toolRuntimeUrl()`，JSON 代理，SSE 代理，流式代理 |
| 认证中间件 | `src/auth.ts` | API Key / JWT HS256 验签（WebCrypto），租户上下文，反向代理头 |
| 请求上下文 | `src/headers.ts`, `src/auth.ts` | 只处理请求 id、认证和租户头；授权事实由 Core 或 Tool Provider 产生 |
| WebSocket 代理 | `src/websocket.ts` | 路由表，目标 URL 构建，消息透传 |
| 流式 HTTP 代理 | `src/streaming.ts` | 大文件/日志流式透传 |
| Model/Agent 配置代理 | `src/index.ts` | 版本化 provider/route/Agent/Mode/Prompt/default 路径；旧 overview 路由已删除，`PUT /api/v1/agents/:id/runtime-binding` 仍是当前代理（`src/index.ts:1523` → Core `AspNetCore/Endpoints/AgentConfigurationEndpoints.cs:21`）。 |
| Agent Pack 代理 | `src/index.ts`, `src/runtimeProxy.test.ts`, `tests/__snapshots__/openapi.external.json` | 四条显式薄代理；保留 ETag、`If-Match`、`Idempotency-Key` 和 Core ProblemDetails code。 |
| Code tools 传输 | `src/index.ts`, `src/toolRuntimeClient.ts` | Desktop 工具目录代理 Core，用户执行请求原样转发 Tool Provider |
| MCP 读代理 | `src/index.ts` | 只有 Core 实现的两条 GET；`source` 字段逐字透传，网关不判断"连不连得上"（历史上这里还有 5 条 Core 从不存在的路由，见 `DELETED FILES`） |
| 市场读代理 | `src/index.ts`, `src/marketProxy.test.ts` | 六条 `/api/v1/market/*`（sources GET/POST/PATCH/DELETE、refresh、catalog 两条）纯透传，五类信封带 `detail.responses` 类型。**目录查询参数名按 Core 的拼写转发**（`q`/`source_id`/`limit`/`offset`）：这里曾发 `query` 与 `sourceId`，Core 从来没读过这两个名字，所以搜索框在真机上什么都不会筛而每个 mock 过测试的调用方都是绿的 |
| 测试 | `src/coreClient.test.ts`, `src/modelAgentCenter.test.ts`, `src/runtimeProxy.test.ts` | Bun test |

## CONVENTIONS

### 包管理
- 使用 **Bun** 作为运行时和包管理器，拥有独立的 `bun.lock`
- 从根 npm workspace 移除，不再通过 `npm -w` 调用
- TypeScript 使用 `bundler` 模块解析，`@types/bun` 类型
- `"type": "module"` ESM

### 环境变量
| 变量 | 默认值 | 说明 |
|------|--------|------|
| `TINADEC_GATEWAY_MODE` | `local` | 部署模式：`local` 或 `cloud` |
| `TINADEC_GATEWAY_PORT` | `48730` | 监听端口 |
| `TINADEC_CORE_URL` | `http://127.0.0.1:48731` | Core 服务 URL |
| `TINADEC_TOOL_RUNTIME_URL` | `http://127.0.0.1:48732` | 外部 Tool Runtime 转发地址；本机没有这个 HTTP 服务——工具宿主由 Core 按 `TinadecTools:ExecutablePath` 自动探测（content root → `TinadecTools/bin/{Debug\|Release}/net10.0/`）拉起的子进程承载，没有固定端口 |
| `TINADEC_GATEWAY_AUTH_REQUIRED` | `true`（云端） | 是否必须认证 |
| `TINADEC_GATEWAY_JWT_SECRET` | — | JWT 验证密钥 |
| `TINADEC_GATEWAY_API_KEY` | — | API Key |
| `TINADEC_GATEWAY_CORS_ORIGINS` | — | 额外 CORS 来源（逗号分隔） |
| `TINADEC_GATEWAY_TIMEOUT_MS` | `120000` | 请求超时 |

### Gateway 薄代理原则
- Gateway 不存储任何业务状态
- Gateway 不执行文件、Git、Shell、PTY 或 MCP 操作
- Gateway 只代理请求，不实现业务逻辑
- 所有工具执行请求代理到 Tool Runtime
- MCP 连接管理由 Tool Runtime 负责

### 全双工运行期代理
- **TinaChat（2026-09-18）**：`registerTinaChatRoutes` 挂载 `/api/v1/tina-chat`，参与现有认证与转发头路径，使用 `proxyRaw` 保留 Core JSON/ProblemDetails/204/ETag。OpenAPI `detail` 只用于文档，不新增响应裁剪或本地业务验证。`scripts/sync-tina-chat-contract.mjs` 从 Core snapshot 提取相应路径及递归 schema，生成投影随 Gateway 提交以支持独立构建；`bun run generate:tina-chat-contract` 生成、`bun run check:tina-chat-contract` 验证漂移，外部快照后在根目录执行 `npm run generate:client -w @tinadec/desktop`。普通 session interaction 也可能返回 TinaChat 隔离拒绝；通用 `errorMapper` 必须保留公开 `tina_chat_input_locked`，不能降级为 `conflict`。TinaChat 自身没有群消息 SSE/WS 推送，收件箱为游标拉取；执行输出复用 Core run stream。智能体侧的唤醒与结果回群发生在 Core（`tina_chat_wakes` + Core 调度循环），不经过 Gateway，Gateway 不排空、不定时、不持任何通信状态。`src/tinaChatRoutes.test.ts` 现按契约枚举每一条操作逐条验证透传，并把路径数与操作数钉成 17/24——投影缩水会直接失败，不再只检查下限。
- **市场读面（2026-09-23，#36 / M1）**：`/api/v1/market/*` 从"代理到 Core 桩"变成代理到真实现
  （Core 侧记录见 `TinadecCore/AGENTS.md` 的 THE MARKET PANEL WAS POINTING AT A SURFACE THAT WAS NEVER BUILT 段）。
  网关只做三件事：改路径参数名（`{sourceId}`/`{catalogId}`）、按 Core 的拼写转发查询串、把六个 200 钉上类型。
  **本批顺手修掉一条自己的缺陷**：catalog 代理曾把 `query` 与 `sourceId` 发给 Core，而 Core 读的是
  `q` 与 `source_id`——两个名字都不报错，都只是没人读，所以搜索与按源筛选穿过网关时静默失效。
  现在 `marketProxy.test.ts` 断转发串里必须出现 `q=`/`source_id=`，并**断 `query=` 与 `sourceId` 不出现**。
  `errorMapper` 的 `ALLOWED_CODES` 补六个市场码（`market_source_not_found`/`market_entry_not_found`/
  `market_source_exists`/`market_source_disabled`/`unsupported_market_source_kind`/`invalid_market_source`）：
  漏在白名单外会被压成 `conflict`，把"这个 kind 没有 adapter"说成"稍后重试"。
  `DELETE /market/sources/{id}` 成功回 204 无体，因此它是六条里唯一没有响应 schema 的一条。
  契约：`tests/__snapshots__/openapi.external.json` 已再生成；`apps/desktop/src/generated/schema.d.ts` 同一提交内重生成。
- **市场安装面（2026-09-23，#37 / M2）**：同一批里 `/api/v1/market/*` 长出四条**写**路由——
  `POST catalog/{catalogId}/install-preview`、`POST installations/{installationId}/uninstall-preview`、
  `POST install-proposals/{proposalId}/apply`、`GET installations`。网关仍然只做透传：改路径参数名、贴类型、保留状态码与机器码，
  **不在网关侧生成或修改提案**（提案的字节由 Core 冻结，任何在这里重写 payload 的写法都会让"批准的就是落盘的"这句话失效）。
  `ALLOWED_CODES` 同批补上安装族的码（`market_install_not_expressible`/`market_install_target_unresolved`/
  `market_install_proposal_stale`/`market_install_proposal_not_found`/`market_installation_not_found`/
  `market_install_project_not_found`/`market_source_in_use`）——白名单外会被压成 `conflict`，
  而 **412 `..._proposal_stale` 被压成就错了**：客户端唯一的正确后续动作是"重新预览"，说成"稍后重试同一请求"是把用户引回同一次失败。
  新增两例断言这件事（`marketProxy.test.ts`：412 保码、409 带原因原文穿过），并把"契约里 market 路径集合"从五条深比较扩成九条——
  该用例刻意用深等于而不是下限，因为 M1 之前的教训正是契约里活着五条 Core 从不实现的幽灵路由。
  仍**没有**代理的路由：`/api/v1/extensions/*` 七条（Core 那侧一直回 501，M2 之后桌面也不再打它们；技能安装属 M3）。
- **终端会话路由 (2026-08-31)**：`GET /api/v1/terminals`、`POST /api/v1/terminals/:terminalSessionId/stdin`、`POST /api/v1/terminals/:terminalSessionId/kill` 是 Core 的纯透传（Tags: Terminal）。终端实时输出走既有 `GET /api/v1/runs/:runId/stream` SSE 代理，不需要单独的 WS 通道；`/ws/terminal` 无效桩仍未启用。openapi.external.json 快照已随新路由再生成（快照测试已修复为「先写后断言」，漂移会重新生成文件并由 `git diff --exit-code` 把关）。
- `POST /api/v1/sessions/{sessionId}/invoke-stream` **已退役**（`src/index.ts:540` 起不再注册，返回 404）；当前入口是 `POST /api/v1/sessions/{sessionId}/interactions`（`interactionsMapper` 只做薄枚举校验），运行输出经 `GET /api/v1/runs/{runId}/stream` 读取。
- `POST /api/v1/sessions/{sessionId}/interactions` 同样原样透传；`interactionsMapper` 只做薄枚举校验（`dispatch_mode`、可选 `agent_mode` = plan|spec|ask|vibe|auto|agent），解析与持久化属于 Core。`sessionMapper` 必须保留 Core 拥有的会话绑定字段：`mode_version_id`、`meeting_model_override`（结构化 `{provider_instance_id, model}`，Desktop 依赖它们感知当前模式；旧自由文本模型字段与分散 provider 字段已于 2026-08-27 重构删除）。
- **自由对话与会话迁移 (2026-09-10)**：Core 支持无 `project_id` 的自由对话会话（合成 manifest 只含 Core 自有 `create_workspace` 虚拟工具）；`POST /api/v1/sessions/:sessionId/migrate` 是对应的薄代理（`target_project_id` 或 `project_name`+`project_path`，Core 端 find-or-create 项目并原子迁移会话），openapi.external.json 快照与 Desktop generated client 已同步再生成。
- **自由对话契约细节（修复 2026-09-10）**：`POST /api/v1/sessions` 的 body schema 是 `project_id: t.Optional(t.String())` —— 曾为必填 `t.String()`，无项目的自由对话请求会在到达 Core 前被网关 400 拒绝；客户端省略该键而不是发送 `null`。`sessionMapper` 对无项目会话返回 `project_id: null`（**不再**强转 `''`：空串能满足真值判断，却会让 `=== projectId` 之类的严格比较失配，Desktop 曾因此重复建会话）；对应地 `externalDtoOpenApi.ts` 的 `Session.project_id` 声明为 `nullable: true`，openapi.external.json 与 `apps/desktop/src/generated/schema.d.ts` 均已重生成。`composerModeProxy.test.ts` 钉住无项目返回 `null` 的映射。
- `GET /api/v1/agent-modes?application_mode=` 直接读取 Core 的可用模式；`im`/`hub` 是当前内置别名，解析属于 Core。旧 `GET /api/v1/application-modes` TOML 投影与 `PUT /api/v1/agents/:agentId/mode` 代理已删除（2026-08-27 模型与智能体控制面重构）。
- Run 控制与运行期投影均为纯 Core 代理：`POST /api/v1/runs/{runId}/control`、`GET /api/v1/runs/{runId}/orchestration`、`GET /api/v1/runs/{runId}/agent-lineage`、`GET /api/v1/sessions/{sessionId}/context-versions`。
- `GET /api/v1/model-providers/cli/discover` 与 `POST /api/v1/model-providers/cli/connect` 为纯 Core 代理（CLI 运行时发现与连接，见 Core `ControlPlaneService`）。
- `POST /api/v1/model-providers/:providerInstanceId/models/refresh` 为纯 Core 代理（模型发现，canonical 路径；Core 从 provider 配置读取 base_url/api_key 拉取远端 `/models`，OpenAI 兼容走 Bearer、Anthropic 走 x-api-key）。
- `GET /api/v1/health` 降级契约（2026-08-28 服务自发现）：Core 网络不可达（`CORE_UNREACHABLE`）时返回 503 + `{ gateway: 'ok', core_status: 'unreachable', mode, core_url, tool_runtime_url }`（Gateway 自身健康、上游离线），Desktop 主进程据此把"可达但 Core 离线"的 Gateway 列入发现清单；Core 正常时返回 200 并携带 `core_status: 'ready'`，其余错误仍走 ProblemDetails 映射。
- `GET /api/v1/agent-packs`、`GET /api/v1/agent-packs/:packId`、`POST /api/v1/agent-packs/install-preview`、`PUT /api/v1/agent-packs/:packId` 是纯 Core 代理。Gateway 不解析 manifest、不重算 hash、不保存 preview/receipt；PUT 必须透传 `If-Match` 与 `Idempotency-Key`，读/preview/apply 必须保留 ETag。
- 记忆和智能体候选的读取、晋升与拒绝同样直接代理 Core：`/api/v1/memory-candidates` 与 `/api/v1/agent-candidates`。Gateway 不审核候选、不生成 profile，也不修改记忆状态。
- `src/index.ts` 导出未监听的 `app` 供 `runtimeProxy.test.ts` 验证代理契约；仅直接作为 Bun 入口运行时才监听端口。

### 工具传输与治理边界
1. Core 是 agent/run 工具治理、权限状态、动作审批、会话归属和审计的权威；Gateway 不信任或解释客户端 `approved`、`source`、参数哈希或工具风险字段。
2. `/api/v1/runs/{runId}/tools/{toolId}/execute` 是智能体的 Core-owned 工具执行路径，Core 负责冻结配置、PDP、租约、ActionApproval 和 Tool Provider 调用。
3. `/api/v1/code/tools/{toolId}/execute` 与 `/api/v1/tool-runtime/tools/{toolId}/execute` 是 Desktop 用户直操作的当前 v1 传输入口。Gateway 原样转发请求、状态码和响应，不在本地审批或过滤。
4. Tool Runtime/Tool Provider 必须继续执行自己的沙箱与协议校验；需要 Core 治理事实的用户动作应由 Core 提供对应的直接工具 API，Gateway 不得自行补做一套授权状态机。

### Code Tool 规格
- `/api/v1/code/tools` 是 Core `/api/v1/tools` 的薄代理；目录由 Core/当前 Tool Provider 生成，Gateway 不维护风险、审批或工具状态事实。
- Gateway 不再包含本地 `codeTools.ts` catalog 或 `approval.ts` 风险/审批辅助；路由层只代理实时清单和执行传输。
- `/api/v1/code/tools/:toolId/execute` 与 `/api/v1/tool-runtime/tools/:toolId/execute` 均为当前 v1 的无状态传输入口，工具请求不得在 Gateway 形成授权事实。
- 这两组入口不是兼容路由：它们是 Desktop/用户显式使用工具的当前传输面。Gateway 必须保留请求体、Tool Provider 状态码、响应体和必要响应头；不要把 provider 错误转换成 Core ProblemDetails，也不要把用户请求改写成 run-scoped agent 调用。
- 智能体执行必须使用 `/api/v1/runs/{runId}/tools/{toolId}/execute`，不要从用户直操作入口绕过 Core。

### User Tool Actions and Governance
- `/api/v1/user/tool-actions`（list/create/detail/resume/snapshot-override）是 Core-owned durable action state 的无状态北向代理；Gateway 不生成 nonce、参数哈希、审批决定、租约或 PDP 结果。
- `/api/v1/governance/permission-requests` 及其 detail/decision、grant/delegation/lease 控制路由全部直接代理 Core；Gateway 不持有治理状态或内部 nonce。

### 外部 DTO 文档面 (2026-08-29)
- `src/externalDtoOpenApi.ts` 为 generated client 消费面（health/projects/sessions/messages/runs/orchestration/task-nodes/supervision-findings/context-versions）提供响应 schema：以 `detail.responses` + swagger `components.schemas` 注册，**不装** Elysia `response:` 运行时校验——Gateway 保持透传，runtimeProxy 测试的 proxied mock 依赖这一点。给新路由补响应 schema 时沿用同一模式，不要用会触发运行时校验的 `response:` 键。
- `apps/desktop/src/generated/client.ts` 的响应 DTO 是这些组件的 type 别名（`Schemas['Project']` 等）；`AgentPackEnvelopeDto` 保持请求侧宽松手写（App 用打包 manifest 字面量构造）。改外部 DTO 形状时：先改 mapper → 补/改 `externalDtoOpenApi.ts` → `bun test` 重写快照 → `npm run generate:client` → 同一提交入库。

### 会话附件代理（2026-09-21）
- `GET /api/v1/files/:sessionId/*` **已删除**：它代理到 Core 里从未存在过的路径，所以每个请求都是 Core 回来的 404，而在这个文件里看起来像一个能用的功能。取而代之的是五条 `attachments` 路由（上传 / 列表 / 元数据 / 取字节 / 丢弃），Core 侧真身见 `TinadecCore/AspNetCore/Endpoints/AttachmentEndpoints.cs`。
- **上传不缓冲、不改码**：`body: request.body` 直接把字节流转给 Core（`proxyStream` 的 `duplex:'half'`）。一旦 Gateway 先 `arrayBuffer()` 缓冲，Core 那个 per-request 32 MiB 上限就变成"先收完再说"，上限的意义没了。文件名与 media type 走 query 参数，不是 multipart：代理要解开的编码越多，与客户端悄悄不一致的地方越多。
- **安全判定必须穿过代理**：`setStreamHeaders` 只复制 `content-type`，所以下载路由**另外**转发了 `content-disposition`。Core 判定"这类字节不能内联（html/svg 能带脚本，而它们从渲染器信任的同一 origin 发出）"，如果这一句在代理里被丢掉，边界就只存在于源码里而不在客户端实际走的路上。别把它删掉当噪音。
- 新 DTO `MessageAttachment` / `MessageAttachmentList` 按 `### 外部 DTO 文档面` 那一条的既有流程入库；`apps/desktop` 侧 `MessageAttachmentDto` 是 `Schemas['MessageAttachment']` 别名，不再手写第二份。
- **绑定与上下文注入已接上**（2026-09-21，阶段 2g）：`/interactions` 现在接受 `attachment_ids`，网关只转发不校验——数组/uuid/每条消息上限 8 个/`dispatch_mode: insert` 拒收，这些判定属于 Core，一个替 Core 重判形状的代理只会漂移，并把"用户读得懂的 400"换成"静默少带一个文件"。外部契约新增 `MessageAttachmentSummary`（Core 嵌在消息里的那份窄投影，刻意没有 `session_id`/`message_id`：父行已经带了，重复就是把同一件事变成两处可说不一致的）并挂到 `Message.attachments`；`openapi.external.json` 由 `bun test` 重生成（+46/-1），`src/openapi.snapshot.test.ts` 钉住嵌套键集与"任何一层都不出现 `content_reference`"。仍未做：模型侧读取非文本附件（`read_attachment`）。`.qoder/repowiki/**` 里关于旧 files 路由的描述是自动生成的文档，未随本次删除更新。

### Model/Agent Center
- 旧 `GET /api/v1/model-center/overview`、`GET /api/v1/agent-center/overview` 与 model-center refresh alias 已删除并返回 404；`PUT /api/v1/agents/:id/runtime-binding` 是**当前有效**代理（`src/index.ts:1523`），不是 404 幽灵路由；模型发现的 canonical 转发路由是 `POST /api/v1/model-providers/:providerInstanceId/models/refresh`（Desktop `api.refreshProviderModels` 调用，快照 `tests/__snapshots__/openapi.external.json` 由 `bun test` 再生成）。
- Desktop 通过 Gateway 的版本化 provider/route/agent/mode/prompt/default/pack 路径自行组合视图；Gateway 不持久化或推导第二真相源。
- 任何仍保留的 BFF/代理响应都必须递归剥离 API Key 和其他密钥字段。

### JWT 认证（云端模式）
- `authenticate()` 是 **async**，`index.ts` 的 `onRequest` 中间件必须 `await` 它
- 使用 Bun 原生 WebCrypto 做 HMAC-SHA256 验签，**不引入 JWT 库依赖**
- 强制 `alg === 'HS256'`；`alg: none` 与其他算法一律拒绝（算法混淆防护）
- **Fail-closed**：云端模式下未配置 `jwtSecret` 时，Bearer token 一律拒绝，
  绝不退化为「仅解码不验签」。这条是安全边界，不要为了开发便利放宽
- base64url 解码必须补齐 padding，并用 `Uint8Array` + `TextDecoder` 而非裸 `atob`
- 本地模式（`authConfig === undefined`）完全跳过认证，不得因云端逻辑变更而回归
- 不加 JWKS、密钥轮换、token 缓存：Gateway 是无状态薄代理

### WS 代理现状（已知缺陷）
`index.ts` 的三个 WS 路由（`/ws/terminal`、`/ws/debug`、`/ws/collaboration`）目前都是无效桩：
它们订阅 Bun pub/sub topic 却从不连接目标服务。`websocket.ts` 的 `createWsProxyHandlers`
实现了真正的双向代理但无人调用。启用任何 WS 功能前必须先修这里。

## DELETED FILES
以下文件已删除，功能已迁移：
- `src/toolLayerBridge.ts` — TinadecTools 进程管理（迁移到 Tool Runtime）
- `src/debugProxy.ts` — 调试代理（合并到 `coreClient.ts` 和 `websocket.ts`）
- `src/mcp/McpConnectionManager.ts` — MCP 连接管理（迁移到 Tool Runtime）
- `src/mcp/McpClient.ts` — MCP 客户端（迁移到 Tool Runtime）
- `src/mcp/mcpRoutes.ts`（2026-09-22）— 曾经代理 `connect`/`disconnect`/`status`/`tools/{toolName}/call` 四条 **Core 从未注册**的路由，所以这四面只能 404；其中 `tools/{toolName}/call` 若真通了就是一条绕过审批门直达 MCP 工具的口子。表格旧行写的"纯代理到 Tool Runtime"也是错的：它代理的是 Core。改由 `src/mcpProxy.test.ts` 钉住"契约里只剩两条读路由 + 幻影不再被代理"。

## ANTI-PATTERNS
- 不要在 Gateway 中添加持久状态
- 不要让 Code tool 执行绕过 Core 审批语义
- 不要在 Gateway 中直接执行文件、Git、Shell、PTY 或 MCP 操作
- 不要在 Gateway 中管理 MCP 连接生命周期
- 不要绕过 Core 合约转发 `/api/v1/*` 形状
- 不要移除本地开发/Electron 允许的 CORS 来源

## COMMANDS
```bash
# 开发
cd TinadecGateway && bun run dev

# 构建
cd TinadecGateway && bun run build

# 测试
cd TinadecGateway && bun test

# 单个测试文件
cd TinadecGateway && bun test src/coreClient.test.ts
```
