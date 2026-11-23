/**
 * Deterministic Squarified Treemap layout (Bruls, Huizing & van Wijk 2000)
 * + Core `RunContextDiagnosticsDto.compaction` projection and legacy `ContextPackDto` fallback.
 */

import type {
  CompactionReconciliationDto,
  ContextBudgetShareDto,
  ContextCategoryNodeDto,
  ContextCompactionDiagnosticsDto,
  ContextPackDto,
  RunContextDiagnosticsDto,
} from '@/api'

export type ContextTreemapCategory =
  | 'system_prompt'
  | 'mode_pipeline'
  | 'project_instructions'
  | 'tool_schemas'
  | 'skills_catalog'
  | 'memory'
  | 'compacted_digest'
  | 'conversation_turns'
  | 'tool_outputs'
  | 'other'

export interface TreemapRect {
  x: number
  y: number
  width: number
  height: number
}

export interface TreemapInputItem {
  id: string
  value: number
}

export interface TreemapPlacedRect<T extends TreemapInputItem = TreemapInputItem> extends TreemapRect {
  item: T
  areaShare: number
}

export interface ContextTreemapEvidenceItem {
  id: string
  source: string
  tokens: number
  bytesEstimate: number
  dropped: boolean
}

export interface ContextTreemapNode {
  id: string
  label: string
  category: ContextTreemapCategory
  sourceKey?: string | null
  depth: number
  retainedTokens: number
  droppedTokens: number
  retainedBytesEstimate: number
  droppedBytesEstimate: number
  /** Share of pack total retained tokens (0..100). */
  sharePercent: number
  /** Share of pack token_budget when budget > 0 (0..100). */
  budgetSharePercent: number
  itemCount: number
  droppedItemCount: number
  items: ContextTreemapEvidenceItem[]
  children: ContextTreemapNode[]
}

export interface CompactionReconciliation {
  packId: string
  laneKey: string | null
  createdAt: string
  tokenBudget: number
  retainedTokens: number
  droppedTokens: number
  preCompactionTokens: number
  retainedBytesEstimate: number
  droppedBytesEstimate: number
  utilizationPercent: number
  retainedRatioPercent: number
  droppedRatioPercent: number
  hasPricingData: boolean
  hasCompactionDigest: boolean
  previousPackRetainedDelta: number | null
  previousPackDroppedDelta: number | null
}

export interface ContextTreemapModel {
  pack: ContextPackDto | null
  packCount: number
  root: ContextTreemapNode
  reconciliation: CompactionReconciliation
  /** All packs in chronological order for pack-switcher controls. */
  packs: Array<{
    id: string
    laneKey: string | null
    createdAt: string
    estimatedTokens: number
    tokenBudget: number
    droppedTokens: number
  }>
}

const BYTES_PER_TOKEN_ESTIMATE = 4

const SOURCE_CATEGORY_PATTERNS: Array<{ category: ContextTreemapCategory; match: RegExp }> = [
  {
    category: 'system_prompt',
    match: /^(system_prompt|agent_system_prompt|base_prompt|accepted_intent|run_steering)$/i,
  },
  {
    category: 'mode_pipeline',
    match: /^(mode_pipeline|prompt_pipeline|structured_session_state|task_context|plan-ledger|plan_ledger|run_recorded_facts)$/i,
  },
  {
    category: 'project_instructions',
    match: /^(workspace_instructions|agents_md|apm_instructions|project_instructions)$/i,
  },
  {
    category: 'tool_schemas',
    match: /^(tool_schemas?|tool_manifest|mcp_servers?)$/i,
  },
  {
    category: 'skills_catalog',
    match: /^(workspace_skills|skills_catalog|skills_index|skills?)$/i,
  },
  {
    category: 'memory',
    match: /^(reviewed_memory|user_profile_memory|project_core_memory|l2_memory|l3_memory|memory)$/i,
  },
  {
    category: 'compacted_digest',
    match: /^(session_compaction|compacted_digest|exploration-digest|exploration_digest|organization_reports)$/i,
  },
  {
    category: 'conversation_turns',
    match: /^(session_history|conversation_turns?|session_attachments|chat_history)$/i,
  },
  {
    category: 'tool_outputs',
    match: /^(tool|tool_outputs?|tool_results?)$/i,
  },
]

