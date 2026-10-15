<script setup lang="ts">
/**
 * 预览画廊主组件 — 卡片岛式重构版
 * 左岛：页面/组件树 · 中岛：预览视口（沉浸）· 右岛：场景控制
 * 跟随全局 usePanelStyles 材质（opaque/translucent/blur），不再自管主题
 */
import { computed, onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  LayoutDashboard,
  MessageSquare,
  MessagesSquare,
  GitBranch,
  Code2,
  Settings,
  Store,
  Activity,
  Wrench,
  Brain,
  ListTree,
  BarChart3,
  GitCompare,
  Edit3,
  Folder,
  RefreshCw,
  ChevronRight,
  ChevronDown,
} from '@lucide/vue'
import { UiBadge } from '@/components/ui'
import PreviewIslandCard from './PreviewIslandCard.vue'
import PagePreview from './PagePreview.vue'
import ComponentPreview from './ComponentPreview.vue'
import { installPreviewApi, restoreRealApi, unmockedMethods } from './apiBridge'
import { SCENARIOS, buildScenarioData, type ScenarioId } from './scenarios'
import type { MockDataBundle } from './mockData'

type ItemType = 'page' | 'component'
interface TreeItem {
  id: string
  name: string
  type: ItemType
  icon: typeof LayoutDashboard
}

const PAGES: TreeItem[] = [
  { id: 'ChatPanel', name: 'ChatPanel', type: 'page', icon: MessageSquare },
  { id: 'ConversationFlow', name: 'ConversationFlow', type: 'page', icon: MessagesSquare },
  { id: 'GitPanel', name: 'GitPanel', type: 'page', icon: GitBranch },
  { id: 'CodePage', name: 'CodePage', type: 'page', icon: Code2 },
  { id: 'SettingsPage', name: 'SettingsPage', type: 'page', icon: Settings },
  { id: 'MarketPage', name: 'MarketPage', type: 'page', icon: Store },
]

const COMPONENTS: TreeItem[] = [
  { id: 'AgentActivityBanner', name: 'AgentActivityBanner', type: 'component', icon: Activity },
  { id: 'ToolCallCard', name: 'ToolCallCard', type: 'component', icon: Wrench },
  { id: 'ThinkingProcess', name: 'ThinkingProcess', type: 'component', icon: Brain },
  { id: 'ToolExecutionTimeline', name: 'ToolExecutionTimeline', type: 'component', icon: ListTree },
  { id: 'ToolCatalogBrowser', name: 'ToolCatalogBrowser', type: 'component', icon: Wrench },
  { id: 'ToolStatsDashboard', name: 'ToolStatsDashboard', type: 'component', icon: BarChart3 },
  { id: 'DiffViewer', name: 'DiffViewer', type: 'component', icon: GitCompare },
  { id: 'CommitMessageEditor', name: 'CommitMessageEditor', type: 'component', icon: Edit3 },
  { id: 'FileTreePanel', name: 'FileTreePanel', type: 'component', icon: Folder },
]

const { t } = useI18n()
const itemLabel = (item: TreeItem) => t(`debugStudio.preview.items.${item.id}`)
const scenarioText = (id: ScenarioId, field: 'label' | 'description') =>
  t(`debugStudio.preview.scenario.${id.replace(/-(\w)/g, (_, c: string) => c.toUpperCase())}.${field}`)
const selectedItem = ref<TreeItem>(PAGES[0])
const scenarioId = ref<ScenarioId>('populated')

// 真实控件预览：挂载期间把 mock api 补丁到单例上（引用计数，卸载时还原）。
// 必须在子组件 setup 之前执行——本组件 setup 先于 PagePreview/ComponentPreview。
installPreviewApi(scenarioId)
onBeforeUnmount(restoreRealApi)

const previewWidth = ref(100)
const showApprovals = ref(true)
const showToolCalls = ref(true)
const showErrors = ref(false)
const refreshKey = ref(0)
const expandedGroups = ref<Set<string>>(new Set(['pages', 'components']))

