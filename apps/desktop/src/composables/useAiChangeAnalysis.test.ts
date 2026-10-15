// @vitest-environment happy-dom
import { describe, it, expect } from 'vitest'
import { createI18n } from 'vue-i18n'
import en from '../locales/en'
import { generateLocalChangeAnalysis } from './useAiChangeAnalysis'
import type { GitStatusFile, GitDiffSection } from './useGitOperation'

// The heuristics word their findings in the app's language; these cases read the English ones.
const { t } = createI18n({ legacy: false, locale: 'en', messages: { en } }).global

describe('generateLocalChangeAnalysis', () => {
  it('returns low risk for an empty change set', () => {
    const result = generateLocalChangeAnalysis([], [], t)
    expect(result.riskLevel).toBe('low')
    expect(result.riskScore).toBe(0)
    expect(result.concerns).toHaveLength(0)
  })

  it('flags deletions', () => {
    const files: GitStatusFile[] = [{ path: 'src/legacy.ts', status: 'D' }]
    const result = generateLocalChangeAnalysis(files, [], t)
    expect(result.concerns.some((c) => c.category === 'Deletion')).toBe(true)
    expect(result.riskLevel).toBe('medium')
    expect(result.riskScore).toBeGreaterThan(0)
  })

  it('flags security-sensitive files', () => {
    const files: GitStatusFile[] = [{ path: 'src/auth/token.ts', status: 'M' }]
    const result = generateLocalChangeAnalysis(files, [], t)
    expect(result.concerns.some((c) => c.category === 'Security')).toBe(true)
    expect(result.testSuggestions.some((s) => s.includes('security'))).toBe(true)
  })

  it('flags lockfile changes', () => {
    const files: GitStatusFile[] = [{ path: 'package-lock.json', status: 'M' }]
    const result = generateLocalChangeAnalysis(files, [], t)
    expect(result.concerns.some((c) => c.category === 'Dependencies')).toBe(true)
    expect(result.testSuggestions.length).toBeGreaterThan(0)
  })

  it('flags conflicts as high severity', () => {
    const files: GitStatusFile[] = [{ path: 'src/conflict.ts', status: 'UU', is_conflicted: true }]
    const result = generateLocalChangeAnalysis(files, [], t)
    expect(result.concerns.some((c) => c.category === 'Conflict' && c.severity === 'high')).toBe(true)
  })

  it('adds size risk for large diffs', () => {
    const files: GitStatusFile[] = [{ path: 'src/big.ts', status: 'M' }]
    const sections: GitDiffSection[] = [
      {
        id: 'working-tree',
        kind: 'working_tree',
        title: 'Working tree',
        diff: '',
        files: [],
        file_count: 1,
        additions: 600,
        deletions: 10,
        notices: [],
      },
    ]
    const result = generateLocalChangeAnalysis(files, sections, t)
    expect(result.concerns.some((c) => c.category === 'Size')).toBe(true)
  })

  it('accumulates risk for multiple deletions', () => {
    const files: GitStatusFile[] = [
      { path: 'src/a.ts', status: 'D' },
      { path: 'src/b.ts', status: 'D' },
    ]
    const result = generateLocalChangeAnalysis(files, [], t)
    expect(result.concerns.filter((c) => c.category === 'Deletion')).toHaveLength(2)
    expect(result.riskLevel).toBe('high')
  })
})
