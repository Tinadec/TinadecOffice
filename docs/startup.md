# TinadecOffice Local Startup Guide

This document standardizes the local startup flow for Core, Gateway, and Desktop.

## Process Roles

- **Tinadec Core** runs at `http://127.0.0.1:48731`.
  It is the only state authority for projects, sessions, messages, approvals, model routes, extensions, agents, task graphs, context packs, and supervision findings.
  User chat turns are admitted through `POST /api/v1/sessions/{id}/interactions` and answered by the built-in Meeting Agent (`meeting`); other operation and execution agents are dispatched by Core rather than addressed directly from the input box. Note: `DevSeed` seeds **only** a `chat` model route plus an OpenAI provider — the agent roster comes from the bundled Office Agent Pack and `Runtime/Configuration/bootstrap-agent-directory.toml`.
- **TinadecOffice Gateway** runs at `http://127.0.0.1:48730`.
  It is a thin Elysia proxy/BFF. Desktop and browser-based development clients should call Gateway, not Core directly.
- **Desktop/Vite UI** runs at `http://127.0.0.1:5173` in development.
  The UI reads agent data from Gateway `/api/v1/agents`, which proxies Core.

## One Command Startup

For normal development, start all three processes from the repo root:

```bash
bun run dev
```

This runs:

```bash
bun run dev:core
bun run dev:gateway
bun run dev:desktop
```

The `predev` chain builds `TinadecTools`, stages the pinned ripgrep, and reclaims leftover 48730 / 48731 / 5173 ports before the stack starts. `scripts/dotnet.mjs` also clears the local `Version` and `Ice-Version` environment variables for .NET; those variables can otherwise break MSBuild version parsing on this machine.

## Split Startup

Use split startup when you only need part of the stack or when restarting stale services.

Start Core:

```bash
bun run dev:core
# = bun scripts/dotnet.mjs run --project TinadecCore/Api/TinadecCore.Api.csproj --urls http://127.0.0.1:48731
```

The wrapper clears `Version` / `Ice-Version` itself, so the PowerShell preamble is no longer needed.

Start Gateway:

```powershell
$env:TINADEC_GATEWAY_PORT = '48730'
bun run dev:gateway   # = bun run --cwd TinadecGateway dev; the Gateway is a workspace member of the root Bun install
```

Start Desktop/Vite:

```bash
bun run dev:desktop   # = bun run --cwd apps/desktop dev
```

If Vite is already running and only Core/Gateway changed, restart Core and Gateway, then refresh the browser or Electron window.

## Background Startup On Windows

For local manual debugging, background logs should go under `output/logs`.

```powershell
$repo = (Get-Location).Path
$logDir = Join-Path $repo 'output/logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

Start-Process -FilePath powershell `
  -WindowStyle Hidden `
  -RedirectStandardOutput (Join-Path $logDir 'core.stdout.log') `
  -RedirectStandardError (Join-Path $logDir 'core.stderr.log') `
  -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-Command',
    "Set-Location '$repo'; bun scripts/dotnet.mjs run --project TinadecCore/Api/TinadecCore.Api.csproj --urls http://127.0.0.1:48731"
  )

Start-Process -FilePath powershell `
  -WindowStyle Hidden `
  -RedirectStandardOutput (Join-Path $logDir 'gateway.stdout.log') `
  -RedirectStandardError (Join-Path $logDir 'gateway.stderr.log') `
  -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-Command',
    "Set-Location '$repo'; `$env:TINADEC_GATEWAY_PORT='48730'; bun run dev:gateway"
  )
```

## Verification

Check ports:

```powershell
Get-NetTCPConnection -LocalPort 48731,48730 -ErrorAction SilentlyContinue |
  Select-Object LocalAddress,LocalPort,State,OwningProcess
```

Check Core health:

```powershell
Invoke-RestMethod http://127.0.0.1:48731/api/v1/health
```

Check Gateway health:

```powershell
Invoke-RestMethod http://127.0.0.1:48730/api/v1/health
```

Check the installed agent roster through Gateway:

```powershell
$agents = Invoke-RestMethod http://127.0.0.1:48730/api/v1/agents
$agents | Select-Object id,name,layer,agent_type,enabled
```

Expected roster (source of truth: `TinadecCore/Runtime/Configuration/bootstrap-agent-directory.toml`, 14 agents):

Operation layer (`layer = operation`, `tools = []`):

- `meeting`
- `context_compressor`
- `skill_recommender`
- `supervisor`
- `evolution`
- `git_steward`

Execution layer (`layer = execution`):

- `task_planner`
- `worker.code`
- `worker.document`
- `worker.data`
- `worker.browser`
- `worker.file`
- `worker.general`
- `worker.git`

After a fresh workspace the roster is empty until the bundled Office Agent Pack (`tinadec.office.agent-pack`, currently 0.2.4) is confirmed/installed; Desktop renders it from Gateway `/api/v1/agents`.

## Troubleshooting

If Settings > Agents is empty:

1. Verify Gateway is reachable at `http://127.0.0.1:48730/api/v1/health`.
2. Verify Gateway can proxy agents with `http://127.0.0.1:48730/api/v1/agents`.
3. If `/api/v1/agents` returns `404`, Gateway is stale. Restart `bun run dev:gateway`.
4. If Gateway health returns `500`, Core is down or still building. Start or restart Core on port `48731`.
5. If old names appear, re-install/upgrade the bundled Agent Pack; there is no `CoreStore` class in the current codebase.
6. Refresh the Vite/Electron settings page after Core and Gateway are both healthy.

If `dotnet build` fails with `NETSDK1018` and a `V7.24.42SP3` version string, a stray `Version` / `Ice-Version` variable is confusing MSBuild. Run .NET through the wrapper, which strips both:

```bash
bun scripts/dotnet.mjs build TinadecCore/TinadecCore.slnx
```

Calling `dotnet` directly means clearing them first:

```powershell
Remove-Item Env:Version -ErrorAction SilentlyContinue
Remove-Item Env:Ice-Version -ErrorAction SilentlyContinue
```

If `dotnet build` cannot copy `TinadecCore.dll`, an old `TinadecCore` process is locking the output. Stop that process or build to an isolated output directory.

## Test Commands

Run the normal test suite:

```bash
bun test
```

Run Core tests only:

```bash
bun scripts/dotnet.mjs test TinadecCore/TinadecCore.slnx -v minimal
```

Run frontend and Gateway tests/builds:

```bash
bun run --workspaces --if-present test
bun run --workspaces --if-present build
```

