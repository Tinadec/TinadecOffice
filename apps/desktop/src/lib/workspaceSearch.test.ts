// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { groupSearchLines, searchedFilePaths, toDirEntryView, stripSearchRoot } from './workspaceSearch'

const fetchMock = vi.fn()

beforeEach(() => {
  fetchMock.mockReset()
  fetchMock.mockResolvedValue({
    ok: true,
    status: 200,
    headers: { get: () => null },
    text: async () => '{"data":{"entries":[]}}',
  })
  vi.stubGlobal('fetch', fetchMock)
  vi.stubGlobal('navigator', { onLine: true })
})

function body(): Record<string, unknown> {
  const [, init] = fetchMock.mock.calls[0]!
  return JSON.parse(String((init as RequestInit).body)) as Record<string, unknown>
}

function url(): string {
  return String(fetchMock.mock.calls[0]![0])
}

/**
 * The ids and argument keys below are not style: Core answers 404 tool_not_found for
 * an id outside the TinadecTools manifest, and the tool ignores keys it does not
 * declare. Three of these wrappers used to name tools that do not exist
 * (`list_directory`, `glob_search`, `grep_content`) and `read_file` was called with
 * `path`/`start_line` while the tool declares `filepath`/`start_row`.
 */
describe('code tool wire contract', () => {
  it('reads through read_file with the manifest argument names', async () => {
    await api.readFile('C:/ws/demo', 'C:/ws/demo/src/main.ts', { start_row: 2, end_row: 9 })
    expect(url()).toContain('/api/v1/code/tools/read_file/execute')
    expect(body()).toEqual({
      cwd: 'C:/ws/demo',
      arguments: { filepath: 'C:/ws/demo/src/main.ts', start_row: 2, end_row: 9 },
    })
  })

  it('lists through ls', async () => {
    await api.listDirectory('C:/ws/demo', 'C:/ws/demo/src')
    expect(url()).toContain('/api/v1/code/tools/ls/execute')
    expect(body()).toEqual({ cwd: 'C:/ws/demo', arguments: { path: 'C:/ws/demo/src' } })
  })

  it('searches through file_search', async () => {
    await api.grepContent('C:/ws/demo', 'todo', { case_sensitive: true, max_results: 20, fixed_strings: true })
    expect(url()).toContain('/api/v1/code/tools/file_search/execute')
    expect(body()).toEqual({
      cwd: 'C:/ws/demo',
      arguments: { pattern: 'todo', case_sensitive: true, max_results: 20, fixed_strings: true },
    })
  })
})

/**
 * The read side of the same contract: Core hands the provider's JSON to the renderer
 * untouched, so a component that invents its own keys gets an empty panel with a 200.
 * `ls` classifies with a string `type` (no is_dir) and `file_search` answers with a
 * flat `lines` array plus a `file_hashes` map (no matches).
 */
describe('toDirEntryView', () => {
  it('reads the directory flag from the tool type string', () => {
    expect(toDirEntryView({ name: 'src', type: 'directory', size: 0 })).toEqual({
      name: 'src', isDir: true, size: 0, modifiedAt: null,
    })
    expect(toDirEntryView({ name: 'a.ts', type: 'file', size: 12, modified_at: 'x' })).toEqual({
      name: 'a.ts', isDir: false, size: 12, modifiedAt: 'x',
    })
  })

  it('drops rows the tree cannot label', () => {
    expect(toDirEntryView({ type: 'file' })).toBeNull()
  })
})

describe('searchedFilePaths', () => {
  it('uses the tool\'s own hit-file map, without the "./" ripgrep prefixes', () => {
    expect(searchedFilePaths({
      file_hashes: { './src/a.ts': 'h1', './src/b.ts': 'h2' },
      lines: [{ filepath: './src/a.ts' }],
    })).toEqual(['src/a.ts', 'src/b.ts'])
  })

  it('accepts an absent payload', () => {
    expect(searchedFilePaths(undefined)).toEqual([])
  })
})

describe('groupSearchLines', () => {
  it('regroups the flat row list by file, keeping match and context rows apart', () => {
    const groups = groupSearchLines({
      lines: [
        { filepath: './src/a.ts', line_number: 10, content: 'before', is_match: false },
        { filepath: './src/a.ts', line_number: 11, content: 'hit', is_match: true },
        { filepath: './src/b.ts', line_number: 3, content: 'other', is_match: true },
      ],
    })
    expect(groups).toEqual([
      { path: 'src/a.ts', name: 'a.ts', lines: [
        { number: 10, text: 'before', isMatch: false },
        { number: 11, text: 'hit', isMatch: true },
      ] },
      { path: 'src/b.ts', name: 'b.ts', lines: [{ number: 3, text: 'other', isMatch: true }] },
    ])
  })

  it('skips rows that carry no path or line number', () => {
    expect(groupSearchLines({ lines: [{ content: 'x' }, { filepath: 'a', content: 'y' }] })).toEqual([])
  })

  it('strips only a leading search-root prefix', () => {
    expect(stripSearchRoot('./src/a.ts')).toBe('src/a.ts')
    expect(stripSearchRoot('.\\src\\a.ts')).toBe('src\\a.ts')
    expect(stripSearchRoot('C:/ws/src/a.ts')).toBe('C:/ws/src/a.ts')
  })
})
