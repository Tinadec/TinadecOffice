/**
 * Read-side adapters for the TinadecTools file/search responses.
 *
 * Core forwards the provider's JSON verbatim into `CodeToolExecuteResultDto.data`
 * (DirectToolEndpoints.cs:89, `response.Result?.Clone()`), so the keys below are the
 * TinadecTools contracts and nothing else:
 * - `ls`          → FileSystemTools.cs:35 `entries[].{name,path,type,size,modified_at}`
 * - `file_search` → FileSearch.cs:87 `lines[]`, :91 `file_hashes`
 *
 * They carry no `is_dir` and no `matches`: guessing either key yields an empty panel
 * with a 200 response, which is why the tree and both search boxes looked wired but
 * showed nothing.
 */

export interface DirEntryDto {
  name?: string
  path?: string
  /** 'directory' | 'file' | 'link' (FileSystemTools.cs:188) */
  type?: string
  size?: number | null
  modified_at?: string | null
}

export interface DirEntryView {
  name: string
  isDir: boolean
  size: number | null
  modifiedAt: string | null
}

export function toDirEntryView(entry: DirEntryDto): DirEntryView | null {
  if (!entry.name) return null
  return {
    name: entry.name,
    isDir: entry.type === 'directory',
    size: typeof entry.size === 'number' ? entry.size : null,
    modifiedAt: typeof entry.modified_at === 'string' ? entry.modified_at : null,
  }
}

export interface FileSearchLineDto {
  filepath?: string
  line_number?: number
  content?: string
  is_match?: boolean
}

export interface FileSearchDataDto {
  lines?: FileSearchLineDto[]
  /** Hit file path → its current hash; the tool already deduplicated the file list. */
  file_hashes?: Record<string, string>
  truncated?: boolean
  total_match_count?: number
}

export function searchedFilePaths(data: FileSearchDataDto | undefined): string[] {
  // `path` defaults to "." in the tool, so ripgrep echoes results prefixed with it.
  return Object.keys(data?.file_hashes ?? {}).map(stripSearchRoot)
}

export function stripSearchRoot(path: string): string {
  return path.replace(/^\.[\\/]/, '')
}

export interface SearchGroup {
  path: string
  name: string
  lines: Array<{ number: number; text: string; isMatch: boolean }>
}

/**
 * `file_search` answers with one flat row per line — match rows and their context
 * rows interleaved, sorted by path then line. The panel wants files with their hits,
 * so the regrouping lives here rather than in the template.
 */
export function groupSearchLines(data: FileSearchDataDto | undefined): SearchGroup[] {
  const byPath = new Map<string, SearchGroup>()
  for (const line of data?.lines ?? []) {
    const path = line.filepath ? stripSearchRoot(line.filepath) : undefined
    const number = line.line_number
    if (!path || typeof number !== 'number') continue
    let group = byPath.get(path)
    if (!group) {
      group = { path, name: path.split(/[\\/]/).pop() ?? path, lines: [] }
      byPath.set(path, group)
    }
    group.lines.push({ number, text: line.content ?? '', isMatch: line.is_match === true })
  }
  return [...byPath.values()]
}
