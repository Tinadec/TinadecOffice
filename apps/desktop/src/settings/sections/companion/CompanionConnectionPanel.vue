<script setup lang="ts">
/**
 * Connection bindings for one companion.
 * - External connectors (`mcp`, `server`, `phone`, `external_agent`) do not perform handshakes here,
 *   so their runtime status stays honestly `unsupported` or `unknown`.
 * - Local governed connectors (`work_session`, `browser`) honestly display `connected` / `active`
 *   when Core reports them as live.
 */
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Plus, Trash2 } from '@lucide/vue'
import { api, type ProjectDto } from '@/api'
import { UiBadge, UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import {
  companionApi,
  type CompanionBrowserDriver,
  type CompanionBrowserProfile,
  type CompanionConnection,
  type CompanionConnectionKind,
} from '@/lib/companionApi'
import {
  encodeBrowserConnectionEndpoint,
  parseBrowserConnectionEndpoint,
} from '@/lib/companionWorkSession'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'

const props = defineProps<{ companionId: string }>()
const emit = defineEmits<{ conflict: [] }>()

const { t } = useI18n()
const { confirm, notify } = useNotifications()

type ConnectionKind = CompanionConnectionKind

const connections = ref<CompanionConnection[]>([])
const projects = ref<ProjectDto[]>([])
const browserProfiles = ref<CompanionBrowserProfile[]>([])
const loading = ref(false)
const failure = ref<ErrorState | null>(null)
const busy = ref(false)
const form = reactive({
  kind: 'work_session' as ConnectionKind,
  name: '',
  endpoint: '',
  scope: '*',
  browserDriver: 'builtin_electron' as CompanionBrowserDriver,
  browserProfileId: '',
})
let readGeneration = 0

const kinds: ConnectionKind[] = ['work_session', 'browser', 'mcp', 'server', 'phone', 'external_agent']
const browserDrivers: CompanionBrowserDriver[] = ['builtin_electron', 'ego_lite']

function kindLabel(kind: string): string {
  switch (kind) {
    case 'work_session': return t('settings.companions.connection.kindWorkSession')
    case 'browser': return t('settings.companions.connection.kindBrowser')
    case 'server': return t('settings.companions.connection.kindServer')
    case 'phone': return t('settings.companions.connection.kindPhone')
    case 'external_agent': return t('settings.companions.connection.kindExternalAgent')
    default: return t('settings.companions.connection.kindMcp')
  }
}

function browserDriverLabel(driver: string): string {
  if (driver === 'ego_lite') return t('settings.companions.connection.driverEgoLite')
  if (driver === 'builtin_electron') return t('settings.companions.connection.driverBuiltinElectron')
  return driver
}

function authorizationLabel(authorization: string): string {
  if (authorization === 'authorized') return t('settings.companions.connection.authAuthorized')
  if (authorization === 'revoked') return t('settings.companions.connection.authRevoked')
  return t('settings.companions.connection.authConfigured')
}

function authorizationVariant(authorization: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (authorization === 'authorized') return 'default'
  if (authorization === 'revoked') return 'destructive'
  return 'outline'
}

function isLiveRuntimeStatus(status: string | null | undefined): boolean {
  return status === 'connected' || status === 'active'
}

/** Reports Core's exact status honestly: local connectors may report `connected` / `active`; external ones never fake online. */
function runtimeLabel(status: string): string {
  if (status === 'connected') return t('settings.companions.connection.runtimeConnected')
  if (status === 'active') return t('settings.companions.connection.runtimeActive')
  if (status === 'disconnected') return t('settings.companions.connection.runtimeDisconnected')
  if (status === 'configured') return t('settings.companions.connection.runtimeConfigured')
  if (status === 'unavailable') return t('settings.companions.connection.runtimeUnavailable')
  return status === 'unsupported'
    ? t('settings.companions.connection.runtimeUnsupported')
    : t('settings.companions.connection.runtimeUnknown')
}

function runtimeVariant(status: string): 'default' | 'secondary' {
  return isLiveRuntimeStatus(status) ? 'default' : 'secondary'
}

const projectNameById = computed(() => {
  const map = new Map<string, string>()
  for (const project of projects.value) {
    map.set(project.id, project.name)
  }
  return map
})

const filteredBrowserProfiles = computed(() =>
  browserProfiles.value.filter((profile) => profile.driver === form.browserDriver))

const scopeLabelText = computed(() => t('settings.companions.connection.workspaceScope'))
const allWorkspacesLabel = computed(() => t('settings.companions.connection.scopeAllWorkspaces'))
const browserDriverFieldLabel = computed(() => t('settings.companions.connection.browserDriver'))
const browserProfileFieldLabel = computed(() => t('settings.companions.connection.browserProfile'))
const browserDefaultProfileLabel = computed(() => t('settings.companions.connection.browserProfileDefault'))
const namePlaceholder = computed(() => {
  if (form.kind === 'work_session') return defaultWorkSessionName()
  if (form.kind === 'browser') return defaultBrowserName()
  return ''
})

function defaultWorkSessionName(): string {
  const prefix = t('settings.companions.connection.kindWorkSession')
  if (form.scope === '*') return `${prefix} · ${allWorkspacesLabel.value}`
  const projectName = projectNameById.value.get(form.scope) ?? form.scope
  return `${prefix} · ${projectName}`
}

function defaultBrowserName(): string {
  const driverText = form.browserDriver === 'ego_lite' ? 'Ego Lite' : t('settings.companions.connection.driverBuiltinShort')
  if (!form.browserProfileId) return driverText
  const profile = browserProfiles.value.find((item) => item.id === form.browserProfileId)
  return `${driverText} · ${profile?.name ?? form.browserProfileId}`
}

function endpointDisplay(connection: CompanionConnection): string | null {
  if (connection.kind === 'work_session') {
    const scope = connection.endpoint?.trim()
    if (!scope || scope === '*') return allWorkspacesLabel.value
    return projectNameById.value.get(scope) ?? scope
  }
  if (connection.kind === 'browser') {
    const extra = connection as unknown as { driver?: unknown; profile_id?: unknown }
    const parsed = parseBrowserConnectionEndpoint(connection.endpoint, extra)
    const driverText = browserDriverLabel(parsed.driver)
    if (!parsed.profileId) return driverText
    const profile = browserProfiles.value.find((item) => item.id === parsed.profileId)
    return `${driverText} · ${profile?.name ?? parsed.profileId}`
  }
  return connection.endpoint ?? null
}

const canSubmit = computed(() => {
  if (busy.value) return false
  if (form.kind === 'work_session' || form.kind === 'browser') return true
  return form.name.trim().length > 0
})

function time(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleString() : ''
}

async function loadAuxiliaries() {
  const tasks: Array<Promise<void>> = []
  if (typeof api.listProjects === 'function') {
    tasks.push(
      api.listProjects({ lifecycleStatus: 'active' })
        .then((rows) => { projects.value = Array.isArray(rows) ? rows : [] })
        .catch(() => { projects.value = [] }),
    )
  }
  if (typeof companionApi.listBrowserProfiles === 'function') {
    tasks.push(
      companionApi.listBrowserProfiles()
        .then((rows) => { browserProfiles.value = Array.isArray(rows) ? rows : [] })
        .catch(() => { browserProfiles.value = [] }),
    )
  }
  await Promise.all(tasks)
}

async function load() {
  const read = ++readGeneration
  loading.value = true
  failure.value = null
  try {
    const rows = await companionApi.listConnections(props.companionId)
    if (read !== readGeneration) return
    connections.value = rows
  } catch (error) {
    if (read !== readGeneration) return
    failure.value = toErrorState(error, t('settings.companions.connection.loadFailed'))
  } finally {
    if (read === readGeneration) loading.value = false
  }
}

onMounted(() => {
  void load()
  void loadAuxiliaries()
})
onBeforeUnmount(() => { readGeneration++ })
watch(() => props.companionId, () => {
  form.kind = 'work_session'
  form.name = ''
  form.endpoint = ''
  form.scope = '*'
  form.browserDriver = 'builtin_electron'
  form.browserProfileId = ''
  void load()
})
watch(() => form.browserDriver, () => {
  if (form.browserProfileId && !filteredBrowserProfiles.value.some((item) => item.id === form.browserProfileId)) {
    form.browserProfileId = ''
  }
})

function reportWriteFailure(error: unknown, title: string) {
  if (isCompanionRevisionConflict(error)) { emit('conflict'); return }
  notify.error(error, { title, key: `companion-connection-${props.companionId}` })
}

function replaceRow(updated: CompanionConnection) {
  connections.value = connections.value.map((row) => (row.id === updated.id ? updated : row))
}

async function addConnection() {
  if (!canSubmit.value) return
  busy.value = true
  try {
    if (form.kind === 'work_session') {
      const name = form.name.trim() || defaultWorkSessionName()
      await companionApi.addConnection(props.companionId, {
        kind: 'work_session',
        name,
        endpoint: form.scope || '*',
        authorization: 'authorized',
      })
    } else if (form.kind === 'browser') {
      const name = form.name.trim() || defaultBrowserName()
      const profileId = form.browserProfileId.trim() || null
      await companionApi.addConnection(props.companionId, {
        kind: 'browser',
        name,
        endpoint: encodeBrowserConnectionEndpoint(form.browserDriver, profileId),
        driver: form.browserDriver,
        profile_id: profileId,
        authorization: 'authorized',
      })
    } else {
      const name = form.name.trim()
      if (!name) return
      await companionApi.addConnection(props.companionId, {
        kind: form.kind,
        name,
        endpoint: form.endpoint.trim() || null,
        authorization: 'configured',
      })
    }
    form.name = ''
    form.endpoint = ''
    form.scope = '*'
    form.browserProfileId = ''
    await load()
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.connection.saveFailed'))
  } finally {
    busy.value = false
  }
}

async function authorize(connection: CompanionConnection) {
  if (busy.value) return
  busy.value = true
  try {
    replaceRow(await companionApi.patchConnection(props.companionId, connection.id, connection.revision, { authorization: 'authorized' }))
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.connection.saveFailed'))
  } finally {
    busy.value = false
  }
}

async function revoke(connection: CompanionConnection) {
  const confirmed = await confirm({
    title: t('settings.companions.connection.revokeTitle'),
    message: t('settings.companions.connection.revokeMessage'),
    confirmLabel: t('settings.companions.connection.revokeConfirm'),
    cancelLabel: t('common.cancel'),
    destructive: true,
  })
  if (!confirmed) return
  busy.value = true
  try {
    replaceRow(await companionApi.patchConnection(props.companionId, connection.id, connection.revision, { authorization: 'revoked' }))
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.connection.saveFailed'))
  } finally {
    busy.value = false
  }
}

async function toggle(connection: CompanionConnection) {
  if (busy.value) return
  busy.value = true
  try {
    replaceRow(await companionApi.patchConnection(props.companionId, connection.id, connection.revision, { enabled: !connection.enabled }))
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.connection.saveFailed'))
  } finally {
    busy.value = false
  }
}

async function remove(connection: CompanionConnection) {
  const confirmed = await confirm({
    title: t('settings.companions.connection.deleteTitle'),
    message: t('settings.companions.connection.deleteMessage'),
    confirmLabel: t('settings.companions.connection.deleteConfirm'),
    cancelLabel: t('common.cancel'),
    destructive: true,
  })
  if (!confirmed) return
  busy.value = true
  try {
    await companionApi.deleteConnection(props.companionId, connection.id, connection.revision)
    await load()
  } catch (error) {
    reportWriteFailure(error, t('settings.companions.connection.deleteFailed'))
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="companion-panel" role="tabpanel" data-testid="companion-connection-panel">
    <h4>{{ t('settings.companions.connection.title') }}</h4>
    <p class="companion-hint">{{ t('settings.companions.connection.hint') }}</p>

    <div v-if="failure" role="alert" class="companion-error-text">
      <p>{{ failure.message }}</p>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="load">{{ t('common.retry') }}</UiButton>
    </div>

    <p v-if="loading && connections.length === 0" role="status" class="companion-hint">{{ t('common.loading') }}</p>
    <p v-else-if="!loading && connections.length === 0" class="companion-hint" data-testid="companion-connection-empty">
      {{ t('settings.companions.connection.empty') }}
    </p>
    <ul v-else class="companion-list-reset">
      <li v-for="connection in connections" :key="connection.id" class="companion-card-row" :data-testid="`companion-connection-row-${connection.id}`">
        <p class="companion-row-title">
          <strong>{{ connection.name }}</strong>
          <UiBadge variant="outline">{{ kindLabel(connection.kind) }}</UiBadge>
          <UiBadge :variant="authorizationVariant(connection.authorization)" :data-testid="`companion-connection-auth-${connection.id}`">
            {{ authorizationLabel(connection.authorization) }}
          </UiBadge>
          <UiBadge :variant="runtimeVariant(connection.runtime_status)" :data-testid="`companion-connection-runtime-${connection.id}`">
            {{ runtimeLabel(connection.runtime_status) }}
          </UiBadge>
          <UiBadge :variant="connection.enabled ? 'default' : 'secondary'">
            {{ connection.enabled ? t('settings.companions.connection.enabled') : t('settings.companions.connection.disabled') }}
          </UiBadge>
        </p>
        <p class="companion-row-meta">
          <span v-if="endpointDisplay(connection)" :data-testid="`companion-connection-endpoint-display-${connection.id}`">
            {{ endpointDisplay(connection) }}
          </span>
          <span>{{ t('settings.companions.connection.runtime') }}: {{ runtimeLabel(connection.runtime_status) }}</span>
          <span>
            {{ connection.last_checked_at
              ? t('settings.companions.connection.lastChecked', { time: time(connection.last_checked_at) })
              : t('settings.companions.connection.neverChecked') }}
          </span>
        </p>
        <div class="companion-inline companion-inline-end">
          <UiButton
            v-if="connection.authorization !== 'authorized'"
            variant="outline"
            size="sm"
            :disabled="busy"
            :data-testid="`companion-connection-authorize-${connection.id}`"
            @click="authorize(connection)"
          >
            {{ t('settings.companions.connection.authorize') }}
          </UiButton>
          <UiButton
            v-if="connection.authorization === 'authorized'"
            variant="outline"
            size="sm"
            :disabled="busy"
            :data-testid="`companion-connection-revoke-${connection.id}`"
            @click="revoke(connection)"
          >
            {{ t('settings.companions.connection.revoke') }}
          </UiButton>
          <UiButton variant="ghost" size="sm" :disabled="busy" :data-testid="`companion-connection-toggle-${connection.id}`" @click="toggle(connection)">
            {{ connection.enabled ? t('settings.companions.connection.disable') : t('settings.companions.connection.enable') }}
          </UiButton>
          <UiButton variant="ghost" size="sm" :disabled="busy" :data-testid="`companion-connection-delete-${connection.id}`" @click="remove(connection)">
            <Trash2 :size="14" />
            <span>{{ t('settings.companions.connection.delete') }}</span>
          </UiButton>
        </div>
      </li>
    </ul>

    <div class="companion-card-row companion-form">
      <h5>{{ t('settings.companions.connection.addTitle') }}</h5>
      <div class="companion-field">
        <label for="companion-connection-kind">{{ t('settings.companions.connection.kind') }}</label>
        <select id="companion-connection-kind" v-model="form.kind" class="settings-select" data-testid="companion-connection-kind">
          <option v-for="kind in kinds" :key="kind" :value="kind">{{ kindLabel(kind) }}</option>
        </select>
      </div>

      <template v-if="form.kind === 'work_session'">
        <div class="companion-field">
          <label for="companion-connection-scope">{{ scopeLabelText }}</label>
          <select id="companion-connection-scope" v-model="form.scope" class="settings-select" data-testid="companion-connection-scope">
            <option value="*">{{ allWorkspacesLabel }}</option>
            <option v-for="project in projects" :key="project.id" :value="project.id">{{ project.name }}</option>
          </select>
        </div>
      </template>

      <template v-else-if="form.kind === 'browser'">
        <div class="companion-field">
          <label for="companion-connection-browser-driver">{{ browserDriverFieldLabel }}</label>
          <select
            id="companion-connection-browser-driver"
            v-model="form.browserDriver"
            class="settings-select"
            data-testid="companion-connection-browser-driver"
          >
            <option v-for="driver in browserDrivers" :key="driver" :value="driver">{{ browserDriverLabel(driver) }}</option>
          </select>
        </div>
        <div class="companion-field">
          <label for="companion-connection-browser-profile">{{ browserProfileFieldLabel }}</label>
          <select
            v-if="filteredBrowserProfiles.length > 0"
            id="companion-connection-browser-profile"
            v-model="form.browserProfileId"
            class="settings-select"
            data-testid="companion-connection-browser-profile"
          >
            <option value="">{{ browserDefaultProfileLabel }}</option>
            <option v-for="profile in filteredBrowserProfiles" :key="profile.id" :value="profile.id">
              {{ profile.name }}
            </option>
          </select>
          <input
            v-else
            id="companion-connection-browser-profile"
            v-model="form.browserProfileId"
            class="settings-select"
            data-testid="companion-connection-browser-profile"
            type="text"
            :placeholder="browserDefaultProfileLabel"
          />
        </div>
      </template>

      <div class="companion-field">
        <label for="companion-connection-name">{{ t('settings.companions.connection.name') }}</label>
        <input
          id="companion-connection-name"
          v-model="form.name"
          class="settings-select"
          data-testid="companion-connection-name"
          type="text"
          :placeholder="namePlaceholder"
        />
      </div>

      <div v-if="form.kind !== 'work_session' && form.kind !== 'browser'" class="companion-field">
        <label for="companion-connection-endpoint">{{ t('settings.companions.connection.endpoint') }}</label>
        <input id="companion-connection-endpoint" v-model="form.endpoint" class="settings-select" data-testid="companion-connection-endpoint" type="text" />
      </div>

      <div class="companion-inline companion-inline-end">
        <UiButton size="sm" data-testid="companion-connection-add" :disabled="!canSubmit" @click="addConnection">
          <Plus :size="14" />
          <span>{{ t('settings.companions.connection.add') }}</span>
        </UiButton>
      </div>
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
  padding: 10px 12px;
  border-radius: var(--radius-card, 10px);
  border: 1px solid var(--border-muted);
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

.companion-row-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  margin: 0;
  font-size: 11px;
  color: var(--text-muted);
}
</style>
