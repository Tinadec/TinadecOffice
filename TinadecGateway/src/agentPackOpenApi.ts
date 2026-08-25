import { t } from 'elysia';

const componentRef = (name: string) => t.Unsafe({ $ref: `#/components/schemas/${name}` });
const nullableString = () => t.Unsafe({ type: 'string', nullable: true });
const nullableUuid = () => t.Unsafe({ type: 'string', format: 'uuid', nullable: true });
const jsonObject = () => t.Unsafe({ type: 'object', additionalProperties: true });
const nullableJsonObject = () => t.Unsafe({ type: 'object', additionalProperties: true, nullable: true });
const nonNegativeInteger = () => t.Unsafe({ type: 'integer', minimum: 0 });
const stringEnum = (...values: string[]) => t.Unsafe({ type: 'string', enum: values });

const metadata = t.Object({
  pack_id: t.String(),
  owner: t.String(),
  product_id: t.String(),
  name: t.String(),
  version: t.String({ description: 'Semantic version.' }),
}, { additionalProperties: false });

const compatibility = t.Object({
  minimum_core_version: t.String({ description: 'Minimum compatible Core semantic version.' }),
  required_core_capabilities: t.Array(t.String(), { uniqueItems: true }),
}, { additionalProperties: false });

const agentResource = t.Object({
  resource_key: t.String(),
  slug: t.String(),
  display_name: t.String(),
  description: nullableString(),
  layer: stringEnum('operation', 'execution'),
  role: t.String(),
  capabilities: t.Array(t.String(), { uniqueItems: true }),
  model_strategy: jsonObject(),
  tool_scope: t.Array(t.String(), { uniqueItems: true }),
  system_prompt: t.String(),
  enabled: t.Boolean(),
  base_prompt_pipeline_ref: nullableString(),
}, { additionalProperties: false });

const promptPipelineResource = t.Object({
  resource_key: t.String(),
  slug: t.String(),
  display_name: t.String(),
  description: nullableString(),
  graph: jsonObject(),
}, { additionalProperties: false });

const modeNode = t.Object({
  node_key: t.String(),
  agent_ref: t.String({ description: 'Pack-local agent resource key.' }),
  layer: stringEnum('operation', 'execution'),
  label: t.String(),
  config: jsonObject(),
  position: nullableJsonObject(),
}, { additionalProperties: false });

const modeEdge = t.Object({
  edge_key: t.String(),
  source_node_key: t.String(),
  target_node_key: t.String(),
  condition: jsonObject(),
}, { additionalProperties: false });

const modeResource = t.Object({
  resource_key: t.String(),
  slug: t.String(),
  display_name: t.String(),
  description: nullableString(),
  nodes: t.Array(componentRef('AgentPackModeNode')),
  edges: t.Array(componentRef('AgentPackModeEdge')),
  canvas_layout: jsonObject(),
}, { additionalProperties: false });

const resources = t.Object({
  agents: t.Array(componentRef('AgentPackAgentResource')),
  prompt_pipelines: t.Array(componentRef('AgentPackPromptPipelineResource')),
  modes: t.Array(componentRef('AgentPackModeResource')),
}, { additionalProperties: false });

const workspaceDefaults = t.Object({
  agent_ref: t.String({ description: 'Pack-local agent resource key.' }),
  mode_ref: t.String({ description: 'Pack-local mode resource key.' }),
  prompt_pipeline_ref: t.String({ description: 'Pack-local prompt pipeline resource key.' }),
}, { additionalProperties: false });

const activation = t.Object({
  workspace_defaults: componentRef('AgentPackWorkspaceDefaults'),
}, { additionalProperties: false });

const manifest = t.Object({
  api_version: stringEnum('tinadec.io/agent-pack/v1alpha1'),
  kind: stringEnum('AgentPack'),
  metadata: componentRef('AgentPackMetadata'),
  compatibility: componentRef('AgentPackCompatibility'),
  resources: componentRef('AgentPackResources'),
  activation: componentRef('AgentPackActivation'),
}, { additionalProperties: false });

const integrity = t.Object({
  algorithm: stringEnum('sha256'),
  digest: t.String({ pattern: '^[a-f0-9]{64}$' }),
}, { additionalProperties: false });

const envelope = t.Object({
  manifest: componentRef('AgentPackManifest'),
  integrity: componentRef('AgentPackIntegrity'),
}, { additionalProperties: false });

const applyRequest = t.Object({
  preview_id: t.String({ format: 'uuid' }),
  envelope: componentRef('AgentPackEnvelope'),
}, { additionalProperties: false });

const counts = t.Object({
  agents: nonNegativeInteger(),
  prompt_pipelines: nonNegativeInteger(),
  modes: nonNegativeInteger(),
  created: nonNegativeInteger(),
  adopted: nonNegativeInteger(),
  reused: nonNegativeInteger(),
  updated: nonNegativeInteger(),
}, { additionalProperties: false });

const resourceBinding = t.Object({
  kind: stringEnum('agent', 'prompt_pipeline', 'mode'),
  resource_key: t.String(),
  logical_entity_id: nullableUuid(),
  version_id: nullableUuid(),
  content_hash: nullableString(),
  disposition: stringEnum('created', 'adopted', 'reused', 'updated', 'conflict'),
}, { additionalProperties: false });

