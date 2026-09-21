import type { Component } from 'vue'
import {
  FileCode2,
  FileText,
  Folder,
  GitBranch,
  MessagesSquare,
  Package,
  Search,
  Shield,
  Terminal,
  Workflow,
  Wrench
} from '@lucide/vue'

/**
 * The one place that knows how a tool id looks and reads.
 *
 * Why this exists: four components each carried their own copy of an id→icon chain, and
 * every copy branched on ids that were never in the provider (list_directory,
 * glob_search, grep_content, apply_patch, code_editor, sandbox_exec). A
 * hardcoded branch on an id that does not exist is worse than no branch: it looks
 * reviewed, keeps its imports alive, and silently routes every real tool to the fallback.
 *
 * The id sets below are not transcribed from a document. They are the output of a live
 * stdio session against the built provider (`tool_id: "#manifest"`, protocol v2,
 * manifest_hash `aab13ada12a6ef0385ddd7b8faf2f68fb50fd7874a50e91d611fbd9ad4712713`,
 * 49 tools) plus Core's own declarations, cited per constant. `toolPresentation.test.ts`
 * pins both directions: every id this module branches on must be in a measured set, and
 * the six invented ids must never reappear as quoted tool ids anywhere in `src/`.
 */

/** One entry per tool the TinadecTools child process actually advertises. */
export const PROVIDER_TOOL_IDS = [
  'command_run',
  'delete_bytes',
  'delete_line',
  'file_search',
  'git_blame',
  'git_branch_create',
  'git_branch_delete',
  'git_branch_list',
  'git_branch_rename',
  'git_checkout',
  'git_commit',
  'git_conflict_preview',
  'git_conflict_resolve',
  'git_diff',
  'git_discard',
  'git_fetch',
  'git_file_at_revision',
  'git_file_history',
  'git_log',
  'git_log_detail',
  'git_log_list',
  'git_merge',
  'git_pull',
  'git_push',
  'git_push_readiness',
  'git_rebase',
  'git_ref_list',
  'git_remote_list',
  'git_stage',
  'git_status',
  'git_unstage',
  'git_worktree_create',
  'git_worktree_list',
  'git_worktree_remove',
  'insert_byte',
  'insert_bytes',
  'insert_line',
  'ls',
  'mcp_invoke',
  'mcp_list',
  'mcp_search',
  'read_file',
  'replace_bytes',
  'replace_lines',
  'sandbox_reset',
  'sandbox_status',
  'shell',
  'stat',
  'write_file'
] as const

/**
 * Tools Core executes itself, so they never appear in the provider manifest.
 * Mirrors `CoreVirtualToolPolicy` (`create_workspace` :12, `task_dispatch` :23,
 * `read_attachment`, the nine `tina_chat_*` ids) — and `toolPresentation.test.ts` now proves the
 * copy by parsing that file, so a Core virtual tool cannot arrive here as an unknown id.
 */
export const CORE_VIRTUAL_TOOL_IDS = [
  'create_workspace',
  'read_attachment',
  'task_dispatch',
  'tina_chat_bind',
  'tina_chat_search_people',
  'tina_chat_list_rooms',
  'tina_chat_read_inbox',
  'tina_chat_send',
  'tina_chat_propose_intent',
  'tina_chat_list_intents',
  'tina_chat_decide_intent',
  'tina_chat_execute_intent'
] as const

/**
 * Core's git façade (`DirectToolEndpoints.cs:57`): one public id that routes seven
 * actions (`status`/`diff_preview`/`diff_compare`/`push_plan`/`branch_list`/`worktrees`/`log`)
 * onto the real `git_*` tools above.
 */
export const GIT_FACADE_TOOL_ID = 'git_worktree_manager'

export type ProviderToolId = (typeof PROVIDER_TOOL_IDS)[number]
export type CoreVirtualToolId = (typeof CORE_VIRTUAL_TOOL_IDS)[number]

/** What a tool does, as far as the id can tell us. */
export type ToolKind =
  | 'read'
  | 'list'
  | 'search'
  | 'write'
  | 'shell'
  | 'git'
  | 'mcp'
  | 'sandbox'
  | 'orchestration'
  | 'chat'
  | 'other'

