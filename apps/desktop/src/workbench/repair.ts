import { createWorkbenchPreset, DEFAULT_SPLIT_RATIO } from './presets'
import {
  WORKBENCH_PAGE_IDS,
  WORKBENCH_SLOT_IDS,
  type PersistedCardInstance,
  type WorkbenchCardDescriptor,
  type WorkbenchLayoutSnapshot,
  type WorkbenchPageId,
  type WorkbenchSlotId,
  type WorkbenchStack,
} from './types'

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function finiteNumber(value: unknown, fallback: number, minimum: number, maximum: number): number {
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.max(minimum, Math.min(maximum, value))
    : fallback
}

function repairStack(value: unknown, knownCards: ReadonlySet<string>, seen: Set<string>): WorkbenchStack {
  const candidate = isRecord(value) ? value : {}
  const cardIds = Array.isArray(candidate.cardIds)
    ? candidate.cardIds.filter((id): id is string => {
        if (typeof id !== 'string' || !knownCards.has(id) || seen.has(id)) return false
        seen.add(id)
        return true
      })
    : []
  const requestedActive = typeof candidate.activeCardId === 'string' ? candidate.activeCardId : null
  return {
    cardIds,
    activeCardId: requestedActive && cardIds.includes(requestedActive) ? requestedActive : cardIds[0] ?? null,
  }
}

function repairCards(
  value: unknown,
  descriptors?: ReadonlyMap<string, WorkbenchCardDescriptor>,
): Record<string, PersistedCardInstance> {
  if (!isRecord(value)) return {}
  const cards: Record<string, PersistedCardInstance> = {}
  const singletonTypes = new Set<string>()

  for (const [id, raw] of Object.entries(value)) {
    if (!id || !isRecord(raw) || typeof raw.type !== 'string') continue
    const descriptor = descriptors?.get(raw.type)
    if (descriptors && !descriptor) continue
    if (descriptor?.singleton && singletonTypes.has(raw.type)) continue
    if (descriptor?.singleton) singletonTypes.add(raw.type)
    cards[id] = {
      id,
      type: raw.type,
      state: isRecord(raw.state) ? structuredClone(raw.state) : {},
    }
  }
  return cards
}

function validPageId(value: unknown): value is WorkbenchPageId {
  return typeof value === 'string' && (WORKBENCH_PAGE_IDS as readonly string[]).includes(value)
}

export function repairWorkbenchLayout(
  value: unknown,
  pageId: WorkbenchPageId,
  descriptors?: ReadonlyMap<string, WorkbenchCardDescriptor>,
): WorkbenchLayoutSnapshot {
  if (!isRecord(value) || value.version !== 1 || !validPageId(value.pageId) || value.pageId !== pageId) {
    return createWorkbenchPreset(pageId)
  }

  const fallback = createWorkbenchPreset(pageId)
  if (pageId === 'settings') {
    const rawColumns = isRecord(value.columns) ? value.columns : {}
    const left = isRecord(rawColumns.left) ? rawColumns.left : {}
    fallback.columns.left.width = finiteNumber(left.width, fallback.columns.left.width, 200, 480)
    fallback.revision = finiteNumber(value.revision, 0, 0, Number.MAX_SAFE_INTEGER)
    fallback.focusedCardId = value.focusedCardId === 'settings:navigation'
      ? 'settings:navigation'
      : 'settings:content'
    return fallback
  }
  const cards = repairCards(value.cards, descriptors)
  if (Object.keys(cards).length === 0) return fallback

  const requestedOrder = Array.isArray(value.columnOrder) ? value.columnOrder : []
  const columnOrder = requestedOrder.filter(
    (slotId, index): slotId is WorkbenchSlotId =>
      typeof slotId === 'string'
      && (WORKBENCH_SLOT_IDS as readonly string[]).includes(slotId)
      && requestedOrder.indexOf(slotId) === index,
  )
  for (const slotId of WORKBENCH_SLOT_IDS) {
    if (!columnOrder.includes(slotId)) columnOrder.push(slotId)
  }

  const seen = new Set<string>()
  const rawColumns = isRecord(value.columns) ? value.columns : {}
  const columns = structuredClone(fallback.columns)
  for (const slotId of WORKBENCH_SLOT_IDS) {
    const rawColumn = isRecord(rawColumns[slotId]) ? rawColumns[slotId] : {}
    columns[slotId] = {
      width: finiteNumber(rawColumn.width, fallback.columns[slotId].width, 160, 1200),
      collapsed: rawColumn.collapsed === true,
      primary: repairStack(rawColumn.primary, new Set(Object.keys(cards)), seen),
      secondary: rawColumn.secondary
        ? repairStack(rawColumn.secondary, new Set(Object.keys(cards)), seen)
        : null,
      splitRatio: finiteNumber(rawColumn.splitRatio, DEFAULT_SPLIT_RATIO, 0.2, 0.8),
    }
    if (columns[slotId].secondary?.cardIds.length === 0) columns[slotId].secondary = null
  }

  for (const cardId of Object.keys(cards)) {
    if (!seen.has(cardId)) columns.center.primary.cardIds.push(cardId)
  }
  columns.center.primary.activeCardId ??= columns.center.primary.cardIds[0] ?? null

  const focusedCardId = typeof value.focusedCardId === 'string' && cards[value.focusedCardId]
    ? value.focusedCardId
    : columns.center.primary.activeCardId

  return {
    version: 1,
    revision: finiteNumber(value.revision, 0, 0, Number.MAX_SAFE_INTEGER),
    pageId,
    columnOrder,
    columns,
    cards,
    focusedCardId,
  }
}
