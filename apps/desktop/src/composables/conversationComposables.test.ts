import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope, nextTick, ref } from 'vue'

const api = vi.hoisted(() => ({
  getToolExecutionPreview: vi.fn(),
  listAgents: vi.fn(),
}))
vi.mock('@/api', () => ({ api }))

const { useElapsed, elapsedSubscriberCount } = await import('./useElapsed')
const { useSmoothText } = await import('./useSmoothText')
const { useToolPreview, clearToolPreviewCache } = await import('./useToolPreview')
const { useAgentDirectory, resetAgentDirectory } = await import('./useAgentDirectory')

describe('useElapsed', () => {
  beforeEach(() => vi.useFakeTimers({ now: Date.parse('2026-10-13T09:00:10Z') }))
  afterEach(() => vi.useRealTimers())

  it('counts live time on one shared ticker that stops with the last subscriber', () => {
    const a = effectScope()
    const b = effectScope()
    const start = ref<string | null>('2026-10-13T09:00:00Z')
    const end = ref<string | null>(null)
    const first = a.run(() => useElapsed(start, end))!
    const second = b.run(() => useElapsed(start, ref('2026-10-13T09:00:04Z')))!
    expect(elapsedSubscriberCount()).toBe(2)
    expect(first.value).toBe(10_000)
    vi.advanceTimersByTime(2000)
    expect(first.value).toBe(12_000)
    expect(second.value).toBe(4000)
    end.value = '2026-10-13T09:00:11Z'
    expect(first.value).toBe(11_000)
    a.stop()
    b.stop()
    expect(elapsedSubscriberCount()).toBe(0)
    expect(vi.getTimerCount()).toBe(0)
  })

  it('has no value without a start', () => {
    const scope = effectScope()
    expect(scope.run(() => useElapsed(ref(null), ref(null)))!.value).toBeNull()
    scope.stop()
  })
})

describe('useSmoothText', () => {
  let queue: ((time: number) => void)[] = []
  let clock = 0
  const frame = (callback: (time: number) => void) => { queue.push(callback); return queue.length }
  const step = (ms: number) => { clock += ms; const run = queue; queue = []; run.forEach((cb) => cb(clock)) }

  beforeEach(() => { queue = []; clock = 0 })

  it('shows history whole on the first frame', () => {
    const scope = effectScope()
    const shown = scope.run(() => useSmoothText(ref('Already answered.'), ref(false), { frame, cancelFrame: () => {} }))!
    expect(shown.value).toBe('Already answered.')
    expect(queue).toHaveLength(0)
    scope.stop()
  })

  it('reveals at about 90 characters a second while streaming', async () => {
    const scope = effectScope()
    const target = ref('')
    const streaming = ref(true)
    const shown = scope.run(() => useSmoothText(target, streaming, { frame, cancelFrame: () => {} }))!
    target.value = 'x'.repeat(20)
    await nextTick()
    step(16)
    for (let i = 0; i < 10; i += 1) step(16)
    // ~176ms at 90/s is ~16 characters; never more than arrived.
    expect(shown.value.length).toBeGreaterThan(8)
    expect(shown.value.length).toBeLessThanOrEqual(20)
    scope.stop()
  })

  it('catches up within 0.3s when the stream runs far ahead, and flushes on end', async () => {
    const scope = effectScope()
    const target = ref('')
    const streaming = ref(true)
    const shown = scope.run(() => useSmoothText(target, streaming, { frame, cancelFrame: () => {} }))!
    target.value = 'y'.repeat(900)
    await nextTick()
    for (let elapsed = 0; elapsed < 320; elapsed += 16) step(16)
    expect(shown.value.length).toBe(900)

    target.value += 'z'.repeat(300)
    streaming.value = false
    await nextTick()
    for (let elapsed = 0; elapsed < 140; elapsed += 16) step(16)
    expect(shown.value).toBe(target.value)
    scope.stop()
  })

  it('restarts from the common prefix when the stream is rewritten', async () => {
    const scope = effectScope()
    const target = ref('')
    const streaming = ref(true)
    const shown = scope.run(() => useSmoothText(target, streaming, { frame, cancelFrame: () => {} }))!
    target.value = 'abcdef'
    await nextTick()
    for (let i = 0; i < 20; i += 1) step(16)
    target.value = 'abXYZ'
    await nextTick()
    for (let i = 0; i < 20; i += 1) step(16)
    expect(shown.value).toBe('abXYZ')
    scope.stop()
  })
})

describe('useToolPreview', () => {
  beforeEach(() => {
    clearToolPreviewCache()
    api.getToolExecutionPreview.mockReset()
  })

  it('fetches nothing for a running call, then once per execution when terminal', async () => {
    api.getToolExecutionPreview.mockResolvedValue({ execution_id: 'e', preview: { kind: 'text', text: 'hi' } })
    const status = ref('running')
    const scope = effectScope()
    const first = scope.run(() => useToolPreview(ref('e'), status))!
    await first.load()
    expect(api.getToolExecutionPreview).not.toHaveBeenCalled()
    status.value = 'completed'
    await first.load()
    expect(first.preview.value?.preview).toEqual({ kind: 'text', text: 'hi' })
    const second = scope.run(() => useToolPreview(ref('e'), ref('completed')))!
    expect(second.preview.value).not.toBeNull()
    await second.load()
    expect(api.getToolExecutionPreview).toHaveBeenCalledTimes(1)
    scope.stop()
  })

  it('keeps the error and allows a retry', async () => {
    api.getToolExecutionPreview.mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce({ execution_id: 'f', preview: null })
    const scope = effectScope()
    const preview = scope.run(() => useToolPreview(ref('f'), ref('failed')))!
    await preview.load()
    expect(preview.error.value).toBeInstanceOf(Error)
    expect(preview.loading.value).toBe(false)
    await preview.load()
    expect(preview.error.value).toBeNull()
    expect(preview.preview.value).toEqual({ execution_id: 'f', preview: null })
    scope.stop()
  })
})

describe('useAgentDirectory', () => {
  beforeEach(() => {
    resetAgentDirectory()
    api.listAgents.mockReset()
  })

  it('maps slugs and handles to display names, read once', async () => {
    api.listAgents.mockResolvedValue([{ slug: 'search', display_name: 'Search' }, { slug: 'global_engineering', display_name: 'Engineer' }])
    const { displayName } = useAgentDirectory()
    useAgentDirectory()
    await Promise.resolve()
    await Promise.resolve()
    expect(displayName('search')).toBe('Search')
    expect(displayName('global_engineering#2')).toBe('Engineer #2')
    expect(displayName('unknown')).toBe('unknown')
    expect(api.listAgents).toHaveBeenCalledTimes(1)
  })

  it('falls back to slugs when the directory cannot be read, and retries next time', async () => {
    api.listAgents.mockRejectedValueOnce(new Error('slow')).mockResolvedValueOnce([{ slug: 'search', display_name: 'Search' }])
    const { displayName } = useAgentDirectory()
    await Promise.resolve()
    await Promise.resolve()
    expect(displayName('search#1')).toBe('search #1')
    useAgentDirectory()
    await Promise.resolve()
    await Promise.resolve()
    expect(displayName('search#1')).toBe('Search #1')
  })
})
