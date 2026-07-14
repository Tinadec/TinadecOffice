﻿# TinadecOffice Local Startup Guide

This document standardizes the local startup flow for Core, Gateway, and Desktop.

## Process Roles

- **Tinadec Core** runs at `http://127.0.0.1:48731`.
  It is the only state authority for projects, sessions, messages, approvals, model routes, extensions, agents, task graphs, context packs, and supervision findings.
  User chat turns are routed through the built-in Meeting Agent (`agent_meeting`) on the `planner` model route; other planning and execution agents are dispatched by Core rather than addressed directly from the input box.
- **TinadecOffice Gateway** runs at `http://127.0.0.1:48730`.
  It is a thin Elysia proxy/BFF. Desktop and browser-based development clients should call Gateway, not Core directly.
- **Desktop/Vite UI** runs at `http://127.0.0.1:5173` in development.
  The UI reads agent data from Gateway `/api/v1/agents`, which proxies Core.

## One Command Startup

For normal development, start all three processes from the repo root:

```powershell
npm run dev
```

This runs:

```powershell
npm run dev:core
npm run dev:gateway
npm run dev:desktop
```

The root script already clears the local `Version` and `Ice-Version` environment variables for .NET. Those variables can otherwise break MSBuild version parsing on this machine.

## Split Startup

Use split startup when you only need part of the stack or when restarting stale services.

Start Core:

```powershell
Remove-Item Env:Version -ErrorAction SilentlyContinue
Remove-Item Env:Ice-Version -ErrorAction SilentlyContinue
dotnet run --project src/TinadecCore/TinadecCore.csproj --urls http://127.0.0.1:48731
```

Start Gateway:

```powershell
$env:TINADEC_GATEWAY_PORT = '48730'
npm run dev -w @tinadec/gateway
```

Start Desktop/Vite:

```powershell
npm run dev -w @tinadec/desktop
```

If Vite is already running and only Core/Gateway changed, restart Core and Gateway, then refresh the browser or Electron window.

## Background Startup On Windows

For local manual debugging, background logs should go under `output/logs`.

```powershell
$logDir = 'D:\github\TinadecOffice\output\logs'
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
    "Set-Location 'D:\github\TinadecOffice'; Remove-Item Env:Version -ErrorAction SilentlyContinue; Remove-Item Env:Ice-Version -ErrorAction SilentlyContinue; dotnet run --project src/TinadecCore/TinadecCore.csproj --urls http://127.0.0.1:48731"
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
    "Set-Location 'D:\github\TinadecOffice'; `$env:TINADEC_GATEWAY_PORT='48730'; npm run dev -w @tinadec/gateway"
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

Check Core-seeded agents through Gateway:

```powershell
$agents = Invoke-RestMethod http://127.0.0.1:48730/api/v1/agents
$agents | Select-Object id,name,layer,agent_type,enabled
```

Expected built-in planning agents:

- `agent_meeting`
- `agent_tool_assistant`
- `agent_evolver`
- `agent_context_compressor`
- `agent_supervisor`
- `agent_skill_learner`

Expected built-in execution agents:

- `executor_task_planner`
- `executor_test_multimodal`
- `executor_search_specialist`
- `executor_code_explorer`
- `executor_file_finder`
- `executor_git_manager`
- `executor_code_writer`
- `executor_designer`

All built-in agents are seeded by Core as enabled by default. Desktop should render them from Gateway `/api/v1/agents`; the Settings page can open each agent's configuration from the three-dot menu and update its enabled state, orchestration mode, provider, and model route.

## Troubleshooting

If Settings > Agents is empty:

1. Verify Gateway is reachable at `http://127.0.0.1:48730/api/v1/health`.
2. Verify Gateway can proxy agents with `http://127.0.0.1:48730/api/v1/agents`.
3. If `/api/v1/agents` returns `404`, Gateway is stale. Restart `npm run dev -w @tinadec/gateway`.
4. If Gateway health returns `500`, Core is down or still building. Start or restart Core on port `48731`.
5. If old names appear, restart Core so `CoreStore.Initialize()` can run the built-in seed normalization.
6. Refresh the Vite/Electron settings page after Core and Gateway are both healthy.

If `dotnet build` fails with `NETSDK1018` and a `V7.24.42SP3` version string, clear these environment variables before running .NET:

```powershell
Remove-Item Env:Version -ErrorAction SilentlyContinue
Remove-Item Env:Ice-Version -ErrorAction SilentlyContinue
```

If `dotnet build` cannot copy `TinadecCore.dll`, an old `TinadecCore` process is locking the output. Stop that process or build to an isolated output directory.

## Test Commands

Run the normal test suite:

```powershell
npm test
```

Run Core tests only:

```powershell
Remove-Item Env:Version -ErrorAction SilentlyContinue
Remove-Item Env:Ice-Version -ErrorAction SilentlyContinue
dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal
```

Run frontend and Gateway tests/builds:

```powershell
npm run test --workspaces --if-present
npm run build --workspaces --if-present
```

