<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ChevronDown,
  ChevronRight,
  CopyCheck,
  FileSearch,
  RefreshCw,
  Sparkles,
} from '@lucide/vue'
import {
  api,
  type AgentViewDto,
  type PromptPipelineDto,
  type ToolDescriptorDto,
} from '@/api'
import {
  auditContextOverlap,
  formatLineRangePair,
  type ContextAuditSource,
  type ContextOverlapAuditResult,
  type ContextOverlapPairSummary,
  type ContextSourceKind,
} from '@/lib/contextOverlapWinnowing'

const props = withDefaults(defineProps<{
  /** Pre-supplied audit sources; when omitted and `autoLoadMode !== 'none'`, loads live sources. */
  sources?: ContextAuditSource[]
  autoLoadMode?: 'none' | 'prompts' | 'tools' | 'all'
  agents?: AgentViewDto[]
  promptPipelines?: PromptPipelineDto[]
  tools?: ToolDescriptorDto[]
  projectId?: string
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
  canAccessBackend: true,
  collapsible: false,
  defaultOpen: true,
  compact: false,
  kGramSize: 64,
  windowSize: 16,
})

const emit = defineEmits<{
  'diagnose-overlap': [audit: ContextOverlapAuditResult, pair?: ContextOverlapPairSummary]
}>()

const { t } = useI18n()

const expanded = ref(props.collapsible ? props.defaultOpen : true)
const loading = ref(false)
const loadedRemoteSources = ref<ContextAuditSource[]>([])
const customInstructionsOpen = ref(false)
const customInstructionsText = ref('')
const expandedPairIds = ref<Set<string>>(new Set())

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

const propDerivedSources = computed<ContextAuditSource[]>(() => {
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

  return list
})

async function loadLiveSources(): Promise<void> {
  if (props.sources || props.autoLoadMode === 'none' || !props.canAccessBackend) return
  loading.value = true
  try {
    const gathered: ContextAuditSource[] = []

    const wantPrompts = props.autoLoadMode === 'prompts' || props.autoLoadMode === 'all'
    const wantTools = props.autoLoadMode === 'tools' || props.autoLoadMode === 'all'

    const tasks: Array<Promise<void>> = []

    if (wantPrompts && typeof api.listPromptFragments === 'function') {
      tasks.push(
        api.listPromptFragments({ enabled: true }).then((fragments) => {
          for (const frag of fragments) {
            if (!frag.content || !frag.content.trim()) continue
            gathered.push({
              id: `fragment:${frag.id}`,
              label: frag.title || frag.key,
              kind: 'prompt_fragment',
              content: frag.content,
            })
          }
        }).catch(() => { /* ignore optional endpoint failure */ }),
      )
    }

    if (wantPrompts && props.promptPipelines.length === 0 && typeof api.listPromptPipelines === 'function') {
      tasks.push(
        api.listPromptPipelines().then((pipelines) => {
          gathered.push(...extractPipelineTemplates(pipelines))
        }).catch(() => { /* ignore */ }),
      )
    }

    if ((wantPrompts || wantTools) && typeof api.listToolSkills === 'function') {
      tasks.push(
        api.listToolSkills(props.projectId).then(async (res) => {
          const enabledSkills = (res.skills ?? []).filter((s) => s.enabled !== false).slice(0, 24)
          for (const skill of enabledSkills) {
            let body = typeof skill.content === 'string' && skill.content.trim()
              ? skill.content
              : ''
            if (!body && typeof api.getToolSkill === 'function') {
              try {
                const detail = await api.getToolSkill(skill.resource_id, props.projectId)
                if (typeof detail.content === 'string') body = detail.content
              } catch {
                // fallback to description
              }
            }
            const combined = [skill.description, skill.when_to_use, body].filter(Boolean).join('\n')
            if (combined.trim()) {
              gathered.push({
                id: `skill:${skill.resource_id}`,
                label: skill.name || skill.resource_id,
                kind: 'skill',
                path: skill.relative_path || skill.path || null,
                content: combined,
              })
            }
          }
        }).catch(() => { /* ignore */ }),
      )
    }

    if (wantTools && props.tools.length === 0 && typeof api.listTools === 'function') {
      tasks.push(
        api.listTools().then((tools) => {
          const lines = tools
            .filter((tool) => tool.description && tool.description.trim())
            .map((tool) => `${tool.id}: ${tool.description}`)
          if (lines.length > 0) {
            gathered.push({
              id: 'tools:live-catalog',
              label: t('trajectoryEval.overlap.toolDescriptionsSource', { count: lines.length }),
              kind: 'tool_schema',
              content: lines.join('\n'),
            })
          }
        }).catch(() => { /* ignore */ }),
      )
    }

    await Promise.all(tasks)
    loadedRemoteSources.value = gathered
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  if (expanded.value && !props.sources && props.autoLoadMode !== 'none') {
    void loadLiveSources()
  }
})

