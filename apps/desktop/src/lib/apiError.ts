import i18n from '@/i18n'

/**
 * Who can resolve a failure, as reported by the server's error contract. Clients branch on the
 * category rather than on individual codes, so an unmodelled code still lands in a sane bucket.
 */
export type ApiErrorCategory = 'user_action_required' | 'retryable' | 'environment_unavailable' | 'internal'

/**
 * Stable recovery hints. The server names an action; the client owns the wording and behaviour.
 * Unknown kinds are ignored rather than rendered, so a newer server cannot inject a surprise.
 */
export type ApiErrorActionKind =
  | 'retry'
  | 'reload'
  | 'open_settings'
  | 'open_storage_settings'
  | 'open_tool_settings'
  | 'unregister_workspace'
  | 'choose_folder'

const KNOWN_ACTIONS = new Set<ApiErrorActionKind>([
  'retry', 'reload', 'open_settings', 'open_storage_settings', 'open_tool_settings',
  'unregister_workspace', 'choose_folder',
])

const KNOWN_CATEGORIES = new Set<ApiErrorCategory>([
  'user_action_required', 'retryable', 'environment_unavailable', 'internal',
])

/** Reads the category/retryable/actions triple, falling back to an honest status-based guess. */
function parseContract(data: Record<string, unknown> | null, status: number): {
  category: ApiErrorCategory
  retryable: boolean
  actions: ApiErrorActionKind[]
} {
  const rawCategory = typeof data?.category === 'string' ? data.category : ''
  const category = KNOWN_CATEGORIES.has(rawCategory as ApiErrorCategory)
    ? rawCategory as ApiErrorCategory
    : status >= 500 ? 'internal' : 'user_action_required'
  // Mirrors Core's fallback exactly: a transient or internal failure may succeed if repeated,
  // a user-action failure will not. Diverging here would show "retry" for a form error.
  const retryable = typeof data?.retryable === 'boolean'
    ? data.retryable
    : category === 'retryable' || category === 'internal'
  const actions = Array.isArray(data?.actions)
    ? (data.actions.filter((value): value is ApiErrorActionKind =>
        typeof value === 'string' && KNOWN_ACTIONS.has(value as ApiErrorActionKind)))
    : []
  return { category, retryable, actions }
}

/**
 * Codes that mean "the recorded folder/scope is not usable right now". Kept here so the sidebar
 * and settings page can recognise an unavailable workspace without pattern-matching prose.
 */
export const UNAVAILABLE_STORAGE_CODES = new Set([
  'storage_scope_unavailable',
  'storage_scope_not_found',
  'workspace_authorization_required',
])

export function isUnavailableStorageError(error: unknown): boolean {
  if (!(error instanceof ApiError)) return false
  return (error.code != null && UNAVAILABLE_STORAGE_CODES.has(error.code)) || error.category === 'environment_unavailable'
}

export interface ApiDiagnostic {
  code: string
  message: string
  severity: string
  line?: number
  column?: number
}

function record(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown> : null
}

/**
 * The server's text is diagnostic (docs/error-contract: the server ships no UI copy). A code this
 * build has words for (`errors.code.*`) is said in the interface language, with the body's string
 * and string-list fields available as named values; any other code keeps the server's text.
 */
/** The caller's translator: a component passes its own `t`/`te`, module code gets the app's. */
export interface ErrorWords {
  t: (key: string, named?: Record<string, unknown>) => string
  te: (key: string) => boolean
}

export function errorCodeMessage(code: string | null | undefined, body?: unknown, words: ErrorWords = i18n.global): string | undefined {
  const key = `errors.code.${code}`
  if (!code || !words.te(key)) return undefined
  const named: Record<string, string | number> = {}
  for (const [name, value] of Object.entries(record(body) ?? {})) {
    if (typeof value === 'string' || typeof value === 'number') named[name] = value
    else if (Array.isArray(value)) named[name] = value.filter((item) => typeof item === 'string').join(', ')
  }
  return words.t(key, named)
}

/** A Core error_category (why a run stopped or a call failed) in the interface language, when this build has words for it. */
export function errorCategoryMessage(category: unknown, words: ErrorWords = i18n.global): string | undefined {
  const key = `errors.category.${category}`
  return typeof category === 'string' && category && words.te(key) ? words.t(key) : undefined
}

export function apiErrorMessage(body: unknown, fallback: string): string {
  const data = record(body)
  const candidates = [data?.message, record(data?.error)?.message, data?.error,
    data?.summary, data?.detail, data?.title]
  return candidates.find((value): value is string => typeof value === 'string' && value.length > 0) ?? fallback
}

/** Only public diagnostic fields enter UI state; never retain the response body. */
export function parseApiDiagnostics(value: unknown): ApiDiagnostic[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((entry): ApiDiagnostic[] => {
    const item = record(entry)
    if (!item || typeof item.code !== 'string' || typeof item.message !== 'string'
      || typeof item.severity !== 'string') return []
    for (const position of [item.line, item.column]) {
      if (position != null && (!Number.isSafeInteger(position) || Number(position) < 1)) return []
    }
    return [{
      code: item.code,
      message: item.message,
      severity: item.severity,
      ...(typeof item.line === 'number' ? { line: item.line } : {}),
      ...(typeof item.column === 'number' ? { column: item.column } : {}),
    }]
  })
}

export class ApiError extends Error {
  readonly status: number
  readonly code?: string
  /** What the server itself said, kept when `message` is the translated wording for `code`. */
  readonly serverMessage?: string
  readonly trace_id?: string
  readonly storageId?: string
  readonly instance?: string
  readonly diagnostics: ApiDiagnostic[]
  /** Who can resolve this, per the server contract. */
  readonly category: ApiErrorCategory
  /** Whether repeating the identical request could succeed. */
  readonly retryable: boolean
  /** Stable recovery actions the UI may offer, already filtered to the known set. */
  readonly actions: ApiErrorActionKind[]

  constructor(message: string, status: number, body: unknown, context?: { storageId?: string }) {
    const data = record(body)
    const code = typeof data?.code === 'string' ? data.code : undefined
    const translated = errorCodeMessage(code, data)
    super(translated ?? message)
    this.name = 'ApiError'
    this.storageId = context?.storageId
    this.status = status
    this.code = code
    if (translated && message && message !== translated && message !== code) this.serverMessage = message
    this.trace_id = typeof data?.trace_id === 'string' ? data.trace_id : undefined
    this.instance = typeof data?.instance === 'string' ? data.instance : undefined
    this.diagnostics = parseApiDiagnostics(data?.diagnostics)
    const contract = parseContract(data, status)
    this.category = contract.category
    this.retryable = contract.retryable
    this.actions = contract.actions
  }

  /** True when this failure is worth offering a plain retry for. */
  get canRetry(): boolean { return this.retryable || this.actions.includes('retry') }
}

export function apiErrorDetails(error: unknown): string | undefined {
  if (!(error instanceof Error)) return undefined
  const data = error as Error & { diagnostics?: unknown; trace_id?: unknown; serverMessage?: unknown }
  const lines = parseApiDiagnostics(data.diagnostics).map((item) => {
    const position = item.line == null ? '' : ` (${item.line}${item.column == null ? '' : `:${item.column}`})`
    return `${item.severity} ${item.code}${position}: ${item.message}`
  })
  if (typeof data.serverMessage === 'string' && data.serverMessage) lines.unshift(data.serverMessage)
  if (typeof data.trace_id === 'string' && data.trace_id) lines.push(`trace_id: ${data.trace_id}`)
  return lines.length > 0 ? lines.join('\n') : undefined
}
