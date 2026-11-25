<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Copy, Link2, Star, X } from '@lucide/vue'
import { basenameFromPath } from '@/format'
import type { WorkspaceFileGrant, WorkspaceRoot } from '@/lib/workspaces'
import { useTheme } from '@/composables/useTheme'
import {
  useUiPreferences,
  type UiWorkspaceDropAction,
  type UiWorkspaceFolderTone,
} from '@/composables/useUiPreferences'
import {
  createLivingFolderEngine,
  type LivingFolderCardDescriptor,
  type LivingFolderEngineHandle,
  type LivingFolderEngineState,
} from './LivingFolderThreeEngine'
import {
  reconcileWorkspaceItemOrder,
  type WorkspaceUnifiedItem,
} from './workspaceCardOrder'
import {
  FOLDER_TONE_PALETTES,
  buildWorkspaceMediaThumbUrl,
  classifyWorkspaceFileVisual,
  fileBadgeLabel,
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
  orderedItems?: WorkspaceUnifiedItem[]
  primaryRootId?: string | null
  tone?: UiWorkspaceFolderTone
  dropAction?: UiWorkspaceDropAction
  compact?: boolean
  disabled?: boolean
}>(), {
  roots: () => [],
  files: () => [],
  orderedItems: undefined,
  primaryRootId: null,
  tone: undefined,
  dropAction: undefined,
  compact: false,
  disabled: false,
})

const emit = defineEmits<{
  'drop-batch': [payload: WorkspaceDropBatchPayload]
  'select-card': [item: WorkspaceUnifiedItem]
  'reorder-cards': [reorderedVisibleKeys: string[]]
}>()

const { t } = useI18n()
const uid = useId()
const prefs = useUiPreferences()
const { theme } = useTheme()

const stageRef = ref<HTMLElement | null>(null)
const canvasRef = ref<HTMLCanvasElement | null>(null)
const webglActive = ref(false)
let engine: LivingFolderEngineHandle | null = null

const hovered = ref(false)
const hoveredCard = ref<LivingFolderCardDescriptor | null>(null)
const cardDragging = ref(false)

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

const systemPrefersDark = ref(
  typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia('(prefers-color-scheme: dark)').matches
    : true,
)
let colorSchemeMql: MediaQueryList | null = null

function onColorSchemeChange(event: MediaQueryListEvent): void {
  systemPrefersDark.value = event.matches
}

const resolvedTheme = computed<'dark' | 'light'>(() => {
  if (theme.value === 'light') return 'light'
  if (theme.value === 'dark') return 'dark'
  return systemPrefersDark.value ? 'dark' : 'light'
})

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

const effectiveOrderedItems = computed<WorkspaceUnifiedItem[]>(() => {
  if (props.orderedItems !== undefined) {
    return props.orderedItems
  }
  return reconcileWorkspaceItemOrder(props.roots, props.files, [])
})

const primaryRootResolved = computed<string | null>(
  () => props.primaryRootId ?? props.roots[0]?.id ?? null,
)

const cardDescriptors = computed<LivingFolderCardDescriptor[]>(() => {
  const ordered = effectiveOrderedItems.value
  if (ordered.length === 0) {
    if (props.compact) {
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
          sourceItem: null,
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
          sourceItem: null,
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
          sourceItem: null,
        },
      ]
    }
    return [
      {
        key: 'empty-guide',
        kind: 'empty',
        category: 'generic',
        label: t('workspaceFolder.emptyGuideCard'),
        badge: '',
        thumbUrl: '',
        isPrimary: false,
        accentColor: palette.value.swatch,
        sourceItem: null,
      },
    ]
  }

  return ordered.slice(0, 6).map((item): LivingFolderCardDescriptor => {
    if (item.kind === 'root') {
      return {
        key: item.key,
        kind: 'folder',
        category: 'folder',
        label: basenameFromPath(item.root.path),
        badge: 'DIR',
        thumbUrl: '',
        isPrimary: item.root.id === primaryRootResolved.value,
        accentColor: palette.value.swatch,
        sourceItem: item,
      }
    }

    const cat = classifyWorkspaceFileVisual(item.file.path)
    const kind: LivingFolderCardDescriptor['kind'] =
      cat === 'image'
        ? 'photo'
        : cat === 'code' || cat === 'config'
          ? 'code'
          : 'doc'

    return {
      key: item.key,
      kind,
      category: cat,
      label: basenameFromPath(item.file.path),
      badge: fileBadgeLabel(item.file.path, cat),
      thumbUrl: cat === 'image' && !failedThumbs[item.key] ? buildWorkspaceMediaThumbUrl(item.file.path) : '',
      isPrimary: false,
      accentColor: DOC_BADGE_COLORS[cat] ?? '#57534e',
      sourceItem: item,
    }
  })
})

