<script setup lang="ts">
/**
 * A companion's private memories. Reads and writes always go through this companion's id, so a
 * search can only ever answer with its own rows; deleting re-reads the list instead of hiding a
 * row locally, which is also what makes the entry disappear from later searches.
 */
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Plus, Search, Trash2 } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { companionApi, type CompanionMemory } from '@/lib/companionApi'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'

const props = defineProps<{ companionId: string }>()
const emit = defineEmits<{ conflict: [] }>()

const { t } = useI18n()
const { confirm, notify } = useNotifications()

const memories = ref<CompanionMemory[]>([])
const query = ref('')
const loading = ref(false)
const failure = ref<ErrorState | null>(null)
const busy = ref(false)
const draft = ref('')
const editingId = ref<string | null>(null)
const editingContent = ref('')
let readGeneration = 0

function time(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleString() : ''
}

function sourceLabel(kind: string): string {
  if (kind === 'user') return t('settings.companions.memory.sourceUser')
  if (kind === 'companion_run') return t('settings.companions.memory.sourceCompanionRun')
  if (kind === 'group_message') return t('settings.companions.memory.sourceGroupMessage')
  return kind
}

function indexLabel(status: string): string {
  if (status === 'indexed') return t('settings.companions.memory.indexIndexed')
  if (status === 'pending') return t('settings.companions.memory.indexPending')
  if (status === 'deleted') return t('settings.companions.memory.indexDeleted')
  return status
}

async function load() {
  const read = ++readGeneration
  loading.value = true
  failure.value = null
  try {
    const rows = await companionApi.listMemories(props.companionId, query.value.trim() || undefined)
    if (read !== readGeneration) return
    memories.value = rows
  } catch (error) {
    if (read !== readGeneration) return
    failure.value = toErrorState(error, t('settings.companions.memory.loadFailed'))
  } finally {
    if (read === readGeneration) loading.value = false
  }
}

onMounted(() => { void load() })
onBeforeUnmount(() => { readGeneration++ })
watch(() => props.companionId, () => { void load() })

function search() {
  void load()
}

function clearSearch() {
  query.value = ''
  void load()
}

function reportWriteFailure(error: unknown, title: string) {
  if (isCompanionRevisionConflict(error)) { emit('conflict'); return }
  notify.error(error, { title, key: `companion-memory-${props.companionId}` })
}

async function addMemory() {
  const content = draft.value.trim()
  if (!content || busy.value) return
  busy.value = true
  try {
    await companionApi.addMemory(props.companionId, { content, source_kind: 'user' })
    draft.value = ''
    await load()
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.memory.saveFailed'))
  } finally {
    busy.value = false
  }
}

function beginEdit(memory: CompanionMemory) {
  editingId.value = memory.id
  editingContent.value = memory.content
}

function cancelEdit() {
  editingId.value = null
  editingContent.value = ''
}

async function saveEdit(memory: CompanionMemory) {
  const content = editingContent.value.trim()
  if (!content || busy.value) return
  busy.value = true
  try {
    await companionApi.patchMemory(props.companionId, memory.id, memory.revision, { content })
    cancelEdit()
    await load()
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.memory.saveFailed'))
  } finally {
    busy.value = false
  }
}

