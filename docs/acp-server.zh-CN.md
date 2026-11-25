# Tinadec ACP Agent Server 接入与工程指南

`TinadecCore` 原生提供符合 **Agent Client Protocol (ACP) v1**（`protocolVersion: 1`）规范的 `stdio` Agent Server 模式（`--acp`）。外部 IDE 与智能体宿主——包括 **Zed**、**JetBrains AI / ACP Client**、**Antigravity**、**VS Code ACP 扩展**以及**另一台 TinadecOffice**——均可通过标准 `stdio` 管道直接唤起、持续多轮对话、切换模式/模型，并通过双向权限门禁审批 `TinadecCore` 的工具执行。

---

## 一、运行模式与架构契约

### 1. 双模运行后端（Mode A vs Mode B）

`TinadecCore` ACP Server 通过统一后端抽象支持两种运行拓扑，对外暴露完全一致的 ACP v1 协议面：

| 维度 | Mode A：独立内嵌模式（默认） | Mode B：桌面实时联动模式（`--core-url`） |
| :--- | :--- | :--- |
| **触发条件** | 未指定 `--core-url` 且未设置 `TINADEC_CORE_URL` | 传入 `--core-url <url>` 或设置环境变量 `TINADEC_CORE_URL` |
| **执行形态** | 进程内直接启动完整 `TinadecCore` DI 宿主（SQLite 迁移、`FullDuplexRunCoordinator`、`TinadecTools`、治理审批门禁），**不启动任何 Kestrel HTTP 端口** | 作为轻量 HTTP + SSE 桥接进程挂载到本机已运行的 `TinadecCore` 实例（如桌面端或 `dev:core` 默认监听的 `http://127.0.0.1:48731`） |
| **存储与锁边界** | 独占所指存储根的用户租约锁（`state/host.lock`，`UserStorageHostLease`）；同一存储根不可与桌面端同时以 Mode A 打开 | 复用正在运行的桌面端 Core 存储作用域（自动透传 `X-Tinadec-Storage-Id` 与 `X-Tinadec-Host-Control`），零锁冲突 |
| **桌面端联动可视性** | 独立运行，无需打开桌面端 | **双向实时同步**：外部 IDE 提问与执行期间，**TinadecOffice 桌面端实时同步呈现完整对话流、实时概要（Live Digest）、实时协作图（Orchestration）与执行轨迹（Trajectory）** |
| **生命周期** | `stdin` 收到 `EOF` 或进程收到 `SIGINT`/`SIGTERM` 时取消活跃回合、安全落盘并以退出码 `0` 退出，零孤儿进程 | `stdin` `EOF` 或信号终止时取消当前挂起交互并干净断开 SSE 流，不影响宿桌面端 Core 继续运行 |

### 2. `stdout` 绝对纯净契约（BOM-Free UTF-8 NDJSON）

ACP over `stdio` 对标准输出字节流有零容忍要求。`TinadecCore` 与 `scripts/tinadec-acp.mjs` 共同执行以下铁律：

1. **`stdout` 100% 专用于 ACP 帧**：在 `Program.cs` 入口最前端拦截 `--acp`，第一时间捕获原始 `stdin`/`stdout` 流并将 `Console.Out` 重定向至 `Console.Error`，禁用一切 Kestrel 启动横幅与 .NET SDK 欢迎提示。
2. **无 BOM UTF-8 NDJSON**：每条 JSON-RPC 2.0 消息编码为单行无 BOM（绝无 `EF BB BF` 前缀）UTF-8 JSON，以 `\n` 结尾并立即 Flush。
3. **日志全量走 `stderr`**：所有 `ILogger`（`LogToStandardErrorThreshold = LogLevel.Trace`）与启动器错误全部输出到 `stderr`，确保并发告警或第三方库 `Console.WriteLine` 绝不污染 `stdout`。

