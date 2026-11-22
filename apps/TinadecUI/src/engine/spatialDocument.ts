// Pure TypeScript Figma-style Flat Map CRDT/LWW Multiplayer Spatial Document Kernel.
//
// Architecture:
//   - Flat `Map<ObjectId, SpatialDocObject>` with per-property LWW (`(lamport, writerId)`).
//   - Dual-layer reconciliation: `confirmedObjects` (authoritative/remote LWW base) +
//     `pendingOps` (unacked local operations in flight) so dragged/resized objects
//     maintain zero-latency optimistic state and NEVER rubber-band/snap back mid-gesture.
//   - Strict DAG `parentId` cycle detection in both local and remote application.
//   - Pluggable `ISpatialSyncAdapter` + `LocalLoopbackSyncAdapter` + `ExternalCrdtSyncContract`.

import { emptySpace, type SpatialItem, type SpatialLayout } from './spatial'
import {
  compareFractionalKeys,
  generateKeyBetween,
  generateNKeysBetween,
  isValidFractionalKey,
} from './spatialFractionalIndex'

export type SpatialObjectKind =
  | 'work'
  | 'section'
  | 'sticky'
  | 'text'
  | 'drawing'
  | 'media'
  | 'connector'

export interface SpatialLwwTimestamp {
  lamport: number
  writerId: string
}

export interface SpatialDocObject {
  id: string
  kind: SpatialObjectKind
  parentId: string | null
  sortKey: string
  x: number
  y: number
  width: number
  height: number
  rotation?: number
  props: Record<string, unknown>
  propTimestamps: Record<string, SpatialLwwTimestamp>
  deleted?: boolean
  deletedTs?: SpatialLwwTimestamp
}

export interface SpatialPropertyOp {
  clientOpId: string
  objectId: string
  kind?: SpatialObjectKind
  parentId?: string | null
  hasParentId?: boolean
  sortKey?: string
  x?: number
  y?: number
  width?: number
  height?: number
  rotation?: number
  props?: Record<string, unknown>
  timestamps: Record<string, SpatialLwwTimestamp>
  deleted?: boolean
  deletedTs?: SpatialLwwTimestamp
}

export interface SpatialRejectedOp {
  clientOpId: string
  objectId: string
  code: string
  message?: string
}

export interface SpatialAckPayload {
  seq: number
  acceptedOpIds: string[]
  rejectedOps?: SpatialRejectedOp[]
  objects?: Record<string, SpatialDocObject> | SpatialDocObject[]
}

export interface SpatialRemoteDelta {
  seq: number
  senderClientId?: string
  ops?: SpatialPropertyOp[]
  objects?: Record<string, SpatialDocObject> | SpatialDocObject[]
}

export interface SpatialViewportState {
  x: number
  y: number
  zoom: number
}

export interface SpatialCollaboratorPresence {
  clientId: string
  displayName: string
  color: string
  role: 'human' | 'ai'
  agentInstanceId?: string | null
  status?: string
  cursor?: { x: number; y: number } | null
  cursorX?: number | null
  cursorY?: number | null
  selection: string[]
  viewport?: SpatialViewportState | null
  activeTool?: string | null
  followingClientId?: string | null
  updatedAtUnixMs?: number
}

export type SpatialPresenceUpdate = Partial<Omit<SpatialCollaboratorPresence, 'clientId'>> & {
  clientId?: string
}

export type SpatialSyncFrame =
  | {
      type: 'snapshot'
      seq: number
      objects: Record<string, SpatialDocObject> | SpatialDocObject[]
      collaborators?: SpatialCollaboratorPresence[]
    }
  | {
      type: 'ack'
      ack: SpatialAckPayload
    }
  | {
      type: 'delta'
      delta: SpatialRemoteDelta
    }
  | {
      type: 'presence'
      collaborators: SpatialCollaboratorPresence[]
    }
  | {
      type: 'status'
      status: 'connected' | 'disconnected' | 'reconnecting'
    }

export interface ISpatialSyncAdapter {
  connect(): void | Promise<void>
  disconnect(): void | Promise<void>
  sendOps(ops: SpatialPropertyOp[], baseSeq: number): void | Promise<void>
  sendPresence(presence: SpatialPresenceUpdate): void | Promise<void>
  onFrame(handler: (frame: SpatialSyncFrame) => void): () => void
}

/**
 * Contract allowing external state-vector CRDT engines (e.g. Yjs, Automerge)
 * to bridge cleanly into `ISpatialSyncAdapter` without coupling UI consumers.
 */
export interface ExternalCrdtSyncContract<TStateVector = Uint8Array, TUpdate = Uint8Array> {
  readonly providerId: string
  encodeStateVector(): TStateVector
  encodeStateAsUpdate(targetStateVector?: TStateVector): TUpdate
  applyExternalUpdate(update: TUpdate, origin?: string): void
  toSyncAdapter(clientId: string): ISpatialSyncAdapter
}

export interface UpsertSpatialObjectInput {
  id: string
  kind?: SpatialObjectKind
  parentId?: string | null
  sortKey?: string
  x?: number
  y?: number
  width?: number
  height?: number
  rotation?: number
  props?: Record<string, unknown>
}

export interface SpatialMoveChange {
  id: string
  x?: number
  y?: number
  width?: number
  height?: number
  rotation?: number
}

export interface SpatialMultiplayerDocumentOptions {
  sessionId?: string
  writerId?: string
  initialLamport?: number
  viewport?: SpatialViewportState
  syncAdapter?: ISpatialSyncAdapter | null
}

export type SpatialSnapshotInput =
  | {
      seq?: number
      sessionId?: string
      viewport?: SpatialViewportState
      objects: Record<string, SpatialDocObject> | Iterable<SpatialDocObject>
      collaborators?: SpatialCollaboratorPresence[]
    }
  | Record<string, SpatialDocObject>
  | SpatialDocObject[]

const DEFAULT_SORT_KEY = generateKeyBetween(null, null)

function compareStringsOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0
}

/**
 * Total-order LWW comparison: higher `lamport` wins; ties break by ordinal `writerId`.
 */
export function compareLwwTimestamps(
  a?: SpatialLwwTimestamp | null,
  b?: SpatialLwwTimestamp | null,
): number {
  if (!a && !b) return 0
  if (!a) return -1
  if (!b) return 1
  if (a.lamport !== b.lamport) {
    return a.lamport < b.lamport ? -1 : 1
  }
  return compareStringsOrdinal(a.writerId, b.writerId)
}

