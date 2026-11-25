import type { WorkspaceFileGrant, WorkspaceRoot } from '@/lib/workspaces'

export type WorkspaceUnifiedItem =
  | { key: `root:${string}`; kind: 'root'; id: string; root: WorkspaceRoot }
  | { key: `file:${string}`; kind: 'file'; id: string; file: WorkspaceFileGrant }

export const WORKSPACE_ORDER_STORAGE_PREFIX = 'tinadec:workspace-item-order:'

export function reconcileWorkspaceItemOrder(
  roots: readonly WorkspaceRoot[],
  files: readonly WorkspaceFileGrant[],
  savedOrderKeys: readonly string[],
): WorkspaceUnifiedItem[] {
  const byKey = new Map<string, WorkspaceUnifiedItem>()
  const naturalOrder: WorkspaceUnifiedItem[] = []

  for (const root of roots) {
    const key: `root:${string}` = `root:${root.id}`
    if (!byKey.has(key)) {
      const item: WorkspaceUnifiedItem = {
        key,
        kind: 'root',
        id: root.id,
        root,
      }
      byKey.set(key, item)
      naturalOrder.push(item)
    }
  }

  for (const file of files) {
    const key: `file:${string}` = `file:${file.id}`
    if (!byKey.has(key)) {
      const item: WorkspaceUnifiedItem = {
        key,
        kind: 'file',
        id: file.id,
        file,
      }
      byKey.set(key, item)
      naturalOrder.push(item)
    }
  }

  const result: WorkspaceUnifiedItem[] = []
  const placed = new Set<string>()

  for (const savedKey of savedOrderKeys) {
    if (placed.has(savedKey)) continue
    const matched = byKey.get(savedKey)
    if (matched) {
      result.push(matched)
      placed.add(savedKey)
    }
  }

  for (const item of naturalOrder) {
    if (!placed.has(item.key)) {
      result.push(item)
      placed.add(item.key)
    }
  }

  return result
}

export function reorderKeys(
  keys: readonly string[],
  movedKey: string,
  targetIndex: number,
): string[] {
  const next = [...keys]
  if (next.length <= 1 || !Number.isFinite(targetIndex)) {
    return next
  }

  const fromIndex = next.indexOf(movedKey)
  if (fromIndex === -1) {
    return next
  }

  const clampedTarget = Math.max(0, Math.min(next.length - 1, Math.trunc(targetIndex)))
  if (clampedTarget === fromIndex) {
    return next
  }

  const [moved] = next.splice(fromIndex, 1)
  if (moved === undefined) {
    return next
  }
  next.splice(clampedTarget, 0, moved)
  return next
}

export function mergeReorderedVisibleKeys(
  allKeys: readonly string[],
  reorderedVisibleKeys: readonly string[],
): string[] {
  if (allKeys.length === 0) {
    return []
  }

  const validSet = new Set<string>(allKeys)
  const seen = new Set<string>()
  const merged: string[] = []

  for (const key of reorderedVisibleKeys) {
    if (validSet.has(key) && !seen.has(key)) {
      seen.add(key)
      merged.push(key)
    }
  }

  for (const key of allKeys) {
    if (!seen.has(key)) {
      seen.add(key)
      merged.push(key)
    }
  }

  return merged
}

export function loadWorkspaceItemOrder(projectId: string | null | undefined): string[] {
  const normalizedId = typeof projectId === 'string' ? projectId.trim() : ''
  if (!normalizedId) {
    return []
  }

  try {
    const storage = globalThis.localStorage
    if (!storage) {
      return []
    }
    const raw = storage.getItem(`${WORKSPACE_ORDER_STORAGE_PREFIX}${normalizedId}`)
    if (!raw) {
      return []
    }
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) {
      return []
    }

    const result: string[] = []
    const seen = new Set<string>()
    for (const entry of parsed) {
      if (typeof entry === 'string' && entry.length > 0 && !seen.has(entry)) {
        seen.add(entry)
        result.push(entry)
      }
    }
    return result
  } catch {
    return []
  }
}

export function saveWorkspaceItemOrder(
  projectId: string | null | undefined,
  orderedKeys: readonly string[],
): void {
  const normalizedId = typeof projectId === 'string' ? projectId.trim() : ''
  if (!normalizedId) {
    return
  }

  try {
    const storage = globalThis.localStorage
    if (!storage) {
      return
    }
    const cleaned: string[] = []
    const seen = new Set<string>()
    for (const entry of orderedKeys) {
      if (typeof entry === 'string' && entry.length > 0 && !seen.has(entry)) {
        seen.add(entry)
        cleaned.push(entry)
      }
    }
    storage.setItem(`${WORKSPACE_ORDER_STORAGE_PREFIX}${normalizedId}`, JSON.stringify(cleaned))
  } catch {
    // Ignore private-browsing or quota storage errors
  }
}
