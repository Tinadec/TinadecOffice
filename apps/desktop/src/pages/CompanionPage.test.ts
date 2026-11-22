// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import zh from '@/locales/zh-CN'
import CompanionPage from './CompanionPage.vue'
import type {
  CompanionConversation,
  CompanionMember,
  CompanionMessage,
  CompanionRun,
  CompanionSettings,
  CompanionSummary,
} from '@/lib/companionApi'
import type { CompanionConnectedWorkSession } from '@/lib/companionWorkSession'

const CAPSULE_RIGHT: CompanionState = {
  open: true, mode: 'capsule', expanded: false, side: 'right', capsuleStyle: 'peek', capsuleReveal: 50,
}
const PANEL_RIGHT: CompanionState = {
  open: true, mode: 'panel', expanded: true, side: 'right', capsuleStyle: 'peek', capsuleReveal: 50,
}
const PANEL_LEFT: CompanionState = {
  open: true, mode: 'panel', expanded: true, side: 'left', capsuleStyle: 'peek', capsuleReveal: 50,
}

enableAutoUnmount(afterEach)

let state: CompanionState
let listener: ((next: CompanionState) => void) | undefined
let disposeState: ReturnType<typeof vi.fn>
let companion: {
  api?: ReturnType<typeof vi.fn>
  getState: ReturnType<typeof vi.fn>
  expand: ReturnType<typeof vi.fn>
  collapse: ReturnType<typeof vi.fn>
  moveTo: ReturnType<typeof vi.fn>
  dragMove?: ReturnType<typeof vi.fn>
  dragEnd?: ReturnType<typeof vi.fn>
  openMainSurface?: ReturnType<typeof vi.fn>
  setSide: ReturnType<typeof vi.fn>
  setMode: ReturnType<typeof vi.fn>
  onStateChanged: ReturnType<typeof vi.fn>
}

function broadcast(patch: Partial<CompanionState>): CompanionState {
  state = { ...state, ...patch }
  if (patch.expanded !== undefined && patch.mode === undefined) {
    state.mode = patch.expanded ? 'panel' : 'capsule'
  }
  listener?.({ ...state })
  return { ...state }
}

function create() {
  return mount(CompanionPage, {
    global: {
      plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })],
    },
  })
}

function pointer(target: Element, type: string, init: { screenX: number; screenY: number }): void {
  target.dispatchEvent(new PointerEvent(type, { bubbles: true, cancelable: true, pointerId: 1, button: 0, ...init }))
}

function click(target: Element, detail: number): void {
  target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail }))
}

function doubleClick(target: Element): void {
  target.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true, detail: 2 }))
}

function pressEscape(): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true })
  window.dispatchEvent(event)
  return event
}

class FakeBroadcastChannel {
  static all: FakeBroadcastChannel[] = []
  private readonly listeners = new Set<(event: MessageEvent) => void>()
  constructor(readonly name: string) { FakeBroadcastChannel.all.push(this) }
  addEventListener(type: string, listener: (event: MessageEvent) => void): void {
    if (type === 'message') this.listeners.add(listener)
  }
  removeEventListener(type: string, listener: (event: MessageEvent) => void): void {
    this.listeners.delete(listener)
  }
  postMessage(data: unknown): void {
    for (const channel of FakeBroadcastChannel.all) {
      if (channel === this || channel.name !== this.name) continue
      for (const listener of [...channel.listeners]) listener(new MessageEvent('message', { data }))
    }
  }
  close(): void {
    FakeBroadcastChannel.all = FakeBroadcastChannel.all.filter((entry) => entry !== this)
  }
}

