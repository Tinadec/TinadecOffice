import type {
  ResolvedWorkbenchColumn,
  ResolvedWorkbenchLayout,
  ResolvedWorkbenchStack,
  WorkbenchCardDescriptor,
  WorkbenchLayoutSnapshot,
  WorkbenchSlotId,
  WorkbenchStack,
} from './types'

const CANVAS_INSET = 8
const COLUMN_GAP = 8
const STACK_GAP = 8
const COLLAPSED_WIDTH = 44
const DEFAULT_MIN_WIDTH = 180
const DEFAULT_MIN_HEIGHT = 180

function collapsedWidth(snapshot: WorkbenchLayoutSnapshot, slotId: WorkbenchSlotId): number {
  const column = snapshot.columns[slotId]
  // Settings reserves an empty right slot so its content card can span the
  // remaining work area without leaving a visible rail.
  if (column.width <= 0 && column.primary.cardIds.length === 0 && !column.secondary) return 0
  return COLLAPSED_WIDTH
}

function activeMinimumWidth(
  snapshot: WorkbenchLayoutSnapshot,
  stack: WorkbenchStack | null,
  descriptors?: ReadonlyMap<string, WorkbenchCardDescriptor>,
): number {
  const card = stack?.activeCardId ? snapshot.cards[stack.activeCardId] : undefined
  return card ? descriptors?.get(card.type)?.minWidth ?? DEFAULT_MIN_WIDTH : DEFAULT_MIN_WIDTH
}

function activeMinimumHeight(
  snapshot: WorkbenchLayoutSnapshot,
  stack: WorkbenchStack | null,
  descriptors?: ReadonlyMap<string, WorkbenchCardDescriptor>,
): number {
  const card = stack?.activeCardId ? snapshot.cards[stack.activeCardId] : undefined
  return card ? descriptors?.get(card.type)?.minHeight ?? DEFAULT_MIN_HEIGHT : DEFAULT_MIN_HEIGHT
}

function makeStack(
  slotId: WorkbenchSlotId,
  stackId: 'primary' | 'secondary',
  stack: WorkbenchStack,
  x: number,
  y: number,
  width: number,
  height: number,
): ResolvedWorkbenchStack {
  return {
    slotId,
    stackId,
    rect: { x, y, width: Math.max(0, width), height: Math.max(0, height) },
    cardIds: [...stack.cardIds],
    activeCardId: stack.activeCardId,
  }
}

