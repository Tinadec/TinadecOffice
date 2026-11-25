<script lang="ts">
import dagre from '@dagrejs/dagre'
import type {
  DeclaredGraphNodeDto,
  OrchestrationFlowDto,
  OrchestrationSnapshotDto,
  TaskNodeDto,
  ToolExecutionTimelineItemDto,
  TopologyInstanceDto,
  TopologyRunDto,
  TopologyTaskDto,
} from '../api'
import { pickNodeLabel } from '../lib/packI18n'

export type RuntimeTopologyRoleTier = 'coordinator' | 'worker' | 'subagent'
export type RuntimeTopologyStatus = 'running' | 'awaiting_approval' | 'completed' | 'failed' | 'queued'
export type RuntimeEngineKind = 'acp' | 'cli' | 'tui' | 'api'

export interface RuntimeEngineBadge {
  label: string
  kind: RuntimeEngineKind
}

export interface RuntimeEngineMetaInput {
  runtime_badge?: string | null
  runtimeBadge?: string | null
  protocol?: string | null
  channel?: string | null
  connection_kind?: string | null
  connectionKind?: string | null
  provider?: string | null
  provider_name?: string | null
  providerName?: string | null
  provider_display_name?: string | null
  providerDisplayName?: string | null
  driver?: string | null
  model?: string | null
  model_id?: string | null
  modelId?: string | null
  provider_instance_id?: string | null
  providerInstanceId?: string | null
  recent_actual_model?: {
    protocol?: string | null
    provider_instance_id?: string | null
    model?: string | null
  } | null
}

const KNOWN_DRIVER_LABELS: Record<string, string> = {
  antigravity: 'Antigravity',
  'claude-code': 'Claude Code',
  claude_code: 'Claude Code',
  claude: 'Claude Code',
  codex: 'Codex',
  'codex-cli': 'Codex',
  'gemini-cli': 'Gemini CLI',
  gemini: 'Gemini CLI',
  opencode: 'OpenCode',
  openai: 'OpenAI',
  'openai-chat': 'OpenAI',
  'openai-responses': 'OpenAI',
  anthropic: 'Anthropic',
  'anthropic-messages': 'Anthropic',
}

function humanizeEngineDriver(driver: string): string {
  const key = driver.trim().toLowerCase()
  if (!key) return ''
  if (KNOWN_DRIVER_LABELS[key]) return KNOWN_DRIVER_LABELS[key]!
  return driver
    .trim()
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((part) => (part.length <= 3 ? part.toUpperCase() : part[0]!.toUpperCase() + part.slice(1)))
    .join(' ')
}

export function classifyRuntimeEngineKind(rawLabelOrProtocol?: string | null): RuntimeEngineKind {
  const head = String(rawLabelOrProtocol ?? '').trim().toLowerCase()
  if (head.startsWith('acp') || head.includes('acp')) return 'acp'
  if (head.startsWith('tui') || head.includes('tui')) return 'tui'
  if (head.startsWith('cli') || head.includes('cli') || head.includes('headless')) return 'cli'
  return 'api'
}

export function formatRuntimeEngineBadge(
  meta?: RuntimeEngineMetaInput | null,
  providerLookup?: ReadonlyMap<string, {
    driver?: string | null
    protocol?: string | null
    channel?: string | null
    display_name?: string | null
    connection_kind?: string | null
    model?: string | null
  }>,
): RuntimeEngineBadge | null {
  if (!meta) return null
  const explicit = (meta.runtime_badge ?? meta.runtimeBadge ?? '').trim()
  if (explicit) {
    const normalized = explicit.replace(/\s*[·•|/]\s*/g, ' · ')
    return {
      label: normalized,
      kind: classifyRuntimeEngineKind(normalized),
    }
  }

  const providerId = (meta.provider_instance_id ?? meta.providerInstanceId ?? meta.recent_actual_model?.provider_instance_id ?? '').trim()
  const providerRow = providerId && providerLookup ? providerLookup.get(providerId) : undefined

  const channel = (meta.channel ?? providerRow?.channel ?? '').trim().toLowerCase()
  const protocol = (meta.protocol ?? meta.recent_actual_model?.protocol ?? providerRow?.protocol ?? '').trim().toLowerCase()
  const connKind = (meta.connection_kind ?? meta.connectionKind ?? providerRow?.connection_kind ?? '').trim().toLowerCase()
  const driver = (meta.driver ?? providerRow?.driver ?? '').trim()
  const displayName = (
    meta.provider_display_name
    ?? meta.providerDisplayName
    ?? meta.provider_name
    ?? meta.providerName
    ?? meta.provider
    ?? providerRow?.display_name
    ?? ''
  ).trim()
  const model = (meta.model ?? meta.model_id ?? meta.modelId ?? meta.recent_actual_model?.model ?? providerRow?.model ?? '').trim()

  let prefix: string | null = null
  let kind: RuntimeEngineKind = 'api'
  if (channel === 'acp' || protocol === 'acp' || protocol.includes('acp')) {
    prefix = 'ACP'
    kind = 'acp'
  } else if (channel === 'tui' || protocol === 'tui' || protocol.includes('tui')) {
    prefix = 'TUI'
    kind = 'tui'
  } else if (channel === 'cli' || connKind === 'cli' || protocol === 'cli' || protocol.includes('cli') || protocol.includes('headless')) {
    prefix = 'CLI'
    kind = 'cli'
  } else if (protocol || channel === 'http' || channel === 'api' || driver || displayName) {
    prefix = 'API'
    kind = 'api'
  }

  if (!prefix) return null
  const name = displayName || humanizeEngineDriver(driver) || model
  if (!name) return { label: prefix, kind }
  if (name.toUpperCase().startsWith(`${prefix} ·`) || name.toUpperCase() === prefix) {
    return { label: name, kind }
  }
  return { label: `${prefix} · ${name}`, kind }
}

export interface RuntimeTopologyNode {
  id: string
  roleTier: RuntimeTopologyRoleTier
  depth: number
  label: string
  subtitle?: string | null
  runtimeBadge?: string | null
  runtimeKind?: RuntimeEngineKind | null
  status: RuntimeTopologyStatus
  rawStatus: string
  parentId?: string | null
  taskId?: string | null
  taskKey?: string | null
  instanceIds: string[]
  handle?: string | null
  agentSlug?: string | null
  laneKey?: string | null
  title?: string | null
  description?: string | null
  resultSummary?: string | null
  writeScope: string[]
  dependencies: string[]
  via?: 'plan' | 'task_dispatch'
}

export interface RuntimeTopologyEdge {
  id: string
  source: string
  target: string
  via: 'plan' | 'task_dispatch'
  status: RuntimeTopologyStatus
  animated: boolean
}

export interface BuildRuntimeTopologyInput {
  snapshot: OrchestrationSnapshotDto | null
  topologyRun?: TopologyRunDto | null
  toolExecutions?: ToolExecutionTimelineItemDto[]
  coordinatorFallbackLabel: string
  locale?: string
  resolveAgentName?: (slugOrHandle: string | null | undefined, fallback?: string | null) => string
}

export const MINI_NODE_WIDTH = 208
export const MINI_NODE_HEIGHT = 72

let nextMiniGraphInstance = 0

