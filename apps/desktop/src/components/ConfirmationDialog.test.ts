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

  it('opens notification detail from openDetail', async () => {
    const wrapper = mount(NotificationDetailDialog, { attachTo: document.body })
    const id = notifications.notify.error({ message: 'Backend down', title: 'Offline', persistent: true })
    notifications.openDetail(id)
    await nextTick()
    await nextTick()

    expect(document.body.querySelector('.detail-dialog')?.textContent).toContain('Backend down')
    document.body.querySelector<HTMLButtonElement>('.detail-dialog__secondary')?.click()
    await nextTick()
    expect(notifications.items.value.some((item) => item.id === id)).toBe(false)
    wrapper.unmount()
  })
})
