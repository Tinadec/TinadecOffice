import type { Component } from 'vue'
import { toolIconOf, toolKindOf, type ToolKind } from '@/lib/toolPresentation'

/**
 * A tool call read as a sentence: "Read src/a.ts", "Searched useResults in src", "Ran npm test".
 *
 * The facts come from Core's approval-evidence projection on `tool.execution.requested`
 * (`resource_path`, `command`, `query`, `arguments_summary`), never from raw parameters.
 * This module only picks the verb, the subject and whether the call is a quiet exploration
 * step; the words themselves are i18n keys resolved by the component (`verbKey`).
 */

export type ToolVerb =
  | 'read'
  | 'list'
  | 'search'
  | 'edit'
  | 'run'
  | 'git'
  | 'mcp'
  | 'fetch'
  | 'workspace'
  | 'other'

/** Exploration that folds into "Explored 6 files": it changes nothing and needs nobody. */
export type QuietCategory = 'read' | 'list' | 'search'

export type VerbPhase = 'active' | 'done' | 'failed'

export interface ToolSentenceInput {
  tool_id: string
  resource_path?: string | null
  command?: string | null
  query?: string | null
  arguments_summary?: string | null
}

export interface ToolSentence {
  kind: ToolKind
  verb: ToolVerb
  /** Non-null for read/list/search calls, which group into an exploration summary. */
  quiet: QuietCategory | null
  /** What the verb acts on: a path, a command line, a search term, an MCP target. */
  subject: string | null
  /** Where a search ran, when it names a directory. */
  location: string | null
  icon: Component
}

const VERB_BY_KIND: Record<ToolKind, ToolVerb> = {
  read: 'read',
  list: 'list',
  search: 'search',
  write: 'edit',
  shell: 'run',
  git: 'git',
  mcp: 'mcp',
  sandbox: 'other',
  orchestration: 'other',
  chat: 'other',
  other: 'other',
}

/**
 * Display form of a path: forward slashes, and relative to the workspace root when it sits
 * inside it. A path outside the root is shown whole rather than guessed at.
 */
export function displayPath(path: string, workspaceRoot?: string | null): string {
  const normalized = path.replace(/\\/g, '/')
  if (!workspaceRoot) return normalized
  const root = workspaceRoot.replace(/\\/g, '/').replace(/\/+$/, '')
  const caseless = /^[a-z]:\//i.test(root)
  const comparable = caseless ? normalized.toLowerCase() : normalized
  const comparableRoot = caseless ? root.toLowerCase() : root
  if (comparable === comparableRoot) return '.'
  return comparable.startsWith(`${comparableRoot}/`) ? normalized.slice(root.length + 1) : normalized
}

/** "write_file → src/a.ts" carries its subject after the arrow; anything else is the whole line. */
function summarySubject(toolId: string, summary: string | null | undefined): string | null {
  if (!summary) return null
  const prefix = `${toolId} → `
  if (summary.startsWith(prefix)) return summary.slice(prefix.length) || null
  return summary === toolId ? null : summary
}

export function toolSentence(input: ToolSentenceInput, workspaceRoot?: string | null): ToolSentence {
  const kind = toolKindOf(input.tool_id)
  const icon = toolIconOf(input.tool_id)
  const path = input.resource_path ? displayPath(input.resource_path, workspaceRoot) : null
  let verb = VERB_BY_KIND[kind]
  if (input.tool_id === 'web_fetch') verb = 'fetch'
  if (input.tool_id === 'create_workspace') verb = 'workspace'

  const quiet: QuietCategory | null = verb === 'read' || verb === 'list' || verb === 'search' ? verb : null
  let subject: string | null
  let location: string | null = null
  switch (verb) {
    case 'run':
      subject = input.command ?? summarySubject(input.tool_id, input.arguments_summary)
      break
    case 'search':
      subject = input.query ?? null
      location = path
      if (!subject) {
        subject = path
        location = null
      }
      break
    case 'git': {
      const op = input.tool_id.startsWith('git_') ? input.tool_id.slice(4).replace(/_/g, ' ') : input.tool_id
      subject = `git ${op}`
      break
    }
    case 'fetch':
      subject = path ?? input.query ?? summarySubject(input.tool_id, input.arguments_summary)
      break
    case 'mcp':
      subject = summarySubject(input.tool_id, input.arguments_summary)
      break
    default:
      subject = path ?? summarySubject(input.tool_id, input.arguments_summary)
  }
  if (verb === 'other' && !subject) subject = input.tool_id
  return { kind, verb, quiet, subject, location, icon }
}

/** The sentence of a reduced tool entry (the feed's camelCase record). */
export function entrySentence(entry: {
  toolId: string
  resourcePath: string | null
  command: string | null
  query: string | null
  argumentsSummary: string | null
}, workspaceRoot?: string | null): ToolSentence {
  return toolSentence({
    tool_id: entry.toolId,
    resource_path: entry.resourcePath,
    command: entry.command,
    query: entry.query,
    arguments_summary: entry.argumentsSummary,
  }, workspaceRoot)
}

/** The i18n key of a verb in one phase: `conversation.tool.<verb>.<phase>`. */
export function verbKey(verb: ToolVerb, phase: VerbPhase): string {
  return `conversation.tool.${verb}.${phase}`
}
