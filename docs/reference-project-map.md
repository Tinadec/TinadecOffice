# Reference Project Map

TinadecCode should continue absorbing ideas from the sibling repositories under
`D:\github\agent`, while preserving the two-layer agent architecture:

- Core owns state, orchestration, approvals, model routes, policy, audit, and events.
- Tool layer declares and executes capabilities, but does not own orchestration state.
- Desktop presents, controls, and explains Core state without creating a second source of truth.

## Reference Set

| Project | What to study | What to absorb | What to avoid |
| --- | --- | --- | --- |
| `vscode` | Workbench, built-in extensions, CLI, contribution boundaries. | Dense workbench UI, command surfaces, extension-style separation between platform and language/tool features. | Turning TinadecCode into a monolithic editor shell before Core/tool contracts are stable. |
| `codex rust` | `codex-rs`, app-server protocol, sandbox/approval tests, tool search, rollout tracing, agent graph store. | Structured tool descriptors, approval callbacks, app-server style contracts, auditable tool/terminal reduction. | Letting a CLI runtime become TinadecCode's state authority. |
| `t3code` | `apps/server`, `apps/web`, provider runtime ingestion, typed WebSocket pushes, readiness barriers, runtime receipts, worktree bootstrap tests. | Ordered push bus, startup readiness, receipt-based async completion, Git/worktree UX. | Moving orchestration ownership into Desktop/Gateway. |
| `opencode` | Session timeline, provider APIs, permission UI, app/CLI split, E2E timeline tests. | Timeline-focused tool/result UI, provider normalization, permission affordances. | Flattening TinadecCode's planning/execution split into one agent loop. |
| `OpenHarness` | Tool-use, skills, memory, multi-agent coordination, long-session assistant patterns. | Harness vocabulary, skill loading, memory and tool catalog ergonomics. | A generic harness that weakens TinadecCode's Core-owned governance. |
| `Open-ClaudeCode` | Tools, commands, services, plugins, Ink/React UI organization. | Command/plugin organization and output-style conventions. | Reconstructing opaque bundled behavior without explicit Tinadec contracts. |
| `openclaw` | Local personal assistant, onboarding, channels, live canvas, companion app patterns. | Onboarding flow, local-first assistant feel, multi-channel future direction. | Letting external channel plumbing enter Core before session/tool state is mature. |
| `pi` | Agent core, coding-agent CLI, multi-provider API, TUI, containerization notes. | Package boundaries, provider abstraction, TUI/session sharing ideas, sandbox documentation. | Running with process permissions as the default TinadecCode policy. |
| `DeepSeek-TUI` | Rust crates, terminal/web surfaces, deployment/integration folders. | TUI ergonomics and Rust crate organization for future native/runtime work. | Forking a TUI runtime as the main product architecture. |
| `the-zeroth-docs` | Bilingual documentation, release notes, screenshots, multi-agent docs structure. | English/Chinese route parity, user-facing architecture docs, release-note discipline. | Docs that drift away from executable Core contracts. |
| `Tinadice` | Currently only Git metadata in this checkout. | No active reference until source files exist. | Assuming missing code has usable patterns. |

## Current TinadecCode Mappings

- Codex/OpenCode inspired tool descriptors are now Core-visible through the harness manifest and tool search APIs.
- T3 Code/OpenCode timeline and readiness ideas are reflected in the Core-owned tool execution timeline, `/api/v1/readiness` receipts, and Desktop right rail.
- Codex/T3 Code Git/worktree ideas are reflected in `executor_git_manager`, `git_worktree_manager`, and the Desktop Git push readiness panel.
- OpenHarness-style multi-agent vocabulary is kept inside TinadecCode's two-layer split: planning agents actively coordinate; execution agents remain task-bound.

## Implementation Priorities

1. Expand Git/worktree tooling with approved mutation paths after the preview/handoff path is stable.
2. Make right-rail timelines as inspectable as OpenCode/T3 Code timelines while preserving Core event ownership.
3. Extend provider/runtime readiness receipts with deeper live probes and async-completion evidence, following T3 Code's startup lessons, while keeping the status owned by Core.
4. Grow tool catalog metadata toward VS Code-style contribution boundaries: project, runtime, environment, editor, git, review, and future extension domains.
5. Keep bilingual docs and UI text aligned with The Zeroth Docs style: same concepts, same route surface, no Chinese/English drift.
