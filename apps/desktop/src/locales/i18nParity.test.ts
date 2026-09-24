import { describe, expect, it } from 'vitest'
import { readdirSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
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
    './../settings/sections/AgentPacksPanel.vue',
    './../components/agentCenter/GovernanceRolesPanel.vue',
    './../components/AgentEvolutionPanel.vue',
    './../components/AgentActivityPanel.vue',
    './../components/TaskGraphPanel.vue',
    './../components/TerminalPanel.vue',
    // The chat surface ships through TinadecUI's ChatCard, not a page, so nothing
    // else proved its keys exist: chat.copy/chat.edit rendered as raw keys for the
    // whole time the pencil was wired to a stub. ChatPanel itself is not listed —
    // it labels nothing and translates no key.
    './../components/MessageItem.vue',
    './../components/MessageList.vue',
    './../components/ComposerBar.vue',
    // The palette's rows are all labels from the command table, and its chrome is
    // translated too, so an unlisted key here would show a raw dotted string in a dialog.
    './../components/CommandPalette.vue',
    // The collapsed thinking row is translated by nothing but itself: its title was a hardcoded
    // Chinese sentence, which is invisible to a reader of the English app.
    './../components/chat/ThinkingProcess.vue',
    // The supervision decision row labels Core's continue/correct/cancel vocabulary;
    // those three buttons were hardcoded Chinese and read as Chinese in the English app.
    './../components/chat/TurnTimeline.vue',
    // Market labels are resolved by the controller (one owner for both the filter rail and the
    // row badges), so a dead key there would render in two cards at once.
    './../controllers/MarketController.ts',
  ],
  { query: '?raw', import: 'default', eager: true },
) as Record<string, string>

/**
 * The TinadecUI cards, read straight off disk.
 *
 * `import.meta.glob` above is rooted in this app and does not resolve aliases, and `@tinadec/ui`
 * is an alias to `../TinadecUI/src/index.ts` — so the shipping market face (three of these five
 * cards) was unreachable to this gate no matter what was listed. Precedent for `node:fs` in this
 * suite: RowContextMenu.test.ts, toolPresentation.test.ts.
 */
function cardSources(): Record<string, string> {
  const root = path.resolve(fileURLToPath(import.meta.url), '../../../../TinadecUI/src/components/cards')
  const out: Record<string, string> = {}
  for (const group of readdirSync(root, { withFileTypes: true })) {
    if (!group.isDirectory()) continue
    for (const file of readdirSync(path.join(root, group.name))) {
      if (!file.endsWith('.vue')) continue
      const rel = `TinadecUI/cards/${group.name}/${file}`
      out[rel] = readFileSync(path.join(root, group.name, file), 'utf8')
    }
  }
  return out
}

function referencedKeys(source: string): Set<string> {
  const keys = new Set<string>()
  for (const match of source.matchAll(/\bt\(\s*'([a-zA-Z0-9_.]+)'/g)) {
    keys.add(match[1])
  }
  return keys
}

function assertKeysExist(file: string, keys: Set<string>): void {
  for (const key of keys) {
    expect(resolveKey(zhCN as Bundle, key), `${file} → ${key} missing from zh-CN.ts`).toBeDefined()
    expect(resolveKey(en as Bundle, key), `${file} → ${key} missing from en.ts`).toBeDefined()
  }
}

/** Template text only: comments and the script block are allowed to say anything. */
function templateText(source: string): string | null {
  const withoutComments = source.replace(/<!--[\s\S]*?-->/g, '')
  // `<template>` and `<template vapor>` — the cards all carry the vapor compiler flag, and matching
  // only the bare tag made this rule skip every file that used it.
  const open = /<template[^>]*>/.exec(withoutComments)
  if (!open) return null
  const close = withoutComments.lastIndexOf('</template>')
  if (close === -1) return null
  // Cutting at the root close tag also drops the `<style>` block below it, which is where a
  // Chinese code comment is most likely to live in a card.
  return withoutComments.slice(open.index + open[0].length, close)
}

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
      const keys = referencedKeys(source)
      expect(keys.size, `${file} should reference at least one translation`).toBeGreaterThan(0)
      assertKeysExist(file, keys)
    }
  })

  it('every t() key referenced by a TinadecUI card exists in both locales', () => {
    const cards = cardSources()
    // A mis-anchored walk would hand the loop nothing to check and report green.
    expect(Object.keys(cards).length).toBeGreaterThan(0)
    for (const [file, source] of Object.entries(cards)) {
      // A card that labels nothing is not a failure — several are thin wrappers around a panel that
      // does its own translating, and those panels are listed above.
      assertKeysExist(file, referencedKeys(source))
    }
  })

  it('panel templates carry no raw CJK strings outside comments', () => {
    for (const [file, source] of Object.entries({ ...panelSources, ...cardSources() })) {
      if (!file.endsWith('.vue')) continue
      const template = templateText(source)
      if (template === null) continue
      const rawCjk = template.match(new RegExp(`[^'"\\\`>]*${CJK.source}`))
      expect(rawCjk, `${file} has untranslated CJK text in template: ${rawCjk?.[0]?.trim()}`).toBeNull()
    }
  })
})