function cloneTimestamp(ts: SpatialLwwTimestamp): SpatialLwwTimestamp {
  return { lamport: ts.lamport, writerId: ts.writerId }
}

function cloneValue<T>(value: T): T {
  if (value === null || typeof value !== 'object') return value
  if (Array.isArray(value)) return value.map(item => cloneValue(item)) as unknown as T
  const copy: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
    copy[k] = cloneValue(v)
  }
  return copy as T
}

function cloneDocObject(obj: SpatialDocObject): SpatialDocObject {
  const propTimestamps: Record<string, SpatialLwwTimestamp> = {}
  for (const [k, ts] of Object.entries(obj.propTimestamps)) {
    if (ts) propTimestamps[k] = cloneTimestamp(ts)
  }
  return {
    id: obj.id,
    kind: obj.kind,
    parentId: obj.parentId,
    sortKey: obj.sortKey,
    x: obj.x,
    y: obj.y,
    width: obj.width,
    height: obj.height,
    ...(obj.rotation !== undefined ? { rotation: obj.rotation } : {}),
    props: cloneValue(obj.props),
    propTimestamps,
    ...(obj.deleted !== undefined ? { deleted: obj.deleted } : {}),
    ...(obj.deletedTs ? { deletedTs: cloneTimestamp(obj.deletedTs) } : {}),
  }
}

function cloneOp(op: SpatialPropertyOp): SpatialPropertyOp {
  const timestamps: Record<string, SpatialLwwTimestamp> = {}
  for (const [k, ts] of Object.entries(op.timestamps)) {
    if (ts) timestamps[k] = cloneTimestamp(ts)
  }
  return {
    clientOpId: op.clientOpId,
    objectId: op.objectId,
    ...(op.kind !== undefined ? { kind: op.kind } : {}),
    ...(op.parentId !== undefined ? { parentId: op.parentId } : {}),
    ...(op.hasParentId !== undefined ? { hasParentId: op.hasParentId } : {}),
    ...(op.sortKey !== undefined ? { sortKey: op.sortKey } : {}),
    ...(op.x !== undefined ? { x: op.x } : {}),
    ...(op.y !== undefined ? { y: op.y } : {}),
    ...(op.width !== undefined ? { width: op.width } : {}),
    ...(op.height !== undefined ? { height: op.height } : {}),
    ...(op.rotation !== undefined ? { rotation: op.rotation } : {}),
    ...(op.props !== undefined ? { props: cloneValue(op.props) } : {}),
    timestamps,
    ...(op.deleted !== undefined ? { deleted: op.deleted } : {}),
    ...(op.deletedTs !== undefined ? { deletedTs: cloneTimestamp(op.deletedTs) } : {}),
  }
}

function normalizeSortKey(key: string | undefined | null): string {
  return typeof key === 'string' && isValidFractionalKey(key) ? key : DEFAULT_SORT_KEY
}

function wouldCreateCycleInMap(
  map: ReadonlyMap<string, SpatialDocObject>,
  id: string,
  candidateParentId: string | null | undefined,
): boolean {
  if (candidateParentId === null || candidateParentId === undefined || candidateParentId === '') {
    return false
  }
  if (id === candidateParentId) return true
  const visited = new Set<string>([id])
  let current: string | null | undefined = candidateParentId
  while (current) {
    if (visited.has(current)) return true
    visited.add(current)
    current = map.get(current)?.parentId ?? null
  }
  return false
}

function sanitizeCyclesInMap(map: Map<string, SpatialDocObject>): void {
  const orderedIds = [...map.keys()].sort(compareStringsOrdinal)
  for (const id of orderedIds) {
    const obj = map.get(id)!
    if (obj.parentId && wouldCreateCycleInMap(map, id, obj.parentId)) {
      obj.parentId = null
    }
  }
}

function maxTimestampInOp(op: SpatialPropertyOp): SpatialLwwTimestamp | undefined {
  let maxTs: SpatialLwwTimestamp | undefined = op.deletedTs
  for (const ts of Object.values(op.timestamps ?? {})) {
    if (compareLwwTimestamps(ts, maxTs) > 0) {
      maxTs = ts
    }
  }
  return maxTs
}

function resolveFieldTs(
  timestamps: Record<string, SpatialLwwTimestamp> | undefined,
  primaryKey: string,
  fallbackKey: string | undefined,
  defaultTs: SpatialLwwTimestamp | undefined,
): SpatialLwwTimestamp | undefined {
  if (timestamps) {
    if (timestamps[primaryKey]) return timestamps[primaryKey]
    if (fallbackKey && timestamps[fallbackKey]) return timestamps[fallbackKey]
  }
  return defaultTs
}

function normalizeObjectsCollection(
  collection: Record<string, SpatialDocObject> | Iterable<SpatialDocObject>,
): SpatialDocObject[] {
  if (Array.isArray(collection)) return collection
  if (typeof (collection as Iterable<SpatialDocObject>)[Symbol.iterator] === 'function') {
    return [...(collection as Iterable<SpatialDocObject>)]
  }
  return Object.values(collection as Record<string, SpatialDocObject>)
}

function createSkeletonObject(id: string, kind: SpatialObjectKind = 'sticky'): SpatialDocObject {
  return {
    id,
    kind,
    parentId: null,
    sortKey: DEFAULT_SORT_KEY,
    x: 0,
    y: 0,
    width: 240,
    height: 160,
    props: {},
    propTimestamps: {},
  }
}

/**
 * Merges a `SpatialPropertyOp` into `map` using strict `(lamport, writerId)` LWW
 * when `forceOverlay` is false, or unconditionally as an in-flight local overlay
 * when `forceOverlay` is true. Always enforces DAG cycle safety on `parentId`.
 */
