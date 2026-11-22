import i18n from '@/i18n'
import { ApiError, apiErrorMessage } from './apiError'
import { readBackendResponse } from './backendRequest'
import { assertHostAccess, useHostAccess } from './hostAccess'
import { isElectronHost, resolveWebGatewayBase } from './hostConnection'
import { invalidateReadCache } from './readCache'
import {
  captureStorageId,
  normalizeStorageRequest,
  rememberStorageResult,
  storageHeaders,
  type StorageRequestOptions,
} from './storageScope'

export type ContextStrategyTier = 'direct' | 'managed' | 'deep_curator'
export type ContextStrategyPreset = ContextStrategyTier
export type RecallFidelity = 'summary_only' | 'balanced' | 'verbatim_first'
export type MemoryEvolutionMode = 'auto_evolve' | 'candidate_only'
export type MemoryDocumentScope = 'principal' | 'project' | 'companion'
export type MemoryScope = 'session' | 'project' | 'principal' | 'companion' | 'workspace' | 'agent'
export type MemoryMutationAction = 'add' | 'supersede' | 'revoke' | 'expire'
export type MemoryKind =
  | 'fact'
  | 'preference'
  | 'decision'
  | 'success_pattern'
  | 'failure_pattern'
  | 'task_template'
  | 'supervision_rule'
  | 'bond'
  | 'episode'
  | (string & {})

export type CompanionMemorySectionKey =
  | 'persona_bond'
  | 'episodic_highlights'
  | 'operational_focus'
  | 'cross_project_notes'

export interface CompanionMemorySectionSpec {
  key: CompanionMemorySectionKey
  heading: string
  defaultKind: MemoryKind
}

export const COMPANION_MEMORY_SECTIONS: readonly CompanionMemorySectionSpec[] = [
  { key: 'persona_bond', heading: 'Persona & Bond', defaultKind: 'bond' },
  { key: 'episodic_highlights', heading: 'Episodic Highlights', defaultKind: 'episode' },
  { key: 'operational_focus', heading: 'Operational Focus', defaultKind: 'task_template' },
  { key: 'cross_project_notes', heading: 'Cross-Project Notes', defaultKind: 'fact' },
] as const

export interface ContextPolicyParams {
  recall_fidelity: RecallFidelity
  compaction_trigger_ratio: number
  recent_message_limit: number
  allow_cross_session_recall: boolean
  evolution_mode: MemoryEvolutionMode
  core_markdown_max_tokens: number
}

export const DEFAULT_CONTEXT_POLICY_PARAMS: Readonly<ContextPolicyParams> = {
  recall_fidelity: 'balanced',
  compaction_trigger_ratio: 0.75,
  recent_message_limit: 24,
  allow_cross_session_recall: false,
  evolution_mode: 'auto_evolve',
  core_markdown_max_tokens: 1500,
}

export interface ContextMemoryPolicyContextBlock {
  strategy: ContextStrategyTier
  recall_fidelity: RecallFidelity
  compaction_trigger_ratio: number
  recent_message_limit: number
  allow_cross_session_recall: boolean
  micro_compact_stale_tool_outputs: boolean
  offload_large_tool_payload_bytes: number
  default_token_budget?: number
  task_token_budget?: number
  run_token_budget?: number
}

export interface ContextMemoryPolicyMemoryBlock {
  evolution_mode: MemoryEvolutionMode
  candidate_only: boolean
  core_markdown_max_tokens: number
  retrieval_limit?: number
  project_markdown_projection: boolean
  project_memory_path: string
  companion_can_write_project_memory: boolean
  allowed_scopes?: string[]
  allowed_kinds?: string[]
}

export interface ContextMemoryPolicyDto extends ContextPolicyParams {
  strategy: ContextStrategyTier
  micro_compact_stale_tool_outputs: boolean
  offload_large_tool_payload_bytes: number
  candidate_only: boolean
  project_markdown_projection: boolean
  project_memory_path: string
  companion_can_write_project_memory: boolean
  retrieval_limit?: number
  default_token_budget?: number
  allowed_scopes?: string[]
  allowed_kinds?: string[]
  source_path?: string | null
  content_hash?: string | null
  updated_at?: string | null
  context: ContextMemoryPolicyContextBlock
  memory: ContextMemoryPolicyMemoryBlock
}

export interface ContextMemoryPolicyUpdateInput extends Partial<ContextPolicyParams> {
  strategy?: ContextStrategyTier
  candidate_only?: boolean
  project_markdown_projection?: boolean
  project_memory_path?: string
  companion_can_write_project_memory?: boolean
  retrieval_limit?: number
  expected_hash?: string | null
}

export interface MemoryDocumentQuery {
  project_id?: string | null
  workspace_root?: string | null
  companion_id?: string | null
}

export interface MemoryDocumentSectionSummaryDto {
  heading: string
  key: string
  item_count: number
  estimated_tokens: number
}