const fallbackCards = computed<LivingFolderCardDescriptor[]>(() =>
  cardDescriptors.value.filter((card) => card.kind !== 'empty'),
)

function buildEngineState(): LivingFolderEngineState {
  return {
    tone: effectiveTone.value,
    theme: resolvedTheme.value,
    cards: cardDescriptors.value,
    hovered: hovered.value,
    externalDragging: dragging.value,
    altInvertActive: dragging.value && altPressed.value,
    compact: props.compact,
    disabled: props.disabled,
    reducedMotion: reducedMotion.value,
    emptyGuideLabel: t('workspaceFolder.emptyGuideCard'),
  }
}

function cardSlotStyle(index: number, total: number): Record<string, string> {
  const center = (total - 1) / 2
  const offset = total <= 1 ? 0 : index - center
  const spread = props.compact ? 20 : 28
  const baseX = offset * spread
  const baseY = Math.abs(offset) * (props.compact ? 3 : 4.5)
  const baseRot = offset * 3.8
  const hoverX = offset * (spread + (props.compact ? 4 : 6))
  const hoverRot = offset * 6.4
  const partDirection = offset < 0 ? -1 : offset > 0 ? 1 : index % 2 === 0 ? -1 : 1
  const partX = baseX + partDirection * (props.compact ? 10 : 18)

  return {
    '--card-x': `${baseX.toFixed(1)}px`,
    '--card-y': `${baseY.toFixed(1)}px`,
    '--card-rot': `${baseRot.toFixed(1)}deg`,
    '--card-hover-x': `${hoverX.toFixed(1)}px`,
    '--card-hover-rot': `${hoverRot.toFixed(1)}deg`,
    '--card-part-x': `${partX.toFixed(1)}px`,
    '--card-z': `${index + 2}`,
    '--card-accent': fallbackCards.value[index]?.accentColor ?? '#64748b',
  }
}

const resolvedLiveDropMode = computed<'link' | 'copy' | 'ask'>(() => {
  const base = effectiveDropAction.value
  if (altPressed.value) {
    return base === 'copy' ? 'link' : 'copy'
  }
  return base
})

const hasActiveCardHover = computed(
  () => hoveredCard.value !== null && hoveredCard.value.kind !== 'empty',
)

