export type CompanionConnectionKind = 'work_session' | 'browser' | 'mcp' | 'server' | 'phone' | 'external_agent'
export type CompanionBrowserDriver = 'builtin_electron' | 'ego_lite'
export type CompanionConnectionAuthorization = 'configured' | 'authorized' | 'revoked'
export type CompanionConnectionRuntimeStatus = 'unsupported' | 'unknown' | 'connected' | 'active' | string

export interface CompanionBrowserProfile {
  id: string
  name: string
  driver: CompanionBrowserDriver | string
  is_default?: boolean
  available?: boolean
  detail?: string | null
}

export interface CompanionWorkSessionPlanProgress {
  done: number
  total: number
  current_step: string | null
  summary: string | null
}

export interface CompanionWorkSessionPendingApproval {
  approval_id: string
  tool_id: string
  summary: string | null
  risk_level: string | null
  created_at: string
}

export interface CompanionWorkSessionRunSummary {
  id: string
  status: string
  summary: string | null
}

export interface CompanionConnectedWorkSession {
  session_id: string
  title: string
  project_id: string | null
  project_name: string | null
  mode_name: string | null
  view_mode?: 'flat' | 'space' | null
  latest_run_id: string | null
  latest_run_status: string | null
  latest_run_summary: string | null
  plan_summary: string | null
  plan_progress: CompanionWorkSessionPlanProgress | null
  pending_approvals: CompanionWorkSessionPendingApproval[]
  browser_space_id?: string | null
  browser_url?: string | null
  browser_driver?: string | null
  browser_handoff_reason?: string | null
  updated_at?: string | null
  latest_run?: CompanionWorkSessionRunSummary | null
}

export function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

export function asNonEmptyString(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null
}

export function asViewMode(value: unknown): 'flat' | 'space' | null {
  if (value === 'flat' || value === 'space') return value
  return null
}

export function parseWorkSessionPlanProgress(
  planSummary: string | null | undefined,
  rawProgress?: unknown,
): CompanionWorkSessionPlanProgress | null {
  const structured = asRecord(rawProgress)
  if (structured) {
    const done = typeof structured.done === 'number' && Number.isFinite(structured.done) ? structured.done : null
    const total = typeof structured.total === 'number' && Number.isFinite(structured.total) ? structured.total : null
    const currentStep = asNonEmptyString(structured.current_step ?? structured.currentStep)
    const summary = asNonEmptyString(structured.summary) ?? asNonEmptyString(planSummary)
    if (done !== null && total !== null && total > 0) {
      return { done: Math.max(0, Math.min(done, total)), total, current_step: currentStep, summary }
    }
  }
  const text = asNonEmptyString(planSummary)
  if (!text) return null
  const ratio = text.match(/(\d+)\s*\/\s*(\d+)/)
  if (ratio) {
    const done = Number(ratio[1])
    const total = Number(ratio[2])
    if (Number.isFinite(done) && Number.isFinite(total) && total > 0) {
      const stripped = text
        .replace(ratio[0], '')
        .replace(/^[\s·•:\-—|,()（）步steps]+/i, '')
        .replace(/^(?:steps?|步)\s*[·•:\-—|]?\s*/i, '')
        .trim()
      return {
        done: Math.max(0, Math.min(done, total)),
        total,
        current_step: stripped || null,
        summary: text,
      }
    }
  }
  return { done: 0, total: 0, current_step: text, summary: text }
}

export function normalizeWorkSessionApproval(raw: unknown): CompanionWorkSessionPendingApproval | null {
  const row = asRecord(raw)
  if (!row) return null
  const approvalId = asNonEmptyString(row.approval_id ?? row.approvalId ?? row.id)
  if (!approvalId) return null
  return {
    approval_id: approvalId,
    tool_id: asNonEmptyString(row.tool_id ?? row.toolId) ?? 'tool',
    summary: asNonEmptyString(row.summary),
    risk_level: asNonEmptyString(row.risk_level ?? row.riskLevel ?? row.risk),
    created_at: asNonEmptyString(row.created_at ?? row.createdAt) ?? '',
  }
}

