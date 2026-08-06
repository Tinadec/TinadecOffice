import type { PersistedCardInstance } from '../types'

// ---------------------------------------------------------------------------
// Migration from the legacy panel model to the Workbench card registry.
//
// Old detached-panel records were `{ windowId, tabId, type, title, state, bounds }`
// where `type` was a PanelType ('git' | 'approval' | ... | 'home' | 'preview').
// The Workbench model keys cards by descriptorId. This maps the legacy PanelType
// to the new descriptorId and produces PersistedCardInstance entries.
// ---------------------------------------------------------------------------

/** Legacy PanelType -> Workbench descriptorId. */
export const PANEL_TYPE_TO_DESCRIPTOR: Readonly<Record<string, string>> = {
  home: 'homePicker',
  git: 'git',
  approval: 'approval',
  orchestration: 'orchestration',
  events: 'events',
  doctor: 'doctor',
  preview: 'browser',
  agent: 'agent',
  terminal: 'terminal',
}

export interface LegacyPanelRecord {
  windowId?: number
  tabId?: string
  type: string
  title?: string
  state?: Record<string, unknown>
  bounds?: unknown
}

export interface LegacyPanelLayoutFile {
  panels?: LegacyPanelRecord[]
  savedAt?: number
}

/**
 * Migrate a legacy panel-layout payload into Workbench card instances.
 * Returns null if the payload has nothing migratable.
 * Pure function — safe to unit test.
 */
export function migrateLegacyPayload(
  raw: unknown,
  instanceIdFor: (tabId: string | undefined, index: number) => string,
): PersistedCardInstance[] | null {
  if (!raw || typeof raw !== 'object') return null
  const file = raw as LegacyPanelLayoutFile
  if (!Array.isArray(file.panels) || file.panels.length === 0) return null

  const instances: PersistedCardInstance[] = []
  for (let i = 0; i < file.panels.length; i++) {
    const record = file.panels[i]
    if (!record || typeof record !== 'object' || typeof record.type !== 'string') continue
    const descriptorId = PANEL_TYPE_TO_DESCRIPTOR[record.type]
    if (!descriptorId) continue
    instances.push({
      id: instanceIdFor(record.tabId, i),
      descriptorId,
      title: record.title ?? descriptorId,
      ...(record.state && typeof record.state === 'object' ? { state: record.state as Record<string, unknown> } : {}),
    })
  }

  return instances.length > 0 ? instances : null
}