const KIND_BY_ID: Record<string, ToolKind> = {
  read_file: 'read',
  read_attachment: 'read',
  stat: 'read',
  ls: 'list',
  file_search: 'search',
  write_file: 'write',
  insert_line: 'write',
  insert_byte: 'write',
  insert_bytes: 'write',
  replace_lines: 'write',
  replace_bytes: 'write',
  delete_line: 'write',
  delete_bytes: 'write',
  shell: 'shell',
  command_run: 'shell',
  mcp_invoke: 'mcp',
  mcp_list: 'mcp',
  mcp_search: 'mcp',
  sandbox_status: 'sandbox',
  sandbox_reset: 'sandbox',
  create_workspace: 'orchestration',
  task_dispatch: 'orchestration'
}

const ICON_BY_KIND: Record<ToolKind, Component> = {
  read: FileText,
  list: Folder,
  search: Search,
  write: FileCode2,
  shell: Terminal,
  git: GitBranch,
  mcp: Package,
  sandbox: Shield,
  orchestration: Workflow,
  chat: MessagesSquare,
  other: Wrench
}

const knownIds = new Set<string>([
  ...PROVIDER_TOOL_IDS,
  ...CORE_VIRTUAL_TOOL_IDS,
  GIT_FACADE_TOOL_ID
])

/**
 * True only for an id some layer actually advertises. Used to keep the catalog honest;
 * an unknown id still renders, it just falls back.
 */
export function isKnownToolId(toolId: string | null | undefined): boolean {
  return !!toolId && knownIds.has(toolId)
}

/**
 * Prefix families are genuine here: the provider's whole git surface is `git_*` and the
 * TinaChat surface is `tina_chat_*`, so a family rule tracks the manifest rather than
 * enumerating a list that changes when tools are added.
 */
export function toolKindOf(toolId: string | null | undefined): ToolKind {
  if (!toolId) return 'other'
  const explicit = KIND_BY_ID[toolId]
  if (explicit) return explicit
  if (toolId === GIT_FACADE_TOOL_ID || toolId.startsWith('git_')) return 'git'
  if (toolId.startsWith('tina_chat_')) return 'chat'
  return 'other'
}

export function toolIconOf(toolId: string | null | undefined): Component {
  return ICON_BY_KIND[toolKindOf(toolId)]
}

/**
 * The risk column as it is actually emitted: the provider manifest only ever said `low`
 * or `high` across all 49 tools, and Core's virtual tools add `low`/`high`
 * (`CoreWorkspaceTool.cs:31`, `TinaChatVirtualTools.cs:111`). The approval path uses
 * `elevated` (`ToolApprovalCoordinator.cs:1087`). `medium` is kept because Core's own
 * test doubles assert it, so a fake provider can produce it.
 */
export const RISK_LEVELS = ['low', 'medium', 'high', 'elevated'] as const

/**
 * Tone classes are named after the level, not after a guessed capability. The previous
 * chain looked for `read`/`shell`/`git`/`url`/`write` inside the risk string, so with
 * real `low`/`high` values every badge landed on the default colour.
 */
export function riskToneClass(risk: string | null | undefined): string {
  const normalized = risk?.trim().toLowerCase() ?? ''
  return (RISK_LEVELS as readonly string[]).includes(normalized)
    ? `risk-${normalized}`
    : 'risk-default'
}

/**
 * Filter choices derived from the rows on screen instead of a fixed vocabulary. This is
 * the same move `sourceOptions` already made; a hardcoded risk list could only ever be
 * wrong in one direction or the other.
 */
export function riskLevelsPresent(risks: Iterable<string | null | undefined>): string[] {
  const present = new Set<string>()
  for (const risk of risks) {
    const normalized = risk?.trim().toLowerCase()
    if (normalized) present.add(normalized)
  }
  const known = RISK_LEVELS.filter((level) => present.has(level))
  const rest = [...present].filter((r) => !(RISK_LEVELS as readonly string[]).includes(r)).sort()
  return [...known, ...rest]
}
