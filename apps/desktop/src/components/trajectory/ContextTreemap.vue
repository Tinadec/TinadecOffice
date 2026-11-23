<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight, Database, Layers, Sparkles, X, ZoomIn } from '@lucide/vue'
import type { ContextPackDto, RunContextDiagnosticsDto } from '@/api'
import {
  computeSquarifiedTreemap,
  findTreemapPath,
  resolveContextTreemapModel,
  type ContextTreemapCategory,
  type ContextTreemapModel,
  type ContextTreemapNode,
} from '@/lib/contextTreemapLayout'

const props = withDefaults(defineProps<{
  contextPacks?: ContextPackDto[]
  diagnostics?: RunContextDiagnosticsDto | null
  model?: ContextTreemapModel | null
  stale?: boolean
  compact?: boolean
}>(), {
  contextPacks: () => [],
  diagnostics: null,
  model: null,
  stale: false,
  compact: false,
})

const emit = defineEmits<{
  'select-segment': [node: ContextTreemapNode | null]
  'diagnose-segment': [node: ContextTreemapNode, model: ContextTreemapModel]
  'select-pack': [packId: string | null]
}>()

const { t } = useI18n()

function nodeDisplayLabel(node: ContextTreemapNode): string {
  if (node.depth === 0) return t('trajectoryEval.treemap.rootLabel')
  if (node.depth === 1) return t(`trajectoryEval.treemap.categories.${node.category}`)
  return node.label
}

const selectedPackId = ref<string | null>(null)
const zoomedNodeId = ref<string>('root')
const inspectedNodeId = ref<string | null>(null)

const activeModel = computed<ContextTreemapModel>(() => {
  if (props.model) return props.model
  return resolveContextTreemapModel({
    diagnostics: props.diagnostics,
    contextPacks: props.contextPacks,
    selectedPackId: selectedPackId.value,
  })
})

watch(selectedPackId, (nextPackId) => {
  emit('select-pack', nextPackId)
})

watch(
  () => activeModel.value.pack?.id,
  () => {
    zoomedNodeId.value = 'root'
    inspectedNodeId.value = null
  },
)

const breadcrumbPath = computed<ContextTreemapNode[]>(() => {
  const path = findTreemapPath(activeModel.value.root, zoomedNodeId.value)
  return path.length > 0 ? path : [activeModel.value.root]
})

const currentScopeNode = computed<ContextTreemapNode>(
  () => breadcrumbPath.value[breadcrumbPath.value.length - 1] ?? activeModel.value.root,
)

const inspectedNode = computed<ContextTreemapNode | null>(() => {
  if (!inspectedNodeId.value) return null
  const path = findTreemapPath(activeModel.value.root, inspectedNodeId.value)
  return path.length > 0 ? path[path.length - 1]! : null
})

interface TreemapTileItem {
  id: string
  value: number
  node: ContextTreemapNode
}

const layoutTiles = computed(() => {
  const sourceNodes = currentScopeNode.value.children.length > 0
    ? currentScopeNode.value.children
    : currentScopeNode.value.retainedTokens > 0 || currentScopeNode.value.droppedTokens > 0
      ? [currentScopeNode.value]
      : []

  const items: TreemapTileItem[] = sourceNodes
    .map((node) => ({
      id: node.id,
      value: node.retainedTokens > 0 ? node.retainedTokens : node.droppedTokens,
      node,
    }))
    .filter((entry) => entry.value > 0)

  return computeSquarifiedTreemap(items, { x: 0, y: 0, width: 100, height: 100 })
})

const CATEGORY_TONE_CLASS: Record<ContextTreemapCategory, string> = {
  system_prompt: 'tone-1',
  mode_pipeline: 'tone-2',
  project_instructions: 'tone-3',
  tool_schemas: 'tone-4',
  skills_catalog: 'tone-5',
  memory: 'tone-6',
  compacted_digest: 'tone-7',
  conversation_turns: 'tone-8',
  tool_outputs: 'tone-9',
  other: 'tone-0',
}

