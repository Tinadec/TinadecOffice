import { computed, nextTick, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { api, type ModelCatalogReadinessReceiptDto, type ModelCatalogTemplateReadinessDto, type ModelCenterAcpRuntimeDto, type ModelCenterOverviewDto, type ModelProviderInstanceDto, type ModelProviderReadinessDto, type ModelReadinessReceiptDto, type SaveModelProviderInstanceInput } from '@/api'
import { buildModelCenterRows, filterModelCenterRows, type ModelCenterFilter } from '@/modelCenterView'
import { findTemplate, PROVIDER_TEMPLATES, type ProviderTemplate } from '@/providerTemplates'
import { providerTemplateFromSupplier, providersFromOverview, type ModelCenterSection } from '@/runtimeCenterView'
import { loadAgentCenter } from './agentState'
import { useSettingsLabels } from './settingsLabels'

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

const modelCenterOverview = ref<ModelCenterOverviewDto | null>(null)
const providers = ref<ModelProviderInstanceDto[]>([])
const modelReadiness = ref<ModelReadinessReceiptDto | null>(null)
const modelCatalogReadiness = ref<ModelCatalogReadinessReceiptDto | null>(null)
const selectedProviderId = ref('')
const selectedProviderDetailId = ref('')
const modelCenterSection = ref<ModelCenterSection>('suppliers')
const modelProviderFilter = ref<ModelCenterFilter>('all')
const modelProviderQuery = ref('')
const modelProviderListRef = ref<HTMLElement | null>(null)
const modelDiagnosticsRef = ref<HTMLDetailsElement | null>(null)
const confirmDeleteId = ref('')
const modelCenterLoading = ref(false)
const modelCenterBusy = ref(false)
const modelCenterError = ref('')
const modelCenterNotice = ref('')
const showModal = ref(false)

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
  enabled: true,
})

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

