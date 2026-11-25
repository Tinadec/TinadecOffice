<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import { Copy, Link2, Star, X } from '@lucide/vue'
import { basenameFromPath } from '@/format'
import type { WorkspaceFileGrant, WorkspaceRoot } from '@/lib/workspaces'
import {
  useUiPreferences,
  type UiWorkspaceDropAction,
  type UiWorkspaceFolderTone,
} from '@/composables/useUiPreferences'
import {
  FOLDER_TONE_PALETTES,
  buildWorkspaceMediaThumbUrl,
  classifyWorkspaceFileVisual,
  fileBadgeLabel,
  type FileVisualCategory,
} from './workspaceVisuals'

export interface DroppedWorkspaceEntry {
  path: string
  name: string
}

export interface WorkspaceDropBatchPayload {
  folders: DroppedWorkspaceEntry[]
  files: DroppedWorkspaceEntry[]
  fileAction: 'link' | 'copy'
}

const props = withDefaults(defineProps<{
  roots?: WorkspaceRoot[]
  files?: WorkspaceFileGrant[]
  primaryRootId?: string | null
  tone?: UiWorkspaceFolderTone
  dropAction?: UiWorkspaceDropAction
  compact?: boolean
  disabled?: boolean
}>(), {
  roots: () => [],
  files: () => [],
  primaryRootId: null,
  tone: undefined,
  dropAction: undefined,
  compact: false,
  disabled: false,
})

const emit = defineEmits<{
  'drop-batch': [payload: WorkspaceDropBatchPayload]
}>()

const { t } = useI18n()
const uid = useId()
const prefs = useUiPreferences()

const hovered = ref(false)
const dragDepth = ref(0)
const dragging = computed(() => dragDepth.value > 0 && !props.compact && !props.disabled)
const altPressed = ref(false)
const snapping = ref(false)
let snapTimer: ReturnType<typeof setTimeout> | null = null

const pendingAsk = ref<{
  folders: DroppedWorkspaceEntry[]
  files: DroppedWorkspaceEntry[]
} | null>(null)

const failedThumbs = reactive<Record<string, boolean>>({})

const effectiveTone = computed<UiWorkspaceFolderTone>(
  () => props.tone ?? prefs.appearance.workspace_folder_tone ?? 'amber',
)
const effectiveDropAction = computed<UiWorkspaceDropAction>(
  () => props.dropAction ?? prefs.interaction.workspace_drop_action ?? 'link',
)
const reducedMotion = computed(() => prefs.isReducedMotion.value)

const palette = computed(() => FOLDER_TONE_PALETTES[effectiveTone.value] ?? FOLDER_TONE_PALETTES.amber)

const stageVars = computed<Record<string, string>>(() => {
  const p = palette.value
  return {
    '--lf-back-top': p.backTop,
    '--lf-back-bottom': p.backBottom,
    '--lf-back-tab': p.backTab,
    '--lf-back-stroke': p.backStroke,
    '--lf-pocket-well': p.pocketWell,
    '--lf-flap-top': p.flapTop,
    '--lf-flap-mid': p.flapMid,
    '--lf-flap-bottom': p.flapBottom,
    '--lf-flap-ready-top': p.flapReadyTop,
    '--lf-flap-ready-bottom': p.flapReadyBottom,
    '--lf-specular-top': p.specularTop,
    '--lf-refraction-bottom': p.refractionBottom,
    '--lf-ambient-shadow': p.ambientShadow,
    '--lf-ready-glow': altPressed.value && dragging.value ? 'rgba(16, 185, 129, 0.44)' : p.readyGlow,
    '--lf-pill-bg': p.pillBg,
    '--lf-pill-text': p.pillText,
    '--lf-pill-border': p.pillBorder,
  }
})

interface PocketCardItem {
  key: string
  kind: 'folder' | 'photo' | 'doc' | 'code'
  category: FileVisualCategory
  label: string
  badge: string
  thumbUrl: string
  isPrimary: boolean
  accentColor: string
}

const DOC_BADGE_COLORS: Record<string, string> = {
  pdf: '#dc2626',
  doc: '#0284c7',
  sheet: '#059669',
  code: '#4f46e5',
  config: '#7c3aed',
  archive: '#d97706',
  media: '#e11d48',
  generic: '#57534e',
}

