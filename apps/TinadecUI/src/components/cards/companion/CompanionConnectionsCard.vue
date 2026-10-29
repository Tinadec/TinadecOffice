<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, Phone, PlugZap, RefreshCw, Server, type LucideIcon } from '@lucide/vue'
import type { CompanionConnectionOverview } from '@/lib/companionApi'
import {
  connections,
  failures,
  loading,
  selectedCompanion,
  selectedCompanionId,
  useCompanionCenter,
} from '@/lib/companionStore'
import { companionErrorText, formatCompanionTime } from './companionText'

interface ConnectionGroup {
  companionId: string
  name: string
  connections: CompanionConnectionOverview[]
}

const { t, locale } = useI18n()
const store = useCompanionCenter()

const failure = computed(() => (failures.value.connections ? companionErrorText(failures.value.connections, t) : ''))

/** A selected companion narrows the overview to its own bindings; nothing selected shows them all. */
const groups = computed<ConnectionGroup[]>(() => {
  const byCompanion = new Map<string, ConnectionGroup>()
  for (const connection of connections.value) {
    if (selectedCompanionId.value && connection.companion_id !== selectedCompanionId.value) continue
    let group = byCompanion.get(connection.companion_id)
    if (!group) {
      group = {
        companionId: connection.companion_id,
        name: connection.companion_name || connection.companion_id,
        connections: [],
      }
      byCompanion.set(connection.companion_id, group)
    }
    group.connections.push(connection)
  }
  return [...byCompanion.values()]
})

const authorized = computed(() => connections.value.filter((item) => item.authorization === 'authorized').length)
// The published vocabulary is only `unsupported | unknown`: nothing can claim "online" yet, so
// every binding counts as unprobed. Compared as a string because the type system is right — and
// that is exactly the gap: the schema has no word for a connection that actually handshook.
const unprobed = computed(() => connections.value.filter((item) => String(item.runtime_status) !== 'online').length)

const KIND_ICONS: Record<string, LucideIcon> = {
  mcp: PlugZap,
  server: Server,
  phone: Phone,
  external_agent: Bot,
}

function kindIcon(kind: string): LucideIcon {
  return KIND_ICONS[kind] ?? PlugZap
}

function kindLabel(kind: string): string {
  if (kind === 'mcp') return t('companion.center.connectionKindMcp')
  if (kind === 'server') return t('companion.center.connectionKindServer')
  if (kind === 'phone') return t('companion.center.connectionKindPhone')
  if (kind === 'external_agent') return t('companion.center.connectionKindExternalAgent')
  return kind
}

/**
 * Configured/authorized are not "online". Only a runtime that actually handshook says that;
 * Core writes `unsupported` today, and saying otherwise here would be a claim we cannot back.
 */
function runtimeLabel(status: string): string {
  if (status === 'online') return t('companion.center.connectionRuntimeOnline')
  if (status === 'unsupported') return t('companion.center.connectionRuntimeUnsupported')
  return t('companion.center.connectionRuntimeUnknown')
}

function authorizationLabel(authorization: string): string {
  if (authorization === 'authorized') return t('companion.center.connectionAuthAuthorized')
  if (authorization === 'revoked') return t('companion.center.connectionAuthRevoked')
  return t('companion.center.connectionAuthConfigured')
}

function checkedLabel(connection: CompanionConnectionOverview): string {
  const time = formatCompanionTime(connection.last_checked_at, locale.value)
  return time
    ? t('companion.center.connectionCheckedAt', { time })
    : t('companion.center.connectionNeverChecked')
}
</script>

