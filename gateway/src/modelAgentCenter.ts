import { proxyJson, type ProxyOptions, type ProxyResult } from './coreClient.js';

export type CoreJsonFetcher = (path: string, options?: ProxyOptions) => Promise<ProxyResult>;

export type TransportKind = 'http' | 'local_http' | 'cli' | 'unknown';
export type CredentialKind = 'api_key' | 'none' | 'cli' | 'unknown';
export type RuntimeKind = 'model' | 'cli' | 'acp' | 'unresolved';

export interface CenterDiagnostic {
  code: string;
  severity: 'warning' | 'error';
  message: string;
  source?: string;
  status?: number;
  route_purpose?: string;
  agent_ids?: string[];
}

export interface CenterCapabilities {
  provider_crud: true;
  model_catalog_mode: 'configured_only';
  model_discovery_refresh: false;
  live_model_discovery: false;
  agent_runtime_binding_write: false;
  acp_adapter_read: boolean;
  acp_probe: boolean;
}

export interface SupplierResource {
  supplier_id: string;
  provider_family: string;
  driver: string;
  display_name: string;
  connection_kind: string;
  transport_kind: TransportKind;
  credential_kind: CredentialKind;
  summary: string;
  contributor_description: string;
  default_base_url: string | null;
  default_model: string | null;
  default_timeout_seconds: number;
  capabilities: Record<string, unknown>;
}

export interface ApiConnectionResource {
  id: string;
  provider_instance_id: string;
  provider_family: string | null;
  driver: string;
  display_name: string;
  connection_kind: string;
  transport_kind: TransportKind;
  credential_kind: CredentialKind;
  base_url: string | null;
  model: string | null;
  has_api_key: boolean;
  server_url: string | null;
  capabilities: string[];
  enabled: boolean;
  status: string;
  status_message: string;
  cooldown_until: string | null;
  created_at: string | null;
  updated_at: string | null;
  route_purposes: string[];
  readiness: Record<string, unknown> | null;
}

export interface ConfiguredModelResource {
  id: string;
  model_id: string;
  display_name: string;
  provider_instance_id: string;
  provider_display_name: string | null;
  source: 'configured_only';
  configuration_sources: Array<'provider_default' | 'route_override'>;
  is_provider_default: boolean;
  route_purposes: string[];
  enabled: boolean;
  status: string;
}

export interface CliRuntimeResource {
  id: string;
  runtime_id: string;
  provider_instance_id: string;
  source: 'provider_instance';
  driver: string;
  display_name: string;
  binary_path: string | null;
  home_path: string | null;
  server_url: string | null;
  launch_args: string | null;
  model: string | null;
  capabilities: string[];
  enabled: boolean;
  status: string;
  status_message: string;
  route_purposes: string[];
  readiness: Record<string, unknown> | null;
}

export interface AcpRuntimeResource {
  id: string;
  runtime_id: string;
  source: 'adapter' | 'legacy_provider';
  adapter_id: string | null;
  provider_instance_id: string | null;
  extension_id: string | null;
  driver: string | null;
  display_name: string;
  command: string | null;
  binary_path: string | null;
  home_path: string | null;
  capabilities: string[];
  enabled: boolean;
  status: string;
  status_message: string;
  route_purposes: string[];
  updated_at: string | null;
  readiness: Record<string, unknown> | null;
}

export interface ModelCenterReadiness {
  model: unknown | null;
  catalog: unknown | null;
}

export interface ModelCenterOverview {
  suppliers: SupplierResource[];
  api_connections: ApiConnectionResource[];
  models: ConfiguredModelResource[];
  cli_runtimes: CliRuntimeResource[];
  acp_runtimes: AcpRuntimeResource[];
  readiness: ModelCenterReadiness;
  capabilities: CenterCapabilities;
  diagnostics: CenterDiagnostic[];
}

export interface RuntimeBindingWarning {
  code: 'LEGACY_SHARED_ROUTE';
  message: string;
  shared_agent_ids: string[];
}

