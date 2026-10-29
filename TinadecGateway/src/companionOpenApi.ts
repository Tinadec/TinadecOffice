/**
 * Documentation-only schemas for the companion domain (spec: add-companion-mode). These mirror
 * Core's `Companion/CompanionContracts.cs` projections and are attached via `detail.responses`
 * only — the Gateway forwards Core's bytes verbatim and installs no Elysia `response:` validator,
 * so Core remains the single authority for every status, refusal code and field.
 *
 * Shapes were checked against `TinadecCore/Companion/CompanionContracts.cs` and the endpoint
 * handlers in `AspNetCore/Endpoints/Companion*Endpoints.cs`; optional members are explicit
 * `nullable` because Core serializes them with `DefaultIgnoreCondition = Never`.
 */

const ref = (name: string) => ({ $ref: `#/components/schemas/${name}` });
const nullableRef = (name: string) => ({ anyOf: [ref(name), { type: 'null' }] });
const nullableString = { type: 'string', nullable: true };
const nullableUuid = { type: 'string', format: 'uuid', nullable: true };
const nullableTimestamp = { type: 'string', format: 'date-time', nullable: true };
const timestamp = { type: 'string', format: 'date-time' };
const uuid = { type: 'string', format: 'uuid' };
const uuidArray = { type: 'array', items: uuid };

const companionIdentityProperties = {
  id: uuid,
  name: { type: 'string' },
  tagline: nullableString,
  avatar: nullableString,
  status: { type: 'string', enum: ['active', 'archived', 'paused'] },
  model_purpose: { type: 'string' },
  // The pinned provider model; null means "resolve the tenant's route purpose". `model` is the
  // model name on that provider instance (itself may be null for CLI runtimes that pick their own).
  model_provider_id: nullableUuid,
  model: nullableString,
  autonomy_enabled: { type: 'boolean' },
  autonomy_paused: { type: 'boolean' },
  revision: { type: 'integer' },
  created_at: timestamp,
  updated_at: timestamp,
  memory_count: { type: 'integer' },
  skill_count: { type: 'integer' },
  connection_count: { type: 'integer' },
  last_activity_at: nullableTimestamp,
  current_activity: { type: 'string', enum: ['idle', 'thinking', 'paused'] },
};

const companionAutonomy = {
  type: 'object',
  additionalProperties: true,
  properties: {
    enabled: { type: 'boolean' },
    paused: { type: 'boolean' },
    interval_minutes: { type: 'integer' },
    daily_action_budget: { type: 'integer' },
  },
  required: ['enabled', 'paused', 'interval_minutes', 'daily_action_budget'],
};

const companionSummary = {
  type: 'object',
  additionalProperties: true,
  properties: companionIdentityProperties,
  required: Object.keys(companionIdentityProperties),
};

const companionDetail = {
  type: 'object',
  additionalProperties: true,
  properties: {
    ...companionIdentityProperties,
    persona_document: nullableString,
    autonomy: ref('CompanionAutonomy'),
  },
  required: [...Object.keys(companionIdentityProperties), 'autonomy'],
};

const companionMemory = {
  type: 'object',
  additionalProperties: true,
  properties: {
    id: uuid,
    companion_id: uuid,
    content: { type: 'string' },
    source_kind: { type: 'string', description: 'user | companion_run | group_message' },
    source_conversation_id: nullableUuid,
    source_message_id: nullableUuid,
    index_status: { type: 'string', description: 'indexed | pending | deleted' },
    revision: { type: 'integer' },
    created_at: timestamp,
    updated_at: timestamp,
  },
  required: ['id', 'companion_id', 'content', 'source_kind', 'index_status', 'revision'],
};

const companionSkill = {
  type: 'object',
  additionalProperties: true,
  properties: {
    id: uuid,
    companion_id: uuid,
    name: { type: 'string' },
    source_kind: { type: 'string', description: 'manual | pack' },
    pack_id: nullableString,
    pack_version: nullableString,
    instructions: nullableString,
    enabled: { type: 'boolean' },
    last_loaded_run_id: nullableUuid,
    last_loaded_at: nullableTimestamp,
    revision: { type: 'integer' },
  },
  required: ['id', 'companion_id', 'name', 'source_kind', 'enabled', 'revision'],
};

const companionConnection = {
  type: 'object',
  additionalProperties: true,
  properties: {
    id: uuid,
    companion_id: uuid,
    kind: { type: 'string', description: 'mcp | server | phone | external_agent' },
    name: { type: 'string' },
    endpoint: nullableString,
    authorization: { type: 'string', enum: ['configured', 'authorized', 'revoked'] },
    // No real handshake exists in this release, so a binding must never read as "online".
    runtime_status: { type: 'string', enum: ['unsupported', 'unknown'] },
    enabled: { type: 'boolean' },
    last_checked_at: nullableTimestamp,
    revision: { type: 'integer' },
  },
  required: ['id', 'companion_id', 'kind', 'name', 'authorization', 'runtime_status', 'enabled', 'revision'],
};

const companionConnectionOverview = {
  type: 'object',
  additionalProperties: true,
  properties: {
    ...companionConnection.properties,
    companion_name: nullableString,
  },
  required: [...companionConnection.required, 'companion_name'],
};

const companionMember = {
  type: 'object',
  additionalProperties: true,
  properties: {
    member_kind: { type: 'string', enum: ['user', 'companion'] },
    companion_id: nullableUuid,
    display_name: { type: 'string' },
    joined_at: timestamp,
    status: { type: 'string', enum: ['active', 'left'] },
  },
  required: ['member_kind', 'display_name', 'joined_at', 'status'],
};

