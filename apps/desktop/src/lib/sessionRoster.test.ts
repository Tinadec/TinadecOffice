import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from './apiError'
import { readSessionRoster, loadSessionCatalog } from './sessionRoster'
const h = vi.hoisted(() => ({ scopes: vi.fn(), sessions: vi.fn() }))
vi.mock('@/api', () => ({ api: { listStorageScopes: h.scopes, listSessions: h.sessions } }))

describe('per-scope session roster', () => {
  beforeEach(() => { vi.clearAllMocks() })
  it('retains failed scope rows while updating successful scopes and preserves copied IDs', async () => {
    const previous = [{ id: 'same', storage_id: 'broken' }, { id: 'stale', storage_id: 'healthy' }]
    const result = await readSessionRoster({
      sources: [{ storageId: 'user' }, { storageId: 'healthy' }, { storageId: 'broken' }, { storageId: 'healthy' }], previous,
      read: vi.fn(async source => {
        if (source.storageId === 'broken') throw new ApiError('folder gone', 409, { code: 'storage_scope_unavailable', trace_id: 'trace-1', category: 'environment_unavailable', actions: ['unregister_workspace'] })
        return [{ id: 'same', storage_id: source.storageId }]
      }),
    })
    expect(result.rows).toEqual([{ id: 'same', storage_id: 'user' }, { id: 'same', storage_id: 'healthy' }, { id: 'same', storage_id: 'broken' }])
    expect(result.failures[0]?.error).toMatchObject({ details: 'folder gone\ntrace_id: trace-1', traceId: 'trace-1', actions: ['unregister_workspace'] })
    expect(previous).toHaveLength(2)
  })
  it('propagates cancellation instead of publishing an empty or failed roster', async () => {
    const controller = new AbortController()
    await expect(readSessionRoster({ sources: [{ storageId: 'user' }], signal: controller.signal, read: async () => { controller.abort(); return [] } })).rejects.toMatchObject({ name: 'AbortError' })
  })
  it('queries each scope with the explicit lifecycle and a captured scope header', async () => {
    h.scopes.mockResolvedValue([{ storage_id: 'user' }, { storage_id: 'project-a' }, { storage_id: 'project-b' }])
    h.sessions.mockImplementation(async (_project, _signal, storage) => [{ id: 'same', storage_id: storage }])
    const result = await loadSessionCatalog({ lifecycleStatus: 'archived' })
    expect(h.sessions.mock.calls.map(call => [call[2], call[3]])).toEqual([['user', 'archived'], ['project-a', 'archived'], ['project-b', 'archived']])
    expect(result.rows).toHaveLength(3)
  })
  it('does not present failed scope discovery as a successful user-only catalog', async () => {
    h.scopes.mockRejectedValue(new Error('registry unavailable'))
    await expect(loadSessionCatalog()).rejects.toThrow('registry unavailable')
    expect(h.sessions).not.toHaveBeenCalled()
  })
})