export interface MemoryDocumentDto {
  scope: MemoryDocumentScope
  path: string
  file_path: string
  markdown: string
  estimated_tokens: number
  core_markdown_max_tokens: number
  active_item_count: number
  active_hot_count: number
  cold_archive_count: number
  companion_can_write_project_memory?: boolean
  content_hash?: string | null
  etag?: string | null
  revision?: number | null
  updated_at?: string | null
  sections?: MemoryDocumentSectionSummaryDto[]
  items?: MemoryLedgerItemDto[]
}

export interface MemoryDocumentUpdateInput extends MemoryDocumentQuery {
  markdown: string
  expected_hash?: string | null
  etag?: string | null
}

export interface MemoryItemVersionDto {
  version: number
  action: MemoryMutationAction
  content: string
  previous_content?: string | null
  source_session_id?: string | null
  source_message_seq?: number | null
  source_run_id?: string | null
  created_at: string
}

export interface MemoryLedgerItemDto {
  id: string
  scope: MemoryScope
  kind: MemoryKind
  section?: string | null
  content: string
  previous_content?: string | null
  action: MemoryMutationAction
  status: string
  in_core_markdown: boolean
  version: number
  estimated_tokens: number
  source_session_id?: string | null
  source_message_seq?: number | null
  source_run_id?: string | null
  superseded_by_id?: string | null
  supersedes_id?: string | null
  revocation_reason?: string | null
  project_id?: string | null
  companion_id?: string | null
  created_at: string
  updated_at: string
  versions?: MemoryItemVersionDto[]
}

export interface ListMemoryItemsQuery {
  scope?: MemoryScope
  project_id?: string | null
  workspace_root?: string | null
  companion_id?: string | null
  status?: string | null
  kind?: string | null
  q?: string | null
  include_cold?: boolean
  include_history?: boolean
  limit?: number
}

export interface CreateMemoryItemInput {
  scope: MemoryScope
  kind?: MemoryKind
  section?: string | null
  content: string
  project_id?: string | null
  workspace_root?: string | null
  companion_id?: string | null
  source_session_id?: string | null
  source_message_seq?: number | null
  supersedes_id?: string | null
}

export interface SessionRecallRequest {
  query: string
  cross_session?: boolean
  limit?: number
  fidelity?: RecallFidelity
}

export interface SessionRecallSnippetDto {
  session_id: string
  session_title?: string | null
  message_seq: number
  role: string
  created_at: string
  excerpt: string
  score?: number
}

export interface SessionRecallResultDto {
  session_id?: string
  query?: string
  fidelity: RecallFidelity
  cross_session_allowed?: boolean
  cross_session_used?: boolean
  summary: string
  verbatim_snippets: SessionRecallSnippetDto[]
  searched_sessions?: number
  total_matches?: number
  note?: string | null
}

export interface MarkdownMemoryAnchor {
  id: string
  version: number | null
  sourceSessionId: string | null
  sourceMessageSeq: number | null
  raw: string
}

export interface ParsedCompanionStructuredMarkdown {
  preamble: string
  sections: Record<CompanionMemorySectionKey, string>
  extras: Array<{ heading: string; body: string }>
}

