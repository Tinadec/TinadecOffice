// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import { ref } from 'vue'
import OrganizationPanel from './OrganizationPanel.vue'
import panelSource from './OrganizationPanel.vue?raw'
// The mint point of the report vocabulary. Read as text: Core's lists are the ones to translate.
import organizationToolsSource from '../../../../../TinadecCore/TinaChat/TinaChatService.OrganizationTools.cs?raw'
import zh from '@/locales/zh-CN'
import en from '@/locales/en'
import type { OrganizationDto, OrganizationMemberDto, OrganizationMessageDto, OrganizationReportDto, OrganizationRoomDto, SessionTopologyDto } from '@/api'

const mocks = vi.hoisted(() => ({
  getOrganization: vi.fn(), readRoom: vi.fn(), post: vi.fn(), reports: vi.fn(), decide: vi.fn(), topology: vi.fn(), setVisibility: vi.fn(),
  success: vi.fn(), warning: vi.fn(), error: vi.fn(), statusError: vi.fn(), dismiss: vi.fn(),
}))
vi.mock('@/api', () => ({ api: {
  getOrganization: mocks.getOrganization, readOrganizationRoom: mocks.readRoom, postOrganizationMessage: mocks.post,
  listOrganizationReports: mocks.reports, decideOrganizationReport: mocks.decide, getSessionTopology: mocks.topology,
  setOrganizationMemberVisibility: mocks.setVisibility,
} }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({
  notify: { success: mocks.success, warning: mocks.warning, error: mocks.error },
  status: { error: mocks.statusError },
  dismissByKey: mocks.dismiss,
}) }))

const member = (id: string, name: string, role: string, extra: Partial<OrganizationMemberDto> = {}): OrganizationMemberDto =>
  ({ id, handle: id, display_name: name, role: role as OrganizationMemberDto['role'], presence: 'online', ...extra })
const room = (id: string, kind: OrganizationRoomDto['kind'], title: string, isMember: boolean, lastSequence = 2): OrganizationRoomDto =>
  ({ id, kind, title, last_sequence: lastSequence, member_count: 3, is_member: isMember })
const organization = (extra: Partial<OrganizationDto> = {}): OrganizationDto => ({
  id: 'org-1', session_id: 'session-1', status: 'active', you_participant_id: 'me',
  // Core lists online members first; the panel regroups them by role.
  members: [
    member('reviewer', '审查员', 'governance'),
    member('meeting', '会议主持', 'conversation'),
    member('me', '林', 'human'),
    member('search-1', 'search#1', 'executor', { parent_id: 'meeting', presence: 'offline', agent_slug: 'search' }),
  ],
  rooms: [room('plan-1', 'plan', '计划：设置页', false), room('lobby-1', 'lobby', '会话大厅', true)],
  open_reports: 1, members_truncated: false, rooms_truncated: false, ...extra,
})
const message = (id: string, sequence: number, content: string, extra: Partial<OrganizationMessageDto> = {}): OrganizationMessageDto => ({
  id, room_id: 'lobby-1', sender_id: 'meeting', sender_display_name: '会议主持', sender_role: 'conversation', sequence,
  kind: 'message', content, sensitivity: 'normal', created_at: '2026-09-29T08:00:00Z', ...extra,
})
const LEASE = '3f2a9c1e-7b1d-4c5e-9a00-000000000001'
const report = (id: string, extra: Partial<OrganizationReportDto> = {}): OrganizationReportDto => ({
  id, room_id: 'lobby-1', author_id: 'reviewer', author_display_name: '审查员', report_kind: 'conflict', severity: 'warning',
  status: 'open', subject_kind: 'lease', subject_id: LEASE, proposed_verb: 'serialize', finding: 'search#1 与 search#2 同时写 src/app.ts',
  evidence: ['租约 path src/app.ts 被 search#1 持有'], revision: 3, created_at: '2026-09-29T08:00:00Z',
  subject_state: { kind: 'lease', id: LEASE, status: 'active', label: 'path src/app.ts' }, ...extra,
})
const topologyView = (): SessionTopologyDto => ({
  session_id: 'session-1', generated_at: '2026-09-29T09:00:00Z',
  runs: [
    { run_id: 'aaaaaaaa-0000-4000-8000-000000000001', status: 'completed', started_at: '2026-09-29T07:00:00Z', completed_at: '2026-09-29T07:30:00Z',
      tier: 'solo', tasks: [], instances: [], tasks_truncated: false, instances_truncated: false },
    { run_id: 'bbbbbbbb-0000-4000-8000-000000000002', status: 'executing', started_at: '2026-09-29T08:00:00Z', tier: 'team', phase: 'executing',
      tasks: [{ task_id: 't-1', task_key: 'write-tests', title: '补测试', status: 'running', handle: 'search#1', dependencies: [],
        write_scope: ['src/app.ts', 'src/app.test.ts'] }],
      instances: [{ instance_id: 'i-1', agent_slug: 'search', layer: 'execution', role: 'worker', status: 'running', depth: 1 }],
      tasks_truncated: true, instances_truncated: false },
  ],
  leases: [{ lease_id: 'l-1', kind: 'path', resource_key: 'src/app.ts', purpose: '补测试时独占写入', exclusive: true, task_id: 't-1' }],
  members: [], runs_truncated: false, leases_truncated: true, members_truncated: false,
})