const dragCaptionText = computed(() => {
  if (dragging.value) {
    const mode = resolvedLiveDropMode.value
    if (altPressed.value) {
      return mode === 'copy'
        ? t('workspaceFolder.dragHintCopyAlt')
        : t('workspaceFolder.dragHintLinkAlt')
    }
    if (mode === 'copy') return t('workspaceFolder.dragHintCopy')
    if (mode === 'ask') return t('workspaceFolder.dragHintAsk')
    return t('workspaceFolder.dragHintLink')
  }

  if (cardDragging.value) {
    return t('workspaceFolder.cardDraggingHint')
  }

  if (hoveredCard.value !== null && hoveredCard.value.kind !== 'empty') {
    return t('workspaceFolder.cardHoverHint', { name: hoveredCard.value.label })
  }

  return t('workspaceFolder.restHint')
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
  engine?.triggerDropPulse()
  if (reducedMotion.value) return
  if (snapTimer) clearTimeout(snapTimer)
  snapping.value = true
  snapTimer = setTimeout(() => {
    snapping.value = false
    snapTimer = null
  }, 520)
}

function onWrapMouseLeave(): void {
  hovered.value = false
  if (!webglActive.value && !cardDragging.value) {
    hoveredCard.value = null
  }
}

function onFallbackCardEnter(card: LivingFolderCardDescriptor): void {
  if (props.compact || props.disabled) return
  hoveredCard.value = card
}

function onFallbackCardLeave(card: LivingFolderCardDescriptor): void {
  if (hoveredCard.value?.key === card.key) {
    hoveredCard.value = null
  }
}

function onFallbackCardClick(card: LivingFolderCardDescriptor): void {
  if (props.compact || props.disabled || !card.sourceItem) return
  emit('select-card', card.sourceItem)
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

watch(
  [
    effectiveTone,
    resolvedTheme,
    cardDescriptors,
    hovered,
    dragging,
    altPressed,
    () => props.compact,
    () => props.disabled,
    reducedMotion,
    () => t('workspaceFolder.emptyGuideCard'),
  ],
  () => {
    if (props.compact || props.disabled) {
      hoveredCard.value = null
      cardDragging.value = false
    }
    engine?.updateState(buildEngineState())
  },
)

onMounted(() => {
  if (typeof window !== 'undefined') {
    window.addEventListener('keydown', onWindowKeyChange)
    window.addEventListener('keyup', onWindowKeyChange)
    if (typeof window.matchMedia === 'function') {
      colorSchemeMql = window.matchMedia('(prefers-color-scheme: dark)')
      systemPrefersDark.value = colorSchemeMql.matches
      colorSchemeMql.addEventListener?.('change', onColorSchemeChange)
    }
  }

  if (canvasRef.value && stageRef.value) {
    engine = createLivingFolderEngine(
      canvasRef.value,
      stageRef.value,
      buildEngineState(),
      {
        onHoverCard: (card) => {
          hoveredCard.value = card
        },
        onSelectCard: (card) => {
          if (props.compact || props.disabled) return
          if (card?.sourceItem) {
            emit('select-card', card.sourceItem)
          }
        },
        onReorderCards: (reorderedVisibleKeys) => {
          if (props.compact || props.disabled) return
          emit('reorder-cards', [...reorderedVisibleKeys])
        },
        onDragStateChange: (active) => {
          cardDragging.value = Boolean(active)
        },
      },
    )
    webglActive.value = engine.isWebGLActive
  }
})

onBeforeUnmount(() => {
  if (snapTimer) {
    clearTimeout(snapTimer)
    snapTimer = null
  }
  engine?.dispose()
  engine = null
  if (typeof window !== 'undefined') {
    window.removeEventListener('keydown', onWindowKeyChange)
    window.removeEventListener('keyup', onWindowKeyChange)
  }
  colorSchemeMql?.removeEventListener?.('change', onColorSchemeChange)
  colorSchemeMql = null
})
</script>

<template>
  <div
    class="living-folder-wrap"
    :class="{
      'is-compact': compact,
      'is-hovered': (hovered || cardDragging || hasActiveCardHover) && !dragging,
      'is-dragging': dragging,
      'is-card-dragging': cardDragging,
      'is-alt-invert': dragging && altPressed,
      'is-snapping': snapping,
      'is-reduced-motion': reducedMotion,
      'is-disabled': disabled,
    }"
    :data-folder-tone="effectiveTone"
    :data-folder-state="dragging ? 'dragover' : cardDragging ? 'reordering' : hovered ? 'hover' : 'rest'"
    :style="stageVars"
    data-testid="living-workspace-folder"
    @mouseenter="hovered = true"
    @mouseleave="onWrapMouseLeave"
    @dragenter.prevent="onDragEnter"
    @dragover.prevent="onDragOver"
    @dragleave="onDragLeave"
    @drop.prevent="onDrop"
  >
    <div ref="stageRef" class="living-folder-stage">
      <!-- Hover / Dragover rounded backing plate (flat 2D backdrop, zero preserve-3d/filter flattening) -->
      <div class="living-folder-backing" aria-hidden="true" />

      <!-- Soft ambient ground shadow -->
      <div class="living-folder-ambient" aria-hidden="true" />

      <!-- True Three.js WebGL2 3D Canvas -->
      <canvas
        ref="canvasRef"
        class="living-folder-canvas"
        :class="{ 'is-inactive': !webglActive }"
        data-testid="living-folder-canvas"
      />

      <!-- Accessible GPU-less / happy-dom Fallback -->
      <div v-if="!webglActive" class="living-folder-fallback">
        <svg viewBox="0 0 320 215" class="living-folder-backplate" aria-hidden="true">
          <defs>
            <linearGradient :id="`${uid}-bp`" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stop-color="var(--lf-back-top)" />
              <stop offset="100%" stop-color="var(--lf-back-bottom)" />
            </linearGradient>
          </defs>
          <path
            d="M22 54C22 42.954 30.954 34 42 34H116C123.6 34 130.75 37.56 135.34 43.62L144.66 55.88C147.68 59.86 152.38 62.2 157.38 62.2H278C289.046 62.2 298 71.154 298 82.2V182C298 193.046 289.046 202 278 202H42C30.954 202 22 193.046 22 182V54Z"
            :fill="`url(#${uid}-bp)`"
            stroke="var(--lf-back-stroke)"
            stroke-width="1.2"
          />
          <path
            d="M38 46H112"
            stroke="rgba(255, 255, 255, 0.42)"
            stroke-width="1.5"
            stroke-linecap="round"
          />
          <rect
            x="30"
            y="70"
            width="260"
            height="122"
            rx="14"
            fill="var(--lf-pocket-well)"
          />
        </svg>

        <div class="living-folder-pocket" data-testid="living-folder-pocket">
          <template v-if="fallbackCards.length > 0">
            <button
              v-for="(card, idx) in fallbackCards"
              :key="card.key"
              type="button"
              class="pocket-card"
              :class="[
                `pocket-card--${card.kind}`,
                { 'is-card-hovered': hoveredCard?.key === card.key },
              ]"
              :data-pocket-card-key="card.key"
              :data-pocket-kind="card.kind"
              :style="cardSlotStyle(idx, fallbackCards.length)"
              :title="card.label"
              :disabled="compact || disabled || !card.sourceItem"
              @mouseenter="onFallbackCardEnter(card)"
              @mouseleave="onFallbackCardLeave(card)"
              @click="onFallbackCardClick(card)"
            >
              <!-- Image photo card -->
              <template v-if="card.kind === 'photo'">
                <span class="pocket-photo-frame">
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
                </span>
                <span v-if="!compact" class="pocket-card-caption">{{ card.label }}</span>
              </template>

              <!-- Mini folder tab card -->
              <template v-else-if="card.kind === 'folder'">
                <span class="pocket-folder-tab">
                  <span class="pocket-folder-ear" />
                  <span class="pocket-folder-body">
                    <Star v-if="card.isPrimary" :size="compact ? 8 : 10" class="pocket-folder-star" />
                    <span v-if="!compact" class="pocket-folder-name">{{ card.label }}</span>
                  </span>
                </span>
              </template>

              <!-- Code / Config card -->
              <template v-else-if="card.kind === 'code'">
                <span class="pocket-code-head">
                  <span class="pocket-code-glyph">&lt;/&gt;</span>
                  <span class="pocket-code-badge">{{ card.badge }}</span>
                </span>
                <span class="pocket-code-lines" aria-hidden="true">
                  <span class="cline cline--a" />
                  <span class="cline cline--b" />
                  <span class="cline cline--c" />
                </span>
                <span v-if="!compact" class="pocket-card-caption pocket-card-caption--dark">{{ card.label }}</span>
              </template>

              <!-- Folded-corner document card -->
              <template v-else>
                <span class="pocket-doc-fold" aria-hidden="true" />
                <span class="pocket-doc-badge">{{ card.badge }}</span>
                <span class="pocket-doc-lines" aria-hidden="true">
                  <span class="dline dline--title" />
                  <span class="dline dline--body" />
                  <span class="dline dline--short" />
                </span>
                <span v-if="!compact" class="pocket-card-caption">{{ card.label }}</span>
              </template>
            </button>
          </template>

          <!-- Empty directory dashed guide card -->
          <div v-else class="pocket-empty-guide" data-testid="living-folder-empty-guide">
            <span class="pocket-empty-guide-text">{{ t('workspaceFolder.emptyGuideCard') }}</span>
          </div>
        </div>

        <div class="living-folder-flap" data-testid="living-folder-flap" aria-hidden="true">
          <div class="flap-layer-frost" />
          <div class="flap-layer-specular" />
          <div class="flap-layer-sheen" />
        </div>
      </div>

      <!-- Frosted bottom-right count capsule -->
      <div v-if="!compact" class="flap-count-pill" data-testid="living-folder-count-pill">
        {{ t('workspaceFolder.countBadge', { folders: roots.length, files: files.length }) }}
      </div>
    </div>

    <!-- Single-line live drop / hover / reorder hint below folder -->
    <p
      v-if="!compact"
      class="living-folder-caption"
      :class="{
        'is-active': dragging || cardDragging || hasActiveCardHover,
        'is-alt': dragging && altPressed,
      }"
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
  max-width: 296px;
  aspect-ratio: 320 / 215;
  display: flex;
  align-items: center;
  justify-content: center;
}

