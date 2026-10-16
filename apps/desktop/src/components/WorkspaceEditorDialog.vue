<script setup lang="ts">
import { useHostAccess } from '@/lib/hostAccess'
import { recoveryActions, useErrorState } from '@/composables/useErrorState'
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { DialogRoot, DialogPortal, DialogOverlay, DialogContent, DialogTitle, DialogDescription, DialogClose } from 'reka-ui'
import { FolderPlus, FolderOpen, Star, Trash2, X, LoaderCircle } from '@lucide/vue'
import { UiButton, UiInput } from '@/components/ui'
import { api } from '@/api'
import { basenameFromPath } from '@/format'
import { homeController as c } from '@/controllers/HomeController'
import { selectionIdentity, projectStorageId } from '@/lib/storageScope'
import { folderIdentity, workspaceIcons, workspaceColors, type WorkspaceRoot, type WorkspaceDefinition, type WorkspacePreview, type WorkspaceInput } from '@/lib/workspaces'
import { usePanelStyles } from '@/composables/usePanelStyles'

const { t } = useI18n()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const { canAccessBackend, reason: hostReason } = useHostAccess()
const failureOperation = ref<'read' | 'preview' | 'submit'>('read')
const latestWorkspace = ref<WorkspaceDefinition | null>(null)
const form = ref<WorkspaceInput>({ name: '', roots: [], primary_root_id: '', icon: 'folder', color: 'default' })
const nameEdited = ref(false), saving = ref(false), picking = ref(false), loading = ref(false), failure = useErrorState(), error = computed(() => failure.error.value?.message ?? ''), hash = ref('')
const existing = ref<WorkspacePreview | null>(null)
const editor = c.workspaceEditor
let read = 0
const primary = computed(() => form.value.roots.find(root => root.id === form.value.primary_root_id))
/**
 * The dialog's single read path. It is a named function so the retry/reload recovery actions
 * can honestly re-run it, instead of closing and reopening the dialog and hoping.
 */
async function loadEditor(reset = false): Promise<void> {
  const version = ++read
  const key = editor.value.projectKey
  if (reset) {
    form.value = { name: '', roots: [], primary_root_id: '', icon: 'folder', color: 'default' }
    existing.value = null; hash.value = ''; nameEdited.value = false; latestWorkspace.value = null
  }
  failure.clear(); loading.value = false; failureOperation.value = 'read'
  if (!key) return
  loading.value = true
  try {
    const identity = selectionIdentity(key)
    const workspace = await api.readWorkspace(identity.storageId ?? projectStorageId(identity.id))
    if (version !== read) return
    if (reset || !hash.value) applyWorkspace(workspace)
    else if (workspace.content_hash !== hash.value) latestWorkspace.value = workspace
  } catch (reason) { if (version === read) failure.set(reason) }
  finally { if (version === read) loading.value = false }
}

watch(() => [editor.value.open, editor.value.projectKey] as const, async ([open]) => {
  if (!open) return
  await loadEditor(true)
}, { immediate: true })

/** Re-runs the dialog's read path: the retry action for a failed workspace read. */
async function reload(): Promise<void> { await loadEditor() }
function applyWorkspace(workspace: WorkspaceDefinition) {
  form.value = { ...workspace, roots: workspace.roots.map(root => ({ ...root })) }
  hash.value = workspace.content_hash; nameEdited.value = true; latestWorkspace.value = null
}
/** Re-reads whatever this dialog is currently showing; used by the retry/reload recovery actions. */
function setPrimary(root: WorkspaceRoot) {
  form.value.primary_root_id = root.id
  if (!nameEdited.value) form.value.name = basenameFromPath(root.path)
}
function removeRoot(root: WorkspaceRoot) {
  if (form.value.roots.length === 1 || root.id === form.value.primary_root_id) return
  form.value.roots = form.value.roots.filter(item => item.id !== root.id)
}
async function addFolders() {
  if (picking.value || saving.value) return
  const version = read
  if (!canAccessBackend.value) return
  picking.value = true; failure.clear(); failureOperation.value = 'preview'
  try {
    const paths = await window.tinadec.selectWorkspaceFolders(t('workspaceEditor.pickTitle'))
    if (version !== read) return
    if (!paths.length) return
    const seen = new Set(form.value.roots.map(root => folderIdentity(root.path)))
    const duplicate = paths.find(path => { const id = folderIdentity(path); if (seen.has(id)) return true; seen.add(id); return false })
    if (duplicate) throw new Error(t('workspaceEditor.duplicateFolder', { path: duplicate }))
    if (!editor.value.projectKey) {
      const previews = await Promise.all(paths.map(path => api.previewWorkspace(path)))
      if (version !== read) return
      const saved = previews.find(preview => preview.exists)
      if (saved) { existing.value = saved; return }
    }
    if (form.value.roots.length + paths.length > 32) throw new Error(t('workspaceEditor.tooManyFolders', { max: 32 }))
    const roots = paths.map(path => ({ id: crypto.randomUUID(), path }))
    form.value.roots.push(...roots)
    if (!form.value.primary_root_id) setPrimary(roots[0]!)
  } catch (reason) { if (version === read) failure.set(reason) }
  finally { picking.value = false }
}
function close(open: boolean) { if (!saving.value) editor.value = { ...editor.value, open } }
async function submit() {
  if (saving.value || loading.value || picking.value) return
  const saved = existing.value?.workspace
  const input = saved ?? form.value
  if (!input.name.trim() || !input.roots.length || (!saved && editor.value.projectKey && !hash.value)) return
  if (!canAccessBackend.value) return
  saving.value = true; failure.clear(); failureOperation.value = 'submit'
  try { await c.completeWorkspace(input, editor.value.projectKey, hash.value, existing.value?.project_path) }
  catch (reason) { failure.set(reason) }
  finally { saving.value = false }
}

