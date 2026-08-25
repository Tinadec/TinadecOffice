# GATEWAY KNOWLEDGE

**Last Updated:** 2026-08-25
**Last Updated By:** openai/gpt-5.6
**Last Verified Commit:** daf2648
**Branch:** main

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
| Tool Runtime 代理 | `src/toolRuntimeClient.ts` | `toolRuntimeUrl()`，JSON 代理，SSE 代理，流式代理 |
| 认证中间件 | `src/auth.ts` | API Key / JWT HS256 验签（WebCrypto），租户上下文，反向代理头 |
| 请求上下文 | `src/headers.ts`, `src/auth.ts` | 只处理请求 id、认证和租户头；授权事实由 Core 或 Tool Provider 产生 |
| WebSocket 代理 | `src/websocket.ts` | 路由表，目标 URL 构建，消息透传 |
| 流式 HTTP 代理 | `src/streaming.ts` | 大文件/日志流式透传 |
| Model/Agent 配置代理 | `src/index.ts` | 版本化 provider/route/Agent/Mode/Prompt/default 路径；旧 overview/runtime-binding 路由已删除。 |
| Agent Pack 代理 | `src/index.ts`, `src/runtimeProxy.test.ts`, `tests/__snapshots__/openapi.external.json` | 四条显式薄代理；保留 ETag、`If-Match`、`Idempotency-Key` 和 Core ProblemDetails code。 |
| Code tools 传输 | `src/index.ts`, `src/toolRuntimeClient.ts` | Desktop 工具目录代理 Core，用户执行请求原样转发 Tool Provider |
| MCP 路由 | `src/mcp/mcpRoutes.ts` | 纯代理到 Tool Runtime |
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
| `TINADEC_TOOL_RUNTIME_URL` | `http://127.0.0.1:48732` | Tool Runtime 服务 URL |
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
- `POST /api/v1/sessions/{sessionId}/invoke-stream` 原样转发完整 JSON 请求和 Core 的 SSE 状态/主体；Gateway 不解释 `application_mode`、`agent_mode`、`permission_mode`、`target_run_id` 或 `expected_context_revision`。
- `GET /api/v1/application-modes` 与 `GET /api/v1/agent-modes?application_mode=` 直接读取 Core 的可用模式；`im`/`hub` 是当前内置别名，解析属于 Core。
- Run 控制与运行期投影均为纯 Core 代理：`POST /api/v1/runs/{runId}/control`、`GET /api/v1/runs/{runId}/orchestration`、`GET /api/v1/runs/{runId}/agent-lineage`、`GET /api/v1/sessions/{sessionId}/context-versions`。
- `GET /api/v1/model-providers/cli/discover` 与 `POST /api/v1/model-providers/cli/connect` 为纯 Core 代理（CLI 运行时发现与连接，见 Core `ControlPlaneService`）。
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

### Model/Agent Center
- 旧 `GET /api/v1/model-center/overview`、`GET /api/v1/agent-center/overview`、`PUT /api/v1/agents/:id/runtime-binding` 与 model-center refresh alias 已删除并返回 404。
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
