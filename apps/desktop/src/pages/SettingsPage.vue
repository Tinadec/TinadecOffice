<script setup lang="ts">
import {
  ArrowLeft,
  Bot,
  Check,
  CheckCircle2,
  ChevronRight,
  Circle,
  Cloud,
  Cpu,
  Edit3,
  FileText,
  Globe,
  HardDrive,
  Info,
  KeyRound,
  LayoutGrid,
  List,
  Monitor,
  Moon,
  MoreHorizontal,
  Palette,
  Plus,
  Save,
  Server,
  Settings2,
  ShieldCheck,
  Sun,
  Terminal,
  Trash2,
  Workflow,
  X
} from '@lucide/vue'
import { computed, nextTick, reactive, ref } from 'vue' 
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useTheme } from '../composables/useTheme'
import {
  api,
  type AgentCandidateDto,
  type AgentModeDto,
  type AgentProfileDto,
  type ModelProviderInstanceDto,
  type ModelRouteDto,
  type SaveModelProviderInstanceInput,
  type ToolDescriptorDto
} from '../api'
import {
  PROVIDER_TEMPLATES,
  PROVIDER_CATEGORIES,
  findTemplate,
  type ProviderTemplate,
  type ProviderCategory
} from '../providerTemplates'
import { UiButton, UiInput, UiCard, UiBadge, UiLabel, UiSwitch } from '@/components/ui'
import AgentTopologyCanvas from '@/components/AgentTopologyCanvas.vue'

type SettingsSection = 'model' | 'agents' | 'appearance' | 'language' | 'apiDocs' | 'about'

interface ProviderForm {
  id: string
  driver: string
  display_name: string
  connection_kind: string
  base_url: string
  model: string
  api_key: string
  clear_api_key: boolean
  binary_path: string
  home_path: string
  server_url: string
  launch_args: string
  enabled: boolean
}

const CATEGORY_ICONS: Record<ProviderCategory, typeof Cloud> = {
  'cloud-api': Cloud,
  'local-server': HardDrive,
  'agent-cli': Terminal,
  'custom': Settings2,
}

const { t, locale } = useI18n()
const router = useRouter()
const { theme, setTheme, accentColor, setAccentColor, accentColors } = useTheme()

function minimizeWindow() {
  window.tinadec?.minimizeWindow?.()
}

function maximizeWindow() {
  window.tinadec?.maximizeWindow?.()
}

function closeWindow() {
  window.tinadec?.closeWindow?.()
}

const activeSection = ref<SettingsSection>('model')
const providers = ref<ModelProviderInstanceDto[]>([])
const routes = ref<ModelRouteDto[]>([])
const agentModes = ref<AgentModeDto[]>([])
const agents = ref<AgentProfileDto[]>([])
const agentCandidates = ref<AgentCandidateDto[]>([])
const availableTools = ref<ToolDescriptorDto[]>([])
const selectedProviderId = ref('')
const selectedAgentId = ref('')
const configuringAgentId = ref('')
const agentRouteProviderId = ref('')
const agentRouteModel = ref('')
const agentEditTools = ref<string[]>([])
const agentEditCapabilities = ref<string[]>([])
const agentEditSystemPrompt = ref('')
const agentEditDescription = ref('')
const agentNewCapability = ref('')
const selectedProviderDetailId = ref('')
const confirmDeleteId = ref('')
const busy = ref(false)
const loading = ref(false)
const showModal = ref(false)
const agentViewMode = ref<'topology' | 'list'>('topology')

const providerForm = reactive<ProviderForm>({
  id: '',
  driver: 'openai-compatible',
  display_name: 'OpenAI Compatible',
  connection_kind: 'api-key',
  base_url: 'https://api.openai.com/v1',
  model: 'gpt-5.4-mini',
  api_key: '',
  clear_api_key: false,
  binary_path: '',
  home_path: '',
  server_url: '',
  launch_args: '',
  enabled: true
})

const navItems = computed(() => [
  { key: 'model' as const, icon: KeyRound, label: t('settings.model') },
  { key: 'agents' as const, icon: Workflow, label: t('settings.agents') },
  { key: 'appearance' as const, icon: Palette, label: t('settings.appearance') },
  { key: 'language' as const, icon: Globe, label: t('settings.language') },
  { key: 'apiDocs' as const, icon: FileText, label: t('settings.apiDocs') },
  { key: 'about' as const, icon: Info, label: t('settings.about') },
])

const currentTemplate = computed(() => findTemplate(providerForm.driver))

const chatRoute = computed(() =>
  routes.value.find((route) => route.purpose === 'planner') ?? routes.value.find((route) => route.purpose === 'chat') ?? null
)
const chatProvider = computed(() =>
  providers.value.find((provider) => provider.id === chatRoute.value?.provider_instance_id) ?? null
)

const formFields = computed(() => currentTemplate.value?.fields ?? {
  base_url: true, model: true, api_key: true,
  binary_path: false, home_path: false, server_url: false, launch_args: false
})
const formPlaceholders = computed(() => currentTemplate.value?.placeholders ?? {})

const addedDriverSet = computed(() => new Set(providers.value.map((p) => p.driver)))

const templatesByCategory = computed(() => {
  const map = new Map<ProviderCategory, ProviderTemplate[]>()
  for (const cat of PROVIDER_CATEGORIES) {
    map.set(cat.key, PROVIDER_TEMPLATES.filter((t) => t.category === cat.key))
  }
  return map
})

const selectedProvider = computed(() =>
  providers.value.find((provider) => provider.id === selectedProviderId.value) ?? null
)
const selectedAgent = computed(() =>
  agents.value.find((agent) => agent.id === selectedAgentId.value) ?? agents.value[0] ?? null
)
const configuringAgent = computed(() =>
  agents.value.find((agent) => agent.id === configuringAgentId.value) ?? null
)
const planningAgents = computed(() => agents.value.filter((agent) => agent.layer === 'planning'))
const executionAgents = computed(() => agents.value.filter((agent) => agent.layer === 'execution'))
const configuredAgentMode = computed(() => agentModes.value.find((mode) => mode.id === configuringAgent.value?.mode) ?? null)

