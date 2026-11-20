<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { FileDown, FileUp, Loader2, RefreshCw, Save } from '@lucide/vue'
import { UiBadge, UiButton, UiSelectField } from '@/components/ui'
import { api } from '@/api'
import type { ConfigurationDocumentDto } from '@/settings/storage'
import { useNotifications } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'

const props = withDefaults(defineProps<{ storageId?: string }>(), { storageId: 'user' })
const emit = defineEmits<{ changed: [] }>()

const { t } = useI18n()
const { notify, confirm } = useNotifications()
const { canAccessBackend } = useHostAccess()

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

/**
 * Monaco is never imported eagerly, and `@/composables/useMonaco` is not imported at module
 * scope either.
 *
 * That composable pulls `@monaco-editor/loader` and, transitively, `@/monaco.config` — which
 * imports five `?worker` entrypoints. Vite has no worker resolution under happy-dom or jsdom,
 * so importing it while a settings suite renders this panel fails the whole module graph, not
 * just the editor. Both the composable and Monaco itself are therefore reached only through
 * `import()` inside `initMonaco()`, which is gated on `monacoAvailable`.
 *
 * The textarea is a permanent, dependency-free fallback: it renders whenever Monaco is
 * unavailable AND whenever loading Monaco throws, so this panel can never be left without an
 * editor.
 */
function detectTestDom(): boolean {
  if (typeof window === 'undefined') return true
  if (typeof (window as { happyDOM?: unknown }).happyDOM !== 'undefined') return true
  if (typeof (window as { jsdom?: unknown }).jsdom !== 'undefined') return true
  return /happy-dom|jsdom/i.test(window.navigator?.userAgent ?? '')
}

const monacoFallback = ref(detectTestDom())

let disposed = false
let editor: import('monaco-editor').editor.IStandaloneCodeEditor | null = null
let model: import('monaco-editor').editor.ITextModel | null = null

const isDirty = computed(() => activeDocument.value !== null && text.value !== activeDocument.value.text)
const documentOptions = computed(() => documents.value.map((id) => ({ value: id, label: id })))

async function confirmDiscardUnsaved(): Promise<boolean> {
  if (!isDirty.value) return true
  return confirm({
    // `storage.unsavedTitle` / `storage.unsavedMessage` are the existing leaves that talk about
    // unsaved configuration-document text; `settings.unsaved*` only ever existed as `storage.*`.
    title: t('storage.unsavedTitle'),
    message: t('storage.unsavedMessage'),
    confirmLabel: t('settings.discardChanges'),
    cancelLabel: t('settings.keepEditing'),
    destructive: true,
  })
}

async function initMonaco(): Promise<void> {
  if (monacoFallback.value || !monacoHost.value || editor) return
  try {
    // Both of these are imported HERE, not at module scope: neither may be in the module
    // graph of a test that merely mounts the panel (see the `monacoFallback` comment).
    const [{ useMonaco, detectLanguage }] = await Promise.all([
      import('@/composables/useMonaco'),
    ])
    const { getMonaco, isDark } = useMonaco()
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
    // Worker assets missing or the CDN is unreachable: keep the textarea rather than blank.
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

/**
 * Focus a specific configuration document by id.
 *
 * Called by `WorkspaceDefaultsPanel`'s "Edit runtime.toml" button. The list is fetched first
 * because the caller may fire before this panel's own `reload()` finished, and a lookup
 * against an empty list would silently open nothing. Resolution is tolerant of the three
 * spellings Core uses (`runtime`, `runtime.toml`, `runtime.*`) and returns whether it found a
 * match, so a caller (or a test) can tell "opened the wrong document" from "there is no such
 * document" instead of both looking like success.
 */
async function selectDocument(documentId: string): Promise<boolean> {
  if (!canAccessBackend.value || !documentId) return false
  const wanted = documentId.trim().toLowerCase()
  if (!wanted) return false
  if (documents.value.length === 0) {
    await reload()
  }
  const normalized = (id: string) => id.trim().toLowerCase()
  const match =
    documents.value.find((id) => normalized(id) === wanted) ??
    documents.value.find((id) => normalized(id) === `${wanted}.toml`) ??
    documents.value.find((id) => normalized(id).startsWith(`${wanted}.`)) ??
    documents.value.find((id) => normalized(id).startsWith(wanted))
  if (match) {
    // Same document already open and loaded: scroll to it rather than re-reading the file
    // and discarding any in-progress edit behind a confirm dialog.
    if (match === selected.value && activeDocument.value) {
      panelRef.value?.scrollIntoView?.({ behavior: 'smooth', block: 'nearest' })
      return true
    }
    await onSelectDocument(match)
  }
  panelRef.value?.scrollIntoView?.({ behavior: 'smooth', block: 'nearest' })
  return Boolean(match)
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
    <!--
      The textarea is the editor of record whenever Monaco is unavailable, which includes
      every happy-dom/jsdom test and any browser where the worker bundle failed to load.
      It is never conditionally absent, so the panel always has exactly one editing surface.
    -->
    <textarea
      v-if="monacoFallback"
      v-show="!loading"
      v-model="text"
      class="toml-editor"
      data-testid="toml-editor-fallback"
      :readonly="!editing"
      spellcheck="false"
      :aria-label="t('settings.tomlTitle')"
    ></textarea>
    <div
      v-else
      v-show="!loading"
      ref="monacoHost"
      class="toml-monaco-host"
      data-testid="toml-editor-monaco"
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
