// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import zhCN from '@/locales/zh-CN'

const apiMocks = vi.hoisted(() => ({
  listRunTeammates: vi.fn(),
  steerRunTeammate: vi.fn(),
  pauseRunTeammate: vi.fn(),
  resumeRunTeammate: vi.fn(),
}))

vi.mock('@/lib/teammateControlApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/teammateControlApi')>()
  return {
    ...actual,
    listRunTeammates: apiMocks.listRunTeammates,
    steerRunTeammate: apiMocks.steerRunTeammate,
    pauseRunTeammate: apiMocks.pauseRunTeammate,
    resumeRunTeammate: apiMocks.resumeRunTeammate,
  }
})

import TeammateSteerPopover from './TeammateSteerPopover.vue'
import AgentTimeline from '../timeline/AgentTimeline.vue'
import OrchestrationMiniGraph from '../OrchestrationMiniGraph.vue'
import type { OrchestrationSnapshotDto, TopologyRunDto } from '@/api'

const wrappers: VueWrapper[] = []

function buildI18n() {
  return createI18n({
    legacy: false,
    locale: 'en',
    messages: { en, 'zh-CN': zhCN },
  })
}

function mountPopover(props: Record<string, unknown>) {
  const wrapper = mount(TeammateSteerPopover, {
    props: {
      open: true,
      runId: 'run-1',
      handle: 'impl#1',
      displayName: 'Backend Engineer',
      status: 'working',
      paused: false,
      taskSummary: 'Implement teammate control routes',
      teleportDisabled: true,
      ...props,
    },
    global: {
      plugins: [buildI18n()],
    },
  })
  wrappers.push(wrapper)
  return wrapper
}

beforeEach(() => {
  vi.clearAllMocks()
  apiMocks.listRunTeammates.mockResolvedValue([])
})

afterEach(() => {
  wrappers.splice(0).forEach((w) => w.unmount())
})

