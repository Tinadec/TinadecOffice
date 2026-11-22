import type {
  ExternalCliDiscoveredItemDto,
  ImportMcpJsonInput,
  ImportMcpJsonResultDto,
  InstallToolSkillInput,
  InstallToolSkillResultDto,
} from '@/api'
import type { ToolMcpResource, ToolSkillResource } from '@/settings/toolSettings'

export const SECRET_MASK = '********'

export interface SupplyChainMetadata {
  commit?: string | null
  content_hash?: string | null
  package_hash?: string | null
  has_hidden_unicode?: boolean | null
  security_warnings?: string[] | null
}

export interface SecretPlaceholderCarrier {
  secret_placeholders?: string[] | null
}

export interface ImportedAgentSummary {
  id?: string
  slug?: string
  name?: string
  display_name?: string
  source_path?: string
}

export type ExtendedExternalCliItem = Omit<ExternalCliDiscoveredItemDto, 'kind'> & SupplyChainMetadata & SecretPlaceholderCarrier & {
  kind: ExternalCliDiscoveredItemDto['kind'] | 'agent' | string
}

export type ExtendedToolSkill = ToolSkillResource & SupplyChainMetadata

export type ExtendedToolMcp = ToolMcpResource & SecretPlaceholderCarrier & SupplyChainMetadata

export type ExtendedImportMcpInput = ImportMcpJsonInput & {
  secret_values?: Record<string, string>
}

export type ExtendedImportMcpResult = ImportMcpJsonResultDto & SecretPlaceholderCarrier

export type ExtendedInstallToolSkillInput = InstallToolSkillInput & {
  secret_values?: Record<string, string>
}

export type ExtendedInstallToolSkillResult = InstallToolSkillResultDto & SupplyChainMetadata & {
  imported_agents?: Array<ImportedAgentSummary | string> | null
}

export const KNOWN_ECOSYSTEMS = [
  { id: 'apm', label: 'Microsoft APM' },
  { id: 'claude_code', label: 'Claude Code' },
  { id: 'codex', label: 'Codex' },
  { id: 'z_code', label: 'Z Code' },
  { id: 'minimax_code', label: 'MiniMax Code' },
  { id: 'pi', label: 'Pi' },
] as const

const SECRET_PLACEHOLDER_RE = /\$\{(?:env:)?([A-Za-z_][A-Za-z0-9_]*)\}/g
const ZERO_WIDTH_RE = /[\u200B-\u200F\u2060]/
const BIDI_CONTROL_RE = /[\u202A-\u202E\u2066-\u2069]/
const UNICODE_TAG_RE = /[\u{E0000}-\u{E007F}]/u

export function parseEnvLines(raw: string): Record<string, string> {
  const result: Record<string, string> = {}
  for (const line of raw.split(/\r?\n/)) {
    const trimmed = line.trim()
    if (!trimmed || trimmed.startsWith('#')) continue
    const eq = trimmed.indexOf('=')
    if (eq <= 0) continue
    const key = trimmed.slice(0, eq).trim()
    const value = trimmed.slice(eq + 1)
    if (key) result[key] = value
  }
  return result
}

export function formatEnvLines(env?: Record<string, string | null> | null): string {
  if (!env) return ''
  return Object.entries(env)
    .filter(([key]) => Boolean(key.trim()))
    .map(([key, value]) => `${key}=${value ?? ''}`)
    .join('\n')
}

export function parseHeaderLines(raw: string): Record<string, string> {
  const result: Record<string, string> = {}
  for (const line of raw.split(/\r?\n/)) {
    const trimmed = line.trim()
    if (!trimmed || trimmed.startsWith('#')) continue
    const colon = trimmed.indexOf(':')
    if (colon <= 0) continue
    const key = trimmed.slice(0, colon).trim()
    const value = trimmed.slice(colon + 1).trim()
    if (key) result[key] = value
  }
  return result
}

export function formatHeaderLines(headers?: Record<string, string | null> | null): string {
  if (!headers) return ''
  return Object.entries(headers)
    .filter(([key]) => Boolean(key.trim()))
    .map(([key, value]) => `${key}: ${value ?? ''}`)
    .join('\n')
}

