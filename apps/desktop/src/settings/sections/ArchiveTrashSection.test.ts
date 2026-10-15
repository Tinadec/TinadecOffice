// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ArchiveTrashSection from './ArchiveTrashSection.vue'

vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string, params?: Record<string, string>) => (params ? `${key}:${params.name}` : key) }),
}))

const { confirmMock, generatedApi, scopeApi } = vi.hoisted(() => ({
  confirmMock: vi.fn(async (..._args: unknown[]) => true),
  scopeApi: { listStorageScopes: vi.fn(), listSessions: vi.fn() },
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
vi.mock('@/api', () => ({ api: scopeApi }))

function stubLists() {
  generatedApi.listProjects.mockImplementation(async (status?: string) => {
    if (status === 'archived') return [{ id: 'p-archived', name: 'Archived project', path: 'C:/a', created_at: '2026-08-27T00:00:00Z' }]
    if (status === 'trashed') return [{ id: 'p-trashed', name: 'Trashed project', path: 'C:/t', created_at: '2026-08-27T00:00:00Z' }]
    return [{ id: 'p-active', name: 'Active project', path: 'C:/live', created_at: '2026-08-27T00:00:00Z' }]
  })
  generatedApi.listSessions.mockImplementation(async (_projectId?: string, status?: string) => {
    if (status === 'archived') return [
      { id: 's-archived', project_id: 'p-active', title: 'Archived session', status: 'ready', created_at: '2026-08-27T00:00:00Z', updated_at: '2026-08-27T00:00:00Z' },
      // A free conversation has no project; its row must not render a blank parent.
      { id: 's-free', project_id: null, title: 'Free conversation', status: 'ready', created_at: '2026-08-27T00:00:00Z', updated_at: '2026-08-27T00:00:00Z' },
    ]
    if (status === 'trashed') return [{ id: 's-trashed', project_id: 'p-trashed', title: 'Trashed session', status: 'ready', created_at: '2026-08-27T00:00:00Z', updated_at: '2026-08-27T00:00:00Z' }]
    return []
  })
}

describe('ArchiveTrashSection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    confirmMock.mockResolvedValue(true)
    stubLists()
    scopeApi.listStorageScopes.mockResolvedValue([{ storage_id: 'user' }])
    scopeApi.listSessions.mockImplementation((_project?: string, _signal?: AbortSignal, _storage?: string, status?: string) => generatedApi.listSessions(_project, status))
  })

  it('lists archived and trashed projects and sessions with their parent project', async () => {
    const wrapper = mount(ArchiveTrashSection)
    await flushPromises()
    expect(generatedApi.listProjects).toHaveBeenCalledWith('archived', expect.objectContaining({ signal: expect.any(AbortSignal) }))
    expect(generatedApi.listProjects).toHaveBeenCalledWith('trashed', expect.objectContaining({ signal: expect.any(AbortSignal) }))
    expect(wrapper.text()).toContain('Archived project')
    expect(wrapper.text()).toContain('Trashed session')
    expect(wrapper.text()).toContain('settings.sessionInProject:Trashed project')
  })

  it('labels an archived free conversation instead of rendering an empty parent', async () => {
    const wrapper = mount(ArchiveTrashSection)
    await flushPromises()
    expect(wrapper.text()).toContain('Free conversation')
    expect(wrapper.text()).toContain('settings.sessionInProject:sidebar.freeConversations')
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
    expect(generatedApi.purgeSession).toHaveBeenCalledWith('user::s-trashed')
  })

  it('retains failed project history while refreshing other scopes and routes restores to the source', async () => {
    scopeApi.listStorageScopes.mockResolvedValue([{ storage_id: 'user' }, { storage_id: 'project-scope' }])
    scopeApi.listSessions.mockImplementation(async (_project, _signal, storage, status) => status === 'archived'
      ? [{ id: 'same-id', title: storage + ' history', project_id: storage === 'user' ? null : 'p-active' }] : [])
    const wrapper = mount(ArchiveTrashSection)
    await flushPromises()
    expect(wrapper.findAll('[data-testid="archived-session-row"]')).toHaveLength(2)
    const projectRow = wrapper.findAll('[data-testid="archived-session-row"]').find(row => row.text().includes('project-scope history'))!
    scopeApi.listSessions.mockImplementation(async (_project, _signal, storage, status) => {
      if (storage === 'project-scope') throw new Error('project database unavailable')
      return status === 'archived' ? [{ id: 'new-user', title: 'updated user history', project_id: null }] : []
    })
    await projectRow.find('button').trigger('click')
    await flushPromises()
    expect(generatedApi.restoreSession).toHaveBeenCalledWith('project-scope::same-id')
    expect(wrapper.text()).toContain('updated user history')
    expect(wrapper.text()).toContain('project-scope history')
    expect(wrapper.text()).toContain('project database unavailable')
    expect(wrapper.text()).not.toContain('settings.emptyArchived')
    expect(scopeApi.listSessions).toHaveBeenCalledWith(undefined, expect.any(AbortSignal), 'project-scope', 'archived')
    wrapper.unmount()
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
