// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import zhCN from '@/locales/zh-CN'
import type {
  AgentModeTopologyDto,
  ApprovalDecisionResultDto,
  ApprovalDto,
  ApprovalGatesDto,
  ApprovalPolicySummaryDto,
  ApprovalRememberRuleInput,
  ApprovalRuleDto,
  BatchApprovalDecisionInput,
  BatchApprovalDecisionResultDto,
  EventEnvelope,
} from '@/api'
import type { CommandHost } from '@/lib/appCommands'
import { defaultSpaceOptions } from '@/lib/composerSettings'
import type { ToolEntry } from '@/lib/conversation/reducer'

const sseDeliveries: Array<(event: EventEnvelope) => void> = []

const apiMocks = vi.hoisted(() => ({
  getApprovalPolicy: vi.fn<() => Promise<ApprovalPolicySummaryDto>>(),
  getApprovalGates: vi.fn<(id: string) => Promise<ApprovalGatesDto | null>>(),
  decideApproval: vi.fn<
    (
      approvalId: string,
      decision: 'approved' | 'rejected',
      reason?: string | null,
      scope?: 'once' | 'run' | null,
      rememberRule?: ApprovalRememberRuleInput | null,
    ) => Promise<ApprovalDecisionResultDto>
  >(),
  batchDecideApprovals: vi.fn<(input: BatchApprovalDecisionInput) => Promise<BatchApprovalDecisionResultDto>>(),
  createApprovalRule: vi.fn(),
  revokeApprovalRule: vi.fn(),
  listAgentModeTopologies: vi.fn<() => Promise<AgentModeTopologyDto[]>>(),
  listMessages: vi.fn(),
  listApprovals: vi.fn<() => Promise<ApprovalDto[]>>(),
  listApprovalRules: vi.fn<() => Promise<ApprovalRuleDto[]>>(),
  getOrchestrationSnapshot: vi.fn(),
  listToolExecutions: vi.fn(),
  listRuns: vi.fn(),
  connectEvents: vi.fn(),
}))

vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return {
    ...actual,
    api: {
      ...actual.api,
      getApprovalPolicy: apiMocks.getApprovalPolicy,
      getApprovalGates: apiMocks.getApprovalGates,
      decideApproval: apiMocks.decideApproval,
      batchDecideApprovals: apiMocks.batchDecideApprovals,
      createApprovalRule: apiMocks.createApprovalRule,
      revokeApprovalRule: apiMocks.revokeApprovalRule,
      listAgentModeTopologies: apiMocks.listAgentModeTopologies,
      listMessages: apiMocks.listMessages,
      listApprovals: apiMocks.listApprovals,
      listApprovalRules: apiMocks.listApprovalRules,
      getOrchestrationSnapshot: apiMocks.getOrchestrationSnapshot,
      listToolExecutions: apiMocks.listToolExecutions,
      listRuns: apiMocks.listRuns,
      connectEvents: apiMocks.connectEvents,
    },
  }
})

vi.mock('@/composables/useWorkspaceCommands', () => ({
  useWorkspaceCommands: () => ({
    commands: { value: [] },
    refresh: vi.fn(),
  }),
  renderWorkspaceCommandTemplate: (cmd: { template?: string }) => cmd.template ?? '',
}))

vi.mock('@/components/ui', () => ({
  UiSelectField: defineComponent({
    name: 'UiSelectField',
    props: {
      modelValue: String,
      options: { type: Array as () => Array<{ value: string; label: string }>, default: () => [] },
    },
    emits: ['update:modelValue'],
    setup(props, { attrs, emit }) {
      return () =>
        h(
          'select',
          {
            ...attrs,
            value: props.modelValue,
            onChange: (e: Event) => emit('update:modelValue', (e.target as HTMLSelectElement).value),
          },
          props.options.map((opt) => h('option', { value: opt.value }, opt.label)),
        )
    },
  }),
}))

