import { computed, ref, toValue, type ComputedRef, type MaybeRefOrGetter } from 'vue'

export interface ObservabilityTurnEntry {
  runId: string
  turnNo: number
  status: string
  isRunning: boolean
  isFailed: boolean
  durationMs: number | null
  startedAt: string | null
  completedAt: string | null
  headline?: string | null
}

export interface UseObservabilityRunSelectionOptions {
  sessionId: MaybeRefOrGetter<string | null | undefined>
  runs: MaybeRefOrGetter<readonly unknown[] | null | undefined>
  activeRunId?: MaybeRefOrGetter<string | null | undefined>
}

export interface UseObservabilityRunSelectionReturn {
  turns: ComputedRef<ObservabilityTurnEntry[]>
  pinnedRunId: ComputedRef<string | null>
  isFollowingLatest: ComputedRef<boolean>
  effectiveRunId: ComputedRef<string | null>
  selectedTurn: ComputedRef<ObservabilityTurnEntry | null>
  selectRun: (runId: string | null) => void
  selectTurnByNo: (turnNo: number) => void
  stepTurn: (delta: -1 | 1) => void
  followLatest: () => void
}

const DEFAULT_SESSION_KEY = '__default__'

const TERMINAL_RUN_STATUSES = new Set([
  'completed',
  'succeeded',
  'success',
  'done',
  'failed',
  'error',
  'cancelled',
  'canceled',
  'aborted',
  'interrupted',
  'stopped',
  'timed_out',
  'timeout',
])

const FAILED_RUN_STATUSES = new Set([
  'failed',
  'error',
  'aborted',
  'timed_out',
  'timeout',
])

/**
 * Shared module-level reactive map keyed by normalized sessionId so switching a turn
 * in LiveDigestPanel, TrajectoryWorkbenchPanel, or OrchestrationTab synchronizes all three.
 */
const pinnedRunBySession = ref<Record<string, string | null>>({})

function normalizeSessionKey(raw: string | null | undefined): string {
  if (typeof raw !== 'string') return DEFAULT_SESSION_KEY
  const trimmed = raw.trim()
  if (!trimmed) return DEFAULT_SESSION_KEY
  const splitIdx = trimmed.indexOf('::')
  const bare = splitIdx >= 0 ? trimmed.slice(splitIdx + 2).trim() : trimmed
  return bare || DEFAULT_SESSION_KEY
}

function normalizeString(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  return trimmed.length > 0 ? trimmed : null
}

function normalizeRunId(value: unknown): string | null {
  const raw = normalizeString(value)
  if (!raw) return null
  const splitIdx = raw.indexOf('::')
  return splitIdx >= 0 ? (raw.slice(splitIdx + 2).trim() || raw) : raw
}

interface ParsedCandidateRun {
  runId: string
  status: string
  isRunning: boolean
  isFailed: boolean
  durationMs: number | null
  startedAt: string | null
  completedAt: string | null
  headline: string | null
  startEpoch: number
  originalIndex: number
}

function parseDurationMs(
  record: Record<string, unknown>,
  startedAt: string | null,
  endAt: string | null,
): number | null {
  const explicit = record.duration_ms ?? record.durationMs
  if (typeof explicit === 'number' && Number.isFinite(explicit) && explicit >= 0) {
    return Math.round(explicit)
  }
  if (startedAt && endAt) {
    const startMs = Date.parse(startedAt)
    const endMs = Date.parse(endAt)
    if (Number.isFinite(startMs) && Number.isFinite(endMs) && endMs >= startMs) {
      return Math.round(endMs - startMs)
    }
  }
  return null
}

function parseRunEntry(
  raw: unknown,
  index: number,
  activeRunId: string | null,
): ParsedCandidateRun | null {
  if (!raw || typeof raw !== 'object') return null
  const record = raw as Record<string, unknown>
  const runId = normalizeRunId(record.id ?? record.run_id ?? record.runId)
  if (!runId) return null

  const status = normalizeString(record.status) ?? 'unknown'
  const statusLower = status.toLowerCase()
  const isFailed = FAILED_RUN_STATUSES.has(statusLower)
  const isTerminal = TERMINAL_RUN_STATUSES.has(statusLower)
  const isRunning = !isTerminal || (activeRunId !== null && runId === activeRunId && !isTerminal)

  const startedAt = normalizeString(
    record.started_at ?? record.startedAt ?? record.created_at ?? record.createdAt,
  )
  const updatedAt = normalizeString(record.updated_at ?? record.updatedAt)
  const completedAt = normalizeString(
    record.completed_at ?? record.completedAt ?? (isTerminal ? updatedAt : null),
  )
  const durationMs = parseDurationMs(record, startedAt, completedAt ?? (isTerminal ? updatedAt : null))
  const headline = normalizeString(record.headline ?? record.summary ?? record.title ?? record.goal)
  const parsedEpoch = startedAt ? Date.parse(startedAt) : Number.NaN

  return {
    runId,
    status,
    isRunning,
    isFailed,
    durationMs,
    startedAt,
    completedAt,
    headline,
    startEpoch: Number.isFinite(parsedEpoch) ? parsedEpoch : Number.NaN,
    originalIndex: index,
  }
}

