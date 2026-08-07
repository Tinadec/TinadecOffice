import type {
  PersistedCardInstance,
  WorkbenchColumn,
  WorkbenchLayoutSnapshot,
  WorkbenchPageId,
  WorkbenchSlotId,
  WorkbenchStack,
  WorkbenchStackId,
} from './types'
import { COLLAPSED_COLUMN_WIDTH } from './types'
import type { CardRegistry } from './registry'
import { buildPreset, type PresetContext } from './presets'
import { createEmptySnapshot } from './reducer'

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

const VALID_SLOTS: readonly WorkbenchSlotId[] = ['left', 'center', 'right']
const MIN_WIDTH = 160
const MAX_WIDTH = 2000
const MAX_CARDS = 500

function clampWidth(w: unknown, fallback: number): number {
  const n = typeof w === 'number' && Number.isFinite(w) ? Math.round(w) : fallback
  return Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, n))
}

/** Float pages keep an 8px window-edge gap; app pages stay flush. */
function defaultEdgeInset(pageId: WorkbenchPageId): number {
  return pageId === 'home' || pageId === 'settings' ? 8 : 0
}

function isValidPageId(p: unknown): p is WorkbenchPageId {
  return p === 'home' || p === 'settings' || p === 'market' || p === 'code' || p === 'debug'
}

function repairStack(
  stack: unknown,
  stackId: WorkbenchStackId,
  cards: Record<string, PersistedCardInstance>,
  registry: CardRegistry,
): WorkbenchStack | null {
  if (!stack || typeof stack !== 'object') return null
  const s = stack as Partial<WorkbenchStack>
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

function repairColumn(
  col: unknown,
  slotId: WorkbenchSlotId,
  cards: Record<string, PersistedCardInstance>,
  registry: CardRegistry,
): WorkbenchColumn {
  const c = col && typeof col === 'object' ? (col as Partial<WorkbenchColumn>) : {}
  const primary = repairStack(c.primary, 'primary', cards, registry) ?? { stackId: 'primary' as const, tabIds: [], activeTabId: null }
  const secondaryRaw = c.secondary
  const secondary = secondaryRaw ? repairStack(secondaryRaw, 'secondary', cards, registry) : null
  const splitRatio = secondary && typeof c.splitRatio === 'number' ? Math.max(0.1, Math.min(0.9, c.splitRatio)) : null
  return {
    slotId,
    width: clampWidth(c.width, slotId === 'left' ? 260 : slotId === 'right' ? 420 : 600),
    collapsed: c.collapsed === true,
    surfaceMode: c.surfaceMode === 'app' ? 'app' : 'float',
    topInset: typeof c.topInset === 'number' ? c.topInset : 8,
    primary,
    secondary,
    splitRatio,
  }
}

export function repairLayout(
  raw: unknown,
  ctx: RepairContext,
): WorkbenchLayoutSnapshot {
  const { registry, preset } = ctx

  // Non-object or wrong version → built-in preset.
  if (!raw || typeof raw !== 'object') return buildPreset('home', preset)
  const r = raw as Partial<WorkbenchLayoutSnapshot>
  if (r.version !== 1) return buildPreset(isValidPageId(r.pageId) ? r.pageId : 'home', preset)

  const pageId: WorkbenchPageId = isValidPageId(r.pageId) ? r.pageId : 'home'

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
      ...(i.state && typeof i.state === 'object' ? { state: i.state as Record<string, unknown> } : {}),
    }
    count++
  }

  // Repair columns.
  const rawCols = r.columns && typeof r.columns === 'object' ? r.columns as Record<string, unknown> : {}
  const columns = {} as Record<WorkbenchSlotId, WorkbenchColumn>
  for (const slotId of VALID_SLOTS) {
    columns[slotId] = repairColumn(rawCols[slotId], slotId, cards, registry)
  }

  // Determine column order.
  let columnOrder: WorkbenchSlotId[] = VALID_SLOTS.slice()
  if (Array.isArray(r.columnOrder)) {
    const present = r.columnOrder.filter((s): s is WorkbenchSlotId => VALID_SLOTS.includes(s))
    if (present.length === VALID_SLOTS.length) columnOrder = present
  }

  // Ensure each column stack hosts at most one instance of a singleton.
  // (already handled by card dedup above)

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
