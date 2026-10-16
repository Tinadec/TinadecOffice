import { onScopeDispose, ref, watch } from 'vue'
import { api as baseApi } from '@/api'
import { scopedApi, projectStorageId } from '@/lib/storageScope'

export interface ToolUsageAgent {
  id: string
  display_name?: string | null
  slug?: string | null
  enabled?: boolean
}

/** Shared overview usage comes from effective Core configuration, never from grants alone. */
export function useToolAgentUsage(
  agents: () => ToolUsageAgent[],
  projectId: () => string | undefined,
  selectedAgentId: () => string | undefined,
) {
  const usage = ref<Record<string, string[]>>({})
  const loading = ref(false)
  const failedAgentNames = ref<string[]>([])
  let generation = 0

  async function refresh(): Promise<void> {
    const current = ++generation
    usage.value = {}
    failedAgentNames.value = []
    loading.value = false
    if (selectedAgentId()) return
    const project = projectId() || undefined
    const storageId = projectStorageId(project)
    const api = scopedApi(baseApi, () => storageId)
    const visibleAgents = [...new Map(agents().filter(agent => agent.enabled !== false).map(agent => [agent.id, { ...agent }])).values()]
    if (!visibleAgents.length) return
    loading.value = true
    const results: { name: string; toolIds: string[] | null }[] = new Array(visibleAgents.length)
    let next = 0
    async function readAgents() {
      while (current === generation && next < visibleAgents.length) {
        const index = next++
        const agent = visibleAgents[index]!
        const name = agent.display_name || agent.slug || agent.id
        try {
          const effective = await api.getEffectiveToolSettings(agent.id, project)
          const ids = effective.allowed_tool_ids
          results[index] = { name, toolIds: Array.isArray(ids) && ids.every(id => typeof id === 'string') ? ids : null }
        } catch {
          results[index] = { name, toolIds: null }
        }
      }
    }
    await Promise.all(Array.from({ length: Math.min(4, visibleAgents.length) }, readAgents))
    if (current !== generation) return
    const resolved: Record<string, string[]> = Object.create(null)
    const failed: string[] = []
    for (const result of results) {
      if (result.toolIds === null) { failed.push(result.name); continue }
      for (const id of new Set(result.toolIds)) (resolved[id] ??= []).push(result.name)
    }
    usage.value = resolved
    failedAgentNames.value = failed
    loading.value = false
  }

  watch(() => JSON.stringify([projectId(), selectedAgentId(), agents().map(agent => [agent.id, agent.display_name, agent.slug, agent.enabled])]), () => { void refresh() }, { immediate: true })
  onScopeDispose(() => { generation++ })
  return { usage, loading, failedAgentNames, refresh }
}
