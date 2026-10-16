import { ref, computed, type ComputedRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type ModelStreamChunkDto } from '../api'
import type { Translate } from '../i18n'
import type { GitStatusFile, GitDiffSection } from './useGitOperation'
import { statusToLabel } from './useGitOperation'

// ---- Types ----

export type AiRiskLevel = 'low' | 'medium' | 'high' | 'critical'

export interface AiChangeConcern {
  severity: AiRiskLevel
  category: string
  description: string
}

export interface AiChangeAnalysis {
  summary: string
  riskLevel: AiRiskLevel
  riskScore: number
  concerns: AiChangeConcern[]
  affectedAreas: string[]
  testSuggestions: string[]
  explanation: string
}

// ---- Local heuristics (no AI call needed) ----

const RISK_WEIGHTS = {
  deletion: 4,
  config: 2,
  test: 1,
  security: 5,
  api: 3,
  largeDiff: 3,
  conflict: 5,
  lockfile: 3,
}

function isSecuritySensitive(path: string): boolean {
  const lower = path.toLowerCase()
  return (
    lower.includes('auth') ||
    lower.includes('password') ||
    lower.includes('secret') ||
    lower.includes('token') ||
    lower.includes('credential') ||
    lower.includes('permission') ||
    lower.includes('encrypt') ||
    lower.includes('hash') ||
    lower.includes('jwt') ||
    lower.includes('csrf') ||
    lower.includes('xss') ||
    lower.includes('sql') ||
    lower.includes('injection')
  )
}

function isApiSurface(path: string): boolean {
  const lower = path.toLowerCase()
  return (
    lower.includes('controller') ||
    lower.includes('route') ||
    lower.includes('endpoint') ||
    lower.includes('api') ||
    lower.includes('schema') ||
    lower.includes('openapi') ||
    lower.includes('grpc') ||
    lower.includes('proto')
  )
}

function isLockfile(path: string): boolean {
  const lower = path.toLowerCase()
  return lower.endsWith('package-lock.json') || lower.endsWith('yarn.lock') || lower.endsWith('pnpm-lock.yaml') || lower.endsWith('bun.lockb') || lower.endsWith('cargo.lock') || lower.endsWith('poetry.lock')
}

function isConfigFile(path: string): boolean {
  const lower = path.toLowerCase()
  return (
    lower.endsWith('.json') ||
    lower.endsWith('.yaml') ||
    lower.endsWith('.yml') ||
    lower.endsWith('.toml') ||
    lower.endsWith('.ini') ||
    lower.endsWith('.env') ||
    lower.endsWith('.editorconfig') ||
    lower === 'package.json' ||
    lower === 'tsconfig.json' ||
    lower === '.gitignore'
  )
}

