<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowUpRight,
  Brain,
  CheckCircle2,
  ChevronDown,
  FileCode2,
  Layers3,
  Loader2,
  Search,
  Sparkles,
  Wrench,
  XCircle,
} from '@lucide/vue'
import type { LiveDigestChapterDto } from '@/lib/liveDigestApi'

const props = withDefaults(defineProps<{
  chapter: LiveDigestChapterDto
  defaultExpanded?: boolean
  compact?: boolean
}>(), {
  defaultExpanded: false,
  compact: false,
})

const emit = defineEmits<{
  'open-orchestration': [chapter: LiveDigestChapterDto]
  'open-trajectory': [chapter: LiveDigestChapterDto]
}>()

const { t, te, locale } = useI18n()
const expanded = ref(props.defaultExpanded)

const isZh = computed(() => String(locale.value || '').toLowerCase().startsWith('zh'))

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? String(t(key, params)) : String(t(key))
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

const jumpTrajectoryLabel = computed(() =>
  tr(
    'liveDigest.actions.viewInTrajectory',
    isZh.value ? '在轨迹中查看 ↗' : 'View in trajectory ↗',
  ),
)

const phaseMeta = computed(() => {
  const phase = String(props.chapter.phase ?? '').toLowerCase()
  if (phase === 'verification') {
    return {
      cls: 'phase-verification',
      label: t('liveDigest.phase.verification'),
    }
  }
  if (phase === 'conclusion') {
    return {
      cls: 'phase-conclusion',
      label: t('liveDigest.phase.conclusion'),
    }
  }
  return {
    cls: 'phase-hypothesis',
    label: t('liveDigest.phase.hypothesis'),
  }
})

const sourceLabel = computed(() => {
  const source = String(props.chapter.source ?? '').toLowerCase()
  if (source === 'l2_scout') return t('liveDigest.source.l2Scout')
  if (source === 'l1_narrator') return t('liveDigest.source.l1Narrator')
  return t('liveDigest.source.l0Structural')
})

const hasEvidence = computed(() =>
  Boolean(
    props.chapter.raw_thought_excerpt
    || props.chapter.scout_note
    || props.chapter.tool_calls.length > 0,
  ),
)

const formattedIndex = computed(() => String(props.chapter.index).padStart(2, '0'))

function shortFileName(filePath: string): string {
  const parts = filePath.replace(/\\/g, '/').split('/').filter(Boolean)
  if (parts.length <= 2) return filePath
  return `${parts[parts.length - 2]}/${parts[parts.length - 1]}`
}

function toggleExpanded(): void {
  expanded.value = !expanded.value
}
</script>

