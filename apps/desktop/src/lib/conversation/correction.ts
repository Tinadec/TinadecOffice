export type CorrectionPhase = 'OBSERVE' | 'INTERVENE' | 'CONVERGE' | 'BACKTRACK'

export interface CorrectionHypothesis {
  id: string
  statement: string
  status: 'testing' | 'confirmed' | 'rejected'
  killCriterion: string | null
  evidenceIds: string[]
  confidence: string
}

export interface CorrectionEvidence {
  id: string
  source: string
  kind: string
  strength: string
  summary: string
  executionId: string | null
  toolId: string | null
  path: string | null
  createdAt: string | null
}

export interface CorrectionIntervention {
  id: string
  executionId: string | null
  toolId: string | null
  path: string | null
  summary: string
  revertible: boolean
  revertedAt: string | null
}

export interface CorrectionVerification {
  executionId: string | null
  toolId: string | null
  command: string | null
  exitCode: number | null
  succeeded: boolean
  summary: string
}

export interface CorrectionCompletion {
  requirement: 'test' | 'diagnostics' | 'evidence' | 'user'
  authority: 'machine' | 'user'
  status: string
  modifiedArtifacts: string[]
  verifiedArtifacts: string[]
  strongestCheck: CorrectionVerification | null
  failedBeforeExecutionId: string | null
  passedAfterExecutionId: string | null
  unresolvedReason: string | null
  verifiedAt: string | null
}

export interface CorrectionQuestion {
  id: string
  question: string
  blocking: boolean
  options: string[]
  answer: string | null
  askedAt: string | null
  answeredAt: string | null
}

export interface CorrectionTrace {
  episodeId: string
  runId: string
  parentRunId: string | null
  revision: number
  phase: CorrectionPhase
  status: string
  goal: string
  symptom: string
  doneWhen: string
  summary: string
  reason: string
  rootCause: string
  recheck: string | null
  confidence: string
  intervention: string | null
  prediction: string | null
  verdict: string | null
  fix: string | null
  verify: string | null
  hypotheses: CorrectionHypothesis[]
  evidence: CorrectionEvidence[]
  interventions: CorrectionIntervention[]
  completion: CorrectionCompletion
  openQuestions: CorrectionQuestion[]
  createdAt: string | null
  updatedAt: string | null
}

type Row = Record<string, unknown>

