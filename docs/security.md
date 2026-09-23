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

## Extension-market installs (2026-09-23)

The market surface (Core `Skills/MarketInstallService.cs`) treats every listing as **untrusted data,
never as an instruction or an authorization**. What that means in the code, not in intention:

- No client-supplied destination. An install request carries a `project_id` and a catalog id only.
  For a server entry the file path comes from the Tool Provider's own `mcp_list → config_path`
  answer, and a path outside the project root is refused (`market_install_target_unresolved`) rather
  than written "somewhere plausible". For a skill entry Core composes the path itself —
  `skills/<name>/SKILL.md` under the project root, from the same rule the workspace's skill loader
  reads — and that composed name is the *only* text from a listing allowed to reach a path: a row is
  stored only if its name matches `^[a-z0-9]([a-z0-9]*-[a-z0-9])*[a-z0-9]*$`, so it cannot contain a
  separator, a dot-dot or an encoding trick.
- A listing cannot choose what Core dials. A skill's document address is built from the source's own
  stored https location plus that validated name, on the source's own origin; the `url`/`link`
  members an index publishes are kept as display text and never fetched. Without this, an approved
  source would be an oracle for pointing the egress guard at any host the listing names.
- No arbitrary command text. Command, args and package identifiers from a listing pass a character
  whitelist (`[A-Za-z0-9@:._/+-=]`, braces rejected) and length caps before they can be frozen into
  a proposal; anything else is refused as `market_install_not_expressible` **before** any I/O.
  This is a command-line argument boundary, not a shell boundary — no string here is ever
  interpolated into a shell command line by Core.
- Secrets stay out of the proposal. Environment descriptors carry names and `required`/`secret`
  flags only; values belong to the secret store. A proposal that could carry a value would leak it
  into a durable row, an approval record and the UI.
- Nothing on disk changes without a human. Apply creates one governed `write_file` user action with
  the reviewed bytes frozen into it and an idempotency key bound to the proposal, so a double click
  cannot queue a second write, and the write goes through the existing permission envelope,
  approval record and prewrite snapshot path.
- The proposal cannot change after it is handed out. Each stored proposal is hashed over exactly the
  fields a reviewer is shown (`MarketInstallService.ComputeDigest`), and apply recomputes that hash
  from the stored row before it reads the row's status or replays a previous apply: a mismatch is
  refused as `market_install_proposal_stale` and written down as `stale`, so neither a first attempt
  nor a retry can queue a write whose bytes are not the bytes that were reviewed. The digest is a
  Core-side binding, not something a person is asked to compare: no market card renders it, so the
  human-facing guarantee stays the command, args and target path the proposal names.
- No new egress. Market reads still go through the provider's reserved `#fetch` control tool;
  Core decides only which URL may be fetched and never accepts a client-supplied one.
- An installed skill is text the model will be told about. This is the one respect in which a skill
  install is a larger decision than a server install: a server is inert until something starts it,
  while `skills/<name>/SKILL.md` is discovered by the workspace loader and its name and description
  enter the context of every later run. Three things bound that, and none of them is "trust the
  source": the document is fetched **once**, at preview, and the frozen bytes are what the write puts
  there (apply performs no network read, so a source that changes its mind afterwards changes nothing
  already approved); the document must pass the same `WorkspaceSkillPolicy` rules a hand-written skill
  is refused by — name equal to its directory, description within 1024 characters, and not switched
  off by its own `disabled:` frontmatter — while its size is capped at the same ceiling the loader
  refuses past (`MarketInstallPolicy.MaxSkillBodyBytes` *is* `WorkspaceSkillPolicy.MaxFileBytes`, so
  a document too large to advertise is never fetched into a proposal). An install therefore cannot
  succeed into a file the workspace would ignore; and the rendered index states that skills rank
  below the run's frozen permissions and tool grants, and that the body must be opened rather than
  acted on from its description.
- Known limits, stated rather than hidden: for a package the pinned version is the host's *name* for
  a release, not a content digest, and nothing in this phase verifies a signature or a digest
  (still "Not In MVP" below); for a skill the pin is stronger — the reviewed bytes are the written
  bytes — but the source can publish a different document afterwards and this install will not notice.
  The proposal `warnings[]` carries the sentence that matches the kind, because an approval that
  cannot see what it does not guarantee is not informed.
- Failure text policy: market routes answer with a machine code plus a message built by Core, and
  Gateway's `ALLOWED_CODES` whitelist keeps those codes intact. The rule for what may cross is
  "the reason the operation stopped", not "whatever the upstream process emitted": provider/HTTP
  failure text reaches the UI only through `last_error`/`reason` fields Core itself composed, and
  Core never echoes back a fetched body.

## Not In MVP

- Enterprise policy center.
- Remote browser sessions with login state.
- Plugin marketplace trust and signing — the market surface pins a version and freezes the exact
  bytes it will write, but verifies no signature and no package digest (see the section above).

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
