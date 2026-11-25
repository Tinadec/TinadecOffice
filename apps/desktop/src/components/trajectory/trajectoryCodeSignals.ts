/**
 * Command, script, terminal, and code-frame execution aggregator, executor
 * normalizer, governance/runtime failure signal grouper, and structured
 * 4-direction remediation prompt builder for the Trajectory Code Execution view.
 */

import type {
  RunContextDiagnosticsDto,
  ToolExecutionTimelineItemDto,
} from '@/api'
import {
  detectToolGovernanceSignals,
  extractErrorCodeFrames,
  type CodeFrameLine,
  type GovernanceSignal,
  type TrajectoryCodeFrame,
  type TrajectoryNode,
  type TrajectorySummary,
} from '@/lib/trajectoryBuilder'

export type CodeExecutorKind = 'PowerShell' | 'Bash' | 'Terminal' | 'Other'
export type CodeExecutorFilter = 'all' | CodeExecutorKind

export type CodeFailureSignalSlug =
  | 'parser_error'
  | 'verification_failed'
  | 'worktree_guard'
  | 'timeout'
  | 'permission_denied'
  | 'stale_version'
  | 'execution_failed'

export type CodeFailureSignalCode =
  | 'ParserError'
  | 'VerificationFailed'
  | 'WorktreeGuard'
  | 'Timeout'
  | 'PermissionDenied'
  | 'StaleVersion'
  | 'ExecutionFailed'

export type CodeRemediationKey =
  | 'fix_impl'
  | 'recover_observe'
  | 'add_capability'
  | 'keep_reusable'

export interface CodeRemediationDraft {
  key: CodeRemediationKey
  i18nKey: string
  label: string
  descI18nKey: string
  description: string
  draft: string
}

export interface CodeExecutionInvocation {
  id: string
  executionId: string | null
  node: TrajectoryNode | null
  toolId: string
  toolDisplayName: string
  executor: CodeExecutorKind
  subProgram: string | null
  seqLabel: string
  seqOrder: number
  turnNo: number
  turnLabel: string | null
  status: string
  isError: boolean
  durationMs: number | null
  frames: TrajectoryCodeFrame[]
  primaryErrorMessage: string | null
  scriptSource: string
  feedbackText: string
  lineLocation: string
  lineNumber: number | null
  columnNumber: number | null
  filePath: string | null
  cwd: string | null
  charCount: number
  errorSignalCode: string
  signalSlug: CodeFailureSignalSlug | null
  signalCode: CodeFailureSignalCode | null
  signalLabel: string | null
  signalI18nKey: string | null
  isSpecificSignal: boolean
  governanceSignals: GovernanceSignal[]
  remediations: CodeRemediationDraft[]
}

export interface CodeSignalGroup {
  slug: CodeFailureSignalSlug
  code: CodeFailureSignalCode
  label: string
  i18nKey: string
  isSpecificSignal: boolean
  count: number
  turnCount: number
  primaryExecutor: CodeExecutorKind
  subProgramSummary: string
  subtitle: string
  items: CodeExecutionInvocation[]
}

export interface CodeExecutionSignalsReport {
  runId: string | null
  sessionId: string | null
  executorFilter: CodeExecutorFilter
  totalCalls: number
  errorCount: number
  detectedSignalCount: number
  unfilteredTotalCalls: number
  unfilteredErrorCount: number
  unfilteredDetectedSignalCount: number
  invocations: CodeExecutionInvocation[]
  failedInvocations: CodeExecutionInvocation[]
  succeededInvocations: CodeExecutionInvocation[]
  signalGroups: CodeSignalGroup[]
}

export interface AggregateCodeSignalsInput {
  trajectory?: TrajectorySummary | null
  toolExecutions?: ToolExecutionTimelineItemDto[] | null
  diagnostics?: RunContextDiagnosticsDto | null
  runId?: string | null
  sessionId?: string | null
  executorFilter?: CodeExecutorFilter | null
}

const COMMAND_TOOL_TOKEN_RE =
  /(^|[./:_-])(bash|pwsh|powershell|ps1|sh|zsh|ash|dash|cmd|run_command|shell|shell_command|subprocess|terminal[a-z0-9_-]*|exec[a-z0-9_-]*)($|[./:_-])/i

const PWSH_TOOL_RE = /(^|[./:_-])(pwsh|powershell|ps1)($|[./:_-])/i
const BASH_TOOL_RE = /(^|[./:_-])(bash|sh|zsh|ash|dash)($|[./:_-])/i
const TERMINAL_TOOL_RE =
  /(^|[./:_-])(cmd|run_command|shell|shell_command|subprocess|terminal[a-z0-9_-]*|exec[a-z0-9_-]*)($|[./:_-])/i

const FILE_EDIT_ONLY_TOOL_RE =
  /(^|[./:_-])(read_file|edit_file|write_file|patch_file|apply_patch|multi_edit|create_file|delete_file)($|[./:_-])/i

function isErrorStatus(status?: string | null): boolean {
  const s = (status ?? '').trim().toLowerCase()
  return (
    s === 'failed'
    || s === 'error'
    || s === 'rejected'
    || s === 'denied'
    || s === 'approval_rejected'
    || s === 'timeout'
    || s === 'timed_out'
    || s === 'cancelled'
    || s === 'aborted'
  )
}

/**
 * Determine whether a trajectory node / tool execution belongs in the Code Execution analysis view.
 */