const ANCHOR_PATTERN = /<!--\s*mem:([^\s>]+)(?:\s+v(\d+))?(?:\s+src:([^#\s>]+)(?:#(\d+))?)?\s*-->/gi

export function extractMarkdownMemoryAnchors(markdown: string): MarkdownMemoryAnchor[] {
  if (!markdown) return []
  const anchors: MarkdownMemoryAnchor[] = []
  let match: RegExpExecArray | null
  const regex = new RegExp(ANCHOR_PATTERN.source, 'gi')
  while ((match = regex.exec(markdown)) !== null) {
    anchors.push({
      id: match[1] ?? '',
      version: match[2] ? Number(match[2]) : null,
      sourceSessionId: match[3] ?? null,
      sourceMessageSeq: match[4] ? Number(match[4]) : null,
      raw: match[0],
    })
  }
  return anchors
}

export function estimateMarkdownTokens(text: string): number {
  const trimmed = text.trim()
  if (!trimmed) return 0
  let cjkCount = 0
  for (let i = 0; i < trimmed.length; i++) {
    const code = trimmed.charCodeAt(i)
    if (code >= 0x2e80 && code <= 0x9fff) cjkCount++
  }
  const restCount = trimmed.length - cjkCount
  return Math.max(1, Math.ceil(cjkCount * 0.7 + restCount / 4))
}

function matchCompanionSectionKey(heading: string): CompanionMemorySectionKey | null {
  const normalized = heading.trim().toLowerCase().replace(/\s+/g, ' ')
  if (normalized === 'persona & bond' || normalized === 'persona and bond' || normalized.includes('人格')) {
    return 'persona_bond'
  }
  if (normalized === 'episodic highlights' || normalized.includes('情景')) {
    return 'episodic_highlights'
  }
  if (normalized === 'operational focus' || normalized.includes('工作态')) {
    return 'operational_focus'
  }
  if (normalized === 'cross-project notes' || normalized === 'cross project notes' || normalized.includes('跨项目')) {
    return 'cross_project_notes'
  }
  return null
}

export function parseCompanionStructuredMarkdown(markdown: string): ParsedCompanionStructuredMarkdown {
  const sections: Record<CompanionMemorySectionKey, string> = {
    persona_bond: '',
    episodic_highlights: '',
    operational_focus: '',
    cross_project_notes: '',
  }
  const extras: Array<{ heading: string; body: string }> = []
  const normalized = (markdown ?? '').replace(/\r\n/g, '\n')
  const lines = normalized.split('\n')

  const preambleLines: string[] = []
  let currentHeading: string | null = null
  let currentKey: CompanionMemorySectionKey | null = null
  let buffer: string[] = []

  const flush = () => {
    const body = buffer.join('\n').trim()
    if (currentKey) {
      sections[currentKey] = sections[currentKey]
        ? `${sections[currentKey]}\n\n${body}`.trim()
        : body
    } else if (currentHeading !== null) {
      extras.push({ heading: currentHeading, body })
    } else {
      preambleLines.push(...buffer)
    }
    buffer = []
  }

  for (const line of lines) {
    const h2 = /^##\s+(.+?)\s*$/.exec(line)
    if (h2) {
      flush()
      currentHeading = h2[1]!
      currentKey = matchCompanionSectionKey(currentHeading)
      continue
    }
    buffer.push(line)
  }
  flush()

  return {
    preamble: preambleLines.join('\n').trim(),
    sections,
    extras,
  }
}

export function serializeCompanionStructuredMarkdown(parsed: ParsedCompanionStructuredMarkdown): string {
  const blocks: string[] = []
  const preamble = parsed.preamble.trim()
  if (preamble) {
    blocks.push(preamble)
  }
  for (const spec of COMPANION_MEMORY_SECTIONS) {
    const body = (parsed.sections[spec.key] ?? '').trim()
    blocks.push(body ? `## ${spec.heading}\n\n${body}` : `## ${spec.heading}\n`)
  }
  for (const extra of parsed.extras) {
    const body = extra.body.trim()
    blocks.push(body ? `## ${extra.heading}\n\n${body}` : `## ${extra.heading}\n`)
  }
  return `${blocks.join('\n\n').trim()}\n`
}

export function resolveContextMemoryGatewayUrl(): string {
  if (typeof window !== 'undefined') {
    const bridgeUrl = window.tinadec?.gatewayUrl?.()
    if (bridgeUrl) return bridgeUrl.replace(/\/+$/, '')
    if (!isElectronHost()) {
      return resolveWebGatewayBase().replace(/\/+$/, '')
    }
  }
  return 'http://127.0.0.1:48730'
}

function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') {
      search.set(key, String(value))
    }
  }
  const text = search.toString()
  return text ? `?${text}` : ''
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {}
}

function asStrategy(value: unknown, fallback: ContextStrategyTier = 'managed'): ContextStrategyTier {
  return value === 'direct' || value === 'managed' || value === 'deep_curator' ? value : fallback
}

function asFidelity(value: unknown, fallback: RecallFidelity = 'balanced'): RecallFidelity {
  return value === 'summary_only' || value === 'balanced' || value === 'verbatim_first' ? value : fallback
}

function asEvolutionMode(value: unknown, candidateOnly: boolean): MemoryEvolutionMode {
  if (value === 'candidate_only' || value === 'auto_evolve') return value
  return candidateOnly ? 'candidate_only' : 'auto_evolve'
}

function clampNumber(value: unknown, min: number, max: number, fallback: number): number {
  const num = typeof value === 'number' ? value : typeof value === 'string' && value.trim() !== '' ? Number(value) : NaN
  if (!Number.isFinite(num)) return fallback
  return Math.min(max, Math.max(min, num))
}

