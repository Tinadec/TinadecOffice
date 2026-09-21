import manifestJson from './manifest.json'

export type AgentPackLayer = 'operation' | 'execution'
export type AgentPackReference<Kind extends 'agent' | 'mode' | 'prompt'> = `${Kind}:${string}`

/** Five-field per-node duty contract compiled into the role system prompt. */
export interface AgentPackNodeRelationship {
  duty: string
  inputs_outputs: Record<string, unknown>
  allowed_dispatch_targets: string[]
  success_criteria: string[]
  /** Spawn whitelist: the only agent slugs a node may create at runtime. */
  agent_types: string[]
}

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
  relationship?: AgentPackNodeRelationship | null
  position: Record<string, unknown> | null
}

export interface AgentPackModeEdgeResource {
  edge_key: string
  source_node_key: string
  target_node_key: string
  condition: Record<string, unknown>
}

export interface AgentPackModeBindingResource {
  /** Absent for a binding that attaches an envelope to a spawnable template. */
  node_key?: string | null
  agent_ref: AgentPackReference<'agent'>
  duty_description_ref?: string | null
  tool_switches?: Record<string, boolean>
  envelope?: {
    spawn?: { max_depth: number; max_agents_per_run: number; max_parallel_workers: number }
    capabilities?: string[]
    tools?: string[]
    resources?: { read?: string[]; write?: string[] }
  }
  includes_core_reserved: boolean
}

export interface AgentPackModeResource {
  resource_key: string
  slug: string
  display_name: string
  description: string | null
  /**
   * Mode-level prompt pipeline (source priority: mode > agent > workspace default).
   * A prompt pipeline is otherwise bound per AGENT, so without this every mode shares
   * one set of instructions. When declared it wins for the WHOLE mode — the pipeline
   * describes how that mode collaborates, so it reaches the conversation identity and
   * the execution-layer workers alike. Per-agent role wording inside one mode stays on
   * each agent's `system_prompt`.
   */
  prompt_pipeline_ref?: AgentPackReference<'prompt'> | null
  nodes: AgentPackModeNodeResource[]
  edges: AgentPackModeEdgeResource[]
  bindings?: AgentPackModeBindingResource[]
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
    description?: string
  }
  compatibility: {
    minimum_core_version: string
    required_core_capabilities: string[]
  }
  resources: {
    agents: AgentPackAgentResource[]
    prompt_pipelines: AgentPackPromptPipelineResource[]
    modes: AgentPackModeResource[]
    tools?: Record<string, unknown>[]
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

export const GRAPH_SEED_PACK_ID = 'tinadec.graph.seed-pack'
export const GRAPH_SEED_PACK_VERSION = '2.6.0'

export const graphSeedPackManifest = manifestJson as AgentPackManifest

// Filled from the RFC 8785 canonical manifest bytes. A test recalculates this
// value so any manifest edit must intentionally publish a new digest.
// The manifest's own metadata.version must move with every digest change: Core
// keys installs by version and rejects a re-publish of an already installed
// version whose digest differs (agent_pack_version_hash_conflict).
//
// The digest must be computed over the DTO-closed manifest, i.e. the raw file as
// written — Core canonicalizes its DTO round-trip of this same body. See
// packIntegrity.ts for the invariant and the gates that enforce it.
export const GRAPH_SEED_PACK_DIGEST = 'e34896a257e378e80e60f9370bcc6edf4bcb5c4a3a6bf82eb60328979f268fbf'

export const graphSeedPackEnvelope: AgentPackEnvelope = {
  manifest: graphSeedPackManifest,
  integrity: {
    algorithm: 'sha256',
    digest: GRAPH_SEED_PACK_DIGEST,
  },
}