import ApprovalTab from '../ApprovalTab.vue'
import PermissionSelector from '../PermissionSelector.vue'
import ComposerCommandPanel from '../ComposerCommandPanel.vue'
import ApprovalGateStatus from '../approval/ApprovalGateStatus.vue'
import ConversationApprovalCard from '../conversation/ApprovalCard.vue'
import HomeApprovalCard from '../../../../TinadecUI/src/components/cards/home/ApprovalCard.vue'
import { permissionChoices } from '@/lib/composerCommands'
import { activeDockFocus, resetDockSyncForTests } from '@/lib/homeDockSync'
import { followSession } from '@/lib/sessionEventBus'
import { resetHostAccessForTests, setHostAccessStatus } from '@/lib/hostAccess'
import { __resetNotificationsForTests } from '@/composables/useNotifications'
import { homeController } from '@/controllers/HomeController'

const POLICY_SUMMARY: ApprovalPolicySummaryDto = {
  policy_version: 'tool-approval-v1',
  enabled: true,
  risk_max: 'medium',
  delegated_risk_max: 'medium',
  max_per_run: 32,
  release_read_only_in_ask_mode: true,
  human_only_tools: ['shell', 'command_run', 'git_push'],
  delegation_blocked_tools: ['web_fetch'],
}

const commandHostStub: CommandHost = {
  canStop: () => false,
  draft: () => '',
  setDraft: () => {},
  send: () => {},
  stopRun: () => {},
  newSession: () => {},
  navigate: () => {},
  routeName: () => 'home',
  toggleSessionPartner: () => {},
}

const wrappers: VueWrapper[] = []

function buildI18n(locale: 'zh-CN' | 'en' = 'zh-CN') {
  return createI18n({
    legacy: false,
    locale,
    messages: { 'zh-CN': zhCN, en },
  })
}

function makeApproval(overrides: Partial<ApprovalDto> & { id: string }): ApprovalDto {
  return {
    session_id: 'sess-1',
    kind: 'tool',
    tool_id: 'shell',
    risk: 'medium',
    summary: 'Execute shell command',
    command: 'npm test --watch',
    cwd: '/workspace/app',
    resource_path: null,
    arguments: null,
    status: 'pending',
    created_at: '2026-11-25T10:00:00Z',
    decided_at: null,
    decision: null,
    decision_reason: null,
    run_id: 'run-1',
    ...overrides,
  }
}

function makeToolEntry(overrides: Partial<ToolEntry> = {}): ToolEntry {
  return {
    kind: 'tool',
    id: 'entry-1',
    seq: 1,
    taskId: null,
    agentInstanceId: null,
    toolId: 'shell',
    status: 'awaiting_approval',
    risk: 'medium',
    requiresApproval: true,
    mutatesWorkspace: true,
    approvalId: 'app-chat-1',
    argumentsSummary: 'npm test',
    resourcePath: null,
    command: 'npm test --run',
    cwd: '/workspace/app',
    query: null,
    toolSuccess: null,
    errorCategory: null,
    exitCode: null,
    timedOut: false,
    output: [],
    outputPartial: '',
    outputDropped: 0,
    terminalSessionId: null,
    changedPaths: [],
    startedAt: '2026-11-25T10:00:00Z',
    endedAt: null,
    ...overrides,
  }
}

function emitSse(event: EventEnvelope) {
  for (const deliver of sseDeliveries) deliver(event)
}

beforeEach(() => {
  vi.clearAllMocks()
  sseDeliveries.length = 0
  __resetNotificationsForTests()
  resetDockSyncForTests()
  resetHostAccessForTests()
  setHostAccessStatus({ state: 'ready', managed: true })

  homeController.approvals.value = []
  homeController.approvalRules.value = []
  homeController.approvalPolicy.value = null
  homeController.selectedSessionId.value = null

  apiMocks.getApprovalPolicy.mockResolvedValue(POLICY_SUMMARY)
  apiMocks.getApprovalGates.mockResolvedValue(null)
  apiMocks.listAgentModeTopologies.mockResolvedValue([])
  apiMocks.listMessages.mockResolvedValue([])
  apiMocks.listApprovals.mockResolvedValue([])
  apiMocks.listApprovalRules.mockResolvedValue([])
  apiMocks.getOrchestrationSnapshot.mockResolvedValue(null)
  apiMocks.listToolExecutions.mockResolvedValue([])
  apiMocks.listRuns.mockResolvedValue([])
  apiMocks.decideApproval.mockImplementation(async (id, decision, reason, scope, rememberRule) => ({
    id,
    status: decision,
    decided_at: '2026-11-25T10:05:00Z',
    scope: scope ?? 'once',
    created_rule_id: rememberRule ? 'rule-created-1' : null,
    decision_reason: reason ?? null,
  }))
  apiMocks.batchDecideApprovals.mockImplementation(async (input) => ({
    results: input.approval_ids.map((id) => ({ id, ok: true, status: input.decision })),
  }))
  apiMocks.connectEvents.mockImplementation((_sessionId: string | null, onEvent: (e: EventEnvelope) => void) => {
    sseDeliveries.push(onEvent)
    return { close: vi.fn() } as unknown as EventSource
  })
})

