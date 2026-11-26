<script setup lang="ts">
import { ArrowUp, ChevronDown, FileText, Folder, FolderOpen, FolderPlus, Image, Layers, Plus, Settings, Sparkles, Square, TriangleAlert, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { useStatusLabels } from '@/lib/statusLabels'
import { errorCodeMessage } from '@/lib/apiError'
import { TransitionGroup, ref, shallowRef, computed, watch, onMounted, onUnmounted, nextTick, useId, type Component, type Ref } from 'vue'

const ComposerTransitionGroup = {
  ...(TransitionGroup as object),
  name: 'ComposerTransitionGroup',
} as unknown as typeof TransitionGroup
import { useRouter } from 'vue-router'
import { UiButton, UiSelectField } from '@/components/ui'
import ComposerCommandPanel from './ComposerCommandPanel.vue'
import ImageLightbox, { type LightboxImageItem } from './chat/ImageLightbox.vue'
import { Attachment, AttachmentAction, AttachmentActions, AttachmentContent, AttachmentDescription, AttachmentGroup, AttachmentMedia, AttachmentTitle } from '@/components/ui/attachment'
import { Spinner } from '@/components/ui/spinner'
import { useDefaultModel } from '@/composables/useDefaultModel'
import { useAgentModels, type ModelChoice } from '@/composables/useAgentModels'
import { requestSettingsSection } from '@/lib/pageRequests'
import type { PermissionLevel } from '@/types/mode'
import type { MeetingModelOverrideDto, ModelProviderInstanceDto, ProjectDto, SpaceOptionsDto } from '@/api'
import { homeController } from '@/controllers/HomeController'
import { selectionKey, selectedStorage } from '@/lib/storageScope'
import { getDispatchPref, type DispatchPref } from '@/lib/dispatchPref'
import { isCommandAvailable, parseSlashCommand, type AppCommand, type CommandHost } from '@/lib/appCommands'
import { toggleSessionPartner as togglePartnerWindow } from '@/lib/sessionPartner'
import { composerSlashQuery, composerSettings, permissionChoices, type ComposerPage } from '@/lib/composerCommands'
import { contextAdvancedEqual, copySpaceOptions, defaultSpaceOptions, spaceOptionsEqual } from '@/lib/composerSettings'
import { completeMentionToken, filterMentionEntries, parseMentionToken, type MentionToken } from '@/lib/fileMentions'
import {
  attachFiles,
  attachmentDisplayState,
  fileNameParts,
  formatAttachmentBytes,
  MAX_ATTACHMENT_BYTES,
  pendingAttachments,
  pendingAttachmentCount,
  reconcileSession,
  removePendingAttachment,
  TOO_LARGE_CODE,
  type AttachableFile,
  type PendingAttachment,
} from '@/lib/pendingAttachments'
import { api } from '@/api'
import { toDirEntryView, type DirEntryDto, type DirEntryView } from '@/lib/workspaceSearch'
import { computeDropdownPlacement, type DropdownPlacement } from '@/lib/dropdownPlacement'
import { useHostAccess } from '@/lib/hostAccess'
import { usePeerDevices } from '@/composables/usePeerDevices'
import { isConnectionLevelError, useNotifications } from '@/composables/useNotifications'

const COMPOSER_SEND_KEY = 'composer-send'
const COMPOSER_SETTINGS_KEY = 'composer-settings'
const COMPOSER_ATTACHMENT_KEY = 'composer-attachment'

const { t, te } = useI18n()
const { canAccessBackend } = useHostAccess()
const { isActivePeerOfflineReadonly } = usePeerDevices()
const { notify, dismissByKey } = useNotifications()
const { runStatusLabel } = useStatusLabels()
const router = useRouter()

const props = defineProps<{
  /** Hero (start page) variant: centered box, welcome-send dispatch, no queued cards. */
  hero?: boolean
  spatial?: boolean
  /** The send itself is still in flight (Core has not answered yet). Never "a run is working". */
  busy: boolean
  modelValue: string
  permission: PermissionLevel
  projects?: ProjectDto[]
  selectedProjectId?: string | null
  sessionId?: string | null
  runs?: Array<{ id: string; status: string }>
  modeVersionId?: string | null
  meetingModelOverride?: MeetingModelOverrideDto | null
  spaceOptions?: SpaceOptionsDto | null
  settingsSaving?: boolean
  settingsError?: string | null
  panelStyle?: Record<string, string>
  panelDataAttrs?: Record<string, string>
  /**
   * True while a run can still be cancelled. The composer only showed a spinner,
   * so "the agent is doing something I no longer want" had no answer in the one
   * place the user was already looking.
   */
  canStop?: boolean
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  'update:permission': [value: PermissionLevel]
  'update:modeVersionId': [value: string | null]
  'update:meetingModelOverride': [value: MeetingModelOverrideDto | null]
  'update:spaceOptions': [value: SpaceOptionsDto]
  'select-mode': [payload: { mode_version_id: string | null; space_options: SpaceOptionsDto }]
  'submit': [payload: { dispatch_mode: 'parallel' | 'queued' | 'insert'; target_run_id?: string | null; mode_version_id?: string | null; meeting_model_override?: MeetingModelOverrideDto | null; space_options?: SpaceOptionsDto | null }]
  'welcome-submit': [payload: { content: string; permission_mode: PermissionLevel; mode_version_id: string | null; meeting_model_override?: MeetingModelOverrideDto | null; space_options?: SpaceOptionsDto | null }]
  'create-project': []
  'select-project': [id: string | null]
  'stop': []
}>()

const textareaRef = ref<HTMLTextAreaElement | null>(null)
const composerBoxRef = ref<HTMLElement | null>(null)
const commandPanelRef = ref<InstanceType<typeof ComposerCommandPanel> | null>(null)
const commandPage = ref<ComposerPage>('root')
const modeLabel = ref(t('commandPanel.mode'))
const modeGlyph = shallowRef<Component>(Layers)
const modeUnavailable = ref(false)
// Sessions created by older clients may carry a legacy permission spelling (for
// example "ask"). Keep the actual permission untouched, but never let the toolbar
// dereference an undefined choice while rendering that session.
const permissionChoice = computed(() => permissionChoices.find(choice => choice.value === props.permission) ?? permissionChoices[0])
const permissionLabel = computed(() => t(permissionChoice.value.label))
const activeModeDefaults = ref<SpaceOptionsDto | null>(null)
const hasActiveMode = ref(false)
const hasSpaceOverrides = computed(() => {
  if (!props.spaceOptions) return false
  if (props.modeVersionId && !hasActiveMode.value) return false
  return !spaceOptionsEqual(props.spaceOptions, activeModeDefaults.value ?? defaultSpaceOptions())
})
const enabledSpaceLabels = computed(() => {
  if (props.spatial) {
    return [
      ...composerSettings.filter(setting => 'toggle' in setting && props.spaceOptions?.[setting.toggle]).map(setting => t(setting.label)),
      ...(props.spaceOptions?.context_strategy || props.spaceOptions?.context_advanced ? [t('commandPanel.contextStrategy')] : []),
      ...(props.spaceOptions?.workflow_mode_version_id ? [t('commandPanel.workflow')] : []),
    ]
  }
  if (!hasSpaceOverrides.value) return []
  const baseline = activeModeDefaults.value ?? defaultSpaceOptions()
  const changed = composerSettings
    .filter(setting => 'toggle' in setting && Boolean(props.spaceOptions?.[setting.toggle]) !== Boolean(baseline[setting.toggle]))
    .map(setting => t(setting.label))
  const contextChanged = (props.spaceOptions?.context_strategy ?? null) !== (baseline.context_strategy ?? null)
    || !contextAdvancedEqual(props.spaceOptions?.context_advanced, baseline.context_advanced)
  if (contextChanged) changed.push(t('commandPanel.contextStrategy'))
  return [t('commandPanel.temporaryOverrides'), ...changed]
})
const plusTriggerRef = ref<HTMLElement | null>(null)
const fileInputRef = ref<HTMLInputElement | null>(null)
const sendTriggerRef = ref<HTMLElement | null>(null)
const showPlusMenu = ref(false)
const showAskMenu = ref(false)
const askMenuStyle = ref<DropdownPlacement>({ position: 'fixed', left: '0px' })
const projectTriggerRef = ref<HTMLElement | null>(null)
const projectMenuRef = ref<HTMLDivElement | null>(null)
const projectMenuId = `composer-project-${useId()}`
const showProjectDropdown = ref(false)
const projectDropdownStyle = ref<DropdownPlacement>({ position: 'fixed', left: '0px' })
const projectPortalStyle = computed(() => {
  const { overflowY: _overflow, maxHeight, ...placement } = projectDropdownStyle.value
  // One native list owns scrolling; the portal only supplies the available
  // height to its flex layout, rather than creating a second scrollport.
  return { ...placement, '--project-menu-max-height': maxHeight ?? '320px' }
})
const attachmentPreparing = ref(false)
/**
 * Files picked while the draft session is still being created. They have no row yet, so
 * they show as idle attachments until the upload starts (or the pick is abandoned).
 */
const preparingFiles = ref<Array<{ key: string; name: string; size: number; type: string }>>([])
let disposed = false

const queued = homeController.queuedMessages
const activeRuns = homeController.activeRuns
// 发送失败归口灵动通知，不在输入框上方滞留野生错误条。
const invokeError = computed(() => (homeController.invokeError as unknown as { value: string | null } | undefined)?.value ?? null)
let sendErrorDraftSnapshot: string | null = null

function clearControllerInvokeError() {
  const refLike = homeController.invokeError as unknown as { value: string | null } | undefined
  if (refLike && typeof refLike === 'object' && 'value' in refLike) {
    refLike.value = null
  }
}

function clearComposerErrorNotifications() {
  sendErrorDraftSnapshot = null
  clearControllerInvokeError()
  dismissByKey(COMPOSER_SEND_KEY)
  dismissByKey(COMPOSER_SETTINGS_KEY)
  dismissByKey(COMPOSER_ATTACHMENT_KEY)
}

function isOfflineSendError(message: string): boolean {
  if (!canAccessBackend.value) return true
  if (message === t('home.sendError.offline') || message === t('home.sendError.recovering')) return true
  return isConnectionLevelError(message)
}

watch(
  invokeError,
  (errText) => {
    if (!errText) {
      sendErrorDraftSnapshot = null
      dismissByKey(COMPOSER_SEND_KEY)
      return
    }
    sendErrorDraftSnapshot = props.modelValue
    notify.error({
      key: COMPOSER_SEND_KEY,
      message: errText,
      ...(isOfflineSendError(errText) ? { category: 'connection' } : {}),
    })
  },
  { immediate: true },
)

watch(
  () => props.settingsError,
  (errText) => {
    if (!errText) {
      dismissByKey(COMPOSER_SETTINGS_KEY)
      return
    }
    notify.error({
      key: COMPOSER_SETTINGS_KEY,
      message: errText,
      ...(!canAccessBackend.value || isConnectionLevelError(errText) ? { category: 'connection' } : {}),
    })
  },
  { immediate: true },
)
const steeringId = ref<string | null>(null)
const steerTarget = ref('')

/**
 * The composer's half of `CommandHost`: the parts of running a command that are
 * local to this component - the textarea's height, the mode props, the stop emit
 * chain. What a command *does* is no longer decided here, because the palette has to
 * run the same commands from a place that owns none of these.
 */
const commandHost: CommandHost = {
  canStop: () => Boolean(props.canStop),
  draft: () => props.modelValue,
  setDraft: (value) => updateDraft(value),
  send: (text, dispatch) => {
    // One-shot dispatch override: the persisted Enter preference the send menu owns
    // is left alone. The draft is written through the controller rather than by
    // awaiting two layers of emit, because sendMessage reads it right after.
    updateDraft(text)
    resetTextareaHeight()
    void homeController.sendMessage({
      dispatch_mode: dispatch,
      target_run_id: null,
      mode_version_id: props.modeVersionId ?? null,
      meeting_model_override: props.meetingModelOverride ?? null,
      ...(props.spaceOptions ? { space_options: copySpaceOptions(props.spaceOptions) } : {}),
    })
  },
  // Stop keeps going out over the emit chain instead of calling homeController.stopRun()
  // here, because that chain is the live path from ChatCard and shortening only one of
  // its two callers is how the two would drift.
  stopRun: () => emit('stop'),
  newSession: () => {
    resetTextareaHeight()
    void homeController.createSession(props.selectedProjectId ?? null)
  },
  navigate: (routeName) => {
    void router.push({ name: routeName })
  },
  routeName: () => String(router.currentRoute.value.name ?? ''),
  toggleSessionPartner: () => { void togglePartnerWindow(homeController.selectedSessionId.value) },
}

const commandsDismissed = ref(false)
const slashQuery = computed(() => commandsDismissed.value ? null : composerSlashQuery(props.modelValue))
const commandPanelOpen = computed(() => showPlusMenu.value || slashQuery.value !== null)
const modePanelExpanded = computed(() => commandPanelOpen.value && commandPanelRef.value?.page === 'mode')
const permissionPanelExpanded = computed(() => commandPanelOpen.value && commandPanelRef.value?.page === 'permission')
const modelPanelExpanded = computed(() => commandPanelOpen.value && ['model', 'model-agents', 'model-agent'].includes(commandPanelRef.value?.page ?? ''))
const defaultModel = useDefaultModel()
const agentModels = useAgentModels({
  // Older sessions carry no frozen identity; every shipped mode answers through `meeting`.
  conversationSlug: computed(() => homeController.currentSession.value?.conversation_template_slug || 'meeting'),
  modeVersionId: computed(() => props.modeVersionId ?? null),
})
/**
 * The model the agent that answers the user runs on: the session's pin, else the agent's own
 * model, else the default model. The toolbar names it and the reasoning effort applies to it.
 */
const conversationChoice = computed<ModelChoice | null>(() => {
  const pin = props.meetingModelOverride
  if (pin) return { provider_instance_id: pin.provider_instance_id, model: pin.model ?? null }
  const agent = agentModels.conversationAgent.value
  const own = agent ? agentModels.effectiveChoice(agent) : null
  if (own) return own
  const current = defaultModel.current.value
  return current ? { provider_instance_id: current.provider_instance_id, model: current.model ?? null } : null
})
watch(conversationChoice, (choice) => { defaultModel.reasoningTarget.value = choice }, { immediate: true })
/** A model being saved holds the next send, so the task does not resolve the model it replaced. */
const modelSaving = computed(() => agentModels.busy.value || defaultModel.busy.value)
// Unknown reads as the neutral row name, never as "no default model".
// A chosen reasoning effort rides along ("gpt-5 · High"); the model default adds nothing.
const defaultModelLabel = computed(() => {
  if (defaultModel.name.value === undefined) return t('commandPanel.model')
  const choice = conversationChoice.value
  const name = choice ? choice.model || defaultModel.providerName(choice.provider_instance_id) || t('composer.pinnedModelUnknown') : null
  if (!name) return t('composer.modelSwitch.none')
  const effort = defaultModel.reasoningSupported.value ? defaultModel.reasoningEffort.value : ''
  return effort ? `${name} · ${t(`settings.reasoningTick.${effort}`)}` : name
})
function manageModels() {
  requestSettingsSection('model')
  if (router.currentRoute.value.name !== 'settings') void router.push({ name: 'settings' })
}
watch(() => props.modelValue, (value, previous) => {
  if (value !== previous && (sendErrorDraftSnapshot === null || value !== sendErrorDraftSnapshot)) {
    sendErrorDraftSnapshot = null
    clearControllerInvokeError()
    dismissByKey(COMPOSER_SEND_KEY)
    dismissByKey(COMPOSER_SETTINGS_KEY)
  }
  commandsDismissed.value = false
  if (!showPlusMenu.value && composerSlashQuery(value) !== null) commandPage.value = 'root'
  void refreshMentions()
})

function acceptCommand(command: AppCommand) {
  updateDraft(`/${command.slash}${command.needsArgument ? ' ' : ''}`)
  commandsDismissed.value = true
  void nextTick(() => textareaRef.value?.focus())
}

function runCommand(command: AppCommand, argument: string) {
  if (!isCommandAvailable(command, commandHost) || props.settingsSaving) return
  commandsDismissed.value = true
  showPlusMenu.value = false
  command.run(commandHost, argument)
}

function chooseCommand(command: AppCommand) {
  if (command.needsArgument) {
    const parsed = parseSlashCommand(props.modelValue)
    if (parsed.kind === 'run' && parsed.command.id === command.id) runCommand(command, parsed.argument)
    else acceptCommand(command)
    showPlusMenu.value = false
  } else runCommand(command, '')
}

function closeCommandPanel() {
  showPlusMenu.value = false
  commandsDismissed.value = true
  void nextTick(() => textareaRef.value?.focus())
}

function openCommandPanel(page: ComposerPage = 'root') {
  if (commandPanelOpen.value && commandPanelRef.value?.page === page) {
    closeCommandPanel()
    return
  }
  commandPage.value = page
  commandsDismissed.value = true
  showPlusMenu.value = true
  commandPanelRef.value?.selectPage(page)
}

function consumeSettingsSlash() {
  if (composerSlashQuery(props.modelValue) !== null && !commandsDismissed.value) {
    updateDraft(props.modelValue.replace(/^\/\S*[ \t]?/u, ''))
  }
}

function selectModePreset(payload: { mode_version_id: string | null; space_options: SpaceOptionsDto }) {
  consumeSettingsSlash()
  emit('update:modeVersionId', payload.mode_version_id)
  emit('select-mode', payload)
}
function changePermission(value: PermissionLevel) { consumeSettingsSlash(); emit('update:permission', value) }
function changeModel(value: MeetingModelOverrideDto | null) { consumeSettingsSlash(); emit('update:meetingModelOverride', value) }

/*
 * The composer no longer picks a model: the conversation model is the model center's chat
 * route. A conversation pinned to a model earlier keeps that choice, so it is shown here —
 * it must never override the model center unseen — and one click returns it to the default.
 * Its provider is checked in the current storage scope: deleted, disabled, or never present
 * in this workspace's own models.toml, it is named unavailable (Core already runs the
 * default route then) rather than shown as if it were in use.
 */
const providerCatalog = ref<ModelProviderInstanceDto[] | null>(null)
let providerCatalogRead = 0
let lastProviderStorage = selectedStorage.value
let lastPinnedProviderId = props.meetingModelOverride?.provider_instance_id
watch(
  () => [
    props.meetingModelOverride?.provider_instance_id,
    conversationChoice.value?.provider_instance_id,
    defaultModel.loaded.value,
    defaultModel.busy.value,
    selectedStorage.value,
  ] as const,
  async ([pinnedId, choiceProviderId, defaultLoaded, defaultBusy, storage]) => {
    const read = ++providerCatalogRead
    if (pinnedId !== lastPinnedProviderId || storage !== lastProviderStorage) {
      providerCatalog.value = null
    }
    lastPinnedProviderId = pinnedId
    lastProviderStorage = storage
    if (!pinnedId && (!choiceProviderId && !defaultLoaded || defaultBusy)) return
    try {
      const rows = await api.listModelProviders()
      if (read === providerCatalogRead) providerCatalog.value = Array.isArray(rows) ? rows : []
    } catch {
      /* unknown: keep pinned neutral and avoid false vision warnings while offline */
      if (read === providerCatalogRead && pinnedId) providerCatalog.value = null
    }
  },
  { immediate: true },
)
const pinnedUnavailable = computed(() => {
  const pinned = props.meetingModelOverride
  if (!pinned || !providerCatalog.value) return false
  const provider = providerCatalog.value.find(row => row.id === pinned.provider_instance_id)
  return !provider || !provider.enabled
})
const pinnedName = computed(() => {
  const pinned = props.meetingModelOverride
  if (!pinned) return ''
  return pinned.model || providerCatalog.value?.find(row => row.id === pinned.provider_instance_id)?.display_name || t('composer.pinnedModelUnknown')
})

function supportsVisionForChoice(
  provider: ModelProviderInstanceDto | null | undefined,
  modelId: string | null | undefined,
): boolean | null {
  if (!provider) return null
  const resolvedModel = modelId?.trim() || provider.model?.trim() || ''
  const profile = resolvedModel ? provider.model_profiles?.[resolvedModel] : undefined
  const profileVision = Array.isArray(profile?.specialties)
    ? profile!.specialties.includes('vision')
    : null

  const imageInput = provider.image_input
  let protocolImageEnabled: boolean | null = null
  if (imageInput) {
    const override = resolvedModel ? imageInput.model_overrides?.[resolvedModel]?.enabled : undefined
    if (imageInput.source === 'protocol_unsupported') protocolImageEnabled = false
    else if (typeof override === 'boolean') protocolImageEnabled = override
    else if (typeof imageInput.base_enabled === 'boolean' && resolvedModel && resolvedModel !== provider.model) protocolImageEnabled = imageInput.base_enabled
    else protocolImageEnabled = Boolean(imageInput.enabled)
  }

  if (protocolImageEnabled === false || profileVision === false) return false
  if (protocolImageEnabled === true || profileVision === true) return true
  return null
}

function isRasterImageType(mediaType: string): boolean {
  return mediaType.startsWith('image/') && mediaType !== 'image/svg+xml'
}

const activeModelProvider = computed(() => {
  const choice = conversationChoice.value
  if (!choice || !providerCatalog.value) return null
  return providerCatalog.value.find(row => row.id === choice.provider_instance_id) ?? null
})
const visionSupported = computed(() =>
  supportsVisionForChoice(activeModelProvider.value, conversationChoice.value?.model ?? null),
)

function insertCommandTemplate(template: string) {
  showPlusMenu.value = false
  commandsDismissed.value = true
  const current = props.modelValue
  const hasSlash = composerSlashQuery(current) !== null
  const nextDraft = hasSlash || !current.trim()
    ? template
    : `${current.replace(/\s+$/, '')}\n\n${template}`
  updateDraft(nextDraft)
  void nextTick(() => {
    const el = textareaRef.value
    if (!el) return
    el.focus()
    const argToken = '$ARGUMENTS'
    const argIdx = nextDraft.indexOf(argToken)
    if (argIdx >= 0) {
      el.setSelectionRange(argIdx, argIdx + argToken.length)
      caretPos.value = argIdx
    } else {
      el.setSelectionRange(nextDraft.length, nextDraft.length)
      caretPos.value = nextDraft.length
    }
    autoResize()
  })
}
function changeSpaceOptions(value: SpaceOptionsDto) { consumeSettingsSlash(); emit('update:spaceOptions', value) }

function updateDraft(value: string) {
  sendErrorDraftSnapshot = null
  clearControllerInvokeError()
  dismissByKey(COMPOSER_SEND_KEY)
  dismissByKey(COMPOSER_SETTINGS_KEY)
  homeController.updateDraft(value)
  emit('update:modelValue', value)
}

/** True when Enter was consumed by a command, so the plain send path must not run. */
function handleCommandEnter(): boolean {
  const parsed = parseSlashCommand(props.modelValue)
  if (parsed.kind === 'run') {
    runCommand(parsed.command, parsed.argument)
    return true
  }
  if (parsed.kind === 'incomplete') {
    acceptCommand(parsed.command)
    return true
  }
  if (composerSlashQuery(props.modelValue) !== null) {
    // Unknown command-like text stays a draft until the explicit send-as-text action.
    if (!commandPanelOpen.value || showPlusMenu.value) { commandPage.value = 'root'; showPlusMenu.value = false; commandsDismissed.value = false }
    else commandPanelRef.value?.activate()
    return true
  }
  return false
}

const selectedProject = computed(() =>
  props.projects?.find((p) => p.id === props.selectedProjectId && (!p.storage_id || p.storage_id === selectedStorage.value)) ?? null
)

/**
 * `@` path completion. It walks the workspace one directory at a time because `ls`
 * is the only listing the tool manifest offers; there is no filename index, so this
 * is not fuzzy whole-workspace search and does not pretend to be.
 */
const caretPos = ref(0)
const mentionToken = ref<MentionToken | null>(null)
const mentionEntries = ref<DirEntryView[]>([])
const mentionDirectory = ref<string | null>(null)
const mentionIndex = ref(0)
const mentionsDismissed = ref(false)
const mentionMenuStyle = ref<DropdownPlacement>({ position: 'fixed', left: '0px' })
let mentionSeq = 0

const mentionSuggestions = computed(() =>
  mentionToken.value ? filterMentionEntries(mentionEntries.value, mentionToken.value.query) : [],
)

watch(mentionSuggestions, async (list) => {
  if (!list.length) return
  await nextTick()
  placeMenu(textareaRef.value, mentionMenuStyle, { minWidth: 240, estimatedHeight: 116 })
})

async function refreshMentions() {
  const cwd = selectedProject.value?.path
  const caret = caretPos.value || props.modelValue.length
  const token = cwd && !mentionsDismissed.value
    ? parseMentionToken(props.modelValue, caret)
    : null
  mentionToken.value = token
  if (!token) {
    mentionEntries.value = []
    mentionDirectory.value = null
    // Dismissing only lasts for the current fragment; the next `@` is a new request.
    mentionsDismissed.value = false
    return
  }
  mentionIndex.value = 0
  if (token.directory === mentionDirectory.value) return
  mentionDirectory.value = token.directory
  const seq = ++mentionSeq
  try {
    const result = await api.listDirectory(cwd!, token.directory.replace(/\/$/, '') || '.')
    const data = result.data as { entries?: DirEntryDto[] }
    if (seq !== mentionSeq) return
    mentionEntries.value = (Array.isArray(data?.entries) ? data.entries : [])
      .map(toDirEntryView)
      .filter((entry): entry is DirEntryView => entry !== null && !entry.name.startsWith('.'))
  } catch {
    if (seq !== mentionSeq) return
    mentionEntries.value = []
  }
}

function acceptMention(entry: DirEntryView) {
  const token = mentionToken.value
  if (!token) return
  const caret = caretPos.value || props.modelValue.length
  const completed = completeMentionToken(props.modelValue, caret, token, entry)
  updateDraft(completed.text)
  caretPos.value = completed.caret
  if (!entry.isDir) mentionsDismissed.value = true
  void nextTick(() => {
    const el = textareaRef.value
    if (!el) return
    el.focus()
    el.setSelectionRange(completed.caret, completed.caret)
    autoResize()
  })
}

function onDraftInput(event: Event) {
  const el = event.target as HTMLTextAreaElement
  caretPos.value = el.selectionStart ?? el.value.length
  sendErrorDraftSnapshot = null
  clearControllerInvokeError()
  dismissByKey(COMPOSER_SEND_KEY)
  dismissByKey(COMPOSER_SETTINGS_KEY)
  emit('update:modelValue', el.value)
  autoResize()
}

/** Moving the caret with the mouse or the arrow keys is also a completion request. */
function onCaretMove(event: Event) {
  const el = event.target as HTMLTextAreaElement
  caretPos.value = el.selectionStart ?? caretPos.value
  void refreshMentions()
}

function autoResize() {
  const el = textareaRef.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = Math.min(el.scrollHeight, 200) + 'px'
}

function resetTextareaHeight() {
  const el = textareaRef.value
  if (!el) return
  el.style.height = 'auto'
}

function placeMenu(
  trigger: HTMLElement | null,
  target: Ref<DropdownPlacement>,
  options?: { minWidth?: number; estimatedHeight?: number },
) {
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  target.value = computeDropdownPlacement(rect, window.innerWidth, window.innerHeight, {
    minWidth: options?.minWidth ?? 130,
    estimatedHeight: options?.estimatedHeight ?? 90,
  })
}

async function togglePlusMenu() {
  if (commandPanelOpen.value) closeCommandPanel()
  else openCommandPanel()
}

const attachments = pendingAttachments
// The native picker opens within the user gesture. If necessary, its selection
// creates a correctly typed draft session before bytes are uploaded.
const canAttach = computed(
  () => !attachmentPreparing.value && !props.settingsSaving && !isActivePeerOfflineReadonly.value,
)
/**
 * A file with no words is a turn of its own: Core appends it to the transcript and starts no
 * run. So Send unlocks on either half. Only *ready* chips count - an upload still in flight
 * names no stored row, and sending early would drop the file.
 */
const canSend = computed(
  () => !isActivePeerOfflineReadonly.value && (Boolean(props.modelValue.trim()) || pendingAttachmentCount() > 0),
)

function openFilePicker(accept: string) {
  closeCommandPanel()
  if (!canAttach.value) return
  const input = fileInputRef.value
  if (!input) return
  // Cleared first: picking the same file twice in a row would otherwise fire no change
  // event, because the input still holds the previous selection.
  input.value = ''
  input.accept = accept
  input.click()
}

function pickedFiles(event: Event): AttachableFile[] {
  const input = event.target as HTMLInputElement
  return Array.from(input.files ?? [])
}

async function enqueueAttachableFiles(files: readonly AttachableFile[]) {
  if (!files.length || !canAttach.value) return
  const projectId = props.selectedProjectId
  const spatial = props.spatial
  dismissByKey(COMPOSER_ATTACHMENT_KEY)
  attachmentPreparing.value = true
  if (!props.sessionId) {
    preparingFiles.value = files.map((file, index) => ({
      key: `${index}:${file.name}`,
      name: file.name,
      size: file.size,
      type: file.type,
    }))
  }
  try {
    const sessionId = props.sessionId ?? null
    if (disposed || projectId !== props.selectedProjectId || spatial !== props.spatial) return
    // Attaching never creates a session. Without one the files stay staged and the
    // send path creates the session, uploads them, then submits the message.
    await attachFiles(files, sessionId, { projectId })
  } catch (error) {
    if (!disposed) {
      notify.error(error, {
        key: COMPOSER_ATTACHMENT_KEY,
        title: t('commandPanel.attachmentError'),
      })
    }
  } finally {
    attachmentPreparing.value = false
    preparingFiles.value = []
  }
}

async function onFilesPicked(event: Event) {
  await enqueueAttachableFiles(pickedFiles(event))
}

let pastedImageSeq = 0
const dragDepth = ref(0)
const isDraggingFiles = computed(() => dragDepth.value > 0)

function extensionForImageMime(mime: string): string {
  const normalized = mime.toLowerCase().split(';')[0]!.trim()
  if (normalized === 'image/jpeg') return 'jpg'
  if (normalized === 'image/svg+xml') return 'svg'
  if (normalized === 'image/webp') return 'webp'
  if (normalized === 'image/gif') return 'gif'
  if (normalized === 'image/bmp') return 'bmp'
  return 'png'
}

function normalizeImageFile(file: File, index: number): AttachableFile {
  const trimmed = file.name?.trim()
  if (trimmed && trimmed !== 'image.png' && trimmed !== 'blob') return file
  const ext = extensionForImageMime(file.type || 'image/png')
  const seq = ++pastedImageSeq
  const name = trimmed && trimmed !== 'blob'
    ? trimmed.replace(/(\.[^.]+)?$/, `-${seq}.${ext}`)
    : `pasted-image-${seq}${index > 0 ? `-${index + 1}` : ''}.${ext}`
  if (typeof File === 'function' && file instanceof Blob) {
    return new File([file], name, { type: file.type || `image/${ext}`, lastModified: file.lastModified || Date.now() })
  }
  return { name, type: file.type || `image/${ext}`, size: file.size, arrayBuffer: () => file.arrayBuffer() }
}

function extractClipboardImages(clipboard: DataTransfer | null | undefined): AttachableFile[] {
  if (!clipboard) return []
  const images: AttachableFile[] = []
  const seen = new Set<File>()
  if (clipboard.items?.length) {
    for (let i = 0; i < clipboard.items.length; i++) {
      const item = clipboard.items[i]
      if (!item || item.kind !== 'file' || !item.type.startsWith('image/')) continue
      const file = item.getAsFile()
      if (!file || seen.has(file)) continue
      seen.add(file)
      images.push(normalizeImageFile(file, images.length))
    }
  }
  if (!images.length && clipboard.files?.length) {
    for (const file of Array.from(clipboard.files)) {
      if (!file.type.startsWith('image/') || seen.has(file)) continue
      seen.add(file)
      images.push(normalizeImageFile(file, images.length))
    }
  }
  return images
}

function onTextareaPaste(event: ClipboardEvent) {
  const images = extractClipboardImages(event.clipboardData)
  if (!images.length) return
  event.preventDefault()
  void enqueueAttachableFiles(images)
}

function hasDraggedFiles(event: DragEvent): boolean {
  const dt = event.dataTransfer
  if (!dt) return false
  if (dt.files?.length) return true
  if (dt.items?.length && Array.from(dt.items).some(item => item.kind === 'file')) return true
  return Array.from(dt.types ?? []).includes('Files')
}

function onComposerDragEnter(event: DragEvent) {
  if (!hasDraggedFiles(event)) return
  event.preventDefault()
  dragDepth.value += 1
}

function onComposerDragOver(event: DragEvent) {
  if (!hasDraggedFiles(event)) return
  event.preventDefault()
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'
}

function onComposerDragLeave(event: DragEvent) {
  if (dragDepth.value === 0) return
  event.preventDefault()
  dragDepth.value = Math.max(0, dragDepth.value - 1)
}

function onComposerDrop(event: DragEvent) {
  dragDepth.value = 0
  if (!hasDraggedFiles(event)) return
  event.preventDefault()
  const dt = event.dataTransfer
  if (!dt) return
  const picked: File[] = []
  if (dt.files?.length) {
    picked.push(...Array.from(dt.files))
  } else if (dt.items?.length) {
    for (const item of Array.from(dt.items)) {
      if (item.kind !== 'file') continue
      const file = item.getAsFile()
      if (file) picked.push(file)
    }
  }
  if (picked.length) void enqueueAttachableFiles(picked)
}

const hasQueuedImages = computed(() =>
  attachments.value.some((item) => item.status !== 'failed' && isRasterImageType(item.mediaType))
  || preparingFiles.value.some((file) => isRasterImageType(file.type)),
)
const showVisionNotice = computed(() => hasQueuedImages.value && visionSupported.value === false)

const composerLightboxOpen = ref(false)
const composerLightboxIndex = ref(0)
const composerLightboxImages = computed<LightboxImageItem[]>(() =>
  attachments.value
    .filter((item): item is PendingAttachment & { previewUrl: string } => Boolean(item.previewUrl))
    .map((item) => ({ src: item.previewUrl, name: item.fileName })),
)

function openComposerLightbox(clientId: string) {
  const previewable = attachments.value.filter((item) => Boolean(item.previewUrl))
  const idx = previewable.findIndex((item) => item.clientId === clientId)
  if (idx < 0) return
  composerLightboxIndex.value = idx
  composerLightboxOpen.value = true
}

function dropAttachment(clientId: string) {
  void removePendingAttachment(clientId)
}

function attachmentHint(item: PendingAttachment): string {
  if (item.status === 'uploading') return t('composer.attachUploading')
  if (item.status === 'ready') return t('composer.attachReady')
  if (item.errorCode === TOO_LARGE_CODE) {
    return t('composer.attachTooLarge', { max: formatAttachmentBytes(MAX_ATTACHMENT_BYTES) })
  }
  if (item.errorCode === 'backend_network_unavailable' || item.errorCode === 'backend_write_outcome_unknown') return t('home.sendError.offline')
  // A code with wording reads as a sentence; an unknown one stays visible for a bug report.
  return errorCodeMessage(item.errorCode, undefined, { t, te })
    ?? (item.errorCode ? t('composer.attachFailed', { code: item.errorCode }) : t('composer.attachFailedUnknown'))
}

// Chips describe rows in the session the composer was attached to. immediate covers
// a remount onto a different session, where no change is observed from inside this
// component's lifetime.
watch(
  () => [props.sessionId, props.selectedProjectId] as const,
  ([id, projectId]) => { reconcileSession(id ?? null, projectId ?? null) },
  { immediate: true },
)

async function toggleAskMenu() {
  showAskMenu.value = !showAskMenu.value
  if (showAskMenu.value) {
    await nextTick()
    placeMenu(sendTriggerRef.value, askMenuStyle, { minWidth: 96, estimatedHeight: 76 })
  }
}

async function toggleProjectDropdown() {
  showProjectDropdown.value = !showProjectDropdown.value
  if (showProjectDropdown.value) {
    await nextTick()
    if (!showProjectDropdown.value || disposed) return
    placeMenu(projectTriggerRef.value, projectDropdownStyle, { minWidth: 220, estimatedHeight: 260 })
    await nextTick()
    if (!showProjectDropdown.value || disposed) return
    const selected = projectMenuRef.value?.querySelector<HTMLButtonElement>('.project-dropdown-item.active')
      ?? projectMenuRef.value?.querySelector<HTMLButtonElement>('.project-dropdown-item')
    if (selected) focusProjectOption(selected)
  }
}

function focusProjectOption(button: HTMLButtonElement) {
  button.focus({ preventScroll: true })
  button.scrollIntoView?.({ block: 'nearest', inline: 'nearest', behavior: 'auto' })
}

function closeProjectDropdown() {
  showProjectDropdown.value = false
  projectTriggerRef.value?.focus({ preventScroll: true })
}

function projectMenuKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    closeProjectDropdown()
    return
  }
  if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return
  const options = Array.from(projectMenuRef.value?.querySelectorAll<HTMLButtonElement>('.project-dropdown-item:not(:disabled)') ?? [])
  if (!options.length) return
  const index = options.indexOf(document.activeElement as HTMLButtonElement)
  const next = event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1
    : (index + (event.key === 'ArrowDown' ? 1 : -1) + options.length) % options.length
  event.preventDefault()
  focusProjectOption(options[next]!)
}