function handleTileClick(node: ContextTreemapNode): void {
  inspectedNodeId.value = node.id
  emit('select-segment', node)
  if (node.children.length > 0 && node.id !== zoomedNodeId.value) {
    zoomedNodeId.value = node.id
  }
}

function jumpToBreadcrumb(node: ContextTreemapNode): void {
  zoomedNodeId.value = node.id
  inspectedNodeId.value = node.id === 'root' ? null : node.id
  emit('select-segment', node.id === 'root' ? null : node)
}

function closeDrawer(): void {
  inspectedNodeId.value = null
  emit('select-segment', null)
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  return `${(bytes / 1024).toFixed(1)} KB`
}
</script>

<template>
  <section class="tm-card" :class="{ 'is-compact': compact }" data-testid="context-treemap">
    <header class="tm-head">
      <div class="tm-title">
        <Layers :size="14" aria-hidden="true" />
        <strong>{{ t('trajectoryEval.treemap.title') }}</strong>
      </div>
      <div class="tm-head-right">
        <span v-if="stale" class="tm-pill is-stale mono" data-testid="treemap-cached-badge">
          <Database :size="11" aria-hidden="true" />
          <span>{{ t('trajectoryEval.offline.cachedBadge') }}</span>
        </span>
        <select
          v-if="activeModel.packs.length > 1 && !model"
          v-model="selectedPackId"
          class="tm-pack-select"
          :aria-label="t('trajectoryEval.treemap.selectPack')"
          data-testid="treemap-pack-select"
        >
          <option
            v-for="(p, idx) in activeModel.packs"
            :key="p.id"
            :value="p.id"
          >
            {{
              p.laneKey
                ? t('trajectoryEval.treemap.packOptionLane', { lane: p.laneKey, index: idx + 1, tokens: p.estimatedTokens })
                : t('trajectoryEval.treemap.packOptionPlanner', { index: idx + 1, tokens: p.estimatedTokens })
            }}
          </option>
        </select>
        <span class="tm-pill mono" data-testid="treemap-utilization">
          {{
            activeModel.reconciliation.tokenBudget > 0
              ? t('trajectoryEval.treemap.utilizationWithBudget', {
                used: activeModel.reconciliation.retainedTokens.toLocaleString(),
                budget: activeModel.reconciliation.tokenBudget.toLocaleString(),
                pct: activeModel.reconciliation.utilizationPercent,
              })
              : t('trajectoryEval.treemap.utilizationNoBudget', {
                used: activeModel.reconciliation.retainedTokens.toLocaleString(),
              })
          }}
        </span>
        <span
          v-if="activeModel.reconciliation.droppedTokens > 0"
          class="tm-pill is-warn mono"
          data-testid="treemap-dropped-badge"
        >
          {{ t('trajectoryEval.treemap.droppedTokens', { tokens: activeModel.reconciliation.droppedTokens.toLocaleString() }) }}
        </span>
      </div>
    </header>

    <div v-if="layoutTiles.length === 0" class="tm-empty" data-testid="treemap-empty">
      {{ t('trajectoryEval.treemap.empty') }}
    </div>

    <template v-else>
      <nav
        class="tm-breadcrumbs"
        :aria-label="t('trajectoryEval.treemap.breadcrumbsLabel')"
        data-testid="treemap-breadcrumbs"
      >
        <template v-for="(crumb, idx) in breadcrumbPath" :key="crumb.id">
          <ChevronRight v-if="idx > 0" :size="12" class="tm-crumb-sep" aria-hidden="true" />
          <button
            type="button"
            class="tm-crumb-btn"
            :class="{ active: idx === breadcrumbPath.length - 1 }"
            :data-testid="`treemap-crumb-${crumb.id}`"
            @click="jumpToBreadcrumb(crumb)"
          >
            {{ nodeDisplayLabel(crumb) }}
          </button>
        </template>
      </nav>

      <div class="tm-canvas" role="region" :aria-label="t('trajectoryEval.treemap.canvasLabel')">
        <button
          v-for="tile in layoutTiles"
          :key="tile.item.id"
          type="button"
          class="tm-tile"
          :class="[
            CATEGORY_TONE_CLASS[tile.item.node.category],
            {
              'is-selected': inspectedNode?.id === tile.item.node.id,
              'is-composite': tile.item.node.children.length > 0,
              'has-dropped': tile.item.node.droppedTokens > 0,
              'is-small': tile.width < 16 || tile.height < 20,
            },
          ]"
          :style="{
            left: `${tile.x}%`,
            top: `${tile.y}%`,
            width: `${tile.width}%`,
            height: `${tile.height}%`,
          }"
          :title="`${nodeDisplayLabel(tile.item.node)}: ${tile.item.node.retainedTokens} / ${tile.item.node.droppedTokens} (${tile.item.node.sharePercent}%)`"
          :data-testid="`treemap-tile-${tile.item.node.id}`"
          @click="handleTileClick(tile.item.node)"
        >
          <span class="tm-tile__head">
            <span class="tm-tile__label">{{ nodeDisplayLabel(tile.item.node) }}</span>
            <ZoomIn
              v-if="tile.item.node.children.length > 0 && tile.width >= 18 && tile.height >= 22"
              :size="11"
              class="tm-tile__zoom"
              aria-hidden="true"
            />
          </span>
          <span v-if="tile.width >= 14 && tile.height >= 20" class="tm-tile__meta mono">
            {{ t('trajectoryEval.treemap.tileMeta', { tokens: tile.item.node.retainedTokens.toLocaleString(), pct: tile.item.node.sharePercent }) }}
          </span>
        </button>
      </div>

      <!-- Compaction reconciliation strip -->
      <div class="tm-reconcile mono" data-testid="treemap-reconciliation">
        <span>
          {{ t('trajectoryEval.treemap.retainedSummary', {
            tokens: activeModel.reconciliation.retainedTokens.toLocaleString(),
            bytes: formatBytes(activeModel.reconciliation.retainedBytesEstimate),
          }) }}
        </span>
        <span>
          {{ t('trajectoryEval.treemap.droppedSummary', {
            tokens: activeModel.reconciliation.droppedTokens.toLocaleString(),
            bytes: formatBytes(activeModel.reconciliation.droppedBytesEstimate),
          }) }}
        </span>
        <span v-if="activeModel.reconciliation.previousPackRetainedDelta !== null">
          {{ t('trajectoryEval.treemap.deltaPrev', {
            delta: `${activeModel.reconciliation.previousPackRetainedDelta >= 0 ? '+' : ''}${activeModel.reconciliation.previousPackRetainedDelta}`,
          }) }}
        </span>
      </div>

      <!-- On-demand segment detail drawer -->
      <div v-if="inspectedNode" class="tm-drawer" data-testid="treemap-detail-drawer">
        <div class="tm-drawer__head">
          <div class="tm-drawer__title">
            <strong>{{ nodeDisplayLabel(inspectedNode) }}</strong>
            <span class="tm-pill mono">{{ inspectedNode.sharePercent }}%</span>
          </div>
          <div class="tm-drawer__actions">
            <button
              type="button"
              class="tm-diagnose-btn"
              data-testid="treemap-diagnose-btn"
              @click="emit('diagnose-segment', inspectedNode, activeModel)"
            >
              <Sparkles :size="12" aria-hidden="true" />
              <span>{{ t('trajectoryEval.actions.diagnoseInComposer') }}</span>
            </button>
            <button
              type="button"
              class="tm-close-btn"
              :aria-label="t('trajectoryEval.actions.close')"
              data-testid="treemap-drawer-close"
              @click="closeDrawer"
            >
              <X :size="13" />
            </button>
          </div>
        </div>

        <div class="tm-drawer__stats mono">
          <div class="tm-stat-box">
            <span class="tm-stat-label">{{ t('trajectoryEval.treemap.retainedLabel') }}</span>
            <strong>{{ t('trajectoryEval.tree.tokensShort', { tokens: inspectedNode.retainedTokens.toLocaleString() }) }}</strong>
            <small>{{ t('trajectoryEval.treemap.itemsCount', { bytes: formatBytes(inspectedNode.retainedBytesEstimate), count: inspectedNode.itemCount }) }}</small>
          </div>
          <div class="tm-stat-box" :class="{ 'is-warn': inspectedNode.droppedTokens > 0 }">
            <span class="tm-stat-label">{{ t('trajectoryEval.treemap.droppedLabel') }}</span>
            <strong>{{ t('trajectoryEval.tree.tokensShort', { tokens: inspectedNode.droppedTokens.toLocaleString() }) }}</strong>
            <small>{{ t('trajectoryEval.treemap.itemsCount', { bytes: formatBytes(inspectedNode.droppedBytesEstimate), count: inspectedNode.droppedItemCount }) }}</small>
          </div>
        </div>

        <div v-if="inspectedNode.items.length > 0" class="tm-drawer__items">
          <div
            v-for="item in inspectedNode.items.slice(0, 10)"
            :key="item.id"
            class="tm-item-row mono"
            :class="{ 'is-dropped': item.dropped }"
          >
            <span class="tm-item-source">{{ item.source }}</span>
            <span class="tm-item-status">
              {{ item.dropped ? t('trajectoryEval.treemap.statusDropped') : t('trajectoryEval.treemap.statusRetained') }}
            </span>
            <span class="tm-item-cost">{{ t('trajectoryEval.tree.tokensShort', { tokens: item.tokens.toLocaleString() }) }} ({{ formatBytes(item.bytesEstimate) }})</span>
          </div>
        </div>
      </div>
    </template>
  </section>