export function readCorrectionTrace(payload: Row): CorrectionTrace | null {
  const raw = payload.trace
  if (!raw || typeof raw !== 'object') return null
  const trace = raw as Row
  const episodeId = text(trace.episode_id) ?? text(trace.episodeId)
  if (!episodeId) return null
  return {
    episodeId,
    runId: text(trace.run_id) ?? text(trace.runId) ?? '',
    parentRunId: text(trace.parent_run_id) ?? text(trace.parentRunId),
    revision: integer(trace.revision) ?? 0,
    phase: phase(trace.phase),
    status: text(trace.status) ?? 'investigating',
    goal: text(trace.goal) ?? '',
    symptom: text(trace.symptom) ?? '',
    doneWhen: text(trace.done_when) ?? text(trace.doneWhen) ?? '',
    summary: text(trace.summary) ?? '',
    reason: text(trace.reason) ?? '',
    rootCause: text(trace.root_cause) ?? text(trace.rootCause) ?? 'UNKNOWN',
    recheck: text(trace.recheck),
    confidence: text(trace.confidence) ?? 'low',
    intervention: text(trace.intervention),
    prediction: text(trace.prediction),
    verdict: text(trace.verdict),
    fix: text(trace.fix),
    verify: text(trace.verify),
    hypotheses: rows(trace.hypotheses).map((row) => ({
      id: text(row.id) ?? '',
      statement: text(row.statement) ?? '',
      status: (['testing', 'confirmed', 'rejected'].includes(String(row.status)) ? row.status : 'testing') as CorrectionHypothesis['status'],
      killCriterion: text(row.kill_criterion) ?? text(row.killCriterion),
      evidenceIds: strings(row.evidence_ids ?? row.evidenceIds),
      confidence: text(row.confidence) ?? 'low',
    })).filter((row) => row.id && row.statement),
    evidence: rows(trace.evidence).map((row) => ({
      id: text(row.id) ?? '',
      source: text(row.source) ?? 'tool',
      kind: text(row.kind) ?? 'observation',
      strength: text(row.strength) ?? 'inference',
      summary: text(row.summary) ?? '',
      executionId: text(row.execution_id) ?? text(row.executionId),
      toolId: text(row.tool_id) ?? text(row.toolId),
      path: text(row.path),
      createdAt: text(row.created_at) ?? text(row.createdAt),
    })).filter((row) => row.id),
    interventions: rows(trace.interventions).map((row) => ({
      id: text(row.id) ?? '',
      executionId: text(row.execution_id) ?? text(row.executionId),
      toolId: text(row.tool_id) ?? text(row.toolId),
      path: text(row.path),
      summary: text(row.summary) ?? '',
      revertible: row.revertible === true,
      revertedAt: text(row.reverted_at) ?? text(row.revertedAt),
    })).filter((row) => row.id),
    completion: completion(trace.completion),
    openQuestions: rows(trace.open_questions ?? trace.openQuestions).map((row) => ({
      id: text(row.id) ?? '',
      question: text(row.question) ?? '',
      blocking: row.blocking === true,
      options: strings(row.options),
      answer: text(row.answer),
      askedAt: text(row.asked_at) ?? text(row.askedAt),
      answeredAt: text(row.answered_at) ?? text(row.answeredAt),
    })).filter((row) => row.id && row.question),
    createdAt: text(trace.created_at) ?? text(trace.createdAt),
    updatedAt: text(trace.updated_at) ?? text(trace.updatedAt),
  }
}

function completion(raw: unknown): CorrectionCompletion {
  const row = raw && typeof raw === 'object' ? raw as Row : {}
  const repro = row.repro && typeof row.repro === 'object' ? row.repro as Row : {}
  const strongest = row.strongest_check ?? row.strongestCheck
  const check = strongest && typeof strongest === 'object' ? strongest as Row : null
  const requirement = ['test', 'diagnostics', 'evidence', 'user'].includes(String(row.requirement))
    ? row.requirement as CorrectionCompletion['requirement'] : 'diagnostics'
  return {
    requirement,
    authority: row.authority === 'user' ? 'user' : 'machine',
    status: text(row.status) ?? 'blocked',
    modifiedArtifacts: strings(row.modified_artifacts ?? row.modifiedArtifacts),
    verifiedArtifacts: strings(row.verified_artifacts ?? row.verifiedArtifacts),
    strongestCheck: check ? {
      executionId: text(check.execution_id) ?? text(check.executionId),
      toolId: text(check.tool_id) ?? text(check.toolId),
      command: text(check.command),
      exitCode: integer(check.exit_code) ?? integer(check.exitCode),
      succeeded: check.succeeded === true,
      summary: text(check.summary) ?? '',
    } : null,
    failedBeforeExecutionId: text(repro.failed_before_execution_id) ?? text(repro.failedBeforeExecutionId),
    passedAfterExecutionId: text(repro.passed_after_execution_id) ?? text(repro.passedAfterExecutionId),
    unresolvedReason: text(row.unresolved_reason) ?? text(row.unresolvedReason),
    verifiedAt: text(row.verified_at) ?? text(row.verifiedAt),
  }
}

function phase(value: unknown): CorrectionPhase {
  return ['OBSERVE', 'INTERVENE', 'CONVERGE', 'BACKTRACK'].includes(String(value))
    ? value as CorrectionPhase : 'OBSERVE'
}

function rows(value: unknown): Row[] {
  return Array.isArray(value)
    ? value.filter((row): row is Row => Boolean(row) && typeof row === 'object' && !Array.isArray(row))
    : []
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string' && item.length > 0) : []
}

function text(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null
}

function integer(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? Math.trunc(value) : null
}
