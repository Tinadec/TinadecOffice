<script setup lang="ts">
import {
  Activity,
  Check,
  ChevronLeft,
  ChevronRight,
  Circle,
  Cpu,
  Database,
  Edit3,
  Eye,
  EyeOff,
  Info,
  PanelRight,
  Plus,
  RefreshCw,
  Route,
  Save,
  Search,
  Server,
  Settings2,
  Terminal,
  Trash2,
  Workflow,
  X,
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { DialogRoot, DialogPortal, DialogOverlay, DialogContent, DialogTitle, DialogDescription, DialogClose } from 'reka-ui'
import { UiBadge, UiButton, UiCard, UiCheckbox, UiInput, UiLabel, UiSelectField, UiSkeleton, UiSwitch } from '@/components/ui'
import ModelParametersEditor from '@/settings/sections/ModelParametersEditor.vue'
import ModelScopeNotice from '@/settings/sections/ModelScopeNotice.vue'
import { centerDiagnosticLabel as centerDiagnosticText, providerStatusLabel } from '@/settings/modelCenterLabels'
import { useNotifications } from '@/composables/useNotifications'
import type { ModelCenterContext } from '@/composables/useModelCenter'
import type {
  CliDiscoveryCandidateDto,
  CenterDiagnosticDto,
  HarnessChannelDto,
  ModelCenterOverviewDto,
  ModelProviderInstanceDto,
  ModelReferenceDto,
  SaveModelProviderInstanceInput,
} from '@/api'
import {
  PROVIDER_CATEGORIES,
  PROVIDER_TEMPLATES,
  findTemplate,
  templateProtocol,
  templateProtocols,
  type ChatProtocol,
  type ProviderCategory,
  type ProviderTemplate,
} from '@/providerTemplates'
import { buildModelCenterRows, filterModelCenterRows, type ModelCenterFilter } from '@/modelCenterView'
import {
  discoveryAffordanceLabelKey,
  protocolLabelKey,
  providersFromOverview,
  type ModelCenterSection as ModelCenterTab,
} from '@/runtimeCenterView'
import { readinessReason } from '@/lib/readinessText'

interface ProviderForm {
  id: string
  driver: string
  display_name: string
  connection_kind: string
  protocol: string
  base_url: string
  model: string
  models: string[]
  api_key: string
  clear_api_key: boolean
  binary_path: string
  home_path: string
  server_url: string
  launch_args: string
  enabled: boolean
}

const props = defineProps<{
  center: ModelCenterContext
  runtimeModels: ModelCenterOverviewDto['models']
  runtimeCliOptions: ModelCenterOverviewDto['cli_runtimes']
  runtimeAcpOptions: ModelCenterOverviewDto['acp_runtimes']
  canAccessBackend: boolean
}>()

const {
  modelCenterOverview,
  providers,
  modelReadiness,
  modelCatalogReadiness,
  readinessReceipt,
  routes,
  selectedProviderId,
  selectedProvider,
  modelCenterBusy,
  modelCenterLoading,
  modelCenterFailure,
  modelCenterPartialFailureSources,
  loadModelCenter,
  modelApiProvider,
  modelsForProvider,
  setDefaultChatModel,
  toggleProviderEnabled,
  removeModel,
  saveProviderModels,
  fetchModelReferences,
  cliDiscoveryCandidates,
  cliDiscoveryLoading,
  cliDiscoveryLoaded,
  discoverHarnesses,
  connectDiscoveredCli,
  discoverProviderModels,
  saveProviderInstance,
} = props.center

const { t, te } = useI18n()
const router = useRouter()
const { notify, confirm } = useNotifications()

const statusLabel = (status: string) => providerStatusLabel(t, status)
const centerDiagnosticLabel = (diagnostic: CenterDiagnosticDto) => centerDiagnosticText(t, diagnostic)

const modelCenterSection = ref<ModelCenterTab>('api')
const selectedProviderDetailId = ref('')
// Narrow layouts show one pane at a time; the rail keeps its search and scroll while hidden.
const providerDetailOpen = ref(false)
const providerWorkspaceEl = ref<HTMLElement | null>(null)
const providerRailEl = ref<HTMLElement | null>(null)
const providerRailScroll = ref(0)

function providerWorkspaceNarrow(): boolean {
  const width = providerWorkspaceEl.value?.clientWidth
  return width !== undefined && width > 0 && width <= 700
}
const modelProviderFilter = ref<ModelCenterFilter>('all')
const modelProviderQuery = ref('')
const modelProviderListRef = ref<HTMLElement | null>(null)
const catalogQuery = ref('')
const showDiagnostics = ref(false)

// One section per channel Core can drive a harness over, plus the HTTP serve shape which lives in
// the CLI section because that is how it is reached. Labels are locale keys: 'CLI'/'ACP' as literals
// shipped untranslated, and a channel name is exactly the thing a reader must understand.
const modelCenterSections = computed(() => [
  { key: 'api' as const, label: t('settings.centerSuppliers'), count: modelCenterOverview.value?.api_connections.length ?? 0 },
  { key: 'models' as const, label: t('settings.centerModels'), count: modelCenterOverview.value?.models.length ?? 0 },
  { key: 'routes' as const, label: t('settings.centerRoutes'), count: routes.value.length },
  { key: 'cli' as const, label: t('settings.centerCli'), count: modelCenterOverview.value?.cli_runtimes.length ?? 0 },
  { key: 'tui' as const, label: t('settings.centerTui'), count: modelCenterOverview.value?.tui_runtimes.length ?? 0 },
  { key: 'acp' as const, label: t('settings.centerAcp'), count: modelCenterOverview.value?.acp_runtimes.length ?? 0 }
])

type CenterTabKey = 'suppliers' | 'models' | 'routes' | 'runtime'
const runtimeSectionKeys = ['cli', 'tui', 'acp'] as const

/** The four task pages. Runtime is one page holding the CLI / TUI / ACP inventory sub-tabs. */
const modelCenterTabs = computed(() => [
  { key: 'suppliers' as const, label: t('settings.centerSuppliers'), count: modelCenterOverview.value?.api_connections.length ?? 0 },
  { key: 'models' as const, label: t('settings.centerModels'), count: modelCenterOverview.value?.models.length ?? 0 },
  { key: 'routes' as const, label: t('settings.centerRoutes'), count: routes.value.length },
  {
    key: 'runtime' as const,
    label: t('settings.centerRuntime'),
    count: (modelCenterOverview.value?.cli_runtimes.length ?? 0)
      + (modelCenterOverview.value?.tui_runtimes.length ?? 0)
      + (modelCenterOverview.value?.acp_runtimes.length ?? 0)
  }
])

const centerTab = computed<CenterTabKey>(() => {
  if (modelCenterSection.value === 'models') return 'models'
  if (modelCenterSection.value === 'routes') return 'routes'
  if (modelCenterSection.value === 'api') return 'suppliers'
  return 'runtime'
})

const runtimeSections = computed(() =>
  modelCenterSections.value.filter((section) => isRuntimeSection(section.key))
)

function applyCenterTab(key: CenterTabKey) {
  if (key === 'suppliers') modelCenterSection.value = 'api'
  else if (key === 'models') modelCenterSection.value = 'models'
  else if (key === 'routes') modelCenterSection.value = 'routes'
  else if (!runtimeSectionKeys.includes(modelCenterSection.value as typeof runtimeSectionKeys[number])) modelCenterSection.value = 'cli'
}

function isRuntimeSection(section: ModelCenterTab): section is typeof runtimeSectionKeys[number] {
  return runtimeSectionKeys.includes(section as typeof runtimeSectionKeys[number])
}

const centerTabKeys = computed(() => modelCenterTabs.value.map((tab) => tab.key))

function selectRuntimeTab(key: string) {
  if (isRuntimeSection(key as ModelCenterTab)) modelCenterSection.value = key as typeof runtimeSectionKeys[number]
}

function onCenterTabKeydown(event: KeyboardEvent) {
  void onTablistKeydown(event, centerTabKeys.value, centerTab.value, (key) => selectCenterTab(key as CenterTabKey))
}

function onRuntimeTabKeydown(event: KeyboardEvent) {
  void onTablistKeydown(event, runtimeSectionKeys, modelCenterSection.value, selectRuntimeTab)
}

const sortedRoutes = computed(() => [
  ...routes.value.filter((route) => route.purpose === 'chat'),
  ...routes.value.filter((route) => route.purpose !== 'chat'),
])

const showModal = ref(false)
const showTemplatePicker = ref(false)
const templatePickerQuery = ref('')
// Set after a confirmed provider_in_use guard so the retry send bypasses the guard once.
const forceProviderWrite = ref(false)

const providerForm = reactive<ProviderForm>({
  id: '',
  driver: 'openai-compatible',
  display_name: t('settings.providerDefaultName'),
  connection_kind: 'api-key',
  protocol: 'openai-chat',
  base_url: 'https://api.openai.com/v1',
  model: 'gpt-5.4-mini',
  models: [],
  api_key: '',
  clear_api_key: false,
  binary_path: '',
  home_path: '',
  server_url: '',
  launch_args: '',
  enabled: true
})

const currentTemplate = computed(() => findTemplate(providerForm.driver))

const pickerTemplates = computed(() => PROVIDER_TEMPLATES)
const filteredPickerTemplates = computed(() => {
  const query = templatePickerQuery.value.trim().toLocaleLowerCase()
  return pickerTemplates.value.filter((template) => !query || [
    t(template.display_name_key),
    template.driver,
    template.connection_kind,
    template.default_model ?? ''
  ].some((value) => value.toLocaleLowerCase().includes(query)))
})
const pickerTemplateGroups = computed(() => {
  const groups: { category: ProviderCategory; labelKey: string; templates: ProviderTemplate[] }[] = []
  for (const category of PROVIDER_CATEGORIES) {
    const templates = filteredPickerTemplates.value.filter((template) => template.category === category.key)
    if (templates.length > 0) groups.push({ category: category.key, labelKey: category.labelKey, templates })
  }
  return groups
})

const blockedModelRoutes = computed(() =>
  (modelReadiness.value?.routes ?? []).filter((route) => route.status === 'blocked')
)
const warningCatalogTemplates = computed(() =>
  (modelCatalogReadiness.value?.templates ?? []).filter((template) => template.status !== 'ready')
)
/** Why the model layer is not ready, worded from the unified receipt's reason codes. The deprecated
 *  receipt's notes are Core's English diagnostics, shown only when the unified receipt could not be read. */
const modelReadinessNotes = computed(() => readinessReceipt.value
  ? [...new Set((readinessReceipt.value.items ?? [])
      .filter((item) => item.id.startsWith('model_') && item.status !== 'ready')
      .flatMap((item) => readinessReason(item, { t, te }) ?? []))]
  : modelReadiness.value?.design_notes ?? [])

const formFields = computed(() => {
  const fields = currentTemplate.value?.fields ?? {
    base_url: true, model: true, api_key: true,
    binary_path: false, home_path: false, server_url: false, launch_args: false
  }
  return { ...fields, model: false }
})
const formPlaceholders = computed(() => currentTemplate.value?.placeholders ?? {})
const formProtocolOptions = computed<ChatProtocol[]>(() =>
  currentTemplate.value ? templateProtocols(currentTemplate.value) : []
)
const protocolOptionLabel = (protocol: ChatProtocol) => t(protocolLabelKey(protocol))

const showProviderKey = ref(false)
const providerFormSubmitted = ref(false)
/**
 * Client-side checks the backend would otherwise answer with an opaque rejection. Only fields the
 * chosen template actually shows are checked; the URL format is the one rule this form can state
 * without guessing at the provider.
 */
const providerFormErrors = computed<Record<string, string>>(() => {
  const errors: Record<string, string> = {}
  if (!providerForm.display_name.trim()) errors.display_name = t('settings.fieldRequired')
  for (const key of ['base_url', 'server_url'] as const) {
    if (!formFields.value[key]) continue
    const value = providerForm[key].trim()
    if (value && !/^https?:\/\/\S+$/i.test(value)) errors[key] = t('settings.fieldUrlInvalid')
  }
  if (formFields.value.binary_path && providerForm.connection_kind === 'cli' && !providerForm.binary_path.trim()) {
    errors.binary_path = t('settings.fieldRequired')
  }
  return errors
})

const modelCenterRows = computed(() => buildModelCenterRows(
  providersFromOverview(modelCenterOverview.value).filter((provider) => provider.connection_kind !== 'cli'),
  PROVIDER_TEMPLATES,
  modelReadiness.value,
  (key) => t(key)
).filter((row) => row.kind === 'instance'))
const filteredModelCenterRows = computed(() => filterModelCenterRows(
  modelCenterRows.value,
  modelProviderFilter.value,
  modelProviderQuery.value
))
const modelCenterIssueCount = computed(() => filterModelCenterRows(
  modelCenterRows.value,
  'issues',
  ''
).length)
const firstNeedsKeyProvider = computed(() =>
  providers.value.find((provider) => provider.status === 'needs_key') ?? null
)
const modelCenterDiagnostics = computed(() => modelCenterOverview.value?.diagnostics ?? [])

const selectedProviderDetail = computed(() =>
  modelApiProvider(selectedProviderDetailId.value)
  ?? modelApiProvider(modelCenterOverview.value?.api_connections[0]?.id ?? '')
  ?? null
)
/** The writable instance behind the selected overview row (the projection's revision may be null). */
const selectedProviderInstance = computed(() =>
  providers.value.find((provider) => provider.id === selectedProviderDetail.value?.id) ?? null
)

function openEditProviderById(providerId: string) {
  const provider = providers.value.find((item) => item.id === providerId)
  if (provider) void openEditModal(provider)
}

function disableProviderById(providerId: string) {
  const provider = providers.value.find((item) => item.id === providerId)
  if (provider) void disableProviderWithReferences(provider)
}

const defaultChatRoute = computed(() => routes.value.find((route) => route.purpose === 'chat') ?? null)
const defaultChatCandidate = computed(() => defaultChatRoute.value?.candidates?.[0] ?? null)
const defaultChatValue = computed(() => defaultChatCandidate.value
  ? `${defaultChatCandidate.value.provider_instance_id}\u0000${defaultChatCandidate.value.model ?? ''}`
  : '')
const defaultChatLabel = computed(() => { const candidate = defaultChatCandidate.value
  return candidate ? [routeCandidateProviderLabel(candidate.provider_instance_id), candidate.model].filter(Boolean).join(' · ') : '' })
const defaultChatOptions = computed(() => {
  const options = (modelCenterOverview.value?.api_connections ?? []).flatMap((provider) =>
    modelsForProvider(provider.id).map((model) => ({
      value: `${provider.id}\u0000${model.model_id}`,
      label: `${provider.display_name} · ${model.model_id}`,
    })))
  if (defaultChatValue.value && !options.some((option) => option.value === defaultChatValue.value)) {
    options.unshift({ value: defaultChatValue.value, label: defaultChatLabel.value })
  }
  return options
})

async function changeDefaultChat(value: string) {
  if (!value || value === defaultChatValue.value) return
  const [providerId, model] = value.split('\u0000')
  if (!providerId) return
  await setDefaultChatModel(providerId, model ?? '')
}

/** The catalog across every supplier; a query matches a supplier name or a model identity. */
const modelCatalogGroups = computed(() => {
  const query = catalogQuery.value.trim().toLocaleLowerCase()
  return (modelCenterOverview.value?.api_connections ?? []).flatMap((provider) => {
    const providerHit = !query || [provider.display_name, provider.driver]
      .some((value) => value.toLocaleLowerCase().includes(query))
    const models = modelsForProvider(provider.id).filter((model) => providerHit
      || [model.model_id, ...(model.configuration_sources ?? []), ...(model.route_purposes ?? [])]
        .some((value) => value.toLocaleLowerCase().includes(query)))
    return models.length > 0 || (!query && providerHit) ? [{ provider, models }] : []
  })
})
const modelCatalogCount = computed(() => modelCatalogGroups.value.reduce((total, group) => total + group.models.length, 0))

async function locateProvider(providerId: string) {
  if (!await confirmDiscardProviderDraft()) return
  modelCenterSection.value = 'api'
  modelProviderFilter.value = 'all'
  modelProviderQuery.value = ''
  providerDetailOpen.value = true
  selectedProviderDetailId.value = providerId
  await nextTick()
  if (providerWorkspaceNarrow()) {
    providerWorkspaceEl.value?.querySelector<HTMLElement>('.model-center-provider-back')?.focus()
    return
  }
  Array.from(modelProviderListRef.value?.querySelectorAll<HTMLButtonElement>('.model-provider-identity') ?? [])
    .find((button) => button.dataset.providerId === providerId)?.focus()
}

function modelCatalogModeLabel(mode?: string) {
  return mode === 'configured_only' ? t('settings.configuredOnly') : mode ?? t('settings.configuredOnly')
}

function configuredModelSourceLabel(source: string) {
  if (source === 'provider_default') return t('settings.modelSourceProviderDefault')
  if (source === 'provider_models') return t('settings.modelSourceProviderModels')
  if (source === 'route_override') return t('settings.modelSourceRouteOverride')
  return source
}

function acpRuntimeSourceLabel(source: string) {
  if (source === 'provider_instance') return t('settings.harnessProviderInstance')
  if (source === 'legacy_provider') return t('settings.legacyProvider')
  return t('settings.acpAdapter')
}

function fillForm(provider: ModelProviderInstanceDto) {
  providerForm.id = provider.id
  providerForm.driver = provider.driver
  providerForm.display_name = provider.display_name
  providerForm.connection_kind = provider.connection_kind
  providerForm.protocol = provider.protocol ?? templateProtocol(findTemplate(provider.driver) ?? PROVIDER_TEMPLATES[0]) ?? 'openai-chat'
  providerForm.base_url = provider.base_url ?? ''
  providerForm.model = provider.model ?? ''
  providerForm.models = provider.models ?? []
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
  providerForm.protocol = templateProtocol(template) ?? ''
  providerForm.base_url = template.default_base_url ?? ''
  providerForm.model = ''
  providerForm.models = []
  providerForm.binary_path = ''
  providerForm.home_path = ''
  providerForm.server_url = template.fields.server_url ? template.default_base_url ?? '' : ''
  providerForm.launch_args = ''
}

/**
 * Switching away from a dirty provider form asks first — the same snapshot comparison the
 * backdrop and Escape use, applied to tab switches and provider selection.
 */
async function confirmDiscardProviderDraft(): Promise<boolean> {
  if (!showModal.value) return true
  if (!providerFormDirty()) {
    showModal.value = false
    return true
  }
  await requestCloseProviderModal()
  return !showModal.value
}

async function selectCenterTab(key: CenterTabKey): Promise<void> {
  if (!await confirmDiscardProviderDraft()) return
  applyCenterTab(key)
}

async function selectProviderDetail(providerId: string): Promise<void> {
  if (showModal.value && providerForm.id === providerId) return
  if (!await confirmDiscardProviderDraft()) return
  providerRailScroll.value = providerRailEl.value?.scrollTop ?? providerRailScroll.value
  providerDetailOpen.value = true
  selectedProviderDetailId.value = providerId
  selectedProviderId.value = providerId
  await nextTick()
  if (providerWorkspaceNarrow()) {
    providerWorkspaceEl.value?.querySelector<HTMLElement>('.model-center-provider-back')?.focus()
  }
}

/** Narrow layouts replace the rail with the detail; going back restores its search, scroll and focus. */
async function closeProviderDetail(): Promise<void> {
  if (showModal.value) {
    await requestCloseProviderModal()
    if (showModal.value) return
  }
  providerDetailOpen.value = false
  await nextTick()
  if (providerRailEl.value) providerRailEl.value.scrollTop = providerRailScroll.value
  const providerId = selectedProviderDetailId.value
  Array.from(providerRailEl.value?.querySelectorAll<HTMLElement>('[data-provider-id]') ?? [])
    .find((element) => element.dataset.providerId === providerId)
    ?.focus()
}

async function openAddModal(template?: ProviderTemplate) {
  if (!await confirmDiscardProviderDraft()) return
  modelCenterSection.value = 'api'
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
  forceProviderWrite.value = false
  showProviderKey.value = false
  providerFormSubmitted.value = false
  showModal.value = true
  captureProviderFormBaseline()
}

async function openEditModal(provider: ModelProviderInstanceDto) {
  if (showModal.value && providerForm.id === provider.id) return
  if (!await confirmDiscardProviderDraft()) return
  modelCenterSection.value = 'api'
  selectedProviderId.value = provider.id
  selectedProviderDetailId.value = provider.id
  providerDetailOpen.value = true
  fillForm(provider)
  forceProviderWrite.value = false
  showProviderKey.value = false
  providerFormSubmitted.value = false
  showModal.value = true
  captureProviderFormBaseline()
}

function focusModelProviderList(filter: ModelCenterFilter) {
  modelCenterSection.value = 'api'
  modelProviderFilter.value = filter
  nextTick(() => {
    modelProviderListRef.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
    modelProviderListRef.value?.querySelector<HTMLInputElement>('input')?.focus()
  })
}

function handleAddProviderClick() {
  if (showModal.value) {
    void confirmDiscardProviderDraft().then((proceed) => { if (proceed) showTemplatePicker.value = true })
    return
  }
  showTemplatePicker.value = true
}

async function pickTemplate(template: ProviderTemplate) {
  showTemplatePicker.value = false
  await openAddModal(template)
}

function openModelDiagnostics() {
  showDiagnostics.value = true
}

/** Arrow/Home/End across a tablist; selection follows focus, as the tab pattern expects. */
async function onTablistKeydown(
  event: KeyboardEvent,
  keys: readonly string[],
  current: string,
  select: (key: string) => Promise<void> | void,
): Promise<void> {
  const index = keys.indexOf(current)
  if (index < 0) return
  let target = -1
  if (event.key === 'ArrowRight') target = (index + 1) % keys.length
  else if (event.key === 'ArrowLeft') target = (index - 1 + keys.length) % keys.length
  else if (event.key === 'Home') target = 0
  else if (event.key === 'End') target = keys.length - 1
  if (target < 0) return
  event.preventDefault()
  const list = (event.currentTarget as HTMLElement | null)?.closest<HTMLElement>('[role="tablist"]')
  await select(keys[target])
  await nextTick()
  list?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]')?.focus()
}

