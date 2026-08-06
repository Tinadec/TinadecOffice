import type { WorkbenchLayoutSnapshot, WorkbenchPageId } from '../types'

// ---------------------------------------------------------------------------
// Layout persistence — storage blob + adapter interface.
//
// The blob holds three layers of layouts, resolved most-specific-first:
//   workspaceByKey[`${projectId}:${pageId}`]  (workspace-page scope)
//   pageByPageId[pageId]                       (page scope)
//   globalByPage[pageId]                       (global default template)
// The `version` is the blob's own version (1), independent of snapshot.version.
// ---------------------------------------------------------------------------

export interface LayoutStorageBlob {
  version: 1
  /** Layout used as the global default template, keyed by page. */
  globalByPage?: Record<string, WorkbenchLayoutSnapshot>
  /** Layout used for a whole page across all workspaces. */
  pageByPageId?: Record<string, WorkbenchLayoutSnapshot>
  /** Layout scoped to a specific project + page. */
  workspaceByKey?: Record<string, WorkbenchLayoutSnapshot>
}

export interface LayoutAdapter {
  load(): Promise<LayoutStorageBlob | null>
  save(blob: LayoutStorageBlob): Promise<boolean>
}

/** Electron adapter — persists via the main process (userData). */
export function createElectronLayoutAdapter(): LayoutAdapter {
  return {
    async load() {
      const tinadec = (window as unknown as { tinadec?: { layout?: { load?: () => Promise<unknown> } } }).tinadec
      if (!tinadec?.layout?.load) return null
      const raw = await tinadec.layout.load()
      if (!raw || typeof raw !== 'object') return null
      return raw as LayoutStorageBlob
    },
    async save(blob) {
      const tinadec = (window as unknown as { tinadec?: { layout?: { save?: (payload: unknown) => Promise<{ ok?: boolean }> } } }).tinadec
      if (!tinadec?.layout?.save) return false
      const result = await tinadec.layout.save(blob)
      return result?.ok === true
    },
  }
}

/** Page-scope key helper. */
export function pageKey(pageId: WorkbenchPageId): string {
  return pageId
}

/** Workspace-page scope key helper. */
export function workspaceKey(projectId: string, pageId: WorkbenchPageId): string {
  return `${projectId}:${pageId}`
}
