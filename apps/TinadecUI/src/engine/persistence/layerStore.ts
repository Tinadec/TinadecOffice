import type { UieLayoutSnapshot, UiePageId } from '../types'
import type { LayoutAdapter, LayoutStorageBlob } from './types'
import { pageKey, workspaceKey } from './types'
import { writeScopeFor } from '../scope'
import type { LayoutScope } from '../types'

// ---------------------------------------------------------------------------
// Layer store — reads/writes the three scope layers with
// "most specific valid wins" resolution, plus debounced auto-save.
// ---------------------------------------------------------------------------

export interface LayerStore {
  /** Resolve the effective snapshot for the current page/project. */
  resolveSnapshot(pageId: UiePageId, activeProjectId: string | null): UieLayoutSnapshot | null
  /** Save a snapshot to the current write scope (project-scoped when active). */
  saveSnapshot(snapshot: UieLayoutSnapshot, activeProjectId: string | null): void
  /** Set the layout as the global default for a page. */
  saveAsGlobal(snapshot: UieLayoutSnapshot): void
  /** Set the layout as the page-wide default. */
  saveAsPage(snapshot: UieLayoutSnapshot): void
  /** Delete the workspace-page override, falling back to page/global. */
  resetWorkspace(pageId: UiePageId, activeProjectId: string | null): void
  /** Hydrate from disk (async). Returns the loaded blob or null. */
  hydrate(): Promise<LayoutStorageBlob | null>
  /** Flush any pending debounced save immediately. */
  flush(): void
}

const DEBOUNCE_MS = 400

export function createLayerStore(adapter: LayoutAdapter): LayerStore {
  let blob: LayoutStorageBlob = { version: 1 }
  let hydrated = false
  let timer: ReturnType<typeof setTimeout> | null = null

  async function hydrate(): Promise<LayoutStorageBlob | null> {
    const loaded = await adapter.load()
    if (loaded && typeof loaded === 'object' && (loaded as LayoutStorageBlob).version === 1) {
      blob = loaded as LayoutStorageBlob
    }
    hydrated = true
    return blob
  }

  function scheduleSave() {
    if (timer) clearTimeout(timer)
    timer = setTimeout(() => {
      timer = null
      void adapter.save(blob)
    }, DEBOUNCE_MS)
  }

  function flush() {
    if (timer) {
      clearTimeout(timer)
      timer = null
      void adapter.save(blob)
    }
  }

  function resolveSnapshot(pageId: UiePageId, activeProjectId: string | null): UieLayoutSnapshot | null {
    // Most specific valid wins: workspace-page > page > global (per-page) > null.
    if (activeProjectId) {
      const ws = blob.workspaceByKey?.[workspaceKey(activeProjectId, pageId)]
      if (ws) return ws
    }
    const page = blob.pageByPageId?.[pageId]
    if (page) return page
    const global = blob.globalByPage?.[pageId]
    if (global) return global
    return null
  }

  function saveSnapshot(snapshot: UieLayoutSnapshot, activeProjectId: string | null): void {
    const scope = writeScopeFor(snapshot.pageId, activeProjectId)
    setSnapshot(scope, snapshot, blob)
    scheduleSave()
  }

  function saveAsGlobal(snapshot: UieLayoutSnapshot): void {
    blob = { ...blob, globalByPage: { ...(blob.globalByPage ?? {}), [pageKey(snapshot.pageId)]: snapshot } }
    scheduleSave()
  }

  function saveAsPage(snapshot: UieLayoutSnapshot): void {
    blob = { ...blob, pageByPageId: { ...(blob.pageByPageId ?? {}), [pageKey(snapshot.pageId)]: snapshot } }
    scheduleSave()
  }

  function resetWorkspace(pageId: UiePageId, activeProjectId: string | null): void {
    if (!activeProjectId) return
    const key = workspaceKey(activeProjectId, pageId)
    const next = { ...(blob.workspaceByKey ?? {}) }
    delete next[key]
    blob = { ...blob, workspaceByKey: next }
    scheduleSave()
  }

  return { resolveSnapshot, saveSnapshot, saveAsGlobal, saveAsPage, resetWorkspace, hydrate, flush }
}

// --- Internal blob accessors ---

function setSnapshot(scope: LayoutScope, snapshot: UieLayoutSnapshot, blob: LayoutStorageBlob): void {
  switch (scope.kind) {
    case 'global':
      blob.globalByPage = { ...(blob.globalByPage ?? {}), [pageKey(snapshot.pageId)]: snapshot }
      break
    case 'page':
      blob.pageByPageId = { ...(blob.pageByPageId ?? {}), [scope.pageId]: snapshot }
      break
    case 'workspace-page':
      blob.workspaceByKey = { ...(blob.workspaceByKey ?? {}), [workspaceKey(scope.projectId, scope.pageId)]: snapshot }
      break
  }
}

export { DEBOUNCE_MS }
