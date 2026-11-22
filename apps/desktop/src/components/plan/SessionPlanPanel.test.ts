// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import type { SessionPlanDto } from './useSessionPlan'

const { getSessionPlan, approveSessionPlan, patchSessionPlan } = vi.hoisted(() => ({
  getSessionPlan: vi.fn(),
  approveSessionPlan: vi.fn(),
  patchSessionPlan: vi.fn(),
}))

vi.mock('@/api', () => ({
  api: {
    getSessionPlan,
    approveSessionPlan,
    patchSessionPlan,
  },
}))

vi.mock('@/lib/sessionEventBus', () => ({
  subscribeToSessionEvents: vi.fn(() => () => {}),
}))

// UI primitives are Vapor components; plain vdom stubs keep the panel mountable in happy-dom.
vi.mock('@/components/ui', () => ({
  UiBadge: { template: '<span class="ui-badge"><slot /></span>' },
  UiButton: { template: '<button type="button"><slot /></button>' },
  UiInput: {
    props: ['modelValue', 'placeholder'],
    emits: ['update:modelValue'],
    template: '<input :value="modelValue" :placeholder="placeholder" @input="$emit(\'update:modelValue\', $event.target.value)" />',
  },
  UiSelectField: {
    props: ['modelValue', 'options'],
    emits: ['update:modelValue'],
    template: '<select :value="modelValue" @change="$emit(\'update:modelValue\', $event.target.value)"><option v-for="o in options" :key="o.value" :value="o.value">{{ o.label }}</option></select>',
  },
}))

const { default: SessionPlanPanel } = await import('./SessionPlanPanel.vue')

function makePlan(overrides: Partial<SessionPlanDto> = {}): SessionPlanDto {
  return {
    id: 'plan-1',
    session_id: 'session-1',
    title: 'Refactor Storage Layer',
    goal: 'Migrate SQLite indexes cleanly',
    status: 'draft',
    revision: 3,
    updated_at: '2026-11-22T06:00:00Z',
    phases: [
      {
        id: 'phase-1',
        title: 'Phase 1: Analysis',
        order: 1,
        status: 'in_progress',
        steps: [
          {
            id: 'step-1',
            title: 'Audit existing schema',
            status: 'completed',
            depends_on: [],
            assigned_agent_slug: 'architect',
            bound_task_key: 'task-audit',
            verification_criteria: ['All tables mapped'],
            artifacts: [{ kind: 'file', label: 'schema.sql', path: 'db/schema.sql' }],
          },
          {
            id: 'step-2',
            title: 'Apply migration script',
            status: 'pending',
            depends_on: ['step-1'],
            verification_criteria: ['Migration runs cleanly'],
            artifacts: [],
          },
        ],
      },
    ],
    progress: {
      total_steps: 2,
      completed_steps: 1,
      in_progress_steps: 0,
      blocked_steps: 0,
      failed_steps: 0,
      pending_steps: 1,
    },
    ...overrides,
  }
}

function mountPanel(sessionId = 'session-1') {
  const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })
  return mount(SessionPlanPanel, {
    props: { sessionId },
    global: { plugins: [i18n] },
  })
}

describe('SessionPlanPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders phases, progress, dependencies, verification criteria, artifacts, and focuses orchestration nodes', async () => {
    getSessionPlan.mockResolvedValue(makePlan())
    const dispatchSpy = vi.spyOn(window, 'dispatchEvent')

    const wrapper = mountPanel()
    await flushPromises()

    expect(wrapper.get('[data-testid="session-plan-panel"]').text()).toContain('Refactor Storage Layer')
    expect(wrapper.get('[data-testid="session-plan-progress"]').text()).toContain('1 / 2')
    expect(wrapper.get('[data-testid="session-plan-step-step-1"]').text()).toContain('schema.sql')
    expect(wrapper.get('[data-testid="session-plan-step-step-2"]').text()).toContain('step-1')

    await wrapper.find('.session-plan-chip--action').trigger('click')
    expect(wrapper.emitted('focus-node')?.[0]?.[0]).toEqual({
      taskKey: 'task-audit',
      agentSlug: 'architect',
    })
    expect(dispatchSpy).toHaveBeenCalled()
    dispatchSpy.mockRestore()
  })

  it('approves a draft plan with expected_revision and supports adding a step', async () => {
    const draft = makePlan({ status: 'draft', revision: 4 })
    const approved = makePlan({ status: 'approved', revision: 5 })
    getSessionPlan.mockResolvedValue(draft)
    approveSessionPlan.mockResolvedValue(approved)
    patchSessionPlan.mockResolvedValue({
      ...approved,
      revision: 6,
      phases: [
        {
          ...approved.phases[0],
          steps: [
            ...approved.phases[0].steps,
            {
              id: 'step-3',
              title: 'Run smoke tests',
              status: 'pending',
              depends_on: [],
              verification_criteria: [],
              artifacts: [],
            },
          ],
        },
      ],
    })

    const wrapper = mountPanel()
    await flushPromises()

    await wrapper.get('[data-testid="session-plan-approve"]').trigger('click')
    await flushPromises()
    expect(approveSessionPlan).toHaveBeenCalledWith('session-1', { expected_revision: 4 })
    expect(wrapper.find('[data-testid="session-plan-approve"]').exists()).toBe(false)

    await wrapper.get('[data-testid="session-plan-add-toggle"]').trigger('click')
    await wrapper.get('[data-testid="session-plan-new-step-title"] input, input[data-testid="session-plan-new-step-title"]').setValue('Run smoke tests')
    await wrapper.get('[data-testid="session-plan-add-form"]').trigger('submit')
    await flushPromises()

    expect(patchSessionPlan).toHaveBeenCalledWith('session-1', {
      add_steps: [{ title: 'Run smoke tests', phase_id: 'phase-1', assigned_agent_slug: undefined }],
      expected_revision: 5,
    })
    expect(wrapper.text()).toContain('Run smoke tests')
  })
})
