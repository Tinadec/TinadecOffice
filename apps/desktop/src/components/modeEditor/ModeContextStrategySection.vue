<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronDown, ChevronRight, Layers, RotateCcw, Sparkles, Zap } from '@lucide/vue'
import { UiBadge, UiInput, UiLabel, UiSelectField, UiSwitch } from '@/components/ui'
import {
  DEFAULT_CONTEXT_POLICY_PARAMS,
  type ContextPolicyParams,
  type ContextStrategyTier,
  type MemoryEvolutionMode,
  type RecallFidelity,
} from '@/lib/contextMemoryApi'

type ContextAdvancedInput = {
  [K in keyof ContextPolicyParams]?: ContextPolicyParams[K] | null
}

const props = withDefaults(
  defineProps<{
    strategy?: ContextStrategyTier | null
    advanced?: ContextAdvancedInput | null
    baselineStrategy?: ContextStrategyTier | null
    baselineAdvanced?: ContextAdvancedInput | null
    compact?: boolean
    readonly?: boolean
    allowInherit?: boolean
  }>(),
  {
    strategy: null,
    advanced: null,
    baselineStrategy: null,
    baselineAdvanced: null,
    compact: false,
    readonly: false,
    allowInherit: false,
  },
)

const emit = defineEmits<{
  'update:strategy': [value: ContextStrategyTier | null]
  'update:advanced': [value: Partial<ContextPolicyParams>]
  reset: []
}>()

const { t } = useI18n()

const advancedOpen = ref(false)

const STRATEGY_TIERS = computed(() => [
  {
    id: 'direct' as const,
    icon: Zap,
    label: t('memoryContext.strategy.direct.label'),
    desc: t('memoryContext.strategy.direct.desc'),
    recommended: false,
  },
  {
    id: 'managed' as const,
    icon: Layers,
    label: t('memoryContext.strategy.managed.label'),
    desc: t('memoryContext.strategy.managed.desc'),
    recommended: true,
  },
  {
    id: 'deep_curator' as const,
    icon: Sparkles,
    label: t('memoryContext.strategy.deep_curator.label'),
    desc: t('memoryContext.strategy.deep_curator.desc'),
    recommended: false,
  },
])

const ADVANCED_KEYS = Object.keys(DEFAULT_CONTEXT_POLICY_PARAMS) as Array<keyof ContextPolicyParams>

const normalizedStrategy = computed<ContextStrategyTier | null>(() =>
  props.strategy === 'direct' || props.strategy === 'managed' || props.strategy === 'deep_curator'
    ? props.strategy
    : null,
)

const normalizedBaselineStrategy = computed<ContextStrategyTier | null>(() =>
  props.baselineStrategy === 'direct' || props.baselineStrategy === 'managed' || props.baselineStrategy === 'deep_curator'
    ? props.baselineStrategy
    : null,
)

const effectiveStrategy = computed<ContextStrategyTier | null>(() => {
  if (normalizedStrategy.value) return normalizedStrategy.value
  if (normalizedBaselineStrategy.value) return normalizedBaselineStrategy.value
  return props.allowInherit ? null : 'managed'
})

const customizedAdvancedCount = computed(() => {
  let count = 0
  for (const key of ADVANCED_KEYS) {
    const val = props.advanced?.[key] ?? null
    const baseVal = props.baselineAdvanced?.[key] ?? null
    if (props.allowInherit) {
      if (val !== baseVal) count++
    } else if (val !== null) {
      count++
    }
  }
  return count
})

const isOverridden = computed(() => {
  if (!props.allowInherit) return false
  return normalizedStrategy.value !== normalizedBaselineStrategy.value || customizedAdvancedCount.value > 0
})

