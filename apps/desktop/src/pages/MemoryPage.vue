<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { BookMarked, Inbox, Lightbulb } from '@lucide/vue'
import { api, type MemoryCandidateDto, type MemoryItemDto } from '@/api'
import CommandPaletteButton from '@/components/CommandPaletteButton.vue'

/**
 * The memory review surface: what the experience curator proposed, what the workspace
 * decided, and what the agents currently remember.
 *
 * Two shelves, never merged. A candidate is not memory yet — it is a claim about this
 * workspace with a run behind it — and promoted memory is not a proposal: it is already
 * reaching the model. Mixing them would let one click mean two things.
 *
 * Filters are the queue's own, and Core refuses a value it cannot honour, so an empty
 * shelf here really does mean nothing is on it.
 */
const { t } = useI18n()

/** One page of each shelf. A full page is reported as a page, not as the whole queue. */
const PAGE = 100

const candidates = ref<MemoryCandidateDto[]>([])
const items = ref<MemoryItemDto[]>([])
const loading = ref(false)
const loadError = ref<string | null>(null)
const deciding = ref<string | null>(null)
const decisionError = ref<string | null>(null)
const status = ref('proposed')
const scope = ref('')
const showHistory = ref(false)

const CANDIDATE_STATUSES = ['proposed', 'promoted', 'rejected'] as const
const SCOPES = ['workspace', 'principal', 'project', 'agent'] as const

const activeItems = computed(() => (showHistory.value ? items.value.filter((x) => x.status === 'revoked') : items.value))
const candidatesTruncated = computed(() => candidates.value.length >= PAGE)
const itemsTruncated = computed(() => items.value.length >= PAGE)

function errorMessage(e: unknown): string {
  return e instanceof Error ? e.message : String(e)
}

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    const [queue, shelf] = await Promise.all([
      api.listMemoryCandidates({ status: status.value, scope: scope.value || undefined, limit: PAGE }),
      api.listMemoryItems({ status: showHistory.value ? 'revoked' : 'active', scope: scope.value || undefined, limit: PAGE }),
    ])
    candidates.value = queue
    items.value = shelf
  } catch (e) {
    loadError.value = errorMessage(e)
    candidates.value = []
    items.value = []
  } finally {
    loading.value = false
  }
}

async function decide(candidate: MemoryCandidateDto, promote: boolean): Promise<void> {
  deciding.value = candidate.id
  decisionError.value = null
  try {
    const next = promote
      ? await api.promoteMemoryCandidate(candidate.id)
      : await api.rejectMemoryCandidate(candidate.id)
    // The decided row leaves the shelf it was answered from rather than being dropped:
    // a queue filtered on "proposed" no longer holds it, and its new status is the answer.
    if (status.value === next.status) candidates.value = candidates.value.map((x) => (x.id === next.id ? next : x))
    else candidates.value = candidates.value.filter((x) => x.id !== candidate.id)
    if (promote) await load()
  } catch (e) {
    const code = (e as { code?: string | null }).code
    decisionError.value = code === 'conflict' ? t('memory.alreadyDecided', 'Someone already answered this one.') : errorMessage(e)
  } finally {
    deciding.value = null
  }
}

async function revoke(item: MemoryItemDto): Promise<void> {
  deciding.value = item.id
  decisionError.value = null
  try {
    await api.revokeMemoryItem(item.id)
    await load()
  } catch (e) {
    decisionError.value = errorMessage(e)
  } finally {
    deciding.value = null
  }
}

function confidencePercent(value: unknown): string {
  // A confidence that is not a number renders as unknown, never as "NaN%".
  const numeric = Number(value)
  if (!Number.isFinite(numeric)) return t('memory.confidenceUnknown', 'no score')
  return `${Math.round(Math.max(0, Math.min(1, numeric)) * 100)}%`
}

onMounted(() => {
  void load()
})
</script>

