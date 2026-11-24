<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RefreshCw } from '@lucide/vue'
import { UiBadge, UiButton, UiSelectField } from '@/components/ui'
import { api, type AgentDefinitionDto } from '@/api'
import { useHostAccess } from '@/lib/hostAccess'
import { isAbortError } from '@/lib/isAbortError'
import { isConnectionLevelError, useNotifications } from '@/composables/useNotifications'
import {
  capabilityReason,
  formatSettings,
  type SettingsIssue,
  type ToolCapabilities,
  type ToolJsonSchema,
  type ToolSettingsDocument,
  type ToolSettingsEffective,
} from '@/settings/toolSettings'

const ToolJsonEditor = defineAsyncComponent(() => import('@/settings/sections/ToolJsonEditor.vue'))

const TOOLS_ADVANCED_KEY = 'tools-advanced'

const props = defineProps<{
  agentId: string
  agents: AgentDefinitionDto[]
  loading: boolean
  saving: boolean
  dirty: boolean
  stale: boolean
  error: string
  schemaVersion?: number
  editorSchema: ToolJsonSchema | null
  draft: string
  validationIssues: SettingsIssue[]
  diff: Array<{ path: string; before: unknown; after: unknown }>
  document: ToolSettingsDocument | null
  effective: ToolSettingsEffective | null
  capabilities: ToolCapabilities | null
  defaults?: Record<string, unknown>
}>()

const emit = defineEmits<{
  'update:agentId': [value: string]
  'update:draft': [value: string]
  save: []
  restoreDefaults: []
  inheritAll: []
}>()

const { t, te } = useI18n()
const { canAccessBackend } = useHostAccess()
const { notify, dismissByKey } = useNotifications()

const localDocument = ref<ToolSettingsDocument | null>(null)
const localEffective = ref<ToolSettingsEffective | null>(null)
const localCapabilities = ref<ToolCapabilities | null>(null)
const reloading = ref(false)
let reloadGeneration = 0

const resolvedDocument = computed(() => props.document ?? localDocument.value)
const resolvedEffective = computed(() => props.effective ?? localEffective.value)
const resolvedCapabilities = computed(() => props.capabilities ?? localCapabilities.value)

async function reloadAdvanced() {
  if (!canAccessBackend.value || reloading.value) return
  const token = ++reloadGeneration
  reloading.value = true
  try {
    const agentArg = props.agentId || undefined
    const [docRes, effRes, capRes] = await Promise.all([
      agentArg ? api.getAgentToolSettings(agentArg) : api.getToolSettingsDefaults(),
      api.getEffectiveToolSettings(agentArg).catch(() => null),
      api.getToolCapabilities(agentArg).catch(() => null),
    ])
    if (token !== reloadGeneration) return
    localDocument.value = docRes
    localEffective.value = effRes
    localCapabilities.value = capRes
    if (!props.draft) {
      emit('update:draft', formatSettings(docRes.settings ?? {}))
    }
    dismissByKey(TOOLS_ADVANCED_KEY)
  } catch (err) {
    if (token !== reloadGeneration || isAbortError(err)) return
    notify.error(err, {
      key: TOOLS_ADVANCED_KEY,
      action: { label: t('common.retry'), run: () => reloadAdvanced() },
    })
  } finally {
    if (token === reloadGeneration) reloading.value = false
  }
}

watch(
  () => [props.error, props.stale] as const,
  ([errorText, isStale]) => {
    if (errorText) {
      notify.error(errorText, {
        key: TOOLS_ADVANCED_KEY,
        ...(isConnectionLevelError(errorText) || !canAccessBackend.value ? { category: 'connection' } : {}),
        action: { label: t('common.retry'), run: () => reloadAdvanced() },
      })
    } else if (isStale) {
      notify.warning({
        key: TOOLS_ADVANCED_KEY,
        message: t('toolsSettings.stale'),
      })
    } else {
      dismissByKey(TOOLS_ADVANCED_KEY)
    }
  },
  { immediate: true },
)

watch(() => props.document, (doc) => {
  if (doc) localDocument.value = null
})

watch(() => props.agentId, () => {
  localDocument.value = null
  localEffective.value = null
  localCapabilities.value = null
})

watch(canAccessBackend, (ready, wasReady) => {
  if (ready && !wasReady) {
    dismissByKey(TOOLS_ADVANCED_KEY)
    if (!props.document || props.error) {
      void reloadAdvanced()
    }
  }
})

onBeforeUnmount(() => {
  ++reloadGeneration
  dismissByKey(TOOLS_ADVANCED_KEY)
})

const agentOptions = computed(() => [
  { value: '', label: t('toolsSettings.sharedDefaults') },
  ...props.agents.map(agent => ({ value: agent.id, label: agent.display_name || agent.slug || agent.id })),
])

const diagnostics = computed(() => resolvedEffective.value?.resource_diagnostics ?? [])

