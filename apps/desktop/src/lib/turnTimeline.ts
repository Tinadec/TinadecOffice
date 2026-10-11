import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'
import { toolKindOf } from './toolPresentation'
import { isFoldableKind, keepsOwnLine } from './toolSentence'

/**
 * The ordered, layered rows of one turn's activity.
 *
 * Three things happen here that the transcript used to leave to the template:
 *
 * 1. **Ordering.** Session journal sequence is the shared clock; timestamps are only a
 *    fallback for old rows that predate it.
 * 2. **Reasoning stays its own block.** Provider reasoning is a different kind of fact from
 *    orchestration steps, and merging them produced "9 thoughts" whenever the engine emitted
 *    nine lifecycle events.
 * 3. **Layering.** Consecutive quiet steps (read/list/search) collapse into one group, so a
 *    run of twelve reads does not bury the answer. Anything that changes something, needs a
 *    decision, or fails keeps its own row. Waiting approvals are lifted out entirely —
 *    `kind: 'pending'` — because folding a decision away is the one thing that must not
 *    happen; the caller renders those outside the collapsible region.
 */

export type TurnRow =
  | { kind: 'thinking'; id: string; steps: ThinkingStep[] }
  | { kind: 'tool'; id: string; call: ToolCall }
  | { kind: 'toolGroup'; id: string; calls: ToolCall[] }
  | { kind: 'pending'; id: string; call: ToolCall }

interface Ordered {
  seq: number | undefined
  at: string | null
  step?: ThinkingStep
  call?: ToolCall
}

export function turnRows(steps: ThinkingStep[], calls: ToolCall[]): TurnRow[] {
  const ordered: Ordered[] = [
    ...steps.map((step) => ({ seq: step.seq, at: step.timestamp, step })),
    ...calls.map((call) => ({ seq: call.seq || undefined, at: call.startedAt, call })),
  ].sort((a, b) => a.seq != null && b.seq != null
    ? a.seq - b.seq
    : (Date.parse(a.at ?? '') || 0) - (Date.parse(b.at ?? '') || 0))

  const rows: TurnRow[] = []
  /** The group currently collecting consecutive foldable calls, if any. */
  let openGroup: Extract<TurnRow, { kind: 'toolGroup' }> | null = null

  const closeGroup = () => {
    if (openGroup) rows.push(openGroup)
    openGroup = null
  }

  for (const item of ordered) {
    if (item.call) {
      const call = item.call
      if (call.status === 'waiting_approval') {
        // A decision is never folded into a summary or hidden behind one.
        closeGroup()
        rows.push({ kind: 'pending', id: call.id, call })
        continue
      }
      const kind = toolKindOf(call.toolId)
      if (isFoldableKind(kind) && !keepsOwnLine(kind, call.status)) {
        if (!openGroup) {
          openGroup = { kind: 'toolGroup', id: `group-${call.id}`, calls: [call] }
        } else {
          openGroup.calls.push(call)
        }
        continue
      }
      closeGroup()
      rows.push({ kind: 'tool', id: call.id, call })
      continue
    }

    const step = item.step
    if (!step) continue
    closeGroup()
    const last = rows.at(-1)
    const isReasoning = step.type === 'reasoning'
    // Provider reasoning and orchestration facts never merge into one block.
    if (last?.kind === 'thinking' && !isReasoning && last.steps[0]?.type !== 'reasoning') {
      last.steps.push(step)
    } else {
      rows.push({ kind: 'thinking', id: step.id, steps: [step] })
    }
  }
  closeGroup()
  return rows
}

/** Rows that belong inside the collapsible region: everything a person need not act on. */
export function detailRows(rows: TurnRow[]): TurnRow[] {
  return rows.filter((row) => row.kind !== 'pending')
}

/** Rows that must stay in view however the turn is folded. */
export function attentionRows(rows: TurnRow[]): Array<Extract<TurnRow, { kind: 'pending' }>> {
  return rows.filter((row): row is Extract<TurnRow, { kind: 'pending' }> => row.kind === 'pending')
}