// The dialog shows the whole failure contract: an unreadable preview, a refused save and a
// stale ETag are three different problems with three different next steps.
const failureState = computed(() => failure.error.value)
const failureRecovery = computed(() => recoveryActions(failure.error.value, {
  // Read retries preserve the draft. Failed writes require a fresh read and an explicit save.
  retry: failureOperation.value === 'read' ? () => reload() : failureOperation.value === 'preview' ? () => addFolders() : undefined,
  reload: editor.value.projectKey ? () => reload() : undefined,
  choose_folder: () => addFolders(),
}))

</script>

<template>
  <DialogRoot :open="editor.open" @update:open="close">
    <DialogPortal>
      <DialogOverlay class="workspace-dialog-overlay" />
      <DialogContent class="workspace-dialog" :style="getPanelStyle()" v-bind="getPanelDataAttributes()" @escape-key-down="saving && $event.preventDefault()" @interact-outside="$event.preventDefault()">
        <form @submit.prevent="submit">
          <header><DialogTitle>{{ editor.projectKey ? t('workspaceEditor.editTitle') : t('workspaceEditor.newTitle') }}</DialogTitle><DialogClose as-child><UiButton type="button" variant="ghost" size="icon" :aria-label="t('common.close')" :disabled="saving"><X :size="16" /></UiButton></DialogClose></header>
          <DialogDescription>{{ t('workspaceEditor.description') }}</DialogDescription>
          <p v-if="!canAccessBackend" role="status">{{ hostReason }}</p>
          <p v-if="loading" role="status"><LoaderCircle class="animate-spin" :size="16" />{{ t('workspaceEditor.loading') }}</p>
          <section v-else-if="existing?.workspace" class="workspace-existing">
            <h3>{{ t('workspaceEditor.existingTitle') }}</h3><strong>{{ existing.workspace.name }}</strong>
            <ul><li v-for="root in existing.workspace.roots" :key="root.id">{{ root.path }}<span v-if="root.id === existing.workspace.primary_root_id"> · {{ t('workspaceEditor.primary') }}</span></li></ul>
            <p>{{ t('workspaceEditor.storageRoot', { path: existing.storage_root }) }}</p><p>{{ t('workspaceEditor.existingHint') }}</p>
            <UiButton type="button" variant="ghost" :disabled="saving" @click="existing = null">{{ t('workspaceEditor.reselect') }}</UiButton>
          </section>
          <template v-else>
            <section v-if="latestWorkspace" class="workspace-existing" role="status"><h3>{{ t('workspaceEditor.updatedTitle') }}</h3><strong>{{ latestWorkspace.name }}</strong><p v-for="root in latestWorkspace.roots" :key="root.id">{{ root.path }}</p><UiButton type="button" variant="outline" @click="applyWorkspace(latestWorkspace)">{{ t('workspaceEditor.loadLatest') }}</UiButton></section>
            <section class="workspace-source-section">
              <h3>{{ t('workspaceEditor.sources') }}</h3>
              <ul v-if="form.roots.length" class="workspace-source-list">
                <li v-for="root in form.roots" :key="root.id">
                  <FolderOpen :size="16" /><div class="workspace-source-path"><strong>{{ basenameFromPath(root.path) }}</strong><span :title="root.path">{{ root.path }}</span></div>
                  <span v-if="root.id === form.primary_root_id" class="workspace-primary"><Star :size="12" />{{ t('workspaceEditor.primary') }}</span>
                  <UiButton v-else type="button" size="sm" variant="ghost" :disabled="saving" @click="setPrimary(root)">{{ t('workspaceEditor.setPrimary') }}</UiButton>
                  <UiButton type="button" variant="ghost" size="icon" :aria-label="t('workspaceEditor.remove', { name: basenameFromPath(root.path) })" :disabled="saving || root.id === form.primary_root_id || form.roots.length === 1" :title="root.id === form.primary_root_id ? t('workspaceEditor.removeBlocked') : t('workspaceEditor.removeHint')" @click="removeRoot(root)"><Trash2 :size="14" /></UiButton>
                </li>
              </ul>
              <UiButton type="button" variant="outline" class="workspace-add-folders" :disabled="saving || picking || !canAccessBackend" @click="addFolders"><FolderPlus :size="18" />{{ picking ? t('workspaceEditor.picking') : t('workspaceEditor.addSources') }}</UiButton>
            </section>
            <label class="workspace-field">{{ t('workspaceEditor.name') }}<UiInput v-model="form.name" maxlength="128" :placeholder="t('workspaceEditor.namePlaceholder')" :disabled="saving" @update:model-value="nameEdited = true" /></label>
            <fieldset class="workspace-appearance" :disabled="saving"><legend>{{ t('workspaceEditor.icon') }}</legend><button v-for="(icon, key) in workspaceIcons" :key="key" type="button" :aria-label="t('workspaceEditor.iconLabel', { key })" :aria-pressed="form.icon === key" @click="form.icon = key"><component :is="icon" :size="18" /></button></fieldset>
            <fieldset class="workspace-appearance" :disabled="saving"><legend>{{ t('workspaceEditor.color') }}</legend><button v-for="color in workspaceColors" :key="color" type="button" :aria-label="t('workspaceEditor.colorLabel', { color })" :aria-pressed="form.color === color" @click="form.color = color"><span class="workspace-color-swatch" :class="`workspace-color-${color}`" /></button></fieldset>
            <p class="workspace-storage-note">{{ editor.projectKey ? t('workspaceEditor.storageNoteEdit') : primary ? t('workspaceEditor.storageNoteAt', { path: primary.path + '/.tinadec' }) : t('workspaceEditor.storageNoteDefault') }}</p>
          </template>
          <div v-if="failureState" role="alert" class="workspace-error">
            <p>{{ failureState.message }}</p>
            <p v-if="failureState.details" class="workspace-error-details">{{ failureState.details }}</p>
            <p v-if="failureState.traceId" class="workspace-error-trace">trace_id: {{ failureState.traceId }}</p>
            <div v-if="failureRecovery.length" class="workspace-error-actions">
              <UiButton v-for="action in failureRecovery" :key="action.kind" variant="outline" size="sm" @click="action.run()">{{ action.label }}</UiButton>
            </div>
          </div>
          <footer><UiButton type="button" variant="ghost" :disabled="saving" @click="close(false)">{{ t('common.cancel') }}</UiButton><UiButton type="submit" :disabled="!canAccessBackend || saving || loading || picking || (!existing?.workspace && (!form.name.trim() || !form.roots.length || (!!editor.projectKey && !hash)))"><LoaderCircle v-if="saving" :size="16" class="animate-spin" />{{ existing ? t('workspaceEditor.openExisting') : editor.projectKey ? t('workspaceEditor.save') : t('workspaceEditor.create') }}</UiButton></footer>
        </form>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>

