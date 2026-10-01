<script setup lang="ts">
import { computed, provide } from 'vue'
import { useUie } from './useUie'
import type { PersistedCardInstance } from '../engine/types'

const props = defineProps<{
  instance: PersistedCardInstance
  /** True when this card is the active tab of its stack. */
  active: boolean
}>()

const uie = useUie()

const component = computed(() => uie.componentFor(props.instance.descriptorId))

// Provide card context via inject so the card content can read its instance id,
// serialized state, and visibility without extraneous non-props attribute warnings.
//
// `uie:active` is provided as a ComputedRef: this SFC is Vapor, so setup runs once and
// a plain value would freeze the flag at whatever it was on mount. Consumers must
// unwrap it (`toValue`) rather than treat it as a boolean.
provide('uie:instanceId', props.instance.id)
provide('uie:cardState', props.instance.state)
provide('uie:active', computed(() => props.active))
</script>

<template vapor>
  <div
    class="uie-card-host"
    :class="{ 'uie-card-host--hidden': !active }"
    :aria-hidden="!active ? 'true' : undefined"
    :inert="!active ? true : undefined"
  >
    <component
      :is="component"
      v-if="component"
      :key="instance.id"
    />
    <div v-else class="uie-card-unknown">
      Unknown card: {{ instance.descriptorId }}
    </div>
  </div>
</template>

<style scoped>
.uie-card-host {
  width: 100%;
  height: 100%;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.uie-card-host--hidden {
  display: none;
}

.uie-card-unknown {
  padding: 12px;
  font-size: 12px;
  color: var(--text-secondary);
}
</style>
