import type {
  AcpAdapterDto,
  AgentCenterOverviewDto,
  AgentRuntimeBindingDto,
  CenterDiagnosticDto,
  ModelCatalogReadinessReceiptDto,
  ModelCenterAcpRuntimeDto,
  ModelCenterApiConnectionDto,
  ModelCenterCliRuntimeDto,
  ModelCenterOverviewDto,
  ModelCenterSupplierDto,
  ModelProviderInstanceDto,
  ModelProviderTemplateDto,
  ModelReadinessReceiptDto,
  ModelRouteDto
} from './api'
import { findTemplate, type ProviderCategory, type ProviderTemplate } from './providerTemplates'

export type ModelCenterSection = 'suppliers' | 'api' | 'models' | 'routes' | 'cli' | 'acp'

export const supplierPresentationAliases = new Map<string, string>([
  ['openai', 'openai-compatible'],
  ['local-http', 'custom'],
  ['local-http-openai-compatible', 'openai-compatible'],
  ['local-http-ollama', 'ollama']
])

function uniqueProviders(providers: ModelProviderInstanceDto[]) {
  const byId = new Map<string, ModelProviderInstanceDto>()
  for (const provider of providers) byId.set(provider.id, provider)
  return [...byId.values()]
}

function apiConnectionProvider(connection: ModelCenterApiConnectionDto): ModelProviderInstanceDto {
  return {
    id: connection.provider_instance_id,
    driver: connection.driver,
    display_name: connection.display_name,
    connection_kind: connection.connection_kind,
    base_url: connection.base_url ?? null,
    model: connection.model ?? null,
    has_api_key: connection.has_api_key,
    server_url: connection.server_url ?? null,
    capabilities: connection.capabilities,
    enabled: connection.enabled,
    status: connection.status,
    status_message: connection.status_message,
    cooldown_until: connection.cooldown_until ?? null,
    created_at: connection.created_at ?? '',
    updated_at: connection.updated_at ?? ''
  }
}

function cliRuntimeProvider(runtime: ModelCenterCliRuntimeDto): ModelProviderInstanceDto {
  return {
    id: runtime.provider_instance_id,
    driver: runtime.driver,
    display_name: runtime.display_name,
    connection_kind: 'cli',
    model: runtime.model ?? null,
    has_api_key: false,
    binary_path: runtime.binary_path ?? null,
    home_path: runtime.home_path ?? null,
    server_url: runtime.server_url ?? null,
    launch_args: runtime.launch_args ?? null,
    capabilities: runtime.capabilities,
    enabled: runtime.enabled,
    status: runtime.status,
    status_message: runtime.status_message,
    created_at: '',
    updated_at: ''
  }
}

export function providersFromOverview(overview: ModelCenterOverviewDto | null) {
  if (!overview) return []

  return uniqueProviders([
    ...overview.api_connections.map(apiConnectionProvider),
    ...overview.cli_runtimes.map(cliRuntimeProvider)
  ])
}

function categoryForSupplier(supplier: ModelCenterSupplierDto): ProviderCategory {
  if (supplier.transport_kind === 'cli' || supplier.transport_kind === 'acp') return 'agent-cli'
  if (supplier.transport_kind === 'local_http') return 'local-server'
  if (supplier.driver === 'custom') return 'custom'
  return 'cloud-api'
}