function applyOpToMap(
  map: Map<string, SpatialDocObject>,
  op: SpatialPropertyOp,
  forceOverlay: boolean,
): void {
  if (!op || typeof op.objectId !== 'string' || op.objectId.length === 0) return
  const fallbackTs = maxTimestampInOp(op) ?? { lamport: 0, writerId: '' }
  let obj = map.get(op.objectId)
  if (!obj) {
    obj = createSkeletonObject(op.objectId, op.kind ?? 'sticky')
    map.set(op.objectId, obj)
  }

  const shouldWrite = (fieldKey: string, incomingTs: SpatialLwwTimestamp | undefined): boolean => {
    if (forceOverlay) return true
    if (!incomingTs) return false
    return compareLwwTimestamps(incomingTs, obj!.propTimestamps[fieldKey]) >= 0
  }

  if (op.kind !== undefined) {
    const ts = resolveFieldTs(op.timestamps, 'kind', undefined, fallbackTs)
    if (shouldWrite('kind', ts)) {
      obj.kind = op.kind
      if (ts) obj.propTimestamps.kind = cloneTimestamp(ts)
    }
  }

  const wantsParentUpdate = op.hasParentId === true || op.parentId !== undefined
  if (wantsParentUpdate) {
    const candidateParent = op.parentId ?? null
    const ts = resolveFieldTs(op.timestamps, 'parentId', 'parent_id', fallbackTs)
    if (shouldWrite('parentId', ts) && !wouldCreateCycleInMap(map, op.objectId, candidateParent)) {
      obj.parentId = candidateParent
      if (ts) obj.propTimestamps.parentId = cloneTimestamp(ts)
    }
  }

  if (op.sortKey !== undefined) {
    const ts = resolveFieldTs(op.timestamps, 'sortKey', 'sort_key', fallbackTs)
    if (shouldWrite('sortKey', ts)) {
      obj.sortKey = normalizeSortKey(op.sortKey)
      if (ts) obj.propTimestamps.sortKey = cloneTimestamp(ts)
    }
  }

  if (op.x !== undefined && Number.isFinite(op.x)) {
    const ts = resolveFieldTs(op.timestamps, 'x', undefined, fallbackTs)
    if (shouldWrite('x', ts)) {
      obj.x = op.x
      if (ts) obj.propTimestamps.x = cloneTimestamp(ts)
    }
  }

  if (op.y !== undefined && Number.isFinite(op.y)) {
    const ts = resolveFieldTs(op.timestamps, 'y', undefined, fallbackTs)
    if (shouldWrite('y', ts)) {
      obj.y = op.y
      if (ts) obj.propTimestamps.y = cloneTimestamp(ts)
    }
  }

  if (op.width !== undefined && Number.isFinite(op.width)) {
    const ts = resolveFieldTs(op.timestamps, 'width', undefined, fallbackTs)
    if (shouldWrite('width', ts)) {
      obj.width = Math.max(1, op.width)
      if (ts) obj.propTimestamps.width = cloneTimestamp(ts)
    }
  }

  if (op.height !== undefined && Number.isFinite(op.height)) {
    const ts = resolveFieldTs(op.timestamps, 'height', undefined, fallbackTs)
    if (shouldWrite('height', ts)) {
      obj.height = Math.max(1, op.height)
      if (ts) obj.propTimestamps.height = cloneTimestamp(ts)
    }
  }

  if (op.rotation !== undefined && Number.isFinite(op.rotation)) {
    const ts = resolveFieldTs(op.timestamps, 'rotation', undefined, fallbackTs)
    if (shouldWrite('rotation', ts)) {
      obj.rotation = op.rotation
      if (ts) obj.propTimestamps.rotation = cloneTimestamp(ts)
    }
  }

  if (op.props) {
    for (const [propKey, propVal] of Object.entries(op.props)) {
      const tsKey = `props.${propKey}`
      const ts = resolveFieldTs(op.timestamps, tsKey, propKey, fallbackTs)
      if (shouldWrite(tsKey, ts)) {
        if (propVal === undefined) {
          delete obj.props[propKey]
        } else {
          obj.props[propKey] = cloneValue(propVal)
        }
        if (ts) obj.propTimestamps[tsKey] = cloneTimestamp(ts)
      }
    }
  }

  if (op.deleted !== undefined) {
    const ts = op.deletedTs ?? resolveFieldTs(op.timestamps, 'deleted', undefined, fallbackTs)
    const currentDelTs = obj.propTimestamps.deleted ?? obj.deletedTs
    if (forceOverlay || (ts && compareLwwTimestamps(ts, currentDelTs) >= 0)) {
      if (op.deleted) {
        obj.deleted = true
        if (ts) {
          obj.deletedTs = cloneTimestamp(ts)
          obj.propTimestamps.deleted = cloneTimestamp(ts)
        }
      } else {
        obj.deleted = false
        delete obj.deletedTs
        if (ts) obj.propTimestamps.deleted = cloneTimestamp(ts)
      }
    }
  }
}

function mergeDocObjectIntoMap(map: Map<string, SpatialDocObject>, incoming: SpatialDocObject): void {
  if (!incoming || typeof incoming.id !== 'string' || incoming.id.length === 0) return
  const existing = map.get(incoming.id)
  if (!existing) {
    const copy = cloneDocObject(incoming)
    copy.sortKey = normalizeSortKey(copy.sortKey)
    if (copy.deleted && copy.deletedTs && !copy.propTimestamps.deleted) {
      copy.propTimestamps.deleted = cloneTimestamp(copy.deletedTs)
    }
    if (copy.parentId && wouldCreateCycleInMap(map, copy.id, copy.parentId)) {
      copy.parentId = null
    }
    map.set(copy.id, copy)
    return
  }

  const synthOp: SpatialPropertyOp = {
    clientOpId: `merge:${incoming.id}`,
    objectId: incoming.id,
    kind: incoming.kind,
    parentId: incoming.parentId,
    hasParentId: true,
    sortKey: incoming.sortKey,
    x: incoming.x,
    y: incoming.y,
    width: incoming.width,
    height: incoming.height,
    ...(incoming.rotation !== undefined ? { rotation: incoming.rotation } : {}),
    props: incoming.props,
    timestamps: incoming.propTimestamps ?? {},
    ...(incoming.deleted !== undefined ? { deleted: incoming.deleted } : {}),
    ...(incoming.deletedTs ? { deletedTs: incoming.deletedTs } : {}),
  }
  applyOpToMap(map, synthOp, false)
}

interface LoopbackRoomState {
  seq: number
  peers: Set<LocalLoopbackSyncAdapter>
  collaborators: Map<string, SpatialCollaboratorPresence>
}

export interface LocalLoopbackSyncOptions {
  clientId?: string
  displayName?: string
  color?: string
  deferAck?: boolean
}

/**
 * In-memory `ISpatialSyncAdapter` for offline/local sessions, deterministic unit tests,
 * and multi-peer loopback rooms (`LocalLoopbackSyncAdapter.createRoom()`).
 */
export class LocalLoopbackSyncAdapter implements ISpatialSyncAdapter {
  readonly clientId: string
  private readonly displayName: string
  private readonly color: string
  private readonly deferAck: boolean
  private readonly room: LoopbackRoomState
  private readonly handlers = new Set<(frame: SpatialSyncFrame) => void>()
  private readonly deferredActions: Array<() => void> = []
  private connected = false

