<script setup lang="ts">
import { cn } from '@/lib/utils'
import { ref, watch } from 'vue'

interface Props {
  modelValue?: string
  type?: 'single' | 'multiple'
  class?: string
}

const props = withDefaults(defineProps<Props>(), {
  type: 'single',
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const activeValue = ref(props.modelValue)

watch(() => props.modelValue, (val) => {
  activeValue.value = val
})

function toggle(value: string) {
  if (activeValue.value === value) {
    activeValue.value = ''
  } else {
    activeValue.value = value
  }
  emit('update:modelValue', activeValue.value)
}
</script>

<template>
  <div :class="cn('inline-flex items-center justify-center rounded-md bg-[var(--surface-section)] p-1 gap-1', props.class)">
    <slot :toggle="toggle" :active-value="activeValue" />
  </div>
</template>
