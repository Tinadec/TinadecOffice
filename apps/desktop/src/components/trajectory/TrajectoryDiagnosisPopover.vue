<script lang="ts">
import type { TrajectoryNode, TrajectorySummary } from '@/lib/trajectoryBuilder'

export type DiagnosisPresetKey = 'progress' | 'stuck' | 'custom'

export function composePresetDraft(
  preset: DiagnosisPresetKey,
  ctx: {
    runId: string | null
    sessionId: string | null
    turnNo: number | null | undefined
    turnLabel: string | null | undefined
    stepNo: number | null | undefined
    node: TrajectoryNode | null | undefined
    trajectory: TrajectorySummary | null | undefined
    staleCachedAt: string | null | undefined
  },
): string {
  const lines: string[] = []
  const node = ctx.node

  if (preset === 'progress') {
    lines.push(
      '请分析「这一步推进了什么」：结合工具调用结果、文件变更与上下文产出，归纳本步已验证的事实、推进的结论与下一步衔接点。',
    )
  } else if (preset === 'stuck') {
    lines.push(
      '请分析「为什么卡在这一步」：检查工具反馈、前置观察条件（如文件观测/版本锁/写范围）、重复参数重试与治理阻断信号，定位具体根因并给出最小修复建议。',
    )
  } else {
    lines.push('请基于下方轨迹定位证据回答我的问题：\n> ')
  }

  lines.push('', '### 轨迹定位证据 (Evidence)')
  if (ctx.runId) lines.push(`- **run_id**: \`${ctx.runId}\``)
  if (ctx.sessionId) lines.push(`- **session_id**: \`${ctx.sessionId}\``)
  if (ctx.turnNo !== null && ctx.turnNo !== undefined) {
    lines.push(`- **turn**: \`#${ctx.turnNo}\`${ctx.turnLabel ? ` (${ctx.turnLabel})` : ''}`)
  } else if (ctx.turnLabel) {
    lines.push(`- **turn**: ${ctx.turnLabel}`)
  }
  if (ctx.stepNo !== null && ctx.stepNo !== undefined) {
    lines.push(
      `- **step**: \`#${ctx.stepNo}\`${node ? ` — \`${node.id}\` (${node.label} [${node.status}])` : ''}`,
    )
  } else if (node) {
    lines.push(`- **step**: \`${node.id}\` (${node.kind}) — ${node.label} [${node.status}]`)
  }

  if (node?.toolId) lines.push(`- **tool_id**: \`${node.toolId}\``)
  if (node?.resourcePath) lines.push(`- **resource_path**: \`${node.resourcePath}\``)
  if (node?.command) lines.push(`- **command**: \`${node.command}\``)
  if (node?.durationMs !== null && node?.durationMs !== undefined) {
    lines.push(`- **duration_ms**: ${node.durationMs}`)
  }
  if (node?.editStats) {
    lines.push(`- **file_diff**: +${node.editStats.added} / -${node.editStats.removed}`)
  }

  const nodeSignals = node?.signals ?? []
  const activeRunSignals = ctx.trajectory?.activeSignals ?? []
  if (nodeSignals.length > 0) {
    const formatted = nodeSignals
      .map((s) => (s.count && s.count > 1 ? `${s.code} (x${s.count})` : s.code))
      .join(', ')
    lines.push(`- **governance_signals**: ${formatted}`)
  } else if (activeRunSignals.length > 0) {
    lines.push(`- **governance_signals**: ${activeRunSignals.join(', ')}`)
  } else {
    lines.push('- **governance_signals**: none')
  }

  if (ctx.staleCachedAt) {
    lines.push(`- **snapshot_source**: cached (${ctx.staleCachedAt})`)
  }

  if (node?.summary) {
    lines.push(`- **outcome_summary**: ${node.summary}`)
  }

  const frames = node?.codeFrames ?? []
  if (frames.length > 0) {
    lines.push('', '### 报错代码帧 (Error Frame)')
    const frame = frames[0]!
    lines.push(`\`${frame.title}\`${frame.errorCategory ? ` (${frame.errorCategory})` : ''}`)
    lines.push('```text')
    for (const fl of frame.lines.slice(0, 6)) {
      const ln = fl.lineNumber !== null && fl.lineNumber !== undefined ? `${fl.lineNumber} | ` : ''
      lines.push(`${fl.highlight ? '> ' : '  '}${ln}${fl.text}`)
    }
    lines.push('```')
  }

  lines.push(
    '',
    '请先使用只读 `tinadec_inspect`（按需查询 `run_diagnostics`、`context_audit` 或相关配置）核实实时状态，再给出有证据支撑的结论。',
  )

  return lines.join('\n')
}
</script>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Copy, Sparkles, X } from '@lucide/vue'
import type { ContextEvolutionProposalDto, RunContextDiagnosticsDto } from '@/api'
import type { ContextTreemapModel } from '@/lib/contextTreemapLayout'
import { triggerDiagnoseInComposer } from '@/lib/offlineDiagnosticReport'
import {
  resolveTrajectoryCopy,
  type ContextWindowBucketReport,
} from '@/lib/trajectoryBuilder'