export function resolveWorkbenchLayout(
  snapshot: WorkbenchLayoutSnapshot,
  viewport: Readonly<{ width: number; height: number }>,
  descriptors?: ReadonlyMap<string, WorkbenchCardDescriptor>,
): ResolvedWorkbenchLayout {
  const width = Math.max(0, viewport.width)
  const height = Math.max(0, viewport.height)
  const collapsed = new Set<WorkbenchSlotId>(
    snapshot.columnOrder.filter((slotId) => snapshot.columns[slotId].collapsed),
  )
  const contributingColumnCount = snapshot.columnOrder.filter(
    (slotId) => !collapsed.has(slotId) || collapsedWidth(snapshot, slotId) > 0,
  ).length
  const availableWidth = Math.max(
    0,
    width - CANVAS_INSET * 2 - COLUMN_GAP * Math.max(0, contributingColumnCount - 1),
  )
  const availableHeight = Math.max(0, height - CANVAS_INSET * 2)
  const autoCollapsed = new Set<WorkbenchSlotId>()

  const requiredWidth = () => snapshot.columnOrder.reduce((sum, slotId) => {
    if (collapsed.has(slotId)) return sum + collapsedWidth(snapshot, slotId)
    const column = snapshot.columns[slotId]
    const minimum = Math.max(
      activeMinimumWidth(snapshot, column.primary, descriptors),
      activeMinimumWidth(snapshot, column.secondary, descriptors),
    )
    return sum + (slotId === 'center' ? minimum : Math.max(column.width, minimum))
  }, 0)

  for (const candidate of ['right', 'left'] as const) {
    if (requiredWidth() <= availableWidth || collapsed.has(candidate)) continue
    collapsed.add(candidate)
    autoCollapsed.add(candidate)
  }

  const fixedSlots = snapshot.columnOrder.filter((slotId) => slotId !== 'center')
  const fixedWidths = new Map<WorkbenchSlotId, number>()
  for (const slotId of fixedSlots) {
    const column = snapshot.columns[slotId]
    const minimum = activeMinimumWidth(snapshot, column.primary, descriptors)
    fixedWidths.set(slotId, collapsed.has(slotId) ? collapsedWidth(snapshot, slotId) : Math.max(minimum, column.width))
  }
  const centerWidth = collapsed.has('center')
    ? collapsedWidth(snapshot, 'center')
    : Math.max(
        activeMinimumWidth(snapshot, snapshot.columns.center.primary, descriptors),
        availableWidth - [...fixedWidths.values()].reduce((sum, value) => sum + value, 0),
      )

  const naturalTotal = centerWidth + [...fixedWidths.values()].reduce((sum, value) => sum + value, 0)
  const scale = naturalTotal > availableWidth && naturalTotal > 0 ? availableWidth / naturalTotal : 1
  const resolvedWidths = new Map<WorkbenchSlotId, number>([['center', centerWidth * scale]])
  for (const [slotId, requested] of fixedWidths) resolvedWidths.set(slotId, requested * scale)

  const columns: ResolvedWorkbenchColumn[] = []
  const visibleSlots = snapshot.columnOrder.filter((slotId) => (resolvedWidths.get(slotId) ?? 0) > 0)
  let x = CANVAS_INSET
  for (const slotId of snapshot.columnOrder) {
    const column = snapshot.columns[slotId]
    const columnWidth = resolvedWidths.get(slotId) ?? 0
    const isCollapsed = collapsed.has(slotId)
    const primaryMinHeight = activeMinimumHeight(snapshot, column.primary, descriptors)
    const secondaryMinHeight = activeMinimumHeight(snapshot, column.secondary, descriptors)
    const mergedSecondary = Boolean(
      column.secondary
      && !isCollapsed
      && availableHeight < primaryMinHeight + secondaryMinHeight + STACK_GAP,
    )
    const stacks: ResolvedWorkbenchStack[] = []

    if (!isCollapsed) {
      if (column.secondary && !mergedSecondary) {
        const primaryHeight = Math.max(
          primaryMinHeight,
          Math.min(availableHeight - secondaryMinHeight - STACK_GAP, availableHeight * column.splitRatio),
        )
        stacks.push(makeStack(slotId, 'primary', column.primary, x, CANVAS_INSET, columnWidth, primaryHeight))
        stacks.push(makeStack(
          slotId,
          'secondary',
          column.secondary,
          x,
          CANVAS_INSET + primaryHeight + STACK_GAP,
          columnWidth,
          availableHeight - primaryHeight - STACK_GAP,
        ))
      } else {
        const primary = mergedSecondary && column.secondary
          ? {
              cardIds: [...column.primary.cardIds, ...column.secondary.cardIds],
              activeCardId: snapshot.focusedCardId && [
                ...column.primary.cardIds,
                ...column.secondary.cardIds,
              ].includes(snapshot.focusedCardId)
                ? snapshot.focusedCardId
                : column.primary.activeCardId ?? column.secondary.activeCardId,
            }
          : column.primary
        stacks.push(makeStack(slotId, 'primary', primary, x, CANVAS_INSET, columnWidth, availableHeight))
      }
    }

    columns.push({
      slotId,
      rect: { x, y: CANVAS_INSET, width: columnWidth, height: availableHeight },
      collapsed: isCollapsed,
      autoCollapsed: autoCollapsed.has(slotId),
      mergedSecondary,
      stacks,
    })
    if (columnWidth > 0) {
      x += columnWidth
      if (visibleSlots.indexOf(slotId) < visibleSlots.length - 1) x += COLUMN_GAP
    }
  }

  return { width, height, columns }
}
