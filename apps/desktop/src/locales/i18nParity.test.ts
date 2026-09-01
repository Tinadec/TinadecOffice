import { describe, expect, it } from 'vitest'
import zhCN from './zh-CN'
import en from './en'

// ── helpers ──────────────────────────────────────────────────────────
type Bundle = Record<string, unknown>

function flattenLeaves(bundle: Bundle, prefix = ''): Set<string> {
  const out = new Set<string>()
  for (const [key, value] of Object.entries(bundle)) {
    const path = prefix ? `${prefix}.${key}` : key
    if (value && typeof value === 'object') {
      for (const leaf of flattenLeaves(value as Bundle, path)) out.add(leaf)
    } else {
      out.add(path)
    }
  }
  return out
}

function resolveKey(bundle: Bundle, key: string): unknown {
  let node: unknown = bundle
  for (const part of key.split('.')) {
    if (!node || typeof node !== 'object') return undefined
    node = (node as Bundle)[part]
  }
  return node
}

// ── sources whose t() references must exist in BOTH bundles ─────────
// Panel sources this contract guards. A referenced key missing from both bundles
// renders nothing at all, so every user-facing panel belongs here.
const panelSources = import.meta.glob(
  [
    './../settings/sections/AgentModesPanel.vue',
    './../settings/sections/PromptEngineeringMerged.vue',
    './../settings/sections/RuntimeInstancesPanel.vue',
    './../components/agentCenter/GovernanceRolesPanel.vue',
    './../components/AgentEvolutionPanel.vue',
    './../components/TerminalPanel.vue',
  ],
  { query: '?raw', import: 'default', eager: true },
) as Record<string, string>

const CJK = /[\u4e00-\u9fff]/

describe('i18n locale parity and reference integrity', () => {
  it('zh-CN and en expose the same leaf keys in every namespace', () => {
    const zhLeaves = flattenLeaves(zhCN as Bundle)
    const enLeaves = flattenLeaves(en as Bundle)
    const onlyZh = [...zhLeaves].filter((k) => !enLeaves.has(k))
    const onlyEn = [...enLeaves].filter((k) => !zhLeaves.has(k))
    expect(onlyZh, `keys missing from en.ts: ${onlyZh.join(', ')}`).toEqual([])
    expect(onlyEn, `keys missing from zh-CN.ts: ${onlyEn.join(', ')}`).toEqual([])
  })

  it('every t() key referenced by agent-center panels exists in both locales', () => {
    for (const [file, source] of Object.entries(panelSources)) {
      const keys = new Set<string>()
      for (const match of source.matchAll(/\bt\(\s*'([a-zA-Z0-9_.]+)'/g)) {
        keys.add(match[1])
      }
      expect(keys.size, `${file} should reference at least one translation`).toBeGreaterThan(0)
      for (const key of keys) {
        expect(resolveKey(zhCN as Bundle, key), `${file} → ${key} missing from zh-CN.ts`).toBeDefined()
        expect(resolveKey(en as Bundle, key), `${file} → ${key} missing from en.ts`).toBeDefined()
      }
    }
  })

  it('panel templates carry no raw CJK strings outside comments', () => {
    for (const [file, source] of Object.entries(panelSources)) {
      if (!file.endsWith('.vue')) continue
      // strip HTML comments and script blocks — only template text nodes matter
      const withoutComments = source.replace(/<!--[\s\S]*?-->/g, '')
      const templateStart = withoutComments.indexOf('<template>')
      if (templateStart === -1) continue
      const template = withoutComments.slice(templateStart).replace(/^<script[\s\S]*?<\/script>/gm, '')
      const rawCjk = template.match(new RegExp(`[^'"\\\`>]*${CJK.source}`))
      expect(rawCjk, `${file} has untranslated CJK text in template: ${rawCjk?.[0]?.trim()}`).toBeNull()
    }
  })
})
