# 对齐 MCP 运行时与市场安装 Spec（P0 MCP 先行）

## Why

TinadecCode 的"调试工具实验面板"（`PreviewGallery.vue` + `mockData.ts`）渲染了一个看起来完整的市场功能，但所有数据来自虚构的 `clawhub.ai` mock。真实市场链路（`MarketPage.vue` → Core `/api/v1/extensions/install` → `CoreStore.UpsertRuntimeCache`）只往 SQLite 写元数据，从不 spawn 任何 MCP/ACP 子进程，安装后 `status='ready'` 是假的——用户"根本安装不了东西"。

本 spec 是用户要求的"P0 MCP 先行"落地：先让 MCP server 能真正被安装、连接、握手、注入工具、被模型调用，打通端到端的真实链路。ACP、Skills、Codex/Claude Code/VSCode 插件兼容在后续 P1/P2 spec 推进。

设计借鉴自对 `vscode` 与 `codex rust` 两个兄弟项目的研究结论：
- **VSCode MCP**：extHost 进程 spawn、`McpServerRequestHandler` 握手、`McpStdioStateHandler` 优雅关闭（stdin end → 10s → SIGTERM → 10s → SIGKILL）、`McpLanguageModelToolContribution` autorun 监听注入工具、`McpGatewayService` 反向 Gateway
- **Codex Rust MCP**：`McpConnectionManager`（HashMap by name）、`AsyncManagedClient`（`Shared<BoxFuture>` 避免重复启动）、`CancellationToken` + `child_token()` 取消传播、`JoinSet` 并发启动、`LocalStdioServerLauncher`（`kill_on_drop` + `process_group(0)` + `env_clear`）、StreamableHttp 传输、`active_time_timeout`、ToolInfo 双名设计（协议名 vs 模型可见名）

## 当前状态审查

### 已完成 ✅

1. **市场 UI 完整**：`apps/desktop/src/pages/MarketPage.vue`（528 行）调用真实 API（`api.listMarketCatalog`、`api.installExtension`、`api.decideApproval`），`selectedRuntime` 展示 `mcpServers`/`acpAdapters`，审批流接入
2. **扩展目录定义完整**：`src/TinadecCore/Services/ExtensionCatalog.cs`（419 行）含 3 个内置描述符模板，`DescriptorFromRequest`/`BuildPreview`/`NormalizeId`/`IsRemoteSource` 齐全，manifest 已写 `npx -y @modelcontextprotocol/server-filesystem`
3. **DB schema 就绪**：`mcp_servers` 表（id/name/transport/command_json/tools_json/status/status_message）+ `acp_adapters` 表已存在
4. **审批流真实可用**：`/api/v1/approvals` + Gateway `codeToolApprovalBlockFor` 已验证
5. **进程管理范式已验证**：`apps/gateway/src/codeTools.ts`（2110 行）的 `tryExecuteNativeTool` 是已跑通的 spawn 范式——`spawn(binary, ['execute'], { cwd, env, stdio: ['pipe','pipe','pipe'], windowsHide: true })` + 15s 超时 + stdout/stderr 收集 + JSON.parse
6. **工具注册扩展点存在**：`ToolRegistryService` 的 `ICapabilityProvider` 是工具注册扩展点，当前 `Source="code"` 注册了 4 个工具
7. **Core API 路由齐备**：`Program.cs` 含 `POST /api/v1/extensions/install`、`GET /api/v1/mcp/servers`、`POST /api/v1/mcp/servers/{id}/reload` 等端点

### 未完成 ❌

