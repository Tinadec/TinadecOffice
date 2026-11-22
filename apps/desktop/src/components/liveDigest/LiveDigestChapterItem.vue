<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Brain,
  CheckCircle2,
  ChevronDown,
  FileCode2,
  GitBranch,
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

const { t } = useI18n()
const expanded = ref(props.defaultExpanded)

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
    <button
      type="button"
      class="ld-chapter-head ui-pressable"
      :aria-expanded="expanded"
      data-testid="live-digest-chapter-toggle"
      @click="toggleExpanded"
    >
      <span class="ld-chapter-index">{{ formattedIndex }}</span>
      <span class="ld-phase-pill" :class="phaseMeta.cls">{{ phaseMeta.label }}</span>
      <span class="ld-chapter-title" :title="chapter.title">{{ chapter.title }}</span>
      <span class="ld-source-tag">{{ sourceLabel }}</span>
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
        <FileCode2 :size="13" class="ld-file-chip-icon" aria-hidden="true" />
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
              <Search :size="13" aria-hidden="true" />
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
              <Brain :size="13" aria-hidden="true" />
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
              <Wrench :size="13" aria-hidden="true" />
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
                  :size="13"
                  class="ld-tool-icon is-running"
                  aria-hidden="true"
                />
                <XCircle
                  v-else-if="call.status === 'failed'"
                  :size="13"
                  class="ld-tool-icon is-failed"
                  aria-hidden="true"
                />
                <CheckCircle2
                  v-else
                  :size="13"
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
              <Layers3 :size="13" aria-hidden="true" />
              <span>{{ t('liveDigest.actions.openTimeline') }}</span>
            </button>
            <button
              type="button"
              class="ld-link-btn ui-pressable"
              data-testid="live-digest-jump-trajectory"
              @click="emit('open-trajectory', chapter)"
            >
              <GitBranch :size="13" aria-hidden="true" />
              <span>{{ t('liveDigest.actions.openTrajectory') }}</span>
            </button>
            <span v-if="chapter.source === 'l1_narrator'" class="ld-ai-hint">
              <Sparkles :size="12" aria-hidden="true" />
              <span>{{ t('liveDigest.source.narratedBadge') }}</span>
            </span>
          </div>
        </div>
      </div>
    </div>
  </article>
</template>

<style scoped>
.ld-chapter {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 11px;
  border-radius: var(--radius-card, 10px);
  border: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  transition:
    border-color var(--duration-tap, 120ms) var(--ease-snappy, ease),
    background-color var(--duration-tap, 120ms) var(--ease-snappy, ease);
}

.ld-chapter:hover {
  border-color: var(--border-default);
}

.ld-chapter.is-expanded {
  background: var(--bg-tertiary);
  border-color: var(--border-default);
}

.ld-chapter.is-compact {
  padding: 8px 9px;
}

.ld-chapter-head {
  display: flex;
  align-items: center;
  gap: 7px;
  width: 100%;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
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
  border-radius: 999px;
  font-size: 11px;
  font-weight: 600;
  line-height: 1.4;
  flex-shrink: 0;
  background: var(--bg-status-neutral);
  color: var(--text-secondary);
}

.ld-phase-pill.phase-hypothesis {
  background: var(--bg-status-info-soft, var(--bg-status-info));
  color: var(--text-primary);
}

.ld-phase-pill.phase-verification {
  background: var(--bg-status-warn);
  color: var(--accent-warning);
}

.ld-phase-pill.phase-conclusion {
  background: var(--bg-status-ok);
  color: var(--accent-success);
}

.ld-chapter-title {
  flex: 1;
  min-width: 0;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ld-source-tag {
  font-size: 10.5px;
  font-family: var(--font-mono);
  color: var(--text-muted);
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--bg-primary);
  flex-shrink: 0;
}

.ld-chapter-chevron {
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
}

.ld-file-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  padding: 2px 7px;
  border-radius: 6px;
  background: var(--bg-primary);
  border: 1px solid var(--border-muted);
  font-size: 11px;
  color: var(--text-secondary);
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
  max-width: 180px;
}

.ld-diff-add {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-weight: 600;
  color: var(--accent-success);
}

.ld-diff-del {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-weight: 600;
  color: var(--accent-danger);
}

.ld-evidence-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-top: 8px;
  margin-top: 2px;
  border-top: 1px solid var(--border-muted);
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
  padding: 7px 9px;
  border-radius: 7px;
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: 12px;
  line-height: 1.5;
}

.ld-thought-pre {
  margin: 0;
  padding: 7px 9px;
  border-radius: 7px;
  background: var(--bg-primary);
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
  gap: 4px;
}

.ld-tool-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 7px;
  border-radius: 6px;
  background: var(--bg-primary);
  font-size: 11.5px;
  min-width: 0;
}

.ld-tool-icon {
  flex-shrink: 0;
}

.ld-tool-icon.is-ok {
  color: var(--accent-success);
}

.ld-tool-icon.is-failed {
  color: var(--accent-danger);
}

.ld-tool-icon.is-running {
  color: var(--accent-primary);
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
  gap: 6px;
  padding-top: 2px;
}

.ld-link-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 8px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: var(--bg-button);
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.2;
  cursor: pointer;
}

.ld-link-btn:hover {
  background: var(--bg-button-hover);
  color: var(--text-primary);
  border-color: var(--border-default);
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
