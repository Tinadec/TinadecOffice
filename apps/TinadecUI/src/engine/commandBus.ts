import type { UieCommand, UieCommandEnvelope } from './commands'
import type { UieLayoutSnapshot, UieSlotId } from './types'
import type { CardRegistry } from './registry'
import { reduce, type ReduceContext } from './reducer'
import { createUndoStack, type UndoStack } from './undoStack'

// ---------------------------------------------------------------------------
// Command bus.
//
// Single entry point for all layout mutations. Validates source + revision,
// runs the reducer, records undo/redo (coalescing drag gestures via gestureId),
// and notifies listeners. Persistence is handled by the caller subscribing.
// ---------------------------------------------------------------------------

export interface CommandBusOptions {
  registry: CardRegistry
  /** Called after a successful mutation with the new snapshot. */
  onChanged?: (snapshot: UieLayoutSnapshot) => void
  /** Called when a command was rejected. */
  onRejected?: (envelope: UieCommandEnvelope, reason: string) => void
  /** Locked slots (settings) that reject mutations. */
  lockedSlots?: ReadonlySet<UieSlotId> | readonly UieSlotId[]
  nextInstanceId?: () => string
  /** Factory for new dock pane ids. */
  nextDockId?: () => string
  /** Factory for new dock split ids. */
  nextDockSplitId?: () => string
}

export interface CommandBus {
  /** Dispatch a command envelope; returns true when accepted & applied. */
  dispatch(envelope: UieCommandEnvelope, options?: { gestureId?: string }): boolean
  /** Dispatch an internal command (reducer/undo only). */
  dispatchInternal(command: UieCommand, expectedRevision: number): boolean
  undo(): UieLayoutSnapshot | undefined
  redo(): UieLayoutSnapshot | undefined
  getSnapshot(): UieLayoutSnapshot
  setSnapshot(snapshot: UieLayoutSnapshot): void
  canUndo(): boolean
  canRedo(): boolean
}

let counter = 0
function defaultNextInstanceId(): string {
  return `wb-instance-${++counter}-${Math.random().toString(36).slice(2, 8)}`
}

let dockCounter = 0
function defaultNextDockId(): string {
  return `dock-pane-${++dockCounter}`
}
function defaultNextDockSplitId(): string {
  return `dock-split-${++dockCounter}`
}

export function createCommandBus(
  initial: UieLayoutSnapshot,
  options: CommandBusOptions,
): CommandBus {
  let snapshot = initial
  const undoStack: UndoStack = createUndoStack()
  const { registry, onChanged, onRejected } = options
  const lockedSlots = new Set(options.lockedSlots ?? [])

  const reduceCtx: ReduceContext = {
    registry,
    nextInstanceId: options.nextInstanceId ?? defaultNextInstanceId,
    nextDockId: options.nextDockId ?? defaultNextDockId,
    nextDockSplitId: options.nextDockSplitId ?? defaultNextDockSplitId,
    lockedSlots,
  }

  function applyEnvelope(
    envelope: UieCommandEnvelope,
    gestureId?: string,
  ): boolean {
    const outcome = reduce(snapshot, envelope, reduceCtx)
    if (!outcome.ok) {
      onRejected?.(envelope, outcome.error.message)
      return false
    }
    const { result } = outcome
    const record = {
      id: gestureId ?? envelope.command.type,
      inverse: {
        command: result.inverse,
        source: envelope.source,
        expectedRevision: snapshot.revision,
      },
      redo: envelope,
      before: snapshot,
      after: result.next,
      gesture: !!gestureId,
    }
    undoStack.push(record)
    snapshot = result.next
    onChanged?.(snapshot)
    return true
  }

  return {
    dispatch(envelope, options) {
      return applyEnvelope(envelope, options?.gestureId)
    },
    dispatchInternal(command, expectedRevision) {
      return applyEnvelope({
        command,
        source: 'restore',
        expectedRevision,
      })
    },
    undo() {
      const record = undoStack.popUndo()
      if (!record) return undefined
      // Undo = restore the pre-mutation snapshot directly (pure, no replay).
      snapshot = structuredClone(record.before)
      undoStack.pushRedo(record)
      onChanged?.(snapshot)
      return snapshot
    },
    redo() {
      const record = undoStack.popRedo()
      if (!record) return undefined
      snapshot = structuredClone(record.after)
      onChanged?.(snapshot)
      return snapshot
    },
    getSnapshot() {
      return snapshot
    },
    setSnapshot(next) {
      snapshot = next
      undoStack.clearRedo()
      onChanged?.(snapshot)
    },
    canUndo() {
      return undoStack.undoCount() > 0
    },
    canRedo() {
      return undoStack.redoCount() > 0
    },
  }
}
