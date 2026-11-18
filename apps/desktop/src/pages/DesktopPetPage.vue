<script setup lang="ts">
import { Grip, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  PETDEX_BASE_FRAME_HEIGHT,
  PETDEX_BASE_FRAME_WIDTH,
  parsePetSprite,
  petFrameAt,
  petSourceRect,
  type PetSpriteLayout,
  type PetState,
} from '@/pets/petRuntime'

const { t } = useI18n()
const canvas = ref<HTMLCanvasElement | null>(null)
const pet = ref<DownloadedPet | null>(null)
const loaded = ref(false)
const dragging = ref(false)
const scale = ref(1)
let image: HTMLImageElement | null = null
let animationFrame = 0
let animationStarted = 0
let state: PetState = 'idle'
let dragState: PetState | null = null
let clickThrough = false
let hasRenderedFrame = false
let layout: PetSpriteLayout | null = null
let dragStart: { pointerX: number; pointerY: number; windowX: number; windowY: number } | null = null
let resizeStart: { pointerX: number; pointerY: number; scale: number } | null = null

function setState(next: PetState) {
  state = next
  animationStarted = performance.now()
}

// Drag and the one-shot jump win over the run status: the user is holding the pet, or the pet is
// mid-hop, and swapping the sprite underneath either would look broken.
function applyState() {
  if (dragState) return setState(dragState)
  if (state === 'jumping') return
  setState(runPetState.value)
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
    setState(runPetState.value)
    frame = petFrameAt(state, 0)
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
  // A bubble is a surface, not a hole: a click on it must not fall through to the desktop. The
  // column around them is pointer-events:none, so this only matches when the pointer is really
  // over a bubble (otherwise the canvas is the target and the transparent-pixel rule applies).
  if (target.closest('.pet-bubble, .pet-chat')) return setClickThrough(false)
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
  dragState = 'running-right'
  setState('running-right')
}

function moveDrag(event: PointerEvent) {
  if (!dragStart) return
  const direction: PetState = event.screenX < dragStart.pointerX ? 'running-left' : 'running-right'
  if (dragState !== direction) {
    dragState = direction
    setState(direction)
  }
  void window.tinadec.pets.setCurrentBounds({
    x: dragStart.windowX + event.screenX - dragStart.pointerX,
    y: dragStart.windowY + event.screenY - dragStart.pointerY,
  })
}