const CATEGORY_DEFAULT_LABELS: Record<ContextTreemapCategory, string> = {
  system_prompt: 'System Prompt',
  mode_pipeline: 'Mode & Pipeline',
  project_instructions: 'Project Instructions (AGENTS.md)',
  tool_schemas: 'Tool Schemas & MCP',
  skills_catalog: 'Skills Catalog',
  memory: 'Memory (L2 / L3)',
  compacted_digest: 'Compacted Digest',
  conversation_turns: 'Conversation Turns',
  tool_outputs: 'Tool Outputs',
  other: 'Other Context',
}

export function classifyContextSource(source: string): ContextTreemapCategory {
  const trimmed = source.trim()
  for (const rule of SOURCE_CATEGORY_PATTERNS) {
    if (rule.match.test(trimmed)) return rule.category
  }
  const lower = trimmed.toLowerCase()
  if (lower.includes('skill')) return 'skills_catalog'
  if (lower.includes('memory')) return 'memory'
  if (lower.includes('compact') || lower.includes('digest')) return 'compacted_digest'
  if (lower.includes('instruction') || lower.includes('agents')) return 'project_instructions'
  if (lower.includes('mcp') || lower.includes('schema')) return 'tool_schemas'
  if (lower.includes('history') || lower.includes('conversation') || lower.includes('attachment')) {
    return 'conversation_turns'
  }
  if (lower.includes('tool')) return 'tool_outputs'
  if (lower.includes('pipeline') || lower.includes('mode') || lower.includes('plan')) return 'mode_pipeline'
  if (lower.includes('system') || lower.includes('prompt')) return 'system_prompt'
  return 'other'
}

export function categoryDefaultLabel(category: ContextTreemapCategory): string {
  return CATEGORY_DEFAULT_LABELS[category] ?? category
}

function worstAspectRatio(rowValues: number[], shortSide: number, scale: number): number {
  if (rowValues.length === 0 || shortSide <= 0 || scale <= 0) return Number.POSITIVE_INFINITY
  let sum = 0
  let maxVal = 0
  let minVal = Number.POSITIVE_INFINITY
  for (const v of rowValues) {
    const area = v * scale
    sum += area
    if (area > maxVal) maxVal = area
    if (area < minVal) minVal = area
  }
  if (sum <= 0 || minVal <= 0) return Number.POSITIVE_INFINITY
  const s2 = shortSide * shortSide
  const sum2 = sum * sum
  return Math.max((s2 * maxVal) / sum2, sum2 / (s2 * minVal))
}

/**
 * Deterministic Bruls-Huizing-van Wijk Squarified Treemap layout.
 *
 * Guarantees:
 * - Zero overlaps and exact coverage of `bounds` (within floating epsilon).
 * - Deterministic sorting by descending value with lexicographic `id` tie-break.
 * - Safe against empty arrays, zero values, or extreme value skews.
 */
