<script setup lang="ts">
import { computed, ref, toRef, type CSSProperties } from 'vue'
import {
  ArrowDownToLine,
  ArrowLeftToLine,
  ArrowRightToLine,
  ArrowUpToLine,
  Combine,
  ExternalLink,
  GripVertical,
  MoreHorizontal,
  PanelLeftClose,
  Rows3,
  X,
} from '@lucide/vue'
import { cn } from '@/lib/utils'
import { UiButton, UiDropdownMenu, UiSeparator, UiTooltip } from '@/components/ui'
import type {
  PersistedCardInstance,
  WorkbenchCardDescriptor,
  WorkbenchRect,
  WorkbenchSlotId,
  WorkbenchStackId,
  WorkbenchPageId,
} from '../types'
import { provideWorkbenchCard } from '../cardContext'

const props = defineProps<{
  card: PersistedCardInstance
  descriptor: WorkbenchCardDescriptor
  rect: WorkbenchRect
  stackCards: readonly PersistedCardInstance[]
  slotId: WorkbenchSlotId
  stackId: WorkbenchStackId
  hasSplit: boolean
  descriptors: ReadonlyMap<string, WorkbenchCardDescriptor>
  active: boolean
  pageActive: boolean
  locked: boolean
  pageId: WorkbenchPageId
  panelStyle: Record<string, string>
  panelDataAttributes: Record<string, string>
}>()

const emit = defineEmits<{
  activate: [cardId: string]
  move: [cardId: string, slotId: WorkbenchSlotId, stackId: WorkbenchStackId, index?: number]
  moveStack: [fromSlotId: WorkbenchSlotId, fromStackId: WorkbenchStackId, toSlotId: WorkbenchSlotId, toStackId: WorkbenchStackId]
  mergeStack: [slotId: WorkbenchSlotId]
  collapseColumn: [slotId: WorkbenchSlotId]
  close: [cardId: string]
  dragStart: [cardId: string]
  dragMove: [cardId: string, clientX: number, clientY: number]
  dragEnd: [cardId: string, clientX: number, clientY: number]
  detach: [card: PersistedCardInstance]
  updateState: [cardId: string, state: Record<string, unknown>]
}>()

const menuOpen = ref(false)
const dragging = ref(false)
const tabListRef = ref<HTMLElement | null>(null)
const tabDropIndex = ref<number | null>(null)
const suppressTabClickId = ref<string | null>(null)
let pointerStart: { x: number; y: number; pointerId: number } | null = null
let tabPointerStart: {
  cardId: string
  index: number
  x: number
  pointerId: number
  moved: boolean
} | null = null

const shown = computed(() => props.active && props.pageActive)
const cardRef = toRef(props, 'card')
provideWorkbenchCard({
  card: cardRef,
  instanceId: props.card.id,
  pageId: props.pageId,
  updateState: (state) => emit('updateState', props.card.id, state),
})
const surfaceStyle = computed<CSSProperties>(() => ({
  left: `${props.rect.x}px`,
  top: `${props.rect.y}px`,
  width: `${props.rect.width}px`,
  height: `${props.rect.height}px`,
  visibility: shown.value ? 'visible' : 'hidden',
  pointerEvents: shown.value ? 'auto' : 'none',
  ...props.panelStyle,
}))

function moveTo(slotId: WorkbenchSlotId, stackId: WorkbenchStackId = 'primary'): void {
  menuOpen.value = false
  emit('move', props.card.id, slotId, stackId)
}

function moveCurrentStack(toSlotId: WorkbenchSlotId, toStackId: WorkbenchStackId = 'primary'): void {
  menuOpen.value = false
  emit('moveStack', props.slotId, props.stackId, toSlotId, toStackId)
}

function mergeCurrentSplit(): void {
  menuOpen.value = false
  emit('mergeStack', props.slotId)
}

function collapseCurrentColumn(): void {
  menuOpen.value = false
  emit('collapseColumn', props.slotId)
}

function closeCard(): void {
  menuOpen.value = false
  emit('close', props.card.id)
}

function detachCard(): void {
  menuOpen.value = false
  emit('detach', props.card)
}

