import type { EventEnvelope, MessageDto } from '@/api'
import { EventScript } from './builder'

/**
 * Named conversation scenarios, shared by the data-layer tests and the Debug Studio
 * "conversation feed" preview. Each one is a full session: messages, run rows and the
 * session SSE events Core would have written for them.
 */
export interface FeedFixture {
  name: string
  messages: MessageDto[]
  runs: { id: string; status: string; trigger_message_id: string | null; created_at: string; updated_at: string }[]
  events: EventEnvelope[]
}

function message(id: string, role: 'user' | 'assistant', content: string, created_at: string, run_id: string | null = null): MessageDto {
  return { id, role, content, created_at, run_id, session_id: 'session-1', attachments: [] } as MessageDto
}

function runRow(id: string, status: string, trigger: string | null, events: EventEnvelope[]) {
  const own = events.filter((e) => e.run_id === id)
  return { id, status, trigger_message_id: trigger, created_at: own[0]?.ts ?? '2026-10-13T09:00:00Z', updated_at: own.at(-1)?.ts ?? '2026-10-13T09:00:00Z' }
}

export function simpleAnswer(): FeedFixture {
  const s = new EventScript()
  s.thought('run-1', 'resp-1', '**Reading the question**\nThe user wants a greeting.')
  const events = s.events
  return {
    name: 'simple-answer',
    messages: [
      message('m-1', 'user', 'Say hello', '2026-10-13T09:00:00Z'),
      message('m-2', 'assistant', 'Hello!', events.at(-1)!.ts, 'run-1'),
    ],
    runs: [runRow('run-1', 'completed', 'm-1', events)],
    events,
  }
}

/** Reads and searches that fold into one exploration group, then an edit and a shell run. */
export function exploreEditShell(): FeedFixture {
  const s = new EventScript()
  s.thought('run-1', 'resp-1', '**Finding the callers**\nLook for useResults first.')
  s.toolCall('run-1', 'e-1', 'file_search', { query: 'useResults', resource_path: '/w/src' }).toolDone('run-1', 'e-1', 'file_search')
  s.toolCall('run-1', 'e-2', 'read_file', { resource_path: '/w/src/a.ts' }).toolDone('run-1', 'e-2', 'read_file')
  s.toolCall('run-1', 'e-3', 'read_file', { resource_path: '/w/src/b.ts' }).toolDone('run-1', 'e-3', 'read_file')
  s.toolCall('run-1', 'e-4', 'ls', { resource_path: '/w/src' }).toolDone('run-1', 'e-4', 'ls')
  s.toolCall('run-1', 'e-5', 'replace_lines', { resource_path: '/w/src/a.ts' }).toolDone('run-1', 'e-5', 'replace_lines')
  s.toolCall('run-1', 'e-6', 'shell', { command: 'npm test' })
  s.emit('run-1', 'terminal.command', { execution_id: 'e-6', task_id: null, tool_id: 'shell', command: 'npm test', cwd: '/w', long_lived: false, timeout_ms: 60000 })
  s.emit('run-1', 'terminal.stdout', { execution_id: 'e-6', task_id: null, tool_id: 'shell', terminal_session_id: 't-1', stream: 'stdout', data: 'PASS a.test.ts\nPASS b.' })
  s.emit('run-1', 'terminal.stdout', { execution_id: 'e-6', task_id: null, tool_id: 'shell', terminal_session_id: 't-1', stream: 'stdout', data: 'test.ts\n' })
  s.emit('run-1', 'terminal.exit', { execution_id: 'e-6', task_id: null, tool_id: 'shell', terminal_session_id: 't-1', exit_code: 0, timed_out: false })
  s.toolDone('run-1', 'e-6', 'shell')
  const events = s.events
  return {
    name: 'explore-edit-shell',
    messages: [
      message('m-1', 'user', 'Fix useResults', '2026-10-13T09:00:00Z'),
      message('m-2', 'assistant', 'Fixed and tested.', events.at(-1)!.ts, 'run-1'),
    ],
    runs: [runRow('run-1', 'completed', 'm-1', events)],
    events,
  }
}

/** A shell call parked on a person; the run has no answer yet. */
export function pendingApproval(): FeedFixture {
  const s = new EventScript()
  s.toolCall('run-1', 'e-1', 'shell', { command: 'git push origin main', requires_approval: true, risk: 'high' })
  s.emit('run-1', 'approval.requested', { approval_id: 'p-1', permission_request_id: 'p-1', execution_id: 'e-1', task_id: null, tool_id: 'shell', risk: 'high', status: 'awaiting_user', lane_key: null })
  const events = s.events
  return {
    name: 'pending-approval',
    messages: [message('m-1', 'user', 'Push it', '2026-10-13T09:00:00Z')],
    runs: [runRow('run-1', 'awaiting_user', 'm-1', events)],
    events,
  }
}