beforeEach(() => {
  state = { ...CAPSULE_RIGHT }
  listener = undefined
  disposeState = vi.fn(() => { listener = undefined })
  companion = {
    getState: vi.fn(async () => ({ ...state })),
    expand: vi.fn(async () => broadcast({ expanded: true })),
    collapse: vi.fn(async () => broadcast({ expanded: false })),
    moveTo: vi.fn(async () => ({ ...state })),
    dragMove: vi.fn(async () => ({ ...state })),
    dragEnd: vi.fn(async (screenX: number) => broadcast({ side: screenX < 500 ? 'left' : 'right' })),
    openMainSurface: vi.fn(async () => true),
    setSide: vi.fn(async (side: 'left' | 'right') => broadcast({ side })),
    setMode: vi.fn(async (mode: 'capsule' | 'panel' | 'immersive') => broadcast({ mode, expanded: mode !== 'capsule' })),
    onStateChanged: vi.fn((callback: (next: CompanionState) => void) => {
      listener = callback
      return disposeState
    }),
  }
  vi.stubGlobal('tinadec', { companion })
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('floating companion capsule & 2D drag', () => {
  it('renders a clean avatar capsule with no idle dot or direction arrows, and expands on click', async () => {
    const wrapper = create()
    await flushPromises()

    const capsule = wrapper.get('[data-testid="companion-capsule"]')
    expect(capsule.attributes('aria-label')).toBe(zh.companion.expand)
    expect(capsule.attributes('aria-expanded')).toBe('false')
    expect(wrapper.find('[data-testid="companion-panel"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="companion-capsule-status"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="companion-capsule-unread"]').exists()).toBe(false)
    expect(wrapper.find('.companion-capsule-chevron').exists()).toBe(false)

    // Pointer click (< 5px displacement) expands once and suppresses trailing click
    pointer(capsule.element, 'pointerdown', { screenX: 900, screenY: 300 })
    pointer(capsule.element, 'pointerup', { screenX: 902, screenY: 301 })
    click(capsule.element, 1)
    await flushPromises()

    expect(companion.expand).toHaveBeenCalledTimes(1)
    expect(companion.dragMove).not.toHaveBeenCalled()
    expect(companion.dragEnd).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="companion-panel"]').exists()).toBe(true)
  })

  it('performs true 2D X+Y dragMove and dragEnd magnetic edge snap without expanding', async () => {
    const wrapper = create()
    await flushPromises()
    const capsule = wrapper.get('[data-testid="companion-capsule"]').element

    pointer(capsule, 'pointerdown', { screenX: 900, screenY: 300 })
    pointer(capsule, 'pointermove', { screenX: 400, screenY: 420 })
    await flushPromises()
    expect(wrapper.classes()).toContain('companion--dragging')
    expect(companion.dragMove).toHaveBeenCalledWith(400, 420)

    pointer(capsule, 'pointerup', { screenX: 120, screenY: 440 })
    // Trailing mouse click after drag must be suppressed
    click(capsule, 1)
    await flushPromises()

    expect(companion.dragEnd).toHaveBeenCalledWith(120, 440)
    expect(companion.expand).not.toHaveBeenCalled()
    expect(wrapper.classes()).toContain('companion--side-left')
    expect(wrapper.classes()).not.toContain('companion--dragging')

    // Keyboard activation (detail === 0) must still expand immediately
    click(capsule, 0)
    await flushPromises()
    expect(companion.expand).toHaveBeenCalledTimes(1)
  })

  it('falls back gracefully to moveTo when dragMove is absent in partial bridge mocks', async () => {
    delete companion.dragMove
    delete companion.dragEnd
    const wrapper = create()
    await flushPromises()
    const capsule = wrapper.get('[data-testid="companion-capsule"]').element

    pointer(capsule, 'pointerdown', { screenX: 900, screenY: 300 })
    pointer(capsule, 'pointermove', { screenX: 902, screenY: 340 })
    pointer(capsule, 'pointerup', { screenX: 902, screenY: 340 })
    await flushPromises()

    expect(companion.moveTo).toHaveBeenCalledWith(40)
    expect(companion.expand).not.toHaveBeenCalled()
  })

  it('reports expand failures instead of faking panel expansion', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
    companion.expand.mockRejectedValue(new Error('window unavailable'))
    const wrapper = create()
    await flushPromises()

    click(wrapper.get('[data-testid="companion-capsule"]').element, 1)
    await flushPromises()

    expect(wrapper.find('[data-testid="companion-panel"]').exists()).toBe(false)
    expect(warn).toHaveBeenCalledWith('[companion] expand failed:', expect.any(Error))
  })

  it('keeps a state broadcast arriving while initial getState is pending', async () => {
    let resolveInitial: ((next: CompanionState) => void) | undefined
    companion.getState.mockReturnValue(new Promise<CompanionState>((resolve) => { resolveInitial = resolve }))
    const wrapper = create()

    broadcast({ expanded: true })
    await flushPromises()
    expect(wrapper.find('[data-testid="companion-panel"]').exists()).toBe(true)

    resolveInitial?.({ ...CAPSULE_RIGHT })
    await flushPromises()
    expect(wrapper.find('[data-testid="companion-panel"]').exists()).toBe(true)
  })
})

