<script setup lang="ts">
import { cn } from '@/lib/utils'
import { computed } from 'vue'

interface Props {
  modelValue?: string
  placeholder?: string
  disabled?: boolean
  class?: string
  rows?: number
}

const props = withDefaults(defineProps<Props>(), {
  rows: 3,
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
  keydown: [event: KeyboardEvent]
}>()

const classes = computed(() =>
  cn(
    'ui-textarea flex min-h-[68px] w-full min-w-0 rounded-[var(--radius-control,6px)] border border-[var(--border-input,var(--border-default))] bg-[var(--surface-input)] px-3 py-2 text-[13px] leading-[1.5] text-[var(--text-primary)] transition-[border-color,background-color,box-shadow] duration-150 ease-out motion-reduce:transition-none placeholder:text-[var(--text-muted)] hover:border-[var(--border-input-focus)] focus-visible:border-[var(--border-input-focus)] focus-visible:outline-none focus-visible:shadow-[var(--focus-ring,0_0_0_2px_color-mix(in_srgb,var(--accent-primary)_22%,transparent))] disabled:cursor-not-allowed disabled:opacity-50',
    props.class,
  ),
)

function onInput(event: Event) {
  emit('update:modelValue', (event.target as HTMLTextAreaElement).value)
}
</script>

<template>
  <textarea
    :value="modelValue"
    :placeholder="placeholder"
    :disabled="disabled"
    :rows="rows"
    :class="classes"
    @input="onInput"
    @keydown="emit('keydown', $event)"
  />
</template>
