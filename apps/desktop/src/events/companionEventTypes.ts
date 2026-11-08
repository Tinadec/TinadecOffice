/**
 * The companion feed's event names, in one place.
 *
 * Core writes NAMED SSE frames on `GET /api/v1/companion-events` (`event: companion.activity`,
 * `companion.run`, `companion.settings`) and a browser only dispatches a named frame to a listener
 * registered for exactly that name — every other frame is dropped silently. This is the lesson
 * already paid for in `coreEventTypes.ts`, where eleven of eighteen registered names had no
 * producer and the facts Core really recorded had no listener, so a whole tool timeline stayed
 * invisible no matter what the server sent.
 *
 * Rule for new events: add the name here first, then subscribe. The producer is
 * `TinadecCore/AspNetCore/Endpoints/CompanionEventEndpoints.cs`; grep the literal before adding an
 * entry and delete the entry when the producer goes away. Subscribing to an event Core never sends
 * is pure dead weight, and it hides the fact that a real fact is unrendered.
 */

/** One journal row of the companion domain: messages, group membership, autonomy. */
export const COMPANION_ACTIVITY_EVENT = 'companion.activity'
/** A run transition (`run_started` / `run_completed` / `run_failed`), projected from the same rows. */
export const COMPANION_RUN_EVENT = 'companion.run'
/** A user-wide settings change (main companion, buddy, global pause). */
export const COMPANION_SETTINGS_EVENT = 'companion.settings'

export const COMPANION_EVENT_TYPES = [
  COMPANION_ACTIVITY_EVENT,
  COMPANION_RUN_EVENT,
  COMPANION_SETTINGS_EVENT,
] as const

export type CompanionEventType = (typeof COMPANION_EVENT_TYPES)[number]

/**
 * The `data:` body of one frame. Field names are Core's snake_case wire shape; `payload` is the
 * row's own JSON blob (a run id, a message id, an error code) parsed by Core, not by us.
 */
export interface CompanionEventPayload {
  seq?: number
  id?: string
  kind?: string
  companion_id?: string | null
  conversation_id?: string | null
  summary?: string | null
  detail?: string | null
  payload?: Record<string, unknown> | null
  created_at?: string
}

/** `event: heartbeat` carries the cursor the subscriber stands at, nothing else. */
export interface CompanionHeartbeatPayload {
  after_seq?: number
}

/**
 * What a subscriber acts on: the frame name plus just enough identity to decide what to reread.
 * The full row is not needed — every consumer already knows how to read the authoritative list.
 */
export interface CompanionEventNotice {
  type: CompanionEventType
  seq: number
  kind: string | null
  companionId: string | null
  conversationId: string | null
  /** The run a `companion.run` row names, when the row carries one. */
  runId: string | null
}

/**
 * The satellite companion window cannot hold this stream: it is not a trusted host, so the main
 * process withholds the host credential and its only Gateway path is the whitelist proxy, which
 * carries request/response pairs, not a long-lived body. The trusted main window therefore owns the
 * one subscription and relays each notice to the other renderers of the same app over a
 * BroadcastChannel — same origin, no credential, and no new IPC surface for a window that is
 * deliberately not trusted.
 */
export const COMPANION_EVENT_CHANNEL = 'tinadec.companion-events'

export interface CompanionEventBroadcast {
  type: 'notice'
  notice: CompanionEventNotice
}

/** A relayed message is untrusted input like any other: check the shape before acting on it. */
export function isCompanionEventBroadcast(value: unknown): value is CompanionEventBroadcast {
  if (!value || typeof value !== 'object') return false
  const message = value as { type?: unknown; notice?: unknown }
  if (message.type !== 'notice' || !message.notice || typeof message.notice !== 'object') return false
  const notice = message.notice as Partial<CompanionEventNotice>
  return typeof notice.type === 'string'
    && (COMPANION_EVENT_TYPES as readonly string[]).includes(notice.type)
    && typeof notice.seq === 'number'
    && (notice.kind === null || typeof notice.kind === 'string')
    && (notice.companionId === null || typeof notice.companionId === 'string')
    && (notice.conversationId === null || typeof notice.conversationId === 'string')
    && (notice.runId === null || typeof notice.runId === 'string')
}
