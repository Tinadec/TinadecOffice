<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { FileDown, FileUp, Loader2, RefreshCw, Save } from '@lucide/vue'
import { UiBadge, UiButton, UiSelectField } from '@/components/ui'
import { api } from '@/api'
import type { ConfigurationDocumentDto } from '@/settings/storage'
import { useNotifications } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'
import { detectLanguage, useMonaco } from '@/composables/useMonaco'

const props = withDefaults(defineProps<{ storageId?: string }>(), { storageId: 'user' })
const emit = defineEmits<{ changed: [] }>()

const { t } = useI18n()
const { notify, confirm } = useNotifications()
const { canAccessBackend } = useHostAccess()
const { getMonaco, isDark } = useMonaco()

const panelRef = ref<HTMLElement | null>(null)
const monacoHost = ref<HTMLDivElement | null>(null)
const documents = ref<string[]>([])
const selected = ref('')
const activeDocument = ref<ConfigurationDocumentDto | null>(null)
const text = ref('')
const loading = ref(false)
const busy = ref(false)
const editing = ref(false)
const diagnostics = ref<Array<Record<string, unknown>>>([])
const notice = ref('')
const fileInput = ref<HTMLInputElement | null>(null)
const monacoFallback = ref(
  typeof window === 'undefined' || /happy-dom|jsdom/i.test(window.navigator?.userAgent ?? ''),
)

let disposed = false
let editor: import('monaco-editor').editor.IStandaloneCodeEditor | null = null
let model: import('monaco-editor').editor.ITextModel | null = null

const isDirty = computed(() => activeDocument.value !== null && text.value !== activeDocument.value.text)
const documentOptions = computed(() => documents.value.map((id) => ({ value: id, label: id })))

async function confirmDiscardUnsaved(): Promise<boolean> {
  if (!isDirty.value) return true
  return confirm({
    title: t('settings.unsavedTitle'),
    message: t('settings.unsavedMessage'),
    confirmLabel: t('settings.discardChanges'),
    cancelLabel: t('settings.keepEditing'),
    destructive: true,
  })
}

async function initMonaco(): Promise<void> {
  if (monacoFallback.value || !monacoHost.value || editor) return
  try {
    const monaco = await getMonaco()
    if (disposed || !monacoHost.value) return
    model = monaco.editor.createModel(text.value, detectLanguage(selected.value || 'config.toml'))
    editor = monaco.editor.create(monacoHost.value, {
      model,
      readOnly: !editing.value,
      theme: isDark.value ? 'vs-dark' : 'vs',
      ariaLabel: t('settings.tomlTitle'),
      automaticLayout: true,
      minimap: { enabled: false },
      fontSize: 12,
      lineNumbers: 'on',
      tabSize: 2,
      scrollBeyondLastLine: false,
      wordWrap: 'on',
      padding: { top: 10 },
    })
    editor.onDidChangeModelContent(() => {
      if (model) {
        const nextValue = model.getValue()
        if (nextValue !== text.value) text.value = nextValue
      }
    })
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
      if (editing.value && !busy.value) void save()
    })
  } catch {
    if (!disposed) monacoFallback.value = true
  }
}

watch(text, (nextValue) => {
  if (model && model.getValue() !== nextValue) {
    model.setValue(nextValue)
  }
})

watch(editing, (isEditing) => {
  editor?.updateOptions({ readOnly: !isEditing })
})

watch(monacoHost, (el) => {
  if (el && !monacoFallback.value && !editor) {
    void initMonaco()
  }
})

async function reload() {
  if (!canAccessBackend.value) return
  if (!(await confirmDiscardUnsaved())) return
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
  if (!canAccessBackend.value || !selected.value) return
  loading.value = true
  notice.value = ''
  diagnostics.value = []
  try {
    const loaded = await api.getConfigurationDocument(props.storageId, selected.value)
    activeDocument.value = loaded
    text.value = loaded.text
    editing.value = false
  } catch (error) {
    activeDocument.value = null
    notify.error(error, { title: t('settings.tomlLoadFailed') })
  } finally {
    loading.value = false
  }
}

async function onSelectDocument(nextId: string) {
  if (!nextId) return
  if (nextId === selected.value && activeDocument.value) return
  if (!(await confirmDiscardUnsaved())) return
  selected.value = nextId
  await loadDocument()
}

async function cancelEdit() {
  if (!(await confirmDiscardUnsaved())) return
  if (activeDocument.value) {
    text.value = activeDocument.value.text
  }
  editing.value = false
  diagnostics.value = []
  notice.value = ''
}

