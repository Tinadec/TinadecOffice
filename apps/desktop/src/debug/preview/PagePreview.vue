<script setup lang="ts">
/**
 * 页面预览包装器
 * 根据选中的页面名称，用 mock 数据渲染对应页面。
 * 对于接受 props 的组件（ChatPanel、ContextPanel），直接渲染；
 * 对于内部调用 api 的页面（GitPanel、CodePage、SettingsPage、MarketPage），
 * 使用简化的预览布局展示关键 UI 元素。
 */
import { computed, ref } from 'vue'
import AppHeader from '@/components/AppHeader.vue'
import AppSidebar from '@/components/AppSidebar.vue'
import ChatPanel from '@/components/ChatPanel.vue'
import ContextPanel from '@/components/ContextPanel.vue'
import ConversationPreview from './ConversationPreview.vue'
import DiffViewer from '@/components/git/DiffViewer.vue'
import CommitMessageEditor from '@/components/git/CommitMessageEditor.vue'
import { UiBadge, UiButton, UiCard, UiInput, UiLabel, UiSwitch } from '@/components/ui'
import type { AgentActivity, AgentState } from '@/composables/useAgentActivity'
import {
  FileCode2,
  GitBranch,
  Globe,
  Bot,
  ShieldCheck,
  Package,
  Server,
  Settings as SettingsIcon,
  Store,
  Cpu,
  KeyRound,
  Wrench,
  CheckCircle2,
  XCircle,
  AlertTriangle,
  Download,
  Trash2,
  ToggleLeft,
  ToggleRight,
  RefreshCw,
} from '@lucide/vue'
import type { AgentMode, PermissionLevel } from '@/types/mode'
import type { MockDataBundle } from './mockData'
import { mockCodeContent, mockThinkingSteps, mockToolCalls } from './mockData'

const props = defineProps<{
  pageName: string
  data: MockDataBundle
}>()

// ---- 共享状态 ----
const draft = ref('')
const currentMode = ref<AgentMode>('auto')
const currentPermission = ref<PermissionLevel>('default')
const rightRailCollapsed = ref(false)
const rightRailWidth = ref(420)
const shellCommand = ref('npm test')
const selectedProjectId = ref<string | null>(null)
const selectedSessionId = ref<string | null>(null)

// ---- Mock agent activity data for preview ----
const mockAgentActivity: AgentActivity = {
  status: 'idle',
  runId: null,
  runStartedAt: null,
  runSummary: null,
  activeAgentName: null,
  activeAgentRole: null,
  completedNodes: 0,
  totalNodes: 0,
  lastUpdated: null,
}
const mockAgentStates: Record<string, AgentState> = {}
const mockProgressEvents: never[] = []
const previewThinkingSteps = mockThinkingSteps()
const previewToolCalls = mockToolCalls()

// 初始化选中项
function ensureSelection() {
  if (props.data.projects.length > 0 && !selectedProjectId.value) {
    selectedProjectId.value = props.data.projects[0].id
  }
  if (props.data.sessions.length > 0 && !selectedSessionId.value) {
    selectedSessionId.value = props.data.sessions[0].id
  }
}
ensureSelection()

const currentProject = computed(() => props.data.projects.find((p) => p.id === selectedProjectId.value) ?? null)
const currentSession = computed(() => props.data.sessions.find((s) => s.id === selectedSessionId.value) ?? null)
const recentEvents = computed(() => props.data.events.slice(-8).reverse())

// ---- GitPanel 预览数据 ----
const gitPreviewData = computed(() => {
  const preview = props.data.gitDiffPreview
  if (!preview) return null
  return preview.data as Record<string, unknown>
})

const gitSections = computed(() => {
  const d = gitPreviewData.value
  if (!d) return []
  return (d.sections as Array<{
    id: string
    title: string
    subtitle?: string | null
    diff: string
    files: Array<{
      path: string
      change_type: string
      additions: number
      deletions: number
      binary: boolean
      truncated: boolean
    }>
    file_count: number
    additions: number
    deletions: number
  }>) ?? []
})

const selectedGitSectionId = ref('working_tree')
const activeGitSection = computed(() => gitSections.value.find((s) => s.id === selectedGitSectionId.value) ?? gitSections.value[0] ?? null)

