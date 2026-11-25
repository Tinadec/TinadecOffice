// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import zhCN from '@/locales/zh-CN'
import { __resetNotificationsForTests, useNotifications } from '@/composables/useNotifications'
import {
  resetHostAccessForTests,
  setGatewayConnectionLiveness,
  setHostAccessStatus,
} from '@/lib/hostAccess'
import {
  __resetUiPreferencesForTests,
  DEFAULT_UI_APPEARANCE_PREFERENCES,
  DEFAULT_UI_INTERACTION_PREFERENCES,
  UI_PREFERENCES_MIGRATED_KEY,
  useUiPreferences,
} from '@/composables/useUiPreferences'
import { __resetThemeForTests } from '@/composables/useTheme'
import { __resetPanelStylesForTests } from '@/composables/usePanelStyles'
import LivingWorkspaceFolder from '@/components/workspace/LivingWorkspaceFolder.vue'

const mocks = vi.hoisted(() => ({
  listProjectExposures: vi.fn<() => Promise<unknown[]>>(),
  importWorkspaceFiles: vi.fn(),
  addWorkspaceFolderPaths: vi.fn(async () => {}),
  addWorkspaceFilePaths: vi.fn(async () => {}),
  refreshProjectsAndSessions: vi.fn(async () => {}),
  getUiPreferences: vi.fn(),
  patchUiPreferences: vi.fn(),
  putUiPreferences: vi.fn(),
}))

vi.mock('@/lib/scopedEventSource', () => ({
  ScopedEventSource: class {
    addEventListener() {}
    removeEventListener() {}
    close() {}
  },
}))

vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return {
    ...actual,
    api: {
      ...actual.api,
      listProjectExposures: mocks.listProjectExposures,
      attachmentContentUrl: (id: string) => `/attachments/${id}`,
      getUiPreferences: mocks.getUiPreferences,
      patchUiPreferences: mocks.patchUiPreferences,
      putUiPreferences: mocks.putUiPreferences,
      uiPreferencesEventsUrl: () => 'http://127.0.0.1:48730/api/v1/preferences/events',
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
        files: [
          { id: 'file-img', path: 'C:/demo/assets/hero.png' },
          { id: 'file-doc', path: 'C:/demo/docs/guide.md' },
        ],
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
const pathMap = new Map<File, string>()

function makeDroppedFile(name: string, absPath: string, type = 'text/plain'): File {
  const f = new File(['x'], name, { type })
  pathMap.set(f, absPath)
  return f
}

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
  pathMap.clear()
  localStorage.clear()
  localStorage.setItem(UI_PREFERENCES_MIGRATED_KEY, '1')
  __resetUiPreferencesForTests()
  __resetThemeForTests()
  __resetPanelStylesForTests()
  __resetNotificationsForTests()
  resetHostAccessForTests()
  setHostAccessStatus({ state: 'ready', managed: true })

  ;(window as unknown as { tinadec: Record<string, unknown> }).tinadec = {
    gatewayUrl: () => 'http://127.0.0.1:48730',
    pathForFile: (file: File) => pathMap.get(file) ?? '',
  }

  mocks.listProjectExposures.mockResolvedValue([])
  mocks.getUiPreferences.mockResolvedValue({
    version: 1,
    appearance: { ...DEFAULT_UI_APPEARANCE_PREFERENCES },
    interaction: { ...DEFAULT_UI_INTERACTION_PREFERENCES },
    file_path: 'C:/Users/demo/.tinadec/preferences.toml',
    content_hash: 'h1',
  })
  mocks.patchUiPreferences.mockImplementation(async (patch) => ({
    version: 1,
    appearance: { ...DEFAULT_UI_APPEARANCE_PREFERENCES, ...(patch?.appearance ?? {}) },
    interaction: { ...DEFAULT_UI_INTERACTION_PREFERENCES, ...(patch?.interaction ?? {}) },
    file_path: 'C:/Users/demo/.tinadec/preferences.toml',
    content_hash: 'h2',
  }))
  mocks.importWorkspaceFiles.mockImplementation(async (paths: string[]) => ({
    imported: paths.map((p) => ({ name: p.split('/').pop() ?? p, path: p, bytes: 10 })),
    skipped: [],
  }))
})

afterEach(() => {
  vi.useRealTimers()
  wrappers.splice(0).forEach((w) => w.unmount())
  __resetUiPreferencesForTests()
  __resetThemeForTests()
  __resetPanelStylesForTests()
  __resetNotificationsForTests()
  resetHostAccessForTests()
  localStorage.clear()
})

