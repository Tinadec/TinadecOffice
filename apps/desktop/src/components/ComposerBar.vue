<script setup lang="ts">
import { ArrowUp, ChevronDown, FileText, Folder, FolderOpen, FolderPlus, Image, Layers, Plus, Settings, Sparkles, Square, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { ref, shallowRef, computed, watch, onMounted, onUnmounted, nextTick, useId, type Component, type Ref } from 'vue'
import { useRouter } from 'vue-router'
import { UiButton, UiSelectField } from '@/components/ui'
import ComposerCommandPanel from './ComposerCommandPanel.vue'
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
import { composerSlashQuery, composerSettings, permissionChoices, type ComposerPage } from '@/lib/composerCommands'
import { defaultSpaceOptions } from '@/lib/composerSettings'
import { completeMentionToken, filterMentionEntries, parseMentionToken, type MentionToken } from '@/lib/fileMentions'
import {
  attachFiles,
  attachmentDisplayState,
  fileNameParts,
  formatAttachmentBytes,
  MAX_ATTACHMENT_BYTES,
  pendingAttachments,
  readyAttachmentCount,
  reconcileSession,
  removePendingAttachment,
  TOO_LARGE_CODE,
  type AttachableFile,
  type PendingAttachment,
} from '@/lib/pendingAttachments'
import { api } from '@/api'
import { toDirEntryView, type DirEntryDto, type DirEntryView } from '@/lib/workspaceSearch'
import { computeDropdownPlacement, type DropdownPlacement } from '@/lib/dropdownPlacement'

const { t } = useI18n()
const router = useRouter()

const props = defineProps<{
  /** Hero (start page) variant: centered box, welcome-send dispatch, no queued cards. */
  hero?: boolean
  spatial?: boolean
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
const permissionChoice = computed(() => permissionChoices.find(choice => choice.value === props.permission)!)
const permissionLabel = computed(() => t(permissionChoice.value.label))
const enabledSpaceLabels = computed(() => props.spatial ? [
  ...composerSettings.filter(setting => 'toggle' in setting && props.spaceOptions?.[setting.toggle]).map(setting => t(setting.label)),
  ...(props.spaceOptions?.workflow_mode_version_id ? [t('commandPanel.workflow')] : []),
] : [])
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
const attachmentError = ref<string | null>(null)
let disposed = false

const queued = homeController.queuedMessages
const activeRuns = homeController.activeRuns
// 发送失败可见化（此前 invokeError 被写入但从未渲染，用户看不到失败原因）。
const invokeError = computed(() => (homeController.invokeError as unknown as { value: string | null } | undefined)?.value ?? null)
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
      ...(props.spatial ? { space_options: { ...(props.spaceOptions ?? defaultSpaceOptions()) } } : {}),
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
watch(() => props.modelValue, (value) => {
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

function changeMode(value: string | null) { consumeSettingsSlash(); emit('update:modeVersionId', value) }
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
const pinnedProviders = ref<ModelProviderInstanceDto[] | null>(null)
let pinnedRead = 0
watch(() => [props.meetingModelOverride?.provider_instance_id, selectedStorage.value] as const, async ([providerId]) => {
  const read = ++pinnedRead
  pinnedProviders.value = null
  if (!providerId) return
  try {
    const rows = await api.listModelProviders()
    if (read === pinnedRead) pinnedProviders.value = Array.isArray(rows) ? rows : []
  } catch { /* unknown: shown as pinned, not as unavailable */ }
}, { immediate: true })
const pinnedUnavailable = computed(() => {
  const pinned = props.meetingModelOverride
  if (!pinned || !pinnedProviders.value) return false
  const provider = pinnedProviders.value.find(row => row.id === pinned.provider_instance_id)
  return !provider || !provider.enabled
})
const pinnedName = computed(() => {
  const pinned = props.meetingModelOverride
  if (!pinned) return ''
  return pinned.model || pinnedProviders.value?.find(row => row.id === pinned.provider_instance_id)?.display_name || t('composer.pinnedModelUnknown')
})
function changeSpaceOptions(value: SpaceOptionsDto) { consumeSettingsSlash(); emit('update:spaceOptions', value) }

function updateDraft(value: string) {
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
const canAttach = computed(() => !attachmentPreparing.value && !props.settingsSaving)
/**
 * A file with no words is a turn of its own: Core appends it to the transcript and starts no
 * run. So Send unlocks on either half. Only *ready* chips count - an upload still in flight
 * names no stored row, and sending early would drop the file.
 */
const canSend = computed(() => Boolean(props.modelValue.trim()) || readyAttachmentCount() > 0)

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

async function onFilesPicked(event: Event) {
  const files = pickedFiles(event)
  if (!files.length) return
  const projectId = props.selectedProjectId
  const spatial = props.spatial
  attachmentError.value = null
  attachmentPreparing.value = true
  if (!props.sessionId) preparingFiles.value = files.map((file, index) => ({ key: `${index}:${file.name}`, name: file.name, size: file.size, type: file.type }))
  try {
    const sessionId = props.sessionId ?? await homeController.ensureComposerSession()
    if (disposed || projectId !== props.selectedProjectId || spatial !== props.spatial || props.sessionId && props.sessionId !== sessionId) return
    await attachFiles(files, sessionId)
  } catch (error) {
    if (!disposed) attachmentError.value = `${t('commandPanel.attachmentError')} ${error instanceof Error ? error.message : String(error)}`
  } finally {
    attachmentPreparing.value = false
    preparingFiles.value = []
  }
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
  return t('composer.attachFailed', { code: item.errorCode ?? 'unknown' })
}

// Chips describe rows in the session the composer was attached to. immediate covers
// a remount onto a different session, where no change is observed from inside this
// component's lifetime.
watch(() => props.sessionId, (id) => { reconcileSession(id ?? null) }, { immediate: true })

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

onMounted(() => {
  void defaultModel.load()
  void agentModels.load()
  document.addEventListener('click', handleClickOutside)
  // A draft can arrive pre-filled (edit-and-resend, a restored session), and the
  // modelValue watcher does not fire for a value that was never changed.
  void refreshMentions()
})

onUnmounted(() => { disposed = true; document.removeEventListener('click', handleClickOutside) })

function submit(pref?: DispatchPref, asText = false) {
  if (props.settingsSaving || modelSaving.value || attachmentPreparing.value) return
  if (!asText && handleCommandEnter()) return
  const content = props.modelValue.trim()
  if (!content && !canSend.value) return
  if (commandPanelOpen.value) closeCommandPanel()
  if (props.hero) {
    // Start-page send: full welcome payload, no dispatch menu.
    resetTextareaHeight()
    emit('welcome-submit', {
      content,
      permission_mode: props.permission,
      mode_version_id: props.modeVersionId ?? null,
      meeting_model_override: props.meetingModelOverride ?? null,
      ...(props.spatial ? { space_options: { ...(props.spaceOptions ?? defaultSpaceOptions()) } } : {}),
    })
    return
  }
  const p = pref ?? getDispatchPref()
  if (p === 'ask') {
    void toggleAskMenu()
    return
  }
  showAskMenu.value = false
  resetTextareaHeight()
  emit('submit', {
    dispatch_mode: p,
    target_run_id: null,
    mode_version_id: props.modeVersionId ?? null,
    meeting_model_override: props.meetingModelOverride ?? null,
    ...(props.spatial ? { space_options: { ...(props.spaceOptions ?? defaultSpaceOptions()) } } : {}),
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
    <div v-if="invokeError" class="composer-error" role="alert">{{ invokeError }}</div>
    <div v-if="settingsError && !commandPanelOpen" class="composer-error" role="alert">{{ settingsError }}</div>
    <div v-if="attachmentError" class="composer-error" role="alert">{{ attachmentError }}</div>
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
      :style="panelStyle"
      v-bind="panelDataAttrs"
    >
      <!-- queued cards sit inside dialog top when items are queued -->
      <div v-if="!hero && !spatial && queued.length" class="composer-queued">
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
                  ...activeRuns.map((r) => ({ value: r.id, label: `${r.id.slice(0,8)} · ${r.status}` })),
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
      </div>

      <!-- The attachment primitive's five states: idle while the draft session is being made,
           uploading (an image previews locally, dimmed), done, or error with the reason in words. -->
      <AttachmentGroup v-if="attachments.length || preparingFiles.length" class="composer-attachments" role="list" aria-live="polite" :aria-label="t('composer.attachments')">
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
            <img v-if="item.previewUrl" :src="item.previewUrl" alt="" />
            <component :is="item.mediaType.startsWith('image/') ? Image : FileText" v-else aria-hidden="true" />
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
      </AttachmentGroup>

      <div class="welcome-dialog-main">
        <div class="welcome-dialog-plus-wrapper">
          <button
            ref="plusTriggerRef"
            class="welcome-dialog-plus"
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
          :placeholder="t('chat.whatToDo')"
          rows="1"
          @input="onDraftInput"
          @click="onCaretMove"
          @keydown="handleKeydown"
        />

        <div ref="sendTriggerRef" class="composer-send-wrapper">
          <UiButton
            v-if="busy && canStop && (!spatial || !canSend)"
            variant="ghost"
            size="icon"
            class="composer-stop-button"
            data-testid="composer-stop"
            :aria-label="t('chat.stopRun')"
            :title="t('chat.stopRun')"
            @click="emit('stop')"
          >
            <Square :size="14" />
          </UiButton>
          <UiButton
            v-if="!spatial || !(busy && canStop && !canSend)"
            variant="ghost"
            size="icon"
            class="welcome-dialog-send"
            data-testid="composer-send"
            :disabled="!canSend || settingsSaving || modelSaving || attachmentPreparing"
            :aria-label="canSend ? t('chat.send') : t('chat.nothingToSend')"
            @click="submit()"
          >
            <span v-if="busy" class="composer-send-spinner" role="status" aria-label="sending" />
            <ArrowUp v-else :size="15" />
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
      @close="closeCommandPanel"
      @focus-composer="textareaRef?.focus()"
      @attach="openFilePicker"
      @action="chooseCommand"
      @send-as-text="closeCommandPanel(); submit(undefined, true)"
      @update:mode-version-id="changeMode"
      @update:permission="changePermission"
      @update:space-options="changeSpaceOptions"
      @clear-model-pin="changeModel(null)"
      @manage-models="manageModels"
      @mode-label="(label, unavailable, icon) => { modeLabel = label; modeUnavailable = unavailable; modeGlyph = icon }"
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
  </div>
</template>

<style scoped>
.mode-selector-chevron,
.permission-selector-chevron { flex-shrink: 0; transition: transform 160ms ease; }
.mode-selector-chevron.is-expanded,
.permission-selector-chevron.is-expanded { transform: rotate(180deg); }
@media (prefers-reduced-motion: reduce) {
  .mode-selector-chevron,
  .permission-selector-chevron { transition: none; }
}
.composer-permission-icon[data-risk='low'] { color: color-mix(in srgb, var(--accent-info) 80%, var(--text-primary)); }
.composer-permission-icon[data-risk='medium'] { color: var(--accent-warning); }
.composer-permission-icon[data-risk='high'] { color: var(--accent-danger); }
.composer-settings-summary { display: flex; align-items: center; gap: 6px; padding: 0 8px 6px; flex-wrap: wrap; }
.composer-settings-summary button { background: var(--surface-section); border: 1px solid var(--border-muted); border-radius: 999px; padding: 3px 8px; font-size: 11px; color: var(--text-secondary); cursor: pointer; }
.composer-settings-summary button:hover { background: var(--surface-hover); }
.composer-settings-summary .composer-pinned-model.is-unavailable { color: var(--accent-warning); border-color: color-mix(in srgb, var(--accent-warning) 40%, transparent); }
.composer-error {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 0 8px;
  padding: 8px 12px;
  border: 1px solid rgba(239, 68, 68, 0.35);
  border-radius: 10px;
  background: rgba(239, 68, 68, 0.08);
  color: #ef4444;
  font-size: 13px;
}
.composer-queued {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 12px 0;
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
}
.composer-send-spinner {
  width: 15px;
  height: 15px;
  border: 2px solid var(--border-muted);
  border-top-color: var(--text-primary);
  border-radius: 50%;
  animation: composer-spin 0.8s linear infinite;
}
.composer-stop-button {
  margin-right: 2px;
  color: var(--text-secondary);
}
.composer-stop-button:hover {
  color: var(--text-primary);
}
@keyframes composer-spin {
  to { transform: rotate(360deg); }
}
.ask-menu {
  z-index: 9999;
  display: flex;
  flex-direction: column;
  min-width: 96px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-section);
  padding: 4px;
  box-shadow: var(--shadow-panel);
}
.ask-menu-item {
  border: none;
  background: none;
  text-align: left;
  font-size: 12px;
  padding: 5px 8px;
  border-radius: 4px;
  cursor: pointer;
  color: var(--text-primary);
  white-space: nowrap;
}
.ask-menu-item:hover {
  background: var(--bg-hover);
}
.queued-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-raised);
  padding: 6px 8px;
}
.queued-content {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  color: var(--text-muted);
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
  border-radius: 4px;
  cursor: pointer;
  color: var(--text-muted);
  white-space: nowrap;
}
.queued-action:hover:not(:disabled) {
  background: var(--bg-hover);
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
  padding: 8px 12px 0;
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
}
.attachment-spinner {
  position: absolute;
  color: var(--text-primary);
}
.plus-menu-item:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
</style>
