/**
 * The event names Core actually emits, in one place.
 *
 * Core writes NAMED SSE frames (`event: <type>`), and a browser only dispatches a
 * named frame to a listener registered for exactly that name — every other frame is
 * dropped silently. Subscribing therefore has to use the real names, and the
 * previous 18-name list did not: eleven of them are names Core has never sent
 * (`project.created`, `session.created`, `message.created`, `approval.approved`,
 * `approval.rejected`, `tool.shell.approval_required`, `run.started`,
 * `task.assigned`, `step.result.created`, `supervision.checked`,
 * `context.pack.created`), while the names Core really uses for those facts
 * (`approval.decided`, `tool.execution.*`, `run.failed`, `worker.*`) had no listener
 * at all. That is why a tool timeline, an approval decision, and every run failure
 * were invisible in the UI no matter what Core had durably recorded.
 *
 * Rule for new events: add the name here first, then subscribe. The event producer
 * lives under `TinadecCore/`; grep the literal before adding an entry, and delete
 * the entry when the producer goes away. Subscribing to something Core never sends
 * is pure dead weight, and it hides the fact that a real fact is unrendered.
 */

/** Run and task lifecycle. */
export const RUN_EVENT_TYPES = [
  'task.accepted',
  'task_graph.created',
  'task.cancelled',
  'interaction.created',
  'run.queued',
  'run.failed',
  'run.recovered',
  'run.paused',
  'run.resumed',
  'user.response',
] as const

/** Execution-layer instances and their tool loop. */
export const WORKER_EVENT_TYPES = [
  'agent.created',
  'worker.assigned',
  'worker.completed',
  'worker.failed',
  // A dispatch that failed and was handed back to the model as a tool result,
  // rather than ending the task.
  'worker.tool_failed',
  'worker.tool_round',
  'worker.budget_exhausted',
] as const

/** Tool dispatch outcomes and the terminal stream. */
export const TOOL_EVENT_TYPES = [
  'tool.execution.requested',
  'tool.execution.completed',
  'tool.execution.failed',
  'tool.execution.outcome_unknown',
  'tool.execution.recovery_decided',
  'terminal.command',
  'terminal.stdout',
  'terminal.exit',
  'terminal.stdin',
  'terminal.session.killed',
] as const

/** Approvals: the tool-approval gate, the PDP park, and their outcomes. */
export const APPROVAL_EVENT_TYPES = [
  'approval.requested',
  'approval.decided',
  'approval.auto_decided',
  'approval.park_expired',
  'approval.pre_authorized_minted',
  // "Always allow for this session": the run-scoped envelopes a decision minted.
  'approval.run_scope_granted',
  'governance.permission_decided',
] as const

/** Supervision, context, orchestration, and operational roles. */
export const ORCHESTRATION_EVENT_TYPES = [
  'supervision.requested',
  'supervision.completed',
  'supervision.skipped',
  'supervision.user_review.requested',
  'context.packed',
  'context.patch.accepted',
  'context.patch.stale',
  'context.compacted',
  'orchestration.mode_tier_decided',
  'orchestration.directive.rejected',
  'capability.recommended',
  'memory.candidate_created',
  'git.steward.reviewed',
  'session.workspace_bound',
] as const

/**
 * Every Core event name this client subscribes to. Typed as a mutable string array
 * because the subscription API takes names, but the source lists above stay `as
 * const` so a typo is a compile error at the definition.
 */
export const CORE_EVENT_TYPES: string[] = [
  ...RUN_EVENT_TYPES,
  ...WORKER_EVENT_TYPES,
  ...TOOL_EVENT_TYPES,
  ...APPROVAL_EVENT_TYPES,
  ...ORCHESTRATION_EVENT_TYPES,
]
