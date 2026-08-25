<script setup lang="ts">
/**
 * 页面预览包装器 — 真实控件版
 * 除 ChatPanel/ContextPanel 等本就 props 驱动的组合外，
 * GitPanel / CodePage / SettingsPage / MarketPage 均直接挂载真实组件/页面，
 * 数据来自 apiBridge 注入的 mock api（按场景响应式切换）。
 */
import { computed, defineAsyncComponent, ref } from 'vue'
import AppHeader from '@/components/AppHeader.vue'
import AppSidebar from '@/components/AppSidebar.vue'
import ChatPanel from '@/components/ChatPanel.vue'
import ContextPanel from '@/components/ContextPanel.vue'
import ConversationPreview from './ConversationPreview.vue'
import PreviewIslandCard from './PreviewIslandCard.vue'
import PreviewNavScope from './PreviewNavScope.vue'
import RealMarketPage from './RealMarketPage.vue'
import { api } from '@/api'
import { UiBadge } from '@/components/ui'
import type { AgentActivity, AgentState } from '@/composables/useAgentActivity'
import {
  FileCode2,
  GitBranch,
  Home,
  MessageSquare,
  MessagesSquare,
  PanelRight,
  Settings as SettingsIcon,
  Store,
} from '@lucide/vue'
import type { AgentMode, PermissionLevel } from '@/types/mode'
import type { MockDataBundle } from './mockData'
import { mockThinkingSteps, mockToolCalls } from './mockData'

const props = defineProps<{
  pageName: string
  data: MockDataBundle
}>()

// ---- 共享状态（HomePage / ChatPanel / ContextPanel 组合预览） ----
const draft = ref('')
const currentMode = ref<AgentMode>('auto')
const currentPermission = ref<PermissionLevel>('default')
const rightRailCollapsed = ref(false)
const rightRailWidth = ref(420)
const shellCommand = ref('npm test')
const selectedProjectId = ref<string | null>(null)
const selectedSessionId = ref<string | null>(null)

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

// ---- 真实控件（懒加载保持代码分包） ----
const RealGitPanel = defineAsyncComponent(() => import('@/components/GitPanel.vue'))
const RealCodePage = defineAsyncComponent(() => import('@/pages/CodePage.vue'))
const RealSettingsPage = defineAsyncComponent(() => import('@/pages/SettingsPage.vue'))

async function onDecideApproval(approvalId: string, decision: 'approved' | 'rejected') {
  try {
    await api.decideApproval(approvalId, decision)
  } catch {
    // mock 场景下忽略决策失败
  }
}
</script>

<template>
  <div class="page-preview">
    <!-- ==================== HomePage：真实三栏组合 ==================== -->
    <div v-if="pageName === 'HomePage'" class="home-preview">
      <PreviewIslandCard variant="section" padding="none" class="home-shell-island">
        <template #header>
          <div class="preview-island-header">
            <Home :size="14" />
            <span>首页（三栏布局 · 真实组件）</span>
            <UiBadge variant="outline">{{ currentProject?.name ?? '预览' }}</UiBadge>
          </div>
        </template>
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
      </PreviewIslandCard>
    </div>

    <!-- ==================== ChatPanel：真实组件 ==================== -->
    <div v-else-if="pageName === 'ChatPanel'" class="chat-preview">
      <PreviewIslandCard variant="raised" padding="none">
        <template #header>
          <div class="preview-island-header">
            <MessageSquare :size="14" />
            <span>聊天面板（真实组件）</span>
          </div>
        </template>
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
      </PreviewIslandCard>
    </div>

    <!-- ==================== ConversationFlow：真实 MessageList ==================== -->
    <div v-else-if="pageName === 'ConversationFlow'" class="conversation-flow-preview">
      <PreviewIslandCard variant="raised" padding="none" class="conversation-island">
        <template #header>
          <div class="preview-island-header">
            <MessagesSquare :size="14" />
            <span>对话消息流（真实 MessageList）</span>
          </div>
        </template>
        <ConversationPreview />
      </PreviewIslandCard>
    </div>

    <!-- ==================== ContextPanel：真实组件 ==================== -->
    <div v-else-if="pageName === 'ContextPanel'" class="context-preview">
      <PreviewIslandCard variant="raised" padding="none" class="context-island">
        <template #header>
          <div class="preview-island-header">
            <PanelRight :size="14" />
            <span>上下文面板（真实组件）</span>
          </div>
        </template>
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
      </PreviewIslandCard>
    </div>

    <!-- ==================== GitPanel：真实右栏 Git 组件 ==================== -->
    <div v-else-if="pageName === 'GitPanel'" class="git-real-preview">
      <PreviewIslandCard variant="section" padding="none" class="full-shell-island">
        <template #header>
          <div class="preview-island-header">
            <GitBranch :size="14" />
            <span>Git 管理（真实 GitPanel 组件）</span>
            <UiBadge variant="outline">{{ currentProject?.name ?? 'tinadec' }}</UiBadge>
          </div>
        </template>
        <PreviewNavScope>
          <div class="real-rail-host">
            <RealGitPanel
              :approvals="data.approvals"
              :current-project-path="currentProject?.path ?? 'D:/workspace/tinadec'"
              :selected-session-id="selectedSessionId"
              @decide-approval="(a, d) => onDecideApproval(a.id, d)"
            />
          </div>
        </PreviewNavScope>
      </PreviewIslandCard>
    </div>

    <!-- ==================== CodePage：真实页面 ==================== -->
    <div v-else-if="pageName === 'CodePage'" class="real-page-preview">
      <PreviewIslandCard variant="section" padding="none" class="full-shell-island">
        <template #header>
          <div class="preview-island-header">
            <FileCode2 :size="14" />
            <span>代码编辑器（真实 CodePage）</span>
            <UiBadge variant="outline">Monaco · FileTree · Patch</UiBadge>
          </div>
        </template>
        <PreviewNavScope>
          <div class="real-page-host">
            <RealCodePage />
          </div>
        </PreviewNavScope>
      </PreviewIslandCard>
    </div>

    <!-- ==================== SettingsPage：真实页面 ==================== -->
    <div v-else-if="pageName === 'SettingsPage'" class="real-page-preview">
      <PreviewIslandCard variant="section" padding="none" class="full-shell-island">
        <template #header>
          <div class="preview-island-header">
            <SettingsIcon :size="14" />
            <span>设置（真实 SettingsPage）</span>
            <UiBadge variant="outline">全部子页签可用</UiBadge>
          </div>
        </template>
        <PreviewNavScope>
          <div class="real-page-host">
            <RealSettingsPage />
          </div>
        </PreviewNavScope>
      </PreviewIslandCard>
    </div>

    <!-- ==================== MarketPage：真实页面（引擎无持久化） ==================== -->
    <div v-else-if="pageName === 'MarketPage'" class="real-page-preview">
      <PreviewIslandCard variant="section" padding="none" class="full-shell-island">
        <template #header>
          <div class="preview-island-header">
            <Store :size="14" />
            <span>扩展市场（真实 MarketPage · UIE 引擎隔离）</span>
          </div>
        </template>
        <RealMarketPage />
      </PreviewIslandCard>
    </div>

    <div v-else class="preview-empty-hint">
      未找到页面：{{ pageName }}
    </div>
  </div>
