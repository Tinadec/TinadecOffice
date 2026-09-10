import manifestJson from './manifest.json'

export type AgentPackLayer = 'operation' | 'execution'
export type AgentPackReference<Kind extends 'agent' | 'mode' | 'prompt'> = `${Kind}:${string}`

export interface AgentPackAgentResource {
  resource_key: string
  slug: string
  display_name: string
  description: string | null
  layer: AgentPackLayer
  role: string
  capabilities: string[]
  model_strategy: Record<string, unknown>
  tool_scope: string[]
  system_prompt: string | null
  enabled: boolean
  base_prompt_pipeline_ref: AgentPackReference<'prompt'> | null
}

export interface AgentPackPromptPipelineResource {
  resource_key: string
  slug: string
  display_name: string
  description: string | null
  graph: {
    nodes: Record<string, unknown>[]
    edges: Record<string, unknown>[]
  }
}

export interface AgentPackModeNodeResource {
  node_key: string
  agent_ref: AgentPackReference<'agent'>
  layer: AgentPackLayer
  label: string
  config: Record<string, unknown>
  position: Record<string, unknown> | null
}

export interface AgentPackModeEdgeResource {
  edge_key: string
  source_node_key: string
  target_node_key: string
  condition: Record<string, unknown>
}

export interface AgentPackModeResource {
  resource_key: string
  slug: string
  display_name: string
  description: string | null
  nodes: AgentPackModeNodeResource[]
  edges: AgentPackModeEdgeResource[]
  canvas_layout: Record<string, unknown>
}

export interface AgentPackManifest {
  api_version: 'tinadec.io/agent-pack/v1alpha1'
  kind: 'AgentPack'
  metadata: {
    pack_id: string
    owner: string
    product_id: string
    name: string
    version: string
  }
  compatibility: {
    minimum_core_version: string
    required_core_capabilities: string[]
  }
  resources: {
    agents: AgentPackAgentResource[]
    prompt_pipelines: AgentPackPromptPipelineResource[]
    modes: AgentPackModeResource[]
  }
  activation: {
    workspace_defaults: {
      agent_ref: AgentPackReference<'agent'>
      mode_ref: AgentPackReference<'mode'>
      prompt_pipeline_ref: AgentPackReference<'prompt'>
    }
  }
}

export interface AgentPackEnvelope {
  manifest: AgentPackManifest
  integrity: {
    algorithm: 'sha256'
    digest: string
  }
}

export const OFFICE_AGENT_PACK_ID = 'tinadec.office.agent-pack'
export const OFFICE_AGENT_PACK_VERSION = '0.2.4'

export const officeAgentPackManifest = manifestJson as AgentPackManifest

// Filled from the RFC 8785 canonical manifest bytes. A test recalculates this
// value so any manifest edit must intentionally publish a new digest.
// The manifest's own metadata.version must move with every digest change: Core
// keys installs by version and rejects a re-publish of an already installed
// version whose digest differs (agent_pack_version_hash_conflict).
export const OFFICE_AGENT_PACK_DIGEST = 'e99cd56747cd96fb9d89b5a9082a1b00f54c3622620ce43fb676e3551216f2e9'

export const officeAgentPackEnvelope: AgentPackEnvelope = {
  manifest: officeAgentPackManifest,
  integrity: {
    algorithm: 'sha256',
    digest: OFFICE_AGENT_PACK_DIGEST,
  },
}

type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue }

/** RFC 8785 JSON Canonicalization Scheme for manifest integrity checks. */
export function canonicalizeAgentPackManifest(value: unknown): string {
  return canonicalize(value as JsonValue)
}

function canonicalize(value: JsonValue): string {
  if (value === null || typeof value === 'boolean' || typeof value === 'string') {
    return JSON.stringify(value)
  }
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) throw new TypeError('Agent pack manifest contains a non-finite number.')
    return JSON.stringify(value)
  }
  if (Array.isArray(value)) return `[${value.map(canonicalize).join(',')}]`
  return `{${Object.keys(value)
    .sort()
    .map((key) => `${JSON.stringify(key)}:${canonicalize(value[key]!)}`)
    .join(',')}}`
}

export async function digestAgentPackManifest(manifest: AgentPackManifest): Promise<string> {
  const bytes = new TextEncoder().encode(canonicalizeAgentPackManifest(manifest))
  const digest = await crypto.subtle.digest('SHA-256', bytes)
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, '0')).join('')
}