export interface DerivedAgentRuntimeBinding {
  selection_kind: 'inherit';
  source: 'legacy_route';
  writable: false;
  route_purpose: string;
  runtime_kind: RuntimeKind;
  runtime_id: string | null;
  provider_instance_id: string | null;
  provider_display_name: string | null;
  model_id: string | null;
  model_source: 'route_override' | 'provider_default' | 'unset';
  shared_agent_ids: string[];
  warnings: RuntimeBindingWarning[];
}

export interface AgentWithRuntimeBinding extends Record<string, unknown> {
  id: string;
  name: string;
  layer: string;
  agent_type: string;
  mode: string;
  description: string;
  model_route_purpose: string;
  allowed_tools: string[];
  capabilities: string[];
  system_prompt: string | null;
  enabled: boolean;
  is_built_in: boolean;
  updated_at: string | null;
  runtime_binding: DerivedAgentRuntimeBinding;
}

export interface AgentRuntimeSources {
  providers: ApiConnectionResource[];
  models: ConfiguredModelResource[];
  cli_runtimes: CliRuntimeResource[];
  acp_runtimes: AcpRuntimeResource[];
}

export interface AgentCenterOverview {
  agents: AgentWithRuntimeBinding[];
  modes: Array<Record<string, unknown>>;
  candidates: Array<Record<string, unknown>>;
  runtime_sources: AgentRuntimeSources;
  readiness: ModelCenterReadiness;
  capabilities: CenterCapabilities;
  diagnostics: CenterDiagnostic[];
}

export type AgentRuntimeBindingInput =
  | { selection_kind: 'inherit' }
  | { selection_kind: 'fixed_model'; provider_instance_id: string; model_id: string }
  | { selection_kind: 'provider_auto'; provider_instance_id: string }
  | { selection_kind: 'cli'; runtime_id: string }
  | { selection_kind: 'acp'; runtime_id: string };

export type AgentRuntimeBindingValidation =
  | { ok: true; value: AgentRuntimeBindingInput }
  | { ok: false; errors: string[] };

export function agentRuntimeBindingWriteResult(agentId: unknown, input: unknown): ProxyResult {
  const normalizedAgentId = requiredString(agentId);
  if (!normalizedAgentId) {
    return {
      status: 400,
      data: {
        code: 'AGENT_RUNTIME_BINDING_INVALID',
        message: 'agentId must be a non-empty string.',
        details: ['agentId must be a non-empty string.']
      }
    };
  }

  const validation = validateAgentRuntimeBindingInput(input);
  if (!validation.ok) {
    return {
      status: 400,
      data: {
        code: 'AGENT_RUNTIME_BINDING_INVALID',
        message: 'The runtime binding request is invalid.',
        details: validation.errors
      }
    };
  }

  return {
    status: 501,
    data: {
      code: 'AGENT_RUNTIME_BINDING_UNSUPPORTED',
      message: 'Core does not yet support persistent per-agent runtime bindings.',
      agent_id: normalizedAgentId,
      selection_kind: validation.value.selection_kind,
      capabilities: {
        agent_runtime_binding_write: false
      }
    }
  };
}

export function modelDiscoveryRefreshResult(providerInstanceId: unknown): ProxyResult {
  const normalizedProviderId = requiredString(providerInstanceId);
  if (!normalizedProviderId) {
    return {
      status: 400,
      data: {
        code: 'MODEL_DISCOVERY_REQUEST_INVALID',
        message: 'Provider instance id must be a non-empty string.'
      }
    };
  }

  return {
    status: 501,
    data: {
      code: 'MODEL_DISCOVERY_UNSUPPORTED',
      message: 'Core does not yet support live provider model discovery.',
      provider_instance_id: normalizedProviderId,
      capabilities: {
        model_discovery_refresh: false,
        model_catalog_mode: 'configured_only'
      }
    }
  };
}

export interface ModelCenterAggregateInput {
  templates: unknown;
  providers: unknown;
  routes: unknown;
  model_readiness?: unknown | null;
  catalog_readiness?: unknown | null;
  acp_adapters?: unknown | null;
  unavailable_capabilities?: UnavailableCapability[];
}

export interface AgentCenterAggregateInput extends ModelCenterAggregateInput {
  agents: unknown;
  modes?: unknown | null;
  candidates?: unknown | null;
}