describe('decluttered panel chrome & 2D topbar drag', () => {
  it('removes immersive mode, side-flip, native select pickers, and middle pet stage while keeping one collapse button', async () => {
    state = { ...PANEL_RIGHT, mode: 'immersive' }
    const wrapper = create()
    await flushPromises()

    // Legacy immersive snapshot renders cleanly as pure 2-state panel
    expect(wrapper.find('[data-testid="companion-panel"]').exists()).toBe(true)
    expect(wrapper.find('.companion-call').exists()).toBe(false)
    expect(wrapper.find('[data-testid="companion-immersive-toggle"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="companion-stage-expand"]').exists()).toBe(false)
    expect(wrapper.find('select').exists()).toBe(false)
    expect(wrapper.find(`[aria-label="${zh.companion.dockLeft}"]`).exists()).toBe(false)
    expect(wrapper.find(`[aria-label="${zh.companion.dockRight}"]`).exists()).toBe(false)

    // Only one right-hand collapse icon button
    const collapseBtn = wrapper.get('[data-testid="companion-collapse-btn"]')
    expect(collapseBtn.attributes('aria-label')).toBe(zh.companion.collapse)
    await collapseBtn.trigger('click')
    await flushPromises()

    expect(companion.collapse).toHaveBeenCalledTimes(1)
    expect(wrapper.find('[data-testid="companion-capsule"]').exists()).toBe(true)
  })

  it('drags the 40px topbar in 2D and snaps on release', async () => {
    state = { ...PANEL_LEFT }
    const wrapper = create()
    await flushPromises()
    const topbar = wrapper.get('[data-testid="companion-topbar"]').element

    pointer(topbar, 'pointerdown', { screenX: 120, screenY: 200 })
    pointer(topbar, 'pointermove', { screenX: 700, screenY: 280 })
    pointer(topbar, 'pointerup', { screenX: 900, screenY: 310 })
    await flushPromises()

    expect(companion.dragMove).toHaveBeenCalledWith(700, 280)
    expect(companion.dragEnd).toHaveBeenCalledWith(900, 310)
    expect(wrapper.classes()).toContain('companion--side-right')
  })

  it('handles Escape layering (popover first, then panel collapse) and cleans up on unmount', async () => {
    state = { ...PANEL_RIGHT }
    const wrapper = create()
    await flushPromises()

    await wrapper.get('[data-testid="companion-identity-trigger"]').trigger('click')
    expect(wrapper.find('[data-testid="companion-popover"]').exists()).toBe(true)

    const firstEsc = pressEscape()
    await flushPromises()
    expect(firstEsc.defaultPrevented).toBe(true)
    expect(wrapper.find('[data-testid="companion-popover"]').exists()).toBe(false)
    expect(companion.collapse).not.toHaveBeenCalled()

    const secondEsc = pressEscape()
    await flushPromises()
    expect(secondEsc.defaultPrevented).toBe(true)
    expect(companion.collapse).toHaveBeenCalledTimes(1)

    // Escape while collapsed does nothing
    const thirdEsc = pressEscape()
    expect(thirdEsc.defaultPrevented).toBe(false)

    wrapper.unmount()
    expect(disposeState).toHaveBeenCalledTimes(1)
  })
})

// ── Chat, identity popover & #37 work-session fixtures ────────────────

const NOW = '2026-10-28T08:00:00.000Z'

function summaryFixture(id: string, name: string, avatar: string | null = null): CompanionSummary {
  return {
    id,
    name,
    tagline: null,
    avatar,
    model_purpose: 'companion_chat',
    model: null,
    model_provider_id: null,
    status: 'active',
    role: 'autonomous',
    current_activity: 'idle',
    autonomy_enabled: false,
    autonomy_paused: false,
    connection_count: 0,
    skill_count: 0,
    memory_count: 0,
    created_at: NOW,
    updated_at: NOW,
    last_activity_at: null,
    revision: 1,
  }
}

function memberFixture(companionId: string | null, name: string, kind: 'user' | 'companion'): CompanionMember {
  return {
    companion_id: companionId,
    display_name: name,
    joined_at: NOW,
    member_kind: kind,
    status: 'active',
  }
}