function projectMenuFocusout(event: FocusEvent) {
  if (event.relatedTarget && event.relatedTarget !== projectTriggerRef.value
    && !projectMenuRef.value?.contains(event.relatedTarget as Node)) showProjectDropdown.value = false
}

function selectProject(id: string | null) {
  closeProjectDropdown()
  emit('select-project', id)
}

function openNewProject() {
  closeProjectDropdown()
  emit('create-project')
}

function handleClickOutside(event: MouseEvent) {
  const target = event.target as HTMLElement
  if (!target.closest('.composer-send-wrapper') && !target.closest('.ask-menu')) {
    showAskMenu.value = false
  }
  if (!target.closest('.project-dropdown-trigger') && !target.closest('.project-dropdown-portal')) {
    showProjectDropdown.value = false
  }
}

watch(canAccessBackend, (ready, wasReady) => {
  if (ready && !wasReady) {
    clearComposerErrorNotifications()
    void defaultModel.load()
    void agentModels.load()
  }
})

onMounted(() => {
  void defaultModel.load()
  void agentModels.load()
  document.addEventListener('click', handleClickOutside)
  // A draft can arrive pre-filled (edit-and-resend, a restored session), and the
  // modelValue watcher does not fire for a value that was never changed.
  void refreshMentions()
})

