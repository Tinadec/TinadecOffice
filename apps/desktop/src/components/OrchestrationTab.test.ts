// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import OrchestrationTab from './OrchestrationTab.vue'
import type { ContextPackDto, OrchestrationSnapshotDto } from '../api'

/**
 * The child panels are not what these cases are about, and two of them reach for stores.
 */
const stubs = {
  DeclaredGraphCanvas: true,
  ToolExecutionTimeline: true,
  ToolCatalogBrowser: true,
  ToolStatsDashboard: true,
}

function pack(over: Partial<ContextPackDto> = {}): ContextPackDto {
  return {
    id: 'ctx-1',
    run_id: 'run-1',
    evidence_count: 2,
    estimated_tokens: 3120,
    token_budget: 8192,
    sources: ['workspace_instructions', 'session_history'],
    created_at: '2026-09-22T00:00:00Z',
    ...over,
  }
}

function snapshot(contextPacks: ContextPackDto[]): OrchestrationSnapshotDto {
  return {
    // The detail panel behind which the packs are listed is gated on a run, not on the snapshot
    // object, so a fixture without one renders the empty state and proves nothing.
    run: {
      id: 'run-1',
      session_id: 'session-1',
      status: 'completed',
      summary: 'Planned two tasks.',
      created_at: '2026-09-22T00:00:00Z',
      updated_at: '2026-09-22T00:01:00Z',
    },
    nodes: [],
    lanes: [],
    assignments: [],
    step_results: [],
    context_packs: contextPacks,
    supervision_findings: [],
  }
}

function mountTab(contextPacks: ContextPackDto[]) {
  return mount(OrchestrationTab, {
    props: { snapshot: snapshot(contextPacks), toolExecutions: [], tools: [] },
    global: { stubs },
  })
}

describe('OrchestrationTab context packs', () => {
  it('names the evidence a pack carried, which is the only way to see what was left out', () => {
    const wrapper = mountTab([pack()])
    const row = wrapper.get('[data-testid="context-pack-row"]')

    expect(row.text()).toContain('workspace_instructions')
    expect(row.text()).toContain('session_history')
    expect(row.text()).toContain('3120 / 8192')
  })

  it('prints no NaN when the wire has nothing like a compression ratio', () => {
    // The projection in Core emits evidence count, tokens and budget. A ratio the server never
    // produced rendered as `Math.round(undefined * 100)` — every row of this panel said "NaN%".
    const wrapper = mountTab([pack()])

    expect(wrapper.get('[data-testid="context-pack-row"]').text()).toContain('2 evidence')
    expect(wrapper.text()).not.toContain('NaN')
  })

  it('says the source list is unknown for an older event rather than claiming the pack was empty', () => {
    const wrapper = mountTab([pack({ sources: [] })])
    const row = wrapper.get('[data-testid="context-pack-row"]')

    expect(row.text()).toContain('unknown here rather than empty')
    expect(row.find('[data-testid="context-pack-sources"]').exists()).toBe(false)
  })

  it('labels a pack by the one field that separates them, because the wire carries no summary', () => {
    const laneRow = mountTab([pack({ lane_key: 'implementation' })]).get('[data-testid="context-pack-row"]')
    expect(laneRow.text()).toContain("Lane 'implementation' pack")

    const plannerRow = mountTab([pack()]).get('[data-testid="context-pack-row"]')
    expect(plannerRow.text()).toContain('Planner pack')
    expect(plannerRow.text()).not.toContain('Lane')
  })
})