<template>
  <section class="companion-card">
    <header class="companion-card-head">
      <h2>{{ t('companion.center.connectionTitle') }}</h2>
      <div class="companion-card-tools">
        <span v-if="selectedCompanion" class="companion-card-scope">{{ selectedCompanion.name }}</span>
        <button
          type="button"
          class="companion-card-refresh"
          :title="t('companion.center.retry')"
          :aria-label="t('companion.center.retry')"
          @click="store.refresh"
        >
          <RefreshCw :size="13" />
        </button>
      </div>
    </header>

    <p v-if="loading" class="companion-card-note">{{ t('companion.center.loading') }}</p>

    <div v-else-if="failure" class="companion-card-error" role="alert">
      <span>{{ failure }}</span>
      <button type="button" @click="store.refresh">{{ t('companion.center.retry') }}</button>
    </div>

    <p v-else-if="connections.length === 0 || groups.length === 0" class="companion-card-note">
      {{ t('companion.center.connectionEmpty') }}
    </p>

    <template v-else>
      <p class="companion-connection-summary">
        {{ t('companion.center.connectionSummary', {
          total: connections.length,
          authorized,
          unprobed,
        }) }}
      </p>

      <div class="companion-connections">
        <div v-for="group in groups" :key="group.companionId" class="companion-connection-group">
          <h3>{{ group.name }}</h3>
          <div
            v-for="connection in group.connections"
            :key="connection.id"
            class="companion-connection"
            :class="{ disabled: connection.enabled === false }"
          >
            <span class="companion-connection-icon">
              <component :is="kindIcon(connection.kind)" :size="13" />
            </span>
            <div class="companion-connection-body">
              <div class="companion-connection-head">
                <span class="companion-connection-name">{{ connection.name }}</span>
                <span class="companion-connection-kind">{{ kindLabel(connection.kind) }}</span>
              </div>
              <div class="companion-connection-meta">
                <span>{{ authorizationLabel(connection.authorization) }}</span>
                <span v-if="connection.enabled === false" class="companion-connection-off">
                  {{ t('companion.center.connectionDisabled') }}
                </span>
                <span class="companion-connection-runtime">{{ runtimeLabel(connection.runtime_status) }}</span>
              </div>
              <span class="companion-connection-checked">{{ checkedLabel(connection) }}</span>
            </div>
          </div>
        </div>
      </div>
    </template>
  </section>
</template>

<style scoped>
.companion-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  height: 100%;
  overflow-y: auto;
  padding: 14px;
}

.companion-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.companion-card-head h2 {
  margin: 0;
  font-size: 13px;
  font-weight: 700;
  color: var(--text-primary);
}

.companion-card-tools {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.companion-card-scope {
  padding: 1px 7px;
  border-radius: 999px;
  background: var(--surface-raised);
  font-size: 10.5px;
  color: var(--text-secondary);
}

.companion-card-refresh {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.companion-card-refresh:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.companion-card-note {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-muted);
}

.companion-card-error {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-secondary);
}

.companion-card-error button {
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary);
  padding: 3px 10px;
  font-size: 11px;
  cursor: pointer;
}

.companion-connection-summary {
  margin: 0;
  font-size: 11px;
  color: var(--text-muted);
}

.companion-connections {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.companion-connection-group {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.companion-connection-group h3 {
  margin: 0;
  font-size: 11.5px;
  font-weight: 600;
  color: var(--text-secondary);
}

.companion-connection {
  display: flex;
  gap: 8px;
  padding: 7px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-raised);
}

.companion-connection.disabled {
  opacity: 0.6;
}

.companion-connection-icon {
  display: grid;
  place-items: center;
  flex: none;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  background: var(--surface-hover);
  color: var(--text-secondary);
}

.companion-connection-body {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  flex: 1;
}

.companion-connection-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
}

.companion-connection-name {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-connection-kind {
  flex: none;
  font-size: 10px;
  color: var(--text-muted);
}

.companion-connection-meta {
  display: flex;
  gap: 8px;
  font-size: 10.5px;
  color: var(--text-muted);
}

.companion-connection-off {
  color: #d29922;
}

.companion-connection-runtime {
  color: var(--text-secondary);
}

.companion-connection-checked {
  font-size: 10px;
  color: var(--text-muted);
}
</style>
