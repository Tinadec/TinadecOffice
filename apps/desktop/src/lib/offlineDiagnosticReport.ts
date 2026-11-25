/**
 * Click-to-Diagnose -> Composer draft injection & self-contained Offline
 * Single-File HTML Diagnostic Report generator.
 *
 * Adheres strictly to Two-Surface Discipline (#29): routes cleanly to Home `/`
 * Composer (`homeController.updateDraft`) and never creates standalone debug routes.
 */

import type {
  ContextEvolutionProposalDto,
  RunContextDiagnosticsDto,
} from '@/api'
import {
  formatLineRangePair,
  type ContextOverlapAuditResult,
  type ContextOverlapPairSummary,
} from '@/lib/contextOverlapWinnowing'
import type { ContextTreemapModel, ContextTreemapNode } from '@/lib/contextTreemapLayout'
import type {
  ContextWindowBucketReport,
  TrajectoryNode,
  TrajectorySummary,
} from '@/lib/trajectoryBuilder'

export interface StaleSnapshotNotice {
  cachedAt: string
  reason?: string | null
}

export interface DiagnosticDraftInput {
  runId?: string | null
  sessionId?: string | null
  selectedNode?: TrajectoryNode | null
  trajectory?: TrajectorySummary | null
  treemap?: ContextTreemapModel | null
  selectedTreemapSegment?: ContextTreemapNode | null
  buckets?: ContextWindowBucketReport | null
  overlap?: ContextOverlapAuditResult | null
  selectedOverlapPair?: ContextOverlapPairSummary | null
  diagnostics?: RunContextDiagnosticsDto | null
  proposal?: ContextEvolutionProposalDto | null
  staleSnapshot?: StaleSnapshotNotice | null
  /** Optional pre-built Markdown draft (e.g. from `buildEvalCompareDraft`). */
  customDraft?: string | null
}

export interface DiagnoseInComposerOptions {
  /** Override draft updater (defaults to `homeController.updateDraft`). */
  updateDraft?: (draft: string) => void
  /** Override router navigation (defaults to navigating to `'/'` only if off Home). */
  navigateToHome?: () => Promise<unknown> | unknown
  /** Skip DOM focus attempt (useful in non-DOM tests). */
  skipFocus?: boolean
}

export interface DiagnoseInComposerResult {
  draft: string
  navigated: boolean
}

export interface OfflineReportInput {
  title?: string
  generatedAt?: string
  runId?: string | null
  sessionId?: string | null
  trajectory?: TrajectorySummary | null
  buckets?: ContextWindowBucketReport | null
  treemap?: ContextTreemapModel | null
  overlap?: ContextOverlapAuditResult | null
  diagnostics?: RunContextDiagnosticsDto | null
  staleSnapshot?: StaleSnapshotNotice | null
}

export interface ExportOfflineHtmlOptions {
  filename?: string
  /** Optional custom download trigger for tests or Electron host bridges. */
  download?: (filename: string, html: string) => void
}

export interface ExportOfflineHtmlResult {
  filename: string
  html: string
  bytes: number
}