describe('WorkspaceRootsPanel & LivingWorkspaceFolder behavior', () => {
  it('routes exposures API load failure to useNotifications without inline alert banner, keeps bottom accordions collapsed by default, and recovers on reconnect', async () => {
    const { items } = useNotifications()
    mocks.listProjectExposures.mockRejectedValueOnce(new Error('Exposures endpoint failed'))

    const wrapper = mountWorkspacePanel()
    await flushPromises()

    expect(wrapper.find('.ws-problem').exists()).toBe(false)
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(items.value.some((item) => item.key === 'workspace-roots' && item.level === 'error')).toBe(true)

    // Exposures and Access sections start quietly collapsed by default
    expect(wrapper.get('[data-testid="workspace-exposures-toggle"]').attributes('aria-expanded')).toBe('false')
    expect(wrapper.get('[data-testid="workspace-access-toggle"]').attributes('aria-expanded')).toBe('false')

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

    // Expanding exposures reveals the loaded record
    await wrapper.get('[data-testid="workspace-exposures-toggle"]').trigger('click')
    expect(wrapper.text()).toContain('readme.md')
  })

  it('keeps pure client-side input validation hint when dropping a file onto the folder-only control', async () => {
    const { items } = useNotifications()
    const wrapper = mountWorkspacePanel()
    await flushPromises()

    const fakeFile = makeDroppedFile('notes.txt', 'C:/outside/notes.txt')
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

  it('renders 3D LivingWorkspaceFolder pocket cards, contains zero inline view/drop switchers, and auto-switches grid vs list from useUiPreferences', async () => {
    const wrapper = mountWorkspacePanel()
    await flushPromises()

    // Living folder reflects tone & pocket stack items
    const folder = wrapper.get('[data-testid="living-workspace-folder"]')
    expect(folder.attributes('data-folder-tone')).toBe('amber')
    expect(wrapper.findAll('.pocket-card').length).toBe(3)

    // Photo thumbnail uses /api/v1/fs/media?token=... and falls back cleanly on @error
    const photoImg = wrapper.find('.pocket-photo-img')
    expect(photoImg.exists()).toBe(true)
    expect(photoImg.attributes('src')).toContain('/api/v1/fs/media?token=')
    await photoImg.trigger('error')
    await flushPromises()
    expect(wrapper.find('.pocket-photo-fallback').exists()).toBe(true)

    // Zero inline view or drop-mode switchers inside WorkspaceRootsPanel
    expect(wrapper.find('[data-testid="workspace-view-grid"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="workspace-view-list"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="workspace-drop-action-link"]').exists()).toBe(false)

    // Default view is grid; changing preference switches live to list
    expect(wrapper.find('[data-testid="workspace-items-grid"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="workspace-items-list"]').exists()).toBe(false)

    const prefs = useUiPreferences()
    await prefs.updateAppearance({ workspace_view: 'list', workspace_folder_tone: 'teal' })
    await flushPromises()

    expect(wrapper.find('[data-testid="workspace-items-grid"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="workspace-items-list"]').exists()).toBe(true)
    expect(folder.attributes('data-folder-tone')).toBe('teal')
  })

  it('handles mixed folder+file drop batches, Alt-key live inversion, and ask-mode inline 2-button confirmation', async () => {
    const wrapper = mountWorkspacePanel()
    await flushPromises()

    const folderEl = wrapper.get('[data-testid="living-workspace-folder"]')
    const droppedDir = makeDroppedFile('shared-lib', 'C:/repos/shared-lib')
    const droppedFile = makeDroppedFile('spec.pdf', 'C:/docs/spec.pdf', 'application/pdf')

    const mixedTransfer = {
      items: [
        {
          kind: 'file',
          getAsFile: () => droppedDir,
          webkitGetAsEntry: () => ({ isDirectory: true }),
        },
        {
          kind: 'file',
          getAsFile: () => droppedFile,
          webkitGetAsEntry: () => ({ isDirectory: false }),
        },
      ],
      files: [droppedDir, droppedFile],
    }

    // 1. Default 'link' mode authorizes folders and links files simultaneously
    await folderEl.trigger('drop', { dataTransfer: mixedTransfer })
    await flushPromises()
    expect(mocks.addWorkspaceFolderPaths).toHaveBeenCalledWith(['C:/repos/shared-lib'])
    expect(mocks.addWorkspaceFilePaths).toHaveBeenCalledWith(['C:/docs/spec.pdf'])
    expect(mocks.importWorkspaceFiles).not.toHaveBeenCalled()

    // 2. Holding Alt in 'link' mode flips file handling to 'copy'
    mocks.addWorkspaceFilePaths.mockClear()
    const fileOnlyTransfer = {
      items: [
        {
          kind: 'file',
          getAsFile: () => droppedFile,
          webkitGetAsEntry: () => ({ isDirectory: false }),
        },
      ],
      files: [droppedFile],
    }
    await folderEl.trigger('drop', { dataTransfer: fileOnlyTransfer, altKey: true })
    await flushPromises()
    expect(mocks.importWorkspaceFiles).toHaveBeenCalledWith(['C:/docs/spec.pdf'])
    expect(mocks.addWorkspaceFilePaths).not.toHaveBeenCalled()

    // 3. 'ask' mode opens lightweight 2-button bubble below folder before executing
    mocks.importWorkspaceFiles.mockClear()
    const prefs = useUiPreferences()
    await prefs.updateInteraction({ workspace_drop_action: 'ask' })
    await flushPromises()

    await folderEl.trigger('drop', { dataTransfer: fileOnlyTransfer })
    await flushPromises()
    expect(wrapper.find('[data-testid="workspace-drop-ask-bubble"]').exists()).toBe(true)
    expect(mocks.importWorkspaceFiles).not.toHaveBeenCalled()
    expect(mocks.addWorkspaceFilePaths).not.toHaveBeenCalled()

    await wrapper.get('[data-testid="workspace-drop-ask-copy"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="workspace-drop-ask-bubble"]').exists()).toBe(false)
    expect(mocks.importWorkspaceFiles).toHaveBeenCalledWith(['C:/docs/spec.pdf'])
  })

  it('loads persisted mixed item order from localStorage and renders unified root+file sequence in both grid and list views', async () => {
    localStorage.setItem(
      'tinadec:workspace-item-order:proj-1',
      JSON.stringify(['file:file-doc', 'root:root-1', 'file:file-img']),
    )

    const wrapper = mountWorkspacePanel()
    await flushPromises()

    const pocketKeys = wrapper
      .findAll('[data-testid="living-folder-pocket"] [data-pocket-card-key]')
      .map((el) => el.attributes('data-pocket-card-key'))
    expect(pocketKeys).toEqual(['file:file-doc', 'root:root-1', 'file:file-img'])

    const gridKeys = wrapper
      .findAll('[data-testid="workspace-grid-card"]')
      .map((el) => el.attributes('data-workspace-item-key'))
    expect(gridKeys).toEqual(['file:file-doc', 'root:root-1', 'file:file-img'])

    const prefs = useUiPreferences()
    await prefs.updateAppearance({ workspace_view: 'list' })
    await flushPromises()

    const listKeys = wrapper
      .findAll('[data-testid="workspace-list-row"]')
      .map((el) => el.attributes('data-workspace-item-key'))
    expect(listKeys).toEqual(['file:file-doc', 'root:root-1', 'file:file-img'])
  })

  it('synchronizes Zone 2 DOM order and persists to localStorage when LivingWorkspaceFolder emits reorder-cards', async () => {
    const wrapper = mountWorkspacePanel()
    await flushPromises()

    expect(
      wrapper
        .findAll('[data-testid="workspace-grid-card"]')
        .map((el) => el.attributes('data-workspace-item-key')),
    ).toEqual(['root:root-1', 'file:file-img', 'file:file-doc'])

    const folderComponent = wrapper.findComponent(LivingWorkspaceFolder)
    folderComponent.vm.$emit('reorder-cards', ['file:file-img', 'file:file-doc', 'root:root-1'])
    await flushPromises()

    expect(
      wrapper
        .findAll('[data-testid="workspace-grid-card"]')
        .map((el) => el.attributes('data-workspace-item-key')),
    ).toEqual(['file:file-img', 'file:file-doc', 'root:root-1'])

    const persisted = JSON.parse(localStorage.getItem('tinadec:workspace-item-order:proj-1') ?? '[]')
    expect(persisted).toEqual(['file:file-img', 'file:file-doc', 'root:root-1'])
  })

  it('highlights matching Zone 2 item, calls scrollIntoView on select-card, and updates live hover caption', async () => {
    vi.useFakeTimers()
    const scrollSpy = vi.fn()
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView
    HTMLElement.prototype.scrollIntoView = scrollSpy

    try {
      const wrapper = mountWorkspacePanel()
      await flushPromises()

      const docPocketBtn = wrapper.get('[data-pocket-card-key="file:file-doc"]')
      await docPocketBtn.trigger('mouseenter')
      await flushPromises()
      expect(wrapper.get('[data-testid="living-folder-caption"]').text()).toContain('guide.md')

      await docPocketBtn.trigger('click')
      await flushPromises()

      const docGridCard = wrapper.get('[data-workspace-item-key="file:file-doc"]')
      const rootGridCard = wrapper.get('[data-workspace-item-key="root:root-1"]')
      expect(docGridCard.classes()).toContain('is-highlighted')
      expect(rootGridCard.classes()).not.toContain('is-highlighted')
      expect(scrollSpy).toHaveBeenCalledWith({ behavior: 'smooth', block: 'nearest' })

      vi.advanceTimersByTime(1450)
      await flushPromises()
      expect(wrapper.get('[data-workspace-item-key="file:file-doc"]').classes()).not.toContain(
        'is-highlighted',
      )
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView
    }
  })
})
