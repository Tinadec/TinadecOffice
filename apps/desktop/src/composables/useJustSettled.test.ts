import { describe, expect, it } from 'vitest'
import { effectScope, nextTick, ref } from 'vue'
import { useJustSettled } from './useJustSettled'

describe('useJustSettled', () => {
  it('flags a live -> done transition and clears when work resumes', async () => {
    const live = ref(true)
    const scope = effectScope()
    const settled = scope.run(() => useJustSettled(live))!
    expect(settled.value).toBe(false)
    live.value = false
    await nextTick()
    expect(settled.value).toBe(true)
    live.value = true
    await nextTick()
    expect(settled.value).toBe(false)
    scope.stop()
  })

  it('never flags something that mounted already done', async () => {
    const live = ref(false)
    const scope = effectScope()
    const settled = scope.run(() => useJustSettled(live))!
    await nextTick()
    expect(settled.value).toBe(false)
    scope.stop()
  })
})
