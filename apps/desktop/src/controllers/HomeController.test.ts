// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ref, nextTick } from 'vue'
import { flushPromises } from '@vue/test-utils'

const h = vi.hoisted(() => ({
  createUserToolActionForPath: vi.fn(),
  listProjects: vi.fn(async () => []),
  readiness: vi.fn(async () => ({ items: [] })),
  listSessions: vi.fn(async (_project?: string, _signal?: AbortSignal, _storage?: string) => []),
  readWorkspace: vi.fn(),
  createSession: vi.fn(),
  listMessages: vi.fn(async () => []),
  revertSessionMessage: vi.fn(),
  listApprovals: vi.fn(async () => []),
  listApprovalRules: vi.fn(async () => []),
  getOrchestrationSnapshot: vi.fn(async () => null),
  listToolExecutions: vi.fn(async () => []),
  listRuns: vi.fn(async () => []),
  connectEvents: vi.fn(() => ({ close: vi.fn(), disconnect: vi.fn() })),
  createInteraction: vi.fn(async (_sessionId: string, _body: Record<string, unknown>) => ({ run_id: null, status: 'accepted' })),
  cancelInteraction: vi.fn(async () => ({ status: 'cancelled' })),
  updateSessionTitle: vi.fn(async (id: string, title: string) => ({ id, title })),
  updateSessionSettings: vi.fn(async (id: string, settings: Record<string, unknown>) => ({ id, ...settings, settings_revision: Number(settings.expected_settings_revision ?? 0) + 1 })),
  migrateSession: vi.fn(), getSessionTransfer: vi.fn(),
  task: vi.fn(() => ({ id: 'transfer-task', update: vi.fn(), succeed: vi.fn(), fail: vi.fn(), dismiss: vi.fn() })),
  notifyError: vi.fn(),
  bannerError: vi.fn(),
  dismissByKey: vi.fn(),
}))

// The strip's own behaviour is pinned in pendingAttachments.test.ts; here it is only
// the source of "what a send carries", so the controller's three decisions (forward,
// clear, refuse) can be read off one call.
const attach = vi.hoisted(() => {
  const forSend = vi.fn(() => ({
    clientIds: [] as string[],
    attachmentIds: [] as string[],
    summaries: [] as Record<string, unknown>[],
  }))
  return {
    forSend,
    // Derived from the same stub the controller forwards, so a case that hands over two ready
    // rows also moves the count: two independent stubs could disagree and hide a real
    // disagreement between the two rules inside the double.
    readyCount: vi.fn(() => forSend().attachmentIds.length),
    settle: vi.fn(),
  }
})

vi.mock('@/lib/pendingAttachments', () => ({
  pendingAttachments: { value: [] },
  attachmentsForSend: attach.forSend,
  readyAttachmentCount: attach.readyCount,
  settleSentAttachments: attach.settle,
}))

vi.mock('@/api', () => ({
  api: {
    listProjects: h.listProjects,
    readiness: h.readiness,
    listSessions: h.listSessions,
    readWorkspace: h.readWorkspace,
    createSession: h.createSession,
    listMessages: h.listMessages,
    revertSessionMessage: h.revertSessionMessage,
    listApprovals: h.listApprovals,
    listApprovalRules: h.listApprovalRules,
    getOrchestrationSnapshot: h.getOrchestrationSnapshot,
    listToolExecutions: h.listToolExecutions,
    listRuns: h.listRuns,
    connectEvents: h.connectEvents,
    createInteraction: h.createInteraction,
    cancelInteraction: h.cancelInteraction,
    updateSessionTitle: h.updateSessionTitle,
    updateSessionSettings: h.updateSessionSettings,
    migrateSession: h.migrateSession, getSessionTransfer: h.getSessionTransfer,
  },
  createUserToolActionForPath: h.createUserToolActionForPath,
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: { error: h.notifyError, info: vi.fn(), task: h.task },
    banner: { error: h.bannerError },
    dismissByKey: h.dismissByKey,
  }),
}))

vi.mock('@/composables/useAgentActivity', () => ({
  useAgentActivity: () => ({
    activity: ref([]),
    toolCalls: ref([]),
    thinkingSteps: ref([]),
    agentStates: ref({}),
    progressEvents: ref([]),
  }),
}))

import { homeController } from './HomeController'
import i18n from '@/i18n'
import { setHostAccessStatus } from '@/lib/hostAccess'
import { ApiError } from '@/lib/apiError'
import * as durableStream from '@/lib/runStream'

function seedProject(): void {
  homeController.projects.value = [
    {
      id: 'project-1',
      name: 'demo',
      path: 'C:/workspace/demo',
      kind: null,
      created_at: null,
      updated_at: null,
      lifecycle_status: 'active',
      trashed_at: null,
    },
  ] as never
  homeController.setSelectedProject('project-1')
}

afterEach(() => {
  vi.clearAllMocks()
})

