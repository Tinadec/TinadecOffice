<script setup lang="ts">
/**
 * 组件预览包装器
 * 根据选中的组件名称，用 mock 数据渲染对应的独立组件。
 */
import { computed, ref } from 'vue'
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

const props = defineProps<{
  componentName: string
  data: MockDataBundle
}>()

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

// ---- FileTreePanel mock (简化版，不调用 api) ----
// 由于 FileTreePanel 内部调用 api.listDirectory，这里用一个简化的内联文件树替代
const fileTreeEntries = computed(() => [
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

const expandedDirs = ref<Set<string>>(new Set(['src', 'src/__tests__']))

function toggleDir(path: string) {
  const next = new Set(expandedDirs.value)
  if (next.has(path)) next.delete(path)
  else next.add(path)
  expandedDirs.value = next
}

function filePath(entry: { name: string; depth: number }): string {
  return entry.name
}
</script>

<template>
  <div class="component-preview">
    <!-- AgentActivityBanner -->
    <div v-if="componentName === 'AgentActivityBanner'" class="preview-frame">
      <AgentActivityBanner :activity="mockActivity" :agent-states="mockAgentStates" />
      <div v-if="!data.orchestration?.run" class="preview-empty-hint">当前场景无编排运行数据，切换到「正常填充」或「智能体工作中」场景查看效果。</div>
    </div>

    <!-- ToolCallCard：共享 mock 全量展示五种状态 -->
    <div v-else-if="componentName === 'ToolCallCard'" class="preview-frame">
      <div class="preview-card-list">
        <ToolCallCard
          v-for="call in mockToolCalls()"
          :key="call.id"
          :tool-call="call"
        />
      </div>
    </div>

    <!-- ThinkingProcess：共享 mock，不依赖编排场景 -->
    <div v-else-if="componentName === 'ThinkingProcess'" class="preview-frame">
      <ThinkingProcess :steps="mockThinkingSteps()" />
    </div>

    <!-- ToolExecutionTimeline -->
    <div v-else-if="componentName === 'ToolExecutionTimeline'" class="preview-frame">
      <ToolExecutionTimeline :tool-executions="data.toolExecutions" />
    </div>

    <!-- ToolCatalogBrowser -->
    <div v-else-if="componentName === 'ToolCatalogBrowser'" class="preview-frame">
      <ToolCatalogBrowser :tools="mockTools" />
    </div>

    <!-- ToolStatsDashboard -->
    <div v-else-if="componentName === 'ToolStatsDashboard'" class="preview-frame">
      <ToolStatsDashboard :tool-executions="data.toolExecutions" />
    </div>

    <!-- DiffViewer -->
    <div v-else-if="componentName === 'DiffViewer'" class="preview-frame">
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
      <div v-else class="preview-empty-hint">当前场景无 Git diff 数据，切换到「Git 变更」场景查看效果。</div>
    </div>

    <!-- CommitMessageEditor -->
    <div v-else-if="componentName === 'CommitMessageEditor'" class="preview-frame">
      <CommitMessageEditor
        v-model="commitMessage"
        :recent-commits="[
          'refactor(orchestrator): support dynamic dependency resolution',
          'feat(graph): add cycle detection',
          'docs: update orchestrator architecture',
        ]"
      />
    </div>

    <!-- FileTreePanel (简化版预览) -->
    <div v-else-if="componentName === 'FileTreePanel'" class="preview-frame">
      <div class="file-tree-preview">
        <div class="file-tree-head">
          <span>文件树预览</span>
          <span class="file-tree-cwd">D:/workspace/tinadec</span>
        </div>
        <div class="file-tree-list">
          <div
            v-for="(entry, i) in fileTreeEntries"
            :key="i"
            class="file-tree-row"
            :style="{ paddingLeft: `${entry.depth * 16 + 8}px` }"
            @click="entry.isDir && toggleDir(filePath(entry))"
          >
            <span v-if="entry.isDir" class="ft-icon">📁</span>
            <span v-else class="ft-icon">📄</span>
            <span class="ft-name">{{ entry.name }}</span>
            <span v-if="!entry.isDir && entry.size" class="ft-size">{{ entry.size }}</span>
          </div>
        </div>
      </div>
    </div>

    <div v-else class="preview-empty-hint">
      未找到组件：{{ componentName }}
    </div>
  </div>
</template>

<style scoped>
.component-preview {
  height: 100%;
  overflow: auto;
  padding: 16px;
  background: var(--bg-primary, #0d1117);
}

.preview-frame {
  max-width: 900px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.preview-card-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.preview-empty-hint {
  padding: 24px;
  text-align: center;
  color: var(--text-muted, #8b949e);
  font-size: 13px;
  background: var(--bg-secondary, #161b22);
  border: 1px dashed var(--border-muted, #30363d);
  border-radius: 8px;
}

.file-tree-preview {
  border: 1px solid var(--border-muted, #30363d);
  border-radius: 8px;
  overflow: hidden;
  background: var(--bg-secondary, #161b22);
}

.file-tree-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border-muted, #30363d);
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary, #e6edf3);
}

.file-tree-cwd {
  font-weight: 400;
  color: var(--text-muted, #8b949e);
  font-family: monospace;
}

.file-tree-list {
  padding: 4px 0;
}

.file-tree-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 8px;
  cursor: pointer;
  font-size: 12px;
  color: var(--text-secondary, #c9d1d9);
  transition: background 0.12s;
}

.file-tree-row:hover {
  background: var(--bg-hover, #21262d);
}

.ft-icon {
  font-size: 14px;
  line-height: 1;
}

.ft-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ft-size {
  color: var(--text-muted, #8b949e);
  font-size: 11px;
}
</style>
