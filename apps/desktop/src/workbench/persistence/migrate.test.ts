import { describe, it, expect } from 'vitest'
import { migrateLegacyPayload, PANEL_TYPE_TO_DESCRIPTOR } from './migrate'

describe('legacy panel migration', () => {
  it('maps legacy PanelTypes to Workbench descriptorIds', () => {
    expect(PANEL_TYPE_TO_DESCRIPTOR.preview).toBe('browser')
    expect(PANEL_TYPE_TO_DESCRIPTOR.home).toBe('homePicker')
    expect(PANEL_TYPE_TO_DESCRIPTOR.git).toBe('git')
    expect(PANEL_TYPE_TO_DESCRIPTOR.terminal).toBe('terminal')
  })

  it('migrates a legacy payload to card instances', () => {
    const raw = {
      panels: [
        { windowId: 1, tabId: 't1', type: 'git', title: 'Git' },
        { windowId: 2, tabId: 't2', type: 'preview', title: 'Browser', state: { url: 'https://x' } },
      ],
      savedAt: 123,
    }
    const result = migrateLegacyPayload(raw, (tabId, i) => tabId ?? `mig-${i}`)
    expect(result).not.toBeNull()
    expect(result!.length).toBe(2)
    expect(result![0].descriptorId).toBe('git')
    expect(result![1].descriptorId).toBe('browser')
    expect(result![1].state?.url).toBe('https://x')
  })

  it('returns null for empty payload', () => {
    expect(migrateLegacyPayload({ panels: [] }, () => 'x')).toBeNull()
    expect(migrateLegacyPayload(null, () => 'x')).toBeNull()
  })

  it('drops unknown panel types', () => {
    const raw = {
      panels: [
        { tabId: 'a', type: 'git' },
        { tabId: 'b', type: 'totally-unknown' },
      ],
    }
    const result = migrateLegacyPayload(raw, (tabId) => tabId ?? 'x')
    expect(result!.length).toBe(1)
    expect(result![0].descriptorId).toBe('git')
  })
})
