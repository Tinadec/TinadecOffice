<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Activity,
  AlertTriangle,
  ChevronDown,
  ChevronRight,
  FileCode2,
  MessageSquare,
  RefreshCw,
  ShieldAlert,
  Sparkles,
  Wrench,
} from '@lucide/vue'
import {
  useLiveDigest,
  type LiveDigestChapterDto,
  type LiveDigestSentinelAlertDto,
  type LiveDigestTeammateThoughtDto,
} from '@/composables/useLiveDigest'
import { normalizeTeammateStatusKind } from '@/lib/teammateControlApi'
import type { TeammateSteerAnchorRect, TeammateSteerTarget } from '@/components/teammate/TeammateSteerPopover.vue'

export interface SpatialDigestFillSteerEvent {
  prompt: string
  teammateHandle?: string | null
}

const props = withDefaults(defineProps<{
  runId: string | null
  storageId?: string
  pausedOverrides?: Record<string, boolean>
}>(), {
  storageId: undefined,
  pausedOverrides: () => ({}),
})

const emit = defineEmits<{
  'steer-teammate': [target: TeammateSteerTarget & { initialDraft?: string }]
  'fill-steer': [payload: SpatialDigestFillSteerEvent]
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? t(key, params) : t(key)
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

const digest = useLiveDigest(() => props.runId, {
  storageId: () => props.storageId,
})

const expandedChapters = ref<Set<string>>(new Set())

watch(
  () => props.runId,
  () => {
    expandedChapters.value = new Set()
  },
)

function toggleChapter(id: string) {
  const next = new Set(expandedChapters.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  expandedChapters.value = next
}

function phaseLabel(phase: string | null | undefined): string {
  const clean = String(phase ?? '').trim().toLowerCase()
  if (clean === 'hypothesis') return tr('space.liveDigest.phase.hypothesis', 'Hypothesis')
  if (clean === 'verification') return tr('space.liveDigest.phase.verification', 'Verification')
  if (clean === 'conclusion') return tr('space.liveDigest.phase.conclusion', 'Conclusion')
  return clean || tr('space.liveDigest.phase.hypothesis', 'Hypothesis')
}

function formatCompactTokens(tokens: number): string {
  const safe = Math.max(0, Math.round(tokens))
  if (safe >= 1_000_000) return `${(safe / 1_000_000).toFixed(1)}M`
  if (safe >= 1_000) return `${(safe / 1_000).toFixed(1)}k`
  return String(safe)
}

const waterlinePercent = computed(() => {
  const wl = digest.contextWaterline.value
  if (!wl) return 0
  return Math.max(0, Math.min(100, Math.round(wl.usage_percent)))
})

const waterlineTone = computed<'normal' | 'warning' | 'critical'>(() => {
  if (waterlinePercent.value >= 90) return 'critical'
  if (waterlinePercent.value >= 75) return 'warning'
  return 'normal'
})

const hasContent = computed(() =>
  Boolean(
    digest.headline.value
    || digest.chapters.value.length > 0
    || digest.teammateThoughts.value.length > 0
    || digest.sentinelAlerts.value.length > 0
    || digest.governanceSignals.value.length > 0
    || (digest.contextWaterline.value && digest.contextWaterline.value.max_tokens > 0),
  ),
)

function isTeammatePaused(tm: LiveDigestTeammateThoughtDto): boolean {
  const override = props.pausedOverrides[tm.handle]
  if (override !== undefined) return Boolean(override)
  return String(tm.status ?? '').toLowerCase() === 'paused'
}

function teammateStatusKind(tm: LiveDigestTeammateThoughtDto) {
  return normalizeTeammateStatusKind(tm.status, isTeammatePaused(tm))
}

function teammateStatusLabel(tm: LiveDigestTeammateThoughtDto): string {
  const kind = teammateStatusKind(tm)
  const fallbackMap = {
    running: 'Running',
    paused: 'Paused',
    idle: 'Sleeping',
    awaiting_approval: 'Awaiting approval',
    completed: 'Completed',
    failed: 'Failed',
    queued: 'Queued',
  } as const
  return tr(`teammateControl.status.${kind}`, fallbackMap[kind])
}

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

function onTeammateClick(tm: LiveDigestTeammateThoughtDto, event?: Event) {
  if (!props.runId) return
  emit('steer-teammate', {
    runId: props.runId,
    handle: tm.handle,
    displayName: tm.display_name || tm.handle,
    status: isTeammatePaused(tm) ? 'paused' : tm.status,
    paused: isTeammatePaused(tm),
    taskSummary: tm.current_task ?? null,
    thought: tm.thought || null,
    anchorRect: extractAnchorRect(event),
  })
}

function onAlertFillSteer(alert: LiveDigestSentinelAlertDto) {
  const prompt = (alert.suggested_steer_prompt || alert.message || '').trim()
  if (!prompt) return
  emit('fill-steer', {
    prompt,
    teammateHandle: alert.teammate_handle ?? null,
  })
}

function onAlertSteerTeammate(alert: LiveDigestSentinelAlertDto, event?: Event) {
  if (!props.runId || !alert.teammate_handle) return
  const matched = digest.teammateThoughtByHandle.value[alert.teammate_handle]
  emit('steer-teammate', {
    runId: props.runId,
    handle: alert.teammate_handle,
    displayName: matched?.display_name || alert.teammate_handle,
    status: matched?.status ?? 'working',
    paused: matched ? isTeammatePaused(matched) : false,
    taskSummary: matched?.current_task ?? alert.title,
    thought: matched?.thought ?? alert.message,
    initialDraft: alert.suggested_steer_prompt || '',
    anchorRect: extractAnchorRect(event),
  })
}

function hasChapterEvidence(chapter: LiveDigestChapterDto): boolean {
  return Boolean(
    chapter.raw_thought_excerpt?.trim()
    || chapter.scout_note?.trim()
    || (chapter.tool_calls && chapter.tool_calls.length > 0),
  )
}

function shortFileName(path: string): string {
  const normalized = path.replace(/\\/g, '/')
  const parts = normalized.split('/').filter(Boolean)
  if (parts.length <= 2) return normalized
  return `${parts[parts.length - 2]}/${parts[parts.length - 1]}`
}
</script>

<template>
  <div class="spatial-digest-inspector" data-testid="space-live-digest-panel">
    <!-- Top Live Status + Headline -->
    <section class="spatial-digest__hero">
      <div class="spatial-digest__hero-meta">
        <span
          class="spatial-digest__live-dot"
          :class="{
            'is-running': digest.isRunning.value,
            'is-terminal': digest.isTerminal.value,
          }"
          aria-hidden="true"
        />
        <span class="spatial-digest__phase-pill">
          {{ phaseLabel(digest.currentPhase.value) }}
        </span>
        <span v-if="digest.explainerActive.value && digest.explainerModel.value" class="spatial-digest__model-pill">
          {{ digest.explainerModel.value }}
        </span>
        <span v-else class="spatial-digest__model-pill">L0</span>
        <button
          type="button"
          class="spatial-digest__refresh-btn ui-pressable"
          :disabled="!runId || digest.loading.value"
          :aria-label="tr('common.refresh', 'Refresh')"
          :title="tr('common.refresh', 'Refresh')"
          data-testid="spatial-digest-refresh"
          @click="digest.refresh()"
        >
          <RefreshCw :size="13" :class="{ 'is-spinning': digest.loading.value }" aria-hidden="true" />
        </button>
      </div>

      <p
        v-if="digest.headline.value"
        class="spatial-digest__headline"
        data-testid="spatial-digest-headline"
      >
        {{ digest.headline.value }}
      </p>
      <p v-else class="spatial-digest__empty-inline" data-testid="spatial-digest-empty">
        <Activity :size="13" aria-hidden="true" />
        <span>{{ tr('space.liveDigest.empty', 'Waiting for live digest stream…') }}</span>
      </p>
    </section>

    <!-- Context Waterline Progress Bar -->
    <section
      v-if="digest.contextWaterline.value && (digest.contextWaterline.value.max_tokens > 0 || digest.contextWaterline.value.usage_percent > 0)"
      class="spatial-digest__section"
      data-testid="spatial-digest-waterline"
    >
      <div class="spatial-digest__waterline-head">
        <span class="spatial-digest__section-label">
          {{ tr('space.liveDigest.contextBadge', 'Ctx {percent}%', { percent: waterlinePercent }) }}
        </span>
        <span class="spatial-digest__waterline-tokens">
          {{ formatCompactTokens(digest.contextWaterline.value.used_tokens) }}
          /
          {{ formatCompactTokens(digest.contextWaterline.value.max_tokens) }}
        </span>
        <span
          v-if="digest.contextWaterline.value.compaction_count > 0"
          class="spatial-digest__compaction-pill"
          data-testid="spatial-digest-compaction"
        >
          {{ tr('space.liveDigest.compactionCount', '{count} compacted', { count: digest.contextWaterline.value.compaction_count }) }}
        </span>
      </div>
      <div class="spatial-digest__waterline-track" :class="`is-${waterlineTone}`">
        <div
          class="spatial-digest__waterline-fill"
          :style="{ width: `${waterlinePercent}%` }"
        />
      </div>
    </section>

    <!-- Governance Signals -->
    <section
      v-if="digest.governanceSignals.value.length > 0"
      class="spatial-digest__section"
      data-testid="spatial-digest-signals"
    >
      <div class="spatial-digest__section-head">
        <ShieldAlert :size="13" aria-hidden="true" />
        <span class="spatial-digest__section-label">
          {{ tr('space.liveDigest.signalsTitle', 'Governance signals') }}
        </span>
      </div>
      <div class="spatial-digest__signal-list">
        <span
          v-for="sig in digest.governanceSignals.value"
          :key="sig.id"
          class="spatial-digest__signal-chip"
          :class="`is-${sig.severity}`"
          :title="sig.summary"
        >
          {{ sig.code }}
        </span>
      </div>
    </section>

    <!-- Sentinel Alerts with 1-click Fill Steer / Direct Teammate Steer -->
    <section
      v-if="digest.sentinelAlerts.value.length > 0"
      class="spatial-digest__section"
      data-testid="spatial-digest-alerts"
    >
      <div class="spatial-digest__section-head">
        <AlertTriangle :size="13" aria-hidden="true" />
        <span class="spatial-digest__section-label">
          {{ tr('space.liveDigest.alertsTitle', 'Sentinel alerts') }}
        </span>
      </div>
      <div class="spatial-digest__alert-list">
        <article
          v-for="alert in digest.sentinelAlerts.value"
          :key="alert.id"
          class="spatial-digest__alert-card"
          :class="`is-${alert.severity}`"
          data-testid="spatial-digest-alert-card"
        >
          <div class="spatial-digest__alert-title">{{ alert.title }}</div>
          <p class="spatial-digest__alert-msg">{{ alert.message }}</p>
          <div class="spatial-digest__alert-actions">
            <button
              v-if="alert.suggested_steer_prompt"
              type="button"
              class="spatial-digest__primary-btn ui-pressable"
              data-testid="spatial-digest-fill-steer"
              @click="onAlertFillSteer(alert)"
            >
              <Sparkles :size="13" aria-hidden="true" />
              <span>{{ tr('space.liveDigest.fillSteer', 'Fill steer prompt') }}</span>
            </button>
            <button
              v-if="alert.teammate_handle && runId"
              type="button"
              class="spatial-digest__secondary-btn ui-pressable"
              data-testid="spatial-digest-alert-steer"
              @click="onAlertSteerTeammate(alert, $event)"
            >
              <MessageSquare :size="13" aria-hidden="true" />
              <span>@{{ alert.teammate_handle }}</span>
            </button>
          </div>
        </article>
      </div>
    </section>

    <!-- Active Teammates Current Thought Strip -->
    <section
      v-if="digest.teammateThoughts.value.length > 0"
      class="spatial-digest__section"
      data-testid="spatial-digest-teammates"
    >
      <div class="spatial-digest__section-head">
        <MessageSquare :size="13" aria-hidden="true" />
        <span class="spatial-digest__section-label">
          {{ tr('space.liveDigest.teammatesTitle', 'Active teammates') }}
        </span>
      </div>
      <div class="spatial-digest__teammate-list">
        <button
          v-for="tm in digest.teammateThoughts.value"
          :key="tm.handle"
          type="button"
          class="spatial-digest__teammate-row ui-pressable"
          :class="`is-${teammateStatusKind(tm)}`"
          :title="tr('teammateControl.triggerTooltip', 'Steer or pause {name}', { name: tm.display_name || tm.handle })"
          data-testid="spatial-digest-teammate"
          :data-teammate-handle="tm.handle"
          @click="onTeammateClick(tm, $event)"
        >
          <div class="spatial-digest__teammate-top">
            <span class="spatial-digest__tm-dot" :class="`is-${teammateStatusKind(tm)}`" aria-hidden="true" />
            <strong class="spatial-digest__tm-name">{{ tm.display_name || tm.handle }}</strong>
            <span class="spatial-digest__tm-status">{{ teammateStatusLabel(tm) }}</span>
            <MessageSquare :size="13" class="spatial-digest__tm-icon" aria-hidden="true" />
          </div>
          <p v-if="tm.thought || tm.current_task" class="spatial-digest__tm-thought">
            {{ tm.thought || tm.current_task }}
          </p>
        </button>
      </div>
    </section>

    <!-- Phase Chapters Timeline -->
    <section
      v-if="digest.chapters.value.length > 0"
      class="spatial-digest__section"
      data-testid="spatial-digest-chapters"
    >
      <div class="spatial-digest__section-head">
        <span class="spatial-digest__section-label">
          {{ tr('space.liveDigest.chaptersTitle', 'Phase chapters') }}
        </span>
        <span class="spatial-digest__count-pill">{{ digest.chapters.value.length }}</span>
      </div>
      <div class="spatial-digest__chapter-list">
        <article
          v-for="chapter in digest.chapters.value"
          :key="chapter.id"
          class="spatial-digest__chapter-card"
          data-testid="spatial-digest-chapter"
        >
          <button
            type="button"
            class="spatial-digest__chapter-head"
            :aria-expanded="expandedChapters.has(chapter.id)"
            @click="hasChapterEvidence(chapter) && toggleChapter(chapter.id)"
          >
            <span v-if="hasChapterEvidence(chapter)" class="spatial-digest__chapter-chevron" aria-hidden="true">
              <ChevronDown v-if="expandedChapters.has(chapter.id)" :size="13" />
              <ChevronRight v-else :size="13" />
            </span>
            <span class="spatial-digest__chapter-phase">{{ phaseLabel(chapter.phase) }}</span>
            <strong class="spatial-digest__chapter-title">{{ chapter.title }}</strong>
          </button>

          <p v-if="chapter.narrative" class="spatial-digest__chapter-narrative">
            {{ chapter.narrative }}
          </p>

          <div v-if="chapter.touched_files.length > 0" class="spatial-digest__files">
            <span
              v-for="file in chapter.touched_files.slice(0, 6)"
              :key="file.path"
              class="spatial-digest__file-chip"
              :title="file.path"
            >
              <FileCode2 :size="11" aria-hidden="true" />
              <span class="spatial-digest__file-name">{{ shortFileName(file.path) }}</span>
              <span v-if="file.added_lines > 0" class="spatial-digest__diff-add">+{{ file.added_lines }}</span>
              <span v-if="file.removed_lines > 0" class="spatial-digest__diff-del">-{{ file.removed_lines }}</span>
            </span>
          </div>

          <div
            v-if="expandedChapters.has(chapter.id) && hasChapterEvidence(chapter)"
            class="spatial-digest__evidence"
            data-testid="spatial-digest-chapter-evidence"
          >
            <p v-if="chapter.scout_note" class="spatial-digest__scout-note">
              {{ chapter.scout_note }}
            </p>
            <p v-if="chapter.raw_thought_excerpt" class="spatial-digest__thought-excerpt">
              {{ chapter.raw_thought_excerpt }}
            </p>
            <div v-if="chapter.tool_calls.length > 0" class="spatial-digest__tool-list">
              <div
                v-for="tool in chapter.tool_calls"
                :key="tool.tool_call_id"
                class="spatial-digest__tool-row"
              >
                <Wrench :size="11" aria-hidden="true" />
                <span class="spatial-digest__tool-name">{{ tool.tool_name }}</span>
                <span class="spatial-digest__tool-summary">{{ tool.compact_summary }}</span>
              </div>
            </div>
          </div>
        </article>
      </div>
    </section>

    <p v-if="digest.error.value && !hasContent" class="spatial-digest__error" role="alert">
      {{ digest.error.value }}
    </p>
  </div>
</template>

<style scoped>
.spatial-digest-inspector {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 12px;
  color: var(--text-primary);
  min-width: 0;
}

.spatial-digest__hero {
  display: flex;
  flex-direction: column;
  gap: 7px;
  padding: 10px 11px;
  border-radius: var(--radius-card, 10px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
}

.spatial-digest__hero-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.spatial-digest__live-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
  background: var(--text-muted);
}

.spatial-digest__live-dot.is-running {
  background: var(--accent-primary);
  animation: spatial-digest-pulse 1.6s ease-in-out infinite;
}

.spatial-digest__live-dot.is-terminal {
  background: var(--accent-success);
}

.spatial-digest__phase-pill,
.spatial-digest__model-pill,
.spatial-digest__count-pill,
.spatial-digest__compaction-pill {
  display: inline-flex;
  align-items: center;
  height: 19px;
  padding: 0 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-overlay));
  color: var(--text-secondary);
  font-size: 10.5px;
  font-weight: 600;
  line-height: 1;
  white-space: nowrap;
}

