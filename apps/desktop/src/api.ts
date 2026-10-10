import { readBackendResponse } from '@/lib/backendRequest'
import type { AgentPackEnvelope } from '@/agentPacks/GraphSeedPack'
import { CORE_EVENT_TYPES } from '@/events/coreEventTypes'
import type { components } from '@/generated/schema'
import type { MessageAttachmentDto, MessageDto, SseChunk } from '@/generated/client'
import { createRunStream, runStreamDelta, type RunStreamHandle } from '@/lib/runStream'
import { ApiError } from '@/lib/apiError'
import { captureStorageId, rememberStorageResult, normalizeStorageRequest, storageHeaders, storageFetch, sessionStorageId, selectionIdentity, type StorageRequestOptions } from '@/lib/storageScope'
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

export interface SessionDto {
  storage_id?: string;
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
 * A market source as Core stores it. There is no `created_at`: the row has one, the route does
 * not send it, and this interface used to promise it — which is how a UI ends up rendering a
 * field that is undefined on every machine.
 */
export interface ExtensionSourceDto {
  id: string;
  name: string;
  kind: string;
  location: string;
  enabled: boolean;
  revision: number;
  last_refreshed_at?: string;
  /** Why the last refresh did not land. Present alongside the row so an empty catalog can be read as an outage. */
  last_error?: string;
  entry_count: number;
}

/** The list is an envelope because `supported_kinds` decides what the picker may offer at all. */
export interface MarketSourceListDto {
  sources: ExtensionSourceDto[];
  supported_kinds: string[];
}

/** One refresh's answer. `outcome` is the field to branch on, not `fetched_rows`. */
export interface MarketRefreshDto {
  source_id: string;
  outcome: 'fetched' | 'blocked' | 'unavailable' | string;
  fetched_rows: number;
  refused_rows: number;
  removed_rows: number;
  /** Rows a running install still references, so a dropped listing kept them instead of deleting. */
  retained_rows: number;
  pages_fetched: number;
  truncated_pages: boolean;
  reason?: string;
  refreshed_at?: string;
}

/**
 * One catalog row: what a source claimed, not what is installed. `publisher`, `capabilities`,
 * `permissions`, `status`, and `installed_extension_id` used to be declared here and sent by
 * nobody — the registry publishes no such fields, and an install surface that does will carry them
 * with its own source.
 */
export interface MarketCatalogItemDto {
  catalog_id: string;
  source_id: string;
  source_name: string;
  extension_id: string;
  kind: 'mcp-server' | 'skill' | 'acp-adapter' | 'tool-pack' | string;
  version: string;
  display_name: string;
  description?: string;
  homepage?: string;
  registry_type?: string;
  transports: string[];
  manifest_hash: string;
  refreshed_at: string;
  expires_at: string;
  /** False is not a defect in the entry — `install_blocker` says which case this is. */
  installable: boolean;
  install_blocker?: string;
}

export interface MarketCatalogPageDto {
  items: MarketCatalogItemDto[];
  total_available: number;
  has_more: boolean;
  /** Newest refresh among the matching rows; absent means nothing matched. */
  as_of?: string;
}

/** One environment variable a package asks for. Core's shape carries names and flags, never values. */
export interface MarketEnvironmentRequestDto {
  name: string;
  required: boolean;
  secret: boolean;
  description?: string;
}

/**
 * What a human says yes or no to: the command, the file, the exact bytes, and the instant after
 * which Core refuses to act. Nothing here is recomputed at apply time, so what was reviewed is
 * what lands. Field list is `MarketInstallProposalDto` in Core, keyed to the wire.
 */
export interface MarketInstallProposalDto {
  id: string;
  action: string;
  project_id?: string | null;
  catalog_id?: string | null;
  installation_id?: string | null;
  source_name: string;
  extension_id: string;
  kind: string;
  /** The exact version pinned. Never a range, never `latest`. */
  version: string;
  server_id: string;
  /** The command line this write overwrites, when the config already named this server. */
  replaces_command?: string | null;
  command?: string | null;
  args: string[];
  environment: MarketEnvironmentRequestDto[];
  target_path: string;
  content: string;
  /** Absent when the file could not be read as well as when it does not exist: create, never clobber. */
  expected_file_hash?: string | null;
  digest: string;
  expires_at: string;
  /** What this phase cannot guarantee, stated for the reader of the approval. */
  warnings: string[];
  scope?: 'shared' | 'project';
  resource_id?: string | null;
  package_hash?: string | null;
  package_files?: { path: string; content_hash: string; size_bytes: number }[];
  availability?: string | null;
}

/**
 * An entry this workspace approved, and where the write for it stands. The status is read live from
 * the linked user tool action rather than copied into Core's row, so a failed or still-pending
 * approval can never be reported as an install.
 */
export interface MarketInstallationDto {
  id: string;
  project_id?: string | null;
  catalog_id: string;
  source_name: string;
  extension_id: string;
  kind: string;
  version: string;
  server_id: string;
  config_path: string;
  /** `installing` or `removing`; a removal that finished is reported as gone. */
  state: string;
  install_action_id: string;
  uninstall_action_id?: string | null;
  /** Live status of whichever action is in front of the user; absent means none is running. */
  action_status?: string | null;
  created_at: string;
  updated_at: string;
  scope?: 'shared' | 'project';
  resource_id?: string | null;
  package_hash?: string | null;
  package_files?: { path: string; content_hash: string; size_bytes: number }[];
  availability?: string | null;
  reason?: string | null;
}

/** Vocabulary for `MarketInstallProposalDto.action`; Core owns the words, this mirrors them. */
export const MARKET_INSTALL_ACTION_INSTALL = 'install'
export const MARKET_INSTALL_ACTION_UNINSTALL = 'uninstall'

/** An envelope, not a bare array: the row count alone cannot say the surface was never used. */
export interface MarketInstallationListDto {
  installations: MarketInstallationDto[];
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
  role?: string;
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

export interface AgentDirectoryItemDto {
  id: string;
  slug: string;
  display_name: string;
  layer: 'operation' | 'execution' | string;
  role: string;
  source_kind: 'custom' | 'pack' | 'bootstrap' | 'missing_reference' | string;
  source_key: string;
  managed: boolean;
  writable: boolean;
  enabled: boolean;
  status: string;
  revision: number;
  version: number;
  current_version_id?: string | null;
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
  max_parallel_executors?: number;
  worktree_isolation?: boolean;
  approval_required?: boolean;
  budget_policy?: string;
  status?: string;
  managed?: boolean;
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
  layer: 'operation' | 'execution' | string;
  /** Legacy flat shape. */
  agent_type?: string;
  /** Versioned shape. */
  role?: string;
  model_route_purpose?: string | null;
  /** Versioned shape: { kind: inherit|route|fixed, ... }. */
  model_strategy?: Record<string, unknown> | 'inherit' | 'route' | 'fixed' | string | null;
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

export interface AgentModeNodeDto {
  id: string;
  agent_id: string;
  lane: 'operation' | 'execution';
  position: { x: number; y: number };
  label?: string | null;
  model_strategy_override?: { kind: 'inherit' | 'route' | 'fixed'; route_purpose?: string; provider_instance_id?: string; model?: string | null } | null;
  /** Core node_key for published projections; write-back uses it before id. */
  node_key?: string;
  data?: Record<string, unknown> | null;
}

export interface AgentModeEdgeDto {
  id: string;
  source: string;
  target: string;
  label?: string | null;
}

/** Core UpsertModeTopology write contract (node_key/agent_definition_id/layer). */
export interface AgentModeTopologyWriteDto {
  nodes: Array<{ node_key: string; agent_definition_id: string; layer: 'operation' | 'execution'; label?: string | null; position?: { x: number; y: number } | null; model_strategy_override?: Record<string, unknown> | null }>;
  edges: Array<{ source_node_key: string; target_node_key: string; condition?: Record<string, unknown> }>;
  canvas_layout?: Record<string, unknown> | null;
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
  nodes: AgentModeNodeDto[];
  edges: AgentModeEdgeDto[];
  canvas_layout?: Record<string, unknown> | null;
  status?: string;
  /** True when an installed Agent Pack owns this mode (read-only; clone to customize). */
  managed?: boolean;
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
  created_at: string;
  is_active?: boolean;
}

export interface PromptPipelineDto {
  id: string;
  name?: string | null;
  title?: string | null;
  key?: string | null;
  description?: string | null;
  scope?: string | null;
  status?: string;
  version?: number | null;
  revision?: number | null;
  etag?: string | null;
  nodes?: unknown[] | null;
  edges?: unknown[] | null;
  canvas_layout?: Record<string, unknown> | null;
  created_at?: string | null;
  updated_at?: string | null;
}

export interface PromptPipelineVersionDto {
  id: string;
  pipeline_id: string;
  version: number;
  content?: Record<string, unknown> | null;
  created_at: string;
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
}

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
      throw new Error(`Invalid response from server: ${text.substring(0, 200)}`);
    }
  }

