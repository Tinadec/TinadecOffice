# Ponytail Debt Ledger

Harvested 2026-08-23 from live `ponytail:` comments in source. One-shot report: each entry is a
deliberate shortcut with a known ceiling; the upgrade path comes from the comment itself.
Re-harvest with `/ponytail-debt` after bulk refactors.

## Gateway

| Location | Shortcut & ceiling | Upgrade path |
|----------|--------------------|--------------|
| `TinadecGateway/src/openapi.snapshot.test.ts:8` | CI drift gate is a zero-dep raw file snapshot compare | Fine until snapshots need selective/field-level diffing |
| `TinadecGateway/src/mappers/agentsMapper.ts:5` | Passthrough mapper, snake_case only | Core validates ownership/lifecycle; add field mapping when external contract diverges from Core DTOs |
| `TinadecGateway/src/index.ts:132` | Minimal RFC9457 catch-all error handler | Reuse `toProblemDetails`/`CODE_MAP`; keep `X-Request-Id` principal header |

## Core (.NET)

| Location | Shortcut & ceiling | Upgrade path |
|----------|--------------------|--------------|
| `TinadecCore/Lifecycle/StorageLifecycleService.cs:315` | Admission-grace filter runs in memory because EF Core SQLite cannot translate DateTimeOffset comparisons | Stored unix-ms column keeps the filter in SQL when the runs table grows |
| `TinadecCore/Lifecycle/StorageLifecycleService.cs:788` | SQLite-only additive column alignment for runtime-owned runs table | Fold into real migrations if PostgreSQL parity is required for these columns |
| `TinadecCore/Memory/ProjectSessionStore.cs:505` | Dual-provider idempotent column migration: SQLite pragma + PG `ADD COLUMN IF NOT EXISTS` | Replace with generated migrations when the session schema stabilizes |
| `TinadecCore/AspNetCore/Endpoints/InteractionsEndpoints.cs:187-226` | Queued interactions are now persisted (`RunDirectiveRecord` + `run.queued` event) — this entry is resolved and kept only as history | — |
| `TinadecCore/AspNetCore/Endpoints/AgentConfigurationEndpoints.cs` | Strong publish validation requires ≥1 `template` AND ≥1 `assemble`; whitelist + cycle check run separately | Single-pass validator if publish checks multiply |

## TinadecTools

| Location | Shortcut & ceiling | Upgrade path |
|----------|--------------------|--------------|
| `TinadecTools/Tools/ToolConfirmations.cs:3` | Pass values not args object; compile-time binding, AOT safe; helper only does whitespace check | — |
| `TinadecTools/Tools/Git/LogRefTypeMap.cs:3` | One-shot `git for-each-ref` read-only dict of short name → ref type | Rebuild map per invocation is fine at current repo sizes |
| `TinadecTools/Tools/Git/GitLogParser.cs:77` | Unknown decorations conservatively classified as branch | Detached short-hash decorations land here; misclassification is cosmetic but never remote — tighten with rev-parse verification if it matters |
| `TinadecTools/Tools/Git/GitLogDetailTool.cs:208` | `after_commit` continue mode cannot express a diff range, so files are left empty | Thread the original range through continue state to fill files |
| `TinadecTools/Tools/FileRW/FileAccessor.cs:426` | Once mutation starts, cancellation is ignored mid-write | Mid-write cancel can corrupt the file; only revisit with atomic temp-file + rename writes |

## Desktop / Web UI

| Location | Shortcut & ceiling | Upgrade path |
|----------|--------------------|--------------|
| `apps/desktop/src/main.ts:4` | Tolerate Vue 3.5 (no vaporInteropPlugin) vs 3.6-rc | Remove guard once root vue pins ≥3.6 stable |
| `apps/desktop/vite.config.ts:21` | Dedupe vue reactivity: nested desktop 3.5.41 vs root 3.6.0-rc.7 caused split-identity bugs | Drop dedupe after nested copies are gone |
| `apps/desktop/src/generated/client.ts:10` | Hand-written minimal fetch wrapper | Swap to openapi-fetch now that external OpenAPI is live |
| `apps/desktop/src/composables/useRunStream.ts:8` | fetch+ReadableStream instead of EventSource | Needed to control `Last-Event-ID`/`?cursor=` and `id=seq` dedup; keep until native EventSource covers both |
| `apps/desktop/src/transport/sseTransport.ts:9` | Transport seam placeholder; real SSE lives in `useRunStream.ts` | Wire WS upgrade through this seam in stage 2 |
| `apps/desktop/src/stores/workbench.ts:41` | Approval transport via httpTransport | Seam reserved for stage-2 WS upgrade |
| `apps/desktop/src/pages/WorkbenchPage.vue:127` | `unknown` cast bridges generated snapshot → legacy panel type | Make panel consume `generated/client.ts` DTOs directly (single canonical type) |
| `apps/desktop/src/pages/SettingsPage.vue:478` | Agent tool panel reuses `manifestTools` filters, no new deps | Extract component when the panel grows beyond settings page |
| `apps/web/src/platform/webShim.ts:18` | Terminal intentionally undefined so `isTerminalAvailable()` gates the UI | Defining it turns on every `window.tinadec.terminal.*` call site — only do so with a real web terminal |
| `apps/TinadecUI/src/components/cards/market/MarketCatalogCard.vue:14` | Vapor template does not auto-unwrap Ref from controller destructure; plain-typed computed bridges vue-tsc | Remove bridge computeds when vapor auto-unwraps or controller exposes plain refs |

## Test fixtures

| Location | Shortcut & ceiling | Upgrade path |
|----------|--------------------|--------------|
| `tests/TinadecTools.Tests/TempGitRepo.cs:6` | Shared fixture replaces init/config/RunGit/Cleanup duplicated by 4 old git test files | Migrate remaining suites onto it |
| `tests/TinadecTools.Tests/TempGitRepo.cs:12` | Implicit string conversion lets fixture act as cwd | Compile-time only, no runtime cost |
| `tests/TinadecTools.Tests/GitLogToolsTests.cs:487` | Non-git workspace directory fixture local to this file | Move to TempGitRepo-style shared fixture if reused |
| `tests/TinadecTools.Tests/GitWorktreeToolsTests.cs:100` | Boundary asserted via ResolveManagedPath before explicit "current worktree" checks | Keep ordering if error-type contracts depend on it |
| `tests/TinadecTools.Tests/GitWorktreeToolsTests.cs:150` | Canonical `.tinadec/worktrees` ignore convention assumed from repo-root .gitignore | Fixture tolerates its absence in fresh clones |
| `tests/TinadecTools.Tests/ThreeWayTextMergeTests.cs:88` | Deliberately exceeds MaxLcsCells to exercise LCS DP degradation to whole-block replace | — |
| `tests/TinadecTools.Tests/GitConflictResolveToolTests.cs:48` | Non-overlapping edits auto-merge in git; resolve must honestly report "not a conflict" | — |
