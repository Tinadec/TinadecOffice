# TUI CLIENT KNOWLEDGE

## OVERVIEW
Terminal.Gui TUI client for TinadecOffice — a pure-terminal presentation surface alongside
the Electron Desktop and the browser Web client. First milestone is the welcome page.

`apps/tui/` is an **independent .NET 10 solution** (`TinadecOffice.Tui.slnx`, single project).
It is deliberately **not** registered in the root `TinadecOffice.slnx` (same separation as
`TinadecCore/TinadecCore.slnx`).

## STRUCTURE
```
apps/tui/
├── TinadecOffice.Tui.slnx     # standalone solution (single project)
├── TinadecOffice.Tui.csproj   # net10.0 Exe; PackageReference Terminal.Gui 2.4.17
├── Program.cs                 # bootstrap: Application.Create().Init() → Run<WelcomePage>; Ctrl+Q
├── AppTheme.cs                # dark Scheme palette (mirrors desktop styles.css tokens)
├── WelcomePage.cs             # Runnable subclass: brand + input + action buttons + health status
├── GatewayClient.cs           # thin HttpClient → GET /api/v1/health
└── AGENTS.md
```

## LAYER BOUNDARY
- TUI is a **presentation layer** like Desktop/Web: it calls **Gateway only**, never Core directly.
- TUI stores **no business state**; Core is the only state authority.
- `GatewayClient` talks to the same `/api/v1/*` contracts as `apps/desktop/src/api.ts`.
- First-version action buttons are placeholders: Core invocation write paths are still 501 stubs,
  so building a session is pointless until those exist.

## RUN
```bash
# Build (through the shared dotnet env wrapper that clears Version / Ice-Version)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 build apps/tui/TinadecOffice.Tui.slnx

# Run in a real terminal (Windows Terminal recommended; TrueColor)
npm run dev:tui
```

Environment:
- `TINADEC_GATEWAY_URL` overrides the Gateway base URL (default `http://127.0.0.1:48730`).

## TERMINAL.GUI v2 NOTES (2.4.17)
- **v2 is instance-based**, not the v1 static API. `Application.Create().Init()` returns an
  `IApplication`; `using` disposes it (v1's `Shutdown`). `Application.SetDefaultKeyBinding` must
  run **before** `Init()`.
- All view constructors are parameterless; layout via `X`/`Y`/`Width`/`Height` with
  `Pos.Center()` / `Dim.Fill()` (methods, not v1 constants).
- Theming: v1 `ColorScheme`/`Colors` are gone. Use `Scheme` (immutable record) + `view.SetScheme(...)`.
- There is no `View.Loaded` event in 2.4.17. Set initial focus by overriding
  `Runnable.OnIsRunningChanged(bool)` when `newIsRunning == true`, then `SetFocus()`.
- Async work must marshal back to the UI thread via `App?.Invoke(...)`.
- Windows: force `DriverRegistry.Names.ANSI` for TrueColor in Windows Terminal.

## CONVENTIONS
- `AssemblyName`/`RootNamespace` = `TinadecOffice.Tui`.
- The csproj hard-pins `<AssemblyVersion>`/`<FileVersion>` to guard against the local
  `Version=V7.24.42SP3` env var breaking MSBuild version parsing (same as `TinadecCore/`).
