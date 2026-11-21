import type {
  DispatchEntry,
  FeedEntry,
  MarkerEntry,
  PlanEntry,
  CorrectionEntry,
  ReasoningEntry,
  RunActivity,
  SubAgent,
  SupervisionEntry,
  ToolEntry,
  WaitEntry,
} from './reducer'
import type { SupervisionReviewBrief } from './supervision'
import { entrySentence } from './toolSentence'

/**
 * Turns one run's flat entry list into the nodes a turn renders.
 *
 * Quiet entries — reasoning, read/list/search calls that did not fail, waits — fold into
 * an exploration group when two or more are adjacent ("Explored 6 files · 2 searches");
 * a single quiet entry stays on its own line. Everything that changes something, needs a
 * decision or failed is loud and gets its own line. A tool waiting on a person is lifted
 * out of the flow entirely into `approvals`, so it can never be folded away; that includes
 * approvals a sub-agent asked for. Consecutive dispatches become one agent group, and each
 * sub-agent's own entries go through the same rules recursively.
 */

export interface ActivityGroupNode {
  type: 'group'
  id: string
  entries: (ReasoningEntry | ToolEntry | WaitEntry)[]
  /** Distinct files read, searches run, directories listed, thoughts. */
  counts: { files: number; searches: number; lists: number; thoughts: number }
  thinkingMs: number
  /** Only reasoning: the group reads "Thought for 12s". */
  thinkingOnly: boolean
  /** The open end of a live turn: still exploring, so it stays expanded. */
  exploring: boolean
}

export interface SubAgentNode {
  agent: SubAgent
  /** The stored status, or `awaiting_approval` while one of its calls waits on a person. */
  status: SubAgent['status'] | 'awaiting_approval'
  dispatch: DispatchEntry
  nodes: FeedNode[]
  /** The newest entry of the sub-agent, for the "currently doing" line. */
  latest: FeedEntry | null
}

export type FeedNode =
  | ActivityGroupNode
  | { type: 'reasoning'; id: string; entry: ReasoningEntry }
  | { type: 'tool'; id: string; entry: ToolEntry }
  | { type: 'wait'; id: string; entry: WaitEntry }
  | { type: 'agents'; id: string; agents: SubAgentNode[] }
  | { type: 'plan'; id: string; entry: PlanEntry }
  | { type: 'correction'; id: string; entry: CorrectionEntry }
  | { type: 'marker'; id: string; entry: MarkerEntry }
  | { type: 'supervision'; id: string; entry: SupervisionEntry }

export interface PendingApproval {
  entry: ToolEntry
  /** The sub-agent that asked; null when the turn's own agent did. */
  requester: SubAgent | null
}

export interface GroupedRun {
  nodes: FeedNode[]
  /**
   * Ids of the earliest rows the row budget hides behind "N earlier steps" (empty when
   * nothing is hidden). The nodes are still in `nodes`; the renderer decides whether to draw
   * them, so opening the row costs no regroup. Only a settled run is trimmed — while a run is
   * live its newest rows are what the reader is watching, and parked rows are what they must
   * decide on — and only rows a reader can afford to miss (see `isBudgetRow`).
   */
  hidden: string[]
  approvals: PendingApproval[]
  /** Sub-agents of this run, in dispatch order (the right-column roster reads this). */
  agents: SubAgentNode[]
  running: number
  /**
   * A supervision escalation this run is parked on, carried through grouping so the
   * renderer reads it from the feed instead of rebuilding it out of a per-run activity
   * map that only exists for the run the user happens to have selected.
   */
  review: { reasons: string[]; options: string[]; brief: SupervisionReviewBrief | null; at: string } | null
}

type QuietEntry = ReasoningEntry | ToolEntry | WaitEntry

/** Not a type guard: a tool that failed is a tool but not quiet. */
function isQuiet(entry: FeedEntry): boolean {
  if (entry.kind === 'reasoning' || entry.kind === 'wait') return true
  if (entry.kind !== 'tool') return false
  if (entry.status === 'failed' || entry.status === 'outcome_unknown' || entry.toolSuccess === false) return false
  return entrySentence(entry).quiet !== null
}

function isLive(entry: QuietEntry): boolean {
  if (entry.kind === 'tool') return entry.status === 'requested' || entry.status === 'running'
  if (entry.kind === 'reasoning') return entry.status === 'running'
  return entry.status === 'waiting'
}