const mounted: Array<{ unmount(): void }> = []
function panel(sessionId: string | null = 'session-1', active: unknown = true) {
  const wrapper = mount(OrganizationPanel, {
    props: { sessionId },
    global: {
      plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })],
      provide: { 'uie:active': active },
    },
  })
  mounted.push(wrapper)
  return wrapper
}
async function openTab(wrapper: ReturnType<typeof panel>, key: string) {
  await wrapper.get(`[data-testid="organization-tab-${key}"]`).trigger('click')
  await flushPromises()
}
async function openRoom(wrapper: ReturnType<typeof panel>, id: string) {
  await openTab(wrapper, 'rooms')
  await wrapper.get(`[data-room-id="${id}"]`).trigger('click')
  await flushPromises()
}

beforeEach(() => {
  vi.clearAllMocks()
  mocks.getOrganization.mockResolvedValue(organization())
  mocks.readRoom.mockResolvedValue({ items: [], next_cursor: 0 })
  mocks.reports.mockResolvedValue({ items: [], truncated: false })
  mocks.topology.mockResolvedValue(topologyView())
})
afterEach(() => {
  mounted.splice(0).forEach((wrapper) => wrapper.unmount())
  vi.useRealTimers()
})

describe('OrganizationPanel members', () => {
  it('groups members by role in a fixed order, with presence and who dispatched an executor', async () => {
    const wrapper = panel()
    await flushPromises()
    expect(mocks.getOrganization).toHaveBeenCalledWith('session-1')
    expect(wrapper.findAll('.org-group').map((group) => group.attributes('data-role'))).toEqual(['human', 'conversation', 'governance', 'executor'])
    expect(wrapper.findAll('.org-group h3').map((heading) => heading.text())).toEqual(['用户1', '对话身份1', '治理1', '执行者1'])

    const executor = wrapper.get('[data-member-id="search-1"]')
    expect(executor.classes()).toContain('offline')
    expect(executor.text()).toContain('（离线）')
    expect(executor.text()).toContain('由 会议主持 派出')
    expect(executor.get('.presence-dot').attributes('aria-label')).toBe('离线')
    const online = wrapper.get('[data-member-id="reviewer"]')
    expect(online.get('.presence-dot').classes()).toContain('online')
    expect(online.text()).not.toContain('（离线）')
    expect(wrapper.get('[data-member-id="me"]').text()).toContain('（你）')
  })

  it('lets the owner mute a machine member’s visibility and shows the state, never on the human row', async () => {
    mocks.setVisibility.mockResolvedValue({ ...member('reviewer', '审查员', 'governance'), visibility_scope: 'own' })
    const wrapper = panel()
    await flushPromises()

    const reviewer = wrapper.get('[data-member-id="reviewer"]')
    expect(reviewer.find('[data-visibility-toggle="reviewer"]').exists()).toBe(true)
    // The human row is the owner themselves: blinding the panel's only reader makes no sense.
    expect(wrapper.get('[data-member-id="me"]').find('[data-visibility-toggle]').exists()).toBe(false)

    await reviewer.get('[data-visibility-toggle="reviewer"]').trigger('click')
    await flushPromises()
    expect(mocks.setVisibility).toHaveBeenCalledWith('session-1', 'reviewer', 'own')
    expect(wrapper.get('[data-member-id="reviewer"]').text()).toContain('仅本 run')
    expect(mocks.warning).toHaveBeenCalled()

    // Clicking again restores the default (null, not the "down" string).
    mocks.setVisibility.mockResolvedValue(member('reviewer', '审查员', 'governance'))
    await wrapper.get('[data-member-id="reviewer"] [data-visibility-toggle="reviewer"]').trigger('click')
    await flushPromises()
    expect(mocks.setVisibility).toHaveBeenLastCalledWith('session-1', 'reviewer', null)
  })

  it('says the organization is created by the first run while Core has none, and asks for a session without one', async () => {
    mocks.getOrganization.mockResolvedValue(null)
    const wrapper = panel()
    await flushPromises()
    expect(wrapper.get('[data-testid="organization-not-started"]').text()).toContain('还没有组织——会话第一次运行时建立')
    expect(wrapper.find('[role="tablist"]').exists()).toBe(false)

    const idle = panel(null)
    await flushPromises()
    expect(idle.find('[data-testid="organization-no-session"]').exists()).toBe(true)
    expect(mocks.getOrganization).toHaveBeenCalledTimes(1)
  })

  it('drops an answer that arrives after the session changed', async () => {
    let resolveOld!: (value: OrganizationDto) => void
    mocks.getOrganization.mockImplementationOnce(() => new Promise<OrganizationDto>((resolve) => { resolveOld = resolve }))
    mocks.getOrganization.mockResolvedValue(organization({ session_id: 'session-2', members: [member('me', '林', 'human'), member('solo', '独奏者', 'conversation')] }))
    const wrapper = panel('session-1')
    await wrapper.setProps({ sessionId: 'session-2' })
    await flushPromises()
    resolveOld(organization())
    await flushPromises()
    expect(mocks.getOrganization).toHaveBeenLastCalledWith('session-2')
    expect(wrapper.find('[data-member-id="solo"]').exists()).toBe(true)
    expect(wrapper.find('[data-member-id="search-1"]').exists()).toBe(false)
  })

  it('polls every five seconds while the card is active, and stops when it is hidden or unmounted', async () => {
    vi.useFakeTimers()
    const active = ref(true)
    const wrapper = panel('session-1', active)
    await flushPromises()
    expect(mocks.getOrganization).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(5000)
    expect(mocks.getOrganization).toHaveBeenCalledTimes(2)

    active.value = false
    await vi.advanceTimersByTimeAsync(15000)
    expect(mocks.getOrganization).toHaveBeenCalledTimes(2)
    // Coming back into view reads at once instead of waiting out the interval.
    active.value = true
    await flushPromises()
    expect(mocks.getOrganization).toHaveBeenCalledTimes(3)

    wrapper.unmount()
    mounted.splice(mounted.indexOf(wrapper), 1)
    await vi.advanceTimersByTimeAsync(15000)
    expect(mocks.getOrganization).toHaveBeenCalledTimes(3)
  })
})

