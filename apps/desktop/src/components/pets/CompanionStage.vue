<script setup lang="ts">
/**
 * Unified companion avatar stage: renders either an 8-column pixel sprite atlas OR a pure-data
 * Live2D WebGL model powered by the user's BYO `live2dcubismcore.min.js` runtime.
 *
 * When a Live2D avatar is selected and the BYO Cubism Core SDK is not yet imported, the stage
 * renders an explicit `companion-stage-live2d-unavailable` pill rather than masquerading as a
 * sprite. When the SDK IS loaded, it mounts `mountLive2dStage` with lip-sync (`speaking` /
 * `mouthOpen`), state/emotion cues, and automatic offscreen/hidden WebGL loop pause + release.
 */
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAgentAvatars } from '@/composables/useAgentAvatars'
import { hasCubismCoreGlobal, planAvatar, type AvatarKind } from '@/pets/avatarKind'
import {
  checkLive2dSdkStatus,
  live2dSdkState,
  mountLive2dStage,
  type Live2dStageController,
} from '@/pets/live2dRuntimeLoader'
import { PET_STATES, petFrameAt, parsePetSprite, type PetState } from '@/pets/petRuntime'

export type StagePetState = PetState | 'working' | 'celebrate' | 'sleep'

const props = withDefaults(defineProps<{
  petSlug: string
  /** Which renderer this avatar needs: sprite atlas or imported Live2D model. */
  avatarKind?: AvatarKind
  /** Which row of the atlas to play, or state cue for Live2D parameters. */
  state?: StagePetState
  /** Whether the companion is actively speaking / streaming tokens (drives lip-sync). */
  speaking?: boolean
  /** Optional explicit lip-sync mouth openness in [0, 1]. */
  mouthOpen?: number
  /** Optional emotion cue ('idle' | 'happy' | 'thinking' | 'sleep'). */
  emotion?: string | null
  /** Optional Live2D motion group hint. */
  motionGroup?: string | null
  /** Optional model3.json relative entry path. */
  entryModelJson?: string | null
  /** Rendered height of one frame in px; sprite width follows the 192:208 aspect. */
  height?: number
  alt?: string
}>(), {
  avatarKind: 'sprite',
  state: 'idle',
  speaking: false,
  mouthOpen: undefined,
  emotion: null,
  motionGroup: null,
  entryModelJson: null,
  height: 160,
  alt: '',
})

const { t } = useI18n()

const sdkReady = computed(() => hasCubismCoreGlobal() || live2dSdkState.value.ready)
const plan = computed(() =>
  planAvatar(props.avatarKind, props.petSlug, null, { live2dSdkReady: sdkReady.value }),
)
const isLive2d = computed(() => plan.value.kind === 'live2d')
const live2dLoadFailed = ref(false)
const live2dReadyToRender = computed(
  () => plan.value.kind === 'live2d' && plan.value.ready && !live2dLoadFailed.value,
)
const live2dCanvasRef = ref<HTMLCanvasElement | null>(null)
let live2dController: Live2dStageController | null = null

function disposeLive2d(): void {
  live2dController?.dispose()
  live2dController = null
}

watch(
  () => [props.petSlug, props.avatarKind, sdkReady.value] as const,
  () => {
    live2dLoadFailed.value = false
    if (props.avatarKind === 'live2d' && !sdkReady.value && !live2dSdkState.value.checked) {
      void checkLive2dSdkStatus()
    }
  },
)

onMounted(() => {
  if (props.avatarKind === 'live2d' && !sdkReady.value && !live2dSdkState.value.checked) {
    void checkLive2dSdkStatus()
  }
})

watch(
  [
    live2dCanvasRef,
    () => (live2dReadyToRender.value && plan.value.kind === 'live2d' ? plan.value.modelId : ''),
    () => props.entryModelJson,
  ] as const,
  ([canvasEl, modelId, entryJson]) => {
    disposeLive2d()
    if (!canvasEl || !modelId) return
    live2dController = mountLive2dStage(canvasEl, {
      modelId,
      entryModelJson: entryJson,
      getDynamicState: () => ({
        state: props.state,
        speaking: props.speaking,
        mouthOpen: props.mouthOpen,
        emotion: props.emotion,
        motionGroup: props.motionGroup,
      }),
      onError: () => {
        live2dLoadFailed.value = true
      },
    })
  },
  { immediate: true },
)

