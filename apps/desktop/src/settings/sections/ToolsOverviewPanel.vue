<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useStatusLabels } from '@/lib/statusLabels'
import { UiBadge, UiButton, UiInput, UiSelectField } from '@/components/ui'
import { api as baseApi, type HarnessManifestDto, type ToolCapabilities, type ToolDescriptorDto, type ToolLayerReadinessReceiptDto, type ToolSearchResultDto, type ToolSettingsEffective } from '@/api'
import { scopedApi, projectStorageId } from '@/lib/storageScope'
import { manifestTools, sortedToolSearchResults } from '@/toolCatalog'
import { consumeRequest, pendingToolId } from '@/lib/pageRequests'
import { useNotifications } from '@/composables/useNotifications'
import { formatSettings } from '@/settings/toolSettings'
import { useToolAgentUsage } from '@/settings/toolAgentUsage'
const props = defineProps<{ agentId?: string; projectId?: string; agents?: { id: string; display_name?: string | null; slug?: string | null }[]; effectiveToolIds?: string[] | null; capabilities?: ToolCapabilities | null; hostSettings?: Record<string, unknown>; effective?: ToolSettingsEffective | null }>()
const api = scopedApi(baseApi, () => projectStorageId(props.projectId))
const { t } = useI18n()
const { riskLabel } = useStatusLabels()
const { notify } = useNotifications()
const { usage: toolUsage, loading: usageLoading, failedAgentNames: usageFailures, refresh: refreshUsage } = useToolAgentUsage(() => props.agents ?? [], () => props.projectId, () => props.agentId)
function usageText(toolId: string) {
  if (props.agentId) { const agent = props.agents?.find(item => item.id === props.agentId); return agent?.display_name || agent?.slug || props.agentId }
  if (usageLoading.value) return t('toolsSettings.usageLoading')
  return toolUsage.value[toolId]?.join(t('conversation.listSeparator')) || t(usageFailures.value.length ? 'toolsSettings.usageUnavailable' : 'toolsSettings.noUsingAgents')
}
const manifest = ref<HarnessManifestDto | null>(null)
const tools = ref<ToolDescriptorDto[]>([])
const readiness = ref<ToolLayerReadinessReceiptDto | null>(null)
const results = ref<ToolSearchResultDto[]>([])
const query = ref('')
const requestedToolId = ref('')
const source = ref('all')
const risk = ref('all')
const loading = ref(false)
const error = ref('')
const discovery = ref<HTMLElement | null>(null)
const visibleTools = computed(() => manifestTools(manifest.value, tools.value).filter(tool => props.effectiveToolIds ? props.effectiveToolIds.includes(tool.id) : !props.agentId))
const sources = computed(() => [...new Set(visibleTools.value.map(tool => tool.source).filter(Boolean))].sort())
const risks = computed(() => [...new Set(visibleTools.value.map(tool => tool.risk))].sort())
const visibleResults = computed(() => sortedToolSearchResults(results.value).filter(result =>
  visibleTools.value.some(tool => tool.id === result.tool.id)
  && (source.value === 'all' || result.tool.source === source.value)
  && (risk.value === 'all' || result.tool.risk === risk.value)
  && (!requestedToolId.value || query.value !== requestedToolId.value || result.tool.id === requestedToolId.value),
))
const resources = computed(() => [
  { label: 'MCP', count: Array.isArray(props.effective?.mcp_servers) ? props.effective.mcp_servers.length : null },
  { label: t('toolsSettings.tabs.skills'), count: Array.isArray(props.effective?.skill_resources) ? props.effective.skill_resources.length : null },
])
let discoveryRead = 0
async function search() {
  const current = ++discoveryRead
  loading.value = true; error.value = ''
  try {
    const rows = await api.searchTools({ query: query.value.trim() || undefined, limit: 100 })
    if (current === discoveryRead) results.value = rows
  } catch (err) { if (current === discoveryRead) error.value = err instanceof Error ? err.message : String(err) }
  finally { if (current === discoveryRead) loading.value = false }
}
async function refresh(includeUsage = true) {
  const storageId = projectStorageId(props.projectId)
  const capturedApi = scopedApi(baseApi, () => storageId)
  try {
    const [receipt, catalog] = await Promise.all([capturedApi.getToolLayerReadiness().catch(() => null), capturedApi.getHarnessManifest().catch(() => null)])
    readiness.value = receipt; manifest.value = catalog
    tools.value = catalog?.tools?.length ? catalog.tools : await capturedApi.listTools()
    await Promise.all([search(), ...(includeUsage ? [refreshUsage()] : [])])
  } catch (err) { notify.error(err, { title: t('app.loadFailed'), source: 'tools' }) }
}
consumeRequest(pendingToolId, async id => {
  requestedToolId.value = id
  query.value = id; source.value = risk.value = 'all'
  await search(); await nextTick()
  discovery.value?.scrollIntoView({ block: 'center' }); discovery.value?.focus({ preventScroll: true })
})
onMounted(() => refresh(false))
defineExpose({ refresh })
</script>
<template>
  <div class="tools-effective-card">
    <strong>{{ t('toolsSettings.availableTools', { count: visibleTools.length }) }}</strong>
    <p class="quiet">{{ t('toolsSettings.effectiveScopeHint') }}</p>
    <div class="model-capability-row"><span v-for="resource in resources" :key="resource.label">{{ resource.label }} · {{ resource.count ?? '—' }}</span></div>
    <p v-if="!capabilities?.capabilities" class="quiet" :title="capabilities?.reason ?? undefined">{{ t(capabilities?.reason ? 'toolsSettings.diagnostic.host_unavailable' : 'toolsSettings.executionUnknown') }}</p>
    <p v-else class="quiet">{{ t('toolsSettings.capabilitySummary', { rg: t(capabilities.capabilities.ripgrep?.available ? 'toolsSettings.detected' : 'toolsSettings.unavailable'), sandbox: t(capabilities.capabilities.sandbox?.supported ? 'toolsSettings.detected' : 'toolsSettings.unavailable') }) }}</p>
  </div>
  <div class="model-section-header"><h3>{{ t('settings.toolDiscovery') }}</h3><UiBadge variant="outline">{{ visibleResults.length }}</UiBadge></div>
  <div ref="discovery" class="tool-discovery-controls" tabindex="-1">
    <UiInput v-model="query" :placeholder="t('settings.toolDiscoveryPlaceholder')" @keydown.enter="search" />
    <UiSelectField v-model="source" :aria-label="t('settings.allSources')" :options="[{ value: 'all', label: t('settings.allSources') }, ...sources.map((item) => ({ value: item, label: item }))]" />
    <UiSelectField v-model="risk" :aria-label="t('settings.allRisks')" :options="[{ value: 'all', label: t('settings.allRisks') }, ...risks.map((item) => ({ value: item, label: item }))]" />
    <UiButton size="sm" :disabled="loading" @click="search">{{ t('toolsSettings.searchTools') }}</UiButton>
  </div>
  <p v-if="error" class="tools-field-error" role="alert">{{ error }}</p>
  <p v-if="usageFailures.length" class="quiet" role="status">{{ t('toolsSettings.usageFailed', { agents: usageFailures.join(t('conversation.listSeparator')) }) }}</p>
  <div class="tools-resource-list"><article v-for="result in visibleResults" :key="result.tool.id" class="tools-resource-row tool-discovery-card" :class="{ selected: result.tool.id === requestedToolId }"><span class="tools-tool-identity"><strong>{{ result.tool.display_name }}</strong><code v-if="result.tool.display_name !== result.tool.id">{{ result.tool.id }}</code><small>{{ result.tool.description }}</small></span><span class="tools-tool-facts"><small>{{ result.tool.source || t('toolsSettings.sourceUndeclared') }} · {{ riskLabel(result.tool.risk) }}</small><small v-if="result.tool.requires_approval">{{ t('settings.approvalRequired') }}</small><small>{{ t('toolsSettings.usedBy', { agents: usageText(result.tool.id) }) }}</small></span></article></div>
  <p v-if="!loading && !error && !visibleResults.length" class="quiet">{{ t('settings.noTools') }}</p>
  <details class="tools-effective-card"><summary>{{ t('toolsSettings.runtimeDetails') }}</summary>
    <p class="quiet">{{ t('toolsSettings.globalReceipt') }}</p>
    <p v-if="manifest">{{ manifest.runtime }} · {{ manifest.ownership_model }}</p>
    <pre v-if="readiness">{{ formatSettings(readiness) }}</pre>
    <pre v-if="capabilities?.capabilities">{{ formatSettings(capabilities) }}</pre>
    <h4>{{ t('toolsSettings.managedHost') }}</h4><p class="quiet">{{ t('toolsSettings.managedHostHint') }}</p><pre>{{ formatSettings(hostSettings ?? {}) }}</pre>
  </details>
</template>