function onTabKeydown(event: KeyboardEvent, index: number): void {
  if ((event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') || props.stackCards.length < 2) return
  event.preventDefault()
  const delta = event.key === 'ArrowLeft' ? -1 : 1
  if (event.ctrlKey && event.shiftKey) {
    const targetIndex = Math.max(0, Math.min(props.stackCards.length - 1, index + delta))
    const tab = props.stackCards[index]
    if (tab && targetIndex !== index) emit('move', tab.id, props.slotId, props.stackId, targetIndex)
    return
  }
  const tab = props.stackCards[(index + delta + props.stackCards.length) % props.stackCards.length]
  if (tab) emit('activate', tab.id)
}

function onTabClick(event: MouseEvent, cardId: string): void {
  if (suppressTabClickId.value === cardId) {
    event.preventDefault()
    event.stopPropagation()
    return
  }
  emit('activate', cardId)
}

function onTabPointerDown(event: PointerEvent, cardId: string, index: number): void {
  if (props.locked || event.button !== 0 || props.descriptors.get(props.stackCards[index]?.type ?? '')?.movable === false) return
  tabPointerStart = { cardId, index, x: event.clientX, pointerId: event.pointerId, moved: false }
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
}

function onTabPointerMove(event: PointerEvent): void {
  if (!tabPointerStart || event.pointerId !== tabPointerStart.pointerId) return
  if (!tabPointerStart.moved && Math.abs(event.clientX - tabPointerStart.x) < 5) return
  tabPointerStart.moved = true
  const tabs = [...(tabListRef.value?.querySelectorAll<HTMLElement>('[data-workbench-tab]') ?? [])]
  const targetIndex = tabs.findIndex((tab) => {
    const bounds = tab.getBoundingClientRect()
    return event.clientX < bounds.left + bounds.width / 2
  })
  tabDropIndex.value = targetIndex < 0 ? Math.max(0, tabs.length - 1) : targetIndex
}

function finishTabPointer(event: PointerEvent, cancelled = false): void {
  if (!tabPointerStart || event.pointerId !== tabPointerStart.pointerId) return
  const drag = tabPointerStart
  if (!cancelled && drag.moved && tabDropIndex.value !== null && tabDropIndex.value !== drag.index) {
    suppressTabClickId.value = drag.cardId
    emit('move', drag.cardId, props.slotId, props.stackId, tabDropIndex.value)
    window.setTimeout(() => {
      if (suppressTabClickId.value === drag.cardId) suppressTabClickId.value = null
    }, 0)
  }
  tabPointerStart = null
  tabDropIndex.value = null
}

function onPointerDown(event: PointerEvent): void {
  if (props.locked || !props.descriptor.movable || event.button !== 0) return
  pointerStart = { x: event.clientX, y: event.clientY, pointerId: event.pointerId }
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
}

function onPointerMove(event: PointerEvent): void {
  if (!pointerStart || event.pointerId !== pointerStart.pointerId) return
  const distance = Math.hypot(event.clientX - pointerStart.x, event.clientY - pointerStart.y)
  if (!dragging.value && distance >= 6) {
    dragging.value = true
    emit('dragStart', props.card.id)
  }
  if (dragging.value) emit('dragMove', props.card.id, event.clientX, event.clientY)
}

function onPointerUp(event: PointerEvent): void {
  if (!pointerStart || event.pointerId !== pointerStart.pointerId) return
  if (dragging.value) emit('dragEnd', props.card.id, event.clientX, event.clientY)
  pointerStart = null
  dragging.value = false
}
</script>

<template>
  <article
    class="workbench-card-surface"
    :class="{ 'is-dragging': dragging }"
    :style="surfaceStyle"
    :aria-hidden="!shown"
    :inert="shown ? undefined : true"
    v-bind="panelDataAttributes"
  >
    <header class="workbench-card-header">
      <UiTooltip v-if="descriptor.movable && !locked" content="Move card">
        <UiButton
          variant="ghost"
          size="icon"
          class="workbench-card-grip"
          :aria-label="`Move ${descriptor.title}`"
          @pointerdown="onPointerDown"
          @pointermove="onPointerMove"
          @pointerup="onPointerUp"
          @pointercancel="onPointerUp"
        >
          <GripVertical />
        </UiButton>
      </UiTooltip>

      <div ref="tabListRef" class="workbench-card-tabs" role="tablist">
        <button
          v-for="tab in stackCards"
          :key="tab.id"
          :class="cn(
            'workbench-card-tab',
            tab.id === card.id && 'is-active',
            tabDropIndex === stackCards.indexOf(tab) && 'is-drop-target',
          )"
          data-workbench-tab
          role="tab"
          :aria-selected="tab.id === card.id"
          :tabindex="tab.id === card.id ? 0 : -1"
          :title="descriptors.get(tab.type)?.title ?? tab.type"
          @click="onTabClick($event, tab.id)"
          @keydown="onTabKeydown($event, stackCards.indexOf(tab))"
          @pointerdown="onTabPointerDown($event, tab.id, stackCards.indexOf(tab))"
          @pointermove="onTabPointerMove"
          @pointerup="finishTabPointer"
          @pointercancel="finishTabPointer($event, true)"
        >
          <component :is="descriptors.get(tab.type)?.icon" />
          <span>{{ descriptors.get(tab.type)?.title ?? tab.type }}</span>
        </button>
      </div>

      <UiDropdownMenu
        v-if="!locked && (descriptor.movable || descriptor.closable || descriptor.detachable)"
        v-model:open="menuOpen"
        placement="bottom"
        class="right-0 left-auto w-56"
      >
        <template #trigger>
          <UiTooltip content="Card actions">
            <UiButton variant="ghost" size="icon" class="workbench-card-action" aria-label="Card actions">
              <MoreHorizontal />
            </UiButton>
          </UiTooltip>
        </template>
        <div class="workbench-card-menu" role="menu">
          <span class="workbench-card-menu-label">Card</span>
          <UiButton v-if="descriptor.movable" variant="ghost" size="sm" role="menuitem" @click="moveTo('left')">
            <ArrowLeftToLine data-icon="inline-start" />
            Move left
          </UiButton>
          <UiButton v-if="descriptor.movable" variant="ghost" size="sm" role="menuitem" @click="moveTo('center')">
            <ArrowDownToLine data-icon="inline-start" />
            Move center
          </UiButton>
          <UiButton v-if="descriptor.movable" variant="ghost" size="sm" role="menuitem" @click="moveTo('right')">
            <ArrowRightToLine data-icon="inline-start" />
            Move right
          </UiButton>
          <UiButton v-if="descriptor.movable" variant="ghost" size="sm" role="menuitem" @click="moveTo(slotId, 'secondary')">
            <ArrowDownToLine data-icon="inline-start" />
            Split below
          </UiButton>
          <UiSeparator />
          <span class="workbench-card-menu-label">Stack</span>
          <UiButton v-if="slotId !== 'left'" variant="ghost" size="sm" role="menuitem" @click="moveCurrentStack('left')">
            <ArrowLeftToLine data-icon="inline-start" />
            Move stack left
          </UiButton>
          <UiButton v-if="slotId !== 'center'" variant="ghost" size="sm" role="menuitem" @click="moveCurrentStack('center')">
            <Rows3 data-icon="inline-start" />
            Move stack center
          </UiButton>
          <UiButton v-if="slotId !== 'right'" variant="ghost" size="sm" role="menuitem" @click="moveCurrentStack('right')">
            <ArrowRightToLine data-icon="inline-start" />
            Move stack right
          </UiButton>
          <UiButton variant="ghost" size="sm" role="menuitem" @click="moveCurrentStack(slotId, stackId === 'primary' ? 'secondary' : 'primary')">
            <component :is="stackId === 'primary' ? ArrowDownToLine : ArrowUpToLine" data-icon="inline-start" />
            {{ stackId === 'primary' ? 'Move stack below' : 'Move stack above' }}
          </UiButton>
          <UiButton v-if="hasSplit" variant="ghost" size="sm" role="menuitem" @click="mergeCurrentSplit">
            <Combine data-icon="inline-start" />
            Merge split
          </UiButton>
          <UiButton variant="ghost" size="sm" role="menuitem" @click="collapseCurrentColumn">
            <PanelLeftClose data-icon="inline-start" />
            Collapse column
          </UiButton>
          <UiSeparator />
          <UiButton
            v-if="descriptor.closable"
            variant="ghost"
            size="sm"
            role="menuitem"
            @click="closeCard"
          >
            <X data-icon="inline-start" />
            Close
          </UiButton>
          <UiButton v-if="descriptor.detachable" variant="ghost" size="sm" role="menuitem" @click="detachCard">
            <ExternalLink data-icon="inline-start" />
            Detach window
          </UiButton>
        </div>
      </UiDropdownMenu>
    </header>

    <div class="workbench-card-content">
      <component :is="descriptor.component" />
    </div>
  </article>
</template>

<style scoped>
.workbench-card-surface {
  position: absolute;
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: visible;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  box-shadow: var(--shadow-panel);
  background: var(--surface-section);
  transition: left 180ms ease, top 180ms ease, width 180ms ease, height 180ms ease, opacity 140ms ease;
}

.workbench-card-surface.is-dragging {
  opacity: 0.64;
}

.workbench-card-header {
  display: flex;
  align-items: center;
  height: 34px;
  flex: 0 0 34px;
  min-width: 0;
  padding: 2px 4px;
  border-bottom: 1px solid var(--border-muted);
  border-radius: 8px 8px 0 0;
  background: var(--surface-chrome);
}

.workbench-card-grip,
.workbench-card-action {
  width: 28px;
  height: 28px;
  flex: 0 0 28px;
  cursor: grab;
}

.workbench-card-action {
  cursor: pointer;
}

.workbench-card-tabs {
  display: flex;
  align-items: stretch;
  min-width: 0;
  flex: 1;
  height: 30px;
  overflow-x: auto;
  scrollbar-width: none;
}

.workbench-card-tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  max-width: 180px;
  padding: 0 10px;
  border: 0;
  border-bottom: 2px solid transparent;
  color: var(--text-muted);
  background: transparent;
  cursor: pointer;
  touch-action: none;
}

.workbench-card-tab svg {
  width: 14px;
  height: 14px;
  flex: 0 0 14px;
}

.workbench-card-tab span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.workbench-card-tab.is-active {
  color: var(--text-primary);
  border-bottom-color: var(--accent);
  background: var(--surface-active);
}

.workbench-card-tab.is-drop-target {
  box-shadow: inset 2px 0 0 var(--accent-primary);
}

.workbench-card-content {
  position: relative;
  min-width: 0;
  min-height: 0;
  flex: 1;
  overflow: hidden;
  border-radius: 0 0 8px 8px;
}

.workbench-card-menu {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.workbench-card-menu :deep(button) {
  width: 100%;
  justify-content: flex-start;
}

.workbench-card-menu-label {
  padding: 3px 8px 1px;
  color: var(--text-muted);
  font-size: 11px;
  font-weight: 600;
}

@media (prefers-reduced-motion: reduce) {
  .workbench-card-surface { transition: none; }
}
</style>