export function computeSquarifiedTreemap<T extends TreemapInputItem>(
  items: T[],
  bounds: TreemapRect = { x: 0, y: 0, width: 100, height: 100 },
): Array<TreemapPlacedRect<T>> {
  if (bounds.width <= 0 || bounds.height <= 0) return []
  const positive = items
    .filter((item) => Number.isFinite(item.value) && item.value > 0)
    .sort((a, b) => b.value - a.value || a.id.localeCompare(b.id))

  if (positive.length === 0) return []

  const totalValue = positive.reduce((acc, item) => acc + item.value, 0)
  if (totalValue <= 0) return []

  const totalArea = bounds.width * bounds.height
  const scale = totalArea / totalValue

  const result: Array<TreemapPlacedRect<T>> = []
  let rx = bounds.x
  let ry = bounds.y
  let rw = bounds.width
  let rh = bounds.height

  let idx = 0
  while (idx < positive.length && rw > 1e-9 && rh > 1e-9) {
    const horizontalStrip = rw < rh
    const shortSide = Math.min(rw, rh)
    const row: T[] = [positive[idx]!]
    const rowVals: number[] = [positive[idx]!.value]
    idx++

    let currentWorst = worstAspectRatio(rowVals, shortSide, scale)
    while (idx < positive.length) {
      const candidate = positive[idx]!
      rowVals.push(candidate.value)
      const nextWorst = worstAspectRatio(rowVals, shortSide, scale)
      if (nextWorst <= currentWorst + 1e-12) {
        row.push(candidate)
        currentWorst = nextWorst
        idx++
      } else {
        rowVals.pop()
        break
      }
    }

    const rowSum = row.reduce((acc, item) => acc + item.value, 0)
    const rowArea = rowSum * scale

    if (horizontalStrip) {
      const stripHeight = Math.min(rh, shortSide > 0 ? rowArea / rw : 0)
      let cursorX = rx
      for (let i = 0; i < row.length; i++) {
        const item = row[i]!
        const isLast = i === row.length - 1
        const w = isLast ? Math.max(0, rx + rw - cursorX) : (item.value / rowSum) * rw
        result.push({
          item,
          x: cursorX,
          y: ry,
          width: Math.max(0, w),
          height: Math.max(0, stripHeight),
          areaShare: item.value / totalValue,
        })
        cursorX += w
      }
      ry += stripHeight
      rh = Math.max(0, rh - stripHeight)
    } else {
      const stripWidth = Math.min(rw, shortSide > 0 ? rowArea / rh : 0)
      let cursorY = ry
      for (let i = 0; i < row.length; i++) {
        const item = row[i]!
        const isLast = i === row.length - 1
        const h = isLast ? Math.max(0, ry + rh - cursorY) : (item.value / rowSum) * rh
        result.push({
          item,
          x: rx,
          y: cursorY,
          width: Math.max(0, stripWidth),
          height: Math.max(0, h),
          areaShare: item.value / totalValue,
        })
        cursorY += h
      }
      rx += stripWidth
      rw = Math.max(0, rw - stripWidth)
    }
  }

  return result
}

function normalizeShares(pack: ContextPackDto): {
  retained: ContextBudgetShareDto[]
  dropped: ContextBudgetShareDto[]
  hasPricingData: boolean
} {
  const hasPricingData = Array.isArray(pack.source_tokens) && pack.source_tokens.length > 0
  if (hasPricingData) {
    return {
      retained: pack.source_tokens!.filter((s) => s && typeof s.source === 'string'),
      dropped: (pack.dropped_sources ?? []).filter((s) => s && typeof s.source === 'string'),
      hasPricingData: true,
    }
  }
  // Legacy fallback: event recorded `sources` & `estimated_tokens` without per-item prices.
  const rawSources = Array.isArray(pack.sources) ? pack.sources : []
  if (rawSources.length === 0) {
    return { retained: [], dropped: [], hasPricingData: false }
  }
  const perItem = Math.max(1, Math.round((pack.estimated_tokens || rawSources.length) / rawSources.length))
  return {
    retained: rawSources.map((source) => ({ source, tokens: perItem })),
    dropped: (pack.dropped_sources ?? []).filter((s) => s && typeof s.source === 'string'),
    hasPricingData: false,
  }
}

/**
 * Build a hierarchical zoomable Context Treemap model + compaction reconciliation
 * from one or more Phase 11 `ContextPackDto` snapshots.
 */