> [!IMPORTANT]
> 切勿将 `scripts/dotnet.mjs` 或 `scripts/setup-dotnet-env.ps1` 直接配置为 ACP 客户端的启动命令——Windows 开发环境脚本中的 `Write-Host` 会向标准输出写入环境准备提示并破坏 JSON-RPC 握手。请始终使用专用的 **`scripts/tinadec-acp.mjs`** 或直接调用编译后的 **`TinadecCore.Api.dll --acp`**。

---

## 二、CLI 启动入口、参数与环境变量

### 1. 推荐启动方式

首次接入前建议在仓库根目录编译一次 Release 产物，以获得毫秒级冷启动体验：

```powershell
Set-Location C:\git\agent\TinadecOffice
bun scripts/dotnet.mjs build TinadecOffice.slnx -c Release
```

- **跨平台 Node / Bun 启动器（推荐）**：
  ```bash
  node /path/to/TinadecOffice/scripts/tinadec-acp.mjs [options]
  ```
  `scripts/tinadec-acp.mjs` 会自动清理干扰子进程的 `Version`/`Ice-Version` 环境变量、注入 `DOTNET_NOLOGO=1`，并按以下优先级定位可执行目标：
  1. 环境变量 `TINADEC_ACP_EXECUTABLE` 显式指定的 `.dll` 或可执行文件；
  2. `TinadecCore/Api/bin/Release/net11.0/TinadecCore.Api.dll`（及当前平台 RID 子目录或可执行文件）；
  3. `artifacts/tinadec-core-api/TinadecCore.Api.dll`（`dotnet publish` 产物）；
  4. `TinadecCore/Api/bin/Debug/net11.0/TinadecCore.Api.dll`；
  5. 若尚未构建或显式传入 `--dotnet-run`，自动回退至 `dotnet run --project TinadecCore/Api/TinadecCore.Api.csproj --no-launch-profile -v quiet -- --acp ...`。

- **直接运行 .NET 编译/发布产物**：
  ```bash
  dotnet /path/to/TinadecOffice/TinadecCore/Api/bin/Release/net11.0/TinadecCore.Api.dll --acp [options]
  ```

### 2. CLI 参数与环境变量对照表

| CLI 参数 | 等效环境变量 | 适用模式 | 说明 |
| :--- | :--- | :---: | :--- |
| `--acp` | — | 全部 | 启用 ACP v1 `stdio` Agent Server 模式（使用 `scripts/tinadec-acp.mjs` 时会自动补齐） |
| `--core-url <url>` | `TINADEC_CORE_URL` | Mode B | 连接运行中的 `TinadecCore` HTTP 地址（例如 `http://127.0.0.1:48731`） |
| `--host-token <token>` | `TINADEC_HOST_CONTROL_TOKEN` | Mode B | 连接受宿主门禁保护的 Core 时携带的宿主控制令牌（映射为 `X-Tinadec-Host-Control` 请求头；**强烈建议通过环境变量注入**） |
| `--storage-root <path>` | `TinadecStorage__UserRoot` | Mode A | 显式指定独立用户存储根目录，用于多实例并发运行或与已打开的桌面端隔离 `host.lock` |
| `--dotnet-run` | `TINADEC_ACP_DOTNET_RUN=1` | 启动器 | 仅 `scripts/tinadec-acp.mjs` 识别：跳过预编译二进制查找，强制通过 `dotnet run` 启动 |
| — | `TINADEC_ACP_EXECUTABLE` | 启动器 | 仅 `scripts/tinadec-acp.mjs` 识别：显式锁定 `TinadecCore.Api.dll` 或可执行文件的绝对路径 |

---

## 三、主流客户端接入配置（复制即用）

> 以下示例请将 `/path/to/TinadecOffice`（Windows 下如 `C:/git/agent/TinadecOffice`）替换为本机实际绝对路径。

### 1. Zed（`settings.json` → `agent_servers`）

打开 Zed 配置文件（macOS/Linux：`~/.config/zed/settings.json`；Windows：`%APPDATA%\Zed\settings.json`），在 `agent_servers` 下添加配置：

