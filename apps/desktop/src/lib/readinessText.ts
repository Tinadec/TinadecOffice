import type { ReadinessItemDto } from '../api'
import type { ErrorWords } from './apiError'

/** The item's facts (provider name, durations, counts…) as named values for its wording. */
function facts(item: ReadinessItemDto): Record<string, string | number> {
  const named: Record<string, string | number> = {}
  for (const [name, value] of Object.entries(item.data ?? {})) if (typeof value === 'string' || typeof value === 'number') named[name] = value
  return named
}

/** A readiness reason in the interface language from its code; Core's English text when this build has no words for it. */
export function readinessReason(item: ReadinessItemDto, words: ErrorWords): string | null {
  const key = `doctor.reason.${item.reason_code}`
  return item.reason_code && words.te(key) ? words.t(key, facts(item)) : item.reason ?? null
}

/** An action only where Core names one (ready items carry none), worded the same way. */
export function readinessAction(item: ReadinessItemDto, words: ErrorWords): string | null {
  if (!item.action) return null
  const key = `doctor.action.${item.reason_code}`
  return item.reason_code && words.te(key) ? words.t(key, facts(item)) : item.action
}