export function buildContextTreemapModel(
  contextPacks: ContextPackDto[],
  selectedPackId?: string | null,
): ContextTreemapModel {
  const sortedPacks = [...contextPacks].sort(
    (a, b) => Date.parse(a.created_at || '0') - Date.parse(b.created_at || '0') || a.id.localeCompare(b.id),
  )

  const packSummaries = sortedPacks.map((p) => ({
    id: p.id,
    laneKey: p.lane_key ?? null,
    createdAt: p.created_at,
    estimatedTokens: p.estimated_tokens,
    tokenBudget: p.token_budget,
    droppedTokens: (p.dropped_sources ?? []).reduce((sum, s) => sum + Math.max(0, s.tokens || 0), 0),
  }))

  const activeIdx = selectedPackId
    ? sortedPacks.findIndex((p) => p.id === selectedPackId)
    : sortedPacks.length - 1
  const pack = activeIdx >= 0 ? sortedPacks[activeIdx]! : sortedPacks[sortedPacks.length - 1] ?? null
  const prevPack = activeIdx > 0 ? sortedPacks[activeIdx - 1]! : null

  if (!pack) {
    const emptyRoot: ContextTreemapNode = {
      id: 'root',
      label: 'Context Window',
      category: 'other',
      depth: 0,
      retainedTokens: 0,
      droppedTokens: 0,
      retainedBytesEstimate: 0,
      droppedBytesEstimate: 0,
      sharePercent: 0,
      budgetSharePercent: 0,
      itemCount: 0,
      droppedItemCount: 0,
      items: [],
      children: [],
    }
    return {
      pack: null,
      packCount: 0,
      root: emptyRoot,
      reconciliation: {
        packId: '',
        laneKey: null,
        createdAt: '',
        tokenBudget: 0,
        retainedTokens: 0,
        droppedTokens: 0,
        preCompactionTokens: 0,
        retainedBytesEstimate: 0,
        droppedBytesEstimate: 0,
        utilizationPercent: 0,
        retainedRatioPercent: 0,
        droppedRatioPercent: 0,
        hasPricingData: false,
        hasCompactionDigest: false,
        previousPackRetainedDelta: null,
        previousPackDroppedDelta: null,
      },
      packs: [],
    }
  }

  const { retained, dropped, hasPricingData } = normalizeShares(pack)
  const tokenBudget = Math.max(0, pack.token_budget || 0)

  // Group items by category -> sourceKey -> individual items
  const categoryMap = new Map<
    ContextTreemapCategory,
    Map<string, ContextTreemapEvidenceItem[]>
  >()

  let itemSeq = 0
  const pushItem = (share: ContextBudgetShareDto, isDropped: boolean) => {
    const tokens = Math.max(0, Math.round(share.tokens || 0))
    const category = classifyContextSource(share.source)
    let bySource = categoryMap.get(category)
    if (!bySource) {
      bySource = new Map()
      categoryMap.set(category, bySource)
    }
    let list = bySource.get(share.source)
    if (!list) {
      list = []
      bySource.set(share.source, list)
    }
    list.push({
      id: `${category}:${share.source}:${isDropped ? 'drop' : 'keep'}:${++itemSeq}`,
      source: share.source,
      tokens,
      bytesEstimate: tokens * BYTES_PER_TOKEN_ESTIMATE,
      dropped: isDropped,
    })
  }

  for (const s of retained) pushItem(s, false)
  for (const s of dropped) pushItem(s, true)

  const summedRetained = retained.reduce((sum, s) => sum + Math.max(0, Math.round(s.tokens || 0)), 0)
  const totalRetained = summedRetained > 0 ? summedRetained : Math.max(0, pack.estimated_tokens || 0)
  const totalDropped = dropped.reduce((sum, s) => sum + Math.max(0, Math.round(s.tokens || 0)), 0)
  const preCompactionTokens = totalRetained + totalDropped

  const pct = (part: number, whole: number) =>
    whole > 0 ? Math.min(100, Math.round((part / whole) * 1000) / 10) : 0

  const categoryChildren: ContextTreemapNode[] = []

  for (const [category, bySource] of categoryMap.entries()) {
    const sourceChildren: ContextTreemapNode[] = []
    let catRetained = 0
    let catDropped = 0
    let catItemCount = 0
    let catDroppedCount = 0
    const catItems: ContextTreemapEvidenceItem[] = []

    for (const [sourceKey, items] of bySource.entries()) {
      const keptItems = items.filter((i) => !i.dropped)
      const dropItems = items.filter((i) => i.dropped)
      const srcRetained = keptItems.reduce((sum, i) => sum + i.tokens, 0)
      const srcDropped = dropItems.reduce((sum, i) => sum + i.tokens, 0)

      catRetained += srcRetained
      catDropped += srcDropped
      catItemCount += keptItems.length
      catDroppedCount += dropItems.length
      catItems.push(...items)

      // Level-3 item nodes when a source has multiple entries (e.g. multiple reviewed_memory / session_history rows)
      const leafChildren: ContextTreemapNode[] =
        keptItems.length > 1
          ? keptItems.map((item, idx) => ({
              id: `item:${category}:${sourceKey}:${idx + 1}`,
              label: `${sourceKey} #${idx + 1}`,
              category,
              sourceKey,
              depth: 3,
              retainedTokens: item.tokens,
              droppedTokens: 0,
              retainedBytesEstimate: item.bytesEstimate,
              droppedBytesEstimate: 0,
              sharePercent: pct(item.tokens, totalRetained),
              budgetSharePercent: pct(item.tokens, tokenBudget),
              itemCount: 1,
              droppedItemCount: 0,
              items: [item],
              children: [],
            }))
          : []

      sourceChildren.push({
        id: `src:${category}:${sourceKey}`,
        label: sourceKey,
        category,
        sourceKey,
        depth: 2,
        retainedTokens: srcRetained,
        droppedTokens: srcDropped,
        retainedBytesEstimate: srcRetained * BYTES_PER_TOKEN_ESTIMATE,
        droppedBytesEstimate: srcDropped * BYTES_PER_TOKEN_ESTIMATE,
        sharePercent: pct(srcRetained, totalRetained),
        budgetSharePercent: pct(srcRetained, tokenBudget),
        itemCount: keptItems.length,
        droppedItemCount: dropItems.length,
        items,
        children: leafChildren,
      })
    }

    sourceChildren.sort(
      (a, b) =>
        b.retainedTokens - a.retainedTokens
        || b.droppedTokens - a.droppedTokens
        || a.id.localeCompare(b.id),
    )

    // Flatten redundant single-source wrapper only when the source itself has no leaf children
    const catNodeChildren =
      sourceChildren.length === 1 && sourceChildren[0]!.children.length > 0
        ? sourceChildren[0]!.children
        : sourceChildren

    categoryChildren.push({
      id: `cat:${category}`,
      label: categoryDefaultLabel(category),
      category,
      sourceKey: sourceChildren.length === 1 ? sourceChildren[0]!.sourceKey : null,
      depth: 1,
      retainedTokens: catRetained,
      droppedTokens: catDropped,
      retainedBytesEstimate: catRetained * BYTES_PER_TOKEN_ESTIMATE,
      droppedBytesEstimate: catDropped * BYTES_PER_TOKEN_ESTIMATE,
      sharePercent: pct(catRetained, totalRetained),
      budgetSharePercent: pct(catRetained, tokenBudget),
      itemCount: catItemCount,
      droppedItemCount: catDroppedCount,
      items: catItems,
      children: catNodeChildren,
    })
  }

  categoryChildren.sort(
    (a, b) =>
      b.retainedTokens - a.retainedTokens
      || b.droppedTokens - a.droppedTokens
      || a.id.localeCompare(b.id),
  )

  const root: ContextTreemapNode = {
    id: 'root',
    label: pack.lane_key ? `Context (${pack.lane_key})` : 'Context (Planner)',
    category: 'other',
    depth: 0,
    retainedTokens: totalRetained,
    droppedTokens: totalDropped,
    retainedBytesEstimate: totalRetained * BYTES_PER_TOKEN_ESTIMATE,
    droppedBytesEstimate: totalDropped * BYTES_PER_TOKEN_ESTIMATE,
    sharePercent: 100,
    budgetSharePercent: pct(totalRetained, tokenBudget),
    itemCount: retained.length,
    droppedItemCount: dropped.length,
    items: categoryChildren.flatMap((c) => c.items),
    children: categoryChildren,
  }

  const prevDropped = prevPack
    ? (prevPack.dropped_sources ?? []).reduce((sum, s) => sum + Math.max(0, s.tokens || 0), 0)
    : null

  const reconciliation: CompactionReconciliation = {
    packId: pack.id,
    laneKey: pack.lane_key ?? null,
    createdAt: pack.created_at,
    tokenBudget,
    retainedTokens: totalRetained,
    droppedTokens: totalDropped,
    preCompactionTokens,
    retainedBytesEstimate: totalRetained * BYTES_PER_TOKEN_ESTIMATE,
    droppedBytesEstimate: totalDropped * BYTES_PER_TOKEN_ESTIMATE,
    utilizationPercent: pct(totalRetained, tokenBudget),
    retainedRatioPercent: pct(totalRetained, preCompactionTokens),
    droppedRatioPercent: pct(totalDropped, preCompactionTokens),
    hasPricingData,
    hasCompactionDigest: categoryMap.has('compacted_digest'),
    previousPackRetainedDelta: prevPack ? totalRetained - (prevPack.estimated_tokens || 0) : null,
    previousPackDroppedDelta: prevDropped !== null ? totalDropped - prevDropped : null,
  }

  return {
    pack,
    packCount: sortedPacks.length,
    root,
    reconciliation,
    packs: packSummaries,
  }
}