export function isCodeExecutionCandidate(input: {
  kind?: TrajectoryNode['kind'] | null
  toolId?: string | null
  label?: string | null
  command?: string | null
  codeFrames?: TrajectoryCodeFrame[] | null
}): boolean {
  if (input.kind === 'run' || input.kind === 'turn') return false
  const hasCommand = Boolean(input.command && input.command.trim().length > 0)
  if (input.kind === 'model_call') {
    return hasCommand
  }
  const hasFrames = Boolean(input.codeFrames && input.codeFrames.length > 0)
  if (hasCommand || hasFrames) return true

  const rawId = (input.toolId || input.label || '').trim()
  if (!rawId) return false
  return COMMAND_TOOL_TOKEN_RE.test(rawId)
}

/**
 * Normalize executor kind into `'PowerShell' | 'Bash' | 'Terminal' | 'Other'`.
 */
export function normalizeExecutorKind(input: {
  toolId?: string | null
  command?: string | null
  feedbackText?: string | null
  errorCategory?: string | null
}): CodeExecutorKind {
  const toolId = (input.toolId ?? '').trim()
  const cmd = (input.command ?? '').trim()
  const feedback = `${input.errorCategory ?? ''}\n${input.feedbackText ?? ''}`

  if (PWSH_TOOL_RE.test(toolId)) return 'PowerShell'
  if (BASH_TOOL_RE.test(toolId)) return 'Bash'

  // Inspect command prefix / syntax signatures when toolId is generic (e.g. run_command / terminal / cmd)
  if (/^\s*(pwsh|powershell)(\.exe)?\b/i.test(cmd)) return 'PowerShell'
  if (/^\s*(bash|sh|zsh)\b|^#!\/(usr\/bin\/env\s+)?(ba|z)?sh\b/i.test(cmd)) return 'Bash'

  if (TERMINAL_TOOL_RE.test(toolId) || (!toolId && cmd.length > 0)) {
    if (
      /\b(Set-Location|Get-ChildItem|Select-Object|Where-Object|ForEach-Object|Invoke-RestMethod|Invoke-WebRequest|\$PSVersionTable|\$ErrorActionPreference)\b/i.test(cmd)
      || /\b(ParserError|FullyQualifiedErrorId|CommandNotFoundException)\b/i.test(feedback)
    ) {
      return 'PowerShell'
    }
    if (/\b(bash|zsh):\s*(line\s+\d+|syntax error)/i.test(feedback)) {
      return 'Bash'
    }
    return 'Terminal'
  }

  if (FILE_EDIT_ONLY_TOOL_RE.test(toolId)) {
    return 'Other'
  }

  if (cmd.length > 0) {
    if (/\bParserError\b/i.test(feedback)) return 'PowerShell'
    return 'Terminal'
  }

  return 'Other'
}

function detectSubProgram(
  executor: CodeExecutorKind,
  toolId: string,
  scriptSource: string,
): string | null {
  const trimmed = scriptSource.trim()
  if (trimmed) {
    // Strip leading cd / Set-Location segment if chained with ; or &&
    const segments = trimmed
      .split(/\s*(?:&&|;|\r?\n)\s*/)
      .map((s) => s.trim())
      .filter(Boolean)
    const activeSeg =
      segments.find((s) => !/^(cd|Set-Location|pushd|export|\$[A-Za-z_]\w*\s*=)\b/i.test(s))
      ?? segments[0]
      ?? ''
    const firstToken = activeSeg.match(/^([A-Za-z0-9_./\\-]+)\b/)?.[1] ?? ''
    const base = firstToken.split(/[\\/]/).pop()?.replace(/\.exe$/i, '') ?? ''
    const lower = base.toLowerCase()
    if (lower === 'node' || lower === 'npx' || lower === 'pnpm' || lower === 'npm' || lower === 'yarn' || lower === 'vitest' || lower === 'tsc' || lower === 'vue-tsc') {
      return 'Node'
    }
    if (lower === 'bun' || lower === 'bunx') return 'Bun'
    if (lower === 'python' || lower === 'python3' || lower === 'pytest' || lower === 'uv') return 'Python'
    if (lower === 'cargo' || lower === 'rustc') return 'Cargo'
    if (lower === 'git') return 'Git'
    if (lower === 'rg' || lower === 'grep') return lower
    if (lower === 'pwsh' || lower === 'powershell') return 'PowerShell'
    if (lower === 'bash' || lower === 'sh' || lower === 'zsh') return 'Bash'
  }
  if (executor === 'PowerShell') return 'PowerShell'
  if (executor === 'Bash') return 'Bash'
  return toolId || null
}

interface SignalClassification {
  slug: CodeFailureSignalSlug
  code: CodeFailureSignalCode
  label: string
  i18nKey: string
  isSpecificSignal: boolean
  errorSignalCode: string
}

const SIGNAL_ORDER: Record<CodeFailureSignalSlug, number> = {
  parser_error: 1,
  verification_failed: 2,
  worktree_guard: 3,
  timeout: 4,
  permission_denied: 5,
  stale_version: 6,
  execution_failed: 99,
}

function resolveErrorSignalBadge(
  status: string,
  errorCategory: string | null | undefined,
  feedbackText: string,
  signals: GovernanceSignal[],
): string {
  if (signals.some((s) => s.code === 'TIMEOUT') || status.toLowerCase() === 'timeout' || status.toLowerCase() === 'timed_out') {
    return 'TIMEOUT'
  }
  if (signals.some((s) => s.code === 'WORKTREE_GUARD')) return 'WORKTREE_GUARD'
  if (signals.some((s) => s.code === 'WRITE_SCOPE_LOCKED')) return 'WRITE_SCOPE_LOCKED'
  if (signals.some((s) => s.code === 'APPROVAL_REJECTED')) return 'APPROVAL_REJECTED'
  if (signals.some((s) => s.code === 'FS_STALE_VERSION')) return 'FS_STALE_VERSION'
  if (signals.some((s) => s.code === 'FS_NOT_OBSERVED')) return 'FS_NOT_OBSERVED'

  const exitMatch = feedbackText.match(/\b(?:exit(?:\s+code|\s+status)?|exited\s+with(?:\s+code)?)\s*[:=]?\s*(\d+)\b/i)
  if (exitMatch?.[1]) {
    return `EXIT_${exitMatch[1]}`
  }
  if (errorCategory && /^exit[_-]?\d+$/i.test(errorCategory.trim())) {
    return errorCategory.trim().toUpperCase().replace('-', '_')
  }
  const sUpper = status.trim().toUpperCase()
  if (sUpper === 'FAILED' || sUpper === 'ERROR') return 'EXIT_1'
  return sUpper || 'EXIT_1'
}

/**
 * Classify a failed command/code invocation into one of the canonical failure signal groups.
 */
export function classifyCodeFailureSignal(input: {
  status: string
  errorCategory?: string | null
  scriptSource: string
  feedbackText: string
  signals: GovernanceSignal[]
}): SignalClassification {
  const errCat = (input.errorCategory ?? '').toLowerCase()
  const statusLower = (input.status ?? '').toLowerCase()
  const haystack = `${errCat}\n${input.feedbackText}\n${input.scriptSource}`.toLowerCase()
  const sigCodes = new Set(input.signals.map((s) => s.code))
  const errorSignalCode = resolveErrorSignalBadge(
    input.status,
    input.errorCategory,
    input.feedbackText,
    input.signals,
  )

  // 1. WorktreeGuard / WriteScope isolation
  if (
    sigCodes.has('WORKTREE_GUARD')
    || sigCodes.has('WRITE_SCOPE_LOCKED')
    || /\b(worktree[_\s-]*guard|outside worktree|worktree isolation|dirty worktree|branch guard|write_scope_locked|outside write_scope|scope[._\s-]*violation|lease[._\s-]*conflict)\b/i.test(haystack)
  ) {
    return {
      slug: 'worktree_guard',
      code: 'WorktreeGuard',
      label: '工作树拦截',
      i18nKey: 'trajectoryCode.signals.worktreeBlocked',
      isSpecificSignal: true,
      errorSignalCode,
    }
  }

  // 2. Timeout
  if (
    sigCodes.has('TIMEOUT')
    || statusLower === 'timeout'
    || statusLower === 'timed_out'
    || /\b(timed?\s*out|timeout|deadline exceeded|etimedout)\b/i.test(haystack)
  ) {
    return {
      slug: 'timeout',
      code: 'Timeout',
      label: '超时',
      i18nKey: 'trajectoryCode.signals.timeout',
      isSpecificSignal: true,
      errorSignalCode,
    }
  }

  // 3. PermissionDenied / ApprovalRejected
  if (
    sigCodes.has('APPROVAL_REJECTED')
    || statusLower === 'rejected'
    || statusLower === 'denied'
    || statusLower === 'approval_rejected'
    || /\b(approval[_\s-]*(rejected|denied)|permission[_\s-]*denied|access is denied|unauthorizedaccessexception|eacces|eperm|operation not permitted|user rejected)\b/i.test(haystack)
  ) {
    return {
      slug: 'permission_denied',
      code: 'PermissionDenied',
      label: '权限拒绝',
      i18nKey: 'trajectoryCode.signals.permissionDenied',
      isSpecificSignal: true,
      errorSignalCode,
    }
  }

  // 4. StaleVersion / File conflict
  if (
    sigCodes.has('FS_STALE_VERSION')
    || sigCodes.has('FS_NOT_OBSERVED')
    || /\b(fs_stale_version|stale[_\s-]*version|version_conflict|line_hash mismatch|hash mismatch|etag mismatch|modified since last read|fs_not_observed|file[_\s-]*not[_\s-]*observed|must read before edit|merge conflict)\b/i.test(haystack)
  ) {
    return {
      slug: 'stale_version',
      code: 'StaleVersion',
      label: '文件冲突',
      i18nKey: 'trajectoryCode.signals.staleVersion',
      isSpecificSignal: true,
      errorSignalCode,
    }
  }

  // 5. ParserError / Shell or Script Syntax Error
  if (
    /\b(parsererror|syntaxerror|syntax[_\s-]*error|parse[_\s-]*error|missing argument in parameter list|missingexpression|emptypipeelement|missingendparenthesis|missingendcurlybrace|terminatorexpectedatendofstring|unexpected token|unexpected eof|unterminated quoted string)\b/i.test(haystack)
    || /unmatched\s+['"`]/i.test(haystack)
  ) {
    return {
      slug: 'parser_error',
      code: 'ParserError',
      label: '脚本语法错误',
      i18nKey: 'trajectoryCode.signals.syntaxError',
      isSpecificSignal: true,
      errorSignalCode,
    }
  }

  // 6. VerificationFailed (tests, assertions, typecheck, lint)
  if (
    /\b(verification[_\s-]*failed|assertionerror|test[s]?\s+failed|failed\s+tests|error\s+ts\d+|typecheck\s+failed|lint\s+failed)\b/i.test(haystack)
    || /\bfail\s+\S+\.(test|spec)\.[A-Za-z0-9]+/i.test(haystack)
    || /expect\(.*\)\.(to|not)\b/i.test(haystack)
    || /\b(vitest|jest|pytest|cargo\s+test|go\s+test|bun\s+test|npm\s+test|pnpm\s+test|yarn\s+test|vue-tsc|tsc|typecheck|eslint|oxlint|biome\s+check)\b/i.test(input.scriptSource)
  ) {
    return {
      slug: 'verification_failed',
      code: 'VerificationFailed',
      label: '验证未通过',
      i18nKey: 'trajectoryCode.signals.verificationFailed',
      isSpecificSignal: true,
      errorSignalCode,
    }
  }

  // 7. Generic fallback
  return {
    slug: 'execution_failed',
    code: 'ExecutionFailed',
    label: '执行失败',
    i18nKey: 'trajectoryCode.signals.executionFailed',
    isSpecificSignal: false,
    errorSignalCode,
  }
}

interface NormalizedFrameMetadata {
  frames: TrajectoryCodeFrame[]
  lineNumber: number | null
  columnNumber: number | null
  filePath: string | null
  lineLocation: string
  primaryErrorMessage: string | null
}

function normalizeInvocationFrames(input: {
  id: string
  isError: boolean
  rawFrames: TrajectoryCodeFrame[]
  scriptSource: string
  feedbackText: string
  resourcePath: string | null
  cwd: string | null
  errorCategory: string | null
  signalCode: CodeFailureSignalCode | null
}): NormalizedFrameMetadata {
  let lineNumber: number | null = null
  let columnNumber: number | null = null
  let filePath: string | null = input.resourcePath

  // Clone frames so we never mutate shared TrajectoryNode state
  const frames: TrajectoryCodeFrame[] = input.rawFrames.map((f) => ({
    ...f,
    lines: f.lines.map((l) => ({ ...l })),
  }))

  for (const f of frames) {
    if (f.filePath && !filePath) filePath = f.filePath
    if (f.line && lineNumber === null) lineNumber = f.line
    if (f.column && columnNumber === null) columnNumber = f.column
    for (const fl of f.lines) {
      if (typeof fl.lineNumber === 'number' && fl.lineNumber > 0 && lineNumber === null) {
        lineNumber = fl.lineNumber
      }
    }
  }

  // Detect explicit line numbers in feedback text (PowerShell `1 | ...`, `line 4`, `file.ts:18:2`)
  if (lineNumber === null && input.feedbackText) {
    const pwshLine = input.feedbackText.match(/^\s*(?:>|→)?\s*(\d+)\s*\|/m)
    const fileCol = input.feedbackText.match(
      /((?:[A-Za-z]:[\\/]|[./\\]|\b[\w-]+[/\\])[\w./\\-]+\.[A-Za-z0-9]{1,8})(?::(\d+)(?::(\d+))?|\((\d+),(\d+)\))/,
    )
    const bashLine = input.feedbackText.match(/\bline\s+(\d+)(?::(\d+))?\b/i)
    if (fileCol) {
      if (!filePath) filePath = fileCol[1] ?? null
      lineNumber = Number.parseInt(fileCol[2] ?? fileCol[4] ?? '', 10) || null
      columnNumber = Number.parseInt(fileCol[3] ?? fileCol[5] ?? '', 10) || null
    } else if (pwshLine?.[1]) {
      lineNumber = Number.parseInt(pwshLine[1], 10) || null
    } else if (bashLine?.[1]) {
      lineNumber = Number.parseInt(bashLine[1], 10) || null
      columnNumber = bashLine[2] ? Number.parseInt(bashLine[2], 10) || null : null
    }
  }

  if (input.isError && lineNumber === null && input.scriptSource.trim().length > 0) {
    lineNumber = 1
  }

  // Ensure failed invocations always have a clean highlighted code frame
  if (input.isError) {
    if (frames.length === 0) {
      const scriptFirstLine = input.scriptSource.split(/\r?\n/)[0]?.trim()
      const fallbackLineText =
        scriptFirstLine
        || input.feedbackText.split(/\r?\n/).find((l) => l.trim().length > 0)?.trim()
        || 'Command execution failed'
      const lines: CodeFrameLine[] = [
        {
          lineNumber: lineNumber ?? 1,
          text: fallbackLineText,
          highlight: true,
        },
      ]
      frames.push({
        id: `code-frame:${input.id}:0`,
        title: filePath
          ? lineNumber
            ? `${filePath}:${lineNumber}`
            : filePath
          : scriptFirstLine
            ? `$ ${scriptFirstLine}`
            : input.id,
        filePath,
        line: lineNumber ?? 1,
        column: columnNumber,
        command: input.scriptSource || null,
        cwd: input.cwd,
        errorCategory: input.errorCategory ?? input.signalCode,
        lines,
      })
    } else {
      const firstFrame = frames[0]!
      if (!firstFrame.line && lineNumber !== null) {
        firstFrame.line = lineNumber
      }
      const numberedLines = firstFrame.lines.filter(
        (l) => typeof l.lineNumber === 'number' && l.lineNumber > 0,
      )
      if (numberedLines.length > 0) {
        const anyNumberedHighlighted = numberedLines.some((l) => l.highlight)
        if (!anyNumberedHighlighted) {
          const targetLn = lineNumber ?? numberedLines[0]!.lineNumber
          for (const l of firstFrame.lines) {
            l.highlight = l.lineNumber === targetLn
          }
        } else {
          // Un-highlight pure wrapper headers like `ParserError:` when actual numbered source lines exist
          for (const l of firstFrame.lines) {
            if (l.lineNumber === null || l.lineNumber === undefined) {
              l.highlight = false
            }
          }
        }
      } else if (!firstFrame.lines.some((l) => l.highlight) && firstFrame.lines[0]) {
        firstFrame.lines[0].highlight = true
      }
    }
  }

  // Extract concise primary error message from feedback lines
  let primaryErrorMessage: string | null = null
  if (input.isError && input.feedbackText) {
    const rawLines = input.feedbackText
      .split(/\r?\n/)
      .map((l) => l.trim())
      .filter(Boolean)
    const pipeMsg = rawLines
      .map((l) => l.match(/^\|\s*(?!~+\s*$)(.+)$/)?.[1]?.trim())
      .find(Boolean)
    if (pipeMsg) {
      primaryErrorMessage = pipeMsg
    } else {
      const diagnosticLine = rawLines.find(
        (l) =>
          !/^(ParserError:?|Line\s*\||>\s*\d+\s*\||\d+\s*\||~+)$/i.test(l)
          && /(error|missing|failed|denied|rejected|timeout|timed out|conflict|mismatch|outside|violation|unexpected|cannot|not found)/i.test(l),
      )
      primaryErrorMessage = diagnosticLine ?? rawLines[0] ?? null
    }
  }

  const lineLocation =
    lineNumber !== null
      ? columnNumber !== null
        ? `L${lineNumber}:${columnNumber}`
        : `L${lineNumber}`
      : filePath || '—'

  return {
    frames,
    lineNumber,
    columnNumber,
    filePath,
    lineLocation,
    primaryErrorMessage,
  }
}

const REMEDIATION_SPECS: Array<{
  key: CodeRemediationKey
  i18nKey: string
  label: string
  descI18nKey: string
  description: string
  leadPrompt: string
  inspectTargets: string
}> = [
  {
    key: 'fix_impl',
    i18nKey: 'trajectoryCode.directions.fixImpl',
    label: '修复环境与实现',
    descI18nKey: 'trajectoryCode.directions.fixImplDesc',
    description: '已有反馈指向输入、执行条件或具体实现；沿原调用定位可调整的位置。',
    leadPrompt:
      '请围绕「修复环境与实现」诊断这次代码/命令执行失败：核对当前执行器语法规则、参数分隔、工作目录与源码实现，在原调用定位处给出最小可验证修正。',
    inspectTargets: '`run_diagnostics`, `tools`, `context_audit`',
  },
  {
    key: 'recover_observe',
    i18nKey: 'trajectoryCode.directions.recoverObserve',
    label: '恢复与观察',
    descI18nKey: 'trajectoryCode.directions.recoverObserveDesc',
    description: '沿后续查看如何继续，恢复前核对已产生的效果。',
    leadPrompt:
      '请围绕「恢复与观察」诊断这次代码/命令执行失败：在重试或恢复前，先核查本次失败前已产生的文件副作用、工作树状态与文件观测版本锁，再给出安全的下一步观察与恢复顺序。',
    inspectTargets: '`run_diagnostics`, `context_audit`, `agents`',
  },
  {
    key: 'add_capability',
    i18nKey: 'trajectoryCode.directions.addCapability',
    label: '补齐能力',
    descI18nKey: 'trajectoryCode.directions.addCapabilityDesc',
    description: '核对能力是否存在、已加载且当前可用。',
    leadPrompt:
      '请围绕「补齐能力」诊断这次代码/命令执行失败：检查所需命令行程序、工具权限、写范围 (write_scope) 或技能是否在当前执行环境中存在、已加载且被治理策略允许。',
    inspectTargets: '`tools`, `skills`, `agents`, `modes`',
  },
  {
    key: 'keep_reusable',
    i18nKey: 'trajectoryCode.directions.keepReusable',
    label: '保留可复用方法',
    descI18nKey: 'trajectoryCode.directions.keepReusableDesc',
    description: '重复出现时，先核对触发条件及当时已有的指导。',
    leadPrompt:
      '请围绕「保留可复用方法」诊断这次代码/命令执行失败：分析该错误信号的触发条件与现有项目指令/技能提示之间的缺口，提炼一条简明可复用的命令规范或防呆检查，避免后续回合重复踩坑。',
    inspectTargets: '`run_diagnostics`, `skills`, `context_memory`, `context_audit`',
  },
]

/**
 * Build a structured Markdown remediation prompt carrying exact code-frame lines,
 * executor, line/file location, governance/runtime signal, and `run_id`.
 */
export function buildCodeRemediationDraft(
  key: CodeRemediationKey,
  invocation: Omit<CodeExecutionInvocation, 'remediations'>,
  context: { runId?: string | null; sessionId?: string | null },
): CodeRemediationDraft {
  const spec = REMEDIATION_SPECS.find((s) => s.key === key) ?? REMEDIATION_SPECS[0]!
  const runId = context.runId ?? 'unknown-run'
  const lines: string[] = [
    spec.leadPrompt,
    '',
    '### 代码执行定位证据 (Code Execution Evidence)',
    `- **remediation_direction**: \`${spec.key}\` (${spec.label})`,
    `- **run_id**: \`${runId}\``,
  ]

  if (context.sessionId) {
    lines.push(`- **session_id**: \`${context.sessionId}\``)
  }
  lines.push(`- **turn**: \`#${invocation.turnNo}\` · **seq**: \`${invocation.seqLabel}\``)
  lines.push(`- **executor**: \`${invocation.executor}\` (tool: \`${invocation.toolId}\`)`)
  lines.push(
    `- **location**: \`${invocation.lineLocation}\`${invocation.filePath ? ` (${invocation.filePath})` : ''}${invocation.cwd ? ` · cwd: \`${invocation.cwd}\`` : ''}`,
  )
  if (invocation.signalCode) {
    lines.push(
      `- **signal**: \`${invocation.signalCode}\` (${invocation.signalLabel ?? invocation.signalCode}) · **error_code**: \`${invocation.errorSignalCode}\``,
    )
  } else {
    lines.push(`- **status**: \`${invocation.status}\``)
  }

  if (invocation.governanceSignals.length > 0) {
    const govText = invocation.governanceSignals
      .map((s) => (s.count && s.count > 1 ? `${s.code} (x${s.count})` : s.code))
      .join(', ')
    lines.push(`- **governance_signals**: ${govText}`)
  }

  if (invocation.scriptSource.trim()) {
    lines.push('', '### 原始命令 / 脚本 (Script)')
    lines.push('```text')
    lines.push(invocation.scriptSource.trim())
    lines.push('```')
  }

  if (invocation.frames.length > 0) {
    const frame = invocation.frames[0]!
    lines.push('', `### 出错代码帧 (${frame.title})`)
    lines.push('```text')
    for (const fl of frame.lines.slice(0, 8)) {
      const ln = fl.lineNumber !== null && fl.lineNumber !== undefined ? `${fl.lineNumber} | ` : '  | '
      lines.push(`${fl.highlight ? '> ' : '  '}${ln}${fl.text}`)
    }
    lines.push('```')
  } else if (invocation.feedbackText.trim()) {
    lines.push('', '### 执行反馈摘要 (Feedback)')
    lines.push('```text')
    lines.push(invocation.feedbackText.trim().slice(0, 600))
    lines.push('```')
  }

  lines.push(
    '',
    `请先调用只读 \`tinadec_inspect\`（建议目标：${spec.inspectTargets}）核实 \`${runId}\` 的实时上下文与工具配置，再给出有证据支撑的处置方案。`,
  )

  return {
    key: spec.key,
    i18nKey: spec.i18nKey,
    label: spec.label,
    descI18nKey: spec.descI18nKey,
    description: spec.description,
    draft: lines.join('\n'),
  }
}