</template>

<style scoped>
.tm-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid var(--border-muted);
  border-radius: var(--radius-card, 10px);
  background: var(--bg-secondary);
  min-width: 0;
}

.tm-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.tm-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-primary);
}

.tm-head-right {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.tm-pack-select {
  height: 22px;
  padding: 0 6px;
  border-radius: 6px;
  border: 1px solid var(--border-default);
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: 11px;
}

.tm-pill {
  display: inline-flex;
  align-items: center;
  height: 21px;
  padding: 0 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--bg-primary);
  color: var(--text-secondary);
  font-size: 10.5px;
}

.tm-pill.is-warn {
  background: var(--bg-status-warn);
  color: var(--text-primary);
  border-color: color-mix(in srgb, var(--accent-warning) 36%, var(--border-muted));
}

.tm-pill.is-stale {
  background: var(--bg-tertiary);
  color: var(--text-secondary);
  border-style: dashed;
}

.tm-empty {
  padding: 12px;
  text-align: center;
  color: var(--text-muted);
  font-size: 12px;
}

.tm-breadcrumbs {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}

.tm-crumb-sep {
  color: var(--text-muted);
}

.tm-crumb-btn {
  padding: 2px 7px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--bg-primary);
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.tm-crumb-btn.active {
  border-color: var(--border-default);
  background: var(--bg-tertiary);
  color: var(--text-primary);
  font-weight: 500;
}