// ---- CodePage 预览数据 ----
const codeFiles = computed(() => [
  { name: 'src', isDir: true, depth: 0 },
  { name: 'orchestrator.ts', isDir: false, depth: 1, size: '12 KB' },
  { name: 'graph.ts', isDir: false, depth: 1, size: '6 KB' },
  { name: 'types.ts', isDir: false, depth: 1, size: '3 KB' },
  { name: '__tests__', isDir: true, depth: 1 },
  { name: 'orch.test.ts', isDir: false, depth: 2, size: '4 KB' },
  { name: 'tests', isDir: true, depth: 0 },
  { name: 'package.json', isDir: false, depth: 0, size: '2 KB' },
  { name: 'tsconfig.json', isDir: false, depth: 0, size: '512 B' },
  { name: 'README.md', isDir: false, depth: 0, size: '4 KB' },
])
const selectedCodeFile = ref('src/orchestrator.ts')
const codeContent = computed(() => mockCodeContent(selectedCodeFile.value))

// ---- SettingsPage 预览 ----
const settingsSection = ref<'general' | 'models' | 'agents' | 'tools' | 'prompts' | 'extensions' | 'readiness' | 'about'>('models')

// ---- MarketPage 预览 ----
const marketKindFilter = ref('all')
const marketQuery = ref('')

const filteredCatalog = computed(() => {
  return props.data.marketCatalog.filter((item) => {
    if (marketKindFilter.value !== 'all' && item.kind !== marketKindFilter.value) return false
    if (marketQuery.value && !item.display_name.toLowerCase().includes(marketQuery.value.toLowerCase())) return false
    return true
  })
})
</script>

