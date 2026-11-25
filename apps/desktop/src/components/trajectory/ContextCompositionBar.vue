<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Database } from '@lucide/vue'
import type {
  ModelInvocationDto,
  RunContextDiagnosticsDto,
  ToolExecutionTimelineItemDto,
} from '@/api'
import {
  type ContextTreemapCategory,
  type ContextTreemapModel,
} from '@/lib/contextTreemapLayout'
import { resolveTrajectoryCopy } from '@/lib/trajectoryBuilder'

export type ContextCompositionCategoryKey =
  | 'system'
  | 'tools'
  | 'user'
  | 'inject'
  | 'skill'
  | 'assistant'
  | 'tool_output'

export interface ContextCompositionSegment {
  key: ContextCompositionCategoryKey
  label: string
  color: string
  tokens: number
  shareOfUsed: number
  shareOfWindow: number
  estimated: boolean
  treemapCategory: ContextTreemapCategory
}

const props = withDefaults(defineProps<{
  treemap?: ContextTreemapModel | null
  diagnostics?: RunContextDiagnosticsDto | null
  modelInvocations?: readonly ModelInvocationDto[] | null
  toolExecutions?: readonly ToolExecutionTimelineItemDto[] | null
  contextWindowTokens?: number | null
  turnLabel?: string | null
  modelLabel?: string | null
  activeCategory?: string | null
  stale?: boolean
  forceEstimated?: boolean
}>(), {
  treemap: null,
  diagnostics: null,
  modelInvocations: () => [],
  toolExecutions: () => [],
  contextWindowTokens: null,
  turnLabel: null,
  modelLabel: null,
  activeCategory: null,
  stale: false,
  forceEstimated: false,
})

const emit = defineEmits<{
  'select-category': [category: ContextCompositionCategoryKey, treemapCategory: ContextTreemapCategory]
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  return resolveTrajectoryCopy(t, te, key, fallback, params)
}

const CATEGORY_DEFS: ReadonlyArray<{
  key: ContextCompositionCategoryKey
  labelKey: string
  fallbackLabel: string
  color: string
  defaultTreemapCategory: ContextTreemapCategory
}> = [
  {
    key: 'system',
    labelKey: 'trajectoryWorkbench.categories.system',
    fallbackLabel: '系统提示',
    color: 'var(--obs-ctx-system, #8053e3)',
    defaultTreemapCategory: 'system_prompt',
  },
  {
    key: 'tools',
    labelKey: 'trajectoryWorkbench.categories.tools',
    fallbackLabel: '工具定义',
    color: 'var(--obs-ctx-tools, #d49b00)',
    defaultTreemapCategory: 'tool_schemas',
  },
  {
    key: 'user',
    labelKey: 'trajectoryWorkbench.categories.user',
    fallbackLabel: '用户输入',
    color: 'var(--obs-ctx-user, #44a56b)',
    defaultTreemapCategory: 'conversation_turns',
  },
  {
    key: 'inject',
    labelKey: 'trajectoryWorkbench.categories.inject',
    fallbackLabel: '注入',
    color: 'var(--obs-ctx-inject, #c442a7)',
    defaultTreemapCategory: 'project_instructions',
  },
  {
    key: 'skill',
    labelKey: 'trajectoryWorkbench.categories.skill',
    fallbackLabel: 'Skill',
    color: 'var(--obs-ctx-skill, #e66d26)',
    defaultTreemapCategory: 'skills_catalog',
  },
  {
    key: 'assistant',
    labelKey: 'trajectoryWorkbench.categories.assistant',
    fallbackLabel: '模型输出',
    color: 'var(--obs-ctx-assistant, #5b9fe8)',
    defaultTreemapCategory: 'conversation_turns',
  },
  {
    key: 'tool_output',
    labelKey: 'trajectoryWorkbench.categories.toolOutput',
    fallbackLabel: '工具输出',
    color: 'var(--obs-ctx-tool-output, #3ab4a4)',
    defaultTreemapCategory: 'tool_outputs',
  },
]

function mapTreemapToCompositionKey(cat: ContextTreemapCategory): ContextCompositionCategoryKey {
  switch (cat) {
    case 'system_prompt':
    case 'mode_pipeline':
      return 'system'
    case 'tool_schemas':
      return 'tools'
    case 'conversation_turns':
      return 'user'
    case 'skills_catalog':
      return 'skill'
    case 'tool_outputs':
      return 'tool_output'
    case 'project_instructions':
    case 'memory':
    case 'compacted_digest':
    case 'other':
    default:
      return 'inject'
  }
}

const resolvedModelLabel = computed<string | null>(() => {
  if (props.modelLabel) return props.modelLabel
  const invs = props.modelInvocations ?? []
  for (let i = invs.length - 1; i >= 0; i--) {
    const m = invs[i]?.model?.trim()
    if (m) return m
  }
  return null
})

