<script setup lang="ts">
import { useHostAccess } from '@/lib/hostAccess'
import { ApiError } from '@/lib/apiError'

import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiButton, UiInput } from '@/components/ui'
import { api } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import { suspendFollowingStorage } from '@/lib/sessionEventBus'
import { suspendStorageRunStreams } from '@/lib/runStream'
import type { ConfigurationDocumentDto, StorageCleanupPreviewDto, StorageContentPreviewDto, StorageDeletePreviewDto, StorageScopeDto, StorageStatsDto } from '@/settings/storage'
import { recoveryActions, useErrorState, type ErrorState } from '@/composables/useErrorState'
import type { ApiErrorActionKind } from '@/lib/apiError'

const { locale } = useI18n()
const { canAccessBackend, reason: hostReason } = useHostAccess()
const { confirm } = useNotifications()
const zh = computed(() => locale.value.startsWith('zh'))
const label = (cn: string, en: string) => zh.value ? cn : en
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
  if (!window.tinadec?.storageAction) throw new Error(label('此写入需要可信主窗口宿主。远程或 Web 客户端需使用显式可信宿主凭据。', 'This change requires the trusted main host window. Remote or web clients need an explicitly trusted host credential.'))
  return window.tinadec.storageAction(storageId, action, input)
}
function bytes(size: number) { return `${(size / 1024 / 1024).toFixed(2)} MiB` }
function restart() { window.tinadec?.restartApp() }
async function canLeave() {
  if (busy.value) return false
  if (!dirty.value) return true
  return await confirm({ title: label('未保存的 TOML 配置', 'Unsaved TOML configuration'), message: label('离开会放弃当前草稿。', 'Leaving discards the current draft.'), confirmLabel: label('放弃草稿', 'Discard draft'), destructive: true })
}
defineExpose({ canLeave })
function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value) { event.preventDefault(); event.returnValue = '' } }
async function loadDocument(current = generation) {
  const capturedScope = scopeId.value; const capturedDocument = documentId.value
  const result = await api.getConfigurationDocument(capturedScope, capturedDocument)
  if (current !== generation) return
  document.value = result; text.value = baseline.value = result.text; diagnostics.value = result.diagnostics
}
/**
 * Usage is measured, not read: `StatisticsAsync` walks every owned directory and stats each file,
 * and the user scope here is thousands of files. On the critical path that was seconds of an empty
 * panel on every open and every scope switch, so it is now an explicit action — the reader asked
 * for it, or the section was opened. Everything else on this page is cheap and paints at once.
 */
const statisticsLoading = ref(false)
const statisticsLoaded = ref(false)
/** Marks the shown numbers as older than the last change the reader made. */
const statisticsStale = ref(false)
/**
 * Identifies the in-flight measurement. Only the request that owns the current token may clear the
 * loading flag: keying that off `generation` alone meant a superseded request left the flag set (it
 * was no longer "current"), and the new page's measure button stayed disabled with no way back.
 */
let statisticsToken = 0
async function loadStatistics() {
  const captured = scopeId.value
  if (!canAccessBackend.value) return
  const token = ++statisticsToken
  statisticsLoading.value = true
  try {
    const result = await api.getStorageStats(captured)
    if (token !== statisticsToken || captured !== scopeId.value || !canAccessBackend.value) return
    stats.value = result
    statisticsLoaded.value = true
    statisticsStale.value = false
    scopeDiagnostics.value = [...scopeDiagnostics.value.filter(item => item.code !== 'storage_unreadable'), ...result.diagnostics]
  } catch (value) {
    if (token !== statisticsToken) return
    // Keep the last measurement on screen and say it is stale rather than clearing it: a refresh
    // that failed must not look like a scope with no usage, and the reader keeps the retry.
    if (stats.value) statisticsStale.value = true
    else failure.set(value)
  } finally { if (token === statisticsToken) statisticsLoading.value = false }
}

