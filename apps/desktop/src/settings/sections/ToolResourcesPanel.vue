<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useStatusLabels } from '@/lib/statusLabels'
import { UiBadge, UiButton, UiInput, UiSelectField } from '@/components/ui'
import { api as baseApi, type ToolMcpResource, type ToolMcpInput, type ToolSkillResource } from '@/api'
import { scopedApi, projectStorageId, selectionIdentity } from '@/lib/storageScope'
import { useNotifications } from '@/composables/useNotifications'
import { formatSettings, validateSettings, type ToolJsonSchema } from '@/settings/toolSettings'
import { decodeSkillAsset, skillNameFromDocument, SKILL_ASSET_MAX_BYTES, SKILL_PACKAGE_MAX_BYTES, validateSkillPackagePaths } from '@/settings/skillPackage'
import { isUserToolActionTerminal, userToolActionReason } from '@/userToolAction'
const ToolJsonEditor = defineAsyncComponent(() => import('./ToolJsonEditor.vue'))
const ToolDraftDialog = defineAsyncComponent(() => import('./ToolDraftDialog.vue'))
const props = defineProps<{ kind: 'mcp' | 'skills'; projectId: string; selection: string[] | null; inherited?: boolean; agentScope?: boolean; effectiveSelection?: string[] | null; canBind: boolean }>()
const api = scopedApi(baseApi, () => projectStorageId(props.projectId))
const emit = defineEmits<{ binding: [ids: string[] | null, inherit?: boolean]; dirty: [value: boolean]; changed: [] }>()
const { t, te } = useI18n()
const { userActionStatusLabel } = useStatusLabels()
const { confirm, notify } = useNotifications()
const rows = ref<(ToolMcpResource | ToolSkillResource)[]>([])
const scope = ref<'shared' | 'project'>('shared')
const loading = ref(false)
const busy = ref(false)
const error = ref('')
const result = ref('')
const editing = ref(false)
const selected = ref<ToolMcpResource | ToolSkillResource | null>(null)
const content = ref('')
const baseline = ref('')
const name = ref('')
const enabled = ref(true)
const files = ref<Record<string, string>>({})
const packageLoaded = ref(false)
const baselineFiles = ref('')
const previewPath = ref('')
const previewContent = ref('')
const connectionResults = ref<Record<string, { status: string; reason?: string }>>({})
let epoch = 0
let contextEpoch = 0
let actionTimer: ReturnType<typeof setTimeout> | null = null
let actionStartedAt = 0
const draftDialog = ref(false)
let chooseDraft: ((value: 'save' | 'discard' | 'cancel') => void) | null = null
/** Skills that cannot be used, each with its path and reason; Core's own list repeats the rows' English reasons. */
const diagnostics = computed(() => props.kind === 'skills'
  ? rows.value.filter(row => (row as ToolSkillResource).valid === false).map(row => `${(row as ToolSkillResource).path ?? row.name}: ${skillReason(row)}`)
  : [])
