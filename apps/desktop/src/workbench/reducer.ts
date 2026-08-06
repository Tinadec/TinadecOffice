import type {
  PersistedCardInstance,
  WorkbenchColumn,
  WorkbenchLayoutSnapshot,
  WorkbenchPageId,
  WorkbenchSlotId,
  WorkbenchStack,
  WorkbenchStackId,
} from './types'
import type {
  WorkbenchCommand,
  WorkbenchCommandEnvelope,
} from './commands'
import { isInternalCommand, isRejectedSource } from './commands'
import type { CardRegistry } from './registry'
import { COLLAPSED_COLUMN_WIDTH } from './types'

// ---------------------------------------------------------------------------
// Layout reducer.
//
// reduce(snapshot, envelope) => { next, inverse }
//   - validates source (reject 'ai'), expectedRevision, internal commands,
//     singleton conflicts, and illegal operations (settings cards).
//   - returns the next snapshot AND the inverse command so the undo stack can
//     replay it. Pure: no side effects, no IDs generated (caller passes them).
// ---------------------------------------------------------------------------

export type ReducerResult = {
  next: WorkbenchLayoutSnapshot
  inverse: WorkbenchCommand
  /** True when the command mutated the layout. */
  changed: boolean
}

export interface ReduceContext {
  registry: CardRegistry
  /** Used to create new instance ids (openCard). */
  nextInstanceId: () => string
  /** Slot that is "locked" (e.g. settings nav) and rejects mutations. */
  lockedSlots?: ReadonlySet<WorkbenchSlotId>
}

export interface LayoutError {
  code: string
  message: string
}

export type ReduceOutcome =
  | { ok: true; result: ReducerResult }
  | { ok: false; error: LayoutError }

const PAGE_DEFAULT_ORDER: readonly WorkbenchSlotId[] = ['left', 'center', 'right']

function cloneSnapshot(s: WorkbenchLayoutSnapshot): WorkbenchLayoutSnapshot {
  return structuredClone(s)
}

function bumpRevision(s: WorkbenchLayoutSnapshot): WorkbenchLayoutSnapshot {
  return { ...s, revision: s.revision + 1 }
}

function emptyStack(stackId: WorkbenchStackId): WorkbenchStack {
  return { stackId, tabIds: [], activeTabId: null }
}

function emptyColumn(slotId: WorkbenchSlotId): WorkbenchColumn {
  return {
    slotId,
    width: 260,
    collapsed: false,
    surfaceMode: 'float',
    topInset: 8,
    primary: emptyStack('primary'),
    secondary: null,
    splitRatio: null,
  }
}

/** Find the stack (slot + stackId) that currently hosts an instance, if any. */
export function findInstanceLocation(
  snapshot: WorkbenchLayoutSnapshot,
  instanceId: string,
): { slotId: WorkbenchSlotId; stackId: WorkbenchStackId; index: number } | null {
  for (const slotId of snapshot.columnOrder) {
    const col = snapshot.columns[slotId]
    for (const stack of [col.primary, col.secondary]) {
      if (!stack) continue
      const index = stack.tabIds.indexOf(instanceId)
      if (index !== -1) return { slotId, stackId: stack.stackId, index }
    }
  }
  return null
}

function stackOf(
  col: WorkbenchColumn,
  stackId: WorkbenchStackId,
): WorkbenchStack | null {
  if (stackId === 'primary') return col.primary
  return col.secondary
}

/** Gather the instance in `cards` that has the given descriptorId (singleton lookup). */
function findSingletonInstance(
  snapshot: WorkbenchLayoutSnapshot,
  descriptorId: string,
  ctx: ReduceContext,
): string | undefined {
  const desc = ctx.registry.get(descriptorId)
  if (!desc?.singleton) return undefined
  return Object.values(snapshot.cards).find((c) => c.descriptorId === descriptorId)?.id
}

