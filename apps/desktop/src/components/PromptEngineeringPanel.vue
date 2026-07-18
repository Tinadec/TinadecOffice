<script setup lang="ts">
import {
  Activity,
  ArrowLeftRight,
  FileText,
  GitBranch,
  History,
  Plus,
  RefreshCw,
  ThumbsDown,
  ThumbsUp,
  Undo2
} from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  api,
  type PromptFragmentAbTestResultDto,
  type PromptFragmentDto,
  type PromptFragmentEffectivenessDto,
  type PromptFragmentVersionDto
} from '../api'
import { UiBadge, UiButton, UiCard, UiInput, UiLabel } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'

const { t } = useI18n()
const { notify, confirm } = useNotifications()

const fragments = ref<PromptFragmentDto[]>([])
const effectivenessList = ref<PromptFragmentEffectivenessDto[]>([])
const selectedFragmentId = ref('')
const versions = ref<PromptFragmentVersionDto[]>([])
const effectiveness = ref<PromptFragmentEffectivenessDto | null>(null)
const loading = ref(false)
const busy = ref(false)
const error = ref<string | null>(null)

// New version form
const showNewVersion = ref(false)
const newVersionContent = ref('')
const newVersionChangedFields = ref<string>('')
const newVersionSummary = ref('')

// Signal form
const signalNote = ref('')
const signalVersion = ref<number | null>(null)

// Compare form
const compareVersionA = ref<number | null>(null)
const compareVersionB = ref<number | null>(null)
const compareResult = ref<PromptFragmentAbTestResultDto | null>(null)

const selectedFragment = computed(() =>
  fragments.value.find((f) => f.id === selectedFragmentId.value) ?? null
)

const selectedEffectiveness = computed(() =>
  effectivenessList.value.find((e) => e.fragment_id === selectedFragmentId.value) ?? null
)

const sortedVersions = computed(() =>
  [...versions.value].sort((a, b) => b.version - a.version)
)

const sortedEffectivenessList = computed(() =>
  [...effectivenessList.value].sort((a, b) => b.effectiveness_score - a.effectiveness_score)
)

function effectivenessVariant(score: number): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (score >= 0.7) return 'default'
  if (score >= 0.4) return 'outline'
  if (score > 0) return 'secondary'
  return 'destructive'
}

function formatDate(iso: string): string {
  try {
    return new Date(iso).toLocaleString()
  } catch {
    return iso
  }
}

async function loadAll() {
  loading.value = true
  error.value = null
  try {
    const [fragmentList, effList] = await Promise.all([
      api.listPromptFragments(),
      api.listAllPromptFragmentEffectiveness().catch(() => [] as PromptFragmentEffectivenessDto[])
    ])
    fragments.value = fragmentList
    effectivenessList.value = effList
    if (!selectedFragmentId.value && fragmentList.length > 0) {
      await selectFragment(fragmentList[0])
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
  }
}

async function selectFragment(fragment: PromptFragmentDto) {
  const fragmentId = fragment.id
  error.value = null
  selectedFragmentId.value = fragmentId
  versions.value = []
  effectiveness.value = null
  compareResult.value = null
  compareVersionA.value = null
  compareVersionB.value = null
  signalVersion.value = null
  try {
    const [versionList, eff] = await Promise.all([
      api.listPromptFragmentVersions(fragmentId),
      api.getPromptFragmentEffectiveness(fragmentId).catch(() => null)
    ])
    if (selectedFragmentId.value !== fragmentId) return
    versions.value = versionList
    effectiveness.value = eff
    if (versionList.length > 0) {
      compareVersionA.value = versionList[versionList.length - 1].version
      compareVersionB.value = versionList[0].version
    }
  } catch (err) {
    if (selectedFragmentId.value === fragmentId) {
      error.value = err instanceof Error ? err.message : String(err)
    }
  }
}

function openNewVersion() {
  showNewVersion.value = true
  newVersionContent.value = selectedFragment.value?.content ?? ''
  newVersionChangedFields.value = 'content'
  newVersionSummary.value = ''
}

async function createVersion() {
  if (!selectedFragment.value || !newVersionContent.value.trim()) return
  busy.value = true
  try {
    await api.createPromptFragmentVersion(selectedFragment.value.id, {
      content: newVersionContent.value,
      changed_fields: newVersionChangedFields.value
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean),
      change_summary: newVersionSummary.value || 'Updated content'
    })
    showNewVersion.value = false
    await selectFragment(selectedFragment.value)
    notify.success('Prompt version created.')
  } catch (err) {
    notify.error(err, { title: 'Could not create prompt version' })
  } finally {
    busy.value = false
  }
}

