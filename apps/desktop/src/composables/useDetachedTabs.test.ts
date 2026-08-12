import { Window } from 'happy-dom'
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'

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

  describe('detached type mapping', () => {
    it('maps the UIE browser descriptor to the legacy preview panel type', () => {
      expect(module.detachedTypeForDescriptor('browser')).toBe('preview')
    })

    it('passes through descriptors that share their panel type name', () => {
      expect(module.detachedTypeForDescriptor('git')).toBe('git')
      expect(module.detachedTypeForDescriptor('approval')).toBe('approval')
      expect(module.detachedTypeForDescriptor('terminal')).toBe('terminal')
    })

    it('maps the preview panel type back to the browser descriptor', () => {
      expect(module.descriptorForDetachedType('preview')).toBe('browser')
      expect(module.descriptorForDetachedType('git')).toBe('git')
    })
  })

  describe('detach / tracking', () => {
    it('does nothing when the Electron detach API is unavailable', async () => {
      const { detach } = module.useDetachedTabs()
      const ok = await detach({ id: 'wb-1', descriptorId: 'git', title: 'Git' })
      expect(ok).toBe(false)
      expect(module.useDetachedTabs().detachedTabs.value).toEqual([])
    })

    it('records a detached indicator after spawning a floating window', async () => {
      const detachPanel = vi.fn().mockResolvedValue({ windowId: 7, tabId: 'wb-9' })
      ;(testWindow as unknown as { tinadec: { detachPanel: unknown } }).tinadec = { detachPanel }

      const { detach, detachedTabs } = module.useDetachedTabs()
      const ok = await detach({ id: 'wb-9', descriptorId: 'browser', title: '浏览器' }, { url: 'http://x' })

      expect(ok).toBe(true)
      expect(detachPanel).toHaveBeenCalledWith('wb-9', 'preview', '浏览器', { url: 'http://x' })
      expect(detachedTabs.value).toEqual([{ tabId: 'wb-9', type: 'preview', title: '浏览器', windowId: 7 }])
    })

    it('does not record the indicator when the window creation fails', async () => {
      ;(testWindow as unknown as { tinadec: { detachPanel: unknown } }).tinadec = {
        detachPanel: vi.fn().mockResolvedValue(null),
      }

      const { detach, detachedTabs } = module.useDetachedTabs()
      const ok = await detach({ id: 'wb-9', descriptorId: 'git', title: 'Git' })
      expect(ok).toBe(false)
      expect(detachedTabs.value).toEqual([])
    })

    it('removeDetachedTab drops a single indicator', async () => {
      ;(testWindow as unknown as { tinadec: { detachPanel: unknown } }).tinadec = {
        detachPanel: vi.fn().mockResolvedValue({ windowId: 1, tabId: 'wb-1' }),
      }
      const { detach, removeDetachedTab, detachedTabs } = module.useDetachedTabs()
      await detach({ id: 'wb-1', descriptorId: 'git', title: 'Git' })

      removeDetachedTab('wb-1')
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
      detachedTabs.value = [{ tabId: 'wb-5', type: 'git', title: 'Git', windowId: 2 }]

      const unsubscribe = bind()
      closedHandlers[0]({ tabId: 'wb-5' })
      expect(detachedTabs.value).toEqual([])

      unsubscribe()
      expect(offClosed).toHaveBeenCalled()
    })

    it('forwards reattach events to the layout owner after dropping the indicator', () => {
      const reattachHandlers: Array<(data: { tabId: string; type: string }) => void> = []
      ;(testWindow as unknown as { tinadec: { onPanelReattach: unknown } }).tinadec = {
        onPanelReattach: (cb: (data: { tabId: string; type: string }) => void) => {
          reattachHandlers.push(cb)
          return () => {}
        },
      }

      const onReattach = vi.fn()
      const { bind, detachedTabs } = module.useDetachedTabs()
      detachedTabs.value = [{ tabId: 'wb-5', type: 'preview', title: '浏览器', windowId: 2 }]

      const unsubscribe = bind(onReattach)
      reattachHandlers[0]({ tabId: 'wb-5', type: 'preview' })
      expect(detachedTabs.value).toEqual([])
      expect(onReattach).toHaveBeenCalledWith({ tabId: 'wb-5', type: 'preview' })

      unsubscribe()
    })
  })
})