const mockData = computed<MockDataBundle>(() => buildScenarioData(scenarioId.value, 'sess-tinadec-1001'))
const currentScenario = computed(() => SCENARIOS.find((s) => s.id === scenarioId.value) ?? SCENARIOS[0])

const stats = computed(() => {
  const d = mockData.value
  return [
    { label: t('debugStudio.preview.stats.projects'), value: d.projects.length },
    { label: t('debugStudio.preview.stats.sessions'), value: d.sessions.length },
    { label: t('debugStudio.preview.stats.messages'), value: d.messages.length },
    { label: t('debugStudio.preview.stats.approvals'), value: d.approvals.length },
    { label: t('debugStudio.preview.stats.toolCalls'), value: d.toolExecutions.length },
    { label: t('debugStudio.preview.stats.events'), value: d.events.length },
    { label: t('debugStudio.preview.stats.agents'), value: d.agents.length },
    { label: t('debugStudio.preview.stats.tools'), value: d.tools.length },
  ]
})

function selectItem(item: TreeItem) { selectedItem.value = item }
function toggleGroup(group: string) {
  const next = new Set(expandedGroups.value)
  if (next.has(group)) next.delete(group)
  else next.add(group)
  expandedGroups.value = next
}
function refresh() { refreshKey.value++ }
</script>