.living-folder-wrap.is-compact {
  width: auto;
  gap: 0;
}

.living-folder-wrap.is-compact .living-folder-stage {
  width: 148px;
  max-width: 148px;
}

/* Soft outer backing plate appearing on Hover / Dragover */
.living-folder-backing {
  position: absolute;
  inset: 4px 6px;
  border-radius: 22px;
  background: color-mix(in srgb, var(--surface-raised, rgba(255, 255, 255, 0.06)) 78%, transparent);
  border: 1px solid color-mix(in srgb, var(--border-default, rgba(255, 255, 255, 0.14)) 65%, transparent);
  opacity: 0;
  transform: scale(0.97);
  transition: opacity 220ms cubic-bezier(0.22, 1, 0.36, 1), transform 240ms cubic-bezier(0.22, 1, 0.36, 1), border-color 200ms ease;
  pointer-events: none;
  z-index: 0;
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

/* Ambient ground shadow */
.living-folder-ambient {
  position: absolute;
  left: 14%;
  right: 14%;
  bottom: 5px;
  height: 26px;
  border-radius: 999px;
  background: var(--lf-ambient-shadow);
  filter: blur(16px);
  opacity: 0.76;
  transform: translateY(3px) scaleX(0.94);
  transition:
    background-color 220ms ease,
    opacity 220ms ease,
    transform 260ms cubic-bezier(0.22, 1, 0.36, 1);
  pointer-events: none;
  z-index: 0;
}

.living-folder-wrap.is-hovered .living-folder-ambient {
  opacity: 0.94;
  transform: translateY(6px) scaleX(1);
}

.living-folder-wrap.is-dragging .living-folder-ambient {
  background: var(--lf-ready-glow);
  opacity: 1;
  transform: translateY(8px) scaleX(1.05);
}

/* Three.js WebGL2 Canvas */
.living-folder-canvas {
  position: relative;
  z-index: 1;
  width: 100%;
  height: 100%;
  display: block;
  touch-action: none;
}

.living-folder-canvas.is-inactive {
  position: absolute;
  inset: 0;
  opacity: 0;
  pointer-events: none;
}

/* Accessible No-WebGL Fallback Container */
.living-folder-fallback {
  position: relative;
  z-index: 2;
  width: 100%;
  height: 100%;
}

.living-folder-backplate {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  display: block;
  pointer-events: none;
}

.living-folder-pocket {
  position: absolute;
  left: 11%;
  right: 11%;
  top: 16%;
  bottom: 16%;
  display: flex;
  align-items: flex-start;
  justify-content: center;
  z-index: 2;
}

.pocket-card {
  position: absolute;
  top: 8%;
  width: 27%;
  height: 74%;
  border-radius: 8px;
  padding: 5px 5px 4px;
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  text-align: left;
  font: inherit;
  cursor: pointer;
  box-shadow: 0 4px 12px rgba(15, 23, 42, 0.22);
  transform-origin: 50% 100%;
  z-index: var(--card-z, 2);
  transform: translate(var(--card-x, 0px), var(--card-y, 0px)) rotate(var(--card-rot, 0deg));
  transition: transform 260ms cubic-bezier(0.22, 1, 0.36, 1), box-shadow 220ms ease;
}

.pocket-card:disabled {
  cursor: default;
}

.pocket-card:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 2px;
}

