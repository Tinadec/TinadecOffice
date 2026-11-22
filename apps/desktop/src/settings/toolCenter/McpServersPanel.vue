<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Activity, AlertTriangle, KeyRound, Pencil, Plus, Server, Trash2, Wrench } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiSheet, UiSwitch } from '@/components/ui'
import { api, type ImportMcpJsonResultDto, type McpServerTestResultDto } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState } from '@/composables/useErrorState'
import type { ToolMcpResource } from '@/settings/toolSettings'
import {
  getSecurityWarnings,
  hasHiddenUnicodeWarning,
  type ExtendedImportMcpResult,
  type ExtendedToolMcp,
} from './mcpFormUtils'
import McpEditorModal from './McpEditorModal.vue'

const props = defineProps<{
  projectId?: string
}>()

const { t } = useI18n()
const { notify } = useNotifications()

const servers = ref<ToolMcpResource[]>([])
const loading = ref(false)
const loadError = ref('')
const search = ref('')
const filter = ref<'all' | 'enabled' | 'disabled'>('all')
const modalOpen = ref(false)
const editingServer = ref<ToolMcpResource | null>(null)
const testingId = ref('')
const togglingId = ref('')
const testResults = ref<Record<string, McpServerTestResultDto>>({})
const drawerServer = ref<ToolMcpResource | null>(null)

const filteredServers = computed(() => {
  const q = search.value.trim().toLowerCase()
  return servers.value.filter((srv) => {
    if (filter.value === 'enabled' && !srv.enabled) return false
    if (filter.value === 'disabled' && srv.enabled) return false
    if (!q) return true
    return (
      srv.name.toLowerCase().includes(q)
      || srv.id.toLowerCase().includes(q)
      || (srv.command || '').toLowerCase().includes(q)
      || (srv.url || '').toLowerCase().includes(q)
    )
  })
})

const drawerTestResult = computed(() => (drawerServer.value ? testResults.value[drawerServer.value.id] ?? null : null))

async function refresh() {
  loading.value = true
  loadError.value = ''
  try {
    servers.value = await api.listConfiguredMcpServers(props.projectId || undefined)
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.mcp')
    loadError.value = errState.details || errState.message
  } finally {
    loading.value = false
  }
}

watch(() => props.projectId, () => {
  void refresh()
})

onMounted(() => {
  void refresh()
})

function openCreate() {
  editingServer.value = null
  modalOpen.value = true
}

function openEdit(server: ToolMcpResource) {
  editingServer.value = server
  modalOpen.value = true
}

async function onToggleEnabled(server: ToolMcpResource, next: boolean) {
  togglingId.value = server.id
  try {
    const updated = await api.setMcpServerEnabled(server.id, next, props.projectId || undefined)
    servers.value = servers.value.map(item => (item.id === server.id ? updated : item))
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.mcp')
    notify.error(errState.details || errState.message)
  } finally {
    togglingId.value = ''
  }
}

async function onTestConnection(server: ToolMcpResource, openDrawer = false) {
  testingId.value = server.id
  try {
    const res = await api.testMcpServer(server.id, props.projectId || undefined)
    testResults.value = { ...testResults.value, [server.id]: res }
    if (res.status === 'connected') {
      notify.success(t('toolsSettings.center.mcp.testSuccess', { count: res.tool_count }))
    } else {
      notify.error(res.reason || t('toolsSettings.center.mcp.testFailed'))
    }
    if (openDrawer) drawerServer.value = server
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.mcp')
    notify.error(errState.details || errState.message || t('toolsSettings.center.mcp.testFailed'))
  } finally {
    testingId.value = ''
  }
}

async function onDelete(server: ToolMcpResource) {
  if (!window.confirm(t('toolsSettings.deleteResource'))) return
  try {
    await api.deleteMcpServer(server.id, props.projectId || undefined)
    servers.value = servers.value.filter(item => item.id !== server.id)
    if (drawerServer.value?.id === server.id) drawerServer.value = null
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.mcp')
    notify.error(errState.details || errState.message)
  }
}

function onSaved() {
  modalOpen.value = false
  notify.success(t('toolsSettings.saved'))
  void refresh()
}