const installationProperties = {
  pack_id: t.String(),
  owner: t.String(),
  product_id: t.String(),
  name: t.String(),
  status: t.String(),
  active_version: nullableString(),
  integrity_digest: nullableString(),
  revision: nonNegativeInteger(),
  installed_at: t.String({ format: 'date-time' }),
  updated_at: t.String({ format: 'date-time' }),
};

const installation = t.Object(installationProperties, { additionalProperties: false });

const version = t.Object({
  id: t.String({ format: 'uuid' }),
  version: t.String(),
  integrity_digest: t.String(),
  created_at: t.String({ format: 'date-time' }),
  active: t.Boolean(),
}, { additionalProperties: false });

const installationDetail = t.Object({
  ...installationProperties,
  versions: t.Array(componentRef('AgentPackVersion')),
  resources: t.Array(componentRef('AgentPackResourceBinding')),
}, { additionalProperties: false });

const preview = t.Object({
  action: stringEnum('install', 'upgrade', 'up_to_date', 'newer_installed', 'conflict'),
  preview_id: t.String({ format: 'uuid' }),
  pack_id: t.String(),
  owner: t.String(),
  bundled_version: t.String(),
  installed_version: nullableString(),
  integrity_digest: t.String(),
  revision: nonNegativeInteger(),
  expires_at: t.String({ format: 'date-time' }),
  counts: componentRef('AgentPackCounts'),
  resources: t.Array(componentRef('AgentPackResourceBinding')),
  defaults_will_adopt: t.Boolean(),
  required_core_version: nullableString(),
  current_core_version: t.String(),
  differences: t.Array(t.String()),
  warnings: t.Array(t.String()),
}, { additionalProperties: false });

const applyResult = t.Object({
  status: stringEnum('installed', 'updated', 'up_to_date', 'newer_installed'),
  pack_id: t.String(),
  owner: t.String(),
  active_version: t.String(),
  integrity_digest: t.String(),
  revision: nonNegativeInteger(),
  counts: componentRef('AgentPackCounts'),
  resources: t.Array(componentRef('AgentPackResourceBinding')),
  defaults_adopted: t.Boolean(),
  installed_at: t.String({ format: 'date-time' }),
  updated_at: t.String({ format: 'date-time' }),
}, { additionalProperties: false });

const problemDetails = t.Object({
  type: t.String({ format: 'uri' }),
  title: t.String(),
  status: t.Unsafe({ type: 'integer', minimum: 400, maximum: 599 }),
  detail: t.Optional(t.String()),
  code: t.String({ description: 'Stable snake_case machine-readable error code.' }),
  trace_id: t.Optional(t.String()),
  instance: t.Optional(t.String()),
}, { additionalProperties: true });

export const agentPackOpenApiSchemas = {
  AgentPackMetadata: metadata,
  AgentPackCompatibility: compatibility,
  AgentPackAgentResource: agentResource,
  AgentPackPromptPipelineResource: promptPipelineResource,
  AgentPackModeNode: modeNode,
  AgentPackModeEdge: modeEdge,
  AgentPackModeResource: modeResource,
  AgentPackResources: resources,
  AgentPackWorkspaceDefaults: workspaceDefaults,
  AgentPackActivation: activation,
  AgentPackManifest: manifest,
  AgentPackIntegrity: integrity,
  AgentPackEnvelope: envelope,
  AgentPackApplyRequest: applyRequest,
  AgentPackCounts: counts,
  AgentPackResourceBinding: resourceBinding,
  AgentPackInstallation: installation,
  AgentPackInstallationList: t.Array(componentRef('AgentPackInstallation')),
  AgentPackVersion: version,
  AgentPackInstallationDetail: installationDetail,
  AgentPackInstallPreview: preview,
  AgentPackApplyResult: applyResult,
  ProblemDetails: problemDetails,
};

const etagHeader = {
  ETag: {
    description: 'Quoted workspace-scoped agent pack installation revision.',
    schema: { type: 'string', example: '"2"' },
  },
} as const;

export const agentPackEnvelopeRequestBody = {
  required: true,
  content: {
    'application/json': { schema: { $ref: '#/components/schemas/AgentPackEnvelope' } },
  },
};

export const agentPackApplyRequestBody = {
  required: true,
  content: {
    'application/json': { schema: { $ref: '#/components/schemas/AgentPackApplyRequest' } },
  },
};

export function agentPackJsonResponse(schemaName: string, description: string, includeEtag = false) {
  return {
    description,
    ...(includeEtag ? { headers: etagHeader } : {}),
    content: {
      'application/json': { schema: { $ref: `#/components/schemas/${schemaName}` } },
    },
  } as const;
}

export function agentPackProblemResponse(description: string) {
  return {
    description,
    content: {
      'application/problem+json': { schema: { $ref: '#/components/schemas/ProblemDetails' } },
    },
  } as const;
}

export function agentPackApplyHeaderParameters() {
  return [
    {
      name: 'Idempotency-Key',
      in: 'header' as const,
      required: true,
      description: 'Unique key for convergent install or upgrade retries.',
      schema: { type: 'string' as const, minLength: 1, maxLength: 256 },
    },
    {
      name: 'If-Match',
      in: 'header' as const,
      required: false,
      description: 'Quoted preview base revision. Required when upgrading an installed pack.',
      schema: { type: 'string' as const, example: '"1"' },
    },
  ];
}