export function normalizeContextMemoryPolicy(raw: unknown): ContextMemoryPolicyDto {
  const root = asRecord(raw)
  const ctx = asRecord(root.context)
  const mem = asRecord(root.memory)

  const strategy = asStrategy(ctx.strategy ?? root.strategy, 'managed')
  const recall_fidelity = asFidelity(ctx.recall_fidelity ?? root.recall_fidelity, DEFAULT_CONTEXT_POLICY_PARAMS.recall_fidelity)
  const compaction_trigger_ratio = clampNumber(
    ctx.compaction_trigger_ratio ?? root.compaction_trigger_ratio,
    0.4,
    0.95,
    DEFAULT_CONTEXT_POLICY_PARAMS.compaction_trigger_ratio,
  )
  const recent_message_limit = Math.round(
    clampNumber(
      ctx.recent_message_limit ?? root.recent_message_limit,
      4,
      200,
      DEFAULT_CONTEXT_POLICY_PARAMS.recent_message_limit,
    ),
  )
  const allow_cross_session_recall = Boolean(
    ctx.allow_cross_session_recall ?? root.allow_cross_session_recall ?? (strategy === 'deep_curator'),
  )
  const micro_compact_stale_tool_outputs =
    ctx.micro_compact_stale_tool_outputs !== undefined
      ? Boolean(ctx.micro_compact_stale_tool_outputs)
      : root.micro_compact_stale_tool_outputs !== undefined
        ? Boolean(root.micro_compact_stale_tool_outputs)
        : true
  const offload_large_tool_payload_bytes = Math.round(
    clampNumber(ctx.offload_large_tool_payload_bytes ?? root.offload_large_tool_payload_bytes, 1024, 1048576, 16384),
  )

  const rawCandidateOnly = Boolean(mem.candidate_only ?? root.candidate_only ?? false)
  const evolution_mode = asEvolutionMode(mem.evolution_mode ?? root.evolution_mode, rawCandidateOnly)
  const candidate_only = evolution_mode === 'candidate_only' || rawCandidateOnly
  const core_markdown_max_tokens = Math.round(
    clampNumber(
      mem.core_markdown_max_tokens ?? root.core_markdown_max_tokens,
      256,
      8000,
      DEFAULT_CONTEXT_POLICY_PARAMS.core_markdown_max_tokens,
    ),
  )
  const project_markdown_projection =
    mem.project_markdown_projection !== undefined
      ? Boolean(mem.project_markdown_projection)
      : root.project_markdown_projection !== undefined
        ? Boolean(root.project_markdown_projection)
        : true
  const project_memory_path =
    typeof (mem.project_memory_path ?? root.project_memory_path) === 'string' &&
    String(mem.project_memory_path ?? root.project_memory_path).trim()
      ? String(mem.project_memory_path ?? root.project_memory_path)
      : '.tinadec/memory/project-core.md'
  const companion_can_write_project_memory = Boolean(
    mem.companion_can_write_project_memory ?? root.companion_can_write_project_memory ?? false,
  )
  const retrieval_limit =
    mem.retrieval_limit !== undefined || root.retrieval_limit !== undefined
      ? Math.round(clampNumber(mem.retrieval_limit ?? root.retrieval_limit, 1, 64, 8))
      : undefined
  const default_token_budget =
    ctx.default_token_budget !== undefined || root.default_token_budget !== undefined
      ? Math.round(clampNumber(ctx.default_token_budget ?? root.default_token_budget, 1024, 2000000, 131072))
      : undefined

  const allowed_scopes = Array.isArray(mem.allowed_scopes ?? root.allowed_scopes)
    ? ((mem.allowed_scopes ?? root.allowed_scopes) as unknown[]).map(String)
    : undefined
  const allowed_kinds = Array.isArray(mem.allowed_kinds ?? root.allowed_kinds)
    ? ((mem.allowed_kinds ?? root.allowed_kinds) as unknown[]).map(String)
    : undefined

  return {
    strategy,
    recall_fidelity,
    compaction_trigger_ratio,
    recent_message_limit,
    allow_cross_session_recall,
    micro_compact_stale_tool_outputs,
    offload_large_tool_payload_bytes,
    evolution_mode,
    candidate_only,
    core_markdown_max_tokens,
    project_markdown_projection,
    project_memory_path,
    companion_can_write_project_memory,
    retrieval_limit,
    default_token_budget,
    allowed_scopes,
    allowed_kinds,
    source_path: typeof root.source_path === 'string' ? root.source_path : null,
    content_hash: typeof root.content_hash === 'string' ? root.content_hash : null,
    updated_at: typeof root.updated_at === 'string' ? root.updated_at : null,
    context: {
      strategy,
      recall_fidelity,
      compaction_trigger_ratio,
      recent_message_limit,
      allow_cross_session_recall,
      micro_compact_stale_tool_outputs,
      offload_large_tool_payload_bytes,
      default_token_budget,
    },
    memory: {
      evolution_mode,
      candidate_only,
      core_markdown_max_tokens,
      retrieval_limit,
      project_markdown_projection,
      project_memory_path,
      companion_can_write_project_memory,
      allowed_scopes,
      allowed_kinds,
    },
  }
}

function defaultDocumentPath(scope: MemoryDocumentScope, companionId?: string | null): string {
  if (scope === 'principal') return 'memory/user-profile.md'
  if (scope === 'project') return '.tinadec/memory/project-core.md'
  return `companions/${companionId || 'default'}/memory.md`
}

