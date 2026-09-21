// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import {
  groupSearchLines,
  readFileText,
  searchedFilePaths,
  toDirEntryView,
  toWorkspaceRelative,
} from './workspaceSearch'

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
 * (list_directory, glob_search, grep_content) and `read_file` was called with
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

  it('stats through stat with the path key', async () => {
    await api.statEntry('C:/ws/demo', 'C:/ws/demo/src/main.ts')
    expect(url()).toContain('/api/v1/code/tools/stat/execute')
    expect(body()).toEqual({ cwd: 'C:/ws/demo', arguments: { path: 'C:/ws/demo/src/main.ts' } })
  })
})

/**
 * The read side of the same contract. These payloads are copied from a live stdio
 * session against TinadecTools.exe, not written by hand: Core hands the provider's JSON
 * to the renderer untouched, so a component that invents a key gets an empty panel with
 * a 200. Note the three traps in here — `ls` has no is_dir, `file_search` has no
 * matches and answers with absolute backslash paths, and read_file's line objects keep
 * the C# member names (`Content`, `LineNumber`).
 */
const CWD = 'C:/tmp/tina-probe'

describe('toDirEntryView', () => {
  it('reads the directory flag from the tool type string', () => {
    expect(toDirEntryView({ name: 'inner', path: 'inner', type: 'directory', size: 0, modified_at: '2026-09-21T12:27:07Z' })).toEqual({
      name: 'inner', isDir: true, size: 0, modifiedAt: '2026-09-21T12:27:07Z',
    })
    expect(toDirEntryView({ name: 'note.txt', type: 'file', size: 11 })).toEqual({
      name: 'note.txt', isDir: false, size: 11, modifiedAt: null,
    })
  })

  it('drops rows the tree cannot label', () => {
    expect(toDirEntryView({ type: 'file' })).toBeNull()
  })
})

describe('searchedFilePaths', () => {
  it('uses the tool\'s own hit-file map and makes it workspace-relative', () => {
    expect(searchedFilePaths({
      file_hashes: { 'C:\\tmp\\tina-probe\\note.txt': 'ZZWQ', 'C:\\tmp\\tina-probe\\inner\\deep.txt': 'ZQMM' },
    }, CWD)).toEqual(['note.txt', 'inner/deep.txt'])
  })

  it('accepts an absent payload', () => {
    expect(searchedFilePaths(undefined, CWD)).toEqual([])
  })
})

describe('groupSearchLines', () => {
  it('regroups the flat row list by file, keeping match and context rows apart', () => {
    const groups = groupSearchLines({
      lines: [
        { filepath: 'C:\\tmp\\tina-probe\\note.txt', line_number: 1, content: 'alpha', is_match: true },
        { filepath: 'C:\\tmp\\tina-probe\\note.txt', line_number: 2, content: 'beta', is_match: false },
      ],
      file_hashes: { 'C:\\tmp\\tina-probe\\note.txt': 'ZZWQ' },
      truncated: false,
      total_match_count: 1,
    }, CWD)
    expect(groups).toEqual([
      { path: 'note.txt', name: 'note.txt', lines: [
        { number: 1, text: 'alpha', isMatch: true },
        { number: 2, text: 'beta', isMatch: false },
      ] },
    ])
  })

  it('skips rows that carry no path or line number', () => {
    expect(groupSearchLines({ lines: [{ content: 'x' }, { filepath: 'a', content: 'y' }] }, CWD)).toEqual([])
  })
})

describe('readFileText', () => {
  it('joins the per-line entries the provider actually returns', () => {
    expect(readFileText({
      success: true,
      file_hash: 'ZZWQ',
      all_contents: [
        { content: { Content: 'alpha', LineNumber: 1, StartOffset: 0, EndOffset: 6 }, line_hash: '1|JN' },
        { content: { Content: 'beta', LineNumber: 2, StartOffset: 6, EndOffset: 11 }, line_hash: '2|NK' },
      ],
    })).toBe('alpha\nbeta')
  })

  it('answers with nothing for an empty or absent payload', () => {
    expect(readFileText({ success: true, file_hash: '', all_contents: [] })).toBe('')
    expect(readFileText(undefined)).toBe('')
  })
})

describe('toWorkspaceRelative', () => {
  it('leaves a path it cannot attribute to the root alone', () => {
    expect(toWorkspaceRelative('D:/other/file.ts', CWD)).toBe('D:/other/file.ts')
    expect(toWorkspaceRelative('./src/a.ts', CWD)).toBe('src/a.ts')
  })

  it('tolerates a trailing separator on the root', () => {
    expect(toWorkspaceRelative('C:\\tmp\\tina-probe\\note.txt', 'C:/tmp/tina-probe/')).toBe('note.txt')
  })
})
