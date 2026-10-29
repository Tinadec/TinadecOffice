import { describe, expect, it } from 'vitest'
import type { CompanionActivity, CompanionConversation } from './companionApi'
import {
  attentionCounts,
  conversationOrigin,
  feedRows,
  groupByDay,
  groupSections,
  isAutonomous,
  needsAttention,
  relativeTime,
} from './companionView'

function activity(overrides: Partial<CompanionActivity> & { created_at: string }): CompanionActivity {
  return { id: overrides.id ?? overrides.created_at, kind: 'message_sent', ...overrides } as CompanionActivity
}

function conversation(overrides: Partial<CompanionConversation> & { id: string }): CompanionConversation {
  return {
    kind: 'group', title: overrides.id, autonomy_paused: false, message_count: 0,
    turn_budget: 6, origin: 'user', can_open: true, members: [],
    ...overrides,
  } as CompanionConversation
}

const MEMBER_ALPHA = { display_name: 'Alpha', member_kind: 'companion' as const, companion_id: 'c-a', status: 'active' as const, joined_at: '2026-10-01T00:00:00Z' }
const MEMBER_BETA = { display_name: 'Beta', member_kind: 'companion' as const, companion_id: 'c-b', status: 'active' as const, joined_at: '2026-10-01T00:00:00Z' }
const MEMBER_YOU = { display_name: 'You', member_kind: 'user' as const, status: 'active' as const, joined_at: '2026-10-01T00:00:00Z' }

describe('companion feed projection', () => {
  it('sorts newest first and keeps an unreadable timestamp at the bottom', () => {
    const rows = [
      activity({ id: 'old', created_at: '2026-10-01T00:00:00Z' }),
      activity({ id: 'broken', created_at: 'not a date' }),
      activity({ id: 'new', created_at: '2026-10-02T00:00:00Z' }),
    ]
    expect(feedRows(rows).map((row) => row.id)).toEqual(['new', 'old', 'broken'])
  })

  it('filters by companion, by attention and by autonomous activity', () => {
    const rows = [
      activity({ id: 'mine', created_at: '2026-10-01T00:00:00Z', companion_id: 'c-a' }),
      activity({ id: 'other', created_at: '2026-10-01T00:00:01Z', companion_id: 'c-b' }),
      activity({ id: 'failed', created_at: '2026-10-01T00:00:02Z', companion_id: 'c-a', kind: 'run_failed' }),
      activity({ id: 'auto', created_at: '2026-10-01T00:00:03Z', companion_id: 'c-a', kind: 'autonomy_message_sent' }),
    ]
    expect(feedRows(rows, { companionId: 'c-a' }).map((row) => row.id)).toEqual(['auto', 'failed', 'mine'])
    expect(feedRows(rows, { filter: 'attention' }).map((row) => row.id)).toEqual(['failed'])
    expect(feedRows(rows, { filter: 'autonomous' }).map((row) => row.id)).toEqual(['auto'])
  })

  it('decides "groups" from the conversation index instead of guessing from the kind', () => {
    const rows = [
      activity({ id: 'in-group', created_at: '2026-10-01T00:00:00Z', conversation_id: 'conv-group' }),
      activity({ id: 'private', created_at: '2026-10-01T00:00:01Z', conversation_id: 'conv-private' }),
      activity({ id: 'no-conversation', created_at: '2026-10-01T00:00:02Z' }),
    ]
    const kinds = new Map([['conv-group', 'group'], ['conv-private', 'private']])
    expect(feedRows(rows, { filter: 'groups', conversationKinds: kinds }).map((row) => row.id)).toEqual(['in-group'])
  })

  it('groups by local day in the order rows already have', () => {
    const rows = [
      activity({ id: 'b', created_at: '2026-10-02T12:00:00' }),
      activity({ id: 'a', created_at: '2026-10-02T08:00:00' }),
      activity({ id: 'yesterday', created_at: '2026-10-01T22:00:00' }),
    ]
    const days = groupByDay(rows)
    expect(days.map((day) => day.key)).toEqual(['2026-10-02', '2026-10-01'])
    expect(days[0]!.rows.map((row) => row.id)).toEqual(['b', 'a'])
  })

  it('counts failures per companion and treats a chat message as nothing to act on', () => {
    const counts = attentionCounts([
      activity({ id: '1', created_at: '2026-10-01T00:00:00Z', companion_id: 'c-a', kind: 'run_failed' }),
      activity({ id: '2', created_at: '2026-10-01T00:00:01Z', companion_id: 'c-a', kind: 'run_failed' }),
      activity({ id: '3', created_at: '2026-10-01T00:00:02Z', companion_id: 'c-b', kind: 'message_sent' }),
    ])
    expect(counts.get('c-a')).toBe(2)
    expect(counts.has('c-b')).toBe(false)
    expect(needsAttention(activity({ created_at: '2026-10-01T00:00:00Z', kind: 'message_sent' }))).toBe(false)
    expect(isAutonomous(activity({ created_at: '2026-10-01T00:00:00Z', kind: 'autonomy_group_created' }))).toBe(true)
  })
})

describe('companion group projection', () => {
  it('splits groups by who opened them and keeps the newest first', () => {
    const sections = groupSections([
      conversation({ id: 'mine', origin: 'user', last_message_at: '2026-10-01T00:00:00Z' }),
      conversation({ id: 'theirs', origin: 'companion', last_message_at: '2026-10-02T00:00:00Z' }),
    ], null)
    expect(sections.map((section) => section.origin)).toEqual(['user', 'companion'])
    expect(sections[0]!.rows.map((row) => row.id)).toEqual(['mine'])
    expect(sections[1]!.rows.map((row) => row.id)).toEqual(['theirs'])
  })

  it('narrows to the selected companion and drops conversations the user cannot open', () => {
    const sections = groupSections([
      conversation({ id: 'with-alpha', members: [MEMBER_YOU, MEMBER_ALPHA] }),
      conversation({ id: 'with-beta', members: [MEMBER_YOU, MEMBER_BETA] }),
      conversation({ id: 'closed', can_open: false, members: [MEMBER_YOU, MEMBER_ALPHA] }),
    ], 'c-a')
    expect(sections.flatMap((section) => section.rows.map((row) => row.id))).toEqual(['with-alpha'])
  })

  it('treats a conversation without an origin as user-started', () => {
    expect(conversationOrigin(conversation({ id: 'legacy', origin: undefined }))).toBe('user')
    expect(conversationOrigin(conversation({ id: 'auto', origin: 'companion' }))).toBe('companion')
  })

  it('omits an empty section instead of pinning an empty heading', () => {
    const sections = groupSections([conversation({ id: 'mine' })], null)
    expect(sections).toHaveLength(1)
    expect(sections[0]!.origin).toBe('user')
  })
})

describe('relative time', () => {
  const now = Date.parse('2026-10-02T12:00:00Z')

  it('uses the platform relative formatter per locale', () => {
    expect(relativeTime('2026-10-02T11:58:00Z', 'zh-CN', now)).toContain('2')
    expect(relativeTime('2026-10-02T11:58:00Z', 'en', now)).toContain('2')
    // `numeric: 'auto'` is what makes a whole day read as "yesterday" instead of "-1 day".
    expect(relativeTime('2026-10-01T12:00:00Z', 'en', now)).toBe('yesterday')
  })

  it('returns empty text for a missing or unreadable stamp', () => {
    expect(relativeTime(null, 'en', now)).toBe('')
    expect(relativeTime('nope', 'en', now)).toBe('')
  })
})
