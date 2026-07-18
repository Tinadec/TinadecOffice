<script setup lang="ts">
import { Grip, X } from '@lucide/vue'
import { onBeforeUnmount, onMounted, ref } from 'vue'
import {
  PETDEX_BASE_FRAME_HEIGHT,
  PETDEX_BASE_FRAME_WIDTH,
  parsePetSprite,
  petFrameAt,
  petSourceRect,
  type PetSpriteLayout,
  type PetState,
} from '@/pets/petRuntime'

const canvas = ref<HTMLCanvasElement | null>(null)
const pet = ref<DownloadedPet | null>(null)
const loaded = ref(false)
const dragging = ref(false)
const scale = ref(1)
let image: HTMLImageElement | null = null
let animationFrame = 0
let animationStarted = 0
let state: PetState = 'idle'
let clickThrough = false
let hasRenderedFrame = false
let layout: PetSpriteLayout | null = null
let dragStart: { pointerX: number; pointerY: number; windowX: number; windowY: number } | null = null
let resizeStart: { pointerX: number; pointerY: number; scale: number } | null = null

function setState(next: PetState) {
  state = next
  animationStarted = performance.now()
}

function draw(now: number) {
  const target = canvas.value
  if (!target || !image || !layout) {
    animationFrame = requestAnimationFrame(draw)
    return
  }

  const ratio = window.devicePixelRatio || 1
  const width = Math.max(1, Math.round(window.innerWidth * ratio))
  const height = Math.max(1, Math.round(window.innerHeight * ratio))
  if (target.width !== width || target.height !== height) {
    target.width = width
    target.height = height
  }

  let frame = petFrameAt(state, now - animationStarted)
  if (state === 'jumping' && frame.complete) {
    setState('idle')
    frame = petFrameAt('idle', 0)
  }
  const context = target.getContext('2d', { willReadFrequently: true })
  const source = petSourceRect(layout, frame)
  if (context) {
    try {
      context.save()
      context.globalCompositeOperation = 'copy'
      context.imageSmoothingEnabled = false
      context.drawImage(image, source.x, source.y, source.width, source.height, 0, 0, width, height)
      context.restore()
      hasRenderedFrame = true
    } catch {
      context.restore()
      loaded.value = false
      setClickThrough(false)
      return
    }
  }
  animationFrame = requestAnimationFrame(draw)
}

function setClickThrough(enabled: boolean) {
  if (clickThrough === enabled) return
  clickThrough = enabled
  void window.tinadec.pets.setCurrentClickThrough(enabled)
}

function updateHitTest(event: MouseEvent) {
  if (dragging.value || resizeStart) return setClickThrough(false)
  const target = event.target as HTMLElement
  if (target.closest('button')) return setClickThrough(false)
  if (!hasRenderedFrame) return setClickThrough(false)
  const context = canvas.value?.getContext('2d', { willReadFrequently: true })
  if (!context || !canvas.value) return
  const x = Math.min(canvas.value.width - 1, Math.max(0, Math.floor(event.clientX * canvas.value.width / window.innerWidth)))
  const y = Math.min(canvas.value.height - 1, Math.max(0, Math.floor(event.clientY * canvas.value.height / window.innerHeight)))
  setClickThrough(context.getImageData(x, y, 1, 1).data[3] < 12)
}

function startDrag(event: PointerEvent) {
  if (event.button !== 0) return
  const target = event.currentTarget as HTMLElement
  target.setPointerCapture(event.pointerId)
  dragStart = { pointerX: event.screenX, pointerY: event.screenY, windowX: window.screenX, windowY: window.screenY }
  dragging.value = true
  setState('running-right')
}

function moveDrag(event: PointerEvent) {
  if (!dragStart) return
  const direction: PetState = event.screenX < dragStart.pointerX ? 'running-left' : 'running-right'
  if (state !== direction) setState(direction)
  void window.tinadec.pets.setCurrentBounds({
    x: dragStart.windowX + event.screenX - dragStart.pointerX,
    y: dragStart.windowY + event.screenY - dragStart.pointerY,
  })
}

function stopDrag() {
  if (!dragStart) return
  dragStart = null
  dragging.value = false
  setState('idle')
}

