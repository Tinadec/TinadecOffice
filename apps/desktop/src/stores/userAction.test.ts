import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useUserActionStore } from './userAction'
import { api, type UserToolActionDto } from '@/api'

vi.mock('@/api', () => ({
  api: {
    createUserToolAction: vi.fn(),
    getUserToolAction: vi.fn(),
    decidePermissionRequest: vi.fn(),
    decideApproval: vi.fn(),
    resumeUserToolAction: vi.fn(),
    overrideUserToolActionSnapshot: vi.fn(),
    decideUserToolActionRecovery: vi.fn(),
    listUserToolActions: vi.fn(),
  },
}))

const apiMock = vi.mocked(api, true)

function makeAction(overrides: Partial<Omit<UserToolActionDto, 'audit_reference'>> = {}): UserToolActionDto {
  return {
    id: '0b8e6f60-0000-4000-8000-000000000001',
    audit_reference: 'user-tool-action:test',
    tenant_id: 't',
    workspace_id: 'w',
    project_id: 'p',
    principal_id: 'u',
    tool_id: 'git_commit',
    status: 'running',
    risk: 'high',
    mutates_workspace: true,
    requires_approval: true,
    created_at: '2026-08-23T00:00:00Z',
    updated_at: '2026-08-23T00:00:00Z',
    ...overrides,
  }
}

describe('userAction store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
    vi.useFakeTimers()
  })

  afterEach(() => {
    const store = useUserActionStore()
    store.stopAllPolling()
    vi.useRealTimers()
  })

  it('create stores the returned action and exposes it via get', async () => {
    const action = makeAction({ status: 'awaiting_user' })
    apiMock.createUserToolAction.mockResolvedValue(action)

    const store = useUserActionStore()
    const result = await store.create({ project_id: 'p', tool_id: 'git_commit' })

    expect(result).toEqual(action)
    expect(store.get(action.id)).toEqual(action)
    expect(store.pendingDecisions.map((a) => a.id)).toContain(action.id)
  })

  it('refresh updates the stored snapshot and clears stale errors', async () => {
    const before = makeAction({ status: 'running', message: null })
    const after = makeAction({ status: 'completed', completed_at: '2026-08-23T00:01:00Z' })
    apiMock.getUserToolAction.mockResolvedValueOnce(after)
    // Seed the store with the earlier snapshot via create.
    apiMock.createUserToolAction.mockResolvedValueOnce(before)

    const store = useUserActionStore()
    await store.create({ project_id: 'p', tool_id: 'git_commit' })
    const fresh = await store.refresh(before.id)

    expect(fresh?.status).toBe('completed')
    expect(store.get(before.id)?.status).toBe('completed')
  })

  it('decidePermission sends approve boolean to the permission request and refreshes', async () => {
    const awaiting = makeAction({
      status: 'awaiting_user',
      permission_request_id: '11111111-1111-4111-8111-111111111111',
    })
    const advanced = makeAction({
      status: 'awaiting_approval',
      permission_request_id: '11111111-1111-4111-8111-111111111111',
      action_approval_id: '22222222-2222-4222-8222-222222222222',
    })
    apiMock.createUserToolAction.mockResolvedValue(awaiting)
    apiMock.decidePermissionRequest.mockResolvedValue({} as never)
    apiMock.getUserToolAction.mockResolvedValue(advanced)

    const store = useUserActionStore()
    await store.create({ project_id: 'p', tool_id: 'git_commit' })
    const next = await store.decidePermission(awaiting.id, 'approved')

    expect(apiMock.decidePermissionRequest).toHaveBeenCalledWith(
      awaiting.permission_request_id,
      { approve: true, reason: null },
    )
    expect(next?.status).toBe('awaiting_approval')
  })

  it('decideApproval throws when the action has no action_approval_id yet', async () => {
    const awaitingUser = makeAction({ status: 'awaiting_user' })
    apiMock.createUserToolAction.mockResolvedValue(awaitingUser)

    const store = useUserActionStore()
    await store.create({ project_id: 'p', tool_id: 'git_commit' })

    await expect(store.decideApproval(awaitingUser.id, 'approved')).rejects.toThrow(
      /no action_approval_id/,
    )
  })

  it('decideRecovery posts mark_completed and stops tracking as active', async () => {
    const unknown = makeAction({ status: 'outcome_unknown' })
    const settled = makeAction({
      status: 'completed',
      recovery_decision: 'mark_completed',
      recovered_at: '2026-08-23T00:02:00Z',
    })
    apiMock.createUserToolAction.mockResolvedValue(unknown)
    apiMock.decideUserToolActionRecovery.mockResolvedValue(settled)

    const store = useUserActionStore()
    await store.create({ project_id: 'p', tool_id: 'git_commit' })
    const next = await store.decideRecovery(unknown.id, {
      decision: 'mark_completed',
      reason: 'verified workspace',
    })

    expect(apiMock.decideUserToolActionRecovery).toHaveBeenCalledWith(unknown.id, {
      decision: 'mark_completed',
      reason: 'verified workspace',
    })
    expect(next?.recovery_decision).toBe('mark_completed')
  })

  it('hydrate merges a durable list without dropping existing entries', async () => {
    const local = makeAction({ tool_id: 'git_push' })
    apiMock.createUserToolAction.mockResolvedValue(local)
    apiMock.listUserToolActions.mockResolvedValue([
      makeAction({ id: '0b8e6f60-0000-4000-8000-000000000002', tool_id: 'git_stage', status: 'completed' }),
    ])

    const store = useUserActionStore()
    await store.create({ project_id: 'p', tool_id: 'git_commit' })
    await store.hydrate()

    expect(store.allActions.length).toBe(2)
    expect(store.allActions[0].created_at >= store.allActions[1].created_at).toBe(true)
  })
})