interface ComputedComposition {
  segments: ContextCompositionSegment[]
  usedTokens: number
  maxTokens: number
  usagePct: number | null
  isEstimated: boolean
  estimationBasis: string | null
}

const composition = computed<ComputedComposition>(() => {
  const tm = props.treemap
  const invs = props.modelInvocations ?? []
  const execs = props.toolExecutions ?? []

  const bucketTokens: Record<ContextCompositionCategoryKey, number> = {
    system: 0,
    tools: 0,
    user: 0,
    inject: 0,
    skill: 0,
    assistant: 0,
    tool_output: 0,
  }

  const dominantTreemapCat = new Map<ContextCompositionCategoryKey, { cat: ContextTreemapCategory; tokens: number }>()
  let isEstimated = Boolean(props.forceEstimated)
  let estimationBasis: string | null = props.forceEstimated
    ? tr(
        'trajectoryWorkbench.composition.estimatedManual',
        '当前分类占比包含按权重估算的 token 口径。',
      )
    : null

  const hasTreemapNodes = Boolean(tm && tm.root.children.length > 0 && tm.reconciliation.retainedTokens > 0)

  if (hasTreemapNodes && tm) {
    for (const child of tm.root.children) {
      const compKey = mapTreemapToCompositionKey(child.category)
      const val = Math.max(0, Math.round(child.retainedTokens))
      bucketTokens[compKey] += val
      const prevDom = dominantTreemapCat.get(compKey)
      if (!prevDom || val > prevDom.tokens) {
        dominantTreemapCat.set(compKey, { cat: child.category, tokens: val })
      }
    }

    if (!tm.reconciliation.hasPricingData) {
      isEstimated = true
      estimationBasis = tr(
        'trajectoryWorkbench.composition.estimatedPackUniform',
        '宿主未返回逐源精确字节/token 计量，当前各分类按 ContextPack 来源均摊估算。',
      )
    }
  } else if (invs.length > 0 || execs.length > 0) {
    // Fallback when no ContextPack / Core compaction breakdown exists:
    // derive honest approximate weights from model invocations and tool output summaries.
    let latestInput = 0
    let sumOutput = 0
    for (const inv of invs) {
      if ((inv.input_tokens ?? 0) > latestInput) latestInput = inv.input_tokens ?? 0
      sumOutput += Math.max(0, inv.output_tokens ?? 0)
    }
    let toolOutputEst = 0
    for (const ex of execs) {
      const chars =
        (ex.summary?.length ?? 0)
        + (ex.arguments_summary?.length ?? 0)
        + (ex.evidence ?? []).reduce((acc, s) => acc + s.length, 0)
      toolOutputEst += Math.ceil(chars / 4)
    }

    if (latestInput > 0 || sumOutput > 0 || toolOutputEst > 0) {
      isEstimated = true
      estimationBasis = tr(
        'trajectoryWorkbench.composition.estimatedFromInvocations',
        '未捕获 ContextPack 逐源明细，当前构成按模型输入/输出 token 与工具摘要长度比例估算。',
      )
      const cappedTool = latestInput > 0 ? Math.min(toolOutputEst, Math.round(latestInput * 0.6)) : toolOutputEst
      const remainingInput = Math.max(0, latestInput - cappedTool)
      bucketTokens.system = Math.round(remainingInput * 0.28)
      bucketTokens.tools = Math.round(remainingInput * 0.24)
      bucketTokens.inject = Math.round(remainingInput * 0.14)
      bucketTokens.user = Math.max(0, remainingInput - bucketTokens.system - bucketTokens.tools - bucketTokens.inject)
      bucketTokens.assistant = sumOutput
      bucketTokens.tool_output = cappedTool
    }
  }

  const summedUsed = Object.values(bucketTokens).reduce((acc, v) => acc + v, 0)
  const usedTokens =
    hasTreemapNodes && tm
      ? Math.max(summedUsed, tm.reconciliation.retainedTokens)
      : summedUsed

  const explicitWindow =
    props.contextWindowTokens && props.contextWindowTokens > 0
      ? props.contextWindowTokens
      : tm?.reconciliation.tokenBudget && tm.reconciliation.tokenBudget > 0
        ? tm.reconciliation.tokenBudget
        : props.diagnostics?.compaction?.reconciliation?.token_budget && props.diagnostics.compaction.reconciliation.token_budget > 0
          ? props.diagnostics.compaction.reconciliation.token_budget
          : 0

  const maxTokens = explicitWindow > 0 ? Math.max(explicitWindow, usedTokens) : 0
  const usagePct = maxTokens > 0 ? Math.min(100, Math.round((usedTokens / maxTokens) * 1000) / 10) : null

  const segments: ContextCompositionSegment[] = CATEGORY_DEFS.map((def) => {
    const tokens = bucketTokens[def.key]
    const shareOfUsed = usedTokens > 0 ? Math.round((tokens / usedTokens) * 1000) / 10 : 0
    const shareOfWindow = maxTokens > 0 ? Math.round((tokens / maxTokens) * 1000) / 10 : shareOfUsed
    const dom = dominantTreemapCat.get(def.key)
    return {
      key: def.key,
      label: tr(def.labelKey, def.fallbackLabel),
      color: def.color,
      tokens,
      shareOfUsed,
      shareOfWindow,
      estimated: isEstimated && tokens > 0,
      treemapCategory: dom?.cat ?? def.defaultTreemapCategory,
    }
  })

  return {
    segments,
    usedTokens,
    maxTokens,
    usagePct,
    isEstimated,
    estimationBasis,
  }
})

