// @vitest-environment happy-dom
import { createI18n } from 'vue-i18n'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import zhCN from '@/locales/zh-CN'
import {
  openServerDirectoryPicker,
  registerServerDirectoryPicker,
} from '@/lib/hostConnection'

const mocks = vi.hoisted(() => ({
  browseFilesystem: vi.fn(),
  createHostDirectory: vi.fn(),
}))

vi.mock('@/api', () => ({
  api: {
    browseFilesystem: mocks.browseFilesystem,
    createHostDirectory: mocks.createHostDirectory,
  },
}))

import ServerDirectoryPickerModal from './ServerDirectoryPickerModal.vue'

function makeBrowseResponse(overrides: Record<string, unknown> = {}) {
  return {
    platform: 'windows',
    separator: '\\',
    home_path: 'C:\\Users\\demo',
    current_path: 'C:\\Users\\demo',
    parent_path: 'C:\\Users',
    roots: [
      { name: 'Home', path: 'C:\\Users\\demo', kind: 'home' },
      { name: 'C:\\', path: 'C:\\', kind: 'drive' },
    ],
    entries: [
      {
        name: 'Projects',
        path: 'C:\\Users\\demo\\Projects',
        kind: 'directory',
        is_dir: true,
        has_children: true,
        is_git_repo: true,
      },
      {
        name: 'Docs',
        path: 'C:\\Users\\demo\\Docs',
        kind: 'directory',
        is_dir: true,
        has_children: false,
        is_git_repo: false,
      },
    ],
    truncated: false,
    ...overrides,
  }
}

let previousTinadec: unknown
let wrapper: VueWrapper | null = null

function mountModal() {
  const i18n = createI18n({
    legacy: false,
    locale: 'zh-CN',
    messages: { 'zh-CN': zhCN },
  })
  wrapper = mount(ServerDirectoryPickerModal, {
    attachTo: document.body,
    global: { plugins: [i18n] },
  })
  return wrapper
}

beforeEach(() => {
  vi.clearAllMocks()
  previousTinadec = (window as unknown as { tinadec?: unknown }).tinadec
  mocks.browseFilesystem.mockResolvedValue(makeBrowseResponse())
  mocks.createHostDirectory.mockResolvedValue({
    created_path: 'C:\\Users\\demo\\NewWorkspace',
    entry: {
      name: 'NewWorkspace',
      path: 'C:\\Users\\demo\\NewWorkspace',
      kind: 'directory',
      is_dir: true,
      has_children: false,
      is_git_repo: false,
    },
  })
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  registerServerDirectoryPicker(null)
  Object.defineProperty(window, 'tinadec', { configurable: true, value: previousTinadec })
  document.body.innerHTML = ''
})

