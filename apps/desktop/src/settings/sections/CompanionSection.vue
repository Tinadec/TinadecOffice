<script setup lang="ts">
/**
 * Companion settings section (spec: add-companion-mode, Task 8).
 *
 * Two surfaces in one section: the user-wide switchboard (main companion, global autonomy
 * pause) and a per-companion editor. The section is mounted the first time the user enters it
 * and then kept alive by SettingsPage, so switching away and back does not re-read; every save
 * is conditional on the revision Core returned last and a 412 is reported, never papered over.
 */
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Archive, Bot, Plus, RefreshCw } from '@lucide/vue'
import { UiBadge, UiButton, UiSwitch } from '@/components/ui'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { useHostAccess } from '@/lib/hostAccess'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'
import type { CompanionSettingsTab } from '@/lib/pageRequests'
import {
  companionApi,
  type CompanionSettings,
  type CompanionSummary,
} from '@/lib/companionApi'
import CompanionEditor from './companion/CompanionEditor.vue'

const props = defineProps<{
  /**
   * A deep link from the companion centre: which companion to open, the request's sequence, and
   * the editor tab when the caller meant a specific panel ("memories", not "the companion").
   */
  companionRequest?: { id: string; seq: number; tab?: CompanionSettingsTab } | null
}>()

const { t } = useI18n()
const { canAccessBackend, reason: hostReason } = useHostAccess()
const { confirm, notify } = useNotifications()

const settings = ref<CompanionSettings | null>(null)
const companions = ref<CompanionSummary[]>([])
const selectedId = ref<string | null>(null)
const loading = ref(false)
const listFailure = ref<ErrorState | null>(null)
const settingsFailure = ref<ErrorState | null>(null)
const settingsConflict = ref(false)
const savingGlobal = ref(false)
const busyId = ref<string | null>(null)
const createOpen = ref(false)
const creating = ref(false)
const createError = ref('')
const createForm = reactive({ name: '', tagline: '' })
/** The id a deep link or a fresh creation should land on once the list is known. */
const pendingSelectId = ref<string | null>(props.companionRequest?.id ?? null)
/** Which editor panel a deep link asked for; the editor opens on it and follows later requests. */
const editorTab = ref<CompanionSettingsTab>(props.companionRequest?.tab ?? 'identity')
/**
 * Bumped on every deep link. It keys the editor so a link to the same companion still lands on
 * the requested panel even when the user had clicked away from it in between.
 */
const editorRequestSeq = ref(0)
let readGeneration = 0

const activeCompanions = computed(() => companions.value.filter((item) => item.status !== 'archived'))
const archivedCompanions = computed(() => companions.value.filter((item) => item.status === 'archived'))
const mainCompanion = computed(() => settings.value?.main_companion_id ?? '')

function selectFromList() {
  const wanted = pendingSelectId.value ?? settings.value?.main_companion_id ?? null
  const known = wanted != null ? companions.value.find((item) => item.id === wanted) : undefined
  const fallback = activeCompanions.value[0] ?? companions.value[0]
  selectedId.value = known?.id ?? fallback?.id ?? null
  pendingSelectId.value = null
}

async function loadList() {
  const read = readGeneration
  try {
    companions.value = await companionApi.listCompanions(true)
    if (read !== readGeneration) return
    listFailure.value = null
    selectFromList()
  } catch (error) {
    if (read !== readGeneration) return
    listFailure.value = toErrorState(error, t('settings.companions.listLoadFailed'))
  }
}

async function loadSettings() {
  const read = readGeneration
  try {
    settings.value = await companionApi.getSettings()
    if (read !== readGeneration) return
    settingsFailure.value = null
  } catch (error) {
    if (read !== readGeneration) return
    settingsFailure.value = toErrorState(error, t('settings.companions.loadFailed'))
  }
}

async function load() {
  if (!canAccessBackend.value) return
  const read = ++readGeneration
  loading.value = true
  settingsConflict.value = false
  await Promise.all([loadList(), loadSettings()])
  if (read !== readGeneration) return
  loading.value = false
}

async function refresh() {
  await load()
}

onMounted(() => { void load() })
onBeforeUnmount(() => { readGeneration++ })
watch(canAccessBackend, (ready) => { if (ready) void load() })