.spatial-digest__model-pill {
  font-family: var(--font-mono);
  font-weight: 500;
  max-width: 150px;
  overflow: hidden;
  text-overflow: ellipsis;
}

.spatial-digest__refresh-btn {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border: 0;
  border-radius: var(--radius-control, 6px);
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.spatial-digest__refresh-btn:hover:not(:disabled) {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.spatial-digest__refresh-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.spatial-digest__headline {
  margin: 0;
  font-size: 12.5px;
  font-weight: 600;
  line-height: 1.45;
  color: var(--text-primary);
}

.spatial-digest__empty-inline {
  margin: 0;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-muted);
}

.spatial-digest__section {
  display: flex;
  flex-direction: column;
  gap: 7px;
  min-width: 0;
}

.spatial-digest__section-head,
.spatial-digest__waterline-head {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--text-secondary);
}

.spatial-digest__section-label {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.02em;
  color: var(--text-secondary);
}

.spatial-digest__waterline-tokens {
  margin-left: auto;
  font-size: 11px;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  color: var(--text-muted);
}

.spatial-digest__waterline-track {
  position: relative;
  width: 100%;
  height: 5px;
  border-radius: 999px;
  background: var(--border-default);
  overflow: hidden;
  color: var(--accent-primary);
}

.spatial-digest__waterline-track.is-warning {
  color: var(--accent-warning);
}