/** Strict HTML-escaping for XSS-safe single-file report rendering. */
export function escapeHtml(value: unknown): string {
  const str = value === null || value === undefined ? '' : String(value)
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
    .replace(/`/g, '&#96;')
}

/**
 * Build an editable Markdown diagnostic prompt draft targeting a run/node/segment
 * and explicitly instructing the agent to invoke `tinadec_inspect` (#30) for
 * evidence-based root-cause analysis.
 */
export function buildDiagnosticDraft(input: DiagnosticDraftInput): string {
  if (input.customDraft && input.customDraft.trim().length > 0) {
    return input.customDraft
  }
  if (input.proposal?.composer_prompt && !input.selectedNode && !input.selectedTreemapSegment && !input.selectedOverlapPair) {
    return input.proposal.composer_prompt
  }

  const proposalRunIds = input.proposal?.evidence_run_ids ?? []
  const runId =
    input.runId
    ?? input.diagnostics?.run_id
    ?? input.trajectory?.runId
    ?? input.proposal?.source_run_id
    ?? proposalRunIds[0]
    ?? null
  const sessionId =
    input.sessionId
    ?? input.diagnostics?.session_id
    ?? input.trajectory?.sessionId
    ?? null
  const node = input.selectedNode ?? null
  const segment = input.selectedTreemapSegment ?? null
  const rec = input.treemap?.reconciliation ?? null

  const lines: string[] = [
    'Diagnose the following execution trajectory and context evidence using read-only `tinadec_inspect` before proposing any configuration or code remediation:',
    '',
    '### Context Reference',
  ]

  if (runId) lines.push(`- **run_id**: \`${runId}\``)
  if (sessionId) lines.push(`- **session_id**: \`${sessionId}\``)
  if (input.staleSnapshot?.cachedAt) {
    lines.push(`- **snapshot_source**: cached local snapshot (${input.staleSnapshot.cachedAt})`)
  }

  if (node) {
    lines.push(`- **selected_span**: \`${node.id}\` (${node.kind}) — ${node.label} [${node.status}]`)
    if (node.durationMs !== null) lines.push(`- **duration**: ${node.durationMs} ms`)
    if (node.toolId) lines.push(`- **tool_id**: \`${node.toolId}\``)
    if (node.resourcePath) lines.push(`- **resource_path**: \`${node.resourcePath}\``)
    if (node.command) lines.push(`- **command**: \`${node.command}\``)
    if (node.editStats) {
      lines.push(`- **file_edit_delta**: +${node.editStats.added} / -${node.editStats.removed}`)
    }
    if (node.signals.length > 0) {
      const sigList = node.signals.map((s) => (s.count && s.count > 1 ? `${s.code} (x${s.count})` : s.code)).join(', ')
      lines.push(`- **governance_signals**: ${sigList}`)
    }
  } else if (input.trajectory && input.trajectory.activeSignals.length > 0) {
    lines.push(`- **governance_signals**: ${input.trajectory.activeSignals.join(', ')}`)
  }

  if (segment) {
    lines.push(
      `- **context_segment**: \`${segment.label}\` — ${segment.retainedTokens} retained tokens (${segment.sharePercent}% of packed context${segment.budgetSharePercent ? `, ${segment.budgetSharePercent}% of budget` : ''})${segment.droppedTokens > 0 ? `, ${segment.droppedTokens} dropped tokens` : ''}`,
    )
  } else if (rec && rec.tokenBudget > 0) {
    lines.push(
      `- **context_share**: ${rec.retainedTokens}/${rec.tokenBudget} tokens (${rec.utilizationPercent}% utilization, ${rec.droppedTokens} dropped)`,
    )
  }

  if (input.buckets && input.buckets.totalSamples > 0) {
    lines.push(
      `- **window_occupancy**: peak ${input.buckets.peakOccupancyPct}%, error rate ${Math.round(input.buckets.overallErrorRate * 1000) / 10}%, avg latency ${input.buckets.avgLatencyMs} ms${input.buckets.degradationDetected ? ' (high-occupancy >=70% degradation detected)' : ''}`,
    )
  }

  const pair = input.selectedOverlapPair ?? input.overlap?.pairs[0] ?? null
  if (pair) {
    const firstMatch = pair.matches[0]
    lines.push(
      `- **context_overlap**: \`${pair.sourceALabel}\` <-> \`${pair.sourceBLabel}\` (${pair.overlapPercent}% overlap, ~${pair.estimatedDuplicateTokens} duplicate tokens${firstMatch ? `, ${formatLineRangePair(firstMatch.sourceALines, firstMatch.sourceBLines)}` : ''})`,
    )
    lines.push(`- **overlap_remediation_hint**: ${pair.remediationSummary}`)
  }

  const frames = node?.codeFrames ?? []
  if (frames.length > 0) {
    lines.push('', '### Error Code Frame')
    for (const frame of frames.slice(0, 2)) {
      lines.push(`\`${frame.title}\`${frame.errorCategory ? ` (${frame.errorCategory})` : ''}`)
      lines.push('```text')
      for (const fl of frame.lines.slice(0, 8)) {
        const prefix = fl.lineNumber !== null && fl.lineNumber !== undefined ? `${fl.lineNumber} | ` : ''
        lines.push(`${fl.highlight ? '> ' : '  '}${prefix}${fl.text}`)
      }
      lines.push('```')
    }
  }

  const coreFindings = input.diagnostics?.executive_findings ?? []
  if (coreFindings.length > 0) {
    lines.push('', '### Core Executive Findings')
    for (const f of coreFindings.slice(0, 5)) {
      lines.push(`- **[${f.severity.toUpperCase()}] \`${f.code}\`**: ${f.summary}${f.suggested_action ? ` -> _${f.suggested_action}_` : ''}`)
    }
  }

  if (input.proposal) {
    const p = input.proposal
    const evRuns = p.evidence_run_ids?.length ? p.evidence_run_ids : (p.source_run_id ? [p.source_run_id] : [])
    lines.push('', '### Context Evolution Proposal')
    lines.push(`- **proposal_id**: \`${p.id}\` (${p.kind}, status: \`${p.status}\`)`)
    if (p.target_scope) {
      const modeRef = p.target_mode_name || p.target_mode_slug
      lines.push(`- **target_scope**: \`${p.target_scope}\`${modeRef ? ` (${modeRef})` : ''}`)
    }
    lines.push(`- **summary**: ${p.title ? `${p.title} — ` : ''}${p.summary}`)
    if (p.rationale) lines.push(`- **rationale**: ${p.rationale}`)
    if (evRuns.length > 0) lines.push(`- **evidence_runs**: ${evRuns.map((r) => `\`${r}\``).join(', ')}`)
    if (p.dedup_evidence) {
      lines.push(
        `- **dedup_overlap**: \`${p.dedup_evidence.source_a_label}\` <-> \`${p.dedup_evidence.source_b_label}\` (${p.dedup_evidence.line_range_label}, ~${p.dedup_evidence.estimated_wasted_tokens} wasted tokens)`,
      )
    }
    if (p.remediation_code) lines.push(`- **remediation_code**: \`${p.remediation_code}\``)
    if (p.metrics_comparison) {
      if (Object.keys(p.metrics_comparison.before_values ?? {}).length > 0) {
        lines.push(`- **before_values**: \`${JSON.stringify(p.metrics_comparison.before_values)}\``)
      }
      if (Object.keys(p.metrics_comparison.after_values ?? {}).length > 0) {
        lines.push(`- **after_values**: \`${JSON.stringify(p.metrics_comparison.after_values)}\``)
      }
    }
  }

  lines.push(
    '',
    '### Requested Analysis',
    '1. Call `tinadec_inspect` (targets: `run_diagnostics`, `context_audit`, `context_memory`, `agents`, `modes`, `skills`, or `tools` as relevant) to verify current live configuration and context budget.',
    '2. Identify the concrete root cause from the trajectory evidence, governance signals, and context share above.',
    '3. Propose minimal, evidence-backed fixes (without guessing or bypassing governance gates).',
  )

  return lines.join('\n')
}

