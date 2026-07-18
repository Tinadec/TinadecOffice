// @vitest-environment happy-dom

import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import ConfirmationDialog from './ConfirmationDialog.vue'
import { resolveConfirmation, useNotifications } from '@/composables/useNotifications'

vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }))

const notifications = useNotifications()

beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function showModal() { this.open = true }
  HTMLDialogElement.prototype.close = function close() { this.open = false }
})

afterEach(() => {
  while (notifications.currentConfirmation.value) {
    resolveConfirmation(notifications.currentConfirmation.value.id, false)
  }
  document.body.innerHTML = ''
})

describe('ConfirmationDialog', () => {
  it('resolves one queued confirmation per activation', async () => {
    const wrapper = mount(ConfirmationDialog, { attachTo: document.body })
    const first = notifications.confirm({ message: 'First?', destructive: true })
    const second = notifications.confirm({ message: 'Second?' })
    await nextTick()
    await nextTick()

    const confirmButton = document.body.querySelector<HTMLButtonElement>('.confirmation-dialog__confirm')!
    confirmButton.click()
    confirmButton.click()
    await expect(first).resolves.toBe(true)
    expect(notifications.currentConfirmation.value?.message).toBe('Second?')

    resolveConfirmation(notifications.currentConfirmation.value!.id, false)
    await expect(second).resolves.toBe(false)
    wrapper.unmount()
  })
})