1. **无 MCP 客户端运行时**：全仓 grep 确认 `src/` 下只有 `CliProviderRuntime.cs` 和 `DoctorService.cs` 有 `Process.Start`，无任何 MCP 客户端代码——没有 initialize 握手、没有 tools/list、没有 tools/call
2. **`CoreStore.UpsertRuntimeCache` 是假安装**：MCP 安装只往 `mcp_servers` 表插 `status='ready'` + 假工具名 `{extension_id}.tool`；ACP 安装只插 `status='ready'`，明文注释 "Runtime process spawning remains approval-gated"
3. **市场源是虚构的**：`BUILT_IN_SOURCES` 指向不存在的 `https://clawhub.ai/`，`mockData.ts` 的 6 个条目全是 mock
4. **实验面板与真实后端脱节**：`PreviewGallery.vue` 用 `mockData.ts` 渲染，不触发任何真实 API
5. **无 MCP 生命周期管理**：没有启动、健康检查、优雅关闭、崩溃重启
6. **无工具注入链路**：MCP server 的 tools 从未被注册进 `ToolRegistryService`，模型无法调用
7. **`/api/v1/mcp/servers/{id}/reload` 是空操作**：只改 DB，不重连进程

## What Changes

### 决策：MCP 子进程放在 Gateway 层

**理由**：
- 对应 VSCode 的 extHost 模式（spawn 在 Extension Host 进程，不在主进程）
- 对应 Codex Rust 的 `LocalStdioServerLauncher` 模式（spawn 在 codex 主进程的子任务里）
- Gateway 已有 `tryExecuteNativeTool` 的 spawn 范式可复用，TypeScript 生态有现成的 `@modelcontextprotocol/sdk`
- Core 是 .NET 状态权威，不应持有短生命周期子进程；Desktop 是 Electron 渲染层，不应管系统进程
- Gateway 已是 BFF/进程执行层，职责匹配

### 改造内容

1. **新增 Gateway MCP 客户端模块** `apps/gateway/src/mcp/`：
   - `McpConnectionManager.ts` — 借鉴 codex 的 `HashMap<String, AsyncManagedClient>`，按 server id 索引
   - `AsyncManagedClient.ts` — 借鉴 codex 的 `Shared<Promise>` 模式，避免重复启动；含状态机 `stopped/starting/running/error`（借鉴 vscode `mcpServerConnection.ts`）
   - `StdioTransport.ts` — spawn 子进程，`kill_on_drop` 语义（Node 用 `child.on('exit')` + `AbortController` 模拟），`windowsHide: true`
   - `StreamableHttpTransport.ts` — P0 预留接口，P1 实现（借鉴 codex 的 StreamableHttp 而非旧 SSE）
   - `McpClient.ts` — JSON-RPC 2.0 over stdio，实现 `initialize` → `notifications/initialized` → `tools/list` → `tools/call` 握手（借鉴 vscode `mcpServerRequestHandler.ts`）
   - `LifecycleManager.ts` — 优雅关闭：stdin end → 10s grace → SIGTERM → 10s → SIGKILL（借鉴 vscode `mcpStdioStateHandler.ts`）；`active_time_timeout` 在 tools/call 期间暂停超时（借鉴 codex）
   - `ToolDiscovery.ts` — tools/list 分页拉取，回传 Core 注册

2. **改造 Core `CoreStore.UpsertRuntimeCache`**：
   - MCP 安装时不再写 `status='ready'`，改为 `status='pending_connect'`，`tools_json` 写 `[]`
   - 新增 `status='connected'`（Gateway 握手成功后回写）、`status='error'`（握手失败）、`status='disconnected'`（进程退出）

3. **新增 Core ↔ Gateway MCP 协调 API**：
   - `POST /api/v1/mcp/servers/{id}/connect` — Core 通知 Gateway 启动并握手
   - `POST /api/v1/mcp/servers/{id}/disconnect` — Core 通知 Gateway 优雅关闭
   - `GET /api/v1/mcp/servers/{id}/status` — 查询真实运行时状态
   - Gateway 回调 Core `POST /api/v1/mcp/servers/{id}/report` — 上报 tools 列表、状态变更

4. **工具注入链路**：
   - Gateway `ToolDiscovery` 拉到 tools → 回传 Core `/report`
   - Core 收到后注册进 `ToolRegistryService`，`Source="mcp"`，`Endpoint` 指向 Gateway 的 `POST /api/v1/mcp/servers/{id}/tools/{tool_name}/call`
   - 借鉴 codex 的 ToolInfo 双名设计：`server_id` + `tool.name`（协议路由）vs `mcp__{server_name}__{tool_name}`（模型可见，借鉴 vscode 的命名风格）

