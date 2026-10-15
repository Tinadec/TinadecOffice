import { ref, computed, type ComputedRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type ModelStreamChunkDto } from '../api'
import type { GitStatusFile, GitDiffSection } from './useGitOperation'
import { statusToLabel } from './useGitOperation'

// ---- Types ----

export interface AiCommitSuggestion {
  type: string
  scope: string
  subject: string
  body: string
  fullMessage: string
  confidence: 'high' | 'medium' | 'low'
}

export interface AiSummaryStats {
  fileCount: number
  additions: number
  deletions: number
  changeTypes: Record<string, number>
  topScopes: string[]
}

// ---- Local analysis (no AI call needed) ----

/**
 * Infer a conventional-commit scope from a file path.
 * e.g. "apps/desktop/src/components/Foo.vue" -> "desktop"
 *      "src/TinadecCore/Services/Bar.cs" -> "core"
 *      "docs/baz.md" -> "docs"
 */
export function inferScope(filePath: string): string {
  const parts = filePath.split(/[\\/]/)
  if (parts.length === 0) return ''
  // Check common patterns
  if (parts[0] === 'apps' && parts.length > 1) return parts[1]
  if (parts[0] === 'src' && parts.length > 1) {
    if (parts[1] === 'TinadecCore') return 'core'
    if (parts[1] === 'TinadecModel') return 'model'
    return parts[1].toLowerCase()
  }
  if (parts[0] === 'docs') return 'docs'
  if (parts[0] === 'tests') return 'tests'
  if (parts[0] === 'native' && parts.length > 1) return 'native'
  // Fallback: use the first directory
  return parts[0]
}

/**
 * Infer a commit type based on the set of changed files.
 * - If all/most files are test files -> "test"
 * - If all/most files are .md files -> "docs"
 * - If all/most files are config/build files -> "chore"
 * - If there are new files (additions) and no deletions -> "feat"
 * - If there are modifications only -> "fix" or "refactor"
 * - If there are deletions -> "refactor"
 */
export function inferCommitType(files: GitStatusFile[]): string {
  if (files.length === 0) return 'feat'
  const testCount = files.filter((f) => isTestFile(f.path)).length
  const docCount = files.filter((f) => isDocFile(f.path)).length
  const configCount = files.filter((f) => isConfigFile(f.path)).length
  const newFiles = files.filter((f) => f.status === 'A' || f.is_untracked).length
  const deletedFiles = files.filter((f) => f.status === 'D').length

  if (testCount / files.length > 0.6) return 'test'
  if (docCount / files.length > 0.6) return 'docs'
  if (configCount / files.length > 0.6) return 'chore'
  if (newFiles > 0 && deletedFiles === 0) return 'feat'
  if (deletedFiles > 0 && newFiles === 0) return 'refactor'
  if (newFiles === 0 && deletedFiles === 0) return 'fix'
  return 'feat'
}

function isTestFile(path: string): boolean {
  const lower = path.toLowerCase()
  return (
    lower.includes('.test.') ||
    lower.includes('.spec.') ||
    lower.includes('/tests/') ||
    lower.includes('/test/') ||
    lower.includes('__tests__') ||
    lower.endsWith('.test.ts') ||
    lower.endsWith('.test.tsx') ||
    lower.endsWith('.test.js')
  )
}

function isDocFile(path: string): boolean {
  const lower = path.toLowerCase()
  return (
    lower.endsWith('.md') ||
    lower.endsWith('.markdown') ||
    lower.endsWith('.txt') ||
    lower.startsWith('docs/') ||
    lower.startsWith('doc/')
  )
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
    lower === '.gitignore' ||
    lower.startsWith('.github/') ||
    lower.startsWith('.vscode/')
  )
}

/**
 * Build a concise summary of the changes for AI prompt or local analysis.
 */
export function buildChangeSummary(
  files: GitStatusFile[],
  sections: GitDiffSection[],
): AiSummaryStats {
  const changeTypes: Record<string, number> = {}
  let additions = 0
  let deletions = 0
  const scopeCount: Record<string, number> = {}

  for (const file of files) {
    const label = statusToLabel(file.status ?? file.unstaged_status ?? file.staged_status)
    changeTypes[label] = (changeTypes[label] ?? 0) + 1
    const scope = inferScope(file.path)
    if (scope) {
      scopeCount[scope] = (scopeCount[scope] ?? 0) + 1
    }
  }

  for (const section of sections) {
    additions += section.additions ?? 0
    deletions += section.deletions ?? 0
    for (const file of section.files) {
      changeTypes[file.change_type] = (changeTypes[file.change_type] ?? 0) + 1
    }
  }

  const topScopes = Object.entries(scopeCount)
    .sort((a, b) => b[1] - a[1])
    .slice(0, 3)
    .map(([scope]) => scope)

  return {
    fileCount: files.length,
    additions,
    deletions,
    changeTypes,
    topScopes,
  }
}

/**
 * Generate a local (non-AI) commit message suggestion based on file analysis.
 */
