// @vitest-environment happy-dom

import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import PreAuthorizationDialog from './PreAuthorizationDialog.vue'
import { api, type PreAuthorizationDto } from '@/api'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string) => key, locale: { value: 'en' } }),
}))

vi.mock('@/api', () => ({
  api: { createPreAuthorization: vi.fn() },
}))

const createPreAuthorization = vi.mocked(api.createPreAuthorization)

const granted: PreAuthorizationDto = {
  id: 'preauth-1',
  run_id: 'run-1',
  lane_key: null,
  tool_scope: ['git_commit', 'shell'],
  parameter_constraint_hash: null,
  risk_max: 'low',
  max_uses: 3,
  use_count: 0,
  expires_at: '2026-09-08T00:00:00.000Z',
  revoked: false,
}

beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function showModal() {
    this.open = true
  }
  HTMLDialogElement.prototype.close = function close() {
    this.open = false
  }
})

beforeEach(() => {
  createPreAuthorization.mockReset()
  document.body.innerHTML = ''
})

function qs<T extends HTMLElement = HTMLElement>(selector: string): T | null {
  return document.body.querySelector<T>(selector)
}

async function setInput(selector: string, value: string): Promise<void> {
  const input = qs<HTMLInputElement>(selector)
  if (!input) throw new Error(`missing input ${selector}`)
  input.value = value
  input.dispatchEvent(new Event('input'))
  await nextTick()
}

async function openDialog(): Promise<ReturnType<typeof mount>> {
  // Dialog content is teleported to document.body; drive it via setProps + body queries.
  const wrapper = mount(PreAuthorizationDialog, { props: { open: false } })
  await wrapper.setProps({ open: true })
  await nextTick()
  return wrapper
}

async function flush(): Promise<void> {
  await nextTick()
  await nextTick()
  await nextTick()
}

describe('PreAuthorizationDialog', () => {
  it('keeps submit disabled until run id and tool scope are present', async () => {
    const wrapper = await openDialog()
    const submit = qs<HTMLButtonElement>('[data-testid="pre-auth-submit"]')
    expect(submit).not.toBeNull()
    expect(submit!.disabled).toBe(true)

    await setInput('[data-testid="pre-auth-run-id"]', 'run-1')
    expect(qs<HTMLButtonElement>('[data-testid="pre-auth-submit"]')!.disabled).toBe(true)
    await setInput('[data-testid="pre-auth-tools"]', 'git_commit, shell')
    expect(qs<HTMLButtonElement>('[data-testid="pre-auth-submit"]')!.disabled).toBe(false)
    wrapper.unmount()
  })

  it('posts the parsed form once and shows the granted record', async () => {
    createPreAuthorization.mockResolvedValue(granted)
    const wrapper = await openDialog()

    await setInput('[data-testid="pre-auth-run-id"]', ' run-1 ')
    await setInput('[data-testid="pre-auth-lane"]', ' l2 ')
    await setInput('[data-testid="pre-auth-tools"]', 'git_commit, , shell')
    await setInput('[data-testid="pre-auth-max-uses"]', '3')
    await setInput('[data-testid="pre-auth-expires"]', '2026-09-08T10:30')
    qs<HTMLButtonElement>('[data-testid="pre-auth-submit"]')!.click()
    await flush()

    expect(createPreAuthorization).toHaveBeenCalledTimes(1)
    expect(createPreAuthorization).toHaveBeenCalledWith({
      run_id: 'run-1',
      tool_scope: ['git_commit', 'shell'],
      risk_max: 'low',
      max_uses: 3,
      lane_key: 'l2',
      expires_at: new Date('2026-09-08T10:30').toISOString(),
    })
    expect(qs('[data-testid="pre-auth-success"]')).not.toBeNull()
    expect(qs('[data-testid="pre-auth-record"]')?.textContent).toContain('preauth-1')
    expect(wrapper.emitted('created')?.length).toBe(1)
    wrapper.unmount()
  })

  it('surfaces server errors and stays open for retry', async () => {
    createPreAuthorization.mockRejectedValue(
      new Error('Wildcard tool grants are not allowed in a pre-authorization.'),
    )
    const wrapper = await openDialog()

    await setInput('[data-testid="pre-auth-run-id"]', 'run-1')
    await setInput('[data-testid="pre-auth-tools"]', '*')
    qs<HTMLButtonElement>('[data-testid="pre-auth-submit"]')!.click()
    await flush()

    expect(qs('[data-testid="pre-auth-error"]')?.textContent).toContain('Wildcard')
    expect(qs('[data-testid="pre-auth-success"]')).toBeNull()
    expect(createPreAuthorization).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })
})