export function normalizeConnectedWorkSession(raw: unknown): CompanionConnectedWorkSession | null {
  const row = asRecord(raw)
  if (!row) return null
  const sessionId = asNonEmptyString(row.session_id ?? row.sessionId ?? row.id)
  if (!sessionId) return null
  const latestRunObj = asRecord(row.latest_run ?? row.latestRun)
  const latestRunId = asNonEmptyString(latestRunObj?.id ?? row.latest_run_id ?? row.latestRunId ?? row.run_id ?? row.runId)
  const latestRunStatus = asNonEmptyString(latestRunObj?.status ?? row.latest_run_status ?? row.latestRunStatus ?? row.status)
  const latestRunSummary = asNonEmptyString(latestRunObj?.summary ?? row.latest_run_summary ?? row.latestRunSummary ?? row.summary)
  const planSummary = asNonEmptyString(row.plan_summary ?? row.planSummary)
  const planProgress = parseWorkSessionPlanProgress(planSummary, row.plan_progress ?? row.planProgress)
  const rawApprovals = Array.isArray(row.pending_approvals)
    ? row.pending_approvals
    : Array.isArray(row.pendingApprovals)
      ? row.pendingApprovals
      : []
  const pendingApprovals = rawApprovals
    .map(normalizeWorkSessionApproval)
    .filter((item): item is CompanionWorkSessionPendingApproval => item !== null)
  return {
    session_id: sessionId,
    title: asNonEmptyString(row.title ?? row.session_title ?? row.sessionTitle) ?? sessionId.slice(0, 8),
    project_id: asNonEmptyString(row.project_id ?? row.projectId),
    project_name: asNonEmptyString(row.project_name ?? row.projectName),
    mode_name: asNonEmptyString(row.mode_name ?? row.modeName),
    view_mode: asViewMode(row.view_mode ?? row.viewMode),
    latest_run_id: latestRunId,
    latest_run_status: latestRunStatus,
    latest_run_summary: latestRunSummary,
    plan_summary: planSummary,
    plan_progress: planProgress,
    pending_approvals: pendingApprovals,
    browser_space_id: asNonEmptyString(row.browser_space_id ?? row.browserSpaceId ?? row.space_id ?? row.spaceId),
    browser_url: asNonEmptyString(row.browser_url ?? row.browserUrl ?? row.url),
    browser_driver: asNonEmptyString(row.browser_driver ?? row.browserDriver ?? row.driver),
    browser_handoff_reason: asNonEmptyString(row.browser_handoff_reason ?? row.browserHandoffReason ?? row.handoff_reason ?? row.handoffReason),
    updated_at: asNonEmptyString(row.updated_at ?? row.updatedAt),
    latest_run: latestRunId
      ? { id: latestRunId, status: latestRunStatus ?? 'idle', summary: latestRunSummary }
      : null,
  }
}

export function encodeBrowserConnectionEndpoint(driver: CompanionBrowserDriver | string, profileId?: string | null): string {
  const cleanDriver = (driver || 'builtin_electron').trim()
  let cleanProfile = profileId?.trim() || ''
  if (cleanProfile.toLowerCase().startsWith(`${cleanDriver.toLowerCase()}:`)) {
    cleanProfile = cleanProfile.slice(cleanDriver.length + 1).trim()
  }
  return cleanProfile ? `${cleanDriver}:${cleanProfile}` : cleanDriver
}

export function parseBrowserConnectionEndpoint(
  endpoint: string | null | undefined,
  extra?: { driver?: unknown; profile_id?: unknown },
): { driver: CompanionBrowserDriver | string; profileId: string | null } {
  const explicitDriver = asNonEmptyString(extra?.driver)
  const explicitProfile = asNonEmptyString(extra?.profile_id)
  if (explicitDriver) {
    return { driver: explicitDriver, profileId: explicitProfile }
  }
  const raw = asNonEmptyString(endpoint)
  if (!raw) return { driver: 'builtin_electron', profileId: explicitProfile }
  if (raw.startsWith('{')) {
    try {
      const parsed = asRecord(JSON.parse(raw))
      const parsedDriver = asNonEmptyString(parsed?.driver)
      const parsedProfile = asNonEmptyString(parsed?.profile_id ?? parsed?.profileId)
      if (parsedDriver) return { driver: parsedDriver, profileId: parsedProfile }
    } catch { /* fall through to colon parsing */ }
  }
  const colonIdx = raw.indexOf(':')
  if (colonIdx > 0) {
    const driverPart = raw.slice(0, colonIdx).trim()
    const profilePart = raw.slice(colonIdx + 1).trim()
    if (driverPart === 'builtin_electron' || driverPart === 'ego_lite') {
      return { driver: driverPart, profileId: profilePart || null }
    }
  }
  return { driver: raw, profileId: explicitProfile }
}

export function normalizeBrowserProfile(raw: unknown): CompanionBrowserProfile | null {
  const row = asRecord(raw)
  if (!row) return null
  const driver = asNonEmptyString(row.driver) ?? 'builtin_electron'
  let id = asNonEmptyString(row.profile_id ?? row.profileId ?? row.id)
  if (!id) return null
  if (id.toLowerCase().startsWith(`${driver.toLowerCase()}:`)) {
    id = id.slice(driver.length + 1).trim() || id
  }
  return {
    id,
    name: asNonEmptyString(row.name ?? row.display_name ?? row.displayName) ?? id,
    driver,
    is_default: typeof row.is_default === 'boolean' ? row.is_default : typeof row.isDefault === 'boolean' ? row.isDefault : undefined,
    available: typeof row.available === 'boolean' ? row.available : undefined,
    detail: asNonEmptyString(row.detail ?? row.path ?? row.description),
  }
}
