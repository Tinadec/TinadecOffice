import { ref, type Ref } from 'vue'
import { resolveWebGatewayBase, toWebSocketUrl } from './hostConnection'

export type EndpointScope =
  | { readonly kind: 'local' }
  | { readonly kind: 'peer'; readonly deviceId: string }

export type EndpointId = 'local' | `peer:${string}`

export type EndpointInput = EndpointScope | EndpointId | null | undefined

export const LOCAL_ENDPOINT_SCOPE: EndpointScope = Object.freeze({ kind: 'local' })
export const LOCAL_ENDPOINT_ID: EndpointId = 'local'

export interface EndpointEventCursorSnapshot {
  watermarks: Record<string, string>
  lastFrameId: string
  afterSeq: number | null
}

/** Reactive active endpoint scope for the workbench (defaults to local). */
export const activeEndpointScope: Ref<EndpointScope> = ref<EndpointScope>(LOCAL_ENDPOINT_SCOPE)

let callEndpointScope: EndpointScope | undefined

const endpointEventCursors = new Map<string, EndpointEventCursorSnapshot>()

export function normalizeEndpointScope(input?: EndpointInput): EndpointScope {
  if (input == null || input === 'local') return LOCAL_ENDPOINT_SCOPE
  if (typeof input === 'string') {
    if (!input.startsWith('peer:')) {
      throw new Error(`Invalid endpoint identifier: "${input}". Expected "local" or "peer:<deviceId>".`)
    }
    const deviceId = input.slice('peer:'.length).trim()
    if (!deviceId) {
      throw new Error('Peer endpoint deviceId must not be empty.')
    }
    return { kind: 'peer', deviceId }
  }
  if (input.kind === 'local') return LOCAL_ENDPOINT_SCOPE
  if (input.kind === 'peer') {
    const deviceId = typeof input.deviceId === 'string' ? input.deviceId.trim() : ''
    if (!deviceId) {
      throw new Error('Peer endpoint deviceId must not be empty.')
    }
    return { kind: 'peer', deviceId }
  }
  return LOCAL_ENDPOINT_SCOPE
}

export function toEndpointId(input?: EndpointInput): EndpointId {
  const scope = normalizeEndpointScope(input)
  return scope.kind === 'local' ? LOCAL_ENDPOINT_ID : `peer:${scope.deviceId}`
}

export function parseEndpointId(endpointId?: string | null): EndpointScope {
  return normalizeEndpointScope((endpointId ?? undefined) as EndpointInput)
}

export function isPeerEndpoint(input?: EndpointInput): boolean {
  return normalizeEndpointScope(input).kind === 'peer'
}

export function peerDeviceIdOf(input?: EndpointInput): string | null {
  const scope = normalizeEndpointScope(input)
  return scope.kind === 'peer' ? scope.deviceId : null
}

export function setActiveEndpoint(endpoint: EndpointInput): EndpointScope {
  const normalized = normalizeEndpointScope(endpoint)
  activeEndpointScope.value = normalized
  return normalized
}

export function currentEndpointScope(explicit?: EndpointInput): EndpointScope {
  if (explicit !== undefined && explicit !== null) return normalizeEndpointScope(explicit)
  if (callEndpointScope !== undefined) return callEndpointScope
  return activeEndpointScope.value
}

export function currentEndpointId(explicit?: EndpointInput): EndpointId {
  return toEndpointId(currentEndpointScope(explicit))
}

export function withEndpointScope<T>(endpoint: EndpointInput, fn: () => T): T {
  const previous = callEndpointScope
  callEndpointScope = normalizeEndpointScope(endpoint)
  try {
    return fn()
  } finally {
    callEndpointScope = previous
  }
}

/**
 * Zero-intrusion proxy wrapper mirroring `scopedApi(api, getStorage)`:
 * captures the caller's endpoint synchronously at call time without mutating global state.
 */
export function endpointScopedApi<T extends object>(api: T, getEndpoint: () => EndpointInput): T {
  return new Proxy(api, {
    get(target, key, receiver) {
      const value = Reflect.get(target, key, receiver)
      if (typeof value !== 'function') return value
      return (...args: unknown[]) => withEndpointScope(getEndpoint(), () => Reflect.apply(value, target, args))
    },
  })
}

