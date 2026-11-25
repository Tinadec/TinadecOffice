<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowUpRight, Terminal } from '@lucide/vue'
import type {
  ContextEvolutionProposalDto,
  RunContextDiagnosticsDto,
  ToolExecutionTimelineItemDto,
} from '@/api'
import { triggerDiagnoseInComposer } from '@/lib/offlineDiagnosticReport'
import {
  resolveTrajectoryCopy,
  type TrajectorySummary,
} from '@/lib/trajectoryBuilder'
import {
  aggregateCodeExecutionSignals,
  buildCodeSignalGroupDraft,
  type CodeExecutionInvocation,
  type CodeExecutorFilter,
  type CodeFailureSignalSlug,
  type CodeRemediationDraft,
  type CodeSignalGroup,
} from './trajectoryCodeSignals'

const props = withDefaults(defineProps<{
  trajectory: TrajectorySummary
  runId?: string | null
  sessionId?: string | null
  toolExecutions?: ToolExecutionTimelineItemDto[]
  diagnostics?: RunContextDiagnosticsDto | null
  focusTurnNo?: number | null
}>(), {
  runId: null,
  sessionId: null,
  toolExecutions: () => [],
  diagnostics: null,
  focusTurnNo: null,
})

const emit = defineEmits<{
  'diagnose-draft': [draft: string]
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  return resolveTrajectoryCopy(t, te, key, fallback, params)
}

const executorFilter = ref<CodeExecutorFilter>('all')
const selectedGroupSlug = ref<CodeFailureSignalSlug | null>(null)
const selectedCallId = ref<string | null>(null)
const showAllRecordsDrawer = ref<boolean>(false)

const report = computed(() =>
  aggregateCodeExecutionSignals({
    trajectory: props.trajectory,
    toolExecutions: props.toolExecutions,
    diagnostics: props.diagnostics,
    runId: props.runId,
    sessionId: props.sessionId,
    executorFilter: executorFilter.value,
  }),
)

const activeGroup = computed<CodeSignalGroup | null>(() => {
  const groups = report.value.signalGroups
  if (groups.length === 0) return null
  if (selectedGroupSlug.value) {
    const matched = groups.find((g) => g.slug === selectedGroupSlug.value)
    if (matched) return matched
  }
  return groups[0] ?? null
})

const activeInvocation = computed<CodeExecutionInvocation | null>(() => {
  const grp = activeGroup.value
  if (!grp || grp.items.length === 0) return null
  if (selectedCallId.value) {
    const matched = grp.items.find((item) => item.id === selectedCallId.value)
    if (matched) return matched
  }
  return grp.items[0] ?? null
})

// Sync selection when signal groups change
watch(
  () => report.value.signalGroups,
  (groups) => {
    if (groups.length === 0) {
      selectedGroupSlug.value = null
      selectedCallId.value = null
      return
    }
    const currentGroup = groups.find((g) => g.slug === selectedGroupSlug.value) ?? groups[0]!
    selectedGroupSlug.value = currentGroup.slug
    if (!currentGroup.items.some((i) => i.id === selectedCallId.value)) {
      selectedCallId.value = currentGroup.items[0]?.id ?? null
    }
  },
  { immediate: true },
)

// Deep-link focus by turnNo (e.g. jumping from Eval scatter / turn compare)
watch(
  () => props.focusTurnNo,
  (turnNo) => {
    if (!turnNo || turnNo <= 0) return
    const matchingFailed = report.value.failedInvocations.find((i) => i.turnNo === turnNo)
    if (matchingFailed && matchingFailed.signalSlug) {
      selectedGroupSlug.value = matchingFailed.signalSlug
      selectedCallId.value = matchingFailed.id
    }
  },
  { immediate: true },
)

function selectGroup(group: CodeSignalGroup): void {
  selectedGroupSlug.value = group.slug
  selectedCallId.value = group.items[0]?.id ?? null
}

