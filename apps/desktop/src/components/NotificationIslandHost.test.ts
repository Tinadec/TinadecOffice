// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import NotificationIslandHost from './NotificationIslandHost.vue'
import {
  __resetNotificationsForTests,
  useNotifications,
} from '@/composables/useNotifications'

vi.mock('vue-router', () => ({ useRoute: () => ({ name: 'home' }) }))
vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string) => key, locale: { value: 'en' } }),
}))

const notifications = useNotifications()

afterEach(() => {
  __resetNotificationsForTests()
  document.body.innerHTML = ''
})

function closeButtonOf(id: string): Element | null | undefined {
  return document.body
    .querySelector(`[data-notification-id="${id}"]`)
    ?.closest('.island-capsule')
    ?.querySelector('.island-capsule__close')
}

describe('NotificationIslandHost', () => {
  it('renders nothing when empty and appears on first notification', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    await nextTick()
    expect(document.body.querySelector('.island-host')).toBeNull()

    const id = notifications.notify.info('hello')
    await nextTick()
    expect(document.body.querySelector(`[data-notification-id="${id}"]`)).not.toBeNull()
    wrapper.unmount()
  })

  it('status and transient items occupy separate zones', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    notifications.status.error({ key: 'e1', message: 'Error 1' })
    notifications.status.error({ key: 'e2', message: 'Error 2' })
    notifications.notify.info('notice 1')
    notifications.notify.info('notice 2')
    notifications.notify.info('notice 3')
    notifications.notify.info('notice 4')
    await nextTick()

    expect(
      document.body.querySelectorAll('.island-zone--status .island-capsule--status').length,
    ).toBe(2)
    expect(
      document.body.querySelectorAll('.island-zone--transient .island-capsule--transient').length,
    ).toBe(3)
    wrapper.unmount()
  })

  it('click opens the detail dialog directly (通知打开)', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    const id = notifications.notify.info('click test')
    await nextTick()

    const capsule = document.body.querySelector<HTMLButtonElement>(
      `[data-notification-id="${id}"]`,
    )!
    expect(capsule).not.toBeNull()

    capsule.click()
    await nextTick()
    expect(notifications.detailId.value).toBe(id)
    // The hover-expanded card closes behind the modal surface.
    expect(notifications.openId.value).toBeNull()
    expect(document.body.querySelector('.island-card')).toBeNull()
    wrapper.unmount()
  })

  it('hover auto-expands the card after an intent delay and closes after leaving', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
      const id = notifications.notify.info('hover test')
      await nextTick()

      const capsule = document.body.querySelector<HTMLButtonElement>(
        `[data-notification-id="${id}"]`,
      )!
      capsule.dispatchEvent(new Event('mouseenter'))
      await nextTick()

      // Highlight is instant; expansion waits for hover intent so passing the
      // pointer over the title bar never flashes UI.
      expect(notifications.hoveredId.value).toBe(id)
      expect(notifications.openId.value).toBeNull()

      await vi.advanceTimersByTimeAsync(300)
      await nextTick()
      expect(notifications.openId.value).toBe(id)
      expect(document.body.querySelector('.island-card')).not.toBeNull()

      // Leaving the whole strip starts the close grace period.
      document.body.querySelector('.island-host')!.dispatchEvent(new Event('mouseleave'))
      await nextTick()
      expect(notifications.hoveredId.value).toBeNull()
      expect(notifications.openId.value).toBe(id)

      await vi.advanceTimersByTimeAsync(500)
      await nextTick()
      expect(notifications.openId.value).toBeNull()
      expect(document.body.querySelector('.island-card')).toBeNull()
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })

  it('clicking the hover-expanded card opens the detail dialog (通知打开)', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
      const id = notifications.notify.info('card click')
      await nextTick()

      const capsule = document.body.querySelector<HTMLButtonElement>(
        `[data-notification-id="${id}"]`,
      )!
      capsule.dispatchEvent(new Event('mouseenter'))
      await vi.advanceTimersByTimeAsync(300)
      await nextTick()
      expect(notifications.openId.value).toBe(id)

      // Clicking the card itself opens the dialog and tucks the card away.
      const card = document.body.querySelector<HTMLElement>('.island-card')!
      expect(card).not.toBeNull()
      card.click()
      await nextTick()
      expect(notifications.detailId.value).toBe(id)
      expect(notifications.openId.value).toBeNull()
      expect(document.body.querySelector('.island-card')).toBeNull()
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })

  it('moving from the capsule into the card keeps it open', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
      const id = notifications.notify.info('glide')
      await nextTick()

      const capsule = document.body.querySelector<HTMLButtonElement>(
        `[data-notification-id="${id}"]`,
      )!
      capsule.dispatchEvent(new Event('mouseenter'))
      await vi.advanceTimersByTimeAsync(300)
      await nextTick()
      expect(notifications.openId.value).toBe(id)

      // Pointer crosses the 8px gap (leaves the strip) then lands on the card
      // before the grace period ends — the card must stay open.
      document.body.querySelector('.island-host')!.dispatchEvent(new Event('mouseleave'))
      await vi.advanceTimersByTimeAsync(100)
      const card = document.body.querySelector('.island-card')!
      card.dispatchEvent(new Event('mouseenter'))
      await vi.advanceTimersByTimeAsync(500)
      await nextTick()
      expect(notifications.openId.value).toBe(id)

      // Leaving the card for good closes it.
      card.dispatchEvent(new Event('mouseleave'))
      await vi.advanceTimersByTimeAsync(500)
      await nextTick()
      expect(notifications.openId.value).toBeNull()
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })

  it('close affordance follows the dismissal contract', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    const transientId = notifications.notify.info('closable')
    const statusId = notifications.status.error({ key: 'src', message: 'condition' })
    const task = notifications.notify.task({ message: 'working' })
    await nextTick()

    // Transient feedback is user-closable; status conditions and pending tasks
    // are source-owned and render a residency marker instead of a close button.
    expect(closeButtonOf(transientId)).not.toBeNull()
    expect(closeButtonOf(statusId)).toBeNull()
    expect(closeButtonOf(task.id)).toBeNull()

    // Settling the task hands its lifecycle back to the user.
    task.succeed('done')
    await nextTick()
    expect(closeButtonOf(task.id)).not.toBeNull()

    // Clicking close on the transient dismisses it.
    ;(closeButtonOf(transientId) as HTMLButtonElement).click()
    await nextTick()
    expect(notifications.items.value.some((item) => item.id === transientId)).toBe(false)
    wrapper.unmount()
  })

  it('overflow reveals all items', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    notifications.notify.info('t1')
    notifications.notify.info('t2')
    notifications.notify.info('t3')
    notifications.notify.info('t4')
    await nextTick()

    const overflowBadge = document.body.querySelector(
      '.island-capsule--overflow .island-capsule__badge',
    )
    expect(overflowBadge).not.toBeNull()
    expect(overflowBadge?.textContent).toBe('+1')

    notifications.toggleOverflow()
    await nextTick()
    expect(document.body.querySelector('#notification-overflow-panel')).not.toBeNull()
    wrapper.unmount()
  })

  it('notification center shows live and cleared sections with contract-aware actions', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    notifications.notify.info('keep')
    const goneId = notifications.notify.info('gone')
    notifications.status.error({ key: 'src', message: 'standing condition' })
    notifications.dismiss(goneId)
    await nextTick()

    notifications.toggleOverflow()
    await nextTick()

    const panel = document.body.querySelector('#notification-overflow-panel')!
    expect(panel).not.toBeNull()
    // Live section: closable transient gets a dismiss action, source-owned
    // status gets a static residency marker.
    const liveRows = panel.querySelectorAll('.island-overflow__section:first-child .island-overflow__row')
    expect(liveRows.length).toBe(2)
    const dismissButtons = panel.querySelectorAll('button.island-overflow__action[aria-label="app.dismiss"]')
    expect(dismissButtons.length).toBe(1)
    expect(panel.querySelector('.island-overflow__action--static')).not.toBeNull()
    // Cleared section: the dismissed transient is retained read-only.
    const clearedRows = panel.querySelectorAll('.island-overflow__row--cleared')
    expect(clearedRows.length).toBe(1)
    expect(clearedRows[0].textContent).toContain('gone')
    wrapper.unmount()
  })

  it('merge counter increments count for repeated notifications', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    notifications.notify.info({ message: 'dup' })
    await nextTick()
    notifications.notify.info({ message: 'dup' })
    await nextTick()

    const capsules = document.body.querySelectorAll(
      '.island-zone--transient .island-capsule--transient',
    )
    expect(capsules.length).toBe(1)

    const badge = capsules[0].querySelector('.island-capsule__badge')
    expect(badge).not.toBeNull()
    expect(badge?.textContent).toBe('×2')
    wrapper.unmount()
  })
})