export function providerTemplateFromSupplier(supplier: ModelCenterSupplierDto): ProviderTemplate {
  const presentationAlias = supplierPresentationAliases.get(supplier.driver)
  const presentation = findTemplate(supplier.driver) ?? (presentationAlias ? findTemplate(presentationAlias) : undefined)
  const cliLike = supplier.transport_kind === 'cli' || supplier.transport_kind === 'acp'
  const apiKey = ['api-key', 'api_key'].includes(supplier.credential_kind)
  const local = supplier.transport_kind === 'local_http'

  const supportsStreaming = supplier.capabilities.supports_streaming === true
  const supportsTools = supplier.capabilities.supports_tools === true
  const supportsJsonMode = supplier.capabilities.supports_json_mode === true
  const requiresWorkspace = supplier.capabilities.requires_workspace === true

  return {
    driver: supplier.driver,
    display_name_key: presentation?.display_name_key ?? supplier.display_name,
    summary_key: presentation?.summary_key ?? supplier.summary,
    connection_kind: cliLike ? 'cli' : local ? 'local-server' : apiKey ? 'api-key' : 'public-api',
    category: categoryForSupplier(supplier),
    default_base_url: supplier.default_base_url ?? null,
    default_model: supplier.default_model ?? null,
    capabilities: [
      ...(supportsStreaming ? ['streaming'] : []),
      ...(supportsTools ? ['tool-calls'] : []),
      ...(supportsJsonMode ? ['json-mode'] : []),
      ...(requiresWorkspace ? ['workspace'] : []),
      ...(cliLike ? ['agent', supplier.transport_kind] : ['chat'])
    ],
    brand_color: presentation?.brand_color ?? '#8b949e',
    brand_bg: presentation?.brand_bg ?? 'rgba(139,148,158,0.10)',
    icon: presentation?.icon ?? '',
    fields: {
      base_url: !cliLike,
      model: !cliLike,
      api_key: apiKey,
      binary_path: cliLike,
      home_path: cliLike && requiresWorkspace,
      server_url: cliLike && Boolean(supplier.default_base_url),
      launch_args: cliLike
    },
    placeholders: presentation?.placeholders ?? {}
  }
}

export function bindingForAgent(overview: AgentCenterOverviewDto | null, agentId: string) {
  return overview?.agents.find((agent) => agent.id === agentId)?.runtime_binding ?? null
}

/** Derive a display binding from the formal inherit|route|fixed strategy. */
export function bindingFromModelStrategy(
  agent: { id: string; model_route_purpose?: string | null; model_strategy?: unknown }
): AgentRuntimeBindingDto {
  const strategy = (agent.model_strategy && typeof agent.model_strategy === 'object')
    ? agent.model_strategy as Record<string, unknown>
    : typeof agent.model_strategy === 'string' ? { kind: agent.model_strategy } : null
  const rawKind = strategy && typeof strategy.kind === 'string'
    ? strategy.kind.toLowerCase()
    : strategy && typeof strategy.selection_kind === 'string' ? String(strategy.selection_kind).toLowerCase() : 'inherit'
  const routePurpose = agent.model_route_purpose ?? ''

  if (rawKind === 'fixed') {
    const fixedStrategy = (strategy ?? {}) as Record<string, unknown>
    return {
      selection_kind: 'fixed_model',
      source: 'agent_binding',
      writable: true,
      route_purpose: routePurpose,
      runtime_kind: 'model',
      runtime_id: typeof fixedStrategy.provider_instance_id === 'string' ? fixedStrategy.provider_instance_id : null,
      provider_instance_id: typeof fixedStrategy.provider_instance_id === 'string' ? fixedStrategy.provider_instance_id : null,
      model_id: typeof fixedStrategy.model === 'string'
        ? fixedStrategy.model
        : typeof fixedStrategy.model_id === 'string' ? fixedStrategy.model_id : null,
      model_source: fixedStrategy.model || fixedStrategy.model_id ? 'route_override' : 'unset',
      shared_agent_ids: [],
      warnings: []
    }
  }

  if (rawKind === 'route') {
    const purpose = typeof strategy?.route_purpose === 'string' ? strategy.route_purpose : routePurpose
    return {
      selection_kind: 'route',
      source: 'agent_binding',
      writable: true,
      route_purpose: purpose,
      runtime_kind: 'model',
      runtime_id: null,
      provider_instance_id: null,
      model_id: null,
      model_source: 'route_override',
      shared_agent_ids: [],
      warnings: []
    }
  }

  return {
    selection_kind: 'inherit',
    source: 'agent_binding',
    writable: true,
    route_purpose: routePurpose,
    runtime_kind: 'unresolved',
    runtime_id: null,
    provider_instance_id: null,
    model_id: null,
    model_source: 'unset',
    shared_agent_ids: [],
    warnings: []
  }
}

export function runtimeSourceSummary(binding?: AgentRuntimeBindingDto | null) {
  if (!binding || binding.runtime_kind === 'unresolved') return ''
  const name = binding.provider_display_name ?? binding.runtime_id ?? binding.route_purpose
  return binding.model_id ? `${name} · ${binding.model_id}` : name
}