.tm-canvas {
  position: relative;
  width: 100%;
  height: 196px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-primary);
  overflow: hidden;
}

.tm-tile {
  position: absolute;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  align-items: flex-start;
  padding: 6px 7px;
  border: 1px solid var(--bg-primary);
  box-shadow: inset 0 0 0 1px var(--border-muted);
  text-align: left;
  cursor: pointer;
  overflow: hidden;
  transition: filter 110ms ease, box-shadow 110ms ease;
}

.tm-tile:hover {
  filter: brightness(1.04);
  box-shadow: inset 0 0 0 1px var(--accent-primary);
  z-index: 2;
}

.tm-tile.is-selected {
  box-shadow: inset 0 0 0 1px var(--accent-primary);
  z-index: 3;
}

.tm-tile.has-dropped {
  border-style: dashed;
}

/* Tonal monochrome / lilac-neutral palette — zero garish multi-color stripes */
.tm-tile.tone-1 { background: color-mix(in srgb, var(--accent-primary) 16%, var(--bg-secondary)); }
.tm-tile.tone-2 { background: color-mix(in srgb, var(--accent-primary) 11%, var(--bg-tertiary)); }
.tm-tile.tone-3 { background: color-mix(in srgb, var(--text-primary) 10%, var(--bg-secondary)); }
.tm-tile.tone-4 { background: color-mix(in srgb, var(--text-secondary) 13%, var(--bg-secondary)); }
.tm-tile.tone-5 { background: color-mix(in srgb, var(--accent-primary) 20%, var(--bg-tertiary)); }
.tm-tile.tone-6 { background: color-mix(in srgb, var(--text-primary) 7%, var(--bg-tertiary)); }
.tm-tile.tone-7 { background: var(--bg-status-warn); }
.tm-tile.tone-8 { background: color-mix(in srgb, var(--text-secondary) 18%, var(--bg-secondary)); }
.tm-tile.tone-9 { background: color-mix(in srgb, var(--text-primary) 13%, var(--bg-tertiary)); }
.tm-tile.tone-0 { background: var(--bg-tertiary); }