/**
 * Build a group-level diagnostic draft (`分析这组`) summarizing recurring failures in a signal group.
 */
export function buildCodeSignalGroupDraft(
  group: CodeSignalGroup,
  context: { runId?: string | null; sessionId?: string | null },
): string {
  const runId = context.runId ?? 'unknown-run'
  const sample = group.items[0]
  const lines: string[] = [
    `请分析本组重复/同类代码执行失败信号「${group.label} (${group.code})」：共 ${group.count} 条报错跨越 ${group.turnCount} 个轮次（主执行器：${group.primaryExecutor}）。`,
    '',
    '### 失败信号归组证据 (Signal Group Summary)',
    `- **run_id**: \`${runId}\``,
  ]
  if (context.sessionId) {
    lines.push(`- **session_id**: \`${context.sessionId}\``)
  }
  lines.push(`- **signal_group**: \`${group.code}\` (${group.label}) — ${group.count} errors across ${group.turnCount} turn(s)`)
  lines.push(`- **calls**: ${group.items.slice(0, 6).map((item) => `第${item.turnNo}轮 ${item.seqLabel} (${item.toolId} @ ${item.lineLocation})`).join(', ')}`)

  if (sample && sample.frames.length > 0) {
    const frame = sample.frames[0]!
    lines.push('', `### 代表性出错代码帧 (Turn #${sample.turnNo} · ${sample.seqLabel})`)
    lines.push('```text')
    for (const fl of frame.lines.slice(0, 6)) {
      const ln = fl.lineNumber !== null && fl.lineNumber !== undefined ? `${fl.lineNumber} | ` : '  | '
      lines.push(`${fl.highlight ? '> ' : '  '}${ln}${fl.text}`)
    }
    lines.push('```')
  }

  lines.push(
    '',
    `请先使用只读 \`tinadec_inspect\` 核实 \`${runId}\` 的运行诊断与工具/技能约束，定位同组失败的共同根因并给出一次性收敛建议。`,
  )
  return lines.join('\n')
}

