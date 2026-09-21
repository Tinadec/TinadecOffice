<script setup lang="ts">
import { ArrowUp, ChevronDown, FileText, Folder, FolderOpen, FolderPlus, Image, Plus, Settings, Sparkles, Square } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { ref, computed, watch, onMounted, onUnmounted, nextTick, type Ref } from 'vue'
import { useRouter } from 'vue-router'
import { UiButton, UiScrollArea } from '@/components/ui'
import ModeSelector from './ModeSelector.vue'
import PermissionSelector from './PermissionSelector.vue'
import type { PermissionLevel } from '@/types/mode'
import type { MeetingModelOverrideDto, ProjectDto } from '@/api'
import { homeController } from '@/controllers/HomeController'
import { getDispatchPref, type DispatchPref } from '@/lib/dispatchPref'
import { filterSlashCommands, parseSlashCommand, type AppCommand, type CommandHost } from '@/lib/appCommands'
import { completeMentionToken, filterMentionEntries, parseMentionToken, type MentionToken } from '@/lib/fileMentions'
import {
  attachFiles,
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
  busy: boolean
  modelValue: string
  permission: PermissionLevel
  projects?: ProjectDto[]
  selectedProjectId?: string | null
  sessionId?: string | null
  runs?: Array<{ id: string; status: string }>
  modeVersionId?: string | null
  meetingModelOverride?: MeetingModelOverrideDto | null
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
  'submit': [payload: { dispatch_mode: 'parallel' | 'queued' | 'insert'; target_run_id?: string | null; mode_version_id?: string | null; meeting_model_override?: MeetingModelOverrideDto | null }]
  'welcome-submit': [payload: { content: string; permission_mode: PermissionLevel; mode_version_id: string | null }]
  'create-project': []
  'select-project': [id: string | null]
  'stop': []
}>()

const textareaRef = ref<HTMLTextAreaElement | null>(null)
const plusTriggerRef = ref<HTMLElement | null>(null)
const fileInputRef = ref<HTMLInputElement | null>(null)
const sendTriggerRef = ref<HTMLElement | null>(null)
const showPlusMenu = ref(false)
const plusMenuStyle = ref<DropdownPlacement>({ position: 'fixed', left: '0px' })
const showAskMenu = ref(false)
const askMenuStyle = ref<DropdownPlacement>({ position: 'fixed', left: '0px' })
const projectTriggerRef = ref<HTMLElement | null>(null)
const showProjectDropdown = ref(false)
const projectDropdownStyle = ref<DropdownPlacement>({ position: 'fixed', left: '0px' })

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

const commandIndex = ref(0)
const commandsDismissed = ref(false)
const commandSuggestions = computed(() =>
  commandsDismissed.value
    ? []
    : filterSlashCommands(props.modelValue, commandHost),
)
watch(() => props.modelValue, () => {
  commandIndex.value = 0
  commandsDismissed.value = false
  void refreshMentions()
})

const commandMenuStyle = ref<DropdownPlacement>({ position: 'fixed', left: '0px' })
watch(commandSuggestions, async (list) => {
  if (!list.length) return
  await nextTick()
  placeMenu(textareaRef.value, commandMenuStyle, { minWidth: 280, estimatedHeight: 116 })
})

function acceptCommand(command: AppCommand) {
  updateDraft(`/${command.slash}${command.needsArgument ? ' ' : ''}`)
  commandsDismissed.value = true
  void nextTick(() => textareaRef.value?.focus())
}

function runCommand(command: AppCommand, argument: string) {
  commandsDismissed.value = true
  command.run(commandHost, argument)
}

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
  const highlighted = commandSuggestions.value[commandIndex.value]
  if (highlighted) {
    acceptCommand(highlighted)
    return true
  }
  return false
}

const selectedProject = computed(() =>
  props.projects?.find((p) => p.id === props.selectedProjectId) ?? null
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
  showPlusMenu.value = !showPlusMenu.value
  if (showPlusMenu.value) {
    await nextTick()
    placeMenu(plusTriggerRef.value, plusMenuStyle)
  }
}

