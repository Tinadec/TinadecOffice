import type {
  AgentModeBindingDto,
  AgentModeDraftUpdate,
  AgentModeEdgeDto,
  AgentModeNodeDto,
  AgentModeTopologyDto,
  AgentOrchestrationTier,
  AgentViewDto,
} from '@/api'

/**
 * A "team" is the shallow projection of a Mode: one conversation leader plus
 * members. The same ModeVersion is the deep graph editor's object, so this
 * module only translates between the two views and never invents a second
 * persistence shape.
 */
export type TeamStyle = 'solo' | 'team' | 'pipeline'

export interface MemberScope {
  read: boolean
  write: boolean
}

export interface TeamSelection {
  style: TeamStyle
  leaderId: string
  memberIds: string[]
}

export interface TeamShape extends TeamSelection {
  editable: boolean
  reason?: 'no-conversation' | 'multiple-leaders' | 'complex-topology'
  memberScopes?: Record<string, MemberScope>
}

const WRITE_TOOLS = new Set([
  'write_file', 'shell', 'command_run', 'git_commit', 'git_push', 'git_stage', 'git_unstage',
  'insert_line', 'replace_lines', 'delete_line',
])

function nodeLayer(node: AgentModeNodeDto): 'operation' | 'execution' {
  return (node.layer ?? node.lane ?? 'operation') === 'execution' ? 'execution' : 'operation'
}

function nodeAgentId(node: AgentModeNodeDto): string {
  return node.agent_definition_id ?? node.agent_id ?? ''
}

function nodeKey(node: AgentModeNodeDto): string {
  return node.node_key ?? node.id
}

function stripAgentPrefix(ref: string): string {
  return ref.startsWith('agent:') ? ref.slice('agent:'.length) : ref
}

function isConversationNode(node: AgentModeNodeDto, agentBySlug: Map<string, AgentViewDto>): boolean {
  if (node.config?.conversation === true) return true
  const agent = agentBySlug.get(nodeAgentId(node)) ?? agentBySlug.get(node.agent_id ?? '')
  return agent?.slug === 'meeting'
}

export interface AgentSlugSource {
  id: string
  slug?: string | null
  name?: string | null
}

export interface AgentToolSource {
  allowed_tools?: string[] | null
  tool_scope?: string[] | string | null
}

export function agentSlugOf(agent: AgentSlugSource): string {
  return agent.slug ?? agent.name ?? agent.id
}

function canWrite(agent: AgentToolSource): boolean {
  if (agent.tool_scope === '*') return true
  const scopeTools = Array.isArray(agent.tool_scope) ? agent.tool_scope : []
  const tools = [...(agent.allowed_tools ?? []), ...scopeTools]
  return tools.includes('*') || tools.some(tool => WRITE_TOOLS.has(tool))
}

export function defaultMemberScope(agent: AgentToolSource): MemberScope {
  return { read: true, write: canWrite(agent) }
}

export function memberEnvelope(agent: AgentToolSource, scope?: MemberScope): Record<string, unknown> {
  const effective = scope ?? defaultMemberScope(agent)
  const resources: Record<string, string[]> = {}
  if (effective.read) resources.read = ['']
  if (effective.write) resources.write = ['']
  return { resources }
}

function scopeOfBinding(binding: AgentModeBindingDto | undefined, fallback: MemberScope): MemberScope {
  const resources = binding?.envelope?.resources as { read?: unknown; write?: unknown } | undefined
  const has = (value: unknown) => Array.isArray(value) && value.length > 0
  if (!resources) return fallback
  return { read: has(resources.read), write: has(resources.write) }
}

function relationshipTypes(value: unknown): string[] {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return []
  const types = (value as Record<string, unknown>).agent_types
  return Array.isArray(types) ? types.filter((item): item is string => typeof item === 'string') : []
}

export function naturalTierForStyle(style: TeamStyle): AgentOrchestrationTier {
  if (style === 'solo') return 'solo_dispatch'
  if (style === 'pipeline') return 'deterministic'
  return 'free_form'
}

/**
 * Reads the editable team shape out of a Mode detail. Anything with branching,
 * a second leader, extra operation nodes or non-chain execution graphs is
 * left to the graph editor instead of being flattened and losing facts.
 */
