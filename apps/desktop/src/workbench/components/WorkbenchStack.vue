<script setup lang="ts">
import { computed } from 'vue'
import WorkbenchCardHost from './WorkbenchCardHost.vue'
import { useWorkbench } from '../useWorkbench'
import { usePanelStyles } from '@/composables/usePanelStyles'
import type { StackGeometry, WorkbenchStack as StackModel, PersistedCardInstance, SurfaceMode } from '../types'

const props = defineProps<{
  stack: StackModel
  geometry: StackGeometry
  /** Card instances in this stack, ordered by tabIds. */
  instances: PersistedCardInstance[]
  /** Whether this stack is a degraded split (visual single stack). */
  degraded?: boolean
  /** Column surface mode — float panels vs connected app layout. */
  surfaceMode?: SurfaceMode
}>()

const wb = useWorkbench()

// The stack is a single material root (like the old ContextPanel / .sidebar /
// .conversation). Tab bar and content share one continuous surface.
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()

// Immersive stacks (Home chat column) are transparent: the root carries no
// background/backdrop so the page background shows through, but it still keeps
// the data-panel-effect attribute so inner objects (composer, welcome dialog,
// bubbles) inherit the remapped --surface-* tokens and follow the global
// material. Keep the --material-filter-* vars for blur-glass inner surfaces.
const materialStyle = computed(() => {
  const style = getPanelStyle()
  if (props.surfaceMode === 'immersive') {
    const { backgroundColor, backdropFilter, WebkitBackdropFilter, ...rest } = style
    return rest
  }
  return style
})
const materialAttrs = computed(() => getPanelDataAttributes())

const showTabBar = computed(() => props.instances.length > 1)

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

<template vapor>
  <div
    class="wb-stack"
    :class="{
      'wb-stack--app': surfaceMode === 'app',
      'wb-stack--immersive': surfaceMode === 'immersive',
    }"
    :style="{
      left: `${geometry.x}px`,
      top: `${geometry.y}px`,
      width: `${geometry.width}px`,
      height: `${geometry.height}px`,
      ...materialStyle,
    }"
    v-bind="materialAttrs"
  >
    <!-- Browser-style tab bar for multi-card stacks -->
    <div v-if="showTabBar" class="browser-tab-bar wb-stack-tabbar">
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
        :surface-mode="surfaceMode"
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
  background: var(--surface-section);
  /* Float-panel look: rounded, shadowed, no outline (borderless). */
  border: none;
  border-radius: 12px;
  box-shadow: var(--shadow-panel);
}

/* Connected app look: no rounding, no border, continuous surface. */
.wb-stack--app {
  background: var(--bg-primary);
  border: none;
  border-radius: 0;
  box-shadow: none;
}

/* Immersive zone (Home chat column): the root is transparent so the page
   background shows through; inner objects carry their own material. */
.wb-stack--immersive {
  background: transparent;
  border: none;
  border-radius: 0;
  box-shadow: none;
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
