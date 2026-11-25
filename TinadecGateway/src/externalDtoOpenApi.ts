import { t } from 'elysia';

/**
 * Documentation-only OpenAPI schemas for the Gateway external DTOs consumed by
 * the generated frontend client (projects, sessions, messages, runs, runs
 * orchestration/task-nodes/supervision-findings/context-versions, health).
 * These mirror the explicit CoreDto -> ExternalDto mappers in src/mappers and
 * are attached via `detail.responses` only — Gateway stays a runtime
 * passthrough proxy, so no Elysia `response:` validation is installed here.
 */

const componentRef = (name: string) => t.Unsafe({ $ref: `#/components/schemas/${name}` });
const nullableString = () => t.Unsafe({ type: 'string', nullable: true });
const nullableInteger = () => t.Unsafe({ type: 'integer', nullable: true });
const jsonObject = () => t.Unsafe({ type: 'object', additionalProperties: true });
const nullableJsonObject = () => t.Unsafe({ type: 'object', additionalProperties: true, nullable: true });
const lifecycleStatus = () => t.Unsafe({ type: 'string', enum: ['active', 'archived', 'trashed'] });

const recoveryProperties = {
  category: t.Optional(componentRef('ErrorCategory')),
  retryable: t.Optional(t.Boolean()),
  actions: t.Optional(t.Array(componentRef('ErrorRecoveryAction'))),
};

const project = t.Object({
  ...recoveryProperties,
  roots: t.Optional(t.Array(t.Object({ id: t.String(), path: t.String() }))),
  primary_root_id: t.Optional(t.String()),
  icon: t.Optional(t.String()), color: t.Optional(t.String()),
  configuration_hash: t.Optional(t.String()), storage_root: t.Optional(t.String()),
  external: t.Optional(t.Boolean()), availability: t.Optional(t.Unsafe({ type: 'string', enum: ['ready', 'error'] })), availability_error: t.Optional(t.String()),
  availability_code: t.Optional(t.String()), trace_id: t.Optional(t.String()),
  diagnostics: t.Optional(t.Array(t.Object({ code: t.String(), message: t.String(), severity: t.String(),
    line: t.Optional(t.Unsafe({ type: 'integer', minimum: 1 })), column: t.Optional(t.Unsafe({ type: 'integer', minimum: 1 })),
  }, { additionalProperties: false }))),
  storage_id: t.Optional(t.String()),
  id: t.String({ format: 'uuid' }),
  name: t.String(),
  path: t.String(),
  kind: nullableString(),
  created_at: nullableString(),
  updated_at: nullableString(),
  lifecycle_status: t.Unsafe({ type: 'string', enum: ['active', 'archived', 'trashed'], nullable: true, description: 'Null when the workspace cannot be read and its lifecycle is unknown.' }),
  trashed_at: nullableString(),
}, { additionalProperties: true });

const nullableRef = (name: string) => t.Unsafe({ anyOf: [componentRef(name), { type: 'null' }] });

const meetingModelOverride = t.Object({
  provider_instance_id: t.String(),
  model: nullableString(),
}, { additionalProperties: true });

const contextPolicyParamsOverride = t.Object({
  recall_fidelity: t.Optional(t.Unsafe({ type: 'string', enum: ['summary_only', 'balanced', 'verbatim_first'], nullable: true })),
  compaction_trigger_ratio: t.Optional(t.Unsafe({ type: 'number', nullable: true })),
  recent_message_limit: t.Optional(nullableInteger()),
  allow_cross_session_recall: t.Optional(t.Unsafe({ type: 'boolean', nullable: true })),
  evolution_mode: t.Optional(t.Unsafe({ type: 'string', enum: ['auto_evolve', 'candidate_only'], nullable: true })),
  core_markdown_max_tokens: t.Optional(nullableInteger()),
}, { additionalProperties: false });

const spaceOptions = t.Object({
  plan_first: t.Boolean(),
  spec_enabled: t.Boolean(),
  multi_agent: t.Boolean(),
  workflow_mode_version_id: nullableString(),
  bulletin_board: t.Boolean(),
  worktree: t.Boolean(),
  context_strategy: t.Optional(t.Unsafe({ type: 'string', enum: ['direct', 'managed', 'deep_curator'], nullable: true })),
  context_advanced: t.Optional(nullableRef('ContextPolicyParamsOverride')),
}, { additionalProperties: false });