export function classifyTeamShape(
  detail: AgentModeTopologyDto,
  agents: AgentViewDto[],
): TeamShape {
  const byId = new Map(agents.map(agent => [agent.id, agent]))
  const bySlug = new Map(agents.map(agent => [agentSlugOf(agent), agent]))
  const nodes = detail.nodes ?? []
  const edges = detail.edges ?? []
  const conversation = nodes.filter(node => nodeLayer(node) === 'operation' && isConversationNode(node, bySlug))
  if (conversation.length === 0) {
    return { style: 'solo', leaderId: '', memberIds: [], editable: false, reason: 'no-conversation' }
  }
  if (conversation.length > 1) {
    return { style: 'team', leaderId: nodeAgentId(conversation[0]), memberIds: [], editable: false, reason: 'multiple-leaders' }
  }
  const leader = conversation[0]
  const extraOperationNodes = nodes.some(node =>
    nodeLayer(node) === 'operation' && node !== leader)
  if (extraOperationNodes) {
    return { style: 'team', leaderId: nodeAgentId(leader), memberIds: [], editable: false, reason: 'complex-topology' }
  }

  const executionNodes = nodes.filter(node => nodeLayer(node) === 'execution')
  const bindings = detail.bindings ?? []
  const nodeLessBindings = bindings.filter(binding => !binding.node_key && binding.agent_ref)
  const types = relationshipTypes(leader.relationship)
  const memberIds: string[] = []
  for (const node of executionNodes) {
    const id = nodeAgentId(node)
    if (id) memberIds.push(id)
  }
  for (const binding of nodeLessBindings) {
    const agent = bySlug.get(stripAgentPrefix(binding.agent_ref!))
    if (agent && !memberIds.includes(agent.id)) memberIds.push(agent.id)
  }
  // A spawnable member may be declared only through the relationship whitelist;
  // the graph editor can still render it even before a binding is written.
  for (const slug of types) {
    const agent = bySlug.get(stripAgentPrefix(slug))
    if (agent && !memberIds.includes(agent.id)) memberIds.push(agent.id)
  }

  const memberScopes: Record<string, MemberScope> = {}
  executionNodes.forEach((node) => {
    const id = nodeAgentId(node)
    const agent = byId.get(id)
    if (!id || !agent) return
    const binding = bindings.find(item => item.node_key === nodeKey(node))
    memberScopes[id] = scopeOfBinding(binding, defaultMemberScope(agent))
  })
  for (const binding of nodeLessBindings) {
    const agent = bySlug.get(stripAgentPrefix(binding.agent_ref!))
    if (agent) memberScopes[agent.id] = scopeOfBinding(binding, defaultMemberScope(agent))
  }

  if (edges.length > 0) {
    const chain = pipelineChain(leader, executionNodes, edges)
    if (!chain) {
      return { style: 'pipeline', leaderId: nodeAgentId(leader), memberIds, editable: false, reason: 'complex-topology' }
    }
    return { style: 'pipeline', leaderId: nodeAgentId(leader), memberIds: chain, memberScopes, editable: true }
  }
  if (executionNodes.length > 0) {
    return { style: 'pipeline', leaderId: nodeAgentId(leader), memberIds, memberScopes, editable: true }
  }
  if (nodeLessBindings.length > 0 || types.length > 0) {
    return { style: 'team', leaderId: nodeAgentId(leader), memberIds, memberScopes, editable: true }
  }
  return { style: 'solo', leaderId: nodeAgentId(leader), memberIds: [], memberScopes: {}, editable: true }
}

/** Returns the ordered execution-node member ids when the edges form one chain. */
function pipelineChain(
  leader: AgentModeNodeDto,
  executionNodes: AgentModeNodeDto[],
  edges: AgentModeEdgeDto[],
): string[] | null {
  if (executionNodes.length === 0) return null
  const byKey = new Map<string, AgentModeNodeDto>()
  byKey.set(nodeKey(leader), leader)
  for (const node of executionNodes) byKey.set(nodeKey(node), node)
  const next = new Map<string, string>()
  const incoming = new Map<string, number>()
  for (const edge of edges) {
    const source = edge.source_node_key ?? edge.source ?? ''
    const target = edge.target_node_key ?? edge.target ?? ''
    if (!byKey.has(source) || !byKey.has(target)) return null
    if (next.has(source)) return null
    next.set(source, target)
    incoming.set(target, (incoming.get(target) ?? 0) + 1)
    if ((incoming.get(target) ?? 0) > 1) return null
  }
  const result: string[] = []
  const seen = new Set<string>()
  let current = nodeKey(leader)
  while (next.has(current)) {
    const target = next.get(current)!
    if (seen.has(target)) return null
    seen.add(target)
    const node = byKey.get(target)!
    result.push(nodeAgentId(node))
    current = target
  }
  if (result.length !== executionNodes.length) return null
  return result
}

