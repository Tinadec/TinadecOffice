import { DEFAULT_SPLIT_RATIO, isSettingsPreset } from './presets'
import type {
  PersistedCardInstance,
  WorkbenchCardInstanceId,
  WorkbenchColumn,
  WorkbenchCommandEnvelope,
  WorkbenchLayoutCommand,
  WorkbenchLayoutSnapshot,
  WorkbenchReducerOptions,
  WorkbenchReducerResult,
  WorkbenchSlotId,
  WorkbenchStack,
  WorkbenchStackId,
} from './types'

const MIN_COLUMN_WIDTH = 160
const MAX_COLUMN_WIDTH = 1200

function cloneSnapshot(snapshot: WorkbenchLayoutSnapshot): WorkbenchLayoutSnapshot {
  return structuredClone(snapshot)
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, value))
}

function stackFor(column: WorkbenchColumn, stackId: WorkbenchStackId): WorkbenchStack | null {
  return stackId === 'primary' ? column.primary : column.secondary
}

function ensureStack(column: WorkbenchColumn, stackId: WorkbenchStackId): WorkbenchStack {
  if (stackId === 'primary') return column.primary
  column.secondary ??= { cardIds: [], activeCardId: null }
  return column.secondary
}

function findCardPlacement(snapshot: WorkbenchLayoutSnapshot, cardId: WorkbenchCardInstanceId) {
  for (const slotId of snapshot.columnOrder) {
    const column = snapshot.columns[slotId]
    for (const stackId of ['primary', 'secondary'] as const) {
      const stack = stackFor(column, stackId)
      const index = stack?.cardIds.indexOf(cardId) ?? -1
      if (stack && index >= 0) return { slotId, stackId, stack, index }
    }
  }
  return null
}

function selectAfterRemoval(stack: WorkbenchStack, removedIndex: number): void {
  if (stack.activeCardId && stack.cardIds.includes(stack.activeCardId)) return
  stack.activeCardId = stack.cardIds[removedIndex - 1] ?? stack.cardIds[removedIndex] ?? null
}

function removeCardFromPlacement(snapshot: WorkbenchLayoutSnapshot, cardId: string): void {
  const placement = findCardPlacement(snapshot, cardId)
  if (!placement) return
  placement.stack.cardIds.splice(placement.index, 1)
  selectAfterRemoval(placement.stack, placement.index)
  const column = snapshot.columns[placement.slotId]
  if (placement.stackId === 'secondary' && column.secondary?.cardIds.length === 0) {
    column.secondary = null
    column.splitRatio = DEFAULT_SPLIT_RATIO
  }
}

function insertCard(stack: WorkbenchStack, cardId: string, index?: number): void {
  const insertionIndex = index === undefined
    ? stack.cardIds.length
    : clamp(Math.trunc(index), 0, stack.cardIds.length)
  stack.cardIds.splice(insertionIndex, 0, cardId)
  stack.activeCardId = cardId
}

function descriptorAllows(
  snapshot: WorkbenchLayoutSnapshot,
  cardId: string,
  operation: 'move' | 'close',
  options: WorkbenchReducerOptions,
): boolean {
  const instance = snapshot.cards[cardId]
  const descriptor = instance ? options.descriptors?.get(instance.type) : undefined
  if (!descriptor) return true
  return operation === 'move' ? descriptor.movable : descriptor.closable
}

function openCard(
  snapshot: WorkbenchLayoutSnapshot,
  card: PersistedCardInstance,
  slotId: WorkbenchSlotId,
  stackId: WorkbenchStackId,
  index: number | undefined,
  options: WorkbenchReducerOptions,
): string | null {
  if (!card.id || !card.type || snapshot.cards[card.id]) return 'Card id is invalid or already open.'
  const descriptor = options.descriptors?.get(card.type)
  if (descriptor?.singleton && Object.values(snapshot.cards).some((item) => item.type === card.type)) {
    return 'Singleton card is already open.'
  }
  snapshot.cards[card.id] = structuredClone(card)
  insertCard(ensureStack(snapshot.columns[slotId], stackId), card.id, index)
  snapshot.focusedCardId = card.id
  return null
}