/** Default slot for a newly opened card: the last non-collapsed float column, else center. */
function defaultSlotId(snapshot: WorkbenchLayoutSnapshot): WorkbenchSlotId {
  const order = [...snapshot.columnOrder].reverse()
  for (const slotId of order) {
    const col = snapshot.columns[slotId]
    if (!col.collapsed) return slotId
  }
  return 'center'
}

function isLocked(slotId: WorkbenchSlotId, ctx: ReduceContext): boolean {
  return ctx.lockedSlots?.has(slotId) ?? false
}

// ---------------------------------------------------------------------------

export function reduce(
  snapshot: WorkbenchLayoutSnapshot,
  envelope: WorkbenchCommandEnvelope,
  ctx: ReduceContext,
): ReduceOutcome {
  const { command, source, expectedRevision } = envelope

  if (isRejectedSource(source)) {
    return { ok: false, error: { code: 'source_rejected', message: `source '${source}' is not allowed this round` } }
  }
  if (isInternalCommand(command)) {
    return { ok: false, error: { code: 'internal_command', message: `command '${command.type}' is internal and cannot be dispatched` } }
  }
  if (expectedRevision !== snapshot.revision) {
    return { ok: false, error: { code: 'revision_mismatch', message: `expected revision ${expectedRevision}, got ${snapshot.revision}` } }
  }

  const result = applyCommand(snapshot, command, ctx)
  if (!result) {
    return { ok: false, error: { code: 'rejected', message: `command '${command.type}' rejected` } }
  }
  return { ok: true, result }
}

function applyCommand(
  snapshot: WorkbenchLayoutSnapshot,
  command: WorkbenchCommand,
  ctx: ReduceContext,
): ReducerResult | null {
  switch (command.type) {
    case 'openCard':
      return openCard(snapshot, command, ctx)
    case 'closeCard':
      return closeCard(snapshot, command, ctx)
    case 'activateCard':
      return activateCard(snapshot, command, ctx)
    case 'moveCard':
      return moveCard(snapshot, command, ctx)
    case 'moveStack':
      return moveStack(snapshot, command, ctx)
    case 'swapColumns':
      return swapColumns(snapshot, command, ctx)
    case 'splitStack':
      return splitStack(snapshot, command, ctx)
    case 'mergeStack':
      return mergeStack(snapshot, command, ctx)
    case 'resizeColumn':
      return resizeColumn(snapshot, command, ctx)
    case 'resizeSplit':
      return resizeSplit(snapshot, command, ctx)
    case 'collapseColumn':
      return collapseColumn(snapshot, command, ctx)
    case 'applyPreset':
      return applyPreset(snapshot, command, ctx)
    case 'resetScope':
      return resetScope(snapshot, command, ctx)
    default:
      return null
  }
}

function openCard(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'openCard' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const next = bumpRevision(cloneSnapshot(snapshot))
  const desc = ctx.registry.get(command.descriptorId)
  if (!desc) {
    return null
  }

  // Singleton: activate existing instance instead of opening a duplicate.
  if (desc.singleton) {
    const existingId = findSingletonInstance(snapshot, command.descriptorId, ctx)
    if (existingId) {
      return activateCard(snapshot, { type: 'activateCard', scope: command.scope, instanceId: existingId }, ctx)
    }
  }

  const instanceId = command.instanceId ?? ctx.nextInstanceId()
  const instance: PersistedCardInstance = {
    id: instanceId,
    descriptorId: command.descriptorId,
    title: command.title ?? desc.defaultTitle,
    ...(command.state ? { state: command.state } : {}),
  }

  const slotId = command.slotId ?? defaultSlotId(snapshot)
  const stackId = command.stackId ?? 'primary'
  const col = next.columns[slotId]
  const stack = stackOf(col, stackId)
  if (!stack) {
    return null
  }
  if (isLocked(slotId, ctx) && !desc.movable) {
    return null
  }

  const index = command.toIndex ?? stack.tabIds.length
  stack.tabIds.splice(Math.max(0, Math.min(index, stack.tabIds.length)), 0, instanceId)
  stack.activeTabId = instanceId
  next.cards[instanceId] = instance
  next.focusedCardId = instanceId

  const inverse: WorkbenchCommand = { type: 'closeCard', scope: command.scope, instanceId }
  return { next, inverse, changed: true }
}