const meterOccupiedWidthPct = computed<number>(() => {
  if (composition.value.usedTokens <= 0) return 0
  if (composition.value.maxTokens <= 0) return 100
  return Math.min(100, Math.max(1, (composition.value.usedTokens / composition.value.maxTokens) * 100))
})

function formatCompactTokens(tokens: number, estimated: boolean): string {
  const prefix = estimated && tokens > 0 ? '≈' : ''
  if (tokens <= 0) return '0'
  if (tokens < 1000) return `${prefix}${Math.round(tokens)}`
  const k = tokens / 1000
  return `${prefix}${k >= 100 ? k.toFixed(1) : k.toFixed(1)}k`
}

function isSegmentActive(seg: ContextCompositionSegment): boolean {
  if (!props.activeCategory) return false
  return props.activeCategory === seg.key || props.activeCategory === seg.treemapCategory
}
</script>

<template>
  <section class="ccb-root" data-testid="context-composition-bar">
    <header class="ccb-header">
      <div class="ccb-header__left">
        <strong class="ccb-title">{{ tr('trajectoryWorkbench.composition.title', '当前上下文') }}</strong>
        <span v-if="turnLabel" class="ccb-sub mono">{{ turnLabel }}</span>
        <span v-if="stale" class="ccb-stale-pill mono" data-testid="composition-cached-badge">
          <Database :size="10" aria-hidden="true" />
          <span>{{ tr('trajectoryEval.offline.cachedBadge', '离线快照') }}</span>
        </span>
      </div>
      <span v-if="resolvedModelLabel" class="ccb-model mono" :title="resolvedModelLabel">
        {{ resolvedModelLabel }}
      </span>
    </header>

    <div class="ccb-usage" data-testid="context-composition-usage">
      <div class="ccb-usage__tokens mono">
        <strong class="ccb-usage__used" data-testid="context-composition-used">
          {{ formatCompactTokens(composition.usedTokens, composition.isEstimated) }}
        </strong>
        <span class="ccb-usage__max">
          <template v-if="composition.maxTokens > 0">
            / {{ formatCompactTokens(composition.maxTokens, false) }} tokens
          </template>
          <template v-else>
            tokens
          </template>
        </span>
      </div>

      <div class="ccb-usage__right mono" data-testid="context-composition-pct">
        <template v-if="composition.usagePct !== null">
          <b class="ccb-usage__pct-num">{{ composition.usagePct.toFixed(1) }}%</b>
          <span class="ccb-usage__pct-label">{{ tr('trajectoryWorkbench.composition.usedSuffix', '已用') }}</span>
        </template>
        <span v-else class="ccb-usage__pct-label">
          {{ tr('trajectoryWorkbench.composition.unboundedSuffix', '已计量') }}
        </span>
      </div>
    </div>

    <div
      class="ccb-meter"
      role="img"
      :aria-label="tr('trajectoryWorkbench.composition.meterAria', '7 类上下文构成比例条')"
      data-testid="context-composition-meter"
    >
      <div
        v-if="composition.usedTokens > 0"
        class="ccb-meter__occupied"
        :style="{ width: `${meterOccupiedWidthPct}%` }"
      >
        <span
          v-for="seg in composition.segments.filter((s) => s.tokens > 0)"
          :key="seg.key"
          class="ccb-meter__seg"
          :data-category="seg.key"
          :data-muted="activeCategory && !isSegmentActive(seg) ? 'true' : undefined"
          :style="{
            width: `${seg.shareOfUsed}%`,
            backgroundColor: seg.color,
          }"
          :title="`${seg.label}: ${formatCompactTokens(seg.tokens, seg.estimated)} (${seg.shareOfUsed.toFixed(1)}%)`"
        />
      </div>
    </div>

    <div class="ccb-legend" role="list" data-testid="context-composition-legend">
      <button
        v-for="seg in composition.segments"
        :key="seg.key"
        type="button"
        role="listitem"
        class="ccb-legend__item"
        :class="{
          'is-zero': seg.tokens === 0,
          'is-active': isSegmentActive(seg),
        }"
        :data-testid="`context-category-${seg.key}`"
        @click="emit('select-category', seg.key, seg.treemapCategory)"
      >
        <i class="ccb-legend__dot" :style="{ backgroundColor: seg.color }" aria-hidden="true" />
        <span class="ccb-legend__name">{{ seg.label }}</span>
        <b class="ccb-legend__val mono">
          <span>{{ formatCompactTokens(seg.tokens, seg.estimated) }}</span>
          <span class="ccb-legend__pct">{{ seg.shareOfUsed.toFixed(1) }}%</span>
        </b>
      </button>
    </div>

    <p
      v-if="composition.isEstimated && composition.estimationBasis"
      class="ccb-footnote"
      data-testid="context-composition-footnote"
    >
      <span>≈ {{ composition.estimationBasis }}</span>
    </p>
  </section>
