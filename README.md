# TinadecOffice

TinadecOffice is a Windows-first intelligent agent desktop workbench for individual developers.

This MVP implements the foundation from the research plan:

- `src/TinadecCore`: portable C# Core framework, runtime, orchestration layer, and state authority.
- `gateway`: TinadecOffice Elysia BFF/API layer.
- `apps/desktop`: TinadecOffice Desktop with Electron + Vue.
- Provider-instance based model center for API key, local server, and CLI model access.
- SQLite persistence for projects, sessions, messages, events, and approvals.
- Approval-first shell workflow.

## Start

```powershell
npm install
npm run restore:dotnet
npm run dev
```

OpenAPI docs are available from the gateway at `http://127.0.0.1:48730/docs`.

For the standardized Core/Gateway/Desktop startup flow and troubleshooting checklist, see [docs/startup.md](docs/startup.md).