function diagnosticText(item: { kind: string; status: string; reason?: string | null }): string {
  if (item.kind === 'mcp_program') {
    const statusText = te(`toolsSettings.programStatus.${item.status}`)
      ? t(`toolsSettings.programStatus.${item.status}`)
      : item.status
    return t('toolsSettings.diagnostic.mcp_program', { status: statusText })
  }
  const key = `toolsSettings.diagnostic.${item.kind}_${item.status}`
  return te(key) ? t(key) : item.reason || `${item.kind} · ${item.status}`
}

const capabilityText = computed(() => {
  const cap = resolvedCapabilities.value?.capabilities
  if (!cap) return capabilityReason(resolvedCapabilities.value ? { reason: resolvedCapabilities.value.reason ?? undefined } : undefined, { t, te }) || t('toolsSettings.unavailable')
  const rg = cap.ripgrep?.available ? t('toolsSettings.detected') : t('toolsSettings.unavailable')
  const sb = cap.sandbox?.supported ? t('toolsSettings.detected') : t('toolsSettings.unavailable')
  return t('toolsSettings.capabilitySummary', { rg, sandbox: sb })
})

defineExpose({ reloadAdvanced })
</script>

<template>
  <div class="tool-center-panel" data-testid="tools-advanced-panel">
    <div class="tool-center-advanced-grid">
      <div class="tool-center-diagnostic-card">
        <h3>{{ t('toolsSettings.center.advanced.diagnosticsTitle') }}</h3>
        <p class="setting-desc">{{ capabilityText }}</p>

        <div v-if="diagnostics.length" class="tool-center-diagnostics-list" role="status">
          <div v-for="(item, idx) in diagnostics" :key="`${item.kind}-${idx}`" class="tool-center-diagnostic-item">
            <UiBadge variant="outline">{{ item.kind }}</UiBadge>
            <p :title="item.reason || ''">{{ diagnosticText(item) }}</p>
          </div>
        </div>
      </div>

      <div class="tool-center-diagnostic-card">
        <h3>{{ t('toolsSettings.center.advanced.title') }}</h3>
        <p class="setting-desc">{{ t('toolsSettings.center.advanced.subtitle') }}</p>
        <label class="tool-center-field">
          <span>{{ t('toolsSettings.agent') }}</span>
          <UiSelectField
            :model-value="agentId"
            :options="agentOptions"
            :disabled="loading || reloading"
            @update:model-value="val => emit('update:agentId', String(val))"
          />
        </label>
        <p class="tool-center-hint">
          {{ agentId ? t('toolsSettings.overrideHint') : t('toolsSettings.nextRun') }}
        </p>
      </div>
    </div>

    <div class="tools-command-bar tool-center-command-bar">
      <div class="tool-center-inline-row">
        <UiBadge v-if="schemaVersion" variant="outline">{{ t('toolsSettings.schemaVersion', { version: schemaVersion }) }}</UiBadge>
        <UiBadge v-if="resolvedDocument?.revision !== undefined" variant="outline">
          {{ t('toolsSettings.revision') }} #{{ resolvedDocument.revision }}
        </UiBadge>
        <UiBadge v-if="diff.length" variant="outline">
          {{ t('toolsSettings.diff', { count: diff.length }) }}
        </UiBadge>
      </div>
      <div class="tool-center-inline-row">
        <UiButton v-if="agentId" size="sm" variant="outline" @click="emit('inheritAll')">
          {{ t('toolsSettings.inheritAll') }}
        </UiButton>
        <UiButton v-else-if="defaults" size="sm" variant="outline" @click="emit('restoreDefaults')">
          {{ t('toolsSettings.restoreDefaults') }}
        </UiButton>
        <UiButton
          size="sm"
          :disabled="saving || validationIssues.length > 0 || !dirty"
          @click="emit('save')"
        >
          {{ saving ? t('toolsSettings.saving') : t('settings.save') }}
        </UiButton>
      </div>
    </div>

    <div v-if="!resolvedDocument && !loading && !reloading" class="tool-center-empty">
      <span>{{ t('toolsSettings.noSettings') }}</span>
      <UiButton
        size="sm"
        variant="outline"
        :disabled="!canAccessBackend || loading || reloading"
        @click="reloadAdvanced"
      >
        <RefreshCw :size="14" />
        {{ t('common.retry') }}
      </UiButton>
    </div>

    <template v-else>
      <ul v-if="validationIssues.length" class="tool-center-validation-list" role="alert">
        <li v-for="(issue, idx) in validationIssues" :key="idx">
          <code>{{ issue.path }}</code>: {{ issue.message }}
        </li>
      </ul>

      <ToolJsonEditor
        :model-value="draft"
        :schema="editorSchema"
        :label="t('toolsSettings.advanced')"
        @update:model-value="val => emit('update:draft', val)"
        @save="emit('save')"
      />
    </template>
  </div>
</template>
