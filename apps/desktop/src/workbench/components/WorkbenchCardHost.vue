<script setup lang="ts">
import { computed, provide } from 'vue'
import { useWorkbench } from '../useWorkbench'
import type { PersistedCardInstance } from '../types'

const props = defineProps<{
  instance: PersistedCardInstance
  /** True when this card is the active tab of its stack. */
  active: boolean
}>()

const wb = useWorkbench()

const component = computed(() => wb.componentFor(props.instance.descriptorId))

// Provide card context via inject so the card content can read its instance id,
// serialized state, and visibility without extraneous non-props attribute warnings.
provide('wb:instanceId', props.instance.id)
provide('wb:cardState', props.instance.state)
provide('wb:active', props.active)
</script>

<template vapor>
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