function composeFeedbackText(
  node: TrajectoryNode | null,
  exec: ToolExecutionTimelineItemDto | null,
): string {
  const parts: string[] = []
  const seen = new Set<string>()
  const pushLine = (text?: string | null) => {
    const trimmed = (text ?? '').replace(/\r\n/g, '\n').trim()
    if (!trimmed || seen.has(trimmed)) return
    seen.add(trimmed)
    parts.push(trimmed)
  }

  for (const ev of exec?.evidence ?? node?.evidence ?? []) {
    pushLine(ev)
  }
  pushLine(exec?.summary ?? node?.summary)
  pushLine(exec?.checkpoint_summary)
  return parts.join('\n')
}

function formatSeqLabel(
  exec: ToolExecutionTimelineItemDto | null,
  ordinal: number,
): { seqLabel: string; seqOrder: number } {
  if (exec && typeof exec.requested_seq === 'number' && exec.requested_seq > 0) {
    if (typeof exec.updated_seq === 'number' && exec.updated_seq > 0 && exec.updated_seq !== exec.requested_seq) {
      return {
        seqLabel: `#${exec.requested_seq} → #${exec.updated_seq}`,
        seqOrder: exec.requested_seq,
      }
    }
    return {
      seqLabel: `#${exec.requested_seq}`,
      seqOrder: exec.requested_seq,
    }
  }
  return {
    seqLabel: `#${ordinal}`,
    seqOrder: ordinal,
  }
}

