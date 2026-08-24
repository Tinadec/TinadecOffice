<script setup lang="ts">
/**
 * 预览画廊主组件
 * 参考 Storybook 的设计理念，提供页面/组件的可视化预览。
 * 左侧栏：页面/组件树
 * 右侧栏：场景控制面板
 * 主预览区：渲染选中的页面/组件
 */
import { computed, ref, watch } from 'vue'
import {
  LayoutDashboard,
  Home,
  MessageSquare,
  MessagesSquare,
  PanelRight,
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
  Sun,
  Moon,
  ChevronRight,
  ChevronDown,
} from '@lucide/vue'
import { UiBadge } from '@/components/ui'
import PagePreview from './PagePreview.vue'
import ComponentPreview from './ComponentPreview.vue'
import { SCENARIOS, buildScenarioData, type ScenarioId } from './scenarios'
import type { MockDataBundle } from './mockData'

// ---- 选中项类型 ----
type ItemType = 'page' | 'component'
interface TreeItem {
  id: string
  name: string
  label: string
  type: ItemType
  icon: typeof Home
}

// ---- 页面/组件树 ----
const PAGES: TreeItem[] = [
  { id: 'HomePage', name: 'HomePage', label: '首页（三栏布局）', type: 'page', icon: Home },
  { id: 'ChatPanel', name: 'ChatPanel', label: '聊天面板', type: 'page', icon: MessageSquare },
  { id: 'ConversationFlow', name: 'ConversationFlow', label: '对话消息流', type: 'page', icon: MessagesSquare },
  { id: 'ContextPanel', name: 'ContextPanel', label: '上下文面板', type: 'page', icon: PanelRight },
  { id: 'GitPanel', name: 'GitPanel', label: 'Git 管理', type: 'page', icon: GitBranch },
  { id: 'CodePage', name: 'CodePage', label: '代码编辑器', type: 'page', icon: Code2 },
  { id: 'SettingsPage', name: 'SettingsPage', label: '设置页', type: 'page', icon: Settings },
  { id: 'MarketPage', name: 'MarketPage', label: '扩展市场', type: 'page', icon: Store },
]

const COMPONENTS: TreeItem[] = [
  { id: 'AgentActivityBanner', name: 'AgentActivityBanner', label: 'Agent 活动横幅', type: 'component', icon: Activity },
  { id: 'ToolCallCard', name: 'ToolCallCard', label: '工具调用卡片', type: 'component', icon: Wrench },
  { id: 'ThinkingProcess', name: 'ThinkingProcess', label: '思考过程', type: 'component', icon: Brain },
  { id: 'ToolExecutionTimeline', name: 'ToolExecutionTimeline', label: '工具执行时间线', type: 'component', icon: ListTree },
  { id: 'ToolCatalogBrowser', name: 'ToolCatalogBrowser', label: '工具目录浏览器', type: 'component', icon: Wrench },
  { id: 'ToolStatsDashboard', name: 'ToolStatsDashboard', label: '工具统计仪表板', type: 'component', icon: BarChart3 },
  { id: 'DiffViewer', name: 'DiffViewer', label: 'Diff 查看器', type: 'component', icon: GitCompare },
  { id: 'CommitMessageEditor', name: 'CommitMessageEditor', label: 'Commit 消息编辑器', type: 'component', icon: Edit3 },
  { id: 'FileTreePanel', name: 'FileTreePanel', label: '文件树面板', type: 'component', icon: Folder },
]

// ---- 状态 ----
const selectedItem = ref<TreeItem>(PAGES[0])
const scenarioId = ref<ScenarioId>('populated')
const theme = ref<'dark' | 'light'>('dark')
const previewWidth = ref(100)
const showApprovals = ref(true)
const showToolCalls = ref(true)
const showErrors = ref(false)
const refreshKey = ref(0)
const expandedGroups = ref<Set<string>>(new Set(['pages', 'components']))

// ---- 计算数据 ----
const mockData = computed<MockDataBundle>(() => {
  return buildScenarioData(scenarioId.value, 'sess-tinadec-1001')
})

const currentScenario = computed(() => SCENARIOS.find((s) => s.id === scenarioId.value) ?? SCENARIOS[0])