const props = withDefaults(defineProps<{
  open: boolean
  runId?: string | null
  sessionId?: string | null
  turnNo?: number | null
  turnLabel?: string | null
  stepNo?: number | null
  node?: TrajectoryNode | null
  trajectory?: TrajectorySummary | null
  treemap?: ContextTreemapModel | null
  buckets?: ContextWindowBucketReport | null
  diagnostics?: RunContextDiagnosticsDto | null
  staleCachedAt?: string | null
  initialPreset?: DiagnosisPresetKey | null
}>(), {
  runId: null,
  sessionId: null,
  turnNo: null,
  turnLabel: null,
  stepNo: null,
  node: null,
  trajectory: null,
  treemap: null,
  buckets: null,
  diagnostics: null,
  staleCachedAt: null,
  initialPreset: null,
})

const emit = defineEmits<{
  close: []
  injected: [payload: { draft: string; preset: DiagnosisPresetKey }]
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  return resolveTrajectoryCopy(t, te, key, fallback, params)
}

const activePreset = ref<DiagnosisPresetKey>('progress')
const draftText = ref('')
const copied = ref(false)
const submitting = ref(false)

const resolvedRunId = computed<string | null>(
  () => props.runId ?? props.diagnostics?.run_id ?? props.trajectory?.runId ?? null,
)

const resolvedSessionId = computed<string | null>(
  () => props.sessionId ?? props.diagnostics?.session_id ?? props.trajectory?.sessionId ?? null,
)

const headingTitle = computed<string>(() => {
  const parts: string[] = []
  if (props.turnNo !== null && props.turnNo !== undefined) {
    parts.push(tr('trajectoryPath.diagnosis.turnIndex', '第 {n} 轮', { n: props.turnNo }))
  } else if (props.turnLabel) {
    parts.push(props.turnLabel)
  }
  if (props.stepNo !== null && props.stepNo !== undefined) {
    parts.push(tr('trajectoryPath.diagnosis.stepIndex', '第 {n} 步', { n: props.stepNo }))
  } else if (props.node) {
    parts.push(props.node.label)
  }
  return parts.length > 0
    ? parts.join(' · ')
    : tr('trajectoryPath.diagnosis.defaultTitle', '当前执行轨迹')
})

function defaultPresetForNode(node: TrajectoryNode | null | undefined): DiagnosisPresetKey {
  if (props.initialPreset) return props.initialPreset
  if (!node) return 'progress'
  if (node.isError || node.signals.length > 0) return 'stuck'
  return 'progress'
}