5. **改造 `/api/v1/mcp/servers/{id}/reload`**：先 disconnect 再 connect，而非空操作

6. **市场源对齐**：`BUILT_IN_SOURCES` 移除虚构 `clawhub.ai`，P0 先支持本地 manifest 安装（用户手动填 command/args/env），P1 再接真实 registry

7. **实验面板对齐**：`PreviewGallery.vue` 的 market 场景改为调用真实 API（带 mock fallback），让"实验面板"与"真实市场"共享同一数据源

## Impact

- **Affected specs**: `glue-codex-rust-tools`（共享 Gateway spawn 范式）、后续 ACP/Skills spec
- **Affected code**:
  - 新增：`apps/gateway/src/mcp/` 目录及 7 个模块
  - 修改：`src/TinadecCore/Storage/CoreStore.cs`（`UpsertRuntimeCache` 改造 + 新增 status 转换方法）
  - 修改：`src/TinadecCore/Program.cs`（新增 4 个 MCP 协调端点）
  - 修改：`src/TinadecCore/Services/ToolRegistryService.cs`（接收 MCP tools 注册，`Source="mcp"`）
  - 修改：`src/TinadecCore/Services/ExtensionCatalog.cs`（`BUILT_IN_SOURCES` 去虚构，支持本地 manifest）
  - 修改：`apps/gateway/src/codeTools.ts`（复用 spawn 范式，可选抽取公共 `spawnManaged` helper）
  - 修改：`apps/desktop/src/pages/MarketPage.vue`（调用真实 connect API，展示真实 status）
  - 修改：`apps/desktop/src/debug/preview/mockData.ts`（market 场景改为真实 API + fallback）
  - 修改：`apps/desktop/src/debug/preview/PreviewGallery.vue`（market 场景接真实 API）

## ADDED Requirements

### Requirement: Gateway MCP 连接管理器

Gateway SHALL 实现 `McpConnectionManager`，按 server id 索引管理多个 MCP 客户端连接，借鉴 codex 的 `HashMap<String, AsyncManagedClient>` 模式。

#### Scenario: 首次连接避免重复启动
- **WHEN** Core 调用 `POST /api/v1/mcp/servers/{id}/connect`，且该 server 已有 `starting` 状态的连接
- **THEN** SHALL 返回已有的 `Shared<Promise>`，不重复 spawn 子进程
- **AND** 借鉴 codex `AsyncManagedClient` 的 `Shared<BoxFuture>` 模式

#### Scenario: 并发启动多个 server
- **WHEN** Core 连续调用多个 server 的 connect
- **THEN** SHALL 并发启动（借鉴 codex `JoinSet`），不串行阻塞

#### Scenario: 状态机转换
- **WHEN** 连接经历 stopped → starting → running → error → stopped
- **THEN** SHALL 每次转换都回调 Core `/report` 上报状态
- **AND** 借鉴 vscode `mcpServerConnection.ts` 的状态机

### Requirement: MCP stdio 传输与握手

Gateway SHALL 通过 stdio 与 MCP server 子进程通信，完成 JSON-RPC 2.0 握手。

#### Scenario: spawn 子进程
- **WHEN** Gateway 启动一个 stdio 类型的 MCP server
- **THEN** SHALL 使用 `spawn(command, args, { env: { ...process.env, ...serverEnv }, stdio: ['pipe','pipe','pipe'], windowsHide: true })`
- **AND** 借鉴 codex `LocalStdioServerLauncher` 的 `kill_on_drop` + `process_group(0)` 语义（Node 用 `AbortController` + `child.kill` 模拟）
- **AND** 复用 `codeTools.ts` 的 `tryExecuteNativeTool` spawn 范式

