// @vitest-environment happy-dom
import { beforeEach, describe, expect, it } from 'vitest'
import { ref } from 'vue'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import zhCN from '@/locales/zh-CN'
import ObservabilityTurnBar from '@/components/observability/ObservabilityTurnBar.vue'

function makeI18n() {
  return createI18n({
    legacy: false,
    locale: 'zh-CN',
    messages: { 'zh-CN': zhCN as never },
  })
}
import {
  __resetObservabilityRunSelectionForTests,
  useObservabilityRunSelection,
} from './useObservabilityRunSelection'
import {
  activeDockFocus,
  focusDockCard,
  resetDockSyncForTests,
} from '@/lib/homeDockSync'

describe('useObservabilityRunSelection', () => {
  beforeEach(() => {
    __resetObservabilityRunSelectionForTests()
    resetDockSyncForTests()
  })

  it('orders turns chronologically (1..N), computes durations, and places optimistic active runs at the tail', () => {
    const sessionId = ref<string | null>('sess-chrono')
    const runs = ref<unknown[]>([
      // Optimistic admission prepended by HomeController before listRuns fills timestamps
      { id: 'run-live', status: 'planning' },
      // API listRuns returned out-of-order timestamps
      {
        id: 'run-2',
        status: 'failed',
        created_at: '2026-11-25T10:02:00.000Z',
        updated_at: '2026-11-25T10:02:14.200Z',
        summary: 'Verify tests',
      },
      {
        id: 'run-1',
        status: 'completed',
        created_at: '2026-11-25T10:00:00.000Z',
        updated_at: '2026-11-25T10:00:04.500Z',
        summary: 'Initial scan',
      },
    ])

    const sel = useObservabilityRunSelection({
      sessionId: () => sessionId.value,
      runs: () => runs.value,
      activeRunId: () => 'run-live',
    })

    expect(sel.turns.value.map((t) => ({ runId: t.runId, turnNo: t.turnNo }))).toEqual([
      { runId: 'run-1', turnNo: 1 },
      { runId: 'run-2', turnNo: 2 },
      { runId: 'run-live', turnNo: 3 },
    ])
    expect(sel.turns.value[0]?.durationMs).toBe(4500)
    expect(sel.turns.value[0]?.isFailed).toBe(false)
    expect(sel.turns.value[0]?.isRunning).toBe(false)
    expect(sel.turns.value[1]?.durationMs).toBe(14200)
    expect(sel.turns.value[1]?.isFailed).toBe(true)
    expect(sel.turns.value[2]?.isRunning).toBe(true)

    expect(sel.isFollowingLatest.value).toBe(true)
    expect(sel.effectiveRunId.value).toBe('run-live')
    expect(sel.selectedTurn.value?.turnNo).toBe(3)
  })

  it('synchronizes historical turn selection across LiveDigest, Trajectory, and Orchestration instances while isolating sessions', () => {
    const runsA = ref([
      { id: 'r1', status: 'completed', created_at: '2026-11-25T09:00:00Z' },
      { id: 'r2', status: 'completed', created_at: '2026-11-25T09:05:00Z' },
      { id: 'r3', status: 'running', created_at: '2026-11-25T09:10:00Z' },
    ])
    const runsB = ref([
      { id: 'b1', status: 'completed', created_at: '2026-11-25T08:00:00Z' },
      { id: 'b2', status: 'completed', created_at: '2026-11-25T08:03:00Z' },
    ])

    // LiveDigest passes storage-scoped session key while Trajectory/Orchestration pass bare sessionId
    const digestSel = useObservabilityRunSelection({
      sessionId: () => 'user::sess-shared',
      runs: () => runsA.value,
      activeRunId: () => 'r3',
    })
    const trajectorySel = useObservabilityRunSelection({
      sessionId: () => 'sess-shared',
      runs: () => runsA.value,
      activeRunId: () => 'r3',
    })
    const otherSessionSel = useObservabilityRunSelection({
      sessionId: () => 'sess-other',
      runs: () => runsB.value,
    })

    // Switch turn in LiveDigest -> Trajectory immediately sees the same historical turn
    digestSel.selectTurnByNo(1)
    expect(digestSel.isFollowingLatest.value).toBe(false)
    expect(digestSel.pinnedRunId.value).toBe('r1')
    expect(trajectorySel.isFollowingLatest.value).toBe(false)
    expect(trajectorySel.effectiveRunId.value).toBe('r1')
    expect(trajectorySel.selectedTurn.value?.turnNo).toBe(1)

    // Other session remains untouched and following its own latest turn
    expect(otherSessionSel.isFollowingLatest.value).toBe(true)
    expect(otherSessionSel.effectiveRunId.value).toBe('b2')

    // Step forward in Trajectory -> LiveDigest steps to #2
    trajectorySel.stepTurn(1)
    expect(digestSel.effectiveRunId.value).toBe('r2')
    expect(digestSel.selectedTurn.value?.turnNo).toBe(2)

    // Boundary step clamp at #1
    trajectorySel.selectTurnByNo(1)
    trajectorySel.stepTurn(-1)
    expect(trajectorySel.effectiveRunId.value).toBe('r1')

    // Follow latest restores live run tracking on both
    trajectorySel.followLatest()
    expect(digestSel.isFollowingLatest.value).toBe(true)
    expect(trajectorySel.isFollowingLatest.value).toBe(true)
    expect(digestSel.effectiveRunId.value).toBe('r3')
    expect(trajectorySel.effectiveRunId.value).toBe('r3')
  })
})

