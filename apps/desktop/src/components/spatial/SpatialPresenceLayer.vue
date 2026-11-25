<script setup lang="ts">
import { computed } from 'vue'
import { Bot, Eye, MessageSquare, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { LiveDigestTeammateThoughtDto } from '@/composables/useLiveDigest'
import type { TeammateSteerAnchorRect } from '@/components/teammate/TeammateSteerPopover.vue'
import type { SpatialViewport } from './spatialWhiteboardTypes'

export interface SpatialCollaboratorLike {
  clientId: string
  displayName: string
  color: string
  role: 'human' | 'ai'
  handle?: string | null
  runId?: string | null
  agentInstanceId?: string | null
  status?: string | null
  paused?: boolean
  thought?: string | null
  currentTask?: string | null
  cursor?: { x: number; y: number } | null
  cursorX?: number | null
  cursorY?: number | null
  selection?: string[]
  viewport?: SpatialViewport | null
  activeTool?: string | null
  followingClientId?: string | null
  updatedAtUnixMs?: number
}

export interface SpatialTeammateSteerEvent {
  collaborator: SpatialCollaboratorLike
  runId: string
  handle: string
  displayName: string
  status: string | null
  paused: boolean
  taskSummary: string | null
  thought: string | null
  anchorRect: TeammateSteerAnchorRect | null
}

export interface SpatialObjectRect {
  x: number
  y: number
  width: number
  height: number
}

const props = withDefaults(defineProps<{
  collaborators: SpatialCollaboratorLike[]
  viewport: SpatialViewport
  localClientId?: string
  followingClientId?: string | null
  objectRects?: Record<string, SpatialObjectRect>
  teammateThoughts?: Record<string, LiveDigestTeammateThoughtDto>
  activeRunId?: string | null
}>(), {
  localClientId: '',
  followingClientId: null,
  objectRects: () => ({}),
  teammateThoughts: () => ({}),
  activeRunId: null,
})

const emit = defineEmits<{
  'follow-user': [clientId: string]
  'stop-follow': []
  'steer-teammate': [payload: SpatialTeammateSteerEvent]
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? t(key, params) : t(key)
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

const remoteCollaborators = computed(() =>
  props.collaborators.filter((item) => item.clientId && item.clientId !== props.localClientId),
)

const followedCollaborator = computed(() =>
  props.followingClientId
    ? remoteCollaborators.value.find((item) => item.clientId === props.followingClientId) ?? null
    : null,
)

const followingLabel = computed(() => {
  const followed = followedCollaborator.value
  if (!followed) return ''
  const raw = t('space.whiteboard.followingBadge', { name: followed.displayName })
  return raw.includes(followed.displayName) ? raw : `${raw} ${followed.displayName}`
})

const normalizedThoughtsByKey = computed(() => {
  const map = new Map<string, LiveDigestTeammateThoughtDto>()
  for (const [key, item] of Object.entries(props.teammateThoughts ?? {})) {
    if (!item) continue
    const k = key.trim().toLowerCase().replace(/^@/, '')
    if (k) map.set(k, item)
    if (item.handle) map.set(item.handle.trim().toLowerCase().replace(/^@/, ''), item)
    if (item.display_name) map.set(item.display_name.trim().toLowerCase().replace(/^@/, ''), item)
  }
  return map
})

function resolveThoughtEntry(collaborator: SpatialCollaboratorLike): LiveDigestTeammateThoughtDto | null {
  if (collaborator.role !== 'ai') return null
  const candidates = [collaborator.handle, collaborator.displayName, collaborator.clientId]
  for (const raw of candidates) {
    if (!raw) continue
    const key = raw.trim().toLowerCase().replace(/^@/, '').replace(/^ai:/, '')
    const hit = normalizedThoughtsByKey.value.get(key)
    if (hit) return hit
  }
  return null
}

function isWorkingStatus(status?: string | null): boolean {
  const s = String(status ?? '').trim().toLowerCase()
  return (
    s === 'working'
    || s === 'running'
    || s === 'in_progress'
    || s === 'planning'
    || s === 'executing'
    || s === 'active'
  )
}

interface CursorView {
  collaborator: SpatialCollaboratorLike
  screenX: number
  screenY: number
  handle: string
  runId: string
  status: string | null
  paused: boolean
  working: boolean
  thought: string
  taskSummary: string | null
}

const visibleCursors = computed<CursorView[]>(() => {
  const out: CursorView[] = []
  const zoom = props.viewport.zoom || 1
  for (const collaborator of remoteCollaborators.value) {
    const cx = collaborator.cursor?.x ?? collaborator.cursorX
    const cy = collaborator.cursor?.y ?? collaborator.cursorY
    if (typeof cx !== 'number' || typeof cy !== 'number' || !Number.isFinite(cx) || !Number.isFinite(cy)) {
      continue
    }
    const thoughtEntry = resolveThoughtEntry(collaborator)
    const handle = (collaborator.handle ?? thoughtEntry?.handle ?? collaborator.displayName).trim()
    const runId = (collaborator.runId ?? props.activeRunId ?? '').trim()
    const status = collaborator.paused
      ? 'paused'
      : (thoughtEntry?.status ?? collaborator.status ?? null)
    const paused = Boolean(collaborator.paused || String(status ?? '').toLowerCase() === 'paused')
    const rawThought = (collaborator.thought ?? thoughtEntry?.thought ?? '').replace(/\s+/g, ' ').trim()
    const taskSummary = (collaborator.currentTask ?? thoughtEntry?.current_task ?? '').trim() || null

    out.push({
      collaborator,
      screenX: cx * zoom + props.viewport.x,
      screenY: cy * zoom + props.viewport.y,
      handle,
      runId,
      status,
      paused,
      working: !paused && isWorkingStatus(status),
      thought: rawThought,
      taskSummary,
    })
  }
  return out
})

interface SelectionFrameView {
  key: string
  collaborator: SpatialCollaboratorLike
  left: number
  top: number
  width: number
  height: number
}

const selectionFrames = computed<SelectionFrameView[]>(() => {
  const out: SelectionFrameView[] = []
  const zoom = props.viewport.zoom || 1
  for (const collaborator of remoteCollaborators.value) {
    for (const objectId of collaborator.selection ?? []) {
      const rect = props.objectRects[objectId]
      if (!rect) continue
      out.push({
        key: `${collaborator.clientId}:${objectId}`,
        collaborator,
        left: rect.x * zoom + props.viewport.x,
        top: rect.y * zoom + props.viewport.y,
        width: rect.width * zoom,
        height: rect.height * zoom,
      })
    }
  }
  return out
})

function extractAnchorRect(event?: Event): TeammateSteerAnchorRect | null {
  const el = (event?.currentTarget as HTMLElement | null) ?? null
  const rect = el?.getBoundingClientRect?.()
  if (!rect || (rect.width === 0 && rect.height === 0)) return null
  return {
    top: rect.top,
    left: rect.left,
    width: rect.width,
    height: rect.height,
    right: rect.right,
    bottom: rect.bottom,
  }
}

function emitSteerForCursor(item: CursorView, event?: Event) {
  emit('steer-teammate', {
    collaborator: item.collaborator,
    runId: item.runId,
    handle: item.handle,
    displayName: item.collaborator.displayName,
    status: item.status,
    paused: item.paused,
    taskSummary: item.taskSummary,
    thought: item.thought || null,
    anchorRect: extractAnchorRect(event),
  })
}

function onCursorPillClick(item: CursorView, event: Event) {
  if (item.collaborator.role === 'ai') {
    emitSteerForCursor(item, event)
    return
  }
  emit('follow-user', item.collaborator.clientId)
}
</script>

<template>
  <div class="spatial-presence-layer" data-testid="spatial-presence-layer">
    <div
      v-for="frame in selectionFrames"
      :key="frame.key"
      class="spatial-remote-selection"
      :style="{
        transform: `translate3d(${frame.left.toFixed(1)}px, ${frame.top.toFixed(1)}px, 0)`,
        width: `${Math.max(12, frame.width).toFixed(1)}px`,
        height: `${Math.max(12, frame.height).toFixed(1)}px`,
        borderColor: frame.collaborator.color,
      }"
      :data-remote-selection="frame.key"
    >
      <span
        class="spatial-remote-selection-tag"
        :style="{ backgroundColor: frame.collaborator.color }"
      >
        {{ frame.collaborator.displayName }}
      </span>
    </div>

    <div
      v-for="item in visibleCursors"
      :key="item.collaborator.clientId"
      class="spatial-remote-cursor"
      :class="{
        'is-ai': item.collaborator.role === 'ai',
        'is-paused': item.paused,
      }"
      :style="{
        left: `${item.screenX.toFixed(1)}px`,
        top: `${item.screenY.toFixed(1)}px`,
      }"
      :data-cursor-client="item.collaborator.clientId"
    >
      <svg class="spatial-cursor-arrow" width="18" height="20" viewBox="0 0 18 20" fill="none" aria-hidden="true">
        <path
          d="M2.2 1.8L15.8 10.6L9.4 11.7L6.5 17.8L2.2 1.8Z"
          :fill="item.collaborator.color"
          stroke="var(--bg-primary)"
          stroke-width="1.4"
          stroke-linejoin="round"
        />
      </svg>

      <div class="spatial-cursor-stack">
        <button
          type="button"
          class="spatial-cursor-pill ui-pressable"
          :style="{ backgroundColor: item.collaborator.color }"
          :title="item.collaborator.role === 'ai'
            ? tr('space.liveDigest.thoughtBubbleHint', 'Click to steer or pause {name}', { name: item.collaborator.displayName })
            : t('space.whiteboard.followUser', { name: item.collaborator.displayName })"
          :data-testid="item.collaborator.role === 'ai' ? 'spatial-ai-cursor-pill' : 'spatial-cursor-pill'"
          @click.stop="onCursorPillClick(item, $event)"
        >
          <Bot v-if="item.collaborator.role === 'ai'" :size="11" aria-hidden="true" />
          <span class="spatial-cursor-name">{{ item.collaborator.displayName }}</span>
          <span v-if="item.paused" class="spatial-cursor-ai-tag">
            {{ tr('teammateControl.status.paused', 'Paused') }}
          </span>
          <span v-else-if="item.collaborator.role === 'ai'" class="spatial-cursor-ai-tag">
            {{ t('space.whiteboard.aiTeammateBadge') }}
          </span>
        </button>

        <!-- Translucent AI Teammate Thought Micro-Bubble -->
        <button
          v-if="item.collaborator.role === 'ai' && item.thought"
          type="button"
          class="spatial-cursor-thought-bubble"
          :class="{
            'is-working': item.working,
            'is-paused': item.paused,
          }"
          :title="`${item.thought} — ${tr('space.liveDigest.thoughtBubbleHint', 'Click to steer or pause {name}', { name: item.collaborator.displayName })}`"
          data-testid="spatial-thought-bubble"
          :data-thought-client="item.collaborator.clientId"
          :data-thought-handle="item.handle"
          @click.stop="emitSteerForCursor(item, $event)"
        >
          <span
            class="spatial-thought-dot"
            :class="{ 'is-working': item.working, 'is-paused': item.paused }"
            aria-hidden="true"
          />
          <span class="spatial-thought-text">{{ item.thought }}</span>
          <MessageSquare :size="11" class="spatial-thought-icon" aria-hidden="true" />
        </button>
      </div>
    </div>

    <div
      v-if="followedCollaborator"
      class="spatial-follow-hud"
      role="status"
      data-testid="spatial-following-banner"
      :style="{ '--follow-color': followedCollaborator.color }"
    >
      <span class="spatial-follow-dot" />
      <Eye :size="13" aria-hidden="true" />
      <span class="spatial-follow-text">{{ followingLabel }}</span>
      <button
        type="button"
        class="spatial-follow-stop"
        data-testid="spatial-stop-follow-btn"
        @click.stop="emit('stop-follow')"
      >
        <X :size="12" aria-hidden="true" />
        <span>{{ t('space.whiteboard.stopFollow') }}</span>
      </button>
    </div>
  </div>
