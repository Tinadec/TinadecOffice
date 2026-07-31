# WEB APP KNOWLEDGE

## OVERVIEW
Browser client that reuses the Electron desktop renderer verbatim. This package contains
NO UI code. Vite aliases `@` to `../desktop/src` and compiles the desktop sources in place.

The desktop renderer's only Electron coupling is the global `window.tinadec` object
(defined by `apps/desktop/electron/preload.cjs`, typed in `apps/desktop/src/env.d.ts:105-197`).
`src/platform/webShim.ts` reimplements that same contract for the browser. That is the
entire porting mechanism.

## STRUCTURE
```
apps/web/
├── index.html                # mirrors desktop index.html (early theme + splash)
├── vite.config.ts            # alias @ -> ../desktop/src; publicDir -> ../desktop/public
└── src/
    ├── main.ts               # imports webShim FIRST, then @/main
    └── platform/webShim.ts   # browser implementation of window.tinadec
```

## HARD RULES
- **Never modify `apps/desktop/`.** The desktop renderer must stay unaware that a web build
  exists. If something cannot work without a desktop change, raise it rather than editing.
- **Never add `if (isWeb)` branches to desktop source.** All platform difference lives in
  `webShim.ts`. This is the only thing preventing the desktop tree from rotting.
- **Import order in `src/main.ts` is load-bearing.** `webShim` must be imported before
  `@/main`, because desktop modules read `window.tinadec.gatewayUrl()` at module top level
  (`apps/desktop/src/api.ts:943`).
- Do not copy or move desktop source files into this package. Alias only.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Platform contract | `src/platform/webShim.ts` | Must satisfy every member of the `window.tinadec` type in `apps/desktop/src/env.d.ts`. |
| Build/dev config | `vite.config.ts` | Port 5174 (5173 is reserved for desktop Vite). |
| Backend wiring | `vite.config.ts` `server.proxy` | `/api`, `/docs`, `/ws` proxy to Gateway 48730 for same-origin dev. |

## CONVENTIONS
- `server.fs.allow` must include the REPO ROOT. Desktop deps (`@fontsource-variable/geist`,
  monaco) are hoisted to root `node_modules`; allowing only this package plus `../desktop`
  makes Vite reject those files and the page hangs mid-load.
- `base` is `/` here, NOT `./`. The `./` in the desktop config exists only because packaged
  Electron loads `dist/index.html` via `loadFile()`.
- `gatewayUrl()` returns `window.location.origin`. Same-origin via the dev proxy or a
  production reverse proxy avoids CORS entirely. Do not point the browser at an arbitrary
  backend origin.
- `getAppConfig()` reports `managed: true`, which makes the Settings Gateway URL field
  read-only. Correct for web: the browser cannot choose its own backend.
- `terminal` is deliberately LEFT UNDEFINED. `useTerminal.ts:130` gates on
  `!!window.tinadec.terminal`, so the UI degrades to a terminal-unavailable state. Defining
  a partial `terminal` object would expose every unguarded `window.tinadec.terminal.*`
  call site instead of hiding the feature.
- `pets.*` MUST expose every method. `SettingsPage.vue` and `DesktopPetPage.vue` call
  `window.tinadec.pets.*` WITHOUT optional chaining. Reads return `[]` / `null`; mutations
  reject (every caller is inside try/catch). Verified: removing the shim makes
  `SettingsPage.vue:168` throw immediately.
- Listener registrations (`onChanged`, `onPanel*`, `onStatusNotification`) MUST return an
  unsubscribe function. Consumers store the return value and call it on unmount.

## FEATURE PARITY
Available: chat, sessions/projects, task graph, approvals, context packs, event SSE,
Model/Agent Center, Market, Settings, Debug Studio, Monaco code viewing.

Unavailable by design: terminal (see `docs/web-client.md` stage 2), desktop pets,
detachable panel windows, native directory picker.

## COMMANDS
```bash
npm run dev -w @tinadec/web      # 127.0.0.1:5174
npm run build -w @tinadec/web
npm run dev:web                  # from repo root
```