.living-folder-wrap.is-compact .pocket-card {
  padding: 3px;
  border-radius: 5px;
}

.living-folder-wrap.is-hovered .pocket-card {
  transform: translate(var(--card-hover-x, 0px), calc(var(--card-y, 0px) - 22px)) rotate(var(--card-hover-rot, 0deg));
  box-shadow: 0 8px 18px rgba(15, 23, 42, 0.26);
}

.living-folder-wrap.is-hovered .pocket-card.is-card-hovered {
  transform: translate(var(--card-hover-x, 0px), calc(var(--card-y, 0px) - 32px)) scale(1.05);
  z-index: 12;
}

.living-folder-wrap.is-compact.is-hovered .pocket-card {
  transform: translate(var(--card-hover-x, 0px), calc(var(--card-y, 0px) - 10px)) rotate(var(--card-hover-rot, 0deg));
}

.living-folder-wrap.is-dragging .pocket-card {
  transform: translate(var(--card-part-x, 0px), calc(var(--card-y, 0px) - 10px)) rotate(calc(var(--card-hover-rot, 0deg) * 1.15));
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
  display: block;
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
  width: 100%;
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
  width: 100%;
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
  width: 100%;
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
  border: 0;
  background: transparent;
  box-shadow: none;
}

.pocket-folder-tab {
  width: 100%;
  height: 100%;
  display: flex;
  flex-direction: column;
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
  box-shadow: 0 4px 10px rgba(15, 23, 42, 0.18);
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
  width: 100%;
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
  height: 66%;
  margin-top: 10%;
  border-radius: 10px;
  border: 1.5px dashed rgba(255, 255, 255, 0.58);
  background: rgba(255, 255, 255, 0.16);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 6px 10px;
  text-align: center;
  pointer-events: none;
}

