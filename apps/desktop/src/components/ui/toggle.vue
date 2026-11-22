<script setup lang="ts">
import { cn } from '@/lib/utils'
import { cva, type VariantProps } from 'class-variance-authority'
import { computed } from 'vue'

const toggleVariants = cva(
  'ui-toggle inline-flex shrink-0 items-center justify-center gap-1.5 rounded-[var(--radius-control,6px)] border border-transparent text-[13px] font-medium text-[var(--text-secondary)] transition-[background-color,border-color,color,transform,box-shadow] duration-150 ease-out hover:bg-[var(--surface-hover)] hover:text-[var(--text-primary)] active:scale-[0.985] motion-reduce:transition-none motion-reduce:active:scale-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--focus-ring-color,var(--accent-primary))] focus-visible:ring-offset-1 focus-visible:ring-offset-transparent disabled:pointer-events-none disabled:opacity-50 data-[state=on]:border-[var(--border-default)] data-[state=on]:bg-[var(--surface-raised)] data-[state=on]:text-[var(--text-primary)] data-[state=on]:shadow-[0_1px_2px_rgba(0,0,0,0.08)]',
  {
    variants: {
      variant: {
        default: 'bg-transparent',
        outline: 'border-[var(--border-default)] bg-[var(--surface-button)] hover:bg-[var(--surface-button-hover)] hover:border-[var(--border-input-focus)]',
      },
      size: {
        default: 'h-[var(--control-height-sm,30px)] px-2.5',
        sm: 'h-[var(--control-height-xs,26px)] px-2 text-xs',
        lg: 'h-[var(--control-height-md,34px)] px-3.5',
      },
    },
    defaultVariants: {
      variant: 'default',
      size: 'default',
    },
  },
)

type ToggleVariants = VariantProps<typeof toggleVariants>

interface Props {
  pressed?: boolean
  variant?: ToggleVariants['variant']
  size?: ToggleVariants['size']
  disabled?: boolean
  class?: string
}

const props = withDefaults(defineProps<Props>(), {
  variant: 'default',
  size: 'default',
})

const emit = defineEmits<{
  'update:pressed': [value: boolean]
}>()

const classes = computed(() => cn(toggleVariants({ variant: props.variant, size: props.size }), props.class))

function toggle() {
  if (props.disabled) return
  emit('update:pressed', !props.pressed)
}
</script>

<template>
  <button
    type="button"
    :aria-pressed="Boolean(pressed)"
    :data-state="pressed ? 'on' : 'off'"
    :data-variant="variant"
    :disabled="disabled"
    :class="classes"
    @click="toggle"
  >
    <slot />
  </button>
</template>

<style scoped>
/* Preserve 1px borders over global unlayered `button { border: 0 }` reset. */
button.ui-toggle[data-variant="outline"],
button.ui-toggle[data-state="on"] {
  border: 1px solid var(--border-default);
}

button.ui-toggle[data-variant="outline"]:hover:not(:disabled) {
  border-color: var(--border-input-focus);
}
</style>