describe('OrganizationPanel rooms', () => {
  it('lists rooms by kind and reads the newest page of a selected room, then earlier ones', async () => {
    mocks.getOrganization.mockResolvedValue(organization({ rooms: [room('plan-1', 'plan', '计划：设置页', false), room('lobby-1', 'lobby', '会话大厅', true, 120)] }))
    mocks.readRoom.mockResolvedValueOnce({
      items: [
        message('m-119', 119, '先跑测试'),
        message('m-120', 120, '[report · conflict · warning] …', { kind: 'report', sender_display_name: '审查员', sender_role: 'governance', report: report('m-120') }),
        message('m-118', 118, 'search#1 已完成任务', { kind: 'notice', sender_display_name: 'host', sender_role: '' }),
      ],
      next_cursor: 120,
    })
    const wrapper = panel()
    await flushPromises()
    await openTab(wrapper, 'rooms')
    expect(wrapper.findAll('.org-group').map((group) => group.attributes('data-kind'))).toEqual(['lobby', 'plan'])
    expect(wrapper.get('[data-kind="lobby"] h3').text()).toContain('大厅')
    expect(wrapper.get('[data-kind="plan"] h3').text()).toContain('计划室')
    expect(wrapper.get('[data-room-id="plan-1"]').text()).toContain('只读')

    await wrapper.get('[data-room-id="lobby-1"]').trigger('click')
    await flushPromises()
    // Newest page first: (70, 120] is a single page because a room numbers its messages densely.
    expect(mocks.readRoom).toHaveBeenCalledWith('session-1', 'lobby-1', 70, 50)
    const rows = wrapper.findAll('.org-message')
    expect(rows.map((row) => row.attributes('data-message-id'))).toEqual(['m-118', 'm-119', 'm-120'])
    expect(rows[0].get('.org-tag[data-kind="notice"]').text()).toBe('通知')
    expect(rows[1].find('.org-tag').exists()).toBe(false)
    expect(rows[2].get('.org-tag[data-kind="report"]').text()).toBe('报告')
    expect(rows[2].text()).toContain('search#1 与 search#2 同时写 src/app.ts')
    expect(wrapper.find('[data-testid="organization-composer"]').exists()).toBe(true)

    mocks.readRoom.mockResolvedValueOnce({ items: [message('m-70', 70, '最早的一条')], next_cursor: 70 })
    await wrapper.get('[data-testid="organization-earlier"]').trigger('click')
    await flushPromises()
    expect(mocks.readRoom).toHaveBeenLastCalledWith('session-1', 'lobby-1', 20, 50)
    expect(wrapper.findAll('.org-message')[0].attributes('data-message-id')).toBe('m-70')
  })

  it('offers no composer in a room the user is not a member of, nor in an archived organization', async () => {
    const wrapper = panel()
    await flushPromises()
    await openRoom(wrapper, 'plan-1')
    expect(wrapper.find('[data-testid="organization-composer"]').exists()).toBe(false)
    expect(wrapper.find('textarea').exists()).toBe(false)
    expect(wrapper.get('[data-testid="organization-room-readonly"]').text()).toContain('你不是这个会议室的成员')

    mocks.getOrganization.mockResolvedValue(organization({ status: 'archived' }))
    const archived = panel()
    await flushPromises()
    expect(archived.get('[data-testid="organization-archived"]').text()).toContain('会话已归档')
    await openRoom(archived, 'lobby-1')
    expect(archived.find('[data-testid="organization-composer"]').exists()).toBe(false)
    expect(archived.get('[data-testid="organization-room-readonly"]').text()).toContain('会话已归档')
  })

  it('posts with mention handles and retries the same text under the same client message id', async () => {
    mocks.post.mockRejectedValueOnce(new Error('Cannot connect to backend'))
    mocks.post.mockResolvedValueOnce(message('m-3', 3, '先串行', { sender_id: 'me', sender_display_name: '林', sender_role: 'human' }))
    const wrapper = panel()
    await flushPromises()
    await openRoom(wrapper, 'lobby-1')
    await wrapper.get('textarea').setValue('先串行')
    await wrapper.get('[data-testid="organization-mention-toggle"]').trigger('click')
    const picker = wrapper.get('[data-testid="organization-mention-picker"]')
    // The user is never offered as a mention of themselves.
    expect(picker.find('[data-handle="me"]').exists()).toBe(false)
    await picker.get('[data-handle="reviewer"]').trigger('click')
    expect(wrapper.get('.org-mention').text()).toContain('审查员')

    await wrapper.get('[data-testid="organization-composer"]').trigger('submit')
    await flushPromises()
    expect(mocks.error).toHaveBeenCalledTimes(1)
    expect(wrapper.get('textarea').element.value).toBe('先串行')

    await wrapper.get('textarea').trigger('keydown', { key: 'Enter' })
    await flushPromises()
    expect(mocks.post).toHaveBeenCalledTimes(2)
    const [first, second] = mocks.post.mock.calls
    expect(first.slice(0, 2)).toEqual(['session-1', 'lobby-1'])
    expect(first[2]).toEqual({ content: '先串行', client_message_id: expect.any(String), mention: ['reviewer'] })
    expect(second[2].client_message_id).toBe(first[2].client_message_id)
    expect(wrapper.get('[data-message-id="m-3"]').text()).toContain('先串行')
    expect(wrapper.get('textarea').element.value).toBe('')
  })
})

