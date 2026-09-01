<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ShieldCheck } from '@lucide/vue'
import { api, type CreatePreAuthorizationInput, type PreAuthorizationDto } from '@/api'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  'update:open': [value: boolean]
  created: [preAuthorization: PreAuthorizationDto]
}>()

const { t } = useI18n()

const runId = ref('')
const laneKey = ref('')
const toolScope = ref('')
const riskMax = ref('low')
const maxUses = ref<number | ''>(1)
const expiresAt = ref('')
const parameterHash = ref('')
const summary = ref('')

const submitting = ref(false)
const error = ref<string | null>(null)
const created = ref<PreAuthorizationDto | null>(null)
const dialog = ref<HTMLDialogElement | null>(null)

const toolIds = computed(() =>
  toolScope.value.split(',').map((t) => t.trim()).filter(Boolean),
)
const canSubmit = computed(
  () => runId.value.trim().length > 0 && toolIds.value.length > 0 && !submitting.value,
)

watch(
  () => props.open,
  (open) => {
    if (!import.meta.env.SSR && dialog.value) {
      if (open && !dialog.value.open) {
        resetForm()
        dialog.value.showModal()
      } else if (!open && dialog.value.open) {
        dialog.value.close()
      }
    }
  },
)

function resetForm(): void {
  runId.value = ''
  laneKey.value = ''
  toolScope.value = ''
  riskMax.value = 'low'
  maxUses.value = 1
  expiresAt.value = ''
  parameterHash.value = ''
  summary.value = ''
  error.value = null
  created.value = null
}

function cancel(): void {
  emit('update:open', false)
}

async function submit(): Promise<void> {
  if (!canSubmit.value) return
  submitting.value = true
  error.value = null
  try {
    const payload: CreatePreAuthorizationInput = {
      run_id: runId.value.trim(),
      tool_scope: toolIds.value,
      risk_max: riskMax.value,
      max_uses: Math.max(1, Math.floor(Number(maxUses.value) || 1)),
    }
    if (laneKey.value.trim()) payload.lane_key = laneKey.value.trim()
    if (parameterHash.value.trim()) payload.parameter_constraint_hash = parameterHash.value.trim()
    if (summary.value.trim()) payload.summary = summary.value.trim()
    if (expiresAt.value) {
      const parsed = new Date(expiresAt.value)
      if (!Number.isNaN(parsed.getTime())) payload.expires_at = parsed.toISOString()
    }
    const result = await api.createPreAuthorization(payload)
    created.value = result
    emit('created', result)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <Teleport to="body">
    <dialog
      ref="dialog"
      class="detail-dialog no-drag pre-auth-dialog"
      data-testid="pre-auth-dialog"
      @cancel.prevent="cancel"
      @click="(e: MouseEvent) => { if (e.target === dialog) cancel() }"
    >
      <div class="detail-dialog__heading">
        <span class="detail-dialog__icon" aria-hidden="true"><ShieldCheck :size="20" /></span>
        <h2 data-testid="pre-auth-title">
          {{ t('governance.preAuthorizeTitle', 'Pre-authorize tools') }}
        </h2>
      </div>

      <template v-if="!created">
        <p class="detail-dialog__summary" data-testid="pre-auth-hint">
          {{
            t(
              'governance.preAuthorizeHint',
              'Grant a bounded number of unattended uses for specific tools on one run. Binding checks still apply to every invocation.',
            )
          }}
        </p>

        <section class="panel pre-auth-form">
          <label>
            {{ t('governance.preAuthRunId', 'Run ID') }}
            <input v-model="runId" data-testid="pre-auth-run-id" placeholder="00000000-0000-0000-0000-000000000000" />
          </label>
          <label>
            {{ t('governance.preAuthLane', 'Lane (optional)') }}
            <input v-model="laneKey" data-testid="pre-auth-lane" placeholder="main" />
          </label>
          <label>
            {{ t('governance.preAuthTools', 'Tool IDs (comma-separated)') }}
            <input v-model="toolScope" data-testid="pre-auth-tools" placeholder="git_commit, shell" />
          </label>
          <div class="pre-auth-grid">
            <label>
              {{ t('governance.preAuthRiskMax', 'Risk max') }}
              <select v-model="riskMax" data-testid="pre-auth-risk">
                <option value="low">low</option>
                <option value="medium">medium</option>
                <option value="high">high</option>
                <option value="elevated">elevated</option>
                <option value="critical">critical</option>
              </select>
            </label>
            <label>
              {{ t('governance.preAuthMaxUses', 'Max uses') }}
              <input v-model.number="maxUses" type="number" min="1" data-testid="pre-auth-max-uses" />
            </label>
            <label>
              {{ t('governance.preAuthExpires', 'Expires at (optional)') }}
              <input v-model="expiresAt" type="datetime-local" data-testid="pre-auth-expires" />
            </label>
          </div>
          <label>
            {{ t('governance.preAuthHash', 'Parameter constraint hash (optional)') }}
            <input v-model="parameterHash" data-testid="pre-auth-hash" />
          </label>
          <label>
            {{ t('governance.preAuthSummary', 'Summary (optional)') }}
            <input v-model="summary" data-testid="pre-auth-summary" />
          </label>

          <p v-if="error" class="pre-auth-error" data-testid="pre-auth-error">{{ error }}</p>
        </section>

        <div class="detail-dialog__actions">
          <button type="button" class="detail-dialog__btn" data-testid="pre-auth-cancel" @click="cancel">
            {{ t('common.cancel', 'Cancel') }}
          </button>
          <button
            type="button"
            class="detail-dialog__btn detail-dialog__btn--danger"
            :disabled="!canSubmit"
            data-testid="pre-auth-submit"
            @click="submit"
          >
            {{ submitting ? t('common.loading', 'Submitting…') : t('governance.preAuthorizeConfirm', 'Grant pre-authorization') }}
          </button>
        </div>
      </template>

      <template v-else>
        <p class="detail-dialog__summary" data-testid="pre-auth-success">
          {{ t('governance.preAuthorizeGranted', 'Pre-authorization granted.') }}
        </p>
        <div class="approval-row" data-testid="pre-auth-record">
          <div>
            <strong>{{ created.id }}</strong>
            <p>{{ created.tool_scope.join(', ') }}</p>
            <p class="quiet">
              risk_max: {{ created.risk_max }} · max_uses: {{ created.max_uses }} ·
              use_count: {{ created.use_count }}
            </p>
          </div>
        </div>
        <div class="detail-dialog__actions">
          <button type="button" class="detail-dialog__btn" data-testid="pre-auth-done" @click="cancel">
            {{ t('common.close', 'Close') }}
          </button>
        </div>
      </template>
    </dialog>
  </Teleport>
</template>

<style scoped>
.pre-auth-dialog {
  min-width: 440px;
  max-width: 560px;
}

.pre-auth-form {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin: 12px 0;
}

.pre-auth-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
}

.pre-auth-error {
  margin: 0;
  color: var(--accent-danger);
  font-size: 12px;
}
</style>
