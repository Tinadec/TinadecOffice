import type { Component } from 'vue'

// ---------------------------------------------------------------------------
// Uie layout engine — core types.
//
// This module is a pure TypeScript layout authority: it owns the layout state,
// the command/reducer semantics, the undo/redo stack, scope resolution, repair,
// presets, and the constraint solver. It deliberately has no DOM/Vue dependency
// so every part can be unit-tested without mounting components.
// ---------------------------------------------------------------------------

export type UiePageId = 'home' | 'settings' | 'market' | 'code' | 'debug'
export type UieSlotId = 'left' | 'center' | 'right'
/** secondary only exists when a column is vertically split. */
export type UieStackId = 'primary' | 'secondary'

export type LayoutScope =
  | { kind: 'global' }
  | { kind: 'page'; pageId: UiePageId }
  | { kind: 'workspace-page'; projectId: string; pageId: UiePageId }

/** Source of a layout command. `ai` is reserved and rejected this round. */
export type LayoutSource = 'user' | 'route' | 'restore' | 'ai'

/**
 * Column surface look.
 * - `float`: floating panel — rounded, shadowed, material root (sidebar/right rail on Home).
 * - `app`: connected application look — flush, no radius/shadow (market/code/debug).
 * - `immersive`: transparent immersive zone (Home chat column) — the root carries no
 *   material and the page background shows through; inner objects (composer, welcome
 *   dialog, bubbles) still carry material and follow the global panel effect via
 *   the inherited `data-panel-effect` surface tokens.
 */
export type SurfaceMode = 'float' | 'app' | 'immersive'
export type CardTitlebarMode = 'hidden' | 'minimal' | 'full'

/** Versioned, pure-data layout snapshot. version:1 is fixed for this round. */
export interface UieLayoutSnapshot {
  version: 1
  /** Monotonic counter for optimistic concurrency (expectedRevision checks). */
  revision: number
  pageId: UiePageId
  columnOrder: UieSlotId[]
  columns: Record<UieSlotId, UieColumn>
  /** instanceId -> instance. Cards not in any stack are still listed here. */
  cards: Record<string, PersistedCardInstance>
  focusedCardId: string | null
  /** Column gap (px). home/settings = 8, market = 1. */
  gap: number
  /** Window-edge inset (px). home/settings = 8 (float pages); market/code/debug = 0 (app pages, flush to edges). */
  edgeInset: number
}

export interface UieColumn {
  slotId: UieSlotId
  /** Width in px. Collapsed columns render at the collapsed width. */
  width: number
  collapsed: boolean
  surfaceMode: SurfaceMode
  /** Top inset (px) so floating cards clear the window chrome. */
  topInset: number
  primary: UieStack
  secondary: UieStack | null
  /** Split ratio (0..1) dividing primary (top) and secondary (bottom). */
  splitRatio: number | null
  /**
   * Dock layout (feature/right column). A recursive split tree of panes.
   * When non-null it REPLACES primary/secondary rendering and the two are
   * mutually exclusive: primary.tabIds=[], secondary=null, splitRatio=null.
   * Invariants (reducer + repair):
   *   - exactly one pane has `main: true`; homePicker lives there at tabIds[0].
   *   - non-main panes are never empty (removed on close/move/merge).
   *   - when the tree collapses to a single main pane, dock is normalized back
   *     to null and primary restores the main pane's tabs.
   */
  dock: UieDockNode | null
}

export interface UieStack {
  stackId: UieStackId
  /** Ordered card instanceIds. */
  tabIds: string[]
  activeTabId: string | null
}

// ---------------------------------------------------------------------------
// Dock layout — a recursive binary split tree of panes within one column.
//
// Each pane behaves like a mini stack (browser tabs); splits divide the column
// horizontally (`row` = left/right panes) or vertically (`column` = top/bottom).
// The tree is binary: a split has exactly two children. Repeated splitting
// yields arbitrary row/column mixed layouts, all confined to the column.
// ---------------------------------------------------------------------------

