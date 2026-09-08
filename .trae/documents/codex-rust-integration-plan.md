> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# Codex Rust / .NET Agent 集成分层计划

## 一、核心原则

TinadecCode 的分层依据是**领域归属**，不是“能不能用 Rust”，也不是“能力来自 Codex 还是自研”。

- **Core = 通用智能体运行时**：短小精悍，负责所有 Tinadec 产品都需要的智能体运行、任务图、监督、审批、记忆、技能、工具路由、上下文压缩、事件、模型路由。
- **Code = 编程领域层**：负责只有 TinadecCode 才需要的代码、文件、shell、补丁、测试、审查、工作区观察等编程能力。
- **Rust = 能力来源与胶水对象**：Core 可以通过 cdylib/PInvoke 使用通用 Rust 能力，Code 可以通过 napi-rs 使用编程 Rust 能力；Rust 不决定能力归属。
- **Tinadec 双层智能体架构 = 产品语义层**：会议智能体、工具管理智能体、监督智能体、进化智能体、实时上下文压缩智能体和执行层智能体仍由 Core 定义和调度。

判断标准：如果做 TinadecDoc 或 TinadecData 也需要，属于 Core；如果只服务编程工作流，属于 Code。

---

## 二、总体架构

TinadecCore 以 Microsoft .NET Agent Framework 作为内部通用 Agent/Workflow 运行时，以 Tinadec 双层智能体架构作为产品级编排模型，以 Codex Rust 作为稳定上游能力来源。

```text
Desktop
  UI / 可视化 / 审批交互

Code
  Elysia Gateway + 编程领域工具层
  napi-rs -> Codex Rust 编程能力
  search_files / sandbox_exec / apply_patch / review_format / watcher

Core
  通用智能体运行时
  Tinadec TaskGraph -> Microsoft .NET Agent Framework workflow
  AgentCatalog / Model Router / Approval / EventHub / SQLite
  可选 cdylib -> Codex Rust 通用能力
```

Core 是唯一状态源。Microsoft Agent Framework 的 workflow 状态、Codex Rust 的工具结果、Code 层的执行结果都必须翻译回 Core 的 Run、TaskNode、AgentAssignment、StepResult、SupervisionFinding、ContextPack 和 EventEnvelope。

---

## 三、能力归属

### Core：通用智能体运行时

Core 保留并强化这些跨产品通用能力：

| 能力 | 归属理由 | 实现方向 |
|---|---|---|
| 任务图与运行态 | TinadecDoc/Data/Code 都需要 | Tinadec DTO + Microsoft Agent Framework workflow |
| Agent 定义与模式 | 智能体角色是产品语义 | `AgentCatalog` + Agent Center |
| 监督与审批 | 所有工具执行都需要安全边界 | Core policy + approval event |
| 模型路由 | 所有 Agent 都需要模型选择 | `Microsoft.Extensions.AI` + 现有 provider/route |
| 工具路由 | Core 决定工具能否被调用 | `IToolRegistry` |
| 记忆/技能/上下文压缩 | 跨产品长期智能能力 | C# 优先，可通过 cdylib 接入通用 Rust |
| 事件与审计 | UI、回放、监督都依赖 | EventEnvelope + SQLite |

Core 可以引入 Codex Rust 的通用能力，例如 protocol、guardian、memory、skills、hooks、rollout/config，但必须先满足“跨产品通用”标准，不能因为 Rust 里有某个模块就放入 Core。

### Code：编程领域层

Code 承接这些只对 TinadecCode 有意义的能力：

| 能力 | 归属理由 | 实现方向 |
|---|---|---|
| `search_files` | 编程工作区搜索 | Elysia endpoint -> napi-rs -> Codex Rust |
| `sandbox_exec` | 编程命令/测试执行 | 受 Core 审批约束 |
| `apply_patch` | 代码补丁应用 | 受 Core 审批约束 |
| `review_format` | 代码审查格式化 | 只读工具 |
| `file_watcher` | 工作区观察 | Code 层 runtime |

Code 不保存业务状态，不拥有任务图，不拥有 Agent 编排权。Code 只返回结构化工具结果，Core 负责审批、审计、证据链和 StepResult。

---

## 四、Microsoft .NET Agent Framework 引入

