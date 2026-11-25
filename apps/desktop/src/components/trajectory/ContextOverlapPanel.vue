<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ChevronDown,
  ChevronRight,
  CopyCheck,
  Database,
  FileSearch,
  RefreshCw,
  Sparkles,
} from '@lucide/vue'
import {
  api,
  type AgentViewDto,
  type ContextAuditExtraSourceDto,
  type PromptPipelineDto,
  type ToolDescriptorDto,
} from '@/api'
import {
  buildOverlapCacheScopeKey,
  fetchOverlapAuditWithCache,
  getCachedOverlapAudit,
} from '@/lib/contextDiagnosticsCache'
import {
  auditContextOverlap,
  formatLineRangePair,
  fromCoreOverlapAuditDto,
  type ContextAuditSource,
  type ContextOverlapAuditResult,
  type ContextOverlapPairSummary,
  type ContextSourceKind,
} from '@/lib/contextOverlapWinnowing'

const props = withDefaults(defineProps<{
  /** Pre-supplied audit sources; when omitted and `autoLoadMode !== 'none'`, queries Core `POST /api/v1/context/audit-overlap`. */
  sources?: ContextAuditSource[]
  autoLoadMode?: 'none' | 'prompts' | 'tools' | 'all'
  agents?: AgentViewDto[]
  promptPipelines?: PromptPipelineDto[]
  tools?: ToolDescriptorDto[]
  projectId?: string
  modeId?: string | null
  agentId?: string | null
  runId?: string | null
  canAccessBackend?: boolean
  collapsible?: boolean
  defaultOpen?: boolean
  compact?: boolean
  kGramSize?: number
  windowSize?: number
}>(), {
  sources: undefined,
  autoLoadMode: 'none',
  agents: () => [],
  promptPipelines: () => [],
  tools: () => [],
  projectId: undefined,
  modeId: null,
  agentId: null,
  runId: null,
  canAccessBackend: true,
  collapsible: false,
  defaultOpen: true,
  compact: false,
  kGramSize: 64,
  windowSize: 16,
})

const emit = defineEmits<{
  'diagnose-overlap': [audit: ContextOverlapAuditResult, pair?: ContextOverlapPairSummary]
  'audit-updated': [audit: ContextOverlapAuditResult, meta: { stale: boolean; cachedAt: string | null }]
}>()

const { t } = useI18n()

const expanded = ref(props.collapsible ? props.defaultOpen : true)
const loading = ref(false)
const remoteAuditResult = ref<ContextOverlapAuditResult | null>(null)
const isStaleSnapshot = ref(false)
const cachedSnapshotAt = ref<string | null>(null)
const customInstructionsOpen = ref(false)
const customInstructionsText = ref('')
const expandedPairIds = ref<Set<string>>(new Set())

let activeAbort: AbortController | null = null
let customDebounceTimer: ReturnType<typeof setTimeout> | null = null
let requestEpoch = 0

function extractPipelineTemplates(pipelines: PromptPipelineDto[]): ContextAuditSource[] {
  const out: ContextAuditSource[] = []
  for (const pipeline of pipelines) {
    const nodes = pipeline.graph?.nodes ?? pipeline.nodes ?? []
    const chunks: string[] = []
    for (const node of nodes) {
      const cfg = (node.config ?? node.data ?? {}) as Record<string, unknown>
      const tpl = typeof cfg.template === 'string'
        ? cfg.template
        : typeof cfg.content === 'string'
          ? cfg.content
          : typeof node.template === 'string'
            ? node.template
            : ''
      if (tpl.trim()) chunks.push(tpl.trim())
    }
    if (chunks.length > 0) {
      out.push({
        id: `pipeline:${pipeline.id}`,
        label: pipeline.display_name || pipeline.name || pipeline.slug || pipeline.id,
        kind: 'pipeline_template',
        content: chunks.join('\n\n'),
      })
    }
  }
  return out
}

