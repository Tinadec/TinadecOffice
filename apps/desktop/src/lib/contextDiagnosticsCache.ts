/**
 * Scoped local snapshot cache for P1 Core context diagnostics (`RunContextDiagnosticsDto`)
 * and cross-source overlap audits (`ContextOverlapAuditDto`).
 *
 * Resilience guarantee:
 * - Persists the latest successful snapshots in memory + bounded `localStorage` fallback
 *   scoped by storage identity (`storageId`).
 * - When Core or Gateway transiently disconnects, restarts, or returns 5xx/unreachable
 *   errors, UI views degrade gracefully to the cached snapshot (`stale: true`) instead
 *   of rendering a blank screen or crashing, keeping node drilldown and single-file
 *   offline HTML export (`offlineDiagnosticReport.ts`) 100% functional.
 * - Clears memory + `localStorage` entries immediately if web auth is explicitly
 *   revoked (`hostAccessState() === 'auth_required'`).
 */

import type { ContextOverlapAuditDto, RunContextDiagnosticsDto } from '@/api'
import { hostAccessState, onHostAccessChanged } from '@/lib/hostAccess'
import { projectStorageId, runStorageId, selectedStorageId, selectionIdentity } from '@/lib/storageScope'

const STORAGE_PREFIX_RUN = 'tinadec.ctxDiag.run.v1:'
const STORAGE_PREFIX_OVERLAP = 'tinadec.ctxDiag.overlap.v1:'
const STORAGE_INDEX_KEY = 'tinadec.ctxDiag.index.v1'

const MAX_RUN_SNAPSHOTS = 24
const MAX_OVERLAP_SNAPSHOTS = 16

export interface CachedDiagnosticsSnapshot<T> {
  key: string
  storageId: string
  cachedAt: string
  data: T
}

export interface ResilientDiagnosticsFetchResult<T> {
  data: T | null
  fromCache: boolean
  stale: boolean
  cachedAt: string | null
  error: string | null
}

const runMemoryCache = new Map<string, CachedDiagnosticsSnapshot<RunContextDiagnosticsDto>>()
const overlapMemoryCache = new Map<string, CachedDiagnosticsSnapshot<ContextOverlapAuditDto>>()

function safeLocalStorage(): Storage | null {
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      return window.localStorage
    }
    if (typeof globalThis !== 'undefined' && 'localStorage' in globalThis && globalThis.localStorage) {
      return globalThis.localStorage
    }
  } catch {
    // Ignore sandboxed / disabled storage environments.
  }
  return null
}

function isAuthRevoked(): boolean {
  return hostAccessState() === 'auth_required'
}

function normalizeId(raw?: string | null): string {
  if (!raw) return ''
  return selectionIdentity(raw.trim()).id
}

function resolveRunScopeStorageId(runId: string, explicitStorageId?: string | null): string {
  if (explicitStorageId && explicitStorageId.trim()) return explicitStorageId.trim()
  const split = selectionIdentity(runId)
  if (split.storageId) return split.storageId
  return runStorageId(split.id) || selectedStorageId() || 'user'
}

function resolveOverlapScopeStorageId(
  params?: { projectId?: string | null; runId?: string | null; storageId?: string | null },
): string {
  if (params?.storageId && params.storageId.trim()) return params.storageId.trim()
  if (params?.runId) return resolveRunScopeStorageId(params.runId)
  if (params?.projectId) return projectStorageId(params.projectId)
  return selectedStorageId() || 'user'
}

function makeCompositeKey(storageId: string, entityKey: string): string {
  return `${storageId}::${entityKey}`
}

interface PersistedIndex {
  runs: string[]
  overlaps: string[]
}

