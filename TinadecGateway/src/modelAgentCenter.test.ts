import assert from 'node:assert/strict';
import test from 'node:test';
import {
  agentRuntimeBindingWriteResult,
  aggregateAgentCenter,
  aggregateModelCenter,
  loadAgentCenterOverview,
  loadModelCenterOverview,
  modelDiscoveryRefreshResult,
  validateAgentRuntimeBindingInput,
  type CoreJsonFetcher
} from './modelAgentCenter.js';

const templates = [
  {
    provider_family: 'openai-compatible',
    driver: 'openai-compatible',
    display_name: 'OpenAI Compatible',
    connection_kind: 'http',
    credential_kind: 'api_key',
    summary: 'HTTP provider',
    contributor_description: 'Contributor description',
    default_base_url: 'https://example.invalid/v1',
    default_model: 'model-default',
    default_timeout_seconds: 60,
    capabilities: { supports_tools: true, api_key: 'must-not-leak' }
  },
  {
    provider_family: 'codex-cli',
    driver: 'codex-cli',
    display_name: 'Codex CLI',
    connection_kind: 'cli',
    credential_kind: 'cli',
    summary: 'CLI provider',
    contributor_description: 'CLI contributor description',
    default_base_url: null,
    default_model: 'cli-model',
    default_timeout_seconds: 180,
    capabilities: { requires_workspace: true }
  }
];

const providers = [
  provider({
    id: 'provider_api',
    driver: 'openai-compatible',
    display_name: 'Primary API',
    connection_kind: 'http',
    model: 'model-default',
    has_api_key: true,
    api_key: 'must-not-leak'
  }),
  provider({
    id: 'provider_local_unknown',
    driver: 'future-local-driver',
    display_name: 'Future local runtime',
    connection_kind: 'local-server',
    model: 'local-model',
    capabilities: ['chat', 'no-api-key']
  }),
  provider({
    id: 'provider_cli',
    driver: 'codex-cli',
    display_name: 'Codex CLI',
    connection_kind: 'cli',
    model: 'cli-model',
    binary_path: 'C:/tools/codex.exe',
    home_path: 'D:/work',
    capabilities: ['agent', 'cli', 'workspace']
  }),
  provider({
    id: 'provider_acp_legacy',
    driver: 'cursor-acp',
    display_name: 'Legacy Cursor ACP',
    connection_kind: 'cli',
    model: 'auto',
    binary_path: 'C:/tools/cursor-agent.exe',
    capabilities: ['agent', 'cli', 'acp']
  })
];

const routes = [
  route('planner', 'provider_api', null),
  route('review', 'provider_api', 'model-default'),
  route('alternate', 'provider_api', 'model-route-override'),
  route('executor', 'provider_cli', null),
  route('external', 'provider_acp_legacy', null)
];

test('model center classifies API, local, CLI, and ACP resources without merging legacy ACP providers', () => {
  const overview = aggregateModelCenter({
    templates,
    providers,
    routes,
    model_readiness: {
      status: 'ready',
      api_key: 'must-not-leak',
      providers: [{ provider_instance_id: 'provider_api', status: 'ready', api_key: 'must-not-leak' }]
    },
    catalog_readiness: { status: 'warning' },
    acp_adapters: [{
      id: 'adapter_cursor',
      extension_id: 'extension_cursor',
      name: 'Cursor ACP Adapter',
      command: 'cursor agent acp',
      status: 'ready',
      status_message: 'Installed',
      capabilities: ['agent', 'acp'],
      updated_at: '2026-07-13T00:00:00Z'
    }]
  });

  assert.equal(overview.suppliers.length, 2);
  assert.equal(overview.suppliers[0].transport_kind, 'http');
  assert.equal(overview.suppliers[0].credential_kind, 'api_key');
  assert.equal(overview.suppliers[1].transport_kind, 'cli');
  assert.equal(overview.api_connections.length, 2);
  assert.equal(overview.api_connections.find((item) => item.id === 'provider_local_unknown')?.transport_kind, 'local_http');
  assert.equal(overview.api_connections.find((item) => item.id === 'provider_local_unknown')?.credential_kind, 'none');
  assert.deepEqual(overview.cli_runtimes.map((item) => item.id), ['provider_cli']);
  assert.deepEqual(overview.acp_runtimes.map((item) => item.source).sort(), ['adapter', 'legacy_provider']);
  assert.ok(overview.acp_runtimes.some((item) => item.runtime_id === 'adapter:adapter_cursor' && item.source === 'adapter'));
  assert.ok(overview.acp_runtimes.some((item) => item.runtime_id === 'legacy_provider:provider_acp_legacy' && item.source === 'legacy_provider'));
  assert.equal(overview.capabilities.model_catalog_mode, 'configured_only');
  assert.equal(overview.capabilities.model_discovery_refresh, false);
  assert.equal(overview.capabilities.agent_runtime_binding_write, false);
  assert.ok(!JSON.stringify(overview).includes('must-not-leak'));
});