const pocketCards = computed<PocketCardItem[]>(() => {
  if (props.compact && props.roots.length === 0 && props.files.length === 0) {
    return [
      {
        key: 'preview-photo',
        kind: 'photo',
        category: 'image',
        label: 'cover.png',
        badge: 'IMG',
        thumbUrl: '',
        isPrimary: false,
        accentColor: '#0ea5e9',
      },
      {
        key: 'preview-doc',
        kind: 'doc',
        category: 'pdf',
        label: 'spec.pdf',
        badge: 'PDF',
        thumbUrl: '',
        isPrimary: false,
        accentColor: '#dc2626',
      },
      {
        key: 'preview-code',
        kind: 'code',
        category: 'code',
        label: 'index.ts',
        badge: 'TS',
        thumbUrl: '',
        isPrimary: false,
        accentColor: '#4f46e5',
      },
    ]
  }

  const items: PocketCardItem[] = []
  const fileSlice = props.files.slice(0, props.roots.length > 0 ? 4 : 5)
  const remainingSlots = Math.max(0, 5 - fileSlice.length)
  const rootSlice = props.roots.slice(0, remainingSlots)

  for (const root of rootSlice) {
    items.push({
      key: `root:${root.id}`,
      kind: 'folder',
      category: 'folder',
      label: basenameFromPath(root.path),
      badge: 'DIR',
      thumbUrl: '',
      isPrimary: root.id === (props.primaryRootId ?? props.roots[0]?.id ?? null),
      accentColor: palette.value.swatch,
    })
  }

  for (const file of fileSlice) {
    const cat = classifyWorkspaceFileVisual(file.path)
    const key = `file:${file.id}`
    const kind: PocketCardItem['kind'] =
      cat === 'image'
        ? 'photo'
        : cat === 'code' || cat === 'config'
          ? 'code'
          : 'doc'
    items.push({
      key,
      kind,
      category: cat,
      label: basenameFromPath(file.path),
      badge: fileBadgeLabel(file.path, cat),
      thumbUrl: cat === 'image' && !failedThumbs[key] ? buildWorkspaceMediaThumbUrl(file.path) : '',
      isPrimary: false,
      accentColor: DOC_BADGE_COLORS[cat] ?? '#57534e',
    })
  }

  return items.slice(0, 5)
})

function cardSlotStyle(index: number, total: number): Record<string, string> {
  const center = (total - 1) / 2
  const offset = total <= 1 ? 0 : index - center
  const spread = props.compact ? 22 : 34
  const baseX = offset * spread
  const baseY = Math.abs(offset) * (props.compact ? 3.5 : 5)
  const baseRot = offset * 4.2
  const hoverX = offset * (spread + (props.compact ? 4 : 7))
  const hoverRot = offset * 7.5
  const partDirection = offset < 0 ? -1 : offset > 0 ? 1 : index % 2 === 0 ? -1 : 1
  const partX = baseX + partDirection * (props.compact ? 10 : 18)
  const z = 6 + index * 2

  return {
    '--card-x': `${baseX.toFixed(1)}px`,
    '--card-y': `${baseY.toFixed(1)}px`,
    '--card-rot': `${baseRot.toFixed(1)}deg`,
    '--card-hover-x': `${hoverX.toFixed(1)}px`,
    '--card-hover-rot': `${hoverRot.toFixed(1)}deg`,
    '--card-part-x': `${partX.toFixed(1)}px`,
    '--card-z': `${z}px`,
    '--card-accent': pocketCards.value[index]?.accentColor ?? '#64748b',
  }
}

const resolvedLiveDropMode = computed<'link' | 'copy' | 'ask'>(() => {
  const base = effectiveDropAction.value
  if (altPressed.value) {
    return base === 'copy' ? 'link' : 'copy'
  }
  return base
})

const dragCaptionText = computed(() => {
  if (!dragging.value) {
    return t('workspaceFolder.restHint')
  }
  const mode = resolvedLiveDropMode.value
  if (altPressed.value) {
    return mode === 'copy'
      ? t('workspaceFolder.dragHintCopyAlt')
      : t('workspaceFolder.dragHintLinkAlt')
  }
  if (mode === 'copy') return t('workspaceFolder.dragHintCopy')
  if (mode === 'ask') return t('workspaceFolder.dragHintAsk')
  return t('workspaceFolder.dragHintLink')
})

function classifyDataTransfer(transfer: DataTransfer | null): {
  folders: DroppedWorkspaceEntry[]
  files: DroppedWorkspaceEntry[]
} {
  const folders: DroppedWorkspaceEntry[] = []
  const files: DroppedWorkspaceEntry[] = []
  const resolvePath = (file: File | null): string =>
    file ? window.tinadec?.pathForFile?.(file) ?? '' : ''

  const items = Array.from(transfer?.items ?? []).filter((item) => item.kind === 'file')
  if (items.length > 0) {
    for (const item of items) {
      const file = item.getAsFile()
      const resolvedPath = resolvePath(file)
      const name = file?.name || basenameFromPath(resolvedPath) || resolvedPath
      const entry = typeof item.webkitGetAsEntry === 'function' ? item.webkitGetAsEntry() : null
      if (entry?.isDirectory) {
        folders.push({ path: resolvedPath, name })
      } else {
        files.push({ path: resolvedPath, name })
      }
    }
    return { folders, files }
  }

  for (const file of Array.from(transfer?.files ?? [])) {
    const resolvedPath = resolvePath(file)
    files.push({ path: resolvedPath, name: file?.name || basenameFromPath(resolvedPath) || '' })
  }
  return { folders, files }
}