describe('TeammateSteerPopover', () => {
  it('renders identity, status badge, claimed task summary and hydrates live status on open', async () => {
    apiMocks.listRunTeammates.mockResolvedValueOnce([
      {
        handle: 'impl#1',
        status: 'working',
        paused: false,
        awaiting_approval: false,
        current_task_title: 'Verify atomic task claims',
        pending_steer_count: 2,
      },
    ])

    const wrapper = mountPopover({})
    await flushPromises()

    expect(apiMocks.listRunTeammates).toHaveBeenCalledWith('run-1', expect.objectContaining({ signal: expect.any(AbortSignal) }))
    expect(wrapper.get('[data-testid="teammate-steer-name"]').text()).toBe('Backend Engineer')
    expect(wrapper.get('[data-testid="teammate-steer-status"]').text()).toBe('Running')
    expect(wrapper.get('[data-testid="teammate-steer-task"]').text()).toBe('Verify atomic task claims')
    expect(wrapper.get('[data-testid="teammate-steer-pending"]').text()).toContain('2 queued')
  })

  it('sends direct steer on Enter, respects Shift+Enter newline, and surfaces delivery receipt', async () => {
    apiMocks.steerRunTeammate.mockResolvedValueOnce({
      accepted: true,
      run_id: 'run-1',
      handle: 'impl#1',
      status: 'working',
      paused: false,
      urgent: true,
      woken: true,
      delivery_mode: 'woken',
      board_seq: 9,
      pending_steer_count: 1,
    })

    const wrapper = mountPopover({ status: 'waiting' })
    await flushPromises()

    const textarea = wrapper.get('[data-testid="teammate-steer-input"]')
    await textarea.setValue('Keep existing public DTO fields intact.')

    // Shift+Enter should NOT send
    await textarea.trigger('keydown', { key: 'Enter', shiftKey: true })
    expect(apiMocks.steerRunTeammate).not.toHaveBeenCalled()

    // Enter without Shift sends immediately
    await textarea.trigger('keydown', { key: 'Enter', shiftKey: false })
    await flushPromises()

    expect(apiMocks.steerRunTeammate).toHaveBeenCalledWith(
      'run-1',
      'impl#1',
      { message: 'Keep existing public DTO fields intact.', urgent: true },
      { storageId: undefined },
    )
    expect((textarea.element as HTMLTextAreaElement).value).toBe('')
    expect(wrapper.get('[data-testid="teammate-steer-feedback"]').text()).toContain('woke sleeping teammate')
    expect(wrapper.emitted('steered')?.[0]?.[0]).toMatchObject({ delivery_mode: 'woken', woken: true })
    expect(wrapper.emitted('status-change')?.[0]?.[0]).toEqual({
      handle: 'impl#1',
      status: 'working',
      paused: false,
    })
  })

  it('pauses and resumes a single teammate independently and toggles button label', async () => {
    apiMocks.pauseRunTeammate.mockResolvedValueOnce({
      accepted: true,
      run_id: 'run-1',
      handle: 'impl#1',
      action: 'pause',
      status: 'paused',
      paused: true,
    })
    apiMocks.resumeRunTeammate.mockResolvedValueOnce({
      accepted: true,
      run_id: 'run-1',
      handle: 'impl#1',
      action: 'resume',
      status: 'working',
      paused: false,
    })

    const wrapper = mountPopover({ paused: false })
    await flushPromises()

    const pauseBtn = wrapper.get('[data-testid="teammate-steer-pause-btn"]')
    expect(pauseBtn.text()).toContain('Pause')

    await pauseBtn.trigger('click')
    await flushPromises()

    expect(apiMocks.pauseRunTeammate).toHaveBeenCalledWith('run-1', 'impl#1', { storageId: undefined })
    expect(wrapper.get('[data-testid="teammate-steer-status"]').text()).toBe('Paused')
    expect(pauseBtn.text()).toContain('Resume')
    expect(wrapper.emitted('paused')).toHaveLength(1)

    await pauseBtn.trigger('click')
    await flushPromises()

    expect(apiMocks.resumeRunTeammate).toHaveBeenCalledWith('run-1', 'impl#1', { storageId: undefined })
    expect(wrapper.get('[data-testid="teammate-steer-status"]').text()).toBe('Running')
    expect(pauseBtn.text()).toContain('Pause')
    expect(wrapper.emitted('resumed')).toHaveLength(1)
  })

  it('emits error feedback when steer fails and closes cleanly on Escape', async () => {
    apiMocks.steerRunTeammate.mockRejectedValueOnce(new Error('Teammate already terminated'))

    const wrapper = mountPopover({})
    await flushPromises()

    const textarea = wrapper.get('[data-testid="teammate-steer-input"]')
    await textarea.setValue('Check edge case')
    await wrapper.get('[data-testid="teammate-steer-send-btn"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="teammate-steer-feedback"]').text()).toContain('Teammate already terminated')
    expect(wrapper.emitted('error')?.[0]).toEqual(['Teammate already terminated'])

    await textarea.trigger('keydown', { key: 'Escape' })
    expect(wrapper.emitted('update:open')?.[0]).toEqual([false])
    expect(wrapper.emitted('close')).toHaveLength(1)
  })

  it('opens TeammateSteerPopover when clicking a teammate row in AgentTimeline or node in OrchestrationMiniGraph', async () => {
    const snapshot: OrchestrationSnapshotDto = {
      run: {
        id: 'run-team-1',
        session_id: 'sess-1',
        status: 'running',
        summary: 'Working in parallel',
        created_at: '2026-11-23T02:00:00Z',
        updated_at: '2026-11-23T02:01:00Z',
      },
      nodes: [],
      lanes: [],
      assignments: [],
      step_results: [],
      context_packs: [],
      supervision_findings: [],
    }
    const topologyRun: TopologyRunDto = {
      run_id: 'run-team-1',
      status: 'running',
      started_at: '2026-11-23T02:00:00Z',
      tasks_truncated: false,
      instances_truncated: false,
      instances: [
        { instance_id: 'i-coord', agent_slug: 'solo_master', layer: 'meeting', role: 'coordinator', status: 'running', depth: 0 },
        { instance_id: 'i-be', agent_slug: 'backend', layer: 'execution', role: 'worker', status: 'running', parent_instance_id: 'i-coord', task_id: 't-be', depth: 1 },
      ],
      tasks: [
        {
          task_id: 't-be',
          task_key: 'be',
          title: 'Build API routes',
          status: 'running',
          handle: 'backend-api',
          agent_slug: 'backend',
          worker_instance_id: 'i-be',
          dependencies: [],
          write_scope: [],
        },
      ],
    }

    const timeline = mount(AgentTimeline, {
      attachTo: document.body,
      props: { snapshot, topologyRun },
      global: { plugins: [buildI18n()] },
    })
    wrappers.push(timeline)

    const steerableRow = timeline.findAll('[data-testid="timeline-agent-row-label"]').find((row) =>
      row.attributes('data-steerable') === 'true',
    )!
    await steerableRow.trigger('click')
    await flushPromises()

    const popped = document.body.querySelector('[data-testid="teammate-steer-popover"]')
    expect(popped).not.toBeNull()
    expect(popped?.getAttribute('data-teammate-handle')).toBe('backend-api')
    timeline.unmount()

    const miniGraph = mount(OrchestrationMiniGraph, {
      attachTo: document.body,
      props: { snapshot, topologyRun },
      global: { plugins: [buildI18n()] },
    })
    wrappers.push(miniGraph)

    const workerNode = miniGraph.findAll('[data-testid="mini-topo-node"]').find((el) =>
      el.attributes('data-role-tier') !== 'coordinator',
    )
    if (workerNode) {
      await workerNode.trigger('click')
      await flushPromises()
      const graphPop = document.body.querySelector('[data-testid="teammate-steer-popover"]')
      expect(graphPop).not.toBeNull()
      expect(graphPop?.getAttribute('data-teammate-handle')).toBe('backend-api')
    }
  })
})