async function rollbackVersion(targetVersion: number) {
  const fragment = selectedFragment.value
  if (!fragment) return
  if (!await confirm({
    title: 'Rollback prompt fragment',
    message: `Rollback ${fragment.title} to v${targetVersion}?`,
    confirmLabel: 'Rollback',
    cancelLabel: 'Cancel',
    destructive: true
  })) return
  busy.value = true
  try {
    const updated = await api.rollbackPromptFragment(fragment.id, targetVersion)
    // Update local fragment list
    const idx = fragments.value.findIndex((f) => f.id === updated.id)
    if (idx >= 0) fragments.value[idx] = updated
    await selectFragment(updated)
    notify.success(`Rolled back to v${targetVersion}.`)
  } catch (err) {
    notify.error(err, { title: `Could not roll back to v${targetVersion}` })
  } finally {
    busy.value = false
  }
}

async function recordSignal(signal: 'positive' | 'negative') {
  const fragment = selectedFragment.value
  if (!fragment) return
  busy.value = true
  try {
    const result = await api.recordPromptFragmentSignal(fragment.id, {
      signal,
      note: signalNote.value || null,
      version: signalVersion.value ?? undefined
    })
    if (selectedFragmentId.value === fragment.id) effectiveness.value = result
    signalNote.value = ''
    // Refresh the global effectiveness list too
    effectivenessList.value = await api.listAllPromptFragmentEffectiveness().catch(() => effectivenessList.value)
    notify.success(`${signal === 'positive' ? 'Positive' : 'Negative'} signal recorded.`)
  } catch (err) {
    notify.error(err, { title: 'Could not record signal' })
  } finally {
    busy.value = false
  }
}

async function compareVersions() {
  const fragment = selectedFragment.value
  const versionA = compareVersionA.value
  const versionB = compareVersionB.value
  if (!fragment || versionA === null || versionB === null) return
  busy.value = true
  try {
    const result = await api.comparePromptFragmentVersions(fragment.id, versionA, versionB)
    if (selectedFragmentId.value === fragment.id && compareVersionA.value === versionA && compareVersionB.value === versionB) {
      compareResult.value = result
    }
  } catch (err) {
    notify.error(err, { title: 'Could not compare prompt versions' })
  } finally {
    busy.value = false
  }
}

onMounted(() => {
  void loadAll()
})
</script>

