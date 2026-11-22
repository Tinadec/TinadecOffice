import { describe, expect, it } from 'vitest'
import { digestAgentPackManifest } from '../packIntegrity'
import {
  GRAPH_SEED_PACK_DIGEST,
  GRAPH_SEED_PACK_ID,
  GRAPH_SEED_PACK_VERSION,
  graphSeedPackEnvelope,
  graphSeedPackManifest,
} from './index'

const REQUIRED_LOCALES = ['zh-CN', 'zh-TW', 'en', 'ja', 'ru'] as const

const TEAM_TOOLS = ['team_spawn', 'team_message', 'team_wait', 'team_plan', 'team_integrate'] as const

const TINADEC_CONFIG_TOOLS = [
  'tinadec_inspect',
  'tinadec_agent_apply',
  'tinadec_mode_apply',
  'tinadec_mode_publish',
  'tinadec_skill_apply',
  'tinadec_mcp_apply',
  'tinadec_prompt_apply',
  'tinadec_model_profile_apply',
  'tinadec_context_memory_apply',
] as const

const BROWSER_V2_TOOLS = [
  'browser_run',
  'browser_handoff',
  'browser_takeover',
  'browser_finish',
  'browser_dialog',
  'browser_profiles_list',
  'browser_upload',
  'browser_download_save',
] as const

const WORKSPACE_MUTATING_TOOLS = new Set([
  'write_file',
  'delete_file',
  'replace_lines',
  'replace_bytes',
  'insert_line',
  'insert_bytes',
  'insert_byte',
  'delete_line',
  'delete_bytes',
  'shell',
  'command_run',
  'git_stage',
  'git_unstage',
  'git_commit',
  'git_merge',
  'git_rebase',
  'git_conflict_resolve',
  'git_discard',
  'git_checkout',
  'git_branch_create',
  'git_branch_delete',
  'git_branch_rename',
  'git_fetch',
  'git_pull',
  'git_push',
  'git_worktree_create',
  'git_worktree_remove',
  'sandbox_reset',
])

const LEGACY_GLUE_TOOL_IDS = [
  'tina_chat_bind',
  'tina_chat_search_people',
  'tina_chat_list_rooms',
  'tina_chat_read_inbox',
  'tina_chat_send',
  'tina_chat_propose_intent',
  'tina_chat_list_intents',
  'tina_chat_decide_intent',
  'tina_chat_execute_intent',
  'org_directory',
  'org_read',
  'org_send',
  'org_report',
  'org_decide_report',
  'org_execute_report',
  'org_contact',
  'org_room',
  'task_dispatch',
  'task_wait',
  'plan_update',
] as const