describe('HomeController session families', () => {
  it('does not recycle a pending empty flat conversation when starting a space conversation', async () => {
    homeController.setViewMode('flat')
    homeController.setSelectedProject(null)
    homeController.sessions.value = []
    h.createSession.mockResolvedValueOnce({ id: 'pending-flat-family', project_id: null, view_mode: 'flat' })
    await homeController.createSession(null)
    homeController.setViewMode('space')
    h.createSession.mockResolvedValueOnce({ id: 'pending-space-family', project_id: null, view_mode: 'space' })
    await homeController.createSession(null)
    expect(h.createSession).toHaveBeenCalledTimes(2)
    expect(homeController.selectedSessionId.value).toBe('pending-space-family')
    homeController.setViewMode('flat')
  })
  const familySessions = [
    { id: 'flat-family', project_id: null, title: 'Flat', view_mode: 'flat', mode_version_id: 'fixed-flat' },
    { id: 'space-family', project_id: null, title: 'Space', view_mode: 'space', mode_version_id: 'fixed-space' },
  ] as never[]
  it('switches lists and selection without reusing or reclassifying a conversation', async () => {
    homeController.setViewMode('flat')
    homeController.setSelectedProject(null)
    h.listSessions.mockResolvedValue(familySessions)
    homeController.sessions.value = familySessions
    await flushPromises()
    homeController.setSelectedSession('flat-family')
    homeController.updateDraft('flat draft')
    homeController.setViewMode('space')
    await flushPromises()
    expect(homeController.selectedSessionId.value).toBe('space-family')
    expect(homeController.visibleSessions.value.map(s => s.id)).toEqual(['space-family'])
    expect(homeController.draft.value).toBe('')
    homeController.setSelectedSession('flat-family')
    expect(homeController.selectedSessionId.value).toBe('space-family')
    homeController.setViewMode('flat')
    expect(homeController.selectedSessionId.value).toBe('flat-family')
    h.listSessions.mockResolvedValue([])
  })
  it('freezes creation family across a mode switch while awaiting the server', async () => {
    homeController.sessions.value = familySessions
    homeController.setSelectedProject(null)
    homeController.setViewMode('space')
    let finish!: (value: never) => void
    h.createSession.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const creating = homeController.createSession(null)
    homeController.setViewMode('flat')
    const selected = homeController.selectedSessionId.value
    finish({ id: 'new-space-family', project_id: null, title: 'Space', view_mode: 'space' } as never)
    await creating
    // A new session is titled in the app's language until its first message names it.
    expect(h.createSession).toHaveBeenLastCalledWith(null, i18n.global.t('chat.title'), null, 'space')
    expect(homeController.selectedSessionId.value).toBe(selected)
    expect(homeController.visibleSessions.value.some(s => s.id === 'new-space-family')).toBe(false)
  })
  it('honors an explicitly selected preset for later sends instead of the old session binding', async () => {
    homeController.setViewMode('flat')
    homeController.sessions.value = familySessions
    homeController.setSelectedSession('flat-family')
    homeController.updateDraft('next task')
    await homeController.sendMessage({ mode_version_id: 'different-mode', dispatch_mode: 'parallel' })
    expect(h.createInteraction.mock.calls.at(-1)?.[1]).toMatchObject({ mode_version_id: 'different-mode' })
  })
  it('keeps the draft when creating its new conversation fails', async () => {
    homeController.sessions.value = []
    homeController.selectedSessionId.value = null
    homeController.updateDraft('keep my request')
    h.createSession.mockRejectedValueOnce(new Error('create failed'))
    await homeController.sendMessage()
    expect(homeController.draft.value).toBe('keep my request')
  })
})

describe('HomeController session read ownership', () => {
  it('cannot restore an old conversation after its read resolves late', async () => {
    let resolve!: (value: never[]) => void
    h.listMessages.mockImplementationOnce(() => new Promise<never[]>((done) => { resolve = done }))
    homeController.setSelectedSession('slow-session')
    await nextTick()
    h.listMessages.mockResolvedValue([{ id: 'new-message', session_id: 'new-session', role: 'user', content: 'new' }] as never[])
    homeController.setSelectedSession('new-session')
    await flushPromises()
    resolve([{ id: 'old-message', session_id: 'slow-session', role: 'user', content: 'old' }] as never[])
    await flushPromises()
    expect(homeController.messages.value.map((message) => message.id)).toEqual(['new-message'])
    h.listMessages.mockResolvedValue([])
  })

  it('marks the transcript loading until the newest session read lands, not a stale one', async () => {
    let resolveSlow!: (value: never[]) => void
    let resolveNew!: (value: never[]) => void
    h.listMessages.mockImplementationOnce(() => new Promise<never[]>((done) => { resolveSlow = done }))
    homeController.setSelectedSession('slow-session-2')
    await nextTick()
    expect(homeController.transcriptLoading.value).toBe(true)
    h.listMessages.mockImplementationOnce(() => new Promise<never[]>((done) => { resolveNew = done }))
    homeController.setSelectedSession('new-session-2')
    await nextTick()
    resolveSlow([] as never[])
    await flushPromises()
    // The stale read finishing says nothing about the session on screen.
    expect(homeController.transcriptLoading.value).toBe(true)
    resolveNew([{ id: 'm', session_id: 'new-session-2', role: 'user', content: 'hi' }] as never[])
    await flushPromises()
    expect(homeController.transcriptLoading.value).toBe(false)
    h.listMessages.mockResolvedValue([])
  })

  it('does not let a late session roster restore the old selection after creating a new session', async () => {
    let resolveRoster!: (value: never[]) => void
    let staleSignal!: AbortSignal
    h.listSessions.mockImplementationOnce((_projectId?: string, signal?: AbortSignal) => {
      staleSignal = signal!
      return new Promise<never[]>((resolve) => { resolveRoster = resolve })
    })

    homeController.setSelectedProject('project-1')
    await nextTick()
    h.createSession.mockResolvedValueOnce({
      id: 'new-session',
      project_id: null,
      title: 'Tinadec session',
      status: 'active',
      created_at: '2026-10-02T00:00:00Z',
      updated_at: '2026-10-02T00:00:00Z',
    })

    await homeController.createSession('project-1')
    expect(homeController.selectedSessionId.value).toBe('new-session')
    expect(staleSignal.aborted).toBe(true)

    resolveRoster([{ id: 'old-session', project_id: null, title: '旧会话', status: 'active' }] as never[])
    await flushPromises()

    expect(homeController.selectedSessionId.value).toBe('new-session')
    expect(homeController.sessions.value.some((session) => session.id === 'new-session')).toBe(true)
  })
})

