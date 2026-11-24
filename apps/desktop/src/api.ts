import { readBackendResponse } from '@/lib/backendRequest'
import i18n from '@/i18n'
import type { AgentPackEnvelope } from '@/agentPacks/GraphSeedPack'
import { CORE_EVENT_TYPES } from '@/events/coreEventTypes'
import type { components } from '@/generated/schema'
import type { MessageAttachmentDto, MessageDto, SseChunk } from '@/generated/client'
import { createRunStream, runStreamDelta, type RunStreamHandle } from '@/lib/runStream'
import { ApiError } from '@/lib/apiError'
import { assertHostAccess } from '@/lib/hostAccess'
import { captureStorageId, rememberStorageResult, normalizeStorageRequest, storageHeaders, storageFetch, projectStorageId, sessionStorageId, selectionIdentity, selectedStorageId, type StorageRequestOptions } from '@/lib/storageScope'
import { cachedRead, invalidateReadCache, CATALOG_READ_CACHE_TTL_MS } from '@/lib/readCache'
import type { StorageScopeDto, StorageStatsDto, StorageCleanupPreviewDto, StorageContentPreviewDto, StorageDeletePreviewDto, SessionTransferDto, ConfigurationDocumentDto, StorageConfigureInput } from '@/settings/storage'
import { ScopedEventSource } from '@/lib/scopedEventSource'
type StorageDiagnosticResult = { storage_id: string; scope: StorageScopeDto; diagnostics: Array<Record<string, unknown>>; configuration: { content_hash: string; documents: Record<string, string> } }
import type { ToolSettingsDocument, ToolSettingsSchema, ToolSettingsEffective, ToolMcpResource, ToolMcpInput, ToolSkillResource, ToolCapabilities } from '@/settings/toolSettings'
export type { ToolSettingsDocument, ToolSettingsSchema, ToolSettingsEffective, ToolMcpResource, ToolMcpInput, ToolSkillResource, ToolCapabilities } from '@/settings/toolSettings'

export type { MessageDto }

export type TinaChatObservedConversation = components['schemas']['TinaChatObservedConversationDto']
export type TinaChatObservedConversationPage = components['schemas']['TinaChatObservedConversationPage']
export type TinaChatObservedDetail = components['schemas']['TinaChatObservedConversationDetail']
export type TinaChatObservedMessage = components['schemas']['TinaChatObservedMessageDto']
export type TinaChatObservedMessagePage = components['schemas']['TinaChatObservedMessagePage']
export type TinaChatParticipant = components['schemas']['TinaChatParticipantDto']
export type TinaChatConversation = components['schemas']['TinaChatConversationDto']
export type TinaChatMember = components['schemas']['TinaChatMemberDto']
export type TinaChatMessage = components['schemas']['TinaChatMessageDto']
export type TinaChatInboxPage = components['schemas']['TinaChatInboxPage']
export type TinaChatIntent = components['schemas']['TinaChatIntentDto']
export type TinaChatIntentContent = components['schemas']['TinaChatIntentContent']
export type TinaChatExecution = components['schemas']['TinaChatExecutionDto']
export type TinaChatWorkspacePolicy = components['schemas']['TinaChatWorkspacePolicyDto']

export interface ProjectDto {
  roots?: import('@/lib/workspaces').WorkspaceRoot[] | null;
  files?: import('@/lib/workspaces').WorkspaceFileGrant[] | null;
  primary_root_id?: string | null;
  icon?: string;
  color?: string;
  configuration_hash?: string | null;
  storage_root?: string | null;
  external?: boolean | null;
  availability?: 'ready' | 'error';
  availability_error?: string;
  availability_code?: string;
  category?: import('@/lib/apiError').ApiErrorCategory;
  retryable?: boolean;
  actions?: import('@/lib/apiError').ApiErrorActionKind[];
  trace_id?: string;
  storage_id?: string;
  id: string;
  name: string;
  path: string;
  created_at: string;
  lifecycle_status?: 'active' | 'archived' | 'trashed' | null;
  trashed_at?: string | null;
}

/** What Gateway's health answer says about each service; see `api.healthProbe`. */
export interface HealthProbeDto {
  gateway: boolean
  core: boolean
  core_version: string | null
}

export interface SessionDto {
  storage_id?: string;
  /** Slug of the agent that answers the user in this session, frozen when the session was created. */
  conversation_template_slug?: string | null;
  /** Creation-time conversation family. Older servers/rows default to flat. */
  view_mode?: 'flat' | 'space';
  id: string;
  /** Null for a free conversation created without a workspace (Codex-style). */
  project_id: string | null;
  title: string;
  status: string;
  mode_version_id?: string | null;
  meeting_model_override?: MeetingModelOverrideDto | null;
  permission_mode: string;
  space_options: SpaceOptionsDto | null;
  settings_revision: number;
  created_at: string;
  updated_at: string;
  lifecycle_status?: 'active' | 'archived' | 'trashed';
  trashed_at?: string | null;
  origin?: string | null;
  board_task_id?: string | null;
}

export interface CreateSessionInput extends SessionSettingsUpdate {
  project_id?: string | null;
  title?: string;
  mode_version_id?: string | null;
  view_mode?: 'flat' | 'space';
  origin?: string | null;
  board_task_id?: string | null;
}

/** Persisted by Core and captured with each submitted task, including queued tasks. */
export type SpaceOptionsDto = components['schemas']['SpaceOptions'];

export interface SessionSettingsUpdate {
  expected_settings_revision?: number;
  mode_version_id?: string | null;
  permission_mode?: string;
  meeting_model_override?: MeetingModelOverrideDto | null;
  clear_meeting_model_override?: boolean;
  clear_mode_version?: boolean;
  space_options?: SpaceOptionsDto | null;
}

export interface ComposerSubmitOptions extends SessionSettingsUpdate {
  dispatch_mode?: DispatchMode;
  target_run_id?: string | null;
}

/** Hand-written: the revert endpoint returns a bare object, so no generated type exists. */
export interface SessionHistoryRevertDto {
  from_message_id: string;
  from_sequence: number;
  removed_count: number;
  history_revision: number;
}

export interface ApprovalDto {
  id: string;
  session_id?: string | null;
  kind: string;
  tool_id?: string | null;
  risk?: string | null;
  summary: string;
  /** Set when `summary` is one of Core's fixed sentences; the UI words it from `approval.summaryCode.*` with `tool_id`. */
  summary_code?: string | null;
  /**
   * Redacted, bounded projection of the tool parameters, minted with the approval.
   * Bulk values (file bodies, patches, MCP payloads) arrive as size plus hash and
   * secret-shaped keys never leave Core, so this is safe to render verbatim.
   */
  arguments?: string | null;
  command?: string | null;
  cwd?: string | null;
  resource_path?: string | null;
  status: string;
  /** Core user-action state kept separate from the approval projection. */
  governance_status?: string | null;
  created_at: string;
  decided_at?: string | null;
  decision?: string | null;
  decision_reason?: string | null;
  run_id?: string | null;
}

/** Core ApprovalRuleDto: active, persisted rules; never inferred from approval history. */
export interface ApprovalRuleDto {
  id: string;
  kind: string;
  tool_id: string;
  pattern?: string | null;
  session_id?: string | null;
  created_by_principal_id: string;
  created_at: string;
  use_count: number;
}

export interface CreateApprovalRuleInput {
  kind: 'command_prefix' | 'delegate_tool'
  tool_id: string
  pattern?: string | null
  session_id?: string | null
}

export interface PreAuthorizationDto {
  id: string;
  run_id: string;
  lane_key?: string | null;
  tool_scope: string[];
  parameter_constraint_hash?: string | null;
  risk_max: string;
  max_uses: number;
  use_count: number;
  expires_at: string;
  revoked: boolean;
}

export interface CreatePreAuthorizationInput {
  run_id: string;
  lane_key?: string | null;
  tool_scope: string[];
  parameter_constraint_hash?: string | null;
  risk_max?: string;
  max_uses: number;
  expires_at?: string | null;
  summary?: string | null;
}

export type GovernanceRequestStatus =
  | 'pending'
  | 'awaiting_delegate'
  | 'awaiting_user'
  | 'awaiting_approval'
  | 'approved'
  | 'denied'
  | 'expired'
  | 'revoked'
  | 'blocked'
  | string;

/** A persisted request for a capability that is not currently available. */
export interface PermissionRequestDto {
  id: string;
  tenant_id: string;
  workspace_id: string;
  subject_principal_id: string;
  subject_agent_instance_id?: string | null;
  parent_agent_instance_id?: string | null;
  capability: string;
  action: string;
  resource: string;
  run_id?: string | null;
  task_id?: string | null;
  risk: string;
  expected_cost: number;
  status: GovernanceRequestStatus;
  authorization_decision_id?: string | null;
  capability_grant_id?: string | null;
  capability_lease_id?: string | null;
  expires_at: string;
  created_at: string;
  updated_at: string;
}

export interface AuthorizationDecisionDto {
  id: string;
  tenant_id: string;
  workspace_id: string;
  subject_principal_id: string;
  subject_agent_instance_id?: string | null;
  capability: string;
  action: string;
  resource: string;
  run_id?: string | null;
  task_id?: string | null;
  permission_request_id?: string | null;
  capability_grant_id?: string | null;
  capability_lease_id?: string | null;
  outcome: string;
  reason_code: string;
  reason: string;
  decision_source: string;
  policy_snapshot_hash: string;
  decided_by_principal_id: string;
  decided_by_agent_instance_id?: string | null;
  created_at: string;
}

export interface CapabilityGrantDto {
  id: string;
  tenant_id: string;
  workspace_id: string;
  subject_principal_id: string;
  subject_agent_instance_id?: string | null;
  capability: string;
  action: string;
  resource: string;
  run_id?: string | null;
  task_id?: string | null;
  parent_grant_id?: string | null;
  transferable: boolean;
  status: string;
  max_uses: number;
  use_count: number;
  starts_at: string;
  expires_at: string;
  revoked_at?: string | null;
  revoke_reason?: string | null;
}

export interface CapabilityRuleDto {
  effect: string;
  capability: string;
  action: string;
  resource_pattern: string;
}

export interface ApprovalDelegationDto {
  id: string;
  tenant_id: string;
  workspace_id: string;
  delegated_by_principal_id: string;
  delegate_agent_version_id: string;
  delegate_agent_instance_id: string;
  rules: CapabilityRuleDto[];
  max_risk: string;
  max_cost: number;
  run_id?: string | null;
  require_user_review: boolean;
  status: string;
  max_uses: number;
  use_count: number;
  starts_at: string;
  expires_at: string;
  revoked_at?: string | null;
  revoke_reason?: string | null;
}

/** Lease responses intentionally do not expose the internal one-time nonce. */
export interface CapabilityLeaseDto {
  id: string;
  tenant_id: string;
  workspace_id: string;
  subject_principal_id: string;
  subject_agent_instance_id?: string | null;
  capability_grant_id: string;
  permission_request_id?: string | null;
  capability: string;
  action: string;
  resource: string;
  run_id?: string | null;
  task_id?: string | null;
  policy_snapshot_hash: string;
  status: string;
  max_uses: number;
  use_count: number;
  starts_at: string;
  expires_at: string;
  revoked_at?: string | null;
  revoke_reason?: string | null;
}

export interface PermissionResolutionDto {
  request: PermissionRequestDto;
  decision: AuthorizationDecisionDto;
  grant?: CapabilityGrantDto | null;
  lease?: CapabilityLeaseDto | null;
}

export interface PermissionDecisionInput {
  approve: boolean;
  approver_agent_instance_id?: string | null;
  approval_delegation_id?: string | null;
  reason?: string | null;
}

export interface CreateCapabilityGrantInput {
  subject_principal_id: string;
  subject_agent_instance_id?: string | null;
  capability: string;
  action: string;
  resource: string;
  run_id?: string | null;
  task_id?: string | null;
  expires_at: string;
  max_uses?: number;
  transferable?: boolean;
  parent_grant_id?: string | null;
  reason?: string | null;
}

export interface CreateApprovalDelegationInput {
  delegate_agent_version_id: string;
  delegate_agent_instance_id: string;
  rules?: CapabilityRuleDto[];
  max_risk?: string | null;
  max_cost: number;
  max_uses?: number;
  expires_at: string;
  run_id?: string | null;
  require_user_review?: boolean;
}