// ---- 数据统计 ----
const stats = computed(() => {
  const d = mockData.value
  return [
    { label: '项目', value: d.projects.length },
    { label: '会话', value: d.sessions.length },
    { label: '消息', value: d.messages.length },
    { label: '审批', value: d.approvals.length },
    { label: '工具调用', value: d.toolExecutions.length },
    { label: '事件', value: d.events.length },
    { label: '智能体', value: d.agents.length },
    { label: '工具', value: d.tools.length },
  ]
})

// ---- 操作 ----
function selectItem(item: TreeItem) {
  selectedItem.value = item
}

function toggleGroup(group: string) {
  const next = new Set(expandedGroups.value)
  if (next.has(group)) next.delete(group)
  else next.add(group)
  expandedGroups.value = next
}

function refresh() {
  refreshKey.value++
}

function setTheme(t: 'dark' | 'light') {
  theme.value = t
}

// 主题切换时更新 CSS 变量
watch(theme, (t) => {
  const root = document.documentElement
  if (t === 'light') {
    root.classList.remove('dark')
  } else {
    root.classList.add('dark')
  }
}, { immediate: true })
</script>

<template>
  <div class="preview-gallery" :class="`theme-${theme}`">
    <!-- 左侧栏：页面/组件树 -->
    <aside class="gallery-sidebar">
      <div class="sidebar-head">
        <LayoutDashboard :size="14" />
        <span>预览画廊</span>
      </div>
      <div class="sidebar-tree">
        <!-- 页面组 -->
        <div class="tree-group">
          <button class="tree-group-head" @click="toggleGroup('pages')">
            <component :is="expandedGroups.has('pages') ? ChevronDown : ChevronRight" :size="12" />
            <span>页面</span>
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
              <span>{{ item.label }}</span>
            </button>
          </template>
        </div>

        <!-- 组件组 -->
        <div class="tree-group">
          <button class="tree-group-head" @click="toggleGroup('components')">
            <component :is="expandedGroups.has('components') ? ChevronDown : ChevronRight" :size="12" />
            <span>组件</span>
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
              <span>{{ item.label }}</span>
            </button>
          </template>
        </div>
      </div>
    </aside>

    <!-- 主预览区 -->
    <main class="gallery-main">
      <!-- 顶部工具栏 -->
      <div class="gallery-toolbar">
        <div class="toolbar-left">
          <component :is="selectedItem.icon" :size="14" />
          <span class="toolbar-name">{{ selectedItem.label }}</span>
          <span class="toolbar-type" :class="selectedItem.type">{{ selectedItem.type === 'page' ? '页面' : '组件' }}</span>
          <UiBadge variant="outline" class="toolbar-scenario">{{ currentScenario.label }}</UiBadge>
        </div>
        <div class="toolbar-right">
          <button class="toolbar-btn" title="刷新预览" @click="refresh">
            <RefreshCw :size="13" />
          </button>
        </div>
      </div>

      <!-- 内容区 -->
      <div class="gallery-content" :style="{ '--preview-width': `${previewWidth}%` }">
        <div class="preview-viewport">
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
        </div>
      </div>

      <!-- 底部状态栏 -->
      <div class="gallery-statusbar">
        <div class="status-stats">
          <span v-for="s in stats" :key="s.label" class="status-stat">
            <small>{{ s.label }}</small>
            <strong>{{ s.value }}</strong>
          </span>
        </div>
        <div class="status-scenario">
          <span class="status-scenario-label">当前场景：</span>
          <strong>{{ currentScenario.label }}</strong>
        </div>
      </div>
    </main>

    <!-- 右侧栏：场景控制面板 -->
    <aside class="gallery-controls">
      <div class="controls-head">
        <span>场景控制</span>
      </div>

      <!-- 场景选择 -->
      <div class="controls-section">
        <label class="controls-label">场景预设</label>
        <div class="scenario-list">
          <button
            v-for="s in SCENARIOS"
            :key="s.id"
            class="scenario-item"
            :class="{ active: scenarioId === s.id }"
            @click="scenarioId = s.id"
          >
            <span class="scenario-label">{{ s.label }}</span>
            <small class="scenario-desc">{{ s.description }}</small>
          </button>
        </div>
      </div>

      <!-- 当前场景描述 -->
      <div class="controls-section">
        <label class="controls-label">场景描述</label>
        <p class="scenario-detail">{{ currentScenario.description }}</p>
      </div>

      <!-- 数据覆盖选项 -->
      <div class="controls-section">
        <label class="controls-label">数据选项</label>
        <div class="toggle-row">
          <span>显示审批</span>
          <button class="toggle-switch" :class="{ on: showApprovals }" @click="showApprovals = !showApprovals">
            <span class="toggle-knob" />
          </button>
        </div>
        <div class="toggle-row">
          <span>显示工具调用</span>
          <button class="toggle-switch" :class="{ on: showToolCalls }" @click="showToolCalls = !showToolCalls">
            <span class="toggle-knob" />
          </button>
        </div>
        <div class="toggle-row">
          <span>显示错误</span>
          <button class="toggle-switch" :class="{ on: showErrors }" @click="showErrors = !showErrors">
            <span class="toggle-knob" />
          </button>
        </div>
      </div>

      <!-- 主题切换 -->
      <div class="controls-section">
        <label class="controls-label">主题</label>
        <div class="theme-buttons">
          <button class="theme-btn" :class="{ active: theme === 'dark' }" @click="setTheme('dark')">
            <Moon :size="13" />
            <span>深色</span>
          </button>
          <button class="theme-btn" :class="{ active: theme === 'light' }" @click="setTheme('light')">
            <Sun :size="13" />
            <span>浅色</span>
          </button>
        </div>
      </div>

      <!-- 尺寸控制 -->
      <div class="controls-section">
        <label class="controls-label">预览宽度 ({{ previewWidth }}%)</label>
        <input
          type="range"
          min="40"
          max="100"
          v-model.number="previewWidth"
          class="width-slider"
        />
      </div>
    </aside>
  </div>
