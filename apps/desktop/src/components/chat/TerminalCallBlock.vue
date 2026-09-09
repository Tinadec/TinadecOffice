<script setup lang="ts">
/**
 * TerminalCallBlock — the agent terminal widget shown inside a conversation turn.
 *
 * Shell tool calls render as a small live terminal instead of a text summary:
 * output streams from the Core-owned session, and the two actions that matter
 * when an agent goes off the rails sit on the header — pause/resume of the run
 * and "open in panel", which re-attaches the *same* session to the function
 * panel (replay + follow), where the user has a full-size view and can type to
 * help the agent finish.
 */
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { Pause, Play, Square, ExternalLink, Loader2 } from '@lucide/vue'
import { api, type EventEnvelope } from '@/api'
import { homeController } from '@/controllers/HomeController'
import { useTerminal } from '@/composables/useTerminal'
import { createAgentTerminalSource } from '@/composables/useTerminalSource'
import { useUie } from '@tinadec/ui'
import { useNotifications } from '@/composables/useNotifications'

const props = defineProps<{
  /** Tool execution id (the `shell` tool call's id). */
  executionId: string
  /** Command line, used as the block title. */
  command: string
  /** Optional run id hint; resolved from the event stream when absent. */
  runId?: string | null
  /** Conversation status of the owning tool call. */
  status?: 'running' | 'completed' | 'failed' | 'waiting_approval' | string
}>()

const { notify } = useNotifications()
const { openAgentTerminal } = useTerminal()

const containerRef = ref<HTMLElement | null>(null)
const terminalSessionId = ref<string | null>(null)
const resolvedRunId = ref<string | null>(props.runId ?? null)
const exited = ref(false)
const busy = ref(false)
const paused = ref(false)

let term: Terminal | null = null
let fitAddon: FitAddon | null = null
let source: ReturnType<typeof createAgentTerminalSource> | null = null
let unsubscribeEvents: (() => void) | null = null
const captured: EventEnvelope[] = []
const capturedSeqs = new Set<number>()
let backfilling = false

function payloadOf(event: EventEnvelope): Record<string, unknown> {
  return event.payload && typeof event.payload === 'object' ? (event.payload as Record<string, unknown>) : {}
}

function belongsTo(event: EventEnvelope): boolean {
  return payloadOf(event).execution_id === props.executionId
}

function runIdOf(event: EventEnvelope): string | null {
  const raw = event as unknown as Record<string, unknown>
  const value = raw.run_id ?? payloadOf(event).run_id
  return typeof value === 'string' ? value : null
}

/** Append to the replay buffer, deduped by seq (SSE reconnects can redeliver). */
function capture(event: EventEnvelope): boolean {
  if (capturedSeqs.has(event.seq)) return false
  capturedSeqs.add(event.seq)
  captured.push(event)
  return true
}

/** Correlate this execution with its run and terminal session from the stream. */
function inspect(event: EventEnvelope) {
  if (!event.type.startsWith('terminal.')) return
  if (!belongsTo(event)) return

  capture(event)
  const sessionId = payloadOf(event).terminal_session_id
  if (!terminalSessionId.value && typeof sessionId === 'string') {
    terminalSessionId.value = sessionId
    attachSession(sessionId)
  }
  if (!resolvedRunId.value) {
    const runId = runIdOf(event)
    if (runId) {
      resolvedRunId.value = runId
      if (!terminalSessionId.value) void backfillSession()
    }
  }
}

/**
 * Mount-time association recovery. The live listener only sees events emitted
 * after subscription, so a shell call that started before this block mounted
 * would never produce a terminal_session_id. Recover in two steps: replay the
 * controller's bounded recent-event buffer (same session, no extra request),
 * then ask Core's terminal registry for the session owning this execution.
 */
