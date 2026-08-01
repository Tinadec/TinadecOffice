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

  it('status and transient items each aggregate to a single hero', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    notifications.status.error({ key: 'e1', message: 'Error 1' })
    notifications.status.error({ key: 'e2', message: 'Error 2' })
    notifications.notify.info('notice 1')
    notifications.notify.info('notice 2')
    notifications.notify.info('notice 3')
    notifications.notify.info('notice 4')
    await nextTick()

    // Each zone renders exactly one aggregated hero capsule.
    expect(
      document.body.querySelectorAll('.island-zone--status .island-capsule--status').length,
    ).toBe(1)
    expect(
      document.body.querySelectorAll('.island-zone--transient .island-capsule--transient').length,
    ).toBe(1)
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
    vi.useFakeTimers()
    try {
      const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
      const transientId = notifications.notify.info('closable')
      const statusId = notifications.status.error({ key: 'src', message: 'condition' })
      const task = notifications.notify.task({ message: 'working' })
      await nextTick()

      // Status hero is source-owned → residency marker, no close button.
      expect(closeButtonOf(statusId)).toBeNull()
      // Transient hero is user-closable.
      expect(closeButtonOf(transientId)).not.toBeNull()

      // The pending task folds behind the transient hero; its row in the stack
      // shows a residency marker (handle-owned), not a close button.
      notifications.hoverHeroEnter('transient', notifications.transientHero.value!.id)
      await vi.advanceTimersByTimeAsync(300)
      await nextTick()
      const stack = document.body.querySelector('#notification-island-stack')!
      const taskRow = stack.querySelector(`[data-notification-id="${task.id}"]`)!
      expect(taskRow.querySelector('.island-stack__action--static')).not.toBeNull()
      // No dismiss button (only the static residency marker) while pending.
      expect(taskRow.querySelector('.island-stack__action:not(.island-stack__action--static)')).toBeNull()

      // Settling the task hands its lifecycle back to the user → close appears.
      task.succeed('done')
      await nextTick()
      const settledRow = stack.querySelector(`[data-notification-id="${task.id}"]`)!
      expect(settledRow.querySelector('.island-stack__action:not(.island-stack__action--static)')).not.toBeNull()

      // Clicking close on the transient hero dismisses it.
      ;(closeButtonOf(transientId) as HTMLButtonElement).click()
      await nextTick()
      expect(notifications.items.value.some((item) => item.id === transientId)).toBe(false)
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })

  it('fold badge shows on the hero and the stack footer opens the notification center', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
      notifications.notify.info('t1')
      notifications.notify.info('t2')
      notifications.notify.info('t3')
      notifications.notify.info('t4')
      await nextTick()

      // The transient hero shows a +3 fold badge; the rest are not rendered.
      const heroBadge = document.body.querySelector('.island-capsule__stack')
      expect(heroBadge).not.toBeNull()
      expect(heroBadge?.textContent).toBe('3')
      expect(document.body.querySelectorAll('.island-capsule--transient')).toHaveLength(1)

      // Opening the stack and clicking its footer enters the notification center.
      notifications.hoverHeroEnter('transient', notifications.transientHero.value!.id)
      await vi.advanceTimersByTimeAsync(300)
      await nextTick()
      const stackFooterBtn = document.body.querySelector<HTMLButtonElement>('.island-stack__footer button')!
      expect(stackFooterBtn).not.toBeNull()
      stackFooterBtn.click()
      await nextTick()
      expect(document.body.querySelector('#notification-overflow-panel')).not.toBeNull()
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
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

  it('hovering an aggregated status hero reveals the stack and it closes after leaving', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
      notifications.status.error({ key: 's1', message: 'cond 1' })
      notifications.status.error({ key: 's2', message: 'cond 2' })
      await nextTick()

      const hero = document.body.querySelector<HTMLButtonElement>(
        '[data-notification-id]',
      )!
      hero.dispatchEvent(new Event('mouseenter'))
      await vi.advanceTimersByTimeAsync(300)
      await nextTick()

      const stack = document.body.querySelector('#notification-island-stack')!
      expect(stack).not.toBeNull()
      expect(stack.querySelectorAll('.island-stack__row').length).toBe(2)

      // Leaving the panel starts the close grace period; the stack stays open
      // while the pointer is on it, then closes after the full grace.
      stack.dispatchEvent(new Event('mouseenter'))
      document.body.querySelector('.island-host')!.dispatchEvent(new Event('mouseleave'))
      await vi.advanceTimersByTimeAsync(100)
      await nextTick()
      expect(document.body.querySelector('#notification-island-stack')).not.toBeNull()

      stack.dispatchEvent(new Event('mouseleave'))
      await vi.advanceTimersByTimeAsync(500)
      await nextTick()
      expect(document.body.querySelector('#notification-island-stack')).toBeNull()
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })

  it('clicking a stack row opens that notification detail dialog', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
      notifications.status.error({ key: 's1', message: 'cond 1' })
      const second = notifications.status.error({ key: 's2', message: 'cond 2' })
      await nextTick()

      notifications.hoverHeroEnter('status', notifications.statusHero.value!.id)
      await vi.advanceTimersByTimeAsync(300)
      await nextTick()

      const stack = document.body.querySelector('#notification-island-stack')!
      const row = stack.querySelector<HTMLElement>(
        `[data-notification-id="${second}"]`,
      )!
      row.click()
      await nextTick()
      expect(notifications.detailId.value).toBe(second)
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })

  it('hero fold badge text shows the folded count', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    notifications.notify.info('a')
    notifications.notify.info('b')
    notifications.notify.info('c')
    await nextTick()

    const badge = document.body.querySelector('.island-zone--transient .island-capsule__stack')
    expect(badge).not.toBeNull()
    expect(badge?.textContent).toBe('2')
    wrapper.unmount()
  })

  it('stack footer button opens the notification center panel', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
      notifications.notify.info('a')
      notifications.notify.info('b')
      await nextTick()

      notifications.hoverHeroEnter('transient', notifications.transientHero.value!.id)
      await vi.advanceTimersByTimeAsync(300)
      await nextTick()

      const footerBtn = document.body.querySelector<HTMLButtonElement>('.island-stack__footer button')!
      footerBtn.click()
      await nextTick()
      expect(document.body.querySelector('#notification-overflow-panel')).not.toBeNull()
      expect(notifications.stackZone.value).toBeNull()
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })
})