.tm-tile__head {
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 4px;
  min-width: 0;
}

.tm-tile__label {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tm-tile__zoom {
  color: var(--text-secondary);
  flex-shrink: 0;
}

.tm-tile__meta {
  font-size: 10px;
  color: var(--text-secondary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
}

.tm-tile.is-small {
  padding: 2px 4px;
}

.tm-tile.is-small .tm-tile__label {
  font-size: 9.5px;
}

.tm-reconcile {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  padding: 6px 9px;
  border: 1px solid var(--border-muted);
  border-radius: 7px;
  background: var(--bg-primary);
  font-size: 10.5px;
  color: var(--text-secondary);
}

.tm-drawer {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  border: 1px solid var(--border-default);
  border-radius: 8px;
  background: var(--bg-primary);
}

.tm-drawer__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.tm-drawer__title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-primary);
}

.tm-drawer__actions {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.tm-diagnose-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: var(--control-height-xs, 26px);
  padding: 0 9px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-default);
  background: var(--bg-secondary);
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
}

.tm-diagnose-btn:hover {
  border-color: var(--accent-primary);
  background: var(--bg-selected);
}

.tm-close-btn {
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.tm-close-btn:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.tm-drawer__stats {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
}

.tm-stat-box {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 8px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 7px;
  background: var(--bg-secondary);
  font-size: 11px;
}

.tm-stat-box.is-warn {
  background: var(--bg-status-warn);
  border-color: color-mix(in srgb, var(--accent-warning) 35%, var(--border-muted));
}

.tm-stat-label {
  font-size: 10px;
  color: var(--text-muted);
  text-transform: uppercase;
}

.tm-stat-box strong {
  font-size: 13px;
  color: var(--text-primary);
}

.tm-stat-box small {
  font-size: 10.5px;
  color: var(--text-secondary);
}

.tm-drawer__items {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 140px;
  overflow-y: auto;
}

.tm-item-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--bg-secondary);
  font-size: 10.5px;
}

.tm-item-row.is-dropped {
  background: var(--bg-status-warn);
}

.tm-item-source {
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tm-item-status {
  color: var(--text-muted);
  font-size: 10px;
}

.tm-item-cost {
  color: var(--text-secondary);
  white-space: nowrap;
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}
</style>
