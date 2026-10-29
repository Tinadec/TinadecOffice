<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { Pause, Play, RefreshCw, Settings2 } from '@lucide/vue'
import {
  companionApi,
  type CompanionSummary,
} from '@/lib/companionApi'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'
import { requestCompanion } from '@/lib/pageRequests'
import {
  activity as activityRows,
  activeCompanions,
  failures,
  loading,
  refreshCompanionCenter,
  selectCompanion,
  selectedCompanionId,
  useCompanionCenter,
} from '@/lib/companionStore'
import { attentionCounts } from '@/lib/companionView'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { companionErrorText, formatCompanionTime } from './companionText'

const { t, locale } = useI18n()
const router = useRouter()
const store = useCompanionCenter()

const companions = computed(() => activeCompanions.value)
const selected = computed(() => companions.value.find((item) => item.id === selectedCompanionId.value) ?? null)
const failure = computed(() => (failures.value.companions ? companionErrorText(failures.value.companions, t) : ''))
const attention = computed(() => attentionCounts(activityRows.value))
const pausing = ref<string | null>(null)
const pauseError = ref('')

/** Core's vocabulary is exactly three states; anything else must not be dressed up as one. */
function statusKey(summary: CompanionSummary): 'idle' | 'thinking' | 'paused' | 'unknown' {
  const activity = summary.current_activity as string
  if (activity === 'idle' || activity === 'thinking' || activity === 'paused') return activity
  return 'unknown'
}

function statusLabel(summary: CompanionSummary): string {
  const key = statusKey(summary)
  if (key === 'idle') return t('companion.center.statusIdle')
  if (key === 'thinking') return t('companion.center.statusThinking')
  if (key === 'paused') return t('companion.center.statusPaused')
  return t('companion.center.statusUnknown')
}

function autonomyLabel(summary: CompanionSummary): string {
  if (!summary.autonomy_enabled) return t('companion.center.autonomyOff')
  if (summary.autonomy_paused) return t('companion.center.autonomyPaused')
  return t('companion.center.autonomyOn')
}

function lastActivityLabel(summary: CompanionSummary): string {
  const time = formatCompanionTime(summary.last_activity_at, locale.value)
  return time ? t('companion.center.lastActivityAt', { time }) : t('companion.center.neverActivity')
}

/** The editor lives in Settings; a deep link opens it on this companion. */
function openInSettings(): void {
  if (!selected.value) return
  requestCompanion(selected.value.id, 'identity')
  void router.push('/settings')
}

function openMemory(): void {
  if (!selected.value) return
  requestCompanion(selected.value.id, 'memory')
  void router.push('/settings')
}

function openSkills(): void {
  if (!selected.value) return
  requestCompanion(selected.value.id, 'skills')
  void router.push('/settings')
}

function openConnections(): void {
  if (!selected.value) return
  requestCompanion(selected.value.id, 'connections')
  void router.push('/settings')
}

/**
 * Pausing autonomy is a write with a revision, so the current rules are read first: a PATCH that
 * dropped interval_minutes or daily_action_budget would silently reset them.
 */
async function toggleAutonomy(): Promise<void> {
  const target = selected.value
  if (!target || pausing.value) return
  pausing.value = target.id
  pauseError.value = ''
  try {
    const detail = await companionApi.getCompanion(target.id)
    await companionApi.patchCompanion(target.id, detail.revision, {
      autonomy: {
        enabled: detail.autonomy.enabled,
        paused: !detail.autonomy.paused,
        interval_minutes: detail.autonomy.interval_minutes,
        daily_action_budget: detail.autonomy.daily_action_budget,
      },
    })
    await refreshCompanionCenter()
  } catch (error) {
    pauseError.value = isCompanionRevisionConflict(error)
      ? t('companion.center.pauseConflict')
      : companionErrorText(error, t)
  } finally {
    pausing.value = null
  }
}
</script>

