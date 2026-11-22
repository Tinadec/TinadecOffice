<script setup lang="ts">
import { computed, ref } from 'vue'
import { Eraser, Highlighter, Pen } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import {
  SPATIAL_WHITEBOARD_DEFAULTS,
  buildSmoothStrokePath,
  isPointNearStroke,
  type SpatialDrawingMode,
  type SpatialDrawingProps,
  type SpatialToolMode,
  type SpatialViewport,
} from './spatialWhiteboardTypes'

export interface SpatialDrawingStrokeLike {
  id: string
  props: Record<string, unknown>
}

const props = withDefaults(defineProps<{
  strokes: SpatialDrawingStrokeLike[]
  viewport: SpatialViewport
  tool: SpatialToolMode
  color?: string
  strokeWidth?: number
  eraserRadius?: number
}>(), {
  color: '',
  strokeWidth: 0,
  eraserRadius: 14,
})

const emit = defineEmits<{
  'stroke-complete': [stroke: SpatialDrawingProps]
  'stroke-erase': [id: string]
}>()

const { t } = useI18n()

const svgRef = ref<SVGSVGElement | null>(null)
const drawing = ref(false)
const erasing = ref(false)
const activePoints = ref<[number, number][]>([])
const hoveredStrokeId = ref<string | null>(null)
const erasedThisSweep = new Set<string>()

const isDrawTool = computed(() => props.tool === 'pen' || props.tool === 'highlighter')
const isEraserTool = computed(() => props.tool === 'eraser')
const interactive = computed(() => isDrawTool.value || isEraserTool.value)

const currentMode = computed<SpatialDrawingMode>(() =>
  props.tool === 'highlighter' ? 'highlighter' : 'pen',
)

const resolvedColor = computed(() => {
  if (props.color) return props.color
  return currentMode.value === 'highlighter'
    ? SPATIAL_WHITEBOARD_DEFAULTS.highlighter.color
    : SPATIAL_WHITEBOARD_DEFAULTS.pen.color
})

const resolvedStrokeWidth = computed(() => {
  if (props.strokeWidth > 0) return props.strokeWidth
  return currentMode.value === 'highlighter'
    ? SPATIAL_WHITEBOARD_DEFAULTS.highlighter.strokeWidth
    : SPATIAL_WHITEBOARD_DEFAULTS.pen.strokeWidth
})

const worldTransform = computed(
  () => `translate(${props.viewport.x} ${props.viewport.y}) scale(${props.viewport.zoom})`,
)

interface RenderedStroke {
  id: string
  path: string
  points: [number, number][]
  color: string
  strokeWidth: number
  mode: SpatialDrawingMode
}

const renderedStrokes = computed<RenderedStroke[]>(() => {
  const out: RenderedStroke[] = []
  for (const item of props.strokes) {
    const rawPoints = item.props?.points
    if (!Array.isArray(rawPoints) || rawPoints.length === 0) continue
    const points: [number, number][] = []
    for (const pt of rawPoints) {
      if (Array.isArray(pt) && pt.length >= 2 && Number.isFinite(Number(pt[0])) && Number.isFinite(Number(pt[1]))) {
        points.push([Number(pt[0]), Number(pt[1])])
      }
    }
    if (!points.length) continue
    const mode: SpatialDrawingMode = item.props?.mode === 'highlighter' ? 'highlighter' : 'pen'
    const strokeWidth = typeof item.props?.strokeWidth === 'number' && item.props.strokeWidth > 0
      ? item.props.strokeWidth
      : mode === 'highlighter'
        ? SPATIAL_WHITEBOARD_DEFAULTS.highlighter.strokeWidth
        : SPATIAL_WHITEBOARD_DEFAULTS.pen.strokeWidth
    const color = typeof item.props?.color === 'string' && item.props.color
      ? item.props.color
      : mode === 'highlighter'
        ? SPATIAL_WHITEBOARD_DEFAULTS.highlighter.color
        : SPATIAL_WHITEBOARD_DEFAULTS.pen.color
    out.push({
      id: item.id,
      path: buildSmoothStrokePath(points),
      points,
      color,
      strokeWidth,
      mode,
    })
  }
  return out
})

const activePreviewPath = computed(() => buildSmoothStrokePath(activePoints.value))

function toWorldPoint(event: PointerEvent): [number, number] {
  const rect = svgRef.value?.getBoundingClientRect()
  const left = rect?.left ?? 0
  const top = rect?.top ?? 0
  const zoom = Math.max(0.05, props.viewport.zoom || 1)
  return [
    (event.clientX - left - props.viewport.x) / zoom,
    (event.clientY - top - props.viewport.y) / zoom,
  ]
}

function findHitStrokes(wx: number, wy: number): RenderedStroke[] {
  const zoom = Math.max(0.2, props.viewport.zoom || 1)
  const baseRadius = props.eraserRadius / zoom
  return renderedStrokes.value.filter((stroke) =>
    isPointNearStroke(wx, wy, stroke.points, baseRadius + stroke.strokeWidth / 2),
  )
}

function eraseAtPoint(wx: number, wy: number) {
  const hits = findHitStrokes(wx, wy)
  for (const hit of hits) {
    if (!erasedThisSweep.has(hit.id)) {
      erasedThisSweep.add(hit.id)
      emit('stroke-erase', hit.id)
    }
  }
}