</template>

<style scoped>
.preview-gallery {
  display: flex;
  height: 100%;
  background: #0d1117;
  color: #e6edf3;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans SC', sans-serif;
  font-size: 13px;
}

/* ---- 左侧栏 ---- */
.gallery-sidebar {
  width: 240px;
  flex-shrink: 0;
  background: #161b22;
  border-right: 1px solid #30363d;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.sidebar-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  font-size: 12px;
  font-weight: 700;
  color: #58a6ff;
  border-bottom: 1px solid #30363d;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.sidebar-tree {
  flex: 1;
  overflow: auto;
  padding: 4px 0;
}

.tree-group {
  margin-bottom: 4px;
}

.tree-group-head {
  display: flex;
  align-items: center;
  gap: 4px;
  width: 100%;
  padding: 6px 12px;
  background: none;
  border: none;
  color: #8b949e;
  font-size: 11px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  cursor: pointer;
}

.tree-group-head:hover {
  color: #c9d1d9;
}

.tree-group-head small {
  margin-left: auto;
  background: #21262d;
  padding: 1px 6px;
  border-radius: 8px;
  font-size: 10px;
}

.tree-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 12px 6px 28px;
  background: none;
  border: none;
  color: #c9d1d9;
  font-size: 12px;
  cursor: pointer;
  text-align: left;
  transition: background 0.12s, color 0.12s;
}

.tree-item:hover {
  background: #21262d;
  color: #e6edf3;
}

.tree-item.active {
  background: rgba(56, 139, 253, 0.15);
  color: #58a6ff;
  border-left: 2px solid #58a6ff;
  padding-left: 26px;
}

/* ---- 主预览区 ---- */
.gallery-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.gallery-toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 8px 16px;
  background: #161b22;
  border-bottom: 1px solid #30363d;
  flex-shrink: 0;
}

.toolbar-left {
  display: flex;
  align-items: center;
  gap: 8px;
}

.toolbar-name {
  font-size: 13px;
  font-weight: 600;
  color: #e6edf3;
}

.toolbar-type {
  font-size: 10px;
  font-weight: 700;
  padding: 1px 6px;
  border-radius: 8px;
  text-transform: uppercase;
}

.toolbar-type.page {
  background: rgba(88, 166, 255, 0.14);
  color: #58a6ff;
}

.toolbar-type.component {
  background: rgba(188, 140, 255, 0.14);
  color: #bc8cff;
}

.toolbar-scenario {
  margin-left: 8px;
}

