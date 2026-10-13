import { describe, expect, it } from 'vitest'
import { FileText, Search, Terminal } from '@lucide/vue'
import en from '@/locales/en'
import zhCN from '@/locales/zh-CN'
import { displayPath, toolSentence, verbKey, type ToolVerb } from './toolSentence'

describe('toolSentence', () => {
  it('reads a file as a quiet step with a workspace-relative subject', () => {
    const sentence = toolSentence({ tool_id: 'read_file', resource_path: 'C:\\repo\\src\\a.ts', arguments_summary: 'read_file → C:\\repo\\src\\a.ts' }, 'C:\\repo')
    expect(sentence).toMatchObject({ kind: 'read', verb: 'read', quiet: 'read', subject: 'src/a.ts', location: null })
    expect(sentence.icon).toBe(FileText)
  })

  it('names a search by its term and where it looked', () => {
    const sentence = toolSentence({ tool_id: 'file_search', query: 'useResults', resource_path: '/w/src' }, '/w')
    expect(sentence).toMatchObject({ verb: 'search', quiet: 'search', subject: 'useResults', location: 'src' })
    expect(sentence.icon).toBe(Search)
    expect(toolSentence({ tool_id: 'file_search', resource_path: '/w/src' }, '/w')).toMatchObject({ subject: 'src', location: null })
  })

  it('runs a command as a loud step named by its command line', () => {
    const sentence = toolSentence({ tool_id: 'shell', command: 'npm test', arguments_summary: 'shell → `npm test`' })
    expect(sentence).toMatchObject({ verb: 'run', quiet: null, subject: 'npm test' })
    expect(sentence.icon).toBe(Terminal)
    expect(toolSentence({ tool_id: 'command_run', arguments_summary: 'command_run → `dotnet test`' }).subject).toBe('`dotnet test`')
  })

  it('treats edits, git, mcp and web fetch as loud', () => {
    expect(toolSentence({ tool_id: 'replace_lines', resource_path: '/w/a.ts' }, '/w')).toMatchObject({ verb: 'edit', quiet: null, subject: 'a.ts' })
    expect(toolSentence({ tool_id: 'git_commit', arguments_summary: 'git_commit → message=x' })).toMatchObject({ verb: 'git', subject: 'git commit' })
    expect(toolSentence({ tool_id: 'git_worktree_create' }).subject).toBe('git worktree create')
    expect(toolSentence({ tool_id: 'mcp_invoke', arguments_summary: 'mcp_invoke → github' })).toMatchObject({ verb: 'mcp', subject: 'github' })
    expect(toolSentence({ tool_id: 'web_fetch', resource_path: 'https://example.com/a' })).toMatchObject({ verb: 'fetch', quiet: null, subject: 'https://example.com/a' })
  })

  it('falls back to the tool id when nothing better is known', () => {
    expect(toolSentence({ tool_id: 'sandbox_status' })).toMatchObject({ verb: 'other', subject: 'sandbox_status' })
    expect(toolSentence({ tool_id: 'brand_new_tool', arguments_summary: 'brand_new_tool → x=1' })).toMatchObject({ verb: 'other', subject: 'x=1' })
  })

  it('lists a directory as quiet', () => {
    expect(toolSentence({ tool_id: 'ls', resource_path: '/w' }, '/w')).toMatchObject({ verb: 'list', quiet: 'list', subject: '.' })
  })
})

describe('displayPath', () => {
  it('relativizes inside the root, case-insensitively on drive paths, and keeps outside paths whole', () => {
    expect(displayPath('C:\\Repo\\src\\a.ts', 'c:\\repo\\')).toBe('src/a.ts')
    expect(displayPath('/w/src', '/w')).toBe('src')
    expect(displayPath('/wx/src', '/w')).toBe('/wx/src')
    expect(displayPath('/W/src', '/w')).toBe('/W/src')
    expect(displayPath('src/a.ts', null)).toBe('src/a.ts')
  })
})

describe('verb keys', () => {
  const verbs: ToolVerb[] = ['read', 'list', 'search', 'edit', 'run', 'git', 'mcp', 'fetch', 'workspace', 'other']

  it('exist for every verb and phase in both locales', () => {
    const lookup = (bundle: Record<string, unknown>, key: string) =>
      key.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], bundle)
    for (const verb of verbs) {
      for (const phase of ['active', 'done', 'failed'] as const) {
        const key = verbKey(verb, phase)
        expect(typeof lookup(en as Record<string, unknown>, key), `en ${key}`).toBe('string')
        expect(typeof lookup(zhCN as Record<string, unknown>, key), `zh ${key}`).toBe('string')
      }
    }
  })
})