<template>
  <section class="prompt-engineering-panel">
    <div class="pe-header">
      <div>
        <h2><FileText :size="18" /> Prompt Engineering</h2>
        <p>Version snapshots, A/B testing, and effectiveness tracking for prompt fragments.</p>
      </div>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="loadAll">
        <RefreshCw :size="14" />
        <span>Refresh</span>
      </UiButton>
    </div>

    <div v-if="error" class="pe-error">
      <span>{{ error }}</span>
    </div>

    <div class="pe-layout">
      <!-- Fragment list -->
      <div class="pe-fragment-list">
        <div class="pe-list-header">
          <h3>Fragments</h3>
          <UiBadge variant="outline">{{ fragments.length }}</UiBadge>
        </div>
        <p v-if="loading" class="quiet">Loading…</p>
        <p v-else-if="fragments.length === 0" class="quiet">No fragments found.</p>
        <button
          v-for="fragment in fragments"
          :key="fragment.id"
          class="pe-fragment-card"
          :class="{ active: selectedFragmentId === fragment.id }"
          @click="selectFragment(fragment)"
        >
          <div class="pe-fragment-head">
            <strong>{{ fragment.title }}</strong>
            <UiBadge v-if="selectedEffectiveness && selectedEffectiveness.fragment_id === fragment.id" :variant="effectivenessVariant(selectedEffectiveness.effectiveness_score)">
              {{ (selectedEffectiveness.effectiveness_score * 100).toFixed(0) }}%
            </UiBadge>
          </div>
          <span class="pe-fragment-meta">
            {{ fragment.scope }} / {{ fragment.category }}
            <template v-if="fragment.is_builtin"> / built-in</template>
          </span>
        </button>
      </div>

      <!-- Detail panel -->
      <div class="pe-detail">
        <p v-if="!selectedFragment" class="quiet">Select a fragment to view versions and effectiveness.</p>
        <template v-else>
          <UiCard class="pe-detail-card">
            <template #content>
              <div class="pe-detail-head">
                <div>
                  <h3>{{ selectedFragment.title }}</h3>
                  <p>{{ selectedFragment.key }} · {{ selectedFragment.scope }} / {{ selectedFragment.category }}</p>
                </div>
                <UiBadge :variant="selectedFragment.enabled ? 'default' : 'secondary'">
                  {{ selectedFragment.enabled ? 'enabled' : 'disabled' }}
                </UiBadge>
              </div>

              <div v-if="effectiveness" class="pe-metrics">
                <div class="pe-metric">
                  <Activity :size="14" />
                  <div>
                    <span>Effectiveness</span>
                    <strong>{{ (effectiveness.effectiveness_score * 100).toFixed(0) }}%</strong>
                  </div>
                </div>
                <div class="pe-metric">
                  <History :size="14" />
                  <div>
                    <span>Active Version</span>
                    <strong>v{{ effectiveness.active_version }}</strong>
                  </div>
                </div>
                <div class="pe-metric">
                  <ThumbsUp :size="14" />
                  <div>
                    <span>Positive Signals</span>
                    <strong>{{ effectiveness.positive_signals }}</strong>
                  </div>
                </div>
                <div class="pe-metric">
                  <ThumbsDown :size="14" />
                  <div>
                    <span>Negative Signals</span>
                    <strong>{{ effectiveness.negative_signals }}</strong>
                  </div>
                </div>
                <div class="pe-metric">
                  <FileText :size="14" />
                  <div>
                    <span>Total Invocations</span>
                    <strong>{{ effectiveness.total_invocations }}</strong>
                  </div>
                </div>
              </div>

              <div class="pe-section">
                <div class="pe-section-head">
                  <div class="pe-section-title">
                    <GitBranch :size="14" />
                    <span>Current Content</span>
                  </div>
                  <UiButton size="sm" variant="outline" @click="openNewVersion">
                    <Plus :size="14" />
                    <span>New Version</span>
                  </UiButton>
                </div>
                <textarea
                  :value="selectedFragment.content"
                  class="pe-content-textarea"
                  rows="6"
                  readonly
                ></textarea>
              </div>
            </template>
          </UiCard>

          <!-- Versions list -->
          <UiCard class="pe-detail-card">
            <template #content>
              <div class="pe-section-head">
                <div class="pe-section-title">
                  <History :size="14" />
                  <span>Version History</span>
                </div>
                <UiBadge variant="outline">{{ versions.length }}</UiBadge>
              </div>
              <p v-if="versions.length === 0" class="quiet">No version snapshots yet. Create one to start tracking.</p>
              <div v-else class="pe-version-list">
                <div
                  v-for="version in sortedVersions"
                  :key="version.id"
                  class="pe-version-row"
                  :class="{ active: version.is_active }"
                >
                  <div class="pe-version-main">
                    <div class="pe-version-head">
                      <strong>v{{ version.version }}</strong>
                      <UiBadge v-if="version.is_active" variant="default">active</UiBadge>
                      <span class="pe-version-date">{{ formatDate(version.created_at) }}</span>
                    </div>
                    <p class="pe-version-summary">{{ version.change_summary }}</p>
                    <div v-if="version.changed_fields.length > 0" class="pe-version-fields">
                      <span v-for="field in version.changed_fields" :key="field" class="pe-version-field">{{ field }}</span>
                    </div>
                  </div>
                  <div class="pe-version-actions">
                    <UiButton
                      v-if="!version.is_active"
                      size="sm"
                      variant="ghost"
                      :disabled="busy"
                      :title="`Rollback to v${version.version}`"
                      @click="rollbackVersion(version.version)"
                    >
                      <Undo2 :size="14" />
                      <span>Rollback</span>
                    </UiButton>
                  </div>
                </div>
              </div>
            </template>
          </UiCard>

          <!-- Signal recording -->
          <UiCard class="pe-detail-card">
            <template #content>
              <div class="pe-section-title">
                <Activity :size="14" />
                <span>Record Signal</span>
              </div>
              <p class="pe-hint">Track effectiveness by recording positive/negative signals for this fragment.</p>
              <div class="pe-signal-form">
                <div class="pe-signal-field">
                  <UiLabel>Version (optional)</UiLabel>
                  <select v-model="signalVersion" class="pe-select">
                    <option :value="null">Active version</option>
                    <option v-for="version in versions" :key="version.id" :value="version.version">
                      v{{ version.version }}{{ version.is_active ? ' (active)' : '' }}
                    </option>
                  </select>
                </div>
                <div class="pe-signal-field pe-signal-note">
                  <UiLabel>Note (optional)</UiLabel>
                  <UiInput v-model="signalNote" placeholder="context for this signal" />
                </div>
              </div>
              <div class="pe-signal-actions">
                <UiButton variant="outline" size="sm" :disabled="busy" @click="recordSignal('positive')">
                  <ThumbsUp :size="14" />
                  <span>Positive</span>
                </UiButton>
                <UiButton variant="outline" size="sm" :disabled="busy" @click="recordSignal('negative')">
                  <ThumbsDown :size="14" />
                  <span>Negative</span>
                </UiButton>
              </div>
            </template>
          </UiCard>

          <!-- A/B Compare -->
          <UiCard v-if="versions.length >= 2" class="pe-detail-card">
            <template #content>
              <div class="pe-section-title">
                <ArrowLeftRight :size="14" />
                <span>A/B Compare</span>
              </div>
              <p class="pe-hint">Compare effectiveness scores between two versions.</p>
              <div class="pe-compare-form">
                <div class="pe-signal-field">
                  <UiLabel>Version A</UiLabel>
                  <select v-model="compareVersionA" class="pe-select">
                    <option v-for="version in versions" :key="version.id" :value="version.version">v{{ version.version }}</option>
                  </select>
                </div>
                <div class="pe-signal-field">
                  <UiLabel>Version B</UiLabel>
                  <select v-model="compareVersionB" class="pe-select">
                    <option v-for="version in versions" :key="version.id" :value="version.version">v{{ version.version }}</option>
                  </select>
                </div>
                <UiButton size="sm" :disabled="busy || compareVersionA === compareVersionB" @click="compareVersions">
                  <ArrowLeftRight :size="14" />
                  <span>Compare</span>
                </UiButton>
              </div>

              <div v-if="compareResult" class="pe-compare-result">
                <div class="pe-compare-grid">
                  <div class="pe-compare-cell">
                    <span>Version A (v{{ compareResult.version_a }})</span>
                    <strong>{{ (compareResult.score_a * 100).toFixed(0) }}%</strong>
                  </div>
                  <div class="pe-compare-cell">
                    <span>Version B (v{{ compareResult.version_b }})</span>
                    <strong>{{ (compareResult.score_b * 100).toFixed(0) }}%</strong>
                  </div>
                  <div class="pe-compare-cell">
                    <span>Difference</span>
                    <strong :class="compareResult.score_difference >= 0 ? 'positive' : 'negative'">
                      {{ compareResult.score_difference >= 0 ? '+' : '' }}{{ (compareResult.score_difference * 100).toFixed(0) }}%
                    </strong>
                  </div>
                </div>
                <p class="pe-compare-recommendation">
                  <strong>Recommendation:</strong> {{ compareResult.recommendation }}
                </p>
              </div>
            </template>
          </UiCard>
        </template>
      </div>
    </div>

    <!-- Effectiveness overview -->
    <UiCard class="pe-overview-card">
      <template #content>
        <div class="pe-section-title">
          <Activity :size="14" />
          <span>Effectiveness Overview</span>
        </div>
        <p v-if="sortedEffectivenessList.length === 0" class="quiet">No effectiveness data yet.</p>
        <div v-else class="pe-overview-grid">
          <div
            v-for="eff in sortedEffectivenessList"
            :key="eff.fragment_id"
            class="pe-overview-row"
            @click="() => { const f = fragments.find((x) => x.id === eff.fragment_id); if (f) selectFragment(f); }"
          >
            <div class="pe-overview-name">
              <strong>{{ fragments.find((f) => f.id === eff.fragment_id)?.title ?? eff.fragment_id }}</strong>
              <span>v{{ eff.active_version }} · {{ eff.total_invocations }} invocations</span>
            </div>
            <div class="pe-overview-score">
              <div class="pe-score-bar">
                <div
                  class="pe-score-fill"
                  :style="{ width: `${Math.max(0, Math.min(100, eff.effectiveness_score * 100))}%` }"
                  :class="eff.effectiveness_score >= 0.7 ? 'high' : eff.effectiveness_score >= 0.4 ? 'mid' : 'low'"
                ></div>
              </div>
              <UiBadge :variant="effectivenessVariant(eff.effectiveness_score)">
                {{ (eff.effectiveness_score * 100).toFixed(0) }}%
              </UiBadge>
            </div>
            <div class="pe-overview-signals">
              <span class="positive"><ThumbsUp :size="12" /> {{ eff.positive_signals }}</span>
              <span class="negative"><ThumbsDown :size="12" /> {{ eff.negative_signals }}</span>
            </div>
          </div>
        </div>
      </template>
    </UiCard>

    <!-- New version modal -->
    <Transition name="modal-fade">
      <div v-if="showNewVersion" class="pe-modal" @click.self="showNewVersion = false">
        <UiCard class="pe-modal-content">
          <template #header>
            <div class="pe-modal-header">
              <h3>New Version Snapshot</h3>
              <UiButton variant="ghost" size="icon" @click="showNewVersion = false">
                <span>×</span>
              </UiButton>
            </div>
          </template>
          <template #content>
            <p class="pe-modal-subtitle">
              Create a versioned snapshot for <strong>{{ selectedFragment?.title }}</strong>.
            </p>
            <div class="pe-modal-section">
              <UiLabel>Content</UiLabel>
              <textarea
                v-model="newVersionContent"
                class="pe-content-textarea"
                rows="8"
              ></textarea>
            </div>
            <div class="pe-modal-section">
              <UiLabel>Changed Fields (comma-separated)</UiLabel>
              <UiInput v-model="newVersionChangedFields" placeholder="content, priority, enabled" />
            </div>
            <div class="pe-modal-section">
              <UiLabel>Change Summary</UiLabel>
              <UiInput v-model="newVersionSummary" placeholder="brief description of this change" />
            </div>
          </template>
          <template #footer>
            <div class="modal-actions">
              <UiButton variant="outline" @click="showNewVersion = false">Cancel</UiButton>
              <UiButton :disabled="busy || !newVersionContent.trim()" @click="createVersion">
                <Plus :size="14" />
                <span>Create Version</span>
              </UiButton>
            </div>
          </template>
        </UiCard>
      </div>
    </Transition>
  </section>