describe('GraphSeedPack', () => {
  it('has stable app ownership, complete 11-agent roster, modern tool scopes, and zero legacy glue', () => {
    expect(graphSeedPackManifest.metadata.pack_id).toBe(GRAPH_SEED_PACK_ID)
    expect(graphSeedPackManifest.metadata.owner).toBe('tinadec')
    expect(graphSeedPackManifest.metadata.product_id).toBe('tinadec-core')
    expect(graphSeedPackManifest.metadata.version).toBe(GRAPH_SEED_PACK_VERSION)
    expect(graphSeedPackManifest.compatibility.required_core_capabilities).toContain('structured_investigation_v1')
    expect(graphSeedPackManifest.compatibility.required_core_capabilities).toContain('autonomous_exploration_v1')

    const agents = graphSeedPackManifest.resources.agents
    expect(agents.map((agent) => agent.resource_key)).toEqual([
      'meeting',
      'solo_master',
      'search',
      'global_engineering',
      'reviewer',
      'governance_reviewer',
      'context_compressor',
      'artex_director',
      'artex_planner',
      'artex_worker',
      'artex_verifier',
    ])

    // Every agent must carry non-stub translations (> 80 chars system_prompt) across all 5 locales,
    // zero legacy glue tools, and zero "holds no tools" contradiction.
    const serialized = JSON.stringify(graphSeedPackManifest)
    for (const contradiction of [
      '自身不持工具',
      '本身不持有工具',
      'Holds no tools',
      '自らはツールを持ちません',
      'Сам не владеет инструментами',
    ]) {
      expect(serialized, `manifest must not contain "${contradiction}"`).not.toContain(contradiction)
    }

    for (const agent of agents) {
      expect(Boolean(agent.system_prompt?.trim())).toBe(true)
      for (const tool of LEGACY_GLUE_TOOL_IDS) {
        expect(agent.tool_scope, `${agent.resource_key} must not hold legacy tool ${tool}`).not.toContain(tool)
      }
      for (const locale of REQUIRED_LOCALES) {
        const entry = agent.i18n?.[locale]
        expect(entry, `${agent.resource_key} missing locale ${locale}`).toBeDefined()
        expect(entry!.display_name?.trim().length ?? 0, `${agent.resource_key}.${locale}.display_name`).toBeGreaterThan(0)
        expect(entry!.description?.trim().length ?? 0, `${agent.resource_key}.${locale}.description`).toBeGreaterThan(15)
        expect(entry!.system_prompt?.trim().length ?? 0, `${agent.resource_key}.${locale}.system_prompt`).toBeGreaterThan(80)
      }
    }

    // Unified conversation identity (`meeting`): Explore-First Coordinator + Solo/Correction ceiling.
    const meeting = agents.find((agent) => agent.resource_key === 'meeting')!
    expect(meeting.layer).toBe('operation')
    expect(meeting.capabilities).toContain('user.respond')
    for (const tool of [
      'ls',
      'stat',
      'read_file',
      'file_search',
      'git_status',
      'git_diff',
      'git_log',
      'graph_view',
      'recall_evidence',
      'recall_conversation_history',
      'load_skill',
      'mcp_list',
      'mcp_search',
      'mcp_invoke',
      'spec_propose',
      'write_file',
      'replace_lines',
      'insert_line',
      'delete_line',
      'shell',
      'git_commit',
      'git_worktree_create',
      'git_worktree_remove',
      'environment_list',
      'environment_acquire',
      'environment_release',
      ...TEAM_TOOLS,
      ...TINADEC_CONFIG_TOOLS,
      ...BROWSER_V2_TOOLS,
    ]) {
      expect(meeting.tool_scope, `meeting must hold ${tool}`).toContain(tool)
    }
    expect(meeting.tool_scope).not.toContain('git_push')
    expect(meeting.tool_scope.some((tool) => tool.startsWith('tina_chat_') || tool.startsWith('org_'))).toBe(false)

    // Legacy `solo_master`: updated cleanly alongside meeting (no legacy glue, includes #33, Phase 11.5 & #38).
    const soloMaster = agents.find((agent) => agent.resource_key === 'solo_master')!
    expect(soloMaster.layer).toBe('operation')
    expect(soloMaster.tool_scope).toEqual(meeting.tool_scope.filter((tool) => tool !== 'spec_propose'))

    // `search`: read-only research + recall + board messaging + browser v2.
    const search = agents.find((agent) => agent.resource_key === 'search')!
    expect(search.layer).toBe('execution')
    for (const tool of ['read_file', 'file_search', 'git_log', 'web_fetch', 'recall_conversation_history', 'recall_evidence', 'team_message', 'browser_run', 'browser_screenshot', 'browser_handoff']) {
      expect(search.tool_scope, `search must hold ${tool}`).toContain(tool)
    }
    for (const tool of ['write_file', 'shell', 'git_commit', 'team_spawn', 'team_integrate']) {
      expect(search.tool_scope, `search must not hold ${tool}`).not.toContain(tool)
    }

    // `global_engineering`: writable engineering + active browser verification (#38) + team_plan/team_message.
    const engineering = agents.find((agent) => agent.resource_key === 'global_engineering')!
    for (const tool of [
      'replace_lines',
      'insert_line',
      'delete_line',
      'write_file',
      'shell',
      'git_commit',
      'git_push',
      'team_plan',
      'team_message',
      'recall_conversation_history',
      'browser_navigate',
      'browser_snapshot',
      'browser_screenshot',
      'browser_wait',
      'browser_click',
      'browser_type',
      'browser_press',
      'browser_scroll',
      'browser_run',
      'browser_dialog',
      'browser_tabs_list',
      'browser_handoff',
    ]) {
      expect(engineering.tool_scope, `global_engineering must hold ${tool}`).toContain(tool)
    }

    // `reviewer`: STRICTLY read-only and non-spawning (zero code/git/shell mutations, zero team_spawn,
    // zero interactive browser writes, only read/search/git-read + recall_* + team_message + read-only browser verification).
    const reviewer = agents.find((agent) => agent.resource_key === 'reviewer')!
    expect(reviewer.layer).toBe('execution')
    for (const tool of [
      'ls',
      'stat',
      'read_file',
      'load_skill',
      'file_search',
      'git_status',
      'git_diff',
      'git_log',
      'git_blame',
      'git_file_at_revision',
      'git_branch_list',
      'read_attachment',
      'recall_evidence',
      'recall_conversation_history',
      'team_message',
      'browser_navigate',
      'browser_snapshot',
      'browser_screenshot',
      'browser_wait',
      'browser_tabs_list',
    ]) {
      expect(reviewer.tool_scope, `reviewer must hold ${tool}`).toContain(tool)
    }
    for (const forbidden of [
      'team_spawn',
      'team_integrate',
      'team_plan',
      'write_file',
      'delete_file',
      'replace_lines',
      'insert_line',
      'delete_line',
      'shell',
      'git_commit',
      'git_push',
      'git_worktree_create',
      'git_worktree_remove',
      'web_fetch',
      'mcp_invoke',
      'browser_click',
      'browser_type',
      'browser_press',
      'browser_scroll',
      'browser_run',
      'browser_upload',
      'browser_download_save',
      'tinadec_agent_apply',
      'tinadec_context_memory_apply',
    ]) {
      expect(reviewer.tool_scope, `reviewer must not hold ${forbidden}`).not.toContain(forbidden)
    }
    expect(reviewer.tool_scope.some((tool) => WORKSPACE_MUTATING_TOOLS.has(tool))).toBe(false)
    expect(reviewer.tool_scope.some((tool) => tool.startsWith('org_') || tool.startsWith('tina_chat_'))).toBe(false)

    // `governance_reviewer`: read-only standing governance observer on shared board.
    const governance = agents.find((agent) => agent.resource_key === 'governance_reviewer')!
    expect(governance.layer).toBe('operation')
    expect(governance.tool_scope).toEqual([
      'graph_view',
      'team_message',
      'recall_evidence',
      'recall_conversation_history',
      'environment_list',
    ])
    expect(governance.capabilities).not.toContain('user.respond')
    expect(governance.tool_scope).not.toContain('team_spawn')

    // `context_compressor`: structural summarization + verbatim recall only, zero mutation/spawn.
    const compressor = agents.find((agent) => agent.resource_key === 'context_compressor')!
    expect(compressor.layer).toBe('operation')
    expect(compressor.role).toBe('context_maintenance')
    expect(compressor.tool_scope).toEqual(['recall_conversation_history', 'recall_evidence'])
    expect(compressor.capabilities).toEqual(['context.maintain'])
    expect(compressor.capabilities).not.toContain('user.respond')
  })

  it('declares nine modes on the four orchestration tiers with Explore-First Team/Review/Spec and valid ModePublishGate bindings', () => {
    const modes = graphSeedPackManifest.resources.modes
    expect(modes.map((mode) => mode.resource_key)).toEqual([
      'free_director',
      'vibe_graph',
      'fixed_pipeline',
      'solo',
      'plan',
      'review',
      'spec',
      'correction',
      'artex',
    ])
    expect(modes.map((mode) => mode.display_name)).toEqual(['Team', 'Graph', 'Workflow', 'Solo', 'Plan', 'Review', 'Spec', '纠偏', 'ArtEx'])
    for (const mode of modes) {
      expect(mode.slug).toBe(mode.resource_key)
      expect(mode.description!.length, `${mode.resource_key} description`).toBeLessThanOrEqual(50)
      for (const locale of REQUIRED_LOCALES) {
        const locDesc = mode.i18n?.[locale]?.description ?? ''
        expect(locDesc.length, `${mode.resource_key}.i18n[${locale}].description`).toBeGreaterThan(0)
        expect(locDesc.length, `${mode.resource_key}.i18n[${locale}].description`).toBeLessThanOrEqual(50)
      }
    }

    const bySlug = new Map(modes.map((mode) => [mode.resource_key, mode] as const))
    const agentsByRef = new Map(graphSeedPackManifest.resources.agents.map((agent) => [`agent:${agent.resource_key}`, agent] as const))
    const meeting = graphSeedPackManifest.resources.agents.find((agent) => agent.resource_key === 'meeting')!

    // Team (`free_director`): Explore-First Coordinator + standing governance & context upkeep + worktree default ON.
    const team = bySlug.get('free_director')!
    expect(team.default_options).toEqual({
      plan_first: false,
      spec_enabled: false,
      multi_agent: true,
      bulletin_board: true,
      worktree: true,
    })
    expect(team.nodes.map((node) => node.node_key)).toEqual(['meeting', 'governance_reviewer', 'context_compressor'])
    expect(team.edges).toHaveLength(0)
    const standingGov = team.nodes.find((node) => node.node_key === 'governance_reviewer')!
    expect(standingGov.layer).toBe('operation')
    expect(standingGov.config.conversation).toBeUndefined()
    expect(standingGov.relationship!.subscriptions).toEqual(['lease_conflict'])
    expect(standingGov.relationship!.allowed_dispatch_targets).toEqual([])
    const standingComp = team.nodes.find((node) => node.node_key === 'context_compressor')!
    expect(standingComp.layer).toBe('operation')
    expect(standingComp.config.conversation).toBeUndefined()
    expect(standingComp.relationship!.allowed_dispatch_targets).toEqual([])
    expect(standingComp.relationship!.agent_types).toEqual([])

    const conversationBinding = (key: string) => {
      const binding = bySlug.get(key)?.bindings?.find((candidate) =>
        candidate.agent_ref === 'agent:meeting' && (candidate.node_key ?? 'meeting') === 'meeting')
      expect(binding, `${key} conversation binding`).toBeDefined()
      return binding!
    }
    const effectiveMeetingTools = (key: string) => {
      const switches = conversationBinding(key).tool_switches ?? {}
      return meeting.tool_scope.filter((tool) => switches[tool] !== false)
    }

    // Explore-First Team effective tools: read-only exploration + 5 team_* + 9 tinadec_* + load_skill + mcp_*, zero mutations.
    const teamEffective = effectiveMeetingTools('free_director')
    for (const tool of ['ls', 'stat', 'read_file', 'file_search', 'git_status', 'git_diff', 'git_log', 'graph_view', 'recall_evidence', 'recall_conversation_history', 'load_skill', 'mcp_list', 'mcp_search', 'mcp_invoke', ...TEAM_TOOLS, ...TINADEC_CONFIG_TOOLS]) {
      expect(teamEffective, `free_director must enable ${tool}`).toContain(tool)
    }
    expect(teamEffective.some((tool) => WORKSPACE_MUTATING_TOOLS.has(tool))).toBe(false)
    expect(conversationBinding('free_director').envelope!.resources!.read).toEqual([''])
    expect(conversationBinding('free_director').envelope!.resources!.write).toBeUndefined()

    // Explore-First Review: read-only exploration + team_spawn/team_message/team_wait/team_plan + tinadec_inspect, zero writes/apply.
    const reviewEffective = effectiveMeetingTools('review')
    for (const tool of ['ls', 'stat', 'read_file', 'file_search', 'git_status', 'git_diff', 'git_log', 'recall_conversation_history', 'team_spawn', 'team_message', 'team_wait', 'team_plan', 'tinadec_inspect']) {
      expect(reviewEffective, `review must enable ${tool}`).toContain(tool)
    }
    for (const tool of ['team_integrate', 'spec_propose', 'write_file', 'shell', 'tinadec_agent_apply', 'tinadec_context_memory_apply']) {
      expect(reviewEffective, `review must disable ${tool}`).not.toContain(tool)
    }
    expect(conversationBinding('review').envelope!.resources!.read).toEqual([''])

    // Explore-First Spec: read-only exploration + spec_propose + 5 team_* + tinadec_*, zero mutations on meeting.
    const specEffective = effectiveMeetingTools('spec')
    for (const tool of ['read_file', 'file_search', 'git_diff', 'spec_propose', ...TEAM_TOOLS, ...TINADEC_CONFIG_TOOLS]) {
      expect(specEffective, `spec must enable ${tool}`).toContain(tool)
    }
    expect(specEffective.some((tool) => WORKSPACE_MUTATING_TOOLS.has(tool))).toBe(false)
    expect(conversationBinding('spec').envelope!.resources!.read).toEqual([''])

    // Declared-edge modes (`vibe_graph`, `fixed_pipeline`): switch off all non-tinadec_* tools on meeting.
    const tinadecSet = new Set<string>(TINADEC_CONFIG_TOOLS)
    for (const slug of ['vibe_graph', 'fixed_pipeline']) {
      const switches = conversationBinding(slug).tool_switches ?? {}
      for (const tool of meeting.tool_scope) {
        if (tinadecSet.has(tool)) {
          expect(switches[tool], `${slug} keeps ${tool} enabled`).not.toBe(false)
        } else {
          expect(switches[tool], `${slug} switches ${tool} off`).toBe(false)
        }
      }
      expect(bySlug.get(slug)!.nodes).toHaveLength(3)
      expect(bySlug.get(slug)!.edges).toHaveLength(2)
    }

    // Unified conversation identity across all non-ArtEx modes.
    const conversationAgent = (key: string) => bySlug.get(key)?.nodes?.find((node) => node.config?.conversation === true)?.agent_ref
    for (const slug of ['solo', 'plan', 'free_director', 'vibe_graph', 'fixed_pipeline', 'review', 'spec', 'correction']) {
      expect(conversationAgent(slug), `${slug} conversation`).toBe('agent:meeting')
    }

    const expectedTiers: Record<string, string> = {
      free_director: 'free_form',
      vibe_graph: 'self_dispatch',
      fixed_pipeline: 'deterministic',
      solo: 'solo_dispatch',
      plan: 'solo_dispatch',
      review: 'free_form',
      spec: 'free_form',
      correction: 'solo_dispatch',
      artex: 'solo_dispatch',
    }
    for (const [slug, tier] of Object.entries(expectedTiers)) {
      const mode = bySlug.get(slug)!
      expect(mode.orchestration_tier, `${slug} orchestration_tier`).toBe(tier)
      expect(mode.default_options, `${slug} default_options`).toBeDefined()
    }

    // ModePublishGate invariant + zero stale tool_switches keys across EVERY binding in EVERY mode.
    const resourceKeys = new Set(graphSeedPackManifest.resources.agents.map((agent) => agent.resource_key))
    for (const mode of modes) {
      expect(mode.nodes.some((node) => node.layer === 'operation')).toBe(true)
      for (const node of mode.nodes) {
        expect(node.agent_ref).toMatch(/^agent:/)
        expect(resourceKeys.has(node.agent_ref.slice('agent:'.length))).toBe(true)
      }
      const nodeKeys = new Set(mode.nodes.map((node) => node.node_key))
      for (const edge of mode.edges) {
        expect(nodeKeys.has(edge.source_node_key), `${mode.resource_key} edge source`).toBe(true)
        expect(nodeKeys.has(edge.target_node_key), `${mode.resource_key} edge target`).toBe(true)
      }
      for (const binding of mode.bindings ?? []) {
        const agent = agentsByRef.get(binding.agent_ref)!
        expect(agent, `${mode.resource_key} unknown binding ${binding.agent_ref}`).toBeDefined()
        const switches = binding.tool_switches ?? {}
        for (const switchKey of Object.keys(switches)) {
          expect(agent.tool_scope, `${mode.resource_key} binding switches unknown tool '${switchKey}'`).toContain(switchKey)
        }
        const effective = agent.tool_scope.filter((tool) => switches[tool] !== false)
        const mutatingKept = effective.filter((tool) => WORKSPACE_MUTATING_TOOLS.has(tool))
        if (mutatingKept.length > 0) {
          expect(binding.envelope?.resources?.write?.length ?? 0, `${mode.resource_key}/${binding.node_key ?? binding.agent_ref} needs write grant for [${mutatingKept.join(', ')}]`).toBeGreaterThan(0)
        }
      }
    }
  })

  it('binds every mode to its own prompt pipeline with complete 5-locale non-stub templates (>80 chars)', () => {
    const modes = graphSeedPackManifest.resources.modes
    const pipelines = graphSeedPackManifest.resources.prompt_pipelines
    expect(pipelines.map((p) => p.resource_key)).toEqual([
      'seed-base',
      'solo-base',
      'vibe-base',
      'fixed-base',
      'free-base',
      'plan-base',
      'review-base',
      'spec-base',
      'correction-base',
      'artex-base',
    ])

    const promptKeys = new Set(pipelines.map((pipeline) => pipeline.resource_key))
    const bound = modes.map((mode) => {
      expect(mode.prompt_pipeline_ref, `mode '${mode.resource_key}' prompt_pipeline_ref`).toMatch(/^prompt:/)
      const key = mode.prompt_pipeline_ref!.slice('prompt:'.length)
      expect(promptKeys.has(key), `mode '${mode.resource_key}' references unknown pipeline '${key}'`).toBe(true)
      return key
    })
    expect(new Set(bound).size).toBe(modes.length)
    expect(bound).toEqual(['free-base', 'vibe-base', 'fixed-base', 'solo-base', 'plan-base', 'review-base', 'spec-base', 'correction-base', 'artex-base'])

    for (const pipeline of pipelines) {
      const templateNodes = pipeline.graph.nodes.filter((n) => (n as { type?: string }).type === 'template') as Array<{
        id: string
        config?: { content?: string }
      }>
      expect(templateNodes.length, `pipeline '${pipeline.resource_key}' template nodes`).toBeGreaterThan(0)
      for (const node of templateNodes) {
        expect(node.config?.content?.trim().length ?? 0, `${pipeline.resource_key}.${node.id}`).toBeGreaterThan(80)
      }
      for (const locale of REQUIRED_LOCALES) {
        const entry = pipeline.i18n?.[locale]
        expect(entry, `pipeline '${pipeline.resource_key}' missing locale ${locale}`).toBeDefined()
        expect(entry!.display_name?.trim().length ?? 0).toBeGreaterThan(0)
        expect(entry!.description?.trim().length ?? 0).toBeGreaterThan(15)
        for (const node of templateNodes) {
          const text = entry!.templates?.[node.id] ?? ''
          expect(text.trim().length, `pipeline '${pipeline.resource_key}'.${node.id} (${locale})`).toBeGreaterThan(80)
        }
      }
    }

    const contentOf = (key: string) => graphSeedPackManifest.resources.prompt_pipelines
      .find((pipeline) => pipeline.resource_key === key)!
      .graph.nodes
      .map((node) => (node as { config?: { content?: string } }).config?.content ?? '')
      .join('\n')

    // seed-base: shared core charter (evidence-first, recall_conversation_history, SessionPlan, tinadec_inspect, browser_screenshot/handoff).
    expect(contentOf('seed-base')).toContain('recall_conversation_history')
    expect(contentOf('seed-base')).toContain('SessionPlan')
    expect(contentOf('seed-base')).toContain('tinadec_inspect')
    expect(contentOf('seed-base')).toContain('browser_screenshot')
    expect(contentOf('seed-base')).toContain('browser_handoff')

    // free-base (Team): Explore-First + 5 orthogonal team_* primitives + WorktreeIntegratorService.
    for (const token of ['team_spawn', 'team_message', 'team_wait', 'team_plan', 'team_integrate', 'WorktreeIntegratorService', 'SessionPlan']) {
      expect(contentOf('free-base'), `free-base must mention ${token}`).toContain(token)
    }

    // solo-base: direct execution + optional ephemeral team_spawn + team_wait + team_plan + browser_run.
    for (const token of ['team_spawn', 'ephemeral', 'team_wait', 'team_plan', 'browser_run', '人工审批']) {
      expect(contentOf('solo-base'), `solo-base must mention ${token}`).toContain(token)
    }

    // plan-base: read-only + team_plan + switch to Solo / Team.
    expect(contentOf('plan-base')).toContain('已关闭')
    expect(contentOf('plan-base')).toContain('team_plan')
    expect(contentOf('plan-base')).toContain('切到 Solo')

    // review-base: independent read-only reviewers.
    expect(contentOf('review-base')).toContain('agent_slug="reviewer"')
    expect(contentOf('review-base')).toContain('独立判断')

    // spec-base: three specs + spec_propose + no write before confirmation.
    for (const token of ['requirements.md', 'design.md', 'tasks.md', 'spec_propose', '用户确认之前绝不落盘']) {
      expect(contentOf('spec-base'), `spec-base must mention ${token}`).toContain(token)
    }

    // correction-base: host-owned causal trace + same focused repro + user reproduction reopens.
    expect(contentOf('correction-base')).toContain('update_correction_trace')
    expect(contentOf('correction-base')).toContain('同一个聚焦 repro')
    expect(contentOf('correction-base')).toContain('用户报告仍复现')
  })

  it('carries all five relationship fields on every node and exact spawn whitelists', () => {
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

    const whitelists: Record<string, string[]> = {
      free_director: ['search', 'global_engineering', 'reviewer'],
      fixed_pipeline: ['search', 'global_engineering'],
      solo: ['search', 'global_engineering'],
      spec: ['search', 'global_engineering'],
      plan: ['search'],
      review: ['reviewer'],
      correction: ['search', 'global_engineering'],
    }
    for (const [key, whitelist] of Object.entries(whitelists)) {
      const mode = graphSeedPackManifest.resources.modes.find((row) => row.resource_key === key)!
      expect(mode.nodes[0].relationship!.agent_types, `${key} spawn whitelist`).toEqual(whitelist)
      expect(mode.nodes[0].relationship!.allowed_dispatch_targets, `${key} dispatch targets`).toEqual(whitelist)
    }

    const fixedPipeline = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'fixed_pipeline')!
    const fixedBinding = fixedPipeline.bindings!.find((binding) => binding.node_key === 'meeting')!
    expect(fixedBinding.envelope!.spawn!.max_depth).toBe(0)
  })

  it('lets the delegating modes nest one level deeper than a flat dispatch', () => {
    const depth = (key: string) => {
      const mode = graphSeedPackManifest.resources.modes.find((row) => row.resource_key === key)!
      return mode.bindings!.find((binding) => binding.node_key === 'meeting')!.envelope!.spawn!.max_depth
    }
    for (const key of ['free_director', 'review', 'correction']) expect(depth(key), `${key} nesting depth`).toBe(2)
    for (const key of ['vibe_graph', 'solo', 'plan', 'spec']) expect(depth(key), `${key} nesting depth`).toBe(1)
    expect(depth('fixed_pipeline')).toBe(0)
  })

  it('binds node-less envelopes for spawnable templates and configures Correction & ArtEx policies', () => {
    const spawnable: Record<string, string[]> = {
      free_director: ['agent:global_engineering', 'agent:reviewer', 'agent:search'],
      solo: ['agent:global_engineering', 'agent:search'],
      spec: ['agent:global_engineering', 'agent:search'],
      correction: ['agent:global_engineering', 'agent:search'],
      plan: ['agent:search'],
      review: ['agent:reviewer'],
    }
    for (const [key, templates] of Object.entries(spawnable)) {
      const mode = graphSeedPackManifest.resources.modes.find((row) => row.resource_key === key)!
      const nodeLess = mode.bindings!.filter((binding) => binding.node_key == null)
      expect(nodeLess.map((binding) => binding.agent_ref).sort(), `${key} node-less bindings`).toEqual(templates)
      for (const binding of nodeLess) {
        expect(binding.envelope!.resources!.read).toEqual([''])
        const expectedWrite = binding.agent_ref === 'agent:global_engineering' ? [''] : undefined
        expect(binding.envelope!.resources!.write, `${key} ${binding.agent_ref} write`).toEqual(expectedWrite)
      }
    }

    for (const mode of graphSeedPackManifest.resources.modes) {
      for (const binding of mode.bindings!.filter((item) => item.agent_ref === 'agent:global_engineering')) {
        expect(binding.tool_switches ?? {}, `${mode.resource_key} engineering switches`).toEqual({})
      }
    }

    const correction = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'correction')!
    expect(correction.runtime_policy).toEqual({
      kind: 'structured_investigation',
      version: 1,
      features: ['causal_trace', 'completion_contract', 'failing_repro_required', 'clarification_ledger', 'user_reproduction_reopens', 'local_hunk_revert'],
    })
    const correctionBinding = correction.bindings!.find((binding) => binding.node_key === 'meeting')!
    expect(correctionBinding.tool_switches).toEqual({ spec_propose: false, team_plan: false })

    const artex = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'artex')!
    expect(artex.runtime_policy?.kind).toBe('autonomous_exploration')
    expect(artex.runtime_policy?.version).toBe(1)
    for (const feature of ['exploration_graph', 'asset_graph', 'plan_ledger', 'event_driven_planning', 'cross_worker_trace',
      'worker_settlement', 'evidence_refs_required', 'merged_digest', 'structured_context_compaction', 'model_health_failover',
      'run_dossier', 'side_question']) {
      expect(artex.runtime_policy?.features, `artex feature ${feature}`).toContain(feature)
    }
    expect(artex.nodes.map((node) => node.node_key)).toEqual(['artex_director', 'artex_planner'])
    expect(artex.edges).toEqual([])
    expect(artex.nodes[0].config.conversation).toBe(true)
    expect(artex.nodes[1].relationship?.subscriptions).toEqual(['worker_settled', 'user_steer', 'plan_tick', 'budget_tick'])
    expect(artex.nodes[1].relationship?.allowed_dispatch_targets).toEqual(['artex_worker', 'artex_verifier'])

    const artexPlanner = graphSeedPackManifest.resources.agents.find((agent) => agent.resource_key === 'artex_planner')!
    expect(artexPlanner.capabilities).not.toContain('user.respond')
    expect(artexPlanner.tool_scope).toContain('plan_ledger_update')
    expect(artexPlanner.tool_scope).toContain('team_spawn')
    expect(artexPlanner.tool_scope).toContain('team_wait')
    expect(artexPlanner.tool_scope).toContain('recall_conversation_history')

    const artexVerifier = graphSeedPackManifest.resources.agents.find((agent) => agent.resource_key === 'artex_verifier')!
    for (const tool of ['write_file', 'shell', 'web_fetch', 'mcp_invoke', 'plan_ledger_update', 'team_spawn']) {
      expect(artexVerifier.tool_scope, `verifier must not hold ${tool}`).not.toContain(tool)
    }
  })

  it('grants Solo the workspace write and spawn room it needs to work directly', () => {
    const solo = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'solo')!
    const masterBinding = solo.bindings!.find((binding) => binding.node_key === 'meeting')!
    expect(masterBinding.agent_ref).toBe('agent:meeting')
    expect(masterBinding.envelope!.resources!.read).toEqual([''])
    expect(masterBinding.envelope!.resources!.write).toEqual([''])
    expect(masterBinding.envelope!.spawn!.max_depth).toBeGreaterThan(0)
    expect(masterBinding.envelope!.spawn!.max_agents_per_run).toBeGreaterThan(0)
  })

  it('keeps Plan strictly read-only while running on the unified conversation identity', () => {
    const plan = graphSeedPackManifest.resources.modes.find((mode) => mode.resource_key === 'plan')!
    const binding = plan.bindings!.find((item) => item.node_key === 'meeting')!
    expect(binding.agent_ref).toBe('agent:meeting')
    const meeting = graphSeedPackManifest.resources.agents.find((agent) => agent.resource_key === 'meeting')!
    const switches = binding.tool_switches ?? {}
    const effective = meeting.tool_scope.filter((tool) => switches[tool] !== false)
    expect(effective).toEqual([
      'ls',
      'stat',
      'read_file',
      'file_search',
      'git_status',
      'git_diff',
      'git_log',
      'read_attachment',
      'graph_view',
      'recall_evidence',
      'recall_conversation_history',
      'environment_list',
      'load_skill',
      'team_spawn',
      'team_wait',
      'team_plan',
      'tinadec_inspect',
    ])
    for (const tool of [
      'mcp_list',
      'mcp_search',
      'mcp_invoke',
      'tinadec_agent_apply',
      'tinadec_mode_apply',
      'tinadec_mode_publish',
      'tinadec_skill_apply',
      'tinadec_mcp_apply',
      'tinadec_prompt_apply',
      'tinadec_model_profile_apply',
      'tinadec_context_memory_apply',
      'team_integrate',
      'team_message',
      'spec_propose',
      'browser_run',
      'browser_navigate',
    ]) {
      expect(switches[tool], `plan switches ${tool} off`).toBe(false)
    }
    expect(binding.envelope!.resources!.read).toEqual([''])
    expect(binding.envelope!.resources!.write).toBeUndefined()
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