<template>
  <div class="page-preview">
    <!-- ==================== HomePage 预览 ==================== -->
    <div v-if="pageName === 'HomePage'" class="home-preview">
      <AppHeader :busy="false" />
      <section class="workspace" :style="{ '--chat-left': '260px', '--chat-right': rightRailCollapsed ? '52px' : `${rightRailWidth + 8}px`, '--chat-top': '0px' }">
        <ChatPanel
          :messages="data.messages"
          :sessions="data.sessions"
          :projects="data.projects"
          :current-session="currentSession"
          :current-project="currentProject"
          :selected-project-id="selectedProjectId"
          :model-name="data.modelSettings?.model ?? 'gpt-4o-mini'"
          :orchestration="data.orchestration"
          :busy="false"
          :draft="draft"
          :mode="currentMode"
          :permission="currentPermission"
          :thinking-steps="previewThinkingSteps"
          :tool-calls="previewToolCalls"
          @update:draft="draft = $event"
          @update:mode="currentMode = $event"
          @update:permission="currentPermission = $event"
        />
        <AppSidebar
          :projects="data.projects"
          :sessions="data.sessions"
          :selected-project-id="selectedProjectId"
          :selected-session-id="selectedSessionId"
          :busy="false"
          @select-project="selectedProjectId = $event"
          @select-session="selectedSessionId = $event"
        />
        <ContextPanel
          v-model:collapsed="rightRailCollapsed"
          v-model:width="rightRailWidth"
          :approvals="data.approvals"
          :events="recentEvents"
          :doctor="data.doctor"
          :readiness="data.readiness"
          :orchestration="data.orchestration"
          :tool-executions="data.toolExecutions"
          :shell-command="shellCommand"
          :busy="false"
          :selected-session-id="selectedSessionId"
          :current-project-path="currentProject?.path"
          :agent-activity="mockAgentActivity"
          :agent-states="mockAgentStates"
          :thinking-steps="previewThinkingSteps"
          :progress-events="mockProgressEvents"
          @update:shell-command="shellCommand = $event"
        />
      </section>
    </div>

    <!-- ==================== ChatPanel 预览 ==================== -->
    <div v-else-if="pageName === 'ChatPanel'" class="chat-preview">
      <ChatPanel
        :messages="data.messages"
        :sessions="data.sessions"
        :projects="data.projects"
        :current-session="currentSession"
        :current-project="currentProject"
        :selected-project-id="selectedProjectId"
        :model-name="data.modelSettings?.model ?? 'gpt-4o-mini'"
        :orchestration="data.orchestration"
        :busy="false"
        :draft="draft"
        :mode="currentMode"
        :permission="currentPermission"
        :thinking-steps="previewThinkingSteps"
        :tool-calls="previewToolCalls"
        @update:draft="draft = $event"
        @update:mode="currentMode = $event"
        @update:permission="currentPermission = $event"
      />
    </div>

    <!-- ==================== ConversationFlow 对话消息流预览 ==================== -->
    <div v-else-if="pageName === 'ConversationFlow'" class="conversation-flow-preview">
      <ConversationPreview />
    </div>

    <!-- ==================== ContextPanel 预览 ==================== -->
    <div v-else-if="pageName === 'ContextPanel'" class="context-preview">
      <ContextPanel
        v-model:collapsed="rightRailCollapsed"
        v-model:width="rightRailWidth"
        :approvals="data.approvals"
        :events="recentEvents"
        :doctor="data.doctor"
        :readiness="data.readiness"
        :orchestration="data.orchestration"
        :tool-executions="data.toolExecutions"
        :shell-command="shellCommand"
        :busy="false"
        :selected-session-id="selectedSessionId"
        :current-project-path="currentProject?.path"
        :agent-activity="mockAgentActivity"
        :agent-states="mockAgentStates"
        :thinking-steps="previewThinkingSteps"
        :progress-events="mockProgressEvents"
        @update:shell-command="shellCommand = $event"
      />
    </div>

    <!-- ==================== GitPanel 预览 ==================== -->
    <div v-else-if="pageName === 'GitPanel'" class="git-preview">
      <div class="git-preview-head">
        <div class="git-preview-title">
          <GitBranch :size="16" />
          <span>Git 管理（预览）</span>
        </div>
        <div class="git-preview-branch">
          <UiBadge variant="secondary">{{ (gitPreviewData as any)?.branch ?? 'feature/orchestrator-refactor' }}</UiBadge>
          <span class="git-ahead-behind">↑{{ (gitPreviewData as any)?.ahead ?? 2 }} ↓{{ (gitPreviewData as any)?.behind ?? 0 }}</span>
        </div>
      </div>

      <div class="git-summary-grid">
        <div><span>分支</span><strong>{{ (gitPreviewData as any)?.branch ?? '-' }}</strong></div>
        <div><span>上游</span><strong>{{ (gitPreviewData as any)?.upstream ?? '-' }}</strong></div>
        <div><span>领先/落后</span><strong>{{ (gitPreviewData as any)?.ahead ?? 0 }} / {{ (gitPreviewData as any)?.behind ?? 0 }}</strong></div>
        <div><span>变更文件</span><strong>{{ (gitPreviewData as any)?.files?.length ?? 0 }}</strong></div>
      </div>

      <div class="git-section-tabs">
        <button
          v-for="section in gitSections"
          :key="section.id"
          class="git-section-tab"
          :class="{ active: selectedGitSectionId === section.id }"
          @click="selectedGitSectionId = section.id"
        >
          <GitBranch :size="13" />
          <span>{{ section.title }}</span>
          <small>{{ section.file_count }}</small>
        </button>
      </div>

      <div v-if="activeGitSection" class="git-diff-wrap">
        <DiffViewer
          :files="activeGitSection.files.map((f) => ({
            path: f.path,
            diffText: activeGitSection.diff,
            additions: f.additions,
            deletions: f.deletions,
            binary: f.binary,
            truncated: f.truncated,
            changeType: f.change_type,
          }))"
          :selected-file-path="activeGitSection.files[0]?.path ?? null"
          :enable-hunk-actions="false"
        />
      </div>

      <div class="git-commit-area">
        <div class="git-panel-subtitle"><span>提交消息</span></div>
        <CommitMessageEditor
          :recent-commits="[
            'refactor(orchestrator): support dynamic dependency resolution',
            'feat(graph): add cycle detection',
          ]"
        />
      </div>

      <div v-if="data.gitPushPlan" class="git-push-area">
        <div class="git-panel-subtitle"><span>推送就绪状态</span></div>
        <div class="git-push-status" :class="{ ready: (data.gitPushPlan.data as any)?.push_ready }">
          <CheckCircle2 v-if="(data.gitPushPlan.data as any)?.push_ready" :size="18" />
          <AlertTriangle v-else :size="18" />
          <div>
            <strong>{{ (data.gitPushPlan.data as any)?.push_ready ? '可推送' : '推送受阻' }}</strong>
            <span>{{ data.gitPushPlan.summary }}</span>
          </div>
        </div>
      </div>
    </div>

    <!-- ==================== CodePage 预览 ==================== -->
    <div v-else-if="pageName === 'CodePage'" class="code-preview">
      <div class="code-preview-head">
        <FileCode2 :size="16" />
        <span>代码编辑器（预览）</span>
        <UiBadge variant="secondary">{{ currentProject?.name ?? 'tinadec' }}</UiBadge>
      </div>
      <div class="code-preview-body">
        <div class="code-file-tree">
          <div class="code-tree-head">文件</div>
          <div
            v-for="(entry, i) in codeFiles"
            :key="i"
            class="code-tree-row"
            :class="{ selected: !entry.isDir && selectedCodeFile.endsWith(entry.name) }"
            :style="{ paddingLeft: `${entry.depth * 16 + 8}px` }"
            @click="!entry.isDir && (selectedCodeFile = entry.name.includes('.') ? `src/${entry.name}` : entry.name)"
          >
            <span class="ct-icon">{{ entry.isDir ? '📁' : '📄' }}</span>
            <span class="ct-name">{{ entry.name }}</span>
            <span v-if="!entry.isDir && entry.size" class="ct-size">{{ entry.size }}</span>
          </div>
        </div>
        <div class="code-viewer-wrap">
          <div class="code-viewer-head">
            <span>{{ selectedCodeFile }}</span>
            <UiBadge variant="outline">{{ codeContent.split('\n').length }} 行</UiBadge>
          </div>
          <pre class="code-viewer-content"><code>{{ codeContent }}</code></pre>
        </div>
      </div>
    </div>

    <!-- ==================== SettingsPage 预览 ==================== -->
    <div v-else-if="pageName === 'SettingsPage'" class="settings-preview">
      <div class="settings-head">
        <SettingsIcon :size="16" />
        <span>设置（预览）</span>
      </div>
      <div class="settings-body">
        <div class="settings-nav">
          <button v-for="s in [
            { id: 'models', label: '模型中心', icon: Cpu },
            { id: 'agents', label: '智能体', icon: Bot },
            { id: 'tools', label: '工具', icon: Wrench },
            { id: 'prompts', label: '提示词', icon: FileCode2 },
            { id: 'extensions', label: '扩展', icon: Package },
            { id: 'readiness', label: '就绪检查', icon: ShieldCheck },
            { id: 'general', label: '通用', icon: SettingsIcon },
            { id: 'about', label: '关于', icon: Globe },
          ]" :key="s.id" class="settings-nav-item" :class="{ active: settingsSection === s.id }" @click="settingsSection = s.id as any">
            <component :is="s.icon" :size="14" />
            <span>{{ s.label }}</span>
          </button>
        </div>
        <div class="settings-content">
          <!-- 模型中心 -->
          <template v-if="settingsSection === 'models'">
            <h3 class="settings-section-title">模型中心</h3>
            <UiCard class="settings-card">
              <div class="settings-card-head"><KeyRound :size="14" /><span>默认模型设置</span></div>
              <div class="settings-row">
                <UiLabel>Base URL</UiLabel>
                <UiInput :model-value="data.modelSettings?.base_url ?? ''" readonly />
              </div>
              <div class="settings-row">
                <UiLabel>模型</UiLabel>
                <UiInput :model-value="data.modelSettings?.model ?? ''" readonly />
              </div>
              <div class="settings-row">
                <UiLabel>API Key</UiLabel>
                <UiBadge :variant="data.modelSettings?.has_api_key ? 'default' : 'destructive'">{{ data.modelSettings?.has_api_key ? '已配置' : '未配置' }}</UiBadge>
              </div>
            </UiCard>
            <h4 class="settings-subtitle">Provider 实例（{{ data.modelProviders.length }}）</h4>
            <div v-for="p in data.modelProviders" :key="p.id" class="provider-row">
              <div class="provider-info">
                <strong>{{ p.display_name }}</strong>
                <span>{{ p.driver }} · {{ p.connection_kind }}</span>
              </div>
              <div class="provider-meta">
                <UiBadge :variant="p.status === 'healthy' ? 'default' : p.status === 'unknown' ? 'secondary' : 'destructive'">{{ p.status }}</UiBadge>
                <UiBadge variant="outline">{{ p.enabled ? '已启用' : '已禁用' }}</UiBadge>
              </div>
            </div>
            <h4 class="settings-subtitle">路由（{{ data.modelRoutes.length }}）</h4>
            <div v-for="r in data.modelRoutes" :key="r.purpose" class="route-row">
              <span class="route-purpose">{{ r.purpose }}</span>
              <span class="route-arrow">→</span>
              <span class="route-target">{{ data.modelProviders.find((p) => p.id === r.provider_instance_id)?.display_name ?? r.provider_instance_id }}</span>
              <UiBadge variant="outline">{{ r.model ?? 'default' }}</UiBadge>
            </div>
          </template>

          <!-- 智能体 -->
          <template v-else-if="settingsSection === 'agents'">
            <h3 class="settings-section-title">智能体（{{ data.agents.length }}）</h3>
            <div v-for="a in data.agents" :key="a.id" class="agent-row">
              <div class="agent-info">
                <strong>{{ a.name }}</strong>
                <span>{{ a.agent_type }} · {{ a.layer }} · {{ a.mode }}</span>
                <p>{{ a.description }}</p>
              </div>
              <div class="agent-meta">
                <UiBadge :variant="a.enabled ? 'default' : 'secondary'">{{ a.enabled ? '启用' : '禁用' }}</UiBadge>
                <UiBadge variant="outline">{{ a.is_built_in ? '内置' : '自定义' }}</UiBadge>
                <div class="agent-tools">
                  <UiBadge v-for="t in a.allowed_tools.slice(0, 3)" :key="t" variant="outline">{{ t }}</UiBadge>
                  <span v-if="a.allowed_tools.length > 3" class="more-tools">+{{ a.allowed_tools.length - 3 }}</span>
                </div>
              </div>
            </div>
          </template>

          <!-- 工具 -->
          <template v-else-if="settingsSection === 'tools'">
            <h3 class="settings-section-title">工具（{{ data.tools.length }}）</h3>
            <div v-for="t in data.tools" :key="t.id" class="tool-row">
              <div class="tool-info">
                <strong>{{ t.display_name }}</strong>
                <span>{{ t.id }} · {{ t.domain }} · {{ t.source }}</span>
              </div>
              <div class="tool-meta">
                <UiBadge :variant="t.risk === 'high' ? 'destructive' : t.risk === 'medium' ? 'default' : 'secondary'">{{ t.risk }}</UiBadge>
                <UiBadge v-if="t.requires_approval" variant="outline">需审批</UiBadge>
              </div>
            </div>
          </template>

          <!-- 提示词 -->
          <template v-else-if="settingsSection === 'prompts'">
            <h3 class="settings-section-title">提示词片段（{{ data.promptFragments.length }}）</h3>
            <div v-for="f in data.promptFragments" :key="f.id" class="prompt-row">
              <div class="prompt-info">
                <strong>{{ f.title }}</strong>
                <span>{{ f.key }} · {{ f.scope }} · 优先级 {{ f.priority }}</span>
                <p class="prompt-content">{{ f.content }}</p>
              </div>
              <UiBadge :variant="f.enabled ? 'default' : 'secondary'">{{ f.enabled ? '启用' : '禁用' }}</UiBadge>
            </div>
          </template>

          <!-- 扩展 -->
          <template v-else-if="settingsSection === 'extensions'">
            <h3 class="settings-section-title">已安装扩展（{{ data.installedExtensions.length }}）</h3>
            <div v-for="e in data.installedExtensions" :key="e.id" class="ext-row">
              <div class="ext-info">
                <strong>{{ e.display_name }}</strong>
                <span>{{ e.extension_id }} · v{{ e.version }} · {{ e.kind }}</span>
                <p>{{ e.description }}</p>
              </div>
              <div class="ext-meta">
                <UiBadge :variant="e.enabled ? 'default' : 'secondary'">{{ e.status }}</UiBadge>
                <UiBadge variant="outline">{{ e.enabled ? '启用' : '禁用' }}</UiBadge>
              </div>
            </div>
          </template>

          <!-- 就绪检查 -->
          <template v-else-if="settingsSection === 'readiness'">
            <h3 class="settings-section-title">就绪检查</h3>
            <div v-if="data.doctor" class="doctor-section">
              <h4 class="settings-subtitle">Doctor 检查</h4>
              <div v-for="c in data.doctor.checks" :key="c.name" class="check-row">
                <CheckCircle2 v-if="c.status === 'ok'" :size="16" class="check-ok" />
                <AlertTriangle v-else-if="c.status === 'warning'" :size="16" class="check-warn" />
                <XCircle v-else :size="16" class="check-err" />
                <strong>{{ c.name }}</strong>
                <span>{{ c.message }}</span>
              </div>
            </div>
            <div v-if="data.readiness" class="readiness-section">
              <h4 class="settings-subtitle">运行时就绪</h4>
              <div class="readiness-summary">
                <UiBadge variant="default">就绪 {{ data.readiness.ready_count }}</UiBadge>
                <UiBadge variant="secondary">警告 {{ data.readiness.warning_count }}</UiBadge>
                <UiBadge variant="destructive">阻塞 {{ data.readiness.blocked_count }}</UiBadge>
              </div>
              <div v-for="c in data.readiness.components" :key="c.id" class="check-row">
                <CheckCircle2 v-if="c.status === 'ready'" :size="16" class="check-ok" />
                <AlertTriangle v-else-if="c.status === 'warning'" :size="16" class="check-warn" />
                <XCircle v-else :size="16" class="check-err" />
                <strong>{{ c.name }}</strong>
                <span>{{ c.summary }}</span>
              </div>
            </div>
          </template>

          <!-- 通用 -->
          <template v-else-if="settingsSection === 'general'">
            <h3 class="settings-section-title">通用设置</h3>
            <UiCard class="settings-card">
              <div class="settings-row">
                <UiLabel>主题</UiLabel>
                <UiBadge variant="outline">深色</UiBadge>
              </div>
              <div class="settings-row">
                <UiLabel>语言</UiLabel>
                <UiBadge variant="outline">简体中文</UiBadge>
              </div>
            </UiCard>
          </template>

          <!-- 关于 -->
          <template v-else-if="settingsSection === 'about'">
            <h3 class="settings-section-title">关于</h3>
            <UiCard class="settings-card">
              <div class="settings-row"><UiLabel>应用</UiLabel><span>TinadecOffice</span></div>
              <div class="settings-row"><UiLabel>版本</UiLabel><span>0.4.2</span></div>
              <div class="settings-row"><UiLabel>运行时</UiLabel><span>{{ data.readiness?.runtime ?? 'TinadecCore 0.4.2' }}</span></div>
              <div class="settings-row"><UiLabel>平台</UiLabel><span>{{ data.doctor?.platform ?? 'win32-x64' }}</span></div>
            </UiCard>
          </template>
        </div>
      </div>
    </div>

    <!-- ==================== MarketPage 预览 ==================== -->
    <div v-else-if="pageName === 'MarketPage'" class="market-preview">
      <div class="market-head">
        <Store :size="16" />
        <span>扩展市场（预览）</span>
      </div>
      <div class="market-toolbar">
        <UiInput v-model="marketQuery" placeholder="搜索扩展..." class="market-search" />
        <div class="market-filters">
          <button v-for="k in ['all', 'tool-pack', 'mcp-server', 'acp-adapter', 'skill']" :key="k" class="market-filter-btn" :class="{ active: marketKindFilter === k }" @click="marketKindFilter = k">
            {{ k }}
          </button>
        </div>
      </div>

      <div class="market-sources">
        <span class="market-sources-label">数据源：</span>
        <UiBadge v-for="s in data.extensionSources" :key="s.id" variant="outline">
          {{ s.name }} · {{ s.kind }}
        </UiBadge>
      </div>

      <div class="market-grid">
        <UiCard v-for="item in filteredCatalog" :key="item.catalog_id" class="market-card">
          <div class="market-card-head">
            <Package :size="16" />
            <strong>{{ item.display_name }}</strong>
            <UiBadge variant="outline">v{{ item.version }}</UiBadge>
          </div>
          <p class="market-card-desc">{{ item.description }}</p>
          <div class="market-card-meta">
            <UiBadge variant="secondary">{{ item.kind }}</UiBadge>
            <span class="market-publisher">{{ item.publisher }}</span>
          </div>
          <div class="market-card-caps">
            <UiBadge v-for="cap in item.capabilities.slice(0, 3)" :key="cap" variant="outline">{{ cap }}</UiBadge>
          </div>
          <div class="market-card-actions">
            <UiButton size="sm" variant="default">
              <Download :size="14" />
              <span>安装</span>
            </UiButton>
          </div>
        </UiCard>
      </div>

      <div class="market-installed">
        <h4 class="settings-subtitle">已安装（{{ data.installedExtensions.length }}）</h4>
        <div v-for="e in data.installedExtensions" :key="e.id" class="ext-row">
          <div class="ext-info">
            <strong>{{ e.display_name }}</strong>
            <span>{{ e.extension_id }} · v{{ e.version }}</span>
          </div>
          <div class="ext-meta">
            <UiBadge :variant="e.enabled ? 'default' : 'secondary'">{{ e.status }}</UiBadge>
            <button class="market-toggle-btn" :title="e.enabled ? '禁用' : '启用'">
              <component :is="e.enabled ? ToggleRight : ToggleLeft" :size="18" />
            </button>
            <button class="market-trash-btn" title="卸载">
              <Trash2 :size="14" />
            </button>
          </div>
        </div>
      </div>

      <div class="market-mcp">
        <h4 class="settings-subtitle">MCP 服务（{{ data.mcpServers.length }}）</h4>
        <div v-for="m in data.mcpServers" :key="m.id" class="mcp-row">
          <Server :size="14" />
          <strong>{{ m.name }}</strong>
          <UiBadge :variant="m.status === 'connected' ? 'default' : 'secondary'">{{ m.status }}</UiBadge>
          <span class="mcp-tools">{{ m.tools.length }} 个工具</span>
        </div>
      </div>
    </div>

    <div v-else class="preview-empty-hint">
      未找到页面：{{ pageName }}
    </div>
  </div>
