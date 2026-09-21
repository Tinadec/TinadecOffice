// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { flushPromises } from '@vue/test-utils'

const h = vi.hoisted(() => ({
  createUserToolActionForPath: vi.fn(),
  listSessions: vi.fn(async () => []),
  createSession: vi.fn(),
  listMessages: vi.fn(async () => []),
  revertSessionMessage: vi.fn(),
  listApprovals: vi.fn(async () => []),
  getOrchestrationSnapshot: vi.fn(async () => null),
  listToolExecutions: vi.fn(async () => []),
  listRuns: vi.fn(async () => []),
  connectEvents: vi.fn(() => ({ close: vi.fn(), disconnect: vi.fn() })),
  createInteraction: vi.fn(async (_sessionId: string, _body: Record<string, unknown>) => ({ run_id: null, status: 'accepted' })),
  updateSessionTitle: vi.fn(async () => ({ id: 'session-1' })),
  notifyError: vi.fn(),
}))

// The strip's own behaviour is pinned in pendingAttachments.test.ts; here it is only
// the source of "what a send carries", so the controller's three decisions (forward,
// clear, refuse) can be read off one call.
const attach = vi.hoisted(() => ({
  forSend: vi.fn(() => ({
    clientIds: [] as string[],
    attachmentIds: [] as string[],
    summaries: [] as Record<string, unknown>[],
  })),
  settle: vi.fn(),
}))

vi.mock('@/lib/pendingAttachments', () => ({
  attachmentsForSend: attach.forSend,
  settleSentAttachments: attach.settle,
}))

vi.mock('@/api', () => ({
  api: {
    listSessions: h.listSessions,
    createSession: h.createSession,
    listMessages: h.listMessages,
    revertSessionMessage: h.revertSessionMessage,
    listApprovals: h.listApprovals,
    getOrchestrationSnapshot: h.getOrchestrationSnapshot,
    listToolExecutions: h.listToolExecutions,
    listRuns: h.listRuns,
    connectEvents: h.connectEvents,
    createInteraction: h.createInteraction,
    updateSessionTitle: h.updateSessionTitle,
  },
  createUserToolActionForPath: h.createUserToolActionForPath,
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: { error: h.notifyError, info: vi.fn() },
    banner: { error: vi.fn() },
    dismissByKey: vi.fn(),
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
})
