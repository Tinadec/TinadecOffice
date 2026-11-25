<script setup lang="ts">
import { computed, ref, useId, watch } from 'vue'
import type { UiWorkspaceFolderTone } from '@/composables/useUiPreferences'
import {
  FOLDER_TONE_PALETTES,
  buildWorkspaceMediaThumbUrl,
  classifyWorkspaceFileVisual,
  fileBadgeLabel,
  type FileVisualCategory,
} from './workspaceVisuals'

const props = withDefaults(defineProps<{
  kind?: 'folder' | 'file'
  path?: string
  name?: string
  isPrimary?: boolean
  size?: number
  tone?: UiWorkspaceFolderTone
}>(), {
  kind: 'file',
  path: '',
  name: '',
  isPrimary: false,
  size: 40,
  tone: 'amber',
})

const uid = useId()
const imageFailed = ref(false)

watch(
  () => props.path,
  () => {
    imageFailed.value = false
  },
)

const category = computed<FileVisualCategory>(() => {
  if (props.kind === 'folder') return 'folder'
  return classifyWorkspaceFileVisual(props.path || props.name)
})

const folderPalette = computed(() => FOLDER_TONE_PALETTES[props.tone] ?? FOLDER_TONE_PALETTES.amber)

const mediaSrc = computed(() => {
  if (category.value !== 'image' || imageFailed.value) return ''
  const source = props.path.trim()
  return source ? buildWorkspaceMediaThumbUrl(source) : ''
})

const badgeText = computed(() => {
  if (category.value === 'folder' || category.value === 'image') return ''
  return fileBadgeLabel(props.path || props.name, category.value)
})

interface DocPalette {
  top: string
  bottom: string
  fold: string
  ribbon: string
  stroke: string
}

const DOC_PALETTES: Record<Exclude<FileVisualCategory, 'folder' | 'image'>, DocPalette> = {
  pdf: {
    top: '#f87171',
    bottom: '#dc2626',
    fold: '#fecaca',
    ribbon: '#991b1b',
    stroke: 'rgba(254, 226, 226, 0.55)',
  },
  doc: {
    top: '#38bdf8',
    bottom: '#0284c7',
    fold: '#bae6fd',
    ribbon: '#075985',
    stroke: 'rgba(224, 242, 254, 0.58)',
  },
  sheet: {
    top: '#34d399',
    bottom: '#059669',
    fold: '#a7f3d0',
    ribbon: '#065f46',
    stroke: 'rgba(209, 250, 229, 0.55)',
  },
  code: {
    top: '#818cf8',
    bottom: '#4338ca',
    fold: '#c7d2fe',
    ribbon: '#312e81',
    stroke: 'rgba(224, 231, 255, 0.55)',
  },
  config: {
    top: '#a78bfa',
    bottom: '#6d28d9',
    fold: '#ddd6fe',
    ribbon: '#4c1d95',
    stroke: 'rgba(237, 233, 254, 0.52)',
  },
  archive: {
    top: '#fbbf24',
    bottom: '#b45309',
    fold: '#fde68a',
    ribbon: '#78350f',
    stroke: 'rgba(254, 243, 199, 0.58)',
  },
  media: {
    top: '#fb7185',
    bottom: '#e11d48',
    fold: '#fecdd3',
    ribbon: '#9f1239',
    stroke: 'rgba(255, 228, 230, 0.55)',
  },
  generic: {
    top: '#a8a29e',
    bottom: '#57534e',
    fold: '#e7e5e4',
    ribbon: '#44403c',
    stroke: 'rgba(245, 245, 244, 0.52)',
  },
}

const docPalette = computed(() => {
  if (category.value === 'folder' || category.value === 'image') return DOC_PALETTES.generic
  return DOC_PALETTES[category.value]
})

const boxStyle = computed(() => ({
  width: `${props.size}px`,
  height: `${props.size}px`,
}))
</script>

