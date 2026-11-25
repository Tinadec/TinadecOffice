// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import type { SessionDto } from '@/api'
import { captureStorageId, selectionKey, setSelectedStorage } from '@/lib/storageScope'
import { ACTIVITY_CONCURRENCY, ACTIVITY_LIMIT, ACTIVITY_TTL_MS, latestRun, useRecentActivity, type ActivityCandidate } from './useRecentActivity'

const { listRunsMock } = vi.hoisted(() => ({
  listRunsMock: vi.fn(async (..._args: unknown[]): Promise<unknown[]> => []),
}))

vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return { ...actual, api: { ...actual.api, listRuns: (...args: unknown[]) => listRunsMock(...args) } }
})

function candidate(id: string, updatedAt = '2026-10-21T10:00:00Z'): ActivityCandidate {
  return {
    session: {
      permission_mode: 'default', space_options: null, settings_revision: 0,
      id, project_id: 'p-1', title: id, status: 'ready',
      created_at: '2026-10-01T00:00:00Z', updated_at: updatedAt,
    } as SessionDto,
    projectName: 'Demo project',
  }
}

function harness(candidates: () => ActivityCandidate[], enabled = ref(true), limit?: () => number) {
  let activity!: ReturnType<typeof useRecentActivity>
  const wrapper = mount(defineComponent({
    setup() {
      activity = useRecentActivity(candidates, { enabled: () => enabled.value, limit })
      return () => null
    },
  }))
  return { activity, wrapper, enabled }
}

const runRow = (id: string, status: string, createdAt: string) => ({
  id, session_id: 's', status, summary: '', created_at: createdAt, updated_at: createdAt,
})

beforeEach(() => {
  listRunsMock.mockReset()
  listRunsMock.mockResolvedValue([])
  setSelectedStorage('user')
})

describe('useRecentActivity', () => {
  it('reads exactly the conversations it is handed, and honours an explicit cap', async () => {
    const rows = Array.from({ length: ACTIVITY_LIMIT + 3 }, (_, index) => candidate(`s-${index}`))
    // The caller owns the width: 所有活动 hands over the whole list, and every entry is read.
    const wide = harness(() => rows)
    await wide.activity.refresh()
    expect(listRunsMock).toHaveBeenCalledTimes(rows.length)
    expect(listRunsMock).toHaveBeenCalledWith('s-0', expect.anything())
    wide.wrapper.unmount()

    listRunsMock.mockClear()
    const capped = harness(() => rows, ref(true), () => ACTIVITY_LIMIT)
    await capped.activity.refresh()
    expect(listRunsMock).toHaveBeenCalledTimes(ACTIVITY_LIMIT)
    capped.wrapper.unmount()
  })

  it('fans out in batches so a wide view does not fire one burst', async () => {
    let inFlight = 0, peak = 0
    listRunsMock.mockImplementation(async () => {
      inFlight += 1
      peak = Math.max(peak, inFlight)
      await Promise.resolve()
      inFlight -= 1
      return []
    })
    const rows = Array.from({ length: ACTIVITY_CONCURRENCY * 2 + 3 }, (_, index) => candidate(`s-${index}`))
    const { activity, wrapper } = harness(() => rows)
    await activity.refresh()
    expect(peak).toBeLessThanOrEqual(ACTIVITY_CONCURRENCY)
    expect(listRunsMock).toHaveBeenCalledTimes(rows.length)
    wrapper.unmount()
  })

  it('reads nothing while the option is off', async () => {
    const enabled = ref(false)
    const { activity, wrapper } = harness(() => [candidate('s-1')], enabled)
    await activity.refresh()
    expect(listRunsMock).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('keeps the newest run of a roster instead of trusting its order', async () => {
    listRunsMock.mockResolvedValue([
      runRow('old', 'completed', '2026-10-20T09:00:00Z'),
      runRow('newest', 'awaiting_user', '2026-10-21T09:00:00Z'),
      runRow('middle', 'failed', '2026-10-20T18:00:00Z'),
    ])
    const { activity, wrapper } = harness(() => [candidate('s-1')])
    await activity.refresh()
    expect(activity.runs.value['s-1']?.id).toBe('newest')
    expect(latestRun([])).toBeNull()
    wrapper.unmount()
  })

  it('reuses a read until the session itself moves', async () => {
    const updated = ref('2026-10-21T10:00:00Z')
    const { activity, wrapper } = harness(() => [candidate('s-1', updated.value)])
    await activity.refresh()
    await activity.refresh()
    // A roster cannot change without the session changing, so the second read is skipped.
    expect(listRunsMock).toHaveBeenCalledTimes(1)

    updated.value = '2026-10-21T11:00:00Z'
    await activity.refresh()
    expect(listRunsMock).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })

  it('refreshes an unchanged session once the read passes the TTL', async () => {
    const clock = vi.spyOn(Date, 'now').mockReturnValue(Date.parse('2026-10-21T12:00:00Z'))
    try {
      const { activity, wrapper } = harness(() => [candidate('s-1')])
      await activity.refresh()
      await activity.refresh()
      expect(listRunsMock).toHaveBeenCalledTimes(1)

      clock.mockReturnValue(Date.parse('2026-10-21T12:00:00Z') + ACTIVITY_TTL_MS + 1)
      await activity.refresh()
      expect(listRunsMock).toHaveBeenCalledTimes(2)
      wrapper.unmount()
    } finally {
      clock.mockRestore()
    }
  })

  it('keeps the other cards when one roster cannot be read', async () => {
    listRunsMock.mockImplementation(async (sessionId: unknown) => {
      if (sessionId === 's-broken') throw new Error('roster unavailable')
      return [runRow(`${String(sessionId)}-run`, 'completed', '2026-10-21T09:00:00Z')]
    })
    const { activity, wrapper } = harness(() => [candidate('s-ok'), candidate('s-broken')])
    await activity.refresh()
    expect(activity.runs.value['s-ok']?.status).toBe('completed')
    expect(activity.runs.value['s-broken']).toBeUndefined()
    wrapper.unmount()
  })

  it('drops a read that lands after the storage scope changed', async () => {
    let release: (rows: unknown[]) => void = () => {}
    listRunsMock.mockImplementationOnce(() => new Promise(resolve => { release = resolve }))
    const { activity, wrapper } = harness(() => [candidate('s-1')])
    const pending = activity.refresh()
    setSelectedStorage('project-2')
    release([runRow('stale', 'completed', '2026-10-21T09:00:00Z')])
    await pending
    await flushPromises()
    expect(activity.runs.value['s-1']).toBeUndefined()
    wrapper.unmount()
    setSelectedStorage('user')
  })

  it('scopes each listRuns request to its session storage_id rather than selectedStorage', async () => {
    setSelectedStorage('user')
    const seenScopes: Record<string, string> = {}
    listRunsMock.mockImplementation(async (sessionId: unknown) => {
      const id = String(sessionId)
      seenScopes[id] = captureStorageId(`/api/v1/sessions/${id}/runs`)
      return [runRow(`${id}-run`, 'completed', '2026-10-21T09:00:00Z')]
    })
    const a = candidate('s-a')
    a.session.storage_id = 'project-alpha'
    const b = candidate('s-b')
    b.session.storage_id = 'project-beta'
    const { activity, wrapper } = harness(() => [a, b])
    await activity.refresh()
    expect(seenScopes).toEqual({ 's-a': 'project-alpha', 's-b': 'project-beta' })
    expect(activity.runs.value[selectionKey(a.session)]?.status).toBe('completed')
    expect(activity.runs.value[selectionKey(b.session)]?.status).toBe('completed')
    wrapper.unmount()
  })
})
