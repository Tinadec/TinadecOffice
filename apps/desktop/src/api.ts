export interface ProjectDto {
  id: string;
  name: string;
  path: string;
  created_at: string;
}

export interface SessionDto {
  id: string;
  project_id: string;
  title: string;
  status: string;
  created_at: string;
  updated_at: string;
}

export interface MessageDto {
  id: string;
  session_id: string;
  role: 'user' | 'assistant' | string;
  content: string;
  created_at: string;
}

export interface ApprovalDto {
  id: string;
  session_id?: string | null;
  kind: string;
  summary: string;
  command?: string | null;
  cwd?: string | null;
  status: string;
  created_at: string;
  decided_at?: string | null;
}

export interface CreateApprovalInput {
  session_id?: string | null;
  kind: string;
  summary: string;
  command?: string | null;
  cwd?: string | null;
}

export interface ModelSettingsDto {
  base_url: string;
  model: string;
  has_api_key: boolean;
  updated_at: string;
}

export interface ModelProviderTemplateDto {
  provider_family: string;
  driver: string;
  display_name: string;
  connection_kind: 'api-key' | 'cli' | 'local-server' | string;
  credential_kind: string;
  summary: string;
  contributor_description: string;
  default_base_url?: string | null;
  default_model?: string | null;
  default_timeout_seconds: number;
  capabilities: ProviderCapabilityDto;
}

export interface ProviderCapabilityDto {
  supports_streaming: boolean;
  supports_tools: boolean;
  supports_json_mode: boolean;
  supports_system_prompt: boolean;
  max_context_tokens?: number | null;
  requires_workspace: boolean;
  credential_kind: string;
  health_status: 'healthy' | 'unhealthy' | 'unknown' | 'disabled' | 'cooldown' | string;
}

export interface ModelProviderInstanceDto {
  id: string;
  driver: string;
  display_name: string;
  connection_kind: 'api-key' | 'cli' | 'local-server' | string;
  base_url?: string | null;
  model?: string | null;
  has_api_key: boolean;
  binary_path?: string | null;
  home_path?: string | null;
  server_url?: string | null;
  launch_args?: string | null;
  capabilities: string[];
  enabled: boolean;
  status: string;
  status_message: string;
  cooldown_until?: string | null;
  created_at: string;
  updated_at: string;
}

export interface ModelRouteDto {
  purpose: string;
  provider_instance_id: string;
  model?: string | null;
  updated_at: string;
}

export interface ModelProviderReadinessDto {
  provider_instance_id: string;
  display_name: string;
  driver: string;
  connection_kind: string;
  status: string;
  provider_status: string;
  enabled: boolean;
  has_credential: boolean;
  route_purposes: string[];
  summary: string;
  evidence: string[];
}

export interface ModelRouteReadinessDto {
  purpose: string;
  provider_instance_id?: string | null;
  provider_display_name?: string | null;
  model?: string | null;
  status: string;
  summary: string;
  evidence: string[];
}

export interface ModelReadinessReceiptDto {
  status: string;
  generated_at: string;
  receipt_id: string;
  provider_count: number;
  ready_provider_count: number;
  warning_provider_count: number;
  blocked_provider_count: number;
  route_count: number;
  ready_route_count: number;
  warning_route_count: number;
  blocked_route_count: number;
  providers: ModelProviderReadinessDto[];
  routes: ModelRouteReadinessDto[];
  design_notes: string[];
}

export interface ModelCatalogTemplateReadinessDto {
  provider_family: string;
  driver: string;
  display_name: string;
  connection_kind: string;
  credential_kind: string;
  status: string;
  runtime_module_family: string;
  runtime_module_status: string;
  configured_instance_count: number;
  supports_live_discovery: boolean;
  live_discovery_policy: string;
  summary: string;
  evidence: string[];
}

export interface ModelCatalogReadinessReceiptDto {
  status: string;
  generated_at: string;
  receipt_id: string;
  template_count: number;
  ready_template_count: number;
  warning_template_count: number;
  blocked_template_count: number;
  runtime_module_count: number;
  configured_provider_count: number;
  advisory_probe_template_count: number;
  templates: ModelCatalogTemplateReadinessDto[];
  design_notes: string[];
}

export interface SaveModelProviderInstanceInput {
  id?: string | null;
  driver: string;
  display_name: string;
  connection_kind: string;
  base_url?: string | null;
  model?: string | null;
  api_key?: string | null;
  clear_api_key?: boolean;
  binary_path?: string | null;
  home_path?: string | null;
  server_url?: string | null;
  launch_args?: string | null;
  capabilities?: string[];
  enabled?: boolean;
}

export interface DoctorReportDto {
  platform: string;
  agent_core_version: string;
  checks: Array<{ name: string; status: string; message: string }>;
}

export interface RuntimeReadinessComponentDto {
  id: string;
  name: string;
  status: string;
  summary: string;
  evidence: string[];
}

export interface RuntimeReadinessReceiptDto {
  status: string;
  generated_at: string;
  runtime: string;
  receipt_id: string;
  components: RuntimeReadinessComponentDto[];
  ready_count: number;
  warning_count: number;
  blocked_count: number;
}

export interface ToolLayerToolReadinessDto {
  tool_id: string;
  display_name: string;
  source: string;
  provider_layer: string;
  risk: string;
  status: string;
  requires_approval: boolean;
  requires_human_checkpoint: boolean;
  is_future: boolean;
  assigned_execution_agent_count: number;
  summary: string;
  evidence: string[];
}

