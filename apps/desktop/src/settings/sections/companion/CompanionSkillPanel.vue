<script setup lang="ts">
/**
 * Skill bindings for one companion. A manual skill carries its own instructions; a pack binding
 * only records a reference to an installed Agent Pack — this version does not load a pack's
 * instructions, and the panel says so instead of implying the skill will run.
 */
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { PackagePlus, Plus, Trash2 } from '@lucide/vue'
import { api as baseApi, type AgentPackDto } from '@/api'
import { scopedApi } from '@/lib/storageScope'
import { UiBadge, UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { companionApi, type CompanionSkill } from '@/lib/companionApi'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'

const api = scopedApi(baseApi, () => 'user')

const props = defineProps<{ companionId: string }>()
const emit = defineEmits<{ conflict: [] }>()

const { t } = useI18n()
const { confirm, notify } = useNotifications()

const skills = ref<CompanionSkill[]>([])
const packs = ref<AgentPackDto[]>([])
const loading = ref(false)
const failure = ref<ErrorState | null>(null)
const packFailure = ref('')
const busy = ref(false)
const form = reactive({ name: '', instructions: '', packId: '', packVersion: '' })
let readGeneration = 0

const selectedPack = computed(() => packs.value.find((pack) => pack.pack_id === form.packId) ?? null)

function time(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleString() : ''
}

function sourceLabel(kind: string): string {
  return kind === 'pack'
    ? t('settings.companions.skill.sourcePack')
    : t('settings.companions.skill.sourceManual')
}

async function loadSkills() {
  const read = ++readGeneration
  loading.value = true
  failure.value = null
  try {
    const rows = await companionApi.listSkills(props.companionId)
    if (read !== readGeneration) return
    skills.value = rows
  } catch (error) {
    if (read !== readGeneration) return
    failure.value = toErrorState(error, t('settings.companions.skill.loadFailed'))
  } finally {
    if (read === readGeneration) loading.value = false
  }
}

async function loadPacks() {
  try {
    packs.value = await api.listAgentPacks()
    packFailure.value = ''
  } catch (error) {
    packFailure.value = toErrorState(error, t('settings.companions.skill.packLoadFailed')).message
  }
}

onMounted(() => {
  void loadSkills()
  void loadPacks()
})
onBeforeUnmount(() => { readGeneration++ })
watch(() => props.companionId, () => {
  form.name = ''
  form.instructions = ''
  form.packId = ''
  form.packVersion = ''
  void loadSkills()
})

function reportWriteFailure(error: unknown, title: string) {
  if (isCompanionRevisionConflict(error)) { emit('conflict'); return }
  notify.error(error, { title, key: `companion-skill-${props.companionId}` })
}

async function addManualSkill() {
  const name = form.name.trim()
  if (!name || busy.value) return
  busy.value = true
  try {
    await companionApi.addSkill(props.companionId, {
      name,
      source_kind: 'manual',
      instructions: form.instructions.trim() || null,
      enabled: true,
    })
    form.name = ''
    form.instructions = ''
    await loadSkills()
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.skill.saveFailed'))
  } finally {
    busy.value = false
  }
}

async function bindPack() {
  const pack = selectedPack.value
  if (!pack || busy.value) return
  busy.value = true
  try {
    await companionApi.addSkill(props.companionId, {
      name: pack.name ?? pack.pack_id,
      source_kind: 'pack',
      pack_id: pack.pack_id,
      pack_version: form.packVersion || pack.active_version,
      instructions: null,
      enabled: true,
    })
    form.packId = ''
    form.packVersion = ''
    await loadSkills()
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.skill.saveFailed'))
  } finally {
    busy.value = false
  }
}

async function toggleSkill(skill: CompanionSkill) {
  if (busy.value) return
  busy.value = true
  try {
    const updated = await companionApi.patchSkill(props.companionId, skill.id, skill.revision, { enabled: !skill.enabled })
    skills.value = skills.value.map((row) => (row.id === updated.id ? updated : row))
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.skill.saveFailed'))
  } finally {
    busy.value = false
  }
}

async function deleteSkill(skill: CompanionSkill) {
  const confirmed = await confirm({
    title: t('settings.companions.skill.deleteTitle'),
    message: t('settings.companions.skill.deleteMessage'),
    confirmLabel: t('settings.companions.skill.deleteConfirm'),
    cancelLabel: t('common.cancel'),
    destructive: true,
  })
  if (!confirmed) return
  busy.value = true
  try {
    await companionApi.deleteSkill(props.companionId, skill.id, skill.revision)
    await loadSkills()
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.skill.deleteFailed'))
  } finally {
    busy.value = false
  }
}

function onPackChange() {
  const pack = selectedPack.value
  form.packVersion = pack?.active_version ?? ''
}
</script>

<template>
  <div class="companion-panel" role="tabpanel" data-testid="companion-skill-panel">
    <h4>{{ t('settings.companions.skill.title') }}</h4>
    <p class="companion-hint">{{ t('settings.companions.skill.hint') }}</p>

    <div v-if="failure" role="alert" class="companion-error-text">
      <p>{{ failure.message }}</p>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="loadSkills">{{ t('common.retry') }}</UiButton>
    </div>

    <p v-if="loading && skills.length === 0" role="status" class="companion-hint">{{ t('common.loading') }}</p>
    <p v-else-if="!loading && skills.length === 0" class="companion-hint" data-testid="companion-skill-empty">
      {{ t('settings.companions.skill.empty') }}
    </p>
    <ul v-else class="companion-list-reset">
      <li v-for="skill in skills" :key="skill.id" class="companion-card-row" :data-testid="`companion-skill-row-${skill.id}`">
        <p class="companion-row-title">
          <strong>{{ skill.name }}</strong>
          <UiBadge variant="outline">{{ sourceLabel(skill.source_kind) }}</UiBadge>
          <UiBadge v-if="skill.pack_version" variant="secondary">{{ skill.pack_version }}</UiBadge>
          <UiBadge :variant="skill.enabled ? 'default' : 'secondary'">
            {{ skill.enabled ? t('settings.companions.skill.enabled') : t('settings.companions.skill.disabled') }}
          </UiBadge>
        </p>
        <p class="companion-row-meta">
          <span v-if="skill.pack_id">{{ skill.pack_id }}</span>
          <span>
            {{ skill.last_loaded_at
              ? t('settings.companions.skill.lastLoaded', { time: time(skill.last_loaded_at) })
              : t('settings.companions.skill.neverLoaded') }}
          </span>
        </p>
        <p v-if="skill.instructions" class="companion-row-text">{{ skill.instructions }}</p>
        <div class="companion-inline companion-inline-end">
          <UiButton variant="outline" size="sm" :disabled="busy" :data-testid="`companion-skill-toggle-${skill.id}`" @click="toggleSkill(skill)">
            {{ skill.enabled ? t('settings.companions.skill.disable') : t('settings.companions.skill.enable') }}
          </UiButton>
          <UiButton variant="ghost" size="sm" :disabled="busy" :data-testid="`companion-skill-delete-${skill.id}`" @click="deleteSkill(skill)">
            <Trash2 :size="14" />
            <span>{{ t('settings.companions.skill.delete') }}</span>
          </UiButton>
        </div>
      </li>
    </ul>

    <div class="companion-card-row companion-form">
      <h5>{{ t('settings.companions.skill.addManual') }}</h5>
      <div class="companion-field">
        <label for="companion-skill-name">{{ t('settings.companions.skill.manualName') }}</label>
        <input id="companion-skill-name" v-model="form.name" class="settings-select" data-testid="companion-skill-name" type="text" />
      </div>
      <div class="companion-field">
        <label for="companion-skill-instructions">{{ t('settings.companions.skill.instructions') }}</label>
        <textarea id="companion-skill-instructions" v-model="form.instructions" class="settings-textarea" data-testid="companion-skill-instructions" rows="3" />
      </div>
      <div class="companion-inline companion-inline-end">
        <UiButton size="sm" data-testid="companion-skill-add" :disabled="busy || !form.name.trim()" @click="addManualSkill">
          <Plus :size="14" />
          <span>{{ t('settings.companions.skill.addManual') }}</span>
        </UiButton>
      </div>
    </div>

    <div class="companion-card-row companion-form">
      <h5>{{ t('settings.companions.skill.bindPack') }}</h5>
      <p v-if="packFailure" role="alert" class="companion-error-text">{{ packFailure }}</p>
      <p v-else-if="packs.length === 0" class="companion-hint" data-testid="companion-pack-empty">{{ t('settings.companions.skill.packEmpty') }}</p>
      <template v-else>
        <div class="companion-field">
          <label for="companion-skill-pack">{{ t('settings.companions.skill.pack') }}</label>
          <select id="companion-skill-pack" v-model="form.packId" class="settings-select" data-testid="companion-skill-pack" @change="onPackChange">
            <option value="">{{ t('common.selectPlaceholder') }}</option>
            <option v-for="pack in packs" :key="pack.pack_id" :value="pack.pack_id">
              {{ pack.name ?? pack.pack_id }}
            </option>
          </select>
        </div>
        <div class="companion-field">
          <label for="companion-skill-pack-version">{{ t('settings.companions.skill.packVersion') }}</label>
          <input id="companion-skill-pack-version" v-model="form.packVersion" class="settings-select" data-testid="companion-skill-pack-version" type="text" />
          <p class="companion-hint">{{ t('settings.companions.skill.packHint') }}</p>
        </div>
        <div class="companion-inline companion-inline-end">
          <UiButton size="sm" data-testid="companion-skill-bind-pack" :disabled="busy || !form.packId" @click="bindPack">
            <PackagePlus :size="14" />
            <span>{{ t('settings.companions.skill.bindPack') }}</span>
          </UiButton>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.companion-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.companion-panel h4 {
  margin: 0;
  font-size: 13px;
  color: var(--text-primary);
}

.companion-form {
  gap: 8px;
}

.companion-form h5 {
  margin: 0;
  font-size: 12px;
  color: var(--text-primary);
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

.companion-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.companion-field label {
  font-size: 12px;
  color: var(--text-secondary);
}

.companion-inline {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.companion-inline-end {
  justify-content: flex-end;
}

.companion-list-reset {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.companion-card-row {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px;
  border-radius: 8px;
  background: var(--surface-chrome, transparent);
}

.companion-row-title {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin: 0;
  font-size: 12px;
  color: var(--text-primary);
}

.companion-row-text {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-secondary);
  white-space: pre-wrap;
}

.companion-row-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  margin: 0;
  font-size: 11px;
  color: var(--text-muted);
}
</style>
