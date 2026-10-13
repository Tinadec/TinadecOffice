// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { nextTick, ref, shallowRef } from 'vue'
import type { ApprovalDto, MessageDto } from '@/api'
import type { MessageAttachmentSummaryDto } from '@/generated/client'
import en from '@/locales/en'
import zh from '@/locales/zh-CN'
import { applyEvents, createFeedState, type MarkerType } from '@/lib/conversation/reducer'
import { exploreEditShell, failedRun, parallelSubAgents, pendingApproval, simpleAnswer, type FeedFixture } from '@/lib/conversation/fixtures'
import type { ConversationFeed } from '@/composables/useConversationFeed'

/**
 * The flat feed's behaviour, migrated from the deleted MessageList.test.ts: the first turn shows its tool work and
 * approval before any answer exists, activity belongs to the turn that ran it, edit-and-resend
 * keeps its rules, and the attachment strip keeps its security properties.
 */
const controller = vi.hoisted(() => ({
  currentSession: { value: { id: 'session-1' } },
  decideApprovalById: vi.fn(async () => {}),
  createApprovalRule: vi.fn(async () => {}),
  onEvent: vi.fn(() => () => {}),
}))
vi.mock('@/controllers/HomeController', () => ({ homeController: controller }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify: { error: vi.fn() } }) }))
const apiMock = vi.hoisted(() => ({
  attachmentContentUrl: (id: string) => `http://gateway.test/api/v1/attachments/${id}/content`,
  listAgents: vi.fn(async () => [{ slug: 'search', display_name: 'Search' }]),
  getToolExecutionPreview: vi.fn(),
  controlRun: vi.fn(),
  createInteraction: vi.fn(),
}))
vi.mock('@/api', () => ({ api: apiMock }))

const { default: ConversationView } = await import('./ConversationView.vue')

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })

function feedOf(fixture: Pick<FeedFixture, 'events'>): ConversationFeed {
  const state = createFeedState()
  applyEvents(state, fixture.events)
  return { state: shallowRef(state), revision: ref(1), hydrated: ref(true) }
}

function mountView(fixture: FeedFixture, extra: Record<string, unknown> = {}) {
  return mount(ConversationView, {
    props: { messages: fixture.messages, runs: fixture.runs, feed: feedOf(fixture), ...extra } as never,
    global: { plugins: [i18n] },
    attachTo: document.body,
  })
}

const user = (id: string, content = `user ${id}`, over: Partial<MessageDto> = {}) =>
  ({ id, session_id: 's', role: 'user', content, created_at: '2026-10-13T09:00:00Z', run_id: null, attachments: [], ...over }) as MessageDto

afterEach(() => { document.body.innerHTML = '' })

const flush = async () => {
  for (let i = 0; i < 3; i += 1) { await nextTick(); await Promise.resolve() }
}