</template>

<style scoped>
.ccb-root {
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding: 8px 14px 10px;
  background: var(--bg-primary);
  border-bottom: 1px solid var(--border-muted);
  min-width: 0;
}

.ccb-header {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 4px 12px;
  flex-wrap: wrap;
}

.ccb-header__left {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
}

.ccb-title {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
  letter-spacing: -0.01em;
}

.ccb-sub {
  font-size: 11px;
  color: var(--text-muted);
}

.ccb-stale-pill {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 5px;
  border: 1px dashed var(--border-default);
  border-radius: 4px;
  font-size: 10px;
  color: var(--text-secondary);
}

.ccb-model {
  font-size: 11px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 200px;
}

.ccb-usage {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.ccb-usage__tokens {
  display: inline-flex;
  align-items: baseline;
  gap: 4px;
  min-width: 0;
}

.ccb-usage__used {
  font-size: 20px;
  line-height: 1.15;
  font-weight: 700;
  letter-spacing: -0.035em;
  color: var(--text-primary);
}

.ccb-usage__max {
  font-size: 11.5px;
  color: var(--text-muted);
}

.ccb-usage__right {
  display: inline-flex;
  align-items: baseline;
  gap: 4px;
  margin-left: auto;
}

.ccb-usage__pct-num {
  font-size: 14px;
  font-weight: 600;
  color: var(--text-primary);
}

.ccb-usage__pct-label {
  font-size: 11px;
  color: var(--text-muted);
}

.ccb-meter {
  height: 10px;
  width: 100%;
  border-radius: 5px;
  background: var(--bg-tertiary);
  overflow: hidden;
}

.ccb-meter__occupied {
  height: 100%;
  display: flex;
  overflow: hidden;
  border-radius: 5px;
  transition: width 160ms ease;
}

.ccb-meter__seg {
  height: 100%;
  flex: none;
  min-width: 2px;
  transition: width 160ms ease, opacity 120ms ease;
}

.ccb-meter__seg[data-muted='true'] {
  opacity: 0.22;
}

.ccb-legend {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 4px 12px;
  margin-top: 3px;
}

@container (min-width: 520px) {
  .ccb-legend {
    grid-template-columns: repeat(4, minmax(0, 1fr));
    gap: 5px 16px;
  }
}

@container (min-width: 860px) {
  .ccb-legend {
    grid-template-columns: repeat(7, minmax(0, 1fr));
    gap: 4px 14px;
  }
}

.ccb-legend__item {
  display: flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  padding: 2px 0;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font-size: 11.5px;
  line-height: 1.35;
  text-align: left;
  cursor: pointer;
}

.ccb-legend__item:hover .ccb-legend__name {
  text-decoration: underline;
  text-underline-offset: 3px;
}

.ccb-legend__item.is-zero {
  opacity: 0.46;
}

.ccb-legend__item.is-active .ccb-legend__name {
  font-weight: 600;
  text-decoration: underline;
  text-underline-offset: 3px;
}

.ccb-legend__dot {
  width: 8px;
  height: 8px;
  border-radius: 2px;
  flex: none;
}

.ccb-legend__name {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.ccb-legend__val {
  display: inline-flex;
  align-items: baseline;
  gap: 4px;
  margin-left: auto;
  font-size: 10.5px;
  font-weight: 400;
  color: var(--text-secondary);
  white-space: nowrap;
}

.ccb-legend__pct {
  color: var(--text-muted);
}

.ccb-footnote {
  margin: 2px 0 0;
  font-size: 10.5px;
  line-height: 1.4;
  color: var(--text-muted);
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}
</style>