  constructor(options: LocalLoopbackSyncOptions = {}, room?: LoopbackRoomState) {
    this.clientId = options.clientId ?? 'local-client'
    this.displayName = options.displayName ?? this.clientId
    this.color = options.color ?? '#3b82f6'
    this.deferAck = options.deferAck ?? false
    this.room = room ?? {
      seq: 0,
      peers: new Set(),
      collaborators: new Map(),
    }
  }

  static createRoom(): {
    createAdapter: (clientId: string, options?: Omit<LocalLoopbackSyncOptions, 'clientId'>) => LocalLoopbackSyncAdapter
    getSeq: () => number
  } {
    const room: LoopbackRoomState = {
      seq: 0,
      peers: new Set(),
      collaborators: new Map(),
    }
    return {
      createAdapter: (clientId, options = {}) =>
        new LocalLoopbackSyncAdapter({ ...options, clientId }, room),
      getSeq: () => room.seq,
    }
  }

  connect(): void {
    if (this.connected) return
    this.connected = true
    this.room.peers.add(this)
    this.emit({ type: 'status', status: 'connected' })
  }

  disconnect(): void {
    if (!this.connected) return
    this.connected = false
    this.room.peers.delete(this)
    this.room.collaborators.delete(this.clientId)
    this.broadcastPresence()
    this.emit({ type: 'status', status: 'disconnected' })
  }

  sendOps(ops: SpatialPropertyOp[], _baseSeq: number): void {
    if (!this.connected || ops.length === 0) return
    const clonedOps = ops.map(cloneOp)
    const deliver = () => {
      const seq = ++this.room.seq
      this.emit({
        type: 'ack',
        ack: {
          seq,
          acceptedOpIds: clonedOps.map(o => o.clientOpId),
        },
      })
      for (const peer of this.room.peers) {
        if (peer === this || !peer.connected) continue
        peer.emit({
          type: 'delta',
          delta: {
            seq,
            senderClientId: this.clientId,
            ops: clonedOps.map(cloneOp),
          },
        })
      }
    }
    if (this.deferAck) {
      this.deferredActions.push(deliver)
    } else {
      deliver()
    }
  }

  flushDeferred(): void {
    while (this.deferredActions.length > 0) {
      const fn = this.deferredActions.shift()!
      fn()
    }
  }

  sendPresence(presence: SpatialPresenceUpdate): void {
    if (!this.connected) return
    const prev = this.room.collaborators.get(this.clientId)
    const cursor = presence.cursor !== undefined
      ? presence.cursor
      : presence.cursorX !== undefined && presence.cursorY !== undefined
        ? (presence.cursorX !== null && presence.cursorY !== null
            ? { x: presence.cursorX, y: presence.cursorY }
            : null)
        : (prev?.cursor ?? null)

    const next: SpatialCollaboratorPresence = {
      clientId: this.clientId,
      displayName: presence.displayName ?? prev?.displayName ?? this.displayName,
      color: presence.color ?? prev?.color ?? this.color,
      role: presence.role ?? prev?.role ?? 'human',
      agentInstanceId: presence.agentInstanceId ?? prev?.agentInstanceId ?? null,
      status: presence.status ?? prev?.status,
      cursor,
      cursorX: cursor ? cursor.x : null,
      cursorY: cursor ? cursor.y : null,
      selection: presence.selection ? [...presence.selection] : (prev?.selection ?? []),
      viewport: presence.viewport !== undefined ? presence.viewport : (prev?.viewport ?? null),
      activeTool: presence.activeTool !== undefined ? presence.activeTool : (prev?.activeTool ?? null),
      followingClientId:
        presence.followingClientId !== undefined
          ? presence.followingClientId
          : (prev?.followingClientId ?? null),
      updatedAtUnixMs: presence.updatedAtUnixMs ?? Date.now(),
    }
    this.room.collaborators.set(this.clientId, next)
    this.broadcastPresence()
  }

  onFrame(handler: (frame: SpatialSyncFrame) => void): () => void {
    this.handlers.add(handler)
    return () => {
      this.handlers.delete(handler)
    }
  }

  emit(frame: SpatialSyncFrame): void {
    for (const handler of this.handlers) {
      handler(frame)
    }
  }

  private broadcastPresence(): void {
    const list = [...this.room.collaborators.values()].map(c => cloneValue(c))
    for (const peer of this.room.peers) {
      if (!peer.connected) continue
      peer.emit({ type: 'presence', collaborators: list.map(c => cloneValue(c)) })
    }
  }
}

export class SpatialMultiplayerDocument {
  readonly writerId: string
  sessionId: string
  viewport: SpatialViewportState

  private confirmedObjects = new Map<string, SpatialDocObject>()
  private visibleObjects = new Map<string, SpatialDocObject>()
  private pendingOps: SpatialPropertyOp[] = []
  private collaborators: SpatialCollaboratorPresence[] = []
  private lamportClock: number
  private seq = 0
  private opCounter = 0
  private adapter: ISpatialSyncAdapter | null = null
  private unsubscribeAdapter: (() => void) | null = null
  private readonly listeners = new Set<() => void>()

  constructor(optionsOrWriterId?: SpatialMultiplayerDocumentOptions | string) {
    const opts: SpatialMultiplayerDocumentOptions =
      typeof optionsOrWriterId === 'string'
        ? { writerId: optionsOrWriterId }
        : (optionsOrWriterId ?? {})

    this.writerId = opts.writerId && opts.writerId.length > 0 ? opts.writerId : 'local-writer'
    this.sessionId = opts.sessionId && opts.sessionId.length > 0 ? opts.sessionId : 'default'
    this.viewport = opts.viewport ? { ...opts.viewport } : { x: 40, y: 72, zoom: 0.85 }
    this.lamportClock = Math.max(0, Math.floor(opts.initialLamport ?? 0))

    if (opts.syncAdapter) {
      this.attachSyncAdapter(opts.syncAdapter)
    }
  }

  get serverSeq(): number {
    return this.seq
  }

  get lamport(): number {
    return this.lamportClock
  }

  getServerSeq(): number {
    return this.seq
  }

  getLamport(): number {
    return this.lamportClock
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }

