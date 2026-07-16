import { computed, ref } from 'vue'
import { api, type HarnessManifestDto, type ToolDescriptorDto, type ToolLayerReadinessReceiptDto, type ToolSearchResultDto } from '@/api'
import { codeSuiteTools, languageSupportFromTools, manifestTools, projectTemplatesFromResult, sortedAgentLayers, sortedRiskPolicies, sortedToolProviders, sortedToolSearchResults, type ProjectTemplateSummary } from '@/toolCatalog'
import { useAgentState } from './agentState'
import { useSettingsLabels } from './settingsLabels'

const availableTools = ref<ToolDescriptorDto[]>([])
const harnessManifest = ref<HarnessManifestDto | null>(null)
const toolLayerReadiness = ref<ToolLayerReadinessReceiptDto | null>(null)
const toolSearchResults = ref<ToolSearchResultDto[]>([])
const projectTemplates = ref<ProjectTemplateSummary[]>([])
const toolDiscoveryQuery = ref('')
const toolDiscoverySource = ref('all')
const toolDiscoveryRisk = ref('all')
const toolDiscoveryLoading = ref(false)
const loading = ref(false)
const toolCatalogError = ref('')
const toolDiscoveryError = ref('')

async function loadToolDiscovery() {
  toolDiscoveryLoading.value = true
  toolDiscoveryError.value = ''
  try {
    toolSearchResults.value = await api.searchTools({
      query: toolDiscoveryQuery.value.trim() || undefined,
      source: toolDiscoverySource.value === 'all' ? undefined : toolDiscoverySource.value,
      risk: toolDiscoveryRisk.value === 'all' ? undefined : toolDiscoveryRisk.value,
      limit: 10,
    })
  } catch (error) {
    toolSearchResults.value = []
    toolDiscoveryError.value = error instanceof Error ? error.message : String(error)
  } finally {
    toolDiscoveryLoading.value = false
  }
}

async function loadToolCatalog() {
  loading.value = true
  toolCatalogError.value = ''
  try {
    const [toolReadiness, templates] = await Promise.all([
      api.getToolLayerReadiness().catch(() => null),
      api.executeCodeTool('project_templates').catch(() => null),
    ])
    toolLayerReadiness.value = toolReadiness
    projectTemplates.value = templates ? projectTemplatesFromResult(templates) : []
    try {
      const manifest = await api.getHarnessManifest()
      harnessManifest.value = manifest
      availableTools.value = manifest.tools
    } catch {
      harnessManifest.value = null
      try {
        availableTools.value = await api.listTools()
      } catch (error) {
        availableTools.value = []
        toolCatalogError.value = error instanceof Error ? error.message : String(error)
      }
    }
    await loadToolDiscovery()
  } finally {
    loading.value = false
  }
}

let requested = false

function ensureToolCatalogLoaded() {
  if (requested) return
  requested = true
  void loadToolCatalog()
}

export function useToolSettings() {
  ensureToolCatalogLoaded()
  const { executionAgents, loadAgentCenter } = useAgentState()
  const labels = useSettingsLabels()
  const manifestToolList = computed(() => manifestTools(harnessManifest.value, availableTools.value))
  const manifestProviders = computed(() => sortedToolProviders(harnessManifest.value))
  const manifestAgentLayers = computed(() => sortedAgentLayers(harnessManifest.value))
  const manifestRiskPolicies = computed(() => sortedRiskPolicies(harnessManifest.value))
  const codeSuiteToolList = computed(() => codeSuiteTools(manifestToolList.value))
  const codexPrimitiveTools = computed(() => manifestToolList.value.filter((tool) => tool.source === 'codex-rust'))
  const supportedLanguages = computed(() => languageSupportFromTools(manifestToolList.value))
  const warningToolLayerTools = computed(() => (toolLayerReadiness.value?.tools ?? []).filter((tool) => tool.status !== 'ready'))
  const warningToolLayerAgents = computed(() => (toolLayerReadiness.value?.agent_scopes ?? []).filter((agent) => agent.status !== 'ready'))
  const toolSourceOptions = computed(() => Array.from(new Set(manifestToolList.value.map((tool) => tool.source))).sort())
  const toolRiskOptions = computed(() => Array.from(new Set(manifestToolList.value.map((tool) => tool.risk))).sort())
  const sortedToolDiscoveryResults = computed(() => sortedToolSearchResults(toolSearchResults.value))

  return { ...labels, availableTools, codeSuiteToolList, codexPrimitiveTools, executionAgents, harnessManifest, loadAgentCenter, loadToolCatalog, loadToolDiscovery, loading, manifestAgentLayers, manifestProviders, manifestRiskPolicies, projectTemplates, sortedToolDiscoveryResults, supportedLanguages, toolCatalogError, toolDiscoveryError, toolDiscoveryLoading, toolDiscoveryQuery, toolDiscoveryRisk, toolDiscoverySource, toolLayerReadiness, toolRiskOptions, toolSearchResults, toolSourceOptions, warningToolLayerAgents, warningToolLayerTools }
}