export interface TeamBuildInput extends TeamSelection {
  displayName: string
  description?: string
  /** Team-level collaboration instructions, frozen into the mode version. */
  instructions?: string
  orchestrationTier?: AgentOrchestrationTier | null
  memberScopes?: Record<string, MemberScope>
  /**
   * Existing topology when saving from TeamsTab so runtime_policy, canvas_layout,
   * node model_strategy_override, and unknown advanced bindings are preserved.
   */
  existingDetail?: AgentModeTopologyDto | null
}

/** Qoder's squad keeps the leader plus at most four additional members. */
export const MAX_TEAM_MEMBERS = 4

function validStringArray(value: unknown): string[] | null {
  if (!Array.isArray(value)) return null
  const filtered = value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
  return filtered.length > 0 ? filtered : null
}

function validInputsOutputs(value: unknown): Record<string, unknown> | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const record = value as Record<string, unknown>
  if (typeof record.receives === 'string' || typeof record.returns === 'string') return record
  return null
}

function validToolSwitches(switches: Record<string, boolean | string> | null | undefined, stripWildcardOff: boolean): Record<string, boolean | string> {
  if (!switches || typeof switches !== 'object') return {}
  const next: Record<string, boolean | string> = {}
  for (const [key, val] of Object.entries(switches)) {
    if (stripWildcardOff && key === '*' && val === false) continue
    next[key] = val
  }
  return next
}

/**
 * Builds the draft write payload for a team selection. Always emits complete,
 * valid `relationship` shapes (valid strings/objects/arrays), persists
 * `display_name`, `description`, and `orchestration_tier`, and losslessly
 * preserves advanced fields (`runtime_policy`, `canvas_layout`,
 * `model_strategy_override`, and unknown bindings) when `existingDetail` is given.
 */