describe('ConversationView turns', () => {
  it('keeps the tool evidence checkpoint out of the visible transcript', () => {
    const fixture = simpleAnswer()
    fixture.messages.push({ ...fixture.messages[1]!, id: 'evidence', role: 'tool_evidence', content: 'tool evidence blob' } as MessageDto)
    const wrapper = mountView(fixture)
    expect(wrapper.text()).not.toContain('tool evidence blob')
    wrapper.unmount()
  })

  it('shows tool work and the approval while no answer exists yet', async () => {
    const fixture = pendingApproval()
    const approval = { id: 'p-1', kind: 'permission', tool_id: 'shell', summary: 'shell → git push', status: 'pending', created_at: '', run_id: 'run-1', command: 'git push origin main', cwd: '/w' } as ApprovalDto
    const wrapper = mountView(fixture, { approvals: [approval] })
    const card = wrapper.get('[data-testid="approval-card"]')
    expect(card.text()).toContain('git push origin main')
    expect(card.text()).toContain('/w')
    controller.decideApprovalById.mockClear()
    await card.get('[data-testid="approval-approve"]').trigger('click')
    await flush()
    await card.get('[data-testid="approval-reject"]').trigger('click')
    await flush()
    expect(controller.decideApprovalById.mock.calls).toEqual([['p-1', 'approved'], ['p-1', 'rejected']])
    wrapper.unmount()
  })

  it('still shows an approval Core lists for the run before the feed has seen it', () => {
    const fixture = simpleAnswer()
    fixture.runs[0]!.status = 'awaiting_user'
    const approval = { id: 'late', kind: 'tool', tool_id: 'write_file', summary: 'write_file → a.ts', status: 'pending', created_at: '', run_id: 'run-1', resource_path: 'a.ts' } as ApprovalDto
    const wrapper = mountView(fixture, { approvals: [approval] })
    expect(wrapper.findAll('[data-testid="approval-card"]')).toHaveLength(1)
    expect(wrapper.get('[data-testid="approval-card"]').text()).toContain('a.ts')
    wrapper.unmount()
  })

  it('puts activity on the turn that ran it, not on every assistant answer', async () => {
    const first = simpleAnswer()
    const second = exploreEditShell()
    const fixture: FeedFixture = {
      name: 'two-turns',
      messages: [
        first.messages[0]!, first.messages[1]!,
        user('m-3', 'Fix it', { created_at: '2026-10-13T10:00:00Z' }),
        { ...second.messages[1]!, id: 'm-4', run_id: 'run-2', created_at: '2026-10-13T10:05:00Z' },
      ],
      runs: [first.runs[0]!, { ...second.runs[0]!, id: 'run-2', trigger_message_id: 'm-3', created_at: '2026-10-13T10:00:01Z' }],
      events: [...first.events, ...second.events.map((event) => ({ ...event, run_id: 'run-2' }))],
    }
    const wrapper = mountView(fixture, { workspaceRoot: '/w' })
    const turns = wrapper.findAll('[data-testid="conversation-turn"]')
    expect(turns).toHaveLength(2)
    expect(turns[0]!.findAll('[data-testid="tool-row"]')).toHaveLength(0)
    // A settled turn folds its work into one summary line; opening it shows the rows.
    expect(turns[1]!.get('[data-testid="turn-summary"]').text()).toMatch(/^Worked for .* · 2 files · 1 edit · 1 command$/)
    expect(turns[1]!.find('[data-testid="tool-row"]').exists()).toBe(false)
    await turns[1]!.get('[data-testid="turn-work"] > [data-slot="collapsible-trigger"]').trigger('click')
    await flush()
    expect(turns[1]!.findAll('[data-testid="tool-sentence"]').map((row) => row.text())).toEqual(['Edited src/a.ts', 'Ran npm test'])
    expect(turns[1]!.get('[data-testid="group-label"]').text()).toBe('Explored 2 files · 1 search · 1 folder')
    wrapper.unmount()
  })

  it('renders nothing extra for a run with no activity', () => {
    const fixture: FeedFixture = { name: 'quiet', messages: [user('m-1'), { ...user('m-2', 'Done'), role: 'assistant', run_id: 'run-1' }], runs: [{ id: 'run-1', status: 'completed', trigger_message_id: 'm-1', created_at: '2026-10-13T09:00:01Z', updated_at: '2026-10-13T09:00:02Z' }], events: [] }
    const wrapper = mountView(fixture)
    expect(wrapper.find('.conversation-activity').exists()).toBe(false)
    expect(wrapper.find('[data-testid="approval-card"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="assistant-answer"]').text()).toContain('Done')
    wrapper.unmount()
  })

  it('names the sub-agent that asked for an approval and lifts it to the turn', () => {
    const wrapper = mountView(parallelSubAgents())
    const agents = wrapper.findAll('[data-testid="sub-agent-card"]')
    expect(agents).toHaveLength(2)
    expect(agents[1]!.text()).toContain('Needs approval')
    const card = wrapper.get('[data-testid="approval-card"]')
    expect(card.text()).toContain('global_engineering #1')
    expect(card.element.closest('[data-testid="sub-agent-card"]')).toBeNull()
    wrapper.unmount()
  })

  it('says why a run stopped in words', () => {
    const wrapper = mountView(failedRun())
    const markers = wrapper.findAll('[data-testid="turn-marker"]').map((marker) => marker.text())
    expect(markers).toContain('Stopped: The model provider is unavailable.')
    wrapper.unmount()
  })

  it('streams a reply for a run that has no stored answer yet', async () => {
    const fixture = pendingApproval()
    fixture.events = []
    const wrapper = mountView(fixture, { streamingReplies: { 'run-1': 'Working on it' } })
    await nextTick()
    const streaming = wrapper.get('[data-testid="chat-streaming-reply"]')
    expect(streaming.attributes('aria-busy')).toBe('true')
    wrapper.unmount()
  })
})

