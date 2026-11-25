<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  AlignCenterHorizontal,
  AlignCenterVertical,
  AlignEndHorizontal,
  AlignEndVertical,
  AlignHorizontalSpaceAround,
  AlignStartHorizontal,
  AlignStartVertical,
  AlignVerticalSpaceAround,
  Check,
  ChevronDown,
  Columns3,
  Copy,
  Frame,
  Sparkles,
  Trash2,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { ProjectBoardTaskStatus } from '@/api'
import {
  readSpatialPromotionPreference,
  saveSpatialPromotionPreference,
  type SpatialPromotionMode,
  type SpatialPromotionPreference,
} from '@/components/board'
import type {
  SpatialAlignMode,
  SpatialDistributeAxis,
  SpatialSelectionBounds,
  SpatialViewport,
} from './spatialWhiteboardTypes'

export interface SpatialPromoteToBoardEvent {
  objectIds: string[]
  mode: SpatialPromotionMode
  targetStatus: ProjectBoardTaskStatus
}

const props = withDefaults(defineProps<{
  selectedIds: string[]
  bounds: SpatialSelectionBounds | null
  viewport: SpatialViewport
  surfaceWidth?: number
  surfaceHeight?: number
  projectId?: string | null
  promotableIds?: string[]
  canPromoteToBoard?: boolean
  promoting?: boolean
}>(), {
  surfaceWidth: 0,
  surfaceHeight: 0,
  projectId: null,
  canPromoteToBoard: undefined,
  promoting: false,
})

const emit = defineEmits<{
  align: [mode: SpatialAlignMode]
  distribute: [axis: SpatialDistributeAxis]
  'wrap-section': []
  duplicate: []
  delete: []
  'dispatch-to-team': [selectedIds: string[]]
  'promote-to-board': [payload: SpatialPromoteToBoardEvent]
}>()

const { t } = useI18n()

const visible = computed(() => props.selectedIds.length > 0 && props.bounds !== null)
const canAlign = computed(() => props.selectedIds.length >= 2)
const canDistribute = computed(() => props.selectedIds.length >= 3)

const resolvedPromotableIds = computed(() => props.promotableIds ?? props.selectedIds)
const promotableCount = computed(() => resolvedPromotableIds.value.length)
const showPromoteSplit = computed(() => {
  if (props.canPromoteToBoard !== undefined) {
    return props.canPromoteToBoard && promotableCount.value > 0
  }
  return Boolean(props.projectId && props.projectId.trim()) && promotableCount.value > 0
})

const promoteMenuOpen = ref(false)
const promotePref = ref<SpatialPromotionPreference>(readSpatialPromotionPreference())

function onUiPreferencesSpatialSync() {
  promotePref.value = readSpatialPromotionPreference()
}

onMounted(() => {
  if (typeof window !== 'undefined') {
    window.addEventListener('tinadec:ui-preferences-spatial-changed', onUiPreferencesSpatialSync)
  }
})

onBeforeUnmount(() => {
  if (typeof window !== 'undefined') {
    window.removeEventListener('tinadec:ui-preferences-spatial-changed', onUiPreferencesSpatialSync)
  }
})

watch(
  () => props.selectedIds.join(','),
  () => {
    promoteMenuOpen.value = false
    promotePref.value = readSpatialPromotionPreference()
  },
)

function executePromoteMain() {
  if (props.promoting || !showPromoteSplit.value) return
  promotePref.value = readSpatialPromotionPreference()
  promoteMenuOpen.value = false
  emit('promote-to-board', {
    objectIds: [...resolvedPromotableIds.value],
    mode: promotePref.value.mode,
    targetStatus: promotePref.value.target_status,
  })
}

function togglePromoteMenu() {
  if (props.promoting) return
  if (!promoteMenuOpen.value) {
    promotePref.value = readSpatialPromotionPreference()
  }
  promoteMenuOpen.value = !promoteMenuOpen.value
}

function choosePromotionMode(mode: SpatialPromotionMode) {
  if (props.promoting || !showPromoteSplit.value) return
  const saved = saveSpatialPromotionPreference({
    mode,
    target_status: promotePref.value.target_status,
  })
  promotePref.value = saved
  promoteMenuOpen.value = false
  emit('promote-to-board', {
    objectIds: [...resolvedPromotableIds.value],
    mode: saved.mode,
    targetStatus: saved.target_status,
  })
}