const sessionPreferenceProperties = {
  mode_version_id: t.Optional(nullableString()),
  meeting_model_override: t.Optional(nullableRef('MeetingModelOverride')),
  permission_mode: t.Optional(nullableString()),
  space_options: t.Optional(nullableRef('SpaceOptions')),
};
const createSessionRequest = t.Object({
  ...sessionPreferenceProperties,
  project_id: t.Optional(nullableString()), title: t.Optional(nullableString()),
  view_mode: t.Optional(t.Union([t.Literal('flat'), t.Literal('space')])),
  conversation_node_key: t.Optional(nullableString()),
  origin: t.Optional(nullableString()),
  board_task_id: t.Optional(t.Unsafe({ type: 'string', format: 'uuid', nullable: true })),
}, { additionalProperties: false });
const updateSessionRequest = t.Object({
  ...sessionPreferenceProperties,
  title: t.Optional(nullableString()),
  clear_mode_version: t.Optional(t.Boolean()),
  clear_meeting_model_override: t.Optional(t.Boolean()),
  expected_settings_revision: t.Optional(t.Integer()),
}, { additionalProperties: false });
const sessionInteractionRequest = t.Object({
  ...sessionPreferenceProperties,
  content: t.Optional(t.String()), client_message_id: t.String(),
  dispatch_mode: t.Union([t.Literal('queued'), t.Literal('parallel'), t.Literal('insert')]),
  target_run_id: t.Optional(nullableString()),
  expected_context_revision: t.Optional(t.Integer()),
  expected_settings_revision: t.Optional(t.Integer()),
  clear_meeting_model_override: t.Optional(t.Boolean()),
  attachment_ids: t.Optional(t.Array(t.String())), interrupt: t.Optional(t.Boolean()),
  // Frozen onto the run so the response language follows the UI that submitted
  // it; Core normalizes and falls back, the Gateway only forwards it.
  interface_locale: t.Optional(t.String()),
}, { additionalProperties: false });

const session = t.Object({
  storage_id: t.Optional(t.String()),
  id: t.String({ format: 'uuid' }),
  // Null for a free-conversation session created without project_id.
  project_id: t.Unsafe({ type: 'string', format: 'uuid', nullable: true }),
  title: nullableString(),
  status: nullableString(),
  mode: nullableString(),
  mode_version_id: nullableString(),
  view_mode: t.Union([t.Literal('flat'), t.Literal('space')]),
  permission_mode: t.String(),
  space_options: nullableRef('SpaceOptions'),
  settings_revision: t.Integer(),
  meeting_model_override: nullableRef('MeetingModelOverride'),
  origin: t.Optional(nullableString()),
  board_task_id: t.Optional(t.Unsafe({ type: 'string', format: 'uuid', nullable: true })),
  summary: nullableString(),
  history_revision: nullableInteger(),
  created_at: nullableString(),
  updated_at: nullableString(),
  lifecycle_status: lifecycleStatus(),
  trashed_at: nullableString(),
}, { additionalProperties: true });

const message = t.Object({
  id: t.String(),
  session_id: t.String(),
  run_id: nullableString(),
  role: t.String(),
  content: t.String(),
  created_at: nullableString(),
  // Files the user sent with this message. An empty array means "none", never
  // "unknown": Core groups the whole page from one listing, so the field is
  // always present.
  attachments: t.Array(componentRef('MessageAttachmentSummary')),
}, { additionalProperties: true });

const run = t.Object({
  id: t.String(),
  session_id: t.String(),
  trigger_message_id: nullableString(),
  status: t.String({ description: 'Ten-state durable run status (planning..cancelled).' }),
  summary: nullableString(),
  task_revision: nullableInteger(),
  latest_event_sequence: nullableInteger(),
  latest_event_at: nullableString(),
  created_at: nullableString(),
  updated_at: nullableString(),
  completed_at: nullableString(),
}, { additionalProperties: true });

const taskNode = t.Object({
  id: t.String(),
  graph_id: nullableString(),
  run_id: t.String(),
  session_id: t.String(),
  title: t.String(),
  description: t.String(),
  status: t.String(),
  lane_key: t.String(),
  priority: t.Unsafe({ type: 'integer' }),
  risk: t.String(),
  success_criteria: t.Array(t.String()),
  dependencies: t.Array(t.String()),
  required_capabilities: t.Array(t.String()),
  created_at: nullableString(),
  updated_at: nullableString(),
}, { additionalProperties: true });

