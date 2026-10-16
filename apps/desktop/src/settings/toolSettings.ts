import type { Translate } from '@/i18n'
/** The editor consumes the schema published by Core; it does not own tool defaults. */
export interface ToolJsonSchema {
  type?: string | string[]
  properties?: Record<string, ToolJsonSchema>
  additionalProperties?: boolean | ToolJsonSchema
  required?: string[]
  items?: ToolJsonSchema
  enum?: unknown[]
  minimum?: number
  maximum?: number
  minLength?: number
  maxLength?: number
  minItems?: number
  maxItems?: number
  uniqueItems?: boolean
  format?: string
  description?: string
  default?: unknown
  [key: string]: unknown
}
export interface ToolSettingsDocument {
  schema_version: number
  revision: number
  settings: Record<string, unknown>
  effective_settings: Record<string, unknown>
  settings_hash: string
  agent_definition_id?: string
}
export interface ToolSettingsSchema {
  schema_version: number
  schema: ToolJsonSchema
  defaults?: Record<string, unknown>
  host_settings?: Record<string, unknown>
}
export interface ToolCapabilities {
  status: 'available' | 'unavailable'
  reason?: string | null
  settings_hash?: string | null
  capabilities?: {
    ripgrep?: { available: boolean; path?: string; reason?: string; reason_code?: string }
    shells?: { id: string; available: boolean; path?: string; reason?: string; reason_code?: string }[]
    sandbox?: { supported: boolean; initialized: boolean; frozen_grants: boolean }
    web?: { supported: boolean; address_guard: boolean }
  } | null
}
/** Why a probed executable (ripgrep, a shell) is missing, in the interface language from its code; the host's text when there are no words for it. */
export function capabilityReason(item: { reason?: string; reason_code?: string } | undefined, words: { t: (key: string) => string; te: (key: string) => boolean }): string | undefined {
  const key = `toolsSettings.capabilityReason.${item?.reason_code}`
  return item?.reason_code && words.te(key) ? words.t(key) : item?.reason
}
export interface ToolSettingsEffective {
  schema_version?: number
  settings?: Record<string, unknown>
  effective_settings?: Record<string, unknown>
  settings_hash: string
  /** `mcp_program` diagnostics carry the program status (not_installed, outdated, invalid, needs_reinstall). */
  resource_diagnostics?: { kind: 'mcp' | 'skills' | 'host' | 'mcp_program'; resource_id?: string | null; status: string; reason: string }[]
  [key: string]: unknown
}
export interface ToolMcpResource {
  resource_id: string
  id: string
  name: string
  enabled: boolean
  command: string
  args: string[]
  env: Record<string, string | null>
  cwd?: string | null
  revision: number
  configuration_hash: string
  project_id?: string | null
}
export type ToolMcpInput = Omit<ToolMcpResource, 'resource_id' | 'revision' | 'configuration_hash'>
export interface ToolSkillResource {
  resource_id: string
  name: string
  description: string
  enabled: boolean
  scope?: string
  project_id?: string | null
  relative_path?: string
  path?: string
  status?: string
  valid?: boolean
  reason?: string | null
  /** What `reason` is about; the UI words it from `toolsSettings.skillReason.*` and `reason_data`. */
  reason_code?: string | null
  reason_data?: Record<string, string> | null
  revision?: number
  content_hash?: string
  content?: string
  file_hash?: string | null
  user_action_id?: string | null
  action_status?: string | null
  availability?: string | null
  package_files?: { path: string; content_hash?: string; size_bytes?: number }[]
  package_hash?: string | null
  source?: string | null
  version?: string | null
  commit?: string | null
  [key: string]: unknown
}
export interface SettingsIssue { path: string; message: string }

export function formatSettings(value: unknown): string { return JSON.stringify(value, null, 2) }
export function effectiveSettings(document: ToolSettingsDocument | ToolSettingsEffective | null): Record<string, unknown> {
  return document?.effective_settings ?? document?.settings ?? {}
}

