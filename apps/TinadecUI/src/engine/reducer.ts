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
import type {
  UieCommand,
  UieCommandEnvelope,
} from './commands'
import { isInternalCommand, isRejectedSource } from './commands'
import type { CardRegistry } from './registry'
import { COLLAPSED_COLUMN_WIDTH, DEFAULT_DOCK_RATIO, EDGE_INSET, MAX_DOCK_COLUMN_WIDTH } from './types'

/** Target width after the first row (side-by-side) dock split. */
const INITIAL_DOCK_SPLIT_WIDTH = 480

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
  next: UieLayoutSnapshot
  inverse: UieCommand
  /** True when the command mutated the layout. */
  changed: boolean
}

export interface ReduceContext {
  registry: CardRegistry
  /** Used to create new instance ids (openCard). */
  nextInstanceId: () => string
  /** Used to create new dock pane ids (splitDockPane). */
  nextDockId?: () => string
  /** Used to create new dock split ids (splitDockPane). */
  nextDockSplitId?: () => string
  /** Slot that is "locked" (e.g. settings nav) and rejects mutations. */
  lockedSlots?: ReadonlySet<UieSlotId>
}

export interface InstanceLocation {
  slotId: UieSlotId
  /** Stack within the column (null when the card lives in a dock pane). */
  stackId: UieStackId | null
  /** Dock pane (null when the card lives in a column stack). */
  paneId: string | null
  index: number
}

export interface LayoutError {
  code: string
  message: string
}

export type ReduceOutcome =
  | { ok: true; result: ReducerResult }
  | { ok: false; error: LayoutError }

const PAGE_DEFAULT_ORDER: readonly UieSlotId[] = ['left', 'center', 'right']

function cloneSnapshot(s: UieLayoutSnapshot): UieLayoutSnapshot {
  return structuredClone(s)
}

function bumpRevision(s: UieLayoutSnapshot): UieLayoutSnapshot {
  return { ...s, revision: s.revision + 1 }
}

function emptyStack(stackId: UieStackId): UieStack {
  return { stackId, tabIds: [], activeTabId: null }
}

function emptyColumn(slotId: UieSlotId): UieColumn {
  return {
    slotId,
    width: 260,
    collapsed: false,
    surfaceMode: 'float',
    topInset: 8,
    primary: emptyStack('primary'),
    secondary: null,
    splitRatio: null,
    dock: null,
  }
}

// ---------------------------------------------------------------------------
// Dock tree helpers (operate on the cloned snapshot's column.dock).
// ---------------------------------------------------------------------------

/** Find a pane node by id (DFS). */
export function findDockPane(
  node: UieDockNode,
  paneId: string,
): UieDockPane | null {
  if (node.kind === 'pane') {
    return node.paneId === paneId ? node : null
  }
  return findDockPane(node.a, paneId) ?? findDockPane(node.b, paneId)
}

/** Find a split node by id (DFS). */
export function findDockSplit(node: UieDockNode, splitId: string): Extract<UieDockNode, { kind: 'split' }> | null {
  if (node.kind === 'split') {
    if (node.splitId === splitId) return node
    return findDockSplit(node.a, splitId) ?? findDockSplit(node.b, splitId)
  }
  return null
}

/** Collect all panes (DFS, rendering order). */
export function collectDockPanes(node: UieDockNode): UieDockPane[] {
  if (node.kind === 'pane') return [node]
  return [...collectDockPanes(node.a), ...collectDockPanes(node.b)]
}

/** Collect all tab instanceIds across every pane (main first). */
export function collectDockTabIds(node: UieDockNode): string[] {
  if (node.kind === 'pane') return [...node.tabIds]
  return [...collectDockTabIds(node.a), ...collectDockTabIds(node.b)]
}

/** Find the single main pane. */
function mainDockPane(node: UieDockNode): UieDockPane {
  const panes = collectDockPanes(node)
  const main = panes.find((p) => p.main)
  return main ?? panes[0]
}

/** Find the pane hosting an instance (DFS). */
export function findDockPaneForInstance(node: UieDockNode, instanceId: string): UieDockPane | null {
  if (node.kind === 'pane') {
    return node.tabIds.includes(instanceId) ? node : null
  }
  return findDockPaneForInstance(node.a, instanceId) ?? findDockPaneForInstance(node.b, instanceId)
}

