export type ModeLane = 'operation' | 'execution'

export interface ModeValidationNode {
  id: string
  lane: ModeLane
  /** Empty/absent means the node still has no bound agent and cannot publish. */
  agent_id?: string | null
  config?: Record<string, unknown> | null
}

export interface ModeValidationEdge {
  id: string
  source: string
  target: string
}

export type ModeIssueCode =
  | 'empty'
  | 'unbound-node'
  | 'self-loop'
  | 'duplicate-edge'
  | 'dangling-endpoint'
  | 'cycle'
  | 'backflow'
  | 'no-conversation'
  | 'multi-conversation'
  | 'conversation-layer'

export interface ModeIssue {
  code: ModeIssueCode
  severity: 'error' | 'warning'
  nodes?: string[]
  edges?: string[]
}

export interface ModeValidationOptions {
  /** Publish requires exactly one operation-layer conversation node. */
  requireConversation?: boolean
}

const CODE_ORDER: ModeIssueCode[] = [
  'empty',
  'unbound-node',
  'dangling-endpoint',
  'self-loop',
  'duplicate-edge',
  'cycle',
  'backflow',
  'conversation-layer',
  'multi-conversation',
  'no-conversation',
]

function compareIds(left: string, right: string): number {
  if (left === right) return 0
  return left < right ? -1 : 1
}

function sorted(values: Iterable<string>): string[] {
  return [...values].sort(compareIds)
}

/**
 * Structural checks shared by the canvas and the save/publish buttons. The
 * canvas keeps drafts saveable: only publish-grade checks are errors there.
 */
export function validateMode(
  nodes: ModeValidationNode[],
  edges: ModeValidationEdge[],
  options: ModeValidationOptions = {},
): ModeIssue[] {
  const issues: ModeIssue[] = []
  const nodeById = new Map<string, ModeValidationNode>()
  for (const node of nodes) if (!nodeById.has(node.id)) nodeById.set(node.id, node)
  const ids = sorted(nodeById.keys())

  if (ids.length === 0) {
    issues.push({ code: 'empty', severity: 'warning' })
  }

  for (const id of ids) {
    const node = nodeById.get(id)!
    if (!node.agent_id?.trim()) issues.push({ code: 'unbound-node', severity: 'error', nodes: [id] })
  }

  const pairIds = new Map<string, string[]>()
  const dangling = new Set<string>()
  const selfLoops = new Set<string>()
  for (const edge of edges) {
    if (!nodeById.has(edge.source) || !nodeById.has(edge.target)) {
      dangling.add(edge.id)
      continue
    }
    if (edge.source === edge.target) {
      selfLoops.add(edge.id)
      continue
    }
    const pair = `${edge.source}\u0000${edge.target}`
    const bucket = pairIds.get(pair) ?? []
    bucket.push(edge.id)
    pairIds.set(pair, bucket)
  }

  if (dangling.size > 0) {
    issues.push({ code: 'dangling-endpoint', severity: 'error', edges: sorted(dangling) })
  }
  if (selfLoops.size > 0) {
    issues.push({ code: 'self-loop', severity: 'error', edges: sorted(selfLoops) })
  }
  for (const edgeIds of pairIds.values()) {
    if (edgeIds.length < 2) continue
    issues.push({ code: 'duplicate-edge', severity: 'error', edges: sorted(edgeIds) })
  }

  const backflow = edges
    .filter((edge) => {
      const source = nodeById.get(edge.source)
      const target = nodeById.get(edge.target)
      return source?.lane === 'execution' && target?.lane === 'operation'
    })
    .map((edge) => edge.id)
  if (backflow.length > 0) {
    issues.push({ code: 'backflow', severity: 'error', edges: sorted(backflow) })
  }

  const adjacency = new Map<string, string[]>(ids.map((id) => [id, []]))
  for (const edge of edges) {
    if (!adjacency.has(edge.source) || !adjacency.has(edge.target) || edge.source === edge.target) continue
    adjacency.get(edge.source)!.push(edge.target)
  }
  for (const targets of adjacency.values()) targets.sort(compareIds)

  const state = new Map<string, 1 | 2>()
  const path: string[] = []
  const visit = (id: string): string[] | null => {
    state.set(id, 1)
    path.push(id)
    for (const target of adjacency.get(id) ?? []) {
      const targetState = state.get(target)
      if (targetState === 1) {
        const start = path.indexOf(target)
        return path.slice(start >= 0 ? start : 0)
      }
      if (targetState === undefined) {
        const found = visit(target)
        if (found) return found
      }
    }
    path.pop()
    state.set(id, 2)
    return null
  }
  let cycle: string[] | null = null
  for (const id of ids) {
    if (state.get(id) === undefined) {
      cycle = visit(id)
      if (cycle) break
    }
  }
  if (cycle) {
    issues.push({ code: 'cycle', severity: 'error', nodes: sorted(cycle) })
  }

  const conversation = ids.filter((id) => nodeById.get(id)?.config?.conversation === true)
  const misplaced = conversation.filter((id) => nodeById.get(id)?.lane !== 'operation')
  if (misplaced.length > 0) {
    issues.push({ code: 'conversation-layer', severity: 'error', nodes: sorted(misplaced) })
  }
  if (conversation.length > 1) {
    issues.push({ code: 'multi-conversation', severity: 'error', nodes: sorted(conversation) })
  }
  if (conversation.length === 0) {
    issues.push({
      code: 'no-conversation',
      severity: options.requireConversation ? 'error' : 'warning',
    })
  }

  const issueKey = (issue: ModeIssue): string =>
    sorted([...(issue.nodes ?? []), ...(issue.edges ?? [])]).join('\u0000')
  return issues.sort((left, right) => {
    const codeDelta = CODE_ORDER.indexOf(left.code) - CODE_ORDER.indexOf(right.code)
    if (codeDelta !== 0) return codeDelta
    return compareIds(issueKey(left), issueKey(right))
  })
}

export function hasBlockingModeIssue(modeIssues: readonly ModeIssue[]): boolean {
  return modeIssues.some((issue) => issue.severity === 'error')
}
