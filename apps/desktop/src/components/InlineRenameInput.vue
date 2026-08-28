<script setup lang="ts">
import { nextTick, onMounted, ref } from 'vue'

interface Props {
  modelValue: string
  placeholder?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  submit: [value: string]
  cancel: []
}>()

const value = ref(props.modelValue)
const inputRef = ref<HTMLInputElement | null>(null)
let cancelled = false

onMounted(() => {
  void nextTick(() => {
    inputRef.value?.focus()
    inputRef.value?.select()
  })
})

function submit() {
  if (cancelled) return
  const trimmed = value.value.trim()
  if (!trimmed || trimmed === props.modelValue) {
    emit('cancel')
    return
  }
  emit('submit', trimmed)
}

function cancel() {
  cancelled = true
  emit('cancel')
}

function onKeyDown(event: KeyboardEvent) {
  if (event.key === 'Enter') {
    event.preventDefault()
    inputRef.value?.blur()
  } else if (event.key === 'Escape') {
    event.stopPropagation()
    cancel()
  }
}
</script>

<template>
  <input
    ref="inputRef"
    v-model="value"
    class="inline-rename-input"
    type="text"
    :placeholder="placeholder"
    @keydown="onKeyDown"
    @blur="submit"
    @click.stop
    @dblclick.stop
  />
</template>

<style scoped>
.inline-rename-input {
  width: 100%;
  min-width: 0;
  padding: 0 4px;
  border: 1px solid var(--border-focus, rgba(94, 152, 255, 0.65));
  border-radius: 4px;
  background: var(--surface-input, rgba(18, 20, 24, 0.72));
  color: inherit;
  font-size: inherit;
  line-height: inherit;
  outline: none;
}
</style>