.spatial-digest__waterline-track.is-critical {
  color: var(--accent-danger);
}

.spatial-digest__waterline-fill {
  height: 100%;
  border-radius: 999px;
  background: currentColor;
  transition: width var(--duration-panel, 240ms) var(--ease-snappy, ease);
}

.spatial-digest__signal-list {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.spatial-digest__signal-chip {
  display: inline-flex;
  align-items: center;
  height: 20px;
  padding: 0 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 10px;
  font-family: var(--font-mono);
  font-weight: 600;
}

.spatial-digest__signal-chip.is-warning {
  border-color: color-mix(in srgb, var(--accent-warning) 40%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-warning) 10%, transparent);
  color: var(--accent-warning);
}

.spatial-digest__signal-chip.is-critical {
  border-color: color-mix(in srgb, var(--accent-danger) 42%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-danger) 10%, transparent);
  color: var(--accent-danger);
}

.spatial-digest__alert-list,
.spatial-digest__teammate-list,
.spatial-digest__chapter-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.spatial-digest__alert-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 9px 10px;
  border-radius: var(--radius-card, 10px);
  border: 1px solid color-mix(in srgb, var(--accent-warning) 36%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-warning) 8%, var(--surface-section, var(--bg-secondary)));
}

.spatial-digest__alert-card.is-critical {
  border-color: color-mix(in srgb, var(--accent-danger) 40%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-danger) 8%, var(--surface-section, var(--bg-secondary)));
}

