<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, ChevronRight, Database, RotateCcw, Sparkles, X, ZoomIn } from '@lucide/vue'
import type { ContextPackDto, RunContextDiagnosticsDto } from '@/api'
import {
  computeSquarifiedTreemap,
  findTreemapPath,
  resolveContextTreemapModel,
  type ContextTreemapCategory,
  type ContextTreemapModel,
  type ContextTreemapNode,
} from '@/lib/contextTreemapLayout'
import { resolveTrajectoryCopy } from '@/lib/trajectoryBuilder'

const props = withDefaults(defineProps<{
  contextPacks?: ContextPackDto[]
  diagnostics?: RunContextDiagnosticsDto | null
  model?: ContextTreemapModel | null
  focusCategory?: string | null
  stale?: boolean
  compact?: boolean
}>(), {
  contextPacks: () => [],
  diagnostics: null,
  model: null,
  focusCategory: null,
  stale: false,
  compact: false,
})

const emit = defineEmits<{
  'select-segment': [node: ContextTreemapNode | null]
  'diagnose-segment': [node: ContextTreemapNode, model: ContextTreemapModel]
  'select-pack': [packId: string | null]
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  return resolveTrajectoryCopy(t, te, key, fallback, params)
}

function nodeDisplayLabel(node: ContextTreemapNode): string {
  if (node.depth === 0) return t('trajectoryEval.treemap.rootLabel')
  if (node.depth === 1) return t(`trajectoryEval.treemap.categories.${node.category}`)
  return node.label
}

function categoryDisplayLabel(category: ContextTreemapCategory): string {
  return t(`trajectoryEval.treemap.categories.${category}`)
}

const selectedPackId = ref<string | null>(null)
const zoomedNodeId = ref<string>('root')
const inspectedNodeId = ref<string | null>(null)
const canvasZoom = ref<number>(1)

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
    canvasZoom.value = 1
  },
)

const ALIAS_TO_TREEMAP_CATEGORY: Record<string, ContextTreemapCategory> = {
  system: 'system_prompt',
  tools: 'tool_schemas',
  user: 'conversation_turns',
  inject: 'project_instructions',
  skill: 'skills_catalog',
  assistant: 'conversation_turns',
  tool_output: 'tool_outputs',
}

watch(
  () => props.focusCategory,
  (catKey) => {
    if (!catKey) return
    const normalized = ALIAS_TO_TREEMAP_CATEGORY[catKey] ?? catKey
    const children = activeModel.value.root.children
    const hit =
      children.find((c) => c.category === normalized || c.id === catKey || c.id === `cat:${normalized}`)
      ?? (catKey === 'inject'
        ? children.find((c) =>
            c.category === 'project_instructions'
            || c.category === 'memory'
            || c.category === 'compacted_digest'
            || c.category === 'other',
          )
        : undefined)
    if (hit) {
      zoomedNodeId.value = 'root'
      inspectedNodeId.value = hit.id
      emit('select-segment', hit)
    }
  },
  { immediate: true },
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
  system_prompt: 'tone-system',
  mode_pipeline: 'tone-system',
  project_instructions: 'tone-inject',
  tool_schemas: 'tone-tools',
  skills_catalog: 'tone-skill',
  memory: 'tone-inject',
  compacted_digest: 'tone-inject',
  conversation_turns: 'tone-user',
  tool_outputs: 'tone-tool-output',
  other: 'tone-other',
}

const CATEGORY_DOT_COLOR: Record<ContextTreemapCategory, string> = {
  system_prompt: 'var(--obs-ctx-system, #8053e3)',
  mode_pipeline: 'var(--obs-ctx-system, #8053e3)',
  project_instructions: 'var(--obs-ctx-inject, #c442a7)',
  tool_schemas: 'var(--obs-ctx-tools, #d49b00)',
  skills_catalog: 'var(--obs-ctx-skill, #e66d26)',
  memory: 'var(--obs-ctx-inject, #c442a7)',
  compacted_digest: 'var(--obs-ctx-inject, #c442a7)',
  conversation_turns: 'var(--obs-ctx-user, #44a56b)',
  tool_outputs: 'var(--obs-ctx-tool-output, #3ab4a4)',
  other: 'var(--text-muted)',
}

const droppedSourcesList = computed<string[]>(() => {
  const names = new Set<string>()
  for (const item of activeModel.value.root.items) {
    if (item.dropped && item.source) names.add(item.source)
  }
  return [...names]
})

