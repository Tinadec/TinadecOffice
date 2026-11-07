import { defaultStorage, safeReadJson, safeRemove, trySet, type KeyValueStorage } from './storage'

/**
 * Local, best-effort drafts for the settings centers.
 *
 * The server (TOML through Core) remains the only authority; this store exists so
 * a half-edited agent/team/model form survives a tab switch, a section switch or
 * a renderer reload. Drafts are keyed by storage scope + entity + id, carry the
 * revision they were based on, and are never replayed automatically: a 412 marks
 * the draft `stale` and the editor asks the user what to do.
 */
export type DraftEntity = 'agent' | 'team' | 'mode' | 'connection' | 'route'
export type DraftStatus = 'dirty' | 'stale'

export interface DraftBase {
  revision: number | null
  etag: string | null
}

export interface StoredDraft {
  scope: string
  entity: DraftEntity
  id: string
  payload: Record<string, unknown>
  base: DraftBase
  savedAt: number
  status: DraftStatus
}

export interface DraftIndexEntry {
  scope: string
  entity: DraftEntity
  id: string
  savedAt: number
  status: DraftStatus
}

export type DraftWriteResult =
  | { ok: true }
  | { ok: false; reason: 'too_large' | 'unavailable' | 'invalid' }

export const DRAFT_STORAGE_PREFIX = 'tinadec.settings.center.draft.v1'
export const DRAFT_INDEX_KEY = `${DRAFT_STORAGE_PREFIX}.index`
export const DRAFT_MAX_BYTES = 256 * 1024
const INDEX_LIMIT = 50

const ENTITIES: ReadonlySet<string> = new Set(['agent', 'team', 'mode', 'connection', 'route'])

/** Credential-shaped keys never leave the form through local persistence. */
const SECRET_KEY = /(^|_)(api[_-]?key|secret|password|passwd|token|authorization|credential|private[_-]?key)($|_)/i

export function draftKey(scope: string, entity: DraftEntity, id: string): string {
  return `${DRAFT_STORAGE_PREFIX}.${encodeURIComponent(scope)}.${entity}.${encodeURIComponent(id)}`
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value)
}

export function redactDraftSecrets(value: unknown, seen = new WeakSet<object>()): unknown {
  if (Array.isArray(value)) {
    if (seen.has(value)) return null
    seen.add(value)
    return value.map(item => redactDraftSecrets(item, seen))
  }
  if (!isRecord(value)) return value
  if (seen.has(value)) return null
  seen.add(value)
  const result: Record<string, unknown> = {}
  for (const [key, item] of Object.entries(value)) {
    result[key] = SECRET_KEY.test(key) ? '[redacted]' : redactDraftSecrets(item, seen)
  }
  return result
}

function normalizeDraft(value: unknown): StoredDraft | null {
  if (!isRecord(value)) return null
  const entity = typeof value.entity === 'string' ? value.entity : ''
  if (!ENTITIES.has(entity)) return null
  const scope = typeof value.scope === 'string' ? value.scope : ''
  const id = typeof value.id === 'string' ? value.id : ''
  if (!scope || !id) return null
  if (!isRecord(value.payload)) return null
  const base = isRecord(value.base) ? value.base : {}
  return {
    scope,
    entity: entity as DraftEntity,
    id,
    payload: value.payload,
    base: {
      revision: typeof base.revision === 'number' ? base.revision : null,
      etag: typeof base.etag === 'string' ? base.etag : null,
    },
    savedAt: typeof value.savedAt === 'number' ? value.savedAt : 0,
    status: value.status === 'stale' ? 'stale' : 'dirty',
  }
}

export function readDraft(
  scope: string,
  entity: DraftEntity,
  id: string,
  storage: KeyValueStorage = defaultStorage(),
): StoredDraft | null {
  return normalizeDraft(safeReadJson(storage, draftKey(scope, entity, id)))
}

export function writeDraft(
  scope: string,
  entity: DraftEntity,
  id: string,
  payload: Record<string, unknown>,
  base: DraftBase,
  now: number = Date.now(),
  storage: KeyValueStorage = defaultStorage(),
): DraftWriteResult {
  if (!scope || !id || !isRecord(payload)) return { ok: false, reason: 'invalid' }
  const redacted = redactDraftSecrets(payload)
  if (!isRecord(redacted)) return { ok: false, reason: 'invalid' }
  const draft: StoredDraft = {
    scope,
    entity,
    id,
    payload: redacted,
    base: { revision: base.revision, etag: base.etag },
    savedAt: now,
    status: 'dirty',
  }
  const serialized = JSON.stringify(draft)
  if (new TextEncoder().encode(serialized).length > DRAFT_MAX_BYTES) {
    return { ok: false, reason: 'too_large' }
  }
  if (!trySet(storage, draftKey(scope, entity, id), serialized)) {
    return { ok: false, reason: 'unavailable' }
  }
  upsertIndex(draft, storage)
  return { ok: true }
}

export function markDraftStale(
  scope: string,
  entity: DraftEntity,
  id: string,
  now: number = Date.now(),
  storage: KeyValueStorage = defaultStorage(),
): StoredDraft | null {
  const draft = readDraft(scope, entity, id, storage)
  if (!draft) return null
  const next: StoredDraft = { ...draft, status: 'stale', savedAt: now }
  if (!trySet(storage, draftKey(scope, entity, id), JSON.stringify(next))) return draft
  upsertIndex(next, storage)
  return next
}

export function clearDraft(
  scope: string,
  entity: DraftEntity,
  id: string,
  storage: KeyValueStorage = defaultStorage(),
): void {
  safeRemove(storage, draftKey(scope, entity, id))
  const index = readDraftIndex(storage).filter(entry =>
    !(entry.scope === scope && entry.entity === entity && entry.id === id))
  trySet(storage, DRAFT_INDEX_KEY, JSON.stringify(index))
}

export function clearAllDrafts(storage: KeyValueStorage = defaultStorage()): void {
  for (const entry of readDraftIndex(storage)) {
    safeRemove(storage, draftKey(entry.scope, entry.entity, entry.id))
  }
  safeRemove(storage, DRAFT_INDEX_KEY)
}

export function readDraftIndex(storage: KeyValueStorage = defaultStorage()): DraftIndexEntry[] {
  const raw = safeReadJson(storage, DRAFT_INDEX_KEY)
  if (!Array.isArray(raw)) return []
  const entries: DraftIndexEntry[] = []
  for (const item of raw) {
    if (!isRecord(item)) continue
    const entity = typeof item.entity === 'string' ? item.entity : ''
    if (!ENTITIES.has(entity)) continue
    if (typeof item.scope !== 'string' || typeof item.id !== 'string') continue
    entries.push({
      scope: item.scope,
      entity: entity as DraftEntity,
      id: item.id,
      savedAt: typeof item.savedAt === 'number' ? item.savedAt : 0,
      status: item.status === 'stale' ? 'stale' : 'dirty',
    })
  }
  return entries
}

function upsertIndex(draft: StoredDraft, storage: KeyValueStorage): void {
  const entries = readDraftIndex(storage).filter(entry =>
    !(entry.scope === draft.scope && entry.entity === draft.entity && entry.id === draft.id))
  entries.push({
    scope: draft.scope,
    entity: draft.entity,
    id: draft.id,
    savedAt: draft.savedAt,
    status: draft.status,
  })
  entries.sort((a, b) => b.savedAt - a.savedAt)
  trySet(storage, DRAFT_INDEX_KEY, JSON.stringify(entries.slice(0, INDEX_LIMIT)))
}
