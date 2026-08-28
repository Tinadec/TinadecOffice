import { ref, computed } from 'vue'
import { defineStore } from 'pinia'
import { generatedApi, type RunDto, type SseChunk, RUN_STATUSES } from '@/generated/client'

function gatewayUrl(): string {
  const w = window as unknown as { tinadec?: { gatewayUrl?: () => string } }
  return w.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
}

export type InvokeParams = {
  content: string
  client_message_id: string
  application_mode: string
  agent_mode: string
  permission_mode: string
  target_run_id?: string | null
  expected_context_revision?: number | null
}

export const useRunStore = defineStore('run', () => {
  const runs = ref<RunDto[]>([])
  const selectedRunId = ref<string | null>(null)
  const status = ref<string>('idle')
  const invoking = ref(false)
  const cursor = ref<number | null>(null)
  const error = ref<string | null>(null)
  const deltas = ref<Map<string, string>>(new Map())
  const seen = new Set<string>()

  const current = computed(() => runs.value.find(r => r.id === selectedRunId.value) ?? runs.value[0] ?? null)

  async function fetchRuns(sessionId: string) {
    try { runs.value = await generatedApi.listRuns(sessionId) } catch (e) { error.value = e instanceof Error ? e.message : String(e) }
    if (selectedRunId.value && !runs.value.find(r => r.id === selectedRunId.value)) selectedRunId.value = runs.value[0]?.id ?? null
    if (!selectedRunId.value) selectedRunId.value = runs.value[0]?.id ?? null
  }

  function dedupKey(c: SseChunk): string { return `${c.run_id}:${c.seq}` }

  function handleChunk(c: SseChunk) {
    const key = dedupKey(c)
    if (seen.has(key)) return
    seen.add(key)
    cursor.value = c.seq
    if (c.kind === 'heartbeat') return
    if (c.kind === 'ack') {
      status.value = 'understanding'
      if (c.run_id && !runs.value.find(r => r.id === c.run_id)) {
        runs.value = [{ id: c.run_id, session_id: '', trigger_message_id: c.message_id, status: 'understanding', summary: null, task_revision: null, latest_event_sequence: null, latest_event_at: null, created_at: c.occurred_at, updated_at: c.occurred_at, completed_at: null }, ...runs.value]
        selectedRunId.value = c.run_id
      }
      // optimistic placeholder
      return
    }
    if (c.kind === 'delta') {
      const delta = String((c.payload.delta as string) ?? '')
      if (delta) {
        const cur = deltas.value.get(c.run_id) ?? ''
        deltas.value.set(c.run_id, cur + delta)
      }
      status.value = 'executing'
      return
    }
    if (c.kind === 'done') { status.value = 'completed'; return }
    if (c.kind === 'error') {
      const cat = String((c.payload.error_category as string) ?? (c.payload as Record<string,unknown>).code ?? '')
      if (cat === 'model_not_configured') status.value = 'model_not_configured'
      else if (cat === 'permission_denied' || cat === 'forbidden') status.value = 'permission_denied'
      else if (cat === 'recovering' || cat === 'recovering_state') status.value = 'recovering'
      else status.value = 'failed'
      error.value = String((c.payload.safe_error_message as string) ?? (c.payload as Record<string,unknown>).message ?? cat ?? 'error')
      return
    }
    if (c.kind === 'task_node_update' || c.kind === 'supervision_update' || c.kind === 'context_version_update') {
      // workbench will react; just bump cursor
      return
    }
  }

  async function invoke(sessionId: string, params: InvokeParams, onChunk?: (c: SseChunk) => void): Promise<string | null> {
    invoking.value = true; error.value = null; status.value = 'understanding'
    const ac = new AbortController()
    let runId: string | null = params.target_run_id ?? null
    try {
      const res = await fetch(`${gatewayUrl()}/api/v1/sessions/${encodeURIComponent(sessionId)}/invoke-stream`, {
        method: 'POST',
        headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
        body: JSON.stringify(params),
        signal: ac.signal,
      })
      if (!res.ok) {
        const text = await res.text().catch(() => '')
        let msg = text
        try { const j = JSON.parse(text); msg = (j.message as string) ?? (j.error as string) ?? text } catch {}
        // map explicit states
        if (res.status === 401 || res.status === 403) status.value = 'permission_denied'
        else if (res.status === 409) status.value = 'recovering'
        else if (res.status === 503 || msg.includes('model_not_configured')) status.value = 'model_not_configured'
        else status.value = 'failed'
        error.value = msg || `HTTP ${res.status}`
        if (!navigator.onLine || msg.includes('Cannot connect')) status.value = 'disconnected'
        throw new Error(error.value)
      }
      const reader = res.body?.getReader()
      if (!reader) throw new Error('No SSE body')
      const decoder = new TextDecoder()
      let buffer = ''
      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        let idx: number
        while ((idx = buffer.indexOf('\n\n')) !== -1) {
          const block = buffer.slice(0, idx); buffer = buffer.slice(idx + 2)
          if (!block.trim() || block.startsWith(':')) continue
          let id: string | null = null, kind: string | null = null, data = ''
          for (const line of block.split('\n')) {
            if (line.startsWith('id:')) id = line.slice(3).trim()
            else if (line.startsWith('event:')) kind = line.slice(7).trim()
            else if (line.startsWith('data:')) data += line.slice(5).trim()
          }
          if (!data) continue
          try {
            const obj = JSON.parse(data) as Record<string, unknown>
            const chunk: SseChunk = {
              run_id: String((obj.run_id as string) ?? runId ?? ''),
              turn_id: (obj.turn_id as string) ?? null,
              message_id: (obj.message_id as string) ?? null,
              seq: Number((obj.seq as number) ?? id ?? 0),
              kind: String((obj.kind as string) ?? kind ?? 'delta'),
              occurred_at: (obj.occurred_at as string) ?? new Date().toISOString(),
              payload: (obj.payload as Record<string, unknown>) ?? obj,
            }
            if (chunk.kind === 'heartbeat') { cursor.value = chunk.seq; continue }
            handleChunk(chunk)
            onChunk?.(chunk)
            if (chunk.run_id) runId = chunk.run_id
            if (chunk.kind === 'done' || chunk.kind === 'error') { invoking.value = false }
          } catch {}
        }
      }
      return runId
    } catch (e) {
      if ((e as DOMException).name === 'AbortError') return runId
      if (!error.value) { error.value = e instanceof Error ? e.message : String(e); if (!navigator.onLine) status.value = 'disconnected' }
      throw e
    } finally { invoking.value = false }
  }

  async function control(runId: string, action: 'cancel'|'pause'|'resume', expectedContextRevision?: number | null) {
    const body: Record<string, unknown> = { action }
    if (expectedContextRevision != null) body.expected_context_revision = expectedContextRevision
    await generatedApi.controlRun(runId, body)
    await fetchRuns(current.value?.session_id ?? '')
  }

  function select(id: string) { selectedRunId.value = id }

  // normalize 10 states helper
  function isTerminal(s: string): boolean { return ['completed','failed','cancelled'].includes(s) }

  return { runs, selectedRunId, current, status, invoking, cursor, error, deltas, seen: seen as Set<string>, fetchRuns, invoke, control, select, handleChunk, isTerminal, RUN_STATUSES }
})