</template>

<style scoped>
.spatial-presence-layer {
  position: absolute;
  inset: 0;
  pointer-events: none;
  z-index: 8;
  overflow: hidden;
}

.spatial-remote-selection {
  position: absolute;
  top: 0;
  left: 0;
  border-radius: 12px;
  border: 1.5px solid;
  box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.18);
  pointer-events: none;
  transition: transform 60ms linear, width 60ms linear, height 60ms linear;
}

.spatial-remote-selection-tag {
  position: absolute;
  top: -19px;
  left: 4px;
  padding: 1px 6px;
  border-radius: 999px;
  color: rgba(250, 250, 250, 0.96);
  font-size: 10px;
  font-weight: 600;
  line-height: 15px;
  white-space: nowrap;
  box-shadow: 0 2px 6px rgba(0, 0, 0, 0.16);
}

.spatial-remote-cursor {
  position: absolute;
  top: 0;
  left: 0;
  display: inline-flex;
  align-items: flex-start;
  pointer-events: none;
  transition: left 80ms linear, top 80ms linear;
}

.spatial-cursor-arrow {
  display: block;
  filter: drop-shadow(0 2px 4px rgba(0, 0, 0, 0.24));
  flex-shrink: 0;
}

.spatial-cursor-stack {
  display: inline-flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 4px;
  margin-top: 12px;
  margin-left: -3px;
}

