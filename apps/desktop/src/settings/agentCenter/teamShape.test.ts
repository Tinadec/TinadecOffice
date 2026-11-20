import { describe, expect, it } from 'vitest'
import type { AgentModeTopologyDto, AgentViewDto } from '@/api'
import { buildTeamTopology, classifyTeamShape, cloneModeTopology, defaultMemberScope } from './teamShape'

function agent(id: string, slug: string, layer: 'operation' | 'execution', tools: string[] = []): AgentViewDto {
  return {
    id,
    slug,
    name: slug,
    layer,
    agent_type: slug,
    description: `${slug} description`,
    allowed_tools: tools,
    capabilities: [],
    model_strategy: {},
    enabled: true,
    is_built_in: true,
    status: 'published',
    revision: 1,
    version: 1,
  } as unknown as AgentViewDto
}

const leader = agent('leader', 'meeting', 'operation')
const search = agent('search', 'search', 'execution', ['read_file', 'file_search'])
const engineering = agent('eng', 'global_engineering', 'execution', ['read_file', 'write_file', 'shell'])
const agents = [leader, search, engineering]

function detail(overrides: Partial<AgentModeTopologyDto> = {}): AgentModeTopologyDto {
  return {
    id: 'mode-1',
    display_name: 'Team',
    nodes: [
      {
        id: 'node-leader',
        agent_id: leader.id,
        agent_definition_id: leader.id,
        lane: 'operation',
        position: { x: 0, y: 0 },
        config: { conversation: true },
        relationship: { agent_types: ['search', 'global_engineering'] },
      },
    ],
    edges: [],
    bindings: [{ agent_ref: 'search' }, { agent_ref: 'global_engineering' }],
    status: 'draft',
    ...overrides,
  } as AgentModeTopologyDto
}