test('Core local-http templates stay local and ACP runtime ids remain unambiguous across sources', () => {
  const overview = aggregateModelCenter({
    templates: [{
      provider_family: 'local-http',
      driver: 'local-http-openai-compatible',
      display_name: 'Local OpenAI Compatible',
      connection_kind: 'http',
      credential_kind: 'none',
      summary: 'Local runtime',
      contributor_description: '',
      default_base_url: 'http://127.0.0.1:8080/v1',
      default_model: 'local-model',
      default_timeout_seconds: 120,
      capabilities: { requires_workspace: false }
    }],
    providers: [
      provider({
        id: 'local-provider',
        driver: 'local-http-openai-compatible',
        connection_kind: 'http',
        has_api_key: false
      }),
      provider({
        id: 'same-id',
        driver: 'cursor-acp',
        connection_kind: 'cli',
        capabilities: ['agent', 'cli', 'acp']
      })
    ],
    routes: [],
    acp_adapters: [{ id: 'same-id', name: 'Same ID adapter', status: 'ready' }]
  });

  assert.equal(overview.suppliers[0]?.transport_kind, 'local_http');
  assert.equal(overview.api_connections[0]?.transport_kind, 'local_http');
  assert.deepEqual(
    overview.acp_runtimes.map((runtime) => runtime.runtime_id).sort(),
    ['adapter:same-id', 'legacy_provider:same-id']
  );
});

test('configured models deduplicate provider defaults and route overrides and exclude CLI/ACP models', () => {
  const overview = aggregateModelCenter({ templates, providers, routes });

  assert.equal(overview.models.length, 3);
  const defaultModel = overview.models.find((model) => model.provider_instance_id === 'provider_api'
    && model.model_id === 'model-default');
  assert.deepEqual(defaultModel?.configuration_sources, ['provider_default', 'route_override']);
  assert.deepEqual(defaultModel?.route_purposes, ['planner', 'review']);
  assert.equal(defaultModel?.is_provider_default, true);

  const override = overview.models.find((model) => model.model_id === 'model-route-override');
  assert.deepEqual(override?.configuration_sources, ['route_override']);
  assert.deepEqual(override?.route_purposes, ['alternate']);
  assert.equal(override?.is_provider_default, false);
  assert.ok(!overview.models.some((model) => model.provider_instance_id === 'provider_cli'));
  assert.ok(!overview.models.some((model) => model.provider_instance_id === 'provider_acp_legacy'));
});

test('agent center derives read-only legacy bindings and warns for shared route purposes', () => {
  const overview = aggregateAgentCenter({
    templates,
    providers,
    routes,
    agents: [
      agent('agent_one', 'planner'),
      agent('agent_two', 'planner'),
      agent('agent_override', 'alternate'),
      agent('agent_cli', 'executor'),
      agent('agent_acp', 'external'),
      agent('agent_missing', 'missing')
    ],
    modes: [{ id: 'balanced', display_name: 'Balanced', api_key: 'must-not-leak' }],
    candidates: [{ id: 'candidate_one', name: 'Candidate', secret: 'must-not-leak' }]
  });

  const first = overview.agents.find((agent) => agent.id === 'agent_one');
  assert.equal(first?.runtime_binding.selection_kind, 'inherit');
  assert.equal(first?.runtime_binding.source, 'legacy_route');
  assert.equal(first?.runtime_binding.writable, false);
  assert.equal(first?.runtime_binding.runtime_kind, 'model');
  assert.equal(first?.runtime_binding.model_id, 'model-default');
  assert.equal(first?.runtime_binding.model_source, 'provider_default');
  assert.deepEqual(first?.runtime_binding.shared_agent_ids, ['agent_two']);
  assert.equal(first?.runtime_binding.warnings[0]?.code, 'LEGACY_SHARED_ROUTE');

  const overrideBinding = overview.agents.find((agent) => agent.id === 'agent_override')?.runtime_binding;
  assert.equal(overrideBinding?.model_id, 'model-route-override');
  assert.equal(overrideBinding?.model_source, 'route_override');
  assert.equal(overview.agents.find((agent) => agent.id === 'agent_cli')?.runtime_binding.runtime_kind, 'cli');
  const acpBinding = overview.agents.find((agent) => agent.id === 'agent_acp')?.runtime_binding;
  assert.equal(acpBinding?.runtime_kind, 'acp');
  assert.equal(acpBinding?.runtime_id, 'legacy_provider:provider_acp_legacy');
  assert.equal(overview.agents.find((agent) => agent.id === 'agent_missing')?.runtime_binding.runtime_kind, 'unresolved');

  const warning = overview.diagnostics.find((diagnostic) => diagnostic.code === 'LEGACY_SHARED_ROUTE');
  assert.deepEqual(warning?.agent_ids, ['agent_one', 'agent_two']);
  assert.deepEqual(Object.keys(overview.runtime_sources).sort(), ['acp_runtimes', 'cli_runtimes', 'models', 'providers']);
  assert.ok(!JSON.stringify(overview).includes('must-not-leak'));
});