function regenerateDraft(preset: DiagnosisPresetKey): void {
  activePreset.value = preset
  draftText.value = composePresetDraft(preset, {
    runId: resolvedRunId.value,
    sessionId: resolvedSessionId.value,
    turnNo: props.turnNo,
    turnLabel: props.turnLabel,
    stepNo: props.stepNo,
    node: props.node,
    trajectory: props.trajectory,
    staleCachedAt: props.staleCachedAt,
  })
}

watch(
  () => [props.open, props.node?.id, props.turnNo, props.stepNo, props.runId] as const,
  ([isOpen]) => {
    if (isOpen) {
      copied.value = false
      regenerateDraft(defaultPresetForNode(props.node))
    }
  },
  { immediate: true },
)

async function handleInjectComposer(): Promise<void> {
  if (submitting.value) return
  submitting.value = true
  try {
    const finalDraft = draftText.value.trim()
    if (!finalDraft) return
    const now = new Date().toISOString()
    const customProposalCarrier: ContextEvolutionProposalDto = {
      id: 'trajectory-diagnosis-popover',
      kind: 'trajectory_diagnosis',
      status: 'draft',
      target_scope: 'run',
      title: headingTitle.value,
      summary: activePreset.value,
      rationale: '',
      confidence: 1,
      risk_level: 'low',
      requires_human_approval: false,
      evidence_run_ids: resolvedRunId.value ? [resolvedRunId.value] : [],
      composer_prompt: finalDraft,
      created_at: now,
      updated_at: now,
    }
    await triggerDiagnoseInComposer({
      runId: resolvedRunId.value,
      sessionId: resolvedSessionId.value,
      proposal: customProposalCarrier,
    })
    emit('injected', { draft: finalDraft, preset: activePreset.value })
    emit('close')
  } finally {
    submitting.value = false
  }
}

async function handleCopyDraft(): Promise<void> {
  try {
    if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(draftText.value)
      copied.value = true
      setTimeout(() => {
        copied.value = false
      }, 1500)
    }
  } catch {
    // Ignore clipboard restrictions in non-secure/test contexts.
  }
}
</script>

<template>
  <div
    v-if="open"
    class="tdp-popover"
    role="dialog"
    :aria-label="headingTitle"
    data-testid="trajectory-diagnosis-popover"
    @keydown.esc.stop="emit('close')"
  >
    <header class="tdp-head">
      <div class="tdp-head__titles">
        <span class="tdp-eyebrow">{{ tr('trajectoryPath.diagnosis.eyebrow', '诊断草稿') }}</span>
        <strong class="tdp-title" data-testid="diagnosis-popover-title">{{ headingTitle }}</strong>
      </div>
      <button
        type="button"
        class="tdp-close"
        :aria-label="tr('trajectoryEval.actions.close', '关闭')"
        data-testid="diagnosis-popover-close"
        @click="emit('close')"
      >
        <X :size="14" />
      </button>
    </header>

    <div
      class="tdp-presets"
      role="tablist"
      :aria-label="tr('trajectoryPath.diagnosis.presetsLabel', '预设诊断角度')"
    >
      <button
        type="button"
        role="tab"
        class="tdp-chip"
        :class="{ 'is-active': activePreset === 'progress' }"
        :aria-selected="activePreset === 'progress'"
        data-testid="diagnosis-preset-progress"
        @click="regenerateDraft('progress')"
      >
        {{ tr('trajectoryPath.diagnosis.presetProgress', '这一步推进了什么') }}
      </button>
      <button
        type="button"
        role="tab"
        class="tdp-chip"
        :class="{ 'is-active': activePreset === 'stuck' }"
        :aria-selected="activePreset === 'stuck'"
        data-testid="diagnosis-preset-stuck"
        @click="regenerateDraft('stuck')"
      >
        {{ tr('trajectoryPath.diagnosis.presetStuck', '为什么卡在这一步') }}
      </button>
      <button
        type="button"
        role="tab"
        class="tdp-chip"
        :class="{ 'is-active': activePreset === 'custom' }"
        :aria-selected="activePreset === 'custom'"
        data-testid="diagnosis-preset-custom"
        @click="regenerateDraft('custom')"
      >
        {{ tr('trajectoryPath.diagnosis.presetCustom', '自己问') }}
      </button>
    </div>

    <textarea
      v-model="draftText"
      rows="8"
      class="tdp-textarea mono"
      :aria-label="tr('trajectoryPath.diagnosis.draftAria', '可编辑诊断草稿')"
      data-testid="diagnosis-draft-textarea"
    />

    <footer class="tdp-foot">
      <button
        type="button"
        class="tdp-btn tdp-btn--primary"
        :disabled="submitting || !draftText.trim()"
        data-testid="diagnosis-inject-btn"
        @click="handleInjectComposer"
      >
        <Sparkles :size="12" aria-hidden="true" />
        <span>{{ tr('trajectoryPath.diagnosis.injectComposer', '填入对话') }}</span>
      </button>
      <button
        type="button"
        class="tdp-btn tdp-btn--secondary"
        data-testid="diagnosis-copy-btn"
        @click="handleCopyDraft"
      >
        <Check v-if="copied" :size="12" aria-hidden="true" />
        <Copy v-else :size="12" aria-hidden="true" />
        <span>{{ copied ? tr('trajectoryPath.diagnosis.copied', '已复制') : tr('trajectoryPath.diagnosis.copyDraft', '复制草稿') }}</span>
      </button>
    </footer>
  </div>