describe('OrganizationPanel reports', () => {
  it('shows the live state of what a report is about and decides it at the revision it was read at', async () => {
    mocks.reports.mockResolvedValue({ items: [
      report('r-closed', { status: 'acted', severity: 'info', decided_by_id: 'me', decided_at: '2026-09-29T09:00:00Z', decision_note: '已处理完',
        subject_state: { kind: 'lease', id: LEASE, status: 'released' } }),
      report('r-1'),
    ], truncated: false })
    mocks.decide.mockResolvedValue(report('r-1', { status: 'acted', revision: 4, decided_by_id: 'me', decided_at: '2026-09-29T09:05:00Z' }))
    const wrapper = panel()
    await flushPromises()
    expect(wrapper.get('[data-testid="organization-tab-reports"]').text()).toContain('1')
    await openTab(wrapper, 'reports')
    expect(mocks.reports).toHaveBeenCalledWith('session-1', undefined, 100)

    const rows = wrapper.findAll('.org-report')
    expect(rows.map((row) => row.attributes('data-report-id'))).toEqual(['r-1', 'r-closed'])
    expect(rows[0].get('.org-subject-state').text()).toBe('持有中')
    expect(rows[0].get('[data-testid="organization-report-subject"]').text()).toContain('path src/app.ts')
    expect(rows[0].text()).toContain('串行执行')
    expect(rows[0].text()).toContain('租约 path src/app.ts 被 search#1 持有')
    expect(rows[1].get('.org-subject-state').text()).toBe('已释放')
    expect(rows[1].find('[data-testid="organization-act-r-closed"]').exists()).toBe(false)
    expect(rows[1].text()).toContain('已处理完')

    await rows[0].get('input').setValue('已改为串行')
    await wrapper.get('[data-testid="organization-act-r-1"]').trigger('click')
    await flushPromises()
    expect(mocks.decide).toHaveBeenCalledWith('session-1', 'r-1', { decision: 'acted', expected_revision: 3, note: '已改为串行' })
    expect(mocks.success).toHaveBeenCalledTimes(1)
  })

  it('re-reads and says so when the report changed underneath (412) or was already decided (409)', async () => {
    mocks.reports
      .mockResolvedValueOnce({ items: [report('r-1')], truncated: false })
      .mockResolvedValue({ items: [report('r-1', { revision: 4, finding: '冲突已扩大到 src/lib' })], truncated: false })
    mocks.decide
      .mockRejectedValueOnce(Object.assign(new Error('Expected revision 3; current revision is 4.'), { status: 412, code: 'tina_chat_revision_conflict' }))
      .mockRejectedValueOnce(Object.assign(new Error('The report is already acted.'), { status: 409, code: 'report_closed' }))
    const wrapper = panel()
    await flushPromises()
    await openTab(wrapper, 'reports')

    await wrapper.get('[data-testid="organization-dismiss-r-1"]').trigger('click')
    await flushPromises()
    expect(mocks.decide).toHaveBeenLastCalledWith('session-1', 'r-1', { decision: 'dismissed', expected_revision: 3 })
    expect(mocks.warning).toHaveBeenLastCalledWith(expect.objectContaining({ message: expect.stringContaining('有了变化') }))
    expect(wrapper.get('[data-report-id="r-1"]').text()).toContain('冲突已扩大到 src/lib')

    await wrapper.get('[data-testid="organization-dismiss-r-1"]').trigger('click')
    await flushPromises()
    // The retry carries the revision the reader has now seen.
    expect(mocks.decide).toHaveBeenLastCalledWith('session-1', 'r-1', { decision: 'dismissed', expected_revision: 4 })
    expect(mocks.warning).toHaveBeenLastCalledWith(expect.objectContaining({ message: expect.stringContaining('已被处理') }))
    expect(mocks.error).not.toHaveBeenCalled()
  })
})

