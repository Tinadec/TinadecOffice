// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { flushPromises } from '@vue/test-utils'

const h = vi.hoisted(() => ({
  createUserToolActionForPath: vi.fn(),
  listSessions: vi.fn(async () => []),
  createSession: vi.fn(),
  listMessages: vi.fn(async () => []),
  listApprovals: vi.fn(async () => []),
  getOrchestrationSnapshot: vi.fn(async () => null),
  listToolExecutions: vi.fn(async () => []),
  listRuns: vi.fn(async () => []),
  connectEvents: vi.fn(() => ({ close: vi.fn(), disconnect: vi.fn() })),
  notifyError: vi.fn(),
}))

vi.mock('@/api', () => ({
  api: {
    listSessions: h.listSessions,
    createSession: h.createSession,
    listMessages: h.listMessages,
    listApprovals: h.listApprovals,
    getOrchestrationSnapshot: h.getOrchestrationSnapshot,
    listToolExecutions: h.listToolExecutions,
    listRuns: h.listRuns,
    connectEvents: h.connectEvents,
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
