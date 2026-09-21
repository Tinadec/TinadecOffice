// @vitest-environment happy-dom
import { mount, flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import FileTreePanel from './FileTreePanel.vue'

/**
 * Pins what the tree's own rows are: right-click belongs to the row (so the global
 * selection menu can never steal it), and a row the tool typed as a directory is
 * expandable. The file name predates the second case; both are row ownership.
 */

const listDirectory = vi.fn().mockResolvedValue({
  data: {
    entries: [
      { name: 'alpha.txt', path: './alpha.txt', type: 'file', size: 12 },
      { name: 'src', path: './src', type: 'directory', size: 0 },
    ],
  },
})

vi.mock('@/api', () => ({
  api: {
    listDirectory: (...args: unknown[]) => listDirectory(...args),
    grepContent: vi.fn().mockResolvedValue({ data: { lines: [], file_hashes: {} } }),
    getUserToolAction: vi.fn().mockResolvedValue({}),
    resumeUserToolAction: vi.fn().mockResolvedValue({}),
    snapshotOverrideUserToolAction: vi.fn().mockResolvedValue({}),
  },
  createUserToolActionForPath: vi.fn(),
}))

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string) => key }),
}))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: { error: vi.fn(), success: vi.fn(), warning: vi.fn() },
    status: vi.fn(),
    dismissByKey: vi.fn(),
  }),
}))

describe('FileTreePanel row ownership', () => {
  it('consumes contextmenu on a tree row so the global selection menu stays out', async () => {
    const wrapper = mount(FileTreePanel, {
      props: { cwd: '/workspace' },
      attachTo: document.body,
    })
    await flushPromises()

    const item = document.body.querySelector<HTMLElement>('.code-tree-item')
    expect(item).not.toBeNull()

    const event = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 10, clientY: 10 })
    item!.dispatchEvent(event)
    await flushPromises()

    // The row's @contextmenu.prevent is the ownership signal.
    expect(event.defaultPrevented).toBe(true)
    wrapper.unmount()
    document.body.innerHTML = ''
  })

  /**
   * `ls` says "directory" with type, not is_dir. A row that is not recognised as a
   * directory cannot be expanded at all, so the whole tree below the root is dead
   * weight — this is the read that used to get it wrong.
   */
  it('expands a row the tool typed as a directory', async () => {
    listDirectory.mockClear()
    const wrapper = mount(FileTreePanel, {
      props: { cwd: '/workspace' },
      attachTo: document.body,
    })
    await flushPromises()

    const rows = Array.from(document.body.querySelectorAll<HTMLElement>('.code-tree-item'))
    const dir = rows.find((row) => row.textContent?.includes('src'))
    expect(dir).toBeDefined()
    dir!.click()
    await flushPromises()

    expect(listDirectory).toHaveBeenLastCalledWith('/workspace', 'src')
    wrapper.unmount()
    document.body.innerHTML = ''
  })
})
