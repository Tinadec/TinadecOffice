<script setup lang="ts">
/**
 * 组件预览包装器
 * 根据选中的组件名称，用 mock 数据渲染对应的独立组件。
 */
import { computed, defineAsyncComponent, ref } from 'vue'
import AgentActivityBanner from '@/components/chat/AgentActivityBanner.vue'
import ToolCallCard from '@/components/chat/ToolCallCard.vue'
import ThinkingProcess from '@/components/chat/ThinkingProcess.vue'
import ToolExecutionTimeline from '@/components/tools/ToolExecutionTimeline.vue'
import ToolCatalogBrowser from '@/components/tools/ToolCatalogBrowser.vue'
import ToolStatsDashboard from '@/components/tools/ToolStatsDashboard.vue'
import DiffViewer from '@/components/git/DiffViewer.vue'
import CommitMessageEditor from '@/components/git/CommitMessageEditor.vue'
import type { AgentActivity, AgentState } from '@/composables/useAgentActivity'
import type { ToolExecutionTimelineItemDto, ToolDescriptorDto } from '@/api'
import type { MockDataBundle } from './mockData'
import { mockThinkingSteps, mockToolCalls } from './mockData'
import PreviewIslandCard from './PreviewIslandCard.vue'

const props = defineProps<{
  componentName: string
  data: MockDataBundle
}>()

// 真实 FileTreePanel（内部经 apiBridge 走 mock listDirectory/globSearch）
const RealFileTreePanel = defineAsyncComponent(() => import('@/components/code/FileTreePanel.vue'))

