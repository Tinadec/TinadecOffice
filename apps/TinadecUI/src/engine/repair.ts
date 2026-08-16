import type {
  PersistedCardInstance,
  UieColumn,
  UieDockNode,
  UieDockPane,
  UieLayoutSnapshot,
  UiePageId,
  UieSlotId,
  UieStack,
  UieStackId,
} from './types'
import { COLLAPSED_COLUMN_WIDTH, DEFAULT_DOCK_RATIO } from './types'
import type { CardRegistry } from './registry'
import { buildPreset, type PresetContext } from './presets'
import { createEmptySnapshot, collectDockPanes } from './reducer'

// ---------------------------------------------------------------------------
// Layout repair.
//
// repairLayout(raw, ctx) validates/normalizes a parsed snapshot: unknown cards,
// duplicate singletons, illegal sizes, missing columns. On unrecoverable
// problems it falls back to the built-in preset — never a blank window.
// ---------------------------------------------------------------------------

export interface RepairContext {
  registry: CardRegistry
  preset: PresetContext
}

const VALID_SLOTS: readonly UieSlotId[] = ['left', 'center', 'right']
const MIN_WIDTH = 160
const MAX_WIDTH = 2000
const MAX_CARDS = 500

function clampWidth(w: unknown, fallback: number): number {
  const n = typeof w === 'number' && Number.isFinite(w) ? Math.round(w) : fallback
  return Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, n))
}

/** Float pages keep an 8px window-edge gap; app pages stay flush. */
function defaultEdgeInset(pageId: UiePageId): number {
  return pageId === 'home' || pageId === 'settings' || pageId === 'market' ? 8 : 0
}

function isValidPageId(p: unknown): p is UiePageId {
  return p === 'home' || p === 'settings' || p === 'market' || p === 'code' || p === 'debug'
}

function repairStack(
  stack: unknown,
  stackId: UieStackId,
  cards: Record<string, PersistedCardInstance>,
  registry: CardRegistry,
): UieStack | null {
  if (!stack || typeof stack !== 'object') return null
  const s = stack as Partial<UieStack>
  const tabIds = Array.isArray(s.tabIds) ? s.tabIds : []
  // Keep only known descriptor ids; drop unknowns.
  const valid = tabIds.filter(
    (id): id is string => typeof id === 'string' && !!cards[id] && registry.has(cards[id].descriptorId),
  )
  // De-duplicate instance ids.
  const seen = new Set<string>()
  const unique = valid.filter((id) => (seen.has(id) ? false : (seen.add(id), true)))
  const active = typeof s.activeTabId === 'string' && unique.includes(s.activeTabId) ? s.activeTabId : unique[0] ?? null
  return { stackId, tabIds: unique, activeTabId: active }
}

// ---------------------------------------------------------------------------
// Dock repair — validate/normalize a column's dock split tree.
// ---------------------------------------------------------------------------

let dockIdCounter = 0
function nextRepairId(kind: 'pane' | 'split'): string {
  return `dock-${kind}-${++dockIdCounter}`
}

function repairDockPane(
  raw: unknown,
  cards: Record<string, PersistedCardInstance>,
  registry: CardRegistry,
): UieDockPane | null {
  if (!raw || typeof raw !== 'object') return null
  const r = raw as Record<string, unknown>
  const tabIds = Array.isArray(r.tabIds) ? r.tabIds : []
  const valid = tabIds.filter(
    (id): id is string => typeof id === 'string' && !!cards[id] && registry.has(cards[id].descriptorId),
  )
  const seen = new Set<string>()
  const unique = valid.filter((id) => (seen.has(id) ? false : (seen.add(id), true)))
  const active =
    typeof r.activeTabId === 'string' && unique.includes(r.activeTabId)
      ? r.activeTabId
      : unique[0] ?? null
  return {
    kind: 'pane',
    paneId: typeof r.paneId === 'string' && r.paneId ? r.paneId : nextRepairId('pane'),
    main: r.main === true,
    tabIds: unique,
    activeTabId: active,
  }
}

function repairDockNode(
  raw: unknown,
  cards: Record<string, PersistedCardInstance>,
  registry: CardRegistry,
): UieDockNode | null {
  if (!raw || typeof raw !== 'object') return null
  const r = raw as Record<string, unknown>
  if (r.kind === 'pane') return repairDockPane(r, cards, registry)
  if (r.kind === 'split') {
    const a = repairDockNode(r.a, cards, registry)
    const b = repairDockNode(r.b, cards, registry)
    if (!a || !b) return null
    const dir = r.dir === 'row' || r.dir === 'column' ? (r.dir as 'row' | 'column') : 'column'
    const ratio = typeof r.ratio === 'number' && Number.isFinite(r.ratio)
      ? Math.max(0.1, Math.min(0.9, r.ratio))
      : DEFAULT_DOCK_RATIO
    return {
      kind: 'split',
      splitId: typeof r.splitId === 'string' && r.splitId ? r.splitId : nextRepairId('split'),
      dir,
      ratio,
      a,
      b,
    }
  }
  return null
}

