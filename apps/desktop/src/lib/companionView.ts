import type { CompanionActivity, CompanionConversation } from './companionApi'

/**
 * Pure projections for the companion centre. Kept out of the cards so the filtering rules can be
 * tested without mounting anything, and so the feed and the list agree on what "needs attention"
 * means.
 */

/** Kinds the user is expected to act on. A chat message is never one of them. */
const ATTENTION_KINDS = new Set(['run_failed'])

/** Kinds a companion produced on its own schedule rather than in reply to the user. */
const AUTONOMOUS_KINDS = new Set([
  'autonomy_message_sent',
  'autonomy_group_created',
  'autonomy_invited',
  'autonomy_skipped',
])

export type CompanionFeedFilter = 'all' | 'attention' | 'groups' | 'autonomous'

export function needsAttention(row: CompanionActivity): boolean {
  return ATTENTION_KINDS.has(row.kind)
}

export function isAutonomous(row: CompanionActivity): boolean {
  return AUTONOMOUS_KINDS.has(row.kind) || row.kind.startsWith('autonomy_')
}

/** Milliseconds for a timestamp, or null when it is missing or unreadable. */
export function activityTime(row: CompanionActivity): number | null {
  const parsed = Date.parse(row.created_at)
  return Number.isFinite(parsed) ? parsed : null
}

export interface CompanionFeedOptions {
  companionId?: string | null
  filter?: CompanionFeedFilter
  /** Conversation id → kind, so "groups" can be decided without a second request. */
  conversationKinds?: Map<string, string>
}

/** Newest first; an unreadable timestamp sorts last instead of jumping to the top. */
export function feedRows(rows: CompanionActivity[], options: CompanionFeedOptions = {}): CompanionActivity[] {
  const filter = options.filter ?? 'all'
  const filtered = rows.filter((row) => {
    if (options.companionId && row.companion_id !== options.companionId) return false
    if (filter === 'attention') return needsAttention(row)
    if (filter === 'autonomous') return isAutonomous(row)
    if (filter === 'groups') {
      if (!row.conversation_id) return false
      return options.conversationKinds?.get(row.conversation_id) === 'group'
    }
    return true
  })
  return [...filtered].sort((left, right) => {
    const a = activityTime(left)
    const b = activityTime(right)
    if (a === null) return 1
    if (b === null) return -1
    return b - a
  })
}

export interface CompanionFeedDay {
  /** Local calendar day, `YYYY-MM-DD`. */
  key: string
  rows: CompanionActivity[]
}

function dayKey(date: Date): string {
  const month = `${date.getMonth() + 1}`.padStart(2, '0')
  const day = `${date.getDate()}`.padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}

/** Groups an already sorted row list by local day, preserving order inside each day. */
export function groupByDay(rows: CompanionActivity[]): CompanionFeedDay[] {
  const days: CompanionFeedDay[] = []
  for (const row of rows) {
    const time = activityTime(row)
    const date = time === null ? null : new Date(time)
    const key = date === null ? 'unknown' : dayKey(date)
    const last = days.at(-1)
    if (last?.key === key) last.rows.push(row)
    else days.push({ key, rows: [row] })
  }
  return days
}

/**
 * Relative label for a timeline row. Uses the platform's own relative formatter, so the five
 * locales get their plural and word-order rules without a table of hand-written strings.
 */
export function relativeTime(iso: string | null | undefined, locale: string, now: number = Date.now()): string {
  if (!iso) return ''
  const time = Date.parse(iso)
  if (!Number.isFinite(time)) return ''
  const seconds = Math.round((time - now) / 1000)
  const format = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })
  const abs = Math.abs(seconds)
  if (abs < 60) return format.format(Math.round(seconds), 'second')
  if (abs < 3600) return format.format(Math.round(seconds / 60), 'minute')
  if (abs < 86_400) return format.format(Math.round(seconds / 3600), 'hour')
  if (abs < 604_800) return format.format(Math.round(seconds / 86_400), 'day')
  return format.format(Math.round(seconds / 604_800), 'week')
}

/** `origin` is authoritative; a conversation without one predates the column and was user-started. */
export function conversationOrigin(conversation: CompanionConversation): 'user' | 'companion' {
  return conversation.origin === 'companion' ? 'companion' : 'user'
}

export function hasCompanion(conversation: CompanionConversation, companionId: string): boolean {
  return conversation.members.some((member) =>
    member.member_kind === 'companion' && member.companion_id === companionId && member.status === 'active')
}

/** Group conversations the centre shows, narrowest first: selected companion, then both classes. */
export function groupSections(
  conversations: CompanionConversation[],
  companionId: string | null,
): Array<{ origin: 'user' | 'companion'; rows: CompanionConversation[] }> {
  const scoped = conversations
    .filter((conversation) => conversation.kind === 'group')
    .filter((conversation) => (companionId ? hasCompanion(conversation, companionId) : true))
    .filter((conversation) => conversation.can_open !== false)
    .sort((left, right) => {
      const a = left.last_message_at ? Date.parse(left.last_message_at) : Number.NEGATIVE_INFINITY
      const b = right.last_message_at ? Date.parse(right.last_message_at) : Number.NEGATIVE_INFINITY
      return b - a
    })
  const mine = scoped.filter((conversation) => conversationOrigin(conversation) === 'user')
  const theirs = scoped.filter((conversation) => conversationOrigin(conversation) === 'companion')
  return [
    { origin: 'user' as const, rows: mine },
    { origin: 'companion' as const, rows: theirs },
  ].filter((section) => section.rows.length > 0)
}

/** Attention items per companion, used for the badge in the list. */
export function attentionCounts(rows: CompanionActivity[]): Map<string, number> {
  const counts = new Map<string, number>()
  for (const row of rows) {
    if (!needsAttention(row) || !row.companion_id) continue
    counts.set(row.companion_id, (counts.get(row.companion_id) ?? 0) + 1)
  }
  return counts
}
