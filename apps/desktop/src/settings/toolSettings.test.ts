import { describe, expect, it } from 'vitest'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import { settingsDiff, validateSettings, type ToolJsonSchema } from './toolSettings'

const { t } = createI18n({ legacy: false, locale: 'en', messages: { en } }).global
const schema: ToolJsonSchema = { type: 'object', additionalProperties: false, properties: {
  shell: { type: 'object', additionalProperties: false, properties: { enabled: { type: 'boolean' }, timeout_ms: { type: 'integer', minimum: 1, maximum: 1000 } } },
  skills: { type: 'object', additionalProperties: false, properties: { resource_ids: { type: ['array', 'null'], uniqueItems: true, items: { type: 'string', format: 'uuid' } } } },
} }
describe('strict tool configuration editor', () => {
  it('accepts sparse overrides and preserves null, empty array and omitted binding as distinct values', () => {
    expect(validateSettings('{}', schema, t).issues).toEqual([])
    expect(validateSettings('{"skills":{"resource_ids":null}}', schema, t).value).toEqual({ skills: { resource_ids: null } })
    expect(validateSettings('{"skills":{"resource_ids":[]}}', schema, t).value).toEqual({ skills: { resource_ids: [] } })
  })
  it('rejects comments, trailing commas, duplicate keys including escaped names and unknown properties', () => {
    for (const text of ['{"shell":{},}', '{/* comment */}', '{"shell":{"enabled":true,"enabled":false}}', '{"shell":{},"sh\\u0065ll":{}}', '{"unknown":{}}']) expect(validateSettings(text, schema, t).issues.length).toBeGreaterThan(0)
  })
  it('identifies exact type, range, resource identity and duplicate selection errors', () => {
    expect(validateSettings('{"shell":{"enabled":"yes","timeout_ms":1001}}', schema, t).issues.map(issue => issue.path)).toEqual(['$.shell.enabled', '$.shell.timeout_ms'])
    const id = '11111111-1111-1111-1111-111111111111'
    expect(validateSettings(JSON.stringify({ skills: { resource_ids: [id, id] } }), schema, t).issues[0]?.message).toContain('Duplicate')
    expect(validateSettings('{"skills":{"resource_ids":["not-a-resource"]}}', schema, t).issues[0]?.path).toBe('$.skills.resource_ids[0]')
  })
  it('diffs arrays as replacements and shows reset-to-inherit removals', () => {
    expect(settingsDiff({ shell: { enabled: false }, skills: { resource_ids: ['a'] } }, { skills: { resource_ids: [] } })).toEqual([
      { path: 'shell', before: { enabled: false }, after: undefined }, { path: 'skills.resource_ids', before: ['a'], after: [] },
    ])
  })
})