function handleTileClick(node: ContextTreemapNode): void {
  inspectedNodeId.value = node.id
  emit('select-segment', node)
  if (node.children.length > 0 && node.id !== zoomedNodeId.value) {
    zoomedNodeId.value = node.id
  }
}

function handleTileDoubleClick(node: ContextTreemapNode): void {
  if (node.children.length > 0 && node.id !== zoomedNodeId.value) {
    zoomedNodeId.value = node.id
    inspectedNodeId.value = node.id
    emit('select-segment', node)
  } else if (breadcrumbPath.value.length > 1) {
    const parent = breadcrumbPath.value[breadcrumbPath.value.length - 2]!
    jumpToBreadcrumb(parent)
  }
}

function handleCanvasWheel(event: WheelEvent): void {
  const delta = event.deltaY < 0 ? 0.15 : -0.15
  const next = Math.min(2.2, Math.max(0.85, Math.round((canvasZoom.value + delta) * 100) / 100))
  if (next < 1 && breadcrumbPath.value.length > 1) {
    const parent = breadcrumbPath.value[breadcrumbPath.value.length - 2]!
    jumpToBreadcrumb(parent)
    canvasZoom.value = 1
    return
  }
  canvasZoom.value = Math.max(1, next)
}

function resetToFullMap(): void {
  zoomedNodeId.value = 'root'
  canvasZoom.value = 1
  inspectedNodeId.value = null
  emit('select-segment', null)
}

function jumpToBreadcrumb(node: ContextTreemapNode): void {
  zoomedNodeId.value = node.id
  canvasZoom.value = 1
  inspectedNodeId.value = node.id === 'root' ? null : node.id
  emit('select-segment', node.id === 'root' ? null : node)
}

function closeDrawer(): void {
  inspectedNodeId.value = null
  emit('select-segment', null)
}

function onGlobalKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && (zoomedNodeId.value !== 'root' || canvasZoom.value > 1 || inspectedNodeId.value)) {
    resetToFullMap()
  }
}

onMounted(() => {
  if (typeof window !== 'undefined') {
    window.addEventListener('keydown', onGlobalKeydown)
  }
})

onBeforeUnmount(() => {
  if (typeof window !== 'undefined') {
    window.removeEventListener('keydown', onGlobalKeydown)
  }
})

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  return `${(bytes / 1024).toFixed(1)} KB`
}
</script>

