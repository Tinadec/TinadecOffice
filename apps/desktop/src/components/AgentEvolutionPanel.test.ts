// @vitest-environment happy-dom
/**
 * Part 4 of the brief: a legacy `operation` layer proposal must be handled exactly like the
 * `planning` spelling Core writes today — same icon, same label, same route purpose. Before
 * this, `isOperationLayer` compared against `'planning' || 'operation'` at every call site and
 * any third spelling (`planner`, mixed case, stray whitespace) fell through to the execution
 * branch, so a governance proposal rendered as a worker.
 *
 * Part 3 of the brief is also pinned here: promotion must send the `eval_run_id` Core requires
 * (`AgentCandidatePromotion.cs:35-38` answers 422 `EVAL_REQUIRED` without it), not the
 * agent_id/mode/tools fields Core ignores.
 */
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import en from '@/locales/en'

const mocks = vi.hoisted(() => ({
  listEvolutionProposals: vi.fn(),
  promoteAgentCandidate: vi.fn(),
  rejectAgentCandidate: vi.fn(),
  generateEvolutionProposals: vi.fn(),
  notify: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
  status: { error: vi.fn() },
  confirm: vi.fn(async () => true),
}))

vi.mock('@/api', () => ({ api: mocks }))
vi.mock('@/lib/storageScope', () => ({ scopedApi: (api: unknown) => api }))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: mocks.notify,
    status: mocks.status,
    confirm: mocks.confirm,
    dismissByKey: vi.fn(),
  }),
}))
vi.mock('@/lib/hostAccess', async () => {
  const { ref } = await import('vue')
  return { useHostAccess: () => ({ canAccessBackend: ref(true) }) }
})
vi.mock('@/components/ui', async () => {
  const { defineComponent, h } = await import('vue')
  const withContent = (name: string, tag = 'div') =>
    defineComponent({
      name,
      inheritAttrs: false,
      setup: (_, { slots, attrs }) => () =>
        h(tag, attrs, slots.content ? slots.content() : slots.default?.()),
    })
  return {
    UiCard: withContent('UiCard'),
    UiSheet: withContent('UiSheet'),
    UiBadge: withContent('UiBadge', 'span'),
    UiLabel: withContent('UiLabel', 'label'),
    UiSkeleton: withContent('UiSkeleton'),
    UiInput: defineComponent({
      name: 'UiInput',
      props: ['modelValue', 'placeholder', 'size', 'type'],
      emits: ['update:modelValue'],
      setup: (props, { emit, attrs }) => () =>
        h('input', {
          ...attrs,
          value: props.modelValue,
          onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value),
        }),
    }),
    UiButton: defineComponent({
      name: 'UiButton',
      props: ['disabled'],
      emits: ['click'],
      setup: (props, { emit, slots }) => () =>
        h('button', { disabled: props.disabled, onClick: () => emit('click') }, slots.default?.()),
    }),
  }
})

const { default: AgentEvolutionPanel } = await import('./AgentEvolutionPanel.vue')

const global = { plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] }

const proposal = (over: Record<string, unknown> = {}) => ({
  id: 'p-1',
  source_run_id: 'r-1',
  source_instance_id: 'i-1',
  generated_by_instance_id: 'gen-1',
  name: 'Review specialist',
  layer: 'planning',
  agent_type: 'reviewer',
  status: 'proposed',
  confidence: 0.8,
  promoted_agent_id: null,
  decision_reason: 'Repeated review work',
  created_at: '2026-01-01T00:00:00Z',
  updated_at: '2026-01-01T00:00:00Z',
  ...over,
})

async function mountPanel(): Promise<VueWrapper> {
  const wrapper = mount(AgentEvolutionPanel, { global })
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.clearAllMocks()
  mocks.listEvolutionProposals.mockResolvedValue([proposal()])
  mocks.confirm.mockResolvedValue(true)
})

describe('AgentEvolutionPanel — legacy operation layer', () => {
  it.each(['operation', 'planning', 'planner', 'Operation', ' PLANNING '])(
    'labels a %s layer proposal as the governance layer',
    async (layer) => {
      mocks.listEvolutionProposals.mockResolvedValue([proposal({ layer })])
      const wrapper = await mountPanel()

      const card = wrapper.get('.evolution-proposal-card')
      expect(card.text()).toContain(en.settings.agentLayerPlanning)
      // The execution label must not leak in: that was the bug for `planner`/mixed case.
      expect(card.text()).not.toContain(en.settings.agentLayerExecution)
      wrapper.unmount()
    },
  )

  it('does not treat an execution proposal as governance', async () => {
    mocks.listEvolutionProposals.mockResolvedValue([proposal({ layer: 'execution' })])
    const wrapper = await mountPanel()

    const card = wrapper.get('.evolution-proposal-card')
    expect(card.text()).toContain(en.settings.agentLayerExecution)
    expect(card.text()).not.toContain(en.settings.agentLayerPlanning)
    wrapper.unmount()
  })
})

describe('AgentEvolutionPanel — promotion contract', () => {
  async function openPromote(wrapper: VueWrapper) {
    const button = wrapper.findAll('button').find((b) => b.text().includes(en.agentCenter.evolution.promote))
    expect(button, 'promote button must exist').toBeTruthy()
    await button!.trigger('click')
    await flushPromises()
  }

  it('requires the evaluation run id before enabling promotion', async () => {
    const wrapper = await mountPanel()
    await openPromote(wrapper)

    const confirm = wrapper
      .findAll('button')
      .find((b) => b.text().includes(en.agentCenter.evolution.promoteConfirm))
    expect(confirm, 'confirm button must exist').toBeTruthy()
    // Core answers 422 EVAL_REQUIRED without it, so the button must not be clickable yet.
    expect(confirm!.attributes('disabled')).toBeDefined()

    await wrapper.get('[data-testid="promote-eval-run-id"]').setValue('eval-123')
    const enabled = wrapper
      .findAll('button')
      .find((b) => b.text().includes(en.agentCenter.evolution.promoteConfirm))
    expect(enabled!.attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })

  it('sends only eval_run_id and reason — the fields Core actually reads', async () => {
    mocks.promoteAgentCandidate.mockResolvedValue(proposal({ status: 'promoted' }))
    const wrapper = await mountPanel()
    await openPromote(wrapper)

    await wrapper.get('[data-testid="promote-eval-run-id"]').setValue('eval-123')
    const confirm = wrapper
      .findAll('button')
      .find((b) => b.text().includes(en.agentCenter.evolution.promoteConfirm))
    await confirm!.trigger('click')
    await flushPromises()

    expect(mocks.promoteAgentCandidate).toHaveBeenCalledTimes(1)
    const [id, body] = mocks.promoteAgentCandidate.mock.calls[0]
    expect(id).toBe('p-1')
    expect(body).toEqual({ eval_run_id: 'eval-123', reason: null })
    // The dead fields must be gone, not merely ignored by the server.
    for (const dead of ['agent_id', 'mode', 'model_route_purpose', 'allowed_tools', 'capabilities']) {
      expect(body).not.toHaveProperty(dead)
    }
    wrapper.unmount()
  })

  it('explains why the run id is required', async () => {
    const wrapper = await mountPanel()
    await openPromote(wrapper)
    expect(wrapper.get('[data-testid="promote-eval-hint"]').text()).toContain('evaluation run')
    wrapper.unmount()
  })
})