export function normalizeTopologyStatus(rawStatus?: string | null, hasPendingApproval = false): RuntimeTopologyStatus {
  if (hasPendingApproval) return 'awaiting_approval'
  const status = String(rawStatus ?? '').trim().toLowerCase()
  if (
    status === 'awaiting_approval'
    || status === 'waiting_approval'
    || status === 'awaiting_user'
    || status === 'awaiting_delegate'
    || status === 'gate_review'
  ) {
    return 'awaiting_approval'
  }
  if (
    status === 'running'
    || status === 'executing'
    || status === 'in_progress'
    || status === 'in-progress'
    || status === 'planning'
    || status === 'understanding'
    || status === 'replanning'
    || status === 'reviewing'
    || status === 'finalizing'
    || status === 'assigned'
  ) {
    return 'running'
  }
  if (status === 'completed' || status === 'done' || status === 'succeeded') {
    return 'completed'
  }
  if (status === 'failed' || status === 'error' || status === 'cancelled' || status === 'blocked') {
    return 'failed'
  }
  return 'queued'
}

interface RawSnapshotAgentInstance extends RuntimeEngineMetaInput {
  id?: string
  instance_id?: string
  parent_instance_id?: string | null
  task_id?: string | null
  layer?: string | null
  role?: string | null
  agent_slug?: string | null
  agent_display_name?: string | null
  generation_depth?: number
  depth?: number
  status?: string | null
}

interface RawSnapshotTeammate extends RuntimeEngineMetaInput {
  handle?: string | null
  agent_slug?: string | null
  agentSlug?: string | null
  task_id?: string | null
  taskId?: string | null
}

function collectRuntimeBadges(
  snapshot: OrchestrationSnapshotDto | null,
  topologyRun: TopologyRunDto | null,
): {
  byTaskId: Map<string, RuntimeEngineBadge>
  byTaskKey: Map<string, RuntimeEngineBadge>
  byInstanceId: Map<string, RuntimeEngineBadge>
  byHandle: Map<string, RuntimeEngineBadge>
  bySlug: Map<string, RuntimeEngineBadge>
} {
  const byTaskId = new Map<string, RuntimeEngineBadge>()
  const byTaskKey = new Map<string, RuntimeEngineBadge>()
  const byInstanceId = new Map<string, RuntimeEngineBadge>()
  const byHandle = new Map<string, RuntimeEngineBadge>()
  const bySlug = new Map<string, RuntimeEngineBadge>()

  const put = (
    badge: RuntimeEngineBadge | null,
    keys: { taskId?: string | null; taskKey?: string | null; instanceId?: string | null; handle?: string | null; slug?: string | null },
  ) => {
    if (!badge) return
    if (keys.taskId && !byTaskId.has(keys.taskId)) byTaskId.set(keys.taskId, badge)
    if (keys.taskKey && !byTaskKey.has(keys.taskKey)) byTaskKey.set(keys.taskKey, badge)
    if (keys.instanceId && !byInstanceId.has(keys.instanceId)) byInstanceId.set(keys.instanceId, badge)
    if (keys.handle && !byHandle.has(keys.handle)) byHandle.set(keys.handle, badge)
    if (keys.slug && !bySlug.has(keys.slug)) bySlug.set(keys.slug, badge)
  }

  const snapAny = snapshot as unknown as {
    teammates?: RawSnapshotTeammate[]
    agent_instances?: RawSnapshotAgentInstance[]
  } | null

  for (const tm of snapAny?.teammates ?? []) {
    put(formatRuntimeEngineBadge(tm), {
      taskId: tm.task_id ?? tm.taskId ?? null,
      handle: tm.handle ?? null,
      slug: tm.agent_slug ?? tm.agentSlug ?? null,
    })
  }

  const topoAny = topologyRun as unknown as {
    teammates?: RawSnapshotTeammate[]
  } | null
  for (const tm of topoAny?.teammates ?? []) {
    put(formatRuntimeEngineBadge(tm), {
      taskId: tm.task_id ?? tm.taskId ?? null,
      handle: tm.handle ?? null,
      slug: tm.agent_slug ?? tm.agentSlug ?? null,
    })
  }

  for (const task of topologyRun?.tasks ?? []) {
    const raw = task as unknown as RuntimeEngineMetaInput
    put(formatRuntimeEngineBadge(raw), {
      taskId: task.task_id,
      taskKey: task.task_key,
      instanceId: task.worker_instance_id ?? null,
      handle: task.handle ?? null,
      slug: task.agent_slug ?? null,
    })
  }

  for (const inst of topologyRun?.instances ?? []) {
    const raw = inst as unknown as RuntimeEngineMetaInput
    put(formatRuntimeEngineBadge(raw), {
      instanceId: inst.instance_id,
      taskId: inst.task_id ?? null,
      slug: inst.agent_slug ?? (inst.role || null),
    })
  }

  for (const node of snapshot?.nodes ?? []) {
    const raw = node as unknown as RuntimeEngineMetaInput & { task_key?: string | null; handle?: string | null }
    put(formatRuntimeEngineBadge(raw), {
      taskId: node.id,
      taskKey: raw.task_key ?? null,
      handle: raw.handle ?? null,
    })
  }

  for (const inst of snapAny?.agent_instances ?? []) {
    const id = inst.instance_id ?? inst.id
    put(formatRuntimeEngineBadge(inst), {
      instanceId: id ? String(id) : null,
      taskId: inst.task_id ? String(inst.task_id) : null,
      slug: inst.agent_slug ?? inst.role ?? null,
    })
  }

  for (const assign of snapshot?.assignments ?? []) {
    const raw = assign as unknown as RuntimeEngineMetaInput & { task_node_id?: string; agent_instance_id?: string; role?: string }
    put(formatRuntimeEngineBadge(raw), {
      taskId: raw.task_node_id ? String(raw.task_node_id) : null,
      instanceId: raw.agent_instance_id ?? null,
      slug: raw.role ?? null,
    })
  }

  for (const flow of snapshot?.flows ?? []) {
    const raw = flow as unknown as RuntimeEngineMetaInput
    put(formatRuntimeEngineBadge(raw), {
      taskKey: flow.task_key,
      handle: flow.handle ?? null,
      slug: flow.to || null,
    })
  }

  return { byTaskId, byTaskKey, byInstanceId, byHandle, bySlug }
}

interface RawWorkItem {
  nodeId: string
  taskId: string | null
  taskKey: string | null
  instanceId: string | null
  parentInstanceId: string | null
  dispatchedByTaskId: string | null
  dispatcherHint: string | null
  handle: string | null
  agentSlug: string | null
  agentDisplayName: string | null
  laneKey: string | null
  rawStatus: string
  title: string | null
  description: string | null
  resultSummary: string | null
  writeScope: string[]
  dependencies: string[]
  via: 'plan' | 'task_dispatch'
}

function readSnapshotInstances(snapshot: OrchestrationSnapshotDto | null): TopologyInstanceDto[] {
  if (!snapshot) return []
  const raw = (snapshot as unknown as { agent_instances?: unknown }).agent_instances
  if (!Array.isArray(raw)) return []
  const out: TopologyInstanceDto[] = []
  for (const item of raw as RawSnapshotAgentInstance[]) {
    const id = item.instance_id ?? item.id
    if (!id) continue
    out.push({
      instance_id: String(id),
      agent_slug: item.agent_slug ?? item.role ?? null,
      layer: String(item.layer ?? 'execution'),
      role: String(item.role ?? ''),
      status: String(item.status ?? 'created'),
      parent_instance_id: item.parent_instance_id ? String(item.parent_instance_id) : null,
      task_id: item.task_id ? String(item.task_id) : null,
      depth: Number(item.depth ?? item.generation_depth ?? 0),
    })
  }
  return out
}

