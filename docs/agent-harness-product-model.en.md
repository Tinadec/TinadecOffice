# TinadecOffice Agent Harness Product Model

This document defines TinadecOffice's product layers, responsibility boundaries, and long-term direction. It is not a design for a single feature. It is the shared product model that Core, the Tool layer, and the Desktop UI should follow as the system evolves.

## Core Thesis

TinadecOffice is not simply a code editor with a chat box. It is a desktop workbench centered on general-purpose agent orchestration:

- **Core** is the universal agent orchestration model and reusable agent harness.
- **Tool layer** is the capability layer that provides discoverable, approval-aware, executable tools.
- **Code** is a built-in tool suite inside the Tool layer, focused on code, projects, and developer environments.
- **Desktop** is the UI presentation layer that turns Core state and Tool-layer capabilities into an understandable, controllable product experience.

These three layers should stay distinct. Core should not become a backend written only for the TinadecOffice Desktop shell. The Tool layer should not own orchestration state. Desktop should not hide session state, approvals, routing, or tool policy in local frontend state.

## Three Product Layers

### Core: Universal Agent Orchestration Model

Core is the agent operating-system kernel of the product. It defines how work is understood, decomposed, assigned, supervised, executed, recorded, and audited.

Core owns:

- Authoritative state for projects, sessions, messages, events, and approvals.
- The dual-layer agent model: an **operation** layer that actively understands, coordinates, maintains context, supervises, and proposes evolution candidates, plus an **execution** layer that works within explicit task, permission, and budget boundaries. `planning` is a legacy input alias only; new Core contracts, configuration, and presentation use `operation`.
- Task graphs, task nodes, execution assignments, context packs, supervision findings, and step results.
- Model providers, model routes, agent profiles, agent modes, tool descriptors, and permission policy.
- Approval gates, risk modeling, traces, debug APIs, and auditable event streams.
- Stable APIs for tool layers, UI shells, and external harness consumers.

Core is meant to be general. It should drive TinadecOffice, but it should also be reusable by other product shells, CLIs, IDE extensions, or automation runtimes. In other words, Core should describe how agent work is organized, not how a particular page is rendered.

### Tool Layer: Capability Provider

The Tool layer sits below Core as the capability layer. It provides concrete features across work domains so the agent harness can operate on the outside world, gather evidence, execute actions, and return structured results.

The Tool layer owns:

- Tool capabilities, metadata, risk levels, input/output contracts, and execution adapters.
- Execution of tool requests authorized by Core, with structured results, evidence, errors, and diagnostics.
- Integration with built-in tools, native tools, MCP tools, ACP tools, browser tools, document tools, code tools, and future tool types.
- Separation between tool implementation and orchestration policy: tools define what can be done and how to execute it; Core decides whether, when, and how it is audited.

### Code: Code Tool Suite Inside The Tool Layer

Code is not a standalone product layer. It is an important built-in tool suite inside the Tool layer. It focuses on developer workflows and gives Core's agent harness code and project capabilities.

The Code tool suite owns:

- **Project templates**: creating, initializing, recognizing, and managing common project structures.
- **Bash-like environment**: command execution, environment variables, working directories, output streams, and error capture for agents and users.
- **Built-in debugging**: run controls, breakpoints, logs, traces, diagnostics, and reproducible experiments.
- **Built-in code editor**: file browsing, editing, diffs, patches, symbol and full-text search, and code review workflows.
- **Git worktree manager**: branches, worktrees, diffs, commits, rebases, conflicts, and isolated execution spaces.
- **Local tool glue**: Codex/Tool-layer-backed capabilities such as search, read, grep, patch, sandbox, and review formatting.

Code does not decide whether an agent may perform a risky action. It can declare capabilities, execute tool requests, and return structured results, but approvals, permissions, state recording, and policy decisions belong to Core.

### Desktop: UI Presentation Layer

Desktop is the product experience layer. It should not become a second source of business state. Its role is to make Core and the Tool layer understandable and operable for the user.

Desktop owns:

