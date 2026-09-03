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
    'peer grid h-4 w-4 shrink-0 place-items-center rounded-[6px] border border-primary bg-transparent shadow-sm transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50 data-[state=checked]:border-primary data-[state=checked]:bg-primary data-[state=checked]:text-primary-foreground',
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
      class="h-3 w-3"
    />
    <Check
      v-else-if="modelValue"
      class="h-3 w-3"
    />
  </button>
</template>