function seedFromRecentEvents(): void {
  for (const event of homeController.events.value) {
    if (!event.type.startsWith('terminal.')) continue
    if (!belongsTo(event)) continue
    if (!capture(event)) continue
    if (!resolvedRunId.value) {
      const runId = runIdOf(event)
      if (runId) resolvedRunId.value = runId
    }
    const sessionId = payloadOf(event).terminal_session_id
    if (!terminalSessionId.value && typeof sessionId === 'string') {
      terminalSessionId.value = sessionId
    }
  }
}

async function backfillSession(): Promise<void> {
  const runId = resolvedRunId.value
  if (!runId || terminalSessionId.value || backfilling) return
  backfilling = true
  try {
    const sessions = await api.listTerminalSessions(runId)
    const match = sessions.find((session) => session.execution_id === props.executionId)
    if (match) {
      terminalSessionId.value = match.terminal_session_id
      if (!match.live) exited.value = true
      attachSession(match.terminal_session_id)
    }
  } catch {
    // Best effort: a later live event can still associate the session.
  } finally {
    backfilling = false
  }
}

function attachSession(sessionId: string) {
  if (!term || source) return
  source = createAgentTerminalSource({
    terminalSessionId: sessionId,
    runId: resolvedRunId.value ?? undefined,
    feed: {
      subscribe: (handler) => homeController.onEvent(handler),
      replay: () => captured.slice(),
    },
    sendStdin: (id, data) => api.sendTerminalStdin(id, data),
    kill: (id) => api.killTerminalSession(id),
  })
  source.attach({
    onData: (data) => term?.write(data),
    onStatus: (status) => {
      if (status === 'exited' || status === 'killed') exited.value = true
    },
  })
  term.onData((data) => source?.write(data))
}

/** Jump: open the same session as a full-size panel tab. */
function openInPanel() {
  const sessionId = terminalSessionId.value
  if (!sessionId) {
    notify.info('终端会话尚未建立，稍候再试')
    return
  }
  const panelSource = createAgentTerminalSource({
    terminalSessionId: sessionId,
    runId: resolvedRunId.value ?? undefined,
    feed: {
      subscribe: (handler) => homeController.onEvent(handler),
      // The panel has no history of its own: replay everything this block saw
      // plus everything the session emits from now on.
      replay: () => captured.slice(),
    },
    sendStdin: (id, data) => api.sendTerminalStdin(id, data),
    kill: (id) => api.killTerminalSession(id),
  })
  // Registering first makes this session the active tab of the shared terminal
  // list, so opening the card is the whole handoff.
  openAgentTerminal({
    terminalSessionId: sessionId,
    runId: resolvedRunId.value,
    command: props.command,
    title: props.command?.slice(0, 40) || 'Agent terminal',
    source: panelSource,
  })
  try {
    const wb = useUie()
    wb.bus.dispatch({
      command: {
        type: 'openCard',
        scope: wb.scope.value,
        descriptorId: 'terminal',
        slotId: 'right',
        title: props.command?.slice(0, 24) || 'Agent terminal',
      },
      source: 'user',
      expectedRevision: wb.snapshot.value.revision,
    })
  } catch {
    // No layout engine on this host: the session is registered, just not visible.
    notify.info('已加入终端面板，请在右侧功能面板打开「终端」标签查看')
  }
}

async function togglePause() {
  if (!resolvedRunId.value || busy.value) return
  busy.value = true
  try {
    await api.controlRun(resolvedRunId.value, paused.value ? 'resume' : 'pause')
    paused.value = !paused.value
  } catch (err) {
    notify.error(err, { title: '暂停失败' })
  } finally {
    busy.value = false
  }
}

async function stop() {
  if (busy.value) return
  busy.value = true
  try {
    // A long-lived session is killed directly; otherwise stop the run itself.
    if (terminalSessionId.value) {
      await api.killTerminalSession(terminalSessionId.value)
      exited.value = true
      return
    }
    if (resolvedRunId.value) {
      await api.controlRun(resolvedRunId.value, 'cancel')
    }
  } catch (err) {
    notify.error(err, { title: '终止失败' })
  } finally {
    busy.value = false
  }
}

