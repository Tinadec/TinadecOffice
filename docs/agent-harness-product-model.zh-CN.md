# TinadecOffice 智能体 Harness 集成模型

> 本文保留当前 Desktop -> Gateway -> Core -> Tool layer 的集成部署视图。TinadecOffice 四产品矩阵、TinadecCore 的权威定位及 DmaEA 目标架构以 [TinadecCore 产品定义与 DmaEA 架构基线](tinadec-core-product-definition.zh-CN.md) 为准；两者冲突时以后者为准。

本文描述现有 Core、Tool layer 和 Desktop UI 如何协作，不再作为四产品是否独立交付的定义依据。

## 核心判断

TinadecOffice 不是“一个带聊天框的代码编辑器”。它的本质是一个以通用智能体编排为中心的桌面工作台：

- **Core** 是通用智能体编排模型，也是可复用的 agent harness。
- **Tool layer** 是工具层，为 harness 提供可发现、可审批、可执行的工具能力。
- **Code** 是 Tool layer 里的一个内置工具套件，专注于代码、项目和开发环境能力。
- **Desktop** 是 UI 呈现层，把 Core 的编排状态和 Tool layer 的工具能力组织成可理解、可操作的产品体验。

这三层应保持清晰分工。Core 不应被写成只服务 TinadecOffice Desktop 的业务后端；Tool layer 不应持有编排状态；Desktop 不应把状态、审批、路由和工具策略藏在前端局部状态里。

## 三层产品职责

### Core：通用智能体编排模型

Core 是产品的智能体操作系统内核。它负责定义一个任务如何被理解、拆解、分派、监督、执行、记录和审计。

Core 的产品职责包括：

- 会话、消息、项目、事件和审批的权威状态。
- 双层智能体编排模型：**治理层**（`operation`）主动理解、协调、维护上下文、监督和提出演进候选；**执行层**（`execution`）在明确任务、权限和预算内产出证据。`planning` 仅是迁移期的兼容输入名；新的 Core 契约、配置和展示统一使用 `operation`。
- 任务图、任务节点、执行分派、上下文包、监督发现和 step result。
- 模型 provider、模型 route、agent profile、agent mode、tool descriptor 和权限策略。
- 审批门、风险模型、trace、debug API 和可审计事件流。
- 面向不同工具层、UI 或外部 harness consumer 的稳定 API。

Core 的设计目标是通用性。它应能驱动 TinadecOffice，也应能被其它产品外壳、CLI、IDE 插件或自动化 runtime 复用。换句话说，Core 应表达“agent work 如何被组织”，而不是表达“某个页面如何展示”。

### Tool Layer：工具层与能力提供者

Tool layer 是 Core 下方的工具与能力层。它提供面向不同工作域的具体 feature，让 agent harness 能够真正操作外部世界、读取证据、执行动作和返回结构化结果。

Tool layer 的产品职责包括：

- 定义工具能力、工具元数据、风险级别、输入输出契约和执行适配器。
- 执行 Core 授权后的工具请求，并把结果、证据、错误和诊断结构化返回。
- 接入内置工具、native 工具、MCP 工具、ACP 工具、浏览器工具、文档工具、代码工具和其它未来工具。
- 保持工具实现和编排策略分离：工具层负责“能做什么、如何执行”，Core 负责“是否应该做、何时做、如何审计”。

### Code：Tool Layer 中的代码工具套件

Code 不是单独的产品层，而是 Tool layer 里的一个重要内置工具套件。它聚焦开发者工作流，为 Core 的 agent harness 提供代码和项目相关能力。

Code 工具套件的职责包括：

- **项目模板**：创建、初始化、识别和管理常见项目结构。
- **bash-like environment**：为智能体和用户提供类 shell 的命令执行、环境变量、工作目录、输出流和错误捕获能力。
- **内置调试**：暴露运行、断点、日志、trace、诊断和复现实验能力。
- **内置 code editor**：提供文件浏览、编辑、diff、patch、符号/全文检索和代码审阅能力。
- **Git worktree manager**：管理分支、worktree、diff、提交、变基、冲突和隔离执行空间。
- **本地工具 glue**：通过 Codex/Tool layer 能力提供搜索、读取、grep、patch、sandbox、review formatting 等底层工具。

Code 不决定某个 agent 是否可以执行危险动作。它可以声明工具能力、执行工具请求、返回结构化结果，但审批、权限、状态记录和策略判断属于 Core。

### Desktop：UI 呈现层

Desktop 是产品体验层。它不应成为业务状态的第二来源，而应把 Core 和 Tool layer 的能力以用户可理解的方式呈现出来。

Desktop 的产品职责包括：

