# TinadecCore：基于 Microsoft Agent Framework 的模块化骨架

## 总结

- 在 `TinadecCore/` 下建立 .NET 10 modular monolith：八个业务模块均为独立类库，可按项目引用裁剪。
- Microsoft Agent Framework 是基础平台：直接复用 Agent、Workflow、Context、Memory、Skills、Loop、Approval、Session、Checkpoint 与 OpenTelemetry 能力；Tinadec 只增加领域治理和状态权威。
- C# 负责公共契约、MAF 集成、DI、ASP.NET Core 和副作用；F# 负责上下文预算、提示词选择、记忆评分、防循环和状态转换等纯策略。
- 本轮只交付可构建骨架及 `health`、`harness/manifest`、`readiness`，不实现完整业务、SQLite schema 或 Gateway/Desktop 新功能。

## 目录与依赖结构

```text
TinadecCore/
├── AGENTS.md
├── README.md
├── Directory.Build.props
├── Directory.Packages.props
├── TinadecCore.slnx
├── DmaEA（双层智能体框架/混合智能涌现框架）                 # 核心多智能体协作模块
├── Models
├── Context
├── Prompts
├── Memory
├── Skills
├── LoopGuard
└── Lifecycle
└── tests/
    ├── TinadecCore.Architecture.Tests/
    ├── TinadecCore.AgentFramework.Tests/
    └── TinadecCore.Api.Tests/
```

依赖固定为：

```text
Contracts
   ↑
Abstractions ← Strategies(F#)
   ↑
八个业务模块及可选 provider
   ↑
Runtime
   ↑
ASP.NET Core API
   ↑ HTTP/SSE
Gateway → Desktop
```

- 业务模块之间不直接引用具体实现，通过 `Abstractions` 中的端口协作。
- 各相关模块可直接使用 MAF 公共类型；MAF 类型不得进入 HTTP DTO 或事件 envelope。
- 每个模块提供显式 `AddTinadec...()` 注册入口和 `ModuleDescriptor`，禁止反射扫描。
- `Runtime` 是默认全量组合；定制宿主可只引用需要的模块，实现编译期裁剪。

## 模块实现边界

- `DmaEA` 是模块之一，是整个框架的核心多智能体模块。它基于 Microsoft Agent Framework，实现 Tinadec 自有的双层 Agent 模型、动态创建、任务分派、协作通信、调度与结果汇总。
- `Models`：以 `IChatClient`、`ChatClientAgent` 和 MAF provider 为入口，Tinadec 只实现 provider 实例、凭据引用、模型路由、能力、错误归一化和 readiness，不重写模型 HTTP 客户端。
- `Context`：扩展 `AIContextProvider`，产生带证据、来源和 token 预算的 `ContextPack`；不组装最终 system prompt。
- `Prompts`：把片段、Agent 指令、Skill 贡献和 ContextPack 确定性组装到 MAF `ChatOptions.Instructions`/`AIContext`；完整提示词只允许出现在受控 preview 和调用内存中。
- `Memory`：复用 `AgentSession` 序列化、`ChatHistoryProvider`、`ChatHistoryMemoryProvider` 和 `Microsoft.Extensions.VectorData` 抽象；Tinadec 管理作用域、保留策略和 provenance，本轮不选择向量数据库。
- `Skills`：直接采用 `AgentSkillsProvider`、文件/类/内联 Skill、`SKILL.md` 和 SEP-2640，不定义 Tinadec 私有 Skill 格式；脚本执行以后接 TinadecTools，所有写操作仍由 Core 审批。
- `LoopGuard`：使用 MAF `LoopAgent`、`LoopEvaluator` 和硬迭代上限；F# 策略补充重复调用指纹、无进展、Agent 往返、连续错误及 token/time/tool-call budget 检测。
- `Lifecycle`：接收 MAF middleware、workflow event、session/checkpoint 和取消信号，维护 Core 自有的 run/task/agent/tool/approval 状态和审计事件。

F# 策略内核按领域文件拆分，但保持单一 `.fsproj`，避免项目数量膨胀。跨语言边界只使用 C# 定义的 records、enums、数组、`IReadOnlyList<T>`、`Task`/`ValueTask`；不暴露 F# DU、list、option、async 或 curried function。在需要f#时才按需使用

