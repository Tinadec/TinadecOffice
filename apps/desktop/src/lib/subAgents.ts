import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'
import { toolKindOf } from './toolPresentation'

/**
 * Groups one turn's activity by the subagents that produced it.
 *
 * Core names each execution instance with a run-scoped handle (`search#2`) precisely because
 * two parallel workers of one role are otherwise indistinguishable, and it stamps both
 * `handle` and `task_id` on the events an instance emits: `worker.assigned` carries the
 * handle and the agent slug, `model.output.*` carries the task and handle behind each thought,
 * `plan.updated` carries the plan, and tool events carry the task. So the attribution is in
 * the data; it was simply being flattened into one list.
 *
 * The structural rule, taken from how the reference products show this: **the lead agent's
 * own activity is the turn's spine; a subagent is a block that owns its own steps.** Only
 * work explicitly attributed to an instance is lifted out — an unattributed step stays with
 * the turn, because guessing that it belonged to some subagent would be inventing structure.
 *
 * Attribution covers thoughts and plans. A tool call carries a task id but not the handle, and
 * the handle is what the card is keyed by, so calls stay on the turn spine rather than being
 * guessed onto a card. That is a limitation of the current event payloads, not a decision.
 */

export interface SubAgentGroup {
  /** `task_id` when Core named one, else the handle. Stable across the run. */
  key: string
  handle: string
  /** The agent slug (`search`, `global_engineering`) when a worker.assigned named it. */
  slug: string | null
  steps: ThinkingStep[]
  /** Calls Core attributed to this task. Owned by the card, not shown on the turn's spine. */
  calls: ToolCall[]
  /** Ordering clock: the earliest sequence in the group. */
  seq: number
}

export interface SubAgentSplit {
  /** Steps no instance claimed — the lead agent's own work. */
  spine: ThinkingStep[]
  /** Calls no instance claimed; these are the ones the turn renders itself. */
  ownCalls: ToolCall[]
  groups: SubAgentGroup[]
}

function seqOf(step: ThinkingStep): number {
  return typeof step.seq === 'number' ? step.seq : Number.MAX_SAFE_INTEGER
}

export function subAgentGroups(steps: ThinkingStep[], calls: ToolCall[]): SubAgentSplit {
  // The handle is the display name and the task id is the stable key; a slug lets the card
  // say "搜索专家" instead of only "search#2". worker.assigned is the only event with all
  // three, so it is read first and its mapping applied to every later step.
  const slugByTask = new Map<string, string>()
  // A call arrives with its task but not its handle — the timeline route names the task, and the
  // handle is what a card is keyed by. The steps are where a task's handle is known, so the map is
  // built from them and then used to place calls.
  const handleByTask = new Map<string, string>()
  for (const step of steps) {
    const slug = typeof step.details?.slug === 'string' ? step.details.slug : undefined
    if (step.taskId && slug) slugByTask.set(step.taskId, slug)
    if (step.taskId && step.handle) handleByTask.set(step.taskId, step.handle)
  }

  const spine: ThinkingStep[] = []
  const byKey = new Map<string, SubAgentGroup>()

  for (const step of steps) {
    const handle = step.handle
    const key = step.taskId ?? handle
    if (!handle || !key) {
      spine.push(step)
      continue
    }
    let group = byKey.get(key)
    if (!group) {
      group = {
        key,
        handle,
        slug: slugByTask.get(key) ?? null,
        steps: [],
        calls: [],
        seq: seqOf(step),
      }
      byKey.set(key, group)
    }
    group.steps.push(step)
    group.seq = Math.min(group.seq, seqOf(step))
  }

  // Place each call under the instance that made it. A call whose task never named a handle stays on
  // the spine: guessing which subagent it belonged to would put it under the wrong card, and a card
  // is a claim about who did the work.
  const ownCalls: ToolCall[] = []
  for (const call of calls) {
    const handle = call.taskId ? handleByTask.get(call.taskId) : undefined
    const group = handle ? byKey.get(call.taskId!) : undefined
    // A call waiting on a person always stays on the spine, whichever instance made it. Moving it
    // into a card would put the decision behind a fold — the one thing this transcript must never
    // do — because only the turn's own rows are promoted into the always-visible attention region.
    if (!group || call.status === 'waiting_approval') { ownCalls.push(call); continue }
    group.calls.push(call)
    group.seq = Math.min(group.seq, typeof call.seq === 'number' ? call.seq : group.seq)
  }

  return {
    spine,
    ownCalls,
    groups: [...byKey.values()].sort((a, b) => a.seq - b.seq),
  }
}

/** Is this group doing anything right now? Drives the card's live badge. */
export function groupStatus(group: SubAgentGroup): 'running' | 'failed' | 'done' {
  if (group.steps.some((step) => step.status === 'failed' || step.severity === 'critical' || step.severity === 'error')) {
    return 'failed'
  }
  if (group.steps.some((step) => step.status === 'running')) return 'running'
  return 'done'
}

/** What the group touched, for the collapsed card line. */
export function groupTargets(steps: ThinkingStep[]): string[] {
  const seen = new Set<string>()
  for (const step of steps) {
    if (step.type !== 'plan') continue
    for (const line of step.description.split('\n')) {
      const text = line.replace(/^[✓→○]\s*/, '').trim()
      if (text) seen.add(text)
    }
  }
  return [...seen]
}

/** Tool kinds that count as "read-only exploration" on a card's summary line. */
export function groupQuietCalls(calls: ToolCall[]): number {
  return calls.filter((call) => {
    const kind = toolKindOf(call.toolId)
    return kind === 'read' || kind === 'list' || kind === 'search'
  }).length
}
