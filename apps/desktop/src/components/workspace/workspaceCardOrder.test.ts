import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { WorkspaceFileGrant, WorkspaceRoot } from '@/lib/workspaces'
import {
  WORKSPACE_ORDER_STORAGE_PREFIX,
  loadWorkspaceItemOrder,
  mergeReorderedVisibleKeys,
  reconcileWorkspaceItemOrder,
  reorderKeys,
  saveWorkspaceItemOrder,
} from './workspaceCardOrder'

describe('workspaceCardOrder', () => {
  const roots: WorkspaceRoot[] = [
    { id: 'r1', path: '/projects/alpha' },
    { id: 'r2', path: '/projects/beta' },
  ]
  const files: WorkspaceFileGrant[] = [
    { id: 'f1', path: '/docs/spec.pdf' },
    { id: 'f2', path: '/assets/cover.png' },
    { id: 'f3', path: '/src/index.ts' },
  ]

  beforeEach(() => {
    localStorage.clear()
  })

  afterEach(() => {
    vi.restoreAllMocks()
    localStorage.clear()
  })

  describe('reconcileWorkspaceItemOrder', () => {
    it('returns roots followed by files when savedOrderKeys is empty', () => {
      const items = reconcileWorkspaceItemOrder(roots, files, [])
      expect(items.map((item) => item.key)).toEqual([
        'root:r1',
        'root:r2',
        'file:f1',
        'file:f2',
        'file:f3',
      ])
      expect(items[0]).toEqual({
        key: 'root:r1',
        kind: 'root',
        id: 'r1',
        root: roots[0],
      })
      expect(items[2]).toEqual({
        key: 'file:f1',
        kind: 'file',
        id: 'f1',
        file: files[0],
      })
    })

    it('restores mixed root and file order, drops deleted orphans, deduplicates, and appends newly added items', () => {
      const saved = [
        'file:f2',
        'root:orphan',
        'root:r2',
        'file:f2',
        'file:missing',
        'file:f1',
      ]
      const items = reconcileWorkspaceItemOrder(roots, files, saved)
      expect(items.map((item) => item.key)).toEqual([
        'file:f2',
        'root:r2',
        'file:f1',
        'root:r1',
        'file:f3',
      ])
    })

    it('handles empty roots and files cleanly', () => {
      expect(reconcileWorkspaceItemOrder([], [], ['root:r1', 'file:f1'])).toEqual([])
    })
  })

  describe('reorderKeys', () => {
    it('moves a key forward and backward without mutating input', () => {
      const original = ['root:r1', 'file:f1', 'file:f2', 'root:r2']
      const movedRight = reorderKeys(original, 'root:r1', 2)
      expect(movedRight).toEqual(['file:f1', 'file:f2', 'root:r1', 'root:r2'])
      expect(original).toEqual(['root:r1', 'file:f1', 'file:f2', 'root:r2'])

      const movedLeft = reorderKeys(original, 'root:r2', 1)
      expect(movedLeft).toEqual(['root:r1', 'root:r2', 'file:f1', 'file:f2'])
    })

    it('clamps out-of-range target indices and ignores missing keys', () => {
      const keys = ['a', 'b', 'c']
      expect(reorderKeys(keys, 'c', -10)).toEqual(['c', 'a', 'b'])
      expect(reorderKeys(keys, 'a', 99)).toEqual(['b', 'c', 'a'])
      expect(reorderKeys(keys, 'unknown', 1)).toEqual(['a', 'b', 'c'])
      expect(reorderKeys(keys, 'b', 1)).toEqual(['a', 'b', 'c'])
      expect(reorderKeys(['solo'], 'solo', 0)).toEqual(['solo'])
      expect(reorderKeys(keys, 'a', Number.NaN)).toEqual(['a', 'b', 'c'])
    })
  })

  describe('mergeReorderedVisibleKeys', () => {
    it('replaces top-6 visible prefix while preserving trailing >6 items in order', () => {
      const allKeys = [
        'root:r1',
        'file:f1',
        'file:f2',
        'root:r2',
        'file:f3',
        'file:f4',
        'file:f5',
        'root:r3',
      ]
      const reorderedVisible = [
        'file:f3',
        'root:r1',
        'file:f1',
        'file:f2',
        'file:f4',
        'root:r2',
      ]

      expect(mergeReorderedVisibleKeys(allKeys, reorderedVisible)).toEqual([
        'file:f3',
        'root:r1',
        'file:f1',
        'file:f2',
        'file:f4',
        'root:r2',
        'file:f5',
        'root:r3',
      ])
    })

    it('ignores stale or placeholder keys not present in allKeys', () => {
      const allKeys = ['root:r1', 'file:f1', 'file:f2']
      expect(
        mergeReorderedVisibleKeys(allKeys, ['empty', 'file:f2', 'preview-1', 'root:r1']),
      ).toEqual(['file:f2', 'root:r1', 'file:f1'])
    })
  })

  describe('loadWorkspaceItemOrder & saveWorkspaceItemOrder', () => {
    it('round-trips ordered keys per projectId using exact storage prefix', () => {
      saveWorkspaceItemOrder('proj-1', ['file:f2', 'root:r1', 'file:f1', 'file:f2'])
      saveWorkspaceItemOrder('proj-2', ['root:r2'])

      expect(localStorage.getItem(`${WORKSPACE_ORDER_STORAGE_PREFIX}proj-1`)).toBe(
        JSON.stringify(['file:f2', 'root:r1', 'file:f1']),
      )
      expect(loadWorkspaceItemOrder('proj-1')).toEqual(['file:f2', 'root:r1', 'file:f1'])
      expect(loadWorkspaceItemOrder('proj-2')).toEqual(['root:r2'])
    })

    it('returns empty array for blank projectId, malformed JSON, or non-string entries', () => {
      expect(loadWorkspaceItemOrder(null)).toEqual([])
      expect(loadWorkspaceItemOrder(undefined)).toEqual([])
      expect(loadWorkspaceItemOrder('   ')).toEqual([])

      localStorage.setItem(`${WORKSPACE_ORDER_STORAGE_PREFIX}bad-json`, '{broken')
      expect(loadWorkspaceItemOrder('bad-json')).toEqual([])

      localStorage.setItem(`${WORKSPACE_ORDER_STORAGE_PREFIX}bad-obj`, JSON.stringify({ a: 1 }))
      expect(loadWorkspaceItemOrder('bad-obj')).toEqual([])

      localStorage.setItem(
        `${WORKSPACE_ORDER_STORAGE_PREFIX}mixed`,
        JSON.stringify(['root:r1', 42, '', null, 'file:f1']),
      )
      expect(loadWorkspaceItemOrder('mixed')).toEqual(['root:r1', 'file:f1'])
    })

    it('swallows localStorage exceptions gracefully', () => {
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
        throw new Error('SecurityError')
      })
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
        throw new Error('QuotaExceededError')
      })

      expect(loadWorkspaceItemOrder('proj-err')).toEqual([])
      expect(() => saveWorkspaceItemOrder('proj-err', ['root:r1'])).not.toThrow()
    })
  })
})