function readSnapshotDisplayNames(snapshot: OrchestrationSnapshotDto | null): Map<string, string> {
  const names = new Map<string, string>()
  if (!snapshot) return names
  const rawInstances = (snapshot as unknown as { agent_instances?: RawSnapshotAgentInstance[] }).agent_instances
  if (Array.isArray(rawInstances)) {
    for (const item of rawInstances) {
      const id = item.instance_id ?? item.id
      if (id && item.agent_display_name) names.set(String(id), item.agent_display_name)
      if (item.task_id && item.agent_display_name) names.set(String(item.task_id), item.agent_display_name)
    }
  }
  for (const assign of snapshot.assignments ?? []) {
    const rawAssign = assign as unknown as {
      task_node_id?: string
      agent_name?: string
      agent_display_name?: string
    }
    const label = rawAssign.agent_display_name || rawAssign.agent_name
    if (rawAssign.task_node_id && label) names.set(String(rawAssign.task_node_id), label)
  }
  for (const node of snapshot.nodes ?? []) {
    const rawNode = node as unknown as { agent_display_name?: string | null }
    if (node.id && rawNode.agent_display_name) names.set(node.id, rawNode.agent_display_name)
  }
  return names
}

/**
 * Projects the active run's 2-layer nested runtime tree (`Coordinator -> Worker -> Subagent`)
 * from Core's topology/checkpoint lineage (`dispatched_by_task_id`, `parent_instance_id`,
 * `flows` with `via: 'plan' | 'task_dispatch'`, and `nodes`).
 */
