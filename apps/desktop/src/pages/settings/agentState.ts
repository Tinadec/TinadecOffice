import { computed, ref } from 'vue'
import { api, type AgentCandidateDto, type AgentCenterOverviewDto, type AgentModeDto, type AgentProfileDto, type ModelRouteDto } from '@/api'

const agentCenterOverview = ref<AgentCenterOverviewDto | null>(null)
const agentModes = ref<AgentModeDto[]>([])
const agents = ref<AgentProfileDto[]>([])
const agentCandidates = ref<AgentCandidateDto[]>([])
const routes = ref<ModelRouteDto[]>([])
const agentCenterLoading = ref(false)
const agentCenterError = ref('')

function routesFromAgents(overview: AgentCenterOverviewDto) {
  const routeMap = new Map<string, ModelRouteDto>()
  for (const agent of overview.agents) {
    const binding = agent.runtime_binding
    if (!binding.provider_instance_id) continue
    routeMap.set(binding.route_purpose, {
      purpose: binding.route_purpose,
      provider_instance_id: binding.provider_instance_id,
      model: binding.model_id ?? null,
      updated_at: agent.updated_at ?? '',
    })
  }
  return [...routeMap.values()]
}

export async function loadAgentCenter(loadFailedMessage = 'Failed to load center data') {
  agentCenterLoading.value = true
  agentCenterError.value = ''
  try {
    const overview = await api.getAgentCenterOverview()
    agentCenterOverview.value = overview
    agentModes.value = overview.modes
    agents.value = overview.agents
    agentCandidates.value = overview.candidates
    routes.value = routesFromAgents(overview)
  } catch (error) {
    agentCenterError.value = error instanceof Error ? error.message : loadFailedMessage
  } finally {
    agentCenterLoading.value = false
  }
}

let requested = false

export function ensureAgentCenterLoaded() {
  if (requested) return
  requested = true
  void loadAgentCenter()
}

export function useAgentState() {
  ensureAgentCenterLoaded()
  const agentCenterDiagnostics = computed(() => agentCenterOverview.value?.diagnostics ?? [])
  const planningAgents = computed(() => agents.value.filter((agent) => agent.layer === 'planning'))
  const executionAgents = computed(() => agents.value.filter((agent) => agent.layer === 'execution'))

  return { agentCandidates, agentCenterDiagnostics, agentCenterError, agentCenterLoading, agentCenterOverview, agentModes, agents, executionAgents, loadAgentCenter, planningAgents, routes }
}
