import { describe, expect, it } from 'vitest'
import { crossesDay, dayLabel, elapsedMs, feedLocale, formatClock, formatDuration } from './format'

describe('formatDuration', () => {
  it.each([
    [0, '<1 秒', '<1s'],
    [999, '<1 秒', '<1s'],
    [1_000, '1 秒', '1s'],
    [12_400, '12 秒', '12s'],
    [80_000, '1 分 20 秒', '1m 20s'],
    [120_000, '2 分', '2m'],
    [3_600_000, '1 小时', '1h'],
    [3_900_000, '1 小时 5 分', '1h 5m'],
  ])('%i ms', (ms, zh, en) => {
    expect(formatDuration(ms, 'zh-CN')).toBe(zh)
    expect(formatDuration(ms, 'en')).toBe(en)
  })

  it('treats an unreadable value as under a second', () => {
    expect(formatDuration(Number.NaN, 'en')).toBe('<1s')
  })
})

describe('elapsedMs', () => {
  it('measures between ISO timestamps and refuses missing ends', () => {
    expect(elapsedMs('2026-10-13T10:00:00Z', '2026-10-13T10:01:20Z')).toBe(80_000)
    expect(elapsedMs('2026-10-13T10:00:00Z', null)).toBeNull()
    expect(elapsedMs('nope', '2026-10-13T10:00:00Z')).toBeNull()
    expect(elapsedMs('2026-10-13T10:00:05Z', '2026-10-13T10:00:00Z')).toBe(0)
  })
})

describe('day labels', () => {
  const now = new Date(2026, 9, 13, 15, 0)
  const at = (month: number, day: number, year = 2026) => new Date(year, month, day, 9, 30).toISOString()

  it('says today, yesterday, a weekday within the week, then a date', () => {
    expect(dayLabel(at(9, 13), now, 'zh-CN')).toBe('今天')
    expect(dayLabel(at(9, 12), now, 'en')).toBe('Yesterday')
    expect(dayLabel(at(9, 10), now, 'en')).toBe('Saturday')
    expect(dayLabel(at(9, 10), now, 'zh-CN')).toBe('星期六')
    expect(dayLabel(at(8, 1), now, 'en')).toBe('September 1')
    expect(dayLabel(at(11, 31, 2025), now, 'en')).toBe('December 31, 2025')
  })

  it('detects a local calendar-day boundary between neighbours', () => {
    expect(crossesDay(new Date(2026, 9, 12, 23, 59).toISOString(), new Date(2026, 9, 13, 0, 1).toISOString())).toBe(true)
    expect(crossesDay(new Date(2026, 9, 13, 8).toISOString(), new Date(2026, 9, 13, 22).toISOString())).toBe(false)
    expect(crossesDay(null, now.toISOString())).toBe(false)
  })
})

describe('clock and locale', () => {
  it('formats a 24-hour clock and maps locales', () => {
    expect(formatClock(new Date(2026, 9, 13, 14, 32).toISOString(), 'en')).toBe('14:32')
    expect(formatClock('bad', 'en')).toBe('')
    expect(feedLocale('zh-CN')).toBe('zh-CN')
    expect(feedLocale('en-US')).toBe('en')
    expect(feedLocale(undefined)).toBe('en')
  })
})