<template>
  <div class="memory-page" data-testid="memory-page">
    <header class="memory-page__header">
      <h1><BookMarked class="size-4" /> {{ t('memory.title', 'Memory review') }}</h1>
      <div class="memory-page__filters" role="group" :aria-label="t('memory.filters', 'Filters')">
        <label class="memory-page__filter">
          <span>{{ t('memory.queueStatus', 'Queue') }}</span>
          <select v-model="status" data-testid="memory-status" @change="void load()">
            <option v-for="value in CANDIDATE_STATUSES" :key="value" :value="value">
              {{ t(`memory.status.${value}`, value) }}
            </option>
          </select>
        </label>
        <label class="memory-page__filter">
          <span>{{ t('memory.scope', 'Scope') }}</span>
          <select v-model="scope" data-testid="memory-scope" @change="void load()">
            <option value="">{{ t('memory.scope.all', 'Every scope') }}</option>
            <option v-for="value in SCOPES" :key="value" :value="value">
              {{ t(`memory.scope.${value}`, value) }}
            </option>
          </select>
        </label>
        <CommandPaletteButton />
        <button type="button" class="memory-page__button" :disabled="loading" data-testid="memory-refresh" @click="void load()">
          {{ loading ? t('common.loading', 'Loading…') : t('common.refresh', 'Refresh') }}
        </button>
      </div>
    </header>

    <p v-if="loadError" class="memory-page__error" data-testid="memory-load-error">{{ loadError }}</p>
    <p v-if="decisionError" class="memory-page__error" data-testid="memory-decision-error">{{ decisionError }}</p>

    <section class="memory-page__section" aria-labelledby="memory-queue-heading">
      <h2 id="memory-queue-heading">
        <Inbox class="size-4" /> {{ t('memory.queueTitle', 'Awaiting review') }}
        <span class="memory-page__count">{{ candidates.length }}</span>
      </h2>
      <!-- A shelf that failed to answer is not a shelf that answered "nothing", so the
           empty line waits until the request actually came back. -->
      <p v-if="!candidates.length && !loading && !loadError" class="memory-page__empty" data-testid="memory-queue-empty">
        {{ t('memory.queueEmpty', 'Nothing is waiting for you in this scope.') }}
      </p>
      <article v-for="candidate in candidates" :key="candidate.id" class="memory-card" data-testid="memory-candidate">
        <p class="memory-card__content">{{ candidate.content }}</p>
        <p class="memory-card__meta">
          <span class="memory-card__chip">{{ candidate.kind }}</span>
          <span class="memory-card__chip">{{ t(`memory.scope.${candidate.scope}`, candidate.scope) }}</span>
          <span class="memory-card__chip">{{ t('memory.confidence', 'confidence') }} {{ confidencePercent(candidate.confidence) }}</span>
        </p>
        <dl v-if="candidate.applicability || candidate.expiry_condition || candidate.evidence" class="memory-card__grounds">
          <template v-if="candidate.evidence">
            <dt><Lightbulb class="size-3" /> {{ t('memory.evidence', 'Grounded in') }}</dt>
            <dd>{{ candidate.evidence }}</dd>
          </template>
          <template v-if="candidate.applicability">
            <dt>{{ t('memory.applicability', 'Applies while') }}</dt>
            <dd>{{ candidate.applicability }}</dd>
          </template>
          <template v-if="candidate.expiry_condition">
            <dt>{{ t('memory.expiry', 'Stops being true when') }}</dt>
            <dd>{{ candidate.expiry_condition }}</dd>
          </template>
        </dl>
        <div v-if="candidate.status === 'proposed'" class="memory-card__actions">
          <button
            type="button"
            class="memory-page__button memory-page__button--primary"
            :disabled="deciding === candidate.id"
            :data-testid="`memory-promote-${candidate.id}`"
            @click="decide(candidate, true)"
          >
            {{ t('memory.promote', 'Remember this') }}
          </button>
          <button
            type="button"
            class="memory-page__button"
            :disabled="deciding === candidate.id"
            :data-testid="`memory-reject-${candidate.id}`"
            @click="decide(candidate, false)"
          >
            {{ t('memory.reject', 'Discard') }}
          </button>
        </div>
        <p v-else class="memory-card__decided">
          {{ t(`memory.status.${candidate.status}`, candidate.status) }}
          <span v-if="candidate.decision_reason"> · {{ candidate.decision_reason }}</span>
        </p>
      </article>
      <p v-if="candidatesTruncated" class="memory-page__paged">{{ t('memory.queueTruncated', 'Showing the first 100 — there may be more.') }}</p>
    </section>

    <section class="memory-page__section" aria-labelledby="memory-shelf-heading">
      <h2 id="memory-shelf-heading">
        <BookMarked class="size-4" />
        {{ showHistory ? t('memory.historyTitle', 'Forgotten') : t('memory.shelfTitle', 'What the agents remember') }}
        <span class="memory-page__count">{{ activeItems.length }}</span>
      </h2>
      <button type="button" class="memory-page__link" data-testid="memory-toggle-history" @click="showHistory = !showHistory; void load()">
        {{ showHistory ? t('memory.showActive', 'Show active memory') : t('memory.showHistory', 'Show what was forgotten') }}
      </button>
      <p v-if="!activeItems.length && !loading && !loadError" class="memory-page__empty" data-testid="memory-shelf-empty">
        {{ showHistory ? t('memory.historyEmpty', 'Nothing has been revoked.') : t('memory.shelfEmpty', 'No promoted memory in this scope.') }}
      </p>
      <article v-for="item in activeItems" :key="item.id" class="memory-card" data-testid="memory-item">
        <p class="memory-card__content">{{ item.content }}</p>
        <p class="memory-card__meta">
          <span class="memory-card__chip">{{ item.kind }}</span>
          <span class="memory-card__chip">{{ t(`memory.scope.${item.scope}`, item.scope) }}</span>
          <span class="memory-card__chip">v{{ item.version }}</span>
        </p>
        <p v-if="item.applicability || item.expiry_condition" class="memory-card__meta">
          <span v-if="item.applicability">{{ t('memory.applicability', 'Applies while') }} {{ item.applicability }}</span>
          <span v-if="item.expiry_condition"> · {{ t('memory.expiry', 'Stops being true when') }} {{ item.expiry_condition }}</span>
        </p>
        <div class="memory-card__actions">
          <button
            type="button"
            class="memory-page__button memory-page__button--danger"
            :disabled="deciding === item.id || showHistory"
            :data-testid="`memory-revoke-${item.id}`"
            @click="revoke(item)"
          >
            {{ t('memory.revoke', 'Forget this') }}
          </button>
        </div>
      </article>
      <p v-if="itemsTruncated" class="memory-page__paged">{{ t('memory.shelfTruncated', 'Showing the first 100 — there may be more.') }}</p>
    </section>
  </div>
