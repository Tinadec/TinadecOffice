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

  window.MonacoEnvironment = {
    getWorker(_workerId: string, label: string): Worker {
      // Use the generic editor worker for all language features.
      // Rich language services (TS IntelliSense, CSS/HTML completion) require
      // additional worker configuration that can be added later.
      if (label === 'editorWorker' || !label) {
        return new Worker(
          new URL('monaco-editor/esm/vs/editor/editor.worker.js', import.meta.url),
          { type: 'module' },
        )
      }
      // Language-specific workers fall back to the generic editor worker.
      // This keeps the build simple while still providing syntax highlighting.
      return new Worker(
        new URL('monaco-editor/esm/vs/editor/editor.worker.js', import.meta.url),
        { type: 'module' },
      )
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
