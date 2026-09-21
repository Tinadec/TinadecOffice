/**
 * Read-side adapters for the TinadecTools file/search responses.
 *
 * Core forwards the provider's JSON verbatim into `CodeToolExecuteResultDto.data`
 * (DirectToolEndpoints.cs:89, `response.Result?.Clone()`), so the keys below are the
 * provider's and nothing else. They were taken from a live stdio session against
 * TinadecTools.exe, not inferred:
 * - `ls`          → `entries[].{name,path,type,size,modified_at}`; `path` is relative
 *                   to the workspace root, `type` is `directory`|`file`|`link`.
 * - `file_search` → flat `lines[]` + `file_hashes`; `filepath` comes back **absolute
 *                   with backslashes** on Windows.
 * - `read_file`   → `file_hash` + `all_contents[].{content,line_hash}`, where the
 *                   nested `content` keeps the C# member names (`Content`,
 *                   `LineNumber`, …) because `LineContent` has no [JsonPropertyName]
 *                   and its generator sets no naming policy.
 *
 * Inventing a key here is invisible: the call answers 200 with an empty panel.
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

export function searchedFilePaths(data: FileSearchDataDto | undefined, cwd: string): string[] {
  // The tool already deduplicated the hit files, so its own map keys are the list.
  return Object.keys(data?.file_hashes ?? {}).map((path) => toWorkspaceRelative(path, cwd))
}

/**
 * `file_search` answers with absolute, platform-separated paths while the tree, the
 * editor tabs and `ls` all speak workspace-relative forward slashes. Convert with the
 * root the caller already has; a path that does not start with it is left alone rather
 * than guessed at.
 */
export function toWorkspaceRelative(path: string, cwd: string): string {
  const normalized = path.replace(/\\/g, '/')
  const root = cwd.replace(/\\/g, '/').replace(/\/+$/, '')
  const prefix = `${root}/`
  const relative = normalized.startsWith(prefix)
    ? normalized.slice(prefix.length)
    : normalized.replace(/^\.\//, '')
  return relative.length > 0 ? relative : normalized
}

export interface ReadFileDataDto {
  success?: boolean
  file_hash?: string
  /** One entry per line. `content` carries the provider's C# member names verbatim. */
  all_contents?: Array<{
    content?: { Content?: string; LineNumber?: number; StartOffset?: number; EndOffset?: number }
    line_hash?: string
  }>
}

/**
 * `read_file` has no single content field: it answers with one entry per line, and the
 * nested line object keeps the C# member names (`Content`), because `LineContent` is a
 * positional record struct with no [JsonPropertyName]. The file's trailing newline is
 * not in the payload at all, so a save round-trip can drop it — that is the provider's
 * limit, not something the client can restore.
 */
export function readFileText(data: ReadFileDataDto | undefined): string {
  return (data?.all_contents ?? []).map((line) => line.content?.Content ?? '').join('\n')
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
export function groupSearchLines(data: FileSearchDataDto | undefined, cwd: string): SearchGroup[] {
  const byPath = new Map<string, SearchGroup>()
  for (const line of data?.lines ?? []) {
    const path = line.filepath ? toWorkspaceRelative(line.filepath, cwd) : undefined
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
