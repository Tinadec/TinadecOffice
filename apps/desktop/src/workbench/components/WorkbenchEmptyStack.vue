<script setup lang="ts">
import { computed, ref } from 'vue'
import { Plus } from '@lucide/vue'
import { UiButton, UiCommand, UiDropdownMenu, UiTooltip } from '@/components/ui'
import type { WorkbenchCardDescriptor, WorkbenchRect } from '../types'

const props = defineProps<{
  rect: WorkbenchRect
  availableCards: readonly WorkbenchCardDescriptor[]
  panelStyle: Record<string, string>
  panelDataAttributes: Record<string, string>
}>()

const emit = defineEmits<{
  open: [type: string]
}>()

const moduleQuery = ref('')
const filteredCards = computed(() => {
  const query = moduleQuery.value.trim().toLocaleLowerCase()
  if (!query) return props.availableCards
  return props.availableCards.filter((descriptor) =>
    `${descriptor.title} ${descriptor.type}`.toLocaleLowerCase().includes(query),
  )
})
</script>

<template>
  <div
    class="workbench-empty-stack"
    :style="{
      left: `${rect.x}px`,
      top: `${rect.y}px`,
      width: `${rect.width}px`,
      height: `${rect.height}px`,
      ...panelStyle,
    }"
    v-bind="panelDataAttributes"
  >
    <UiDropdownMenu class="left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-52">
      <template #trigger>
        <UiTooltip content="Open module">
          <UiButton variant="outline" size="icon" aria-label="Open module">
            <Plus />
          </UiButton>
        </UiTooltip>
      </template>
      <UiCommand v-model="moduleQuery" class="h-auto bg-transparent" placeholder="Find a module...">
      <div class="workbench-module-menu">
        <UiButton
          v-for="descriptor in filteredCards"
          :key="descriptor.type"
          variant="ghost"
          size="sm"
          @click="emit('open', descriptor.type)"
        >
          <component :is="descriptor.icon" data-icon="inline-start" />
          {{ descriptor.title }}
        </UiButton>
        <p v-if="filteredCards.length === 0" class="workbench-module-empty">No matching modules</p>
      </div>
      </UiCommand>
    </UiDropdownMenu>
  </div>
</template>

<style scoped>
.workbench-empty-stack {
  position: absolute;
  display: grid;
  place-items: center;
  border: 1px dashed var(--border-muted);
  border-radius: 8px;
  background: var(--surface-section);
}

.workbench-module-menu {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.workbench-module-menu :deep(button) {
  width: 100%;
  justify-content: flex-start;
}

.workbench-module-empty {
  margin: 0;
  padding: 10px 12px;
  color: var(--text-muted);
  font-size: 12px;
  text-align: center;
}
</style>