export function normalizeMemoryDocument(
  raw: unknown,
  fallbackScope: MemoryDocumentScope,
  companionId?: string | null,
): MemoryDocumentDto {
  const row = asRecord(raw)
  const scope =
    row.scope === 'principal' || row.scope === 'project' || row.scope === 'companion'
      ? row.scope
      : fallbackScope
  const markdown =
    typeof row.markdown === 'string'
      ? row.markdown
      : typeof row.content === 'string'
        ? row.content
        : typeof row.text === 'string'
          ? row.text
          : ''
  const path =
    typeof row.path === 'string' && row.path.trim()
      ? row.path
      : typeof row.file_path === 'string' && row.file_path.trim()
        ? row.file_path
        : typeof row.document_path === 'string' && row.document_path.trim()
          ? row.document_path
          : defaultDocumentPath(scope, companionId)
  const core_markdown_max_tokens = Math.round(
    clampNumber(
      row.core_markdown_max_tokens ?? row.max_tokens ?? row.token_budget,
      256,
      8000,
      DEFAULT_CONTEXT_POLICY_PARAMS.core_markdown_max_tokens,
    ),
  )
  const estimated_tokens =
    typeof row.estimated_tokens === 'number' && Number.isFinite(row.estimated_tokens)
      ? Math.max(0, Math.round(row.estimated_tokens))
      : estimateMarkdownTokens(markdown)
  const active_item_count = Math.max(
    0,
    Math.round(
      clampNumber(
        row.active_hot_count ?? row.active_item_count ?? row.hot_item_count ?? row.projected_item_count ?? row.projected_count,
        0,
        100000,
        extractMarkdownMemoryAnchors(markdown).length,
      ),
    ),
  )
  const cold_archive_count = Math.max(
    0,
    Math.round(
      clampNumber(
        row.cold_archive_count ?? row.cold_item_count ?? row.overflow_item_count ?? row.overflow_count ?? row.archived_count,
        0,
        100000,
        0,
      ),
    ),
  )

  return {
    scope,
    path,
    file_path: path,
    markdown,
    estimated_tokens,
    core_markdown_max_tokens,
    active_item_count,
    active_hot_count: active_item_count,
    cold_archive_count,
    companion_can_write_project_memory:
      typeof row.companion_can_write_project_memory === 'boolean'
        ? row.companion_can_write_project_memory
        : undefined,
    content_hash: typeof row.content_hash === 'string' ? row.content_hash : null,
    etag: typeof row.etag === 'string' ? row.etag : null,
    revision: typeof row.revision === 'number' ? row.revision : null,
    updated_at: typeof row.updated_at === 'string' ? row.updated_at : null,
    sections: Array.isArray(row.sections)
      ? row.sections.map((s) => {
          const sr = asRecord(s)
          return {
            heading: typeof sr.heading === 'string' ? sr.heading : '',
            key: typeof sr.key === 'string' ? sr.key : '',
            item_count: Math.max(0, Math.round(clampNumber(sr.item_count, 0, 100000, 0))),
            estimated_tokens: Math.max(0, Math.round(clampNumber(sr.estimated_tokens, 0, 100000, 0))),
          }
        })
      : undefined,
    items: Array.isArray(row.items) ? row.items.map(normalizeMemoryLedgerItem) : undefined,
  }
}

function inferMutationAction(row: Record<string, unknown>): MemoryMutationAction {
  const explicit = row.action ?? row.last_action ?? row.mutation_action
  if (explicit === 'add' || explicit === 'supersede' || explicit === 'revoke' || explicit === 'expire') {
    return explicit
  }
  const status = String(row.status ?? 'active').toLowerCase()
  if (status === 'revoked') return 'revoke'
  if (status === 'expired') return 'expire'
  const version = typeof row.version === 'number' ? row.version : typeof row.current_version === 'number' ? row.current_version : 1
  if (version > 1 || row.previous_content || row.supersedes_id) return 'supersede'
  return 'add'
}

export function normalizeMemoryLedgerItem(raw: unknown): MemoryLedgerItemDto {
  const row = asRecord(raw)
  const content = typeof row.content === 'string' ? row.content : typeof row.text === 'string' ? row.text : ''
  const status = typeof row.status === 'string' && row.status ? row.status : 'active'
  const version = Math.max(
    1,
    Math.round(clampNumber(row.version ?? row.current_version, 1, 100000, 1)),
  )
  const inCore =
    typeof row.in_core_markdown === 'boolean'
      ? row.in_core_markdown
      : typeof row.is_projected === 'boolean'
        ? row.is_projected
        : typeof row.in_hot_markdown === 'boolean'
          ? row.in_hot_markdown
          : status === 'active' && row.tier !== 'cold'
  const versions = Array.isArray(row.versions)
    ? row.versions.map((v): MemoryItemVersionDto => {
        const vr = asRecord(v)
        return {
          version: Math.max(1, Math.round(clampNumber(vr.version, 1, 100000, 1))),
          action: inferMutationAction(vr),
          content: typeof vr.content === 'string' ? vr.content : '',
          previous_content: typeof vr.previous_content === 'string' ? vr.previous_content : null,
          source_session_id: typeof vr.source_session_id === 'string' ? vr.source_session_id : null,
          source_message_seq: typeof vr.source_message_seq === 'number' ? vr.source_message_seq : null,
          source_run_id: typeof vr.source_run_id === 'string' ? vr.source_run_id : null,
          created_at: typeof vr.created_at === 'string' ? vr.created_at : '',
        }
      })
    : undefined

  return {
    id: String(row.id ?? ''),
    scope: (typeof row.scope === 'string' ? row.scope : 'principal') as MemoryScope,
    kind: (typeof row.kind === 'string' ? row.kind : 'fact') as MemoryKind,
    section: typeof row.section === 'string' ? row.section : null,
    content,
    previous_content: typeof row.previous_content === 'string' ? row.previous_content : null,
    action: inferMutationAction(row),
    status,
    in_core_markdown: inCore,
    version,
    estimated_tokens:
      typeof row.estimated_tokens === 'number' && Number.isFinite(row.estimated_tokens)
        ? Math.max(0, Math.round(row.estimated_tokens))
        : estimateMarkdownTokens(content),
    source_session_id: typeof row.source_session_id === 'string' ? row.source_session_id : null,
    source_message_seq:
      typeof row.source_message_seq === 'number' && Number.isFinite(row.source_message_seq)
        ? row.source_message_seq
        : null,
    source_run_id: typeof row.source_run_id === 'string' ? row.source_run_id : null,
    superseded_by_id:
      typeof row.superseded_by_id === 'string'
        ? row.superseded_by_id
        : typeof row.superseded_by === 'string'
          ? row.superseded_by
          : null,
    supersedes_id: typeof row.supersedes_id === 'string' ? row.supersedes_id : null,
    revocation_reason: typeof row.revocation_reason === 'string' ? row.revocation_reason : null,
    project_id: typeof row.project_id === 'string' ? row.project_id : null,
    companion_id: typeof row.companion_id === 'string' ? row.companion_id : null,
    created_at: typeof row.created_at === 'string' ? row.created_at : '',
    updated_at:
      typeof row.updated_at === 'string'
        ? row.updated_at
        : typeof row.created_at === 'string'
          ? row.created_at
          : '',
    versions,
  }
}