<template>
  <article
    class="ld-chapter"
    :class="{ 'is-expanded': expanded, 'is-compact': compact }"
    :data-chapter-id="chapter.id"
    :data-phase="chapter.phase"
    data-testid="live-digest-chapter"
  >
    <!-- 1.5px neutral vertical timeline spine + stage dot -->
    <div class="ld-chapter-rail" aria-hidden="true">
      <span class="ld-chapter-dot" :class="phaseMeta.cls" />
      <span class="ld-chapter-spine" />
    </div>

    <div class="ld-chapter-main">
      <!-- Top compact metadata + 1-click trajectory deep-link -->
      <div class="ld-chapter-meta-row">
        <span class="ld-chapter-index">#{{ formattedIndex }}</span>
        <span class="ld-phase-pill" :class="phaseMeta.cls">{{ phaseMeta.label }}</span>
        <span class="ld-source-tag">{{ sourceLabel }}</span>
        <button
          type="button"
          class="ld-trajectory-link ui-pressable"
          data-testid="live-digest-jump-trajectory"
          @click="emit('open-trajectory', chapter)"
        >
          <span>{{ jumpTrajectoryLabel }}</span>
        </button>
      </div>

      <!-- Dedicated full-width chapter title button (never crushed by index/source badges) -->
      <button
        type="button"
        class="ld-chapter-head ui-pressable"
        :aria-expanded="expanded"
        data-testid="live-digest-chapter-toggle"
        @click="toggleExpanded"
      >
        <span class="ld-chapter-title" :title="chapter.title">{{ chapter.title }}</span>
        <ChevronDown
          :size="14"
          class="ld-chapter-chevron"
          :class="{ 'is-open': expanded }"
          aria-hidden="true"
        />
      </button>

      <p v-if="chapter.narrative" class="ld-chapter-narrative">
        {{ chapter.narrative }}
      </p>

      <div
        v-if="chapter.touched_files.length > 0"
        class="ld-file-chips"
        data-testid="live-digest-chapter-files"
      >
        <span
          v-for="file in chapter.touched_files"
          :key="file.path"
          class="ld-file-chip"
          :title="file.path"
        >
          <FileCode2 :size="12" class="ld-file-chip-icon" aria-hidden="true" />
          <span class="ld-file-chip-name">{{ shortFileName(file.path) }}</span>
          <span v-if="file.added_lines > 0" class="ld-diff-add">+{{ file.added_lines }}</span>
          <span v-if="file.removed_lines > 0" class="ld-diff-del">-{{ file.removed_lines }}</span>
        </span>
      </div>

      <div
        class="ui-collapse ld-chapter-collapse"
        :class="{ 'is-open': expanded }"
        :aria-hidden="!expanded"
      >
        <div class="ui-collapse-inner">
          <div class="ld-evidence-body" data-testid="live-digest-chapter-evidence">
            <div v-if="chapter.scout_note" class="ld-evidence-block" data-testid="live-digest-scout-note">
              <div class="ld-evidence-label">
                <Search :size="12" aria-hidden="true" />
                <span>{{ t('liveDigest.chapter.scoutNote') }}</span>
              </div>
              <p class="ld-scout-text">{{ chapter.scout_note }}</p>
            </div>

            <div
              v-if="chapter.raw_thought_excerpt"
              class="ld-evidence-block"
              data-testid="live-digest-raw-thought"
            >
              <div class="ld-evidence-label">
                <Brain :size="12" aria-hidden="true" />
                <span>{{ t('liveDigest.chapter.rawThought') }}</span>
              </div>
              <pre class="ld-thought-pre">{{ chapter.raw_thought_excerpt }}</pre>
            </div>

            <div
              v-if="chapter.tool_calls.length > 0"
              class="ld-evidence-block"
              data-testid="live-digest-tool-calls"
            >
              <div class="ld-evidence-label">
                <Wrench :size="12" aria-hidden="true" />
                <span>{{ t('liveDigest.chapter.toolEvidence', { count: chapter.tool_calls.length }) }}</span>
              </div>
              <ul class="ld-tool-list">
                <li
                  v-for="call in chapter.tool_calls"
                  :key="call.tool_call_id"
                  class="ld-tool-row"
                  :data-tool-status="call.status"
                >
                  <Loader2
                    v-if="call.status === 'running'"
                    :size="12"
                    class="ld-tool-icon is-running"
                    aria-hidden="true"
                  />
                  <XCircle
                    v-else-if="call.status === 'failed'"
                    :size="12"
                    class="ld-tool-icon is-failed"
                    aria-hidden="true"
                  />
                  <CheckCircle2
                    v-else
                    :size="12"
                    class="ld-tool-icon is-ok"
                    aria-hidden="true"
                  />
                  <code class="ld-tool-name">{{ call.tool_name }}</code>
                  <span class="ld-tool-summary" :title="call.compact_summary">{{ call.compact_summary }}</span>
                </li>
              </ul>
            </div>

            <p v-if="!hasEvidence" class="ld-evidence-empty">
              {{ t('liveDigest.chapter.noEvidence') }}
            </p>

            <div class="ld-chapter-links">
              <button
                type="button"
                class="ld-link-btn ui-pressable"
                data-testid="live-digest-jump-orchestration"
                @click="emit('open-orchestration', chapter)"
              >
                <Layers3 :size="12" aria-hidden="true" />
                <span>{{ t('liveDigest.actions.openTimeline') }}</span>
                <ArrowUpRight :size="11" aria-hidden="true" />
              </button>
              <span v-if="chapter.source === 'l1_narrator'" class="ld-ai-hint">
                <Sparkles :size="11" aria-hidden="true" />
                <span>{{ t('liveDigest.source.narratedBadge') }}</span>
              </span>
            </div>
          </div>
        </div>
      </div>
    </div>
  </article>
</template>

<style scoped>
.ld-chapter {
  display: grid;
  grid-template-columns: 14px minmax(0, 1fr);
  column-gap: 10px;
  min-width: 0;
}

.ld-chapter-rail {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding-top: 5px;
}

.ld-chapter-dot {
  width: 7px;
  height: 7px;
  border-radius: 999px;
  background: var(--text-secondary);
  flex-shrink: 0;
}

.ld-chapter-dot.phase-verification {
  background: var(--obs-status-warn, var(--accent-warning));
}

.ld-chapter-dot.phase-conclusion {
  background: var(--obs-status-ok, var(--accent-success));
}

.ld-chapter-spine {
  width: 1.5px;
  flex: 1 1 auto;
  min-height: 14px;
  margin-top: 5px;
  background: var(--obs-hairline, var(--border-muted));
}

.ld-chapter:last-child .ld-chapter-spine {
  background: linear-gradient(
    to bottom,
    var(--obs-hairline, var(--border-muted)) 0%,
    transparent 100%
  );
}

.ld-chapter-main {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
  padding-bottom: 14px;
  border-bottom: 1px solid var(--obs-hairline, var(--border-muted));
}

.ld-chapter:last-child .ld-chapter-main {
  border-bottom: 0;
  padding-bottom: 4px;
}

.ld-chapter.is-compact .ld-chapter-main {
  padding-bottom: 10px;
  gap: 4px;
}

.ld-chapter-meta-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  min-width: 0;
}

.ld-chapter-index {
  font-family: var(--font-mono);
  font-size: 11px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  color: var(--text-muted);
  flex-shrink: 0;
}

