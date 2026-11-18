// @vitest-environment happy-dom
import type { Ref } from 'vue'
import { createI18n } from 'vue-i18n'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import zh from '@/locales/zh-CN'
import type { CompanionActivity } from '@/lib/companionApi'
import type { OrchestrationRunDto, SessionDto } from '@/api'

const api = vi.hoisted(() => ({
  listSessions: vi.fn(async (): Promise<SessionDto[]> => []),
  listRuns: vi.fn(async (_sessionId: string): Promise<OrchestrationRunDto[]> => []),
  listApprovals: vi.fn(async (_sessionId?: string, _status?: string): Promise<unknown[]> => []),
}))
vi.mock('@/api', () => ({ api }))

const partnerState = vi.hoisted(() => ({ current: null as Ref<Set<string>> | null }))
vi.mock('@/lib/sessionPartner', async () => {
  const { ref } = await import('vue')
  partnerState.current = ref(new Set<string>())
  return { activeSessionPartners: partnerState.current }
})

const activityState = vi.hoisted(() => ({ current: null as Ref<CompanionActivity[]> | null }))
vi.mock('@/lib/companionStore', async () => {
  const { ref } = await import('vue')
  activityState.current = ref<CompanionActivity[]>([])
  return { activity: activityState.current }
})

vi.mock('@/lib/hostAccess', async () => {
  const { ref } = await import('vue')
  return { useHostAccess: () => ({ canAccessBackend: ref(true) }) }
})

import CompanionSupervisionCard from '../../../TinadecUI/src/components/cards/companion/CompanionSupervisionCard.vue'

function session(id: string, title: string): SessionDto {
  return {
    id,
    title,
    project_id: null,
    status: 'active',
    permission_mode: 'ask',
    space_options: null,
    settings_revision: 0,
    created_at: '',
    updated_at: '',
  }
}

function run(id: string, sessionId: string, status: string): OrchestrationRunDto {
  return { id, session_id: sessionId, status, summary: '', created_at: '', updated_at: '' }
}

function dispatch(id: string, sessionId: string, runId: string, title: string): CompanionActivity {
  return {
    id,
    kind: 'companion_dispatched',
    companion_id: 'c-a',
    companion_name: 'Alpha',
    created_at: '2026-11-08T10:00:00Z',
    payload: { session_id: sessionId, run_id: runId, session_title: title, task_preview: 'do the thing' },
  }
}

function mountCard() {
  const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })
  return mount(CompanionSupervisionCard, { global: { plugins: [i18n] } })
}

enableAutoUnmount(afterEach)

beforeEach(() => {
  api.listSessions.mockReset().mockResolvedValue([])
  api.listRuns.mockReset().mockResolvedValue([])
  api.listApprovals.mockReset().mockResolvedValue([])
  partnerState.current!.value = new Set()
  activityState.current!.value = []
})

afterEach(() => {
  vi.useRealTimers()
  vi.clearAllMocks()
})

