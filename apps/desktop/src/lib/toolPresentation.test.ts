import { readFileSync, readdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { Wrench } from '@lucide/vue'
import {
  CORE_VIRTUAL_TOOL_IDS,
  GIT_FACADE_TOOL_ID,
  PROVIDER_TOOL_IDS,
  RISK_LEVELS,
  isKnownToolId,
  riskLevelsPresent,
  riskToneClass,
  toolIconOf,
  toolKindOf
} from './toolPresentation'

/**
 * These ids came from a live `#manifest` session against the built provider, not from a
 * design document — the last time this layer's id list was written from imagination it
 * shipped four components that branched on tools which do not exist. If the provider
 * gains or renames a tool, update PROVIDER_TOOL_IDS from a fresh probe, do not extend it
 * by guesswork.
 */
const MEASURED_MANIFEST = {
  protocol_version: 2,
  manifest_hash: 'd578f134e326f0f4d229e60a7409fd9c73e73d168650bcafe8e8d20dd0ecba06',
  tool_count: 50
} as const

/** Ids the old display layer branched on and which no layer has ever advertised. */
const INVENTED_TOOL_IDS = [
  'list_directory',
  'glob_search',
  'grep_content',
  'apply_patch',
  'code_editor',
  'sandbox_exec'
] as const

describe('provider tool id allowlist', () => {
  it('holds exactly the tools the measured manifest advertises', () => {
    expect(PROVIDER_TOOL_IDS.length).toBe(MEASURED_MANIFEST.tool_count)
  })

  it('carries no duplicates and stays alphabetically ordered for diffing', () => {
    const ids = [...PROVIDER_TOOL_IDS]
    expect(new Set(ids).size).toBe(ids.length)
    expect(ids).toEqual([...ids].sort((a, b) => a.localeCompare(b)))
  })

  it('has a decided kind for every tool it claims to know', () => {
    const undecided = PROVIDER_TOOL_IDS.filter((id) => toolKindOf(id) === 'other')
    expect(undecided).toEqual([])
  })

  it('has a decided kind for every Core virtual tool', () => {
    const undecided = CORE_VIRTUAL_TOOL_IDS.filter((id) => toolKindOf(id) === 'other')
    expect(undecided).toEqual([])
  })
})

describe('Core virtual tool mirror', () => {
  // `CoreVirtualToolPolicy` is the only holder of "Core executes this tool itself", and the
  // display layer copies it by hand. A Core virtual tool that is not copied here arrives with
  // no kind, no icon and no result view - the silent half of the work this file exists to catch.
  const policySource = readFileSync(
    fileURLToPath(
      new URL('../../../../TinadecCore/Abstractions/Ports/CoreVirtualToolPolicy.cs', import.meta.url),
    ),
    'utf8',
  )

  function mintedById(): string[] {
    const singles = [...policySource.matchAll(/public const string \w+ = "([a-z_]+)";/g)].map((m) => m[1])
    const roster = policySource.match(/TinaChatToolIds\s*=\s*\[([^\]]*)\]/)
    const chat = roster ? [...roster[1].matchAll(/"([a-z_0-9]+)"/g)].map((m) => m[1]) : []
    return [...singles, ...chat]
  }

  it('reads the real policy file, so a moved file cannot blank the check', () => {
    expect(policySource).toContain('IsCoreVirtual')
    expect(mintedById().length).toBeGreaterThan(10)
  })

  it('mirrors exactly the ids Core declares virtual', () => {
    expect([...CORE_VIRTUAL_TOOL_IDS].sort()).toEqual(mintedById().sort())
  })
})

describe('toolKindOf', () => {
  it('classifies the file surface by what it actually does', () => {
    expect(toolKindOf('read_file')).toBe('read')
    expect(toolKindOf('stat')).toBe('read')
    expect(toolKindOf('ls')).toBe('list')
    expect(toolKindOf('file_search')).toBe('search')
    expect(toolKindOf('write_file')).toBe('write')
    expect(toolKindOf('replace_lines')).toBe('write')
    expect(toolKindOf('delete_bytes')).toBe('write')
  })

  it('routes both command tools to shell and both sandbox tools to sandbox', () => {
    expect(toolKindOf('shell')).toBe('shell')
    expect(toolKindOf('command_run')).toBe('shell')
    expect(toolKindOf('sandbox_status')).toBe('sandbox')
    expect(toolKindOf('sandbox_reset')).toBe('sandbox')
  })

  it('treats the whole git family and the Core facade as git', () => {
    expect(toolKindOf('git_commit')).toBe('git')
    expect(toolKindOf('git_worktree_list')).toBe('git')
    expect(toolKindOf(GIT_FACADE_TOOL_ID)).toBe('git')
  })

  it('separates mcp, orchestration and chat surfaces', () => {
    expect(toolKindOf('mcp_search')).toBe('mcp')
    expect(toolKindOf('mcp_invoke')).toBe('mcp')
    expect(toolKindOf('create_workspace')).toBe('orchestration')
    expect(toolKindOf('task_dispatch')).toBe('orchestration')
    expect(toolKindOf('tina_chat_send')).toBe('chat')
    expect(toolKindOf('tina_chat_read_inbox')).toBe('chat')
  })

  it('falls back without inventing a capability', () => {
    expect(toolKindOf('some_future_tool')).toBe('other')
    expect(toolKindOf('')).toBe('other')
    expect(toolKindOf(undefined)).toBe('other')
  })
})