export interface ToolLayerAgentScopeReadinessDto {
  agent_id: string;
  agent_name: string;
  layer: string;
  agent_type: string;
  enabled: boolean;
  status: string;
  declared_scope_count: number;
  dispatchable_tool_count: number;
  internal_capability_count: number;
  unresolved_scope_count: number;
  approval_gated_tool_count: number;
  tool_ids: string[];
  unresolved_scopes: string[];
  summary: string;
  evidence: string[];
}

export interface ToolLayerReadinessReceiptDto {
  status: string;
  generated_at: string;
  runtime: string;
  receipt_id: string;
  tool_count: number;
  ready_tool_count: number;
  warning_tool_count: number;
  blocked_tool_count: number;
  execution_agent_count: number;
  ready_agent_count: number;
  warning_agent_count: number;
  blocked_agent_count: number;
  approval_gated_tool_count: number;
  human_checkpoint_tool_count: number;
  future_tool_count: number;
  unresolved_scope_count: number;
  tools: ToolLayerToolReadinessDto[];
  agent_scopes: ToolLayerAgentScopeReadinessDto[];
  design_notes: string[];
}

export interface EventEnvelope {
  v: string;
  type: string;
  request_id: string;
  session_id?: string | null;
  trace_id: string;
  seq: number;
  ts: string;
  capabilities: string[];
  payload?: Record<string, unknown> | null;
  error?: { code: string; message: string; detail?: string | null } | null;
}

export interface ExtensionSourceDto {
  id: string;
  name: string;
  kind: string;
  location: string;
  enabled: boolean;
  last_refreshed_at?: string | null;
  created_at: string;
}

export interface MarketCatalogItemDto {
  catalog_id: string;
  source_id: string;
  extension_id: string;
  kind: 'skill' | 'mcp-server' | 'acp-adapter' | 'tool-pack' | string;
  version: string;
  publisher: string;
  display_name: string;
  description: string;
  source_kind: string;
  source_location: string;
  capabilities: string[];
  permissions: string[];
  status: string;
  installed_extension_id?: string | null;
}

export interface InstalledExtensionDto {
  id: string;
  catalog_id?: string | null;
  extension_id: string;
  kind: string;
  version: string;
  publisher: string;
  display_name: string;
  description: string;
  source_kind: string;
  source_location: string;
  capabilities: string[];
  permissions: string[];
  enabled: boolean;
  status: string;
  status_message: string;
  installed_at: string;
  updated_at: string;
}

export interface ExtensionInstallPreviewDto {
  extension_id: string;
  kind: string;
  version: string;
  publisher: string;
  display_name: string;
  description: string;
  source_kind: string;
  source_location: string;
  capabilities: string[];
  permissions: string[];
  risks: string[];
  requires_approval: boolean;
  approval_summary: string;
}

export interface ExtensionInstallResultDto {
  approval_required: boolean;
  approval?: ApprovalDto | null;
  extension?: InstalledExtensionDto | null;
  preview: ExtensionInstallPreviewDto;
}

export interface McpServerDto {
  id: string;
  extension_id: string;
  name: string;
  transport: string;
  status: string;
  tools: string[];
  updated_at: string;
}

export interface AcpAdapterDto {
  id: string;
  extension_id: string;
  name: string;
  command: string;
  status: string;
  status_message: string;
  capabilities: string[];
  updated_at: string;
}

export interface CenterDiagnosticDto {
  code: string;
  severity: 'warning' | 'error' | string;
  message: string;
  source?: string | null;
  status?: number | null;
  route_purpose?: string | null;
  agent_ids?: string[] | null;
}

export interface ModelCenterCapabilitiesDto {
  provider_crud: boolean;
  model_catalog_mode: 'configured_only' | string;
  model_discovery_refresh: boolean;
  live_model_discovery: boolean;
  agent_runtime_binding_write: boolean;
  acp_adapter_read: boolean;
  acp_probe: boolean;
}

export interface ModelCenterSupplierDto {
  supplier_id: string;
  provider_family: string;
  driver: string;
  display_name: string;
  connection_kind: string;
  transport_kind: string;
  credential_kind: string;
  summary: string;
  contributor_description: string;
  default_base_url?: string | null;
  default_model?: string | null;
  default_timeout_seconds: number;
  capabilities: Record<string, unknown>;
}

export interface ModelCenterApiConnectionDto {
  id: string;
  provider_instance_id: string;
  provider_family?: string | null;
  driver: string;
  display_name: string;
  connection_kind: string;
  transport_kind: string;
  credential_kind: string;
  base_url?: string | null;
  model?: string | null;
  has_api_key: boolean;
  server_url?: string | null;
  capabilities: string[];
  enabled: boolean;
  status: string;
  status_message: string;
  cooldown_until?: string | null;
  created_at?: string | null;
  updated_at?: string | null;
  route_purposes: string[];
  readiness?: Record<string, unknown> | null;
}

export interface ModelCenterModelDto {
  id: string;
  display_name: string;
  provider_instance_id: string;
  provider_display_name?: string | null;
  model_id: string;
  source: 'configured_only' | string;
  configuration_sources: Array<'provider_default' | 'route_override' | string>;
  is_provider_default: boolean;
  route_purposes: string[];
  enabled: boolean;
  status: string;
}

