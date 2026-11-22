// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const readBackendResponse = vi.hoisted(() => vi.fn())
const invalidateReadCache = vi.hoisted(() => vi.fn())

vi.mock('./backendRequest', () => ({
  readBackendResponse,
}))

vi.mock('./readCache', () => ({
  invalidateReadCache,
}))

vi.mock('./hostConnection', () => ({
  isElectronHost: () => false,
  resolveWebGatewayBase: () => 'http://127.0.0.1:48730',
}))

import {
  getRunTeammatesSnapshot,
  listRunTeammates,
  normalizeTeammateHandle,
  normalizeTeammateStatusKind,
  pauseRunTeammate,
  resumeRunTeammate,
  steerRunTeammate,
  teammateControlApi,
} from './teammateControlApi'

function mockResponse(body: unknown, status = 200, statusText = 'OK') {
  readBackendResponse.mockResolvedValueOnce({
    response: {
      ok: status >= 200 && status < 300,
      status,
      statusText,
      headers: new Headers(),
    },
    text: body === undefined ? '' : JSON.stringify(body),
  })
}

beforeEach(() => {
  vi.clearAllMocks()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('teammateControlApi', () => {
  it('normalizes handles and teammate status kinds accurately', () => {
    expect(normalizeTeammateHandle('@backend-api')).toBe('backend-api')
    expect(normalizeTeammateHandle('  impl#1  ')).toBe('impl#1')

    expect(normalizeTeammateStatusKind('working', false)).toBe('running')
    expect(normalizeTeammateStatusKind('working', true)).toBe('paused')
    expect(normalizeTeammateStatusKind('waiting', false)).toBe('idle')
    expect(normalizeTeammateStatusKind('awaiting_approval', false)).toBe('awaiting_approval')
    expect(normalizeTeammateStatusKind('terminated', false)).toBe('completed')
  })

  it('fetches run teammates snapshot from GET /api/v1/runs/{runId}/teammates', async () => {
    mockResponse({
      run_id: 'run-123',
      session_id: 'sess-9',
      run_status: 'executing',
      teammates: [
        {
          handle: 'backend-api',
          agent_slug: 'global_engineering',
          mode: 'teammate',
          status: 'working',
          paused: false,
          awaiting_approval: false,
          current_task_id: 'task-1',
          current_task_title: 'Wire teammate control endpoints',
          claimed_step_id: 'step-2',
          latest_summary: 'Adding steer/pause routes',
          runtime_badge: 'API · Gemini',
          write_scope: ['TinadecCore/**'],
          relay_depth: 1,
          pending_steer_count: 2,
          updated_at: '2026-11-23T02:26:10Z',
        },
      ],
    })

    const snap = await getRunTeammatesSnapshot('run-123')
    expect(readBackendResponse).toHaveBeenCalledTimes(1)
    const [url, path, init] = readBackendResponse.mock.calls[0]!
    expect(url).toBe('http://127.0.0.1:48730/api/v1/runs/run-123/teammates')
    expect(path).toBe('/api/v1/runs/run-123/teammates')
    expect(init.method).toBe('GET')
    expect(snap.run_id).toBe('run-123')
    expect(snap.run_status).toBe('executing')
    expect(snap.teammates).toHaveLength(1)
    expect(snap.teammates[0]).toMatchObject({
      handle: 'backend-api',
      status: 'working',
      paused: false,
      current_task_title: 'Wire teammate control endpoints',
      pending_steer_count: 2,
    })

    mockResponse({ teammates: [{ handle: 'worker#2', status: 'paused', paused: true }] })
    const list = await listRunTeammates('run-123')
    expect(list).toHaveLength(1)
    expect(list[0]!.paused).toBe(true)
  })

  it('URL-encodes teammate handles with # and strips @ when steering', async () => {
    mockResponse({
      accepted: true,
      run_id: 'run-123',
      handle: 'impl#1',
      status: 'working',
      paused: false,
      urgent: true,
      woken: true,
      delivery_mode: 'woken',
      board_seq: 18,
      pending_steer_count: 1,
      task_id: 'task-7',
      delivered_at: '2026-11-23T02:27:00Z',
    })

    const receipt = await steerRunTeammate('run-123', '@impl#1', {
      message: 'Please preserve backward-compatible schema fields.',
      urgent: true,
      client_control_id: 'ctrl-1',
    })

    const [url, path, init] = readBackendResponse.mock.calls[0]!
    expect(url).toBe('http://127.0.0.1:48730/api/v1/runs/run-123/teammates/impl%231/steer')
    expect(path).toBe('/api/v1/runs/run-123/teammates/impl%231/steer')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body)).toEqual({
      message: 'Please preserve backward-compatible schema fields.',
      urgent: true,
      client_control_id: 'ctrl-1',
    })
    expect(receipt).toMatchObject({
      accepted: true,
      run_id: 'run-123',
      handle: 'impl#1',
      woken: true,
      delivery_mode: 'woken',
      board_seq: 18,
      pending_steer_count: 1,
    })
    expect(invalidateReadCache).toHaveBeenCalledTimes(1)
  })

  it('calls pause and resume endpoints and parses control state receipts', async () => {
    mockResponse({
      accepted: true,
      run_id: 'run-123',
      handle: 'audit#2',
      action: 'pause',
      status: 'paused',
      paused: true,
      current_task_id: 'task-9',
      updated_at: '2026-11-23T02:28:00Z',
    })

    const paused = await pauseRunTeammate('run-123', 'audit#2', { reason: 'Wait for schema review' })
    expect(readBackendResponse.mock.calls[0]![0]).toBe(
      'http://127.0.0.1:48730/api/v1/runs/run-123/teammates/audit%232/pause',
    )
    expect(JSON.parse(readBackendResponse.mock.calls[0]![2].body)).toEqual({
      reason: 'Wait for schema review',
    })
    expect(paused).toMatchObject({
      accepted: true,
      action: 'pause',
      paused: true,
      status: 'paused',
    })

    mockResponse({
      accepted: true,
      run_id: 'run-123',
      handle: 'audit#2',
      action: 'resume',
      status: 'working',
      paused: false,
      current_task_id: 'task-9',
      updated_at: '2026-11-23T02:28:15Z',
    })

    const resumed = await resumeRunTeammate('run-123', 'audit#2')
    expect(readBackendResponse.mock.calls[1]![0]).toBe(
      'http://127.0.0.1:48730/api/v1/runs/run-123/teammates/audit%232/resume',
    )
    expect(resumed).toMatchObject({
      accepted: true,
      action: 'resume',
      paused: false,
      status: 'working',
    })
    expect(teammateControlApi.pause).toBe(pauseRunTeammate)
    expect(teammateControlApi.resume).toBe(resumeRunTeammate)
  })

  it('surfaces ApiError on 409 RUN_NOT_ACTIVE responses', async () => {
    mockResponse({ code: 'RUN_NOT_ACTIVE', message: 'Run is already completed.' }, 409, 'Conflict')
    await expect(
      steerRunTeammate('run-closed', 'backend-api', { message: 'Try again' }),
    ).rejects.toThrow('Run is already completed.')
  })
})