- Presentation of chat, task graphs, execution assignments, context packs, supervision findings, approvals, and event streams.
- Configuration UI for agents, model providers, model routes, tool bindings, and permission modes.
- UI surfaces for Tool-layer features, including project templates, terminal environment, debugging, editor, diff, and worktree management provided by the Code tool suite.
- Agent Debug Studio, trace timelines, agent graphs, metrics, and simulation or replay UI.
- Interaction design that helps users understand what the agent harness is doing, why approval is needed, and what will happen next.

Desktop's core value is not state ownership. Its value is reducing the cognitive cost of Core and the Tool layer. It should make complex orchestration visible, risks explainable, and tool operations controllable.

## Product Data Flow

A typical user request should flow in this direction:

1. The user starts a goal, selects a project, configures a mode, or makes an approval decision in Desktop.
2. Desktop calls Gateway, and Gateway proxies the request to Core.
3. Core creates or updates session state, then creates a run, task graph, agent assignments, context packs, and supervision findings.
4. Core uses tool descriptors and permission policy to decide which read-only tools may run automatically and which tools require approval.
5. Core requests the Tool layer through tool adapters; the current code-tool path is served by the Code tool adapter.
6. The Tool layer invokes the matching tool implementation; the Code tool suite may call local capabilities and return structured results.
7. Core records those results as step results, events, traces, and durable state.
8. Desktop refreshes through HTTP, SSE, or WebSocket channels so the user can see messages, tasks, tool results, approvals, and debug information.

The key boundary is state writeback. Tool outputs from the Tool layer and interactions from Desktop must return to Core instead of creating hidden state outside the harness.

## Dual-Layer Agent Orchestration

TinadecOffice's agent model has two layers:

- **Operation layer (`operation`)**: active agents responsible for intent understanding, the meeting entry point, global coordination, context maintenance, capability advice, quality supervision, and evolution proposals.
- **Execution layer (`execution`)**: task planning agents and workers that complete concrete task nodes and deliver evidence under explicit permission boundaries and tool constraints.

`planning` is a compatibility alias for old data and old calls. It is neither a third layer nor a new public layer name. Old values may be normalized to `operation`; new APIs, events, configuration versions, and UI output must use the canonical `operation` value.

The point of this split is to separate understanding, supervision, and authorization from execution, evidence, and mutation. The operation layer creates structured plans and controls risk. The execution layer performs auditable work. Every mutating action should be traceable to a task node, agent assignment, approval record, and tool result.

### Meeting Entry, Generation, And Memory Boundaries

- **The meeting agent is the only user entry point.** Only it may create a formal user-facing answer. Other agents submit progress, evidence, recommendations, supervision findings, or safe errors for the meeting agent to consolidate. They may not bypass Core to invoke tools.
- **Runtime spawning is Core orchestration, not a general tool.** A meeting agent or execution task planner with `agent.spawn` must specify the goal, success criteria, parent instance, context selector, model, tool/resource scope, and budget when creating a child. Ordinary workers may only submit a spawn request. A child may not expand parent permissions or cross a layer boundary, and may not obtain `direct_user_output`, formal-memory write, or candidate-promotion authority. It is released at the end of its run by default, with parent/child lineage and audit preserved.
- **A candidate is not a formal asset.** Evolution behavior may create only memory or agent candidates with provenance, evidence, applicability, expiry conditions, and confidence. Candidates are excluded from long-term retrieval and do not automatically become durable profiles; human evaluation and promotion create an immutable formal version. Rejection, revocation, replacement, and usage feedback are auditable facts too.
- **Session memory differs from long-term memory.** The current session history, summaries, and reviewed long-term memories may enter the context pack. Full bodies stay in immutable ContentStore; relational data and events retain only references, hashes, counts, and summaries. Long-term-memory review and tool approval have separate state machines.

### Full-Duplex Runtime Contract

Full duplex is an application-level semantic, not a requirement to hold one HTTP connection open. A background run must continue independently of an SSE reader; while it is active, the user can ask for status, add constraints, change the goal, create another task, pause, resume, or cancel. The meeting agent classifies each message and binds it to an existing run or a new run.