/**
 * Repair a column's dock tree: valid structure, exactly one main pane
 * (homePicker is forced there at tabIds[0]), known/deduped tab references.
 * Returns null when the raw dock is unusable (caller falls back to stacks).
 */
export function repairDock(
  raw: unknown,
  cards: Record<string, PersistedCardInstance>,
  registry: CardRegistry,
): UieDockNode | null {
  const root = repairDockNode(raw, cards, registry)
  if (!root) return null

  const panes = collectDockPanes(root)
  const mains = panes.filter((p) => p.main)
  const homePickerId = Object.values(cards).find((c) => c.descriptorId === 'homePicker')?.id
  let main = mains[0]
  if (mains.length !== 1) {
    // No or multiple mains: promote the pane hosting homePicker (else the first).
    main = panes.find((p) => homePickerId !== undefined && p.tabIds.includes(homePickerId)) ?? panes[0]
    for (const p of panes) p.main = p === main
  }
  // Force homePicker to the front of the main pane.
  if (homePickerId !== undefined) {
    const hpIndex = main.tabIds.indexOf(homePickerId)
    if (hpIndex > 0) {
      main.tabIds.splice(hpIndex, 1)
      main.tabIds.unshift(homePickerId)
    }
  }
  return root
}

function repairColumn(
  col: unknown,
  slotId: UieSlotId,
  cards: Record<string, PersistedCardInstance>,
  registry: CardRegistry,
): UieColumn {
  const c = col && typeof col === 'object' ? (col as Partial<UieColumn>) : {}
  const primary = repairStack(c.primary, 'primary', cards, registry) ?? { stackId: 'primary' as const, tabIds: [], activeTabId: null }
  const secondaryRaw = c.secondary
  const secondary = secondaryRaw ? repairStack(secondaryRaw, 'secondary', cards, registry) : null
  const splitRatio = secondary && typeof c.splitRatio === 'number' ? Math.max(0.1, Math.min(0.9, c.splitRatio)) : null
  const dock = repairDock(c.dock, cards, registry)
  return {
    slotId,
    width: clampWidth(c.width, slotId === 'left' ? 260 : slotId === 'right' ? 420 : 600),
    collapsed: c.collapsed === true,
    surfaceMode: c.surfaceMode === 'immersive' ? 'immersive' : c.surfaceMode === 'app' ? 'app' : 'float',
    topInset: typeof c.topInset === 'number' ? c.topInset : 8,
    // Dock and stacks are mutually exclusive: when a dock survives repair, the
    // stacks are emptied (the main pane already holds every referenced card).
    primary: dock ? { stackId: 'primary' as const, tabIds: [], activeTabId: null } : primary,
    secondary: dock ? null : secondary,
    splitRatio: dock ? null : splitRatio,
    dock,
  }
}

