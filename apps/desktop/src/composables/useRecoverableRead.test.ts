// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { defineComponent } from 'vue'
import { useRecoverableRead, type ReadContext } from './useRecoverableRead'
import { setHostAccessStatus } from '@/lib/hostAccess'

const ready = { state: 'ready', managed: true } as const
const wrappers: ReturnType<typeof mount>[] = []
function mountRead(read: (context: ReadContext) => Promise<void>) {
  let result!: ReturnType<typeof useRecoverableRead>
  const wrapper = mount(defineComponent({ setup() { result = useRecoverableRead(read); return () => null } }))
  wrappers.push(wrapper)
  return { wrapper, result }
}
beforeEach(() => setHostAccessStatus(ready))
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()) })

describe('screen read lifecycle', () => {
  it('waits for host verification then automatically recovers its first read', async () => {
    setHostAccessStatus({ ...ready, state: 'checking' })
    const read = vi.fn(async () => {})
    const { result } = mountRead(read)
    await flushPromises()
    expect(read).not.toHaveBeenCalled()
    setHostAccessStatus(ready)
    await flushPromises()
    expect(read).toHaveBeenCalledTimes(1)
    expect(result.loaded.value).toBe(true)
  })
  it('merges manual refreshes and retains the failure until successful recovery', async () => {
    const read = vi.fn().mockRejectedValueOnce(new Error('Offline')).mockResolvedValue(undefined)
    const { result } = mountRead(read)
    await flushPromises()
    expect(result.failure.message.value).toBe('Offline')
    const one = result.refresh()
    expect(result.refresh()).toBe(one)
    expect(result.failure.message.value).toBe('Offline')
    await one
    expect(read).toHaveBeenCalledTimes(2)
    expect(result.failure.error.value).toBeNull()
  })
  it('cancels revoked reads and prevents late results from replacing a recovered read', async () => {
    let old!: ReadContext
    let finishOld!: () => void
    const applied: string[] = []
    const read = vi.fn().mockImplementationOnce((context: ReadContext) => {
      old = context
      return new Promise<void>(resolve => { finishOld = () => { if (context.isCurrent()) applied.push('old'); resolve() } })
    }).mockImplementation(async (context: ReadContext) => { if (context.isCurrent()) applied.push('new') })
    mountRead(read)
    await flushPromises()
    setHostAccessStatus({ ...ready, state: 'unavailable' })
    expect(old.signal.aborted).toBe(true)
    setHostAccessStatus(ready)
    await flushPromises()
    finishOld()
    await flushPromises()
    expect(applied).toEqual(['new'])
  })
  it('does not refresh after unmount, including a late completion', async () => {
    let context!: ReadContext
    const read = vi.fn(async (next: ReadContext) => { context = next })
    const { wrapper } = mountRead(read)
    await flushPromises()
    wrapper.unmount()
    expect(context.isCurrent()).toBe(false)
    setHostAccessStatus({ ...ready, state: 'unavailable' })
    setHostAccessStatus(ready)
    await flushPromises()
    expect(read).toHaveBeenCalledTimes(1)
  })
})
