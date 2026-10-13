import { describe, expect, it } from 'vitest'
import type { MessageDto } from '@/api'
import { applyEvents, createFeedState } from './reducer'
import { buildTurns } from './turns'
import { FEED_FIXTURES, failedRun, pendingApproval, simpleAnswer } from './fixtures'

const msg = (id: string, role: 'user' | 'assistant', created_at: string, run_id: string | null = null) =>
  ({ id, role, content: id, created_at, run_id, session_id: 's', attachments: [] }) as MessageDto

function feedOf(events: Parameters<typeof applyEvents>[1]) {
  const feed = createFeedState()
  applyEvents(feed, events)
  return feed
}

describe('buildTurns', () => {
  it('pairs a run with its trigger message and the answer with its run', () => {
    const fixture = simpleAnswer()
    const [turn, ...rest] = buildTurns({ messages: fixture.messages, runs: fixture.runs, feed: feedOf(fixture.events) })
    expect(rest).toEqual([])
    expect(turn!.user?.id).toBe('m-1')
    expect(turn!.runs.map((r) => r.run.id)).toEqual(['run-1'])
    expect(turn!.runs[0]!.activity?.entries.length).toBeGreaterThan(0)
    expect(turn!.answers.map((a) => a.id)).toEqual(['m-2'])
    expect(turn!.phase).toBe('settled')
    expect(turn!.endedAt).not.toBeNull()
  })

  it('uses the trigger even when timing would point elsewhere', () => {
    const turns = buildTurns({
      messages: [msg('u1', 'user', '2026-10-13T09:00:00Z'), msg('u2', 'user', '2026-10-13T09:05:00Z')],
      runs: [{ id: 'r', status: 'completed', trigger_message_id: 'u1', created_at: '2026-10-13T09:06:00Z' }],
      feed: createFeedState(),
    })
    expect(turns.map((t) => t.runs.map((r) => r.run.id))).toEqual([['r'], []])
  })

  it('places a run without a visible trigger after the latest earlier turn, else alone', () => {
    const turns = buildTurns({
      messages: [msg('u1', 'user', '2026-10-13T09:00:00Z')],
      runs: [
        { id: 'early', status: 'completed', trigger_message_id: null, created_at: '2026-10-13T08:00:00Z' },
        { id: 'late', status: 'completed', trigger_message_id: 'gone', created_at: '2026-10-13T09:01:00Z' },
      ],
      feed: createFeedState(),
    })
    expect(turns.map((t) => [t.user?.id ?? null, t.runs.map((r) => r.run.id)])).toEqual([[null, ['early']], ['u1', ['late']]])
  })

  it('puts a run known only by id (just admitted) into the newest turn', () => {
    const turns = buildTurns({
      messages: [msg('u1', 'user', '2026-10-13T09:00:00Z'), msg('pending-2', 'user', '2026-10-13T09:05:00Z')],
      runs: [{ id: 'old', status: 'completed', trigger_message_id: 'u1', created_at: '2026-10-13T09:00:01Z' }, { id: 'new', status: 'planning' }],
      feed: createFeedState(),
    })
    expect(turns.map((t) => [t.user?.id, t.runs.map((r) => r.run.id), t.phase])).toEqual([['u1', ['old'], 'settled'], ['pending-2', ['new'], 'running']])
  })

  it('derives the phase: sending, awaiting, running, failed, cancelled', () => {
    const sending = buildTurns({ messages: [msg('pending-1', 'user', '2026-10-13T09:00:00Z')], runs: [], feed: createFeedState() })
    expect(sending[0]!.phase).toBe('sending')
    const pending = pendingApproval()
    expect(buildTurns({ messages: pending.messages, runs: pending.runs, feed: feedOf(pending.events) })[0]!.phase).toBe('awaiting')
    const failed = failedRun()
    const failedTurn = buildTurns({ messages: failed.messages, runs: failed.runs, feed: feedOf(failed.events) })[0]!
    expect(failedTurn.phase).toBe('failed')
    expect(failedTurn.runs[0]!.activity?.failure?.category).toBe('model_unavailable')
    const base = { messages: [msg('u', 'user', '2026-10-13T09:00:00Z')], feed: createFeedState() }
    expect(buildTurns({ ...base, runs: [{ id: 'r', status: 'executing', trigger_message_id: 'u' }] })[0]!.phase).toBe('running')
    expect(buildTurns({ ...base, runs: [{ id: 'r', status: 'cancelled', trigger_message_id: 'u' }] })[0]!.phase).toBe('cancelled')
    expect(buildTurns({ ...base, runs: [{ id: 'r', status: 'executing', trigger_message_id: 'u' }] })[0]!.endedAt).toBeNull()
  })

  it('carries streaming text per run and attaches a run-less answer by time', () => {
    const turns = buildTurns({
      messages: [msg('u', 'user', '2026-10-13T09:00:00Z'), msg('a', 'assistant', '2026-10-13T09:00:05Z')],
      runs: [{ id: 'r', status: 'executing', trigger_message_id: 'u' }],
      feed: createFeedState(),
      streaming: new Map([['r', 'Hel']]),
    })
    expect(turns[0]!.runs[0]!.streamingText).toBe('Hel')
    expect(turns[0]!.answers.map((a) => a.id)).toEqual(['a'])
  })

  it('builds every shared fixture into turns without losing a message', () => {
    for (const make of FEED_FIXTURES) {
      const fixture = make()
      const turns = buildTurns({ messages: fixture.messages, runs: fixture.runs, feed: feedOf(fixture.events) })
      const placed = turns.flatMap((t) => [t.user, ...t.answers]).filter(Boolean).length
      expect(placed, fixture.name).toBe(fixture.messages.length)
    }
  })
})