function buildOrderedTurns(
  rawRuns: readonly unknown[] | null | undefined,
  activeRunId: string | null,
): ObservabilityTurnEntry[] {
  const deduped = new Map<string, ParsedCandidateRun>()
  if (Array.isArray(rawRuns)) {
    for (let i = 0; i < rawRuns.length; i += 1) {
      const parsed = parseRunEntry(rawRuns[i], i, activeRunId)
      if (!parsed) continue
      const existing = deduped.get(parsed.runId)
      if (!existing) {
        deduped.set(parsed.runId, parsed)
      } else {
        deduped.set(parsed.runId, {
          ...existing,
          status: parsed.status !== 'unknown' ? parsed.status : existing.status,
          isRunning: parsed.isRunning,
          isFailed: parsed.isFailed,
          durationMs: parsed.durationMs ?? existing.durationMs,
          startedAt: parsed.startedAt ?? existing.startedAt,
          completedAt: parsed.completedAt ?? existing.completedAt,
          headline: parsed.headline ?? existing.headline,
          startEpoch: Number.isFinite(parsed.startEpoch) ? parsed.startEpoch : existing.startEpoch,
        })
      }
    }
  }

  if (activeRunId && !deduped.has(activeRunId)) {
    deduped.set(activeRunId, {
      runId: activeRunId,
      status: 'running',
      isRunning: true,
      isFailed: false,
      durationMs: null,
      startedAt: null,
      completedAt: null,
      headline: null,
      startEpoch: Number.NaN,
      originalIndex: deduped.size,
    })
  }

  const candidates = [...deduped.values()]
  const hasAnyTimestamp = candidates.some((item) => Number.isFinite(item.startEpoch))

  if (hasAnyTimestamp) {
    candidates.sort((a, b) => {
      const aHas = Number.isFinite(a.startEpoch)
      const bHas = Number.isFinite(b.startEpoch)
      if (aHas && bHas) {
        if (a.startEpoch !== b.startEpoch) return a.startEpoch - b.startEpoch
        return a.originalIndex - b.originalIndex
      }
      if (aHas && !bHas) {
        // Optimistic newly-admitted runs lack timestamps until listRuns refreshes; place them at the tail.
        return b.isRunning ? -1 : a.originalIndex - b.originalIndex
      }
      if (!aHas && bHas) {
        return a.isRunning ? 1 : a.originalIndex - b.originalIndex
      }
      return a.originalIndex - b.originalIndex
    })
  }

  return candidates.map((item, idx) => ({
    runId: item.runId,
    turnNo: idx + 1,
    status: item.status,
    isRunning: item.isRunning,
    isFailed: item.isFailed,
    durationMs: item.durationMs,
    startedAt: item.startedAt,
    completedAt: item.completedAt,
    headline: item.headline,
  }))
}

export function useObservabilityRunSelection(
  options: UseObservabilityRunSelectionOptions,
): UseObservabilityRunSelectionReturn {
  const sessionKey = computed(() => normalizeSessionKey(toValue(options.sessionId)))
  const normalizedActiveRunId = computed(() => normalizeRunId(toValue(options.activeRunId)))

  const turns = computed<ObservabilityTurnEntry[]>(() =>
    buildOrderedTurns(toValue(options.runs), normalizedActiveRunId.value),
  )

  const pinnedRunId = computed<string | null>(() => {
    const rawPinned = pinnedRunBySession.value[sessionKey.value] ?? null
    if (!rawPinned) return null
    if (turns.value.length > 0 && !turns.value.some((turn) => turn.runId === rawPinned)) {
      return null
    }
    return rawPinned
  })

  const isFollowingLatest = computed<boolean>(() => pinnedRunId.value === null)

  const latestRunId = computed<string | null>(() => {
    const active = normalizedActiveRunId.value
    if (active && (turns.value.length === 0 || turns.value.some((turn) => turn.runId === active))) {
      return active
    }
    const list = turns.value
    for (let i = list.length - 1; i >= 0; i -= 1) {
      if (list[i]!.isRunning) return list[i]!.runId
    }
    return list.length > 0 ? list[list.length - 1]!.runId : active
  })

  const effectiveRunId = computed<string | null>(() => pinnedRunId.value ?? latestRunId.value)

  const selectedTurn = computed<ObservabilityTurnEntry | null>(() => {
    const targetId = effectiveRunId.value
    if (!targetId) return null
    return turns.value.find((turn) => turn.runId === targetId) ?? null
  })

  function selectRun(runId: string | null): void {
    const normalized = normalizeRunId(runId)
    pinnedRunBySession.value = {
      ...pinnedRunBySession.value,
      [sessionKey.value]: normalized,
    }
  }

  function followLatest(): void {
    selectRun(null)
  }

  function selectTurnByNo(turnNo: number): void {
    if (!Number.isFinite(turnNo)) return
    const target = turns.value.find((turn) => turn.turnNo === Math.trunc(turnNo))
    if (target) {
      selectRun(target.runId)
    }
  }

  function stepTurn(delta: -1 | 1): void {
    const list = turns.value
    if (list.length === 0) return
    const currentId = effectiveRunId.value
    const foundIdx = currentId ? list.findIndex((turn) => turn.runId === currentId) : list.length - 1
    const baseIdx = foundIdx >= 0 ? foundIdx : list.length - 1
    const nextIdx = baseIdx + delta
    if (nextIdx >= 0 && nextIdx < list.length) {
      selectRun(list[nextIdx]!.runId)
    }
  }

  return {
    turns,
    pinnedRunId,
    isFollowingLatest,
    effectiveRunId,
    selectedTurn,
    selectRun,
    selectTurnByNo,
    stepTurn,
    followLatest,
  }
}

export function __resetObservabilityRunSelectionForTests(): void {
  pinnedRunBySession.value = {}
}