export interface ModelCenterCliRuntimeDto {
  id: string;
  runtime_id: string;
  provider_instance_id: string;
  source: 'provider_instance' | string;
  driver: string;
  display_name: string;
  binary_path?: string | null;
  home_path?: string | null;
  server_url?: string | null;
  launch_args?: string | null;
  model?: string | null;
  capabilities: string[];
  enabled: boolean;
  status: string;
  status_message: string;
  route_purposes: string[];
  readiness?: Record<string, unknown> | null;
}

export interface ModelCenterAcpRuntimeDto {
  id: string;
  runtime_id: string;
  source: 'adapter' | 'legacy_provider' | string;
  adapter_id?: string | null;
  provider_instance_id?: string | null;
  extension_id?: string | null;
  driver?: string | null;
  display_name: string;
  command?: string | null;
  binary_path?: string | null;
  home_path?: string | null;
  status: string;
  status_message: string;
  capabilities: string[];
  enabled: boolean;
  route_purposes: string[];
  updated_at?: string | null;
  readiness?: Record<string, unknown> | null;
}

export interface ModelCenterOverviewDto {
  capabilities: ModelCenterCapabilitiesDto;
  suppliers: ModelCenterSupplierDto[];
  api_connections: ModelCenterApiConnectionDto[];
  models: ModelCenterModelDto[];
  cli_runtimes: ModelCenterCliRuntimeDto[];
  acp_runtimes: ModelCenterAcpRuntimeDto[];
  readiness: {
    model?: ModelReadinessReceiptDto | null;
    catalog?: ModelCatalogReadinessReceiptDto | null;
  };
  diagnostics: CenterDiagnosticDto[];
}

export type AgentRuntimeSelectionKind = 'inherit' | 'fixed_model' | 'provider_auto' | 'cli' | 'acp';

export type AgentRuntimeBindingInput =
  | { selection_kind: 'inherit' }
  | { selection_kind: 'fixed_model'; provider_instance_id: string; model_id: string }
  | { selection_kind: 'provider_auto'; provider_instance_id: string }
  | { selection_kind: 'cli'; runtime_id: string }
  | { selection_kind: 'acp'; runtime_id: string };

export interface AgentRuntimeBindingWarningDto {
  code: 'LEGACY_SHARED_ROUTE' | string;
  message: string;
  shared_agent_ids: string[];
}

export interface AgentRuntimeBindingDto {
  selection_kind: AgentRuntimeSelectionKind;
  source: 'legacy_route' | 'agent_binding' | string;
  writable: boolean;
  route_purpose: string;
  runtime_kind: 'model' | 'cli' | 'acp' | 'unresolved' | string;
  runtime_id?: string | null;
  provider_instance_id?: string | null;
  provider_display_name?: string | null;
  model_id?: string | null;
  model_source: 'route_override' | 'provider_default' | 'unset' | string;
  shared_agent_ids: string[];
  warnings: AgentRuntimeBindingWarningDto[];
}

export interface AgentCenterAgentDto extends AgentProfileDto {
  runtime_binding: AgentRuntimeBindingDto;
}

export interface AgentCenterOverviewDto {
  capabilities: ModelCenterCapabilitiesDto;
  agents: AgentCenterAgentDto[];
  modes: AgentModeDto[];
  candidates: AgentCandidateDto[];
  runtime_sources: {
    models: ModelCenterModelDto[];
    providers: ModelCenterApiConnectionDto[];
    cli_runtimes: ModelCenterCliRuntimeDto[];
    acp_runtimes: ModelCenterAcpRuntimeDto[];
  };
  readiness: {
    model?: ModelReadinessReceiptDto | null;
    catalog?: ModelCatalogReadinessReceiptDto | null;
  };
  diagnostics: CenterDiagnosticDto[];
}

export interface AgentProfileDto {
  id: string;
  name: string;
  layer: 'planning' | 'execution' | string;
  agent_type: string;
  mode: string;
  description: string;
  model_route_purpose: string;
  allowed_tools: string[];
  capabilities: string[];
  system_prompt?: string | null;
  enabled: boolean;
  is_built_in: boolean;
  updated_at: string | null;
}

export interface AgentModeDto {
  id: string;
  display_name: string;
  summary: string;
  max_parallel_executors: number;
  worktree_isolation: boolean;
  approval_required: boolean;
  budget_policy: string;
}

export interface AgentCandidateDto {
  id: string;
  generated_by_agent_id: string;
  name: string;
  layer: string;
  agent_type: string;
  description: string;
  suggested_tools: string[];
  evaluation_notes: string[];
  status: string;
  created_at: string;
}

export interface AgentEvolutionProposalDto {
  id: string;
  generated_by_agent_id: string;
  name: string;
  layer: string;
  agent_type: string;
  description: string;
  suggested_tools: string[];
  evaluation_notes: string[];
  observed_patterns: string[];
  confidence_score: number;
  status: string;
  created_at: string;
}

export interface PromoteAgentCandidateInput {
  agent_id: string;
  mode: string;
  model_route_purpose: string;
  allowed_tools: string[];
  capabilities: string[];
  system_prompt?: string | null;
}

export interface PromptFragmentVersionDto {
  id: string;
  fragment_id: string;
  version: number;
  content: string;
  changed_fields: string[];
  change_summary: string;
  is_active: boolean;
  created_at: string;
}

export interface PromptFragmentEffectivenessDto {
  fragment_id: string;
  active_version: number;
  total_invocations: number;
  positive_signals: number;
  negative_signals: number;
  effectiveness_score: number;
  last_evaluated_at: string;
  versions: PromptFragmentVersionDto[];
}