function stopDrag() {
  if (!dragStart) return
  dragStart = null
  dragging.value = false
  dragState = null
  applyState()
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

// --- Session partner: approval bubbles, chat bubble, run status + quick return ---
// A session partner window carries its sessionId in the URL; a decorative pet does not. The
// window is not a trusted host, so every read and the approval decision go through the
// main-process proxy, which also pins session reads to this window's own session.
const sessionId = (() => {
  const query = window.location.hash.split('?')[1] ?? ''
  return new URLSearchParams(query).get('sessionId')
})()
interface PartnerApproval { id: string; tool_id?: string; summary?: string; risk?: string }
interface PartnerMessage { role?: string; content?: string; created_at?: string }
interface PartnerRun { id?: string; status?: string; updated_at?: string; created_at?: string }
const approvals = ref<PartnerApproval[]>([])
const chatSummary = ref('')
const runStatus = ref<string | null>(null)
const runVersion = ref<string | null>(null)
const SETTLED_RUN_STATUSES = new Set(['completed', 'failed', 'cancelled'])
const deciding = ref(false)
const topApproval = computed(() => approvals.value[0] ?? null)
let pollTimer: ReturnType<typeof setTimeout> | null = null
let pollDisposed = false

// Core's 12-state run vocabulary (TinadecGateway/src/contracts/runStatuses.ts). Anything outside it
// is not guessed into a state: the pet stays idle and no badge is drawn, because inventing
// "running" for an unread status would be a lie.
const RUN_STATE_BY_STATUS: Record<string, 'thinking' | 'working' | 'needs-approval' | 'failed' | 'settled'> = {
  planning: 'thinking',
  understanding: 'thinking',
  reviewing: 'thinking',
  replanning: 'working',
  executing: 'working',
  awaiting_approval: 'needs-approval',
  awaiting_delegate: 'needs-approval',
  awaiting_user: 'needs-approval',
  failed: 'failed',
  paused: 'settled',
  completed: 'settled',
  cancelled: 'settled',
}
const runState = computed(() => (runStatus.value ? RUN_STATE_BY_STATUS[runStatus.value] ?? null : null))
// Petdex has no dedicated "thinking" row; `review` is the closest existing one (a still, attentive
// animation). The badge carries the exact phase, so the sprite never claims a state the run is not in.
const runPetState = computed<PetState>(() => {
  switch (runState.value) {
    case 'thinking': return 'review'
    case 'working': return 'running'
    case 'needs-approval': return 'waiting'
    case 'failed': return 'failed'
    default: return 'idle'
  }
})
// A paused run has no frame of its own, so the badge is the only honest signal: the sprite stands
// still either way. Completed/cancelled get neither, because the pet is simply idle again.
const RUN_BADGE_KEY: Record<string, string> = {
  planning: 'statusLabels.run.planning',
  understanding: 'statusLabels.run.understanding',
  reviewing: 'statusLabels.run.reviewing',
  replanning: 'statusLabels.run.replanning',
  executing: 'statusLabels.run.executing',
  awaiting_approval: 'statusLabels.run.awaiting_approval',
  awaiting_delegate: 'statusLabels.run.awaiting_delegate',
  awaiting_user: 'statusLabels.run.awaiting_user',
  failed: 'statusLabels.run.failed',
  paused: 'statusLabels.run.paused',
}
const runBadge = computed(() => (runStatus.value && RUN_BADGE_KEY[runStatus.value] ? t(RUN_BADGE_KEY[runStatus.value]) : ''))
const runTone = computed(() => (runState.value === 'failed' ? 'failed' : runState.value === 'needs-approval' ? 'attention' : 'normal'))

watch(runPetState, applyState)

// A bubble can appear while the pointer is parked over a transparent pixel, where the window was
// already click-through. Re-enable mouse events as soon as there is something to click, otherwise
// the approval buttons would stay unreachable until the user happens to move the mouse.
const hasBubbles = computed(() => Boolean(topApproval.value || chatSummary.value || runBadge.value))
watch(hasBubbles, (visible) => {
  if (visible) setClickThrough(false)
})

function summarize(content: string | undefined): string {
  if (!content) return ''
  const flat = content.replace(/\s+/g, ' ').trim()
  return flat.length > 160 ? `${flat.slice(0, 160)}…` : flat
}

async function refreshApprovals() {
  try {
    const result = await window.tinadec.sessionPartner.agentRequest({
      method: 'GET',
      path: `/api/v1/approvals?status=pending&session_id=${encodeURIComponent(sessionId!)}`,
    })
    if (result.status >= 200 && result.status < 300 && Array.isArray(result.body)) {
      approvals.value = result.body as PartnerApproval[]
    }
  } catch {
    // Proxy unavailable: keep the last known set rather than flashing empty.
  }
}

async function refreshChatSummary() {
  try {
    const result = await window.tinadec.sessionPartner.agentRequest({
      method: 'GET',
      path: `/api/v1/sessions/${encodeURIComponent(sessionId!)}/messages`,
    })
    if (result.status < 200 || result.status >= 300 || !Array.isArray(result.body)) return
    const messages = result.body as PartnerMessage[]
    // The latest persisted assistant turn. A run that is still streaming has no persisted answer
    // yet, so the bubble keeps showing the previous one instead of pretending to be live.
    for (let index = messages.length - 1; index >= 0; index -= 1) {
      const message = messages[index]
      if (message?.role === 'assistant') {
        chatSummary.value = summarize(message.content)
        return
      }
    }
    chatSummary.value = ''
  } catch {
    // Keep the previous summary; a transient failure is not "the assistant said nothing".
  }
}

async function refreshRunStatus() {
  try {
    const result = await window.tinadec.sessionPartner.agentRequest({
      method: 'GET',
      path: `/api/v1/sessions/${encodeURIComponent(sessionId!)}/runs`,
    })
    if (result.status < 200 || result.status >= 300 || !Array.isArray(result.body)) return
    // Core lists runs newest first.
    const latest = (result.body as PartnerRun[])[0]
    runStatus.value = typeof latest?.status === 'string' ? latest.status.toLowerCase() : null
    // The answer is only persisted when a run finalizes, so the message list is worth re-reading
    // when the latest run changes or finishes — not on every tick of a long execution, which would
    // pull the whole history every four seconds for text that cannot have moved.
    const settled = runStatus.value !== null && SETTLED_RUN_STATUSES.has(runStatus.value)
    const version = `${latest?.id ?? ''}@${settled ? 'settled' : 'live'}`
    const changed = version !== runVersion.value
    runVersion.value = version
    if (changed) await refreshChatSummary()
  } catch {
    // Keep the last known status rather than dropping to idle on a transient failure.
  }
}

// One timer for the partner reads: the window is an ambient surface, and independent pollers would
// multiply the traffic for no fresher answer. Run status goes first because it decides whether the
// (heavier) message list is worth re-reading.
function schedulePoll() {
  if (pollDisposed || !sessionId) return
  pollTimer = setTimeout(async () => {
    pollTimer = null
    if (pollDisposed) return
    if (typeof document === 'undefined' || document.visibilityState === 'visible') {
      await refreshApprovals()
      await refreshRunStatus()
    }
    schedulePoll()
  }, 4000)
}

async function decideApproval(approval: PartnerApproval, decision: 'approved' | 'rejected') {
  if (deciding.value) return
  deciding.value = true
  try {
    await window.tinadec.sessionPartner.agentRequest({
      method: 'POST',
      path: `/api/v1/approvals/${approval.id}/decision`,
      body: { decision, scope: 'once' },
    })
    approvals.value = approvals.value.filter((item) => item.id !== approval.id)
    await refreshApprovals()
  } finally {
    deciding.value = false
  }
}

function returnToSession() {
  void window.tinadec.sessionPartner.focus()
}

onMounted(async () => {
  if (sessionId) {
    void refreshApprovals()
    // Run status also seeds the chat bubble on the first pass, so the message list is read once.
    void refreshRunStatus()
    schedulePoll()
  }
  pet.value = await window.tinadec.pets.getCurrent()
  if (!pet.value?.imageDataUrl) return
  scale.value = pet.value.scale
  image = new Image()
  image.onload = () => {
    try {
      layout = parsePetSprite(image!.naturalWidth, image!.naturalHeight)
      loaded.value = true
      animationStarted = performance.now()
      state = runPetState.value
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
  pollDisposed = true
  if (pollTimer) clearTimeout(pollTimer)
  pollTimer = null
  cancelAnimationFrame(animationFrame)
  setClickThrough(false)
})
</script>

<template>
  <main
    class="desktop-pet-window"
    :class="{ dragging }"
    :aria-label="t('desktopPet.window')"
    @mousemove="updateHitTest"
    @wheel="zoom"
  >
    <canvas
      v-show="loaded"
      ref="canvas"
      class="pet-canvas"
      :aria-label="pet?.displayName ?? t('desktopPet.fallbackName')"
      role="img"
      @dblclick="jump"
      @pointerdown="startDrag"
      @pointermove="moveDrag"
      @pointerup="stopDrag"
      @pointercancel="stopDrag"
    />
    <div v-if="!loaded" class="pet-placeholder">{{ t('desktopPet.placeholder') }}</div>
    <button class="pet-control pet-close" type="button" :title="t('desktopPet.close')" :aria-label="t('desktopPet.close')" @click="closePet">
      <X :size="14" />
    </button>
    <button
      class="pet-control pet-resize"
      type="button"
      :title="t('desktopPet.resize')"
      :aria-label="t('desktopPet.resize')"
      @pointerdown="startResize"
      @pointermove="moveResize"
      @pointerup="stopResize"
      @pointercancel="stopResize"
    >
      <Grip :size="14" />
    </button>
    <!-- One column: badge, then approval, then chat, so nothing ever stacks on top of anything. -->
    <div v-if="sessionId" class="pet-bubbles">
      <div v-if="runBadge" class="pet-status" :class="`pet-status--${runTone}`" role="status">
        {{ runBadge }}
      </div>
      <div v-if="topApproval" class="pet-bubble" role="alertdialog" :aria-label="t('desktopPet.approvalTitle')">
        <p class="pet-bubble-title">{{ t('desktopPet.approvalTitle') }}</p>
        <p class="pet-bubble-body">{{ topApproval.summary || topApproval.tool_id || '' }}</p>
        <div class="pet-bubble-actions">
          <button type="button" class="pet-bubble-approve" :disabled="deciding" @click="decideApproval(topApproval, 'approved')">
            {{ t('desktopPet.approve') }}
          </button>
          <button type="button" class="pet-bubble-reject" :disabled="deciding" @click="decideApproval(topApproval, 'rejected')">
            {{ t('desktopPet.reject') }}
          </button>
        </div>
        <button type="button" class="pet-bubble-return" @click="returnToSession">{{ t('desktopPet.returnToSession') }}</button>
      </div>
      <!-- Display only: no controls, but still a surface so a click does not fall through it. -->
      <div v-if="chatSummary" class="pet-chat" aria-live="polite">
        <p class="pet-chat-body">{{ chatSummary }}</p>
      </div>
    </div>
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

.pet-status {
  /* A row of the bubble column, not an overlay: it must not sit on top of the approval bubble. */
  align-self: flex-start;
  padding: 3px 8px;
  border: 1px solid rgb(255 255 255 / 24%);
  border-radius: 999px;
  font: 600 11px/1.2 system-ui, sans-serif;
  color: rgb(255 255 255 / 88%);
  background: rgb(20 20 24 / 82%);
  pointer-events: none;
}

.pet-status--attention {
  border-color: rgb(255 190 60 / 55%);
  color: rgb(255 214 130);
}

.pet-status--failed {
  border-color: rgb(255 69 58 / 55%);
  color: rgb(255 150 143);
}

/* The column is inert so the pet underneath keeps receiving drags; only the approval bubble opts
   back in, because its buttons are the one thing here the user must be able to click. */
.pet-bubbles {
  position: absolute;
  top: 6px;
  left: 6px;
  right: 6px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  pointer-events: none;
}

.pet-bubble {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px;
  border: 1px solid rgb(255 255 255 / 24%);
  border-radius: 10px;
  color: white;
  background: rgb(20 20 24 / 92%);
  box-shadow: 0 8px 24px rgb(0 0 0 / 40%);
  pointer-events: auto;
}

.pet-chat {
  padding: 8px 10px;
  border: 1px solid rgb(255 255 255 / 18%);
  border-radius: 10px;
  background: rgb(20 20 24 / 82%);
  box-shadow: 0 8px 24px rgb(0 0 0 / 30%);
  /* Display only, but it must not be a hole: a click here stays on the bubble rather than
     falling through to the desktop behind the window. */
  pointer-events: auto;
  cursor: default;
  user-select: none;
}

.pet-chat-body {
  margin: 0;
  display: -webkit-box;
  max-height: 72px;
  overflow: hidden;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 4;
  font: 400 12px/1.4 system-ui, sans-serif;
  color: rgb(255 255 255 / 80%);
  word-break: break-word;
}

.pet-bubble-title {
  margin: 0;
  font: 600 12px/1.3 system-ui, sans-serif;
}

.pet-bubble-body {
  margin: 0;
  max-height: 60px;
  overflow: hidden;
  font: 400 12px/1.4 system-ui, sans-serif;
  color: rgb(255 255 255 / 80%);
  word-break: break-word;
}

.pet-bubble-actions {
  display: flex;
  gap: 6px;
}

.pet-bubble-actions button,
.pet-bubble-return {
  flex: 1;
  padding: 5px 8px;
  border: 1px solid rgb(255 255 255 / 24%);
  border-radius: 6px;
  font: 600 12px/1 system-ui, sans-serif;
  color: white;
  background: rgb(255 255 255 / 10%);
  cursor: pointer;
}

.pet-bubble-approve {
  background: rgb(52 199 89 / 28%);
}

.pet-bubble-reject {
  background: rgb(255 69 58 / 28%);
}

.pet-bubble-actions button:disabled {
  opacity: 0.5;
  cursor: default;
}

.pet-bubble-return {
  flex: none;
  background: transparent;
  font-weight: 400;
  color: rgb(255 255 255 / 70%);
}
</style>
