<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Braces, Package, Server, Wrench } from '@lucide/vue'
import { UiSelectField } from '@/components/ui'
import {
  api as baseApi,
  type AgentDefinitionDto,
  type ProjectDto,
  type ToolDescriptorDto,
  type ToolSettingsDocument,
  type ToolSettingsEffective,
  type ToolSettingsSchema,
} from '@/api'
import { scopedApi, projectStorageId } from '@/lib/storageScope'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState } from '@/composables/useErrorState'
import { consumeRequest, pendingToolAgentId, pendingToolId } from '@/lib/pageRequests'
import {
  formatSettings,
  settingsDiff,
  validateSettings,
  type ToolCapabilities,
  type ToolJsonSchema,
} from '@/settings/toolSettings'
import McpServersPanel from '@/settings/toolCenter/McpServersPanel.vue'
import SkillsLibraryPanel from '@/settings/toolCenter/SkillsLibraryPanel.vue'
import BuiltinToolsPanel from '@/settings/toolCenter/BuiltinToolsPanel.vue'
import ToolsAdvancedPanel from '@/settings/toolCenter/ToolsAdvancedPanel.vue'
import ContextOverlapPanel from '@/components/trajectory/ContextOverlapPanel.vue'
import { triggerDiagnoseInComposer } from '@/lib/offlineDiagnosticReport'
import type { ContextOverlapAuditResult, ContextOverlapPairSummary } from '@/lib/contextOverlapWinnowing'

const ToolDraftDialog = defineAsyncComponent(() => import('./ToolDraftDialog.vue'))

const { t } = useI18n()
const { notify } = useNotifications()

const tabs = [
  { id: 'mcp', icon: Server, labelKey: 'toolsSettings.center.tabs.mcp' },
  { id: 'skills', icon: Package, labelKey: 'toolsSettings.center.tabs.skills' },
  { id: 'builtin', icon: Wrench, labelKey: 'toolsSettings.center.tabs.builtin' },
  { id: 'advanced', icon: Braces, labelKey: 'toolsSettings.center.tabs.advanced' },
] as const

type Tab = typeof tabs[number]['id']

const tab = ref<Tab>('mcp')
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
const leaveDialog = ref(false)
let chooseLeave: ((value: 'save' | 'discard' | 'cancel') => void) | null = null
let generation = 0

const mcpPanelRef = ref<InstanceType<typeof McpServersPanel> | null>(null)

function sparseSchema(rule: ToolJsonSchema): ToolJsonSchema {
  return {
    ...rule,
    required: [],
    ...(rule.properties
      ? { properties: Object.fromEntries(Object.entries(rule.properties).map(([key, child]) => [key, sparseSchema(child)])) }
      : {}),
  }
}

const editorSchema = computed(() =>
  schema.value ? (agentId.value ? sparseSchema(schema.value.schema) : schema.value.schema) : null,
)
const validation = computed(() => validateSettings(draft.value, editorSchema.value, t, Boolean(agentId.value)))
const dirty = computed(() => draft.value !== baseline.value)
const diff = computed(() =>
  validation.value.value && document.value ? settingsDiff(document.value.settings, validation.value.value) : [],
)
const allowedToolIds = computed(() =>
  Array.isArray(effective.value?.allowed_tool_ids) ? (effective.value.allowed_tool_ids as string[]) : null,
)
const projectOptions = computed(() => [
  { value: '', label: t('toolsSettings.sharedOnly') },
  ...projects.value.map(project => ({ value: project.id, label: project.name || project.id })),
])

async function loadMeta() {
  const [schemaRes, agentsRes, projectsRes, toolsRes] = await Promise.all([
    api.getToolSettingsSchema().catch(() => null),
    baseApi.listAgentDefinitions().catch(() => [] as AgentDefinitionDto[]),
    baseApi.listProjects().catch(() => [] as ProjectDto[]),
    api.listTools().catch(() => [] as ToolDescriptorDto[]),
  ])
  if (schemaRes) schema.value = schemaRes
  agents.value = agentsRes
  projects.value = projectsRes
  catalogTools.value = toolsRes
}

async function refreshAgents() {
  agents.value = await baseApi.listAgentDefinitions().catch(() => agents.value)
}

async function loadState() {
  const token = ++generation
  loading.value = true
  error.value = ''
  try {
    const projectArg = projectId.value || undefined
    const agentArg = agentId.value || undefined
    const [docRes, effRes, capRes] = await Promise.all([
      agentArg
        ? api.getAgentToolSettings(agentArg)
        : api.getToolSettingsDefaults(),
      api.getEffectiveToolSettings(agentArg, projectArg).catch(() => null),
      api.getToolCapabilities(agentArg, projectArg).catch(() => null),
    ])
    if (token !== generation) return
    document.value = docRes
    effective.value = effRes
    capabilities.value = capRes
    const formatted = formatSettings(docRes.settings ?? {})
    baseline.value = formatted
    draft.value = formatted
    stale.value = false
  } catch (err) {
    if (token !== generation) return
    const errState = toErrorState(err, 'toolsSettings')
    error.value = errState.details || errState.message
  } finally {
    if (token === generation) loading.value = false
  }
}

async function selectAgent(next: string) {
  if (next === agentId.value) return
  if (!(await canLeave())) return
  agentId.value = next
  await loadState()
}

async function selectProject(next: string) {
  if (next === projectId.value) return
  if (!(await canLeave())) return
  projectId.value = next
  await loadState()
}