test('runtime binding validator accepts all five variants and rejects mixed or incomplete inputs', () => {
  assert.deepEqual(validateAgentRuntimeBindingInput({ selection_kind: 'inherit' }), {
    ok: true,
    value: { selection_kind: 'inherit' }
  });
  assert.equal(validateAgentRuntimeBindingInput({
    selection_kind: 'fixed_model',
    provider_instance_id: ' provider_api ',
    model_id: ' model-one '
  }).ok, true);
  assert.equal(validateAgentRuntimeBindingInput({ selection_kind: 'provider_auto', provider_instance_id: 'provider_api' }).ok, true);
  assert.equal(validateAgentRuntimeBindingInput({ selection_kind: 'cli', runtime_id: 'provider_cli' }).ok, true);
  assert.equal(validateAgentRuntimeBindingInput({ selection_kind: 'acp', runtime_id: 'adapter_cursor' }).ok, true);

  const missingModel = validateAgentRuntimeBindingInput({ selection_kind: 'fixed_model', provider_instance_id: 'provider_api' });
  assert.equal(missingModel.ok, false);
  if (!missingModel.ok) assert.match(missingModel.errors.join(' '), /model_id/);

  const mixed = validateAgentRuntimeBindingInput({
    selection_kind: 'provider_auto',
    provider_instance_id: 'provider_api',
    model_id: 'not-allowed'
  });
  assert.equal(mixed.ok, false);
  if (!mixed.ok) assert.match(mixed.errors.join(' '), /not valid/);

  assert.equal(validateAgentRuntimeBindingInput({ selection_kind: 'future' }).ok, false);
  assert.equal(validateAgentRuntimeBindingInput(null).ok, false);
});

test('unsupported write handlers return explicit 400 and 501 contracts without calling Core', () => {
  const invalid = agentRuntimeBindingWriteResult('agent_one', {
    selection_kind: 'fixed_model',
    provider_instance_id: 'provider_api'
  });
  assert.equal(invalid.status, 400);
  assert.equal((invalid.data as { code: string }).code, 'AGENT_RUNTIME_BINDING_INVALID');

  const unsupported = agentRuntimeBindingWriteResult('agent_one', {
    selection_kind: 'provider_auto',
    provider_instance_id: 'provider_api'
  });
  assert.equal(unsupported.status, 501);
  assert.equal((unsupported.data as { code: string }).code, 'AGENT_RUNTIME_BINDING_UNSUPPORTED');

  const invalidRefresh = modelDiscoveryRefreshResult('   ');
  assert.equal(invalidRefresh.status, 400);
  const unsupportedRefresh = modelDiscoveryRefreshResult('provider_api');
  assert.equal(unsupportedRefresh.status, 501);
  assert.equal((unsupportedRefresh.data as { code: string }).code, 'MODEL_DISCOVERY_UNSUPPORTED');
});

test('overview loaders degrade optional 404/501 responses into diagnostics', async () => {
  const fetcher = mapFetcher({
    '/api/v1/model-provider-templates': ok(templates),
    '/api/v1/model-providers': ok(providers),
    '/api/v1/model-routes': ok(routes),
    '/api/v1/model-readiness': { status: 404, data: { code: 'NOT_FOUND' } },
    '/api/v1/model-catalog-readiness': { status: 501, data: { code: 'NOT_IMPLEMENTED' } },
    '/api/v1/acp/adapters': { status: 404, data: { code: 'NOT_FOUND' } }
  });

  const result = await loadModelCenterOverview(fetcher);
  assert.equal(result.status, 200);
  const overview = result.data as ReturnType<typeof aggregateModelCenter>;
  assert.equal(overview.diagnostics.length, 3);
  assert.equal(overview.readiness.model, null);
  assert.equal(overview.readiness.catalog, null);
  assert.equal(overview.capabilities.acp_adapter_read, false);
  assert.equal(overview.capabilities.acp_probe, false);
});

