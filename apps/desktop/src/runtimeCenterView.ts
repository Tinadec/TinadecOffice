import type {
  AgentCenterOverviewDto,
  AgentRuntimeBindingDto,
  ModelCenterAcpRuntimeDto,
  ModelCenterApiConnectionDto,
  ModelCenterCliRuntimeDto,
  ModelCenterOverviewDto,
  ModelCenterSupplierDto,
  ModelProviderInstanceDto
} from './api'
import { findTemplate, type ProviderCategory, type ProviderTemplate } from './providerTemplates'

export type ModelCenterSection = 'suppliers' | 'api' | 'models' | 'cli' | 'acp'

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

function legacyAcpProvider(runtime: ModelCenterAcpRuntimeDto): ModelProviderInstanceDto | null {
  if (runtime.source !== 'legacy_provider' || !runtime.provider_instance_id) return null
  return {
    id: runtime.provider_instance_id,
    driver: runtime.driver ?? 'acp',
    display_name: runtime.display_name,
    connection_kind: 'cli',
    model: null,
    has_api_key: false,
    binary_path: runtime.binary_path ?? null,
    home_path: runtime.home_path ?? null,
    capabilities: runtime.capabilities,
    enabled: runtime.enabled,
    status: runtime.status,
    status_message: runtime.status_message,
    created_at: '',
    updated_at: runtime.updated_at ?? ''
  }
}

export function providersFromOverview(overview: ModelCenterOverviewDto | null) {
  if (!overview) return []

  return uniqueProviders([
    ...overview.api_connections.map(apiConnectionProvider),
    ...overview.cli_runtimes.map(cliRuntimeProvider),
    ...overview.acp_runtimes.flatMap((runtime) => {
      const provider = legacyAcpProvider(runtime)
      return provider ? [provider] : []
    })
  ])
}

function categoryForSupplier(supplier: ModelCenterSupplierDto): ProviderCategory {
  if (supplier.transport_kind === 'cli' || supplier.transport_kind === 'acp') return 'agent-cli'
  if (supplier.transport_kind === 'local_http') return 'local-server'
  if (supplier.driver === 'custom') return 'custom'
  return 'cloud-api'
}

export function providerTemplateFromSupplier(supplier: ModelCenterSupplierDto): ProviderTemplate {
  const presentation = findTemplate(supplier.driver)
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
