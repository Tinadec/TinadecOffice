import { hostAccessState, onHostAccessChanged } from './hostAccess'
import { buildEndpointReadCacheKey, currentEndpointId, toEndpointId, type EndpointInput } from './endpointScope'
import { activeStorageScopeId } from './storageScope'

/**
 * Long enough to cover one interaction — open the command panel, switch a settings tab,
 * drop the mode menu — and short enough that a change made elsewhere shows up by itself.
 * Our own writes never wait for it: every completed mutation invalidates these projections.
 */
export const READ_CACHE_TTL_MS = 3000

/**
 * Directory-shaped projections: provider templates, ACP adapters, model catalogs. They only
 * change when someone edits configuration, and every completed mutation bumps the cache
 * generation, so a long window cannot serve data that our own writes made stale. Kept well
 * under a working session so an edit made by another client still appears without a restart.
 */
export const CATALOG_READ_CACHE_TTL_MS = 5 * 60 * 1000

interface CacheEntry {
  expiresAt: number
  hostState: string | null
  value: unknown
}

const entries = new Map<string, CacheEntry>()
const inFlight = new Map<string, Promise<unknown>>()
const endpointGenerations = new Map<string, number>()
let generation = 0

function endpointPrefixOfKey(qualifiedKey: string): string {
  if (qualifiedKey.startsWith('peer:')) {
    const thirdColon = qualifiedKey.indexOf(':', 'peer:'.length)
    return thirdColon > 0 ? qualifiedKey.slice(0, thirdColon) : qualifiedKey
  }
  const firstColon = qualifiedKey.indexOf(':')
  return firstColon > 0 ? qualifiedKey.slice(0, firstColon) : 'local'
}

function currentEndpointGeneration(endpointId: string): number {
  return endpointGenerations.get(endpointId) ?? 0
}

export function qualifyReadCacheKey(
  key: string,
  options?: { endpoint?: EndpointInput; storageId?: string },
): string {
  if (key.startsWith('local:') || key.startsWith('peer:')) {
    return key
  }
  return buildEndpointReadCacheKey(key, {
    endpoint: options?.endpoint ?? currentEndpointId(),
    storageId: options?.storageId ?? activeStorageScopeId(),
  })
}

/**
 * Writes change what these read-only projections describe, so nothing may outlive them —
 * not even the TTL. Bumping the generation also stops a read that started before the write
 * from caching its now-stale answer when it lands.
 */
export function invalidateReadCache(): void {
  generation++
  endpointGenerations.clear()
  entries.clear()
  inFlight.clear()
}

/**
 * Invalidates cached reads and in-flight entries strictly for one endpoint (`local` or `peer:<deviceId>`),
 * leaving all other endpoints untouched.
 */
export function invalidateEndpointReadCache(endpoint: EndpointInput): void {
  const endpointId = toEndpointId(endpoint)
  endpointGenerations.set(endpointId, currentEndpointGeneration(endpointId) + 1)
  const prefix = `${endpointId}:`
  for (const key of [...entries.keys()]) {
    if (key.startsWith(prefix)) entries.delete(key)
  }
  for (const key of [...inFlight.keys()]) {
    if (key.startsWith(prefix)) inFlight.delete(key)
  }
}

export function peekReadCacheKeys(): string[] {
  return [...entries.keys()]
}

/** A revoked host must never be served from memory; the gate state is part of a hit. */
function cachedValue<T>(key: string): T | undefined {
  const entry = entries.get(key)
  if (!entry) return undefined
  if (Date.now() >= entry.expiresAt) return undefined
  if (entry.hostState !== hostAccessState()) return undefined
  return entry.value as T
}

/**
 * Single flight plus a per-endpoint TTL for the catalogs several mounted surfaces read on
 * the same turn (composer, command panel, model center, agent center). One key is one
 * request: whoever mounts second joins the first instead of issuing its own.
 *
 * The tier is a property of what the endpoint describes, not a knob to paper over a slow
 * backend: receipts whose answer changes with runtime health keep the short default, while
 * configuration directories opt into the catalog window.
 *
 * The shared read deliberately takes no caller signal — one caller leaving must not cancel
 * data another caller is waiting for — while that caller's own await still rejects on abort.
 */
export function cachedRead<T>(
  key: string,
  read: () => Promise<T>,
  signal?: AbortSignal,
  ttlMs: number = READ_CACHE_TTL_MS,
  scopeOptions?: { endpoint?: EndpointInput; storageId?: string },
): Promise<T> {
  const qualifiedKey = qualifyReadCacheKey(key, scopeOptions)
  const hit = cachedValue<T>(qualifiedKey)
  if (hit !== undefined) return Promise.resolve(hit)
  let request = inFlight.get(qualifiedKey) as Promise<T> | undefined
  if (!request) {
    const endpointId = endpointPrefixOfKey(qualifiedKey)
    const startedIn = generation
    const startedEndpointGen = currentEndpointGeneration(endpointId)
    const created = read().then((value) => {
      if (startedIn === generation && startedEndpointGen === currentEndpointGeneration(endpointId)) {
        entries.set(qualifiedKey, { expiresAt: Date.now() + ttlMs, hostState: hostAccessState(), value })
      }
      return value
    }).finally(() => {
      if (inFlight.get(qualifiedKey) === created) inFlight.delete(qualifiedKey)
    })
    request = created
    inFlight.set(qualifiedKey, created)
  }
  if (!signal) return request
  if (signal.aborted) return Promise.reject(signal.reason ?? abortError())
  return Promise.race([request, rejectionOnAbort(signal)])
}

function rejectionOnAbort(signal: AbortSignal): Promise<never> {
  return new Promise<never>((_resolve, reject) => {
    signal.addEventListener('abort', () => reject(signal.reason ?? abortError()), { once: true })
  })
}

function abortError(): Error {
  const error = new Error('This operation was aborted')
  error.name = 'AbortError'
  return error
}

// A host gate change (revocation, restart requirement, preview) retires every projection.
onHostAccessChanged(() => invalidateReadCache())