.spatial-cursor-pill {
  pointer-events: auto;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border: 1px solid rgba(255, 255, 255, 0.28);
  border-radius: 999px;
  color: rgba(250, 250, 250, 0.96);
  font-size: 11px;
  font-weight: 600;
  line-height: 16px;
  white-space: nowrap;
  cursor: pointer;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.2);
}

.spatial-cursor-ai-tag {
  padding: 0 4px;
  border-radius: 999px;
  background: rgba(0, 0, 0, 0.22);
  font-size: 9.5px;
  font-weight: 700;
  letter-spacing: 0.02em;
}

/* Light translucent AI Thought Micro-Bubble */
.spatial-cursor-thought-bubble {
  pointer-events: auto;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  max-width: 244px;
  padding: 4px 9px;
  border-radius: 999px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-primary);
  font-family: inherit;
  font-size: 11px;
  font-weight: 500;
  line-height: 1.35;
  cursor: pointer;
  box-shadow: var(--shadow-panel, 0 4px 14px rgba(0, 0, 0, 0.16));
  animation: spatial-thought-fade var(--duration-pop, 180ms) var(--ease-snappy, ease) both;
  transition:
    border-color var(--duration-tap, 120ms) var(--ease-snappy, ease),
    background-color var(--duration-tap, 120ms) var(--ease-snappy, ease),
    box-shadow var(--duration-tap, 120ms) var(--ease-snappy, ease);
}