export function legacyRouteWarning(binding?: AgentRuntimeBindingDto | null) {
  if (!binding || binding.shared_agent_ids.length === 0) return null
  return {
    purpose: binding.route_purpose,
    agent_ids: binding.shared_agent_ids
  }
}

export function modelOptionKey(providerInstanceId: string, modelId: string) {
  return `${providerInstanceId}\u0000${modelId}`
}

// ──────────────────────────────────────────────────────────
// Client-side model center overview aggregation.
// The Gateway BFF (`/api/v1/model-center/overview`) was deleted; the same
// projection is derived here from the versioned Core surfaces.
// ──────────────────────────────────────────────────────────

export interface ModelCenterAggregateInput {
  providers: ModelProviderInstanceDto[];
  templates: ModelProviderTemplateDto[];
  routes: ModelRouteDto[];
  acp_adapters?: AcpAdapterDto[] | null;
  model_readiness?: ModelReadinessReceiptDto | null;
  catalog_readiness?: ModelCatalogReadinessReceiptDto | null;
}

function routeCandidates(route: ModelRouteDto): Array<{ provider_instance_id: string; model?: string | null; position: number }> {
  if (Array.isArray(route.candidates) && route.candidates.length > 0) return route.candidates
  return []
}

type RuntimeKind = 'model' | 'cli' | 'acp';

function classifyProvider(provider: ModelProviderInstanceDto): RuntimeKind {
  const capabilities = new Set(provider.capabilities.map((capability) => capability.toLowerCase()));
  if (capabilities.has('acp')) return 'acp';
  if (provider.connection_kind.toLowerCase() === 'cli' || capabilities.has('cli')) return 'cli';
  return 'model';
}

function normalizeTransportKind(connectionKind: string): string {
  switch (connectionKind.trim().toLowerCase()) {
    case 'http':
    case 'api-key':
    case 'api_key':
    case 'public-api':
      return 'http';
    case 'local-server':
    case 'local_http':
    case 'local-http':
      return 'local_http';
    case 'cli':
      return 'cli';
    default:
      return 'unknown';
  }
}

function normalizeTemplateTransportKind(template: ModelProviderTemplateDto): string {
  const providerFamily = template.provider_family.trim().toLowerCase();
  const driver = template.driver.trim().toLowerCase();
  if (providerFamily === 'local-http' || driver === 'local-http' || driver.startsWith('local-http-')) {
    return 'local_http';
  }
  return normalizeTransportKind(template.connection_kind);
}

function normalizeCredentialKind(credentialKind: string): string {
  switch (credentialKind.trim().toLowerCase()) {
    case 'api-key':
    case 'api_key':
      return 'api_key';
    case 'none':
    case 'public':
      return 'none';
    case 'cli':
      return 'cli';
    default:
      return 'unknown';
  }
}

function inferProviderCredentialKind(provider: ModelProviderInstanceDto): string {
  if (provider.connection_kind.toLowerCase() === 'cli') return 'cli';
  if (provider.capabilities.some((capability) => capability.toLowerCase() === 'no-api-key')) return 'none';
  return provider.has_api_key ? 'api_key' : 'unknown';
}

function templateCapabilities(template: ModelProviderTemplateDto): Record<string, unknown> {
  return {
    supports_streaming: template.capabilities.supports_streaming,
    supports_tools: template.capabilities.supports_tools,
    supports_json_mode: template.capabilities.supports_json_mode,
    supports_system_prompt: template.capabilities.supports_system_prompt,
    requires_workspace: template.capabilities.requires_workspace,
    credential_kind: template.capabilities.credential_kind,
    health_status: template.capabilities.health_status
  };
}

function supplierFromTemplate(template: ModelProviderTemplateDto): ModelCenterSupplierDto {
  return {
    supplier_id: template.driver,
    provider_family: template.provider_family,
    driver: template.driver,
    display_name: template.display_name,
    connection_kind: template.connection_kind,
    transport_kind: normalizeTemplateTransportKind(template),
    credential_kind: normalizeCredentialKind(template.credential_kind),
    summary: template.summary,
    contributor_description: template.contributor_description,
    default_base_url: template.default_base_url ?? null,
    default_model: template.default_model ?? null,
    default_timeout_seconds: template.default_timeout_seconds,
    capabilities: templateCapabilities(template)
  };
}

