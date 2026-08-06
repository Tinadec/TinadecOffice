import { createWorkbenchPreset } from './presets'
import { repairWorkbenchLayout } from './repair'
import {
  WORKBENCH_PAGE_IDS,
  type LayoutScope,
  type WorkbenchCardDescriptor,
  type WorkbenchLayoutDocument,
  type WorkbenchLayoutSnapshot,
  type WorkbenchLayoutStorage,
  type WorkbenchPageId,
} from './types'

export const WORKBENCH_LAYOUT_STORAGE_KEY = 'tinadec-workbench-layout-v1'

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function createEmptyLayoutDocument(): WorkbenchLayoutDocument {
  return {
    version: 1,
    revision: 0,
    globalDefault: null,
    pages: {},
    workspacePages: {},
  }
}

export function repairLayoutDocument(
  value: unknown,
  descriptors?: ReadonlyMap<string, WorkbenchCardDescriptor>,
): WorkbenchLayoutDocument {
  if (!isRecord(value) || value.version !== 1) return createEmptyLayoutDocument()
  const document = createEmptyLayoutDocument()
  document.revision = typeof value.revision === 'number' && Number.isFinite(value.revision)
    ? Math.max(0, Math.trunc(value.revision))
    : 0

  if (isRecord(value.globalDefault) && typeof value.globalDefault.pageId === 'string') {
    const pageId = value.globalDefault.pageId as WorkbenchPageId
    if ((WORKBENCH_PAGE_IDS as readonly string[]).includes(pageId)) {
      document.globalDefault = repairWorkbenchLayout(value.globalDefault, pageId, descriptors)
    }
  }

  if (isRecord(value.pages)) {
    for (const pageId of WORKBENCH_PAGE_IDS) {
      if (value.pages[pageId]) document.pages[pageId] = repairWorkbenchLayout(value.pages[pageId], pageId, descriptors)
    }
  }

  if (isRecord(value.workspacePages)) {
    for (const [projectId, rawPages] of Object.entries(value.workspacePages)) {
      if (!projectId || !isRecord(rawPages)) continue
      const repairedPages: Partial<Record<WorkbenchPageId, WorkbenchLayoutSnapshot>> = {}
      for (const pageId of WORKBENCH_PAGE_IDS) {
        if (rawPages[pageId]) repairedPages[pageId] = repairWorkbenchLayout(rawPages[pageId], pageId, descriptors)
      }
      if (Object.keys(repairedPages).length > 0) document.workspacePages[projectId] = repairedPages
    }
  }

  return document
}

function applyGlobalGeometry(
  preset: WorkbenchLayoutSnapshot,
  globalDefault: WorkbenchLayoutSnapshot | null,
): WorkbenchLayoutSnapshot {
  if (!globalDefault || preset.pageId === 'settings') return preset
  const next = structuredClone(preset)
  next.columnOrder = [...globalDefault.columnOrder]
  for (const slotId of next.columnOrder) {
    next.columns[slotId].width = globalDefault.columns[slotId].width
    next.columns[slotId].collapsed = globalDefault.columns[slotId].collapsed
    next.columns[slotId].splitRatio = globalDefault.columns[slotId].splitRatio
  }
  return next
}

export function resolvePersistedLayout(
  document: WorkbenchLayoutDocument,
  pageId: WorkbenchPageId,
  projectId: string | null,
  descriptors?: ReadonlyMap<string, WorkbenchCardDescriptor>,
): WorkbenchLayoutSnapshot {
  if (projectId) {
    const workspace = document.workspacePages[projectId]?.[pageId]
    if (workspace) return repairWorkbenchLayout(workspace, pageId, descriptors)
  }
  const page = document.pages[pageId]
  if (page) return repairWorkbenchLayout(page, pageId, descriptors)
  return applyGlobalGeometry(createWorkbenchPreset(pageId), document.globalDefault)
}

export function storeLayoutAtScope(
  document: WorkbenchLayoutDocument,
  scope: LayoutScope,
  snapshot: WorkbenchLayoutSnapshot,
): WorkbenchLayoutDocument {
  const next = structuredClone(document)
  next.revision += 1
  const stored = structuredClone(snapshot)
  if (scope.kind === 'global') {
    next.globalDefault = stored
  } else if (scope.kind === 'page') {
    next.pages[scope.pageId] = stored
  } else {
    next.workspacePages[scope.projectId] ??= {}
    next.workspacePages[scope.projectId][scope.pageId] = stored
  }
  return next
}

export function clearLayoutScope(
  document: WorkbenchLayoutDocument,
  scope: LayoutScope,
): WorkbenchLayoutDocument {
  const next = structuredClone(document)
  next.revision += 1
  if (scope.kind === 'global') next.globalDefault = null
  else if (scope.kind === 'page') delete next.pages[scope.pageId]
  else {
    delete next.workspacePages[scope.projectId]?.[scope.pageId]
    if (Object.keys(next.workspacePages[scope.projectId] ?? {}).length === 0) {
      delete next.workspacePages[scope.projectId]
    }
  }
  return next
}

export function createBrowserLayoutStorage(storage: Storage = localStorage): WorkbenchLayoutStorage {
  return {
    async load() {
      const raw = storage.getItem(WORKBENCH_LAYOUT_STORAGE_KEY)
      if (!raw) return null
      try {
        return JSON.parse(raw) as unknown
      } catch {
        return null
      }
    },
    async save(document) {
      storage.setItem(WORKBENCH_LAYOUT_STORAGE_KEY, JSON.stringify(document))
      return document
    },
  }
}

export function createPlatformLayoutStorage(): WorkbenchLayoutStorage {
  const platformStorage = typeof window !== 'undefined' ? window.tinadec?.workbenchLayout : undefined
  if (platformStorage) {
    return {
      load: () => platformStorage.load(),
      save: (document) => platformStorage.save(document).then(() => document),
    }
  }
  return createBrowserLayoutStorage()
}