/**
 * Inject an editable diagnostic draft directly into the Home Composer
 * (`homeController.updateDraft`) and switch cleanly to `'/'` if needed.
 */
export async function triggerDiagnoseInComposer(
  input: DiagnosticDraftInput,
  options: DiagnoseInComposerOptions = {},
): Promise<DiagnoseInComposerResult> {
  const draft = buildDiagnosticDraft(input)
  if (options.updateDraft) {
    options.updateDraft(draft)
  } else {
    try {
      const { homeController } = await import('@/controllers/HomeController')
      homeController.updateDraft(draft)
    } catch {
      // Ignore partial-mock test environments where HomeController cannot be dynamically loaded.
    }
  }

  let navigated = false
  if (options.navigateToHome) {
    await options.navigateToHome()
    navigated = true
  } else {
    try {
      const { default: appRouter } = await import('@/router')
      const currentPath = appRouter.currentRoute?.value?.path ?? '/'
      if (currentPath !== '/' && currentPath !== '/space') {
        await appRouter.push('/')
        navigated = true
      }
    } catch {
      // Ignore router unavailable in isolated unit-test environments.
    }
  }

  if (!options.skipFocus && typeof window !== 'undefined' && typeof document !== 'undefined') {
    window.dispatchEvent(new CustomEvent('tinadec:focus-composer', { detail: { draft } }))
    setTimeout(() => {
      const el = document.querySelector<HTMLTextAreaElement>(
        'textarea[data-testid="composer-input"], .composer-textarea, .composer-bar textarea',
      )
      el?.focus()
    }, 0)
  }

  return { draft, navigated }
}