watch(() => props.companionRequest, (request) => {
  if (!request?.id) return
  pendingSelectId.value = request.id
  // A link without a tab means "the companion", not "leave the last panel open".
  editorTab.value = request.tab ?? 'identity'
  editorRequestSeq.value++
  if (companions.value.length === 0) { void load(); return }
  selectFromList()
})

function selectCompanion(id: string) {
  if (!companions.value.some((item) => item.id === id)) return
  selectedId.value = id
}

async function patchGlobal(patch: { main_companion_id?: string | null; autonomy_paused?: boolean }) {
  const current = settings.value
  if (!current || savingGlobal.value) return
  savingGlobal.value = true
  settingsConflict.value = false
  try {
    settings.value = await companionApi.patchSettings(current.revision, patch)
  } catch (error) {
    if (isCompanionRevisionConflict(error)) {
      settingsConflict.value = true
      return
    }
    notify.error(error, { title: t('settings.companions.globalSaveFailed'), key: 'companion-global-settings' })
  } finally {
    savingGlobal.value = false
  }
}

function onMainCompanionChange(event: Event) {
  const value = (event.target as HTMLSelectElement).value
  void patchGlobal({ main_companion_id: value || null })
}

function openCreate() {
  createForm.name = ''
  createForm.tagline = ''
  createError.value = ''
  createOpen.value = true
}

function closeCreate() {
  createOpen.value = false
  creating.value = false
}

async function createCompanion() {
  const name = createForm.name.trim()
  if (!name) {
    createError.value = t('settings.companions.createNameRequired')
    return
  }
  creating.value = true
  createError.value = ''
  try {
    const created = await companionApi.createCompanion({
      name,
      tagline: createForm.tagline.trim() || null,
      model_purpose: 'chat',
    })
    pendingSelectId.value = created.id
    createOpen.value = false
    createForm.name = ''
    createForm.tagline = ''
    await loadList()
    selectedId.value = created.id
  } catch (error) {
    if (isCompanionRevisionConflict(error)) {
      createError.value = t('settings.companions.conflictBody')
      return
    }
    createError.value = toErrorState(error, t('settings.companions.createFailed')).message
  } finally {
    creating.value = false
  }
}

async function archiveCompanion(companion: CompanionSummary) {
  const confirmed = await confirm({
    title: t('settings.companions.archiveTitle', { name: companion.name }),
    message: t('settings.companions.archiveMessage'),
    confirmLabel: t('settings.companions.archiveConfirm'),
    cancelLabel: t('common.cancel'),
    destructive: true,
  })
  if (!confirmed) return
  busyId.value = companion.id
  try {
    // Archive is conditional on the companion's current revision, which only the detail carries.
    const detail = await companionApi.getCompanion(companion.id)
    await companionApi.archiveCompanion(companion.id, detail.revision)
    await loadList()
  } catch (error) {
    if (isCompanionRevisionConflict(error)) {
      settingsConflict.value = true
      await loadList()
      return
    }
    notify.error(error, { title: t('settings.companions.archiveFailed'), key: `companion-archive-${companion.id}` })
  } finally {
    busyId.value = null
  }
}
</script>