export function generateLocalChangeAnalysis(
  files: GitStatusFile[],
  sections: GitDiffSection[],
  t: Translate,
): AiChangeAnalysis {
  let score = 0
  const concerns: AiChangeConcern[] = []
  const affectedAreas = new Set<string>()
  const testSuggestions: string[] = []

  const additions = sections.reduce((sum, s) => sum + (s.additions ?? 0), 0)
  const deletions = sections.reduce((sum, s) => sum + (s.deletions ?? 0), 0)
  const totalLines = additions + deletions

  for (const file of files) {
    const status = statusToLabel(file.status ?? file.unstaged_status ?? file.staged_status)
    const parts = file.path.split(/[\\/]/)
    const area = parts.length > 1 ? parts[0] : 'root'
    affectedAreas.add(area)

    if (file.is_conflicted) {
      score += RISK_WEIGHTS.conflict
      concerns.push({
        severity: 'high',
        category: 'Conflict',
        description: t('context.gitRisk.conflict', { path: file.path }),
      })
    }

    if (status === 'D') {
      score += RISK_WEIGHTS.deletion
      concerns.push({
        severity: 'medium',
        category: 'Deletion',
        description: t('context.gitRisk.deletion', { path: file.path }),
      })
    }

    if (isLockfile(file.path)) {
      score += RISK_WEIGHTS.lockfile
      concerns.push({
        severity: 'medium',
        category: 'Dependencies',
        description: t('context.gitRisk.lockfile', { path: file.path }),
      })
      testSuggestions.push(t('context.gitRisk.lockfileTest', { path: file.path }))
    }

    if (isConfigFile(file.path)) {
      score += RISK_WEIGHTS.config
      concerns.push({
        severity: 'low',
        category: 'Configuration',
        description: t('context.gitRisk.config', { path: file.path }),
      })
    }

    if (isSecuritySensitive(file.path)) {
      score += RISK_WEIGHTS.security
      concerns.push({
        severity: 'high',
        category: 'Security',
        description: t('context.gitRisk.security', { path: file.path }),
      })
      testSuggestions.push(t('context.gitRisk.securityTest', { path: file.path }))
    }

    if (isApiSurface(file.path)) {
      score += RISK_WEIGHTS.api
      concerns.push({
        severity: 'medium',
        category: 'API',
        description: t('context.gitRisk.api', { path: file.path }),
      })
      testSuggestions.push(t('context.gitRisk.apiTest'))
    }

    if (status === 'A' && file.path.toLowerCase().includes('test')) {
      testSuggestions.push(t('context.gitRisk.newTest', { path: file.path }))
    }
  }

  if (totalLines > 500) {
    score += RISK_WEIGHTS.largeDiff
    concerns.push({
      severity: totalLines > 1500 ? 'high' : 'medium',
      category: 'Size',
      description: t('context.gitRisk.size', { additions, deletions, files: files.length }),
    })
  }

  // Deduplicate concerns by description
  const seen = new Set<string>()
  const uniqueConcerns = concerns.filter((c) => {
    if (seen.has(c.description)) return false
    seen.add(c.description)
    return true
  })

  const riskLevel: AiRiskLevel =
    score >= 12 ? 'critical' : score >= 8 ? 'high' : score >= 4 ? 'medium' : 'low'

  const riskScore = Math.min(100, Math.max(0, score * 7))

  if (files.length > 0 && testSuggestions.length === 0) {
    testSuggestions.push(t('context.gitRisk.defaultTest'))
  }

  const summary =
    uniqueConcerns.length === 0
      ? t('context.gitRisk.summaryLow', { n: files.length }, files.length)
      : t('context.gitRisk.summary', { n: files.length, concerns: t('context.gitRisk.concernCount', { n: uniqueConcerns.length }, uniqueConcerns.length) }, files.length)

  return {
    summary,
    riskLevel,
    riskScore,
    concerns: uniqueConcerns,
    affectedAreas: [...affectedAreas],
    testSuggestions: [...new Set(testSuggestions)],
    explanation: t('context.gitRisk.localExplanation'),
  }
}

// ---- AI-powered analysis (streaming) ----

/**
 * Composable for AI-powered change risk analysis.
 * Streams an analysis from the session model and falls back to local
 * heuristics when no session is available.
 */
