<script setup lang="ts">
import { cn } from '@/lib/utils'
import { ref, watch } from 'vue'

interface Props {
  modelValue?: string
  type?: 'single' | 'multiple'
  class?: string
}

const props = withDefaults(defineProps<Props>(), {
  type: 'single',
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const activeValue = ref(props.modelValue)

watch(() => props.modelValue, (val) => {
  activeValue.value = val
})

function toggle(value: string) {
  if (activeValue.value === value) {
    activeValue.value = ''
  } else {
    activeValue.value = value
  }
  emit('update:modelValue', activeValue.value)
}
</script>

<template>
  <div
    role="group"
    data-slot="toggle-group"
    :class="cn(
      'ui-toggle-group inline-flex items-center justify-center gap-0.5 rounded-[var(--radius-control-md,8px)] border border-[var(--border-muted)] bg-[var(--surface-section)] p-[3px] text-[var(--text-secondary)]',
      props.class,
    )"
  >
    <slot :toggle="toggle" :active-value="activeValue" />
  </div>
</template>

<style scoped>
.ui-toggle-group :deep([data-state="on"]),
.ui-toggle-group :deep([aria-pressed="true"]),
.ui-toggle-group :deep(.active) {
  background: var(--surface-raised);
  border: 1px solid var(--border-default);
  color: var(--text-primary);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.08);
}
</style>
