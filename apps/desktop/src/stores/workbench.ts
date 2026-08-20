import { ref } from 'vue'
import { defineStore } from 'pinia'
import { generatedApi, type OrchestrationSnapshotDto, type TaskNodeDto, type SupervisionFindingDto, type ContextVersionDto } from '@/generated/client'

export const useWorkbenchStore = defineStore('workbench', () => {
  const snapshot = ref<OrchestrationSnapshotDto | null>(null)
  const nodes = ref<TaskNodeDto[]>([])
  const findings = ref<SupervisionFindingDto[]>([])
  const contextVersions = ref<ContextVersionDto[]>([])
  const cursor = ref<number | null>(null)
  const status = ref<string>('idle')
  const loading = ref(false)
  const error = ref<string | null>(null)

  async function fetchAll(sessionId: string, runId?: string | null) {
    loading.value = true; error.value = null
    try {
      const [snap, n, f, c] = await Promise.all([
        generatedApi.getOrchestration(sessionId).catch(() => null),
        generatedApi.listTaskNodes(sessionId).catch(() => [] as TaskNodeDto[]),
        generatedApi.listSupervisionFindings(sessionId).catch(() => [] as SupervisionFindingDto[]),
        generatedApi.listContextVersions(sessionId, runId ?? undefined).catch(() => [] as ContextVersionDto[]),
      ])
      snapshot.value = snap
      nodes.value = Array.isArray(n) ? n : []
      findings.value = Array.isArray(f) ? f : []
      contextVersions.value = Array.isArray(c) ? c : []
      if (snap?.run?.status) status.value = String(snap.run.status)
      const maxSeq = Math.max(0, ...contextVersions.value.map(v => v.revision))
      if (maxSeq) cursor.value = maxSeq
    } catch (e) { error.value = e instanceof Error ? e.message : String(e) }
    finally { loading.value = false }
  }

  async function control(runId: string, action: 'cancel'|'pause'|'resume') {
    await generatedApi.controlRun(runId, { action })
  }

  // approval stage2 placeholder: transport abstraction reserved
  async function decideApproval(_approvalId: string, _decision: 'approved'|'rejected') {
    // ponytail: approval transport via httpTransport; keep seam for stage2 WS upgrade
    throw new Error('approval transport not wired in workbench store — use HomeController/api.decideApproval')
  }

  return { snapshot, nodes, findings, contextVersions, cursor, status, loading, error, fetchAll, control, decideApproval }
})