export function generateLocalSuggestion(
  files: GitStatusFile[],
  sections: GitDiffSection[],
): AiCommitSuggestion {
  const type = inferCommitType(files)
  const summary = buildChangeSummary(files, sections)
  const scope = summary.topScopes[0] ?? ''

  // Build subject from file names
  const fileNames = files.map((f) => {
    const parts = f.path.split(/[\\/]/)
    return parts[parts.length - 1] ?? f.path
  })
  const uniqueNames = [...new Set(fileNames)]

  let subject: string
  if (uniqueNames.length === 1) {
    subject = `update ${uniqueNames[0]}`
  } else if (uniqueNames.length <= 3) {
    subject = `update ${uniqueNames.join(', ')}`
  } else {
    subject = `update ${uniqueNames.length} files in ${scope || 'project'}`
  }

  const bodyParts: string[] = []
  const typeLabels = Object.entries(summary.changeTypes)
    .sort((a, b) => b[1] - a[1])
    .map(([type, count]) => `${count} ${type}`)

  if (typeLabels.length > 0) {
    bodyParts.push(`Changes: ${typeLabels.join(', ')}`)
  }
  if (summary.additions > 0 || summary.deletions > 0) {
    bodyParts.push(`+${summary.additions} -${summary.deletions} lines`)
  }
  if (summary.topScopes.length > 1) {
    bodyParts.push(`Affected areas: ${summary.topScopes.join(', ')}`)
  }

  const scopePart = scope ? `(${scope})` : ''
  const fullMessage = [`${type}${scopePart}: ${subject}`, bodyParts.length > 0 ? '' : '', ...bodyParts]
    .filter((line, idx) => idx > 0 || line !== '')
    .join('\n')

  return {
    type,
    scope,
    subject,
    body: bodyParts.join('\n'),
    fullMessage,
    confidence: summary.fileCount > 0 ? 'medium' : 'low',
  }
}

// ---- AI-powered generation (streaming) ----

/**
 * Composable for AI-powered commit message generation.
 * Uses the session's invoke-stream endpoint to ask the AI to generate
 * a conventional commit message based on the diff summary.
 */
export function useAiCommitMessage(
  selectedSessionId: ComputedRef<string | null>,
) {
  const { t } = useI18n()
  const generating = ref(false)
  const aiError = ref<string | null>(null)
  const aiSuggestion = ref<AiCommitSuggestion | null>(null)
  const streamingText = ref('')
  let abortController: AbortController | null = null

  const canGenerate = computed(() => Boolean(selectedSessionId.value && !generating.value))

  /**
   * Generate a commit message using AI.
   * Falls back to local analysis if no session is available.
   */
  async function generate(
    files: GitStatusFile[],
    sections: GitDiffSection[],
    branch?: string,
  ): Promise<void> {
    if (files.length === 0) {
      aiError.value = t('context.gitAiNoChanges')
      return
    }

    const sessionId = selectedSessionId.value
    const localSuggestion = generateLocalSuggestion(files, sections)
    aiSuggestion.value = localSuggestion

    if (!sessionId) {
      // No session available, use local suggestion only
      return
    }

    generating.value = true
    aiError.value = null
    streamingText.value = ''

    const summary = buildChangeSummary(files, sections)
    const fileList = files
      .slice(0, 30)
      .map((f) => `${statusToLabel(f.status ?? f.unstaged_status)} ${f.path}`)
      .join('\n')

    // Build a concise diff summary for AI context (capped to avoid huge prompts)
    const MAX_DIFF_CHARS = 6000
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
      'You are a commit message generator. Analyze the following Git changes and generate a conventional commit message.',
      '',
      `Branch: ${branch ?? 'unknown'}`,
      `Files changed: ${summary.fileCount} (+${summary.additions} -${summary.deletions})`,
      '',
      'Changed files:',
      fileList,
      '',
      'Diff summary:',
      diffSummary || 'No diff available.',
      '',
      'Respond with ONLY a conventional commit message in this format:',
      'type(scope): subject',
      '',
      'Optional body explaining what and why (not how).',
      '',
      'Rules:',
      '- Use one of: feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert',
      '- Subject line: imperative mood, lowercase, no period, max 50 chars',
      '- Body: wrap at 72 chars, explain the why not the how',
      '- Do not include any explanation, only the commit message',
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
              const parsed = parseAiResponse(text)
              aiSuggestion.value = {
                ...parsed,
                confidence: 'high',
              }
            }
            generating.value = false
          } else if (chunk.kind === 'error') {
            aiError.value = chunk.delta ?? t('context.gitAiGenerateFailed')
            generating.value = false
          }
        },
        (err: Error) => {
          aiError.value = err.message
          generating.value = false
        },
      )
    } catch (err) {
      aiError.value = err instanceof Error ? err.message : t('context.gitAiGenerateFailed')
      generating.value = false
    }
  }

  function cancel() {
    abortController?.abort()
    abortController = null
    generating.value = false
  }

  function clear() {
    aiSuggestion.value = null
    aiError.value = null
    streamingText.value = ''
    abortController?.abort()
    abortController = null
    generating.value = false
  }

  return {
    generating,
    aiError,
    aiSuggestion,
    streamingText,
    canGenerate,
    generate,
    cancel,
    clear,
    // Expose local analysis for synchronous use
    generateLocalSuggestion,
    buildChangeSummary,
    inferScope,
    inferCommitType,
  }
}

/**
 * Parse an AI-generated commit message response into structured parts.
 */
function parseAiResponse(text: string): AiCommitSuggestion {
  const lines = text.split(/\r?\n/)
  const header = lines[0] ?? ''
  const match = /^(\w+)(?:\(([^)]*)\))?!?\s*:\s*(.*)$/.exec(header)

  if (match) {
    const type = match[1] ?? 'feat'
    const scope = match[2] ?? ''
    const subject = match[3] ?? ''
    const body = lines.slice(1).join('\n').replace(/^\n+/, '').trim()
    return {
      type,
      scope,
      subject,
      body,
      fullMessage: text.trim(),
      confidence: 'high',
    }
  }

  return {
    type: 'feat',
    scope: '',
    subject: header,
    body: lines.slice(1).join('\n').trim(),
    fullMessage: text.trim(),
    confidence: 'medium',
  }
}
