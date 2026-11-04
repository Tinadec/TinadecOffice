/**
 * Minimal storage seam shared by the settings draft/UI-state modules.
 *
 * Tests inject a Map-backed implementation; the app defaults to `localStorage`
 * and degrades to a no-op when the host refuses it (private mode, quota, or an
 * Electron window without storage access). Persistence is always optional: a
 * refused write must never break the editor.
 */
export interface KeyValueStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
  removeItem(key: string): void
}

const memory = new Map<string, string>()

const memoryStorage: KeyValueStorage = {
  getItem: key => memory.get(key) ?? null,
  setItem: (key, value) => { memory.set(key, value) },
  removeItem: key => { memory.delete(key) },
}

export function defaultStorage(): KeyValueStorage {
  try {
    const storage = globalThis.localStorage
    if (storage) return storage
  } catch {
    // Accessing localStorage itself can throw when the host blocks storage.
  }
  return memoryStorage
}

/** Storage writes are best effort; callers decide whether a refusal is worth surfacing. */
export function trySet(storage: KeyValueStorage, key: string, value: string): boolean {
  try {
    storage.setItem(key, value)
    return true
  } catch {
    return false
  }
}

export function safeReadJson(storage: KeyValueStorage, key: string): unknown {
  try {
    const raw = storage.getItem(key)
    return raw === null ? null : JSON.parse(raw)
  } catch {
    return null
  }
}

export function safeRemove(storage: KeyValueStorage, key: string): void {
  try {
    storage.removeItem(key)
  } catch {
    // Removing a key that the host already dropped is not an error.
  }
}