interface UnavailableCapability {
  source: string;
  status: number;
}

interface CoreTemplate {
  provider_family: string;
  driver: string;
  display_name: string;
  connection_kind: string;
  credential_kind: string;
  summary: string;
  contributor_description: string;
  default_base_url: string | null;
  default_model: string | null;
  default_timeout_seconds: number;
  capabilities: Record<string, unknown>;
}

interface CoreProvider {
  id: string;
  driver: string;
  display_name: string;
  connection_kind: string;
  base_url: string | null;
  model: string | null;
  has_api_key: boolean;
  binary_path: string | null;
  home_path: string | null;
  server_url: string | null;
  launch_args: string | null;
  capabilities: string[];
  enabled: boolean;
  status: string;
  status_message: string;
  cooldown_until: string | null;
  created_at: string | null;
  updated_at: string | null;
}

interface CoreRoute {
  purpose: string;
  provider_instance_id: string;
  model: string | null;
  updated_at: string | null;
}

interface CoreAgent {
  id: string;
  name: string;
  layer: string;
  agent_type: string;
  mode: string;
  description: string;
  model_route_purpose: string;
  allowed_tools: string[];
  capabilities: string[];
  system_prompt: string | null;
  enabled: boolean;
  is_built_in: boolean;
  updated_at: string | null;
}

interface ModelCenterSnapshot {
  overview: ModelCenterOverview;
  providers: CoreProvider[];
  routes: CoreRoute[];
}

const OPTIONAL_STATUS_CODES = new Set([404, 501]);
const SECRET_KEYS = new Set([
  'api_key',
  'apikey',
  'access_token',
  'refresh_token',
  'authorization',
  'client_secret',
  'secret',
  'password'
]);

export function aggregateModelCenter(input: ModelCenterAggregateInput): ModelCenterOverview {
  return buildModelCenterSnapshot(input).overview;
}

export function aggregateAgentCenter(input: AgentCenterAggregateInput): AgentCenterOverview {
  const snapshot = buildModelCenterSnapshot(input);
  const agents = records(input.agents).map(toAgent).filter((agent): agent is CoreAgent => agent !== null);
  const routesByPurpose = new Map(snapshot.routes.map((route) => [route.purpose.toLowerCase(), route]));
  const providersById = new Map(snapshot.providers.map((provider) => [provider.id.toLowerCase(), provider]));
  const agentIdsByPurpose = new Map<string, string[]>();

  for (const agent of agents) {
    const key = agent.model_route_purpose.toLowerCase();
    const ids = agentIdsByPurpose.get(key) ?? [];
    ids.push(agent.id);
    agentIdsByPurpose.set(key, ids);
  }

  const sharedRouteDiagnostics: CenterDiagnostic[] = [];
  for (const [purpose, agentIds] of agentIdsByPurpose) {
    if (agentIds.length < 2) continue;
    sharedRouteDiagnostics.push({
      code: 'LEGACY_SHARED_ROUTE',
      severity: 'warning',
      message: `Route purpose '${purpose}' is shared by ${agentIds.length} agents; the inherited binding is read-only.`,
      source: 'model-routes',
      route_purpose: purpose,
      agent_ids: [...agentIds]
    });
  }

  const mappedAgents = agents.map((agent): AgentWithRuntimeBinding => {
    const route = routesByPurpose.get(agent.model_route_purpose.toLowerCase()) ?? null;
    const provider = route
      ? providersById.get(route.provider_instance_id.toLowerCase()) ?? null
      : null;
    const sharedAgentIds = (agentIdsByPurpose.get(agent.model_route_purpose.toLowerCase()) ?? [])
      .filter((id) => id !== agent.id);
    const warnings: RuntimeBindingWarning[] = sharedAgentIds.length > 0
      ? [{
          code: 'LEGACY_SHARED_ROUTE',
          message: 'This Core route purpose is shared with other agents and cannot be edited as an agent-specific binding.',
          shared_agent_ids: sharedAgentIds
        }]
      : [];

    const runtimeKind = provider ? classifyProvider(provider) : 'unresolved';
    const modelSource = route?.model
      ? 'route_override'
      : provider?.model
        ? 'provider_default'
        : 'unset';
    const modelId = route?.model ?? provider?.model ?? null;

    return {
      ...agent,
      runtime_binding: {
        selection_kind: 'inherit',
        source: 'legacy_route',
        writable: false,
        route_purpose: agent.model_route_purpose,
        runtime_kind: runtimeKind,
        runtime_id: runtimeKind === 'cli'
          ? provider?.id ?? null
          : runtimeKind === 'acp' && provider
            ? `legacy_provider:${provider.id}`
            : null,
        provider_instance_id: provider?.id ?? route?.provider_instance_id ?? null,
        provider_display_name: provider?.display_name ?? null,
        model_id: runtimeKind === 'model' ? modelId : null,
        model_source: runtimeKind === 'model' ? modelSource : 'unset',
        shared_agent_ids: sharedAgentIds,
        warnings
      }
    };
  });

  return {
    agents: mappedAgents,
    modes: sanitizeRecordArray(input.modes),
    candidates: sanitizeRecordArray(input.candidates),
    runtime_sources: {
      providers: snapshot.overview.api_connections,
      models: snapshot.overview.models,
      cli_runtimes: snapshot.overview.cli_runtimes,
      acp_runtimes: snapshot.overview.acp_runtimes
    },
    readiness: snapshot.overview.readiness,
    capabilities: snapshot.overview.capabilities,
    diagnostics: [...snapshot.overview.diagnostics, ...sharedRouteDiagnostics]
  };
}

