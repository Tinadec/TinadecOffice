import { describe, expect, it } from 'vitest'

/**
 * Guards on Debug Studio's transport surface. The panel used to ship a live/offline
 * pill plus step / run / pause / reset buttons that could never be reachable: the pill
 * dials `/api/v1/debug/ws`, Core exposes no WebSocket server at all (a repo-wide search
 * for `WebSocket` over the C# sources returns nothing; the Gateway only proxies
 * `/ws/debug` onward), and `mode` was written by nobody, so three of the four buttons
 * stayed `disabled` forever. These scans keep that class of affordance from coming back.
 */

const sources = import.meta.glob(
  ['./../**/*.vue', './../**/*.ts', '!./../generated/**', '!./../locales/**'],
  { query: '?raw', import: 'default', eager: true },
) as Record<string, string>

/** Keys come back relative to this file; normalise them to `src/…` so suffix matches read clearly. */
function srcPath (key: string): string {
  return key.replace(/^(\.\.\/)+/, '').replace(/^\.\//, 'debug/')
}

function nonTestSources (): Array<{ path: string; text: string }> {
  return Object.entries(sources)
    .map(([key, text]) => ({ path: srcPath(key), text }))
    .filter(({ path }) => !path.endsWith('.test.ts'))
}

function fileEndingWith (suffix: string): string {
  const hit = nonTestSources().find(({ path }) => path.endsWith(suffix))
  if (!hit) throw new Error(`no source matched ${suffix}`)
  return hit.text
}

/** Event names a `<script setup>` child declares in `defineEmits<{ … }>()`. */
function declaredEmits (text: string): string[] {
  const block = /defineEmits<\{([\s\S]*?)\}>\(\)/.exec(text)
  if (!block) return []
  return [...block[1].matchAll(/'([^']+)'|^\s*([a-z][\w-]*)\s*:/gm)]
    .map((m) => m[1] ?? m[2])
    .filter((name): name is string => Boolean(name))
}

/** Event names the same file actually fires through `emit('name', …)`. */
function emittedEvents (text: string): string[] {
  return [...text.matchAll(/\bemit\(\s*'([^']+)'/g)].map((m) => m[1])
}

/** `@event=` bindings a parent puts on one child tag. */
function boundEvents (tag: string): string[] {
  return [...tag.matchAll(/@([a-z][\w-]*)=/g)].map((m) => m[1])
}

describe('debug studio transport surface', () => {
  it('opens no WebSocket anywhere in the desktop front-end', () => {
    // Core serves SSE (`/api/v1/runs/{id}/stream`, `/api/v1/events`), not WebSockets,
    // so a socket client here can only ever sit in the closed state.
    const offenders = nonTestSources()
      .filter(({ text }) => text.includes('new WebSocket('))
      .map(({ path }) => path)
    expect(offenders).toEqual([])
  })

  it('declares no child event that its own template never fires', () => {
    const bar = fileEndingWith('debug/components/SimulatorBar.vue')
    const unfired = declaredEmits(bar).filter((name) => !emittedEvents(bar).includes(name))
    expect(unfired).toEqual([])
  })

  it('binds only events the child declares', () => {
    const studio = fileEndingWith('debug/DebugStudio.vue')
    const bar = fileEndingWith('debug/components/SimulatorBar.vue')
    const tag = /<SimulatorBar[\s\S]*?\/>/.exec(studio)?.[0]
    if (!tag) throw new Error('DebugStudio no longer mounts SimulatorBar')
    const declared = declaredEmits(bar)
    const unbound = boundEvents(tag).filter((name) => !declared.includes(name))
    expect(unbound).toEqual([])
  })
})
