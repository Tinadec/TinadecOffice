<script setup lang="ts">
import { ArrowUp, Plus, Image, FileText, Settings } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { ref, onMounted, onUnmounted, nextTick } from 'vue'
import { useRouter } from 'vue-router'
import { UiButton } from '@/components/ui'
import PermissionSelector from './PermissionSelector.vue'
import type { PermissionLevel } from '@/types/mode'
import { homeController } from '@/controllers/HomeController'
import { getDispatchPref, type DispatchPref } from '@/lib/dispatchPref'

const { t } = useI18n()
const router = useRouter()

const props = defineProps<{
  busy: boolean
  modelValue: string
  mode?: unknown // deprecated, kept for compat
  permission: PermissionLevel
  sessionId?: string | null
  runs?: Array<{ id: string; status: string }>
  modeVersionId?: string | null
  meetingModel?: string | null
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  'update:permission': [value: PermissionLevel]
  'submit': [payload: { dispatch_mode: 'parallel' | 'queued' | 'insert'; target_run_id?: string | null; mode_version_id?: string | null; meeting_model?: string | null }]
  'add-image': []
  'add-file': []
}>()

const textareaRef = ref<HTMLTextAreaElement | null>(null)
const plusTriggerRef = ref<HTMLElement | null>(null)
const showPlusMenu = ref(false)
const plusMenuStyle = ref<Record<string, string>>({})
const showAskMenu = ref(false)

const queued = homeController.queuedMessages
const activeRuns = homeController.activeRuns
const steeringId = ref<string | null>(null)
const steerTarget = ref('')

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

function updatePlusMenuPosition() {
  const trigger = plusTriggerRef.value
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  plusMenuStyle.value = {
    position: 'fixed',
    bottom: `${window.innerHeight - rect.top + 6}px`,
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

function handleClickOutside(event: MouseEvent) {
  const target = event.target as HTMLElement
  if (!target.closest('.welcome-dialog-plus-wrapper') && !target.closest('.plus-dropdown-portal')) {
    showPlusMenu.value = false
  }
  if (!target.closest('.composer-send-wrapper')) {
    showAskMenu.value = false
  }
}

onMounted(() => document.addEventListener('click', handleClickOutside))
onUnmounted(() => document.removeEventListener('click', handleClickOutside))

function submit(pref?: DispatchPref) {
  const content = props.modelValue.trim()
  if (!content) return
  const p = pref ?? getDispatchPref()
  if (p === 'ask') {
    showAskMenu.value = !showAskMenu.value
    return
  }
  showAskMenu.value = false
  resetTextareaHeight()
  emit('submit', {
    dispatch_mode: p,
    target_run_id: null,
    mode_version_id: props.modeVersionId ?? null,
    meeting_model: props.meetingModel?.trim() ? props.meetingModel.trim() : null,
  })
}

function handleKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault()
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
  <div class="composer">
    <div class="composer-box welcome-dialog">
      <!-- queued cards sit inside dialog top when items are queued -->
      <div v-if="queued.length" class="composer-queued">
        <div v-for="item in queued" :key="item.id" class="queued-card">
          <div class="queued-content">{{ item.content }}</div>
          <div class="queued-actions">
            <template v-if="steeringId === item.id">
              <select v-model="steerTarget" class="composer-select-input queued-run-select">
                <option value="" disabled>选择目标 run</option>
                <option v-for="r in activeRuns" :key="r.id" :value="r.id">{{ r.id.slice(0,8) }} · {{ r.status }}</option>
              </select>
              <button class="queued-action" :disabled="!steerTarget" @click="confirmSteer(item.id)">确认引导</button>
            </template>
            <template v-else>
              <button class="queued-action" @click="startSteer(item.id)">引导</button>
              <button class="queued-action" @click="homeController.promoteQueued(item.id)">并列</button>
              <button class="queued-action" @click="homeController.editQueued(item.id)">编辑</button>
              <button class="queued-action" @click="homeController.dismissQueued(item.id)">×</button>
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

        <textarea
          ref="textareaRef"
          :value="modelValue"
          class="welcome-dialog-input"
          :placeholder="t('chat.whatToDo')"
          rows="1"
          @input="emit('update:modelValue', ($event.target as HTMLTextAreaElement).value); autoResize()"
          @keydown="handleKeydown"
        />

        <div class="composer-send-wrapper">
          <UiButton
            variant="ghost"
            size="icon"
            class="welcome-dialog-send"
            :disabled="!modelValue.trim()"
            @click="submit()"
          >
            <ArrowUp :size="15" />
          </UiButton>
          <div v-if="showAskMenu" class="ask-menu">
            <button class="ask-menu-item" @click="submit('queued')">排队发送</button>
            <button class="ask-menu-item" @click="submit('parallel')">并列发送</button>
          </div>
        </div>
      </div>

      <div class="welcome-dialog-toolbar">
        <div class="toolbar-left">
          <PermissionSelector
            :model-value="permission"
            @update:model-value="emit('update:permission', $event)"
          />
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
</template>

<style scoped>
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
.ask-menu {
  position: absolute;
  bottom: calc(100% + 6px);
  right: 0;
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

