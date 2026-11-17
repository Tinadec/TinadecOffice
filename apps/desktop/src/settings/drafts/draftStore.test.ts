import { describe, expect, it } from 'vitest'
import {
  DRAFT_INDEX_KEY,
  DRAFT_MAX_BYTES,
  clearAllDrafts,
  clearDraft,
  draftKey,
  markDraftStale,
  readDraft,
  readDraftIndex,
  redactDraftSecrets,
  writeDraft,
  type DraftEntity,
} from './draftStore'
import type { KeyValueStorage } from './storage'

function memoryStorage(): KeyValueStorage & { dump(): Map<string, string> } {
  const map = new Map<string, string>()
  return {
    getItem: key => map.get(key) ?? null,
    setItem: (key, value) => { map.set(key, value) },
    removeItem: key => { map.delete(key) },
    dump: () => map,
  }
}

const base = { revision: 3, etag: '"3"' }

describe('draftStore', () => {
  it('round-trips a draft with its base revision', () => {
    const storage = memoryStorage()
    expect(writeDraft('user', 'agent', 'a1', { display_name: 'Scout' }, base, 100, storage)).toEqual({ ok: true })
    expect(readDraft('user', 'agent', 'a1', storage)).toMatchObject({
      scope: 'user', entity: 'agent', id: 'a1', payload: { display_name: 'Scout' }, base, savedAt: 100, status: 'dirty',
    })
  })

  it('keeps the same id isolated across storage scopes', () => {
    const storage = memoryStorage()
    writeDraft('user', 'agent', 'shared', { display_name: 'User copy' }, base, 1, storage)
    writeDraft('project-7', 'agent', 'shared', { display_name: 'Project copy' }, base, 2, storage)
    expect(readDraft('user', 'agent', 'shared', storage)?.payload.display_name).toBe('User copy')
    expect(readDraft('project-7', 'agent', 'shared', storage)?.payload.display_name).toBe('Project copy')
  })

  it('records a draft in the index and replaces the previous entry', () => {
    const storage = memoryStorage()
    writeDraft('user', 'agent', 'a1', { display_name: 'One' }, base, 10, storage)
    writeDraft('user', 'agent', 'a1', { display_name: 'Two' }, base, 20, storage)
    const index = readDraftIndex(storage)
    expect(index).toHaveLength(1)
    expect(index[0]).toMatchObject({ id: 'a1', savedAt: 20 })
  })

  it('marks a draft stale after a revision conflict', () => {
    const storage = memoryStorage()
    writeDraft('user', 'team', 't1', { display_name: 'Team' }, base, 10, storage)
    expect(markDraftStale('user', 'team', 't1', 30, storage)?.status).toBe('stale')
    expect(readDraft('user', 'team', 't1', storage)?.status).toBe('stale')
    expect(readDraftIndex(storage)[0].status).toBe('stale')
  })

  it('removes both the draft and its index entry', () => {
    const storage = memoryStorage()
    writeDraft('user', 'connection', 'c1', { display_name: 'AMD' }, base, 10, storage)
    clearDraft('user', 'connection', 'c1', storage)
    expect(readDraft('user', 'connection', 'c1', storage)).toBeNull()
    expect(readDraftIndex(storage)).toHaveLength(0)
  })

  it('refuses a draft larger than the configured cap', () => {
    const storage = memoryStorage()
    const payload = { system_prompt: 'x'.repeat(DRAFT_MAX_BYTES) }
    expect(writeDraft('user', 'agent', 'big', payload, base, 1, storage)).toEqual({ ok: false, reason: 'too_large' })
    expect(storage.dump().size).toBe(0)
  })

  it('redacts credential-shaped keys at any depth', () => {
    const redacted = redactDraftSecrets({
      api_key: 'sk-live',
      nested: { access_token: 'abc', max_tokens: 4096, password: 'hunter2' },
      list: [{ client_secret: 's' }],
      display_name: 'keep me',
    })
    expect(redacted).toEqual({
      api_key: '[redacted]',
      nested: { access_token: '[redacted]', max_tokens: 4096, password: '[redacted]' },
      list: [{ client_secret: '[redacted]' }],
      display_name: 'keep me',
    })
  })

  it('never persists a credential value even when the form contains one', () => {
    const storage = memoryStorage()
    writeDraft('user', 'connection', 'c1', { api_key: 'sk-live', display_name: 'AMD' }, base, 1, storage)
    const raw = storage.dump().get(draftKey('user', 'connection', 'c1')) ?? ''
    expect(raw).not.toContain('sk-live')
    expect(readDraft('user', 'connection', 'c1', storage)?.payload.api_key).toBe('[redacted]')
  })

  it('treats corrupt storage as no draft', () => {
    const storage = memoryStorage()
    storage.setItem(draftKey('user', 'agent', 'a1'), '{not json')
    expect(readDraft('user', 'agent', 'a1', storage)).toBeNull()
  })

  it('rejects an invalid entity or payload', () => {
    const storage = memoryStorage()
    expect(writeDraft('user', 'agent', '', {}, base, 1, storage)).toEqual({ ok: false, reason: 'invalid' })
    expect(writeDraft('user', 'agent' as DraftEntity, 'a1', null as unknown as Record<string, unknown>, base, 1, storage))
      .toEqual({ ok: false, reason: 'invalid' })
  })

  it('clears every indexed draft', () => {
    const storage = memoryStorage()
    writeDraft('user', 'agent', 'a1', {}, base, 1, storage)
    writeDraft('project', 'team', 't1', {}, base, 2, storage)
    clearAllDrafts(storage)
    expect(readDraft('user', 'agent', 'a1', storage)).toBeNull()
    expect(readDraft('project', 'team', 't1', storage)).toBeNull()
    expect(storage.dump().has(DRAFT_INDEX_KEY)).toBe(false)
  })
})