function durationMs(start: string, end: string | null): number {
  if (!end) return 0
  const ms = Date.parse(end) - Date.parse(start)
  return Number.isFinite(ms) && ms > 0 ? ms : 0
}

function groupOf(entries: QuietEntry[]): ActivityGroupNode {
  const files = new Set<string>()
  let searches = 0
  let lists = 0
  let thoughts = 0
  let thinkingMs = 0
  for (const entry of entries) {
    if (entry.kind === 'reasoning') {
      thoughts += 1
      thinkingMs += durationMs(entry.startedAt, entry.endedAt)
    } else if (entry.kind === 'tool') {
      const quiet = entrySentence(entry).quiet
      if (quiet === 'read') files.add(entry.resourcePath ?? entry.id)
      else if (quiet === 'search') searches += 1
      else if (quiet === 'list') lists += 1
    }
  }
  return {
    type: 'group',
    id: `group:${entries[0]!.id}`,
    entries,
    counts: { files: files.size, searches, lists, thoughts },
    thinkingMs,
    thinkingOnly: thoughts === entries.length,
    exploring: false,
  }
}

function single(entry: QuietEntry): FeedNode {
  if (entry.kind === 'reasoning') return { type: 'reasoning', id: entry.id, entry }
  if (entry.kind === 'tool') return { type: 'tool', id: entry.id, entry }
  return { type: 'wait', id: entry.id, entry }
}

interface Context {
  record: RunActivity
  /** Entries per sub-agent task id; the main scope is keyed by null. */
  byScope: Map<string | null, FeedEntry[]>
  approvals: PendingApproval[]
  agents: SubAgentNode[]
  live: boolean
  /** `listed`: a quiet step gets its own row instead of joining an exploration group. */
  quiet: 'grouped' | 'listed'
}

function scopeOf(record: RunActivity, entry: FeedEntry): string | null {
  // A dispatch belongs where it was issued; every other entry belongs to its task.
  const taskId = entry.kind === 'dispatch' ? entry.parentTaskId : entry.taskId
  if (!taskId) return null
  const agent = record.tasks.get(taskId)
  return agent && !agent.isMain ? taskId : null
}

function build(context: Context, scope: string | null, requester: SubAgent | null): FeedNode[] {
  const nodes: FeedNode[] = []
  let quiet: QuietEntry[] = []
  const flush = () => {
    if (quiet.length === 1) nodes.push(single(quiet[0]!))
    else if (quiet.length > 1) nodes.push(groupOf(quiet))
    quiet = []
  }

  for (const entry of context.byScope.get(scope) ?? []) {
    if (entry.kind === 'tool' && entry.status === 'awaiting_approval') {
      context.approvals.push({ entry, requester })
      continue
    }
    if (entry.kind === 'supervision' && (entry.decision === 'pass' || entry.decision === 'skipped')) continue
    // A call that finished without a thought or a word leaves nothing to read: no empty row.
    if (entry.kind === 'reasoning' && entry.status !== 'running' && !entry.text.trim() && !entry.output.trim()) continue
    if (isQuiet(entry)) {
      if (context.quiet === 'listed') {
        flush()
        nodes.push(single(entry as QuietEntry))
      } else {
        quiet.push(entry as QuietEntry)
      }
      continue
    }
    flush()
    switch (entry.kind) {
      case 'dispatch': {
        const agent = context.record.tasks.get(entry.taskId)
        if (!agent || agent.isMain) break
        const child = subAgent(context, agent, entry)
        const previous = nodes.at(-1)
        if (previous?.type === 'agents') previous.agents.push(child)
        else nodes.push({ type: 'agents', id: `agents:${entry.id}`, agents: [child] })
        break
      }
      case 'tool':
        nodes.push({ type: 'tool', id: entry.id, entry })
        break
      case 'plan':
        nodes.push({ type: 'plan', id: entry.id, entry })
        break
      case 'correction':
        nodes.push({ type: 'correction', id: entry.id, entry })
        break
      case 'marker':
        nodes.push({ type: 'marker', id: entry.id, entry })
        break
      case 'supervision':
        nodes.push({ type: 'supervision', id: entry.id, entry })
        break
      default:
        break
    }
  }
  flush()

  const last = nodes.at(-1)
  if (context.live && last?.type === 'group') last.exploring = last.entries.some(isLive)
  return nodes
}