function conversationFixture(
  id: string,
  kind: 'private' | 'group',
  members: CompanionMember[],
  overrides: Partial<CompanionConversation> = {},
): CompanionConversation {
  return {
    id,
    kind,
    members,
    message_count: 0,
    autonomy_paused: false,
    turn_budget: 6,
    created_at: NOW,
    title: kind === 'group' ? 'Aria & Bolt' : null,
    parent_conversation_id: null,
    last_message_at: null,
    last_message_preview: null,
    origin: 'user',
    can_open: true,
    ...overrides,
  }
}

function messageFixture(
  id: string,
  conversationId: string,
  seq: number,
  authorKind: CompanionMessage['author_kind'],
  authorName: string,
  body: string,
  authorCompanionId: string | null = null,
): CompanionMessage {
  return {
    id,
    conversation_id: conversationId,
    seq,
    author_kind: authorKind,
    author_name: authorName,
    author_companion_id: authorCompanionId,
    addressed_to: [],
    body,
    created_at: NOW,
  }
}

function runFixture(
  id: string,
  companionId: string,
  conversationId: string,
  overrides: Partial<CompanionRun> = {},
): CompanionRun {
  return {
    id,
    companion_id: companionId,
    conversation_id: conversationId,
    config_revision: 1,
    depth: 0,
    rounds: 0,
    error_code: null,
    error_message: null,
    loaded_memory_ids: [],
    loaded_skill_binding_ids: [],
    reply_message_id: null,
    trigger_message_id: null,
    status: 'running',
    created_at: NOW,
    started_at: NOW,
    finished_at: null,
    ...overrides,
  }
}

function workSessionFixture(overrides: Partial<CompanionConnectedWorkSession> = {}): CompanionConnectedWorkSession {
  return {
    session_id: 'session-work-1',
    title: 'Fix auth middleware',
    project_id: 'proj-1',
    project_name: 'TinadecCore',
    mode_name: 'auto',
    view_mode: 'flat',
    latest_run_id: 'run-work-1',
    latest_run_status: 'awaiting_approval',
    latest_run_summary: 'Waiting to run shell command',
    plan_summary: '2/4 · Run integration tests',
    plan_progress: { done: 2, total: 4, current_step: 'Run integration tests', summary: '2/4 · Run integration tests' },
    pending_approvals: [
      {
        approval_id: 'approval-1',
        tool_id: 'shell_exec',
        summary: 'Run dotnet test',
        risk_level: 'medium',
        created_at: NOW,
      },
    ],
    browser_space_id: null,
    browser_url: null,
    browser_driver: null,
    browser_handoff_reason: null,
    updated_at: NOW,
    latest_run: { id: 'run-work-1', status: 'awaiting_approval', summary: 'Waiting to run shell command' },
    ...overrides,
  }
}

interface ApiCall {
  method: string
  path: string
  body: unknown
}

interface ChatApiOptions {
  companions?: CompanionSummary[]
  settings?: CompanionSettings
  privateConversations?: Record<string, CompanionConversation>
  groups?: CompanionConversation[]
  messages?: Record<string, CompanionMessage[]>
  runs?: Record<string, CompanionRun>
  sendRuns?: CompanionRun[]
  workSessions?: Record<string, CompanionConnectedWorkSession[]>
}

