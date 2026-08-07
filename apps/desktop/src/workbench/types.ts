import type { Component } from 'vue'

// ---------------------------------------------------------------------------
// Workbench layout engine — core types.
//
// This module is a pure TypeScript layout authority: it owns the layout state,
// the command/reducer semantics, the undo/redo stack, scope resolution, repair,
// presets, and the constraint solver. It deliberately has no DOM/Vue dependency
// so every part can be unit-tested without mounting components.
// ---------------------------------------------------------------------------

export type WorkbenchPageId = 'home' | 'settings' | 'market' | 'code' | 'debug'
export type WorkbenchSlotId = 'left' | 'center' | 'right'
/** secondary only exists when a column is vertically split. */
export type WorkbenchStackId = 'primary' | 'secondary'

export type LayoutScope =
  | { kind: 'global' }
  | { kind: 'page'; pageId: WorkbenchPageId }
  | { kind: 'workspace-page'; projectId: string; pageId: WorkbenchPageId }

/** Source of a layout command. `ai` is reserved and rejected this round. */
export type LayoutSource = 'user' | 'route' | 'restore' | 'ai'

/** Floating-panel look vs. connected application look. */
export type SurfaceMode = 'float' | 'app'
export type CardTitlebarMode = 'hidden' | 'minimal' | 'full'

/** Versioned, pure-data layout snapshot. version:1 is fixed for this round. */
export interface WorkbenchLayoutSnapshot {
  version: 1
  /** Monotonic counter for optimistic concurrency (expectedRevision checks). */
  revision: number
  pageId: WorkbenchPageId
  columnOrder: WorkbenchSlotId[]
  columns: Record<WorkbenchSlotId, WorkbenchColumn>
  /** instanceId -> instance. Cards not in any stack are still listed here. */
  cards: Record<string, PersistedCardInstance>
  focusedCardId: string | null
  /** Column gap (px). home/settings = 8, market = 1. */
  gap: number
  /** Window-edge inset (px). home/settings = 8 (float pages); market/code/debug = 0 (app pages, flush to edges). */
  edgeInset: number
}

export interface WorkbenchColumn {
  slotId: WorkbenchSlotId
  /** Width in px. Collapsed columns render at the collapsed width. */
  width: number
  collapsed: boolean
  surfaceMode: SurfaceMode
  /** Top inset (px) so floating cards clear the window chrome. */
  topInset: number
  primary: WorkbenchStack
  secondary: WorkbenchStack | null
  /** Split ratio (0..1) dividing primary (top) and secondary (bottom). */
  splitRatio: number | null
}

export interface WorkbenchStack {
  stackId: WorkbenchStackId
  /** Ordered card instanceIds. */
  tabIds: string[]
  activeTabId: string | null
}

export interface PersistedCardInstance {
  /** instanceId — globally unique across the app. */
  id: string
  /** Registry key (e.g. 'git', 'chat', 'browser'). */
  descriptorId: string
  title: string
  /** Serializable card state (preview URL, sessionId, etc.). */
  state?: Record<string, unknown>
}

/** Card descriptor — the component is NOT inlined into the snapshot. */
export interface WorkbenchCardDescriptor {
  type: string
  component: Component
  minWidth: number
  minHeight: number
  singleton: boolean
  movable: boolean
  closable: boolean
  detachable: boolean
  defaultTitle: string
  titlebarMode?: CardTitlebarMode
  /** Serialize instance state for detach/persistence. */
  serializeState?: (instance: PersistedCardInstance) => Record<string, unknown>
}

/** Result of the constraint solver (derived, never persisted). */
export interface WorkbenchGeometry {
  columns: Record<WorkbenchSlotId, ColumnGeometry>
  splits: Record<string, SplitGeometry>
  /** Visual-only degradations — never written back to the layout. */
  degraded: {
    collapsedRight: boolean
    collapsedLeft: boolean
    degradedSplits: string[]
  }
}

export interface ColumnGeometry {
  slotId: WorkbenchSlotId
  x: number
  y: number
  width: number
  height: number
  /** Actual width used (collapsed width if collapsed). */
  effectiveWidth: number
  /** Top inset carried over from the column (rendering anchor). */
  topInset: number
}

export interface SplitGeometry {
  slotId: WorkbenchSlotId
  dividerY: number
  upper: StackGeometry
  lower: StackGeometry
}

export interface StackGeometry {
  x: number
  y: number
  width: number
  height: number
  /** Whether this stack was visually degraded into a single stack. */
  degraded: boolean
}

/** Container size fed into the constraint solver. */
export interface WorkbenchContainerSize {
  width: number
  height: number
}

export const COLLAPSED_COLUMN_WIDTH = 44
export const DEFAULT_GAP = 8
/** Window-edge inset for float pages (home/settings). App pages use 0. */
export const EDGE_INSET = 8
export const UNDO_LIMIT = 50