/**
 * Local management & host-auth endpoints NEVER proxy through `/api/v1/peers/:id/proxy/*`,
 * and already-rewritten `/api/v1/peers/:id/proxy/*` paths are NEVER double-proxied.
 */
export function isLocalManagementApiPath(path: string): boolean {
  return /^\/api\/v1\/(?:peers|host-challenge|web-auth)(?:\/|\?|$)/.test(path)
}

/**
 * Unwraps `/api/v1/peers/:deviceId/proxy/api/v1/...` back into its peer scope and inner `/api/v1/...` path
 * so storage scope capture and entity registration behave identically before and after rewriting.
 */
export function unwrapPeerProxyPath(path: string): { endpointScope: EndpointScope; innerPath: string } {
  const match = /^\/api\/v1\/peers\/([^/?]+)\/proxy(\/api\/v1(?:\/.*|\?.*|$))$/.exec(path)
  if (!match) {
    return { endpointScope: LOCAL_ENDPOINT_SCOPE, innerPath: path }
  }
  return {
    endpointScope: { kind: 'peer', deviceId: decodeURIComponent(match[1]!) },
    innerPath: match[2]!,
  }
}

/**
 * Renderer requests MUST always target the local Gateway origin (`127.0.0.1:48730` or `resolveWebGatewayBase()`).
 * Direct HTTP/WS connections to remote peer device IPs from the renderer are strictly forbidden.
 */
export function resolveLocalGatewayBase(explicitGatewayBase?: string): string {
  const raw = (explicitGatewayBase ?? resolveWebGatewayBase()).trim().replace(/\/+$/, '')
  return raw || 'http://127.0.0.1:48730'
}

function extractRelativePath(pathOrUrl: string, allowedOrigin?: string): string {
  if (/^[a-zA-Z][a-zA-Z\d+.-]*:\/\//.test(pathOrUrl)) {
    const parsed = new URL(pathOrUrl)
    const expectedOrigin = new URL(resolveLocalGatewayBase(allowedOrigin)).origin
    if (parsed.origin !== expectedOrigin) {
      throw new Error(
        `Security boundary violation: renderer cannot connect directly to remote origin "${parsed.origin}" (expected local Gateway "${expectedOrigin}").`,
      )
    }
    return `${parsed.pathname}${parsed.search}`
  }
  return pathOrUrl.startsWith('/') ? pathOrUrl : `/${pathOrUrl}`
}

/**
 * Rewrites business `/api/v1/...` paths to `/api/v1/peers/:deviceId/proxy/api/v1/...` when targeting a peer,
 * while keeping local management routes (`/api/v1/peers/*`, `/api/v1/host-challenge*`, `/api/v1/web-auth*`) local.
 */
export function rewriteApiPathForEndpoint(
  path: string,
  endpoint?: EndpointInput,
  options?: { gatewayBase?: string },
): string {
  const relative = extractRelativePath(path, options?.gatewayBase)
  const scope = currentEndpointScope(endpoint)
  if (scope.kind === 'local') return relative
  if (isLocalManagementApiPath(relative)) return relative
  if (/^\/api\/v1(?:\/|\?|$)/.test(relative)) {
    return `/api/v1/peers/${encodeURIComponent(scope.deviceId)}/proxy${relative}`
  }
  return relative
}

/**
 * Rewrites interactive terminal WebSocket path `/ws/terminal` to `/ws/peers/:deviceId/terminal` when targeting a peer.
 */