export function repairLayout(
  raw: unknown,
  ctx: RepairContext,
): UieLayoutSnapshot {
  const { registry, preset } = ctx

  // Non-object or wrong version → built-in preset.
  if (!raw || typeof raw !== 'object') return buildPreset('home', preset)
  const r = raw as Partial<UieLayoutSnapshot>
  if (r.version !== 1) return buildPreset(isValidPageId(r.pageId) ? r.pageId : 'home', preset)

  const pageId: UiePageId = isValidPageId(r.pageId) ? r.pageId : 'home'

  // Repair cards first.
  const rawCards = r.cards && typeof r.cards === 'object' ? r.cards : {}
  const cards: Record<string, PersistedCardInstance> = {}
  let count = 0
  for (const [id, inst] of Object.entries(rawCards)) {
    if (count >= MAX_CARDS) break
    if (!inst || typeof inst !== 'object') continue
    const i = inst as Partial<PersistedCardInstance>
    if (typeof i.descriptorId !== 'string' || !registry.has(i.descriptorId)) continue
    const descriptor = registry.get(i.descriptorId)
    // Drop duplicate singletons — keep the first.
    if (descriptor?.singleton) {
      const existing = Object.values(cards).find((c) => c.descriptorId === i.descriptorId)
      if (existing) continue
    }
    cards[id] = {
      id,
      descriptorId: i.descriptorId,
      title: typeof i.title === 'string' && i.title ? i.title : descriptor?.defaultTitle ?? i.descriptorId,
      ...(typeof i.x === 'number' && Number.isFinite(i.x) ? { x: Math.max(0, Math.round(i.x)) } : {}),
      ...(typeof i.y === 'number' && Number.isFinite(i.y) ? { y: Math.max(0, Math.round(i.y)) } : {}),
      ...(typeof i.w === 'number' && Number.isFinite(i.w) ? { w: Math.max(1, Math.round(i.w)) } : {}),
      ...(typeof i.h === 'number' && Number.isFinite(i.h) ? { h: Math.max(1, Math.round(i.h)) } : {}),
      ...(typeof i.minW === 'number' && Number.isFinite(i.minW) ? { minW: Math.max(1, Math.round(i.minW)) } : {}),
      ...(typeof i.minH === 'number' && Number.isFinite(i.minH) ? { minH: Math.max(1, Math.round(i.minH)) } : {}),
      ...(typeof i.static === 'boolean' ? { static: i.static } : {}),
      ...(typeof i.isDraggable === 'boolean' ? { isDraggable: i.isDraggable } : {}),
      ...(typeof i.isResizable === 'boolean' ? { isResizable: i.isResizable } : {}),
      ...(i.state && typeof i.state === 'object' ? { state: i.state as Record<string, unknown> } : {}),
    }
    count++
  }

  // Repair columns.
  const rawCols = r.columns && typeof r.columns === 'object' ? r.columns as Record<string, unknown> : {}
  const columns = {} as Record<UieSlotId, UieColumn>
  for (const slotId of VALID_SLOTS) {
    columns[slotId] = repairColumn(rawCols[slotId], slotId, cards, registry)
  }

  // Home's chat column is inherently immersive: migrate any persisted layout
  // (which may predate the immersive surface mode) so the conversation zone
  // stays transparent even when the saved snapshot still says 'float'. The chat
  // card's descriptor is the source of truth, so whichever column hosts it
  // becomes immersive (normally center).
  if (pageId === 'home') {
    for (const slotId of VALID_SLOTS) {
      const col = columns[slotId]
      const hostsChat = [...(col.primary?.tabIds ?? []), ...(col.secondary?.tabIds ?? [])].some(
        (id) => cards[id]?.descriptorId === 'chat',
      )
      if (hostsChat) col.surfaceMode = 'immersive'
    }
  }

  // Determine column order.
  let columnOrder: UieSlotId[] = VALID_SLOTS.slice()
  if (Array.isArray(r.columnOrder)) {
    const present = r.columnOrder.filter((s): s is UieSlotId => VALID_SLOTS.includes(s))
    if (present.length === VALID_SLOTS.length) columnOrder = present
  }

  // Ensure each column stack hosts at most one instance of a singleton.
  // (already handled by card dedup above)

  // Orphan guard: every known card must be referenced by a stack or dock pane.
  // In a valid layout closing a card deletes it, so a card that survives in
  // `cards` while being referenced nowhere is a signature of a corrupted
  // snapshot. Recover by rebuilding the page's built-in preset — the window is
  // never blank/empty, mirroring the module's "never a blank window" rule.
  const referenced = new Set<string>()
  for (const slotId of VALID_SLOTS) {
    const col = columns[slotId]
    for (const stack of [col.primary, col.secondary]) {
      if (!stack) continue
      for (const id of stack.tabIds) referenced.add(id)
    }
    if (col.dock) {
      for (const pane of collectDockPanes(col.dock)) {
        for (const id of pane.tabIds) referenced.add(id)
      }
    }
  }
  const orphaned = Object.values(cards).some((c) => !referenced.has(c.id))
  if (orphaned) return buildPreset(pageId, preset)

  const gap = typeof r.gap === 'number' && r.gap >= 0 && r.gap <= 32 ? r.gap : 8
  const edgeInset =
    typeof r.edgeInset === 'number' && r.edgeInset >= 0 && r.edgeInset <= 32
      ? Math.round(r.edgeInset)
      : defaultEdgeInset(pageId)
  const focused =
    typeof r.focusedCardId === 'string' && cards[r.focusedCardId] ? r.focusedCardId : null

  const revision = typeof r.revision === 'number' && Number.isFinite(r.revision) ? r.revision : 1

  return {
    version: 1,
    revision,
    pageId,
    columnOrder,
    columns,
    cards,
    focusedCardId: focused,
    gap,
    edgeInset,
  }
}
