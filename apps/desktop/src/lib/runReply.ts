import type { SseChunk } from '@/generated/client'
import { runStreamDelta } from './runStream'

export interface RunReply { text: string; provisional: boolean }

/** Preview output is replaceable; the terminal, governed delta is the authoritative answer. */
export function projectRunReply(previous: RunReply, chunk: SseChunk): RunReply | null {
  if (chunk.kind === 'answer.started' || chunk.kind === 'answer.failed') return { text: '', provisional: true }
  if (chunk.kind !== 'answer.delta' && chunk.kind !== 'delta') return null
  const delta = runStreamDelta(chunk)
  return {
    text: chunk.kind === 'delta' && previous.provisional ? delta : previous.text + delta,
    provisional: chunk.kind === 'answer.delta',
  }
}