/** Up/Down/Home/End move through the supplier rows and select as they land, like a listbox. */
async function onProviderRailKeydown(event: KeyboardEvent): Promise<void> {
  if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return
  const rail = event.currentTarget as HTMLElement | null
  const buttons = Array.from(rail?.querySelectorAll<HTMLButtonElement>('.model-provider-identity') ?? [])
  const index = buttons.indexOf(document.activeElement as HTMLButtonElement)
  if (index < 0) return
  event.preventDefault()
  const target = event.key === 'Home' ? 0
    : event.key === 'End' ? buttons.length - 1
      : event.key === 'ArrowDown' ? (index + 1) % buttons.length
        : (index - 1 + buttons.length) % buttons.length
  buttons[target]?.focus()
  const row = filteredModelCenterRows.value[target]
  if (row?.kind === 'instance') await selectProviderDetail(row.provider.id)
}

/**
 * What this provider's models are wired into, read before a destructive action. A failed read is
 * reported as failed, never as "nothing references it".
 */
async function providerReferenceNote(providerId: string): Promise<{ count: number; text: string; unreadable: boolean }> {
  const models = modelsForProvider(providerId)
  if (models.length === 0) return { count: 0, text: t('settings.providerNoReferences'), unreadable: false }
  try {
    const rows = (await Promise.all(models.map((model) => fetchModelReferences(providerId, model.model_id)))).flat()
    if (rows.length === 0) return { count: 0, text: t('settings.providerNoReferences'), unreadable: false }
    const counts = new Map<string, number>()
    for (const row of rows) counts.set(row.reference_kind, (counts.get(row.reference_kind) ?? 0) + 1)
    const summary = [...counts].map(([kind, count]) => `${referenceKindLabel(kind)} ×${count}`).join(' · ')
    return { count: rows.length, text: t('settings.providerReferencesAffected', { count: rows.length, summary }), unreadable: false }
  } catch {
    return { count: 0, text: t('settings.providerReferencesUnreadable'), unreadable: true }
  }
}

async function disableProviderWithReferences(provider: ModelProviderInstanceDto) {
  if (!provider.enabled) {
    await toggleProviderEnabled(provider)
    return
  }
  const note = await providerReferenceNote(provider.id)
  if (note.count > 0 || note.unreadable) {
    if (!await confirm({
      title: `${t('settings.disable')} · ${provider.display_name}`,
      message: note.text,
      confirmLabel: t('settings.disable'),
      cancelLabel: t('settings.cancel'),
      destructive: note.count > 0,
    })) return
  }
  await toggleProviderEnabled(provider, note.count > 0 ? { referencesNote: note.text } : undefined)
}

async function deleteProvider(providerId: string) {
  const note = await providerReferenceNote(providerId)
  if (!await props.center.deleteProvider(providerId, { referencesNote: note.text })) return
  if (selectedProviderDetailId.value === providerId) {
    selectedProviderDetailId.value = ''
    providerDetailOpen.value = false
  }
}

/**
 * The form as it was when the modal opened.
 *
 * Comparing against a snapshot is the only reliable way to tell whether a backdrop click is about
 * to throw away real work: the user pastes an API key once, and a stray click outside the card must
 * not be the end of it. `clear_api_key` is in the snapshot too, because unchecking it is an edit.
 */
const providerFormBaseline = ref('')

function providerFormSnapshot(): string {
  return JSON.stringify(providerForm)
}

function captureProviderFormBaseline(): void {
  providerFormBaseline.value = providerFormSnapshot()
}

function providerFormDirty(): boolean {
  return showModal.value && providerFormSnapshot() !== providerFormBaseline.value
}

/** Backdrop, the close button and Cancel all come here: a dirty form asks before it disappears. */
async function requestCloseProviderModal(): Promise<void> {
  if (!providerFormDirty()) {
    showModal.value = false
    return
  }
  const discard = await confirm({
    title: t('settings.discardProviderChanges'),
    message: t('settings.discardProviderChangesMessage'),
    confirmLabel: t('settings.discardChanges'),
    cancelLabel: t('settings.keepEditing'),
    destructive: true,
  })
  if (discard) showModal.value = false
}