function applyCommand(
  snapshot: WorkbenchLayoutSnapshot,
  command: WorkbenchLayoutCommand,
  options: WorkbenchReducerOptions,
): string | null {
  switch (command.kind) {
    case 'openCard':
      return openCard(snapshot, command.card, command.slotId, command.stackId, command.index, options)

    case 'closeCard': {
      if (!snapshot.cards[command.cardId]) return 'Card is not open.'
      if (!descriptorAllows(snapshot, command.cardId, 'close', options)) return 'Card cannot be closed.'
      removeCardFromPlacement(snapshot, command.cardId)
      delete snapshot.cards[command.cardId]
      if (snapshot.focusedCardId === command.cardId) {
        snapshot.focusedCardId = snapshot.columns.center.primary.activeCardId
          ?? snapshot.columns.left.primary.activeCardId
          ?? snapshot.columns.right.primary.activeCardId
      }
      return null
    }

    case 'activateCard': {
      const placement = findCardPlacement(snapshot, command.cardId)
      if (!placement) return 'Card is not placed.'
      placement.stack.activeCardId = command.cardId
      snapshot.focusedCardId = command.cardId
      return null
    }

    case 'moveCard': {
      if (!descriptorAllows(snapshot, command.cardId, 'move', options)) return 'Card cannot be moved.'
      const placement = findCardPlacement(snapshot, command.cardId)
      if (!placement) return 'Card is not placed.'
      placement.stack.cardIds.splice(placement.index, 1)
      selectAfterRemoval(placement.stack, placement.index)
      const sourceColumn = snapshot.columns[placement.slotId]
      if (placement.stackId === 'secondary' && sourceColumn.secondary?.cardIds.length === 0) {
        sourceColumn.secondary = null
      }
      insertCard(ensureStack(snapshot.columns[command.slotId], command.stackId), command.cardId, command.index)
      snapshot.focusedCardId = command.cardId
      return null
    }

    case 'moveStack': {
      const fromColumn = snapshot.columns[command.fromSlotId]
      const toColumn = snapshot.columns[command.toSlotId]
      const from = stackFor(fromColumn, command.fromStackId)
      if (!from) return 'Source stack does not exist.'
      const to = stackFor(toColumn, command.toStackId)
      const replacement = to ? structuredClone(to) : null
      if (command.toStackId === 'primary') toColumn.primary = structuredClone(from)
      else toColumn.secondary = structuredClone(from)
      if (command.fromStackId === 'primary') {
        fromColumn.primary = replacement ?? { cardIds: [], activeCardId: null }
      } else {
        fromColumn.secondary = replacement
      }
      return null
    }

    case 'swapColumns': {
      if (command.first === command.second) return null
      const firstIndex = snapshot.columnOrder.indexOf(command.first)
      const secondIndex = snapshot.columnOrder.indexOf(command.second)
      if (firstIndex < 0 || secondIndex < 0) return 'Column is not present.'
      snapshot.columnOrder[firstIndex] = command.second
      snapshot.columnOrder[secondIndex] = command.first
      return null
    }

    case 'splitStack': {
      const column = snapshot.columns[command.slotId]
      const secondary = ensureStack(column, 'secondary')
      column.splitRatio = clamp(command.ratio ?? DEFAULT_SPLIT_RATIO, 0.2, 0.8)
      if (command.cardId) {
        const placement = findCardPlacement(snapshot, command.cardId)
        if (!placement) return 'Card is not placed.'
        if (!descriptorAllows(snapshot, command.cardId, 'move', options)) return 'Card cannot be moved.'
        placement.stack.cardIds.splice(placement.index, 1)
        selectAfterRemoval(placement.stack, placement.index)
        if (!secondary.cardIds.includes(command.cardId)) insertCard(secondary, command.cardId)
      }
      return null
    }

    case 'mergeStack': {
      const column = snapshot.columns[command.slotId]
      if (!column.secondary) return null
      for (const cardId of column.secondary.cardIds) {
        if (!column.primary.cardIds.includes(cardId)) column.primary.cardIds.push(cardId)
      }
      column.primary.activeCardId ??= column.secondary.activeCardId
      column.secondary = null
      column.splitRatio = DEFAULT_SPLIT_RATIO
      return null
    }

    case 'resizeColumn':
      snapshot.columns[command.slotId].width = clamp(command.width, MIN_COLUMN_WIDTH, MAX_COLUMN_WIDTH)
      return null

    case 'resizeSplit':
      snapshot.columns[command.slotId].splitRatio = clamp(command.ratio, 0.2, 0.8)
      return null

    case 'collapseColumn':
      snapshot.columns[command.slotId].collapsed = command.collapsed
      return null

    case 'applyPreset':
    case 'resetScope': {
      if (command.snapshot.pageId !== snapshot.pageId) return 'Preset page does not match.'
      const replacement = cloneSnapshot(command.snapshot)
      replacement.revision = snapshot.revision
      Object.assign(snapshot, replacement)
      return null
    }
  }
}

export function reduceWorkbenchLayout(
  current: WorkbenchLayoutSnapshot,
  envelope: WorkbenchCommandEnvelope,
  options: WorkbenchReducerOptions = {},
): WorkbenchReducerResult {
  if (envelope.source === 'ai') {
    return { ok: false, state: current, error: 'AI layout commands are not enabled.' }
  }
  if (envelope.expectedRevision !== current.revision) {
    return { ok: false, state: current, error: 'Layout revision is stale.' }
  }

  const settingsWidthChange = isSettingsPreset(current)
    && envelope.command.kind === 'resizeColumn'
    && envelope.command.slotId === 'left'
  const structuralCommand = !['activateCard', 'applyPreset', 'resetScope'].includes(envelope.command.kind)
  if ((options.locked || isSettingsPreset(current)) && structuralCommand && !settingsWidthChange) {
    return { ok: false, state: current, error: 'This workbench preset has a fixed layout.' }
  }

  const next = cloneSnapshot(current)
  const inverseSnapshot = cloneSnapshot(current)
  const error = applyCommand(next, envelope.command, options)
  if (error) return { ok: false, state: current, error }

  next.revision = current.revision + 1
  return {
    ok: true,
    state: next,
    inverse: { kind: 'applyPreset', snapshot: inverseSnapshot },
  }
}
