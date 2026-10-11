import { describe, expect, it } from 'vitest'
import { foldSummary, isFoldableKind, keepsOwnLine, verbFor } from './toolSentence'
import { toolKindOf } from './toolPresentation'

describe('tool sentence', () => {
  it('folds the quiet kinds and only the quiet kinds', () => {
    expect(isFoldableKind(toolKindOf('read_file'))).toBe(true)
    expect(isFoldableKind(toolKindOf('ls'))).toBe(true)
    expect(isFoldableKind(toolKindOf('file_search'))).toBe(true)
    // Mutating and deciding work keeps its own line.
    expect(isFoldableKind(toolKindOf('write_file'))).toBe(false)
    expect(isFoldableKind(toolKindOf('shell'))).toBe(false)
    expect(isFoldableKind(toolKindOf('git_commit'))).toBe(false)
  })

  it('gives a failed or parked step its own line whatever its kind', () => {
    expect(keepsOwnLine(toolKindOf('read_file'), 'completed')).toBe(false)
    expect(keepsOwnLine(toolKindOf('read_file'), 'failed')).toBe(true)
    expect(keepsOwnLine(toolKindOf('read_file'), 'waiting_approval')).toBe(true)
    expect(keepsOwnLine(toolKindOf('write_file'), 'completed')).toBe(true)
  })

  it('switches tense with the step status', () => {
    expect(verbFor('read_file', 'running')).toBe('正在读取')
    expect(verbFor('read_file', 'completed')).toBe('读取')
    expect(verbFor('shell', 'pending')).toBe('正在运行')
    expect(verbFor('shell', 'completed')).toBe('运行')
  })

  it('falls back rather than throwing on an unknown tool id', () => {
    expect(verbFor('some_future_tool', 'completed')).toBe('执行')
    expect(isFoldableKind(toolKindOf('some_future_tool'))).toBe(false)
  })

  it('words a folded run after the kind that dominates it', () => {
    expect(foldSummary([toolKindOf('read_file'), toolKindOf('read_file'), toolKindOf('ls')]))
      .toEqual({ steps: 3, label: '读取' })
    expect(foldSummary([toolKindOf('file_search'), toolKindOf('file_search'), toolKindOf('read_file')]))
      .toEqual({ steps: 3, label: '搜索' })
  })

  it('reports a one-step run as itself', () => {
    expect(foldSummary([toolKindOf('read_file')])).toEqual({ steps: 1, label: '读取' })
  })
})
