import { describe, expect, it } from 'vitest'
import { foldSummary, isFoldableKind, keepsOwnLine, verbKeyFor } from './toolSentence'
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

  // Keys, not words: the label has to follow the reader's language, so this module only decides
  // WHICH verb applies and the component resolves it.
  it('switches tense with the step status', () => {
    expect(verbKeyFor('read_file', 'running')).toBe('agent.toolVerb.reading')
    expect(verbKeyFor('read_file', 'completed')).toBe('agent.toolVerb.read')
    expect(verbKeyFor('shell', 'pending')).toBe('agent.toolVerb.running')
    expect(verbKeyFor('shell', 'completed')).toBe('agent.toolVerb.ran')
  })

  it('falls back rather than throwing on an unknown tool id', () => {
    expect(verbKeyFor('some_future_tool', 'completed')).toBe('agent.toolVerb.work')
    expect(isFoldableKind(toolKindOf('some_future_tool'))).toBe(false)
  })

  it('words a folded run after the kind that dominates it', () => {
    expect(foldSummary([toolKindOf('read_file'), toolKindOf('read_file'), toolKindOf('ls')]))
      .toEqual({ steps: 3, labelKey: 'agent.toolVerb.read' })
    expect(foldSummary([toolKindOf('file_search'), toolKindOf('file_search'), toolKindOf('read_file')]))
      .toEqual({ steps: 3, labelKey: 'agent.toolVerb.search' })
  })

  it('reports a one-step run as itself', () => {
    expect(foldSummary([toolKindOf('read_file')])).toEqual({ steps: 1, labelKey: 'agent.toolVerb.read' })
  })
})