function selectTargetStatus(targetStatus: 'backlog' | 'ready') {
  const saved = saveSpatialPromotionPreference({
    mode: promotePref.value.mode,
    target_status: targetStatus,
  })
  promotePref.value = saved
}

const rememberedModeSummary = computed(() => {
  const mode = promotePref.value.mode
  const statusLabel = t(`board.status.${promotePref.value.target_status}`)
  if (mode === 'merge_single') {
    return `${t('space.boardBridge.modeMergeSingle')} · ${statusLabel}`
  }
  if (mode === 'parent_with_children') {
    return `${t('space.boardBridge.modeParentWithChildren', { count: promotableCount.value })} · ${statusLabel}`
  }
  return `${t('space.boardBridge.modeSplitMany', { count: promotableCount.value })} · ${statusLabel}`
})

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

    <!-- Remembered Split Button: Promote Spatial Nodes -> Project Board Tasks -->
    <template v-if="showPromoteSplit">
      <span class="spatial-sel-divider" aria-hidden="true" />

      <div class="spatial-promote-split-wrap" data-testid="selection-promote-split">
        <button
          type="button"
          class="spatial-promote-main ui-pressable"
          :disabled="promoting"
          :title="rememberedModeSummary"
          data-testid="selection-promote-board-main"
          @click="executePromoteMain"
        >
          <Columns3 :size="13" aria-hidden="true" />
          <span>{{ t('space.boardBridge.promoteToBoard') }}</span>
        </button>
        <span class="spatial-promote-sep-v" aria-hidden="true" />
        <button
          type="button"
          class="spatial-promote-trigger ui-pressable"
          :class="{ 'is-open': promoteMenuOpen }"
          :disabled="promoting"
          :aria-expanded="promoteMenuOpen"
          :aria-label="t('space.boardBridge.promoteOptionsAria')"
          :title="t('space.boardBridge.promoteOptionsAria')"
          data-testid="selection-promote-board-trigger"
          @click="togglePromoteMenu"
        >
          <ChevronDown :size="13" aria-hidden="true" />
        </button>

        <div
          v-if="promoteMenuOpen"
          class="spatial-promote-popover"
          role="menu"
          data-testid="selection-promote-board-menu"
          @pointerdown.stop
          @click.stop
        >
          <div class="spatial-promote-section-label">
            {{ t('space.boardBridge.promoteModeHeading') }}
          </div>

          <button
            type="button"
            role="menuitem"
            class="spatial-promote-option ui-pressable"
            :class="{ 'is-selected': promotePref.mode === 'merge_single' }"
            data-testid="promote-mode-merge_single"
            @click="choosePromotionMode('merge_single')"
          >
            <span>{{ t('space.boardBridge.modeMergeSingle') }}</span>
            <Check v-if="promotePref.mode === 'merge_single'" :size="12" aria-hidden="true" />
          </button>

          <button
            type="button"
            role="menuitem"
            class="spatial-promote-option ui-pressable"
            :class="{ 'is-selected': promotePref.mode === 'split_many' }"
            data-testid="promote-mode-split_many"
            @click="choosePromotionMode('split_many')"
          >
            <span>{{ t('space.boardBridge.modeSplitMany', { count: promotableCount }) }}</span>
            <Check v-if="promotePref.mode === 'split_many'" :size="12" aria-hidden="true" />
          </button>

          <button
            type="button"
            role="menuitem"
            class="spatial-promote-option ui-pressable"
            :class="{ 'is-selected': promotePref.mode === 'parent_with_children' }"
            data-testid="promote-mode-parent_with_children"
            @click="choosePromotionMode('parent_with_children')"
          >
            <span>{{ t('space.boardBridge.modeParentWithChildren', { count: promotableCount }) }}</span>
            <Check v-if="promotePref.mode === 'parent_with_children'" :size="12" aria-hidden="true" />
          </button>

          <div class="spatial-promote-sep" />

          <div class="spatial-promote-target-row">
            <span class="spatial-promote-target-label">
              {{ t('space.boardBridge.targetColumnLabel') }}
            </span>
            <div class="spatial-promote-target-pills" role="group">
              <button
                type="button"
                class="spatial-promote-col-pill ui-pressable"
                :class="{ 'is-active': promotePref.target_status === 'backlog' }"
                data-testid="promote-target-backlog"
                @click="selectTargetStatus('backlog')"
              >
                {{ t('board.status.backlog') }}
              </button>
              <button
                type="button"
                class="spatial-promote-col-pill ui-pressable"
                :class="{ 'is-active': promotePref.target_status === 'ready' }"
                data-testid="promote-target-ready"
                @click="selectTargetStatus('ready')"
              >
                {{ t('board.status.ready') }}
              </button>
            </div>
          </div>
        </div>
      </div>
    </template>

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

