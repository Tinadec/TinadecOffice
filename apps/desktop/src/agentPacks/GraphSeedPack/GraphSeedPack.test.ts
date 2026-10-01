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

    // Three execution templates, TWO conversation identities — `meeting` (no tools, it only
    // orchestrates) and `solo_master` (holds tools and does the work itself) — and one standing
    // governance role. The governance role is declared by the pack like any other agent (a
    // relationship-file subscription wires it), never a Core-internal role the pack depends on.
    const agents = graphSeedPackManifest.resources.agents
    expect(agents.map((agent) => agent.resource_key)).toEqual([
      'meeting',
      'solo_master',
      'search',
      'global_engineering',
      'reviewer',
      'governance_reviewer',
    ])
    expect(agents.every((agent) => Boolean(agent.system_prompt?.trim()))).toBe(true)

    // Todo E6 (one conversation identity): every mode converses through `meeting`, which
    // carries the chairman tool ceiling. The modes that coordinate only switch every tool
    // off on its binding (their tier derives from EFFECTIVE tools, never from slugs);
    // Solo keeps the full surface and derives solo_dispatch exactly as before.
    const meeting = agents.find((agent) => agent.resource_key === 'meeting')!
    expect(meeting.layer).toBe('operation')
    expect(meeting.tool_scope).toContain('read_file')
    expect(meeting.tool_scope).toContain('write_file')
    expect(meeting.tool_scope).toContain('shell')
    expect(meeting.tool_scope).toContain('task_dispatch')
    expect(meeting.tool_scope).toContain('task_wait')
    // A single tool loop has no planner: plan_update is where a multi-step plan lives.
    expect(meeting.tool_scope).toContain('plan_update')
    expect(meeting.tool_scope).not.toContain('git_push')
    // A conversation identity must carry the conversation capability or admission
    // refuses the run (conversation_identity_locked_mismatch).
    expect(meeting.capabilities).toContain('user.respond')

    // solo_master stays published for sessions frozen on it, but no mode may still point
    // a conversation node at it (they all converse through meeting now).
    const soloMaster = agents.find((agent) => agent.resource_key === 'solo_master')!
    expect(soloMaster.layer).toBe('operation')
    expect(soloMaster.tool_scope).toEqual(meeting.tool_scope)

    // The engineering template carries the git tools this round added: a pack whose
    // prose promises repository work must declare them or the ceiling silently
    // omits them.
    const engineering = agents.find((agent) => agent.resource_key === 'global_engineering')!
    expect(engineering.tool_scope).toContain('git_commit')
    expect(engineering.tool_scope).toContain('git_push')
    expect(engineering.tool_scope).toContain('plan_update')
    // Line-level edits: without them every change is a whole-file rewrite.
    for (const tool of ['replace_lines', 'insert_line', 'delete_line']) {
      expect(engineering.tool_scope).toContain(tool)
      expect(soloMaster.tool_scope).toContain(tool)
    }

    // The reviewer only reads: no file, git or process mutation, and no egress (web_fetch and
    // mcp_invoke always ask a human). That is what lets a Review run end to end without a
    // single approval prompt, and what keeps "Review does not change code" true by construction.
    const reviewer = agents.find((agent) => agent.resource_key === 'reviewer')!
    expect(reviewer.layer).toBe('execution')
    expect(reviewer.tool_scope).toContain('git_diff')
    expect(reviewer.tool_scope).toContain('read_file')
    for (const tool of ['write_file', 'replace_lines', 'insert_line', 'delete_line', 'shell', 'git_commit', 'git_push', 'web_fetch', 'mcp_invoke']) {
      expect(reviewer.tool_scope, `reviewer must not hold ${tool}`).not.toContain(tool)
    }
    // The code reviewer stays outside the organization's conversation on purpose: a Review lens
    // must not read the other lenses' conclusions before it has formed its own.
    expect(reviewer.tool_scope.some((tool) => tool.startsWith('org_'))).toBe(false)

    // Executors are organization members: they can see who is there, read their plan room, talk to
    // a contact and ask for one. Siblings coordinate through TinaChat, never a side channel.
    for (const executor of [engineering, agents.find((agent) => agent.resource_key === 'search')!]) {
      for (const tool of ['org_directory', 'org_read', 'org_send', 'org_contact']) expect(executor.tool_scope).toContain(tool)
      expect(executor.tool_scope).not.toContain('org_report')
    }
    // The solo master reads the graph and closes reports addressed to it.
    for (const tool of ['graph_view', 'org_read', 'org_send', 'org_decide_report', 'recall_evidence', 'git_worktree_create', 'git_worktree_remove']) expect(soloMaster.tool_scope).toContain(tool)
    // The environment steward's tools: see, take for this run, give back.
    for (const tool of ['environment_list', 'environment_acquire', 'environment_release']) expect(soloMaster.tool_scope).toContain(tool)

    // The governance reviewer observes and reports; it holds no workspace tool and no conversation
    // capability (it must never become a conversation identity or flip a tier).
    const governance = agents.find((agent) => agent.resource_key === 'governance_reviewer')!
    expect(governance.layer).toBe('operation')
    expect(governance.tool_scope).toEqual(['graph_view', 'org_directory', 'org_read', 'org_send', 'org_report', 'org_execute_report', 'recall_evidence', 'environment_list'])
    expect(governance.capabilities).not.toContain('user.respond')
  })

  it('declares seven modes on the four orchestration tiers', () => {
    const modes = graphSeedPackManifest.resources.modes
    expect(modes.map((mode) => mode.resource_key)).toEqual([
      'free_director',
      'vibe_graph',
      'fixed_pipeline',
      'solo',
      'plan',
      'review',
      'spec',
    ])
    // Display names are the familiar words users know from other agents; the slug (the id
    // Core, tests and the icon table key on) stays the resource_key.
    expect(modes.map((mode) => mode.display_name)).toEqual(['Team', 'Graph', 'Workflow', 'Solo', 'Plan', 'Review', 'Spec'])
    for (const mode of modes) {
      expect(mode.slug).toBe(mode.resource_key)
      // The picker shows two lines of description; longer text is cut off mid-sentence.
      expect(mode.description!.length, `${mode.resource_key} description`).toBeLessThanOrEqual(50)
    }

    const bySlug = new Map(modes.map((mode) => [mode.resource_key, mode] as const))
    // free_form: one director node and no declared edges — the tier is derived from the
    // topology, so an authoring mistake here silently changes which enforcement path a run
    // takes. Team also carries a standing governance node: an operation-layer member woken by
    // lease conflicts, which is not a dispatch target and adds no edge.
    const team = bySlug.get('free_director')!
    expect(team.nodes.map((node) => node.node_key)).toEqual(['meeting', 'governance_reviewer'])
    expect(team.edges).toHaveLength(0)
    const standing = team.nodes.find((node) => node.node_key === 'governance_reviewer')!
    expect(standing.layer).toBe('operation')
    expect(standing.config.conversation).toBeUndefined()
    expect(standing.relationship!.subscriptions).toEqual(['lease_conflict'])
    expect(standing.relationship!.allowed_dispatch_targets).toEqual([])
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
    // the edge authority deny dispatch to them. The conversation agent is the unified
    // identity (todo E6); the solo tier comes from its effective tools, not its slug.
    const solo = bySlug.get('solo')!
    expect(solo.nodes).toHaveLength(1)
    expect(solo.edges).toHaveLength(0)
    expect(solo.nodes[0].agent_ref).toBe('agent:meeting')

    // Plan, Review and Spec are compositions of existing tiers, not new machinery: Plan is the
    // solo_dispatch shape with its mutating tools switched off, Review and Spec are free-form
    // directors with their own spawn whitelist and pipeline.
    for (const key of ['plan', 'review', 'spec'] as const) {
      expect(bySlug.get(key)!.nodes, `${key} nodes`).toHaveLength(1)
      expect(bySlug.get(key)!.edges, `${key} edges`).toHaveLength(0)
    }

    // Todo E6 (one conversation identity): EVERY mode converses through the same agent, so a
    // session can move between any two modes; switching what the mode does is strategy, not
    // identity. A session frozen on the legacy solo_master identity migrates at admission
    // while nothing is running.
    const conversationAgent = (key: string) => bySlug.get(key)?.nodes?.find((node) => node.config?.conversation === true)?.agent_ref
    for (const slug of ['solo', 'plan', 'free_director', 'vibe_graph', 'fixed_pipeline', 'review', 'spec']) {
      expect(conversationAgent(slug), `${slug} conversation`).toBe('agent:meeting')
    }
    // The modes whose conversation must hold NO tools (their tier derives from effective
    // tools) switch the whole chairman ceiling off; Solo and Plan opt into their own tools.
    const conversationBinding = (key: string) => {
      const binding = bySlug.get(key)?.bindings?.find((candidate) =>
        candidate.agent_ref === 'agent:meeting' && (candidate.node_key ?? 'meeting') === 'meeting')
      expect(binding, `${key} conversation binding`).toBeDefined()
      return binding!
    }
    const soloMaster = graphSeedPackManifest.resources.agents.find((agent) => agent.resource_key === 'solo_master')!
    for (const slug of ['free_director', 'vibe_graph', 'fixed_pipeline', 'review', 'spec']) {
      const switches = conversationBinding(slug).tool_switches ?? {}
      for (const tool of soloMaster.tool_scope ?? []) expect(switches[tool], `${slug} switches ${tool} off`).toBe(false)
    }

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
    // differently". One pipeline per mode, each describing its own
    // collaboration semantics for the master AND the sub-agents.
    const modes = graphSeedPackManifest.resources.modes
    const promptKeys = new Set(graphSeedPackManifest.resources.prompt_pipelines.map((pipeline) => pipeline.resource_key))

    const bound = modes.map((mode) => {
      expect(mode.prompt_pipeline_ref, `mode '${mode.resource_key}' prompt_pipeline_ref`).toMatch(/^prompt:/)
      const key = mode.prompt_pipeline_ref!.slice('prompt:'.length)
      expect(promptKeys.has(key), `mode '${mode.resource_key}' references unknown pipeline '${key}'`).toBe(true)
      return key
    })
    // Distinct pipelines, not seven names for one: that is the whole point.
    expect(new Set(bound).size).toBe(modes.length)
    expect(bound).toEqual(['free-base', 'vibe-base', 'fixed-base', 'solo-base', 'plan-base', 'review-base', 'spec-base'])

    // Each pipeline must actually carry prose — an empty template list assembles to
    // nothing, which would silently ship a mode with no instructions at all.
    for (const pipeline of graphSeedPackManifest.resources.prompt_pipelines) {
      const content = pipeline.graph.nodes
        .map((node) => (node as { config?: { content?: string } }).config?.content ?? '')
        .join('')
      expect(content.trim().length, `pipeline '${pipeline.resource_key}' content`).toBeGreaterThan(0)
    }

    // The solo pipeline has to state the things the mode depends on and that the model
    // would otherwise get wrong: dispatching is QUEUED (its result is not in the tool's
    // return value), task_wait is how the result comes back, and writes still need
    // per-call approval.
    const soloContent = graphSeedPackManifest.resources.prompt_pipelines
      .find((pipeline) => pipeline.resource_key === 'solo-base')!
      .graph.nodes
      .map((node) => (node as { config?: { content?: string } }).config?.content ?? '')
      .join('')
    expect(soloContent).toContain('task_dispatch')
    expect(soloContent).toContain('结果不在这次调用里')
    expect(soloContent).toContain('task_wait')
    expect(soloContent).toContain('plan_update')
    expect(soloContent).toContain('人工批准')

    // The three new pipelines each carry the one rule their mode stands on.
    const contentOf = (key: string) => graphSeedPackManifest.resources.prompt_pipelines
      .find((pipeline) => pipeline.resource_key === key)!
      .graph.nodes
      .map((node) => (node as { config?: { content?: string } }).config?.content ?? '')
      .join('')
    // Plan: the tools are off by design, the plan is written with plan_update, and the way
    // forward is switching to Solo (same conversation agent, so the switch is allowed).
    expect(contentOf('plan-base')).toContain('已关闭')
    expect(contentOf('plan-base')).toContain('plan_update')
    expect(contentOf('plan-base')).toContain('切到 Solo')
    // Review: every lens goes to a reviewer, and reviewers must not see each other's findings.
    expect(contentOf('review-base')).toContain('assignee 写 reviewer')
    expect(contentOf('review-base')).toContain('互相看不到对方')
    // Spec: three named documents, and nothing is written before the user confirms it.
    for (const file of ['requirements.md', 'design.md', 'tasks.md']) expect(contentOf('spec-base')).toContain(file)
    expect(contentOf('spec-base')).toContain('用户确认之前不要落盘')
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
    const whitelists: Record<string, string[]> = {
      free_director: ['search', 'global_engineering'],
      fixed_pipeline: ['search', 'global_engineering'],
      solo: ['search', 'global_engineering'],
      spec: ['search', 'global_engineering'],
      // Plan can only send out read-only investigation; Review only independent reviewers.
      plan: ['search'],
      review: ['reviewer'],
    }
    for (const [key, whitelist] of Object.entries(whitelists)) {
      const mode = graphSeedPackManifest.resources.modes.find((row) => row.resource_key === key)!
      expect(mode.nodes[0].relationship!.agent_types, `${key} spawn whitelist`).toEqual(whitelist)
      // Dispatch targets and the spawn whitelist name the same roles in a single-node mode.
      expect(mode.nodes[0].relationship!.allowed_dispatch_targets, `${key} dispatch targets`).toEqual(whitelist)
    }
    // The graph tier derives the deterministic contract from the topology, but the
    // spawn room is what the gate reads: an absent spawn envelope is a denial.
    const fixedPipeline = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'fixed_pipeline')!
    const fixedBinding = fixedPipeline.bindings!.find((binding) => binding.node_key === 'meeting')!
    expect(fixedBinding.envelope!.spawn!.max_depth).toBe(0)
  })

  it('binds node-less envelopes for spawnable templates and narrows tools where declared', () => {
    const spawnable: Record<string, string[]> = {
      free_director: ['agent:global_engineering', 'agent:search'],
      solo: ['agent:global_engineering', 'agent:search'],
      spec: ['agent:global_engineering', 'agent:search'],
      plan: ['agent:search'],
      review: ['agent:reviewer'],
    }
    for (const [key, templates] of Object.entries(spawnable)) {
      const mode = graphSeedPackManifest.resources.modes.find((row) => row.resource_key === key)!
      // Node-less bindings attach a resource envelope to a spawnable template: the
      // director spawns these through the engine-authoritative path, and the
      // envelope is where the spawned instance's resource grants come from.
      const nodeLess = mode.bindings!.filter((binding) => binding.node_key == null)
      expect(nodeLess.map((binding) => binding.agent_ref).sort(), `${key} node-less bindings`).toEqual(templates)
      for (const binding of nodeLess) {
        expect(binding.envelope!.resources!.read).toEqual([''])
        // Only the engineering executor is ever granted write.
        const expectedWrite = binding.agent_ref === 'agent:global_engineering' ? [''] : undefined
        expect(binding.envelope!.resources!.write, `${key} ${binding.agent_ref} write`).toEqual(expectedWrite)
      }
    }

    // No mode narrows file editing away from the engineering executor. The strict
    // pipeline once switched write_file off, which never stopped writes (shell and
    // git_commit stayed) — it only pushed edits through the shell, around the
    // file-hash guard. Its strictness is the declared edges and the denied spawn.
    for (const mode of graphSeedPackManifest.resources.modes) {
      for (const binding of mode.bindings!.filter((item) => item.agent_ref === 'agent:global_engineering')) {
        expect(binding.tool_switches ?? {}, `${mode.resource_key} engineering switches`).toEqual({})
      }
    }
  })

  it('grants the solo master the workspace write it needs to work itself', () => {
    // The resource envelope is the only thing that can grant write:
    // WorkspaceGrantDefaults never implies it, so a master holding write_file with a
    // read-only envelope would have every write DENIED (not asked) at the decision
    // point. The spawn room must be present too, or "dispatch aggressively" is
    // rejected as graph_tier_spawn_denied. (The conversation agent is the unified
    // identity since E6; the solo behavior lives on this binding, not on the slug.)
    const solo = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'solo')!
    const masterBinding = solo.bindings!.find((binding) => binding.node_key === 'meeting')!
    expect(masterBinding.agent_ref).toBe('agent:meeting')
    expect(masterBinding.envelope!.resources!.read).toEqual([''])
    expect(masterBinding.envelope!.resources!.write).toEqual([''])
    expect(masterBinding.envelope!.spawn!.max_depth).toBeGreaterThan(0)
    expect(masterBinding.envelope!.spawn!.max_agents_per_run).toBeGreaterThan(0)
  })

  it('keeps Plan read-only while it runs on the unified conversation identity', () => {
    // Plan reuses the unified identity (todo E6), like every other mode. Its read-only
    // promise therefore rests on the binding, not on the agent: the node's effective
    // tools (tool_scope minus switched-off ids, the same view Core publishes) must be
    // exactly the reading, planning and dispatch set. Pinned as an exact list so a tool
    // later added to the chairman ceiling cannot reach Plan without someone deciding.
    const plan = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'plan')!
    const binding = plan.bindings!.find((item) => item.node_key === 'meeting')!
    expect(binding.agent_ref).toBe('agent:meeting')
    const soloMaster = graphSeedPackManifest.resources.agents.find((agent) => agent.resource_key === 'solo_master')!
    const switches = binding.tool_switches ?? {}
    const effective = soloMaster.tool_scope.filter((tool) => switches[tool] !== false)
    // Reading the organization (who is here, what was said, the session graph, the evidence archive)
    // is reading; posting into it and closing reports are not, so those stay off in Plan.
    expect(effective).toEqual(['ls', 'stat', 'read_file', 'file_search', 'git_status', 'git_diff', 'git_log', 'read_attachment', 'task_dispatch', 'task_wait', 'plan_update',
      'graph_view', 'org_directory', 'org_read', 'recall_evidence', 'environment_list'])
    // No write grant either: even a tool that slipped through would be denied at the decision point.
    expect(binding.envelope!.resources!.read).toEqual([''])
    expect(binding.envelope!.resources!.write).toBeUndefined()
    // The spawn room stays, so Plan can still send read-only investigation out.
    expect(binding.envelope!.spawn!.max_depth).toBeGreaterThan(0)
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