afterEach(() => {
  vi.useRealTimers()
  followSession(null)
  wrappers.splice(0).forEach((w) => w.unmount())
  resetDockSyncForTests()
  __resetNotificationsForTests()
  resetHostAccessForTests()
  document.body.innerHTML = ''
})

describe('ApprovalTab redesign (Pending / Auto rules / History)', () => {
  it('renders 3 flat tabs without debug shell box, expands core evidence by default, and offers run scope for tool approvals', async () => {
    const shellApproval = makeApproval({
      id: 'app-1',
      kind: 'tool',
      tool_id: 'shell',
      command: 'git status -sb',
      cwd: '/repo',
      resource_path: 'src/index.ts',
      arguments: '{"command":"git status -sb","timeout":30}',
    })
    const specApproval = makeApproval({
      id: 'app-spec',
      kind: 'tool',
      tool_id: 'spec_propose',
      command: null,
      cwd: null,
      arguments: JSON.stringify({ stage: 'design', document: '# Full Architecture Spec\nLine 2' }),
    })

    const wrapper = mount(ApprovalTab, {
      props: {
        approvals: [shellApproval, specApproval],
        approvalRules: [],
        approvalPolicy: POLICY_SUMMARY,
        permission: 'auto-approve',
        busy: false,
        selectedSessionId: 'sess-1',
      },
      global: { plugins: [buildI18n('zh-CN')] },
    })
    wrappers.push(wrapper)
    await flushPromises()

    // Debug shell request box and collapsed <details> evidence folds are gone
    expect(wrapper.find('.approval-request-box').exists()).toBe(false)
    expect(wrapper.find('details').exists()).toBe(false)

    // 3 flat tabs show exact counts
    expect(wrapper.get('[data-testid="approval-tab-pending"]').text()).toContain('待处理')
    expect(wrapper.get('[data-testid="approval-tab-pending"]').text()).toContain('2')
    expect(wrapper.get('[data-testid="approval-tab-rules"]').text()).toContain('自动规则')
    expect(wrapper.get('[data-testid="approval-tab-history"]').text()).toContain('历史')

    // Evidence is expanded immediately without clicking any disclosure
    const firstCard = wrapper.get('[data-testid="approval-pending-app-1"]')
    expect(firstCard.get('[data-testid="approval-evidence-command"]').text()).toContain('git status -sb')
    expect(firstCard.get('[data-testid="approval-evidence-cwd"]').text()).toContain('/repo')
    expect(firstCard.get('[data-testid="approval-evidence-path"]').text()).toContain('src/index.ts')
    expect(firstCard.get('[data-testid="approval-evidence-args"]').text()).toContain('"timeout":30')

    // Spec proposal preserves full document with data-testid="approval-spec-document" and omits run-scope allow
    const specCard = wrapper.get('[data-testid="approval-pending-app-spec"]')
    expect(specCard.get('[data-testid="approval-spec-document"]').text()).toContain('# Full Architecture Spec')
    expect(specCard.find('[data-testid="approval-allow-run-btn"]').exists()).toBe(false)

    // `kind === 'tool'` non-spec approval offers unified `scope: 'run'`
    await firstCard.get('[data-testid="approval-allow-run-btn"]').trigger('click')
    const emitted = wrapper.emitted('decide-approval')
    expect(emitted).toBeTruthy()
    expect(emitted![0]).toEqual([shellApproval, 'approved', { scope: 'run' }])
  })

  it('submits atomic remember_rule with editable command prefix & scope, rejection reason on Enter/Esc, and batchDecideApprovals for >= 2 eligible items', async () => {
    const a1 = makeApproval({ id: 'app-1', command: 'npm test --watch' })
    const a2 = makeApproval({ id: 'app-2', tool_id: 'file_write', command: null, resource_path: 'src/a.ts' })
    const spec = makeApproval({
      id: 'app-spec',
      tool_id: 'spec_propose',
      command: null,
      arguments: JSON.stringify({ stage: 'tasks', document: '- Task 1' }),
    })
    const unknown = makeApproval({
      id: 'app-unknown',
      governance_status: 'outcome_unknown',
    })

    const wrapper = mount(ApprovalTab, {
      props: {
        approvals: [a1, a2, spec, unknown],
        approvalRules: [],
        busy: false,
        selectedSessionId: 'sess-1',
      },
      global: { plugins: [buildI18n('zh-CN')] },
    })
    wrappers.push(wrapper)

    // Batch button counts ONLY the 2 eligible pending items (excludes spec_propose and outcome_unknown)
    const batchBtn = wrapper.get('[data-testid="approval-batch-approve-btn"]')
    expect(batchBtn.text()).toContain('2')
    await batchBtn.trigger('click')
    expect(wrapper.emitted('batch-decide')?.[0]?.[0]).toEqual({
      approval_ids: ['app-1', 'app-2'],
      decision: 'approved',
      scope: 'once',
    })

    // Atomic remember_rule flow: pre-filled with 'npm test', editable, workspace scope selectable
    const card1 = wrapper.get('[data-testid="approval-pending-app-1"]')
    await card1.get('[data-testid="approval-remember-toggle-btn"]').trigger('click')
    const prefixInput = card1.get('[data-testid="approval-remember-prefix-input"]')
    expect((prefixInput.element as HTMLInputElement).value).toBe('npm test')
    await prefixInput.setValue('npm test --run')
    await card1.get('[data-testid="approval-remember-scope-workspace"]').trigger('click')
    await card1.get('[data-testid="approval-remember-submit-btn"]').trigger('click')

    const decisions = wrapper.emitted('decide-approval')!
    expect(decisions[0]).toEqual([
      a1,
      'approved',
      {
        scope: 'once',
        rememberRule: {
          kind: 'command_prefix',
          pattern: 'npm test --run',
          scope: 'workspace',
        },
      },
    ])

    // Rejection steering reason: Esc closes, Enter submits reason back to Core
    const card2 = wrapper.get('[data-testid="approval-pending-app-2"]')
    await card2.get('[data-testid="approval-reject-reason-toggle"]').trigger('click')
    expect(card2.find('[data-testid="approval-reject-reason-input"]').exists()).toBe(true)
    await card2.get('[data-testid="approval-reject-reason-input"]').trigger('keydown', { key: 'Escape' })
    expect(card2.find('[data-testid="approval-reject-reason-input"]').exists()).toBe(false)

    await card2.get('[data-testid="approval-reject-reason-toggle"]').trigger('click')
    const reasonInput = card2.get('[data-testid="approval-reject-reason-input"]')
    await reasonInput.setValue('请改写到 src/b.ts')
    await reasonInput.trigger('keydown', { key: 'Enter' })

    expect(decisions[1]).toEqual([a2, 'rejected', { reason: '请改写到 src/b.ts' }])
  })

  it('preserves approved, rejected, expired, and outcome_unknown items in History sorted newest-first with filter pills and decision_reason', async () => {
    const historyItems: ApprovalDto[] = [
      makeApproval({
        id: 'h-expired',
        status: 'expired',
        command: 'git push',
        created_at: '2026-11-25T08:00:00Z',
        decided_at: '2026-11-25T08:05:00Z',
      }),
      makeApproval({
        id: 'h-approved',
        status: 'approved',
        decision: 'approved',
        command: 'bun x vitest run',
        created_at: '2026-11-25T09:00:00Z',
        decided_at: '2026-11-25T09:30:00Z',
      }),
      makeApproval({
        id: 'h-rejected',
        status: 'rejected',
        decision: 'rejected',
        command: 'rm -rf dist',
        decision_reason: '不要删除构建产物',
        created_at: '2026-11-25T09:10:00Z',
        decided_at: '2026-11-25T09:45:00Z',
      }),
      makeApproval({
        id: 'h-granted',
        status: 'granted',
        command: 'git status',
        created_at: '2026-11-25T08:30:00Z',
        decided_at: '2026-11-25T08:31:00Z',
      }),
    ]

    const wrapper = mount(ApprovalTab, {
      props: {
        approvals: historyItems,
        approvalRules: [],
        busy: false,
        selectedSessionId: 'sess-1',
      },
      global: { plugins: [buildI18n('zh-CN')] },
    })
    wrappers.push(wrapper)

    expect(wrapper.get('[data-testid="approval-tab-history"]').text()).toContain('4')
    await wrapper.get('[data-testid="approval-tab-history"]').trigger('click')

    // All 4 non-pending items are preserved and sorted newest-first
    const allCards = wrapper.findAll('[data-testid="approval-history-item"]')
    expect(allCards).toHaveLength(4)
    expect(allCards[0]!.text()).toContain('rm -rf dist')
    expect(allCards[0]!.get('[data-testid="approval-history-reason"]').text()).toBe('不要删除构建产物')
    expect(allCards[1]!.text()).toContain('bun x vitest run')
    expect(allCards[2]!.text()).toContain('git status')
    expect(allCards[3]!.text()).toContain('git push')

    // Filter: Approved -> shows approved + granted
    await wrapper.get('[data-testid="approval-history-filter-approved"]').trigger('click')
    const approvedCards = wrapper.findAll('[data-testid="approval-history-item"]')
    expect(approvedCards).toHaveLength(2)
    expect(approvedCards[0]!.text()).toContain('bun x vitest run')
    expect(approvedCards[1]!.text()).toContain('git status')

    // Filter: Rejected -> shows rejected item with decision_reason
    await wrapper.get('[data-testid="approval-history-filter-rejected"]').trigger('click')
    const rejectedCards = wrapper.findAll('[data-testid="approval-history-item"]')
    expect(rejectedCards).toHaveLength(1)
    expect(rejectedCards[0]!.text()).toContain('rm -rf dist')
  })
})