describe('HomeController.requestShellApproval', () => {
  it('creates the governed action with the shell tool id and {command, cwd} params', async () => {
    seedProject()
    await flushPromises()
    homeController.shellCommand.value = 'npm test'
    h.createUserToolActionForPath.mockResolvedValue({
      id: 'action-1',
      tool_id: 'shell',
      status: 'awaiting_approval',
      action_approval_id: 'approval-1',
      created_at: '2026-09-09T00:00:00Z',
      completed_at: null,
    })

    await homeController.requestShellApproval()

    expect(h.createUserToolActionForPath).toHaveBeenCalledTimes(1)
    const [path, toolId, params, idempotencyKey] = h.createUserToolActionForPath.mock.calls[0]!
    expect(path).toBe('C:/workspace/demo')
    // Core resolves any manifest-registered id; 'shell' is the governed command
    // tool (agent side uses the same id) and its frozen schema takes {command, cwd}.
    expect(toolId).toBe('shell')
    expect(params).toEqual({ command: 'npm test', cwd: 'C:/workspace/demo' })
    expect(typeof idempotencyKey).toBe('string')
    expect((idempotencyKey as string).startsWith('desktop:home:shell:')).toBe(true)
    expect(h.notifyError).not.toHaveBeenCalled()
    expect(homeController.approvals.value[0]?.command).toBe('shell')
  })

  it('rejects an empty command without calling Core', async () => {
    seedProject()
    await flushPromises()
    homeController.shellCommand.value = '   '

    await homeController.requestShellApproval()

    expect(h.createUserToolActionForPath).not.toHaveBeenCalled()
    expect(h.notifyError).toHaveBeenCalled()
  })
})

describe('HomeController.createSession free-conversation dedup', () => {
  it('reuses the pending free conversation instead of creating a duplicate', async () => {
    homeController.projects.value = []
    homeController.setSelectedProject(null)
    // The selectedProjectId watcher fires an async loadSessions(); let it settle
    // before seeding state so it cannot overwrite sessions mid-assertion.
    await flushPromises()
    // Core omits project_id for a free conversation, so the echoed row can carry
    // null/undefined while the argument is null; a raw === check used to miss and
    // create a second invisible conversation.
    h.createSession.mockResolvedValue({
      id: 'free-1',
      project_id: null,
      title: 'Tinadec session',
      status: 'active',
      created_at: '2026-09-10T00:00:00Z',
      updated_at: '2026-09-10T00:00:00Z',
    })

    await homeController.createSession(null)
    await homeController.createSession(null)

    expect(h.createSession).toHaveBeenCalledTimes(1)
    expect(homeController.selectedSessionId.value).toBe('free-1')
  })
})

describe('HomeController.editAndResend', () => {
  async function selectSession(): Promise<void> {
    homeController.projects.value = []
    homeController.setSelectedProject(null)
    await flushPromises()
    homeController.selectedSessionId.value = 'session-1'
    homeController.draft.value = ''
    // The selected-session watcher reloads the transcript; settle it before asserting.
    await flushPromises()
  }

  it('cuts the conversation at the edited message and hands the correction to the composer', async () => {
    await selectSession()
    h.revertSessionMessage.mockResolvedValue({ from_message_id: 'm2', from_sequence: 2, removed_count: 2, history_revision: 4 })

    await homeController.editAndResend({ id: 'm2', content: '改过的那条' })

    expect(h.revertSessionMessage).toHaveBeenCalledWith('session-1', 'm2')
    expect(homeController.draft.value).toBe('改过的那条')
    expect(homeController.invokeError.value).toBeNull()
  })

  it('keeps the corrected text when Core refuses the cut because a run still holds it', async () => {
    await selectSession()
    const readsBefore = h.listMessages.mock.calls.length
    h.revertSessionMessage.mockRejectedValue(Object.assign(new Error('run in flight'), { code: 'active_run_conflict' }))

    await homeController.editAndResend({ id: 'm2', content: '改过的那条' })

    expect(homeController.draft.value).toBe('改过的那条')
    expect(homeController.invokeError.value).toContain('停止它')
    // Nothing was cut, so the transcript must not be re-read as though it had been.
    expect(h.listMessages.mock.calls.length).toBe(readsBefore)
  })

  it('refuses to start while the composer holds unsent text', async () => {
    await selectSession()
    homeController.draft.value = '还没发的那句'

    await homeController.editAndResend({ id: 'm2', content: '改过的那条' })

    expect(h.revertSessionMessage).not.toHaveBeenCalled()
    expect(homeController.draft.value).toBe('还没发的那句')
    expect(homeController.invokeError.value).toContain('输入框')
  })
})

