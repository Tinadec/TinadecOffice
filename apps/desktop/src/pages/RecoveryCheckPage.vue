<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { CheckCircle2, FileWarning, RefreshCw } from '@lucide/vue'
import { useUserActionStore } from '@/stores/userAction'
import { userToolActionDetail, userToolActionReason, userToolActionStatusMessage } from '@/userToolAction'

/**
 * Read-only inspection surface for `outcome_unknown` actions.
 *
 * Core contract (docs/app-core-ui.md §6): this state never auto-replays. The
 * user inspects the workspace out-of-band and either marks the action
 * completed or failed; there is intentionally no "retry execution" button.
 */
const route = useRoute()
const router = useRouter()
const store = useUserActionStore()
const { t, te } = useI18n()

const working = ref(false)
const loadError = ref<string | null>(null)

const actionId = computed(() => String(route.params.actionId ?? ''))
const action = computed(() => store.get(actionId.value))
const isUnknown = computed(() => action.value?.status === 'outcome_unknown')

const statusText = computed(() =>
  action.value ? userToolActionStatusMessage(action.value, action.value.tool_id, { t, te }) : '',
)

async function load(): Promise<void> {
  loadError.value = null
  try {
    await store.refresh(actionId.value)
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  }
}

async function decide(decision: 'mark_completed' | 'mark_failed'): Promise<void> {
  if (!actionId.value || working.value) return
  working.value = true
  try {
    await store.decideRecovery(actionId.value, {
      decision,
      reason: decision === 'mark_completed'
        ? t('governance.recoveryCompletedReason')
        : t('governance.recoveryFailedReason'),
    })
    void router.back()
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    working.value = false
  }
}

function back(): void {
  void router.back()
}

onMounted(() => {
  if (!action.value) void load()
})

onBeforeUnmount(() => {
  // Recovery decisions are terminal; nothing to stop explicitly, but keep the
  // store clean of one-off navigation actions.
})
</script>

<template>
  <div class="recovery-page" data-testid="recovery-page">
    <header class="recovery-page__header">
      <h1>{{ t('governance.recoveryTitle') }}</h1>
      <p class="recovery-page__subtitle">
        {{ t('governance.recoverySubtitle') }}
      </p>
    </header>

    <div v-if="!action && !loadError" class="recovery-page__state">
      <RefreshCw class="size-4 animate-spin" />
      {{ t('common.loading') }}
    </div>

    <div v-else-if="loadError" class="recovery-page__state recovery-page__state--error" data-testid="recovery-error">
      {{ loadError }}
      <button type="button" class="detail-dialog__btn" @click="load">{{ t('common.retry') }}</button>
    </div>

    <template v-else-if="action">
      <section v-if="!isUnknown" class="recovery-page__notice" data-testid="recovery-not-unknown">
        {{ t('governance.recoveryNotNeeded') }}
      </section>

      <dl class="recovery-page__grid">
        <div><dt>{{ t('governance.recoveryFacts.tool') }}</dt><dd>{{ action.tool_id }}</dd></div>
        <div><dt>{{ t('governance.recoveryFacts.status') }}</dt><dd data-testid="recovery-status">{{ statusText }}</dd></div>
        <div><dt>{{ t('governance.recoveryFacts.auditReference') }}</dt><dd class="font-mono text-xs">{{ action.audit_reference }}</dd></div>
        <div v-if="action.snapshot_id"><dt>{{ t('governance.recoveryFacts.snapshot') }}</dt><dd class="font-mono text-xs">{{ action.snapshot_id }}</dd></div>
        <div v-if="action.recovery_reason"><dt>{{ t('governance.recoveryFacts.lastReason') }}</dt><dd>{{ action.recovery_reason }}</dd></div>
        <div v-if="action.message"><dt>{{ t('governance.recoveryFacts.message') }}</dt><dd :title="userToolActionDetail(action, { t, te })">{{ userToolActionReason(action, { t, te }) }}</dd></div>
      </dl>

      <footer v-if="isUnknown" class="recovery-page__actions">
        <button
          type="button"
          class="detail-dialog__btn"
          :disabled="working"
          data-testid="recovery-mark-completed"
          @click="decide('mark_completed')"
        >
          <CheckCircle2 class="size-4" />
          {{ t('governance.markCompleted') }}
        </button>
        <button
          type="button"
          class="detail-dialog__btn detail-dialog__btn--danger"
          :disabled="working"
          data-testid="recovery-mark-failed"
          @click="decide('mark_failed')"
        >
          <FileWarning class="size-4" />
          {{ t('governance.markFailed') }}
        </button>
      </footer>

      <button type="button" class="detail-dialog__btn recovery-page__back" @click="back">
        {{ t('common.back') }}
      </button>
    </template>
  </div>
</template>

<style scoped>
.recovery-page {
  max-width: 720px;
  margin: 0 auto;
  padding: 24px 20px 40px;
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.recovery-page__header h1 {
  font-size: 18px;
  font-weight: 700;
  color: var(--text-primary);
}

.recovery-page__subtitle {
  margin-top: 4px;
  font-size: 13px;
  color: var(--text-secondary);
}

.recovery-page__grid {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 8px 16px;
  padding: 14px 16px;
  border-radius: 8px;
  background: var(--surface-section);
  font-size: 13px;
}

.recovery-page__grid dt {
  color: var(--text-secondary);
  font-family: var(--font-mono);
  font-size: 11px;
  padding-top: 2px;
}

.recovery-page__grid dd {
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.recovery-page__actions {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
}

.recovery-page__back {
  align-self: flex-start;
}

.recovery-page__state,
.recovery-page__notice {
  padding: 12px 14px;
  border-radius: 8px;
  background: var(--surface-section);
  font-size: 13px;
  display: flex;
  gap: 8px;
  align-items: center;
}

.recovery-page__state--error {
  color: var(--accent-danger);
}
</style>