export function buildRuntimeTopology(input: BuildRuntimeTopologyInput): {
  nodes: RuntimeTopologyNode[]
  edges: RuntimeTopologyEdge[]
} {
  const { snapshot, topologyRun, toolExecutions = [], coordinatorFallbackLabel, locale = 'en', resolveAgentName } = input
  const run = snapshot?.run
  if (!run) return { nodes: [], edges: [] }

  const nameOf = (slugOrHandle: string | null | undefined, fallback?: string | null): string => {
    if (!slugOrHandle) return fallback ?? ''
    return resolveAgentName ? resolveAgentName(slugOrHandle, fallback) : (fallback ?? slugOrHandle)
  }

  const runExecs = toolExecutions.filter((exec) => !exec.run_id || exec.run_id === run.id)
  const approvalTaskIds = new Set<string>()
  const approvalInstanceIds = new Set<string>()
  let unassignedApproval = false
  for (const exec of runExecs) {
    const s = String(exec.status ?? '').toLowerCase()
    if (s === 'waiting_approval' || s === 'awaiting_approval') {
      if (exec.task_id) approvalTaskIds.add(exec.task_id)
      if (exec.agent_instance_id) approvalInstanceIds.add(exec.agent_instance_id)
      if (!exec.task_id) unassignedApproval = true
    }
  }

  const validTopoRun = topologyRun && (!topologyRun.run_id || topologyRun.run_id === run.id) ? topologyRun : null
  const instances: TopologyInstanceDto[] = validTopoRun?.instances?.length
    ? validTopoRun.instances
    : readSnapshotInstances(snapshot)

  const instanceById = new Map<string, TopologyInstanceDto>()
  const instanceByTaskId = new Map<string, TopologyInstanceDto>()
  for (const inst of instances) {
    instanceById.set(inst.instance_id, inst)
    if (inst.task_id && !instanceByTaskId.has(inst.task_id)) {
      instanceByTaskId.set(inst.task_id, inst)
    }
  }

  const displayNames = readSnapshotDisplayNames(snapshot)
  const snapNodes = (snapshot.nodes ?? []).filter((n) => !n.run_id || n.run_id === run.id)
  const snapFlows = snapshot.flows ?? []

  // Identify Coordinator identity (declared conversation node, root plan flow, or operation-layer instance)
  const declaredConv: DeclaredGraphNodeDto | undefined = snapshot.graph?.nodes.find((n) => n.is_conversation)
  const planFlow: OrchestrationFlowDto | undefined = snapFlows.find((f) => f.via === 'plan' || !f.via)
  const coordinatorInstances = instances.filter(
    (inst) => !inst.task_id && (inst.layer === 'operation' || inst.layer === 'planning' || inst.depth === 0 || inst.role === 'conversation'),
  )
  const coordinatorSlug =
    declaredConv?.node_key
    ?? planFlow?.from
    ?? coordinatorInstances.find((i) => i.role === 'conversation' || i.agent_slug)?.agent_slug
    ?? null

  const declaredLabel = declaredConv ? pickNodeLabel(declaredConv, locale) : undefined
  const coordinatorLabel =
    declaredLabel
    || (coordinatorSlug ? nameOf(coordinatorSlug, coordinatorSlug) : '')
    || coordinatorFallbackLabel

  const coordinatorInstanceIds = coordinatorInstances.map((i) => i.instance_id)
  const coordinatorApproval =
    unassignedApproval || coordinatorInstanceIds.some((id) => approvalInstanceIds.has(id))

  const workItems: RawWorkItem[] = []
  const representedInstanceIds = new Set<string>(coordinatorInstanceIds)

  const snapNodeById = new Map<string, TaskNodeDto>()
  const snapNodeByTitleOrKey = new Map<string, TaskNodeDto>()
  for (const n of snapNodes) {
    snapNodeById.set(n.id, n)
    const extraKey = (n as unknown as { task_key?: string }).task_key
    if (extraKey) snapNodeByTitleOrKey.set(extraKey, n)
    if (n.title && !snapNodeByTitleOrKey.has(n.title)) snapNodeByTitleOrKey.set(n.title, n)
  }

  const flowByTaskKey = new Map<string, OrchestrationFlowDto>()
  for (const f of snapFlows) {
    if (f.task_key && !flowByTaskKey.has(f.task_key)) flowByTaskKey.set(f.task_key, f)
  }

  const topoTasks: TopologyTaskDto[] = validTopoRun?.tasks ?? []
  if (topoTasks.length > 0) {
    for (const task of topoTasks) {
      const snapNode = snapNodeById.get(task.task_id) ?? snapNodeByTitleOrKey.get(task.task_key)
      const flow = flowByTaskKey.get(task.task_key) ?? flowByTaskKey.get(task.task_id)
      const inst =
        (task.worker_instance_id ? instanceById.get(task.worker_instance_id) : undefined)
        ?? instanceByTaskId.get(task.task_id)
      const instanceId = task.worker_instance_id ?? inst?.instance_id ?? null
      if (instanceId) representedInstanceIds.add(instanceId)
      const rawExtraDispatchedBy = (snapNode as unknown as { dispatched_by_task_id?: string | null } | undefined)?.dispatched_by_task_id
      const dispatchedByTaskId = task.dispatched_by_task_id ?? rawExtraDispatchedBy ?? null
      workItems.push({
        nodeId: `task:${task.task_id || task.task_key}`,
        taskId: task.task_id || null,
        taskKey: task.task_key || null,
        instanceId,
        parentInstanceId: inst?.parent_instance_id ?? null,
        dispatchedByTaskId,
        dispatcherHint: flow?.via === 'task_dispatch' ? flow.from : null,
        handle: task.handle ?? flow?.handle ?? null,
        agentSlug: task.agent_slug ?? inst?.agent_slug ?? flow?.to ?? null,
        agentDisplayName:
          displayNames.get(task.task_id)
          ?? (instanceId ? displayNames.get(instanceId) : undefined)
          ?? null,
        laneKey: snapNode?.lane_key ?? null,
        rawStatus: task.status || snapNode?.status || flow?.status || inst?.status || 'queued',
        title: task.title || snapNode?.title || task.task_key,
        description: snapNode?.description || null,
        resultSummary: task.result_summary ?? null,
        writeScope: task.write_scope ?? [],
        dependencies: task.dependencies?.length ? task.dependencies : (snapNode?.dependencies ?? []),
        via: flow?.via ?? (dispatchedByTaskId ? 'task_dispatch' : 'plan'),
      })
    }
  } else if (snapNodes.length > 0 || snapFlows.length > 0) {
    const matchedFlows = new Set<OrchestrationFlowDto>()
    for (let idx = 0; idx < snapNodes.length; idx++) {
      const n = snapNodes[idx]!
      const extraTaskKey = (n as unknown as { task_key?: string | null }).task_key ?? null
      const extraDispatchedBy = (n as unknown as { dispatched_by_task_id?: string | null }).dispatched_by_task_id ?? null
      const extraHandle = (n as unknown as { handle?: string | null }).handle ?? null
      let flow =
        (extraTaskKey ? flowByTaskKey.get(extraTaskKey) : undefined)
        ?? flowByTaskKey.get(n.id)
        ?? flowByTaskKey.get(n.title)
      if (!flow && snapNodes.length === snapFlows.length) {
        flow = snapFlows[idx]
      }
      if (flow) matchedFlows.add(flow)

      const inst = instanceByTaskId.get(n.id)
      const assign = (snapshot.assignments ?? []).find((a) => String(a.task_node_id) === n.id) as
        | (typeof snapshot.assignments[number] & { agent_instance_id?: string; role?: string })
        | undefined
      const instanceId = inst?.instance_id ?? assign?.agent_instance_id ?? assign?.agent_id ?? null
      if (instanceId) representedInstanceIds.add(instanceId)
      const taskKey = extraTaskKey ?? flow?.task_key ?? null
      workItems.push({
        nodeId: `task:${n.id || taskKey || idx}`,
        taskId: n.id || null,
        taskKey,
        instanceId,
        parentInstanceId: inst?.parent_instance_id ?? null,
        dispatchedByTaskId: extraDispatchedBy,
        dispatcherHint: flow?.via === 'task_dispatch' ? flow.from : null,
        handle: extraHandle ?? flow?.handle ?? null,
        agentSlug: flow?.to ?? inst?.agent_slug ?? assign?.role ?? null,
        agentDisplayName:
          displayNames.get(n.id)
          ?? (instanceId ? displayNames.get(instanceId) : undefined)
          ?? assign?.agent_name
          ?? null,
        laneKey: n.lane_key || null,
        rawStatus: flow?.status || n.status || inst?.status || 'queued',
        title: n.title || taskKey || n.id,
        description: n.description || null,
        resultSummary: null,
        writeScope: [],
        dependencies: n.dependencies ?? [],
        via: flow?.via ?? (extraDispatchedBy ? 'task_dispatch' : 'plan'),
      })
    }

    for (const flow of snapFlows) {
      if (matchedFlows.has(flow)) continue
      workItems.push({
        nodeId: `task:${flow.task_key}`,
        taskId: null,
        taskKey: flow.task_key,
        instanceId: null,
        parentInstanceId: null,
        dispatchedByTaskId: null,
        dispatcherHint: flow.via === 'task_dispatch' ? flow.from : null,
        handle: flow.handle ?? null,
        agentSlug: flow.to || null,
        agentDisplayName: null,
        laneKey: null,
        rawStatus: flow.status || 'queued',
        title: flow.task_key,
        description: null,
        resultSummary: null,
        writeScope: [],
        dependencies: [],
        via: flow.via === 'task_dispatch' ? 'task_dispatch' : 'plan',
      })
    }
  }

  // Include any execution agent instances not yet attached to a task node
  for (const inst of instances) {
    if (representedInstanceIds.has(inst.instance_id)) continue
    if (inst.layer === 'operation' || inst.layer === 'planning') continue
    representedInstanceIds.add(inst.instance_id)
    workItems.push({
      nodeId: `inst:${inst.instance_id}`,
      taskId: inst.task_id ?? null,
      taskKey: null,
      instanceId: inst.instance_id,
      parentInstanceId: inst.parent_instance_id ?? null,
      dispatchedByTaskId: null,
      dispatcherHint: null,
      handle: null,
      agentSlug: inst.agent_slug ?? (inst.role || null),
      agentDisplayName: displayNames.get(inst.instance_id) ?? null,
      laneKey: null,
      rawStatus: inst.status || 'queued',
      title: inst.role || inst.agent_slug || inst.instance_id.slice(0, 8),
      description: null,
      resultSummary: null,
      writeScope: [],
      dependencies: [],
      via: inst.depth >= 2 || inst.parent_instance_id ? 'task_dispatch' : 'plan',
    })
  }

  const byTaskId = new Map<string, RawWorkItem>()
  const byTaskKey = new Map<string, RawWorkItem>()
  const byHandle = new Map<string, RawWorkItem>()
  const byInstanceId = new Map<string, RawWorkItem>()
  const byAgentSlug = new Map<string, RawWorkItem>()

  for (const item of workItems) {
    if (item.taskId && !byTaskId.has(item.taskId)) byTaskId.set(item.taskId, item)
    if (item.taskKey && !byTaskKey.has(item.taskKey)) byTaskKey.set(item.taskKey, item)
    if (item.handle && !byHandle.has(item.handle)) byHandle.set(item.handle, item)
    if (item.instanceId && !byInstanceId.has(item.instanceId)) byInstanceId.set(item.instanceId, item)
    if (item.agentSlug && !byAgentSlug.has(item.agentSlug)) byAgentSlug.set(item.agentSlug, item)
  }

  const parentOfItem = new Map<RawWorkItem, RawWorkItem | null>()
  for (const item of workItems) {
    let parent: RawWorkItem | undefined
    if (item.dispatchedByTaskId) {
      parent = byTaskId.get(item.dispatchedByTaskId) ?? byTaskKey.get(item.dispatchedByTaskId)
    }
    if (!parent && item.parentInstanceId) {
      parent = byInstanceId.get(item.parentInstanceId)
    }
    if (!parent && item.dispatcherHint) {
      parent =
        byHandle.get(item.dispatcherHint)
        ?? byTaskKey.get(item.dispatcherHint)
        ?? byTaskId.get(item.dispatcherHint)
        ?? byAgentSlug.get(item.dispatcherHint)
    }
    parentOfItem.set(item, parent && parent !== item ? parent : null)
  }

  const depthOfItem = (item: RawWorkItem): { parentId: string; depth: number } => {
    const visited = new Set<RawWorkItem>([item])
    let cursor = parentOfItem.get(item) ?? null
    if (!cursor) return { parentId: 'coordinator', depth: 1 }
    const directParent = cursor
    let depth = 1
    while (cursor && !visited.has(cursor)) {
      visited.add(cursor)
      depth += 1
      cursor = parentOfItem.get(cursor) ?? null
    }
    return { parentId: directParent.nodeId, depth: Math.max(2, depth) }
  }

  const badgeLookup = collectRuntimeBadges(snapshot, validTopoRun)
  const coordinatorEngine =
    coordinatorInstanceIds.map((id) => badgeLookup.byInstanceId.get(id)).find(Boolean)
    ?? (coordinatorSlug ? badgeLookup.byHandle.get(coordinatorSlug) ?? badgeLookup.bySlug.get(coordinatorSlug) : undefined)
    ?? null

  const coordinatorNode: RuntimeTopologyNode = {
    id: 'coordinator',
    roleTier: 'coordinator',
    depth: 0,
    label: coordinatorLabel,
    subtitle: coordinatorSlug && coordinatorSlug !== coordinatorLabel ? coordinatorSlug : null,
    runtimeBadge: coordinatorEngine?.label ?? null,
    runtimeKind: coordinatorEngine?.kind ?? null,
    status: normalizeTopologyStatus(run.status, coordinatorApproval && workItems.length === 0),
    rawStatus: run.status,
    parentId: null,
    taskId: null,
    taskKey: null,
    instanceIds: coordinatorInstanceIds,
    handle: coordinatorSlug,
    agentSlug: coordinatorSlug,
    laneKey: null,
    title: coordinatorLabel,
    description: run.summary || null,
    resultSummary: null,
    writeScope: [],
    dependencies: [],
  }

  const nodes: RuntimeTopologyNode[] = [coordinatorNode]
  const edges: RuntimeTopologyEdge[] = []

  for (const item of workItems) {
    const { parentId, depth } = depthOfItem(item)
    const roleTier: RuntimeTopologyRoleTier = depth >= 2 ? 'subagent' : 'worker'
    const hasApproval =
      Boolean(item.taskId && approvalTaskIds.has(item.taskId))
      || Boolean(item.taskKey && approvalTaskIds.has(item.taskKey))
      || Boolean(item.instanceId && approvalInstanceIds.has(item.instanceId))
    const status = normalizeTopologyStatus(item.rawStatus, hasApproval)

    const primaryLabel = item.handle
      ? nameOf(item.handle, item.handle)
      : item.agentDisplayName
        || (item.agentSlug ? nameOf(item.agentSlug, item.agentSlug) : '')
        || item.title
        || item.taskKey
        || item.nodeId

    const subtitle =
      item.title && item.title !== primaryLabel
        ? item.title
        : item.taskKey && item.taskKey !== primaryLabel
          ? item.taskKey
          : null

    const itemEngine =
      (item.taskId ? badgeLookup.byTaskId.get(item.taskId) : undefined)
      ?? (item.taskKey ? badgeLookup.byTaskKey.get(item.taskKey) : undefined)
      ?? (item.instanceId ? badgeLookup.byInstanceId.get(item.instanceId) : undefined)
      ?? (item.handle ? badgeLookup.byHandle.get(item.handle) : undefined)
      ?? (item.agentSlug ? badgeLookup.bySlug.get(item.agentSlug) : undefined)
      ?? null

    nodes.push({
      id: item.nodeId,
      roleTier,
      depth,
      label: primaryLabel,
      subtitle,
      runtimeBadge: itemEngine?.label ?? null,
      runtimeKind: itemEngine?.kind ?? null,
      status,
      rawStatus: item.rawStatus,
      parentId,
      taskId: item.taskId,
      taskKey: item.taskKey,
      instanceIds: item.instanceId ? [item.instanceId] : [],
      handle: item.handle,
      agentSlug: item.agentSlug,
      laneKey: item.laneKey,
      title: item.title,
      description: item.description,
      resultSummary: item.resultSummary,
      writeScope: item.writeScope,
      dependencies: item.dependencies,
      via: depth >= 2 ? 'task_dispatch' : item.via,
    })

    const via: 'plan' | 'task_dispatch' = depth >= 2 || item.via === 'task_dispatch' ? 'task_dispatch' : 'plan'
    edges.push({
      id: `edge:${parentId}->${item.nodeId}`,
      source: parentId,
      target: item.nodeId,
      via,
      status,
      animated: status === 'running' || status === 'awaiting_approval',
    })
  }

  return { nodes, edges }
}

