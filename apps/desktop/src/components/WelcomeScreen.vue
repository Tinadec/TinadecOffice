<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'
import {
  ArrowUp,
  ChevronDown,
  FolderOpen,
  FolderPlus,
  Image,
  MessageCircle,
  Plus,
  SquareTerminal,
  FileText,
  ChevronRight,
  Settings,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import type { ProjectDto } from '../api'
import { UiButton, UiScrollArea } from '@/components/ui'
import ModeSelector from './ModeSelector.vue'
import PermissionSelector from './PermissionSelector.vue'
import type { AgentMode, PermissionLevel } from '@/types/mode'

const { t } = useI18n()
const router = useRouter()

const props = defineProps<{
  projects: ProjectDto[]
  selectedProjectId: string | null
  modelName: string
  busy: boolean
  panelStyle?: Record<string, string>
  panelDataAttrs?: Record<string, string>
}>()

const emit = defineEmits<{
  'send': [content: string]
  'create-project': []
  'select-project': [id: string]
  'add-image': []
  'add-file': []
  'update:mode': [value: AgentMode]
  'update:permission': [value: PermissionLevel]
}>()

const draft = ref('')
const showPlusMenu = ref(false)
const showProjectDropdown = ref(false)
const isChatMode = ref(false)
const currentMode = ref<AgentMode>('auto')
const currentPermission = ref<PermissionLevel>('default')
const textareaRef = ref<HTMLTextAreaElement | null>(null)
const projectTriggerRef = ref<HTMLElement | null>(null)
const dropdownStyle = ref<Record<string, string>>({})
const plusTriggerRef = ref<HTMLElement | null>(null)
const plusMenuStyle = ref<Record<string, string>>({})

const selectedProject = computed(() =>
  props.projects.find((p) => p.id === props.selectedProjectId) ?? null
)

const titleText = computed(() =>
  isChatMode.value ? t('chat.chatWithTinadec') : t('chat.startProject')
)

function handleSend() {
  const content = draft.value.trim()
  if (!content) return
  draft.value = ''
  resetTextareaHeight()
  emit('send', content)
}

function handleKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault()
    handleSend()
  }
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

function toggleChatMode() {
  isChatMode.value = !isChatMode.value
}

function updateDropdownPosition() {
  const trigger = projectTriggerRef.value
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  dropdownStyle.value = {
    position: 'fixed',
    top: `${rect.bottom + 6}px`,
    left: `${rect.left}px`,
    minWidth: `${Math.max(rect.width, 220)}px`,
  }
}

async function toggleProjectDropdown() {
  showProjectDropdown.value = !showProjectDropdown.value
  if (showProjectDropdown.value) {
    await nextTick()
    updateDropdownPosition()
  }
}

function selectProject(id: string) {
  emit('select-project', id)
  showProjectDropdown.value = false
}

function openNewProject() {
  emit('create-project')
  showProjectDropdown.value = false
}

function handleModeChange(mode: AgentMode) {
  currentMode.value = mode
  emit('update:mode', mode)
}

function updatePlusMenuPosition() {
  const trigger = plusTriggerRef.value
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  plusMenuStyle.value = {
    position: 'fixed',
    top: `${rect.bottom + 6}px`,
    left: `${rect.left}px`,
    minWidth: `${Math.max(rect.width, 130)}px`,
  }
}

async function togglePlusMenu() {
  showPlusMenu.value = !showPlusMenu.value
  if (showPlusMenu.value) {
    await nextTick()
    updatePlusMenuPosition()
  }
}

function handlePermissionChange(perm: PermissionLevel) {
  currentPermission.value = perm
  emit('update:permission', perm)
}

function handleClickOutside(event: MouseEvent) {
  const target = event.target as HTMLElement
  if (!target.closest('.project-dropdown-trigger') && !target.closest('.project-dropdown-portal')) {
    showProjectDropdown.value = false
  }
  if (!target.closest('.welcome-dialog-plus-wrapper') && !target.closest('.plus-dropdown-portal')) {
    showPlusMenu.value = false
  }
}

onMounted(() => document.addEventListener('click', handleClickOutside))
onUnmounted(() => document.removeEventListener('click', handleClickOutside))
</script>

<template>
  <div class="welcome-screen">
    <div class="welcome-content">
      <div class="welcome-title-row">
        <Transition name="title-fade" mode="out-in">
          <h1 :key="titleText" class="welcome-title">{{ titleText }}</h1>
        </Transition>
        <UiButton
          variant="ghost"
          size="icon"
          class="welcome-title-action"
          :title="isChatMode ? t('chat.terminal') : t('chat.chatMode')"
          @click="toggleChatMode"
        >
          <MessageCircle v-if="isChatMode" :size="15" />
          <SquareTerminal v-else :size="15" />
        </UiButton>
      </div>

      <div class="welcome-dialog" :style="panelStyle" v-bind="panelDataAttrs">
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
                :style="[plusMenuStyle, panelStyle]"
                v-bind="panelDataAttrs"
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

          <textarea
            ref="textareaRef"
            v-model="draft"
            class="welcome-dialog-input"
            :placeholder="t('chat.whatToDo')"
            rows="1"
            @keydown="handleKeydown"
            @input="autoResize"
          />

          <UiButton
            variant="ghost"
            size="icon"
            class="welcome-dialog-send"
            :disabled="!draft.trim()"
            @click="handleSend"
          >
            <ArrowUp :size="15" />
          </UiButton>
        </div>

        <div class="welcome-dialog-toolbar">
          <div class="toolbar-left">
            <ModeSelector
              :model-value="currentMode"
              @update:model-value="handleModeChange"
            />
            <PermissionSelector
              :model-value="currentPermission"
              @update:model-value="handlePermissionChange"
            />
            <button
              ref="projectTriggerRef"
              class="project-dropdown-trigger"
              @click="toggleProjectDropdown"
            >
              <FolderOpen :size="12" />
              <span class="project-dropdown-label">
                {{ selectedProject ? selectedProject.name : t('chat.selectProject') }}
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
    </div>

    <Teleport to="body">
      <div
        v-if="showProjectDropdown"
        class="project-dropdown-portal"
        :style="[dropdownStyle, panelStyle]"
        v-bind="panelDataAttrs"
      >
        <UiScrollArea v-if="projects.length > 0" class="project-dropdown-scroll">
          <div class="project-dropdown-section">
            <div class="project-dropdown-section-title">{{ t('chat.openedProjects') }}</div>
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
        <button class="project-dropdown-item project-dropdown-new" @click="openNewProject">
          <FolderPlus :size="12" />
          <span>{{ t('chat.openNewProject') }}</span>
        </button>
      </div>
    </Teleport>
  </div>
</template>