/** Escape is the keyboard form of the backdrop click: same guard, same answer. */
function onProviderModalKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || !showModal.value || event.defaultPrevented) return
  event.preventDefault()
  void requestCloseProviderModal()
}
onMounted(() => window.addEventListener('keydown', onProviderModalKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', onProviderModalKeydown))

const showModelModal = ref(false)
const editingModelParameters = ref<{ providerId: string; model: string } | null>(null)

async function modelParametersSaved() {
  editingModelParameters.value = null
  await loadModelCenter()
  notify.success(t('settings.modelParametersSaved'))
}
const modelModalProviderId = ref('')
const modelModalManual = ref('')
/** New models only: whatever is already persisted stays saved whether or not a row is ticked. */
const modelModalSelected = ref<string[]>([])
const modelModalDiscovered = ref<Array<{ id: string; display_name: string }>>([])
const modelModalQuery = ref('')
const modelModalBusy = ref(false)
const modelModalError = ref('')
const modelModalFetched = ref(false)
/** A discovery answered after another dialog opened belongs to the dialog that asked. */
let modelModalFetchToken = 0

const modelModalExisting = computed(() => modelApiProvider(modelModalProviderId.value)?.models ?? [])

/**
 * One list for everything the provider can offer: saved models (ticked, fixed), discovered ones
 * and names typed by hand — deduped, so "already saved" cannot be added twice.
 */
const modelModalRows = computed(() => {
  const existing = modelModalExisting.value
  const seen = new Set(existing)
  const rows = existing.map((id) => ({ id, label: id, saved: true }))
  for (const item of modelModalDiscovered.value) {
    if (!item.id || seen.has(item.id)) continue
    seen.add(item.id)
    rows.push({ id: item.id, label: item.display_name || item.id, saved: false })
  }
  for (const id of modelModalSelected.value) {
    if (seen.has(id)) continue
    seen.add(id)
    rows.push({ id, label: id, saved: false })
  }
  return rows
})

const modelModalVisibleRows = computed(() => {
  const query = modelModalQuery.value.trim().toLocaleLowerCase()
  return modelModalRows.value.filter((row) => !query
    || row.id.toLocaleLowerCase().includes(query)
    || row.label.toLocaleLowerCase().includes(query))
})

function openAddModelModal(providerId: string) {
  modelModalProviderId.value = providerId
  modelModalManual.value = ''
  modelModalSelected.value = []
  modelModalDiscovered.value = []
  modelModalQuery.value = ''
  modelModalError.value = ''
  modelModalFetched.value = false
  modelModalBusy.value = false
  modelModalFetchToken += 1
  showModelModal.value = true
}

function closeModelModal() {
  // An in-flight discovery no longer owns this dialog; its answer is dropped on arrival.
  modelModalFetchToken += 1
  modelModalBusy.value = false
  showModelModal.value = false
}

function toggleModelSelection(id: string) {
  if (modelModalExisting.value.includes(id)) return
  modelModalSelected.value = modelModalSelected.value.includes(id)
    ? modelModalSelected.value.filter((item) => item !== id)
    : [...modelModalSelected.value, id]
}

async function fetchDiscoveredModels() {
  const providerId = modelModalProviderId.value
  const token = ++modelModalFetchToken
  modelModalBusy.value = true
  modelModalError.value = ''
  try {
    const result = await discoverProviderModels(providerId)
    // Closed, or another provider's dialog replaced this one while the request was in flight.
    if (token !== modelModalFetchToken || providerId !== modelModalProviderId.value || !showModelModal.value) return
    modelModalDiscovered.value = result
    modelModalFetched.value = true
    // Pre-select what the provider does not persist yet; the user confirms via save.
    const fresh = result
      .map((model) => model.id)
      .filter((id) => id && !modelModalExisting.value.includes(id) && !modelModalSelected.value.includes(id))
    modelModalSelected.value = [...modelModalSelected.value, ...fresh]
    notify.success(t(fresh.length > 0 ? 'settings.discoveredNewModels' : 'settings.noNewModels', { count: fresh.length }))
  } catch (error) {
    if (token !== modelModalFetchToken) return
    modelModalError.value = error instanceof Error ? error.message : String(error)
  } finally {
    if (token === modelModalFetchToken) modelModalBusy.value = false
  }
}

function addManualModel() {
  const id = modelModalManual.value.trim()
  if (!id) return
  if (!modelModalExisting.value.includes(id)) {
    if (!modelModalSelected.value.includes(id)) modelModalSelected.value = [...modelModalSelected.value, id]
  }
  modelModalManual.value = ''
}

async function saveModelModal() {
  const provider = modelApiProvider(modelModalProviderId.value)
  if (!provider) return
  const merged = [...new Set([...(provider.models ?? []), ...modelModalSelected.value])]
  modelModalBusy.value = true
  try {
    if (await saveProviderModels(provider, merged, provider.model ?? merged[0] ?? null)) closeModelModal()
  } finally { modelModalBusy.value = false }
}

/** Channels Core can start this binary on, split by whether this build can actually drive them. */
function drivableChannels(candidate: CliDiscoveryCandidateDto): HarnessChannelDto[] {
  return (candidate.channels ?? []).filter((channel) => channel.drivable)
}

function blockedChannels(candidate: CliDiscoveryCandidateDto): HarnessChannelDto[] {
  return (candidate.channels ?? []).filter((channel) => !channel.drivable)
}

function channelLabel(fact: HarnessChannelDto): string {
  return t(discoveryAffordanceLabelKey(fact))
}

/** Why this build cannot drive a channel, from its code; Core's text, then a general sentence, otherwise. */
function channelReason(channel: HarnessChannelDto, candidate: CliDiscoveryCandidateDto): string {
  const key = `settings.channelReason.${channel.reason_code}`
  return channel.reason_code && te(key) ? t(key, { harness: candidate.display_name }) : channel.reason ?? t('settings.channelNotDrivableDefault')
}

/** Why a probe could not confirm the binary, from its code and facts; Core's text when there are no words for it. */
function probeNote(candidate: CliDiscoveryCandidateDto): string | null {
  const key = `settings.probeNote.${candidate.probe_note_code}`
  return candidate.probe_note_code && te(key) ? t(key, candidate.probe_note_data ?? {}) : candidate.probe_note ?? null
}

/**
 * The server's status note for a provider, when it says more than the status badge: a configured or
 * disabled provider carries only a fixed diagnostic line there, not something to show a person.
 */
function providerNote(provider: { status?: string | null; status_message?: string | null }): string | null {
  return provider.status_message && !['configured', 'disabled'].includes(provider.status ?? '') ? provider.status_message : null
}

// ── Model Center route fallback editor ──────────────────────────────
// Ordered candidate chains: one write publishes an immutable route version.
const routeEditorPurpose = ref('')
const routeEditorCandidates = ref<Array<{ provider_instance_id: string; model: string | null }>>([])
const routeEditorBusy = ref(false)
const routeEditorAdding = ref(false)
const routeEditorNewProviderId = ref('')
const routeEditorNewModelId = ref('')
const modelReferenceRows = ref<Record<string, ModelReferenceDto[]>>({})
const modelReferenceBusy = ref<Record<string, boolean>>({})

const routeEditorRoute = computed(() =>
  routes.value.find((route) => route.purpose === routeEditorPurpose.value) ?? null
)
const routeEditorDirty = computed(() => {
  const route = routeEditorRoute.value
  if (!route) return false
  const current = route.candidates?.map((candidate) => ({ provider_instance_id: candidate.provider_instance_id, model: candidate.model ?? null })) ?? []
  const editing = routeEditorCandidates.value
  return current.length !== editing.length
    || current.some((candidate, index) =>
      candidate.provider_instance_id !== editing[index]?.provider_instance_id
      || (candidate.model ?? null) !== (editing[index]?.model ?? null))
})

function openRouteEditor(purpose: string) {
  const route = routes.value.find((item) => item.purpose === purpose)
  routeEditorPurpose.value = purpose
  routeEditorCandidates.value = (route?.candidates ?? []).map((candidate) => ({
    provider_instance_id: candidate.provider_instance_id,
    model: candidate.model ?? null,
  }))
  routeEditorAdding.value = false
  routeEditorNewProviderId.value = ''
  routeEditorNewModelId.value = ''
}

function routeCandidateProviderLabel(providerInstanceId: string): string {
  const provider = providers.value.find((item) => item.id === providerInstanceId)
  if (provider) return provider.display_name
  const cli = props.runtimeCliOptions.find((item) => item.provider_instance_id === providerInstanceId)
  if (cli) return `${cli.display_name} (CLI)`
  const acp = props.runtimeAcpOptions.find((item) => item.provider_instance_id === providerInstanceId)
  if (acp) return `${acp.display_name} (ACP)`
  return providerInstanceId
}

function moveRouteCandidate(index: number, delta: -1 | 1) {
  const target = index + delta
  if (target < 0 || target >= routeEditorCandidates.value.length) return
  const candidates = [...routeEditorCandidates.value]
  const [moved] = candidates.splice(index, 1)
  candidates.splice(target, 0, moved)
  routeEditorCandidates.value = candidates
}

function removeRouteCandidate(index: number) {
  routeEditorCandidates.value = routeEditorCandidates.value.filter((_, i) => i !== index)
}

function addRouteCandidate() {
  const providerInstanceId = routeEditorNewProviderId.value
  if (!providerInstanceId) return
  routeEditorCandidates.value = [
    ...routeEditorCandidates.value,
    { provider_instance_id: providerInstanceId, model: routeEditorNewModelId.value || null },
  ]
  routeEditorAdding.value = false
  routeEditorNewProviderId.value = ''
  routeEditorNewModelId.value = ''
}

async function saveRouteCandidates() {
  if (!routeEditorPurpose.value) return
  routeEditorBusy.value = true
  try {
    if (await props.center.saveRouteCandidates(routeEditorPurpose.value, routeEditorCandidates.value)) {
      openRouteEditor(routeEditorPurpose.value)
    }
  } finally {
    routeEditorBusy.value = false
  }
}

/** Reverse references (agents / modes / routes / recent invocations) for one configured model. */
async function toggleModelReferences(providerInstanceId: string, modelId: string) {
  const key = `${providerInstanceId}:${modelId}`
  if (modelReferenceRows.value[key]) {
    delete modelReferenceRows.value[key]
    modelReferenceRows.value = { ...modelReferenceRows.value }
    return
  }
  modelReferenceBusy.value = { ...modelReferenceBusy.value, [key]: true }
  try {
    const references = await fetchModelReferences(providerInstanceId, modelId)
    modelReferenceRows.value = { ...modelReferenceRows.value, [key]: references }
  } catch {
    modelReferenceRows.value = { ...modelReferenceRows.value, [key]: [] }
  } finally {
    modelReferenceBusy.value = { ...modelReferenceBusy.value, [key]: false }
  }
}

function referenceKey(providerInstanceId: string, modelId: string): string {
  return `${providerInstanceId}:${modelId}`
}

function referenceKindLabel(kind: string): string {  const map: Record<string, string> = {
    route: t('settings.centerRoutes'),
    agent_default: t('settings.referenceAgentDefault'),
    mode_override: t('settings.referenceModeOverride'),
    session_override: t('settings.referenceMeetingOverride'),
    recent_invocation: t('settings.referenceRecentInvocation'),
  }
  return map[kind] ?? kind
}

/** A reference's detail in words: a route's candidate position, or the referencing record's status (a recent invocation's detail is an id). */
function referenceDetailLabel(reference: ModelReferenceDto): string | null {
  const detail = reference.detail
  if (!detail || reference.reference_kind === 'recent_invocation') return detail ?? null
  const candidate = /^candidate:(\d+)$/.exec(detail)
  if (candidate) return t('settings.referenceCandidate', { position: candidate[1] })
  return te(`agentCenter.filter.${detail}`) ? t(`agentCenter.filter.${detail}`) : detail
}

async function saveProvider() {
  providerFormSubmitted.value = true
  if (Object.keys(providerFormErrors.value).length > 0) {
    // Point at the first fixable field rather than leaving the user to scan the form.
    void nextTick(() => document.querySelector<HTMLElement>('.model-provider-form [aria-invalid="true"]')?.focus())
    return
  }
  modelCenterBusy.value = true
  try {
    const isNewProvider = !providerForm.id
    const tmpl = currentTemplate.value
    const payload: SaveModelProviderInstanceInput = {
      id: providerForm.id || undefined,
      driver: providerForm.driver,
      display_name: providerForm.display_name,
      connection_kind: providerForm.connection_kind,
      protocol: providerForm.protocol || null,
      base_url: formFields.value.base_url ? (providerForm.base_url || null) : null,
      model: providerForm.id ? (providerForm.model || null) : null,
      models: providerForm.models,
      api_key: formFields.value.api_key ? (providerForm.api_key || null) : null,
      clear_api_key: providerForm.clear_api_key,
      binary_path: formFields.value.binary_path ? (providerForm.binary_path || null) : null,
      home_path: formFields.value.home_path ? (providerForm.home_path || null) : null,
      server_url: formFields.value.server_url ? (providerForm.server_url || null) : null,
      launch_args: formFields.value.launch_args ? (providerForm.launch_args || null) : null,
      capabilities: providerForm.id
        ? selectedProvider.value?.capabilities ?? tmpl?.capabilities ?? []
        : tmpl?.capabilities ?? [],
      enabled: providerForm.enabled
    }

    await saveProviderInstance(payload, {
      id: providerForm.id,
      expectedRevision: selectedProvider.value?.revision,
      force: forceProviderWrite.value,
    })

    showModal.value = false
    // Land on what was just saved; a new provider's next step is its models, not a default route.
    if (selectedProviderId.value) selectedProviderDetailId.value = selectedProviderId.value
    providerDetailOpen.value = true
    if (isNewProvider) {
      modelProviderFilter.value = 'configured'
    }
  } catch (error) {
    const err = error as { code?: string | null; message?: string }
    if (err.code === 'provider_in_use') {
      // The form's enabled toggle triggered the guard; ask once and retry with force.
      const message = err.message ?? t('settings.providerInUse')
      if (!await confirm({
        title: providerForm.display_name,
        message,
        confirmLabel: t('settings.forceAnyway'),
        cancelLabel: t('settings.cancel'),
        destructive: true
      })) return
      forceProviderWrite.value = true
      await saveProvider()
      return
    }
    if (props.center.isModelWriteConflict(error)) {
      notify.warning({ message: t('settings.configStaleReloading') })
      await loadModelCenter()
    } else {
      notify.error(error, { title: providerForm.display_name })
    }
  }
}

function connectionKindLabel(kind: string) {
  if (kind === 'cli') return t('settings.connectionKindCli')
  if (kind === 'local-server') return t('settings.connectionKindLocal')
  if (kind === 'public-api') return t('settings.connectionKindPublicApi')
  return t('settings.connectionKindApiKey')
}

function providerPresentation(driver: string) {
  return findTemplate(driver)
}

function statusVariant(status: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (status === 'ready') return 'default'
  if (status === 'needs_key' || status === 'not_configured') return 'destructive'
  if (status === 'disabled') return 'secondary'
  if (status === 'cooldown') return 'outline'
  return 'outline'
}

function readinessVariant(status: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (status === 'ready') return 'default'
  if (status === 'blocked') return 'destructive'
  if (status === 'warning') return 'outline'
  return 'secondary'
}

function readinessStatusLabel(status: string) {
  if (status === 'ready') return t('settings.readinessReady')
  if (status === 'blocked') return t('settings.readinessBlocked')
  if (status === 'warning') return t('settings.readinessWarning')
  return status
}

/** Deep links open one provider in the pane its runtime lives in. */
async function focusProvider(providerId: string): Promise<void> {
  modelCenterSection.value = 'api'
  modelProviderQuery.value = ''
  modelProviderFilter.value = 'all'
  selectedProviderDetailId.value = providerId
  providerDetailOpen.value = true
  const overview = modelCenterOverview.value
  modelCenterSection.value = overview?.cli_runtimes.some(row => row.provider_instance_id === providerId) ? 'cli'
    : overview?.tui_runtimes.some(row => row.provider_instance_id === providerId) ? 'tui'
      : overview?.acp_runtimes.some(row => row.provider_instance_id === providerId) ? 'acp' : 'api'
  await nextTick()
}

/** A pending provider form must not be discarded by a tab switch, window close or route leave. */
async function canLeave(): Promise<boolean> {
  if (!showModal.value) return true
  await requestCloseProviderModal()
  return !showModal.value
}

defineExpose({ canLeave, focusProvider })
</script>

<template>
  <ModelScopeNotice v-if="canAccessBackend" />
<div class="center-page model-center-page">
  <div class="center-command-bar">
    <div>
      <h2>{{ t('settings.modelCenter') }}</h2>
      <p>{{ t('settings.modelCenterSubtitle') }}</p>
    </div>
    <div class="center-command-actions">
      <UiButton variant="outline" size="sm" @click="handleAddProviderClick">
        <Plus :size="14" />
        <span>{{ t('settings.addProvider') }}</span>
      </UiButton>
      <UiButton variant="outline" size="sm" :disabled="modelCenterLoading || modelCenterBusy || !canAccessBackend" @click="loadModelCenter">
        <RefreshCw :size="14" />
        <span>{{ t('settings.refresh') }}</span>
      </UiButton>
      <UiButton variant="ghost" size="sm" :disabled="!canAccessBackend" @click="openModelDiagnostics">
        <Activity :size="14" />
        <span>{{ t('settings.advancedDiagnostics') }}</span>
      </UiButton>
    </div>
  </div>

  <!-- A partial failure keeps the data it could read and reports itself further down;
       this banner is only for a read that produced nothing at all. Saying "the center
       failed to load" beside readable rows would be false. -->
  <div v-if="modelCenterFailure && !modelCenterOverview" class="center-message" role="alert" data-testid="model-center-read-failure">
    <Info :size="16" aria-hidden="true" />
    <div class="center-message-content">
      <strong>{{ t('settings.centerLoadFailed') }}</strong>
      <span>{{ modelCenterFailure.message }}</span>
      <span v-if="modelCenterFailure.code">{{ modelCenterFailure.code }}</span>
      <pre v-if="modelCenterFailure.details">{{ modelCenterFailure.details }}</pre>
    </div>
    <UiButton variant="outline" size="sm" :disabled="modelCenterLoading || !canAccessBackend" @click="loadModelCenter">{{ t('settings.retry') }}</UiButton>
  </div>

<div v-if="modelCenterLoading && !modelCenterOverview" class="center-loading-state" aria-live="polite">
    <UiSkeleton v-for="index in 3" :key="index" class="center-loading-line" />
  </div>

  <!-- Same data already on screen and a refresh in flight: the skeleton above would
       throw away readable rows, so the progress is a line that says so. -->
  <p v-if="modelCenterLoading && modelCenterOverview" class="center-stale-status" role="status">
    <RefreshCw :size="13" class="center-stale-spinner" aria-hidden="true" />
    {{ t('settings.refreshing') }}
  </p>

  <div v-if="modelCenterDiagnostics.length > 0" class="center-message warning center-diagnostics-message">
    <Info :size="16" />
    <div class="center-message-content">
      <strong>{{ t('settings.centerDiagnostics') }}</strong>
      <ul>
        <li v-for="diagnostic in modelCenterDiagnostics" :key="`${diagnostic.code}:${diagnostic.source ?? ''}:${diagnostic.status ?? ''}`">
          {{ centerDiagnosticLabel(diagnostic) }}
        </li>
      </ul>
    </div>
    <UiButton variant="outline" size="sm" :disabled="modelCenterLoading" @click="loadModelCenter">{{ t('settings.retry') }}</UiButton>
  </div>

  <!-- A single read that failed keeps its section's previous data. Saying so is the
       difference between "still loading" and "this one source is stale". -->
  <div v-if="modelCenterPartialFailureSources.length > 0" class="center-message warning">
    <Info :size="16" />
    <div class="center-message-content">
      <strong>{{ t('settings.centerPartialLoadFailed') }}</strong>
      <ul>
        <li v-for="source in modelCenterPartialFailureSources" :key="source">{{ source }}</li>
      </ul>
    </div>
  </div>

  <!-- A readiness problem that has a repair action stays on the first screen. The capability
       receipts, health metrics and developer diagnostics live one click away, in Diagnostics. -->
  <div v-if="modelReadiness && modelReadiness.status !== 'ready'" class="model-health-alert model-center-health-alert" role="status">
    <Info :size="16" />
    <div>
      <strong>
        {{ firstNeedsKeyProvider
          ? t('settings.missingKeySummary', { name: firstNeedsKeyProvider.display_name })
          : t('settings.modelIssuesSummary') }}
      </strong>
      <span>{{ t('settings.modelIssueHint') }}</span>
    </div>
    <UiButton v-if="firstNeedsKeyProvider" variant="outline" size="sm" @click="openEditModal(firstNeedsKeyProvider)">
      {{ t('settings.configureNow') }}
    </UiButton>
    <UiButton v-else-if="modelCenterIssueCount > 0" variant="outline" size="sm" @click="focusModelProviderList('issues')">
      {{ t('settings.viewIssues') }}
    </UiButton>
    <UiButton v-else variant="outline" size="sm" @click="openModelDiagnostics">
      {{ t('settings.advancedDiagnostics') }}
    </UiButton>
  </div>

  <div class="model-center-shell">
    <div class="model-center-tabs" role="tablist" :aria-label="t('settings.modelCenterResources')">
      <button
        v-for="tab in modelCenterTabs"
        :key="tab.key"
        role="tab"
        :aria-selected="centerTab === tab.key"
        :tabindex="centerTab === tab.key ? 0 : -1"
        :class="{ active: centerTab === tab.key }"
        @click="selectCenterTab(tab.key)"
        @keydown="onCenterTabKeydown"
      >
        <span>{{ tab.label }}</span>
        <UiBadge variant="secondary">{{ tab.count }}</UiBadge>
      </button>
    </div>

    <div class="model-center-default-chat">
      <span class="model-center-default-label">{{ t('settings.defaultChatModel') }}</span>
      <Route :size="15" aria-hidden="true" />
      <strong v-if="defaultChatCandidate" class="model-center-default-value">{{ defaultChatLabel }}</strong>
      <span v-else class="quiet">{{ t('settings.defaultChatNone') }}</span>
      <UiSelectField
        v-if="defaultChatOptions.length > 0"
        class="model-center-default-select"
        :model-value="defaultChatValue"
        :options="defaultChatOptions"
        :aria-label="t('settings.defaultChatModel')"
        :placeholder="t('settings.defaultChatNone')"
        @update:model-value="changeDefaultChat"
      />
      <UiButton variant="ghost" size="sm" @click="selectCenterTab('routes')">
        {{ t('settings.defaultChatChange') }}
      </UiButton>
    </div>

    <main class="center-resource-stage model-center-stage">
  <section v-if="modelCenterSection === 'models'" class="center-resource-section">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.centerModels') }}</h3>
        <p>{{ t('settings.configuredModelsHint') }}</p>
      </div>
      <UiBadge variant="outline">{{ modelCatalogModeLabel(modelCenterOverview?.capabilities.model_catalog_mode) }}</UiBadge>
    </div>
    <div class="model-center-catalog-toolbar">
      <div class="model-provider-search">
        <Search :size="15" />
        <UiInput v-model="catalogQuery" :placeholder="t('settings.modelCatalogSearch')" :aria-label="t('settings.modelCatalogSearch')" />
      </div>
      <span class="model-provider-count">{{ t('settings.providerResultCount', { visible: modelCatalogCount, total: modelCenterOverview?.models.length ?? 0 }) }}</span>
    </div>
    <div
      v-for="{ provider, models } in modelCatalogGroups"
      :key="provider.id"
      class="model-provider-group"
    >
      <div class="center-resource-list-row model-provider-group-head">
        <div class="center-resource-primary">
          <span
            class="provider-brand-icon"
            :style="{ color: providerPresentation(provider.driver)?.brand_color, backgroundColor: providerPresentation(provider.driver)?.brand_bg }"
          >
            <span v-if="providerPresentation(provider.driver)?.icon" class="provider-brand-mark" v-html="providerPresentation(provider.driver)?.icon"></span>
            <Server v-else :size="16" />
          </span>
          <div>
            <strong>{{ provider.display_name }}</strong>
            <span>{{ provider.driver }}</span>
          </div>
        </div>
        <UiBadge :variant="statusVariant(provider.status)">{{ statusLabel(provider.status) }}</UiBadge>
        <UiButton
          variant="ghost"
          size="sm"
          :title="t('settings.locateProvider')"
          @click="locateProvider(provider.id)"
        >
          <PanelRight :size="14" />
          {{ t('settings.locateProvider') }}
        </UiButton>
        <UiButton
          variant="outline"
          size="sm"
          :disabled="modelCenterBusy || !modelCenterOverview?.capabilities.model_discovery_refresh"
          :title="modelCenterOverview?.capabilities.model_discovery_refresh ? t('settings.addModel') : t('settings.modelDiscoveryUnsupported')"
          @click="openAddModelModal(provider.id)"
        >
          <Plus :size="14" />
          {{ t('settings.addModel') }}
        </UiButton>
      </div>
      <div class="model-group-models">
        <template v-for="model in models" :key="model.id">
        <div class="center-resource-list-row model-group-model">
          <div class="center-resource-primary">
            <Cpu :size="15" />
            <div>
              <strong :title="model.model_id">{{ model.model_id }}</strong>
              <div class="center-resource-meta">
                <span v-for="source in model.configuration_sources" :key="source">{{ configuredModelSourceLabel(source) }}</span>
                <span v-for="purpose in model.route_purposes" :key="purpose">{{ purpose }}</span>
              </div>
              <div v-if="modelReferenceRows[`${provider.id}:${model.model_id}`]" class="model-reference-rows">
                <span v-for="(reference, index) in modelReferenceRows[`${provider.id}:${model.model_id}`]" :key="index">
                  {{ referenceKindLabel(reference.reference_kind) }}<template v-if="reference.detail"> · {{ referenceDetailLabel(reference) }}</template>
                </span>
                <span v-if="modelReferenceRows[`${provider.id}:${model.model_id}`].length === 0" class="quiet">{{ t('settings.noModelReferences') }}</span>
              </div>
            </div>
          </div>
          <UiBadge :variant="statusVariant(model.status)">{{ statusLabel(model.status) }}</UiBadge>
          <div class="center-resource-actions">
          <UiButton
            variant="outline"
            size="sm"
            :disabled="modelCenterBusy"
            :title="t('settings.setDefaultModel')"
            @click="setDefaultChatModel(provider.id, model.model_id)"
          >
            {{ t('settings.setDefaultModel') }}
          </UiButton>
          <UiButton variant="outline" size="sm" :disabled="modelCenterBusy" :aria-expanded="editingModelParameters?.providerId === provider.id && editingModelParameters?.model === model.model_id"
            @click="editingModelParameters = editingModelParameters?.providerId === provider.id && editingModelParameters?.model === model.model_id ? null : { providerId: provider.id, model: model.model_id }">
            <Settings2 data-icon="inline-start" />
            {{ t('settings.modelParameters') }}
          </UiButton>
          <UiButton
            variant="ghost"
            size="sm"
            :disabled="modelReferenceBusy[`${provider.id}:${model.model_id}`]"
            :title="t('settings.toggleModelReferences')"
            @click="toggleModelReferences(provider.id, model.model_id)"
          >
            <Info :size="14" />
          </UiButton>
          <UiButton
            variant="ghost"
            size="sm"
            :disabled="modelCenterBusy"
            :title="t('settings.removeModel')"
            @click="removeModel(provider.id, model.model_id)"
          >
            <Trash2 :size="14" />
          </UiButton>
          </div>
        </div>
        <ModelParametersEditor v-if="editingModelParameters?.providerId === provider.id && editingModelParameters?.model === model.model_id && providers.find(item => item.id === provider.id)"
          :provider="providers.find(item => item.id === provider.id)!" :model="model.model_id"
          @saved="modelParametersSaved" @cancel="editingModelParameters = null" @reload="loadModelCenter" />
        </template>
        <div v-if="models.length === 0" class="center-empty-state">
          <Cpu :size="20" />
          <span>{{ t('settings.noProviderModels') }}</span>
        </div>
      </div>
    </div>
    <div v-if="modelCatalogGroups.length === 0" class="center-empty-state">
      <Cpu :size="20" />
      <span>{{ catalogQuery.trim() ? t('settings.modelFilterEmpty') : t('settings.noConfiguredModels') }}</span>
    </div>
  </section>

  <section v-if="modelCenterSection === 'routes'" class="center-resource-section">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.centerRoutes') }}</h3>
        <p>{{ t('settings.centerRoutesHint') }}</p>
      </div>
      <UiBadge variant="outline">{{ routes.length }}</UiBadge>
    </div>
    <div class="center-resource-list-row model-center-route-row" :class="{ active: routeEditorPurpose === route.purpose, default: route.purpose === 'chat' }" v-for="route in sortedRoutes" :key="route.id ?? route.purpose">
      <button class="agent-card-select" @click="openRouteEditor(route.purpose)">
        <Route :size="15" />
        <div class="center-resource-primary">
          <strong>
            {{ route.purpose }}
            <span v-if="route.purpose === 'chat'" class="provider-cap-tag">{{ t('settings.defaultChatModel') }}</span>
          </strong>
          <span>
            {{ (route.candidates ?? []).map((candidate) => `${routeCandidateProviderLabel(candidate.provider_instance_id)}${candidate.model ? ` · ${candidate.model}` : ''}`).join(' → ') || t('settings.routeNoCandidates') }}
          </span>
        </div>
      </button>
    </div>
    <div v-if="routes.length === 0" class="center-empty-state">
      <Route :size="20" />
      <span>{{ t('settings.routeNoCandidates') }}</span>
    </div>

    <div v-if="routeEditorPurpose" class="route-candidate-editor">
      <div class="route-candidate-head">
        <strong>{{ routeEditorPurpose }}</strong>
        <span>{{ t('settings.routeEditorHint') }}</span>
      </div>
      <div v-for="(candidate, index) in routeEditorCandidates" :key="`${candidate.provider_instance_id}:${index}`" class="route-candidate-row">
        <UiBadge variant="secondary">{{ index + 1 }}</UiBadge>
        <div class="route-candidate-main">
          <strong>{{ routeCandidateProviderLabel(candidate.provider_instance_id) }}</strong>
          <span :title="candidate.model ?? undefined">{{ candidate.model ?? t('settings.fixedModelNoModel') }}</span>
        </div>
        <UiButton variant="ghost" size="icon" :disabled="routeEditorBusy || index === 0" :title="t('settings.routeCandidateUp')" @click="moveRouteCandidate(index, -1)">
          <ChevronRight :size="14" class="rotate-[-90deg]" />
        </UiButton>
        <UiButton variant="ghost" size="icon" :disabled="routeEditorBusy || index === routeEditorCandidates.length - 1" :title="t('settings.routeCandidateDown')" @click="moveRouteCandidate(index, 1)">
          <ChevronRight :size="14" class="rotate-90" />
        </UiButton>
        <UiButton variant="ghost" size="icon" :disabled="routeEditorBusy" :title="t('settings.delete')" @click="removeRouteCandidate(index)">
          <Trash2 :size="14" />
        </UiButton>
      </div>
      <div v-if="routeEditorCandidates.length === 0" class="center-empty-state">
        <Route :size="20" />
        <span>{{ t('settings.routeNoCandidates') }}</span>
      </div>

      <div v-if="routeEditorAdding" class="route-candidate-add">
        <UiSelectField
          v-model="routeEditorNewProviderId"
          :aria-label="t('settings.selectProvider')"
          :options="[
            { value: '', label: t('settings.selectProvider'), disabled: true },
            ...providers.map((provider) => ({ value: provider.id, label: provider.display_name })),
            ...runtimeCliOptions.map((runtime) => ({ value: runtime.provider_instance_id, label: `${runtime.display_name} (CLI)` })),
            ...runtimeAcpOptions.map((runtime) => ({ value: runtime.provider_instance_id ?? runtime.runtime_id, label: `${runtime.display_name} (ACP)` })),
          ]"
        />
        <UiSelectField
          v-if="modelsForProvider(routeEditorNewProviderId).length > 0"
          v-model="routeEditorNewModelId"
          :aria-label="t('settings.model')"
          :options="[
            { value: '', label: t('settings.fixedModelNoModel') },
            ...modelsForProvider(routeEditorNewProviderId).map((model) => ({ value: model.model_id, label: model.model_id })),
          ]"
        />
        <UiButton variant="outline" size="sm" :disabled="!routeEditorNewProviderId" @click="addRouteCandidate">
          <Check :size="14" />
        </UiButton>
        <UiButton variant="ghost" size="sm" @click="routeEditorAdding = false">{{ t('settings.cancel') }}</UiButton>
      </div>
      <div v-else class="route-candidate-actions">
        <UiButton variant="outline" size="sm" :disabled="routeEditorBusy" @click="routeEditorAdding = true">
          <Plus :size="14" />
          <span>{{ t('settings.routeAddCandidate') }}</span>
        </UiButton>
        <UiButton
          variant="outline"
          size="sm"
          :disabled="routeEditorBusy || !routeEditorDirty"
          @click="saveRouteCandidates"
        >
          <Save :size="14" />
          <span>{{ t('settings.saveRoute') }}</span>
        </UiButton>
      </div>
    </div>
  </section>

  <div v-if="centerTab === 'runtime'" class="model-center-subtabs" role="tablist" :aria-label="t('settings.centerRuntime')">
    <button
      v-for="section in runtimeSections"
      :key="section.key"
      role="tab"
      :aria-selected="modelCenterSection === section.key"
      :tabindex="modelCenterSection === section.key ? 0 : -1"
      :class="{ active: modelCenterSection === section.key }"
      @click="selectRuntimeTab(section.key)"
      @keydown="onRuntimeTabKeydown"
    >
      <span>{{ section.label }}</span>
      <UiBadge variant="secondary">{{ section.count }}</UiBadge>
    </button>
  </div>

  <section v-if="modelCenterSection === 'cli'" class="center-resource-section">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.centerCli') }}</h3>
        <p>{{ t('settings.cliRuntimeHint') }}</p>
      </div>
      <UiButton
        variant="outline"
        size="sm"
        :disabled="cliDiscoveryLoading"
        @click="discoverHarnesses"
      >
        <RefreshCw :size="14" :class="{ 'animate-spin': cliDiscoveryLoading }" />
        {{ cliDiscoveryLoading ? t('settings.discoveringCli') : t('settings.discoverCli') }}
      </UiButton>
    </div>

    <!-- Discovery Candidates Panel -->
    <div v-if="cliDiscoveryLoaded" class="cli-discovery-panel">
      <div class="cli-discovery-header">
        <strong>{{ t('settings.cliDiscoveryTitle') }}</strong>
        <p>{{ t('settings.cliDiscoveryHint') }}</p>
      </div>
      <div v-if="cliDiscoveryCandidates.length === 0" class="cli-discovery-empty">
        {{ t('settings.cliDiscoveryEmpty') }}
      </div>
      <div v-else class="cli-discovery-grid">
        <div
          v-for="candidate in cliDiscoveryCandidates"
          :key="candidate.driver"
          class="cli-discovery-card"
          :class="{ 'is-found': candidate.status === 'found', 'is-configured': candidate.status === 'configured', 'is-missing': candidate.status === 'missing' }"
        >
          <div class="cli-discovery-info">
            <div class="cli-discovery-name-row">
              <Terminal :size="15" />
              <strong>{{ candidate.display_name }}</strong>
              <span class="cli-discovery-driver">{{ candidate.driver }}</span>
            </div>
            <code v-if="candidate.binary_path" class="cli-discovery-path" :title="candidate.binary_path">
              {{ candidate.binary_path }}
            </code>
            <span v-else-if="candidate.status === 'missing'" class="cli-discovery-status-text muted" :title="probeNote(candidate) === candidate.probe_note ? undefined : candidate.probe_note ?? undefined">
              {{ probeNote(candidate) || t('settings.notDetected') }}
            </span>
          </div>
          <div class="cli-discovery-action">
            <UiBadge v-if="candidate.status === 'configured'" variant="secondary">
              {{ t('settings.alreadyConnected') }}
            </UiBadge>
            <template v-if="candidate.status === 'found' || candidate.status === 'configured'">
              <!-- One button per channel this build can drive, and a named reason for the ones
                   it cannot. A single "Quick connect" would pick a channel silently, which is
                   the confusion the channel axis exists to end. A configured harness still shows
                   the other channel buttons so ACP and CLI can coexist for one vendor binary. -->
              <UiButton
                v-for="channel in drivableChannels(candidate)"
                :key="channel.channel ?? channel.protocol"
                variant="default"
                size="sm"
                :disabled="cliDiscoveryLoading"
                @click="connectDiscoveredCli(candidate, channel)"
              >
                <Plus :size="13" />
                {{ t('settings.quickConnectChannel', { channel: channelLabel(channel) }) }}
              </UiButton>
              <UiBadge
                v-for="channel in blockedChannels(candidate)"
                :key="`blocked-${channel.channel ?? channel.protocol}`"
                variant="outline"
                class="muted-badge"
                :title="channelReason(channel, candidate)"
              >
                {{ t('settings.channelNotDrivable', { channel: channelLabel(channel) }) }}
              </UiBadge>
              <UiButton
                v-if="drivableChannels(candidate).length === 0 && blockedChannels(candidate).length === 0"
                variant="default"
                size="sm"
                :disabled="cliDiscoveryLoading"
                @click="connectDiscoveredCli(candidate)"
              >
                <Plus :size="13" />
                {{ t('settings.quickConnect') }}
              </UiButton>
            </template>
            <UiBadge v-else variant="outline" class="muted-badge">
              {{ t('settings.notDetected') }}
            </UiBadge>
          </div>
        </div>
      </div>
    </div>

    <div class="center-resource-list">
      <article v-for="runtime in modelCenterOverview?.cli_runtimes ?? []" :key="runtime.runtime_id" class="center-resource-list-row">
        <div class="center-resource-primary">
          <Terminal :size="17" />
          <div>
            <strong>{{ runtime.display_name }}</strong>
            <span>{{ runtime.driver }} · {{ runtime.runtime_id }}</span>
          </div>
        </div>
        <div class="center-resource-paths">
          <code>{{ runtime.binary_path || t('settings.pathNotConfigured') }}</code>
          <code>{{ runtime.home_path || t('settings.workspaceNotConfigured') }}</code>
        </div>
        <UiBadge :variant="statusVariant(runtime.status)">{{ statusLabel(runtime.status) }}</UiBadge>
        <div class="center-resource-actions">
          <UiButton
            v-if="providers.find(provider => provider.id === runtime.provider_instance_id)"
            variant="outline"
            size="sm"
            @click="openEditModal(providers.find(provider => provider.id === runtime.provider_instance_id)!)"
          >
            <Settings2 :size="14" />
            {{ t('settings.editConfig') }}
          </UiButton>
          <UiButton
            v-if="runtime.provider_instance_id"
            variant="ghost"
            size="icon"
            class="provider-delete-btn"
            :disabled="modelCenterBusy"
            :title="t('settings.delete')"
            @click="deleteProvider(runtime.provider_instance_id)"
          >
            <Trash2 :size="14" />
          </UiButton>
        </div>
      </article>
    </div>
    <div v-if="(modelCenterOverview?.cli_runtimes.length ?? 0) === 0" class="center-empty-state">
      <Terminal :size="20" />
      <span>{{ t('settings.noCliRuntimes') }}</span>
    </div>
  </section>

  <section v-if="modelCenterSection === 'acp'" class="center-resource-section">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.centerAcp') }}</h3>
        <p>{{ t('settings.acpRuntimeHint') }}</p>
      </div>
      <UiButton variant="outline" size="sm" @click="router.push('/market')">
        <Plus :size="14" />
        {{ t('settings.manageInMarketplace') }}
      </UiButton>
    </div>
    <!-- Permanent, not dismissible: declaring fs/terminal capabilities as false does not stop
         the harness writing inside its own working directory, and Round 1 refuses every
         permission request on the agent's behalf. Both are facts about what a connected
         harness can do, and neither is discoverable from the row itself. -->
    <p class="acp-permission-notice" role="note">
      {{ t('settings.acpPermissionWarning') }}
    </p>
    <div class="center-resource-list">
      <article v-for="runtime in modelCenterOverview?.acp_runtimes ?? []" :key="runtime.runtime_id" class="center-resource-list-row">
        <div class="center-resource-primary">
          <Workflow :size="17" />
          <div>
            <strong>{{ runtime.display_name }}</strong>
            <span>{{ acpRuntimeSourceLabel(runtime.source) }} · {{ runtime.runtime_id }}</span>
          </div>
        </div>
        <div class="center-resource-paths">
          <code>{{ runtime.binary_path || t('settings.pathNotConfigured') }}</code>
        </div>
        <div class="center-resource-meta">
          <span v-for="capability in runtime.capabilities.slice(0, 4)" :key="capability">{{ capability }}</span>
        </div>
        <UiBadge :variant="statusVariant(runtime.status)">{{ statusLabel(runtime.status) }}</UiBadge>
        <div class="center-resource-actions">
          <UiButton
            v-if="providers.find(provider => provider.id === runtime.provider_instance_id)"
            variant="outline"
            size="sm"
            @click="openEditModal(providers.find(provider => provider.id === runtime.provider_instance_id)!)"
          >
            <Settings2 :size="14" />
            {{ t('settings.editConfig') }}
          </UiButton>
          <UiButton
            v-if="runtime.provider_instance_id"
            variant="ghost"
            size="icon"
            class="provider-delete-btn"
            :disabled="modelCenterBusy"
            :title="t('settings.delete')"
            @click="deleteProvider(runtime.provider_instance_id)"
          >
            <Trash2 :size="14" />
          </UiButton>
          <!-- No probe affordance here: Core answers 501 for adapter probes, so a button
               would be a control that cannot work, which is the failure class the read-only
               notices on these panes exist to prevent. -->
        </div>
      </article>
    </div>
    <div v-if="(modelCenterOverview?.acp_runtimes.length ?? 0) === 0" class="center-empty-state">
      <Workflow :size="20" />
      <span>{{ t('settings.noAcpRuntimes') }}</span>
    </div>
  </section>

  <section v-if="modelCenterSection === 'tui'" class="center-resource-section">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.centerTui') }}</h3>
        <p>{{ t('settings.tuiRuntimeHint') }}</p>
      </div>
    </div>
    <!-- TUI is a one-shot model channel backed by Core's PTY host. This pane remains an
         inventory; connecting a row happens through discovery so the selected channel is
         explicit and can coexist with ACP/CLI for the same binary. -->
    <p class="acp-permission-notice" role="note">
      {{ t('settings.tuiBackendUnavailable') }}
    </p>
    <div class="center-resource-list">
      <article v-for="runtime in modelCenterOverview?.tui_runtimes ?? []" :key="runtime.runtime_id" class="center-resource-list-row">
        <div class="center-resource-primary">
          <Terminal :size="17" />
          <div>
            <strong>{{ runtime.display_name }}</strong>
            <span>{{ runtime.driver }} · {{ runtime.protocol || t('settings.protocolUnknown') }}</span>
          </div>
        </div>
        <div class="center-resource-paths">
          <code>{{ runtime.binary_path || t('settings.pathNotConfigured') }}</code>
        </div>
        <UiBadge :variant="statusVariant(runtime.status)">{{ statusLabel(runtime.status) }}</UiBadge>
        <div class="center-resource-actions">
          <UiButton
            v-if="providers.find(provider => provider.id === runtime.provider_instance_id)"
            variant="outline"
            size="sm"
            @click="openEditModal(providers.find(provider => provider.id === runtime.provider_instance_id)!)"
          >
            <Settings2 :size="14" />
            {{ t('settings.editConfig') }}
          </UiButton>
          <UiButton
            variant="ghost"
            size="icon"
            class="provider-delete-btn"
            :disabled="modelCenterBusy"
            :title="t('settings.delete')"
            @click="deleteProvider(runtime.provider_instance_id)"
          >
            <Trash2 :size="14" />
          </UiButton>
        </div>
      </article>
    </div>
    <div v-if="(modelCenterOverview?.tui_runtimes.length ?? 0) === 0" class="center-empty-state">
      <Terminal :size="20" />
      <span>{{ t('settings.noTuiRuntimes') }}</span>
    </div>
  </section>

  <section v-if="modelCenterSection === 'api'" ref="modelProviderListRef" class="model-provider-section model-center-providers">
    <div class="model-provider-toolbar">
      <div class="model-provider-search">
        <Search :size="15" />
        <UiInput v-model="modelProviderQuery" :placeholder="t('settings.providerSearchPlaceholder')" :aria-label="t('settings.providerSearchPlaceholder')" />
      </div>
      <div class="model-provider-filters" role="group" :aria-label="t('settings.providerFilters')">
        <button :class="{ active: modelProviderFilter === 'all' }" :aria-pressed="modelProviderFilter === 'all'" @click="modelProviderFilter = 'all'">
          {{ t('settings.filterAll') }}
        </button>
        <button :class="{ active: modelProviderFilter === 'issues' }" :aria-pressed="modelProviderFilter === 'issues'" @click="modelProviderFilter = 'issues'">
          {{ t('settings.filterIssues') }}
          <span v-if="modelCenterIssueCount > 0">{{ modelCenterIssueCount }}</span>
        </button>
        <button :class="{ active: modelProviderFilter === 'configured' }" :aria-pressed="modelProviderFilter === 'configured'" @click="modelProviderFilter = 'configured'">
          {{ t('settings.filterConfigured') }}
        </button>
      </div>
      <span class="model-provider-count">{{ t('settings.providerResultCount', { visible: filteredModelCenterRows.length, total: modelCenterRows.length }) }}</span>
    </div>

    <div ref="providerWorkspaceEl" class="model-center-provider-workspace" :class="{ 'show-detail': providerDetailOpen || showModal }">
      <div
        ref="providerRailEl"
        class="model-center-provider-rail"
        role="list"
        :aria-label="t('settings.providerListLabel')"
        @keydown="onProviderRailKeydown"
      >
        <div
          v-for="row in filteredModelCenterRows"
          :key="row.key"
          role="listitem"
          class="model-provider-row model-center-provider-row"
          :class="{
            active: row.kind === 'instance' && selectedProviderDetail?.id === row.provider.id,
            issue: row.kind === 'instance' && ['blocked', 'warning'].includes(row.readiness?.status ?? '')
          }"
        >
          <button
            class="model-provider-identity"
            :data-provider-id="row.kind === 'instance' ? row.provider.id : undefined"
            :aria-current="row.kind === 'instance' && selectedProviderDetail?.id === row.provider.id ? 'true' : undefined"
            @click="row.kind === 'instance' ? selectProviderDetail(row.provider.id) : openAddModal(row.template)"
          >
            <span
              class="provider-brand-icon"
              :style="{ color: row.template?.brand_color, backgroundColor: row.template?.brand_bg }"
            >
              <span v-if="row.template?.icon" class="provider-brand-mark" v-html="row.template?.icon"></span>
              <Database v-else :size="16" />
            </span>
            <span>
              <strong :title="row.display_name">{{ row.display_name }}</strong>
              <small :title="row.driver">{{ row.kind === 'instance' ? row.driver : t('settings.notAdded') }}</small>
            </span>
          </button>
          <UiBadge v-if="row.kind === 'instance'" :variant="statusVariant(row.provider.status)">
            <Circle :size="8" />
            {{ statusLabel(row.provider.status) }}
          </UiBadge>
          <UiButton v-else variant="outline" size="icon" :title="t('settings.addProvider')" :aria-label="t('settings.addProvider')" @click="openAddModal(row.template)">
            <Plus :size="14" />
          </UiButton>
        </div>
        <div v-if="filteredModelCenterRows.length === 0" class="model-provider-empty">
          <Search :size="18" />
          <span>{{ t('settings.noProviderResults') }}</span>
          <UiButton variant="outline" size="sm" @click="handleAddProviderClick">
            <Plus :size="14" />
            {{ t('settings.addProvider') }}
          </UiButton>
        </div>
      </div>

      <div class="model-center-provider-detail">
        <UiButton variant="ghost" size="sm" class="model-center-provider-back" @click="closeProviderDetail()">
          <ChevronLeft :size="14" />
          <span>{{ t('settings.backToProviderList') }}</span>
        </UiButton>
        <form v-if="showModal" class="model-provider-form" @submit.prevent="saveProvider()">
          <UiCard>
            <template #header>
              <div class="modal-header-row">
                <div class="modal-header-left">
                  <span
                    class="modal-provider-logo"
                    :style="{ color: currentTemplate?.brand_color, backgroundColor: currentTemplate?.brand_bg }"
                  >
                    <span v-if="currentTemplate?.icon" class="provider-brand-mark" v-html="currentTemplate?.icon"></span>
                    <Database v-else :size="18" />
                  </span>
                  <div class="modal-header-info">
                    <h3>{{ providerForm.id ? t('settings.editProviderTitle') : t('settings.newProvider') }}</h3>
                    <span class="modal-header-sub">{{ currentTemplate ? t(currentTemplate.display_name_key) : providerForm.driver }}</span>
                  </div>
                </div>
                <UiButton type="button" variant="ghost" size="icon" :aria-label="t('common.close')" @click="requestCloseProviderModal()">
                  <X :size="16" />
                </UiButton>
              </div>
            </template>

            <template #content>
              <p v-if="currentTemplate" class="template-summary">{{ t(currentTemplate.summary_key) }}</p>

              <div class="modal-form-section">
                <div class="modal-form-section-title">{{ t('settings.basicInfo') }}</div>
                <div class="settings-field">
                  <UiLabel for="provider-display-name">{{ t('settings.displayName') }}</UiLabel>
                  <UiInput id="provider-display-name" v-model="providerForm.display_name" :aria-invalid="providerFormSubmitted && !!providerFormErrors.display_name" :aria-describedby="providerFormSubmitted && providerFormErrors.display_name ? 'provider-display-name-error' : undefined" />
                  <span v-if="providerFormSubmitted && providerFormErrors.display_name" id="provider-display-name-error" class="model-field-error" role="alert">{{ providerFormErrors.display_name }}</span>
                </div>
              </div>

              <div v-if="formFields.base_url || formFields.model || formProtocolOptions.length > 0" class="modal-form-section">
                <div class="modal-form-section-title">{{ t('settings.connectionParams') }}</div>
                <div class="model-form-grid">
                  <div v-if="formFields.base_url" class="settings-field">
                    <UiLabel for="provider-base-url">{{ t('settings.baseUrl') }}</UiLabel>
                    <UiInput id="provider-base-url" v-model="providerForm.base_url" :placeholder="formPlaceholders.base_url" :aria-invalid="providerFormSubmitted && !!providerFormErrors.base_url" :aria-describedby="providerFormSubmitted && providerFormErrors.base_url ? 'provider-base-url-error' : undefined" />
                    <span v-if="providerFormSubmitted && providerFormErrors.base_url" id="provider-base-url-error" class="model-field-error" role="alert">{{ providerFormErrors.base_url }}</span>
                  </div>
                  <div v-if="formFields.model" class="settings-field">
                    <UiLabel>{{ t('settings.modelLabel') }}</UiLabel>
                    <UiInput v-model="providerForm.model" :placeholder="formPlaceholders.model" />
                  </div>
                  <div v-if="formProtocolOptions.length > 0" class="settings-field">
                    <UiLabel>{{ t('settings.protocol') }}</UiLabel>
                    <UiSelectField
                      v-model="providerForm.protocol"
                      :aria-label="t('settings.protocol')"
                      :options="formProtocolOptions.map((option) => ({ value: option, label: protocolOptionLabel(option) }))"
                    />
                  </div>
                </div>
              </div>

              <div v-if="formFields.api_key" class="modal-form-section">
                <div class="modal-form-section-title">{{ t('settings.authentication') }}</div>
                <div class="settings-field">
                  <UiLabel for="provider-api-key">{{ t('settings.apiKey') }}</UiLabel>
                  <div class="model-key-row">
                    <UiInput
                      id="provider-api-key"
                      v-model="providerForm.api_key"
                      :type="showProviderKey ? 'text' : 'password'"
                      autocomplete="off"
                      :disabled="providerForm.clear_api_key"
                      :placeholder="selectedProvider?.has_api_key ? t('settings.apiKeyStored') : formPlaceholders.api_key ?? t('settings.apiKeyNotSet')"
                    />
                    <!-- Visibility applies to this input only. The stored key is never read back into it. -->
                    <UiButton
                      type="button"
                      variant="ghost"
                      size="icon"
                      :disabled="providerForm.clear_api_key"
                      :aria-label="showProviderKey ? t('settings.hideApiKey') : t('settings.showApiKey')"
                      :aria-pressed="showProviderKey"
                      @click="showProviderKey = !showProviderKey"
                    >
                      <EyeOff v-if="showProviderKey" :size="15" />
                      <Eye v-else :size="15" />
                    </UiButton>
                  </div>
                  <span v-if="selectedProvider?.has_api_key" class="quiet">{{ t('settings.apiKeyKeepHint') }}</span>
                </div>
                <!-- A stored key cannot be removed any other way: the field is empty by design, so
                     "leave it blank" has to mean "keep it". Without this the only exit was deleting
                     the whole provider. -->
                <label v-if="selectedProvider?.has_api_key" class="modal-clear-key">
                  <UiCheckbox v-model="providerForm.clear_api_key" :aria-label="t('settings.clearApiKey')" />
                  <span>
                    <strong>{{ t('settings.clearApiKey') }}</strong>
                    <em>{{ t('settings.clearApiKeyHint') }}</em>
                  </span>
                </label>
              </div>

              <div v-if="formFields.binary_path || formFields.home_path" class="modal-form-section">
                <div class="modal-form-section-title">{{ t('settings.localPaths') }}</div>
                <div class="model-form-grid">
                  <div v-if="formFields.binary_path" class="settings-field">
                    <UiLabel for="provider-binary-path">{{ t('settings.binaryPath') }}</UiLabel>
                    <UiInput id="provider-binary-path" v-model="providerForm.binary_path" :placeholder="formPlaceholders.binary_path" :aria-invalid="providerFormSubmitted && !!providerFormErrors.binary_path" :aria-describedby="providerFormSubmitted && providerFormErrors.binary_path ? 'provider-binary-path-error' : undefined" />
                    <span v-if="providerFormSubmitted && providerFormErrors.binary_path" id="provider-binary-path-error" class="model-field-error" role="alert">{{ providerFormErrors.binary_path }}</span>
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
                    <UiLabel for="provider-server-url">{{ t('settings.serverUrl') }}</UiLabel>
                    <UiInput id="provider-server-url" v-model="providerForm.server_url" :placeholder="formPlaceholders.server_url" :aria-invalid="providerFormSubmitted && !!providerFormErrors.server_url" :aria-describedby="providerFormSubmitted && providerFormErrors.server_url ? 'provider-server-url-error' : undefined" />
                    <span v-if="providerFormSubmitted && providerFormErrors.server_url" id="provider-server-url-error" class="model-field-error" role="alert">{{ providerFormErrors.server_url }}</span>
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

              <div v-if="selectedProvider && providerNote(selectedProvider)" class="model-provider-note">
                <Terminal :size="14" />
                <span>{{ selectedProvider.status_message }}</span>
              </div>
            </template>

            <template #footer>
              <div class="modal-actions model-provider-form-actions">
                <span v-if="providerFormDirty()" class="modal-unsaved" role="status">{{ t('settings.unsavedChanges') }}</span>
                <UiButton type="button" variant="outline" @click="requestCloseProviderModal()">
                  {{ t('settings.cancel') }}
                </UiButton>
                <UiButton type="submit" :disabled="modelCenterBusy">
                  <Save :size="14" />
                  <span>{{ t('settings.save') }}</span>
                </UiButton>
              </div>
            </template>
          </UiCard>
        </form>

        <div v-else-if="selectedProviderDetail" class="model-center-provider-card">
          <div class="provider-detail-head compact model-center-provider-head">
            <span
              class="provider-brand-icon"
              :style="{ color: providerPresentation(selectedProviderDetail.driver)?.brand_color, backgroundColor: providerPresentation(selectedProviderDetail.driver)?.brand_bg }"
            >
              <span v-if="providerPresentation(selectedProviderDetail.driver)?.icon" class="provider-brand-mark" v-html="providerPresentation(selectedProviderDetail.driver)?.icon"></span>
              <Database v-else :size="16" />
            </span>
            <div class="provider-detail-info">
              <strong>{{ selectedProviderDetail.display_name }}</strong>
              <span class="provider-detail-driver">{{ selectedProviderDetail.driver }} · {{ connectionKindLabel(selectedProviderDetail.connection_kind) }}</span>
            </div>
            <UiBadge :variant="statusVariant(selectedProviderDetail.status)">
              <Circle :size="8" />
              {{ statusLabel(selectedProviderDetail.status) }}
            </UiBadge>
          </div>
          <div class="provider-detail-grid compact">
            <div v-if="selectedProviderDetail.base_url" class="provider-detail-cell">
              <span class="provider-detail-label">{{ t('settings.baseUrl') }}</span>
              <span class="provider-detail-value provider-detail-mono">{{ selectedProviderDetail.base_url }}</span>
            </div>
            <div v-if="selectedProviderDetail.model" class="provider-detail-cell">
              <span class="provider-detail-label">{{ t('settings.modelLabel') }}</span>
              <span class="provider-detail-value provider-detail-mono">{{ selectedProviderDetail.model }}</span>
            </div>
            <div class="provider-detail-cell">
              <span class="provider-detail-label">{{ t('settings.apiKey') }}</span>
              <span class="provider-detail-value">
                <span :class="['provider-key-indicator', selectedProviderDetail.has_api_key ? 'has-key' : 'no-key']"></span>
                {{ selectedProviderDetail.has_api_key ? t('settings.apiKeyStored') : t('settings.apiKeyNotSet') }}
              </span>
            </div>
            <div class="provider-detail-cell">
              <span class="provider-detail-label">{{ t('settings.connectionKind') }}</span>
              <span class="provider-detail-value">{{ connectionKindLabel(selectedProviderDetail.connection_kind) }}</span>
            </div>
          </div>
          <div v-if="providerNote(selectedProviderDetail)" class="provider-status-note compact">
            <Terminal :size="14" />
            <span>{{ selectedProviderDetail.status_message }}</span>
          </div>
          <div class="provider-detail-actions compact">
            <UiButton variant="outline" size="sm" @click="openEditProviderById(selectedProviderDetail.id)">
              <Edit3 :size="14" />
              <span>{{ t('settings.editConfig') }}</span>
            </UiButton>
            <UiButton variant="outline" size="sm" :disabled="modelCenterBusy" @click="disableProviderById(selectedProviderDetail.id)">
              <component :is="selectedProviderDetail.enabled ? X : Check" :size="14" />
              <span>{{ selectedProviderDetail.enabled ? t('settings.disable') : t('settings.enable') }}</span>
            </UiButton>
            <UiButton variant="ghost" size="sm" class="provider-delete-btn" :disabled="modelCenterBusy" @click="deleteProvider(selectedProviderDetail.id)">
              <Trash2 :size="14" />
              <span>{{ t('settings.delete') }}</span>
            </UiButton>
          </div>

          <section class="model-center-provider-models" :aria-label="t('settings.centerModels')">
            <div class="model-center-provider-models-head">
              <div>
                <strong>{{ t('settings.centerModels') }}</strong>
                <span>{{ t('settings.providerModelsHint') }}</span>
              </div>
              <UiButton
                variant="outline"
                size="sm"
                :disabled="modelCenterBusy || !modelCenterOverview?.capabilities.model_discovery_refresh"
                :title="modelCenterOverview?.capabilities.model_discovery_refresh ? t('settings.addModel') : t('settings.modelDiscoveryUnsupported')"
                @click="openAddModelModal(selectedProviderDetail.id)"
              >
                <Plus :size="14" />
                {{ t('settings.addModel') }}
              </UiButton>
            </div>
            <div class="model-center-provider-model-list">
              <div v-for="model in modelsForProvider(selectedProviderDetail.id)" :key="model.id" class="center-resource-list-row model-group-model">
                <div class="center-resource-primary">
                  <Cpu :size="15" />
                  <div>
                    <strong :title="model.model_id">{{ model.model_id }}</strong>
                    <div class="center-resource-meta">
                      <span v-for="source in model.configuration_sources" :key="source">{{ configuredModelSourceLabel(source) }}</span>
                      <span v-for="purpose in model.route_purposes" :key="purpose">{{ purpose }}</span>
                    </div>
                    <div v-if="modelReferenceRows[referenceKey(selectedProviderDetail.id, model.model_id)]" class="model-reference-rows">
                      <span v-for="(reference, index) in modelReferenceRows[referenceKey(selectedProviderDetail.id, model.model_id)]" :key="index">
                        {{ referenceKindLabel(reference.reference_kind) }}<template v-if="reference.detail"> · {{ referenceDetailLabel(reference) }}</template>
                      </span>
                      <span v-if="modelReferenceRows[referenceKey(selectedProviderDetail.id, model.model_id)].length === 0" class="quiet">{{ t('settings.noModelReferences') }}</span>
                    </div>
                  </div>
                </div>
                <UiBadge :variant="statusVariant(model.status)">{{ statusLabel(model.status) }}</UiBadge>
                <div class="center-resource-actions">
                  <UiButton
                    variant="outline"
                    size="sm"
                    :disabled="modelCenterBusy"
                    :title="t('settings.setDefaultModel')"
                    @click="setDefaultChatModel(selectedProviderDetail.id, model.model_id)"
                  >
                    {{ t('settings.setDefaultModel') }}
                  </UiButton>
                  <UiButton
                    variant="outline"
                    size="sm"
                    :disabled="modelCenterBusy"
                    :aria-expanded="editingModelParameters?.providerId === selectedProviderDetail.id && editingModelParameters?.model === model.model_id"
                    @click="editingModelParameters = editingModelParameters?.providerId === selectedProviderDetail.id && editingModelParameters?.model === model.model_id ? null : { providerId: selectedProviderDetail.id, model: model.model_id }"
                  >
                    <Settings2 data-icon="inline-start" />
                    {{ t('settings.modelParameters') }}
                  </UiButton>
                  <UiButton
                    variant="ghost"
                    size="sm"
                    :disabled="modelReferenceBusy[referenceKey(selectedProviderDetail.id, model.model_id)]"
                    :title="t('settings.toggleModelReferences')"
                    @click="toggleModelReferences(selectedProviderDetail.id, model.model_id)"
                  >
                    <Info :size="14" />
                  </UiButton>
                  <UiButton
                    variant="ghost"
                    size="sm"
                    :disabled="modelCenterBusy"
                    :title="t('settings.removeModel')"
                    @click="removeModel(selectedProviderDetail.id, model.model_id)"
                  >
                    <Trash2 :size="14" />
                  </UiButton>
                </div>
                <ModelParametersEditor
                  v-if="selectedProviderInstance && editingModelParameters?.providerId === selectedProviderDetail.id && editingModelParameters?.model === model.model_id"
                  :provider="selectedProviderInstance"
                  :model="model.model_id"
                  @saved="modelParametersSaved"
                  @cancel="editingModelParameters = null"
                  @reload="loadModelCenter"
                />
              </div>
            </div>
            <div v-if="modelsForProvider(selectedProviderDetail.id).length === 0" class="center-empty-state">
              <Cpu :size="20" />
              <span>{{ t('settings.noProviderModels') }}</span>
            </div>
            <!-- A saved provider without models cannot answer; the next step belongs here, not in a toast. -->
            <div v-if="modelsForProvider(selectedProviderDetail.id).length === 0" class="provider-next-step" role="note">
              <Cpu :size="14" aria-hidden="true" />
              <span>{{ t('settings.providerNextStep') }}</span>
              <UiButton
                variant="outline"
                size="sm"
                :disabled="modelCenterBusy || !modelCenterOverview?.capabilities.model_discovery_refresh"
                @click="openAddModelModal(selectedProviderDetail.id)"
              >
                <Plus :size="14" />
                {{ t('settings.addModel') }}
              </UiButton>
            </div>
          </section>
        </div>

        <div v-else class="center-empty-state">
          <Database :size="20" />
          <span>{{ t('settings.noProviderSelected') }}</span>
        </div>
      </div>
    </div>
  </section>
  </main>
  </div>
  </div>