function buildConclusions(input: OfflineReportInput): string[] {
  const coreFindings = input.diagnostics?.executive_findings ?? []
  if (coreFindings.length > 0) {
    return coreFindings.map((f) =>
      f.suggested_action
        ? `[${f.code}] ${f.summary} — Action: ${f.suggested_action}`
        : `[${f.code}] ${f.summary}`,
    )
  }

  const conclusions: string[] = []
  const traj = input.trajectory
  const buckets = input.buckets
  const rec = input.treemap?.reconciliation
  const overlap = input.overlap

  if (traj) {
    if (traj.errorCount === 0 && traj.activeSignals.length === 0) {
      conclusions.push(
        `Run completed cleanly across ${traj.totalToolCalls} tool calls and ${traj.totalModelCalls} model invocations with zero governance blocks.`,
      )
    } else {
      conclusions.push(
        `Detected ${traj.errorCount} failed span(s) across ${traj.totalToolCalls} tool calls`
        + (traj.activeSignals.length > 0 ? ` with governance signals: ${traj.activeSignals.join(', ')}.` : '.'),
      )
    }
    if (traj.signalCounts.SAME_ARGS_RETRY > 0) {
      conclusions.push(
        `Repeated-argument retry streak detected (${traj.signalCounts.SAME_ARGS_RETRY} event(s)): inspect tool constraints or stale file state before retrying.`,
      )
    }
  }

  if (buckets && buckets.totalSamples > 0) {
    if (buckets.degradationDetected) {
      conclusions.push(
        `High-occupancy degradation (>=70% window): error rate jumped from ${Math.round(buckets.lowOccupancyErrorRate * 100)}% to ${Math.round(buckets.highOccupancyErrorRate * 100)}% (peak ${buckets.peakOccupancyPct}%).`,
      )
    } else {
      conclusions.push(
        `Context window peaked at ${buckets.peakOccupancyPct}% across ${buckets.totalSamples} sample(s) (avg latency ${buckets.avgLatencyMs} ms).`,
      )
    }
  }

  if (rec && rec.tokenBudget > 0) {
    if (rec.droppedTokens > 0) {
      conclusions.push(
        `Compaction / budget trimming dropped ${rec.droppedTokens.toLocaleString('en-US')} tokens (${rec.droppedRatioPercent}% of pre-compaction context); retained ${rec.retainedTokens.toLocaleString('en-US')} / ${rec.tokenBudget.toLocaleString('en-US')} tokens.`,
      )
    } else {
      conclusions.push(
        `Context pack fit cleanly within budget: ${rec.retainedTokens.toLocaleString('en-US')} / ${rec.tokenBudget.toLocaleString('en-US')} tokens (${rec.utilizationPercent}%), 0 tokens dropped.`,
      )
    }
  }

  if (overlap && overlap.pairs.length > 0) {
    conclusions.push(
      `64-char Winnowing audit found ${overlap.pairs.length} overlapping source pair(s) wasting ~${overlap.duplicateEstimatedTokens.toLocaleString('en-US')} duplicate tokens (${overlap.overallOverlapPercent}% of scanned prompt/skill corpus).`,
    )
  }

  if (conclusions.length === 0) {
    conclusions.push('No trajectory or context anomalies recorded in this diagnostic snapshot.')
  }

  return conclusions
}

