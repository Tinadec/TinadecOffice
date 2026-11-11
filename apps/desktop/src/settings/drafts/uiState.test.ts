import { describe, expect, it } from 'vitest'
import {
  CENTER_UI_STORAGE_KEY,
  DEFAULT_CENTER_UI,
  patchAgentCenterUi,
  patchModelCenterUi,
  readCenterUiState,
  writeCenterUiState,
} from './uiState'
import type { KeyValueStorage } from './storage'

function memoryStorage(): KeyValueStorage & { dump(): Map<string, string> } {
  const map = new Map<string, string>()
  return {
    getItem: key => map.get(key) ?? null,
    setItem: (key, value) => { map.set(key, value) },
    removeItem: key => { map.delete(key) },
    dump: () => map,
  }
}

describe('center UI state', () => {
  it('falls back to defaults for an empty or corrupt store', () => {
    const storage = memoryStorage()
    expect(readCenterUiState(storage)).toEqual(DEFAULT_CENTER_UI)
    storage.setItem(CENTER_UI_STORAGE_KEY, '{broken')
    expect(readCenterUiState(storage)).toEqual(DEFAULT_CENTER_UI)
  })

  it('persists an agent-center patch without touching the model center', () => {
    const storage = memoryStorage()
    patchModelCenterUi({ tab: 'models', selectedConnectionId: 'c1' }, storage)
    patchAgentCenterUi({ tab: 'teams', selectedTeamId: 't1', viewMode: 'topology' }, storage)
    const state = readCenterUiState(storage)
    expect(state.agentCenter).toMatchObject({ tab: 'teams', selectedTeamId: 't1', viewMode: 'topology' })
    expect(state.modelCenter).toMatchObject({ tab: 'models', selectedConnectionId: 'c1' })
  })

  it('keeps legacy tab names readable during the navigation migration', () => {
    const storage = memoryStorage()
    writeCenterUiState({
      agentCenter: { ...DEFAULT_CENTER_UI.agentCenter, tab: 'modes' },
      modelCenter: { ...DEFAULT_CENTER_UI.modelCenter, tab: 'suppliers' },
    }, storage)
    const state = readCenterUiState(storage)
    expect(state.agentCenter.tab).toBe('modes')
    expect(state.modelCenter.tab).toBe('suppliers')
  })

  it('rejects an unknown tab instead of rendering an empty stage', () => {
    const storage = memoryStorage()
    writeCenterUiState({
      agentCenter: { ...DEFAULT_CENTER_UI.agentCenter, tab: 'nope' as never },
      modelCenter: { ...DEFAULT_CENTER_UI.modelCenter, tab: 'nope' as never },
    }, storage)
    const state = readCenterUiState(storage)
    expect(state.agentCenter.tab).toBe(DEFAULT_CENTER_UI.agentCenter.tab)
    expect(state.modelCenter.tab).toBe(DEFAULT_CENTER_UI.modelCenter.tab)
  })

  it('drops non-string filters and collapsed entries', () => {
    const storage = memoryStorage()
    storage.setItem(CENTER_UI_STORAGE_KEY, JSON.stringify({
      agentCenter: { tab: 'agents', viewMode: 'list', filters: { layer: 'operation', bad: 3 }, collapsed: ['a', 2] },
      modelCenter: { tab: 'models', filters: { status: 'ready' } },
    }))
    const state = readCenterUiState(storage)
    expect(state.agentCenter.filters).toEqual({ layer: 'operation' })
    expect(state.agentCenter.collapsed).toEqual(['a'])
    expect(state.modelCenter.filters).toEqual({ status: 'ready' })
  })
})
