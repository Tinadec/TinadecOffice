import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  __resetNotificationsForTests,
  isUserDismissible,
  resolveConfirmation,
  setNotificationFallbackText,
  applyStatusBroadcast,
  useNotifications,
  type NotificationItem,
} from './useNotifications'

const n = useNotifications()

beforeEach(() => vi.useFakeTimers())

afterEach(() => {
  __resetNotificationsForTests()
  while (n.currentConfirmation.value) {
    resolveConfirmation(n.currentConfirmation.value.id, false)
  }
  setNotificationFallbackText('An unknown error occurred')
  vi.useRealTimers()
})

function byId(id: string): NotificationItem {
  const item = n.items.value.find((candidate) => candidate.id === id)
  if (!item) throw new Error(`notification ${id} not found`)
  return item
}

// ---------------------------------------------------------------------------
// Ported from old file (adapted to new API)
// ---------------------------------------------------------------------------

describe('useNotifications', () => {
  it('status zone precedes transient zone; errors rank highest within zones', () => {
    const first = n.notify.info('first')
    vi.advanceTimersByTime(1)
    const latest = n.notify.success('latest')
    vi.advanceTimersByTime(1)
    const statusItem = n.status.warning('persistent')
    vi.advanceTimersByTime(1)
    const error = n.notify.error(new Error('failed'))

    // Status items are in their own zone (never consume transient slots).
    expect(n.statusZone.value.map((i) => i.id)).toEqual([statusItem])
    // Transient zone shows up to 3 items ranked by priority then recency.
    expect(n.transientZone.value.map((i) => i.id)).toEqual([error, latest, first])
    // Combined visible includes both zones.
    expect(n.visibleItems.value.map((i) => i.id)).toEqual([statusItem, error, latest, first])
  })

  it('shows at most three transient items and reports overflow', () => {
    for (let i = 0; i < 5; i++) n.notify.info(`item ${i}`)

    expect(n.visibleItems.value).toHaveLength(3)
    expect(n.overflowCount.value).toBe(2)
    expect(n.orderedItems.value).toHaveLength(5)
  })

  it('expires notifications and pauses the remaining duration', () => {
    const id = n.notify.info({ message: 'timed', duration: 1000 })
    vi.advanceTimersByTime(400)
    n.pause(id, 'hover')
    n.pause(id, 'focus')
    vi.advanceTimersByTime(1000)
    expect(n.items.value).toHaveLength(1)

    n.resume(id, 'hover')
    vi.advanceTimersByTime(1000)
    expect(n.items.value).toHaveLength(1)
    n.resume(id, 'focus')
    vi.advanceTimersByTime(599)
    expect(n.items.value).toHaveLength(1)
    vi.advanceTimersByTime(1)
    expect(n.items.value).toHaveLength(0)
  })

  it('opens and closes detail without changing notification order or lifetime', () => {
    const info = n.notify.info('details')
    n.openDetail(info)
    const error = n.notify.error('failed')

    expect(n.primaryId.value).toBe(error)
    expect(n.detailId.value).toBe(info)
    n.closeDetail()
    expect(n.detailId.value).toBeNull()
    expect(n.primaryId.value).toBe(error)
    expect(n.items.value.some((item) => item.id === info)).toBe(true)
  })

  it('toggleOpen does not promote the item to primary', () => {
    const first = n.notify.info('first')
    vi.advanceTimersByTime(1)
    const second = n.notify.success('second')
    expect(n.primaryId.value).toBe(second)

    n.toggleOpen(first)
    expect(n.openId.value).toBe(first)
    expect(n.primaryId.value).toBe(second)

    n.closeOpen()
    expect(n.openId.value).toBeNull()
    expect(n.items.value.some((item) => item.id === first)).toBe(true)
  })

  it('normalizes unknown errors with a fallback', () => {
    const known = n.notify.error(new Error('broken'))
    const unknown = n.notify.error({ reason: 'missing' })

    expect(n.items.value.find((item) => item.id === known)?.message).toBe('broken')
    expect(n.items.value.find((item) => item.id === unknown)?.message).toBe(
      'An unknown error occurred',
    )
  })

  it('expires transient errors but keeps status errors', () => {
    const operation = n.notify.error('temporary failure')
    const statusItem = n.status.error('service unavailable')

    vi.advanceTimersByTime(10000)
    expect(n.items.value.some((item) => item.id === operation)).toBe(false)
    expect(n.items.value.some((item) => item.id === statusItem)).toBe(true)
  })

  it('keeps action errors on the notification for reusable views', async () => {
    const id = n.status.error({
      message: 'Unavailable',
      action: { label: 'Retry', run: () => { throw new Error('Still offline') } },
    })

    await expect(n.runAction(id)).resolves.toBe(false)
    expect(n.actionStates.value[id]).toEqual({ running: false, error: 'Still offline' })
    expect(n.items.value.some((item) => item.id === id)).toBe(true)
  })

  it('replaces keyed status and dismissByKey clears them', () => {
    n.status.error({ key: 'backend-connection', message: 'first' })
    n.status.error({ key: 'backend-connection', message: 'second' })
    expect(n.items.value.filter((item) => item.key === 'backend-connection')).toHaveLength(1)
    expect(n.items.value.find((item) => item.key === 'backend-connection')?.message).toBe('second')
    n.dismissByKey('backend-connection')
    expect(n.items.value.some((item) => item.key === 'backend-connection')).toBe(false)
  })

  it('resolves confirmations in FIFO order', async () => {
    const first = n.confirm({ message: 'first?' })
    const second = n.confirm({ message: 'second?' })

    expect(n.currentConfirmation.value?.message).toBe('first?')
    resolveConfirmation(n.currentConfirmation.value!.id, true)
    await expect(first).resolves.toBe(true)
    expect(n.currentConfirmation.value?.message).toBe('second?')
    resolveConfirmation(n.currentConfirmation.value!.id, false)
    await expect(second).resolves.toBe(false)
    expect(n.currentConfirmation.value).toBeNull()
  })

  it('ignores a stale confirmation resolution', async () => {
    const first = n.confirm({ message: 'first?' })
    const second = n.confirm({ message: 'second?' })
    const firstId = n.currentConfirmation.value!.id

    resolveConfirmation(firstId, true)
    resolveConfirmation(firstId, true) // stale — should be ignored
    await expect(first).resolves.toBe(true)
    expect(n.currentConfirmation.value?.message).toBe('second?')

    resolveConfirmation(n.currentConfirmation.value!.id, false)
    await expect(second).resolves.toBe(false)
  })

  it('banner is an alias for status', () => {
    expect(n.banner).toBe(n.status)
  })

  // -----------------------------------------------------------------------
  // Dismissal & persistence contract (Apple Live Activity / 超级岛 model)
  // -----------------------------------------------------------------------

  it('contract: status and pending tasks are source-owned; transients and settled tasks are user-closable', () => {
    const transient = n.notify.info('toast')
    const sticky = n.notify.warning({ message: 'sticky', persistence: 'sticky' })
    const task = n.notify.task({ message: 'working' })
    const statusItem = n.status.error({ key: 'cond', message: 'down' })

    expect(isUserDismissible(byId(transient))).toBe(true)
    expect(isUserDismissible(byId(sticky))).toBe(true)
    expect(isUserDismissible(byId(task.id))).toBe(false)
    expect(isUserDismissible(byId(statusItem))).toBe(false)
    expect(byId(statusItem).persistence).toBe('source')
    expect(byId(task.id).persistence).toBe('source')
    expect(byId(sticky).persistence).toBe('sticky')

    // Settling a task hands its lifecycle back to the user + timer.
    task.succeed('done')
    expect(isUserDismissible(byId(task.id))).toBe(true)
    expect(byId(task.id).persistence).toBe('auto')
  })

  it('sticky transients never expire on their own but stay user-closable', () => {
    const id = n.notify.info({ message: 'stay', persistence: 'sticky' })
    vi.advanceTimersByTime(60000)
    expect(n.items.value.some((item) => item.id === id)).toBe(true)

    n.dismiss(id)
    expect(n.items.value.some((item) => item.id === id)).toBe(false)
  })

  it('dismissAll clears only user-closable items; status and pending tasks survive', () => {
    n.notify.info('a')
    const task = n.notify.task({ message: 'working' })
    n.status.error({ key: 'cond', message: 'down' })

    n.dismissAll()
    expect(n.items.value.some((item) => item.message === 'a')).toBe(false)
    expect(n.items.value.some((item) => item.id === task.id)).toBe(true)
    expect(n.items.value.some((item) => item.key === 'cond')).toBe(true)
  })

  it('programmatic dismiss of a status item never broadcasts clear; dismissByKey does', () => {
    const originalWindow = (globalThis as any).window
    const mockBroadcast = vi.fn()
    ;(globalThis as any).window = {
      tinadec: { broadcastStatusNotification: mockBroadcast },
    }

    try {
      const id = n.status.error({ key: 'k1', message: 'down' })
      mockBroadcast.mockClear()
      // Incidental removal must not tell other windows the condition recovered.
      n.dismiss(id)
      expect(mockBroadcast).not.toHaveBeenCalled()

      n.status.error({ key: 'k2', message: 'down' })
      mockBroadcast.mockClear()
      n.dismissByKey('k2')
      expect(mockBroadcast).toHaveBeenCalledTimes(1)
      expect(mockBroadcast).toHaveBeenCalledWith(
        expect.objectContaining({ op: 'clear', key: 'k2' }),
      )
    } finally {
      if (originalWindow === undefined) {
        delete (globalThis as any).window
      } else {
        ;(globalThis as any).window = originalWindow
      }
    }
  })

  // -----------------------------------------------------------------------
  // Test 1 — Queue never stalls (headline regression lock)
  // -----------------------------------------------------------------------

  it('queue never stalls: transient expires even with many status items', () => {
    // Status items occupy the status zone, never the transient zone.
    n.status.error({ key: 's1', message: 'status 1' })
    n.status.error({ key: 's2', message: 'status 2' })
    n.status.error({ key: 's3', message: 'status 3' })
    // A short-lived transient behind them.
    n.notify.info({ message: 'x', duration: 1000 })

    vi.advanceTimersByTime(2000)
    // The transient MUST be gone — old code froze it forever.
    expect(n.items.value.some((item) => item.message === 'x')).toBe(false)
  })

  // -----------------------------------------------------------------------
  // Test 2 — Reversed old test: off-screen transients MUST expire
  // -----------------------------------------------------------------------

  it('off-screen transients expire (reversed: old code froze them forever)', () => {
    // Fill the visible transient slots with errors.
    n.notify.error('one')
    n.notify.error('two')
    n.notify.error('three')
    // This short-lived info is queued off-screen (behind 3 errors).
    const queued = n.notify.info({ message: 'queued', duration: 1000 })

    expect(n.visibleItems.value.some((item) => item.id === queued)).toBe(false)

    vi.advanceTimersByTime(2000)
    // Off-screen transient MUST have expired.
    expect(n.items.value.some((item) => item.id === queued)).toBe(false)
  })

  // -----------------------------------------------------------------------
  // Test 3 — Dedupe merge
  // -----------------------------------------------------------------------

  it('dedupes same-message notifications: one item, count incremented, same id', () => {
    const id1 = n.notify.info('duplicate message')
    const id2 = n.notify.info('duplicate message')

    expect(id1).toBe(id2) // same slot preserved, not recreated
    expect(n.items.value.filter((item) => item.message === 'duplicate message')).toHaveLength(1)
    expect(n.items.value.find((item) => item.id === id1)!.count).toBe(2)
  })

  // -----------------------------------------------------------------------
  // Test 4 — Keyed status replace + clear
  // -----------------------------------------------------------------------

  it('keyed status replaces in place and dismissByKey removes it', () => {
    const id = n.status.error({ key: 'my-service', message: 'down' })
    const id2 = n.status.error({ key: 'my-service', message: 'still down' })

    expect(id).toBe(id2) // same slot
    expect(n.items.value.filter((i) => i.key === 'my-service')).toHaveLength(1)
    expect(n.items.value.find((i) => i.key === 'my-service')?.message).toBe('still down')

    n.dismissByKey('my-service')
    expect(n.items.value.filter((i) => i.key === 'my-service')).toHaveLength(0)
  })

  // -----------------------------------------------------------------------
  // Test 5 — Task lifecycle
  // -----------------------------------------------------------------------

  it('task lifecycle: pending → succeed settles then expires', () => {
    const task = n.notify.task({ message: 'building', duration: 5000 })
    const taskItem = n.items.value.find((i) => i.id === task.id)

    expect(taskItem?.taskState).toBe('pending')
    expect(n.hasPendingTask.value).toBe(true)

    // Pending task does not expire even after a long wait.
    vi.advanceTimersByTime(20000)
    expect(n.items.value.some((i) => i.id === task.id)).toBe(true)

    task.succeed('done')
    const afterSucceed = n.items.value.find((i) => i.id === task.id)
    expect(afterSucceed?.level).toBe('success')
    expect(afterSucceed?.taskState).toBe('settled')
    expect(afterSucceed?.message).toBe('done')
    expect(n.hasPendingTask.value).toBe(false)

    // Settled task expires after its duration.
    vi.advanceTimersByTime(5001)
    expect(n.items.value.some((i) => i.id === task.id)).toBe(false)
  })

  it('task lifecycle: pending → fail sets error level and message', () => {
    const task = n.notify.task({ message: 'deploying' })
    task.fail(new Error('boom'))

    const afterFail = n.items.value.find((i) => i.id === task.id)
    expect(afterFail?.level).toBe('error')
    expect(afterFail?.taskState).toBe('settled')
    expect(afterFail?.message).toBe('boom')
  })

  // -----------------------------------------------------------------------
  // Test 6 — Attention pauses, off-screen does not
  // -----------------------------------------------------------------------

  it('hovered transient is held past its duration; unhover lets it expire', () => {
    const id = n.notify.info({ message: 'hover me', duration: 1000 })
    n.setHovered(id)

    vi.advanceTimersByTime(2000)
    // Hovered item must survive past its duration.
    expect(n.items.value.some((i) => i.id === id)).toBe(true)

    n.setHovered(null)
    vi.advanceTimersByTime(1001)
    // Unhovered item must now expire.
    expect(n.items.value.some((i) => i.id === id)).toBe(false)
  })

  it('a re-raised notification stays paused while still hovered', () => {
    const id = n.notify.info({ message: 'repeating', duration: 1000 })
    n.setHovered(id)

    // The same condition fires again while the pointer is still on the capsule.
    n.notify.info({ message: 'repeating', duration: 1000 })
    expect(n.items.value.find((i) => i.id === id)?.count).toBe(2)

    vi.advanceTimersByTime(3000)
    // Restarting the timer must not silently resume it under the pointer.
    expect(n.items.value.some((i) => i.id === id)).toBe(true)

    n.setHovered(null)
    vi.advanceTimersByTime(1001)
    expect(n.items.value.some((i) => i.id === id)).toBe(false)
  })

  it('an explicitly paused notification stays paused across a re-raise', () => {
    const id = n.notify.info({ message: 'held', duration: 1000 })
    n.pause(id, 'manual')

    n.notify.info({ message: 'held', duration: 1000 })
    vi.advanceTimersByTime(5000)
    expect(n.items.value.some((i) => i.id === id)).toBe(true)

    n.resume(id, 'manual')
    vi.advanceTimersByTime(1001)
    expect(n.items.value.some((i) => i.id === id)).toBe(false)
  })

  // -----------------------------------------------------------------------
  // Test 7 — Capacity cap
  // -----------------------------------------------------------------------

  it('items never exceed MAX_ITEMS; status and pending task survive cap', () => {
    // Status item — exempt from cap pressure.
    n.status.error({ key: 'cap-test', message: 'persistent status' })
    // Pending task — exempt from cap pressure.
    const task = n.notify.task({ message: 'long task' })

    // Flood with transients to exceed the cap.
    for (let i = 0; i < 60; i++) n.notify.info(`flood ${i}`)

    expect(n.items.value.length).toBeLessThanOrEqual(50)
    // Status item survives.
    expect(n.items.value.some((i) => i.key === 'cap-test')).toBe(true)
    // Pending task survives.
    expect(n.items.value.some((i) => i.id === task.id && i.taskState === 'pending')).toBe(true)
  })

  it('cap still holds when the overflow is entirely status items', () => {
    const pending = n.notify.task({ message: 'still running' })
    // Unkeyed status raises never merge, so they would grow without bound if the
    // cap could only shed queue items.
    for (let i = 0; i < 70; i++) n.status.warning(`condition ${i}`)

    expect(n.items.value.length).toBeLessThanOrEqual(50)
    // The pending task is never sacrificed to make room.
    expect(n.items.value.some((i) => i.id === pending.id)).toBe(true)
    // Newest conditions are the ones kept.
    expect(n.items.value.some((i) => i.message === 'condition 69')).toBe(true)
    expect(n.items.value.some((i) => i.message === 'condition 0')).toBe(false)
  })

  // -----------------------------------------------------------------------
  // Test 8 — History ring
  // -----------------------------------------------------------------------

  it('dismissed items appear in history newest-first and never exceed 50', () => {
    const ids: string[] = []
    for (let i = 0; i < 55; i++) ids.push(n.notify.info(`msg ${i}`))

    for (const id of ids) n.dismiss(id)

    expect(n.history.value.length).toBe(50)
    // Newest-first: the last dismissed item is at index 0.
    expect(n.history.value[0].message).toBe('msg 54')
    // Oldest surviving entry is at the end (msg 0–4 were evicted).
    expect(n.history.value[n.history.value.length - 1].message).toBe('msg 5')
    // Entry shape check.
    expect(n.history.value[0]).toMatchObject({
      kind: 'transient',
      level: 'info',
      message: 'msg 54',
      count: 1,
    })
  })

  // -----------------------------------------------------------------------
  // Test 9 — toggleOpen closes even while hovered (interaction bug lock)
  // -----------------------------------------------------------------------

  it('toggleOpen closes even while hovered — interaction bug lock', () => {
    const id = n.notify.info('open me')
    n.toggleOpen(id)
    expect(n.openId.value).toBe(id)

    n.setHovered(id)
    expect(n.hoveredId.value).toBe(id)

    // Toggle again while hovered — must close.
    n.toggleOpen(id)
    expect(n.openId.value).toBeNull()
  })

  // -----------------------------------------------------------------------
  // Test 10 — Zones independent
  // -----------------------------------------------------------------------

  it('status zone and transient zone are independent', () => {
    n.status.error({ key: 'z1', message: 'status 1' })
    n.status.error({ key: 'z2', message: 'status 2' })
    for (let i = 0; i < 5; i++) n.notify.info(`transient ${i}`)

    expect(n.statusZone.value).toHaveLength(2)
    expect(n.transientZone.value).toHaveLength(3)
    expect(n.visibleItems.value).toHaveLength(5)
    expect(n.orderedItems.value).toHaveLength(7)
  })

  // -----------------------------------------------------------------------
  // Test 11 — Broadcast echo suppression
  // -----------------------------------------------------------------------

  it('broadcast echo suppression: remote raises not echoed, local raises echoed', () => {
    const originalWindow = (globalThis as any).window
    const mockBroadcast = vi.fn()
    ;(globalThis as any).window = {
      tinadec: { broadcastStatusNotification: mockBroadcast },
    }

    try {
      // Remote broadcast should NOT call the spy (echo suppression).
      applyStatusBroadcast({
        op: 'raise',
        key: 'remote-key',
        level: 'error',
        message: 'remote msg',
        createdAt: Date.now(),
      })
      expect(mockBroadcast).not.toHaveBeenCalled()

      // Local raise SHOULD call the spy with op:'raise'.
      n.status.error({ key: 'local-key', message: 'local msg' })
      expect(mockBroadcast).toHaveBeenCalledTimes(1)
      expect(mockBroadcast).toHaveBeenCalledWith(
        expect.objectContaining({ op: 'raise', key: 'local-key' }),
      )

      // Local dismiss SHOULD call the spy with op:'clear'.
      mockBroadcast.mockClear()
      n.dismissByKey('local-key')
      expect(mockBroadcast).toHaveBeenCalledTimes(1)
      expect(mockBroadcast).toHaveBeenCalledWith(
        expect.objectContaining({ op: 'clear', key: 'local-key' }),
      )
    } finally {
      if (originalWindow === undefined) {
        delete (globalThis as any).window
      } else {
        ;(globalThis as any).window = originalWindow
      }
    }
  })

  // -----------------------------------------------------------------------
  // Test 12 — Error-options discrimination
  // -----------------------------------------------------------------------

  it('error-options: object without option fields is treated as error value (no field leak)', () => {
    // A DTO that happens to have `message` but no option fields → treated as the error value.
    const id = n.notify.error({ message: 'boom', code: 500, trace_id: 'abc' })
    const item = n.items.value.find((i) => i.id === id)
    expect(item?.message).toBe('boom')
    // Must NOT carry DTO fields that leak from the raw object.
    expect(item).not.toHaveProperty('code')
    expect(item).not.toHaveProperty('trace_id')
  })

  it('error-options: object WITH option fields is treated as notification options', () => {
    const id = n.notify.error({ message: 'boom', title: 'T', key: 'k' })
    const item = n.items.value.find((i) => i.id === id)
    expect(item?.title).toBe('T')
    expect(item?.key).toBe('k')
  })

  // -----------------------------------------------------------------------
  // Test 13 — Fallback text
  // -----------------------------------------------------------------------

  it('fallback text: setNotificationFallbackText + error(null) uses fallback', () => {
    setNotificationFallbackText('FALLBACK')
    const id = n.notify.error(null)
    expect(n.items.value.find((i) => i.id === id)?.message).toBe('FALLBACK')
  })
})
