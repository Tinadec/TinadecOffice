<script setup lang="ts">
import { ArrowUp, Plus, Image, FileText, Settings } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { ref, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { UiButton, UiDropdownMenu, UiInput, UiLabel } from '@/components/ui'
import PermissionSelector from './PermissionSelector.vue'
import type { PermissionLevel } from '@/types/mode'
import { api, type AgentModeTopologyDto } from '@/api'

const { t } = useI18n()
const router = useRouter()

const props = defineProps<{
  busy: boolean
  modelValue: string
  mode?: unknown // deprecated, kept for compat
  permission: PermissionLevel
  sessionId?: string | null
  runs?: Array<{ id: string; status: string }>
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  'update:permission': [value: PermissionLevel]
  'submit': [payload: { dispatch_mode: 'parallel' | 'queued' | 'insert'; target_run_id?: string | null; mode_version_id?: string | null; meeting_model?: string | null }]
  'add-image': []
  'add-file': []
}>()

const textareaRef = ref<HTMLTextAreaElement | null>(null)
const showPlusMenu = ref(false)
const showInsertDialog = ref(false)
const targetRunIdDraft = ref('')

// mode_version selection (published modes)
const modes = ref<AgentModeTopologyDto[]>([])
const modeVersionId = ref<string | null>(null)
const meetingModel = ref<string>('')

// dispatch preference for Enter: parallel | queued | ask (default parallel)
const ENTER_PREF_KEY = 'tinadec.enter_pref'
type EnterPref = 'parallel' | 'queued' | 'ask'
const enterPref = ref<EnterPref>((localStorage.getItem(ENTER_PREF_KEY) as EnterPref) || 'parallel')
function setEnterPref(v: EnterPref) { enterPref.value = v; localStorage.setItem(ENTER_PREF_KEY, v) }

// runs for insert picker
const runOptions = ref<Array<{ id: string; status: string }>>([])

async function loadModes() {
  try {
    const list = await api.listAgentModeTopologies()
    modes.value = Array.isArray(list) ? list as AgentModeTopologyDto[] : []
    // pick first published or first as default
    if (!modeVersionId.value && modes.value[0]) modeVersionId.value = modes.value[0].id
  } catch { /* gateway may be offline */ }
}

async function loadRuns() {
  if (!props.sessionId) return
  try {
    const list = await api.listRuns(props.sessionId)
    runOptions.value = (Array.isArray(list) ? list : []).map((r) => ({ id: (r as { id: string }).id, status: String((r as { status: string }).status ?? '') }))
    if (props.runs?.length) runOptions.value = props.runs
  } catch { /* ignore */ }
}

onMounted(() => { loadModes(); loadRuns() })
watch(() => props.sessionId, loadRuns)

function autoResize() {
  const el = textareaRef.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = Math.min(el.scrollHeight, 200) + 'px'
}

function dispatchSubmit(mode: 'parallel' | 'queued' | 'insert') {
  if (mode === 'insert' && !targetRunIdDraft.value.trim()) {
    showInsertDialog.value = true
    return
  }
  emit('submit', {
    dispatch_mode: mode,
    target_run_id: mode === 'insert' ? targetRunIdDraft.value.trim() : null,
    mode_version_id: modeVersionId.value,
    meeting_model: meetingModel.value.trim() || null,
  })
  showInsertDialog.value = false
}

function handleKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault()
    if (enterPref.value === 'ask') {
      // ask: default to parallel but user must pick via buttons; we fallback to parallel for Enter
      dispatchSubmit('parallel')
      return
    }
    dispatchSubmit(enterPref.value === 'queued' ? 'queued' : 'parallel')
  }
}

function goToAgentSettings() {
  router.push('/agent-center')
}
</script>