export function useAiChangeAnalysis(selectedSessionId: ComputedRef<string | null>) {
  const { t } = useI18n()
  const analyzing = ref(false)
  const aiError = ref<string | null>(null)
  const analysis = ref<AiChangeAnalysis | null>(null)
  const streamingText = ref('')
  let abortController: AbortController | null = null

  const canAnalyze = computed(() => Boolean(selectedSessionId.value && !analyzing.value))

  async function analyze(
    files: GitStatusFile[],
    sections: GitDiffSection[],
    branch?: string,
  ): Promise<void> {
    if (files.length === 0) {
      aiError.value = t('context.gitAiNoChanges')
      return
    }

    const sessionId = selectedSessionId.value
    const localAnalysis = generateLocalChangeAnalysis(files, sections, t)
    analysis.value = localAnalysis

    if (!sessionId) {
      return
    }

    analyzing.value = true
    aiError.value = null
    streamingText.value = ''

    const fileList = files
      .slice(0, 30)
      .map((f) => `${statusToLabel(f.status ?? f.unstaged_status)} ${f.path}`)
      .join('\n')

    const MAX_DIFF_CHARS = 4000
    const diffParts: string[] = []
    let diffChars = 0
    for (const section of sections) {
      if (!section.diff) continue
      const piece = `--- ${section.title} ---\n${section.diff}`
      if (diffChars + piece.length > MAX_DIFF_CHARS) {
        const remaining = MAX_DIFF_CHARS - diffChars
        if (remaining > 100) {
          diffParts.push(piece.slice(0, remaining))
          diffChars = MAX_DIFF_CHARS
        }
        break
      }
      diffParts.push(piece)
      diffChars += piece.length
    }
    const diffSummary = diffParts.join('\n')

    const prompt = [
      'You are a senior code reviewer. Analyze the following Git changes and produce a concise risk assessment.',
      '',
      `Branch: ${branch ?? 'unknown'}`,
      `Files changed: ${files.length}`,
      '',
      'Changed files:',
      fileList || 'No files',
      '',
      'Diff summary:',
      diffSummary || 'No diff available.',
      '',
      'Respond with ONLY a JSON object in this exact shape (no markdown, no explanation):',
      JSON.stringify({
        summary: 'One-sentence overview of the change',
        riskLevel: 'low|medium|high|critical',
        riskScore: 0,
        concerns: [
          { severity: 'medium', category: 'Category', description: 'Specific concern' },
        ],
        affectedAreas: ['area1', 'area2'],
        testSuggestions: ['suggestion1', 'suggestion2'],
        explanation: 'Why these risks were identified',
      }),
      '',
      'Rules:',
      '- riskScore must be an integer 0-100',
      '- riskLevel must be one of: low, medium, high, critical',
      '- Keep the response strictly valid JSON',
      `- Write summary, descriptions, testSuggestions and explanation in ${t('common.languageName')}`,
    ].join('\n')

    try {
      abortController = api.invokeStream(
        sessionId,
        prompt,
        (chunk: ModelStreamChunkDto) => {
          if (chunk.kind === 'delta' && chunk.delta) {
            streamingText.value += chunk.delta
          } else if (chunk.kind === 'done') {
            const text = streamingText.value.trim()
            if (text) {
              const parsed = parseAiAnalysisResponse(text, t)
              if (parsed) {
                analysis.value = parsed
              }
            }
            analyzing.value = false
          } else if (chunk.kind === 'error') {
            aiError.value = chunk.delta ?? t('context.gitAiAnalysisFailed')
            analyzing.value = false
          }
        },
        (err: Error) => {
          aiError.value = err.message
          analyzing.value = false
        },
      )
    } catch (err) {
      aiError.value = err instanceof Error ? err.message : t('context.gitAiAnalysisFailed')
      analyzing.value = false
    }
  }

  function cancel() {
    abortController?.abort()
    abortController = null
    analyzing.value = false
  }

  function clear() {
    analysis.value = null
    aiError.value = null
    streamingText.value = ''
    abortController?.abort()
    abortController = null
    analyzing.value = false
  }

  return {
    analyzing,
    aiError,
    analysis,
    streamingText,
    canAnalyze,
    analyze,
    cancel,
    clear,
    generateLocalChangeAnalysis,
  }
}

function parseAiAnalysisResponse(text: string, t: Translate): AiChangeAnalysis | null {
  // Try to extract JSON from a markdown code block if the model wrapped it
  const codeBlockMatch = /```(?:json)?\s*([\s\S]*?)```/.exec(text)
  const jsonText = codeBlockMatch ? codeBlockMatch[1] : text

  try {
    const parsed = JSON.parse(jsonText) as Partial<AiChangeAnalysis>
    if (!parsed.summary || typeof parsed.riskScore !== 'number') {
      return null
    }
    const riskLevel = normalizeRiskLevel(parsed.riskLevel)
    const concerns = Array.isArray(parsed.concerns)
      ? parsed.concerns
          .filter(
            (c): c is AiChangeConcern =>
              typeof c === 'object' &&
              c !== null &&
              typeof c.description === 'string' &&
              typeof c.category === 'string',
          )
          .map((c) => ({ ...c, severity: normalizeRiskLevel(c.severity) }))
      : []
    return {
      summary: String(parsed.summary),
      riskLevel,
      riskScore: Math.min(100, Math.max(0, Math.round(parsed.riskScore))),
      concerns,
      affectedAreas: Array.isArray(parsed.affectedAreas) ? parsed.affectedAreas.map(String) : [],
      testSuggestions: Array.isArray(parsed.testSuggestions) ? parsed.testSuggestions.map(String) : [],
      explanation: String(parsed.explanation ?? t('context.gitRisk.aiExplanation')),
    }
  } catch {
    return null
  }
}

function normalizeRiskLevel(level: unknown): AiRiskLevel {
  const str = String(level).toLowerCase()
  if (str === 'critical') return 'critical'
  if (str === 'high') return 'high'
  if (str === 'medium') return 'medium'
  return 'low'
}