export function validateAgentRuntimeBindingInput(input: unknown): AgentRuntimeBindingValidation {
  if (!isRecord(input)) {
    return { ok: false, errors: ['Request body must be a JSON object.'] };
  }

  const selectionKind = requiredString(input.selection_kind);
  const allowedKinds = ['inherit', 'fixed_model', 'provider_auto', 'cli', 'acp'] as const;
  if (!selectionKind || !allowedKinds.includes(selectionKind as typeof allowedKinds[number])) {
    return {
      ok: false,
      errors: [`selection_kind must be one of: ${allowedKinds.join(', ')}.`]
    };
  }

  const allowedKeys: Record<string, string[]> = {
    inherit: ['selection_kind'],
    fixed_model: ['selection_kind', 'provider_instance_id', 'model_id'],
    provider_auto: ['selection_kind', 'provider_instance_id'],
    cli: ['selection_kind', 'runtime_id'],
    acp: ['selection_kind', 'runtime_id']
  };
  const unexpected = Object.keys(input).filter((key) => !allowedKeys[selectionKind].includes(key));
  const errors = unexpected.map((key) => `Field '${key}' is not valid for selection_kind '${selectionKind}'.`);

  if (selectionKind === 'inherit') {
    return errors.length > 0 ? { ok: false, errors } : { ok: true, value: { selection_kind: 'inherit' } };
  }

  if (selectionKind === 'fixed_model') {
    const providerId = requiredString(input.provider_instance_id);
    const modelId = requiredString(input.model_id);
    if (!providerId) errors.push('provider_instance_id is required for fixed_model.');
    if (!modelId) errors.push('model_id is required for fixed_model.');
    return errors.length > 0
      ? { ok: false, errors }
      : {
          ok: true,
          value: { selection_kind: 'fixed_model', provider_instance_id: providerId!, model_id: modelId! }
        };
  }

  if (selectionKind === 'provider_auto') {
    const providerId = requiredString(input.provider_instance_id);
    if (!providerId) errors.push('provider_instance_id is required for provider_auto.');
    return errors.length > 0
      ? { ok: false, errors }
      : { ok: true, value: { selection_kind: 'provider_auto', provider_instance_id: providerId! } };
  }

  const runtimeId = requiredString(input.runtime_id);
  if (!runtimeId) errors.push(`runtime_id is required for ${selectionKind}.`);
  return errors.length > 0
    ? { ok: false, errors }
    : { ok: true, value: { selection_kind: selectionKind, runtime_id: runtimeId! } as AgentRuntimeBindingInput };
}