const laneWait = t.Object({
  waiting_task: t.String(),
  lane: t.String(),
  predicate: t.String(),
  required_criteria: t.Array(t.String()),
  facts_hash: nullableString(),
}, { additionalProperties: true });

const lane = t.Object({
  lane_key: t.String(),
  status: t.String(),
  escalated: t.Unsafe({ type: 'boolean' }),
  task_keys: t.Array(t.String()),
  waits: t.Array(componentRef('LaneWait')),
}, { additionalProperties: true });

const supervisionFinding = t.Object({
  id: t.String(),
  run_id: t.String(),
  session_id: t.String(),
  severity: t.String(),
  category: t.String(),
  summary: t.String(),
  recommendation: t.String(),
  status: t.String(),
  created_at: nullableString(),
}, { additionalProperties: true });

const contextVersion = t.Object({
  id: t.String(),
  session_id: t.String(),
  run_id: nullableString(),
  revision: t.Unsafe({ type: 'integer', minimum: 0 }),
  kind: t.String(),
  status: t.String(),
  base_revision: nullableInteger(),
  created_at: nullableString(),
}, { additionalProperties: true });

const assignment = t.Object({
  id: t.String(),
  run_id: t.String(),
  task_node_id: t.String(),
  agent_id: t.String(),
  agent_name: t.String(),
  agent_layer: t.Unsafe({ type: 'string', enum: ['operation', 'execution'] }),
  status: t.String(),
}, { additionalProperties: true });

const orchestrationSnapshot = t.Object({
  run: nullableRef('Run'),
  graph: nullableJsonObject(),
  flows: t.Array(t.Unknown()),
  nodes: t.Array(t.Unknown()),
  lanes: t.Array(componentRef('Lane')),
  assignments: t.Array(componentRef('Assignment')),
  step_results: t.Array(t.Unknown()),
  context_packs: t.Array(t.Unknown()),
  supervision_findings: t.Array(componentRef('SupervisionFinding')),
  agent_instances: t.Array(t.Unknown()),
  frozen: t.Unsafe({ anyOf: [jsonObject(), { type: 'null' }] }),
}, { additionalProperties: true });

const health = t.Object({
  gateway: t.Unsafe({ type: 'string', enum: ['ok'] }),
  core_status: t.Unsafe({ type: 'string', enum: ['ready', 'unreachable'] }),
  mode: t.String(),
  core_url: t.String(),
  tool_runtime_url: t.String(),
}, { additionalProperties: true, description: 'Gateway health fingerprint with forwarded Core health fields.' });

/**
 * Mirrors Core's MessageAttachmentDto. No content path: Core keeps the storage
 * reference to itself, and the Gateway has no reason to learn it.
 */
const attachment = t.Object({
  id: t.String(),
  session_id: t.String(),
  message_id: nullableString(),
  file_name: t.String(),
  media_type: t.String(),
  content_hash: t.String(),
  content_length: t.Unsafe({ type: 'integer' }),
  origin: t.String(),
  project_id: nullableString(),
  source_path: nullableString(),
  delivered_to_model: t.Unsafe({ type: 'boolean' }),
  delivered_run_id: nullableString(),
  delivered_at: nullableString(),
  created_at: nullableString(),
  bound_at: nullableString(),
}, { additionalProperties: true });

/**
 * The narrower shape Core projects inside a Message. session_id and message_id
 * are absent because the parent row already carries both — restating them per
 * attachment would only create a second place where the two can disagree.
 * content_reference never leaves Core at any level.
 */
const messageAttachmentSummary = t.Object({
  id: t.String(),
  file_name: t.String(),
  media_type: t.String(),
  content_hash: t.String(),
  content_length: t.Unsafe({ type: 'integer' }),
  origin: t.String(),
  project_id: nullableString(),
  source_path: nullableString(),
  delivered_to_model: t.Unsafe({ type: 'boolean' }),
  delivered_run_id: nullableString(),
  delivered_at: nullableString(),
  created_at: nullableString(),
  bound_at: nullableString(),
}, { additionalProperties: true });

const preAuthorization = t.Object({
  id: t.String({ format: 'uuid' }),
  run_id: t.String({ format: 'uuid' }),
  lane_key: nullableString(),
  tool_scope: t.Array(t.String()),
  parameter_constraint_hash: nullableString(),
  risk_max: t.String(),
  max_uses: t.Unsafe({ type: 'integer' }),
  use_count: t.Unsafe({ type: 'integer' }),
  expires_at: nullableString(),
  revoked: t.Unsafe({ type: 'boolean' }),
}, { additionalProperties: true });

