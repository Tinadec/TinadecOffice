// @vitest-environment happy-dom
import { afterEach, expect, it, vi } from 'vitest'
import { installRendererErrorFallback } from './rendererErrorFallback'
afterEach(() => { document.body.innerHTML = ''; vi.restoreAllMocks() })
it('keeps the original error, complete stack and component lifecycle context', () => {
  const error = new Error('dimensions')
  error.stack = `Error: dimensions\n${'    at viewport (xterm.js:1:50803)\n'.repeat(80)}`
  const log = vi.spyOn(console, 'error').mockImplementation(() => {})
  const app: { config: { errorHandler?: unknown } } = { config: {} }
  installRendererErrorFallback(app)
  ;(app.config.errorHandler as (err: unknown, instance: unknown, info: string) => void)(error, { type: { __name: 'TerminalView' } }, 'mounted hook')
  const diagnostic = document.querySelector('#renderer-error-fallback pre')?.textContent ?? ''
  expect(diagnostic).toContain(error.stack)
  expect(diagnostic).toContain('component=<TerminalView>; mounted hook')
  expect(log.mock.calls[0][1]).toBe(error)
})