describe('HomeController.sendMessage attachment hand-off', () => {
  async function readySession(): Promise<void> {
    homeController.projects.value = []
    homeController.setSelectedProject(null)
    await flushPromises()
    homeController.selectedSessionId.value = 'session-1'
    homeController.updateDraft('看这个文件')
    await flushPromises()
    h.createInteraction.mockClear()
    attach.forSend.mockClear()
    attach.settle.mockClear()
  }

  const outgoing = {
    clientIds: ['c-1', 'c-2'],
    attachmentIds: ['att-1', 'att-2'],
    summaries: [
      { id: 'att-1', file_name: 'notes.txt', media_type: 'text/plain', content_hash: 'h1', content_length: 12, created_at: null, bound_at: null },
      { id: 'att-2', file_name: 'shot.png', media_type: 'image/png', content_hash: 'h2', content_length: 2048, created_at: null, bound_at: null },
    ],
  }

  it('names the ready rows in the interaction and clears the strip after Core answers', async () => {
    await readySession()
    attach.forSend.mockReturnValue(outgoing)

    await homeController.sendMessage({ dispatch_mode: 'parallel' })

    expect(h.createInteraction).toHaveBeenCalledTimes(1)
    expect(h.createInteraction.mock.calls[0]![1]).toMatchObject({ attachment_ids: ['att-1', 'att-2'] })
    expect(attach.settle).toHaveBeenCalledWith(outgoing)
  })

  it('keeps the selection when Core refuses the send', async () => {
    await readySession()
    attach.forSend.mockReturnValue(outgoing)
    h.createInteraction.mockRejectedValueOnce(new Error('attachment_already_bound'))

    await homeController.sendMessage({ dispatch_mode: 'parallel' })

    // A chip that vanished on a failed send is an upload the user cannot retry, and
    // Core never bound the rows, so they are still the only copy of those bytes.
    expect(attach.settle).not.toHaveBeenCalled()
    expect(h.notifyError).toHaveBeenCalled()
  })

  it('refuses to steer a message that carries files, because steering appends nothing', async () => {
    await readySession()
    attach.forSend.mockReturnValue(outgoing)

    await homeController.sendMessage({ dispatch_mode: 'insert', target_run_id: 'run-1' })

    // Sending without the files would be the silent failure; sending them would be an
    // orphan row, since insert never creates a message to own them. The refusal goes
    // through the same channel as the other pre-flight guard (run() notifies).
    expect(h.createInteraction).not.toHaveBeenCalled()
    expect(attach.settle).not.toHaveBeenCalled()
    expect(String(h.notifyError.mock.calls[0]?.[0])).toContain('附件')
  })

  it('omits the field entirely when nothing is attached', async () => {
    await readySession()
    const empty = { clientIds: [], attachmentIds: [], summaries: [] }
    attach.forSend.mockReturnValue(empty)

    await homeController.sendMessage({ dispatch_mode: 'parallel' })

    const body = h.createInteraction.mock.calls[0]![1] as Record<string, unknown>
    expect('attachment_ids' in body).toBe(false)
    // The clear runs but claims nothing: an empty bundle must not drop any chip.
    expect(attach.settle).toHaveBeenCalledWith(empty)
  })

  /**
   * The pair below is what gives the send guard its meaning: an empty draft with a finished
   * upload must go out, and an empty draft with nothing to send must not. Either one alone
   * passes if the guard is deleted (the first) or if it was never relaxed (the second).
   */
  it('sends an empty draft when an upload is there to speak for the turn', async () => {
    await readySession()
    attach.forSend.mockReturnValue(outgoing)
    homeController.updateDraft('')
    await flushPromises()
    h.createInteraction.mockResolvedValueOnce({ run_id: null, status: 'message_only' })

    await homeController.sendMessage({ dispatch_mode: 'queued' })

    const body = h.createInteraction.mock.calls[0]![1] as Record<string, unknown>
    expect(body.content).toBe('')
    expect(body.attachment_ids).toEqual(['att-1', 'att-2'])
    expect(attach.settle).toHaveBeenCalledWith(outgoing)
    // No run means no queued card: the turn is already in the transcript, nothing is waiting.
    expect(homeController.queuedMessages.value).toEqual([])
  })

  it('refuses an empty draft with only an in-flight upload, because it names no row', async () => {
    await readySession()
    attach.forSend.mockReturnValue({ clientIds: [], attachmentIds: [], summaries: [] })
    homeController.updateDraft('   ')
    await flushPromises()

    await homeController.sendMessage({ dispatch_mode: 'queued' })

    expect(h.createInteraction).not.toHaveBeenCalled()
    expect(attach.settle).not.toHaveBeenCalled()
  })
})

/**
 * Queued delivery waits behind the unfinished run (Core todo D1). Core answers with the run the
 * message waits behind and no turn of its own; the card is Core's queue entry, so acting on it
 * takes it out of that queue first — sending it again without that would post the words twice.
 */
describe('HomeController queued messages Core holds', () => {
  async function queuedBehind(modeVersionId?: string, modelOverride?: { provider_instance_id: string; model: string }, permissionMode = 'default'): Promise<void> {
    homeController.projects.value = []
    homeController.setSelectedProject(null)
    await flushPromises()
    homeController.selectedSessionId.value = 'session-q'
    homeController.updateDraft('完成后再跑一遍测试')
    await flushPromises()
    h.createInteraction.mockResolvedValueOnce({ interaction_id: 'directive-1', run_id: 'run-busy', status: 'queued', reason: 'busy' } as never)
    await homeController.sendMessage({ dispatch_mode: 'queued', mode_version_id: modeVersionId, meeting_model_override: modelOverride, permission_mode: permissionMode })
    await flushPromises()
  }

  it('shows a waiting message as a queued card and leaves the busy run alone', async () => {
    await queuedBehind()
    expect(homeController.queuedMessages.value).toEqual([
      expect.objectContaining({ content: '完成后再跑一遍测试', interactionId: 'directive-1' }),
    ])
    expect(homeController.runs.value.find((run) => run.id === 'run-busy')?.status).not.toBe('queued')
    homeController.queuedMessages.value = []
  })

  it('dequeues in Core before dismissing, and keeps the card when Core says it already left', async () => {
    await queuedBehind()
    const card = homeController.queuedMessages.value[0]!
    h.cancelInteraction.mockRejectedValueOnce(Object.assign(new Error('conflict'), { status: 409 }))
    await homeController.dismissQueued(card.id)
    expect(h.cancelInteraction).toHaveBeenCalledWith('session-q', 'directive-1')
    expect(homeController.queuedMessages.value).toHaveLength(1)

    await homeController.dismissQueued(card.id)
    expect(homeController.queuedMessages.value).toEqual([])
  })

  it('runs a promoted waiting message under its original id instead of posting it twice', async () => {
    await queuedBehind()
    const card = homeController.queuedMessages.value[0]!
    h.createInteraction.mockClear()
    await homeController.promoteQueued(card.id)
    expect(h.cancelInteraction).toHaveBeenCalledWith('session-q', 'directive-1')
    const body = h.createInteraction.mock.calls[0]![1] as Record<string, unknown>
    expect(body.dispatch_mode).toBe('parallel')
    expect(body.client_message_id).toBe(card.id)
    expect(homeController.queuedMessages.value).toEqual([])
  })

  it.each(['promote', 'steer', 'edit'] as const)('keeps the source session when %s waits while the user changes sessions', async action => {
    await queuedBehind('source-mode', { provider_instance_id: 'source-provider', model: 'source-model' }, 'full-access')
    const card = homeController.queuedMessages.value[0]!
    let finish!: (value: { status: string }) => void
    h.cancelInteraction.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    h.createInteraction.mockClear()
    const pending = action === 'promote' ? homeController.promoteQueued(card.id)
      : action === 'steer' ? homeController.steerQueued(card.id, 'source-run') : homeController.editQueued(card.id)
    homeController.selectedSessionId.value = 'another-session'
    homeController.updateDraft('another session draft')
    await flushPromises()
    finish({ status: 'cancelled' })
    await pending
    expect(homeController.draft.value).toBe('another session draft')
    if (action === 'edit') expect(h.createInteraction).not.toHaveBeenCalled()
    else expect(h.createInteraction.mock.calls[0]?.[0]).toBe('session-q')
  })

  it('keeps the queued policy and model choice even after the composer changes', async () => {
    await queuedBehind('mode-team', { provider_instance_id: 'provider-one', model: 'm' }, 'full-access')
    const card = homeController.queuedMessages.value[0]!
    homeController.updatePermission('default')
    h.createInteraction.mockClear()
    await homeController.promoteQueued(card.id)
    expect(h.createInteraction.mock.calls[0]?.[1]).toMatchObject({
      permission_mode: 'full-access', mode_version_id: 'mode-team', meeting_model_override: { provider_instance_id: 'provider-one', model: 'm' },
    })
  })

  it('freezes the full-access choice before asynchronous session creation', async () => {
    homeController.projects.value = []
    homeController.setSelectedProject(null)
    await flushPromises()
    homeController.selectedSessionId.value = null
    homeController.updatePermission('full-access')
    homeController.updateDraft('运行命令')
    let finishSession!: (session: never) => void
    h.createSession.mockImplementationOnce(() => new Promise(resolve => { finishSession = resolve }))
    const sending = homeController.sendMessage({ dispatch_mode: 'parallel' })
    homeController.updatePermission('default')
    finishSession({ id: 'created-policy-session', project_id: null, title: 'test' } as never)
    await sending
    expect(h.createInteraction.mock.calls.at(-1)?.[1]).toMatchObject({ permission_mode: 'full-access' })
  })

  it('asks Core to interrupt only when the user chose to interrupt (hard insert)', async () => {
    await queuedBehind()
    h.createInteraction.mockClear()
    h.createInteraction.mockResolvedValue({ interaction_id: 'i', session_id: 'session-q', status: 'steering_injected' } as never)
    await homeController.steerQueued(homeController.queuedMessages.value[0]!.id, 'run-busy', true)
    const hard = h.createInteraction.mock.calls[0]![1] as Record<string, unknown>
    expect(hard).toMatchObject({ dispatch_mode: 'insert', target_run_id: 'run-busy', interrupt: true })

    await queuedBehind()
    h.createInteraction.mockClear()
    await homeController.steerQueued(homeController.queuedMessages.value[0]!.id, 'run-busy')
    const soft = h.createInteraction.mock.calls[0]![1] as Record<string, unknown>
    expect(soft.dispatch_mode).toBe('insert')
    expect(soft).not.toHaveProperty('interrupt')
    expect(homeController.queuedMessages.value).toEqual([])
    h.createInteraction.mockReset()
  })
})

