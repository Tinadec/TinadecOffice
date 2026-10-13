// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { defineComponent, h, nextTick, shallowRef } from 'vue'
import type { MessageDto } from '@/api'
import type { ConversationTurn } from '@/lib/conversation/turns'
import en from '@/locales/en'

/** The scroller's "content below" flag, driven by hand: happy-dom has no layout to measure. */
const scrollable = shallowRef({ start: false, end: false })
vi.mock('@/components/ui/message-scroller', () => ({
  useMessageScrollerScrollable: () => scrollable,
  MessageScrollerButton: defineComponent({ inheritAttrs: true, setup: (_, { slots }) => () => h('button', slots.default?.()) }),
}))
const { default: JumpToLatest } = await import('./JumpToLatest.vue')

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })
const turn = (id: string): ConversationTurn => ({
  id: `turn:${id}`, runs: [], answers: [], phase: 'settled', startedAt: null, endedAt: null,
  user: { id, session_id: 's', role: 'user', content: id, created_at: null, run_id: null, attachments: [] } as unknown as MessageDto,
})

function mountJump(turns: ConversationTurn[]) {
  scrollable.value = { start: false, end: false }
  return mount(JumpToLatest, { props: { turns, behavior: 'auto' }, global: { plugins: [i18n] } })
}
const count = (wrapper: ReturnType<typeof mountJump>) => wrapper.find('[data-testid="jump-unread"]').text()

describe('JumpToLatest', () => {
  it('counts turns that arrive while the reader is scrolled up, and clears at the bottom', async () => {
    const wrapper = mountJump([turn('a')])
    scrollable.value = { start: true, end: true }
    await nextTick()
    await wrapper.setProps({ turns: [turn('a'), turn('b'), turn('c')] })
    expect(count(wrapper)).toBe('2 new')
    expect(wrapper.attributes('aria-label')).toBe('Jump to latest · 2 new')
    scrollable.value = { start: true, end: false }
    await nextTick()
    expect(wrapper.find('[data-testid="jump-unread"]').exists()).toBe(false)
  })

  it('counts nothing while following the bottom', async () => {
    const wrapper = mountJump([turn('a')])
    await wrapper.setProps({ turns: [turn('a'), turn('b')] })
    expect(wrapper.find('[data-testid="jump-unread"]').exists()).toBe(false)
  })

  it('ignores the opening history, another session, the reader\'s own send and an id swap', async () => {
    const wrapper = mountJump([])
    scrollable.value = { start: false, end: true }
    await nextTick()
    await wrapper.setProps({ turns: [turn('a'), turn('b')] })
    expect(wrapper.find('[data-testid="jump-unread"]').exists()).toBe(false)
    await wrapper.setProps({ turns: [turn('x'), turn('y'), turn('z')] })
    expect(wrapper.find('[data-testid="jump-unread"]').exists()).toBe(false)
    await wrapper.setProps({ turns: [turn('x'), turn('y'), turn('z'), turn('pending-1')] })
    expect(wrapper.find('[data-testid="jump-unread"]').exists()).toBe(false)
    await wrapper.setProps({ turns: [turn('x'), turn('y'), turn('z'), turn('m-9')] })
    expect(wrapper.find('[data-testid="jump-unread"]').exists()).toBe(false)
  })
})