- 展示聊天、任务图、执行分派、上下文包、监督发现、审批和事件流。
- 提供 agent、model provider、model route、工具绑定和权限模式的配置界面。
- 呈现 Tool layer feature，包括 Code 工具套件提供的项目模板、终端环境、调试、编辑器、diff、worktree 管理。
- 提供 Agent Debug Studio、trace timeline、agent graph、metrics 和 simulation/replay UI。
- 通过交互设计帮助用户理解“当前 agent harness 正在做什么、为什么需要审批、下一步会发生什么”。

Desktop 的核心价值不是保存状态，而是降低 Core 和 Tool layer 的认知成本。它应该让复杂编排可见、让风险可解释、让工具操作可控。

## 产品数据流

典型用户请求应按以下方向流动：

1. 用户在 Desktop 发起目标、选择项目、配置模式或审批动作。
2. Desktop 调用 Gateway，Gateway 代理到 Core。
3. Core 创建或更新会话状态，生成 run、task graph、agent assignment、context pack 和 supervision finding。
4. Core 根据工具描述和权限策略决定哪些只读工具可自动执行，哪些工具必须进入审批流程。
5. Core 通过工具适配器请求 Tool layer；当前代码工具路径由 Code tool adapter 承接。
6. Tool layer 调用对应工具实现；Code 工具套件可调用本地能力并返回结构化结果。
7. Core 将结果写回 step result、event、trace 和状态存储。
8. Desktop 通过 HTTP/SSE/WebSocket 刷新 UI，让用户看到消息、任务、工具结果、审批和调试信息。

重要边界：状态回写必须回到 Core。Tool layer 的工具输出和 Desktop 的交互结果都不应绕过 Core 形成隐式状态。

## 双层智能体编排模型

TinadecOffice 的智能体模型分为两层：

- **治理层（`operation`）**：主动智能体，负责理解意图、会议入口、全局协调、上下文维护、能力建议、监督质量和提出演进候选。
- **执行层（`execution`）**：任务规划和 worker 在明确任务节点、权限边界和工具约束下完成具体工作并交付证据。

`planning` 是旧数据和旧调用中的兼容别名，不是第三层也不是新的对外层名。读取旧值时可规范化为 `operation`；新 API、事件、配置版本和 UI 只能写出正式的 `operation` 值。

这种分层的意义是让“理解、监督、授权”和“执行、取证、修改”分离。治理层负责提出结构化计划和控制风险；执行层负责可审计地完成任务。任何 mutating action 都应能追溯到任务节点、agent assignment、审批记录和工具结果。

### 会议入口、生成与记忆边界

- **会议智能体是唯一用户入口。** 只有它可以生成用户可见的正式答复；其它智能体只能提交进度、证据、建议、监督结论或安全错误，再由会议智能体汇总。它们不得绕过 Core 调用工具。
- **运行期 spawn 是 Core 编排能力。** 具有 `agent.spawn` 能力的会议智能体或执行层任务规划智能体，必须带着目标、成功标准、父实例、上下文选择器、模型、工具/资源范围和预算创建子智能体。普通 worker 只能提出 spawn 请求。子实例不得扩大父权限、跨越层级，或取得 `direct_user_output`、正式记忆写入、候选晋升权限；默认在 run 结束时释放，并保留 parent/child lineage 和审计记录。
- **候选不等于正式资产。** 演化行为只能生成带来源、证据、适用条件、失效条件和置信度的记忆或 agent 候选。候选不参与长期检索，也不自动成为永久 profile；人工评测并晋升后才建立不可变正式版本。拒绝、撤销、替代和使用反馈同样是可审计事实。
- **会话与长期记忆不同。** 当前会话历史、摘要和已审核的长期记忆可以参与上下文包；完整正文保留在不可变 ContentStore，关系库和事件只保存引用、哈希、计数和摘要。长期记忆审核状态机与工具审批状态机必须分离。

### 全双工运行契约

全双工是应用层语义，而不是要求客户端一直占用一个 HTTP 连接。后台 run 必须独立于 SSE 读取连接持续运行；用户可以在其运行时查询状态、补充约束、调整目标、创建另一项任务、暂停、恢复或取消。会议智能体将这些消息分类后绑定已有 run 或创建新 run。

共享会话状态有单调递增的 `context_revision`。每个上下文补丁和目标调整都带基准版本；过期结果不得覆盖较新的目标或约束，必须被拒绝、合并为安全补丁，或触发受影响节点的重规划。run 需要覆盖 `understanding`、`executing`、`replanning`、`awaiting_approval`、`paused`、`reviewing`、`completed`、`failed`、`cancelled` 等可恢复状态。

### 配置与模式契约

