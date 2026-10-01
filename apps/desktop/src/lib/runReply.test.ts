import { describe, it, expect } from 'vitest'
import { projectRunReply, type RunReply } from './runReply'
import type { SseChunk } from '@/generated/client'

describe('run reply projection', () => {
  it('resets retries and replaces provisional text with the single committed answer', () => {
    let state: RunReply = { text: '', provisional: false }
    const push = (kind: string, delta = '') => {
      const chunk: SseChunk = { kind, payload: { delta }, run_id: 'run-1', turn_id: 'turn-1',
        message_id: null, seq: 1, occurred_at: '2026-09-23T00:00:00Z' }
      state = projectRunReply(state, chunk)!
      return state.text
    }
    push('answer.started')
    expect(push('answer.delta', 'partial failure')).toBe('partial failure')
    expect(push('answer.failed')).toBe('')
    push('answer.started')
    expect(push('answer.delta', 'Hello ')).toBe('Hello ')
    expect(push('answer.delta', 'world')).toBe('Hello world')
    expect(push('delta', 'Hello world')).toBe('Hello world')
    expect(state.provisional).toBe(false)
  })
})