export function buildTeamTopology(input: TeamBuildInput, agents: AgentViewDto[]): AgentModeDraftUpdate {
  const byId = new Map(agents.map(agent => [agent.id, agent]))
  const bySlug = new Map(agents.map(agent => [agentSlugOf(agent), agent]))
  const leader = byId.get(input.leaderId)
  const members = input.memberIds
    .map(id => byId.get(id))
    .filter((agent): agent is AgentViewDto => Boolean(agent))
  const memberSlugs = members.map(agentSlugOf)
  const isPipeline = input.style === 'pipeline'
  const isTeam = input.style === 'team'

  const existingNodes = input.existingDetail?.nodes ?? []
  const existingEdges = input.existingDetail?.edges ?? []
  const existingBindings = input.existingDetail?.bindings ?? []
  const existingLeader = existingNodes.find(n => nodeLayer(n) === 'operation' && isConversationNode(n, bySlug))
    ?? existingNodes.find(n => nodeLayer(n) === 'operation')
  const leaderKey = existingLeader ? nodeKey(existingLeader) : 'leader'
  const existingLeaderRel = (existingLeader?.relationship ?? null) as Record<string, unknown> | null
  const existingSubscriptions = Array.isArray(existingLeaderRel?.subscriptions)
    ? existingLeaderRel.subscriptions.filter((item): item is string => typeof item === 'string')
    : null

  const defaultLeaderDuty = isTeam
    ? 'Understand the user goal, dispatch independent subtasks to team members, and synthesize verified results.'
    : isPipeline
      ? 'Break down the goal, hand off along the declared pipeline stages, and consolidate verified outputs.'
      : 'Complete the user goal directly and verify evidence.'

  // The team view only models five relationship keys, but the graph editor can
  // write any others (subscriptions, per-node policy hints, future Core fields).
  // Spreading the existing object first keeps every unknown key; the explicit
  // keys below then overwrite only the ones this view owns. Without the spread a
  // team save silently deleted them, because the mode write replaces the whole
  // RelationshipJson.
  const leaderRelationship: Record<string, unknown> = {
    ...(existingLeaderRel ?? {}),
    duty: typeof existingLeaderRel?.duty === 'string' && existingLeaderRel.duty.trim()
      ? existingLeaderRel.duty
      : defaultLeaderDuty,
    inputs_outputs: validInputsOutputs(existingLeaderRel?.inputs_outputs) ?? {
      receives: 'user goal',
      returns: 'final answer',
    },
    allowed_dispatch_targets: memberSlugs,
    success_criteria: validStringArray(existingLeaderRel?.success_criteria) ?? [
      'The goal is completed and backed by evidence',
    ],
    agent_types: isTeam ? memberSlugs : [],
  }

  const leaderNode: AgentModeDraftUpdate['nodes'][number] = {
    node_key: leaderKey,
    agent_definition_id: input.leaderId,
    layer: 'operation',
    label: leader?.name ?? existingLeader?.label ?? 'Leader',
    position: existingLeader?.position ?? { x: 120, y: 60 },
    config: { ...(existingLeader?.config ?? {}), conversation: true },
    relationship: leaderRelationship,
    ...(existingLeader?.model_strategy_override ? { model_strategy_override: existingLeader.model_strategy_override } : {}),
  }

  const nodes: AgentModeDraftUpdate['nodes'] = [leaderNode]
  const edges: AgentModeDraftUpdate['edges'] = []

  const existingLeaderBinding = existingBindings.find(b => b.node_key === leaderKey)
  const existingLeaderEnvelope = (existingLeaderBinding?.envelope ?? {}) as Record<string, unknown>
  const leaderToolSwitches = input.style === 'solo'
    ? validToolSwitches(existingLeaderBinding?.tool_switches, true)
    : { ...validToolSwitches(existingLeaderBinding?.tool_switches, false), '*': false }

  const bindings: AgentModeBindingDto[] = [{
    ...(existingLeaderBinding ?? {}),
    node_key: leaderKey,
    tool_switches: leaderToolSwitches,
    envelope: {
      spawn: { max_depth: 2, max_agents_per_run: 8, max_parallel_workers: 4 },
      ...existingLeaderEnvelope,
    },
    includes_core_reserved: existingLeaderBinding?.includes_core_reserved ?? false,
  }]

  const existingExecutionNodes = existingNodes.filter(n => nodeLayer(n) === 'execution')
  const consumedExecutionKeys = new Set(existingExecutionNodes.map(nodeKey))

  if (isPipeline) {
    members.forEach((member, index) => {
      const key = `member-${index + 1}`
      const existingMemberNode = existingExecutionNodes.find(n => nodeKey(n) === key)
        ?? existingExecutionNodes.find(n => nodeAgentId(n) === member.id)
      const existingNodeKey = existingMemberNode ? nodeKey(existingMemberNode) : key
      const existingMemberBinding = existingBindings.find(b => b.node_key === key || b.node_key === existingNodeKey)
      const existingMemberRel = (existingMemberNode?.relationship ?? null) as Record<string, unknown> | null
      const nextTargetSlug = index < members.length - 1 ? [agentSlugOf(members[index + 1])] : []

      nodes.push({
        node_key: key,
        agent_definition_id: member.id,
        layer: 'execution',
        label: member.name ?? member.display_name ?? agentSlugOf(member),
        position: existingMemberNode?.position && existingMemberNode.position.y >= 240
          ? existingMemberNode.position
          : { x: 60 + index * 260, y: 280 },
        ...(existingMemberNode?.config ? { config: existingMemberNode.config } : {}),
        // Same rule as the leader: keep unknown relationship keys the graph
        // editor authored, overwrite only the ones the team view owns.
        relationship: {
          ...(existingMemberRel ?? {}),
          duty: (member.description ?? '').trim() || `Execute pipeline stage ${index + 1} as ${member.name || agentSlugOf(member)}.`,
          inputs_outputs: validInputsOutputs(existingMemberRel?.inputs_outputs) ?? {
            receives: index === 0 ? 'goal and constraints from leader' : 'artifact from previous stage',
            returns: index === members.length - 1 ? 'verified final deliverable' : 'stage output for next stage',
          },
          allowed_dispatch_targets: nextTargetSlug,
          success_criteria: validStringArray(existingMemberRel?.success_criteria) ?? [
            'Turn the incoming work into a verified result',
          ],
          agent_types: [],
        },
        ...(existingMemberNode?.model_strategy_override ? { model_strategy_override: existingMemberNode.model_strategy_override } : {}),
      })

      const env = memberEnvelope(member, input.memberScopes?.[member.id])
      bindings.push({
        ...(existingMemberBinding ?? {}),
        node_key: key,
        envelope: {
          ...(existingMemberBinding?.envelope ?? {}),
          ...env,
        },
        includes_core_reserved: existingMemberBinding?.includes_core_reserved ?? false,
      })

      const sourceKey = index === 0 ? leaderKey : `member-${index}`
      const existingEdge = existingEdges.find(e =>
        (e.source_node_key ?? e.source) === sourceKey && (e.target_node_key ?? e.target) === key)
      edges.push({
        edge_key: index === 0 ? `${leaderKey}-member-1` : `member-${index}-member-${index + 1}`,
        source_node_key: sourceKey,
        target_node_key: key,
        condition: existingEdge?.condition ?? { label: 'handoff' },
      })
    })
  } else if (isTeam) {
    for (const member of members) {
      const slug = agentSlugOf(member)
      const existingMemberBinding = existingBindings.find(
        b => !b.node_key && b.agent_ref && stripAgentPrefix(b.agent_ref) === slug,
      )
      const env = memberEnvelope(member, input.memberScopes?.[member.id])
      bindings.push({
        ...(existingMemberBinding ?? {}),
        agent_ref: slug,
        envelope: {
          ...(existingMemberBinding?.envelope ?? {}),
          ...env,
        },
        includes_core_reserved: existingMemberBinding?.includes_core_reserved ?? false,
      })
    }
  }

  // Preserve any unknown advanced bindings (e.g. auxiliary node_key bindings not
  // owned by leader/pipeline execution nodes, or unmapped unknown agent_ref bindings).
  for (const binding of existingBindings) {
    if (binding.node_key) {
      if (binding.node_key === leaderKey) continue
      if (consumedExecutionKeys.has(binding.node_key) || /^member-\d+$/.test(binding.node_key)) continue
      bindings.push({ ...binding })
    } else if (binding.agent_ref) {
      const refSlug = stripAgentPrefix(binding.agent_ref)
      if (!bySlug.has(refSlug)) {
        bindings.push({ ...binding })
      }
    }
  }

  let resolvedTier: AgentOrchestrationTier | null
  if (input.orchestrationTier !== undefined) {
    resolvedTier = input.orchestrationTier
  } else if (input.existingDetail) {
    const previousStyle = classifyTeamShape(input.existingDetail, agents).style
    resolvedTier = previousStyle === input.style && input.existingDetail.orchestration_tier !== undefined
      ? (input.existingDetail.orchestration_tier ?? naturalTierForStyle(input.style))
      : naturalTierForStyle(input.style)
  } else {
    resolvedTier = naturalTierForStyle(input.style)
  }

  const trimmedName = input.displayName.trim() || input.existingDetail?.display_name || 'Team'
  const trimmedDescription = input.description !== undefined
    ? (input.description.trim() || null)
    : (input.existingDetail?.description ?? null)
  const trimmedInstructions = input.instructions !== undefined
    ? (input.instructions.trim() || null)
    : (input.existingDetail?.instructions ?? null)

  return {
    display_name: trimmedName,
    description: trimmedDescription,
    orchestration_tier: resolvedTier,
    nodes,
    edges,
    bindings,
    instructions: trimmedInstructions,
    canvas_layout: input.existingDetail?.canvas_layout ?? {},
    ...(input.existingDetail?.runtime_policy !== undefined
      ? { runtime_policy: input.existingDetail.runtime_policy }
      : {}),
  }
}

