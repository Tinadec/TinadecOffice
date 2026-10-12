import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { readFileSync, readdirSync } from 'node:fs'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { ref, nextTick } from 'vue'
import { api, type EventEnvelope } from '@/api'

vi.mock('@/api', () => ({
  api: {
    connectEvents: vi.fn(),
  },
}))

const connect = vi.mocked(api.connectEvents)

interface FakeSource {
  closed: number
  close(): void
}

/** The api hands back an EventSource; the bus only ever calls close() on it. */
function stubConnection(): (
  sessionId: string | null,
  onEvent: (event: EventEnvelope) => void,
) => EventSource {
  return (sessionId, onEvent) => {
    void sessionId
    const source: FakeSource = { closed: 0, close() { source.closed += 1 } }
    // Stash the delivery callback so the test can push frames through it.
    deliveries.push(onEvent)
    liveSources.push(source)
    return source as unknown as EventSource
  }
}

let deliveries: Array<(event: EventEnvelope) => void> = []
let liveSources: FakeSource[] = []
let bus: typeof import('./sessionEventBus')

function event(type: string, sessionId: string | null, seq = 1): EventEnvelope {
  return { v: '1', type, request_id: 'r-1', session_id: sessionId, trace_id: 't-1', seq, ts: '', capabilities: [] }
}

function emit(event_: EventEnvelope) {
  for (const deliver of deliveries) deliver(event_)
}

beforeEach(async () => {
  vi.resetModules()
  deliveries = []
  liveSources = []
  connect.mockReset()
  connect.mockImplementation(stubConnection())
  bus = await import('./sessionEventBus')
})

afterEach(() => {
  process.removeAllListeners('unhandledRejection')
})

