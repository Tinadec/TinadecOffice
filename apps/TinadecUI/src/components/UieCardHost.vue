<script setup lang="ts">
import { computed, provide, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useUie } from './useUie'
import type { PersistedCardInstance } from '../engine/types'

const { t } = useI18n()

const props = defineProps<{
  instance: PersistedCardInstance
  /** True when this card is the active tab of its stack. */
  active: boolean
}>()

const uie = useUie()

const component = computed(() => uie.componentFor(props.instance.descriptorId))

// Presets include unopened tabs. Mount their content on first activation, then
// keep it alive when hidden so editors, terminals and card-local state survive.
const activated = ref(props.active)
watch(() => props.active, (active) => {
  if (active) activated.value = true
})

// Provide card context via inject so the card content can read its instance id,
// serialized state, and visibility without extraneous non-props attribute warnings.
//
// `uie:active` is provided as a ComputedRef; a plain value would freeze the flag
// at whatever it was on mount. Consumers must
// unwrap it (`toValue`) rather than treat it as a boolean.
provide('uie:instanceId', props.instance.id)
provide('uie:cardState', props.instance.state)
provide('uie:active', computed(() => props.active))
</script>

<template>
  <div
    class="uie-card-host"
    :class="{ 'uie-card-host--hidden': !active }"
    :aria-hidden="!active ? 'true' : undefined"
    :inert="!active ? true : undefined"
  >
    <component
      :is="component"
      v-if="activated && component"
      :key="instance.id"
    />
    <div v-else-if="activated" class="uie-card-unknown">
      {{ t('uie.unknownCard', { id: instance.descriptorId }) }}
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
