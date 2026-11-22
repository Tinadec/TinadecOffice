<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Handle, Position } from '@vue-flow/core'
import { NodeResizer } from '@vue-flow/node-resizer'
import { ExternalLink, FileCode2, Image as ImageIcon } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import { resolveWebGatewayBase } from '@/lib/hostConnection'
import { readFileText, type ReadFileDataDto } from '@/lib/workspaceSearch'
import {
  encodeMediaPathToken,
  type SpatialMediaKind,
  type SpatialMediaProps,
} from './spatialWhiteboardTypes'

const props = withDefaults(defineProps<{
  id: string
  mediaKind?: SpatialMediaKind
  path?: string
  url?: string
  title?: string
  snippet?: string
  language?: string
  cwd?: string
  selected?: boolean
  readonly?: boolean
  data?: Partial<SpatialMediaProps>
}>(), {
  selected: false,
  readonly: false,
})

const emit = defineEmits<{
  'open-file': [payload: { id: string; path: string; mediaKind: SpatialMediaKind }]
  'snippet-loaded': [payload: { id: string; snippet: string }]
  'resize-start': []
  'resize-end': [params: { x: number; y: number; width: number; height: number }]
}>()

const { t } = useI18n()

const resolvedKind = computed<SpatialMediaKind>(() => props.mediaKind ?? props.data?.mediaKind ?? 'file')
const resolvedPath = computed(() => props.path ?? props.data?.path ?? '')
const resolvedUrl = computed(() => props.url ?? props.data?.url ?? '')
const resolvedLanguage = computed(() => props.language ?? props.data?.language ?? '')
const resolvedTitle = computed(() => {
  const explicit = props.title ?? props.data?.title
  if (explicit) return explicit
  const normalized = resolvedPath.value.replace(/\\/g, '/')
  return normalized.split('/').filter(Boolean).pop() || t('space.whiteboard.filePreview')
})

const loadedSnippet = ref('')
const loadingSnippet = ref(false)
const loadError = ref(false)
const imageFailed = ref(false)
const sides = [Position.Top, Position.Bottom, Position.Left, Position.Right] as const

const displaySnippet = computed(() => props.snippet ?? props.data?.snippet ?? loadedSnippet.value)

const snippetLines = computed(() => {
  const content = displaySnippet.value
  if (!content) return []
  return content.split(/\r?\n/).slice(0, 40)
})

const resolvedImageSrc = computed(() => {
  if (resolvedKind.value !== 'image') return ''
  const candidate = (resolvedUrl.value || resolvedPath.value).trim()
  if (!candidate) return ''
  if (
    /^(?:https?:|data:|blob:|tinadec-media:)/i.test(candidate)
    || candidate.startsWith('/api/v1/fs/media')
  ) {
    return candidate
  }
  return `${resolveWebGatewayBase()}/api/v1/fs/media?token=${encodeMediaPathToken(candidate)}`
})

let fetchVersion = 0

async function maybeLoadSnippet() {
  if (resolvedKind.value !== 'file') return
  if (props.snippet || props.data?.snippet) return
  if (!props.cwd || !resolvedPath.value || typeof api?.readFile !== 'function') return
  const version = ++fetchVersion
  loadingSnippet.value = true
  loadError.value = false
  try {
    const result = await api.readFile(props.cwd, resolvedPath.value, { start_row: 1, end_row: 40 })
    if (version !== fetchVersion) return
    const payload = result?.data as ReadFileDataDto | undefined
    if (result?.status === 'failed' || payload?.success === false) {
      loadError.value = true
      return
    }
    const text = readFileText(payload)
    loadedSnippet.value = text
    if (text) emit('snippet-loaded', { id: props.id, snippet: text })
  } catch {
    if (version === fetchVersion) loadError.value = true
  } finally {
    if (version === fetchVersion) loadingSnippet.value = false
  }
}

watch([resolvedKind, resolvedPath, () => props.cwd, () => props.snippet, () => props.data?.snippet], () => {
  imageFailed.value = false
  void maybeLoadSnippet()
}, { immediate: true })

function handleOpen() {
  emit('open-file', {
    id: props.id,
    path: resolvedPath.value || resolvedUrl.value,
    mediaKind: resolvedKind.value,
  })
}
</script>

