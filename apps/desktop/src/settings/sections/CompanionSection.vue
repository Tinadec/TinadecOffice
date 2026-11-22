<script setup lang="ts">
/**
 * Dedicated Companion Settings section (strictly distinct from Agent Center).
 *
 * Surfaces:
 * 1. User-wide switchboard: default main companion, global autonomy pause, recruitment ceiling,
 *    and `max_bot_hop_depth` (`0..10`, default `3`; `0` disables bot-to-bot auto-relay).
 *    (`buddy_companion_id` is purged per Phase 7 unification.)
 * 2. Unified Companion Avatar Library (`PetsSection` embedded: pixel sprites + Live2D + BYO SDK).
 * 3. Companion Bots directory + inline editor + `CompanionEditorDrawer` slide-over support.
 */
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Archive, Bot, PanelRightOpen, Plus, RefreshCw } from '@lucide/vue'
import { UiBadge, UiButton, UiSwitch } from '@/components/ui'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { useHostAccess } from '@/lib/hostAccess'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'
import { avatarKindOf } from '@/pets/avatarKind'
import type { CompanionSettingsTab } from '@/lib/pageRequests'
import {
  companionApi,
  type CompanionAvatar,
  type CompanionDetail,
  type CompanionSettings,
  type CompanionSummary,
} from '@/lib/companionApi'
import CompanionEditor from './companion/CompanionEditor.vue'
import CompanionEditorDrawer from './CompanionEditorDrawer.vue'
import PetsSection from './PetsSection.vue'

type CompanionSettingsWithHop = CompanionSettings & {
  max_bot_hop_depth?: number
}

const props = defineProps<{
  companionRequest?: { id: string; seq: number; tab?: CompanionSettingsTab } | null
}>()

const { t } = useI18n()
const { canAccessBackend, reason: hostReason } = useHostAccess()
const { confirm, notify } = useNotifications()

const settings = ref<CompanionSettingsWithHop | null>(null)
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

const drawerOpen = ref(false)
const drawerCompanionId = ref<string | null>(null)

const avatars = ref<CompanionAvatar[]>([])
const avatarsLoading = ref(false)
const avatarsFailure = ref<ErrorState | null>(null)

const pendingSelectId = ref<string | null>(props.companionRequest?.id ?? null)
const editorTab = ref<CompanionSettingsTab>(props.companionRequest?.tab ?? 'identity')
const editorRequestSeq = ref(0)
let readGeneration = 0

const activeCompanions = computed(() => companions.value.filter((item) => item.status !== 'archived'))
const archivedCompanions = computed(() => companions.value.filter((item) => item.status === 'archived'))
const mainCompanion = computed(() => settings.value?.main_companion_id ?? '')
const maxBotHopDepth = computed(() => settings.value?.max_bot_hop_depth ?? 3)
const live2dIds = computed(() => avatars.value.map((model) => model.id))

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
    settings.value = (await companionApi.getSettings()) as CompanionSettingsWithHop
    if (read !== readGeneration) return
    settingsFailure.value = null
  } catch (error) {
    if (read !== readGeneration) return
    settingsFailure.value = toErrorState(error, t('settings.companions.loadFailed'))
  }
}

async function loadAvatars() {
  if (!canAccessBackend.value) return
  const read = readGeneration
  avatarsLoading.value = true
  try {
    avatars.value = await companionApi.listCompanionAvatars()
    if (read !== readGeneration) return
    avatarsFailure.value = null
  } catch (error) {
    if (read !== readGeneration) return
    avatarsFailure.value = toErrorState(error, t('settings.companions.live2d.loadFailed'))
  } finally {
    if (read === readGeneration) avatarsLoading.value = false
  }
}

async function load() {
  if (!canAccessBackend.value) return
  const read = ++readGeneration
  loading.value = true
  settingsConflict.value = false
  await Promise.all([loadList(), loadSettings(), loadAvatars()])
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
  editorTab.value = request.tab ?? 'identity'
  editorRequestSeq.value++
  if (companions.value.length === 0) { void load(); return }
  selectFromList()
})

function selectCompanion(id: string) {
  if (!companions.value.some((item) => item.id === id)) return
  selectedId.value = id
}

function openDrawerForCompanion(id: string | null) {
  drawerCompanionId.value = id
  drawerOpen.value = true
}

async function onDrawerCreated(created: CompanionDetail) {
  pendingSelectId.value = created.id
  await loadList()
  selectedId.value = created.id
}

async function onDrawerSaved() {
  await loadList()
}

