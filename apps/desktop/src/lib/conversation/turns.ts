import type { MessageDto } from '@/api'
import type { FeedState, RunActivity } from './reducer'

/**
 * Pairs a session's messages, runs and run activity into turns: a user message, the runs
 * it started, and the answers they produced.
 *
 * A run names the message that started it (`trigger_message_id`), so pairing never
 * guesses from timing when Core said. A run with no trigger — or one whose trigger is not
 * on screen — joins the latest turn that began before it, else stands as its own turn. An
 * answer joins the turn of its `run_id`; one without a run follows the same time rule.
 */

export type TurnPhase = 'sending' | 'running' | 'awaiting' | 'settled' | 'failed' | 'cancelled'

export interface TurnRunInput {
  id: string
  status: string
  trigger_message_id?: string | null
  created_at?: string | null
  updated_at?: string | null
}

export interface TurnRun {
  run: TurnRunInput
  activity: RunActivity | null
  /** Answer text still streaming for this run, when the stream is open. */
  streamingText: string | null
}

export interface ConversationTurn {
  id: string
  user: MessageDto | null
  runs: TurnRun[]
  answers: MessageDto[]
  phase: TurnPhase
  startedAt: string | null
  /** When the last run settled; null while any run can still change. */
  endedAt: string | null
}

const RUNNING = new Set(['pending', 'planning', 'understanding', 'executing', 'running', 'reviewing', 'replanning', 'paused'])
const AWAITING = new Set(['awaiting_user', 'awaiting_approval', 'awaiting_delegate'])

export function isLiveRunStatus(status: string): boolean {
  return RUNNING.has(status) || AWAITING.has(status)
}

function time(value: string | null | undefined): number {
  const parsed = value ? Date.parse(value) : Number.NaN
  return Number.isFinite(parsed) ? parsed : Number.NEGATIVE_INFINITY
}

function phaseOf(turn: ConversationTurn): TurnPhase {
  if (turn.user?.id.startsWith('pending-') && turn.runs.length === 0) return 'sending'
  const statuses = turn.runs.map((entry) => entry.run.status)
  if (statuses.some((status) => RUNNING.has(status))) return 'running'
  if (statuses.some((status) => AWAITING.has(status))) return 'awaiting'
  const last = statuses.at(-1)
  if (last === 'failed') return 'failed'
  if (last === 'cancelled') return 'cancelled'
  return 'settled'
}

export interface BuildTurnsInput {
  messages: readonly MessageDto[]
  runs: readonly TurnRunInput[]
  feed: FeedState
  /** Answer text being streamed, by run id. */
  streaming?: ReadonlyMap<string, string>
}

export function buildTurns({ messages, runs, feed, streaming }: BuildTurnsInput): ConversationTurn[] {
  const turns: ConversationTurn[] = []
  const byUserMessage = new Map<string, ConversationTurn>()
  const byRun = new Map<string, ConversationTurn>()

  const ordered = [...messages].sort((a, b) => time(a.created_at) - time(b.created_at))
  for (const message of ordered) {
    if (message.role !== 'user') continue
    const turn: ConversationTurn = { id: `turn:${message.id}`, user: message, runs: [], answers: [], phase: 'settled', startedAt: message.created_at ?? null, endedAt: null }
    turns.push(turn)
    byUserMessage.set(message.id, turn)
  }

  /** The latest turn that began at or before `at`. */
  const precedingTurn = (at: number): ConversationTurn | null => {
    let found: ConversationTurn | null = null
    for (const turn of turns) {
      const start = time(turn.startedAt)
      if (start <= at && (!found || start >= time(found.startedAt))) found = turn
    }
    return found
  }

  const runTime = (run: TurnRunInput) => (run.created_at ? time(run.created_at) : Number.POSITIVE_INFINITY)
  for (const run of [...runs].sort((a, b) => runTime(a) - runTime(b))) {
    // A run admitted moments ago is known only by id until the run list reloads: it
    // belongs to the newest turn, not before everything as a missing time would sort it.
    const at = run.created_at ? time(run.created_at) : Number.POSITIVE_INFINITY
    const turn = (run.trigger_message_id ? byUserMessage.get(run.trigger_message_id) : undefined)
      ?? precedingTurn(at)
      ?? (() => {
        const own: ConversationTurn = { id: `turn:run:${run.id}`, user: null, runs: [], answers: [], phase: 'settled', startedAt: run.created_at ?? null, endedAt: null }
        turns.push(own)
        return own
      })()
    turn.runs.push({ run, activity: feed.runs.get(run.id) ?? null, streamingText: streaming?.get(run.id) ?? null })
    byRun.set(run.id, turn)
  }

  for (const message of ordered) {
    if (message.role === 'user') continue
    const turn = (message.run_id ? byRun.get(message.run_id) : undefined) ?? precedingTurn(time(message.created_at))
    if (turn) turn.answers.push(message)
  }

  turns.sort((a, b) => time(a.startedAt) - time(b.startedAt))
  for (const turn of turns) {
    turn.phase = phaseOf(turn)
    if (turn.phase === 'settled' || turn.phase === 'failed' || turn.phase === 'cancelled') {
      const ends = turn.runs.map((entry) => entry.activity?.lastAt ?? entry.run.updated_at ?? null).filter((end): end is string => !!end)
      turn.endedAt = ends.sort().at(-1) ?? null
    }
  }
  return turns
}
