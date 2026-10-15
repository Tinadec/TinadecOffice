import { api, type SessionDto } from '@/api'
import i18n from '@/i18n'
import { selectionKey } from './storageScope'
import { isAbortError } from './isAbortError'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'

export interface SessionSource { storageId: string; projectId?: string; key?: string }
export interface SessionRosterFailure { source: SessionSource; error: ErrorState }
export interface SessionRoster<T> { rows: T[]; failures: SessionRosterFailure[] }

/** Independent reads preserve the last known records of failed scopes, never inventing emptiness. */
export async function readSessionRoster<T extends { id: string; storage_id?: string; project_id?: string | null }>(options: {
  sources: SessionSource[]
  read: (source: SessionSource, signal?: AbortSignal) => Promise<T[]>
  previous?: T[]
  signal?: AbortSignal
}): Promise<SessionRoster<T>> {
  options.signal?.throwIfAborted()
  const sources = [...new Map(options.sources.map(source => [source.storageId + '::' + (source.projectId ?? ''), source])).values()]
  const results = await Promise.allSettled(sources.map(source => options.read(source, options.signal)))
  options.signal?.throwIfAborted()
  const rows: T[] = [], failures: SessionRosterFailure[] = []
  results.forEach((result, index) => {
    const source = sources[index]!
    if (result.status === 'fulfilled') rows.push(...result.value.map(row => ({ ...row, storage_id: row.storage_id ?? source.storageId })))
    else {
      if (isAbortError(result.reason)) throw result.reason
      failures.push({ source, error: toErrorState(result.reason, i18n.global.t('home.sessionReadFailed')) })
      rows.push(...(options.previous ?? []).filter(row => (row.storage_id ?? 'user') === source.storageId && (source.projectId === undefined || row.project_id === source.projectId)))
    }
  })
  return { rows: [...new Map(rows.map(row => [selectionKey(row), row])).values()], failures }
}

/** Controlled cross-scope read: caller lifecycle and each storage identity are explicit. */
export async function loadSessionCatalog(options: {
  lifecycleStatus?: 'active' | 'archived' | 'trashed'
  signal?: AbortSignal
  previous?: SessionDto[]
} = {}): Promise<SessionRoster<SessionDto>> {
  const scopes = await api.listStorageScopes(options.signal)
  return readSessionRoster({
    sources: scopes.map(scope => ({ storageId: scope.storage_id, key: scope.storage_id })),
    previous: options.previous, signal: options.signal,
    read: (source, signal) => api.listSessions(undefined, signal, source.storageId, options.lifecycleStatus ?? 'active'),
  })
}
