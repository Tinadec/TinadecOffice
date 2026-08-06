<script setup lang="ts">
import { cn } from '@/lib/utils'
import { Search } from '@lucide/vue'

interface Props {
  class?: string
  modelValue?: string
  placeholder?: string
  showSearch?: boolean
}

const props = withDefaults(defineProps<Props>(), {
  modelValue: '',
  placeholder: 'Type a command or search...',
  showSearch: true,
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()
</script>

<template>
  <div
    :class="cn(
      'flex h-full w-full flex-col overflow-hidden rounded-md bg-[var(--surface-raised)] text-popover-foreground',
      props.class,
    )"
  >
    <div v-if="showSearch" class="flex items-center border-b px-3">
      <Search class="mr-2 h-4 w-4 shrink-0 opacity-50" />
      <input
        class="flex h-10 w-full rounded-md bg-[var(--surface-input)] py-3 text-sm outline-none placeholder:text-muted-foreground disabled:cursor-not-allowed disabled:opacity-50"
        role="searchbox"
        :placeholder="placeholder"
        :value="modelValue"
        @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)"
      />
    </div>
    <div class="max-h-[300px] overflow-y-auto overflow-x-hidden">
      <slot />
    </div>
  </div>
</template>