const approvalPolicySummary = t.Object({
  policy_version: t.String(),
  enabled: t.Boolean(),
  risk_max: t.String(),
  delegated_risk_max: t.String(),
  max_per_run: t.Unsafe({ type: 'integer' }),
  release_read_only_in_ask_mode: t.Boolean(),
  human_only_tools: t.Array(t.String()),
  delegation_blocked_tools: t.Array(t.String()),
}, { additionalProperties: true });

const approvalRememberRuleInput = t.Object({
  kind: t.Unsafe({ type: 'string', enum: ['command_prefix', 'delegate_tool'] }),
  pattern: t.Optional(nullableString()),
  scope: t.Optional(t.Unsafe({ type: 'string', enum: ['session', 'workspace'], nullable: true })),
}, { additionalProperties: false });

const approvalDecisionRequest = t.Object({
  decision: t.Unsafe({ type: 'string', enum: ['approved', 'rejected'] }),
  reason: t.Optional(nullableString()),
  scope: t.Optional(t.Unsafe({ type: 'string', enum: ['once', 'run'], nullable: true })),
  remember_rule: t.Optional(nullableRef('ApprovalRememberRuleInput')),
}, { additionalProperties: false });

const approvalDecisionResponse = t.Object({
  id: t.String({ format: 'uuid' }),
  status: t.String(),
  decided_at: t.Optional(nullableString()),
  scope: t.Optional(t.Unsafe({ type: 'string', enum: ['once', 'run'], nullable: true })),
  run_scope_released: t.Optional(nullableInteger()),
  pre_authorization_id: t.Optional(nullableString()),
  capability_grant_id: t.Optional(nullableString()),
  created_rule_id: t.Optional(nullableString()),
  decision_reason: t.Optional(nullableString()),
  user_tool_action_id: t.Optional(nullableString()),
  action: t.Optional(nullableJsonObject()),
}, { additionalProperties: true });

const batchApprovalDecisionRequest = t.Object({
  approval_ids: t.Array(t.String({ format: 'uuid' }), { minItems: 1, maxItems: 50 }),
  decision: t.Unsafe({ type: 'string', enum: ['approved', 'rejected'] }),
  reason: t.Optional(nullableString()),
  scope: t.Optional(t.Unsafe({ type: 'string', enum: ['once', 'run'], nullable: true })),
}, { additionalProperties: false });

const batchApprovalDecisionItem = t.Object({
  id: t.String({ format: 'uuid' }),
  ok: t.Boolean(),
  status: t.Optional(nullableString()),
  error_code: t.Optional(nullableString()),
  error: t.Optional(nullableString()),
}, { additionalProperties: true });

const batchApprovalDecisionResponse = t.Object({
  results: t.Array(componentRef('BatchApprovalDecisionItem')),
}, { additionalProperties: true });

/**
 * Core's ModelInvocationPageDto, deliberately shallow. Core drops null keys when it
 * writes, so a field-by-field mirror would declare `input_tokens: number | null` for
 * rows where the key is simply absent — and "absent" is the one thing this page needs
 * to say: it means the provider reported no usage, not that the call cost nothing.
 */
const modelInvocationItem = t.Object({
  id: t.String({ format: 'uuid' }),
  call_id: t.String({ format: 'uuid' }),
  attempt: t.Integer({ minimum: 0 }),
  session_id: t.String({ format: 'uuid' }),
  run_id: t.String({ format: 'uuid' }),
  turn_id: t.Optional(t.String({ format: 'uuid' })),
  agent_instance_id: t.Optional(t.String({ format: 'uuid' })),
  agent_definition_id: t.String({ format: 'uuid' }),
  agent_version_id: t.String({ format: 'uuid' }),
  mode_version_id: t.String({ format: 'uuid' }),
  strategy_source: t.String(),
  route_id: t.Optional(t.String({ format: 'uuid' })),
  route_version_id: t.Optional(t.String({ format: 'uuid' })),
  provider_instance_id: t.String({ format: 'uuid' }),
  provider_version_id: t.String({ format: 'uuid' }),
  model: t.Optional(t.String()),
  protocol: t.String(),
  fallback_position: t.Integer({ minimum: 0 }),
  status: t.String(),
  error_category: t.Optional(t.String()),
  safe_error_message: t.Optional(t.String()),
  input_tokens: t.Optional(t.Number()),
  output_tokens: t.Optional(t.Number()),
  total_tokens: t.Optional(t.Number()),
  started_at: t.String({ format: 'date-time' }),
  completed_at: t.Optional(t.String({ format: 'date-time' })),
}, { additionalProperties: true });