内置、带注释的 TOML 是只读基线；工作区覆盖、人工晋升和不可变版本属于 Core 的关系型控制面。解析优先级是 TOML 基线后叠加工作区版本，且每个 run 必须冻结最终配置版本和内容哈希。有效热重载只影响新 run；无效编辑必须保留上一有效快照，并进入 readiness 诊断。

默认配置规定：`conversation` 开放 `plan`、`spec`、`ask`、`vibe`、`auto`、`agent` 六种 agent mode，默认 `auto`；`space` 仅开放 `agent`，绑定 `space.full_duplex`。`im` 和 `hub` 分别只是 `conversation`、`space` 的兼容别名。默认生成预算为深度 2、每 run 8 个生成实例、4 个并行 worker、每会话 2 个活跃 run；配置可收紧，但子智能体不可突破父级或 run 的上限。

## 边界规则

- Core 拥有状态、编排、审批、模型路由、工具策略和事件日志。
- Tool layer 拥有工具实现、工具能力目录、执行适配器和结构化工具结果。
- Code 是 Tool layer 中的代码工具套件，拥有开发环境能力和项目操作 feature。
- Desktop 拥有交互、可视化、配置表单和用户体验流程。
- Gateway 是代理和工具桥，不持久化核心状态。
- 只读工具可以由 Core 策略自动调度；写文件、shell、Git、外部网络、MCP/ACP 等高风险动作必须保留人工检查点。
- Desktop 的 mode、permission、agent configuration 等 UI 控件最终都应落到 Core 可审计的请求或配置中。
- Tool layer 的新 feature 应先暴露为工具能力或服务能力，再由 Core 决定如何纳入 agent harness，最后由 Desktop 做 UI 呈现。
- Code 的新 feature 应作为 Tool layer 中的代码工具能力注册，而不是作为绕过 Tool layer 的独立产品层。

## 对未来功能设计的含义

新增能力时，应先回答三个问题：

1. **这是编排语义吗？**  
   如果它定义任务、状态、权限、审批、模型路由、agent 行为或审计事件，它属于 Core。

2. **这是工具能力吗？**  
   如果它提供编辑、调试、shell、模板、worktree、检索、patch、浏览器操作、文档操作、外部系统调用或项目操作，它属于 Tool layer。其中代码和项目相关能力通常属于 Code 工具套件。

3. **这是用户如何看见和控制能力吗？**  
   如果它主要是布局、表单、图形、交互或信息呈现，它属于 Desktop。

这个判断顺序可以避免把产品做成前端状态拼图，也避免把某个工具套件变成一个绕过 Core 的第二套 agent runtime。

## 一句话产品定位

TinadecOffice 是一个桌面智能体工作台：Core 提供通用 agent harness，Tool layer 提供可执行工具能力，Code 是其中的代码工具套件，Desktop 把编排、工具和风险控制呈现为可操作的 UI。

## 实现状态（2026-08-18）

上述章节定义的是产品契约，不把目标能力当作已交付功能。当前工作树中，`TinadecCore/DmaEA/Configuration/default-agent-runtime.toml` 与 `AgentRuntimeConfigurationStore` 已提供带校验的 TOML 基线、`im`/`hub` 别名、`planning` 到 `operation` 的配置层规范化，以及“该 store 被解析后有效快照热重载、无效修改保留旧快照”的进程内基础。`agent_instances`、`agent_candidates` 和 `runtime_profile_overrides` 也已有关系投影。

自 2026-08-18 起全双工契约已实现并由 Core 测试套件覆盖：`POST /api/v1/sessions/{id}/invoke-stream` 运行持久化 `FullDuplexRunEngine`，含 client-message-id 幂等准入、`context_revision` 快照与会议上下文补丁、治理协调→任务规划→执行→监督→meeting 终态化、带预算的 worker spawn/lineage、持久 SSE（ack/delta/done/error）回放/跟随、run 控制（取消/暂停/恢复）、活跃 run 限流与租约检查点重启恢复；`agent-evolution/proposals` GET/generate/promote/reject 提供候选审核。工具链路端到端打通：Core-owned `TinadecToolsProcessManager` 按工作区根托管真实 manifest-v2 子进程，`ToolManifestSnapshotResolver` 每 run 冻结授权 manifest，`ToolDispatcher` prepare/resume 持久化执行并一次性消费审批，真实 run→审批→恢复→`write_file` E2E 测试证明全链路。`GET /api/v1/tool-layer-readiness` 报告真实 manifest 与执行层 agent scopes。

仍待实现：长期检索注入（晋升/撤销、已审核记忆检索）、工作区 runtime-profile 覆盖与 readiness 诊断、scheduling 与 `tools/shell`（501），以及 Gateway/Desktop 尚未将普通聊天切换到此全双工契约。