function createChatApi(options: ChatApiOptions = {}) {
  const companions = options.companions ?? [
    summaryFixture('companion-a', 'Aria', 'fox'),
    summaryFixture('companion-b', 'Bolt'),
  ]
  const settings: CompanionSettings = options.settings ?? {
    main_companion_id: companions[0]?.id ?? null,
    max_bot_hop_depth: 3,
    recruit_limit: 3,
    autonomy_paused: false,
    revision: 1,
  }
  const privateConversations = options.privateConversations ?? Object.fromEntries(
    companions.map((item) => [
      item.id,
      conversationFixture(`conv-${item.id}`, 'private', [
        memberFixture(null, 'User', 'user'),
        memberFixture(item.id, item.name, 'companion'),
      ]),
    ]),
  )
  const groups = [...(options.groups ?? [])]
  const messages: Record<string, CompanionMessage[]> = options.messages ?? {}
  const runs: Record<string, CompanionRun> = options.runs ?? {}
  const workSessionsByCompanion: Record<string, CompanionConnectedWorkSession[]> = options.workSessions ?? {}
  const calls: ApiCall[] = []

  function findConversation(id: string): CompanionConversation | undefined {
    return groups.find((group) => group.id === id)
      ?? Object.values(privateConversations).find((conversation) => conversation.id === id)
  }

  function replaceConversation(updated: CompanionConversation): void {
    const index = groups.findIndex((group) => group.id === updated.id)
    if (index >= 0) {
      groups[index] = updated
      return
    }
    for (const key of Object.keys(privateConversations)) {
      if (privateConversations[key]?.id === updated.id) privateConversations[key] = updated
    }
  }

  const api = vi.fn(async (method: string, path: string, body?: unknown) => {
    calls.push({ method, path, body })
    const [url, search] = path.split('?')
    const params = new URLSearchParams(search ?? '')
    const segments = url.split('/').map((part) => decodeURIComponent(part))

    if (method === 'GET' && url === '/api/v1/companion-settings') return { status: 200, body: settings }
    if (method === 'GET' && url === '/api/v1/companions') return { status: 200, body: companions }
    if (method === 'GET' && url === '/api/v1/companions/activity') return { status: 200, body: [] }

    if (segments.length === 6 && segments[3] === 'companions' && segments[5] === 'work-sessions' && method === 'GET') {
      return { status: 200, body: workSessionsByCompanion[segments[4]] ?? [] }
    }

    if (segments.length === 6 && segments[3] === 'approvals' && segments[5] === 'decision' && method === 'POST') {
      return { status: 200, body: { id: segments[4], decision: (body as { decision?: string })?.decision ?? 'approved' } }
    }

    if (segments.length === 6 && segments[3] === 'runs' && segments[5] === 'control' && method === 'POST') {
      return { status: 200, body: { run_id: segments[4], status: 'cancelled', action: 'cancel', accepted: true } }
    }

    if (segments.length === 6 && segments[3] === 'companions' && segments[5] === 'conversations') {
      const companionId = segments[4]
      if (method === 'POST') {
        return { status: 200, body: privateConversations[companionId] }
      }
      const list = [
        privateConversations[companionId],
        ...groups.filter((group) => group.members.some((member) => member.companion_id === companionId)),
      ].filter((conversation): conversation is CompanionConversation => conversation != null)
      return { status: 200, body: list }
    }

    if (segments.length === 6 && segments[3] === 'companion-conversations' && segments[5] === 'messages') {
      const conversationId = segments[4]
      if (method === 'GET') {
        const after = Number(params.get('after_seq') ?? 0)
        return {
          status: 200,
          body: (messages[conversationId] ?? []).filter((message) => message.seq > after),
        }
      }
      const input = body as { body?: string } | undefined
      const seq = (messages[conversationId] ?? []).reduce((max, message) => Math.max(max, message.seq), 0) + 1
      const posted = messageFixture(`msg-${conversationId}-${seq}`, conversationId, seq, 'user', 'User', input?.body ?? '')
      messages[conversationId] = [...(messages[conversationId] ?? []), posted]
      const created = [...(options.sendRuns ?? [])]
      for (const run of created) runs[run.id] = run
      return { status: 200, body: { duplicate: false, message: posted, runs: created, skipped_runs: [] } }
    }

    if (segments.length === 6 && segments[3] === 'companion-conversations' && segments[5] === 'pause' && method === 'POST') {
      const conversation = findConversation(segments[4])
      if (!conversation) return { status: 404, body: { code: 'companion_not_found', message: 'missing' } }
      const updated: CompanionConversation = {
        ...conversation,
        autonomy_paused: (body as { paused?: boolean } | undefined)?.paused === true,
      }
      replaceConversation(updated)
      return { status: 200, body: updated }
    }

    if (segments.length === 6 && segments[3] === 'companion-conversations' && segments[5] === 'members' && method === 'POST') {
      const conversation = findConversation(segments[4])
      if (!conversation) return { status: 404, body: { code: 'companion_not_found', message: 'missing' } }
      const input = body as { companion_id: string; share_history?: boolean }
      const invited = companions.find((item) => item.id === input.companion_id)
      const invitee = memberFixture(input.companion_id, invited?.name ?? input.companion_id, 'companion')
      const group = conversationFixture(`group-${conversation.id}`, 'group', [...conversation.members, invitee], {
        parent_conversation_id: conversation.id,
      })
      groups.push(group)
      messages[group.id] = input.share_history === true ? [...(messages[conversation.id] ?? [])] : []
      return { status: 200, body: group }
    }

    if (segments.length === 5 && segments[3] === 'companion-runs' && method === 'GET') {
      const run = runs[segments[4]]
      if (!run) return { status: 404, body: { code: 'companion_not_found', message: 'run missing' } }
      return { status: 200, body: run }
    }

    return { status: 404, body: { code: 'companion_not_found', message: `unmatched ${path}` } }
  })

  return { api, calls, groups, messages, privateConversations }
}

