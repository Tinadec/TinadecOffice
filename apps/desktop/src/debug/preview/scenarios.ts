/**
 * 场景预设
 * 定义预览画廊中可切换的各种工况场景
 */
import type { MockDataBundle } from './mockData'
import {
  buildMockDataBundle,
  mockReadinessBlocked,
  mockPendingApprovals,
  mockManyMessages,
  mockFailedToolExecutions,
  mockOrchestrationSnapshot,
} from './mockData'

export type ScenarioId =
  | 'empty'
  | 'loading'
  | 'populated'
  | 'busy'
  | 'approval-pending'
  | 'error'
  | 'agent-working'
  | 'many-messages'
  | 'git-changes'
  | 'tool-failures'

/** A scenario's name and description are in the locales: debugStudio.preview.scenario.<id in camelCase>. */
export interface Scenario {
  id: ScenarioId
  overrides?: Partial<MockDataBundle>
}

export const SCENARIOS: Scenario[] = [
  {
    id: 'populated',
  },
  {
    id: 'empty',
    overrides: {
      projects: [],
      sessions: [],
      messages: [],
      approvals: [],
      orchestration: null,
      toolExecutions: [],
      events: [],
      readiness: null,
      modelSettings: null,
      modelProviders: [],
      modelRoutes: [],
      agents: [],
      agentModes: [],
      tools: [],
      harnessManifest: null,
      promptFragments: [],
      extensionSources: { sources: [], supported_kinds: ['mcp_registry'] },
      marketCatalog: { items: [], total_available: 0, has_more: false },
      installations: [],
      mcpInventory: { source: 'tool_provider', servers: [] },
      acpAdapters: [],
      gitDiffPreview: null,
      gitPushPlan: null,
    },
  },
  {
    id: 'loading',
    overrides: {
      projects: [],
      sessions: [],
      messages: [],
      approvals: [],
      orchestration: null,
      toolExecutions: [],
      events: [],
      readiness: null,
      modelSettings: null,
      modelProviders: [],
      modelRoutes: [],
      agents: [],
      agentModes: [],
      tools: [],
      harnessManifest: null,
      promptFragments: [],
      extensionSources: { sources: [], supported_kinds: ['mcp_registry'] },
      marketCatalog: { items: [], total_available: 0, has_more: false },
      installations: [],
      mcpInventory: { source: 'tool_provider', servers: [] },
      acpAdapters: [],
      gitDiffPreview: null,
      gitPushPlan: null,
    },
  },
  {
    id: 'busy',
    overrides: {
      orchestration: (() => {
        const snap = mockOrchestrationSnapshot('sess-tinadec-1001')
        if (snap.run) {
          snap.run.status = 'running'
          snap.run.summary = '正在执行：实现 DynamicDependencyResolver'
        }
        return snap
      })(),
    },
  },
  {
    id: 'approval-pending',
    overrides: {
      approvals: mockPendingApprovals('sess-tinadec-1001'),
    },
  },
  {
    id: 'error',
    overrides: {
      readiness: mockReadinessBlocked(),
    },
  },
  {
    id: 'agent-working',
    overrides: {
      orchestration: (() => {
        const snap = mockOrchestrationSnapshot('sess-tinadec-1001')
        if (snap.run) {
          snap.run.status = 'running'
          snap.run.summary = 'Agent 活动中：Code Writer 正在编写 DynamicDependencyResolver'
        }
        snap.step_results = snap.step_results.map((sr) =>
          sr.status === 'completed' ? { ...sr, status: 'stubbed' } : sr,
        )
        return snap
      })(),
    },
  },
  {
    id: 'many-messages',
    overrides: {
      messages: mockManyMessages('sess-tinadec-1001'),
    },
  },
  {
    id: 'git-changes',
    overrides: {},
  },
  {
    id: 'tool-failures',
    overrides: {
      toolExecutions: mockFailedToolExecutions('sess-tinadec-1001'),
    },
  },
]

export function getScenario(id: ScenarioId): Scenario {
  return SCENARIOS.find((s) => s.id === id) ?? SCENARIOS[0]
}

export function buildScenarioData(scenarioId: ScenarioId, sessionId: string = 'sess-tinadec-1001'): MockDataBundle {
  const base = buildMockDataBundle(sessionId)
  const scenario = getScenario(scenarioId)
  if (!scenario.overrides) return base
  return { ...base, ...scenario.overrides }
}

export function isLoadingScenario(scenarioId: ScenarioId): boolean {
  return scenarioId === 'loading'
}
