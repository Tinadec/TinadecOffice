// apiBridge.ts — Patch-style mock injection for the Debug Studio preview.
//
// The renderer `api` is a singleton object whose methods are always accessed
// as properties at call time (`api.listProjects()`), never destructured at
// module top level. That makes a patch-and-restore bridge safe: while the
// preview gallery is mounted we swap every function member of the singleton
// for either the mock implementation (mockApi.ts) or a shape-convention stub,
// then restore the originals on unmount.
//
// Scope safety: each Electron window is its own JS context, so patching here
// only affects the Debug Studio window. Real pages (SettingsPage, CodePage,
// GitPanel, MarketPage cards) render against mock data without touching the
// Gateway. Anything mockApi does not implement is routed to a convention
// stub (`list*/search*/discover* → []`, everything else → null) and recorded
// in `unmockedMethods` so the gallery can surface coverage gaps.

import { ref, type Ref } from 'vue'
import { api } from '@/api'
import { createMockApi, type MockApi } from './mockApi'
import type { ScenarioId } from './scenarios'

type ApiRecord = Record<string, unknown>

const originals = new Map<string, unknown>()
let depth = 0

/** Methods invoked by real pages but not implemented by mockApi. */
export const unmockedMethods = ref<ReadonlySet<string>>(new Set())

function recordUnmocked(name: string): void {
  if (unmockedMethods.value.has(name)) return
  const next = new Set(unmockedMethods.value)
  next.add(name)
  unmockedMethods.value = next
}

function stubFor(name: string): () => Promise<unknown> {
  if (/^(list|search|discover)/.test(name)) return async () => []
  return async () => null
}

/**
 * Install the mock api over the real singleton. Idempotent and reference
 * counted: nested installs (gallery + page) share one patch layer; the real
 * methods come back when the last consumer restores.
 */
export function installPreviewApi(scenarioRef: Ref<ScenarioId>): void {
  if (depth === 0) {
    const target = api as unknown as ApiRecord
    const mock: MockApi = createMockApi(scenarioRef)
    const mockRecord = mock as unknown as ApiRecord

    originals.clear()
    for (const key of Object.keys(target)) {
      if (typeof target[key] === 'function') originals.set(key, target[key])
    }
    for (const [key, fn] of originals) {
      target[key] = (...args: unknown[]) => {
        const mocked = mockRecord[key]
        if (typeof mocked === 'function') {
          return (mocked as (...a: unknown[]) => unknown).apply(mock, args)
        }
        recordUnmocked(key)
        return stubFor(key)()
      }
    }
    // Mock-only extras that the real api lacks (none today, kept for parity).
    for (const key of Object.keys(mockRecord)) {
      if (!(key in target) && typeof mockRecord[key] === 'function') {
        originals.set(key, undefined)
        target[key] = (...args: unknown[]) =>
          (mockRecord[key] as (...a: unknown[]) => unknown).apply(mock, args)
      }
    }
  }
  depth++
}

/** Undo installPreviewApi (reference counted). */
export function restoreRealApi(): void {
  if (depth === 0) return
  depth--
  if (depth === 0) {
    const target = api as unknown as ApiRecord
    for (const [key, fn] of originals) {
      if (fn === undefined) delete target[key]
      else target[key] = fn
    }
    originals.clear()
    unmockedMethods.value = new Set()
  }
}