// ---- AgentActivityBanner mock 数据 ----
const mockActivity = computed<AgentActivity>(() => {
  const orch = props.data.orchestration
  if (!orch?.run) {
    return {
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
  }
  const completed = orch.nodes.filter((n) => n.status === 'completed' || n.status === 'done').length
  const isRunning = orch.run.status === 'running'
  const activeAssignment = orch.assignments.find((a) => a.status === 'active')
  return {
    status: isRunning ? 'working' : orch.run.status === 'failed' ? 'error' : 'completed',
    runId: orch.run.id,
    runStartedAt: orch.run.created_at,
    runSummary: orch.run.summary,
    activeAgentName: activeAssignment?.agent_name ?? 'Meeting Agent',
    activeAgentRole: activeAssignment?.agent_type ?? '会议智能体',
    completedNodes: completed,
    totalNodes: orch.nodes.length,
    lastUpdated: orch.run.updated_at,
  }
})

const mockAgentStates = computed<Record<string, AgentState>>(() => {
  const orch = props.data.orchestration
  if (!orch) return {}
  const states: Record<string, AgentState> = {}
  for (const a of orch.assignments) {
    states[a.agent_id] = {
      agentId: a.agent_id,
      agentName: a.agent_name,
      agentLayer: a.agent_layer,
      agentType: a.agent_type,
      status: a.status === 'completed' ? 'completed' : a.status === 'active' || a.status === 'running' ? 'active' : a.status === 'waiting' ? 'waiting' : 'idle',
      lastActiveAt: a.created_at,
      currentTask: orch.nodes.find((n) => n.id === a.task_node_id)?.title ?? null,
    }
  }
  return states
})

// ---- DiffViewer mock 数据 ----
const mockDiffText = computed(() => {
  const preview = props.data.gitDiffPreview
  const sections = (preview?.data as Record<string, unknown> | null)?.sections as Array<{ diff?: string }> | undefined
  return sections?.[0]?.diff ?? ''
})

const mockDiffFiles = computed(() => {
  const preview = props.data.gitDiffPreview
  if (!preview) return null
  const data = preview.data as Record<string, unknown>
  const sections = data.sections as Array<{
    files: Array<{
      path: string
      previous_path?: string | null
      change_type: string
      additions: number
      deletions: number
      binary: boolean
      truncated: boolean
    }>
    diff: string
  }> | undefined
  if (!sections || sections.length === 0) return null
  const section = sections[0]
  return {
    files: section.files,
    diff: section.diff,
  }
})

// ---- CommitMessageEditor mock ----
const commitMessage = ref('feat(orchestrator): support dynamic dependency resolution')

// ---- ToolCatalogBrowser mock tools ----
const mockTools = computed<ToolDescriptorDto[]>(() => props.data.tools)
</script>

<template>
  <div class="component-preview">
    <!-- AgentActivityBanner -->
    <PreviewIslandCard v-if="componentName === 'AgentActivityBanner'" variant="raised" padding="sm" class="preview-frame">
      <AgentActivityBanner :activity="mockActivity" :agent-states="mockAgentStates" />
      <PreviewIslandCard v-if="!data.orchestration?.run" variant="section" padding="sm" class="preview-empty-hint" :hoverable="false">
        当前场景无编排运行数据，切换到「正常填充」或「智能体工作中」场景查看效果。
      </PreviewIslandCard>
    </PreviewIslandCard>

    <!-- ToolCallCard：共享 mock 全量展示五种状态（orca 悬浮卡片风格） -->
    <PreviewIslandCard v-else-if="componentName === 'ToolCallCard'" variant="section" padding="sm" class="preview-frame">
      <template #header><span class="preview-frame-title">工具调用卡片 — 5 种状态（hover 抬升 / 拖拽）</span></template>
      <div class="preview-card-list">
        <PreviewIslandCard
          v-for="call in mockToolCalls()"
          :key="call.id"
          variant="raised"
          padding="none"
          :hoverable="true"
          :draggable="true"
        >
          <ToolCallCard :tool-call="call" />
        </PreviewIslandCard>
      </div>
    </PreviewIslandCard>

    <!-- ThinkingProcess：共享 mock，不依赖编排场景 -->
    <PreviewIslandCard v-else-if="componentName === 'ThinkingProcess'" variant="raised" padding="sm" class="preview-frame">
      <template #header><span class="preview-frame-title">思考过程</span></template>
      <ThinkingProcess :steps="mockThinkingSteps()" />
    </PreviewIslandCard>

    <!-- ToolExecutionTimeline -->
    <PreviewIslandCard v-else-if="componentName === 'ToolExecutionTimeline'" variant="raised" padding="sm" class="preview-frame">
      <template #header><span class="preview-frame-title">工具执行时间线</span></template>
      <ToolExecutionTimeline :tool-executions="data.toolExecutions" />
    </PreviewIslandCard>

    <!-- ToolCatalogBrowser -->
    <PreviewIslandCard v-else-if="componentName === 'ToolCatalogBrowser'" variant="raised" padding="sm" class="preview-frame">
      <template #header><span class="preview-frame-title">工具目录</span></template>
      <ToolCatalogBrowser :tools="mockTools" />
    </PreviewIslandCard>

    <!-- ToolStatsDashboard -->
    <PreviewIslandCard v-else-if="componentName === 'ToolStatsDashboard'" variant="raised" padding="sm" class="preview-frame">
      <template #header><span class="preview-frame-title">工具统计</span></template>
      <ToolStatsDashboard :tool-executions="data.toolExecutions" />
    </PreviewIslandCard>

    <!-- DiffViewer -->
    <PreviewIslandCard v-else-if="componentName === 'DiffViewer'" variant="raised" padding="sm" class="preview-frame">
      <template #header><span class="preview-frame-title">Diff 查看器</span></template>
      <DiffViewer
        v-if="mockDiffFiles"
        :files="mockDiffFiles.files.map((f) => ({
          path: f.path,
          previousPath: f.previous_path,
          diffText: mockDiffFiles?.diff ?? '',
          additions: f.additions,
          deletions: f.deletions,
          binary: f.binary,
          truncated: f.truncated,
          changeType: f.change_type,
        }))"
        :selected-file-path="mockDiffFiles.files[0]?.path ?? null"
        :enable-hunk-actions="false"
      />
      <PreviewIslandCard v-else variant="section" padding="sm" class="preview-empty-hint" :hoverable="false">
        当前场景无 Git diff 数据，切换到「Git 变更」场景查看效果。
      </PreviewIslandCard>
    </PreviewIslandCard>

    <!-- CommitMessageEditor -->
    <PreviewIslandCard v-else-if="componentName === 'CommitMessageEditor'" variant="raised" padding="sm" class="preview-frame">
      <template #header><span class="preview-frame-title">Commit 消息编辑器</span></template>
      <CommitMessageEditor
        v-model="commitMessage"
        :recent-commits="[
          'refactor(orchestrator): support dynamic dependency resolution',
          'feat(graph): add cycle detection',
          'docs: update orchestrator architecture',
        ]"
      />
    </PreviewIslandCard>

    <!-- FileTreePanel（真实组件，经 apiBridge 走 mock 目录/搜索） -->
    <PreviewIslandCard v-else-if="componentName === 'FileTreePanel'" variant="raised" padding="none" class="preview-frame file-tree-frame">
      <template #header>
        <div class="file-tree-head">
          <span>文件树（真实 FileTreePanel）</span>
          <span class="file-tree-cwd">D:/workspace/tinadec</span>
        </div>
      </template>
      <RealFileTreePanel cwd="D:/workspace/tinadec" :approvals="data.approvals" />
    </PreviewIslandCard>

    <PreviewIslandCard v-else variant="section" padding="sm" class="preview-empty-hint" :hoverable="false">
      未找到组件：{{ componentName }}
    </PreviewIslandCard>
  </div>
</template>

<style scoped>
.component-preview {
  min-height: 100%;
  background: transparent;
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.preview-frame {
  max-width: 960px;
  width: 100%;
  margin: 0 auto;
}

.preview-frame-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.preview-card-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.preview-empty-hint {
  text-align: center;
  color: var(--text-muted, #6e7681);
  font-size: 13px;
  border-style: dashed !important;
}

.file-tree-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  width: 100%;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.file-tree-cwd {
  font-weight: 400;
  color: var(--text-muted, #6e7681);
  font-family: monospace;
  font-size: 11px;
}

/* 真实 FileTreePanel 宿主：限高滚动，保持岛内布局 */
.file-tree-frame :deep(.island-body) {
  height: 480px;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
.file-tree-frame :deep(.island-body > *) {
  flex: 1;
  min-height: 0;
  overflow: auto;
}
</style>