const modelInvocationPage = t.Object({
  items: t.Array(modelInvocationItem),
  next_cursor: t.Optional(t.String()),
}, { additionalProperties: true });

const lifecycleHookEntry = t.Object({
  id: t.String(),
  name: t.Optional(t.String()),
  event: t.String(),
  matcher: t.Optional(t.String()),
  command: t.String(),
  timeout_ms: t.Integer({ minimum: 100 }),
  blocking: t.Boolean(),
  enabled: t.Boolean(),
}, { additionalProperties: true });

const lifecycleHooksDocument = t.Object({
  scope: t.String(),
  workspace_root: t.Optional(t.String()),
  hooks: t.Array(lifecycleHookEntry),
  revision: t.Integer({ minimum: 0 }),
}, { additionalProperties: true });

const lifecycleHooksWriteRequest = t.Object({
  scope: t.Optional(t.String()),
  workspace_root: t.Optional(t.String()),
  hooks: t.Array(lifecycleHookEntry),
  expected_revision: t.Optional(t.Integer({ minimum: 0 })),
}, { additionalProperties: true });

/**
 * The MCP inventory envelope. Typed at the envelope level for one reason: `source` is the field
 * that separates "no MCP server is configured" from "Core could not ask the Tool Provider", and
 * those two used to arrive as the same empty array. Per-server and per-tool rows stay opaque
 * because the gateway forwards them verbatim — mirroring them here would put a second copy of
 * Core's DTOs in a layer that cannot enforce either one.
 */
const mcpInventory = t.Object({
  source: t.String(),
  reason: t.Optional(t.String()),
  workspace_root: t.Optional(t.String()),
  config_path: t.Optional(t.String()),
  dropped_rows: t.Optional(t.Number()),
  servers: t.Array(t.Unknown()),
}, { additionalProperties: true });

const mcpServerTools = t.Object({
  source: t.String(),
  reason: t.Optional(t.String()),
  workspace_root: t.Optional(t.String()),
  config_path: t.Optional(t.String()),
  server_id: t.String(),
  server: t.Optional(t.Unknown()),
}, { additionalProperties: true });

const sessionPlanArtifact = t.Object({
  kind: t.Union([t.Literal('file'), t.Literal('worktree'), t.Literal('summary'), t.Literal('attachment')]),
  label: t.String(),
  path: t.Optional(nullableString()),
  ref_id: t.Optional(nullableString()),
}, { additionalProperties: true });

const sessionPlanStep = t.Object({
  id: t.String(),
  title: t.String(),
  description: t.Optional(nullableString()),
  status: t.Union([t.Literal('pending'), t.Literal('in_progress'), t.Literal('completed'), t.Literal('blocked'), t.Literal('failed'), t.Literal('skipped')]),
  depends_on: t.Array(t.String()),
  bound_task_key: t.Optional(nullableString()),
  assigned_agent_slug: t.Optional(nullableString()),
  verification_criteria: t.Array(t.String()),
  artifacts: t.Array(componentRef('SessionPlanArtifact')),
  summary: t.Optional(nullableString()),
  updated_at: t.Optional(nullableString()),
}, { additionalProperties: true });

const sessionPlanPhase = t.Object({
  id: t.String(),
  title: t.String(),
  description: t.Optional(nullableString()),
  order: t.Integer(),
  status: t.Union([t.Literal('pending'), t.Literal('in_progress'), t.Literal('completed'), t.Literal('blocked'), t.Literal('failed'), t.Literal('skipped')]),
  steps: t.Array(componentRef('SessionPlanStep')),
}, { additionalProperties: true });

const sessionPlanProgress = t.Object({
  total_steps: t.Integer(),
  completed_steps: t.Integer(),
  in_progress_steps: t.Integer(),
  blocked_steps: t.Integer(),
  failed_steps: t.Integer(),
  pending_steps: t.Integer(),
}, { additionalProperties: true });

