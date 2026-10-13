import { describe, expect, it } from 'vitest'
import type {
  AgentRuntimeBindingDto,
  ModelCenterOverviewDto,
  ModelCenterSupplierDto,
  ModelProviderInstanceDto
} from './api'
import {
  aggregateModelCenterOverview,
  asChannel,
  bindingFromModelStrategy,
  channelForProtocol,
  discoveryAffordanceLabelKey,
  legacyRouteWarning,
  modelOptionKey,
  protocolLabelKey,
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

it('carries inference parameters and the protocol through both model-center projections', () => {
  const configured = { ...provider, protocol: 'openai-responses', revision: 4, model_parameters: { 'gpt-test': { reasoning_effort: 'high' as const } } }
  const overview = aggregateModelCenterOverview({ providers: [configured], routes: [], templates: [] })
  expect(overview.api_connections[0]).toMatchObject({ protocol: configured.protocol, model_parameters: configured.model_parameters })
  expect(providersFromOverview(overview)[0]).toMatchObject({ protocol: configured.protocol, model_parameters: configured.model_parameters, revision: 4 })
})

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
    tui_runtimes: [],
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

function harness(id: string, driver: string, protocol: string, channel: string): ModelProviderInstanceDto {
  return {
    id,
    driver,
    protocol,
    channel,
    display_name: driver,
    connection_kind: 'cli',
    binary_path: `/usr/local/bin/${driver}`,
    has_api_key: false,
    capabilities: [],
    enabled: true,
    status: 'configured',
    status_message: '',
    revision: 11,
    created_at: '2026-10-04T00:00:00Z',
    updated_at: '2026-10-04T00:00:00Z'
  }
}

describe('runtime center view', () => {
  it('collects every provider-backed runtime, but never an adapter row', () => {
    // An ACP harness is a provider instance like any other: leaving it out of the provider list is
    // what made the channel unselectable in a route. An adapter row has no provider instance behind
    // it, so projecting it would invent an id and a revision that no Core row has.
    const source = overview()
    source.acp_runtimes.push({
      id: 'adapter:one',
      runtime_id: 'adapter:one',
      source: 'adapter',
      adapter_id: 'one',
      provider_instance_id: null,
      display_name: 'Adapter',
      status: 'unknown',
      status_message: '',
      capabilities: [],
      enabled: true,
      route_purposes: []
    })

    expect(providersFromOverview(source).map((item) => item.id))
      .toEqual(['provider-http', 'provider-cli', 'provider-acp'])
  })

  it('carries the revision of every runtime section so writes can send If-Match', () => {
    const source = overview()
    source.tui_runtimes = [{
      id: 'provider-tui',
      runtime_id: 'provider-tui',
      provider_instance_id: 'provider-tui',
      source: 'provider_instance',
      channel: 'tui',
      driver: 'claude-code',
      display_name: 'Claude Code',
      protocol: 'tui',
      capabilities: [],
      enabled: true,
      status: 'configured',
      status_message: '',
      route_purposes: [],
      revision: 5
    }]
    source.api_connections[0]!.revision = 7
    source.cli_runtimes[0]!.revision = 3
    source.acp_runtimes[0]!.revision = 9

    const providers = providersFromOverview(source)
    expect(providers.find((item) => item.id === 'provider-http')?.revision).toBe(7)
    expect(providers.find((item) => item.id === 'provider-cli')?.revision).toBe(3)
    expect(providers.find((item) => item.id === 'provider-acp')?.revision).toBe(9)
    expect(providers.find((item) => item.id === 'provider-tui')?.revision).toBe(5)
  })

  it('groups harness rows by the channel their protocol names', () => {
    const aggregate = aggregateModelCenterOverview({
      providers: [
        harness('p-acp', 'codebuddy', 'acp', 'acp'),
        harness('p-tui', 'claude-code', 'tui', 'tui'),
        harness('p-cli', 'codex', 'headless-cli', 'cli'),
        harness('p-serve', 'opencode', 'opencode-serve', '')
      ],
      templates: [],
      routes: []
    })

    expect(aggregate.acp_runtimes.map((row) => row.provider_instance_id)).toEqual(['p-acp'])
    expect(aggregate.tui_runtimes.map((row) => row.provider_instance_id)).toEqual(['p-tui'])
    // `opencode serve` is reached over its own local HTTP server, so it is not a channel row: it
    // groups with the CLI section by connection_kind, and the row keeps saying which protocol.
    expect(aggregate.cli_runtimes.map((row) => row.provider_instance_id)).toEqual(['p-cli', 'p-serve'])
    expect(aggregate.cli_runtimes.find((row) => row.provider_instance_id === 'p-serve')?.protocol)
      .toBe('opencode-serve')
    expect(aggregate.api_connections).toEqual([])
  })

  it('round-trips a harness row through the sections without losing its channel', () => {
    // Aggregate → providers → aggregate must land in the same bucket, or editing a provider would
    // silently move it between sections. The projection has to carry protocol and channel back out.
    const first = aggregateModelCenterOverview({ providers: [harness('p-acp', 'cursor', 'acp', 'acp')], templates: [], routes: [] })
    const providers = providersFromOverview(first)
    const second = aggregateModelCenterOverview({ providers, templates: [], routes: [] })

    expect(second.acp_runtimes.map((row) => row.provider_instance_id)).toEqual(['p-acp'])
    expect(second.tui_runtimes).toEqual([])
    expect(second.cli_runtimes).toEqual([])
  })

  it('reads the channel from protocol first and only then from a stored channel', () => {
    expect(channelForProtocol('acp')).toBe('acp')
    expect(channelForProtocol('TUI')).toBe('tui')
    expect(channelForProtocol('headless-cli')).toBe('cli')
    expect(channelForProtocol('opencode-serve')).toBeNull()
    expect(channelForProtocol('openai-chat')).toBeNull()
    expect(asChannel('acp')).toBe('acp')
    expect(asChannel('anything-else')).toBeNull()

    // A row whose protocol is absent still lands in the right section by its stored channel.
    const legacy = { ...harness('p-legacy', 'dsh', '', 'acp') }
    const aggregate = aggregateModelCenterOverview({ providers: [legacy], templates: [], routes: [] })
    expect(aggregate.acp_runtimes.map((row) => row.provider_instance_id)).toEqual(['p-legacy'])
  })

  it('does not claim an adapter probe this build cannot perform', () => {
    // Core answers 501 NOT_IMPLEMENTED for POST /api/v1/acp/adapters/{id}/probe, and this flag is what
    // gates the only control that would call it. A `true` here is a button that cannot work.
    const aggregate = aggregateModelCenterOverview({ providers: [], templates: [], routes: [] })
    expect(aggregate.capabilities.acp_probe).toBe(false)
  })

  it('names a channel-less discovery affordance by protocol instead of calling it a CLI', () => {
    // Core reports opencode's `serve` as a fact with no channel. Labelling it 'one-shot CLI' would
    // offer the user two words for one shape and hide that this one is reached over HTTP.
    expect(discoveryAffordanceLabelKey({ channel: 'acp', protocol: 'acp' })).toBe('settings.channelAcp')
    expect(discoveryAffordanceLabelKey({ channel: 'tui', protocol: 'tui' })).toBe('settings.channelTui')
    expect(discoveryAffordanceLabelKey({ channel: 'cli', protocol: 'headless-cli' })).toBe('settings.channelCli')
    expect(discoveryAffordanceLabelKey({ channel: null, protocol: 'opencode-serve' })).toBe('settings.protocolOpencodeServe')
    expect(discoveryAffordanceLabelKey({ channel: 'bogus', protocol: 'acp' })).toBe('settings.protocolAcp')

    // An unrecorded protocol must not fall back to a channel word; the locale key exists so the row
    // says "no protocol recorded" rather than claiming a shape Core never resolved.
    expect(protocolLabelKey('no-such-protocol')).toBe('settings.protocolUnknown')
    expect(protocolLabelKey('anthropic-messages')).toBe('settings.protocolAnthropicMessages')
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
    expect(template.fields).toMatchObject({ binary_path: true, home_path: true, launch_args: false })
    expect(template.capabilities).toContain('workspace')

    // The same driver with a default endpoint is the serve runtime, and that is the one local shape
    // whose stored arguments Core actually uses.
    const served = providerTemplateFromSupplier({ ...supplier, default_base_url: 'http://127.0.0.1:4096' })
    expect(served.fields).toMatchObject({ server_url: true, launch_args: true })
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

  it('derives runtime bindings from the formal inherit|route|fixed strategy', () => {
    expect(bindingFromModelStrategy({ id: 'a', model_route_purpose: 'chat', model_strategy: { kind: 'inherit' } }))
      .toMatchObject({ selection_kind: 'inherit', writable: true, route_purpose: 'chat', runtime_kind: 'unresolved' })

    expect(bindingFromModelStrategy({
      id: 'b',
      model_route_purpose: 'planner',
      model_strategy: { kind: 'fixed', provider_instance_id: 'prov-1', model: 'gpt-x' }
    })).toMatchObject({
      selection_kind: 'fixed_model',
      source: 'agent_binding',
      writable: true,
      runtime_kind: 'model',
      provider_instance_id: 'prov-1',
      model_id: 'gpt-x'
    })

    expect(bindingFromModelStrategy({
      id: 'c',
      model_strategy: { kind: 'route', route_purpose: 'search' }
    })).toMatchObject({
      selection_kind: 'route',
      source: 'agent_binding',
      writable: true,
      route_purpose: 'search',
      runtime_kind: 'model'
    })

    // CLI/ACP are provider instances under `fixed` and may omit the model.
    expect(bindingFromModelStrategy({ id: 'd', model_strategy: { kind: 'fixed', provider_instance_id: 'cli-9' } }))
      .toMatchObject({ selection_kind: 'fixed_model', provider_instance_id: 'cli-9', model_id: null })

    expect(bindingFromModelStrategy({ id: 'e', model_strategy: null }).selection_kind).toBe('inherit')
    expect(bindingFromModelStrategy({ id: 'f', model_strategy: 'fixed' })).toMatchObject({ selection_kind: 'fixed_model' })
  })

  it('prefers the user-level runtime binding over the definition strategy', () => {
    // 定义策略停在 inherit（运行时绑定写入不会改动它）；绑定才是用户在智能体中心设的值。
    // 只读定义就是「保存成功后立刻回弹」的成因，所以这里必须是绑定赢。
    const agent = { id: 'a', model_route_purpose: 'chat', model_strategy: { kind: 'inherit' } }

    expect(bindingFromModelStrategy(agent, {
      mode: 'fixed', provider_instance_id: 'prov-1', model: 'gpt-x', revision: 3, updated_at: '2026-09-06T00:00:00Z'
    })).toMatchObject({
      selection_kind: 'fixed_model',
      source: 'agent_binding',
      writable: true,
      runtime_kind: 'model',
      provider_instance_id: 'prov-1',
      model_id: 'gpt-x'
    })

    expect(bindingFromModelStrategy(agent, {
      mode: 'route', route_purpose: 'search', revision: 4, updated_at: '2026-09-06T00:00:00Z'
    })).toMatchObject({
      selection_kind: 'route',
      source: 'agent_binding',
      route_purpose: 'search',
      runtime_kind: 'model',
      provider_instance_id: null
    })

    expect(bindingFromModelStrategy(
      { id: 'a', model_route_purpose: 'chat', model_strategy: { kind: 'fixed', provider_instance_id: 'prov-old', model: 'stale' } },
      { mode: 'inherit', revision: 5, updated_at: '2026-09-06T00:00:00Z' }
    )).toMatchObject({ selection_kind: 'inherit', route_purpose: 'chat', runtime_kind: 'unresolved', model_id: null })

    // CLI/ACP 的 fixed 绑定没有 model。
    expect(bindingFromModelStrategy(agent, {
      mode: 'fixed', provider_instance_id: 'cli-9', model: null, revision: 6, updated_at: '2026-09-06T00:00:00Z'
    })).toMatchObject({ selection_kind: 'fixed_model', provider_instance_id: 'cli-9', model_id: null, model_source: 'unset' })
  })

  it('falls back to the definition strategy when no runtime binding exists', () => {
    const agent = { id: 'a', model_route_purpose: 'chat', model_strategy: { kind: 'fixed', provider_instance_id: 'prov-1', model: 'gpt-x' } }
    expect(bindingFromModelStrategy(agent, null)).toMatchObject({ selection_kind: 'fixed_model', model_id: 'gpt-x' })
    expect(bindingFromModelStrategy(agent, undefined)).toMatchObject({ selection_kind: 'fixed_model', model_id: 'gpt-x' })
  })

  it('resolves the inherit summary from effective_previews instead of showing nothing', () => {
    const inherited = {
      selection_kind: 'inherit' as const,
      source: 'agent_binding' as const,
      writable: true,
      route_purpose: 'chat',
      runtime_kind: 'unresolved' as const,
      runtime_id: null,
      provider_instance_id: null,
      model_id: null,
      model_source: 'unset' as const,
      shared_agent_ids: [],
      warnings: []
    }
    const previews = {
      'conversation.auto': {
        strategy_source: 'workspace_default',
        chain: [],
        candidates: [],
        expected_selection: { position: 0, provider_instance_id: 'prov-1', model: 'gpt-x', available: true }
      }
    } as never

    expect(runtimeSourceSummary(inherited, previews, () => 'OpenAI')).toBe('OpenAI · gpt-x')
    // provider 名字查不到时只显示模型，不显示裸 uuid。
    expect(runtimeSourceSummary(inherited, previews, () => null)).toBe('gpt-x')
    // 没有可用解析结果时仍然返回空串，交由调用方渲染「尚未解析」。
    expect(runtimeSourceSummary(inherited, {} as never, () => 'OpenAI')).toBe('')
  })
})
