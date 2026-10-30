// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import zh from '@/locales/zh-CN'
import CompanionPage from './CompanionPage.vue'
import CompanionStage from '@/components/pets/CompanionStage.vue'
import type {
  CompanionConversation,
  CompanionMember,
  CompanionMessage,
  CompanionRun,
  CompanionSettings,
  CompanionSummary,
} from '@/lib/companionApi'

/**
 * The window renders what the main process reports and sends gestures; it never flips `expanded`
 * itself. Every fake command therefore broadcasts the next snapshot back through `onStateChanged`
 * like the real main process does, and a page that mutated local state would still look right in
 * these tests only if the mirror agreed — which is what the broadcast-only fakes rule out.
 */

const CAPSULE_RIGHT: CompanionState = {
  open: true, mode: 'capsule', expanded: false, side: 'right', capsuleStyle: 'peek', capsuleReveal: 50,
}
const PANEL_RIGHT: CompanionState = {
  open: true, mode: 'panel', expanded: true, side: 'right', capsuleStyle: 'peek', capsuleReveal: 50,
}
const PANEL_LEFT: CompanionState = {
  open: true, mode: 'panel', expanded: true, side: 'left', capsuleStyle: 'peek', capsuleReveal: 50,
}
const IMMERSIVE_RIGHT: CompanionState = {
  open: true, mode: 'immersive', expanded: true, side: 'right', capsuleStyle: 'peek', capsuleReveal: 50,
}

// Every page answers Escape on `window`; a wrapper left mounted keeps collapsing in later tests.
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
  setSide: ReturnType<typeof vi.fn>
  setMode: ReturnType<typeof vi.fn>
  onStateChanged: ReturnType<typeof vi.fn>
}

function broadcast(patch: Partial<CompanionState>): CompanionState {
  state = { ...state, ...patch }
  // `mode` and `expanded` are two views of one fact. A fake that only moved one of them would let
  // the page read the other and still look right, which is exactly the bug these tests exist for.
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

/** A real double click bubbles, exactly like the one the gesture handler has to filter. */
function doubleClick(target: Element): void {
  target.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true, detail: 2 }))
}

function pressEscape(): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true })
  window.dispatchEvent(event)
  return event
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
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('companion capsule', () => {
  it('renders the stored shape and expands on a mouse click', async () => {
    const wrapper = create()
    await flushPromises()

    const capsule = wrapper.get('.companion-capsule')
    expect(capsule.attributes('aria-label')).toBe(zh.companion.expand)
    expect(capsule.attributes('aria-expanded')).toBe('false')
    expect(wrapper.find('.companion-panel').exists()).toBe(false)
    expect(wrapper.classes()).toContain('companion--side-right')

    click(capsule.element, 1)
    await flushPromises()

    expect(companion.expand).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.companion-capsule').exists()).toBe(false)
    expect(wrapper.get('[role="region"]').attributes('aria-label')).toBe(zh.companion.title)
  })

  it('reports a command failure instead of pretending the panel opened', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
    companion.expand.mockRejectedValue(new Error('window unavailable'))
    const wrapper = create()
    await flushPromises()

    click(wrapper.get('.companion-capsule').element, 1)
    await flushPromises()

    expect(wrapper.find('.companion-panel').exists()).toBe(false)
    expect(warn).toHaveBeenCalledWith('[companion] expand failed:', expect.any(Error))
  })

  it('keeps a broadcast that lands while the initial read is still pending', async () => {
    let resolveInitial: ((next: CompanionState) => void) | undefined
    companion.getState.mockReturnValue(new Promise<CompanionState>((resolve) => { resolveInitial = resolve }))
    const wrapper = create()

    // The subscription is installed before the read, so the main process's later answer wins.
    broadcast({ expanded: true })
    await flushPromises()
    expect(wrapper.find('.companion-panel').exists()).toBe(true)

    resolveInitial?.({ ...CAPSULE_RIGHT })
    await flushPromises()
    expect(wrapper.find('.companion-panel').exists()).toBe(true)
  })
})

