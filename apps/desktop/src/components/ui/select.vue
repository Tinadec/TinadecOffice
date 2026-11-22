<script setup lang="ts">
import { cn } from '@/lib/utils'
import { ChevronDown } from '@lucide/vue'
import { ref, watch } from 'vue'
import i18n from '@/i18n'

interface Props {
  modelValue?: string
  placeholder?: string
  displayValue?: string
  ariaLabel?: string
  class?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const isOpen = ref(false)
const selectedValue = ref(props.modelValue)

watch(() => props.modelValue, (val) => {
  selectedValue.value = val
})

function select(value: string) {
  selectedValue.value = value
  emit('update:modelValue', value)
  isOpen.value = false
}
</script>

<template>
  <div class="relative">
    <button
      type="button"
      :class="cn(
        'flex h-[var(--control-height-md,34px)] w-full min-w-0 items-center justify-between gap-2 rounded-[var(--radius-control,6px)] border border-[var(--border-input)] bg-[var(--surface-input)] px-3 py-1.5 text-[13px] text-[var(--text-primary)] transition-colors placeholder:text-[var(--text-muted)] hover:bg-[var(--surface-hover)] focus-visible:border-[var(--border-input-focus)] focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-[var(--focus-ring-color,var(--accent-primary))] disabled:cursor-not-allowed disabled:opacity-50',
        props.class,
      )"
      :aria-label="ariaLabel"
      @click="isOpen = !isOpen"
    >
      <span v-if="selectedValue" class="min-w-0 truncate">{{ displayValue || selectedValue }}</span>
      <span v-else class="min-w-0 truncate text-[var(--text-muted)]">{{ placeholder || i18n.global.t('common.selectPlaceholder') }}</span>
      <ChevronDown class="h-4 w-4 shrink-0 opacity-60" />
    </button>
    <div
      v-if="isOpen"
      class="absolute z-50 mt-1 max-h-60 w-full overflow-auto rounded-[var(--radius-control-md,8px)] border border-[var(--border-muted)] bg-[var(--surface-raised)] text-[var(--text-primary)] shadow-[var(--shadow-panel)]"
    >
      <slot :select="select" :selected-value="selectedValue" />
    </div>
  </div>
</template>
