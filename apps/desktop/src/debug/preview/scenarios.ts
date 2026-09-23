/**
 * 场景预设
 * 定义预览画廊中可切换的各种工况场景
 */
import type { MockDataBundle } from './mockData'
import {
  buildMockDataBundle,
  mockDoctorWithError,
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

export interface Scenario {
  id: ScenarioId
  label: string
  description: string
  overrides?: Partial<MockDataBundle>
}

export const SCENARIOS: Scenario[] = [
  {
    id: 'populated',
    label: '正常填充',
    description: '默认的丰富数据状态，包含项目、会话、消息、编排、工具调用等完整数据。',
  },
  {
    id: 'empty',
    label: '空状态',
    description: '无项目、无会话、无消息的初始空状态，用于验证空态 UI。',
    overrides: {
      projects: [],
      sessions: [],
      messages: [],
      approvals: [],
      orchestration: null,
      toolExecutions: [],
      events: [],
      doctor: null,
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
    label: '加载中',
    description: '数据正在加载，所有集合为空但组件应显示骨架屏或加载指示器。',
    overrides: {
      projects: [],
      sessions: [],
      messages: [],
      approvals: [],
      orchestration: null,
      toolExecutions: [],
      events: [],
      doctor: null,
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
    label: '智能体工作中',
    description: '编排运行状态为 running，有正在执行的工具调用与待审批项。',
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
    label: '待审批',
    description: '有 3 个待审批项，涵盖 git / shell / code 三种类型，用于验证审批 UI。',
    overrides: {
      approvals: mockPendingApprovals('sess-tinadec-1001'),
    },
  },
  {
    id: 'error',
    label: '错误状态',
    description: 'Doctor 检查存在 error 项，Readiness 状态为 blocked，用于验证错误态 UI。',
    overrides: {
      doctor: mockDoctorWithError(),
      readiness: mockReadinessBlocked(),
    },
  },
  {
    id: 'agent-working',
    label: 'Agent 活动中',
    description: '编排运行中，多个 step_results 处于 stubbed 状态，展示 Agent 活动横幅。',
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
    label: '大量消息',
    description: '20+ 条消息，包含长 Markdown 内容，用于验证消息列表的滚动与渲染性能。',
    overrides: {
      messages: mockManyMessages('sess-tinadec-1001'),
    },
  },
  {
    id: 'git-changes',
    label: 'Git 变更',
    description: 'Git diff 预览包含 3 个 section（工作区/暂存/分支比较），多个文件变更。',
    overrides: {},
  },
  {
    id: 'tool-failures',
    label: '工具失败',
    description: '多个工具调用处于 failed 状态，用于验证失败态 UI 与错误展示。',
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
