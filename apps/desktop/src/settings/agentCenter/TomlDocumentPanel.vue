<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { FileDown, FileUp, Loader2, RefreshCw, Save } from '@lucide/vue'
import { UiButton, UiSelectField } from '@/components/ui'
import { api } from '@/api'
import type { ConfigurationDocumentDto } from '@/settings/storage'
import { useNotifications } from '@/composables/useNotifications'

const props = withDefaults(defineProps<{ storageId?: string }>(), { storageId: 'user' })
const { t } = useI18n()
const { notify } = useNotifications()

const documents = ref<string[]>([])
const selected = ref('')
const document = ref<ConfigurationDocumentDto | null>(null)
const text = ref('')
const loading = ref(false)
const busy = ref(false)
const editing = ref(false)
const diagnostics = ref<Array<Record<string, unknown>>>([])
const notice = ref('')
const fileInput = ref<HTMLInputElement | null>(null)

const documentOptions = () => documents.value.map(id => ({ value: id, label: id }))

async function reload() {
  loading.value = true
  notice.value = ''
  try {
    const ids = await api.listConfigurationDocuments(props.storageId)
    documents.value = Array.isArray(ids) ? ids : []
    if (!selected.value || !documents.value.includes(selected.value)) {
      selected.value = documents.value[0] ?? ''
    }
    if (selected.value) await loadDocument()
  } catch (error) {
    notify.error(error, { title: t('settings.tomlLoadFailed') })
  } finally {
    loading.value = false
  }
}

async function loadDocument() {
  if (!selected.value) return
  loading.value = true
  notice.value = ''
  diagnostics.value = []
  try {
    const loaded = await api.getConfigurationDocument(props.storageId, selected.value)
    document.value = loaded
    text.value = loaded.text
    editing.value = false
  } catch (error) {
    document.value = null
    notify.error(error, { title: t('settings.tomlLoadFailed') })
  } finally {
    loading.value = false
  }
}

async function validate(): Promise<boolean> {
  if (!selected.value) return false
  busy.value = true
  notice.value = ''
  try {
    const result = await api.validateConfigurationDocument(props.storageId, selected.value, text.value)
    diagnostics.value = result.diagnostics ?? []
    if (result.valid) {
      notice.value = t('settings.tomlValid')
      return true
    }
    notice.value = t('settings.tomlInvalid')
    return false
  } catch (error) {
    notify.error(error, { title: t('settings.tomlInvalid') })
    return false
  } finally {
    busy.value = false
  }
}

async function save() {
  if (!document.value || !await validate()) return
  busy.value = true
  try {
    const saved = await api.saveConfigurationDocument(props.storageId, selected.value, text.value, document.value.content_hash)
    document.value = saved
    text.value = saved.text
    editing.value = false
    notice.value = t('settings.tomlSaved')
  } catch (error) {
    notify.error(error, { title: t('settings.tomlSaveFailed') })
  } finally {
    busy.value = false
  }
}

function exportDocument() {
  if (!document.value) return
  const blob = new Blob([text.value], { type: 'text/plain;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const anchor = window.document.createElement('a')
  anchor.href = url
  anchor.download = selected.value || 'configuration.toml'
  anchor.click()
  URL.revokeObjectURL(url)
}

async function importDocument(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  text.value = await file.text()
  editing.value = true
  notice.value = t('settings.tomlImported')
  input.value = ''
}

onMounted(() => { void reload() })
defineExpose({ reload })
</script>

<template>
  <section class="toml-panel" data-testid="toml-document-panel">
    <div class="toml-head">
      <div>
        <h3>{{ t('settings.tomlTitle') }}</h3>
        <p>{{ t('settings.tomlHint') }}</p>
      </div>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="reload">
        <RefreshCw :size="14" />
        <span>{{ t('settings.refresh') }}</span>
      </UiButton>
    </div>

    <div class="toml-toolbar">
      <UiSelectField
        v-model="selected"
        :aria-label="t('settings.tomlSelect')"
        :options="documentOptions()"
        @update:model-value="loadDocument"
      />
      <span v-if="document" class="toml-hash" :title="document.content_hash">{{ document.content_hash.slice(0, 12) }}</span>
      <UiButton v-if="!editing" variant="outline" size="sm" :disabled="!document || loading" @click="editing = true">
        {{ t('settings.tomlEdit') }}
      </UiButton>
      <template v-else>
        <UiButton variant="outline" size="sm" :disabled="busy" @click="validate">{{ t('settings.tomlValidate') }}</UiButton>
        <UiButton size="sm" :disabled="busy" @click="save">
          <Save :size="14" />
          <span>{{ t('settings.tomlSave') }}</span>
        </UiButton>
        <UiButton variant="ghost" size="sm" :disabled="busy" @click="loadDocument">{{ t('settings.tomlCancel') }}</UiButton>
      </template>
      <UiButton variant="ghost" size="sm" :disabled="!document" @click="exportDocument">
        <FileDown :size="14" />
        <span>{{ t('settings.tomlExport') }}</span>
      </UiButton>
      <UiButton variant="ghost" size="sm" :disabled="loading" @click="fileInput?.click()">
        <FileUp :size="14" />
        <span>{{ t('settings.tomlImport') }}</span>
      </UiButton>
      <input ref="fileInput" type="file" accept=".toml,text/plain" class="toml-file-input" @change="importDocument" />
    </div>

    <div v-if="loading" class="toml-loading" aria-live="polite">
      <Loader2 :size="14" class="center-stale-spinner" aria-hidden="true" />
      <span>{{ t('settings.refreshing') }}</span>
    </div>
    <textarea
      v-else
      v-model="text"
      class="toml-editor"
      :readonly="!editing"
      spellcheck="false"
      :aria-label="t('settings.tomlTitle')"
    ></textarea>

    <p v-if="notice" class="toml-notice">{{ notice }}</p>
    <ul v-if="diagnostics.length" class="toml-diagnostics">
      <li v-for="(diagnostic, index) in diagnostics" :key="index">
        <strong>{{ String(diagnostic.severity ?? 'error') }}</strong>
        <span>{{ String(diagnostic.message ?? JSON.stringify(diagnostic)) }}</span>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.toml-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px;
  border: 1px solid var(--border-muted);
  border-radius: 12px;
  background: var(--surface-section);
}
.toml-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; }
.toml-head h3 { margin: 0 0 4px; }
.toml-head p { margin: 0; color: var(--text-secondary); font-size: 12px; line-height: 1.5; }
.toml-toolbar { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.toml-toolbar .ui-select { min-width: 180px; }
.toml-hash { color: var(--text-muted); font-family: var(--font-mono); font-size: 11px; }
.toml-file-input { display: none; }
.toml-editor {
  width: 100%;
  min-height: 280px;
  padding: 10px 12px;
  border: 1px solid var(--border-muted);
  border-radius: 9px;
  background: var(--surface-input);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 12px;
  line-height: 1.55;
  resize: vertical;
}
.toml-loading { display: flex; align-items: center; gap: 8px; padding: 12px 4px; color: var(--text-secondary); font-size: 12px; }
.toml-notice { margin: 0; color: var(--text-secondary); font-size: 12px; }
.toml-diagnostics { margin: 0; padding-left: 18px; color: var(--accent-danger); font-size: 12px; line-height: 1.5; }
</style>