const sessionPlan = t.Object({
  id: t.String(),
  session_id: t.String(),
  title: t.String(),
  goal: t.Optional(nullableString()),
  status: t.Union([t.Literal('draft'), t.Literal('approved'), t.Literal('active'), t.Literal('completed'), t.Literal('blocked')]),
  revision: t.Integer(),
  approved_at: t.Optional(nullableString()),
  last_run_id: t.Optional(nullableString()),
  updated_at: t.String(),
  phases: t.Array(componentRef('SessionPlanPhase')),
  progress: componentRef('SessionPlanProgress'),
}, { additionalProperties: true });

/**
 * The bounded result preview of one finished tool call. `preview.kind` picks which members are
 * present; `preview` is null while the call is still running and when a finished call stored no
 * result. `arguments` is Core's approval-evidence projection, never the stored parameters.
 */
const toolPreviewLine = t.Object({
  path: t.Optional(t.String()),
  line_number: t.Optional(t.Integer()),
  text: t.String(),
  is_match: t.Optional(t.Boolean()),
});

const toolPreviewEntry = t.Object({
  name: t.String(),
  type: t.String(),
  size: t.Optional(t.Integer()),
});

const toolResultPreview = t.Object({
  kind: t.Unsafe({ type: 'string', enum: ['file_lines', 'search_matches', 'directory', 'command_output', 'text', 'json', 'withheld'] }),
  path: t.Optional(t.String()),
  lines: t.Optional(t.Array(componentRef('ToolPreviewLine'))),
  entries: t.Optional(t.Array(componentRef('ToolPreviewEntry'))),
  total_count: t.Optional(t.Integer()),
  stdout: t.Optional(t.String()),
  stderr: t.Optional(t.String()),
  exit_code: t.Optional(t.Integer()),
  timed_out: t.Optional(t.Boolean()),
  text: t.Optional(t.String()),
  language: t.Optional(t.String()),
  value: t.Optional(t.Unknown()),
  reason: t.Optional(t.String()),
});

const toolExecutionPreview = t.Object({
  execution_id: t.String(),
  run_id: t.String(),
  task_id: t.Optional(nullableString()),
  tool_id: t.String(),
  status: t.String(),
  tool_success: t.Optional(t.Unsafe({ type: 'boolean', nullable: true })),
  error_category: t.Optional(nullableString()),
  safe_error_message: t.Optional(nullableString()),
  arguments: nullableJsonObject(),
  result_length: t.Optional(nullableInteger()),
  preview: nullableRef('ToolResultPreview'),
  truncated: t.Boolean(),
});

export const UI_PREFERENCES_THEMES = ['dark', 'light', 'system'] as const;
export const UI_PREFERENCES_ACCENTS = [
  'monochrome',
  'warm',
  'dynamic',
  'custom',
  'blue',
  'green',
  'orange',
  'purple',
  'pink',
  'red',
  'cyan',
  'yellow',
] as const;
export const UI_PREFERENCES_PANEL_EFFECTS = ['solid', 'translucent', 'blur'] as const;
export const UI_PREFERENCES_PANEL_EFFECT_INPUTS = ['solid', 'translucent', 'blur', 'opaque'] as const;
export const UI_PREFERENCES_PANEL_PRESETS = [
  'solid',
  'frosted',
  'crystal',
  'mica',
  'translucent',
  'custom',
] as const;
export const UI_PREFERENCES_WORKSPACE_VIEWS = ['grid', 'list'] as const;
export const UI_PREFERENCES_WORKSPACE_FOLDER_TONES = ['amber', 'teal', 'sky', 'coral', 'frost'] as const;
export const UI_PREFERENCES_WORKSPACE_DROP_ACTIONS = ['link', 'copy', 'ask'] as const;
export const UI_PREFERENCES_SPATIAL_PROMOTE_MODES = ['merge', 'split', 'parent_children'] as const;
export const UI_PREFERENCES_SPATIAL_PROMOTE_MODE_INPUTS = [
  'merge',
  'split',
  'parent_children',
  'merge_single',
  'split_many',
  'parent_with_children',
] as const;
export const UI_PREFERENCES_REDUCED_MOTIONS = ['system', 'on', 'off'] as const;

