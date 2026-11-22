<script setup lang="ts">
import { computed, toRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { Activity, Bot, Layers3, ListChecks } from '@lucide/vue'
import { UiIslandCard } from '@/components/ui'
import { useSessionPlan } from '@/components/plan/useSessionPlan'
import type { OrchestrationSnapshotDto } from '@/api'

const props = defineProps<{
  sessionId: string | null | undefined
  working?: boolean
  activeRunCount?: number
  orchestration?: OrchestrationSnapshotDto | null
}>()

const emit = defineEmits<{
  'open-orchestration': []
  'open-agent': []
}>()

const { t, te } = useI18n()

function label(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? String(t(key, params ?? {})) : fallback
}

const sessionIdRef = toRef(props, 'sessionId')
const { hasPlan, progress, progressPercent } = useSessionPlan(sessionIdRef)

const activeWorkers = computed(() => {
  const nodes = props.orchestration?.nodes ?? []
  const runningNodes = nodes.filter((node) => {
    const st = String((node as { status?: string }).status ?? '').toLowerCase()
    return st === 'running' || st === 'in_progress' || st === 'assigned'
  }).length
  if (runningNodes > 0) return runningNodes
  return props.activeRunCount ?? (props.working ? 1 : 0)
})

const isRunning = computed(() => Boolean(props.working || activeWorkers.value > 0))
</script>

<template>
  <UiIslandCard variant="raised" padding="none" class="bento-widget session-run-widget" data-testid="session-run-widget">
    <div class="bento-widget-inner">
      <div class="bento-widget-head">
        <span class="bento-widget-icon is-run">
          <Activity :size="15" aria-hidden="true" />
        </span>
        <span class="bento-widget-title">{{ label('context.bentoSessionRunTitle', 'Session & Plan') }}</span>
        <span class="bento-status-dot" :class="{ active: isRunning }" />
      </div>

      <div class="bento-run-headline" :class="{ active: isRunning }">
        {{
          isRunning
            ? label('context.bentoRunActive', `Running · ${activeWorkers} active`, { workers: activeWorkers })
            : label('context.bentoRunIdle', 'Idle · ready for next task')
        }}
      </div>

      <div v-if="hasPlan && progress.total_steps > 0" class="bento-plan-block" data-testid="session-run-plan-progress">
        <div class="bento-plan-row">
          <ListChecks :size="12" aria-hidden="true" />
          <span>
            {{
              label(
                'context.bentoPlanProgress',
                `Plan ${progress.completed_steps}/${progress.total_steps}`,
                { done: progress.completed_steps, total: progress.total_steps },
              )
            }}
          </span>
          <span class="bento-plan-pct">{{ progressPercent }}%</span>
        </div>
        <div class="bento-plan-bar" role="progressbar" :aria-valuenow="progressPercent" aria-valuemin="0" aria-valuemax="100">
          <div class="bento-plan-fill" :style="{ width: `${progressPercent}%` }" />
        </div>
      </div>

      <div class="bento-widget-links">
        <button type="button" class="bento-chip-btn" data-testid="session-run-open-orchestration" @click="emit('open-orchestration')">
          <Layers3 :size="12" aria-hidden="true" />
          <span>{{ label('context.bentoOpenOrchestration', 'Orchestration') }}</span>
        </button>
        <button type="button" class="bento-chip-btn" data-testid="session-run-open-agent" @click="emit('open-agent')">
          <Bot :size="12" aria-hidden="true" />
          <span>{{ label('context.bentoOpenAgent', 'Agent') }}</span>
        </button>
      </div>
    </div>
  </UiIslandCard>
</template>

<style scoped>
.bento-widget { width: 100%; min-width: 0; flex: none; }
.bento-widget-inner { display: flex; flex-direction: column; gap: 8px; padding: 12px 13px; min-width: 0; }
.bento-widget-head { display: flex; align-items: center; gap: 8px; min-width: 0; }
.bento-widget-icon {
  display: grid; place-items: center; width: 26px; height: 26px; border-radius: 7px; flex: none;
  background: color-mix(in srgb, var(--accent-primary) 14%, transparent);
  color: var(--accent-primary);
}
.bento-widget-title { font-size: 12.5px; font-weight: 700; color: var(--text-primary); flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.bento-status-dot { width: 7px; height: 7px; border-radius: 999px; background: var(--text-muted); opacity: 0.45; flex: none; }
.bento-status-dot.active { background: var(--accent-success, #22c55e); opacity: 1; box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-success, #22c55e) 22%, transparent); }
.bento-run-headline { font-size: 12px; color: var(--text-secondary); line-height: 1.35; }
.bento-run-headline.active { color: var(--text-primary); font-weight: 600; }
.bento-plan-block { display: flex; flex-direction: column; gap: 4px; }
.bento-plan-row { display: flex; align-items: center; gap: 5px; font-size: 11px; color: var(--text-secondary); }
.bento-plan-pct { margin-left: auto; font-variant-numeric: tabular-nums; color: var(--text-muted); }
.bento-plan-bar { height: 4px; border-radius: 999px; background: var(--surface-hover); overflow: hidden; }
.bento-plan-fill { height: 100%; border-radius: 999px; background: var(--accent-primary); transition: width 180ms ease; }
.bento-widget-links { display: flex; flex-wrap: wrap; gap: 6px; padding-top: 4px; }
.bento-chip-btn {
  display: inline-flex; align-items: center; gap: 4px; padding: 4px 8px; border-radius: 7px;
  border: 1px solid var(--border-muted); background: var(--surface-section); color: var(--text-secondary);
  font-size: 11px; cursor: pointer; transition: background 140ms ease, color 140ms ease, border-color 140ms ease;
}
.bento-chip-btn:hover { background: var(--surface-hover); color: var(--text-primary); border-color: var(--accent-primary); }
</style>