export function rewriteTerminalWsPathForEndpoint(
  path: string = '/ws/terminal',
  endpoint?: EndpointInput,
  options?: { gatewayBase?: string },
): string {
  const normalizedInput = path.startsWith('?') ? `/ws/terminal${path}` : path
  const relative = extractRelativePath(normalizedInput, options?.gatewayBase)
  const parsed = new URL(relative, 'http://tinadec.local')
  const scope = currentEndpointScope(endpoint)
  if (scope.kind === 'local') {
    const unwound = /^\/ws\/peers\/[^/]+\/terminal$/.test(parsed.pathname) ? '/ws/terminal' : parsed.pathname
    return `${unwound}${parsed.search}`
  }
  if (/^\/ws\/peers\/[^/]+\/terminal$/.test(parsed.pathname)) {
    return `/ws/peers/${encodeURIComponent(scope.deviceId)}/terminal${parsed.search}`
  }
  if (parsed.pathname === '/ws/terminal') {
    return `/ws/peers/${encodeURIComponent(scope.deviceId)}/terminal${parsed.search}`
  }
  return `${parsed.pathname}${parsed.search}`
}

export function resolveEndpointHttpUrl(
  path: string,
  options?: { endpoint?: EndpointInput; gatewayBase?: string },
): string {
  const base = resolveLocalGatewayBase(options?.gatewayBase)
  const rewritten = rewriteApiPathForEndpoint(path, options?.endpoint, { gatewayBase: base })
  return `${base}${rewritten}`
}

export function resolveEndpointTerminalWsUrl(options?: {
  path?: string
  endpoint?: EndpointInput
  gatewayBase?: string
}): string {
  const base = resolveLocalGatewayBase(options?.gatewayBase)
  const wsPath = rewriteTerminalWsPathForEndpoint(options?.path ?? '/ws/terminal', options?.endpoint, {
    gatewayBase: base,
  })
  return toWebSocketUrl(base, wsPath)
}

/**
 * Builds an endpoint + storage isolated cache key guaranteed to start with `${endpointId}:${storageId}:`.
 */
export function buildEndpointReadCacheKey(
  rawKey: string,
  options: { endpoint?: EndpointInput; storageId: string },
): string {
  const endpointId = currentEndpointId(options.endpoint)
  const storageId = options.storageId || 'user'
  if (rawKey.startsWith(`${endpointId}:${storageId}:`)) {
    return rawKey
  }
  if (rawKey.startsWith(`${storageId}:`)) {
    return `${endpointId}:${rawKey}`
  }
  return `${endpointId}:${storageId}:${rawKey}`
}

function makeCursorStoreKey(endpoint: EndpointInput, storageId: string, streamKey: string): string {
  const endpointId = currentEndpointId(endpoint)
  return `${endpointId}:${storageId || 'user'}:${streamKey}`
}

export function readEndpointEventCursor(
  endpoint: EndpointInput,
  storageId: string,
  streamKey: string,
): EndpointEventCursorSnapshot | undefined {
  const snap = endpointEventCursors.get(makeCursorStoreKey(endpoint, storageId, streamKey))
  if (!snap) return undefined
  return {
    watermarks: { ...snap.watermarks },
    lastFrameId: snap.lastFrameId,
    afterSeq: snap.afterSeq,
  }
}

export function writeEndpointEventCursor(
  endpoint: EndpointInput,
  storageId: string,
  streamKey: string,
  snapshot: EndpointEventCursorSnapshot,
): void {
  endpointEventCursors.set(makeCursorStoreKey(endpoint, storageId, streamKey), {
    watermarks: { ...snapshot.watermarks },
    lastFrameId: snapshot.lastFrameId,
    afterSeq: snapshot.afterSeq,
  })
}

export function clearEndpointEventCursors(endpoint?: EndpointInput): void {
  if (endpoint === undefined || endpoint === null) {
    endpointEventCursors.clear()
    return
  }
  const prefix = `${toEndpointId(endpoint)}:`
  for (const key of [...endpointEventCursors.keys()]) {
    if (key.startsWith(prefix)) {
      endpointEventCursors.delete(key)
    }
  }
}

export function newClientRequestId(): string {
  return (globalThis.crypto as Crypto | undefined)?.randomUUID?.()
    ?? `${Date.now().toString(16)}-${Math.random().toString(16).slice(2, 10)}-${Math.random().toString(16).slice(2, 10)}`
}

export function resetEndpointScopeForTests(): void {
  activeEndpointScope.value = LOCAL_ENDPOINT_SCOPE
  callEndpointScope = undefined
  endpointEventCursors.clear()
}