async function patchGlobal(patch: {
  main_companion_id?: string | null
  recruit_limit?: number
  max_bot_hop_depth?: number
  autonomy_paused?: boolean
}) {
  const current = settings.value
  if (!current || savingGlobal.value) return
  savingGlobal.value = true
  settingsConflict.value = false
  try {
    settings.value = (await companionApi.patchSettings(
      current.revision,
      patch as Parameters<typeof companionApi.patchSettings>[1],
    )) as CompanionSettingsWithHop
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

function onRecruitLimitChange(event: Event) {
  const value = Number((event.target as HTMLInputElement).value)
  if (!Number.isInteger(value) || value < 0) return
  void patchGlobal({ recruit_limit: Math.min(value, 20) })
}

function onMaxBotHopDepthChange(event: Event) {
  const input = event.target as HTMLInputElement
  const raw = Number(input.value)
  if (!Number.isFinite(raw)) return
  const clamped = Math.max(0, Math.min(10, Math.trunc(raw)))
  input.value = String(clamped)
  void patchGlobal({ max_bot_hop_depth: clamped })
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
      <div class="companion-switch-row">
        <div class="companion-switch-copy">
          <strong>{{ t('settings.companions.recruitLimit') }}</strong>
          <p class="companion-hint">{{ t('settings.companions.recruitLimitHint') }}</p>
        </div>
        <input
          class="companion-recruit-limit"
          type="number"
          min="0"
          max="20"
          step="1"
          data-testid="companion-recruit-limit"
          :value="settings?.recruit_limit ?? 3"
          :disabled="!settings || savingGlobal"
          @change="onRecruitLimitChange"
        />
      </div>
      <div class="companion-switch-row">
        <div class="companion-switch-copy">
          <strong>{{ t('settings.companions.maxBotHopDepth') }}</strong>
          <p class="companion-hint">{{ t('settings.companions.maxBotHopDepthHint') }}</p>
        </div>
        <input
          class="companion-recruit-limit"
          type="number"
          min="0"
          max="10"
          step="1"
          data-testid="companion-max-hop-depth"
          :value="maxBotHopDepth"
          :disabled="!settings || savingGlobal"
          @change="onMaxBotHopDepthChange"
        />
      </div>
    </section>

    <PetsSection
      embedded
      :live2d-avatars="avatars"
      :loading-avatars="avatarsLoading"
      :avatars-failure="avatarsFailure"
      @avatars-changed="loadAvatars"
    />

    <section class="companion-list-section">
      <div class="companion-list-head">
        <h3>{{ t('settings.companions.listTitle') }}</h3>
        <div class="companion-list-head-actions">
          <UiButton
            v-if="selectedId"
            variant="outline"
            size="sm"
            data-testid="companion-open-drawer"
            :disabled="!canAccessBackend"
            @click="openDrawerForCompanion(selectedId)"
          >
            <PanelRightOpen :size="14" />
            <span>{{ t('settings.companions.openDrawerEdit') }}</span>
          </UiButton>
          <UiButton size="sm" data-testid="companion-create-open" :disabled="!canAccessBackend" @click="openCreate">
            <Plus :size="14" />
            <span>{{ t('settings.companions.createAction') }}</span>
          </UiButton>
        </div>
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
            <AgentPetAvatar
              class="companion-avatar"
              :pet-slug="companion.avatar ?? ''"
              :avatar-kind="avatarKindOf(companion.avatar, live2dIds) ?? 'sprite'"
              :size="26"
              :alt="companion.name"
            />
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
      v-if="selectedId && !drawerOpen"
      :key="`${selectedId}:${editorRequestSeq}`"
      :companion-id="selectedId"
      :initial-tab="editorTab"
      :live2d-avatars="avatars"
      class="companion-editor-host"
      @saved="loadList"
    />
    <p v-else-if="!selectedId && !loading" class="companion-empty">{{ t('settings.companions.selectCompanion') }}</p>

    <CompanionEditorDrawer
      v-model:open="drawerOpen"
      :companion-id="drawerCompanionId"
      :initial-tab="editorTab"
      :live2d-avatars="avatars"
      @created="onDrawerCreated"
      @saved="onDrawerSaved"
    />
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
  background: color-mix(in srgb, var(--accent-warning) 8%, var(--surface-section, var(--surface-raised)));
  box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--accent-warning) 32%, transparent);
}

.companion-error {
  background: color-mix(in srgb, var(--accent-danger) 8%, var(--surface-section, var(--surface-raised)));
  box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--accent-danger) 32%, transparent);
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

.companion-recruit-limit {
  width: 72px;
  padding: 4px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--surface-input);
  color: var(--text-primary);
  font-size: 12px;
  text-align: right;
}

.companion-list-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.companion-list-head-actions {
  display: flex;
  align-items: center;
  gap: 8px;
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
  background: var(--surface-selected);
  box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--accent-primary) 32%, transparent);
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
  box-shadow: 0 12px 32px rgb(0 0 0 / 35%);
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