/** Replace the subtree rooted at `targetId` with `newNode` (returns new root). */
export function replaceDockNode(node: UieDockNode, targetId: string, newNode: UieDockNode): UieDockNode {
  if (node.kind === 'pane') {
    return node.paneId === targetId ? newNode : node
  }
  if (node.splitId === targetId) return newNode
  const a = replaceDockNode(node.a, targetId, newNode)
  const b = replaceDockNode(node.b, targetId, newNode)
  if (a === node.a && b === node.b) return node
  return { ...node, a, b }
}

/**
 * Remove a pane from the tree, normalizing like dockview: an empty pane is
 * removed, and a split left with a single child is replaced by that child
 * (grandchild spread). Returns null when the tree becomes empty.
 */
export function removeDockPane(node: UieDockNode, paneId: string): UieDockNode | null {
  if (node.kind === 'pane') {
    return node.paneId === paneId ? null : node
  }
  const a = removeDockPane(node.a, paneId)
  const b = removeDockPane(node.b, paneId)
  if (a === null && b === null) return null
  if (a === null) return b
  if (b === null) return a
  if (a === node.a && b === node.b) return node
  return { ...node, a: a!, b: b! }
}

/**
 * Build the initial dock tree for a column that has no dock yet, using the
 * column's existing stacks. Mutates the given (cloned) column: primary keeps
 * the main pane's tabs, secondary/splitRatio are cleared.
 */
function ensureDockFromColumn(col: UieColumn, mainPaneId: string): UieDockPane {
  const tabs = [...col.primary.tabIds]
  if (col.secondary) tabs.push(...col.secondary.tabIds)
  col.primary.tabIds = []
  col.secondary = null
  col.splitRatio = null
  return {
    kind: 'pane',
    paneId: mainPaneId,
    main: true,
    tabIds: tabs,
    activeTabId: tabs[0] ?? null,
  }
}

/** Find the stack (slot + stackId) or dock pane that currently hosts an instance. */
export function findInstanceLocation(
  snapshot: UieLayoutSnapshot,
  instanceId: string,
): InstanceLocation | null {
  for (const slotId of snapshot.columnOrder) {
    const col = snapshot.columns[slotId]
    for (const stack of [col.primary, col.secondary]) {
      if (!stack) continue
      const index = stack.tabIds.indexOf(instanceId)
      if (index !== -1) return { slotId, stackId: stack.stackId, paneId: null, index }
    }
    if (col.dock) {
      const pane = findDockPaneForInstance(col.dock, instanceId)
      if (pane) return { slotId, stackId: null, paneId: pane.paneId, index: pane.tabIds.indexOf(instanceId) }
    }
  }
  return null
}

function stackOf(
  col: UieColumn,
  stackId: UieStackId,
): UieStack | null {
  if (stackId === 'primary') return col.primary
  return col.secondary
}

/** Gather the instance in `cards` that has the given descriptorId (singleton lookup). */
function findSingletonInstance(
  snapshot: UieLayoutSnapshot,
  descriptorId: string,
  ctx: ReduceContext,
): string | undefined {
  const desc = ctx.registry.get(descriptorId)
  if (!desc?.singleton) return undefined
  return Object.values(snapshot.cards).find((c) => c.descriptorId === descriptorId)?.id
}

/** Default slot for a newly opened card: the last non-collapsed float column, else center. */
function defaultSlotId(snapshot: UieLayoutSnapshot): UieSlotId {
  const order = [...snapshot.columnOrder].reverse()
  for (const slotId of order) {
    const col = snapshot.columns[slotId]
    if (!col.collapsed) return slotId
  }
  return 'center'
}

function isLocked(slotId: UieSlotId, ctx: ReduceContext): boolean {
  return ctx.lockedSlots?.has(slotId) ?? false
}

// ---------------------------------------------------------------------------

export function reduce(
  snapshot: UieLayoutSnapshot,
  envelope: UieCommandEnvelope,
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
  snapshot: UieLayoutSnapshot,
  command: UieCommand,
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
    case 'updateCardGrid':
      return updateCardGrid(snapshot, command, ctx)
    case 'splitDockPane':
      return splitDockPane(snapshot, command, ctx)
    case 'mergeDockPane':
      return mergeDockPane(snapshot, command, ctx)
    case 'mergeDockColumn':
      return mergeDockColumn(snapshot, command, ctx)
    case 'moveCardToDockPane':
      return moveCardToDockPane(snapshot, command, ctx)
    case 'resizeDockSplit':
      return resizeDockSplit(snapshot, command, ctx)
    case 'applyPreset':
      return applyPreset(snapshot, command, ctx)
    case 'resetScope':
      return resetScope(snapshot, command, ctx)
    default:
      return null
  }
}

