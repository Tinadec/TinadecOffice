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