onUnmounted(() => {
  disposed = true
  document.removeEventListener('click', handleClickOutside)
  dismissByKey(COMPOSER_SEND_KEY)
  dismissByKey(COMPOSER_SETTINGS_KEY)
  dismissByKey(COMPOSER_ATTACHMENT_KEY)
})

function submit(pref?: DispatchPref, asText = false) {
  if (isActivePeerOfflineReadonly.value || props.settingsSaving || modelSaving.value || attachmentPreparing.value) return
  if (!asText && handleCommandEnter()) return
  const content = props.modelValue.trim()
  if (!content && !canSend.value) return
  if (commandPanelOpen.value) closeCommandPanel()
  if (props.hero) {
    // Start-page send: full welcome payload, no dispatch menu.
    clearComposerErrorNotifications()
    resetTextareaHeight()
    emit('welcome-submit', {
      content,
      permission_mode: props.permission,
      mode_version_id: props.modeVersionId ?? null,
      meeting_model_override: props.meetingModelOverride ?? null,
      ...(props.spaceOptions ? { space_options: copySpaceOptions(props.spaceOptions) } : {}),
    })
    return
  }
  const p = pref ?? getDispatchPref()
  if (p === 'ask') {
    void toggleAskMenu()
    return
  }
  showAskMenu.value = false
  clearComposerErrorNotifications()
  resetTextareaHeight()
  emit('submit', {
    dispatch_mode: p,
    target_run_id: null,
    mode_version_id: props.modeVersionId ?? null,
    meeting_model_override: props.meetingModelOverride ?? null,
    ...(props.spaceOptions ? { space_options: { ...props.spaceOptions } } : {}),
  })
}

