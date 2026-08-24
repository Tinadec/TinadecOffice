// @vitest-environment happy-dom
import { describe, expect, it, vi, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import SnapshotOverrideDialog from './SnapshotOverrideDialog.vue'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string, fallback?: string) => fallback ?? key }),
}))

const baseProps = {
  actionId: '0b8e6f60-0000-4000-8000-000000000001',
  toolId: 'git_commit',
}

function mountDialog(open = true) {
  return mount(SnapshotOverrideDialog, {
    props: { ...baseProps, open },
    attachTo: document.body,
  })
}

// The dialog Teleports to body; query the live DOM, not the wrapper.
function q<T extends HTMLElement = HTMLElement>(selector: string): T | null {
  return document.body.querySelector<T>(selector)
}

describe('SnapshotOverrideDialog', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('requires the non-reversible acknowledgement before confirming', async () => {
    const wrapper = mountDialog()
    await vi.waitFor(() => expect(q('[data-testid="override-dialog"]')).toBeTruthy())

    const confirmBtn = q<HTMLButtonElement>('[data-testid="override-confirm"]')!
    expect(confirmBtn.disabled).toBe(true)

    const ack = q<HTMLInputElement>('[data-testid="override-acknowledge"]')!
    ack.checked = true
    ack.dispatchEvent(new Event('change'))
    await vi.waitFor(() => expect(confirmBtn.disabled).toBe(false))

    wrapper.unmount()
  })

  it('emits confirmed with the typed reason and closes', async () => {
    const wrapper = mountDialog()
    await vi.waitFor(() => expect(q('[data-testid="override-dialog"]')).toBeTruthy())

    const ack = q<HTMLInputElement>('[data-testid="override-acknowledge"]')!
    ack.checked = true
    ack.dispatchEvent(new Event('change'))

    const reasonInput = q<HTMLTextAreaElement>('[data-testid="override-reason"]')!
    reasonInput.value = 'workspace is disposable'
    reasonInput.dispatchEvent(new Event('input'))

    await vi.waitFor(() => expect(q<HTMLButtonElement>('[data-testid="override-confirm"]')!.disabled).toBe(false))
    q<HTMLButtonElement>('[data-testid="override-confirm"]')!.click()

    expect(wrapper.emitted('confirmed')?.[0]).toEqual(['workspace is disposable'])
    expect(wrapper.emitted('update:open')?.[0]).toEqual([false])
    wrapper.unmount()
  })

  it('falls back to a default audit reason when none is typed', async () => {
    const wrapper = mountDialog()
    await vi.waitFor(() => expect(q('[data-testid="override-dialog"]')).toBeTruthy())

    const ack = q<HTMLInputElement>('[data-testid="override-acknowledge"]')!
    ack.checked = true
    ack.dispatchEvent(new Event('change'))
    await vi.waitFor(() => expect(q<HTMLButtonElement>('[data-testid="override-confirm"]')!.disabled).toBe(false))
    q<HTMLButtonElement>('[data-testid="override-confirm"]')!.click()

    const reason = wrapper.emitted('confirmed')?.[0]?.[0]
    expect(String(reason)).toContain('non-reversible')
    wrapper.unmount()
  })

  it('cancel emits cancelled plus open=false without confirmed', async () => {
    const wrapper = mountDialog()
    await vi.waitFor(() => expect(q('[data-testid="override-dialog"]')).toBeTruthy())

    q<HTMLButtonElement>('[data-testid="override-cancel"]')!.click()

    expect(wrapper.emitted('cancelled')).toHaveLength(1)
    expect(wrapper.emitted('update:open')?.[0]).toEqual([false])
    expect(wrapper.emitted('confirmed')).toBeUndefined()
    wrapper.unmount()
  })
})
