import type { WorkbenchCommandEnvelope } from './commands'
import type { WorkbenchLayoutSnapshot } from './types'
import { UNDO_LIMIT } from './types'

// ---------------------------------------------------------------------------
// Undo/redo stack.
//
// Each record stores the before/after snapshots plus the inverse (undo) and the
// redo (original) envelopes. Consecutive drag/resize commands sharing a
// `gestureId` are coalesced into a single record (first `before`, last `after`).
// ---------------------------------------------------------------------------

export interface UndoRecord {
  id: string
  inverse: WorkbenchCommandEnvelope
  redo: WorkbenchCommandEnvelope
  before: WorkbenchLayoutSnapshot
  after: WorkbenchLayoutSnapshot
  /** True when this record coalesces a drag gesture. */
  gesture: boolean
}

export interface UndoStack {
  /** Push a record; coalesces when `gestureId` matches the last record. */
  push(record: UndoRecord): void
  /** Pop the top undo record (for undo). */
  popUndo(): UndoRecord | undefined
  /** Pop the top redo record (for redo). */
  popRedo(): UndoRecord | undefined
  /** Push a record back onto the redo stack (after an undo). */
  pushRedo(record: UndoRecord): void
  /** Clear redo entries (after a fresh mutation). */
  clearRedo(): void
  undoCount(): number
  redoCount(): number
}

export function createUndoStack(limit: number = UNDO_LIMIT): UndoStack {
  let undo: UndoRecord[] = []
  let redo: UndoRecord[] = []

  return {
    push(record) {
      // If this record is a continuation of the previous gesture, replace the
      // previous record's `after` with this one's.
      if (record.gesture && undo.length > 0) {
        const last = undo[undo.length - 1]
        if (last.gesture && last.id === record.id) {
          last.after = record.after
          last.redo = record.redo
          return
        }
      }
      undo.push(record)
      if (undo.length > limit) undo = undo.slice(undo.length - limit)
      redo = []
    },
    popUndo() {
      return undo.pop()
    },
    popRedo() {
      return redo.pop()
    },
    pushRedo(record) {
      redo.push(record)
      if (redo.length > limit) redo = redo.slice(redo.length - limit)
    },
    clearRedo() {
      redo = []
    },
    undoCount() {
      return undo.length
    },
    redoCount() {
      return redo.length
    },
  }
}