Microsoft .NET Agent Framework 是 Core 内部运行时，不是外部 provider。

- Tinadec TaskGraph 是源模型。
- `TaskGraphWorkflowCompiler` 将 TaskGraph、AgentAssignment、permission envelope、context pack 编译成 workflow step。
- `IAgentWorkflowRuntime` 执行 workflow，并把 step 状态翻译成 Tinadec 事件。
- Tinadec 双层智能体架构仍决定任务拆分、工具选择、监督、上下文压缩和进化策略。

`Microsoft.Extensions.AI` 替代当前单薄的 OpenAI-compatible 客户端，提供统一的 `IChatClient`、tool calling、streaming 和结构化输出能力，同时保留现有 provider/route/secret 存储。

---

## 五、Codex Rust 引入

Codex Rust 是稳定上游能力来源，分两条胶水线接入：

### Core cdylib 胶水

只接入跨产品通用能力，且延后于工具注册和 workflow runtime：

- guardian / policy
- memory
- skills
- hooks
- config
- rollout
- protocol 类型转换

### Code napi-rs 胶水

优先接入编程领域工具：

- `applyPatch`
- `execCommand`
- `sandboxExec`
- `searchFiles`
- `startWatcher`
- `formatReview`

第一阶段先用 TypeScript stub 固定接口和事件；第二阶段建立 `native/` workspace 和 napi-rs 包；第三阶段逐个替换 stub 为 Codex Rust 实现。

---

## 六、Core ↔ Code 工具协议

Core 通过 `IToolRegistry` 管理工具元数据：

- `id`
- `domain`
- `source`
- `risk`
- `requires_approval`
- `execute_endpoint`
- `capabilities`

Code 暴露编程工具端点：

- `POST /api/v1/code/tools/search_files/execute`
- `POST /api/v1/code/tools/sandbox_exec/execute`
- `POST /api/v1/code/tools/apply_patch/execute`
- `POST /api/v1/code/tools/review_format/execute`

Core 调用工具前必须检查风险和权限。shell、写文件、Git、联网、MCP/ACP 进程启动、apply patch 等变更类动作必须走现有审批流。

---

## 七、实施顺序

1. 更新架构文档，锁定“领域归属决定分层”的原则。
2. 在 Core 新增 `IAgentWorkflowRuntime`、`IToolRegistry`、`ICodeToolClient` 抽象和最小 DTO。
3. 在 Core 增加 `ToolRegistryService` 和 Microsoft Agent Framework runtime stub，先编译现有 TaskGraph，不执行真实写操作。
4. 在 Code 层新增 `/api/v1/code/tools/*/execute` stub，返回统一结构化结果。
5. Core 调用 Code 工具时先走注册表和审批策略，再把结果写回 StepResult/EventEnvelope。
6. 引入 `Microsoft.Extensions.AI` 和 Microsoft Agent Framework 包，逐步替换现有模型客户端与运行时 stub。
7. 建立 `native/` workspace、napi-rs 胶水包，逐步替换 Code 工具 stub 为 Codex Rust 实现。
8. Core cdylib 通用 Rust 能力后置接入，只引入确认跨产品通用的模块。

---

## 八、测试计划

- 文档验收：读者能按领域归属判断能力属于 Core 还是 Code。
- Core 单测：TaskGraph 能编译为 workflow plan；Code 工具进入 `IToolRegistry`；未审批变更类工具不会执行。
- Gateway/Code 测试：四个 Code 工具端点返回统一结构化结果和稳定错误码。
- Native 测试：napi-rs 加载成功；Codex Rust search/exec/apply patch 可独立调用。
- 端到端验收：会议智能体生成任务图，Core 用 .NET Agent runtime 调度，执行层通过 Code 调用 Codex Rust 编程工具，结果回写 Core 事件和任务图 UI。

---

## 九、默认假设

- Core 可以用 Rust，但不能因为 Rust 能做某事就把它放进 Core。
- Code 可以很强，但不能拥有 Tinadec 的任务图、监督、审批和 Agent 编排权。
- Codex Rust 上游以固定 commit 引入，升级通过显式同步流程完成。
- 第一阶段不实现完全自治写代码闭环，只跑通可见、可审计、可审批的 workflow 和工具调用。