describe('panel controls', () => {
  it('collapses only through the main process, which reports the capsule back', async () => {
    state = { ...PANEL_RIGHT }
    const wrapper = create()
    await flushPromises()

    await wrapper.get(`[aria-label="${zh.companion.collapse}"]`).trigger('click')
    await flushPromises()

    expect(companion.collapse).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.companion-panel').exists()).toBe(false)
    expect(wrapper.find('.companion-capsule').exists()).toBe(true)
  })

  it('offers the opposite edge and follows the new side', async () => {
    state = { ...PANEL_RIGHT }
    const wrapper = create()
    await flushPromises()

    await wrapper.get(`[aria-label="${zh.companion.dockLeft}"]`).trigger('click')
    await flushPromises()
    expect(companion.setSide).toHaveBeenCalledWith('left')
    expect(wrapper.classes()).toContain('companion--side-left')
    expect(wrapper.find(`[aria-label="${zh.companion.dockRight}"]`).exists()).toBe(true)

    await wrapper.get(`[aria-label="${zh.companion.dockRight}"]`).trigger('click')
    await flushPromises()
    expect(companion.setSide).toHaveBeenLastCalledWith('right')
    expect(wrapper.classes()).toContain('companion--side-right')
  })

  it('collapses on Escape only while the panel is open', async () => {
    const wrapper = create()
    await flushPromises()

    const collapsed = pressEscape()
    await flushPromises()
    expect(companion.collapse).not.toHaveBeenCalled()
    expect(collapsed.defaultPrevented).toBe(false)

    broadcast({ expanded: true })
    await flushPromises()
    const expanded = pressEscape()
    await flushPromises()
    expect(companion.collapse).toHaveBeenCalledTimes(1)
    expect(expanded.defaultPrevented).toBe(true)
  })
})

describe('capsule gestures', () => {
  it('expands when a right-docked capsule is dragged inward far enough', async () => {
    const wrapper = create()
    await flushPromises()
    const capsule = wrapper.get('.companion-capsule').element

    pointer(capsule, 'pointerdown', { screenX: 900, screenY: 300 })
    pointer(capsule, 'pointermove', { screenX: 880, screenY: 300 })
    pointer(capsule, 'pointerup', { screenX: 870, screenY: 300 })
    await flushPromises()

    expect(companion.expand).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.companion-panel').exists()).toBe(true)
  })

  it('expands inward on a left-docked capsule too', async () => {
    state = { ...CAPSULE_RIGHT, side: 'left' }
    const wrapper = create()
    await flushPromises()
    const capsule = wrapper.get('.companion-capsule').element

    pointer(capsule, 'pointerdown', { screenX: 200, screenY: 400 })
    pointer(capsule, 'pointermove', { screenX: 215, screenY: 400 })
    pointer(capsule, 'pointerup', { screenX: 230, screenY: 400 })
    await flushPromises()

    expect(companion.expand).toHaveBeenCalledTimes(1)
  })

  it('does not expand on a short drag and suppresses the click that follows it', async () => {
    const wrapper = create()
    await flushPromises()
    const capsule = wrapper.get('.companion-capsule').element

    pointer(capsule, 'pointerdown', { screenX: 900, screenY: 300 })
    pointer(capsule, 'pointermove', { screenX: 890, screenY: 300 })
    pointer(capsule, 'pointerup', { screenX: 885, screenY: 300 })
    await flushPromises()
    expect(companion.expand).not.toHaveBeenCalled()

    // A drag ends on the button and therefore still produces a click; that click is not a request.
    click(capsule, 1)
    await flushPromises()
    expect(companion.expand).not.toHaveBeenCalled()

    // Keyboard activation reports detail 0 and must always open, even inside the suppression window.
    click(capsule, 0)
    await flushPromises()
    expect(companion.expand).toHaveBeenCalledTimes(1)
  })

  it('drags the window vertically without expanding it', async () => {
    const wrapper = create()
    await flushPromises()
    const capsule = wrapper.get('.companion-capsule').element

    pointer(capsule, 'pointerdown', { screenX: 900, screenY: 300 })
    await flushPromises()
    expect(wrapper.classes()).toContain('companion--dragging')

    pointer(capsule, 'pointermove', { screenX: 901, screenY: 340 })
    pointer(capsule, 'pointerup', { screenX: 901, screenY: 340 })
    await flushPromises()

    // happy-dom reports window.screenY as 0, so the target top is the gesture delta itself.
    expect(companion.moveTo).toHaveBeenCalledWith(40)
    expect(companion.expand).not.toHaveBeenCalled()
    expect(wrapper.classes()).not.toContain('companion--dragging')
  })
})

