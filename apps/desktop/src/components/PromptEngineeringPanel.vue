<script setup lang="ts">
import {
  Activity,
  ArrowLeftRight,
  FileText,
  GitBranch,
  History,
  Info,
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
import { useDialogFocus } from '@/composables/useDialogFocus'
import { useSettingsLabels } from '@/pages/settings/settingsLabels'

const { t } = useI18n()
const { settingsErrorLabel } = useSettingsLabels()

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

// Rollback confirm
const confirmRollbackVersion = ref<number | null>(null)
const {
  closeDialog: closeNewVersionDialog,
  dialogRef: newVersionDialogRef,
  onDialogKeydown: onNewVersionDialogKeydown,
  openDialog: focusNewVersionDialog
} = useDialogFocus(() => { showNewVersion.value = false })

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
  selectedFragmentId.value = fragment.id
  versions.value = []
  effectiveness.value = null
  compareResult.value = null
  compareVersionA.value = null
  compareVersionB.value = null
  signalVersion.value = null
  try {
    const [versionList, eff] = await Promise.all([
      api.listPromptFragmentVersions(fragment.id),
      api.getPromptFragmentEffectiveness(fragment.id).catch(() => null)
    ])
    versions.value = versionList
    effectiveness.value = eff
    if (versionList.length > 0) {
      compareVersionA.value = versionList[versionList.length - 1].version
      compareVersionB.value = versionList[0].version
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  }
}

function openNewVersion() {
  showNewVersion.value = true
  newVersionContent.value = selectedFragment.value?.content ?? ''
  newVersionChangedFields.value = 'content'
  newVersionSummary.value = ''
  focusNewVersionDialog()
}

function closeNewVersion() {
  closeNewVersionDialog()
}

async function createVersion() {
  if (!selectedFragment.value || !newVersionContent.value.trim()) return
  busy.value = true
  error.value = null
  try {
    await api.createPromptFragmentVersion(selectedFragment.value.id, {
      content: newVersionContent.value,
      changed_fields: newVersionChangedFields.value
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean),
      change_summary: newVersionSummary.value || t('settings.promptDefaultChangeSummary')
    })
    closeNewVersion()
    await selectFragment(selectedFragment.value)
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

async function rollbackVersion(targetVersion: number) {
  if (!selectedFragment.value) return
  busy.value = true
  error.value = null
  try {
    const updated = await api.rollbackPromptFragment(selectedFragment.value.id, targetVersion)
    // Update local fragment list
    const idx = fragments.value.findIndex((f) => f.id === updated.id)
    if (idx >= 0) fragments.value[idx] = updated
    confirmRollbackVersion.value = null
    await selectFragment(updated)
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

async function recordSignal(signal: 'positive' | 'negative') {
  if (!selectedFragment.value) return
  busy.value = true
  error.value = null
  try {
    effectiveness.value = await api.recordPromptFragmentSignal(selectedFragment.value.id, {
      signal,
      note: signalNote.value || null,
      version: signalVersion.value ?? undefined
    })
    signalNote.value = ''
    // Refresh the global effectiveness list too
    effectivenessList.value = await api.listAllPromptFragmentEffectiveness().catch(() => effectivenessList.value)
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

async function compareVersions() {
  if (!selectedFragment.value || compareVersionA.value === null || compareVersionB.value === null) return
  busy.value = true
  error.value = null
  try {
    compareResult.value = await api.comparePromptFragmentVersions(
      selectedFragment.value.id,
      compareVersionA.value,
      compareVersionB.value
    )
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
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
        <h1><FileText :size="18" /> {{ t('settings.promptEngineeringTitle') }}</h1>
        <p>{{ t('settings.promptEngineeringSubtitle') }}</p>
      </div>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="loadAll">
        <RefreshCw :size="14" />
        <span>{{ t('settings.refresh') }}</span>
      </UiButton>
    </div>

    <div v-if="error" class="center-message error">
      <Info :size="16" />
      <span>{{ settingsErrorLabel(error) }}</span>
      <UiButton variant="outline" size="sm" @click="loadAll">{{ t('settings.retry') }}</UiButton>
    </div>

    <div v-if="!error || fragments.length > 0" class="pe-layout">
      <!-- Fragment list -->
      <div class="pe-fragment-list">
        <div class="pe-list-header">
          <h2>{{ t('settings.promptFragmentsList') }}</h2>
          <UiBadge variant="outline">{{ fragments.length }}</UiBadge>
        </div>
        <p v-if="loading" class="quiet">{{ t('settings.promptLoading') }}</p>
        <p v-else-if="fragments.length === 0" class="quiet">{{ t('settings.promptNoFragments') }}</p>
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
            <template v-if="fragment.is_builtin"> / {{ t('settings.promptBuiltinLabel') }}</template>
          </span>
        </button>
      </div>

      <!-- Detail panel -->
      <div class="pe-detail">
        <p v-if="!selectedFragment" class="quiet">{{ t('settings.promptSelectFragment') }}</p>
        <template v-else>
          <UiCard class="pe-detail-card">
            <template #content>
              <div class="pe-detail-head">
                <div>
                  <h2>{{ selectedFragment.title }}</h2>
                  <p>{{ selectedFragment.key }} · {{ selectedFragment.scope }} / {{ selectedFragment.category }}</p>
                </div>
                <UiBadge :variant="selectedFragment.enabled ? 'default' : 'secondary'">
                  {{ selectedFragment.enabled ? t('settings.enabled') : t('settings.promptDisabled') }}
                </UiBadge>
              </div>

              <div v-if="effectiveness" class="pe-metrics">
                <div class="pe-metric">
                  <Activity :size="14" />
                  <div>
                    <span>{{ t('settings.promptEffectiveness') }}</span>
                    <strong>{{ (effectiveness.effectiveness_score * 100).toFixed(0) }}%</strong>
                  </div>
                </div>
                <div class="pe-metric">
                  <History :size="14" />
                  <div>
                    <span>{{ t('settings.promptActiveVersion') }}</span>
                    <strong>v{{ effectiveness.active_version }}</strong>
                  </div>
                </div>
                <div class="pe-metric">
                  <ThumbsUp :size="14" />
                  <div>
                    <span>{{ t('settings.promptPositiveSignals') }}</span>
                    <strong>{{ effectiveness.positive_signals }}</strong>
                  </div>
                </div>
                <div class="pe-metric">
                  <ThumbsDown :size="14" />
                  <div>
                    <span>{{ t('settings.promptNegativeSignals') }}</span>
                    <strong>{{ effectiveness.negative_signals }}</strong>
                  </div>
                </div>
                <div class="pe-metric">
                  <FileText :size="14" />
                  <div>
                    <span>{{ t('settings.promptTotalInvocations') }}</span>
                    <strong>{{ effectiveness.total_invocations }}</strong>
                  </div>
                </div>
              </div>

              <div class="pe-section">
                <div class="pe-section-head">
                  <div class="pe-section-title">
                    <GitBranch :size="14" />
                    <span>{{ t('settings.promptCurrentContent') }}</span>
                  </div>
                  <UiButton size="sm" variant="outline" @click="openNewVersion">
                    <Plus :size="14" />
                    <span>{{ t('settings.promptNewVersion') }}</span>
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
                  <span>{{ t('settings.promptVersionHistory') }}</span>
                </div>
                <UiBadge variant="outline">{{ versions.length }}</UiBadge>
              </div>
              <p v-if="versions.length === 0" class="quiet">{{ t('settings.promptNoVersions') }}</p>
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
                      <UiBadge v-if="version.is_active" variant="default">{{ t('settings.promptActive') }}</UiBadge>
                      <span class="pe-version-date">{{ formatDate(version.created_at) }}</span>
                    </div>
                    <p class="pe-version-summary">{{ version.change_summary }}</p>
                    <div v-if="version.changed_fields.length > 0" class="pe-version-fields">
                      <span v-for="field in version.changed_fields" :key="field" class="pe-version-field">{{ field }}</span>
                    </div>
                  </div>
                  <div class="pe-version-actions">
                    <template v-if="confirmRollbackVersion === version.version">
                      <span class="pe-confirm-text">{{ t('settings.promptRollbackQuestion', { version: version.version }) }}</span>
                      <UiButton size="sm" variant="destructive" :disabled="busy" @click="rollbackVersion(version.version)">
                        {{ t('settings.promptConfirm') }}
                      </UiButton>
                      <UiButton size="sm" variant="ghost" @click="confirmRollbackVersion = null">{{ t('settings.cancel') }}</UiButton>
                    </template>
                    <template v-else>
                      <UiButton
                        v-if="!version.is_active"
                        size="sm"
                        variant="ghost"
                        :disabled="busy"
                        :title="t('settings.promptRollbackTo', { version: version.version })"
                        @click="confirmRollbackVersion = version.version"
                      >
                        <Undo2 :size="14" />
                        <span>{{ t('settings.promptRollback') }}</span>
                      </UiButton>
                    </template>
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
                <span>{{ t('settings.promptRecordSignal') }}</span>
              </div>
              <p class="pe-hint">{{ t('settings.promptSignalHint') }}</p>
              <div class="pe-signal-form">
                <div class="pe-signal-field">
                  <UiLabel>{{ t('settings.promptVersionOptional') }}</UiLabel>
                  <select v-model="signalVersion" class="pe-select">
                    <option :value="null">{{ t('settings.promptActiveVersionOption') }}</option>
                    <option v-for="version in versions" :key="version.id" :value="version.version">
                      v{{ version.version }}{{ version.is_active ? ` (${t('settings.promptActive')})` : '' }}
                    </option>
                  </select>
                </div>
                <div class="pe-signal-field pe-signal-note">
                  <UiLabel>{{ t('settings.promptNoteOptional') }}</UiLabel>
                  <UiInput v-model="signalNote" :placeholder="t('settings.promptSignalPlaceholder')" />
                </div>
              </div>
              <div class="pe-signal-actions">
                <UiButton variant="outline" size="sm" :disabled="busy" @click="recordSignal('positive')">
                  <ThumbsUp :size="14" />
                  <span>{{ t('settings.promptPositive') }}</span>
                </UiButton>
                <UiButton variant="outline" size="sm" :disabled="busy" @click="recordSignal('negative')">
                  <ThumbsDown :size="14" />
                  <span>{{ t('settings.promptNegative') }}</span>
                </UiButton>
              </div>
            </template>
          </UiCard>

          <!-- A/B Compare -->
          <UiCard v-if="versions.length >= 2" class="pe-detail-card">
            <template #content>
              <div class="pe-section-title">
                <ArrowLeftRight :size="14" />
                <span>{{ t('settings.promptCompareTitle') }}</span>
              </div>
              <p class="pe-hint">{{ t('settings.promptCompareHint') }}</p>
              <div class="pe-compare-form">
                <div class="pe-signal-field">
                  <UiLabel>{{ t('settings.promptVersionA') }}</UiLabel>
                  <select v-model="compareVersionA" class="pe-select">
                    <option v-for="version in versions" :key="version.id" :value="version.version">v{{ version.version }}</option>
                  </select>
                </div>
                <div class="pe-signal-field">
                  <UiLabel>{{ t('settings.promptVersionB') }}</UiLabel>
                  <select v-model="compareVersionB" class="pe-select">
                    <option v-for="version in versions" :key="version.id" :value="version.version">v{{ version.version }}</option>
                  </select>
                </div>
                <UiButton size="sm" :disabled="busy || compareVersionA === compareVersionB" @click="compareVersions">
                  <ArrowLeftRight :size="14" />
                  <span>{{ t('settings.promptCompare') }}</span>
                </UiButton>
              </div>

              <div v-if="compareResult" class="pe-compare-result">
                <div class="pe-compare-grid">
                  <div class="pe-compare-cell">
                    <span>{{ t('settings.promptVersionA') }} (v{{ compareResult.version_a }})</span>
                    <strong>{{ (compareResult.score_a * 100).toFixed(0) }}%</strong>
                  </div>
                  <div class="pe-compare-cell">
                    <span>{{ t('settings.promptVersionB') }} (v{{ compareResult.version_b }})</span>
                    <strong>{{ (compareResult.score_b * 100).toFixed(0) }}%</strong>
                  </div>
                  <div class="pe-compare-cell">
                    <span>{{ t('settings.promptDifference') }}</span>
                    <strong :class="compareResult.score_difference >= 0 ? 'positive' : 'negative'">
                      {{ compareResult.score_difference >= 0 ? '+' : '' }}{{ (compareResult.score_difference * 100).toFixed(0) }}%
                    </strong>
                  </div>
                </div>
                <p class="pe-compare-recommendation">
                  <strong>{{ t('settings.promptRecommendation') }}</strong> {{ compareResult.recommendation }}
                </p>
              </div>
            </template>
          </UiCard>
        </template>
      </div>
    </div>

    <!-- Effectiveness overview -->
    <UiCard v-if="!error || fragments.length > 0" class="pe-overview-card">
      <template #content>
        <div class="pe-section-title">
          <Activity :size="14" />
          <span>{{ t('settings.promptEffectivenessOverview') }}</span>
        </div>
        <p v-if="sortedEffectivenessList.length === 0" class="quiet">{{ t('settings.promptNoEffectiveness') }}</p>
        <div v-else class="pe-overview-grid">
          <button
            type="button"
            v-for="eff in sortedEffectivenessList"
            :key="eff.fragment_id"
            class="pe-overview-row"
            @click="() => { const f = fragments.find((x) => x.id === eff.fragment_id); if (f) selectFragment(f); }"
          >
            <div class="pe-overview-name">
              <strong>{{ fragments.find((f) => f.id === eff.fragment_id)?.title ?? eff.fragment_id }}</strong>
              <span>v{{ eff.active_version }} · {{ t('settings.promptInvocationCount', { count: eff.total_invocations }) }}</span>
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
          </button>
        </div>
      </template>
    </UiCard>

    <!-- New version modal -->
    <Transition name="modal-fade">
      <div v-if="showNewVersion" class="pe-modal" @click.self="closeNewVersion">
        <div ref="newVersionDialogRef" role="dialog" aria-modal="true" aria-labelledby="prompt-new-version-title" tabindex="-1" @keydown="onNewVersionDialogKeydown">
        <UiCard class="pe-modal-content">
          <template #header>
            <div class="pe-modal-header">
              <h2 id="prompt-new-version-title">{{ t('settings.promptNewSnapshot') }}</h2>
              <UiButton variant="ghost" size="icon" :aria-label="t('app.close')" @click="closeNewVersion">
                <span>×</span>
              </UiButton>
            </div>
          </template>
          <template #content>
            <p class="pe-modal-subtitle">
              {{ t('settings.promptSnapshotDescription', { name: selectedFragment?.title }) }}
            </p>
            <div class="pe-modal-section">
              <UiLabel>{{ t('settings.promptContent') }}</UiLabel>
              <textarea
                v-model="newVersionContent"
                class="pe-content-textarea"
                rows="8"
              ></textarea>
            </div>
            <div class="pe-modal-section">
              <UiLabel>{{ t('settings.promptChangedFields') }}</UiLabel>
              <UiInput v-model="newVersionChangedFields" :placeholder="t('settings.promptChangedFieldsPlaceholder')" />
            </div>
            <div class="pe-modal-section">
              <UiLabel>{{ t('settings.promptChangeSummary') }}</UiLabel>
              <UiInput v-model="newVersionSummary" :placeholder="t('settings.promptChangeSummaryPlaceholder')" />
            </div>
          </template>
          <template #footer>
            <div class="modal-actions">
              <UiButton variant="outline" @click="closeNewVersion">{{ t('settings.cancel') }}</UiButton>
              <UiButton :disabled="busy || !newVersionContent.trim()" @click="createVersion">
                <Plus :size="14" />
                <span>{{ t('settings.promptCreateVersion') }}</span>
              </UiButton>
            </div>
          </template>
        </UiCard>
        </div>
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
.pe-header h1 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 0 4px;
  font-size: 24px;
  font-weight: 600;
  line-height: 1.2;
  letter-spacing: 0;
}
.pe-header p {
  margin: 0;
  color: var(--text-muted);
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
.pe-list-header h2 {
  margin: 0;
  font-size: 13px;
}
.pe-fragment-card {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 10px 12px;
  background: var(--bg-tertiary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  cursor: pointer;
  text-align: left;
  color: inherit;
  transition: border-color 0.15s;
}
.pe-fragment-card:hover {
  border-color: var(--accent-primary);
}
.pe-fragment-card.active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 8%, transparent);
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
  color: var(--text-muted);
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
.pe-detail-head h2 {
  margin: 0;
  font-size: 16px;
}
.pe-detail-head p {
  margin: 2px 0 0;
  font-size: 12px;
  color: var(--text-muted);
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
  background: color-mix(in srgb, var(--text-primary) 3%, transparent);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
}
.pe-metric span {
  font-size: 11px;
  color: var(--text-muted);
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
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.5px;
}
.pe-content-textarea {
  width: 100%;
  padding: 8px 10px;
  background: var(--bg-input);
  border: 1px solid var(--border-muted);
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
  background: color-mix(in srgb, var(--text-primary) 3%, transparent);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
}
.pe-version-row.active {
  border-color: var(--accent-success);
  background: color-mix(in srgb, var(--accent-success) 6%, transparent);
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
  color: var(--text-muted);
}
.pe-version-summary {
  margin: 0 0 4px;
  font-size: 12px;
  color: var(--text-muted);
}
.pe-version-fields {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.pe-version-field {
  padding: 1px 6px;
  background: color-mix(in srgb, var(--accent-primary) 10%, transparent);
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
.pe-confirm-text {
  font-size: 12px;
  color: var(--accent-danger);
}
.pe-hint {
  margin: 4px 0 10px;
  font-size: 12px;
  color: var(--text-muted);
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
  background: var(--bg-input);
  border: 1px solid var(--border-muted);
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
  background: color-mix(in srgb, var(--text-primary) 3%, transparent);
  border: 1px solid var(--border-muted);
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
  color: var(--text-muted);
}
.pe-compare-cell strong {
  font-size: 18px;
}
.pe-compare-cell strong.positive {
  color: var(--accent-success);
}
.pe-compare-cell strong.negative {
  color: var(--accent-danger);
}
.pe-compare-recommendation {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
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
  width: 100%;
  display: grid;
  grid-template-columns: 1fr 200px auto;
  gap: 12px;
  align-items: center;
  padding: 8px 10px;
  background: color-mix(in srgb, var(--text-primary) 3%, transparent);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  cursor: pointer;
  color: inherit;
  font: inherit;
  text-align: left;
  transition: border-color 0.15s;
}
.pe-overview-row:hover {
  border-color: var(--accent-primary);
}
.pe-overview-name strong {
  font-size: 13px;
  display: block;
}
.pe-overview-name span {
  font-size: 11px;
  color: var(--text-muted);
}
.pe-overview-score {
  display: flex;
  align-items: center;
  gap: 8px;
}
.pe-score-bar {
  flex: 1;
  height: 6px;
  background: color-mix(in srgb, var(--text-primary) 6%, transparent);
  border-radius: 3px;
  overflow: hidden;
}
.pe-score-fill {
  height: 100%;
}
.pe-score-fill.high {
  background: var(--accent-success);
}
.pe-score-fill.mid {
  background: var(--accent-warning);
}
.pe-score-fill.low {
  background: var(--accent-danger);
}
.pe-overview-signals {
  display: flex;
  gap: 8px;
  font-size: 11px;
}
.pe-overview-signals .positive {
  color: var(--accent-success);
  display: flex;
  align-items: center;
  gap: 3px;
}
.pe-overview-signals .negative {
  color: var(--accent-danger);
  display: flex;
  align-items: center;
  gap: 3px;
}
.pe-modal {
  position: fixed;
  inset: 0;
  background: color-mix(in srgb, var(--bg-primary) 70%, transparent);
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
.pe-modal-header h2 {
  margin: 0;
  font-size: 16px;
}
.pe-modal-subtitle {
  margin: 0 0 14px;
  font-size: 13px;
  color: var(--text-muted);
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
  color: var(--text-muted);
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

@media (prefers-reduced-motion: reduce) {
  .modal-fade-enter-active,
  .modal-fade-leave-active {
    transition: none;
  }
}

@media (max-width: 640px) {
  .pe-layout,
  .pe-signal-form,
  .pe-compare-form,
  .pe-compare-grid,
  .pe-overview-row {
    grid-template-columns: 1fr;
  }

  .pe-fragment-list {
    max-height: none;
    min-width: 0;
  }

  .pe-detail,
  .pe-version-main,
  .pe-overview-name {
    min-width: 0;
  }

  .pe-version-row,
  .pe-version-actions,
  .pe-header {
    align-items: stretch;
    flex-direction: column;
  }
}
</style>