  if (!response.ok) {
    const message = extractErrorMessage(data, response.statusText);
    throw new ApiError(message, response.status, data, { storageId });
  }

  rememberStorageResult(path, data, storageId)
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
  if (!project) throw new Error('The selected workspace is not registered in TinadecCore.');
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
  const seqFromId = Number(lastEventId)
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
    if (!response.ok) {
      const data = await response.json().catch(() => null)
      throw Object.assign(new Error(extractErrorMessage(data, response.statusText)), { status: response.status })
    }
    return { blob: await response.blob(), filename: response.headers.get('content-disposition')?.match(/filename="?([^";]+)"?/)?.[1] ?? `tinadec-${storageId}.zip` }
  },
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
  listToolSkills: (projectId?: string) => request<{ skills: ToolSkillResource[]; diagnostics: string[]; source: string }>(`/api/v1/tools/skills${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`),
  getToolSkill: (resourceId: string, projectId?: string) => request<ToolSkillResource>(`/api/v1/tools/skills/${encodeURIComponent(resourceId)}${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`),
  importToolSkill: (body: { scope: 'shared' | 'project'; project_id?: string; name: string; content: string; files?: Record<string, string>; enabled?: boolean }) => request<ToolSkillResource>('/api/v1/tools/skills/import', { method: 'POST', headers: { 'if-match': '"0"' }, body: JSON.stringify(body) }),
  getToolSkillFile: (resourceId: string, path: string, projectId?: string) => request<{ path: string; content_hash: string; size_bytes: number; content: string | null; base64: string }>(`/api/v1/tools/skills/${encodeURIComponent(resourceId)}/files?${new URLSearchParams({ path, ...(projectId ? { project_id: projectId } : {}) })}`),
  saveToolSkill: (resourceId: string, body: { content?: string; enabled?: boolean; files?: Record<string, string>; replace_files?: boolean; expected_file_hash?: string }, revision: number, projectId?: string) => request<ToolSkillResource>(`/api/v1/tools/skills/${encodeURIComponent(resourceId)}${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`, { method: 'PUT', headers: { 'if-match': `"${revision}"` }, body: JSON.stringify(body) }),
  deleteToolSkill: (resourceId: string, revision: number, projectId?: string) => request<ToolSkillResource>(`/api/v1/tools/skills/${encodeURIComponent(resourceId)}${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`, { method: 'DELETE', headers: { 'if-match': `"${revision}"` } }),
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
  doctor: () => request<DoctorReportDto>('/api/v1/doctor'),
  readiness: () => request<RuntimeReadinessReceiptDto>('/api/v1/readiness'),
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
  createSession: (projectId?: string | null, title?: string, modeVersionId?: string | null, viewMode: 'flat' | 'space' = 'flat', settings?: SessionSettingsUpdate) => request<SessionDto>('/api/v1/sessions', {
    method: 'POST',
    body: JSON.stringify({ project_id: projectId ?? undefined, title, mode_version_id: modeVersionId ?? undefined, ...(viewMode === 'space' ? { view_mode: viewMode } : {}), ...settings })
  }).then(session => {
    if (viewMode === 'space' && session.view_mode !== 'space') throw new Error('无法创建空间会话：服务尚未支持独立会话类型，请更新服务后重试。')
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
  listModelProviderTemplates: (options?: { signal?: AbortSignal }) => request<ModelProviderTemplateDto[]>('/api/v1/model-provider-templates', options),
  listModelProviders: (options?: { signal?: AbortSignal }) => request<ModelProviderInstanceDto[]>('/api/v1/model-providers', options),
  discoverHarnesses: () => request<CliDiscoveryResultDto>('/api/v1/model-providers/harnesses/discover'),
  connectHarness: (input: ConnectHarnessInput) => request<ModelProviderInstanceDto>('/api/v1/model-providers/harnesses/connect', {
    method: 'POST',
    body: JSON.stringify(input)
  }),
  refreshProviderModels: (providerInstanceId: string) => request<ModelDiscoveryResultDto>(`/api/v1/model-providers/${encodeURIComponent(providerInstanceId)}/models/refresh`, {
    method: 'POST'
  }),
  getModelReadiness: (options?: { signal?: AbortSignal }) => request<ModelReadinessReceiptDto>('/api/v1/model-readiness', options),
  getModelCatalogReadiness: (options?: { signal?: AbortSignal }) => request<ModelCatalogReadinessReceiptDto>('/api/v1/model-catalog-readiness', options),
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
  listModelRoutes: (options?: { signal?: AbortSignal }) => request<ModelRouteDto[]>('/api/v1/model-routes', options),
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
  listExtensionSources: () => request<MarketSourceListDto>('/api/v1/market/sources'),
  createExtensionSource: (source: { name: string; kind: string; location: string; enabled?: boolean }) => request<ExtensionSourceDto>('/api/v1/market/sources', {
    method: 'POST',
    body: JSON.stringify(source)
  }),
  setExtensionSourceEnabled: (sourceId: string, enabled: boolean) => request<ExtensionSourceDto>(`/api/v1/market/sources/${encodeURIComponent(sourceId)}`, {
    method: 'PATCH',
    body: JSON.stringify({ enabled })
  }),
  deleteExtensionSource: (sourceId: string) => request<void>(`/api/v1/market/sources/${encodeURIComponent(sourceId)}`, {
    method: 'DELETE'
  }),
  refreshExtensionSource: (sourceId: string) => request<MarketRefreshDto>(`/api/v1/market/sources/${encodeURIComponent(sourceId)}/refresh`, {
    method: 'POST'
  }),
  // `q`, not `query`: this used to send a parameter name Core has never read, so the search box
  // narrowed nothing over the wire while every mocked test passed.
  listMarketCatalog: (params: { kind?: string; q?: string; source_id?: string; limit?: number; offset?: number } = {}) => {
    const search = new URLSearchParams();
    if (params.kind && params.kind !== 'all') search.set('kind', params.kind);
    if (params.q) search.set('q', params.q);
    if (params.source_id) search.set('source_id', params.source_id);
    if (params.limit !== undefined) search.set('limit', String(params.limit));
    if (params.offset) search.set('offset', String(params.offset));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    return request<MarketCatalogPageDto>(`/api/v1/market/catalog${suffix}`);
  },
  getMarketCatalogItem: (catalogId: string) => request<MarketCatalogItemDto>(`/api/v1/market/catalog/${encodeURIComponent(catalogId)}`),
  // The project is the approval context; Skills can target the shared resource library.
  previewMarketInstall: (catalogId: string, projectId?: string | null, scope?: 'shared' | 'project') => request<MarketInstallProposalDto>(`/api/v1/market/catalog/${encodeURIComponent(catalogId)}/install-preview`, {
    method: 'POST',
    body: JSON.stringify({ ...(projectId ? { project_id: projectId } : {}), ...(scope ? { scope } : {}) })
  }),
  previewMarketUninstall: (installationId: string) => request<MarketInstallProposalDto>(`/api/v1/market/installations/${encodeURIComponent(installationId)}/uninstall-preview`, {
    method: 'POST'
  }),
  // Queues the governed write and nothing more. The bytes land when a human approves the user tool
  // action this returns, on the approval surface — never here.
  applyMarketInstallProposal: (proposalId: string) => request<MarketInstallationDto>(`/api/v1/market/install-proposals/${encodeURIComponent(proposalId)}/apply`, {
    method: 'POST'
  }),
  listMarketInstallations: () => request<MarketInstallationListDto>('/api/v1/market/installations'),
  listMcpServers: () => request<McpInventoryDto>('/api/v1/mcp/servers'),
  listMcpServerTools: (serverId: string) =>
    request<McpServerToolsDto>(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/tools`),
  listAcpAdapters: (options?: { signal?: AbortSignal }) => request<AcpAdapterDto[]>('/api/v1/acp/adapters', options),
  listAgentModes: (options?: { signal?: AbortSignal }) => request<AgentModeDto[]>('/api/v1/agent-modes', options),
  listAgents: (options?: { signal?: AbortSignal }) => request<AgentDirectoryItemDto[]>('/api/v1/agents', options),
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
  updateAgentDraft: (id: string, body: Partial<AgentDefinitionDto>, etag?: string | null) => request<AgentDefinitionDto>(`/api/v1/agents/${encodeURIComponent(id)}/draft`, { method: 'PUT', headers: etag ? { 'if-match': etag } : {}, body: JSON.stringify(body) }),
  publishAgent: (id: string, etag?: string | null) => request<{ id: string; version: number; revision: number; snapshot: AgentDefinitionDto }>(`/api/v1/agents/${encodeURIComponent(id)}/publish`, { method: 'POST', headers: etag ? { 'if-match': etag } : {} }),
  // 用户级运行时绑定（配置体验改造 A）：pack 管理的智能体也可写，绕开 draft/publish。
  putAgentRuntimeBinding: (id: string, body: { mode: 'inherit' | 'route' | 'fixed'; provider_instance_id?: string | null; model?: string | null; route_purpose?: string | null; tool_scope?: string[] | null }) => request<Record<string, unknown>>(`/api/v1/agents/${encodeURIComponent(id)}/runtime-binding`, { method: 'PUT', body: JSON.stringify(body) }),
  archiveAgent: (id: string) => request<AgentDefinitionDto>(`/api/v1/agents/${encodeURIComponent(id)}/archive`, { method: 'POST' }),
  getWorkspaceDefaults: (signal?: AbortSignal) => request<WorkspaceDefaultsDto>('/api/v1/workspace-defaults', { signal }),
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
  listAgentModeTopologies: () => request<AgentModeTopologyDto[]>('/api/v1/agent-modes'),
  createAgentModeDraft: (body: Omit<Partial<AgentModeTopologyDto>, 'nodes' | 'edges'> & Partial<AgentModeTopologyWriteDto>) => request<AgentModeTopologyDto>('/api/v1/agent-modes', { method: 'POST', body: JSON.stringify(body) }),
  getAgentModeTopology: (id: string) => request<AgentModeTopologyDto>(`/api/v1/agent-modes/${encodeURIComponent(id)}`),
  updateAgentModeDraft: (id: string, body: AgentModeTopologyWriteDto, etag?: string | null) => request<AgentModeTopologyDto>(`/api/v1/agent-modes/${encodeURIComponent(id)}/draft`, { method: 'PUT', headers: etag ? { 'if-match': etag } : {}, body: JSON.stringify(body) }),
  publishAgentMode: (id: string) => request<AgentModeTopologyDto>(`/api/v1/agent-modes/${encodeURIComponent(id)}/publish`, { method: 'POST' }),
  archiveAgentMode: (id: string) => request<AgentModeTopologyDto>(`/api/v1/agent-modes/${encodeURIComponent(id)}/archive`, { method: 'POST' }),
  listAgentModeVersions: (id: string) => request<ModeVersionDto[]>(`/api/v1/agent-modes/${encodeURIComponent(id)}/versions`),
  // prompt pipelines
  listPromptPipelines: (options?: { signal?: AbortSignal }) => request<PromptPipelineDto[]>('/api/v1/prompt-pipelines', options),
  createPromptPipelineDraft: (body: Partial<PromptPipelineDto>) => request<PromptPipelineDto>('/api/v1/prompt-pipelines', { method: 'POST', body: JSON.stringify(body) }),
  getPromptPipeline: (id: string) => request<PromptPipelineDto>(`/api/v1/prompt-pipelines/${encodeURIComponent(id)}`),
  updatePromptPipelineDraft: (id: string, body: Partial<PromptPipelineDto>, etag?: string | null) => request<PromptPipelineDto>(`/api/v1/prompt-pipelines/${encodeURIComponent(id)}/draft`, { method: 'PUT', headers: etag ? { 'if-match': etag } : {}, body: JSON.stringify(body) }),
  publishPromptPipeline: (id: string) => request<PromptPipelineDto>(`/api/v1/prompt-pipelines/${encodeURIComponent(id)}/publish`, { method: 'POST' }),
  archivePromptPipeline: (id: string) => request<PromptPipelineDto>(`/api/v1/prompt-pipelines/${encodeURIComponent(id)}/archive`, { method: 'POST' }),
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
  // interactions (queued/insert/parallel)
  createInteraction: (sessionId: string, body: { content: string; client_message_id: string; mode_version_id?: string | null; permission_mode?: string | null; dispatch_mode: DispatchMode; target_run_id?: string | null; meeting_model_override?: MeetingModelOverrideDto | null; clear_meeting_model_override?: boolean; expected_settings_revision?: number; space_options?: SpaceOptionsDto | null; attachment_ids?: string[]; interrupt?: boolean }) => request<SessionInteractionDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/interactions`, { method: 'POST', body: JSON.stringify(body) }),
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
  ) => {
    const search = new URLSearchParams({ filename: fileName });
    if (mediaType) search.set('media_type', mediaType);
    return request<MessageAttachmentDto>(
      `/api/v1/sessions/${encodeURIComponent(sessionId)}/attachments?${search.toString()}`,
      { method: 'POST', body: bytes as BodyInit, headers: { 'content-type': 'application/octet-stream' } },
    );
  },
  listAttachments: (sessionId: string) =>
    request<MessageAttachmentDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/attachments`),
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
  /** Current v1 user tool transport. Gateway forwards this request to the Tool Provider. */
  executeCodeTool: (toolId: string, payload: CodeToolExecuteRequestDto = {}, options?: { signal?: AbortSignal }) => request<CodeToolExecuteResultDto>(`/api/v1/code/tools/${encodeURIComponent(toolId)}/execute`, {
    signal: options?.signal,
    method: 'POST',
    body: JSON.stringify(payload)
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
  listAgentCandidates: (options?: { signal?: AbortSignal }) => request<AgentCandidateDto[]>('/api/v1/agent-candidates', options),

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

  connectEvents(sessionId: string | null, onEvent: (event: EventEnvelope) => void): EventSource {
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
};

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