async function selectDocument(documentId: string) {
  if (!canAccessBackend.value) return
  if (documents.value.length === 0) {
    await reload()
  }
  const match = documents.value.find(
    (id) => id === documentId || id === `${documentId}.toml` || id.startsWith(`${documentId}.`),
  )
  if (match) {
    await onSelectDocument(match)
  }
  panelRef.value?.scrollIntoView?.({ behavior: 'smooth', block: 'nearest' })
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
  if (!activeDocument.value || !(await validate())) return
  busy.value = true
  try {
    const saved = await api.saveConfigurationDocument(props.storageId, selected.value, text.value, activeDocument.value.content_hash)
    activeDocument.value = saved
    text.value = saved.text
    editing.value = false
    notice.value = t('settings.tomlSaved')
    emit('changed')
  } catch (error) {
    notify.error(error, { title: t('settings.tomlSaveFailed') })
  } finally {
    busy.value = false
  }
}

function exportDocument() {
  if (!activeDocument.value) return
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

onMounted(() => {
  void reload()
  if (!monacoFallback.value) void initMonaco()
})
watch(canAccessBackend, (ready) => { if (ready) void reload() })
onBeforeUnmount(() => {
  disposed = true
  editor?.dispose()
  model?.dispose()
  editor = null
  model = null
})
defineExpose({ reload, selectDocument })
</script>

<template>
  <section ref="panelRef" class="toml-panel" data-testid="toml-document-panel">
    <div class="toml-head">
      <div>
        <h3>{{ t('settings.tomlTitle') }}</h3>
        <p>{{ t('settings.tomlHint') }}</p>
      </div>
      <UiButton variant="outline" size="sm" :disabled="loading || !canAccessBackend" @click="reload">
        <RefreshCw :size="14" />
        <span>{{ t('settings.refresh') }}</span>
      </UiButton>
    </div>

    <div class="toml-toolbar">
      <UiSelectField
        :model-value="selected"
        :aria-label="t('settings.tomlSelect')"
        :placeholder="t('settings.tomlSelect')"
        :disabled="!canAccessBackend || loading || documents.length === 0"
        :options="documentOptions"
        @update:model-value="onSelectDocument"
      />
      <span v-if="activeDocument" class="toml-hash" :title="activeDocument.content_hash">{{ activeDocument.content_hash.slice(0, 12) }}</span>
      <UiBadge v-if="isDirty" variant="secondary">{{ t('settings.tomlUnsaved') }}</UiBadge>
      <UiButton v-if="!editing" variant="outline" size="sm" :disabled="!activeDocument || loading" @click="editing = true">
        {{ t('settings.tomlEdit') }}
      </UiButton>
      <template v-else>
        <UiButton variant="outline" size="sm" :disabled="busy" @click="validate">{{ t('settings.tomlValidate') }}</UiButton>
        <UiButton size="sm" :disabled="busy" @click="save">
          <Save :size="14" />
          <span>{{ t('settings.tomlSave') }}</span>
        </UiButton>
        <UiButton variant="ghost" size="sm" :disabled="busy" @click="cancelEdit">{{ t('settings.tomlCancel') }}</UiButton>
      </template>
      <UiButton variant="ghost" size="sm" :disabled="!activeDocument" @click="exportDocument">
        <FileDown :size="14" />
        <span>{{ t('settings.tomlExport') }}</span>
      </UiButton>
      <UiButton variant="ghost" size="sm" :disabled="loading || !activeDocument" @click="fileInput?.click()">
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
      v-if="monacoFallback"
      v-show="!loading"
      v-model="text"
      class="toml-editor"
      :readonly="!editing"
      spellcheck="false"
      :aria-label="t('settings.tomlTitle')"
    ></textarea>
    <div
      v-else
      v-show="!loading"
      ref="monacoHost"
      class="toml-monaco-host"
      :aria-label="t('settings.tomlTitle')"
    />

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
.toml-monaco-host {
  width: 100%;
  height: 320px;
  border: 1px solid var(--border-muted);
  border-radius: 9px;
  overflow: hidden;
  background: var(--surface-input);
}
.toml-loading { display: flex; align-items: center; gap: 8px; padding: 12px 4px; color: var(--text-secondary); font-size: 12px; }
.toml-notice { margin: 0; color: var(--text-secondary); font-size: 12px; }
.toml-diagnostics { margin: 0; padding-left: 18px; color: var(--accent-danger); font-size: 12px; line-height: 1.5; }
</style>
