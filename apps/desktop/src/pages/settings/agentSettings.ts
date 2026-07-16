import { computed, nextTick, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type AgentProfileDto, type AgentRuntimeBindingInput, type AgentRuntimeSelectionKind } from '@/api'
import { bindingForAgent, legacyRouteWarning, modelOptionKey, runtimeSourceSummary } from '@/runtimeCenterView'
import { loadAgentCenter as loadAgentState, useAgentState } from './agentState'
import { useModelSettings } from './modelSettings'
import { useSettingsLabels } from './settingsLabels'
import { useToolSettings } from './toolSettings'

const selectedAgentId = ref('')
const configuringAgentId = ref('')
const agentRuntimeSelection = ref<AgentRuntimeSelectionKind>('inherit')
const agentRuntimeProviderId = ref('')
const agentRuntimeModelKey = ref('')
const agentRuntimeCliId = ref('')
const agentRuntimeAcpId = ref('')
const agentRuntimeModelQuery = ref('')
const agentRuntimeProviderQuery = ref('')
const agentRuntimeCliQuery = ref('')
const agentRuntimeAcpQuery = ref('')
const agentRuntimeNotice = ref('')
const agentEditTools = ref<string[]>([])
const agentEditCapabilities = ref<string[]>([])
const agentEditSystemPrompt = ref('')
const agentEditDescription = ref('')
const agentNewCapability = ref('')
const agentRuntimeBusy = ref(false)
const busy = ref(false)
const agentViewMode = ref<'topology' | 'list'>('list')

function runtimeQueryMatches(query: string, ...values: Array<string | null | undefined>) {
  const normalized = query.trim().toLocaleLowerCase()
  if (!normalized) return true
  return values.some((value) => value?.toLocaleLowerCase().includes(normalized))
}