export function layoutRuntimeTopology(
  nodes: RuntimeTopologyNode[],
  edges: RuntimeTopologyEdge[],
): Record<string, { x: number; y: number }> {
  if (nodes.length === 0) return {}
  const graph = new dagre.graphlib.Graph({ multigraph: false, compound: false })
  graph.setGraph({
    rankdir: 'TB',
    nodesep: 24,
    ranksep: 44,
    marginx: 16,
    marginy: 16,
    ranker: 'network-simplex',
  })
  graph.setDefaultEdgeLabel(() => ({}))

  for (const node of nodes) {
    graph.setNode(node.id, { width: MINI_NODE_WIDTH, height: MINI_NODE_HEIGHT })
  }
  for (const edge of edges) {
    if (edge.source && edge.target && edge.source !== edge.target) {
      graph.setEdge(edge.source, edge.target)
    }
  }

  dagre.layout(graph)

  const positions: Record<string, { x: number; y: number }> = {}
  nodes.forEach((node, index) => {
    const pos = graph.node(node.id)
    positions[node.id] = pos && Number.isFinite(pos.x) && Number.isFinite(pos.y)
      ? {
          x: Math.round(pos.x - MINI_NODE_WIDTH / 2),
          y: Math.round(pos.y - MINI_NODE_HEIGHT / 2),
        }
      : { x: 12 + index * 180, y: 12 + node.depth * 92 }
  })
  return positions
}
</script>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Maximize2, MessageSquare } from '@lucide/vue'
import { Handle, MarkerType, Position, VueFlow, useVueFlow, type Edge, type Node } from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import { api, type ModelProviderInstanceDto } from '../api'
import { useStatusLabels } from '../lib/statusLabels'
import { useAgentDirectory } from '../composables/useAgentDirectory'
import TeammateSteerPopover, { type TeammateSteerTarget } from './teammate/TeammateSteerPopover.vue'

const props = withDefaults(
  defineProps<{
    nodes?: RuntimeTopologyNode[]
    edges?: RuntimeTopologyEdge[]
    snapshot?: OrchestrationSnapshotDto | null
    topologyRun?: TopologyRunDto | null
    toolExecutions?: ToolExecutionTimelineItemDto[]
    selectedNodeId?: string | null
    runId?: string | null
    fillHeight?: boolean
  }>(),
  {
    nodes: undefined,
    edges: undefined,
    snapshot: null,
    topologyRun: null,
    toolExecutions: () => [],
    selectedNodeId: null,
    runId: null,
    fillHeight: false,
  },
)

const emit = defineEmits<{
  'update:selectedNodeId': [nodeId: string | null]
  'select-node': [node: RuntimeTopologyNode | null]
  'steer-teammate': [target: TeammateSteerTarget]
}>()

const flowId = `orchestration-mini-graph-${++nextMiniGraphInstance}`
const { fitView } = useVueFlow(flowId)
const { t, te, locale } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? t(key, params) : t(key)
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

const containerRef = ref<HTMLElement | null>(null)
let resizeObserver: ResizeObserver | null = null

const effectiveRunId = computed(() => props.runId ?? props.snapshot?.run?.id ?? props.topologyRun?.run_id ?? null)
const steerTarget = ref<TeammateSteerTarget | null>(null)
const steerOpen = ref(false)
const pausedOverrides = ref<Record<string, boolean>>({})
const { runStatusLabel } = useStatusLabels()
const agentDirectory = typeof api?.listAgents === 'function' ? useAgentDirectory() : null
const nameFor = agentDirectory
  ? agentDirectory.nameFor
  : (slug: string | null | undefined, fallback?: string | null) => fallback ?? slug ?? ''

