import { describe, it, expect } from 'vitest'
import { createUndoStack, type UndoRecord } from './undoStack'
import type { WorkbenchLayoutSnapshot } from './types'
import { createEmptySnapshot } from './reducer'

function makeRecord(id: string, rev: number, gesture = false): UndoRecord {
  const snap = (r: number): WorkbenchLayoutSnapshot => ({ ...createEmptySnapshot('home'), revision: r })
  const before = snap(rev)
  const after = snap(rev + 1)
  return {
    id,
    inverse: { command: { type: 'closeCard', scope: { kind: 'page', pageId: 'home' }, instanceId: 'x' }, source: 'user', expectedRevision: rev },
    redo: { command: { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'git' }, source: 'user', expectedRevision: rev },
    before,
    after,
    gesture,
  }
}

describe('undo stack', () => {
  it('enforces the 50-record limit (FIFO)', () => {
    const stack = createUndoStack(50)
    for (let i = 0; i < 60; i++) stack.push(makeRecord(`r${i}`, i))
    expect(stack.undoCount()).toBe(50)
  })

  it('coalesces consecutive drag resizes sharing a gesture id', () => {
    const stack = createUndoStack(50)
    stack.push(makeRecord('g1', 1, true))
    stack.push(makeRecord('g1', 2, true))
    stack.push(makeRecord('g1', 3, true))
    expect(stack.undoCount()).toBe(1)
  })

  it('does not coalesce different gesture ids', () => {
    const stack = createUndoStack(50)
    stack.push(makeRecord('g1', 1, true))
    stack.push(makeRecord('g2', 2, true))
    expect(stack.undoCount()).toBe(2)
  })

  it('undo pops and redo pushes back', () => {
    const stack = createUndoStack(50)
    stack.push(makeRecord('a', 1))
    const popped = stack.popUndo()
    expect(popped?.id).toBe('a')
    stack.pushRedo(popped!)
    expect(stack.redoCount()).toBe(1)
    expect(stack.undoCount()).toBe(0)
  })

  it('clearRedo empties the redo stack', () => {
    const stack = createUndoStack(50)
    const rec = makeRecord('a', 1)
    stack.push(rec)
    stack.popUndo()
    stack.pushRedo(rec)
    expect(stack.redoCount()).toBe(1)
    stack.clearRedo()
    expect(stack.redoCount()).toBe(0)
  })
})