function openCard(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'openCard' }>,
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
  if (isLocked(slotId, ctx) && !desc.movable) {
    return null
  }

  if (col.dock) {
    // Dock column: open into the requested pane (default: main pane).
    const main = mainDockPane(col.dock)
    const pane = command.paneId ? (findDockPane(col.dock, command.paneId) ?? main) : main
    const index = command.toIndex ?? pane.tabIds.length
    pane.tabIds.splice(Math.max(0, Math.min(index, pane.tabIds.length)), 0, instanceId)
    pane.activeTabId = instanceId
  } else {
    const stack = stackOf(col, stackId)
    if (!stack) {
      return null
    }
    const index = command.toIndex ?? stack.tabIds.length
    stack.tabIds.splice(Math.max(0, Math.min(index, stack.tabIds.length)), 0, instanceId)
    stack.activeTabId = instanceId
  }
  next.cards[instanceId] = instance
  next.focusedCardId = instanceId

  const inverse: UieCommand = { type: 'closeCard', scope: command.scope, instanceId }
  return { next, inverse, changed: true }
}

function closeCard(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'closeCard' }>,
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

  let nextFocus: string | null = null
  if (loc.paneId !== null) {
    const pane = findDockPane(col.dock!, loc.paneId)
    if (!pane) {
      return { next: snapshot, inverse: command, changed: false }
    }
    // Guard: never empty the main pane (homePicker is closable:false, but a
    // defensively cleared main pane would break the whole dock).
    if (pane.main && pane.tabIds.length === 1) {
      return { next: snapshot, inverse: command, changed: false }
    }
    pane.tabIds.splice(loc.index, 1)
    if (pane.activeTabId === command.instanceId) {
      pane.activeTabId = pane.tabIds[Math.max(0, loc.index - 1)] ?? pane.tabIds[0] ?? null
    }
    nextFocus = pane.activeTabId
    if (!pane.main && pane.tabIds.length === 0) {
      col.dock = removeDockPane(col.dock!, pane.paneId)
      // Normalize back to a stack when only the main pane remains.
      if (col.dock && collectDockPanes(col.dock).length === 1) {
        const only = collectDockPanes(col.dock)[0]
        col.primary.tabIds = [...only.tabIds]
        col.primary.activeTabId = only.activeTabId
        col.dock = null
        nextFocus = only.activeTabId
      }
    }
  } else {
    const stack = stackOf(col, loc.stackId!)
    if (!stack) return { next: snapshot, inverse: command, changed: false }
    stack.tabIds.splice(loc.index, 1)
    if (stack.activeTabId === command.instanceId) {
      const remaining = stack.tabIds
      stack.activeTabId = remaining[Math.max(0, loc.index - 1)] ?? remaining[0] ?? null
    }
    nextFocus = stack.activeTabId
  }
  if (next.focusedCardId === command.instanceId) {
    next.focusedCardId = nextFocus
  }
  delete next.cards[command.instanceId]

  const inverse: UieCommand = {
    type: 'openCard',
    scope: command.scope,
    descriptorId: instance.descriptorId,
    slotId: loc.slotId,
    stackId: loc.stackId ?? undefined,
    paneId: loc.paneId ?? undefined,
    toIndex: loc.index,
    title: instance.title,
    instanceId: command.instanceId,
    ...(instance.state ? { state: instance.state } : {}),
  }
  return { next, inverse, changed: true }
}

function activateCard(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'activateCard' }>,
  _ctx: ReduceContext,
): ReducerResult {
  const loc = findInstanceLocation(snapshot, command.instanceId)
  if (!loc) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const col = snapshot.columns[loc.slotId]
  const prevActive = loc.paneId !== null
    ? (findDockPane(col.dock!, loc.paneId)?.activeTabId ?? null)
    : (stackOf(col, loc.stackId!)?.activeTabId ?? null)
  const next = bumpRevision(cloneSnapshot(snapshot))
  const nextCol = next.columns[loc.slotId]
  if (loc.paneId !== null) {
    const pane = findDockPane(nextCol.dock!, loc.paneId)
    if (!pane) return { next: snapshot, inverse: command, changed: false }
    pane.activeTabId = command.instanceId
  } else {
    const stack = stackOf(nextCol, loc.stackId!)!
    stack.activeTabId = command.instanceId
  }
  next.focusedCardId = command.instanceId

  const inverse: UieCommand = {
    type: 'activateCard',
    scope: command.scope,
    instanceId: prevActive ?? command.instanceId,
  }
  return { next, inverse, changed: prevActive !== command.instanceId }
}