describe('ConversationView edit-and-resend', () => {
  const fixture = (): FeedFixture => ({ name: 'edit', messages: [user('u1', 'first try')], runs: [], events: [] })

  it('re-emits an edited user turn so the caller can cut history and resend it', async () => {
    const wrapper = mountView(fixture())
    await wrapper.get(`[aria-label="${en.chat.edit}"]`).trigger('click')
    await wrapper.get('[data-testid="message-edit-input"]').setValue('second try')
    await wrapper.get('[data-testid="message-edit-save"]').trigger('click')
    expect(wrapper.emitted('edit')).toEqual([[{ id: 'u1', content: 'second try' }]])
    wrapper.unmount()
  })

  it('offers no edit affordance for an optimistic row Core has never seen', () => {
    const wrapper = mountView({ name: 'pending', messages: [user('pending-1', 'sending')], runs: [], events: [] })
    expect(wrapper.find(`[aria-label="${en.chat.edit}"]`).exists()).toBe(false)
    expect(wrapper.text()).toContain('Sending…')
    wrapper.unmount()
  })

  it('cuts nothing when the correction is cleared, and Escape cancels', async () => {
    const wrapper = mountView(fixture())
    await wrapper.get(`[aria-label="${en.chat.edit}"]`).trigger('click')
    await wrapper.get('[data-testid="message-edit-input"]').setValue('   ')
    await wrapper.get('[data-testid="message-edit-save"]').trigger('click')
    expect(wrapper.emitted('edit')).toBeUndefined()
    await wrapper.get(`[aria-label="${en.chat.edit}"]`).trigger('click')
    await wrapper.get('[data-testid="message-edit-input"]').trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[data-testid="message-edit-input"]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('ConversationView attachment strip', () => {
  function attachment(over: Partial<MessageAttachmentSummaryDto> = {}): MessageAttachmentSummaryDto {
    return { id: 'att-1', file_name: 'notes.txt', media_type: 'text/plain', content_hash: 'a'.repeat(64), content_length: 2048, created_at: '', bound_at: '', ...over }
  }
  const carrying = (rows: MessageAttachmentSummaryDto[]): FeedFixture => ({ name: 'files', messages: [user('u1', 'see files', { attachments: rows })], runs: [], events: [] })

  it('addresses every row by its id through the gateway content route', () => {
    const wrapper = mountView(carrying([attachment(), attachment({ id: 'att-2', file_name: 'a.bin', media_type: 'application/octet-stream' })]))
    const links = wrapper.findAll('[data-testid="message-attachments"] a')
    expect(links).toHaveLength(2)
    expect(links[0]!.attributes('href')).toBe(apiMock.attachmentContentUrl('att-1'))
    expect(links[0]!.attributes('download')).toBe('notes.txt')
    expect(links[1]!.attributes('href')).toBe(apiMock.attachmentContentUrl('att-2'))
    expect(wrapper.get('[data-testid="message-attachments"]').text()).toContain('2 KB')
    wrapper.unmount()
  })

  it('shows an image as a thumbnail and its link still carries the original', () => {
    const wrapper = mountView(carrying([attachment({ file_name: 'shot.png', media_type: 'image/png' })]))
    const item = wrapper.get('[data-testid="message-attachments"] [data-slot="attachment"]')
    expect(item.get('img').attributes('src')).toBe(apiMock.attachmentContentUrl('att-1'))
    expect(item.get('img').attributes('alt')).toBe('shot.png')
    expect(item.get('a').attributes('href')).toBe(apiMock.attachmentContentUrl('att-1'))
    wrapper.unmount()
  })

  it('will not render svg as an image, because svg can carry script', () => {
    const wrapper = mountView(carrying([attachment({ file_name: 'diagram.svg', media_type: 'image/svg+xml' })]))
    const strip = wrapper.get('[data-testid="message-attachments"]')
    expect(strip.find('img').exists()).toBe(false)
    expect(strip.text()).toContain('diagram.svg')
    wrapper.unmount()
  })

  it('names the strip and shows no storage path for any row', () => {
    const wrapper = mountView(carrying([attachment()]))
    const strip = wrapper.get('[data-testid="message-attachments"]')
    expect(strip.attributes('aria-label')).toBe(en.chat.attachments)
    expect(strip.attributes('role')).toBe('list')
    expect(strip.html()).not.toContain('content_reference')
    expect(strip.html()).not.toContain('DataRoot')
    wrapper.unmount()
  })

  it('renders no strip at all when the message carries nothing', () => {
    const wrapper = mountView({ name: 'none', messages: [user('u1')], runs: [], events: [] })
    expect(wrapper.find('[data-testid="message-attachments"]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('dynamic feed labels', () => {
  const lookup = (bundle: Record<string, unknown>, key: string) =>
    key.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], bundle)

  it('exist in both locales for every marker type and agent status', () => {
    const markers: MarkerType[] = ['compacted', 'interrupted', 'model_retry', 'model_recovered', 'recovered', 'workspace_bound', 'response_fallback', 'run_failed', 'run_cancelled', 'worker_interrupted']
    const statuses = ['waiting', 'running', 'awaiting_approval', 'completed', 'failed', 'blocked', 'cancelled']
    for (const key of [...markers.map((m) => `conversation.marker.${m}`), ...statuses.map((s) => `conversation.agents.status.${s}`)]) {
      expect(typeof lookup(en as Record<string, unknown>, key), `en ${key}`).toBe('string')
      expect(typeof lookup(zh as Record<string, unknown>, key), `zh ${key}`).toBe('string')
    }
  })
})

describe('ConversationView turn states (APP-HOME-108 follow-up)', () => {
  const run = (id: string, status: string, trigger: string, at = '2026-10-13T09:00:01Z') =>
    ({ id, status, trigger_message_id: trigger, created_at: at, updated_at: at })
  const answer = (id: string, content: string, runId: string, at = '2026-10-13T09:00:05Z') =>
    ({ id, session_id: 's', role: 'assistant', content, created_at: at, run_id: runId, attachments: [] }) as MessageDto

  it('puts a failed last turn back in the composer through edit, without resending', async () => {
    const wrapper = mountView(failedRun())
    await wrapper.get('[data-testid="turn-retry"]').trigger('click')
    expect(wrapper.emitted('edit')).toEqual([[{ id: 'm-1', content: 'Anything' }]])
    wrapper.unmount()
  })

  it('offers retry on a stopped last turn, never on an older one or an optimistic row', () => {
    const stopped: FeedFixture = { name: 'stopped', messages: [user('u1', 'first'), user('u2', 'second', { created_at: '2026-10-13T09:01:00Z' })], runs: [run('r1', 'cancelled', 'u1'), run('r2', 'cancelled', 'u2', '2026-10-13T09:01:01Z')], events: [] }
    const wrapper = mountView(stopped)
    const turns = wrapper.findAll('[data-testid="conversation-turn"]')
    expect(turns[0]!.find('[data-testid="turn-retry"]').exists()).toBe(false)
    expect(turns[1]!.find('[data-testid="turn-retry"]').exists()).toBe(true)
    wrapper.unmount()

    const settled = mountView({ ...simpleAnswer() })
    expect(settled.find('[data-testid="turn-retry"]').exists()).toBe(false)
    settled.unmount()
  })

  it('draws a date line where the conversation crosses a day, not above the first turn', () => {
    const fixture: FeedFixture = {
      name: 'days',
      messages: [
        user('u1', 'yesterday', { created_at: new Date(2026, 9, 12, 21).toISOString() }),
        user('u2', 'later that night', { created_at: new Date(2026, 9, 12, 23).toISOString() }),
        user('u3', 'next morning', { created_at: new Date(2026, 9, 13, 9).toISOString() }),
      ],
      runs: [],
      events: [],
    }
    const wrapper = mountView(fixture)
    const lines = wrapper.findAll('[data-testid="day-separator"]')
    expect(lines).toHaveLength(1)
    expect(lines[0]!.attributes('role')).toBe('separator')
    const turns = wrapper.findAll('[data-testid="conversation-turn"]')
    expect(turns[2]!.element.previousElementSibling).toBe(lines[0]!.element)
    wrapper.unmount()
  })

  it('compacts short interim answers and keeps the last answer whole', () => {
    const fixture: FeedFixture = {
      name: 'interim',
      messages: [
        user('u1', 'Fix it'),
        answer('a1', 'Let me check the tests first.', 'r1', '2026-10-13T09:00:03Z'),
        answer('a2', 'Fixed.', 'r1', '2026-10-13T09:00:09Z'),
      ],
      runs: [run('r1', 'completed', 'u1')],
      events: [],
    }
    const wrapper = mountView(fixture)
    const answers = wrapper.findAll('[data-testid="assistant-answer"]')
    expect(answers).toHaveLength(2)
    expect(answers[0]!.classes()).toContain('is-compact')
    expect(answers[0]!.attributes('title')).toBe('Let me check the tests first.')
    expect(answers[1]!.classes()).not.toContain('is-compact')
    wrapper.unmount()
  })

  it('shows a skeleton only when loading outlasts 200ms, and no "ready" placeholder meanwhile', async () => {
    vi.useFakeTimers()
    try {
      const empty: FeedFixture = { name: 'empty', messages: [], runs: [], events: [] }
      const wrapper = mountView(empty, { loading: true })
      expect(wrapper.find('[data-testid="conversation-skeleton"]').exists()).toBe(false)
      expect(wrapper.find('.conversation-empty').exists()).toBe(false)
      await vi.advanceTimersByTimeAsync(200)
      expect(wrapper.find('[data-testid="conversation-skeleton"]').attributes('role')).toBe('status')
      // Real turns replace it at once.
      await wrapper.setProps({ loading: false, messages: simpleAnswer().messages })
      expect(wrapper.find('[data-testid="conversation-skeleton"]').exists()).toBe(false)
      expect(wrapper.findAll('[data-testid="conversation-turn"]')).toHaveLength(1)
      wrapper.unmount()

      // An empty session: the skeleton holds its 500ms minimum, then the placeholder shows.
      const blank = mountView(empty, { loading: true })
      await vi.advanceTimersByTimeAsync(200)
      await blank.setProps({ loading: false })
      await vi.advanceTimersByTimeAsync(100)
      expect(blank.find('[data-testid="conversation-skeleton"]').exists()).toBe(true)
      await vi.advanceTimersByTimeAsync(400)
      expect(blank.find('[data-testid="conversation-skeleton"]').exists()).toBe(false)
      expect(blank.find('.conversation-empty').exists()).toBe(true)
      blank.unmount()
    } finally {
      vi.useRealTimers()
    }
  })
})