export async function loadModelCenterOverview(fetchCore: CoreJsonFetcher = proxyJson): Promise<ProxyResult> {
  const loaded = await loadCenterInputs(fetchCore, false);
  if (!loaded.ok) return loaded.result;

  return {
    status: 200,
    data: aggregateModelCenter(loaded.input)
  };
}

export async function loadAgentCenterOverview(fetchCore: CoreJsonFetcher = proxyJson): Promise<ProxyResult> {
  const loaded = await loadCenterInputs(fetchCore, true);
  if (!loaded.ok) return loaded.result;

  return {
    status: 200,
    data: aggregateAgentCenter(loaded.input as AgentCenterAggregateInput)
  };
}

function buildModelCenterSnapshot(input: ModelCenterAggregateInput): ModelCenterSnapshot {
  const templates = records(input.templates).map(toTemplate).filter((item): item is CoreTemplate => item !== null);
  const providers = records(input.providers).map(toProvider).filter((item): item is CoreProvider => item !== null);
  const routes = records(input.routes).map(toRoute).filter((item): item is CoreRoute => item !== null);
  const templateByDriver = new Map(templates.map((template) => [template.driver.toLowerCase(), template]));
  const providerReadinessById = readinessProviders(input.model_readiness);
  const routePurposesByProviderId = new Map<string, string[]>();

  for (const route of routes) {
    const key = route.provider_instance_id.toLowerCase();
    const purposes = routePurposesByProviderId.get(key) ?? [];
    purposes.push(route.purpose);
    routePurposesByProviderId.set(key, purposes);
  }

  const suppliers = templates.map((template): SupplierResource => ({
    supplier_id: template.driver,
    provider_family: template.provider_family,
    driver: template.driver,
    display_name: template.display_name,
    connection_kind: template.connection_kind,
    transport_kind: normalizeTemplateTransportKind(template),
    credential_kind: normalizeCredentialKind(template.credential_kind),
    summary: template.summary,
    contributor_description: template.contributor_description,
    default_base_url: template.default_base_url,
    default_model: template.default_model,
    default_timeout_seconds: template.default_timeout_seconds,
    capabilities: sanitizeRecord(template.capabilities)
  }));

  const apiConnections: ApiConnectionResource[] = [];
  const cliRuntimes: CliRuntimeResource[] = [];
  const legacyAcpRuntimes: AcpRuntimeResource[] = [];

  for (const provider of providers) {
    const template = templateByDriver.get(provider.driver.toLowerCase()) ?? null;
    const routePurposes = sortedUnique(routePurposesByProviderId.get(provider.id.toLowerCase()) ?? []);
    const readiness = providerReadinessById.get(provider.id.toLowerCase()) ?? null;
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
        binary_path: provider.binary_path,
        home_path: provider.home_path,
        capabilities: provider.capabilities,
        enabled: provider.enabled,
        status: provider.status,
        status_message: provider.status_message,
        route_purposes: routePurposes,
        updated_at: provider.updated_at,
        readiness
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
        binary_path: provider.binary_path,
        home_path: provider.home_path,
        server_url: provider.server_url,
        launch_args: provider.launch_args,
        model: provider.model,
        capabilities: provider.capabilities,
        enabled: provider.enabled,
        status: provider.status,
        status_message: provider.status_message,
        route_purposes: routePurposes,
        readiness
      });
      continue;
    }

    const credentialKind = template
      ? normalizeCredentialKind(template.credential_kind)
      : inferProviderCredentialKind(provider);
    apiConnections.push({
      id: provider.id,
      provider_instance_id: provider.id,
      provider_family: template?.provider_family ?? null,
      driver: provider.driver,
      display_name: provider.display_name,
      connection_kind: provider.connection_kind,
      transport_kind: template
        ? normalizeTemplateTransportKind(template)
        : normalizeTransportKind(provider.connection_kind),
      credential_kind: credentialKind,
      base_url: provider.base_url,
      model: provider.model,
      has_api_key: provider.has_api_key,
      server_url: provider.server_url,
      capabilities: provider.capabilities,
      enabled: provider.enabled,
      status: provider.status,
      status_message: provider.status_message,
      cooldown_until: provider.cooldown_until,
      created_at: provider.created_at,
      updated_at: provider.updated_at,
      route_purposes: routePurposes,
      readiness
    });
  }

  const adapterRuntimes = records(input.acp_adapters)
    .map(toAcpRuntime)
    .filter((adapter): adapter is AcpRuntimeResource => adapter !== null);
  const unavailable = input.unavailable_capabilities ?? [];
  const acpAvailable = !unavailable.some((item) => item.source === 'acp_adapters');
  const diagnostics = unavailable.map(unavailableDiagnostic);

  return {
    providers,
    routes,
    overview: {
      suppliers,
      api_connections: apiConnections,
      models: buildConfiguredModels(providers, routes),
      cli_runtimes: cliRuntimes,
      acp_runtimes: [...adapterRuntimes, ...legacyAcpRuntimes],
      readiness: {
        model: sanitizeUnknown(input.model_readiness ?? null),
        catalog: sanitizeUnknown(input.catalog_readiness ?? null)
      },
      capabilities: {
        provider_crud: true,
        model_catalog_mode: 'configured_only',
        model_discovery_refresh: false,
        live_model_discovery: false,
        agent_runtime_binding_write: false,
        acp_adapter_read: acpAvailable,
        acp_probe: acpAvailable
      },
      diagnostics
    }
  };
}

