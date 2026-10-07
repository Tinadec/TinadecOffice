import { ref, computed } from 'vue'
import { defineStore } from 'pinia'
import { generatedApi, type RunDto, type SseChunk, RUN_STATUSES } from '@/generated/client'

// Run 投影 store（plan §6.3-5）。提交路径已统一到 HomeController 的
// POST /interactions + createRunStream；本 store 只保留 runs 列表投影、
// 选择与 run control，不再持有独立的 invoke-stream 客户端。

export const useRunStore = defineStore('run', () => {
  const runs = ref<RunDto[]>([])
  const selectedRunId = ref<string | null>(null)
  const status = ref<string>('idle')
  const error = ref<string | null>(null)

  const current = computed(() => runs.value.find(r => r.id === selectedRunId.value) ?? runs.value[0] ?? null)

  // Last call wins — see useWorkbenchStore.fetchAll for the overlapping-refresh rationale.
  let fetchSeq = 0

  async function fetchRuns(sessionId: string) {
    const seq = ++fetchSeq
    try { const fetched = await generatedApi.listRuns(sessionId); if (seq !== fetchSeq) return; runs.value = fetched } catch (e) { if (seq !== fetchSeq) return; error.value = e instanceof Error ? e.message : String(e) }
    if (selectedRunId.value && !runs.value.find(r => r.id === selectedRunId.value)) selectedRunId.value = runs.value[0]?.id ?? null
    if (!selectedRunId.value) selectedRunId.value = runs.value[0]?.id ?? null
  }

  async function control(runId: string, action: 'cancel'|'pause'|'resume', expectedContextRevision?: number | null) {
    const body: Record<string, unknown> = { action }
    if (expectedContextRevision != null) body.expected_context_revision = expectedContextRevision
    await generatedApi.controlRun(runId, body)
    await fetchRuns(current.value?.session_id ?? '')
  }

  function select(id: string) { selectedRunId.value = id }

  function isTerminal(s: string): boolean { return ['completed','failed','cancelled'].includes(s) }

  return { runs, selectedRunId, current, status, error, fetchRuns, control, select, isTerminal, RUN_STATUSES }
})