function triggerSnapAnimation(): void {
  if (reducedMotion.value) return
  if (snapTimer) clearTimeout(snapTimer)
  snapping.value = true
  snapTimer = setTimeout(() => {
    snapping.value = false
    snapTimer = null
  }, 520)
}

function onDragEnter(event: DragEvent): void {
  if (props.compact || props.disabled) return
  dragDepth.value += 1
  altPressed.value = Boolean(event.altKey)
}

function onDragOver(event: DragEvent): void {
  if (props.compact || props.disabled) return
  if (dragDepth.value === 0) dragDepth.value = 1
  altPressed.value = Boolean(event.altKey)
  if (event.dataTransfer) {
    event.dataTransfer.dropEffect = resolvedLiveDropMode.value === 'copy' ? 'copy' : 'link'
  }
}

function onDragLeave(): void {
  if (props.compact || props.disabled) return
  dragDepth.value = Math.max(0, dragDepth.value - 1)
  if (dragDepth.value === 0) altPressed.value = false
}

function onDrop(event: DragEvent): void {
  if (props.compact || props.disabled) return
  const wasAlt = Boolean(event.altKey) || altPressed.value
  dragDepth.value = 0
  altPressed.value = false

  const { folders, files } = classifyDataTransfer(event.dataTransfer)
  if (folders.length === 0 && files.length === 0) return

  triggerSnapAnimation()

  const baseMode = effectiveDropAction.value
  if (!wasAlt && baseMode === 'ask' && files.length > 0) {
    pendingAsk.value = { folders, files }
    return
  }

  pendingAsk.value = null
  const fileAction: 'link' | 'copy' = wasAlt
    ? baseMode === 'copy'
      ? 'link'
      : 'copy'
    : baseMode === 'copy'
      ? 'copy'
      : 'link'

  emit('drop-batch', { folders, files, fileAction })
}

function confirmAsk(fileAction: 'link' | 'copy'): void {
  const batch = pendingAsk.value
  if (!batch) return
  pendingAsk.value = null
  triggerSnapAnimation()
  emit('drop-batch', {
    folders: batch.folders,
    files: batch.files,
    fileAction,
  })
}

function cancelAsk(): void {
  pendingAsk.value = null
}

function onWindowKeyChange(event: KeyboardEvent): void {
  if (dragDepth.value > 0) {
    altPressed.value = Boolean(event.altKey)
  }
}

onMounted(() => {
  if (typeof window !== 'undefined') {
    window.addEventListener('keydown', onWindowKeyChange)
    window.addEventListener('keyup', onWindowKeyChange)
  }
})

onBeforeUnmount(() => {
  if (snapTimer) clearTimeout(snapTimer)
  if (typeof window !== 'undefined') {
    window.removeEventListener('keydown', onWindowKeyChange)
    window.removeEventListener('keyup', onWindowKeyChange)
  }
})
</script>