<template>
  <section
    class="tm-surface"
    :class="{ 'is-compact': compact }"
    data-testid="context-treemap"
    @keydown.esc.stop="resetToFullMap"
  >
    <header class="tm-head">
      <div class="tm-head__titles">
        <strong class="tm-title">{{ t('trajectoryEval.treemap.title') }}</strong>
        <span class="tm-subtitle mono">
          {{ tr('trajectoryWorkbench.matrix.itemCountSummary', '{count} 项已计量', {
            count: activeModel.root.itemCount,
          }) }}
        </span>
      </div>

      <div class="tm-head-right">
        <span v-if="stale" class="tm-pill is-stale mono" data-testid="treemap-cached-badge">
          <Database :size="11" aria-hidden="true" />
          <span>{{ t('trajectoryEval.offline.cachedBadge') }}</span>
        </span>
        <select
          v-if="activeModel.packs.length > 1 && !model"
          v-model="selectedPackId"
          class="tm-pack-select mono"
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
      <div class="tm-nav-row">
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

        <button
          v-if="zoomedNodeId !== 'root' || canvasZoom > 1"
          type="button"
          class="tm-reset-btn mono"
          data-testid="treemap-reset-btn"
          @click="resetToFullMap"
        >
          <RotateCcw :size="11" aria-hidden="true" />
          <span>{{ tr('trajectoryWorkbench.matrix.resetFull', '回全图') }}</span>
        </button>
      </div>

      <!-- Responsive @container Grid: Stacked on 360px narrow dock, Side-by-Side on wide /panel -->
      <div class="tm-workspace">
        <div class="tm-canvas-col">
          <div
            class="tm-canvas-viewport"
            role="region"
            :aria-label="t('trajectoryEval.treemap.canvasLabel')"
            @wheel.prevent="handleCanvasWheel"
          >
            <div
              class="tm-canvas"
              :style="canvasZoom > 1 ? { transform: `scale(${canvasZoom})`, transformOrigin: 'top left' } : undefined"
            >
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
                :title="`${nodeDisplayLabel(tile.item.node)}: ${tile.item.node.retainedTokens} tokens (${tile.item.node.sharePercent}%)`"
                :data-testid="`treemap-tile-${tile.item.node.id}`"
                @click="handleTileClick(tile.item.node)"
                @dblclick.stop="handleTileDoubleClick(tile.item.node)"
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
                <span v-if="tile.width >= 14 && tile.height >= 19" class="tm-tile__meta mono">
                  {{ tile.item.node.retainedTokens.toLocaleString() }} tokens · {{ categoryDisplayLabel(tile.item.node.category) }}
                </span>
              </button>
            </div>
          </div>

          <p class="tm-hint">
            {{ tr('trajectoryWorkbench.matrix.gestureHint', '单击查看详情 · 双击进入 / 返回 · 滚轮缩放 · Esc 回全图') }}
          </p>
        </div>

        <!-- Detail Pane: Side-by-side right column on wide panels, stacked below on narrow dock -->
        <aside v-if="inspectedNode" class="tm-detail" data-testid="treemap-detail-drawer">
          <div class="tm-detail__top">
            <span class="tm-detail__cat">
              <i class="tm-detail__dot" :style="{ backgroundColor: CATEGORY_DOT_COLOR[inspectedNode.category] }" />
              <span>{{ categoryDisplayLabel(inspectedNode.category) }}</span>
            </span>
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

          <strong class="tm-detail__name">{{ nodeDisplayLabel(inspectedNode) }}</strong>
          <span v-if="inspectedNode.sourceKey" class="tm-detail__source mono">{{ inspectedNode.sourceKey }}</span>

          <!-- Hero Token Metric (matching dsh-preset.jpg) -->
          <div class="tm-detail__hero mono">
            <strong class="tm-detail__hero-num">{{ inspectedNode.retainedTokens.toLocaleString() }}</strong>
            <span class="tm-detail__hero-unit">tokens</span>
          </div>
          <span class="tm-detail__share mono">
            {{ tr('trajectoryWorkbench.matrix.shareOfMeasured', '占全部已计量内容 {pct}%', {
              pct: inspectedNode.sharePercent,
            }) }}
          </span>

          <div class="tm-drawer__stats mono">
            <div class="tm-stat-row">
              <span class="tm-stat-label">{{ t('trajectoryEval.treemap.retainedLabel') }}</span>
              <strong>{{ t('trajectoryEval.tree.tokensShort', { tokens: inspectedNode.retainedTokens.toLocaleString() }) }}</strong>
              <small>{{ t('trajectoryEval.treemap.itemsCount', { bytes: formatBytes(inspectedNode.retainedBytesEstimate), count: inspectedNode.itemCount }) }}</small>
            </div>
            <div class="tm-stat-row" :class="{ 'is-warn': inspectedNode.droppedTokens > 0 }">
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
              <span class="tm-item-source" :title="item.source">{{ item.source }}</span>
              <span class="tm-item-status">
                {{ item.dropped ? t('trajectoryEval.treemap.statusDropped') : t('trajectoryEval.treemap.statusRetained') }}
              </span>
              <span class="tm-item-cost">
                {{ t('trajectoryEval.tree.tokensShort', { tokens: item.tokens.toLocaleString() }) }} ({{ formatBytes(item.bytesEstimate) }})
              </span>
            </div>
          </div>

          <button
            type="button"
            class="tm-diagnose-link"
            data-testid="treemap-diagnose-btn"
            @click="emit('diagnose-segment', inspectedNode, activeModel)"
          >
            <Sparkles :size="12" aria-hidden="true" />
            <span>{{ t('trajectoryEval.actions.diagnoseInComposer') }} ↗</span>
          </button>
        </aside>

        <aside v-else class="tm-detail tm-detail--placeholder">
          <div class="tm-detail__hero mono">
            <strong class="tm-detail__hero-num">{{ activeModel.reconciliation.retainedTokens.toLocaleString() }}</strong>
            <span class="tm-detail__hero-unit">tokens</span>
          </div>
          <p class="tm-placeholder-copy">
            {{ tr('trajectoryWorkbench.matrix.selectPrompt', '单击左侧任一色块查看来源构成、裁剪状态与内容计量明细。') }}
          </p>
        </aside>
      </div>

      <!-- Dropped-Sources Warning Bar -->
      <div
        v-if="activeModel.reconciliation.droppedTokens > 0"
        class="tm-dropped-warning"
        role="status"
        data-testid="treemap-dropped-warning"
      >
        <AlertTriangle :size="13" class="tm-dropped-warning__icon" aria-hidden="true" />
        <div class="tm-dropped-warning__body">
          <strong>
            {{ tr('trajectoryWorkbench.matrix.droppedWarningLead', '预算裁剪已丢弃 {tokens} tokens', {
              tokens: activeModel.reconciliation.droppedTokens.toLocaleString(),
            }) }}
          </strong>
          <span v-if="droppedSourcesList.length > 0" class="mono">
            ({{ droppedSourcesList.slice(0, 5).join(', ') }})
          </span>
        </div>
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
    </template>
  </section>