export interface PromptFragmentSignalInput {
  fragment_id: string;
  signal: 'positive' | 'negative';
  run_id?: string | null;
  session_id?: string | null;
  note?: string | null;
  version?: number | null;
}

export interface PromptFragmentAbTestResultDto {
  fragment_id: string;
  version_a: number;
  version_b: number;
  version_a_details?: PromptFragmentVersionDto | null;
  version_b_details?: PromptFragmentVersionDto | null;
  score_a: number;
  score_b: number;
  score_difference: number;
  recommendation: string;
}

export interface ModelStreamChunkDto {
  run_id: string;
  session_id: string;
  purpose: string;
  provider_instance_id: string;
  effective_model?: string | null;
  kind: 'context' | 'delta' | 'tool_call_delta' | 'usage' | 'done' | 'error';
  delta?: string | null;
  tool_call_delta?: {
    call_id: string;
    tool_id: string;
    arguments: Record<string, unknown>;
  } | null;
  usage?: { prompt_tokens: number; completion_tokens: number; total_tokens: number } | null;
  finish_reason?: string | null;
  error_category?: string | null;
  is_retryable?: boolean;
  safe_error_message?: string | null;
  fallback_provider_selected?: boolean;
  error_provider_id?: string | null;
}

export interface PromptFragmentDto {
  id: string;
  key: string;
  title: string;
  scope: string;
  target_agent_id?: string | null;
  category: string;
  content: string;
  priority: number;
  enabled: boolean;
  is_builtin: boolean;
  created_at: string;
  updated_at: string;
}

export interface SavePromptFragmentInput {
  key: string;
  title: string;
  scope: string;
  target_agent_id?: string | null;
  category: string;
  content: string;
  priority: number;
  enabled: boolean;
}

export interface PromptContextPreviewInput {
  agent_id: string;
  mode?: string | null;
  session_id?: string | null;
  run_id?: string | null;
  user_content?: string | null;
}

export interface PromptContextPreviewDto {
  agent_id: string;
  mode: string;
  fragments: PromptFragmentDto[];
  context_pack_ids: string[];
  estimated_tokens: number;
  system_prompt: string;
  warnings: string[];
}

export interface OrchestrationRunDto {
  id: string;
  session_id: string;
  user_message_id?: string | null;
  status: string;
  summary: string;
  created_at: string;
  updated_at: string;
}

export interface TaskGraphDto {
  id: string;
  run_id: string;
  session_id: string;
  title: string;
  status: string;
  created_at: string;
  updated_at: string;
}

export interface TaskNodeDto {
  id: string;
  graph_id: string;
  run_id: string;
  session_id: string;
  title: string;
  description: string;
  status: string;
  priority: number;
  risk: string;
  success_criteria: string[];
  dependencies: string[];
  required_capabilities: string[];
  created_at: string;
  updated_at: string;
}

export interface AgentAssignmentDto {
  id: string;
  run_id: string;
  task_node_id: string;
  agent_id: string;
  agent_name: string;
  agent_layer: string;
  agent_type: string;
  model_route_purpose: string;
  permission_mode: string;
  allowed_tools: string[];
  status: string;
  created_at: string;
}

export interface StepResultDto {
  id: string;
  run_id: string;
  task_node_id: string;
  agent_id: string;
  status: string;
  summary: string;
  evidence: string[];
  created_at: string;
}

export interface ToolExecutionTimelineItemDto {
  id: string;
  run_id: string;
  session_id: string;
  tool_id: string;
  tool_display_name: string;
  source: string;
  provider_layer: string;
  risk: string;
  requires_approval: boolean;
  status: string;
  approval_id?: string | null;
  step_result_id?: string | null;
  summary: string;
  evidence: string[];
  requested_at: string;
  updated_at: string;
  duration_ms: number;
  requested_seq: number;
  updated_seq: number;
  event_types: string[];
  checkpoint_summary: string;
}

export interface ContextPackDto {
  id: string;
  run_id: string;
  session_id: string;
  created_by_agent_id: string;
  summary: string;
  token_budget: number;
  compression_ratio: number;
  evidence_map: string[];
  created_at: string;
}

export interface SupervisionFindingDto {
  id: string;
  run_id: string;
  session_id: string;
  severity: string;
  category: string;
  summary: string;
  recommendation: string;
  status: string;
  created_at: string;
}

export interface ToolDescriptorDto {
  id: string;
  display_name: string;
  domain: string;
  source: string;
  risk: string;
  requires_approval: boolean;
  execute_endpoint: string;
  capabilities: string[];
}

export interface ToolSearchResultDto {
  tool: ToolDescriptorDto;
  score: number;
  matched_fields: string[];
  provider_layer: string;
  requires_human_checkpoint: boolean;
  approval_summary: string;
}

export interface AgentLayerManifestDto {
  layer: string;
  role: string;
  agent_count: number;
  enabled_agent_count: number;
  max_parallel_executors: number;
  worktree_isolation: boolean;
  approval_required: boolean;
  agent_types: string[];
  tool_ids: string[];
}

export interface ToolProviderManifestDto {
  source: string;
  display_name: string;
  layer: string;
  status: string;
  tool_count: number;
  active_tool_count: number;
  future_tool_count: number;
  approval_required_count: number;
  read_only_count: number;
  capability_prefixes: string[];
}

export interface ToolRiskManifestDto {
  risk: string;
  tool_count: number;
  requires_human_checkpoint: boolean;
  policy_summary: string;
}