describe('OrganizationPanel topology', () => {
  it('draws runs newest first with their tasks, leases and truncation notices', async () => {
    const wrapper = panel()
    await flushPromises()
    await openTab(wrapper, 'topology')
    expect(mocks.topology).toHaveBeenCalledWith('session-1')

    const runs = wrapper.findAll('.org-run')
    expect(runs.map((run) => run.attributes('data-run-id'))).toEqual(['bbbbbbbb-0000-4000-8000-000000000002', 'aaaaaaaa-0000-4000-8000-000000000001'])
    expect(runs[0].text()).toContain('运行 bbbbbbbb')
    expect(runs[0].text()).toContain('执行中')
    expect(runs[0].text()).toContain('档位 team')
    const cells = runs[0].get('[data-task-id="t-1"]').findAll('td').map((cell) => cell.text())
    expect(cells).toEqual(['search#1', '补测试', '运行中', 'src/app.ts, src/app.test.ts'])
    expect(runs[0].text()).toContain('任务较多，列表已截断')
    expect(runs[1].text()).toContain('这个运行还没有任务')

    const lease = wrapper.get('[data-lease-id="l-1"]')
    expect(lease.text()).toContain('path src/app.ts')
    expect(lease.text()).toContain('补测试时独占写入')
    expect(lease.text()).toContain('独占')
    expect(lease.text()).toContain('持有者 search#1')
    expect(wrapper.text()).toContain('租约较多，列表已截断')
  })
})