export function normalizeSessionRecallResult(raw: unknown): SessionRecallResultDto {
  const root = asRecord(raw)
  const snippetsRaw = Array.isArray(root.verbatim_snippets)
    ? root.verbatim_snippets
    : Array.isArray(root.snippets)
      ? root.snippets
      : []
  return {
    session_id: typeof root.session_id === 'string' ? root.session_id : undefined,
    query: typeof root.query === 'string' ? root.query : undefined,
    fidelity: asFidelity(root.fidelity, 'balanced'),
    cross_session_allowed:
      typeof root.cross_session_allowed === 'boolean' ? root.cross_session_allowed : undefined,
    cross_session_used:
      typeof root.cross_session_used === 'boolean' ? root.cross_session_used : undefined,
    summary: typeof root.summary === 'string' ? root.summary : '',
    verbatim_snippets: snippetsRaw.map((entry): SessionRecallSnippetDto => {
      const item = asRecord(entry)
      return {
        session_id: String(item.session_id ?? ''),
        session_title: typeof item.session_title === 'string' ? item.session_title : null,
        message_seq: Math.max(0, Math.round(clampNumber(item.message_seq ?? item.sequence, 0, 10000000, 0))),
        role: typeof item.role === 'string' ? item.role : 'user',
        created_at: typeof item.created_at === 'string' ? item.created_at : '',
        excerpt: typeof item.excerpt === 'string' ? item.excerpt : typeof item.content === 'string' ? item.content : '',
        score: typeof item.score === 'number' ? item.score : undefined,
      }
    }),
    searched_sessions: typeof root.searched_sessions === 'number' ? root.searched_sessions : undefined,
    total_matches: typeof root.total_matches === 'number' ? root.total_matches : undefined,
    note: typeof root.note === 'string' ? root.note : null,
  }
}

async function requestJson<T>(
  path: string,
  init?: StorageRequestOptions,
): Promise<{ data: T; headers: Headers }> {
  const { canAccessBackend, reason } = useHostAccess()
  if (!canAccessBackend.value) {
    await assertHostAccess(path, init?.signal ?? undefined)
    throw new ApiError(reason.value, 503, {
      code: 'host_unavailable',
      detail: reason.value,
      category: 'environment_unavailable',
      retryable: true,
      actions: ['retry'],
    })
  }

  const gatewayUrl = resolveContextMemoryGatewayUrl()
  const storageId = captureStorageId(path, init)
  const normalized = normalizeStorageRequest(path, init)
  const headers = storageHeaders(path, { ...init, storageId })
  if (!headers.has('accept')) headers.set('accept', 'application/json')
  if (init?.body && !headers.has('content-type')) headers.set('content-type', 'application/json')

  const { response, text } = await readBackendResponse(
    `${gatewayUrl}${normalized.path}`,
    path,
    {
      ...init,
      credentials: init?.credentials ?? 'include',
      body: normalized.body,
      headers,
    },
    { storageId },
  )

  let data: unknown = null
  if (text.length > 0) {
    try {
      data = JSON.parse(text)
    } catch {
      throw new Error(i18n.global.t('errors.invalidServerResponse', { text: text.substring(0, 200) }))
    }
  }

  if (!response.ok) {
    throw new ApiError(apiErrorMessage(data, response.statusText), response.status, data, { storageId })
  }

  rememberStorageResult(path, data, storageId)
  if (init?.method && init.method !== 'GET' && init.method !== 'HEAD') {
    invalidateReadCache()
  }

  return { data: data as T, headers: response.headers }
}