describe('panel drag and teardown', () => {
  it('moves the panel by the vertical drag delta', async () => {
    state = { ...PANEL_LEFT }
    const wrapper = create()
    await flushPromises()
    const panel = wrapper.get('.companion-panel').element

    pointer(panel, 'pointerdown', { screenX: 100, screenY: 200 })
    pointer(panel, 'pointermove', { screenX: 140, screenY: 260 })
    pointer(panel, 'pointerup', { screenX: 140, screenY: 260 })
    await flushPromises()

    expect(companion.moveTo).toHaveBeenCalledWith(60)
    expect(companion.setSide).not.toHaveBeenCalled()
  })

  it('drops the state listener and the Escape binding on unmount', async () => {
    state = { ...PANEL_RIGHT }
    const wrapper = create()
    await flushPromises()

    wrapper.unmount()
    expect(disposeState).toHaveBeenCalledTimes(1)

    const event = pressEscape()
    await flushPromises()
    expect(companion.collapse).not.toHaveBeenCalled()
    expect(event.defaultPrevented).toBe(false)
  })
})

// ── Chat (Task 6) ─────────────────────────────────────────────────────
// The companion window reaches the companion domain only through the main-process proxy, so these
// fakes stand in for `window.tinadec.companion.api` (the bridge's transport) and never fetch.

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
    // A private chat the user started, and openable: the two facts the centre's list now carries.
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
  /** Runs the fake Core creates for every send, so polling has something to converge on. */
  sendRuns?: CompanionRun[]
}

