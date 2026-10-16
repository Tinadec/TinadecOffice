<script setup lang="ts">
import { useHostAccess } from '@/lib/hostAccess'
import { ApiError } from '@/lib/apiError'

import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiButton, UiInput, UiSelectField } from '@/components/ui'
import { api } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import { suspendFollowingStorage } from '@/lib/sessionEventBus'
import { suspendStorageRunStreams } from '@/lib/runStream'
import type { ConfigurationDocumentDto, StorageCleanupPreviewDto, StorageContentPreviewDto, StorageDeletePreviewDto, StorageScopeDto, StorageStatsDto } from '@/settings/storage'
import { recoveryActions, useErrorState, type ErrorState } from '@/composables/useErrorState'
import type { ApiErrorActionKind } from '@/lib/apiError'

const { t, te, locale } = useI18n()
/** A storage or configuration diagnostic in words from its code, keeping Core's specifics (names, lines) where the wording needs them. */
function diagnosticText(item: Record<string, unknown>): string {
  const message = typeof item.message === 'string' ? item.message : ''
  const key = `storage.diagnostic.${item.code}`
  return typeof item.code === 'string' && te(key) ? t(key, { detail: message, line: item.line ?? '', column: item.column ?? '' }) : message || JSON.stringify(item)
}
/** Core's own sentence, as the tooltip, when the line shows a translation of it. */
const diagnosticTitle = (item: Record<string, unknown>) => typeof item.message === 'string' && item.message !== diagnosticText(item) ? item.message : undefined
const categoryLabel = (category: string) => t(`storage.categoryName.${category}`, category)
const formatTime = (iso: string) => { const date = new Date(iso); return Number.isNaN(date.getTime()) ? iso : date.toLocaleString(locale.value) }
const { canAccessBackend, reason: hostReason } = useHostAccess()
const { confirm } = useNotifications()
const scopes = ref<StorageScopeDto[]>([])
const scopeId = ref('user')
const scope = computed(() => scopes.value.find(item => item.storage_id === scopeId.value))
const stats = ref<StorageStatsDto | null>(null)
const preview = ref<StorageCleanupPreviewDto | null>(null)
const contentPreview = ref<StorageContentPreviewDto | null>(null)
const deletePreview = ref<StorageDeletePreviewDto | null>(null)
const document = ref<ConfigurationDocumentDto | null>(null)
const documentId = ref('runtime')
const modules = ['runtime', 'storage', 'logging', 'tools', 'mcp', 'skills', 'models', 'agents', 'prompts']
const text = ref('')
const baseline = ref('')
const dirty = computed(() => text.value !== baseline.value)
const backend = ref('sqlite')
const root = ref('')
const postgresReference = ref('')
const busy = ref(false)
// The full failure (code, category, actions, trace id), not just a sentence. The plain
// `error` string below stays for compact inline slots that only need the message.
const failure = useErrorState()
const error = computed(() => failure.message.value)
const notice = ref('')
const diagnostics = ref<Array<Record<string, unknown>>>([])
const scopeDiagnostics = ref<Array<Record<string, unknown>>>([])
const hostStorage = ref<Awaited<ReturnType<typeof window.tinadec.getAppConfig>>['storage']>()
const closeController = ref<AbortController | null>(null)
let generation = 0
function isCurrent(current: number, captured: string) { return current === generation && captured === scopeId.value && canAccessBackend.value }
function hostAction(storageId: string, action: 'configure' | 'cleanup' | 'content-collect' | 'storage-delete' | 'unregister', input?: Record<string, unknown>) {
  if (!window.tinadec?.storageAction) throw new Error(t('storage.trustedWindowRequired'))
  return window.tinadec.storageAction(storageId, action, input)
}
function bytes(size: number) { return `${(size / 1024 / 1024).toFixed(2)} MiB` }
function restart() { window.tinadec?.restartApp() }
async function canLeave() {
  if (busy.value) return false
  if (!dirty.value) return true
  return await confirm({ title: t('storage.unsavedTitle'), message: t('storage.unsavedMessage'), confirmLabel: t('storage.discardDraft'), destructive: true })
}
defineExpose({ canLeave })
function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value) { event.preventDefault(); event.returnValue = '' } }
async function loadDocument(current = generation) {
  const capturedScope = scopeId.value; const capturedDocument = documentId.value
  const result = await api.getConfigurationDocument(capturedScope, capturedDocument)
  if (current !== generation) return
  document.value = result; text.value = baseline.value = result.text; diagnostics.value = result.diagnostics
}
async function loadContext() {
  const current = ++generation; busy.value = true; failure.clear(); preview.value = null; contentPreview.value = null; deletePreview.value = null; notice.value = ''
  const captured = scopeId.value
  stats.value = null; scopeDiagnostics.value = []; document.value = null
  try {
    if (scope.value?.availability === 'error') {
      const item = scope.value
      throw new ApiError(item.availability_error ?? t('storage.workspaceUnavailable'), 409, { ...item, code: item.availability_code })
    }
    const [result, diagnosis] = await Promise.all([api.getStorageStats(captured), api.getStorageDiagnostics(captured)])
    if (current !== generation) return
    stats.value = result
    scopeDiagnostics.value = [...(diagnosis.diagnostics ?? []), ...result.diagnostics]
    backend.value = scope.value?.backend.toLowerCase() === 'postgresql' ? 'postgresql' : 'sqlite'; root.value = scope.value?.storage_root ?? ''; postgresReference.value = scope.value?.postgres_connection_reference ?? ''
    await loadDocument(current)
  } catch (value) { if (current === generation) failure.set(value) }
  finally { if (current === generation) busy.value = false }
}
async function refresh(preserveDraft = false) {
  if (!canAccessBackend.value) return
  if (!(preserveDraft && dirty.value) && !await canLeave()) return
  const current = ++generation
  busy.value = true; failure.clear()
  try {
    const rows = await api.listStorageScopes()
    if (current !== generation || !canAccessBackend.value) return
    scopes.value = rows
    if (!scope.value && !(preserveDraft && dirty.value)) scopeId.value = 'user'
  } catch (value) { if (current === generation) failure.set(value) }
  finally { if (current === generation) busy.value = false }
  if (current !== generation || error.value) return
  if (preserveDraft && dirty.value) {
    notice.value = t('storage.reconnectedDraftKept')
  } else await loadContext()
}
async function selectScope(next: string) {
  if (next === scopeId.value || !await canLeave()) return
  scopeId.value = next; await loadContext()
}
async function selectDocument(next: string) {
  if (next === documentId.value || !await canLeave()) return
  documentId.value = next; const current = ++generation; busy.value = true; failure.clear()
  try { await loadDocument(current) } catch (value) { if (current === generation) failure.set(value) } finally { if (current === generation) busy.value = false }
}
async function validate() {
  const current = generation
  busy.value = true; failure.clear(); notice.value = ''
  const capturedScope = scopeId.value; const capturedDocument = documentId.value; const draft = text.value
  try { const result = await api.validateConfigurationDocument(capturedScope, capturedDocument, draft); if (current !== generation || text.value !== draft) return; diagnostics.value = result.diagnostics; notice.value = t('storage.validated') }
  catch (value) { if (current === generation) failure.set(value) } finally { if (current === generation) busy.value = false }
}
async function save() {
  if (!document.value || !dirty.value) return
  const current = generation
  const capturedScope = scopeId.value; const capturedDocument = documentId.value; const hash = document.value.content_hash; const draft = text.value
  busy.value = true; failure.clear(); notice.value = ''
  try {
    const result = await api.saveConfigurationDocument(capturedScope, capturedDocument, draft, hash)
    if (current !== generation || text.value !== draft) return
    document.value = result; text.value = baseline.value = result.text; diagnostics.value = result.diagnostics
    notice.value = capturedDocument === 'storage'
      ? t('storage.savedStorage')
      : capturedDocument === 'logging'
      ? t('storage.savedLogging')
      : t('storage.savedNextRun')
  } catch (value) {
    if (current !== generation) return
    failure.set(value)
    if ([409, 412, 428].includes((value as { status?: number }).status ?? 0)) notice.value = t('storage.conflict')
  }
  finally { if (current === generation) busy.value = false }
}
async function configure() {
  const captured = scopeId.value; const current = generation
  if (captured === 'user' && root.value !== scope.value?.storage_root && hostStorage.value && (hostStorage.value.managed || !hostStorage.value.local_services)) {
    failure.set(t('storage.userStorageExternal'))
    return
  }
  if (!await confirm({ title: t('storage.switchTitle'), message: t('storage.switchMessage'), confirmLabel: t('storage.applyConfiguration') }) || !isCurrent(current, captured)) return
  busy.value = true; failure.clear()
  try {
    const result = await hostAction(captured, 'configure', { backend: backend.value, storage_root: root.value, ...(postgresReference.value ? { postgres_connection_reference: postgresReference.value } : {}) }) as StorageScopeDto
    if (!isCurrent(current, captured)) return
    scopes.value = scopes.value.map(item => item.storage_id === captured ? result : item)
    if (result.restart_required) {
      if (result.requested_storage_root && result.requested_storage_root !== result.storage_root) {
        if (!window.tinadec?.saveUserStorageRoot) throw new Error(t('storage.userRootNeedsHost'))
        await window.tinadec.saveUserStorageRoot(result.requested_storage_root)
      }
      notice.value = hostStorage.value?.local_services === false
        ? t('storage.savedRestartExternal')
        : t('storage.savedRestartDesktop')
      return
    }
    await loadContext()
  } catch (value) { if (isCurrent(current, captured)) failure.set(value) } finally { if (isCurrent(current, captured)) busy.value = false }
}
async function writePolicy(event: Event) {
  const current = generation; const captured = scopeId.value
  const input = event.target as HTMLInputElement; const allow = input.checked; input.checked = Boolean(scope.value?.allow_storage_write)
  if (allow && !await confirm({ title: t('storage.grantTitle'), message: t('storage.grantMessage'), confirmLabel: t('storage.grantConfirm') })) return
  if (!isCurrent(current, captured)) return
  busy.value = true; failure.clear()
    try {
      if (!window.tinadec?.setStorageWritePolicy) throw new Error(t('storage.hostRequired'))
      const result = await window.tinadec.setStorageWritePolicy(captured, allow)
      if (!isCurrent(current, captured)) return
      scopes.value = scopes.value.map(item => item.storage_id === captured ? { ...item, ...result } : item)
    }
  catch (value) { if (isCurrent(current, captured)) failure.set(value) } finally { if (isCurrent(current, captured)) busy.value = false }
}
async function previewCleanup(category: string) {
  const current = generation; const captured = scopeId.value
  busy.value = true; failure.clear()
  try { const result = await api.previewStorageCleanup(captured, category); if (isCurrent(current, captured)) preview.value = result }
  catch (value) { if (isCurrent(current, captured)) failure.set(value) } finally { if (isCurrent(current, captured)) busy.value = false }
}
async function cleanup() {
  if (!preview.value || preview.value.storage_id !== scopeId.value) return
  const captured = { ...preview.value }; const current = generation
  busy.value = true; failure.clear()
  try {
    await hostAction(captured.storage_id, 'cleanup', { preview_id: captured.preview_id })
    if (!isCurrent(current, captured.storage_id)) return
    preview.value = null; const result = await api.getStorageStats(captured.storage_id)
    if (isCurrent(current, captured.storage_id)) { stats.value = result; notice.value = t('storage.cleanupDone') }
  } catch (value) { if (isCurrent(current, captured.storage_id)) failure.set(value) } finally { if (isCurrent(current, captured.storage_id)) busy.value = false }
}
async function previewContent() {
  const current = generation; const captured = scopeId.value
  busy.value = true; failure.clear()
  try { const result = await api.previewContentCollection(captured); if (isCurrent(current, captured)) contentPreview.value = result }
  catch (value) { if (isCurrent(current, captured)) failure.set(value) } finally { if (isCurrent(current, captured)) busy.value = false }
}
async function collectContent() {
  if (!contentPreview.value || contentPreview.value.storage_id !== scopeId.value) return
  const captured = { ...contentPreview.value }; const current = generation; busy.value = true; failure.clear()
  try {
    await hostAction(captured.storage_id, 'content-collect', { preview_id: captured.preview_id })
    if (!isCurrent(current, captured.storage_id)) return
    contentPreview.value = null; const result = await api.getStorageStats(captured.storage_id)
    if (isCurrent(current, captured.storage_id)) { stats.value = result; notice.value = t('storage.contentCollected') }
  } catch (value) { if (isCurrent(current, captured.storage_id)) failure.set(value) } finally { if (isCurrent(current, captured.storage_id)) busy.value = false }
}
async function previewDelete() {
  const current = generation; const captured = scopeId.value
  if (captured === 'user' || !await canLeave() || !isCurrent(current, captured)) return
  busy.value = true; failure.clear()
  try { const result = await api.previewStorageDeletion(captured); if (isCurrent(current, captured)) deletePreview.value = result }
  catch (value) { if (isCurrent(current, captured)) failure.set(value) } finally { if (isCurrent(current, captured)) busy.value = false }
}
async function deleteProjectStorage() {
  if (!deletePreview.value || deletePreview.value.storage_id !== scopeId.value) return
  const captured = { ...deletePreview.value }; const current = generation
  if (!await confirm({ title: t('storage.deleteTitle'), message: t('storage.deleteMessage', { path: captured.path }), confirmLabel: t('storage.deleteConfirm'), destructive: true }) || !isCurrent(current, captured.storage_id)) return
  busy.value = true; failure.clear()
  try {
    await hostAction(captured.storage_id, 'storage-delete', { preview_id: captured.preview_id })
    if (!isCurrent(current, captured.storage_id)) return
    const rows = await api.listStorageScopes(); if (!isCurrent(current, captured.storage_id)) return
    scopeId.value = 'user'; scopes.value = rows; await loadContext()
  } catch (value) { if (isCurrent(current, captured.storage_id)) failure.set(value) } finally { if (isCurrent(current, captured.storage_id)) busy.value = false }
}
async function unregisterScope() {
  const captured = scopeId.value; const current = generation
  if (captured === 'user' || !await canLeave() || !isCurrent(current, captured)) return
  if (!await confirm({ title: t('storage.unregisterProject'), message: t('storage.unregisterMessage'), confirmLabel: t('storage.unregister') }) || !isCurrent(current, captured)) return
  busy.value = true; failure.clear()
  try {
    await hostAction(captured, 'unregister'); if (!isCurrent(current, captured)) return
    const rows = await api.listStorageScopes(); if (!isCurrent(current, captured)) return
    scopeId.value = 'user'; scopes.value = rows; await loadContext()
  } catch (value) { if (isCurrent(current, captured)) failure.set(value) } finally { if (isCurrent(current, captured)) busy.value = false }
}
async function exportScope() {
  const captured = scopeId.value; const current = generation; busy.value = true; failure.clear()
  try {
    const result = await api.exportStorageScope(captured); if (!isCurrent(current, captured)) return
    const url = URL.createObjectURL(result.blob); const link = window.document.createElement('a')
    link.href = url; link.download = result.filename; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
  } catch (value) { if (isCurrent(current, captured)) failure.set(value) } finally { if (isCurrent(current, captured)) busy.value = false }
}
async function closeScope() {
  const captured = scopeId.value; const current = generation
  if (captured === 'user' || !await canLeave() || !isCurrent(current, captured)) return
  if (!await confirm({ title: t('storage.closeProjectStorage'), message: t('storage.closeMessage'), confirmLabel: t('storage.closeScope') }) || !isCurrent(current, captured)) return
  busy.value = true; failure.clear(); notice.value = t('storage.closingWait')
  const resumeEvents = suspendFollowingStorage(captured); const resumeRuns = suspendStorageRunStreams(captured)
  const controller = new AbortController(); closeController.value = controller
  try {
    await api.closeStorageScope(captured, controller.signal); if (!isCurrent(current, captured)) return
    const rows = await api.listStorageScopes(); if (!isCurrent(current, captured)) return
    scopeId.value = 'user'; scopes.value = rows; await loadContext()
  } catch (value) { resumeEvents(); resumeRuns(); if (isCurrent(current, captured)) { notice.value = ''; failure.set(controller.signal.aborted ? t('storage.closingCancelled') : value) } }
  finally { if (closeController.value === controller) closeController.value = null; if (isCurrent(current, captured)) busy.value = false }
}
watch(canAccessBackend, ready => {
  if (ready) void refresh(true)
  else { ++generation; closeController.value?.abort(); preview.value = null; contentPreview.value = null; deletePreview.value = null; busy.value = false }
})
onMounted(() => { window.addEventListener('beforeunload', beforeUnload); void window.tinadec?.getAppConfig?.().then(result => { hostStorage.value = result.storage }).catch(() => {}); void refresh() })
onBeforeUnmount(() => { ++generation; closeController.value?.abort(); window.removeEventListener('beforeunload', beforeUnload) })

