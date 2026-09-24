import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

export type TurnTimelineItem =
  | { kind: 'thinking'; id: string; steps: ThinkingStep[] }
  | { kind: 'tool'; id: string; call: ToolCall }

/** Session journal sequence is the shared clock; timestamps are only a fallback for old rows. */
export function turnTimeline(steps: ThinkingStep[], calls: ToolCall[]): TurnTimelineItem[] {
  const ordered = [
    ...steps.map((step) => ({ seq: step.seq, at: step.timestamp, step, call: undefined })),
    ...calls.map((call) => ({ seq: call.seq || undefined, at: call.startedAt, step: undefined, call })),
  ].sort((a, b) => a.seq != null && b.seq != null
    ? a.seq - b.seq
    : (Date.parse(a.at ?? '') || 0) - (Date.parse(b.at ?? '') || 0))
  const result: TurnTimelineItem[] = []
  for (const item of ordered) {
    if (item.call) result.push({ kind: 'tool', id: item.call.id, call: item.call })
    else if (item.step) {
      const last = result.at(-1)
      // Provider reasoning stays distinct from orchestration facts, never "9 thoughts"
      // because the engine created nine lifecycle events.
      if (last?.kind === 'thinking' && item.step.type !== 'reasoning' && last.steps[0].type !== 'reasoning') {
        last.steps.push(item.step)
      } else result.push({ kind: 'thinking', id: item.step.id, steps: [item.step] })
    }
  }
  return result
}
