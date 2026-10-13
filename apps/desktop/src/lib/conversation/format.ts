/**
 * Time formatting for the conversation feed: elapsed durations ("1 分 20 秒" / "1m 20s"),
 * message clock times and the day label of a date separator. Pure and locale-explicit so
 * the feed and its tests agree without an i18n instance; components pass the active locale.
 */

export type FeedLocale = 'zh-CN' | 'en'

const UNITS: Record<FeedLocale, { lessThanSecond: string; s: string; m: string; h: string; sep: string }> = {
  'zh-CN': { lessThanSecond: '<1 秒', s: ' 秒', m: ' 分', h: ' 小时', sep: ' ' },
  en: { lessThanSecond: '<1s', s: 's', m: 'm', h: 'h', sep: ' ' },
}

export function feedLocale(locale: string | null | undefined): FeedLocale {
  return locale?.toLowerCase().startsWith('zh') ? 'zh-CN' : 'en'
}

/**
 * Elapsed time at the precision a reader needs: seconds under a minute, minutes and seconds
 * under an hour, hours and minutes beyond. A zero trailing unit is dropped ("2 分", "1h").
 */
export function formatDuration(ms: number, locale: FeedLocale): string {
  const u = UNITS[locale]
  if (!Number.isFinite(ms) || ms < 1000) return u.lessThanSecond
  const totalSeconds = Math.floor(ms / 1000)
  if (totalSeconds < 60) return `${totalSeconds}${u.s}`
  const totalMinutes = Math.floor(totalSeconds / 60)
  if (totalMinutes < 60) {
    const seconds = totalSeconds % 60
    return seconds ? `${totalMinutes}${u.m}${u.sep}${seconds}${u.s}` : `${totalMinutes}${u.m}`
  }
  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  return minutes ? `${hours}${u.h}${u.sep}${minutes}${u.m}` : `${hours}${u.h}`
}

/** Elapsed milliseconds between two ISO timestamps; null when either is missing or unreadable. */
export function elapsedMs(startIso: string | null | undefined, endIso: string | null | undefined): number | null {
  if (!startIso || !endIso) return null
  const start = Date.parse(startIso)
  const end = Date.parse(endIso)
  return Number.isFinite(start) && Number.isFinite(end) ? Math.max(0, end - start) : null
}

/** Local wall-clock time, 24-hour, e.g. "14:32". */
export function formatClock(iso: string, locale: FeedLocale): string {
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

const RELATIVE: Record<FeedLocale, { today: string; yesterday: string }> = {
  'zh-CN': { today: '今天', yesterday: '昨天' },
  en: { today: 'Today', yesterday: 'Yesterday' },
}

/**
 * The label of a date separator: today, yesterday, a weekday within the last week, then a
 * month-day date, with the year only when it differs from now.
 */
export function dayLabel(iso: string, now: Date, locale: FeedLocale): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  const days = Math.round((startOfDay(now) - startOfDay(date)) / 86_400_000)
  if (days === 0) return RELATIVE[locale].today
  if (days === 1) return RELATIVE[locale].yesterday
  if (days > 1 && days < 7) return date.toLocaleDateString(locale, { weekday: 'long' })
  return date.toLocaleDateString(locale, date.getFullYear() === now.getFullYear()
    ? { month: 'long', day: 'numeric' }
    : { year: 'numeric', month: 'long', day: 'numeric' })
}
