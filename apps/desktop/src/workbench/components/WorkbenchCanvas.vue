<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { PanelLeftOpen } from '@lucide/vue'
import { resolveWorkbenchLayout } from '../constraints'
import { usePanelStyles } from '@/composables/usePanelStyles'
import WorkbenchCardSurface from './WorkbenchCardSurface.vue'
import WorkbenchEmptyStack from './WorkbenchEmptyStack.vue'
import { listWorkbenchCardDescriptors } from '../registry'
import type {
  PersistedCardInstance,
  ResolvedWorkbenchStack,
  WorkbenchCardDescriptor,
  WorkbenchLayoutCommand,
  WorkbenchLayoutSnapshot,
  WorkbenchPageId,
  WorkbenchRect,
  WorkbenchSlotId,
  WorkbenchStackId,
} from '../types'

const props = defineProps<{
  pageId: WorkbenchPageId
  snapshot: WorkbenchLayoutSnapshot
  descriptors: ReadonlyMap<string, WorkbenchCardDescriptor>
  active: boolean
  locked?: boolean
}>()

const emit = defineEmits<{
  command: [command: WorkbenchLayoutCommand, coalesceKey?: string]
  detach: [card: PersistedCardInstance, pageId: WorkbenchPageId]
  updateState: [cardId: string, state: Record<string, unknown>]
  finishCoalescing: []
}>()

const canvasRef = ref<HTMLElement | null>(null)
const viewport = ref({ width: 0, height: 0 })
const drag = ref<{ cardId: string; clientX: number; clientY: number } | null>(null)
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
let resizeObserver: ResizeObserver | null = null

const resolved = computed(() => resolveWorkbenchLayout(props.snapshot, viewport.value, props.descriptors))
const panelStyle = computed(() => getPanelStyle())
const panelDataAttributes = computed(() => getPanelDataAttributes())

const stackByCard = computed(() => {
  const placements = new Map<string, ResolvedWorkbenchStack>()
  for (const column of resolved.value.columns) {
    for (const stack of column.stacks) {
      for (const cardId of stack.cardIds) placements.set(cardId, stack)
    }
  }
  return placements
})

const cards = computed(() => Object.values(props.snapshot.cards).map((card) => {
  const stack = stackByCard.value.get(card.id)
  return {
    card,
    descriptor: props.descriptors.get(card.type),
    stack,
    rect: stack?.rect ?? { x: 0, y: 0, width: 0, height: 0 },
    active: stack?.activeCardId === card.id,
    stackCards: stack?.cardIds.map((id) => props.snapshot.cards[id]).filter(Boolean) ?? [],
    hasSplit: stack ? Boolean(props.snapshot.columns[stack.slotId].secondary) : false,
  }
}).filter((entry): entry is typeof entry & { descriptor: WorkbenchCardDescriptor } => Boolean(entry.descriptor)))

const availableCards = computed(() => listWorkbenchCardDescriptors().filter((descriptor) => {
  if (descriptor.pages && !descriptor.pages.includes(props.pageId)) return false
  if (!descriptor.singleton) return true
  return !Object.values(props.snapshot.cards).some((card) => card.type === descriptor.type)
}))

const emptyStacks = computed(() => resolved.value.columns.flatMap((column) =>
  column.stacks.filter((stack) => stack.cardIds.length === 0),
))

const dropTarget = computed(() => {
  if (!drag.value || !canvasRef.value) return null
  const bounds = canvasRef.value.getBoundingClientRect()
  const x = drag.value.clientX - bounds.left
  const y = drag.value.clientY - bounds.top
  for (const column of resolved.value.columns) {
    if (column.collapsed || column.rect.width <= 0) continue
    const rect = column.rect
    if (x < rect.x || x > rect.x + rect.width || y < rect.y || y > rect.y + rect.height) continue
    const secondary = y > rect.y + rect.height * 0.58
    const targetRect: WorkbenchRect = secondary
      ? { x: rect.x, y: rect.y + rect.height * 0.65 + 4, width: rect.width, height: rect.height * 0.35 - 4 }
      : { x: rect.x, y: rect.y, width: rect.width, height: rect.height * (column.stacks.length === 2 ? 0.65 : 1) }
    return { slotId: column.slotId, secondary, rect: targetRect }
  }
  return null
})

function dispatch(command: WorkbenchLayoutCommand, coalesceKey?: string): void {
  emit('command', command, coalesceKey)
}

function activateCard(cardId: string): void {
  dispatch({ kind: 'activateCard', cardId })
}