// Rendered straight from the shared error state so every field the server sent is visible.
const failureState = computed<ErrorState | null>(() => failure.error.value)
/** Only actions this screen can actually perform, labelled from the shared wording. */
const failureRecovery = computed(() => recoveryActions(failure.error.value, {
  retry: () => refresh(),
  reload: () => refresh(),
  unregister_workspace: () => unregisterScope(),
}))
</script>

<template>
  <section class="storage-settings" :aria-busy="busy">
    <div class="model-center-heading"><div><h2>{{ t('storage.title') }}</h2><p>{{ t('storage.description') }}</p></div><UiButton variant="outline" :disabled="busy" @click="refresh()">{{ t('common.refresh') }}</UiButton></div>
    <p v-if="!canAccessBackend" role="status">{{ hostReason }}</p>
    <label class="storage-field">{{ t('storage.scope') }}<UiSelectField data-testid="storage-scope" :value="scopeId" :disabled="busy" :aria-label="t('storage.scope')" :options="scopes.map((item) => ({ value: item.storage_id, label: `${item.scope_kind} · ${item.project_root || item.storage_root}` }))" @change="selectScope" /></label>
    <div v-if="failureState" class="tools-field-error" role="alert">
      <p>{{ failureState.message }}</p>
      <!-- The reason, the actions and the correlation id all belong together: a sentence
           alone is what made "the folder is gone" a dead end. -->
      <p v-if="failureState.details" class="tools-field-error-details">{{ failureState.details }}</p>
      <p v-if="failureState.traceId" class="tools-field-error-trace">trace_id: {{ failureState.traceId }}</p>
      <div v-if="failureRecovery.length" class="tools-field-error-actions">
        <UiButton v-for="action in failureRecovery" :key="action.kind" variant="outline" size="sm" @click="action.run()">{{ action.label }}</UiButton>
      </div>
    </div>
    <p v-if="notice" role="status">{{ notice }}</p>
    <UiButton v-if="closeController" variant="outline" @click="closeController.abort()">{{ t('storage.cancelClosing') }}</UiButton>
    <template v-if="scope">
      <p class="quiet"><code>{{ scope.storage_id }}</code> · {{ scope.backend }} · {{ (scope.external ? t('storage.externalStorage') : t('storage.defaultStorage')) }}</p>
      <p v-if="scopeId === 'user' && hostStorage" class="quiet">{{ t('storage.desktopStartupSource') }}: {{ t(`storage.hostSource.${hostStorage.source}`, hostStorage.source) }} · <code>{{ hostStorage.bootstrap_config }}</code> · {{ t('storage.hostUserRoot') }}: <code>{{ hostStorage.root }}</code><span v-if="hostStorage.managed"> · {{ t('storage.managedByHome') }}</span><span v-if="!hostStorage.local_services"> · {{ t('storage.externalServiceMode') }}</span></p>
      <div class="storage-actions"><UiButton variant="outline" size="sm" :disabled="busy || !canAccessBackend || scope.availability === 'error'" @click="exportScope">{{ t('storage.exportZip') }}</UiButton><UiButton v-if="scopeId !== 'user'" variant="outline" size="sm" :disabled="busy" @click="closeScope">{{ t('storage.closeProjectStorage') }}</UiButton><UiButton v-if="scopeId !== 'user'" variant="outline" size="sm" :disabled="busy || !canAccessBackend" @click="unregisterScope">{{ t('storage.unregisterProject') }}</UiButton><UiButton v-if="scope.restart_required && hostStorage?.local_services !== false" variant="outline" size="sm" :disabled="busy" @click="restart">{{ t('storage.restartToApply') }}</UiButton></div>
      <dl class="storage-paths"><div v-for="(value, key) in scope.paths" :key="key"><dt>{{ categoryLabel(String(key)) }}</dt><dd><code>{{ value }}</code></dd></div></dl>
      <div v-if="scopeDiagnostics.length" class="tools-validation" role="status"><p v-for="(item, index) in scopeDiagnostics" :key="index" :title="diagnosticTitle(item)">{{ diagnosticText(item) }}</p></div>
      <fieldset class="storage-capability-fields" :disabled="!canAccessBackend || scope.availability === 'error'">
      <div class="storage-config"><label class="storage-field">{{ t('storage.backend') }}<UiSelectField v-model="backend" :disabled="busy" :aria-label="t('storage.backend')" :options="[{ value: 'sqlite', label: 'SQLite' }, { value: 'postgresql', label: 'PostgreSQL' }]" /></label><label class="storage-field">{{ t('storage.root') }}<UiInput v-model="root" :disabled="busy" /></label><label v-if="backend === 'postgresql'" class="storage-field">{{ t('storage.postgresReference') }}<UiInput v-model="postgresReference" :disabled="busy" :placeholder="t('storage.secretReference')" /></label><UiButton variant="outline" :disabled="busy || dirty" @click="configure">{{ t('storage.applyStorage') }}</UiButton></div>
      <label v-if="scope.scope_kind === 'project'" class="storage-policy"><input type="checkbox" :checked="scope.allow_storage_write" :disabled="busy" @change="writePolicy" />{{ t('storage.allowAgentWrites') }}</label>
      <h3>{{ t('storage.usageTitle') }}</h3>
      <table v-if="stats" class="storage-table"><thead><tr><th>{{ t('storage.category') }}</th><th>{{ t('storage.sizeFiles') }}</th><th>{{ t('storage.action') }}</th></tr></thead><tbody><tr v-for="category in stats.categories" :key="category.category"><td><strong>{{ categoryLabel(category.category) }}</strong><code>{{ category.path }}</code></td><td>{{ bytes(category.size_bytes) }} / {{ category.file_count }}</td><td><UiButton v-if="category.clearable" variant="outline" size="sm" :disabled="busy" @click="previewCleanup(category.category)">{{ t('storage.previewCleanup') }}</UiButton></td></tr></tbody></table>
      <div v-if="preview" class="tools-validation" role="status"><strong>{{ t('storage.cleanupPreview') }} · {{ categoryLabel(preview.category) }}</strong><p><code>{{ preview.path }}</code></p><p>{{ bytes(preview.size_bytes) }} · {{ preview.file_count }} {{ t('storage.files') }} · {{ t('storage.expires') }} {{ formatTime(preview.expires_at) }}</p><UiButton variant="destructive" :disabled="busy" @click="cleanup">{{ t('storage.executePreview') }}</UiButton><UiButton variant="ghost" :disabled="busy" @click="preview = null">{{ t('common.cancel') }}</UiButton></div>
      <div class="storage-actions"><UiButton variant="outline" size="sm" :disabled="busy" @click="previewContent">{{ t('storage.previewContent') }}</UiButton><UiButton v-if="scope.scope_kind === 'project'" variant="outline" size="sm" :disabled="busy" @click="previewDelete">{{ t('storage.previewDelete') }}</UiButton></div>
      <div v-if="contentPreview" class="tools-validation" role="status"><strong>{{ t('storage.contentPreview') }}</strong><p>{{ bytes(contentPreview.size_bytes) }} · {{ contentPreview.file_count }} {{ t('storage.files') }} · {{ formatTime(contentPreview.expires_at) }}</p><p>{{ t('storage.referenceChecks') }}: <code>{{ JSON.stringify(contentPreview.references) }}</code></p><p>{{ t('storage.referenceChecksHint') }}</p><UiButton variant="destructive" :disabled="busy" @click="collectContent">{{ t('storage.collectPreview') }}</UiButton><UiButton variant="ghost" :disabled="busy" @click="contentPreview = null">{{ t('common.cancel') }}</UiButton></div>
      <div v-if="deletePreview" class="tools-validation" role="alert"><strong>{{ t('storage.deletePreview') }}</strong><p><code>{{ deletePreview.path }}</code></p><p>{{ bytes(deletePreview.size_bytes) }} · {{ deletePreview.file_count }} {{ t('storage.files') }} · {{ formatTime(deletePreview.expires_at) }}</p><UiButton variant="destructive" :disabled="busy" @click="deleteProjectStorage">{{ t('storage.deleteThis') }}</UiButton><UiButton variant="ghost" :disabled="busy" @click="deletePreview = null">{{ t('common.cancel') }}</UiButton></div>
      <h3>{{ t('storage.documentTitle') }}</h3>
      <label class="storage-field">{{ t('storage.module') }}<UiSelectField :value="documentId" :disabled="busy" :aria-label="t('storage.module')" :options="modules.map((module) => ({ value: module, label: `${module}.toml` }))" @change="selectDocument" /></label>
      <p v-if="document" class="quiet"><code>{{ document.path }}</code> · v{{ document.version }} · {{ document.content_hash }}</p>
      <textarea v-model="text" class="storage-editor" :readonly="busy" spellcheck="false" :aria-label="t('storage.documentText')" />
      <div v-if="diagnostics.length" class="tools-validation" role="status"><p v-for="(item, index) in diagnostics" :key="index" :title="diagnosticTitle(item)">{{ diagnosticText(item) }}</p></div>
      <div class="storage-actions"><UiButton variant="outline" :disabled="busy || !document" @click="validate">{{ t('storage.validate') }}</UiButton><UiButton :disabled="busy || !dirty || !document" @click="save">{{ t('storage.save') }}</UiButton><span v-if="dirty" class="quiet">{{ t('storage.unsavedDraft') }}</span></div>
      </fieldset>
    </template>
  </section>