#### 方案 A：独立内嵌模式（Mode A，无需后台运行桌面端）

```json
{
  "agent_servers": {
    "Tinadec (Standalone)": {
      "command": "node",
      "args": [
        "C:/git/agent/TinadecOffice/scripts/tinadec-acp.mjs",
        "--acp"
      ],
      "env": {}
    }
  }
}
```

> 若希望在打开 TinadecOffice 桌面端的同时运行一个完全隔离的独立实例，可在 `args` 中追加 `"--storage-root", "C:/Users/<you>/.tinadec-zed"`。

#### 方案 B：桌面实时联动模式（Mode B，推荐与 TinadecOffice 桌面端协同观测）

当本机已通过统一控制令牌启动 `TinadecCore` / `TinadecOffice` 时（例如设定了共享 `TINADEC_HOST_CONTROL_TOKEN` 的本地运行栈）：

```json
{
  "agent_servers": {
    "Tinadec (Desktop Live-Sync)": {
      "command": "node",
      "args": [
        "C:/git/agent/TinadecOffice/scripts/tinadec-acp.mjs",
        "--acp"
      ],
      "env": {
        "TINADEC_CORE_URL": "http://127.0.0.1:48731",
        "TINADEC_HOST_CONTROL_TOKEN": "<your-host-control-token>"
      }
    }
  }
}
```

连接成功后，在 Zed Agent Panel 新建会话并发起指令，切回 TinadecOffice 桌面端对应工作区即可实时查看会话消息、编排拓扑与工具轨迹，并支持在任意一端完成权限审批。

---

### 2. JetBrains AI / ACP Client

在 JetBrains IDE（IntelliJ IDEA、WebStorm、Rider、PyCharm 等）的 `~/.jetbrains/acp.json`（或 **Settings → Tools → AI Assistant → Agent Client Protocol (ACP)**）中添加：

```json
{
  "agent_servers": {
    "tinadec": {
      "command": "node",
      "args": [
        "C:/git/agent/TinadecOffice/scripts/tinadec-acp.mjs",
        "--acp"
      ],
      "env": {
        "TINADEC_CORE_URL": "http://127.0.0.1:48731",
        "TINADEC_HOST_CONTROL_TOKEN": "<your-host-control-token>"
      }
    }
  }
}
```

> 若使用独立内嵌模式（Mode A），将上方 `"env"` 清空为 `{}` 即可（必要时可在 `args` 追加 `"--storage-root", "<独立目录>"`）。

---

### 3. Antigravity / MCP-ACP 宿主

在支持标准 `stdio` ACP Agent 挂载的宿主（如 Antigravity 或通用 ACP 协调器配置）中注册自定义 Agent Server：

```json
{
  "acpServers": {
    "tinadec": {
      "command": "node",
      "args": [
        "C:/git/agent/TinadecOffice/scripts/tinadec-acp.mjs",
        "--acp"
      ],
      "env": {
        "TINADEC_CORE_URL": "http://127.0.0.1:48731",
        "TINADEC_HOST_CONTROL_TOKEN": "<your-host-control-token>"
      }
    }
  }
}
```

- **独立无状态/CI 沙箱场景**：去掉 `env` 中的 `TINADEC_CORE_URL` 与 `TINADEC_HOST_CONTROL_TOKEN`，并在 `args` 中传入 `"--storage-root", "/tmp/tinadec-acp-sandbox"`，每次任务均可在隔离存储根中冷启动运行。

---

### 4. TinadecOffice 自互联（内置 `custom-acp`）

一台 `TinadecOffice` 实例可以通过内置的 **Custom ACP Agent（`driver: "custom-acp"`）** 将另一个 `TinadecCore --acp` 实例作为子 Harness 驱动（这也是仓库 E2E 自举测试的标准拓扑）：