function closeCard(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'closeCard' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const loc = findInstanceLocation(snapshot, command.instanceId)
  if (!loc) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const instance = snapshot.cards[command.instanceId]
  if (!instance) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const desc = ctx.registry.get(instance.descriptorId)
  if (desc && !desc.closable) {
    return null
  }

  const next = bumpRevision(cloneSnapshot(snapshot))
  const col = next.columns[loc.slotId]
  const stack = stackOf(col, loc.stackId)!
  stack.tabIds.splice(loc.index, 1)
  if (stack.activeTabId === command.instanceId) {
    const remaining = stack.tabIds
    stack.activeTabId = remaining[Math.max(0, loc.index - 1)] ?? remaining[0] ?? null
  }
  if (next.focusedCardId === command.instanceId) {
    next.focusedCardId = stack.activeTabId
  }
  delete next.cards[command.instanceId]

  const inverse: WorkbenchCommand = {
    type: 'openCard',
    scope: command.scope,
    descriptorId: instance.descriptorId,
    slotId: loc.slotId,
    stackId: loc.stackId,
    toIndex: loc.index,
    title: instance.title,
    instanceId: command.instanceId,
    ...(instance.state ? { state: instance.state } : {}),
  }
  return { next, inverse, changed: true }
}

function activateCard(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'activateCard' }>,
  _ctx: ReduceContext,
): ReducerResult {
  const loc = findInstanceLocation(snapshot, command.instanceId)
  if (!loc) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const prevActive = snapshot.columns[loc.slotId].primary.activeTabId
  const next = bumpRevision(cloneSnapshot(snapshot))
  const col = next.columns[loc.slotId]
  const stack = stackOf(col, loc.stackId)!
  stack.activeTabId = command.instanceId
  next.focusedCardId = command.instanceId

  const inverse: WorkbenchCommand = {
    type: 'activateCard',
    scope: command.scope,
    instanceId: prevActive ?? command.instanceId,
  }
  return { next, inverse, changed: prevActive !== command.instanceId }
}

function moveCard(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'moveCard' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const loc = findInstanceLocation(snapshot, command.instanceId)
  if (!loc) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const instance = snapshot.cards[command.instanceId]
  const desc = ctx.registry.get(instance.descriptorId)
  if (desc && !desc.movable) {
    return null
  }
  const toSlotId = command.toSlotId ?? loc.slotId
  if (isLocked(toSlotId, ctx)) {
    return null
  }

  const next = bumpRevision(cloneSnapshot(snapshot))
  const fromCol = next.columns[loc.slotId]
  const fromStack = stackOf(fromCol, loc.stackId)!
  fromStack.tabIds.splice(loc.index, 1)
  if (fromStack.activeTabId === command.instanceId) {
    const rem = fromStack.tabIds
    fromStack.activeTabId = rem[Math.max(0, loc.index - 1)] ?? rem[0] ?? null
  }

  const toStackId = command.toStackId ?? 'primary'
  const toCol = next.columns[toSlotId]
  const toStack = stackOf(toCol, toStackId)
  if (!toStack) {
    // If secondary doesn't exist yet, create it by splitting.
    return null
  }
  const index = command.toIndex ?? toStack.tabIds.length
  toStack.tabIds.splice(Math.max(0, Math.min(index, toStack.tabIds.length)), 0, command.instanceId)
  toStack.activeTabId = command.instanceId
  next.focusedCardId = command.instanceId

  const inverse: WorkbenchCommand = {
    type: 'moveCard',
    scope: command.scope,
    instanceId: command.instanceId,
    toSlotId: loc.slotId,
    toStackId: loc.stackId,
    toIndex: loc.index,
  }
  return { next, inverse, changed: true }
}

