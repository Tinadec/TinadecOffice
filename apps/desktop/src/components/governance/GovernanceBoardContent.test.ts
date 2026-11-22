// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import GovernanceBoardContent from './GovernanceBoardContent.vue'
import GovernanceDrawer from './GovernanceDrawer.vue'

vi.mock('@/api', () => ({
  api: {
    listApprovals: vi.fn(),
    decideApproval: vi.fn(),
    listPermissionRequests: vi.fn(),
    decidePermissionRequest: vi.fn(),
    listUserToolActions: vi.fn(),
    getUserToolAction: vi.fn(),
    listRuntimeInstances: vi.fn().mockResolvedValue([]),
  },
}))

import { api, type ApprovalDto } from '@/api'
const apiMock = vi.mocked(api, true)
const english = () => ({ global: { plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] } })

afterEach(() => {
  document.body.innerHTML = ''
  vi.clearAllMocks()
})

describe('GovernanceBoardContent & GovernanceDrawer', () => {
  it('splits the heterogeneous /approvals projection by kind into separate columns', async () => {
    setActivePinia(createPinia())
    const actionApproval: ApprovalDto = {
      id: 'aa-1',
      kind: 'user_tool',
      summary: 'Commit staged changes',
      command: 'git_commit',
      status: 'pending',
      governance_status: 'awaiting_approval',
      created_at: '2026-08-23T00:00:00Z',
    }
    const permissionProjection: ApprovalDto = {
      id: 'perm-1',
      kind: 'permission',
      summary: 'git.write capability',
      command: 'tool.invoke',
      status: 'pending',
      created_at: '2026-08-23T00:00:00Z',
    }
    apiMock.listApprovals.mockResolvedValue([actionApproval, permissionProjection])
    apiMock.listPermissionRequests.mockResolvedValue([])
    apiMock.listUserToolActions.mockResolvedValue([])

    const wrapper = mount(GovernanceBoardContent, english())
    await flushPromises()

    const approvalsCol = wrapper.find('[data-testid="column-approvals"]')
    const permissionsCol = wrapper.find('[data-testid="column-permissions"]')

    expect(approvalsCol.text()).toContain('Commit staged changes')
    expect(approvalsCol.text()).not.toContain('git.write capability')
    expect(permissionsCol.text()).toContain('git.write capability')
    expect(permissionsCol.text()).not.toContain('Commit staged changes')

    wrapper.unmount()
  })

  it('deciding an approval removes only that card and posts to the approvals endpoint', async () => {
    setActivePinia(createPinia())
    apiMock.listApprovals.mockResolvedValue([
      {
        id: 'aa-1',
        kind: 'user_tool',
        summary: 'Push main',
        command: 'git_push',
        status: 'pending',
        created_at: '2026-08-23T00:00:00Z',
      },
    ])
    apiMock.listPermissionRequests.mockResolvedValue([])
    apiMock.listUserToolActions.mockResolvedValue([])
    apiMock.decideApproval.mockResolvedValue({
      id: 'aa-1',
      kind: 'user_tool',
      summary: 'Push main',
      status: 'approved',
      created_at: '2026-08-23T00:00:00Z',
    } as ApprovalDto)

    const wrapper = mount(GovernanceBoardContent, english())
    await flushPromises()

    const approveBtn = wrapper.find('[data-testid="column-approvals"] button')
    expect(approveBtn.text()).toBeTruthy()
    await approveBtn.trigger('click')
    await flushPromises()

    expect(apiMock.decideApproval).toHaveBeenCalledWith('aa-1', 'approved', null)
    expect(wrapper.find('[data-testid="column-approvals"]').text()).not.toContain('Push main')

    wrapper.unmount()
  })

  it('polls approvals on an interval while mounted and stops after unmount', async () => {
    vi.useFakeTimers()
    try {
      setActivePinia(createPinia())
      apiMock.listApprovals.mockResolvedValue([])
      apiMock.listPermissionRequests.mockResolvedValue([])
      apiMock.listUserToolActions.mockResolvedValue([])

      const wrapper = mount(GovernanceBoardContent, english())
      await vi.advanceTimersByTimeAsync(0)
      expect(apiMock.listApprovals).toHaveBeenCalledTimes(1)

      await vi.advanceTimersByTimeAsync(12_000)
      expect(apiMock.listApprovals).toHaveBeenCalledTimes(2)
      await vi.advanceTimersByTimeAsync(12_000)
      expect(apiMock.listApprovals).toHaveBeenCalledTimes(3)

      wrapper.unmount()
      await vi.advanceTimersByTimeAsync(48_000)
      expect(apiMock.listApprovals).toHaveBeenCalledTimes(3)
    } finally {
      vi.useRealTimers()
    }
  })

  it('opens GovernanceDrawer on window tinadec:open-governance-drawer event to the requested tab', async () => {
    setActivePinia(createPinia())
    apiMock.listApprovals.mockResolvedValue([])
    apiMock.listPermissionRequests.mockResolvedValue([])
    apiMock.listUserToolActions.mockResolvedValue([])

    const wrapper = mount(GovernanceDrawer, {
      ...english(),
      global: {
        ...english().global,
        stubs: {
          Teleport: true,
          RuntimeInstancesPanel: { template: '<div data-testid="runtime-panel-stub" />', methods: { loadRuntimeInstances: async () => {} } },
        },
      },
    })
    expect(wrapper.find('[data-testid="governance-drawer"]').exists()).toBe(false)

    window.dispatchEvent(new CustomEvent('tinadec:open-governance-drawer', { detail: { tab: 'runtime' } }))
    await flushPromises()

    expect(wrapper.find('[data-testid="governance-drawer"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="governance-runtime-section"]').exists()).toBe(true)

    await wrapper.get('[data-testid="governance-drawer-close"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="governance-drawer"]').exists()).toBe(false)
    wrapper.unmount()
  })
})