</template>

<style scoped>
.tm-surface {
  container-type: inline-size;
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px 14px;
  background: var(--bg-primary);
  border-bottom: 1px solid var(--border-muted);
  min-width: 0;
}

.tm-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px 12px;
  flex-wrap: wrap;
}

.tm-head__titles {
  display: inline-flex;
  align-items: baseline;
  gap: 8px;
  flex-wrap: wrap;
}

.tm-title {
  font-size: 14px;
  font-weight: 600;
  letter-spacing: -0.02em;
  color: var(--text-primary);
}

.tm-subtitle {
  font-size: 11px;
  color: var(--text-muted);
}

.tm-head-right {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.tm-pack-select {
  height: 23px;
  padding: 0 6px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: 11px;
}

.tm-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 21px;
  padding: 0 7px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  color: var(--text-secondary);
  font-size: 10.5px;
}

.tm-pill.is-warn {
  background: var(--bg-status-warn);
  color: var(--text-primary);
  border-color: color-mix(in srgb, var(--accent-warning) 38%, var(--border-muted));
}

.tm-pill.is-stale {
  border-style: dashed;
}

.tm-empty {
  padding: 20px 12px;
  text-align: center;
  color: var(--text-muted);
  font-size: 12px;
}

.tm-nav-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
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
  padding: 2px 6px;
  border-radius: 4px;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  cursor: pointer;
}

.tm-crumb-btn:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.tm-crumb-btn.active {
  background: var(--bg-secondary);
  color: var(--text-primary);
  font-weight: 600;
}

.tm-reset-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 7px;
  border: 1px solid var(--border-muted);
  border-radius: 5px;
  background: var(--bg-primary);
  color: var(--text-secondary);
  font-size: 10.5px;
  cursor: pointer;
}

.tm-reset-btn:hover {
  color: var(--text-primary);
  border-color: var(--border-default);
}

/* Responsive Workspace: stacked on narrow 360px dock, side-by-side on wide panels */
.tm-workspace {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 12px;
  align-items: start;
}

@container (min-width: 560px) {
  .tm-workspace {
    grid-template-columns: minmax(0, 1.45fr) minmax(220px, 0.95fr);
    gap: 16px;
  }
}