describe('companion supervision board', () => {
  it('lists a session partner window with its live title, state and partner tag', async () => {
    api.listSessions.mockResolvedValue([session('s1', 'Fix the parser')])
    api.listRuns.mockResolvedValue([run('r1', 's1', 'executing')])
    partnerState.current!.value = new Set(['s1'])

    const wrapper = mountCard()
    await flushPromises()

    const row = wrapper.get('[data-testid="supervision-s1"]')
    expect(row.text()).toContain('Fix the parser')
    expect(row.text()).toContain(zh.companion.center.supervisionWorking)
    expect(row.attributes('data-sources')).toBe('partner')
    expect(row.text()).toContain(zh.companion.center.supervisionSourcePartner)
    expect(row.text()).not.toContain(zh.companion.center.supervisionSourceDispatch)
  })

  it('lists a session a companion dispatched to and says where the row came from', async () => {
    api.listSessions.mockResolvedValue([session('s2', 'Release notes')])
    activityState.current!.value = [dispatch('a1', 's2', 'r9', 'Release notes')]

    const wrapper = mountCard()
    await flushPromises()

    const row = wrapper.get('[data-testid="supervision-s2"]')
    expect(row.text()).toContain('Release notes')
    expect(row.attributes('data-sources')).toBe('dispatch')
    expect(row.text()).toContain(zh.companion.center.supervisionSourceDispatch)
    expect(row.text()).not.toContain(zh.companion.center.supervisionSourcePartner)
  })

  it('reacts to a new companion dispatch without waiting for the status poll', async () => {
    const wrapper = mountCard()
    await flushPromises()
    expect(wrapper.find('.companion-supervision-row').exists()).toBe(false)

    api.listSessions.mockResolvedValue([session('s3', 'Nightly build')])
    activityState.current!.value = [dispatch('a2', 's3', 'r3', 'Nightly build')]
    await flushPromises()

    expect(wrapper.get('[data-testid="supervision-s3"]').text()).toContain('Nightly build')
  })

  it('shows one row with both source tags when the two sources name the same session', async () => {
    api.listSessions.mockResolvedValue([session('s1', 'Shared session')])
    partnerState.current!.value = new Set(['s1'])
    activityState.current!.value = [dispatch('a1', 's1', 'r1', 'Shared session')]

    const wrapper = mountCard()
    await flushPromises()

    const rows = wrapper.findAll('.companion-supervision-row')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.attributes('data-sources')).toBe('partner dispatch')
    expect(rows[0]!.text()).toContain(zh.companion.center.supervisionSourcePartner)
    expect(rows[0]!.text()).toContain(zh.companion.center.supervisionSourceDispatch)
  })

  it('never invents a title for a session that no longer exists', async () => {
    api.listSessions.mockResolvedValue([])
    activityState.current!.value = [dispatch('a1', '12345678-aaaa-bbbb', 'r1', 'Title recorded at dispatch time')]

    const wrapper = mountCard()
    await flushPromises()

    const row = wrapper.get('[data-testid="supervision-12345678-aaaa-bbbb"]')
    expect(row.text()).toContain('12345678')
    expect(row.text()).toContain(zh.companion.center.supervisionUnknownSession)
    expect(row.text()).not.toContain('Title recorded at dispatch time')
  })

  it('routes attention in order: approval, working, done, idle', async () => {
    api.listSessions.mockResolvedValue([
      session('s-approve', 'Approve me'),
      session('s-work', 'Working'),
      session('s-done', 'Done'),
      session('s-idle', 'Idle'),
    ])
    partnerState.current!.value = new Set(['s-approve', 's-work', 's-done', 's-idle'])
    api.listApprovals.mockImplementation(async (sessionId?: string) => (sessionId === 's-approve' ? [{}] : []))
    api.listRuns.mockImplementation(async (sessionId: string) => {
      if (sessionId === 's-work') return [run('r1', sessionId, 'executing')]
      if (sessionId === 's-done') return [run('r2', sessionId, 'completed')]
      return []
    })

    const wrapper = mountCard()
    await flushPromises()

    const ids = wrapper.findAll('.companion-supervision-row').map((row) => row.attributes('data-testid'))
    expect(ids).toEqual([
      'supervision-s-approve',
      'supervision-s-work',
      'supervision-s-done',
      'supervision-s-idle',
    ])
    expect(wrapper.get('[data-testid="supervision-s-approve"]').text()).toContain(zh.companion.center.supervisionNeedsApproval)
    expect(wrapper.get('[data-testid="supervision-s-work"]').text()).toContain(zh.companion.center.supervisionWorking)
    expect(wrapper.get('[data-testid="supervision-s-done"]').text()).toContain(zh.companion.center.supervisionDone)
    expect(wrapper.get('[data-testid="supervision-s-idle"]').text()).toContain(zh.companion.center.supervisionIdle)
  })

  it('keeps the 10s run/approval refresh for runtime state', async () => {
    vi.useFakeTimers()
    api.listSessions.mockResolvedValue([session('s1', 'Polled')])
    partnerState.current!.value = new Set(['s1'])

    mountCard()
    await vi.advanceTimersByTimeAsync(0)
    expect(api.listRuns).toHaveBeenCalledTimes(1)

    api.listRuns.mockClear()
    await vi.advanceTimersByTimeAsync(10_000)
    expect(api.listRuns).toHaveBeenCalledTimes(1)
  })
})
