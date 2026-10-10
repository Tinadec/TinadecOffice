// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { readBackendResponse } from './backendRequest'
import { ApiError } from './apiError'
import { setHostAccessStatus } from './hostAccess'

const ready = { state: 'ready', managed: true } as const
const getHostStatus = vi.fn()
const fetchMock = vi.fn()
function request(init: RequestInit = {}) {
  return readBackendResponse('http://127.0.0.1:48730/api/v1/agents', '/api/v1/agents',
    { headers: { 'x-tinadec-storage-id': 'project-a' }, ...init }, { storageId: 'project-a' })
}
beforeEach(() => {
  vi.useFakeTimers()
  getHostStatus.mockReset().mockResolvedValue(ready)
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus } })
  setHostAccessStatus(ready)
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); delete (window as unknown as { tinadec?: unknown }).tinadec })

describe('backend read recovery', () => {
  it('recovers a transient read with the original scope and revalidates the host', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch')).mockResolvedValueOnce(new Response('[]'))
    const result = request()
    await vi.advanceTimersByTimeAsync(250)
    expect((await result).text).toBe('[]')
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(getHostStatus).toHaveBeenCalledTimes(2)
    for (const [, init] of fetchMock.mock.calls) expect(init.headers.get('x-tinadec-storage-id')).toBe('project-a')
  })
  it('limits persistent failure to three attempts and retains a classified scoped error', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))
    const result = request().catch(error => error)
    await vi.advanceTimersByTimeAsync(1000)
    expect(await result).toMatchObject({ code: 'backend_network_unavailable', status: 0, storageId: 'project-a', retryable: true, actions: ['retry'] })
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })
  it('includes body transfer failure in the read budget', async () => {
    fetchMock.mockResolvedValueOnce({ text: async () => { throw new TypeError('Connection reset') } }).mockResolvedValueOnce(new Response('[]'))
    const result = request()
    await vi.advanceTimersByTimeAsync(250)
    expect((await result).text).toBe('[]')
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })
  it.each(['POST', 'PUT', 'PATCH', 'DELETE'])('does not replay %s after a transport failure', async method => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))
    await expect(request({ method, body: '{}' })).rejects.toMatchObject({ code: 'backend_write_outcome_unknown', retryable: false, actions: [] })
    await vi.advanceTimersByTimeAsync(2000)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
  it('does not replay a write whose response body is lost', async () => {
    fetchMock.mockResolvedValue({ text: async () => { throw new TypeError('Connection reset') } })
    await expect(request({ method: 'PUT', body: '{}' })).rejects.toMatchObject({ code: 'backend_write_outcome_unknown' })
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
  it('returns an HTTP validation failure without retrying', async () => {
    fetchMock.mockResolvedValue(new Response('{"code":"configuration_validation_failed"}', { status: 400 }))
    expect((await request()).response.status).toBe(400)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
  it('cancels the retry delay and preserves cancellation identity', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))
    const controller = new AbortController()
    const result = request({ signal: controller.signal }).catch(error => error)
    await vi.advanceTimersByTimeAsync(1)
    controller.abort()
    expect(await result).toBe(controller.signal.reason)
    await vi.advanceTimersByTimeAsync(2000)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
  it('stops retrying when host access is revoked', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))
    const result = request().catch(error => error)
    await vi.advanceTimersByTimeAsync(1)
    const rejected = { ...ready, state: 'rejected', error: { code: 'host_identity_mismatch', message: 'Wrong service identity' } }
    getHostStatus.mockResolvedValue(rejected)
    setHostAccessStatus(rejected as Parameters<typeof setHostAccessStatus>[0])
    await vi.advanceTimersByTimeAsync(250)
    expect(await result).toBeInstanceOf(ApiError)
    expect(await result).toMatchObject({ code: 'host_identity_mismatch' })
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
  it('does not turn a programming exception into a network retry', async () => {
    const error = new Error('Invalid transport fixture')
    fetchMock.mockRejectedValue(error)
    await expect(request()).rejects.toBe(error)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
})
