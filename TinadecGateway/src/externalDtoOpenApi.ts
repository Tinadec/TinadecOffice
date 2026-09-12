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

const project = t.Object({
  id: t.String({ format: 'uuid' }),
  name: t.String(),
  path: t.String(),
  kind: nullableString(),
  created_at: nullableString(),
  updated_at: nullableString(),
  lifecycle_status: lifecycleStatus(),
  trashed_at: nullableString(),
}, { additionalProperties: true });

const nullableRef = (name: string) => t.Unsafe({ anyOf: [componentRef(name), { type: 'null' }] });

const meetingModelOverride = t.Object({
  provider_instance_id: t.String(),
  model: nullableString(),
}, { additionalProperties: true });

const session = t.Object({
  id: t.String({ format: 'uuid' }),
  // Null for a free-conversation session created without project_id.
  project_id: t.Unsafe({ type: 'string', format: 'uuid', nullable: true }),
  title: nullableString(),
  status: nullableString(),
  mode: nullableString(),
  mode_version_id: nullableString(),
  meeting_model_override: nullableRef('MeetingModelOverride'),
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

export const externalDtoSchemas = {
  MeetingModelOverride: meetingModelOverride,
  Project: project,
  ProjectList: t.Array(componentRef('Project')),
  Session: session,
  SessionList: t.Array(componentRef('Session')),
  Message: message,
  MessageList: t.Array(componentRef('Message')),
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
  Health: health,
  PreAuthorization: preAuthorization,
};

export function externalJsonResponse(schemaName: string, description: string) {
  return {
    description,
    content: {
      'application/json': { schema: { $ref: `#/components/schemas/${schemaName}` } },
    },
  } as const;
}
