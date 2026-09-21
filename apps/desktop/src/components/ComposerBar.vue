<script setup lang="ts">
import { ArrowUp, ChevronDown, FolderOpen, FolderPlus, Image, FileText, Plus, Settings, Sparkles, Square } from '@lucide/vue'
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
import { filterComposerCommands, parseComposerCommand, type ComposerCommand } from '@/lib/composerCommands'
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
  'add-image': []
  'add-file': []
  'stop': []
}>()

const textareaRef = ref<HTMLTextAreaElement | null>(null)
const plusTriggerRef = ref<HTMLElement | null>(null)
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

const commandIndex = ref(0)
const commandsDismissed = ref(false)
const commandSuggestions = computed(() =>
  commandsDismissed.value
    ? []
    : filterComposerCommands(props.modelValue).filter((command) => command.id !== 'stop' || props.canStop),
)
watch(() => props.modelValue, () => {
  commandIndex.value = 0
  commandsDismissed.value = false
})

const commandMenuStyle = ref<DropdownPlacement>({ position: 'fixed', left: '0px' })
watch(commandSuggestions, async (list) => {
  if (!list.length) return
  await nextTick()
  placeMenu(textareaRef.value, commandMenuStyle, { minWidth: 280, estimatedHeight: 116 })
})

function acceptCommand(command: ComposerCommand) {
  updateDraft(`/${command.name}${command.needsArgument ? ' ' : ''}`)
  commandsDismissed.value = true
  void nextTick(() => textareaRef.value?.focus())
}

function runCommand(command: ComposerCommand, argument: string) {
  commandsDismissed.value = true
  switch (command.id) {
    case 'stop':
      updateDraft('')
      emit('stop')
      break
    case 'new':
      updateDraft('')
      resetTextareaHeight()
      void homeController.createSession(props.selectedProjectId ?? null)
      break
    case 'queue':
    case 'parallel': {
      // One-shot dispatch override: the persisted Enter preference the send menu owns
      // is left alone. The draft is written through the controller rather than by
      // awaiting two layers of emit, because sendMessage reads it right after.
      const dispatchMode = command.id === 'queue' ? 'queued' : 'parallel'
      updateDraft(argument)
      resetTextareaHeight()
      void homeController.sendMessage({
        dispatch_mode: dispatchMode,
        target_run_id: null,
        mode_version_id: props.modeVersionId ?? null,
        meeting_model_override: props.meetingModelOverride ?? null,
      })
      break
    }
  }
}

function updateDraft(value: string) {
  homeController.updateDraft(value)
  emit('update:modelValue', value)
}

/** True when Enter was consumed by a command, so the plain send path must not run. */
function handleCommandEnter(): boolean {
  const parsed = parseComposerCommand(props.modelValue)
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

onMounted(() => document.addEventListener('click', handleClickOutside))
onUnmounted(() => document.removeEventListener('click', handleClickOutside))

function submit(pref?: DispatchPref) {
  const content = props.modelValue.trim()
  if (!content) return
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
              <button class="plus-menu-item" @click="emit('add-image'); showPlusMenu = false">
                <Image :size="12" />
                <span>{{ t('chat.addImage') }}</span>
              </button>
              <button class="plus-menu-item" @click="emit('add-file'); showPlusMenu = false">
                <FileText :size="12" />
                <span>{{ t('chat.addFile') }}</span>
              </button>
            </div>
          </Teleport>
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
              <code class="composer-command-syntax">/{{ command.name }}{{ command.needsArgument ? ' …' : '' }}</code>
              <span class="composer-command-label">{{ t(command.labelKey) }}</span>
              <span class="composer-command-hint">{{ t(command.hintKey) }}</span>
            </li>
          </ul>
        </Teleport>

        <textarea
          ref="textareaRef"
          :value="modelValue"
          class="welcome-dialog-input"
          :placeholder="t('chat.whatToDo')"
          rows="1"
          @input="emit('update:modelValue', ($event.target as HTMLTextAreaElement).value); autoResize()"
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
            :disabled="!modelValue.trim()"
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
</style>