1. 打开 **TinadecOffice → 设置（Settings）→ 模型中心（Model Center）→ 本地运行时（Local Runtimes）**，点击 **添加 Custom ACP Agent**（`custom-acp`）。
2. 按以下字段填写：
   - **可执行文件路径（`binary_path`）**：`node`（或编译产物 `C:/git/agent/TinadecOffice/TinadecCore/Api/bin/Release/net11.0/TinadecCore.Api.exe`）
   - **启动参数（`launch_args`）**：
     ```text
     C:/git/agent/TinadecOffice/scripts/tinadec-acp.mjs --acp --storage-root C:/Users/<you>/.tinadec-peer
     ```
     *（若 `binary_path` 直接填写了 `TinadecCore.Api.exe` 或非 Windows 下的 `TinadecCore.Api`，则 `launch_args` 仅需填写 `--acp --storage-root C:/Users/<you>/.tinadec-peer`。）*
   - **配置目录（`home_path`，可选）**：`C:/Users/<you>/.tinadec-peer`

> [!NOTE]
> **为什么同机自互联需指定独立的 `--storage-root`？**
> 宿主 `TinadecOffice` 自身已持有默认用户存储根（`~/.tinadec/state/host.lock`），且宿主拉起外部 ACP 子进程时出于最小权限安全边界会自动剥离 `TINADEC_HOST_CONTROL_TOKEN`（见 `ProcessAcpTransport`）。为被控子实例指定独立的 `--storage-root`（并在该目录配置好模型 Provider，或先对其初始化一次配置）即可实现零冲突自互联。

---

## 四、ACP v1 协议支持矩阵与事件映射

`TinadecCore` ACP Server 严格遵循 JSON-RPC 2.0 与 ACP `protocolVersion: 1` 线缆契约（全字段严格 `camelCase`，版本号为整型 `1`）。

### 1. Client → Agent 方法

| 方法名 | 类型 | 入参摘要 | 行为与返回契约 |
| :--- | :---: | :--- | :--- |
| `initialize` | Request | `{ protocolVersion: 1, clientCapabilities?, clientInfo? }` | 校验整型协议版本 `1`；返回 `{ protocolVersion: 1, agentCapabilities: { loadSession: true, promptCapabilities: { image: true }, sessionCapabilities: { fork: false } }, agentInfo: { name: "tinadec", title: "TinadecOffice", version } }` |
| `session/new` | Request | `{ cwd, mcpServers }` | 校验 `cwd` 为已存在的绝对目录路径，自动绑定或初始化对应工作区项目与新会话；返回真实 `{ sessionId, modes: { currentModeId, availableModes }, models: { currentModelId, availableModels } }`（涵盖 `auto`、`plan`、`spec`、`ask`、`vibe`、`agent` 及已发布自定义模式） |
| `session/load` | Request | `{ sessionId, cwd, mcpServers }` | 校验目标 `sessionId` 存在且可访问；**先按序通过 `session/update`（`user_message_chunk` / `agent_message_chunk`）完整回放历史对话，Flush 后再返回** `{ modes, models }` |
| `session/prompt` | Request | `{ sessionId, prompt: AcpContentBlock[] }` | 解析多模态输入块并驱动全双工运行引擎；流式推送 `session/update` 通知，**严格保证所有尾部通知 Flush 完毕后**才返回 `{ stopReason: "end_turn" \| "cancelled" \| "max_tokens" \| "refusal" }` |
| `session/cancel` | Notification | `{ sessionId }` | 单向通知（无 `id`、无响应）：立即终止活跃 Core Run，并中断正在等待用户响应的 `session/request_permission`，使挂起的 `session/prompt` 干净收口返回 `{ stopReason: "cancelled" }` |
| `session/set_mode` | Request | `{ sessionId, modeId }` | 校验并切换会话运行模式，落库后广播 `session/update`（`current_mode_update`）；非法 `modeId` 返回 `-32602` |
| `session/set_model` | Request | `{ sessionId, modelId }` | 校验并将会话模型覆盖（`meeting_model_override`）切换至已配置的 `modelId`；未知或非法 `modelId` 返回 `-32602` |

### 2. `session/prompt` 支持的内容块（`AcpContentBlock`）

