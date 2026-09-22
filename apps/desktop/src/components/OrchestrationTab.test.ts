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

  it('prices each source and collapses the ones that contributed several items', () => {
    const row = mountTab([pack({
      sources: ['workspace_instructions', 'session_history', 'reviewed_memory', 'reviewed_memory'],
      source_tokens: [
        { source: 'workspace_instructions', tokens: 900 },
        { source: 'session_history', tokens: 40 },
        { source: 'reviewed_memory', tokens: 7 },
        { source: 'reviewed_memory', tokens: 5 },
      ],
    })]).get('[data-testid="context-pack-row"]')

    const sources = row.findAll('[data-testid="context-pack-source"]').map((tag) => tag.text())
    expect(sources).toContain('workspace_instructions · 900')
    // Two items from one source must not render as two prices for the same name.
    expect(sources.filter((text) => text.startsWith('reviewed_memory'))).toEqual(['reviewed_memory · 12'])
    // The chips are a list, not a run of adjacent text: without the roles a screen reader reads
    // "workspace_instructions · 900session_history · 40" as one word, because the markup has no
    // whitespace between spans (the gap is CSS). Same convention as the composer's attachment strip.
    const list = row.get('[data-testid="context-pack-sources"]')
    expect(list.attributes('role')).toBe('list')
    expect(list.findAll('[role="listitem"]')).toHaveLength(3)
  })

  it('shows what the budget crowded out, because a missing name in the pack is not a missing fact', () => {
    const row = mountTab([pack({
      source_tokens: [{ source: 'workspace_instructions', tokens: 900 }],
      dropped_sources: [{ source: 'workspace_skills', tokens: 2100 }],
    })]).get('[data-testid="context-pack-row"]')

    const dropped = row.findAll('[data-testid="context-pack-dropped-source"]')
    expect(dropped).toHaveLength(1)
    expect(dropped[0].text()).toBe('workspace_skills (2100 tokens)')
    expect(row.text()).toContain('the model was not told them')
  })

  it('invents no price and no cut for an event written before either was recorded', () => {
    const row = mountTab([pack()]).get('[data-testid="context-pack-row"]')

    // A bare name is the honest rendering of "no price recorded"; "· 0" would claim
    // the source was measured free.
    expect(row.get('[data-testid="context-pack-source"]').text()).toBe('workspace_instructions')
    expect(row.find('[data-testid="context-pack-dropped"]').exists()).toBe(false)
    expect(row.text()).not.toContain('undefined')
    expect(row.text()).not.toContain('NaN')
  })

  it('labels a pack by the one field that separates them, because the wire carries no summary', () => {
    const laneRow = mountTab([pack({ lane_key: 'implementation' })]).get('[data-testid="context-pack-row"]')
    expect(laneRow.text()).toContain("Lane 'implementation' pack")

    const plannerRow = mountTab([pack()]).get('[data-testid="context-pack-row"]')
    expect(plannerRow.text()).toContain('Planner pack')
    expect(plannerRow.text()).not.toContain('Lane')
  })
})