/** Find the breadcrumb chain `[root, ..., target]` for a given `nodeId`. */
export function findTreemapPath(root: ContextTreemapNode, nodeId: string): ContextTreemapNode[] {
  if (root.id === nodeId) return [root]
  for (const child of root.children) {
    const sub = findTreemapPath(child, nodeId)
    if (sub.length > 0) return [root, ...sub]
  }
  return []
}

const VALID_TREEMAP_CATEGORIES = new Set<ContextTreemapCategory>([
  'system_prompt',
  'mode_pipeline',
  'project_instructions',
  'tool_schemas',
  'skills_catalog',
  'memory',
  'compacted_digest',
  'conversation_turns',
  'tool_outputs',
  'other',
])

function normalizeTreemapCategory(raw?: string | null): ContextTreemapCategory {
  if (raw && VALID_TREEMAP_CATEGORIES.has(raw as ContextTreemapCategory)) {
    return raw as ContextTreemapCategory
  }
  return raw ? classifyContextSource(raw) : 'other'
}

function mapCoreCategoryNode(dto: ContextCategoryNodeDto): ContextTreemapNode {
  const category = normalizeTreemapCategory(dto.category)
  return {
    id: dto.id,
    label: dto.label || (dto.depth === 1 ? categoryDefaultLabel(category) : dto.id),
    category,
    sourceKey: dto.source_key ?? null,
    depth: dto.depth ?? 0,
    retainedTokens: Math.max(0, dto.retained_tokens ?? 0),
    droppedTokens: Math.max(0, dto.dropped_tokens ?? 0),
    retainedBytesEstimate:
      dto.retained_bytes_estimate ?? Math.max(0, dto.retained_tokens ?? 0) * BYTES_PER_TOKEN_ESTIMATE,
    droppedBytesEstimate:
      dto.dropped_bytes_estimate ?? Math.max(0, dto.dropped_tokens ?? 0) * BYTES_PER_TOKEN_ESTIMATE,
    sharePercent: dto.share_percent ?? 0,
    budgetSharePercent: dto.budget_share_percent ?? 0,
    itemCount: dto.item_count ?? 0,
    droppedItemCount: dto.dropped_item_count ?? 0,
    items: (dto.items ?? []).map((item, idx) => ({
      id: item.id || `${dto.id}:item:${idx + 1}`,
      source: item.source,
      tokens: Math.max(0, item.tokens ?? 0),
      bytesEstimate: item.bytes_estimate ?? Math.max(0, item.tokens ?? 0) * BYTES_PER_TOKEN_ESTIMATE,
      dropped: Boolean(item.dropped),
    })),
    children: (dto.children ?? []).map(mapCoreCategoryNode),
  }
}