describe('ServerDirectoryPickerModal', () => {
  it('opens in a Reka Dialog portal, navigates rows with arrow keys, and confirms directory selection', async () => {
    Object.defineProperty(window, 'tinadec', { configurable: true, value: { isWebShim: true } })
    mountModal()

    const pickPromise = openServerDirectoryPicker({
      title: '选择工作区',
      mode: 'directory',
      multiple: true,
    })
    await flushPromises()

    const dialog = document.body.querySelector<HTMLElement>('[data-testid="server-directory-picker-modal"]')
    expect(dialog).not.toBeNull()
    expect(dialog?.getAttribute('role')).toBe('dialog')
    expect(mocks.browseFilesystem).toHaveBeenCalledWith(expect.objectContaining({
      includeFiles: false,
      showHidden: false,
      limit: 500,
    }))

    // Arrow key navigation focuses directory rows inside the Reka dialog.
    const list = document.body.querySelector<HTMLElement>('[data-testid="dir-picker-list"]')!
    const rows = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.dir-picker-row'))
    expect(rows).toHaveLength(2)
    rows[0]!.focus()
    list.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
    expect(document.activeElement).toBe(rows[1])

    // Multi-select both directories and confirm.
    rows[0]!.click()
    rows[1]!.click()
    await flushPromises()

    document.body.querySelector<HTMLButtonElement>('[data-testid="dir-picker-confirm"]')!.click()
    await expect(pickPromise).resolves.toEqual([
      'C:\\Users\\demo\\Projects',
      'C:\\Users\\demo\\Docs',
    ])
  })

  it('supports file mode with case-insensitive extension filtering, folder navigation, and double-click confirm', async () => {
    Object.defineProperty(window, 'tinadec', { configurable: true, value: { isWebShim: true } })
    mocks.browseFilesystem.mockResolvedValueOnce(makeBrowseResponse({
      entries: [
        { name: 'Wallpapers', path: 'C:\\Users\\demo\\Wallpapers', kind: 'directory', is_dir: true },
        { name: 'hero.PNG', path: 'C:\\Users\\demo\\hero.PNG', kind: 'file', is_dir: false, size_bytes: 2048 },
        { name: 'notes.txt', path: 'C:\\Users\\demo\\notes.txt', kind: 'file', is_dir: false, size_bytes: 120 },
      ],
    }))
    mocks.browseFilesystem.mockResolvedValueOnce(makeBrowseResponse({
      current_path: 'C:\\Users\\demo\\Wallpapers',
      parent_path: 'C:\\Users\\demo',
      entries: [
        { name: 'sunset.jpg', path: 'C:\\Users\\demo\\Wallpapers\\sunset.jpg', kind: 'file', is_dir: false, size_bytes: 4096 },
      ],
    }))

    mountModal()
    const pickPromise = openServerDirectoryPicker({
      title: '选择背景图片',
      mode: 'file',
      multiple: false,
      acceptExtensions: ['.png', '.jpg'],
    })
    await flushPromises()

    expect(mocks.browseFilesystem).toHaveBeenCalledWith(expect.objectContaining({
      includeFiles: true,
    }))

    // notes.txt is filtered out; folder appears first, followed by hero.PNG.
    const initialRows = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.dir-picker-row'))
    expect(initialRows.map((row) => row.dataset.entryPath)).toEqual([
      'C:\\Users\\demo\\Wallpapers',
      'C:\\Users\\demo\\hero.PNG',
    ])
    // Confirm button is disabled until a file is selected.
    const confirmBtn = document.body.querySelector<HTMLButtonElement>('[data-testid="dir-picker-confirm"]')!
    expect(confirmBtn.disabled).toBe(true)

    // Clicking a folder in file mode navigates into it rather than selecting it.
    initialRows[0]!.click()
    await flushPromises()
    expect(mocks.browseFilesystem).toHaveBeenLastCalledWith(expect.objectContaining({
      path: 'C:\\Users\\demo\\Wallpapers',
      includeFiles: true,
    }))

    const subRows = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.dir-picker-row'))
    expect(subRows).toHaveLength(1)
    expect(subRows[0]!.textContent).toContain('sunset.jpg')
    expect(subRows[0]!.textContent).toContain('4.0 KB')

    // Double-clicking a file in single-file mode resolves immediately.
    subRows[0]!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))
    await expect(pickPromise).resolves.toEqual(['C:\\Users\\demo\\Wallpapers\\sunset.jpg'])
  })

  it('creates a new folder inline via api.createHostDirectory, refreshes, and selects it', async () => {
    Object.defineProperty(window, 'tinadec', { configurable: true, value: { isWebShim: true } })
    mocks.browseFilesystem
      .mockResolvedValueOnce(makeBrowseResponse())
      .mockResolvedValueOnce(makeBrowseResponse({
        entries: [
          ...makeBrowseResponse().entries,
          { name: 'NewWorkspace', path: 'C:\\Users\\demo\\NewWorkspace', kind: 'directory', is_dir: true },
        ],
      }))

    mountModal()
    const pickPromise = openServerDirectoryPicker({ mode: 'directory', multiple: false })
    await flushPromises()

    document.body.querySelector<HTMLButtonElement>('[data-testid="dir-picker-new-folder-btn"]')!.click()
    await flushPromises()

    const input = document.body.querySelector<HTMLInputElement>('[data-testid="dir-picker-mkdir-input"]')!
    input.value = 'NewWorkspace'
    input.dispatchEvent(new Event('input', { bubbles: true }))
    await flushPromises()

    document.body.querySelector<HTMLFormElement>('[data-testid="dir-picker-mkdir-form"]')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    expect(mocks.createHostDirectory).toHaveBeenCalledWith({
      parent_path: 'C:\\Users\\demo',
      name: 'NewWorkspace',
    })
    expect(mocks.browseFilesystem).toHaveBeenCalledTimes(2)

    document.body.querySelector<HTMLButtonElement>('[data-testid="dir-picker-confirm"]')!.click()
    await expect(pickPromise).resolves.toEqual(['C:\\Users\\demo\\NewWorkspace'])
  })

  it('hides the bottom-left native OS file manager button completely in Web mode', async () => {
    Object.defineProperty(window, 'tinadec', { configurable: true, value: { isWebShim: true } })
    mountModal()

    const pickPromise = openServerDirectoryPicker({
      mode: 'directory',
      nativeFallback: vi.fn(async () => ['C:\\ShouldNotAppear']),
    })
    await flushPromises()

    expect(document.body.querySelector('[data-testid="dir-picker-native-fallback"]')).toBeNull()

    document.body.querySelector<HTMLButtonElement>('.dir-picker-close')!.click()
    await expect(pickPromise).resolves.toEqual([])
  })

  it('shows OS-aware native escape hatch in Electron, stays open on cancel, and resolves on pick', async () => {
    Object.defineProperty(window, 'tinadec', {
      configurable: true,
      value: { platform: 'darwin', gatewayUrl: () => 'http://127.0.0.1:48730' },
    })
    mocks.browseFilesystem.mockResolvedValue(makeBrowseResponse({
      platform: 'macos',
      current_path: '/Users/demo',
      parent_path: '/Users',
    }))
    const nativeFallback = vi.fn()
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce(['/Users/demo/NativeRepo'])

    mountModal()
    const pickPromise = openServerDirectoryPicker({
      mode: 'directory',
      nativeFallback,
    })
    await flushPromises()

    const nativeBtn = document.body.querySelector<HTMLButtonElement>('[data-testid="dir-picker-native-fallback"]')
    expect(nativeBtn).not.toBeNull()
    expect(nativeBtn?.textContent).toContain(zhCN.webDirPicker.useNativeFinder)

    // Cancelling the native OS dialog keeps the built-in Reka modal open.
    nativeBtn!.click()
    await flushPromises()
    expect(nativeFallback).toHaveBeenCalledTimes(1)
    expect(document.body.querySelector('[data-testid="server-directory-picker-modal"]')).not.toBeNull()

    // Selecting a path in the native OS dialog closes the built-in modal and resolves immediately.
    nativeBtn!.click()
    await expect(pickPromise).resolves.toEqual(['/Users/demo/NativeRepo'])
    await flushPromises()
    expect(document.body.querySelector('[data-testid="server-directory-picker-modal"]')).toBeNull()
  })

  it('shows Windows Explorer label and exposes quick native escape button on browse error in Electron', async () => {
    Object.defineProperty(window, 'tinadec', {
      configurable: true,
      value: { platform: 'win32', gatewayUrl: () => 'http://127.0.0.1:48730' },
    })
    mocks.browseFilesystem.mockRejectedValueOnce(new Error('Permission denied'))
    const nativeFallback = vi.fn(async () => ['D:\\Workspace'])

    mountModal()
    const pickPromise = openServerDirectoryPicker({
      mode: 'directory',
      nativeFallback,
    })
    await flushPromises()

    const errorBox = document.body.querySelector<HTMLElement>('[data-testid="dir-picker-error"]')
    expect(errorBox?.textContent).toContain('Permission denied')
    const errorNativeBtn = document.body.querySelector<HTMLButtonElement>('[data-testid="dir-picker-error-native-btn"]')
    expect(errorNativeBtn?.textContent).toContain(zhCN.webDirPicker.useNativeExplorer)

    errorNativeBtn!.click()
    await expect(pickPromise).resolves.toEqual(['D:\\Workspace'])
  })
})
