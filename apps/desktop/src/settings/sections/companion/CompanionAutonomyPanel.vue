<script setup lang="ts">
/**
 * One companion's autonomy rules. Defaults are off; the interval and the daily budget are clamped
 * to the range Core accepts before they are sent, and "act once now" reports Core's own answer —
 * including the reason it did nothing — instead of inventing activity.
 */
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Play } from '@lucide/vue'
import { UiButton, UiSwitch } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import {
  companionApi,
  type CompanionAutonomyTickResult,
  type CompanionDetail,
} from '@/lib/companionApi'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'

const props = defineProps<{ companion: CompanionDetail }>()
const emit = defineEmits<{ updated: [detail: CompanionDetail]; conflict: [] }>()

const { t, te } = useI18n()
const { notify } = useNotifications()

const MIN_INTERVAL = 5
const MAX_INTERVAL = 1440
const MIN_BUDGET = 0
const MAX_BUDGET = 50

const enabled = ref(props.companion.autonomy.enabled)
const paused = ref(props.companion.autonomy.paused)
const interval = ref(props.companion.autonomy.interval_minutes)
const budget = ref(props.companion.autonomy.daily_action_budget)
const saving = ref(false)
const ticking = ref(false)
const tickResult = ref<CompanionAutonomyTickResult | null>(null)

watch(() => props.companion, (next) => {
  enabled.value = next.autonomy.enabled
  paused.value = next.autonomy.paused
  interval.value = next.autonomy.interval_minutes
  budget.value = next.autonomy.daily_action_budget
})

function clamp(value: number, min: number, max: number, fallback: number): number {
  if (!Number.isFinite(value)) return fallback
  return Math.min(max, Math.max(min, Math.round(value)))
}

function actionLabel(action: string): string {
  switch (action) {
    case 'create_group': return t('settings.companions.autonomy.actionCreateGroup')
    case 'invite': return t('settings.companions.autonomy.actionInvite')
    case 'send_message': return t('settings.companions.autonomy.actionSendMessage')
    default: return t('settings.companions.autonomy.actionNone')
  }
}

/** Core's reason codes are stable; an unknown one is shown as it arrived rather than guessed at. */
function reasonLabel(reason: string | null): string {
  if (!reason) return t('settings.companions.autonomy.tickNoneNoReason')
  const key = `settings.companions.autonomy.reason.${reason}`
  return te(key) ? t(key) : reason
}

async function save() {
  if (saving.value) return
  const nextInterval = clamp(interval.value, MIN_INTERVAL, MAX_INTERVAL, MIN_INTERVAL)
  const nextBudget = clamp(budget.value, MIN_BUDGET, MAX_BUDGET, MIN_BUDGET)
  interval.value = nextInterval
  budget.value = nextBudget
  saving.value = true
  try {
    const updated = await companionApi.patchCompanion(props.companion.id, props.companion.revision, {
      autonomy: {
        enabled: enabled.value,
        paused: paused.value,
        interval_minutes: nextInterval,
        daily_action_budget: nextBudget,
      },
    })
    emit('updated', updated)
  } catch (error) {
    if (isCompanionRevisionConflict(error)) { emit('conflict'); return }
    notify.error(error, { title: t('settings.companions.autonomy.saveFailed'), key: `companion-autonomy-${props.companion.id}` })
  } finally {
    saving.value = false
  }
}

async function tick() {
  if (ticking.value) return
  ticking.value = true
  try {
    tickResult.value = await companionApi.tickAutonomy(props.companion.id)
  } catch (error) {
    if (isCompanionRevisionConflict(error)) { emit('conflict'); return }
    notify.error(error, { title: t('settings.companions.autonomy.tickFailed'), key: `companion-tick-${props.companion.id}` })
  } finally {
    ticking.value = false
  }
}
</script>

<template>
  <div class="companion-panel" role="tabpanel" data-testid="companion-autonomy-panel">
    <h4>{{ t('settings.companions.autonomy.title') }}</h4>
    <p class="companion-hint">{{ t('settings.companions.autonomy.hint') }}</p>

    <div class="companion-switch-row">
      <strong>{{ t('settings.companions.autonomy.enabled') }}</strong>
      <UiSwitch v-model="enabled" data-testid="companion-autonomy-enabled" />
    </div>
    <div class="companion-switch-row">
      <div class="companion-switch-copy">
        <strong>{{ t('settings.companions.autonomy.paused') }}</strong>
        <p class="companion-hint">{{ t('settings.companions.autonomy.pausedHint') }}</p>
      </div>
      <UiSwitch v-model="paused" data-testid="companion-autonomy-paused" />
    </div>

    <div class="companion-field">
      <label for="companion-autonomy-interval">{{ t('settings.companions.autonomy.interval') }}</label>
      <input
        id="companion-autonomy-interval"
        v-model.number="interval"
        class="settings-select"
        data-testid="companion-autonomy-interval"
        type="number"
        :min="MIN_INTERVAL"
        :max="MAX_INTERVAL"
      />
      <p class="companion-hint">{{ t('settings.companions.autonomy.intervalHint') }}</p>
    </div>
    <div class="companion-field">
      <label for="companion-autonomy-budget">{{ t('settings.companions.autonomy.budget') }}</label>
      <input
        id="companion-autonomy-budget"
        v-model.number="budget"
        class="settings-select"
        data-testid="companion-autonomy-budget"
        type="number"
        :min="MIN_BUDGET"
        :max="MAX_BUDGET"
      />
      <p class="companion-hint">{{ t('settings.companions.autonomy.budgetHint') }}</p>
    </div>

    <div class="companion-inline companion-inline-end">
      <UiButton size="sm" data-testid="companion-autonomy-save" :disabled="saving" @click="save">
        {{ t('settings.companions.autonomy.save') }}
      </UiButton>
      <UiButton variant="outline" size="sm" data-testid="companion-autonomy-tick" :disabled="ticking" @click="tick">
        <Play :size="14" />
        <span>{{ ticking ? t('settings.companions.autonomy.ticking') : t('settings.companions.autonomy.tick') }}</span>
      </UiButton>
    </div>

    <p v-if="tickResult" class="companion-tick-result" role="status" data-testid="companion-autonomy-result">
      <template v-if="tickResult.acted && tickResult.action !== 'none'">
        {{ t('settings.companions.autonomy.tickActed', { action: actionLabel(tickResult.action) }) }}
      </template>
      <template v-else>
        {{ t('settings.companions.autonomy.tickNone', { reason: reasonLabel(tickResult.reason) }) }}
      </template>
    </p>
  </div>
</template>

<style scoped>
.companion-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.companion-panel h4 {
  margin: 0;
  font-size: 13px;
  color: var(--text-primary);
}

.companion-hint {
  margin: 0;
  font-size: 11px;
  line-height: 1.5;
  color: var(--text-muted);
}

.companion-switch-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  font-size: 13px;
  color: var(--text-primary);
}

.companion-switch-copy {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.companion-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.companion-field label {
  font-size: 12px;
  color: var(--text-secondary);
}

.companion-inline {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.companion-inline-end {
  justify-content: flex-end;
}

.companion-tick-result {
  margin: 0;
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--surface-chrome, transparent);
  font-size: 12px;
  color: var(--text-primary);
}
</style>
