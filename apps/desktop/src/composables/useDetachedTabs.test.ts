import { Window } from 'happy-dom'
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'

// The floating window's session context comes from the app-wide HomeController; a stub keeps
// the import light (the real controller pulls the whole API graph) and pins the values.
vi.mock('@/controllers/HomeController', () => ({
  homeController: {
    selectedSessionId: { value: 'session-1' },
    currentProject: { value: { path: 'C:/work/project' } },
  },
}))

let testWindow: Window
let module: typeof import('./useDetachedTabs')

describe('useDetachedTabs', () => {
  beforeAll(async () => {
    testWindow = new Window({ url: 'http://127.0.0.1:5173' })
    vi.stubGlobal('window', testWindow)
    vi.stubGlobal('document', testWindow.document)
    module = await import('./useDetachedTabs')
  })

  beforeEach(() => {
    module.__resetDetachedTabsForTests()
  })

  afterAll(() => {
    testWindow.close()
    vi.unstubAllGlobals()
  })

  describe('detach / tracking', () => {
    it('does nothing when the Electron detach API is unavailable', async () => {
      const { detach } = module.useDetachedTabs()
      const ok = await detach({ id: 'uie-1', descriptorId: 'git', title: 'Git' })
      expect(ok).toBe(false)
      expect(module.useDetachedTabs().detachedTabs.value).toEqual([])
    })

    it('records a detached indicator after spawning a floating window', async () => {
      const detachPanel = vi.fn().mockResolvedValue({ windowId: 7, tabId: 'uie-9' })
      ;(testWindow as unknown as { tinadec: { detachPanel: unknown } }).tinadec = { detachPanel }

      const { detach, detachedTabs } = module.useDetachedTabs()
      const ok = await detach({ id: 'uie-9', descriptorId: 'browser', title: '浏览器' }, { url: 'http://x' })

      expect(ok).toBe(true)
      expect(detachPanel).toHaveBeenCalledWith('uie-9', 'browser', '浏览器', expect.objectContaining({ url: 'http://x' }))
      // The floating window is its own renderer: the session context rides along.
      expect(detachPanel.mock.calls[0][3]).toEqual({ url: 'http://x', sessionId: 'session-1', projectPath: 'C:/work/project' })
      expect(detachedTabs.value).toEqual([{ tabId: 'uie-9', type: 'browser', title: '浏览器', windowId: 7 }])
    })

    it('does not record the indicator when the window creation fails', async () => {
      ;(testWindow as unknown as { tinadec: { detachPanel: unknown } }).tinadec = {
        detachPanel: vi.fn().mockResolvedValue(null),
      }

      const { detach, detachedTabs } = module.useDetachedTabs()
      const ok = await detach({ id: 'uie-9', descriptorId: 'git', title: 'Git' })
      expect(ok).toBe(false)
      expect(detachedTabs.value).toEqual([])
    })

    it('removeDetachedTab drops a single indicator', async () => {
      ;(testWindow as unknown as { tinadec: { detachPanel: unknown } }).tinadec = {
        detachPanel: vi.fn().mockResolvedValue({ windowId: 1, tabId: 'uie-1' }),
      }
      const { detach, removeDetachedTab, detachedTabs } = module.useDetachedTabs()
      await detach({ id: 'uie-1', descriptorId: 'git', title: 'Git' })

      removeDetachedTab('uie-1')
      expect(detachedTabs.value).toEqual([])
    })
  })

  describe('IPC listener binding', () => {
    it('drops the indicator when a floating window closes without reattach', () => {
      const closedHandlers: Array<(data: { tabId: string }) => void> = []
      const offClosed = vi.fn()
      ;(testWindow as unknown as { tinadec: { onPanelClosed: unknown } }).tinadec = {
        onPanelClosed: (cb: (data: { tabId: string }) => void) => {
          closedHandlers.push(cb)
          return offClosed
        },
      }

      const { bind, detachedTabs } = module.useDetachedTabs()
      detachedTabs.value = [{ tabId: 'uie-5', type: 'git', title: 'Git', windowId: 2 }]

      const unsubscribe = bind()
      closedHandlers[0]({ tabId: 'uie-5' })
      expect(detachedTabs.value).toEqual([])

      unsubscribe()
      expect(offClosed).toHaveBeenCalled()
    })

    it('forwards reattach events to the layout owner after dropping the indicator', () => {
      type Reattach = { tabId: string; type: string; state: Record<string, unknown> }
      const reattachHandlers: Array<(data: Reattach) => void> = []
      ;(testWindow as unknown as { tinadec: { onPanelReattach: unknown } }).tinadec = {
        onPanelReattach: (cb: (data: Reattach) => void) => {
          reattachHandlers.push(cb)
          return () => {}
        },
      }

      const onReattach = vi.fn()
      const { bind, detachedTabs } = module.useDetachedTabs()
      detachedTabs.value = [{ tabId: 'uie-5', type: 'browser', title: '浏览器', windowId: 2 }]

      const unsubscribe = bind(onReattach)
      reattachHandlers[0]({ tabId: 'uie-5', type: 'browser', state: { url: 'http://x', sessionId: 's1', projectPath: 'C:/p' } })
      expect(detachedTabs.value).toEqual([])
      // The window-only session context never flows back into the card state.
      expect(onReattach).toHaveBeenCalledWith({ tabId: 'uie-5', type: 'browser', state: { url: 'http://x' } })

      unsubscribe()
    })
  })
})
