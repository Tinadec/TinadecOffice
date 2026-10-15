/**
 * Time formatting for the conversation feed: elapsed durations ("1 分 20 秒" / "1m 20s"),
 * message clock times and the day label of a date separator. Pure: the words come from the
 * caller's `t` (`conversation.duration.*`, `conversation.day.*`) and dates from Intl in the
 * caller's locale, so no language is spelled out here.
 */
import type { Translate } from '@/i18n'

/**
 * Elapsed time at the precision a reader needs: seconds under a minute, minutes and seconds
 * under an hour, hours and minutes beyond. A zero trailing unit is dropped ("2 分", "1h").
 */
export function formatDuration(ms: number, t: Translate): string {
  if (!Number.isFinite(ms) || ms < 1000) return t('conversation.duration.underSecond')
  const totalSeconds = Math.floor(ms / 1000)
  if (totalSeconds < 60) return t('conversation.duration.seconds', { s: totalSeconds })
  const totalMinutes = Math.floor(totalSeconds / 60)
  if (totalMinutes < 60) {
    const s = totalSeconds % 60
    return s ? t('conversation.duration.minutesSeconds', { m: totalMinutes, s }) : t('conversation.duration.minutes', { m: totalMinutes })
  }
  const h = Math.floor(totalMinutes / 60)
  const m = totalMinutes % 60
  return m ? t('conversation.duration.hoursMinutes', { h, m }) : t('conversation.duration.hours', { h })
}

/** Elapsed milliseconds between two ISO timestamps; null when either is missing or unreadable. */
export function elapsedMs(startIso: string | null | undefined, endIso: string | null | undefined): number | null {
  if (!startIso || !endIso) return null
  const start = Date.parse(startIso)
  const end = Date.parse(endIso)
  return Number.isFinite(start) && Number.isFinite(end) ? Math.max(0, end - start) : null
}

/** Local wall-clock time, 24-hour, e.g. "14:32". */
export function formatClock(iso: string, locale: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit', hour12: false })
}

function startOfDay(date: Date): number {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime()
}

/** True when two timestamps fall on different local calendar days. */
export function crossesDay(previousIso: string | null | undefined, nextIso: string): boolean {
  if (!previousIso) return false
  const previous = new Date(previousIso)
  const next = new Date(nextIso)
  if (Number.isNaN(previous.getTime()) || Number.isNaN(next.getTime())) return false
  return startOfDay(previous) !== startOfDay(next)
}

/**
 * The label of a date separator: today, yesterday, a weekday within the last week, then a
 * month-day date, with the year only when it differs from now.
 */
export function dayLabel(iso: string, now: Date, locale: string, t: Translate): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  const days = Math.round((startOfDay(now) - startOfDay(date)) / 86_400_000)
  if (days === 0) return t('conversation.day.today')
  if (days === 1) return t('conversation.day.yesterday')
  if (days > 1 && days < 7) return date.toLocaleDateString(locale, { weekday: 'long' })
  return date.toLocaleDateString(locale, date.getFullYear() === now.getFullYear()
    ? { month: 'long', day: 'numeric' }
    : { year: 'numeric', month: 'long', day: 'numeric' })
}