  attachSyncAdapter(adapter: ISpatialSyncAdapter | null): void {
    this.detachSyncAdapter()
    this.adapter = adapter
    if (!adapter) return
    this.unsubscribeAdapter = adapter.onFrame((frame) => {
      switch (frame.type) {
        case 'snapshot':
          this.hydrateSnapshot({
            seq: frame.seq,
            objects: frame.objects,
            collaborators: frame.collaborators,
          })
          break
        case 'ack':
          this.applyServerAck(frame.ack)
          break
        case 'delta':
          if (frame.delta.senderClientId && frame.delta.senderClientId === this.writerId) {
            this.seq = Math.max(this.seq, frame.delta.seq)
            break
          }
          this.applyRemoteDelta(frame.delta)
          break
        case 'presence':
          this.setCollaborators(frame.collaborators)
          break
        case 'status':
          if (frame.status === 'connected' && this.pendingOps.length > 0) {
            void this.adapter?.sendOps(this.getPendingOps(), this.seq)
          }
          break
      }
    })
  }

  detachSyncAdapter(): void {
    if (this.unsubscribeAdapter) {
      this.unsubscribeAdapter()
      this.unsubscribeAdapter = null
    }
    this.adapter = null
  }

  getCollaborators(): SpatialCollaboratorPresence[] {
    return this.collaborators.map(c => cloneValue(c))
  }

  setCollaborators(collaborators: SpatialCollaboratorPresence[]): void {
    this.collaborators = collaborators.map(c => cloneValue(c))
    this.notify()
  }

  getObject(id: string, options?: { includeDeleted?: boolean }): SpatialDocObject | undefined {
    const obj = this.visibleObjects.get(id)
    if (!obj) return undefined
    if (obj.deleted && !options?.includeDeleted) return undefined
    return cloneDocObject(obj)
  }

  getAllObjects(options?: { includeDeleted?: boolean }): Map<string, SpatialDocObject> {
    const out = new Map<string, SpatialDocObject>()
    for (const [id, obj] of this.visibleObjects) {
      if (obj.deleted && !options?.includeDeleted) continue
      out.set(id, cloneDocObject(obj))
    }
    return out
  }

  toRecord(options?: { includeDeleted?: boolean }): Record<string, SpatialDocObject> {
    const rec: Record<string, SpatialDocObject> = {}
    for (const [id, obj] of this.visibleObjects) {
      if (obj.deleted && !options?.includeDeleted) continue
      rec[id] = cloneDocObject(obj)
    }
    return rec
  }

  getPendingOps(): SpatialPropertyOp[] {
    return this.pendingOps.map(cloneOp)
  }

  hasPendingOps(objectId?: string): boolean {
    if (objectId === undefined) return this.pendingOps.length > 0
    return this.pendingOps.some(op => op.objectId === objectId)
  }

  /**
   * Checks whether assigning `candidateParentId` as the parent of `id` would introduce
   * a self-reference (`id === candidateParentId`) or ancestor cycle in the parent DAG.
   */
  wouldCreateParentCycle(id: string, candidateParentId: string | null | undefined): boolean {
    return wouldCreateCycleInMap(this.visibleObjects, id, candidateParentId)
  }

  /**
   * Zero-latency optimistic upsert of a spatial document object.
   */
  upsertObject(input: UpsertSpatialObjectInput): SpatialDocObject {
    const existing = this.visibleObjects.get(input.id)
    const ts = this.nextTimestamp()
    const timestamps: Record<string, SpatialLwwTimestamp> = {}

    const kind: SpatialObjectKind = input.kind ?? existing?.kind ?? 'sticky'
    timestamps.kind = ts

    let parentId: string | null = existing?.parentId ?? null
    let includeParent = false
    if (input.parentId !== undefined) {
      const candidate = input.parentId ?? null
      if (!this.wouldCreateParentCycle(input.id, candidate)) {
        parentId = candidate
        includeParent = true
        timestamps.parentId = ts
      }
    } else if (!existing) {
      parentId = null
      includeParent = true
      timestamps.parentId = ts
    }

    let sortKey = existing?.sortKey
    if (input.sortKey !== undefined && isValidFractionalKey(input.sortKey)) {
      sortKey = input.sortKey
      timestamps.sortKey = ts
    } else if (!sortKey) {
      sortKey = this.nextAppendSortKey()
      timestamps.sortKey = ts
    }

    const x = input.x !== undefined && Number.isFinite(input.x) ? input.x : (existing?.x ?? 0)
    const y = input.y !== undefined && Number.isFinite(input.y) ? input.y : (existing?.y ?? 0)
    const width =
      input.width !== undefined && Number.isFinite(input.width)
        ? Math.max(1, input.width)
        : (existing?.width ?? 240)
    const height =
      input.height !== undefined && Number.isFinite(input.height)
        ? Math.max(1, input.height)
        : (existing?.height ?? 160)

    if (!existing || input.x !== undefined) timestamps.x = ts
    if (!existing || input.y !== undefined) timestamps.y = ts
    if (!existing || input.width !== undefined) timestamps.width = ts
    if (!existing || input.height !== undefined) timestamps.height = ts

    let rotation: number | undefined
    if (input.rotation !== undefined && Number.isFinite(input.rotation)) {
      rotation = input.rotation
      timestamps.rotation = ts
    }

    const propsPatch: Record<string, unknown> | undefined = input.props ? {} : undefined
    if (input.props && propsPatch) {
      for (const [k, v] of Object.entries(input.props)) {
        propsPatch[k] = cloneValue(v)
        timestamps[`props.${k}`] = ts
      }
    }

    const resurrecting = Boolean(existing?.deleted)
    if (resurrecting) {
      timestamps.deleted = ts
    }

    const op: SpatialPropertyOp = {
      clientOpId: this.nextClientOpId(),
      objectId: input.id,
      kind,
      ...(includeParent ? { parentId, hasParentId: true } : {}),
      ...(timestamps.sortKey ? { sortKey } : {}),
      ...(timestamps.x ? { x } : {}),
      ...(timestamps.y ? { y } : {}),
      ...(timestamps.width ? { width } : {}),
      ...(timestamps.height ? { height } : {}),
      ...(rotation !== undefined ? { rotation } : {}),
      ...(propsPatch ? { props: propsPatch } : {}),
      ...(resurrecting ? { deleted: false } : {}),
      timestamps,
    }

    this.commitLocalOps([op])
    return cloneDocObject(this.visibleObjects.get(input.id)!)
  }