<!-- Capability receipts, health metrics and developer diagnostics: one command away, so the
     first screen can stay about the work rather than the instrumentation. -->
<DialogRoot :open="showDiagnostics" @update:open="(open) => { if (!open) showDiagnostics = false }">
  <DialogPortal>
    <DialogOverlay class="model-diagnostics-overlay" />
    <DialogContent class="model-diagnostics-dialog">
      <header class="model-diagnostics-header">
        <div>
          <DialogTitle class="model-diagnostics-title">{{ t('settings.advancedDiagnostics') }}</DialogTitle>
          <DialogDescription class="model-diagnostics-description">{{ t('settings.diagnosticsDescription') }}</DialogDescription>
        </div>
        <DialogClose as-child>
          <UiButton variant="ghost" size="icon" :aria-label="t('common.close')">
            <X :size="16" />
          </UiButton>
        </DialogClose>
      </header>

      <section class="center-overview-receipt" :aria-label="t('settings.centerOverview')">
        <div class="center-receipt-item" :class="{ ready: modelCenterOverview?.capabilities.provider_crud }">
          <Database :size="17" />
          <div>
            <span>{{ t('settings.modelProviderManagement') }}</span>
            <strong>{{ t('settings.modelProviderManagementHint') }}</strong>
          </div>
          <UiBadge :variant="modelCenterOverview?.capabilities.provider_crud ? 'default' : 'secondary'">
            {{ modelCenterOverview?.capabilities.provider_crud ? t('settings.writable') : t('settings.readOnly') }}
          </UiBadge>
        </div>
        <div class="center-receipt-item configured">
          <Cpu :size="17" />
          <div>
            <span>{{ t('settings.modelCatalogScope') }}</span>
            <strong>{{ t('settings.configuredModelsOnly') }}</strong>
          </div>
          <UiBadge variant="outline">{{ modelCatalogModeLabel(modelCenterOverview?.capabilities.model_catalog_mode) }}</UiBadge>
        </div>
        <div class="center-receipt-item" :class="{ ready: modelCenterOverview?.capabilities.live_model_discovery, unavailable: !modelCenterOverview?.capabilities.live_model_discovery }">
          <Search :size="17" />
          <div>
            <span>{{ t('settings.liveDiscovery') }}</span>
            <strong>{{ modelCenterOverview?.capabilities.live_model_discovery ? t('settings.available') : t('settings.pendingCore') }}</strong>
          </div>
          <UiBadge :variant="modelCenterOverview?.capabilities.live_model_discovery ? 'default' : 'secondary'">
            {{ modelCenterOverview?.capabilities.live_model_discovery ? t('settings.available') : t('settings.unavailable') }}
          </UiBadge>
        </div>
      </section>

      <section v-if="modelReadiness || modelCatalogReadiness" class="model-health-overview">
        <div class="model-health-head">
          <div>
            <h3>{{ t('settings.modelHealth') }}</h3>
            <span>{{ t('settings.modelHealthHint') }}</span>
          </div>
          <UiBadge v-if="modelReadiness" :variant="readinessVariant(modelReadiness.status)">
            <Circle :size="8" />
            {{ readinessStatusLabel(modelReadiness.status) }}
          </UiBadge>
        </div>
        <div class="model-health-metrics">
          <div>
            <span>{{ t('settings.readyProvidersMetric') }}</span>
            <strong>{{ modelReadiness ? `${modelReadiness.ready_provider_count}/${modelReadiness.provider_count}` : '—' }}</strong>
          </div>
          <div :class="{ attention: (modelReadiness?.blocked_route_count ?? 0) > 0 }">
            <span>{{ t('settings.blockedRoutesMetric') }}</span>
            <strong>{{ modelReadiness?.blocked_route_count ?? '—' }}</strong>
          </div>
          <div>
            <span>{{ t('settings.readyTemplatesMetric') }}</span>
            <strong>{{ modelCatalogReadiness ? `${modelCatalogReadiness.ready_template_count}/${modelCatalogReadiness.template_count}` : '—' }}</strong>
          </div>
          <div>
            <span>{{ t('settings.runtimeModulesMetric') }}</span>
            <strong>{{ modelCatalogReadiness?.runtime_module_count ?? '—' }}</strong>
          </div>
        </div>
      </section>

      <div v-if="modelReadiness || modelCatalogReadiness" class="model-diagnostics-grid">
        <section v-if="modelReadiness" class="model-diagnostic-section">
          <div class="model-diagnostic-head">
            <div>
              <strong>{{ t('settings.providerReceipt') }}</strong>
              <span>{{ modelReadiness.receipt_id }}</span>
            </div>
            <UiBadge :variant="readinessVariant(modelReadiness.status)">{{ readinessStatusLabel(modelReadiness.status) }}</UiBadge>
          </div>
          <p class="model-diagnostic-meta">{{ t('settings.generatedAt') }} · {{ modelReadiness.generated_at }}</p>
          <div class="model-diagnostic-list">
            <strong>{{ t('settings.blockedRoutes') }}</strong>
            <div v-if="blockedModelRoutes.length > 0" class="model-readiness-routes">
              <span v-for="route in blockedModelRoutes" :key="route.purpose">
                {{ route.purpose }} · {{ route.provider_display_name ?? route.provider_instance_id }}
              </span>
            </div>
            <span v-else class="quiet">{{ t('settings.noBlockedRoutes') }}</span>
          </div>
          <ul v-if="modelReadinessNotes.length > 0" class="model-diagnostic-notes">
            <li v-for="note in modelReadinessNotes" :key="note">{{ note }}</li>
          </ul>
        </section>
        <section v-if="modelCatalogReadiness" class="model-diagnostic-section">
          <div class="model-diagnostic-head">
            <div>
              <strong>{{ t('settings.catalogReceipt') }}</strong>
              <span>{{ modelCatalogReadiness.receipt_id }}</span>
            </div>
            <UiBadge :variant="readinessVariant(modelCatalogReadiness.status)">{{ readinessStatusLabel(modelCatalogReadiness.status) }}</UiBadge>
          </div>
          <p class="model-diagnostic-meta">{{ t('settings.generatedAt') }} · {{ modelCatalogReadiness.generated_at }}</p>
          <div class="model-diagnostic-list">
            <strong>{{ t('settings.catalogWarnings') }}</strong>
            <div v-if="warningCatalogTemplates.length > 0" class="catalog-readiness-rows">
              <div v-for="template in warningCatalogTemplates" :key="template.driver" class="catalog-readiness-row">
                <div>
                  <strong>{{ template.display_name }}</strong>
                  <span>{{ template.runtime_module_family }} · {{ template.live_discovery_policy }}</span>
                </div>
                <UiBadge :variant="readinessVariant(template.status)">{{ template.runtime_module_status }}</UiBadge>
              </div>
            </div>
            <span v-else class="quiet">{{ t('settings.noCatalogWarnings') }}</span>
          </div>
          <ul v-if="readinessReceipt?.status === 'blocked'" class="model-diagnostic-notes">
            <li>{{ t('settings.readinessBlockedHint') }}</li>
          </ul>
        </section>
      </div>
    </DialogContent>
  </DialogPortal>