- **`text`**：标准用户提示文本（`{ type: "text", text }`）。
- **`resource` / `resource_link`**：IDE 附带的文件片段、选区或 URI 引用，按输入顺序拼接为结构化上下文传入模型。
- **`image`**：Base64 编码图片（`{ type: "image", data, mimeType }`），经 `IMessageAttachmentStore` 安全校验并持久化后作为多模态附件绑定至当前交互回合（非法 Base64 或不支持的载荷返回 `-32602`）。

### 3. Agent → Client 流式通知（`session/update`）

| `sessionUpdate` 类型 | 触发时机与载荷说明 |
| :--- | :--- |
| `user_message_chunk` | `session/load` 历史回放期间的用户消息片段 |
| `agent_thought_chunk` | 智能体思考/推理增量（`content: { type: "text", text }`） |
| `agent_message_chunk` | 智能体正文回答增量（去重保序，`content: { type: "text", text }`） |
| `tool_call` | 工具准备或开始执行，携带 `toolCallId`、`title`、规范化 ACP `kind`（`read \| edit \| delete \| move \| search \| execute \| think \| fetch \| other`）、`status`（`"pending" \| "in_progress"`）、`locations`（涉及文件路径）与 `rawInput` |
| `tool_call_update` | 工具执行结束，携带 `toolCallId`、`status`（`"completed" \| "failed"`）、`content` 与 `rawOutput` |
| `plan` | 会话计划/任务图推进快照（`entries: [{ content, priority, status }]`） |
| `current_mode_update` | 会话模式变更同步（`currentModeId`） |

---

## 五、双向权限审批桥（`session/request_permission`）与竞态消解

当智能体调用受 `ToolApprovalPolicy` 门禁保护的工具（如受限命令执行、跨边界修改等）导致运行进入 `awaiting_approval` 状态时，ACP Server 不会静默挂死或越权放行，而是向 ACP 客户端发起反向 JSON-RPC 请求 `session/request_permission`：

### 1. 标准三态授权选项

- **`allow_once`**（`kind: "allow_once"`）：仅批准当前这一次操作（映射 Core `decision: "approved"`, `scope: "once"`）。
- **`allow_always`**（`kind: "allow_always"`）：批准当前操作；当该审批请求满足安全规则记忆条件（具备稳定可记忆签名）时，自动写入会话级持久化放行规则（`remember_rule: true`），后续同类调用免重复弹窗。
- **`reject_once`**（`kind: "reject_once"`）：拒绝本次操作（映射 Core `decision: "rejected"`），运行恢复并将拒绝结果反馈给模型自行调整方案。

### 2. Fail-Closed 兜底原则

若客户端返回 `{ outcome: { outcome: "cancelled" } }`、传入未知 `optionId`、或期间收到 `session/cancel`，服务端一律按 **Fail-Closed（安全拒绝）** 处理并安全解挂，绝不降级放行任何未获明确批准的工具调用。

### 3. Mode B 桌面端 / IDE 双端审批竞态消解

在 `--core-url` 桌面实时联动模式下，同一个待审批动作会同时出现在 **外部 IDE（Zed / JetBrains 等）** 与 **TinadecOffice 桌面端会话界面** 中：

- 若用户在 **外部 IDE** 点击批准/拒绝：ACP Server 将决策提交至 Core `/api/v1/approvals/{id}/decision`，桌面端审批卡片随即自动收起并继续流式渲染。
- 若用户直接在 **TinadecOffice 桌面端** 点击了批准/拒绝：ACP Server 通过后台 SSE 事件流（`approval.decided` / `governance.permission_decided`）及审批状态感知到该 `approval_id` 已决，立即安全解除正在挂起的客户端等待协程并继续推送后续 `session/update`，避免重复提交二次冲突决策或发生双端死锁。

---

## 六、安全边界与不变量