function serializePolicyPatch(patch: ContextMemoryPolicyUpdateInput): Record<string, unknown> {
  const contextPatch: Record<string, unknown> = {}
  const memoryPatch: Record<string, unknown> = {}
  const body: Record<string, unknown> = {}

  if (patch.strategy !== undefined) {
    body.strategy = patch.strategy
    contextPatch.strategy = patch.strategy
  }
  if (patch.recall_fidelity !== undefined) {
    body.recall_fidelity = patch.recall_fidelity
    contextPatch.recall_fidelity = patch.recall_fidelity
  }
  if (patch.compaction_trigger_ratio !== undefined) {
    body.compaction_trigger_ratio = patch.compaction_trigger_ratio
    contextPatch.compaction_trigger_ratio = patch.compaction_trigger_ratio
  }
  if (patch.recent_message_limit !== undefined) {
    body.recent_message_limit = patch.recent_message_limit
    contextPatch.recent_message_limit = patch.recent_message_limit
  }
  if (patch.allow_cross_session_recall !== undefined) {
    body.allow_cross_session_recall = patch.allow_cross_session_recall
    contextPatch.allow_cross_session_recall = patch.allow_cross_session_recall
  }
  if (patch.evolution_mode !== undefined) {
    body.evolution_mode = patch.evolution_mode
    body.candidate_only = patch.evolution_mode === 'candidate_only'
    memoryPatch.evolution_mode = patch.evolution_mode
    memoryPatch.candidate_only = patch.evolution_mode === 'candidate_only'
  } else if (patch.candidate_only !== undefined) {
    body.candidate_only = patch.candidate_only
    memoryPatch.candidate_only = patch.candidate_only
  }
  if (patch.core_markdown_max_tokens !== undefined) {
    body.core_markdown_max_tokens = patch.core_markdown_max_tokens
    memoryPatch.core_markdown_max_tokens = patch.core_markdown_max_tokens
  }
  if (patch.project_markdown_projection !== undefined) {
    body.project_markdown_projection = patch.project_markdown_projection
    memoryPatch.project_markdown_projection = patch.project_markdown_projection
  }
  if (patch.project_memory_path !== undefined) {
    body.project_memory_path = patch.project_memory_path
    memoryPatch.project_memory_path = patch.project_memory_path
  }
  if (patch.companion_can_write_project_memory !== undefined) {
    body.companion_can_write_project_memory = patch.companion_can_write_project_memory
    memoryPatch.companion_can_write_project_memory = patch.companion_can_write_project_memory
  }
  if (patch.retrieval_limit !== undefined) {
    body.retrieval_limit = patch.retrieval_limit
    memoryPatch.retrieval_limit = patch.retrieval_limit
  }
  if (patch.expected_hash) {
    body.expected_content_hash = patch.expected_hash
    body.expected_hash = patch.expected_hash
  }

  if (Object.keys(contextPatch).length > 0) body.context = contextPatch
  if (Object.keys(memoryPatch).length > 0) body.memory = memoryPatch
  return body
}