<template>
  <div
    class="living-folder-wrap"
    :class="{
      'is-compact': compact,
      'is-hovered': hovered && !dragging,
      'is-dragging': dragging,
      'is-alt-invert': dragging && altPressed,
      'is-snapping': snapping,
      'is-reduced-motion': reducedMotion,
      'is-disabled': disabled,
    }"
    :data-folder-tone="effectiveTone"
    :data-folder-state="dragging ? 'dragover' : hovered ? 'hover' : 'rest'"
    :style="stageVars"
    data-testid="living-workspace-folder"
    @mouseenter="hovered = true"
    @mouseleave="hovered = false"
    @dragenter.prevent="onDragEnter"
    @dragover.prevent="onDragOver"
    @dragleave="onDragLeave"
    @drop.prevent="onDrop"
  >
    <div class="living-folder-stage">
      <!-- Hover / Dragover rounded backing plate -->
      <div class="living-folder-backing" aria-hidden="true" />

      <!-- Layer 4: Ambient tinted diffuse shadow -->
      <div class="living-folder-ambient" aria-hidden="true" />

      <!-- 3D Preserve-3D Sandwich -->
      <div class="living-folder-scene">
        <!-- 1. Backplate + Top-left Tab Ear -->
        <svg viewBox="0 0 320 184" class="living-folder-backplate" aria-hidden="true">
          <defs>
            <linearGradient :id="`${uid}-bp`" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stop-color="var(--lf-back-top)" />
              <stop offset="100%" stop-color="var(--lf-back-bottom)" />
            </linearGradient>
            <linearGradient :id="`${uid}-ear`" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0%" stop-color="var(--lf-back-top)" />
              <stop offset="100%" stop-color="var(--lf-back-tab)" />
            </linearGradient>
          </defs>
          <!-- Outer folder backplate with rounded ear -->
          <path
            d="M22 30C22 18.954 30.954 10 42 10H116C123.6 10 130.75 13.56 135.34 19.62L144.66 31.88C147.68 35.86 152.38 38.2 157.38 38.2H278C289.046 38.2 298 47.154 298 58.2V154C298 165.046 289.046 174 278 174H42C30.954 174 22 165.046 22 154V30Z"
            :fill="`url(#${uid}-bp)`"
            stroke="var(--lf-back-stroke)"
            stroke-width="1.2"
          />
          <!-- Subtle ear accent crease -->
          <path
            d="M38 22H112"
            stroke="rgba(255, 255, 255, 0.42)"
            stroke-width="1.5"
            stroke-linecap="round"
          />
          <!-- Recessed inner pocket cavity -->
          <rect
            x="30"
            y="46"
            width="260"
            height="118"
            rx="14"
            fill="var(--lf-pocket-well)"
          />
        </svg>

        <!-- 2. Real Middle Pocket Stack (sandwiched between backplate and front flap) -->
        <div class="living-folder-pocket" data-testid="living-folder-pocket">
          <template v-if="pocketCards.length > 0">
            <div
              v-for="(card, idx) in pocketCards"
              :key="card.key"
              class="pocket-card"
              :class="`pocket-card--${card.kind}`"
              :data-pocket-kind="card.kind"
              :style="cardSlotStyle(idx, pocketCards.length)"
              :title="card.label"
            >
              <!-- Image photo card -->
              <template v-if="card.kind === 'photo'">
                <div class="pocket-photo-frame">
                  <img
                    v-if="card.thumbUrl"
                    :src="card.thumbUrl"
                    alt=""
                    loading="lazy"
                    class="pocket-photo-img"
                    @error="failedThumbs[card.key] = true"
                  />
                  <svg v-else viewBox="0 0 60 44" class="pocket-photo-fallback" aria-hidden="true">
                    <defs>
                      <linearGradient :id="`${uid}-pimg-${idx}`" x1="0" y1="0" x2="0" y2="1">
                        <stop offset="0%" stop-color="#38bdf8" />
                        <stop offset="65%" stop-color="#818cf8" />
                        <stop offset="100%" stop-color="#f472b6" />
                      </linearGradient>
                    </defs>
                    <rect width="60" height="44" rx="4" :fill="`url(#${uid}-pimg-${idx})`" />
                    <circle cx="43" cy="13" r="5" fill="#fef08a" />
                    <path d="M0 36L18 19L31 30L42 21L60 37V44H0V36Z" fill="rgba(15, 23, 42, 0.42)" />
                  </svg>
                </div>
                <span v-if="!compact" class="pocket-card-caption">{{ card.label }}</span>
              </template>

              <!-- Mini folder tab card -->
              <template v-else-if="card.kind === 'folder'">
                <div class="pocket-folder-tab">
                  <span class="pocket-folder-ear" />
                  <div class="pocket-folder-body">
                    <Star v-if="card.isPrimary" :size="compact ? 8 : 10" class="pocket-folder-star" />
                    <span v-if="!compact" class="pocket-folder-name">{{ card.label }}</span>
                  </div>
                </div>
              </template>

              <!-- Code / Config card -->
              <template v-else-if="card.kind === 'code'">
                <div class="pocket-code-head">
                  <span class="pocket-code-glyph">&lt;/&gt;</span>
                  <span class="pocket-code-badge">{{ card.badge }}</span>
                </div>
                <div class="pocket-code-lines" aria-hidden="true">
                  <span class="cline cline--a" />
                  <span class="cline cline--b" />
                  <span class="cline cline--c" />
                </div>
                <span v-if="!compact" class="pocket-card-caption pocket-card-caption--dark">{{ card.label }}</span>
              </template>

              <!-- Folded-corner document card -->
              <template v-else>
                <span class="pocket-doc-fold" aria-hidden="true" />
                <span class="pocket-doc-badge">{{ card.badge }}</span>
                <div class="pocket-doc-lines" aria-hidden="true">
                  <span class="dline dline--title" />
                  <span class="dline dline--body" />
                  <span class="dline dline--short" />
                </div>
                <span v-if="!compact" class="pocket-card-caption">{{ card.label }}</span>
              </template>
            </div>
          </template>

          <!-- Empty directory dashed guide card -->
          <div v-else class="pocket-empty-guide" data-testid="living-folder-empty-guide">
            <span class="pocket-empty-guide-text">{{ t('workspaceFolder.emptyGuideCard') }}</span>
          </div>
        </div>

        <!-- 3. 4-Layer Liquid Glass Front Flap -->
        <div class="living-folder-flap" data-testid="living-folder-flap">
          <!-- Layer ① Frost -->
          <div class="flap-layer-frost" aria-hidden="true" />
          <!-- Layer ② Edge Specular (1px top lip + bottom refraction) -->
          <div class="flap-layer-specular" aria-hidden="true" />
          <!-- Layer ③ Sheen (diagonal hover/dragover sweep) -->
          <div class="flap-layer-sheen" aria-hidden="true" />

          <!-- Subtle lower-left tactile groove -->
          <div v-if="!compact" class="flap-emboss-mark" aria-hidden="true">
            <span class="flap-emboss-line" />
            <span class="flap-emboss-line flap-emboss-line--short" />
          </div>

          <!-- Frosted bottom-right count capsule -->
          <div v-if="!compact" class="flap-count-pill" data-testid="living-folder-count-pill">
            {{ t('workspaceFolder.countBadge', { folders: roots.length, files: files.length }) }}
          </div>
        </div>
      </div>
    </div>

    <!-- Single-line live drop hint below folder -->
    <p
      v-if="!compact"
      class="living-folder-caption"
      :class="{ 'is-active': dragging, 'is-alt': dragging && altPressed }"
      data-testid="living-folder-caption"
    >
      {{ dragCaptionText }}
    </p>

    <!-- Lightweight 2-button confirm bubble right below folder when dropAction === 'ask' -->
    <div
      v-if="!compact && pendingAsk"
      class="living-folder-ask-bubble"
      data-testid="workspace-drop-ask-bubble"
      role="region"
      :aria-label="t('workspaceFolder.askAria')"
    >
      <span class="ask-bubble-label">
        {{ t('workspaceFolder.askPrompt', { count: pendingAsk.folders.length + pendingAsk.files.length }) }}
      </span>
      <div class="ask-bubble-actions">
        <button
          type="button"
          class="ask-bubble-btn is-primary"
          data-testid="workspace-drop-ask-link"
          @click="confirmAsk('link')"
        >
          <Link2 :size="12" aria-hidden="true" />
          <span>{{ t('workspaceFolder.askLinkBtn') }}</span>
        </button>
        <button
          type="button"
          class="ask-bubble-btn"
          data-testid="workspace-drop-ask-copy"
          @click="confirmAsk('copy')"
        >
          <Copy :size="12" aria-hidden="true" />
          <span>{{ t('workspaceFolder.askCopyBtn') }}</span>
        </button>
        <button
          type="button"
          class="ask-bubble-dismiss"
          :title="t('workspaceRoots.cancel')"
          :aria-label="t('workspaceRoots.cancel')"
          @click="cancelAsk"
        >
          <X :size="13" aria-hidden="true" />
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.living-folder-wrap {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  width: 100%;
  user-select: none;
}

