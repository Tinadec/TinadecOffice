// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'
import GovernanceBoardPage from './GovernanceBoardPage.vue'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}))

vi.mock('@/api', () => ({
  api: {
    listApprovals: vi.fn(),
    decideApproval: vi.fn(),
    listPermissionRequests: vi.fn(),
    decidePermissionRequest: vi.fn(),
    listUserToolActions: vi.fn(),
    getUserToolAction: vi.fn(),
  },
}))

import { api, type ApprovalDto } from '@/api'
const apiMock = vi.mocked(api, true)

afterEach(() => {
  document.body.innerHTML = ''
  vi.clearAllMocks()
})

describe('GovernanceBoardPage', () => {
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

    const wrapper = mount(GovernanceBoardPage)
    await flushPromises()

    const approvalsCol = wrapper.find('[data-testid="column-approvals"]')
    const permissionsCol = wrapper.find('[data-testid="column-permissions"]')

    expect(approvalsCol.text()).toContain('Commit staged changes')
    expect(approvalsCol.text()).not.toContain('git.write capability')
    expect(permissionsCol.text()).toContain('git.write capability')
    // The permission-kind row keeps its own id in its own column.
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

    const wrapper = mount(GovernanceBoardPage)
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

      const wrapper = mount(GovernanceBoardPage)
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

  it('pauses polling while the page is hidden and resumes when visible again', async () => {
    vi.useFakeTimers()
    try {
      setActivePinia(createPinia())
      apiMock.listApprovals.mockResolvedValue([])
      apiMock.listPermissionRequests.mockResolvedValue([])
      apiMock.listUserToolActions.mockResolvedValue([])
      const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')

      const wrapper = mount(GovernanceBoardPage)
      await vi.advanceTimersByTimeAsync(0)
      expect(apiMock.listApprovals).toHaveBeenCalledTimes(1)

      await vi.advanceTimersByTimeAsync(36_000)
      expect(apiMock.listApprovals).toHaveBeenCalledTimes(1)

      visibility.mockRestore()
      await vi.advanceTimersByTimeAsync(12_000)
      expect(apiMock.listApprovals).toHaveBeenCalledTimes(2)

      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })
})