function onImported(result: ImportMcpJsonResultDto) {
  modalOpen.value = false
  const ext = result as ExtendedImportMcpResult
  const parts = [t('toolsSettings.center.mcp.importedCount', { count: result.imported.length })]
  if (result.skipped.length) {
    parts.push(t('toolsSettings.center.mcp.skippedCount', { count: result.skipped.length }))
  }
  if (ext.secret_placeholders?.length) {
    parts.push(
      t('toolsSettings.center.mcp.placeholdersBound', {
        vars: ext.secret_placeholders.join(', '),
      }),
    )
  }
  notify.success(parts.join(' · '))
  void refresh()
}

function serverSecretPlaceholders(srv: ToolMcpResource): string[] {
  return ((srv as ExtendedToolMcp).secret_placeholders ?? []).filter(Boolean)
}

function serverHasUnicodeRisk(srv: ToolMcpResource): boolean {
  return hasHiddenUnicodeWarning(srv as ExtendedToolMcp)
}

function serverWarningsTooltip(srv: ToolMcpResource): string {
  return getSecurityWarnings(srv as ExtendedToolMcp).join('; ')
}

function transportLabel(srv: ToolMcpResource): string {
  const kind = (srv.transport || (srv.url ? 'http' : 'stdio')).toLowerCase()
  return kind.toUpperCase()
}

defineExpose({ refresh })
</script>

