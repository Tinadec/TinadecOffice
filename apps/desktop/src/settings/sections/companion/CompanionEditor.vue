<script setup lang="ts">
/**
 * One companion's editor. Every save carries the revision Core returned last: on success the
 * response becomes the new truth, on 412 the editor reports the conflict and offers a reload
 * instead of overwriting the newer revision. Panels below own their own row lists.
 */
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RefreshCw } from '@lucide/vue'
import { UiBadge, UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { useAgentAvatars } from '@/composables/useAgentAvatars'
import { api } from '@/api'
import { companionApi, type CompanionDetail } from '@/lib/companionApi'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'
import CompanionMemoryPanel from './CompanionMemoryPanel.vue'
import CompanionSkillPanel from './CompanionSkillPanel.vue'
import CompanionConnectionPanel from './CompanionConnectionPanel.vue'
import CompanionAutonomyPanel from './CompanionAutonomyPanel.vue'

const props = defineProps<{ companionId: string; initialTab?: EditorTab }>()

const { t } = useI18n()
const { notify } = useNotifications()

type EditorTab = 'identity' | 'memory' | 'skills' | 'connections' | 'autonomy'

const detail = ref<CompanionDetail | null>(null)
const loading = ref(false)
const failure = ref<ErrorState | null>(null)
const conflict = ref(false)
const activeTab = ref<EditorTab>(props.initialTab ?? 'identity')
const savingIdentity = ref(false)
let readGeneration = 0

/** Core enforces 64 KiB of UTF-8 persona bytes; the editor measures the same thing. */
const PERSONA_LIMIT_BYTES = 64 * 1024
const identity = reactive({
  name: '',
  tagline: '',
  avatar: '',
  /** "" = follow the tenant's default route; otherwise "<providerInstanceId>|<model>". */
  model: '',
  persona_document: '',
})

const tabs: EditorTab[] = ['identity', 'memory', 'skills', 'connections', 'autonomy']

// A second deep link to the same companion does not remount the editor (the key is the id), so
// the requested panel has to be followed here as well as at mount.
watch(() => props.initialTab, (tab) => {
  if (tab) activeTab.value = tab
})

/** Literal keys per branch: a dynamic path would type-check nowhere and translation-gate nowhere. */
function tabLabel(tab: EditorTab): string {
  switch (tab) {
    case 'memory': return t('settings.companions.tabMemory')
    case 'skills': return t('settings.companions.tabSkills')
    case 'connections': return t('settings.companions.tabConnections')
    case 'autonomy': return t('settings.companions.tabAutonomy')
    default: return t('settings.companions.tabIdentity')
  }
}

const personaBytes = computed(() => new TextEncoder().encode(identity.persona_document).length)
const personaTooLong = computed(() => personaBytes.value > PERSONA_LIMIT_BYTES)
const canSaveIdentity = computed(() => detail.value !== null && !savingIdentity.value && !personaTooLong.value
  && identity.name.trim().length > 0)

const statusLabel = computed(() => {
  const status = detail.value?.status
  if (status === 'archived') return t('settings.companions.archivedBadge')
  if (status === 'paused') return t('settings.companions.pausedBadge')
  return t('settings.companions.activeBadge')
})

// ── face (Petdex pool) ──
const { pool: avatarPool } = useAgentAvatars()

// ── model choices ──
interface ModelOption { value: string; label: string }
const modelOptions = ref<ModelOption[]>([])
const modelOptionsFailed = ref(false)

async function loadModelOptions() {
  try {
    const providers = await api.listModelProviders()
    const options: ModelOption[] = []
    for (const provider of providers) {
      if (!provider.enabled) continue
      const models = provider.models?.length ? provider.models : provider.model ? [provider.model] : []
      if (models.length === 0) {
        // A CLI runtime picks its own model; pinning the provider alone is still meaningful.
        options.push({ value: `${provider.id}|`, label: provider.display_name })
        continue
      }
      for (const model of models) {
        options.push({ value: `${provider.id}|${model}`, label: `${provider.display_name} · ${model}` })
      }
    }
    modelOptions.value = options
    modelOptionsFailed.value = false
  } catch {
    modelOptions.value = []
    modelOptionsFailed.value = true
  }
}

function seedIdentity(next: CompanionDetail) {
  identity.name = next.name
  identity.tagline = next.tagline ?? ''
  identity.avatar = next.avatar ?? ''
  identity.model = next.model_provider_id ? `${next.model_provider_id}|${next.model ?? ''}` : ''
  identity.persona_document = next.persona_document ?? ''
}

async function load() {
  const read = ++readGeneration
  loading.value = true
  failure.value = null
  conflict.value = false
  try {
    const next = await companionApi.getCompanion(props.companionId)
    if (read !== readGeneration) return
    detail.value = next
    seedIdentity(next)
  } catch (error) {
    if (read !== readGeneration) return
    failure.value = toErrorState(error, t('settings.companions.loadFailed'))
  } finally {
    if (read === readGeneration) loading.value = false
  }
}

onMounted(() => {
  void load()
  void loadModelOptions()
})
onBeforeUnmount(() => { readGeneration++ })
watch(() => props.companionId, () => { void load() })

async function reload() {
  await load()
}

function reportWriteFailure(error: unknown, title: string) {
  if (isCompanionRevisionConflict(error)) {
    conflict.value = true
    return
  }
  notify.error(error, { title, key: `companion-editor-${props.companionId}` })
}

async function saveIdentity() {
  const current = detail.value
  if (!current || personaTooLong.value) return
  savingIdentity.value = true
  conflict.value = false
  try {
    const updated = await companionApi.patchCompanion(current.id, current.revision, {
      name: identity.name.trim(),
      tagline: identity.tagline.trim() || null,
      avatar: identity.avatar.trim() || null,
      model: identity.model,
      persona_document: identity.persona_document,
    })
    detail.value = updated
    seedIdentity(updated)
    notify.success({ title: t('settings.companions.saved'), message: t('settings.companions.saved') })
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.saveFailed'))
  } finally {
    savingIdentity.value = false
  }
}

function applyUpdatedCompanion(updated: CompanionDetail) {
  detail.value = updated
  seedIdentity(updated)
}
</script>

<template>
  <section class="companion-editor" data-testid="companion-editor">
    <div v-if="loading && !detail" class="companion-editor-loading" role="status">
      {{ t('common.loading') }}
    </div>
    <div v-else-if="failure" role="alert" class="companion-editor-error">
      <p>{{ failure.message }}</p>
      <UiButton variant="outline" size="sm" @click="reload">{{ t('common.retry') }}</UiButton>
    </div>

    <template v-else-if="detail">
      <header class="companion-editor-head">
        <AgentPetAvatar class="companion-editor-avatar" :pet-slug="detail.avatar ?? ''" :size="34" :alt="detail.name" />
        <div class="companion-editor-copy">
          <h3 data-testid="companion-editor-name">{{ detail.name }}</h3>
          <p class="companion-editor-meta">
            <UiBadge variant="outline">{{ statusLabel }}</UiBadge>
            <span data-testid="companion-editor-revision">{{ t('settings.companions.revision', { revision: detail.revision }) }}</span>
          </p>
        </div>
        <UiButton variant="outline" size="sm" data-testid="companion-editor-reload" :disabled="loading" @click="reload">
          <RefreshCw :size="14" />
          <span>{{ t('settings.companions.reload') }}</span>
        </UiButton>
      </header>

      <div v-if="conflict" role="alert" class="companion-conflict" data-testid="companion-conflict">
        <p><strong>{{ t('settings.companions.conflictTitle') }}</strong> {{ t('settings.companions.conflictBody') }}</p>
        <UiButton variant="outline" size="sm" :disabled="loading" @click="reload">{{ t('settings.companions.reload') }}</UiButton>
      </div>

      <div class="companion-tabs" role="tablist">
        <button
          v-for="tab in tabs"
          :key="tab"
          type="button"
          role="tab"
          class="companion-tab"
          :class="{ active: activeTab === tab }"
          :aria-selected="activeTab === tab"
          :data-testid="`companion-tab-${tab}`"
          @click="activeTab = tab"
        >
          {{ tabLabel(tab) }}
        </button>
      </div>

      <div v-if="activeTab === 'identity'" class="companion-panel" role="tabpanel">
        <div class="companion-field">
          <label for="companion-identity-name">{{ t('settings.companions.name') }}</label>
          <input id="companion-identity-name" v-model="identity.name" class="settings-select" data-testid="companion-identity-name" type="text" />
        </div>
        <div class="companion-field">
          <label for="companion-identity-tagline">{{ t('settings.companions.tagline') }}</label>
          <input id="companion-identity-tagline" v-model="identity.tagline" class="settings-select" data-testid="companion-identity-tagline" type="text" />
        </div>
        <div class="companion-field">
          <label>{{ t('settings.companions.avatar') }}</label>
          <div class="companion-avatar-choices" role="radiogroup" :aria-label="t('settings.companions.avatar')">
            <label class="companion-avatar-choice" :class="{ active: identity.avatar === '' }">
              <input v-model="identity.avatar" type="radio" value="" name="companion-avatar" />
              <span class="companion-avatar-choice-face"><AgentPetAvatar :size="30" :alt="t('settings.companions.avatarDefault')" /></span>
              <span class="companion-avatar-choice-name">{{ t('settings.companions.avatarDefault') }}</span>
            </label>
            <label
              v-for="pet in avatarPool"
              :key="pet.slug"
              class="companion-avatar-choice"
              :class="{ active: identity.avatar === pet.slug }"
            >
              <input v-model="identity.avatar" type="radio" :value="pet.slug" name="companion-avatar" />
              <span class="companion-avatar-choice-face"><AgentPetAvatar :pet-slug="pet.slug" :size="30" :alt="pet.displayName" /></span>
              <span class="companion-avatar-choice-name">{{ pet.displayName }}</span>
            </label>
          </div>
          <p class="companion-hint">{{ t('settings.companions.avatarHint') }}</p>
        </div>
        <div class="companion-field">
          <label for="companion-identity-model">{{ t('settings.companions.model') }}</label>
          <select
            id="companion-identity-model"
            v-model="identity.model"
            class="settings-select"
            data-testid="companion-identity-model"
          >
            <option value="">{{ t('settings.companions.modelDefault') }}</option>
            <option v-for="option in modelOptions" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
          <p v-if="modelOptionsFailed" class="companion-hint">{{ t('settings.companions.modelListFailed') }}</p>
          <p v-else class="companion-hint">{{ t('settings.companions.modelHint') }}</p>
        </div>
        <div class="companion-field">
          <label for="companion-identity-persona">{{ t('settings.companions.persona') }}</label>
          <textarea
            id="companion-identity-persona"
            v-model="identity.persona_document"
            class="settings-textarea"
            data-testid="companion-identity-persona"
            rows="8"
          />
          <p class="companion-hint">
            {{ t('settings.companions.personaHint', { limit: PERSONA_LIMIT_BYTES }) }}
            <span data-testid="companion-persona-bytes">{{ personaBytes }}</span>
          </p>
          <p v-if="personaTooLong" role="alert" class="companion-error-text" data-testid="companion-persona-too-long">
            {{ t('settings.companions.personaTooLong', { limit: PERSONA_LIMIT_BYTES }) }}
          </p>
        </div>
        <div class="companion-panel-actions">
          <UiButton size="sm" data-testid="companion-identity-save" :disabled="!canSaveIdentity" @click="saveIdentity">
            {{ t('settings.companions.saveIdentity') }}
          </UiButton>
        </div>
      </div>

      <CompanionMemoryPanel v-else-if="activeTab === 'memory'" :companion-id="detail.id" @conflict="conflict = true" />
      <CompanionSkillPanel v-else-if="activeTab === 'skills'" :companion-id="detail.id" @conflict="conflict = true" />
      <CompanionConnectionPanel v-else-if="activeTab === 'connections'" :companion-id="detail.id" @conflict="conflict = true" />
      <CompanionAutonomyPanel
        v-else-if="activeTab === 'autonomy'"
        :companion="detail"
        @updated="applyUpdatedCompanion"
        @conflict="conflict = true"
      />
    </template>
  </section>
</template>

<style scoped>
.companion-editor {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 14px;
  border-radius: 10px;
  background: var(--surface-section, var(--surface-raised));
}

.companion-editor-loading {
  font-size: 12px;
  color: var(--text-muted);
}

.companion-editor-error {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  font-size: 12px;
  color: var(--text-secondary);
}

.companion-editor-error p {
  margin: 0;
  flex: 1;
  min-width: 200px;
}

.companion-editor-head {
  display: flex;
  align-items: center;
  gap: 10px;
}

.companion-avatar-choices {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(76px, 1fr));
  gap: 8px;
  max-width: 520px;
}