export interface GovernanceRevokeInput {
  reason?: string | null;
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
  protocol?: string | null;
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

export interface ModelParametersDto {
  reasoning_effort?: 'none' | 'low' | 'medium' | 'high' | 'xhigh' | null;
  temperature?: number | null;
  top_p?: number | null;
  max_output_tokens?: number | null;
}

export type ModelSpecialty =
  | 'coding'
  | 'architecture'
  | 'research'
  | 'review'
  | 'writing'
  | 'vision'
  | 'tool_calling'
  | 'fast_qa'
  | 'long_context'
  | string;

export type ModelProfileTier = 'fast' | 'balanced' | 'deep';
export type ModelCostTier = 'low' | 'medium' | 'high';

export interface ModelProfileDto {
  specialties: ModelSpecialty[];
  tier: ModelProfileTier;
  notes?: string | null;
  context_window?: number | null;
  cost_tier?: ModelCostTier | null;
}

export interface ImageInputSettingsDto {
  enabled?: boolean;
  max_image_bytes?: number;
  max_images_per_turn?: number;
  model_overrides?: Record<string, Omit<ImageInputSettingsDto, 'model_overrides'>>;
}

export interface ImageInputCapabilityDto extends ImageInputSettingsDto {
  enabled: boolean;
  max_image_bytes: number;
  max_images_per_turn: number;
  base_enabled: boolean;
  base_max_image_bytes: number;
  base_max_images_per_turn: number;
  editable: boolean;
  source: 'config' | 'acp_snapshot' | 'protocol_unsupported';
}

export interface ModelProviderInstanceDto {
  id: string;
  driver: string;
  protocol?: string | null;
  /**
   * Which harness channel the row is configured on: `cli`, `tui`, or `acp`. Core stores it in the
   * provider configuration and resolves `protocol` from the (driver, channel) pair, so a row without
   * a channel is an HTTP API provider or a provider stored before the channel vocabulary existed.
   */
  channel?: string | null;
  display_name: string;
  connection_kind: 'api-key' | 'cli' | 'local-server' | string;
  base_url?: string | null;
  model?: string | null;
  models?: string[];
  model_parameters?: Record<string, ModelParametersDto>;
  model_profiles?: Record<string, ModelProfileDto>;
  image_input?: ImageInputCapabilityDto;
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
  revision?: number;
  created_at: string;
  updated_at: string;
}

export interface ModelRouteDto {
  id?: string;
  purpose: string;
  version_id?: string;
  version?: number;
  candidates: ModelRouteCandidateDto[];
  revision?: number;
  updated_at: string;
}

export interface ModelRouteCandidateDto {
  provider_instance_id: string;
  model?: string | null;
  position: number;
}

export interface ModelRouteWriteRequestDto {
  candidates: Array<{ provider_instance_id: string; model?: string | null }>;
}

export interface MeetingModelOverrideDto {
  provider_instance_id: string;
  model?: string | null;
}

export interface ModelResolutionPreviewRequestDto {
  strategy?: { kind: 'inherit' | 'route' | 'fixed'; route_purpose?: string; provider_instance_id?: string; model?: string | null } | null;
  meeting_model_override?: MeetingModelOverrideDto | null;
  agent_definition_id?: string | null;
  agent_version_id?: string | null;
  mode_version_id?: string | null;
  node_key?: string | null;
  parent_instance_id?: string | null;
}

export interface ModelResolutionStepDto {
  source: string;
  strategy: Record<string, unknown>;
  selected: boolean;
}

export interface ModelResolutionCandidatePreviewDto {
  position: number;
  provider_instance_id: string;
  provider_version_id?: string | null;
  route_id?: string | null;
  route_version_id?: string | null;
  model?: string | null;
  protocol?: string | null;
  available: boolean;
  unavailable_reason?: string | null;
}

export interface ModelResolutionPreviewDto {
  strategy_source: string;
  chain: ModelResolutionStepDto[];
  candidates: ModelResolutionCandidatePreviewDto[];
  expected_selection?: ModelResolutionCandidatePreviewDto | null;
}

export interface ModelReferenceDto {
  reference_kind: string;
  reference_id?: string | null;
  reference_key?: string | null;
  provider_instance_id: string;
  model?: string | null;
  detail?: string | null;
  last_used_at?: string | null;
}

export interface ModelInvocationDto {
  id: string;
  call_id: string;
  attempt: number;
  session_id: string;
  run_id: string;
  turn_id?: string | null;
  agent_instance_id?: string | null;
  agent_definition_id: string;
  agent_version_id: string;
  mode_version_id: string;
  strategy_source: string;
  route_id?: string | null;
  route_version_id?: string | null;
  provider_instance_id: string;
  provider_version_id: string;
  model?: string | null;
  protocol: string;
  fallback_position: number;
  status: string;
  error_category?: string | null;
  safe_error_message?: string | null;
  input_tokens?: number | null;
  output_tokens?: number | null;
  total_tokens?: number | null;
  started_at: string;
  completed_at?: string | null;
}

export interface ModelInvocationPageDto {
  items: ModelInvocationDto[];
  next_cursor?: string | null;
}

export interface ModelDiscoveryResultDto {
  models: Array<{ id: string; display_name: string }>;
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
  protocol?: string | null;
  display_name: string;
  connection_kind: string;
  base_url?: string | null;
  model?: string | null;
  models?: string[];
  model_parameters?: Record<string, ModelParametersDto> | null;
  model_profiles?: Record<string, ModelProfileDto> | null;
  image_input?: ImageInputSettingsDto | null;
  api_key?: string | null;
  clear_api_key?: boolean;
  binary_path?: string | null;
  home_path?: string | null;
  server_url?: string | null;
  launch_args?: string | null;
  capabilities?: string[];
  enabled?: boolean;
}

/** One fixed check of Core's unified readiness receipt (database, model_route, tool_provider, …). */
export interface ReadinessItemDto {
  id: string;
  /** ready | degraded | blocked | unavailable */
  status: string;
  /** What `reason` is about; the UI words it from `doctor.reason.*` / `doctor.action.*` and `data`. */
  reason_code?: string | null;
  reason?: string | null;
  action?: string | null;
  checked_at?: string;
  data?: Record<string, unknown> | null;
}

/** GET /api/v1/readiness: the overall status (ready | degraded | blocked) and the fixed items. */
export interface RuntimeReadinessReceiptDto {
  status: string;
  checked_at?: string;
  items: ReadinessItemDto[];
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

/**
 * Stable renderer-side shape delivered by connectEvents(). Normalized from the Core wire
 * envelope (TinadecCore/Contracts/Events/EventEnvelope.cs: version/event_id/event_type/
 * timestamp/session_id/run_id/payload.sequence + SSE id line) by normalizeEventEnvelope(),
 * which guarantees type/seq/ts/v are always present. request_id/trace_id/capabilities are
 * not part of the Core contract and default to ''/[].
 */
export interface EventEnvelope {
  run_id?: string | null;
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

/**
 * The two answers `source` can carry. Kept as constants because the whole point of the field is
 * that a page must branch on it: an empty `servers` list under `tool_provider` means nothing is
 * configured, and under `tool_provider_unavailable` means Core could not ask the Tool Provider.
 * Typed as a string on the DTO — Core owns the vocabulary and may extend it, and this renderer
 * would rather show an unknown source honestly than fail a type assertion at a boundary.
 */
export const MCP_SOURCE_PROVIDER = 'tool_provider';
export const MCP_SOURCE_UNAVAILABLE = 'tool_provider_unavailable';

export interface McpToolDto {
  id: string;
  name: string;
  description?: string | null;
  /** Absent on the inventory route, which deliberately does not ask for schemas. */
  input_schema?: unknown;
}

export interface McpServerDto {
  id: string;
  name: string;
  /** Passed through from the provider (`connected` / `error`); never upgraded by Core. */
  status: string;
  /** The provider's own failure text for this server. Absent when it answered. */
  error?: string | null;
  tools: McpToolDto[];
}

export interface McpInventoryDto {
  source: string;
  reason?: string | null;
  workspace_root?: string | null;
  /** The file the Tool Provider read, as it reported it. */
  config_path?: string | null;
  /** Rows Core could not identify. Present only when something was dropped. */
  dropped_rows?: number | null;
  servers: McpServerDto[];
}

export interface McpServerToolsDto {
  source: string;
  reason?: string | null;
  workspace_root?: string | null;
  config_path?: string | null;
  server_id: string;
  /** Present only when the inventory read completed and the id matched. */
  server?: McpServerDto | null;
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
  protocol?: string | null;
  display_name: string;
  connection_kind: string;
  transport_kind: string;
  credential_kind: string;
  base_url?: string | null;
  model?: string | null;
  models?: string[];
  model_parameters?: Record<string, ModelParametersDto>;
  image_input?: ImageInputCapabilityDto;
  has_api_key: boolean;
  server_url?: string | null;
  capabilities: string[];
  enabled: boolean;
  status: string;
  status_message: string;
  cooldown_until?: string | null;
  revision?: number | null;
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
  channel?: string | null;
  driver: string;
  display_name: string;
  protocol?: string | null;
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
  /** Provider row revision; required as the If-Match token for writes. */
  revision?: number | null;
  readiness?: Record<string, unknown> | null;
}

/**
 * One channel a discovered harness offers, as Core reports it. `drivable` is this build's answer, not
 * the vendor's: a channel can be genuinely supported by the binary and still have no client in Core
 * yet, and the model center must say so instead of offering a button that cannot work.
 */
export interface HarnessChannelDto {
  /**
   * The channel this affordance is reached on. `null` is the harness's own long-lived local HTTP
   * server (opencode's `serve`), which is not a channel: it is reached over HTTP, so it has no argv
   * channel to name and is labelled by its protocol instead.
   */
  channel: 'cli' | 'tui' | 'acp' | string | null;
  protocol: string;
  drivable: boolean;
  reason?: string | null;
  /** What `reason` is about; the UI words it from `settings.channelReason.*`. */
  reason_code?: string | null;
}

export interface CliDiscoveryCandidateDto {
  driver: string;
  display_name: string;
  binary_path: string | null;
  /** Where the harness keeps its own configuration, when this host has it. Deliberately not the process HOME. */
  config_home?: string | null;
  server_url?: string | null;
  launch_args?: string | null;
  status: 'found' | 'missing' | 'configured';
  /** Channels already persisted for this harness, so another channel can be added without hiding it. */
  configured_channels?: string[];
  /**
   * Why a probe did not confirm this binary — it exited non-zero, or it never answered within the
   * harness's own ceiling. `missing` on its own reads as "not installed", which is a different claim.
   */
  probe_note?: string | null;
  /** What `probe_note` is about; the UI words it from `settings.probeNote.*` and `probe_note_data`. */
  probe_note_code?: string | null;
  probe_note_data?: Record<string, string> | null;
  vendor?: string | null;
  channels?: HarnessChannelDto[];
  caveats?: string[];
}

export interface ConnectHarnessInput {
  driver: string
  binary_path: string
  /** Required for a harness: the channel decides the protocol and the argv Core starts it with. */
  channel?: string | null
  /** The wire protocol discovered for this affordance; required when there is no channel to derive it from. */
  protocol?: string | null
  display_name?: string
  home_path?: string | null
  server_url?: string | null
  launch_args?: string | null
}

export interface CliDiscoveryResultDto {
  cli_runtimes: CliDiscoveryCandidateDto[];
}

export interface ModelCenterAcpRuntimeDto {
  id: string;
  runtime_id: string;
  source: 'adapter' | 'provider_instance' | string;
  adapter_id?: string | null;
  provider_instance_id?: string | null;
  extension_id?: string | null;
  driver?: string | null;
  display_name: string;
  command?: string | null;
  protocol?: string | null;
  binary_path?: string | null;
  home_path?: string | null;
  status: string;
  status_message: string;
  capabilities: string[];
  enabled: boolean;
  route_purposes: string[];
  /** Provider row revision; required as the If-Match token for writes. */
  revision?: number | null;
  updated_at?: string | null;
  readiness?: Record<string, unknown> | null;
}

/**
 * A harness configured on the interactive `tui` channel. This is an inventory row: a PTY session is
 * observed, injected into and audited — never driven as a chat model — and this build has no host PTY
 * backend, so nothing here is actionable until the TinadecTools backend lands.
 */
export interface ModelCenterTuiRuntimeDto {
  id: string;
  runtime_id: string;
  provider_instance_id: string;
  source: 'provider_instance' | string;
  channel: 'tui' | string;
  driver: string;
  display_name: string;
  protocol?: string | null;
  binary_path?: string | null;
  home_path?: string | null;
  launch_args?: string | null;
  capabilities: string[];
  enabled: boolean;
  status: string;
  status_message: string;
  route_purposes: string[];
  /** Provider row revision; required as the If-Match token for writes. */
  revision?: number | null;
  updated_at?: string | null;
  readiness?: Record<string, unknown> | null;
}

export interface ModelCenterOverviewDto {
  capabilities: ModelCenterCapabilitiesDto;
  suppliers: ModelCenterSupplierDto[];
  api_connections: ModelCenterApiConnectionDto[];
  models: ModelCenterModelDto[];
  cli_runtimes: ModelCenterCliRuntimeDto[];
  tui_runtimes: ModelCenterTuiRuntimeDto[];
  acp_runtimes: ModelCenterAcpRuntimeDto[];
  readiness: {
    model?: ModelReadinessReceiptDto | null;
    catalog?: ModelCatalogReadinessReceiptDto | null;
  };
  diagnostics: CenterDiagnosticDto[];
}

/**
 * One partition of the cold-start snapshot. `ok: false` is a section-level degradation, not
 * a failed screen: the caller keeps what it already had for that section only.
 */
export interface ModelCenterSnapshotPartDto<T> {
  ok: boolean;
  status: number;
  data?: T;
  error?: unknown;
}

export interface ModelCenterSnapshotDto {
  parts: {
    providers: ModelCenterSnapshotPartDto<ModelProviderInstanceDto[]>;
    templates: ModelCenterSnapshotPartDto<ModelProviderTemplateDto[]>;
    routes: ModelCenterSnapshotPartDto<ModelRouteDto[]>;
    acp_adapters: ModelCenterSnapshotPartDto<AcpAdapterDto[]>;
    model_readiness: ModelCenterSnapshotPartDto<ModelReadinessReceiptDto>;
    catalog_readiness: ModelCenterSnapshotPartDto<ModelCatalogReadinessReceiptDto>;
    readiness: ModelCenterSnapshotPartDto<RuntimeReadinessReceiptDto>;
  };
}

export type AgentRuntimeSelectionKind = 'inherit' | 'route' | 'fixed_model';

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

export interface AgentCenterAgentDto extends AgentViewDto {
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

export interface AgentViewDto {
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
  revision?: number | null;
  updated_at: string | null;
  /** Versioned projection (Core ToAgentDto) — present on GET /agents since D7.4. */
  slug?: string;
  display_name?: string;
  i18n?: PackI18n | null;
  role?: string;
  source_kind?: AgentSourceKind | string;
  provenance?: AgentProvenanceDto | null;
  base_prompt_pipeline_id?: string | null;
  tool_scope?: string[] | string | null;
  model_strategy?: Record<string, unknown> | string | null;
  /**
   * 用户级运行时绑定覆盖（Core `agent_runtime_bindings`）。这才是「智能体中心设的模型」
   * 的真值 —— `model_strategy` 只是 agent 定义里的策略，运行时绑定写入不会改动它。
   */
  model_binding?: AgentModelBindingDto | null;
  status?: string;
  version?: number | null;
}

export interface AgentModeUsageDto {
  mode_id: string;
  mode_version_id?: string | null;
  mode_slug: string;
  node_key: string;
  model_strategy_override?: Record<string, unknown> | null;
}

/** One locale's overlay for a pack resource; every field is optional and empty means absent. */
export interface PackI18nOverlay {
  display_name?: string | null;
  description?: string | null;
  label?: string | null;
  system_prompt?: string | null;
  templates?: Record<string, string> | null;
}

/** Per-locale overlays keyed by Core locale (`zh-CN`, `zh-TW`, `en`, `ja`, `ru`). */
export type PackI18n = Record<string, PackI18nOverlay>

export type AgentSourceKind = 'pack' | 'custom' | 'agent_generated';

export interface AgentProvenanceDto {
  created_by_agent_id?: string | null;
  created_by_run_id?: string | null;
  reason?: string | null;
}

export type OrchestrationTier = 'solo_dispatch' | 'free_form' | 'self_dispatch' | 'deterministic';
export type AgentOrchestrationTier = OrchestrationTier;

export interface AgentDirectoryItemDto {
  id: string;
  slug: string;
  display_name: string;
  /** Per-locale overlays; absent for rows that ship none. */
  i18n?: PackI18n | null;
  layer: 'operation' | 'execution' | string;
  role: string;
  source_kind: AgentSourceKind | 'bootstrap' | 'missing_reference' | string;
  source_key: string;
  managed: boolean;
  writable: boolean;
  enabled: boolean;
  status: string;
  revision: number;
  version: number;
  current_version_id?: string | null;
  provenance?: AgentProvenanceDto | null;
  base_prompt_pipeline_id?: string | null;
  pack_id?: string | null;
  pack_managed?: boolean;
  pack_disabled?: boolean;
  configured_strategy: { kind: 'inherit' | 'route' | 'fixed'; route_purpose?: string; provider_instance_id?: string; model?: string | null };
  mode_usages: AgentModeUsageDto[];
  effective_previews: Record<string, ModelResolutionPreviewDto>;
  recent_invocation?: ModelInvocationDto | null;
  updated_at: string;
  /** 用户级运行时绑定覆盖（null = 未覆盖，跟随定义策略/默认路由）。 */
  model_binding?: AgentModelBindingDto | null;
}

/**
 * Core 的 `agent_runtime_bindings` 覆盖记录（`AgentRuntimeBindingDto`）。
 * 与本文件里的桌面视图模型 `AgentRuntimeBindingDto` 同名不同形状 —— 那个是渲染用的
 * 派生结构，这个是服务端真值，别混用。
 */
export interface AgentModelBindingDto {
  mode: 'inherit' | 'route' | 'fixed';
  provider_instance_id?: string | null;
  model?: string | null;
  route_purpose?: string | null;
  tool_scope_override?: string[] | null;
  revision: number;
  updated_at: string;
}

export interface AgentModeDto {
  id: string;
  slug?: string;
  display_name: string;
  summary?: string;
  description?: string | null;
  i18n?: PackI18n | null;
  orchestration_tier?: AgentOrchestrationTier | null;
  /** Run switches the mode presets; a composer override is per-session and never written back here. */
  default_options?: SpaceOptionsDto | null;
  latest_published_mode_version_id?: string | null;
  max_parallel_executors?: number;
  worktree_isolation?: boolean;
  approval_required?: boolean;
  budget_policy?: string;
  status?: string;
  managed?: boolean;
  pack_id?: string | null;
  pack_managed?: boolean;
  pack_disabled?: boolean;
  revision?: number;
  version?: number;
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

// ── New config objects (snake_case, If-Match via etag/revision) ──
export interface AgentDefinitionDto {
  id: string;
  /** Flat legacy shape (pre-directory projection). */
  name?: string;
  /** Versioned AgentDefinition shape (Core ToAgentDto). */
  slug?: string;
  display_name?: string;
  /** Per-locale overlays; absent for rows that ship none. */
  i18n?: PackI18n | null;
  layer: 'operation' | 'execution' | string;
  /** Legacy flat shape. */
  agent_type?: string;
  /** Versioned shape. */
  role?: string;
  source_kind?: AgentSourceKind | string;
  source_key?: string | null;
  managed?: boolean;
  writable?: boolean;
  provenance?: AgentProvenanceDto | null;
  base_prompt_pipeline_id?: string | null;
  pack_id?: string | null;
  pack_managed?: boolean;
  pack_disabled?: boolean;
  model_route_purpose?: string | null;
  /** Versioned shape: { kind: inherit|route|fixed, ... }. */
  model_strategy?: Record<string, unknown> | 'inherit' | 'route' | 'fixed' | string | null;
  model_binding?: AgentModelBindingDto | null;
  /** Legacy flat shape. */
  allowed_tools?: string[];
  /** Versioned shape: string[] or "*". */
  tool_scope?: string[] | string | null;
  capabilities?: string[];
  system_prompt?: string | null;
  description?: string | null;
  enabled?: boolean;
  is_built_in?: boolean;
  status?: string;
  revision?: number | null;
  version?: number | null;
  etag?: string | null;
  created_at?: string | null;
  updated_at?: string | null;
}

export interface WorkspaceDefaultsDto {
  id?: string;
  default_agent_id?: string | null;
  default_agent_definition_id?: string | null;
  default_agent_version_id?: string | null;
  default_agent_mode_id?: string | null;
  default_mode_version_id?: string | null;
  default_prompt_pipeline_id?: string | null;
  default_prompt_version_id?: string | null;
  status?: string;
  revision?: number | null;
  etag?: string | null;
}

export type AgentPackPreviewAction =
  | 'install'
  | 'upgrade'
  | 'up_to_date'
  | 'newer_installed'
  | 'conflict'
  | string

export interface AgentPackResourceCountsDto {
  agents: number
  prompt_pipelines: number
  modes: number
  created?: number
  adopted?: number
  reused?: number
  updated?: number
}

export interface AgentPackDto {
  pack_id: string
  owner: string
  product_id?: string | null
  name?: string | null
  status: string
  active_version: string | null
  integrity_digest: string | null
  revision: number
  installed_at: string
  updated_at: string
  etag?: string | null
  /** 工作区默认模式版本是否由本包提供（null = 本包从未接管过默认）。 */
  default_mode_version_id?: string | null
}

/** 每张表的删除计数；SQLite 无外键，计数是删除顺序可验证的证据。 */
export interface AgentPackPurgeDto {
  pack_id: string
  revision: number
  deleted: Record<string, number>
}

export interface AgentPackInstallPreviewDto {
  preview_id: string | null
  pack_id: string
  owner: string
  action: AgentPackPreviewAction
  bundled_version: string
  installed_version: string | null
  integrity_digest: string
  revision: number
  etag?: string | null
  expires_at: string | null
  counts: AgentPackResourceCountsDto
  resources?: AgentPackResourceBindingDto[]
  defaults_will_adopt?: boolean
  required_core_version: string | null
  current_core_version: string
  differences?: string[]
  warnings: string[]
}

export interface AgentPackResourceBindingDto {
  kind: 'agent' | 'prompt_pipeline' | 'mode' | string
  resource_key: string
  logical_entity_id: string | null
  version_id: string | null
  content_hash: string | null
  disposition: 'created' | 'adopted' | 'reused' | 'updated' | 'conflict' | string
}

export interface AgentPackDetailDto extends AgentPackDto {
  versions: Array<Record<string, unknown>>
  resources: AgentPackResourceBindingDto[]
}

export interface AgentPackInstallResultDto {
  status: 'installed' | 'updated' | 'up_to_date' | 'newer_installed' | string
  pack_id: string
  owner: string
  active_version: string
  integrity_digest: string
  revision: number
  counts: AgentPackResourceCountsDto
  resources?: AgentPackResourceBindingDto[]
  defaults_adopted?: boolean
  installed_at: string
  updated_at: string
  etag?: string | null
}

export interface AgentVersionDto {
  id: string;
  agent_id: string;
  version: number;
  content?: Record<string, unknown> | null;
  change_summary?: string | null;
  is_active?: boolean;
  created_at: string;
}

export interface AgentModeBindingDto {
  node_key?: string | null;
  agent_ref?: string | null;
  duty_description_ref?: string | null;
  tool_switches?: Record<string, boolean | string> | null;
  envelope?: Record<string, unknown> | null;
  includes_core_reserved?: boolean;
  instance_naming?: Record<string, unknown> | null;
}

export interface AgentModeNodeDto {
  id: string;
  agent_id: string;
  /** Core's read projection names the agent id `agent_definition_id`. */
  agent_definition_id?: string | null;
  lane: 'operation' | 'execution';
  layer?: 'operation' | 'execution';
  position: { x: number; y: number };
  label?: string | null;
  /** Per-locale overlays; absent for nodes that ship none. */
  i18n?: PackI18n | null;
  config?: Record<string, unknown> | null;
  relationship?: Record<string, unknown> | null;
  model_strategy_override?: { kind: 'inherit' | 'route' | 'fixed'; route_purpose?: string; provider_instance_id?: string; model?: string | null } | null;
  /** Core node_key for published projections; write-back uses it before id. */
  node_key?: string;
  data?: Record<string, unknown> | null;
}

export interface AgentModeEdgeDto {
  id: string;
  edge_key?: string | null;
  source: string;
  target: string;
  source_node_key?: string | null;
  target_node_key?: string | null;
  label?: string | null;
  condition?: Record<string, unknown> | null;
}

/** Core UpsertModeTopology write contract (node_key/agent_definition_id/layer). */
export interface AgentModeTopologyWriteDto {
  instructions?: string | null;
  orchestration_tier?: AgentOrchestrationTier | null;
  default_options?: SpaceOptionsDto | null;
  nodes: Array<{ node_key: string; agent_definition_id: string; layer: 'operation' | 'execution'; label?: string | null; position?: { x: number; y: number } | null; config?: Record<string, unknown> | null; relationship?: Record<string, unknown> | null; model_strategy_override?: Record<string, unknown> | null }>;
  edges: Array<{ edge_key?: string; source_node_key: string; target_node_key: string; condition?: Record<string, unknown> }>;
  bindings?: AgentModeBindingDto[];
  canvas_layout?: Record<string, unknown> | null;
  runtime_policy?: AgentModeRuntimePolicy | null;
}

/** Draft writes may rename the mode as well as replace its topology. */
export interface AgentModeDraftUpdate extends AgentModeTopologyWriteDto {
  display_name?: string | null;
  description?: string | null;
  orchestration_tier?: AgentOrchestrationTier | null;
}

/**
 * Numeric knobs for autonomous exploration. Every key is optional on the wire:
 * Core fills absent values with its frozen defaults. Keys are snake_case to match
 * the manifest and `runtime_policy.settings` exactly — the registry rejects
 * unknown keys instead of guessing an alias.
 */
export interface AutonomousExplorationSettings {
  planner_debounce_ms?: number;
  planner_heartbeat_ms?: number;
  max_parallel_workers?: number;
  max_intents_per_cycle?: number;
  max_worker_rounds?: number;
  digest_after_idle_ms?: number;
  context_compaction_tokens?: number;
  trace_result_limit?: number;
  coverage_min_ratio?: number;
  max_retries_per_intent?: number;
}

export type AgentModeRuntimePolicy =
  | { kind: 'structured_investigation'; version: 1; features: string[]; settings?: never }
  | { kind: 'autonomous_exploration'; version: 1; features: string[]; settings?: AutonomousExplorationSettings };

/** One host-owned policy from the Core registry; the editor never hard-codes these. */
export interface AgentModeRuntimePolicyDefinition {
  kind: AgentModeRuntimePolicy['kind'];
  version: 1;
  required_core_capability: string;
  features: string[];
  required_features: string[];
  dependencies: Array<{ feature: string; requires: string }>;
  default_settings?: AutonomousExplorationSettings | null;
}

export interface AgentModeTopologyDto {
  id: string;
  /** Stable mode id (for a pack mode, its manifest resource_key); keys the icon in modePresentation.ts. */
  slug?: string | null;
  display_name: string;
  /** 该 mode 最新已发布版本的 id —— 提交 interaction 时作为 mode_version_id 使用。 */
  latest_published_mode_version_id?: string | null;
  /** User-facing one-liner: what the mode does and when to pick it. */
  description?: string | null;
  /** Team-level collaboration instructions; injected into the conversation identity at run start. */
  instructions?: string | null;
  /** Per-locale overlays; absent for modes that ship none. */
  i18n?: PackI18n | null;
  orchestration_tier?: AgentOrchestrationTier | null;
  default_options?: SpaceOptionsDto | null;
  runtime_policy?: AgentModeRuntimePolicy | null;
  nodes: AgentModeNodeDto[];
  edges: AgentModeEdgeDto[];
  bindings?: AgentModeBindingDto[] | null;
  canvas_layout?: Record<string, unknown> | null;
  status?: string;
  /** True when an installed Agent Pack owns this mode (read-only; clone to customize). */
  managed?: boolean;
  pack_id?: string | null;
  pack_managed?: boolean;
  pack_disabled?: boolean;
  version?: number | null;
  revision?: number | null;
  etag?: string | null;
  created_at?: string | null;
  updated_at?: string | null;
}

export interface ModeVersionDto {
  id: string;
  mode_id: string;
  version: number;
  nodes?: AgentModeNodeDto[] | null;
  edges?: AgentModeEdgeDto[] | null;
  canvas_layout?: Record<string, unknown> | null;
  /** Frozen `tinadec.mode_version/v1` snapshot returned by Core. */
  snapshot?: {
    schema?: string;
    mode?: Record<string, unknown>;
    nodes?: Array<Record<string, unknown>>;
    edges?: Array<Record<string, unknown>>;
    bindings?: AgentModeBindingDto[];
    canvas_layout?: Record<string, unknown> | null;
    runtime_policy?: AgentModeRuntimePolicy | null;
    instructions?: string | null;
  } | null;
  topology_hash?: string | null;
  warning?: unknown;
  created_at: string;
  is_active?: boolean;
}

export interface PromptPipelineNodeDto {
  id: string;
  type?: 'template' | 'fragment_ref' | 'condition' | 'workspace_rules' | 'skills_index' | 'assemble' | string;
  kind?: string;
  label?: string | null;
  title?: string | null;
  key?: string | null;
  position?: { x: number; y: number } | null;
  x?: number;
  y?: number;
  config?: Record<string, unknown> | null;
  data?: Record<string, unknown> | null;
  [key: string]: unknown;
}

export interface PromptPipelineEdgeDto {
  id: string;
  source: string;
  target: string;
  label?: string | null;
  condition?: Record<string, unknown> | null;
  [key: string]: unknown;
}

export interface PromptPipelineGraphDto {
  nodes: PromptPipelineNodeDto[];
  edges: PromptPipelineEdgeDto[];
  canvas_layout?: Record<string, unknown> | null;
}

export interface PromptPipelineDto {
  id: string;
  slug?: string | null;
  display_name?: string | null;
  name?: string | null;
  title?: string | null;
  key?: string | null;
  description?: string | null;
  i18n?: PackI18n | null;
  scope?: string | null;
  status?: string;
  version?: number | null;
  revision?: number | null;
  etag?: string | null;
  graph?: PromptPipelineGraphDto | null;
  nodes?: PromptPipelineNodeDto[] | null;
  edges?: PromptPipelineEdgeDto[] | null;
  canvas_layout?: Record<string, unknown> | null;
  pack_id?: string | null;
  pack_managed?: boolean;
  pack_disabled?: boolean;
  created_at?: string | null;
  updated_at?: string | null;
}

export function normalizePromptPipeline(raw: PromptPipelineDto): PromptPipelineDto {
  const rawGraph = raw.graph && typeof raw.graph === 'object' ? raw.graph : null
  const nodes = Array.isArray(rawGraph?.nodes)
    ? rawGraph.nodes
    : Array.isArray(raw.nodes)
      ? raw.nodes
      : []
  const edges = Array.isArray(rawGraph?.edges)
    ? rawGraph.edges
    : Array.isArray(raw.edges)
      ? raw.edges
      : []
  const canvasLayout = rawGraph?.canvas_layout ?? raw.canvas_layout ?? null
  const displayName = raw.display_name ?? raw.name ?? raw.title ?? raw.slug ?? raw.id
  return {
    ...raw,
    display_name: displayName,
    name: raw.name ?? displayName,
    graph: {
      nodes,
      edges,
      ...(canvasLayout ? { canvas_layout: canvasLayout } : {}),
    },
    nodes,
    edges,
    canvas_layout: canvasLayout,
  }
}

export interface PromptPipelineVersionDto {
  id: string;
  pipeline_id?: string;
  prompt_pipeline_id?: string;
  version: number;
  graph?: PromptPipelineGraphDto | null;
  content?: Record<string, unknown> | null;
  content_hash?: string | null;
  created_at: string;
}

/**
 * Effective runtime policy ceilings (Core `GET /api/v1/agent-runtime-policy`). Every number is
 * editable only inside the scope's `runtime.toml`; `deviations` names the dotted keys that no
 * longer match the baseline this build ships, so the settings surface can say which value an
 * operator changed instead of presenting all of them as fixed facts.
 */
export interface AgentRuntimePolicyDto {
  revision: number;
  content_hash: string;
  loaded_at: string;
  source_path: string;
  document_id: string;
  diagnostic: { state: string; detail: string; checked_at: string };
  defaults_available: boolean;
  deviations: string[];
  spawn: { max_depth: number; max_agents_per_run: number; max_parallel_workers: number };
  scheduling: { max_active_runs_per_session: number; worker_retry_limit: number; preserve_partial_results: boolean };
  supervision: { required_before_final: boolean; max_revision_rounds: number };
  context: { default_token_budget: number; task_token_budget: number | null; effective_task_token_budget: number; run_token_budget: number; recent_message_limit: number };
  tools: { max_tool_calls: number; max_tool_rounds: number; default_timeout_seconds: number };
}

export interface AgentRuntimeInstanceDto {
  id: string;
  run_id: string;
  session_id?: string | null;
  agent_id?: string | null;
  agent_name?: string | null;
  status: string;
  lane?: string | null;
  parent_instance_id?: string | null;
  task_id?: string | null;
  source_definition?: { id: string; slug: string; display_name: string; source_kind: string; source_key: string; managed: boolean } | null;
  frozen_version?: { agent_version_id: string; content_hash: string } | null;
  recent_actual_model?: { invocation_id: string; provider_instance_id: string; provider_version_id: string; model?: string | null; protocol: string; route_id?: string | null; route_version_id?: string | null; mode_version_id?: string | null; strategy_source: string; fallback_position: number; completed_at?: string | null } | null;
  fallback_summary?: { call_id: string; attempts: number; failed_attempts: number; used_fallback: boolean } | null;
  created_at?: string | null;
  updated_at?: string | null;
}

/**
 * Receipt of `POST /sessions/{id}/interactions`. Core writes this body and the gateway
 * forwards it untouched, so these are the wire keys — not an idealised interaction record.
 * One type covers four outcomes and `status` is what tells them apart: a started run
 * (`run_id` + `turn_id`), a queued turn (`run_id`, no `turn_id`… `status: 'queued'`), a
 * steering insert (`status: 'steering_injected'`, the only branch that echoes `content`),
 * and an attachment-only message (`status: 'message_only'`, no run at all). Absence is the
 * wire representation of null: this host drops nulls on write, which is why the send path
 * tests `if (resp.run_id)` rather than comparing it to null.
 */
export interface SessionInteractionDto {
  interaction_id: string;
  session_id: string;
  status: 'queued' | 'steering_injected' | 'message_only' | string;
  run_id?: string;
  turn_id?: string;
  message_id?: string;
  dispatch_mode?: DispatchMode;
  mode_version_id?: string;
  meeting_model_override?: MeetingModelOverrideDto;
  client_message_id?: string;
  correlation_id?: string;
  content?: string;
  attachment_ids?: string[];
  context_revision?: number;
  stream_cursor?: number;
  reason?: string;
  /** Hard insert (steering only): the run's work in progress was cut off, not just steered. */
  interrupt?: boolean;
  /** How many model calls of the run Core cut off on this host. */
  interrupted_model_calls?: number;
}

/** `.../interactions/{id}/reassign` and `.../cancel` answer about the acted-on run only. */
export interface InteractionActionDto {
  interaction_id: string;
  run_id: string;
  status: string;
  dispatch_mode?: DispatchMode;
  target_run_id?: string;
  action?: string;
}

export type DispatchMode = 'queued' | 'insert' | 'parallel';

export interface AgentEvolutionProposalDto {
  id: string;
  source_run_id: string;
  source_instance_id: string;
  generated_by_instance_id: string;
  name: string;
  layer: string;
  agent_type: string;
  status: string;
  confidence: number;
  promoted_agent_id?: string | null;
  decision_reason?: string | null;
  created_at: string;
  updated_at: string;
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
  /**
   * Core's `ModelUsage` on the wire: `input_tokens` / `output_tokens` / `total_tokens`, every one of
   * which Core may omit rather than send as null, because a provider can complete a call without
   * reporting usage at all. This declaration used to name `prompt_tokens` / `completion_tokens`,
   * which no Core build ever emitted — the field was unreadable rather than zero.
   */
  usage?: {
    input_tokens?: number | null;
    output_tokens?: number | null;
    total_tokens?: number | null;
    cached_input_tokens?: number | null;
    reasoning_tokens?: number | null;
    additional_counts?: Record<string, number> | null;
  } | null;
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
  revision?: number;
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

export interface PromptContextLayerDto {
  id: string;
  kind: string;
  title: string;
  content: string;
  estimated_tokens: number;
  source: string;
}

export interface PromptContextPreviewInput {
  agent_id?: string | null;
  mode?: string | null;
  mode_id?: string | null;
  mode_version_id?: string | null;
  pipeline_id?: string | null;
  draft_system_prompt?: string | null;
  draft_mode_instructions?: string | null;
  draft_pipeline_graph?: PromptPipelineGraphDto | Record<string, unknown> | null;
  include_fragment_ids?: string[] | null;
  session_id?: string | null;
  run_id?: string | null;
  user_content?: string | null;
}

export interface PromptContextPreviewDto {
  assembled_prompt?: string;
  total_estimated_tokens?: number;
  layers?: PromptContextLayerDto[];
  warnings?: string[];
  agent_id?: string;
  mode?: string;
  fragments?: PromptFragmentDto[];
  context_pack_ids?: string[];
  estimated_tokens?: number;
  system_prompt?: string;
}

export interface PromptContextPreviewNormalizedDto extends PromptContextPreviewDto {
  assembled_prompt: string;
  total_estimated_tokens: number;
  layers: PromptContextLayerDto[];
}

export interface OrchestrationRunDto {
  id: string;
  session_id: string;
  user_message_id?: string | null;
  /** The user message that started this run (Core `RunRecord.TriggerMessageId`). */
  trigger_message_id?: string | null;
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
  lane_key: string;
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
  tool_success?: boolean | null;
  /** Sentence data: Core's approval-evidence projection, never the raw parameters. */
  task_id?: string | null;
  agent_instance_id?: string | null;
  error_category?: string | null;
  arguments_summary?: string | null;
  resource_path?: string | null;
  command?: string | null;
  cwd?: string | null;
  /** The search term (`pattern`/`query`/`q`) when the call names one. */
  query?: string | null;
}

/** Bounded result preview of one finished tool call (`GET /api/v1/tool-executions/{id}/preview`). */
export type ToolExecutionPreviewDto = components['schemas']['ToolExecutionPreview']
export type ToolResultPreviewDto = components['schemas']['ToolResultPreview']

/** One evidence item in a context pack and the estimated tokens it cost. */
export interface ContextBudgetShareDto {
  source: string;
  tokens: number;
}

/**
 * One `context.packed` event of a run: what that run was actually told.
 *
 * The field list is the projection in Core's `DmaeaEndpoints` (`context_packs`), which reads the
 * payload the planner and lane assembly steps write — and nothing more. There is no summary text on
 * the wire: the sentence the engine passes to `AppendEventAsync` never reaches `EventEnvelope`, so a
 * `summary` field here would be a promise Core cannot keep. `lane_key` is the one optional member:
 * the orchestration projection serialises a main-planner pack as an explicit null, while the durable
 * event payload this host writes drops null keys entirely, so a reader must survive absent and null
 * as the same fact.
 */
export interface ContextPackDto {
  id: string;
  run_id: string;
  lane_key?: string | null;
  evidence_count: number;
  estimated_tokens: number;
  token_budget: number;
  /** Evidence sources that survived the budget, in pack order. Empty for events written before this field existed. */
  sources: string[];
  /**
   * What each surviving item cost, one row per item and in the same order as `sources`, so a reader
   * can pair a name with a price by index. One source can contribute several rows (`reviewed_memory`
   * adds one per promoted entry), which is why the panel sums by name before showing it.
   */
  source_tokens?: ContextBudgetShareDto[];
  /**
   * Items the token budget crowded out, priced the same way. Absent on events written before either
   * key existed, and this host cannot tell that apart from an empty list — so the reader pairs
   * against `source_tokens`: a pack that names its evidence without pricing it has no budget data,
   * while empty rows beside a priced pack is the real answer that nothing was cut.
   */
  dropped_sources?: ContextBudgetShareDto[];
  created_at: string;
}

export interface ContextLineRangeDto {
  start: number;
  end: number;
  start_line?: number;
  end_line?: number;
}

export interface ContextAuditExtraSourceDto {
  id: string;
  label: string;
  kind: string;
  content: string;
  path?: string | null;
  meta?: string | null;
}

export interface ContextOverlapAuditRequestDto {
  project_id?: string | null;
  mode_id?: string | null;
  agent_id?: string | null;
  run_id?: string | null;
  workspace_root?: string | null;
  include_prompts?: boolean | null;
  include_skills?: boolean | null;
  include_tools?: boolean | null;
  k_gram_size?: number | null;
  window_size?: number | null;
  max_excerpt_length?: number | null;
  max_matches_per_pair?: number | null;
  extra_sources?: ContextAuditExtraSourceDto[] | null;
}

export interface OverlapPassageMatchDto {
  id: string;
  source_a_id: string;
  source_a_label: string;
  source_a_kind: string;
  source_b_id: string;
  source_b_label: string;
  source_b_kind: string;
  source_a_lines: ContextLineRangeDto;
  source_b_lines: ContextLineRangeDto;
  line_range_pair?: string;
  shared_chars: number;
  estimated_duplicate_tokens: number;
  excerpt: string;
  remediation_code: string;
  remediation_summary: string;
}

export interface ContextOverlapPairSummaryDto {
  id: string;
  source_a_id: string;
  source_a_label: string;
  source_a_kind: string;
  source_b_id: string;
  source_b_label: string;
  source_b_kind: string;
  shared_chars: number;
  overlap_percent: number;
  overlap_percent_a: number;
  overlap_percent_b: number;
  estimated_duplicate_tokens: number;
  match_count: number;
  matches: OverlapPassageMatchDto[];
  remediation_code: string;
  remediation_summary: string;
}

export interface ContextOverlapSourceStatsDto {
  id: string;
  label: string;
  kind: string;
  path?: string | null;
  char_count: number;
  line_count: number;
  estimated_tokens: number;
  duplicated_chars: number;
  overlap_percent: number;
}

export interface ContextOverlapAuditDto {
  scope?: 'run_snapshot' | 'static_config' | 'manual_sources' | string | null;
  project_id?: string | null;
  mode_id?: string | null;
  agent_id?: string | null;
  run_id?: string | null;
  k_gram_size: number;
  window_size: number;
  source_count: number;
  total_estimated_tokens: number;
  duplicate_estimated_tokens: number;
  overall_overlap_percent: number;
  pairs: ContextOverlapPairSummaryDto[];
  matches?: OverlapPassageMatchDto[];
  sources: ContextOverlapSourceStatsDto[];
  generated_at?: string | null;
}

export interface ContextWindowBucketDto {
  index: number;
  range_start: number;
  range_end: number;
  label: string;
  sample_count: number;
  error_count: number;
  error_rate: number;
  avg_latency_ms: number;
  max_latency_ms: number;
  avg_occupancy_pct: number;
}

export interface ContextWindowSamplePointDto {
  id: string;
  source_kind: 'invocation' | 'tool' | 'context_pack' | string;
  occupancy_pct: number;
  bucket_index: number;
  is_error: boolean;
  latency_ms: number;
  tokens_used: number;
  token_budget: number;
  timestamp: string;
}

export interface ContextWindowBucketReportDto {
  buckets: ContextWindowBucketDto[];
  samples: ContextWindowSamplePointDto[];
  total_samples: number;
  total_errors: number;
  overall_error_rate: number;
  avg_latency_ms: number;
  peak_occupancy_pct: number;
  low_occupancy_error_rate: number;
  high_occupancy_error_rate: number;
  low_occupancy_avg_latency_ms: number;
  high_occupancy_avg_latency_ms: number;
  degradation_detected: boolean;
  degradation_reason_codes?: string[];
}

export interface ContextEvidenceItemDto {
  id: string;
  source: string;
  tokens: number;
  bytes_estimate: number;
  dropped: boolean;
}

export interface ContextCategoryNodeDto {
  id: string;
  label: string;
  category: string;
  source_key?: string | null;
  depth: number;
  retained_tokens: number;
  dropped_tokens: number;
  retained_bytes_estimate: number;
  dropped_bytes_estimate: number;
  share_percent: number;
  budget_share_percent: number;
  item_count: number;
  dropped_item_count: number;
  items: ContextEvidenceItemDto[];
  children: ContextCategoryNodeDto[];
}

export interface CompactionReconciliationDto {
  pack_id: string;
  lane_key?: string | null;
  created_at: string;
  token_budget: number;
  retained_tokens: number;
  dropped_tokens: number;
  pre_compaction_tokens: number;
  retained_bytes_estimate: number;
  dropped_bytes_estimate: number;
  utilization_percent: number;
  retained_ratio_percent: number;
  dropped_ratio_percent: number;
  has_pricing_data: boolean;
  has_compaction_digest: boolean;
  previous_pack_retained_delta?: number | null;
  previous_pack_dropped_delta?: number | null;
}

export interface ContextCompactionDiagnosticsDto {
  selected_pack_id?: string | null;
  pack_count: number;
  total_dropped_tokens_across_packs: number;
  critical_dropped_sources: string[];
  root: ContextCategoryNodeDto;
  reconciliation: CompactionReconciliationDto;
  packs: CompactionReconciliationDto[];
}

export interface RunGovernanceSignalDto {
  code: string;
  severity: 'danger' | 'warn' | 'neutral' | string;
  count: number;
  max_retry_streak?: number | null;
  detail?: string | null;
  subject_ids?: string[];
}

export interface RunRetryStreakDto {
  execution_id: string;
  tool_id: string;
  scope_key: string;
  streak_count: number;
  arguments_summary?: string | null;
}

export interface RunFileEditDto {
  execution_id: string;
  tool_id: string;
  file_path?: string | null;
  added: number;
  removed: number;
}

export interface DiagnosticCodeFrameLineDto {
  line_number?: number | null;
  text: string;
  highlight?: boolean;
}

export interface DiagnosticCodeFrameDto {
  id: string;
  source_id?: string | null;
  execution_id?: string | null;
  title: string;
  file_path?: string | null;
  line?: number | null;
  column?: number | null;
  command?: string | null;
  cwd?: string | null;
  error_category?: string | null;
  lines: DiagnosticCodeFrameLineDto[];
}

export interface RunGovernanceDiagnosticsDto {
  signal_counts: Record<string, number>;
  active_signals: string[];
  signals: RunGovernanceSignalDto[];
  max_same_args_retry_streak: number;
  retry_streaks: RunRetryStreakDto[];
  total_tool_calls: number;
  total_model_calls: number;
  error_count: number;
  total_added_lines: number;
  total_removed_lines: number;
  file_edits: RunFileEditDto[];
  error_frames: DiagnosticCodeFrameDto[];
}

export interface ExecutiveFindingDto {
  code: string;
  severity: 'danger' | 'warn' | 'info' | string;
  summary: string;
  evidence_refs: string[];
  suggested_action?: string | null;
}

export interface RunContextDiagnosticsDto {
  run_id: string;
  session_id?: string | null;
  status?: string;
  mode_version_id?: string | null;
  generated_at: string;
  window_buckets: ContextWindowBucketReportDto;
  compaction: ContextCompactionDiagnosticsDto;
  governance: RunGovernanceDiagnosticsDto;
  executive_findings: ExecutiveFindingDto[];
}

export interface ContextPolicyPatchDto {
  context_strategy?: 'direct' | 'managed' | 'deep_curator' | string | null;
  compaction_trigger_ratio?: number | null;
  recent_message_limit?: number | null;
  recall_fidelity?: 'summary_only' | 'balanced' | 'verbatim_first' | string | null;
  allow_cross_session_recall?: boolean | null;
  evolution_mode?: 'auto_evolve' | 'candidate_only' | string | null;
  core_markdown_max_tokens?: number | null;
}

export interface ContextDedupProposalEvidenceDto {
  source_a_id: string;
  source_a_label: string;
  source_a_kind: string;
  source_a_path?: string | null;
  source_a_start_line: number;
  source_a_end_line: number;
  source_b_id: string;
  source_b_label: string;
  source_b_kind: string;
  source_b_path?: string | null;
  source_b_start_line: number;
  source_b_end_line: number;
  line_range_label: string;
  overlap_chars: number;
  estimated_wasted_tokens: number;
  remediation_code: string;
  excerpt: string;
  suggested_remediation: string;
}

export interface ContextEvolutionMetricsComparisonDto {
  window_run_count: number;
  degraded_run_count: number;
  degradation_rate: number;
  dropped_critical_run_count: number;
  avg_peak_occupancy_pct: number;
  high_occupancy_error_rate: number;
  low_occupancy_error_rate: number;
  high_occupancy_avg_latency_ms: number;
  low_occupancy_avg_latency_ms: number;
  dropped_sources: string[];
  before_values: Record<string, string>;
  after_values: Record<string, string>;
  expected_benefit: string;
}

export interface ContextEvolutionProposalDto {
  id: string;
  kind: 'context_policy_proposal' | 'prompt_dedup_proposal' | string;
  status: 'pending' | 'applied' | 'rejected' | string;
  target_scope: 'global' | 'mode' | 'workspace' | string;
  title: string;
  summary: string;
  rationale: string;
  confidence: number;
  risk_level: 'low' | 'medium' | 'high' | string;
  requires_human_approval: boolean;
  source_run_id?: string | null;
  evidence_run_ids: string[];
  project_id?: string | null;
  target_mode_id?: string | null;
  target_mode_slug?: string | null;
  target_mode_name?: string | null;
  remediation_code?: string | null;
  policy_patch?: ContextPolicyPatchDto | null;
  dedup_evidence?: ContextDedupProposalEvidenceDto | null;
  metrics_comparison?: ContextEvolutionMetricsComparisonDto | null;
  linked_agent_candidate_id?: string | null;
  eval_run_id?: string | null;
  eval_gate_status?: string | null;
  decision_reason?: string | null;
  applied_toml_path?: string | null;
  applied_toml_content_hash?: string | null;
  applied_mode_id?: string | null;
  applied_mode_version?: number | null;
  promoted_agent_id?: string | null;
  composer_prompt?: string | null;
  created_at: string;
  updated_at: string;
  decided_at?: string | null;
}

export interface ContextEvolutionProposalListDto {
  items: ContextEvolutionProposalDto[];
  total_count: number;
  pending_count: number;
  auto_tune_low_risk_context_policy: boolean;
}

export interface ContextEvolutionProposalQuery {
  status?: string;
  kind?: string;
  target_scope?: string;
  project_id?: string;
  mode_id?: string;
  limit?: number;
}

export interface ApplyContextEvolutionProposalInput {
  reason?: string | null;
  eval_run_id?: string | null;
  require_eval_gate?: boolean | null;
  expected_content_hash?: string | null;
}

export interface ContextEvolutionApplyResultDto {
  proposal: ContextEvolutionProposalDto;
  applied: boolean;
  summary: string;
  updated_toml_preview?: string | null;
  applied_mode_id?: string | null;
  applied_mode_version?: number | null;
  promoted_agent_id?: string | null;
  eval_run_id?: string | null;
}

export interface ContextEvolutionEvaluationDto {
  proposal_id: string;
  proposal: ContextEvolutionProposalDto;
  eval_runs: Array<Record<string, unknown>>;
  evidence_diagnostics: Array<{
    run_id: string;
    status: string;
    peak_occupancy_pct: number;
    degradation_detected: boolean;
    degradation_reason_codes: string[];
    critical_dropped_sources: string[];
    total_dropped_tokens: number;
    active_signals: string[];
    max_same_args_retry_streak: number;
  }>;
}

/**
 * One entry of the memory review queue, as Core's `ToMemoryCandidate` projects it.
 *
 * `evidence`, `applicability` and `expiry_condition` are what make the card reviewable: the
 * curator recorded why it believes the sentence and when the sentence stops being true, and a
 * queue that showed only the sentence asks the reviewer to rule on a claim with no grounds.
 * Absent for candidates proposed before those fields travelled.
 */
export interface MemoryCandidateDto {
  id: string;
  source_run_id: string;
  generated_by_instance_id: string;
  scope: string;
  kind: string;
  status: string;
  confidence: number;
  content: string;
  evidence?: string | null;
  applicability?: string | null;
  expiry_condition?: string | null;
  decision_reason?: string | null;
  promoted_memory_item_id?: string | null;
  created_at: string;
  updated_at: string;
}

/** Promoted (or revoked) long-term memory as Core's item listing projects it. */
export interface MemoryItemDto {
  id: string;
  scope: string;
  kind: string;
  status: string;
  version: number;
  content: string;
  applicability?: string | null;
  expiry_condition?: string | null;
  created_at: string;
  updated_at: string;
  revoked_at?: string | null;
}

/**
 * The revoke response is a narrower projection than the listing: it answers with the
 * revocation, not the row. Declaring the listing's fields here would let a caller read a
 * `content` the endpoint never sent.
 */
export interface MemoryRevocationDto {
  id: string;
  scope: string;
  kind: string;
  status: string;
  version: number;
  applicability?: string | null;
  expiry_condition?: string | null;
  revoked_at?: string | null;
}

/** Queue narrowing. Unknown status or scope values are refused by Core, not answered with nothing. */
export interface MemoryQueueQuery {
  status?: string;
  scope?: string;
  kind?: string;
  run_id?: string;
  project_id?: string;
  limit?: number;
}

function memoryQueryString(query: MemoryQueueQuery, honoured: string[]): string {
  const search = new URLSearchParams();
  for (const key of honoured) {
    const value = (query as Record<string, unknown>)[key];
    if (value !== undefined && value !== '') search.set(key, String(value));
  }
  return search.toString() ? `?${search.toString()}` : '';
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
  description?: string;
  input_schema?: Record<string, unknown>;
  mutates_workspace?: boolean;
  retry_safety?: string;
  confirmation_fields?: string[];
}

/** Current Core manifest transport; display/discovery metadata is not present. */
export interface ToolManifestEntryDto {
  id: string;
  description: string;
  requires_approval: boolean;
  input_schema: Record<string, unknown>;
  risk: string;
  mutates_workspace: boolean;
  retry_safety: string;
  confirmation_fields: string[];
}

export interface ToolSearchResultDto {
  tool: ToolDescriptorDto;
  score?: number;
  matched_fields: string[];
  provider_layer: string;
  requires_human_checkpoint?: boolean;
  approval_summary: string;
}

export function normalizeToolDescriptor(tool: ToolDescriptorDto | ToolManifestEntryDto): ToolDescriptorDto {
  if ('display_name' in tool) return tool;
  return {
    ...tool,
    display_name: tool.id,
    domain: '', source: '', execute_endpoint: '', capabilities: [],
  };
}

export function normalizeToolSearchResults(items: (ToolSearchResultDto | ToolManifestEntryDto)[]): ToolSearchResultDto[] {
  return items.map((item) => 'tool' in item
    ? { ...item, tool: normalizeToolDescriptor(item.tool) }
    : { tool: normalizeToolDescriptor(item), matched_fields: [], provider_layer: '', approval_summary: '' });
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

export interface CodeToolExecuteResultDto<TData = Record<string, unknown>> {
  tool_id: string;
  status: string;
  summary: string;
  evidence: string[];
  data: TData;
  requires_approval: boolean;
  approval_summary?: string | null;
}

export interface GitRefDto {
  name: string;
  type: 'head' | 'branch' | 'tag' | 'remote' | string;
  is_head: boolean;
}

export interface GitGraphEdgeDto {
  parent_hash: string;
  from_lane: number;
  to_lane: number;
}

export interface GitCommitSummaryDto {
  hash: string;
  short_hash: string;
  parents: string[];
  author: string;
  author_email: string;
  author_date: string;
  committer_date: string;
  subject: string;
  refs: GitRefDto[];
  lane_index: number;
  edges: GitGraphEdgeDto[];
}

export interface GitFileChangeDto {
  status: 'A' | 'D' | 'M' | 'R' | 'C' | 'T' | 'U' | string;
  score: number;
  old_path?: string | null;
  new_path: string;
  additions: number;
  deletions: number;
  is_binary: boolean;
  blob_hash?: string | null;
  byte_size?: number | null;
}

export interface GitDiffLineDto {
  type: 'context' | 'add' | 'delete' | string;
  old_line_number?: number | null;
  new_line_number?: number | null;
  content: string;
}

export interface GitDiffHunkDto {
  old_start: number;
  old_count: number;
  new_start: number;
  new_count: number;
  lines: GitDiffLineDto[];
}

export interface GitPatchFileDto {
  old_path: string;
  new_path: string;
  status: string;
  is_binary: boolean;
  blob_hash?: string | null;
  byte_size?: number | null;
  hunks: GitDiffHunkDto[];
  truncated?: boolean;
  truncation_reason?: string | null;
}

export interface GitLogListResultDto {
  success: boolean;
  error?: string | null;
  error_code?: string | null;
  commits: GitCommitSummaryDto[];
  truncated: boolean;
  next_cursor?: string | null;
}

export interface GitLogDetailResultDto {
  success: boolean;
  error?: string | null;
  error_code?: string | null;
  commits: GitCommitSummaryDto[];
  files: GitFileChangeDto[];
  patches?: GitPatchFileDto[] | null;
  truncated: boolean;
  truncation_reason?: string | null;
  diff_mode?: string;
  next_cursor?: string | null;
}

export interface GitFileHistoryEntryDto {
  commit: GitCommitSummaryDto;
  change: GitFileChangeDto;
  patch?: GitPatchFileDto | null;
}

export interface GitFileHistoryResultDto {
  success: boolean;
  error?: string | null;
  error_code?: string | null;
  entries: GitFileHistoryEntryDto[];
  truncated: boolean;
  truncation_reason?: string | null;
  next_cursor?: string | null;
}

export interface GitBlameLineDto {
  commit: string;
  author: string;
  author_time?: string | null;
  line_number: number;
  content: string;
}

export interface GitBlameResultDto {
  success: boolean;
  error?: string | null;
  error_code?: string | null;
  lines: GitBlameLineDto[];
  truncated: boolean;
  truncation_reason?: string | null;
}

export interface GitFileAtRevisionResultDto {
  success: boolean;
  error?: string | null;
  error_code?: string | null;
  path?: string | null;
  rev?: string | null;
  content?: string | null;
  is_binary: boolean;
  blob_hash?: string | null;
  byte_size?: number | null;
  truncated: boolean;
  truncation_reason?: string | null;
}

export interface GitStashEntryDto {
  index: number;
  ref_name: string;
  hash: string;
  subject: string;
  branch?: string | null;
  message: string;
  created_at: string;
  author: string;
}

export interface GitStashListResultDto {
  success: boolean;
  error?: string | null;
  error_code?: string | null;
  entries: GitStashEntryDto[];
  truncated: boolean;
}

export interface GitStashShowResultDto {
  success: boolean;
  error?: string | null;
  error_code?: string | null;
  stash_ref: string;
  files: GitFileChangeDto[];
  patches?: GitPatchFileDto[] | null;
  truncated: boolean;
  truncation_reason?: string | null;
}

export interface GitCommitAmendArgs {
  repository_path?: string;
  message?: string;
  paths?: string[];
  commit_staged_only?: boolean;
  include_all?: boolean;
  amend?: boolean;
}

export interface GitStashPushArgs {
  repository_path?: string;
  message?: string;
  include_untracked?: boolean;
  keep_index?: boolean;
  paths?: string[];
  confirm_stash_push: boolean;
}

export interface GitStashApplyArgs {
  repository_path?: string;
  stash_ref?: string;
  reinstate_index?: boolean;
  confirm_stash_apply: boolean;
}

export interface GitStashPopArgs {
  repository_path?: string;
  stash_ref?: string;
  reinstate_index?: boolean;
  confirm_stash_pop: boolean;
}

export interface GitStashDropArgs {
  repository_path?: string;
  stash_ref: string;
  confirm_stash_drop: boolean;
}

export interface CodeToolExecuteRequestDto {
  session_id?: string | null;
  run_id?: string | null;
  task_node_id?: string | null;
  approval_id?: string | null;
  cwd?: string | null;
  arguments?: Record<string, unknown> | null;
  /** User intent fields are passed through to the Tool Provider; Desktop does not decide authorization. */
  approval?: boolean;
  confirmation?: boolean;
  source?: 'human' | 'agent' | string;
}

export type UserToolActionStatus =
  | 'snapshot_required'
  | 'awaiting_delegate'
  | 'awaiting_user'
  | 'awaiting_approval'
  | 'running'
  | 'completed'
  | 'blocked'
  | 'outcome_unknown'
  | 'failed'
  | string;

export interface ManagedMcpProgramPreview {
  resource_id: string;
  project_id?: string | null;
  action: 'install' | 'uninstall';
  expected_revision: number;
  manager: string;
  package: string;
  version: string;
  package_root: string;
  plan_hash: string;
  program_status: string;
  server_arguments: string[];
  preview_id: string;
}

/** Core-owned user action. Nonces and internal lease material never cross this DTO. */
export interface UserToolActionDto {
  id: string;
  audit_reference: string;
  tenant_id: string;
  workspace_id: string;
  project_id: string;
  principal_id: string;
  tool_id: string;
  status: UserToolActionStatus;
  risk: string;
  mutates_workspace: boolean;
  requires_approval: boolean;
  permission_request_id?: string | null;
  authorization_decision_id?: string | null;
  action_approval_id?: string | null;
  snapshot_id?: string | null;
  snapshot_hash?: string | null;
  snapshot_override?: boolean;
  snapshot_override_reason?: string | null;
  non_reversible?: boolean;
  compensation_guidance?: string | null;
  recovery_decision?: string | null;
  recovery_reason?: string | null;
  recovered_at?: string | null;
  result?: Record<string, unknown> | null;
  error_category?: string | null;
  message?: string | null;
  created_at: string;
  updated_at: string;
  completed_at?: string | null;
}

export type UserToolActionRecoveryDecision = 'mark_completed' | 'mark_failed';

export interface RecoveryDecisionInput {
  decision: UserToolActionRecoveryDecision;
  reason?: string | null;
}

export interface CreateUserToolActionInput {
  project_id: string;
  tool_id: string;
  params?: Record<string, unknown> | null;
  idempotency_key?: string | null;
}

export interface SnapshotDto {
  id: string;
  tenant_id: string;
  workspace_id: string;
  project_id: string;
  kind: string;
  status: string;
  is_git: boolean;
  workspace_hash: string;
  content_hash: string;
  file_count: number;
  created_at: string;
}

/**
 * One row of a snapshot's per-file review. `restorable` is Core's answer to "can this row be
 * undone": a file over the snapshot's content ceiling is reportable but not reversible, so the
 * page must not draw a button for it.
 */
export interface WorkspaceFileChangeDto {
  path: string;
  status: 'added' | 'deleted' | 'modified' | 'unchanged' | string;
  before_sha256?: string | null;
  after_sha256?: string | null;
  before_length?: number | null;
  after_length?: number | null;
  restorable: boolean;
}

export interface WorkspaceFileSideDto {
  present: boolean;
  length?: number | null;
  sha256?: string | null;
  binary: boolean;
  truncated: boolean;
  text?: string | null;
}

export interface WorkspaceFileDiffDto {
  path: string;
  status: string;
  restorable: boolean;
  before: WorkspaceFileSideDto;
  after: WorkspaceFileSideDto;
}

export interface AgentLineageEntryDto {
  id: string;
  run_id: string;
  parent_instance_id?: string | null;
  task_id?: string | null;
  layer: string;
  role: string;
  /** Pack agent definition behind this instance; absent for ad-hoc roles. */
  agent_definition_id?: string | null;
  generation_depth: number;
  generated: boolean;
  status: string;
  capabilities?: string[] | null;
}

export interface OrchestrationLaneWaitDto {
  waiting_task: string;
  lane: string;
  predicate: string;
  required_criteria: string[];
  facts_hash?: string | null;
}

export interface OrchestrationLaneDto {
  lane_key: string;
  status: string;
  escalated: boolean;
  task_keys: string[];
  waits: OrchestrationLaneWaitDto[];
}

export interface DeclaredGraphNodeDto {
  node_key: string;
  label?: string | null;
  /** Per-locale node labels carried by the frozen mode snapshot. */
  i18n?: PackI18n | null;
  layer?: string | null;
  agent_definition_id?: string | null;
  is_conversation: boolean;
  relationship?: Record<string, unknown> | null;
}

export interface DeclaredGraphEdgeDto {
  edge_key: string;
  source_node_key?: string | null;
  target_node_key?: string | null;
  data_contract?: Record<string, unknown> | null;
}

/** Declared mode graph (DmaEA graph orchestration): the published mode-version snapshot projected as base layer. */
export interface DeclaredModeGraphDto {
  /** The run's frozen orchestration tier (deterministic | self_dispatch | free_form); null at session scope. */
  tier?: string | null;
  nodes: DeclaredGraphNodeDto[];
  edges: DeclaredGraphEdgeDto[];
}

/** Observed data flow projected from the durable task graph (dispatch through the conversation identity). */
export interface OrchestrationFlowDto {
  /** Conversation template slug for a planned task; the dispatching instance's handle for a task_dispatch hand-off. */
  from: string;
  to: string;
  /** Run-scoped instance name (search#2), once a worker is assigned. */
  handle?: string | null;
  task_key: string;
  kind: string;
  /** 'plan' (planner task array) or 'task_dispatch' (handed off from inside a tool loop). */
  via?: 'plan' | 'task_dispatch';
  status: string;
}

export interface OrchestrationSnapshotDto {
  run?: OrchestrationRunDto | null;
  graph?: DeclaredModeGraphDto | null;
  flows?: OrchestrationFlowDto[];
  nodes: TaskNodeDto[];
  lanes: OrchestrationLaneDto[];
  assignments: AgentAssignmentDto[];
  step_results: StepResultDto[];
  context_packs: ContextPackDto[];
  supervision_findings: SupervisionFindingDto[];
}

// --- Session topology (GET /api/v1/sessions/{id}/topology) ---
// Mirrors of Core's SessionTopologyDtos.cs / OrganizationDtos.cs. The generated schema has the same
// bodies, but types every int64 as `number | string` and every nullable field as present; Core drops
// null keys, so absent-or-null is spelled `?: T | null` here. api.test.ts pins the key sets against
// the generated contract, so a field invented or dropped on either side fails the typecheck.

export interface TopologyTaskDto {
  task_id: string;
  task_key: string;
  title: string;
  status: string;
  handle?: string | null;
  agent_slug?: string | null;
  worker_instance_id?: string | null;
  dispatched_by_task_id?: string | null;
  dependencies: string[];
  write_scope: string[];
  result_summary?: string | null;
}

export interface TopologyInstanceDto {
  instance_id: string;
  agent_slug?: string | null;
  layer: string;
  role: string;
  status: string;
  parent_instance_id?: string | null;
  task_id?: string | null;
  depth: number;
}

export interface TopologyRunDto {
  run_id: string;
  parent_run_id?: string | null;
  parent_task_id?: string | null;
  run_kind?: string;
  status: string;
  started_at?: string | null;
  completed_at?: string | null;
  tier?: string | null;
  phase?: string | null;
  tasks: TopologyTaskDto[];
  instances: TopologyInstanceDto[];
  tasks_truncated: boolean;
  instances_truncated: boolean;
}

export interface TopologyLeaseDto {
  lease_id: string;
  kind: string;
  resource_key: string;
  purpose?: string | null;
  exclusive: boolean;
  run_id?: string | null;
  task_id?: string | null;
  agent_instance_id?: string | null;
}

export interface TopologyMemberDto {
  participant_id: string;
  handle: string;
  display_name: string;
  role: string;
  presence: string;
  agent_slug?: string | null;
  parent_participant_id?: string | null;
  run_id?: string | null;
  agent_instance_id?: string | null;
}

export interface SessionTopologyDto {
  session_id: string;
  runs: TopologyRunDto[];
  leases: TopologyLeaseDto[];
  members: TopologyMemberDto[];
  runs_truncated: boolean;
  leases_truncated: boolean;
  members_truncated: boolean;
  generated_at: string;
}

export interface SessionTopologyQuery {
  run_id?: string;
  include_finished?: boolean;
  max_runs?: number;
  max_tasks?: number;
}

// --- Delegated approval gates (GET /api/v1/approvals/{id}/gates) ---

/** One gate of a delegated approval: who was asked, what they decided, and exactly what they were shown. */
export interface ApprovalGateDto {
  gate_index: number;
  /** `reviewer_agent` (independent context) or `conversation_identity` (knows the user's goal). */
  gate_kind: string;
  /** pending · evaluating · approved · rejected · escalated · skipped · superseded */
  status: string;
  decider_agent: string | null;
  reason: string | null;
  /** The facts the gate saw — for the reviewer the call and the task, never the conversation. */
  evidence: Record<string, unknown> | null;
  created_at: string;
  decided_at: string | null;
}

/** A delegated approval's gates. `status`: evaluating · approved · rejected · escalated (back to you) · superseded (you decided first). */
export interface ApprovalGatesDto {
  approval_id: string;
  run_id: string | null;
  status: string;
  gates: ApprovalGateDto[];
}

// --- Session evidence archive (GET /api/v1/sessions/{id}/evidence) ---

/** One piece of archived evidence that matched; `matched_by`: semantic · keyword · both. */
export interface EvidenceHitDto {
  evidence_id: string;
  /** task_result · report · member_turn · summary */
  kind: string;
  title: string;
  author: string | null;
  run_id: string | null;
  task_id: string | null;
  snippet: string;
  score: number;
  matched_by: string;
  created_at: string;
}

/** `mode`: hybrid when the semantic index answered, keyword when it could not; `note` says why. */
export interface EvidenceRecallDto {
  mode: string;
  note: string | null;
  hits: EvidenceHitDto[];
}

// --- Environment registry (GET/POST /api/v1/environments, PATCH /api/v1/environments/{id}) ---

export type EnvironmentKind = 'local' | 'cloud' | 'remote' | 'terminal' | 'test';

/** One slot of an environment and the run holding it. */
export interface EnvironmentHolderDto {
  slot: number;
  lease_id: string;
  session_id: string | null;
  run_id: string | null;
  task_id: string | null;
  reason: string;
}

/** A registered environment and who holds its slots; `connection` never carries a credential (only `secret_ref`). */
export interface EnvironmentDto {
  id: string;
  key: string;
  kind: EnvironmentKind | string;
  display_name: string;
  description: string | null;
  connection: Record<string, unknown>;
  capacity: number;
  free_slots: number;
  status: 'available' | 'disabled' | string;
  holders: EnvironmentHolderDto[];
  updated_at: string;
}

export interface EnvironmentRegisterInput {
  key: string;
  kind: EnvironmentKind;
  display_name: string;
  description?: string | null;
  connection?: Record<string, unknown> | null;
  capacity?: number | null;
}

/** Omitted fields are left as they are. */
export interface EnvironmentUpdateInput {
  display_name?: string | null;
  description?: string | null;
  connection?: Record<string, unknown> | null;
  capacity?: number | null;
  status?: 'available' | 'disabled' | null;
}

// --- Session organization (GET /api/v1/sessions/{id}/organization/*) ---

export type OrganizationMemberRole = 'human' | 'conversation' | 'governance' | 'executor';

export interface OrganizationMemberDto {
  id: string;
  handle: string;
  display_name: string;
  role: OrganizationMemberRole;
  presence: 'online' | 'offline';
  agent_slug?: string | null;
  parent_id?: string | null;
  run_id?: string | null;
  /** Per-member visibility into run internals: "own" restricts to its own run; absent = unrestricted ("down"). */
  visibility_scope?: 'down' | 'own' | null;
}

export interface OrganizationRoomDto {
  id: string;
  kind: 'lobby' | 'board' | 'plan' | 'adhoc';
  title: string;
  last_sequence: number;
  member_count: number;
  is_member: boolean;
  plan_owner_id?: string | null;
}

export interface OrganizationDto {
  id: string;
  session_id: string;
  status: 'active' | 'archived';
  you_participant_id: string;
  members: OrganizationMemberDto[];
  rooms: OrganizationRoomDto[];
  open_reports: number;
  members_truncated: boolean;
  rooms_truncated: boolean;
}

/** The live state of a report's subject, resolved by Core at read time; `status` is absent when Core cannot tell. */
export interface OrganizationSubjectStateDto {
  kind: string;
  id: string;
  status?: string | null;
  label?: string | null;
}

export interface OrganizationReportDto {
  id: string;
  room_id: string;
  author_id: string;
  author_display_name: string;
  report_kind: 'conflict' | 'risk' | 'drift' | 'budget' | 'progress';
  severity: 'info' | 'warning' | 'blocking';
  status: 'open' | 'acted' | 'dismissed' | 'superseded';
  subject_kind?: string | null;
  subject_id?: string | null;
  proposed_verb?: string | null;
  proposed_args_json?: string | null;
  finding: string;
  evidence: string[];
  revision: number;
  created_at: string;
  decided_by_id?: string | null;
  decision_note?: string | null;
  decided_at?: string | null;
  supersedes_report_id?: string | null;
  subject_state?: OrganizationSubjectStateDto | null;
}

export interface OrganizationMessageDto {
  id: string;
  room_id: string;
  sender_id: string;
  sender_display_name: string;
  sender_role: string;
  sequence: number;
  kind: 'message' | 'report' | 'notice';
  content: string;
  sensitivity?: string | null;
  created_at: string;
  report?: OrganizationReportDto | null;
}

export interface OrganizationMessagePageDto {
  items: OrganizationMessageDto[];
  /** Sequence of the last row Core scanned (readable or not); read on with `after_sequence = next_cursor`. */
  next_cursor: number;
}

export interface OrganizationReportPageDto {
  items: OrganizationReportDto[];
  truncated: boolean;
}

export interface PostOrganizationMessageInput {
  content: string;
  client_message_id: string;
  reply_to_message_id?: string;
  mention?: string[];
}

export interface OrganizationReportDecisionInput {
  decision: 'acted' | 'dismissed';
  expected_revision: number;
  note?: string;
}

function organizationPath(sessionId: string, suffix = ''): string {
  return `/api/v1/sessions/${encodeURIComponent(sessionId)}/organization${suffix}`;
}

function querySuffix(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}

const gatewayUrl = window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730';

/** Catalog reads describe one storage scope; a switch must never be answered from another. */
function catalogKey(name: string): string { return `${selectedStorageId()}:${name}` }

interface JsonRequestResult<T> {
  data: T
  headers: Headers
}

async function requestResult<T>(path: string, init?: StorageRequestOptions): Promise<JsonRequestResult<T>> {
  const storageId = captureStorageId(path, init)
  const normalized = normalizeStorageRequest(path, init)
  const headers = storageHeaders(path, { ...init, storageId })
  if (!headers.has('accept')) headers.set('accept', 'application/json')
  if (init?.body && !headers.has('content-type')) headers.set('content-type', 'application/json')
  const { response, text } = await readBackendResponse(`${gatewayUrl}${normalized.path}`, path,
    { ...init, body: normalized.body, headers }, { storageId })
  let data: unknown = null;
  if (text.length > 0) {
    try {
      data = JSON.parse(text);
    } catch {
      // Response body is not valid JSON – surface the raw text for debugging
      throw new Error(i18n.global.t('errors.invalidServerResponse', { text: text.substring(0, 200) }));
    }
  }

  if (!response.ok) {
    const message = extractErrorMessage(data, response.statusText);
    throw new ApiError(message, response.status, data, { storageId });
  }

  rememberStorageResult(path, data, storageId)
  // A completed write makes every cached catalog stale, whatever it touched.
  if (init?.method && init.method !== 'GET' && init.method !== 'HEAD') invalidateReadCache()
  return { data: data as T, headers: response.headers };
}

async function request<T>(path: string, init?: StorageRequestOptions): Promise<T> {
  return (await requestResult<T>(path, init)).data;
}

function withResponseEtag<T extends { etag?: string | null }>(result: JsonRequestResult<T>): T {
  const etag = result.headers.get('etag')
  return etag ? { ...result.data, etag } : result.data
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

  // Tool execute results (CodeToolExecuteResultDto) fail with a 4xx/422 body that
  // carries summary/error instead of a message envelope. Without these fallbacks
  // the user only sees the raw HTTP status text (e.g. "Unprocessable Entity").
  if (typeof nestedError === 'string' && nestedError.length > 0) return nestedError;

  const summary = record.summary;
  if (typeof summary === 'string' && summary.length > 0) return summary;

  // RFC 9457 problem+json bodies (gateway / backend errors) carry detail/title instead of message.
  const detail = record.detail;
  if (typeof detail === 'string' && detail.length > 0) return detail;

  const title = record.title;
  if (typeof title === 'string' && title.length > 0) return title;

  return fallback;
}

export async function spawnRunAgent(runId: string, body: { parent_instance_id: string; goal: string; intent?: string; role?: string; allowed_tools?: string[]; allowed_resources?: string[]; success_criteria?: string[]; context_selectors?: string[]; model_route_purpose?: string; budget_tokens?: number }): Promise<unknown> { return request(`/api/v1/runs/${runId}/agents/spawn`, { method: 'POST', body: JSON.stringify(body) }); }

/** Resolve the Core project identity for a desktop workspace path. */
export async function createUserToolActionForPath(
  path: string,
  toolId: string,
  params?: Record<string, unknown> | null,
  idempotencyKey?: string,
): Promise<UserToolActionDto> {
  const capturedStorage = captureStorageId('/api/v1/user/tool-actions')
  const projects = await request<ProjectDto[]>('/api/v1/projects');
  const normalized = path.replace(/[\\/]+$/, '').toLowerCase();
  const project = projects.find((item) => item.path.replace(/[\\/]+$/, '').toLowerCase() === normalized);
  if (!project) throw new Error(i18n.global.t('errors.workspaceNotRegistered'));
  return request<UserToolActionDto>('/api/v1/user/tool-actions', {
    method: 'POST',
    storageId: project.storage_id ?? capturedStorage,
    body: JSON.stringify({ project_id: project.id, tool_id: toolId, params, idempotency_key: idempotencyKey }),
  });
}

/**
 * Normalize a raw SSE event payload into the stable renderer-side EventEnvelope shape.
 *
 * Core's wire envelope (TinadecCore/Contracts/Events/EventEnvelope.cs, serialized with
 * SnakeCaseLower) carries `event_type`/`timestamp`/`version` and nests the sequence in
 * `payload.sequence`; the SSE `id:` line mirrors that sequence. Legacy/mock payloads keep
 * the older top-level `type`/`ts`/`seq` shape. Both are accepted here so consumers can
 * rely on `type`/`seq`/`ts`/`v` always being present (never undefined).
 */
export function normalizeEventEnvelope(
  raw: Record<string, unknown>,
  lastEventId?: string | null,
): EventEnvelope {
  const wirePayload =
    raw.payload && typeof raw.payload === 'object' && !Array.isArray(raw.payload)
      ? (raw.payload as Record<string, unknown>)
      : {};
  // StorageLifecycleService materializes durable journal rows as
  // payload={ sequence, summary, severity, payload: <business payload> }.
  // Consumers should not need to know whether an event arrived live or via
  // durable replay, so flatten that business payload here while retaining the
  // journal metadata used by the renderer (especially sequence/summary/severity).
  const durablePayload =
    wirePayload.payload && typeof wirePayload.payload === 'object' && !Array.isArray(wirePayload.payload)
      ? (wirePayload.payload as Record<string, unknown>)
      : null;
  const payload = durablePayload ? { ...wirePayload, ...durablePayload } : wirePayload;
  const seqFromPayload = Number(wirePayload.sequence)
  // The SSE id is a per-run vector entry (<run hex>:<sequence>), not a bare number.
  const idSequence = /^[0-9a-f]{32}:(\d+)$/.exec(lastEventId ?? '')?.[1]
  const seqFromId = idSequence === undefined ? NaN : Number(idSequence)
  const seqCandidate =
    typeof raw.seq === 'number' && Number.isFinite(raw.seq)
      ? raw.seq
      : Number.isFinite(seqFromPayload)
        ? seqFromPayload
        : Number.isFinite(seqFromId)
          ? seqFromId
          : 0;
  return {
    ...raw,
    v: typeof raw.v === 'string' ? raw.v : typeof raw.version === 'string' ? raw.version : '1.0',
    type: typeof raw.type === 'string' ? raw.type : typeof raw.event_type === 'string' ? raw.event_type : 'unknown',
    seq: seqCandidate,
    ts: typeof raw.ts === 'string' ? raw.ts : typeof raw.timestamp === 'string' ? raw.timestamp : new Date().toISOString(),
    request_id: typeof raw.request_id === 'string' ? raw.request_id : '',
    trace_id: typeof raw.trace_id === 'string' ? raw.trace_id : '',
    capabilities: Array.isArray(raw.capabilities) ? raw.capabilities : [],
    payload,
  };
}

/** Body the durable admission endpoint takes on the compat streaming path. */
interface InvokeStreamBody {
  content: string
  client_message_id: string
  mode_version_id?: string | null
  permission_mode: string
  target_run_id?: string | null
  expected_context_revision?: number | null
}

function newClientMessageId(): string {
  return (globalThis.crypto as Crypto | undefined)?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`
}

/**
 * The durable frame carries its business fields next to `kind`, and `parseRunSseBlock`
 * keeps them in `payload` when the frame has no nested payload of its own.
 * `ModelStreamChunkDto` spells them at the top level, which is what its consumers read,
 * so the lift happens here rather than in every caller. It used to not happen at all:
 * the compat path built a chunk without `delta`, and the AI commit-message and
 * change-analysis panels streamed a whole reply into a field that stayed undefined.
 */
function modelStreamChunkOf(chunk: SseChunk): ModelStreamChunkDto {
  const payload = chunk.payload as Record<string, unknown>
  const text = (value: unknown): string | null => (typeof value === 'string' ? value : null)
  return {
    run_id: chunk.run_id,
    session_id: text(payload.session_id) ?? '',
    purpose: text(payload.purpose) ?? 'dual_layer',
    provider_instance_id: text(payload.provider_instance_id) ?? '',
    effective_model: text(payload.effective_model),
    kind: chunk.kind as ModelStreamChunkDto['kind'],
    delta: runStreamDelta(chunk) || null,
    tool_call_delta: (payload.tool_call_delta as ModelStreamChunkDto['tool_call_delta']) ?? null,
    usage: (payload.usage as ModelStreamChunkDto['usage']) ?? null,
    finish_reason: text(payload.finish_reason),
    error_category: text(payload.error_category),
    is_retryable: payload.is_retryable === true,
    safe_error_message: text(payload.safe_error_message),
    fallback_provider_selected: payload.fallback_provider_selected === true,
    error_provider_id: text(payload.error_provider_id),
  }
}

/**
 * Admit an interaction, then follow its run with the durable reader.
 *
 * This used to be a second hand-written SSE parser over the same endpoint the chat
 * already reads through `createRunStream`. Duplicating the reader cost three things it
 * never noticed: a CRLF-framed stream never split into blocks, a final frame that
 * arrived without a trailing blank line was dropped on the floor, and a connection that
 * died before the terminal event was not retried.
 */
function streamAdmittedInteraction(
  sessionId: string,
  body: InvokeStreamBody,
  onChunk: (chunk: ModelStreamChunkDto) => void,
  onError?: (error: Error) => void,
): AbortController {
  const storageId = sessionStorageId(sessionId)
  const controller = new AbortController()
  let handle: RunStreamHandle | null = null
  controller.signal.addEventListener('abort', () => handle?.disconnect())
  void (async () => {
    try {
      // 模式身份 = 已发布的 ModeVersion；六值 agent_mode 不再发送（Core 收到会 400 unknown_field）。
      const interactionBody: Record<string, unknown> = {
        content: body.content,
        client_message_id: body.client_message_id,
        dispatch_mode: 'parallel',
      }
      if (body.mode_version_id) interactionBody.mode_version_id = body.mode_version_id
      if (body.target_run_id) interactionBody.target_run_id = body.target_run_id
      if (body.expected_context_revision != null) interactionBody.expected_context_revision = body.expected_context_revision
      const receipt = await request<{ run_id?: string; stream_cursor?: number }>(
        `/api/v1/sessions/${encodeURIComponent(sessionId)}/interactions`,
        { method: 'POST', body: JSON.stringify(interactionBody), signal: controller.signal, storageId },
      )
      const runId = receipt.run_id
      if (!runId) throw new Error('Interaction admission did not return a run_id.')
      handle = createRunStream({
        storageId,
        runId,
        cursor: receipt.stream_cursor ?? 0,
        // One-shot on purpose: these callers generate a commit message, and a
        // backoff-retry loop is not what a cancelled generation should become.
        autoReconnect: false,
        onChunk: (chunk) => onChunk(modelStreamChunkOf(chunk)),
        onError,
      })
      if (controller.signal.aborted) {
        handle.disconnect()
        return
      }
      handle.connect()
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') return
      onError?.(error instanceof Error ? error : new Error(String(error)))
    }
  })()
  return controller
}

export const api = {
  gatewayUrl,
  listStorageScopes: (signal?: AbortSignal) => request<StorageScopeDto[]>('/api/v1/storage/scopes', { storageId: 'user', signal }),
  openStorageScope: (input: { project_path: string; name?: string } & Partial<StorageConfigureInput> & Partial<import('@/lib/workspaces').WorkspaceInput>) => request<StorageScopeDto>('/api/v1/storage/scopes/open', { method: 'POST', storageId: 'user', body: JSON.stringify(input) }),
  previewWorkspace: (project_path: string) => request<import('@/lib/workspaces').WorkspacePreview>('/api/v1/storage/scopes/preview', { method: 'POST', storageId: 'user', body: JSON.stringify({ project_path }) }),
  readWorkspace: (storageId: string) => request<import('@/lib/workspaces').WorkspaceDefinition>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/workspace`, { storageId: 'user' }),
  saveWorkspace: (storageId: string, input: import('@/lib/workspaces').WorkspaceInput, hash: string) => request<import('@/lib/workspaces').WorkspaceDefinition>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/workspace`, { method: 'PUT', storageId: 'user', headers: { 'If-Match': `"${hash}"` }, body: JSON.stringify(input) }),
  getStorageDiagnostics: (storageId: string) => request<StorageDiagnosticResult>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/diagnostics`, { storageId: 'user' }),
  setWorkspaceModelSource: (storageId: string, followUser: boolean) => request<{ storage_id: string; follow_user_models: boolean }>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/model-source`, { method: 'POST', storageId: 'user', body: JSON.stringify({ follow_user: followUser }) }),
  getStorageStats: (storageId: string) => request<StorageStatsDto>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/stats`, { storageId: 'user' }),
  previewStorageCleanup: (storageId: string, category: string) => request<StorageCleanupPreviewDto>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/cleanup-preview`, { method: 'POST', storageId: 'user', body: JSON.stringify({ category }) }),
  cleanupStorage: (storageId: string, previewId: string) => request<unknown>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/cleanup`, { method: 'POST', storageId: 'user', body: JSON.stringify({ preview_id: previewId }) }),
  previewContentCollection: (storageId: string) => request<StorageContentPreviewDto>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/content-preview`, { method: 'POST', storageId: 'user' }),
  collectContent: (storageId: string, previewId: string) => request<unknown>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/content-collect`, { method: 'POST', storageId: 'user', body: JSON.stringify({ preview_id: previewId }) }),
  previewStorageDeletion: (storageId: string) => request<StorageDeletePreviewDto>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/storage-delete-preview`, { method: 'POST', storageId: 'user' }),
  deleteStorage: (storageId: string, previewId: string) => request<unknown>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/storage-delete`, { method: 'POST', storageId: 'user', body: JSON.stringify({ preview_id: previewId }) }),
  unregisterStorageScope: (storageId: string) => request<void>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}`, { method: 'DELETE', storageId: 'user' }),
  configureStorage: (storageId: string, input: StorageConfigureInput) => request<StorageScopeDto>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/configure`, { method: 'POST', storageId: 'user', body: JSON.stringify(input) }),
  closeStorageScope: (storageId: string, signal?: AbortSignal) => request<void>(`/api/v1/storage/scopes/${encodeURIComponent(storageId)}/close`, { method: 'POST', storageId: 'user', signal }),
  exportStorageScope: async (storageId: string) => {
    const response = await storageFetch(`${gatewayUrl}/api/v1/storage/scopes/${encodeURIComponent(storageId)}/export`, { method: 'POST', storageId: 'user' })
      .catch((error: unknown) => {
        // Same wording as every other request when the backend cannot be reached.
        if (!(error instanceof TypeError)) throw error
        throw new Error(i18n.global.t('common.backendUnreachable', { origin: new URL(gatewayUrl).origin, reason: error.message }))
      })
    if (!response.ok) {
      const data = await response.json().catch(() => null)
      throw Object.assign(new Error(extractErrorMessage(data, response.statusText)), { status: response.status })
    }
    return { blob: await response.blob(), filename: response.headers.get('content-disposition')?.match(/filename="?([^";]+)"?/)?.[1] ?? `tinadec-${storageId}.zip` }
  },
  listConfigurationDocuments: (storageId: string) => request<string[]>('/api/v1/configuration/documents', { storageId }),
  getConfigurationDocument: (storageId: string, documentId: string) => request<ConfigurationDocumentDto>(`/api/v1/configuration/documents/${encodeURIComponent(documentId)}`, { storageId }),
  validateConfigurationDocument: (storageId: string, documentId: string, text: string) => request<{ valid: boolean; diagnostics: Array<Record<string, unknown>> }>(`/api/v1/configuration/documents/${encodeURIComponent(documentId)}/validate`, { method: 'POST', storageId, body: JSON.stringify({ text }) }),
  saveConfigurationDocument: (storageId: string, documentId: string, text: string, hash: string) => request<ConfigurationDocumentDto>(`/api/v1/configuration/documents/${encodeURIComponent(documentId)}`, { method: 'PUT', storageId, headers: { 'if-match': `"${hash}"` }, body: JSON.stringify({ text }) }),
  getToolSettingsSchema: () => request<ToolSettingsSchema>('/api/v1/tools/settings/schema'),
  getToolSettingsDefaults: () => request<ToolSettingsDocument>('/api/v1/tools/settings/defaults'),
  saveToolSettingsDefaults: (settings: Record<string, unknown>, revision: number, projectId?: string) => request<ToolSettingsDocument>(`/api/v1/tools/settings/defaults${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`, { method: 'PUT', headers: { 'if-match': `"${revision}"` }, body: JSON.stringify({ settings }) }),
  getAgentToolSettings: (agentId: string) => request<ToolSettingsDocument>(`/api/v1/tools/settings/agents/${encodeURIComponent(agentId)}`),
  saveAgentToolSettings: (agentId: string, settings: Record<string, unknown>, revision: number, projectId?: string) => request<ToolSettingsDocument>(`/api/v1/tools/settings/agents/${encodeURIComponent(agentId)}${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`, { method: 'PUT', headers: { 'if-match': `"${revision}"` }, body: JSON.stringify({ settings }) }),
  resetAgentToolSettings: (agentId: string, revision: number) => request<ToolSettingsDocument>(`/api/v1/tools/settings/agents/${encodeURIComponent(agentId)}`, { method: 'DELETE', headers: { 'if-match': `"${revision}"` } }),
  getEffectiveToolSettings: (agentId?: string, projectId?: string) => {
    const query = new URLSearchParams()
    if (agentId) query.set('agent_id', agentId)
    if (projectId) query.set('project_id', projectId)
    return request<ToolSettingsEffective>(`/api/v1/tools/settings/effective${query.size ? `?${query}` : ''}`)
  },
  getToolCapabilities: (agentId?: string, projectId?: string) => {
    const query = new URLSearchParams()
    if (agentId) query.set('agent_id', agentId)
    if (projectId) query.set('project_id', projectId)
    return request<ToolCapabilities>(`/api/v1/tools/settings/capabilities${query.size ? `?${query}` : ''}`)
  },
  listToolMcpResources: (projectId?: string) => request<ToolMcpResource[]>(`/api/v1/tools/mcp/servers${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`),
  createToolMcpResource: (body: ToolMcpInput, projectId?: string) => request<ToolMcpResource>(`/api/v1/tools/mcp/servers${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`, { method: 'POST', headers: { 'if-match': '"0"' }, body: JSON.stringify(body) }),
  saveToolMcpResource: (resourceId: string, body: ToolMcpInput, revision: number, projectId?: string) => request<ToolMcpResource>(`/api/v1/tools/mcp/servers/${encodeURIComponent(resourceId)}${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`, { method: 'PUT', headers: { 'if-match': `"${revision}"` }, body: JSON.stringify(body) }),
  deleteToolMcpResource: (resourceId: string, revision: number, projectId?: string) => request<void>(`/api/v1/tools/mcp/servers/${encodeURIComponent(resourceId)}${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`, { method: 'DELETE', headers: { 'if-match': `"${revision}"` } }),
  testToolMcpResource: (resourceId: string, projectId?: string) => request<Record<string, unknown>>(`/api/v1/tools/mcp/servers/${encodeURIComponent(resourceId)}/test${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`, { method: 'POST' }),
  previewToolMcpProgram: (resourceId: string, projectId?: string, action: 'install' | 'uninstall' = 'install') =>
    request<ManagedMcpProgramPreview>(`/api/v1/tools/mcp/servers/${encodeURIComponent(resourceId)}/install-preview?${new URLSearchParams({ action, ...(projectId ? { project_id: projectId } : {}) })}`, { method: 'POST' }),
  installToolMcpProgram: (resourceId: string, projectId?: string, action: 'install' | 'uninstall' = 'install') =>
    request<UserToolActionDto>(`/api/v1/tools/mcp/servers/${encodeURIComponent(resourceId)}/install?${new URLSearchParams({ action, ...(projectId ? { project_id: projectId } : {}) })}`, { method: 'POST' }),
  listToolSkills: (projectId?: string) => request<{ skills: ToolSkillResource[]; diagnostics: string[]; source: string }>(`/api/v1/tools/skills${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`),
  getToolSkill: (resourceId: string, projectId?: string) => request<ToolSkillResource>(`/api/v1/tools/skills/${encodeURIComponent(resourceId)}${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`),
  importToolSkill: (body: { scope: 'shared' | 'project'; project_id?: string; name: string; content: string; files?: Record<string, string>; enabled?: boolean }) => request<ToolSkillResource>('/api/v1/tools/skills/import', { method: 'POST', headers: { 'if-match': '"0"' }, body: JSON.stringify(body) }),
  getToolSkillFile: (resourceId: string, path: string, projectId?: string) => request<{ path: string; content_hash: string; size_bytes: number; content: string | null; base64: string }>(`/api/v1/tools/skills/${encodeURIComponent(resourceId)}/files?${new URLSearchParams({ path, ...(projectId ? { project_id: projectId } : {}) })}`),
  saveToolSkill: (resourceId: string, body: { content?: string; enabled?: boolean; files?: Record<string, string>; replace_files?: boolean; expected_file_hash?: string }, revision: number, projectId?: string) => request<ToolSkillResource>(`/api/v1/tools/skills/${encodeURIComponent(resourceId)}${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`, { method: 'PUT', headers: { 'if-match': `"${revision}"` }, body: JSON.stringify(body) }),
  deleteToolSkill: (resourceId: string, revision: number, projectId?: string) => request<ToolSkillResource>(`/api/v1/tools/skills/${encodeURIComponent(resourceId)}${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`, { method: 'DELETE', headers: { 'if-match': `"${revision}"` } }),
  copyToolSkillToProject: (resourceId: string, projectId: string) => request<ToolSkillResource>(`/api/v1/tools/skills/${encodeURIComponent(resourceId)}/copy-to-project?project_id=${encodeURIComponent(projectId)}`, { method: 'POST' }),
  // Participant and conversation writes below are actor-scoped: Core re-verifies the authenticated
  // principal against every actor_id on each call, so a stale local identity fails closed here.
  tinaChatParticipants: (query?: string, signal?: AbortSignal) => {
    const search = new URLSearchParams()
    if (query) search.set('query', query)
    return request<TinaChatParticipant[]>(`/api/v1/tina-chat/participants?${search}`, { signal, cache: 'no-store' })
  },
  tinaChatRegisterParticipant: (body: components['schemas']['TinaChatRegisterParticipantRequest']) =>
    request<TinaChatParticipant>('/api/v1/tina-chat/participants', { method: 'POST', body: JSON.stringify(body) }),
  tinaChatUpdateParticipant: (id: string, body: components['schemas']['TinaChatUpdateParticipantRequest']) =>
    request<TinaChatParticipant>(`/api/v1/tina-chat/participants/${encodeURIComponent(id)}`, { method: 'PATCH', body: JSON.stringify(body) }),
  tinaChatInbox: (id: string, params: { after_sequence?: number; limit?: number } = {}, signal?: AbortSignal) => {
    const search = new URLSearchParams()
    for (const [key, value] of Object.entries(params)) if (value !== undefined) search.set(key, String(value))
    return request<TinaChatInboxPage>(`/api/v1/tina-chat/participants/${encodeURIComponent(id)}/inbox?${search}`, { signal, cache: 'no-store' })
  },
  tinaChatAcknowledge: (id: string, messageId: string) =>
    request<void>(`/api/v1/tina-chat/participants/${encodeURIComponent(id)}/inbox/${encodeURIComponent(messageId)}/ack`, { method: 'POST' }),
  tinaChatConversations: (actorId: string, signal?: AbortSignal) =>
    request<TinaChatConversation[]>(`/api/v1/tina-chat/conversations?actor_id=${encodeURIComponent(actorId)}`, { signal, cache: 'no-store' }),
  tinaChatCreateConversation: (body: components['schemas']['TinaChatCreateConversationRequest']) =>
    request<TinaChatConversation>('/api/v1/tina-chat/conversations', { method: 'POST', body: JSON.stringify(body) }),
  tinaChatConversation: (id: string, actorId: string, signal?: AbortSignal) =>
    request<TinaChatConversation>(`/api/v1/tina-chat/conversations/${encodeURIComponent(id)}?actor_id=${encodeURIComponent(actorId)}`, { signal, cache: 'no-store' }),
  tinaChatMembers: (id: string, actorId: string, signal?: AbortSignal) =>
    request<TinaChatMember[]>(`/api/v1/tina-chat/conversations/${encodeURIComponent(id)}/members?actor_id=${encodeURIComponent(actorId)}`, { signal, cache: 'no-store' }),
  tinaChatChangeMember: (id: string, body: components['schemas']['TinaChatMemberRequest']) =>
    request<TinaChatConversation>(`/api/v1/tina-chat/conversations/${encodeURIComponent(id)}/members`, { method: 'PUT', body: JSON.stringify(body) }),
  tinaChatMessages: (id: string, params: { actor_id: string; after_sequence?: number; limit?: number }, signal?: AbortSignal) => {
    const search = new URLSearchParams(Object.entries(params).map(([key, value]) => [key, String(value)]))
    return request<components['schemas']['TinaChatMessagePage']>(`/api/v1/tina-chat/conversations/${encodeURIComponent(id)}/messages?${search}`, { signal, cache: 'no-store' })
  },
  tinaChatSendMessage: (id: string, body: components['schemas']['TinaChatSendMessageRequest']) =>
    request<TinaChatMessage>(`/api/v1/tina-chat/conversations/${encodeURIComponent(id)}/messages`, { method: 'POST', body: JSON.stringify(body) }),
  tinaChatPolicy: (signal?: AbortSignal) => request<TinaChatWorkspacePolicy>('/api/v1/tina-chat/workspace-policy', { signal, cache: 'no-store' }),
  tinaChatSetPolicy: (body: components['schemas']['TinaChatWorkspacePolicyRequest']) =>
    request<TinaChatWorkspacePolicy>('/api/v1/tina-chat/workspace-policy', { method: 'PUT', body: JSON.stringify(body) }),
  tinaChatIntents: (id: string, actorId: string, signal?: AbortSignal) =>
    request<TinaChatIntent[]>(`/api/v1/tina-chat/conversations/${encodeURIComponent(id)}/intents?actor_id=${encodeURIComponent(actorId)}`, { signal, cache: 'no-store' }),
  tinaChatGenerateIntent: (id: string, body: components['schemas']['TinaChatGenerateIntentRequest']) =>
    request<TinaChatIntent>(`/api/v1/tina-chat/conversations/${encodeURIComponent(id)}/intents/generate`, { method: 'POST', body: JSON.stringify(body) }),
  tinaChatDecideIntent: (id: string, intentId: string, body: components['schemas']['TinaChatIntentDecisionRequest']) =>
    request<TinaChatIntent>(`/api/v1/tina-chat/conversations/${encodeURIComponent(id)}/intents/${encodeURIComponent(intentId)}/decision`, { method: 'POST', body: JSON.stringify(body) }),
  tinaChatExecuteIntent: (id: string, intentId: string, body: components['schemas']['TinaChatExecuteIntentRequest']) =>
    request<TinaChatExecution>(`/api/v1/tina-chat/conversations/${encodeURIComponent(id)}/intents/${encodeURIComponent(intentId)}/execute`, { method: 'POST', body: JSON.stringify(body) }),
  health: () => request<Record<string, unknown>>('/api/v1/health'),
  /**
   * Health as the About page reads it. When only Core is unreachable Gateway answers 503 with its
   * own fingerprint, which `health()` turns into an error that keeps no body; this reads the
   * fingerprint and returns only what it says. A network failure still throws.
   */
  healthProbe: async (): Promise<HealthProbeDto> => {
    const path = '/api/v1/health'
    const storageId = captureStorageId(path)
    const headers = storageHeaders(path, { storageId })
    headers.set('accept', 'application/json')
    const { response, text } = await readBackendResponse(`${gatewayUrl}${normalizeStorageRequest(path).path}`, path, { headers }, { storageId })
    let data: Record<string, unknown> = {}
    try {
      const parsed: unknown = text ? JSON.parse(text) : null
      if (parsed && typeof parsed === 'object') data = parsed as Record<string, unknown>
    } catch { /* not JSON: it says nothing about either service */ }
    return {
      gateway: data.gateway === 'ok',
      core: response.ok && data.status === 'ok',
      core_version: response.ok && typeof data.version === 'string' ? data.version : null,
    }
  },
  readiness: (options?: { signal?: AbortSignal }) => request<RuntimeReadinessReceiptDto>('/api/v1/readiness', options),
  getToolLayerReadiness: (options?: { signal?: AbortSignal }) => request<ToolLayerReadinessReceiptDto>('/api/v1/tool-layer-readiness', options),
  listProjects: (options?: { signal?: AbortSignal; storageId?: string; lifecycleStatus?: 'active' | 'archived' | 'trashed' }) => request<ProjectDto[]>(`/api/v1/projects${options?.lifecycleStatus ? '?lifecycle_status=' + encodeURIComponent(options.lifecycleStatus) : ''}`, options),
  createProject: (name: string, path: string) => request<ProjectDto>('/api/v1/projects', {
    method: 'POST',
    body: JSON.stringify({ name, path })
  }),
  listSessions: (projectId?: string, signal?: AbortSignal, storageId?: string, lifecycleStatus?: 'active' | 'archived' | 'trashed') => {
    const query = new URLSearchParams()
    if (projectId) query.set('project_id', projectId)
    if (lifecycleStatus) query.set('lifecycle_status', lifecycleStatus)
    return request<SessionDto[]>(`/api/v1/sessions${query.size ? '?' + query : ''}`, { signal, storageId, cache: 'no-store' })
  },
  // mode_version_id decides which agent holds the conversation. Omitted = the workspace default.
  createSession: (
    projectId?: string | null,
    title?: string,
    modeVersionId?: string | null,
    viewMode: 'flat' | 'space' = 'flat',
    settings?: SessionSettingsUpdate & { origin?: string | null; board_task_id?: string | null },
  ) => request<SessionDto>('/api/v1/sessions', {
    method: 'POST',
    body: JSON.stringify({
      project_id: projectId ?? undefined,
      title,
      mode_version_id: modeVersionId ?? undefined,
      ...(viewMode === 'space' ? { view_mode: viewMode } : {}),
      ...settings,
    }),
  }).then(session => {
    if (viewMode === 'space' && session.view_mode !== 'space') throw new Error(i18n.global.t('space.sessionUnsupported'))
    return session
  }),
  migrateSession: (sessionId: string, payload: { target_storage_id: string; target_project_id?: string }) => request<SessionTransferDto>(`/api/v1/sessions/${sessionId}/migrate`, {
    method: 'POST',
    body: JSON.stringify(payload)
  }),
  getSessionTransfer: (transferId: string) => request<SessionTransferDto>(`/api/v1/session-transfers/${encodeURIComponent(transferId)}`, { storageId: 'user' }),
  updateSessionTitle: (sessionId: string, title: string) => request<SessionDto>(`/api/v1/sessions/${sessionId}`, {
    method: 'PATCH',
    body: JSON.stringify({ title })
  }),
  updateSessionSettings: (sessionId: string, settings: SessionSettingsUpdate) => request<SessionDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}`, {
    method: 'PATCH', body: JSON.stringify(settings),
  }),
  listMessages: (sessionId: string, signal?: AbortSignal) => request<MessageDto[]>(`/api/v1/sessions/${sessionId}/messages`, { signal, cache: 'no-store' }),
  postMessage: (sessionId: string, content: string) => request<MessageDto>(`/api/v1/sessions/${sessionId}/messages`, {
    method: 'POST',
    body: JSON.stringify({ content })
  }),
  revertSessionMessage: (sessionId: string, messageId: string) => request<SessionHistoryRevertDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/messages/${encodeURIComponent(messageId)}/revert`, { method: 'POST' }),
  getOrchestrationSnapshot: (sessionId: string, signal?: AbortSignal) => request<OrchestrationSnapshotDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/orchestration`, { signal, cache: 'no-store' }),
  // Session organization + graph (Core OrganizationEndpoints, proxied verbatim by the Gateway).
  /** The session's organization, or null before its first run creates one (404 `organization_not_started`). */
  getOrganization: async (sessionId: string): Promise<OrganizationDto | null> => {
    try {
      return await request<OrganizationDto>(organizationPath(sessionId), { cache: 'no-store' })
    } catch (error) {
      const { status, code } = error as { status?: number; code?: string | null }
      if (status === 404 && code === 'organization_not_started') return null
      throw error
    }
  },
  readOrganizationRoom: (sessionId: string, roomId: string, afterSequence?: number, limit?: number) =>
    request<OrganizationMessagePageDto>(organizationPath(sessionId, `/rooms/${encodeURIComponent(roomId)}/messages${querySuffix({ after_sequence: afterSequence, limit })}`), { cache: 'no-store' }),
  postOrganizationMessage: (sessionId: string, roomId: string, body: PostOrganizationMessageInput) =>
    request<OrganizationMessageDto>(organizationPath(sessionId, `/rooms/${encodeURIComponent(roomId)}/messages`), { method: 'POST', body: JSON.stringify(body) }),
  listOrganizationReports: (sessionId: string, status?: OrganizationReportDto['status'], limit?: number) =>
    request<OrganizationReportPageDto>(organizationPath(sessionId, `/reports${querySuffix({ status, limit })}`), { cache: 'no-store' }),
  /** Recorded against the revision the report was read at: 412 = stale revision, 409 `report_closed` = already decided. */
  decideOrganizationReport: (sessionId: string, reportId: string, body: OrganizationReportDecisionInput) =>
    request<OrganizationReportDto>(organizationPath(sessionId, `/reports/${encodeURIComponent(reportId)}/decision`), { method: 'POST', body: JSON.stringify(body) }),
  /** The owner narrows (or restores) one machine member's visibility into run internals. */
  setOrganizationMemberVisibility: (sessionId: string, participantId: string, visibilityScope: 'down' | 'own' | null) =>
    request<OrganizationMemberDto>(organizationPath(sessionId, `/members/${encodeURIComponent(participantId)}`), { method: 'PATCH', body: JSON.stringify({ visibility_scope: visibilityScope }) }),
  /** Search the session's evidence archive (verbatim task results, reports, member conclusions, summaries). */
  recallEvidence: (sessionId: string, q: string, params: { kinds?: string[]; run_id?: string; limit?: number } = {}) =>
    request<EvidenceRecallDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/evidence${querySuffix({
      q, kinds: params.kinds?.join(','), run_id: params.run_id, limit: params.limit,
    })}`, { cache: 'no-store' }),
  /** The workspace's environments (what the environment steward hands out) and who holds each slot. */
  listEnvironments: () => request<EnvironmentDto[]>('/api/v1/environments', { cache: 'no-store' }),
  /** 409 `environment_exists`; 400 `environment_connection_invalid` when the connection carries a credential. */
  registerEnvironment: (body: EnvironmentRegisterInput) =>
    request<EnvironmentDto>('/api/v1/environments', { method: 'POST', body: JSON.stringify(body) }),
  updateEnvironment: (environmentId: string, body: EnvironmentUpdateInput) =>
    request<EnvironmentDto>(`/api/v1/environments/${encodeURIComponent(environmentId)}`, { method: 'PATCH', body: JSON.stringify(body) }),
  getSessionTopology: (sessionId: string, params: SessionTopologyQuery = {}, signal?: AbortSignal) =>
    request<SessionTopologyDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/topology${querySuffix({
      run_id: params.run_id, include_finished: params.include_finished, max_runs: params.max_runs, max_tasks: params.max_tasks,
    })}`, { cache: 'no-store', signal }),
  listToolExecutions: (sessionId: string, params: { run_id?: string; limit?: number } = {}, signal?: AbortSignal) => {
    const search = new URLSearchParams();
    if (params.run_id) search.set('run_id', params.run_id);
    if (params.limit !== undefined) search.set('limit', String(params.limit));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<ToolExecutionTimelineItemDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/tool-executions${suffix}`, { signal, cache: 'no-store' });
  },
  getToolExecutionPreview: (executionId: string, params: { max_bytes?: number } = {}, signal?: AbortSignal) => {
    const suffix = params.max_bytes === undefined ? '' : `?max_bytes=${params.max_bytes}`
    return request<ToolExecutionPreviewDto>(`/api/v1/tool-executions/${encodeURIComponent(executionId)}/preview${suffix}`, { signal, cache: 'no-store' })
  },
  listRuns: (sessionId: string, signal?: AbortSignal) => request<OrchestrationRunDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/runs`, { signal, cache: 'no-store' }),
  listTaskNodes: (sessionId: string) => request<TaskNodeDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/task-nodes`),
  listContextPacks: (sessionId: string) => request<ContextPackDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/context-packs`),
  listSupervisionFindings: (sessionId: string) => request<SupervisionFindingDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/supervision-findings`),
  listApprovals: (sessionId?: string, status?: string, signal?: AbortSignal) => {
    const search = new URLSearchParams();
    if (status) search.set('status', status);
    if (sessionId) search.set('session_id', sessionId);
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<ApprovalDto[]>(`/api/v1/approvals${suffix}`, { signal, cache: 'no-store' });
  },
  listApprovalRules: (sessionId: string, signal?: AbortSignal) =>
    request<ApprovalRuleDto[]>(`/api/v1/approval-rules?sessionId=${encodeURIComponent(sessionId)}`, { signal, cache: 'no-store' }),
  createApprovalRule: (input: CreateApprovalRuleInput) =>
    request<ApprovalRuleDto>('/api/v1/approval-rules', { method: 'POST', body: JSON.stringify(input) }),
  revokeApprovalRule: (id: string) =>
    request<void>(`/api/v1/approval-rules/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  /** The delegated gates of one approval, or null when it was never delegated (404). */
  getApprovalGates: async (approvalId: string): Promise<ApprovalGatesDto | null> => {
    try {
      return await request<ApprovalGatesDto>(`/api/v1/approvals/${encodeURIComponent(approvalId)}/gates`, { cache: 'no-store' })
    } catch (error) {
      if ((error as { status?: number }).status === 404) return null
      throw error
    }
  },
  listPermissionRequests: (params: { status?: string; run_id?: string; task_id?: string } = {}) => {
    const search = new URLSearchParams();
    if (params.status) search.set('status', params.status);
    if (params.run_id) search.set('run_id', params.run_id);
    if (params.task_id) search.set('task_id', params.task_id);
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<PermissionRequestDto[]>(`/api/v1/governance/permission-requests${suffix}`);
  },
  getPermissionRequest: (requestId: string) => request<PermissionResolutionDto>(`/api/v1/governance/permission-requests/${encodeURIComponent(requestId)}`),
  decidePermissionRequest: (requestId: string, input: PermissionDecisionInput) => request<PermissionResolutionDto>(`/api/v1/governance/permission-requests/${encodeURIComponent(requestId)}/decision`, {
    method: 'POST',
    body: JSON.stringify(input),
  }),
  listUserToolActions: (status?: string) => request<UserToolActionDto[]>(`/api/v1/user/tool-actions${status ? `?status=${encodeURIComponent(status)}` : ''}`),
  createUserToolAction: (input: CreateUserToolActionInput) => request<UserToolActionDto>('/api/v1/user/tool-actions', {
    method: 'POST',
    body: JSON.stringify(input),
  }),
  getUserToolAction: (actionId: string) => request<UserToolActionDto>(`/api/v1/user/tool-actions/${encodeURIComponent(actionId)}`),
  resumeUserToolAction: (actionId: string) => request<UserToolActionDto>(`/api/v1/user/tool-actions/${encodeURIComponent(actionId)}/resume`, {
    method: 'POST',
  }),
  overrideUserToolActionSnapshot: (actionId: string, reason: string) => request<UserToolActionDto>(`/api/v1/user/tool-actions/${encodeURIComponent(actionId)}/snapshot-override`, {
    method: 'POST',
    body: JSON.stringify({ reason }),
  }),
  decideUserToolActionRecovery: (actionId: string, input: RecoveryDecisionInput) => request<UserToolActionDto>(`/api/v1/user/tool-actions/${encodeURIComponent(actionId)}/recovery-decision`, {
    method: 'POST',
    body: JSON.stringify(input),
  }),
  getRunAgentLineage: (runId: string) => request<AgentLineageEntryDto[]>(`/api/v1/runs/${encodeURIComponent(runId)}/agent-lineage`),
  listWorkspaceSnapshots: (projectId: string) => request<SnapshotDto[]>(`/api/v1/projects/${encodeURIComponent(projectId)}/snapshots`),
  getWorkspaceSnapshot: (snapshotId: string) => request<SnapshotDto>(`/api/v1/workspace-snapshots/${encodeURIComponent(snapshotId)}`),
  restoreWorkspaceSnapshot: (snapshotId: string, input: { idempotency_key?: string; expected_workspace_hash?: string; allow_conflicts?: boolean } = {}) => request<Record<string, unknown>>(`/api/v1/workspace-snapshots/${encodeURIComponent(snapshotId)}/restore`, {
    method: 'POST',
    body: JSON.stringify(input),
  }),
  /**
   * Per-file review of one snapshot: what moved, what each path held before, and
   * undoing a single row. Absent keys mean null — this host drops nulls on write —
   * which is why the optional fields below are `| null` *and* optional.
   */
  listWorkspaceSnapshotFiles: (snapshotId: string) => request<WorkspaceFileChangeDto[]>(`/api/v1/workspace-snapshots/${encodeURIComponent(snapshotId)}/files`),
  getWorkspaceSnapshotFileDiff: (snapshotId: string, path: string) =>
    request<WorkspaceFileDiffDto>(`/api/v1/workspace-snapshots/${encodeURIComponent(snapshotId)}/files/diff?path=${encodeURIComponent(path)}`),
  /**
   * `expectedSha256` is required and must be the value the reviewer was just shown; pass the empty
   * string to assert "this file was absent", which is a different fact from not knowing.
   */
  restoreWorkspaceSnapshotFile: (snapshotId: string, path: string, expectedSha256: string) =>
    request<WorkspaceFileChangeDto>(`/api/v1/workspace-snapshots/${encodeURIComponent(snapshotId)}/files/restore`, {
      method: 'POST',
      body: JSON.stringify({ path, expected_sha256: expectedSha256 }),
    }),
  /**
   * Decide a pending approval. `scope: 'run'` is "always allow this tool for this
   * session": Core mints a run-scoped pre-authorization plus a run-scoped capability
   * grant, and releases the run's other pending requests for the same tool. Without
   * a scope the decision stays a one-shot.
   */
  decideApproval: (approvalId: string, decision: 'approved' | 'rejected', reason?: string | null, scope?: 'once' | 'run') => request<ApprovalDto>(`/api/v1/approvals/${approvalId}/decision`, {
    method: 'POST',
    body: JSON.stringify({
      decision,
      ...(reason ? { reason } : {}),
      ...(scope ? { scope } : {}),
    })
  }),
  createPreAuthorization: (input: CreatePreAuthorizationInput) => request<PreAuthorizationDto>('/api/v1/approvals/pre-authorizations', {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  listModelProviderTemplates: (options?: { signal?: AbortSignal }) => cachedRead(catalogKey('model-provider-templates'), () => request<ModelProviderTemplateDto[]>('/api/v1/model-provider-templates'), options?.signal, CATALOG_READ_CACHE_TTL_MS),
  listModelProviders: (options?: { signal?: AbortSignal }) => cachedRead(catalogKey('model-providers'), () => request<ModelProviderInstanceDto[]>('/api/v1/model-providers'), options?.signal),
  discoverHarnesses: () => request<CliDiscoveryResultDto>('/api/v1/model-providers/harnesses/discover'),
  connectHarness: (input: ConnectHarnessInput) => request<ModelProviderInstanceDto>('/api/v1/model-providers/harnesses/connect', {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  refreshProviderModels: (providerInstanceId: string) => request<ModelDiscoveryResultDto>(`/api/v1/model-providers/${encodeURIComponent(providerInstanceId)}/models/refresh`, {
    method: 'POST'
  }),
  // Readiness answers how the runtime behaves right now, not what is configured, so it stays
  // on the seconds tier even though the model center reads it beside the catalogs.
  getModelReadiness: (options?: { signal?: AbortSignal }) => cachedRead(catalogKey('model-readiness'), () => request<ModelReadinessReceiptDto>('/api/v1/model-readiness'), options?.signal),
  // A catalog receipt describes what is configured, not how a runtime behaves: it rides the
  // catalog window with the templates and adapters it belongs to.
  getModelCatalogReadiness: (options?: { signal?: AbortSignal }) => cachedRead(catalogKey('model-catalog-readiness'), () => request<ModelCatalogReadinessReceiptDto>('/api/v1/model-catalog-readiness'), options?.signal, CATALOG_READ_CACHE_TTL_MS),
  /**
   * The model center's cold-start reads in one round trip. Rides the short tier: readiness
   * rides inside it, so it must not be held for the catalog window.
   */
  getModelCenterSnapshot: (options?: { signal?: AbortSignal }) => cachedRead(catalogKey('model-center-snapshot'), () => request<ModelCenterSnapshotDto>('/api/v1/model-center/snapshot'), options?.signal),
  createModelProvider: (provider: SaveModelProviderInstanceInput) => request<ModelProviderInstanceDto>('/api/v1/model-providers', {
    method: 'POST',
    body: JSON.stringify(provider)
  }),
  saveModelProvider: (providerId: string, provider: SaveModelProviderInstanceInput, options?: { expected_revision?: number; force?: boolean }) => {
    const suffix = options?.force ? '?force=true' : ''
    return request<ModelProviderInstanceDto>(`/api/v1/model-providers/${encodeURIComponent(providerId)}${suffix}`, {
      method: 'PUT',
      headers: options?.expected_revision !== undefined ? { 'if-match': `"${options.expected_revision}"` } : undefined,
      body: JSON.stringify(provider)
    })
  },
  deleteModelProvider: (providerId: string, options?: { expected_revision?: number; force?: boolean }) => {
    const suffix = options?.force ? '?force=true' : ''
    return request<void>(`/api/v1/model-providers/${encodeURIComponent(providerId)}${suffix}`, {
      method: 'DELETE',
      headers: options?.expected_revision !== undefined ? { 'if-match': `"${options.expected_revision}"` } : undefined
    })
  },
  listModelRoutes: (options?: { signal?: AbortSignal }) => cachedRead(catalogKey('model-routes'), () => request<ModelRouteDto[]>('/api/v1/model-routes'), options?.signal),
  saveModelRoute: (purpose: string, candidates: ModelRouteWriteRequestDto | string, model?: string | null, options?: { expected_revision?: number }) => request<ModelRouteDto>(`/api/v1/model-routes/${encodeURIComponent(purpose)}`, {
    method: 'PUT',
    headers: options?.expected_revision !== undefined ? { 'if-match': `"${options.expected_revision}"` } : undefined,
    body: JSON.stringify(typeof candidates === 'string'
      ? { candidates: [{ provider_instance_id: candidates, model: model ?? null }] }
      : candidates)
  }),
  previewModelResolution: (input: ModelResolutionPreviewRequestDto) => request<ModelResolutionPreviewDto>('/api/v1/model-resolution/preview', {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  listModelReferences: (params: { provider_instance_id?: string; model?: string } = {}) => {
    const search = new URLSearchParams()
    if (params.provider_instance_id) search.set('provider_instance_id', params.provider_instance_id)
    if (params.model) search.set('model', params.model)
    const suffix = search.toString() ? `?${search.toString()}` : ''
    return request<ModelReferenceDto[]>(`/api/v1/model-references${suffix}`)
  },
  listModelInvocations: (params: { run_id?: string; agent_id?: string; mode_version_id?: string; provider_instance_id?: string; model?: string; status?: string; from?: string; to?: string; cursor?: string; limit?: number } = {}) => {
    const search = new URLSearchParams()
    for (const [key, value] of Object.entries(params)) if (value !== undefined && value !== '') search.set(key, String(value))
    const suffix = search.toString() ? `?${search.toString()}` : ''
    return request<ModelInvocationPageDto>(`/api/v1/model-invocations${suffix}`)
  },
  getModelSettings: () => request<ModelSettingsDto>('/api/v1/model-settings'),
  saveModelSettings: (settings: { base_url: string; model: string; api_key?: string; clear_api_key?: boolean }) => request<ModelSettingsDto>('/api/v1/model-settings', {
    method: 'PUT',
    body: JSON.stringify(settings)
  }),
  listMcpServers: (projectId?: string) => request<McpInventoryDto>(`/api/v1/mcp/servers${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`),
  listMcpServerTools: (serverId: string) =>
    request<McpServerToolsDto>(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/tools`),
  listAcpAdapters: (options?: { signal?: AbortSignal }) => cachedRead(catalogKey('acp-adapters'), () => request<AcpAdapterDto[]>('/api/v1/acp/adapters'), options?.signal, CATALOG_READ_CACHE_TTL_MS),
  listAgentModes: (options?: { signal?: AbortSignal }) => request<AgentModeDto[]>('/api/v1/agent-modes', options),
  listAgents: (options?: { signal?: AbortSignal }) => cachedRead(catalogKey('agents'), () => request<AgentDirectoryItemDto[]>('/api/v1/agents'), options?.signal),
  getAgent: (id: string, options?: { signal?: AbortSignal }) => request<AgentDefinitionDto>(`/api/v1/agents/${encodeURIComponent(id)}`, options),
  // ── New 5-tab config objects (snake_case, If-Match via etag) ──
  // `/api/v1/agents` is the directory projection; full definitions are fetched per id.
  listAgentDefinitions: async (): Promise<AgentDefinitionDto[]> => {
    const directory = await request<AgentDirectoryItemDto[]>('/api/v1/agents')
    if (!Array.isArray(directory)) return []
    const live = directory.filter((item) => item.source_kind !== 'missing_reference')
    const definitions = await Promise.all(live.map((item) =>
      request<AgentDefinitionDto>(`/api/v1/agents/${encodeURIComponent(item.id)}`).catch(() => null)
    ))
    return definitions.filter((item): item is AgentDefinitionDto => item !== null)
  },
  createAgentDraft: (body: Partial<AgentDefinitionDto>) => request<AgentDefinitionDto>('/api/v1/agents', { method: 'POST', body: JSON.stringify(body) }),
  updateAgentDraft: (id: string, body: Partial<AgentDefinitionDto>, etag?: string | number | null) => request<AgentDefinitionDto>(`/api/v1/agents/${encodeURIComponent(id)}/draft`, { method: 'PUT', headers: etag != null && etag !== '' ? { 'if-match': String(etag) } : {}, body: JSON.stringify(body) }),
  publishAgent: (id: string, etag?: string | number | null) => request<{ id: string; version: number; revision: number; snapshot: AgentDefinitionDto }>(`/api/v1/agents/${encodeURIComponent(id)}/publish`, { method: 'POST', headers: etag != null && etag !== '' ? { 'if-match': String(etag) } : {} }),
  // 用户级运行时绑定（配置体验改造 A）：pack 管理的智能体也可写，绕开 draft/publish。
  putAgentRuntimeBinding: (id: string, body: { mode: 'inherit' | 'route' | 'fixed'; provider_instance_id?: string | null; model?: string | null; route_purpose?: string | null; tool_scope?: string[] | null }) => request<Record<string, unknown>>(`/api/v1/agents/${encodeURIComponent(id)}/runtime-binding`, { method: 'PUT', body: JSON.stringify(body) }),
  archiveAgent: (id: string, revision?: number | null) => request<AgentDefinitionDto>(`/api/v1/agents/${encodeURIComponent(id)}/archive`, { method: 'POST', headers: revision != null ? { 'if-match': String(revision) } : {} }),
  getWorkspaceDefaults: (signal?: AbortSignal) => request<WorkspaceDefaultsDto>('/api/v1/workspace-defaults', { signal }),
  updateWorkspaceDefaultsDraft: (body: Partial<WorkspaceDefaultsDto>, revision?: number | null) => request<WorkspaceDefaultsDto>('/api/v1/workspace-defaults/draft', {
    method: 'PUT',
    headers: revision != null ? { 'if-match': String(revision) } : {},
    body: JSON.stringify(body),
  }),
  publishWorkspaceDefaults: (revision?: number | null) => request<WorkspaceDefaultsDto>('/api/v1/workspace-defaults/publish', {
    method: 'POST',
    headers: revision != null ? { 'if-match': String(revision) } : {},
  }),
  listAgentPacks: (signal?: AbortSignal) => request<AgentPackDto[]>('/api/v1/agent-packs', { signal }),
  getAgentPack: async (packId: string, options?: { signal?: AbortSignal }) => withResponseEtag(await requestResult<AgentPackDetailDto>(`/api/v1/agent-packs/${encodeURIComponent(packId)}`, options)),
  // 包管理：卸载（不可逆，带 revision 守卫）、启用/禁用、设为工作区默认。
  purgeAgentPack: (packId: string, revision: number | string) => request<AgentPackPurgeDto>(`/api/v1/agent-packs/${encodeURIComponent(packId)}`, {
    method: 'DELETE',
    headers: { 'if-match': typeof revision === 'number' ? `"${revision}"` : revision },
  }),
  setAgentPackEnabled: (packId: string, enabled: boolean) => request<AgentPackDto>(`/api/v1/agent-packs/${encodeURIComponent(packId)}/${enabled ? 'enable' : 'disable'}`, { method: 'POST' }),
  adoptAgentPackDefaults: (packId: string) => request<AgentPackDto>(`/api/v1/agent-packs/${encodeURIComponent(packId)}/adopt-defaults`, { method: 'POST' }),
  previewAgentPackInstall: async (envelope: AgentPackEnvelope) => withResponseEtag(await requestResult<AgentPackInstallPreviewDto>('/api/v1/agent-packs/install-preview', {
    method: 'POST',
    body: JSON.stringify(envelope),
  })),
  installAgentPack: async (
    packId: string,
    input: { preview_id: string; envelope: AgentPackEnvelope },
    options: { if_match?: string | null; idempotency_key: string },
  ) => withResponseEtag(await requestResult<AgentPackInstallResultDto>(`/api/v1/agent-packs/${encodeURIComponent(packId)}`, {
    method: 'PUT',
    headers: {
      ...(options.if_match ? { 'if-match': options.if_match } : {}),
      'idempotency-key': options.idempotency_key,
    },
    body: JSON.stringify(input),
  })),
  listAgentVersions: (id: string) => request<AgentVersionDto[]>(`/api/v1/agents/${encodeURIComponent(id)}/versions`),
  getAgentVersion: (id: string, versionId: string) => request<AgentVersionDto>(`/api/v1/agents/${encodeURIComponent(id)}/versions/${encodeURIComponent(versionId)}`),
  // agent-modes topology
  listAgentModeRuntimePolicies: (options?: { signal?: AbortSignal }) => request<AgentModeRuntimePolicyDefinition[]>('/api/v1/agent-mode-runtime-policies', options),
  listAgentModeTopologies: (options?: { signal?: AbortSignal }) => cachedRead(catalogKey('agent-modes'), () => request<AgentModeTopologyDto[]>('/api/v1/agent-modes'), options?.signal),
  createAgentModeDraft: (body: Omit<Partial<AgentModeTopologyDto>, 'nodes' | 'edges'> & Partial<AgentModeTopologyWriteDto>) => request<AgentModeTopologyDto>('/api/v1/agent-modes', { method: 'POST', body: JSON.stringify(body) }),
  getAgentModeTopology: (id: string) => request<AgentModeTopologyDto>(`/api/v1/agent-modes/${encodeURIComponent(id)}`),
  updateAgentModeDraft: (id: string, body: AgentModeDraftUpdate, etag?: string | number | null) => request<AgentModeTopologyDto>(`/api/v1/agent-modes/${encodeURIComponent(id)}/draft`, { method: 'PUT', headers: etag != null && etag !== '' ? { 'if-match': String(etag) } : {}, body: JSON.stringify(body) }),
  reopenAgentModeDraft: (id: string, body: Partial<AgentModeDraftUpdate> = {}, etag?: string | number | null) => request<AgentModeTopologyDto>(`/api/v1/agent-modes/${encodeURIComponent(id)}/draft`, { method: 'PUT', headers: etag != null && etag !== '' ? { 'if-match': String(etag) } : {}, body: JSON.stringify(body) }),
  publishAgentMode: (id: string, etag?: string | number | null) => request<{ id?: string; version?: number; revision?: number; topology_hash?: string; warning?: unknown }>(`/api/v1/agent-modes/${encodeURIComponent(id)}/publish`, { method: 'POST', headers: etag != null && etag !== '' ? { 'if-match': String(etag) } : {} }),
  cloneAgentMode: (id: string, body?: { display_name?: string; slug?: string }) => request<AgentModeTopologyDto>(`/api/v1/agent-modes/${encodeURIComponent(id)}/clone`, { method: 'POST', body: JSON.stringify(body ?? {}) }),
  deleteAgentMode: (id: string, etag?: string | number | null) => request<void>(`/api/v1/agent-modes/${encodeURIComponent(id)}`, { method: 'DELETE', headers: etag != null && etag !== '' ? { 'if-match': String(etag) } : {} }),
  archiveAgentMode: (id: string, etag?: string | number | null) => request<AgentModeTopologyDto>(`/api/v1/agent-modes/${encodeURIComponent(id)}/archive`, { method: 'POST', headers: etag != null && etag !== '' ? { 'if-match': String(etag) } : {} }),
  listAgentModeVersions: (id: string) => request<ModeVersionDto[]>(`/api/v1/agent-modes/${encodeURIComponent(id)}/versions`),
  // prompt pipelines
  listPromptPipelines: async (options?: { signal?: AbortSignal }): Promise<PromptPipelineDto[]> => {
    const list = await request<PromptPipelineDto[]>('/api/v1/prompt-pipelines', options)
    return Array.isArray(list) ? list.map(normalizePromptPipeline) : []
  },
  createPromptPipelineDraft: async (body: Partial<PromptPipelineDto>): Promise<PromptPipelineDto> => {
    const display_name = body.display_name ?? body.name ?? body.title ?? undefined
    const nodes = body.graph?.nodes ?? body.nodes
    const edges = body.graph?.edges ?? body.edges
    const canvas_layout = body.graph?.canvas_layout ?? body.canvas_layout
    const graph = body.graph ?? { nodes: nodes ?? [], edges: edges ?? [], ...(canvas_layout !== undefined ? { canvas_layout } : {}) }
    const created = await request<PromptPipelineDto>('/api/v1/prompt-pipelines', {
      method: 'POST',
      body: JSON.stringify({
        ...(body.slug !== undefined ? { slug: body.slug } : {}),
        ...(display_name !== undefined ? { display_name } : {}),
        ...(body.description !== undefined ? { description: body.description } : {}),
        graph,
      }),
    })
    return normalizePromptPipeline(created)
  },
  getPromptPipeline: async (id: string): Promise<PromptPipelineDto> =>
    normalizePromptPipeline(await request<PromptPipelineDto>(`/api/v1/prompt-pipelines/${encodeURIComponent(id)}`)),
  updatePromptPipelineDraft: async (id: string, payload: Partial<PromptPipelineDto>, expectedRevision?: number | string | null): Promise<PromptPipelineDto> => {
    const display_name = payload.display_name ?? payload.name ?? payload.title ?? undefined
    const nodes = payload.graph?.nodes ?? payload.nodes
    const edges = payload.graph?.edges ?? payload.edges
    const canvas_layout = payload.graph?.canvas_layout ?? payload.canvas_layout
    const graph = payload.graph ?? (nodes !== undefined || edges !== undefined
      ? { nodes: nodes ?? [], edges: edges ?? [], ...(canvas_layout !== undefined ? { canvas_layout } : {}) }
      : undefined)
    const updated = await request<PromptPipelineDto>(`/api/v1/prompt-pipelines/${encodeURIComponent(id)}/draft`, {
      method: 'PUT',
      headers: expectedRevision != null && expectedRevision !== '' ? { 'if-match': String(expectedRevision) } : {},
      body: JSON.stringify({
        ...(display_name !== undefined ? { display_name } : {}),
        ...(payload.description !== undefined ? { description: payload.description } : {}),
        ...(graph !== undefined ? { graph } : {}),
      }),
    })
    return normalizePromptPipeline(updated)
  },
  publishPromptPipeline: async (id: string, expectedRevision?: number | string | null): Promise<PromptPipelineDto> =>
    normalizePromptPipeline(await request<PromptPipelineDto>(`/api/v1/prompt-pipelines/${encodeURIComponent(id)}/publish`, {
      method: 'POST',
      headers: expectedRevision != null && expectedRevision !== '' ? { 'if-match': String(expectedRevision) } : {},
    })),
  archivePromptPipeline: async (id: string, expectedRevision?: number | string | null): Promise<PromptPipelineDto> =>
    normalizePromptPipeline(await request<PromptPipelineDto>(`/api/v1/prompt-pipelines/${encodeURIComponent(id)}/archive`, {
      method: 'POST',
      headers: expectedRevision != null && expectedRevision !== '' ? { 'if-match': String(expectedRevision) } : {},
    })),
  clonePromptPipeline: async (id: string): Promise<PromptPipelineDto> => {
    const source = await api.getPromptPipeline(id)
    const baseName = source.display_name ?? source.name ?? source.slug ?? source.id
    return api.createPromptPipelineDraft({
      display_name: `${baseName} (copy)`,
      description: source.description ?? undefined,
      graph: source.graph ?? { nodes: source.nodes ?? [], edges: source.edges ?? [] },
    })
  },
  listPromptPipelineVersions: (id: string) => request<PromptPipelineVersionDto[]>(`/api/v1/prompt-pipelines/${encodeURIComponent(id)}/versions`),
  // candidates
  listCandidates: () => request<AgentCandidateDto[]>('/api/v1/agent-candidates'),
  promoteCandidate: (candidateId: string, body: { target_mode_draft_id: string }) => request<AgentDefinitionDto>(`/api/v1/agent-candidates/${encodeURIComponent(candidateId)}/promote`, { method: 'POST', body: JSON.stringify(body) }),
  rejectCandidate: (candidateId: string, reason?: string) => request<{ status: string }>(`/api/v1/agent-candidates/${encodeURIComponent(candidateId)}/reject`, { method: 'POST', body: JSON.stringify({ reason }) }),
  // runtime instances
  listRuntimeInstances: (runId?: string) => {
    const qs = runId ? `?run_id=${encodeURIComponent(runId)}` : '';
    return request<AgentRuntimeInstanceDto[]>(`/api/v1/agent-runtime-instances${qs}`);
  },
  /** Effective policy ceilings of this scope (read-only; edited through the runtime document). */
  getAgentRuntimePolicy: () => request<AgentRuntimePolicyDto>('/api/v1/agent-runtime-policy'),
  // interactions (queued/insert/parallel)
  createInteraction: (sessionId: string, body: { content: string; client_message_id: string; mode_version_id?: string | null; permission_mode?: string | null; dispatch_mode: DispatchMode; target_run_id?: string | null; meeting_model_override?: MeetingModelOverrideDto | null; clear_meeting_model_override?: boolean; expected_settings_revision?: number; space_options?: SpaceOptionsDto | null; attachment_ids?: string[]; interrupt?: boolean; interface_locale?: string }) => request<SessionInteractionDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/interactions`, { method: 'POST', body: JSON.stringify(body) }),
  getCorrection: (runId: string) => request<Record<string, unknown>>(`/api/v1/runs/${encodeURIComponent(runId)}/correction`),
  answerCorrectionQuestion: (runId: string, questionId: string, body: { answer: string; option_id?: string | null; client_request_id: string }) => request<Record<string, unknown>>(`/api/v1/runs/${encodeURIComponent(runId)}/correction/questions/${encodeURIComponent(questionId)}/answer`, { method: 'POST', body: JSON.stringify(body) }),
  submitCorrectionFeedback: (runId: string, body: { kind: 'reproduced' | 'resolved'; note?: string | null; client_request_id: string; interface_locale?: string }) => request<Record<string, unknown>>(`/api/v1/runs/${encodeURIComponent(runId)}/correction/feedback`, { method: 'POST', body: JSON.stringify(body) }),
  reassignInteraction: (sessionId: string, interactionId: string, body: { target_run_id: string }) => request<InteractionActionDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/interactions/${encodeURIComponent(interactionId)}/reassign`, { method: 'POST', body: JSON.stringify(body) }),
  cancelInteraction: (sessionId: string, interactionId: string) => request<InteractionActionDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/interactions/${encodeURIComponent(interactionId)}/cancel`, { method: 'POST' }),
  // --- Attachments ---
  // Bytes never travel as JSON here: Core stores the file and returns a row of references,
  // so an upload is a raw body with its name and type in the query string. The ceiling is
  // enforced in Core, and a refusal arrives as a coded error ({ code: 'attachment_too_large' })
  // that requestResult() already turns into a branchable `err.code`.
  uploadAttachment: (
    sessionId: string,
    bytes: Blob | ArrayBuffer | Uint8Array,
    fileName: string,
    mediaType?: string,
    metadata: { origin?: 'composer' | 'workspace'; projectId?: string | null; sourcePath?: string } = {},
  ) => {
    const search = new URLSearchParams({ filename: fileName });
    if (mediaType) search.set('media_type', mediaType);
    if (metadata.origin) search.set('origin', metadata.origin);
    if (metadata.projectId) search.set('project_id', metadata.projectId);
    if (metadata.sourcePath) search.set('source_path', metadata.sourcePath);
    return request<MessageAttachmentDto>(
      `/api/v1/sessions/${encodeURIComponent(sessionId)}/attachments?${search.toString()}`,
      { method: 'POST', body: bytes as BodyInit, headers: { 'content-type': 'application/octet-stream' } },
    );
  },
  listAttachments: (sessionId: string) =>
    request<MessageAttachmentDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/attachments`),
  listProjectExposures: (projectId: string) =>
    request<MessageAttachmentDto[]>(`/api/v1/projects/${encodeURIComponent(projectId)}/exposures`),
  getAttachment: (attachmentId: string) =>
    request<MessageAttachmentDto>(`/api/v1/attachments/${encodeURIComponent(attachmentId)}`),
  deleteAttachment: (attachmentId: string) =>
    request<void>(`/api/v1/attachments/${encodeURIComponent(attachmentId)}`, { method: 'DELETE' }),
  /**
   * Direct URL for <img> and download links. It is deliberately a Gateway path, not a
   * fetch-then-objectURL: Core decides inline vs attachment per media type, and a browser
   * navigating this URL inherits that decision. Never build a filesystem path here.
   */
  attachmentContentUrl: (attachmentId: string) =>
    `${gatewayUrl}/api/v1/attachments/${encodeURIComponent(attachmentId)}/content`,

  listTools: async (options?: { signal?: AbortSignal }) =>
    (await request<(ToolDescriptorDto | ToolManifestEntryDto)[]>('/api/v1/tools', { signal: options?.signal })).map(normalizeToolDescriptor),
  searchTools: async (params: { query?: string; domain?: string; source?: string; risk?: string; limit?: number } = {}, options?: { signal?: AbortSignal }) => {
    const search = new URLSearchParams();
    if (params.query) search.set('query', params.query);
    if (params.domain) search.set('domain', params.domain);
    if (params.source) search.set('source', params.source);
    if (params.risk) search.set('risk', params.risk);
    if (params.limit !== undefined) search.set('limit', String(params.limit));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return normalizeToolSearchResults(await request<(ToolSearchResultDto | ToolManifestEntryDto)[]>(`/api/v1/tools/search${suffix}`, { signal: options?.signal }));
  },
  getHarnessManifest: (options?: { signal?: AbortSignal }) => request<HarnessManifestDto>('/api/v1/harness/manifest', options),
  listPromptFragments: (params: { scope?: string; target_agent_id?: string; category?: string; enabled?: boolean } = {}, options?: { signal?: AbortSignal }) => {
    const search = new URLSearchParams();
    if (params.scope) search.set('scope', params.scope);
    if (params.target_agent_id) search.set('target_agent_id', params.target_agent_id);
    if (params.category) search.set('category', params.category);
    if (params.enabled !== undefined) search.set('enabled', String(params.enabled));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<PromptFragmentDto[]>(`/api/v1/prompt-fragments${suffix}`, { signal: options?.signal });
  },
  createPromptFragment: (fragment: SavePromptFragmentInput) => request<PromptFragmentDto>('/api/v1/prompt-fragments', {
    method: 'POST',
    body: JSON.stringify(fragment)
  }),
  savePromptFragment: (fragmentId: string, fragment: SavePromptFragmentInput, expectedRevision?: number | string | null) => request<PromptFragmentDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}`, {
    method: 'PUT',
    headers: expectedRevision != null && expectedRevision !== '' ? { 'if-match': String(expectedRevision) } : {},
    body: JSON.stringify(fragment)
  }),
  deletePromptFragment: (fragmentId: string) => request<void>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}`, {
    method: 'DELETE'
  }),
  clonePromptFragment: (fragmentId: string) => request<PromptFragmentDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/clone`, {
    method: 'POST'
  }),
  clonePrompt: (id: string) => request<PromptFragmentDto>(`/api/v1/prompt-fragments/${encodeURIComponent(id)}/clone`, {
    method: 'POST'
  }),
  previewPromptContext: async (input: PromptContextPreviewInput): Promise<PromptContextPreviewNormalizedDto> => {
    const raw = await request<PromptContextPreviewDto>('/api/v1/prompt-context/preview', {
      method: 'POST',
      body: JSON.stringify(input)
    })
    return {
      ...raw,
      assembled_prompt: raw.assembled_prompt ?? raw.system_prompt ?? '',
      total_estimated_tokens: raw.total_estimated_tokens ?? raw.estimated_tokens ?? 0,
      layers: Array.isArray(raw.layers) ? raw.layers : [],
    }
  },
  /** Current v1 user tool transport. Gateway forwards this request to the Tool Provider. */
  executeCodeTool: <TData = Record<string, unknown>>(
    toolId: string,
    payload: CodeToolExecuteRequestDto = {},
    options?: { signal?: AbortSignal },
  ) =>
    request<CodeToolExecuteResultDto<TData>>(`/api/v1/code/tools/${encodeURIComponent(toolId)}/execute`, {
      signal: options?.signal,
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  /** Explicit provider transport alias for clients that use the Tool Runtime surface. */
  executeToolRuntime: (toolId: string, payload: CodeToolExecuteRequestDto = {}) => request<CodeToolExecuteResultDto>(`/api/v1/tool-runtime/tools/${encodeURIComponent(toolId)}/execute`, {
    method: 'POST',
    body: JSON.stringify(payload)
  }),

  // Semantic wrappers for code tools. The ids and argument keys below must match the
  // TinadecTools manifest, not an aspiration: Core answers 404 tool_not_found for an
  // unknown id and the tool rejects unknown argument keys, so a wrong string here is a
  // silently broken feature rather than a type error. Verified against
  // TinadecTools/Tools/FileRW/FileReader.cs:59 (read_file → filepath),
  // FileSystemTools.cs:69 (ls → path) and Tools/Search/FileSearch.cs:118
  // (file_search → pattern/path/glob/type/case_sensitive/fixed_strings/context_lines/
  // max_results, FileSearch.cs:8-38).
  readFile: (cwd: string, filePath: string, options?: { start_row?: number; end_row?: number }) =>
    api.executeCodeTool('read_file', { cwd, arguments: { filepath: filePath, ...options } }),
  listDirectory: (cwd: string, dirPath: string) =>
    api.executeCodeTool('ls', { cwd, arguments: { path: dirPath } }),
  /** stat is the only tool that reports an entry's size and mtime; its envelope is
   * `{ success, error, entry }` with `entry.type` in directory|file|link. */
  statEntry: (cwd: string, filePath: string) =>
    api.executeCodeTool('stat', { cwd, arguments: { path: filePath } }),
  grepContent: (cwd: string, pattern: string, options?: { case_sensitive?: boolean; context_lines?: number; max_results?: number; glob?: string; fixed_strings?: boolean }, transport?: { signal?: AbortSignal }) =>
    api.executeCodeTool('file_search', { cwd, arguments: { pattern, ...options } }, transport),
  // There is deliberately no apply_patch / code_editor wrapper here: neither tool id
  // exists in the TinadecTools manifest, and the governed write path is
  // createUserToolActionForPath(cwd, 'write_file', { filepath, content, file_hash }).
  gitDiffCompare: (cwd: string, baseRef: string, headRef: string, paths?: string[]) =>
    api.executeCodeTool('git_worktree_manager', { cwd, arguments: { action: 'diff_compare', base_ref: baseRef, head_ref: headRef, paths } }),
  gitLog: (cwd: string, limit?: number, ref?: string) =>
    api.executeCodeTool('git_worktree_manager', { cwd, arguments: { action: 'log', limit, ref } }),
  gitLogList: (
    cwd: string,
    options?: { repository_path?: string; revs?: string[]; skip?: number; after_commit?: string; limit?: number },
    transport?: { signal?: AbortSignal },
  ) =>
    api.executeCodeTool<GitLogListResultDto>('git_log_list', { cwd, arguments: { ...(options ?? {}) } }, transport),
  gitLogDetail: (
    cwd: string,
    rev: string,
    options?: {
      repository_path?: string;
      skip?: number;
      after_commit?: string;
      limit?: number;
      include_patch?: boolean;
      max_files?: number;
      max_patch_bytes?: number;
    },
    transport?: { signal?: AbortSignal },
  ) =>
    api.executeCodeTool<GitLogDetailResultDto>('git_log_detail', { cwd, arguments: { rev, ...(options ?? {}) } }, transport),
  gitFileHistory: (
    cwd: string,
    filePath: string,
    options?: {
      repository_path?: string;
      follow?: boolean;
      include_patch?: boolean;
      skip?: number;
      after_commit?: string;
      limit?: number;
      max_patch_bytes?: number;
    },
    transport?: { signal?: AbortSignal },
  ) =>
    api.executeCodeTool<GitFileHistoryResultDto>('git_file_history', { cwd, arguments: { path: filePath, ...(options ?? {}) } }, transport),
  gitBlame: (
    cwd: string,
    filePath: string,
    options?: {
      repository_path?: string;
      rev?: string;
      start_line?: number;
      end_line?: number;
      max_output_bytes?: number;
    },
    transport?: { signal?: AbortSignal },
  ) =>
    api.executeCodeTool<GitBlameResultDto>('git_blame', { cwd, arguments: { path: filePath, ...(options ?? {}) } }, transport),
  gitFileAtRevision: (
    cwd: string,
    filePath: string,
    rev = 'HEAD',
    options?: { repository_path?: string; max_output_bytes?: number },
    transport?: { signal?: AbortSignal },
  ) =>
    api.executeCodeTool<GitFileAtRevisionResultDto>('git_file_at_revision', { cwd, arguments: { path: filePath, rev, ...(options ?? {}) } }, transport),
  gitStashList: (
    cwd: string,
    options?: { repository_path?: string; limit?: number },
    transport?: { signal?: AbortSignal },
  ) =>
    api.executeCodeTool<GitStashListResultDto>('git_stash_list', { cwd, arguments: { ...(options ?? {}) } }, transport),
  gitStashShow: (
    cwd: string,
    stashRef = 'stash@{0}',
    options?: { repository_path?: string; include_patch?: boolean; max_files?: number; max_patch_bytes?: number },
    transport?: { signal?: AbortSignal },
  ) =>
    api.executeCodeTool<GitStashShowResultDto>('git_stash_show', { cwd, arguments: { stash_ref: stashRef, ...(options ?? {}) } }, transport),
  listAgentCandidates: (options?: { signal?: AbortSignal }) => cachedRead(catalogKey('agent-candidates'), () => request<AgentCandidateDto[]>('/api/v1/agent-candidates'), options?.signal),

  // --- Memory review (candidates are never retrieval-visible; only promoted items are) ---
  listMemoryCandidates: (query: MemoryQueueQuery = {}) =>
    request<MemoryCandidateDto[]>(`/api/v1/memory-candidates${memoryQueryString(query, ['status', 'scope', 'kind', 'run_id', 'project_id', 'limit'])}`),
  promoteMemoryCandidate: (candidateId: string, reason?: string) =>
    request<MemoryCandidateDto>(`/api/v1/memory-candidates/${encodeURIComponent(candidateId)}/promote`, { method: 'POST', body: JSON.stringify({ reason: reason ?? null }) }),
  rejectMemoryCandidate: (candidateId: string, reason?: string) =>
    request<MemoryCandidateDto>(`/api/v1/memory-candidates/${encodeURIComponent(candidateId)}/reject`, { method: 'POST', body: JSON.stringify({ reason: reason ?? null }) }),
  listMemoryItems: (query: MemoryQueueQuery = {}) =>
    request<MemoryItemDto[]>(`/api/v1/memory-items${memoryQueryString(query, ['status', 'scope', 'kind', 'project_id', 'limit'])}`),
  // A revocation has nowhere to record a reason, so this sends none.
  revokeMemoryItem: (itemId: string) =>
    request<MemoryRevocationDto>(`/api/v1/memory-items/${encodeURIComponent(itemId)}/revoke`, { method: 'POST', body: JSON.stringify({}) }),

  // --- Agent Evolution ---
  listEvolutionProposals: () => request<AgentEvolutionProposalDto[]>('/api/v1/agent-evolution/proposals'),
  generateEvolutionProposals: (params: { session_id?: string; lookback_event_count?: number } = {}) => {
    const search = new URLSearchParams();
    if (params.session_id) search.set('session_id', params.session_id);
    if (params.lookback_event_count !== undefined) search.set('lookback_event_count', String(params.lookback_event_count));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<AgentEvolutionProposalDto[]>(`/api/v1/agent-evolution/generate${suffix}`, { method: 'POST' });
  },
  promoteAgentCandidate: (candidateId: string, input: PromoteAgentCandidateInput) => request<AgentEvolutionProposalDto>(`/api/v1/agent-evolution/proposals/${encodeURIComponent(candidateId)}/promote`, {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  rejectAgentCandidate: (candidateId: string, reason?: string) => request<{ status: string; candidate_id: string }>(`/api/v1/agent-evolution/proposals/${encodeURIComponent(candidateId)}/reject`, {
    method: 'POST',
    body: JSON.stringify({ reason })
  }),

  // --- Context Diagnostics & Context Self-Evolution (P1 / P2) ---
  getRunContextDiagnostics: (
    runId: string,
    params: { context_window_tokens?: number; selected_pack_id?: string | null } = {},
    signal?: AbortSignal,
  ) => {
    const search = new URLSearchParams();
    if (params.context_window_tokens !== undefined) search.set('context_window_tokens', String(params.context_window_tokens));
    if (params.selected_pack_id) search.set('selected_pack_id', params.selected_pack_id);
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<RunContextDiagnosticsDto>(`/api/v1/runs/${encodeURIComponent(runId)}/context-diagnostics${suffix}`, {
      signal,
      cache: 'no-store',
    });
  },
  auditContextOverlap: (body: ContextOverlapAuditRequestDto, signal?: AbortSignal) =>
    request<ContextOverlapAuditDto>('/api/v1/context/audit-overlap', {
      method: 'POST',
      body: JSON.stringify(body),
      signal,
    }),
  listContextEvolutionProposals: async (
    query: ContextEvolutionProposalQuery = {},
    signal?: AbortSignal,
  ): Promise<ContextEvolutionProposalListDto> => {
    const search = new URLSearchParams();
    if (query.status) search.set('status', query.status);
    if (query.kind) search.set('kind', query.kind);
    if (query.target_scope) search.set('target_scope', query.target_scope);
    if (query.project_id) search.set('project_id', query.project_id);
    if (query.mode_id) search.set('mode_id', query.mode_id);
    if (query.limit !== undefined) search.set('limit', String(query.limit));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    const raw = await request<ContextEvolutionProposalListDto | ContextEvolutionProposalDto[]>(
      `/api/v1/context-evolution/proposals${suffix}`,
      { signal, cache: 'no-store' },
    );
    if (Array.isArray(raw)) {
      return {
        items: raw,
        total_count: raw.length,
        pending_count: raw.filter((item) => item.status === 'pending').length,
        auto_tune_low_risk_context_policy: false,
      };
    }
    const items = Array.isArray(raw?.items) ? raw.items : [];
    return {
      items,
      total_count: typeof raw?.total_count === 'number' ? raw.total_count : items.length,
      pending_count:
        typeof raw?.pending_count === 'number'
          ? raw.pending_count
          : items.filter((item) => item.status === 'pending').length,
      auto_tune_low_risk_context_policy: Boolean(raw?.auto_tune_low_risk_context_policy),
    };
  },
  getContextEvolutionProposal: (proposalId: string, signal?: AbortSignal) =>
    request<ContextEvolutionProposalDto>(
      `/api/v1/context-evolution/proposals/${encodeURIComponent(proposalId)}`,
      { signal, cache: 'no-store' },
    ),
  getContextEvolutionProposalEvaluation: (proposalId: string, signal?: AbortSignal) =>
    request<ContextEvolutionEvaluationDto>(
      `/api/v1/context-evolution/proposals/${encodeURIComponent(proposalId)}/evaluation`,
      { signal, cache: 'no-store' },
    ),
  applyContextEvolutionProposal: (proposalId: string, input: ApplyContextEvolutionProposalInput = {}) =>
    request<ContextEvolutionApplyResultDto>(
      `/api/v1/context-evolution/proposals/${encodeURIComponent(proposalId)}/apply`,
      {
        method: 'POST',
        body: JSON.stringify(input),
      },
    ),
  rejectContextEvolutionProposal: (proposalId: string, reason?: string | null) =>
    request<ContextEvolutionProposalDto>(
      `/api/v1/context-evolution/proposals/${encodeURIComponent(proposalId)}/reject`,
      {
        method: 'POST',
        body: JSON.stringify({ reason: reason ?? null }),
      },
    ),

  // --- Prompt Engineering: Versioning + A/B Testing + Effectiveness ---
  listPromptFragmentVersions: (fragmentId: string) => request<PromptFragmentVersionDto[]>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/versions`),
  listPromptVersions: (id: string) => request<PromptFragmentVersionDto[]>(`/api/v1/prompt-fragments/${encodeURIComponent(id)}/versions`),
  createPromptFragmentVersion: (fragmentId: string, input: { content: string; changed_fields: string[]; change_summary: string }) => request<PromptFragmentVersionDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/versions`, {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  rollbackPromptFragment: (fragmentId: string, targetVersion: number) => request<PromptFragmentDto>(`/api/v1/prompt-fragments/${encodeURIComponent(fragmentId)}/rollback`, {
    method: 'POST',
    body: JSON.stringify({ target_version: targetVersion })
  }),
  rollbackPrompt: (id: string, versionIdOrNumber: string | number) => request<PromptFragmentDto>(`/api/v1/prompt-fragments/${encodeURIComponent(id)}/rollback`, {
    method: 'POST',
    body: JSON.stringify(typeof versionIdOrNumber === 'number'
      ? { target_version: versionIdOrNumber }
      : { version_id: versionIdOrNumber })
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

  // --- Streaming Invoke (SSE) — compat adapter over the durable protocol (plan §4.3-4):
  // POST /sessions/{id}/interactions for the admission receipt, then the one durable
  // reader in src/lib/runStream.ts follows /runs/{runId}/stream. Call sites keep their
  // old signature; the legacy POST /sessions/{id}/invoke-stream wire is retired, and so
  // is the second SSE parser that used to live in this file.

  invokeStreamWithAdmission: (
    sessionId: string,
    body: InvokeStreamBody,
    onChunk: (chunk: ModelStreamChunkDto) => void,
    onError?: (error: Error) => void,
  ): AbortController => streamAdmittedInteraction(sessionId, body, onChunk, onError),

  // compat: the old four-argument signature delegates to the admission variant
  invokeStream: (
    sessionId: string,
    content: string,
    onChunk: (chunk: ModelStreamChunkDto) => void,
    onError?: (error: Error) => void,
  ): AbortController => streamAdmittedInteraction(
    sessionId,
    { content, client_message_id: newClientMessageId(), mode_version_id: null, permission_mode: 'default' },
    onChunk,
    onError,
  ),

  /**
   * `onHeartbeat` receives Core's named `heartbeat` frame. Core writes one right after the
   * initial durable replay and then every 15s, so the first one marks "history is loaded".
   * It is not an EventEnvelope and is deliberately not in CORE_EVENT_TYPES.
   */
  connectEvents(sessionId: string | null, onEvent: (event: EventEnvelope) => void, onHeartbeat?: () => void): EventSource {
    const params = sessionId ? `?session_id=${encodeURIComponent(selectionIdentity(sessionId).id)}` : '';
    const source = new ScopedEventSource(`${gatewayUrl}/api/v1/events${params}`, sessionStorageId(sessionId));
    const handle = (message: MessageEvent) => {
      try {
        onEvent(normalizeEventEnvelope(JSON.parse(message.data), message.lastEventId));
      } catch {
        // Malformed SSE frames must not crash the renderer (rendererErrorFallback watches window errors).
      }
    };
    source.onmessage = handle;
    // Core writes NAMED SSE frames, and the browser drops any named frame with no
    // listener of the same name — so the subscription list has to be the real event
    // vocabulary, not a hand-maintained guess. See events/coreEventTypes.ts for why
    // the previous list left tools, approvals, and run failures permanently invisible.
    for (const eventType of CORE_EVENT_TYPES) {
      source.addEventListener(eventType, handle as EventListener);
    }
    if (onHeartbeat) source.addEventListener('heartbeat', (() => { try { onHeartbeat() } catch { /* see above */ } }) as EventListener);
    return source as unknown as EventSource;
  },

  /** Terminal sessions Core has admitted for a run (agent terminal panel source). */
  async listTerminalSessions(runId: string): Promise<TerminalSessionDto[]> {
    return request<TerminalSessionDto[]>(
      `/api/v1/terminals?run_id=${encodeURIComponent(runId)}`,
    );
  },

  /** Send user keystrokes into an agent-owned terminal session. */
  async sendTerminalStdin(terminalSessionId: string, data: string): Promise<{ terminal_session_id: string; accepted: boolean }> {
    return request<{ terminal_session_id: string; accepted: boolean }>(
      `/api/v1/terminals/${encodeURIComponent(terminalSessionId)}/stdin`,
      {
        method: 'POST',
        headers: { accept: 'application/json', 'content-type': 'application/json' },
        body: JSON.stringify({ data }),
      },
    );
  },

  /** Terminate an agent-owned terminal session (panel × run control). */
  async killTerminalSession(terminalSessionId: string): Promise<{ terminal_session_id: string; killed: boolean }> {
    return request<{ terminal_session_id: string; killed: boolean }>(
      `/api/v1/terminals/${encodeURIComponent(terminalSessionId)}/kill`,
      { method: 'POST', headers: { accept: 'application/json', 'content-type': 'application/json' } },
    );
  },

  /**
   * Pause / resume / cancel a run. Cancelling on the Core side also terminates
   * the live terminal sessions that run owns.
   */
  async controlRun(
    runId: string,
    action: 'pause' | 'resume' | 'cancel',
    clientControlId?: string,
  ): Promise<{ run_id: string; status: string; action: string; accepted: boolean; killed_terminal_sessions?: number }> {
    return request<{ run_id: string; status: string; action: string; accepted: boolean; killed_terminal_sessions?: number }>(
      `/api/v1/runs/${encodeURIComponent(runId)}/control`,
      {
        method: 'POST',
        headers: { accept: 'application/json', 'content-type': 'application/json' },
        body: JSON.stringify({ action, client_control_id: clientControlId }),
      },
    );
  },

  /** List lifecycle hooks for workspace or global scope. */
  async listLifecycleHooks(options?: {
    scope?: LifecycleHookScope;
    workspaceRoot?: string | null;
    signal?: AbortSignal;
  }): Promise<LifecycleHooksDocumentDto> {
    const query = new URLSearchParams();
    if (options?.scope) query.set('scope', options.scope);
    if (options?.workspaceRoot) query.set('workspace_root', options.workspaceRoot);
    const qs = query.toString();
    return request<LifecycleHooksDocumentDto>(
      `/api/v1/lifecycle-hooks${qs ? `?${qs}` : ''}`,
      options?.signal ? { signal: options.signal } : undefined,
    );
  },

  /** Save lifecycle hooks for workspace or global scope with optimistic concurrency. */
  async saveLifecycleHooks(
    payload: SaveLifecycleHooksInput,
    etag?: string | number | null,
  ): Promise<LifecycleHooksDocumentDto> {
    const rev = etag ?? payload.expected_revision;
    const headers: Record<string, string> = {
      accept: 'application/json',
      'content-type': 'application/json',
    };
    if (rev != null && rev !== '') {
      headers['if-match'] = String(rev);
    }
    return request<LifecycleHooksDocumentDto>('/api/v1/lifecycle-hooks', {
      method: 'PUT',
      headers,
      body: JSON.stringify(payload),
    });
  },

  /** Minimal real 1-token model connectivity probe. */
  probeModel(force = true): Promise<ModelProbeResultDto> {
    return request<ModelProbeResultDto>(`/api/v1/model-probe${force ? '?force=true' : ''}`, {
      method: 'POST',
    });
  },

  /** Fetch the persistent structured session plan (empty draft when not yet created). */
  getSessionPlan(sessionId: string, options?: { signal?: AbortSignal }): Promise<SessionPlanDto> {
    return request<SessionPlanDto>(
      `/api/v1/sessions/${encodeURIComponent(sessionId)}/plan`,
      options?.signal ? { signal: options.signal } : undefined,
    );
  },

  /** Replace the persistent structured session plan with optimistic concurrency. */
  saveSessionPlan(
    sessionId: string,
    payload: PutSessionPlanInput,
    etag?: string | number | null,
  ): Promise<SessionPlanDto> {
    const rev = etag ?? payload.expected_revision;
    const headers: Record<string, string> = {
      accept: 'application/json',
      'content-type': 'application/json',
    };
    if (rev != null && rev !== '') {
      headers['if-match'] = String(rev);
    }
    return request<SessionPlanDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/plan`, {
      method: 'PUT',
      headers,
      body: JSON.stringify(payload),
    });
  },

  /** Incrementally update steps or add steps on the session plan. */
  patchSessionPlan(
    sessionId: string,
    payload: PatchSessionPlanInput,
    etag?: string | number | null,
  ): Promise<SessionPlanDto> {
    const rev = etag ?? payload.expected_revision;
    const headers: Record<string, string> = {
      accept: 'application/json',
      'content-type': 'application/json',
    };
    if (rev != null && rev !== '') {
      headers['if-match'] = String(rev);
    }
    return request<SessionPlanDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/plan`, {
      method: 'PATCH',
      headers,
      body: JSON.stringify(payload),
    });
  },

  /** Approve a draft session plan (`draft` -> `approved`). */
  approveSessionPlan(
    sessionId: string,
    payload?: ApproveSessionPlanInput,
    etag?: string | number | null,
  ): Promise<SessionPlanDto> {
    const rev = etag ?? payload?.expected_revision;
    const headers: Record<string, string> = {
      accept: 'application/json',
      'content-type': 'application/json',
    };
    if (rev != null && rev !== '') {
      headers['if-match'] = String(rev);
    }
    return request<SessionPlanDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/plan/approve`, {
      method: 'POST',
      headers,
      body: JSON.stringify(payload ?? {}),
    });
  },

  /** Canonical configured MCP servers list (`GET /api/v1/mcp/servers/configured`). */
  listConfiguredMcpServers(projectId?: string, options?: { signal?: AbortSignal }): Promise<ToolMcpResource[]> {
    const query = new URLSearchParams();
    if (projectId) query.set('project_id', projectId);
    const qs = query.toString();
    return request<ToolMcpResource[]>(
      `/api/v1/mcp/servers/configured${qs ? `?${qs}` : ''}`,
      options?.signal ? { signal: options.signal } : undefined,
    );
  },

  /** Create an MCP server entry (`stdio` / `http` / `sse`). */
  createMcpServer(body: ToolMcpInput, projectId?: string): Promise<ToolMcpResource> {
    const query = new URLSearchParams();
    if (projectId) query.set('project_id', projectId);
    const qs = query.toString();
    return request<ToolMcpResource>(`/api/v1/mcp/servers${qs ? `?${qs}` : ''}`, {
      method: 'POST',
      body: JSON.stringify(body),
    });
  },

  /** Update an MCP server entry by GUID resource_id or server id. */
  updateMcpServer(
    serverId: string,
    body: ToolMcpInput,
    revision?: number | null,
    projectId?: string,
  ): Promise<ToolMcpResource> {
    const query = new URLSearchParams();
    if (projectId) query.set('project_id', projectId);
    const qs = query.toString();
    return request<ToolMcpResource>(
      `/api/v1/mcp/servers/${encodeURIComponent(serverId)}${qs ? `?${qs}` : ''}`,
      {
        method: 'PUT',
        ...(revision != null ? { headers: { 'if-match': `"${revision}"` } } : {}),
        body: JSON.stringify(body),
      },
    );
  },

  /** Import one or more MCP servers from `.mcp.json` / Claude / Codex / QwenPaw config JSON or TOML. */
  importMcpServersJson(body: ImportMcpJsonInput): Promise<ImportMcpJsonResultDto> {
    return request<ImportMcpJsonResultDto>('/api/v1/mcp/servers/import-json', {
      method: 'POST',
      body: JSON.stringify(body),
    });
  },

  /** Toggle an MCP server enabled state without requiring full PUT + If-Match. */
  setMcpServerEnabled(serverId: string, enabled: boolean, projectId?: string): Promise<ToolMcpResource> {
    return request<ToolMcpResource>(
      `/api/v1/mcp/servers/${encodeURIComponent(serverId)}/enabled`,
      {
        method: 'PATCH',
        body: JSON.stringify({ enabled, ...(projectId ? { project_id: projectId } : {}) }),
      },
    );
  },

  /** Run live handshake probe on a configured MCP server and return discovered tools. */
  testMcpServer(serverId: string, projectId?: string): Promise<McpServerTestResultDto> {
    const query = new URLSearchParams();
    if (projectId) query.set('project_id', projectId);
    const qs = query.toString();
    return request<McpServerTestResultDto>(
      `/api/v1/mcp/servers/${encodeURIComponent(serverId)}/test${qs ? `?${qs}` : ''}`,
      { method: 'POST' },
    );
  },

  /** Delete an MCP server configuration by resource_id or server id. */
  deleteMcpServer(serverId: string, projectId?: string): Promise<void> {
    const query = new URLSearchParams();
    if (projectId) query.set('project_id', projectId);
    const qs = query.toString();
    return request<void>(
      `/api/v1/mcp/servers/${encodeURIComponent(serverId)}${qs ? `?${qs}` : ''}`,
      { method: 'DELETE' },
    );
  },

  /** Discover local external CLI ecosystems (`~/.claude`, `~/.codex`, `~/.zcode`, `~/.minimax`, `~/.pi`) with redacted secrets. */
  discoverExternalCliTools(options?: {
    projectId?: string;
    ecosystems?: string[];
    signal?: AbortSignal;
  }): Promise<ExternalCliDiscoveryDto> {
    const query = new URLSearchParams();
    if (options?.projectId) query.set('project_id', options.projectId);
    if (options?.ecosystems?.length) query.set('ecosystems', options.ecosystems.join(','));
    const qs = query.toString();
    return request<ExternalCliDiscoveryDto>(
      `/api/v1/tools/external-cli/discover${qs ? `?${qs}` : ''}`,
      options?.signal ? { signal: options.signal } : undefined,
    );
  },

  /** Install a skill from inline, local directory, git URL, or external CLI import. */
  installToolSkill(body: InstallToolSkillInput): Promise<InstallToolSkillResultDto> {
    return request<InstallToolSkillResultDto>('/api/v1/tools/skills/install', {
      method: 'POST',
      body: JSON.stringify(body),
    });
  },

  /** Toggle a skill's enabled state without requiring full PUT. */
  setToolSkillEnabled(resourceId: string, enabled: boolean, projectId?: string): Promise<ToolSkillResource> {
    return request<ToolSkillResource>(
      `/api/v1/tools/skills/${encodeURIComponent(resourceId)}/enabled`,
      {
        method: 'PATCH',
        body: JSON.stringify({ enabled, ...(projectId ? { project_id: projectId } : {}) }),
      },
    );
  },

  /** Reload and re-index skills for global or project scope. */
  reloadToolSkills(projectId?: string): Promise<{ skills: ToolSkillResource[]; diagnostics: string[]; source: string }> {
    const query = new URLSearchParams();
    if (projectId) query.set('project_id', projectId);
    const qs = query.toString();
    return request<{ skills: ToolSkillResource[]; diagnostics: string[]; source: string }>(
      `/api/v1/tools/skills/reload${qs ? `?${qs}` : ''}`,
      { method: 'POST' },
    );
  },

  /** Browse host directories and files via the authenticated Gateway/Core filesystem endpoint. */
  browseFilesystem(options?: {
    path?: string;
    includeFiles?: boolean;
    showHidden?: boolean;
    limit?: number;
    signal?: AbortSignal;
  }): Promise<FilesystemBrowseResponseDto> {
    const query = new URLSearchParams();
    if (options?.path !== undefined && options.path !== '') query.set('path', options.path);
    if (options?.includeFiles !== undefined) query.set('include_files', options.includeFiles ? 'true' : 'false');
    if (options?.showHidden !== undefined) query.set('show_hidden', options.showHidden ? 'true' : 'false');
    if (options?.limit !== undefined) query.set('limit', String(options.limit));
    const qs = query.toString();
    return request<FilesystemBrowseResponseDto>(
      `/api/v1/fs/browse${qs ? `?${qs}` : ''}`,
      {
        storageId: 'user',
        credentials: 'include',
        ...(options?.signal ? { signal: options.signal } : {}),
      },
    );
  },

  /** Create a new folder on the host filesystem for the built-in Reka picker. */
  createHostDirectory(input: CreateHostDirectoryRequestDto): Promise<CreateHostDirectoryResponseDto> {
    return request<CreateHostDirectoryResponseDto>('/api/v1/fs/mkdir', {
      method: 'POST',
      storageId: 'user',
      credentials: 'include',
      body: JSON.stringify(input),
    });
  },

  /** Rollup project overview: active runs, open governance reports, resource leases, and session count. */
  async getProjectOverview(
    projectId: string,
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<ProjectOverviewDto> {
    const raw = await request<Partial<ProjectOverviewDto> & Record<string, unknown>>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/overview`,
      { ...options, cache: 'no-store' },
    );
    const sessions = typeof raw?.sessions === 'number'
      ? raw.sessions
      : typeof raw?.session_count === 'number'
        ? raw.session_count
        : 0;
    const heldResources = Array.isArray(raw?.held_resources)
      ? (raw.held_resources as ProjectOverviewLeaseDto[])
      : Array.isArray(raw?.leases)
        ? (raw.leases as ProjectOverviewLeaseDto[])
        : [];
    return {
      project_id: typeof raw?.project_id === 'string' ? raw.project_id : selectionIdentity(projectId).id,
      name: typeof raw?.name === 'string' ? raw.name : '',
      sessions,
      session_count: sessions,
      active_runs: Array.isArray(raw?.active_runs) ? (raw.active_runs as ProjectOverviewRunDto[]) : [],
      open_reports: Array.isArray(raw?.open_reports) ? (raw.open_reports as ProjectOverviewReportDto[]) : [],
      held_resources: heldResources,
      leases: heldResources,
      reports_truncated: Boolean(raw?.reports_truncated),
    };
  },

  // --- Project Hub & Composable Project Board (Phase 1 #54) ---
  getProjectBoardSettings(
    projectId: string,
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<ProjectBoardSettingsDto> {
    return request<ProjectBoardSettingsDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/settings`,
      { ...options, cache: 'no-store' },
    );
  },

  updateProjectBoardSettings(
    projectId: string,
    input: UpdateProjectBoardSettingsInput,
    options?: { storageId?: string },
  ): Promise<ProjectBoardSettingsDto> {
    return request<ProjectBoardSettingsDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/settings`,
      {
        ...options,
        method: 'PUT',
        body: JSON.stringify(input),
      },
    );
  },

  syncProjectBoard(
    projectId: string,
    options?: { storageId?: string },
  ): Promise<ProjectBoardSyncResultDto> {
    return request<ProjectBoardSyncResultDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/sync`,
      {
        ...options,
        method: 'POST',
        body: JSON.stringify({}),
      },
    );
  },

  listProjectBoardTasks(
    projectId: string,
    params: ListBoardTasksQuery = {},
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<ProjectBoardTaskDto[]> {
    const qs = serializeBoardTaskQuery(params);
    return request<ProjectBoardTaskDto[]>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks${qs}`,
      { ...options, cache: 'no-store' },
    ).then((rows) => (Array.isArray(rows) ? rows.map(normalizeBoardTaskDto) : []));
  },

  createProjectBoardTask(
    projectId: string,
    input: CreateProjectBoardTaskInput,
    options?: { storageId?: string },
  ): Promise<ProjectBoardTaskDto> {
    return request<ProjectBoardTaskDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks`,
      {
        ...options,
        method: 'POST',
        body: JSON.stringify(input),
      },
    ).then(normalizeBoardTaskDto);
  },

  promoteSpatialToBoard(
    projectId: string,
    input: PromoteFromSpatialInput,
    options?: { storageId?: string },
  ): Promise<PromoteFromSpatialResultDto> {
    return request<PromoteFromSpatialResultDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks/promote-from-spatial`,
      {
        ...options,
        method: 'POST',
        body: JSON.stringify(input),
      },
    ).then(normalizePromoteFromSpatialResultDto);
  },

  getProjectBoardTask(
    projectId: string,
    taskId: string,
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<ProjectBoardTaskDto> {
    return request<ProjectBoardTaskDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks/${encodeURIComponent(taskId)}`,
      { ...options, cache: 'no-store' },
    ).then(normalizeBoardTaskDto);
  },

  updateProjectBoardTask(
    projectId: string,
    taskId: string,
    input: UpdateProjectBoardTaskInput,
    options?: { storageId?: string },
  ): Promise<ProjectBoardTaskDto> {
    return request<ProjectBoardTaskDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks/${encodeURIComponent(taskId)}`,
      {
        ...options,
        method: 'PATCH',
        body: JSON.stringify(input),
      },
    ).then(normalizeBoardTaskDto);
  },

  deleteProjectBoardTask(
    projectId: string,
    taskId: string,
    options?: { storageId?: string },
  ): Promise<void> {
    return request<void>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks/${encodeURIComponent(taskId)}`,
      { ...options, method: 'DELETE' },
    );
  },

  resolveProjectBoardConflict(
    projectId: string,
    taskId: string,
    input: ResolveProjectBoardConflictInput,
    options?: { storageId?: string },
  ): Promise<ProjectBoardTaskDto> {
    return request<ProjectBoardTaskDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks/${encodeURIComponent(taskId)}/resolve-conflict`,
      {
        ...options,
        method: 'POST',
        body: JSON.stringify(input),
      },
    ).then(normalizeBoardTaskDto);
  },

  dispatchProjectBoardTask(
    projectId: string,
    taskId: string,
    input: DispatchProjectBoardTaskInput = {},
    options?: { storageId?: string },
  ): Promise<ProjectBoardTaskDto> {
    return request<ProjectBoardTaskDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks/${encodeURIComponent(taskId)}/dispatch`,
      {
        ...options,
        method: 'POST',
        body: JSON.stringify(input),
      },
    ).then(normalizeBoardTaskDto);
  },

  getProjectBoardTaskDiff(
    projectId: string,
    taskId: string,
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<ProjectBoardTaskDiffDto> {
    return request<ProjectBoardTaskDiffDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks/${encodeURIComponent(taskId)}/diff`,
      { ...options, cache: 'no-store' },
    ).then((raw) => ({
      worktree_path: raw?.worktree_path ?? null,
      diff_text: typeof raw?.diff_text === 'string' ? raw.diff_text : '',
      files: Array.isArray(raw?.files) ? raw.files : [],
    }));
  },

  approveProjectBoardTask(
    projectId: string,
    taskId: string,
    input: ApproveProjectBoardTaskInput = {},
    options?: { storageId?: string },
  ): Promise<ProjectBoardTaskDto> {
    return request<ProjectBoardTaskDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks/${encodeURIComponent(taskId)}/approve`,
      {
        ...options,
        method: 'POST',
        body: JSON.stringify(input),
      },
    ).then(normalizeBoardTaskDto);
  },

  requestProjectBoardTaskChanges(
    projectId: string,
    taskId: string,
    input: RequestTaskChangesInput,
    options?: { storageId?: string },
  ): Promise<ProjectBoardTaskDto> {
    return request<ProjectBoardTaskDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks/${encodeURIComponent(taskId)}/request-changes`,
      {
        ...options,
        method: 'POST',
        body: JSON.stringify(input),
      },
    ).then(normalizeBoardTaskDto);
  },

  cancelProjectBoardTask(
    projectId: string,
    taskId: string,
    options?: { storageId?: string },
  ): Promise<ProjectBoardTaskDto> {
    return request<ProjectBoardTaskDto>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/tasks/${encodeURIComponent(taskId)}/cancel`,
      {
        ...options,
        method: 'POST',
        body: JSON.stringify({}),
      },
    ).then(normalizeBoardTaskDto);
  },

  listGlobalBoardTasks(
    params: ListBoardTasksQuery = {},
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<ProjectBoardTaskDto[]> {
    const qs = serializeBoardTaskQuery(params);
    return request<ProjectBoardTaskDto[]>(`/api/v1/board/tasks${qs}`, {
      storageId: options?.storageId ?? 'user',
      ...(options?.signal ? { signal: options.signal } : {}),
      cache: 'no-store',
    }).then((rows) => (Array.isArray(rows) ? rows.map(normalizeBoardTaskDto) : []));
  },

  getGlobalBoardSummary(
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<ProjectBoardSummaryDto> {
    return request<Partial<ProjectBoardSummaryDto> & Record<string, unknown>>('/api/v1/board/summary', {
      storageId: options?.storageId ?? 'user',
      ...(options?.signal ? { signal: options.signal } : {}),
      cache: 'no-store',
    }).then(normalizeBoardSummaryDto);
  },

  listBoardViews(
    params: { scope?: ProjectBoardViewScope; project_id?: string | null } = {},
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<ProjectBoardViewDto[]> {
    const query = new URLSearchParams();
    if (params.scope) query.set('scope', params.scope);
    if (params.project_id) query.set('project_id', selectionIdentity(params.project_id).id);
    const qs = query.toString();
    const resolvedStorage = options?.storageId
      ?? (params.project_id ? projectStorageId(params.project_id) : 'user');
    return request<ProjectBoardViewDto[]>(`/api/v1/board/views${qs ? `?${qs}` : ''}`, {
      storageId: resolvedStorage,
      ...(options?.signal ? { signal: options.signal } : {}),
      cache: 'no-store',
    }).then((rows) => (Array.isArray(rows) ? rows : []));
  },

  createBoardView(
    input: CreateProjectBoardViewInput,
    options?: { storageId?: string },
  ): Promise<ProjectBoardViewDto> {
    const resolvedStorage = options?.storageId
      ?? (input.project_id ? projectStorageId(input.project_id) : 'user');
    return request<ProjectBoardViewDto>('/api/v1/board/views', {
      storageId: resolvedStorage,
      method: 'POST',
      body: JSON.stringify(input),
    });
  },

  updateBoardView(
    viewId: string,
    input: UpdateProjectBoardViewInput,
    options?: { storageId?: string; projectId?: string | null },
  ): Promise<ProjectBoardViewDto> {
    const resolvedStorage = options?.storageId
      ?? (options?.projectId ? projectStorageId(options.projectId) : 'user');
    return request<ProjectBoardViewDto>(`/api/v1/board/views/${encodeURIComponent(viewId)}`, {
      storageId: resolvedStorage,
      method: 'PATCH',
      body: JSON.stringify(input),
    });
  },

  deleteBoardView(
    viewId: string,
    options?: { storageId?: string; projectId?: string | null },
  ): Promise<void> {
    const resolvedStorage = options?.storageId
      ?? (options?.projectId ? projectStorageId(options.projectId) : 'user');
    return request<void>(`/api/v1/board/views/${encodeURIComponent(viewId)}`, {
      storageId: resolvedStorage,
      method: 'DELETE',
    });
  },

  subscribeProjectBoardEvents(
    projectId: string,
    onEvent: (event: ProjectBoardSseEventDto) => void,
    options: BoardEventStreamOptions = {},
  ): BoardEventStreamHandle {
    const identity = selectionIdentity(projectId);
    const storageId = options.storageId ?? identity.storageId ?? projectStorageId(projectId);
    return openBoardEventStream(
      `/api/v1/projects/${encodeURIComponent(identity.id)}/board/events`,
      onEvent,
      { ...options, storageId },
    );
  },

  subscribeGlobalBoardEvents(
    onEvent: (event: ProjectBoardSseEventDto) => void,
    options: BoardEventStreamOptions = {},
  ): BoardEventStreamHandle {
    return openBoardEventStream('/api/v1/board/events', onEvent, {
      ...options,
      storageId: options.storageId ?? 'user',
    });
  },

  // --- Phase 3 (#56): Project Living Notes, Coordinator Planner & Autopilot ---
  getProjectNotes(
    projectId: string,
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<ProjectNotesDto> {
    return request<Partial<ProjectNotesDto>>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/notes`,
      { ...options, cache: 'no-store' },
    ).then((raw) => normalizeProjectNotesDto(raw, projectId));
  },

  updateProjectNotes(
    projectId: string,
    input: UpdateProjectNotesInput,
    options?: { storageId?: string },
  ): Promise<ProjectNotesDto> {
    return request<Partial<ProjectNotesDto>>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/notes`,
      {
        ...options,
        method: 'PUT',
        body: JSON.stringify(input),
      },
    ).then((raw) => normalizeProjectNotesDto(raw, projectId));
  },

  planCoordinatorTasks(
    projectId: string,
    input: CoordinatorPlanInput,
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<CoordinatorPlanResultDto> {
    return request<Partial<CoordinatorPlanResultDto>>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/coordinator/plan`,
      {
        ...options,
        method: 'POST',
        body: JSON.stringify(input),
      },
    ).then((raw) => normalizeCoordinatorPlanResultDto(raw, projectId));
  },

  applyCoordinatorPlan(
    projectId: string,
    input: CoordinatorApplyPlanInput,
    options?: { storageId?: string },
  ): Promise<CoordinatorApplyPlanResultDto> {
    return request<Partial<CoordinatorApplyPlanResultDto>>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/coordinator/apply-plan`,
      {
        ...options,
        method: 'POST',
        body: JSON.stringify(input),
      },
    ).then((raw) => normalizeCoordinatorApplyPlanResultDto(raw, projectId));
  },

  getProjectAutopilot(
    projectId: string,
    options?: { signal?: AbortSignal; storageId?: string },
  ): Promise<ProjectAutopilotStateDto> {
    return request<Partial<ProjectAutopilotStateDto>>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/coordinator/autopilot`,
      { ...options, cache: 'no-store' },
    ).then((raw) => normalizeProjectAutopilotStateDto(raw, projectId));
  },

  updateProjectAutopilot(
    projectId: string,
    input: UpdateProjectAutopilotInput,
    options?: { storageId?: string },
  ): Promise<ProjectAutopilotStateDto> {
    return request<Partial<ProjectAutopilotStateDto>>(
      `/api/v1/projects/${encodeURIComponent(projectId)}/board/coordinator/autopilot`,
      {
        ...options,
        method: 'PUT',
        body: JSON.stringify(input),
      },
    ).then((raw) => normalizeProjectAutopilotStateDto(raw, projectId));
  },
};

export type ModelProbeResultDto = ReadinessItemDto;

export type SessionPlanStatus = 'draft' | 'approved' | 'active' | 'completed' | 'blocked';
export type SessionPlanStepStatus = 'pending' | 'in_progress' | 'completed' | 'blocked' | 'failed' | 'skipped';
export type SessionPlanArtifactKind = 'file' | 'worktree' | 'summary' | 'attachment';

export interface SessionPlanArtifactDto {
  kind: SessionPlanArtifactKind;
  label: string;
  path?: string | null;
  ref_id?: string | null;
}

export interface SessionPlanStepDto {
  id: string;
  title: string;
  description?: string | null;
  status: SessionPlanStepStatus;
  depends_on: string[];
  bound_task_key?: string | null;
  assigned_agent_slug?: string | null;
  verification_criteria: string[];
  artifacts: SessionPlanArtifactDto[];
  summary?: string | null;
  updated_at?: string | null;
}

export interface SessionPlanPhaseDto {
  id: string;
  title: string;
  description?: string | null;
  order: number;
  status: SessionPlanStepStatus;
  steps: SessionPlanStepDto[];
}

export interface SessionPlanProgressDto {
  total_steps: number;
  completed_steps: number;
  in_progress_steps: number;
  blocked_steps: number;
  failed_steps: number;
  pending_steps: number;
}

export interface SessionPlanDto {
  id: string;
  session_id: string;
  title: string;
  goal?: string | null;
  status: SessionPlanStatus;
  revision: number;
  approved_at?: string | null;
  last_run_id?: string | null;
  updated_at: string;
  phases: SessionPlanPhaseDto[];
  progress: SessionPlanProgressDto;
}

export interface PutSessionPlanInput {
  title?: string | null;
  goal?: string | null;
  status?: SessionPlanStatus | null;
  phases: SessionPlanPhaseDto[];
  expected_revision?: number | null;
}

export interface SessionPlanStepUpdateInput {
  id: string;
  status?: SessionPlanStepStatus | null;
  title?: string | null;
  description?: string | null;
  summary?: string | null;
  assigned_agent_slug?: string | null;
  bound_task_key?: string | null;
  verification_criteria?: string[] | null;
  depends_on?: string[] | null;
  artifacts?: SessionPlanArtifactDto[] | null;
}

export interface SessionPlanAddStepInput {
  phase_id?: string | null;
  id?: string | null;
  title: string;
  status?: SessionPlanStepStatus | null;
  depends_on?: string[] | null;
  assigned_agent_slug?: string | null;
  bound_task_key?: string | null;
  verification_criteria?: string[] | null;
}

export interface PatchSessionPlanInput {
  title?: string | null;
  goal?: string | null;
  status?: SessionPlanStatus | null;
  phases?: SessionPlanPhaseDto[] | null;
  step_updates?: SessionPlanStepUpdateInput[] | null;
  add_steps?: SessionPlanAddStepInput[] | null;
  expected_revision?: number | null;
}

export interface ApproveSessionPlanInput {
  expected_revision?: number | null;
}

export const getSessionPlan = api.getSessionPlan;
export const saveSessionPlan = api.saveSessionPlan;
export const patchSessionPlan = api.patchSessionPlan;
export const approveSessionPlan = api.approveSessionPlan;
export const probeModel = api.probeModel;

export const listLifecycleHooks = api.listLifecycleHooks;
export const saveLifecycleHooks = api.saveLifecycleHooks;

export type LifecycleHookEvent = 'PreToolUse' | 'PostToolUse' | 'OnTaskComplete' | 'OnSessionIdle';
export type LifecycleHookScope = 'workspace' | 'global';

export interface LifecycleHookEntryDto {
  id: string;
  name?: string | null;
  event: LifecycleHookEvent;
  matcher?: string | null;
  command: string;
  timeout_ms: number;
  blocking: boolean;
  enabled: boolean;
}

export interface LifecycleHooksDocumentDto {
  scope: LifecycleHookScope;
  workspace_root?: string | null;
  hooks: LifecycleHookEntryDto[];
  revision: number;
}

export interface SaveLifecycleHooksInput {
  scope?: LifecycleHookScope;
  workspace_root?: string | null;
  hooks: LifecycleHookEntryDto[];
  expected_revision?: number | null;
}

/** One Core-owned terminal session projection. */
export interface TerminalSessionDto {
  terminal_session_id: string;
  run_id: string;
  task_id: string;
  agent_instance_id: string;
  execution_id: string;
  command: string;
  status: string;
  live: boolean;
  started_at: string;
  ended_at?: string | null;
  exit_code?: number | null;
  timed_out?: boolean;
}

export interface ImportMcpJsonInput {
  json?: string;
  config?: Record<string, unknown>;
  project_id?: string;
  overwrite?: boolean;
  default_enabled?: boolean;
  import_source?: string;
}

export interface ImportMcpJsonResultDto {
  imported: ToolMcpResource[];
  skipped: Array<{ id: string; reason: string }>;
  imported_count: number;
  skipped_count: number;
}

export interface McpDiscoveredToolDto {
  id: string;
  name: string;
  description?: string | null;
  input_schema?: unknown;
}

export interface McpServerTestResultDto {
  resource_id: string;
  id: string;
  name: string;
  enabled: boolean;
  status: string;
  reason?: string | null;
  tool_count: number;
  tools: McpDiscoveredToolDto[];
}

export interface ExternalCliEcosystemDto {
  id: string;
  label: string;
  detected: boolean;
  paths_scanned: string[];
}

export interface ExternalCliDiscoveredItemDto {
  item_id: string;
  ecosystem: string;
  kind: 'mcp-server' | 'skill' | 'command';
  name: string;
  display_name: string;
  description?: string | null;
  source_path: string;
  transport?: string | null;
  command?: string | null;
  args?: string[] | null;
  url?: string | null;
  env_keys?: string[] | null;
  header_keys?: string[] | null;
  has_secrets: boolean;
  default_enabled: boolean;
  already_imported: boolean;
  content_preview?: string | null;
}

export interface ExternalCliDiscoveryDto {
  user_home: string;
  workspace_root?: string | null;
  ecosystems: ExternalCliEcosystemDto[];
  items: ExternalCliDiscoveredItemDto[];
}

export type ToolSkillInstallSourceType = 'inline' | 'local_dir' | 'git_url' | 'external_cli_import';

export interface InstallToolSkillInput {
  source_type: ToolSkillInstallSourceType;
  scope?: 'shared' | 'project';
  project_id?: string;
  name?: string;
  content?: string;
  files?: Record<string, string>;
  local_path?: string;
  git_url?: string;
  subpath?: string;
  item_ids?: string[];
  ecosystems?: string[];
  enabled?: boolean;
  enable_stdio_mcp?: boolean;
  overwrite?: boolean;
  governed?: boolean;
}

export interface InstallToolSkillResultDto {
  source_type: string;
  installed_skills: ToolSkillResource[];
  installed_mcp_servers: ToolMcpResource[];
  installed_commands: string[];
  skipped: Array<{ id?: string; name?: string; reason: string }>;
}

export const listConfiguredMcpServers = api.listConfiguredMcpServers;
export const createMcpServer = api.createMcpServer;
export const updateMcpServer = api.updateMcpServer;
export const importMcpServersJson = api.importMcpServersJson;
export const setMcpServerEnabled = api.setMcpServerEnabled;
export const testMcpServer = api.testMcpServer;
export const deleteMcpServer = api.deleteMcpServer;
export const discoverExternalCliTools = api.discoverExternalCliTools;
export const installToolSkill = api.installToolSkill;
export const setToolSkillEnabled = api.setToolSkillEnabled;
export const reloadToolSkills = api.reloadToolSkills;
export const gitLogList = api.gitLogList;
export const gitLogDetail = api.gitLogDetail;
export const gitFileHistory = api.gitFileHistory;
export const gitBlame = api.gitBlame;
export const gitFileAtRevision = api.gitFileAtRevision;
export const gitStashList = api.gitStashList;
export const gitStashShow = api.gitStashShow;

export interface FilesystemBrowseRootDto {
  name: string;
  path: string;
  kind: 'home' | 'drive' | string;
}

export interface FilesystemBrowseEntryDto {
  name: string;
  path: string;
  kind: 'drive' | 'directory' | 'file' | string;
  is_dir: boolean;
  has_children?: boolean;
  is_git_repo?: boolean;
  is_hidden?: boolean;
  size_bytes?: number | null;
  modified_at?: string | null;
}

export interface FilesystemBrowseResponseDto {
  platform: string;
  separator: string;
  home_path: string;
  current_path: string;
  parent_path?: string | null;
  roots: FilesystemBrowseRootDto[];
  entries: FilesystemBrowseEntryDto[];
  truncated?: boolean;
}

export interface CreateHostDirectoryRequestDto {
  parent_path: string;
  name: string;
}

export interface CreateHostDirectoryResponseDto {
  created_path: string;
  entry: FilesystemBrowseEntryDto;
}

export const browseFilesystem = api.browseFilesystem;
export const createHostDirectory = api.createHostDirectory;

// ---------------------------------------------------------------------------
// Project Overview & Project Board Types + Helpers (Phase 1 #54)
// ---------------------------------------------------------------------------

export interface ProjectOverviewRunDto {
  run_id: string;
  session_id: string;
  status: string;
  phase?: string | null;
  started_at: string;
}

export interface ProjectOverviewReportDto {
  session_id: string;
  report_id: string;
  author_display_name: string;
  report_kind: string;
  severity: string;
  finding: string;
  created_at: string;
}

export interface ProjectOverviewLeaseDto {
  lease_id: string;
  kind: string;
  resource_key: string;
  purpose: string;
  run_id?: string | null;
  reason?: string | null;
}

export interface ProjectOverviewDto {
  project_id: string;
  name: string;
  sessions: number;
  session_count: number;
  active_runs: ProjectOverviewRunDto[];
  open_reports: ProjectOverviewReportDto[];
  held_resources: ProjectOverviewLeaseDto[];
  leases: ProjectOverviewLeaseDto[];
  reports_truncated: boolean;
}

export type ProjectBoardStorageMode = 'repo_tinadec' | 'app_data' | 'custom_dir';
export type ProjectBoardTaskStatus = 'backlog' | 'ready' | 'in_progress' | 'in_review' | 'done';
export type ProjectBoardTaskPriority = 'urgent' | 'high' | 'medium' | 'low' | 'none';
export type ProjectBoardAssigneeKind = 'human' | 'native' | 'cli' | 'coordinator';
export type ProjectBoardLayoutMode = 'kanban' | 'list' | 'swimlane';
export type ProjectBoardGroupBy = 'status' | 'project' | 'assignee' | 'priority' | 'none';
export type ProjectBoardSwimlaneBy = 'project' | 'assignee';
export type ProjectBoardSortBy = 'manual' | 'priority' | 'updated_at' | 'dependency';
export type ProjectBoardViewScope = 'global' | 'project';
export type ProjectBoardConflictResolution = 'keep_file' | 'keep_board';
export type ProjectBoardVisibleField =
  | 'project_badge'
  | 'live_digest'
  | 'diff_stats'
  | 'dependencies'
  | 'conflict_badge'
  | 'spatial_ref'
  | 'labels'
  | 'checklist'
  | (string & {});

export type ProjectBoardEventType =
  | 'snapshot'
  | 'task_upserted'
  | 'task_deleted'
  | 'settings_updated'
  | 'view_upserted'
  | 'view_deleted'
  | 'sync_completed'
  | 'notes_updated'
  | 'autopilot_updated'
  | (string & {});

export type CoordinatorPlannerSource = 'llm' | 'heuristic';

export interface ProjectNotesDto {
  project_id: string;
  content_md: string;
  resolved_file_path: string;
  storage_mode: ProjectBoardStorageMode;
  revision: number;
  last_synced_hash: string;
  updated_at_unix_ms: number;
}

export interface UpdateProjectNotesInput {
  content_md: string;
  expected_revision?: number | null;
}

export interface CoordinatorTaskDraftDto {
  draft_id: string;
  title: string;
  description_md: string;
  checklist: ProjectBoardChecklistItemDto[];
  priority: ProjectBoardTaskPriority;
  status: ProjectBoardTaskStatus;
  assignee: ProjectBoardAssigneeDto | null;
  labels: string[];
  depends_on_draft_ids: string[];
  depends_on_task_ids: string[];
}

export interface CoordinatorPlanInput {
  goal: string;
  default_status?: ProjectBoardTaskStatus | null;
  default_assignee?: ProjectBoardAssigneeDto | null;
  auto_apply?: boolean;
  max_tasks?: number | null;
}

export interface CoordinatorPlanResultDto {
  project_id: string;
  goal: string;
  summary: string;
  planner_source: CoordinatorPlannerSource;
  auto_applied: boolean;
  drafts: CoordinatorTaskDraftDto[];
  created_tasks: ProjectBoardTaskDto[];
}

export interface CoordinatorApplyPlanInput {
  goal?: string | null;
  update_notes?: boolean | null;
  drafts: CoordinatorTaskDraftDto[];
}

export interface CoordinatorApplyPlanResultDto {
  project_id: string;
  created_tasks: ProjectBoardTaskDto[];
}

export interface UpdateProjectAutopilotInput {
  autopilot_enabled?: boolean | null;
  max_concurrency?: number | null;
  cancel_active?: boolean | null;
}

export interface ProjectAutopilotStateDto {
  project_id: string;
  autopilot_enabled: boolean;
  max_concurrency: number;
  active_task_count: number;
  active_task_ids: string[];
  queued_ready_count: number;
  blocked_task_count: number;
  blocked_tasks: ProjectBoardTaskDto[];
  last_tick_at_unix_ms: number | null;
  last_error: string | null;
  updated_at_unix_ms: number;
}

export interface ProjectBoardSettingsDto {
  project_id: string;
  key_prefix: string;
  next_sequence: number;
  storage_mode: ProjectBoardStorageMode;
  custom_sync_root?: string | null;
  resolved_board_dir?: string | null;
  project_instructions?: string | null;
  default_assignee_kind: ProjectBoardAssigneeKind;
  max_concurrency: number;
  autopilot_enabled: boolean;
  updated_at_unix_ms: number;
}

export interface UpdateProjectBoardSettingsInput {
  key_prefix?: string | null;
  storage_mode?: ProjectBoardStorageMode | null;
  custom_sync_root?: string | null;
  project_instructions?: string | null;
  default_assignee_kind?: ProjectBoardAssigneeKind | null;
  max_concurrency?: number | null;
  autopilot_enabled?: boolean | null;
}

export interface ProjectBoardChecklistItemDto {
  id: string;
  text: string;
  done: boolean;
}

export interface ProjectBoardAssigneeDto {
  kind: ProjectBoardAssigneeKind;
  id?: string | null;
  display_name?: string | null;
  mode_version_id?: string | null;
  harness_id?: string | null;
}

export interface ProjectBoardSpatialRefDto {
  session_id: string;
  object_id: string;
  viewport_hint?: { x: number; y: number; zoom: number } | Record<string, unknown> | null;
}

export interface ProjectBoardDiffStatsDto {
  added: number;
  removed: number;
  files: number;
}

export interface ProjectBoardDeclarativeSnapshotDto {
  title?: string;
  description_md?: string | null;
  checklist?: ProjectBoardChecklistItemDto[];
  status?: ProjectBoardTaskStatus;
  priority?: ProjectBoardTaskPriority;
  blocked?: boolean;
  blocked_reason?: string | null;
  assignee?: ProjectBoardAssigneeDto | null;
  parent_task_id?: string | null;
  depends_on?: string[];
  labels?: string[];
  spatial_ref?: ProjectBoardSpatialRefDto | null;
  sort_key?: string;
  revision?: number;
  updated_at_unix_ms?: number;
  [key: string]: unknown;
}

export interface ProjectBoardSyncConflictDto {
  file_rel_path: string;
  detected_at_unix_ms: number;
  file_snapshot: ProjectBoardDeclarativeSnapshotDto;
  board_snapshot: ProjectBoardDeclarativeSnapshotDto;
  conflicting_fields: string[];
}

export interface ProjectBoardTaskDto {
  task_id: string;
  project_id: string;
  project_name?: string | null;
  task_key: string;
  title: string;
  description_md: string;
  checklist: ProjectBoardChecklistItemDto[];
  status: ProjectBoardTaskStatus;
  priority: ProjectBoardTaskPriority;
  blocked: boolean;
  blocked_reason?: string | null;
  assignee?: ProjectBoardAssigneeDto | null;
  parent_task_id?: string | null;
  depends_on: string[];
  labels: string[];
  spatial_ref?: ProjectBoardSpatialRefDto | null;
  sort_key: string;
  active_session_id?: string | null;
  active_run_id?: string | null;
  run_status?: string | null;
  worktree_path?: string | null;
  lease_id?: string | null;
  live_digest_summary?: string | null;
  diff_stats?: ProjectBoardDiffStatsDto | null;
  has_sync_conflict: boolean;
  sync_conflict?: ProjectBoardSyncConflictDto | null;
  revision: number;
  created_at_unix_ms: number;
  updated_at_unix_ms: number;
}

export interface CreateProjectBoardTaskInput {
  title: string;
  description_md?: string | null;
  checklist?: ProjectBoardChecklistItemDto[] | null;
  status?: ProjectBoardTaskStatus | null;
  priority?: ProjectBoardTaskPriority | null;
  blocked?: boolean | null;
  blocked_reason?: string | null;
  assignee?: ProjectBoardAssigneeDto | null;
  parent_task_id?: string | null;
  depends_on?: string[] | null;
  labels?: string[] | null;
  spatial_ref?: ProjectBoardSpatialRefDto | null;
  sort_key?: string | null;
}

export interface PromoteFromSpatialInput {
  session_id: string;
  object_ids: string[];
  mode?: 'merge_single' | 'split_many' | 'parent_with_children';
  target_status?: ProjectBoardTaskStatus;
  priority?: ProjectBoardTaskPriority | null;
  assignee?: ProjectBoardAssigneeDto | null;
  labels?: string[] | null;
  viewport_hint?: Record<string, unknown> | null;
  writer_id?: string | null;
}

export interface SpatialObjectTaskLinkDto {
  object_id: string;
  task_id: string;
  task_key: string;
}

export interface PromoteFromSpatialResultDto {
  mode: 'merge_single' | 'split_many' | 'parent_with_children';
  parent_task_id: string | null;
  tasks: ProjectBoardTaskDto[];
  updated_object_links: SpatialObjectTaskLinkDto[];
  spatial_seq: number;
}

export interface UpdateProjectBoardTaskInput {
  title?: string | null;
  description_md?: string | null;
  checklist?: ProjectBoardChecklistItemDto[] | null;
  status?: ProjectBoardTaskStatus | null;
  priority?: ProjectBoardTaskPriority | null;
  blocked?: boolean | null;
  blocked_reason?: string | null;
  assignee?: ProjectBoardAssigneeDto | null;
  clear_assignee?: boolean | null;
  parent_task_id?: string | null;
  clear_parent_task_id?: boolean | null;
  depends_on?: string[] | null;
  labels?: string[] | null;
  spatial_ref?: ProjectBoardSpatialRefDto | null;
  clear_spatial_ref?: boolean | null;
  sort_key?: string | null;
  expected_revision?: number | null;
}

export interface ProjectBoardFiltersDto {
  project_ids?: string[];
  assignee_kinds?: ProjectBoardAssigneeKind[];
  statuses?: ProjectBoardTaskStatus[];
  priorities?: ProjectBoardTaskPriority[];
  labels?: string[];
  needs_my_action?: boolean;
  has_conflict?: boolean;
  query?: string;
}

export interface ProjectBoardViewConfigDto {
  layout: ProjectBoardLayoutMode;
  group_by: ProjectBoardGroupBy;
  swimlane_by?: ProjectBoardSwimlaneBy | null;
  sort_by: ProjectBoardSortBy;
  filters: ProjectBoardFiltersDto;
  visible_fields: ProjectBoardVisibleField[];
}

export interface ProjectBoardViewDto {
  view_id: string;
  scope: ProjectBoardViewScope;
  project_id?: string | null;
  name: string;
  config: ProjectBoardViewConfigDto;
  sort_order: number;
  created_at_unix_ms: number;
  updated_at_unix_ms: number;
}

export interface CreateProjectBoardViewInput {
  scope: ProjectBoardViewScope;
  project_id?: string | null;
  name: string;
  config: ProjectBoardViewConfigDto;
  sort_order?: number | null;
}

export interface UpdateProjectBoardViewInput {
  name?: string | null;
  config?: ProjectBoardViewConfigDto | null;
  sort_order?: number | null;
}

export interface ProjectBoardSummaryProjectItemDto {
  project_id: string;
  project_name: string;
  total_tasks: number;
  in_progress_count: number;
  in_review_count: number;
  blocked_count: number;
  conflict_count: number;
  needs_action_count: number;
}

export interface ProjectBoardSummaryDto {
  total_tasks: number;
  backlog_count: number;
  ready_count: number;
  in_progress_count: number;
  in_review_count: number;
  done_count: number;
  blocked_count: number;
  conflict_count: number;
  active_run_count: number;
  needs_action_count: number;
  projects: ProjectBoardSummaryProjectItemDto[];
  updated_at_unix_ms: number;
}

export interface ProjectBoardSyncResultDto {
  project_id: string;
  storage_mode: ProjectBoardStorageMode;
  resolved_board_dir?: string | null;
  imported_count: number;
  exported_count: number;
  merged_count: number;
  unchanged_count: number;
  conflict_count: number;
  synced_at_unix_ms: number;
}

export interface ResolveProjectBoardConflictInput {
  resolution: ProjectBoardConflictResolution;
}

export interface DispatchProjectBoardTaskInput {
  assignee_override?: ProjectBoardAssigneeDto | null;
  use_worktree?: boolean | null;
  extra_prompt?: string | null;
}

export interface ProjectBoardTaskDiffFileDto {
  path: string;
  status: string;
  added: number;
  removed: number;
}

export interface ProjectBoardTaskDiffDto {
  worktree_path?: string | null;
  diff_text: string;
  files: ProjectBoardTaskDiffFileDto[];
}

export interface ApproveProjectBoardTaskInput {
  merge_or_clean_worktree?: boolean | null;
  note?: string | null;
}

export interface RequestTaskChangesInput {
  feedback: string;
}

export interface ListBoardTasksQuery {
  project_id?: string;
  project_ids?: string[];
  status?: ProjectBoardTaskStatus;
  statuses?: ProjectBoardTaskStatus[];
  priority?: ProjectBoardTaskPriority;
  priorities?: ProjectBoardTaskPriority[];
  assignee_kind?: ProjectBoardAssigneeKind;
  assignee_kinds?: ProjectBoardAssigneeKind[];
  labels?: string[];
  needs_my_action?: boolean;
  has_conflict?: boolean;
  query?: string;
}

export interface ProjectBoardSseEventDto {
  seq: number;
  event_type: ProjectBoardEventType;
  project_id?: string | null;
  task_id?: string | null;
  task?: ProjectBoardTaskDto | null;
  tasks?: ProjectBoardTaskDto[] | null;
  settings?: ProjectBoardSettingsDto | null;
  view_id?: string | null;
  view?: ProjectBoardViewDto | null;
  views?: ProjectBoardViewDto[] | null;
  sync_result?: ProjectBoardSyncResultDto | null;
  summary?: ProjectBoardSummaryDto | null;
  notes?: ProjectNotesDto | null;
  autopilot?: ProjectAutopilotStateDto | null;
  occurred_at_unix_ms: number;
}

export interface BoardEventStreamOptions {
  afterSeq?: number;
  storageId?: string;
  fetchImpl?: typeof fetch;
  idleTimeoutMs?: number;
  reconnectDelayMs?: number;
  maxReconnectDelayMs?: number;
  onOpen?: () => void;
  onError?: (error: Error) => void;
}

export interface BoardEventStreamHandle {
  close: () => void;
  readonly closed: boolean;
  readonly lastSeq: number;
}

function serializeBoardTaskQuery(params: ListBoardTasksQuery): string {
  const search = new URLSearchParams();
  if (params.project_id) search.set('project_id', selectionIdentity(params.project_id).id);
  if (params.project_ids?.length) {
    search.set('project_ids', params.project_ids.map((id) => selectionIdentity(id).id).join(','));
  }
  if (params.status) search.set('status', params.status);
  if (params.statuses?.length) search.set('statuses', params.statuses.join(','));
  if (params.priority) search.set('priority', params.priority);
  if (params.priorities?.length) search.set('priorities', params.priorities.join(','));
  if (params.assignee_kind) search.set('assignee_kind', params.assignee_kind);
  if (params.assignee_kinds?.length) search.set('assignee_kinds', params.assignee_kinds.join(','));
  if (params.labels?.length) search.set('labels', params.labels.join(','));
  if (params.needs_my_action !== undefined) search.set('needs_my_action', String(params.needs_my_action));
  if (params.has_conflict !== undefined) search.set('has_conflict', String(params.has_conflict));
  if (params.query) search.set('query', params.query);
  const qs = search.toString();
  return qs ? `?${qs}` : '';
}

function normalizeBoardTaskDto(raw: Partial<ProjectBoardTaskDto>): ProjectBoardTaskDto {
  return {
    task_id: typeof raw.task_id === 'string' ? raw.task_id : '',
    project_id: typeof raw.project_id === 'string' ? raw.project_id : '',
    project_name: typeof raw.project_name === 'string' ? raw.project_name : null,
    task_key: typeof raw.task_key === 'string' ? raw.task_key : '',
    title: typeof raw.title === 'string' ? raw.title : '',
    description_md: typeof raw.description_md === 'string' ? raw.description_md : '',
    checklist: Array.isArray(raw.checklist)
      ? raw.checklist.map((item) => ({
          id: typeof item?.id === 'string' ? item.id : '',
          text: typeof item?.text === 'string' ? item.text : '',
          done: Boolean(item?.done),
        }))
      : [],
    status: (typeof raw.status === 'string' ? raw.status : 'backlog') as ProjectBoardTaskStatus,
    priority: (typeof raw.priority === 'string' ? raw.priority : 'none') as ProjectBoardTaskPriority,
    blocked: Boolean(raw.blocked),
    blocked_reason: typeof raw.blocked_reason === 'string' ? raw.blocked_reason : null,
    assignee: raw.assignee && typeof raw.assignee === 'object' ? (raw.assignee as ProjectBoardAssigneeDto) : null,
    parent_task_id: typeof raw.parent_task_id === 'string' ? raw.parent_task_id : null,
    depends_on: Array.isArray(raw.depends_on) ? raw.depends_on.filter((id): id is string => typeof id === 'string') : [],
    labels: Array.isArray(raw.labels) ? raw.labels.filter((label): label is string => typeof label === 'string') : [],
    spatial_ref: raw.spatial_ref && typeof raw.spatial_ref === 'object'
      ? (raw.spatial_ref as ProjectBoardSpatialRefDto)
      : null,
    sort_key: typeof raw.sort_key === 'string' && raw.sort_key.length > 0 ? raw.sort_key : 'V',
    active_session_id: typeof raw.active_session_id === 'string' ? raw.active_session_id : null,
    active_run_id: typeof raw.active_run_id === 'string' ? raw.active_run_id : null,
    run_status: typeof raw.run_status === 'string' ? raw.run_status : null,
    worktree_path: typeof raw.worktree_path === 'string' ? raw.worktree_path : null,
    lease_id: typeof raw.lease_id === 'string' ? raw.lease_id : null,
    live_digest_summary: typeof raw.live_digest_summary === 'string' ? raw.live_digest_summary : null,
    diff_stats: raw.diff_stats && typeof raw.diff_stats === 'object' ? (raw.diff_stats as ProjectBoardDiffStatsDto) : null,
    has_sync_conflict: Boolean(raw.has_sync_conflict),
    sync_conflict: raw.sync_conflict && typeof raw.sync_conflict === 'object'
      ? (raw.sync_conflict as ProjectBoardSyncConflictDto)
      : null,
    revision: typeof raw.revision === 'number' ? raw.revision : 1,
    created_at_unix_ms: typeof raw.created_at_unix_ms === 'number' ? raw.created_at_unix_ms : Date.now(),
    updated_at_unix_ms: typeof raw.updated_at_unix_ms === 'number' ? raw.updated_at_unix_ms : Date.now(),
  };
}

function normalizeBoardSummaryDto(
  raw: Partial<ProjectBoardSummaryDto> & Record<string, unknown>,
): ProjectBoardSummaryDto {
  const num = (v: unknown): number => (typeof v === 'number' && Number.isFinite(v) ? v : 0);
  const inReview = num(raw?.in_review_count);
  const blocked = num(raw?.blocked_count);
  const conflict = num(raw?.conflict_count);
  const needsAction = typeof raw?.needs_action_count === 'number'
    ? raw.needs_action_count
    : inReview + blocked + conflict;
  const activeRuns = typeof raw?.active_run_count === 'number'
    ? raw.active_run_count
    : num(raw?.active_runs_count);
  return {
    total_tasks: num(raw?.total_tasks),
    backlog_count: num(raw?.backlog_count),
    ready_count: num(raw?.ready_count),
    in_progress_count: num(raw?.in_progress_count),
    in_review_count: inReview,
    done_count: num(raw?.done_count),
    blocked_count: blocked,
    conflict_count: conflict,
    active_run_count: activeRuns,
    needs_action_count: needsAction,
    projects: Array.isArray(raw?.projects)
      ? raw.projects.map((p) => ({
          project_id: typeof p?.project_id === 'string' ? p.project_id : '',
          project_name: typeof p?.project_name === 'string' ? p.project_name : '',
          total_tasks: num(p?.total_tasks),
          in_progress_count: num(p?.in_progress_count),
          in_review_count: num(p?.in_review_count),
          blocked_count: num(p?.blocked_count),
          conflict_count: num(p?.conflict_count),
          needs_action_count: num(p?.needs_action_count),
        }))
      : [],
    updated_at_unix_ms: typeof raw?.updated_at_unix_ms === 'number' ? raw.updated_at_unix_ms : Date.now(),
  };
}

function openBoardEventStream(
  path: string,
  onEvent: (event: ProjectBoardSseEventDto) => void,
  options: BoardEventStreamOptions = {},
): BoardEventStreamHandle {
  const storageId = options.storageId ?? captureStorageId(path);
  const fetchImpl = options.fetchImpl ?? fetch;
  const idleTimeoutMs = options.idleTimeoutMs ?? 45_000;
  const baseReconnectDelayMs = options.reconnectDelayMs ?? 1_000;
  const maxReconnectDelayMs = options.maxReconnectDelayMs ?? 10_000;

  let closed = false;
  let lastSeq = Math.max(0, options.afterSeq ?? 0);
  let reconnectAttempt = 0;
  let abortController: AbortController | null = null;
  let retryTimer: ReturnType<typeof setTimeout> | null = null;

  function close(): void {
    closed = true;
    if (retryTimer !== null) {
      clearTimeout(retryTimer);
      retryTimer = null;
    }
    abortController?.abort();
    abortController = null;
  }

  function dispatchBlock(block: string): void {
    let eventName = 'message';
    let frameId = '';
    const dataLines: string[] = [];
    for (const line of block.replace(/\r/g, '').split('\n')) {
      if (!line || line.startsWith(':')) continue;
      const sep = line.indexOf(':');
      const key = sep < 0 ? line : line.slice(0, sep);
      const value = sep < 0 ? '' : line.slice(sep + 1).replace(/^ /, '');
      if (key === 'event') eventName = value || 'message';
      else if (key === 'id') frameId = value;
      else if (key === 'data') dataLines.push(value);
    }

    if (frameId && /^\d+$/.test(frameId)) {
      const parsedSeq = Number.parseInt(frameId, 10);
      if (Number.isFinite(parsedSeq) && parsedSeq > lastSeq) {
        lastSeq = parsedSeq;
      }
    }

    if (eventName === 'heartbeat' || eventName === 'keepalive' || dataLines.length === 0) return;

    let parsed: Record<string, unknown>;
    try {
      const json = JSON.parse(dataLines.join('\n'));
      if (!json || typeof json !== 'object') return;
      parsed = json as Record<string, unknown>;
    } catch {
      return;
    }

    const payloadSeq = typeof parsed.seq === 'number' && Number.isFinite(parsed.seq) ? parsed.seq : lastSeq;
    if (payloadSeq > lastSeq) lastSeq = payloadSeq;
    const resolvedType = (
      typeof parsed.event_type === 'string' && parsed.event_type
        ? parsed.event_type
        : eventName !== 'message'
          ? eventName
          : 'task_upserted'
    ) as ProjectBoardEventType;

    const normalizedEvent: ProjectBoardSseEventDto = {
      seq: payloadSeq,
      event_type: resolvedType,
      project_id: typeof parsed.project_id === 'string' ? parsed.project_id : null,
      task_id: typeof parsed.task_id === 'string' ? parsed.task_id : null,
      task: parsed.task && typeof parsed.task === 'object'
        ? normalizeBoardTaskDto(parsed.task as Record<string, unknown>)
        : null,
      tasks: Array.isArray(parsed.tasks)
        ? parsed.tasks.map((item) => normalizeBoardTaskDto(item as Record<string, unknown>))
        : null,
      settings: parsed.settings && typeof parsed.settings === 'object'
        ? (parsed.settings as ProjectBoardSettingsDto)
        : null,
      view_id: typeof parsed.view_id === 'string' ? parsed.view_id : null,
      view: parsed.view && typeof parsed.view === 'object' ? (parsed.view as ProjectBoardViewDto) : null,
      views: Array.isArray(parsed.views) ? (parsed.views as ProjectBoardViewDto[]) : null,
      sync_result: parsed.sync_result && typeof parsed.sync_result === 'object'
        ? (parsed.sync_result as ProjectBoardSyncResultDto)
        : null,
      summary: parsed.summary && typeof parsed.summary === 'object'
        ? normalizeBoardSummaryDto(parsed.summary as Record<string, unknown>)
        : null,
      notes: parsed.notes && typeof parsed.notes === 'object'
        ? normalizeProjectNotesDto(parsed.notes as Partial<ProjectNotesDto>)
        : null,
      autopilot: parsed.autopilot && typeof parsed.autopilot === 'object'
        ? normalizeProjectAutopilotStateDto(parsed.autopilot as Partial<ProjectAutopilotStateDto>)
        : null,
      occurred_at_unix_ms: typeof parsed.occurred_at_unix_ms === 'number' ? parsed.occurred_at_unix_ms : Date.now(),
    };

    try {
      onEvent(normalizedEvent);
    } catch {
      // Consumer handler errors must not break the SSE reader loop.
    }
  }

  async function connect(): Promise<void> {
    if (closed) return;
    const controller = new AbortController();
    abortController = controller;
    const normalized = normalizeStorageRequest(path, { storageId });
    const querySep = normalized.path.includes('?') ? '&' : '?';
    const pathWithCursor = lastSeq > 0 ? `${normalized.path}${querySep}after_seq=${lastSeq}` : normalized.path;
    const fullUrl = `${gatewayUrl}${pathWithCursor}`;

    try {
      await assertHostAccess(pathWithCursor, controller.signal);
      if (closed || controller.signal.aborted) return;

      const headers = storageHeaders(pathWithCursor, { storageId });
      headers.set('accept', 'text/event-stream');
      if (lastSeq > 0) headers.set('last-event-id', String(lastSeq));

      const response = await fetchImpl(fullUrl, {
        method: 'GET',
        headers,
        credentials: 'include',
        signal: controller.signal,
      });

      if (closed || controller.signal.aborted) {
        await response.body?.cancel().catch(() => undefined);
        return;
      }
      if (response.status === 404) {
        close();
        return;
      }
      if (!response.ok || !response.body) {
        throw new Error(`Board event stream returned ${response.status}`);
      }

      reconnectAttempt = 0;
      options.onOpen?.();

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      let idleTimer: ReturnType<typeof setTimeout> | null = null;
      const armIdle = () => {
        if (idleTimer !== null) clearTimeout(idleTimer);
        idleTimer = setTimeout(() => controller.abort(), idleTimeoutMs);
      };
      controller.signal.addEventListener(
        'abort',
        () => {
          if (idleTimer !== null) clearTimeout(idleTimer);
        },
        { once: true },
      );
      armIdle();

      while (!closed && !controller.signal.aborted) {
        const { done, value } = await reader.read();
        armIdle();
        if (closed || controller.signal.aborted) break;
        buffer = (buffer + decoder.decode(value, { stream: !done })).replace(/\r\n/g, '\n');
        let boundary: number;
        while ((boundary = buffer.indexOf('\n\n')) >= 0) {
          dispatchBlock(buffer.slice(0, boundary));
          buffer = buffer.slice(boundary + 2);
          if (closed) break;
        }
        if (done) {
          if (buffer.trim() && !closed) dispatchBlock(buffer);
          break;
        }
      }
      if (idleTimer !== null) clearTimeout(idleTimer);
    } catch (error) {
      if (!closed) {
        options.onError?.(error instanceof Error ? error : new Error(String(error)));
      }
    }

    if (!closed) {
      const delay = Math.min(baseReconnectDelayMs * Math.pow(2, reconnectAttempt), maxReconnectDelayMs);
      reconnectAttempt += 1;
      retryTimer = setTimeout(() => {
        retryTimer = null;
        void connect();
      }, delay);
    }
  }

  void connect();

  return {
    close,
    get closed() {
      return closed;
    },
    get lastSeq() {
      return lastSeq;
    },
  };
}

function normalizePromoteFromSpatialResultDto(
  raw: Partial<PromoteFromSpatialResultDto>,
): PromoteFromSpatialResultDto {
  const mode =
    raw?.mode === 'merge_single' || raw?.mode === 'split_many' || raw?.mode === 'parent_with_children'
      ? raw.mode
      : 'split_many';
  return {
    mode,
    parent_task_id: typeof raw?.parent_task_id === 'string' ? raw.parent_task_id : null,
    tasks: Array.isArray(raw?.tasks) ? raw.tasks.map((item) => normalizeBoardTaskDto(item)) : [],
    updated_object_links: Array.isArray(raw?.updated_object_links)
      ? raw.updated_object_links
          .filter((item): item is SpatialObjectTaskLinkDto => Boolean(item && typeof item === 'object'))
          .map((item) => ({
            object_id: typeof item.object_id === 'string' ? item.object_id : '',
            task_id: typeof item.task_id === 'string' ? item.task_id : '',
            task_key: typeof item.task_key === 'string' ? item.task_key : '',
          }))
          .filter((item) => Boolean(item.object_id && item.task_id))
      : [],
    spatial_seq: typeof raw?.spatial_seq === 'number' && Number.isFinite(raw.spatial_seq) ? raw.spatial_seq : 0,
  };
}

function normalizeProjectNotesDto(
  raw: Partial<ProjectNotesDto> | null | undefined,
  fallbackProjectId = '',
): ProjectNotesDto {
  const storageMode: ProjectBoardStorageMode =
    raw?.storage_mode === 'repo_tinadec' || raw?.storage_mode === 'app_data' || raw?.storage_mode === 'custom_dir'
      ? raw.storage_mode
      : 'repo_tinadec';
  return {
    project_id: typeof raw?.project_id === 'string' ? raw.project_id : selectionIdentity(fallbackProjectId).id,
    content_md: typeof raw?.content_md === 'string' ? raw.content_md : '',
    resolved_file_path: typeof raw?.resolved_file_path === 'string' ? raw.resolved_file_path : '',
    storage_mode: storageMode,
    revision: typeof raw?.revision === 'number' && Number.isFinite(raw.revision) ? raw.revision : 1,
    last_synced_hash: typeof raw?.last_synced_hash === 'string' ? raw.last_synced_hash : '',
    updated_at_unix_ms:
      typeof raw?.updated_at_unix_ms === 'number' && Number.isFinite(raw.updated_at_unix_ms)
        ? raw.updated_at_unix_ms
        : Date.now(),
  };
}

function normalizeCoordinatorTaskDraftDto(
  raw: Partial<CoordinatorTaskDraftDto> | null | undefined,
  fallbackIndex = 0,
): CoordinatorTaskDraftDto {
  return {
    draft_id: typeof raw?.draft_id === 'string' && raw.draft_id ? raw.draft_id : `draft-${fallbackIndex + 1}`,
    title: typeof raw?.title === 'string' ? raw.title : '',
    description_md: typeof raw?.description_md === 'string' ? raw.description_md : '',
    checklist: Array.isArray(raw?.checklist)
      ? raw.checklist.map((item) => ({
          id: typeof item?.id === 'string' ? item.id : '',
          text: typeof item?.text === 'string' ? item.text : '',
          done: Boolean(item?.done),
        }))
      : [],
    priority: (typeof raw?.priority === 'string' ? raw.priority : 'medium') as ProjectBoardTaskPriority,
    status: (typeof raw?.status === 'string' ? raw.status : 'ready') as ProjectBoardTaskStatus,
    assignee: raw?.assignee && typeof raw.assignee === 'object' ? (raw.assignee as ProjectBoardAssigneeDto) : null,
    labels: Array.isArray(raw?.labels) ? raw.labels.filter((lbl): lbl is string => typeof lbl === 'string') : [],
    depends_on_draft_ids: Array.isArray(raw?.depends_on_draft_ids)
      ? raw.depends_on_draft_ids.filter((id): id is string => typeof id === 'string')
      : [],
    depends_on_task_ids: Array.isArray(raw?.depends_on_task_ids)
      ? raw.depends_on_task_ids.filter((id): id is string => typeof id === 'string')
      : [],
  };
}

function normalizeCoordinatorPlanResultDto(
  raw: Partial<CoordinatorPlanResultDto> | null | undefined,
  fallbackProjectId = '',
): CoordinatorPlanResultDto {
  const plannerSource: CoordinatorPlannerSource = raw?.planner_source === 'llm' ? 'llm' : 'heuristic';
  return {
    project_id: typeof raw?.project_id === 'string' ? raw.project_id : selectionIdentity(fallbackProjectId).id,
    goal: typeof raw?.goal === 'string' ? raw.goal : '',
    summary: typeof raw?.summary === 'string' ? raw.summary : '',
    planner_source: plannerSource,
    auto_applied: Boolean(raw?.auto_applied),
    drafts: Array.isArray(raw?.drafts)
      ? raw.drafts.map((item, idx) => normalizeCoordinatorTaskDraftDto(item, idx))
      : [],
    created_tasks: Array.isArray(raw?.created_tasks) ? raw.created_tasks.map((item) => normalizeBoardTaskDto(item)) : [],
  };
}

function normalizeCoordinatorApplyPlanResultDto(
  raw: Partial<CoordinatorApplyPlanResultDto> | null | undefined,
  fallbackProjectId = '',
): CoordinatorApplyPlanResultDto {
  return {
    project_id: typeof raw?.project_id === 'string' ? raw.project_id : selectionIdentity(fallbackProjectId).id,
    created_tasks: Array.isArray(raw?.created_tasks) ? raw.created_tasks.map((item) => normalizeBoardTaskDto(item)) : [],
  };
}

function normalizeProjectAutopilotStateDto(
  raw: Partial<ProjectAutopilotStateDto> | null | undefined,
  fallbackProjectId = '',
): ProjectAutopilotStateDto {
  const num = (v: unknown, def = 0): number => (typeof v === 'number' && Number.isFinite(v) ? v : def);
  const blockedTasks = Array.isArray(raw?.blocked_tasks)
    ? raw.blocked_tasks.map((item) => normalizeBoardTaskDto(item))
    : [];
  return {
    project_id: typeof raw?.project_id === 'string' ? raw.project_id : selectionIdentity(fallbackProjectId).id,
    autopilot_enabled: Boolean(raw?.autopilot_enabled),
    max_concurrency: Math.max(1, Math.min(8, num(raw?.max_concurrency, 2))),
    active_task_count: num(raw?.active_task_count),
    active_task_ids: Array.isArray(raw?.active_task_ids)
      ? raw.active_task_ids.filter((id): id is string => typeof id === 'string')
      : [],
    queued_ready_count: num(raw?.queued_ready_count),
    blocked_task_count:
      typeof raw?.blocked_task_count === 'number' && Number.isFinite(raw.blocked_task_count)
        ? raw.blocked_task_count
        : blockedTasks.length,
    blocked_tasks: blockedTasks,
    last_tick_at_unix_ms:
      typeof raw?.last_tick_at_unix_ms === 'number' && Number.isFinite(raw.last_tick_at_unix_ms)
        ? raw.last_tick_at_unix_ms
        : null,
    last_error: typeof raw?.last_error === 'string' ? raw.last_error : null,
    updated_at_unix_ms: num(raw?.updated_at_unix_ms, Date.now()),
  };
}

export const getProjectOverview = api.getProjectOverview;
export const getProjectBoardSettings = api.getProjectBoardSettings;
export const updateProjectBoardSettings = api.updateProjectBoardSettings;
export const syncProjectBoard = api.syncProjectBoard;
export const listProjectBoardTasks = api.listProjectBoardTasks;
export const createProjectBoardTask = api.createProjectBoardTask;
export const promoteSpatialToBoard = api.promoteSpatialToBoard;
export const getProjectBoardTask = api.getProjectBoardTask;
export const updateProjectBoardTask = api.updateProjectBoardTask;
export const deleteProjectBoardTask = api.deleteProjectBoardTask;
export const resolveProjectBoardConflict = api.resolveProjectBoardConflict;
export const dispatchProjectBoardTask = api.dispatchProjectBoardTask;
export const getProjectBoardTaskDiff = api.getProjectBoardTaskDiff;
export const approveProjectBoardTask = api.approveProjectBoardTask;
export const requestProjectBoardTaskChanges = api.requestProjectBoardTaskChanges;
export const cancelProjectBoardTask = api.cancelProjectBoardTask;
export const listGlobalBoardTasks = api.listGlobalBoardTasks;
export const getGlobalBoardSummary = api.getGlobalBoardSummary;
export const listBoardViews = api.listBoardViews;
export const createBoardView = api.createBoardView;
export const updateBoardView = api.updateBoardView;
export const deleteBoardView = api.deleteBoardView;
export const subscribeProjectBoardEvents = api.subscribeProjectBoardEvents;
export const subscribeGlobalBoardEvents = api.subscribeGlobalBoardEvents;
export const getProjectNotes = api.getProjectNotes;
export const updateProjectNotes = api.updateProjectNotes;
export const planCoordinatorTasks = api.planCoordinatorTasks;
export const applyCoordinatorPlan = api.applyCoordinatorPlan;
export const getProjectAutopilot = api.getProjectAutopilot;
export const updateProjectAutopilot = api.updateProjectAutopilot;