function handleKeydown(event: KeyboardEvent) {
  if (event.isComposing || event.keyCode === 229) return
  const mentions = mentionSuggestions.value
  if (mentions.length) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      const delta = event.key === 'ArrowDown' ? 1 : -1
      mentionIndex.value = (mentionIndex.value + delta + mentions.length) % mentions.length
      return
    }
    if (event.key === 'Escape') {
      event.preventDefault()
      mentionsDismissed.value = true
      mentionToken.value = null
      return
    }
    if (event.key === 'Tab' || (event.key === 'Enter' && !event.shiftKey)) {
      // Enter completes while the list is open; it does not send a half-typed path.
      event.preventDefault()
      acceptMention(mentions[mentionIndex.value] ?? mentions[0])
      return
    }
  }
  if (event.key !== 'Enter' && commandPanelOpen.value && commandPanelRef.value?.keydown(event)) return
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault()
    submit()
  }
}

// With one active run there is nothing to choose, so steering goes straight to it; the choice
// between steering at the next step and interrupting now is still the user's.
function startSteer(id: string, interrupt = false) {
  const list = activeRuns.value
  if (list.length === 1) {
    void homeController.steerQueued(id, list[0].id, interrupt)
    return
  }
  steeringId.value = id
  steerTarget.value = ''
}