function brandColor(driver: string) {
  return findTemplate(driver)?.brand_color ?? '#58a6ff'
}

function brandBg(driver: string) {
  return findTemplate(driver)?.brand_bg ?? 'rgba(88,166,255,0.12)'
}

function setLocale(lang: string) {
  locale.value = lang
  localStorage.setItem('tinadec-locale', lang)
}

function fillForm(provider: ModelProviderInstanceDto) {
  providerForm.id = provider.id
  providerForm.driver = provider.driver
  providerForm.display_name = provider.display_name
  providerForm.connection_kind = provider.connection_kind
  providerForm.base_url = provider.base_url ?? ''
  providerForm.model = provider.model ?? ''
  providerForm.api_key = ''
  providerForm.clear_api_key = false
  providerForm.binary_path = provider.binary_path ?? ''
  providerForm.home_path = provider.home_path ?? ''
  providerForm.server_url = provider.server_url ?? ''
  providerForm.launch_args = provider.launch_args ?? ''
  providerForm.enabled = provider.enabled
}

function applyTemplateDefaults(template: ProviderTemplate) {
  providerForm.driver = template.driver
  providerForm.display_name = t(template.display_name_key)
  providerForm.connection_kind = template.connection_kind
  providerForm.base_url = template.default_base_url ?? ''
  providerForm.model = template.default_model ?? ''
  providerForm.binary_path = ''
  providerForm.home_path = ''
  providerForm.server_url = template.driver === 'opencode' ? template.default_base_url ?? '' : ''
  providerForm.launch_args = ''
}

function openAddModal(template?: ProviderTemplate) {
  selectedProviderId.value = ''
  providerForm.id = ''
  if (template) {
    applyTemplateDefaults(template)
  } else {
    applyTemplateDefaults(PROVIDER_TEMPLATES[0])
  }
  providerForm.api_key = ''
  providerForm.clear_api_key = false
  providerForm.enabled = true
  showModal.value = true
}

function openEditModal(provider: ModelProviderInstanceDto) {
  selectedProviderId.value = provider.id
  fillForm(provider)
  showModal.value = true
}

function toggleProviderDetail(providerId: string) {
  selectedProviderDetailId.value = selectedProviderDetailId.value === providerId ? '' : providerId
}

async function toggleProviderEnabled(provider: ModelProviderInstanceDto) {
  busy.value = true
  try {
    const tmpl = findTemplate(provider.driver)
    const payload: SaveModelProviderInstanceInput = {
      id: provider.id,
      driver: provider.driver,
      display_name: provider.display_name,
      connection_kind: provider.connection_kind,
      base_url: provider.base_url,
      model: provider.model,
      clear_api_key: false,
      binary_path: provider.binary_path,
      home_path: provider.home_path,
      server_url: provider.server_url,
      launch_args: provider.launch_args,
      capabilities: provider.capabilities,
      enabled: !provider.enabled
    }
    await api.saveModelProvider(provider.id, payload)
    await loadModelCenter()
  } finally {
    busy.value = false
  }
}

async function deleteProvider(providerId: string) {
  busy.value = true
  try {
    await api.deleteModelProvider(providerId)
    if (selectedProviderDetailId.value === providerId) {
      selectedProviderDetailId.value = ''
    }
    await loadModelCenter()
  } finally {
    busy.value = false
    confirmDeleteId.value = ''
  }
}

function closeModal() {
  showModal.value = false
}

async function loadModelCenter() {
  loading.value = true
  try {
    const [instances, modelRoutes] = await Promise.all([
      api.listModelProviders(),
      api.listModelRoutes()
    ])
    providers.value = instances
    routes.value = modelRoutes

    const selected = instances.find((provider) => provider.id === selectedProviderId.value) ?? instances[0]
    if (selected) {
      selectedProviderId.value = selected.id
    }
  } finally {
    loading.value = false
  }
}

async function loadAgentCenter() {
  loading.value = true
  try {
    const [modes, agentList, candidates] = await Promise.all([
      api.listAgentModes(),
      api.listAgents(),
      api.listAgentCandidates()
    ])
    agentModes.value = modes
    agents.value = agentList
    agentCandidates.value = candidates
    // Tools list is non-critical — load independently so a missing Core doesn't block agent display
    api.listTools().then((tools) => { availableTools.value = tools }).catch(() => { availableTools.value = [] })
    if (!agentList.some((agent) => agent.id === selectedAgentId.value)) {
      selectedAgentId.value = agentList[0]?.id ?? ''
    }
  } finally {
    loading.value = false
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
    await api.saveAgent(agent.id, {
      name: agent.name,
      layer: agent.layer,
      agent_type: agent.agent_type,
      mode: agent.mode,
      description: agent.description,
      model_route_purpose: agent.model_route_purpose,
      allowed_tools: agent.allowed_tools,
      capabilities: agent.capabilities,
      system_prompt: agent.system_prompt,
      enabled
    })
    await loadAgentCenter()
  } finally {
    busy.value = false
  }
}

async function saveAgentProfile() {
  if (!configuringAgent.value) return
  busy.value = true
  try {
    await api.saveAgent(configuringAgent.value.id, {
      name: configuringAgent.value.name,
      layer: configuringAgent.value.layer,
      agent_type: configuringAgent.value.agent_type,
      mode: configuringAgent.value.mode,
      description: agentEditDescription.value,
      model_route_purpose: configuringAgent.value.model_route_purpose,
      allowed_tools: agentEditTools.value,
      capabilities: agentEditCapabilities.value,
      system_prompt: agentEditSystemPrompt.value || null,
      enabled: configuringAgent.value.enabled
    })
    await loadAgentCenter()
    // Re-sync edit state from the saved agent
    const updated = agents.value.find((a) => a.id === configuringAgentId.value)
    if (updated) {
      agentEditTools.value = [...updated.allowed_tools]
      agentEditCapabilities.value = [...updated.capabilities]
      agentEditSystemPrompt.value = updated.system_prompt ?? ''
      agentEditDescription.value = updated.description
    }
  } finally {
    busy.value = false
  }
}