function moveCard(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'moveCard' }>,
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

  if (loc.paneId !== null) {
    const srcPane = findDockPane(fromCol.dock!, loc.paneId)
    if (!srcPane) return { next: snapshot, inverse: command, changed: false }
    srcPane.tabIds.splice(loc.index, 1)
    if (srcPane.activeTabId === command.instanceId) {
      const rem = srcPane.tabIds
      srcPane.activeTabId = rem[Math.max(0, loc.index - 1)] ?? rem[0] ?? null
    }
    if (!srcPane.main && srcPane.tabIds.length === 0) {
      fromCol.dock = removeDockPane(fromCol.dock!, srcPane.paneId)
    }
  } else {
    const fromStack = stackOf(fromCol, loc.stackId!)!
    fromStack.tabIds.splice(loc.index, 1)
    if (fromStack.activeTabId === command.instanceId) {
      const rem = fromStack.tabIds
      fromStack.activeTabId = rem[Math.max(0, loc.index - 1)] ?? rem[0] ?? null
    }
  }

  const toCol = next.columns[toSlotId]
  if (toCol.dock) {
    const toPane = mainDockPane(toCol.dock)
    const index = command.toIndex ?? toPane.tabIds.length
    toPane.tabIds.splice(Math.max(0, Math.min(index, toPane.tabIds.length)), 0, command.instanceId)
    toPane.activeTabId = command.instanceId
  } else {
    const toStackId = command.toStackId ?? 'primary'
    const toStack = stackOf(toCol, toStackId)
    if (!toStack) {
      // If secondary doesn't exist yet, create it by splitting.
      return null
    }
    const index = command.toIndex ?? toStack.tabIds.length
    toStack.tabIds.splice(Math.max(0, Math.min(index, toStack.tabIds.length)), 0, command.instanceId)
    toStack.activeTabId = command.instanceId
  }
  next.focusedCardId = command.instanceId

  const inverse: UieCommand = {
    type: 'moveCard',
    scope: command.scope,
    instanceId: command.instanceId,
    toSlotId: loc.slotId,
    toStackId: loc.stackId ?? undefined,
    toIndex: loc.index,
  }
  return { next, inverse, changed: true }
}

function moveStack(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'moveStack' }>,
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

  const inverse: UieCommand = {
    type: 'moveStack',
    scope: command.scope,
    slotId: command.toSlotId,
    toSlotId: command.slotId,
    toIndex: 0,
  }
  return { next, inverse, changed: cardsToMove.length > 0 }
}

function swapColumns(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'swapColumns' }>,
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

  const inverse: UieCommand = {
    type: 'swapColumns',
    scope: command.scope,
    a: command.b,
    b: command.a,
  }
  return { next, inverse, changed: true }
}

function splitStack(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'splitStack' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const col = snapshot.columns[command.slotId]
  if (!col || col.secondary || col.dock) {
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

  const inverse: UieCommand = { type: 'mergeStack', scope: command.scope, slotId: command.slotId }
  return { next, inverse, changed: moved.length > 0 }
}

function mergeStack(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'mergeStack' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const col = snapshot.columns[command.slotId]
  if (!col || !col.secondary || col.dock) {
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

  const inverse: UieCommand = {
    type: 'splitStack',
    scope: command.scope,
    slotId: command.slotId,
    instanceId: secIds[0] ?? undefined,
    ratio,
  }
  return { next, inverse, changed: secIds.length > 0 }
}

function resizeColumn(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'resizeColumn' }>,
  ctx: ReduceContext,
): ReducerResult {
  const col = snapshot.columns[command.slotId]
  if (!col) return { next: snapshot, inverse: command, changed: false }
  const prevWidth = col.width
  const maxW = col.dock ? MAX_DOCK_COLUMN_WIDTH : 1200
  const width = Math.max(160, Math.min(maxW, Math.round(command.width)))
  if (width === prevWidth) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const next = bumpRevision(cloneSnapshot(snapshot))
  next.columns[command.slotId].width = width

  const inverse: UieCommand = {
    type: 'resizeColumn',
    scope: command.scope,
    slotId: command.slotId,
    width: prevWidth,
  }
  return { next, inverse, changed: true }
}

function resizeSplit(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'resizeSplit' }>,
  ctx: ReduceContext,
): ReducerResult {
  const col = snapshot.columns[command.slotId]
  if (!col || !col.secondary || col.dock) return { next: snapshot, inverse: command, changed: false }
  const prevRatio = col.splitRatio ?? 0.65
  const ratio = Math.max(0.1, Math.min(0.9, command.ratio))
  if (ratio === prevRatio) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const next = bumpRevision(cloneSnapshot(snapshot))
  next.columns[command.slotId].splitRatio = ratio

  const inverse: UieCommand = {
    type: 'resizeSplit',
    scope: command.scope,
    slotId: command.slotId,
    ratio: prevRatio,
  }
  return { next, inverse, changed: true }
}