function selectInvocation(item: CodeExecutionInvocation): void {
  selectedCallId.value = item.id
}

function formatDurationMs(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms) || ms < 0) return '—'
  if (ms < 1000) return `${Math.round(ms)}ms`
  return `${(ms / 1000).toFixed(1)}s`
}

function resolveSignalTitle(group: { i18nKey: string; label: string }): string {
  return tr(group.i18nKey, group.label)
}

async function dispatchDraftToComposer(title: string, summaryKey: string, draft: string): Promise<void> {
  const trimmed = draft.trim()
  if (!trimmed) return
  const now = new Date().toISOString()
  const resolvedRunId = report.value.runId
  const resolvedSessionId = report.value.sessionId
  const proposal: ContextEvolutionProposalDto = {
    id: `code-exec-${summaryKey}`,
    kind: 'trajectory_diagnosis',
    status: 'draft',
    target_scope: 'run',
    title,
    summary: summaryKey,
    rationale: '',
    confidence: 1,
    risk_level: 'low',
    requires_human_approval: false,
    evidence_run_ids: resolvedRunId ? [resolvedRunId] : [],
    composer_prompt: trimmed,
    created_at: now,
    updated_at: now,
  }
  await triggerDiagnoseInComposer({
    runId: resolvedRunId,
    sessionId: resolvedSessionId,
    proposal,
  })
  emit('diagnose-draft', trimmed)
}

function handleRemediationClick(rem: CodeRemediationDraft): void {
  const call = activeInvocation.value
  const title = call
    ? `${tr(rem.i18nKey, rem.label)} · #${call.turnNo} ${call.toolId}`
    : tr(rem.i18nKey, rem.label)
  void dispatchDraftToComposer(title, rem.key, rem.draft)
}

function handleDiagnoseSingleCall(item: CodeExecutionInvocation): void {
  const primaryRem = item.remediations[0]
  if (!primaryRem) return
  void dispatchDraftToComposer(
    `${item.signalLabel ?? item.toolId} · #${item.turnNo}`,
    `call-${item.id}`,
    primaryRem.draft,
  )
}

function handleAnalyzeGroup(group: CodeSignalGroup): void {
  const draft = buildCodeSignalGroupDraft(group, {
    runId: report.value.runId,
    sessionId: report.value.sessionId,
  })
  void dispatchDraftToComposer(
    `${resolveSignalTitle(group)} (${group.count})`,
    `group-${group.slug}`,
    draft,
  )
}
</script>