function toggleAgentTool(toolId: string) {
  const idx = agentEditTools.value.indexOf(toolId)
  if (idx >= 0) {
    agentEditTools.value.splice(idx, 1)
  } else {
    agentEditTools.value.push(toolId)
  }
}

function removeAgentCapability(cap: string) {
  const idx = agentEditCapabilities.value.indexOf(cap)
  if (idx >= 0) {
    agentEditCapabilities.value.splice(idx, 1)
  }
}

function addAgentCapability() {
  const cap = agentNewCapability.value.trim()
  if (cap && !agentEditCapabilities.value.includes(cap)) {
    agentEditCapabilities.value.push(cap)
    agentNewCapability.value = ''
  }
}

function agentRoute(agent: AgentProfileDto | null) {
  if (!agent) return null
  return routes.value.find((route) => route.purpose === agent.model_route_purpose) ?? null
}

function agentRouteProvider(agent: AgentProfileDto | null) {
  const route = agentRoute(agent)
  return providers.value.find((provider) => provider.id === route?.provider_instance_id) ?? null
}

function openAgentConfig(agent: AgentProfileDto) {
  selectedAgentId.value = agent.id
  configuringAgentId.value = agent.id
  agentEditTools.value = [...(agent.allowed_tools ?? [])]
  agentEditCapabilities.value = [...(agent.capabilities ?? [])]
  agentEditSystemPrompt.value = agent.system_prompt ?? ''
  agentEditDescription.value = agent.description ?? ''
  agentNewCapability.value = ''
  try {
    const route = agentRoute(agent)
    if (route && providers.value.some((p) => p.id === route.provider_instance_id)) {
      agentRouteProviderId.value = route.provider_instance_id
      agentRouteModel.value = route.model ?? providers.value.find((p) => p.id === route.provider_instance_id)?.model ?? ''
    } else if (providers.value.length > 0) {
      agentRouteProviderId.value = providers.value[0].id
      agentRouteModel.value = providers.value[0].model ?? ''
    } else {
      agentRouteProviderId.value = ''
      agentRouteModel.value = ''
    }
  } catch {
    agentRouteProviderId.value = ''
    agentRouteModel.value = ''
  }
  // Scroll to config panel after next tick
  nextTick(() => {
    const panel = document.querySelector('.agent-detail-panel')
    if (panel) {
      panel.scrollIntoView({ behavior: 'smooth', block: 'start' })
    }
  })
}

async function saveAgentRoute(agent: AgentProfileDto) {
  if (!agentRouteProviderId.value) return
  busy.value = true
  try {
    await api.saveModelRoute(agent.model_route_purpose, agentRouteProviderId.value, agentRouteModel.value || null)
    await loadModelCenter()
    const updatedRoute = routes.value.find((r) => r.purpose === agent.model_route_purpose)
    if (updatedRoute) {
      agentRouteProviderId.value = updatedRoute.provider_instance_id
      agentRouteModel.value = updatedRoute.model ?? ''
    }
  } finally {
    busy.value = false
  }
}

async function saveProvider() {
  busy.value = true
  try {
    const tmpl = currentTemplate.value
    const payload: SaveModelProviderInstanceInput = {
      id: providerForm.id || undefined,
      driver: providerForm.driver,
      display_name: providerForm.display_name,
      connection_kind: providerForm.connection_kind,
      base_url: formFields.value.base_url ? (providerForm.base_url || null) : null,
      model: formFields.value.model ? (providerForm.model || null) : null,
      api_key: formFields.value.api_key ? (providerForm.api_key || null) : null,
      clear_api_key: providerForm.clear_api_key,
      binary_path: formFields.value.binary_path ? (providerForm.binary_path || null) : null,
      home_path: formFields.value.home_path ? (providerForm.home_path || null) : null,
      server_url: formFields.value.server_url ? (providerForm.server_url || null) : null,
      launch_args: formFields.value.launch_args ? (providerForm.launch_args || null) : null,
      capabilities: tmpl?.capabilities ?? [],
      enabled: providerForm.enabled
    }

    const saved = providerForm.id
      ? await api.saveModelProvider(providerForm.id, payload)
      : await api.createModelProvider(payload)

    selectedProviderId.value = saved.id
    showModal.value = false
    await loadModelCenter()
  } finally {
    busy.value = false
  }
}

function connectionKindLabel(kind: string) {
  if (kind === 'cli') return t('settings.connectionKindCli')
  if (kind === 'local-server') return t('settings.connectionKindLocal')
  return t('settings.connectionKindApiKey')
}

function agentTypeLabel(type: string) {
  const map: Record<string, string> = {
    // Layer 1 · Planning 主动智能体
    meeting: t('settings.agentTypeMeeting'),
    'context-compressor': t('settings.agentTypeContextCompressor'),
    evolver: t('settings.agentTypeEvolver'),
    'tool-assistant': t('settings.agentTypeToolAssistant'),
    supervisor: t('settings.agentTypeSupervisor'),
    'skill-learner': t('settings.agentTypeSkillLearner'),
    // Layer 2 · Execution 被动执行类智能体
    'task-planner': t('settings.agentTypeTaskPlanner'),
    'test-multimodal': t('settings.agentTypeTestMultimodal'),
    'code-explorer': t('settings.agentTypeCodeExplorer'),
    'search-specialist': t('settings.agentTypeSearchSpecialist'),
    'file-finder': t('settings.agentTypeFileFinder'),
    'git-manager': t('settings.agentTypeGitManager'),
    'code-writer': t('settings.agentTypeCodeWriter'),
    designer: t('settings.agentTypeDesigner'),
    // Legacy types (kept for backward compatibility)
    chair: t('settings.agentTypeMeeting'),
    planner: t('settings.agentTypeTaskPlanner'),
    'tool-manager': t('settings.agentTypeToolAssistant'),
    'evolution-algorithm': t('settings.agentTypeEvolver'),
    executor: t('settings.agentTypeCodeWriter'),
    reviewer: t('settings.agentTypeSupervisor'),
  }
  return map[type] ?? type
}

