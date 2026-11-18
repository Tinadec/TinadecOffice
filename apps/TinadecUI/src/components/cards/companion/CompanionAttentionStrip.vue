<script setup lang="ts">
/**
 * The band above the buddy timeline: everything that is waiting on the user, in one place.
 *
 * It draws nothing at all when there is nothing to answer. An empty band with a "no items" line
 * would take the same height every day and teach the reader to ignore it — which is exactly the
 * failure mode a "needs you" surface must not have.
 */
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, ShieldCheck, UserPlus, type LucideIcon } from '@lucide/vue'
import { companionApi, type CompanionActivity } from '@/lib/companionApi'
import { activity as activityRows, refreshCompanionCenter } from '@/lib/companionStore'
import { needsAttention } from '@/lib/companionView'

const { t } = useI18n()

const deciding = ref<string | null>(null)
const decideError = ref('')

/** Newest first, capped: this is a prompt to act, not the archive. */
const items = computed(() => activityRows.value
  .filter(needsAttention)
  .sort((left, right) => Date.parse(right.created_at) - Date.parse(left.created_at))
  .slice(0, 5))

interface Action {
  kind: 'mandate' | 'recruit'
  id: string
  revision: number
}

/** The proposal row carries the id and revision it belongs to, so no extra lookup is needed. */
function actionOf(row: CompanionActivity): Action | null {
  const id = row.payload?.mandate_id ?? row.payload?.recruitment_id
  const revision = row.payload?.revision
  if (typeof id !== 'string' || typeof revision !== 'number') return null
  if (row.kind === 'autonomy_proposed') return { kind: 'mandate', id, revision }
  if (row.kind === 'autonomy_recruit_proposed') return { kind: 'recruit', id, revision }
  return null
}

function iconOf(row: CompanionActivity): LucideIcon {
  if (row.kind === 'autonomy_recruit_proposed') return UserPlus
  if (row.kind === 'autonomy_proposed') return ShieldCheck
  return AlertTriangle
}

function textOf(row: CompanionActivity): string {
  if (row.kind === 'autonomy_proposed') return t('companion.center.eventAutonomyProposed', { who: row.companion_name || '' })
  if (row.kind === 'autonomy_recruit_proposed') {
    const name = typeof row.payload?.name === 'string' ? row.payload.name : ''
    return t('companion.center.eventRecruitProposed', { who: row.companion_name || '', name })
  }
  return row.summary || row.detail || row.kind
}

async function decide(row: CompanionActivity, decision: 'approved' | 'denied'): Promise<void> {
  const action = actionOf(row)
  if (!action || !row.companion_id || deciding.value) return
  deciding.value = action.id
  decideError.value = ''
  try {
    if (action.kind === 'mandate') {
      await companionApi.decideMandate(row.companion_id, action.id, action.revision, decision)
    } else {
      await companionApi.decideRecruitment(action.id, action.revision, decision)
    }
    await refreshCompanionCenter()
  } catch {
    // A stale revision means somebody decided it elsewhere; the refresh shows the truth.
    decideError.value = t('companion.center.mandateDecideFailed')
    await refreshCompanionCenter()
  } finally {
    deciding.value = null
  }
}
</script>

<template>
  <div v-if="items.length" class="companion-attention-strip" role="region" :aria-label="t('companion.center.attentionTitle')">
    <ul class="companion-attention-list">
      <li v-for="row in items" :key="row.id" class="companion-attention-item">
        <component :is="iconOf(row)" :size="13" class="companion-attention-icon" aria-hidden="true" />
        <span class="companion-attention-text">{{ textOf(row) }}</span>
        <span v-if="actionOf(row)" class="companion-attention-actions">
          <button
            type="button"
            class="companion-approve"
            :disabled="deciding === actionOf(row)!.id"
            :data-testid="`attention-approve-${actionOf(row)!.id}`"
            @click="decide(row, 'approved')"
          >{{ t('companion.center.mandateApprove') }}</button>
          <button
            type="button"
            class="companion-deny"
            :disabled="deciding === actionOf(row)!.id"
            :data-testid="`attention-deny-${actionOf(row)!.id}`"
            @click="decide(row, 'denied')"
          >{{ t('companion.center.mandateDeny') }}</button>
        </span>
      </li>
    </ul>
    <p v-if="decideError" class="companion-attention-error" role="alert">{{ decideError }}</p>
  </div>
</template>

<style scoped>
.companion-attention-strip {
  padding: 8px 12px;
  border-bottom: 1px solid var(--border-muted);
  background: color-mix(in oklab, #d29922 9%, transparent);
}

.companion-attention-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.companion-attention-item {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--text-primary);
}

.companion-attention-icon {
  flex: none;
  color: #d29922;
}

.companion-attention-text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-attention-actions {
  display: flex;
  flex: none;
  gap: 6px;
}

.companion-attention-actions button {
  padding: 2px 9px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  font-size: 11.5px;
  color: var(--text-primary);
  background: var(--surface-raised);
  cursor: pointer;
}

.companion-attention-actions button:disabled {
  opacity: 0.55;
  cursor: default;
}

.companion-approve {
  border-color: color-mix(in oklab, #3fb950 55%, var(--border-muted));
}

.companion-deny {
  border-color: color-mix(in oklab, #f85149 45%, var(--border-muted));
}

.companion-attention-error {
  margin: 6px 0 0;
  font-size: 11.5px;
  color: #f85149;
}
</style>