// ---------------------------------------------------------------------------
// Dock commands — multi-pane splitting inside a column (feature/right column).
// ---------------------------------------------------------------------------

/**
 * Split a dock pane in two along `dir`, moving `instanceId` into the new pane.
 * On the first split the column's primary/secondary stacks are folded into the
 * main pane. The instance must be movable (homePicker is rejected upstream by
 * the UI; this is a defensive check too).
 */
function splitDockPane(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'splitDockPane' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const col = snapshot.columns[command.slotId]
  if (!col) return { next: snapshot, inverse: command, changed: false }
  if (isLocked(command.slotId, ctx)) return null
  if (command.dir !== 'row' && command.dir !== 'column') return null
  if (command.place !== 'start' && command.place !== 'end') return null

  const instance = snapshot.cards[command.instanceId]
  const desc = instance ? ctx.registry.get(instance.descriptorId) : undefined
  if (desc && !desc.movable) return null
  if (!instance) return { next: snapshot, inverse: command, changed: false }

  // No dock yet: fold the existing stacks into a main pane first.
  if (!col.dock) {
    const loc = findInstanceLocation(snapshot, command.instanceId)
    if (!loc || loc.slotId !== command.slotId || loc.paneId !== null) {
      return { next: snapshot, inverse: command, changed: false }
    }
    const next = bumpRevision(cloneSnapshot(snapshot))
    const nextCol = next.columns[command.slotId]
    const mainId = command.paneId2 ?? ctx.nextDockId?.() ?? 'dock-main'
    const mainPane = ensureDockFromColumn(nextCol, mainId)
    const idx = mainPane.tabIds.indexOf(command.instanceId)
    if (idx !== -1) mainPane.tabIds.splice(idx, 1)
    if (mainPane.activeTabId === command.instanceId) {
      mainPane.activeTabId = mainPane.tabIds[Math.max(0, idx - 1)] ?? mainPane.tabIds[0] ?? null
    }

    const newPane: UieDockPane = {
      kind: 'pane',
      paneId: command.paneId2 ?? ctx.nextDockId?.() ?? 'dock-pane-new',
      main: false,
      tabIds: [command.instanceId],
      activeTabId: command.instanceId,
    }
    const splitId = command.splitId ?? ctx.nextDockSplitId?.() ?? 'dock-split-new'
    const splitNode: UieDockNode = {
      kind: 'split',
      splitId,
      dir: command.dir,
      ratio: command.ratio ?? DEFAULT_DOCK_RATIO,
      a: newPane,
      b: mainPane,
    }
    if (command.place === 'end') {
      splitNode.a = mainPane
      splitNode.b = newPane
    }
    nextCol.dock = splitNode
    // Initial row split widens the column a bit so both panes fit comfortably.
    // The ceiling is capped well below MAX_DOCK_COLUMN_WIDTH so a narrow window
    // does not push the constraint solver into collapsing the right column.
    if (command.dir === 'row') {
      nextCol.width = Math.max(nextCol.width, Math.min(INITIAL_DOCK_SPLIT_WIDTH, MAX_DOCK_COLUMN_WIDTH))
    }
    next.focusedCardId = command.instanceId

    const inverse: UieCommand = {
      type: 'mergeDockPane',
      scope: command.scope,
      slotId: command.slotId,
      paneId: newPane.paneId,
    }
    return { next, inverse, changed: true }
  }

  // Dock already exists: remove the instance from its source, then split.
  const loc = findInstanceLocation(snapshot, command.instanceId)
  if (!loc || loc.slotId !== command.slotId) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const next = bumpRevision(cloneSnapshot(snapshot))
  const nextCol = next.columns[command.slotId]

  if (loc.paneId !== null) {
    const srcPane = findDockPane(nextCol.dock!, loc.paneId)
    if (!srcPane) return { next: snapshot, inverse: command, changed: false }
    // A pane with a single card cannot split itself (would leave it empty).
    if (loc.paneId === command.paneId && srcPane.tabIds.length === 1) {
      return { next: snapshot, inverse: command, changed: false }
    }
    const idx = srcPane.tabIds.indexOf(command.instanceId)
    srcPane.tabIds.splice(idx, 1)
    if (srcPane.activeTabId === command.instanceId) {
      srcPane.activeTabId = srcPane.tabIds[Math.max(0, idx - 1)] ?? srcPane.tabIds[0] ?? null
    }
    if (srcPane !== mainDockPane(nextCol.dock!) && srcPane.tabIds.length === 0) {
      nextCol.dock = removeDockPane(nextCol.dock!, srcPane.paneId)
    }
  } else {
    const srcStack = stackOf(nextCol, loc.stackId!)
    if (!srcStack) return { next: snapshot, inverse: command, changed: false }
    srcStack.tabIds.splice(loc.index, 1)
    if (srcStack.activeTabId === command.instanceId) {
      srcStack.activeTabId = srcStack.tabIds[Math.max(0, loc.index - 1)] ?? srcStack.tabIds[0] ?? null
    }
  }

  if (!nextCol.dock) return { next: snapshot, inverse: command, changed: false }
  const targetPane = findDockPane(nextCol.dock, command.paneId)
  if (!targetPane) return { next: snapshot, inverse: command, changed: false }

  const newPane: UieDockPane = {
    kind: 'pane',
    paneId: command.paneId2 ?? ctx.nextDockId?.() ?? 'dock-pane-new',
    main: false,
    tabIds: [command.instanceId],
    activeTabId: command.instanceId,
  }
  const splitId = command.splitId ?? ctx.nextDockSplitId?.() ?? 'dock-split-new'
  const splitNode: UieDockNode = {
    kind: 'split',
    splitId,
    dir: command.dir,
    ratio: command.ratio ?? DEFAULT_DOCK_RATIO,
    a: newPane,
    b: targetPane,
  }
  if (command.place === 'end') {
    splitNode.a = targetPane
    splitNode.b = newPane
  }
  nextCol.dock = replaceDockNode(nextCol.dock, command.paneId, splitNode)
  if (command.dir === 'row') {
    nextCol.width = Math.max(nextCol.width, Math.min(INITIAL_DOCK_SPLIT_WIDTH, MAX_DOCK_COLUMN_WIDTH))
  }
  next.focusedCardId = command.instanceId

  const inverse: UieCommand = {
    type: 'mergeDockPane',
    scope: command.scope,
    slotId: command.slotId,
    paneId: newPane.paneId,
  }
  return { next, inverse, changed: true }
}

