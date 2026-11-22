<script setup lang="ts">
import { cn, type ClassValue } from '@/lib/utils'
import { cva, type VariantProps } from 'class-variance-authority'
import { computed } from 'vue'

const buttonVariants = cva(
  'ui-button inline-flex shrink-0 items-center justify-center gap-1.5 whitespace-nowrap rounded-[var(--radius-control,6px)] text-[13px] font-medium transition-[background-color,border-color,color,transform,box-shadow] duration-150 ease-out active:scale-[0.985] motion-reduce:transition-none motion-reduce:active:scale-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--focus-ring-color,var(--accent-primary))] focus-visible:ring-offset-1 focus-visible:ring-offset-transparent disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:size-4 [&_svg]:shrink-0',
  {
    variants: {
      variant: {
        default: 'bg-[var(--bg-primary-button,hsl(var(--primary)))] text-[var(--text-on-primary,#ffffff)] shadow-[inset_0_1px_0_rgba(255,255,255,0.16),0_1px_2px_rgba(0,0,0,0.12)] hover:bg-[var(--bg-primary-button-hover,hsl(var(--primary)/0.9))]',
        destructive: 'bg-destructive text-destructive-foreground shadow-[inset_0_1px_0_rgba(255,255,255,0.14),0_1px_2px_rgba(0,0,0,0.12)] hover:bg-destructive/90',
        outline: 'border border-[var(--border-default)] bg-[var(--surface-button)] text-[var(--text-primary)] shadow-[0_1px_1px_rgba(0,0,0,0.04)] hover:bg-[var(--surface-button-hover)] hover:border-[var(--border-input-focus)]',
        secondary: 'border border-[var(--border-muted)] bg-[var(--surface-button)] text-[var(--text-primary)] hover:bg-[var(--surface-button-hover)] hover:border-[var(--border-default)]',
        ghost: 'text-[var(--text-secondary)] hover:bg-[var(--surface-hover)] hover:text-[var(--text-primary)]',
        link: 'text-[var(--text-link,var(--accent-primary))] underline-offset-4 hover:underline',
      },
      size: {
        default: 'h-[var(--control-height-md,34px)] px-3.5 py-1.5',
        xs: 'h-[var(--control-height-xs,26px)] rounded-[var(--radius-control,6px)] px-2 text-[11px]',
        sm: 'h-[var(--control-height-sm,30px)] rounded-[var(--radius-control,6px)] px-3 text-xs',
        lg: 'h-[var(--control-height-lg,40px)] rounded-[var(--radius-control-md,8px)] px-6 text-[13px]',
        icon: 'h-[var(--control-height-md,34px)] w-[var(--control-height-md,34px)] rounded-[var(--radius-control,6px)]',
      },
    },
    defaultVariants: {
      variant: 'default',
      size: 'default',
    },
  },
)

type ButtonVariants = VariantProps<typeof buttonVariants>

interface Props {
  variant?: ButtonVariants['variant']
  size?: ButtonVariants['size']
  class?: ClassValue
  disabled?: boolean
  type?: 'button' | 'submit' | 'reset'
}

const props = withDefaults(defineProps<Props>(), {
  variant: 'default',
  size: 'default',
  type: 'button',
})

const emit = defineEmits<{
  click: [event: MouseEvent]
}>()

const classes = computed(() => cn(buttonVariants({ variant: props.variant, size: props.size }), props.class))
</script>

<template>
  <button
    :type="type"
    :data-variant="variant"
    :data-size="size"
    :class="classes"
    :disabled="disabled"
    @click="emit('click', $event)"
  >
    <slot />
  </button>
</template>

<style scoped>
/* Preserve 1px hairlines over the global unlayered `button { border: 0 }` reset. */
button.ui-button[data-variant="outline"] {
  border: 1px solid var(--border-default);
}

button.ui-button[data-variant="outline"]:hover:not(:disabled) {
  border-color: var(--border-input-focus);
}

button.ui-button[data-variant="secondary"] {
  border: 1px solid var(--border-muted);
}

button.ui-button[data-variant="secondary"]:hover:not(:disabled) {
  border-color: var(--border-default);
}
</style>