/** Strict JSON syntax plus the same object/type/range constraints returned by Core. */
export function validateSettings(text: string, schema: ToolJsonSchema | null, t: Translate, sparse = false): { value?: Record<string, unknown>; issues: SettingsIssue[] } {
  let value: unknown
  try { value = JSON.parse(text) }
  catch (error) { return { issues: [{ path: '$', message: error instanceof Error ? error.message : String(error) }] } }
  if (!value || typeof value !== 'object' || Array.isArray(value)) return { issues: [{ path: '$', message: t('toolsSettings.validation.expectedObject') }] }
  if (!schema) return { issues: [{ path: '$', message: t('toolsSettings.validation.schemaNotLoaded') }] }
  const issues: SettingsIssue[] = []
  // JSON.parse accepts duplicate keys. Configuration documents must reject them before save.
  const tokens = text.match(/"(?:\\.|[^"\\])*"|[{}\[\],:]/g) ?? []
  const stack: { object: boolean; keys: Set<string>; key: string; expectingKey: boolean; path: string }[] = []
  for (const token of tokens) {
    const parent = stack[stack.length - 1]
    if (token === '{' || token === '[') stack.push({ object: token === '{', keys: new Set(), key: '', expectingKey: token === '{', path: parent ? parent.path + (parent.object ? `.${parent.key}` : '[]') : '$' })
    else if (token === '}' || token === ']') stack.pop()
    else if (token === ',' && parent?.object) parent.expectingKey = true
    else if (token.startsWith('"') && parent?.object && parent.expectingKey) {
      const key = JSON.parse(token) as string
      if (parent.keys.has(key)) issues.push({ path: `${parent.path}.${key}`, message: t('toolsSettings.validation.duplicateProperty') })
      parent.keys.add(key); parent.key = key; parent.expectingKey = false
    }
  }
  function walk(item: unknown, rule: ToolJsonSchema, path: string) {
    const types = typeof rule.type === 'string' ? [rule.type] : rule.type ?? []
    const actual = item === null ? 'null' : Array.isArray(item) ? 'array' : typeof item
    if (types.length && !types.some(type => type === actual || (type === 'integer' && typeof item === 'number' && Number.isSafeInteger(item)))) {
      issues.push({ path, message: t('toolsSettings.validation.expectedType', { types: types.join(' / ') }) }); return
    }
    if (rule.enum && !rule.enum.some(candidate => JSON.stringify(candidate) === JSON.stringify(item))) issues.push({ path, message: t('toolsSettings.validation.expectedOneOf', { values: rule.enum.map(value => JSON.stringify(value)).join(', ') }) })
    if (typeof item === 'number') {
      if (rule.minimum !== undefined && item < rule.minimum) issues.push({ path, message: t('toolsSettings.validation.minimum', { value: rule.minimum }) })
      if (rule.maximum !== undefined && item > rule.maximum) issues.push({ path, message: t('toolsSettings.validation.maximum', { value: rule.maximum }) })
    }
    if (typeof item === 'string') {
      if (rule.format === 'uuid' && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(item)) issues.push({ path, message: t('toolsSettings.validation.expectedUuid') })
      if (rule.minLength !== undefined && item.length < rule.minLength) issues.push({ path, message: t('toolsSettings.validation.minLength', { value: rule.minLength }) })
      if (rule.maxLength !== undefined && item.length > rule.maxLength) issues.push({ path, message: t('toolsSettings.validation.maxLength', { value: rule.maxLength }) })
    }
    if (Array.isArray(item)) {
      if (rule.uniqueItems && new Set(item.map(value => JSON.stringify(value))).size !== item.length) issues.push({ path, message: t('toolsSettings.validation.duplicateItems') })
      if (rule.minItems !== undefined && item.length < rule.minItems) issues.push({ path, message: t('toolsSettings.validation.minItems', { value: rule.minItems }) })
      if (rule.maxItems !== undefined && item.length > rule.maxItems) issues.push({ path, message: t('toolsSettings.validation.maxItems', { value: rule.maxItems }) })
      if (rule.items) item.forEach((child, index) => walk(child, rule.items!, `${path}[${index}]`))
    } else if (item && typeof item === 'object') {
      const record = item as Record<string, unknown>
      if (!sparse) for (const key of rule.required ?? []) if (!(key in record)) issues.push({ path: `${path}.${key}`, message: t('toolsSettings.validation.required') })
      for (const [key, child] of Object.entries(record)) {
        const property = rule.properties?.[key]
        if (property) walk(child, property, `${path}.${key}`)
        else if (rule.additionalProperties === false) issues.push({ path: `${path}.${key}`, message: t('toolsSettings.validation.unknownProperty') })
        else if (typeof rule.additionalProperties === 'object') walk(child, rule.additionalProperties, `${path}.${key}`)
      }
    }
  }
  walk(value, schema, '$')
  return { value: value as Record<string, unknown>, issues }
}

export function settingsDiff(before: Record<string, unknown>, after: Record<string, unknown>): { path: string; before: unknown; after: unknown }[] {
  const result: { path: string; before: unknown; after: unknown }[] = []
  function walk(left: unknown, right: unknown, path: string) {
    if (JSON.stringify(left) === JSON.stringify(right)) return
    if (left && right && typeof left === 'object' && typeof right === 'object' && !Array.isArray(left) && !Array.isArray(right)) {
      for (const key of new Set([...Object.keys(left), ...Object.keys(right)])) walk((left as Record<string, unknown>)[key], (right as Record<string, unknown>)[key], path ? `${path}.${key}` : key)
    } else result.push({ path, before: left, after: right })
  }
  walk(before, after, '')
  return result
}