export function useAgentSettings() {
  const { t } = useI18n()
  const labels = useSettingsLabels()
  const agentState = useAgentState()
  const model = useModelSettings()
  const tools = useToolSettings()
  const agentRuntimeBindings = computed(() => Object.fromEntries((agentState.agentCenterOverview.value?.agents ?? []).map((agent) => [agent.id, agent.runtime_binding])))
  const topologyAgentLabels = computed(() => Object.fromEntries(agentState.agents.value.map((agent) => [agent.id, labels.agentTypeLabel(agent.agent_type)])))
  const topologyCandidateLabels = computed(() => Object.fromEntries(agentState.agentCandidates.value.map((candidate) => [candidate.id, labels.agentTypeLabel(candidate.agent_type)])))
  const selectedAgent = computed(() => agentState.agents.value.find((agent) => agent.id === selectedAgentId.value) ?? null)
  const configuringAgent = computed(() => agentState.agents.value.find((agent) => agent.id === configuringAgentId.value) ?? null)
  const configuredAgentMode = computed(() => agentState.agentModes.value.find((mode) => mode.id === configuringAgent.value?.mode) ?? null)
  const configuringRuntimeBinding = computed(() => bindingForAgent(agentState.agentCenterOverview.value, configuringAgentId.value))
  const configuringLegacyWarning = computed(() => legacyRouteWarning(configuringRuntimeBinding.value))
  const runtimeBindingWritable = computed(() => Boolean(agentState.agentCenterOverview.value?.capabilities.agent_runtime_binding_write && configuringRuntimeBinding.value?.writable))
  const runtimeModels = computed(() => agentState.agentCenterOverview.value?.runtime_sources.models ?? model.modelCenterOverview.value?.models ?? [])
  const runtimeProviders = computed(() => agentState.agentCenterOverview.value?.runtime_sources.providers ?? model.modelCenterOverview.value?.api_connections ?? [])
  const runtimeCliOptions = computed(() => agentState.agentCenterOverview.value?.runtime_sources.cli_runtimes ?? model.modelCenterOverview.value?.cli_runtimes ?? [])
  const runtimeAcpOptions = computed(() => agentState.agentCenterOverview.value?.runtime_sources.acp_runtimes ?? model.modelCenterOverview.value?.acp_runtimes ?? [])
  const filteredRuntimeModels = computed(() => runtimeModels.value.filter((item) => runtimeQueryMatches(agentRuntimeModelQuery.value, item.model_id, item.provider_display_name, item.provider_instance_id, item.status, ...item.configuration_sources, ...item.route_purposes)))
  const filteredRuntimeProviders = computed(() => runtimeProviders.value.filter((item) => runtimeQueryMatches(agentRuntimeProviderQuery.value, item.display_name, item.provider_instance_id, item.driver, item.status, item.model)))
  const filteredRuntimeCliOptions = computed(() => runtimeCliOptions.value.filter((item) => runtimeQueryMatches(agentRuntimeCliQuery.value, item.display_name, item.runtime_id, item.driver, item.status, item.binary_path, item.home_path)))
  const filteredRuntimeAcpOptions = computed(() => runtimeAcpOptions.value.filter((item) => runtimeQueryMatches(agentRuntimeAcpQuery.value, item.display_name, item.runtime_id, item.source, item.driver, item.status, item.command)))

  function openAgentConfig(agent: AgentProfileDto) {
    selectedAgentId.value = agent.id
    configuringAgentId.value = agent.id
    agentEditTools.value = [...(agent.allowed_tools ?? [])]
    agentEditCapabilities.value = [...(agent.capabilities ?? [])]
    agentEditSystemPrompt.value = agent.system_prompt ?? ''
    agentEditDescription.value = agent.description ?? ''
    agentNewCapability.value = ''
    const binding = bindingForAgent(agentState.agentCenterOverview.value, agent.id)
    agentRuntimeSelection.value = binding?.selection_kind ?? 'inherit'
    agentRuntimeProviderId.value = binding?.provider_instance_id ?? runtimeProviders.value[0]?.provider_instance_id ?? ''
    agentRuntimeModelKey.value = binding?.provider_instance_id && binding.model_id ? modelOptionKey(binding.provider_instance_id, binding.model_id) : runtimeModels.value[0] ? modelOptionKey(runtimeModels.value[0].provider_instance_id, runtimeModels.value[0].model_id) : ''
    agentRuntimeCliId.value = binding?.runtime_kind === 'cli' ? binding.runtime_id ?? '' : runtimeCliOptions.value[0]?.runtime_id ?? ''
    agentRuntimeAcpId.value = binding?.runtime_kind === 'acp' ? binding.runtime_id ?? '' : runtimeAcpOptions.value[0]?.runtime_id ?? ''
    agentRuntimeModelQuery.value = ''
    agentRuntimeProviderQuery.value = ''
    agentRuntimeCliQuery.value = ''
    agentRuntimeAcpQuery.value = ''
    agentRuntimeNotice.value = ''
    nextTick(() => {
      if (!window.matchMedia('(max-width: 760px)').matches) return
      const behavior = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
      document.querySelector('.agent-detail-panel')?.scrollIntoView({ behavior, block: 'start' })
    })
  }

  async function loadAgentCenter() {
    await loadAgentState(t('settings.centerLoadFailed'))
    const activeAgent = agentState.agents.value.find((agent) => agent.id === configuringAgentId.value) ?? agentState.agents.value.find((agent) => agent.id === selectedAgentId.value) ?? agentState.agents.value[0]
    if (activeAgent) openAgentConfig(activeAgent)
  }

  function closeAgentConfig() {
    selectedAgentId.value = ''
    configuringAgentId.value = ''
  }

  function openAgentConfigById(agentId: string) {
    const agent = agentState.agents.value.find((item) => item.id === agentId)
    if (agent) openAgentConfig(agent)
  }

  function runtimeBindingInput(): AgentRuntimeBindingInput | null {
    if (agentRuntimeSelection.value === 'inherit') return { selection_kind: 'inherit' }
    if (agentRuntimeSelection.value === 'provider_auto') return agentRuntimeProviderId.value ? { selection_kind: 'provider_auto', provider_instance_id: agentRuntimeProviderId.value } : null
    if (agentRuntimeSelection.value === 'cli') return agentRuntimeCliId.value ? { selection_kind: 'cli', runtime_id: agentRuntimeCliId.value } : null
    if (agentRuntimeSelection.value === 'acp') return agentRuntimeAcpId.value ? { selection_kind: 'acp', runtime_id: agentRuntimeAcpId.value } : null
    const selected = runtimeModels.value.find((item) => modelOptionKey(item.provider_instance_id, item.model_id) === agentRuntimeModelKey.value)
    return selected ? { selection_kind: 'fixed_model', provider_instance_id: selected.provider_instance_id, model_id: selected.model_id } : null
  }

  async function saveAgentRuntimeBinding(agent: AgentProfileDto) {
    const binding = runtimeBindingInput()
    if (!binding) return
    agentRuntimeBusy.value = true
    agentRuntimeNotice.value = ''
    try {
      await api.saveAgentRuntimeBinding(agent.id, binding)
      await loadAgentCenter()
    } catch (error) {
      agentRuntimeNotice.value = error instanceof Error ? error.message : t('settings.runtimeBindingUnsupported')
    } finally {
      agentRuntimeBusy.value = false
    }
  }

  async function updateAgentMode(agent: AgentProfileDto, mode: string) {
    busy.value = true
    try {
      await api.updateAgentMode(agent.id, mode)
      await loadAgentCenter()
    } finally {
      busy.value = false
    }
  }

  async function setAgentEnabled(agent: AgentProfileDto, enabled: boolean) {
    busy.value = true
    try {
      await api.saveAgent(agent.id, { name: agent.name, layer: agent.layer, agent_type: agent.agent_type, mode: agent.mode, description: agent.description, model_route_purpose: agent.model_route_purpose, allowed_tools: agent.allowed_tools, capabilities: agent.capabilities, system_prompt: agent.system_prompt, enabled })
      await loadAgentCenter()
    } finally {
      busy.value = false
    }
  }

  async function saveAgentProfile() {
    if (!configuringAgent.value) return
    busy.value = true
    try {
      await api.saveAgent(configuringAgent.value.id, { name: configuringAgent.value.name, layer: configuringAgent.value.layer, agent_type: configuringAgent.value.agent_type, mode: configuringAgent.value.mode, description: agentEditDescription.value, model_route_purpose: configuringAgent.value.model_route_purpose, allowed_tools: agentEditTools.value, capabilities: agentEditCapabilities.value, system_prompt: agentEditSystemPrompt.value || null, enabled: configuringAgent.value.enabled })
      await loadAgentCenter()
      const updated = agentState.agents.value.find((agent) => agent.id === configuringAgentId.value)
      if (!updated) return
      agentEditTools.value = [...updated.allowed_tools]
      agentEditCapabilities.value = [...updated.capabilities]
      agentEditSystemPrompt.value = updated.system_prompt ?? ''
      agentEditDescription.value = updated.description
    } finally {
      busy.value = false
    }
  }

  function toggleAgentTool(toolId: string) {
    const index = agentEditTools.value.indexOf(toolId)
    index >= 0 ? agentEditTools.value.splice(index, 1) : agentEditTools.value.push(toolId)
  }

  function removeAgentCapability(capability: string) {
    const index = agentEditCapabilities.value.indexOf(capability)
    if (index >= 0) agentEditCapabilities.value.splice(index, 1)
  }

  function addAgentCapability() {
    const capability = agentNewCapability.value.trim()
    if (!capability || agentEditCapabilities.value.includes(capability)) return
    agentEditCapabilities.value.push(capability)
    agentNewCapability.value = ''
  }

  if (agentState.agents.value.length === 0 && !agentState.agentCenterLoading.value) void loadAgentCenter()

  return { ...labels, ...agentState, addAgentCapability, agentEditCapabilities, agentEditDescription, agentEditSystemPrompt, agentEditTools, agentNewCapability, agentRuntimeAcpId, agentRuntimeAcpQuery, agentRuntimeBindings, agentRuntimeBusy, agentRuntimeCliId, agentRuntimeCliQuery, agentRuntimeModelKey, agentRuntimeModelQuery, agentRuntimeNotice, agentRuntimeProviderId, agentRuntimeProviderQuery, agentRuntimeSelection, agentViewMode, availableTools: tools.availableTools, busy, closeAgentConfig, configuredAgentMode, configuringAgent, configuringLegacyWarning, configuringRuntimeBinding, filteredRuntimeAcpOptions, filteredRuntimeCliOptions, filteredRuntimeModels, filteredRuntimeProviders, loadAgentCenter, modelOptionKey, openAgentConfig, openAgentConfigById, providers: model.providers, removeAgentCapability, runtimeBindingInput, runtimeBindingWritable, runtimeSourceSummary, saveAgentProfile, saveAgentRuntimeBinding, selectedAgent, selectedAgentId, setAgentEnabled, toggleAgentTool, topologyAgentLabels, topologyCandidateLabels, updateAgentMode }
}