describe('ObservabilityTurnBar & homeDockSync trajectory contract', () => {
  beforeEach(() => {
    resetDockSyncForTests()
  })

  it('renders flat compact pills when turns <= 4 and shows follow-latest button when pinned', async () => {
    const turns = [
      {
        runId: 'r1',
        turnNo: 1,
        status: 'completed',
        isRunning: false,
        isFailed: false,
        durationMs: 3200,
        startedAt: null,
        completedAt: null,
      },
      {
        runId: 'r2',
        turnNo: 2,
        status: 'running',
        isRunning: true,
        isFailed: false,
        durationMs: null,
        startedAt: null,
        completedAt: null,
      },
    ]

    const wrapper = mount(ObservabilityTurnBar, {
      global: { plugins: [makeI18n()] },
      props: {
        turns,
        effectiveRunId: 'r1',
        isFollowingLatest: false,
        filterLabel: 'Node: worker-1',
      },
    })

    const pills = wrapper.findAll('[data-testid="observability-turn-pill"]')
    expect(pills).toHaveLength(2)
    expect(pills[0]!.text()).toContain('#1')
    expect(pills[0]!.text()).toContain('✓')
    expect(pills[1]!.text()).toContain('#2')
    expect(pills[1]!.text()).toContain('●')
    expect(wrapper.find('[data-testid="observability-turn-select"]').exists()).toBe(false)

    await pills[1]!.trigger('click')
    expect(wrapper.emitted('select-run')?.[0]).toEqual(['r2'])

    const followBtn = wrapper.find('[data-testid="observability-follow-latest"]')
    expect(followBtn.exists()).toBe(true)
    await followBtn.trigger('click')
    expect(wrapper.emitted('follow-latest')).toHaveLength(1)

    const filterChip = wrapper.find('[data-testid="observability-filter-chip"]')
    expect(filterChip.exists()).toBe(true)
    await filterChip.trigger('click')
    expect(wrapper.emitted('clear-filter')).toHaveLength(1)
  })

  it('switches to compact dropdown + steppers when turns > 4', async () => {
    const turns = Array.from({ length: 5 }, (_, idx) => ({
      runId: `r${idx + 1}`,
      turnNo: idx + 1,
      status: idx === 4 ? 'running' : 'completed',
      isRunning: idx === 4,
      isFailed: false,
      durationMs: idx === 2 ? 14200 : 2100,
      startedAt: null,
      completedAt: null,
    }))

    const wrapper = mount(ObservabilityTurnBar, {
      global: { plugins: [makeI18n()] },
      props: {
        turns,
        effectiveRunId: 'r3',
        isFollowingLatest: false,
      },
    })

    expect(wrapper.findAll('[data-testid="observability-turn-pill"]')).toHaveLength(0)
    const select = wrapper.find('[data-testid="observability-turn-select"]')
    expect(select.exists()).toBe(true)
    expect(select.text()).toContain('#3 · 14.2s ✓')

    await select.setValue('r2')
    expect(wrapper.emitted('select-run')?.[0]).toEqual(['r2'])

    await wrapper.find('[data-testid="observability-turn-next"]').trigger('click')
    expect(wrapper.emitted('select-run')?.[1]).toEqual(['r4'])
  })

  it('dispatches trajectory deep-link fields via focusDockCard', () => {
    const events: CustomEvent[] = []
    const listener = (e: Event) => events.push(e as CustomEvent)
    window.addEventListener('tinadec:focus-trajectory', listener)

    try {
      const target = focusDockCard({
        card: 'trajectory',
        reason: 'trajectory',
        runId: 'run-42',
        trajectoryTab: 'context',
        trajectoryTurnNo: 2,
        trajectoryNodeId: 'worker-auth',
        trajectoryCategory: 'tools',
      })

      expect(activeDockFocus.value).toMatchObject({
        card: 'trajectory',
        reason: 'trajectory',
        runId: 'run-42',
        trajectoryTab: 'context',
        trajectoryTurnNo: 2,
        trajectoryNodeId: 'worker-auth',
        trajectoryCategory: 'tools',
        nonce: target.nonce,
      })
      expect(events).toHaveLength(1)
      expect(events[0]?.detail).toMatchObject({
        runId: 'run-42',
        trajectoryTab: 'context',
        trajectoryTurnNo: 2,
        trajectoryNodeId: 'worker-auth',
        trajectoryCategory: 'tools',
      })
    } finally {
      window.removeEventListener('tinadec:focus-trajectory', listener)
    }
  })
})