function moveCard(
  cardId: string,
  slotId: WorkbenchSlotId,
  stackId: WorkbenchStackId = 'primary',
  index?: number,
): void {
  dispatch({ kind: 'moveCard', cardId, slotId, stackId, index })
}

function moveStack(
  fromSlotId: WorkbenchSlotId,
  fromStackId: WorkbenchStackId,
  toSlotId: WorkbenchSlotId,
  toStackId: WorkbenchStackId,
): void {
  dispatch({ kind: 'moveStack', fromSlotId, fromStackId, toSlotId, toStackId })
}

function mergeStack(slotId: WorkbenchSlotId): void {
  dispatch({ kind: 'mergeStack', slotId })
}

function collapseColumn(slotId: WorkbenchSlotId): void {
  dispatch({ kind: 'collapseColumn', slotId, collapsed: true })
}

function closeCard(cardId: string): void {
  dispatch({ kind: 'closeCard', cardId })
}

function updateCardState(cardId: string, state: Record<string, unknown>): void {
  emit('updateState', cardId, state)
}

function openCard(type: string, stack: ResolvedWorkbenchStack): void {
  const descriptor = props.descriptors.get(type)
  if (!descriptor) return
  const id = descriptor.singleton
    ? `${props.pageId}:${type}`
    : `${props.pageId}:${type}:${Date.now()}:${Math.random().toString(36).slice(2, 7)}`
  dispatch({ kind: 'openCard', card: { id, type, state: {} }, slotId: stack.slotId, stackId: stack.stackId })
}

function expandColumn(slotId: WorkbenchSlotId): void {
  dispatch({ kind: 'collapseColumn', slotId, collapsed: false })
}

function startColumnResize(event: PointerEvent, index: number): void {
  const leftColumn = resolved.value.columns[index]
  const rightColumn = resolved.value.columns[index + 1]
  if (!leftColumn || !rightColumn || leftColumn.collapsed || rightColumn.collapsed) return
  const startX = event.clientX
  const resizeSlot = leftColumn.slotId === 'center' ? rightColumn.slotId : leftColumn.slotId
  const startWidth = props.snapshot.columns[resizeSlot].width
  const direction = leftColumn.slotId === 'center' ? -1 : 1
  const move = (moveEvent: PointerEvent) => dispatch(
    { kind: 'resizeColumn', slotId: resizeSlot, width: startWidth + (moveEvent.clientX - startX) * direction },
    `column:${resizeSlot}`,
  )
  const end = () => {
    emit('finishCoalescing')
    document.removeEventListener('pointermove', move)
    document.removeEventListener('pointerup', end)
  }
  document.addEventListener('pointermove', move)
  document.addEventListener('pointerup', end)
}

function startSplitResize(event: PointerEvent, slotId: WorkbenchSlotId): void {
  if (props.locked) return
  const column = resolved.value.columns.find((entry) => entry.slotId === slotId)
  if (!column || column.rect.height <= 0) return
  const boundsTop = canvasRef.value?.getBoundingClientRect().top ?? 0
  const move = (moveEvent: PointerEvent) => dispatch(
    { kind: 'resizeSplit', slotId, ratio: (moveEvent.clientY - boundsTop - column.rect.y) / column.rect.height },
    `split:${slotId}`,
  )
  const end = () => {
    emit('finishCoalescing')
    document.removeEventListener('pointermove', move)
    document.removeEventListener('pointerup', end)
  }
  document.addEventListener('pointermove', move)
  document.addEventListener('pointerup', end)
}

function onDragStart(cardId: string): void {
  drag.value = { cardId, clientX: 0, clientY: 0 }
}

function onDragMove(cardId: string, clientX: number, clientY: number): void {
  drag.value = { cardId, clientX, clientY }
}

function onDragEnd(cardId: string, clientX: number, clientY: number): void {
  drag.value = { cardId, clientX, clientY }
  const target = dropTarget.value
  if (target) moveCard(cardId, target.slotId, target.secondary ? 'secondary' : 'primary')
  drag.value = null
}

onMounted(async () => {
  await nextTick()
  if (!canvasRef.value) return
  resizeObserver = new ResizeObserver(([entry]) => {
    viewport.value = { width: entry.contentRect.width, height: entry.contentRect.height }
  })
  resizeObserver.observe(canvasRef.value)
  const rect = canvasRef.value.getBoundingClientRect()
  viewport.value = { width: rect.width, height: rect.height }
})

onBeforeUnmount(() => resizeObserver?.disconnect())
</script>

