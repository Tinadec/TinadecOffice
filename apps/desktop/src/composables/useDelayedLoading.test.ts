// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope, ref, nextTick } from 'vue'
import { useDelayedLoading } from './useDelayedLoading'

describe('useDelayedLoading', () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => { vi.useRealTimers() })

  it('stays hidden for work that finishes inside the delay', async () => {
    const scope = effectScope()
    const loading = ref(false)
    let visible!: ReturnType<typeof useDelayedLoading>
    scope.run(() => { visible = useDelayedLoading(() => loading.value, { delay: 100, minDuration: 300 }) })

    loading.value = true
    await nextTick()
    vi.advanceTimersByTime(50)
    loading.value = false
    await nextTick()
    vi.advanceTimersByTime(1000)
    expect(visible.value).toBe(false)
    scope.stop()
  })

  it('appears once the delay elapses and stays for the minimum', async () => {
    const scope = effectScope()
    const loading = ref(false)
    let visible!: ReturnType<typeof useDelayedLoading>
    scope.run(() => { visible = useDelayedLoading(() => loading.value, { delay: 100, minDuration: 300 }) })

    loading.value = true
    await nextTick()
    vi.advanceTimersByTime(99)
    expect(visible.value).toBe(false)
    vi.advanceTimersByTime(1)
    expect(visible.value).toBe(true)

    // Work ends immediately, but the spinner has only been up for a moment: it holds.
    loading.value = false
    await nextTick()
    vi.advanceTimersByTime(299)
    expect(visible.value).toBe(true)
    vi.advanceTimersByTime(1)
    expect(visible.value).toBe(false)
    scope.stop()
  })

  it('stops its timers when the scope goes away', async () => {
    const scope = effectScope()
    const loading = ref(false)
    let visible!: ReturnType<typeof useDelayedLoading>
    scope.run(() => { visible = useDelayedLoading(() => loading.value, { delay: 100, minDuration: 300 }) })

    loading.value = true
    await nextTick()
    scope.stop()
    vi.advanceTimersByTime(1000)
    expect(visible.value).toBe(false)
  })
})
