import type {
  AcpAdapterDto,
  AgentCenterOverviewDto,
  AgentModelBindingDto,
  AgentRuntimeBindingDto,
  CenterDiagnosticDto,
  ModelCatalogReadinessReceiptDto,
  ModelCenterAcpRuntimeDto,
  ModelCenterApiConnectionDto,
  ModelCenterCliRuntimeDto,
  ModelCenterOverviewDto,
  ModelCenterSupplierDto,
  ModelCenterTuiRuntimeDto,
  ModelProviderInstanceDto,
  ModelProviderTemplateDto,
  ModelReadinessReceiptDto,
  ModelResolutionPreviewDto,
  ModelRouteDto
} from './api'
import { findTemplate, type ProviderCategory, type ProviderTemplate } from './providerTemplates'
import i18n from './i18n'

export type ModelCenterSection = 'suppliers' | 'api' | 'models' | 'routes' | 'cli' | 'tui' | 'acp'

/**
 * The three channels Core can drive an external harness over. This is a different question from
 * `connection_kind` (a local process or an HTTP API) and from `protocol` (which dialect is spoken):
 * a channel says how the process is reached, so it is what the model center groups by.
 */
export type HarnessChannel = 'cli' | 'tui' | 'acp'

const CHANNEL_BY_PROTOCOL: Record<string, HarnessChannel> = {
  'headless-cli': 'cli',
  tui: 'tui',
  acp: 'acp'
}

/**
 * Channel for a wire protocol, or `null` when the row is not a harness channel at all — every HTTP
 * API protocol and `opencode-serve` (reached over its own local server, not over a channel) answer
 * null and keep grouping by `connection_kind`.
 */
export function channelForProtocol(protocol?: string | null): HarnessChannel | null {
  return CHANNEL_BY_PROTOCOL[(protocol ?? '').trim().toLowerCase()] ?? null
}

/** Validate a stored channel value. Anything else is not a channel, rather than a guess. */
export function asChannel(value?: string | null): HarnessChannel | null {
  const normalized = value?.trim().toLowerCase()
  return normalized === 'cli' || normalized === 'tui' || normalized === 'acp' ? normalized : null
}

const CHANNEL_LABEL_KEYS: Record<HarnessChannel, string> = {
  cli: 'settings.channelCli',
  tui: 'settings.channelTui',
  acp: 'settings.channelAcp'
}

const PROTOCOL_LABEL_KEYS: Record<string, string> = {
  'openai-chat': 'settings.protocolOpenaiChat',
  'openai-responses': 'settings.protocolOpenaiResponses',
  'anthropic-messages': 'settings.protocolAnthropicMessages',
  acp: 'settings.protocolAcp',
  'opencode-serve': 'settings.protocolOpencodeServe',
  'headless-cli': 'settings.protocolHeadlessCli',
  tui: 'settings.protocolTui'
}

export function protocolLabelKey(protocol?: string | null): string {
  return PROTOCOL_LABEL_KEYS[(protocol ?? '').trim().toLowerCase()] ?? 'settings.protocolUnknown'
}

/**
 * How to name one connection affordance Core reported for a harness. A row without a channel is the
 * harness's own local HTTP server, so it is named by protocol — labelling it "CLI" would offer the
 * same shape twice under two different words, which is the confusion the channel axis ends.
 */
export function discoveryAffordanceLabelKey(fact: { channel?: string | null, protocol?: string | null }): string {
  const channel = asChannel(fact.channel)
  return channel ? CHANNEL_LABEL_KEYS[channel] : protocolLabelKey(fact.protocol)
}

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
    protocol: connection.protocol,
    display_name: connection.display_name,
    connection_kind: connection.connection_kind,
    base_url: connection.base_url ?? null,
    model: connection.model ?? null,
    models: connection.models ?? [],
    model_parameters: connection.model_parameters ?? {},
    has_api_key: connection.has_api_key,
    server_url: connection.server_url ?? null,
    capabilities: connection.capabilities,
    enabled: connection.enabled,
    status: connection.status,
    status_message: connection.status_message,
    cooldown_until: connection.cooldown_until ?? null,
    // Revision is the If-Match token for every provider write; dropping it
    // makes Core reject the save with 428 precondition_required.
    revision: connection.revision ?? undefined,
    created_at: connection.created_at ?? '',
    updated_at: connection.updated_at ?? ''
  }
}