const extraSources = computed<ContextAuditSource[]>(() => {
  if (props.sources) return props.sources

  const list: ContextAuditSource[] = []

  for (const agent of props.agents) {
    if (agent.system_prompt && agent.system_prompt.trim().length > 0) {
      list.push({
        id: `agent:${agent.id}`,
        label: agent.display_name || agent.name || agent.slug || agent.id,
        kind: 'system_prompt',
        content: agent.system_prompt,
      })
    }
  }

  list.push(...extractPipelineTemplates(props.promptPipelines))

  if (props.tools.length > 0) {
    const schemaLines = props.tools
      .filter((tool) => tool.description && tool.description.trim().length > 0)
      .map((tool) => `${tool.id}: ${tool.description}`)
    if (schemaLines.length > 0) {
      list.push({
        id: 'tools:catalog',
        label: t('trajectoryEval.overlap.toolDescriptionsSource', { count: schemaLines.length }),
        kind: 'tool_schema',
        content: schemaLines.join('\n'),
      })
    }
  }

  if (customInstructionsText.value.trim().length > 0) {
    list.push({
      id: 'custom:workspace_instructions',
      label: 'AGENTS.md / .apm/instructions',
      kind: 'workspace_instructions',
      content: customInstructionsText.value,
    })
  }

  return list
})

const scopeCacheKey = computed(() =>
  buildOverlapCacheScopeKey({
    runId: props.runId,
    projectId: props.projectId,
    modeId: props.modeId,
    agentId: props.agentId,
    autoLoadMode: props.autoLoadMode,
  }),
)

function seedFromCacheIfPresent(): void {
  if (props.sources || remoteAuditResult.value) return
  const cached = getCachedOverlapAudit(scopeCacheKey.value, {
    projectId: props.projectId,
    runId: props.runId,
  })
  if (cached) {
    remoteAuditResult.value = fromCoreOverlapAuditDto(cached.data)
    isStaleSnapshot.value = !props.canAccessBackend
    cachedSnapshotAt.value = cached.cachedAt
  }
}

async function loadCoreOverlapAudit(): Promise<void> {
  if (props.sources || props.autoLoadMode === 'none') return

  activeAbort?.abort()
  const controller = new AbortController()
  activeAbort = controller
  const epoch = ++requestEpoch

  if (!props.canAccessBackend || typeof api.auditContextOverlap !== 'function') {
    const cached = getCachedOverlapAudit(scopeCacheKey.value, {
      projectId: props.projectId,
      runId: props.runId,
    })
    if (cached) {
      const mapped = fromCoreOverlapAuditDto(cached.data)
      remoteAuditResult.value = mapped
      isStaleSnapshot.value = true
      cachedSnapshotAt.value = cached.cachedAt
      emit('audit-updated', mapped, { stale: true, cachedAt: cached.cachedAt })
    }
    return
  }

  loading.value = true
  try {
    const wantPrompts = props.autoLoadMode === 'prompts' || props.autoLoadMode === 'all'
    const wantTools = props.autoLoadMode === 'tools' || props.autoLoadMode === 'all'
    const extraDtos: ContextAuditExtraSourceDto[] = extraSources.value.map((s) => ({
      id: s.id,
      label: s.label,
      kind: s.kind,
      content: s.content,
      path: s.path ?? null,
      meta: s.meta ?? null,
    }))

    const outcome = await fetchOverlapAuditWithCache(
      scopeCacheKey.value,
      () =>
        api.auditContextOverlap(
          {
            project_id: props.projectId ?? null,
            mode_id: props.modeId ?? null,
            agent_id: props.agentId ?? null,
            run_id: props.runId ?? null,
            include_prompts: wantPrompts,
            include_skills: wantPrompts || wantTools,
            include_tools: wantTools,
            k_gram_size: props.kGramSize,
            window_size: props.windowSize,
            extra_sources: extraDtos.length > 0 ? extraDtos : null,
          },
          controller.signal,
        ),
      {
        projectId: props.projectId,
        runId: props.runId,
        canAccessBackend: props.canAccessBackend,
      },
    )

    if (epoch !== requestEpoch || controller.signal.aborted) return

    if (outcome.data) {
      const mapped = fromCoreOverlapAuditDto(outcome.data)
      remoteAuditResult.value = mapped
      isStaleSnapshot.value = outcome.stale
      cachedSnapshotAt.value = outcome.cachedAt
      emit('audit-updated', mapped, { stale: outcome.stale, cachedAt: outcome.cachedAt })
    } else {
      isStaleSnapshot.value = Boolean(outcome.error)
    }
  } catch {
    // Aborted or unexpected error: preserve any existing remoteAuditResult / offline fallback.
  } finally {
    if (epoch === requestEpoch) {
      loading.value = false
    }
    if (activeAbort === controller) {
      activeAbort = null
    }
  }
}

