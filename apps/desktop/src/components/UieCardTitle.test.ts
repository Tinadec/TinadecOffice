// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { defineComponent, h } from 'vue'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import zh from '@/locales/zh-CN'
import { useCardTitle } from '../../../TinadecUI/src/components/cardTitle'

/** The titles a tab bar would show for these stored cards, in one app language. */
function titles(locale: 'en' | 'zh-CN', cards: Array<{ descriptorId: string; title: string }>): string[] {
  let shown: string[] = []
  const Probe = defineComponent({
    setup() {
      const cardTitle = useCardTitle()
      shown = cards.map(cardTitle)
      return () => h('div')
    },
  })
  mount(Probe, { global: { plugins: [createI18n({ legacy: false, locale, messages: { en, 'zh-CN': zh } })] } }).unmount()
  return shown
}

describe('UIE tab titles', () => {
  it('shows a layout saved in Chinese with English tab names in the English app', () => {
    // Before, these stored preset titles were shown as they were, so a saved layout stayed Chinese.
    expect(titles('en', [{ descriptorId: 'nav', title: '项目' }, { descriptorId: 'approval', title: '审批' }])).toEqual(['Projects', 'Approvals'])
  })

  it('names an untitled card by its type and keeps a title the card was opened with', () => {
    expect(titles('zh-CN', [
      { descriptorId: 'homePicker', title: 'Home' },
      { descriptorId: 'terminal', title: '' },
      { descriptorId: 'terminal', title: 'npm test' },
    ])).toEqual(['首页', '终端', 'npm test'])
  })
})