async function deleteMemory(memory: CompanionMemory) {
  const confirmed = await confirm({
    title: t('settings.companions.memory.deleteTitle'),
    message: t('settings.companions.memory.deleteMessage'),
    confirmLabel: t('settings.companions.memory.deleteConfirm'),
    cancelLabel: t('common.cancel'),
    destructive: true,
  })
  if (!confirmed) return
  busy.value = true
  try {
    await companionApi.deleteMemory(props.companionId, memory.id, memory.revision)
    if (editingId.value === memory.id) cancelEdit()
    await load()
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.memory.deleteFailed'))
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="companion-panel" role="tabpanel" data-testid="companion-memory-panel">
    <h4>{{ t('settings.companions.memory.title') }}</h4>
    <p class="companion-hint">{{ t('settings.companions.memory.hint') }}</p>

    <div class="companion-inline">
      <input
        v-model="query"
        class="settings-select"
        type="search"
        data-testid="companion-memory-search"
        :aria-label="t('settings.companions.memory.search')"
        :placeholder="t('settings.companions.memory.search')"
        @keydown.enter.prevent="search"
      />
      <UiButton variant="outline" size="sm" data-testid="companion-memory-search-submit" :disabled="loading" @click="search">
        <Search :size="14" />
        <span>{{ t('settings.companions.memory.searchAction') }}</span>
      </UiButton>
      <UiButton v-if="query" variant="ghost" size="sm" :disabled="loading" @click="clearSearch">
        {{ t('settings.companions.memory.clearSearch') }}
      </UiButton>
    </div>

    <div v-if="failure" role="alert" class="companion-error-text">
      <p>{{ failure.message }}</p>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="load">{{ t('common.retry') }}</UiButton>
    </div>

    <div class="companion-field">
      <label for="companion-memory-draft">{{ t('settings.companions.memory.addTitle') }}</label>
      <textarea id="companion-memory-draft" v-model="draft" class="settings-textarea" data-testid="companion-memory-draft" rows="3" />
      <div class="companion-inline companion-inline-end">
        <UiButton size="sm" data-testid="companion-memory-add" :disabled="busy || !draft.trim()" @click="addMemory">
          <Plus :size="14" />
          <span>{{ t('settings.companions.memory.add') }}</span>
        </UiButton>
      </div>
    </div>

    <p v-if="loading && memories.length === 0" role="status" class="companion-hint">{{ t('common.loading') }}</p>
    <p v-else-if="!loading && memories.length === 0" class="companion-hint" data-testid="companion-memory-empty">
      {{ query ? t('settings.companions.memory.emptySearch') : t('settings.companions.memory.empty') }}
    </p>
    <ul v-else class="companion-list-reset">
      <li v-for="memory in memories" :key="memory.id" class="companion-card-row" :data-testid="`companion-memory-row-${memory.id}`">
        <template v-if="editingId === memory.id">
          <textarea v-model="editingContent" class="settings-textarea" rows="3" :data-testid="`companion-memory-edit-${memory.id}`" />
          <div class="companion-inline companion-inline-end">
            <UiButton variant="ghost" size="sm" :disabled="busy" @click="cancelEdit">{{ t('common.cancel') }}</UiButton>
            <UiButton size="sm" :disabled="busy || !editingContent.trim()" :data-testid="`companion-memory-save-${memory.id}`" @click="saveEdit(memory)">
              {{ t('settings.companions.memory.save') }}
            </UiButton>
          </div>
        </template>
        <template v-else>
          <p class="companion-row-text" :data-testid="`companion-memory-content-${memory.id}`">{{ memory.content }}</p>
          <p class="companion-row-meta">
            <span>{{ t('settings.companions.memory.source') }}: {{ sourceLabel(memory.source_kind) }}</span>
            <span>{{ t('settings.companions.memory.index') }}: {{ indexLabel(memory.index_status) }}</span>
            <span>{{ t('settings.companions.memory.created', { time: time(memory.created_at) }) }}</span>
            <span>{{ t('settings.companions.memory.visibility') }}</span>
          </p>
          <div class="companion-inline companion-inline-end">
            <UiButton variant="outline" size="sm" :disabled="busy" :data-testid="`companion-memory-edit-${memory.id}`" @click="beginEdit(memory)">
              {{ t('settings.companions.memory.edit') }}
            </UiButton>
            <UiButton variant="ghost" size="sm" :disabled="busy" :data-testid="`companion-memory-delete-${memory.id}`" @click="deleteMemory(memory)">
              <Trash2 :size="14" />
              <span>{{ t('settings.companions.memory.delete') }}</span>
            </UiButton>
          </div>
        </template>
      </li>
    </ul>
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

.companion-row-text {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-primary);
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
