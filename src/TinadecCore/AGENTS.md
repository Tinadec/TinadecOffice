# TINADEC CORE KNOWLEDGE

## OVERVIEW

.NET 10 Core runtime and sole state authority. Owns agents, sessions, approvals, model routes, storage, contracts,
tracing, and debug APIs.

## STRUCTURE

```
src/TinadecCore/
├── Program.cs          # DI, HTTP routes, tracing init
├── Abstractions/       # service boundary interfaces
├── Contracts/          # DTO/request/event/security contracts
├── Services/           # orchestration, tools, policy, events, model client
├── Storage/            # SQLite persistence and stored model settings
├── Tracing/            # OpenTelemetry, NDJSON, diagnostics, metrics
└── Debug/              # Debug Studio API, simulation, breakpoints, websocket
```

## WHERE TO LOOK

| Task                        | Location                                                                                                                                                   | Notes                                                                                                                                                   |
|-----------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------|
| Add/trace endpoint          | `Program.cs`                                                                                                                                               | Minimal API route map is centralized.                                                                                                                   |
| Change persistence          | `Storage/CoreStore.cs`                                                                                                                                     | Largest hotspot; pair with CoreStore tests.                                                                                                             |
| Change DTO/request          | `Contracts/Models`, `Contracts/Events`                                                                                                                     | Mirror Desktop `api.ts` after Core changes.                                                                                                             |
| Prompt context engineering  | `Services/PromptContextService.cs`, `Storage/CoreStore.cs`, `Services/ToolRegistryService.cs`                                                              | SQLite prompt fragments, preview assembly, Prompt Context Engineer fallback plan, and read-only `prompt_context_resolve` tool live in Core.             |
| Tool policy/discovery/audit | `Services/ToolRegistryService.cs`, `ToolSearchService.cs`, `ToolLayerReadinessService.cs`, `ToolExecutionTimelineService.cs`, `CapabilityPolicyService.cs` | Approval-first behavior, Core-owned tool registry, searchable discovery metadata, Tool-layer readiness receipts, and tool execution timeline summaries. |
| Code-suite registration     | `Services/ToolRegistryService.cs`                                                                                                                          | `CodeCapabilityProvider` registers Tool-layer Code capabilities; keep Code modeled as tools, not a peer orchestrator.                                   |
| Orchestration               | `Services/OrchestratorService.cs`, `AgentWorkflowRuntime.cs`                                                                                               | Runs, task graph, read-only tools.                                                                                                                      |
| Model providers             | `Services/ModelProviderCatalog.cs`, `ModelReadinessService.cs`, `ModelCatalogReadinessService.cs`, `OpenAiCompatibleClient.cs`                             | Provider-instance model center, runtime readiness, and static catalog/module readiness receipts.                                                        |
| Debug/tracing               | `Tracing/*`, `Debug/*`                                                                                                                                     | Agent Debug Studio backend.                                                                                                                             |
| Tests                       | `tests/TinadecCore.Tests`, `tests/Tinadec.Contracts.Tests`                                                                                                 | xUnit; contracts split from behavior tests.                                                                                                             |

## CONVENTIONS

- Target framework is `net10.0`; nullable and implicit usings are enabled.
- HTTP JSON uses `JsonNamingPolicy.SnakeCaseLower`; keep event/DTO casing stable.
- Provider catalog templates now expose family, driver, connection kind, credential kind, timeout, and capability
  metadata.
- `ModelCatalogReadinessService` owns `/api/v1/model-catalog-readiness`; Gateway/Desktop may display the receipt but
  must not recompute template coverage, runtime module registration, or live-discovery policy.
- `CoreStore` is SQLite-first and seeds built-in agents/providers/routes/extensions.
- `CoreStore` seeds built-in prompt fragments and stores custom prompt fragments/plans. Built-in prompt fragments are
  read-only; clone them before editing.
- `PromptContextService` owns Meeting Agent system prompt assembly. Keep full prompt text out of events/tool results;
  log only fragment ids, estimated token count, context pack ids, and warning counts.
- Tool execution must preserve approval-gated posture.
- Tool layer capabilities are registered in Core. `CodeCapabilityProvider` is the built-in Code suite for project
  templates, runtime probes, bash-like env, debugging, editor, and Git worktree management.
- `ToolRegistryService` publishes a canonical tool catalog and registry summary: duplicate tool ids are resolved inside
  Core before search, manifest, policy, or execution lookup can consume them.
- `executor_git_manager` is the dedicated Git Manager Subagent. Bind the complete read and mutation surface, including
  conflict preview/resolve, to approval-gated TinadecTools descriptors; keep `git_worktree_manager` only for legacy compatibility.
  and legacy compatibility.
- Tool discovery is Core-owned. `/api/v1/tools/search` must derive provider layer, matched fields, and human-checkpoint
  summaries from Core descriptors and policy semantics.
- Tool-layer readiness is Core-owned. `/api/v1/tool-layer-readiness` must derive dispatchability, future-tool state,
  human-checkpoint policy, and execution-agent scope resolution from Core descriptors and seeded agents.
- Tool execution visibility is Core-owned. `/api/v1/sessions/{sessionId}/tool-executions` must derive timeline state,
  provider layer, duration, checkpoint summary, and step-result evidence from Core events and descriptors.
- Keep `project_templates` read-only and `project_template_scaffold` approval-gated as `workspace-write`; scaffolding
  must flow through Core approval before Gateway writes files.
- `SecretProtector` uses DPAPI on Windows; non-Windows fallback is for development only.
- Trace propagation crosses to Gateway/code tools; preserve `traceparent` behavior in client changes.

## ANTI-PATTERNS

- Do not move Core state into Gateway or Desktop.
- Do not mix unrelated API wiring and SQL/schema changes in one broad edit.
- Do not return stored API keys; expose `has_api_key` only.
- Do not run direct dotnet commands on affected Windows env without clearing `Version` and `Ice-Version`.

## COMMANDS

```powershell
Remove-Item Env:Version -ErrorAction SilentlyContinue
Remove-Item Env:Ice-Version -ErrorAction SilentlyContinue
dotnet run --project src/TinadecCore/TinadecCore.csproj --urls http://127.0.0.1:48731
dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal
dotnet test tests/Tinadec.Contracts.Tests/Tinadec.Contracts.Tests.csproj -v minimal
```
