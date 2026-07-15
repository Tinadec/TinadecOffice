# GATEWAY KNOWLEDGE

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
                         Gateway ──HTTP/SSE/WS──> Tool Runtime
                         Core ←──HTTP──→ Tool Runtime
```

Gateway 可直接连接 Core 和 Tool Runtime；Core 与 Tool Runtime 也能互相通信。

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| 运行配置 | `src/config.ts` | 部署模式、端口、Core/Tool Runtime URL、认证、CORS |
| 服务器路由 | `src/index.ts` | Elysia app，CORS，认证中间件，`/api/v1/*`，WebSocket，流式 |
| Core 代理 | `src/coreClient.ts` | `coreUrl()`，JSON 代理，SSE 代理，流式代理 |
| Tool Runtime 代理 | `src/toolRuntimeClient.ts` | `toolRuntimeUrl()`，JSON 代理，SSE 代理，流式代理 |
| 认证中间件 | `src/auth.ts` | API Key / JWT 验证，租户上下文，反向代理头 |
| 审批拦截器 | `src/approval.ts` | 人类操作 approval=true 透传，高风险命令二次确认 |
| WebSocket 代理 | `src/websocket.ts` | 路由表，目标 URL 构建，消息透传 |
| 流式 HTTP 代理 | `src/streaming.ts` | 大文件/日志流式透传 |
| Model/Agent center BFF | `src/modelAgentCenter.ts` | 无状态聚合视图 |
| Code tools 规格 | `src/codeTools.ts` | 工具规格定义，审批验证，Tool Runtime 代理执行 |
| MCP 路由 | `src/mcp/mcpRoutes.ts` | 纯代理到 Tool Runtime |
| 测试 | `src/coreClient.test.ts`, `src/codeTools.test.ts`, `src/modelAgentCenter.test.ts` | Bun test |

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

### 审批流
1. 人类通过 Desktop 发出命令，请求包中带 `approval=true`
2. Gateway 检查命令风险等级（`approval.ts`）
3. 低/中风险命令：直接透传到 Tool Runtime
4. 高风险命令：返回 `449 CONFIRMATION_REQUIRED`，Desktop UI 显示弹窗警告
5. 用户确认后，请求带 `confirmation=true` 重新提交
6. Gateway 验证后透传到 Tool Runtime
7. Agent 请求不带 `approval=true`，按 Core 审批门流程处理

### Code Tool 规格
- `/api/v1/code/tools` 发布工具规格（snake_case DTO），Gateway 仅提供 BFF 组合
- `/api/v1/code/tools/:toolId/execute` 先验证 Core 审批状态，再经审批拦截器，最后代理到 Tool Runtime
- `executeCodeToolViaRuntime()` 是唯一的执行入口，通过 `toolRuntimeClient.ts` 代理

### Model/Agent Center
- `GET /api/v1/model-center/overview` 和 `GET /api/v1/agent-center/overview` 是无状态 BFF 聚合视图
- 必须递归剥离 API Key 和其他密钥字段
- 不持久化或发明第二真相源

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