const companionConversation = {
  type: 'object',
  additionalProperties: true,
  properties: {
    id: uuid,
    kind: { type: 'string', enum: ['private', 'group'] },
    // Who opened the conversation. Membership cannot answer it: an autonomously created group
    // still carries the user as a member.
    origin: { type: 'string', enum: ['user', 'companion'] },
    can_open: { type: 'boolean' },
    title: nullableString,
    // Set when inviting into a private chat and Core builds a linked group; history is copied
    // only when the caller asks for it.
    parent_conversation_id: nullableUuid,
    autonomy_paused: { type: 'boolean' },
    turn_budget: { type: 'integer' },
    created_at: timestamp,
    last_message_at: nullableTimestamp,
    last_message_preview: nullableString,
    message_count: { type: 'integer' },
    members: { type: 'array', items: ref('CompanionMember') },
  },
  required: ['id', 'kind', 'origin', 'can_open', 'autonomy_paused', 'turn_budget', 'message_count', 'members'],
};

const companionMessage = {
  type: 'object',
  additionalProperties: true,
  properties: {
    id: uuid,
    conversation_id: uuid,
    seq: { type: 'integer', format: 'int64' },
    author_kind: { type: 'string', enum: ['user', 'companion', 'system'] },
    author_companion_id: nullableUuid,
    author_name: { type: 'string' },
    body: { type: 'string' },
    addressed_to: uuidArray,
    created_at: timestamp,
  },
  required: ['id', 'conversation_id', 'seq', 'author_kind', 'author_name', 'body', 'addressed_to'],
};

const companionRun = {
  type: 'object',
  additionalProperties: true,
  properties: {
    id: uuid,
    companion_id: uuid,
    conversation_id: uuid,
    trigger_message_id: nullableUuid,
    status: { type: 'string', enum: ['queued', 'running', 'completed', 'failed'] },
    depth: { type: 'integer' },
    config_revision: { type: 'integer' },
    loaded_memory_ids: uuidArray,
    loaded_skill_binding_ids: uuidArray,
    rounds: { type: 'integer' },
    error_code: nullableString,
    error_message: nullableString,
    reply_message_id: nullableUuid,
    created_at: timestamp,
    started_at: nullableTimestamp,
    finished_at: nullableTimestamp,
  },
  required: ['id', 'companion_id', 'conversation_id', 'status', 'depth', 'config_revision', 'loaded_memory_ids', 'loaded_skill_binding_ids', 'rounds'],
};

const companionActivity = {
  type: 'object',
  additionalProperties: true,
  properties: {
    id: uuid,
    kind: { type: 'string' },
    companion_id: nullableUuid,
    companion_name: nullableString,
    conversation_id: nullableUuid,
    conversation_title: nullableString,
    summary: nullableString,
    detail: nullableString,
    payload: { type: 'object', additionalProperties: true, nullable: true },
    created_at: timestamp,
  },
  required: ['id', 'kind', 'created_at'],
};

const companionSettings = {
  type: 'object',
  additionalProperties: true,
  properties: {
    main_companion_id: nullableUuid,
    autonomy_paused: { type: 'boolean' },
    revision: { type: 'integer' },
  },
  required: ['main_companion_id', 'autonomy_paused', 'revision'],
};

const companionSendMessageResult = {
  type: 'object',
  additionalProperties: true,
  properties: {
    message: ref('CompanionMessage'),
    runs: { type: 'array', items: ref('CompanionRun') },
    // A wake that was refused (paused, over budget, too deep) still leaves the message stored;
    // the skip is reported here instead of failing the send.
    skipped_runs: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: true,
        properties: { companion_id: uuid, reason: { type: 'string' } },
        required: ['companion_id', 'reason'],
      },
    },
    duplicate: { type: 'boolean' },
  },
  required: ['message', 'runs', 'skipped_runs', 'duplicate'],
};

const companionAutonomyTickResult = {
  type: 'object',
  additionalProperties: true,
  properties: {
    acted: { type: 'boolean' },
    action: { type: 'string', enum: ['none', 'create_group', 'invite', 'send_message'] },
    reason: nullableString,
    activity: nullableRef('CompanionActivity'),
    conversation: nullableRef('CompanionConversation'),
    message: nullableRef('CompanionMessage'),
  },
  required: ['acted', 'action', 'reason', 'activity', 'conversation', 'message'],
};

export const companionOpenApiSchemas = {
  CompanionSummary: companionSummary,
  CompanionDetail: companionDetail,
  CompanionAutonomy: companionAutonomy,
  CompanionMemory: companionMemory,
  CompanionSkill: companionSkill,
  CompanionConnection: companionConnection,
  CompanionConnectionOverview: companionConnectionOverview,
  CompanionMember: companionMember,
  CompanionConversation: companionConversation,
  CompanionMessage: companionMessage,
  CompanionRun: companionRun,
  CompanionActivity: companionActivity,
  CompanionSettings: companionSettings,
  CompanionSendMessageResult: companionSendMessageResult,
  CompanionAutonomyTickResult: companionAutonomyTickResult,
  CompanionList: { type: 'array', items: ref('CompanionSummary') },
  CompanionMemoryList: { type: 'array', items: ref('CompanionMemory') },
  CompanionSkillList: { type: 'array', items: ref('CompanionSkill') },
  CompanionConnectionList: { type: 'array', items: ref('CompanionConnection') },
  CompanionConnectionOverviewList: { type: 'array', items: ref('CompanionConnectionOverview') },
  CompanionConversationList: { type: 'array', items: ref('CompanionConversation') },
  CompanionMessageList: { type: 'array', items: ref('CompanionMessage') },
  CompanionActivityList: { type: 'array', items: ref('CompanionActivity') },
};