</DialogRoot>

<DialogRoot :open="showModelModal" @update:open="(open) => { if (!open) closeModelModal() }">
  <DialogPortal>
    <DialogOverlay class="model-provider-modal-overlay" />
    <DialogContent class="model-provider-modal model-provider-dialog">
    <UiCard class="model-provider-modal-content">
      <template #header>
        <div class="modal-header-row">
          <div class="modal-header-left">
            <span
              class="modal-provider-logo"
              :style="{ color: providerPresentation(modelApiProvider(modelModalProviderId)?.driver ?? '')?.brand_color, backgroundColor: providerPresentation(modelApiProvider(modelModalProviderId)?.driver ?? '')?.brand_bg }"
            >
              <span v-if="providerPresentation(modelApiProvider(modelModalProviderId)?.driver ?? '')?.icon" class="provider-brand-mark" v-html="providerPresentation(modelApiProvider(modelModalProviderId)?.driver ?? '')?.icon"></span>
              <Cpu v-else :size="18" />
            </span>
            <div class="modal-header-info">
              <DialogTitle as-child><h3>{{ t('settings.addModelTitle') }}</h3></DialogTitle>
              <DialogDescription as-child><span class="modal-header-sub">{{ modelApiProvider(modelModalProviderId)?.display_name ?? modelModalProviderId }}</span></DialogDescription>
            </div>
          </div>
          <DialogClose as-child>
            <UiButton variant="ghost" size="icon" :aria-label="t('common.close')">
              <X :size="16" />
            </UiButton>
          </DialogClose>
        </div>
      </template>

      <template #content>
        <div class="modal-form-section">
          <div class="modal-form-section-title">{{ t('settings.fetchModelsFromProvider') }}</div>
          <div class="model-modal-fetch-row">
            <UiButton
              variant="outline"
              size="sm"
              :disabled="modelModalBusy"
              @click="fetchDiscoveredModels"
            >
              <Server :size="14" />
              {{ modelModalBusy ? t('settings.fetchingModels') : (modelModalFetched ? t('settings.refreshModels') : t('settings.fetchModels')) }}
            </UiButton>
          </div>
          <div v-if="modelModalError" class="model-provider-note">
            <Terminal :size="14" />
            <span>{{ modelModalError }}</span>
          </div>
        </div>

        <div class="modal-form-section">
          <div class="modal-form-section-title">{{ t('settings.manualModel') }}</div>
          <div class="model-manual-row">
            <UiInput v-model="modelModalManual" :placeholder="t('settings.modelNamePlaceholder')" @keydown.enter="addManualModel" />
            <UiButton variant="outline" :disabled="!modelModalManual.trim()" @click="addManualModel">
              <Plus :size="14" />
              {{ t('settings.addModel') }}
            </UiButton>
          </div>
        </div>

        <!-- One list for saved, discovered and hand-typed models: saved rows stay ticked and fixed,
             so "add" means "include in the save", never "add it again". -->
        <div class="modal-form-section">
          <div class="model-modal-search">
            <Search :size="14" aria-hidden="true" />
            <UiInput v-model="modelModalQuery" :placeholder="t('settings.modelSearchPlaceholder')" :aria-label="t('settings.modelSearchPlaceholder')" />
          </div>
          <div v-if="modelModalVisibleRows.length" class="model-discovered-list">
            <label v-for="row in modelModalVisibleRows" :key="row.id" class="model-discovered-row" :class="{ saved: row.saved }">
              <UiCheckbox
                :model-value="row.saved || modelModalSelected.includes(row.id)"
                :disabled="row.saved"
                :aria-label="row.id"
                @update:model-value="toggleModelSelection(row.id)"
              />
              <strong>{{ row.id }}</strong>
              <span v-if="row.label !== row.id" class="quiet model-discovered-alias">{{ row.label }}</span>
              <span v-if="row.saved" class="provider-cap-tag">{{ t('settings.modelAdded') }}</span>
            </label>
          </div>
          <div v-else class="center-empty-state">
            <Cpu :size="20" />
            <span>{{ modelModalQuery.trim()
              ? t('settings.modelFilterEmpty')
              : modelModalFetched ? t('settings.fetchModelsEmpty') : t('settings.noProviderModels') }}</span>
          </div>
        </div>
      </template>

      <template #footer>
        <div class="modal-actions">
          <UiButton variant="outline" @click="closeModelModal">
            {{ t('settings.cancel') }}
          </UiButton>
          <UiButton :disabled="modelModalBusy || !modelModalSelected.length" @click="saveModelModal()">
            <Save :size="14" />
            <span>{{ t('settings.save') }}</span>
          </UiButton>
        </div>
      </template>
    </UiCard>
    </DialogContent>
  </DialogPortal>
