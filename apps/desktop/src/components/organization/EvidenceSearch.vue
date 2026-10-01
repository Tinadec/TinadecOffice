<script setup lang="ts">
/**
 * Search a session's evidence archive: every finished task's result, every governance report, what
 * standing members concluded and the context summaries — verbatim, never compressed. The same recall
 * governance roles use (`recall_evidence`); Core decides what matches. Semantic when an embedding
 * model is configured, keyword otherwise, and the answer says which.
 */
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Search } from '@lucide/vue'
import { UiButton, UiInput } from '@/components/ui'
import { api, type EvidenceRecallDto } from '@/api'

const props = defineProps<{ sessionId?: string | null }>()
const { t, te, locale } = useI18n()

const KINDS = ['task_result', 'report', 'member_turn', 'summary'] as const
const query = ref('')
const kinds = ref<string[]>([])
const result = ref<EvidenceRecallDto | null>(null)
const searching = ref(false)
const failure = ref<string | null>(null)
let generation = 0

// A different session is a different archive: drop what was found and any answer still on its way.
watch(() => props.sessionId, () => {
  generation++
  result.value = null
  failure.value = null
  searching.value = false
})

async function search() {
  const id = props.sessionId
  const text = query.value.trim()
  if (!id || !text) return
  const asked = ++generation
  searching.value = true
  failure.value = null
  try {
    const answer = await api.recallEvidence(id, text, { kinds: kinds.value.length ? kinds.value : undefined, limit: 20 })
    if (asked !== generation) return
    result.value = answer
  } catch (cause) {
    if (asked !== generation) return
    failure.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    if (asked === generation) searching.value = false
  }
}

function toggleKind(kind: string) {
  kinds.value = kinds.value.includes(kind) ? kinds.value.filter((item) => item !== kind) : [...kinds.value, kind]
}

const label = (key: string, fallback: string) => (te(key) ? t(key) : fallback)
const modeLabel = computed(() => result.value
  ? label(`organization.evidenceSearch.mode.${result.value.mode}`, result.value.mode)
  : '')
const formatTime = (value: string) => new Date(value).toLocaleString(locale.value, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
</script>

<template>
  <div class="org-body evidence-search" role="tabpanel" data-testid="organization-evidence">
    <form class="evidence-form" @submit.prevent="search">
      <UiInput v-model="query" :placeholder="t('organization.evidenceSearch.placeholder')" data-testid="evidence-query" />
      <UiButton type="submit" size="sm" :disabled="searching || !query.trim() || !sessionId" data-testid="evidence-search">
        <Search :size="14" />{{ t('organization.evidenceSearch.search') }}
      </UiButton>
    </form>
    <div class="evidence-kinds" role="group" :aria-label="t('organization.evidenceSearch.kindsLabel')">
      <button
        v-for="kind in KINDS"
        :key="kind"
        type="button"
        class="org-tag"
        :class="{ active: kinds.includes(kind) }"
        :aria-pressed="kinds.includes(kind)"
        :data-testid="`evidence-kind-${kind}`"
        @click="toggleKind(kind)"
      >{{ t(`organization.evidenceSearch.kinds.${kind}`) }}</button>
    </div>

    <p v-if="failure" class="org-notice" role="alert">{{ failure }}</p>
    <p v-else-if="!result" class="org-empty-line">{{ t('organization.evidenceSearch.hint') }}</p>
    <template v-else>
      <p class="org-muted" data-testid="evidence-mode">
        {{ modeLabel }}<template v-if="result.note"> · {{ result.note }}</template>
      </p>
      <p v-if="!result.hits.length" class="org-empty-line">{{ t('organization.evidenceSearch.noHits') }}</p>
      <ul v-else class="evidence-hits">
        <li v-for="hit in result.hits" :key="hit.evidence_id" class="evidence-hit" :data-evidence-id="hit.evidence_id">
          <div class="evidence-head">
            <span class="org-tag">{{ label(`organization.evidenceSearch.kinds.${hit.kind}`, hit.kind) }}</span>
            <strong>{{ hit.title }}</strong>
            <span v-if="hit.author" class="org-muted">{{ hit.author }}</span>
            <span class="org-muted evidence-when">{{ formatTime(hit.created_at) }}</span>
          </div>
          <p class="message-content">{{ hit.snippet }}</p>
          <span class="org-muted">{{ label(`organization.evidenceSearch.matchedBy.${hit.matched_by}`, hit.matched_by) }}</span>
        </li>
      </ul>
    </template>
  </div>
</template>

<style scoped>
.evidence-form { display:flex; gap:6px; align-items:center; }
.evidence-form :deep(input) { flex:1; min-width:0; }
.evidence-kinds { display:flex; flex-wrap:wrap; gap:4px; margin:8px 0; }
.evidence-kinds button { cursor:pointer; border:1px solid var(--border-muted); background:transparent; }
.evidence-kinds button.active { background:var(--surface-active); color:var(--text-primary); }
.evidence-hits { list-style:none; margin:0; padding:0; display:flex; flex-direction:column; gap:8px; }
.evidence-hit { padding:8px; border-radius:6px; background:var(--surface-section); }
.evidence-head { display:flex; flex-wrap:wrap; align-items:baseline; gap:6px; }
.evidence-when { margin-left:auto; }
.evidence-hit .message-content { white-space:pre-wrap; overflow-wrap:anywhere; margin:4px 0; font-size:12px; }
</style>