const mergedAdvanced = computed<ContextPolicyParams>(() => ({
  recall_fidelity:
    props.advanced?.recall_fidelity ??
    props.baselineAdvanced?.recall_fidelity ??
    DEFAULT_CONTEXT_POLICY_PARAMS.recall_fidelity,
  compaction_trigger_ratio:
    props.advanced?.compaction_trigger_ratio ??
    props.baselineAdvanced?.compaction_trigger_ratio ??
    DEFAULT_CONTEXT_POLICY_PARAMS.compaction_trigger_ratio,
  recent_message_limit:
    props.advanced?.recent_message_limit ??
    props.baselineAdvanced?.recent_message_limit ??
    DEFAULT_CONTEXT_POLICY_PARAMS.recent_message_limit,
  allow_cross_session_recall:
    props.advanced?.allow_cross_session_recall ??
    props.baselineAdvanced?.allow_cross_session_recall ??
    (effectiveStrategy.value === 'deep_curator' ? true : DEFAULT_CONTEXT_POLICY_PARAMS.allow_cross_session_recall),
  evolution_mode:
    props.advanced?.evolution_mode ??
    props.baselineAdvanced?.evolution_mode ??
    DEFAULT_CONTEXT_POLICY_PARAMS.evolution_mode,
  core_markdown_max_tokens:
    props.advanced?.core_markdown_max_tokens ??
    props.baselineAdvanced?.core_markdown_max_tokens ??
    DEFAULT_CONTEXT_POLICY_PARAMS.core_markdown_max_tokens,
}))

const fidelityOptions = computed(() => [
  { value: 'summary_only', label: t('memoryContext.advanced.fidelity.summary_only') },
  { value: 'balanced', label: t('memoryContext.advanced.fidelity.balanced') },
  { value: 'verbatim_first', label: t('memoryContext.advanced.fidelity.verbatim_first') },
])

const evolutionOptions = computed(() => [
  { value: 'auto_evolve', label: t('memoryContext.advanced.evolution.auto_evolve') },
  { value: 'candidate_only', label: t('memoryContext.advanced.evolution.candidate_only') },
])

function cleanAdvancedObject(input?: ContextAdvancedInput | null): Partial<ContextPolicyParams> {
  const out: Partial<ContextPolicyParams> = {}
  if (!input) return out
  if (input.recall_fidelity != null) out.recall_fidelity = input.recall_fidelity
  if (input.compaction_trigger_ratio != null) out.compaction_trigger_ratio = input.compaction_trigger_ratio
  if (input.recent_message_limit != null) out.recent_message_limit = input.recent_message_limit
  if (input.allow_cross_session_recall != null) out.allow_cross_session_recall = input.allow_cross_session_recall
  if (input.evolution_mode != null) out.evolution_mode = input.evolution_mode
  if (input.core_markdown_max_tokens != null) out.core_markdown_max_tokens = input.core_markdown_max_tokens
  return out
}

function selectStrategy(tier: ContextStrategyTier | null) {
  if (props.readonly) return
  if (props.allowInherit && tier !== null && normalizedStrategy.value === tier && normalizedBaselineStrategy.value === null) {
    emit('update:strategy', null)
    return
  }
  emit('update:strategy', tier)
}

function patchAdvanced<K extends keyof ContextPolicyParams>(key: K, value: ContextPolicyParams[K]) {
  if (props.readonly) return
  emit('update:advanced', {
    ...cleanAdvancedObject(props.advanced),
    [key]: value,
  })
}

function onCompactionRatioInput(raw: string | number) {
  const num = Number(raw)
  if (!Number.isFinite(num)) return
  const clamped = Math.round(Math.min(0.95, Math.max(0.4, num)) * 100) / 100
  patchAdvanced('compaction_trigger_ratio', clamped)
}

function onRecentLimitInput(raw: string | number) {
  const num = Number(raw)
  if (!Number.isFinite(num)) return
  patchAdvanced('recent_message_limit', Math.round(Math.min(200, Math.max(4, num))))
}