<template>
  <div class="composer">
    <div class="composer-box">
      <div class="composer-main">
        <div class="composer-plus-wrapper">
          <UiDropdownMenu v-model:open="showPlusMenu" placement="top" class="plus-dropdown-menu">
            <template #trigger>
              <UiButton variant="ghost" size="icon" class="composer-plus">
                <Plus :size="14" />
              </UiButton>
            </template>
            <button class="plus-menu-item" @click="emit('add-image'); showPlusMenu = false">
              <Image :size="12" />
              <span>{{ t('chat.addImage') }}</span>
            </button>
            <button class="plus-menu-item" @click="emit('add-file'); showPlusMenu = false">
              <FileText :size="12" />
              <span>{{ t('chat.addFile') }}</span>
            </button>
          </UiDropdownMenu>
        </div>

        <textarea
          ref="textareaRef"
          :value="modelValue"
          class="composer-input"
          :placeholder="t('chat.placeholder')"
          rows="1"
          @input="emit('update:modelValue', ($event.target as HTMLTextAreaElement).value); autoResize()"
          @keydown="handleKeydown"
        />

        <UiButton
          variant="ghost"
          size="icon"
          class="composer-send"
          :disabled="busy || !modelValue.trim()"
          @click="dispatchSubmit(enterPref === 'queued' ? 'queued' : 'parallel')"
        >
          <ArrowUp :size="14" />
        </UiButton>
      </div>

      <!-- new selectors -->
      <div class="composer-selects">
        <div class="composer-select">
          <UiLabel class="composer-label">模式版本</UiLabel>
          <select v-model="modeVersionId" class="composer-select-input">
            <option :value="null">跟随默认</option>
            <option v-for="m in modes" :key="m.id" :value="m.id">{{ m.display_name }} {{ m.status === 'published' ? '· 默认' : '' }}</option>
          </select>
        </div>
        <div class="composer-select">
          <UiLabel class="composer-label">会议模型</UiLabel>
          <UiInput v-model="meetingModel" placeholder="可选" class="composer-model-input" />
        </div>
        <div class="composer-select">
          <UiLabel class="composer-label">回车</UiLabel>
          <select :value="enterPref" class="composer-select-input" @change="setEnterPref(($event.target as HTMLSelectElement).value as EnterPref)">
            <option value="parallel">并行</option>
            <option value="queued">排队</option>
            <option value="ask">询问</option>
          </select>
        </div>
      </div>

      <div class="composer-toolbar">
        <div class="composer-toolbar-left">
          <!-- ponytail: ModeSelector removed — mode_version controls routing -->
          <PermissionSelector
            :model-value="permission"
            @update:model-value="emit('update:permission', $event)"
          />
          <div class="dispatch-actions">
            <UiButton size="xs" :variant="enterPref==='parallel' ? 'default' : 'outline'" :disabled="busy" @click="dispatchSubmit('parallel')">并行</UiButton>
            <UiButton size="xs" :variant="enterPref==='queued' ? 'default' : 'outline'" :disabled="busy" @click="dispatchSubmit('queued')">排队</UiButton>
            <UiButton size="xs" variant="outline" :disabled="busy" @click="showInsertDialog = true">插入</UiButton>
          </div>
        </div>
        <div class="composer-toolbar-right">
          <button class="composer-agent-config" @click="goToAgentSettings">
            <Settings :size="11" />
            <span>{{ t('chat.agentConfig') }}</span>
          </button>
        </div>
      </div>

      <!-- insert picker dialog -->
      <div v-if="showInsertDialog" class="insert-dialog">
        <div class="insert-dialog-head"><strong>插入目标 Run</strong> <UiButton size="xs" variant="ghost" @click="showInsertDialog=false">×</UiButton></div>
        <select v-model="targetRunIdDraft" class="composer-select-input" style="width:100%">
          <option value="">选择目标 run</option>
          <option v-for="r in runOptions" :key="r.id" :value="r.id">{{ r.id.slice(0,8) }} · {{ r.status }}</option>
        </select>
        <UiInput v-model="targetRunIdDraft" placeholder="或手动输入 run_id" />
        <div class="dispatch-actions" style="margin-top:8px">
          <UiButton size="sm" :disabled="!targetRunIdDraft.trim()" @click="dispatchSubmit('insert')">确认插入</UiButton>
          <UiButton size="sm" variant="outline" @click="showInsertDialog=false">取消</UiButton>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.composer-selects { display:flex; gap:8px; flex-wrap:wrap; margin-top:8px; }
.composer-select { display:flex; align-items:center; gap:6px; }
.composer-label { font-size:11px; color:var(--text-muted); white-space:nowrap; }
.composer-select-input { height:28px; border:1px solid var(--border-muted); border-radius:6px; padding:0 8px; font-size:12px; background:var(--surface-raised); min-width:140px; }
.composer-model-input { height:28px; width:140px; font-size:12px; }
.dispatch-actions { display:flex; gap:6px; align-items:center; }
.insert-dialog { margin-top:8px; border:1px solid var(--border-muted); border-radius:8px; padding:10px; background:var(--surface-raised); display:grid; gap:8px; }
.insert-dialog-head { display:flex; align-items:center; justify-content:space-between; font-size:13px; }
</style>