describe('OrganizationPanel vocabulary', () => {
  type Bundle = Record<string, unknown>
  const resolve = (bundle: Bundle, key: string): unknown =>
    key.split('.').reduce<unknown>((node, part) => (node && typeof node === 'object' ? (node as Bundle)[part] : undefined), bundle)

  it('references only keys both locales define, and puts no raw CJK in its template', () => {
    const keys = [...panelSource.matchAll(/\bt\(\s*'([a-zA-Z0-9_.]+)'/g)].map((match) => match[1])
    expect(keys.length).toBeGreaterThan(40)
    for (const key of keys) {
      expect(typeof resolve(zh as Bundle, key), `zh-CN is missing ${key}`).toBe('string')
      expect(typeof resolve(en as Bundle, key), `en is missing ${key}`).toBe('string')
    }
    const template = panelSource.slice(panelSource.indexOf('<template>'), panelSource.lastIndexOf('</template>'))
      .replace(/<!--[\s\S]*?-->/g, '')
    expect(template.match(/[\u4e00-\u9fff]+/)?.[0] ?? null).toBeNull()
  })

  it('translates every word Core may put in a report', () => {
    // Read from the mint point, so a verb or kind Core adds shows up here before it shows up raw in the panel.
    const words = (name: string) => {
      const list = new RegExp(`${name}\\s*=\\s*\\[([^\\]]*)\\]`).exec(organizationToolsSource)?.[1]
      expect(list, `${name} not found in TinaChatService.OrganizationTools.cs`).toBeTruthy()
      return [...list!.matchAll(/"([a-z_]+)"/g)].map((match) => match[1])
    }
    const groups: Array<[string, string]> = [['ReportKinds', 'reportKind'], ['ReportSeverities', 'severity'], ['SubjectKinds', 'subjectKind'], ['ProposedVerbs', 'verb']]
    for (const [constant, group] of groups) {
      for (const word of words(constant)) {
        expect(typeof resolve(zh as Bundle, `organization.${group}.${word}`), `zh-CN organization.${group}.${word}`).toBe('string')
        expect(typeof resolve(en as Bundle, `organization.${group}.${word}`), `en organization.${group}.${word}`).toBe('string')
      }
    }
  })
})