Shared session state has a monotonically increasing `context_revision`. Every context patch and goal adjustment carries its base revision. A stale result must never overwrite newer goals or constraints: it is rejected, safely merged as a patch, or causes affected nodes to be replanned. Runs need recoverable states including `understanding`, `executing`, `replanning`, `awaiting_approval`, `paused`, `reviewing`, `completed`, `failed`, and `cancelled`.

### Configuration And Mode Contract

The annotated built-in TOML is the read-only baseline; workspace overrides, human promotions, and immutable versions belong in Core's relational control plane. Resolution starts with the TOML baseline and overlays a workspace version, and each run must freeze the resolved configuration version and content hash. A valid hot reload affects new runs only; an invalid edit retains the prior valid snapshot and appears in readiness diagnostics.

The default configuration makes `plan`, `spec`, `ask`, `vibe`, `auto`, and `agent` available in `conversation`, with `auto` as default. `space` exposes only `agent` and binds it to `space.full_duplex`. `im` and `hub` are compatibility aliases for `conversation` and `space`. Default generation budgets are depth 2, eight generated instances per run, four parallel workers, and two active runs per session; configuration may make these stricter, but a child may not exceed its parent or run limits.

## Boundary Rules

- Core owns state, orchestration, approvals, model routing, tool policy, and event logs.
- The Tool layer owns tool implementations, the capability catalog, execution adapters, and structured tool results.
- Code is the code tool suite inside the Tool layer. It owns developer environment capabilities and project-operation features.
- Desktop owns interaction, visualization, configuration forms, and user experience flows.
- Gateway is a proxy and tool bridge. It should not persist core product state.
- Read-only tools may be auto-dispatched by Core policy. File writes, shell, Git, external network access, MCP, ACP, and other high-risk actions must keep human checkpoints.
- Desktop controls such as mode, permission, and agent configuration should eventually map to auditable Core requests or configuration.
- New Tool-layer features should first surface as tool capabilities or service capabilities, then Core should decide how they enter the agent harness, and finally Desktop should present them in the UI.
- New Code features should register as code-tool capabilities inside the Tool layer, not as a separate product layer that bypasses the Tool layer.

## Implications For Future Feature Design

When adding a capability, answer three questions first:

1. **Is this orchestration semantics?**  
   If it defines tasks, state, permissions, approvals, model routes, agent behavior, or audit events, it belongs in Core.

2. **Is this tool capability?**  
   If it provides editing, debugging, shell, templates, worktrees, search, patching, browser operations, document operations, external-system calls, or project operations, it belongs in the Tool layer. Code and project capabilities usually belong to the Code tool suite.

3. **Is this how users see and control the capability?**  
   If it is primarily layout, forms, graphics, interaction, or information presentation, it belongs in Desktop.

This order prevents the product from becoming a frontend state collage, and it prevents any individual tool suite from turning into a second agent runtime that bypasses Core.

## One-Sentence Product Positioning

TinadecOffice is a desktop agent workbench: Core provides the universal agent harness, the Tool layer provides executable capabilities, Code is the code tool suite inside that layer, and Desktop presents orchestration, tools, and risk control as an operable UI.

## Implementation Status (2026-08-17)

The preceding sections define the product contract; they do not turn planned behavior into delivered functionality. In the current worktree, `TinadecCore/DmaEA/Configuration/default-agent-runtime.toml` and `AgentRuntimeConfigurationStore` provide a validated TOML baseline, `im`/`hub` aliases, configuration-layer normalization from `planning` to `operation`, and an in-process rule that, once the store is resolved, valid snapshots hot reload while invalid edits retain the previous snapshot. Relational projections for `agent_instances`, `agent_candidates`, and `runtime_profile_overrides` also exist.

The existing `DualLayerAgentOrchestrator`, however, still runs on legacy `planning`/`execution` data and does not consume the configuration snapshot or workspace overrides; a run does not yet freeze its resolved configuration hash. The current `invoke-stream` is still a request-bound legacy invocation that emits only terminal SSE chunks and does not persist a meeting-agent answer. A durable background coordinator, run control and recovery, `context_revision` patches, the dynamic spawn service, candidate review APIs, long-term retrieval injection, a Core-owned TinadecTools process, and exact approval resumption remain to be implemented. Gateway and Desktop have not yet moved ordinary chat to this full-duplex contract.