#### Scenario: initialize 握手
- **WHEN** 子进程 spawn 成功
- **THEN** SHALL 发送 `initialize` 请求，含 `protocolVersion`、`capabilities`、`clientInfo`
- **AND** 收到 `initialize` 响应后发送 `notifications/initialized` 通知
- **AND** 借鉴 vscode `mcpServerRequestHandler.ts` 的握手序列

#### Scenario: tools/list 拉取
- **WHEN** initialize 握手完成
- **THEN** SHALL 发送 `tools/list` 请求，分页拉取所有 tools
- **AND** 将 tools 列表回调 Core `/report` 注册进 `ToolRegistryService`

#### Scenario: tools/call 调用
- **WHEN** 模型通过 `ToolRegistryService` 调用一个 `Source="mcp"` 的工具
- **THEN** Core SHALL 转发到 Gateway `POST /api/v1/mcp/servers/{id}/tools/{tool_name}/call`
- **AND** Gateway SHALL 发送 `tools/call` JSON-RPC 请求到子进程，传 args
- **AND** 返回结果给 Core，含审批门控（借鉴 `codeToolApprovalBlockFor`）

### Requirement: MCP 生命周期与优雅关闭

Gateway SHALL 实现 MCP 子进程的生命周期管理，含健康检查、优雅关闭、崩溃处理。

#### Scenario: 优雅关闭顺序
- **WHEN** Core 调用 `POST /api/v1/mcp/servers/{id}/disconnect`
- **THEN** SHALL 按顺序：stdin `end()` → 等 10s → `SIGTERM` → 等 10s → `SIGKILL`
- **AND** 借鉴 vscode `mcpStdioStateHandler.ts` 的优雅关闭时序

#### Scenario: 取消传播
- **WHEN** disconnect 被调用且有进行中的 tools/call
- **THEN** SHALL 取消所有进行中的请求（借鉴 codex `CancellationToken` + `child_token()`）
- **AND** 不阻塞优雅关闭流程

#### Scenario: active_time_timeout 暂停
- **WHEN** 有 tools/call 进行中
- **THEN** SHALL 暂停空闲超时检测（借鉴 codex `active_time_timeout`）
- **AND** tools/call 完成后恢复超时

#### Scenario: 子进程崩溃
- **WHEN** 子进程意外退出（非 disconnect 触发）
- **THEN** SHALL 回调 Core `/report` 上报 `status='disconnected'` + exit code
- **AND** 不自动重启（P0），由用户手动 reload 触发重连

### Requirement: Core 真实状态管理

Core SHALL 移除假 `status='ready'`，改为基于 Gateway 回报的真实状态。

#### Scenario: 安装后初始状态
- **WHEN** 用户安装一个 MCP server 扩展
- **THEN** `CoreStore.UpsertRuntimeCache` SHALL 写 `status='pending_connect'`，`tools_json='[]'`
- **AND** 不再写假工具名 `{extension_id}.tool`

#### Scenario: Gateway 回报连接成功
- **WHEN** Gateway 握手成功并拉到 tools 列表
- **THEN** Gateway SHALL 调用 `POST /api/v1/mcp/servers/{id}/report`，body 含 `status='connected'` + `tools=[...]`
- **AND** Core 更新 `mcp_servers` 表的 status 和 tools_json

#### Scenario: Gateway 回报连接失败
- **WHEN** Gateway 握手失败或子进程崩溃
- **THEN** Gateway SHALL 调用 `/report`，body 含 `status='error'` 或 `'disconnected'` + `status_message`
- **AND** Core 更新 status 和 status_message

#### Scenario: 查询真实状态
- **WHEN** Desktop 调用 `GET /api/v1/mcp/servers/{id}/status`
- **THEN** SHALL 返回 Gateway 回报的最新真实状态，而非 DB 里的静态值
- **AND** 若状态超过 30s 未刷新，SHALL 主动向 Gateway 查询

### Requirement: MCP 工具注入 ToolRegistryService

Core SHALL 将 MCP server 暴露的 tools 注册进 `ToolRegistryService`，使模型可调用。

