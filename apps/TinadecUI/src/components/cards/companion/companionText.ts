import type { Translate } from '@/i18n'
import { ApiError } from '@/lib/apiError'

// ---------------------------------------------------------------------------
// Shared text helpers for the companion-centre cards.
//
// The cards no longer poll for themselves — `@/lib/companionStore` owns the one
// poll and hands every card the same snapshot. What is left here is the honest
// failure text and the timestamp formatting they share.
// ---------------------------------------------------------------------------

/** Honest failure text: the code is shown, never replaced by a friendlier claim. */
export function companionErrorText(failure: unknown, t: Translate): string {
  if (failure instanceof ApiError) {
    if (failure.code === 'desktop_host_required') return t('companion.center.errorHostRequired')
    if (failure.code === 'backend_network_unavailable') {
      return t('companion.center.errorNetwork', { reason: failure.message })
    }
    if (failure.code) return t('companion.center.errorApi', { code: failure.code, message: failure.message })
    return failure.message
  }
  return failure instanceof Error ? failure.message : String(failure)
}

/** Local date + time for rows that want an absolute stamp; empty when unreadable. */
export function formatCompanionTime(iso: string | null | undefined, locale: string): string {
  if (!iso) return ''
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleString(locale, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  })
}