onMounted(() => {
  seedFromCacheIfPresent()
  if (expanded.value && !props.sources && props.autoLoadMode !== 'none') {
    void loadCoreOverlapAudit()
  }
})

onBeforeUnmount(() => {
  activeAbort?.abort()
  if (customDebounceTimer) clearTimeout(customDebounceTimer)
})

watch(
  () => [expanded.value, props.projectId, props.modeId, props.agentId, props.runId, props.canAccessBackend],
  ([isOpen]) => {
    if (isOpen && !props.sources && props.autoLoadMode !== 'none') {
      void loadCoreOverlapAudit()
    }
  },
)

watch(customInstructionsText, () => {
  if (props.sources || props.autoLoadMode === 'none' || !expanded.value) return
  if (customDebounceTimer) clearTimeout(customDebounceTimer)
  customDebounceTimer = setTimeout(() => {
    void loadCoreOverlapAudit()
  }, 260)
})

const localFallbackSources = computed<ContextAuditSource[]>(() => {
  if (!props.sources) return extraSources.value
  const byId = new Map<string, ContextAuditSource>()
  for (const s of props.sources) byId.set(s.id, s)
  if (customInstructionsText.value.trim().length > 0) {
    byId.set('custom:workspace_instructions', {
      id: 'custom:workspace_instructions',
      label: 'AGENTS.md / .apm/instructions',
      kind: 'workspace_instructions',
      content: customInstructionsText.value,
    })
  }
  return [...byId.values()]
})

const auditResult = computed<ContextOverlapAuditResult>(() => {
  if (!props.sources && remoteAuditResult.value) {
    return remoteAuditResult.value
  }
  return auditContextOverlap(localFallbackSources.value, {
    kGramSize: props.kGramSize,
    windowSize: props.windowSize,
  })
})

function togglePair(pairId: string): void {
  const next = new Set(expandedPairIds.value)
  if (next.has(pairId)) next.delete(pairId)
  else next.add(pairId)
  expandedPairIds.value = next
}

function kindLabel(kind: ContextSourceKind): string {
  const map: Record<ContextSourceKind, string> = {
    system_prompt: 'trajectoryEval.overlap.kind.systemPrompt',
    pipeline_template: 'trajectoryEval.overlap.kind.pipelineTemplate',
    workspace_instructions: 'trajectoryEval.overlap.kind.workspaceInstructions',
    tool_schema: 'trajectoryEval.overlap.kind.toolSchema',
    skill: 'trajectoryEval.overlap.kind.skill',
    prompt_fragment: 'trajectoryEval.overlap.kind.promptFragment',
    other: 'trajectoryEval.overlap.kind.other',
  }
  return t(map[kind] ?? map.other)
}
</script>

