// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ArchiveTrashSection from './ArchiveTrashSection.vue'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string, params?: Record<string, string>) => (params ? `${key}:${params.name}` : key) }),
}))

const { confirmMock, generatedApi } = vi.hoisted(() => ({
  confirmMock: vi.fn(async () => true),
  generatedApi: {
    listProjects: vi.fn(),
    listSessions: vi.fn(),
    restoreProject: vi.fn(async () => undefined),
    restoreSession: vi.fn(async () => undefined),
    purgeProject: vi.fn(async () => undefined),
    purgeSession: vi.fn(async () => undefined),
  },
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    confirm: (...args: unknown[]) => confirmMock(...args),
    notify: { error: vi.fn(), success: vi.fn() },
  }),
}))

vi.mock('@/generated/client', () => ({ generatedApi }))

function stubLists() {
  generatedApi.listProjects.mockImplementation(async (status?: string) => {
    if (status === 'archived') return [{ id: 'p-archived', name: 'Archived project', path: 'C:/a', created_at: '2026-08-27T00:00:00Z' }]
    if (status === 'trashed') return [{ id: 'p-trashed', name: 'Trashed project', path: 'C:/t', created_at: '2026-08-27T00:00:00Z' }]
    return [{ id: 'p-active', name: 'Active project', path: 'C:/live', created_at: '2026-08-27T00:00:00Z' }]
  })
  generatedApi.listSessions.mockImplementation(async (_projectId?: string, status?: string) => {
    if (status === 'archived') return [{ id: 's-archived', project_id: 'p-active', title: 'Archived session', status: 'ready', created_at: '2026-08-27T00:00:00Z', updated_at: '2026-08-27T00:00:00Z' }]
    if (status === 'trashed') return [{ id: 's-trashed', project_id: 'p-trashed', title: 'Trashed session', status: 'ready', created_at: '2026-08-27T00:00:00Z', updated_at: '2026-08-27T00:00:00Z' }]
    return []
  })
}

describe('ArchiveTrashSection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    confirmMock.mockResolvedValue(true)
    stubLists()
  })

  it('lists archived and trashed projects and sessions with their parent project', async () => {
    const wrapper = mount(ArchiveTrashSection)
    await flushPromises()
    expect(generatedApi.listProjects).toHaveBeenCalledWith('archived')
    expect(generatedApi.listProjects).toHaveBeenCalledWith('trashed')
    expect(wrapper.text()).toContain('Archived project')
    expect(wrapper.text()).toContain('Trashed session')
    expect(wrapper.text()).toContain('settings.sessionInProject:Trashed project')
  })

  it('restores an archived project without confirmation', async () => {
    const wrapper = mount(ArchiveTrashSection)
    await flushPromises()
    const restoreButtons = wrapper.findAll('[data-testid="archived-project-row"] button')
    await restoreButtons[0].trigger('click')
    await flushPromises()
    expect(generatedApi.restoreProject).toHaveBeenCalledWith('p-archived')
    expect(confirmMock).not.toHaveBeenCalled()
  })

  it('purges a trashed session only after destructive confirmation', async () => {
    const wrapper = mount(ArchiveTrashSection)
    await flushPromises()
    const buttons = wrapper.findAll('[data-testid="trashed-session-row"] button')
    await buttons[1].trigger('click')
    await flushPromises()
    expect(confirmMock).toHaveBeenCalledTimes(1)
    expect(generatedApi.purgeSession).toHaveBeenCalledWith('s-trashed')
  })

  it('does not purge when the confirmation is rejected', async () => {
    confirmMock.mockResolvedValueOnce(false)
    const wrapper = mount(ArchiveTrashSection)
    await flushPromises()
    const buttons = wrapper.findAll('[data-testid="trashed-project-row"] button')
    await buttons[1].trigger('click')
    await flushPromises()
    expect(generatedApi.purgeProject).not.toHaveBeenCalled()
  })
})