function confirmSteer(id: string, interrupt = false) {
  if (!steerTarget.value) return
  void homeController.steerQueued(id, steerTarget.value, interrupt)
  steeringId.value = null
}
</script>

<template>
  <div class="composer" :class="{ 'composer--hero': hero }">
    <div v-if="meetingModelOverride || enabledSpaceLabels.length" class="composer-settings-summary">
      <button
        v-if="meetingModelOverride"
        type="button"
        class="composer-pinned-model"
        :class="{ 'is-unavailable': pinnedUnavailable }"
        :title="t(pinnedUnavailable ? 'composer.pinnedModelUnavailableHint' : 'composer.pinnedModelHint')"
        :disabled="settingsSaving"
        data-testid="composer-pinned-model"
        @click="changeModel(null)"
      >{{ t(pinnedUnavailable ? 'composer.pinnedModelUnavailable' : 'composer.pinnedModel', { model: pinnedName }) }}</button>
      <button v-for="label in enabledSpaceLabels.slice(0, 2)" :key="label" @click="openCommandPanel()">{{ label }}</button>
      <button v-if="enabledSpaceLabels.length > 2" @click="openCommandPanel()">+{{ enabledSpaceLabels.length - 2 }}</button>
    </div>
    <div
      ref="composerBoxRef"
      class="composer-box welcome-dialog"
      :data-composer-active="modelValue.trim() ? 'true' : 'false'"
      :data-dragging="isDraggingFiles ? 'true' : 'false'"
      :style="panelStyle"
      v-bind="panelDataAttrs"
      @dragenter="onComposerDragEnter"
      @dragover="onComposerDragOver"
      @dragleave="onComposerDragLeave"
      @drop="onComposerDrop"
    >
      <div v-if="isDraggingFiles" class="composer-drop-hint" data-testid="composer-drop-hint">
        {{ t('composer.dropFilesHint') }}
      </div>
      <!-- queued cards sit inside dialog top when items are queued -->
      <div v-if="!hero && !spatial && queued.length" class="composer-queued">
        <ComposerTransitionGroup name="ui-pop">
          <div v-for="item in queued" :key="item.id" class="queued-card">
            <div class="queued-content">{{ item.content }}</div>
            <div class="queued-actions">
              <template v-if="steeringId === item.id">
                <UiSelectField
                  v-model="steerTarget"
                  class="queued-run-select"
                  :aria-label="t('composer.selectTargetRun')"
                  :options="[
                    { value: '', label: t('composer.selectTargetRun'), disabled: true },
                    ...activeRuns.map((r) => ({ value: r.id, label: `${r.id.slice(0,8)} · ${runStatusLabel(r.status)}` })),
                  ]"
                />
                <button class="queued-action" :disabled="!steerTarget" @click="confirmSteer(item.id)">{{ t('composer.confirmSteer') }}</button>
                <button class="queued-action" :disabled="!steerTarget" :title="t('composer.interruptSteerHint')" @click="confirmSteer(item.id, true)">{{ t('composer.interruptSteer') }}</button>
              </template>
              <template v-else>
                <button class="queued-action" @click="startSteer(item.id)">{{ t('composer.steer') }}</button>
                <button class="queued-action" :title="t('composer.interruptSteerHint')" @click="startSteer(item.id, true)">{{ t('composer.interruptSteer') }}</button>
                <button class="queued-action" @click="homeController.promoteQueued(item.id)">{{ t('composer.parallel') }}</button>
                <button class="queued-action" @click="homeController.editQueued(item.id)">{{ t('composer.edit') }}</button>
                <button class="queued-action" :aria-label="t('composer.dismiss')" @click="homeController.dismissQueued(item.id)">×</button>
              </template>
            </div>
          </div>
        </ComposerTransitionGroup>
      </div>

      <!-- The attachment primitive's five states: idle while the draft session is being made,
           uploading (an image previews locally, dimmed), done, or error with the reason in words. -->
      <AttachmentGroup v-if="attachments.length || preparingFiles.length" class="composer-attachments" role="list" aria-live="polite" :aria-label="t('composer.attachments')">
        <ComposerTransitionGroup name="ui-pop">
          <Attachment
            v-for="pick in preparingFiles"
            :key="pick.key"
            state="idle"
            size="xs"
            class="attachment-chip is-idle"
            role="listitem"
            data-testid="attachment-chip"
          >
            <AttachmentMedia><component :is="pick.type.startsWith('image/') ? Image : FileText" aria-hidden="true" /></AttachmentMedia>
            <AttachmentContent class="attachment-content">
              <AttachmentTitle class="attachment-name"><span class="attachment-stem">{{ fileNameParts(pick.name).stem }}</span>{{ fileNameParts(pick.name).extension }}</AttachmentTitle>
              <AttachmentDescription><span class="attachment-meta">{{ formatAttachmentBytes(pick.size) }}</span> <span class="attachment-state">{{ t('composer.attachPreparing') }}</span></AttachmentDescription>
            </AttachmentContent>
          </Attachment>
          <Attachment
            v-for="item in attachments"
            :key="item.clientId"
            :state="attachmentDisplayState(item.status)"
            size="xs"
            class="attachment-chip"
            :class="`is-${item.status}`"
            role="listitem"
            :title="attachmentHint(item)"
            data-testid="attachment-chip"
          >
            <AttachmentMedia :variant="item.previewUrl ? 'image' : 'icon'">
              <img v-if="item.previewUrl" :src="item.previewUrl" :alt="item.fileName" />
              <component :is="item.mediaType.startsWith('image/') ? Image : FileText" v-else aria-hidden="true" />
              <button
                v-if="item.previewUrl"
                type="button"
                class="attachment-preview-trigger"
                :aria-label="t('composer.previewAttachment', { name: item.fileName })"
                :title="t('composer.previewAttachment', { name: item.fileName })"
                data-testid="attachment-preview"
                @click.stop="openComposerLightbox(item.clientId)"
              />
              <Spinner v-if="item.status === 'uploading'" decorative class="attachment-spinner" />
            </AttachmentMedia>
            <AttachmentContent class="attachment-content">
              <AttachmentTitle class="attachment-name"><span class="attachment-stem">{{ fileNameParts(item.fileName).stem }}</span>{{ fileNameParts(item.fileName).extension }}</AttachmentTitle>
              <AttachmentDescription>
                <span class="attachment-meta">{{ formatAttachmentBytes(item.size) }}</span>
                <span v-if="item.status !== 'ready'" class="attachment-state"> {{ attachmentHint(item) }}</span>
              </AttachmentDescription>
            </AttachmentContent>
            <AttachmentActions>
              <AttachmentAction class="attachment-remove" :aria-label="t('composer.removeAttachment', { name: item.fileName })" @click="dropAttachment(item.clientId)">
                <X aria-hidden="true" />
              </AttachmentAction>
            </AttachmentActions>
          </Attachment>
        </ComposerTransitionGroup>
      </AttachmentGroup>

      <div
        v-if="showVisionNotice"
        class="composer-vision-notice"
        role="status"
        aria-live="polite"
        data-testid="composer-vision-notice"
      >
        <TriangleAlert :size="13" aria-hidden="true" />
        <span>{{ t('composer.visionUnsupportedNotice') }}</span>
      </div>

      <div class="welcome-dialog-main">
        <div class="welcome-dialog-plus-wrapper">
          <button
            ref="plusTriggerRef"
            class="welcome-dialog-plus"
            :disabled="isActivePeerOfflineReadonly"
            :title="isActivePeerOfflineReadonly ? t('peers.shadowBanner.composerDisabledHint') : undefined"
            :aria-label="t('composer.commands')"
            aria-haspopup="dialog"
            :aria-expanded="commandPanelOpen"
            @click="togglePlusMenu"
          >
            <Plus :size="15" />
          </button>
          <input
            ref="fileInputRef"
            class="composer-file-input"
            type="file"
            multiple
            data-testid="composer-file-input"
            :aria-label="t('composer.attachFile')"
            @change="onFilesPicked"
          />
        </div>

        <Teleport v-if="mentionSuggestions.length" to="body">
          <ul
            class="composer-commands-portal composer-mentions-portal"
            :style="mentionMenuStyle"
            data-testid="composer-mentions"
            role="listbox"
            :aria-label="t('composer.mentions')"
          >
            <li
              v-for="(entry, index) in mentionSuggestions"
              :key="entry.name"
              role="option"
              :aria-selected="index === mentionIndex"
              :class="{ 'is-active': index === mentionIndex }"
              :data-testid="`composer-mention-${entry.name}`"
              @mouseenter="mentionIndex = index"
              @mousedown.prevent="acceptMention(entry)"
            >
              <component :is="entry.isDir ? Folder : FileText" :size="12" class="composer-mention-icon" />
              <code class="composer-command-syntax">{{ entry.name }}{{ entry.isDir ? '/' : '' }}</code>
              <span v-if="mentionToken?.directory" class="composer-command-hint">{{ mentionToken.directory }}</span>
            </li>
          </ul>
        </Teleport>

        <textarea
          ref="textareaRef"
          :value="modelValue"
          class="welcome-dialog-input"
          :disabled="isActivePeerOfflineReadonly"
          :title="isActivePeerOfflineReadonly ? t('peers.shadowBanner.composerDisabledHint') : undefined"
          :placeholder="isActivePeerOfflineReadonly ? t('peers.shadowBanner.composerDisabledPlaceholder') : t('chat.whatToDo')"
          rows="1"
          @input="onDraftInput"
          @click="onCaretMove"
          @paste="onTextareaPaste"
          @keydown="handleKeydown"
        />

        <div ref="sendTriggerRef" class="composer-send-wrapper">
          <UiButton
            v-if="canStop && (!spatial || !canSend)"
            variant="ghost"
            size="icon"
            class="composer-stop-button"
            data-testid="composer-stop"
            :aria-label="t('chat.stopRun')"
            :title="t('chat.stopRun')"
            @click="emit('stop')"
          >
            <Square :size="11" fill="currentColor" />
          </UiButton>
          <UiButton
            v-if="!spatial || !(canStop && !canSend)"
            variant="ghost"
            size="icon"
            class="welcome-dialog-send"
            data-testid="composer-send"
            :disabled="!canSend || isActivePeerOfflineReadonly || settingsSaving || modelSaving || attachmentPreparing"
            :title="isActivePeerOfflineReadonly ? t('peers.shadowBanner.composerDisabledHint') : undefined"
            :aria-label="isActivePeerOfflineReadonly ? t('peers.shadowBanner.composerDisabledHint') : (canSend ? t('chat.send') : t('chat.nothingToSend'))"
            @click="submit()"
          >
            <span v-if="busy" class="composer-send-spinner" role="status" :aria-label="t('chat.sending')" />
            <ArrowUp v-else :size="15" :stroke-width="2.25" />
          </UiButton>
        </div>
      </div>

      <div v-if="!spatial || hero" class="welcome-dialog-toolbar">
        <div class="toolbar-left">
          <!-- THE one mode selector: the installed packs' published versions. -->
          <button
            v-if="!spatial"
            class="mode-selector-trigger"
            type="button"
            :title="t('commandPanel.mode')"
            aria-haspopup="dialog"
            :aria-expanded="modePanelExpanded"
            @click="openCommandPanel('mode')"
          ><component :is="modeGlyph" :size="14" /><span class="mode-selector-label">{{ modeLabel }}</span><span v-if="modeUnavailable" class="mode-selector-stale" :title="t('chat.modeUnavailable')">⚠</span><ChevronDown :size="12" class="mode-selector-chevron" :class="{ 'is-expanded': modePanelExpanded }" aria-hidden="true" /></button>
          <slot v-if="spatial" name="capabilities" />
          <button class="permission-selector-trigger" type="button" :title="t('permission.nextRunHint')" aria-haspopup="dialog" :aria-expanded="permissionPanelExpanded" @click="openCommandPanel('permission')"><component :is="permissionChoice.icon" :size="14" class="composer-permission-icon" :data-risk="permissionChoice.risk" aria-hidden="true" /><span class="permission-selector-label">{{ permissionLabel }}</span><ChevronDown :size="12" class="permission-selector-chevron" :class="{ 'is-expanded': permissionPanelExpanded }" aria-hidden="true" /></button>
          <button class="permission-selector-trigger model-selector-trigger" type="button" :title="t('commandPanel.modelHint')" aria-haspopup="dialog" :aria-expanded="modelPanelExpanded" data-testid="composer-model-trigger" @click="openCommandPanel('model')"><Sparkles :size="14" aria-hidden="true" /><span class="permission-selector-label">{{ defaultModelLabel }}</span><ChevronDown :size="12" class="permission-selector-chevron" :class="{ 'is-expanded': modelPanelExpanded }" aria-hidden="true" /></button>
          <button
            ref="projectTriggerRef"
            class="project-dropdown-trigger"
            type="button"
            aria-haspopup="dialog"
            :aria-expanded="showProjectDropdown"
            :aria-controls="projectMenuId"
            @click="toggleProjectDropdown"
            @keydown.down.prevent="showProjectDropdown ? undefined : toggleProjectDropdown()"
            @keydown.up.prevent="showProjectDropdown ? undefined : toggleProjectDropdown()"
          >
            <FolderOpen :size="12" />
            <span class="project-dropdown-label">
              {{ selectedProject?.name ?? t('chat.freeConversation') }}
            </span>
            <ChevronDown :size="11" class="project-dropdown-chevron" :class="{ 'is-expanded': showProjectDropdown }" />
          </button>
        </div>
        <div class="toolbar-right">
          <button class="toolbar-agent-config" @click="router.push('/settings')">
            <Settings :size="11" />
            <span>{{ t('chat.agentConfig') }}</span>
          </button>
        </div>
      </div>
    </div>

    <ComposerCommandPanel
      ref="commandPanelRef"
      :open="commandPanelOpen"
      :anchor="composerBoxRef"
      :spatial="spatial"
      :slash-query="showPlusMenu ? null : slashQuery"
      :initial-page="commandPage"
      :can-attach="canAttach"
      :permission="permission"
      :mode-version-id="modeVersionId"
      :space-options="spaceOptions"
      :default-model="defaultModel"
      :default-model-label="defaultModelLabel"
      :agent-models="agentModels"
      :model-pinned="Boolean(meetingModelOverride)"
      :settings-saving="settingsSaving"
      :settings-error="settingsError"
      :command-host="commandHost"
      :workspace-root="selectedProject?.path ?? null"
      @close="closeCommandPanel"
      @focus-composer="textareaRef?.focus()"
      @attach="openFilePicker"
      @action="chooseCommand"
      @send-as-text="closeCommandPanel(); submit(undefined, true)"
      @insert-template="insertCommandTemplate"
      @select-mode="selectModePreset"
      @update:permission="changePermission"
      @update:space-options="changeSpaceOptions"
      @clear-model-pin="changeModel(null)"
      @manage-models="manageModels"
      @mode-label="(label, unavailable, icon) => { modeLabel = label; modeUnavailable = unavailable; modeGlyph = icon }"
      @mode-defaults="(defaults, active) => { activeModeDefaults = defaults; hasActiveMode = active }"
    />

    <!-- Docked-only dispatch menu: teleported so the dialog's overflow:hidden never clips it. -->
    <Teleport v-if="!hero" to="body">
      <div v-if="showAskMenu" class="ask-menu" :style="askMenuStyle">
        <button class="ask-menu-item" @click="submit('queued')">{{ t('composer.sendQueued') }}</button>
        <button class="ask-menu-item" @click="submit('parallel')">{{ t('composer.sendParallel') }}</button>
      </div>
    </Teleport>

    <Teleport to="body">
      <div
        v-if="showProjectDropdown"
        :id="projectMenuId"
        ref="projectMenuRef"
        class="project-dropdown-portal"
        :style="projectPortalStyle"
        role="dialog"
        :aria-label="t('chat.openedProjects')"
        @keydown="projectMenuKeydown"
        @focusout="projectMenuFocusout"
      >
        <div v-if="(projects?.length ?? 0) > 0" class="project-dropdown-scroll">
          <div class="project-dropdown-section">
            <div class="project-dropdown-section-title">{{ t('chat.openedProjects') }}</div>
            <button
              class="project-dropdown-item"
              :class="{ active: !selectedProjectId }"
              :aria-pressed="!selectedProjectId"
              @click="selectProject(null)"
            >
              <Sparkles :size="12" />
              <span>{{ t('chat.freeConversation') }}</span>
            </button>
            <button
              v-for="project in projects"
              :key="selectionKey(project)"
              class="project-dropdown-item"
              :class="{ active: project.id === selectedProjectId && (!project.storage_id || project.storage_id === selectedStorage) }"
              :aria-pressed="project.id === selectedProjectId && (!project.storage_id || project.storage_id === selectedStorage)"
              @click="selectProject(selectionKey(project))"
            >
              <FolderOpen :size="12" />
              <span>{{ project.name }}</span>
            </button>
          </div>
        </div>
        <button
          v-else
          class="project-dropdown-item"
          :class="{ active: !selectedProjectId }"
          :aria-pressed="!selectedProjectId"
          @click="selectProject(null)"
        >
          <Sparkles :size="12" />
          <span>{{ t('chat.freeConversation') }}</span>
        </button>
        <button class="project-dropdown-item project-dropdown-new" @click="openNewProject">
          <FolderPlus :size="12" />
          <span>{{ t('chat.openNewProject') }}</span>
        </button>
      </div>
    </Teleport>

    <ImageLightbox
      :open="composerLightboxOpen"
      :images="composerLightboxImages"
      :initial-index="composerLightboxIndex"
      @close="composerLightboxOpen = false"
    />
  </div>
