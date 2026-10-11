import { describe, expect, it } from 'vitest'
import { conversationTurns, type TranscriptRun } from './conversationTurns'
import type { MessageDto } from '@/api'

function message(over: Partial<MessageDto> & { id: string; role: string }): MessageDto {
  return { session_id: 's1', content: '', created_at: null, attachments: [], run_id: null, ...over } as MessageDto
}

function run(id: string, status: string, trigger?: string | null): TranscriptRun {
  return { id, status, trigger_message_id: trigger ?? null }
}

const activity = (runId: string) => ({ runId, thinkingSteps: [], toolCalls: [], supervisionReview: null })

describe('conversationTurns', () => {
  it('returns nothing for an empty session', () => {
    expect(conversationTurns({ messages: [], runs: [] })).toEqual([])
  })

  it('opens a turn for a user message that has no reply yet', () => {
    const turns = conversationTurns({
      messages: [message({ id: 'u1', role: 'user', content: '做点事' })],
      runs: [],
    })
    expect(turns).toHaveLength(1)
    expect(turns[0]).toMatchObject({ key: 'u1', answer: null, status: 'running', runId: null })
  })

  /**
   * Core puts no run on the user message (`SubmitInteractionAsync` passes only a
   * clientMessageId), so the answer's run is what links the two rows.
   */
  it('pairs an answer onto the turn by the run the answer carries', () => {
    const turns = conversationTurns({
      messages: [
        message({ id: 'u1', role: 'user' }),
        message({ id: 'a1', role: 'assistant', run_id: 'r1', content: '做完了' }),
      ],
      runs: [run('r1', 'completed')],
    })
    expect(turns).toHaveLength(1)
    expect(turns[0]).toMatchObject({ key: 'u1', runId: 'r1', status: 'settled' })
    expect(turns[0].answer?.id).toBe('a1')
  })

  /**
   * Core's `sequence` is monotonic per session, so an answer can never precede the message
   * that triggered its run — a fixture that does is a shape Core cannot produce. This states
   * the rule the exact link exists for: a run admitted for the *newer* message must not also
   * claim the turn the reader has already read.
   */
  it('does not let a run admitted for a later message claim an earlier turn', () => {
    const turns = conversationTurns({
      messages: [
        message({ id: 'u1', role: 'user' }),
        message({ id: 'a1', role: 'assistant', run_id: 'r0' }),
        message({ id: 'u2', role: 'user' }),
      ],
      // r1 belongs to u2. u1's turn already ended with a1 under r0, so r1 must stay off it.
      runs: [run('r0', 'completed', 'u1'), run('r1', 'executing', 'u2')],
    })
    const first = turns.find((turn) => turn.key === 'u1')
    const second = turns.find((turn) => turn.key === 'u2')
    expect(first?.runId).toBe('r0')
    expect(second?.runId).toBe('r1')
    expect(first?.status).toBe('settled')
    expect(second?.status).toBe('running')
  })

  it('keeps tool_evidence inside the turn it belongs to and never answers with it', () => {
    const turns = conversationTurns({
      messages: [
        message({ id: 'u1', role: 'user' }),
        message({ id: 'e1', role: 'tool_evidence', run_id: 'r1' }),
        message({ id: 'a1', role: 'assistant', run_id: 'r1' }),
      ],
      runs: [run('r1', 'completed')],
    })
    expect(turns).toHaveLength(1)
    expect(turns[0].runId).toBe('r1')
    expect(turns[0].answer?.id).toBe('a1')
  })

  it('splits a multi-turn session at each user message', () => {
    const turns = conversationTurns({
      messages: [
        message({ id: 'u1', role: 'user' }),
        message({ id: 'a1', role: 'assistant', run_id: 'r1' }),
        message({ id: 'u2', role: 'user' }),
        message({ id: 'a2', role: 'assistant', run_id: 'r2' }),
      ],
      runs: [run('r1', 'completed'), run('r2', 'completed')],
    })
    expect(turns.map((turn) => turn.key)).toEqual(['u1', 'u2'])
    expect(turns.map((turn) => turn.runId)).toEqual(['r1', 'r2'])
  })

  /**
   * The case the old message-anchored rendering could not draw at all: a run working before
   * its assistant message exists has no message id to hang on.
   */
  it('appends a live turn for a run no message has claimed', () => {
    const turns = conversationTurns({
      messages: [message({ id: 'u1', role: 'user' })],
      runs: [run('r1', 'executing')],
      activities: { r1: activity('r1') },
    })
    expect(turns.map((turn) => turn.key)).toEqual(['u1', 'r1'])
    expect(turns[1]).toMatchObject({ userMessage: null, answer: null, status: 'running' })
  })

  it('does not invent a turn for an idle run that produced nothing', () => {
    const turns = conversationTurns({
      messages: [message({ id: 'u1', role: 'user' })],
      runs: [run('r1', 'planning')],
    })
    expect(turns).toHaveLength(1)
  })

  it('keeps two parallel runs apart, each with its own streaming text', () => {
    const turns = conversationTurns({
      messages: [message({ id: 'u1', role: 'user' })],
      runs: [run('r1', 'executing'), run('r2', 'executing')],
      activities: { r1: activity('r1'), r2: activity('r2') },
      streaming: { r1: '第一个', r2: '第二个' },
    })
    expect(turns).toHaveLength(3)
    expect(turns[1]).toMatchObject({ key: 'r1', streamingText: '第一个' })
    expect(turns[2]).toMatchObject({ key: 'r2', streamingText: '第二个' })
  })

  it('leaves a turn running while its run is parked awaiting a decision', () => {
    const turns = conversationTurns({
      messages: [message({ id: 'u1', role: 'user' })],
      runs: [run('r1', 'awaiting_user')],
      activities: { r1: activity('r1') },
    })
    const live = turns.find((turn) => turn.key === 'r1')
    expect(live?.status).toBe('running')
  })

  it('settles a turn whose run failed even though an answer was persisted', () => {
    const turns = conversationTurns({
      messages: [
        message({ id: 'u1', role: 'user' }),
        message({ id: 'a1', role: 'assistant', run_id: 'r1' }),
      ],
      runs: [run('r1', 'failed')],
    })
    expect(turns[0].status).toBe('settled')
  })

  it('produces a turn from a trailing row whose user message was cut away', () => {
    const turns = conversationTurns({
      messages: [message({ id: 'a1', role: 'assistant', run_id: 'r1', content: '历史' })],
      runs: [run('r1', 'completed')],
    })
    expect(turns).toHaveLength(1)
    expect(turns[0]).toMatchObject({ userMessage: null, answer: { id: 'a1' }, status: 'settled' })
  })
})
