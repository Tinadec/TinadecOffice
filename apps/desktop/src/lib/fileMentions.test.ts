import { describe, expect, it } from 'vitest'
import { completeMentionToken, filterMentionEntries, parseMentionToken } from './fileMentions'
import type { DirEntryView } from './workspaceSearch'

function entry(name: string, isDir = false): DirEntryView {
  return { name, isDir, size: null, modifiedAt: null }
}

describe('parseMentionToken', () => {
  it('starts a mention at the beginning of the draft', () => {
    expect(parseMentionToken('@src', 4)).toEqual({ from: 0, directory: '', query: 'src' })
  })

  it('starts after whitespace and stops at the caret', () => {
    const text = 'look at @src/main'
    expect(parseMentionToken(text, text.length)).toEqual({
      from: 8, directory: 'src/', query: 'main',
    })
  })

  it('treats a trailing slash as the directory being listed', () => {
    expect(parseMentionToken('@apps/desktop/', 14)).toEqual({
      from: 0, directory: 'apps/desktop/', query: '',
    })
  })

  it('ignores an @ inside a word — emails, scopes and identifiers are not paths', () => {
    expect(parseMentionToken('mail me@a.ts', 12)).toBeNull()
  })

  it('ends the mention once whitespace follows the fragment', () => {
    expect(parseMentionToken('@src/main and more', 10)).toBeNull()
  })

  it('returns nothing when there is no @ before the caret', () => {
    expect(parseMentionToken('no mention here', 15)).toBeNull()
  })
})

describe('filterMentionEntries', () => {
  const entries = [entry('README.md'), entry('src', true), entry('package.json'), entry('tests', true)]

  it('prefers prefix hits over substring hits, directories and files alike', () => {
    expect(filterMentionEntries(entries, 're').map((e) => e.name)).toEqual(['README.md'])
    expect(filterMentionEntries(entries, 'est').map((e) => e.name)).toEqual(['tests'])
  })

  it('lists everything when nothing has been typed yet, directories included', () => {
    expect(filterMentionEntries(entries, '').map((e) => e.name))
      .toEqual(['package.json', 'README.md', 'src', 'tests'])
  })

  it('caps the list so the menu stays scannable', () => {
    const many = Array.from({ length: 30 }, (_, i) => entry(`f${i}.ts`))
    expect(filterMentionEntries(many, 'f', 8)).toHaveLength(8)
  })
})

describe('completeMentionToken', () => {
  it('leaves a directory open for the next segment', () => {
    const text = 'fix @src/'
    const result = completeMentionToken(text, text.length, parseMentionToken(text, text.length)!, entry('components', true))
    expect(result.text).toBe('fix @src/components/')
    expect(result.caret).toBe(result.text.length)
  })

  it('finishes a file and puts a space after it', () => {
    const text = 'read @src/mai'
    const result = completeMentionToken(text, text.length, parseMentionToken(text, text.length)!, entry('main.ts'))
    expect(result.text).toBe('read @src/main.ts ')
  })

  it('replaces only the fragment and keeps an existing space', () => {
    const text = 'compare @src/ and more'
    const result = completeMentionToken(text, 13, parseMentionToken(text, 13)!, entry('main.ts'))
    expect(result.text).toBe('compare @src/main.ts and more')
  })
})