<template>
  <div class="preview-gallery">
    <!-- 左岛：导航树 -->
    <PreviewIslandCard variant="section" padding="none" class="gallery-sidebar">
      <template #header>
        <div class="sidebar-head">
          <LayoutDashboard :size="14" />
          <span>{{ t('debugStudio.preview.title') }}</span>
        </div>
      </template>
      <div class="sidebar-tree">
        <div class="tree-group">
          <button class="tree-group-head" @click="toggleGroup('pages')">
            <component :is="expandedGroups.has('pages') ? ChevronDown : ChevronRight" :size="12" />
            <span>{{ t('debugStudio.preview.pages') }}</span>
            <small>{{ PAGES.length }}</small>
          </button>
          <template v-if="expandedGroups.has('pages')">
            <button
              v-for="item in PAGES"
              :key="item.id"
              class="tree-item"
              :class="{ active: selectedItem.id === item.id }"
              @click="selectItem(item)"
            >
              <component :is="item.icon" :size="13" />
              <span>{{ itemLabel(item) }}</span>
            </button>
          </template>
        </div>
        <div class="tree-group">
          <button class="tree-group-head" @click="toggleGroup('components')">
            <component :is="expandedGroups.has('components') ? ChevronDown : ChevronRight" :size="12" />
            <span>{{ t('debugStudio.preview.components') }}</span>
            <small>{{ COMPONENTS.length }}</small>
          </button>
          <template v-if="expandedGroups.has('components')">
            <button
              v-for="item in COMPONENTS"
              :key="item.id"
              class="tree-item"
              :class="{ active: selectedItem.id === item.id }"
              @click="selectItem(item)"
            >
              <component :is="item.icon" :size="13" />
              <span>{{ itemLabel(item) }}</span>
            </button>
          </template>
        </div>
      </div>
    </PreviewIslandCard>

    <!-- 中岛：预览视口（沉浸外层 + 内层岛） -->
    <div class="gallery-center">
      <PreviewIslandCard variant="section" padding="none" class="gallery-main">
        <template #header>
          <div class="gallery-toolbar">
            <div class="toolbar-left">
              <component :is="selectedItem.icon" :size="14" />
              <span class="toolbar-name">{{ itemLabel(selectedItem) }}</span>
              <span class="toolbar-type" :class="selectedItem.type">{{ selectedItem.type === 'page' ? t('debugStudio.preview.pages') : t('debugStudio.preview.components') }}</span>
              <UiBadge variant="outline" class="toolbar-scenario">{{ scenarioText(currentScenario.id, 'label') }}</UiBadge>
            </div>
            <div class="toolbar-right">
              <button class="toolbar-btn" :title="t('debugStudio.preview.refresh')" @click="refresh">
                <RefreshCw :size="13" />
              </button>
            </div>
          </div>
        </template>
        <div class="gallery-content" :style="{ '--preview-width': `${previewWidth}%` }">
          <PreviewIslandCard variant="raised" padding="none" class="preview-viewport">
            <PagePreview
              v-if="selectedItem.type === 'page'"
              :key="`${selectedItem.id}-${scenarioId}-${refreshKey}`"
              :page-name="selectedItem.name"
              :data="mockData"
            />
            <ComponentPreview
              v-else
              :key="`${selectedItem.id}-${scenarioId}-${refreshKey}`"
              :component-name="selectedItem.name"
              :data="mockData"
            />
          </PreviewIslandCard>
        </div>
        <template #footer>
          <div class="gallery-statusbar">
            <div class="status-stats">
              <span v-for="s in stats" :key="s.label" class="status-stat">
                <small>{{ s.label }}</small>
                <strong>{{ s.value }}</strong>
              </span>
            </div>
            <div class="status-scenario">
              <span class="status-scenario-label">{{ t('debugStudio.preview.currentScenario') }}</span>
              <strong>{{ scenarioText(currentScenario.id, 'label') }}</strong>
            </div>
          </div>
        </template>
      </PreviewIslandCard>
    </div>

    <!-- 右岛：场景控制 -->
    <PreviewIslandCard variant="section" padding="none" class="gallery-controls">
      <template #header>
        <div class="controls-head">
          <span>{{ t('debugStudio.preview.controls') }}</span>
        </div>
      </template>
      <div class="controls-body">
        <PreviewIslandCard variant="raised" padding="sm" class="controls-section">
          <label class="controls-label">{{ t('debugStudio.preview.presets') }}</label>
          <div class="scenario-list">
            <PreviewIslandCard
              v-for="s in SCENARIOS"
              :key="s.id"
              variant="raised"
              padding="sm"
              :selected="scenarioId === s.id"
              :hoverable="true"
              class="scenario-card"
              @click="scenarioId = s.id"
            >
              <span class="scenario-label">{{ scenarioText(s.id, 'label') }}</span>
              <small class="scenario-desc">{{ scenarioText(s.id, 'description') }}</small>
            </PreviewIslandCard>
          </div>
        </PreviewIslandCard>

        <PreviewIslandCard variant="raised" padding="sm" class="controls-section">
          <label class="controls-label">{{ t('debugStudio.preview.description') }}</label>
          <p class="scenario-detail">{{ scenarioText(currentScenario.id, 'description') }}</p>
        </PreviewIslandCard>

        <PreviewIslandCard variant="raised" padding="sm" class="controls-section">
          <label class="controls-label">{{ t('debugStudio.preview.dataOptions') }}</label>
          <div class="toggle-row">
            <span>{{ t('debugStudio.preview.showApprovals') }}</span>
            <button class="toggle-switch" :class="{ on: showApprovals }" @click="showApprovals = !showApprovals">
              <span class="toggle-knob" />
            </button>
          </div>
          <div class="toggle-row">
            <span>{{ t('debugStudio.preview.showToolCalls') }}</span>
            <button class="toggle-switch" :class="{ on: showToolCalls }" @click="showToolCalls = !showToolCalls">
              <span class="toggle-knob" />
            </button>
          </div>
          <div class="toggle-row">
            <span>{{ t('debugStudio.preview.showErrors') }}</span>
            <button class="toggle-switch" :class="{ on: showErrors }" @click="showErrors = !showErrors">
              <span class="toggle-knob" />
            </button>
          </div>
        </PreviewIslandCard>

        <PreviewIslandCard variant="raised" padding="sm" class="controls-section">
          <label class="controls-label">{{ t('debugStudio.preview.width', { width: previewWidth }) }}</label>
          <input type="range" min="40" max="100" v-model.number="previewWidth" class="width-slider" />
        </PreviewIslandCard>

        <PreviewIslandCard v-if="unmockedMethods.size > 0" variant="raised" padding="sm" class="controls-section">
          <label class="controls-label">{{ t('debugStudio.preview.unmocked') }}</label>
          <div class="unmocked-list">
            <UiBadge v-for="m in unmockedMethods" :key="m" variant="outline">{{ m }}</UiBadge>
          </div>
        </PreviewIslandCard>
      </div>
    </PreviewIslandCard>
  </div>
