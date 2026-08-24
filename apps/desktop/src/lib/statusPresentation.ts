/**
 * Canonical status presentation for Core-owned state machines.
 *
 * Colors are semantic, not decorative: green only for settled success, purple
 * exclusively for `outcome_unknown` (the one state that demands a human
 * decision), blue family for the three waiting-for-decision states.
 */
export type StatusTone =
  | 'ok'
  | 'danger'
  | 'warn'
  | 'info'
  | 'info-soft'
  | 'recovery'
  | 'running'
  | 'queued'
  | 'neutral'

const RUN_TONES: Readonly<Record<string, StatusTone>> = Object.freeze({
  planning: 'queued',
  understanding: 'queued',
  executing: 'running',
  replanning: 'warn',
  awaiting_approval: 'info',
  paused: 'neutral',
  reviewing: 'info-soft',
  completed: 'ok',
  failed: 'danger',
  cancelled: 'neutral',
})

const USER_ACTION_TONES: Readonly<Record<string, StatusTone>> = Object.freeze({
  requested: 'queued',
  snapshot_required: 'info-soft',
  awaiting_delegate: 'info',
  awaiting_user: 'info',
  awaiting_approval: 'info',
  running: 'running',
  completed: 'ok',
  failed: 'danger',
  blocked: 'warn',
  outcome_unknown: 'recovery',
})

export function runStatusTone(status: string): StatusTone {
  return RUN_TONES[status] ?? 'neutral'
}

export function userActionStatusTone(status: string): StatusTone {
  return USER_ACTION_TONES[status] ?? 'neutral'
}

/** i18n-free fallback label; pages pass localized labels when available. */
export function statusLabel(status: string): string {
  return status.replaceAll('_', ' ')
}