export function parseArgsInput(raw: string): string[] {
  const trimmed = raw.trim()
  if (!trimmed) return []
  if (trimmed.includes('\n') || (trimmed.includes(',') && !/["']/.test(trimmed))) {
    return trimmed
      .split(/[\r\n,]+/)
      .map(part => part.trim())
      .filter(Boolean)
  }
  const tokens: string[] = []
  let current = ''
  let quote: '"' | "'" | null = null
  for (let i = 0; i < trimmed.length; i++) {
    const ch = trimmed[i]!
    if (quote) {
      if (ch === quote) {
        quote = null
      } else if (ch === '\\' && i + 1 < trimmed.length) {
        i++
        current += trimmed[i]
      } else {
        current += ch
      }
    } else if (ch === '"' || ch === "'") {
      quote = ch
    } else if (/\s/.test(ch)) {
      if (current) {
        tokens.push(current)
        current = ''
      }
    } else {
      current += ch
    }
  }
  if (current) tokens.push(current)
  return tokens
}

export function formatArgsInput(args?: string[] | null): string {
  if (!args?.length) return ''
  return args.join(', ')
}

export function hasMaskedSecrets(server?: ToolMcpResource | null): boolean {
  if (!server) return false
  const envValues = Object.values(server.env ?? {})
  const headerValues = Object.values(server.headers ?? {})
  return [...envValues, ...headerValues].some(v => typeof v === 'string' && v.includes(SECRET_MASK))
}

/** Extract deduplicated `${VAR}` / `${env:VAR}` secret placeholder names from config text. */
export function extractSecretPlaceholders(raw: string): string[] {
  if (!raw) return []
  const seen = new Set<string>()
  const names: string[] = []
  SECRET_PLACEHOLDER_RE.lastIndex = 0
  let match: RegExpExecArray | null
  while ((match = SECRET_PLACEHOLDER_RE.exec(raw)) !== null) {
    const name = match[1]
    if (name && !seen.has(name)) {
      seen.add(name)
      names.push(name)
    }
  }
  return names
}

/** Detect pasted MCP config format: `.mcp.json`, Codex `config.toml`, or Microsoft `apm.yml`. */
export function detectMcpConfigFormat(raw: string): 'json' | 'toml' | 'apm_yml' {
  const trimmed = (raw ?? '').trim()
  if (!trimmed) return 'json'
  if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
    if (/^\s*\[mcp_servers(?:\.|\])/m.test(trimmed)) return 'toml'
    return 'json'
  }
  if (/^\s*\[mcp_servers(?:\.|\])|^\s*mcp_servers\./m.test(trimmed)) return 'toml'
  if (/^\s*dependencies\s*:/m.test(trimmed) || /^\s*mcp\s*:/m.test(trimmed)) return 'apm_yml'
  return 'json'
}

/** Client-side Unicode smuggling preview check mirroring Core's UnicodeSafetyScanner. */
export function scanHiddenUnicode(text: string): { hasHiddenUnicode: boolean; warnings: string[] } {
  if (!text) return { hasHiddenUnicode: false, warnings: [] }
  const warnings: string[] = []
  const internalBom = text.indexOf('\uFEFF', 1) !== -1
  if (ZERO_WIDTH_RE.test(text) || internalBom) {
    warnings.push('Zero-width or internal BOM control characters detected')
  }
  if (BIDI_CONTROL_RE.test(text)) {
    warnings.push('Bidirectional override/isolate control characters detected')
  }
  if (UNICODE_TAG_RE.test(text)) {
    warnings.push('Hidden Unicode Tag block characters (U+E0000..U+E007F) detected')
  }
  return { hasHiddenUnicode: warnings.length > 0, warnings }
}

export function hasHiddenUnicodeWarning(item?: SupplyChainMetadata | null): boolean {
  if (!item) return false
  if (item.has_hidden_unicode) return true
  return (item.security_warnings ?? []).some(w => /unicode|bidi|zero-width|tag block/i.test(w))
}

export function getSecurityWarnings(item?: SupplyChainMetadata | null): string[] {
  if (!item?.security_warnings?.length) return []
  return item.security_warnings.map(w => String(w).trim()).filter(Boolean)
}

export function shortCommit(commit?: string | null): string {
  const clean = (commit ?? '').trim()
  if (!clean) return ''
  return clean.length > 8 ? clean.slice(0, 8) : clean
}

export function shortDigest(hash?: string | null): string {
  const clean = (hash ?? '').trim()
  if (!clean) return ''
  const hex = clean.replace(/^sha256:/i, '')
  const shortened = hex.length > 12 ? `${hex.slice(0, 12)}…` : hex
  return `sha256:${shortened}`
}

export function ecosystemDisplayLabel(ecosystem: string): string {
  const key = (ecosystem ?? '').trim().toLowerCase()
  const known = KNOWN_ECOSYSTEMS.find(entry => entry.id === key)
  return known ? known.label : ecosystem
}
