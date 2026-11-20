// @vitest-environment happy-dom
/**
 * Live runtime instances moved out of Settings into `/governance`, so the page — not a tab
 * click — has to be what mounts them. These assertions pin the four things that were wrong:
 * the panel was behind `v-if` (so nothing loaded until the tab was clicked), a terminal run
 * still offered Pause/Resume/Cancel/Steer, a `paused` run offered Pause, and `?session_id=`
 * in the route was ignored.
 */
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import en from '@/locales/en'

const mocks = vi.hoisted(() => ({
  listApprovals: vi.fn(),
  listPermissionRequests: vi.fn(),
  listUserToolActions: vi.fn(),
  decideApproval: vi.fn(),
  decidePermissionRequest: vi.fn(),
  listRuntimeInstances: vi.fn(),
  getAgentRuntimePolicy: vi.fn(),
  controlRun: vi.fn(),
  notify: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}))

vi.mock('@/api', () => ({
  api: {
    listApprovals: mocks.listApprovals,
    listPermissionRequests: mocks.listPermissionRequests,
    listUserToolActions: mocks.listUserToolActions,
    decideApproval: mocks.decideApproval,
    decidePermissionRequest: mocks.decidePermissionRequest,
    listRuntimeInstances: mocks.listRuntimeInstances,
    getAgentRuntimePolicy: mocks.getAgentRuntimePolicy,
  },
}))
vi.mock('@/generated/client', () => ({ generatedApi: { controlRun: mocks.controlRun } }))
vi.mock('@/lib/storageScope', () => ({ scopedApi: (api: unknown) => api }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify: mocks.notify }) }))
vi.mock('@/components/ui', () => ({
  UiBadge: { template: '<span class="ui-badge"><slot /></span>' },
  // `disabled` must reach the DOM so the assertions below read the real control state.
  UiButton: {
    props: ['disabled'],
    template: '<button :disabled="disabled"><slot /></button>',
  },
  UiInput: { props: ['modelValue'], template: '<input :value="modelValue" />' },
  UiLabel: { template: '<label><slot /></label>' },
  UiSheet: { props: ['open'], template: '<div class="ui-sheet"><slot /></div>' },
  UiSelectField: {
    props: ['modelValue', 'options'],
    template: '<div class="ui-select"><slot /></div>',
  },
}))

const { default: RuntimeInstancesPanel } = await import('@/settings/sections/RuntimeInstancesPanel.vue')
const { default: GovernanceBoardPage } = await import('./GovernanceBoardPage.vue')

const global = { plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] }

const instance = (over: Record<string, unknown> = {}) => ({
  id: 'i-1',
  run_id: 'run-1',
  session_id: 'sess-1',
  agent_name: 'search#1',
  status: 'running',
  source_definition: null,
  frozen_version: null,
  recent_actual_model: null,
  fallback_summary: null,
  ...over,
})

beforeEach(() => {
  setActivePinia(createPinia())
  vi.clearAllMocks()
  document.body.innerHTML = ''
  mocks.listApprovals.mockResolvedValue([])
  mocks.listPermissionRequests.mockResolvedValue([])
  mocks.listUserToolActions.mockResolvedValue([])
  mocks.listRuntimeInstances.mockResolvedValue([instance()])
  mocks.getAgentRuntimePolicy.mockResolvedValue({
    revision: 1,
    content_hash: 'abc',
    loaded_at: '2026-01-01T00:00:00Z',
    source_path: 'runtime.toml',
    document_id: 'runtime',
    diagnostic: { state: 'ready', detail: '', checked_at: '2026-01-01T00:00:00Z' },
    defaults_available: true,
    deviations: [],
    spawn: { max_depth: 2, max_agents_per_run: 16, max_parallel_workers: 4 },
    scheduling: { max_active_runs_per_session: 4, worker_retry_limit: 2, preserve_partial_results: true },
    supervision: { required_before_final: true, max_revision_rounds: 2 },
    context: { default_token_budget: 131072, task_token_budget: 0, effective_task_token_budget: 0, run_token_budget: 0, recent_message_limit: 128 },
    tools: { max_tool_calls: 500, max_tool_rounds: 0, default_timeout_seconds: 600 },
  })
})

function mountPanel(props: Record<string, unknown> = {}): VueWrapper {
  return mount(RuntimeInstancesPanel, { props, global })
}

function actionButton(wrapper: VueWrapper, testId: string) {
  return wrapper.get(`[data-testid="${testId}"]`)
}