function cliRuntimeProvider(runtime: ModelCenterCliRuntimeDto): ModelProviderInstanceDto {
  return {
    id: runtime.provider_instance_id,
    driver: runtime.driver,
    // Protocol and channel ride back out so a row classified into the harness section stays there
    // after the round trip; without them it degrades to the connection_kind fallback, which is the
    // same label for every harness and cannot tell `acp` from `opencode serve`.
    protocol: runtime.protocol ?? null,
    channel: runtime.channel ?? null,
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
    revision: runtime.revision ?? undefined,
    created_at: '',
    updated_at: ''
  }
}

function tuiRuntimeProvider(runtime: ModelCenterTuiRuntimeDto): ModelProviderInstanceDto {
  return {
    id: runtime.provider_instance_id,
    driver: runtime.driver,
    protocol: runtime.protocol ?? 'tui',
    channel: 'tui',
    display_name: runtime.display_name,
    connection_kind: 'cli',
    base_url: null,
    model: null,
    models: [],
    has_api_key: false,
    binary_path: runtime.binary_path ?? null,
    home_path: runtime.home_path ?? null,
    launch_args: runtime.launch_args ?? null,
    capabilities: runtime.capabilities,
    enabled: runtime.enabled,
    status: runtime.status,
    status_message: runtime.status_message,
    revision: runtime.revision ?? undefined,
    created_at: '',
    updated_at: runtime.updated_at ?? ''
  }
}

function acpRuntimeProvider(runtime: ModelCenterAcpRuntimeDto): ModelProviderInstanceDto | null {
  // Adapter rows are not provider instances at all (no provider_instance_id), so projecting them
  // would invent a provider id out of an adapter id and hand back a `revision` that no row has.
  if (!runtime.provider_instance_id) return null
  return {
    id: runtime.provider_instance_id,
    driver: runtime.driver ?? '',
    protocol: runtime.protocol ?? 'acp',
    channel: 'acp',
    display_name: runtime.display_name,
    connection_kind: 'cli',
    base_url: null,
    model: null,
    models: [],
    has_api_key: false,
    binary_path: runtime.binary_path ?? null,
    home_path: runtime.home_path ?? null,
    capabilities: runtime.capabilities,
    enabled: runtime.enabled,
    status: runtime.status,
    status_message: runtime.status_message,
    revision: runtime.revision ?? undefined,
    created_at: '',
    updated_at: runtime.updated_at ?? ''
  }
}

export function providersFromOverview(overview: ModelCenterOverviewDto | null) {
  if (!overview) return []

  return uniqueProviders([
    ...overview.api_connections.map(apiConnectionProvider),
    ...overview.cli_runtimes.map(cliRuntimeProvider),
    ...overview.tui_runtimes.map(tuiRuntimeProvider),
    ...overview.acp_runtimes.map(acpRuntimeProvider).filter((provider): provider is ModelProviderInstanceDto => provider !== null)
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
      // A harness channel takes its argv from Core's catalog, so an editable launch_args box would
      // collect a string Core then ignores. The one local shape that does read stored arguments is
      // the serve runtime, and it is the one that advertises a default endpoint.
      server_url: cliLike && Boolean(supplier.default_base_url),
      launch_args: cliLike && Boolean(supplier.default_base_url)
    },
    placeholders: presentation?.placeholders ?? {}
  }
}

export function bindingForAgent(overview: AgentCenterOverviewDto | null, agentId: string) {
  return overview?.agents.find((agent) => agent.id === agentId)?.runtime_binding ?? null
}

/**
 * Derive a display binding for an agent.
 *
 * 优先级与 Core 的 `FormalModeResolver` 一致：用户级运行时绑定（`model_binding`）
 * 覆盖 agent 定义里的策略。**必须先看 binding** —— 运行时绑定写入不会改动定义的
 * `model_strategy`，只读定义会让「保存成功后立刻回弹」。
 */