const providerCatalog = shallowRef<ModelProviderInstanceDto[]>([])

function safeFitView(duration = 0): void {
  try {
    void fitView({ padding: 0.16, maxZoom: 1.05, duration })
  } catch {
    // Safe in non-DOM / zero-viewport test environments
  }
}

onMounted(() => {
  if (typeof api?.listModelProviders === 'function') {
    void api.listModelProviders().then((rows) => {
      if (Array.isArray(rows)) providerCatalog.value = rows
    }).catch(() => {})
  }
  if (typeof ResizeObserver !== 'undefined' && containerRef.value) {
    resizeObserver = new ResizeObserver(() => {
      safeFitView(0)
    })
    resizeObserver.observe(containerRef.value)
  }
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  resizeObserver = null
})

const providerById = computed(() => {
  const map = new Map<string, ModelProviderInstanceDto>()
  for (const row of providerCatalog.value) {
    if (row?.id) map.set(row.id, row)
  }
  return map
})

function resolvedNodeBadge(node: RuntimeTopologyNode): RuntimeEngineBadge | null {
  if (node.runtimeBadge) {
    return {
      label: node.runtimeBadge,
      kind: node.runtimeKind ?? classifyRuntimeEngineKind(node.runtimeBadge),
    }
  }
  const slugKey = (node.agentSlug ?? node.handle?.split('#')[0] ?? '').trim()
  if (!slugKey || !agentDirectory) return null
  const agentRow = agentDirectory.items.value.find((row) => row.slug === slugKey || row.id === slugKey)
  if (!agentRow) return null
  const runId = props.snapshot?.run?.id ?? props.topologyRun?.run_id ?? null
  if (agentRow.recent_invocation && (!runId || agentRow.recent_invocation.run_id === runId)) {
    const hit = formatRuntimeEngineBadge({
      protocol: agentRow.recent_invocation.protocol,
      provider_instance_id: agentRow.recent_invocation.provider_instance_id,
      model: agentRow.recent_invocation.model ?? null,
    }, providerById.value)
    if (hit) return hit
  }
  if (agentRow.model_binding?.mode === 'fixed' && agentRow.model_binding.provider_instance_id) {
    const hit = formatRuntimeEngineBadge({
      provider_instance_id: agentRow.model_binding.provider_instance_id,
      model: agentRow.model_binding.model ?? null,
    }, providerById.value)
    if (hit) return hit
  }
  if (agentRow.configured_strategy?.kind === 'fixed' && agentRow.configured_strategy.provider_instance_id) {
    return formatRuntimeEngineBadge({
      provider_instance_id: agentRow.configured_strategy.provider_instance_id,
      model: agentRow.configured_strategy.model ?? null,
    }, providerById.value)
  }
  return null
}

const resolvedGraph = computed(() => {
  if (props.nodes && props.edges) {
    return { nodes: props.nodes, edges: props.edges }
  }
  return buildRuntimeTopology({
    snapshot: props.snapshot ?? null,
    topologyRun: props.topologyRun ?? null,
    toolExecutions: props.toolExecutions,
    coordinatorFallbackLabel: t('orchestrationPanel.roleCoordinator'),
    locale: locale.value,
    resolveAgentName: nameFor,
  })
})

const positions = computed(() =>
  layoutRuntimeTopology(resolvedGraph.value.nodes, resolvedGraph.value.edges),
)

const maxDepth = computed(() =>
  resolvedGraph.value.nodes.reduce((max, n) => Math.max(max, n.depth), 0),
)

const canvasHeightPx = computed(() => {
  if (maxDepth.value === 0) return 180
  if (maxDepth.value === 1) return 260
  return 340
})

const containerStyle = computed(() => {
  if (props.fillHeight) {
    return { minHeight: `${canvasHeightPx.value}px` }
  }
  return { height: `${canvasHeightPx.value}px` }
})

const flowNodes = computed<Node[]>(() =>
  resolvedGraph.value.nodes.map((node) => ({
    id: node.id,
    position: positions.value[node.id] ?? { x: 12, y: 12 },
    data: { node },
  })),
)

function edgeStrokeColor(status: RuntimeTopologyStatus): string {
  switch (status) {
    case 'running':
      return 'var(--obs-status-ok, var(--accent-success, #3fb950))'
    case 'awaiting_approval':
      return 'var(--obs-status-warn, var(--accent-warning, #d29922))'
    case 'completed':
      return 'var(--obs-connector, #64748b)'
    case 'failed':
      return 'var(--obs-status-danger, var(--accent-danger, #f85149))'
    default:
      return 'var(--border-default, #8b949e)'
  }
}

const flowEdges = computed<Edge[]>(() =>
  resolvedGraph.value.edges.map((edge) => {
    const stroke = edgeStrokeColor(edge.status)
    const isFocusedLineage =
      !props.selectedNodeId
      || edge.target === props.selectedNodeId
      || edge.source === props.selectedNodeId
    const edgeClass = edge.status === 'running'
      ? 'mini-edge--running'
      : edge.status === 'awaiting_approval'
        ? 'mini-edge--waiting'
        : ''
    return {
      id: edge.id,
      source: edge.source,
      target: edge.target,
      animated: edge.animated,
      class: edgeClass,
      label: edge.via === 'task_dispatch' ? t('orchestrationPanel.edgeDispatch') : '',
      markerEnd: {
        type: MarkerType.ArrowClosed,
        color: stroke,
        width: 13,
        height: 13,
      },
      style: {
        stroke,
        strokeWidth: edge.animated ? 1.85 : 1.35,
        opacity: isFocusedLineage ? 1 : 0.32,
      },
      labelStyle: {
        fontSize: '9.5px',
        fontFamily: 'var(--font-mono)',
        fill: 'var(--text-muted)',
      },
      labelBgStyle: {
        fill: 'var(--bg-primary)',
        fillOpacity: 0.9,
      },
    }
  }),
)

watch(
  () => resolvedGraph.value.nodes.map((n) => `${n.id}:${n.depth}`).join('|'),
  () => {
    safeFitView(0)
  },
)

function roleBadgeLabel(tier: RuntimeTopologyRoleTier): string {
  if (tier === 'coordinator') return t('orchestrationPanel.roleCoordinator')
  if (tier === 'subagent') return t('orchestrationPanel.roleSubagent')
  return t('orchestrationPanel.roleWorker')
}

function nodeHandleFor(node: RuntimeTopologyNode): string {
  return (node.handle || node.agentSlug || '').trim()
}

function canSteerNode(node: RuntimeTopologyNode): boolean {
  return Boolean(effectiveRunId.value && node.roleTier !== 'coordinator' && nodeHandleFor(node))
}

function isNodePaused(node: RuntimeTopologyNode): boolean {
  const handle = nodeHandleFor(node)
  if (handle && pausedOverrides.value[handle] !== undefined) return Boolean(pausedOverrides.value[handle])
  return String(node.rawStatus ?? '').toLowerCase() === 'paused'
}