.companion-avatar-choice {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 8px 4px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-chrome, transparent);
  cursor: pointer;
  text-align: center;
}

.companion-avatar-choice input {
  position: absolute;
  width: 1px;
  height: 1px;
  opacity: 0;
  pointer-events: none;
}

.companion-avatar-choice.active {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
}

.companion-avatar-choice-name {
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11px;
  color: var(--text-secondary);
}

.companion-editor-copy {
  flex: 1;
  min-width: 0;
}

.companion-editor-copy h3 {
  margin: 0;
  font-size: 14px;
  color: var(--text-primary);
}

.companion-editor-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 2px 0 0;
  font-size: 11px;
  color: var(--text-muted);
}

.companion-conflict {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--surface-chrome, transparent);
  box-shadow: inset 3px 0 0 var(--accent-warning);
  font-size: 12px;
  color: var(--text-secondary);
}

.companion-conflict p {
  margin: 0;
  flex: 1;
  min-width: 200px;
}

.companion-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.companion-tab {
  padding: 4px 10px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  cursor: pointer;
}

.companion-tab.active {
  background: var(--surface-input);
  color: var(--accent-primary);
}

.companion-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
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

.companion-hint {
  margin: 0;
  font-size: 11px;
  line-height: 1.5;
  color: var(--text-muted);
}

.companion-error-text {
  margin: 0;
  font-size: 12px;
  color: var(--accent-danger);
}

.companion-panel-actions {
  display: flex;
  justify-content: flex-end;
}
</style>