/** Merge one dock pane back into the main pane; normalize when only main remains. */
function mergeDockPane(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'mergeDockPane' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const col = snapshot.columns[command.slotId]
  if (!col || !col.dock) return { next: snapshot, inverse: command, changed: false }
  if (isLocked(command.slotId, ctx)) return null

  const next = bumpRevision(cloneSnapshot(snapshot))
  const nextCol = next.columns[command.slotId]
  const main = mainDockPane(nextCol.dock!)
  if (command.paneId === main.paneId) {
    return { next: snapshot, inverse: command, changed: false }
  }
  const pane = findDockPane(nextCol.dock!, command.paneId)
  if (!pane) return { next: snapshot, inverse: command, changed: false }

  main.tabIds.push(...pane.tabIds)
  nextCol.dock = removeDockPane(nextCol.dock!, command.paneId)
  if (nextCol.dock && collectDockPanes(nextCol.dock).length === 1) {
    const only = collectDockPanes(nextCol.dock)[0]
    nextCol.primary.tabIds = [...only.tabIds]
    nextCol.primary.activeTabId = only.activeTabId
    nextCol.dock = null
  }

  const inverse: UieCommand = {
    type: 'mergeDockPane',
    scope: command.scope,
    slotId: command.slotId,
    paneId: command.paneId,
  }
  return { next, inverse, changed: true }
}