describe('Unified permissionChoices, policy summary, and rules shortcut', () => {
  it('shares 2-group permissionChoices across PermissionSelector and ComposerCommandPanel and focuses ApprovalCard on rules tab', async () => {
    expect(permissionChoices.filter((c) => c.group === 'standard').map((c) => c.value)).toEqual([
      'default',
      'auto-approve',
      'full-access',
    ])
    expect(permissionChoices.filter((c) => c.group === 'delegated').map((c) => c.value)).toEqual([
      'delegate-conversation',
      'delegate-reviewer',
      'delegate-both',
    ])

    const sampleRule: ApprovalRuleDto = {
      id: 'rule-1',
      kind: 'command_prefix',
      tool_id: 'shell',
      pattern: 'git status',
      session_id: 'sess-1',
      created_by_principal_id: 'user-1',
      created_at: '2026-11-25T09:00:00Z',
      use_count: 3,
    }
    homeController.approvalRules.value = [sampleRule]
    apiMocks.getApprovalPolicy.mockResolvedValue({
      ...POLICY_SUMMARY,
      risk_max: 'low',
      delegated_risk_max: 'medium',
    })

    // 1. PermissionSelector
    const selector = mount(PermissionSelector, {
      attachTo: document.body,
      props: { modelValue: 'auto-approve' },
      global: {
        plugins: [buildI18n('zh-CN')],
        stubs: { Teleport: true },
      },
    })
    wrappers.push(selector)
    await selector.get('.permission-selector-trigger').trigger('click')
    await flushPromises()

    expect(selector.get('[data-testid="permission-group-standard"]').text()).toBe('常规审批档位')
    expect(selector.get('[data-testid="permission-group-delegated"]').text()).toBe('AI 协助预审（高风险或不确定仍转人工确认）')
    const selectorFooter = selector.get('[data-testid="permission-policy-footer"]')
    expect(selectorFooter.attributes('data-risk-max')).toBe('low')
    expect(selectorFooter.text()).toContain('低风险')
    expect(selectorFooter.text()).toContain('1')

    await selector.get('[data-testid="permission-manage-rules-btn"]').trigger('click')
    expect(activeDockFocus.value?.card).toBe('approval')
    expect(activeDockFocus.value?.approvalTab).toBe('rules')

    // Verify TinadecUI HomeApprovalCard mounts ApprovalTab directly (no GovernanceBoardContent tabs) and switches to rules
    const homeCard = mount(HomeApprovalCard, {
      global: {
        plugins: [buildI18n('zh-CN')],
        stubs: { CardScroll: defineComponent({ setup(_, { slots }) { return () => h('div', slots.default?.()) } }) },
      },
    })
    wrappers.push(homeCard)
    await flushPromises()
    expect(homeCard.find('[data-testid="approval-card-tab-governance"]').exists()).toBe(false)
    expect(homeCard.find('[data-testid="approval-pane-rules"]').exists()).toBe(true)
    expect(homeCard.get('[data-testid="approval-rule-rule-1"]').text()).toContain('git status')

    // 2. ComposerCommandPanel on permission page
    resetDockSyncForTests()
    const anchor = document.createElement('div')
    document.body.appendChild(anchor)
    const panel = mount(ComposerCommandPanel, {
      attachTo: document.body,
      props: {
        open: true,
        anchor,
        initialPage: 'permission',
        canAttach: true,
        permission: 'delegate-reviewer',
        spaceOptions: defaultSpaceOptions(),
        commandHost: commandHostStub,
      },
      global: {
        plugins: [buildI18n('zh-CN')],
        stubs: { Teleport: true },
      },
    })
    wrappers.push(panel)
    await flushPromises()

    expect(panel.get('[data-testid="command-panel-permission-group-standard"]').text()).toBe('常规审批档位')
    expect(panel.get('[data-testid="command-panel-permission-group-delegated"]').text()).toBe('AI 协助预审（高风险或不确定仍转人工确认）')
    const panelFooter = panel.get('[data-testid="command-panel-permission-footer"]')
    expect(panelFooter.attributes('data-risk-max')).toBe('medium')
    expect(panelFooter.text()).toContain('中风险')
    expect(panelFooter.text()).toContain('1')

    await panel.get('[data-testid="command-panel-manage-rules-btn"]').trigger('click')
    expect(activeDockFocus.value?.card).toBe('approval')
    expect(activeDockFocus.value?.approvalTab).toBe('rules')
  })
})

