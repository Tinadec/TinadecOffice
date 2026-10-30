<script setup lang="ts">
/**
 * A pet standing on a stage, animated frame by frame.
 *
 * The atlas is an 8-column grid of 192x208 frames. The stage shows one frame at a time by scaling
 * the whole sheet to `height * rows` tall and walking `background-position` across the row — the
 * same pixels `DesktopPetPage` draws to a canvas, without the canvas. That matters here: the
 * companion window is `app://` while pets come from `tinadec-pet://`, so a canvas would be tainted
 * and its pixels unreadable, whereas CSS background positioning has no such restriction.
 *
 * The frame sequence comes from `petFrameAt`, the one place that knows each state's row, frame count
 * and duration. Nothing about the animation is re-derived here.
 */
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useAgentAvatars } from '@/composables/useAgentAvatars'
import { PET_STATES, petFrameAt, parsePetSprite, type PetState } from '@/pets/petRuntime'

const props = withDefaults(defineProps<{
  petSlug: string
  /** Which row of the atlas to play. */
  state?: PetState
  /** Rendered height of one frame in px; the width follows the 192:208 aspect. */
  height?: number
  alt?: string
}>(), {
  state: 'idle',
  height: 160,
  alt: '',
})

const { urlForSlug } = useAgentAvatars()
const url = computed(() => urlForSlug(props.petSlug))

interface StageLayout {
  frameWidth: number
  rows: number
}

const layout = ref<StageLayout | null>(null)
const frame = ref<{ row: number; column: number }>({ row: 0, column: 0 })

let probe: HTMLImageElement | null = null
let raf = 0
let startedAt = 0

/** Measure the atlas once per URL; a missing sprite degrades to nothing rather than a broken box. */
watch(url, (next) => {
  layout.value = null
  if (!next) return
  const image = new Image()
  probe = image
  image.onload = () => {
    if (probe !== image) return
    try {
      const parsed = parsePetSprite(image.naturalWidth, image.naturalHeight)
      layout.value = { frameWidth: parsed.frameWidth, rows: parsed.rows }
    } catch {
      // Not a valid atlas: leave the stage empty instead of showing a slice of the wrong grid.
      layout.value = null
    }
  }
  image.onerror = () => {
    if (probe === image) layout.value = null
  }
  image.src = next
}, { immediate: true })

function stop(): void {
  if (raf) cancelAnimationFrame(raf)
  raf = 0
}

/**
 * Only write when the frame actually changes. `petFrameAt` runs at display rate, but a six-frame
 * row over 1.1s only changes about five times a second — reassigning every tick would re-render
 * the stage sixty times a second to show the same picture.
 */
function tick(now: number): void {
  const animation = PET_STATES[props.state] ?? PET_STATES.idle
  const elapsed = Math.max(0, now - startedAt)
  const next = petFrameAt(props.state, elapsed)
  if (next.column !== frame.value.column || next.row !== frame.value.row) {
    frame.value = { row: next.row, column: next.column }
  }
  raf = requestAnimationFrame(tick)
}

watch(
  () => [props.state, url.value] as const,
  () => {
    // Start on frame 0 of the requested row so a state change lands immediately instead of waiting
    // out the previous row's cycle.
    frame.value = { row: (PET_STATES[props.state] ?? PET_STATES.idle).row, column: 0 }
    stop()
    startedAt = performance.now()
    raf = requestAnimationFrame(tick)
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  stop()
  if (probe) {
    probe.onload = null
    probe.onerror = null
    probe = null
  }
})

const style = computed(() => {
  if (!url.value || !layout.value) return {}
  const { frameWidth, rows } = layout.value
  const renderedFrameWidth = frameWidth * (props.height / 208)
  return {
    width: `${renderedFrameWidth}px`,
    height: `${props.height}px`,
    backgroundImage: `url("${url.value}")`,
    backgroundSize: `${renderedFrameWidth * 8}px ${props.height * rows}px`,
    backgroundPosition: `${-frame.value.column * renderedFrameWidth}px ${-frame.value.row * props.height}px`,
  }
})
</script>

<template>
  <div
    class="companion-stage"
    :class="`companion-stage--${state}`"
    role="img"
    :aria-label="alt"
    data-testid="companion-stage"
  >
    <div v-if="Object.keys(style).length" class="companion-stage-pet" :style="style" />
  </div>
</template>

<style scoped>
/*
 * The stage paints the character's shadow itself: the window is transparent and has no system
 * shadow, so without this the pet floats on the desktop with nothing holding it down.
 */
.companion-stage {
  position: relative;
  display: flex;
  flex: none;
  align-items: flex-end;
  justify-content: center;
  overflow: hidden;
}

.companion-stage-pet {
  background-repeat: no-repeat;
  image-rendering: pixelated;
  transform-origin: bottom center;
  filter: drop-shadow(0 6px 10px rgb(0 0 0 / 28%));
}

@media (prefers-reduced-motion: reduce) {
  /* The frames still advance — that is the character, not decoration — but the drop shadow's
     soft edge goes away with the rest of the motion budget. */
  .companion-stage-pet {
    filter: none;
  }
}
</style>