<template>
  <article
    class="spatial-media-node"
    :class="[
      `kind-${resolvedKind}`,
      { 'is-selected': selected },
    ]"
    :data-media-id="id"
    :data-media-kind="resolvedKind"
  >
    <template v-for="side in sides" :key="side">
      <Handle :id="`${side}-target`" type="target" :position="side" class="spatial-media-handle" />
      <Handle :id="`${side}-source`" type="source" :position="side" class="spatial-media-handle" />
    </template>

    <NodeResizer
      :is-visible="selected && !readonly"
      :min-width="180"
      :min-height="120"
      :max-width="1400"
      :max-height="1100"
      @resize-start="emit('resize-start')"
      @resize-end="({ params }) => emit('resize-end', params)"
    />

    <template v-if="resolvedKind === 'image'">
      <div class="spatial-media-image-stage space-drag-handle">
        <img
          v-if="resolvedImageSrc && !imageFailed"
          :src="resolvedImageSrc"
          :alt="resolvedTitle"
          class="spatial-media-img"
          draggable="false"
          data-testid="media-image-el"
          @error="imageFailed = true"
        />
        <div v-else class="spatial-media-empty">
          <ImageIcon :size="24" aria-hidden="true" />
          <span>{{ resolvedTitle }}</span>
        </div>
      </div>

      <footer class="spatial-media-image-bar">
        <span class="spatial-media-title" :title="resolvedPath || resolvedTitle">{{ resolvedTitle }}</span>
        <button
          type="button"
          class="spatial-media-open-btn nodrag nopan"
          :title="t('space.whiteboard.openInEditor')"
          data-testid="media-open-btn"
          @click.stop="handleOpen"
        >
          <ExternalLink :size="12" aria-hidden="true" />
          <span>{{ t('space.whiteboard.openInEditor') }}</span>
        </button>
      </footer>
    </template>

    <template v-else>
      <header class="spatial-media-file-header space-drag-handle">
        <FileCode2 :size="15" class="spatial-media-file-icon" aria-hidden="true" />
        <div class="spatial-media-file-meta">
          <strong class="spatial-media-title" :title="resolvedPath || resolvedTitle">{{ resolvedTitle }}</strong>
          <small v-if="resolvedPath && resolvedPath !== resolvedTitle" class="spatial-media-path">{{ resolvedPath }}</small>
        </div>
        <span v-if="resolvedLanguage" class="spatial-media-lang">{{ resolvedLanguage }}</span>
        <button
          type="button"
          class="spatial-media-open-btn nodrag nopan"
          :title="t('space.whiteboard.openInEditor')"
          data-testid="media-open-btn"
          @click.stop="handleOpen"
        >
          <ExternalLink :size="12" aria-hidden="true" />
          <span>{{ t('space.whiteboard.openInEditor') }}</span>
        </button>
      </header>

      <div class="spatial-media-code-body nodrag nopan nowheel">
        <p v-if="loadingSnippet" class="spatial-media-status">{{ t('space.whiteboard.filePreview') }}</p>
        <pre v-else-if="snippetLines.length" class="spatial-media-pre" data-testid="media-code-snippet"><code
          v-for="(line, index) in snippetLines"
          :key="index"
          class="spatial-media-code-line"
        ><span class="spatial-media-line-no">{{ index + 1 }}</span><span class="spatial-media-line-text">{{ line || ' ' }}</span></code></pre>
        <p v-else class="spatial-media-status" data-testid="media-empty-preview">
          {{ loadError ? t('space.fileReadFailed') : t('space.whiteboard.filePreview') }}
        </p>
      </div>
    </template>
  </article>
</template>

<style scoped>
.spatial-media-node {
  position: relative;
  width: 100%;
  height: 100%;
  min-width: 180px;
  min-height: 120px;
  display: flex;
  flex-direction: column;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, #ffffff);
  color: var(--text-primary);
  box-shadow: 0 8px 22px rgba(15, 23, 42, 0.1);
  overflow: hidden;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.spatial-media-node.is-selected {
  border-color: var(--accent-primary, #7c3aed);
  box-shadow:
    0 0 0 2px color-mix(in srgb, var(--accent-primary, #7c3aed) 24%, transparent),
    0 10px 24px rgba(15, 23, 42, 0.14);
}

.spatial-media-image-stage {
  flex: 1;
  min-height: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: color-mix(in srgb, var(--surface-section, rgba(0, 0, 0, 0.04)) 80%, transparent);
  cursor: grab;
  overflow: hidden;
}

.spatial-media-img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.spatial-media-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 16px;
  color: var(--text-muted);
  font-size: 12px;
  text-align: center;
}

.spatial-media-image-bar,
.spatial-media-file-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-top: 1px solid var(--border-muted);
  background: color-mix(in srgb, var(--surface-raised, #ffffff) 94%, transparent);
  min-width: 0;
}

.spatial-media-file-header {
  border-top: 0;
  border-bottom: 1px solid var(--border-muted);
  cursor: grab;
}

.spatial-media-file-icon {
  flex-shrink: 0;
  color: var(--accent-primary, #7c3aed);
}

.spatial-media-file-meta {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.spatial-media-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.spatial-media-path {
  font-size: 11px;
  color: var(--text-muted);
  font-family: var(--font-mono, monospace);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.spatial-media-lang {
  padding: 1px 6px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 10px;
  font-family: var(--font-mono, monospace);
  text-transform: uppercase;
  flex-shrink: 0;
}

.spatial-media-open-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-left: auto;
  padding: 3px 8px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
  flex-shrink: 0;
  transition: background-color 120ms ease, border-color 120ms ease;
}

.spatial-media-open-btn:hover {
  border-color: var(--accent-primary, #7c3aed);
  background: var(--surface-hover, rgba(128, 128, 128, 0.14));
}

.spatial-media-code-body {
  flex: 1;
  min-height: 0;
  overflow: auto;
  background: color-mix(in srgb, var(--surface-section, rgba(0, 0, 0, 0.04)) 65%, var(--surface-raised, #ffffff));
  padding: 8px 0;
  user-select: text;
}

.spatial-media-pre {
  margin: 0;
  font-family: var(--font-mono, monospace);
  font-size: 11.5px;
  line-height: 1.55;
  color: var(--text-primary);
}

.spatial-media-code-line {
  display: flex;
  padding: 0 12px 0 8px;
  white-space: pre;
}

.spatial-media-line-no {
  display: inline-block;
  width: 28px;
  padding-right: 10px;
  text-align: right;
  color: var(--text-muted);
  opacity: 0.65;
  user-select: none;
  flex-shrink: 0;
  font-variant-numeric: tabular-nums;
}

.spatial-media-line-text {
  min-width: 0;
}

.spatial-media-status {
  margin: 0;
  padding: 12px;
  font-size: 12px;
  color: var(--text-muted);
}

.spatial-media-handle {
  opacity: 0;
  width: 8px;
  height: 8px;
}
</style>