describe('HomeController composer settings', () => {
  const space = { plan_first: true, spec_enabled: false, multi_agent: true, workflow_mode_version_id: null, bulletin_board: false, worktree: false }
  async function selectSettingsSession(view: 'flat' | 'space' = 'flat') {
    homeController.setViewMode(view)
    homeController.setSelectedProject(null)
    await flushPromises()
    homeController.sessions.value = [
      { id: 'settings-a', view_mode: view, permission_mode: 'default', settings_revision: 3, mode_version_id: 'preset-old', meeting_model_override: null, space_options: view === 'space' ? { ...space } : null },
      { id: 'settings-b', view_mode: view, permission_mode: 'default', settings_revision: 0, mode_version_id: 'preset-other', meeting_model_override: null, space_options: null },
    ] as never
    homeController.setSelectedSession('settings-a')
    await flushPromises()
  }

  it('uses the revision returned by automatic naming on the second turn', async () => {
    homeController.setViewMode('flat')
    homeController.setSelectedProject(null)
    await flushPromises()
    homeController.sessions.value = []
    homeController.selectedSessionId.value = null
    h.createSession.mockResolvedValueOnce({ id: 'named-session', view_mode: 'flat', settings_revision: 0, permission_mode: 'default', space_options: null } as never)
    h.updateSessionTitle.mockResolvedValueOnce({ id: 'named-session', title: 'first task', settings_revision: 1 } as never)
    homeController.updateDraft('first task')
    await homeController.sendMessage({ dispatch_mode: 'parallel' })
    homeController.updateDraft('second task')
    await homeController.sendMessage({ dispatch_mode: 'parallel' })
    expect(h.createInteraction.mock.calls.at(-1)?.[1]).toMatchObject({ content: 'second task', expected_settings_revision: 1 })
  })

  it('persists to the originating session and never leaks its permission into another session', async () => {
    await selectSettingsSession()
    let finish!: (value: never) => void
    h.updateSessionSettings.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const saving = homeController.updatePermission('full-access')
    expect(homeController.settingsSaving.value).toBe(true)
    expect(homeController.currentPermission.value).toBe('default')
    homeController.setSelectedSession('settings-b')
    finish({ id: 'settings-a', permission_mode: 'full-access', settings_revision: 4 } as never)
    await saving
    expect(homeController.currentPermission.value).toBe('default')
    expect(homeController.settingsSaving.value).toBe(false)
    homeController.setSelectedSession('settings-a')
    expect(homeController.currentPermission.value).toBe('full-access')
    expect(h.updateSessionSettings).toHaveBeenLastCalledWith('settings-a', { permission_mode: 'full-access', expected_settings_revision: 3 })
  })

  it('serializes quick changes against successive server revisions', async () => {
    await selectSettingsSession()
    let finish!: (value: never) => void
    h.updateSessionSettings.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const first = homeController.updateComposerSettings({ mode_version_id: 'preset-new' })
    const second = homeController.updateComposerSettings({ permission_mode: 'auto-approve' })
    expect(h.updateSessionSettings).toHaveBeenCalledTimes(1)
    finish({ id: 'settings-a', mode_version_id: 'preset-new', settings_revision: 4 } as never)
    await Promise.all([first, second])
    expect(h.updateSessionSettings.mock.calls[1]?.[1]).toEqual({ permission_mode: 'auto-approve', expected_settings_revision: 4 })
    expect(homeController.composerSettings.value.mode_version_id).toBe('preset-new')
    expect(homeController.currentPermission.value).toBe('auto-approve')
  })

  it('retains effective settings and draft when saving fails, and waits for a pending save before sending', async () => {
    await selectSettingsSession()
    homeController.updateDraft('keep this task')
    let fail!: (error: Error) => void
    h.updateSessionSettings.mockImplementationOnce(() => new Promise((_resolve, reject) => { fail = reject }))
    const saving = homeController.updateComposerSettings({ mode_version_id: 'invalid-preset' })
    const sending = homeController.sendMessage({ dispatch_mode: 'parallel' })
    fail(new Error('Preset is unavailable'))
    expect(await saving).toBe(false)
    await sending
    expect(h.createInteraction).not.toHaveBeenCalled()
    expect(homeController.composerSettings.value.mode_version_id).toBe('preset-old')
    expect(homeController.settingsError.value).toBe('Preset is unavailable')
    expect(homeController.draft.value).toBe('keep this task')
  })

  it('does not read another session attachments after waiting for a settings save', async () => {
    await selectSettingsSession()
    homeController.updateDraft('source request')
    let finish!: (value: never) => void
    h.updateSessionSettings.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const saving = homeController.updateComposerSettings({ mode_version_id: 'preset-new' })
    const sending = homeController.sendMessage({ dispatch_mode: 'parallel' })
    homeController.setSelectedSession('settings-b')
    homeController.updateDraft('other request')
    finish({ id: 'settings-a', mode_version_id: 'preset-new', settings_revision: 4 } as never)
    await saving
    await sending
    expect(h.createInteraction).not.toHaveBeenCalled()
    expect(homeController.draft.value).toBe('other request')
  })

  it('captures spatial options and explicit default model when a message enters the queue', async () => {
    await selectSettingsSession('space')
    homeController.updateDraft('do the planned work')
    h.createInteraction.mockResolvedValueOnce({ interaction_id: 'settings-queued', run_id: 'busy-settings', status: 'queued' } as never)
    await homeController.sendMessage({ dispatch_mode: 'queued' })
    const queued = homeController.queuedMessages.value[0]!
    await homeController.updateComposerSettings({ space_options: { ...space, plan_first: false }, meeting_model_override: { provider_instance_id: 'new-provider', model: 'later-model' } })
    h.createInteraction.mockClear()
    await homeController.promoteQueued(queued.id)
    expect(h.createInteraction.mock.calls[0]?.[1]).toMatchObject({ space_options: space, clear_meeting_model_override: true, meeting_model_override: null })
    homeController.setViewMode('flat')
  })

  it('creates one draft session for first attachments with the selected model and spatial options', async () => {
    homeController.setViewMode('space')
    homeController.setSelectedProject(null)
    await flushPromises()
    homeController.sessions.value = []
    homeController.selectedSessionId.value = null
    await homeController.updateComposerSettings({ permission_mode: 'default', space_options: space, meeting_model_override: { provider_instance_id: 'first-provider', model: 'first-model' } })
    homeController.updateDraft('draft with a file')
    let finish!: (value: never) => void
    h.createSession.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const first = homeController.ensureComposerSession()
    const second = homeController.ensureComposerSession()
    expect(h.createSession).toHaveBeenCalledTimes(1)
    await homeController.sendMessage({ dispatch_mode: 'parallel' })
    expect(h.createInteraction).not.toHaveBeenCalled()
    expect(h.createSession).toHaveBeenCalledTimes(1)
    expect(h.createSession.mock.calls[0]?.[4]).toMatchObject({ space_options: space, meeting_model_override: { provider_instance_id: 'first-provider', model: 'first-model' } })
    finish({ id: 'attachment-draft', view_mode: 'space', space_options: space } as never)
    expect(await first).toBe('attachment-draft')
    expect(await second).toBe('attachment-draft')
    expect(homeController.draft.value).toBe('draft with a file')
    homeController.setViewMode('flat')
  })
})

