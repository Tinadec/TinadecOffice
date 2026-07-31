<script setup lang="ts">
import { cn } from '@/lib/utils'
import { ref, watch } from 'vue'

interface Props {
  open?: boolean
  class?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  'update:open': [value: boolean]
}>()

const isOpen = ref(props.open)

watch(() => props.open, (val) => {
  isOpen.value = val
})

function close() {
  isOpen.value = false
  emit('update:open', false)
}

function toggle() {
  isOpen.value = !isOpen.value
  emit('update:open', isOpen.value)
}
</script>

<template>
  <div class="relative">
    <div @click="toggle">
      <slot name="trigger" />
    </div>
    <div
      v-if="isOpen"
      :class="cn(
        'absolute z-50 w-72 rounded-md border bg-[var(--surface-raised)] p-4 text-popover-foreground shadow-md outline-none',
        props.class,
      )"
    >
      <slot />
    </div>
  </div>
</template>