</template>

<style scoped>
.page-preview {
  height: 100%;
  overflow: auto;
  background: var(--bg-primary, #0d1117);
}

.preview-empty-hint {
  padding: 24px;
  text-align: center;
  color: var(--text-muted, #8b949e);
  font-size: 13px;
}

/* ---- HomePage 预览 ---- */
.home-preview {
  height: 100%;
  display: flex;
  flex-direction: column;
}
.home-preview .workspace {
  flex: 1;
  min-height: 0;
}

/* ---- ChatPanel 预览 ---- */
.chat-preview {
  height: 100%;
}

/* ---- ConversationFlow 对话消息流预览 ---- */
.conversation-flow-preview {
  height: 100%;
  overflow: hidden;
  display: flex;
  justify-content: center;
  background: var(--bg-primary, #0d1117);
}

/* ---- ContextPanel 预览 ---- */
.context-preview {
  height: 100%;
  display: flex;
  justify-content: flex-end;
}

/* ---- GitPanel 预览 ---- */
.git-preview {
  padding: 16px;
  max-width: 900px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.git-preview-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.git-preview-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  font-weight: 600;
  color: var(--text-primary, #e6edf3);
}
.git-preview-branch {
  display: flex;
  align-items: center;
  gap: 8px;
}
.git-ahead-behind {
  font-size: 12px;
  color: var(--text-muted, #8b949e);
}
.git-summary-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 8px;
}
.git-summary-grid > div {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 8px;
  background: var(--bg-secondary, #161b22);
  border: 1px solid var(--border-muted, #30363d);
  border-radius: 6px;
}
.git-summary-grid span {
  font-size: 11px;
  color: var(--text-muted, #8b949e);
}
.git-summary-grid strong {
  font-size: 13px;
  color: var(--text-primary, #e6edf3);
}
.git-section-tabs {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}
.git-section-tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  background: var(--bg-secondary, #161b22);
  border: 1px solid var(--border-muted, #30363d);
  border-radius: 6px;
  font-size: 12px;
  color: var(--text-secondary, #c9d1d9);
  cursor: pointer;
}
.git-section-tab.active {
  border-color: var(--accent-primary, #58a6ff);
  color: var(--text-primary, #e6edf3);
}
.git-section-tab small {
  background: var(--bg-tertiary, #21262d);
  padding: 1px 6px;
  border-radius: 8px;
  font-size: 10px;
}
.git-diff-wrap {
  height: 400px;
  border: 1px solid var(--border-muted, #30363d);
  border-radius: 8px;
  overflow: hidden;
}
.git-commit-area, .git-push-area {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.git-panel-subtitle {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary, #c9d1d9);
}
.git-push-status {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px;
  background: var(--bg-secondary, #161b22);
  border: 1px solid var(--border-muted, #30363d);
  border-radius: 6px;
}
.git-push-status.ready {
  border-color: rgba(63, 185, 80, 0.35);
}

/* ---- CodePage 预览 ---- */
.code-preview {
  height: 100%;
  display: flex;
  flex-direction: column;
}
.code-preview-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 16px;
  border-bottom: 1px solid var(--border-muted, #30363d);
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary, #e6edf3);
}
.code-preview-body {
  flex: 1;
  display: flex;
  min-height: 0;
}
.code-file-tree {
  width: 240px;
  border-right: 1px solid var(--border-muted, #30363d);
  overflow: auto;
}
.code-tree-head {
  padding: 8px 12px;
  font-size: 11px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--text-muted, #8b949e);
  border-bottom: 1px solid var(--border-muted, #30363d);
}
.code-tree-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 8px;
  font-size: 12px;
  color: var(--text-secondary, #c9d1d9);
  cursor: pointer;
}
.code-tree-row:hover {
  background: var(--bg-hover, #21262d);
}
.code-tree-row.selected {
  background: var(--bg-selected, rgba(56, 139, 253, 0.15));
}
.ct-icon { font-size: 14px; }
.ct-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ct-size { font-size: 11px; color: var(--text-muted, #8b949e); }
.code-viewer-wrap {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.code-viewer-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border-muted, #30363d);
  font-size: 12px;
  color: var(--text-secondary, #c9d1d9);
  font-family: monospace;
}
.code-viewer-content {
  flex: 1;
  overflow: auto;
  margin: 0;
  padding: 12px;
  font-family: 'Fira Code', 'Consolas', monospace;
  font-size: 13px;
  line-height: 1.6;
  color: var(--text-primary, #e6edf3);
  background: var(--bg-primary, #0d1117);
}

/* ---- SettingsPage 预览 ---- */
.settings-preview {
  height: 100%;
  display: flex;
  flex-direction: column;
}
.settings-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 16px;
  border-bottom: 1px solid var(--border-muted, #30363d);
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary, #e6edf3);
}
.settings-body {
  flex: 1;
  display: flex;
  min-height: 0;
}
.settings-nav {
  width: 200px;
  border-right: 1px solid var(--border-muted, #30363d);
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.settings-nav-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border: none;
  background: none;
  color: var(--text-secondary, #c9d1d9);
  font-size: 12px;
  border-radius: 6px;
  cursor: pointer;
  text-align: left;
}
.settings-nav-item:hover {
  background: var(--bg-hover, #21262d);
}
.settings-nav-item.active {
  background: var(--bg-selected, rgba(56, 139, 253, 0.15));
  color: var(--text-primary, #e6edf3);
}
.settings-content {
  flex: 1;
  overflow: auto;
  padding: 16px;
}
.settings-section-title {
  font-size: 16px;
  font-weight: 600;
  margin: 0 0 16px 0;
  color: var(--text-primary, #e6edf3);
}
.settings-subtitle {
  font-size: 13px;
  font-weight: 600;
  margin: 20px 0 10px 0;
  color: var(--text-secondary, #c9d1d9);
}
.settings-card {
  padding: 16px;
  margin-bottom: 16px;
}
.settings-card-head {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  font-weight: 600;
  margin-bottom: 12px;
  color: var(--text-primary, #e6edf3);
}
.settings-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 6px 0;
}
.provider-row, .route-row, .agent-row, .tool-row, .prompt-row, .ext-row, .check-row, .mcp-row {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
  padding: 10px 12px;
  background: var(--bg-secondary, #161b22);
  border: 1px solid var(--border-muted, #30363d);
  border-radius: 6px;
  margin-bottom: 6px;
}
.provider-info strong, .agent-info strong, .tool-info strong, .prompt-info strong, .ext-info strong {
  display: block;
  font-size: 13px;
  color: var(--text-primary, #e6edf3);
}
.provider-info span, .agent-info span, .tool-info span, .prompt-info span, .ext-info span {
  font-size: 11px;
  color: var(--text-muted, #8b949e);
}
.agent-info p, .prompt-info p, .ext-info p {
  font-size: 12px;
  color: var(--text-secondary, #c9d1d9);
  margin: 4px 0 0 0;
}
.prompt-content {
  white-space: pre-wrap;
  font-family: monospace;
  font-size: 11px;
}
.provider-meta, .agent-meta, .tool-meta, .ext-meta {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 4px;
}
.agent-tools {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  justify-content: flex-end;
}
.more-tools {
  font-size: 11px;
  color: var(--text-muted, #8b949e);
}
.route-row {
  align-items: center;
}
.route-purpose {
  font-family: monospace;
  font-size: 12px;
  color: var(--accent-primary, #58a6ff);
}
.route-arrow {
  color: var(--text-muted, #8b949e);
}
.route-target {
  font-size: 12px;
  color: var(--text-secondary, #c9d1d9);
}
.check-row {
  align-items: center;
}
.check-ok { color: #3fb950; }
.check-warn { color: #d29922; }
.check-err { color: #f85149; }
.readiness-summary {
  display: flex;
  gap: 8px;
  margin-bottom: 12px;
}

/* ---- MarketPage 预览 ---- */
.market-preview {
  padding: 16px;
  max-width: 1000px;
  margin: 0 auto;
}
.market-head {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  font-weight: 600;
  margin-bottom: 16px;
  color: var(--text-primary, #e6edf3);
}
.market-toolbar {
  display: flex;
  gap: 12px;
  margin-bottom: 12px;
}
.market-search {
  flex: 1;
}
.market-filters {
  display: flex;
  gap: 4px;
}
.market-filter-btn {
  padding: 6px 10px;
  background: var(--bg-secondary, #161b22);
  border: 1px solid var(--border-muted, #30363d);
  border-radius: 6px;
  font-size: 12px;
  color: var(--text-secondary, #c9d1d9);
  cursor: pointer;
}
.market-filter-btn.active {
  border-color: var(--accent-primary, #58a6ff);
  color: var(--text-primary, #e6edf3);
}
.market-sources {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}
.market-sources-label {
  font-size: 12px;
  color: var(--text-muted, #8b949e);
}
.market-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 12px;
  margin-bottom: 24px;
}
.market-card {
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.market-card-head {
  display: flex;
  align-items: center;
  gap: 8px;
}
.market-card-desc {
  font-size: 12px;
  color: var(--text-secondary, #c9d1d9);
  margin: 0;
  line-height: 1.5;
}
.market-card-meta {
  display: flex;
  align-items: center;
  gap: 8px;
}
.market-publisher {
  font-size: 11px;
  color: var(--text-muted, #8b949e);
}
.market-card-caps {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.market-card-actions {
  display: flex;
  justify-content: flex-end;
}
.market-installed, .market-mcp {
  margin-top: 20px;
}
.market-toggle-btn, .market-trash-btn {
  background: none;
  border: none;
  color: var(--text-muted, #8b949e);
  cursor: pointer;
  padding: 4px;
  display: flex;
  align-items: center;
}
.market-toggle-btn:hover, .market-trash-btn:hover {
  color: var(--text-primary, #e6edf3);
}
.mcp-row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.mcp-tools {
  font-size: 11px;
  color: var(--text-muted, #8b949e);
  margin-left: auto;
}
</style>