.pocket-empty-guide-text {
  font-size: 10.5px;
  font-weight: 600;
  color: rgba(255, 255, 255, 0.92);
  text-shadow: 0 1px 2px rgba(15, 23, 42, 0.28);
}

/* Fallback frosted front flap (pointer-events: none so fallback card buttons stay clickable) */
.living-folder-flap {
  position: absolute;
  left: 6.8%;
  right: 6.8%;
  top: 41%;
  bottom: 6%;
  border-radius: 15px 15px 17px 17px;
  overflow: hidden;
  transform-origin: 50% 100%;
  z-index: 20;
  pointer-events: none;
  box-shadow:
    0 -2px 10px rgba(15, 23, 42, 0.10),
    inset 0 -1px 0 var(--lf-refraction-bottom);
  transition: transform 260ms cubic-bezier(0.22, 1, 0.36, 1), opacity 220ms ease;
}

.living-folder-wrap.is-compact .living-folder-flap {
  border-radius: 10px 10px 12px 12px;
}

.living-folder-wrap.is-hovered .living-folder-flap {
  transform: scaleY(0.92);
  opacity: 0.86;
}

.living-folder-wrap.is-dragging .living-folder-flap {
  transform: scaleY(0.86);
  box-shadow:
    0 0 0 1.5px var(--lf-specular-top),
    0 8px 24px var(--lf-ready-glow);
}

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
}

.flap-layer-specular {
  position: absolute;
  inset: 0;
  border-radius: inherit;
  border-top: 1px solid var(--lf-specular-top);
  border-bottom: 1px solid var(--lf-refraction-bottom);
  border-left: 1px solid rgba(255, 255, 255, 0.32);
  border-right: 1px solid rgba(255, 255, 255, 0.32);
}

.flap-layer-sheen {
  position: absolute;
  inset: -20% -40%;
  background: linear-gradient(
    115deg,
    transparent 34%,
    rgba(255, 255, 255, 0.34) 48%,
    rgba(255, 255, 255, 0.08) 56%,
    transparent 68%
  );
  transform: translateX(-46%);
  opacity: 0;
  transition: transform 480ms cubic-bezier(0.22, 1, 0.36, 1), opacity 240ms ease;
}

.living-folder-wrap.is-hovered .flap-layer-sheen,
.living-folder-wrap.is-dragging .flap-layer-sheen {
  transform: translateX(38%);
  opacity: 1;
}

/* Frosted bottom-right count capsule overlaid on stage */
.flap-count-pill {
  position: absolute;
  right: 22px;
  bottom: 16px;
  z-index: 25;
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

/* Single-line live drop / hover / reorder caption */
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
  color: var(--text-on-primary, var(--bg-primary, #09090b));
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
.living-folder-wrap.is-reduced-motion .pocket-card,
.living-folder-wrap.is-reduced-motion .living-folder-flap,
.living-folder-wrap.is-reduced-motion .flap-layer-sheen,
.living-folder-wrap.is-reduced-motion .living-folder-caption {
  transition: none !important;
  animation: none !important;
}

.living-folder-wrap.is-reduced-motion .flap-layer-sheen {
  display: none;
}
</style>