.spatial-digest__alert-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.spatial-digest__alert-msg {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.4;
  color: var(--text-secondary);
}

.spatial-digest__alert-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.spatial-digest__primary-btn,
.spatial-digest__secondary-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 26px;
  padding: 0 9px;
  border-radius: var(--radius-control, 6px);
  font-size: 11.5px;
  font-weight: 600;
  line-height: 1;
  cursor: pointer;
}

.spatial-digest__primary-btn {
  border: 1px solid transparent;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary);
}

.spatial-digest__primary-btn:hover {
  background: var(--bg-primary-button-hover, var(--accent-primary));
}

.spatial-digest__secondary-btn {
  border: 1px solid var(--border-default);
  background: var(--surface-raised, var(--bg-overlay));
  color: var(--text-primary);
}

.spatial-digest__secondary-btn:hover {
  background: var(--bg-hover);
}

.spatial-digest__teammate-row {
  display: flex;
  flex-direction: column;
  gap: 4px;
  width: 100%;
  padding: 8px 10px;
  border-radius: var(--radius-card, 10px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-primary);
  text-align: left;
  font-family: inherit;
  cursor: pointer;
}

.spatial-digest__teammate-row:hover {
  border-color: var(--border-default);
  background: var(--bg-hover);
}

.spatial-digest__teammate-top {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.spatial-digest__tm-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  flex-shrink: 0;
  background: var(--text-muted);
}

