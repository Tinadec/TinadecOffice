<script setup lang="ts">
import { cn } from '@/lib/utils'
import { computed, ref } from 'vue'

/**
 * 2D saturation × lightness color area — the replacement for the native
 * `<input type="color">` swatch, which renders as an OS-drawn control that
 * ignores the app's design tokens.
 *
 * Controlled widget: the host owns HSL state and the hex value, this component
 * only reports direct manipulation. Saturation runs left→right, lightness
 * bottom→top, both over the host's current hue.
 */
interface Props {
  hue: number
  saturation: number
  lightness: number
  /** Accessible name for the area itself. */
  label: string
  /** Localized axis names for the spoken value text. */
  saturationLabel: string
  lightnessLabel: string
  class?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  /** Fired before a value change so the host can mark it as direct manipulation. */
  'direct-input': []
  'update:saturation': [value: number]
  'update:lightness': [value: number]
}>()

const area = ref<HTMLElement | null>(null)
const dragging = ref(false)

function clamp(value: number): number {
  return Math.min(100, Math.max(0, Math.round(value)))
}

/**
 * Lightness as a black→transparent→white overlay on top of the saturation ramp.
 * `rgba(0, 0, 0, 0)` (not `transparent`) so the middle stop does not interpolate
 * through transparent-black and darken the field. Set as `background-image`
 * (not the `background` shorthand) so both layers survive DOM implementations
 * that only keep the last shorthand layer.
 */
const backgroundImage = computed(() => [
  'linear-gradient(to top, #000 0%, rgba(0, 0, 0, 0) 50%, #fff 100%)',
  `linear-gradient(to right, hsl(${props.hue} 0% 50%), hsl(${props.hue} 100% 50%))`,
].join(', '))

const handleColor = computed(() => `hsl(${props.hue} ${props.saturation}% ${props.lightness}%)`)

const valueText = computed(
  () => `${props.saturationLabel} ${props.saturation}% / ${props.lightnessLabel} ${props.lightness}%`,
)

function emitFromPoint(clientX: number, clientY: number): void {
  const el = area.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  if (!rect.width || !rect.height) return
  const saturation = clamp(((clientX - rect.left) / rect.width) * 100)
  const lightness = clamp((1 - (clientY - rect.top) / rect.height) * 100)
  emit('direct-input')
  emit('update:saturation', saturation)
  emit('update:lightness', lightness)
}

function onPointerDown(event: PointerEvent): void {
  if (event.button !== 0) return
  dragging.value = true
  area.value?.setPointerCapture(event.pointerId)
  emitFromPoint(event.clientX, event.clientY)
}

function onPointerMove(event: PointerEvent): void {
  if (!dragging.value) return
  emitFromPoint(event.clientX, event.clientY)
}

function onPointerUp(event: PointerEvent): void {
  if (!dragging.value) return
  dragging.value = false
  if (area.value?.hasPointerCapture(event.pointerId)) {
    area.value.releasePointerCapture(event.pointerId)
  }
}

/** Arrow keys move one axis at a time; Shift jumps in tens. */
function onKeydown(event: KeyboardEvent): void {
  const step = event.shiftKey ? 10 : 1
  let saturation = props.saturation
  let lightness = props.lightness
  switch (event.key) {
    case 'ArrowLeft':
      saturation -= step
      break
    case 'ArrowRight':
      saturation += step
      break
    case 'ArrowUp':
      lightness += step
      break
    case 'ArrowDown':
      lightness -= step
      break
    case 'Home':
      saturation = 0
      break
    case 'End':
      saturation = 100
      break
    default:
      return
  }
  event.preventDefault()
  emit('direct-input')
  emit('update:saturation', clamp(saturation))
  emit('update:lightness', clamp(lightness))
}
</script>

<template>
  <div
    ref="area"
    :class="cn(
      'relative h-28 w-full cursor-crosshair touch-none select-none overflow-hidden rounded-md border border-input shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
      props.class,
    )"
    :style="{ backgroundImage }"
    role="slider"
    tabindex="0"
    :aria-label="label"
    aria-valuemin="0"
    aria-valuemax="100"
    :aria-valuenow="saturation"
    :aria-valuetext="valueText"
    @pointerdown="onPointerDown"
    @pointermove="onPointerMove"
    @pointerup="onPointerUp"
    @pointercancel="onPointerUp"
    @keydown="onKeydown"
  >
    <span
      class="pointer-events-none absolute size-4 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-white shadow-[0_0_0_1px_rgba(0,0,0,0.45)]"
      :style="{ left: `${saturation}%`, top: `${100 - lightness}%`, background: handleColor }"
    />
  </div>
</template>
