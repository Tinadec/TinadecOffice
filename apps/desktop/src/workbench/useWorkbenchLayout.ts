import { computed, onScopeDispose, ref, shallowRef, triggerRef, type Ref } from 'vue'
import { createWorkbenchPreset } from './presets'
import {
  clearLayoutScope,
  createPlatformLayoutStorage,
  createEmptyLayoutDocument,
  repairLayoutDocument,
  resolvePersistedLayout,
  storeLayoutAtScope,
} from './persistence'
import { reduceWorkbenchLayout } from './reducer'
import {
  WORKBENCH_PAGE_IDS,
  type LayoutScope,
  type WorkbenchCardDescriptor,
  type WorkbenchCommandSource,
  type WorkbenchLayoutCommand,
  type WorkbenchLayoutDocument,
  type WorkbenchLayoutSnapshot,
  type WorkbenchLayoutStorage,
  type WorkbenchPageId,
} from './types'

const HISTORY_LIMIT = 50
const SAVE_DELAY_MS = 250

type PageLayouts = Record<WorkbenchPageId, WorkbenchLayoutSnapshot>
type PageHistory = Record<WorkbenchPageId, WorkbenchLayoutCommand[]>

function initialLayouts(): PageLayouts {
  return Object.fromEntries(
    WORKBENCH_PAGE_IDS.map((pageId) => [pageId, createWorkbenchPreset(pageId)]),
  ) as PageLayouts
}

function initialHistory(): PageHistory {
  return Object.fromEntries(WORKBENCH_PAGE_IDS.map((pageId) => [pageId, []])) as unknown as PageHistory
}

export interface WorkbenchLayoutController {
  activePage: Ref<WorkbenchPageId>
  activeProjectId: Ref<string | null>
  layouts: Ref<PageLayouts>
  currentLayout: Readonly<Ref<WorkbenchLayoutSnapshot>>
  initialized: Readonly<Ref<boolean>>
  canUndo: Readonly<Ref<boolean>>
  canRedo: Readonly<Ref<boolean>>
  initialize: () => Promise<void>
  setActivePage: (pageId: WorkbenchPageId) => void
  setActiveProject: (projectId: string | null) => Promise<void>
  dispatch: (
    command: WorkbenchLayoutCommand,
    source?: WorkbenchCommandSource,
    coalesceKey?: string,
  ) => boolean
  updateCardState: (cardId: string, state: Record<string, unknown>) => boolean
  finishCoalescing: () => void
  undo: () => boolean
  redo: () => boolean
  saveAsGlobalDefault: () => Promise<void>
  saveAsPageDefault: () => Promise<void>
  resetCurrentScope: () => Promise<void>
  resetToBuiltIn: () => Promise<void>
  flush: () => Promise<void>
}