</template>

<style scoped>
.prompt-engineering-panel {
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.pe-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
}
.pe-header h2 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 0 4px;
  font-size: 18px;
}
.pe-header p {
  margin: 0;
  color: var(--text-muted, #888);
  font-size: 13px;
}
.pe-error {
  padding: 8px 12px;
  background: rgba(255, 99, 99, 0.1);
  border: 1px solid rgba(255, 99, 99, 0.3);
  border-radius: 6px;
  color: var(--accent-danger, #ff6363);
  font-size: 13px;
}
.pe-layout {
  display: grid;
  grid-template-columns: 280px 1fr;
  gap: 16px;
  align-items: start;
}
.pe-fragment-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-height: 600px;
  overflow-y: auto;
  padding-right: 4px;
}
.pe-list-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}
.pe-list-header h3 {
  margin: 0;
  font-size: 13px;
}
.pe-fragment-card {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 10px 12px;
  background: var(--bg-elevated, rgba(255, 255, 255, 0.04));
  border: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.08));
  border-radius: 6px;
  cursor: pointer;
  text-align: left;
  color: inherit;
  transition: border-color 0.15s;
}
.pe-fragment-card:hover {
  border-color: var(--accent-primary, #58a6ff);
}
.pe-fragment-card.active {
  border-color: var(--accent-primary, #58a6ff);
  background: rgba(88, 166, 255, 0.08);
}
.pe-fragment-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
}
.pe-fragment-head strong {
  font-size: 13px;
}
.pe-fragment-meta {
  font-size: 11px;
  color: var(--text-muted, #888);
}
.pe-detail {
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.pe-detail-card :deep(.ui-card-content) {
  padding: 16px 18px;
}
.pe-detail-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
  margin-bottom: 12px;
}
.pe-detail-head h3 {
  margin: 0;
  font-size: 16px;
}
.pe-detail-head p {
  margin: 2px 0 0;
  font-size: 12px;
  color: var(--text-muted, #888);
}
.pe-metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 10px;
  margin-bottom: 14px;
}
.pe-metric {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.06));
  border-radius: 6px;
}
.pe-metric span {
  font-size: 11px;
  color: var(--text-muted, #888);
  display: block;
}
.pe-metric strong {
  font-size: 14px;
}
.pe-section {
  margin-top: 12px;
}
.pe-section-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 8px;
}
.pe-section-title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-muted, #aaa);
  text-transform: uppercase;
  letter-spacing: 0.5px;
}
.pe-content-textarea {
  width: 100%;
  padding: 8px 10px;
  background: var(--bg-input, rgba(255, 255, 255, 0.04));
  border: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.12));
  border-radius: 6px;
  color: inherit;
  font-size: 12px;
  font-family: var(--font-mono, monospace);
  resize: vertical;
  line-height: 1.5;
}
.pe-version-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.pe-version-row {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.06));
  border-radius: 6px;
}
.pe-version-row.active {
  border-color: var(--accent-success, #2ec4b6);
  background: rgba(46, 196, 182, 0.06);
}
.pe-version-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}
.pe-version-head strong {
  font-size: 13px;
}
.pe-version-date {
  font-size: 11px;
  color: var(--text-muted, #888);
}
.pe-version-summary {
  margin: 0 0 4px;
  font-size: 12px;
  color: var(--text-muted, #aaa);
}
.pe-version-fields {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.pe-version-field {
  padding: 1px 6px;
  background: rgba(88, 166, 255, 0.1);
  border-radius: 3px;
  font-size: 10px;
  font-family: var(--font-mono, monospace);
}
.pe-version-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}
.pe-hint {
  margin: 4px 0 10px;
  font-size: 12px;
  color: var(--text-muted, #888);
}
.pe-signal-form {
  display: grid;
  grid-template-columns: 160px 1fr;
  gap: 10px;
  margin-bottom: 10px;
}
.pe-signal-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.pe-select {
  height: 32px;
  padding: 0 8px;
  background: var(--bg-input, rgba(255, 255, 255, 0.04));
  border: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.12));
  border-radius: 6px;
  color: inherit;
  font-size: 13px;
}
.pe-signal-actions {
  display: flex;
  gap: 8px;
}
.pe-compare-form {
  display: grid;
  grid-template-columns: 1fr 1fr auto;
  gap: 10px;
  align-items: flex-end;
  margin-bottom: 12px;
}
.pe-compare-result {
  padding: 12px;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.06));
  border-radius: 6px;
}
.pe-compare-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 10px;
  margin-bottom: 10px;
}
.pe-compare-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.pe-compare-cell span {
  font-size: 11px;
  color: var(--text-muted, #888);
}
.pe-compare-cell strong {
  font-size: 18px;
}
.pe-compare-cell strong.positive {
  color: var(--accent-success, #2ec4b6);
}
.pe-compare-cell strong.negative {
  color: var(--accent-danger, #ff6363);
}
.pe-compare-recommendation {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted, #aaa);
}
.pe-overview-card :deep(.ui-card-content) {
  padding: 16px 18px;
}
.pe-overview-grid {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.pe-overview-row {
  display: grid;
  grid-template-columns: 1fr 200px auto;
  gap: 12px;
  align-items: center;
  padding: 8px 10px;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.06));
  border-radius: 6px;
  cursor: pointer;
  transition: border-color 0.15s;
}
.pe-overview-row:hover {
  border-color: var(--accent-primary, #58a6ff);
}
.pe-overview-name strong {
  font-size: 13px;
  display: block;
}
.pe-overview-name span {
  font-size: 11px;
  color: var(--text-muted, #888);
}
.pe-overview-score {
  display: flex;
  align-items: center;
  gap: 8px;
}
.pe-score-bar {
  flex: 1;
  height: 6px;
  background: rgba(255, 255, 255, 0.06);
  border-radius: 3px;
  overflow: hidden;
}
.pe-score-fill {
  height: 100%;
  transition: width 0.3s ease;
}
.pe-score-fill.high {
  background: var(--accent-success, #2ec4b6);
}
.pe-score-fill.mid {
  background: var(--accent-warning, #f0a020);
}
.pe-score-fill.low {
  background: var(--accent-danger, #ff6363);
}
.pe-overview-signals {
  display: flex;
  gap: 8px;
  font-size: 11px;
}
.pe-overview-signals .positive {
  color: var(--accent-success, #2ec4b6);
  display: flex;
  align-items: center;
  gap: 3px;
}
.pe-overview-signals .negative {
  color: var(--accent-danger, #ff6363);
  display: flex;
  align-items: center;
  gap: 3px;
}
.pe-modal {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 100;
  padding: 20px;
}
.pe-modal-content {
  width: 100%;
  max-width: 600px;
  max-height: 90vh;
  overflow-y: auto;
}
.pe-modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.pe-modal-header h3 {
  margin: 0;
  font-size: 16px;
}
.pe-modal-subtitle {
  margin: 0 0 14px;
  font-size: 13px;
  color: var(--text-muted, #aaa);
}
.pe-modal-section {
  margin-bottom: 14px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
.quiet {
  color: var(--text-muted, #888);
  font-size: 13px;
}
.modal-fade-enter-active,
.modal-fade-leave-active {
  transition: opacity 0.18s ease;
}
.modal-fade-enter-from,
.modal-fade-leave-to {
  opacity: 0;
}
</style>
