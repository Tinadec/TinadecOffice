import { toolKindOf, type ToolKind } from './toolPresentation'

/**
 * One tool step, read as a sentence: "Read src/graph.ts", "Searched useResults, 3 results".
 *
 * The grouping rules come from the reference material and are the reason this is not just a
 * label map:
 *
 * - Quiet, non-mutating work (`read` / `list` / `search`) runs back to back and folds into a
 *   single summary — "Explored 6 files". Showing every one buries the answer.
 * - Anything that changes something, needs a decision, or fails stays on its own line.
 *
 * `foldable` is the whole contract the renderer needs: a run of consecutive foldable steps
 * becomes one summary row, and everything else keeps its own row. Verb tense follows status,
 * because "read" while running and "read" once done are the same word in English but the
 * point is that the line stops moving.
 */

export type ToolVerbTense = 'running' | 'done' | 'failed'

/** Kinds that may be folded into a summary when they run consecutively. */
const FOLDABLE = new Set<ToolKind>(['read', 'list', 'search'])

export function isFoldableKind(kind: ToolKind): boolean {
  return FOLDABLE.has(kind)
}

/**
 * Whether a step keeps its own line. Failures and anything awaiting a person always do,
 * whatever kind they are — a failed read is not quiet work.
 */
export function keepsOwnLine(kind: ToolKind, status: string): boolean {
  if (status === 'failed' || status === 'waiting_approval') return true
  return !isFoldableKind(kind)
}

/**
 * Verb **keys**, not words. The rendered label has to follow the reader's language, and a table
 * of literals here would show Chinese in the English build — the one place this transcript could
 * not be translated. `ToolGroupSummary` resolves these through `t()`.
 */
const VERB_KEYS: Record<ToolKind, { running: string; done: string }> = {
  read: { running: 'agent.toolVerb.reading', done: 'agent.toolVerb.read' },
  list: { running: 'agent.toolVerb.listing', done: 'agent.toolVerb.list' },
  search: { running: 'agent.toolVerb.searching', done: 'agent.toolVerb.search' },
  write: { running: 'agent.toolVerb.writing', done: 'agent.toolVerb.write' },
  shell: { running: 'agent.toolVerb.running', done: 'agent.toolVerb.ran' },
  git: { running: 'agent.toolVerb.gitBusy', done: 'agent.toolVerb.git' },
  mcp: { running: 'agent.toolVerb.calling', done: 'agent.toolVerb.call' },
  sandbox: { running: 'agent.toolVerb.sandboxBusy', done: 'agent.toolVerb.sandbox' },
  orchestration: { running: 'agent.toolVerb.orchestrating', done: 'agent.toolVerb.orchestrate' },
  chat: { running: 'agent.toolVerb.communicating', done: 'agent.toolVerb.communicate' },
  other: { running: 'agent.toolVerb.working', done: 'agent.toolVerb.work' },
}

/** The i18n key for one step's verb, in the tense its status calls for. */
export function verbKeyFor(toolId: string | null | undefined, status: string): string {
  const kind = toolKindOf(toolId)
  const pair = VERB_KEYS[kind]
  return status === 'running' || status === 'pending' ? pair.running : pair.done
}

/**
 * Fold a consecutive run of quiet steps into one sentence.
 *
 * The count of distinct targets is what makes it useful ("Explored 6 files"); a run that
 * touched nothing nameable still reports how many steps it took rather than saying nothing.
 */
export interface FoldedSummary {
  /** Step count in the run, always >= 1. */
  steps: number
  /** i18n key for the run's verb phrase, chosen from the dominant kind. */
  labelKey: string
}

export function foldSummary(kinds: ToolKind[]): FoldedSummary {
  const steps = kinds.length
  // The run's own kinds decide the wording; a run of reads that is mostly searches should
  // not claim to be reads.
  const counts = new Map<ToolKind, number>()
  for (const kind of kinds) counts.set(kind, (counts.get(kind) ?? 0) + 1)
  let dominant: ToolKind = kinds[0] ?? 'other'
  for (const [kind, count] of counts) {
    if (count > (counts.get(dominant) ?? 0)) dominant = kind
  }
  return { steps, labelKey: VERB_KEYS[dominant].done }
}