function applyScale(next: number, keepBottom = false) {
  const value = Math.min(1.2, Math.max(0.18, next))
  const width = Math.round(PETDEX_BASE_FRAME_WIDTH * value)
  const height = Math.round(PETDEX_BASE_FRAME_HEIGHT * value)
  const bounds: { x?: number; y?: number; width: number; height: number; scale: number } = { width, height, scale: value }
  if (keepBottom) bounds.y = window.screenY + window.outerHeight - height
  scale.value = value
  void window.tinadec.pets.setCurrentBounds(bounds)
}

function zoom(event: WheelEvent) {
  event.preventDefault()
  applyScale(scale.value + (event.deltaY < 0 ? 0.06 : -0.06), true)
}

function startResize(event: PointerEvent) {
  event.stopPropagation()
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  resizeStart = { pointerX: event.screenX, pointerY: event.screenY, scale: scale.value }
}

function moveResize(event: PointerEvent) {
  if (!resizeStart) return
  const delta = Math.max(event.screenX - resizeStart.pointerX, event.screenY - resizeStart.pointerY)
  applyScale(resizeStart.scale + delta / 208)
}

function stopResize() {
  resizeStart = null
}

function jump() {
  if (!dragging.value) setState('jumping')
}

function closePet() {
  void window.tinadec.pets.closeCurrent()
}

onMounted(async () => {
  pet.value = await window.tinadec.pets.getCurrent()
  if (!pet.value?.imageDataUrl) return
  scale.value = pet.value.scale
  image = new Image()
  image.onload = () => {
    try {
      layout = parsePetSprite(image!.naturalWidth, image!.naturalHeight)
      loaded.value = true
      animationStarted = performance.now()
      animationFrame = requestAnimationFrame(draw)
    } catch {
      loaded.value = false
      setClickThrough(false)
    }
  }
  image.onerror = () => setClickThrough(false)
  image.src = pet.value.imageDataUrl
})

onBeforeUnmount(() => {
  cancelAnimationFrame(animationFrame)
  setClickThrough(false)
})
</script>

<template>
  <main
    class="desktop-pet-window"
    :class="{ dragging }"
    aria-label="Pet window"
    @mousemove="updateHitTest"
    @wheel="zoom"
  >
    <canvas
      v-show="loaded"
      ref="canvas"
      class="pet-canvas"
      :aria-label="pet?.displayName ?? 'Desktop pet'"
      role="img"
      @dblclick="jump"
      @pointerdown="startDrag"
      @pointermove="moveDrag"
      @pointerup="stopDrag"
      @pointercancel="stopDrag"
    />
    <div v-if="!loaded" class="pet-placeholder">Pet</div>
    <button class="pet-control pet-close" type="button" title="Close pet" aria-label="Close pet" @click="closePet">
      <X :size="14" />
    </button>
    <button
      class="pet-control pet-resize"
      type="button"
      title="Resize pet"
      aria-label="Resize pet"
      @pointerdown="startResize"
      @pointermove="moveResize"
      @pointerup="stopResize"
      @pointercancel="stopResize"
    >
      <Grip :size="14" />
    </button>
  </main>
</template>

<style scoped>
:global(html),
:global(body),
:global(#app) {
  width: 100%;
  height: 100%;
  margin: 0;
  overflow: hidden;
  background: transparent !important;
}

.desktop-pet-window {
  position: relative;
  width: 100vw;
  height: 100vh;
  overflow: hidden;
  background: transparent;
}

.pet-canvas {
  display: block;
  width: 100%;
  height: 100%;
  cursor: grab;
  image-rendering: pixelated;
  touch-action: none;
  user-select: none;
}

.dragging .pet-canvas {
  cursor: grabbing;
}

.pet-placeholder {
  display: grid;
  width: 100%;
  height: 100%;
  place-items: center;
  color: rgb(255 255 255 / 80%);
  font: 600 14px/1 system-ui, sans-serif;
  letter-spacing: 0;
  text-transform: uppercase;
}

.pet-control {
  position: absolute;
  display: grid;
  width: 26px;
  height: 26px;
  padding: 0;
  place-items: center;
  border: 1px solid rgb(255 255 255 / 24%);
  border-radius: 4px;
  opacity: 0;
  color: white;
  background: rgb(20 20 24 / 72%);
  transition: opacity 120ms ease;
}

.desktop-pet-window:hover .pet-control,
.pet-control:focus-visible {
  opacity: 1;
}

.pet-close {
  top: 4px;
  right: 4px;
}

.pet-resize {
  right: 4px;
  bottom: 4px;
  cursor: nwse-resize;
}
</style>
