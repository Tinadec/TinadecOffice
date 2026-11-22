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
    <div
      role="tablist"
      data-slot="tabs-list"
      class="ui-tabs-list inline-flex h-9 w-fit items-center justify-center gap-0.5 rounded-[var(--radius-control-md,8px)] border border-[var(--border-muted)] bg-[var(--surface-section)] p-[3px] text-[var(--text-secondary)]"
    >
      <slot :active-tab="activeTab" :set-tab="setTab" />
    </div>
    <div v-if="$slots.content" class="mt-2">
      <slot name="content" :active-tab="activeTab" />
    </div>
  </div>
</template>

<style scoped>
.ui-tabs-list :deep(button),
.ui-tabs-list :deep([role="tab"]) {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  height: 100%;
  padding: 0 10px;
  border-radius: calc(var(--radius-control-md, 8px) - 2px);
  border: 1px solid transparent;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: background-color 150ms ease-out,
              border-color 150ms ease-out,
              color 150ms ease-out,
              box-shadow 150ms ease-out,
              transform 120ms ease-out;
}

.ui-tabs-list :deep(button:hover:not(:disabled)),
.ui-tabs-list :deep([role="tab"]:hover:not(:disabled)) {
  color: var(--text-primary);
}

.ui-tabs-list :deep(button:active:not(:disabled)),
.ui-tabs-list :deep([role="tab"]:active:not(:disabled)) {
  transform: scale(0.985);
}

.ui-tabs-list :deep([data-state="active"]),
.ui-tabs-list :deep([aria-selected="true"]),
.ui-tabs-list :deep(.active) {
  background: var(--surface-raised);
  border-color: var(--border-default);
  color: var(--text-primary);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.08);
}

@media (prefers-reduced-motion: reduce) {
  .ui-tabs-list :deep(button),
  .ui-tabs-list :deep([role="tab"]) {
    transition: none;
  }

  .ui-tabs-list :deep(button:active:not(:disabled)),
  .ui-tabs-list :deep([role="tab"]:active:not(:disabled)) {
    transform: none;
  }
}
</style>