MAF 是技术底座，DmaEA等模块 是建立在其上的 Tinadec 双层多智能体框架，两者不是同一个概念。

## MAF 版本与公共接口

- 集中锁定正式版：
  - `Microsoft.Agents.AI.Abstractions` `1.13.0`
  - `Microsoft.Agents.AI` `1.13.0`
  - `Microsoft.Agents.AI.Workflows` `1.13.0`
  - `Microsoft.Agents.AI.OpenAI` `1.13.0`
- 按用户选择允许 RC：建立可选 `Anthropic` provider 项目并锁定 `Microsoft.Agents.AI.Anthropic` `1.1.0-rc1`，默认不启用。
- 不引入 preview/alpha Hosting、DurableTask、Foundry Hosting、MAF MCP 或独立 Harness 包；API 继续使用标准 ASP.NET Core，MCP 继续沿用官方 `ModelContextProtocol` SDK。
- OpenAI 初始采用稳定的 `IChatClient`/Chat Completions 路径；Experimental Responses 能力保留 feature gate，不默认注册。
- 所有版本集中在 `Directory.Packages.props`，禁止浮动版本；上游升级只修改中央版本并运行兼容测试。

新增公共骨架类型：

- `ITinadecCoreBuilder` 与八个显式模块注册扩展。
- `ModuleDescriptor`/`ModuleDescriptorDto`：模块 id、版本、依赖、能力、语言、MAF primitives、注册状态。
- `HarnessManifestDto.modules` 和 `framework` 为增量字段；现有 manifest 字段保持不变。
- `EventEnvelope` 继续使用 v1.0 和 snake_case，不暴露 MAF/F# 类型。

最小 API：

- `GET /api/v1/health`：保持旧 `{name,status,version,time}` 兼容结构。
- `GET /api/v1/harness/manifest`：返回双层 Agent、MAF 版本和八个模块清单；尚无工具/Agent 实例时返回空集合。
- `GET /api/v1/readiness`：MAF assemblies 可加载时为 ready；未配置模型、存储等模块使用 warning，并附 `module_state:not_configured`，不伪装为可运行。
- Gateway 无需修改，继续透明代理这三个端点；本轮不新增 Memory、Skills、LoopGuard 或 Lifecycle CRUD API。

同步更新根解决方案和 `package.json` 的 Core 项目路径；旧 Core/Contracts 测试保留为需求证据，但从活动解决方案移除。更新根及 Core `AGENTS.md`、架构文档和启动说明。

## 测试与验收

- 构建每个模块、Core 子解决方案和根 `TinadecOffice.slnx`，确认 .NET 10/C#/F# 混合引用无循环。
- 架构测试强制：
  - Contracts 不依赖 MAF、ASP.NET、F# 或 UI。
  - 只有 API 项目使用 Web SDK。
  - 模块之间无具体实现引用。
  - Gateway/Desktop/TinadecTools 不进入 Core 领域模块。
  - MAF 版本全部由中央文件管理。
- MAF smoke test 使用假 `IChatClient` 创建 Agent，并构建最小 Workflow；不访问外部模型。
- F# interop test 从 C# 调用策略内核，并验证公共签名不泄漏 FSharp 类型。
- API 测试验证三个端点返回 200、snake_case、八个模块和正确的 `not_configured` readiness。
- 裁剪测试使用只注册 Agents、Models、Lifecycle 的最小 DI 容器，证明 Memory/Skills 等模块不是运行时必需依赖。
- 运行 Gateway、Desktop 现有测试及 `npm run ai:ponytail:validate`；不需要模型密钥或网络调用。
- 实施前初始化并同步本地 CodeGraph，完成后对 Runtime/API 和模块注册入口执行影响检查，不提交索引产物。

## 已锁定假设

- “可裁剪”指程序集与显式注册级裁剪，本轮不要求 IL trimming 或 NativeAOT。
- 允许正式版和 RC 包，但不允许 preview、alpha 或跟随 `main`。
- 本轮不实现 SQLite schema、真实 provider 调用、向量存储、完整双层运行流或工具执行。
- Core 始终是状态、审批、路由、事件和审计权威；MAF session/checkpoint 仅是执行运行时状态。
