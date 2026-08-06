<script setup lang="ts">
import WorkbenchCardHost from './WorkbenchCardHost.vue'
import { useWorkbench } from '../useWorkbench'
import type { StackGeometry, WorkbenchStack as StackModel, PersistedCardInstance } from '../types'

const props = defineProps<{
  stack: StackModel
  geometry: StackGeometry
  /** Card instances in this stack, ordered by tabIds. */
  instances: PersistedCardInstance[]
  /** Whether this stack is a degraded split (visual single stack). */
  degraded?: boolean
}>()

const wb = useWorkbench()

function activate(instanceId: string) {
  wb.bus.dispatch({
    command: { type: 'activateCard', scope: wb.scope.value, instanceId },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}

function close(instanceId: string) {
  wb.bus.dispatch({
    command: { type: 'closeCard', scope: wb.scope.value, instanceId },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}
</script>

<template>
  <div
    class="wb-stack"
    :style="{
      left: `${geometry.x}px`,
      top: `${geometry.y}px`,
      width: `${geometry.width}px`,
      height: `${geometry.height}px`,
    }"
  >
    <!-- Browser-style tab bar for the stack -->
    <div v-if="instances.length > 0" class="browser-tab-bar wb-stack-tabbar">
      <button
        v-for="inst in instances"
        :key="inst.id"
        class="browser-tab"
        :class="{ active: stack.activeTabId === inst.id }"
        :title="inst.title"
        @click="activate(inst.id)"
      >
        <span class="browser-tab-label">{{ inst.title }}</span>
        <span
          v-if="inst.id !== stack.activeTabId"
          class="browser-tab-close"
          @click.stop="close(inst.id)"
        >
          ×
        </span>
      </button>
    </div>

    <!-- Card hosts — all mounted, visibility toggles only -->
    <div class="wb-stack-body">
      <WorkbenchCardHost
        v-for="inst in instances"
        :key="inst.id"
        :instance="inst"
        :active="stack.activeTabId === inst.id"
      />
    </div>
  </div>
</template>

<style scoped>
.wb-stack {
  position: absolute;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
}

.wb-stack-tabbar {
  flex-shrink: 0;
}

.wb-stack-body {
  flex: 1;
  min-height: 0;
  position: relative;
  overflow: hidden;
}
</style>
