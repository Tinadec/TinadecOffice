import { describe, expect, it } from 'vitest'
import type { ModelInvocationDto } from '../api'
import { summarizeModelInvocations } from './modelUsage'

/**
 * The roll-up behind "what did this run cost". It reads Core's audit rows, and the one thing it can
 * get wrong in a way the eye will not catch is turning "the provider reported nothing" into "the
 * call was free" — so most of these cases are about absent values, not arithmetic.
 */
function row(over: Partial<ModelInvocationDto> = {}): ModelInvocationDto {
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
    input_tokens: 10,
    output_tokens: 5,
    total_tokens: 15,
    ...over,
  }
}

describe('summarizeModelInvocations', () => {
  it('groups by model and provider, and the group sums are the row sums', () => {
    const summary = summarizeModelInvocations([
      row({ id: 'a', input_tokens: 100, output_tokens: 40, total_tokens: 140 }),
      row({ id: 'b', input_tokens: 7, output_tokens: 3, total_tokens: 10 }),
      row({ id: 'c', model: 'other', input_tokens: 1, output_tokens: 1, total_tokens: 2 }),
    ])

    expect(summary.calls).toBe(3)
    expect(summary.groups).toHaveLength(2)
    const main = summary.groups.find((group) => group.model === 'gpt-test')
    expect(main?.calls).toBe(2)
    expect(main?.inputTokens).toBe(107)
    expect(main?.outputTokens).toBe(43)
    expect(main?.totalTokens).toBe(150)
    // The sums must equal the rows they were built from, or the panel is inventing numbers.
    expect(summary.groups.reduce((total, group) => total + (group.totalTokens ?? 0), 0)).toBe(152)
  })

  it('counts an unreported call without pricing it at zero', () => {
    const summary = summarizeModelInvocations([
      row({ id: 'silent', input_tokens: undefined, output_tokens: undefined, total_tokens: undefined }),
    ])

    const group = summary.groups[0]
    expect(group?.calls).toBe(1)
    expect(group?.totalTokens).toBeNull()
    expect(group?.unpricedCalls).toBe(1)
    expect(summary.unpricedCalls).toBe(1)
  })

  it('keeps a present zero as a real zero instead of dropping it', () => {
    const summary = summarizeModelInvocations([
      row({ id: 'free', total_tokens: 0, input_tokens: 0, output_tokens: 0 }),
      row({ id: 'silent', total_tokens: null, input_tokens: null, output_tokens: null }),
    ])

    const group = summary.groups[0]
    expect(group?.calls).toBe(2)
    // One row reported 0 and the other reported nothing: the sum is the reported 0, and exactly
    // one call is still unpriced. Collapsing those two facts is the bug this case exists for.
    expect(group?.totalTokens).toBe(0)
    expect(group?.unpricedCalls).toBe(1)
  })

  it('separates providers that serve the same model name', () => {
    const summary = summarizeModelInvocations([
      row({ id: 'p1', provider_instance_id: 'prov-1', total_tokens: 10, input_tokens: 6, output_tokens: 4 }),
      row({ id: 'p2', provider_instance_id: 'prov-2', total_tokens: 20, input_tokens: 12, output_tokens: 8 }),
    ])

    expect(summary.groups).toHaveLength(2)
    expect(summary.groups[0]?.providerId).toBe('prov-2')
    expect(summary.groups[1]?.providerId).toBe('prov-1')
  })

  it('orders the heaviest group first and the unpriced one last', () => {
    const summary = summarizeModelInvocations([
      row({ id: 'silent', model: 'no-usage', total_tokens: undefined, input_tokens: undefined, output_tokens: undefined }),
      row({ id: 'small', model: 'cheap', total_tokens: 3, input_tokens: 2, output_tokens: 1 }),
      row({ id: 'big', model: 'heavy', total_tokens: 900, input_tokens: 500, output_tokens: 400 }),
    ])

    expect(summary.groups.map((group) => group.model)).toEqual(['heavy', 'cheap', 'no-usage'])
  })

  it('carries the truncation flag so a partial walk cannot read as a total', () => {
    expect(summarizeModelInvocations([row()], { truncated: true }).truncated).toBe(true)
    expect(summarizeModelInvocations([row()]).truncated).toBe(false)
  })

  it('summarises an empty page without inventing a group', () => {
    const summary = summarizeModelInvocations([])
    expect(summary.groups).toEqual([])
    expect(summary.calls).toBe(0)
    expect(summary.unpricedCalls).toBe(0)
  })
})
