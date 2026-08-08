<script setup lang="ts">
import { computed } from 'vue'
import { MoreHorizontal, X, ExternalLink, ArrowUpFromLine, PanelTopOpen, PanelBottom, GripHorizontal } from '@lucide/vue'
import { usePanelStyles } from '@/composables/usePanelStyles'
import type { WorkbenchCardDescriptor } from '../types'

const props = defineProps<{
  title: string
  descriptor: WorkbenchCardDescriptor
  active: boolean
  titlebarMode?: 'hidden' | 'minimal' | 'full'
  /** True when this card is inside a secondary (bottom) stack. */
  inSecondary?: boolean
  /** True when this card is the active tab of its stack. */
  isActiveTab?: boolean
}>()

const emit = defineEmits<{
  'activate': []
  'close': []
  'move-start': []
  'split': []
  'merge': []
  'detach': []
  'menu-open': [open: boolean]
}>()

// Material is applied on the stable card frame only.
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const materialStyle = computed(() => getPanelStyle())
const materialAttrs = computed(() => getPanelDataAttributes())

const mode = computed(() => props.titlebarMode ?? props.descriptor.titlebarMode ?? 'full')
const showTitlebar = computed(() => mode.value !== 'hidden')
const showTab = computed(() => mode.value === 'full')
</script>

<template vapor>
  <article
    class="wb-card-frame float-panel"
    :class="{
      'wb-card-frame--active': active,
      'wb-card-frame--secondary': inSecondary,
    }"
    :style="materialStyle"
    v-bind="materialAttrs"
    @mousedown.stop
    @pointerdown.stop
  >
    <!-- Tab (full mode, browser-style) -->
    <div v-if="showTab" class="browser-tab-bar wb-card-tabbar">
      <button
        class="browser-tab"
        :class="{ active: isActiveTab }"
        :title="title"
        @click="emit('activate')"
        @pointerdown="emit('move-start')"
      >
        <GripHorizontal :size="12" class="browser-tab-icon" />
        <span class="browser-tab-label">{{ title }}</span>
        <span v-if="descriptor.detachable" class="browser-tab-detach" :title="$t('context.detachTab')" @click.stop="emit('detach')">
          <ExternalLink :size="11" />
        </span>
        <span v-if="descriptor.closable" class="browser-tab-close" @click.stop="emit('close')">
          <X :size="11" />
        </span>
      </button>
    </div>

    <!-- Titlebar (full/minimal, when no browser tab or as fallback) -->
    <div v-if="showTitlebar && !showTab" class="wb-card-titlebar">
      <div class="wb-card-titlebar-drag" @pointerdown="emit('move-start')">
        <GripHorizontal :size="12" />
        <span class="wb-card-title">{{ title }}</span>
      </div>
      <div class="wb-card-titlebar-actions">
        <button v-if="descriptor.movable" class="wb-card-action" :title="$t('workbench.move')">
          <ArrowUpFromLine :size="12" />
        </button>
        <button v-if="inSecondary && descriptor.movable" class="wb-card-action" :title="$t('workbench.merge')" @click="emit('merge')">
          <PanelTopOpen :size="12" />
        </button>
        <button v-else-if="descriptor.movable" class="wb-card-action" :title="$t('workbench.split')" @click="emit('split')">
          <PanelBottom :size="12" />
        </button>
        <button v-if="descriptor.detachable" class="wb-card-action" :title="$t('workbench.detach')" @click="emit('detach')">
          <ExternalLink :size="12" />
        </button>
        <button v-if="descriptor.closable" class="wb-card-action" :title="$t('workbench.close')" @click="emit('close')">
          <X :size="12" />
        </button>
        <button class="wb-card-action" :title="$t('workbench.menu')" @pointerdown.stop @click.stop="emit('menu-open', true)">
          <MoreHorizontal :size="12" />
        </button>
      </div>
    </div>

    <!-- Card content (opacity-only transitions to preserve backdrop-filter) -->
    <div class="wb-card-content">
      <slot />
    </div>
  </article>
</template>

<style scoped>
.wb-card-frame {
  position: absolute;
  /* Geometry (left/top/width/height) is set inline by the canvas. */
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  /* Island-style card boundary: subtle border + radius for visual separation */
  border: 1px solid var(--border-card);
  border-radius: 12px;
  /* Conditional shadow: only when not in immersive mode, applied by parent */
}

/* Light shadow variant for floating panels */
.wb-card-frame--shadow {
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.08),
              0 4px 12px rgba(0, 0, 0, 0.05);
}

.wb-card-tabbar {
  flex-shrink: 0;
}

.wb-card-titlebar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 4px 6px 4px 8px;
  flex-shrink: 0;
  gap: 4px;
}

.wb-card-titlebar-drag {
  display: flex;
  align-items: center;
  gap: 5px;
  cursor: grab;
  min-width: 0;
  flex: 1;
}

.wb-card-title {
  font-size: 11.5px;
  font-weight: 500;
  color: var(--text-secondary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.wb-card-titlebar-actions {
  display: flex;
  align-items: center;
  gap: 1px;
}

.wb-card-action {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border-radius: 6px;
  color: var(--text-secondary);
  background: transparent;
  border: none;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.wb-card-action:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.wb-card-content {
  flex: 1;
  min-height: 0;
  overflow: hidden;
  position: relative;
  /* Opacity-only transitions keep backdrop-filter intact (no transform ancestors). */
  transition: opacity 0.2s ease;
}
</style>
