<script setup lang="ts">
import { ref, computed, nextTick, onMounted, onUnmounted } from 'vue'
import { ChevronDown, Map, FileSearch, HelpCircle, Sparkles, Zap, Network } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { AgentMode } from '@/types/mode'

const { t } = useI18n()

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
}>()

const emit = defineEmits<{
  'update:modelValue': [value: AgentMode]
}>()

const showDropdown = ref(false)
const triggerRef = ref<HTMLElement | null>(null)
const dropdownStyle = ref<Record<string, string>>({})

const currentMode = computed(() => modes.find(m => m.key === props.modelValue) ?? modes[0])

function updateDropdownPosition() {
  const trigger = triggerRef.value
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  dropdownStyle.value = {
    position: 'fixed',
    top: `${rect.bottom + 6}px`,
    left: `${rect.left}px`,
    minWidth: '180px',
  }
}

async function toggleDropdown() {
  showDropdown.value = !showDropdown.value
  if (showDropdown.value) {
    await nextTick()
    updateDropdownPosition()
  }
}

function selectMode(key: AgentMode) {
  emit('update:modelValue', key)
  showDropdown.value = false
}

function handleClickOutside(event: MouseEvent) {
  const target = event.target as HTMLElement
  if (!target.closest('.mode-selector-trigger') && !target.closest('.mode-selector-portal')) {
    showDropdown.value = false
  }
}

onMounted(() => document.addEventListener('click', handleClickOutside))
onUnmounted(() => document.removeEventListener('click', handleClickOutside))
</script>

<template>
  <div class="mode-selector">
    <button
      ref="triggerRef"
      class="mode-selector-trigger"
      @click="toggleDropdown"
    >
      <component :is="currentMode.icon" :size="14" />
      <span class="mode-selector-label">{{ currentMode.label }}</span>
      <ChevronDown :size="12" class="mode-selector-chevron" />
    </button>

    <Teleport to="body">
      <div
        v-if="showDropdown"
        class="mode-selector-portal"
        :style="dropdownStyle"
      >
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
      </div>
    </Teleport>
  </div>
</template>