function readIndex(storage: Storage): PersistedIndex {
  try {
    const raw = storage.getItem(STORAGE_INDEX_KEY)
    if (!raw) return { runs: [], overlaps: [] }
    const parsed = JSON.parse(raw) as Partial<PersistedIndex>
    return {
      runs: Array.isArray(parsed.runs) ? parsed.runs.filter((k): k is string => typeof k === 'string') : [],
      overlaps: Array.isArray(parsed.overlaps) ? parsed.overlaps.filter((k): k is string => typeof k === 'string') : [],
    }
  } catch {
    return { runs: [], overlaps: [] }
  }
}

function writeIndex(storage: Storage, index: PersistedIndex): void {
  try {
    storage.setItem(STORAGE_INDEX_KEY, JSON.stringify(index))
  } catch {
    // Ignore quota errors.
  }
}

function persistSnapshot<T>(
  prefix: string,
  kind: 'runs' | 'overlaps',
  maxEntries: number,
  entry: CachedDiagnosticsSnapshot<T>,
): void {
  const storage = safeLocalStorage()
  if (!storage) return
  try {
    storage.setItem(`${prefix}${entry.key}`, JSON.stringify(entry))
    const index = readIndex(storage)
    const list = index[kind].filter((k) => k !== entry.key)
    list.push(entry.key)
    while (list.length > maxEntries) {
      const evicted = list.shift()
      if (evicted) {
        try {
          storage.removeItem(`${prefix}${evicted}`)
        } catch {
          // Ignore remove failure.
        }
      }
    }
    index[kind] = list
    writeIndex(storage, index)
  } catch {
    // QuotaExceededError or private-mode restriction: in-memory cache still holds the snapshot.
  }
}

function loadPersistedSnapshot<T>(prefix: string, compositeKey: string): CachedDiagnosticsSnapshot<T> | null {
  const storage = safeLocalStorage()
  if (!storage) return null
  try {
    const raw = storage.getItem(`${prefix}${compositeKey}`)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<CachedDiagnosticsSnapshot<T>>
    if (
      !parsed
      || typeof parsed !== 'object'
      || typeof parsed.key !== 'string'
      || typeof parsed.storageId !== 'string'
      || typeof parsed.cachedAt !== 'string'
      || !parsed.data
      || typeof parsed.data !== 'object'
    ) {
      return null
    }
    return parsed as CachedDiagnosticsSnapshot<T>
  } catch {
    return null
  }
}

function touchMemoryLru<T>(
  map: Map<string, CachedDiagnosticsSnapshot<T>>,
  entry: CachedDiagnosticsSnapshot<T>,
  maxEntries: number,
): void {
  map.delete(entry.key)
  map.set(entry.key, entry)
  while (map.size > maxEntries) {
    const oldestKey = map.keys().next().value
    if (oldestKey === undefined) break
    map.delete(oldestKey)
  }
}

export function buildOverlapCacheScopeKey(params: {
  runId?: string | null
  projectId?: string | null
  modeId?: string | null
  agentId?: string | null
  autoLoadMode?: string | null
}): string {
  const runId = normalizeId(params.runId)
  if (runId) return `run:${runId}`
  const parts = [
    `proj:${normalizeId(params.projectId) || 'default'}`,
    `mode:${normalizeId(params.modeId) || 'all'}`,
    `agent:${normalizeId(params.agentId) || 'all'}`,
    `load:${params.autoLoadMode || 'all'}`,
  ]
  return parts.join('|')
}

export function saveCachedRunDiagnostics(
  runId: string,
  data: RunContextDiagnosticsDto,
  options?: { storageId?: string | null; cachedAt?: string },
): CachedDiagnosticsSnapshot<RunContextDiagnosticsDto> | null {
  const cleanRunId = normalizeId(runId)
  if (!cleanRunId || isAuthRevoked()) return null
  const storageId = resolveRunScopeStorageId(runId, options?.storageId)
  const compositeKey = makeCompositeKey(storageId, cleanRunId)
  const entry: CachedDiagnosticsSnapshot<RunContextDiagnosticsDto> = {
    key: compositeKey,
    storageId,
    cachedAt: options?.cachedAt || data.generated_at || new Date().toISOString(),
    data,
  }
  touchMemoryLru(runMemoryCache, entry, MAX_RUN_SNAPSHOTS)
  persistSnapshot(STORAGE_PREFIX_RUN, 'runs', MAX_RUN_SNAPSHOTS, entry)
  return entry
}