function sortedUnique(values: string[]): string[] {
  return [...new Set(values)].sort();
}

interface MutableModel extends ModelCenterModelDtoMutable { }

interface ModelCenterModelDtoMutable {
  id: string;
  model_id: string;
  display_name: string;
  provider_instance_id: string;
  provider_display_name: string | null;
  source: string;
  configuration_sources: Array<'provider_default' | 'provider_models' | 'route_override'>;
  is_provider_default: boolean;
  route_purposes: string[];
  enabled: boolean;
  status: string;
}

export function aggregateModelCenterOverview(input: ModelCenterAggregateInput): ModelCenterOverviewDto {
  const providers = input.providers ?? [];
  const routes = input.routes ?? [];
  const templates = input.templates ?? [];
  const templateByDriver = new Map(templates.map((template) => [template.driver.toLowerCase(), template]));
  const routePurposesByProviderId = new Map<string, string[]>();

  for (const route of routes) {
    for (const candidate of routeCandidates(route)) {
      const key = candidate.provider_instance_id.toLowerCase();
      const purposes = routePurposesByProviderId.get(key) ?? [];
      purposes.push(route.purpose);
      routePurposesByProviderId.set(key, purposes);
    }
  }

  const suppliers = templates.map(supplierFromTemplate);

  const apiConnections: ModelCenterApiConnectionDto[] = [];
  const cliRuntimes: ModelCenterCliRuntimeDto[] = [];
  const legacyAcpRuntimes: ModelCenterAcpRuntimeDto[] = [];

  for (const provider of providers) {
    const template = templateByDriver.get(provider.driver.toLowerCase()) ?? null;
    const routePurposes = sortedUnique(routePurposesByProviderId.get(provider.id.toLowerCase()) ?? []);
    const kind = classifyProvider(provider);

    if (kind === 'acp') {
      const runtimeId = `legacy_provider:${provider.id}`;
      legacyAcpRuntimes.push({
        id: runtimeId,
        runtime_id: runtimeId,
        source: 'legacy_provider',
        adapter_id: null,
        provider_instance_id: provider.id,
        extension_id: null,
        driver: provider.driver,
        display_name: provider.display_name,
        command: null,
        binary_path: provider.binary_path ?? null,
        home_path: provider.home_path ?? null,
        capabilities: provider.capabilities,
        enabled: provider.enabled,
        status: provider.status,
        status_message: provider.status_message,
        route_purposes: routePurposes,
        updated_at: provider.updated_at,
        readiness: null
      });
      continue;
    }

    if (kind === 'cli') {
      cliRuntimes.push({
        id: provider.id,
        runtime_id: provider.id,
        provider_instance_id: provider.id,
        source: 'provider_instance',
        driver: provider.driver,
        display_name: provider.display_name,
        binary_path: provider.binary_path ?? null,
        home_path: provider.home_path ?? null,
        server_url: provider.server_url ?? null,
        launch_args: provider.launch_args ?? null,
        model: provider.model ?? null,
        capabilities: provider.capabilities,
        enabled: provider.enabled,
        status: provider.status,
        status_message: provider.status_message,
        route_purposes: routePurposes,
        readiness: null
      });
      continue;
    }

    apiConnections.push({
      id: provider.id,
      provider_instance_id: provider.id,
      provider_family: template?.provider_family ?? null,
      driver: provider.driver,
      display_name: provider.display_name,
      connection_kind: provider.connection_kind,
      transport_kind: template ? normalizeTemplateTransportKind(template) : normalizeTransportKind(provider.connection_kind),
      credential_kind: template ? normalizeCredentialKind(template.credential_kind) : inferProviderCredentialKind(provider),
      base_url: provider.base_url ?? null,
      model: provider.model ?? null,
      models: provider.models ?? [],
      has_api_key: provider.has_api_key,
      server_url: provider.server_url ?? null,
      capabilities: provider.capabilities,
      enabled: provider.enabled,
      status: provider.status,
      status_message: provider.status_message,
      cooldown_until: provider.cooldown_until ?? null,
      created_at: provider.created_at,
      updated_at: provider.updated_at,
      route_purposes: routePurposes,
      readiness: null
    });
  }

  const adapterRuntimes: ModelCenterAcpRuntimeDto[] = (input.acp_adapters ?? []).map((adapter) => ({
    id: `adapter:${adapter.id}`,
    runtime_id: `adapter:${adapter.id}`,
    source: 'adapter',
    adapter_id: adapter.id,
    provider_instance_id: null,
    extension_id: adapter.extension_id,
    driver: null,
    display_name: adapter.name || adapter.id,
    command: adapter.command ?? null,
    binary_path: null,
    home_path: null,
    capabilities: adapter.capabilities,
    enabled: true,
    status: adapter.status || 'unknown',
    status_message: adapter.status_message ?? '',
    route_purposes: [],
    updated_at: adapter.updated_at ?? null,
    readiness: null
  }));

  const diagnostics: CenterDiagnosticDto[] = [];
  if (!input.model_readiness) {
    diagnostics.push({ code: 'MODEL_READINESS_UNAVAILABLE', severity: 'warning', message: 'Model readiness receipt is unavailable; showing configuration only.', source: 'model-readiness', status: null, route_purpose: null, agent_ids: null });
  }

  return {
    capabilities: {
      provider_crud: true,
      model_catalog_mode: 'configured_only',
      model_discovery_refresh: true,
      live_model_discovery: true,
      agent_runtime_binding_write: false,
      acp_adapter_read: true,
      acp_probe: true
    },
    suppliers,
    api_connections: apiConnections,
    models: buildConfiguredModels(providers, routes),
    cli_runtimes: cliRuntimes,
    acp_runtimes: [...adapterRuntimes, ...legacyAcpRuntimes],
    readiness: {
      model: input.model_readiness ?? null,
      catalog: input.catalog_readiness ?? null
    },
    diagnostics
  };
}