  /**
   * Patches custom `props` fields on object `id` at property-level LWW granularity.
   */
  patchProps(id: string, patch: Record<string, unknown>): SpatialDocObject | undefined {
    const existing = this.visibleObjects.get(id)
    if (!existing || existing.deleted) return undefined
    const keys = Object.keys(patch)
    if (keys.length === 0) return cloneDocObject(existing)

    const ts = this.nextTimestamp()
    const timestamps: Record<string, SpatialLwwTimestamp> = {}
    const props: Record<string, unknown> = {}
    for (const k of keys) {
      props[k] = cloneValue(patch[k])
      timestamps[`props.${k}`] = ts
    }

    const op: SpatialPropertyOp = {
      clientOpId: this.nextClientOpId(),
      objectId: id,
      props,
      timestamps,
    }
    this.commitLocalOps([op])
    return this.getObject(id)
  }

  /**
   * Optimistically translates/resizes/rotates one or more objects in a single batched op set.
   */
  moveObjects(changes: ReadonlyArray<SpatialMoveChange>): SpatialPropertyOp[] {
    if (changes.length === 0) return []
    const ts = this.nextTimestamp()
    const ops: SpatialPropertyOp[] = []

    for (const change of changes) {
      const existing = this.visibleObjects.get(change.id)
      if (!existing || existing.deleted) continue
      const timestamps: Record<string, SpatialLwwTimestamp> = {}
      const op: SpatialPropertyOp = {
        clientOpId: this.nextClientOpId(),
        objectId: change.id,
        timestamps,
      }
      let mutated = false

      if (change.x !== undefined && Number.isFinite(change.x)) {
        op.x = change.x
        timestamps.x = ts
        mutated = true
      }
      if (change.y !== undefined && Number.isFinite(change.y)) {
        op.y = change.y
        timestamps.y = ts
        mutated = true
      }
      if (change.width !== undefined && Number.isFinite(change.width)) {
        op.width = Math.max(1, change.width)
        timestamps.width = ts
        mutated = true
      }
      if (change.height !== undefined && Number.isFinite(change.height)) {
        op.height = Math.max(1, change.height)
        timestamps.height = ts
        mutated = true
      }
      if (change.rotation !== undefined && Number.isFinite(change.rotation)) {
        op.rotation = change.rotation
        timestamps.rotation = ts
        mutated = true
      }

      if (mutated) ops.push(op)
    }

    if (ops.length > 0) {
      this.commitLocalOps(ops)
    }
    return ops.map(cloneOp)
  }

  /**
   * Reparents `id` under `newParentId` (or `null` for root).
   * Returns `false` and makes no mutation if `id` is missing/deleted or if reparenting
   * would create a self-parent or ancestor cycle.
   */
  reparentObject(id: string, newParentId: string | null): boolean {
    const existing = this.visibleObjects.get(id)
    if (!existing || existing.deleted) return false
    const normalizedParent = newParentId === '' ? null : newParentId
    if (normalizedParent !== null) {
      const parentObj = this.visibleObjects.get(normalizedParent)
      if (parentObj?.deleted) return false
    }
    if (this.wouldCreateParentCycle(id, normalizedParent)) {
      return false
    }

    const ts = this.nextTimestamp()
    const op: SpatialPropertyOp = {
      clientOpId: this.nextClientOpId(),
      objectId: id,
      parentId: normalizedParent,
      hasParentId: true,
      timestamps: { parentId: ts },
    }
    this.commitLocalOps([op])
    return true
  }

  /**
   * Generates a fractional sort key strictly between `beforeKey` and `afterKey`
   * and updates `id`'s `sortKey`.
   */
  reorderObject(
    id: string,
    beforeKey?: string | null,
    afterKey?: string | null,
  ): string | null {
    const existing = this.visibleObjects.get(id)
    if (!existing || existing.deleted) return null
    const lo = beforeKey && isValidFractionalKey(beforeKey) ? beforeKey : null
    const hi = afterKey && isValidFractionalKey(afterKey) ? afterKey : null
    const nextSortKey = generateKeyBetween(lo, hi)
    const ts = this.nextTimestamp()
    const op: SpatialPropertyOp = {
      clientOpId: this.nextClientOpId(),
      objectId: id,
      sortKey: nextSortKey,
      timestamps: { sortKey: ts },
    }
    this.commitLocalOps([op])
    return nextSortKey
  }

  /**
   * Soft-deletes objects using LWW tombstones.
   */
  deleteObjects(ids: Iterable<string>): SpatialPropertyOp[] {
    const uniqueIds = [...new Set(ids)]
    if (uniqueIds.length === 0) return []
    const ts = this.nextTimestamp()
    const ops: SpatialPropertyOp[] = []

    for (const id of uniqueIds) {
      const existing = this.visibleObjects.get(id)
      if (!existing || existing.deleted) continue
      ops.push({
        clientOpId: this.nextClientOpId(),
        objectId: id,
        deleted: true,
        deletedTs: cloneTimestamp(ts),
        timestamps: { deleted: cloneTimestamp(ts) },
      })
    }

    if (ops.length > 0) {
      this.commitLocalOps(ops)
    }
    return ops.map(cloneOp)
  }

  /**
   * Returns all active recursive descendant IDs under `parentId` in deterministic order.
   */
  getDescendantIds(parentId: string): string[] {
    const childrenByParent = new Map<string, string[]>()
    for (const obj of this.sortedObjectList()) {
      if (!obj.parentId) continue
      let list = childrenByParent.get(obj.parentId)
      if (!list) {
        list = []
        childrenByParent.set(obj.parentId, list)
      }
      list.push(obj.id)
    }

    const descendants: string[] = []
    const visited = new Set<string>([parentId])
    const queue = [...(childrenByParent.get(parentId) ?? [])]
    while (queue.length > 0) {
      const nextId = queue.shift()!
      if (visited.has(nextId)) continue
      visited.add(nextId)
      descendants.push(nextId)
      const grandChildren = childrenByParent.get(nextId)
      if (grandChildren) queue.push(...grandChildren)
    }
    return descendants
  }

  getChildren(parentId: string | null): SpatialDocObject[] {
    return this.sortedObjectList({ parentId })
  }

  /**
   * Atomically translates `sectionId` and all recursive descendants by `(dx, dy)`
   * in a single batched op set.
   */
  moveSectionWithDescendants(sectionId: string, dx: number, dy: number): SpatialPropertyOp[] {
    if (!Number.isFinite(dx) || !Number.isFinite(dy) || (dx === 0 && dy === 0)) {
      return []
    }
    const root = this.visibleObjects.get(sectionId)
    if (!root || root.deleted) return []

    const targetIds = [sectionId, ...this.getDescendantIds(sectionId)]
    const changes: SpatialMoveChange[] = []
    for (const id of targetIds) {
      const obj = this.visibleObjects.get(id)
      if (!obj || obj.deleted) continue
      changes.push({
        id,
        x: obj.x + dx,
        y: obj.y + dy,
      })
    }
    return this.moveObjects(changes)
  }

