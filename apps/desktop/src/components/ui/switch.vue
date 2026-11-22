<script setup lang="ts">
import { cn } from '@/lib/utils'
import { computed } from 'vue'

interface Props {
  modelValue?: boolean
  disabled?: boolean
  ariaLabel?: string
  class?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
}>()

const classes = computed(() =>
  cn(
    'ui-switch peer inline-flex h-5 w-9 shrink-0 cursor-pointer items-center rounded-full border border-[var(--border-default)] px-[2px] transition-[background-color,border-color,box-shadow] duration-150 ease-out motion-reduce:transition-none hover:border-[var(--border-input-focus)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--focus-ring-color,var(--accent-primary))] focus-visible:ring-offset-1 focus-visible:ring-offset-transparent disabled:cursor-not-allowed disabled:opacity-50 data-[state=unchecked]:bg-[color-mix(in_srgb,var(--text-primary)_10%,var(--surface-input))] data-[state=checked]:border-transparent data-[state=checked]:bg-[var(--bg-primary-button,hsl(var(--primary)))]',
    props.class,
  ),
)

function toggle() {
  if (props.disabled) return
  emit('update:modelValue', !props.modelValue)
}
</script>

<template>
  <button
    type="button"
    role="switch"
    :aria-checked="Boolean(modelValue)"
    :aria-label="ariaLabel"
    :disabled="disabled"
    :data-state="modelValue ? 'checked' : 'unchecked'"
    :class="classes"
    @click="toggle"
  >
    <span
      data-slot="switch-thumb"
      :data-state="modelValue ? 'checked' : 'unchecked'"
      :class="cn(
        'pointer-events-none block h-3.5 w-3.5 rounded-full bg-white shadow-[0_1px_2px_rgba(0,0,0,0.22)] ring-0 transition-[transform,background-color] duration-200 ease-[cubic-bezier(0.22,1.28,0.36,1)] motion-reduce:transition-none dark:bg-zinc-200 data-[state=checked]:translate-x-4 data-[state=checked]:bg-[var(--text-on-primary,#ffffff)] data-[state=unchecked]:translate-x-0',
      )"
    />
  </button>
</template>

<style scoped>
/* Preserve 1px neutral track border over the global unlayered `button { border: 0 }` reset. */
button[role="switch"] {
  border: 1px solid var(--border-default);
}

button[role="switch"]:hover:not(:disabled) {
  border-color: var(--border-input-focus);
}

button[role="switch"][data-state="checked"] {
  border-color: transparent;
}
</style>
