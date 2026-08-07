// rendererErrorFallback.ts — Global renderer error containment.
//
// Before this module, the renderer had zero error handling: no
// `app.config.errorHandler`, no `window.onerror`, no `unhandledrejection`
// listener, and `app.mount()` ran bare. Any uncaught error during boot or a
// reload left a silent, solid-color window (a stuck `.app-splash`) with no log
// and no recovery. This installs a reporting + first-error DOM fallback so a
// crash is always logged and never leaves the window blank without a recovery
// path.
//
// The DOM fallback is deliberately PURE DOM — it must not go through Vue,
// because the tree that just crashed must not be asked to re-render.

interface ErrorContext {
  source?: 'vue' | 'window' | 'rejection' | 'app.mount' | string
  /** Best-effort component name / file:line from the crashing site. */
  detail?: string
}

let installed = false

function messageOf(err: unknown): string {
  if (err instanceof Error) return err.message
  if (typeof err === 'string') return err
  if (err && typeof err === 'object' && 'message' in err) {
    return String((err as { message: unknown }).message)
  }
  return String(err)
}

function componentNameOf(instance: unknown): string {
  if (instance && typeof instance === 'object') {
    const c = (instance as { type?: { name?: string; __name?: string } }).type
    return c?.name ?? c?.__name ?? ''
  }
  return ''
}

/** Remove any splash surface at the DOM level so the window can never stay stuck. */
function removeSplashSurfaces(): void {
  document.querySelector('.app-splash')?.remove()
  document.querySelector('.splash-placeholder')?.remove()
}

function buildFallback(message: string): void {
  // Guard against double insertion (first error only).
  if (document.querySelector('#renderer-error-fallback')) return
  removeSplashSurfaces()

  const overlay = document.createElement('div')
  overlay.id = 'renderer-error-fallback'
  overlay.style.cssText =
    'position:fixed;inset:0;z-index:2147483647;background:var(--bg-primary,#0a0e14);' +
    'display:flex;align-items:center;justify-content:center;'

  const card = document.createElement('div')
  card.style.cssText =
    'max-width:560px;width:calc(100% - 48px);padding:24px 28px;' +
    'border:1px solid var(--border-muted,#2a2a3a);border-radius:12px;' +
    'background:var(--surface-section,#131722);box-shadow:var(--shadow-panel,none);' +
    'display:flex;flex-direction:column;gap:16px;'

  const title = document.createElement('h1')
  title.style.cssText = 'margin:0;font-size:15px;font-weight:600;color:var(--text-primary,#e6e6e6);'
  title.textContent = '应用界面出现错误 / UI crashed'

  const pre = document.createElement('pre')
  pre.style.cssText =
    'margin:0;padding:12px;overflow:auto;max-height:180px;font-size:12px;line-height:1.5;' +
    'font-family:ui-monospace,SFMono-Regular,Consolas,monospace;color:var(--text-secondary,#9aa0aa);' +
    'background:var(--bg-primary,#0a0e14);border-radius:8px;white-space:pre-wrap;word-break:break-all;'
  pre.textContent = message.slice(0, 2000)

  const reloadBtn = document.createElement('button')
  reloadBtn.type = 'button'
  reloadBtn.textContent = '重新加载 / Reload'
  reloadBtn.style.cssText =
    'align-self:flex-start;padding:8px 16px;font-size:13px;font-weight:500;color:#fff;' +
    'background:var(--accent-brand,#2ec4b6);border:none;border-radius:8px;cursor:pointer;'
  reloadBtn.addEventListener('click', () => {
    const w = window as unknown as { tinadec?: { restartApp?: () => Promise<unknown> } }
    if (w.tinadec?.restartApp) {
      void w.tinadec.restartApp()
    } else {
      window.location.reload()
    }
  })

  card.append(title, pre, reloadBtn)
  overlay.append(card)
  document.body.append(overlay)
}

/**
 * Install global renderer error containment. Call BEFORE `app.mount()`.
 * Returns a `report` function usable anywhere (e.g. the `app.mount` try/catch).
 *
 * Only the FIRST FATAL error swaps the stuck splash for the DOM fallback.
 * Fatal = a render/lifecycle error (via `app.config.errorHandler`) or a real
 * uncaught script error (via `window.onerror`). Background promise rejections
 * (`unhandledrejection`, e.g. a failed API call) are logged but never trigger
 * the full-screen fallback — they are non-fatal and common, and covering the
 * whole window for one would be worse than the error itself.
 */
export function installRendererErrorFallback(app: { config: { errorHandler?: unknown } }): (err: unknown, ctx?: ErrorContext) => void {
  const report = (err: unknown, ctx: ErrorContext = {}, fatal = false): void => {
    const message = messageOf(err)
    const detail = ctx.detail ?? (ctx.source === 'vue' && err && typeof err === 'object' ? '' : '')
    console.error('[renderer] uncaught:', message, { source: ctx.source, detail })
    if (fatal && typeof document !== 'undefined') buildFallback(message)
  }

  if (installed) return report
  installed = true

  app.config.errorHandler = (err: unknown, instance: unknown, info: string) => {
    const name = componentNameOf(instance)
    report(err, { source: 'vue', detail: name ? `component=<${name}>` : info }, true)
  }

  if (typeof window !== 'undefined') {
    window.addEventListener('error', (e) => {
      // Ignore asset-load failures (no error object); report real script errors.
      if (e.error) report(e.error, { source: 'window', detail: `${e.filename}:${e.lineno}` }, true)
    })
    window.addEventListener('unhandledrejection', (e) => {
      // Log only — background async failures must not cover the whole window.
      report(e.reason, { source: 'rejection' })
    })
  }

  return report
}
