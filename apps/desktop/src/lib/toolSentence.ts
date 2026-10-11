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

const VERBS: Record<ToolKind, { running: string; done: string }> = {
  read: { running: '正在读取', done: '读取' },
  list: { running: '正在列出', done: '列出' },
  search: { running: '正在搜索', done: '搜索' },
  write: { running: '正在写入', done: '写入' },
  shell: { running: '正在运行', done: '运行' },
  git: { running: '正在执行 Git', done: 'Git' },
  mcp: { running: '正在调用', done: '调用' },
  sandbox: { running: '正在检查沙箱', done: '沙箱' },
  orchestration: { running: '正在编排', done: '编排' },
  chat: { running: '正在通信', done: '通信' },
  other: { running: '正在执行', done: '执行' },
}

/** The verb for one step, in the tense its status calls for. */
export function verbFor(toolId: string | null | undefined, status: string): string {
  const kind = toolKindOf(toolId)
  const pair = VERBS[kind]
  if (status === 'running' || status === 'pending') return pair.running
  return pair.done
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
  /** Verb phrase for the run, chosen from the dominant kind. */
  label: string
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
  return { steps, label: VERBS[dominant].done }
}