export function getCachedRunDiagnostics(
  runId: string | null | undefined,
  options?: { storageId?: string | null },
): CachedDiagnosticsSnapshot<RunContextDiagnosticsDto> | null {
  if (!runId || isAuthRevoked()) return null
  const cleanRunId = normalizeId(runId)
  if (!cleanRunId) return null
  const storageId = resolveRunScopeStorageId(runId, options?.storageId)
  const compositeKey = makeCompositeKey(storageId, cleanRunId)

  const memHit = runMemoryCache.get(compositeKey)
  if (memHit) return memHit

  const diskHit = loadPersistedSnapshot<RunContextDiagnosticsDto>(STORAGE_PREFIX_RUN, compositeKey)
  if (diskHit) {
    touchMemoryLru(runMemoryCache, diskHit, MAX_RUN_SNAPSHOTS)
    return diskHit
  }
  return null
}

export function saveCachedOverlapAudit(
  scopeKey: string,
  data: ContextOverlapAuditDto,
  options?: { storageId?: string | null; projectId?: string | null; runId?: string | null; cachedAt?: string },
): CachedDiagnosticsSnapshot<ContextOverlapAuditDto> | null {
  const trimmedKey = scopeKey.trim()
  if (!trimmedKey || isAuthRevoked()) return null
  const storageId = resolveOverlapScopeStorageId(options)
  const compositeKey = makeCompositeKey(storageId, trimmedKey)
  const entry: CachedDiagnosticsSnapshot<ContextOverlapAuditDto> = {
    key: compositeKey,
    storageId,
    cachedAt: options?.cachedAt || data.generated_at || new Date().toISOString(),
    data,
  }
  touchMemoryLru(overlapMemoryCache, entry, MAX_OVERLAP_SNAPSHOTS)
  persistSnapshot(STORAGE_PREFIX_OVERLAP, 'overlaps', MAX_OVERLAP_SNAPSHOTS, entry)
  return entry
}

export function getCachedOverlapAudit(
  scopeKey: string | null | undefined,
  options?: { storageId?: string | null; projectId?: string | null; runId?: string | null },
): CachedDiagnosticsSnapshot<ContextOverlapAuditDto> | null {
  if (!scopeKey || isAuthRevoked()) return null
  const trimmedKey = scopeKey.trim()
  if (!trimmedKey) return null
  const storageId = resolveOverlapScopeStorageId(options)
  const compositeKey = makeCompositeKey(storageId, trimmedKey)

  const memHit = overlapMemoryCache.get(compositeKey)
  if (memHit) return memHit

  const diskHit = loadPersistedSnapshot<ContextOverlapAuditDto>(STORAGE_PREFIX_OVERLAP, compositeKey)
  if (diskHit) {
    touchMemoryLru(overlapMemoryCache, diskHit, MAX_OVERLAP_SNAPSHOTS)
    return diskHit
  }
  return null
}

function isAbortError(error: unknown): boolean {
  return Boolean(
    error
    && typeof error === 'object'
    && ('name' in error && (error as { name?: string }).name === 'AbortError'),
  )
}

function formatErrorReason(error: unknown): string {
  if (error instanceof Error && error.message) return error.message
  return String(error ?? 'Backend unavailable')
}

/**
 * Fetch authoritative `RunContextDiagnosticsDto` from Core, persisting on success
 * and gracefully falling back to the last cached snapshot on disconnect/transient failure.
 */
