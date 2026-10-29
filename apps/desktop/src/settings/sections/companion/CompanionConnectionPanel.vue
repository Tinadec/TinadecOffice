<script setup lang="ts">
/**
 * Connection bindings for one companion. Configuration is not a connection: without a handshake
 * the runtime status is `unsupported` (「尚未支持」) or `unknown` (「未探测」), and this panel never
 * renders a configured binding as online.
 */
import { onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Plus, Trash2 } from '@lucide/vue'
import { UiBadge, UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { companionApi, type CompanionConnection } from '@/lib/companionApi'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'

const props = defineProps<{ companionId: string }>()
const emit = defineEmits<{ conflict: [] }>()

const { t } = useI18n()
const { confirm, notify } = useNotifications()

type ConnectionKind = 'mcp' | 'server' | 'phone' | 'external_agent'

const connections = ref<CompanionConnection[]>([])
const loading = ref(false)
const failure = ref<ErrorState | null>(null)
const busy = ref(false)
const form = reactive({ kind: 'mcp' as ConnectionKind, name: '', endpoint: '' })
let readGeneration = 0

const kinds: ConnectionKind[] = ['mcp', 'server', 'phone', 'external_agent']

function kindLabel(kind: string): string {
  switch (kind) {
    case 'server': return t('settings.companions.connection.kindServer')
    case 'phone': return t('settings.companions.connection.kindPhone')
    case 'external_agent': return t('settings.companions.connection.kindExternalAgent')
    default: return t('settings.companions.connection.kindMcp')
  }
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

/** Only the two statuses Core can honestly report exist here; nothing maps to "online". */
function runtimeLabel(status: string): string {
  return status === 'unsupported'
    ? t('settings.companions.connection.runtimeUnsupported')
    : t('settings.companions.connection.runtimeUnknown')
}

function time(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleString() : ''
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

onMounted(() => { void load() })
onBeforeUnmount(() => { readGeneration++ })
watch(() => props.companionId, () => {
  form.kind = 'mcp'
  form.name = ''
  form.endpoint = ''
  void load()
})

function reportWriteFailure(error: unknown, title: string) {
  if (isCompanionRevisionConflict(error)) { emit('conflict'); return }
  notify.error(error, { title, key: `companion-connection-${props.companionId}` })
}

function replaceRow(updated: CompanionConnection) {
  connections.value = connections.value.map((row) => (row.id === updated.id ? updated : row))
}

async function addConnection() {
  const name = form.name.trim()
  if (!name || busy.value) return
  busy.value = true
  try {
    await companionApi.addConnection(props.companionId, {
      kind: form.kind,
      name,
      endpoint: form.endpoint.trim() || null,
      authorization: 'configured',
    })
    form.name = ''
    form.endpoint = ''
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
          <UiBadge variant="secondary" :data-testid="`companion-connection-runtime-${connection.id}`">
            {{ runtimeLabel(connection.runtime_status) }}
          </UiBadge>
          <UiBadge :variant="connection.enabled ? 'default' : 'secondary'">
            {{ connection.enabled ? t('settings.companions.connection.enabled') : t('settings.companions.connection.disabled') }}
          </UiBadge>
        </p>
        <p class="companion-row-meta">
          <span v-if="connection.endpoint">{{ connection.endpoint }}</span>
          <span>{{ t('settings.companions.connection.runtime') }}: {{ runtimeLabel(connection.runtime_status) }}</span>
          <span>
            {{ connection.last_checked_at
              ? t('settings.companions.connection.lastChecked', { time: time(connection.last_checked_at) })
              : t('settings.companions.connection.neverChecked') }}
          </span>
        </p>
        <div class="companion-inline companion-inline-end">
          <UiButton
            v-if="connection.authorization === 'configured'"
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
      <div class="companion-field">
        <label for="companion-connection-name">{{ t('settings.companions.connection.name') }}</label>
        <input id="companion-connection-name" v-model="form.name" class="settings-select" data-testid="companion-connection-name" type="text" />
      </div>
      <div class="companion-field">
        <label for="companion-connection-endpoint">{{ t('settings.companions.connection.endpoint') }}</label>
        <input id="companion-connection-endpoint" v-model="form.endpoint" class="settings-select" data-testid="companion-connection-endpoint" type="text" />
      </div>
      <div class="companion-inline companion-inline-end">
        <UiButton size="sm" data-testid="companion-connection-add" :disabled="busy || !form.name.trim()" @click="addConnection">
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

.companion-row-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  margin: 0;
  font-size: 11px;
  color: var(--text-muted);
}
</style>
