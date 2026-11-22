// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import RecoveryCheckDialog from './RecoveryCheckDialog.vue'

vi.mock('@/api', () => ({
  api: {
    listUserToolActions: vi.fn(),
    getUserToolAction: vi.fn(),
    decideUserToolActionRecovery: vi.fn(),
  },
}))

import { api, type UserToolActionDto } from '@/api'

const apiMock = vi.mocked(api, true)
const english = () => ({ global: { plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] } })

afterEach(() => {
  document.body.innerHTML = ''
  vi.clearAllMocks()
})

describe('RecoveryCheckDialog', () => {
  it('opens on tinadec:open-recovery-check event and submits mark_completed', async () => {
    setActivePinia(createPinia())
    const action: UserToolActionDto = {
      id: 'act-1',
      tenant_id: 't',
      workspace_id: 'w',
      session_id: 's',
      tool_id: 'git_commit',
      status: 'outcome_unknown',
      audit_reference: 'audit-ref-1',
      snapshot_id: 'snap-99',
      created_at: '2026-10-01T00:00:00Z',
      updated_at: '2026-10-01T00:00:00Z',
    } as unknown as UserToolActionDto
    apiMock.getUserToolAction.mockResolvedValue(action)
    apiMock.decideUserToolActionRecovery.mockResolvedValue({
      ...action,
      status: 'completed',
      recovery_decision: 'mark_completed',
    })

    const wrapper = mount(RecoveryCheckDialog, {
      ...english(),
      global: {
        ...english().global,
        stubs: { Teleport: true },
      },
    })
    expect(wrapper.find('[data-testid="recovery-check-dialog"]').exists()).toBe(false)

    window.dispatchEvent(new CustomEvent('tinadec:open-recovery-check', { detail: { actionId: 'act-1' } }))
    await flushPromises()

    expect(wrapper.find('[data-testid="recovery-check-dialog"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('audit-ref-1')

    await wrapper.get('[data-testid="recovery-mark-completed"]').trigger('click')
    await flushPromises()

    expect(apiMock.decideUserToolActionRecovery).toHaveBeenCalledWith('act-1', expect.objectContaining({
      decision: 'mark_completed',
    }))
    expect(wrapper.find('[data-testid="recovery-check-dialog"]').exists()).toBe(false)
    wrapper.unmount()
  })
})
