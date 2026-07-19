// @vitest-environment happy-dom

import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import NotificationIslandHost from './NotificationIslandHost.vue'
import { useNotifications } from '@/composables/useNotifications'

vi.mock('vue-router', () => ({ useRoute: () => ({ name: 'home' }) }))
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }))

const notifications = useNotifications()

afterEach(() => {
  for (const item of [...notifications.items.value]) notifications.dismiss(item.id)
  notifications.closeDetail()
  notifications.setHovered(null)
  document.body.innerHTML = ''
})

describe('NotificationIslandHost', () => {
  it('renders nothing until notified, hover opens a card, and click pins it', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    expect(document.body.querySelector('.island-host')).toBeNull()

    const id = notifications.notify.info({ message: 'Saved', persistent: true })
    await nextTick()
    const capsule = document.body.querySelector<HTMLButtonElement>('.island-capsule--primary')
    expect(capsule).not.toBeNull()

    capsule?.dispatchEvent(new Event('mouseenter'))
    await nextTick()
    expect(notifications.hoveredId.value).toBe(id)
    expect(document.body.querySelector('.island-card')?.textContent).toContain('Saved')

    capsule?.click()
    await nextTick()
    expect(notifications.pinnedId.value).toBe(id)

    document.body.querySelector<HTMLButtonElement>('.island-card__icon-button')?.click()
    await nextTick()
    expect(notifications.items.value.some((item) => item.id === id)).toBe(true)
    expect(document.body.querySelector('.island-card')).toBeNull()

    notifications.dismiss(id)
    await nextTick()
    expect(document.body.querySelector('.island-host')).toBeNull()
    wrapper.unmount()
  })

  it('shows at most three capsules and overflow badge', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    for (let index = 0; index < 5; index++) {
      notifications.notify.info({ message: `Notice ${index}`, persistent: true })
    }
    await nextTick()

    expect(document.body.querySelectorAll('.island-capsule')).toHaveLength(3)
    expect(document.body.querySelector('.island-capsule__badge')?.textContent).toBe('+2')
    wrapper.unmount()
  })

  it('opens a side notification directly without reordering capsules', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    const first = notifications.banner.error({ title: 'First', message: 'First detail' })
    const second = notifications.banner.warning({ title: 'Second', message: 'Second detail' })
    await nextTick()
    const before = [...document.body.querySelectorAll<HTMLElement>('.island-capsule')]
      .map((element) => element.dataset.notificationId)
    const secondCapsule = document.body.querySelector<HTMLButtonElement>(`[data-notification-id="${second}"]`)!

    secondCapsule.dispatchEvent(new Event('mouseenter'))
    await nextTick()
    expect(document.body.querySelector('.island-card')?.textContent).toContain('Second detail')
    expect([...document.body.querySelectorAll<HTMLElement>('.island-capsule')]
      .map((element) => element.dataset.notificationId)).toEqual(before)

    secondCapsule.click()
    await nextTick()
    expect(notifications.pinnedId.value).toBe(second)
    expect(notifications.primaryId.value).toBe(first)
    wrapper.unmount()
  })
})