</template>

<style scoped>
.tdp-popover {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px 16px;
  border: 1px solid var(--material-floating-border, var(--border-default));
  border-radius: 12px;
  background: var(--material-floating-bg, var(--surface-raised, var(--bg-primary)));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 12px 36px rgba(0, 0, 0, 0.14);
  width: min(440px, 100%);
  min-width: 0;
  z-index: 24;
}

.tdp-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
}

.tdp-head__titles {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.tdp-eyebrow {
  font-size: 11px;
  color: var(--text-muted);
}

.tdp-title {
  font-size: 15px;
  line-height: 1.3;
  font-weight: 600;
  letter-spacing: -0.02em;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.tdp-close {
  width: 26px;
  height: 26px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  flex: none;
}

.tdp-close:hover {
  background: var(--surface-hover, var(--bg-hover));
  color: var(--text-primary);
}

.tdp-presets {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.tdp-chip {
  height: 27px;
  padding: 0 10px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-button, var(--bg-primary));
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  transition: border-color 110ms ease, background-color 110ms ease, color 110ms ease;
}

.tdp-chip:hover {
  border-color: var(--border-default);
  color: var(--text-primary);
  background: var(--surface-button-hover, var(--bg-secondary));
}

.tdp-chip.is-active {
  border-color: var(--text-primary);
  background: var(--surface-selected, var(--bg-selected));
  color: var(--text-primary);
  font-weight: 600;
}

.tdp-textarea {
  width: 100%;
  min-height: 148px;
  padding: 9px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-input, var(--bg-secondary));
  color: var(--text-primary);
  font-size: 11.5px;
  line-height: 1.5;
  resize: vertical;
}

.tdp-textarea:focus {
  outline: none;
  border-color: var(--text-primary);
  background: var(--surface-input, var(--bg-primary));
}

.tdp-foot {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.tdp-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 29px;
  padding: 0 12px;
  border-radius: 6px;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.tdp-btn--primary {
  border: 1px solid var(--text-primary);
  background: var(--text-primary);
  color: var(--text-on-primary, var(--bg-primary));
}

.tdp-btn--primary:hover:not(:disabled) {
  opacity: 0.9;
}

.tdp-btn--primary:disabled {
  opacity: 0.45;
  cursor: default;
}

.tdp-btn--secondary {
  border: 1px solid var(--border-muted);
  background: var(--surface-button, var(--bg-primary));
  color: var(--text-primary);
}

.tdp-btn--secondary:hover {
  border-color: var(--border-default);
  background: var(--surface-button-hover, var(--bg-secondary));
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}
</style>
