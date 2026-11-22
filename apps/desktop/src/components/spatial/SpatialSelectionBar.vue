<script setup lang="ts">
import { computed } from 'vue'
import {
  AlignCenterHorizontal,
  AlignCenterVertical,
  AlignEndHorizontal,
  AlignEndVertical,
  AlignHorizontalSpaceAround,
  AlignStartHorizontal,
  AlignStartVertical,
  AlignVerticalSpaceAround,
  Copy,
  Frame,
  Sparkles,
  Trash2,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type {
  SpatialAlignMode,
  SpatialDistributeAxis,
  SpatialSelectionBounds,
  SpatialViewport,
} from './spatialWhiteboardTypes'

const props = withDefaults(defineProps<{
  selectedIds: string[]
  bounds: SpatialSelectionBounds | null
  viewport: SpatialViewport
  surfaceWidth?: number
  surfaceHeight?: number
}>(), {
  surfaceWidth: 0,
  surfaceHeight: 0,
})

const emit = defineEmits<{
  align: [mode: SpatialAlignMode]
  distribute: [axis: SpatialDistributeAxis]
  'wrap-section': []
  duplicate: []
  delete: []
  'dispatch-to-team': [selectedIds: string[]]
}>()

const { t } = useI18n()

const visible = computed(() => props.selectedIds.length > 0 && props.bounds !== null)
const canAlign = computed(() => props.selectedIds.length >= 2)
const canDistribute = computed(() => props.selectedIds.length >= 3)

const barStyle = computed(() => {
  if (!props.bounds) return {}
  const zoom = props.viewport.zoom || 1
  const rawCenterX = (props.bounds.x + props.bounds.width / 2) * zoom + props.viewport.x
  const rawTop = props.bounds.y * zoom + props.viewport.y - 54
  const minX = 180
  const maxX = props.surfaceWidth > 360 ? props.surfaceWidth - 180 : rawCenterX
  const clampedX = props.surfaceWidth > 360 ? Math.max(minX, Math.min(maxX, rawCenterX)) : rawCenterX
  const minY = 64
  const maxY = props.surfaceHeight > 160 ? props.surfaceHeight - 120 : rawTop
  const clampedY = props.surfaceHeight > 160 ? Math.max(minY, Math.min(maxY, rawTop)) : Math.max(minY, rawTop)
  return {
    left: `${clampedX.toFixed(1)}px`,
    top: `${clampedY.toFixed(1)}px`,
  }
})

const alignButtons: Array<{ mode: SpatialAlignMode; key: string; icon: unknown }> = [
  { mode: 'left', key: 'space.whiteboard.alignLeft', icon: AlignStartVertical },
  { mode: 'centerX', key: 'space.whiteboard.alignCenterX', icon: AlignCenterVertical },
  { mode: 'right', key: 'space.whiteboard.alignRight', icon: AlignEndVertical },
  { mode: 'top', key: 'space.whiteboard.alignTop', icon: AlignStartHorizontal },
  { mode: 'centerY', key: 'space.whiteboard.alignCenterY', icon: AlignCenterHorizontal },
  { mode: 'bottom', key: 'space.whiteboard.alignBottom', icon: AlignEndHorizontal },
]
</script>

<template>
  <div
    v-if="visible"
    class="spatial-selection-bar"
    :style="barStyle"
    role="toolbar"
    data-testid="spatial-selection-bar"
    @pointerdown.stop
    @click.stop
  >
    <template v-if="canAlign">
      <div class="spatial-selection-group">
        <button
          v-for="item in alignButtons"
          :key="item.mode"
          type="button"
          class="spatial-sel-btn"
          :title="t(item.key)"
          :aria-label="t(item.key)"
          :data-align="item.mode"
          @click="emit('align', item.mode)"
        >
          <component :is="item.icon" :size="14" aria-hidden="true" />
        </button>
      </div>

      <span class="spatial-sel-divider" aria-hidden="true" />

      <div class="spatial-selection-group">
        <button
          type="button"
          class="spatial-sel-btn"
          :disabled="!canDistribute"
          :title="t('space.whiteboard.distributeH')"
          :aria-label="t('space.whiteboard.distributeH')"
          data-distribute="horizontal"
          @click="emit('distribute', 'horizontal')"
        >
          <AlignHorizontalSpaceAround :size="14" aria-hidden="true" />
        </button>
        <button
          type="button"
          class="spatial-sel-btn"
          :disabled="!canDistribute"
          :title="t('space.whiteboard.distributeV')"
          :aria-label="t('space.whiteboard.distributeV')"
          data-distribute="vertical"
          @click="emit('distribute', 'vertical')"
        >
          <AlignVerticalSpaceAround :size="14" aria-hidden="true" />
        </button>
      </div>

      <span class="spatial-sel-divider" aria-hidden="true" />
    </template>

    <div class="spatial-selection-group">
      <button
        type="button"
        class="spatial-sel-action"
        :title="t('space.whiteboard.wrapSection')"
        data-testid="selection-wrap-section"
        @click="emit('wrap-section')"
      >
        <Frame :size="13" aria-hidden="true" />
        <span>{{ t('space.whiteboard.wrapSection') }}</span>
      </button>

      <button
        type="button"
        class="spatial-sel-btn"
        :title="t('space.whiteboard.duplicate')"
        :aria-label="t('space.whiteboard.duplicate')"
        data-testid="selection-duplicate"
        @click="emit('duplicate')"
      >
        <Copy :size="14" aria-hidden="true" />
      </button>

      <button
        type="button"
        class="spatial-sel-btn is-danger"
        :title="t('space.whiteboard.deleteSelected')"
        :aria-label="t('space.whiteboard.deleteSelected')"
        data-testid="selection-delete"
        @click="emit('delete')"
      >
        <Trash2 :size="14" aria-hidden="true" />
      </button>
    </div>

    <span class="spatial-sel-divider" aria-hidden="true" />

    <button
      type="button"
      class="spatial-sel-cta"
      data-testid="selection-dispatch-team"
      @click="emit('dispatch-to-team', [...selectedIds])"
    >
      <Sparkles :size="13" aria-hidden="true" />
      <span>{{ t('space.whiteboard.dispatchToTeam') }}</span>
    </button>
  </div>
</template>

<style scoped>
.spatial-selection-bar {
  position: absolute;
  transform: translateX(-50%);
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 6px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: color-mix(in srgb, var(--surface-raised, #ffffff) 92%, transparent);
  backdrop-filter: blur(16px);
  color: var(--text-primary);
  box-shadow: 0 10px 30px rgba(15, 23, 42, 0.18);
  z-index: 11;
  max-width: calc(100% - 24px);
  white-space: nowrap;
}

.spatial-selection-group {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.spatial-sel-divider {
  width: 1px;
  height: 18px;
  background: var(--border-muted);
  margin: 0 2px;
  flex-shrink: 0;
}

.spatial-sel-btn {
  width: 28px;
  height: 28px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 999px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease;
}

.spatial-sel-btn:hover:not(:disabled) {
  background: var(--surface-hover, rgba(128, 128, 128, 0.15));
  color: var(--text-primary);
}

.spatial-sel-btn.is-danger:hover:not(:disabled) {
  color: var(--accent-danger, #f85149);
}

.spatial-sel-btn:disabled {
  opacity: 0.36;
  cursor: not-allowed;
}

.spatial-sel-action {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 9px;
  border: 0;
  border-radius: 999px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color 120ms ease;
}

.spatial-sel-action:hover {
  background: var(--surface-hover, rgba(128, 128, 128, 0.15));
}

.spatial-sel-cta {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 11px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-primary, #7c3aed) 55%, transparent);
  background: color-mix(in srgb, var(--accent-primary, #7c3aed) 18%, var(--surface-section));
  color: var(--accent-primary, #7c3aed);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: background-color 120ms ease, border-color 120ms ease;
}

.spatial-sel-cta:hover {
  border-color: var(--accent-primary, #7c3aed);
  background: color-mix(in srgb, var(--accent-primary, #7c3aed) 28%, var(--surface-section));
}
</style>
