<script setup lang="ts">
import { cn } from '@/lib/utils'
import { Check, Minus } from '@lucide/vue'
import { computed } from 'vue'

interface Props {
  modelValue?: boolean
  /** Tri-state: shows a dash while keeping checked semantics for the parent. */
  indeterminate?: boolean
  disabled?: boolean
  ariaLabel?: string
  class?: string
}

const props = withDefaults(defineProps<Props>(), {
  modelValue: false,
  indeterminate: false,
  disabled: false,
})

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
}>()

const classes = computed(() =>
  cn(
    'peer grid h-4 w-4 shrink-0 place-items-center rounded-[5px] border border-[var(--border-default)] bg-[var(--surface-input)] transition-[background-color,border-color,color,transform,box-shadow] duration-150 ease-out hover:border-[var(--border-input-focus)] active:scale-95 motion-reduce:transition-none motion-reduce:active:scale-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--focus-ring-color,var(--accent-primary))] focus-visible:ring-offset-1 focus-visible:ring-offset-transparent disabled:cursor-not-allowed disabled:opacity-50 data-[state=checked]:border-transparent data-[state=checked]:bg-[var(--bg-primary-button,hsl(var(--primary)))] data-[state=checked]:text-[var(--text-on-primary,#ffffff)]',
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
    role="checkbox"
    :aria-checked="indeterminate ? 'mixed' : modelValue"
    :aria-label="ariaLabel"
    :data-state="modelValue || indeterminate ? 'checked' : 'unchecked'"
    :disabled="disabled"
    :class="classes"
    @click.stop="toggle"
  >
    <Minus
      v-if="indeterminate && !modelValue"
      class="ui-check-icon h-3 w-3"
    />
    <Check
      v-else-if="modelValue"
      class="ui-check-icon h-3 w-3"
    />
  </button>
</template>

<style scoped>
/* The global unlayered `button { border: 0 }` beats Tailwind's layered border utility,
   which left the unchecked box fully transparent. */
button[role="checkbox"] {
  border: 1px solid var(--border-default);
}

button[role="checkbox"]:hover:not(:disabled) {
  border-color: var(--border-input-focus);
}

button[role="checkbox"][data-state="checked"] {
  border-color: transparent;
}

@keyframes ui-check-pop {
  from {
    opacity: 0;
    transform: scale(0.55);
  }
  to {
    opacity: 1;
    transform: scale(1);
  }
}

.ui-check-icon {
  animation: ui-check-pop 150ms var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1)) both;
}

@media (prefers-reduced-motion: reduce) {
  .ui-check-icon {
    animation: none;
  }
}
</style>