describe('toolIconOf', () => {
  it('gives the invented ids the fallback icon instead of a confident wrong one', () => {
    for (const id of INVENTED_TOOL_IDS) {
      expect(toolIconOf(id)).toBe(Wrench)
    }
  })

  it('gives every real tool a distinct-family icon', () => {
    expect(toolIconOf('read_file')).not.toBe(Wrench)
    expect(toolIconOf(GIT_FACADE_TOOL_ID)).not.toBe(Wrench)
    expect(toolIconOf('tina_chat_send')).not.toBe(Wrench)
  })
})

describe('isKnownToolId', () => {
  it('accepts all three id sources', () => {
    expect(isKnownToolId('file_search')).toBe(true)
    expect(isKnownToolId(GIT_FACADE_TOOL_ID)).toBe(true)
    expect(isKnownToolId('task_dispatch')).toBe(true)
  })

  it('rejects the ids this layer used to branch on', () => {
    for (const id of INVENTED_TOOL_IDS) {
      expect(isKnownToolId(id)).toBe(false)
    }
  })
})

describe('riskToneClass', () => {
  /**
   * The defect this replaces: the old chain matched 'read'/'shell'/'git'/'url'/'write'
   * inside the risk string. The provider only ever emits low/high, so both real values
   * landed on the default colour and the badge looked unstyled for every tool.
   */
  it('tones the levels Core and the provider actually emit', () => {
    expect(riskToneClass('low')).toBe('risk-low')
    expect(riskToneClass('high')).toBe('risk-high')
    expect(riskToneClass('HIGH')).toBe('risk-high')
    expect(riskToneClass(' elevated ')).toBe('risk-elevated')
    expect(riskToneClass('medium')).toBe('risk-medium')
  })

  it('does not read a capability out of an unrelated word', () => {
    expect(riskToneClass('read-only')).toBe('risk-default')
    expect(riskToneClass('')).toBe('risk-default')
    expect(riskToneClass(undefined)).toBe('risk-default')
  })
})

describe('riskLevelsPresent', () => {
  it('offers only the levels the rows on screen carry, canonical order first', () => {
    expect(riskLevelsPresent(['high', 'low', 'high', ''])).toEqual(['low', 'high'])
    expect(riskLevelsPresent(['low'])).toEqual(['low'])
  })

  it('keeps an unexpected provider value filterable instead of hiding it', () => {
    expect(riskLevelsPresent(['high', 'critical'])).toEqual(['high', 'critical'])
  })

  it('agrees with the declared vocabulary', () => {
    expect(riskLevelsPresent(RISK_LEVELS)).toEqual([...RISK_LEVELS])
  })
})

/**
 * The regression guard for the whole exercise. A component that re-adds one of these
 * literals is reintroducing a branch that can never fire, which no type or runtime check
 * would catch: `tool_id` is a plain string and the payload is typed
 * `Record<string, unknown>`.
 *
 * Only quoted ids count, so this module may keep explaining the history in prose.
 */
describe('invented tool ids are gone from the source tree', () => {
  const SRC = fileURLToPath(new URL('..', import.meta.url))
  const SELF = 'lib/toolPresentation.test.ts'

  function sources(dir: string): string[] {
    const out: string[] = []
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = `${dir}/${entry.name}`
      if (entry.isDirectory()) {
        out.push(...sources(full))
      } else if (/\.(ts|vue)$/.test(entry.name)) {
        out.push(full)
      }
    }
    return out
  }

  it('appears nowhere as a quoted tool id', () => {
    const offenders: string[] = []
    for (const file of sources(SRC)) {
      const rel = file.slice(SRC.length + 1).replace(/\\/g, '/')
      if (rel === SELF) continue
      const text = readFileSync(file, 'utf8')
      text.split('\n').forEach((line, idx) => {
        for (const id of INVENTED_TOOL_IDS) {
          if (new RegExp(`['"\`]${id}['"\`]`).test(line)) {
            offenders.push(`${rel}:${idx + 1} ${id}`)
          }
        }
      })
    }
    expect(offenders).toEqual([])
  })
})
