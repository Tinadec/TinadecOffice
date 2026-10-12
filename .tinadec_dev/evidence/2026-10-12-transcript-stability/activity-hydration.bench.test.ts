// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { writeFileSync } from 'node:fs'
import { cpus } from 'node:os'
import { resolve } from 'node:path'
import { effectScope, ref } from 'vue'
import { api, type EventEnvelope } from '@/api'
import { useAgentActivity } from '@/composables/useAgentActivity'

vi.mock('@/api', () => ({ api: {
  connectEvents: vi.fn(() => ({ close() {} })),
  listToolExecutions: vi.fn(async () => []),
} }))

const median = (values: number[]) => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)]!
function measure(count: number) {
  const scope = effectScope()
  const owner = scope.run(() => useAgentActivity(ref('benchmark-session'), undefined, { managedReads: true }))!
  const events: EventEnvelope[] = Array.from({ length: count }, (_, index) => ({
    v: '1', type: index === 0 ? 'task.accepted' : 'context.packed',
    run_id: 'benchmark-run', session_id: 'benchmark-session', seq: index + 1,
    ts: '', request_id: '', trace_id: '', capabilities: [], payload: {},
  }))
  const started = performance.now()
  owner.hydrate(events, [], ['benchmark-run'])
  const hydrateMs = performance.now() - started
  const repeatStarted = performance.now()
  owner.hydrate(events, [], ['benchmark-run'])
  const duplicateMs = performance.now() - repeatStarted
  const steps = owner.turnActivities.value['benchmark-run']?.thinkingSteps?.length
  scope.stop()
  expect(steps).toBe(count)
  return { events: count, steps, hydrate_ms: hydrateMs, duplicate_ms: duplicateMs }
}

describe('activity hydration performance evidence', () => {
  it('measures warm reducer batches and duplicate snapshots without network reads', () => {
    for (let index = 0; index < 3; index++) measure(1000)
    const samples: ReturnType<typeof measure>[] = []
    for (let repeat = 0; repeat < 7; repeat++) {
      for (const count of repeat % 2 ? [4000, 2000, 1000] : [1000, 2000, 4000]) samples.push(measure(count))
    }
    const summaries = [1000, 2000, 4000].map(count => {
      const subset = samples.filter(sample => sample.events === count)
      const hydrate_ms = median(subset.map(sample => sample.hydrate_ms))
      return { events: count, samples: subset.length, median_hydrate_ms: hydrate_ms, median_duplicate_ms: median(subset.map(sample => sample.duplicate_ms)), hydrate_microseconds_per_event: hydrate_ms * 1000 / count }
    })
    expect(api.listToolExecutions).not.toHaveBeenCalled()
    writeFileSync(resolve(process.cwd(), '../../.tinadec_dev/evidence/2026-10-12-transcript-stability/activity-hydration-results.json'), JSON.stringify({
      measured_at: new Date().toISOString(), runtime: process.version, cpu: cpus()[0]?.model,
      workload: 'Real useAgentActivity reducer, one accepted event plus context.packed records; no DOM paint or HTTP; three 1000-event warmups, seven alternating-size repetitions.',
      rest_reads: vi.mocked(api.listToolExecutions).mock.calls.length,
      summaries, samples,
      doubling_ratios: [summaries[1]!.median_hydrate_ms / summaries[0]!.median_hydrate_ms, summaries[2]!.median_hydrate_ms / summaries[1]!.median_hydrate_ms],
    }, null, 2) + '\n')
  })
})