.living-folder-stage {
  position: relative;
  width: 100%;
  max-width: 288px;
  aspect-ratio: 320 / 184;
  perspective: 900px;
  display: flex;
  align-items: center;
  justify-content: center;
}

.living-folder-wrap.is-compact {
  width: auto;
  gap: 0;
}

.living-folder-wrap.is-compact .living-folder-stage {
  width: 156px;
  max-width: 156px;
}

/* Soft outer backing plate appearing on Hover / Dragover */
.living-folder-backing {
  position: absolute;
  inset: 2px 6px;
  border-radius: 22px;
  background: color-mix(in srgb, var(--surface-raised, rgba(255, 255, 255, 0.06)) 78%, transparent);
  border: 1px solid color-mix(in srgb, var(--border-default, rgba(255, 255, 255, 0.14)) 65%, transparent);
  opacity: 0;
  transform: scale(0.96);
  transition: opacity 240ms cubic-bezier(0.22, 1, 0.36, 1), transform 260ms cubic-bezier(0.22, 1, 0.36, 1), border-color 220ms ease;
  pointer-events: none;
}

.living-folder-wrap.is-hovered .living-folder-backing {
  opacity: 1;
  transform: scale(1);
}

.living-folder-wrap.is-dragging .living-folder-backing {
  opacity: 1;
  transform: scale(1.02);
  border-color: var(--lf-ready-glow);
}

/* Layer 4: Ambient Shadow */
.living-folder-ambient {
  position: absolute;
  left: 14%;
  right: 14%;
  bottom: 4px;
  height: 28px;
  border-radius: 999px;
  background: var(--lf-ambient-shadow);
  filter: blur(16px);
  opacity: 0.78;
  transform: translateY(4px) scaleX(0.94);
  transition:
    background-color 240ms ease,
    opacity 240ms ease,
    transform 280ms cubic-bezier(0.22, 1, 0.36, 1),
    filter 240ms ease;
  pointer-events: none;
}

.living-folder-wrap.is-hovered .living-folder-ambient {
  opacity: 0.95;
  transform: translateY(7px) scaleX(1);
}

.living-folder-wrap.is-dragging .living-folder-ambient {
  background: var(--lf-ready-glow);
  opacity: 1;
  filter: blur(20px);
  transform: translateY(9px) scaleX(1.06);
}

/* 3D Scene */
.living-folder-scene {
  position: relative;
  width: 100%;
  height: 100%;
  transform-style: preserve-3d;
  transition: transform 280ms cubic-bezier(0.22, 1, 0.36, 1), filter 240ms ease;
}

