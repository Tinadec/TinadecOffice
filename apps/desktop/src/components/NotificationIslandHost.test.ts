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
  it('renders nothing until notified, hover peeks, click opens detail', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    expect(document.body.querySelector('.island-host')).toBeNull()

    const id = notifications.notify.info({ message: 'Saved', persistent: true })
    await nextTick()
    const capsule = document.body.querySelector<HTMLButtonElement>('.island-capsule--primary')
    expect(capsule).not.toBeNull()

    capsule?.dispatchEvent(new Event('mouseenter'))
    await nextTick()
    expect(notifications.hoveredId.value).toBe(id)
    expect(capsule?.classList.contains('island-capsule--peek')).toBe(true)

    capsule?.click()
    await nextTick()
    expect(notifications.detailId.value).toBe(id)

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
})