#### Scenario: 工具注册
- **WHEN** Gateway 回报 tools 列表 `[{name, description, inputSchema}]`
- **THEN** Core SHALL 为每个 tool 注册到 `ToolRegistryService`，`Source="mcp"`
- **AND** `Endpoint` 指向 Gateway `POST /api/v1/mcp/servers/{id}/tools/{tool_name}/call`
- **AND** 借鉴 codex ToolInfo 双名设计：协议名 `{server_id}:{tool_name}`，模型可见名 `mcp__{server_name}__{tool_name}`

#### Scenario: 工具注销
- **WHEN** MCP server disconnect 或卸载
- **THEN** Core SHALL 从 `ToolRegistryService` 移除该 server 的所有 tools
- **AND** 不残留失效工具

#### Scenario: 审批门控
- **WHEN** 模型调用一个 MCP 工具，且该工具 `requiresApproval=true`
- **THEN** SHALL 走现有审批流（`/api/v1/approvals` + `codeToolApprovalBlockFor`）
- **AND** 审批通过后才转发到 Gateway 执行

### Requirement: 市场源对齐与本地 manifest 安装

市场 SHALL 移除虚构源，P0 支持本地 manifest 安装。

#### Scenario: 移除虚构源
- **WHEN** 系统启动
- **THEN** `ExtensionCatalog.BUILT_IN_SOURCES` SHALL 不再包含 `https://clawhub.ai/`
- **AND** 改为内置本地 manifest 模板（filesystem、git 等官方 MCP server）

#### Scenario: 本地 manifest 安装
- **WHEN** 用户在市场页面选择"手动添加 MCP server"
- **THEN** SHALL 让用户填写 `command`、`args`、`env`、`transport`（stdio/http）
- **AND** 提交后调用 `POST /api/v1/extensions/install`，写入 `mcp_servers` 表
- **AND** 自动触发 `POST /api/v1/mcp/servers/{id}/connect` 真实连接

### Requirement: 实验面板与真实市场共享数据源

实验面板的 market 场景 SHALL 调用真实 API，与 `MarketPage.vue` 共享数据源。

#### Scenario: 实验面板 market 场景
- **WHEN** 用户在 DebugStudio 打开"预览" → "MarketPage" 场景
- **THEN** SHALL 调用 `api.listMarketCatalog` 真实 API
- **AND** 仅在 API 失败时降级到 `mockData.ts` fallback
- **AND** 不再默认用 mock 数据渲染

## MODIFIED Requirements

### Requirement: CoreStore.UpsertRuntimeCache

原逻辑：MCP 安装时写 `status='ready'` + 假工具名 `{extension_id}.tool`；ACP 安装时写 `status='ready'` + 注释 "Runtime process spawning remains approval-gated"。

修改为：
- MCP 安装时写 `status='pending_connect'`，`tools_json='[]'`，不写假工具名
- ACP 安装保持 `status='pending_connect'`（ACP 在 P1 spec 处理）
- 真实 `status` 由 Gateway `/report` 回调驱动转换：`pending_connect` → `connected` → `error`/`disconnected`

### Requirement: /api/v1/mcp/servers/{id}/reload

原逻辑：只改 DB status，不重连进程。

修改为：
- 调用 Gateway `disconnect`（优雅关闭旧进程）
- 等待关闭完成（最长 20s）
- 调用 Gateway `connect`（重新 spawn + 握手）
- 返回新的真实 status

### Requirement: ExtensionCatalog.BUILT_IN_SOURCES

原逻辑：指向虚构 `https://clawhub.ai/`。

修改为：
- 移除所有 `clawhub.ai` 引用
- 内置本地 manifest 模板（filesystem、git 等官方 MCP server 的 command/args/env）
- P1 再接真实 registry（如 modelcontextprotocol/servers 的 GitHub releases）

### Requirement: PreviewGallery market 场景

原逻辑：用 `mockData.ts` 的 `mockMarketCatalog` 渲染。

修改为：
- 调用 `api.listMarketCatalog` 真实 API
- API 失败时降级到 mock fallback
- 与 `MarketPage.vue` 共享同一 API 调用模块