.toolbar-right {
  display: flex;
  gap: 4px;
}

.toolbar-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  background: none;
  border: 1px solid #30363d;
  border-radius: 6px;
  color: #8b949e;
  cursor: pointer;
  transition: background 0.12s, color 0.12s;
}

.toolbar-btn:hover {
  background: #21262d;
  color: #e6edf3;
}

.gallery-content {
  flex: 1;
  overflow: auto;
  min-height: 0;
  display: flex;
  justify-content: center;
  background: #010409;
}

.preview-viewport {
  width: var(--preview-width, 100%);
  max-width: 100%;
  height: 100%;
  overflow: hidden;
  background: #0d1117;
  border-left: 1px solid #30363d;
  border-right: 1px solid #30363d;
}

/* ---- 底部状态栏 ---- */
.gallery-statusbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 6px 16px;
  background: #161b22;
  border-top: 1px solid #30363d;
  flex-shrink: 0;
}

.status-stats {
  display: flex;
  gap: 16px;
  flex-wrap: wrap;
}

.status-stat {
  display: flex;
  align-items: center;
  gap: 4px;
}

.status-stat small {
  font-size: 10px;
  color: #8b949e;
}

.status-stat strong {
  font-size: 12px;
  color: #e6edf3;
}

.status-scenario {
  display: flex;
  align-items: center;
  gap: 4px;
}

.status-scenario-label {
  font-size: 11px;
  color: #8b949e;
}

.status-scenario strong {
  font-size: 12px;
  color: #58a6ff;
}

/* ---- 右侧栏 ---- */
.gallery-controls {
  width: 280px;
  flex-shrink: 0;
  background: #161b22;
  border-left: 1px solid #30363d;
  display: flex;
  flex-direction: column;
  overflow: auto;
}

.controls-head {
  display: flex;
  align-items: center;
  padding: 10px 12px;
  font-size: 12px;
  font-weight: 700;
  color: #58a6ff;
  border-bottom: 1px solid #30363d;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.controls-section {
  padding: 12px;
  border-bottom: 1px solid #21262d;
}

.controls-label {
  display: block;
  font-size: 11px;
  font-weight: 600;
  color: #8b949e;
  margin-bottom: 8px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.scenario-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.scenario-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 8px 10px;
  background: #0d1117;
  border: 1px solid #30363d;
  border-radius: 6px;
  cursor: pointer;
  text-align: left;
  transition: border-color 0.12s, background 0.12s;
}

.scenario-item:hover {
  border-color: #58a6ff;
}

.scenario-item.active {
  border-color: #58a6ff;
  background: rgba(56, 139, 253, 0.1);
}

.scenario-label {
  font-size: 12px;
  font-weight: 600;
  color: #e6edf3;
}

.scenario-desc {
  font-size: 10px;
  color: #8b949e;
  line-height: 1.4;
  overflow: hidden;
  text-overflow: ellipsis;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}

.scenario-detail {
  font-size: 12px;
  color: #c9d1d9;
  line-height: 1.5;
  margin: 0;
}

.toggle-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 6px 0;
  font-size: 12px;
  color: #c9d1d9;
}

.toggle-switch {
  position: relative;
  width: 32px;
  height: 18px;
  background: #30363d;
  border: none;
  border-radius: 9px;
  cursor: pointer;
  transition: background 0.15s;
}

.toggle-switch.on {
  background: #238636;
}

.toggle-knob {
  position: absolute;
  top: 2px;
  left: 2px;
  width: 14px;
  height: 14px;
  background: #fff;
  border-radius: 50%;
  transition: transform 0.15s;
}

.toggle-switch.on .toggle-knob {
  transform: translateX(14px);
}

.theme-buttons {
  display: flex;
  gap: 6px;
}

.theme-btn {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 8px;
  background: #0d1117;
  border: 1px solid #30363d;
  border-radius: 6px;
  color: #8b949e;
  font-size: 12px;
  cursor: pointer;
}

.theme-btn:hover {
  color: #c9d1d9;
}

.theme-btn.active {
  border-color: #58a6ff;
  color: #58a6ff;
  background: rgba(56, 139, 253, 0.1);
}

.width-slider {
  width: 100%;
  accent-color: #58a6ff;
}
</style>