function mapCoreReconciliation(dto: CompactionReconciliationDto): CompactionReconciliation {
  const retainedTokens = Math.max(0, dto.retained_tokens ?? 0)
  const droppedTokens = Math.max(0, dto.dropped_tokens ?? 0)
  return {
    packId: dto.pack_id ?? '',
    laneKey: dto.lane_key ?? null,
    createdAt: dto.created_at ?? '',
    tokenBudget: Math.max(0, dto.token_budget ?? 0),
    retainedTokens,
    droppedTokens,
    preCompactionTokens: Math.max(0, dto.pre_compaction_tokens ?? retainedTokens + droppedTokens),
    retainedBytesEstimate:
      dto.retained_bytes_estimate ?? retainedTokens * BYTES_PER_TOKEN_ESTIMATE,
    droppedBytesEstimate:
      dto.dropped_bytes_estimate ?? droppedTokens * BYTES_PER_TOKEN_ESTIMATE,
    utilizationPercent: dto.utilization_percent ?? 0,
    retainedRatioPercent: dto.retained_ratio_percent ?? 0,
    droppedRatioPercent: dto.dropped_ratio_percent ?? 0,
    hasPricingData: Boolean(dto.has_pricing_data),
    hasCompactionDigest: Boolean(dto.has_compaction_digest),
    previousPackRetainedDelta:
      dto.previous_pack_retained_delta !== undefined ? dto.previous_pack_retained_delta : null,
    previousPackDroppedDelta:
      dto.previous_pack_dropped_delta !== undefined ? dto.previous_pack_dropped_delta : null,
  }
}