function mountChat(api: ReturnType<typeof vi.fn>, initialState: CompanionState = PANEL_RIGHT) {
  companion.api = api
  state = { ...initialState }
  return create()
}

function apiCalls(calls: ApiCall[], method: string, suffix: string): ApiCall[] {
  return calls.filter((call) => call.method === method && call.path.split('?')[0].endsWith(suffix))
}

describe('unified top-bar identity popover & companion chat', () => {
  it('switches companions, threads, pauses auto-reply, invites members, and opens Companion Center from the popover', async () => {
    const groupConv = conversationFixture('group-1', 'group', [
      memberFixture(null, 'User', 'user'),
      memberFixture('companion-a', 'Aria', 'companion'),
      memberFixture('companion-b', 'Bolt', 'companion'),
    ], { title: 'Release Squad' })

    const harness = createChatApi({
      groups: [groupConv],
      messages: {
        'conv-companion-a': [messageFixture('a1', 'conv-companion-a', 1, 'user', 'User', 'hi Aria')],
        'group-1': [messageFixture('g1', 'group-1', 1, 'companion', 'Bolt', 'ready for release', 'companion-b')],
        'conv-companion-b': [messageFixture('b1', 'conv-companion-b', 1, 'companion', 'Bolt', 'I am Bolt', 'companion-b')],
      },
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    expect(wrapper.get('.companion-title').text()).toBe('Aria')

    // 1. Open identity popover and switch to group thread
    await wrapper.get('[data-testid="companion-identity-trigger"]').trigger('click')
    await wrapper.get('[data-testid="companion-menu-thread-group-1"]').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('.companion-msg-body').map((n) => n.text())).toEqual(['ready for release'])

    // 2. Open popover -> Pause auto-replies
    await wrapper.get('[data-testid="companion-identity-trigger"]').trigger('click')
    await wrapper.get('[data-testid="companion-menu-pause"]').trigger('click')
    await flushPromises()
    expect(apiCalls(harness.calls, 'POST', '/pause')).toHaveLength(1)
    expect(wrapper.get('.companion-run--paused').text()).toBe(zh.companion.chat.pausedBadge)

    // 3. Open popover -> Open Companion Center
    await wrapper.get('[data-testid="companion-identity-trigger"]').trigger('click')
    await wrapper.get('[data-testid="companion-menu-open-center"]').trigger('click')
    await flushPromises()
    expect(companion.openMainSurface).toHaveBeenCalledWith({
      target: 'companion-center',
      companionId: 'companion-a',
    })

    // 4. Switch companion via popover -> loads Bolt's private chat
    await wrapper.get('[data-testid="companion-identity-trigger"]').trigger('click')
    await wrapper.get('[data-testid="companion-menu-pick-companion-b"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('.companion-title').text()).toBe('Bolt')
    expect(wrapper.findAll('.companion-msg-author').map((n) => n.text())).toEqual(['Bolt'])

    // 5. Invite from popover defaults share_history=false
    await wrapper.get('[data-testid="companion-identity-trigger"]').trigger('click')
    await wrapper.get('[data-testid="companion-menu-invite"]').trigger('click')
    const dialog = wrapper.get('.companion-invite')
    expect((dialog.get('.companion-invite-share input').element as HTMLInputElement).checked).toBe(false)
    await dialog.get('.companion-primary').trigger('click')
    await flushPromises()
    const invite = apiCalls(harness.calls, 'POST', '/members')[0]
    expect((invite.body as { companion_id: string; share_history: boolean })).toEqual({
      companion_id: 'companion-a',
      share_history: false,
    })
  })

  it('sends messages, keeps drafts across collapse/expand, and surfaces real run failures', async () => {
    const modelFailure = runFixture('run-model', 'companion-a', 'conv-companion-a', {
      status: 'failed',
      error_code: 'companion_model_unavailable',
      error_message: 'no chat route',
      finished_at: NOW,
    })
    const harness = createChatApi({
      messages: { 'conv-companion-a': [] },
      sendRuns: [modelFailure],
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    await wrapper.get('.companion-input').setValue('hello companion')
    await wrapper.get('.companion-composer').trigger('submit')
    await flushPromises()
    expect(wrapper.get('.companion-run--failed').text()).toBe(zh.companion.chat.modelUnavailable)
    expect(wrapper.findAll('.companion-msg--user')).toHaveLength(1)

    await wrapper.get('.companion-input').setValue('draft stays')
    broadcast({ expanded: false })
    await flushPromises()
    expect(wrapper.find('[data-testid="companion-capsule"]').exists()).toBe(true)
    // Failed status dot appears on capsule
    expect(wrapper.get('[data-testid="companion-capsule-status"]').classes()).toContain('is-failed')

    broadcast({ expanded: true })
    await flushPromises()
    expect((wrapper.get('.companion-input').element as HTMLTextAreaElement).value).toBe('draft stays')
  })

  it('shows empty state when no active companions exist and streams live deltas without polling', async () => {
    const emptyHarness = createChatApi({
      companions: [],
      settings: { main_companion_id: null, max_bot_hop_depth: 3, recruit_limit: 3, autonomy_paused: false, revision: 1 },
    })
    const emptyWrapper = mountChat(emptyHarness.api)
    await flushPromises()
    expect(emptyWrapper.get('.companion-chat-state-title').text()).toBe(zh.companion.chat.noCompanionsTitle)
    expect(emptyWrapper.find('.companion-input').exists()).toBe(false)
    emptyWrapper.unmount()

    vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel)
    vi.stubGlobal('EventSource', class {})
    FakeBroadcastChannel.all = []
    vi.useFakeTimers()

    const harness = createChatApi({
      messages: { 'conv-companion-a': [] },
      sendRuns: [runFixture('run-live', 'companion-a', 'conv-companion-a')],
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    const mainWindow = new BroadcastChannel('tinadec.companion-events')
    mainWindow.postMessage({
      type: 'notice',
      notice: {
        type: 'companion.delta', seq: 0, kind: null,
        companionId: 'companion-a', conversationId: 'conv-companion-a', runId: 'run-live',
        delta: 'live chunk',
      },
    })
    await flushPromises()
    expect(wrapper.get('.companion-streaming-bubble').text()).toContain('live chunk')

    // Zero polling timers run in background
    const beforeWait = harness.calls.length
    await vi.advanceTimersByTimeAsync(1500)
    await flushPromises()
    expect(harness.calls.length).toBe(beforeWait)
  })

  it('pats on blank space double click and ignores messages', async () => {
    const harness = createChatApi({
      messages: { 'conv-companion-a': [messageFixture('m1', 'conv-companion-a', 1, 'user', 'User', 'hello')] },
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    doubleClick(wrapper.get('.companion-msg-body').element)
    await flushPromises()
    expect(wrapper.find('.companion-pat-note').exists()).toBe(false)

    doubleClick(wrapper.get('.companion-chat').element)
    await flushPromises()
    expect(wrapper.get('.companion-pat-note').text()).toBe(zh.companion.pat)
  })
})

describe('#37 work-session approval / cancel / jump strip & capsule status/unread indicators', () => {
  it('hydrates work sessions, approves or rejects inline, cancels active run, and jumps to main workbench', async () => {
    const ws = workSessionFixture()
    const harness = createChatApi({
      messages: { 'conv-companion-a': [] },
      workSessions: { 'companion-a': [ws] },
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    const strip = wrapper.get('[data-testid="companion-work-session-strip"]')
    expect(strip.text()).toContain('Fix auth middleware')
    expect(strip.text()).toContain('shell_exec')
    expect(strip.text()).toContain('Run dotnet test')

    // Jump to session in main workbench
    await wrapper.get('[data-testid="companion-ws-open"]').trigger('click')
    expect(companion.openMainSurface).toHaveBeenCalledWith({
      target: 'session',
      sessionId: 'session-work-1',
    })

    // Approve inline
    await wrapper.get('[data-testid="companion-ws-approve"]').trigger('click')
    await flushPromises()
    const decisionCall = apiCalls(harness.calls, 'POST', '/api/v1/approvals/approval-1/decision')[0]
    expect(decisionCall).toBeDefined()
    expect(decisionCall.body).toEqual({ decision: 'approved' })

    // Now running -> Stop/Cancel button invokes /runs/${id}/control
    await wrapper.get('[data-testid="companion-ws-cancel"]').trigger('click')
    await flushPromises()
    const cancelCall = apiCalls(harness.calls, 'POST', '/api/v1/runs/run-work-1/control')[0]
    expect(cancelCall).toBeDefined()
    expect((cancelCall.body as { action: string }).action).toBe('cancel')
    // Once cancelled, the active strip hides cleanly
    expect(wrapper.find('[data-testid="companion-work-session-strip"]').exists()).toBe(false)
  })

  it('rejects pending approval inline via POST /approvals/{id}/decision', async () => {
    const ws = workSessionFixture()
    const harness = createChatApi({
      messages: { 'conv-companion-a': [] },
      workSessions: { 'companion-a': [ws] },
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    await wrapper.get('[data-testid="companion-ws-reject"]').trigger('click')
    await flushPromises()
    const decisionCall = apiCalls(harness.calls, 'POST', '/api/v1/approvals/approval-1/decision')[0]
    expect(decisionCall.body).toEqual({ decision: 'rejected' })
  })

  it('updates work-session strip live via BroadcastChannel and shows capsule alert dot + unread badge when collapsed', async () => {
    vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel)
    vi.stubGlobal('EventSource', class {})
    FakeBroadcastChannel.all = []
    vi.useFakeTimers()

    const harness = createChatApi({
      messages: { 'conv-companion-a': [] },
    })
    const wrapper = mountChat(harness.api, CAPSULE_RIGHT)
    await flushPromises()

    // Idle capsule has no status dot and no unread badge
    expect(wrapper.find('[data-testid="companion-capsule-status"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="companion-capsule-unread"]').exists()).toBe(false)

    const mainWindow = new BroadcastChannel('tinadec.companion-events')
    // Live work-session notice enters awaiting_approval -> capsule shows alert dot
    mainWindow.postMessage({
      type: 'notice',
      notice: {
        type: 'companion.work_session',
        seq: 1,
        kind: 'approval_required',
        companionId: 'companion-a',
        conversationId: null,
        runId: 'run-live-ws',
        workSession: {
          sessionId: 'sess-live-1',
          sessionTitle: 'Deploy pipeline',
          workspaceId: null,
          projectId: null,
          projectName: null,
          runId: 'run-live-ws',
          status: 'awaiting_approval',
          modeName: 'auto',
          viewMode: 'flat',
          summary: 'Needs confirmation',
          planSummary: null,
          pendingApprovals: [
            { approval_id: 'appr-live', tool_id: 'git_push', summary: 'Push branch', risk_level: 'high', created_at: NOW },
          ],
          browserSpaceId: null,
          browserUrl: null,
          browserDriver: null,
          browserHandoffReason: null,
          updatedAt: NOW,
        },
      },
    })
    await flushPromises()

    expect(wrapper.get('[data-testid="companion-capsule-status"]').classes()).toContain('is-awaiting_approval')

    // Companion reply arrives while collapsed -> unread badge increments
    harness.messages['conv-companion-a'] = [
      messageFixture('m-unread', 'conv-companion-a', 1, 'companion', 'Aria', 'Need your approval!', 'companion-a'),
    ]
    mainWindow.postMessage({
      type: 'notice',
      notice: {
        type: 'companion.activity',
        seq: 2,
        kind: 'message_sent',
        companionId: 'companion-a',
        conversationId: 'conv-companion-a',
        runId: null,
      },
    })
    await vi.advanceTimersByTimeAsync(150)
    await flushPromises()

    expect(wrapper.get('[data-testid="companion-capsule-unread"]').text()).toBe('1')

    // Expanding clears unread count and surfaces the live work-session strip
    broadcast({ expanded: true })
    await flushPromises()
    expect(wrapper.find('[data-testid="companion-work-session-strip"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="companion-work-session-strip"]').text()).toContain('Deploy pipeline')
    expect(wrapper.get('[data-testid="companion-work-session-strip"]').text()).toContain('git_push')

    // Collapsing again keeps unread cleared (0)
    broadcast({ expanded: false })
    await flushPromises()
    expect(wrapper.find('[data-testid="companion-capsule-unread"]').exists()).toBe(false)
  })
})