describe('RuntimeInstancesPanel — control gating', () => {
  it('disables every control for a terminal run', async () => {
    mocks.listRuntimeInstances.mockResolvedValue([instance({ status: 'completed' })])
    const wrapper = mountPanel()
    await flushPromises()

    for (const id of ['instance-pause', 'instance-resume', 'instance-cancel', 'instance-steer']) {
      expect(actionButton(wrapper, id).attributes('disabled'), `${id} must be disabled`).toBeDefined()
    }
    wrapper.unmount()
  })

  it('offers Resume and Cancel but not Pause on a paused run', async () => {
    mocks.listRuntimeInstances.mockResolvedValue([instance({ status: 'paused' })])
    const wrapper = mountPanel()
    await flushPromises()

    expect(actionButton(wrapper, 'instance-pause').attributes('disabled')).toBeDefined()
    expect(actionButton(wrapper, 'instance-resume').attributes('disabled')).toBeUndefined()
    expect(actionButton(wrapper, 'instance-cancel').attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })

  it('offers Pause and Cancel but not Resume on a running run', async () => {
    const wrapper = mountPanel()
    await flushPromises()

    expect(actionButton(wrapper, 'instance-pause').attributes('disabled')).toBeUndefined()
    expect(actionButton(wrapper, 'instance-resume').attributes('disabled')).toBeDefined()
    expect(actionButton(wrapper, 'instance-cancel').attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })

  it('treats every terminal spelling Core uses as terminal', async () => {
    for (const status of ['completed', 'failed', 'cancelled', 'canceled', 'released', 'stopped', 'COMPLETED']) {
      mocks.listRuntimeInstances.mockResolvedValue([instance({ status })])
      const wrapper = mountPanel()
      await flushPromises()
      expect(actionButton(wrapper, 'instance-cancel').attributes('disabled'), `${status} must be terminal`).toBeDefined()
      wrapper.unmount()
    }
  })
})

describe('RuntimeInstancesPanel — initial session filter', () => {
  it('prefills the session filter from the prop so a deep link lands filtered', async () => {
    mocks.listRuntimeInstances.mockResolvedValue([
      instance({ id: 'i-1', run_id: 'run-1', session_id: 'sess-keep' }),
      instance({ id: 'i-2', run_id: 'run-2', session_id: 'sess-other' }),
    ])
    const wrapper = mountPanel({ initialSessionId: 'sess-keep' })
    await flushPromises()

    const rows = wrapper.findAll('.ac-instance-row')
    expect(rows).toHaveLength(1)
    expect(rows[0].text()).toContain('run-1'.slice(0, 8))
    wrapper.unmount()
  })
})

describe('GovernanceBoardPage — runtime tab', () => {
  it('mounts the runtime panel on load, without waiting for a tab click', async () => {
    const wrapper = mount(GovernanceBoardPage, { global })
    await flushPromises()

    // The section exists in the DOM from first render (hidden via v-show, not absent via v-if).
    expect(wrapper.find('[data-testid="governance-runtime-section"]').exists()).toBe(true)
    // And it has already asked Core for instances.
    expect(mocks.listRuntimeInstances).toHaveBeenCalled()
    wrapper.unmount()
  })

  it('opens the runtime tab and prefilters the session when the route names one', async () => {
    window.location.hash = '#/governance?session_id=sess-route'
    try {
      const wrapper = mount(GovernanceBoardPage, { global })
      await flushPromises()

      expect(wrapper.get('[data-testid="governance-tab-runtime"]').attributes('aria-selected')).toBe('true')
      const panel = wrapper.getComponent(RuntimeInstancesPanel)
      expect(panel.props('initialSessionId')).toBe('sess-route')
      wrapper.unmount()
    } finally {
      window.location.hash = ''
    }
  })

  it('labels the decisions tab from the locale, not a hardcoded string', async () => {
    const wrapper = mount(GovernanceBoardPage, { global })
    await flushPromises()

    const tabs = wrapper.get('[data-testid="governance-board-tabs"]')
    expect(tabs.text()).toContain(en.governance.tabDecisions)
    // A dotted path here means the key is missing from the bundle.
    expect(tabs.text()).not.toContain('governance.tabDecisions')
    wrapper.unmount()
  })

  it('polls only while the document is visible', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = mount(GovernanceBoardPage, { global })
      await vi.advanceTimersByTimeAsync(0)
      const initial = mocks.listApprovals.mock.calls.length
      const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
      await vi.advanceTimersByTimeAsync(36_000)
      expect(mocks.listApprovals.mock.calls.length).toBe(initial)
      visibility.mockRestore()
      await vi.advanceTimersByTimeAsync(12_000)
      expect(mocks.listApprovals.mock.calls.length).toBeGreaterThan(initial)
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })
})
