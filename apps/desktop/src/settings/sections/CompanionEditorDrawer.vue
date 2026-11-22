<script setup lang="ts">
/**
 * Slide-over drawer for creating or editing a Companion Bot from either `CompanionCenterPage`
 * (`+ New Companion` / Edit action) or `CompanionSection` in Settings.
 *
 * - `companionId` null / omitted -> Create mode (identity, sprite/Live2D avatar, model, persona)
 * - `companionId` non-empty string -> Edit mode (full 5-tab `CompanionEditor`)
 */
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, X } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { useAgentAvatars } from '@/composables/useAgentAvatars'
import { toErrorState } from '@/composables/useErrorState'
import { api } from '@/api'
import { companionApi, type CompanionAvatar, type CompanionDetail } from '@/lib/companionApi'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'
import CompanionEditor from './companion/CompanionEditor.vue'

type EditorTab = 'identity' | 'memory' | 'skills' | 'connections' | 'autonomy'

const props = defineProps<{
  open: boolean
  companionId?: string | null
  initialTab?: EditorTab
  live2dAvatars?: CompanionAvatar[]
}>()

const emit = defineEmits<{
  'update:open': [open: boolean]
  close: []
  saved: [companion: CompanionDetail]
  created: [companion: CompanionDetail]
}>()

const { t } = useI18n()
const { pool: avatarPool } = useAgentAvatars()

const PERSONA_LIMIT_BYTES = 64 * 1024

const fetchedAvatars = ref<CompanionAvatar[]>([])
const resolvedAvatars = computed(() => props.live2dAvatars ?? fetchedAvatars.value)

interface ModelOption { value: string; label: string }
const modelOptions = ref<ModelOption[]>([])

const creating = ref(false)
const createError = ref('')
const createForm = reactive({
  name: '',
  tagline: '',
  avatar: '',
  model: '',
  persona_document: '',
})

const personaBytes = computed(() => new TextEncoder().encode(createForm.persona_document).length)
const personaTooLong = computed(() => personaBytes.value > PERSONA_LIMIT_BYTES)

function resetCreateForm(): void {
  createForm.name = ''
  createForm.tagline = ''
  createForm.avatar = ''
  createForm.model = ''
  createForm.persona_document = ''
  createError.value = ''
}

async function ensureCreateReferenceData(): Promise<void> {
  if (props.live2dAvatars === undefined) {
    try {
      fetchedAvatars.value = await companionApi.listCompanionAvatars()
    } catch {
      fetchedAvatars.value = []
    }
  }
  if (!props.companionId && typeof api.listModelProviders === 'function') {
    try {
      const providers = await api.listModelProviders()
      const options: ModelOption[] = []
      for (const provider of providers) {
        if (!provider.enabled) continue
        const models = provider.models?.length ? provider.models : provider.model ? [provider.model] : []
        if (models.length === 0) {
          options.push({ value: `${provider.id}|`, label: provider.display_name })
          continue
        }
        for (const model of models) {
          options.push({ value: `${provider.id}|${model}`, label: `${provider.display_name} · ${model}` })
        }
      }
      modelOptions.value = options
    } catch {
      modelOptions.value = []
    }
  }
}

watch(
  () => [props.open, props.companionId] as const,
  ([isOpen, id]) => {
    if (!isOpen) return
    if (!id) resetCreateForm()
    void ensureCreateReferenceData()
  },
  { immediate: true },
)

function closeDrawer(): void {
  if (creating.value) return
  emit('update:open', false)
  emit('close')
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && props.open) {
    closeDrawer()
  }
}

onMounted(() => {
  window.addEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
})

async function submitCreate(): Promise<void> {
  const name = createForm.name.trim()
  if (!name) {
    createError.value = t('settings.companions.createNameRequired')
    return
  }
  if (personaTooLong.value) return
  creating.value = true
  createError.value = ''
  try {
    const created = await companionApi.createCompanion({
      name,
      tagline: createForm.tagline.trim() || null,
      avatar: createForm.avatar.trim() || null,
      model: createForm.model || null,
      persona_document: createForm.persona_document || null,
      model_purpose: 'chat',
    })
    emit('created', created)
    emit('saved', created)
    creating.value = false
    closeDrawer()
  } catch (error) {
    if (isCompanionRevisionConflict(error)) {
      createError.value = t('settings.companions.conflictBody')
    } else {
      createError.value = toErrorState(error, t('settings.companions.createFailed')).message
    }
  } finally {
    creating.value = false
  }
}
</script>

