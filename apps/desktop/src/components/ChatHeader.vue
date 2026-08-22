<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { SessionDto } from '../api'
import { useRunStore } from '@/stores/run'

const { t } = useI18n()

defineProps<{
  currentSession: SessionDto | null
}>()

/** Active runs for this session, newest first (§4.1: parallel runs get a switcher). */
const runStore = useRunStore()
const activeRuns = computed(() =>
  (runStore.runs as unknown as Array<{ id: string; status: string }>)
    .filter((r) => ['running', 'ready', 'pending', 'queued', 'awaiting_approval', 'awaiting_user', 'awaiting_delegate'].includes(r.status))
    .slice(0, 4),
)
const selectedRunId = computed(() => (runStore.selectedRunId as string | null) ?? null)

function selectRun(runId: string): void {
  runStore.select(runId)
}
</script>

<template>
  <div class="conversation-head">
    <h1>{{ currentSession?.title ?? t('chat.title') }}</h1>
    <div v-if="activeRuns.length > 0" class="run-pills" data-testid="run-pills">
      <button
        v-for="run in activeRuns"
        :key="run.id"
        type="button"
        class="run-pill"
        :class="{
          'run-pill--active': selectedRunId === run.id,
          'run-pill--running': run.status === 'running',
          'run-pill--waiting': String(run.status).startsWith('awaiting'),
        }"
        :title="`${run.id} · ${run.status}`"
        @click="selectRun(run.id)"
      >
        <span class="run-pill__dot" />
        {{ String(run.status).replace('_', ' ') }}
      </button>
      <span v-if="!selectedRunId" class="run-pills__hint">{{ t('chat.latestRun', 'latest') }}</span>
    </div>
  </div>
</template>

<style scoped>
.conversation-head {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.conversation-head h1 {
  font-size: 15px;
  font-weight: 700;
  margin: 0;
}

.run-pills {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.run-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 2px 9px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.run-pill--active {
  border-color: var(--accent-brand);
  color: var(--accent-brand);
}

.run-pill--running .run-pill__dot {
  background: var(--accent-info, #4a9eff);
  animation: pill-pulse 1.4s ease-in-out infinite;
}

.run-pill--waiting .run-pill__dot {
  background: var(--accent-warning, #e3b341);
}

.run-pill__dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--text-secondary);
}

@keyframes pill-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.35; }
}
</style>