/** Atomically merge every dock pane back into a single stack (dock=null). */
function mergeDockColumn(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'mergeDockColumn' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const col = snapshot.columns[command.slotId]
  if (!col || !col.dock) return { next: snapshot, inverse: command, changed: false }
  if (isLocked(command.slotId, ctx)) return null

  const next = bumpRevision(cloneSnapshot(snapshot))
  const nextCol = next.columns[command.slotId]
  const dock = nextCol.dock!
  const main = mainDockPane(dock)
  const others = collectDockPanes(dock).filter((p) => p.paneId !== main.paneId)
  for (const p of others) main.tabIds.push(...p.tabIds)
  nextCol.primary.tabIds = [...main.tabIds]
  nextCol.primary.activeTabId = main.activeTabId
  nextCol.dock = null

  const inverse: UieCommand = {
    type: 'mergeDockColumn',
    scope: command.scope,
    slotId: command.slotId,
  }
  return { next, inverse, changed: others.length > 0 }
}

/** Move a card into a specific dock pane (center drop = merge tab into pane). */
function moveCardToDockPane(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'moveCardToDockPane' }>,
  ctx: ReduceContext,
): ReducerResult | null {
  const loc = findInstanceLocation(snapshot, command.instanceId)
  if (!loc) return { next: snapshot, inverse: command, changed: false }
  const instance = snapshot.cards[command.instanceId]
  if (!instance) return { next: snapshot, inverse: command, changed: false }
  const desc = ctx.registry.get(instance.descriptorId)
  if (desc && !desc.movable) return null

  // Locate the target pane in the current snapshot (before mutation).
  let targetSlot: UieSlotId | null = null
  for (const slotId of snapshot.columnOrder) {
    const col = snapshot.columns[slotId]
    if (!col.dock) continue
    if (findDockPane(col.dock, command.toPaneId)) {
      targetSlot = slotId
      break
    }
  }
  if (!targetSlot) return { next: snapshot, inverse: command, changed: false }

  const next = bumpRevision(cloneSnapshot(snapshot))
  const fromCol = next.columns[loc.slotId]
  let removedPaneId: string | null = null

  if (loc.paneId !== null) {
    const srcPane = findDockPane(fromCol.dock!, loc.paneId)
    if (!srcPane) return { next: snapshot, inverse: command, changed: false }
    const idx = srcPane.tabIds.indexOf(command.instanceId)
    srcPane.tabIds.splice(idx, 1)
    if (srcPane.activeTabId === command.instanceId) {
      srcPane.activeTabId = srcPane.tabIds[Math.max(0, idx - 1)] ?? srcPane.tabIds[0] ?? null
    }
    if (srcPane !== mainDockPane(fromCol.dock!) && srcPane.tabIds.length === 0) {
      fromCol.dock = removeDockPane(fromCol.dock!, srcPane.paneId)
      removedPaneId = srcPane.paneId
    }
    // Moving the last card out of a split collapses the dock back to a stack.
    if (fromCol.dock && collectDockPanes(fromCol.dock).length === 1) {
      const only = collectDockPanes(fromCol.dock)[0]
      fromCol.primary.tabIds = [...only.tabIds]
      fromCol.primary.activeTabId = only.activeTabId
      fromCol.dock = null
    }
  } else {
    const srcStack = stackOf(fromCol, loc.stackId!)
    if (!srcStack) return { next: snapshot, inverse: command, changed: false }
    srcStack.tabIds.splice(loc.index, 1)
    if (srcStack.activeTabId === command.instanceId) {
      srcStack.activeTabId = srcStack.tabIds[Math.max(0, loc.index - 1)] ?? srcStack.tabIds[0] ?? null
    }
  }

  // The source pane cannot also be the target (it is now empty/removed).
  if (removedPaneId === command.toPaneId) {
    return { next: snapshot, inverse: command, changed: false }
  }

  // Insert into the target pane (re-locate post-clone), or — when removing the
  // source collapsed the target column's dock back to a stack — its primary.
  const targetCol = next.columns[targetSlot]
  const targetPaneAfter = targetCol.dock ? findDockPane(targetCol.dock, command.toPaneId) : null
  if (targetPaneAfter) {
    const index = command.toIndex ?? targetPaneAfter.tabIds.length
    targetPaneAfter.tabIds.splice(Math.max(0, Math.min(index, targetPaneAfter.tabIds.length)), 0, command.instanceId)
    targetPaneAfter.activeTabId = command.instanceId
  } else {
    const index = command.toIndex ?? targetCol.primary.tabIds.length
    targetCol.primary.tabIds.splice(Math.max(0, Math.min(index, targetCol.primary.tabIds.length)), 0, command.instanceId)
    targetCol.primary.activeTabId = command.instanceId
  }
  next.focusedCardId = command.instanceId

  const inverse: UieCommand = {
    type: 'moveCardToDockPane',
    scope: command.scope,
    instanceId: command.instanceId,
    toPaneId: command.toPaneId,
    toIndex: command.toIndex,
  }
  return { next, inverse, changed: true }
}

