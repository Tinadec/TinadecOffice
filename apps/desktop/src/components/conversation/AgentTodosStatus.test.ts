// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { nextTick, ref, shallowRef } from 'vue'
import en from '@/locales/en'
import { applyEvents, createFeedState } from '@/lib/conversation/reducer'
import { EventScript } from '@/lib/conversation/fixtures/builder'
import type { ConversationFeed } from '@/composables/useConversationFeed'

vi.mock('@/api', () => ({ api: { listAgents: vi.fn(async () => []) } }))
const { default: AgentTodosStatus } = await import('./AgentTodosStatus.vue')

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })

function feedWithPlans() {
  const s = new EventScript()
  s.emit('run-1', 'worker.assigned', { task_id: 'sub', task_key: 'k', handle: 'search#1', agent_slug: 'search', reason: 'coordinator_assigned' })
  s.emit('run-1', 'plan.updated', { task_id: 'sub', handle: 'search#1', steps: [{ step: 'Sub step', status: 'in_progress' }] })
  s.emit('run-1', 'plan.updated', { task_id: null, steps: [{ step: 'Read', status: 'completed' }, { step: 'Write tests', status: 'in_progress' }, { step: 'Ship', status: 'pending' }] })
  const state = createFeedState()
  applyEvents(state, s.events)
  return { state: shallowRef(state), revision: ref(1), hydrated: ref(true) } satisfies ConversationFeed
}

describe('AgentTodosStatus', () => {
  it('shows the live run\'s own plan as a capsule and opens the full list', async () => {
    const wrapper = mount(AgentTodosStatus, { props: { feed: feedWithPlans(), runs: [{ id: 'run-1', status: 'executing' }] }, global: { plugins: [i18n] } })
    const capsule = wrapper.get('[data-testid="todos-capsule"]')
    expect(capsule.text()).toBe('Plan 1/3· Write tests')
    expect(capsule.attributes('aria-expanded')).toBe('false')
    await capsule.trigger('click')
    expect(capsule.attributes('aria-expanded')).toBe('true')
    expect(wrapper.findAll('.conversation-todo')).toHaveLength(3)
  })

  it('falls back to a sub-agent\'s plan when the run has none of its own', () => {
    const s = new EventScript()
    s.emit('run-1', 'worker.assigned', { task_id: 'sub', task_key: 'k', handle: 'search#1', agent_slug: 'search', reason: 'coordinator_assigned' })
    s.emit('run-1', 'plan.updated', { task_id: 'sub', steps: [{ step: 'Sub step', status: 'in_progress' }] })
    const state = createFeedState()
    applyEvents(state, s.events)
    const wrapper = mount(AgentTodosStatus, { props: { feed: { state: shallowRef(state), revision: ref(1), hydrated: ref(true) }, runs: [{ id: 'run-1', status: 'executing' }] }, global: { plugins: [i18n] } })
    expect(wrapper.get('[data-testid="todos-capsule"]').text()).toContain('Sub step')
  })

  it('disappears once the run settles', async () => {
    const wrapper = mount(AgentTodosStatus, { props: { feed: feedWithPlans(), runs: [{ id: 'run-1', status: 'executing' }] }, global: { plugins: [i18n] } })
    expect(wrapper.find('[data-testid="todos-status"]').exists()).toBe(true)
    await wrapper.setProps({ runs: [{ id: 'run-1', status: 'completed' }] })
    await nextTick()
    expect(wrapper.find('[data-testid="todos-status"]').exists()).toBe(false)
  })
})
