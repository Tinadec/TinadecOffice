import { api, type CodeToolExecuteResultDto } from '@/api'

export interface GitRef {
  name: string
  type: 'head' | 'branch' | 'tag' | 'remote' | string
  is_head: boolean
}

export interface GitGraphEdge {
  parent_hash: string
  from_lane: number
  to_lane: number
}

export interface GitGraphCommit {
  hash: string
  short_hash: string
  parents: string[]
  author: string
  author_email: string
  author_date: string
  committer_date: string
  subject: string
  refs: GitRef[]
  lane_index: number
  edges: GitGraphEdge[]
}

export interface GitFileChange {
  status: string
  score: number
  old_path: string | null
  new_path: string
  additions: number
  deletions: number
  is_binary: boolean
  blob_hash: string | null
  byte_size: number | null
}

export interface GitDiffLine {
  type: 'context' | 'add' | 'delete'
  old_line_number: number | null
  new_line_number: number | null
  content: string
}

export interface GitDiffHunk {
  old_start: number
  old_count: number
  new_start: number
  new_count: number
  lines: GitDiffLine[]
}

export interface GitPatchFile {
  old_path: string
  new_path: string
  status: string
  is_binary: boolean
  blob_hash?: string | null
  byte_size?: number | null
  hunks: GitDiffHunk[]
}

export interface GitLogListResponse {
  success: boolean
  error?: string | null
  commits: GitGraphCommit[]
  truncated: boolean
  next_cursor: string | null
}

export interface GitLogDetailResponse {
  success: boolean
  error?: string | null
  commits: GitGraphCommit[]
  files: GitFileChange[]
  patches?: GitPatchFile[] | null
  truncated: boolean
  truncation_reason?: string | null
  next_cursor?: string | null
}

export interface GitFileHistoryEntry {
  commit: GitGraphCommit
  change: GitFileChange
  patch?: GitPatchFile | null
}

export interface GitFileHistoryResponse {
  success: boolean
  error?: string | null
  entries: GitFileHistoryEntry[]
  truncated: boolean
  next_cursor: string | null
}

export interface GitBlameLine {
  commit: string
  author: string
  author_time?: string | null
  line_number: number
  content: string
}

export interface GitBlameResponse {
  success: boolean
  error?: string | null
  lines: GitBlameLine[]
  truncated: boolean
}

export interface GitFileAtRevisionResponse {
  success: boolean
  error?: string | null
  path?: string | null
  rev?: string | null
  content?: string | null
  is_binary?: boolean
  truncated?: boolean
}

export interface GitStashEntry {
  index: number
  ref_name: string
  hash?: string
  subject?: string
  branch?: string | null
  message: string
  created_at?: string | null
  author?: string | null
}

export interface GitStashListResponse {
  success: boolean
  error?: string | null
  entries: GitStashEntry[]
  truncated?: boolean
}

export interface GitStashShowResponse {
  success: boolean
  error?: string | null
  stash_ref?: string
  files?: GitFileChange[]
  patches?: GitPatchFile[] | null
  truncated?: boolean
}

type DynamicGitApi = typeof api

function unwrapToolData<T>(raw: unknown): T {
  if (raw && typeof raw === 'object' && 'data' in raw && (raw as CodeToolExecuteResultDto).data) {
    return (raw as CodeToolExecuteResultDto).data as T
  }
  return raw as T
}

export async function fetchGitLogList(
  cwd: string,
  opts: { revs?: string[]; limit?: number; after_commit?: string | null } = {},
): Promise<GitLogListResponse> {
  const dyn = api as DynamicGitApi
  const apiOpts = {
    repository_path: cwd,
    revs: opts.revs,
    limit: opts.limit,
    after_commit: opts.after_commit ?? undefined,
  }
  if (typeof dyn.gitLogList === 'function') {
    return unwrapToolData<GitLogListResponse>(await dyn.gitLogList(cwd, apiOpts))
  }
  const res = await api.executeCodeTool('git_log_list', {
    cwd,
    arguments: apiOpts,
  })
  const data = unwrapToolData<Partial<GitLogListResponse> & { commits?: unknown[] }>(res)
  if (Array.isArray(data.commits)) {
    return {
      success: data.success !== false,
      error: data.error ?? null,
      commits: data.commits as GitGraphCommit[],
      truncated: Boolean(data.truncated),
      next_cursor: data.next_cursor ?? null,
    }
  }
  const fallback = await api.executeCodeTool('git_worktree_manager', {
    cwd,
    arguments: { action: 'log', limit: opts.limit ?? 100, ...(opts.revs?.[0] ? { ref: opts.revs[0] } : {}) },
  })
  const fbData = unwrapToolData<{ commits?: Array<Record<string, unknown>> }>(fallback)
  const commits: GitGraphCommit[] = (fbData.commits ?? []).map((c) => ({
    hash: String(c.hash ?? ''),
    short_hash: String(c.short_hash ?? String(c.hash ?? '').slice(0, 7)),
    parents: Array.isArray(c.parents) ? (c.parents as string[]) : [],
    author: String(c.author ?? ''),
    author_email: String(c.email ?? c.author_email ?? ''),
    author_date: String(c.date ?? c.author_date ?? ''),
    committer_date: String(c.committer_date ?? c.date ?? ''),
    subject: String(c.subject ?? ''),
    refs: Array.isArray(c.refs) ? (c.refs as GitRef[]) : [],
    lane_index: typeof c.lane_index === 'number' ? c.lane_index : 0,
    edges: Array.isArray(c.edges) ? (c.edges as GitGraphEdge[]) : [],
  }))
  return { success: true, commits, truncated: false, next_cursor: null }
}