function openNodeSteerPopover(node: RuntimeTopologyNode, event?: Event): void {
  if (!canSteerNode(node) || !effectiveRunId.value) return
  const handle = nodeHandleFor(node)
  const el = (event?.currentTarget as HTMLElement | null) ?? null
  const rect = el?.getBoundingClientRect?.()
  const target: TeammateSteerTarget = {
    runId: effectiveRunId.value,
    handle,
    displayName: node.label,
    status: isNodePaused(node) ? 'paused' : (node.rawStatus || node.status),
    paused: isNodePaused(node),
    taskSummary: node.title && node.title !== node.label ? node.title : (node.subtitle ?? node.description ?? null),
    anchorRect: rect && (rect.width > 0 || rect.height > 0)
      ? { top: rect.top, left: rect.left, width: rect.width, height: rect.height, right: rect.right, bottom: rect.bottom }
      : null,
  }
  steerTarget.value = target
  steerOpen.value = true
  emit('steer-teammate', target)
}

function onSteerStatusChange(payload: { handle: string; status: string; paused: boolean }): void {
  pausedOverrides.value = {
    ...pausedOverrides.value,
    [payload.handle]: payload.paused,
  }
}

watch(effectiveRunId, () => {
  steerOpen.value = false
  steerTarget.value = null
  pausedOverrides.value = {}
})

function statusDisplayLabel(node: RuntimeTopologyNode): string {
  if (isNodePaused(node)) return tr('teammateControl.status.paused', 'Paused')
  if (node.status === 'awaiting_approval') return runStatusLabel('awaiting_approval')
  if (node.status === 'queued' && !node.rawStatus) return runStatusLabel('queued')
  return runStatusLabel(node.rawStatus || node.status)
}

function toggleNode(node: RuntimeTopologyNode, event?: Event): void {
  const next = props.selectedNodeId === node.id ? null : node
  emit('update:selectedNodeId', next ? next.id : null)
  emit('select-node', next)
  if (canSteerNode(node)) {
    openNodeSteerPopover(node, event)
  }
}

function onFlowNodeClick(event: { node: { id: string }; event?: Event }): void {
  const found = resolvedGraph.value.nodes.find((n) => n.id === event.node.id)
  if (found) toggleNode(found, event.event)
}
</script>

<template>
  <div
    v-if="resolvedGraph.nodes.length > 0"
    ref="containerRef"
    class="orchestration-mini-graph"
    :class="{ 'is-fill': fillHeight }"
    :style="containerStyle"
    data-testid="orchestration-mini-graph"
  >
    <button
      type="button"
      class="mini-graph-fit-btn ui-pressable"
      :title="tr('orchestrationPanel.fitView', 'Zoom to fit')"
      :aria-label="tr('orchestrationPanel.fitView', 'Zoom to fit')"
      data-testid="mini-graph-fit-btn"
      @click="safeFitView(160)"
    >
      <Maximize2 :size="12" aria-hidden="true" />
    </button>

    <VueFlow
      :id="flowId"
      :nodes="flowNodes"
      :edges="flowEdges"
      :nodes-draggable="false"
      :nodes-connectable="false"
      :edges-updatable="false"
      :disable-keyboard-a11y="true"
      :zoom-on-double-click="false"
      :min-zoom="0.4"
      :max-zoom="1.25"
      fit-view-on-init
      :default-viewport="{ zoom: 0.9 }"
      @node-click="onFlowNodeClick"
    >
      <Background :gap="18" :size="1" />
      <template #node-default="nodeProps">
        <div
          class="mini-topo-node"
          :class="[
            `mini-topo-node--${nodeProps.data.node.roleTier}`,
            `mini-topo-status--${nodeProps.data.node.status}`,
            {
              'is-selected': selectedNodeId === nodeProps.data.node.id,
              'is-dimmed': Boolean(selectedNodeId && selectedNodeId !== nodeProps.data.node.id),
            },
          ]"
          role="button"
          tabindex="0"
          :aria-pressed="selectedNodeId === nodeProps.data.node.id"
          data-testid="mini-topo-node"
          :data-node-id="nodeProps.data.node.id"
          :data-role-tier="nodeProps.data.node.roleTier"
          :data-node-status="isNodePaused(nodeProps.data.node) ? 'paused' : nodeProps.data.node.status"
          :title="`${nodeProps.data.node.label} · ${statusDisplayLabel(nodeProps.data.node)}`"
          @click.stop="toggleNode(nodeProps.data.node, $event)"
          @keydown.enter.prevent="toggleNode(nodeProps.data.node, $event)"
          @keydown.space.prevent="toggleNode(nodeProps.data.node, $event)"
        >
          <Handle
            type="target"
            :position="Position.Top"
            :connectable="false"
            class="mini-topo-handle"
          />
          <div class="mini-topo-node__top">
            <span
              class="mini-topo-dot"
              :class="isNodePaused(nodeProps.data.node) ? 'dot--paused' : `dot--${nodeProps.data.node.status}`"
            />
            <strong class="mini-topo-label">{{ nodeProps.data.node.label }}</strong>
            <span class="mini-topo-tier">{{ roleBadgeLabel(nodeProps.data.node.roleTier) }}</span>
            <button
              v-if="canSteerNode(nodeProps.data.node)"
              type="button"
              class="mini-topo-steer-btn ui-pressable"
              :title="tr('teammateControl.triggerTooltip', 'Steer or pause {name}', { name: nodeProps.data.node.label })"
              :aria-label="tr('teammateControl.triggerTooltip', 'Steer or pause {name}', { name: nodeProps.data.node.label })"
              data-testid="mini-topo-steer-btn"
              @click.stop="openNodeSteerPopover(nodeProps.data.node, $event)"
            >
              <MessageSquare :size="11" aria-hidden="true" />
            </button>
          </div>
          <div class="mini-topo-node__meta">
            <span
              class="mini-topo-status-pill"
              :class="isNodePaused(nodeProps.data.node) ? 'pill--paused' : `pill--${nodeProps.data.node.status}`"
            >
              {{ statusDisplayLabel(nodeProps.data.node) }}
            </span>
            <span
              v-if="resolvedNodeBadge(nodeProps.data.node)"
              class="mini-topo-engine-badge"
              :data-engine-kind="resolvedNodeBadge(nodeProps.data.node)!.kind"
              :title="resolvedNodeBadge(nodeProps.data.node)!.label"
              data-testid="mini-topo-engine-badge"
            >
              {{ resolvedNodeBadge(nodeProps.data.node)!.label }}
            </span>
            <span v-else-if="nodeProps.data.node.subtitle" class="mini-topo-subtitle">
              {{ nodeProps.data.node.subtitle }}
            </span>
          </div>
          <div v-if="resolvedNodeBadge(nodeProps.data.node) && nodeProps.data.node.subtitle" class="mini-topo-subtitle">
            {{ nodeProps.data.node.subtitle }}
          </div>
          <Handle
            type="source"
            :position="Position.Bottom"
            :connectable="false"
            class="mini-topo-handle"
          />
        </div>
      </template>
    </VueFlow>

    <TeammateSteerPopover
      v-if="steerTarget"
      v-model:open="steerOpen"
      :run-id="steerTarget.runId"
      :handle="steerTarget.handle"
      :display-name="steerTarget.displayName"
      :status="steerTarget.status"
      :paused="steerTarget.paused"
      :task-summary="steerTarget.taskSummary"
      :anchor-rect="steerTarget.anchorRect"
      @status-change="onSteerStatusChange"
    />
  </div>
</template>

<style scoped>
.orchestration-mini-graph {
  position: relative;
  width: 100%;
  min-width: 0;
  border: 1px solid var(--obs-hairline, var(--border-muted));
  border-radius: 8px;
  background: var(--obs-surface, var(--bg-primary));
  overflow: hidden;
  transition: height 0.18s ease;
  font-variant-numeric: tabular-nums;
}