describe('HomeController initial load', () => {
  it('does not let the project watcher abort the initial session roster read', async () => {
    let resolveRoster!: (value: never[]) => void
    let rosterSignal!: AbortSignal
    h.listProjects.mockResolvedValueOnce([{
      id: 'project-1',
      name: 'demo',
      path: 'C:/workspace/demo',
      created_at: '2026-10-07T00:00:00Z',
    }] as never[])
    h.readiness.mockResolvedValueOnce({ items: [] } as never)
    h.listSessions.mockImplementationOnce((_projectId?: string, signal?: AbortSignal) => {
      rosterSignal = signal!
      return new Promise<never[]>((resolve) => { resolveRoster = resolve })
    })

    homeController.start()
    await flushPromises()

    expect(h.listSessions).toHaveBeenCalledTimes(2) // User and workspace rosters load independently.
    expect(rosterSignal.aborted).toBe(false)

    resolveRoster([])
    await flushPromises()

    expect(h.bannerError).not.toHaveBeenCalled()
    expect(h.dismissByKey).toHaveBeenCalledWith('home-load')
  })
})

describe('HomeController independent storage scopes', () => {
  it('leaves local IPC recovery to the host banner without backend-load notifications', async () => {
    homeController.start()
    await flushPromises()
    setHostAccessStatus({ state: 'unavailable', managed: true })
    await nextTick()
    h.bannerError.mockClear()
    h.dismissByKey.mockClear()
    const unavailable = new ApiError('Restart desktop', 503, { code: 'desktop_restart_required', category: 'environment_unavailable', retryable: false, actions: [] })
    h.listProjects.mockRejectedValueOnce(unavailable)
    h.readiness.mockRejectedValueOnce(unavailable)
    setHostAccessStatus({ state: 'ready', managed: true })
    await flushPromises()
    expect(h.bannerError).not.toHaveBeenCalled()
    for (const key of ['home-load', 'home-readiness']) expect(h.dismissByKey).toHaveBeenCalledWith(key)
    h.dismissByKey.mockClear()
    setHostAccessStatus({ state: 'restart_required', managed: true, error: { code: 'desktop_restart_required', message: 'Restart desktop' } })
    await nextTick()
    for (const key of ['home-load', 'home-readiness']) expect(h.dismissByKey).toHaveBeenCalledWith(key)
    setHostAccessStatus({ state: 'ready', managed: true })
    await flushPromises()
  })
  it('keeps workspace data when diagnostics fail during a read-only host recovery', async () => {
    setHostAccessStatus({ state: 'unavailable', managed: true })
    await nextTick()
    h.listProjects.mockResolvedValueOnce([{ id: 'recover-project', storage_id: 'recover-scope', name: 'recovered', path: 'C:/recovered' }] as never)
    h.readiness.mockRejectedValueOnce(new Error('readiness service failed'))
    h.listSessions.mockImplementation(async (_project?: string, _signal?: AbortSignal, storage?: string) => storage === 'recover-scope'
      ? [{ id: 'recover-session', storage_id: storage, project_id: 'recover-project', title: 'retained' }] as never : [])
    setHostAccessStatus({ state: 'ready', managed: true })
    await flushPromises()
    expect(homeController.projects.value[0]?.id).toBe('recover-project')
    expect(homeController.sessions.value[0]?.id).toBe('recover-session')
    expect(h.bannerError).toHaveBeenCalledWith(expect.objectContaining({ key: 'home-readiness' }))
    expect(h.createSession).not.toHaveBeenCalled()
    expect(h.migrateSession).not.toHaveBeenCalled()
    h.listSessions.mockResolvedValue([])
  })

  it('clears only the recovered workspace availability failure after a successful scoped retry', async () => {
    const first = { id: 'failed-a', storage_id: 'failed-scope-a', path: 'C:/failed-a', name: 'first', availability: 'error', availability_error: 'missing directory' }
    const second = { id: 'failed-b', storage_id: 'failed-scope-b', path: 'C:/failed-b', name: 'second', availability: 'error' }
    homeController.projects.value = [first, second] as never
    h.listSessions.mockResolvedValueOnce([])
    h.listProjects.mockImplementationOnce(async () => [{ id: first.id, storage_id: first.storage_id, name: 'recovered first', path: first.path, availability: 'ready', lifecycle_status: 'active', configuration_hash: 'recovered-hash' }] as never)
      .mockResolvedValueOnce([]).mockResolvedValueOnce([])
    await homeController.retryWorkspaces('failed-scope-a::failed-a')
    expect(h.listProjects).toHaveBeenCalledWith({ storageId: first.storage_id, signal: expect.any(AbortSignal), lifecycleStatus: 'active' })
    expect(homeController.projects.value[0]).toMatchObject({ name: 'recovered first', availability: 'ready', configuration_hash: 'recovered-hash' })
    expect(homeController.projects.value[0]?.availability_error).toBeUndefined()
    expect(homeController.projects.value[1]).toEqual(second)
  })
  it('resumes the same run cursor after host recovery without duplicating streamed text', async () => {
    homeController.setSelectedProject(null)
    await flushPromises()
    const project = { id: 'live-project', storage_id: 'live-scope', name: 'live', path: 'C:/live', lifecycle_status: 'active' }
    const session = { id: 'live-session', storage_id: 'live-scope', project_id: project.id, view_mode: 'flat' }
    homeController.projects.value = [project] as never
    homeController.sessions.value = [session] as never
    h.listProjects.mockResolvedValue([project] as never)
    h.listSessions.mockImplementation(async (_project?: string, _signal?: AbortSignal, scope?: string) => scope === 'live-scope' ? [session] as never : [])
    h.listRuns.mockResolvedValue([{ id: 'live-run', status: 'executing' }] as never)
    const create = durableStream.createRunStream
    let handle!: durableStream.RunStreamHandle
    let connect!: ReturnType<typeof vi.spyOn>
    const factory = vi.spyOn(durableStream, 'createRunStream').mockImplementation(options => {
      handle = create(options)
      connect = vi.spyOn(handle, 'connect').mockImplementation(() => { handle.status.value = 'open' })
      vi.spyOn(handle, 'disconnect').mockImplementation(() => { handle.status.value = 'closed' })
      return handle
    })
    try {
      homeController.setSelectedSession('live-scope::live-session')
      await flushPromises()
      const chunk = { run_id: 'live-run', turn_id: null, message_id: null, seq: 7, kind: 'delta', occurred_at: '', payload: { delta: 'hello' } }
      handle.pushChunkForTest(chunk)
      expect(homeController.streamingText.value.get('live-run')).toBe('hello')
      setHostAccessStatus({ state: 'unavailable', managed: true })
      await nextTick()
      expect(handle.status.value).toBe('closed')
      h.listRuns.mockRejectedValueOnce(new Error('run roster unavailable'))
      setHostAccessStatus({ state: 'ready', managed: true })
      await flushPromises()
      expect(factory).toHaveBeenCalledTimes(1)
      expect(homeController.streamingText.value.get('live-run')).toBe('hello')
      expect(handle.status.value).toBe('closed')
      await homeController.loadMessagesAndApprovals()
      expect(factory).toHaveBeenCalledTimes(1)
      expect(connect).toHaveBeenLastCalledWith(7)
      expect(handle.pushChunkForTest(chunk)).toBe(false)
      handle.pushChunkForTest({ ...chunk, seq: 8, payload: { delta: ' world' } })
      expect(homeController.streamingText.value.get('live-run')).toBe('hello world')
    } finally {
      factory.mockRestore()
      h.listRuns.mockResolvedValue([]); h.listProjects.mockResolvedValue([]); h.listSessions.mockResolvedValue([])
      homeController.setSelectedProject(null)
      await flushPromises()
    }
  })
  it('keeps the unknown lifecycle and diagnostics when a workspace retry still fails', async () => {
    const project = { id: 'broken', storage_id: 'broken-scope', path: 'C:/broken', name: 'broken', availability: 'error', lifecycle_status: null, availability_error: 'database failed', availability_code: 'storage_database_unavailable', trace_id: 'scope-trace', actions: ['unregister_workspace'] }
    homeController.projects.value = [project] as never
    h.listSessions.mockResolvedValueOnce([])
    h.listProjects.mockResolvedValueOnce([project] as never).mockResolvedValueOnce([]).mockResolvedValueOnce([])
    await homeController.retryWorkspaces('broken-scope::broken')
    expect(homeController.projects.value[0]).toEqual(project)
    expect(homeController.workspaceLoadStates.value['broken-scope::broken']).toMatchObject({ status: 'error', error: { code: 'storage_database_unavailable', traceId: 'scope-trace', actions: ['unregister_workspace'] } })
  })
  it('retries one workspace without reloading or replacing another workspace roster', async () => {
    const first = { id: 'retry-a', storage_id: 'retry-scope-a', path: 'C:/retry-a', name: 'first' }
    const second = { id: 'retry-b', storage_id: 'retry-scope-b', path: 'C:/retry-b', name: 'second' }
    homeController.projects.value = [first, second] as never
    homeController.sessions.value = [{ id: 'retained-b', project_id: second.id, storage_id: second.storage_id }] as never
    homeController.workspaceLoadStates.value = { 'retry-scope-a::retry-a': { status: 'error' }, 'retry-scope-b::retry-b': { status: 'ready' } }
    h.listSessions.mockResolvedValueOnce([{ id: 'recovered-a', project_id: first.id, storage_id: first.storage_id }] as never)
    await homeController.retryWorkspaces('retry-scope-a::retry-a')
    expect(h.listSessions).toHaveBeenCalledTimes(1)
    expect(h.listSessions).toHaveBeenCalledWith(first.id, expect.any(AbortSignal), first.storage_id)
    expect(homeController.sessions.value.map(row => row.id)).toEqual(['retained-b', 'recovered-a'])
    expect(homeController.workspaceLoadStates.value['retry-scope-b::retry-b']).toEqual({ status: 'ready' })
  })
  it('applies a late settings receipt to its source clone only', async () => {
    let finish!: (result: { id: string; settings_revision: number; permission_mode: string }) => void
    const original = { id: 'clone-project', storage_id: 'original-scope', path: 'C:/original', name: 'original' }
    const copy = { ...original, storage_id: 'copy-scope', path: 'C:/copy', name: 'copy' }
    homeController.projects.value = [original, copy] as never
    homeController.sessions.value = [
      { id: 'clone-session', project_id: original.id, storage_id: original.storage_id, settings_revision: 1, permission_mode: 'default' },
      { id: 'clone-session', project_id: copy.id, storage_id: copy.storage_id, settings_revision: 1, permission_mode: 'default' },
    ] as never
    homeController.setSelectedSession('original-scope::clone-session')
    h.updateSessionSettings.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const writing = homeController.updateComposerSettings({ permission_mode: 'full' })
    homeController.setSelectedSession('copy-scope::clone-session')
    finish({ id: 'clone-session', settings_revision: 2, permission_mode: 'full' })
    expect(await writing).toBe(true)
    expect(homeController.sessions.value.find(row => row.storage_id === 'original-scope')?.permission_mode).toBe('full')
    expect(homeController.sessions.value.find(row => row.storage_id === 'copy-scope')?.permission_mode).toBe('default')
    expect(homeController.currentSession.value?.storage_id).toBe('copy-scope')
    homeController.setSelectedProject(null)
  })
  it('selects the target scope after an accepted asynchronous transfer completes', async () => {
    vi.useFakeTimers()
    try {
      const target = { id: 'transfer-project', storage_id: 'target-scope', path: 'C:/target', name: 'target' }
      const source = { id: 'transfer-session', storage_id: 'user', project_id: null, settings_revision: 1, title: 'free', view_mode: 'flat' }
      homeController.projects.value = [target] as never
      homeController.sessions.value = [source] as never
      homeController.setSelectedSession('user::transfer-session')
      await nextTick()
      const receipt = { transfer_id: 'transfer-1', session_id: source.id, source_storage_id: 'user', storage_id: target.storage_id, project_id: target.id, status: 'pending' }
      h.migrateSession.mockResolvedValueOnce(receipt)
      h.getSessionTransfer.mockResolvedValueOnce({ ...receipt, status: 'completed' })
      const targetSessions = [{ ...source, storage_id: target.storage_id, project_id: target.id }] as never
      h.listSessions.mockResolvedValueOnce(targetSessions).mockResolvedValueOnce(targetSessions)
      await homeController.migrateSession('user::transfer-session', 'target-scope::transfer-project')
      expect(h.migrateSession).toHaveBeenCalledWith(source.id, { target_storage_id: 'target-scope', target_project_id: target.id })
      await vi.advanceTimersByTimeAsync(1000)
      expect(h.getSessionTransfer).toHaveBeenCalledWith('transfer-1')
      expect(homeController.currentSession.value?.storage_id).toBe('target-scope')
      expect(h.task.mock.results.at(-1)?.value.succeed).toHaveBeenCalled()
    } finally { vi.useRealTimers(); homeController.setSelectedProject(null) }
  })
})

