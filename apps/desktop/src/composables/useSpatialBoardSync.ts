import { computed, getCurrentScope, onScopeDispose, ref, shallowRef, toValue, watch, type MaybeRefOrGetter } from 'vue'
import {
  SpatialMultiplayerDocument,
  alignRects,
  computeBoundingSectionRect,
  computeSmartGuides,
  distributeRects,
  generateKeyBetween,
  type ISpatialSyncAdapter,
  type SmartGuideLine,
  type SpatialCollaboratorPresence,
  type SpatialDocObject,
  type SpatialLwwTimestamp,
  type SpatialObjectKind,
  type SpatialPropertyOp,
  type SpatialRect,
  type SpatialSyncFrame,
} from '@tinadec/ui'
import { resolveWebGatewayBase, toWebSocketUrl } from '@/lib/hostConnection'
import { sessionStorageId } from '@/lib/storageScope'
import {
  SPATIAL_WHITEBOARD_DEFAULTS,
  extractMentions,
  type SpatialAlignMode,
  type SpatialCommentPin,
  type SpatialCommentReply,
  type SpatialConnectorCurve,
  type SpatialDistributeAxis,
  type SpatialDrawingMode,
  type SpatialMediaKind,
  type SpatialPresenceUpdate,
  type SpatialSectionTone,
  type SpatialStickyTone,
  type SpatialSyncStatus,
  type SpatialTextScale,
  type SpatialViewport,
} from '@/components/spatial/spatialWhiteboardTypes'

const STORAGE_PREFIX = 'tinadec:spatial-board:'
const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const PRESENCE_COALESCE_MS = 50
const PING_INTERVAL_MS = 25_000
const INITIAL_RECONNECT_MS = 1_000
const MAX_RECONNECT_MS = 15_000

const COLLABORATOR_PALETTE = [
  '#3b82f6',
  '#8b5cf6',
  '#10b981',
  '#f59e0b',
  '#ec4899',
  '#06b6d4',
  '#f97316',
] as const

const ALL_OBJECT_KINDS: readonly SpatialObjectKind[] = [
  'work',
  'section',
  'sticky',
  'text',
  'drawing',
  'media',
  'connector',
] as const

export interface UseSpatialBoardSyncOptions {
  sessionKey: MaybeRefOrGetter<string>
  displayName?: MaybeRefOrGetter<string>
  color?: string
  onFollowViewport?: (viewport: SpatialViewport) => void
  webSocketFactory?: (url: string) => WebSocket
  fetchImpl?: typeof fetch
  adapter?: ISpatialSyncAdapter
}

interface PersistedBoardBlob {
  seq: number
  objects: Record<string, SpatialDocObject>
  comments: SpatialCommentPin[]
  updatedAtUnixMs: number
}