.spatial-digest__tm-dot.is-running {
  background: var(--accent-primary);
}

.spatial-digest__tm-dot.is-paused,
.spatial-digest__tm-dot.is-awaiting_approval {
  background: var(--accent-warning);
}

.spatial-digest__tm-dot.is-completed {
  background: var(--accent-success);
}

.spatial-digest__tm-dot.is-failed {
  background: var(--accent-danger);
}

.spatial-digest__tm-name {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.spatial-digest__tm-status {
  font-size: 10.5px;
  color: var(--text-muted);
  white-space: nowrap;
}

.spatial-digest__tm-icon {
  margin-left: auto;
  flex-shrink: 0;
  color: var(--text-muted);
}

.spatial-digest__tm-thought {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.38;
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.spatial-digest__chapter-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 10px;
  border-radius: var(--radius-card, 10px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
}

.spatial-digest__chapter-head {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font-family: inherit;
  text-align: left;
  cursor: pointer;
}

.spatial-digest__chapter-chevron {
  display: inline-flex;
  align-items: center;
  color: var(--text-muted);
  flex-shrink: 0;
}

.spatial-digest__chapter-phase {
  display: inline-flex;
  align-items: center;
  height: 18px;
  padding: 0 6px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-overlay));
  color: var(--text-secondary);
  font-size: 10px;
  font-weight: 600;
  flex-shrink: 0;
}

