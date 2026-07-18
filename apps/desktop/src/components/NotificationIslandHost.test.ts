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
  document.body.innerHTML = ''
})

describe('NotificationIslandHost', () => {
  it('renders nothing until notified, then expands and dismisses the primary item', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    expect(document.body.querySelector('.notification-host')).toBeNull()

    notifications.notify.info({ message: 'Saved', persistent: true })
    await nextTick()
    const island = document.body.querySelector<HTMLButtonElement>('.notification-island--primary')
    expect(island).not.toBeNull()

    island?.click()
    await nextTick()
    expect(document.body.querySelector('.notification-card')?.textContent).toContain('Saved')

    document.body.querySelector<HTMLButtonElement>('.notification-card__dismiss')?.click()
    await nextTick()
    expect(document.body.querySelector('.notification-host')).toBeNull()
    wrapper.unmount()
  })

  it('shows the full queue when more than three notifications exist', async () => {
    const wrapper = mount(NotificationIslandHost, { attachTo: document.body })
    for (let index = 0; index < 5; index++) {
      notifications.notify.info({ message: `Notice ${index}`, persistent: true })
    }
    await nextTick()

    expect(document.body.querySelectorAll('.notification-island')).toHaveLength(3)
    expect(document.body.querySelector('.notification-island__count')?.textContent).toBe('+2')
    document.body.querySelector<HTMLButtonElement>('.notification-island--primary')?.click()
    await nextTick()
    expect(document.body.querySelectorAll('.notification-card__queue-item')).toHaveLength(5)
    wrapper.unmount()
  })
})