<style scoped>
.workspace-dialog-overlay { position: fixed; inset: 0; z-index: 100; background: color-mix(in srgb, var(--bg-overlay) 65%, transparent); backdrop-filter: blur(8px); }
.workspace-dialog { position: fixed; z-index: 101; top: 50%; left: 50%; transform: translate(-50%, -50%); width: min(640px, calc(100vw - 32px)); max-height: calc(100vh - 48px); overflow: auto; border: 1px solid var(--border-muted); border-radius: var(--radius-lg); background: var(--surface-raised); color: var(--text-primary); padding: 24px; box-shadow: var(--shadow-card-subtle); }
form { display: grid; gap: 16px; } header, footer { display: flex; justify-content: space-between; align-items: center; gap: 8px; } header { font-size: 18px; font-weight: 600; } footer { justify-content: flex-end; } h3 { font-size: 13px; font-weight: 600; margin: 0 0 10px; } p { margin: 0; font-size: 12px; color: var(--text-muted); }
.workspace-field { display: grid; gap: 8px; font-size: 13px; } .workspace-source-list { list-style: none; padding: 0; margin: 0 0 12px; display: grid; gap: 8px; } .workspace-source-list li { display: flex; align-items: center; gap: 8px; padding: 8px; border-radius: var(--radius-md); background: var(--surface-section); }
.workspace-source-path { flex: 1; min-width: 0; display: grid; gap: 4px; font-size: 12px; } .workspace-source-path span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-muted); } .workspace-primary { display: flex; gap: 4px; align-items: center; font-size: 11px; color: var(--text-muted); white-space: nowrap; }
.workspace-add-folders { width: 100%; min-height: 64px; } .workspace-appearance { border: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 6px; } legend { font-size: 13px; margin-bottom: 8px; } .workspace-appearance button { display: grid; place-items: center; width: 32px; height: 32px; border: 1px solid transparent; border-radius: var(--radius-sm); background: var(--surface-section); color: var(--text-secondary); cursor: pointer; }
.workspace-appearance button:hover { background: var(--surface-hover); } .workspace-appearance button[aria-pressed="true"] { border-color: var(--accent-primary); background: var(--surface-selected); } .workspace-appearance button:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 2px; } .workspace-color-swatch { width: 16px; height: 16px; border-radius: 50%; background: currentColor; }
.workspace-existing { display: grid; gap: 8px; } .workspace-existing li, .workspace-existing p, .workspace-error { overflow-wrap: anywhere; } .workspace-error { color: var(--text-error); }
</style>
