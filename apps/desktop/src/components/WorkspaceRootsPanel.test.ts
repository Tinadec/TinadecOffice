// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { ref } from 'vue'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import zhCN from '@/locales/zh-CN'
import { __resetNotificationsForTests, useNotifications } from '@/composables/useNotifications'
import { resetHostAccessForTests, setGatewayConnectionLiveness, setHostAccessStatus } from '@/lib/hostAccess'

const mocks = vi.hoisted(() => ({
  listProjectExposures: vi.fn<() => Promise<unknown[]>>(),
  importWorkspaceFiles: vi.fn(),
  addWorkspaceFolderPaths: vi.fn(async () => {}),
  addWorkspaceFilePaths: vi.fn(async () => {}),
  refreshProjectsAndSessions: vi.fn(async () => {}),
}))

vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return {
    ...actual,
    api: {
      ...actual.api,
      listProjectExposures: mocks.listProjectExposures,
      attachmentContentUrl: (id: string) => `/attachments/${id}`,
    },
  }
})

vi.mock('@/controllers/HomeController', async () => {
  const { ref: vueRef } = await import('vue')
  return {
    homeController: {
      currentProject: vueRef({
        id: 'proj-1',
        name: 'Demo',
        path: 'C:/demo',
        primary_root_id: 'root-1',
        roots: [{ id: 'root-1', path: 'C:/demo' }],
        files: [],
      }),
      currentPermission: vueRef('default'),
      busy: vueRef(false),
      sessions: vueRef([]),
      importWorkspaceFiles: mocks.importWorkspaceFiles,
      addWorkspaceFolderPaths: mocks.addWorkspaceFolderPaths,
      addWorkspaceFilePaths: mocks.addWorkspaceFilePaths,
      removeWorkspaceRoot: vi.fn(async () => {}),
      setPrimaryWorkspaceRoot: vi.fn(async () => {}),
      removeWorkspaceFile: vi.fn(async () => {}),
      prefillWorkspaceAttachments: vi.fn(async () => {}),
      refreshProjectsAndSessions: mocks.refreshProjectsAndSessions,
      openProject: vi.fn(),
    },
  }
})

import WorkspaceRootsPanel from './WorkspaceRootsPanel.vue'

const wrappers: VueWrapper[] = []

function mountWorkspacePanel() {
  const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en, 'zh-CN': zhCN },
  })
  const wrapper = mount(WorkspaceRootsPanel, {
    global: { plugins: [i18n] },
  })
  wrappers.push(wrapper)
  return wrapper
}

beforeEach(() => {
  vi.clearAllMocks()
  __resetNotificationsForTests()
  resetHostAccessForTests()
  setHostAccessStatus({ state: 'ready', managed: true })
  mocks.listProjectExposures.mockResolvedValue([])
})

afterEach(() => {
  wrappers.splice(0).forEach((w) => w.unmount())
  __resetNotificationsForTests()
  resetHostAccessForTests()
})

describe('WorkspaceRootsPanel notification unification', () => {
  it('routes exposures API load failure to useNotifications (key: workspace-roots) without inline alert banner and recovers on reconnect', async () => {
    const { items } = useNotifications()
    mocks.listProjectExposures.mockRejectedValueOnce(new Error('Exposures endpoint failed'))

    const wrapper = mountWorkspacePanel()
    await flushPromises()

    // Zero wild inline role="alert" / .ws-problem banners
    expect(wrapper.find('.ws-problem').exists()).toBe(false)
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)

    // Routed to notification island under key 'workspace-roots'
    expect(items.value.some((item) => item.key === 'workspace-roots' && item.level === 'error')).toBe(true)

    // Reconnect recovery reloads exposures and dismisses 'workspace-roots'
    mocks.listProjectExposures.mockResolvedValueOnce([
      {
        id: 'att-1',
        session_id: 'sess-1',
        file_name: 'readme.md',
        media_type: 'text/markdown',
        byte_length: 120,
        created_at: '2026-11-24T10:00:00Z',
        origin: 'workspace',
        delivered_to_model: true,
      },
    ])
    setGatewayConnectionLiveness(true, false)
    await flushPromises()
    setGatewayConnectionLiveness(true, true)
    await flushPromises()

    expect(items.value.some((item) => item.key === 'workspace-roots')).toBe(false)
    expect(wrapper.text()).toContain('readme.md')
  })

  it('keeps pure client-side input validation hint when dropping a file onto the folder-only control', async () => {
    const { items } = useNotifications()
    const wrapper = mountWorkspacePanel()
    await flushPromises()

    const fakeFile = new File(['hello'], 'notes.txt', { type: 'text/plain' })
    const dataTransfer = {
      items: [
        {
          kind: 'file',
          getAsFile: () => fakeFile,
          webkitGetAsEntry: () => ({ isDirectory: false }),
        },
      ],
      files: [fakeFile],
    }

    await wrapper.get('[data-testid="workspace-add-folder"]').trigger('drop', { dataTransfer })
    await flushPromises()

    expect(wrapper.find('[data-testid="workspace-input-hint"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="workspace-input-hint"]').text()).toContain('notes.txt')
    expect(items.value.some((item) => item.key === 'workspace-roots')).toBe(false)
  })
})