export function bindingFromModelStrategy(
  agent: { id: string; model_route_purpose?: string | null; model_strategy?: unknown },
  binding?: AgentModelBindingDto | null
): AgentRuntimeBindingDto {
  if (binding) return bindingFromRuntimeOverride(agent, binding)
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

/** Project Core's `agent_runtime_bindings` override onto the desktop view model. */
function bindingFromRuntimeOverride(
  agent: { model_route_purpose?: string | null },
  binding: AgentModelBindingDto
): AgentRuntimeBindingDto {
  const base = {
    source: 'agent_binding' as const,
    writable: true,
    shared_agent_ids: [] as string[],
    warnings: [] as AgentRuntimeBindingDto['warnings']
  }
  if (binding.mode === 'fixed') {
    return {
      ...base,
      selection_kind: 'fixed_model',
      route_purpose: agent.model_route_purpose ?? '',
      runtime_kind: 'model',
      runtime_id: binding.provider_instance_id ?? null,
      provider_instance_id: binding.provider_instance_id ?? null,
      model_id: binding.model ?? null,
      model_source: binding.model ? 'route_override' : 'unset'
    }
  }
  if (binding.mode === 'route') {
    return {
      ...base,
      selection_kind: 'route',
      route_purpose: binding.route_purpose ?? agent.model_route_purpose ?? '',
      runtime_kind: 'model',
      runtime_id: null,
      provider_instance_id: null,
      model_id: null,
      model_source: 'route_override'
    }
  }
  return {
    ...base,
    selection_kind: 'inherit',
    route_purpose: agent.model_route_purpose ?? '',
    runtime_kind: 'unresolved',
    runtime_id: null,
    provider_instance_id: null,
    model_id: null,
    model_source: 'unset'
  }
}

/**
 * 「跟随默认」也有可展示的证据：Core 的 `effective_previews` 里带着解析结果
 * （`expected_selection`）。传入 previews 时 inherit 态渲染实际生效的 provider+model，
 * 而不是一个空串（空串会让 UI 退化成「尚未解析」）。
 */
export function runtimeSourceSummary(
  binding?: AgentRuntimeBindingDto | null,
  previews?: Record<string, ModelResolutionPreviewDto> | null,
  providerName?: (providerInstanceId: string) => string | null | undefined
) {
  if (!binding) return ''
  if (binding.runtime_kind === 'unresolved') {
    const selection = Object.values(previews ?? {})
      .map((preview) => preview?.expected_selection)
      .find((sel) => sel && sel.available !== false && (sel.model || sel.provider_instance_id))
    if (!selection) return ''
    const provider = (selection.provider_instance_id ? providerName?.(selection.provider_instance_id) : null) ?? ''
    return selection.model ? (provider ? `${provider} · ${selection.model}` : selection.model) : provider
  }
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

type RuntimeKind = 'model' | 'cli' | 'acp' | 'tui';

function classifyProvider(provider: ModelProviderInstanceDto): RuntimeKind {
  // Core resolves `protocol` itself (explicit value, else the harness catalog's (harness, channel)
  // pair), so it is the only field that states which channel a row is on. Reading `capabilities`
  // first was what made an ACP provider render as a plain CLI row: nothing writes an 'acp'
  // capability any more, and connection_kind 'cli' matched every harness alike.
  const channel = channelForProtocol(provider.protocol) ?? asChannel(provider.channel)
  if (channel) return channel

  const capabilities = new Set(provider.capabilities.map((capability) => capability.toLowerCase()))
  if (capabilities.has('acp')) return 'acp';
  if (provider.connection_kind.toLowerCase() === 'cli' || capabilities.has('cli')) return 'cli';
  return 'model';
}

function normalizeTransportKind(connectionKind: string, protocol?: string | null): string {
  const channel = channelForProtocol(protocol)
  if (channel) return channel
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
  return normalizeTransportKind(template.connection_kind, template.protocol)
    ;
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
  const tuiRuntimes: ModelCenterTuiRuntimeDto[] = [];
  const legacyAcpRuntimes: ModelCenterAcpRuntimeDto[] = [];

  for (const provider of providers) {
    const template = templateByDriver.get(provider.driver.toLowerCase()) ?? null;
    const routePurposes = sortedUnique(routePurposesByProviderId.get(provider.id.toLowerCase()) ?? []);
    const kind = classifyProvider(provider);

    if (kind === 'acp') {
      legacyAcpRuntimes.push({
        id: provider.id,
        runtime_id: provider.id,
        source: 'provider_instance',
        adapter_id: null,
        provider_instance_id: provider.id,
        extension_id: null,
        driver: provider.driver,
        display_name: provider.display_name,
        command: null,
        protocol: provider.protocol ?? null,
        binary_path: provider.binary_path ?? null,
        home_path: provider.home_path ?? null,
        capabilities: provider.capabilities,
        enabled: provider.enabled,
        status: provider.status,
        status_message: provider.status_message,
        route_purposes: routePurposes,
        revision: provider.revision ?? null,
        updated_at: provider.updated_at,
        readiness: null
      });
      continue;
    }

    if (kind === 'tui') {
      // An inventory row, not a control: nothing in this build can host a PTY, so the section says
      // what is configured and nothing more. Rendering a Start button here would promise a host that
      // does not exist.
      tuiRuntimes.push({
        id: provider.id,
        runtime_id: provider.id,
        provider_instance_id: provider.id,
        source: 'provider_instance',
        channel: 'tui',
        driver: provider.driver,
        display_name: provider.display_name,
        protocol: provider.protocol ?? null,
        binary_path: provider.binary_path ?? null,
        home_path: provider.home_path ?? null,
        launch_args: provider.launch_args ?? null,
        capabilities: provider.capabilities,
        enabled: provider.enabled,
        status: provider.status,
        status_message: provider.status_message,
        route_purposes: routePurposes,
        revision: provider.revision ?? null,
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
        channel: 'cli',
        driver: provider.driver,
        display_name: provider.display_name,
        protocol: provider.protocol ?? null,
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
        revision: provider.revision ?? null,
        readiness: null
      });
      continue;
    }

    apiConnections.push({
      id: provider.id,
      provider_instance_id: provider.id,
      provider_family: template?.provider_family ?? null,
      driver: provider.driver,
      protocol: provider.protocol,
      display_name: provider.display_name,
      connection_kind: provider.connection_kind,
      transport_kind: template ? normalizeTemplateTransportKind(template) : normalizeTransportKind(provider.connection_kind),
      credential_kind: template ? normalizeCredentialKind(template.credential_kind) : inferProviderCredentialKind(provider),
      base_url: provider.base_url ?? null,
      model: provider.model ?? null,
      models: provider.models ?? [],
      model_parameters: provider.model_parameters ?? {},
      has_api_key: provider.has_api_key,
      server_url: provider.server_url ?? null,
      capabilities: provider.capabilities,
      enabled: provider.enabled,
      status: provider.status,
      status_message: provider.status_message,
      cooldown_until: provider.cooldown_until ?? null,
      revision: provider.revision ?? null,
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
    diagnostics.push({ code: 'MODEL_READINESS_UNAVAILABLE', severity: 'warning', message: i18n.global.t('settings.modelReadinessUnavailable'), source: 'model-readiness', status: null, route_purpose: null, agent_ids: null });
  }

  return {
    capabilities: {
      provider_crud: true,
      model_catalog_mode: 'configured_only',
      model_discovery_refresh: true,
      live_model_discovery: true,
      // PUT /api/v1/agents/{id}/runtime-binding 是真实端点（Gateway 已代理）。
      agent_runtime_binding_write: true,
      acp_adapter_read: true,
      // Core's `POST /api/v1/acp/adapters/{id}/probe` answers 501 NOT_IMPLEMENTED, so this build
      // cannot probe an adapter and must not claim it can. Kept as a field rather than deleted
      // because it is the flag that decides whether an affordance appears at all.
      acp_probe: false
    },
    suppliers,
    api_connections: apiConnections,
    models: buildConfiguredModels(providers, routes),
    cli_runtimes: cliRuntimes,
    tui_runtimes: tuiRuntimes,
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