export async function fetchRunDiagnosticsWithCache(
  runId: string,
  fetcher: () => Promise<RunContextDiagnosticsDto>,
  options?: { storageId?: string | null; canAccessBackend?: boolean },
): Promise<ResilientDiagnosticsFetchResult<RunContextDiagnosticsDto>> {
  if (options?.canAccessBackend === false) {
    const cached = getCachedRunDiagnostics(runId, { storageId: options.storageId })
    return cached
      ? {
          data: cached.data,
          fromCache: true,
          stale: true,
          cachedAt: cached.cachedAt,
          error: 'Backend offline',
        }
      : { data: null, fromCache: false, stale: false, cachedAt: null, error: 'Backend offline' }
  }

  try {
    const live = await fetcher()
    const saved = saveCachedRunDiagnostics(runId, live, { storageId: options?.storageId })
    return {
      data: live,
      fromCache: false,
      stale: false,
      cachedAt: saved?.cachedAt ?? live.generated_at ?? new Date().toISOString(),
      error: null,
    }
  } catch (error) {
    if (isAbortError(error)) throw error
    const reason = formatErrorReason(error)
    const cached = getCachedRunDiagnostics(runId, { storageId: options?.storageId })
    if (cached) {
      return {
        data: cached.data,
        fromCache: true,
        stale: true,
        cachedAt: cached.cachedAt,
        error: reason,
      }
    }
    return {
      data: null,
      fromCache: false,
      stale: false,
      cachedAt: null,
      error: reason,
    }
  }
}

/**
 * Fetch authoritative `ContextOverlapAuditDto` from Core, persisting on success
 * and gracefully falling back to the last cached snapshot on disconnect/transient failure.
 */
export async function fetchOverlapAuditWithCache(
  scopeKey: string,
  fetcher: () => Promise<ContextOverlapAuditDto>,
  options?: {
    storageId?: string | null
    projectId?: string | null
    runId?: string | null
    canAccessBackend?: boolean
  },
): Promise<ResilientDiagnosticsFetchResult<ContextOverlapAuditDto>> {
  if (options?.canAccessBackend === false) {
    const cached = getCachedOverlapAudit(scopeKey, options)
    return cached
      ? {
          data: cached.data,
          fromCache: true,
          stale: true,
          cachedAt: cached.cachedAt,
          error: 'Backend offline',
        }
      : { data: null, fromCache: false, stale: false, cachedAt: null, error: 'Backend offline' }
  }

  try {
    const live = await fetcher()
    const saved = saveCachedOverlapAudit(scopeKey, live, options)
    return {
      data: live,
      fromCache: false,
      stale: false,
      cachedAt: saved?.cachedAt ?? live.generated_at ?? new Date().toISOString(),
      error: null,
    }
  } catch (error) {
    if (isAbortError(error)) throw error
    const reason = formatErrorReason(error)
    const cached = getCachedOverlapAudit(scopeKey, options)
    if (cached) {
      return {
        data: cached.data,
        fromCache: true,
        stale: true,
        cachedAt: cached.cachedAt,
        error: reason,
      }
    }
    return {
      data: null,
      fromCache: false,
      stale: false,
      cachedAt: null,
      error: reason,
    }
  }
}

export function clearContextDiagnosticsCache(): void {
  runMemoryCache.clear()
  overlapMemoryCache.clear()
  const storage = safeLocalStorage()
  if (!storage) return
  try {
    const index = readIndex(storage)
    for (const k of index.runs) storage.removeItem(`${STORAGE_PREFIX_RUN}${k}`)
    for (const k of index.overlaps) storage.removeItem(`${STORAGE_PREFIX_OVERLAP}${k}`)
    storage.removeItem(STORAGE_INDEX_KEY)
  } catch {
    // Ignore storage cleanup errors.
  }
}

// Only purge persisted snapshots when web authentication is explicitly revoked/expired,
// NEVER on transient backend disconnect (`verifying` / `error`), so offline viewing survives!
onHostAccessChanged(() => {
  if (hostAccessState() === 'auth_required') {
    clearContextDiagnosticsCache()
  }
})