.spatial-cursor-thought-bubble:hover {
  border-color: var(--accent-primary);
  box-shadow: var(--shadow-elevated, 0 6px 18px rgba(0, 0, 0, 0.22));
}

.spatial-cursor-thought-bubble.is-paused {
  border-color: color-mix(in srgb, var(--accent-warning) 44%, var(--border-default));
}

.spatial-thought-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  flex-shrink: 0;
  background: var(--text-muted);
}

.spatial-thought-dot.is-working {
  background: var(--accent-primary);
  animation: spatial-thought-pulse 1.5s ease-in-out infinite;
}

.spatial-thought-dot.is-paused {
  background: var(--accent-warning);
}

.spatial-thought-text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.spatial-thought-icon {
  flex-shrink: 0;
  color: var(--text-muted);
  opacity: 0.72;
  transition: opacity var(--duration-tap, 120ms) var(--ease-snappy, ease), color var(--duration-tap, 120ms) var(--ease-snappy, ease);
}

.spatial-cursor-thought-bubble:hover .spatial-thought-icon {
  opacity: 1;
  color: var(--text-primary);
}

@keyframes spatial-thought-fade {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}

@keyframes spatial-thought-pulse {
  0%, 100% {
    opacity: 1;
  }
  50% {
    opacity: 0.35;
  }
}

.spatial-follow-hud {
  position: absolute;
  top: 16px;
  left: 0;
  right: 0;
  margin-inline: auto;
  width: fit-content;
  pointer-events: auto;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 5px 8px 5px 12px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--follow-color, var(--accent-primary)) 52%, var(--material-floating-border, var(--border-muted)));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 600;
  box-shadow: 0 8px 24px rgba(15, 23, 42, 0.16);
}

.spatial-follow-dot {
  width: 8px;
  height: 8px;
  border-radius: 999px;
  background: var(--follow-color, var(--accent-primary));
  flex-shrink: 0;
}

.spatial-follow-text {
  white-space: nowrap;
}

.spatial-follow-stop {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 11.5px;
  font-weight: 600;
  cursor: pointer;
}

.spatial-follow-stop:hover {
  background: var(--surface-hover, var(--bg-hover));
}

@media (prefers-reduced-motion: reduce) {
  .spatial-cursor-thought-bubble,
  .spatial-thought-dot.is-working {
    animation: none;
  }
}
</style>