const attachments = pendingAttachments
// An attachment row is scoped to a session at write time, so the hero box (no session
// yet) has nowhere to put bytes. The entries stay visible and disabled rather than
// vanishing, so the menu does not change shape between the two surfaces.
const canAttach = computed(() => Boolean(props.sessionId))
/**
 * A file with no words is a turn of its own: Core appends it to the transcript and starts no
 * run. So Send unlocks on either half. Only *ready* chips count - an upload still in flight
 * names no stored row, and sending early would drop the file.
 */
const canSend = computed(() => Boolean(props.modelValue.trim()) || readyAttachmentCount() > 0)

function openFilePicker(accept: string) {
  showPlusMenu.value = false
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

function onFilesPicked(event: Event) {
  const files = pickedFiles(event)
  if (!files.length) return
  void attachFiles(files, props.sessionId ?? null)
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
    placeMenu(projectTriggerRef.value, projectDropdownStyle, { minWidth: 220, estimatedHeight: 260 })
  }
}

function selectProject(id: string | null) {
  emit('select-project', id)
  showProjectDropdown.value = false
}

function openNewProject() {
  emit('create-project')
  showProjectDropdown.value = false
}

function handleClickOutside(event: MouseEvent) {
  const target = event.target as HTMLElement
  if (!target.closest('.welcome-dialog-plus-wrapper') && !target.closest('.plus-dropdown-portal')) {
    showPlusMenu.value = false
  }
  if (!target.closest('.composer-send-wrapper') && !target.closest('.ask-menu')) {
    showAskMenu.value = false
  }
  if (!target.closest('.project-dropdown-trigger') && !target.closest('.project-dropdown-portal')) {
    showProjectDropdown.value = false
  }
}

onMounted(() => {
  document.addEventListener('click', handleClickOutside)
  // A draft can arrive pre-filled (edit-and-resend, a restored session), and the
  // modelValue watcher does not fire for a value that was never changed.
  void refreshMentions()
})

onUnmounted(() => document.removeEventListener('click', handleClickOutside))

function submit(pref?: DispatchPref) {
  const content = props.modelValue.trim()
  if (!content && !canSend.value) return
  if (props.hero) {
    // Start-page send: full welcome payload, no dispatch menu.
    resetTextareaHeight()
    emit('welcome-submit', {
      content,
      permission_mode: props.permission,
      mode_version_id: props.modeVersionId ?? null,
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
  })
}

function handleKeydown(event: KeyboardEvent) {
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
  const suggestions = commandSuggestions.value
  if (suggestions.length) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      const delta = event.key === 'ArrowDown' ? 1 : -1
      commandIndex.value = (commandIndex.value + delta + suggestions.length) % suggestions.length
      return
    }
    if (event.key === 'Escape') {
      event.preventDefault()
      commandsDismissed.value = true
      return
    }
    if (event.key === 'Tab') {
      event.preventDefault()
      acceptCommand(suggestions[commandIndex.value] ?? suggestions[0])
      return
    }
  }
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault()
    if (suggestions.length || props.modelValue.trimStart().startsWith('/')) {
      if (handleCommandEnter()) return
    }
    submit()
  }
}

function startSteer(id: string) {
  const list = activeRuns.value
  if (list.length === 1) {
    void homeController.steerQueued(id, list[0].id)
    return
  }
  steeringId.value = id
  steerTarget.value = ''
}

function confirmSteer(id: string) {
  if (!steerTarget.value) return
  void homeController.steerQueued(id, steerTarget.value)
  steeringId.value = null
}
</script>

