import { assertHostAccess } from './hostAccess'
/** Scope identity is captured before I/O. A later project selection cannot retarget it. */
import { ref } from 'vue'
import { currentEndpointId, toEndpointId, unwrapPeerProxyPath, type EndpointId, type EndpointInput } from './endpointScope'
export interface StorageRequestOptions extends RequestInit {
  storageId?: string
  endpointId?: EndpointInput
}
const projects = new Map<string, Set<string>>()
const sessions = new Map<string, Set<string>>()
const runs = new Map<string, Set<string>>()
export const selectedStorage = ref('user')
let callStorage: string | undefined
export function setSelectedStorage(storageId: string) { selectedStorage.value = storageId }
export function selectedStorageId() { return selectedStorage.value }
export function activeStorageScopeId() { return callStorage ?? selectedStorage.value }
function entityRegistryKey(id: string, endpoint?: EndpointInput): string {
  return `${currentEndpointId(endpoint)}:${id}`
}
function register(map: Map<string, Set<string>>, id: string, scope: string, endpoint?: EndpointInput) {
  const key = entityRegistryKey(id, endpoint)
  const scopes = map.get(key) ?? new Set<string>()
  scopes.add(scope)
  map.set(key, scopes)
}
export function selectionKey(row: { id: string; storage_id?: unknown }) { return typeof row.storage_id === 'string' && row.storage_id ? `${row.storage_id}::${row.id}` : row.id }
export function selectionIdentity(value: string) { const split = value.indexOf('::'); return split < 0 ? { id: value, storageId: undefined } : { id: value.slice(split + 2), storageId: value.slice(0, split) } }
function resolve(map: Map<string, Set<string>>, value: string, fallback: string, endpoint?: EndpointInput) {
  const identity = selectionIdentity(value)
  if (identity.storageId) return identity.storageId
  const candidates = map.get(entityRegistryKey(identity.id, endpoint))
  return candidates?.has(selectedStorage.value) ? selectedStorage.value : candidates?.values().next().value ?? fallback
}
export function registerProjectStorage(projectId: string, storageId: string, endpoint?: EndpointInput) { register(projects, projectId, storageId, endpoint) }
export function projectStorageId(projectId?: string | null, endpoint?: EndpointInput) { return projectId ? resolve(projects, projectId, selectedStorage.value, endpoint) : 'user' }
export function registerSessionStorage(sessionId: string, storageId: string, endpoint?: EndpointInput) { register(sessions, sessionId, storageId, endpoint) }
export function sessionStorageId(sessionId?: string | null, endpoint?: EndpointInput) { return sessionId ? resolve(sessions, sessionId, selectedStorage.value, endpoint) : selectedStorage.value }
export function runStorageId(runId: string, endpoint?: EndpointInput) { return resolve(runs, runId, selectedStorage.value, endpoint) }

export function clearEndpointStorageScope(endpoint: EndpointInput): void {
  const prefix = `${toEndpointId(endpoint)}:`
  for (const map of [projects, sessions, runs]) {
    for (const key of [...map.keys()]) {
      if (key.startsWith(prefix)) map.delete(key)
    }
  }
}

export function captureEndpointId(path?: string, options?: StorageRequestOptions): EndpointId {
  if (options?.endpointId !== undefined && options.endpointId !== null) {
    return currentEndpointId(options.endpointId)
  }
  if (path) {
    const unwrapped = unwrapPeerProxyPath(path)
    if (unwrapped.endpointScope.kind === 'peer') {
      return toEndpointId(unwrapped.endpointScope)
    }
  }
  return currentEndpointId()
}

/** Composite UI keys never enter Core's product identifiers. */
export function normalizeStorageRequest(path: string, options?: StorageRequestOptions) {
  const url = new URL(path, 'http://tinadec.local')
  url.pathname = url.pathname.split('/').map(part => {
    const identity = selectionIdentity(decodeURIComponent(part))
    return identity.storageId ? encodeURIComponent(identity.id) : part
  }).join('/')
  for (const [key, value] of url.searchParams) if (key.endsWith('_id') && selectionIdentity(value).storageId) url.searchParams.set(key, selectionIdentity(value).id)
  let body = options?.body
  if (typeof body === 'string') {
    try {
      const parsed = JSON.parse(body)
      for (const key of ['project_id', 'session_id', 'run_id']) if (typeof parsed[key] === 'string') parsed[key] = selectionIdentity(parsed[key]).id
      body = JSON.stringify(parsed)
    } catch { /* plain request text */ }
  }
  return { path: url.pathname + url.search, body }
}