/**
 * Re-measure after a change the reader made. The caller invalidated the cached walk server-side, so
 * the next read is authoritative; if it fails, the previous numbers stay but are marked stale.
 */
async function remeasure() {
  if (!statisticsLoaded.value) return
  await loadStatistics()
}

async function loadContext() {
  // Supersede any in-flight measurement before the page changes under it; leaving the flag set is
  // what disabled the next page's measure button.
  statisticsToken++
  statisticsLoading.value = false
  const current = ++generation; busy.value = true; failure.clear(); preview.value = null; contentPreview.value = null; deletePreview.value = null; notice.value = ''
  const captured = scopeId.value
  stats.value = null; statisticsLoaded.value = false; statisticsStale.value = false; scopeDiagnostics.value = []; document.value = null
  try {
    if (scope.value?.availability === 'error') {
      const item = scope.value
      throw new ApiError(item.availability_error ?? '工作区不可用', 409, { ...item, code: item.availability_code })
    }
    // The two reads that are not a filesystem walk, and one does not depend on the other.
    const [diagnosis, documentResult] = await Promise.all([
      api.getStorageDiagnostics(captured),
      api.getConfigurationDocument(captured, documentId.value),
    ])
    if (current !== generation) return
    scopeDiagnostics.value = diagnosis.diagnostics ?? []
    document.value = documentResult; text.value = baseline.value = documentResult.text; diagnostics.value = documentResult.diagnostics
    backend.value = scope.value?.backend.toLowerCase() === 'postgresql' ? 'postgresql' : 'sqlite'; root.value = scope.value?.storage_root ?? ''; postgresReference.value = scope.value?.postgres_connection_reference ?? ''
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
    notice.value = label('连接已恢复，草稿保留。重新加载配置前请先保存或放弃草稿。', 'Connection restored. Your draft is preserved; save or discard it before reloading the configuration.')
  } else await loadContext()
}
async function selectScope(event: Event) {
  const element = event.target as HTMLSelectElement; const next = element.value; element.value = scopeId.value
  if (!await canLeave()) return
  scopeId.value = next; await loadContext()
}
async function selectDocument(event: Event) {
  const element = event.target as HTMLSelectElement; const next = element.value; element.value = documentId.value
  if (!await canLeave()) return
  documentId.value = next; const current = ++generation; busy.value = true; failure.clear()
  try { await loadDocument(current) } catch (value) { if (current === generation) failure.set(value) } finally { if (current === generation) busy.value = false }
}
async function validate() {
  const current = generation
  busy.value = true; failure.clear(); notice.value = ''
  const capturedScope = scopeId.value; const capturedDocument = documentId.value; const draft = text.value
  try { const result = await api.validateConfigurationDocument(capturedScope, capturedDocument, draft); if (current !== generation || text.value !== draft) return; diagnostics.value = result.diagnostics; notice.value = label('校验完成，请检查诊断。', 'Validation complete. Review the diagnostics.') }
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
      ? label('配置已保存。请显式应用存储配置；用户作用域需要重启。文件修改不会自动切换已挂载后端。', 'Configuration saved. Explicitly apply storage configuration; the user scope requires a restart. Editing the file does not switch a mounted backend automatically.')
      : capturedDocument === 'logging'
      ? label('配置已保存。请重启宿主以应用日志配置。', 'Configuration saved. Restart the host to apply logging configuration.')
      : label('配置已保存。下一次运行将捕获新的配置版本。', 'Configuration saved. The next run captures the new configuration version.')
  } catch (value) {
    if (current !== generation) return
    failure.set(value)
    if ([409, 412, 428].includes((value as { status?: number }).status ?? 0)) notice.value = label('配置已被其他操作修改。草稿保留，请重新加载后比较。', 'Another operation changed the configuration. Your draft is preserved; reload and compare.')
  }
  finally { if (current === generation) busy.value = false }
}
async function configure() {
  const captured = scopeId.value; const current = generation
  if (captured === 'user' && root.value !== scope.value?.storage_root && hostStorage.value && (hostStorage.value.managed || !hostStorage.value.local_services)) {
    failure.set(label('用户存储由外部环境或服务管理，请在启动环境中修改并重启相应宿主。', 'User storage is managed by the environment or an external service. Change the startup configuration and restart that host.'))
    return
  }
  if (!await confirm({ title: label('切换存储配置', 'Switch storage configuration'), message: label('只切换后续读写位置，不复制或删除原有数据。作用域必须空闲。', 'This switches future reads and writes. Existing data is retained. The scope must be idle.'), confirmLabel: label('应用配置', 'Apply configuration') }) || !isCurrent(current, captured)) return
  busy.value = true; failure.clear()
  try {
    const result = await hostAction(captured, 'configure', { backend: backend.value, storage_root: root.value, ...(postgresReference.value ? { postgres_connection_reference: postgresReference.value } : {}) }) as StorageScopeDto
    if (!isCurrent(current, captured)) return
    scopes.value = scopes.value.map(item => item.storage_id === captured ? result : item)
    if (result.restart_required) {
      if (result.requested_storage_root && result.requested_storage_root !== result.storage_root) {
        if (!window.tinadec?.saveUserStorageRoot) throw new Error(label('用户根变更需要可信桌面宿主。', 'Changing the user root requires the trusted desktop host.'))
        await window.tinadec.saveUserStorageRoot(result.requested_storage_root)
      }
      notice.value = hostStorage.value?.local_services === false
        ? label('配置已保存；请重启外部服务宿主以应用变更。', 'Configuration saved. Restart the external service host to apply the change.')
        : label('配置已保存；请重启桌面以应用用户存储变更。原数据保留。', 'Configuration saved. Restart the desktop to apply the user storage change. Existing data is retained.')
      return
    }
    await loadContext()
  } catch (value) { if (isCurrent(current, captured)) failure.set(value) } finally { if (isCurrent(current, captured)) busy.value = false }
}
async function writePolicy(event: Event) {
  const current = generation; const captured = scopeId.value
  const input = event.target as HTMLInputElement; const allow = input.checked; input.checked = Boolean(scope.value?.allow_storage_write)
  if (allow && !await confirm({ title: label('授予 Agent 存储写入范围', 'Grant Agent storage write access'), message: label('允许 Agent 的文件工具写入整个项目存储目录，包括配置和数据。此范围仅由可信宿主授予。', 'Allow Agent file tools to write the entire project storage directory, including configuration and data. Only the trusted host grants this access.'), confirmLabel: label('授予范围', 'Grant access') })) return
  if (!isCurrent(current, captured)) return
  busy.value = true; failure.clear()
    try {
      if (!window.tinadec?.setStorageWritePolicy) throw new Error(label('此操作需要可信桌面宿主。', 'This operation requires the trusted desktop host.'))
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
    preview.value = null
    if (isCurrent(current, captured.storage_id)) { await remeasure(); notice.value = label('清理完成。', 'Cleanup complete.') }
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
    contentPreview.value = null
    if (isCurrent(current, captured.storage_id)) { await remeasure(); notice.value = label('未被引用的内容已回收。', 'Unreferenced content collected.') }
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
  if (!await confirm({ title: label('删除整个项目存储', 'Delete the entire project storage'), message: label('永久删除此预览的配置和数据：' + captured.path + '。项目源码目录保留。', 'Permanently delete the configuration and data in this preview: ' + captured.path + '. The project source directory is retained.'), confirmLabel: label('永久删除项目存储', 'Permanently delete project storage'), destructive: true }) || !isCurrent(current, captured.storage_id)) return
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
  if (!await confirm({ title: label('取消项目登记', 'Unregister project'), message: label('从宿主登记中移除此项目。磁盘上的项目存储和源码均保留。', 'Remove this project from the host registry. Its storage and source files remain on disk.'), confirmLabel: label('取消登记', 'Unregister') }) || !isCurrent(current, captured)) return
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
  if (!await confirm({ title: label('关闭项目存储', 'Close project storage'), message: label('关闭作用域连接，保留所有配置和数据。关闭会等待当前运行和请求结束；请关闭此项目的其他浮窗，以释放实时连接。', 'Close the scope connection and retain all configuration and data. Closing waits for current runs and requests; close other windows showing this project to release live connections.'), confirmLabel: label('关闭作用域', 'Close scope') }) || !isCurrent(current, captured)) return
  busy.value = true; failure.clear(); notice.value = label('正在等待项目运行和连接结束。', 'Waiting for project runs and connections to finish.')
  const resumeEvents = suspendFollowingStorage(captured); const resumeRuns = suspendStorageRunStreams(captured)
  const controller = new AbortController(); closeController.value = controller
  try {
    await api.closeStorageScope(captured, controller.signal); if (!isCurrent(current, captured)) return
    const rows = await api.listStorageScopes(); if (!isCurrent(current, captured)) return
    scopeId.value = 'user'; scopes.value = rows; await loadContext()
  } catch (value) { resumeEvents(); resumeRuns(); if (isCurrent(current, captured)) { notice.value = ''; failure.set(controller.signal.aborted ? label('关闭已取消，项目连接恢复。', 'Closing cancelled. Project connections resumed.') : value) } }
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
    <div class="model-center-heading"><div><h2>{{ label('存储与配置', 'Storage and configuration') }}</h2><p>{{ label('以实际作用域路径为准。配置、持久数据与可清理缓存分别管理。', 'Inspect actual scope paths. Configuration, durable data, and disposable cache have separate ownership.') }}</p></div><UiButton variant="outline" :disabled="busy" @click="refresh()">{{ label('刷新', 'Refresh') }}</UiButton></div>
    <p v-if="!canAccessBackend" role="status">{{ hostReason }}</p>
    <label class="storage-field">{{ label('存储作用域', 'Storage scope') }}<select :value="scopeId" class="settings-select" :disabled="busy" @change="selectScope"><option v-for="item in scopes" :key="item.storage_id" :value="item.storage_id">{{ item.scope_kind }} · {{ item.project_root || item.storage_root }}</option></select></label>
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
    <UiButton v-if="closeController" variant="outline" @click="closeController.abort()">{{ label('取消关闭', 'Cancel closing') }}</UiButton>
    <template v-if="scope">
      <p class="quiet"><code>{{ scope.storage_id }}</code> · {{ scope.backend }} · {{ label(scope.external ? '外部存储' : '默认存储', scope.external ? 'External storage' : 'Default storage') }}</p>
      <p v-if="scopeId === 'user' && hostStorage" class="quiet">{{ label('桌面启动来源', 'Desktop startup source') }}: {{ hostStorage.source }} · <code>{{ hostStorage.bootstrap_config }}</code> · {{ label('宿主用户根', 'Host user root') }}: <code>{{ hostStorage.root }}</code><span v-if="hostStorage.managed"> · {{ label('TINADEC_HOME 管理', 'Managed by TINADEC_HOME') }}</span><span v-if="!hostStorage.local_services"> · {{ label('外部服务模式', 'External service mode') }}</span></p>
      <div class="storage-actions"><UiButton variant="outline" size="sm" :disabled="busy || !canAccessBackend || scope.availability === 'error'" @click="exportScope">{{ label('导出存储 ZIP', 'Export storage ZIP') }}</UiButton><UiButton v-if="scopeId !== 'user'" variant="outline" size="sm" :disabled="busy" @click="closeScope">{{ label('关闭项目存储', 'Close project storage') }}</UiButton><UiButton v-if="scopeId !== 'user'" variant="outline" size="sm" :disabled="busy || !canAccessBackend" @click="unregisterScope">{{ label('取消项目登记', 'Unregister project') }}</UiButton><UiButton v-if="scope.restart_required && hostStorage?.local_services !== false" variant="outline" size="sm" :disabled="busy" @click="restart">{{ label('重启以应用配置', 'Restart to apply configuration') }}</UiButton></div>
      <dl class="storage-paths"><div v-for="(value, key) in scope.paths" :key="key"><dt>{{ key }}</dt><dd><code>{{ value }}</code></dd></div></dl>
      <div v-if="scopeDiagnostics.length" class="tools-validation" role="status"><p v-for="(item, index) in scopeDiagnostics" :key="index">{{ item.message || JSON.stringify(item) }}</p></div>
      <fieldset class="storage-capability-fields" :disabled="!canAccessBackend || scope.availability === 'error'">
      <div class="storage-config"><label class="storage-field">{{ label('持久化后端', 'Persistence backend') }}<select v-model="backend" class="settings-select" :disabled="busy"><option value="sqlite">SQLite</option><option value="postgresql">PostgreSQL</option></select></label><label class="storage-field">{{ label('存储根目录（绝对路径）', 'Storage root (absolute path)') }}<UiInput v-model="root" :disabled="busy" /></label><label v-if="backend === 'postgresql'" class="storage-field">{{ label('PostgreSQL 连接引用', 'PostgreSQL connection reference') }}<UiInput v-model="postgresReference" :disabled="busy" placeholder="secret reference" /></label><UiButton variant="outline" :disabled="busy || dirty" @click="configure">{{ label('应用存储配置', 'Apply storage configuration') }}</UiButton></div>
      <label v-if="scope.scope_kind === 'project'" class="storage-policy"><input type="checkbox" :checked="scope.allow_storage_write" :disabled="busy" @change="writePolicy" />{{ label('允许 Agent 写入整个项目存储目录', 'Allow Agent writes to the entire project storage directory') }}</label>
      <h3>{{ label('使用量与清理', 'Usage and cleanup') }}</h3>
      <!-- Measuring walks every owned file, so it runs when asked for rather than on open.
           Everything above this line has already painted by the time this button appears. The
           button stays after a first measurement: a stale figure has to have a way back. -->
      <div class="storage-actions">
        <UiButton v-if="!statisticsLoaded || statisticsStale" variant="outline" size="sm" :disabled="statisticsLoading || busy || !canAccessBackend" data-testid="storage-measure" @click="loadStatistics()">
          {{ statisticsLoading ? label('正在统计…', 'Measuring…') : statisticsStale ? label('重新统计', 'Measure again') : label('统计使用量', 'Measure usage') }}
        </UiButton>
        <span v-if="!statisticsLoaded" class="quiet">{{ label('需要遍历全部存储文件，可能耗时数秒。', 'Walks every stored file and can take a few seconds.') }}</span>
        <!-- A failed refresh keeps the last numbers and says so, rather than looking like a scope
             that holds nothing. -->
        <span v-if="statisticsStale" class="quiet" role="status" data-testid="statistics-stale">{{ label('上次统计后存储已变化，数字可能已过期。', 'Storage changed since this measurement; the numbers may be out of date.') }}</span>
      </div>
      <table v-if="stats" class="storage-table"><thead><tr><th>{{ label('分类', 'Category') }}</th><th>{{ label('大小 / 文件数', 'Size / files') }}</th><th>{{ label('操作', 'Action') }}</th></tr></thead><tbody><tr v-for="category in stats.categories" :key="category.category"><td><strong>{{ category.category }}</strong><code>{{ category.path }}</code></td><td>{{ bytes(category.size_bytes) }} / {{ category.file_count }}</td><td><UiButton v-if="category.clearable" variant="outline" size="sm" :disabled="busy" @click="previewCleanup(category.category)">{{ label('预览清理', 'Preview cleanup') }}</UiButton></td></tr></tbody></table>
      <div v-if="preview" class="tools-validation" role="status"><strong>{{ label('清理预览', 'Cleanup preview') }} · {{ preview.category }}</strong><p><code>{{ preview.path }}</code></p><p>{{ bytes(preview.size_bytes) }} · {{ preview.file_count }} {{ label('文件', 'files') }} · {{ label('有效期至', 'Expires') }} {{ preview.expires_at }}</p><UiButton variant="destructive" :disabled="busy" @click="cleanup">{{ label('确认执行此预览', 'Execute this preview') }}</UiButton><UiButton variant="ghost" :disabled="busy" @click="preview = null">{{ label('取消', 'Cancel') }}</UiButton></div>
      <div class="storage-actions"><UiButton variant="outline" size="sm" :disabled="busy" @click="previewContent">{{ label('预览内容回收', 'Preview content collection') }}</UiButton><UiButton v-if="scope.scope_kind === 'project'" variant="outline" size="sm" :disabled="busy" @click="previewDelete">{{ label('预览删除整个项目存储', 'Preview deletion of project storage') }}</UiButton></div>
      <div v-if="contentPreview" class="tools-validation" role="status"><strong>{{ label('未引用内容回收预览', 'Unreferenced content collection preview') }}</strong><p>{{ bytes(contentPreview.size_bytes) }} · {{ contentPreview.file_count }} {{ label('文件', 'files') }} · {{ contentPreview.expires_at }}</p><p>{{ label('引用检查', 'Reference checks') }}: <code>{{ JSON.stringify(contentPreview.references) }}</code></p><p>{{ label('执行时再次检查引用；活动运行和请求会阻止回收。', 'References are checked again on execution. Active runs and requests block collection.') }}</p><UiButton variant="destructive" :disabled="busy" @click="collectContent">{{ label('确认回收此预览', 'Collect this preview') }}</UiButton><UiButton variant="ghost" :disabled="busy" @click="contentPreview = null">{{ label('取消', 'Cancel') }}</UiButton></div>
      <div v-if="deletePreview" class="tools-validation" role="alert"><strong>{{ label('整个项目存储删除预览', 'Entire project storage deletion preview') }}</strong><p><code>{{ deletePreview.path }}</code></p><p>{{ bytes(deletePreview.size_bytes) }} · {{ deletePreview.file_count }} {{ label('文件', 'files') }} · {{ deletePreview.expires_at }}</p><UiButton variant="destructive" :disabled="busy" @click="deleteProjectStorage">{{ label('确认删除此项目存储', 'Delete this project storage') }}</UiButton><UiButton variant="ghost" :disabled="busy" @click="deletePreview = null">{{ label('取消', 'Cancel') }}</UiButton></div>
      <h3>{{ label('TOML 配置文档', 'TOML configuration document') }}</h3>
      <label class="storage-field">{{ label('配置模块', 'Configuration module') }}<select :value="documentId" class="settings-select" :disabled="busy" @change="selectDocument"><option v-for="module in modules" :key="module" :value="module">{{ module }}.toml</option></select></label>
      <p v-if="document" class="quiet"><code>{{ document.path }}</code> · v{{ document.version }} · {{ document.content_hash }}</p>
      <textarea v-model="text" class="storage-editor" :readonly="busy" spellcheck="false" :aria-label="label('TOML 配置内容', 'TOML configuration text')" />
      <div v-if="diagnostics.length" class="tools-validation" role="status"><p v-for="(item, index) in diagnostics" :key="index">{{ item.message || JSON.stringify(item) }}</p></div>
      <div class="storage-actions"><UiButton variant="outline" :disabled="busy || !document" @click="validate">{{ label('校验 TOML', 'Validate TOML') }}</UiButton><UiButton :disabled="busy || !dirty || !document" @click="save">{{ label('保存配置', 'Save configuration') }}</UiButton><span v-if="dirty" class="quiet">{{ label('草稿未保存', 'Unsaved draft') }}</span></div>
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
.storage-settings code, .storage-editor { font-family: var(--font-mono, 'Geist Mono', monospace); font-size: 12px; }
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