/** A stand-in for the main-process whitelist proxy: routes mirror the real companion endpoints. */
function createChatApi(options: ChatApiOptions = {}) {
  const companions = options.companions ?? [
    summaryFixture('companion-a', 'Aria'),
    summaryFixture('companion-b', 'Bolt'),
  ]
  const settings: CompanionSettings = options.settings ?? {
    main_companion_id: companions[0]?.id ?? null,
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

    if (segments.length === 6 && segments[3] === 'companions' && segments[5] === 'conversations') {
      const companionId = segments[4]
      if (method === 'POST') {
        const input = body as { kind?: string } | undefined
        if (input?.kind === 'group') {
          const companion = companions.find((item) => item.id === companionId)
          const group = conversationFixture(`group-${companionId}`, 'group', [
            memberFixture(null, 'User', 'user'),
            memberFixture(companionId, companion?.name ?? companionId, 'companion'),
          ])
          groups.push(group)
          messages[group.id] = []
          return { status: 200, body: group }
        }
        return { status: 200, body: privateConversations[companionId] }
      }
      const list = [
        privateConversations[companionId],
        ...groups.filter((group) => group.members.some((member) => member.companion_id === companionId)),
      ].filter((conversation): conversation is CompanionConversation => conversation != null)
      return { status: 200, body: list }
    }

    if (segments.length === 5 && segments[3] === 'companion-conversations' && method === 'GET') {
      return { status: 200, body: findConversation(segments[4]) }
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
      if (conversation.kind === 'group') {
        const updated: CompanionConversation = { ...conversation, members: [...conversation.members, invitee] }
        replaceConversation(updated)
        return { status: 200, body: updated }
      }
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

function mountChat(api: ReturnType<typeof vi.fn>) {
  companion.api = api
  state = { ...PANEL_RIGHT }
  return create()
}

function apiCalls(calls: ApiCall[], method: string, suffix: string): ApiCall[] {
  return calls.filter((call) => call.method === method && call.path.split('?')[0].endsWith(suffix))
}

describe('companion chat', () => {
  it('shows the empty state when no active companion exists', async () => {
    const harness = createChatApi({
      companions: [],
      settings: { main_companion_id: null, autonomy_paused: false, revision: 1 },
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    expect(wrapper.get('.companion-chat-state-title').text()).toBe(zh.companion.chat.noCompanionsTitle)
    expect(wrapper.get('.companion-chat-state-body').text()).toBe(zh.companion.chat.noCompanionsBody)
    expect(wrapper.find('.companion-input').exists()).toBe(false)
    expect(harness.calls.some((call) => call.path.includes('/conversations'))).toBe(false)
  })

  it('loads the main companion private chat and renders real author names', async () => {
    const harness = createChatApi({
      messages: {
        'conv-companion-a': [
          messageFixture('m1', 'conv-companion-a', 1, 'user', 'User', 'hello'),
          messageFixture('m2', 'conv-companion-a', 2, 'companion', 'Aria', 'hi there', 'companion-a'),
          messageFixture('m3', 'conv-companion-a', 3, 'system', 'System', 'joined'),
        ],
      },
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    expect(wrapper.get('.companion-title').text()).toBe('Aria')
    expect(wrapper.findAll('.companion-msg-author').map((node) => node.text()))
      .toEqual([zh.companion.chat.you, 'Aria', zh.companion.chat.system])
    expect(apiCalls(harness.calls, 'POST', '/api/v1/companions/companion-a/conversations')).toHaveLength(1)
  })

  it('refuses empty text and sends a non-empty message, clearing the draft', async () => {
    const harness = createChatApi({ messages: { 'conv-companion-a': [] } })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    const input = wrapper.get('.companion-input')
    await input.setValue('   ')
    await wrapper.get('.companion-composer').trigger('submit')
    await flushPromises()
    expect(apiCalls(harness.calls, 'POST', '/messages')).toHaveLength(0)

    await input.setValue('hello companion')
    await wrapper.get('.companion-composer').trigger('submit')
    await flushPromises()

    const sends = apiCalls(harness.calls, 'POST', '/messages')
    expect(sends).toHaveLength(1)
    expect((sends[0].body as { body: string }).body).toBe('hello companion')
    expect((wrapper.get('.companion-input').element as HTMLInputElement).value).toBe('')
    expect(wrapper.findAll('.companion-msg--user')).toHaveLength(1)
  })

  it('shows the real run failure and never a fabricated companion reply', async () => {
    const modelFailure = runFixture('run-model', 'companion-a', 'conv-companion-a', {
      status: 'failed',
      error_code: 'companion_model_unavailable',
      error_message: 'no chat route',
      finished_at: NOW,
    })
    const budgetFailure = runFixture('run-budget', 'companion-a', 'conv-companion-a', {
      status: 'failed',
      error_code: 'companion_turn_budget_exhausted',
      error_message: 'budget spent',
      finished_at: NOW,
    })
    const harness = createChatApi({
      messages: { 'conv-companion-a': [] },
      sendRuns: [modelFailure, budgetFailure],
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    await wrapper.get('.companion-input').setValue('hello')
    await wrapper.get('.companion-composer').trigger('submit')
    await flushPromises()
    await flushPromises()

    const failures = wrapper.findAll('.companion-run--failed').map((node) => node.text())
    expect(failures).toContain(zh.companion.chat.modelUnavailable)
    expect(failures).toContain(
      zh.companion.chat.runFailed
        .replace('{code}', 'companion_turn_budget_exhausted')
        .replace('{message}', 'budget spent'),
    )
    expect(wrapper.findAll('.companion-msg--companion')).toHaveLength(0)
  })

  it('defaults the invite dialog to not sharing history', async () => {
    const harness = createChatApi({ messages: { 'conv-companion-a': [] } })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    await wrapper.get(`[aria-label="${zh.companion.chat.invite}"]`).trigger('click')
    const dialog = wrapper.get('.companion-invite')
    expect((dialog.get('.companion-invite-share input').element as HTMLInputElement).checked).toBe(false)

    await dialog.get('.companion-primary').trigger('click')
    await flushPromises()

    const invite = apiCalls(harness.calls, 'POST', '/members')[0]
    expect((invite.body as { companion_id: string }).companion_id).toBe('companion-b')
    expect((invite.body as { share_history: boolean }).share_history).toBe(false)
  })

  it('sends share_history=true only when the checkbox is ticked', async () => {
    const harness = createChatApi({ messages: { 'conv-companion-a': [] } })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    await wrapper.get(`[aria-label="${zh.companion.chat.invite}"]`).trigger('click')
    const dialog = wrapper.get('.companion-invite')
    const share = dialog.get('.companion-invite-share input')
    await share.setValue(true)
    expect((share.element as HTMLInputElement).checked).toBe(true)

    await dialog.get('.companion-primary').trigger('click')
    await flushPromises()

    const invite = apiCalls(harness.calls, 'POST', '/members')[0]
    expect((invite.body as { share_history: boolean }).share_history).toBe(true)
    // The returned conversation is the new group, so the header now describes it and its members.
    expect(wrapper.get('.companion-title').text()).toBe('Aria')
    expect(wrapper.get('.companion-subtitle').text()).toContain('Aria & Bolt')
  })

  it('pauses the current conversation from the header menu and flips the action', async () => {
    const harness = createChatApi({ messages: { 'conv-companion-a': [] } })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    await wrapper.get(`[aria-label="${zh.companion.chat.options}"]`).trigger('click')
    const item = wrapper.get('.companion-menu-item')
    expect(item.text()).toBe(zh.companion.chat.pause)

    await item.trigger('click')
    await flushPromises()

    const pause = apiCalls(harness.calls, 'POST', '/pause')[0]
    expect((pause.body as { paused: boolean }).paused).toBe(true)
    expect(wrapper.get('.companion-menu-item').text()).toBe(zh.companion.chat.resume)
    expect(wrapper.get('.companion-run--paused').text()).toBe(zh.companion.chat.pausedBadge)
  })

  it('switches to another companion and loads that companion private chat', async () => {
    const harness = createChatApi({
      messages: {
        'conv-companion-a': [messageFixture('a1', 'conv-companion-a', 1, 'user', 'User', 'to Aria')],
        'conv-companion-b': [
          messageFixture('b1', 'conv-companion-b', 1, 'companion', 'Bolt', 'I am Bolt', 'companion-b'),
        ],
      },
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    await wrapper.get('.companion-companion-select').setValue('companion-b')
    await flushPromises()

    expect(apiCalls(harness.calls, 'POST', '/api/v1/companions/companion-b/conversations')).toHaveLength(1)
    expect(wrapper.get('.companion-title').text()).toBe('Bolt')
    expect(wrapper.findAll('.companion-msg-author').map((node) => node.text())).toEqual(['Bolt'])
  })

  it('keeps loaded messages and the draft across collapse, expand and resize', async () => {
    const harness = createChatApi({
      messages: { 'conv-companion-a': [messageFixture('m1', 'conv-companion-a', 1, 'user', 'User', 'hello')] },
    })
    const wrapper = mountChat(harness.api)
    await flushPromises()

    await wrapper.get('.companion-input').setValue('unfinished draft')
    const loaded = wrapper.findAll('.companion-msg').length
    expect(loaded).toBe(1)

    broadcast({ expanded: false })
    await flushPromises()
    expect(wrapper.find('.companion-capsule').exists()).toBe(true)

    broadcast({ expanded: true })
    await flushPromises()
    expect((wrapper.get('.companion-input').element as HTMLInputElement).value).toBe('unfinished draft')
    expect(wrapper.findAll('.companion-msg')).toHaveLength(loaded)

    window.dispatchEvent(new Event('resize'))
    await flushPromises()
    expect((wrapper.get('.companion-input').element as HTMLInputElement).value).toBe('unfinished draft')
  })
})

describe('companion capsule face', () => {
  /** The capsule shape, not the panel: `mountChat` opens the panel because chat lives there. */
  function mountCapsule(api: ReturnType<typeof vi.fn>) {
    companion.api = api
    state = { ...CAPSULE_RIGHT }
    return create()
  }

  it('wears the selected companion as the capsule instead of a grip', async () => {
    const harness = createChatApi({ companions: [summaryFixture('companion-a', 'Aria', 'fox')] })
    const wrapper = mountCapsule(harness.api)
    await flushPromises()

    // The peek style draws the pet's head, animated frame by frame, and no handle.
    expect(wrapper.get('.companion-capsule').classes()).toContain('companion-capsule--peek')
    expect(wrapper.find('[data-testid="companion-stage"]').exists()).toBe(true)
    expect(wrapper.find('.companion-capsule-grip').exists()).toBe(false)
    expect(wrapper.find('.companion-capsule-glyph').exists()).toBe(false)
  })

  it('draws a bare handle when the grip style is chosen', async () => {
    const harness = createChatApi({ companions: [summaryFixture('companion-a', 'Aria', 'fox')] })
    companion.api = harness.api
    state = { ...CAPSULE_RIGHT, capsuleStyle: 'grip' }
    const wrapper = create()
    await flushPromises()

    // A grip is not a window onto the pet: no sprite at all, just the handle and the mood dot.
    expect(wrapper.get('.companion-capsule').classes()).toContain('companion-capsule--grip')
    expect(wrapper.find('[data-testid="companion-stage"]').exists()).toBe(false)
    expect(wrapper.find('.companion-capsule-grip').exists()).toBe(true)
    expect(wrapper.get('.companion-capsule-status').classes()).toContain('is-idle')
  })

  it('falls back to a glyph when the companion has no pet, and stays idle', async () => {
    const harness = createChatApi({ companions: [summaryFixture('companion-a', 'Aria')] })
    const wrapper = mountCapsule(harness.api)
    await flushPromises()

    expect(wrapper.find('.companion-capsule-pet').exists()).toBe(false)
    expect(wrapper.find('.companion-capsule-glyph').exists()).toBe(true)
    // The capsule only claims states this window can observe; with nothing running it is idle.
    expect(wrapper.get('.companion-capsule-status').classes()).toContain('is-idle')
    expect(wrapper.get('.companion-capsule-status').classes()).not.toContain('is-failed')
  })
})

describe('immersive shape', () => {
  it('asks the main process for the immersive column and renders whatever shape it reports', async () => {
    const harness = createChatApi({ messages: { 'conv-companion-a': [] } })
    companion.api = harness.api
    state = { ...PANEL_RIGHT }
    const wrapper = create()
    await flushPromises()

    await wrapper.get('[data-testid="companion-immersive-toggle"]').trigger('click')
    await flushPromises()

    expect(companion.setMode).toHaveBeenCalledWith('immersive')
    const panel = wrapper.get('.companion-panel')
    expect(panel.classes()).toContain('companion-panel--immersive')
    expect(wrapper.get('[data-testid="companion-immersive-toggle"]').attributes('aria-pressed')).toBe('true')
  })

  it('walks back out to the panel on Escape instead of closing the companion', async () => {
    const harness = createChatApi({ messages: { 'conv-companion-a': [] } })
    companion.api = harness.api
    state = { ...IMMERSIVE_RIGHT }
    const wrapper = create()
    await flushPromises()

    const event = pressEscape()
    await flushPromises()

    expect(event.defaultPrevented).toBe(true)
    expect(companion.setMode).toHaveBeenCalledWith('panel')
    // Escape inside the immersive shape is a step out, not a dismissal.
    expect(companion.collapse).not.toHaveBeenCalled()
    expect(wrapper.find('.companion-panel').exists()).toBe(true)
  })

  it('keeps the capsule a single click away from the immersive shape', async () => {
    const harness = createChatApi({ messages: { 'conv-companion-a': [] } })
    companion.api = harness.api
    state = { ...IMMERSIVE_RIGHT }
    const wrapper = create()
    await flushPromises()

    click(wrapper.get(`[aria-label="${zh.companion.collapse}"]`).element, 1)
    await flushPromises()

    expect(companion.collapse).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.companion-capsule').exists()).toBe(true)
  })

  it('gives the panel a short strip and the call a full figure', async () => {
    const harness = createChatApi({
      companions: [summaryFixture('companion-a', 'Aria', 'fox')],
      messages: { 'conv-companion-a': [] },
    })
    companion.api = harness.api
    state = { ...PANEL_RIGHT }
    const wrapper = create()
    await flushPromises()

    // The panel's strip is 88px: enough for a standing figure, small enough to keep the chat usable.
    expect(wrapper.getComponent(CompanionStage).props('height')).toBe(88)

    await wrapper.get('[data-testid="companion-immersive-toggle"]').trigger('click')
    await flushPromises()

    // The figure grows inside the same window — the window itself never does.
    const callHeight = wrapper.getComponent(CompanionStage).props('height') as number
    expect(callHeight).toBeGreaterThan(88)
    expect(callHeight).toBeLessThanOrEqual(500)
  })

  it('opens the call when the figure itself is clicked', async () => {
    const harness = createChatApi({
      companions: [summaryFixture('companion-a', 'Aria', 'fox')],
      messages: { 'conv-companion-a': [] },
    })
    companion.api = harness.api
    state = { ...PANEL_RIGHT }
    const wrapper = create()
    await flushPromises()

    await wrapper.get('[data-testid="companion-stage-expand"]').trigger('click')
    await flushPromises()

    // The pet is the door: no toolbar button needed, and the same main-process call is made.
    expect(companion.setMode).toHaveBeenCalledWith('immersive')
    expect(wrapper.find('.companion-call').exists()).toBe(true)
  })

  it('turns immersive into a call: figure, one subtitle, one input, no pickers', async () => {
    const harness = createChatApi({
      companions: [summaryFixture('companion-a', 'Aria', 'fox')],
      messages: {
        'conv-companion-a': [
          messageFixture('m1', 'conv-companion-a', 1, 'user', 'User', '在吗'),
          messageFixture('m2', 'conv-companion-a', 2, 'companion', 'Aria', '在的，说吧', 'companion-a'),
        ],
      },
    })
    companion.api = harness.api
    state = { ...IMMERSIVE_RIGHT }
    const wrapper = create()
    await flushPromises()

    expect(wrapper.find('.companion-call').exists()).toBe(true)
    // A call has no companion or conversation switchers.
    expect(wrapper.find('.companion-pickers').exists()).toBe(false)
    // The scrollback is still mounted but out of sight, so stepping back into the panel is instant.
    // Asserted on the attribute, not `isVisible()`: happy-dom's computed style ignores the inline
    // `display: none` that `v-show` writes, and the directive itself is what this test cares about.
    expect(wrapper.get('.companion-chat').attributes('style')).toContain('display: none')
    expect(wrapper.findAll('.companion-input')).toHaveLength(1)

    const lines = wrapper.findAll('.companion-call-line')
    expect(lines).toHaveLength(2)
    expect(lines[1].text()).toContain('在的，说吧')
    expect(lines[1].classes()).not.toContain('is-past')
    // The line before it stays as dimmed context instead of a log.
    expect(lines[0].classes()).toContain('is-past')
  })

  it('waves when the face is clicked and sends nothing', async () => {
    const harness = createChatApi({
      companions: [summaryFixture('companion-a', 'Aria', 'fox')],
      messages: { 'conv-companion-a': [] },
    })
    companion.api = harness.api
    state = { ...PANEL_RIGHT }
    const wrapper = create()
    await flushPromises()

    await wrapper.get('[data-testid="companion-greet"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('.companion-header-face').classes()).toContain('is-greeting')
    // A local acknowledgement: the companion domain has no emote endpoint, so nothing is posted.
    expect(apiCalls(harness.calls, 'POST', '/messages')).toHaveLength(0)
  })
})

describe('companion presence', () => {
  /** A companion with a pet, so a stage exists; the panel is where every gesture lives. */
  function mountWithPet(options: ChatApiOptions = {}) {
    const harness = createChatApi({
      companions: [summaryFixture('companion-a', 'Aria', 'fox')],
      messages: { 'conv-companion-a': [] },
      ...options,
    })
    return { harness, wrapper: mountChat(harness.api) }
  }

  it('replaces the conversation label with what the companion is doing', async () => {
    const running = runFixture('run-live', 'companion-a', 'conv-companion-a')
    const { wrapper } = mountWithPet({ sendRuns: [running] })
    await flushPromises()

    // Idle: the subtitle names the conversation, which nothing else in the header does.
    expect(wrapper.get('.companion-subtitle').text())
      .toBe(zh.companion.chat.privateSubtitle.replace('{name}', 'Aria'))

    await wrapper.get('.companion-input').setValue('hello')
    await wrapper.get('.companion-composer').trigger('submit')
    await flushPromises()

    expect(wrapper.get('.companion-subtitle').text()).toBe(zh.companion.stateThinking)
    expect(wrapper.findAll('.companion-run-dots > span')).toHaveLength(3)
  })

  it('turns the send key into a pause while a turn is in flight', async () => {
    const running = runFixture('run-live', 'companion-a', 'conv-companion-a')
    const { harness, wrapper } = mountWithPet({ sendRuns: [running] })
    await flushPromises()

    expect(wrapper.get('.companion-send').attributes('aria-label')).toBe(zh.companion.chat.send)

    await wrapper.get('.companion-input').setValue('hello')
    await wrapper.get('.companion-composer').trigger('submit')
    await flushPromises()

    const key = wrapper.get('.companion-send')
    expect(key.classes()).toContain('is-pause')
    expect(key.attributes('aria-label')).toBe(zh.companion.chat.pauseAction)

    await key.trigger('click')
    await flushPromises()

    // Core cannot cancel a run, so this pauses the conversation and says so — it never pretends to
    // stop the reply that is already being written.
    const pauses = apiCalls(harness.calls, 'POST', '/pause')
    expect(pauses).toHaveLength(1)
    expect((pauses[0].body as { paused: boolean }).paused).toBe(true)
  })

  it('pats the companion on a double click over blank space, and never on a message', async () => {
    const { harness, wrapper } = mountWithPet({
      messages: {
        'conv-companion-a': [messageFixture('m1', 'conv-companion-a', 1, 'user', 'User', 'hello')],
      },
    })
    await flushPromises()

    // Double-clicking text is selection, not affection.
    doubleClick(wrapper.get('.companion-msg-body').element)
    await flushPromises()
    expect(wrapper.find('.companion-pat-note').exists()).toBe(false)
    expect(wrapper.get('[data-testid="companion-stage"]').classes())
      .not.toContain('companion-stage--jumping')

    doubleClick(wrapper.get('.companion-chat').element)
    await flushPromises()

    expect(wrapper.get('[data-testid="companion-stage"]').classes()).toContain('companion-stage--jumping')
    expect(wrapper.get('.companion-pat-note').text()).toBe(zh.companion.pat)
    // A gesture is not a message: nothing is posted and no transcript line is invented.
    expect(apiCalls(harness.calls, 'POST', '/messages')).toHaveLength(0)
    expect(wrapper.findAll('.companion-msg')).toHaveLength(1)
  })
})