/**
 * Project Core's authoritative `ContextCompactionDiagnosticsDto` directly into
 * `ContextTreemapModel` so `computeSquarifiedTreemap` renders Core's exact numbers.
 */
export function fromCoreCompactionDiagnostics(
  compaction: ContextCompactionDiagnosticsDto,
  fallbackPacks: ContextPackDto[] = [],
): ContextTreemapModel {
  const reconciliation = mapCoreReconciliation(compaction.reconciliation)
  const root = mapCoreCategoryNode(compaction.root)
  const packList = (compaction.packs ?? []).map((p) => ({
    id: p.pack_id,
    laneKey: p.lane_key ?? null,
    createdAt: p.created_at,
    estimatedTokens: Math.max(0, p.retained_tokens ?? 0),
    tokenBudget: Math.max(0, p.token_budget ?? 0),
    droppedTokens: Math.max(0, p.dropped_tokens ?? 0),
  }))

  const matchedPack =
    fallbackPacks.find((p) => p.id === (compaction.selected_pack_id || reconciliation.packId))
    ?? (reconciliation.packId
      ? {
          id: reconciliation.packId,
          run_id: '',
          lane_key: reconciliation.laneKey,
          evidence_count: root.itemCount,
          estimated_tokens: reconciliation.retainedTokens,
          token_budget: reconciliation.tokenBudget,
          sources: root.items.filter((i) => !i.dropped).map((i) => i.source),
          created_at: reconciliation.createdAt,
        }
      : null)

  return {
    pack: matchedPack,
    packCount: compaction.pack_count ?? packList.length,
    root,
    reconciliation,
    packs: packList,
  }
}

/**
 * Resolve `ContextTreemapModel` preferring Core's authoritative `RunContextDiagnosticsDto.compaction`
 * while falling back cleanly to `buildContextTreemapModel(contextPacks, selectedPackId)` for
 * older snapshots or offline alternate-pack inspection.
 */
export function resolveContextTreemapModel(options: {
  diagnostics?: RunContextDiagnosticsDto | null
  contextPacks?: ContextPackDto[]
  selectedPackId?: string | null
}): ContextTreemapModel {
  const comp = options.diagnostics?.compaction
  const fallbackPacks = options.contextPacks ?? []
  if (comp && comp.root) {
    const activeCorePackId = comp.selected_pack_id || comp.reconciliation?.pack_id || null
    if (!options.selectedPackId || options.selectedPackId === activeCorePackId || fallbackPacks.length === 0) {
      return fromCoreCompactionDiagnostics(comp, fallbackPacks)
    }
  }
  return buildContextTreemapModel(fallbackPacks, options.selectedPackId)
}
