<script setup lang="ts">
import { cn } from '@/lib/utils'
import { cva, type VariantProps } from 'class-variance-authority'
import { computed } from 'vue'

const toggleVariants = cva(
  'inline-flex items-center justify-center rounded-md text-sm font-medium transition-colors hover:bg-[var(--surface-hover)] hover:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-50 data-[state=on]:bg-[var(--surface-selected)] data-[state=on]:text-accent-foreground',
  {
    variants: {
      variant: {
        default: 'bg-transparent',
        outline: 'border border-input bg-transparent shadow-sm hover:bg-[var(--surface-hover)] hover:text-accent-foreground',
      },
      size: {
        default: 'h-9 px-3',
        sm: 'h-8 px-2',
        lg: 'h-10 px-3',
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
  class?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  'update:pressed': [value: boolean]
}>()

const classes = computed(() => cn(toggleVariants({ variant: props.variant, size: props.size }), props.class))

function toggle() {
  emit('update:pressed', !props.pressed)
}
</script>

<template>
  <button
    type="button"
    :aria-pressed="pressed"
    :data-state="pressed ? 'on' : 'off'"
    :class="classes"
    @click="toggle"
  >
    <slot />
  </button>
</template>