function buildSignalGroups(failed: CodeExecutionInvocation[]): CodeSignalGroup[] {
  const bySlug = new Map<CodeFailureSignalSlug, CodeExecutionInvocation[]>()
  for (const item of failed) {
    const slug = item.signalSlug ?? 'execution_failed'
    const bucket = bySlug.get(slug)
    if (bucket) bucket.push(item)
    else bySlug.set(slug, [item])
  }

  const groups: CodeSignalGroup[] = []
  for (const [slug, items] of bySlug.entries()) {
    const sortedItems = [...items].sort(
      (a, b) => b.turnNo - a.turnNo || b.seqOrder - a.seqOrder || a.id.localeCompare(b.id),
    )
    const sample = sortedItems[0]!
    const turnSet = new Set(sortedItems.map((i) => i.turnNo))

    const execCounts = new Map<CodeExecutorKind, number>()
    const subCounts = new Map<string, number>()
    for (const i of sortedItems) {
      execCounts.set(i.executor, (execCounts.get(i.executor) ?? 0) + 1)
      if (i.subProgram) {
        subCounts.set(i.subProgram, (subCounts.get(i.subProgram) ?? 0) + 1)
      }
    }
    const primaryExecutor =
      [...execCounts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? sample.executor
    const topSub =
      [...subCounts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? primaryExecutor

    groups.push({
      slug,
      code: sample.signalCode ?? 'ExecutionFailed',
      label: sample.signalLabel ?? '执行失败',
      i18nKey: sample.signalI18nKey ?? 'trajectoryCode.signals.executionFailed',
      isSpecificSignal: sample.isSpecificSignal,
      count: sortedItems.length,
      turnCount: turnSet.size,
      primaryExecutor,
      subProgramSummary: topSub,
      subtitle: `${primaryExecutor} · ${topSub}`,
      items: sortedItems,
    })
  }

  return groups.sort(
    (a, b) =>
      b.count - a.count
      || (SIGNAL_ORDER[a.slug] ?? 50) - (SIGNAL_ORDER[b.slug] ?? 50),
  )
}

/**
 * Extract, normalize, classify, and group all command/script/code-frame tool
 * executions from `trajectory` + `toolExecutions`.
 */
export function aggregateCodeExecutionSignals(
  input: AggregateCodeSignalsInput,
): CodeExecutionSignalsReport {
  const traj = input.trajectory ?? null
  const rawToolExecs = input.toolExecutions ?? []
  const runId =
    input.runId
    ?? input.diagnostics?.run_id
    ?? traj?.runId
    ?? rawToolExecs[0]?.run_id
    ?? null
  const sessionId =
    input.sessionId
    ?? input.diagnostics?.session_id
    ?? traj?.sessionId
    ?? rawToolExecs[0]?.session_id
    ?? null
  const executorFilter: CodeExecutorFilter = input.executorFilter ?? 'all'

  const execById = new Map<string, ToolExecutionTimelineItemDto>()
  for (const ex of rawToolExecs) {
    execById.set(ex.id, ex)
  }

  const visitedExecIds = new Set<string>()
  const allInvocations: CodeExecutionInvocation[] = []
  let ordinalCounter = 0

  const pushCandidate = (
    node: TrajectoryNode | null,
    exec: ToolExecutionTimelineItemDto | null,
    turnNo: number,
    turnLabel: string | null,
  ) => {
    const toolId = exec?.tool_id || node?.toolId || node?.label || 'command'
    const rawCommand = exec?.command ?? node?.command ?? null
    const baseFrames =
      node && node.codeFrames.length > 0
        ? node.codeFrames
        : exec
          ? extractErrorCodeFrames(exec)
          : []

    if (
      !isCodeExecutionCandidate({
        kind: node?.kind ?? 'tool_call',
        toolId,
        label: exec?.tool_display_name ?? node?.label ?? null,
        command: rawCommand,
        codeFrames: baseFrames,
      })
    ) {
      return
    }

    ordinalCounter += 1
    const id = node?.id ?? (exec ? `tool:${exec.id}` : `cmd:${ordinalCounter}`)
    const status = exec?.status || node?.status || 'completed'
    const isError = Boolean(
      node?.isError
      || isErrorStatus(status)
      || exec?.tool_success === false
      || Boolean(exec?.error_category),
    )
    const durationMs =
      node?.durationMs !== null && node?.durationMs !== undefined
        ? node.durationMs
        : typeof exec?.duration_ms === 'number' && Number.isFinite(exec.duration_ms)
          ? exec.duration_ms
          : null

    const scriptSource =
      rawCommand
      ?? baseFrames[0]?.command
      ?? exec?.arguments_summary
      ?? node?.argumentsSummary
      ?? ''
    const feedbackText = composeFeedbackText(node, exec)
    const errorCategory = exec?.error_category ?? baseFrames[0]?.errorCategory ?? null
    const governanceSignals =
      node && node.signals.length > 0
        ? [...node.signals]
        : exec
          ? detectToolGovernanceSignals(exec)
          : []

    const executor = normalizeExecutorKind({
      toolId,
      command: scriptSource,
      feedbackText,
      errorCategory,
    })
    const subProgram = detectSubProgram(executor, toolId, scriptSource)

    const classification = isError
      ? classifyCodeFailureSignal({
          status,
          errorCategory,
          scriptSource,
          feedbackText,
          signals: governanceSignals,
        })
      : null

    const normalizedFrameMeta = normalizeInvocationFrames({
      id,
      isError,
      rawFrames: baseFrames,
      scriptSource,
      feedbackText,
      resourcePath: exec?.resource_path ?? node?.resourcePath ?? null,
      cwd: exec?.cwd ?? null,
      errorCategory,
      signalCode: classification?.code ?? null,
    })

    const { seqLabel, seqOrder } = formatSeqLabel(exec, ordinalCounter)
    const charSource = feedbackText || scriptSource
    const charCount = charSource.length

    const baseInvocation: Omit<CodeExecutionInvocation, 'remediations'> = {
      id,
      executionId: exec?.id ?? null,
      node,
      toolId,
      toolDisplayName: exec?.tool_display_name || node?.label || toolId,
      executor,
      subProgram,
      seqLabel,
      seqOrder,
      turnNo,
      turnLabel,
      status,
      isError,
      durationMs,
      frames: normalizedFrameMeta.frames,
      primaryErrorMessage: normalizedFrameMeta.primaryErrorMessage,
      scriptSource,
      feedbackText,
      lineLocation: normalizedFrameMeta.lineLocation,
      lineNumber: normalizedFrameMeta.lineNumber,
      columnNumber: normalizedFrameMeta.columnNumber,
      filePath: normalizedFrameMeta.filePath,
      cwd: exec?.cwd ?? normalizedFrameMeta.frames[0]?.cwd ?? null,
      charCount,
      errorSignalCode: classification?.errorSignalCode ?? status.toUpperCase(),
      signalSlug: classification?.slug ?? null,
      signalCode: classification?.code ?? null,
      signalLabel: classification?.label ?? null,
      signalI18nKey: classification?.i18nKey ?? null,
      isSpecificSignal: classification?.isSpecificSignal ?? false,
      governanceSignals,
    }

    const remediations = REMEDIATION_SPECS.map((spec) =>
      buildCodeRemediationDraft(spec.key, baseInvocation, { runId, sessionId }),
    )

    allInvocations.push({
      ...baseInvocation,
      remediations,
    })
  }

  if (traj?.root) {
    const turns = traj.root.children
    for (let tIdx = 0; tIdx < turns.length; tIdx++) {
      const turnNode = turns[tIdx]!
      const turnNo = tIdx + 1
      const walkTurnChild = (n: TrajectoryNode) => {
        const rawExecId = n.id.startsWith('tool:') ? n.id.slice(5) : n.id
        const matchedExec = execById.get(rawExecId) ?? null
        if (matchedExec) visitedExecIds.add(matchedExec.id)
        pushCandidate(n, matchedExec, turnNo, turnNode.label)
        for (const sub of n.children) walkTurnChild(sub)
      }
      for (const child of turnNode.children) {
        walkTurnChild(child)
      }
    }
  }

  // Include any unvisited raw toolExecutions (e.g. standalone list passed without full tree)
  const remainingExecs = rawToolExecs.filter((ex) => !visitedExecIds.has(ex.id))
  for (const ex of remainingExecs) {
    pushCandidate(null, ex, 1, null)
  }

  // Sort invocations most-recent first for clean inspection
  const sortedAll = [...allInvocations].sort(
    (a, b) => b.turnNo - a.turnNo || b.seqOrder - a.seqOrder || a.id.localeCompare(b.id),
  )

  const unfilteredFailed = sortedAll.filter((i) => i.isError)
  const unfilteredDetected = unfilteredFailed.filter((i) => i.isSpecificSignal).length

  const filteredInvocations =
    executorFilter === 'all'
      ? sortedAll
      : sortedAll.filter((i) => i.executor === executorFilter)

  const failedInvocations = filteredInvocations.filter((i) => i.isError)
  const succeededInvocations = filteredInvocations.filter((i) => !i.isError)
  const detectedSignalCount = failedInvocations.filter((i) => i.isSpecificSignal).length
  const signalGroups = buildSignalGroups(failedInvocations)

  return {
    runId,
    sessionId,
    executorFilter,
    totalCalls: filteredInvocations.length,
    errorCount: failedInvocations.length,
    detectedSignalCount,
    unfilteredTotalCalls: sortedAll.length,
    unfilteredErrorCount: unfilteredFailed.length,
    unfilteredDetectedSignalCount: unfilteredDetected,
    invocations: filteredInvocations,
    failedInvocations,
    succeededInvocations,
    signalGroups,
  }
}
