<script setup lang="ts">
/**
 * The workspace's environments (Core todo E1): what the environment steward hands out and who holds
 * each slot now. Registering and switching one off is the user's; taking and giving back is the
 * agents' (`environment_acquire` / `environment_release`), so this view never assigns anything.
 * Core validates everything — a connection that carries a credential comes back as its 400.
 */
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Plus, RefreshCw } from '@lucide/vue'
import { UiButton, UiInput } from '@/components/ui'
import { api, type EnvironmentDto, type EnvironmentKind } from '@/api'

const props = defineProps<{ sessionId?: string | null }>()
const { t } = useI18n()

const KINDS: readonly EnvironmentKind[] = ['test', 'cloud', 'remote', 'local', 'terminal']
const environments = ref<EnvironmentDto[]>([])
const loading = ref(false)
const failure = ref<string | null>(null)
const registering = ref(false)
const form = ref({ key: '', kind: 'test' as EnvironmentKind, display_name: '', capacity: 1, connection: '' })

const message = (cause: unknown) => (cause instanceof Error ? cause.message : String(cause))

async function load() {
  loading.value = true
  try {
    environments.value = await api.listEnvironments()
    failure.value = null
  } catch (cause) {
    failure.value = message(cause)
  } finally {
    loading.value = false
  }
}

async function toggle(environment: EnvironmentDto) {
  try {
    const updated = await api.updateEnvironment(environment.id, { status: environment.status === 'disabled' ? 'available' : 'disabled' })
    environments.value = environments.value.map((item) => (item.id === updated.id ? updated : item))
  } catch (cause) {
    failure.value = message(cause)
  }
}

async function register() {
  let connection: Record<string, unknown> | null = null
  if (form.value.connection.trim()) {
    try {
      const parsed: unknown = JSON.parse(form.value.connection)
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('not an object')
      connection = parsed as Record<string, unknown>
    } catch {
      failure.value = t('organization.environments.invalidJson')
      return
    }
  }
  try {
    const created = await api.registerEnvironment({
      key: form.value.key.trim(), kind: form.value.kind, display_name: form.value.display_name.trim(),
      capacity: form.value.capacity, connection,
    })
    environments.value = [...environments.value, created].sort((a, b) => a.key.localeCompare(b.key))
    registering.value = false
    form.value = { key: '', kind: 'test', display_name: '', capacity: 1, connection: '' }
    failure.value = null
  } catch (cause) {
    failure.value = message(cause)
  }
}

const holderLabel = (holder: EnvironmentDto['holders'][number]) =>
  (holder.session_id && holder.session_id === props.sessionId ? `${t('organization.environments.thisSession')} · ` : '')
  + `#${holder.slot} run ${holder.run_id?.slice(0, 8) ?? '?'}`

onMounted(load)
// Occupancy is live state; re-read when the user switches sessions to see what this one holds.
watch(() => props.sessionId, load)
</script>

<template>
  <div class="org-body environment-list" role="tabpanel" data-testid="organization-environments">
    <div class="environment-toolbar">
      <p class="org-muted">{{ t('organization.environments.hint') }}</p>
      <UiButton size="sm" variant="ghost" :disabled="loading" data-testid="environments-refresh" @click="load"><RefreshCw :size="14" /></UiButton>
      <UiButton size="sm" data-testid="environments-register" @click="registering = !registering"><Plus :size="14" />{{ t('organization.environments.register') }}</UiButton>
    </div>

    <form v-if="registering" class="environment-form" data-testid="environment-form" @submit.prevent="register">
      <UiInput v-model="form.key" :placeholder="t('organization.environments.key')" data-testid="environment-key" />
      <select v-model="form.kind" class="environment-select" data-testid="environment-kind">
        <option v-for="kind in KINDS" :key="kind" :value="kind">{{ t(`organization.environments.kinds.${kind}`) }}</option>
      </select>
      <UiInput v-model="form.display_name" :placeholder="t('organization.environments.displayName')" data-testid="environment-name" />
      <label class="org-muted environment-capacity">{{ t('organization.environments.capacity') }}
        <input v-model.number="form.capacity" type="number" min="1" max="64" data-testid="environment-capacity" />
      </label>
      <textarea v-model="form.connection" class="environment-connection" rows="3" :placeholder="t('organization.environments.connection')" data-testid="environment-connection" />
      <div class="environment-actions">
        <UiButton type="submit" size="sm" :disabled="!form.key.trim() || !form.display_name.trim()" data-testid="environment-save">{{ t('organization.environments.save') }}</UiButton>
        <UiButton type="button" size="sm" variant="ghost" @click="registering = false">{{ t('organization.environments.cancel') }}</UiButton>
      </div>
    </form>

    <p v-if="failure" class="org-notice" role="alert">{{ failure }}</p>
    <p v-if="!environments.length && !loading" class="org-empty-line">{{ t('organization.environments.empty') }}</p>
    <ul v-else class="environment-items">
      <li v-for="environment in environments" :key="environment.id" class="environment-item" :data-environment-key="environment.key">
        <div class="environment-head">
          <span class="org-tag">{{ t(`organization.environments.kinds.${environment.kind}`) }}</span>
          <strong>{{ environment.display_name }}</strong>
          <code class="org-muted">{{ environment.key }}</code>
          <span class="org-muted environment-free">
            {{ environment.status === 'disabled' ? t('organization.environments.disabled') : t('organization.environments.free', { free: environment.free_slots, capacity: environment.capacity }) }}
          </span>
          <UiButton size="sm" variant="ghost" :data-testid="`environment-toggle-${environment.key}`" @click="toggle(environment)">
            {{ environment.status === 'disabled' ? t('organization.environments.enable') : t('organization.environments.disable') }}
          </UiButton>
        </div>
        <p v-if="environment.description" class="message-content">{{ environment.description }}</p>
        <p v-if="environment.holders.length" class="org-muted">
          {{ t('organization.environments.heldBy') }}
          <span v-for="holder in environment.holders" :key="holder.lease_id" class="environment-holder" :title="holder.reason">{{ holderLabel(holder) }}</span>
        </p>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.environment-toolbar { display:flex; align-items:flex-start; gap:6px; }
.environment-toolbar p { flex:1; margin:0; }
.environment-form { display:grid; grid-template-columns:1fr 1fr; gap:6px; margin:8px 0; }
.environment-select, .environment-connection, .environment-capacity input {
  font:inherit; color:var(--text-primary); background:var(--surface-section); border:1px solid var(--border-muted); border-radius:6px; padding:4px 6px;
}
.environment-capacity { display:flex; align-items:center; gap:6px; }
.environment-capacity input { width:64px; }
.environment-connection { grid-column:1 / -1; resize:vertical; font-family:var(--font-mono, monospace); font-size:12px; }
.environment-actions { grid-column:1 / -1; display:flex; gap:6px; }
.environment-items { list-style:none; margin:8px 0 0; padding:0; display:flex; flex-direction:column; gap:8px; }
.environment-item { padding:8px; border-radius:6px; background:var(--surface-section); }
.environment-head { display:flex; flex-wrap:wrap; align-items:center; gap:6px; }
.environment-free { margin-left:auto; }
.environment-holder { margin-left:6px; }
.environment-item .message-content { margin:4px 0; font-size:12px; overflow-wrap:anywhere; }
@media (max-width: 480px) { .environment-form { grid-template-columns:1fr; } }
</style>