<template>
  <section
    class="overlap-surface"
    :class="{ 'is-compact': compact, 'is-collapsed': collapsible && !expanded }"
    data-testid="context-overlap-panel"
  >
    <header class="overlap-head">
      <button
        v-if="collapsible"
        type="button"
        class="overlap-toggle"
        :aria-expanded="expanded"
        data-testid="context-overlap-toggle"
        @click="expanded = !expanded"
      >
        <ChevronDown v-if="expanded" :size="14" aria-hidden="true" />
        <ChevronRight v-else :size="14" aria-hidden="true" />
        <CopyCheck :size="14" aria-hidden="true" />
        <strong>{{ t('trajectoryEval.overlap.title') }}</strong>
      </button>
      <div v-else class="overlap-title">
        <CopyCheck :size="14" aria-hidden="true" />
        <strong>{{ t('trajectoryEval.overlap.title') }}</strong>
      </div>

      <div class="overlap-head-right">
        <span
          v-if="isStaleSnapshot"
          class="overlap-pill is-stale mono"
          :title="cachedSnapshotAt ?? undefined"
          data-testid="overlap-cached-badge"
        >
          <Database :size="11" aria-hidden="true" />
          <span>{{ t('trajectoryEval.offline.cachedBadge') }}</span>
        </span>
        <span class="overlap-pill mono" data-testid="overlap-source-count">
          {{ t('trajectoryEval.overlap.sourcesCount', { count: auditResult.sourceCount }) }}
        </span>
        <span
          class="overlap-pill mono"
          :class="{ 'is-warn': auditResult.pairs.length > 0 }"
          data-testid="overlap-percent-pill"
        >
          {{ t('trajectoryEval.overlap.duplicateSummary', {
            pct: auditResult.overallOverlapPercent,
            tokens: auditResult.duplicateEstimatedTokens.toLocaleString(),
          }) }}
        </span>
        <button
          v-if="expanded && !sources && autoLoadMode !== 'none'"
          type="button"
          class="overlap-icon-btn"
          :disabled="loading"
          :title="t('trajectoryEval.overlap.rescan')"
          data-testid="overlap-rescan-btn"
          @click="loadCoreOverlapAudit"
        >
          <RefreshCw :size="12" :class="{ spinning: loading }" />
        </button>
      </div>
    </header>

    <div v-if="expanded" class="overlap-body" data-testid="context-overlap-body">
      <div class="overlap-toolbar">
        <span class="overlap-meta mono">
          {{ t('trajectoryEval.overlap.metaStats', {
            k: auditResult.kGramSize,
            w: auditResult.windowSize,
            tokens: auditResult.totalEstimatedTokens.toLocaleString(),
          }) }}
        </span>
        <div class="overlap-toolbar-actions">
          <button
            type="button"
            class="overlap-ghost-btn"
            data-testid="overlap-paste-toggle"
            @click="customInstructionsOpen = !customInstructionsOpen"
          >
            <FileSearch :size="12" aria-hidden="true" />
            <span>{{ t('trajectoryEval.overlap.pasteAgentsMd') }}</span>
          </button>
          <button
            v-if="auditResult.pairs.length > 0"
            type="button"
            class="overlap-ghost-btn"
            data-testid="overlap-diagnose-all-btn"
            @click="emit('diagnose-overlap', auditResult, auditResult.pairs[0])"
          >
            <Sparkles :size="12" aria-hidden="true" />
            <span>{{ t('trajectoryEval.actions.diagnoseInComposer') }}</span>
          </button>
        </div>
      </div>

      <div v-if="customInstructionsOpen" class="overlap-paste-box">
        <textarea
          v-model="customInstructionsText"
          rows="4"
          class="overlap-textarea mono"
          :placeholder="t('trajectoryEval.overlap.pastePlaceholder')"
          data-testid="overlap-paste-textarea"
        />
      </div>

      <div v-if="loading && !remoteAuditResult" class="overlap-empty">
        {{ t('trajectoryEval.overlap.scanning') }}
      </div>
      <div v-else-if="auditResult.sourceCount < 2" class="overlap-empty" data-testid="overlap-insufficient">
        {{ t('trajectoryEval.overlap.needTwoSources') }}
      </div>
      <div v-else-if="auditResult.pairs.length === 0" class="overlap-clean" data-testid="overlap-clean">
        {{ t('trajectoryEval.overlap.noOverlap') }}
      </div>

      <div v-else class="overlap-pairs" data-testid="overlap-pair-list">
        <article
          v-for="pair in auditResult.pairs"
          :key="pair.id"
          class="overlap-pair"
          :data-testid="`overlap-pair-${pair.id}`"
        >
          <div class="overlap-pair__head" @click="togglePair(pair.id)">
            <div class="overlap-pair__titles">
              <ChevronDown v-if="expandedPairIds.has(pair.id)" :size="13" />
              <ChevronRight v-else :size="13" />
              <span class="overlap-kind-chip">{{ kindLabel(pair.sourceAKind) }}</span>
              <strong>{{ pair.sourceALabel }}</strong>
              <span class="overlap-arrow">&harr;</span>
              <span class="overlap-kind-chip">{{ kindLabel(pair.sourceBKind) }}</span>
              <strong>{{ pair.sourceBLabel }}</strong>
            </div>
            <div class="overlap-pair__metrics mono">
              <span class="overlap-pill is-warn">{{ pair.overlapPercent }}%</span>
              <span class="overlap-pill">{{ t('trajectoryEval.overlap.dupTokensShort', { tokens: pair.estimatedDuplicateTokens }) }}</span>
              <span class="overlap-pill">{{ pair.matchCount }}x</span>
            </div>
          </div>

          <div class="overlap-remediation" data-testid="overlap-remediation">
            <span class="overlap-remediation__tag">{{ t('trajectoryEval.overlap.remediationLabel') }}</span>
            <span>{{ t(`trajectoryEval.overlap.remediations.${pair.remediationCode}`) }}</span>
          </div>

          <div v-if="expandedPairIds.has(pair.id) || auditResult.pairs.length <= 2" class="overlap-matches">
            <div
              v-for="m in pair.matches"
              :key="m.id"
              class="overlap-match"
              data-testid="overlap-match-row"
            >
              <div class="overlap-match__head mono">
                <span class="overlap-range-pill" data-testid="overlap-line-range">
                  {{ formatLineRangePair(m.sourceALines, m.sourceBLines) }}
                </span>
                <span>{{ t('trajectoryEval.overlap.matchStats', { chars: m.sharedChars, tokens: m.estimatedDuplicateTokens }) }}</span>
              </div>
              <pre class="overlap-match__excerpt mono">{{ m.excerpt }}</pre>
            </div>
          </div>
        </article>
      </div>
    </div>
  </section>