/** The coordinator dispatches two workers in parallel; one asks for approval. */
export function parallelSubAgents(): FeedFixture {
  const s = new EventScript()
  s.thought('run-1', 'resp-1', '**Splitting the work**\nSearch and implement in parallel.')
  s.emit('run-1', 'task.dispatched', { directive_id: 'd-1', dispatch_id: 'task-a', dispatched_by: null, task_key: 'research', title: 'Research the callers of useResults', agent: 'search', follow_up_of: null })
  s.emit('run-1', 'task.dispatched', { directive_id: 'd-2', dispatch_id: 'task-b', dispatched_by: null, task_key: 'implement', title: 'Implement the fix', agent: 'global_engineering', follow_up_of: null })
  s.emit('run-1', 'worker.assigned', { task_id: 'task-a', task_key: 'research', handle: 'search#1', requested_agent: 'search', agent_slug: 'search', reason: 'coordinator_assigned' })
  s.emit('run-1', 'worker.assigned', { task_id: 'task-b', task_key: 'implement', handle: 'global_engineering#1', requested_agent: 'global_engineering', agent_slug: 'global_engineering', reason: 'coordinator_assigned' })
  s.emit('run-1', 'task.wait_started', { run_id: 'run-1', task_id: null, task_key: null, handle: null, call_id: 'w-1', awaited: [{ task_key: 'research', handle: 'search#1', title: 'Research the callers of useResults', agent: 'search', status: 'running' }, { task_key: 'implement', handle: 'global_engineering#1', title: 'Implement the fix', agent: 'global_engineering', status: 'running' }] })
  s.thought('run-1', 'resp-2', 'Look in src/graph.ts', { task_id: 'task-a', handle: 'search#1', agent_name: 'search' })
  s.toolCall('run-1', 'e-1', 'read_file', { task_id: 'task-a', resource_path: '/w/src/graph.ts' })
  s.toolCall('run-1', 'e-2', 'shell', { task_id: 'task-b', command: 'npm run build', requires_approval: true })
  s.emit('run-1', 'approval.requested', { approval_id: 'p-2', permission_request_id: 'p-2', execution_id: 'e-2', task_id: 'task-b', tool_id: 'shell', risk: 'medium', lane_key: null })
  const events = s.events
  return {
    name: 'parallel-sub-agents',
    messages: [message('m-1', 'user', 'Fix useResults across the graph', '2026-10-13T09:00:00Z')],
    runs: [runRow('run-1', 'awaiting_user', 'm-1', events)],
    events,
  }
}

export function failedRun(): FeedFixture {
  const s = new EventScript()
  s.emit('run-1', 'run.model_retry_scheduled', { run_id: 'run-1', retry_number: 1, retry_limit: 3, retry_at: '', delay_ms: 2000, error_category: 'model_unavailable', message: 'busy' })
  s.emit('run-1', 'run.failed', { run_id: 'run-1', error_category: 'model_unavailable', summary: 'The model provider is unavailable.' })
  const events = s.events
  return {
    name: 'failed-run',
    messages: [message('m-1', 'user', 'Anything', '2026-10-13T09:00:00Z')],
    runs: [runRow('run-1', 'failed', 'm-1', events)],
    events,
  }
}

export function interruptedRun(): FeedFixture {
  const s = new EventScript()
  s.thought('run-1', 'resp-1', 'Working on the first plan')
  s.emit('run-1', 'interaction.interrupt_requested', { target_run_id: 'run-1', patch_id: 'patch-1', interrupted_model_calls: 1 })
  s.emit('run-1', 'run.interrupted', { run_id: 'run-1', patch_id: 'patch-1', redone: 'step', partial_characters: 12 })
  const events = s.events
  return {
    name: 'interrupted-run',
    messages: [
      message('m-1', 'user', 'Do A', '2026-10-13T09:00:00Z'),
      message('m-2', 'user', 'Actually do B', events[3]!.ts),
    ],
    runs: [runRow('run-1', 'running', 'm-1', events)],
    events,
  }
}