const isRunning = computed(() => props.status === 'running' && !exited.value)

onMounted(() => {
  if (!containerRef.value) return
  term = new Terminal({
    cursorBlink: false,
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', 'Consolas', monospace",
    fontSize: 12,
    lineHeight: 1.25,
    convertEol: true,
    disableStdin: false,
    scrollback: 2000,
    allowProposedApi: true,
  })
  fitAddon = new FitAddon()
  term.loadAddon(fitAddon)
  term.open(containerRef.value)
  term.write(`\x1b[90m$ ${props.command}\x1b[0m\r\n`)
  try {
    fitAddon.fit()
  } catch {
    // Container may not have dimensions yet.
  }

  unsubscribeEvents = homeController.onEvent(inspect)
  seedFromRecentEvents()
  if (terminalSessionId.value) attachSession(terminalSessionId.value)
  else void backfillSession()
})

onBeforeUnmount(() => {
  unsubscribeEvents?.()
  unsubscribeEvents = null
  source?.detach()
  source = null
  try {
    term?.dispose()
  } catch {
    // Ignore double-dispose.
  }
  term = null
  fitAddon = null
})
</script>

<template>
  <div class="terminal-call-block">
    <header class="terminal-call-head">
      <span class="terminal-call-badge">AGENT</span>
      <code class="terminal-call-command">{{ command }}</code>
      <span v-if="isRunning" class="terminal-call-live">
        <Loader2 :size="11" class="terminal-call-spin" />
        运行中
      </span>
      <span v-else-if="exited" class="terminal-call-done">已结束</span>
      <span class="terminal-call-spacer" />
      <button v-if="resolvedRunId && isRunning" class="terminal-call-btn" type="button" @click="togglePause">
        <Pause v-if="!paused" :size="11" />
        <Play v-else :size="11" />
        {{ paused ? '继续' : '暂停' }}
      </button>
      <button v-if="isRunning" class="terminal-call-btn danger" type="button" @click="stop">
        <Square :size="11" />
        终止
      </button>
      <button class="terminal-call-btn" type="button" @click="openInPanel">
        <ExternalLink :size="11" />
        在功能面板打开
      </button>
    </header>
    <div ref="containerRef" class="terminal-call-body" />
  </div>
</template>

<style scoped>
.terminal-call-block {
  border: 1px solid var(--border-default);
  border-radius: 8px;
  overflow: hidden;
  background: var(--bg-primary);
  margin: 6px 0;
}

.terminal-call-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 5px 8px;
  background: var(--bg-secondary);
  border-bottom: 1px solid var(--border-default);
  font-size: 11.5px;
}

.terminal-call-badge {
  padding: 0 5px;
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 0.05em;
  color: var(--accent-primary);
  background: var(--bg-tertiary);
  border-radius: 3px;
  line-height: 15px;
}

.terminal-call-command {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-primary);
  font-size: 11.5px;
}

.terminal-call-live,
.terminal-call-done {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--text-muted);
  font-size: 10.5px;
}

.terminal-call-spin {
  animation: terminal-call-spin 1s linear infinite;
}

@keyframes terminal-call-spin {
  to {
    transform: rotate(360deg);
  }
}

.terminal-call-spacer {
  flex: 1;
}

.terminal-call-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 7px;
  font-size: 10.5px;
  color: var(--text-secondary);
  background: transparent;
  border: 1px solid var(--border-default);
  border-radius: 5px;
  cursor: pointer;
  transition: background 0.12s ease, color 0.12s ease;
}

.terminal-call-btn:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.terminal-call-btn.danger:hover {
  color: var(--text-error);
  border-color: var(--text-error);
}

.terminal-call-body {
  height: 132px;
  padding: 4px 6px;
  background: var(--bg-primary);
}

.terminal-call-body :deep(.xterm-viewport) {
  background-color: var(--bg-primary) !important;
}
</style>