1. **零特权旁路**：ACP Server 与桌面端共享同一套 `ToolApprovalPolicy`、工作区源目录边界（`WorkspaceRoots`）、精确文件授权、`WriteScope` 与符号链接穿越拦截（`EnsureNoLinkTraversal`）。外部客户端无法通过 ACP 报文绕过 Core 的目录与权限治理。
2. **严格错误码契约**：
   - 未实现的 JSON-RPC 方法统一返回 **`-32601`**（`Method not found`）；
   - 缺失/相对路径/不存在的 `cwd`、未知 `sessionId`、非法 `modeId` / `modelId`、损坏的 Base64 `image` 或不兼容的 `protocolVersion` 统一返回 **`-32602`**（`Invalid params`）。
3. **凭据零落盘零打印**：无论是 `scripts/tinadec-acp.mjs` 还是 `TinadecCore.AcpServer`，在任何异常信息、诊断输出与 `stderr` 日志中**绝不打印**模型 API Key、`TINADEC_HOST_CONTROL_TOKEN`、`--host-token` 参数值或 `X-Tinadec-Host-Control` 请求头明文。生产与日常使用中应优先通过客户端 `env` 配置注入令牌，避免在操作系统进程命令行列表中暴露敏感参数。

---

## 七、排障指南（Troubleshooting）

### 1. Mode A 启动失败并提示 `host.lock` 文件被占用
- **原因**：`UserStorageHostLease` 规定同一存储根下的 `state/host.lock` 同一时刻只能由一个宿主进程持有，防止两个独立宿主把对方正在运行的任务误恢复为孤儿任务。此时通常说明 TinadecOffice 桌面端或另一个 `TinadecCore` 实例正在运行。
- **解决**：
  - 若希望与已打开的桌面端共享会话与模型配置，改用 **Mode B**（设置 `TINADEC_CORE_URL=http://127.0.0.1:48731` 与对应的 `TINADEC_HOST_CONTROL_TOKEN`）；
  - 若需要完全独立的沙箱实例，在启动参数中追加 `--storage-root <独立目录绝对路径>`。

### 2. Mode B 连接报 `403 host_authorization_required`
- **原因**：目标 `TinadecCore` 实例启用了 `UseTinadecStorageScopes` 宿主授权门禁，而 ACP 进程未携带或携带了不匹配的 `TINADEC_HOST_CONTROL_TOKEN`。
- **解决**：确保启动 `TinadecCore` 与配置 ACP 客户端 `env.TINADEC_HOST_CONTROL_TOKEN` 使用同一个令牌字符串（例如通过自定义固定环境变量启动本地开发栈时保持一致）。

### 3. 客户端提示握手超时或 `Unexpected token` / JSON 解析错误
- **原因**：通常是客户端 `command` 误指向了会向 `stdout` 输出文本的包装脚本（如 `scripts/dotnet.mjs`、`npm run` 横幅或用户 shell profile 中的 `echo`）。
- **验证方法**：在终端执行以下最小握手探测，确认 `stdout` 第一字节起即为紧凑 JSON 且无 BOM：
  ```bash
  printf '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}\n' | node scripts/tinadec-acp.mjs --storage-root /tmp/tinadec-acp-probe
  ```
  正常情况下，`stdout` 仅输出一行以 `{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,...}}` 开头的报文。

### 4. `session/new` 返回 `-32602 Invalid params`
- **原因**：传入的 `cwd` 为空、为相对路径、目录在磁盘上不存在，或直接指向了文件系统根目录（如 `/` 或 `C:\`，工作区契约明确禁止将磁盘根目录注册为工作区）。
- **解决**：在 IDE 中先打开一个具体的项目文件夹后再启动 ACP 会话。

### 5. `session/new` 返回的 `availableModels` 为空或提问提示未配置模型
- **原因**：Mode A 使用了全新的 `--storage-root` 目录，该隔离目录下尚未配置任何可用模型 Provider。
- **解决**：切换到复用现有配置的默认存储根（先关闭占用默认存储根的其他进程），或改用连接桌面端的 **Mode B** 直接复用已在桌面端模型中心配置好的全部模型与凭据。