.tm-canvas-col {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.tm-canvas-viewport {
  position: relative;
  width: 100%;
  height: 236px;
  border: 1px solid var(--border-muted);
  background: var(--bg-primary);
  overflow: auto;
}

.tm-canvas {
  position: relative;
  width: 100%;
  height: 100%;
  transition: transform 110ms ease-out;
}

/* Open Geist Treemap Tiles: 1px hairline separators + soft pastel in Light / low-glare in Dark */
.tm-tile {
  position: absolute;
  display: flex;
  flex-direction: column;
  justify-content: flex-start;
  align-items: flex-start;
  gap: 3px;
  padding: 7px 8px;
  border: 1px solid var(--bg-primary);
  text-align: left;
  cursor: pointer;
  overflow: hidden;
  transition: filter 100ms ease, box-shadow 100ms ease;
}

.tm-tile:hover {
  filter: brightness(0.98);
  box-shadow: inset 0 0 0 1.5px var(--text-primary);
  z-index: 2;
}

.tm-tile.is-selected {
  box-shadow: inset 0 0 0 1.8px var(--obs-ctx-assistant, #2684ff);
  z-index: 3;
}

.tm-tile.has-dropped {
  background-image: repeating-linear-gradient(
    -45deg,
    transparent,
    transparent 6px,
    rgba(212, 155, 0, 0.08) 6px,
    rgba(212, 155, 0, 0.08) 12px
  );
}

.tm-tile.tone-system {
  background-color: color-mix(in srgb, var(--obs-ctx-system, #8053e3) 14%, var(--bg-primary));
}
.tm-tile.tone-tools {
  background-color: color-mix(in srgb, var(--obs-ctx-assistant, #5b9fe8) 15%, var(--bg-primary));
}
.tm-tile.tone-user {
  background-color: color-mix(in srgb, var(--obs-ctx-user, #44a56b) 14%, var(--bg-primary));
}
.tm-tile.tone-inject {
  background-color: color-mix(in srgb, var(--obs-ctx-inject, #c442a7) 13%, var(--bg-primary));
}
.tm-tile.tone-skill {
  background-color: color-mix(in srgb, var(--obs-ctx-skill, #e66d26) 14%, var(--bg-primary));
}
.tm-tile.tone-tool-output {
  background-color: color-mix(in srgb, var(--obs-ctx-tool-output, #3ab4a4) 15%, var(--bg-primary));
}
.tm-tile.tone-other {
  background-color: var(--bg-secondary);
}

.tm-tile__head {
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 4px;
  min-width: 0;
}

.tm-tile__label {
  font-family: var(--font-mono);
  font-size: 11.5px;
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
  padding: 3px 4px;
}

.tm-tile.is-small .tm-tile__label {
  font-size: 9.5px;
}

.tm-hint {
  margin: 0;
  font-size: 10.5px;
  color: var(--text-muted);
}

/* Detail Column */
.tm-detail {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding-top: 8px;
  border-top: 1px solid var(--border-muted);
  min-width: 0;
}

@container (min-width: 560px) {
  .tm-detail {
    padding-top: 0;
    padding-left: 16px;
    border-top: 0;
    border-inline-start: 1px solid var(--border-muted);
  }
}

.tm-detail--placeholder {
  display: none;
}

@container (min-width: 560px) {
  .tm-detail--placeholder {
    display: flex;
  }
}

.tm-detail__top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.tm-detail__cat {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 11.5px;
  color: var(--text-secondary);
}

.tm-detail__dot {
  width: 8px;
  height: 8px;
  border-radius: 2px;
}

.tm-close-btn {
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.tm-close-btn:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.tm-detail__name {
  font-family: var(--font-mono);
  font-size: 15px;
  font-weight: 600;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.tm-detail__source {
  font-size: 11px;
  color: var(--text-muted);
  overflow-wrap: anywhere;
}

.tm-detail__hero {
  display: flex;
  align-items: baseline;
  gap: 5px;
  margin-top: 4px;
}

.tm-detail__hero-num {
  font-size: 28px;
  line-height: 1.1;
  font-weight: 700;
  letter-spacing: -0.03em;
  color: var(--obs-ctx-assistant, #2684ff);
}

.tm-detail__hero-unit {
  font-size: 12px;
  color: var(--text-secondary);
}

.tm-detail__share {
  font-size: 11.5px;
  color: var(--text-secondary);
}

.tm-placeholder-copy {
  margin: 4px 0 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-muted);
}

.tm-drawer__stats {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 6px 0;
  border-top: 1px solid var(--border-muted);
  border-bottom: 1px solid var(--border-muted);
}

.tm-stat-row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 11px;
}

.tm-stat-row.is-warn strong {
  color: #b87800;
}

.tm-stat-label {
  color: var(--text-muted);
}

.tm-stat-row strong {
  color: var(--text-primary);
}

.tm-stat-row small {
  color: var(--text-muted);
  font-size: 10.5px;
}

.tm-drawer__items {
  display: flex;
  flex-direction: column;
  max-height: 132px;
  overflow-y: auto;
}

.tm-item-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 0;
  border-bottom: 1px solid var(--border-muted);
  font-size: 10.5px;
}

.tm-item-row:last-child {
  border-bottom: 0;
}

.tm-item-row.is-dropped {
  color: #b87800;
}

.tm-item-source {
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
  flex: 1;
}

.tm-item-status {
  color: var(--text-muted);
  font-size: 10px;
  flex: none;
}

.tm-item-cost {
  color: var(--text-secondary);
  white-space: nowrap;
  flex: none;
}

.tm-diagnose-link {
  align-self: flex-start;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  margin-top: 4px;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--obs-ctx-assistant, #2684ff);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.tm-diagnose-link:hover {
  text-decoration: underline;
  text-underline-offset: 3px;
}

/* Dropped Warning Bar */
.tm-dropped-warning {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 10px;
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d49b00) 40%, var(--border-muted));
  border-radius: 6px;
  background: var(--bg-status-warn);
  font-size: 11.5px;
  color: var(--text-primary);
}

.tm-dropped-warning__icon {
  color: #b87800;
  flex: none;
}

.tm-dropped-warning__body {
  display: flex;
  align-items: baseline;
  gap: 6px;
  flex-wrap: wrap;
}

.tm-reconcile {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 14px;
  padding-top: 6px;
  border-top: 1px solid var(--border-muted);
  font-size: 10.5px;
  color: var(--text-muted);
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}
</style>
