import { describe, expect, it } from 'vitest'
import { digestAgentPackManifest } from '../packIntegrity'
import {
  GRAPH_SEED_PACK_DIGEST,
  GRAPH_SEED_PACK_ID,
  GRAPH_SEED_PACK_VERSION,
  graphSeedPackEnvelope,
  graphSeedPackManifest,
} from './index'

describe('GraphSeedPack', () => {
  it('has stable app ownership and internal references', () => {
    expect(graphSeedPackManifest.metadata.pack_id).toBe(GRAPH_SEED_PACK_ID)
    expect(graphSeedPackManifest.metadata.owner).toBe('tinadec')
    expect(graphSeedPackManifest.metadata.product_id).toBe('tinadec-core')
    expect(graphSeedPackManifest.metadata.version).toBe(GRAPH_SEED_PACK_VERSION)

    // Three execution templates plus TWO conversation identities: `meeting` (no tools —
    // it only orchestrates) and `solo_master` (holds tools and does the work itself).
    // No governance/auxiliary agents: a seed pack must not depend on Core-internal roles.
    const agents = graphSeedPackManifest.resources.agents
    expect(agents.map((agent) => agent.resource_key)).toEqual([
      'meeting',
      'solo_master',
      'search',
      'global_engineering',
    ])
    expect(agents.every((agent) => Boolean(agent.system_prompt?.trim()))).toBe(true)

    // The orchestrating identity declares NO tools: it coordinates and dispatches, and
    // an empty tool_scope is also what keeps its mode on the declared-graph tiers.
    const meeting = agents.find((agent) => agent.resource_key === 'meeting')!
    expect(meeting.layer).toBe('operation')
    expect(meeting.tool_scope).toEqual([])

    // The solo identity is the opposite by design: the operation layer MAY now hold a
    // tool surface (the removed deny floor), and this is what makes its mode derive the
    // solo_dispatch tier. It needs read + write + shell to do the work, `task_dispatch`
    // to hand sub-tasks off from inside its loop, and deliberately NOT git_push — a
    // heavier side effect the master can dispatch to global_engineering instead.
    const soloMaster = agents.find((agent) => agent.resource_key === 'solo_master')!
    expect(soloMaster.layer).toBe('operation')
    expect(soloMaster.tool_scope).toContain('read_file')
    expect(soloMaster.tool_scope).toContain('write_file')
    expect(soloMaster.tool_scope).toContain('shell')
    expect(soloMaster.tool_scope).toContain('task_dispatch')
    expect(soloMaster.tool_scope).not.toContain('git_push')
    // A conversation identity must carry the conversation capability or admission
    // refuses the run (conversation_identity_locked_mismatch).
    expect(soloMaster.capabilities).toContain('user.respond')

    // The engineering template carries the git tools this round added: a pack whose
    // prose promises repository work must declare them or the ceiling silently
    // omits them.
    const engineering = agents.find((agent) => agent.resource_key === 'global_engineering')!
    expect(engineering.tool_scope).toContain('git_commit')
    expect(engineering.tool_scope).toContain('git_push')
  })

  it('declares one mode per orchestration tier', () => {
    const modes = graphSeedPackManifest.resources.modes
    expect(modes.map((mode) => mode.resource_key)).toEqual([
      'free_director',
      'vibe_graph',
      'fixed_pipeline',
      'solo',
    ])

    const bySlug = new Map(modes.map((mode) => [mode.resource_key, mode] as const))
    // free_form: a single director node and no declared edges — the tier is
    // derived from the topology, so an authoring mistake here silently changes
    // which enforcement path a run takes.
    expect(bySlug.get('free_director')!.nodes).toHaveLength(1)
    expect(bySlug.get('free_director')!.edges).toHaveLength(0)
    // self_dispatch and deterministic share the same three nodes and two edges;
    // they differ in the meeting binding's envelope (deterministic removes the
    // spawn room).
    for (const key of ['vibe_graph', 'fixed_pipeline'] as const) {
      expect(bySlug.get(key)!.nodes).toHaveLength(3)
      expect(bySlug.get(key)!.edges).toHaveLength(2)
    }

    // solo_dispatch mirrors the free-director SHAPE on purpose: no declared edges, so
    // its sub-agents must be declared as spawnable templates (agent_types + node-less
    // bindings) rather than as nodes. Declaring them as nodes with no edge would make
    // the edge authority deny dispatch to them.
    const solo = bySlug.get('solo')!
    expect(solo.nodes).toHaveLength(1)
    expect(solo.edges).toHaveLength(0)
    expect(solo.nodes[0].agent_ref).toBe('agent:solo_master')

    const resourceKeys = new Set(graphSeedPackManifest.resources.agents.map((agent) => agent.resource_key))
    for (const mode of modes) {
      expect(mode.nodes.some((node) => node.layer === 'operation')).toBe(true)
      for (const node of mode.nodes) {
        expect(node.agent_ref).toMatch(/^agent:/)
        expect(resourceKeys.has(node.agent_ref.slice('agent:'.length))).toBe(true)
      }
      // Edge endpoints must resolve to declared nodes, or the frozen graph would
      // carry an edge the dispatch path can never walk.
      const nodeKeys = new Set(mode.nodes.map((node) => node.node_key))
      for (const edge of mode.edges) {
        expect(nodeKeys.has(edge.source_node_key), `${mode.resource_key} edge source`).toBe(true)
        expect(nodeKeys.has(edge.target_node_key), `${mode.resource_key} edge target`).toBe(true)
      }
    }
  })

  it('binds every mode to its own prompt pipeline', () => {
    // Prompts used to be bound per AGENT only, so all modes shared one set of
    // instructions and nothing on a mode could say "this mode collaborates
    // differently". Four modes ⇒ four pipelines, each describing its own
    // collaboration semantics for the master AND the sub-agents.
    const modes = graphSeedPackManifest.resources.modes
    const promptKeys = new Set(graphSeedPackManifest.resources.prompt_pipelines.map((pipeline) => pipeline.resource_key))

    const bound = modes.map((mode) => {
      expect(mode.prompt_pipeline_ref, `mode '${mode.resource_key}' prompt_pipeline_ref`).toMatch(/^prompt:/)
      const key = mode.prompt_pipeline_ref!.slice('prompt:'.length)
      expect(promptKeys.has(key), `mode '${mode.resource_key}' references unknown pipeline '${key}'`).toBe(true)
      return key
    })
    // Distinct pipelines, not four names for one: that is the whole point.
    expect(new Set(bound).size).toBe(modes.length)
    expect(bound).toEqual(['free-base', 'vibe-base', 'fixed-base', 'solo-base'])

    // Each pipeline must actually carry prose — an empty template list assembles to
    // nothing, which would silently ship a mode with no instructions at all.
    for (const pipeline of graphSeedPackManifest.resources.prompt_pipelines) {
      const content = pipeline.graph.nodes
        .map((node) => (node as { config?: { content?: string } }).config?.content ?? '')
        .join('')
      expect(content.trim().length, `pipeline '${pipeline.resource_key}' content`).toBeGreaterThan(0)
    }

    // The solo pipeline has to state the two things the mode depends on and that the
    // model would otherwise get wrong: dispatching is QUEUED (its result is not in the
    // tool's return value), and writes still need per-call approval.
    const soloContent = graphSeedPackManifest.resources.prompt_pipelines
      .find((pipeline) => pipeline.resource_key === 'solo-base')!
      .graph.nodes
      .map((node) => (node as { config?: { content?: string } }).config?.content ?? '')
      .join('')
    expect(soloContent).toContain('task_dispatch')
    expect(soloContent).toContain('不会回到你这一次调用里')
    expect(soloContent).toContain('人工批准')
  })

  it('carries all five relationship fields on every node', () => {
    // The relationship file is compiled into the role system prompt through a
    // fixed tail slot that participates in the prompt hash; a missing field means
    // a silently weaker contract rather than a failure.
    for (const mode of graphSeedPackManifest.resources.modes) {
      for (const node of mode.nodes) {
        const relationship = node.relationship
        expect(relationship, `${mode.resource_key}/${node.node_key} relationship`).toBeTruthy()
        expect(relationship!.duty.trim().length).toBeGreaterThan(0)
        expect(Object.keys(relationship!.inputs_outputs).length).toBeGreaterThan(0)
        expect(Array.isArray(relationship!.allowed_dispatch_targets)).toBe(true)
        expect(relationship!.success_criteria.length).toBeGreaterThan(0)
        expect(Array.isArray(relationship!.agent_types)).toBe(true)
      }
    }

    // The spawn whitelist is what makes a spawn demand admissible in free_form and
    // solo_dispatch; only the free director and the solo master declare one (the
    // deterministic tier has none, which is why graph_tier_spawn_denied is its
    // contract).
    const whitelist = ['search', 'global_engineering']
    for (const key of ['free_director', 'fixed_pipeline', 'solo'] as const) {
      const mode = graphSeedPackManifest.resources.modes.find((row) => row.resource_key === key)!
      expect(mode.nodes[0].relationship!.agent_types, `${key} spawn whitelist`).toEqual(whitelist)
    }
    // The graph tier derives the deterministic contract from the topology, but the
    // spawn room is what the gate reads: an absent spawn envelope is a denial.
    const fixedPipeline = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'fixed_pipeline')!
    const fixedBinding = fixedPipeline.bindings!.find((binding) => binding.node_key === 'meeting')!
    expect(fixedBinding.envelope!.spawn!.max_depth).toBe(0)
  })

  it('binds node-less envelopes for spawnable templates and narrows tools where declared', () => {
    for (const key of ['free_director', 'solo'] as const) {
      const mode = graphSeedPackManifest.resources.modes.find((row) => row.resource_key === key)!
      // Node-less bindings attach a resource envelope to a spawnable template: the
      // director spawns these through the engine-authoritative path, and the
      // envelope is where the spawned instance's resource grants come from.
      const nodeLess = mode.bindings!.filter((binding) => binding.node_key == null)
      expect(nodeLess.map((binding) => binding.agent_ref).sort(), `${key} node-less bindings`).toEqual([
        'agent:global_engineering',
        'agent:search',
      ])
      for (const binding of nodeLess) {
        expect(binding.envelope!.resources!.read).toEqual([''])
      }
      expect(nodeLess.find((binding) => binding.agent_ref === 'agent:global_engineering')!.envelope!.resources!.write).toEqual([''])
      expect(nodeLess.find((binding) => binding.agent_ref === 'agent:search')!.envelope!.resources!.write).toBeUndefined()
    }

    // A tool switch can only narrow the template scope; the deterministic tier
    // uses one so its engineering node never writes.
    const fixedPipeline = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'fixed_pipeline')!
    const engineering = fixedPipeline.bindings!.find((binding) => binding.agent_ref === 'agent:global_engineering')!
    expect(engineering.tool_switches).toEqual({ write_file: false })
  })

  it('grants the solo master the workspace write it needs to work itself', () => {
    // The resource envelope is the only thing that can grant write:
    // WorkspaceGrantDefaults never implies it, so a master holding write_file with a
    // read-only envelope would have every write DENIED (not asked) at the decision
    // point. The spawn room must be present too, or "dispatch aggressively" is
    // rejected as graph_tier_spawn_denied.
    const solo = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'solo')!
    const masterBinding = solo.bindings!.find((binding) => binding.node_key === 'meeting')!
    expect(masterBinding.agent_ref).toBe('agent:solo_master')
    expect(masterBinding.envelope!.resources!.read).toEqual([''])
    expect(masterBinding.envelope!.resources!.write).toEqual([''])
    expect(masterBinding.envelope!.spawn!.max_depth).toBeGreaterThan(0)
    expect(masterBinding.envelope!.spawn!.max_agents_per_run).toBeGreaterThan(0)
  })

  it('points activation at declared resources', () => {
    const defaults = graphSeedPackManifest.activation.workspace_defaults
    const agentKeys = new Set(graphSeedPackManifest.resources.agents.map((agent) => agent.resource_key))
    const promptKeys = new Set(graphSeedPackManifest.resources.prompt_pipelines.map((pipeline) => pipeline.resource_key))
    expect(agentKeys.has(defaults.agent_ref.slice('agent:'.length))).toBe(true)
    expect(graphSeedPackManifest.resources.modes.some((mode) => `mode:${mode.resource_key}` === defaults.mode_ref)).toBe(true)
    expect(promptKeys.has(defaults.prompt_pipeline_ref.slice('prompt:'.length))).toBe(true)
    for (const agent of graphSeedPackManifest.resources.agents) {
      expect(agent.base_prompt_pipeline_ref).toMatch(/^prompt:/)
      expect(promptKeys.has(agent.base_prompt_pipeline_ref!.slice('prompt:'.length))).toBe(true)
    }
  })

  it('carries the RFC 8785 SHA-256 digest of the manifest only', async () => {
    expect(await digestAgentPackManifest(graphSeedPackManifest)).toBe(GRAPH_SEED_PACK_DIGEST)
    expect(graphSeedPackEnvelope.integrity.digest).toBe(GRAPH_SEED_PACK_DIGEST)
  })
})
