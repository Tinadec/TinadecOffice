import { describe, expect, it } from 'vitest'
import type {
  AgentRuntimeBindingDto,
  ModelCenterOverviewDto,
  ModelCenterSupplierDto,
  ModelProviderInstanceDto
} from './api'
import {
  legacyRouteWarning,
  modelOptionKey,
  providerTemplateFromSupplier,
  providersFromOverview,
  runtimeSourceSummary
} from './runtimeCenterView'

const provider: ModelProviderInstanceDto = {
  id: 'provider-http',
  driver: 'openai-compatible',
  display_name: 'OpenAI',
  connection_kind: 'api-key',
  model: 'gpt-test',
  has_api_key: true,
  capabilities: ['chat'],
  enabled: true,
  status: 'ready',
  status_message: '',
  created_at: '2026-07-13T00:00:00Z',
  updated_at: '2026-07-13T00:00:00Z'
}

function overview(): ModelCenterOverviewDto {
  return {
    capabilities: {
      provider_crud: true,
      model_catalog_mode: 'configured_only',
      model_discovery_refresh: false,
      live_model_discovery: false,
      agent_runtime_binding_write: false,
      acp_adapter_read: true,
      acp_probe: true
    },
    suppliers: [],
    api_connections: [{
      id: provider.id,
      provider_instance_id: provider.id,
      driver: provider.driver,
      display_name: provider.display_name,
      connection_kind: provider.connection_kind,
      transport_kind: 'http',
      credential_kind: 'api_key',
      model: provider.model,
      has_api_key: true,
      capabilities: provider.capabilities,
      enabled: true,
      status: 'ready',
      status_message: '',
      route_purposes: []
    }],
    models: [],
    cli_runtimes: [{
      id: 'provider-cli',
      runtime_id: 'provider-cli',
      provider_instance_id: 'provider-cli',
      source: 'provider_instance',
      driver: 'codex-cli',
      display_name: 'Codex CLI',
      capabilities: ['cli'],
      enabled: true,
      status: 'ready',
      status_message: '',
      route_purposes: []
    }],
    acp_runtimes: [{
      id: 'provider-acp',
      runtime_id: 'provider-acp',
      source: 'legacy_provider',
      provider_instance_id: 'provider-acp',
      driver: 'cursor-acp',
      display_name: 'ACP',
      status: 'ready',
      status_message: '',
      capabilities: ['acp'],
      enabled: true,
      route_purposes: []
    }],
    readiness: {},
    diagnostics: []
  }
}

describe('runtime center view', () => {
  it('collects API, CLI, and legacy ACP provider instances without duplicates', () => {
    const providers = providersFromOverview(overview())
    expect(providers.map((item) => item.id)).toEqual(['provider-http', 'provider-cli', 'provider-acp'])
  })

  it('derives form fields from the Core supplier contract for unknown drivers', () => {
    const supplier: ModelCenterSupplierDto = {
      supplier_id: 'future-local:future-local',
      provider_family: 'future-local',
      driver: 'future-local',
      display_name: 'Future Local',
      connection_kind: 'local-server',
      summary: 'Local runtime',
      contributor_description: '',
      transport_kind: 'local_http',
      credential_kind: 'none',
      default_base_url: 'http://127.0.0.1:9000/v1',
      default_model: 'default',
      default_timeout_seconds: 120,
      capabilities: {
        supports_streaming: true,
        supports_tools: false,
        supports_json_mode: false,
        supports_system_prompt: true,
        requires_workspace: false,
        credential_kind: 'none',
        health_status: 'unknown'
      }
    }

    const template = providerTemplateFromSupplier(supplier)
    expect(template).toMatchObject({
      driver: 'future-local',
      category: 'local-server',
      connection_kind: 'local-server'
    })
    expect(template.fields).toMatchObject({ base_url: true, model: true, api_key: false, binary_path: false })
  })

  it('uses Core supplier capabilities instead of executable form rules from local presentation metadata', () => {
    const supplier: ModelCenterSupplierDto = {
      supplier_id: 'cursor-acp',
      provider_family: 'cursor-acp',
      driver: 'cursor-acp',
      display_name: 'Cursor ACP',
      connection_kind: 'cli',
      summary: 'Cursor runtime',
      contributor_description: '',
      transport_kind: 'cli',
      credential_kind: 'cli',
      default_base_url: null,
      default_model: 'auto',
      default_timeout_seconds: 180,
      capabilities: {
        supports_streaming: false,
        supports_tools: true,
        supports_json_mode: false,
        requires_workspace: true
      }
    }

    const template = providerTemplateFromSupplier(supplier)
    expect(template.fields).toMatchObject({ binary_path: true, home_path: true, launch_args: true })
    expect(template.capabilities).toContain('workspace')
  })

  it('reports shared legacy route impact without changing the binding', () => {
    const binding: AgentRuntimeBindingDto = {
      selection_kind: 'inherit',
      source: 'legacy_route',
      writable: false,
      route_purpose: 'search',
      runtime_kind: 'model',
      runtime_id: 'provider-http',
      provider_instance_id: 'provider-http',
      provider_display_name: 'OpenAI',
      model_id: 'gpt-test',
      model_source: 'provider_default',
      shared_agent_ids: ['agent-b'],
      warnings: [{ code: 'LEGACY_SHARED_ROUTE', message: 'shared', shared_agent_ids: ['agent-b'] }]
    }

    expect(legacyRouteWarning(binding)).toEqual({ purpose: 'search', agent_ids: ['agent-b'] })
    expect(binding.selection_kind).toBe('inherit')
  })

  it('formats runtime summaries and collision-safe model option keys', () => {
    expect(runtimeSourceSummary({
      selection_kind: 'inherit',
      source: 'legacy_route',
      writable: false,
      route_purpose: 'planner',
      runtime_kind: 'model',
      runtime_id: 'provider-http',
      provider_instance_id: 'provider-http',
      provider_display_name: 'OpenAI',
      model_id: 'gpt-test',
      model_source: 'provider_default',
      shared_agent_ids: [],
      warnings: []
    })).toBe('OpenAI · gpt-test')
    expect(runtimeSourceSummary({
      selection_kind: 'inherit',
      source: 'legacy_route',
      writable: false,
      route_purpose: 'missing',
      runtime_kind: 'unresolved',
      runtime_id: null,
      provider_instance_id: null,
      provider_display_name: null,
      model_id: null,
      model_source: 'unset',
      shared_agent_ids: [],
      warnings: []
    })).toBe('')
    expect(modelOptionKey('a:b', 'c')).not.toBe(modelOptionKey('a', 'b:c'))
  })
})