const uiAppearancePreferencesDto = t.Object({
  theme: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_THEMES] }),
  accent: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_ACCENTS] }),
  custom_accent_hex: t.String({ pattern: '^#[0-9a-fA-F]{6}$' }),
  panel_effect: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_PANEL_EFFECTS] }),
  panel_preset: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_PANEL_PRESETS] })),
  panel_opacity: t.Optional(t.Integer({ minimum: 0, maximum: 100 })),
  panel_blur: t.Optional(t.Integer({ minimum: 0, maximum: 32 })),
  panel_saturation: t.Optional(t.Integer({ minimum: 100, maximum: 200 })),
  workspace_view: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_WORKSPACE_VIEWS] }),
  workspace_folder_tone: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_WORKSPACE_FOLDER_TONES] }),
}, { additionalProperties: false });

const uiInteractionPreferencesDto = t.Object({
  workspace_drop_action: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_WORKSPACE_DROP_ACTIONS] }),
  spatial_promote_mode: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_SPATIAL_PROMOTE_MODES] }),
  reduced_motion: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_REDUCED_MOTIONS] }),
}, { additionalProperties: false });

const uiPreferencesDocumentDto = t.Object({
  version: t.Unsafe({ type: 'integer', minimum: 1 }),
  file_path: t.String(),
  content_hash: t.String(),
  updated_at: t.String({ format: 'date-time' }),
  appearance: componentRef('UiAppearancePreferencesDto'),
  interaction: componentRef('UiInteractionPreferencesDto'),
}, { additionalProperties: false });

const uiAppearancePreferencesInputDto = t.Object({
  theme: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_THEMES] }),
  accent: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_ACCENTS] }),
  custom_accent_hex: t.String({ pattern: '^#[0-9a-fA-F]{6}$' }),
  panel_effect: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_PANEL_EFFECT_INPUTS] }),
  panel_preset: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_PANEL_PRESETS], nullable: true })),
  panel_opacity: t.Optional(t.Unsafe({ type: 'integer', minimum: 0, maximum: 100, nullable: true })),
  panel_blur: t.Optional(t.Unsafe({ type: 'integer', minimum: 0, maximum: 32, nullable: true })),
  panel_saturation: t.Optional(t.Unsafe({ type: 'integer', minimum: 100, maximum: 200, nullable: true })),
  workspace_view: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_WORKSPACE_VIEWS] }),
  workspace_folder_tone: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_WORKSPACE_FOLDER_TONES] }),
}, { additionalProperties: false });

const uiInteractionPreferencesInputDto = t.Object({
  workspace_drop_action: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_WORKSPACE_DROP_ACTIONS] }),
  spatial_promote_mode: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_SPATIAL_PROMOTE_MODE_INPUTS] }),
  reduced_motion: t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_REDUCED_MOTIONS] }),
}, { additionalProperties: false });

const uiPreferencesPutRequestDto = t.Object({
  expected_content_hash: t.Optional(nullableString()),
  reset_defaults: t.Optional(t.Boolean()),
  appearance: t.Optional(nullableRef('UiAppearancePreferencesInputDto')),
  interaction: t.Optional(nullableRef('UiInteractionPreferencesInputDto')),
}, { additionalProperties: false });

const uiAppearancePreferencesPatchDto = t.Object({
  theme: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_THEMES], nullable: true })),
  accent: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_ACCENTS], nullable: true })),
  custom_accent_hex: t.Optional(t.Unsafe({ type: 'string', pattern: '^#[0-9a-fA-F]{6}$', nullable: true })),
  panel_effect: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_PANEL_EFFECT_INPUTS], nullable: true })),
  panel_preset: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_PANEL_PRESETS], nullable: true })),
  panel_opacity: t.Optional(t.Unsafe({ type: 'integer', minimum: 0, maximum: 100, nullable: true })),
  panel_blur: t.Optional(t.Unsafe({ type: 'integer', minimum: 0, maximum: 32, nullable: true })),
  panel_saturation: t.Optional(t.Unsafe({ type: 'integer', minimum: 100, maximum: 200, nullable: true })),
  workspace_view: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_WORKSPACE_VIEWS], nullable: true })),
  workspace_folder_tone: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_WORKSPACE_FOLDER_TONES], nullable: true })),
}, { additionalProperties: false });

const uiInteractionPreferencesPatchDto = t.Object({
  workspace_drop_action: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_WORKSPACE_DROP_ACTIONS], nullable: true })),
  spatial_promote_mode: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_SPATIAL_PROMOTE_MODE_INPUTS], nullable: true })),
  reduced_motion: t.Optional(t.Unsafe({ type: 'string', enum: [...UI_PREFERENCES_REDUCED_MOTIONS], nullable: true })),
}, { additionalProperties: false });