test('agent overview degrades optional modes/candidates while preserving agents and sources', async () => {
  const fetcher = mapFetcher({
    '/api/v1/model-provider-templates': ok(templates),
    '/api/v1/model-providers': ok(providers),
    '/api/v1/model-routes': ok(routes),
    '/api/v1/model-readiness': ok({ status: 'ready', providers: [] }),
    '/api/v1/model-catalog-readiness': ok({ status: 'ready' }),
    '/api/v1/acp/adapters': ok([]),
    '/api/v1/agents': ok([agent('agent_one', 'planner')]),
    '/api/v1/agent-modes': { status: 404, data: { code: 'NOT_FOUND' } },
    '/api/v1/agent-candidates': { status: 501, data: { code: 'NOT_IMPLEMENTED' } }
  });

  const result = await loadAgentCenterOverview(fetcher);
  assert.equal(result.status, 200);
  const overview = result.data as ReturnType<typeof aggregateAgentCenter>;
  assert.equal(overview.agents.length, 1);
  assert.deepEqual(overview.modes, []);
  assert.deepEqual(overview.candidates, []);
  assert.ok(overview.diagnostics.some((item) => item.source === 'agent_modes'));
  assert.ok(overview.diagnostics.some((item) => item.source === 'agent_candidates'));
});

test('overview loaders preserve required failures, optional non-compatibility failures, and Core 502', async () => {
  const requiredFailure = await loadModelCenterOverview(mapFetcher({
    '/api/v1/model-provider-templates': { status: 500, data: { code: 'CORE_FAILURE', api_key: 'must-not-leak' } },
    '/api/v1/model-providers': ok([]),
    '/api/v1/model-routes': ok([]),
    '/api/v1/model-readiness': ok(null),
    '/api/v1/model-catalog-readiness': ok(null),
    '/api/v1/acp/adapters': ok([])
  }));
  assert.equal(requiredFailure.status, 500);
  assert.ok(!JSON.stringify(requiredFailure).includes('must-not-leak'));

  const optionalFailure = await loadModelCenterOverview(mapFetcher({
    '/api/v1/model-provider-templates': ok([]),
    '/api/v1/model-providers': ok([]),
    '/api/v1/model-routes': ok([]),
    '/api/v1/model-readiness': { status: 500, data: { code: 'CORE_FAILURE' } },
    '/api/v1/model-catalog-readiness': ok(null),
    '/api/v1/acp/adapters': ok([])
  }));
  assert.equal(optionalFailure.status, 500);

  const unreachable = await loadModelCenterOverview(mapFetcher({
    '/api/v1/model-provider-templates': ok([]),
    '/api/v1/model-providers': ok([]),
    '/api/v1/model-routes': ok([]),
    '/api/v1/model-readiness': ok(null),
    '/api/v1/model-catalog-readiness': ok(null),
    '/api/v1/acp/adapters': { status: 502, data: { code: 'CORE_UNREACHABLE', nested: { api_key: 'must-not-leak' } } }
  }));
  assert.equal(unreachable.status, 502);
  assert.equal((unreachable.data as { code: string }).code, 'CORE_UNREACHABLE');
  assert.ok(!JSON.stringify(unreachable).includes('must-not-leak'));
});

function provider(overrides: Record<string, unknown>): Record<string, unknown> {
  return {
    id: 'provider',
    driver: 'openai-compatible',
    display_name: 'Provider',
    connection_kind: 'http',
    base_url: 'https://example.invalid/v1',
    model: null,
    has_api_key: false,
    binary_path: null,
    home_path: null,
    server_url: null,
    launch_args: null,
    capabilities: ['chat'],
    enabled: true,
    status: 'ready',
    status_message: 'Ready',
    cooldown_until: null,
    created_at: '2026-07-13T00:00:00Z',
    updated_at: '2026-07-13T00:00:00Z',
    ...overrides
  };
}

function route(purpose: string, providerInstanceId: string, model: string | null): Record<string, unknown> {
  return {
    purpose,
    provider_instance_id: providerInstanceId,
    model,
    updated_at: '2026-07-13T00:00:00Z'
  };
}

function agent(id: string, purpose: string): Record<string, unknown> {
  return {
    id,
    name: id,
    layer: 'execution',
    agent_type: 'test',
    mode: 'balanced',
    description: 'Test agent',
    model_route_purpose: purpose,
    allowed_tools: [],
    capabilities: [],
    system_prompt: null,
    enabled: true,
    is_built_in: true,
    updated_at: '2026-07-13T00:00:00Z'
  };
}

function ok(data: unknown) {
  return { status: 200, data };
}

function mapFetcher(results: Record<string, { status: number; data: unknown }>): CoreJsonFetcher {
  return async (path) => {
    const result = results[path];
    assert.ok(result, `Unexpected Core request: ${path}`);
    return result;
  };
}