function subAgent(context: Context, agent: SubAgent, dispatch: DispatchEntry): SubAgentNode {
  const own = context.byScope.get(agent.taskId) ?? []
  const node: SubAgentNode = { agent, status: agent.status, dispatch, nodes: [], latest: own.at(-1) ?? null }
  // Registered before its children so the roster reads in dispatch order.
  context.agents.push(node)
  const before = context.approvals.length
  node.nodes = build(context, agent.taskId, agent)
  const waiting = context.approvals.slice(before).some((approval) => approval.requester?.taskId === agent.taskId)
  if (waiting && agent.status === 'running') node.status = 'awaiting_approval'
  return node
}

export interface GroupOptions {
  /**
   * `listed` gives every quiet step its own row (one line per read, search or thought).
   * `grouped` — the default — folds adjacent ones into "Explored 6 files".
   */
  quiet?: 'grouped' | 'listed'
  /**
   * How many flow nodes a settled turn keeps in view; the rest fold behind one "N earlier
   * steps" row. 0 or absent keeps everything. Ignored while the run is live.
   */
  budget?: number
}

/**
 * Whether the row budget may hide this row. A short shape is allowed to drop the noise —
 * thoughts, exploration groups, waits and the calls that finished cleanly — but not the
 * things a turn is *about*: a sub-agent's row, the plan, a failure, a correction or a
 * verdict. Those stay in view in every shape; only their neighbours can fold away.
 */
function isBudgetRow(node: FeedNode): boolean {
  switch (node.type) {
    case 'reasoning':
    case 'group':
    case 'wait':
      return true
    case 'tool':
      // A tool that ran and failed its own way (`toolSuccess: false`) is a failure too.
      return node.entry.status === 'completed' && node.entry.toolSuccess !== false
    default:
      return false
  }
}

/**
 * Groups one run. `live` is true while the run can still change (not settled), which is
 * what lets the last exploration group stay open and what suppresses the row budget.
 *
 * Memoized per run: a result is reused while the run's `version`, liveness and shape
 * options are unchanged. The feed re-derives turns on every published batch, but only the
 * run that actually received events pays for a regroup; historical runs return the same
 * object, so their nodes keep a stable identity and Vue skips their leaf updates. Reading
 * `record.version` here also makes the calling computed track exactly that run.
 */
const groupCache = new WeakMap<RunActivity, { version: number; live: boolean; quiet: 'grouped' | 'listed'; budget: number; result: GroupedRun }>()

export function groupRun(record: RunActivity, live: boolean, options: GroupOptions = {}): GroupedRun {
  const quiet = options.quiet ?? 'grouped'
  const budget = live ? 0 : options.budget ?? 0
  const cached = groupCache.get(record)
  if (cached && cached.version === record.version && cached.live === live && cached.quiet === quiet && cached.budget === budget) {
    return cached.result
  }
  const byScope = new Map<string | null, FeedEntry[]>()
  const ordered = [...record.entries].sort((a, b) => a.seq - b.seq)
  for (const entry of ordered) {
    const scope = scopeOf(record, entry)
    const list = byScope.get(scope)
    if (list) list.push(entry)
    else byScope.set(scope, [entry])
  }
  const context: Context = { record, byScope, approvals: [], agents: [], live, quiet }
  const nodes = build(context, null, null)
  const foldable = budget > 0 ? nodes.filter(isBudgetRow) : []
  const hidden = foldable.slice(0, Math.max(0, foldable.length - budget)).map((node) => node.id)
  const running = context.agents.filter((node) => node.status === 'running' || node.status === 'awaiting_approval').length
  const result: GroupedRun = { nodes, hidden, approvals: context.approvals, agents: context.agents, running, review: record.review }
  groupCache.set(record, { version: record.version, live, quiet, budget, result })
  return result
}

/** Longest interim answer that still reads as a note between steps, not as the answer. */
export const GROUP_TEXT_MAX_CHARS = 150

/**
 * Whether an answer of a turn shows as a compact two-line note. A turn that answers more than
 * once ("Let me check the tests first.") keeps its short interim lines out of the way; the
 * last answer of a turn is the reply itself and is never compacted.
 */
export function isCompactAnswer(content: string, index: number, count: number): boolean {
  return index < count - 1 && content.trim().length <= GROUP_TEXT_MAX_CHARS && !RICH_MARKDOWN.test(content)
}

/**
 * Only prose folds into a note. A code fence (a mermaid diagram is one), an image, a table
 * or display math is the point of the answer however short its source is, and clamping it
 * to two lines of small text would hide it.
 */
const RICH_MARKDOWN = /(^|\n)\s*(```|~~~|\$\$|\|.*\|)|!\[[^\]]*\]\(|<img\b/i