export interface UieDockPane {
  kind: 'pane'
  paneId: string
  /** Exactly one pane is the main pane: hosts homePicker + the collapse button. */
  main: boolean
  /** Ordered card instanceIds (like a UieStack). */
  tabIds: string[]
  activeTabId: string | null
}

export interface UieDockSplit {
  kind: 'split'
  splitId: string
  /** `row` = children laid out left/right; `column` = top/bottom. */
  dir: 'row' | 'column'
  /** 0..1 — share of the first child (a) along the split axis. */
  ratio: number
  a: UieDockNode
  b: UieDockNode
}

export type UieDockNode = UieDockPane | UieDockSplit

export interface PersistedCardInstance {
  /** instanceId — globally unique across the app. */
  id: string
  /** Registry key (e.g. 'git', 'chat', 'browser'). */
  descriptorId: string
  title: string
  /** Grid position & dimensions in grid units. */
  x?: number
  y?: number
  w?: number
  h?: number
  minW?: number
  minH?: number
  static?: boolean
  isDraggable?: boolean
  isResizable?: boolean
  /** Serializable card state (preview URL, sessionId, etc.). */
  state?: Record<string, unknown>
}

/** Card descriptor — the component is NOT inlined into the snapshot. */
export interface UieCardDescriptor {
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
export interface UieGeometry {
  columns: Record<UieSlotId, ColumnGeometry>
  splits: Record<string, SplitGeometry>
  /** Flattened dock panes/dividers per column (column-relative coordinates). */
  docks: Record<UieSlotId, UieDockGeometry>
  /** Visual-only degradations — never written back to the layout. */
  degraded: {
    collapsedRight: boolean
    collapsedLeft: boolean
    /** True when the right feature column floats over the center (window-stacking). */
    overlayRight: boolean
    degradedSplits: string[]
  }
}

export interface ColumnGeometry {
  slotId: UieSlotId
  x: number
  y: number
  width: number
  height: number
  /** Actual width used (collapsed width if collapsed). */
  effectiveWidth: number
  /** Top inset carried over from the column (rendering anchor). */
  topInset: number
  /** True when this column floats over the adjacent center column (window-stacking). */
  overlay?: boolean
}

export interface SplitGeometry {
  slotId: UieSlotId
  dividerY: number
  upper: StackGeometry
  lower: StackGeometry
}

export interface UieDockGeometry {
  slotId: UieSlotId
  /** Pane rects in column-relative coordinates, in rendering order. */
  panes: UieDockPaneGeometry[]
  /** Split divider hit-areas (4px), in column-relative coordinates. */
  dividers: UieDockDividerGeometry[]
  /** paneIds visually degraded out (too small); never written back. */
  degradedPanes: string[]
  /** True when ≥2 panes fit — enables the "restore single panel" action. */
  collapsible: boolean
}

export interface UieDockPaneGeometry {
  paneId: string
  /** Column-relative x/y/width/height. */
  x: number
  y: number
  width: number
  height: number
  /** True when this pane is visually degraded (not rendered). */
  degraded: boolean
}

export interface UieDockDividerGeometry {
  splitId: string
  dir: 'row' | 'column'
  /** Column-relative hit-area rect (4px thick, expanded for the pill). */
  x: number
  y: number
  width: number
  height: number
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
export interface UieContainerSize {
  width: number
  height: number
}

export const COLLAPSED_COLUMN_WIDTH = 44
export const DEFAULT_GAP = 8
/** Window-edge inset for float pages (home/settings). App pages use 0. */
export const EDGE_INSET = 8
export const UNDO_LIMIT = 50

// --- Dock (multi-pane split) constants ---
/** Feature-column width ceiling while a dock is active. */
export const MAX_DOCK_COLUMN_WIDTH = 1040
/** Minimum pane width/height before a pane is visually degraded. */
export const MIN_DOCK_PANE_WIDTH = 200
export const MIN_DOCK_PANE_HEIGHT = 120
/** Split-divider thickness (px). */
export const DOCK_DIVIDER = 4
/** Fraction of a pane's width/height treated as a split edge (drop target). */
export const DOCK_DROP_EDGE = 0.2
/** Default ratio for a newly created split. */
export const DEFAULT_DOCK_RATIO = 0.5
