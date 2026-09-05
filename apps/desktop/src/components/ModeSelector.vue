<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'
import { ChevronDown, FileSearch, HelpCircle, Map, Sparkles, Zap, Network } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { api, type AgentModeTopologyDto } from '@/api'
import type { AgentMode } from '@/types/mode'

const { t } = useI18n()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const panelStyle = computed(() => getPanelStyle())
const panelDataAttrs = computed(() => getPanelDataAttributes())

interface ModeOption {
  key: AgentMode
  label: string
  icon: any
}

const modes = computed<ModeOption[]>(() => [
  { key: 'plan', label: t('mode.plan'), icon: Map },
  { key: 'spec', label: t('mode.spec'), icon: FileSearch },
  { key: 'ask', label: t('mode.ask'), icon: HelpCircle },
  { key: 'vibe', label: t('mode.vibe'), icon: Sparkles },
  { key: 'auto', label: t('mode.auto'), icon: Zap },
  { key: 'agent', label: t('mode.agent'), icon: Network },
])

const props = defineProps<{
  modelValue: AgentMode
  /** Explicit published ModeVersion override; null = follow the agent-mode default. */
  modeVersionId?: string | null
}>()

const emit = defineEmits<{
  'update:modelValue': [value: AgentMode]
  'update:modeVersionId': [value: string | null]
}>()

const showDropdown = ref(false)
const triggerRef = ref<HTMLElement | null>(null)
const dropdownStyle = ref<Record<string, string>>({})

const currentMode = computed(() => modes.value.find(m => m.key === props.modelValue) ?? modes.value[0])

// ONE mode selector: the dropdown merges the agent-mode fallback ("follow
// default") with the workspace's published ModeVersions. When the workspace
// has no published versions yet (pack not installed), it falls back to the
// plain agent-mode enum list.
const modeVersions = ref<AgentModeTopologyDto[]>([])
const selectedVersion = computed(() =>
  modeVersions.value.find(v => v.id === props.modeVersionId) ?? null
)
const triggerLabel = computed(() => selectedVersion.value?.display_name ?? currentMode.value.label)

async function loadModeVersions() {
  try {
    const list = await api.listAgentModeTopologies()
    modeVersions.value = Array.isArray(list) ? (list as AgentModeTopologyDto[]) : []
  } catch { /* gateway offline */ }
}

function selectVersion(id: string | null) {
  emit('update:modeVersionId', id)
  showDropdown.value = false
}

function selectMode(key: AgentMode) {
  emit('update:modelValue', key)
  showDropdown.value = false
}

function updateDropdownPosition() {
  const trigger = triggerRef.value
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  const vh = window.innerHeight
  const vw = window.innerWidth
  const ddWidth = 180
  const estH = 220
  const spaceBelow = vh - rect.bottom
  const spaceAbove = rect.top
  const flip = spaceBelow < estH && spaceAbove > spaceBelow
  const left = Math.max(8, Math.min(rect.left, vw - ddWidth - 8))
  if (flip) {
    dropdownStyle.value = {
      position: 'fixed',
      bottom: `${vh - rect.top + 6}px`,
      left: `${left}px`,
      minWidth: '180px',
      maxHeight: `${Math.min(280, spaceAbove - 12)}px`,
      overflowY: 'auto',
    }
  } else {
    dropdownStyle.value = {
      position: 'fixed',
      top: `${rect.bottom + 6}px`,
      left: `${left}px`,
      minWidth: '180px',
      maxHeight: `${Math.min(280, spaceBelow - 12)}px`,
      overflowY: 'auto',
    }
  }
}

async function toggleDropdown() {
  showDropdown.value = !showDropdown.value
  if (showDropdown.value) {
    await nextTick()
    updateDropdownPosition()
  }
}

function handleClickOutside(event: MouseEvent) {
  const target = event.target as HTMLElement
  if (!target.closest('.mode-selector-trigger') && !target.closest('.mode-selector-portal')) {
    showDropdown.value = false
  }
}

onMounted(() => {
  document.addEventListener('click', handleClickOutside)
  void loadModeVersions()
})
onUnmounted(() => document.removeEventListener('click', handleClickOutside))
</script>

<template>
  <div class="mode-selector">
    <button
      ref="triggerRef"
      class="mode-selector-trigger"
      :title="t('chat.modeVersion')"
      @click="toggleDropdown"
    >
      <component :is="selectedVersion ? Sparkles : currentMode.icon" :size="14" />
      <span class="mode-selector-label">{{ triggerLabel }}</span>
      <ChevronDown :size="12" class="mode-selector-chevron" />
    </button>

    <Teleport to="body">
      <div
        v-if="showDropdown"
        class="mode-selector-portal"
        :style="[dropdownStyle, panelStyle]"
        v-bind="panelDataAttrs"
      >
        <template v-if="modeVersions.length">
          <button
            class="mode-selector-item"
            :class="{ active: !modeVersionId }"
            @click="selectVersion(null)"
          >
            <component :is="currentMode.icon" :size="14" />
            <span>{{ t('chat.followDefault') }}</span>
          </button>
          <div class="mode-selector-separator" />
          <button
            v-for="m in modeVersions"
            :key="m.id"
            class="mode-selector-item"
            :class="{ active: m.id === modeVersionId }"
            @click="selectVersion(m.id)"
          >
            <Sparkles :size="14" />
            <span>{{ m.display_name }}{{ m.status === 'published' ? ' · 默认' : '' }}</span>
          </button>
        </template>
        <template v-else>
          <button
            v-for="mode in modes"
            :key="mode.key"
            class="mode-selector-item"
            :class="{ active: mode.key === modelValue }"
            @click="selectMode(mode.key)"
          >
            <component :is="mode.icon" :size="14" />
            <span>{{ mode.label }}</span>
          </button>
        </template>
      </div>
    </Teleport>
  </div>
</template>
