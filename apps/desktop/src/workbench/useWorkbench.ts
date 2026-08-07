import { computed, ref, shallowRef, type Ref } from 'vue'
import { createCommandBus, type CommandBus } from './commandBus'
import type { CardRegistry } from './registry'
import { buildPreset } from './presets'
import { repairLayout, type RepairContext } from './repair'
import { computeGeometry } from './constraints'
import { createInstancePool, type InstancePool } from './instancePool'
import type {
  LayoutScope,
  WorkbenchContainerSize,
  WorkbenchGeometry,
  WorkbenchLayoutSnapshot,
  WorkbenchPageId,
  WorkbenchSlotId,
} from './types'
import type { WorkbenchCommand, WorkbenchCommandEnvelope } from './commands'
import type { Component } from 'vue'

// ---------------------------------------------------------------------------
// useWorkbench — the single reactive store for the Workbench layout.
//
// Owns:
//   - the command bus (single layout authority)
//   - the card registry + instance pool
//   - the current layout scope + snapshot
//   - the computed geometry (derived from the snapshot + container size)
//   - undo/redo access
// ---------------------------------------------------------------------------

export interface WorkbenchStore {
  bus: CommandBus
  registry: CardRegistry
  pool: InstancePool
  snapshot: Ref<WorkbenchLayoutSnapshot>
  pageId: Ref<WorkbenchPageId>
  scope: Ref<LayoutScope>
  geometry: Ref<WorkbenchGeometry>
  containerSize: Ref<WorkbenchContainerSize>
  focusedCardId: Ref<string | null>
  dispatch(envelope: WorkbenchCommandEnvelope, options?: { gestureId?: string }): boolean
  /** Apply a page preset (source: route) — keeps layout but swaps page content. */
  applyPreset(pageId: WorkbenchPageId): void
  /** Restore a snapshot (used for persistence hydration). */
  restoreSnapshot(snapshot: WorkbenchLayoutSnapshot): void
  /** Update the container size (observed by the canvas). */
  setContainerSize(size: WorkbenchContainerSize): void
  undo(): boolean
  redo(): boolean
  canUndo: Ref<boolean>
  canRedo: Ref<boolean>
  componentFor(descriptorId: string): Component | undefined
  setActiveProjectId(projectId: string | null): void
  activeProjectId: Ref<string | null>
}

export interface WorkbenchStoreOptions {
  registry: CardRegistry
  /** Map descriptorId -> Vue component for the instance pool. */
  componentFor?: (descriptorId: string) => Component | undefined
  lockedSlots?: ReadonlySet<WorkbenchSlotId> | readonly WorkbenchSlotId[]
  /** Optional initial snapshot (from persistence); falls back to home preset. */
  initialSnapshot?: WorkbenchLayoutSnapshot
  activeProjectId?: string | null
  /** Optional layer store for layout persistence (auto-save on change). */
  persistence?: {
    store: import('./persistence/layerStore').LayerStore
  }
}

let instanceCounter = 0
export function createWorkbenchInstanceId(): string {
  return `wb-${++instanceCounter}-${Math.random().toString(36).slice(2, 7)}`
}

