/**
 * Monaco Editor Vite integration helpers.
 *
 * This module sets up `self.MonacoEnvironment` so that Monaco can spawn its
 * language web workers through Vite's `new URL(..., import.meta.url)` worker
 * bundling. Import this module once (for side effects) before initialising
 * Monaco.
 *
 * The exported `monacoVitePluginConfig` object is a hint for the eventual
 * `vite.config.ts` integration — it is not consumed at runtime here.
 */

interface MonacoEnvironment {
  getWorker?(workerId: string, label: string): Worker
  getWorkerUrl?(workerId: string, label: string): string
}

declare global {
  interface Window {
    MonacoEnvironment?: MonacoEnvironment
  }
}

function installMonacoEnvironment(): void {
  if (typeof window === 'undefined') return
  if (window.MonacoEnvironment && typeof window.MonacoEnvironment.getWorker === 'function') return

  // `import('monaco-editor')` resolves to editor.main, which registers the
  // FULL language-service clients (typescript/json/css/html). Each client
  // spawns a worker by label and issues RPCs like `getNavigationTree` — those
  // handlers only exist in the matching language worker, so routing every
  // label to the generic editor worker throws
  // "Missing requestHandler or method: …" on first use.
  //
  // Each `new URL(...)` must stay a STATIC string literal: Vite rewrites them
  // at build time (a template literal is parsed as an invalid import glob).
  const tsWorker = new URL(
    'monaco-editor/esm/vs/language/typescript/ts.worker.js',
    import.meta.url,
  )
  const jsonWorker = new URL('monaco-editor/esm/vs/language/json/json.worker.js', import.meta.url)
  const cssWorker = new URL('monaco-editor/esm/vs/language/css/css.worker.js', import.meta.url)
  const htmlWorker = new URL('monaco-editor/esm/vs/language/html/html.worker.js', import.meta.url)
  const editorWorker = new URL('monaco-editor/esm/vs/editor/editor.worker.js', import.meta.url)

  window.MonacoEnvironment = {
    getWorker(_workerId: string, label: string): Worker {
      switch (label) {
        case 'typescript':
        case 'javascript':
          return new Worker(tsWorker, { type: 'module' })
        case 'json':
          return new Worker(jsonWorker, { type: 'module' })
        case 'css':
        case 'scss':
        case 'less':
          return new Worker(cssWorker, { type: 'module' })
        case 'html':
        case 'handlebars':
        case 'razor':
          return new Worker(htmlWorker, { type: 'module' })
        default:
          // Editor core worker (diff computation, word navigation, links…).
          return new Worker(editorWorker, { type: 'module' })
      }
    },
  }
}

installMonacoEnvironment()

/**
 * Configuration hint for `vite.config.ts`.
 *
 * The main agent can spread this into the Vite plugin array to ensure
 * `monaco-editor` ESM workers are handled correctly.
 */
export const monacoVitePluginConfig = {
  optimizeDeps: {
    include: ['monaco-editor/esm/vs/editor/editor.api'],
  },
  worker: {
    format: 'es' as const,
  },
}

export { installMonacoEnvironment }