</DialogRoot>

<DialogRoot :open="showTemplatePicker" @update:open="(open) => { if (!open) showTemplatePicker = false }">
  <DialogPortal>
    <DialogOverlay class="model-provider-modal-overlay" />
    <DialogContent class="model-provider-modal template-picker-dialog">
    <UiCard class="model-provider-modal-content template-picker-content">
      <template #header>
        <div class="modal-header-row">
          <div class="modal-header-left">
            <span class="modal-provider-logo" :style="{ color: '#8b949e', backgroundColor: 'rgba(139,148,158,0.10)' }">
              <Plus :size="18" />
            </span>
            <div class="modal-header-info">
              <DialogTitle as-child><h3>{{ t('settings.addProviderTemplate') }}</h3></DialogTitle>
              <DialogDescription as-child><span class="modal-header-sub">{{ t('settings.templatePickerHint') }}</span></DialogDescription>
            </div>
          </div>
          <DialogClose as-child>
            <UiButton variant="ghost" size="icon" :aria-label="t('common.close')">
              <X :size="16" />
            </UiButton>
          </DialogClose>
        </div>
      </template>

      <template #content>
        <div class="template-picker-search">
          <Search :size="15" />
          <UiInput v-model="templatePickerQuery" :placeholder="t('settings.templateSearchPlaceholder')" />
        </div>

        <div v-if="pickerTemplateGroups.length === 0" class="template-picker-empty">
          <Server :size="20" />
          <span>{{ t('settings.templatePickerEmpty') }}</span>
        </div>

        <div v-for="group in pickerTemplateGroups" :key="group.category" class="template-picker-group">
          <div class="template-picker-group-title">{{ t(group.labelKey) }}</div>
          <div class="template-picker-grid">
            <button
              v-for="template in group.templates"
              :key="template.driver"
              class="template-picker-card"
              :style="{ '--template-accent': template.brand_color }"
              @click="pickTemplate(template)"
            >
              <span class="provider-brand-icon" :style="{ color: template.brand_color, backgroundColor: template.brand_bg }">
                <span v-if="template.icon" class="provider-brand-mark" v-html="template.icon"></span>
                <Database v-else :size="16" />
              </span>
              <span class="template-picker-card-body">
                <strong :title="t(template.display_name_key)">{{ t(template.display_name_key) }}</strong>
                <small :title="template.driver">{{ template.driver }}</small>
              </span>
              <span class="template-picker-card-meta">
                <span>{{ connectionKindLabel(template.connection_kind) }}</span>
                <span v-if="template.default_model" :title="template.default_model">{{ template.default_model }}</span>
              </span>
            </button>
          </div>
        </div>
      </template>
    </UiCard>
    </DialogContent>
  </DialogPortal>
</DialogRoot>
</template>