function onPointerDown(event: PointerEvent) {
  if (!interactive.value || event.button !== 0) return
  event.stopPropagation()
  const [wx, wy] = toWorldPoint(event)
  if (typeof svgRef.value?.setPointerCapture === 'function') {
    try {
      svgRef.value.setPointerCapture(event.pointerId)
    } catch {
      // Synthetic PointerEvent in tests may not have an active pointerId.
    }
  }
  if (isEraserTool.value) {
    erasing.value = true
    erasedThisSweep.clear()
    eraseAtPoint(wx, wy)
    return
  }
  drawing.value = true
  activePoints.value = [[wx, wy]]
}

function onPointerMove(event: PointerEvent) {
  if (!interactive.value) return
  const [wx, wy] = toWorldPoint(event)
  if (isEraserTool.value) {
    const hits = findHitStrokes(wx, wy)
    hoveredStrokeId.value = hits[0]?.id ?? null
    if (erasing.value) {
      event.stopPropagation()
      eraseAtPoint(wx, wy)
    }
    return
  }
  if (!drawing.value) return
  event.stopPropagation()
  const last = activePoints.value[activePoints.value.length - 1]
  if (last) {
    const dx = wx - last[0]
    const dy = wy - last[1]
    if (dx * dx + dy * dy < 2.25) return
  }
  activePoints.value = [...activePoints.value, [wx, wy]]
}

function finishPointer(event?: PointerEvent) {
  if (event && typeof svgRef.value?.releasePointerCapture === 'function') {
    try {
      svgRef.value.releasePointerCapture(event.pointerId)
    } catch {
      // Ignore uncaptured synthetic pointers.
    }
  }
  if (erasing.value) {
    erasing.value = false
    erasedThisSweep.clear()
  }
  if (drawing.value) {
    drawing.value = false
    const pts = activePoints.value
    activePoints.value = []
    if (pts.length > 0) {
      emit('stroke-complete', {
        points: pts,
        color: resolvedColor.value,
        strokeWidth: resolvedStrokeWidth.value,
        mode: currentMode.value,
      })
    }
  }
}

function onStrokeClick(id: string) {
  if (!isEraserTool.value) return
  emit('stroke-erase', id)
}
</script>

<template>
  <div
    class="spatial-drawing-layer"
    :class="{
      'is-interactive': interactive,
      'is-eraser': isEraserTool,
      'is-pen': tool === 'pen',
      'is-highlighter': tool === 'highlighter',
    }"
    data-testid="spatial-drawing-layer"
  >
    <svg
      ref="svgRef"
      class="spatial-drawing-svg"
      @pointerdown="onPointerDown"
      @pointermove="onPointerMove"
      @pointerup="finishPointer"
      @pointercancel="finishPointer"
      @pointerleave="hoveredStrokeId = null"
    >
      <g :transform="worldTransform" data-testid="spatial-drawing-world">
        <path
          v-for="stroke in renderedStrokes"
          :key="stroke.id"
          :d="stroke.path"
          :stroke="stroke.color"
          :stroke-width="stroke.strokeWidth"
          :stroke-opacity="stroke.mode === 'highlighter' ? 0.34 : 0.96"
          fill="none"
          stroke-linecap="round"
          stroke-linejoin="round"
          class="spatial-drawing-stroke"
          :class="{
            'is-highlighter': stroke.mode === 'highlighter',
            'is-erase-hover': isEraserTool && hoveredStrokeId === stroke.id,
          }"
          :data-stroke-id="stroke.id"
          @click.stop="onStrokeClick(stroke.id)"
        />
        <path
          v-if="drawing && activePreviewPath"
          :d="activePreviewPath"
          :stroke="resolvedColor"
          :stroke-width="resolvedStrokeWidth"
          :stroke-opacity="currentMode === 'highlighter' ? 0.34 : 0.96"
          fill="none"
          stroke-linecap="round"
          stroke-linejoin="round"
          class="spatial-drawing-stroke is-preview"
          data-testid="spatial-drawing-preview"
        />
      </g>
    </svg>

    <div v-if="interactive" class="spatial-drawing-hint" role="status" data-testid="spatial-drawing-hint">
      <Eraser v-if="isEraserTool" :size="13" aria-hidden="true" />
      <Highlighter v-else-if="tool === 'highlighter'" :size="13" aria-hidden="true" />
      <Pen v-else :size="13" aria-hidden="true" />
      <span>
        {{
          isEraserTool
            ? t('space.whiteboard.eraserHint')
            : tool === 'highlighter'
              ? t('space.whiteboard.highlighterMode')
              : t('space.whiteboard.penMode')
        }}
      </span>
    </div>
  </div>
</template>

<style scoped>
.spatial-drawing-layer {
  position: absolute;
  inset: 0;
  pointer-events: none;
  z-index: 4;
  overflow: hidden;
}

.spatial-drawing-layer.is-interactive {
  pointer-events: auto;
  touch-action: none;
}

.spatial-drawing-layer.is-pen,
.spatial-drawing-layer.is-highlighter {
  cursor: crosshair;
}

.spatial-drawing-layer.is-eraser {
  cursor: cell;
}

.spatial-drawing-svg {
  width: 100%;
  height: 100%;
  display: block;
  overflow: visible;
}

.spatial-drawing-stroke {
  transition: opacity 110ms ease, filter 110ms ease;
}

.spatial-drawing-stroke.is-erase-hover {
  opacity: 0.42;
  filter: drop-shadow(0 0 4px var(--accent-danger, #f85149));
}

.spatial-drawing-hint {
  position: absolute;
  top: 64px;
  left: 50%;
  transform: translateX(-50%);
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 12px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: color-mix(in srgb, var(--surface-raised, #ffffff) 90%, transparent);
  backdrop-filter: blur(12px);
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  pointer-events: none;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.1);
}
</style>