.orchestration-mini-graph.is-fill {
  flex: 1 1 auto;
  height: 100%;
  border: 0;
  border-radius: 0;
}

.mini-graph-fit-btn {
  position: absolute;
  top: 8px;
  right: 8px;
  z-index: 5;
  display: inline-grid;
  place-items: center;
  width: 26px;
  height: 26px;
  padding: 0;
  border: 1px solid var(--obs-hairline, var(--border-muted));
  border-radius: 5px;
  background: var(--bg-primary);
  color: var(--text-secondary);
  cursor: pointer;
  transition: color 0.12s ease, border-color 0.12s ease, background-color 0.12s ease;
}

.mini-graph-fit-btn:hover {
  color: var(--text-primary);
  border-color: var(--border-default);
  background: var(--bg-secondary);
}

.orchestration-mini-graph :deep(.vue-flow__node-default),
.orchestration-mini-graph :deep(.vue-flow__node-default.selected),
.orchestration-mini-graph :deep(.vue-flow__node-default:hover),
.orchestration-mini-graph :deep(.vue-flow__node-default:focus) {
  width: auto;
  padding: 0;
  border: 0;
  border-radius: 0;
  background: transparent;
  box-shadow: none;
  color: inherit;
  text-align: left;
  font-size: inherit;
}

/* Subtle animated stroke-dasharray pulse on active running / waiting edges */
.orchestration-mini-graph :deep(.vue-flow__edge.mini-edge--running .vue-flow__edge-path) {
  stroke-dasharray: 6 5;
  animation: mini-edge-dash-flow 0.85s linear infinite;
}

.orchestration-mini-graph :deep(.vue-flow__edge.mini-edge--waiting .vue-flow__edge-path) {
  stroke-dasharray: 4 5;
  animation: mini-edge-dash-flow 1.4s linear infinite;
}

@keyframes mini-edge-dash-flow {
  from {
    stroke-dashoffset: 22;
  }
  to {
    stroke-dashoffset: 0;
  }
}

.mini-topo-handle {
  opacity: 0;
  pointer-events: none;
  width: 6px;
  height: 6px;
}

.mini-topo-node {
  width: 208px;
  min-height: 64px;
  box-sizing: border-box;
  padding: 8px 10px;
  border-radius: 7px;
  background: var(--bg-primary);
  border: 1px solid var(--obs-hairline, var(--border-muted));
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.06);
  display: flex;
  flex-direction: column;
  gap: 5px;
  text-align: left;
  cursor: pointer;
  user-select: none;
  transition: border-color 0.14s ease, box-shadow 0.14s ease, opacity 0.14s ease;
}

.mini-topo-node:hover {
  border-color: var(--border-default);
}

.mini-topo-node:focus-visible {
  outline: 2px solid var(--border-input-focus);
  outline-offset: 2px;
}

.mini-topo-node--coordinator {
  border-color: var(--border-default);
  background: var(--bg-secondary);
}

.mini-topo-status--running {
  border-color: color-mix(in srgb, var(--obs-status-ok, var(--accent-success)) 48%, var(--border-muted));
}

.mini-topo-status--awaiting_approval {
  border-color: color-mix(in srgb, var(--obs-status-warn, var(--accent-warning)) 55%, var(--border-muted));
}

.mini-topo-status--failed {
  border-color: color-mix(in srgb, var(--obs-status-danger, var(--accent-danger)) 55%, var(--border-muted));
}

.mini-topo-node.is-selected {
  border-color: var(--text-primary);
  box-shadow: 0 0 0 1px var(--text-primary);
}

.mini-topo-node.is-dimmed {
  opacity: 0.48;
}

.mini-topo-node__top {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.mini-topo-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
  background: var(--text-muted);
}

.mini-topo-dot.dot--running {
  background: var(--obs-status-ok, var(--accent-success));
  animation: mini-topo-pulse 1.5s ease-in-out infinite;
}

.mini-topo-dot.dot--awaiting_approval {
  background: var(--obs-status-warn, var(--accent-warning));
  animation: mini-topo-pulse 1.6s ease-in-out infinite;
}

.mini-topo-dot.dot--completed {
  background: var(--obs-status-ok, var(--accent-success));
}

.mini-topo-dot.dot--failed {
  background: var(--obs-status-danger, var(--accent-danger));
}

.mini-topo-dot.dot--queued {
  background: var(--text-muted);
}

.mini-topo-dot.dot--paused {
  background: var(--obs-status-warn, var(--accent-warning));
}

.mini-topo-steer-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  flex-shrink: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.mini-topo-steer-btn:hover {
  background: var(--bg-secondary);
  color: var(--text-primary);
}

@keyframes mini-topo-pulse {
  0%, 100% {
    box-shadow: 0 0 0 0 color-mix(in srgb, var(--obs-status-ok, var(--accent-success)) 42%, transparent);
  }
  50% {
    box-shadow: 0 0 0 4px transparent;
  }
}

.mini-topo-label {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  font-weight: 600;
  line-height: 1.3;
  letter-spacing: -0.01em;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mini-topo-tier {
  flex-shrink: 0;
  font-size: 10px;
  font-weight: 500;
  line-height: 1.25;
  padding: 1px 5px;
  border-radius: 4px;
  color: var(--text-secondary);
  background: var(--bg-secondary);
}

.mini-topo-node--coordinator .mini-topo-tier {
  background: var(--bg-primary-button);
  color: var(--text-on-primary);
}

.mini-topo-node__meta {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.mini-topo-status-pill {
  flex-shrink: 0;
  font-size: 10px;
  font-weight: 600;
  line-height: 1.3;
  padding: 1px 6px;
  border-radius: 999px;
  color: var(--text-muted);
  background: var(--bg-secondary);
}

.mini-topo-status-pill.pill--running {
  color: var(--obs-status-ok, var(--accent-success));
  background: var(--obs-status-ok-soft, color-mix(in srgb, var(--accent-success) 12%, transparent));
}

.mini-topo-status-pill.pill--awaiting_approval,
.mini-topo-status-pill.pill--paused {
  color: var(--obs-status-warn, var(--accent-warning));
  background: var(--obs-status-warn-soft, color-mix(in srgb, var(--accent-warning) 13%, transparent));
}

.mini-topo-status-pill.pill--completed {
  color: var(--text-secondary);
  background: var(--bg-secondary);
}

.mini-topo-status-pill.pill--failed {
  color: var(--obs-status-danger, var(--accent-danger));
  background: var(--obs-status-danger-soft, color-mix(in srgb, var(--accent-danger) 12%, transparent));
}

.mini-topo-engine-badge {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  max-width: 118px;
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--bg-secondary);
  color: var(--text-muted);
  font-family: var(--font-mono);
  font-size: 9.5px;
  font-weight: 500;
  line-height: 1.3;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.mini-topo-subtitle {
  flex: 1;
  min-width: 0;
  font-size: 10.5px;
  line-height: 1.35;
  color: var(--text-muted);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

@media (prefers-reduced-motion: reduce) {
  .mini-topo-dot.dot--running,
  .mini-topo-dot.dot--awaiting_approval,
  .orchestration-mini-graph :deep(.vue-flow__edge.mini-edge--running .vue-flow__edge-path),
  .orchestration-mini-graph :deep(.vue-flow__edge.mini-edge--waiting .vue-flow__edge-path) {
    animation: none;
  }
}
</style>