export async function fetchGitLogDetail(
  cwd: string,
  rev: string,
  opts: { include_patch?: boolean; max_files?: number } = {},
): Promise<GitLogDetailResponse> {
  const dyn = api as DynamicGitApi
  const apiOpts = { repository_path: cwd, include_patch: true, ...opts }
  if (typeof dyn.gitLogDetail === 'function') {
    return unwrapToolData<GitLogDetailResponse>(await dyn.gitLogDetail(cwd, rev, apiOpts))
  }
  const res = await api.executeCodeTool('git_log_detail', {
    cwd,
    arguments: { ...apiOpts, rev },
  })
  return unwrapToolData<GitLogDetailResponse>(res)
}

export async function fetchGitFileHistory(
  cwd: string,
  path: string,
  opts: { follow?: boolean; include_patch?: boolean; limit?: number; after_commit?: string | null } = {},
): Promise<GitFileHistoryResponse> {
  const dyn = api as DynamicGitApi
  const apiOpts = {
    repository_path: cwd,
    follow: opts.follow ?? true,
    include_patch: opts.include_patch ?? true,
    limit: opts.limit,
    after_commit: opts.after_commit ?? undefined,
  }
  if (typeof dyn.gitFileHistory === 'function') {
    return unwrapToolData<GitFileHistoryResponse>(await dyn.gitFileHistory(cwd, path, apiOpts))
  }
  const res = await api.executeCodeTool('git_file_history', {
    cwd,
    arguments: { ...apiOpts, path },
  })
  return unwrapToolData<GitFileHistoryResponse>(res)
}

export async function fetchGitBlame(
  cwd: string,
  path: string,
  opts: { rev?: string; start_line?: number; end_line?: number } = {},
): Promise<GitBlameResponse> {
  const dyn = api as DynamicGitApi
  const apiOpts = { repository_path: cwd, rev: opts.rev ?? 'HEAD', start_line: opts.start_line, end_line: opts.end_line }
  if (typeof dyn.gitBlame === 'function') {
    return unwrapToolData<GitBlameResponse>(await dyn.gitBlame(cwd, path, apiOpts))
  }
  const res = await api.executeCodeTool('git_blame', {
    cwd,
    arguments: { ...apiOpts, path },
  })
  return unwrapToolData<GitBlameResponse>(res)
}

export async function fetchGitFileAtRevision(
  cwd: string,
  path: string,
  rev = 'HEAD',
): Promise<GitFileAtRevisionResponse> {
  const dyn = api as DynamicGitApi
  if (typeof dyn.gitFileAtRevision === 'function') {
    return unwrapToolData<GitFileAtRevisionResponse>(await dyn.gitFileAtRevision(cwd, path, rev, { repository_path: cwd }))
  }
  const res = await api.executeCodeTool('git_file_at_revision', {
    cwd,
    arguments: { repository_path: cwd, path, rev },
  })
  return unwrapToolData<GitFileAtRevisionResponse>(res)
}

export async function fetchGitStashList(cwd: string): Promise<GitStashListResponse> {
  const dyn = api as DynamicGitApi
  if (typeof dyn.gitStashList === 'function') {
    return unwrapToolData<GitStashListResponse>(await dyn.gitStashList(cwd, { repository_path: cwd }))
  }
  const res = await api.executeCodeTool('git_stash_list', {
    cwd,
    arguments: { repository_path: cwd },
  })
  return unwrapToolData<GitStashListResponse>(res)
}

export async function fetchGitStashShow(cwd: string, stash_ref = 'stash@{0}'): Promise<GitStashShowResponse> {
  const dyn = api as DynamicGitApi
  if (typeof dyn.gitStashShow === 'function') {
    return unwrapToolData<GitStashShowResponse>(await dyn.gitStashShow(cwd, stash_ref, { repository_path: cwd, include_patch: true }))
  }
  const res = await api.executeCodeTool('git_stash_show', {
    cwd,
    arguments: { repository_path: cwd, stash_ref, include_patch: true },
  })
  return unwrapToolData<GitStashShowResponse>(res)
}

export function patchFileToUnifiedDiff(patch: GitPatchFile): string {
  const oldPath = patch.old_path || patch.new_path
  const newPath = patch.new_path || patch.old_path
  const header = [
    `diff --git a/${oldPath} b/${newPath}`,
    `--- a/${oldPath}`,
    `+++ b/${newPath}`,
  ]
  for (const hunk of patch.hunks ?? []) {
    header.push(`@@ -${hunk.old_start},${hunk.old_count} +${hunk.new_start},${hunk.new_count} @@`)
    for (const line of hunk.lines ?? []) {
      const prefix = line.type === 'add' ? '+' : line.type === 'delete' ? '-' : ' '
      header.push(`${prefix}${line.content}`)
    }
  }
  return `${header.join('\n')}\n`
}