watch(
  () => [expanded.value, props.projectId],
  ([isOpen]) => {
    if (isOpen && !props.sources && props.autoLoadMode !== 'none') {
      void loadLiveSources()
    }
  },
)

const combinedSources = computed<ContextAuditSource[]>(() => {
  const byId = new Map<string, ContextAuditSource>()
  for (const s of propDerivedSources.value) byId.set(s.id, s)
  for (const s of loadedRemoteSources.value) {
    if (!byId.has(s.id)) byId.set(s.id, s)
  }
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

const auditResult = computed<ContextOverlapAuditResult>(() =>
  auditContextOverlap(combinedSources.value, {
    kGramSize: props.kGramSize,
    windowSize: props.windowSize,
  }),
)

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
    class="overlap-card"
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
          @click="loadLiveSources"
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

      <div v-if="loading" class="overlap-empty">
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
.overlap-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid var(--border-muted);
  border-radius: var(--radius-card, 10px);
  background: var(--bg-secondary);
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
  font-size: 12px;
  cursor: pointer;
  text-align: left;
}

.overlap-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
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
  height: 21px;
  padding: 0 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--bg-primary);
  color: var(--text-secondary);
  font-size: 10.5px;
}

.overlap-pill.is-warn {
  background: var(--bg-status-warn);
  color: var(--text-primary);
  border-color: color-mix(in srgb, var(--accent-warning) 36%, var(--border-muted));
}

.overlap-icon-btn {
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--bg-primary);
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
  height: var(--control-height-xs, 26px);
  padding: 0 8px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: var(--bg-primary);
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.overlap-ghost-btn:hover {
  border-color: var(--accent-primary);
  color: var(--text-primary);
}

.overlap-paste-box {
  display: flex;
  flex-direction: column;
}

.overlap-textarea {
  width: 100%;
  padding: 8px;
  border: 1px solid var(--border-input);
  border-radius: 7px;
  background: var(--bg-input);
  color: var(--text-primary);
  font-size: 11px;
  resize: vertical;
}

.overlap-empty {
  padding: 10px;
  text-align: center;
  color: var(--text-muted);
  font-size: 11.5px;
}

.overlap-clean {
  padding: 9px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 7px;
  background: var(--bg-status-ok);
  color: var(--text-primary);
  font-size: 11.5px;
}

.overlap-pairs {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.overlap-pair {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 9px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-primary);
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
  background: var(--bg-tertiary);
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
  padding: 6px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--bg-secondary);
  font-size: 11px;
  color: var(--text-secondary);
  line-height: 1.45;
}

.overlap-remediation__tag {
  flex-shrink: 0;
  padding: 0 5px;
  border-radius: 4px;
  border: 1px solid var(--border-default);
  background: var(--bg-primary);
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
  background: var(--bg-secondary);
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
  border: 1px solid var(--border-default);
  background: var(--bg-primary);
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

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}

.spinning {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