.spatial-digest__chapter-title {
  font-size: 12px;
  font-weight: 600;
  line-height: 1.35;
  color: var(--text-primary);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.spatial-digest__chapter-narrative {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.45;
  color: var(--text-secondary);
}

.spatial-digest__files {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.spatial-digest__file-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  padding: 2px 6px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-overlay));
  font-family: var(--font-mono);
  font-size: 10px;
  color: var(--text-secondary);
}

.spatial-digest__file-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 150px;
}

.spatial-digest__diff-add {
  color: var(--accent-success);
  font-weight: 600;
}

.spatial-digest__diff-del {
  color: var(--accent-danger);
  font-weight: 600;
}

.spatial-digest__evidence {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding-top: 6px;
  border-top: 1px solid var(--border-muted);
}

.spatial-digest__scout-note,
.spatial-digest__thought-excerpt {
  margin: 0;
  font-size: 11px;
  line-height: 1.42;
  color: var(--text-secondary);
}

.spatial-digest__thought-excerpt {
  font-family: var(--font-mono);
  color: var(--text-muted);
  white-space: pre-wrap;
  word-break: break-word;
}

.spatial-digest__tool-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.spatial-digest__tool-row {
  display: flex;
  align-items: center;
  gap: 5px;
  font-size: 11px;
  color: var(--text-secondary);
  min-width: 0;
}

.spatial-digest__tool-name {
  font-family: var(--font-mono);
  font-weight: 600;
  color: var(--text-primary);
  flex-shrink: 0;
}

.spatial-digest__tool-summary {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-muted);
}

.spatial-digest__error {
  margin: 0;
  padding: 8px 10px;
  border-radius: var(--radius-control-md, 8px);
  background: color-mix(in srgb, var(--accent-danger) 10%, transparent);
  color: var(--accent-danger);
  font-size: 11.5px;
}

.is-spinning {
  animation: spatial-digest-spin 0.85s linear infinite;
}

@keyframes spatial-digest-pulse {
  0%, 100% {
    opacity: 1;
  }
  50% {
    opacity: 0.36;
  }
}

@keyframes spatial-digest-spin {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}

@media (prefers-reduced-motion: reduce) {
  .spatial-digest__live-dot.is-running,
  .is-spinning {
    animation: none;
  }
}
</style>