export const contextMemoryApi = {
  get gatewayUrl(): string {
    return resolveContextMemoryGatewayUrl()
  },

  async getPolicy(options?: { storageId?: string; signal?: AbortSignal }): Promise<ContextMemoryPolicyDto> {
    const { data } = await requestJson<unknown>('/api/v1/context-memory/policy', {
      storageId: options?.storageId ?? 'user',
      signal: options?.signal,
    })
    return normalizeContextMemoryPolicy(data)
  },

  async updatePolicy(
    patch: ContextMemoryPolicyUpdateInput,
    options?: { storageId?: string; etag?: string | null; signal?: AbortSignal },
  ): Promise<ContextMemoryPolicyDto> {
    const headers: Record<string, string> = {}
    if (options?.etag) headers['If-Match'] = options.etag
    const { data } = await requestJson<unknown>('/api/v1/context-memory/policy', {
      method: 'PUT',
      storageId: options?.storageId ?? 'user',
      signal: options?.signal,
      headers,
      body: JSON.stringify(serializePolicyPatch(patch)),
    })
    return normalizeContextMemoryPolicy(data)
  },

  async getMemoryDocument(
    scope: MemoryDocumentScope,
    query?: MemoryDocumentQuery,
    options?: { storageId?: string; signal?: AbortSignal },
  ): Promise<MemoryDocumentDto> {
    const qs = buildQuery({
      project_id: query?.project_id,
      workspace_root: query?.workspace_root,
      companion_id: query?.companion_id,
    })
    const { data, headers } = await requestJson<unknown>(
      `/api/v1/memories/documents/${encodeURIComponent(scope)}${qs}`,
      {
        storageId: options?.storageId,
        signal: options?.signal,
      },
    )
    const doc = normalizeMemoryDocument(data, scope, query?.companion_id)
    const etag = headers.get('etag')
    return etag && !doc.etag ? { ...doc, etag } : doc
  },

  async updateMemoryDocument(
    scope: MemoryDocumentScope,
    input: MemoryDocumentUpdateInput,
    options?: { storageId?: string; signal?: AbortSignal },
  ): Promise<MemoryDocumentDto> {
    const qs = buildQuery({
      project_id: input.project_id,
      workspace_root: input.workspace_root,
      companion_id: input.companion_id,
    })
    const headers: Record<string, string> = {}
    if (input.etag) headers['If-Match'] = input.etag
    const { data, headers: resHeaders } = await requestJson<unknown>(
      `/api/v1/memories/documents/${encodeURIComponent(scope)}${qs}`,
      {
        method: 'PUT',
        storageId: options?.storageId,
        signal: options?.signal,
        headers,
        body: JSON.stringify({
          markdown: input.markdown,
          content: input.markdown,
          project_id: input.project_id ?? undefined,
          workspace_root: input.workspace_root ?? undefined,
          companion_id: input.companion_id ?? undefined,
          expected_content_hash: input.expected_hash ?? undefined,
          expected_hash: input.expected_hash ?? undefined,
        }),
      },
    )
    const doc = normalizeMemoryDocument(data, scope, input.companion_id)
    const etag = resHeaders.get('etag')
    return etag && !doc.etag ? { ...doc, etag } : doc
  },

  async listMemoryItems(
    query: ListMemoryItemsQuery = {},
    options?: { storageId?: string; signal?: AbortSignal },
  ): Promise<MemoryLedgerItemDto[]> {
    const qs = buildQuery({
      scope: query.scope,
      project_id: query.project_id,
      workspace_root: query.workspace_root,
      companion_id: query.companion_id,
      status: query.status,
      kind: query.kind,
      q: query.q,
      include_cold: query.include_cold,
      include_history: query.include_history,
      limit: query.limit,
    })
    const { data } = await requestJson<unknown>(`/api/v1/memories/items${qs}`, {
      storageId: options?.storageId,
      signal: options?.signal,
    })
    const list = Array.isArray(data)
      ? data
      : Array.isArray(asRecord(data).items)
        ? (asRecord(data).items as unknown[])
        : []
    return list.map(normalizeMemoryLedgerItem)
  },

  async createMemoryItem(
    input: CreateMemoryItemInput,
    options?: { storageId?: string; signal?: AbortSignal },
  ): Promise<MemoryLedgerItemDto> {
    const { data } = await requestJson<unknown>('/api/v1/memories/items', {
      method: 'POST',
      storageId: options?.storageId,
      signal: options?.signal,
      body: JSON.stringify({
        scope: input.scope,
        kind: input.kind ?? 'fact',
        section: input.section ?? undefined,
        content: input.content,
        project_id: input.project_id ?? undefined,
        workspace_root: input.workspace_root ?? undefined,
        companion_id: input.companion_id ?? undefined,
        source_session_id: input.source_session_id ?? undefined,
        source_message_seq: input.source_message_seq ?? undefined,
        supersede_item_id: input.supersedes_id ?? undefined,
        supersedes_id: input.supersedes_id ?? undefined,
      }),
    })
    const root = asRecord(data)
    return normalizeMemoryLedgerItem(root.item ?? data)
  },

  async deleteMemoryItem(
    id: string,
    reason?: string,
    options?: { storageId?: string; signal?: AbortSignal },
  ): Promise<void> {
    const qs = buildQuery({ reason })
    await requestJson<unknown>(`/api/v1/memories/items/${encodeURIComponent(id)}${qs}`, {
      method: 'DELETE',
      storageId: options?.storageId,
      signal: options?.signal,
    })
  },

  async revertMemoryItem(
    id: string,
    targetVersion?: number,
    options?: { storageId?: string; signal?: AbortSignal },
  ): Promise<MemoryLedgerItemDto> {
    const { data } = await requestJson<unknown>(
      `/api/v1/memories/items/${encodeURIComponent(id)}/revert`,
      {
        method: 'POST',
        storageId: options?.storageId,
        signal: options?.signal,
        body: JSON.stringify(targetVersion !== undefined ? { target_version: targetVersion } : {}),
      },
    )
    const root = asRecord(data)
    return normalizeMemoryLedgerItem(root.item ?? data)
  },

  async recallSessionHistory(
    sessionId: string,
    input: SessionRecallRequest,
    options?: { storageId?: string; signal?: AbortSignal },
  ): Promise<SessionRecallResultDto> {
    const { data } = await requestJson<unknown>(
      `/api/v1/sessions/${encodeURIComponent(sessionId)}/recall`,
      {
        method: 'POST',
        storageId: options?.storageId,
        signal: options?.signal,
        body: JSON.stringify({
          query: input.query,
          cross_session: input.cross_session,
          limit: input.limit,
          fidelity: input.fidelity,
        }),
      },
    )
    return normalizeSessionRecallResult(data)
  },
}