<template>
  <div class="composer" :class="{ 'composer--hero': hero }">
    <div v-if="invokeError" class="composer-error" role="alert">{{ invokeError }}</div>
    <div
      class="composer-box welcome-dialog"
      :data-composer-active="modelValue.trim() ? 'true' : 'false'"
      :style="panelStyle"
      v-bind="panelDataAttrs"
    >
      <!-- queued cards sit inside dialog top when items are queued -->
      <div v-if="!hero && queued.length" class="composer-queued">
        <div v-for="item in queued" :key="item.id" class="queued-card">
          <div class="queued-content">{{ item.content }}</div>
          <div class="queued-actions">
            <template v-if="steeringId === item.id">
              <select v-model="steerTarget" class="composer-select-input queued-run-select" :aria-label="t('composer.selectTargetRun')">
                <option value="" disabled>{{ t('composer.selectTargetRun') }}</option>
                <option v-for="r in activeRuns" :key="r.id" :value="r.id">{{ r.id.slice(0,8) }} · {{ r.status }}</option>
              </select>
              <button class="queued-action" :disabled="!steerTarget" @click="confirmSteer(item.id)">{{ t('composer.confirmSteer') }}</button>
            </template>
            <template v-else>
              <button class="queued-action" @click="startSteer(item.id)">{{ t('composer.steer') }}</button>
              <button class="queued-action" @click="homeController.promoteQueued(item.id)">{{ t('composer.parallel') }}</button>
              <button class="queued-action" @click="homeController.editQueued(item.id)">{{ t('composer.edit') }}</button>
              <button class="queued-action" :aria-label="t('composer.dismiss')" @click="homeController.dismissQueued(item.id)">×</button>
            </template>
          </div>
        </div>
      </div>

      <div v-if="attachments.length" class="composer-attachments" role="list" aria-live="polite" :aria-label="t('composer.attachments')">
        <div
          v-for="item in attachments"
          :key="item.clientId"
          class="attachment-chip"
          :class="`is-${item.status}`"
          role="listitem"
          :title="attachmentHint(item)"
          data-testid="attachment-chip"
        >
          <component :is="item.mediaType.startsWith('image/') ? Image : FileText" :size="12" class="attachment-icon" aria-hidden="true" />
          <span class="attachment-name">{{ item.fileName }}</span>
          <span class="attachment-meta">{{ formatAttachmentBytes(item.size) }}</span>
          <span v-if="item.status === 'uploading'" class="attachment-spinner" aria-hidden="true" />
          <span v-if="item.status !== 'ready'" class="attachment-state">{{ attachmentHint(item) }}</span>
          <button class="attachment-remove" :aria-label="t('composer.removeAttachment', { name: item.fileName })" @click="dropAttachment(item.clientId)">×</button>
        </div>
      </div>

      <div class="welcome-dialog-main">
        <div class="welcome-dialog-plus-wrapper">
          <button
            ref="plusTriggerRef"
            class="welcome-dialog-plus"
            @click="togglePlusMenu"
          >
            <Plus :size="15" />
          </button>
          <Teleport to="body">
            <div
              v-if="showPlusMenu"
              class="plus-dropdown-portal"
              :style="plusMenuStyle"
            >
              <button
                class="plus-menu-item"
                data-testid="composer-attach-image"
                :disabled="!canAttach"
                :title="canAttach ? t('chat.addImage') : t('composer.attachNeedsSession')"
                @click="openFilePicker('image/*')"
              >
                <Image :size="12" />
                <span>{{ t('chat.addImage') }}</span>
              </button>
              <button
                class="plus-menu-item"
                data-testid="composer-attach-file"
                :disabled="!canAttach"
                :title="canAttach ? t('chat.addFile') : t('composer.attachNeedsSession')"
                @click="openFilePicker('')"
              >
                <FileText :size="12" />
                <span>{{ t('chat.addFile') }}</span>
              </button>
            </div>
          </Teleport>
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

        <Teleport to="body">
          <ul
            v-if="commandSuggestions.length"
            class="composer-commands-portal"
            :style="commandMenuStyle"
            data-testid="composer-commands"
            role="listbox"
            :aria-label="t('composer.commands')"
          >
            <li
              v-for="(command, index) in commandSuggestions"
              :key="command.id"
              role="option"
              :aria-selected="index === commandIndex"
              :class="{ 'is-active': index === commandIndex }"
              :data-testid="`composer-command-${command.id}`"
              @mouseenter="commandIndex = index"
              @mousedown.prevent="acceptCommand(command)"
            >
              <code class="composer-command-syntax">/{{ command.slash }}{{ command.needsArgument ? ' …' : '' }}</code>
              <span class="composer-command-label">{{ t(command.labelKey) }}</span>
              <span v-if="command.hintKey" class="composer-command-hint">{{ t(command.hintKey ?? '') }}</span>
            </li>
          </ul>
        </Teleport>

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
            v-if="busy && canStop"
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
            variant="ghost"
            size="icon"
            class="welcome-dialog-send"
            data-testid="composer-send"
            :disabled="!canSend"
            :aria-label="canSend ? t('chat.send') : t('chat.nothingToSend')"
            @click="submit()"
          >
            <span v-if="busy" class="composer-send-spinner" role="status" aria-label="sending" />
            <ArrowUp v-else :size="15" />
          </UiButton>
        </div>
      </div>

      <div class="welcome-dialog-toolbar">
        <div class="toolbar-left">
          <!-- THE one mode selector: the installed packs' published versions. -->
          <ModeSelector
            :mode-version-id="modeVersionId ?? null"
            @update:mode-version-id="emit('update:modeVersionId', $event)"
          />
          <PermissionSelector
            :model-value="permission"
            @update:model-value="emit('update:permission', $event)"
          />
          <button
            ref="projectTriggerRef"
            class="project-dropdown-trigger"
            @click="toggleProjectDropdown"
          >
            <FolderOpen :size="12" />
            <span class="project-dropdown-label">
              {{ selectedProject?.name ?? t('chat.freeConversation') }}
            </span>
            <ChevronDown :size="11" class="project-dropdown-chevron" />
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
        class="project-dropdown-portal"
        :style="projectDropdownStyle"
      >
        <UiScrollArea v-if="(projects?.length ?? 0) > 0" class="project-dropdown-scroll">
          <div class="project-dropdown-section">
            <div class="project-dropdown-section-title">{{ t('chat.openedProjects') }}</div>
            <button
              class="project-dropdown-item"
              :class="{ active: !selectedProjectId }"
              @click="selectProject(null)"
            >
              <Sparkles :size="12" />
              <span>{{ t('chat.freeConversation') }}</span>
            </button>
            <button
              v-for="project in projects"
              :key="project.id"
              class="project-dropdown-item"
              :class="{ active: project.id === selectedProjectId }"
              @click="selectProject(project.id)"
            >
              <FolderOpen :size="12" />
              <span>{{ project.name }}</span>
            </button>
          </div>
        </UiScrollArea>
        <button
          v-else
          class="project-dropdown-item"
          :class="{ active: !selectedProjectId }"
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
.queued-run-select {
  min-width: 150px;
  height: 24px;
}
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
  display: inline-flex;
  align-items: center;
  gap: 6px;
  max-width: 100%;
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  background: var(--surface-raised);
  padding: 3px 4px 3px 8px;
  font-size: 12px;
  color: var(--text-secondary);
}
.attachment-chip.is-uploading {
  border-style: dashed;
}
.attachment-chip.is-failed {
  border-color: var(--accent-danger);
  color: var(--accent-danger);
}
.attachment-icon {
  flex-shrink: 0;
  color: var(--text-muted);
}
.attachment-name {
  max-width: 180px;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-primary);
}
.attachment-meta {
  flex-shrink: 0;
  color: var(--text-muted);
}
.attachment-state {
  flex-shrink: 0;
}
.attachment-spinner {
  width: 10px;
  height: 10px;
  border: 2px solid var(--border-muted);
  border-top-color: var(--text-primary);
  border-radius: 50%;
  animation: composer-spin 0.8s linear infinite;
}
.attachment-remove {
  border: none;
  background: none;
  cursor: pointer;
  color: var(--text-muted);
  font-size: 14px;
  line-height: 1;
  padding: 2px 6px;
  border-radius: 999px;
}
.attachment-remove:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}
.plus-menu-item:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
</style>