export function useModelSettings() {
  const { t } = useI18n()
  const router = useRouter()
  const labels = useSettingsLabels()

  const modelCenterSections = computed(() => [
    { key: 'suppliers' as const, label: t('settings.centerSuppliers'), count: modelCenterOverview.value?.suppliers.length ?? 0 },
    { key: 'api' as const, label: t('settings.centerApiConnections'), count: modelCenterOverview.value?.api_connections.length ?? 0 },
    { key: 'models' as const, label: t('settings.centerModels'), count: modelCenterOverview.value?.models.length ?? 0 },
    { key: 'cli' as const, label: 'CLI', count: modelCenterOverview.value?.cli_runtimes.length ?? 0 },
    { key: 'acp' as const, label: 'ACP', count: modelCenterOverview.value?.acp_runtimes.length ?? 0 },
  ])
  const supplierTemplates = computed(() => new Map((modelCenterOverview.value?.suppliers ?? []).map((supplier) => [supplier.driver, providerTemplateFromSupplier(supplier)])))
  const currentTemplate = computed(() => supplierTemplates.value.get(providerForm.driver) ?? findTemplate(providerForm.driver))
  const formFields = computed(() => currentTemplate.value?.fields ?? { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false })
  const formPlaceholders = computed(() => currentTemplate.value?.placeholders ?? {})
  const selectedProvider = computed(() => providers.value.find((provider) => provider.id === selectedProviderId.value) ?? null)
  const selectedProviderDetail = computed(() => providers.value.find((provider) => provider.id === selectedProviderDetailId.value) ?? providers.value[0] ?? null)
  const providerReadinessById = computed(() => new Map<string, ModelProviderReadinessDto>((modelReadiness.value?.providers ?? []).map((provider) => [provider.provider_instance_id, provider])))
  const blockedModelRoutes = computed(() => (modelReadiness.value?.routes ?? []).filter((route) => route.status === 'blocked'))
  const warningCatalogTemplates = computed(() => (modelCatalogReadiness.value?.templates ?? []).filter((template) => template.status !== 'ready'))
  const catalogReadinessByDriver = computed(() => new Map<string, ModelCatalogTemplateReadinessDto>((modelCatalogReadiness.value?.templates ?? []).map((template) => [template.driver, template])))
  const modelCenterRows = computed(() => buildModelCenterRows(providersFromOverview(modelCenterOverview.value).filter((provider) => provider.connection_kind !== 'cli'), [...supplierTemplates.value.values()], modelReadiness.value, (key) => t(key)).filter((row) => row.kind === 'instance'))
  const filteredModelCenterRows = computed(() => filterModelCenterRows(modelCenterRows.value, modelProviderFilter.value, modelProviderQuery.value))
  const modelCenterIssueCount = computed(() => filterModelCenterRows(modelCenterRows.value, 'issues', '').length)
  const firstNeedsKeyProvider = computed(() => providers.value.find((provider) => provider.status === 'needs_key') ?? null)
  const modelCenterDiagnostics = computed(() => modelCenterOverview.value?.diagnostics ?? [])

  function applyTemplateDefaults(template: ProviderTemplate) {
    providerForm.driver = template.driver
    providerForm.display_name = t(template.display_name_key)
    providerForm.connection_kind = template.connection_kind
    providerForm.base_url = template.default_base_url ?? ''
    providerForm.model = template.default_model ?? ''
    providerForm.binary_path = ''
    providerForm.home_path = ''
    providerForm.server_url = template.fields.server_url ? template.default_base_url ?? '' : ''
    providerForm.launch_args = ''
  }

  function openAddModal(template?: ProviderTemplate) {
    selectedProviderId.value = ''
    providerForm.id = ''
    applyTemplateDefaults(template ?? (modelCenterOverview.value?.suppliers[0] ? providerTemplateFromSupplier(modelCenterOverview.value.suppliers[0]) : PROVIDER_TEMPLATES[0]))
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

  function providerById(providerId: string) {
    return providers.value.find((provider) => provider.id === providerId) ?? null
  }

  function openProviderById(providerId: string) {
    const provider = providerById(providerId)
    if (provider) openEditModal(provider)
  }

  function toggleProviderDetail(providerId: string) {
    selectedProviderDetailId.value = selectedProviderDetailId.value === providerId ? '' : providerId
    if (selectedProviderDetailId.value) selectedProviderId.value = providerId
  }

  function focusModelProviderList(filter: ModelCenterFilter) {
    modelCenterSection.value = filter === 'available' ? 'suppliers' : 'api'
    modelProviderFilter.value = filter
    nextTick(() => {
      const behavior = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
      modelProviderListRef.value?.scrollIntoView({ behavior, block: 'start' })
      modelProviderListRef.value?.querySelector<HTMLInputElement>('input')?.focus()
    })
  }

  function openModelDiagnostics() {
    if (!modelDiagnosticsRef.value) return
    modelDiagnosticsRef.value.open = true
    const behavior = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
    modelDiagnosticsRef.value.scrollIntoView({ behavior, block: 'start' })
  }

  async function loadModelCenter() {
    modelCenterLoading.value = true
    modelCenterError.value = ''
    try {
      const overview = await api.getModelCenterOverview()
      modelCenterOverview.value = overview
      modelCenterNotice.value = ''
      providers.value = providersFromOverview(overview)
      modelReadiness.value = overview.readiness.model ?? null
      modelCatalogReadiness.value = overview.readiness.catalog ?? null
      selectedProviderId.value = providers.value.find((provider) => provider.id === selectedProviderId.value)?.id ?? providers.value[0]?.id ?? ''
    } catch (error) {
      modelCenterError.value = error instanceof Error ? error.message : t('settings.centerLoadFailed')
    } finally {
      modelCenterLoading.value = false
    }
  }

  async function refreshProviderModels(providerInstanceId: string) {
    modelCenterBusy.value = true
    modelCenterNotice.value = ''
    try {
      await api.refreshProviderModels(providerInstanceId)
      await loadModelCenter()
    } catch (error) {
      modelCenterNotice.value = error instanceof Error ? error.message : t('settings.modelDiscoveryUnsupported')
    } finally {
      modelCenterBusy.value = false
    }
  }

  async function probeAcpRuntime(runtime: ModelCenterAcpRuntimeDto) {
    if (!runtime.adapter_id) return
    modelCenterBusy.value = true
    modelCenterNotice.value = ''
    try {
      await api.probeAcpAdapter(runtime.adapter_id)
      await Promise.all([loadModelCenter(), loadAgentCenter(t('settings.centerLoadFailed'))])
    } catch (error) {
      modelCenterNotice.value = error instanceof Error ? error.message : t('settings.acpProbeFailed')
    } finally {
      modelCenterBusy.value = false
    }
  }

  async function toggleProviderEnabled(provider: ModelProviderInstanceDto) {
    await saveProviderPayload({ ...provider, enabled: !provider.enabled })
  }

  async function deleteProvider(providerId: string) {
    modelCenterBusy.value = true
    try {
      await api.deleteModelProvider(providerId)
      if (selectedProviderDetailId.value === providerId) selectedProviderDetailId.value = ''
      await Promise.all([loadModelCenter(), loadAgentCenter(t('settings.centerLoadFailed'))])
    } finally {
      modelCenterBusy.value = false
      confirmDeleteId.value = ''
    }
  }

  async function saveProviderPayload(provider: ModelProviderInstanceDto) {
    modelCenterBusy.value = true
    try {
      await api.saveModelProvider(provider.id, { ...provider, clear_api_key: false })
      await Promise.all([loadModelCenter(), loadAgentCenter(t('settings.centerLoadFailed'))])
    } finally {
      modelCenterBusy.value = false
    }
  }

  async function saveProvider() {
    modelCenterBusy.value = true
    try {
      const isNewProvider = !providerForm.id
      const payload: SaveModelProviderInstanceInput = { id: providerForm.id || undefined, driver: providerForm.driver, display_name: providerForm.display_name, connection_kind: providerForm.connection_kind, base_url: formFields.value.base_url ? (providerForm.base_url || null) : null, model: formFields.value.model ? (providerForm.model || null) : null, api_key: formFields.value.api_key ? (providerForm.api_key || null) : null, clear_api_key: providerForm.clear_api_key, binary_path: formFields.value.binary_path ? (providerForm.binary_path || null) : null, home_path: formFields.value.home_path ? (providerForm.home_path || null) : null, server_url: formFields.value.server_url ? (providerForm.server_url || null) : null, launch_args: formFields.value.launch_args ? (providerForm.launch_args || null) : null, capabilities: providerForm.id ? selectedProvider.value?.capabilities ?? currentTemplate.value?.capabilities ?? [] : currentTemplate.value?.capabilities ?? [], enabled: providerForm.enabled }
      const saved = providerForm.id ? await api.saveModelProvider(providerForm.id, payload) : await api.createModelProvider(payload)
      selectedProviderId.value = saved.id
      showModal.value = false
      await Promise.all([loadModelCenter(), loadAgentCenter(t('settings.centerLoadFailed'))])
      if (isNewProvider) modelProviderFilter.value = 'configured'
    } finally {
      modelCenterBusy.value = false
    }
  }

  if (!modelCenterOverview.value && !modelCenterLoading.value) void loadModelCenter()

  return { ...labels, api, blockedModelRoutes, catalogReadinessByDriver, confirmDeleteId, currentTemplate, deleteProvider, filteredModelCenterRows, firstNeedsKeyProvider, focusModelProviderList, formFields, formPlaceholders, loadModelCenter, modelCatalogReadiness, modelCenterBusy, modelCenterDiagnostics, modelCenterError, modelCenterIssueCount, modelCenterLoading, modelCenterNotice, modelCenterOverview, modelCenterRows, modelCenterSection, modelCenterSections, modelDiagnosticsRef, modelProviderFilter, modelProviderListRef, modelProviderQuery, modelReadiness, openAddModal, openEditModal, openModelDiagnostics, openProviderById, probeAcpRuntime, providerById, providerForm, providerPresentation: (driver: string) => supplierTemplates.value.get(driver) ?? findTemplate(driver), providerReadinessById, providers, providerTemplateFromSupplier, refreshProviderModels, router, selectedProvider, selectedProviderDetail, selectedProviderDetailId, selectedProviderId, showModal, toggleProviderDetail, toggleProviderEnabled, warningCatalogTemplates, closeModal: () => { showModal.value = false }, saveProvider }
}