<template>
  <section class="companion-card">
    <header class="companion-card-head">
      <h2>{{ t('companion.center.listTitle') }}</h2>
      <button
        type="button"
        class="companion-card-refresh"
        :title="t('companion.center.retry')"
        :aria-label="t('companion.center.retry')"
        @click="store.refresh"
      >
        <RefreshCw :size="13" />
      </button>
    </header>

    <p v-if="loading" class="companion-card-note">{{ t('companion.center.loading') }}</p>

    <div v-else-if="failure" class="companion-card-error" role="alert">
      <span>{{ failure }}</span>
      <button type="button" @click="store.refresh">{{ t('companion.center.retry') }}</button>
    </div>

    <p v-else-if="companions.length === 0" class="companion-card-note">{{ t('companion.center.listEmpty') }}</p>

    <div v-else class="companion-list" role="list">
      <button
        v-for="summary in companions"
        :key="summary.id"
        type="button"
        role="listitem"
        class="companion-row"
        :class="{ selected: summary.id === selectedCompanionId }"
        :aria-pressed="summary.id === selectedCompanionId"
        :data-testid="`companion-row-${summary.id}`"
        @click="selectCompanion(summary.id)"
      >
        <AgentPetAvatar class="companion-avatar" :pet-slug="summary.avatar ?? ''" :size="28" :alt="summary.name" />
        <span class="companion-row-body">
          <span class="companion-row-name">{{ summary.name }}</span>
          <span class="companion-row-meta">
            <span class="companion-dot" :class="`companion-dot-${statusKey(summary)}`" aria-hidden="true" />
            {{ statusLabel(summary) }}
          </span>
        </span>
        <span
          v-if="attention.get(summary.id)"
          class="companion-row-attention"
          :title="t('companion.center.needsYouCount', { count: attention.get(summary.id) })"
        >{{ attention.get(summary.id) }}</span>
      </button>
    </div>

    <div v-if="selected" class="companion-detail">
      <div class="companion-detail-row">
        <span>{{ t('companion.center.activityLabel') }}</span>
        <strong>{{ statusLabel(selected) }}</strong>
      </div>
      <div class="companion-detail-row">
        <span>{{ t('companion.center.autonomyLabel') }}</span>
        <strong>{{ autonomyLabel(selected) }}</strong>
      </div>
      <p v-if="selected.tagline" class="companion-detail-tagline">{{ selected.tagline }}</p>
      <p class="companion-detail-meta">{{ lastActivityLabel(selected) }}</p>
      <div class="companion-detail-counts">
        <span>{{ t('companion.center.memoryCount', { count: selected.memory_count }) }}</span>
        <span>{{ t('companion.center.skillCount', { count: selected.skill_count }) }}</span>
        <span>{{ t('companion.center.connectionCount', { count: selected.connection_count }) }}</span>
      </div>
      <p v-if="pauseError" class="companion-detail-error" role="alert">{{ pauseError }}</p>
      <div class="companion-detail-actions">
        <button type="button" class="companion-detail-action" :data-testid="`companion-edit-${selected.id}`" @click="openInSettings">
          <Settings2 :size="13" />
          <span>{{ t('companion.center.openDocument') }}</span>
        </button>
        <button
          type="button"
          class="companion-detail-action"
          :disabled="pausing === selected.id || !selected.autonomy_enabled"
          :title="!selected.autonomy_enabled ? t('companion.center.autonomyOffHint') : ''"
          @click="toggleAutonomy"
        >
          <Play v-if="selected.autonomy_paused" :size="13" />
          <Pause v-else :size="13" />
          <span>{{ selected.autonomy_paused ? t('companion.center.resumeAutonomy') : t('companion.center.pauseAutonomy') }}</span>
        </button>
      </div>
      <div class="companion-detail-links">
        <button type="button" @click="openMemory">{{ t('companion.center.openMemory') }}</button>
        <button type="button" @click="openSkills">{{ t('companion.center.openSkills') }}</button>
        <button type="button" @click="openConnections">{{ t('companion.center.openConnections') }}</button>
      </div>
      <p class="companion-detail-hint">{{ t('companion.center.openWindowHint') }}</p>
    </div>
  </section>
</template>

<style scoped>
.companion-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  height: 100%;
  overflow-y: auto;
  padding: 14px;
}

.companion-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.companion-card-head h2 {
  margin: 0;
  font-size: 13px;
  font-weight: 700;
  color: var(--text-primary);
}

.companion-card-refresh {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.companion-card-refresh:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.companion-card-note {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-muted);
}

.companion-card-error {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-secondary);
}

.companion-card-error button {
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary);
  padding: 3px 10px;
  font-size: 11px;
  cursor: pointer;
}

.companion-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.companion-row {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  padding: 8px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  text-align: left;
  cursor: pointer;
  color: var(--text-primary);
}

.companion-row:hover {
  background: var(--surface-hover);
}

.companion-row.selected {
  border-color: var(--border-card);
  background: var(--surface-selected);
}

.companion-avatar {
  flex: none;
  border-radius: 8px;
}

.companion-row-body {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
  flex: 1;
}

.companion-row-name {
  font-size: 12.5px;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-row-meta {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 11px;
  color: var(--text-muted);
}

.companion-dot {
  width: 6px;
  height: 6px;
  flex: none;
  border-radius: 999px;
  background: var(--text-muted);
}

.companion-dot-idle {
  background: #3fb950;
}

.companion-dot-thinking {
  background: #58a6ff;
}

.companion-dot-paused {
  background: #6e7681;
}

.companion-dot-unknown {
  background: #d29922;
}

.companion-row-attention {
  flex: none;
  min-width: 18px;
  padding: 0 5px;
  border-radius: 999px;
  background: rgba(248, 81, 73, 0.18);
  color: #f09595;
  font-size: 10.5px;
  line-height: 18px;
  text-align: center;
}

.companion-detail {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 4px;
  padding: 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-raised);
}

.companion-detail-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-size: 11.5px;
  color: var(--text-muted);
}

.companion-detail-row strong {
  font-weight: 600;
  color: var(--text-primary);
}

.companion-detail-tagline,
.companion-detail-meta,
.companion-detail-hint {
  margin: 0;
  font-size: 11px;
  line-height: 1.45;
  color: var(--text-muted);
}

.companion-detail-hint {
  padding-top: 6px;
  border-top: 1px solid var(--border-muted);
}

.companion-detail-error {
  margin: 0;
  font-size: 11px;
  line-height: 1.45;
  color: #f09595;
}

.companion-detail-counts {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 10px;
  font-size: 11px;
  color: var(--text-secondary);
}

.companion-detail-actions {
  display: flex;
  gap: 6px;
}

.companion-detail-action {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  flex: 1;
  padding: 6px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: transparent;
  color: var(--text-primary);
  font-size: 11.5px;
  cursor: pointer;
}

.companion-detail-action:hover:not(:disabled) {
  background: var(--surface-hover);
}

.companion-detail-action:disabled {
  opacity: 0.5;
  cursor: default;
}

.companion-detail-links {
  display: flex;
  gap: 6px;
}

.companion-detail-links button {
  flex: 1;
  padding: 4px 6px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.companion-detail-links button:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}
</style>