function onMaxTokensInput(raw: string | number) {
  const num = Number(raw)
  if (!Number.isFinite(num)) return
  patchAdvanced('core_markdown_max_tokens', Math.round(Math.min(8000, Math.max(256, num))))
}

function handleReset() {
  if (props.readonly) return
  if (!props.allowInherit) {
    emit('update:strategy', 'managed')
    emit('update:advanced', {})
  }
  emit('reset')
}

function stopInputKeys(event: KeyboardEvent) {
  if (event.key !== 'Escape') event.stopPropagation()
}
</script>

<template>
  <section
    :class="['ctx-strategy-section', { 'is-compact': compact, 'is-overridden': isOverridden }]"
    data-testid="mode-context-strategy-section"
    @keydown="stopInputKeys"
  >
    <header class="ctx-strategy-head">
      <div class="ctx-strategy-title-row">
        <span class="ctx-strategy-title">{{ t('memoryContext.strategy.title') }}</span>
        <span
          v-if="allowInherit"
          :class="['ctx-state-pill', isOverridden ? 'overridden' : 'inherited']"
          data-testid="context-strategy-state-pill"
        >
          {{ isOverridden ? t('memoryContext.strategy.overriddenBadge') : t('memoryContext.strategy.inheritBadge') }}
        </span>
      </div>
      <button
        v-if="(allowInherit && isOverridden && !readonly) || (!allowInherit && customizedAdvancedCount > 0 && !readonly)"
        type="button"
        class="ctx-reset-btn"
        data-testid="context-strategy-reset-btn"
        @click="handleReset"
      >
        <RotateCcw :size="11" aria-hidden="true" />
        <span>{{ t('memoryContext.strategy.resetBtn') }}</span>
      </button>
    </header>

    <!-- Compact segmented picker (ModeInspectorDrawer / ComposerCommandPanel) -->
    <template v-if="compact">
      <div class="ctx-segmented-bar" role="radiogroup" :aria-label="t('memoryContext.strategy.title')">
        <button
          v-if="allowInherit"
          type="button"
          role="radio"
          :aria-checked="effectiveStrategy === null"
          :disabled="readonly"
          :class="['ctx-segment-btn', { active: effectiveStrategy === null }]"
          data-testid="context-strategy-tier-inherit"
          @click="selectStrategy(null)"
        >
          {{ t('memoryContext.strategy.inheritOption') }}
        </button>
        <button
          v-for="tier in STRATEGY_TIERS"
          :key="tier.id"
          type="button"
          role="radio"
          :aria-checked="effectiveStrategy === tier.id"
          :disabled="readonly"
          :class="['ctx-segment-btn', { active: effectiveStrategy === tier.id }]"
          :data-testid="`context-strategy-tier-${tier.id}`"
          :title="tier.desc"
          @click="selectStrategy(tier.id)"
        >
          <span>{{ tier.label }}</span>
        </button>
      </div>
      <p v-if="effectiveStrategy" class="ctx-compact-desc">
        {{ STRATEGY_TIERS.find((item) => item.id === effectiveStrategy)?.desc }}
      </p>
    </template>

    <!-- Full 3-card picker (Settings Policy & TOML / full width editors) -->
    <div
      v-else
      class="ctx-cards-grid"
      role="radiogroup"
      :aria-label="t('memoryContext.strategy.title')"
      data-testid="context-strategy-cards"
    >
      <button
        v-for="tier in STRATEGY_TIERS"
        :key="tier.id"
        type="button"
        role="radio"
        :aria-checked="effectiveStrategy === tier.id"
        :disabled="readonly"
        :class="['ctx-tier-card', { active: effectiveStrategy === tier.id }]"
        :data-testid="`context-strategy-tier-${tier.id}`"
        @click="selectStrategy(tier.id)"
      >
        <div class="ctx-tier-card-top">
          <span class="ctx-tier-icon">
            <component :is="tier.icon" :size="15" aria-hidden="true" />
          </span>
          <strong class="ctx-tier-label">{{ tier.label }}</strong>
          <UiBadge v-if="tier.recommended" variant="secondary" class="ctx-recommended-badge">
            {{ t('memoryContext.strategy.recommended') }}
          </UiBadge>
        </div>
        <p class="ctx-tier-desc">{{ tier.desc }}</p>
      </button>
    </div>

    <!-- Collapsible Advanced Context & Evolution Parameters -->
    <div class="ctx-advanced-box">
      <button
        type="button"
        class="ctx-advanced-trigger"
        :aria-expanded="advancedOpen"
        data-testid="context-advanced-toggle"
        @click="advancedOpen = !advancedOpen"
      >
        <component :is="advancedOpen ? ChevronDown : ChevronRight" :size="14" aria-hidden="true" />
        <span>{{ t('memoryContext.advanced.title') }}</span>
        <UiBadge v-if="customizedAdvancedCount > 0" variant="outline" class="ctx-custom-count">
          {{ t('memoryContext.advanced.customizedCount', { count: customizedAdvancedCount }) }}
        </UiBadge>
      </button>

      <div v-if="advancedOpen" class="ctx-advanced-grid" data-testid="context-advanced-panel">
        <div class="ctx-field">
          <UiLabel>{{ t('memoryContext.advanced.recallFidelity') }}</UiLabel>
          <UiSelectField
            :model-value="mergedAdvanced.recall_fidelity"
            :disabled="readonly"
            :options="fidelityOptions"
            data-testid="adv-recall-fidelity"
            @update:model-value="(val: string) => patchAdvanced('recall_fidelity', val as RecallFidelity)"
          />
        </div>

        <div class="ctx-field">
          <div class="ctx-field-label-row">
            <UiLabel>{{ t('memoryContext.advanced.compactionRatio') }}</UiLabel>
            <span class="ctx-mono-val">{{ Math.round(mergedAdvanced.compaction_trigger_ratio * 100) }}% ({{ mergedAdvanced.compaction_trigger_ratio.toFixed(2) }})</span>
          </div>
          <input
            type="range"
            min="0.40"
            max="0.95"
            step="0.05"
            class="ctx-range-input"
            :disabled="readonly"
            :value="mergedAdvanced.compaction_trigger_ratio"
            :aria-label="t('memoryContext.advanced.compactionRatio')"
            data-testid="adv-compaction-ratio"
            @input="onCompactionRatioInput(($event.target as HTMLInputElement).value)"
          />
        </div>

        <div class="ctx-field">
          <UiLabel>{{ t('memoryContext.advanced.recentMessageLimit') }}</UiLabel>
          <UiInput
            type="number"
            min="4"
            max="200"
            :disabled="readonly"
            :model-value="String(mergedAdvanced.recent_message_limit)"
            data-testid="adv-recent-message-limit"
            @update:model-value="onRecentLimitInput"
          />
        </div>

        <div class="ctx-field">
          <UiLabel>{{ t('memoryContext.advanced.coreMarkdownMaxTokens') }}</UiLabel>
          <UiInput
            type="number"
            min="256"
            max="8000"
            step="100"
            :disabled="readonly"
            :model-value="String(mergedAdvanced.core_markdown_max_tokens)"
            data-testid="adv-core-markdown-max-tokens"
            @update:model-value="onMaxTokensInput"
          />
        </div>

        <div class="ctx-field">
          <UiLabel>{{ t('memoryContext.advanced.evolutionMode') }}</UiLabel>
          <UiSelectField
            :model-value="mergedAdvanced.evolution_mode"
            :disabled="readonly"
            :options="evolutionOptions"
            data-testid="adv-evolution-mode"
            @update:model-value="(val: string) => patchAdvanced('evolution_mode', val as MemoryEvolutionMode)"
          />
        </div>

        <div class="ctx-field ctx-switch-row">
          <UiLabel>{{ t('memoryContext.advanced.allowCrossSession') }}</UiLabel>
          <UiSwitch
            :model-value="mergedAdvanced.allow_cross_session_recall"
            :disabled="readonly"
            data-testid="adv-allow-cross-session"
            @update:model-value="(val: boolean) => patchAdvanced('allow_cross_session_recall', Boolean(val))"
          />
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.ctx-strategy-section {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.ctx-strategy-section.is-compact {
  gap: 8px;
}

.ctx-strategy-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.ctx-strategy-title-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.ctx-strategy-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.ctx-state-pill {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 500;
  white-space: nowrap;
}

.ctx-state-pill.inherited {
  background: var(--surface-hover);
  color: var(--text-muted);
}

.ctx-state-pill.overridden {
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
}

.ctx-reset-btn {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 4px;
  min-height: var(--control-height-xs, 26px);
  padding: 2px 8px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  white-space: nowrap;
  cursor: pointer;
}

.ctx-reset-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.ctx-reset-btn:focus-visible,
.ctx-tier-card:focus-visible,
.ctx-segment-btn:focus-visible,
.ctx-advanced-trigger:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.ctx-cards-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(210px, 1fr));
  gap: 10px;
}

