// @vitest-environment happy-dom

import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import NotificationDetailDialog from './NotificationDetailDialog.vue'
import { resolveConfirmation, useNotifications } from '@/composables/useNotifications'

vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }))

const notifications = useNotifications()

beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function showModal() {
    this.open = true
  }
  HTMLDialogElement.prototype.close = function close() {
    this.open = false
  }
})

afterEach(() => {
  while (notifications.currentConfirmation.value) {
    resolveConfirmation(notifications.currentConfirmation.value.id, false)
  }
  for (const item of [...notifications.items.value]) notifications.dismiss(item.id)
  notifications.closeDetail()
  document.body.innerHTML = ''
})

describe('NotificationDetailDialog', () => {
  it('resolves one queued confirmation per activation', async () => {
    const wrapper = mount(NotificationDetailDialog, { attachTo: document.body })
    const first = notifications.confirm({ message: 'First?', destructive: true })
    const second = notifications.confirm({ message: 'Second?' })
    await nextTick()
    await nextTick()

    const confirmButton = document.body.querySelector<HTMLButtonElement>('.detail-dialog__primary')!
    confirmButton.click()
    confirmButton.click()
    await expect(first).resolves.toBe(true)
    expect(notifications.currentConfirmation.value?.message).toBe('Second?')

    resolveConfirmation(notifications.currentConfirmation.value!.id, false)
    await expect(second).resolves.toBe(false)
    wrapper.unmount()
  })

  it('closes persistent detail without dismissing the notification', async () => {
    const wrapper = mount(NotificationDetailDialog, { attachTo: document.body })
    const id = notifications.notify.error({ message: 'Backend down', title: 'Offline', persistent: true })
    notifications.openDetail(id)
    await nextTick()
    await nextTick()

    expect(document.body.querySelector('.detail-dialog')?.textContent).toContain('Backend down')
    expect(document.body.querySelector('.detail-dialog__dismiss')).toBeNull()
    document.body.querySelector<HTMLButtonElement>('.detail-dialog__secondary')?.click()
    await nextTick()
    expect(notifications.items.value.some((item) => item.id === id)).toBe(true)
    expect(notifications.detailId.value).toBeNull()
    wrapper.unmount()
  })

  it('shows details and retry failures in the reusable dialog', async () => {
    const wrapper = mount(NotificationDetailDialog, { attachTo: document.body })
    const id = notifications.banner.error({
      title: 'Offline',
      message: 'The backend is unavailable.',
      details: 'Connection refused on port 48730.',
      action: { label: 'Retry', run: () => { throw new Error('Connection refused') } },
    })
    notifications.openDetail(id)
    await nextTick()
    await nextTick()

    expect(document.body.querySelector('.detail-dialog__details')?.textContent).toContain('port 48730')
    document.body.querySelector<HTMLButtonElement>('.detail-dialog__primary')?.click()
    await nextTick()
    await nextTick()
    expect(document.body.querySelector('.detail-dialog__error')?.textContent).toContain('Connection refused')
    expect(notifications.items.value.some((item) => item.id === id)).toBe(true)
    wrapper.unmount()
  })
})
