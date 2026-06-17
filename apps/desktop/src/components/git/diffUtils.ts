import { parseUnifiedDiff, type GitDiffFile } from '@/gitDiffParser'

export interface DiffFileEntry {
  path: string
  previousPath?: string | null
  diffText?: string | null
  originalContent?: string | null
  modifiedContent?: string | null
  additions?: number
  deletions?: number
  binary?: boolean
  truncated?: boolean
  changeType?: string
}

const EXT_LANG_MAP: Record<string, string> = {
  ts: 'typescript',
  tsx: 'typescript',
  mts: 'typescript',
  cts: 'typescript',
  js: 'javascript',
  jsx: 'javascript',
  mjs: 'javascript',
  cjs: 'javascript',
  json: 'json',
  jsonc: 'json',
  vue: 'html',
  html: 'html',
  htm: 'html',
  xhtml: 'html',
  css: 'css',
  scss: 'scss',
  sass: 'scss',
  less: 'less',
  md: 'markdown',
  markdown: 'markdown',
  py: 'python',
  pyw: 'python',
  rb: 'ruby',
  go: 'go',
  rs: 'rust',
  java: 'java',
  kt: 'kotlin',
  kts: 'kotlin',
  c: 'c',
  h: 'c',
  cpp: 'cpp',
  cc: 'cpp',
  cxx: 'cpp',
  hpp: 'cpp',
  hh: 'cpp',
  cs: 'csharp',
  php: 'php',
  swift: 'swift',
  sh: 'shell',
  bash: 'shell',
  zsh: 'shell',
  fish: 'shell',
  yml: 'yaml',
  yaml: 'yaml',
  toml: 'ini',
  ini: 'ini',
  cfg: 'ini',
  conf: 'ini',
  xml: 'xml',
  svg: 'xml',
  sql: 'sql',
  lua: 'lua',
  dart: 'dart',
  r: 'r',
  R: 'r',
  pl: 'perl',
  pm: 'perl',
  graphql: 'graphql',
  gql: 'graphql',
  dockerfile: 'dockerfile'
}

export function detectLanguage(filePath?: string | null): string {
  if (!filePath) return 'plaintext'
  const base = filePath.split(/[\\/]/).pop() ?? filePath
  const lower = base.toLowerCase()
  if (lower === 'dockerfile' || lower.startsWith('dockerfile.')) return 'dockerfile'
  if (lower === 'makefile' || lower === 'gnumakefile') return 'makefile'
  if (lower === '.gitignore' || lower === '.gitattributes') return 'ini'
  const ext = lower.includes('.') ? lower.split('.').pop() ?? '' : ''
  return EXT_LANG_MAP[ext] ?? 'plaintext'
}

/**
 * Reconstruct approximate original/modified text from a parsed file's hunks.
 * Only the changed regions (with context) are reconstructed; this is enough
 * for Monaco's DiffEditor to render a meaningful side-by-side view.
 */
export function reconstructFromHunks(file: GitDiffFile): { original: string; modified: string } {
  const originalLines: string[] = []
  const modifiedLines: string[] = []
  for (const hunk of file.hunks) {
    for (const line of hunk.lines) {
      if (line.change === 'context') {
        originalLines.push(line.content)
        modifiedLines.push(line.content)
      } else if (line.change === 'add') {
        modifiedLines.push(line.content)
      } else if (line.change === 'delete') {
        originalLines.push(line.content)
      }
    }
  }
  return { original: originalLines.join('\n'), modified: modifiedLines.join('\n') }
}

/**
 * Build DiffFileEntry[] from a unified diff text plus optional file summaries
 * (carrying additions/deletions/binary flags). Per-file original/modified
 * content is reconstructed from the parsed hunks.
 */
export function buildDiffEntries(
  diffText: string | null | undefined,
  summaries?: Array<{
    path: string
    previous_path?: string | null
    change_type?: string
    additions?: number
    deletions?: number
    binary?: boolean
    truncated?: boolean
  }>
): DiffFileEntry[] {
  if (!diffText) return []
  const parsed = parseUnifiedDiff(diffText)
  type SummaryEntry = NonNullable<typeof summaries>[number]
  const summaryMap = new Map<string, SummaryEntry>()
  for (const summary of summaries ?? []) {
    summaryMap.set(summary.path, summary)
  }
  return parsed.files.map((file) => {
    const summary = summaryMap.get(file.path)
    const { original, modified } = reconstructFromHunks(file)
    return {
      path: file.path,
      previousPath: file.previous_path,
      diffText: null,
      originalContent: original,
      modifiedContent: modified,
      additions: summary?.additions,
      deletions: summary?.deletions,
      binary: summary?.binary ?? file.binary,
      truncated: summary?.truncated,
      changeType: summary?.change_type ?? file.change_type
    }
  })
}

export function summarizeEntries(entries: DiffFileEntry[]): { additions: number; deletions: number } {
  let additions = 0
  let deletions = 0
  for (const entry of entries) {
    additions += entry.additions ?? 0
    deletions += entry.deletions ?? 0
  }
  return { additions, deletions }
}