watch(
  () => [props.state, props.speaking, props.mouthOpen, props.emotion, props.motionGroup] as const,
  () => {
    live2dController?.notifyStateChange()
  },
)

// ── Sprite Atlas Frame Stepping ──────────────────────────────────────

const { urlForSlug } = useAgentAvatars()
const url = computed(() => (props.avatarKind === 'live2d' ? '' : urlForSlug(props.petSlug)))

interface StageLayout {
  frameWidth: number
  rows: number
}

const layout = ref<StageLayout | null>(null)
const frame = ref<{ row: number; column: number }>({ row: 0, column: 0 })

let probe: HTMLImageElement | null = null
let raf = 0
let startedAt = 0

watch([url, () => props.avatarKind] as const, ([next, kind]) => {
  layout.value = null
  if (kind === 'live2d' || !next) return
  const image = new Image()
  probe = image
  image.onload = () => {
    if (probe !== image) return
    try {
      const parsed = parsePetSprite(image.naturalWidth, image.naturalHeight)
      layout.value = { frameWidth: parsed.frameWidth, rows: parsed.rows }
    } catch {
      layout.value = null
    }
  }
  image.onerror = () => {
    if (probe === image) layout.value = null
  }
  image.src = next
}, { immediate: true })

function stopSpriteLoop(): void {
  if (raf) cancelAnimationFrame(raf)
  raf = 0
}

function resolveSpriteState(state: StagePetState): PetState {
  if (state in PET_STATES) return state as PetState
  if (state === 'working') return 'running'
  if (state === 'celebrate') return 'jumping'
  return 'idle'
}

function tickSprite(now: number): void {
  const elapsed = Math.max(0, now - startedAt)
  const next = petFrameAt(resolveSpriteState(props.state), elapsed)
  if (next.column !== frame.value.column || next.row !== frame.value.row) {
    frame.value = { row: next.row, column: next.column }
  }
  raf = requestAnimationFrame(tickSprite)
}

watch(
  () => [props.state, url.value, props.avatarKind] as const,
  ([, nextUrl, kind]) => {
    stopSpriteLoop()
    if (kind === 'live2d' || !nextUrl) return
    const spriteState = resolveSpriteState(props.state)
    frame.value = { row: PET_STATES[spriteState].row, column: 0 }
    startedAt = performance.now()
    raf = requestAnimationFrame(tickSprite)
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  stopSpriteLoop()
  disposeLive2d()
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

const live2dCanvasStyle = computed(() => ({
  width: `${Math.round(props.height * 0.92)}px`,
  height: `${props.height}px`,
}))
</script>

<template>
  <div
    class="companion-stage"
    :class="[`companion-stage--${state}`, { 'companion-stage--speaking': speaking }]"
    :style="isLive2d ? { height: `${height}px` } : undefined"
    role="img"
    :aria-label="alt"
    data-testid="companion-stage"
  >
    <canvas
      v-if="live2dReadyToRender"
      ref="live2dCanvasRef"
      class="companion-stage-live2d-canvas"
      :style="live2dCanvasStyle"
      data-testid="companion-stage-live2d-canvas"
    />
    <div
      v-else-if="isLive2d"
      class="companion-stage-live2d"
      data-testid="companion-stage-live2d-unavailable"
    >
      {{ t('settings.companions.live2d.unavailableShort') }}
    </div>
    <div v-else-if="Object.keys(style).length" class="companion-stage-pet" :style="style" />
  </div>
</template>

<style scoped>
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

.companion-stage-live2d-canvas {
  display: block;
  filter: drop-shadow(0 6px 12px rgb(0 0 0 / 24%));
}

.companion-stage-live2d {
  display: flex;
  align-items: center;
  justify-content: center;
  max-width: 150px;
  padding: 6px 10px;
  border-radius: 8px;
  background: var(--surface-input, rgb(0 0 0 / 10%));
  font-size: 11px;
  line-height: 1.4;
  text-align: center;
  color: var(--text-muted);
}

@media (prefers-reduced-motion: reduce) {
  .companion-stage-pet,
  .companion-stage-live2d-canvas {
    filter: none;
  }
}
</style>