  /**
   * Reconciles server acknowledgement: promotes accepted ops into confirmed LWW state,
   * drops rejected ops cleanly, and keeps any newer still-unacked local ops overlaid.
   */
  applyServerAck(ack: SpatialAckPayload): void {
    if (Number.isFinite(ack.seq)) {
      this.seq = Math.max(this.seq, ack.seq)
    }

    const acceptedSet = new Set(ack.acceptedOpIds ?? [])
    const rejectedSet = new Set((ack.rejectedOps ?? []).map(r => r.clientOpId))
    const rejectedObjectIds = new Set((ack.rejectedOps ?? []).map(r => r.objectId))

    if (acceptedSet.size > 0 || rejectedSet.size > 0) {
      const remaining: SpatialPropertyOp[] = []
      for (const op of this.pendingOps) {
        if (acceptedSet.has(op.clientOpId)) {
          applyOpToMap(this.confirmedObjects, op, false)
        } else if (rejectedSet.has(op.clientOpId)) {
          // Drop rejected op so visible state falls back to confirmed state (plus any later valid ops)
        } else {
          remaining.push(op)
        }
      }
      this.pendingOps = remaining
    }

    if (ack.objects) {
      for (const obj of normalizeObjectsCollection(ack.objects)) {
        this.observeObjectTimestamps(obj)
        if (rejectedObjectIds.has(obj.id)) {
          const copy = cloneDocObject(obj)
          copy.sortKey = normalizeSortKey(copy.sortKey)
          if (copy.deleted && copy.deletedTs && !copy.propTimestamps.deleted) {
            copy.propTimestamps.deleted = cloneTimestamp(copy.deletedTs)
          }
          this.confirmedObjects.set(copy.id, copy)
        } else {
          mergeDocObjectIntoMap(this.confirmedObjects, obj)
        }
      }
      sanitizeCyclesInMap(this.confirmedObjects)
    }

    this.rebuildVisibleState()
    this.notify()
  }

  /**
   * Merges remote property-level LWW delta while preserving unacked local property edits
   * in flight so actively dragged/resized cards never snap back.
   */
  applyRemoteDelta(delta: SpatialRemoteDelta): void {
    if (Number.isFinite(delta.seq)) {
      this.seq = Math.max(this.seq, delta.seq)
    }

    if (delta.ops) {
      for (const op of delta.ops) {
        this.observeOpTimestamps(op)
        applyOpToMap(this.confirmedObjects, op, false)
      }
    }

    if (delta.objects) {
      for (const obj of normalizeObjectsCollection(delta.objects)) {
        this.observeObjectTimestamps(obj)
        mergeDocObjectIntoMap(this.confirmedObjects, obj)
      }
    }

    sanitizeCyclesInMap(this.confirmedObjects)
    this.rebuildVisibleState()
    this.notify()
  }

  /**
   * Hydrates confirmed state from a full snapshot while preserving any unacked local ops
   * queued while offline or reconnecting.
   */
  hydrateSnapshot(snapshot: SpatialSnapshotInput, seqOverride?: number): void {
    let rawObjects: Record<string, SpatialDocObject> | Iterable<SpatialDocObject>
    if (
      !Array.isArray(snapshot)
      && snapshot !== null
      && typeof snapshot === 'object'
      && 'objects' in snapshot
      && snapshot.objects !== undefined
      && typeof snapshot.objects === 'object'
      && !('sortKey' in snapshot && 'kind' in snapshot)
    ) {
      const envelope = snapshot as {
        seq?: number
        sessionId?: string
        viewport?: SpatialViewportState
        objects: Record<string, SpatialDocObject> | Iterable<SpatialDocObject>
        collaborators?: SpatialCollaboratorPresence[]
      }
      if (envelope.seq !== undefined && Number.isFinite(envelope.seq)) {
        this.seq = envelope.seq
      }
      if (envelope.sessionId) {
        this.sessionId = envelope.sessionId
      }
      if (envelope.viewport) {
        this.viewport = { ...envelope.viewport }
      }
      if (envelope.collaborators) {
        this.collaborators = envelope.collaborators.map(c => cloneValue(c))
      }
      rawObjects = envelope.objects
    } else {
      rawObjects = snapshot as Record<string, SpatialDocObject> | SpatialDocObject[]
    }

    if (seqOverride !== undefined && Number.isFinite(seqOverride)) {
      this.seq = seqOverride
    }

    const nextConfirmed = new Map<string, SpatialDocObject>()
    for (const obj of normalizeObjectsCollection(rawObjects)) {
      if (!obj || typeof obj.id !== 'string' || obj.id.length === 0) continue
      this.observeObjectTimestamps(obj)
      const copy = cloneDocObject(obj)
      copy.sortKey = normalizeSortKey(copy.sortKey)
      nextConfirmed.set(copy.id, copy)
    }
    sanitizeCyclesInMap(nextConfirmed)
    this.confirmedObjects = nextConfirmed

    this.rebuildVisibleState()
    this.notify()
  }

  /**
   * Returns objects sorted deterministically by Fractional Index `sortKey` with `id` tie-breaking.
   */
  sortedObjectList(options?: { includeDeleted?: boolean; parentId?: string | null }): SpatialDocObject[] {
    const filterByParent = options !== undefined && 'parentId' in options && options.parentId !== undefined
    const list: SpatialDocObject[] = []
    for (const obj of this.visibleObjects.values()) {
      if (obj.deleted && !options?.includeDeleted) continue
      if (filterByParent && obj.parentId !== options!.parentId) continue
      list.push(cloneDocObject(obj))
    }
    list.sort(
      (a, b) => compareFractionalKeys(a.sortKey, b.sortKey) || compareStringsOrdinal(a.id, b.id),
    )
    return list
  }

