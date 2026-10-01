import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { readFileSync, readdirSync } from 'node:fs'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { ref } from 'vue'
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