.ctx-tier-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
  padding: 12px 14px;
  text-align: left;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  cursor: pointer;
  transition: border-color 0.15s ease, background-color 0.15s ease;
}

.ctx-tier-card:hover:not(:disabled) {
  background: var(--surface-hover);
}

.ctx-tier-card.active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 10%, var(--surface-raised));
}

.ctx-tier-card:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.ctx-tier-card-top {
  display: flex;
  align-items: center;
  gap: 7px;
  width: 100%;
  flex-wrap: wrap;
}

.ctx-tier-icon {
  display: inline-flex;
  color: var(--accent-primary);
}

.ctx-tier-label {
  font-size: 13px;
  font-weight: 600;
}

.ctx-recommended-badge {
  margin-left: auto;
  font-size: 10px;
}

.ctx-tier-desc {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-secondary);
}

.ctx-segmented-bar {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  padding: 3px;
  border-radius: 8px;
  background: var(--surface-input);
  border: 1px solid var(--border-muted);
}

.ctx-segment-btn {
  flex: 1 1 auto;
  min-width: 72px;
  min-height: var(--control-height-xs, 26px);
  padding: 4px 8px;
  border: 0;
  border-radius: var(--radius-control, 6px);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  text-align: center;
}

.ctx-segment-btn:hover:not(:disabled) {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.ctx-segment-btn.active {
  background: var(--surface-selected);
  color: var(--accent-primary);
  font-weight: 600;
}

.ctx-segment-btn:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.ctx-compact-desc {
  margin: 0;
  font-size: 11px;
  line-height: 1.45;
  color: var(--text-muted);
}

.ctx-advanced-box {
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  overflow: hidden;
}

.ctx-advanced-trigger {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  text-align: left;
  cursor: pointer;
}

.ctx-advanced-trigger:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.ctx-custom-count {
  margin-left: auto;
  font-size: 10px;
}

.ctx-advanced-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 10px 14px;
  padding: 10px 12px 12px;
  border-top: 1px solid var(--border-muted);
}

.ctx-strategy-section.is-compact .ctx-advanced-grid {
  grid-template-columns: 1fr;
  gap: 8px;
}

.ctx-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.ctx-field-label-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.ctx-mono-val {
  font-family: var(--font-mono);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  color: var(--text-secondary);
}

.ctx-range-input {
  width: 100%;
  accent-color: var(--accent-primary);
  cursor: pointer;
}

.ctx-switch-row {
  flex-direction: row;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding-top: 4px;
}

@media (prefers-reduced-motion: reduce) {
  .ctx-tier-card {
    transition: none;
  }
}
</style>
