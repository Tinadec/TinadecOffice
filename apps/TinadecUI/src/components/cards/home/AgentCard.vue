<script setup lang="ts">
import { computed, provide } from 'vue'
import { useI18n } from 'vue-i18n'
import { Filter } from '@lucide/vue'
import AgentActivityPanel from '@/components/AgentActivityPanel.vue'
import { homeController } from '@/controllers/HomeController'
import { activeDockFocus, clearDockWorkerFilter, highlightedDockCard } from '@/lib/homeDockSync'

const c = homeController
const { t } = useI18n()

provide('agentCard:sessionId', c.selectedSessionId)

const focusedWorkerLabel = computed(() => {
  const focus = activeDockFocus.value
  if (!focus || focus.card !== 'agent') return null
  return focus.workerLabel ?? focus.workerHandle ?? focus.taskId ?? null
})
</script>

<template>
  <div
    class="agent-card-shell"
    data-dock-card="agent"
    :data-dock-highlighted="highlightedDockCard === 'agent' ? 'true' : undefined"
  >
    <div v-if="focusedWorkerLabel" class="agent-card-focus-bar" data-testid="agent-card-focus-banner">
      <Filter :size="12" aria-hidden="true" />
      <span class="agent-card-focus-label">{{ t('worktreeDock.filterWorker', { worker: focusedWorkerLabel }) }}</span>
      <button type="button" class="agent-card-focus-clear" @click="clearDockWorkerFilter">
        {{ t('worktreeDock.clearFilter') }}
      </button>
    </div>
    <div class="agent-card-body">
      <AgentActivityPanel
        :activity="c.agentActivity.value"
        :agent-states="c.agentStatesMap.value"
        :thinking-steps="c.agentThinkingSteps.value"
        :progress-events="c.agentProgressEvents.value"
        :orchestration="c.orchestration.value"
        :feed="c.conversationFeed"
        :runs="c.runs.value"
        :workspace-root="c.currentProject.value?.path ?? null"
      />
    </div>
  </div>
</template>

<style scoped>
.agent-card-shell {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}

.agent-card-focus-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  border-bottom: 1px solid color-mix(in srgb, var(--accent-primary) 30%, var(--border-card));
  background: color-mix(in srgb, var(--accent-primary) 10%, var(--surface-section));
  color: var(--accent-primary);
  font-size: 11px;
  font-weight: 600;
  flex-shrink: 0;
}

.agent-card-focus-label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-card-focus-clear {
  border: 0;
  padding: 2px 6px;
  border-radius: 5px;
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
  font-size: 10.5px;
  font-weight: 600;
  cursor: pointer;
}

.agent-card-body {
  flex: 1;
  min-height: 0;
}
</style>
