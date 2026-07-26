// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import NotificationIslandHost from './NotificationIslandHost.vue'
import { useNotifications } from '@/composables/useNotifications'

vi.mock('vue-router', () => ({ useRoute: () => ({ name: 'home' }) }))
vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string) => key, locale: { value: 'en' } }),
}))

const notifications = useNotifications()

afterEach(() => {
  notifications.dismissAll()
  notifications.closeDetail()
  notifications.closeOpen()
  notifications.closeOverflow()
  notifications.setHovered(null)
  notifications.history.value = []
  document.body.innerHTML = ''
})

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

  it('click toggles card open then closed even while hovered', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    const id = notifications.notify.info('click test')
    await nextTick()

    const capsule = document.body.querySelector<HTMLButtonElement>(
      `[data-notification-id="${id}"]`,
    )!
    expect(capsule).not.toBeNull()

    capsule.click()
    await nextTick()
    expect(notifications.openId.value).toBe(id)
    expect(document.body.querySelector('.island-card')).not.toBeNull()

    capsule.dispatchEvent(new Event('mouseenter'))
    await nextTick()
    expect(notifications.hoveredId.value).toBe(id)

    capsule.click()
    await nextTick()
    expect(notifications.openId.value).toBeNull()
    expect(document.body.querySelector('.island-card')).toBeNull()
    wrapper.unmount()
  })

  it('hover alone does not open the card', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    const id = notifications.notify.info('hover test')
    await nextTick()

    const capsule = document.body.querySelector<HTMLButtonElement>(
      `[data-notification-id="${id}"]`,
    )!
    capsule.dispatchEvent(new Event('mouseenter'))
    await nextTick()

    expect(notifications.hoveredId.value).toBe(id)
    expect(document.body.querySelector('.island-card')).toBeNull()
    wrapper.unmount()
  })

  it('every item is closable including status', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    const id = notifications.status.error({ key: 'closeable-status', message: 'close me' })
    await nextTick()

    const capsule = document.body.querySelector<HTMLButtonElement>(
      `[data-notification-id="${id}"]`,
    )!
    expect(capsule).not.toBeNull()

    const closeBtn = capsule.closest('.island-capsule')!.querySelector('.island-capsule__close')!
    expect(closeBtn).not.toBeNull()
    ;(closeBtn as HTMLButtonElement).click()
    await nextTick()

    expect(notifications.items.value.some((item) => item.id === id)).toBe(false)
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
