import { describe, expect, it } from 'vitest'
import { ref } from 'vue'
import { latestReasoningTitle, reasoningSections } from './reasoning'
import { useAutoDisclosure } from '@/composables/useAutoDisclosure'

describe('reasoningSections', () => {
  it('turns bold lines into section titles', () => {
    expect(reasoningSections('**Reading**\nLook at a.ts\n\n**Planning**\nThen edit')).toEqual([
      { title: 'Reading', body: 'Look at a.ts' },
      { title: 'Planning', body: 'Then edit' },
    ])
  })

  it('keeps leading text without a title', () => {
    expect(reasoningSections('Just thinking\nmore')).toEqual([{ title: null, body: 'Just thinking\nmore' }])
  })

  it('holds back a title the stream has only half written', () => {
    expect(latestReasoningTitle('**Reading**\nok\n**Check')).toBe('Reading')
    expect(latestReasoningTitle('**Reading**\nok\n**Checking callers**')).toBe('Checking callers')
    expect(latestReasoningTitle('no titles here')).toBeNull()
  })
})

describe('useAutoDisclosure', () => {
  it('follows the work until the reader decides, then keeps their choice', () => {
    const live = ref(true)
    const { open, toggle, userToggled } = useAutoDisclosure(live)
    expect(open.value).toBe(true)
    live.value = false
    expect(open.value).toBe(false)
    live.value = true
    toggle()
    expect(open.value).toBe(false)
    expect(userToggled.value).toBe(true)
    live.value = false
    live.value = true
    expect(open.value).toBe(false)
  })
})