/**
 * Lossless copy of a Mode detail into the draft write contract. Used when the
 * user clones a team (pack modes are read-only) or opens it in the graph editor.
 */
export function cloneModeTopology(detail: AgentModeTopologyDto): AgentModeDraftUpdate {
  return {
    display_name: detail.display_name,
    description: detail.description ?? null,
    ...(detail.orchestration_tier !== undefined ? { orchestration_tier: detail.orchestration_tier } : {}),
    nodes: (detail.nodes ?? []).map(node => ({
      node_key: nodeKey(node),
      agent_definition_id: nodeAgentId(node),
      layer: nodeLayer(node),
      label: node.label ?? null,
      position: node.position ?? null,
      config: node.config ?? null,
      relationship: node.relationship ?? null,
      model_strategy_override: (node.model_strategy_override ?? null) as Record<string, unknown> | null,
    })),
    edges: (detail.edges ?? []).map(edge => ({
      edge_key: edge.edge_key ?? edge.id ?? undefined,
      source_node_key: edge.source_node_key ?? edge.source ?? '',
      target_node_key: edge.target_node_key ?? edge.target ?? '',
      ...(edge.condition ? { condition: edge.condition } : {}),
    })),
    bindings: (detail.bindings ?? []).map(binding => ({ ...binding })),
    instructions: detail.instructions ?? null,
    canvas_layout: detail.canvas_layout ?? {},
    ...(detail.runtime_policy !== undefined ? { runtime_policy: detail.runtime_policy } : {}),
  }
}