describe('team shape', () => {
  it('recognizes a leader + spawnable members as an editable team', () => {
    const shape = classifyTeamShape(detail(), agents)
    expect(shape).toMatchObject({ style: 'team', leaderId: leader.id, editable: true })
    expect(shape.memberIds).toEqual([search.id, engineering.id])
  })

  it('recognizes a leader with no members as solo', () => {
    const shape = classifyTeamShape(detail({
      bindings: [],
      nodes: [{
        id: 'node-leader',
        agent_id: leader.id,
        agent_definition_id: leader.id,
        lane: 'operation',
        position: { x: 0, y: 0 },
        config: { conversation: true },
        relationship: {},
      }],
    }), agents)
    expect(shape).toMatchObject({ style: 'solo', leaderId: leader.id, editable: true })
    expect(shape.memberIds).toEqual([])
  })

  it('reads a single chain as an ordered pipeline', () => {
    const shape = classifyTeamShape(detail({
      nodes: [
        { id: 'l', agent_id: leader.id, agent_definition_id: leader.id, lane: 'operation', position: { x: 0, y: 0 }, config: { conversation: true } },
        { id: 'a', agent_id: search.id, agent_definition_id: search.id, lane: 'execution', position: { x: 1, y: 1 } },
        { id: 'b', agent_id: engineering.id, agent_definition_id: engineering.id, lane: 'execution', position: { x: 2, y: 2 } },
      ],
      edges: [
        { id: 'e1', source: 'l', target: 'a' },
        { id: 'e2', source: 'a', target: 'b' },
      ],
      bindings: [],
    }), agents)
    expect(shape).toMatchObject({ style: 'pipeline', editable: true })
    expect(shape.memberIds).toEqual([search.id, engineering.id])
  })

  it('refuses to flatten a graph with an extra operation node', () => {
    const shape = classifyTeamShape(detail({
      nodes: [
        { id: 'l', agent_id: leader.id, agent_definition_id: leader.id, lane: 'operation', position: { x: 0, y: 0 }, config: { conversation: true } },
        { id: 'g', agent_id: 'governance', agent_definition_id: 'governance', lane: 'operation', position: { x: 1, y: 0 } },
      ],
      bindings: [],
    }), agents)
    expect(shape.editable).toBe(false)
    expect(shape.reason).toBe('complex-topology')
  })

  it('builds a team whose leader sheds its tool face and whose members are node-less bindings', () => {
    const topology = buildTeamTopology({
      style: 'team',
      leaderId: leader.id,
      memberIds: [search.id, engineering.id],
      displayName: 'Team',
      description: 'Deliver the goal',
    }, agents)

    expect(topology.nodes).toHaveLength(1)
    expect(topology.nodes[0].config).toEqual({ conversation: true })
    expect((topology.nodes[0].relationship as Record<string, unknown>).agent_types)
      .toEqual(['search', 'global_engineering'])
    expect(topology.bindings?.[0].tool_switches).toEqual({ '*': false })
    expect(topology.bindings?.some(binding => binding.agent_ref === 'search')).toBe(true)
    expect(topology.edges).toEqual([])
  })

  it('builds a pipeline as ordered execution nodes and handoff edges', () => {
    const topology = buildTeamTopology({
      style: 'pipeline',
      leaderId: leader.id,
      memberIds: [search.id, engineering.id],
      displayName: 'Pipeline',
    }, agents)

    expect(topology.nodes).toHaveLength(3)
    expect(topology.edges).toHaveLength(2)
    expect(topology.edges[0].source_node_key).toBe('leader')
    expect(topology.edges[0].target_node_key).toBe('member-1')
    expect(topology.edges[1].source_node_key).toBe('member-1')
    expect(topology.edges[1].target_node_key).toBe('member-2')
    expect(topology.bindings?.[0].tool_switches).toEqual({ '*': false })
    expect((topology.bindings?.find(binding => binding.node_key === 'member-2'))?.envelope)
      .toMatchObject({ resources: { read: [''], write: [''] } })
  })

  it('honors per-member read/write scope when building and reading a team', () => {
    const topology = buildTeamTopology({
      style: 'team',
      leaderId: leader.id,
      memberIds: [search.id, engineering.id],
      memberScopes: { [search.id]: { read: true, write: false }, [engineering.id]: { read: true, write: true } },
      displayName: 'Scoped',
    }, agents)
    const searchBinding = topology.bindings?.find(binding => binding.agent_ref === 'search')
    expect(searchBinding?.envelope).toEqual({ resources: { read: [''] } })
    const engineeringBinding = topology.bindings?.find(binding => binding.agent_ref === 'global_engineering')
    expect(engineeringBinding?.envelope).toEqual({ resources: { read: [''], write: [''] } })

    const shape = classifyTeamShape(detail({
      bindings: [
        { agent_ref: 'search', envelope: { resources: { read: [''] } } },
        { agent_ref: 'global_engineering', envelope: { resources: { read: [''], write: [''] } } },
      ],
    }), agents)
    expect(shape.memberScopes?.[search.id]).toEqual({ read: true, write: false })
    expect(shape.memberScopes?.[engineering.id]).toEqual({ read: true, write: true })
  })

  it('freezes collaboration instructions and keeps them when cloning', () => {
    const topology = buildTeamTopology({
      style: 'team',
      leaderId: leader.id,
      memberIds: [search.id],
      displayName: 'Instructed',
      instructions: '  only the leader delegates  ',
    }, agents)
    expect(topology.instructions).toBe('only the leader delegates')

    const cloned = cloneModeTopology(detail({ instructions: 'keep the evidence trail' }))
    expect(cloned.instructions).toBe('keep the evidence trail')
  })

  it('maps member scopes by node_key instead of guessing a member index', () => {
    const shape = classifyTeamShape(detail({
      nodes: [
        { id: 'l', agent_id: leader.id, agent_definition_id: leader.id, lane: 'operation', position: { x: 0, y: 0 }, config: { conversation: true } },
        { id: 'w1', node_key: 'member-2', agent_id: search.id, agent_definition_id: search.id, lane: 'execution', position: { x: 1, y: 1 } },
        { id: 'w2', node_key: 'member-3', agent_id: engineering.id, agent_definition_id: engineering.id, lane: 'execution', position: { x: 2, y: 1 } },
      ],
      edges: [
        { id: 'e1', source: 'l', target: 'member-2' },
        { id: 'e2', source: 'member-2', target: 'member-3' },
      ],
      bindings: [
        { node_key: 'member-2', envelope: { resources: { read: [''] } } },
        { node_key: 'member-3', envelope: { resources: { read: [''], write: [''] } } },
      ],
    }), agents)
    expect(shape.memberScopes?.[search.id]).toEqual({ read: true, write: false })
    expect(shape.memberScopes?.[engineering.id]).toEqual({ read: true, write: true })
  })

  it('clones a topology without dropping relationships, bindings or edge conditions', () => {
    const source = detail({
      nodes: [
        {
          id: 'l',
          agent_id: leader.id,
          agent_definition_id: leader.id,
          lane: 'operation',
          position: { x: 0, y: 0 },
          config: { conversation: true },
          relationship: { duty: 'own it', agent_types: ['search'] },
        },
        { id: 'w', agent_id: search.id, agent_definition_id: search.id, lane: 'execution', position: { x: 1, y: 1 } },
      ],
      edges: [{ id: 'e', source: 'l', target: 'w', condition: { label: 'handoff' } }],
      bindings: [{ node_key: 'l', tool_switches: { '*': false } }, { agent_ref: 'search' }],
    })
    const cloned = cloneModeTopology(source)
    expect(cloned.nodes[0].relationship).toEqual({ duty: 'own it', agent_types: ['search'] })
    expect(cloned.edges[0].condition).toEqual({ label: 'handoff' })
    expect(cloned.bindings).toEqual([{ node_key: 'l', tool_switches: { '*': false } }, { agent_ref: 'search' }])
  })

  it('persists display_name, description, orchestration_tier and valid complete relationship objects', () => {
    const topology = buildTeamTopology({
      style: 'pipeline',
      leaderId: leader.id,
      memberIds: [search.id, engineering.id],
      displayName: '  Release Flow  ',
      description: '  Ship safely  ',
    }, agents)

    expect(topology.display_name).toBe('Release Flow')
    expect(topology.description).toBe('Ship safely')
    expect(topology.orchestration_tier).toBe('deterministic')

    for (const node of topology.nodes) {
      const rel = node.relationship as Record<string, unknown>
      expect(typeof rel.duty).toBe('string')
      expect(rel.inputs_outputs && typeof rel.inputs_outputs === 'object' && !Array.isArray(rel.inputs_outputs)).toBe(true)
      expect(Array.isArray(rel.allowed_dispatch_targets)).toBe(true)
      expect(Array.isArray(rel.success_criteria)).toBe(true)
      expect(Array.isArray(rel.agent_types)).toBe(true)
    }
  })

  it('recognizes tool_scope write tools and wildcard in defaultMemberScope', () => {
    expect(defaultMemberScope({ tool_scope: '*' })).toEqual({ read: true, write: true })
    expect(defaultMemberScope({ tool_scope: ['read_file', 'write_file'] })).toEqual({ read: true, write: true })
    expect(defaultMemberScope({ tool_scope: ['read_file', 'file_search'] })).toEqual({ read: true, write: false })
  })

  it('losslessly preserves runtime_policy, canvas_layout, model_strategy_override, and unknown bindings on team update', () => {
    const existing = detail({
      orchestration_tier: 'self_dispatch',
      runtime_policy: { kind: 'structured_investigation', version: 1, features: ['evidence_gate'] },
      canvas_layout: { zoom: 0.9, viewport: { x: 10, y: 20 } },
      nodes: [
        {
          id: 'leader',
          node_key: 'leader',
          agent_id: leader.id,
          agent_definition_id: leader.id,
          lane: 'operation',
          position: { x: 140, y: 60 },
          config: { conversation: true, customFlag: 1 },
          model_strategy_override: { kind: 'route', route_purpose: 'reasoning' },
          relationship: { duty: 'Custom duty', agent_types: ['search'], subscriptions: ['lease_conflict'] },
        },
        {
          id: 'member-1',
          node_key: 'member-1',
          agent_id: search.id,
          agent_definition_id: search.id,
          lane: 'execution',
          position: { x: 200, y: 300 },
          model_strategy_override: { kind: 'fixed', provider_instance_id: 'p1', model: 'm1' },
        },
      ],
      edges: [{ id: 'e1', source: 'leader', target: 'member-1', condition: { label: 'custom-handoff' } }],
      bindings: [
        { node_key: 'leader', tool_switches: { '*': false }, duty_description_ref: 'custom://leader', envelope: { custom: true } },
        { node_key: 'member-1', duty_description_ref: 'custom://member-1', envelope: { resources: { read: [''] }, lease: 'ro' } },
        { node_key: 'audit-hook', tool_switches: { read_file: true } },
      ],
    })

    const updated = buildTeamTopology({
      style: 'pipeline',
      leaderId: leader.id,
      memberIds: [search.id, engineering.id],
      displayName: 'Updated Pipeline',
      existingDetail: existing,
    }, agents)

    expect(updated.orchestration_tier).toBe('self_dispatch')
    expect(updated.runtime_policy).toEqual(existing.runtime_policy)
    expect(updated.canvas_layout).toEqual(existing.canvas_layout)
    expect(updated.nodes[0].model_strategy_override).toEqual({ kind: 'route', route_purpose: 'reasoning' })
    expect(updated.nodes[0].config).toEqual({ conversation: true, customFlag: 1 })
    expect((updated.nodes[0].relationship as Record<string, unknown>).subscriptions).toEqual(['lease_conflict'])
    expect(updated.nodes[1].model_strategy_override).toEqual({ kind: 'fixed', provider_instance_id: 'p1', model: 'm1' })
    expect(updated.edges[0].condition).toEqual({ label: 'custom-handoff' })
    expect(updated.bindings?.find(b => b.node_key === 'leader')?.duty_description_ref).toBe('custom://leader')
    expect(updated.bindings?.find(b => b.node_key === 'leader')?.envelope).toMatchObject({ custom: true })
    expect(updated.bindings?.find(b => b.node_key === 'member-1')?.envelope).toMatchObject({ lease: 'ro', resources: { read: [''] } })
    expect(updated.bindings?.find(b => b.node_key === 'audit-hook')).toEqual({ node_key: 'audit-hook', tool_switches: { read_file: true } })
  })

  it('strips wildcard tool switch-off when switching back to solo style', () => {
    const existing = detail({
      bindings: [{ node_key: 'node-leader', tool_switches: { '*': false, custom_tool: true } }],
    })
    const solo = buildTeamTopology({
      style: 'solo',
      leaderId: leader.id,
      memberIds: [],
      displayName: 'Solo',
      existingDetail: existing,
    }, agents)
    expect(solo.orchestration_tier).toBe('solo_dispatch')
    expect(solo.bindings?.[0].tool_switches).toEqual({ custom_tool: true })
  })

  it('keeps relationship keys the team view does not model, on the leader and on pipeline members', () => {
    // The team view owns five relationship keys, but the graph editor can write
    // others. Because a mode write replaces the whole RelationshipJson, rebuilding
    // the object from a whitelist silently deleted them on every team save.
    const advanced = {
      duty: 'custom duty',
      inputs_outputs: { receives: 'goal', returns: 'answer' },
      allowed_dispatch_targets: ['search'],
      success_criteria: ['evidence'],
      agent_types: ['search'],
      subscriptions: ['lease_conflict'],
      per_node_budget: { tokens: 1200 },
    }
    const existing = detail({
      nodes: [
        {
          id: 'leader',
          node_key: 'leader',
          agent_id: leader.id,
          agent_definition_id: leader.id,
          lane: 'operation',
          position: { x: 60, y: 60 },
          config: { conversation: true },
          relationship: advanced,
        },
        {
          id: 'member-1',
          node_key: 'member-1',
          agent_id: search.id,
          agent_definition_id: search.id,
          lane: 'execution',
          position: { x: 60, y: 280 },
          relationship: { duty: 'stage one', stage_hint: 'keep me' },
        },
      ],
      edges: [{ id: 'e1', source: 'leader', target: 'member-1' }],
      bindings: [],
    })

    const updated = buildTeamTopology({
      style: 'pipeline',
      leaderId: leader.id,
      memberIds: [search.id, engineering.id],
      displayName: 'Kept',
      existingDetail: existing,
    }, agents)

    const leaderRel = updated.nodes[0].relationship as Record<string, unknown>
    expect(leaderRel.subscriptions).toEqual(['lease_conflict'])
    expect(leaderRel.per_node_budget).toEqual({ tokens: 1200 })
    // The keys this view owns are still rewritten from the selection.
    expect(leaderRel.allowed_dispatch_targets).toEqual(['search', 'global_engineering'])
    expect(leaderRel.duty).toBe('custom duty')

    const memberRel = updated.nodes[1].relationship as Record<string, unknown>
    expect(memberRel.stage_hint).toBe('keep me')
  })
})