</template>

<style scoped>
.overlap-surface {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px;
  background: var(--surface-canvas, var(--bg-primary));
  border-bottom: 1px solid var(--border-muted);
  min-width: 0;
}

.overlap-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.overlap-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border: 0;
  padding: 0;
  background: transparent;
  color: var(--text-primary);
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  text-align: left;
}

.overlap-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.overlap-head-right {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.overlap-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 21px;
  padding: 0 7px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 10.5px;
}

.overlap-pill.is-warn {
  background: var(--bg-status-warn);
  color: var(--text-primary);
  border-color: color-mix(in srgb, var(--accent-warning) 36%, var(--border-muted));
}

.overlap-pill.is-stale {
  border-style: dashed;
}

.overlap-icon-btn {
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--border-muted);
  border-radius: 5px;
  background: var(--surface-button, var(--bg-primary));
  color: var(--text-secondary);
  cursor: pointer;
}

.overlap-icon-btn:hover {
  border-color: var(--border-default);
  color: var(--text-primary);
}

.overlap-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.overlap-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.overlap-meta {
  font-size: 10.5px;
  color: var(--text-muted);
}

.overlap-toolbar-actions {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.overlap-ghost-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 25px;
  padding: 0 8px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--surface-button, var(--bg-primary));
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.overlap-ghost-btn:hover {
  border-color: var(--text-primary);
  color: var(--text-primary);
}

.overlap-paste-box {
  display: flex;
  flex-direction: column;
}

.overlap-textarea {
  width: 100%;
  padding: 8px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--surface-input, var(--bg-secondary));
  color: var(--text-primary);
  font-size: 11px;
  resize: vertical;
}

.overlap-empty {
  padding: 12px;
  text-align: center;
  color: var(--text-muted);
  font-size: 11.5px;
}

.overlap-clean {
  padding: 8px 10px;
  border-top: 1px solid var(--border-muted);
  color: var(--text-secondary);
  font-size: 11.5px;
}

.overlap-pairs {
  display: flex;
  flex-direction: column;
}

.overlap-pair {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 0;
  border-top: 1px solid var(--border-muted);
}

.overlap-pair__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
  cursor: pointer;
}

.overlap-pair__titles {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
  font-size: 12px;
  color: var(--text-primary);
}

.overlap-kind-chip {
  padding: 1px 5px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 10px;
}

.overlap-arrow {
  color: var(--text-muted);
}

.overlap-pair__metrics {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.overlap-remediation {
  display: flex;
  align-items: baseline;
  gap: 6px;
  font-size: 11px;
  color: var(--text-secondary);
  line-height: 1.45;
}

.overlap-remediation__tag {
  flex-shrink: 0;
  padding: 0 5px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-primary);
  font-size: 10px;
  font-weight: 500;
}

.overlap-matches {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.overlap-match {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 6px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--surface-section, var(--bg-secondary));
}

.overlap-match__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-size: 10.5px;
  color: var(--text-muted);
}

.overlap-range-pill {
  padding: 1px 6px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-primary));
  color: var(--text-primary);
  font-weight: 500;
}

.overlap-match__excerpt {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 10.5px;
  color: var(--text-secondary);
  line-height: 1.4;
}

.spinning {
  animation: overlap-spin 0.9s linear infinite;
}

@keyframes overlap-spin {
  to {
    transform: rotate(360deg);
  }
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}
</style>