.ld-phase-pill {
  display: inline-flex;
  align-items: center;
  padding: 1px 6px;
  border-radius: 4px;
  font-size: 10.5px;
  font-weight: 600;
  line-height: 1.35;
  flex-shrink: 0;
  background: var(--bg-secondary);
  color: var(--text-secondary);
}

.ld-phase-pill.phase-verification {
  background: var(--obs-status-warn-soft, color-mix(in srgb, var(--accent-warning) 13%, transparent));
  color: var(--obs-status-warn, var(--accent-warning));
}

.ld-phase-pill.phase-conclusion {
  background: var(--obs-status-ok-soft, color-mix(in srgb, var(--accent-success) 12%, transparent));
  color: var(--obs-status-ok, var(--accent-success));
}

.ld-source-tag {
  font-size: 10.5px;
  font-family: var(--font-mono);
  color: var(--text-muted);
  flex-shrink: 0;
}

.ld-trajectory-link {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 4px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text-muted);
  font-size: 11px;
  font-weight: 500;
  line-height: 1.35;
  white-space: nowrap;
  cursor: pointer;
  transition: color 0.12s ease, background-color 0.12s ease;
}

.ld-trajectory-link:hover {
  color: var(--text-primary);
  background: var(--bg-secondary);
}

.ld-chapter-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
  width: 100%;
  padding: 1px 0;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
  min-width: 0;
}

.ld-chapter-head:hover .ld-chapter-title {
  color: var(--text-primary);
}

.ld-chapter-title {
  flex: 1 1 auto;
  min-width: 0;
  font-size: 13px;
  font-weight: 600;
  line-height: 1.45;
  letter-spacing: -0.01em;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.ld-chapter-chevron {
  margin-top: 2px;
  color: var(--text-muted);
  flex-shrink: 0;
  transition: transform var(--duration-pop, 180ms) var(--ease-spring-soft, ease);
}

.ld-chapter-chevron.is-open {
  transform: rotate(180deg);
  color: var(--text-primary);
}

.ld-chapter-narrative {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.55;
  color: var(--text-secondary);
  white-space: pre-wrap;
  word-break: break-word;
}

.ld-file-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
  padding-top: 2px;
}

.ld-file-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  padding: 2px 6px;
  border-radius: 4px;
  background: var(--bg-secondary);
  font-size: 11px;
  color: var(--text-secondary);
  font-variant-numeric: tabular-nums;
}

.ld-file-chip-icon {
  color: var(--text-muted);
  flex-shrink: 0;
}

.ld-file-chip-name {
  font-family: var(--font-mono);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 190px;
}

.ld-diff-add {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-weight: 600;
  color: var(--obs-status-ok, var(--accent-success));
}

.ld-diff-del {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-weight: 600;
  color: var(--obs-status-danger, var(--accent-danger));
}

.ld-evidence-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-top: 8px;
  margin-top: 4px;
  border-top: 1px solid var(--obs-hairline, var(--border-muted));
}

.ld-evidence-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.ld-evidence-label {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted);
}

.ld-scout-text {
  margin: 0;
  padding: 6px 8px;
  border-radius: 5px;
  background: var(--bg-secondary);
  color: var(--text-primary);
  font-size: 12px;
  line-height: 1.5;
}

.ld-thought-pre {
  margin: 0;
  padding: 6px 8px;
  border-radius: 5px;
  background: var(--bg-secondary);
  color: var(--text-secondary);
  font-family: var(--font-mono);
  font-size: 11.5px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 160px;
  overflow-y: auto;
}

.ld-tool-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.ld-tool-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 3px 0;
  font-size: 11.5px;
  min-width: 0;
}

.ld-tool-icon {
  flex-shrink: 0;
}

.ld-tool-icon.is-ok {
  color: var(--obs-status-ok, var(--accent-success));
}

.ld-tool-icon.is-failed {
  color: var(--obs-status-danger, var(--accent-danger));
}

.ld-tool-icon.is-running {
  color: var(--obs-status-ok, var(--accent-success));
  animation: ld-spin 1.1s linear infinite;
}

.ld-tool-name {
  font-family: var(--font-mono);
  font-size: 11px;
  font-weight: 600;
  color: var(--text-primary);
  flex-shrink: 0;
}

.ld-tool-summary {
  flex: 1;
  min-width: 0;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ld-evidence-empty {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-muted);
}

.ld-chapter-links {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding-top: 2px;
}

.ld-link-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 7px;
  border-radius: 5px;
  border: 0;
  background: var(--bg-secondary);
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 500;
  line-height: 1.25;
  cursor: pointer;
  transition: background-color 0.12s ease, color 0.12s ease;
}

.ld-link-btn:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.ld-ai-hint {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  color: var(--text-muted);
}

@keyframes ld-spin {
  to {
    transform: rotate(360deg);
  }
}

@media (prefers-reduced-motion: reduce) {
  .ld-tool-icon.is-running {
    animation: none;
  }
}
</style>