<template>
  <div class="tool-center-panel" data-testid="mcp-servers-panel">
    <div class="tool-center-toolbar">
      <div class="tool-center-toolbar-left">
        <UiInput
          v-model="search"
          class="tool-center-search"
          :placeholder="t('toolsSettings.center.mcp.searchPlaceholder')"
        />
        <div class="tool-center-chip-row">
          <button
            v-for="f in (['all', 'enabled', 'disabled'] as const)"
            :key="f"
            type="button"
            class="tool-center-filter-chip"
            :class="{ active: filter === f }"
            @click="filter = f"
          >
            {{
              f === 'all'
                ? t('toolsSettings.center.mcp.filterAll')
                : f === 'enabled'
                  ? t('toolsSettings.center.mcp.filterEnabled')
                  : t('toolsSettings.center.mcp.filterDisabled')
            }}
          </button>
        </div>
      </div>
      <UiButton data-testid="add-mcp-btn" @click="openCreate">
        <Plus :size="15" />
        {{ t('toolsSettings.center.mcp.addBtn') }}
      </UiButton>
    </div>

    <p v-if="loadError" class="error-hint" role="alert">{{ loadError }}</p>

    <div v-if="loading && !servers.length" class="tool-center-empty">
      {{ t('common.loading') }}
    </div>
    <div v-else-if="!filteredServers.length" class="tool-center-empty">
      <Server :size="24" />
      <span>{{ t('toolsSettings.center.mcp.empty') }}</span>
    </div>

    <div v-else class="tool-center-card-grid">
      <article
        v-for="srv in filteredServers"
        :key="srv.resource_id || srv.id"
        class="tool-center-card"
        :data-testid="`mcp-card-${srv.id}`"
      >
        <div class="tool-center-card-head">
          <div class="tool-center-card-title-group">
            <strong class="tool-center-card-title">{{ srv.name || srv.id }}</strong>
            <span class="tool-center-card-id">{{ srv.id }}</span>
          </div>
          <UiSwitch
            :model-value="srv.enabled"
            :disabled="togglingId === srv.id"
            :aria-label="srv.name || srv.id"
            @update:model-value="val => onToggleEnabled(srv, Boolean(val))"
          />
        </div>

        <div class="tool-center-badge-row">
          <UiBadge variant="outline">{{ transportLabel(srv) }}</UiBadge>
          <UiBadge variant="outline">
            {{ srv.project_id ? t('toolsSettings.projectResources') : t('toolsSettings.shared') }}
          </UiBadge>
          <UiBadge v-if="testResults[srv.id]" :variant="testResults[srv.id]?.status === 'connected' ? 'default' : 'outline'">
            {{
              testResults[srv.id]?.status === 'connected'
                ? t('toolsSettings.center.mcp.testSuccess', { count: testResults[srv.id]?.tool_count ?? 0 })
                : testResults[srv.id]?.reason || t('toolsSettings.center.mcp.testFailed')
            }}
          </UiBadge>
          <span
            v-for="varName in serverSecretPlaceholders(srv)"
            :key="varName"
            class="mcp-card-secret-pill"
            :data-testid="`mcp-secret-pill-${srv.id}-${varName}`"
          >
            <KeyRound :size="11" aria-hidden="true" />
            <span>{{ '${' + varName + '}' }}</span>
          </span>
          <span
            v-if="serverHasUnicodeRisk(srv)"
            class="mcp-card-warning-pill"
            :title="serverWarningsTooltip(srv)"
            :data-testid="`mcp-unicode-warning-${srv.id}`"
          >
            <AlertTriangle :size="11" aria-hidden="true" />
            <span>{{ t('toolsSettings.center.security.hiddenUnicode') }}</span>
          </span>
        </div>

        <p class="tool-center-endpoint">
          <template v-if="srv.url">{{ srv.url }}</template>
          <template v-else>{{ srv.command }} {{ (srv.args ?? []).join(' ') }}</template>
        </p>

        <div class="tool-center-card-actions">
          <UiButton
            size="sm"
            variant="outline"
            :disabled="!srv.enabled || testingId === srv.id"
            @click="onTestConnection(srv, true)"
          >
            <Activity :size="14" />
            {{ testingId === srv.id ? t('toolsSettings.center.mcp.testing') : t('toolsSettings.center.mcp.testConnection') }}
          </UiButton>
          <UiButton
            v-if="testResults[srv.id]?.tools?.length"
            size="sm"
            variant="ghost"
            @click="drawerServer = srv"
          >
            <Wrench :size="14" />
            {{ testResults[srv.id]?.tool_count ?? 0 }}
          </UiButton>
          <div class="tool-center-spacer" />
          <UiButton size="sm" variant="ghost" :aria-label="t('toolsSettings.edit')" @click="openEdit(srv)">
            <Pencil :size="14" />
          </UiButton>
          <UiButton size="sm" variant="ghost" :aria-label="t('toolsSettings.delete')" @click="onDelete(srv)">
            <Trash2 :size="14" />
          </UiButton>
        </div>
      </article>
    </div>

    <McpEditorModal
      :open="modalOpen"
      :editing-server="editingServer"
      :project-id="projectId"
      @close="modalOpen = false"
      @saved="onSaved"
      @imported="onImported"
    />

    <UiSheet :open="Boolean(drawerServer)" side="right" @update:open="val => { if (!val) drawerServer = null }">
      <div v-if="drawerServer" class="tool-center-drawer">
        <div class="tool-center-drawer-head">
          <div>
            <h3>{{ t('toolsSettings.center.mcp.liveToolsTitle') }}</h3>
            <p class="setting-desc">{{ drawerServer.name }} ({{ drawerServer.id }})</p>
          </div>
          <UiButton
            size="sm"
            variant="outline"
            :disabled="!drawerServer.enabled || testingId === drawerServer.id"
            @click="onTestConnection(drawerServer, true)"
          >
            {{ testingId === drawerServer.id ? t('toolsSettings.center.mcp.testing') : t('toolsSettings.center.mcp.testConnection') }}
          </UiButton>
        </div>

        <p v-if="!drawerTestResult?.tools?.length" class="tool-center-hint">
          {{ drawerTestResult?.reason || t('toolsSettings.center.mcp.noDiscoveredTools') }}
        </p>

        <ul v-else class="tool-center-live-tool-list">
          <li v-for="tool in drawerTestResult.tools" :key="tool.name" class="tool-center-live-tool-item">
            <strong>{{ tool.name }}</strong>
            <p v-if="tool.description" class="setting-desc">{{ tool.description }}</p>
            <pre v-if="tool.input_schema" class="tool-center-schema-pre">{{ JSON.stringify(tool.input_schema, null, 2) }}</pre>
          </li>
        </ul>
      </div>
    </UiSheet>
  </div>
</template>

<style scoped>
.mcp-card-secret-pill,
.mcp-card-warning-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 11px;
  line-height: 1.4;
}

.mcp-card-secret-pill {
  border: 1px solid var(--border-color);
  background: var(--surface-chrome);
  color: var(--text-secondary);
  font-family: var(--font-mono);
}

.mcp-card-warning-pill {
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d97706) 45%, var(--border-color));
  background: color-mix(in srgb, var(--accent-warning, #d97706) 11%, transparent);
  color: var(--accent-warning, var(--text-primary));
  font-weight: 500;
}
</style>
