<script setup lang="ts">
import { cn } from '@/lib/utils'
import { ChevronDown } from '@lucide/vue'
import { ref, watch } from 'vue'

interface Props {
  modelValue?: string
  placeholder?: string
  class?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const isOpen = ref(false)
const selectedValue = ref(props.modelValue)

watch(() => props.modelValue, (val) => {
  selectedValue.value = val
})

function select(value: string) {
  selectedValue.value = value
  emit('update:modelValue', value)
  isOpen.value = false
}
</script>

<template>
  <div class="relative">
    <button
      type="button"
      :class="cn(
        'flex h-9 w-full items-center justify-between rounded-md border border-input bg-[var(--surface-input)] px-3 py-2 text-sm shadow-sm ring-offset-background placeholder:text-muted-foreground focus:outline-none focus:ring-1 focus:ring-ring disabled:cursor-not-allowed disabled:opacity-50',
        props.class,
      )"
      @click="isOpen = !isOpen"
    >
      <span v-if="selectedValue" class="truncate">{{ selectedValue }}</span>
      <span v-else class="text-muted-foreground">{{ placeholder || 'Select...' }}</span>
      <ChevronDown class="h-4 w-4 opacity-50" />
    </button>
    <div
      v-if="isOpen"
      class="absolute z-50 mt-1 max-h-60 w-full overflow-auto rounded-md border bg-[var(--surface-raised)] text-popover-foreground shadow-md"
    >
      <slot :select="select" :selected-value="selectedValue" />
    </div>
  </div>
</template>
