<script setup lang="ts">
import { cn } from '@/lib/utils'
import { ref, watch } from 'vue'

interface Props {
  modelValue?: string
  class?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const activeTab = ref(props.modelValue)

watch(() => props.modelValue, (val) => {
  activeTab.value = val
})

function setTab(value: string) {
  activeTab.value = value
  emit('update:modelValue', value)
}
</script>

<template>
  <div :class="cn('flex flex-col gap-2', props.class)">
    <div class="inline-flex h-9 items-center justify-center rounded-lg bg-[var(--surface-section)] p-1 text-muted-foreground">
      <slot :active-tab="activeTab" :set-tab="setTab" />
    </div>
    <div class="mt-2">
      <slot name="content" :active-tab="activeTab" />
    </div>
  </div>
</template>