describe('HomeController session event refresh', () => {
  /**
   * Opening a session replays its history over the event stream. Each replayed event used to
   * start a full reload (six reads, two of them the whole session), aborting the one before:
   * hundreds of reloads Core still ran, and the transcript arrived last. The replay now waits
   * for the stream's first heartbeat, and live bursts coalesce into one trailing read.
   */
  it('reads once after the replay instead of once per replayed event, and coalesces a live burst', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    try {
      homeController.start()
      homeController.projects.value = []
      homeController.setSelectedProject(null)
      await flushPromises()
      homeController.selectedSessionId.value = 'replayed-session'
      await flushPromises()
      expect(homeController.selectedSessionId.value).toBe('replayed-session')
      const call = h.connectEvents.mock.calls.at(-1) as unknown as [string, (event: unknown) => void, () => void]
      const [, deliver, heartbeat] = call
      const event = (seq: number) => ({ type: 'tool.execution.completed', seq, session_id: 'replayed-session', payload: {} })
      h.listMessages.mockClear()

      for (let seq = 1; seq <= 50; seq += 1) deliver(event(seq))
      await vi.advanceTimersByTimeAsync(1000)
      expect(h.listMessages).not.toHaveBeenCalled()

      heartbeat()
      await vi.advanceTimersByTimeAsync(300)
      await flushPromises()
      expect(h.listMessages).toHaveBeenCalledTimes(1)

      for (let seq = 51; seq <= 80; seq += 1) deliver(event(seq))
      await vi.advanceTimersByTimeAsync(300)
      await flushPromises()
      expect(h.listMessages).toHaveBeenCalledTimes(2)
      homeController.selectedSessionId.value = null
      await flushPromises()
    } finally {
      vi.useRealTimers()
    }
  })
})