</template>

<style scoped>
.preview-gallery {
  display: flex;
  gap: 8px;
  height: 100%;
  padding: 8px;
  background: transparent;
  color: var(--text-primary, #c9d1d9);
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans SC', sans-serif;
  font-size: 13px;
  min-height: 0;
}

/* ---- 左岛 ---- */
.gallery-sidebar {
  width: 260px;
  flex-shrink: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
.gallery-sidebar :deep(.island-body) {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.sidebar-head {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  font-weight: 700;
  color: var(--accent-primary, #2ec4b6);
  text-transform: uppercase;
  letter-spacing: 0.04em;
  width: 100%;
}

.sidebar-tree {
  flex: 1;
  overflow: auto;
  padding: 6px 0;
}

.tree-group { margin-bottom: 4px; }

.tree-group-head {
  display: flex;
  align-items: center;
  gap: 4px;
  width: 100%;
  padding: 6px 12px;
  background: none;
  border: none;
  color: var(--text-muted, #6e7681);
  font-size: 11px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  cursor: pointer;
}
.tree-group-head:hover { color: var(--text-secondary, #7d8590); }
.tree-group-head small {
  margin-left: auto;
  background: var(--surface-raised, #1a1f29);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  padding: 1px 6px;
  border-radius: 8px;
  font-size: 10px;
}

.tree-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: calc(100% - 12px);
  margin: 2px 6px;
  padding: 7px 10px 7px 26px;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 8px;
  color: var(--text-secondary, #7d8590);
  font-size: 12px;
  cursor: pointer;
  text-align: left;
  transition: background 0.15s, color 0.15s, box-shadow 0.18s, transform 0.18s, border-color 0.18s;
}
.tree-item:hover {
  background: var(--surface-hover, #1a1f29);
  color: var(--text-primary, #c9d1d9);
  border-color: var(--border-card, rgba(0,0,0,.08));
  box-shadow: var(--shadow-card-subtle);
  transform: translateY(-1px);
}
.tree-item.active {
  background: var(--surface-selected, #0d2e2a);
  color: var(--accent-primary, #2ec4b6);
  border-color: var(--accent-primary, #2ec4b6);
  box-shadow: var(--shadow-card-subtle);
  font-weight: 600;
}

/* ---- 中岛 ---- */
.gallery-center {
  flex: 1;
  min-width: 0;
  min-height: 0;
  display: flex;
}
.gallery-main {
  flex: 1;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
.gallery-main :deep(.island-body) {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: 8px;
  background: transparent;
}

.gallery-toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  width: 100%;
  gap: 12px;
}
.toolbar-left { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.toolbar-name { font-size: 13px; font-weight: 600; color: var(--text-primary); }
.toolbar-type {
  font-size: 10px;
  font-weight: 700;
  padding: 2px 7px;
  border-radius: 999px;
  text-transform: uppercase;
  letter-spacing: 0.02em;
}
.toolbar-type.page { background: color-mix(in srgb, var(--accent-primary, #2ec4b6) 14%, transparent); color: var(--accent-primary, #2ec4b6); }
.toolbar-type.component { background: color-mix(in srgb, #bc8cff 14%, transparent); color: #bc8cff; }
.toolbar-scenario { margin-left: 4px; }
.toolbar-right { display: flex; gap: 4px; }
.toolbar-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 30px;
  background: var(--surface-raised, #1a1f29);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 8px;
  color: var(--text-muted, #6e7681);
  cursor: pointer;
  transition: background 0.15s, color 0.15s, box-shadow 0.18s, transform 0.18s;
}
.toolbar-btn:hover {
  background: var(--surface-hover, #1a1f29);
  color: var(--text-primary);
  box-shadow: var(--shadow-card-subtle);
  transform: translateY(-1px);
}

.gallery-content {
  flex: 1;
  overflow: hidden;
  min-height: 0;
  display: flex;
  justify-content: center;
  padding: 4px;
}

/* 视口是固定高度的单一画布：高度恒等于可用空间，
   滚动统一收敛到它的 island-body（唯一滚动容器） */
.preview-viewport {
  width: var(--preview-width, 100%);
  max-width: 100%;
  height: 100%;
}
.preview-viewport :deep(.island-body) {
  height: 100%;
  overflow: auto;
  padding: 0;
}

.gallery-statusbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  width: 100%;
  flex-wrap: wrap;
}
.status-stats { display: flex; gap: 14px; flex-wrap: wrap; }
.status-stat { display: flex; align-items: center; gap: 4px; }
.status-stat small { font-size: 10px; color: var(--text-muted, #6e7681); }
.status-stat strong { font-size: 12px; color: var(--text-primary); }
.status-scenario { display: flex; align-items: center; gap: 4px; }
.status-scenario-label { font-size: 11px; color: var(--text-muted, #6e7681); }
.status-scenario strong { font-size: 12px; color: var(--accent-primary, #2ec4b6); }

/* ---- 右岛 ---- */
.gallery-controls {
  width: 300px;
  flex-shrink: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
.gallery-controls :deep(.island-body) {
  flex: 1;
  min-height: 0;
  overflow: auto;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 8px;
  background: transparent;
}
.controls-head {
  font-size: 12px;
  font-weight: 700;
  color: var(--accent-primary, #2ec4b6);
  text-transform: uppercase;
  letter-spacing: 0.04em;
  width: 100%;
}
.controls-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.controls-section {
  flex-shrink: 0;
}
.controls-label {
  display: block;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted, #6e7681);
  margin-bottom: 8px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.scenario-list { display: flex; flex-direction: column; gap: 6px; }
.scenario-card {
  cursor: pointer;
  text-align: left;
}
.scenario-card :deep(.island-body) {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 10px 12px;
}
.scenario-label { font-size: 12px; font-weight: 600; color: var(--text-primary); }
.scenario-desc {
  font-size: 11px;
  color: var(--text-muted, #6e7681);
  line-height: 1.4;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
.scenario-detail {
  font-size: 12px;
  color: var(--text-secondary, #7d8590);
  line-height: 1.5;
  margin: 0;
}
.toggle-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 6px 0;
  font-size: 12px;
  color: var(--text-secondary, #7d8590);
}
.toggle-switch {
  position: relative;
  width: 34px;
  height: 20px;
  background: var(--border-default, #1a1f29);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 999px;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
  flex-shrink: 0;
}
.toggle-switch.on { background: var(--accent-success, #238636); border-color: var(--accent-success, #238636); }
.toggle-knob {
  position: absolute;
  top: 2px;
  left: 2px;
  width: 14px;
  height: 14px;
  background: #fff;
  border-radius: 50%;
  box-shadow: 0 1px 3px rgba(0,0,0,.2);
  transition: transform 0.15s;
}
.toggle-switch.on .toggle-knob { transform: translateX(14px); }
.width-slider {
  width: 100%;
  accent-color: var(--accent-primary, #2ec4b6);
}

.unmocked-list {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

/* ---- 响应式：窄屏右岛下沉，超窄左岛收起 ---- */
@media (max-width: 1100px) {
  .preview-gallery { flex-wrap: wrap; }
  .gallery-sidebar { width: 220px; }
  .gallery-controls { width: 100%; flex-direction: row; flex-wrap: wrap; }
  .gallery-controls :deep(.island-body) { flex-direction: row; flex-wrap: wrap; }
  .controls-section { flex: 1 1 260px; }
}
@media (max-width: 700px) {
  .gallery-sidebar { width: 56px; }
  .sidebar-head span, .tree-group-head span, .tree-group-head small, .tree-item span { display: none; }
  .tree-item { padding-left: 10px; justify-content: center; }
}

@media (prefers-reduced-motion: reduce) {
  .tree-item, .toolbar-btn, .scenario-card { transition: none; transform: none !important; }
}
</style>
