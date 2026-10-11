import type { MessageDto } from '@/api'
import type { TurnActivity } from '@/composables/useAgentActivity'

/**
 * Turns the session's flat message list into **turns** — the unit the transcript is read in.
 *
 * Why this exists rather than pairing on `run_id`: Core does not put a run on the user's
 * message. `FullDuplexRunCoordinator.SubmitInteractionAsync` appends it with only a
 * `clientMessageId`; the run is created after and attached to the *turn* row, and only the
 * assistant message is written with a `run_id`. So a user message carries no run to pair on.
 *
 * The exact link does exist one hop away: `Run.trigger_message_id` is the user message a run
 * was admitted for. We use it when the caller supplies it, and fall back to reading the
 * messages in order — which is sound because Core assigns `sequence` monotonically per session
 * and appends the user message before the run exists.
 *
 * Anything left over — a run with activity but no message to hang it on yet — becomes a live
 * turn at the end, in run order. That is the case the old code could not render at all.
 */

/** The run shape this module needs. `RunDto` satisfies it; the controller's local row may not. */
export interface TranscriptRun {
  id: string
  status: string
  /** Core's exact link from a run to the user message it was admitted for. */
  trigger_message_id?: string | null
}

export interface ConversationTurn {
  /** Stable key for the turn: the user message id, or the run id for a live turn. */
  key: string
  userMessage: MessageDto | null
  answer: MessageDto | null
  activity: TurnActivity | null
  streamingText: string | null
  status: 'running' | 'settled'
  runId: string | null
}

export interface ConversationTurnsInput {
  messages: MessageDto[]
  runs: TranscriptRun[]
  activities?: Record<string, TurnActivity>
  streaming?: Record<string, string>
}

/** Core's terminal run states (`RunStatusMachine`). A parked run is not terminal. */
const TERMINAL = new Set(['completed', 'failed', 'cancelled'])

/** Roles that belong to the turn that is already open. */
const TRAILING_ROLES = new Set(['assistant', 'tool_evidence'])

function isSettled(answer: MessageDto | null, runId: string | null, statusById: Map<string, string>): boolean {
  if (answer) return true
  if (!runId) return false
  return TERMINAL.has(statusById.get(runId) ?? '')
}

export function conversationTurns(input: ConversationTurnsInput): ConversationTurn[] {
  const { messages, runs, activities = {}, streaming = {} } = input
  const statusById = new Map(runs.map((run) => [run.id, run.status]))

  // A run admits exactly one trigger message, so first-wins is the whole rule; a duplicate
  // would mean two runs for one message, and the earlier run owns it.
  const runByTrigger = new Map<string, string>()
  for (const run of runs) {
    const trigger = run.trigger_message_id
    if (trigger && !runByTrigger.has(trigger)) runByTrigger.set(trigger, run.id)
  }

  interface Open {
    key: string
    userMessage: MessageDto | null
    answer: MessageDto | null
    runId: string | null
  }

  const open: Open[] = []
  let current: Open | null = null

  for (const message of messages) {
    if (message.role === 'user') {
      current = {
        key: message.id,
        userMessage: message,
        answer: null,
        runId: runByTrigger.get(message.id) ?? null,
      }
      open.push(current)
      continue
    }
    if (!TRAILING_ROLES.has(message.role)) continue
    // A trailing row with no user message before it (a cut history, an old session) still
    // deserves a turn rather than being dropped.
    if (!current) {
      current = { key: message.run_id ?? message.id, userMessage: null, answer: null, runId: null }
      open.push(current)
    }
    if (message.run_id && !current.runId) current.runId = message.run_id
    if (message.role === 'assistant') current.answer = message
  }

  const claimed = new Set(open.map((turn) => turn.runId).filter((id): id is string => Boolean(id)))

  const turns: ConversationTurn[] = open.map((turn) => ({
    key: turn.key,
    userMessage: turn.userMessage,
    answer: turn.answer,
    activity: turn.runId ? activities[turn.runId] ?? null : null,
    streamingText: turn.runId ? streaming[turn.runId] ?? null : null,
    status: isSettled(turn.answer, turn.runId, statusById) ? 'settled' : 'running',
    runId: turn.runId,
  }))

  // Runs the messages never claimed: admitted, working, nothing persisted to anchor on yet.
  for (const run of runs) {
    if (claimed.has(run.id)) continue
    const activity = activities[run.id] ?? null
    const text = streaming[run.id] ?? null
    // A run with neither is a receipt, not a turn — rendering it would put an empty
    // "activity" block in the transcript for every idle run in the session.
    if (!activity && !text) continue
    turns.push({
      key: run.id,
      userMessage: null,
      answer: null,
      activity,
      streamingText: text,
      status: isSettled(null, run.id, statusById) ? 'settled' : 'running',
      runId: run.id,
    })
  }

  return turns
}
