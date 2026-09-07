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
    // 词表以 Core 共享 12 态为准（RunStatusMachine）。pills 是“可见性”口径：所有非终态 run
    // 都要露出——尤其 awaiting_user（监督升级 / 等待用户决策）必须显示为 waiting pill，否则
    // 用户看不到“run 在等我决策”的唯一信号（此前误把它排除，导致升级后聊天头部一片空白）。
    // 与 HomeController.activeRuns 用途不同：那里是“挂 SSE 流 + 引导目标”口径，排除 awaiting_user
    // 这类长驻空闲态才合理，两者不必同口径。
    .filter((r) => !['completed', 'failed', 'cancelled'].includes(r.status))
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
          'run-pill--running': ['planning', 'understanding', 'executing', 'replanning', 'reviewing'].includes(run.status),
          'run-pill--waiting': String(run.status).startsWith('awaiting') || run.status === 'paused',
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