/** Set a dock split's ratio (clamped 0.1..0.9). */
function resizeDockSplit(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'resizeDockSplit' }>,
  _ctx: ReduceContext,
): ReducerResult {
  const col = snapshot.columns[command.slotId]
  if (!col || !col.dock) return { next: snapshot, inverse: command, changed: false }
  const split = findDockSplit(col.dock, command.splitId)
  if (!split) return { next: snapshot, inverse: command, changed: false }
  const prevRatio = split.ratio
  const ratio = Math.max(0.1, Math.min(0.9, command.ratio))
  if (ratio === prevRatio) return { next: snapshot, inverse: command, changed: false }

  const next = bumpRevision(cloneSnapshot(snapshot))
  const s = findDockSplit(next.columns[command.slotId].dock!, command.splitId)!
  s.ratio = ratio

  const inverse: UieCommand = {
    type: 'resizeDockSplit',
    scope: command.scope,
    slotId: command.slotId,
    splitId: command.splitId,
    ratio: prevRatio,
  }
  return { next, inverse, changed: true }
}

function collapseColumn(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'collapseColumn' }>,
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

  const inverse: UieCommand = {
    type: 'collapseColumn',
    scope: command.scope,
    slotId: command.slotId,
    collapsed: !command.collapsed,
  }
  return { next, inverse, changed: true }
}

function updateCardGrid(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'updateCardGrid' }>,
  _ctx: ReduceContext,
): ReducerResult | null {
  const card = snapshot.cards[command.instanceId]
  if (!card) return { next: snapshot, inverse: command, changed: false }

  const prevGrid = { x: card.x, y: card.y, w: card.w, h: card.h }
  const next = bumpRevision(cloneSnapshot(snapshot))
  const target = next.cards[command.instanceId]

  if ('x' in command) {
    if (command.x === undefined) delete target.x
    else target.x = command.x
  }
  if ('y' in command) {
    if (command.y === undefined) delete target.y
    else target.y = command.y
  }
  if ('w' in command) {
    if (command.w === undefined) delete target.w
    else target.w = command.w
  }
  if ('h' in command) {
    if (command.h === undefined) delete target.h
    else target.h = command.h
  }

  const inverse: UieCommand = {
    type: 'updateCardGrid',
    scope: command.scope,
    instanceId: command.instanceId,
    ...prevGrid,
  }
  return { next, inverse, changed: true }
}

function applyPreset(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'applyPreset' }>,
  ctx: ReduceContext,
): ReducerResult {
  const prev = cloneSnapshot(snapshot)
  const next = bumpRevision(cloneSnapshot(prev))
  next.pageId = command.presetId as UiePageId
  // Preset content is applied by the caller via __restoreSnapshot of the built preset;
  // here we only flip the pageId so navigation focuses the right page.
  const inverse: UieCommand = {
    type: '__restoreSnapshot',
    scope: command.scope,
    snapshot: prev,
  }
  return { next, inverse, changed: next.pageId !== prev.pageId }
}

function resetScope(
  snapshot: UieLayoutSnapshot,
  command: Extract<UieCommand, { type: 'resetScope' }>,
  _ctx: ReduceContext,
): ReducerResult {
  const prev = cloneSnapshot(snapshot)
  const next = bumpRevision(cloneSnapshot(prev))
  const inverse: UieCommand = {
    type: '__restoreSnapshot',
    scope: command.scope,
    snapshot: prev,
  }
  return { next, inverse, changed: false }
}

/** Re-export helpers used by presets/repair to build a fresh snapshot. */
export function createEmptySnapshot(pageId: UiePageId, revision = 1): UieLayoutSnapshot {
  const order = [...PAGE_DEFAULT_ORDER] as UieSlotId[]
  const columns: Record<UieSlotId, UieColumn> = {
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
    edgeInset: EDGE_INSET,
  }
}

export { COLLAPSED_COLUMN_WIDTH }