export interface ToolRegistrySummaryDto {
  declared_tool_count: number;
  canonical_tool_count: number;
  duplicate_tool_id_count: number;
  duplicate_tool_ids: string[];
  source_precedence: string[];
  selection_policy: string;
}

export interface HarnessManifestDto {
  runtime: string;
  ownership_model: string;
  tool_registry: ToolRegistrySummaryDto;
  agent_layers: AgentLayerManifestDto[];
  tool_providers: ToolProviderManifestDto[];
  tool_risks: ToolRiskManifestDto[];
  tools: ToolDescriptorDto[];
  design_notes: string[];
}

export interface CodeToolExecuteResultDto {
  tool_id: string;
  status: string;
  summary: string;
  evidence: string[];
  data: Record<string, unknown>;
  requires_approval: boolean;
  approval_summary?: string | null;
}

export interface CodeToolExecuteRequestDto {
  session_id?: string | null;
  run_id?: string | null;
  task_node_id?: string | null;
  approval_id?: string | null;
  cwd?: string | null;
  arguments?: Record<string, unknown> | null;
}

export interface OrchestrationSnapshotDto {
  run?: OrchestrationRunDto | null;
  graph?: TaskGraphDto | null;
  nodes: TaskNodeDto[];
  assignments: AgentAssignmentDto[];
  step_results: StepResultDto[];
  context_packs: ContextPackDto[];
  supervision_findings: SupervisionFindingDto[];
}