function buildConfiguredModels(providers: CoreProvider[], routes: CoreRoute[]): ConfiguredModelResource[] {
  interface MutableModel extends ConfiguredModelResource {
    configuration_sources: Array<'provider_default' | 'route_override'>;
  }

  const providersById = new Map(providers.map((provider) => [provider.id.toLowerCase(), provider]));
  const models = new Map<string, MutableModel>();

  const add = (
    providerId: string,
    modelId: string,
    source: 'provider_default' | 'route_override',
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
  }

  for (const route of routes) {
    const provider = providersById.get(route.provider_instance_id.toLowerCase()) ?? null;
    if (!provider || classifyProvider(provider) !== 'model') continue;
    const modelId = route.model ?? provider?.model ?? null;
    if (!modelId) continue;
    add(route.provider_instance_id, modelId, route.model ? 'route_override' : 'provider_default', route.purpose);
  }

  return [...models.values()]
    .map((model) => ({
      ...model,
      configuration_sources: [...model.configuration_sources].sort(),
      route_purposes: sortedUnique(model.route_purposes)
    }))
    .sort((left, right) => left.provider_display_name?.localeCompare(right.provider_display_name ?? '')
      || left.model_id.localeCompare(right.model_id));
}

function classifyProvider(provider: CoreProvider): RuntimeKind {
  const capabilities = new Set(provider.capabilities.map((capability) => capability.toLowerCase()));
  if (capabilities.has('acp')) return 'acp';
  if (provider.connection_kind.toLowerCase() === 'cli' || capabilities.has('cli')) return 'cli';
  return 'model';
}

