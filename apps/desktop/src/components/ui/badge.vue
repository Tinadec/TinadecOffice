<script setup lang="ts">
import { cn } from '@/lib/utils'
import { cva, type VariantProps } from 'class-variance-authority'
import { computed } from 'vue'

const badgeVariants = cva(
  'inline-flex shrink-0 items-center gap-1 whitespace-nowrap rounded-[var(--radius-control,6px)] border px-2 py-0.5 text-[11px] font-medium leading-[1.35] tracking-[0.005em] tabular-nums transition-colors duration-150 motion-reduce:transition-none focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--focus-ring-color,var(--accent-primary))] focus-visible:ring-offset-1',
  {
    variants: {
      variant: {
        default: 'border-transparent bg-[var(--bg-primary-button,hsl(var(--primary)))] text-[var(--text-on-primary,#ffffff)] shadow-[inset_0_1px_0_rgba(255,255,255,0.14)]',
        secondary: 'border-[var(--border-muted)] bg-[var(--surface-button)] text-[var(--text-primary)]',
        destructive: 'border-transparent bg-destructive text-destructive-foreground',
        outline: 'border-[var(--border-default)] bg-transparent text-[var(--text-secondary)]',
      },
    },
    defaultVariants: {
      variant: 'default',
    },
  },
)

type BadgeVariants = VariantProps<typeof badgeVariants>

interface Props {
  variant?: BadgeVariants['variant']
  class?: string
}

const props = defineProps<Props>()

const classes = computed(() => cn(badgeVariants({ variant: props.variant }), props.class))
</script>

<template vapor>
  <div data-slot="badge" :data-variant="variant ?? 'default'" :class="classes">
    <slot />
  </div>
</template>