export function captureStorageId(path: string, options?: StorageRequestOptions): string {
  if (options?.storageId) return options.storageId
  if (callStorage) return callStorage
  const unwrapped = unwrapPeerProxyPath(path)
  const innerPath = unwrapped.innerPath
  const endpoint = options?.endpointId ?? (unwrapped.endpointScope.kind === 'peer' ? unwrapped.endpointScope : undefined)
  const url = new URL(innerPath, 'http://tinadec.local')
  const projectId = url.searchParams.get('project_id')
  if (projectId) return projectStorageId(projectId, endpoint)
  const querySession = url.searchParams.get('session_id')
  if (querySession) return sessionStorageId(querySession, endpoint)
  const queryRun = url.searchParams.get('run_id')
  if (queryRun) return runStorageId(queryRun, endpoint)
  const pathProjectId = innerPath.match(/^\/api\/v1\/projects\/([^/?]+)/)?.[1]
  if (pathProjectId) return projectStorageId(decodeURIComponent(pathProjectId), endpoint)
  const sessionId = innerPath.match(/^\/api\/v1\/sessions\/([^/?]+)/)?.[1]
  if (sessionId) return sessionStorageId(decodeURIComponent(sessionId), endpoint)
  const runId = innerPath.match(/^\/api\/v1\/runs\/([^/?]+)/)?.[1]
  if (runId) return runStorageId(decodeURIComponent(runId), endpoint)
  if (typeof options?.body === 'string') {
    try {
      const body = JSON.parse(options.body)
      if (body.project_id) return projectStorageId(body.project_id, endpoint)
      if (body.session_id) return sessionStorageId(body.session_id, endpoint)
      if (body.run_id) return runStorageId(body.run_id, endpoint)
    } catch { /* non JSON payload */ }
  }
  // Unbound rosters default to user; explicit bindings and entity identities always win.
  if (/^\/api\/v1\/(storage\/|projects(?:\?|$)|sessions(?:\?|$))/.test(innerPath) && (!options?.method || options.method === 'GET')) return 'user'
  return selectedStorage.value
}
export function storageHeaders(path: string, options?: StorageRequestOptions): Headers {
  const headers = new Headers(options?.headers)
  const endpointId = captureEndpointId(path, options)
  headers.set('x-tinadec-storage-id', captureStorageId(path, { ...options, endpointId }))
  headers.set('x-tinadec-endpoint-id', endpointId)
  return headers
}
export async function storageFetch(input: RequestInfo | URL, options?: StorageRequestOptions): Promise<Response> {
  const url = new URL(input instanceof Request ? input.url : String(input), 'http://tinadec.local')
  const headers = storageHeaders(url.pathname + url.search, options)
  await assertHostAccess(url.pathname + url.search, options?.signal ?? undefined)
  return fetch(input, { ...options, headers })
}
export function rememberStorageResult(path: string, data: unknown, storageId: string, endpoint?: EndpointInput) {
  const unwrapped = unwrapPeerProxyPath(path)
  const innerPath = unwrapped.innerPath
  const resolvedEndpoint = endpoint ?? (unwrapped.endpointScope.kind === 'peer' ? unwrapped.endpointScope : undefined)
  for (const item of Array.isArray(data) ? data : [data]) {
    if (!item || typeof item !== 'object') continue
    const row = item as Record<string, unknown>
    const scope = typeof row.storage_id === 'string' ? row.storage_id : storageId
    if (typeof row.project_id === 'string' && typeof row.storage_id === 'string') registerProjectStorage(row.project_id, scope, resolvedEndpoint)
    if (typeof row.id === 'string') {
      if (innerPath.startsWith('/api/v1/projects')) registerProjectStorage(row.id, scope, resolvedEndpoint)
      else if (innerPath.startsWith('/api/v1/sessions') && !innerPath.includes('/interactions')) registerSessionStorage(row.id, scope, resolvedEndpoint)
      else if (innerPath.startsWith('/api/v1/runs')) register(runs, row.id, scope, resolvedEndpoint)
    }
    if (typeof row.run_id === 'string') register(runs, row.run_id, scope, resolvedEndpoint)
  }
}

/** Wrappers capture the scope at call time; request builders consume it synchronously. */
export function scopedApi<T extends object>(api: T, getStorage: () => string): T {
  return new Proxy(api, { get(target, key, receiver) {
    const value = Reflect.get(target, key, receiver)
    if (typeof value !== 'function') return value
    return (...args: unknown[]) => {
      const previous = callStorage
      callStorage = getStorage()
      try { return Reflect.apply(value, target, args) } finally { callStorage = previous }
    }
  } })
}