  importLegacyLayout(layout: SpatialLayout): this {
    this.sessionId = layout.sessionId
    this.viewport = { ...layout.viewport }
    const entries = Object.values(layout.items).sort((a, b) => compareStringsOrdinal(a.id, b.id))
    const keys = generateNKeysBetween(null, null, entries.length)
    const ts = this.nextTimestamp()
    const hydrated: SpatialDocObject[] = entries.map((item, idx) => {
      const props: Record<string, unknown> = { groupId: item.groupId }
      const propTimestamps: Record<string, SpatialLwwTimestamp> = {
        kind: cloneTimestamp(ts),
        parentId: cloneTimestamp(ts),
        sortKey: cloneTimestamp(ts),
        x: cloneTimestamp(ts),
        y: cloneTimestamp(ts),
        width: cloneTimestamp(ts),
        height: cloneTimestamp(ts),
        'props.groupId': cloneTimestamp(ts),
      }
      if (typeof item.autoHeight === 'boolean') {
        props.autoHeight = item.autoHeight
        propTimestamps['props.autoHeight'] = cloneTimestamp(ts)
      }
      if (typeof item.manualPosition === 'boolean') {
        props.manualPosition = item.manualPosition
        propTimestamps['props.manualPosition'] = cloneTimestamp(ts)
      }
      if (typeof item.compact === 'boolean') {
        props.compact = item.compact
        propTimestamps['props.compact'] = cloneTimestamp(ts)
      }
      return {
        id: item.id,
        kind: 'work',
        parentId: null,
        sortKey: keys[idx],
        x: item.x,
        y: item.y,
        width: item.width,
        height: item.height,
        props,
        propTimestamps,
      }
    })

    for (const obj of hydrated) {
      this.confirmedObjects.set(obj.id, obj)
    }
    this.rebuildVisibleState()
    this.notify()
    return this
  }

  exportToSpatialLayout(options?: {
    sessionId?: string
    viewport?: SpatialViewportState
    kinds?: readonly SpatialObjectKind[]
  }): SpatialLayout {
    return exportToSpatialLayout(this, options)
  }

  private nextTimestamp(): SpatialLwwTimestamp {
    this.lamportClock += 1
    return { lamport: this.lamportClock, writerId: this.writerId }
  }

  private nextClientOpId(): string {
    this.opCounter += 1
    return `${this.writerId}:${this.lamportClock}:${this.opCounter}`
  }

  private nextAppendSortKey(): string {
    let maxKey: string | null = null
    for (const obj of this.visibleObjects.values()) {
      if (obj.deleted) continue
      if (isValidFractionalKey(obj.sortKey)) {
        if (maxKey === null || compareFractionalKeys(obj.sortKey, maxKey) > 0) {
          maxKey = obj.sortKey
        }
      }
    }
    return generateKeyBetween(maxKey, null)
  }

  private observeOpTimestamps(op: SpatialPropertyOp): void {
    const maxTs = maxTimestampInOp(op)
    if (maxTs && Number.isFinite(maxTs.lamport)) {
      this.lamportClock = Math.max(this.lamportClock, Math.floor(maxTs.lamport))
    }
  }

  private observeObjectTimestamps(obj: SpatialDocObject): void {
    if (obj.deletedTs && Number.isFinite(obj.deletedTs.lamport)) {
      this.lamportClock = Math.max(this.lamportClock, Math.floor(obj.deletedTs.lamport))
    }
    for (const ts of Object.values(obj.propTimestamps ?? {})) {
      if (ts && Number.isFinite(ts.lamport)) {
        this.lamportClock = Math.max(this.lamportClock, Math.floor(ts.lamport))
      }
    }
  }

  private commitLocalOps(ops: SpatialPropertyOp[]): void {
    for (const op of ops) {
      this.pendingOps.push(cloneOp(op))
      applyOpToMap(this.visibleObjects, op, true)
    }
    if (this.adapter) {
      void this.adapter.sendOps(ops.map(cloneOp), this.seq)
    }
    this.notify()
  }

  private rebuildVisibleState(): void {
    const nextVisible = new Map<string, SpatialDocObject>()
    for (const [id, obj] of this.confirmedObjects) {
      nextVisible.set(id, cloneDocObject(obj))
    }
    for (const op of this.pendingOps) {
      applyOpToMap(nextVisible, op, true)
    }
    sanitizeCyclesInMap(nextVisible)
    this.visibleObjects = nextVisible
  }

  private notify(): void {
    for (const listener of this.listeners) {
      listener()
    }
  }
}

/**
 * Lossless import from legacy `SpatialLayout` into a `SpatialMultiplayerDocument`.
 */
export function importLegacySpatialLayout(
  layout: SpatialLayout,
  options?: SpatialMultiplayerDocumentOptions,
): SpatialMultiplayerDocument {
  const doc = new SpatialMultiplayerDocument({
    ...options,
    sessionId: layout.sessionId,
    viewport: layout.viewport,
  })
  return doc.importLegacyLayout(layout)
}

/**
 * Lossless export from a `SpatialMultiplayerDocument` (or collection of `SpatialDocObject`s)
 * back to `SpatialLayout`.
 */
export function exportToSpatialLayout(
  source: SpatialMultiplayerDocument | Iterable<SpatialDocObject> | Record<string, SpatialDocObject>,
  options?: {
    sessionId?: string
    viewport?: SpatialViewportState
    kinds?: readonly SpatialObjectKind[]
  },
): SpatialLayout {
  const allowedKinds = new Set<SpatialObjectKind>(options?.kinds ?? ['work'])
  let sessionId = options?.sessionId ?? 'default'
  let viewport = options?.viewport ? { ...options.viewport } : emptySpace(sessionId).viewport
  let objects: SpatialDocObject[]

  if (source instanceof SpatialMultiplayerDocument) {
    sessionId = options?.sessionId ?? source.sessionId
    viewport = options?.viewport ? { ...options.viewport } : { ...source.viewport }
    objects = source.sortedObjectList()
  } else {
    objects = normalizeObjectsCollection(source).filter(obj => !obj.deleted)
  }

  const items: Record<string, SpatialItem> = {}
  for (const obj of objects) {
    if (!allowedKinds.has(obj.kind)) continue
    const groupId =
      typeof obj.props.groupId === 'string' && obj.props.groupId.length > 0
        ? obj.props.groupId
        : (obj.parentId ?? 'default')

    const item: SpatialItem = {
      id: obj.id,
      groupId,
      x: obj.x,
      y: obj.y,
      width: obj.width,
      height: obj.height,
      ...(typeof obj.props.autoHeight === 'boolean' ? { autoHeight: obj.props.autoHeight } : {}),
      ...(typeof obj.props.manualPosition === 'boolean'
        ? { manualPosition: obj.props.manualPosition }
        : {}),
      ...(typeof obj.props.compact === 'boolean' ? { compact: obj.props.compact } : {}),
    }
    items[item.id] = item
  }

  return {
    sessionId,
    items,
    viewport,
  }
}
