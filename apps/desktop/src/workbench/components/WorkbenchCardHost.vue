<script setup lang="ts">
import { computed } from 'vue'
import { useWorkbench } from '../useWorkbench'
import type { PersistedCardInstance } from '../types'

const props = defineProps<{
  instance: PersistedCardInstance
  /** True when this card is the active tab of its stack. */
  active: boolean
}>()

const wb = useWorkbench()

const component = computed(() => wb.componentFor(props.instance.descriptorId))
</script>

<template>
  <div
    class="wb-card-host"
    :class="{ 'wb-card-host--hidden': !active }"
    :aria-hidden="!active ? 'true' : undefined"
    :inert="!active ? true : undefined"
  >
    <component
      :is="component"
      v-if="component"
      :key="instance.id"
      :instance-id="instance.id"
      :card-state="instance.state"
      :active="active"
    />
    <div v-else class="wb-card-unknown">
      Unknown card: {{ instance.descriptorId }}
    </div>
  </div>
</template>

<style scoped>
.wb-card-host {
  width: 100%;
  height: 100%;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.wb-card-host--hidden {
  display: none;
}

.wb-card-unknown {
  padding: 12px;
  font-size: 12px;
  color: var(--text-secondary);
}
</style>