export function createWorkbench(options: WorkbenchStoreOptions): WorkbenchStore {
  const { registry } = options
  const componentFor = options.componentFor ?? (() => undefined)

  // Instance pool keyed by descriptorId -> component lookup.
  const pool = createInstancePool(componentFor)

  const initialPage: WorkbenchPageId = options.initialSnapshot?.pageId ?? 'home'
  const initial =
    options.initialSnapshot ??
    buildPreset(initialPage, { nextInstanceId: createWorkbenchInstanceId })

  const activeProjectId = ref<string | null>(options.activeProjectId ?? null)
  const scope = ref<LayoutScope>(
    activeProjectId.value
      ? { kind: 'workspace-page', projectId: activeProjectId.value, pageId: initialPage }
      : { kind: 'page', pageId: initialPage },
  )

  const snapshot = shallowRef<WorkbenchLayoutSnapshot>(initial)
  const containerSize = ref<WorkbenchContainerSize>({ width: 1440, height: 920 })
  const geometry = shallowRef<WorkbenchGeometry>(
    computeGeometry(containerSize.value, initial),
  )

  const canUndoRef = ref(false)
  const canRedoRef = ref(false)

  const bus = createCommandBus(initial, {
    registry,
    nextInstanceId: createWorkbenchInstanceId,
    lockedSlots: options.lockedSlots,
    onChanged: (next) => {
      snapshot.value = next
      geometry.value = computeGeometry(containerSize.value, next)
      canUndoRef.value = bus.canUndo()
      canRedoRef.value = bus.canRedo()
      // Auto-save to the current write scope (project-scoped when a project is active).
      options.persistence?.store.saveSnapshot(next, activeProjectId.value)
    },
  })

  // Hydrate from disk asynchronously: if a stored layout exists for the current
  // page/project, repair + restore it so the user's layout survives restarts.
  // A failed hydrate/restore must never become an unhandled rejection or take
  // down the renderer — on failure we keep the built-in preset (repairLayout in
  // restoreSnapshot is already the blank-window guard).
  if (options.persistence) {
    const layerStore = options.persistence.store
    void layerStore
      .hydrate()
      .then((loaded) => {
        if (!loaded) return
        const stored = layerStore.resolveSnapshot(initialPage, activeProjectId.value)
        if (stored) {
          restoreSnapshot(stored)
        }
      })
      .catch((err) => {
        console.error('[workbench] layout hydrate/restore failed:', err)
      })

    // Flush the debounced auto-save on unload so Ctrl+R (full renderer reload)
    // never loses the last 400ms of changes and no debounce timer races the
    // reload. flush() is idempotent, so the double fire from beforeunload +
    // pagehide is a no-op.
    if (typeof window !== 'undefined') {
      const flush = () => layerStore.flush()
      window.addEventListener('beforeunload', flush)
      window.addEventListener('pagehide', flush)
    }
  }

  const pageId = computed<WorkbenchPageId>(() => snapshot.value.pageId)
  const focusedCardId = computed<string | null>(() => snapshot.value.focusedCardId)

  function restoreSnapshot(next: WorkbenchLayoutSnapshot) {
    // Run through repair so corrupt state never renders blank.
    const repaired = repairLayout(next, {
      registry,
      preset: { nextInstanceId: createWorkbenchInstanceId },
    })
    bus.setSnapshot(repaired)
  }

  return {
    bus,
    registry,
    pool,
    snapshot,
    pageId,
    scope,
    geometry,
    containerSize,
    focusedCardId,
    dispatch(envelope, opts) {
      const ok = bus.dispatch(envelope, opts)
      return ok
    },
    applyPreset(nextPage) {
      const next = buildPreset(nextPage, { nextInstanceId: createWorkbenchInstanceId })
      bus.setSnapshot(next)
      scope.value = activeProjectId.value
        ? { kind: 'workspace-page', projectId: activeProjectId.value, pageId: nextPage }
        : { kind: 'page', pageId: nextPage }
    },
    restoreSnapshot,
    setContainerSize(size) {
      const { width, height } = size
      const prev = containerSize.value
      // Guard against ResizeObserver feedback loops: only re-render geometry when
      // the container size actually changed (rounded to integer pixels).
      if (prev.width === width && prev.height === height) return
      const next = { width, height }
      containerSize.value = next
      geometry.value = computeGeometry(next, snapshot.value)
    },
    undo() {
      const ok = bus.undo()
      canUndoRef.value = bus.canUndo()
      canRedoRef.value = bus.canRedo()
      return ok !== undefined
    },
    redo() {
      const ok = bus.redo()
      canUndoRef.value = bus.canUndo()
      canRedoRef.value = bus.canRedo()
      return ok !== undefined
    },
    canUndo: canUndoRef,
    canRedo: canRedoRef,
    componentFor,
    setActiveProjectId(id) {
      activeProjectId.value = id
      scope.value = id
        ? { kind: 'workspace-page', projectId: id, pageId: snapshot.value.pageId }
        : { kind: 'page', pageId: snapshot.value.pageId }
    },
    activeProjectId,
  }
}

/** A module-level singleton for the running app (created on first use). */
let store: WorkbenchStore | null = null
export function useWorkbench(): WorkbenchStore {
  if (!store) throw new Error('useWorkbench: store not initialized. Call initWorkbench() first.')
  return store
}

export function initWorkbench(options: WorkbenchStoreOptions): WorkbenchStore {
  // Idempotent: keep the existing singleton so layout state survives route changes.
  if (store) return store
  store = createWorkbench(options)
  return store
}

export function __resetWorkbenchForTests(): void {
  store = null
}
