import { effectScope, type EffectScope } from 'vue'
import { afterEach, describe, expect, it } from 'vitest'
import { createWorkbenchLayoutController } from './useWorkbenchLayout'
import type { WorkbenchLayoutDocument, WorkbenchLayoutStorage } from './types'

describe('createWorkbenchLayoutController', () => {
  let scope: EffectScope | null = null

  afterEach(() => {
    scope?.stop()
    scope = null
  })

  it('coalesces continuous resize commands into one undo entry and redoes the final geometry', async () => {
    let saved: WorkbenchLayoutDocument | null = null
    const storage: WorkbenchLayoutStorage = {
      load: async () => null,
      save: async (document) => {
        saved = structuredClone(document)
        return document
      },
    }
    scope = effectScope()
    const controller = scope.run(() => createWorkbenchLayoutController(new Map(), storage))!
    await controller.initialize()

    expect(controller.dispatch({ kind: 'resizeColumn', slotId: 'left', width: 280 }, 'user', 'column:left')).toBe(true)
    controller.setActivePage('home')
    expect(controller.dispatch({ kind: 'resizeColumn', slotId: 'left', width: 320 }, 'user', 'column:left')).toBe(true)
    controller.finishCoalescing()
    expect(controller.currentLayout.value.columns.left.width).toBe(320)
    expect(controller.undo()).toBe(true)
    expect(controller.currentLayout.value.columns.left.width).toBe(260)
    expect(controller.redo()).toBe(true)
    expect(controller.currentLayout.value.columns.left.width).toBe(320)

    await controller.flush()
    expect(saved).not.toBeNull()
    expect(saved!.pages.home?.columns.left.width).toBe(320)
  })

  it('keeps page histories separate and rejects the reserved AI command source', async () => {
    const storage: WorkbenchLayoutStorage = {
      load: async () => null,
      save: async (document) => document,
    }
    scope = effectScope()
    const controller = scope.run(() => createWorkbenchLayoutController(new Map(), storage))!
    await controller.initialize()

    expect(controller.dispatch({ kind: 'collapseColumn', slotId: 'right', collapsed: true }, 'ai')).toBe(false)
    expect(controller.currentLayout.value.columns.right.collapsed).toBe(false)
    expect(controller.dispatch({ kind: 'collapseColumn', slotId: 'right', collapsed: true })).toBe(true)
    expect(controller.canUndo.value).toBe(true)

    controller.setActivePage('code')
    expect(controller.canUndo.value).toBe(false)
    expect(controller.currentLayout.value.pageId).toBe('code')
    controller.setActivePage('home')
    expect(controller.canUndo.value).toBe(true)
  })
})