const uiPreferencesPatchRequestDto = t.Object({
  expected_content_hash: t.Optional(nullableString()),
  appearance: t.Optional(nullableRef('UiAppearancePreferencesPatchDto')),
  interaction: t.Optional(nullableRef('UiInteractionPreferencesPatchDto')),
}, { additionalProperties: false });

export const uiPreferencesOpenApiSchemas = {
  UiAppearancePreferencesDto: uiAppearancePreferencesDto,
  UiInteractionPreferencesDto: uiInteractionPreferencesDto,
  UiPreferencesDocumentDto: uiPreferencesDocumentDto,
  UiAppearancePreferencesInputDto: uiAppearancePreferencesInputDto,
  UiInteractionPreferencesInputDto: uiInteractionPreferencesInputDto,
  UiPreferencesPutRequestDto: uiPreferencesPutRequestDto,
  UiAppearancePreferencesPatchDto: uiAppearancePreferencesPatchDto,
  UiInteractionPreferencesPatchDto: uiInteractionPreferencesPatchDto,
  UiPreferencesPatchRequestDto: uiPreferencesPatchRequestDto,
};

export const externalDtoSchemas = {
  MeetingModelOverride: meetingModelOverride,
  ContextPolicyParamsOverride: contextPolicyParamsOverride,
  SpaceOptions: spaceOptions,
  CreateSessionRequest: createSessionRequest,
  UpdateSessionRequest: updateSessionRequest,
  SessionInteractionRequest: sessionInteractionRequest,
  Project: project,
  ProjectList: t.Array(componentRef('Project')),
  Session: session,
  SessionList: t.Array(componentRef('Session')),
  Message: message,
  MessageList: t.Array(componentRef('Message')),
  MessageAttachment: attachment,
  MessageAttachmentSummary: messageAttachmentSummary,
  MessageAttachmentList: t.Array(componentRef('MessageAttachment')),
  Run: run,
  RunList: t.Array(componentRef('Run')),
  TaskNode: taskNode,
  TaskNodeList: t.Array(componentRef('TaskNode')),
  LaneWait: laneWait,
  Lane: lane,
  SupervisionFinding: supervisionFinding,
  SupervisionFindingList: t.Array(componentRef('SupervisionFinding')),
  ContextVersion: contextVersion,
  ContextVersionList: t.Array(componentRef('ContextVersion')),
  Assignment: assignment,
  OrchestrationSnapshot: orchestrationSnapshot,
  ModelInvocationItem: modelInvocationItem,
  ModelInvocationPage: modelInvocationPage,
  LifecycleHookEntry: lifecycleHookEntry,
  LifecycleHooksDocument: lifecycleHooksDocument,
  LifecycleHooksWriteRequest: lifecycleHooksWriteRequest,
  McpInventory: mcpInventory,
  McpServerTools: mcpServerTools,
  SessionPlanArtifact: sessionPlanArtifact,
  SessionPlanStep: sessionPlanStep,
  SessionPlanPhase: sessionPlanPhase,
  SessionPlanProgress: sessionPlanProgress,
  SessionPlan: sessionPlan,
  Health: health,
  PreAuthorization: preAuthorization,
  ApprovalPolicySummary: approvalPolicySummary,
  ApprovalRememberRuleInput: approvalRememberRuleInput,
  ApprovalDecisionRequest: approvalDecisionRequest,
  ApprovalDecisionResponse: approvalDecisionResponse,
  BatchApprovalDecisionRequest: batchApprovalDecisionRequest,
  BatchApprovalDecisionItem: batchApprovalDecisionItem,
  BatchApprovalDecisionResponse: batchApprovalDecisionResponse,
  ToolPreviewLine: toolPreviewLine,
  ToolPreviewEntry: toolPreviewEntry,
  ToolResultPreview: toolResultPreview,
  ToolExecutionPreview: toolExecutionPreview,
  ...uiPreferencesOpenApiSchemas,
};

export function externalJsonResponse(schemaName: string, description: string) {
  return {
    description,
    content: {
      'application/json': { schema: { $ref: `#/components/schemas/${schemaName}` } },
    },
  } as const;
}

/** Documentation only. Core remains the validator and owner of each preference. */
export function externalJsonRequest(schemaName: string) {
  return { required: true, content: { 'application/json': { schema: { $ref: `#/components/schemas/${schemaName}` } } } } as const;
}