const visibleRows = computed(() => rows.value.filter(row => scope.value === 'project' ? row.project_id === selectionIdentity(props.projectId).id : !row.project_id))
const activeIds = computed(() => props.selection ?? (props.inherited ? props.effectiveSelection : null) ?? rows.value.filter(row => row.enabled && (!('valid' in row) || row.valid !== false)).map(row => row.resource_id))
const dirty = computed(() => editing.value && (content.value !== baseline.value || enabled.value !== (selected.value?.enabled ?? true) || name.value !== (selected.value?.name ?? '') || JSON.stringify(files.value) !== baselineFiles.value))
watch(dirty, value => emit('dirty', value), { immediate: true })
const mcpSchema: ToolJsonSchema = { type: 'object', additionalProperties: false, required: ['id', 'name', 'enabled', 'command', 'args', 'env'], properties: {
  id: { type: 'string', minLength: 1 }, name: { type: 'string' }, enabled: { type: 'boolean' }, command: { type: 'string', minLength: 1 }, args: { type: 'array', items: { type: 'string' } }, env: { type: 'object', additionalProperties: { type: ['string', 'null'] } }, cwd: { type: ['string', 'null'] }, project_id: { type: ['string', 'null'] },
} }
const validation = computed(() => validateSettings(content.value, mcpSchema, t))
function discard() { ++epoch; editing.value = false; selected.value = null; content.value = baseline.value = ''; name.value = ''; enabled.value = true; files.value = {}; packageLoaded.value = false; baselineFiles.value = JSON.stringify({}); previewPath.value = previewContent.value = ''; emit('dirty', false) }
function stopActionPoll() { if (actionTimer !== null) clearTimeout(actionTimer); actionTimer = null }
function followAction(actionId: string, status: string) {
  stopActionPoll(); actionStartedAt = Date.now()
  const current = contextEpoch
  const update = (next: string) => { result.value = t(scope.value === 'shared' ? 'toolsSettings.skillQueuedShared' : 'toolsSettings.skillQueued', { status: userActionStatusLabel(next), id: actionId }) }
  update(status)
  if (isUserToolActionTerminal(status)) return
  const poll = async () => {
    if (current !== contextEpoch || Date.now() - actionStartedAt > 10 * 60 * 1000) return
    try {
      const action = await api.getUserToolAction(actionId)
      if (current !== contextEpoch) return
      update(action.status)
      if (isUserToolActionTerminal(action.status)) {
        actionTimer = null
        await refresh()
        if (current === contextEpoch) {
          if (action.status !== 'completed') error.value = userToolActionReason(action, { t, te }) || t('toolsSettings.skillActionFailed', { status: userActionStatusLabel(action.status) })
          emit('changed')
        }
        return
      }
    } catch { /* Keep the receipt and retry; an unavailable Core is not a completed action. */ }
    if (current === contextEpoch) actionTimer = setTimeout(poll, 12_000)
  }
  actionTimer = setTimeout(poll, 12_000)
}
async function allowDiscard() {
  if (busy.value) return false
  if (!dirty.value) return true
  draftDialog.value = true
  const choice = await new Promise<'save' | 'discard' | 'cancel'>(resolve => { chooseDraft = resolve })
  draftDialog.value = false; chooseDraft = null
  return choice === 'save' ? save() : choice === 'discard'
}
async function refresh() {
  const current = ++epoch
  loading.value = true; error.value = ''
  try {
    if (props.kind === 'mcp') {
      const resources = await api.listToolMcpResources(props.projectId || undefined)
      if (current === epoch) rows.value = resources
    } else {
      const catalog = await api.listToolSkills(props.projectId || undefined)
      if (current === epoch) rows.value = catalog.skills
    }
  } catch (err) { if (current === epoch) error.value = err instanceof Error ? err.message : String(err) }
  finally { if (current === epoch) loading.value = false }
}
defineExpose({ discard, refresh, save, isBusy: () => busy.value })
watch(() => [props.kind, props.projectId], () => { ++contextEpoch; stopActionPoll(); busy.value = false; result.value = ''; connectionResults.value = {}; discard(); if (!props.projectId) scope.value = 'shared'; void refresh() }, { immediate: true })
async function changeScope(next: string) {
  if (next === scope.value) return
  if (!await allowDiscard()) return
  ++contextEpoch; stopActionPoll(); result.value = ''; discard(); scope.value = next as 'shared' | 'project'
  // A scope switch can invalidate the first inventory request before it settles.
  await refresh()
}
function toggleBinding(id: string) { emit('binding', activeIds.value.includes(id) ? activeIds.value.filter(value => value !== id) : [...activeIds.value, id]) }
async function open(row?: ToolMcpResource | ToolSkillResource) {
  if (!await allowDiscard()) return
  discard(); editing.value = true; selected.value = row ?? null; error.value = ''; result.value = ''
  if (props.kind === 'mcp') {
    const resource = row as ToolMcpResource | undefined
    content.value = baseline.value = formatSettings({ id: resource?.id ?? '', name: resource?.name ?? '', enabled: resource?.enabled ?? true, command: resource?.command ?? '', args: resource?.args ?? [], env: resource?.env ?? {}, cwd: resource?.cwd ?? null, project_id: resource?.project_id ?? (scope.value === 'project' ? props.projectId : null) })
    return
  }
  name.value = row?.name ?? ''; enabled.value = row?.enabled ?? true
  if (!row) { content.value = baseline.value = '---\nname: new-skill\ndescription: \n---\n\n'; packageLoaded.value = true; return }
  const current = ++epoch; busy.value = true
  try {
    const project = props.projectId || undefined
    const detail = await api.getToolSkill(row.resource_id, project)
    if (current !== epoch) return
    const assets = (detail.package_files ?? []).filter(file => file.path !== 'SKILL.md')
    const loadedFiles: Record<string, string> = {}
    for (let index = 0; index < assets.length; index += 4) {
      const batch = await Promise.all(assets.slice(index, index + 4).map(async file => {
        const asset = await api.getToolSkillFile(row.resource_id, file.path, project)
        if (asset.path !== file.path || file.content_hash && asset.content_hash !== file.content_hash || typeof asset.base64 !== 'string') throw new Error(t('toolsSettings.packageChanged'))
        return [file.path, asset.base64] as const
      }))
      if (current !== epoch) return
      for (const [path, base64] of batch) loadedFiles[path] = base64
    }
    selected.value = detail; name.value = detail.name; enabled.value = detail.enabled; content.value = baseline.value = detail.content ?? ''
    files.value = loadedFiles; baselineFiles.value = JSON.stringify(files.value); packageLoaded.value = true
  }
  catch (err) { if (current === epoch) error.value = err instanceof Error ? err.message : String(err) }
  finally { if (current === epoch) busy.value = false }
}
async function cancel() { if (await allowDiscard()) discard() }
async function save() {
  if (busy.value || !editing.value || (props.kind === 'skills' && !packageLoaded.value) || (props.kind === 'mcp' && validation.value.issues.length)) return false
  busy.value = true; error.value = ''; result.value = ''
  const current = contextEpoch
  let receipt: ToolSkillResource | null = null
  try {
    if (props.kind === 'mcp') {
      const body = validation.value.value as unknown as ToolMcpInput
      if (selected.value) await api.saveToolMcpResource(selected.value.resource_id, body, selected.value.revision ?? 0, props.projectId || undefined)
      else await api.createToolMcpResource(body, props.projectId || undefined)
    } else {
      const saved = selected.value ? await api.saveToolSkill(selected.value.resource_id, {
        content: content.value, enabled: enabled.value, ...(JSON.stringify(files.value) !== baselineFiles.value ? { files: files.value, replace_files: true } : {}), ...(typeof (selected.value as ToolSkillResource).file_hash === 'string' ? { expected_file_hash: (selected.value as ToolSkillResource).file_hash! } : {}),
      }, selected.value.revision ?? 0, props.projectId || undefined) : await api.importToolSkill({ scope: scope.value, ...(scope.value === 'project' ? { project_id: props.projectId } : {}), name: name.value, content: content.value, enabled: enabled.value, ...(Object.keys(files.value).length ? { files: files.value } : {}) })
      receipt = saved
    }
    if (current !== contextEpoch) return false
    discard(); await refresh(); if (current !== contextEpoch) return false
    emit('changed')
    if (receipt?.user_action_id) followAction(receipt.user_action_id, receipt.action_status ?? '')
    else notify.success(t('toolsSettings.saved'))
    return true
  } catch (err) { if (current === contextEpoch) error.value = err instanceof Error ? err.message : String(err); return false }
  finally { if (current === contextEpoch) busy.value = false }
}
async function addFiles(event: Event) {
  const input = event.target as HTMLInputElement
  const current = epoch
  const incoming = Array.from(input.files ?? [])
  try {
  const paths = incoming.map(file => file.webkitRelativePath ? file.webkitRelativePath.split('/').slice(1).join('/') : file.name)
  validateSkillPackagePaths(paths)
  if (incoming.some(file => file.size > SKILL_ASSET_MAX_BYTES)) throw new Error(t('toolsSettings.assetTooLarge'))
  if (incoming.reduce((size, file) => size + file.size, 0) > SKILL_PACKAGE_MAX_BYTES) throw new Error(t('toolsSettings.packageTooLarge'))
  let nextContent = content.value; const nextFiles = { ...files.value }
  for (const file of incoming) {
    const segments = (file.webkitRelativePath || file.name).split('/')
    const path = file.webkitRelativePath ? segments.slice(1).join('/') : file.name
    if (path === 'SKILL.md') { nextContent = await file.text(); continue }
    const bytes = new Uint8Array(await file.arrayBuffer()); let binary = ''
    for (let index = 0; index < bytes.length; index++) binary += String.fromCharCode(bytes[index]!)
    nextFiles[path] = btoa(binary)
  }
  validateSkillPackagePaths(['SKILL.md', ...Object.keys(nextFiles)])
  if (new TextEncoder().encode(nextContent).length + Object.values(nextFiles).reduce((size, value) => size + Math.floor(value.length * 3 / 4), 0) > SKILL_PACKAGE_MAX_BYTES) throw new Error(t('toolsSettings.packageTooLarge'))
  if (current !== epoch) return
  content.value = nextContent; files.value = nextFiles
  if (!selected.value) name.value = skillNameFromDocument(nextContent)
  error.value = ''
  } catch (err) { if (current === epoch) error.value = err instanceof Error ? err.message : String(err) }
  input.value = ''
}
function previewAsset(path: string) { previewPath.value = path; previewContent.value = decodeSkillAsset(files.value[path] ?? '') ?? t('toolsSettings.binaryResource') }
function skillStatus(row: ToolMcpResource | ToolSkillResource): string | null { return props.kind === 'skills' ? (row as ToolSkillResource).availability ?? null : null }
function skillMeta(row: ToolMcpResource | ToolSkillResource, key: 'package_hash' | 'source' | 'version' | 'commit'): string { return String((row as ToolSkillResource)[key] ?? '') }
/** A skill's reason in the interface language from its code; Core's English text when this build has no words for it. */
function skillReason(row: ToolMcpResource | ToolSkillResource): string | null {
  const skill = row as ToolSkillResource
  const key = `toolsSettings.skillReason.${skill.reason_code}`
  return skill.reason_code && te(key) ? t(key, skill.reason_data ?? {}) : skill.reason ?? null
}
/** Core's own sentence, kept reachable as the tooltip when the line shows a translation of it. */
function skillReasonTitle(row: ToolMcpResource | ToolSkillResource): string | undefined {
  const reason = (row as ToolSkillResource).reason ?? undefined
  return skillReason(row) === reason ? undefined : reason
}
async function test(row: ToolMcpResource) {
  busy.value = true; error.value = ''
  const current = contextEpoch
  try { const response = await api.testToolMcpResource(row.resource_id, props.projectId || undefined); if (current === contextEpoch) { result.value = formatSettings(response); connectionResults.value[row.resource_id] = { status: String(response.status ?? (response.success ? 'connected' : 'error')), reason: String(response.reason ?? response.error ?? '') } } }
  catch (err) { if (current === contextEpoch) { error.value = err instanceof Error ? err.message : String(err); connectionResults.value[row.resource_id] = { status: 'error', reason: error.value } } }
  finally { if (current === contextEpoch) busy.value = false }
}
async function remove(row: ToolMcpResource | ToolSkillResource) {
  if (!await allowDiscard()) return
  if (!await confirm({ title: t('toolsSettings.deleteResource'), message: `${row.name}\n${row.resource_id}`, confirmLabel: t('toolsSettings.delete'), cancelLabel: t('common.cancel') })) return
  const current = rows.value.find(resource => resource.resource_id === row.resource_id) ?? row
  const context = contextEpoch
  busy.value = true; error.value = ''
  try {
    let receipt: ToolSkillResource | undefined
    if (props.kind === 'mcp') await api.deleteToolMcpResource(current.resource_id, current.revision ?? 0, props.projectId || undefined)
    else receipt = await api.deleteToolSkill(current.resource_id, current.revision ?? 0, props.projectId || undefined)
    if (context !== contextEpoch) return
    discard(); await refresh(); if (context !== contextEpoch) return
    emit('changed'); if (receipt?.user_action_id) followAction(receipt.user_action_id, receipt.action_status ?? '')
  }
  catch (err) { if (context === contextEpoch) error.value = err instanceof Error ? err.message : String(err) }
  finally { if (context === contextEpoch) busy.value = false }
}
onBeforeUnmount(() => { ++epoch; ++contextEpoch; stopActionPoll(); chooseDraft?.('cancel'); emit('dirty', false) })
</script>
<template>
  <section class="tools-resources" :aria-busy="loading || busy">
    <ToolDraftDialog v-if="draftDialog" @choice="chooseDraft?.($event)" />
    <div class="tools-command-bar"><label>{{ t('toolsSettings.resources') }}<UiSelectField :value="scope" :disabled="busy" :aria-label="t('toolsSettings.resources')" :options="[{ value: 'shared', label: t('toolsSettings.shared') }, { value: 'project', label: t('toolsSettings.projectResources'), disabled: !projectId }]" @change="changeScope" /></label><UiButton size="sm" :disabled="busy || loading" @click="open()">{{ t(kind === 'mcp' ? 'toolsSettings.addMcp' : 'toolsSettings.importSkill') }}</UiButton></div>
    <p class="quiet">{{ t(kind === 'mcp' ? 'toolsSettings.mcpHint' : 'toolsSettings.skillHint') }}</p>
    <div class="tools-binding-bar"><span>{{ t('toolsSettings.binding') }}</span><UiButton v-if="agentScope" variant="ghost" size="sm" :disabled="!canBind" @click="emit('binding', null, true)">{{ t('toolsSettings.inherit') }}</UiButton><UiButton variant="ghost" size="sm" :disabled="!canBind" @click="emit('binding', null)">{{ t('toolsSettings.allResources') }}</UiButton><UiButton variant="ghost" size="sm" :disabled="!canBind" @click="emit('binding', [])">{{ t('toolsSettings.none') }}</UiButton><UiBadge variant="outline">{{ t(inherited ? 'toolsSettings.inherited' : selection === null ? 'toolsSettings.allResources' : 'toolsSettings.custom') }}</UiBadge></div>
    <div class="tools-resource-list">
      <article v-for="row in visibleRows" :key="row.resource_id" class="tools-resource-row">
        <label class="settings-checkbox">
          <input type="checkbox" :checked="activeIds.includes(row.resource_id)" :disabled="!canBind || loading" @change="toggleBinding(row.resource_id)" />
          <span>
            <strong>{{ row.name }}</strong><small>{{ row.resource_id }}</small>
            <small>{{ t(row.project_id ? 'toolsSettings.projectResources' : 'toolsSettings.shared') }} · {{ t('toolsSettings.revision') }} {{ row.revision ?? 0 }}</small>
            <small v-if="'description' in row">{{ row.description }}</small>
            <small v-if="'reason' in row" :title="skillReasonTitle(row)">{{ skillReason(row) }}</small>
            <small v-if="kind === 'skills' && skillMeta(row, 'package_hash')">{{ t('toolsSettings.packageHash') }}: {{ skillMeta(row, 'package_hash') }}</small>
            <small v-if="kind === 'skills' && skillMeta(row, 'source')">{{ t('toolsSettings.packageSource') }}: {{ skillMeta(row, 'source') }}<template v-if="skillMeta(row, 'version')"> @ {{ skillMeta(row, 'version') }}</template></small>
            <small v-if="kind === 'skills' && skillMeta(row, 'commit')">{{ t('toolsSettings.packageCommit') }}: {{ skillMeta(row, 'commit') }}</small>
            <small v-if="kind === 'mcp' && connectionResults[row.resource_id]">{{ t('toolsSettings.connectionTest') }}: {{ t(`toolsSettings.connectionStatus.${connectionResults[row.resource_id]?.status}`, connectionResults[row.resource_id]?.status ?? '') }} {{ connectionResults[row.resource_id]?.status === 'disabled' ? t('toolsSettings.connectionDisabledHint') : connectionResults[row.resource_id]?.status === 'not_reported' ? '' : connectionResults[row.resource_id]?.reason }}</small>
          </span>
        </label>
        <UiBadge :variant="skillStatus(row) === 'invalid' || skillStatus(row) === 'pending' ? 'secondary' : row.enabled ? 'outline' : 'secondary'">{{ t(skillStatus(row) === 'invalid' ? 'toolsSettings.invalid' : skillStatus(row) === 'pending' ? 'toolsSettings.pendingApproval' : row.enabled ? 'toolsSettings.enabled' : 'toolsSettings.disabled') }}</UiBadge>
        <UiButton variant="ghost" size="sm" :disabled="busy" @click="open(row)">{{ t('toolsSettings.edit') }}</UiButton>
        <UiButton v-if="kind === 'mcp'" variant="ghost" size="sm" :disabled="busy" @click="test(row as ToolMcpResource)">{{ t('toolsSettings.test') }}</UiButton>
        <UiButton v-if="kind === 'mcp' || !row.project_id" variant="ghost" size="sm" :disabled="busy" @click="remove(row)">{{ t('toolsSettings.delete') }}</UiButton>
      </article>
    </div>
    <p v-if="kind === 'skills' && scope === 'project'" class="quiet">{{ t('toolsSettings.projectSkillRemoval') }}</p>
    <p v-if="!visibleRows.length" class="quiet">{{ t(loading ? 'common.loading' : 'toolsSettings.noResources') }}</p><ul v-if="diagnostics.length" class="tools-validation"><li v-for="diagnostic in diagnostics" :key="diagnostic">{{ diagnostic }}</li></ul>
    <div v-if="editing" class="tools-resource-editor">
      <div class="tools-command-bar"><h3>{{ t(kind === 'mcp' ? 'toolsSettings.mcpConfiguration' : 'toolsSettings.skillDocument') }}</h3><UiButton variant="ghost" size="sm" :disabled="busy" @click="cancel">{{ t('common.cancel') }}</UiButton></div>
      <template v-if="kind === 'skills'"><label v-if="!selected">{{ t('toolsSettings.name') }}<UiInput v-model="name" /></label><label class="tools-checkbox settings-checkbox"><input v-model="enabled" type="checkbox" />{{ t('toolsSettings.enabled') }}</label></template>
      <p v-if="kind === 'skills'" class="quiet">{{ t('toolsSettings.packageReview', { scope: t(scope === 'project' ? 'toolsSettings.projectResources' : 'toolsSettings.shared'), count: Object.keys(files).length + 1 }) }}</p>
      <ToolJsonEditor v-model="content" :schema="kind === 'mcp' ? mcpSchema : null" :language="kind === 'skills' ? 'markdown' : 'json'" :label="t(kind === 'mcp' ? 'toolsSettings.mcpConfiguration' : 'toolsSettings.skillDocument')" :readonly="busy" @save="save" />
      <template v-if="kind === 'skills'"><label class="tools-files-label">{{ t('toolsSettings.packageFiles') }}<input type="file" multiple @change="addFiles" /></label><label class="tools-files-label">{{ t('toolsSettings.packageDirectory') }}<input type="file" webkitdirectory multiple @change="addFiles" /></label><div v-for="path in Object.keys(files)" :key="path" class="tools-command-bar"><code>{{ path }}</code><UiButton variant="ghost" size="sm" @click="previewAsset(path)">{{ t('toolsSettings.previewFile') }}</UiButton><UiButton variant="ghost" size="sm" @click="delete files[path]">{{ t('toolsSettings.remove') }}</UiButton></div><pre v-if="previewPath" class="tools-resource-result"><strong>{{ previewPath }}</strong>\n{{ previewContent }}</pre></template>
      <ul v-if="kind === 'mcp' && validation.issues.length" class="tools-validation" role="alert"><li v-for="issue in validation.issues" :key="issue.path">{{ issue.path }} {{ issue.message }}</li></ul>
      <UiButton :disabled="busy || (kind === 'mcp' && validation.issues.length > 0) || (kind === 'skills' && (!packageLoaded || !selected && !name.trim()))" @click="save">{{ t('settings.save') }}</UiButton>
    </div>
    <p v-if="error" class="tools-field-error" role="alert">{{ error }}</p><pre v-if="result" class="tools-resource-result">{{ result }}</pre>
  </section>
</template>