function renderTrajectoryRowsHtml(node: TrajectoryNode, depth = 0): string {
  const indent = depth * 18
  const editCell = node.editStats
    ? `<span class="mono">+${node.editStats.added} / -${node.editStats.removed}</span>`
    : '<span class="muted">—</span>'
  const durCell = node.durationMs !== null ? `<span class="mono">${escapeHtml(node.durationMs)} ms</span>` : '<span class="muted">—</span>'
  const sigPills = node.signals
    .map((s) => `<span class="pill pill-${escapeHtml(s.severity)}">${escapeHtml(s.code)}${s.count && s.count > 1 ? ` &times;${s.count}` : ''}</span>`)
    .join(' ')

  const frameBlocks = node.codeFrames
    .map((frame) => {
      const codeLines = frame.lines
        .map((fl) => {
          const num = fl.lineNumber !== null && fl.lineNumber !== undefined ? `${String(fl.lineNumber).padStart(4, ' ')} | ` : '     | '
          return `<div class="cf-line${fl.highlight ? ' is-hi' : ''}">${escapeHtml(num + fl.text)}</div>`
        })
        .join('')
      return `<div class="code-frame"><div class="cf-head">${escapeHtml(frame.title)}${frame.errorCategory ? ` · ${escapeHtml(frame.errorCategory)}` : ''}</div><pre>${codeLines}</pre></div>`
    })
    .join('')

  const row = `<tr>
    <td style="padding-left:${10 + indent}px">
      <span class="kind-tag">${escapeHtml(node.kind)}</span>
      <strong>${escapeHtml(node.label)}</strong>
      ${node.subtitle ? `<div class="sub mono">${escapeHtml(node.subtitle)}</div>` : ''}
      ${frameBlocks}
    </td>
    <td><span class="pill${node.isError ? ' pill-danger' : ''}">${escapeHtml(node.status)}</span></td>
    <td>${durCell}</td>
    <td>${editCell}</td>
    <td>${sigPills || '<span class="muted">—</span>'}</td>
  </tr>`

  const childrenHtml = node.children.map((c) => renderTrajectoryRowsHtml(c, depth + 1)).join('')
  return row + childrenHtml
}

/**
 * Render a crisp single-file self-contained `1px` Geist-style monochrome HTML
 * diagnostic report. All dynamic fields are strictly HTML-escaped.
 */