.living-folder-wrap.is-hovered .living-folder-scene {
  filter: brightness(1.04);
}

.living-folder-wrap.is-dragging .living-folder-scene {
  transform: scale(1.04);
  filter: brightness(1.08) saturate(1.08);
}

.living-folder-backplate {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  display: block;
  transform: translateZ(0px);
  pointer-events: none;
}

/* Middle Pocket Stack */
.living-folder-pocket {
  position: absolute;
  left: 11%;
  right: 11%;
  top: 15%;
  bottom: 18%;
  transform-style: preserve-3d;
  transform: translateZ(10px);
  display: flex;
  align-items: flex-start;
  justify-content: center;
  pointer-events: none;
}

.pocket-card {
  position: absolute;
  top: 8%;
  width: 27%;
  height: 76%;
  border-radius: 8px;
  padding: 5px 5px 4px;
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  box-shadow: 0 4px 12px rgba(15, 23, 42, 0.22);
  transform-origin: 50% 100%;
  transform: translate3d(var(--card-x, 0px), var(--card-y, 0px), var(--card-z, 8px)) rotateZ(var(--card-rot, 0deg));
  transition: transform 300ms cubic-bezier(0.22, 1, 0.36, 1), box-shadow 260ms ease;
}

.living-folder-wrap.is-compact .pocket-card {
  padding: 3px;
  border-radius: 5px;
}

/* Hover: pocket cards float up and fan out */
.living-folder-wrap.is-hovered .pocket-card {
  transform:
    translate3d(var(--card-hover-x, 0px), calc(var(--card-y, 0px) - 12px), var(--card-z, 8px))
    rotateZ(var(--card-hover-rot, 0deg));
  box-shadow: 0 8px 18px rgba(15, 23, 42, 0.26);
}

.living-folder-wrap.is-compact.is-hovered .pocket-card {
  transform:
    translate3d(var(--card-hover-x, 0px), calc(var(--card-y, 0px) - 7px), var(--card-z, 8px))
    rotateZ(var(--card-hover-rot, 0deg));
}

/* Dragover: pocket cards part left/right to make room for incoming drop */
.living-folder-wrap.is-dragging .pocket-card {
  transform:
    translate3d(var(--card-part-x, 0px), calc(var(--card-y, 0px) - 6px), var(--card-z, 8px))
    rotateZ(calc(var(--card-hover-rot, 0deg) * 1.15));
}

/* Drop spring snap-in */
@keyframes pocket-card-snap {
  0% {
    transform: translate3d(var(--card-x, 0px), -18px, var(--card-z, 8px)) scale(1.05);
  }
  58% {
    transform: translate3d(var(--card-x, 0px), calc(var(--card-y, 0px) + 5px), var(--card-z, 8px)) scale(0.97);
  }
  100% {
    transform: translate3d(var(--card-x, 0px), var(--card-y, 0px), var(--card-z, 8px)) rotateZ(var(--card-rot, 0deg));
  }
}

.living-folder-wrap.is-snapping .pocket-card {
  animation: pocket-card-snap 480ms cubic-bezier(0.22, 1.35, 0.36, 1);
}

