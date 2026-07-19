import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resolveConfirmation, useNotifications } from './useNotifications'

const notifications = useNotifications()

beforeEach(() => vi.useFakeTimers())

afterEach(() => {
  for (const item of [...notifications.items.value]) notifications.dismiss(item.id)
  while (notifications.currentConfirmation.value) {
    resolveConfirmation(notifications.currentConfirmation.value.id, false)
  }
  notifications.closeDetail()
  notifications.setHovered(null)
  vi.useRealTimers()
})

describe('useNotifications', () => {
  it('prioritizes errors, persistent banners, then recent notifications', () => {
    const first = notifications.notify.info('first')
    const latest = notifications.notify.success('latest')
    const banner = notifications.banner.warning('persistent')
    const error = notifications.notify.error(new Error('failed'))

    expect(notifications.visibleItems.value.map((item) => item.id)).toEqual([error, banner, latest])
    expect(notifications.primaryId.value).toBe(error)
    expect(notifications.visibleItems.value.some((item) => item.id === first)).toBe(false)
  })

  it('shows at most three items and reports overflow', () => {
    for (let index = 0; index < 5; index++) notifications.notify.info(`item ${index}`)

    expect(notifications.visibleItems.value).toHaveLength(3)
    expect(notifications.overflowCount.value).toBe(2)
    expect(notifications.orderedItems.value).toHaveLength(5)
  })

  it('does not expire queued notifications before they become visible', () => {
    const queued = notifications.notify.info({ message: 'queued', duration: 1000 })
    notifications.notify.error('one')
    notifications.banner.warning('two')
    notifications.notify.error('three')

    vi.advanceTimersByTime(2000)
    expect(notifications.items.value.some((item) => item.id === queued)).toBe(true)
  })

  it('expires notifications and pauses the remaining duration', () => {
    const id = notifications.notify.info({ message: 'timed', duration: 1000 })
    vi.advanceTimersByTime(400)
    notifications.pause(id, 'hover')
    notifications.pause(id, 'focus')
    vi.advanceTimersByTime(1000)
    expect(notifications.items.value).toHaveLength(1)

    notifications.resume(id, 'hover')
    vi.advanceTimersByTime(1000)
    expect(notifications.items.value).toHaveLength(1)
    notifications.resume(id, 'focus')
    vi.advanceTimersByTime(599)
    expect(notifications.items.value).toHaveLength(1)
    vi.advanceTimersByTime(1)
    expect(notifications.items.value).toHaveLength(0)
  })

  it('opens and closes detail without changing notification order or lifetime', () => {
    const info = notifications.notify.info('details')
    notifications.openDetail(info)
    const error = notifications.notify.error('failed')

    expect(notifications.primaryId.value).toBe(error)
    expect(notifications.detailId.value).toBe(info)
    notifications.closeDetail()
    expect(notifications.detailId.value).toBeNull()
    expect(notifications.primaryId.value).toBe(error)
    expect(notifications.items.value.some((item) => item.id === info)).toBe(true)
  })

  it('pins any item without promoting it', () => {
    const first = notifications.notify.info('first')
    const second = notifications.notify.success('second')
    expect(notifications.primaryId.value).toBe(second)

    notifications.togglePinned(first)
    expect(notifications.pinnedId.value).toBe(first)
    expect(notifications.primaryId.value).toBe(second)

    notifications.closeExpanded()
    expect(notifications.pinnedId.value).toBeNull()
    expect(notifications.items.value.some((item) => item.id === first)).toBe(true)
  })

  it('normalizes unknown errors with a fallback', () => {
    const known = notifications.notify.error(new Error('broken'))
    const unknown = notifications.notify.error({ reason: 'missing' })

    expect(notifications.items.value.find((item) => item.id === known)?.message).toBe('broken')
    expect(notifications.items.value.find((item) => item.id === unknown)?.message).toBe(
      'An unknown error occurred',
    )
  })

  it('expires operation errors but keeps error banners', () => {
    const operation = notifications.notify.error('temporary failure')
    const persistent = notifications.banner.error('service unavailable')

    vi.advanceTimersByTime(10000)
    expect(notifications.items.value.some((item) => item.id === operation)).toBe(false)
    expect(notifications.items.value.some((item) => item.id === persistent)).toBe(true)
    expect(notifications.items.value.find((item) => item.id === persistent)?.dismissible).toBe(false)
  })

  it('keeps action errors on the notification for reusable views', async () => {
    const id = notifications.banner.error({
      message: 'Unavailable',
      action: { label: 'Retry', run: () => { throw new Error('Still offline') } },
    })

    await expect(notifications.runAction(id)).resolves.toBe(false)
    expect(notifications.actionStates.value[id]).toEqual({ running: false, error: 'Still offline' })
    expect(notifications.items.value.some((item) => item.id === id)).toBe(true)
  })

  it('replaces keyed banners and dismissByKey clears them', () => {
    notifications.banner.error({ key: 'backend-connection', message: 'first' })
    notifications.banner.error({ key: 'backend-connection', message: 'second' })
    expect(notifications.items.value.filter((item) => item.key === 'backend-connection')).toHaveLength(1)
    expect(notifications.items.value.find((item) => item.key === 'backend-connection')?.message).toBe('second')
    notifications.dismissByKey('backend-connection')
    expect(notifications.items.value.some((item) => item.key === 'backend-connection')).toBe(false)
  })

  it('resolves confirmations in FIFO order', async () => {
    const first = notifications.confirm({ message: 'first?' })
    const second = notifications.confirm({ message: 'second?' })

    expect(notifications.currentConfirmation.value?.message).toBe('first?')
    resolveConfirmation(notifications.currentConfirmation.value!.id, true)
    await expect(first).resolves.toBe(true)
    expect(notifications.currentConfirmation.value?.message).toBe('second?')
    resolveConfirmation(notifications.currentConfirmation.value!.id, false)
    await expect(second).resolves.toBe(false)
    expect(notifications.currentConfirmation.value).toBeNull()
  })

  it('ignores a stale confirmation resolution', async () => {
    const first = notifications.confirm({ message: 'first?' })
    const second = notifications.confirm({ message: 'second?' })
    const firstId = notifications.currentConfirmation.value!.id

    resolveConfirmation(firstId, true)
    resolveConfirmation(firstId, true)
    await expect(first).resolves.toBe(true)
    expect(notifications.currentConfirmation.value?.message).toBe('second?')

    resolveConfirmation(notifications.currentConfirmation.value!.id, false)
    await expect(second).resolves.toBe(false)
  })
})