export function createWorkbenchLayoutController(
  descriptors: ReadonlyMap<string, WorkbenchCardDescriptor>,
  storage: WorkbenchLayoutStorage = createPlatformLayoutStorage(),
): WorkbenchLayoutController {
  const activePage = ref<WorkbenchPageId>('home')
  const activeProjectId = ref<string | null>(null)
  const layouts = shallowRef<PageLayouts>(initialLayouts())
  const document = shallowRef<WorkbenchLayoutDocument>(createEmptyLayoutDocument())
  const initialized = ref(false)
  const undoHistory = shallowRef<PageHistory>(initialHistory())
  const redoHistory = shallowRef<PageHistory>(initialHistory())
  const currentLayout = computed(() => layouts.value[activePage.value])
  const canUndo = computed(() => undoHistory.value[activePage.value].length > 0)
  const canRedo = computed(() => redoHistory.value[activePage.value].length > 0)

  let saveTimer: ReturnType<typeof setTimeout> | null = null
  let pendingSave = false
  let coalescingKey: string | null = null

  function activeScope(pageId = activePage.value): LayoutScope {
    return activeProjectId.value
      ? { kind: 'workspace-page', projectId: activeProjectId.value, pageId }
      : { kind: 'page', pageId }
  }

  function resolveAllLayouts(): void {
    const next = {} as PageLayouts
    for (const pageId of WORKBENCH_PAGE_IDS) {
      next[pageId] = resolvePersistedLayout(
        document.value,
        pageId,
        activeProjectId.value,
        descriptors,
      )
    }
    layouts.value = next
    undoHistory.value = initialHistory()
    redoHistory.value = initialHistory()
  }

  async function initialize(): Promise<void> {
    if (initialized.value) return
    try {
      document.value = repairLayoutDocument(await storage.load(), descriptors)
    } catch {
      document.value = createEmptyLayoutDocument()
    }
    resolveAllLayouts()
    initialized.value = true
  }

  function setActivePage(pageId: WorkbenchPageId): void {
    if (activePage.value === pageId) return
    activePage.value = pageId
    coalescingKey = null
  }

  async function setActiveProject(projectId: string | null): Promise<void> {
    if (projectId === activeProjectId.value) return
    await flush()
    activeProjectId.value = projectId
    resolveAllLayouts()
  }

  function trimHistory(history: WorkbenchLayoutCommand[]): void {
    if (history.length > HISTORY_LIMIT) history.splice(0, history.length - HISTORY_LIMIT)
  }

  function queueSave(pageId: WorkbenchPageId): void {
    document.value = storeLayoutAtScope(document.value, activeScope(pageId), layouts.value[pageId])
    pendingSave = true
    if (saveTimer) clearTimeout(saveTimer)
    saveTimer = setTimeout(() => {
      saveTimer = null
      void flush()
    }, SAVE_DELAY_MS)
  }

  function dispatch(
    command: WorkbenchLayoutCommand,
    source: WorkbenchCommandSource = 'user',
    nextCoalescingKey?: string,
  ): boolean {
    const pageId = activePage.value
    const current = layouts.value[pageId]
    const result = reduceWorkbenchLayout(
      current,
      { source, expectedRevision: current.revision, command },
      { descriptors, locked: pageId === 'settings' },
    )
    if (!result.ok) return false

    layouts.value = { ...layouts.value, [pageId]: result.state }
    if (!nextCoalescingKey || coalescingKey !== nextCoalescingKey) {
      undoHistory.value[pageId].push(result.inverse)
      trimHistory(undoHistory.value[pageId])
      triggerRef(undoHistory)
    }
    coalescingKey = nextCoalescingKey ?? null
    redoHistory.value[pageId] = []
    triggerRef(redoHistory)
    queueSave(pageId)
    return true
  }

  function finishCoalescing(): void {
    coalescingKey = null
  }

  function updateCardState(cardId: string, state: Record<string, unknown>): boolean {
    const pageId = activePage.value
    const current = layouts.value[pageId]
    const card = current.cards[cardId]
    if (!card) return false
    const descriptor = descriptors.get(card.type)
    const serialized = descriptor?.serializeState ? descriptor.serializeState(state) : structuredClone(state)
    const next = structuredClone(current)
    next.cards[cardId].state = serialized
    next.revision = current.revision + 1
    layouts.value = { ...layouts.value, [pageId]: next }
    queueSave(pageId)
    return true
  }

  function applyHistory(from: PageHistory, to: PageHistory): boolean {
    const pageId = activePage.value
    const command = from[pageId].pop()
    if (!command) return false
    const current = layouts.value[pageId]
    const result = reduceWorkbenchLayout(
      current,
      { source: 'user', expectedRevision: current.revision, command },
      { descriptors, locked: false },
    )
    if (!result.ok) return false
    layouts.value = { ...layouts.value, [pageId]: result.state }
    to[pageId].push(result.inverse)
    trimHistory(to[pageId])
    queueSave(pageId)
    return true
  }

  function undo(): boolean {
    finishCoalescing()
    const applied = applyHistory(undoHistory.value, redoHistory.value)
    triggerRef(undoHistory)
    triggerRef(redoHistory)
    return applied
  }

  function redo(): boolean {
    finishCoalescing()
    const applied = applyHistory(redoHistory.value, undoHistory.value)
    triggerRef(redoHistory)
    triggerRef(undoHistory)
    return applied
  }

  async function saveAsGlobalDefault(): Promise<void> {
    if (activePage.value === 'settings') return
    document.value = storeLayoutAtScope(document.value, { kind: 'global' }, currentLayout.value)
    pendingSave = true
    await flush()
  }

  async function saveAsPageDefault(): Promise<void> {
    document.value = storeLayoutAtScope(
      document.value,
      { kind: 'page', pageId: activePage.value },
      currentLayout.value,
    )
    pendingSave = true
    await flush()
  }

  async function resetCurrentScope(): Promise<void> {
    document.value = clearLayoutScope(document.value, activeScope())
    layouts.value = {
      ...layouts.value,
      [activePage.value]: resolvePersistedLayout(
        document.value,
        activePage.value,
        activeProjectId.value,
        descriptors,
      ),
    }
    undoHistory.value[activePage.value] = []
    redoHistory.value[activePage.value] = []
    triggerRef(undoHistory)
    triggerRef(redoHistory)
    pendingSave = true
    await flush()
  }

  async function resetToBuiltIn(): Promise<void> {
    const pageId = activePage.value
    const preset = createWorkbenchPreset(pageId)
    layouts.value = { ...layouts.value, [pageId]: preset }
    document.value = storeLayoutAtScope(document.value, activeScope(pageId), preset)
    undoHistory.value[pageId] = []
    redoHistory.value[pageId] = []
    triggerRef(undoHistory)
    triggerRef(redoHistory)
    pendingSave = true
    await flush()
  }

  async function flush(): Promise<void> {
    if (saveTimer) {
      clearTimeout(saveTimer)
      saveTimer = null
    }
    if (!pendingSave) return
    pendingSave = false
    try {
      document.value = await storage.save(document.value)
    } catch {
      // Keep the latest document dirty so a later explicit flush can retry.
      pendingSave = true
    }
  }

  onScopeDispose(() => {
    if (saveTimer) clearTimeout(saveTimer)
    if (pendingSave) void storage.save(document.value)
  })

  return {
    activePage,
    activeProjectId,
    layouts,
    currentLayout,
    initialized,
    canUndo,
    canRedo,
    initialize,
    setActivePage,
    setActiveProject,
    dispatch,
    updateCardState,
    finishCoalescing,
    undo,
    redo,
    saveAsGlobalDefault,
    saveAsPageDefault,
    resetCurrentScope,
    resetToBuiltIn,
    flush,
  }
}