<template>
  <section
    ref="canvasRef"
    class="workbench-canvas"
    :class="{ 'is-active': active }"
    :aria-hidden="!active"
    :inert="active ? undefined : true"
  >
    <WorkbenchCardSurface
      v-for="entry in cards"
      :key="entry.card.id"
      :card="entry.card"
      :descriptor="entry.descriptor"
      :rect="entry.rect"
      :stack-cards="entry.stackCards"
      :slot-id="entry.stack?.slotId ?? 'center'"
      :stack-id="entry.stack?.stackId ?? 'primary'"
      :has-split="entry.hasSplit"
      :descriptors="descriptors"
      :active="entry.active"
      :page-active="active"
      :locked="Boolean(locked)"
      :page-id="pageId"
      :panel-style="panelStyle"
      :panel-data-attributes="panelDataAttributes"
      @activate="activateCard"
      @move="moveCard"
      @move-stack="moveStack"
      @merge-stack="mergeStack"
      @collapse-column="collapseColumn"
      @close="closeCard"
      @detach="emit('detach', $event, pageId)"
      @update-state="updateCardState"
      @drag-start="onDragStart"
      @drag-move="onDragMove"
      @drag-end="onDragEnd"
    />

    <WorkbenchEmptyStack
      v-for="stack in emptyStacks"
      :key="`${stack.slotId}:${stack.stackId}`"
      :rect="stack.rect"
      :available-cards="availableCards"
      :panel-style="panelStyle"
      :panel-data-attributes="panelDataAttributes"
      @open="openCard($event, stack)"
    />

    <button
      v-for="column in resolved.columns.filter((entry) => entry.collapsed && entry.rect.width > 0)"
      :key="`collapsed:${column.slotId}`"
      class="workbench-collapsed-rail"
      :style="{ left: `${column.rect.x}px`, top: `${column.rect.y}px`, width: `${column.rect.width}px`, height: `${column.rect.height}px`, ...panelStyle }"
      v-bind="panelDataAttributes"
      :title="`Restore ${column.slotId} column`"
      @click="expandColumn(column.slotId)"
    >
      <PanelLeftOpen />
    </button>

    <button
      v-for="(_, index) in resolved.columns.slice(0, -1)"
      :key="`column-resizer:${index}`"
      class="workbench-column-resizer"
      :style="{ left: `${resolved.columns[index].rect.x + resolved.columns[index].rect.width + 2}px` }"
      aria-label="Resize columns"
      @pointerdown="startColumnResize($event, index)"
    />
    <button
      v-for="column in resolved.columns.filter((entry) => entry.stacks.length === 2)"
      :key="`split-resizer:${column.slotId}`"
      class="workbench-split-resizer"
      :style="{ left: `${column.rect.x}px`, top: `${column.stacks[0].rect.y + column.stacks[0].rect.height + 2}px`, width: `${column.rect.width}px` }"
      aria-label="Resize split"
      @pointerdown="startSplitResize($event, column.slotId)"
    />
    <div
      v-if="dropTarget"
      class="workbench-drop-target"
      :style="{ left: `${dropTarget.rect.x}px`, top: `${dropTarget.rect.y}px`, width: `${dropTarget.rect.width}px`, height: `${dropTarget.rect.height}px` }"
    />
  </section>
</template>

<style scoped>
.workbench-canvas { position: absolute; inset: 40px 0 0; min-width: 0; min-height: 0; visibility: hidden; opacity: 0; pointer-events: none; transition: opacity 140ms ease; }
.workbench-canvas.is-active { visibility: visible; opacity: 1; pointer-events: auto; }
.workbench-collapsed-rail { position: absolute; display: grid; place-items: center; border: 1px solid var(--border-muted); border-radius: 8px; color: var(--text-muted); background: var(--surface-section); cursor: pointer; }
.workbench-collapsed-rail svg { width: 16px; height: 16px; }
.workbench-column-resizer, .workbench-split-resizer { position: absolute; z-index: 20; border: 0; background: transparent; touch-action: none; }
.workbench-column-resizer { top: 8px; bottom: 8px; width: 4px; cursor: col-resize; }
.workbench-split-resizer { height: 4px; cursor: row-resize; }
.workbench-column-resizer:hover, .workbench-split-resizer:hover { background: var(--accent-primary); }
.workbench-drop-target { position: absolute; z-index: 25; border: 2px solid var(--accent-primary); border-radius: 8px; background: color-mix(in srgb, var(--accent-primary) 12%, transparent); pointer-events: none; }
@media (prefers-reduced-motion: reduce) { .workbench-canvas { transition: none; } }
</style>