const gatewayUrl = window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730';

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${gatewayUrl}${path}`, {
      ...init,
      headers: {
        accept: 'application/json',
        ...(init?.body ? { 'content-type': 'application/json' } : {}),
        ...(init?.headers ?? {})
      }
    });
  } catch (err) {
    // fetch() itself failed (network error, CORS blocked, etc.)
    const msg = err instanceof Error ? err.message : 'Network request failed';
    throw new Error(`Cannot connect to backend (${gatewayUrl}): ${msg}`);
  }

  const text = await response.text();
  let data: unknown = null;
  if (text.length > 0) {
    try {
      data = JSON.parse(text);
    } catch {
      // Response body is not valid JSON – surface the raw text for debugging
      throw new Error(`Invalid response from server: ${text.substring(0, 200)}`);
    }
  }

  if (!response.ok) {
    const message = extractErrorMessage(data, response.statusText);
    throw new Error(message);
  }

  return data as T;
}

function extractErrorMessage(data: unknown, fallback: string): string {
  if (!data || typeof data !== 'object') return fallback;

  const record = data as Record<string, unknown>;
  const directMessage = record.message;
  if (typeof directMessage === 'string' && directMessage.length > 0) return directMessage;

  const nestedError = record.error;
  if (nestedError && typeof nestedError === 'object') {
    const nestedMessage = (nestedError as Record<string, unknown>).message;
    if (typeof nestedMessage === 'string' && nestedMessage.length > 0) return nestedMessage;
  }

  return fallback;
}

export const api = {
  gatewayUrl,
  health: () => request<Record<string, unknown>>('/api/v1/health'),
  doctor: () => request<DoctorReportDto>('/api/v1/doctor'),
  readiness: () => request<RuntimeReadinessReceiptDto>('/api/v1/readiness'),
  getToolLayerReadiness: () => request<ToolLayerReadinessReceiptDto>('/api/v1/tool-layer-readiness'),
  listProjects: () => request<ProjectDto[]>('/api/v1/projects'),
  createProject: (name: string, path: string) => request<ProjectDto>('/api/v1/projects', {
    method: 'POST',
    body: JSON.stringify({ name, path })
  }),
  listSessions: (projectId?: string) => request<SessionDto[]>(`/api/v1/sessions${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`),
  createSession: (projectId: string, title?: string) => request<SessionDto>('/api/v1/sessions', {
    method: 'POST',
    body: JSON.stringify({ project_id: projectId, title })
  }),
  updateSessionTitle: (sessionId: string, title: string) => request<SessionDto>(`/api/v1/sessions/${sessionId}`, {
    method: 'PATCH',
    body: JSON.stringify({ title })
  }),
  listMessages: (sessionId: string) => request<MessageDto[]>(`/api/v1/sessions/${sessionId}/messages`),
  postMessage: (sessionId: string, content: string) => request<MessageDto>(`/api/v1/sessions/${sessionId}/messages`, {
    method: 'POST',
    body: JSON.stringify({ content })
  }),
  getOrchestrationSnapshot: (sessionId: string) => request<OrchestrationSnapshotDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/orchestration`),
  listToolExecutions: (sessionId: string, params: { run_id?: string; limit?: number } = {}) => {
    const search = new URLSearchParams();
    if (params.run_id) search.set('run_id', params.run_id);
    if (params.limit !== undefined) search.set('limit', String(params.limit));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<ToolExecutionTimelineItemDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/tool-executions${suffix}`);
  },
  listRuns: (sessionId: string) => request<OrchestrationRunDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/runs`),
  listTaskNodes: (sessionId: string) => request<TaskNodeDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/task-nodes`),
  listContextPacks: (sessionId: string) => request<ContextPackDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/context-packs`),
  listSupervisionFindings: (sessionId: string) => request<SupervisionFindingDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/supervision-findings`),
  listApprovals: (sessionId?: string, status?: string) => {
    const search = new URLSearchParams();
    if (status) search.set('status', status);
    if (sessionId) search.set('session_id', sessionId);
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<ApprovalDto[]>(`/api/v1/approvals${suffix}`);
  },
  createApproval: (approval: CreateApprovalInput) => request<ApprovalDto>('/api/v1/approvals', {
    method: 'POST',
    body: JSON.stringify(approval)
  }),
  decideApproval: (approvalId: string, decision: 'approved' | 'rejected') => request<ApprovalDto>(`/api/v1/approvals/${approvalId}/decision`, {
    method: 'POST',
    body: JSON.stringify({ decision })
  }),
  createShellApproval: (sessionId: string | null, command: string, cwd?: string) => request<ApprovalDto>('/api/v1/tools/shell', {
    method: 'POST',
    body: JSON.stringify({
      session_id: sessionId,
      kind: 'shell',
      summary: command,
      command,
      cwd
    })
  }),
  listModelProviderTemplates: () => request<ModelProviderTemplateDto[]>('/api/v1/model-provider-templates'),
  listModelProviders: () => request<ModelProviderInstanceDto[]>('/api/v1/model-providers'),
  getModelCenterOverview: () => request<ModelCenterOverviewDto>('/api/v1/model-center/overview'),
  refreshProviderModels: (providerInstanceId: string) => request<ModelCenterOverviewDto>(`/api/v1/model-center/provider-instances/${encodeURIComponent(providerInstanceId)}/models/refresh`, {
    method: 'POST'
  }),
  getModelReadiness: () => request<ModelReadinessReceiptDto>('/api/v1/model-readiness'),
  getModelCatalogReadiness: () => request<ModelCatalogReadinessReceiptDto>('/api/v1/model-catalog-readiness'),
  createModelProvider: (provider: SaveModelProviderInstanceInput) => request<ModelProviderInstanceDto>('/api/v1/model-providers', {
    method: 'POST',
    body: JSON.stringify(provider)
  }),
  saveModelProvider: (providerId: string, provider: SaveModelProviderInstanceInput) => request<ModelProviderInstanceDto>(`/api/v1/model-providers/${encodeURIComponent(providerId)}`, {
    method: 'PUT',
    body: JSON.stringify(provider)
  }),
  deleteModelProvider: (providerId: string) => request<void>(`/api/v1/model-providers/${encodeURIComponent(providerId)}`, {
    method: 'DELETE'
  }),
  listModelRoutes: () => request<ModelRouteDto[]>('/api/v1/model-routes'),
  saveModelRoute: (purpose: string, providerInstanceId: string, model?: string | null) => request<ModelRouteDto>(`/api/v1/model-routes/${encodeURIComponent(purpose)}`, {
    method: 'PUT',
    body: JSON.stringify({ provider_instance_id: providerInstanceId, model })
  }),
  getModelSettings: () => request<ModelSettingsDto>('/api/v1/model-settings'),
  saveModelSettings: (settings: { base_url: string; model: string; api_key?: string; clear_api_key?: boolean }) => request<ModelSettingsDto>('/api/v1/model-settings', {
    method: 'PUT',
    body: JSON.stringify(settings)
  }),
  listExtensionSources: () => request<ExtensionSourceDto[]>('/api/v1/market/sources'),
  createExtensionSource: (source: { name: string; kind: string; location: string; enabled?: boolean }) => request<ExtensionSourceDto>('/api/v1/market/sources', {
    method: 'POST',
    body: JSON.stringify(source)
  }),
  refreshExtensionSource: (sourceId: string) => request<ExtensionSourceDto>(`/api/v1/market/sources/${encodeURIComponent(sourceId)}/refresh`, {
    method: 'POST'
  }),
  listMarketCatalog: (params: { kind?: string; query?: string; source_id?: string } = {}) => {
    const search = new URLSearchParams();
    if (params.kind && params.kind !== 'all') search.set('kind', params.kind);
    if (params.query) search.set('query', params.query);
    if (params.source_id) search.set('source_id', params.source_id);
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<MarketCatalogItemDto[]>(`/api/v1/market/catalog${suffix}`);
  },
  getMarketCatalogItem: (catalogId: string) => request<MarketCatalogItemDto>(`/api/v1/market/catalog/${encodeURIComponent(catalogId)}`),
  previewExtensionInstall: (input: { catalog_id?: string | null; source_kind?: string | null; source_location?: string | null; manifest_json?: string | null }) => request<ExtensionInstallPreviewDto>('/api/v1/extensions/install-preview', {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  installExtension: (input: { catalog_id?: string | null; source_kind?: string | null; source_location?: string | null; manifest_json?: string | null; approval_id?: string | null }) => request<ExtensionInstallResultDto>('/api/v1/extensions/install', {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  listInstalledExtensions: () => request<InstalledExtensionDto[]>('/api/v1/extensions/installed'),
  enableExtension: (extensionId: string) => request<InstalledExtensionDto>(`/api/v1/extensions/${encodeURIComponent(extensionId)}/enable`, { method: 'POST' }),
  disableExtension: (extensionId: string) => request<InstalledExtensionDto>(`/api/v1/extensions/${encodeURIComponent(extensionId)}/disable`, { method: 'POST' }),
  updateExtension: (extensionId: string) => request<InstalledExtensionDto>(`/api/v1/extensions/${encodeURIComponent(extensionId)}/update`, { method: 'POST' }),
  deleteExtension: (extensionId: string) => request<void>(`/api/v1/extensions/${encodeURIComponent(extensionId)}`, { method: 'DELETE' }),
  listMcpServers: () => request<McpServerDto[]>('/api/v1/mcp/servers'),
  reloadMcpServer: (serverId: string) => request<McpServerDto>(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/reload`, { method: 'POST' }),
  connectMcpServer: (serverId: string) => request<McpServerDto>(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/connect`, { method: 'POST' }),
  listAcpAdapters: () => request<AcpAdapterDto[]>('/api/v1/acp/adapters'),
  probeAcpAdapter: (adapterId: string) => request<AcpAdapterDto>(`/api/v1/acp/adapters/${encodeURIComponent(adapterId)}/probe`, { method: 'POST' }),
  getAgentCenterOverview: () => request<AgentCenterOverviewDto>('/api/v1/agent-center/overview'),
  saveAgentRuntimeBinding: (agentId: string, binding: AgentRuntimeBindingInput) => request<AgentRuntimeBindingDto>(`/api/v1/agents/${encodeURIComponent(agentId)}/runtime-binding`, {
    method: 'PUT',
    body: JSON.stringify(binding)
  }),
  listAgentModes: () => request<AgentModeDto[]>('/api/v1/agent-modes'),
  listAgents: () => request<AgentProfileDto[]>('/api/v1/agents'),
  listTools: () => request<ToolDescriptorDto[]>('/api/v1/tools'),
  searchTools: (params: { query?: string; domain?: string; source?: string; risk?: string; limit?: number } = {}) => {
    const search = new URLSearchParams();
    if (params.query) search.set('query', params.query);
    if (params.domain) search.set('domain', params.domain);
    if (params.source) search.set('source', params.source);
    if (params.risk) search.set('risk', params.risk);
    if (params.limit !== undefined) search.set('limit', String(params.limit));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<ToolSearchResultDto[]>(`/api/v1/tools/search${suffix}`);
  },
  getHarnessManifest: () => request<HarnessManifestDto>('/api/v1/harness/manifest'),
  listPromptFragments: (params: { scope?: string; target_agent_id?: string; category?: string; enabled?: boolean } = {}) => {
    const search = new URLSearchParams();
    if (params.scope) search.set('scope', params.scope);
    if (params.target_agent_id) search.set('target_agent_id', params.target_agent_id);
    if (params.category) search.set('category', params.category);
    if (params.enabled !== undefined) search.set('enabled', String(params.enabled));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<PromptFragmentDto[]>(`/api/v1/prompt-fragments${suffix}`);
  },
  createPromptFragment: (fragment: SavePromptFragmentInput) => request<PromptFragmentDto>('/api/v1/prompt-fragments', {
    method: 'POST',
    body: JSON.stringify(fragment)
  }),
  savePromptFragment: (fragmentId: string, fragment: SavePromptFragmentInput) => request<PromptFragmentDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}`, {
    method: 'PUT',
    body: JSON.stringify(fragment)
  }),
  deletePromptFragment: (fragmentId: string) => request<void>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}`, {
    method: 'DELETE'
  }),
  clonePromptFragment: (fragmentId: string) => request<PromptFragmentDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/clone`, {
    method: 'POST'
  }),
  previewPromptContext: (input: PromptContextPreviewInput) => request<PromptContextPreviewDto>('/api/v1/prompt-context/preview', {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  executeCodeTool: (toolId: string, payload: CodeToolExecuteRequestDto = {}) => request<CodeToolExecuteResultDto>(`/api/v1/code/tools/${toolId}/execute`, {
    method: 'POST',
    body: JSON.stringify(payload)
  }),

  // Semantic wrappers for code tools
  readFile: (cwd: string, filePath: string, options?: { start_line?: number; end_line?: number }) =>
    api.executeCodeTool('read_file', { cwd, arguments: { path: filePath, ...options } }),
  listDirectory: (cwd: string, dirPath: string) =>
    api.executeCodeTool('list_directory', { cwd, arguments: { path: dirPath } }),
  globSearch: (cwd: string, pattern: string) =>
    api.executeCodeTool('glob_search', { cwd, arguments: { pattern } }),
  grepContent: (cwd: string, pattern: string, options?: { case_sensitive?: boolean; context_lines?: number; max_results?: number }) =>
    api.executeCodeTool('grep_content', { cwd, arguments: { pattern, ...options } }),
  applyPatch: (cwd: string, patch: string, approvalId?: string) =>
    api.executeCodeTool('apply_patch', { cwd, approval_id: approvalId, arguments: { patch } }),
  codeEditorOpen: (cwd: string, filePath: string) =>
    api.executeCodeTool('code_editor', { cwd, arguments: { action: 'open', path: filePath } }),
  codeEditorSave: (cwd: string, filePath: string, content: string, approvalId: string) =>
    api.executeCodeTool('code_editor', { cwd, approval_id: approvalId, arguments: { action: 'save', path: filePath, content } }),
  codeEditorDiff: (cwd: string, filePath: string) =>
    api.executeCodeTool('code_editor', { cwd, arguments: { action: 'diff', path: filePath } }),
  codeEditorPatch: (cwd: string, filePath: string, patch: string, approvalId: string) =>
    api.executeCodeTool('code_editor', { cwd, approval_id: approvalId, arguments: { action: 'patch', path: filePath, patch } }),
  gitDiffCompare: (cwd: string, baseRef: string, headRef: string, paths?: string[]) =>
    api.executeCodeTool('git_worktree_manager', { cwd, arguments: { action: 'diff_compare', base_ref: baseRef, head_ref: headRef, paths } }),
  gitLog: (cwd: string, limit?: number, ref?: string) =>
    api.executeCodeTool('git_worktree_manager', { cwd, arguments: { action: 'log', limit, ref } }),
  saveAgent: (agentId: string, agent: {
    name: string;
    layer: string;
    agent_type: string;
    mode: string;
    description: string;
    model_route_purpose: string;
    allowed_tools?: string[];
    capabilities?: string[];
    system_prompt?: string | null;
    enabled: boolean;
  }) => request<AgentProfileDto>(`/api/v1/agents/${encodeURIComponent(agentId)}`, {
    method: 'PUT',
    body: JSON.stringify(agent)
  }),
  updateAgentMode: (agentId: string, mode: string) => request<AgentProfileDto>(`/api/v1/agents/${encodeURIComponent(agentId)}/mode`, {
    method: 'PUT',
    body: JSON.stringify({ mode })
  }),
  listAgentCandidates: () => request<AgentCandidateDto[]>('/api/v1/agent-candidates'),

  // --- Agent Evolution ---
  listEvolutionProposals: () => request<AgentEvolutionProposalDto[]>('/api/v1/agent-evolution/proposals'),
  generateEvolutionProposals: (params: { session_id?: string; lookback_event_count?: number } = {}) => {
    const search = new URLSearchParams();
    if (params.session_id) search.set('session_id', params.session_id);
    if (params.lookback_event_count !== undefined) search.set('lookback_event_count', String(params.lookback_event_count));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<AgentEvolutionProposalDto[]>(`/api/v1/agent-evolution/generate${suffix}`, { method: 'POST' });
  },
  promoteAgentCandidate: (candidateId: string, input: PromoteAgentCandidateInput) => request<AgentProfileDto>(`/api/v1/agent-evolution/proposals/${encodeURIComponent(candidateId)}/promote`, {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  rejectAgentCandidate: (candidateId: string, reason?: string) => request<{ status: string; candidate_id: string }>(`/api/v1/agent-evolution/proposals/${encodeURIComponent(candidateId)}/reject`, {
    method: 'POST',
    body: JSON.stringify({ reason })
  }),

  // --- Prompt Engineering: Versioning + A/B Testing + Effectiveness ---
  listPromptFragmentVersions: (fragmentId: string) => request<PromptFragmentVersionDto[]>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/versions`),
  createPromptFragmentVersion: (fragmentId: string, input: { content: string; changed_fields: string[]; change_summary: string }) => request<PromptFragmentVersionDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/versions`, {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  rollbackPromptFragment: (fragmentId: string, targetVersion: number) => request<PromptFragmentDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/rollback`, {
    method: 'POST',
    body: JSON.stringify({ target_version: targetVersion })
  }),
  getPromptFragmentEffectiveness: (fragmentId: string) => request<PromptFragmentEffectivenessDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/effectiveness`),
  listAllPromptFragmentEffectiveness: () => request<PromptFragmentEffectivenessDto[]>('/api/v1/prompt-fragments/effectiveness'),
  recordPromptFragmentSignal: (fragmentId: string, input: Omit<PromptFragmentSignalInput, 'fragment_id'>) => request<PromptFragmentEffectivenessDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/signals`, {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  comparePromptFragmentVersions: (fragmentId: string, versionA: number, versionB: number) => request<PromptFragmentAbTestResultDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/compare`, {
    method: 'POST',
    body: JSON.stringify({ version_a: versionA, version_b: versionB })
  }),

  // --- Streaming Invoke (SSE) ---
  invokeStream: (sessionId: string, content: string, onChunk: (chunk: ModelStreamChunkDto) => void, onError?: (error: Error) => void): AbortController => {
    const controller = new AbortController();
    const decoder = new TextDecoder();
    let buffer = '';

    (async () => {
      try {
        const response = await fetch(`${gatewayUrl}/api/v1/sessions/${encodeURIComponent(sessionId)}/invoke-stream`, {
          method: 'POST',
          headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
          body: JSON.stringify({ content }),
          signal: controller.signal
        });

        if (!response.ok) {
          const text = await response.text();
          throw new Error(extractErrorMessage(text.length > 0 ? JSON.parse(text) : null, response.statusText));
        }

        const reader = response.body?.getReader();
        if (!reader) throw new Error('No response body for streaming');

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });

          const lines = buffer.split('\n');
          buffer = lines.pop() ?? '';

          for (const line of lines) {
            if (line.startsWith('data: ')) {
              const json = line.slice(6).trim();
              if (json) {
                try {
                  onChunk(JSON.parse(json));
                } catch {
                  // Skip malformed JSON
                }
              }
            }
          }
        }
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        onError?.(err instanceof Error ? err : new Error(String(err)));
      }
    })();

    return controller;
  },

  connectEvents(sessionId: string | null, onEvent: (event: EventEnvelope) => void): EventSource {
    const params = sessionId ? `?session_id=${encodeURIComponent(sessionId)}` : '';
    const source = new EventSource(`${gatewayUrl}/api/v1/events${params}`);
    source.onmessage = (message) => onEvent(JSON.parse(message.data));
    source.addEventListener('project.created', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('session.created', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('message.created', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('approval.requested', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('approval.approved', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('approval.rejected', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('tool.shell.approval_required', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('run.started', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('task_graph.created', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('task.assigned', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('step.result.created', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('supervision.checked', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    source.addEventListener('context.pack.created', (message) => onEvent(JSON.parse((message as MessageEvent).data)));
    return source;
  }
};