function moveStack(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'moveStack' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const src = snapshot.columns[command.slotId]
  if (!src) return { next: snapshot, inverse: command, changed: false }
  if (isLocked(command.toSlotId, ctx)) {
    return null
  }
  const next = bumpRevision(cloneSnapshot(snapshot))
  const srcCol = next.columns[command.slotId]
  const dstCol = next.columns[command.toSlotId]

  // Move primary stack contents into the destination primary stack.
  const cardsToMove = [...srcCol.primary.tabIds]
  const index = command.toIndex ?? dstCol.primary.tabIds.length
  dstCol.primary.tabIds.splice(Math.max(0, Math.min(index, dstCol.primary.tabIds.length)), 0, ...cardsToMove)
  dstCol.primary.activeTabId = srcCol.primary.activeTabId
  srcCol.primary = emptyStack('primary')

  const inverse: WorkbenchCommand = {
    type: 'moveStack',
    scope: command.scope,
    slotId: command.toSlotId,
    toSlotId: command.slotId,
    toIndex: 0,
  }
  return { next, inverse, changed: cardsToMove.length > 0 }
}

function swapColumns(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'swapColumns' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  if (isLocked(command.a, ctx) || isLocked(command.b, ctx)) {
    return null
  }
  const next = bumpRevision(cloneSnapshot(snapshot))
  const a = next.columns[command.a]
  const b = next.columns[command.b]
  if (!a || !b) return { next: snapshot, inverse: command, changed: false }

  // Swap full column definitions.
  const aCopy = structuredClone(a)
  const bCopy = structuredClone(b)
  next.columns[command.a] = { ...bCopy, slotId: command.a }
  next.columns[command.b] = { ...aCopy, slotId: command.b }

  const inverse: WorkbenchCommand = {
    type: 'swapColumns',
    scope: command.scope,
    a: command.b,
    b: command.a,
  }
  return { next, inverse, changed: true }
}

function splitStack(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'splitStack' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const col = snapshot.columns[command.slotId]
  if (!col || col.secondary) {
    return { next: snapshot, inverse: command, changed: false }
  }
  if (isLocked(command.slotId, ctx)) {
    return null
  }

  const next = bumpRevision(cloneSnapshot(snapshot))
  const nextCol = next.columns[command.slotId]
  const primaryIds = [...nextCol.primary.tabIds]

  // Move the given instance (or the active card) into the new secondary stack.
  const instanceId = command.instanceId ?? nextCol.primary.activeTabId
  const idx = instanceId ? primaryIds.indexOf(instanceId) : -1
  let moved: string[] = []
  if (idx !== -1) {
    moved = primaryIds.splice(idx)
    // Keep at least one card in primary if we moved everything.
    if (primaryIds.length === 0 && moved.length > 1) {
      const keep = moved.shift()!
      primaryIds.push(keep)
    }
    nextCol.primary.tabIds = primaryIds
    nextCol.primary.activeTabId = primaryIds[0] ?? null
  }

  nextCol.secondary = {
    stackId: 'secondary',
    tabIds: moved,
    activeTabId: moved[0] ?? null,
  }
  nextCol.splitRatio = command.ratio ?? 0.65

  const inverse: WorkbenchCommand = { type: 'mergeStack', scope: command.scope, slotId: command.slotId }
  return { next, inverse, changed: moved.length > 0 }
}

function mergeStack(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'mergeStack' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const col = snapshot.columns[command.slotId]
  if (!col || !col.secondary) {
    return { next: snapshot, inverse: command, changed: false }
  }
  if (isLocked(command.slotId, ctx)) {
    return null
  }

  const next = bumpRevision(cloneSnapshot(snapshot))
  const nextCol = next.columns[command.slotId]
  const sec = nextCol.secondary!
  const secIds = [...sec.tabIds]
  const ratio = nextCol.splitRatio ?? 0.65
  const primaryActive = nextCol.primary.activeTabId

  nextCol.primary.tabIds.push(...secIds)
  nextCol.primary.activeTabId = primaryActive
  nextCol.secondary = null
  nextCol.splitRatio = null

  const inverse: WorkbenchCommand = {
    type: 'splitStack',
    scope: command.scope,
    slotId: command.slotId,
    instanceId: secIds[0] ?? undefined,
    ratio,
  }
  return { next, inverse, changed: secIds.length > 0 }
}

