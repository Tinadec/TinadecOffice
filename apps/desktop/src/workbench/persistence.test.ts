import { describe, expect, it } from 'vitest'
import { createWorkbenchPreset } from './presets'
import {
  clearLayoutScope,
  createEmptyLayoutDocument,
  repairLayoutDocument,
  resolvePersistedLayout,
  storeLayoutAtScope,
} from './persistence'

describe('workbench persistence', () => {
  it('resolves workspace page, page, global geometry, then built-in defaults', () => {
    let document = createEmptyLayoutDocument()
    const global = createWorkbenchPreset('home')
    global.columns.left.width = 288
    document = storeLayoutAtScope(document, { kind: 'global' }, global)

    expect(resolvePersistedLayout(document, 'code', null).columns.left.width).toBe(288)

    const page = createWorkbenchPreset('code')
    page.columns.left.width = 320
    document = storeLayoutAtScope(document, { kind: 'page', pageId: 'code' }, page)
    expect(resolvePersistedLayout(document, 'code', 'project-a').columns.left.width).toBe(320)

    const workspace = createWorkbenchPreset('code')
    workspace.columns.left.width = 360
    document = storeLayoutAtScope(
      document,
      { kind: 'workspace-page', projectId: 'project-a', pageId: 'code' },
      workspace,
    )
    expect(resolvePersistedLayout(document, 'code', 'project-a').columns.left.width).toBe(360)
    expect(resolvePersistedLayout(document, 'code', 'project-b').columns.left.width).toBe(320)

    document = clearLayoutScope(
      document,
      { kind: 'workspace-page', projectId: 'project-a', pageId: 'code' },
    )
    expect(resolvePersistedLayout(document, 'code', 'project-a').columns.left.width).toBe(320)
  })

  it('repairs malformed documents instead of propagating invalid state', () => {
    expect(repairLayoutDocument({ version: 8 })).toEqual(createEmptyLayoutDocument())
    const repaired = repairLayoutDocument({
      version: 1,
      revision: -4,
      pages: { home: { version: 99 } },
      workspacePages: { '': { home: createWorkbenchPreset('home') } },
    })
    expect(repaired.revision).toBe(0)
    expect(repaired.pages.home).toEqual(createWorkbenchPreset('home'))
    expect(repaired.workspacePages).toEqual({})
  })

  it('round-trips the adjustable Settings navigation width without accepting structural changes', () => {
    let document = createEmptyLayoutDocument()
    const settings = createWorkbenchPreset('settings')
    settings.columns.left.width = 344
    settings.columnOrder = ['right', 'center', 'left']
    document = storeLayoutAtScope(document, { kind: 'page', pageId: 'settings' }, settings)

    const resolved = resolvePersistedLayout(document, 'settings', null)
    expect(resolved.columns.left.width).toBe(344)
    expect(resolved.columnOrder).toEqual(['left', 'center', 'right'])
    expect(resolved.columns.right.collapsed).toBe(true)
  })
})