<template>
  <div
    v-if="open"
    class="companion-drawer-backdrop"
    data-testid="companion-editor-drawer-backdrop"
    @click.self="closeDrawer"
  >
    <aside
      class="companion-drawer"
      role="dialog"
      aria-modal="true"
      :aria-label="companionId ? t('settings.companions.title') : t('settings.companions.createTitle')"
      data-testid="companion-editor-drawer"
    >
      <header class="companion-drawer-header">
        <div class="companion-drawer-title">
          <Bot :size="16" aria-hidden="true" />
          <h3>{{ companionId ? t('settings.companions.title') : t('settings.companions.createTitle') }}</h3>
        </div>
        <button
          type="button"
          class="companion-drawer-close"
          data-testid="companion-drawer-close"
          :aria-label="t('common.close')"
          @click="closeDrawer"
        >
          <X :size="15" />
        </button>
      </header>

      <div class="companion-drawer-body">
        <CompanionEditor
          v-if="companionId"
          :key="companionId"
          :companion-id="companionId"
          :initial-tab="initialTab"
          :live2d-avatars="resolvedAvatars"
          @saved="emit('saved', $event)"
        />

        <form v-else class="companion-create-drawer-form" @submit.prevent="submitCreate">
          <div class="companion-field">
            <label for="drawer-create-name">{{ t('settings.companions.createName') }}</label>
            <input
              id="drawer-create-name"
              v-model="createForm.name"
              class="settings-select"
              data-testid="drawer-create-name"
              type="text"
            />
          </div>

          <div class="companion-field">
            <label for="drawer-create-tagline">{{ t('settings.companions.createTagline') }}</label>
            <input
              id="drawer-create-tagline"
              v-model="createForm.tagline"
              class="settings-select"
              data-testid="drawer-create-tagline"
              type="text"
            />
          </div>

          <div class="companion-field">
            <label>{{ t('settings.companions.avatar') }}</label>
            <div class="companion-avatar-choices" role="radiogroup" :aria-label="t('settings.companions.avatar')">
              <label class="companion-avatar-choice" :class="{ active: createForm.avatar === '' }">
                <input v-model="createForm.avatar" type="radio" value="" name="drawer-companion-avatar" />
                <span class="companion-avatar-choice-face">
                  <AgentPetAvatar :size="28" :alt="t('settings.companions.avatarDefault')" />
                </span>
                <span class="companion-avatar-choice-name">{{ t('settings.companions.avatarDefault') }}</span>
              </label>
              <label
                v-for="pet in avatarPool"
                :key="pet.slug"
                class="companion-avatar-choice"
                :class="{ active: createForm.avatar === pet.slug }"
              >
                <input v-model="createForm.avatar" type="radio" :value="pet.slug" name="drawer-companion-avatar" />
                <span class="companion-avatar-choice-face">
                  <AgentPetAvatar :pet-slug="pet.slug" :size="28" :alt="pet.displayName" />
                </span>
                <span class="companion-avatar-choice-name">{{ pet.displayName }}</span>
              </label>
              <label
                v-for="model in resolvedAvatars"
                :key="model.id"
                class="companion-avatar-choice"
                :class="{ active: createForm.avatar === model.id }"
                :data-testid="`drawer-avatar-live2d-${model.id}`"
              >
                <input v-model="createForm.avatar" type="radio" :value="model.id" name="drawer-companion-avatar" />
                <span class="companion-avatar-choice-face">Live2D</span>
                <span class="companion-avatar-choice-name">{{ model.name }}</span>
              </label>
            </div>
          </div>

          <div class="companion-field">
            <label for="drawer-create-model">{{ t('settings.companions.model') }}</label>
            <select
              id="drawer-create-model"
              v-model="createForm.model"
              class="settings-select"
              data-testid="drawer-create-model"
            >
              <option value="">{{ t('settings.companions.modelDefault') }}</option>
              <option v-for="option in modelOptions" :key="option.value" :value="option.value">
                {{ option.label }}
              </option>
            </select>
          </div>

          <div class="companion-field">
            <label for="drawer-create-persona">{{ t('settings.companions.persona') }}</label>
            <textarea
              id="drawer-create-persona"
              v-model="createForm.persona_document"
              class="settings-textarea"
              data-testid="drawer-create-persona"
              rows="6"
            />
            <p class="companion-hint">
              {{ t('settings.companions.personaHint', { limit: PERSONA_LIMIT_BYTES }) }}
            </p>
            <p v-if="personaTooLong" role="alert" class="companion-error-text">
              {{ t('settings.companions.personaTooLong', { limit: PERSONA_LIMIT_BYTES }) }}
            </p>
          </div>

          <p v-if="createError" role="alert" class="companion-error-text" data-testid="drawer-create-error">
            {{ createError }}
          </p>

          <div class="companion-drawer-footer">
            <UiButton variant="ghost" size="sm" type="button" :disabled="creating" @click="closeDrawer">
              {{ t('common.cancel') }}
            </UiButton>
            <UiButton
              size="sm"
              type="submit"
              data-testid="drawer-create-submit"
              :disabled="creating || personaTooLong"
            >
              {{ t('settings.companions.createConfirm') }}
            </UiButton>
          </div>
        </form>
      </div>
    </aside>
  </div>
</template>

<style scoped>
.companion-drawer-backdrop {
  position: fixed;
  inset: 0;
  display: flex;
  justify-content: flex-end;
  background: rgb(0 0 0 / 40%);
  z-index: 60;
}

.companion-drawer {
  display: flex;
  flex-direction: column;
  width: min(560px, 94vw);
  height: 100%;
  background: var(--surface-base, var(--surface-raised));
  border-left: 1px solid var(--border-muted);
  box-shadow: -8px 0 28px rgb(0 0 0 / 28%);
}

.companion-drawer-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 14px 16px;
  border-bottom: 1px solid var(--border-muted);
}

.companion-drawer-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.companion-drawer-title h3 {
  margin: 0;
  font-size: 15px;
  color: var(--text-primary);
}

.companion-drawer-close {
  width: 28px;
  height: 28px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.companion-drawer-close:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.companion-drawer-body {
  flex: 1;
  overflow-y: auto;
  padding: 16px;
}

.companion-create-drawer-form {
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

.companion-avatar-choices {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(72px, 1fr));
  gap: 8px;
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

.companion-avatar-choice-face {
  font-size: 11px;
  color: var(--text-secondary);
}

.companion-avatar-choice-name {
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11px;
  color: var(--text-secondary);
}

.companion-drawer-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding-top: 6px;
}
</style>