.pocket-card--photo {
  background: linear-gradient(180deg, #ffffff 0%, #f1f5f9 100%);
  border: 1px solid rgba(203, 213, 225, 0.85);
}

.pocket-photo-frame {
  width: 100%;
  flex: 1;
  min-height: 0;
  border-radius: 4.5px;
  overflow: hidden;
  background: #e2e8f0;
}

.pocket-photo-img,
.pocket-photo-fallback {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.pocket-card--doc {
  background: linear-gradient(180deg, #ffffff 0%, #f8fafc 100%);
  border: 1px solid rgba(203, 213, 225, 0.85);
  position: absolute;
  overflow: hidden;
}

.pocket-doc-fold {
  position: absolute;
  top: 0;
  right: 0;
  width: 10px;
  height: 10px;
  background: linear-gradient(225deg, rgba(148, 163, 184, 0.28) 50%, #e2e8f0 50%);
  border-bottom-left-radius: 3px;
}

.pocket-doc-badge {
  align-self: flex-start;
  padding: 1px 4px;
  border-radius: 3px;
  background: var(--card-accent, #0284c7);
  color: #ffffff;
  font-size: 7.5px;
  font-weight: 800;
  letter-spacing: 0.03em;
  line-height: 1.25;
}

.pocket-doc-lines {
  display: flex;
  flex-direction: column;
  gap: 3px;
  margin: auto 0;
}

.dline {
  display: block;
  height: 2.5px;
  border-radius: 2px;
  background: rgba(100, 116, 139, 0.32);
}

.dline--title { width: 76%; background: rgba(51, 65, 85, 0.52); }
.dline--body { width: 92%; }
.dline--short { width: 60%; }

.pocket-card--code {
  background: linear-gradient(165deg, #1e1b4b 0%, #312e81 100%);
  border: 1px solid rgba(165, 180, 252, 0.42);
}

.pocket-code-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 2px;
}

.pocket-code-glyph {
  font-size: 8px;
  font-weight: 800;
  color: #a5b4fc;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
}

.pocket-code-badge {
  padding: 0.5px 3.5px;
  border-radius: 3px;
  background: rgba(129, 140, 248, 0.28);
  color: #e0e7ff;
  font-size: 7px;
  font-weight: 700;
}

.pocket-code-lines {
  display: flex;
  flex-direction: column;
  gap: 3px;
  margin: auto 0;
}

.cline {
  display: block;
  height: 2.5px;
  border-radius: 2px;
}

.cline--a { width: 72%; background: #38bdf8; opacity: 0.85; }
.cline--b { width: 88%; background: #a78bfa; opacity: 0.8; }
.cline--c { width: 54%; background: #34d399; opacity: 0.85; }

.pocket-card--folder {
  padding: 0;
  background: transparent;
  box-shadow: none;
}

.pocket-folder-tab {
  width: 100%;
  height: 100%;
  display: flex;
  flex-direction: column;
  filter: drop-shadow(0 4px 10px rgba(15, 23, 42, 0.22));
}

.pocket-folder-ear {
  width: 44%;
  height: 7px;
  border-radius: 5px 5px 0 0;
  background: linear-gradient(90deg, var(--lf-back-top), var(--lf-back-tab));
  border: 1px solid rgba(255, 255, 255, 0.45);
  border-bottom: 0;
}

.pocket-folder-body {
  flex: 1;
  border-radius: 0 7px 7px 7px;
  background: linear-gradient(180deg, #fffbeb 0%, #fef3c7 100%);
  border: 1px solid rgba(251, 191, 36, 0.58);
  padding: 5px;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
}

.pocket-folder-star {
  color: #d97706;
  fill: #fbbf24;
}

.pocket-folder-name {
  font-size: 8.5px;
  font-weight: 700;
  color: #78350f;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.pocket-card-caption {
  font-size: 7.5px;
  font-weight: 600;
  color: #334155;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  margin-top: 2px;
}

.pocket-card-caption--dark {
  color: #e0e7ff;
}

.pocket-empty-guide {
  width: 74%;
  height: 68%;
  margin-top: 6%;
  border-radius: 10px;
  border: 1.5px dashed rgba(255, 255, 255, 0.58);
  background: rgba(255, 255, 255, 0.16);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 6px 10px;
  text-align: center;
}

.pocket-empty-guide-text {
  font-size: 10.5px;
  font-weight: 600;
  color: rgba(255, 255, 255, 0.92);
  text-shadow: 0 1px 2px rgba(15, 23, 42, 0.28);
}

/* 3. 4-Layer Liquid Glass Front Flap */
.living-folder-flap {
  position: absolute;
  left: 6.8%;
  right: 6.8%;
  top: 34%;
  bottom: 5.4%;
  border-radius: 15px 15px 17px 17px;
  overflow: hidden;
  transform-origin: 50% 100%;
  /* Rest state: subtle forward tilt */
  transform: translateZ(20px) rotateX(-10deg);
  transition:
    transform 300ms cubic-bezier(0.22, 1, 0.36, 1),
    box-shadow 260ms ease;
  box-shadow:
    0 -2px 10px rgba(15, 23, 42, 0.10),
    inset 0 -1px 0 var(--lf-refraction-bottom);
}

.living-folder-wrap.is-compact .living-folder-flap {
  border-radius: 10px 10px 12px 12px;
}

/* Hover state: flap opens to -24deg */
.living-folder-wrap.is-hovered .living-folder-flap {
  transform: translateZ(20px) rotateX(-24deg);
}

/* Dragover state: wide open -38deg */
.living-folder-wrap.is-dragging .living-folder-flap {
  transform: translateZ(22px) rotateX(-38deg);
  box-shadow:
    0 0 0 1.5px var(--lf-specular-top),
    0 8px 24px var(--lf-ready-glow);
}

/* Drop spring close bounce */
@keyframes flap-spring-snap {
  0% {
    transform: translateZ(22px) rotateX(-34deg);
  }
  52% {
    transform: translateZ(20px) rotateX(-4deg);
  }
  78% {
    transform: translateZ(20px) rotateX(-14deg);
  }
  100% {
    transform: translateZ(20px) rotateX(-10deg);
  }
}

.living-folder-wrap.is-snapping .living-folder-flap {
  animation: flap-spring-snap 500ms cubic-bezier(0.22, 1.35, 0.36, 1);
}

/* Layer ① Frost */
.flap-layer-frost {
  position: absolute;
  inset: 0;
  backdrop-filter: blur(10px) saturate(140%);
  -webkit-backdrop-filter: blur(10px) saturate(140%);
  background: linear-gradient(
    180deg,
    var(--lf-flap-top) 0%,
    var(--lf-flap-mid) 48%,
    var(--lf-flap-bottom) 100%
  );
  transition: background 240ms ease;
}

.living-folder-wrap.is-dragging .flap-layer-frost {
  background: linear-gradient(
    180deg,
    var(--lf-flap-ready-top) 0%,
    var(--lf-flap-mid) 45%,
    var(--lf-flap-ready-bottom) 100%
  );
}

/* Layer ② Edge Specular (1px top lip + bottom micro-refraction) */
.flap-layer-specular {
  position: absolute;
  inset: 0;
  border-radius: inherit;
  border-top: 1px solid var(--lf-specular-top);
  border-bottom: 1px solid var(--lf-refraction-bottom);
  border-left: 1px solid rgba(255, 255, 255, 0.32);
  border-right: 1px solid rgba(255, 255, 255, 0.32);
  pointer-events: none;
}

/* Layer ③ Sheen (diagonal light sweep on hover/dragover) */
.flap-layer-sheen {
  position: absolute;
  inset: -20% -40%;
  background: linear-gradient(
    115deg,
    transparent 34%,
    rgba(255, 255, 255, 0.38) 48%,
    rgba(255, 255, 255, 0.10) 56%,
    transparent 68%
  );
  transform: translateX(-46%);
  opacity: 0;
  transition: transform 520ms cubic-bezier(0.22, 1, 0.36, 1), opacity 260ms ease;
  pointer-events: none;
}

.living-folder-wrap.is-hovered .flap-layer-sheen,
.living-folder-wrap.is-dragging .flap-layer-sheen {
  transform: translateX(38%);
  opacity: 1;
}

.flap-emboss-mark {
  position: absolute;
  left: 14px;
  bottom: 13px;
  display: flex;
  flex-direction: column;
  gap: 3px;
  opacity: 0.55;
  pointer-events: none;
}

.flap-emboss-line {
  width: 22px;
  height: 2px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.72);
}

.flap-emboss-line--short {
  width: 14px;
}

.flap-count-pill {
  position: absolute;
  right: 11px;
  bottom: 10px;
  padding: 2.5px 9px;
  border-radius: 999px;
  background: var(--lf-pill-bg);
  color: var(--lf-pill-text);
  border: 1px solid var(--lf-pill-border);
  backdrop-filter: blur(8px);
  -webkit-backdrop-filter: blur(8px);
  font-size: 10.5px;
  font-weight: 600;
  letter-spacing: 0.01em;
  font-variant-numeric: tabular-nums;
  pointer-events: none;
}

/* Single-line live drop caption */
.living-folder-caption {
  margin: 0;
  font-size: 11px;
  line-height: 1.4;
  color: var(--text-muted);
  text-align: center;
  transition: color 160ms ease;
}

.living-folder-caption.is-active {
  color: var(--text-primary);
  font-weight: 600;
}

.living-folder-caption.is-alt {
  color: var(--accent-success, #10b981);
}

/* Ask mode lightweight inline 2-button confirm bubble */
.living-folder-ask-bubble {
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
  padding: 7px 10px;
  border-radius: 10px;
  background: var(--surface-raised);
  border: 1px solid color-mix(in srgb, var(--accent-primary) 36%, var(--border-card));
  box-shadow: 0 6px 18px rgba(15, 23, 42, 0.14);
}

.ask-bubble-label {
  font-size: 11.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.ask-bubble-actions {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
}

.ask-bubble-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 26px;
  padding: 0 9px;
  border-radius: 999px;
  border: 1px solid var(--border-default);
  background: var(--surface-button);
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
}

.ask-bubble-btn:hover {
  background: var(--surface-hover);
}

.ask-bubble-btn.is-primary {
  background: var(--accent-primary);
  color: var(--bg-primary, #09090b);
  border-color: transparent;
}

.ask-bubble-btn.is-primary:hover {
  opacity: 0.92;
}

.ask-bubble-dismiss {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border: 0;
  border-radius: 999px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.ask-bubble-dismiss:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

/* Honor reduced motion preference cleanly */
.living-folder-wrap.is-reduced-motion .living-folder-backing,
.living-folder-wrap.is-reduced-motion .living-folder-ambient,
.living-folder-wrap.is-reduced-motion .living-folder-scene,
.living-folder-wrap.is-reduced-motion .pocket-card,
.living-folder-wrap.is-reduced-motion .living-folder-flap,
.living-folder-wrap.is-reduced-motion .flap-layer-frost,
.living-folder-wrap.is-reduced-motion .flap-layer-sheen,
.living-folder-wrap.is-reduced-motion .living-folder-caption {
  transition: none !important;
  animation: none !important;
}

.living-folder-wrap.is-reduced-motion .flap-layer-sheen {
  display: none;
}
</style>