export function buildOfflineDiagnosticHtml(input: OfflineReportInput): string {
  const title = input.title || 'Tinadec Trajectory & Context Diagnostic Report'
  const generatedAt = input.generatedAt || input.diagnostics?.generated_at || new Date().toISOString()
  const runId = input.runId ?? input.diagnostics?.run_id ?? input.trajectory?.runId ?? 'n/a'
  const sessionId = input.sessionId ?? input.diagnostics?.session_id ?? input.trajectory?.sessionId ?? 'n/a'
  const traj = input.trajectory ?? null
  const buckets = input.buckets ?? null
  const treemap = input.treemap ?? null
  const overlap = input.overlap ?? null
  const staleNote = input.staleSnapshot?.cachedAt
    ? `<span>Snapshot: <code class="mono">cached (${escapeHtml(input.staleSnapshot.cachedAt)})</code></span>`
    : ''

  const conclusions = buildConclusions(input)
  const conclusionListHtml = conclusions.map((c) => `<li>${escapeHtml(c)}</li>`).join('')

  const kpis: Array<{ label: string; value: string }> = [
    { label: 'Tool Calls', value: traj ? String(traj.totalToolCalls) : '0' },
    { label: 'Errors', value: traj ? String(traj.errorCount) : '0' },
    { label: 'Peak Window', value: buckets ? `${buckets.peakOccupancyPct}%` : '—' },
    {
      label: 'Retained / Dropped',
      value: treemap?.reconciliation
        ? `${treemap.reconciliation.retainedTokens} / ${treemap.reconciliation.droppedTokens} tok`
        : '—',
    },
    {
      label: 'Winnowing Overlap',
      value: overlap ? `${overlap.overallOverlapPercent}% (~${overlap.duplicateEstimatedTokens} tok)` : '—',
    },
    {
      label: 'Governance Signals',
      value: traj ? String(traj.activeSignals.length) : '0',
    },
  ]

  const kpiHtml = kpis
    .map(
      (k) => `<div class="kpi"><div class="kpi-label">${escapeHtml(k.label)}</div><div class="kpi-val mono">${escapeHtml(k.value)}</div></div>`,
    )
    .join('')

  const bucketRowsHtml = buckets
    ? buckets.buckets
        .map((b) => {
          const errPct = Math.round(b.errorRate * 1000) / 10
          return `<tr${b.sampleCount === 0 ? ' class="is-empty"' : ''}>
            <td class="mono">${escapeHtml(b.label)}</td>
            <td class="mono num">${b.sampleCount}</td>
            <td class="mono num">${b.errorCount}</td>
            <td class="mono num">${errPct}%</td>
            <td><div class="bar-track"><div class="bar-fill" style="width:${Math.min(100, errPct)}%"></div></div></td>
            <td class="mono num">${b.avgLatencyMs} ms</td>
          </tr>`
        })
        .join('')
    : '<tr><td colspan="6" class="muted">No context window occupancy samples recorded.</td></tr>'

  const treemapRowsHtml = treemap && treemap.root.children.length > 0
    ? treemap.root.children
        .map((cat) => {
          const barW = Math.min(100, Math.max(0, cat.sharePercent))
          return `<tr>
            <td><strong>${escapeHtml(cat.label)}</strong></td>
            <td class="mono num">${cat.itemCount}</td>
            <td class="mono num">${cat.retainedTokens.toLocaleString('en-US')}</td>
            <td class="mono num">${cat.retainedBytesEstimate.toLocaleString('en-US')} B</td>
            <td class="mono num">${cat.droppedTokens.toLocaleString('en-US')}</td>
            <td class="mono num">${cat.sharePercent}%</td>
            <td><div class="bar-track"><div class="bar-fill" style="width:${barW}%"></div></div></td>
          </tr>`
        })
        .join('')
    : '<tr><td colspan="7" class="muted">No ContextPack composition data recorded.</td></tr>'

  const overlapRowsHtml = overlap && overlap.pairs.length > 0
    ? overlap.pairs
        .map((pair) => {
          const ranges = pair.matches
            .slice(0, 4)
            .map((m) => `<span class="pill mono">${escapeHtml(formatLineRangePair(m.sourceALines, m.sourceBLines))}</span>`)
            .join(' ')
          const excerpt = pair.matches[0]?.excerpt ?? ''
          return `<tr>
            <td>
              <strong>${escapeHtml(pair.sourceALabel)}</strong> <span class="muted">&harr;</span> <strong>${escapeHtml(pair.sourceBLabel)}</strong>
              <div class="sub">${escapeHtml(pair.sourceAKind)} &harr; ${escapeHtml(pair.sourceBKind)}</div>
              ${excerpt ? `<div class="excerpt mono">${escapeHtml(excerpt)}</div>` : ''}
            </td>
            <td class="mono num">${pair.overlapPercent}%</td>
            <td class="mono num">~${pair.estimatedDuplicateTokens}</td>
            <td>${ranges || '<span class="muted">—</span>'}</td>
            <td>${escapeHtml(pair.remediationSummary)}</td>
          </tr>`
        })
        .join('')
    : '<tr><td colspan="5" class="muted">No >=64-char cross-source overlaps detected.</td></tr>'

  const trajectoryRowsHtml = traj
    ? renderTrajectoryRowsHtml(traj.root, 0)
    : '<tr><td colspan="5" class="muted">No trajectory tree available.</td></tr>'

  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>${escapeHtml(title)}</title>
<style>
  :root {
    color-scheme: light dark;
    --bg: #fafafa;
    --surface: #ffffff;
    --surface-subtle: #f4f4f5;
    --ink: #171717;
    --ink-secondary: #52525b;
    --ink-muted: #737373;
    --hairline: #e5e5e5;
    --hairline-strong: #d4d4d4;
    --accent: #0068d6;
    --danger-bg: #fff0f0;
    --danger-ink: #c72b31;
    --danger-border: #f8c9cb;
    --warn-bg: #fffbeb;
    --warn-ink: #9a6700;
    --warn-border: #fde68a;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #0a0a0a;
      --surface: #111111;
      --surface-subtle: #1a1a1a;
      --ink: #ededed;
      --ink-secondary: #a1a1aa;
      --ink-muted: #737373;
      --hairline: #262626;
      --hairline-strong: #404040;
      --accent: #58a6ff;
      --danger-bg: #3b181b;
      --danger-ink: #ff7b82;
      --danger-border: #6e292e;
      --warn-bg: #36280f;
      --warn-ink: #f5b942;
      --warn-border: #694c19;
    }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    padding: 36px 28px 64px;
    background: var(--surface);
    color: var(--ink);
    font-family: Geist, -apple-system, BlinkMacSystemFont, "Segoe UI", Inter, "PingFang SC", "Microsoft YaHei", sans-serif;
    font-size: 13px;
    line-height: 1.55;
    font-variant-numeric: tabular-nums;
    text-rendering: optimizeLegibility;
  }
  .page { max-width: 1080px; margin: 0 auto; display: flex; flex-direction: column; gap: 0; }
  .mono { font-family: "Geist Mono", ui-monospace, SFMono-Regular, "JetBrains Mono", Menlo, Consolas, monospace; font-variant-numeric: tabular-nums; }
  .muted { color: var(--ink-muted); }
  .num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
  header {
    border: 0;
    border-bottom: 1px solid var(--hairline);
    padding: 0 0 20px;
    background: transparent;
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    gap: 16px;
    flex-wrap: wrap;
  }
  header h1 { margin: 0 0 6px; font-size: 20px; font-weight: 600; letter-spacing: -0.03em; }
  .meta { display: flex; gap: 6px 16px; flex-wrap: wrap; color: var(--ink-secondary); font-size: 12px; }
  .meta code { color: var(--ink); }
  .kpis {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
    border-bottom: 1px solid var(--hairline);
    background: transparent;
  }
  .kpi {
    padding: 14px 16px 14px 0;
    margin-right: 16px;
    border: 0;
    background: transparent;
  }
  .kpi-label { font-size: 11px; color: var(--ink-muted); letter-spacing: 0.01em; }
  .kpi-val { font-size: 18px; font-weight: 600; letter-spacing: -0.025em; margin-top: 3px; }
  section.card {
    border: 0;
    border-bottom: 1px solid var(--hairline);
    padding: 22px 0;
    background: transparent;
  }
  section.card:last-child { border-bottom: 0; }
  section.card h2 {
    margin: 0 0 12px;
    font-size: 14px;
    font-weight: 600;
    letter-spacing: -0.015em;
    padding-bottom: 0;
    border-bottom: 0;
  }
  ul.conclusions { margin: 0; padding-left: 18px; display: flex; flex-direction: column; gap: 6px; }
  table { width: 100%; border-collapse: collapse; font-size: 12px; font-variant-numeric: tabular-nums; }
  th, td { padding: 8px 10px; border-bottom: 1px solid var(--hairline); text-align: left; vertical-align: top; }
  th:first-child, td:first-child { padding-left: 0; }
  th:last-child, td:last-child { padding-right: 0; }
  th { color: var(--ink-muted); font-weight: 500; background: transparent; font-size: 11px; border-top: 1px solid var(--hairline); }
  tr.is-empty td { color: var(--ink-muted); }
  .bar-track { width: 100%; min-width: 80px; height: 6px; background: var(--surface-subtle); border: 0; border-radius: 2px; overflow: hidden; }
  .bar-fill { height: 100%; background: var(--accent); }
  .pill {
    display: inline-block;
    padding: 1px 6px;
    border-radius: 4px;
    border: 1px solid var(--hairline-strong);
    background: var(--bg);
    color: var(--ink);
    font-size: 11px;
    line-height: 1.4;
    margin: 1px 3px 1px 0;
  }
  .pill-danger { background: var(--danger-bg); color: var(--danger-ink); border-color: var(--danger-border); }
  .pill-warn { background: var(--warn-bg); color: var(--warn-ink); border-color: var(--warn-border); }
  .kind-tag {
    display: inline-block;
    padding: 0 5px;
    margin-right: 6px;
    border: 1px solid var(--hairline);
    border-radius: 3px;
    background: var(--bg);
    color: var(--ink-secondary);
    font-size: 10px;
    text-transform: uppercase;
  }
  .sub { font-size: 11px; color: var(--ink-secondary); margin-top: 2px; word-break: break-all; }
  .excerpt {
    margin-top: 6px;
    padding: 6px 8px;
    border: 1px solid var(--hairline);
    border-radius: 4px;
    background: var(--bg);
    color: var(--ink-secondary);
    font-size: 11px;
  }
  .code-frame {
    margin-top: 6px;
    border: 1px solid var(--hairline);
    border-radius: 4px;
    overflow: hidden;
    background: var(--bg);
  }
  .cf-head {
    padding: 4px 8px;
    font-size: 11px;
    color: var(--ink-secondary);
    border-bottom: 1px solid var(--hairline);
    background: var(--surface-subtle);
  }
  .code-frame pre { margin: 0; padding: 6px 8px; font-size: 11px; overflow-x: auto; }
  .cf-line { white-space: pre-wrap; word-break: break-word; padding: 1px 4px; border-radius: 2px; }
  .cf-line.is-hi { background: var(--danger-bg); color: var(--danger-ink); }
  @media print {
    body { padding: 0; background: #ffffff; color: #111111; }
    section.card { break-inside: avoid; }
  }
</style>
</head>
<body>
<main class="page">
  <header>
    <div>
      <h1>${escapeHtml(title)}</h1>
      <div class="meta">
        <span>Run: <code class="mono">${escapeHtml(runId)}</code></span>
        <span>Session: <code class="mono">${escapeHtml(sessionId)}</code></span>
        <span>Generated: <code class="mono">${escapeHtml(generatedAt)}</code></span>
        ${staleNote}
      </div>
    </div>
    <span class="pill${traj?.errorCount ? ' pill-danger' : ''}">${escapeHtml(traj?.status ?? 'snapshot')}</span>
  </header>

  <div class="kpis">${kpiHtml}</div>

  <section class="card">
    <h2>Executive Conclusion Summary</h2>
    <ul class="conclusions">${conclusionListHtml}</ul>
  </section>

  <section class="card">
    <h2>5% Context Window Occupancy vs Error Rate &amp; Latency</h2>
    <table>
      <thead>
        <tr>
          <th>Occupancy Bucket</th>
          <th class="num">Samples</th>
          <th class="num">Errors</th>
          <th class="num">Error Rate</th>
          <th>Distribution</th>
          <th class="num">Avg Latency</th>
        </tr>
      </thead>
      <tbody>${bucketRowsHtml}</tbody>
    </table>
  </section>

  <section class="card">
    <h2>Context Composition Treemap Breakdown</h2>
    <table>
      <thead>
        <tr>
          <th>Category</th>
          <th class="num">Items</th>
          <th class="num">Retained Tokens</th>
          <th class="num">Est. Bytes</th>
          <th class="num">Dropped Tokens</th>
          <th class="num">Share</th>
          <th>Proportion</th>
        </tr>
      </thead>
      <tbody>${treemapRowsHtml}</tbody>
    </table>
  </section>

  <section class="card">
    <h2>64-Char Winnowing Context Overlap Audit</h2>
    <table>
      <thead>
        <tr>
          <th>Overlapping Sources</th>
          <th class="num">Overlap</th>
          <th class="num">Est. Dup Tokens</th>
          <th>Line Ranges</th>
          <th>Remediation</th>
        </tr>
      </thead>
      <tbody>${overlapRowsHtml}</tbody>
    </table>
  </section>

  <section class="card">
    <h2>Trajectory Evidence Trace</h2>
    <table>
      <thead>
        <tr>
          <th>Span Hierarchy &amp; Error Code Frames</th>
          <th>Status</th>
          <th>Duration</th>
          <th>Edits</th>
          <th>Governance Signals</th>
        </tr>
      </thead>
      <tbody>${trajectoryRowsHtml}</tbody>
    </table>
  </section>
</main>
</body>
</html>`
}

function sanitizeFilenamePart(value: string): string {
  return value.replace(/[^A-Za-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 32) || 'run'
}

/**
 * Generate and download a self-contained offline single-file HTML diagnostic report.
 */
export function exportOfflineDiagnosticHtml(
  input: OfflineReportInput,
  options: ExportOfflineHtmlOptions = {},
): ExportOfflineHtmlResult {
  const html = buildOfflineDiagnosticHtml(input)
  const runPart = sanitizeFilenamePart(input.runId ?? input.trajectory?.runId ?? 'snapshot')
  const datePart = (input.generatedAt || new Date().toISOString()).slice(0, 10)
  const filename = options.filename || `tinadec-diagnostic-${runPart}-${datePart}.html`
  const bytes = new TextEncoder().encode(html).byteLength

  if (options.download) {
    options.download(filename, html)
  } else if (typeof window !== 'undefined' && typeof document !== 'undefined' && typeof Blob !== 'undefined') {
    const blob = new Blob([html], { type: 'text/html;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = filename
    link.style.display = 'none'
    document.body.appendChild(link)
    link.click()
    link.remove()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  }

  return { filename, html, bytes }
}