describe('In-stream conversation/ApprovalCard atomic sync & ApprovalGateStatus SSE', () => {
  it('sends a single atomic remember_rule decision on allowPrefix, passes rejection reason, and syncs in lockstep with ApprovalTab', async () => {
    const sharedApproval = makeApproval({ id: 'app-chat-1', command: 'npm test --run' })
    homeController.selectedSessionId.value = 'sess-1'
    homeController.approvals.value = [sharedApproval]

    apiMocks.listApprovals.mockResolvedValue([
      { ...sharedApproval, status: 'approved', decision: 'approved', decided_at: '2026-11-25T10:05:00Z' },
    ])
    apiMocks.listApprovalRules.mockResolvedValue([
      {
        id: 'rule-created-1',
        kind: 'command_prefix',
        tool_id: 'shell',
        pattern: 'npm',
        session_id: 'sess-1',
        created_by_principal_id: 'user-1',
        created_at: '2026-11-25T10:05:00Z',
        use_count: 1,
      },
    ])

    const chatCard = mount(ConversationApprovalCard, {
      props: {
        entry: makeToolEntry({ approvalId: 'app-chat-1', command: 'npm test --run' }),
        runId: 'run-1',
      },
      global: { plugins: [buildI18n('zh-CN')] },
    })
    const rightTab = mount(ApprovalTab, {
      props: {
        approvals: homeController.approvals.value,
        approvalRules: homeController.approvalRules.value,
        busy: false,
        selectedSessionId: 'sess-1',
      },
      global: { plugins: [buildI18n('zh-CN')] },
    })
    wrappers.push(chatCard, rightTab)

    // Click "始终允许以 npm 开头的命令" -> single atomic decideApproval call, zero createApprovalRule call
    await chatCard.get('[data-testid="approval-allow-prefix"]').trigger('click')
    await flushPromises()

    expect(apiMocks.createApprovalRule).not.toHaveBeenCalled()
    expect(apiMocks.decideApproval).toHaveBeenCalledTimes(1)
    expect(apiMocks.decideApproval).toHaveBeenCalledWith(
      'app-chat-1',
      'approved',
      null,
      undefined,
      { kind: 'command_prefix', pattern: 'npm', scope: 'session' },
    )

    // In-stream card immediately settles/hides and HomeController refreshes rules + history in lockstep
    expect(chatCard.find('[data-testid="approval-card"]').exists()).toBe(false)
    expect(homeController.approvalRules.value).toHaveLength(1)
    expect(homeController.approvals.value[0]?.status).toBe('approved')

    // Also verify rejecting with correction passes `reason` atomically
    const secondApproval = makeApproval({ id: 'app-chat-2', command: 'git clean -fd' })
    homeController.approvals.value = [secondApproval]
    apiMocks.listApprovals.mockResolvedValue([
      { ...secondApproval, status: 'rejected', decision: 'rejected', decision_reason: '先用 git status 检查' },
    ])

    const chatCard2 = mount(ConversationApprovalCard, {
      props: {
        entry: makeToolEntry({ approvalId: 'app-chat-2', command: 'git clean -fd' }),
        runId: 'run-1',
      },
      global: { plugins: [buildI18n('zh-CN')] },
    })
    wrappers.push(chatCard2)

    await chatCard2.get('[data-testid="approval-instead"]').trigger('click')
    await chatCard2.get('[data-testid="approval-correction"]').setValue('先用 git status 检查')
    await chatCard2.get('[data-testid="approval-send-correction"]').trigger('click')
    await flushPromises()

    expect(apiMocks.decideApproval).toHaveBeenLastCalledWith(
      'app-chat-2',
      'rejected',
      '先用 git status 检查',
      undefined,
      null,
    )
  })

  it('updates ApprovalGateStatus live from approval.gate_updated SSE without 2s polling', async () => {
    vi.useFakeTimers()
    followSession('sess-1')

    apiMocks.getApprovalGates.mockResolvedValue({
      approval_id: 'app-gate-1',
      run_id: 'run-1',
      status: 'evaluating',
      gates: [
        {
          gate_index: 0,
          gate_kind: 'reviewer_agent',
          status: 'evaluating',
          decider_agent: 'reviewer-1',
          reason: null,
          evidence: null,
          created_at: '2026-11-25T10:00:00Z',
          decided_at: null,
        },
      ],
    })

    const wrapper = mount(ApprovalGateStatus, {
      props: { approvalId: 'app-gate-1' },
      global: { plugins: [buildI18n('zh-CN')] },
    })
    wrappers.push(wrapper)
    await flushPromises()

    expect(apiMocks.getApprovalGates).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-testid="approval-gate-status"]').attributes('data-status')).toBe('evaluating')
    expect(wrapper.text()).toContain('审批中')

    // Advancing 10 seconds triggers ZERO 2-second polling requests
    await vi.advanceTimersByTimeAsync(10_000)
    expect(apiMocks.getApprovalGates).toHaveBeenCalledTimes(1)

    // Push SSE `approval.gate_updated` event -> updates live without extra HTTP poll
    emitSse({
      v: '1',
      type: 'approval.gate_updated',
      request_id: 'req-1',
      session_id: 'sess-1',
      run_id: 'run-1',
      trace_id: 'trace-1',
      seq: 2,
      ts: '2026-11-25T10:00:12Z',
      capabilities: [],
      payload: {
        approval_id: 'app-gate-1',
        session_id: 'sess-1',
        status: 'escalated',
        gates: [
          {
            gate_index: 0,
            gate_kind: 'reviewer_agent',
            status: 'escalated',
            decider_agent: 'reviewer-1',
            reason: '检测到外部网络请求，转交人工确认',
            evidence: null,
            created_at: '2026-11-25T10:00:00Z',
            decided_at: '2026-11-25T10:00:12Z',
          },
        ],
      },
    })
    await flushPromises()

    expect(apiMocks.getApprovalGates).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-testid="approval-gate-status"]').attributes('data-status')).toBe('escalated')
    expect(wrapper.text()).toContain('检测到外部网络请求，转交人工确认')
    expect(wrapper.text()).toContain('已交还给你决定')
  })
})