</template>

<style scoped>
.memory-page {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 24px 20px;
  max-width: 980px;
  margin: 0 auto;
}

.memory-page__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.memory-page__header h1 {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 17px;
  font-weight: 700;
  color: var(--text-primary);
}

.memory-page__filters {
  display: flex;
  align-items: center;
  gap: 10px;
}

.memory-page__filter {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-secondary);
}

.memory-page__filter select {
  font-size: 12px;
  padding: 4px 6px;
  border-radius: 6px;
  background: var(--surface-raised);
  color: var(--text-primary);
}

.memory-page__button {
  font-size: 12px;
  padding: 5px 10px;
  border-radius: 7px;
  background: var(--surface-raised);
  color: var(--text-primary);
  border: 1px solid var(--border-subtle);
  cursor: pointer;
}

.memory-page__button:disabled {
  opacity: 0.55;
  cursor: default;
}

.memory-page__button--primary {
  background: var(--bg-status-primary);
}

.memory-page__button--danger {
  color: var(--accent-danger);
}

.memory-page__link {
  align-self: flex-start;
  font-size: 12px;
  color: var(--accent-primary);
  background: none;
  cursor: pointer;
}

.memory-page__error {
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--bg-status-danger);
  color: var(--accent-danger);
  font-size: 13px;
}

.memory-page__section {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border-radius: 10px;
  background: var(--surface-section);
}

.memory-page__section h2 {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  font-weight: 700;
  color: var(--text-primary);
}

.memory-page__count {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-secondary);
}

.memory-page__empty,
.memory-page__paged {
  font-size: 12px;
  color: var(--text-secondary);
}

.memory-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--surface-raised);
}

.memory-card__content {
  font-size: 13px;
  color: var(--text-primary);
  white-space: pre-wrap;
}

.memory-card__meta {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  font-size: 11px;
  color: var(--text-secondary);
}

.memory-card__chip {
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--surface-section);
}

.memory-card__grounds {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 2px 8px;
  font-size: 11px;
  color: var(--text-secondary);
}

.memory-card__grounds dt {
  display: flex;
  align-items: center;
  gap: 4px;
  font-weight: 600;
}

.memory-card__decided {
  font-size: 11px;
  color: var(--text-secondary);
}

.memory-card__actions {
  display: flex;
  gap: 8px;
}
</style>