export function compactedRun(): FeedFixture {
  const s = new EventScript()
  s.thought('run-1', 'resp-1', 'Long session')
  s.emit('run-1', 'context.compacted', { run_id: 'run-1', agent_slug: 'meeting', trigger_point: 'task_completed', patch_id: 'p', base_context_revision: 3, context_revision: 4 })
  const events = s.events
  return {
    name: 'compacted-run',
    messages: [
      message('m-1', 'user', 'Keep going', '2026-10-13T09:00:00Z'),
      message('m-2', 'assistant', 'Done.', events.at(-1)!.ts, 'run-1'),
    ],
    runs: [runRow('run-1', 'completed', 'm-1', events)],
    events,
  }
}

/**
 * A session carried over to the next morning: yesterday's turn answered twice (a short interim
 * note, then the reply), today's turn stopped by the user — a date line, a compact note, retry.
 */
export function nextDayStopped(): FeedFixture {
  const s = new EventScript('session-1', Date.parse('2026-10-12T15:00:00Z'))
  s.thought('run-1', 'resp-1', '**Checking the tests**\nRun them before touching anything.')
  s.toolCall('run-1', 'e-1', 'shell', { command: 'npm test' }).toolDone('run-1', 'e-1', 'shell')
  const interimAt = s.events.at(-1)!.ts
  s.toolCall('run-1', 'e-2', 'replace_lines', { resource_path: '/w/src/a.ts' }).toolDone('run-1', 'e-2', 'replace_lines')
  const doneAt = s.events.at(-1)!.ts
  const today = new EventScript('session-1', Date.parse('2026-10-13T09:00:00Z'))
  today.thought('run-2', 'resp-2', '**Planning the rename**\nList every caller first.')
  const events = [...s.events, ...today.events]
  return {
    name: 'next-day-stopped',
    messages: [
      message('m-1', 'user', 'Fix the flaky test', '2026-10-12T15:00:00Z'),
      message('m-2', 'assistant', 'The tests pass locally; the flake is a timer. Fixing that next.', interimAt, 'run-1'),
      message('m-3', 'assistant', 'Fixed: the test now waits on the fake clock instead of real time.', doneAt, 'run-1'),
      message('m-4', 'user', 'Now rename useResults to useSearchResults', '2026-10-13T09:00:00Z'),
    ],
    runs: [runRow('run-1', 'completed', 'm-1', s.events), runRow('run-2', 'cancelled', 'm-4', today.events)],
    events,
  }
}

/** An inline image, so the fixture needs no network: a small labelled chart. */
const CHART_IMAGE = 'data:image/svg+xml;utf8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="120"><rect width="320" height="120" rx="8" fill="#2d6cdf"/><rect x="24" y="60" width="40" height="40" fill="#fff"/><rect x="84" y="40" width="40" height="60" fill="#fff"/><rect x="144" y="24" width="40" height="76" fill="#fff"/><text x="204" y="70" fill="#fff" font-family="sans-serif" font-size="16">p95 latency</text></svg>')

/**
 * Answers that carry more than prose: a short interim answer that is only a diagram, then a
 * reply with a Markdown image, a mermaid flowchart and a table.
 */
export function richAnswer(): FeedFixture {
  const s = new EventScript()
  s.thought('run-1', 'resp-1', '**Sketching the flow**\nShow it before describing it.')
  const sketchAt = s.events.at(-1)!.ts
  s.toolCall('run-1', 'e-1', 'read_file', { resource_path: '/w/src/send.ts' }).toolDone('run-1', 'e-1', 'read_file')
  const events = s.events
  return {
    name: 'rich-answer',
    messages: [
      message('m-1', 'user', 'How does a message get sent?', '2026-10-13T09:00:00Z'),
      message('m-2', 'assistant', '```mermaid\nflowchart LR\n  Composer --> Gateway --> Core\n```', sketchAt, 'run-1'),
      message('m-3', 'assistant', [
        'The composer posts an interaction; Core admits it and starts a run.',
        '',
        `![Send latency](${CHART_IMAGE})`,
        '',
        '```mermaid',
        'flowchart TD',
        '  A[Composer] -->|POST /interactions| B(Gateway)',
        '  B --> C{Core admits?}',
        '  C -->|yes| D[Run starts]',
        '  C -->|queued| E[Waits in queue]',
        '```',
        '',
        '| Step | Owner |',
        '| --- | --- |',
        '| Admit | Core |',
        '| Stream | Gateway |',
      ].join('\n'), events.at(-1)!.ts, 'run-1'),
    ],
    runs: [runRow('run-1', 'completed', 'm-1', events)],
    events,
  }
}

export const FEED_FIXTURES = [simpleAnswer, exploreEditShell, pendingApproval, parallelSubAgents, failedRun, interruptedRun, compactedRun, nextDayStopped, richAnswer]
