// @vitest-environment happy-dom
import { mount, flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import FileTreePanel from './FileTreePanel.vue'

/**
 * Pins the file tree's own right-click handler so the global selection menu can
 * never steal it. The tree row calls `@contextmenu.prevent`, which is exactly
 * the signal SelectionContextMenu checks before opening.
 */

const listDirectory = vi.fn().mockResolvedValue({
  data: {
    entries: [
      { name: 'alpha.txt', path: 'alpha.txt', is_directory: false, size: 12 },
      { name: 'src', path: 'src', is_directory: true, size: null },
    ],
  },
})

vi.mock('@/api', () => ({
  api: {
    listDirectory: (...args: unknown[]) => listDirectory(...args),
    globSearch: vi.fn().mockResolvedValue({ data: { matches: [] } }),
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

describe('FileTreePanel right-click ownership', () => {
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
  })
})
