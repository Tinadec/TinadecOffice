import { defaultStorage, safeReadJson, trySet, type KeyValueStorage } from './storage'

/**
 * Persisted *view* state for the two settings centers. This never carries a
 * business truth: tabs, filters and the selected row only decide what the user
 * sees next, while agents/modes/models remain Core-owned.
 *
 * `teams` / `graph` / `advanced` and `connections` are the target IA keys.
 * `modes` / `evolution` / `runtime` / `suppliers` / `routes` stay readable so a
 * build that has not switched its navigation yet does not lose its place.
 */
export type AgentCenterTabKey =
  | 'teams' | 'agents' | 'graph' | 'prompts' | 'advanced'
  | 'modes' | 'evolution' | 'runtime'

export type ModelCenterTabKey =
  | 'connections' | 'models' | 'runtime' | 'advanced'
  | 'suppliers' | 'routes'

export type AgentViewMode = 'topology' | 'list'

export interface AgentCenterUiState {
  tab: AgentCenterTabKey
  viewMode: AgentViewMode
  selectedTeamId: string
  selectedAgentId: string
  filters: Record<string, string>
  collapsed: string[]
}

export interface ModelCenterUiState {
  tab: ModelCenterTabKey
  selectedConnectionId: string
  filters: Record<string, string>
}

export interface CenterUiState {
  agentCenter: AgentCenterUiState
  modelCenter: ModelCenterUiState
}

export const CENTER_UI_STORAGE_KEY = 'tinadec.settings.center.ui.v1'

const AGENT_TABS: ReadonlySet<string> = new Set([
  'teams', 'agents', 'graph', 'prompts', 'advanced',
  'modes', 'evolution', 'runtime',
])
const MODEL_TABS: ReadonlySet<string> = new Set([
  'connections', 'models', 'runtime', 'advanced', 'suppliers', 'routes',
])

export const DEFAULT_CENTER_UI: CenterUiState = {
  agentCenter: {
    tab: 'teams',
    viewMode: 'list',
    selectedTeamId: '',
    selectedAgentId: '',
    filters: {},
    collapsed: [],
  },
  modelCenter: {
    tab: 'connections',
    selectedConnectionId: '',
    filters: {},
  },
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {}
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

function asStringMap(value: unknown): Record<string, string> {
  const source = asRecord(value)
  const result: Record<string, string> = {}
  for (const [key, item] of Object.entries(source)) {
    if (typeof item === 'string') result[key] = item
  }
  return result
}

function asStringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : []
}

export function normalizeCenterUiState(value: unknown): CenterUiState {
  const root = asRecord(value)
  const agent = asRecord(root.agentCenter)
  const model = asRecord(root.modelCenter)
  const agentTab = asString(agent.tab)
  const modelTab = asString(model.tab)
  return {
    agentCenter: {
      tab: AGENT_TABS.has(agentTab) ? agentTab as AgentCenterTabKey : DEFAULT_CENTER_UI.agentCenter.tab,
      viewMode: agent.viewMode === 'topology' ? 'topology' : 'list',
      selectedTeamId: asString(agent.selectedTeamId),
      selectedAgentId: asString(agent.selectedAgentId),
      filters: asStringMap(agent.filters),
      collapsed: asStringArray(agent.collapsed),
    },
    modelCenter: {
      tab: MODEL_TABS.has(modelTab) ? modelTab as ModelCenterTabKey : DEFAULT_CENTER_UI.modelCenter.tab,
      selectedConnectionId: asString(model.selectedConnectionId),
      filters: asStringMap(model.filters),
    },
  }
}

export function readCenterUiState(storage: KeyValueStorage = defaultStorage()): CenterUiState {
  return normalizeCenterUiState(safeReadJson(storage, CENTER_UI_STORAGE_KEY))
}

export function writeCenterUiState(state: CenterUiState, storage: KeyValueStorage = defaultStorage()): boolean {
  return trySet(storage, CENTER_UI_STORAGE_KEY, JSON.stringify(state))
}

export function patchAgentCenterUi(
  patch: Partial<AgentCenterUiState>,
  storage: KeyValueStorage = defaultStorage(),
): CenterUiState {
  const current = readCenterUiState(storage)
  const next: CenterUiState = {
    ...current,
    agentCenter: { ...current.agentCenter, ...patch },
  }
  writeCenterUiState(next, storage)
  return next
}

export function patchModelCenterUi(
  patch: Partial<ModelCenterUiState>,
  storage: KeyValueStorage = defaultStorage(),
): CenterUiState {
  const current = readCenterUiState(storage)
  const next: CenterUiState = {
    ...current,
    modelCenter: { ...current.modelCenter, ...patch },
  }
  writeCenterUiState(next, storage)
  return next
}
