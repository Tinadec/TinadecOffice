<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useStatusLabels } from '@/lib/statusLabels'
import { Braces, FileText, GitBranch, Globe, Package, Search, Server, Terminal, Wrench } from '@lucide/vue'
import { UiBadge, UiButton, UiSelectField } from '@/components/ui'
import { api as baseApi, type AgentDefinitionDto, type ProjectDto, type ToolDescriptorDto, type ToolSettingsDocument, type ToolSettingsSchema, type ToolSettingsEffective, type ToolCapabilities } from '@/api'
import { scopedApi, projectStorageId, selectionKey } from '@/lib/storageScope'
import { useNotifications } from '@/composables/useNotifications'
import { consumeRequest, pendingToolId, pendingToolAgentId, requestAgent } from '@/lib/pageRequests'
import { capabilityReason, effectiveSettings, formatSettings, settingsDiff, validateSettings, type ToolJsonSchema } from '@/settings/toolSettings'
const ToolsOverviewPanel = defineAsyncComponent(() => import('./ToolsOverviewPanel.vue'))
const ToolJsonEditor = defineAsyncComponent(() => import('./ToolJsonEditor.vue'))
const ToolResourcesPanel = defineAsyncComponent(() => import('./ToolResourcesPanel.vue'))
const ToolDraftDialog = defineAsyncComponent(() => import('./ToolDraftDialog.vue'))
const { t, te } = useI18n()
const { riskLabel } = useStatusLabels()
/** A resource diagnostic in the interface language; the server's reason stays available as a tooltip. */
function diagnosticText(item: { kind: string; status: string; reason?: string | null }): string {
  if (item.kind === 'mcp_program') return t('toolsSettings.diagnostic.mcp_program', { status: te(`toolsSettings.programStatus.${item.status}`) ? t(`toolsSettings.programStatus.${item.status}`) : item.status })
  const key = `toolsSettings.diagnostic.${item.kind}_${item.status}`
  return te(key) ? t(key) : item.reason || `${item.kind} · ${item.status}`
}
const yesNo = (value: boolean | undefined) => t(value ? 'common.yes' : 'common.no')
const { notify } = useNotifications()
const tabs = [
  { id: 'overview', icon: Wrench }, { id: 'shell', icon: Terminal }, { id: 'files', icon: FileText },
  { id: 'search', icon: Search }, { id: 'git', icon: GitBranch }, { id: 'web', icon: Globe },
  { id: 'mcp', icon: Server }, { id: 'skills', icon: Package }, { id: 'advanced', icon: Braces },
] as const
type Tab = typeof tabs[number]['id']
const tab = ref<Tab>('overview')
const focusPath = ref('')
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const stale = ref(false)
const projects = ref<ProjectDto[]>([])
const agents = ref<AgentDefinitionDto[]>([])
const catalogTools = ref<ToolDescriptorDto[]>([])
const agentId = ref('')
const projectId = ref('')
const api = scopedApi(baseApi, () => projectStorageId(projectId.value))
const document = ref<ToolSettingsDocument | null>(null)
const schema = ref<ToolSettingsSchema | null>(null)
const effective = ref<ToolSettingsEffective | null>(null)
const capabilities = ref<ToolCapabilities | null>(null)
const draft = ref('')
const baseline = ref('')
const resourceDirty = ref(false)
const resources = ref<{ discard(): void; refresh(): Promise<void>; isBusy(): boolean; save(): Promise<boolean> } | null>(null)
const leaveDialog = ref(false)
let chooseLeave: ((value: 'save' | 'discard' | 'cancel') => void) | null = null
const overview = ref<{ refresh(): Promise<void> } | null>(null)
let generation = 0
function sparseSchema(rule: ToolJsonSchema): ToolJsonSchema {
  return { ...rule, required: [], ...(rule.properties ? { properties: Object.fromEntries(Object.entries(rule.properties).map(([key, child]) => [key, sparseSchema(child)])) } : {}) }
}
const editorSchema = computed(() => schema.value ? (agentId.value ? sparseSchema(schema.value.schema) : schema.value.schema) : null)
const validation = computed(() => validateSettings(draft.value, editorSchema.value, t))
const configDirty = computed(() => draft.value !== baseline.value)
const dirty = computed(() => configDirty.value || resourceDirty.value)
const diff = computed(() => validation.value.value && document.value ? settingsDiff(document.value.settings, validation.value.value) : [])
const selectedAgent = computed(() => agents.value.find(agent => agent.id === agentId.value))
const effectiveValue = computed(() => effectiveSettings(effective.value ?? document.value))
const effectiveToolIds = computed(() => Array.isArray(effective.value?.allowed_tool_ids) ? effective.value.allowed_tool_ids as string[] : null)
const familyTools = computed(() => catalogTools.value.filter(tool => {
  const id = tool.id
  const family = tab.value === 'files' ? ['read_file', 'ls', 'stat', 'write_file', 'replace_lines', 'replace_bytes', 'insert_line', 'insert_bytes', 'insert_byte', 'delete_line', 'delete_bytes'].includes(id)
    : tab.value === 'shell' ? ['shell', 'command_run', 'sandbox_status', 'sandbox_reset'].includes(id)
    : tab.value === 'search' ? id.includes('search') && !id.startsWith('mcp_')
    : id.startsWith(`${tab.value}_`)
  return family
}))
const sectionRows = computed(() => (tab.value === 'files' ? ['read', 'write'] : [tab.value]).flatMap(section => {
  const fields = effectiveValue.value[section]
  if (!fields || typeof fields !== 'object') return []
  return Object.entries(fields as Record<string, unknown>).map(([key, value]) => ({ path: `${section}.${key}`, value, description: schema.value?.schema.properties?.[section]?.properties?.[key]?.description ?? '' }))
}))
function valueText(value: unknown): string { return value === undefined ? '—' : typeof value === 'string' ? value : JSON.stringify(value) }
function sourceFor(path: string): string {
  if (!agentId.value) return t('toolsSettings.shared')
  const [section, key] = path.split('.')
  const overrides = document.value?.settings[section!]
  return overrides && typeof overrides === 'object' && key! in overrides ? t('toolsSettings.agentOverride') : t('toolsSettings.inherited')
}
async function canLeave(): Promise<boolean> {
  if (saving.value || resources.value?.isBusy()) return false
  if (!dirty.value) return true
  if (leaveDialog.value) return false
  leaveDialog.value = true
  const choice = await new Promise<'save' | 'discard' | 'cancel'>(resolve => { chooseLeave = resolve })
  leaveDialog.value = false; chooseLeave = null
  if (choice === 'cancel') return false
  if (choice === 'save') {
    if (configDirty.value && !await save()) return false
    if (resourceDirty.value && !await resources.value?.save()) return false
    return !dirty.value
  }
  draft.value = baseline.value; resources.value?.discard(); resourceDirty.value = false
  return true
}
defineExpose({ canLeave })
function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value) { event.preventDefault(); event.returnValue = '' } }
watch(pendingToolId, id => { if (id) void selectTab('overview') })
consumeRequest(pendingToolAgentId, async id => {
  if (!await canLeave()) return
  agentId.value = id
  if (schema.value) await loadContext()
})
async function loadContext() {
  const current = ++generation
  loading.value = true; error.value = ''; stale.value = false
  try {
    capabilities.value = null
    const [config, resolved, receipt] = await Promise.all([
      agentId.value ? api.getAgentToolSettings(agentId.value) : api.getToolSettingsDefaults(),
      api.getEffectiveToolSettings(agentId.value || undefined, projectId.value || undefined),
      api.getToolCapabilities(agentId.value || undefined, projectId.value || undefined).catch(err => ({ status: 'unavailable' as const, reason: err instanceof Error ? err.message : String(err) })),
    ])
    if (current !== generation) return
    document.value = config; effective.value = resolved; capabilities.value = receipt
    draft.value = baseline.value = formatSettings(config.settings)
  } catch (err) { if (current === generation) error.value = err instanceof Error ? err.message : String(err) }
  finally { if (current === generation) loading.value = false }
}
async function refresh() {
  if (!await canLeave()) return
  await loadContext()
  await resources.value?.refresh()
  if (tab.value === 'overview') await overview.value?.refresh()
}
async function resourceChanged() {
  const current = generation
  try {
    const resolved = await api.getEffectiveToolSettings(agentId.value || undefined, projectId.value || undefined)
    if (current === generation) effective.value = resolved
  } catch (err) { if (current === generation) error.value = err instanceof Error ? err.message : String(err) }
}
async function selectContext(kind: 'agent' | 'project', next: string) {
  if (next === (kind === 'agent' ? agentId.value : projectId.value)) return
  if (!await canLeave()) return
  if (kind === 'agent') agentId.value = next
  else projectId.value = next
  await loadContext()
}
async function selectTab(next: Tab, focus = false) {
  if (resources.value?.isBusy()) return
  if (resourceDirty.value && !await canLeave()) return
  if (next === 'advanced' && tab.value !== 'advanced') focusPath.value = focus ? '' : tab.value === 'files' ? 'read' : ['shell', 'search', 'git', 'web', 'mcp', 'skills'].includes(tab.value) ? tab.value : ''
  resources.value?.discard(); tab.value = next
  if (focus) { await nextTick(); window.document.getElementById(`tools-tab-${next}`)?.focus() }
}
async function save() {
  if (!document.value || !configDirty.value || validation.value.issues.length || saving.value || stale.value) return false
  saving.value = true; error.value = ''
  try {
    const saved = agentId.value ? await api.saveAgentToolSettings(agentId.value, validation.value.value!, document.value.revision, projectId.value || undefined) : await api.saveToolSettingsDefaults(validation.value.value!, document.value.revision, projectId.value || undefined)
    document.value = saved; draft.value = baseline.value = formatSettings(saved.settings)
    effective.value = await api.getEffectiveToolSettings(agentId.value || undefined, projectId.value || undefined)
    capabilities.value = await api.getToolCapabilities(agentId.value || undefined, projectId.value || undefined).catch(err => ({ status: 'unavailable' as const, reason: err instanceof Error ? err.message : String(err) }))
    notify.success(t('toolsSettings.saved'))
    return true
  } catch (err) {
    stale.value = [409, 412, 428].includes((err as { status?: number }).status ?? 0)
    error.value = stale.value ? t('toolsSettings.stale') : err instanceof Error ? err.message : String(err)
    return false
  } finally { saving.value = false }
}
function bindingIds(kind: 'mcp' | 'skills'): string[] | null {
  const section = validation.value.value?.[kind] as Record<string, unknown> | undefined
  const value = section?.[kind === 'mcp' ? 'server_resource_ids' : 'resource_ids']
  return Array.isArray(value) ? value as string[] : null
}
function inheritedBindingIds(kind: 'mcp' | 'skills'): string[] | null {
  const section = effectiveValue.value[kind] as Record<string, unknown> | undefined
  const value = section?.[kind === 'mcp' ? 'server_resource_ids' : 'resource_ids']
  return Array.isArray(value) ? value as string[] : null
}
function hasBinding(kind: 'mcp' | 'skills'): boolean {
  const section = validation.value.value?.[kind] as Record<string, unknown> | undefined
  return Boolean(section && (kind === 'mcp' ? 'server_resource_ids' : 'resource_ids') in section)
}
function stageBinding(kind: 'mcp' | 'skills', ids: string[] | null, inherit = false) {
  if (!validation.value.value) return
  const next = { ...validation.value.value }; const section = { ...(next[kind] as Record<string, unknown> ?? {}) }
  const key = kind === 'mcp' ? 'server_resource_ids' : 'resource_ids'
  if (inherit && agentId.value) delete section[key]
  else section[key] = ids
  if (!Object.keys(section).length && agentId.value) delete next[kind]
  else next[kind] = section
  draft.value = formatSettings(next)
}
async function openAgentCenter() { if (agentId.value && await canLeave()) requestAgent(agentId.value) }
onMounted(async () => {
  window.addEventListener('beforeunload', beforeUnload)
  try { const [contract, roster, projectList, tools] = await Promise.all([api.getToolSettingsSchema(), api.listAgentDefinitions(), api.listProjects(), api.listTools()]); schema.value = contract; agents.value = roster; projects.value = projectList; catalogTools.value = tools; await loadContext() }
  catch (err) { error.value = err instanceof Error ? err.message : String(err) }
})
onBeforeUnmount(() => { ++generation; chooseLeave?.('cancel'); window.removeEventListener('beforeunload', beforeUnload) })
</script>
<template>
  <section class="tools-settings" :aria-busy="loading || saving">
    <ToolDraftDialog v-if="leaveDialog" @choice="chooseLeave?.($event)" />
    <div class="model-center-heading"><div><h2>{{ t('toolsSettings.title') }}</h2><p>{{ t('toolsSettings.subtitle') }}</p></div><UiButton variant="outline" size="sm" :disabled="loading || saving" @click="refresh">{{ t('common.refresh') }}</UiButton></div>
    <div class="tools-context-bar">
      <label>{{ t('toolsSettings.agent') }}<UiSelectField :value="agentId" :disabled="loading || saving" :aria-label="t('toolsSettings.agent')" :options="[{ value: '', label: t('toolsSettings.sharedDefaults') }, ...agents.map((agent) => ({ value: agent.id, label: agent.display_name || agent.slug || agent.name || agent.id }))]" @change="(value) => selectContext('agent', value)" /></label>
      <label>{{ t('toolsSettings.project') }}<UiSelectField :value="projectId" :disabled="loading || saving" :aria-label="t('toolsSettings.project')" :options="[{ value: '', label: t('toolsSettings.sharedOnly') }, ...projects.map((project) => ({ value: selectionKey(project), label: project.name }))]" @change="(value) => selectContext('project', value)" /></label>
      <UiButton v-if="agentId" variant="ghost" size="sm" @click="openAgentCenter">{{ t('toolsSettings.editAuthorization') }}</UiButton>
    </div>
    <p class="quiet">{{ t('toolsSettings.nextRun') }}</p><p v-if="error" class="tools-field-error" role="alert">{{ error }}</p>
    <div class="tools-tabs" role="tablist" :aria-label="t('toolsSettings.title')"><button v-for="(item, index) in tabs" :id="`tools-tab-${item.id}`" :key="item.id" role="tab" :aria-selected="tab === item.id" :aria-controls="`tools-panel-${item.id}`" :tabindex="tab === item.id ? 0 : -1" :class="{ active: tab === item.id }" @click="selectTab(item.id)" @keydown.right.prevent="selectTab(tabs[(index + 1) % tabs.length]!.id, true)" @keydown.left.prevent="selectTab(tabs[(index + tabs.length - 1) % tabs.length]!.id, true)" @keydown.home.prevent="selectTab('overview', true)" @keydown.end.prevent="selectTab('advanced', true)"><component :is="item.icon" :size="15" />{{ t(`toolsSettings.tabs.${item.id}`) }}</button></div>
    <div :id="`tools-panel-${tab}`" role="tabpanel" :aria-labelledby="`tools-tab-${tab}`" class="tools-stage">
      <div v-if="effective?.resource_diagnostics?.length" class="tools-validation" role="status"><strong>{{ t('toolsSettings.resourceDiagnostics') }}</strong><p v-for="item in effective.resource_diagnostics" :key="`${item.kind}:${item.resource_id}:${item.status}`" :title="diagnosticText(item) === item.reason ? undefined : item.reason">{{ item.resource_id || '—' }} · {{ diagnosticText(item) }}</p></div>
      <template v-if="tab === 'overview'"><div v-if="selectedAgent" class="tools-effective-card"><strong>{{ selectedAgent.display_name }}</strong><p>{{ t('toolsSettings.authorizationHint') }}</p><div class="model-capability-row"><span v-for="toolId in (effectiveToolIds ?? [])" :key="toolId">{{ toolId }}</span></div></div><ToolsOverviewPanel ref="overview" :key="`${projectId}:${agentId}`" :agents="agents" :agent-id="agentId" :project-id="projectId" :effective-tool-ids="effectiveToolIds" :capabilities="capabilities" :host-settings="schema?.host_settings" :effective="effective" /></template>
      <template v-else-if="tab === 'advanced'">
        <div class="tools-command-bar"><div><h3>{{ t('toolsSettings.advanced') }}</h3><p class="quiet">{{ t(agentId ? 'toolsSettings.overrideHint' : 'toolsSettings.advancedHint') }}</p><p v-if="focusPath" class="quiet">{{ t('toolsSettings.categoryFocus', { category: focusPath }) }}</p></div><UiBadge variant="outline">{{ t('toolsSettings.schemaVersion', { version: schema?.schema_version ?? '—' }) }} · {{ t('toolsSettings.revision') }} {{ document?.revision ?? '—' }}</UiBadge></div>
        <ToolJsonEditor v-if="document" v-model="draft" :schema="editorSchema" :label="t('toolsSettings.advanced')" :readonly="saving || loading" :focus-path="focusPath" @save="save" />
        <ul v-if="document && validation.issues.length" class="tools-validation" role="alert"><li v-for="issue in validation.issues" :key="issue.path + issue.message"><code>{{ issue.path }}</code> {{ issue.message }}</li></ul>
        <details v-if="diff.length" class="tools-diff" open><summary>{{ t('toolsSettings.diff', { count: diff.length }) }}</summary><div v-for="change in diff" :key="change.path"><code>{{ change.path }}</code><del>{{ valueText(change.before) }}</del><ins>{{ valueText(change.after) }}</ins></div></details>
        <details class="tools-effective-card"><summary>{{ t('toolsSettings.effective') }}</summary><pre>{{ formatSettings(effectiveValue) }}</pre></details>
        <UiButton variant="ghost" size="sm" :disabled="!schema || saving" @click="draft = formatSettings(agentId ? {} : schema?.defaults ?? {})">{{ t(agentId ? 'toolsSettings.inheritAll' : 'toolsSettings.restoreDefaults') }}</UiButton>
      </template>
      <template v-else>
        <div class="tools-command-bar"><div><h3>{{ t(`toolsSettings.tabs.${tab}`) }}</h3><p class="quiet">{{ t('toolsSettings.readonlyHint') }}</p></div><UiButton variant="outline" size="sm" @click="selectTab('advanced')">{{ t('toolsSettings.editAdvanced') }}</UiButton></div>
        <div v-if="tab === 'shell' || tab === 'search' || tab === 'web'" class="tools-effective-card"><h4>{{ t('toolsSettings.runtimeCapability') }}</h4><p v-if="!capabilities?.capabilities" class="quiet" :title="capabilities?.reason ?? undefined">{{ t(capabilities?.reason ? 'toolsSettings.diagnostic.host_unavailable' : 'toolsSettings.executionUnknown') }}</p><template v-else><template v-if="tab === 'shell'"><div v-for="shell in capabilities.capabilities.shells" :key="shell.id" class="tools-command-bar"><code>{{ shell.id }}</code><span>{{ t(shell.available ? 'toolsSettings.detected' : 'toolsSettings.unavailable') }} · {{ shell.path || capabilityReason(shell, { t, te }) }}</span></div><p>{{ t('toolsSettings.sandboxCapability', { supported: yesNo(capabilities.capabilities.sandbox?.supported), initialized: yesNo(capabilities.capabilities.sandbox?.initialized) }) }}</p></template><template v-else-if="tab === 'search'"><p>{{ t(capabilities.capabilities.ripgrep?.available ? 'toolsSettings.detected' : 'toolsSettings.unavailable') }} · {{ capabilities.capabilities.ripgrep?.path }}</p><p>{{ capabilityReason(capabilities.capabilities.ripgrep, { t, te }) }}</p></template><template v-else><p>{{ t('toolsSettings.webCapability', { supported: yesNo(capabilities.capabilities.web?.supported), guard: yesNo(capabilities.capabilities.web?.address_guard) }) }}</p><p class="quiet">{{ t('toolsSettings.webConnectivity') }}</p></template></template></div>
        <dl class="tools-value-list"><div v-for="row in sectionRows" :key="row.path"><dt><code>{{ row.path }}</code><span>{{ row.description }}</span></dt><dd><code>{{ valueText(row.value) }}</code><small>{{ sourceFor(row.path) }}</small></dd></div></dl>
        <p v-if="!sectionRows.length" class="quiet">{{ t(loading ? 'common.loading' : 'toolsSettings.noSettings') }}</p>
        <section v-if="familyTools.length" class="tools-effective-card"><h4>{{ t('toolsSettings.toolSurface') }}</h4><p class="quiet">{{ t('toolsSettings.executionUnknown') }}</p><div class="tools-resource-list"><div v-for="tool in familyTools" :key="tool.id" class="tools-resource-row"><span><strong>{{ tool.display_name }}</strong><code>{{ tool.id }}</code><small>{{ tool.source }} · {{ riskLabel(tool.risk) }}</small></span><UiBadge variant="outline">{{ t(agentId && !effectiveToolIds?.includes(tool.id) ? 'toolsSettings.notGranted' : 'toolsSettings.advertised') }}</UiBadge><small v-if="tool.requires_approval">{{ t('settings.approvalRequired') }}</small></div></div></section>
        <ToolResourcesPanel v-if="tab === 'mcp' || tab === 'skills'" ref="resources" :key="`${tab}:${projectId}`" :kind="tab" :project-id="projectId" :selection="bindingIds(tab)" :inherited="Boolean(agentId) && !hasBinding(tab)" :agent-scope="Boolean(agentId)" :effective-selection="inheritedBindingIds(tab)" :can-bind="Boolean(validation.value)" @binding="(ids, inherit) => stageBinding(tab === 'mcp' ? 'mcp' : 'skills', ids, inherit)" @dirty="resourceDirty = $event" @changed="resourceChanged" />
      </template>
    </div>
    <div v-if="document && (configDirty || tab === 'advanced')" class="tools-save-bar"><span>{{ t(configDirty ? 'toolsSettings.bindingUnsaved' : 'toolsSettings.savedState') }}</span><UiButton :disabled="!configDirty || validation.issues.length > 0 || saving || stale" @click="save">{{ t(saving ? 'toolsSettings.saving' : 'settings.save') }}</UiButton></div>
  </section>
</template>
