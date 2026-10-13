import type { FeedEntry } from '@/lib/conversation/reducer'
import { latestReasoningTitle } from '@/lib/conversation/reasoning'
import { entrySentence, verbKey } from '@/lib/conversation/toolSentence'

type Translate = (key: string, named?: Record<string, unknown>) => string

/**
 * One line saying what an entry is doing or did ("Reading src/graph.ts", "Checking callers"),
 * for the "currently doing" line of a sub-agent card. Null for entries that have no step to say.
 */
export function describeEntry(entry: FeedEntry | null, t: Translate, workspaceRoot?: string | null): string | null {
  if (!entry) return null
  switch (entry.kind) {
    case 'tool': {
      const sentence = entrySentence(entry, workspaceRoot)
      const failed = entry.status === 'failed' || entry.status === 'outcome_unknown' || entry.toolSuccess === false
      const phase = failed ? 'failed' : entry.status === 'completed' ? 'done' : 'active'
      return t(verbKey(sentence.verb, phase), { subject: sentence.subject ?? entry.toolId })
    }
    case 'reasoning':
      return latestReasoningTitle(entry.text) ?? t('conversation.reasoning.thinking')
    case 'wait':
      return t(entry.status === 'waiting' ? 'conversation.wait.waiting' : 'conversation.wait.resolved', {
        names: entry.awaited.map((item) => item.handle ?? item.taskKey).filter(Boolean).join(t('conversation.listSeparator')),
      })
    case 'plan': {
      const current = entry.steps.find((step) => step.status === 'in_progress')
      return current?.step ?? null
    }
    default:
      return null
  }
}
