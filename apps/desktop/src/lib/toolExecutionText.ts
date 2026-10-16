import type { ToolExecutionTimelineItemDto } from '../api'
import { errorCategoryMessage, type ErrorWords } from './apiError'

type Execution = Pick<ToolExecutionTimelineItemDto, 'tool_id' | 'tool_display_name' | 'status' | 'tool_success' | 'error_category' | 'summary'>

/**
 * One line on what a tool call did, from its status and facts. Core's `summary` is the event log's
 * English narration of the last event; it is shown only for a status this build has no words for.
 */
export function toolExecutionSummary(execution: Execution, words: ErrorWords): string {
  const tool = execution.tool_display_name || execution.tool_id
  if (execution.status === 'completed' && execution.tool_success === false) return words.t('toolPanel.summary.completedWithFailure', { tool })
  if (execution.status === 'failed') {
    const reason = errorCategoryMessage(execution.error_category, words)
    return reason ? words.t('toolPanel.summary.failedWith', { tool, reason }) : words.t('toolPanel.summary.failed', { tool })
  }
  const key = `toolPanel.summary.${execution.status}`
  return words.te(key) ? words.t(key, { tool }) : execution.summary
}
