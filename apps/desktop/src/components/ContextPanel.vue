<script setup lang="ts">
import { ref } from 'vue'
import { Activity, PanelRightClose, PanelRightOpen, Terminal, GitBranch, ShieldCheck, MoreHorizontal } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import ApprovalTab from './ApprovalTab.vue'
import DiffTab from './DiffTab.vue'
import EventsTab from './EventsTab.vue'
import DoctorTab from './DoctorTab.vue'
import OrchestrationTab from './OrchestrationTab.vue'
import type { ApprovalDto, EventEnvelope, DoctorReportDto, OrchestrationSnapshotDto, RuntimeReadinessReceiptDto, ToolExecutionTimelineItemDto } from '../api'

const { t } = useI18n()

const activeTab = ref<'approval' | 'tasks' | 'diff' | 'events' | 'doctor'>('approval')
const collapsed = defineModel<boolean>('collapsed', { default: false })
const panelWidth = defineModel<number>('width', { default: 320 })

const isResizing = ref(false)

function startResize(event: MouseEvent) {
  isResizing.value = true
  const startX = event.clientX
  const startWidth = panelWidth.value

  function onMouseMove(e: MouseEvent) {
    const delta = startX - e.clientX
    const newWidth = Math.max(200, Math.min(480, startWidth + delta))
    panelWidth.value = newWidth
  }

  function onMouseUp() {
    isResizing.value = false
    document.removeEventListener('mousemove', onMouseMove)
    document.removeEventListener('mouseup', onMouseUp)
  }

  document.addEventListener('mousemove', onMouseMove)
  document.addEventListener('mouseup', onMouseUp)
}

defineProps<{
  approvals: ApprovalDto[]
  events: EventEnvelope[]
  doctor: DoctorReportDto | null
  readiness: RuntimeReadinessReceiptDto | null
  orchestration: OrchestrationSnapshotDto | null
  toolExecutions: ToolExecutionTimelineItemDto[]
  shellCommand: string
  busy: boolean
  selectedSessionId: string | null
  currentProjectPath: string | undefined
}>()

const emit = defineEmits<{
  'request-approval': []
  'decide-approval': [approval: ApprovalDto, decision: 'approved' | 'rejected']
  'approval-created': [approval: ApprovalDto]
  'update:shellCommand': [value: string]
}>()

const toolbarItems = [
  { key: 'approval' as const, icon: ShieldCheck, label: () => t('context.approval'), badge: (props: any) => props.approvals?.length || 0 },
  { key: 'tasks' as const, icon: Terminal, label: () => 'Tasks' },
  { key: 'diff' as const, icon: GitBranch, label: () => t('context.diff') },
  { key: 'events' as const, icon: MoreHorizontal, label: () => t('context.events') },
  { key: 'doctor' as const, icon: Activity, label: () => 'Runtime' },
]
</script>

<template>
  <aside
    class="float-panel"
    :class="{ collapsed, resizing: isResizing }"
    :style="{ width: collapsed ? undefined : `${panelWidth}px` }"
  >
    <!-- 拖拽手柄 -->
    <div
      v-if="!collapsed"
      class="float-panel-resizer"
      @mousedown="startResize"
    />

    <!-- 统一的标签/图标区域 -->
    <div class="float-panel-tabs-area">
      <!-- 折叠/展开按钮 - 始终在左侧 -->
      <button
        class="float-panel-toggle-btn"
        :title="collapsed ? t('app.expand') : t('app.collapse')"
        @click="collapsed = !collapsed"
      >
        <PanelRightOpen v-if="collapsed" :size="16" />
        <PanelRightClose v-else :size="16" />
      </button>

      <!-- 标签项容器 - 横向或竖向 -->
      <div class="float-panel-items" :class="{ 'is-collapsed': collapsed }">
        <button
          v-for="(item, index) in toolbarItems"
          :key="item.key"
          class="float-panel-item"
          :class="{ active: activeTab === item.key }"
          :style="{ '--item-index': index }"
          :title="item.label()"
          @click="activeTab = item.key"
        >
          <component :is="item.icon" :size="collapsed ? 18 : 14" />
          <span v-if="!collapsed" class="item-label">{{ item.label() }}</span>
          <span v-if="item.badge && item.badge($props) > 0" class="item-badge">{{ item.badge($props) }}</span>
        </button>
      </div>
    </div>

    <!-- 内容区域 -->
    <template v-if="!collapsed">
      <div class="float-panel-content">
        <ApprovalTab
          v-if="activeTab === 'approval'"
          :approvals="approvals"
          :shell-command="shellCommand"
          :busy="busy"
          :selected-session-id="selectedSessionId"
          @request-approval="emit('request-approval')"
          @decide-approval="(a, d) => emit('decide-approval', a, d)"
          @update:shell-command="emit('update:shellCommand', $event)"
        />
        <OrchestrationTab v-if="activeTab === 'tasks'" :snapshot="orchestration" :tool-executions="toolExecutions" />
        <DiffTab
          v-if="activeTab === 'diff'"
          :current-project-path="currentProjectPath"
          :selected-session-id="selectedSessionId"
          @approval-created="emit('approval-created', $event)"
        />
        <EventsTab v-if="activeTab === 'events'" :events="events" />
        <DoctorTab v-if="activeTab === 'doctor'" :doctor="doctor" :readiness="readiness" />
      </div>
    </template>
  </aside>
</template>
