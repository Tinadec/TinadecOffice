import type {
  AgentModeBindingDto,
  AgentModeEdgeDto,
  AgentModeNodeDto,
  AgentModeTopologyDto,
  AgentModeTopologyWriteDto,
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
}

export function agentSlugOf(agent: AgentSlugSource): string {
  return agent.slug ?? agent.name ?? agent.id
}

function canWrite(agent: AgentToolSource): boolean {
  const tools = agent.allowed_tools ?? []
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

/**
 * Reads the editable team shape out of a Mode detail. Anything with branching,
 * a second leader, extra operation nodes or more than one binding per node is
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
    const agent = bySlug.get(binding.agent_ref!)
    if (agent && !memberIds.includes(agent.id)) memberIds.push(agent.id)
  }
  // A spawnable member may be declared only through the relationship whitelist;
  // the graph editor can still render it even before a binding is written.
  for (const slug of types) {
    const agent = bySlug.get(slug)
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
    const agent = bySlug.get(binding.agent_ref!)
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
  memberScopes?: Record<string, MemberScope>
}

/** Qoder's squad keeps the leader plus at most four additional members. */
export const MAX_TEAM_MEMBERS = 4

/**
 * Builds the draft topology for a team selection. The leader loses its business
 * tool face in every non-solo style (`"*": false`), which is what keeps the run
 * in the deterministic/free_form tier instead of solo_dispatch.
 */
export function buildTeamTopology(input: TeamBuildInput, agents: AgentViewDto[]): AgentModeTopologyWriteDto {
  const byId = new Map(agents.map(agent => [agent.id, agent]))
  const leader = byId.get(input.leaderId)
  const members = input.memberIds
    .map(id => byId.get(id))
    .filter((agent): agent is AgentViewDto => Boolean(agent))
  const leaderSlug = leader ? agentSlugOf(leader) : ''
  const memberSlugs = members.map(agentSlugOf)
  const isPipeline = input.style === 'pipeline'
  const isTeam = input.style === 'team'

  const leaderNode: AgentModeTopologyWriteDto['nodes'][number] = {
    node_key: 'leader',
    agent_definition_id: input.leaderId,
    layer: 'operation',
    label: leader?.name ?? 'Leader',
    position: { x: 120, y: 80 },
    config: { conversation: true },
    relationship: isTeam
      ? {
          agent_types: memberSlugs,
          allowed_dispatch_targets: memberSlugs,
          success_criteria: ['The goal is completed and backed by evidence'],
        }
      : {
          success_criteria: ['The goal is completed and backed by evidence'],
        },
  }

  const nodes: AgentModeTopologyWriteDto['nodes'] = [leaderNode]
  const edges: AgentModeTopologyWriteDto['edges'] = []
  const bindings: AgentModeBindingDto[] = [{
    node_key: 'leader',
    tool_switches: input.style === 'solo' ? {} : { '*': false },
    envelope: { spawn: { max_depth: 2, max_agents_per_run: 8, max_parallel_workers: 4 } },
    includes_core_reserved: false,
  }]

  if (isPipeline) {
    members.forEach((member, index) => {
      const key = `member-${index + 1}`
      nodes.push({
        node_key: key,
        agent_definition_id: member.id,
        layer: 'execution',
        label: member.name,
        position: { x: 360 + index * 240, y: 300 },
        relationship: {
          duty: member.description ?? '',
          success_criteria: ['Turn the incoming work into a verified result'],
        },
      })
      bindings.push({ node_key: key, envelope: memberEnvelope(member, input.memberScopes?.[member.id]), includes_core_reserved: false })
      edges.push({
        edge_key: index === 0 ? 'leader-member-1' : `member-${index}-member-${index + 1}`,
        source_node_key: index === 0 ? 'leader' : `member-${index}`,
        target_node_key: key,
        condition: { label: 'handoff' },
      })
    })
  } else if (isTeam) {
    for (const member of members) {
      bindings.push({
        agent_ref: agentSlugOf(member),
        envelope: memberEnvelope(member, input.memberScopes?.[member.id]),
        includes_core_reserved: false,
      })
    }
  }

  return {
    nodes,
    edges,
    bindings,
    instructions: input.instructions?.trim() || null,
    canvas_layout: {},
  }
}

/**
 * Lossless copy of a Mode detail into the draft write contract. Used when the
 * user clones a team (pack modes are read-only) or opens it in the graph editor.
 */
export function cloneModeTopology(detail: AgentModeTopologyDto): AgentModeTopologyWriteDto {
  return {
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
    ...(detail.runtime_policy ? { runtime_policy: detail.runtime_policy } : {}),
  }
}