<template>
  <div class="companion-settings">
    <header class="companion-head">
      <div class="companion-head-copy">
        <h2>{{ t('settings.companions.title') }}</h2>
        <p class="companion-subtitle">{{ t('settings.companions.subtitle') }}</p>
      </div>
      <UiButton variant="outline" size="sm" data-testid="companion-refresh" :disabled="loading || !canAccessBackend" @click="refresh">
        <RefreshCw :size="14" />
        <span>{{ t('common.refresh') }}</span>
      </UiButton>
    </header>

    <p v-if="!canAccessBackend" role="status" class="companion-host">{{ hostReason }}</p>

    <div v-if="settingsFailure" role="alert" class="companion-error">
      <p>{{ settingsFailure.message }}</p>
      <UiButton variant="outline" size="sm" :disabled="loading || !canAccessBackend" @click="load">{{ t('common.retry') }}</UiButton>
    </div>
    <div v-if="listFailure" role="alert" class="companion-error" data-testid="companion-list-error">
      <p>{{ listFailure.message }}</p>
      <UiButton variant="outline" size="sm" :disabled="loading || !canAccessBackend" @click="load">{{ t('common.retry') }}</UiButton>
    </div>
    <div v-if="settingsConflict" role="alert" class="companion-conflict" data-testid="companion-global-conflict">
      <p><strong>{{ t('settings.companions.conflictTitle') }}</strong> {{ t('settings.companions.conflictBody') }}</p>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="load">{{ t('settings.companions.reload') }}</UiButton>
    </div>

    <section class="companion-global" data-testid="companion-global">
      <h3>{{ t('settings.companions.globalTitle') }}</h3>
      <div class="companion-field">
        <label for="companion-main-companion">{{ t('settings.companions.mainCompanion') }}</label>
        <select
          id="companion-main-companion"
          class="settings-select"
          data-testid="companion-main-companion"
          :value="mainCompanion"
          :disabled="!settings || savingGlobal"
          @change="onMainCompanionChange"
        >
          <option value="">{{ t('settings.companions.mainCompanionNone') }}</option>
          <option v-for="companion in activeCompanions" :key="companion.id" :value="companion.id">
            {{ companion.name }}
          </option>
        </select>
        <p class="companion-hint">{{ t('settings.companions.mainCompanionHint') }}</p>
      </div>
      <div class="companion-switch-row">
        <div class="companion-switch-copy">
          <strong>{{ t('settings.companions.globalPause') }}</strong>
          <p class="companion-hint">{{ t('settings.companions.globalPauseHint') }}</p>
        </div>
        <UiSwitch
          data-testid="companion-global-pause"
          :model-value="settings?.autonomy_paused === true"
          :disabled="!settings || savingGlobal"
          @update:model-value="(value: boolean) => patchGlobal({ autonomy_paused: value })"
        />
      </div>
    </section>

    <section class="companion-list-section">
      <div class="companion-list-head">
        <h3>{{ t('settings.companions.listTitle') }}</h3>
        <UiButton size="sm" data-testid="companion-create-open" :disabled="!canAccessBackend" @click="openCreate">
          <Plus :size="14" />
          <span>{{ t('settings.companions.createAction') }}</span>
        </UiButton>
      </div>

      <div v-if="loading && companions.length === 0" class="companion-skeleton" role="status" :aria-label="t('settings.companions.loading')">
        <span />
        <span />
        <span />
      </div>
      <p v-else-if="!loading && companions.length === 0 && !listFailure" class="companion-empty" data-testid="companion-list-empty">
        {{ t('settings.companions.listEmpty') }}
      </p>
      <ul v-else class="companion-rows">
        <li
          v-for="companion in companions"
          :key="companion.id"
          class="companion-row"
          :class="{ selected: companion.id === selectedId, archived: companion.status === 'archived' }"
          :data-testid="`companion-row-${companion.id}`"
        >
          <button type="button" class="companion-row-main" :aria-pressed="companion.id === selectedId" @click="selectCompanion(companion.id)">
            <AgentPetAvatar class="companion-avatar" :pet-slug="companion.avatar ?? ''" :size="26" :alt="companion.name" />
            <span class="companion-row-copy">
              <strong>{{ companion.name }}</strong>
              <span class="companion-row-meta">{{ companion.tagline || t('settings.companions.noTagline') }}</span>
            </span>
            <UiBadge v-if="companion.status === 'archived'" variant="secondary">{{ t('settings.companions.archivedBadge') }}</UiBadge>
            <UiBadge v-else-if="companion.autonomy_paused" variant="outline">{{ t('settings.companions.pausedBadge') }}</UiBadge>
          </button>
          <UiButton
            v-if="companion.status !== 'archived'"
            variant="ghost"
            size="sm"
            :disabled="busyId !== null"
            :data-testid="`companion-archive-${companion.id}`"
            @click="archiveCompanion(companion)"
          >
            <Archive :size="14" />
            <span>{{ t('settings.companions.archiveAction') }}</span>
          </UiButton>
        </li>
      </ul>
      <p v-if="archivedCompanions.length" class="companion-hint" data-testid="companion-archived-count">
        {{ t('settings.companions.archivedCount', { count: archivedCompanions.length }) }}
      </p>
    </section>

    <div v-if="createOpen" class="companion-modal-backdrop" @click.self="closeCreate">
      <div class="companion-modal" role="dialog" aria-modal="true" :aria-label="t('settings.companions.createTitle')">
        <div class="companion-modal-head">
          <Bot :size="16" aria-hidden="true" />
          <h3>{{ t('settings.companions.createTitle') }}</h3>
        </div>
        <div class="companion-field">
          <label for="companion-create-name">{{ t('settings.companions.createName') }}</label>
          <input id="companion-create-name" v-model="createForm.name" class="settings-select" data-testid="companion-create-name" type="text" />
        </div>
        <div class="companion-field">
          <label for="companion-create-tagline">{{ t('settings.companions.createTagline') }}</label>
          <input id="companion-create-tagline" v-model="createForm.tagline" class="settings-select" data-testid="companion-create-tagline" type="text" />
        </div>
        <p v-if="createError" role="alert" class="companion-error-text" data-testid="companion-create-error">{{ createError }}</p>
        <div class="companion-modal-actions">
          <UiButton variant="ghost" size="sm" :disabled="creating" @click="closeCreate">{{ t('common.cancel') }}</UiButton>
          <UiButton size="sm" data-testid="companion-create-confirm" :disabled="creating" @click="createCompanion">
            {{ t('settings.companions.createConfirm') }}
          </UiButton>
        </div>
      </div>
    </div>

    <CompanionEditor
      v-if="selectedId"
      :key="`${selectedId}:${editorRequestSeq}`"
      :companion-id="selectedId"
      :initial-tab="editorTab"
      class="companion-editor-host"
    />
    <p v-else-if="!loading" class="companion-empty">{{ t('settings.companions.selectCompanion') }}</p>
  </div>
