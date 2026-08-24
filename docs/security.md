# MVP Security Notes

- Electron runs with `contextIsolation: true`, `nodeIntegration: false`, `sandbox: true`, and a minimal preload API.
- Renderer code does not receive model API keys or direct filesystem/shell access.
- API keys are stored by the C# core as protected data on Windows through DPAPI.
- Default tool posture is approval-first. Shell requests create approval records instead of executing immediately.
- Logs and API responses never return the stored API key; they only expose `has_api_key`.
- `TinadecTools` `command_run` is approval-gated and, on Windows, launches commands through the local `TinadecSandbox` account. Each call grants workspace and explicitly requested external paths only for the process lifetime, then revokes the ACL entries.
- The first approved command may request UAC to initialize the local sandbox account. Commands receive a whitelist environment plus explicitly approved variable names; package caches use the sandbox account profile. Network access and LAN binding remain enabled and are subject to the host firewall.
- Workspace-local persistent read/write/environment grants are stored in `.tinadec/sandbox.json`, which is gitignored and inaccessible for writes from the sandbox account. `sandbox_status` reports readiness and `sandbox_reset` removes workspace policy or the machine account after approval.
- `command_run` accepts timeouts from 1 ms through 30 minutes. Timeout or cancellation terminates the command job and its process tree; stdout and stderr are drained with a 65,536-character response cap per stream.

## Not In MVP

- Enterprise policy center.
- Remote browser sessions with login state.
- Plugin marketplace trust and signing.

## Security Audit TODO (2026-08-24)

Findings from the 2026-08-24 full-repo sweep (secrets, injection surfaces, Electron
hardening, Gateway boundary, SSRF, information disclosure). Nothing here is an
actively exploitable path in the current local-first topology; items are recorded
for follow-up, deliberately not fixed yet.

### To Fix

1. **Terminal IPC accepts arbitrary `shell` from the renderer** — `apps/desktop/electron/terminalManager.cjs:478`
   - `terminal:create` forwards renderer-supplied `options.shell` / `args` / `cwd`
     straight into `pty.spawn` (line 216) with no validation against the
     `getAvailableShells()` allowlist. Any compromised/XSS'd renderer can start an
     arbitrary executable through the preload-exposed `tinadec.terminal.create()`.
   - Fix: in the IPC handler, resolve `options.shell` only if it matches a known
     shell path from `getAvailableShells()`; otherwise fall back to the default shell.

2. **Cloud-mode tenant header trust gap** — `TinadecGateway/src/auth.ts:65,85,109`
   - `x-tenant-id` is taken verbatim from client headers after successful API Key
     auth, as a JWT fallback when the token lacks `tenant_id`, and for anonymous
     access when auth is not required. Combined with `buildForwardHeaders`, the
     forwarded tenant identity is fully attacker-controlled input.
   - Local mode unaffected (Core uses its bootstrap identity). Required before any
     cloud deployment: JWT must trust only `decoded.tenant_id`; API Key auth needs a
     server-side key→tenant mapping instead of echoing client headers.

### Accepted Risks (documented, no action)

| Location | Risk | Rationale |
|----------|------|-----------|
| `apps/desktop/electron/petWindow.cjs:78` | `webSecurity: false` on pet windows | Needed for pixel-perfect canvas click-through; window loads only local pet content and blocks `window.open`. |
| `TinadecCore/Runtime/ControlPlaneService.cs:77` | Model discovery fetches user-configured `base_url` server-side (SSRF-style probe) | Product semantic: arbitrary OpenAI-compatible endpoints are supported. Before multi-tenant cloud hosting, add link-local/metadata IP filtering. |
| `TinadecCore/Api/Program.cs:71-75` | Exception `Message` returned to clients in ProblemDetails | May leak internal details (table names, paths); accepted trade-off for a local workbench. |
| `TinadecCore/Api/appsettings.json` | Committed sample PG connection string (`Password=tinadec`) + `EnableDevelopmentIdentity: true` | Sample defaults; production deployments must override both. |
| `ExecuteSqlRawAsync` sites (`StorageLifecycleService.cs:842,875`, `ProjectSessionStore.cs:515-527`, `DbContextMigrationParticipant.cs:35`) | EF1002 injection warnings | Verified false positives: interpolated values come from EF model metadata or hardcoded dictionaries, never request input; ALTER TABLE column names cannot be parameterized. |
| Gateway WS routes (`index.ts` `/ws/terminal`, `/ws/debug`, `/ws/collaboration`) | Dead stubs that subscribe but never connect upstream | No proxy target means no hijack surface; see TinadecGateway AGENTS.md. |
| Git history contains a committed `SCREENSHOT_ENCRYPTION_KEY` (removed from working tree in `03fa5ad`) | Treated as exposed per owner decision; local dev MCP screenshot encryption key only, not rotated. |

### Verified Clean (2026-08-24)

- Secret-pattern scan (AWS/GitHub/Slack tokens, PEM keys, generic key assignments): no hits outside test fixtures and UI placeholders.
- Core has no CORS surface (Gateway-only); Gateway CORS reflects origin only for allowlisted origins (127.0.0.1/localhost any port + explicit extras); unsafe requests require preflight.
- Cloud auth is fail-closed: HS256 enforced, `alg:none` rejected, missing secret rejects all Bearer tokens.
- Electron main/debug/panel windows: `contextIsolation` + `sandbox` + `nodeIntegration:false` + deny-all `setWindowOpenHandler`.
- Gateway proxy paths are always `encodeURIComponent`-appended to fixed base URLs — no user-controlled host.
- Chat markdown is DOMPurify-sanitized; remaining `v-html` uses bundled static SVGs or local persisted settings.
- Petdex downloads: slug sanitization (path traversal), https+domain allowlist with per-hop redirect revalidation, size caps, magic-byte checks.
- TinadecTools process launches use `ProcessStartInfo.ArgumentList` + `UseShellExecute:false` throughout.
- Provider secrets stay inside Core (SecretStore → direct upstream call); DTOs expose only `has_api_key`.