async function save(): Promise<boolean> {
  if (!validation.value.value || validation.value.issues.length || !document.value) return false
  saving.value = true
  error.value = ''
  stale.value = false
  try {
    const saved = agentId.value
      ? await api.saveAgentToolSettings(
          agentId.value,
          validation.value.value,
          document.value.revision,
          projectId.value || undefined,
        )
      : await api.saveToolSettingsDefaults(
          validation.value.value,
          document.value.revision,
          projectId.value || undefined,
        )
    document.value = saved
    const formatted = formatSettings(saved.settings ?? {})
    baseline.value = formatted
    draft.value = formatted
    notify.success(t('toolsSettings.saved'))
    await loadState()
    return true
  } catch (err) {
    const state = toErrorState(err, 'toolsSettings')
    if (state.status === 409 || state.status === 412 || state.status === 428) {
      stale.value = true
    } else {
      error.value = state.details || state.message
    }
    return false
  } finally {
    saving.value = false
  }
}

function restoreDefaults() {
  if (!schema.value?.defaults) return
  draft.value = formatSettings(schema.value.defaults)
}

function inheritAll() {
  draft.value = formatSettings({})
}

async function canLeave(): Promise<boolean> {
  if (!dirty.value) return true
  leaveDialog.value = true
  const choice = await new Promise<'save' | 'discard' | 'cancel'>((resolve) => {
    chooseLeave = resolve
  })
  leaveDialog.value = false
  chooseLeave = null
  if (choice === 'cancel') return false
  if (choice === 'discard') {
    draft.value = baseline.value
    stale.value = false
    return true
  }
  return save()
}

function resolveLeave(choice: 'save' | 'discard' | 'cancel') {
  chooseLeave?.(choice)
}

function handleDiagnoseOverlap(audit: ContextOverlapAuditResult, pair?: ContextOverlapPairSummary) {
  void triggerDiagnoseInComposer({ overlap: audit, selectedOverlapPair: pair ?? null })
}

async function onTabKeydown(event: KeyboardEvent, index: number) {
  let next = index
  if (event.key === 'ArrowRight') next = (index + 1) % tabs.length
  else if (event.key === 'ArrowLeft') next = (index - 1 + tabs.length) % tabs.length
  else if (event.key === 'Home') next = 0
  else if (event.key === 'End') next = tabs.length - 1
  else return
  event.preventDefault()
  const target = tabs[next]!
  tab.value = target.id
  await nextTick()
  window.document.getElementById(`tools-tab-${target.id}`)?.focus()
}

watch(
  pendingToolId,
  (id) => {
    if (id) tab.value = 'builtin'
  },
  { immediate: true },
)

consumeRequest(pendingToolAgentId, (id) => {
  tab.value = 'advanced'
  void selectAgent(id)
})

onMounted(async () => {
  await loadMeta()
  await loadState()
})

onBeforeUnmount(() => {
  chooseLeave?.('cancel')
})

defineExpose({ canLeave })
</script>

<template>
  <div class="section-content tool-center-root">
    <div class="tool-center-header">
      <div class="tool-center-header-text">
        <h2 class="section-title">{{ t('toolsSettings.title') }}</h2>
        <p class="section-desc">{{ t('toolsSettings.subtitle') }}</p>
      </div>
      <label class="tool-center-project-select">
        <span>{{ t('toolsSettings.project') }}</span>
        <UiSelectField
          :model-value="projectId"
          :options="projectOptions"
          :disabled="loading"
          @update:model-value="val => selectProject(String(val))"
        />
      </label>
    </div>

    <div class="tool-center-tablist" role="tablist" :aria-label="t('toolsSettings.title')">
      <button
        v-for="(item, index) in tabs"
        :id="`tools-tab-${item.id}`"
        :key="item.id"
        type="button"
        role="tab"
        class="tool-center-tab"
        :class="{ active: tab === item.id }"
        :aria-selected="tab === item.id"
        @click="tab = item.id"
        @keydown="onTabKeydown($event, index)"
      >
        <component :is="item.icon" :size="15" aria-hidden="true" />
        <span>{{ t(item.labelKey) }}</span>
      </button>
    </div>

    <div class="tool-center-tab-panel" role="tabpanel" :aria-labelledby="`tools-tab-${tab}`">
      <McpServersPanel
        v-if="tab === 'mcp'"
        ref="mcpPanelRef"
        :project-id="projectId || undefined"
      />

      <SkillsLibraryPanel
        v-else-if="tab === 'skills'"
        :project-id="projectId || undefined"
        @mcp-imported="mcpPanelRef?.refresh()"
        @agents-imported="refreshAgents"
      />

      <BuiltinToolsPanel
        v-else-if="tab === 'builtin'"
        :tools="catalogTools"
        :capabilities="capabilities"
        :allowed-tool-ids="allowedToolIds"
        :loading="loading"
      />

      <ToolsAdvancedPanel
        v-else
        :agent-id="agentId"
        :agents="agents"
        :loading="loading"
        :saving="saving"
        :dirty="dirty"
        :stale="stale"
        :error="error"
        :schema-version="schema?.schema_version"
        :editor-schema="editorSchema"
        :draft="draft"
        :validation-issues="validation.issues"
        :diff="diff"
        :document="document"
        :effective="effective"
        :capabilities="capabilities"
        :defaults="schema?.defaults"
        @update:agent-id="selectAgent"
        @update:draft="val => { draft = val }"
        @save="save"
        @restore-defaults="restoreDefaults"
        @inherit-all="inheritAll"
      />
    </div>

    <div class="tool-center-overlap-audit" data-testid="tool-center-overlap-section">
      <ContextOverlapPanel
        auto-load-mode="tools"
        :tools="catalogTools"
        :project-id="projectId || undefined"
        collapsible
        :default-open="false"
        @diagnose-overlap="handleDiagnoseOverlap"
      />
    </div>

    <ToolDraftDialog v-if="leaveDialog" @choice="resolveLeave" />
  </div>
</template>

<style scoped>
.tool-center-overlap-audit {
  margin-top: 12px;
}
</style>
