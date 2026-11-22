<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { CheckCircle2, FileWarning, RefreshCw, X } from '@lucide/vue'
import { useUserActionStore } from '@/stores/userAction'
import { userToolActionDetail, userToolActionReason, userToolActionStatusMessage } from '@/userToolAction'

const props = withDefaults(defineProps<{
  open?: boolean
  actionId?: string | null
}>(), {
  open: undefined,
  actionId: null,
})

const emit = defineEmits<{
  'update:open': [value: boolean]
  close: []
  decided: [payload: { actionId: string; decision: 'mark_completed' | 'mark_failed' }]
}>()

const store = useUserActionStore()
const { t, te } = useI18n()

const internalOpen = ref(false)
const eventActionId = ref<string | null>(null)
const working = ref(false)
const loadError = ref<string | null>(null)

const isOpen = computed(() => (props.open !== undefined ? props.open : internalOpen.value))
const activeActionId = computed(() => props.actionId || eventActionId.value || '')
const action = computed(() => (activeActionId.value ? store.get(activeActionId.value) : undefined))
const isUnknown = computed(() => action.value?.status === 'outcome_unknown')
const statusText = computed(() =>
  action.value ? userToolActionStatusMessage(action.value, action.value.tool_id, { t, te }) : '',
)

async function load(): Promise<void> {
  if (!activeActionId.value) return
  loadError.value = null
  try {
    await store.refresh(activeActionId.value)
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  }
}

function close(): void {
  internalOpen.value = false
  emit('update:open', false)
  emit('close')
}

async function decide(decision: 'mark_completed' | 'mark_failed'): Promise<void> {
  if (!activeActionId.value || working.value) return
  working.value = true
  try {
    await store.decideRecovery(activeActionId.value, {
      decision,
      reason: decision === 'mark_completed'
        ? t('governance.recoveryCompletedReason')
        : t('governance.recoveryFailedReason'),
    })
    emit('decided', { actionId: activeActionId.value, decision })
    close()
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    working.value = false
  }
}

watch([isOpen, activeActionId], ([openNow, id]) => {
  if (openNow && id && !action.value) {
    void load()
  }
}, { immediate: true })

function onOpenRecoveryEvent(event: Event): void {
  const custom = event as CustomEvent<{ actionId?: string }>
  const id = custom.detail?.actionId
  if (!id) return
  eventActionId.value = id
  loadError.value = null
  internalOpen.value = true
  emit('update:open', true)
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && isOpen.value) {
    event.preventDefault()
    close()
  }
}

onMounted(() => {
  window.addEventListener('tinadec:open-recovery-check', onOpenRecoveryEvent)
  window.addEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => {
  window.removeEventListener('tinadec:open-recovery-check', onOpenRecoveryEvent)
  window.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <Teleport to="body">
    <div
      v-if="isOpen"
      class="recovery-backdrop"
      data-testid="recovery-dialog-backdrop"
      @click.self="close"
    >
      <div
        class="recovery-dialog"
        role="dialog"
        aria-modal="true"
        :aria-label="t('governance.recoveryTitle')"
        data-testid="recovery-check-dialog"
      >
        <header class="recovery-dialog__header">
          <div>
            <h2>{{ t('governance.recoveryTitle') }}</h2>
            <p class="recovery-dialog__subtitle">{{ t('governance.recoverySubtitle') }}</p>
          </div>
          <button
            type="button"
            class="recovery-dialog__close"
            :title="t('common.close')"
            :aria-label="t('common.close')"
            data-testid="recovery-close-btn"
            @click="close"
          >
            <X :size="16" aria-hidden="true" />
          </button>
        </header>

        <div v-if="!action && !loadError" class="recovery-dialog__state">
          <RefreshCw class="size-4 animate-spin" />
          {{ t('common.loading') }}
        </div>

        <div v-else-if="loadError" class="recovery-dialog__state recovery-dialog__state--error" data-testid="recovery-error">
          {{ loadError }}
          <button type="button" class="detail-dialog__btn" @click="load">{{ t('common.retry') }}</button>
        </div>

        <template v-else-if="action">
          <section v-if="!isUnknown" class="recovery-dialog__notice" data-testid="recovery-not-unknown">
            {{ t('governance.recoveryNotNeeded') }}
          </section>

          <dl class="recovery-dialog__grid">
            <div><dt>{{ t('governance.recoveryFacts.tool') }}</dt><dd>{{ action.tool_id }}</dd></div>
            <div><dt>{{ t('governance.recoveryFacts.status') }}</dt><dd data-testid="recovery-status">{{ statusText }}</dd></div>
            <div><dt>{{ t('governance.recoveryFacts.auditReference') }}</dt><dd class="font-mono text-xs">{{ action.audit_reference }}</dd></div>
            <div v-if="action.snapshot_id"><dt>{{ t('governance.recoveryFacts.snapshot') }}</dt><dd class="font-mono text-xs">{{ action.snapshot_id }}</dd></div>
            <div v-if="action.recovery_reason"><dt>{{ t('governance.recoveryFacts.lastReason') }}</dt><dd>{{ action.recovery_reason }}</dd></div>
            <div v-if="action.message"><dt>{{ t('governance.recoveryFacts.message') }}</dt><dd :title="userToolActionDetail(action, { t, te })">{{ userToolActionReason(action, { t, te }) }}</dd></div>
          </dl>

          <footer v-if="isUnknown" class="recovery-dialog__actions">
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
        </template>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.recovery-backdrop {
  position: fixed;
  inset: 0;
  z-index: 70;
  display: grid;
  place-items: center;
  padding: 16px;
  background: rgba(0, 0, 0, 0.42);
  backdrop-filter: blur(2px);
}

.recovery-dialog {
  width: min(640px, 95vw);
  max-height: 86vh;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 18px 20px;
  border-radius: 14px;
  border: 1px solid var(--border-muted);
  background: var(--surface-panel, var(--surface-section));
  box-shadow: var(--shadow-panel);
}

.recovery-dialog__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.recovery-dialog__header h2 {
  margin: 0;
  font-size: 16px;
  font-weight: 700;
  color: var(--text-primary);
}

.recovery-dialog__subtitle {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.recovery-dialog__close {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.recovery-dialog__close:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.recovery-dialog__grid {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 8px 16px;
  margin: 0;
  padding: 12px 14px;
  border-radius: 8px;
  background: var(--surface-raised);
  font-size: 13px;
}

.recovery-dialog__grid dt {
  color: var(--text-secondary);
  font-family: var(--font-mono);
  font-size: 11px;
  padding-top: 2px;
}

.recovery-dialog__grid dd {
  margin: 0;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.recovery-dialog__actions {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
}

.recovery-dialog__state,
.recovery-dialog__notice {
  padding: 12px 14px;
  border-radius: 8px;
  background: var(--surface-raised);
  font-size: 13px;
  display: flex;
  gap: 8px;
  align-items: center;
}

.recovery-dialog__state--error {
  color: var(--accent-danger);
}
</style>