<template>
  <div class="tce-root obs-geist-surface" data-testid="trajectory-code-panel">
    <!-- 1. Top Header Row: Title/Subtitle + Executor Filter Select -->
    <header class="tce-header">
      <div class="tce-header__titles">
        <h3 class="tce-title">
          {{ tr('trajectoryCode.title', '代码执行分析') }}
        </h3>
        <p class="tce-subtitle">
          {{ tr('trajectoryCode.subtitle', '从具体失败信号，核对代码、执行链和后续结果。') }}
        </p>
      </div>

      <div class="tce-header__controls">
        <select
          v-model="executorFilter"
          class="tce-select"
          :aria-label="tr('trajectoryCode.executorFilterAria', '按执行器筛选命令调用')"
          data-testid="code-executor-filter"
        >
          <option value="all">{{ tr('trajectoryCode.executors.all', '全部') }}</option>
          <option value="PowerShell">{{ tr('trajectoryCode.executors.pwsh', 'PowerShell') }}</option>
          <option value="Bash">{{ tr('trajectoryCode.executors.bash', 'Bash') }}</option>
          <option value="Terminal">{{ tr('trajectoryCode.executors.terminal', '终端') }}</option>
          <option value="Other">{{ tr('trajectoryCode.executors.other', '其他') }}</option>
        </select>
      </div>
    </header>

    <!-- 2. 3 Large Geist KPI Numbers -->
    <div class="tce-kpi-strip" data-testid="code-kpi-strip">
      <div class="tce-kpi-cell">
        <span class="tce-kpi-label">{{ tr('trajectoryCode.kpi.totalCalls', '命令与程序调用') }}</span>
        <strong class="tce-kpi-value mono" data-testid="code-kpi-total">{{ report.totalCalls }}</strong>
      </div>
      <div class="tce-kpi-cell">
        <span class="tce-kpi-label">{{ tr('trajectoryCode.kpi.errorRecords', '报错记录') }}</span>
        <strong
          class="tce-kpi-value mono"
          :class="{ 'is-danger': report.errorCount > 0 }"
          data-testid="code-kpi-errors"
        >
          {{ report.errorCount }}
        </strong>
      </div>
      <div class="tce-kpi-cell">
        <span class="tce-kpi-label">{{ tr('trajectoryCode.kpi.detectedSignals', '识别到具体信号') }}</span>
        <strong class="tce-kpi-value mono" data-testid="code-kpi-signals">
          {{ report.detectedSignalCount }}
        </strong>
      </div>
    </div>

    <!-- 3A. Empty State when 0 command/code executions exist -->
    <div
      v-if="report.totalCalls === 0"
      class="tce-empty"
      data-testid="trajectory-code-empty"
    >
      <Terminal :size="15" aria-hidden="true" />
      <span>{{ tr('trajectoryCode.empty', '当前回合未记录终端命令或代码执行调用。') }}</span>
    </div>

    <!-- 3B. Failure Signal Split View (Left/Top Signal Groups + Right/Bottom Detail) -->
    <div
      v-else-if="report.signalGroups.length > 0"
      class="tce-split"
      data-testid="code-failure-split"
    >
      <!-- Left / Top Signal Group List -->
      <aside class="tce-sidebar" aria-label="失败信号分组">
        <div class="tce-section-eyebrow">
          {{ tr('trajectoryCode.failureSignalsHeading', '失败信号') }}
        </div>

        <div class="tce-group-list" role="listbox">
          <button
            v-for="group in report.signalGroups"
            :key="group.slug"
            type="button"
            role="option"
            class="tce-group-item"
            :class="{ 'is-active': activeGroup?.slug === group.slug }"
            :aria-selected="activeGroup?.slug === group.slug"
            :data-testid="`code-signal-group-${group.slug}`"
            @click="selectGroup(group)"
          >
            <div class="tce-group-item__top">
              <span class="tce-group-item__label">{{ resolveSignalTitle(group) }}</span>
              <span class="tce-group-item__count mono">{{ group.count }}</span>
            </div>
            <span class="tce-group-item__sub">{{ group.subtitle }}</span>
          </button>
        </div>
      </aside>

      <!-- Right / Bottom Selected Group & Invocation Detail -->
      <div v-if="activeGroup && activeInvocation" class="tce-detail">
        <!-- Group Heading & Same-Signal Calls Table -->
        <section class="tce-group-section">
          <div class="tce-detail-head">
            <div class="tce-detail-head__titles">
              <h4 class="tce-group-title">{{ resolveSignalTitle(activeGroup) }}</h4>
              <p class="tce-group-meta mono">
                {{ activeGroup.primaryExecutor }} · {{ activeGroup.count }}
                {{ tr('trajectoryCode.groupErrorUnit', '条报错') }} · {{ activeGroup.turnCount }}
                {{ tr('trajectoryCode.groupTurnUnit', '轮') }} ·
                {{ tr('trajectoryCode.groupedBySignal', '按相同失败信号归组') }}
              </p>
            </div>

            <button
              type="button"
              class="tce-link-btn"
              data-testid="code-analyze-group-btn"
              @click="handleAnalyzeGroup(activeGroup)"
            >
              {{ tr('trajectoryCode.analyzeGroup', '分析这组') }}
            </button>
          </div>

          <div class="tce-calls-table-wrap">
            <table class="tce-calls-table" data-testid="code-group-calls-table">
              <thead>
                <tr>
                  <th scope="col">{{ tr('trajectoryCode.table.turn', '轮次') }}</th>
                  <th scope="col">{{ tr('trajectoryCode.table.seq', '序号') }}</th>
                  <th scope="col">{{ tr('trajectoryCode.table.executor', '执行器') }}</th>
                  <th scope="col" class="is-end">{{ tr('trajectoryCode.table.location', '位置') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="item in activeGroup.items"
                  :key="item.id"
                  class="tce-call-row"
                  :class="{ 'is-selected': item.id === activeInvocation.id }"
                  :data-testid="`code-group-call-row-${item.id}`"
                  tabindex="0"
                  @click="selectInvocation(item)"
                  @keydown.enter.prevent="selectInvocation(item)"
                  @keydown.space.prevent="selectInvocation(item)"
                >
                  <td class="mono">{{ tr('trajectoryCode.turnLabel', '第 {n} 轮', { n: item.turnNo }) }}</td>
                  <td class="mono">{{ item.seqLabel }}</td>
                  <td class="mono">{{ item.toolId }}</td>
                  <td class="mono is-end">{{ item.lineLocation }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>

        <!-- Selected Call Metadata & Highlighted Code Frame -->
        <section class="tce-call-section">
          <div class="tce-section-eyebrow">
            {{ tr('trajectoryCode.syntaxAndParseEyebrow', '语法与解析') }}
          </div>

          <div class="tce-detail-head">
            <div class="tce-detail-head__titles">
              <h4 class="tce-call-title">{{ resolveSignalTitle(activeGroup) }}</h4>
              <p class="tce-call-sub mono">
                <span>{{ tr('trajectoryCode.turnLabel', '第 {n} 轮', { n: activeInvocation.turnNo }) }}</span>
                <span>·</span>
                <span class="tce-accent-token">{{ activeInvocation.toolId }}</span>
                <span>·</span>
                <span class="tce-accent-token">{{ activeInvocation.seqLabel }}</span>
                <span class="tce-muted">{{ activeInvocation.charCount }} {{ tr('trajectoryCode.charUnit', '字符') }}</span>
              </p>
            </div>

            <button
              type="button"
              class="tce-link-btn"
              data-testid="code-diagnose-call-btn"
              @click="handleDiagnoseSingleCall(activeInvocation)"
            >
              {{ tr('trajectoryCode.diagnoseThisCall', '诊断这次') }}
            </button>
          </div>

          <!-- 4-Field Metadata Grid -->
          <dl class="tce-meta-grid" data-testid="code-call-meta-grid">
            <div class="tce-meta-cell">
              <dt>{{ tr('trajectoryCode.meta.executor', '执行器') }}</dt>
              <dd class="mono">{{ activeInvocation.executor }}</dd>
            </div>
            <div class="tce-meta-cell">
              <dt>{{ tr('trajectoryCode.meta.errorSignal', '错误信号') }}</dt>
              <dd class="mono">{{ activeInvocation.errorSignalCode }}</dd>
            </div>
            <div class="tce-meta-cell">
              <dt>{{ tr('trajectoryCode.meta.basis', '反馈依据') }}</dt>
              <dd class="mono">{{ activeInvocation.signalCode ?? '—' }}</dd>
            </div>
            <div class="tce-meta-cell">
              <dt>{{ tr('trajectoryCode.meta.location', '记录中的位置') }}</dt>
              <dd class="mono">{{ activeInvocation.lineLocation }}</dd>
            </div>
          </dl>

          <!-- Syntax Feedback / Highlighted Error Code Frame -->
          <div
            v-if="activeInvocation.frames.length > 0"
            class="tce-frame-section"
            data-testid="code-frame-view"
          >
            <div class="tce-frame-heading">
              <span class="tce-frame-heading__tag">
                {{ tr('trajectoryCode.frame.heading', '语法反馈 / 出错代码帧') }}
              </span>
              <span v-if="activeInvocation.lineNumber !== null" class="tce-frame-heading__line mono">
                {{ tr('trajectoryCode.frame.lineAt', '第 {n} 行', { n: activeInvocation.lineNumber }) }}
              </span>
            </div>

            <div class="tce-frame-box mono">
              <div
                v-for="(line, idx) in activeInvocation.frames[0]!.lines"
                :key="idx"
                class="tce-frame-line"
                :class="{ 'is-highlight': line.highlight }"
                :data-testid="line.highlight ? 'code-frame-highlight-line' : undefined"
              >
                <span class="tce-frame-line__bar" aria-hidden="true" />
                <span class="tce-frame-line__num">{{ line.lineNumber ?? '·' }}</span>
                <span class="tce-frame-line__code">{{ line.text }}</span>
              </div>
            </div>

            <p v-if="activeInvocation.primaryErrorMessage" class="tce-frame-errmsg">
              {{ activeInvocation.primaryErrorMessage }}
            </p>
            <span v-if="activeInvocation.signalCode" class="tce-frame-errcat mono">
              {{ activeInvocation.signalCode }}
            </span>
          </div>

          <!-- 4 Clickable Remediation Direction Cards -->
          <div class="tce-remediation-section">
            <div class="tce-section-eyebrow">
              {{ tr('trajectoryCode.remediationHeading', '可调查的处理方向') }}
            </div>

            <div class="tce-remediation-grid">
              <button
                v-for="rem in activeInvocation.remediations"
                :key="rem.key"
                type="button"
                class="tce-remediation-card"
                :data-testid="`code-remediation-${rem.key}`"
                @click="handleRemediationClick(rem)"
              >
                <div class="tce-remediation-card__head">
                  <strong class="tce-remediation-card__title">{{ tr(rem.i18nKey, rem.label) }}</strong>
                  <ArrowUpRight :size="13" class="tce-remediation-card__arrow" aria-hidden="true" />
                </div>
                <span class="tce-remediation-card__desc">{{ tr(rem.descI18nKey, rem.description) }}</span>
              </button>
            </div>
          </div>

          <!-- Side-by-side / Stacked Raw Script & Execution Feedback -->
          <div class="tce-raw-grid">
            <details class="tce-raw-block" open>
              <summary class="tce-raw-summary">
                {{ tr('trajectoryCode.frame.scriptBlock', '原始脚本') }}
              </summary>
              <pre class="tce-raw-pre mono" data-testid="code-raw-script">{{ activeInvocation.scriptSource || '—' }}</pre>
            </details>

            <details class="tce-raw-block" open>
              <summary class="tce-raw-summary">
                {{ tr('trajectoryCode.frame.outputBlock', '执行反馈') }}
              </summary>
              <pre class="tce-raw-pre mono" data-testid="code-raw-feedback">{{ activeInvocation.feedbackText || '—' }}</pre>
            </details>
          </div>
        </section>

        <!-- Optional expandable succeeded commands when mixed with failures -->
        <section v-if="report.succeededInvocations.length > 0" class="tce-succeeded-Collapse">
          <button
            type="button"
            class="tce-drawer-toggle"
            :aria-expanded="showAllRecordsDrawer"
            data-testid="code-toggle-succeeded"
            @click="showAllRecordsDrawer = !showAllRecordsDrawer"
          >
            <span>
              {{ tr('trajectoryCode.succeededCallsToggle', '其余正常返回命令 ({n})', { n: report.succeededInvocations.length }) }}
            </span>
            <span class="mono">{{ showAllRecordsDrawer ? '−' : '+' }}</span>
          </button>

          <div v-if="showAllRecordsDrawer" class="tce-clean-list" data-testid="trajectory-code-list">
            <article
              v-for="item in report.succeededInvocations"
              :key="item.id"
              class="tce-clean-row"
              :data-testid="`code-exec-item-${item.id}`"
            >
              <div class="tce-clean-row__top mono">
                <span class="tce-clean-turn">{{ tr('trajectoryCode.turnLabel', '第 {n} 轮', { n: item.turnNo }) }}</span>
                <span class="tce-muted">{{ item.seqLabel }}</span>
                <strong>{{ item.toolId }}</strong>
                <span class="tce-status-ok">{{ item.status }}</span>
                <span class="tce-clean-dur">{{ formatDurationMs(item.durationMs) }}</span>
              </div>
              <p v-if="item.scriptSource" class="tce-clean-cmd mono">$ {{ item.scriptSource }}</p>
              <p v-if="item.feedbackText" class="tce-clean-out">{{ item.feedbackText }}</p>
            </article>
          </div>
        </section>
      </div>
    </div>

    <!-- 3C. Clean Succeeded Commands View when 0 failed groups exist -->
    <div v-else class="tce-clean-wrap" data-testid="trajectory-code-list">
      <div class="tce-clean-banner">
        <span>{{ tr('trajectoryCode.allSucceededNote', '全部命令与脚本调用均正常返回，未检出失败信号。') }}</span>
      </div>

      <article
        v-for="item in report.invocations"
        :key="item.id"
        class="tce-clean-row"
        :data-testid="`code-exec-item-${item.id}`"
      >
        <div class="tce-clean-row__top mono">
          <span class="tce-clean-turn">{{ tr('trajectoryCode.turnLabel', '第 {n} 轮', { n: item.turnNo }) }}</span>
          <span class="tce-muted">{{ item.seqLabel }}</span>
          <strong>{{ item.toolId }}</strong>
          <span class="tce-muted">({{ item.executor }})</span>
          <span class="tce-status-ok">{{ item.status }}</span>
          <span class="tce-clean-dur">{{ formatDurationMs(item.durationMs) }}</span>
        </div>
        <p v-if="item.scriptSource" class="tce-clean-cmd mono">$ {{ item.scriptSource }}</p>
        <p v-if="item.feedbackText" class="tce-clean-out">{{ item.feedbackText }}</p>
      </article>
    </div>
  </div>
</template>

<style scoped>
.tce-root {
  container-type: inline-size;
  display: flex;
  flex-direction: column;
  min-width: 0;
  background: var(--bg-primary);
  color: var(--text-primary);
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-feature-settings: "tnum" 1;
}

/* 1. Header Row */
.tce-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  padding: 14px 14px 10px;
}

.tce-header__titles {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.tce-title {
  margin: 0;
  font-size: 16px;
  font-weight: 700;
  letter-spacing: -0.02em;
  color: var(--text-primary);
}

.tce-subtitle {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-secondary);
}

.tce-header__controls {
  flex-shrink: 0;
}

.tce-select {
  height: 28px;
  padding: 0 10px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: 12px;
  cursor: pointer;
}

.tce-select:focus {
  outline: none;
  border-color: var(--text-primary);
}

/* 2. 3 Large Geist KPI Numbers */
.tce-kpi-strip {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
  padding: 8px 14px 14px;
  border-bottom: 1px solid var(--border-muted);
}

.tce-kpi-cell {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.tce-kpi-label {
  font-size: 11px;
  color: var(--text-secondary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.tce-kpi-value {
  font-size: 24px;
  line-height: 1.15;
  font-weight: 600;
  letter-spacing: -0.03em;
  color: var(--text-primary);
}

.tce-kpi-value.is-danger {
  color: var(--obs-status-danger, var(--text-error, #cf222e));
}

/* 3. Responsive Split (>540px Left Signal List + Right Detail; <=540px Stacked) */
.tce-split {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
}

.tce-sidebar {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px;
  border-bottom: 1px solid var(--border-muted);
}

.tce-detail {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

@container (min-width: 541px) {
  .tce-split {
    grid-template-columns: 188px minmax(0, 1fr);
  }

  .tce-sidebar {
    border-bottom: 0;
    border-right: 1px solid var(--border-muted);
    padding: 14px 12px;
  }
}

.tce-section-eyebrow {
  font-size: 11px;
  font-weight: 500;
  color: var(--text-muted);
}

.tce-group-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.tce-group-item {
  width: 100%;
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: 3px;
  padding: 8px 10px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
  transition: background-color 110ms ease;
}

.tce-group-item:hover {
  background: var(--bg-secondary);
}

.tce-group-item.is-active {
  background: var(--bg-selected, var(--bg-secondary));
}

.tce-group-item__top {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
}

.tce-group-item__label {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.tce-group-item.is-active .tce-group-item__label,
.tce-group-item.is-active .tce-group-item__count {
  color: var(--obs-file-read, var(--text-primary));
}

.tce-group-item__count {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary);
}

.tce-group-item__sub {
  font-size: 11px;
  color: var(--text-muted);
}

/* Detail Sections */
.tce-group-section {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px;
  border-bottom: 1px solid var(--border-muted);
}

.tce-call-section {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 14px;
}

.tce-detail-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
}

.tce-detail-head__titles {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.tce-group-title,
.tce-call-title {
  margin: 0;
  font-size: 16px;
  font-weight: 700;
  letter-spacing: -0.02em;
  color: var(--text-primary);
}

.tce-group-meta {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-secondary);
}

.tce-call-sub {
  margin: 0;
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  font-size: 12px;
  color: var(--text-secondary);
}

.tce-accent-token {
  color: var(--obs-file-read, var(--text-primary));
  font-weight: 500;
}

.tce-muted {
  color: var(--text-muted);
}

.tce-link-btn {
  flex-shrink: 0;
  padding: 2px 6px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--obs-file-read, var(--text-primary));
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
}

.tce-link-btn:hover {
  background: var(--bg-secondary);
  text-decoration: underline;
}

/* Same-Signal Calls Table */
.tce-calls-table-wrap {
  width: 100%;
  overflow-x: auto;
}

.tce-calls-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
}

.tce-calls-table th {
  padding: 5px 8px;
  border-bottom: 1px solid var(--border-muted);
  color: var(--text-muted);
  font-size: 11px;
  font-weight: 500;
  text-align: left;
}

.tce-calls-table td {
  padding: 7px 8px;
  border-bottom: 1px solid var(--border-muted);
  color: var(--text-primary);
}

.tce-calls-table .is-end {
  text-align: right;
}

.tce-call-row {
  cursor: pointer;
  transition: background-color 100ms ease;
}

.tce-call-row:hover {
  background: var(--bg-secondary);
}

.tce-call-row.is-selected {
  background: var(--obs-file-read-soft, var(--bg-selected, var(--bg-secondary)));
}

.tce-call-row.is-selected td:first-child,
.tce-call-row.is-selected td:nth-child(2),
.tce-call-row.is-selected td:nth-child(3) {
  color: var(--obs-file-read, var(--text-primary));
  font-weight: 500;
}

/* Metadata 4-Cell Grid */
.tce-meta-grid {
  margin: 0;
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px 16px;
  padding: 10px 0;
  border-top: 1px solid var(--border-muted);
  border-bottom: 1px solid var(--border-muted);
}

.tce-meta-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.tce-meta-cell dt {
  font-size: 11px;
  color: var(--text-muted);
}

.tce-meta-cell dd {
  margin: 0;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

/* Error Code Frame with Red Indicator Bar */
.tce-frame-section {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.tce-frame-heading {
  display: flex;
  align-items: baseline;
  gap: 8px;
  font-size: 12px;
}

.tce-frame-heading__tag {
  font-weight: 600;
  color: var(--obs-status-danger, var(--text-error, #cf222e));
}

.tce-frame-heading__line {
  font-weight: 600;
  color: var(--text-primary);
}

.tce-frame-box {
  display: flex;
  flex-direction: column;
  border-top: 1px solid var(--border-muted);
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  overflow-x: auto;
}

.tce-frame-line {
  display: grid;
  grid-template-columns: 3px 34px minmax(0, 1fr);
  align-items: stretch;
  gap: 8px;
  font-size: 12px;
  line-height: 1.65;
  color: var(--text-secondary);
}

.tce-frame-line__bar {
  width: 3px;
  background: transparent;
}

.tce-frame-line__num {
  padding: 3px 0;
  text-align: right;
  color: var(--text-muted);
  user-select: none;
}

.tce-frame-line__code {
  padding: 3px 10px 3px 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.tce-frame-line.is-highlight {
  background: var(--obs-status-danger-soft, rgba(207, 34, 46, 0.1));
  color: var(--obs-status-danger, var(--text-error, #cf222e));
}

.tce-frame-line.is-highlight .tce-frame-line__bar {
  background: var(--obs-status-danger, var(--text-error, #cf222e));
}

.tce-frame-line.is-highlight .tce-frame-line__num {
  color: var(--obs-status-danger, var(--text-error, #cf222e));
  font-weight: 600;
}

.tce-frame-errmsg {
  margin: 2px 0 0;
  font-size: 12px;
  font-weight: 500;
  color: var(--obs-status-danger, var(--text-error, #cf222e));
  overflow-wrap: anywhere;
}

.tce-frame-errcat {
  font-size: 11px;
  color: var(--text-muted);
}

/* 4 Remediation Direction Cards */
.tce-remediation-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.tce-remediation-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 8px;
}

@container (min-width: 541px) {
  .tce-remediation-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@container (min-width: 820px) {
  .tce-remediation-grid {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

.tce-remediation-card {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: 4px;
  padding: 10px 12px;
  border: 0;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-primary);
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
  transition: background-color 110ms ease;
}

.tce-remediation-card:hover {
  background: var(--bg-secondary);
}

.tce-remediation-card__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.tce-remediation-card__title {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.tce-remediation-card__arrow {
  flex-shrink: 0;
  color: var(--text-secondary);
}

.tce-remediation-card__desc {
  font-size: 11.5px;
  line-height: 1.45;
  color: var(--text-secondary);
}

/* Raw Script & Execution Feedback Blocks */
.tce-raw-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 10px;
}

@container (min-width: 541px) {
  .tce-raw-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

.tce-raw-block {
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.tce-raw-summary {
  padding: 4px 0;
  font-size: 11.5px;
  font-weight: 500;
  color: var(--text-secondary);
  cursor: pointer;
  user-select: none;
}

.tce-raw-pre {
  margin: 4px 0 0;
  padding: 10px 12px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
  color: var(--text-primary);
  font-size: 11.5px;
  line-height: 1.5;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  max-height: 220px;
  overflow-y: auto;
}

/* Succeeded / Empty States */
.tce-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 32px 16px;
  color: var(--text-muted);
  font-size: 12px;
}

.tce-succeeded-Collapse {
  border-top: 1px solid var(--border-muted);
}

.tce-drawer-toggle {
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 9px 14px;
  border: 0;
  background: var(--bg-primary);
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.tce-drawer-toggle:hover {
  background: var(--bg-secondary);
  color: var(--text-primary);
}

.tce-clean-wrap,
.tce-clean-list {
  display: flex;
  flex-direction: column;
}

.tce-clean-banner {
  padding: 8px 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  color: var(--text-secondary);
  font-size: 11.5px;
}

.tce-clean-row {
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding: 11px 14px;
  border-bottom: 1px solid var(--border-muted);
}

.tce-clean-row__top {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 12px;
}

.tce-clean-turn {
  font-weight: 600;
  color: var(--text-primary);
}

.tce-status-ok {
  color: var(--obs-status-ok, #1a7f37);
  font-size: 11px;
}

.tce-clean-dur {
  margin-left: auto;
  color: var(--text-muted);
  font-size: 11.5px;
}

.tce-clean-cmd {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.tce-clean-out {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-secondary);
  overflow-wrap: anywhere;
}
</style>