</template>

<style scoped>
/* 视口岛 body 是唯一滚动容器；本组件铺满视口并向下分配确定高度 */
.page-preview {
  min-height: 100%;
  background: transparent;
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.preview-island-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  width: 100%;
}

.preview-empty-hint {
  padding: 24px;
  text-align: center;
  color: var(--text-muted, #6e7681);
  font-size: 13px;
  background: var(--surface-section, #11151c);
  border: 1px dashed var(--border-card, rgba(0,0,0,.08));
  border-radius: 12px;
}

/* ---- HomePage 预览 ---- */
.home-preview {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.home-preview .workspace {
  flex: 1;
  min-height: 0;
}
.home-shell-island {
  flex: 1;
  min-height: 0;
}
.home-shell-island :deep(.island-body) {
  padding: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
.home-shell-island :deep(.workspace) {
  flex: 1;
  min-height: 0;
}

/* ---- ChatPanel 预览 ---- */
.chat-preview {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.chat-preview :deep(.island-body) { padding: 0; }

/* ---- ConversationFlow 预览 ---- */
.conversation-flow-preview {
  flex: 1;
  min-height: 0;
  display: flex;
  justify-content: center;
}
.conversation-island { width: 100%; max-width: 760px; }
.conversation-island :deep(.island-body) { padding: 0; }

/* ---- ContextPanel 预览 ---- */
.context-preview {
  flex: 1;
  min-height: 0;
  display: flex;
  justify-content: flex-end;
}
.context-island { width: 100%; max-width: 420px; }
.context-island :deep(.island-body) { padding: 0; }

/* ---- 真实页面/组件通用宿主：占满视口，内部自滚动 ---- */
.git-real-preview,
.real-page-preview {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.full-shell-island {
  flex: 1;
  min-height: 0;
}
.full-shell-island :deep(.island-body) {
  padding: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

/* GitPanel 是右栏宽度语义的组件，居中限宽呈现 */
.real-rail-host {
  flex: 1;
  min-height: 0;
  overflow: hidden;
  display: flex;
  justify-content: center;
  background:
    linear-gradient(color-mix(in srgb, var(--bg-primary) 88%, transparent), color-mix(in srgb, var(--bg-primary) 88%, transparent));
}
.real-rail-host > :deep(*) {
  width: 100%;
  max-width: 560px;
  height: 100%;
  overflow: auto;
  border-inline: 1px solid var(--border-card, rgba(0,0,0,.08));
}

.real-page-host {
  flex: 1;
  min-height: 0;
  overflow: auto;
}
/* 真实页面以 100vh 为设计基准，嵌入视口时收敛为容器高度 */
.real-page-host :deep(.shell),
.real-page-host :deep(.settings-page) {
  height: 100%;
  min-height: 0;
}
.real-page-host :deep(.top-drag-bar),
.real-page-host :deep(.settings-window-controls) {
  display: none !important;
}
</style>