.spatial-promote-split-wrap {
  position: relative;
  display: inline-flex;
  align-items: center;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--border-default, var(--border-muted)) 80%, transparent);
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
}

.spatial-promote-main {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 9px 0 10px;
  border: 0;
  border-radius: 999px 0 0 999px;
  background: transparent;
  color: inherit;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: opacity var(--duration-tap, 120ms) ease;
}

.spatial-promote-main:hover:not(:disabled),
.spatial-promote-trigger:hover:not(:disabled) {
  opacity: 0.9;
}

.spatial-promote-main:disabled,
.spatial-promote-trigger:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.spatial-promote-sep-v {
  width: 1px;
  height: 16px;
  background: color-mix(in srgb, currentColor 22%, transparent);
  flex-shrink: 0;
}

.spatial-promote-trigger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 26px;
  padding: 0;
  border: 0;
  border-radius: 0 999px 999px 0;
  background: transparent;
  color: inherit;
  cursor: pointer;
  transition: opacity var(--duration-tap, 120ms) ease;
}

.spatial-promote-popover {
  position: absolute;
  top: calc(100% + 8px);
  left: 50%;
  transform: translateX(-50%);
  min-width: 248px;
  padding: 6px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: color-mix(in srgb, var(--surface-raised, #ffffff) 96%, transparent);
  backdrop-filter: blur(18px);
  color: var(--text-primary);
  box-shadow: 0 12px 32px rgba(15, 23, 42, 0.18);
  display: flex;
  flex-direction: column;
  gap: 2px;
  z-index: 24;
  white-space: normal;
}

.spatial-promote-section-label {
  padding: 4px 8px 2px;
  font-size: 10.5px;
  font-weight: 600;
  color: var(--text-muted);
}

.spatial-promote-option {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  width: 100%;
  padding: 7px 9px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 500;
  text-align: left;
  cursor: pointer;
  transition: background-color var(--duration-tap, 120ms) ease;
}

.spatial-promote-option:hover {
  background: var(--surface-hover, rgba(128, 128, 128, 0.14));
}

.spatial-promote-option.is-selected {
  font-weight: 600;
  background: color-mix(in srgb, var(--surface-section, var(--bg-tertiary)) 85%, transparent);
}

.spatial-promote-sep {
  height: 1px;
  background: color-mix(in srgb, var(--border-muted) 65%, transparent);
  margin: 4px 2px;
}

.spatial-promote-target-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 8px;
}

.spatial-promote-target-label {
  font-size: 11px;
  color: var(--text-secondary);
}

.spatial-promote-target-pills {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 2px;
  border-radius: 8px;
  background: var(--surface-section, var(--bg-tertiary));
  border: 1px solid color-mix(in srgb, var(--border-muted) 70%, transparent);
}

.spatial-promote-col-pill {
  height: 21px;
  padding: 0 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
  transition:
    background-color var(--duration-tap, 120ms) ease,
    color var(--duration-tap, 120ms) ease;
}

.spatial-promote-col-pill:hover {
  color: var(--text-primary);
}

.spatial-promote-col-pill.is-active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
}

.spatial-sel-cta {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 11px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--border-default, var(--border-muted)) 80%, transparent);
  background: color-mix(in srgb, var(--surface-section, var(--bg-tertiary)) 85%, transparent);
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: background-color 120ms ease, border-color 120ms ease;
}

.spatial-sel-cta:hover {
  border-color: var(--text-primary);
  background: var(--surface-hover, rgba(128, 128, 128, 0.16));
}
</style>