</template>

<style scoped>
/* Outer max-width (min(852px, 100%)) and gutters come from global styles.css
   (.composer) and inner 820px cap comes from .welcome-dialog — never set
   max-width: 100% here or [data-v-*] specificity overrides the centered column. */
.composer {
  container-type: inline-size;
  container-name: composer;
  width: 100%;
  min-width: 0;
  box-sizing: border-box;
  overflow: visible;
}
/* Floating composer capsule: 1px border, subtle resting elevation, focus ring.
   NEVER apply transform here — .composer-drop-hint inside uses backdrop-filter. */
.composer-box {
  position: relative;
  width: 100%;
  min-width: 0;
  box-sizing: border-box;
  border: 1px solid var(--border-default);
  border-radius: 16px;
  background: var(--surface-input, var(--surface-section));
  box-shadow: var(--shadow-card-subtle);
  transition:
    border-color var(--duration-pop, 180ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    box-shadow var(--duration-pop, 180ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    background-color var(--duration-pop, 180ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.composer-box:focus-within {
  border-color: var(--border-input-focus, var(--border-default));
  box-shadow: var(--focus-ring), var(--shadow-card-subtle);
}
.welcome-dialog-main {
  width: 100%;
  min-width: 0;
  box-sizing: border-box;
  padding: 12px 14px 6px;
  gap: 8px;
  align-items: flex-end;
}
.composer-box:not(:has(.welcome-dialog-toolbar)) .welcome-dialog-main {
  padding: 10px 12px;
}
.welcome-dialog-input {
  flex: 1 1 0;
  min-width: 0;
  padding: 5px 2px;
}
/* Single continuous capsule — zero horizontal divider lines between input & toolbar. */
.welcome-dialog-toolbar {
  flex-wrap: wrap;
  gap: 4px 8px;
  min-width: 0;
  max-width: 100%;
  box-sizing: border-box;
  padding: 4px 12px 10px;
  border-top: 0;
}
.toolbar-left {
  flex: 1 1 auto;
  min-width: 0;
  max-width: 100%;
  flex-wrap: wrap;
  gap: 4px 6px;
}
.toolbar-right {
  flex-shrink: 0;
  margin-left: auto;
  max-width: 100%;
}
.welcome-dialog-plus {
  width: 28px;
  height: 28px;
  border-radius: 999px;
  color: var(--text-secondary);
  transition:
    transform var(--duration-tap, 120ms) var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1)),
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.welcome-dialog-plus:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}
.welcome-dialog-plus:active {
  transform: scale(0.94);
}
.mode-selector-trigger,
.permission-selector-trigger,
.project-dropdown-trigger,
.toolbar-agent-config {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  max-width: 100%;
  padding: 4px 9px;
  border-radius: 999px;
  border: 1px solid transparent;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  white-space: nowrap;
  flex-shrink: 0;
  cursor: pointer;
  transition:
    transform var(--duration-tap, 120ms) var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1)),
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    border-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.project-dropdown-trigger {
  background: var(--surface-raised);
  border-color: var(--border-muted);
}
.mode-selector-trigger:hover,
.permission-selector-trigger:hover,
.project-dropdown-trigger:hover,
.toolbar-agent-config:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}
.mode-selector-trigger:active,
.permission-selector-trigger:active,
.project-dropdown-trigger:active,
.toolbar-agent-config:active {
  transform: scale(0.97);
}
.welcome-dialog-plus:focus-visible,
.mode-selector-trigger:focus-visible,
.permission-selector-trigger:focus-visible,
.project-dropdown-trigger:focus-visible,
.toolbar-agent-config:focus-visible,
.composer-settings-summary button:focus-visible,
.queued-action:focus-visible,
.ask-menu-item:focus-visible,
.project-dropdown-item:focus-visible,
.attachment-preview-trigger:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
}
.mode-selector-label,
.permission-selector-label,
.project-dropdown-label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.model-selector-trigger .permission-selector-label {
  max-width: 168px;
}
@media (max-width: 1180px) {
  .welcome-dialog-toolbar {
    padding: 4px 10px 8px;
    gap: 4px 6px;
  }
  .mode-selector-trigger,
  .permission-selector-trigger,
  .project-dropdown-trigger,
  .toolbar-agent-config {
    padding: 4px 7px;
  }
  .mode-selector-label,
  .permission-selector-label,
  .project-dropdown-label {
    max-width: 88px;
  }
  .model-selector-trigger .permission-selector-label {
    max-width: 118px;
  }
}
@container composer (max-width: 440px) {
  .welcome-dialog-main {
    padding: 9px 10px 5px;
    gap: 6px;
  }
  .welcome-dialog-toolbar {
    padding: 4px 10px 8px;
    gap: 4px 6px;
  }
  .mode-selector-trigger,
  .permission-selector-trigger,
  .project-dropdown-trigger,
  .toolbar-agent-config {
    padding: 4px 7px;
  }
  .mode-selector-label,
  .permission-selector-label,
  .project-dropdown-label {
    max-width: 76px;
  }
  .model-selector-trigger .permission-selector-label {
    max-width: 92px;
  }
}
@container composer (max-width: 390px) {
  .toolbar-agent-config span {
    display: none;
  }
}
.mode-selector-chevron,
.permission-selector-chevron,
.project-dropdown-chevron {
  flex-shrink: 0;
  transition: transform var(--duration-pop, 180ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.mode-selector-chevron.is-expanded,
.permission-selector-chevron.is-expanded,
.project-dropdown-chevron.is-expanded { transform: rotate(180deg); }
.composer-permission-icon[data-risk='low'] { color: var(--text-secondary); }
.composer-permission-icon[data-risk='medium'] { color: var(--accent-warning); }
.composer-permission-icon[data-risk='high'] { color: var(--accent-danger); }
.composer-settings-summary { display: flex; align-items: center; gap: 6px; padding: 0 8px 6px; flex-wrap: wrap; min-width: 0; max-width: 100%; box-sizing: border-box; }
.composer-settings-summary button { display: inline-flex; align-items: center; background: var(--surface-section); border: 1px solid var(--border-muted); border-radius: 999px; padding: 3px 9px; font-size: 11px; font-variant-numeric: tabular-nums; color: var(--text-secondary); cursor: pointer; max-width: 100%; flex-shrink: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; transition: background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)), color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)); }
.composer-settings-summary button:hover { background: var(--surface-hover); color: var(--text-primary); }
.composer-settings-summary .composer-pinned-model.is-unavailable { color: var(--accent-warning); border-color: color-mix(in srgb, var(--accent-warning) 40%, transparent); }
.composer-queued {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 14px 2px;
}
.composer-select-input {
  height: 26px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  padding: 0 8px;
  font-size: 12px;
  background: var(--surface-raised);
  min-width: 140px;
}
.composer-send-wrapper {
  position: relative;
  display: flex;
  align-items: center;
  gap: 6px;
}
/* High-contrast monochrome-ready circular primary action buttons (Send & Stop) */
.welcome-dialog-send,
.composer-stop-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 30px;
  min-width: 30px;
  padding: 0;
  margin: 0;
  border-radius: 999px;
  border: 1px solid transparent;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.12);
  cursor: pointer;
  flex-shrink: 0;
  transition:
    transform var(--duration-tap, 120ms) var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1)),
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    border-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    opacity var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    box-shadow var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.welcome-dialog-send:hover:not(:disabled),
.composer-stop-button:hover:not(:disabled) {
  background: var(--bg-primary-button-hover, color-mix(in srgb, var(--bg-primary-button, var(--accent-primary)) 88%, var(--text-on-primary, #ffffff)));
  color: var(--text-on-primary, #ffffff);
}
.welcome-dialog-send:active:not(:disabled),
.composer-stop-button:active:not(:disabled) {
  transform: scale(0.96);
}
.welcome-dialog-send:disabled {
  background: var(--surface-raised);
  color: var(--text-muted);
  border-color: var(--border-muted);
  box-shadow: none;
  opacity: 0.55;
  cursor: not-allowed;
}
.composer-send-spinner {
  width: 14px;
  height: 14px;
  border: 1.75px solid color-mix(in srgb, currentColor 28%, transparent);
  border-top-color: currentColor;
  border-radius: 50%;
  animation: composer-spin 0.8s linear infinite;
}
@keyframes composer-spin {
  to { transform: rotate(360deg); }
}
.ask-menu {
  z-index: 9999;
  display: flex;
  flex-direction: column;
  min-width: 108px;
  border: 1px solid var(--border-default, var(--border-muted));
  border-radius: 10px;
  background: var(--surface-section);
  padding: 4px;
  box-shadow: var(--shadow-panel);
  transition:
    opacity var(--duration-pop, 180ms) var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1)),
    transform var(--duration-pop, 180ms) var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1));
}
@starting-style {
  .ask-menu {
    opacity: 0;
    transform: translateY(4px) scale(0.98);
  }
}
.ask-menu-item {
  border: none;
  background: none;
  text-align: left;
  font-size: 12px;
  padding: 6px 9px;
  border-radius: 6px;
  cursor: pointer;
  color: var(--text-primary);
  white-space: nowrap;
  transition: background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.ask-menu-item:hover {
  background: var(--surface-hover, var(--bg-hover));
}
.queued-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  border: 1px solid var(--border-card, var(--border-muted));
  border-radius: 10px;
  background: var(--surface-raised);
  padding: 7px 10px;
}
.queued-content {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  color: var(--text-secondary);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  word-break: break-word;
}
.queued-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}
.queued-action {
  border: none;
  background: none;
  font-size: 12px;
  padding: 3px 6px;
  border-radius: 5px;
  cursor: pointer;
  color: var(--text-muted);
  white-space: nowrap;
  transition:
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.queued-action:hover:not(:disabled) {
  background: var(--surface-hover, var(--bg-hover));
  color: var(--text-primary);
}
.queued-action:disabled {
  opacity: 0.5;
  cursor: default;
}
.queued-run-select { min-width: 150px; }
.queued-run-select :deep(.ui-select-trigger) { height: 24px; padding: 0 6px; font-size: 12px; }
.composer-attachments {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 10px 14px 2px;
}
.composer-box[data-dragging="true"] {
  outline: 1.5px dashed var(--accent-primary, var(--text-primary));
  outline-offset: -2px;
}
.composer-drop-hint {
  position: absolute;
  inset: 0;
  z-index: 15;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 12px;
  border-radius: inherit;
  background: color-mix(in srgb, var(--surface-raised, #16181f) 86%, transparent);
  backdrop-filter: blur(4px);
  color: var(--text-primary);
  font-size: 13px;
  font-weight: 500;
  pointer-events: none;
}
.attachment-preview-trigger {
  position: absolute;
  inset: 0;
  z-index: 1;
  border: none;
  background: transparent;
  padding: 0;
  cursor: zoom-in;
}
.composer-vision-notice {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 6px 14px 0;
  padding: 5px 9px;
  border-radius: 8px;
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d97706) 28%, transparent);
  background: color-mix(in srgb, var(--accent-warning, #d97706) 9%, transparent);
  color: var(--warning-text, var(--text-secondary));
  font-size: 12px;
  line-height: 1.35;
}
.composer-vision-notice svg {
  flex-shrink: 0;
}
/* The picker is driven programmatically from the plus menu; it must not take up a row. */
.composer-file-input {
  display: none;
}
.attachment-chip {
  max-width: 100%;
}
.attachment-content {
  min-width: 0;
}
.attachment-name {
  display: flex;
  max-width: 180px;
  min-width: 0;
  white-space: nowrap;
}
/* The stem truncates; the extension stays readable. */
.attachment-stem {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}
.attachment-meta {
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}
.attachment-spinner {
  position: absolute;
  color: var(--text-primary);
}
.plus-menu-item:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
@media (prefers-reduced-motion: reduce) {
  .composer-box,
  .welcome-dialog-plus,
  .mode-selector-trigger,
  .permission-selector-trigger,
  .project-dropdown-trigger,
  .toolbar-agent-config,
  .mode-selector-chevron,
  .permission-selector-chevron,
  .project-dropdown-chevron,
  .composer-settings-summary button,
  .welcome-dialog-send,
  .composer-stop-button,
  .ask-menu,
  .ask-menu-item,
  .queued-action {
    transition: none;
  }
  .composer-send-spinner {
    animation: none;
  }
  .welcome-dialog-plus:active,
  .mode-selector-trigger:active,
  .permission-selector-trigger:active,
  .project-dropdown-trigger:active,
  .toolbar-agent-config:active,
  .welcome-dialog-send:active:not(:disabled),
  .composer-stop-button:active:not(:disabled) {
    transform: none;
  }
}
</style>
