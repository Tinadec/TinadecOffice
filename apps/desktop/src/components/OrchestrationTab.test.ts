// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import OrchestrationTab from './OrchestrationTab.vue'
import type { ContextPackDto, ModelInvocationDto, OrchestrationSnapshotDto } from '../api'

/**
 * The tab reads one thing itself: the run's model-invocation audit (the sibling tool panels fetch
 * too, which is this subtree's established idiom). Everything else stays props, so the stubs below
 * keep the store-dependent children out of these cases.
 */
const listModelInvocations = vi.fn()
vi.mock('../api', () => ({
  api: { listModelInvocations: (...args: unknown[]) => listModelInvocations(...args) },
}))

function invocation(over: Partial<ModelInvocationDto> = {}): ModelInvocationDto {
  return {
    id: 'inv-1',
    call_id: 'call-1',
    attempt: 1,
    session_id: 'session-1',
    run_id: 'run-1',
    agent_definition_id: 'agent-1',
    agent_version_id: 'version-1',
    mode_version_id: 'mode-1',
    strategy_source: 'route',
    provider_instance_id: 'prov-1',
    provider_version_id: 'provver-1',
    model: 'gpt-test',
    protocol: 'openai',
    fallback_position: 0,
    status: 'succeeded',
    started_at: '2026-09-22T00:00:00Z',
    input_tokens: 100,
    output_tokens: 40,
    total_tokens: 140,
    ...over,
  }
}

function emptyUsagePage() {
  return { items: [] as ModelInvocationDto[] }
}

afterEach(() => {
  listModelInvocations.mockReset()
  listModelInvocations.mockResolvedValue(emptyUsagePage())
})

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

describe('OrchestrationTab model usage', () => {
  async function mountLoaded() {
    const wrapper = mountTab([])
    await flushPromises()
    return wrapper
  }

  it('rolls the run audit up per model instead of listing every call', async () => {
    listModelInvocations.mockResolvedValue({
      items: [
        invocation({ id: 'a', input_tokens: 100, output_tokens: 40, total_tokens: 140 }),
        invocation({ id: 'b', input_tokens: 7, output_tokens: 3, total_tokens: 10 }),
      ],
    })

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    const groups = block.findAll('[data-testid="model-usage-group"]')
    expect(groups).toHaveLength(1)
    expect(groups[0]!.text()).toContain('gpt-test')
    expect(groups[0]!.text()).toContain('150 tokens')
    expect(groups[0]!.text()).toContain('2 calls')
    // Nothing in this run went unreported, so the panel must not claim that anything did.
    expect(block.find('[data-testid="model-usage-unpriced"]').exists()).toBe(false)
  })

  it('walks the cursor so a long run is not silently read as a short one', async () => {
    listModelInvocations
      .mockResolvedValueOnce({ items: [invocation({ id: 'a', total_tokens: 140, input_tokens: 100, output_tokens: 40 })], next_cursor: 'Y3Vyc29yLTI' })
      .mockResolvedValueOnce({ items: [invocation({ id: 'b', total_tokens: 60, input_tokens: 40, output_tokens: 20 })] })

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    expect(listModelInvocations).toHaveBeenCalledTimes(2)
    expect(listModelInvocations.mock.calls[1]![0]).toMatchObject({ run_id: 'run-1', cursor: 'Y3Vyc29yLTI', limit: 200 })
    expect(block.get('[data-testid="model-usage-group"]').text()).toContain('200 tokens')
    expect(block.find('[data-testid="model-usage-truncated"]').exists()).toBe(false)
  })

  it('says a call reported nothing instead of pricing it at zero', async () => {
    listModelInvocations.mockResolvedValue({
      items: [
        invocation({ id: 'paid', total_tokens: 140, input_tokens: 100, output_tokens: 40 }),
        invocation({ id: 'silent', total_tokens: undefined, input_tokens: undefined, output_tokens: undefined }),
      ],
    })

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    const group = block.get('[data-testid="model-usage-group"]').text()
    expect(group).toContain('140 tokens')
    expect(group).toContain('(1 unreported)')
    expect(block.get('[data-testid="model-usage-unpriced"]').text()).toContain('1 of 2 calls')
    // The whole point of the wording: an unreported call is not a free one. Match a standalone
    // zero rather than a substring — "140 tokens" contains "0 tokens" and saying so would make
    // this case fire on the correct rendering.
    expect(group).toMatch(/^gpt-test · 140 tokens \(1 unreported\) · 2 calls$/)
    expect(group).not.toMatch(/(^|\D)0 tokens/)
  })

  it('keeps a model that never reported usage out of the totals without hiding it', async () => {
    listModelInvocations.mockResolvedValue({
      items: [invocation({ id: 'silent', model: 'no-usage', total_tokens: null, input_tokens: null, output_tokens: null })],
    })

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    expect(block.get('[data-testid="model-usage-group"]').text()).toContain('no token usage reported')
    expect(block.get('[data-testid="model-usage-group"]').text()).toContain('no-usage')
    expect(block.text()).not.toContain('NaN')
    expect(block.text()).not.toMatch(/\b0 tokens\b/)
  })

  it('declares the figures a floor once the walk hits its page cap', async () => {
    const page = (cursor: string | null) => ({
      items: [invocation({ id: `p-${cursor ?? 'first'}`, total_tokens: 10, input_tokens: 6, output_tokens: 4 })],
      next_cursor: cursor,
    })
    listModelInvocations
      .mockResolvedValueOnce(page('c2'))
      .mockResolvedValueOnce(page('c3'))
      .mockResolvedValueOnce(page('c4'))
      .mockResolvedValueOnce(page('c5'))
      .mockResolvedValueOnce(page('c6'))

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    expect(listModelInvocations).toHaveBeenCalledTimes(5)
    const truncated = block.get('[data-testid="model-usage-truncated"]')
    expect(truncated.text()).toContain('Partial')
    expect(truncated.text()).toContain('floor')
  })

  it('reports an unreadable audit as unreadable rather than as a run that cost nothing', async () => {
    listModelInvocations.mockRejectedValue(new Error('gateway down'))

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    expect(block.get('[data-testid="model-usage-unavailable"]').text()).toContain('not a zero-cost run')
    expect(block.findAll('[data-testid="model-usage-group"]')).toHaveLength(0)
    expect(block.text()).not.toMatch(/\b0 tokens\b/)
  })

  it('shows the empty state only when the audit really came back empty', async () => {
    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    expect(block.get('[data-testid="model-usage-empty"]').text()).toContain('No model calls')
  })
})