function agentLayerLabel(layer: string) {
  const map: Record<string, string> = {
    planning: t('settings.agentLayerPlanning'),
    execution: t('settings.agentLayerExecution'),
    evolution: t('settings.agentLayerEvolution'),
  }
  return map[layer] ?? layer
}

function agentModeLabel(mode: string) {
  const map: Record<string, string> = {
    chat: t('settings.agentModeChat'),
    plan: t('settings.agentModePlan'),
    execute: t('settings.agentModeExecute'),
    review: t('settings.agentModeReview'),
  }
  return map[mode] ?? mode
}

function statusLabel(status: string) {
  if (status === 'ready') return t('settings.statusReady')
  if (status === 'needs_key') return t('settings.statusNeedsKey')
  if (status === 'disabled') return t('settings.statusDisabled')
  return t('settings.statusNotConfigured')
}

function statusVariant(status: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (status === 'ready') return 'default'
  if (status === 'needs_key' || status === 'not_configured') return 'destructive'
  if (status === 'disabled') return 'secondary'
  return 'outline'
}

loadModelCenter()
loadAgentCenter()
</script>

<template>
  <div class="settings-page">
    <div class="settings-window-controls">
      <UiButton variant="ghost" size="icon" class="window-btn minimize" :title="t('app.minimize')" @click="minimizeWindow">
        <Minus :size="14" />
      </UiButton>
      <UiButton variant="ghost" size="icon" class="window-btn maximize" :title="t('app.maximize')" @click="maximizeWindow">
        <Square :size="12" />
      </UiButton>
      <UiButton variant="ghost" size="icon" class="window-btn close" :title="t('app.close')" @click="closeWindow">
        <X :size="14" />
      </UiButton>
    </div>
    <div class="settings-shell">
      <nav class="settings-nav">
        <div class="settings-nav-header">
          <UiButton variant="ghost" size="icon" :title="t('settings.back')" @click="router.push('/')">
            <ArrowLeft :size="16" />
          </UiButton>
          <span>{{ t('settings.title') }}</span>
        </div>
        <UiButton
          v-for="item in navItems"
          :key="item.key"
          variant="ghost"
          size="sm"
          class="settings-nav-item w-full justify-start"
          :class="{ active: activeSection === item.key }"
          @click="activeSection = item.key"
        >
          <component :is="item.icon" :size="16" />
          {{ item.label }}
        </UiButton>
      </nav>

      <div class="settings-content">
        <template v-if="activeSection === 'model'">
          <div class="model-center-heading">
            <div>
              <h2>{{ t('settings.modelCenter') }}</h2>
              <p>{{ t('settings.modelCenterSubtitle') }}</p>
            </div>
            <UiButton variant="outline" size="sm" :disabled="loading" @click="loadModelCenter">
              <Server :size="14" />
              <span>{{ t('settings.refresh') }}</span>
            </UiButton>
          </div>

          <div class="model-section-header">
            <h3>{{ t('settings.addedProviders') }}</h3>
            <UiButton variant="ghost" size="icon" :title="t('settings.newProvider')" @click="openAddModal()">
              <Plus :size="15" />
            </UiButton>
          </div>

          <div v-if="providers.length > 0" class="model-provider-grid">
            <div
              v-for="provider in providers"
              :key="provider.id"
              class="model-provider-card-wrapper"
            >
              <button
                class="model-provider-card"
                :style="{ background: brandBg(provider.driver), borderColor: brandColor(provider.driver) + '40' }"
                @click="toggleProviderDetail(provider.id)"
              >
                <span class="provider-brand-icon" :style="{ color: brandColor(provider.driver) }" v-html="findTemplate(provider.driver)?.icon ?? ''"></span>
                <div class="model-provider-card-main">
                  <span class="model-provider-name">{{ provider.display_name }}</span>
                  <span class="model-provider-meta">{{ connectionKindLabel(provider.connection_kind) }} · {{ provider.model || provider.driver }}</span>
                </div>
                <div class="model-provider-card-actions">
                  <UiBadge :variant="statusVariant(provider.status)">
                    <Circle :size="8" />
                    {{ statusLabel(provider.status) }}
                  </UiBadge>
                  <ChevronRight :size="14" class="provider-chevron" :class="{ open: selectedProviderDetailId === provider.id }" />
                </div>
              </button>
              <Transition name="detail-slide">
              <div v-if="selectedProviderDetailId === provider.id" class="provider-detail-panel">
                <div class="provider-detail-head">
                  <span class="provider-detail-logo" :style="{ background: brandBg(provider.driver), borderColor: brandColor(provider.driver) + '30' }" v-html="findTemplate(provider.driver)?.icon ?? ''"></span>
                  <div class="provider-detail-info">
                    <strong>{{ provider.display_name }}</strong>
                    <span class="provider-detail-driver">{{ provider.driver }} · {{ connectionKindLabel(provider.connection_kind) }}</span>
                  </div>
                  <UiBadge :variant="statusVariant(provider.status)">
                    <Circle :size="8" />
                    {{ statusLabel(provider.status) }}
                  </UiBadge>
                </div>

                <div class="provider-detail-section">
                  <div class="provider-detail-section-title">{{ t('settings.connectionConfig') }}</div>
                  <div class="provider-detail-grid">
                    <div v-if="provider.base_url" class="provider-detail-cell">
                      <span class="provider-detail-label">{{ t('settings.baseUrl') }}</span>
                      <span class="provider-detail-value provider-detail-mono">{{ provider.base_url }}</span>
                    </div>
                    <div v-if="provider.model" class="provider-detail-cell">
                      <span class="provider-detail-label">{{ t('settings.modelLabel') }}</span>
                      <span class="provider-detail-value provider-detail-mono">{{ provider.model }}</span>
                    </div>
                    <div class="provider-detail-cell">
                      <span class="provider-detail-label">{{ t('settings.apiKey') }}</span>
                      <span class="provider-detail-value">
                        <span :class="['provider-key-indicator', provider.has_api_key ? 'has-key' : 'no-key']"></span>
                        {{ provider.has_api_key ? t('settings.apiKeyStored') : t('settings.apiKeyNotSet') }}
                      </span>
                    </div>
                    <div class="provider-detail-cell">
                      <span class="provider-detail-label">{{ t('settings.connectionKind') }}</span>
                      <span class="provider-detail-value">{{ connectionKindLabel(provider.connection_kind) }}</span>
                    </div>
                    <div v-if="provider.binary_path" class="provider-detail-cell">
                      <span class="provider-detail-label">{{ t('settings.binaryPath') }}</span>
                      <span class="provider-detail-value provider-detail-mono">{{ provider.binary_path }}</span>
                    </div>
                    <div v-if="provider.server_url" class="provider-detail-cell">
                      <span class="provider-detail-label">{{ t('settings.serverUrl') }}</span>
                      <span class="provider-detail-value provider-detail-mono">{{ provider.server_url }}</span>
                    </div>
                    <div v-if="provider.home_path" class="provider-detail-cell">
                      <span class="provider-detail-label">{{ t('settings.homePath') }}</span>
                      <span class="provider-detail-value provider-detail-mono">{{ provider.home_path }}</span>
                    </div>
                  </div>
                </div>

                <div v-if="provider.capabilities.length > 0" class="provider-detail-section">
                  <div class="provider-detail-section-title">{{ t('settings.capabilities') }}</div>
                  <div class="model-capability-row">
                    <span v-for="cap in provider.capabilities" :key="cap" class="provider-cap-tag">{{ cap }}</span>
                  </div>
                </div>

                <div v-if="provider.status_message" class="provider-status-note">
                  <Terminal :size="14" />
                  <span>{{ provider.status_message }}</span>
                </div>

                <div class="provider-detail-actions">
                  <UiButton variant="outline" size="sm" @click="openEditModal(provider)">
                    <Edit3 :size="14" />
                    <span>{{ t('settings.editConfig') }}</span>
                  </UiButton>
                  <UiButton variant="outline" size="sm" @click="toggleProviderEnabled(provider)">
                    <component :is="provider.enabled ? X : Check" :size="14" />
                    <span>{{ provider.enabled ? t('settings.disable') : t('settings.enable') }}</span>
                  </UiButton>
                  <span class="provider-action-spacer"></span>
                  <UiButton v-if="confirmDeleteId !== provider.id" variant="ghost" size="sm" class="provider-delete-btn" @click="confirmDeleteId = provider.id">
                    <Trash2 :size="14" />
                    <span>{{ t('settings.delete') }}</span>
                  </UiButton>
                  <template v-else>
                    <span class="delete-confirm-text">{{ t('settings.confirmDeleteProvider') }}</span>
                    <UiButton variant="destructive" size="sm" :disabled="busy" @click="deleteProvider(provider.id)">{{ t('settings.confirmDelete') }}</UiButton>
                    <UiButton variant="ghost" size="sm" @click="confirmDeleteId = ''">{{ t('settings.cancel') }}</UiButton>
                  </template>
                </div>
              </div>
              </Transition>
            </div>
          </div>
          <p v-else class="quiet">{{ t('settings.noProvider') }}</p>

          <template v-for="cat in PROVIDER_CATEGORIES" :key="cat.key">
            <div class="model-section-header">
              <h3>
                <component :is="CATEGORY_ICONS[cat.key]" :size="14" style="vertical-align:-2px" />
                {{ t(cat.labelKey) }}
              </h3>
            </div>
            <div class="model-provider-grid">
              <button
                v-for="template in templatesByCategory.get(cat.key)"
                :key="template.driver"
                class="model-provider-card add"
                :class="{ 'already-added': addedDriverSet.has(template.driver) }"
                :style="{ background: template.brand_bg, borderColor: template.brand_color + '40' }"
                @click="addedDriverSet.has(template.driver) ? toggleProviderDetail(providers.find(p => p.driver === template.driver)?.id ?? '') : openAddModal(template)"
              >
                <div class="add-label">
                  <span class="provider-brand-icon" :style="{ color: template.brand_color }" v-html="template.icon"></span>
                  <span>{{ t(template.display_name_key) }}</span>
                  <small>{{ t(template.summary_key) }}</small>
                  <span class="add-connection-kind">{{ connectionKindLabel(template.connection_kind) }}</span>
                </div>
                <CheckCircle2 v-if="addedDriverSet.has(template.driver)" :size="16" style="color:var(--accent-success); position:absolute; top:8px; right:8px" />
              </button>
            </div>
          </template>
        </template>

        <template v-if="activeSection === 'agents'">
          <div class="model-center-heading">
            <div>
              <h2>{{ t('settings.agentCenter') }}</h2>
              <p>{{ t('settings.agentCenterSubtitle') }}</p>
            </div>
            <div class="agent-heading-actions">
              <div class="agent-view-toggle">
                <button
                  :class="['agent-view-btn', { active: agentViewMode === 'topology' }]"
                  :title="t('settings.topologyView')"
                  @click="agentViewMode = 'topology'"
                >
                  <LayoutGrid :size="15" />
                </button>
                <button
                  :class="['agent-view-btn', { active: agentViewMode === 'list' }]"
                  :title="t('settings.listView')"
                  @click="agentViewMode = 'list'"
                >
                  <List :size="15" />
                </button>
              </div>
              <UiButton variant="outline" size="sm" :disabled="loading" @click="loadAgentCenter">
                <Server :size="14" />
                <span>{{ t('settings.refresh') }}</span>
              </UiButton>
            </div>
          </div>

          <div v-if="agentViewMode === 'topology'" class="agent-topology-section">
            <AgentTopologyCanvas
              :agents="agents"
              :candidates="agentCandidates"
              :providers="providers"
              :routes="routes"
              :selected-agent-id="selectedAgentId"
              @select-agent="selectedAgentId = $event"
              @configure-agent="openAgentConfig(agents.find(a => a.id === $event)!)"
            />
          </div>

          <template v-if="agentViewMode === 'list'">
            <section class="agent-column">
              <div class="model-section-header">
                <h3>{{ t('settings.planningLayer') }}</h3>
                <UiBadge variant="secondary">{{ planningAgents.length }}</UiBadge>
              </div>
              <article
                v-for="agent in planningAgents"
                :key="agent.id"
                class="agent-card"
                :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
              >
                <button class="agent-card-select" @click="selectedAgentId = agent.id">
                  <div class="agent-card-icon">
                    <Workflow :size="17" />
                  </div>
                  <div class="agent-card-main">
                    <strong>{{ agent.name }}</strong>
                    <span>{{ agentTypeLabel(agent.agent_type) }} · {{ agentModeLabel(agent.mode) }}</span>
                  </div>
                  <UiBadge :variant="agent.enabled ? 'default' : 'secondary'">
                    {{ agent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
                  </UiBadge>
                </button>
                <button class="agent-card-more" :title="t('settings.openAgentConfig')" @click.stop="openAgentConfig(agent)">
                  <MoreHorizontal :size="16" />
                </button>
              </article>
            </section>

            <section class="agent-column">
              <div class="model-section-header">
                <h3>{{ t('settings.executionLayer') }}</h3>
                <UiBadge variant="secondary">{{ executionAgents.length }}</UiBadge>
              </div>
              <article
                v-for="agent in executionAgents"
                :key="agent.id"
                class="agent-card"
                :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
              >
                <button class="agent-card-select" @click="selectedAgentId = agent.id">
                  <div class="agent-card-icon execution">
                    <Cpu :size="17" />
                  </div>
                  <div class="agent-card-main">
                    <strong>{{ agent.name }}</strong>
                    <span>{{ agentTypeLabel(agent.agent_type) }} · {{ agentModeLabel(agent.mode) }}</span>
                  </div>
                  <UiBadge :variant="agent.enabled ? 'default' : 'secondary'">
                    {{ agent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
                  </UiBadge>
                </button>
                <button class="agent-card-more" :title="t('settings.openAgentConfig')" @click.stop="openAgentConfig(agent)">
                  <MoreHorizontal :size="16" />
                </button>
              </article>
            </section>
          </template>

          <UiCard v-if="configuringAgent" class="agent-detail-panel">
            <template #content>
              <div class="agent-detail-head">
                <div class="agent-card-icon" :class="{ execution: configuringAgent.layer === 'execution' }">
                  <component :is="configuringAgent.layer === 'planning' ? Workflow : Cpu" :size="20" />
                </div>
                <div>
                  <h3>{{ t('settings.agentConfiguration') }} · {{ configuringAgent.name }}</h3>
                  <p>{{ agentTypeLabel(configuringAgent.agent_type) }} · {{ agentLayerLabel(configuringAgent.layer) }}</p>
                </div>
                <UiButton variant="ghost" size="icon" :title="t('settings.closeConfig')" @click="configuringAgentId = ''">
                  <X :size="16" />
                </UiButton>
              </div>

              <!-- 启用开关 -->
              <div class="agent-config-switch">
                <div>
                  <strong>{{ t('settings.agentEnabled') }}</strong>
                  <span>{{ configuringAgent.is_built_in ? t('settings.builtInAgent') : configuringAgent.id }}</span>
                </div>
                <UiSwitch
                  :model-value="configuringAgent.enabled"
                  :disabled="busy"
                  @update:model-value="setAgentEnabled(configuringAgent, $event)"
                />
              </div>

              <!-- 运行模式 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentModeTitle') }}</div>
                <div class="agent-mode-grid">
                  <button
                    v-for="mode in agentModes"
                    :key="mode.id"
                    class="agent-mode-card"
                    :class="{ active: configuringAgent.mode === mode.id }"
                    @click="updateAgentMode(configuringAgent, mode.id)"
                  >
                    <strong>{{ mode.display_name }}</strong>
                    <span>{{ mode.summary }}</span>
                    <small>
                      {{ t('settings.parallelExecutors') }} {{ mode.max_parallel_executors }}
                      · {{ mode.worktree_isolation ? t('settings.worktreeOn') : t('settings.worktreeOff') }}
                    </small>
                  </button>
                </div>
                <div v-if="configuredAgentMode" class="agent-policy-strip">
                  <ShieldCheck :size="16" />
                  <span>
                    {{ configuredAgentMode.approval_required ? t('settings.approvalGateOn') : t('settings.approvalGateOff') }}
                    · {{ configuredAgentMode.budget_policy }}
                  </span>
                </div>
              </div>

              <!-- 模型配置 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentModelConfig') }}</div>
                <div class="agent-detail-grid">
                  <div>
                    <span>{{ t('settings.routePurpose') }}</span>
                    <strong>{{ configuringAgent.model_route_purpose }}</strong>
                  </div>
                  <div>
                    <span>{{ t('settings.routeProvider') }}</span>
                    <strong>{{ agentRouteProvider(configuringAgent)?.display_name ?? t('settings.noProvider') }}</strong>
                  </div>
                </div>
                <div class="model-form-grid" style="margin-top:12px">
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.routeProvider') }}</UiLabel>
                    <select v-if="providers.length > 0" v-model="agentRouteProviderId" class="settings-select">
                      <option v-for="provider in providers" :key="provider.id" :value="provider.id">
                        {{ provider.display_name }} · {{ provider.model || provider.driver }}
                      </option>
                    </select>
                    <p v-else class="quiet">{{ t('settings.noProvidersHint') }}</p>
                  </div>
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.routeModel') }}</UiLabel>
                    <UiInput v-model="agentRouteModel" :placeholder="agentRouteProvider(configuringAgent)?.model ?? t('settings.noModel')" />
                  </div>
                </div>
                <div class="modal-actions compact">
                  <UiButton :disabled="busy || !agentRouteProviderId" size="sm" @click="saveAgentRoute(configuringAgent)">
                    <Save :size="14" />
                    <span>{{ t('settings.saveRoute') }}</span>
                  </UiButton>
                </div>
              </div>

              <!-- 描述 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentDescription') }}</div>
                <div class="settings-field">
                  <textarea
                    v-model="agentEditDescription"
                    class="settings-textarea"
                    rows="2"
                    :placeholder="t('settings.agentDescriptionPlaceholder')"
                  ></textarea>
                </div>
              </div>

              <!-- 工具绑定 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentTools') }}</div>
                <p class="agent-config-hint">{{ t('settings.agentToolsHint') }}</p>
                <div class="agent-tool-grid">
                  <button
                    v-for="tool in availableTools"
                    :key="tool.id"
                    class="agent-tool-chip"
                    :class="{
                      active: agentEditTools.includes(tool.id),
                      risky: tool.requires_approval
                    }"
                    @click="toggleAgentTool(tool.id)"
                  >
                    <span class="agent-tool-name">{{ tool.display_name }}</span>
                    <span class="agent-tool-risk">{{ tool.risk }}</span>
                  </button>
                </div>
              </div>

              <!-- 能力标签 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentCapabilities') }}</div>
                <p class="agent-config-hint">{{ t('settings.agentCapabilitiesHint') }}</p>
                <div class="agent-capability-list">
                  <span v-for="cap in agentEditCapabilities" :key="cap" class="agent-cap-tag">
                    {{ cap }}
                    <button class="agent-cap-remove" @click="removeAgentCapability(cap)">×</button>
                  </span>
                </div>
                <div class="agent-cap-add-row">
                  <UiInput v-model="agentNewCapability" :placeholder="t('settings.newCapabilityPlaceholder')" size="sm" @keydown.enter="addAgentCapability" />
                  <UiButton variant="outline" size="sm" :disabled="!agentNewCapability.trim()" @click="addAgentCapability">
                    <Plus :size="14" />
                    {{ t('settings.addCapability') }}
                  </UiButton>
                </div>
              </div>

              <!-- System Prompt -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentSystemPrompt') }}</div>
                <p class="agent-config-hint">{{ t('settings.agentSystemPromptHint') }}</p>
                <div class="settings-field">
                  <textarea
                    v-model="agentEditSystemPrompt"
                    class="settings-textarea prompt-editor"
                    rows="6"
                    :placeholder="t('settings.agentSystemPromptPlaceholder')"
                  ></textarea>
                </div>
              </div>

              <!-- 保存按钮 -->
              <div class="agent-save-bar">
                <UiButton :disabled="busy" @click="saveAgentProfile">
                  <Save :size="14" />
                  <span>{{ t('settings.saveAgent') }}</span>
                </UiButton>
              </div>
            </template>
          </UiCard>

          <div class="model-section-header">
            <h3>{{ t('settings.evolutionCandidates') }}</h3>
            <UiBadge variant="outline">{{ agentCandidates.length }}</UiBadge>
          </div>

          <div class="agent-candidate-grid">
            <UiCard v-for="candidate in agentCandidates" :key="candidate.id" class="agent-candidate-card">
              <template #content>
                <div class="agent-candidate-head">
                  <div>
                    <strong>{{ candidate.name }}</strong>
                    <span>{{ agentLayerLabel(candidate.layer) }} · {{ agentTypeLabel(candidate.agent_type) }} · {{ candidate.status }}</span>
                  </div>
                  <UiBadge variant="secondary">{{ t('settings.generatedByEvolution') }}</UiBadge>
                </div>
                <p>{{ candidate.description }}</p>
                <div class="model-capability-row">
                  <span v-for="tool in candidate.suggested_tools" :key="tool">{{ tool }}</span>
                </div>
                <ul class="agent-eval-list">
                  <li v-for="note in candidate.evaluation_notes" :key="note">{{ note }}</li>
                </ul>
              </template>
            </UiCard>
          </div>
        </template>

        <template v-if="activeSection === 'appearance'">
          <h2>{{ t('settings.appearance') }}</h2>

          <h3>{{ t('settings.theme') }}</h3>
          <div class="theme-options">
            <button
              :class="['theme-option', { active: theme === 'dark' }]"
              @click="setTheme('dark')"
            >
              <Moon :size="18" />
              {{ t('settings.dark') }}
            </button>
            <button
              :class="['theme-option', { active: theme === 'light' }]"
              @click="setTheme('light')"
            >
              <Sun :size="18" />
              {{ t('settings.light') }}
            </button>
            <button
              :class="['theme-option', { active: theme === 'system' }]"
              @click="setTheme('system')"
            >
              <Monitor :size="18" />
              {{ t('settings.system') }}
            </button>
          </div>

          <h3>{{ t('settings.accentColor') }}</h3>
          <p class="accent-color-hint">{{ t('settings.accentColorHint') }}</p>
          <div class="accent-color-grid">
            <button
              v-for="color in accentColors"
              :key="color.key"
              :class="['accent-color-swatch', { active: accentColor === color.key }]"
              :style="{ '--swatch-color': color.dark.accentPrimary }"
              :title="t(color.labelKey)"
              @click="setAccentColor(color.key)"
            >
              <span class="accent-color-dot"></span>
              <span class="accent-color-label">{{ t(color.labelKey) }}</span>
              <Check v-if="accentColor === color.key" :size="14" class="accent-color-check" />
            </button>
          </div>
        </template>

        <template v-if="activeSection === 'language'">
          <h2>{{ t('settings.language') }}</h2>
          <div class="lang-options">
            <UiButton
              variant="outline"
              :class="['lang-option', { active: locale === 'zh-CN' }]"
              @click="setLocale('zh-CN')"
            >
              中文
            </UiButton>
            <UiButton
              variant="outline"
              :class="['lang-option', { active: locale === 'en' }]"
              @click="setLocale('en')"
            >
              English
            </UiButton>
          </div>
        </template>

        <template v-if="activeSection === 'apiDocs'">
          <h2>{{ t('settings.apiDocs') }}</h2>
          <iframe class="api-docs-frame" :src="api.gatewayUrl + '/docs'" />
        </template>

        <template v-if="activeSection === 'about'">
          <h2>{{ t('settings.about') }}</h2>
          <UiCard class="about-section">
            <div class="about-row">
              <span>{{ t('settings.versionDesktop') }}</span>
              <span>0.1.0</span>
            </div>
            <div class="about-row">
              <span>{{ t('settings.versionCode') }}</span>
              <span>0.1.0</span>
            </div>
            <div class="about-row">
              <span>{{ t('settings.versionCore') }}</span>
              <span>0.1.0</span>
            </div>
          </UiCard>
          <p class="about-decouple-hint">{{ t('settings.decoupleHint') }}</p>
        </template>
      </div>
    </div>

    <Transition name="modal-fade">
    <div v-if="showModal" class="model-provider-modal" @click.self="closeModal">
      <UiCard class="model-provider-modal-content">
        <template #header>
          <div class="modal-header-row">
            <div class="modal-header-left">
              <span class="modal-provider-logo" :style="{ background: brandBg(providerForm.driver), borderColor: brandColor(providerForm.driver) + '40' }" v-html="currentTemplate?.icon ?? ''"></span>
              <div class="modal-header-info">
                <h3>{{ providerForm.id ? t('settings.editProviderTitle') : t('settings.newProvider') }}</h3>
                <span class="modal-header-sub">{{ currentTemplate ? t(currentTemplate.display_name_key) : providerForm.driver }}</span>
              </div>
            </div>
            <UiButton variant="ghost" size="icon" @click="closeModal">
              <X :size="16" />
            </UiButton>
          </div>
        </template>

        <template #content>
          <p v-if="currentTemplate" class="template-summary">{{ t(currentTemplate.summary_key) }}</p>

          <div class="modal-form-section">
            <div class="modal-form-section-title">{{ t('settings.basicInfo') }}</div>
            <div class="settings-field">
              <UiLabel>{{ t('settings.displayName') }}</UiLabel>
              <UiInput v-model="providerForm.display_name" />
            </div>
          </div>

          <div v-if="formFields.base_url || formFields.model" class="modal-form-section">
            <div class="modal-form-section-title">{{ t('settings.connectionParams') }}</div>
            <div class="model-form-grid">
              <div v-if="formFields.base_url" class="settings-field">
                <UiLabel>{{ t('settings.baseUrl') }}</UiLabel>
                <UiInput v-model="providerForm.base_url" :placeholder="formPlaceholders.base_url" />
              </div>
              <div v-if="formFields.model" class="settings-field">
                <UiLabel>{{ t('settings.modelLabel') }}</UiLabel>
                <UiInput v-model="providerForm.model" :placeholder="formPlaceholders.model" />
              </div>
            </div>
          </div>

          <div v-if="formFields.api_key" class="modal-form-section">
            <div class="modal-form-section-title">{{ t('settings.authentication') }}</div>
            <div class="settings-field">
              <UiLabel>{{ t('settings.apiKey') }}</UiLabel>
              <UiInput
                v-model="providerForm.api_key"
                type="password"
                :placeholder="selectedProvider?.has_api_key ? t('settings.apiKeyStored') : formPlaceholders.api_key ?? t('settings.apiKeyNotSet')"
              />
            </div>
          </div>

          <div v-if="formFields.binary_path || formFields.home_path" class="modal-form-section">
            <div class="modal-form-section-title">{{ t('settings.localPaths') }}</div>
            <div class="model-form-grid">
              <div v-if="formFields.binary_path" class="settings-field">
                <UiLabel>{{ t('settings.binaryPath') }}</UiLabel>
                <UiInput v-model="providerForm.binary_path" :placeholder="formPlaceholders.binary_path" />
              </div>
              <div v-if="formFields.home_path" class="settings-field">
                <UiLabel>{{ t('settings.homePath') }}</UiLabel>
                <UiInput v-model="providerForm.home_path" :placeholder="formPlaceholders.home_path" />
              </div>
            </div>
          </div>

          <div v-if="formFields.server_url || formFields.launch_args" class="modal-form-section">
            <div class="modal-form-section-title">{{ t('settings.serviceConfig') }}</div>
            <div class="model-form-grid">
              <div v-if="formFields.server_url" class="settings-field">
                <UiLabel>{{ t('settings.serverUrl') }}</UiLabel>
                <UiInput v-model="providerForm.server_url" :placeholder="formPlaceholders.server_url" />
              </div>
              <div v-if="formFields.launch_args" class="settings-field">
                <UiLabel>{{ t('settings.launchArgs') }}</UiLabel>
                <UiInput v-model="providerForm.launch_args" :placeholder="formPlaceholders.launch_args" />
              </div>
            </div>
          </div>

          <div class="modal-form-section">
            <div class="modal-form-section-title">{{ t('settings.status') }}</div>
            <div class="modal-enabled-row">
              <div>
                <strong>{{ t('settings.enabled') }}</strong>
                <span class="modal-enabled-hint">{{ providerForm.enabled ? t('settings.enabledHint') : t('settings.disabledHint') }}</span>
              </div>
              <UiSwitch v-model="providerForm.enabled" />
            </div>
          </div>

          <div v-if="currentTemplate" class="modal-capability-section">
            <div class="modal-form-section-title">{{ t('settings.supportedCapabilities') }}</div>
            <div class="model-capability-row">
              <span v-for="capability in currentTemplate.capabilities" :key="capability" class="provider-cap-tag">{{ capability }}</span>
            </div>
          </div>

          <div v-if="selectedProvider?.status_message" class="model-provider-note">
            <Terminal :size="14" />
            <span>{{ selectedProvider.status_message }}</span>
          </div>
        </template>

        <template #footer>
          <div class="modal-actions">
            <UiButton variant="outline" @click="closeModal">
              {{ t('settings.cancel') }}
            </UiButton>
            <UiButton :disabled="busy" @click="saveProvider()">
              <Save :size="14" />
              <span>{{ t('settings.save') }}</span>
            </UiButton>
          </div>
        </template>
      </UiCard>
    </div>
    </Transition>
  </div>
</template>