function createId(prefix: string): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return `${prefix}:${crypto.randomUUID()}`
  }
  return `${prefix}:${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
}

function pickCollaboratorColor(seed: string): string {
  let hash = 0
  for (let i = 0; i < seed.length; i++) {
    hash = (hash * 31 + seed.charCodeAt(i)) | 0
  }
  return COLLABORATOR_PALETTE[Math.abs(hash) % COLLABORATOR_PALETTE.length]!
}

function isRemoteSessionKey(key: string): boolean {
  const trimmed = key.trim()
  if (!trimmed || trimmed.startsWith('draft:')) return false
  return GUID_RE.test(trimmed)
}

function safeStorageGet(key: string): string | null {
  try {
    if (typeof window === 'undefined' || !window.localStorage) return null
    return window.localStorage.getItem(key)
  } catch {
    return null
  }
}

function safeStorageSet(key: string, value: string): void {
  try {
    if (typeof window === 'undefined' || !window.localStorage) return
    window.localStorage.setItem(key, value)
  } catch {
    // Ignore private-mode or storage quota errors.
  }
}

function fromWireTimestamp(raw: unknown): SpatialLwwTimestamp | undefined {
  if (!raw || typeof raw !== 'object') return undefined
  const rec = raw as Record<string, unknown>
  const lamport = Number(rec.lamport)
  const writerId = typeof rec.writer_id === 'string'
    ? rec.writer_id
    : typeof rec.writerId === 'string'
      ? rec.writerId
      : ''
  if (!Number.isFinite(lamport)) return undefined
  return { lamport, writerId }
}

function toWireTimestamp(ts: SpatialLwwTimestamp): { lamport: number; writer_id: string } {
  return { lamport: ts.lamport, writer_id: ts.writerId }
}

function normalizeObjectKind(raw: unknown): SpatialObjectKind {
  if (typeof raw === 'string' && (ALL_OBJECT_KINDS as readonly string[]).includes(raw)) {
    return raw as SpatialObjectKind
  }
  return 'sticky'
}

function fromWireObject(raw: unknown): SpatialDocObject | null {
  if (!raw || typeof raw !== 'object') return null
  const rec = raw as Record<string, unknown>
  const id = typeof rec.id === 'string' ? rec.id : ''
  if (!id) return null

  const rawTimestamps = (rec.prop_timestamps ?? rec.propTimestamps ?? {}) as Record<string, unknown>
  const propTimestamps: Record<string, SpatialLwwTimestamp> = {}
  for (const [k, v] of Object.entries(rawTimestamps)) {
    const ts = fromWireTimestamp(v)
    if (!ts) continue
    if (k === 'parent_id') propTimestamps.parentId = ts
    else if (k === 'sort_key') propTimestamps.sortKey = ts
    else propTimestamps[k] = ts
  }

  const deletedTs = fromWireTimestamp(rec.deleted_ts ?? rec.deletedTs)
  const propsRaw = rec.props && typeof rec.props === 'object' && !Array.isArray(rec.props)
    ? (rec.props as Record<string, unknown>)
    : {}

  return {
    id,
    kind: normalizeObjectKind(rec.kind),
    parentId: typeof rec.parent_id === 'string'
      ? rec.parent_id
      : typeof rec.parentId === 'string'
        ? rec.parentId
        : null,
    sortKey: typeof rec.sort_key === 'string'
      ? rec.sort_key
      : typeof rec.sortKey === 'string'
        ? rec.sortKey
        : generateKeyBetween(null, null),
    x: Number.isFinite(Number(rec.x)) ? Number(rec.x) : 0,
    y: Number.isFinite(Number(rec.y)) ? Number(rec.y) : 0,
    width: Number.isFinite(Number(rec.width)) && Number(rec.width) > 0 ? Number(rec.width) : 220,
    height: Number.isFinite(Number(rec.height)) && Number(rec.height) > 0 ? Number(rec.height) : 160,
    ...(Number.isFinite(Number(rec.rotation)) && Number(rec.rotation) !== 0
      ? { rotation: Number(rec.rotation) }
      : {}),
    props: { ...propsRaw },
    propTimestamps,
    ...(typeof rec.deleted === 'boolean' ? { deleted: rec.deleted } : {}),
    ...(deletedTs ? { deletedTs } : {}),
  }
}

function fromWireObjectsMap(raw: unknown): Record<string, SpatialDocObject> {
  const out: Record<string, SpatialDocObject> = {}
  if (!raw || typeof raw !== 'object') return out
  const items = Array.isArray(raw) ? raw : Object.values(raw as Record<string, unknown>)
  for (const item of items) {
    const obj = fromWireObject(item)
    if (obj) out[obj.id] = obj
  }
  return out
}

function toWireOp(op: SpatialPropertyOp): Record<string, unknown> {
  const timestamps: Record<string, { lamport: number; writer_id: string }> = {}
  for (const [key, ts] of Object.entries(op.timestamps ?? {})) {
    const wireTs = toWireTimestamp(ts)
    timestamps[key] = wireTs
    if (key === 'parentId') timestamps.parent_id = wireTs
    if (key === 'sortKey') timestamps.sort_key = wireTs
  }
  return {
    client_op_id: op.clientOpId,
    object_id: op.objectId,
    ...(op.kind !== undefined ? { kind: op.kind } : {}),
    ...(op.hasParentId || op.parentId !== undefined
      ? { parent_id: op.parentId ?? null, has_parent_id: true }
      : { has_parent_id: false }),
    ...(op.sortKey !== undefined ? { sort_key: op.sortKey } : {}),
    ...(op.x !== undefined ? { x: op.x } : {}),
    ...(op.y !== undefined ? { y: op.y } : {}),
    ...(op.width !== undefined ? { width: op.width } : {}),
    ...(op.height !== undefined ? { height: op.height } : {}),
    ...(op.rotation !== undefined ? { rotation: op.rotation } : {}),
    ...(op.props !== undefined ? { props: op.props } : {}),
    timestamps,
    ...(op.deleted !== undefined ? { deleted: op.deleted } : {}),
    ...(op.deletedTs ? { deleted_ts: toWireTimestamp(op.deletedTs) } : {}),
  }
}

function fromWireCollaborator(raw: unknown): SpatialCollaboratorPresence | null {
  if (!raw || typeof raw !== 'object') return null
  const rec = raw as Record<string, unknown>
  const clientId = typeof rec.client_id === 'string'
    ? rec.client_id
    : typeof rec.clientId === 'string'
      ? rec.clientId
      : ''
  if (!clientId) return null

  const cursorObj = rec.cursor && typeof rec.cursor === 'object' ? (rec.cursor as Record<string, unknown>) : null
  const rawX = cursorObj ? cursorObj.x : (rec.cursor_x ?? rec.cursorX)
  const rawY = cursorObj ? cursorObj.y : (rec.cursor_y ?? rec.cursorY)
  const hasCursor = typeof rawX === 'number' && typeof rawY === 'number' && Number.isFinite(rawX) && Number.isFinite(rawY)

  const vpRaw = rec.viewport && typeof rec.viewport === 'object' ? (rec.viewport as Record<string, unknown>) : null
  const viewport = vpRaw && Number.isFinite(Number(vpRaw.x)) && Number.isFinite(Number(vpRaw.y)) && Number.isFinite(Number(vpRaw.zoom))
    ? { x: Number(vpRaw.x), y: Number(vpRaw.y), zoom: Number(vpRaw.zoom) }
    : null

  return {
    clientId,
    displayName: typeof rec.display_name === 'string'
      ? rec.display_name
      : typeof rec.displayName === 'string'
        ? rec.displayName
        : clientId,
    color: typeof rec.color === 'string' && rec.color ? rec.color : pickCollaboratorColor(clientId),
    role: rec.role === 'ai' ? 'ai' : 'human',
    agentInstanceId: typeof rec.agent_instance_id === 'string'
      ? rec.agent_instance_id
      : typeof rec.agentInstanceId === 'string'
        ? rec.agentInstanceId
        : null,
    status: typeof rec.status === 'string' ? rec.status : undefined,
    cursor: hasCursor ? { x: rawX as number, y: rawY as number } : null,
    cursorX: hasCursor ? (rawX as number) : null,
    cursorY: hasCursor ? (rawY as number) : null,
    selection: Array.isArray(rec.selection) ? rec.selection.filter((s): s is string => typeof s === 'string') : [],
    viewport,
    activeTool: typeof rec.active_tool === 'string'
      ? rec.active_tool
      : typeof rec.activeTool === 'string'
        ? rec.activeTool
        : null,
    followingClientId: typeof rec.following_client_id === 'string'
      ? rec.following_client_id
      : typeof rec.followingClientId === 'string'
        ? rec.followingClientId
        : null,
    updatedAtUnixMs: Number.isFinite(Number(rec.updated_at_unix_ms ?? rec.updatedAtUnixMs))
      ? Number(rec.updated_at_unix_ms ?? rec.updatedAtUnixMs)
      : Date.now(),
  }
}

function fromWireReply(raw: unknown): SpatialCommentReply | null {
  if (!raw || typeof raw !== 'object') return null
  const rec = raw as Record<string, unknown>
  const id = typeof rec.reply_id === 'string' ? rec.reply_id : typeof rec.id === 'string' ? rec.id : ''
  if (!id) return null
  return {
    id,
    authorId: typeof rec.author_id === 'string' ? rec.author_id : typeof rec.authorId === 'string' ? rec.authorId : '',
    authorName: typeof rec.author_name === 'string' ? rec.author_name : typeof rec.authorName === 'string' ? rec.authorName : 'User',
    authorKind: (rec.author_kind ?? rec.authorKind) === 'ai' ? 'ai' : 'human',
    body: typeof rec.body === 'string' ? rec.body : '',
    createdAtUnixMs: Number.isFinite(Number(rec.created_at_unix_ms ?? rec.createdAtUnixMs))
      ? Number(rec.created_at_unix_ms ?? rec.createdAtUnixMs)
      : Date.now(),
  }
}

function fromWireComment(raw: unknown): SpatialCommentPin | null {
  if (!raw || typeof raw !== 'object') return null
  const rec = raw as Record<string, unknown>
  const id = typeof rec.pin_id === 'string' ? rec.pin_id : typeof rec.id === 'string' ? rec.id : ''
  if (!id) return null
  const body = typeof rec.body === 'string' ? rec.body : ''
  const rawMentions = Array.isArray(rec.mentions)
    ? rec.mentions.filter((m): m is string => typeof m === 'string')
    : extractMentions(body)
  const rawReplies = Array.isArray(rec.replies) ? rec.replies : []
  const replies: SpatialCommentReply[] = []
  for (const item of rawReplies) {
    const parsed = fromWireReply(item)
    if (parsed) replies.push(parsed)
  }
  return {
    id,
    objectId: typeof rec.object_id === 'string'
      ? rec.object_id
      : typeof rec.objectId === 'string'
        ? rec.objectId
        : null,
    x: Number.isFinite(Number(rec.x)) ? Number(rec.x) : 0,
    y: Number.isFinite(Number(rec.y)) ? Number(rec.y) : 0,
    authorId: typeof rec.author_id === 'string' ? rec.author_id : typeof rec.authorId === 'string' ? rec.authorId : '',
    authorName: typeof rec.author_name === 'string' ? rec.author_name : typeof rec.authorName === 'string' ? rec.authorName : 'User',
    authorKind: (rec.author_kind ?? rec.authorKind) === 'ai' ? 'ai' : 'human',
    body,
    mentions: rawMentions,
    replies,
    resolved: Boolean(rec.resolved),
    createdAtUnixMs: Number.isFinite(Number(rec.created_at_unix_ms ?? rec.createdAtUnixMs))
      ? Number(rec.created_at_unix_ms ?? rec.createdAtUnixMs)
      : Date.now(),
    updatedAtUnixMs: Number.isFinite(Number(rec.updated_at_unix_ms ?? rec.updatedAtUnixMs))
      ? Number(rec.updated_at_unix_ms ?? rec.updatedAtUnixMs)
      : Date.now(),
  }
}

interface WebSocketAdapterOptions {
  sessionId: string
  clientId: string
  displayName: string
  color: string
  fetchImpl?: typeof fetch
  webSocketFactory?: (url: string) => WebSocket
  onCommentsSnapshot: (pins: SpatialCommentPin[]) => void
  onCommentEvent: (action: string, comment: SpatialCommentPin | null, pinId: string | null) => void
  onStatusChange: (status: SpatialSyncStatus) => void
}

class SpatialWebSocketSyncAdapter implements ISpatialSyncAdapter {
  private ws: WebSocket | null = null
  private closed = false
  private reconnectDelay = INITIAL_RECONNECT_MS
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null
  private pingTimer: ReturnType<typeof setInterval> | null = null
  private readonly handlers = new Set<(frame: SpatialSyncFrame) => void>()
  private readonly collaboratorsById = new Map<string, SpatialCollaboratorPresence>()

  constructor(private readonly opts: WebSocketAdapterOptions) {}

  connect(): void {
    this.closed = false
    this.openSocket()
  }

  disconnect(): void {
    this.closed = true
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer)
      this.reconnectTimer = null
    }
    if (this.pingTimer) {
      clearInterval(this.pingTimer)
      this.pingTimer = null
    }
    if (this.ws) {
      try {
        this.ws.close()
      } catch {
        // Ignore close errors.
      }
      this.ws = null
    }
    this.opts.onStatusChange('local')
  }

  onFrame(handler: (frame: SpatialSyncFrame) => void): () => void {
    this.handlers.add(handler)
    return () => {
      this.handlers.delete(handler)
    }
  }

  async sendOps(ops: SpatialPropertyOp[], baseSeq: number): Promise<void> {
    if (ops.length === 0) return
    const wireOps = ops.map(toWireOp)
    if (this.ws && this.ws.readyState === 1) {
      try {
        this.ws.send(JSON.stringify({
          type: 'ops',
          request_id: createId('req'),
          client_id: this.opts.clientId,
          base_seq: baseSeq,
          ops: wireOps,
        }))
        return
      } catch {
        // Fall through to REST fallback below.
      }
    }

    const fetcher = this.opts.fetchImpl ?? (typeof fetch === 'function' ? fetch : undefined)
    if (!fetcher) return
    try {
      const base = resolveWebGatewayBase()
      const storageId = sessionStorageId(this.opts.sessionId)
      const response = await fetcher(
        `${base}/api/v1/sessions/${encodeURIComponent(this.opts.sessionId)}/spatial-board/ops`,
        {
          method: 'POST',
          credentials: 'include',
          headers: {
            'content-type': 'application/json',
            accept: 'application/json',
            ...(storageId ? { 'x-tinadec-storage-id': storageId } : {}),
          },
          body: JSON.stringify({
            client_id: this.opts.clientId,
            base_seq: baseSeq,
            ops: wireOps,
          }),
        },
      )
      if (!response.ok) return
      const payload = (await response.json()) as Record<string, unknown>
      this.emitAckFromPayload(payload)
    } catch {
      // Offline: SpatialMultiplayerDocument retains pendingOps for next reconnect.
    }
  }

  sendPresence(presence: SpatialPresenceUpdate): void {
    if (!this.ws || this.ws.readyState !== 1) return
    try {
      const payload: Record<string, unknown> = {
        type: 'presence',
      }
      if (presence.cursor !== undefined) {
        payload.cursor = presence.cursor
        payload.cursor_x = presence.cursor ? presence.cursor.x : null
        payload.cursor_y = presence.cursor ? presence.cursor.y : null
      }
      if (presence.selection !== undefined) {
        payload.selection = presence.selection
      }
      if (presence.viewport !== undefined) {
        payload.viewport = presence.viewport
      }
      if (presence.activeTool !== undefined) {
        payload.active_tool = presence.activeTool
      }
      if (presence.followingClientId !== undefined) {
        payload.following_client_id = presence.followingClientId
      }
      this.ws.send(JSON.stringify(payload))
    } catch {
      // Ignore transient WS send errors.
    }
  }

  async fetchSnapshotRest(): Promise<boolean> {
    const fetcher = this.opts.fetchImpl ?? (typeof fetch === 'function' ? fetch : undefined)
    if (!fetcher) return false
    try {
      const base = resolveWebGatewayBase()
      const storageId = sessionStorageId(this.opts.sessionId)
      const response = await fetcher(
        `${base}/api/v1/sessions/${encodeURIComponent(this.opts.sessionId)}/spatial-board`,
        {
          method: 'GET',
          credentials: 'include',
          headers: {
            accept: 'application/json',
            ...(storageId ? { 'x-tinadec-storage-id': storageId } : {}),
          },
        },
      )
      if (!response.ok) return false
      const payload = (await response.json()) as Record<string, unknown>
      this.handleSnapshotPayload(payload)
      return true
    } catch {
      return false
    }
  }

  private openSocket(): void {
    if (this.closed) return
    const createWs = this.opts.webSocketFactory
      ?? (typeof WebSocket !== 'undefined' ? (url: string) => new WebSocket(url) : undefined)
    if (!createWs) {
      this.opts.onStatusChange('local')
      return
    }

    this.opts.onStatusChange('syncing')
    const base = resolveWebGatewayBase()
    const storageId = sessionStorageId(this.opts.sessionId)
    const params = new URLSearchParams({
      client_id: this.opts.clientId,
      display_name: this.opts.displayName,
      color: this.opts.color,
    })
    if (storageId) params.set('storage_id', storageId)

    const wsUrl = toWebSocketUrl(
      base,
      `/api/v1/sessions/${encodeURIComponent(this.opts.sessionId)}/spatial-board/ws?${params.toString()}`,
    )

    let socket: WebSocket
    try {
      socket = createWs(wsUrl)
    } catch {
      this.opts.onStatusChange('local')
      this.scheduleReconnect()
      return
    }

    this.ws = socket

    socket.onopen = () => {
      if (this.closed || this.ws !== socket) return
      this.reconnectDelay = INITIAL_RECONNECT_MS
      this.opts.onStatusChange('live')
      try {
        socket.send(JSON.stringify({
          type: 'hello',
          client_id: this.opts.clientId,
          display_name: this.opts.displayName,
          color: this.opts.color,
        }))
      } catch {
        // Ignore send failure on immediate close.
      }
      this.emit({ type: 'status', status: 'connected' })
      if (this.pingTimer) clearInterval(this.pingTimer)
      this.pingTimer = setInterval(() => {
        if (this.ws && this.ws.readyState === 1) {
          try {
            this.ws.send(JSON.stringify({ type: 'ping', ts: Date.now() }))
          } catch {
            // Ignore ping error.
          }
        }
      }, PING_INTERVAL_MS)
    }

    socket.onmessage = (event: MessageEvent) => {
      if (this.closed || this.ws !== socket) return
      this.handleMessage(event.data)
    }

    socket.onerror = () => {
      if (this.closed || this.ws !== socket) return
      this.opts.onStatusChange('local')
    }

    socket.onclose = () => {
      if (this.ws === socket) this.ws = null
      if (this.pingTimer) {
        clearInterval(this.pingTimer)
        this.pingTimer = null
      }
      if (this.closed) return
      this.opts.onStatusChange('local')
      this.emit({ type: 'status', status: 'disconnected' })
      this.scheduleReconnect()
    }
  }

  private scheduleReconnect(): void {
    if (this.closed || this.reconnectTimer) return
    const delay = this.reconnectDelay
    this.reconnectDelay = Math.min(MAX_RECONNECT_MS, this.reconnectDelay * 2)
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null
      if (!this.closed) this.openSocket()
    }, delay)
  }

  private handleMessage(raw: unknown): void {
    if (typeof raw !== 'string') return
    let parsed: Record<string, unknown>
    try {
      parsed = JSON.parse(raw) as Record<string, unknown>
    } catch {
      return
    }

    const frameType = typeof parsed.type === 'string' ? parsed.type : ''
    switch (frameType) {
      case 'snapshot': {
        const inner = parsed.snapshot && typeof parsed.snapshot === 'object'
          ? (parsed.snapshot as Record<string, unknown>)
          : parsed
        this.handleSnapshotPayload(inner)
        break
      }
      case 'ops_ack': {
        this.emitAckFromPayload(parsed)
        break
      }
      case 'ops_broadcast': {
        const seq = Number(parsed.seq ?? 0)
        const senderClientId = typeof parsed.client_id === 'string'
          ? parsed.client_id
          : typeof parsed.clientId === 'string'
            ? parsed.clientId
            : undefined
        const objects = fromWireObjectsMap(parsed.objects)
        this.emit({
          type: 'delta',
          delta: {
            seq: Number.isFinite(seq) ? seq : 0,
            senderClientId,
            objects,
          },
        })
        break
      }
      case 'presence_update': {
        if (Array.isArray(parsed.collaborators)) {
          this.collaboratorsById.clear()
          for (const item of parsed.collaborators) {
            const c = fromWireCollaborator(item)
            if (c) this.collaboratorsById.set(c.clientId, c)
          }
        } else {
          if (parsed.collaborator) {
            const c = fromWireCollaborator(parsed.collaborator)
            if (c) this.collaboratorsById.set(c.clientId, c)
          }
          const removedId = typeof parsed.removed_client_id === 'string'
            ? parsed.removed_client_id
            : typeof parsed.removedClientId === 'string'
              ? parsed.removedClientId
              : null
          if (removedId) {
            this.collaboratorsById.delete(removedId)
          }
        }
        this.emit({
          type: 'presence',
          collaborators: [...this.collaboratorsById.values()],
        })
        break
      }
      case 'comment_broadcast': {
        const action = typeof parsed.action === 'string' ? parsed.action : ''
        const comment = parsed.comment ? fromWireComment(parsed.comment) : null
        const pinId = typeof parsed.pin_id === 'string'
          ? parsed.pin_id
          : typeof parsed.pinId === 'string'
            ? parsed.pinId
            : comment?.id ?? null
        this.opts.onCommentEvent(action, comment, pinId)
        break
      }
      default:
        break
    }
  }

  private handleSnapshotPayload(payload: Record<string, unknown>): void {
    const seq = Number(payload.seq ?? 0)
    const objects = fromWireObjectsMap(payload.objects)
    const collaborators: SpatialCollaboratorPresence[] = []
    if (Array.isArray(payload.collaborators)) {
      this.collaboratorsById.clear()
      for (const item of payload.collaborators) {
        const c = fromWireCollaborator(item)
        if (c) {
          this.collaboratorsById.set(c.clientId, c)
          collaborators.push(c)
        }
      }
    }
    if (Array.isArray(payload.comments)) {
      const pins: SpatialCommentPin[] = []
      for (const item of payload.comments) {
        const pin = fromWireComment(item)
        if (pin) pins.push(pin)
      }
      this.opts.onCommentsSnapshot(pins)
    }
    this.emit({
      type: 'snapshot',
      seq: Number.isFinite(seq) ? seq : 0,
      objects,
      collaborators,
    })
  }

  private emitAckFromPayload(payload: Record<string, unknown>): void {
    const seq = Number(payload.seq ?? 0)
    const acceptedOpIds = Array.isArray(payload.accepted_op_ids)
      ? payload.accepted_op_ids.filter((id): id is string => typeof id === 'string')
      : Array.isArray(payload.acceptedOpIds)
        ? payload.acceptedOpIds.filter((id): id is string => typeof id === 'string')
        : []
    const rawRejected = Array.isArray(payload.rejected_ops)
      ? payload.rejected_ops
      : Array.isArray(payload.rejectedOps)
        ? payload.rejectedOps
        : []
    const rejectedOps = rawRejected
      .filter((r): r is Record<string, unknown> => Boolean(r && typeof r === 'object'))
      .map((r) => ({
        clientOpId: String(r.client_op_id ?? r.clientOpId ?? ''),
        objectId: String(r.object_id ?? r.objectId ?? ''),
        code: String(r.code ?? 'rejected'),
        message: typeof r.message === 'string' ? r.message : undefined,
      }))
    const objects = fromWireObjectsMap(payload.objects)
    this.emit({
      type: 'ack',
      ack: {
        seq: Number.isFinite(seq) ? seq : 0,
        acceptedOpIds,
        rejectedOps,
        objects,
      },
    })
  }

  private emit(frame: SpatialSyncFrame): void {
    for (const handler of this.handlers) {
      handler(frame)
    }
  }
}

export function useSpatialBoardSync(options: UseSpatialBoardSyncOptions) {
  const clientId = createId('client')
  const localColor = options.color || pickCollaboratorColor(clientId)

  const doc = shallowRef<SpatialMultiplayerDocument>(
    new SpatialMultiplayerDocument({ sessionId: toValue(options.sessionKey) || 'draft:free', writerId: clientId }),
  )
  const revision = ref(0)
  const comments = ref<SpatialCommentPin[]>([])
  const collaborators = ref<SpatialCollaboratorPresence[]>([])
  const activeGuides = ref<SmartGuideLine[]>([])
  const syncStatus = ref<SpatialSyncStatus>('local')
  const followingClientId = ref<string | null>(null)

  let currentSessionKey = ''
  let activeAdapter: ISpatialSyncAdapter | null = null
  let activeWsAdapter: SpatialWebSocketSyncAdapter | null = null
  let unsubscribeDoc: (() => void) | null = null
  let lastFollowedViewportKey = ''

  // 60Hz / 50ms coalesced presence sender
  let pendingPresence: SpatialPresenceUpdate | null = null
  let lastPresenceSentAt = 0
  let presenceTimer: ReturnType<typeof setTimeout> | null = null

  function resolvedDisplayName(): string {
    const raw = options.displayName ? toValue(options.displayName) : ''
    return (raw && raw.trim()) || 'You'
  }

  function storageKeyFor(sessionKey: string): string {
    return `${STORAGE_PREFIX}${sessionKey}`
  }

  function persistLocal(sessionKey: string): void {
    if (!sessionKey) return
    const blob: PersistedBoardBlob = {
      seq: doc.value.serverSeq,
      objects: doc.value.toRecord({ includeDeleted: true }),
      comments: comments.value,
      updatedAtUnixMs: Date.now(),
    }
    safeStorageSet(storageKeyFor(sessionKey), JSON.stringify(blob))
  }

  function restoreLocal(sessionKey: string, targetDoc: SpatialMultiplayerDocument): SpatialCommentPin[] {
    const raw = safeStorageGet(storageKeyFor(sessionKey))
    if (!raw) return []
    try {
      const parsed = JSON.parse(raw) as Partial<PersistedBoardBlob>
      if (parsed.objects && typeof parsed.objects === 'object') {
        targetDoc.hydrateSnapshot({
          seq: typeof parsed.seq === 'number' ? parsed.seq : 0,
          sessionId: sessionKey,
          objects: parsed.objects,
        })
      }
      if (Array.isArray(parsed.comments)) {
        return parsed.comments
          .map((item) => fromWireComment(item))
          .filter((pin): pin is SpatialCommentPin => pin !== null)
      }
    } catch {
      // Corrupt local cache is ignored cleanly.
    }
    return []
  }

  function clearPresenceTimers(): void {
    if (presenceTimer !== null) {
      clearTimeout(presenceTimer)
      presenceTimer = null
    }
    pendingPresence = null
  }

  function flushPresenceNow(): void {
    presenceTimer = null
    const payload = pendingPresence
    if (!payload) return
    pendingPresence = null
    lastPresenceSentAt = Date.now()
    void activeAdapter?.sendPresence(payload)
  }

  function sendPresence(update: SpatialPresenceUpdate): void {
    pendingPresence = {
      ...(pendingPresence ?? {}),
      ...update,
    }
    const now = Date.now()
    const elapsed = now - lastPresenceSentAt
    if (elapsed >= PRESENCE_COALESCE_MS && presenceTimer === null) {
      flushPresenceNow()
      return
    }
    if (presenceTimer === null) {
      const wait = Math.max(0, PRESENCE_COALESCE_MS - elapsed)
      presenceTimer = setTimeout(() => {
        flushPresenceNow()
      }, wait)
    }
  }

  function applyCommentEvent(action: string, comment: SpatialCommentPin | null, pinId: string | null): void {
    if (action === 'deleted' && pinId) {
      comments.value = comments.value.filter((p) => p.id !== pinId)
      persistLocal(currentSessionKey)
      return
    }
    if (!comment) return
    const idx = comments.value.findIndex((p) => p.id === comment.id)
    if (idx >= 0) {
      const next = [...comments.value]
      next[idx] = comment
      comments.value = next
    } else {
      comments.value = [...comments.value, comment]
    }
    persistLocal(currentSessionKey)
  }

  function teardownConnection(): void {
    clearPresenceTimers()
    if (unsubscribeDoc) {
      unsubscribeDoc()
      unsubscribeDoc = null
    }
    if (activeAdapter) {
      doc.value.detachSyncAdapter()
      if (!options.adapter) {
        void activeAdapter.disconnect()
      }
      activeAdapter = null
      activeWsAdapter = null
    }
  }

  function bindSession(sessionKey: string): void {
    const normalized = (sessionKey || 'draft:free').trim() || 'draft:free'
    if (normalized === currentSessionKey && unsubscribeDoc) return

    teardownConnection()
    currentSessionKey = normalized
    followingClientId.value = null
    lastFollowedViewportKey = ''
    activeGuides.value = []
    collaborators.value = []

    const nextDoc = new SpatialMultiplayerDocument({
      sessionId: normalized,
      writerId: clientId,
    })
    comments.value = restoreLocal(normalized, nextDoc)
    doc.value = nextDoc
    revision.value++

    unsubscribeDoc = nextDoc.subscribe(() => {
      collaborators.value = nextDoc.getCollaborators()
      revision.value++
      persistLocal(currentSessionKey)
    })

    if (options.adapter) {
      activeAdapter = options.adapter
      nextDoc.attachSyncAdapter(options.adapter)
      void options.adapter.connect()
      syncStatus.value = 'live'
      return
    }

    if (isRemoteSessionKey(normalized)) {
      const wsAdapter = new SpatialWebSocketSyncAdapter({
        sessionId: normalized,
        clientId,
        displayName: resolvedDisplayName(),
        color: localColor,
        fetchImpl: options.fetchImpl,
        webSocketFactory: options.webSocketFactory,
        onCommentsSnapshot: (pins) => {
          comments.value = pins
          persistLocal(currentSessionKey)
        },
        onCommentEvent: applyCommentEvent,
        onStatusChange: (status) => {
          syncStatus.value = status
        },
      })
      activeAdapter = wsAdapter
      activeWsAdapter = wsAdapter
      nextDoc.attachSyncAdapter(wsAdapter)
      wsAdapter.connect()
    } else {
      syncStatus.value = 'local'
    }
  }

  watch(
    () => toValue(options.sessionKey),
    (key) => {
      bindSession(key)
    },
    { immediate: true },
  )

  const sortedObjects = computed<SpatialDocObject[]>(() => {
    void revision.value
    return doc.value.sortedObjectList()
  })

  const objectsById = computed<Record<string, SpatialDocObject>>(() => {
    const map: Record<string, SpatialDocObject> = {}
    for (const obj of sortedObjects.value) {
      map[obj.id] = obj
    }
    return map
  })

  const objectsByKind = computed<Record<SpatialObjectKind, SpatialDocObject[]>>(() => {
    const grouped: Record<SpatialObjectKind, SpatialDocObject[]> = {
      work: [],
      section: [],
      sticky: [],
      text: [],
      drawing: [],
      media: [],
      connector: [],
    }
    for (const obj of sortedObjects.value) {
      grouped[obj.kind].push(obj)
    }
    return grouped
  })

  const followedCollaborator = computed<SpatialCollaboratorPresence | null>(() => {
    if (!followingClientId.value) return null
    return collaborators.value.find((c) => c.clientId === followingClientId.value) ?? null
  })

  watch(
    () => {
      const followed = followedCollaborator.value
      const vp = followed?.viewport
      return vp ? `${followed.clientId}:${vp.x}:${vp.y}:${vp.zoom}` : ''
    },
    (key) => {
      if (!key || key === lastFollowedViewportKey) return
      const vp = followedCollaborator.value?.viewport
      if (!vp) return
      lastFollowedViewportKey = key
      options.onFollowViewport?.({ ...vp })
    },
    { flush: 'sync' },
  )

  function followCollaborator(targetClientId: string | null): void {
    const next = targetClientId && targetClientId !== clientId ? targetClientId : null
    followingClientId.value = next
    lastFollowedViewportKey = ''
    sendPresence({ followingClientId: next })
    if (next) {
      const target = collaborators.value.find((c) => c.clientId === next)
      if (target?.viewport) {
        lastFollowedViewportKey = `${next}:${target.viewport.x}:${target.viewport.y}:${target.viewport.zoom}`
        options.onFollowViewport?.({ ...target.viewport })
      }
    }
  }

  function toggleFollowCollaborator(targetClientId: string): void {
    followCollaborator(followingClientId.value === targetClientId ? null : targetClientId)
  }

  function stopFollowing(): void {
    followCollaborator(null)
  }

  // ── Whiteboard Creation & Mutation Helpers ──────────────────────────────────

  function createSection(input: {
    x?: number
    y?: number
    width?: number
    height?: number
    title?: string
    color?: SpatialSectionTone | string
    runId?: string | null
    parentId?: string | null
    sortKey?: string
  } = {}): SpatialDocObject {
    const id = createId('section')
    return doc.value.upsertObject({
      id,
      kind: 'section',
      parentId: input.parentId ?? null,
      sortKey: input.sortKey,
      x: input.x ?? 80,
      y: input.y ?? 80,
      width: input.width ?? SPATIAL_WHITEBOARD_DEFAULTS.section.width,
      height: input.height ?? SPATIAL_WHITEBOARD_DEFAULTS.section.height,
      props: {
        title: input.title ?? '',
        color: input.color ?? SPATIAL_WHITEBOARD_DEFAULTS.section.color,
        collapsed: false,
        ...(input.runId !== undefined ? { runId: input.runId } : {}),
      },
    })
  }

  function createSticky(input: {
    x?: number
    y?: number
    width?: number
    height?: number
    text?: string
    tone?: SpatialStickyTone
    authorName?: string
    authorKind?: 'human' | 'ai'
    parentId?: string | null
  } = {}): SpatialDocObject {
    const id = createId('sticky')
    return doc.value.upsertObject({
      id,
      kind: 'sticky',
      parentId: input.parentId ?? null,
      x: input.x ?? 120,
      y: input.y ?? 120,
      width: input.width ?? SPATIAL_WHITEBOARD_DEFAULTS.sticky.width,
      height: input.height ?? SPATIAL_WHITEBOARD_DEFAULTS.sticky.height,
      props: {
        text: input.text ?? '',
        tone: input.tone ?? SPATIAL_WHITEBOARD_DEFAULTS.sticky.tone,
        authorName: input.authorName ?? resolvedDisplayName(),
        authorKind: input.authorKind ?? 'human',
      },
    })
  }

  function createText(input: {
    x?: number
    y?: number
    width?: number
    height?: number
    text?: string
    scale?: SpatialTextScale
    color?: string
    align?: 'left' | 'center' | 'right'
    parentId?: string | null
  } = {}): SpatialDocObject {
    const id = createId('text')
    return doc.value.upsertObject({
      id,
      kind: 'text',
      parentId: input.parentId ?? null,
      x: input.x ?? 120,
      y: input.y ?? 120,
      width: input.width ?? SPATIAL_WHITEBOARD_DEFAULTS.text.width,
      height: input.height ?? SPATIAL_WHITEBOARD_DEFAULTS.text.height,
      props: {
        text: input.text ?? '',
        scale: input.scale ?? SPATIAL_WHITEBOARD_DEFAULTS.text.scale,
        ...(input.color ? { color: input.color } : {}),
        align: input.align ?? 'left',
      },
    })
  }

  function addDrawingStroke(input: {
    points: [number, number][]
    color?: string
    strokeWidth?: number
    mode?: SpatialDrawingMode
    parentId?: string | null
  }): SpatialDocObject {
    const id = createId('drawing')
    const pts = input.points.length > 0 ? input.points : ([[0, 0]] as [number, number][])
    const xs = pts.map((p) => p[0])
    const ys = pts.map((p) => p[1])
    const minX = Math.min(...xs)
    const minY = Math.min(...ys)
    const maxX = Math.max(...xs)
    const maxY = Math.max(...ys)
    const mode: SpatialDrawingMode = input.mode === 'highlighter' ? 'highlighter' : 'pen'
    const strokeWidth = input.strokeWidth && input.strokeWidth > 0
      ? input.strokeWidth
      : mode === 'highlighter'
        ? SPATIAL_WHITEBOARD_DEFAULTS.highlighter.strokeWidth
        : SPATIAL_WHITEBOARD_DEFAULTS.pen.strokeWidth
    const color = input.color
      || (mode === 'highlighter'
        ? SPATIAL_WHITEBOARD_DEFAULTS.highlighter.color
        : SPATIAL_WHITEBOARD_DEFAULTS.pen.color)

    return doc.value.upsertObject({
      id,
      kind: 'drawing',
      parentId: input.parentId ?? null,
      x: minX,
      y: minY,
      width: Math.max(4, maxX - minX),
      height: Math.max(4, maxY - minY),
      props: {
        points: pts.map(([px, py]) => [px, py]),
        color,
        strokeWidth,
        mode,
      },
    })
  }

  function eraseDrawingStroke(id: string): void {
    doc.value.deleteObjects([id])
  }

  function createMediaTile(input: {
    x?: number
    y?: number
    width?: number
    height?: number
    mediaKind: SpatialMediaKind
    path: string
    url?: string
    title?: string
    snippet?: string
    language?: string
    parentId?: string | null
  }): SpatialDocObject {
    const id = createId('media')
    const defaults = input.mediaKind === 'image'
      ? SPATIAL_WHITEBOARD_DEFAULTS.mediaImage
      : SPATIAL_WHITEBOARD_DEFAULTS.mediaFile
    const fallbackTitle = input.path.replace(/\\/g, '/').split('/').filter(Boolean).pop() || input.path
    return doc.value.upsertObject({
      id,
      kind: 'media',
      parentId: input.parentId ?? null,
      x: input.x ?? 140,
      y: input.y ?? 140,
      width: input.width ?? defaults.width,
      height: input.height ?? defaults.height,
      props: {
        mediaKind: input.mediaKind,
        path: input.path,
        ...(input.url ? { url: input.url } : {}),
        title: input.title ?? fallbackTitle,
        ...(input.snippet !== undefined ? { snippet: input.snippet } : {}),
        ...(input.language ? { language: input.language } : {}),
      },
    })
  }

  function createConnector(input: {
    sourceId: string
    targetId: string
    sourceHandle?: string | null
    targetHandle?: string | null
    label?: string
    curve?: SpatialConnectorCurve
    color?: string
  }): SpatialDocObject {
    const id = createId('connector')
    return doc.value.upsertObject({
      id,
      kind: 'connector',
      parentId: null,
      x: 0,
      y: 0,
      width: 10,
      height: 10,
      props: {
        sourceId: input.sourceId,
        targetId: input.targetId,
        sourceHandle: input.sourceHandle ?? null,
        targetHandle: input.targetHandle ?? null,
        label: input.label ?? '',
        curve: input.curve ?? 'smoothstep',
        ...(input.color ? { color: input.color } : {}),
      },
    })
  }

  function updateObjectProps(
    id: string,
    propPatch: Record<string, unknown>,
    geomPatch?: Partial<Pick<SpatialDocObject, 'x' | 'y' | 'width' | 'height' | 'rotation'>>,
  ): void {
    if (Object.keys(propPatch).length > 0) {
      doc.value.patchProps(id, propPatch)
    }
    if (geomPatch && Object.keys(geomPatch).length > 0) {
      doc.value.moveObjects([{ id, ...geomPatch }])
    }
  }

  function moveObjectsWithSnap(
    changes: Array<{ id: string; x: number; y: number; width?: number; height?: number }>,
    moveOptions?: {
      snap?: boolean
      extraRects?: SpatialRect[]
      threshold?: number
      moveSectionChildren?: boolean
    },
  ): { changes: Array<{ id: string; x: number; y: number }>; guides: SmartGuideLine[] } {
    if (changes.length === 0) {
      activeGuides.value = []
      return { changes: [], guides: [] }
    }

    const movingIds = new Set(changes.map((c) => c.id))
    const shouldMoveChildren = moveOptions?.moveSectionChildren !== false
    if (shouldMoveChildren) {
      for (const ch of changes) {
        const existing = doc.value.getObject(ch.id)
        if (existing?.kind === 'section') {
          for (const descId of doc.value.getDescendantIds(ch.id)) {
            movingIds.add(descId)
          }
        }
      }
    }

    const movingRects: SpatialRect[] = changes.map((ch) => {
      const existing = doc.value.getObject(ch.id)
      return {
        id: ch.id,
        x: ch.x,
        y: ch.y,
        width: ch.width ?? existing?.width ?? 220,
        height: ch.height ?? existing?.height ?? 160,
      }
    })

    let dxSnap = 0
    let dySnap = 0
    let guides: SmartGuideLine[] = []

    if (moveOptions?.snap !== false) {
      const minX = Math.min(...movingRects.map((r) => r.x))
      const minY = Math.min(...movingRects.map((r) => r.y))
      const maxX = Math.max(...movingRects.map((r) => r.x + r.width))
      const maxY = Math.max(...movingRects.map((r) => r.y + r.height))
      const primaryMoving: SpatialRect = movingRects.length === 1
        ? movingRects[0]!
        : { id: '__selection__', x: minX, y: minY, width: Math.max(1, maxX - minX), height: Math.max(1, maxY - minY) }

      const otherRects: SpatialRect[] = []
      for (const obj of sortedObjects.value) {
        if (movingIds.has(obj.id)) continue
        if (obj.kind === 'drawing' || obj.kind === 'connector') continue
        otherRects.push({ id: obj.id, x: obj.x, y: obj.y, width: obj.width, height: obj.height })
      }
      for (const extra of moveOptions?.extraRects ?? []) {
        if (!movingIds.has(extra.id)) otherRects.push(extra)
      }

      const snapResult = computeSmartGuides(primaryMoving, otherRects, moveOptions?.threshold ?? 6)
      dxSnap = snapResult.dx
      dySnap = snapResult.dy
      guides = snapResult.guides
    }

    activeGuides.value = guides

    const finalChanges = changes.map((ch) => ({
      id: ch.id,
      x: ch.x + dxSnap,
      y: ch.y + dySnap,
    }))

    // Apply doc changes atomically (translating section descendants when a section moves).
    if (shouldMoveChildren && finalChanges.length === 1) {
      const only = finalChanges[0]!
      const existing = doc.value.getObject(only.id)
      if (existing?.kind === 'section') {
        const deltaX = only.x - existing.x
        const deltaY = only.y - existing.y
        if (deltaX !== 0 || deltaY !== 0) {
          doc.value.moveSectionWithDescendants(only.id, deltaX, deltaY)
        }
        return { changes: finalChanges, guides }
      }
    }

    const docMovesMap = new Map<string, { id: string; x: number; y: number; width?: number; height?: number }>()
    for (const ch of finalChanges) {
      const existing = doc.value.getObject(ch.id)
      if (!existing) continue
      const orig = changes.find((item) => item.id === ch.id)
      docMovesMap.set(ch.id, {
        id: ch.id,
        x: ch.x,
        y: ch.y,
        ...(orig?.width !== undefined ? { width: orig.width } : {}),
        ...(orig?.height !== undefined ? { height: orig.height } : {}),
      })
      if (shouldMoveChildren && existing.kind === 'section') {
        const deltaX = ch.x - existing.x
        const deltaY = ch.y - existing.y
        if (deltaX !== 0 || deltaY !== 0) {
          for (const descId of doc.value.getDescendantIds(ch.id)) {
            if (docMovesMap.has(descId)) continue
            const desc = doc.value.getObject(descId)
            if (!desc) continue
            docMovesMap.set(descId, { id: descId, x: desc.x + deltaX, y: desc.y + deltaY })
          }
        }
      }
    }
    const docMoves = [...docMovesMap.values()]
    if (docMoves.length > 0) {
      doc.value.moveObjects(docMoves)
    }

    return { changes: finalChanges, guides }
  }

  function clearActiveGuides(): void {
    activeGuides.value = []
  }

  function reparentObject(id: string, newParentId: string | null): boolean {
    return doc.value.reparentObject(id, newParentId)
  }

  function deleteObjects(ids: string[]): void {
    if (!ids.length) return
    doc.value.deleteObjects(ids)
  }

  function duplicateObjects(ids: string[], offset = { x: 28, y: 28 }): SpatialDocObject[] {
    const sourceObjs = ids
      .map((id) => doc.value.getObject(id))
      .filter((obj): obj is SpatialDocObject => Boolean(obj && !obj.deleted))
    if (!sourceObjs.length) return []

    const idMap = new Map<string, string>()
    for (const obj of sourceObjs) {
      idMap.set(obj.id, createId(obj.kind))
    }

    const created: SpatialDocObject[] = []
    for (const obj of sourceObjs) {
      const nextId = idMap.get(obj.id)!
      const nextParent = obj.parentId && idMap.has(obj.parentId) ? idMap.get(obj.parentId)! : obj.parentId
      const copyProps = { ...obj.props }
      if (obj.kind === 'drawing' && Array.isArray(copyProps.points)) {
        copyProps.points = (copyProps.points as [number, number][]).map(([px, py]) => [
          px + offset.x,
          py + offset.y,
        ])
      }
      const nextObj = doc.value.upsertObject({
        id: nextId,
        kind: obj.kind,
        parentId: nextParent,
        x: obj.x + offset.x,
        y: obj.y + offset.y,
        width: obj.width,
        height: obj.height,
        rotation: obj.rotation,
        props: copyProps,
      })
      created.push(nextObj)
    }
    return created
  }

  function collectSelectionRects(
    ids: string[],
    externalRects?: Record<string, { x: number; y: number; width: number; height: number }>,
  ): SpatialRect[] {
    const rects: SpatialRect[] = []
    for (const id of ids) {
      const obj = doc.value.getObject(id)
      if (obj && !obj.deleted) {
        rects.push({ id: obj.id, x: obj.x, y: obj.y, width: obj.width, height: obj.height })
        continue
      }
      const ext = externalRects?.[id]
      if (ext) {
        rects.push({ id, x: ext.x, y: ext.y, width: ext.width, height: ext.height })
      }
    }
    return rects
  }

  function alignSelected(
    ids: string[],
    mode: SpatialAlignMode,
    externalRects?: Record<string, { x: number; y: number; width: number; height: number }>,
  ): Array<{ id: string; x: number; y: number }> {
    const rects = collectSelectionRects(ids, externalRects)
    if (rects.length < 2) return []
    const aligned = alignRects(rects, mode)
    const docChanges = aligned.filter((item) => Boolean(doc.value.getObject(item.id)))
    if (docChanges.length > 0) doc.value.moveObjects(docChanges)
    return aligned
  }

  function distributeSelected(
    ids: string[],
    axis: SpatialDistributeAxis,
    externalRects?: Record<string, { x: number; y: number; width: number; height: number }>,
  ): Array<{ id: string; x: number; y: number }> {
    const rects = collectSelectionRects(ids, externalRects)
    if (rects.length < 3) return []
    const distributed = distributeRects(rects, axis)
    const docChanges = distributed.filter((item) => Boolean(doc.value.getObject(item.id)))
    if (docChanges.length > 0) doc.value.moveObjects(docChanges)
    return distributed
  }

  function wrapSelectionInSection(
    ids: string[],
    wrapOptions?: {
      title?: string
      color?: SpatialSectionTone | string
      externalRects?: Record<string, { x: number; y: number; width: number; height: number }>
    },
  ): SpatialDocObject | null {
    const rects = collectSelectionRects(ids, wrapOptions?.externalRects)
    if (!rects.length) return null
    const bounds = computeBoundingSectionRect(rects, 36, 40)

    // Place section strictly behind all selected doc objects using fractional index.
    let lowestKey: string | null = null
    for (const id of ids) {
      const obj = doc.value.getObject(id)
      if (!obj) continue
      if (lowestKey === null || obj.sortKey < lowestKey) {
        lowestKey = obj.sortKey
      }
    }
    const sectionSortKey = generateKeyBetween(null, lowestKey)

    const section = createSection({
      x: bounds.x,
      y: bounds.y,
      width: bounds.width,
      height: bounds.height,
      title: wrapOptions?.title ?? '',
      color: wrapOptions?.color ?? 'neutral',
      sortKey: sectionSortKey,
    })

    for (const id of ids) {
      if (id === section.id) continue
      if (doc.value.getObject(id)) {
        doc.value.reparentObject(id, section.id)
      }
    }

    return doc.value.getObject(section.id) ?? section
  }

  // ── Comments CRUD (optimistic + REST sync when online) ──────────────────────

  function resolveFetcher(): typeof fetch | undefined {
    return options.fetchImpl ?? (typeof fetch === 'function' ? fetch : undefined)
  }

  async function addCommentPin(input: {
    x: number
    y: number
    body: string
    objectId?: string | null
    authorName?: string
    authorKind?: 'human' | 'ai'
  }): Promise<SpatialCommentPin> {
    const now = Date.now()
    const pinId = createId('pin')
    const body = input.body.trim()
    const mentions = extractMentions(body)
    const optimistic: SpatialCommentPin = {
      id: pinId,
      objectId: input.objectId ?? null,
      x: input.x,
      y: input.y,
      authorId: clientId,
      authorName: input.authorName ?? resolvedDisplayName(),
      authorKind: input.authorKind ?? 'human',
      body,
      mentions,
      replies: [],
      resolved: false,
      createdAtUnixMs: now,
      updatedAtUnixMs: now,
    }
    comments.value = [...comments.value, optimistic]
    persistLocal(currentSessionKey)

    const fetcher = resolveFetcher()
    if (isRemoteSessionKey(currentSessionKey) && fetcher) {
      try {
        const base = resolveWebGatewayBase()
        const storageId = sessionStorageId(currentSessionKey)
        const response = await fetcher(
          `${base}/api/v1/sessions/${encodeURIComponent(currentSessionKey)}/spatial-board/comments`,
          {
            method: 'POST',
            credentials: 'include',
            headers: {
              'content-type': 'application/json',
              accept: 'application/json',
              ...(storageId ? { 'x-tinadec-storage-id': storageId } : {}),
            },
            body: JSON.stringify({
              pin_id: pinId,
              object_id: optimistic.objectId,
              x: optimistic.x,
              y: optimistic.y,
              author_id: optimistic.authorId,
              author_name: optimistic.authorName,
              author_kind: optimistic.authorKind,
              body: optimistic.body,
              mentions: optimistic.mentions,
            }),
          },
        )
        if (response.ok) {
          const parsed = fromWireComment(await response.json())
          if (parsed) {
            applyCommentEvent('created', parsed, parsed.id)
            return parsed
          }
        }
      } catch {
        // Keep optimistic local pin.
      }
    }

    return optimistic
  }

  async function replyCommentPin(
    pinId: string,
    body: string,
    author?: { authorName?: string; authorKind?: 'human' | 'ai' },
  ): Promise<SpatialCommentPin | null> {
    const target = comments.value.find((p) => p.id === pinId)
    if (!target) return null
    const trimmed = body.trim()
    if (!trimmed) return target

    const now = Date.now()
    const replyId = createId('reply')
    const reply: SpatialCommentReply = {
      id: replyId,
      authorId: clientId,
      authorName: author?.authorName ?? resolvedDisplayName(),
      authorKind: author?.authorKind ?? 'human',
      body: trimmed,
      createdAtUnixMs: now,
    }
    const updated: SpatialCommentPin = {
      ...target,
      replies: [...target.replies, reply],
      updatedAtUnixMs: now,
    }
    applyCommentEvent('replied', updated, pinId)

    const fetcher = resolveFetcher()
    if (isRemoteSessionKey(currentSessionKey) && fetcher) {
      try {
        const base = resolveWebGatewayBase()
        const storageId = sessionStorageId(currentSessionKey)
        const response = await fetcher(
          `${base}/api/v1/sessions/${encodeURIComponent(currentSessionKey)}/spatial-board/comments/${encodeURIComponent(pinId)}/replies`,
          {
            method: 'POST',
            credentials: 'include',
            headers: {
              'content-type': 'application/json',
              accept: 'application/json',
              ...(storageId ? { 'x-tinadec-storage-id': storageId } : {}),
            },
            body: JSON.stringify({
              reply_id: replyId,
              author_id: reply.authorId,
              author_name: reply.authorName,
              author_kind: reply.authorKind,
              body: reply.body,
              mentions: extractMentions(reply.body),
            }),
          },
        )
        if (response.ok) {
          const parsed = fromWireComment(await response.json())
          if (parsed) {
            applyCommentEvent('replied', parsed, parsed.id)
            return parsed
          }
        }
      } catch {
        // Keep optimistic reply.
      }
    }

    return updated
  }

  async function toggleResolveCommentPin(
    pinId: string,
    resolved?: boolean,
  ): Promise<SpatialCommentPin | null> {
    const target = comments.value.find((p) => p.id === pinId)
    if (!target) return null
    const nextResolved = resolved ?? !target.resolved
    const updated: SpatialCommentPin = {
      ...target,
      resolved: nextResolved,
      updatedAtUnixMs: Date.now(),
    }
    applyCommentEvent('updated', updated, pinId)

    const fetcher = resolveFetcher()
    if (isRemoteSessionKey(currentSessionKey) && fetcher) {
      try {
        const base = resolveWebGatewayBase()
        const storageId = sessionStorageId(currentSessionKey)
        const response = await fetcher(
          `${base}/api/v1/sessions/${encodeURIComponent(currentSessionKey)}/spatial-board/comments/${encodeURIComponent(pinId)}`,
          {
            method: 'PATCH',
            credentials: 'include',
            headers: {
              'content-type': 'application/json',
              accept: 'application/json',
              ...(storageId ? { 'x-tinadec-storage-id': storageId } : {}),
            },
            body: JSON.stringify({ resolved: nextResolved }),
          },
        )
        if (response.ok) {
          const parsed = fromWireComment(await response.json())
          if (parsed) {
            applyCommentEvent('updated', parsed, parsed.id)
            return parsed
          }
        }
      } catch {
        // Keep optimistic toggle.
      }
    }

    return updated
  }

  async function deleteCommentPin(pinId: string): Promise<void> {
    applyCommentEvent('deleted', null, pinId)
    const fetcher = resolveFetcher()
    if (isRemoteSessionKey(currentSessionKey) && fetcher) {
      try {
        const base = resolveWebGatewayBase()
        const storageId = sessionStorageId(currentSessionKey)
        await fetcher(
          `${base}/api/v1/sessions/${encodeURIComponent(currentSessionKey)}/spatial-board/comments/${encodeURIComponent(pinId)}`,
          {
            method: 'DELETE',
            credentials: 'include',
            headers: {
              accept: 'application/json',
              ...(storageId ? { 'x-tinadec-storage-id': storageId } : {}),
            },
          },
        )
      } catch {
        // Already removed locally.
      }
    }
  }

  async function refreshFromServer(): Promise<void> {
    if (!activeWsAdapter) return
    await activeWsAdapter.fetchSnapshotRest()
  }

  function dispose(): void {
    teardownConnection()
  }

  if (getCurrentScope()) {
    onScopeDispose(dispose)
  }

  return {
    doc,
    clientId,
    objectsByKind,
    sortedObjects,
    objectsById,
    comments,
    collaborators,
    activeGuides,
    syncStatus,
    followingClientId,
    followedCollaborator,
    sendPresence,
    followCollaborator,
    toggleFollowCollaborator,
    stopFollowing,
    createSection,
    createSticky,
    createText,
    addDrawingStroke,
    eraseDrawingStroke,
    createMediaTile,
    createConnector,
    updateObjectProps,
    moveObjectsWithSnap,
    clearActiveGuides,
    reparentObject,
    deleteObjects,
    duplicateObjects,
    alignSelected,
    distributeSelected,
    wrapSelectionInSection,
    addCommentPin,
    replyCommentPin,
    toggleResolveCommentPin,
    deleteCommentPin,
    refreshFromServer,
    dispose,
  }
}