function buildConfiguredModels(providers: ModelProviderInstanceDto[], routes: ModelRouteDto[]): ModelCenterModelDtoMutable[] {
  const providersById = new Map(providers.map((provider) => [provider.id.toLowerCase(), provider]));
  const models = new Map<string, MutableModel>();

  const add = (
    providerId: string,
    modelId: string,
    source: 'provider_default' | 'provider_models' | 'route_override',
    routePurpose?: string
  ) => {
    const key = `${providerId.toLowerCase()}\u0000${modelId}`;
    const provider = providersById.get(providerId.toLowerCase()) ?? null;
    const existing = models.get(key);
    if (existing) {
      if (!existing.configuration_sources.includes(source)) existing.configuration_sources.push(source);
      if (routePurpose && !existing.route_purposes.includes(routePurpose)) existing.route_purposes.push(routePurpose);
      existing.is_provider_default ||= source === 'provider_default';
      return;
    }

    models.set(key, {
      id: `${providerId}:${modelId}`,
      model_id: modelId,
      display_name: modelId,
      provider_instance_id: providerId,
      provider_display_name: provider?.display_name ?? null,
      source: 'configured_only',
      configuration_sources: [source],
      is_provider_default: source === 'provider_default',
      route_purposes: routePurpose ? [routePurpose] : [],
      enabled: provider?.enabled ?? false,
      status: provider?.status ?? 'missing_provider'
    });
  };

  for (const provider of providers) {
    if (classifyProvider(provider) !== 'model') continue;
    if (provider.model) add(provider.id, provider.model, 'provider_default');
    for (const modelId of provider.models ?? []) {
      if (modelId && modelId !== provider.model) add(provider.id, modelId, 'provider_models');
    }
  }

  for (const route of routes) {
    for (const candidate of routeCandidates(route)) {
      const provider = providersById.get(candidate.provider_instance_id.toLowerCase()) ?? null;
      if (!provider || classifyProvider(provider) !== 'model') continue;
      const modelId = candidate.model ?? provider.model ?? null;
      if (!modelId) continue;
      add(candidate.provider_instance_id, modelId, candidate.model ? 'route_override' : 'provider_default', route.purpose);
    }
  }

  return [...models.values()]
    .map((model) => ({
      ...model,
      configuration_sources: [...model.configuration_sources].sort(),
      route_purposes: sortedUnique(model.route_purposes)
    }))
    .sort((a, b) => a.id.localeCompare(b.id));
}