</template>

<style scoped>
.companion-settings {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.companion-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.companion-head-copy h2 {
  margin: 0 0 4px;
  font-size: 18px;
  color: var(--text-primary);
}

.companion-subtitle,
.companion-hint {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-muted);
}

.companion-host {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.companion-error,
.companion-conflict {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--surface-section, var(--surface-raised));
  font-size: 12px;
  color: var(--text-secondary);
}

.companion-error p,
.companion-conflict p {
  margin: 0;
  flex: 1;
  min-width: 200px;
}

.companion-conflict {
  box-shadow: inset 3px 0 0 var(--accent-warning);
}

.companion-error {
  box-shadow: inset 3px 0 0 var(--accent-danger);
}

.companion-error-text {
  margin: 0;
  font-size: 12px;
  color: var(--accent-danger);
}

.companion-global,
.companion-list-section {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px;
  border-radius: 10px;
  background: var(--surface-section, var(--surface-raised));
}

.companion-global h3,
.companion-list-head h3 {
  margin: 0;
  font-size: 13px;
  color: var(--text-primary);
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

.companion-switch-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.companion-switch-copy {
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-size: 13px;
  color: var(--text-primary);
}

.companion-list-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.companion-rows {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.companion-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 6px;
  border-radius: 8px;
  background: var(--surface-chrome, transparent);
}

.companion-row.selected {
  box-shadow: inset 2px 0 0 var(--accent-primary);
}

.companion-row.archived .companion-row-main {
  opacity: 0.75;
}

.companion-row-main {
  display: flex;
  flex: 1;
  min-width: 0;
  align-items: center;
  gap: 8px;
  padding: 4px;
  border: 0;
  background: transparent;
  color: inherit;
  text-align: left;
  cursor: pointer;
}

.companion-avatar {
  flex: none;
  border-radius: 8px;
}

.companion-row-copy {
  display: flex;
  flex: 1;
  min-width: 0;
  flex-direction: column;
}

.companion-row-copy strong {
  font-size: 13px;
  color: var(--text-primary);
}

.companion-row-meta {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11px;
  color: var(--text-muted);
}

.companion-empty {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
}

.companion-skeleton {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.companion-skeleton span {
  height: 30px;
  border-radius: 8px;
  background: var(--surface-input);
  animation: companion-skeleton-pulse 1.2s ease-in-out infinite;
}

.companion-skeleton span:nth-child(2) { animation-delay: 0.15s; }
.companion-skeleton span:nth-child(3) { animation-delay: 0.3s; }

@keyframes companion-skeleton-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.55; }
}

.companion-modal-backdrop {
  position: fixed;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgb(0 0 0 / 45%);
  z-index: 40;
}

.companion-modal {
  display: flex;
  flex-direction: column;
  gap: 12px;
  width: min(420px, 90vw);
  padding: 18px;
  border-radius: 12px;
  background: var(--surface-raised);
  box-shadow: 0 20px 60px rgb(0 0 0 / 35%);
}

.companion-modal-head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.companion-modal-head h3 {
  margin: 0;
  font-size: 14px;
  color: var(--text-primary);
}

.companion-modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
