# TinadecOffice Agent Debug Studio — 完整实施计划

> **文档版本**: v1.0  
> **创建日期**: 2026-05-25  
> **状态**: Planning  
> **目标**: 为 TinadecOffice 构建一套面向 Agent 时代的交互式调试工具

---

## 目录

1. [项目背景与动机](#1-项目背景与动机)
2. [调研总结与设计哲学](#2-调研总结与设计哲学)
3. [架构总览](#3-架构总览)
4. [模块详细设计](#4-模块详细设计)
5. [API 契约](#5-api-契约)
6. [数据模型与追踪 Schema](#6-数据模型与追踪-schema)
7. [前端组件树](#7-前端组件树)
8. [实施路线图与任务拆解](#8-实施路线图与任务拆解)
9. [测试策略](#9-测试策略)
10. [风险与缓解](#10-风险与缓解)
11. [验收标准](#11-验收标准)
12. [参考项目索引](#12-参考项目索引)

---

## 1. 项目背景与动机

### 1.1 现状问题

TinadecOffice 是一个多层架构的 AI 编码助手平台，当前**没有任何结构化调试能力**：

| 能力 | 现状 |
|------|------|
| 结构化日志 | ❌ 仅 ASP.NET Core 默认控制台输出 |
| 分布式追踪 | ❌ 完全没有 |
| 指标采集 | ❌ 完全没有 |
| 进程诊断 | ⚠️ 仅有 `DoctorService`（检查外部工具是否安装） |
| 事件系统 | ✅ `EventHub` + SSE 事件流（但无持久化 trace 文件） |
| 错误追踪 | ❌ 异常直接抛出或返回字符串 |
| 日志持久化 | ⚠️ `output/logs/` 仅有 stdout/stderr 重定向，非结构化 |
| 测试覆盖 | ⚠️ 仅有基础单元测试 |

核心痛点：

- `CoreStore.cs` 一个文件 2,909 行，20+ 数据表，100+ CRUD 操作——SQLite 出问题只能靠断点
- `OrchestratorService` 协调 Agent 工作流、工具执行、审批流程——最复杂的业务逻辑无 span 追踪
- 用户消息 → Gateway → Core → Model API → 返回，横跨多个进程，无 trace_id 贯穿
- Agent 编排是最容易出问题的地方，但没有指标来回答"是模型响应慢，还是工具执行慢，还是审批等待慢？"

### 1.2 为什么传统调试不够

传统调试器的哲学是**确定性重放**——设断点、步进、查看变量。Agent 系统本质上是**非确定性**的：

| 传统调试 | Agent 调试 |
|---------|-----------|
| 确定性执行，断点可复现 | 非确定性推理，每次路径不同 |
| 单线程/有限并发 | 多 Agent 并行，子 Agent 委派 |
| 变量值是标量/对象 | 上下文窗口是高维 Token 流 |
| 错误是代码 bug | 错误可能是推理偏差、工具失败、审批阻塞 |

因此我们需要一套**专为 Agent 设计**的调试工具。

---

## 2. 调研总结与设计哲学

### 2.1 调研项目清单

| 项目 | 类型 | 核心启示 |
|------|------|---------|
| **LangGraph Studio** | Agent IDE | 首个 Agent IDE：可视化图结构 + 中途编辑状态 |
| **LangSmith v4** | LLM 可观测性平台 | 从"请求级追踪"进化到"操作级可查询"——每个 Observation 独立可查 |
| **Langfuse** | 开源 LLMOps | 三层追踪体系：Trace → Span → Observation；成本追踪 |
| **Arize Phoenix** | ML/LLM 可观测性 | Notebook-first 零配置追踪；Embedding 分析 |
| **Codex rollout-trace** | Rust Agent 追踪 | Trace Bundle (JSONL) + Reducer (语义还原) + debug-client (CLI 调试) |
| **Codex otel** | Rust OpenTelemetry | W3C Trace Context 跨进程传播；session telemetry 事件 |
| **Codex debug-client** | Rust 调试客户端 | 交互式 CLI：send turn, resume thread, auto-approve |
| **T3 Code observability** | TS 可观测性 | 四层体系：NDJSON 追踪 + 指标 + 进程诊断 + Trace 诊断分析 |
| **Kilocode** | VSCode AI Agent | Context 可视化（Token 用量）；多模式（Debug Mode）；多模型比较 |
| **Cline** | VSCode AI Agent | 检查点回滚；Token/Cost 追踪；终端集成 |
| **AgentSight** | 学术 eBPF 方案 | 系统级可观测性：弥合 Agent 高层意图与底层系统调用之间的语义鸿沟 |
| **AgentTrace** | 学术框架 | 结构化日志框架：Agent 推理、状态变更、环境交互的审计追踪 |

### 2.2 设计哲学

**五条核心原则**：

1. **First-Class Agent Citizen** — Agent 是调试的一等公民。追踪单位不是 HTTP 请求，而是 Agent Turn。
2. **Time-Travel Debugging** — 任何历史 Run 都可以回溯、重放、干预。参考 LangGraph Studio 的状态编辑。
3. **Live & Replay 双模** — 既能实时追踪正在运行的 Agent，也能离线分析历史轨迹。参考 Codex rollout-trace。
4. **独立窗口** — 作为 Electron 独立 BrowserWindow，不干扰主工作区。参考 LangGraph Studio Desktop App。
5. **跨语言统一** — C# Core 追踪与 Rust Codex 追踪统一 trace schema，全链路关联。参考 Codex otel W3C Trace Context。

---

## 3. 架构总览

### 3.1 系统架构图

```
┌──────────────────────────────────────────────────────────────┐
│                TinadecOffice Desktop (Electron)                 │
│                                                               │
│  ┌─────────────────────┐    ┌──────────────────────────────┐ │
│  │    Main Window       │    │  Agent Debug Studio Window   │ │
│  │    (Chat + Settings) │    │  (独立 BrowserWindow)        │ │
│  │                      │    │                              │ │
│  │  - ChatPanel         │    │  ┌──────────┬─────────────┐ │ │
│  │  - ComposerBar       │    │  │  Trace   │  Inspector  │ │ │
│  │  - MessageList       │    │  │  Timeline│  Panel      │ │ │
│  │  - OrchestrationTab  │    │  │  (瀑布图) │  (详情面板)  │ │ │
│  │  - EventsTab         │    │  ├──────────┴─────────────┤ │ │
│  │  - ApprovalTab       │    │  │  Agent Graph Canvas     │ │ │
│  │                      │    │  │  (拓扑可视化)            │ │ │
│  │                      │    │  ├────────────────────────┤ │ │
│  │                      │    │  │  Simulator / Replay Bar │ │ │
│  │                      │    │  │  (模拟/回放控制)         │ │ │
│  │                      │    │  ├────────────────────────┤ │ │
│  │                      │    │  │  Metrics Dashboard      │ │ │
│  │                      │    │  │  (指标看板)              │ │ │
│  └─────────────────────┘    │  └────────────────────────┘ │ │
│                              └──────────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
         │                                   │
         │  SSE (/api/v1/events)             │  WebSocket (/api/v1/debug/ws)
         │                                   │  + REST (/api/v1/debug/*)
         ▼                                   ▼
┌──────────────────────────────────────────────────────────────┐
│                  TinadecCore (C# .NET 10)                     │
│                                                               │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │              NEW: Agent Trace Collector                  │ │
│  │                                                          │ │
│  │  ┌───────────────────┐  ┌─────────────────────────────┐ │ │
│  │  │ OpenTelemetry SDK │  │ NDJSON Trace Writer         │ │ │
│  │  │ (Tracing+Metrics) │  │ (本地文件持久化)             │ │ │
│  │  └───────────────────┘  └─────────────────────────────┘ │ │
│  │  ┌───────────────────┐  ┌─────────────────────────────┐ │ │
│  │  │ EventHub Bridge   │  │ Trace Diagnostics Service   │ │ │
│  │  │ (SSE→trace spans) │  │ (失败分析/慢span/聚合)      │ │ │
│  │  └───────────────────┘  └─────────────────────────────┘ │ │
│  └─────────────────────────────────────────────────────────┘ │
│                                                               │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────────────┐ │
│  │ Orchestrator │ │ Agent        │ │ Tool Execution       │ │
│  │ Service      │ │ Workflow     │ │ Service              │ │
│  │              │ │ Runtime      │ │                      │ │
│  └──────────────┘ └──────────────┘ └──────────────────────┘ │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────────────┐ │
│  │ CoreStore    │ │ EventHub     │ │ NEW: Debug API       │ │
│  │ (SQLite)     │ │              │ │ Controller           │ │
│  └──────────────┘ └──────────────┘ └──────────────────────┘ │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────────────┐ │
│  │ OpenAi       │ │ Secret       │ │ NEW: Simulation      │ │
│  │ CompatClient │ │ Protector    │ │ Service              │ │
│  └──────────────┘ └──────────────┘ └──────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
         │
         │  HTTP (CodeToolClient)
         ▼
┌──────────────────────────────────────────────────────────────┐
│              Gateway (Elysia BFF)                             │
│              NEW: /debug/* 代理路由                           │
└──────────────────────────────────────────────────────────────┘
```

### 3.2 数据流

```
实时模式:
  用户消息 → Core Orchestrator → Agent Trace Collector (创建 span)
           → Tool Execution → Agent Trace Collector (子 span)
           → EventHub → SSE → Main Window (用户可见)
                       → NDJSON Writer (持久化)
                       → WebSocket → Debug Studio (实时追踪)

回放模式:
  Debug Studio → 读取 NDJSON trace 文件 → 还原 span 树 → Timeline 渲染

模拟模式:
  Debug Studio → Simulation Service → 注入消息/修改状态/强制审批 → Orchestrator
              → Agent Trace Collector (记录模拟 span，标记 simulated=true)
```

---

## 4. 模块详细设计

### 4.1 模块 A：Agent Trace Collector（C# 端）

**职责**: 在 TinadecCore 运行时采集结构化追踪和指标数据。

#### 4.1.1 新增文件清单

| 文件路径 | 职责 |
|---------|------|
| `src/TinadecCore/Tracing/AgentTracing.cs` | OpenTelemetry 初始化：TracerProvider + MeterProvider |
| `src/TinadecCore/Tracing/SpanDefinitions.cs` | Span 名称常量 + 属性键常量 |
| `src/TinadecCore/Tracing/MetricDefinitions.cs` | 指标名称常量 + Counter/Timer 定义 |
| `src/TinadecCore/Tracing/NdjsonTraceExporter.cs` | 自定义 NDJSON 文件导出器（对齐 Codex rollout-trace 格式） |
| `src/TinadecCore/Tracing/TraceDiagnosticService.cs` | 追踪分析：失败模式、慢 span、聚合指标 |
| `src/TinadecCore/Tracing/ActivitySourceHelper.cs` | ActivitySource 工厂 + 便捷扩展方法 |

#### 4.1.2 NuGet 依赖

```xml
<PackageReference Include="OpenTelemetry" Version="1.11.2" />
<PackageReference Include="OpenTelemetry.Exporter.Console" Version="1.11.2" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.11.2" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.11.2" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.11.1" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.11.1" />
<PackageReference Include="OpenTelemetry.Instrumentation.SqlClient" Version="1.11.0-beta.2" />
```

#### 4.1.3 Span 边界定义

| Span 名称 | 触发位置 | 类型 | 关键属性 |
|-----------|---------|------|---------|
| `agent.turn` | `OrchestratorService.CreateRunForMessage` | 顶层 | `session_id`, `run_id`, `agent_id`, `agent_type`, `user_message_id` |
| `agent.inference` | `OpenAiCompatibleClient.CreateAssistantReplyAsync` | 子 span | `model`, `provider_instance_id`, `driver`, `connection_kind`, `token_in`, `token_out`, `latency_ms` |
| `agent.tool_dispatch` | `OrchestratorService.DispatchReadOnlyToolsAsync` | 子 span | `run_id`, `tool_id`, `task_node_id`, `auto_dispatch` |
| `agent.tool_execution` | `ToolExecutionService.ExecuteAsync` | 子 span | `tool_id`, `adapter_id`, `permission_mode`, `status`, `requires_approval` |
| `agent.approval` | `ToolExecutionService` 审批等待 | 子 span | `approval_id`, `kind`, `summary`, `decision`, `wait_ms` |
| `agent.supervision` | `OrchestratorService` 监督 | 子 span | `run_id`, `severity`, `category`, `finding_status` |
| `agent.context_pack` | `OrchestratorService` 上下文打包 | 子 span | `context_pack_id`, `token_budget`, `compression_ratio` |
| `agent.sub_agent_spawn` | 未来多 Agent 扩展 | 子 span | `child_agent_id`, `child_agent_type`, `parent_agent_id` |
| `agent.workflow_compile` | `AgentWorkflowRuntime.Compile` | 子 span | `run_id`, `runtime`, `step_count` |
| `sqlite.query` | `CoreStore` 所有数据库操作 | 基础设施 | `table`, `operation` (select/insert/update/delete), `duration_ms` |
| `model.request` | `OpenAiCompatibleClient` HTTP 调用 | 基础设施 | `base_url`, `model`, `status_code`, `has_api_key` |

#### 4.1.4 指标定义

```
# Agent Turn 指标
tinadec_agent_turn_duration         Timer    Agent turn 耗时 (按 agent_type, status 分)
tinadec_agent_turns_total           Counter  Agent turn 总数 (按 agent_type, status 分)

# 工具执行指标
tinadec_tool_execution_duration     Timer    工具执行耗时 (按 tool_id, status 分)
tinadec_tool_executions_total       Counter  工具执行总数 (按 tool_id, status 分)

# Model API 指标
tinadec_model_request_duration      Timer    Model API 请求耗时 (按 model, provider, status 分)
tinadec_model_requests_total        Counter  Model API 请求总数 (按 model, provider, status 分)
tinadec_model_tokens_total          Counter  Token 消耗 (按 direction: input/output 分)

# 审批指标
tinadec_approval_wait_duration      Timer    审批等待耗时 (按 kind, decision 分)
tinadec_approvals_total             Counter  审批总数 (按 kind, decision 分)

# 编排指标
tinadec_orchestration_duration      Timer    编排全流程耗时
tinadec_orchestration_runs_total    Counter  编排 Run 总数 (按 status 分)

# SQLite 指标
tinadec_sqlite_query_duration       Timer    SQLite 查询耗时 (按 table, operation 分)
tinadec_sqlite_queries_total        Counter  SQLite 查询总数 (按 table, operation 分)
```

#### 4.1.5 NDJSON Trace 文件格式

对齐 Codex rollout-trace 的 `trace.jsonl` 格式：

```json
{
  "schema_version": "1.0.0",
  "seq": 1,
  "trace_id": "trace_abc123",
  "span_id": "span_def456",
  "parent_span_id": null,
  "name": "agent.turn",
  "kind": "INTERNAL",
  "start_time_unix_nano": "1716634530000000000",
  "end_time_unix_nano": "1716634535000000000",
  "duration_ms": 5000,
  "status": "OK",
  "attributes": {
    "session_id": "sess_xxx",
    "run_id": "run_yyy",
    "agent_type": "meeting"
  },
  "events": [
    {
      "name": "model.request.started",
      "time_unix_nano": "1716634531000000000",
      "attributes": { "model": "gpt-4o", "provider": "openai_default" }
    }
  ],
  "resource": {
    "service.name": "tinadec-core",
    "service.version": "0.1.0"
  }
}
```

文件位置：`output/logs/core.trace.ndjson`  
轮转策略：10MB/文件，最多 10 个文件（对齐 T3 Code 的 `server.trace.ndjson`）

#### 4.1.6 配置项

```json
// appsettings.json 新增
{
  "TinadecTracing": {
    "Enabled": true,
    "TraceFilePath": "output/logs/core.trace.ndjson",
    "TraceMaxBytes": 10485760,
    "TraceMaxFiles": 10,
    "TraceBatchWindowMs": 200,
    "TraceMinLevel": "Info",
    "OtlpTracesUrl": null,
    "OtlpMetricsUrl": null,
    "OtlpServiceName": "tinadec-core",
    "OtlpExportIntervalMs": 10000,
    "ConsoleExporterEnabled": false
  }
}
```

环境变量覆盖：
- `TINADEC_TRACING_ENABLED` — 是否启用追踪
- `TINADEC_TRACE_FILE` — 追踪文件路径
- `TINADEC_OTLP_TRACES_URL` — OTLP traces 导出地址
- `TINADEC_OTLP_METRICS_URL` — OTLP metrics 导出地址

---

### 4.2 模块 B：Debug API 端点（C# 端）

**职责**: 为 Debug Studio 前端提供数据查询和模拟控制接口。

#### 4.2.1 REST API 端点

| 方法 | 路径 | 职责 |
|------|------|------|
| GET | `/api/v1/debug/traces` | 查询追踪列表，支持 sessionId/runId/limit 过滤 |
| GET | `/api/v1/debug/traces/{traceId}` | 单条追踪详情（含完整 span 树） |
| GET | `/api/v1/debug/spans` | 查询 span 列表，支持 name/status/duration 过滤 |
| GET | `/api/v1/debug/metrics` | 指标聚合查询，支持 window/bucket/groupBy 参数 |
| GET | `/api/v1/debug/snapshot/{sessionId}` | 实时状态快照（当前所有活跃 agent/run/tool 状态） |
| GET | `/api/v1/debug/diagnostics` | Trace 诊断报告（失败模式、慢 span、常见故障） |
| GET | `/api/v1/debug/processes` | 进程资源诊断（对齐 T3 Code ProcessDiagnostics） |

#### 4.2.2 Simulation API 端点

| 方法 | 路径 | 职责 |
|------|------|------|
| POST | `/api/v1/debug/simulate/message` | 注入模拟用户消息 |
| POST | `/api/v1/debug/simulate/model-response` | 注入模拟模型响应 |
| POST | `/api/v1/debug/simulate/tool-result` | 注入模拟工具结果 |
| POST | `/api/v1/debug/simulate/approval-decision` | 强制审批决策 |
| POST | `/api/v1/debug/simulate/state-patch` | 修改 Agent 状态 |
| POST | `/api/v1/debug/breakpoints` | 设置断点 |
| DELETE | `/api/v1/debug/breakpoints/{id}` | 删除断点 |
| GET | `/api/v1/debug/breakpoints` | 列出所有断点 |

#### 4.2.3 WebSocket 端点

```
WS /api/v1/debug/ws

实时推送消息类型:
- trace.span.started    — 新 span 开始
- trace.span.ended      — span 结束
- trace.metric.sampled  — 指标采样
- agent.state.changed   — Agent 状态变更
- breakpoint.hit        — 断点命中
- simulation.paused     — 模拟暂停

客户端发送消息类型:
- subscribe.topics      — 订阅特定 topic
- simulation.resume     — 恢复模拟
- simulation.step       — 单步执行
- simulation.reset      — 重置模拟
```

#### 4.2.4 新增文件清单

| 文件路径 | 职责 |
|---------|------|
| `src/TinadecCore/Debug/DebugApiController.cs` | Debug REST API 控制器 |
| `src/TinadecCore/Debug/SimulationApiController.cs` | Simulation REST API 控制器 |
| `src/TinadecCore/Debug/DebugWebSocketHandler.cs` | WebSocket 实时推送处理器 |
| `src/TinadecCore/Debug/SimulationService.cs` | 对话模拟核心逻辑 |
| `src/TinadecCore/Debug/BreakpointService.cs` | 断点管理 |
| `src/TinadecCore/Debug/ProcessDiagnosticsService.cs` | 进程资源诊断 |

---

### 4.3 模块 C：Gateway 代理路由

**职责**: 在 Elysia BFF 层代理 `/debug/*` 请求到 TinadecCore。

#### 4.3.1 修改文件

| 文件路径 | 变更内容 |
|---------|---------|
| `gateway/src/index.ts` | 添加 `/debug/*` 代理路由，转发到 Core `http://127.0.0.1:48731` |
| `gateway/src/debugProxy.ts` | 新增：Debug API 代理逻辑（REST + WebSocket 升级） |

---

### 4.4 模块 D：Agent Debug Studio 前端

**职责**: Electron 独立窗口，提供可视化追踪、Agent 图、模拟器、指标看板。

#### 4.4.1 Electron 端新增文件

| 文件路径 | 职责 |
|---------|------|
| `apps/desktop/electron/debug-studio.cjs` | Debug Studio BrowserWindow 创建与管理 |
| `apps/desktop/electron/preload.cjs` | 扩展：添加 `tinadec.debug.*` IPC 方法 |

#### 4.4.2 Vue 前端新增文件

```
apps/desktop/src/debug/
├── DebugStudio.vue               — Debug Studio 主布局
├── composables/
│   ├── useDebugWebSocket.ts      — WebSocket 连接管理
│   ├── useTraceData.ts           — 追踪数据获取与缓存
│   ├── useSimulation.ts          — 模拟器状态管理
│   └── useMetrics.ts             — 指标数据获取
├── components/
│   ├── TraceTimeline.vue         — 追踪瀑布图（核心组件）
│   ├── TraceTimelineRow.vue      — 单个 span 行
│   ├── InspectorPanel.vue        — Span 详情面板
│   ├── SpanAttributesTable.vue   — 属性 KV 表格
│   ├── SpanEventsList.vue        — 内嵌事件列表
│   ├── ModelRequestViewer.vue    — Model 请求/响应查看器
│   ├── TokenUsageBreakdown.vue   — Token 用量分解
│   ├── AgentGraphCanvas.vue      — Agent 拓扑图（复用现有组件经验）
│   ├── AgentGraphNode.vue        — 拓扑图节点
│   ├── AgentGraphEdge.vue        — 拓扑图边
│   ├── SimulatorBar.vue          — 模拟控制条
│   ├── BreakpointEditor.vue      — 断点编辑器
│   ├── MessageInjector.vue       — 消息注入面板
│   ├── StateEditor.vue           — Agent 状态编辑器
│   ├── ReplayControls.vue        — 回放控制（步进/暂停/重置）
│   ├── MetricsDashboard.vue      — 指标看板
│   ├── MetricChart.vue           — 单个指标图表
│   ├── DiagnosticsReport.vue     — 诊断报告
│   ├── SessionSelector.vue       — Session 选择器
│   ├── RunSelector.vue           — Run 选择器
│   └── LiveReplayToggle.vue      — Live/Replay 模式切换
├── types/
│   ├── trace.ts                  — 追踪数据类型
│   ├── simulation.ts             — 模拟相关类型
│   ├── metrics.ts                — 指标类型
│   └── debug-api.ts              — Debug API 请求/响应类型
└── pages/
    └── DebugStudioPage.vue       — Debug Studio 页面入口
```

#### 4.4.3 Trace Timeline 瀑布图设计

参考 Chrome DevTools 的 Network 面板 + LangSmith 的 Trace 视图：

```
┌──────────────────────────────────────────────────────────────┐
│ Filter: [agent_type ▼] [status ▼] [min duration ___ms]  🔍 │
├──────────┬───────────────────────────────────────────────────┤
│ Span     │ 0ms        1s        2s        3s        4s      │
├──────────┼───────────────────────────────────────────────────┤
│ ▾ turn   │ ████████████████████████████████████████████████ │
│ ├ infere │   ████████████████                               │
│ ├ tool_d │                     ████                         │
│ │ ├ tool │                       ████████████               │
│ │ └ appr │                                   ███            │
│ ├ superv │                                          ██      │
│ └ ctx_pk │                                            ███    │
├──────────┼───────────────────────────────────────────────────┤
│ ▾ turn   │ ████████████████████                             │
│ ├ infere │   ██████████████                                 │
│ └ tool_e │                  ████████                        │
├──────────┼───────────────────────────────────────────────────┤
```

交互：
- 点击 span → Inspector Panel 展示详情
- 双击 span → 聚焦该 span 的时间范围
- 右键 → 设置断点 / 复制 traceId / 从此处模拟
- 拖拽时间轴 → 平移视图
- 滚轮 → 缩放时间轴

颜色编码：
- 蓝色：agent.turn / agent.inference
- 绿色：agent.tool_dispatch / agent.tool_execution
- 黄色：agent.approval（等待中）
- 橙色：agent.supervision（有 finding）
- 紫色：agent.context_pack / agent.workflow_compile
- 灰色：sqlite.query
- 红色：任何 status=ERROR 的 span

#### 4.4.4 Agent Graph Canvas 设计

参考 LangGraph Studio 的图可视化 + 现有 `AgentTopologyCanvas.vue`：

```
    ┌──────────────────┐
    │  code-explorer   │────┐
    │  ● completed     │    │
    └──────────────────┘    │
                            ▼
    ┌──────────────────┐  ┌──────────────────┐
    │  code-writer     │──│  review-executor │
    │  ◐ running       │  │  ○ pending       │
    └──────────────────┘  └──────────────────┘
            │
            ▼
    ┌──────────────────┐
    │  test-multimodal │
    │  ⏸ blocked       │
    └──────────────────┘
```

节点状态图标：
- `●` completed（绿色）
- `◐` running（蓝色动画脉冲）
- `○` pending（灰色）
- `✕` failed（红色）
- `⏸` blocked / approval_required（黄色）

点击节点 → 跳转到对应的 span / 显示 Agent Profile 详情

#### 4.4.5 Simulator Bar 设计

```
┌─────────────────────────────────────────────────────────────────┐
│  [▶ Step]  [▶▶ Run]  [⏸ Pause]  [⏮ Reset]  │ Step 5/23  │
├─────────────────────────────────────────────────────────────────┤
│  [💬 Inject Message...]  [🔧 Inject Tool Result...]            │
│  [✏️ Modify Agent State...]  [🔴 Set Breakpoint...]            │
│  [⚡ Force Approval]  [🔄 Replay from Step ___]                │
└─────────────────────────────────────────────────────────────────┘
```

#### 4.4.6 Metrics Dashboard 设计

参考 T3 Code 的指标看板 + Recharts 图表：

```
┌──────────────────────┬──────────────────────┐
│  Agent Turn Duration │  Tool Execution Time │
│  ▁▂▃▅▇▆▅▃▂▁         │  ▁▁▂▃▃▃▂▁▁           │
│  p50: 3.2s           │  p50: 450ms          │
│  p95: 8.7s           │  p95: 2.1s           │
│  p99: 12.1s          │  p99: 5.3s           │
├──────────────────────┼──────────────────────┤
│  Model API Latency   │  Token Usage         │
│  ▃▄▅▇▇▆▅▄▃          │  ██ input: 125K      │
│  p50: 1.8s           │  ██ output: 32K      │
│  p95: 4.2s           │  ██ total: 157K      │
├──────────────────────┼──────────────────────┤
│  Approval Wait Time  │  Error Rate          │
│  ▁▂▃▃▂▁             │  ▁▁▁▁▂▁▁▁            │
│  p50: 2.1s           │  rate: 3.2%          │
└──────────────────────┴──────────────────────┘
```

---

## 5. API 契约

### 5.1 GET /api/v1/debug/traces

**请求参数**:

```typescript
interface GetTracesRequest {
  session_id?: string;
  run_id?: string;
  name?: string;          // span name 过滤
  status?: string;        // OK / ERROR
  min_duration_ms?: number;
  limit?: number;         // 默认 50，最大 200
  offset?: number;
}
```

**响应**:

```typescript
interface GetTracesResponse {
  traces: TraceSummary[];
  total_count: number;
}

interface TraceSummary {
  trace_id: string;
  root_span_name: string;
  root_span_duration_ms: number;
  span_count: number;
  error_count: number;
  started_at: string;     // ISO 8601
  session_id: string | null;
  run_id: string | null;
}
```

### 5.2 GET /api/v1/debug/traces/{traceId}

**响应**:

```typescript
interface TraceDetail {
  trace_id: string;
  root_span: SpanNode;
  resource: Record<string, string>;
}

interface SpanNode {
  span_id: string;
  parent_span_id: string | null;
  name: string;
  kind: string;
  start_time: string;
  end_time: string;
  duration_ms: number;
  status: string;
  status_message: string | null;
  attributes: Record<string, unknown>;
  events: SpanEvent[];
  children: SpanNode[];
  links: SpanLink[];
}

interface SpanEvent {
  name: string;
  timestamp: string;
  attributes: Record<string, unknown>;
}

interface SpanLink {
  trace_id: string;
  span_id: string;
  attributes: Record<string, unknown>;
}
```

### 5.3 GET /api/v1/debug/metrics

**请求参数**:

```typescript
interface GetMetricsRequest {
  metric_name: string;
  window_ms?: number;     // 默认 3600000 (1h)
  bucket_ms?: number;     // 默认 60000 (1min)
  group_by?: string[];    // 例如 ["agent_type", "status"]
}
```

**响应**:

```typescript
interface GetMetricsResponse {
  metric_name: string;
  window_ms: number;
  bucket_ms: number;
  buckets: MetricBucket[];
  summary: MetricSummary;
}

interface MetricBucket {
  started_at: string;
  ended_at: string;
  count: number;
  sum: number;
  min: number;
  max: number;
  p50: number;
  p95: number;
  p99: number;
  attributes: Record<string, string>;
}

interface MetricSummary {
  total_count: number;
  total_sum: number;
  avg: number;
  min: number;
  max: number;
  p50: number;
  p95: number;
  p99: number;
}
```

### 5.4 GET /api/v1/debug/diagnostics

**响应**:

```typescript
interface TraceDiagnosticsReport {
  generated_at: string;
  trace_file_path: string;
  record_count: number;
  parse_error_count: number;
  failure_count: number;
  interruption_count: number;
  slow_span_threshold_ms: number;
  slow_span_count: number;
  top_spans_by_count: SpanSummary[];
  slowest_spans: SlowSpanEntry[];
  common_failures: FailureCluster[];
  latest_failures: RecentFailure[];
  latest_warnings_and_errors: LogEvent[];
}

interface SpanSummary {
  name: string;
  count: number;
  failure_count: number;
  total_duration_ms: number;
  average_duration_ms: number;
  max_duration_ms: number;
}

interface SlowSpanEntry {
  name: string;
  duration_ms: number;
  ended_at: string;
  trace_id: string;
  span_id: string;
}

interface FailureCluster {
  name: string;
  cause: string;
  count: number;
  last_seen_at: string;
  trace_id: string;
  span_id: string;
}

interface RecentFailure {
  name: string;
  duration_ms: number;
  ended_at: string;
  cause: string;
  trace_id: string;
  span_id: string;
}

interface LogEvent {
  span_name: string;
  level: string;
  message: string;
  seen_at: string;
  trace_id: string;
  span_id: string;
}
```

### 5.5 POST /api/v1/debug/simulate/message

**请求**:

```typescript
interface SimulateMessageRequest {
  session_id: string;
  content: string;
  skip_model_call?: boolean;       // 是否跳过真实 Model 调用
  mock_model_response?: string;    // 如果跳过，使用此模拟响应
}
```

**响应**:

```typescript
interface SimulateMessageResponse {
  simulation_id: string;
  trace_id: string;
  simulated: boolean;
}
```

### 5.6 POST /api/v1/debug/breakpoints

**请求**:

```typescript
interface CreateBreakpointRequest {
  condition_type: 'on_tool_call' | 'on_approval' | 'on_agent_error' | 'on_token_budget' | 'on_state_change' | 'on_sub_agent_spawn';
  condition: {
    tool_id?: string;
    agent_type?: string;
    token_budget?: number;
    state_key?: string;
    state_value?: string;
  };
  action: 'pause' | 'log' | 'auto_approve' | 'inject_response';
  action_params?: Record<string, unknown>;
}
```

**响应**:

```typescript
interface Breakpoint {
  id: string;
  condition_type: string;
  condition: Record<string, unknown>;
  action: string;
  action_params: Record<string, unknown>;
  hit_count: number;
  enabled: boolean;
  created_at: string;
}
```

---

## 6. 数据模型与追踪 Schema

### 6.1 追踪数据存储

| 存储 | 格式 | 位置 | 生命周期 |
|------|------|------|---------|
| NDJSON trace 文件 | `core.trace.ndjson` + 轮转 | `output/logs/` | 持久化，轮转保留 10 个文件 |
| SQLite events 表 | 已有 | `output/tinadec.db` | 已有，不需变更 |
| 内存活跃 span | `ConcurrentDictionary` | Core 进程内存 | 进程生命周期 |

### 6.2 跨语言 Trace 关联

C# Core 和 Rust Codex 之间的 trace 通过 W3C Trace Context (`traceparent` / `tracestate`) 关联：

```
Core (C#)                     Codex (Rust)
──────────                    ──────────
agent.turn (span)             
  └─ agent.tool_execution     
       └─ codex.tool_call ──→ TINADEC_TRACEPARENT env var
                              └─ codex.tool.run (span, same trace_id)
```

实现方式：
- Core 在调用 `CodeToolClient` 时，将当前 `traceparent` 注入 HTTP Header `traceparent`
- Codex Rust glue 读取该 Header，通过 `otel::trace_context::set_parent_from_w3c_trace_context` 关联

---

## 7. 前端组件树

```
DebugStudioPage.vue
├── DebugStudio.vue (主布局)
│   ├── SessionSelector.vue
│   ├── RunSelector.vue
│   ├── LiveReplayToggle.vue
│   │
│   ├── 左侧: TraceTimeline.vue
│   │   ├── TraceTimelineRow.vue (× N)
│   │   │   └── SpanEventsList.vue (展开时)
│   │   └── 时间轴标尺
│   │
│   ├── 右侧: InspectorPanel.vue
│   │   ├── SpanAttributesTable.vue
│   │   ├── SpanEventsList.vue
│   │   ├── ModelRequestViewer.vue
│   │   │   └── TokenUsageBreakdown.vue
│   │   └── (状态编辑器，Replay 模式下可用)
│   │       └── StateEditor.vue
│   │
│   ├── 中下: AgentGraphCanvas.vue
│   │   ├── AgentGraphNode.vue (× N)
│   │   └── AgentGraphEdge.vue (× N)
│   │
│   ├── 底部: SimulatorBar.vue
│   │   ├── ReplayControls.vue
│   │   ├── MessageInjector.vue
│   │   ├── BreakpointEditor.vue
│   │   └── StateEditor.vue
│   │
│   └── 可切换: MetricsDashboard.vue
│       ├── MetricChart.vue (× N)
│       └── DiagnosticsReport.vue
│
└── (composables)
    ├── useDebugWebSocket.ts
    ├── useTraceData.ts
    ├── useSimulation.ts
    └── useMetrics.ts
```

---

## 8. 实施路线图与任务拆解

### Phase 1: 基础追踪层（预计 1-2 周）

**目标**: C# Core 端具备结构化追踪和指标采集能力。

| # | 任务 | 涉及文件 | 优先级 | 依赖 |
|---|------|---------|--------|------|
| 1.1 | 添加 OpenTelemetry NuGet 依赖 | `TinadecCore.csproj` | P0 | 无 |
| 1.2 | 创建 SpanDefinitions.cs（span 名称 + 属性键常量） | 新文件 | P0 | 无 |
| 1.3 | 创建 MetricDefinitions.cs（指标名称 + 类型定义） | 新文件 | P0 | 无 |
| 1.4 | 实现 AgentTracing.cs（TracerProvider + MeterProvider 初始化） | 新文件 | P0 | 1.1, 1.2, 1.3 |
| 1.5 | 实现 NdjsonTraceExporter.cs（本地文件持久化） | 新文件 | P0 | 1.4 |
| 1.6 | 在 Program.cs 注册 OpenTelemetry 服务 | `Program.cs` | P0 | 1.4, 1.5 |
| 1.7 | 为 OrchestratorService 添加 agent.turn span | `OrchestratorService.cs` | P0 | 1.6 |
| 1.8 | 为 OpenAiCompatibleClient 添加 agent.inference span | `OpenAiCompatibleClient.cs` | P0 | 1.6 |
| 1.9 | 为 ToolExecutionService 添加 agent.tool_execution span | `ToolExecutionService.cs` | P0 | 1.6 |
| 1.10 | 为 CoreStore SQLite 操作添加 sqlite.query span | `CoreStore.cs` | P1 | 1.6 |
| 1.11 | 为 AgentWorkflowRuntime 添加 agent.workflow_compile span | `AgentWorkflowRuntime.cs` | P1 | 1.6 |
| 1.12 | 添加追踪配置项到 appsettings.json | `appsettings.json`, `appsettings.Development.json` | P0 | 1.4 |
| 1.13 | 实现 ActivitySourceHelper.cs（便捷扩展方法） | 新文件 | P1 | 1.2 |

**验收**: 运行 Core 后，`output/logs/core.trace.ndjson` 包含结构化 span 数据。

### Phase 2: Debug API 端点（预计 1 周）

**目标**: Core 暴露 Debug REST API + WebSocket 实时推送。

| # | 任务 | 涉及文件 | 优先级 | 依赖 |
|---|------|---------|--------|------|
| 2.1 | 实现 DebugApiController.cs（traces/spans/metrics/snapshot） | 新文件 | P0 | Phase 1 |
| 2.2 | 实现 TraceDiagnosticService.cs（失败分析/慢span/聚合） | 新文件 | P0 | Phase 1 |
| 2.3 | 实现 ProcessDiagnosticsService.cs（进程资源查询） | 新文件 | P1 | 无 |
| 2.4 | 实现 DebugWebSocketHandler.cs（实时 span 推送） | 新文件 | P0 | Phase 1 |
| 2.5 | 在 Program.cs 注册 Debug API 路由和 WebSocket | `Program.cs` | P0 | 2.1, 2.4 |
| 2.6 | Gateway 添加 /debug/* 代理路由 | `gateway/src/index.ts`, 新文件 `debugProxy.ts` | P0 | 2.5 |
| 2.7 | 验证 Debug API 可通过 Gateway 访问 | — | P0 | 2.6 |

**验收**: `curl http://127.0.0.1:48730/debug/traces` 返回追踪数据。

### Phase 3: Debug Studio 前端 — 基础（预计 2 周）

**目标**: Electron 独立窗口 + Trace Timeline + Inspector Panel。

| # | 任务 | 涉及文件 | 优先级 | 依赖 |
|---|------|---------|--------|------|
| 3.1 | 创建 debug-studio.cjs（BrowserWindow 管理） | 新文件 | P0 | 无 |
| 3.2 | 扩展 preload.cjs（添加 debug IPC 方法） | `preload.cjs` | P0 | 3.1 |
| 3.3 | 在 main.cjs 添加 IPC：打开 Debug Studio 窗口 | `main.cjs` | P0 | 3.1 |
| 3.4 | 实现 useDebugWebSocket.ts composable | 新文件 | P0 | 2.4 |
| 3.5 | 实现 useTraceData.ts composable | 新文件 | P0 | 2.1 |
| 3.6 | 实现 DebugStudioPage.vue + DebugStudio.vue 布局 | 新文件 | P0 | 3.4, 3.5 |
| 3.7 | 实现 TraceTimeline.vue + TraceTimelineRow.vue | 新文件 | P0 | 3.6 |
| 3.8 | 实现 InspectorPanel.vue + SpanAttributesTable.vue | 新文件 | P0 | 3.6 |
| 3.9 | 实现 SpanEventsList.vue | 新文件 | P1 | 3.8 |
| 3.10 | 实现 SessionSelector.vue + RunSelector.vue | 新文件 | P0 | 3.6 |
| 3.11 | 实现 LiveReplayToggle.vue | 新文件 | P1 | 3.6 |
| 3.12 | 在 AppHeader 或 AppSidebar 添加"打开 Debug Studio"入口 | 现有文件 | P0 | 3.3 |

**验收**: 点击主窗口的 Debug 按钮可打开独立窗口，实时显示 span 瀑布图和详情。

### Phase 4: Debug Studio 前端 — 高级可视化（预计 1-2 周）

**目标**: Agent Graph Canvas + Model Request Viewer + Metrics Dashboard。

| # | 任务 | 涉及文件 | 优先级 | 依赖 |
|---|------|---------|--------|------|
| 4.1 | 实现 AgentGraphCanvas.vue + Node + Edge | 新文件 | P0 | Phase 3 |
| 4.2 | Agent Graph 与 Trace Timeline 联动（点击节点跳转 span） | — | P0 | 4.1, 3.7 |
| 4.3 | 实现 ModelRequestViewer.vue（prompt/response 完整查看） | 新文件 | P0 | 3.8 |
| 4.4 | 实现 TokenUsageBreakdown.vue | 新文件 | P1 | 4.3 |
| 4.5 | 实现 useMetrics.ts composable | 新文件 | P1 | 2.1 |
| 4.6 | 实现 MetricsDashboard.vue + MetricChart.vue | 新文件 | P1 | 4.5 |
| 4.7 | 实现 DiagnosticsReport.vue | 新文件 | P1 | 2.2 |

**验收**: Debug Studio 可视化 Agent 拓扑图，点击节点展示详情，指标图表实时更新。

### Phase 5: 模拟与回放（预计 2 周）

**目标**: 对话模拟器 + 历史回放 + 断点系统。

| # | 任务 | 涉及文件 | 优先级 | 依赖 |
|---|------|---------|--------|------|
| 5.1 | 实现 SimulationService.cs（消息注入/状态修改/审批强制） | 新文件 | P0 | Phase 2 |
| 5.2 | 实现 BreakpointService.cs（断点管理/条件评估/命中通知） | 新文件 | P0 | Phase 2 |
| 5.3 | 实现 SimulationApiController.cs | 新文件 | P0 | 5.1, 5.2 |
| 5.4 | 在 Program.cs 注册 Simulation 服务和 API | `Program.cs` | P0 | 5.3 |
| 5.5 | 实现 useSimulation.ts composable | 新文件 | P0 | 5.3 |
| 5.6 | 实现 SimulatorBar.vue + ReplayControls.vue | 新文件 | P0 | 5.5, Phase 3 |
| 5.7 | 实现 MessageInjector.vue | 新文件 | P0 | 5.6 |
| 5.8 | 实现 BreakpointEditor.vue | 新文件 | P1 | 5.6 |
| 5.9 | 实现 StateEditor.vue（Agent 状态修改） | 新文件 | P1 | 5.6 |
| 5.10 | 历史回放：从 NDJSON 文件加载并逐步重放 | 新文件 | P1 | Phase 3 |
| 5.11 | 断点命中时的模拟暂停和恢复逻辑 | — | P0 | 5.2, 5.5 |
| 5.12 | 模拟 span 标记（`simulated=true` 属性） | `AgentTracing.cs` | P0 | 5.1 |

**验收**: 在 Debug Studio 中可注入消息、设置断点、单步执行、暂停/恢复模拟。

### Phase 6: 跨语言追踪对齐（预计 1 周）

**目标**: C# Core 和 Rust Codex 的 trace 通过 W3C Trace Context 关联。

| # | 任务 | 涉及文件 | 优先级 | 依赖 |
|---|------|---------|--------|------|
| 6.1 | CodeToolClient 调用时注入 traceparent HTTP Header | `CodeToolClient.cs` | P0 | Phase 1 |
| 6.2 | Codex Rust glue 读取 traceparent 并关联（验证已有能力） | `native/glue/` | P1 | 6.1 |
| 6.3 | 统一 NDJSON trace schema（C# 和 Rust 输出格式一致） | — | P1 | Phase 1 |
| 6.4 | Debug Studio 可同时展示 C# 和 Rust 的 span | 前端 | P1 | 6.3 |

**验收**: 一个完整的 Agent turn 可在 Debug Studio 中看到从 Core C# 到 Codex Rust 的全链路 span。

### Phase 7: 测试与文档（持续）

| # | 任务 | 优先级 |
|---|------|--------|
| 7.1 | AgentTracing 单元测试（span 创建、属性注入） | P0 |
| 7.2 | NdjsonTraceExporter 集成测试（文件写入、轮转） | P0 |
| 7.3 | Debug API 集成测试（端点响应验证） | P0 |
| 7.4 | SimulationService 单元测试（消息注入、状态修改） | P1 |
| 7.5 | BreakpointService 单元测试（条件评估、命中通知） | P1 |
| 7.6 | 前端组件测试（TraceTimeline、InspectorPanel） | P1 |
| 7.7 | E2E 测试（完整 Agent turn 的追踪 + 回放） | P1 |
| 7.8 | 更新 architecture.md 文档 | P0 |
| 7.9 | 更新 startup.md 文档（新增 TINADEC_TRACING_* 环境变量） | P0 |

---

## 9. 测试策略

### 9.1 测试层级

| 层级 | 工具 | 覆盖内容 |
|------|------|---------|
| 单元测试 | xUnit | Tracing 服务、Diagnostic 服务、Simulation 服务、Breakpoint 服务 |
| 集成测试 | xUnit + WebApplicationFactory | Debug API 端点、WebSocket 推送 |
| 前端组件测试 | Vitest + @vue/test-utils | TraceTimeline、InspectorPanel、SimulatorBar |
| E2E 测试 | Playwright | 完整 Agent turn 追踪 → Debug Studio 展示 → 模拟注入 |
| 追踪验证 | 自定义 | NDJSON trace 文件格式验证、跨语言 trace 关联验证 |

### 9.2 测试新增文件

| 文件路径 | 职责 |
|---------|------|
| `tests/TinadecCore.Tests/AgentTracingTests.cs` | Tracing 初始化 + span 创建 |
| `tests/TinadecCore.Tests/NdjsonTraceExporterTests.cs` | NDJSON 文件写入 + 轮转 |
| `tests/TinadecCore.Tests/TraceDiagnosticServiceTests.cs` | 诊断分析逻辑 |
| `tests/TinadecCore.Tests/DebugApiTests.cs` | Debug API 端点集成测试 |
| `tests/TinadecCore.Tests/SimulationServiceTests.cs` | 模拟服务逻辑 |
| `tests/TinadecCore.Tests/BreakpointServiceTests.cs` | 断点管理逻辑 |
| `apps/desktop/src/debug/__tests__/TraceTimeline.test.ts` | 瀑布图组件 |
| `apps/desktop/src/debug/__tests__/InspectorPanel.test.ts` | 详情面板组件 |

---

## 10. 风险与缓解

| 风险 | 概率 | 影响 | 缓解措施 |
|------|------|------|---------|
| OpenTelemetry .NET SDK 与 .NET 10 兼容性问题 | 中 | 高 | 预先验证 OTel SDK 在 net10.0 上的运行；保留自定义 ActivitySource 兜底 |
| 追踪采集影响 Core 性能 | 低 | 中 | 使用采样策略（生产环境默认 10%）；BenchmarkDotNet 性能基线测试 |
| NDJSON 文件增长过快 | 低 | 中 | 严格轮转策略（10MB × 10 文件 = 100MB 上限）；异步批量写入 |
| Electron 双窗口通信复杂 | 中 | 中 | 使用 IPC + 共享状态文件，不直接窗口间通信 |
| 跨语言 trace 关联失败 | 中 | 低 | W3C Trace Context 是标准协议；Codex 已有实现；降级为不关联 |
| 前端瀑布图渲染性能（大量 span） | 中 | 中 | 虚拟滚动（只渲染可见行）；span 折叠（合并同类子 span） |
| 模拟器破坏 Core 状态 | 低 | 高 | 模拟操作标记 `simulated=true`；提供一键回滚；模拟运行在独立 trace 标记下 |

---

## 11. 验收标准

### Phase 1 验收

- [ ] Core 启动后，`output/logs/core.trace.ndjson` 自动创建
- [ ] 发送一条消息后，trace 文件包含 `agent.turn` span 及其子 span
- [ ] span 包含正确的 `trace_id`, `span_id`, `parent_span_id` 关联
- [ ] span 属性包含 `session_id`, `run_id`, `agent_type` 等关键信息
- [ ] 配置 `TINADEC_TRACING_ENABLED=false` 后，零追踪开销

### Phase 2 验收

- [ ] `GET /api/v1/debug/traces` 返回追踪列表
- [ ] `GET /api/v1/debug/traces/{traceId}` 返回完整 span 树
- [ ] `GET /api/v1/debug/metrics` 返回指标聚合数据
- [ ] `GET /api/v1/debug/diagnostics` 返回诊断报告
- [ ] WebSocket 连接后实时推送 span 事件

### Phase 3 验收

- [ ] 主窗口有"打开 Debug Studio"入口
- [ ] Debug Studio 作为独立 BrowserWindow 打开
- [ ] 瀑布图实时展示 span 层级和耗时
- [ ] 点击 span 展示详情（属性、事件）
- [ ] Session/Run 选择器可切换上下文

### Phase 4 验收

- [ ] Agent Graph Canvas 展示当前任务图拓扑
- [ ] 节点颜色反映状态（完成/运行/等待/失败）
- [ ] 点击节点跳转到对应 span
- [ ] Model Request Viewer 展示完整 prompt/response
- [ ] Metrics Dashboard 展示至少 4 个指标图表

### Phase 5 验收

- [ ] 可注入模拟用户消息
- [ ] 可注入模拟模型响应
- [ ] 可强制审批决策
- [ ] 断点命中时模拟暂停
- [ ] 可从历史 Run 加载并逐步回放

### Phase 6 验收

- [ ] C# span 和 Rust span 通过 trace_id 关联
- [ ] Debug Studio 同时展示两层 span
- [ ] NDJSON trace 格式在 C# 和 Rust 之间一致

---

## 12. 参考项目索引

| 项目 | 仓库/文档 | 参考内容 |
|------|----------|---------|
| LangGraph Studio | https://github.com/langchain-ai/langgraph-studio | Agent IDE、可视化图、状态编辑 |
| LangSmith | https://docs.smith.langchain.com/ | 操作级可观测性、评估 |
| Langfuse | https://github.com/langfuse/langfuse | 开源 LLMOps、三层追踪、成本追踪 |
| Arize Phoenix | https://github.com/Arize-ai/phoenix | 零配置追踪、Embedding 分析 |
| Codex rollout-trace | `native/codex-src/codex-rs/rollout-trace/` | Trace Bundle + Reducer 模式 |
| Codex otel | `native/codex-src/codex-rs/otel/` | W3C Trace Context 跨进程传播 |
| Codex debug-client | `native/codex-src/codex-rs/debug-client/` | 交互式 CLI 调试客户端 |
| T3 Code observability | `d:/github/t3code/docs/observability.md` | 四层可观测性体系 |
| T3 Code diagnostics | `d:/github/t3code/apps/server/src/diagnostics/` | 进程诊断 + 资源监控 + Trace 诊断 |
| Kilocode | https://github.com/kilocode/kilocode (forked from Roo Code) | Context 可视化、Debug Mode、多模型比较 |
| Cline | https://github.com/cline/cline | 检查点回滚、Token/Cost 追踪 |
| AgentSight | https://arxiv.org/abs/2508.02736 | eBPF 系统级 Agent 可观测性 |
| AgentTrace | https://arxiv.org/abs/2602.10133 | Agent 结构化日志审计框架 |

---

> **文档维护说明**: 本文档是 Agent Debug Studio 的唯一权威计划。所有实现必须以此文档为准。任何偏差需先更新本文档再实施。每个 Phase 完成后，更新对应验收标准的完成状态。