<template>
  <span
    class="ftv-icon"
    :class="`ftv-icon--${category}`"
    :data-visual-category="category"
    :style="boxStyle"
    aria-hidden="true"
  >
    <!-- 1. Folder Visual (tactile gradient + frosted pocket + optional primary gold star) -->
    <svg v-if="category === 'folder'" viewBox="0 0 48 48" class="ftv-svg">
      <defs>
        <linearGradient :id="`${uid}-fld-back`" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" :stop-color="folderPalette.backTop" />
          <stop offset="100%" :stop-color="folderPalette.backBottom" />
        </linearGradient>
        <linearGradient :id="`${uid}-fld-front`" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" :stop-color="folderPalette.flapTop" />
          <stop offset="55%" :stop-color="folderPalette.flapMid" />
          <stop offset="100%" :stop-color="folderPalette.flapBottom" />
        </linearGradient>
        <linearGradient :id="`${uid}-star`" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stop-color="#fef08a" />
          <stop offset="50%" stop-color="#fbbf24" />
          <stop offset="100%" stop-color="#d97706" />
        </linearGradient>
      </defs>
      <!-- Backplate + tab ear -->
      <path
        d="M5 12.5C5 10.015 7.015 8 9.5 8H18.2C19.55 8 20.83 8.61 21.68 9.66L23.15 11.48C23.62 12.06 24.33 12.4 25.08 12.4H38.5C40.985 12.4 43 14.415 43 16.9V36.5C43 38.985 40.985 41 38.5 41H9.5C7.015 41 5 38.985 5 36.5V12.5Z"
        :fill="`url(#${uid}-fld-back)`"
      />
      <!-- Subtle inner paper sheet peeking -->
      <rect x="9.5" y="14" width="29" height="12" rx="2.5" fill="rgba(255,255,255,0.82)" />
      <!-- Frosted front flap -->
      <rect
        x="4.5"
        y="18.5"
        width="39"
        height="22.5"
        rx="5"
        :fill="`url(#${uid}-fld-front)`"
        :stroke="folderPalette.specularTop"
        stroke-width="0.9"
      />
      <line x1="8" y1="19.4" x2="40" y2="19.4" :stroke="folderPalette.specularTop" stroke-width="1.1" stroke-linecap="round" />
      <!-- Primary root gold star -->
      <g v-if="isPrimary" transform="translate(30, 28)">
        <circle cx="7" cy="7" r="7.2" fill="rgba(24, 24, 27, 0.56)" />
        <path
          d="M7 1.8L8.55 5.02L12.1 5.52L9.52 8.01L10.14 11.54L7 9.86L3.86 11.54L4.48 8.01L1.9 5.52L5.45 5.02L7 1.8Z"
          :fill="`url(#${uid}-star)`"
          stroke="#fffbeb"
          stroke-width="0.55"
          stroke-linejoin="round"
        />
      </g>
    </svg>

    <!-- 2. Image Photo Card (real /api/v1/fs/media thumbnail with landscape SVG fallback) -->
    <span v-else-if="category === 'image'" class="ftv-photo-card">
      <img
        v-if="mediaSrc"
        :src="mediaSrc"
        alt=""
        loading="lazy"
        class="ftv-photo-img"
        @error="imageFailed = true"
      />
      <svg v-else viewBox="0 0 48 48" class="ftv-svg">
        <defs>
          <linearGradient :id="`${uid}-img-sky`" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stop-color="#38bdf8" />
            <stop offset="65%" stop-color="#818cf8" />
            <stop offset="100%" stop-color="#f472b6" />
          </linearGradient>
        </defs>
        <rect x="5" y="6" width="38" height="36" rx="6" fill="#f8fafc" stroke="rgba(148,163,184,0.45)" stroke-width="1" />
        <rect x="8.5" y="9.5" width="31" height="25.5" rx="3.5" :fill="`url(#${uid}-img-sky)`" />
        <circle cx="31.5" cy="16.5" r="3.4" fill="#fef08a" />
        <path d="M8.5 31.5L17.5 21.5L24.5 28.5L30.5 23L39.5 32.5V35H8.5V31.5Z" fill="rgba(15, 23, 42, 0.42)" />
        <path d="M8.5 35L15 27.5L22.5 35H8.5Z" fill="rgba(255, 255, 255, 0.28)" />
      </svg>
    </span>

    <!-- 3. Folded-Corner Specular Document / Code / Sheet / Config / Archive / Media SVG -->
    <svg v-else viewBox="0 0 48 48" class="ftv-svg">
      <defs>
        <linearGradient :id="`${uid}-doc-bg`" x1="0.1" y1="0" x2="0.9" y2="1">
          <stop offset="0%" :stop-color="docPalette.top" />
          <stop offset="100%" :stop-color="docPalette.bottom" />
        </linearGradient>
        <linearGradient :id="`${uid}-doc-fold`" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stop-color="#ffffff" stop-opacity="0.92" />
          <stop offset="100%" :stop-color="docPalette.fold" stop-opacity="0.88" />
        </linearGradient>
      </defs>

      <!-- Document body with folded top-right dog-ear -->
      <path
        d="M10 6.5C10 4.843 11.343 3.5 13 3.5H29.2L38.5 12.8V41.5C38.5 43.157 37.157 44.5 35.5 44.5H13C11.343 44.5 10 43.157 10 41.5V6.5Z"
        :fill="`url(#${uid}-doc-bg)`"
        :stroke="docPalette.stroke"
        stroke-width="0.9"
      />
      <!-- Folded corner shadow & specular flap -->
      <path
        d="M29.2 3.5V10.5C29.2 11.77 30.23 12.8 31.5 12.8H38.5L29.2 3.5Z"
        :fill="`url(#${uid}-doc-fold)`"
      />

      <!-- Category-specific tactile interior motif -->
      <g v-if="category === 'sheet'" opacity="0.92">
        <rect x="14.5" y="16" width="19" height="13" rx="1.8" fill="none" stroke="rgba(255,255,255,0.82)" stroke-width="1.2" />
        <line x1="14.5" y1="20.4" x2="33.5" y2="20.4" stroke="rgba(255,255,255,0.75)" stroke-width="1" />
        <line x1="14.5" y1="24.7" x2="33.5" y2="24.7" stroke="rgba(255,255,255,0.75)" stroke-width="1" />
        <line x1="21.2" y1="16" x2="21.2" y2="29" stroke="rgba(255,255,255,0.75)" stroke-width="1" />
      </g>

      <g v-else-if="category === 'code'" stroke="rgba(255,255,255,0.9)" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" fill="none">
        <polyline points="19.5,18 15,22.5 19.5,27" />
        <polyline points="28.5,18 33,22.5 28.5,27" />
        <line x1="25.5" y1="17" x2="22.5" y2="28" stroke-width="1.5" opacity="0.82" />
      </g>

      <g v-else-if="category === 'config'" stroke="rgba(255,255,255,0.88)" stroke-width="1.5" stroke-linecap="round" fill="none">
        <path d="M19.5 17C17.8 17 17.2 17.9 17.2 19.3V20.8C17.2 21.8 16.4 22.5 15.5 22.5C16.4 22.5 17.2 23.2 17.2 24.2V25.7C17.2 27.1 17.8 28 19.5 28" />
        <path d="M28.5 17C30.2 17 30.8 17.9 30.8 19.3V20.8C30.8 21.8 31.6 22.5 32.5 22.5C31.6 22.5 30.8 23.2 30.8 24.2V25.7C30.8 27.1 30.2 28 28.5 28" />
        <circle cx="24" cy="20.6" r="1.1" fill="#ffffff" stroke="none" />
        <circle cx="24" cy="24.6" r="1.1" fill="#ffffff" stroke="none" />
      </g>

      <g v-else-if="category === 'archive'" fill="rgba(255,255,255,0.86)">
        <rect x="22" y="8" width="4" height="2.4" rx="0.8" />
        <rect x="22" y="12" width="4" height="2.4" rx="0.8" />
        <rect x="22" y="16" width="4" height="2.4" rx="0.8" />
        <rect x="20.8" y="20" width="6.4" height="7.2" rx="1.8" fill="none" stroke="rgba(255,255,255,0.9)" stroke-width="1.4" />
        <circle cx="24" cy="23.8" r="1.2" />
      </g>

      <g v-else-if="category === 'media'">
        <circle cx="24" cy="22.5" r="6.8" fill="rgba(255,255,255,0.20)" stroke="rgba(255,255,255,0.78)" stroke-width="1.2" />
        <path d="M22.3 19.3L27.6 22.5L22.3 25.7V19.3Z" fill="#ffffff" />
      </g>

      <g v-else stroke="rgba(255,255,255,0.82)" stroke-width="1.6" stroke-linecap="round">
        <line x1="15.5" y1="17.5" x2="26.5" y2="17.5" />
        <line x1="15.5" y1="22" x2="32.5" y2="22" opacity="0.85" />
        <line x1="15.5" y1="26.5" x2="29.5" y2="26.5" opacity="0.72" />
      </g>

      <!-- Bottom badge pill -->
      <rect x="13.5" y="32.2" width="21" height="8.6" rx="3" :fill="docPalette.ribbon" opacity="0.84" />
      <text
        x="24"
        y="38.2"
        text-anchor="middle"
        fill="#ffffff"
        font-size="6.4"
        font-weight="700"
        font-family="ui-sans-serif, system-ui, -apple-system, sans-serif"
        letter-spacing="0.4"
      >{{ badgeText }}</text>
    </svg>
  </span>
</template>

<style scoped>
.ftv-icon {
  position: relative;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  user-select: none;
}

.ftv-svg {
  width: 100%;
  height: 100%;
  display: block;
  filter: drop-shadow(0 2px 5px rgba(15, 23, 42, 0.16));
}

.ftv-photo-card {
  width: 86%;
  height: 86%;
  padding: 2.5px;
  border-radius: 7px;
  background: linear-gradient(180deg, #ffffff 0%, #f1f5f9 100%);
  border: 1px solid rgba(148, 163, 184, 0.42);
  box-shadow: 0 3px 8px rgba(15, 23, 42, 0.18);
  display: flex;
  align-items: center;
  justify-content: center;
  overflow: hidden;
}

.ftv-photo-img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  border-radius: 4.5px;
  display: block;
}

.ftv-photo-card .ftv-svg {
  width: 118%;
  height: 118%;
  filter: none;
}
</style>