</template>

<style scoped>
.storage-capability-fields { display: grid; gap: 16px; padding: 0; border: 0; margin: 0; min-width: 0; }
.storage-settings { display: grid; gap: 16px; max-width: 1180px; }
.storage-field { display: grid; gap: 8px; min-width: 0; }
.storage-config { display: grid; grid-template-columns: 180px minmax(240px, 1fr); gap: 12px; align-items: end; }
.storage-paths { margin: 0; display: grid; gap: 8px; }
.storage-paths > div { display: grid; grid-template-columns: 100px minmax(0, 1fr); gap: 12px; }
.storage-paths dd { margin: 0; overflow-wrap: anywhere; }
.storage-settings code, .storage-editor { font-family: var(--font-mono); font-size: 12px; }
.storage-table { width: 100%; border-collapse: collapse; text-align: left; font-size: 12px; }
.storage-table th, .storage-table td { padding: 12px 8px; border-bottom: 1px solid var(--border-muted); }
.storage-table td code { display: block; overflow-wrap: anywhere; color: var(--text-muted); margin-top: 4px; }
.storage-editor { width: 100%; min-height: 360px; padding: 12px; border: 1px solid var(--border-default); border-radius: 6px; color: var(--text-primary); background: var(--bg-secondary); resize: vertical; }
.storage-editor:focus-visible, .storage-policy input:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 2px; }
.storage-actions, .storage-policy { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; }
.storage-policy { min-height: 40px; }
.storage-settings h3 { font-size: 15px; font-weight: 600; margin: 8px 0 0; }
@media (max-width: 700px) { .storage-config { grid-template-columns: 1fr; } .storage-paths > div { grid-template-columns: 1fr; gap: 4px; } }
</style>
