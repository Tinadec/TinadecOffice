import { describe, expect, it } from 'vitest'
import type { ModelProviderInstanceDto, ModelReadinessReceiptDto } from './api'
import { buildModelCenterRows, filterModelCenterRows } from './modelCenterView'
import type { ProviderTemplate } from './providerTemplates'

function provider(id: string, driver: string, displayName = driver, model = `${driver}-model`): ModelProviderInstanceDto {
  return {
    id,
    driver,
    display_name: displayName,
    connection_kind: 'api-key',
    model,
    has_api_key: true,
    capabilities: [],
    enabled: true,
    status: 'ready',
    status_message: '',
    created_at: '2026-07-13T00:00:00Z',
    updated_at: '2026-07-13T00:00:00Z'
  }
}

function template(driver: string, displayName = driver): ProviderTemplate {
  return {
    driver,
    display_name_key: `providers.${displayName}`,
    summary_key: `providers.${displayName}Summary`,
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: null,
    default_model: `${driver}-default`,
    capabilities: [],
    brand_color: '#000000',
    brand_bg: 'transparent',
    icon: '',
    fields: {
      base_url: true,
      model: true,
      api_key: true,
      binary_path: false,
      home_path: false,
      server_url: false,
      launch_args: false
    },
    placeholders: {}
  }
}

function readiness(statusByProvider: Record<string, string>): ModelReadinessReceiptDto {
  return {
    status: 'blocked',
    generated_at: '2026-07-13T00:00:00Z',
    receipt_id: 'receipt',
    provider_count: Object.keys(statusByProvider).length,
    ready_provider_count: 0,
    warning_provider_count: 0,
    blocked_provider_count: 0,
    route_count: 0,
    ready_route_count: 0,
    warning_route_count: 0,
    blocked_route_count: 0,
    providers: Object.entries(statusByProvider).map(([id, status]) => ({
      provider_instance_id: id,
      display_name: id,
      driver: id,
      connection_kind: 'api-key',
      status,
      provider_status: status,
      enabled: true,
      has_credential: true,
      route_purposes: [],
      summary: '',
      evidence: []
    })),
    routes: [],
    design_notes: []
  }
}

describe('model center view', () => {
  it('keeps multiple instances and unknown drivers while omitting configured template duplicates', () => {
    const rows = buildModelCenterRows(
      [provider('openai-1', 'openai'), provider('openai-2', 'openai'), provider('unknown-1', 'unknown')],
      [template('openai'), template('anthropic')],
      null,
      (key) => key.replace('providers.', '')
    )

    expect(rows.map((row) => row.key)).toEqual([
      'instance:openai-1',
      'instance:openai-2',
      'instance:unknown-1',
      'template:anthropic'
    ])
  })

  it('sorts Core blocked and warning instances before other configured instances', () => {
    const rows = buildModelCenterRows(
      [provider('ready', 'ready'), provider('warning', 'warning'), provider('blocked', 'blocked')],
      [],
      readiness({ ready: 'ready', warning: 'warning', blocked: 'blocked' })
    )

    expect(rows.map((row) => row.key)).toEqual([
      'instance:blocked',
      'instance:warning',
      'instance:ready'
    ])
  })

  it('supports all four filters without treating available templates as issues', () => {
    const rows = buildModelCenterRows(
      [provider('blocked', 'openai'), provider('ready', 'anthropic')],
      [template('openai'), template('anthropic'), template('ollama')],
      readiness({ blocked: 'blocked', ready: 'ready' })
    )

    expect(filterModelCenterRows(rows, 'all', '')).toHaveLength(3)
    expect(filterModelCenterRows(rows, 'issues', '').map((row) => row.key)).toEqual(['instance:blocked'])
    expect(filterModelCenterRows(rows, 'configured', '')).toHaveLength(2)
    expect(filterModelCenterRows(rows, 'available', '').map((row) => row.key)).toEqual(['template:ollama'])
  })

  it('searches translated names, drivers, connection kinds, and models case-insensitively', () => {
    const rows = buildModelCenterRows(
      [provider('openai', 'openai-compatible', 'OpenAI Compatible', 'GPT-5.4-mini')],
      [template('ollama', 'Ollama')],
      null,
      (key) => key.replace('providers.', '')
    )

    expect(filterModelCenterRows(rows, 'all', 'gpt-5.4')).toHaveLength(1)
    expect(filterModelCenterRows(rows, 'all', 'OPENAI-COMPATIBLE')).toHaveLength(1)
    expect(filterModelCenterRows(rows, 'all', 'api-key')).toHaveLength(2)
    expect(filterModelCenterRows(rows, 'all', 'ollama')).toHaveLength(1)
  })

  it('degrades to stable provider order when the readiness receipt is missing', () => {
    const rows = buildModelCenterRows(
      [provider('first', 'first'), provider('second', 'second')],
      [],
      null
    )

    expect(rows.map((row) => row.key)).toEqual(['instance:first', 'instance:second'])
    expect(filterModelCenterRows(rows, 'issues', '')).toEqual([])
  })
})