function resizeColumn(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'resizeColumn' }>,
  ctx: ReduceContext,
): ReducerResult {
  const col = snapshot.columns[command.slotId]
  if (!col) return { next: snapshot, inverse: command, changed: false }
  const prevWidth = col.width
  const width = Math.max(160, Math.min(1200, Math.round(command.width)))
  if (width === prevWidth) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const next = bumpRevision(cloneSnapshot(snapshot))
  next.columns[command.slotId].width = width

  const inverse: WorkbenchCommand = {
    type: 'resizeColumn',
    scope: command.scope,
    slotId: command.slotId,
    width: prevWidth,
  }
  return { next, inverse, changed: true }
}

function resizeSplit(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'resizeSplit' }>,
  ctx: ReduceContext,
): ReducerResult {
  const col = snapshot.columns[command.slotId]
  if (!col || !col.secondary) return { next: snapshot, inverse: command, changed: false }
  const prevRatio = col.splitRatio ?? 0.65
  const ratio = Math.max(0.1, Math.min(0.9, command.ratio))
  if (ratio === prevRatio) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const next = bumpRevision(cloneSnapshot(snapshot))
  next.columns[command.slotId].splitRatio = ratio

  const inverse: WorkbenchCommand = {
    type: 'resizeSplit',
    scope: command.scope,
    slotId: command.slotId,
    ratio: prevRatio,
  }
  return { next, inverse, changed: true }
}

function collapseColumn(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'collapseColumn' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const col = snapshot.columns[command.slotId]
  if (!col) return { next: snapshot, inverse: command, changed: false }
  if (isLocked(command.slotId, ctx)) {
    return null
  }
  if (col.collapsed === command.collapsed) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const next = bumpRevision(cloneSnapshot(snapshot))
  next.columns[command.slotId].collapsed = command.collapsed

  const inverse: WorkbenchCommand = {
    type: 'collapseColumn',
    scope: command.scope,
    slotId: command.slotId,
    collapsed: !command.collapsed,
  }
  return { next, inverse, changed: true }
}

function applyPreset(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'applyPreset' }>,
  ctx: ReduceContext,
): ReducerResult {
  const prev = cloneSnapshot(snapshot)
  const next = bumpRevision(cloneSnapshot(prev))
  next.pageId = command.presetId as WorkbenchPageId
  // Preset content is applied by the caller via __restoreSnapshot of the built preset;
  // here we only flip the pageId so navigation focuses the right page.
  const inverse: WorkbenchCommand = {
    type: '__restoreSnapshot',
    scope: command.scope,
    snapshot: prev,
  }
  return { next, inverse, changed: next.pageId !== prev.pageId }
}

function resetScope(
  snapshot: WorkbenchLayoutSnapshot,
  command: Extract<WorkbenchCommand, { type: 'resetScope' }>,
  _ctx: ReduceContext,
): ReducerResult {
  const prev = cloneSnapshot(snapshot)
  const next = bumpRevision(cloneSnapshot(prev))
  const inverse: WorkbenchCommand = {
    type: '__restoreSnapshot',
    scope: command.scope,
    snapshot: prev,
  }
  return { next, inverse, changed: false }
}

/** Re-export helpers used by presets/repair to build a fresh snapshot. */
export function createEmptySnapshot(pageId: WorkbenchPageId, revision = 1): WorkbenchLayoutSnapshot {
  const order = [...PAGE_DEFAULT_ORDER] as WorkbenchSlotId[]
  const columns: Record<WorkbenchSlotId, WorkbenchColumn> = {
    left: emptyColumn('left'),
    center: emptyColumn('center'),
    right: emptyColumn('right'),
  }
  return {
    version: 1,
    revision,
    pageId,
    columnOrder: order,
    columns,
    cards: {},
    focusedCardId: null,
    gap: 8,
  }
}

export { COLLAPSED_COLUMN_WIDTH }