function normalizeTransportKind(connectionKind: string): TransportKind {
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

function normalizeTemplateTransportKind(template: CoreTemplate): TransportKind {
  const providerFamily = template.provider_family.trim().toLowerCase();
  const driver = template.driver.trim().toLowerCase();
  if (providerFamily === 'local-http' || driver === 'local-http' || driver.startsWith('local-http-')) {
    return 'local_http';
  }
  return normalizeTransportKind(template.connection_kind);
}

function normalizeCredentialKind(credentialKind: string): CredentialKind {
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

function inferProviderCredentialKind(provider: CoreProvider): CredentialKind {
  if (provider.connection_kind.toLowerCase() === 'cli') return 'cli';
  if (provider.capabilities.some((capability) => capability.toLowerCase() === 'no-api-key')) return 'none';
  return provider.has_api_key ? 'api_key' : 'unknown';
}

async function loadCenterInputs(
  fetchCore: CoreJsonFetcher,
  includeAgents: boolean
): Promise<
  | { ok: true; input: ModelCenterAggregateInput | AgentCenterAggregateInput }
  | { ok: false; result: ProxyResult }
> {
  const requiredPaths = [
    ['templates', '/api/v1/model-provider-templates'],
    ['providers', '/api/v1/model-providers'],
    ['routes', '/api/v1/model-routes']
  ] as const;
  const optionalPaths = [
    ['model_readiness', '/api/v1/model-readiness', 'model_readiness'],
    ['catalog_readiness', '/api/v1/model-catalog-readiness', 'model_catalog_readiness'],
    ['acp_adapters', '/api/v1/acp/adapters', 'acp_adapters']
  ] as const;
  const agentRequiredPaths = includeAgents ? [['agents', '/api/v1/agents'] as const] : [];
  const agentOptionalPaths = includeAgents
    ? [
        ['modes', '/api/v1/agent-modes', 'agent_modes'] as const,
        ['candidates', '/api/v1/agent-candidates', 'agent_candidates'] as const
      ]
    : [];
  const calls = [
    ...requiredPaths.map(async ([key, path]) => ({ key, path, required: true as const, result: await fetchCore(path) })),
    ...agentRequiredPaths.map(async ([key, path]) => ({ key, path, required: true as const, result: await fetchCore(path) })),
    ...optionalPaths.map(async ([key, path, source]) => ({ key, path, source, required: false as const, result: await fetchCore(path) })),
    ...agentOptionalPaths.map(async ([key, path, source]) => ({ key, path, source, required: false as const, result: await fetchCore(path) }))
  ];
  const results = await Promise.all(calls);

  const gatewayFailure = results.find((item) => item.result.status === 502);
  if (gatewayFailure) return { ok: false, result: sanitizeProxyResult(gatewayFailure.result) };

  const values: Record<string, unknown> = {};
  const unavailableCapabilities: UnavailableCapability[] = [];
  for (const item of results) {
    const { result } = item;
    if (result.status >= 200 && result.status < 300) {
      values[item.key] = result.data;
      continue;
    }

    if (!item.required && OPTIONAL_STATUS_CODES.has(result.status)) {
      unavailableCapabilities.push({ source: item.source, status: result.status });
      values[item.key] = null;
      continue;
    }

    return { ok: false, result: sanitizeProxyResult(result) };
  }

  const base: ModelCenterAggregateInput = {
    templates: values.templates,
    providers: values.providers,
    routes: values.routes,
    model_readiness: values.model_readiness,
    catalog_readiness: values.catalog_readiness,
    acp_adapters: values.acp_adapters,
    unavailable_capabilities: unavailableCapabilities
  };

  if (!includeAgents) return { ok: true, input: base };
  return {
    ok: true,
    input: {
      ...base,
      agents: values.agents,
      modes: values.modes,
      candidates: values.candidates
    }
  };
}

function unavailableDiagnostic(item: UnavailableCapability): CenterDiagnostic {
  return {
    code: 'CORE_CAPABILITY_UNAVAILABLE',
    severity: 'warning',
    message: `Optional Core capability '${item.source}' is unavailable; the overview contains the remaining data.`,
    source: item.source,
    status: item.status
  };
}

function readinessProviders(value: unknown): Map<string, Record<string, unknown>> {
  if (!isRecord(value)) return new Map();
  const mapped = new Map<string, Record<string, unknown>>();
  for (const item of records(value.providers)) {
    const providerId = requiredString(item.provider_instance_id);
    if (providerId) mapped.set(providerId.toLowerCase(), sanitizeRecord(item));
  }
  return mapped;
}

function toTemplate(value: Record<string, unknown>): CoreTemplate | null {
  const driver = requiredString(value.driver);
  if (!driver) return null;
  return {
    provider_family: requiredString(value.provider_family) ?? 'unknown',
    driver,
    display_name: requiredString(value.display_name) ?? driver,
    connection_kind: requiredString(value.connection_kind) ?? 'unknown',
    credential_kind: requiredString(value.credential_kind) ?? 'unknown',
    summary: requiredString(value.summary) ?? '',
    contributor_description: requiredString(value.contributor_description) ?? '',
    default_base_url: optionalString(value.default_base_url),
    default_model: optionalString(value.default_model),
    default_timeout_seconds: finiteNumber(value.default_timeout_seconds) ?? 0,
    capabilities: isRecord(value.capabilities) ? sanitizeRecord(value.capabilities) : {}
  };
}

function toProvider(value: Record<string, unknown>): CoreProvider | null {
  const id = requiredString(value.id);
  if (!id) return null;
  const driver = requiredString(value.driver) ?? 'unknown';
  return {
    id,
    driver,
    display_name: requiredString(value.display_name) ?? driver,
    connection_kind: requiredString(value.connection_kind) ?? 'unknown',
    base_url: optionalString(value.base_url),
    model: optionalString(value.model),
    has_api_key: value.has_api_key === true,
    binary_path: optionalString(value.binary_path),
    home_path: optionalString(value.home_path),
    server_url: optionalString(value.server_url),
    launch_args: optionalString(value.launch_args),
    capabilities: stringArray(value.capabilities),
    enabled: value.enabled !== false,
    status: requiredString(value.status) ?? 'unknown',
    status_message: requiredString(value.status_message) ?? '',
    cooldown_until: optionalString(value.cooldown_until),
    created_at: optionalString(value.created_at),
    updated_at: optionalString(value.updated_at)
  };
}

function toRoute(value: Record<string, unknown>): CoreRoute | null {
  const purpose = requiredString(value.purpose);
  const providerId = requiredString(value.provider_instance_id);
  if (!purpose || !providerId) return null;
  return {
    purpose,
    provider_instance_id: providerId,
    model: optionalString(value.model),
    updated_at: optionalString(value.updated_at)
  };
}

function toAgent(value: Record<string, unknown>): CoreAgent | null {
  const id = requiredString(value.id);
  if (!id) return null;
  return {
    id,
    name: requiredString(value.name) ?? id,
    layer: requiredString(value.layer) ?? 'unknown',
    agent_type: requiredString(value.agent_type) ?? 'unknown',
    mode: requiredString(value.mode) ?? 'unknown',
    description: requiredString(value.description) ?? '',
    model_route_purpose: requiredString(value.model_route_purpose) ?? '',
    allowed_tools: stringArray(value.allowed_tools),
    capabilities: stringArray(value.capabilities),
    system_prompt: optionalString(value.system_prompt),
    enabled: value.enabled !== false,
    is_built_in: value.is_built_in === true,
    updated_at: optionalString(value.updated_at)
  };
}

function toAcpRuntime(value: Record<string, unknown>): AcpRuntimeResource | null {
  const id = requiredString(value.id);
  if (!id) return null;
  const runtimeId = `adapter:${id}`;
  return {
    id: runtimeId,
    runtime_id: runtimeId,
    source: 'adapter',
    adapter_id: id,
    provider_instance_id: null,
    extension_id: optionalString(value.extension_id),
    driver: null,
    display_name: requiredString(value.name) ?? id,
    command: optionalString(value.command),
    binary_path: null,
    home_path: null,
    capabilities: stringArray(value.capabilities),
    enabled: true,
    status: requiredString(value.status) ?? 'unknown',
    status_message: requiredString(value.status_message) ?? '',
    route_purposes: [],
    updated_at: optionalString(value.updated_at),
    readiness: null
  };
}

function sanitizeRecordArray(value: unknown): Array<Record<string, unknown>> {
  return records(value).map(sanitizeRecord);
}

function sanitizeProxyResult(result: ProxyResult): ProxyResult {
  return {
    status: result.status,
    data: sanitizeUnknown(result.data)
  };
}

function sanitizeUnknown(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sanitizeUnknown);
  if (!isRecord(value)) return value;
  return sanitizeRecord(value);
}

function sanitizeRecord(value: Record<string, unknown>): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const [key, nested] of Object.entries(value)) {
    if (SECRET_KEYS.has(key.toLowerCase())) continue;
    result[key] = sanitizeUnknown(nested);
  }
  return result;
}

function records(value: unknown): Array<Record<string, unknown>> {
  return Array.isArray(value) ? value.filter(isRecord) : [];
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function requiredString(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
}

function optionalString(value: unknown): string | null {
  return requiredString(value);
}

function stringArray(value: unknown): string[] {
  return Array.isArray(value)
    ? value.map(requiredString).filter((item): item is string => item !== null)
    : [];
}

function finiteNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function sortedUnique(values: string[]): string[] {
  return [...new Set(values)].sort((left, right) => left.localeCompare(right));
}