describe('sessionEventBus', () => {
  it('buffers through the snapshot and delivers each live run and sequence once', async () => {
    bus.followSession('s-1')
    let finish!: () => void
    const seen: EventEnvelope[] = []
    const release = bus.subscribeToSessionEvents(e => {
      if (e.type === 'stream.ready') return new Promise<void>(resolve => { finish = resolve })
      seen.push(e)
    })
    expect(connect.mock.calls[0]?.[2]).toEqual({ replay: false })
    emit(event('stream.ready', 's-1', 0))
    const snap = { ...event('tool.execution.completed', 's-1', 1), run_id: 'old-run' }
    const next = { ...event('task.accepted', 's-1', 1), run_id: 'new-run' }
    emit(snap); emit(snap); emit(next)
    expect(seen).toEqual([])
    finish(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve()
    expect(seen).toEqual([snap, next])
    release()
  })
  it('releases buffered live frames when a snapshot read fails', async () => {
    bus.followSession('s-1')
    const seen: EventEnvelope[] = []
    const release = bus.subscribeToSessionEvents(e => {
      if (e.type === 'stream.ready') return Promise.reject(new Error('snapshot unavailable'))
      seen.push(e)
    })
    emit(event('stream.ready', 's-1', 0)); emit({ ...event('task.accepted', 's-1'), run_id: 'new' })
    await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve()
    expect(seen).toHaveLength(1)
    release()
  })
  it('keeps the new history snapshot barrier after the old snapshot completes', async () => {
    bus.followSession('s-1')
    let ready!: () => void, changed!: () => void
    const seen: EventEnvelope[] = []
    const release = bus.subscribeToSessionEvents(e => {
      if (e.type === 'stream.ready') return new Promise<void>(resolve => { ready = resolve })
      if (e.type === 'history.changed') return new Promise<void>(resolve => { changed = resolve })
      seen.push(e)
    })
    emit(event('stream.ready', 's-1', 0))
    emit({ ...event('tool.execution.completed', 's-1', 1), run_id: 'reverted' })
    emit(event('history.changed', 's-1', 0))
    const latest = { ...event('task.accepted', 's-1', 1), run_id: 'latest' }
    emit(latest)
    ready(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve()
    expect(seen).toEqual([])
    changed(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve()
    expect(seen).toEqual([latest])
    release()
  })
  it('discards old buffered events when an automatic reconnect reports a newer history epoch', async () => {
    bus.followSession('s-1')
    const snapshots: Array<() => void> = [], seen: EventEnvelope[] = []
    const release = bus.subscribeToSessionEvents(e => {
      if (e.type === 'stream.ready') return new Promise<void>(resolve => { snapshots.push(resolve) })
      seen.push(e)
    })
    emit({ ...event('stream.ready', 's-1', 0), payload: { revert_epoch: 1 } })
    emit({ ...event('tool.execution.completed', 's-1', 1), run_id: 'reverted' })
    emit({ ...event('stream.ready', 's-1', 0), payload: { revert_epoch: 2 } })
    const retained = { ...event('task.accepted', 's-1', 1), run_id: 'retained' }
    emit(retained)
    snapshots[0]!(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve()
    expect(seen).toEqual([])
    snapshots[1]!(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve()
    expect(seen).toEqual([retained])
    release()
  })
  it('replaces a raw session feed when the same session id resolves to another storage scope', async () => {
    const storage = await import('./storageScope')
    storage.setSelectedStorage('original'); storage.registerSessionStorage('same', 'original')
    bus.followSession('same')
    const seen: EventEnvelope[] = [], release = bus.subscribeToSessionEvents(e => { seen.push(e) }, { scope: ref('same') })
    storage.setSelectedStorage('copy'); storage.registerSessionStorage('same', 'copy')
    bus.followSession('same')
    expect(connect).toHaveBeenCalledTimes(2)
    expect(liveSources[0]!.closed).toBe(1)
    deliveries[0]!({ ...event('task.accepted', 'same'), run_id: 'original-run' })
    const copied = { ...event('task.accepted', 'same'), run_id: 'copy-run' }
    deliveries[1]!(copied)
    expect(seen).toEqual([copied])
    release()
  })
  it('does not open a preview stream and reconnects reads only after host authentication', async () => {
    const { setHostAccessStatus } = await import('./hostAccess')
    setHostAccessStatus({ state: 'preview', managed: false })
    bus.followSession('project-scope::session')
    const release = bus.subscribeToSessionEvents(() => {})
    await nextTick()
    expect(connect).not.toHaveBeenCalled()
    setHostAccessStatus({ state: 'ready', managed: true })
    await nextTick()
    expect(connect).toHaveBeenCalledTimes(1)
    setHostAccessStatus({ state: 'unavailable', managed: true })
    await nextTick()
    expect(liveSources[0]!.closed).toBe(1)
    setHostAccessStatus({ state: 'ready', managed: true })
    await nextTick()
    expect(connect).toHaveBeenCalledTimes(2)
    release()
  })
  it('releases only its captured scope while closing and can resume on failure', () => {
    bus.followSession('original::same-session')
    const release = bus.subscribeToSessionEvents(() => {})
    const unrelated = bus.suspendFollowingStorage('copy'); unrelated()
    expect(liveSources[0]?.closed).toBe(0)
    const resume = bus.suspendFollowingStorage('original')
    expect(liveSources[0]?.closed).toBe(1)
    resume()
    expect(liveSources).toHaveLength(2)
    release()
  })
  it('suspends a transfer source without reopening it for subscribers and resumes on selection', () => {
    bus.followSession('user::source')
    const release = bus.subscribeToSessionEvents(() => {})
    bus.suspendFollowingSession('user::source')
    expect(liveSources[0]!.closed).toBe(1)
    const releaseSecond = bus.subscribeToSessionEvents(() => {})
    expect(connect).toHaveBeenCalledTimes(1)
    bus.followSession('target::source')
    expect(connect).toHaveBeenCalledTimes(2)
    expect(connect.mock.calls[1]![0]).toBe('target::source')
    release(); releaseSecond()
  })
  it('carries one connection however many owners subscribe', () => {
    bus.followSession('s-1')
    const releaseA = bus.subscribeToSessionEvents(() => {})
    const releaseB = bus.subscribeToSessionEvents(() => {})
    bus.subscribeToSessionEvents(() => {}, { scope: ref('s-1') })

    expect(connect).toHaveBeenCalledTimes(1)
    expect(connect.mock.calls[0]![0]).toBe('s-1')
    releaseA()
    releaseB()
    expect(connect).toHaveBeenCalledTimes(1)
  })

  it('closes the connection when the last owner leaves', () => {
    const release = bus.subscribeToSessionEvents(() => {})
    expect(liveSources).toHaveLength(1)
    release()
    expect(liveSources[0]!.closed).toBe(1)
    expect(bus.subscribeToSessionEvents(() => {})).toBeTypeOf('function')
    expect(connect).toHaveBeenCalledTimes(2)
  })

  it('reconnects once when the window moves to another session', () => {
    bus.followSession('s-1')
    const release = bus.subscribeToSessionEvents(() => {})
    bus.followSession('s-1')
    bus.followSession('s-2')

    expect(connect).toHaveBeenCalledTimes(2)
    expect(liveSources[0]!.closed).toBe(1)
    expect(connect.mock.calls[1]![0]).toBe('s-2')
    release()
  })

  it('scopes an owner to its session without dropping frames that name none', () => {
    bus.followSession(null)
    const release = bus.subscribeToSessionEvents(() => {})
    const scope = ref<string | null>('s-1')
    const scoped: EventEnvelope[] = []
    bus.subscribeToSessionEvents((event) => scoped.push(event), { scope })
    const unscoped: EventEnvelope[] = []
    bus.subscribeToSessionEvents((event) => unscoped.push(event))

    emit(event('run.completed', 's-1'))
    emit(event('run.completed', 's-2'))
    // The durable journal also carries session-less rows; they belong to the window, not
    // to no one, so a scoped owner must still see them.
    emit(event('approval.requested', null))
    expect(scoped).toHaveLength(2)

    scope.value = null
    emit(event('run.completed', 's-1'))

    // A closed session starves its owner instead of letting it render leftovers.
    expect(scoped).toHaveLength(2)
    expect(unscoped).toHaveLength(4)
    release()
  })

  it('keeps delivering when one owner throws', () => {
    bus.followSession('s-1')
    const release = bus.subscribeToSessionEvents(() => {
      throw new Error('widget exploded')
    })
    const seen: EventEnvelope[] = []
    bus.subscribeToSessionEvents((event) => seen.push(event))

    emit(event('message.created', 's-1'))

    expect(seen).toHaveLength(1)
    release()
  })

  it('absorbs a rejected async owner instead of surfacing an unhandled rejection', async () => {
    const rejections: unknown[] = []
    process.on('unhandledRejection', (reason) => rejections.push(reason))

    bus.followSession('s-1')
    const release = bus.subscribeToSessionEvents(async () => {
      throw new Error('reload failed')
    })
    const seen: EventEnvelope[] = []
    bus.subscribeToSessionEvents((event) => seen.push(event))
    emit(event('message.created', 's-1'))

    await Promise.resolve()
    await Promise.resolve()
    expect(seen).toHaveLength(1)
    expect(rejections).toEqual([])
    release()
  })

  /**
   * The point of the module is "one connection", so the guarantee is checked across the
   * whole renderer rather than only in here: a second owner opening its own EventSource
   * is exactly the shape this file was written to remove, and it is invisible to any
   * single-owner test.
   */
  it('is the renderer’s only owner of the session connection', () => {
    const root = fileURLToPath(new URL('..', import.meta.url))
    const allowed = new Set([
      join(root, 'api.ts'),
      join(root, 'api.test.ts'),
      join(root, 'lib', 'sessionEventBus.ts'),
      join(root, 'lib', 'sessionEventBus.test.ts'),
      // The debug preview stands in for the api object itself, so it defines the method.
      join(root, 'debug', 'preview', 'mockApi.ts'),
    ])
    const offenders: string[] = []
    for (const file of sourceFiles(root)) {
      if (allowed.has(file)) continue
      if (readFileSync(file, 'utf8').includes('connectEvents(')) offenders.push(relative(root, file))
    }
    expect(offenders).toEqual([])
  })
})

function sourceFiles(dir: string): string[] {
  const found: string[] = []
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) {
      // generated/ mirrors the contract and node_modules/ is not ours to police.
      if (entry.name === 'generated' || entry.name === 'node_modules') continue
      found.push(...sourceFiles(path))
    } else if (/\.((test\.)?ts|vue)$/.test(entry.name)) {
      found.push(path)
    }
  }
  return found
}
