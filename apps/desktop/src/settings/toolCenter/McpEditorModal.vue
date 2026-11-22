<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, KeyRound, X } from '@lucide/vue'
import { UiButton, UiInput, UiSwitch, UiTextarea } from '@/components/ui'
import { api, type ImportMcpJsonResultDto } from '@/api'
import { toErrorState } from '@/composables/useErrorState'
import type { ToolMcpInput, ToolMcpResource } from '@/settings/toolSettings'
import {
  detectMcpConfigFormat,
  extractSecretPlaceholders,
  formatArgsInput,
  formatEnvLines,
  formatHeaderLines,
  hasMaskedSecrets,
  parseArgsInput,
  parseEnvLines,
  parseHeaderLines,
  scanHiddenUnicode,
  type ExtendedImportMcpInput,
  type ExtendedToolMcp,
} from './mcpFormUtils'

const props = defineProps<{
  open: boolean
  editingServer?: ToolMcpResource | null
  projectId?: string
}>()

const emit = defineEmits<{
  close: []
  saved: [server: ToolMcpResource]
  imported: [result: ImportMcpJsonResultDto]
}>()

const { t } = useI18n()

type ModalMode = 'form' | 'json'
type TransportKind = 'stdio' | 'http' | 'sse'

const mode = ref<ModalMode>('form')
const id = ref('')
const name = ref('')
const enabled = ref(true)
const transport = ref<TransportKind>('stdio')
const command = ref('')
const argsText = ref('')
const cwd = ref('')
const envText = ref('')
const url = ref('')
const headersText = ref('')
const jsonText = ref('')
const jsonEnableImported = ref(true)
const secretValues = ref<Record<string, string>>({})
const saving = ref(false)
const error = ref('')

const isEditing = computed(() => Boolean(props.editingServer))
const showMaskedHint = computed(() => hasMaskedSecrets(props.editingServer))
const editingSecretPlaceholders = computed(() =>
  ((props.editingServer as ExtendedToolMcp | null | undefined)?.secret_placeholders ?? []).filter(Boolean),
)
const detectedConfigFormat = computed(() => detectMcpConfigFormat(jsonText.value))
const detectedPlaceholders = computed(() => extractSecretPlaceholders(jsonText.value))
const importUnicodeScan = computed(() => scanHiddenUnicode(jsonText.value))

const detectedFormatLabel = computed(() => {
  if (detectedConfigFormat.value === 'apm_yml') return 'apm.yml (Microsoft APM)'
  if (detectedConfigFormat.value === 'toml') return 'Codex TOML'
  return '.mcp.json'
})

const MCP_IMPORT_EXAMPLE = `{
  "mcpServers": {
    "github": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-github"],
      "env": { "GITHUB_TOKEN": "\${GITHUB_TOKEN}" }
    }
  }
}

# Or paste Codex config.toml / Microsoft APM apm.yml:
# dependencies:
#   mcp:
#     - name: github
#       command: npx
#       args: ["-y", "@modelcontextprotocol/server-github"]`

function updateSecretValue(varName: string, value: string) {
  secretValues.value = { ...secretValues.value, [varName]: value }
}

function resetFromProps() {
  error.value = ''
  saving.value = false
  secretValues.value = {}
  if (props.editingServer) {
    mode.value = 'form'
    id.value = props.editingServer.id
    name.value = props.editingServer.name
    enabled.value = props.editingServer.enabled
    const rawTransport = (props.editingServer.transport || (props.editingServer.url ? 'http' : 'stdio')).toLowerCase()
    transport.value = rawTransport === 'sse' ? 'sse' : rawTransport === 'http' ? 'http' : 'stdio'
    command.value = props.editingServer.command ?? ''
    argsText.value = formatArgsInput(props.editingServer.args)
    cwd.value = props.editingServer.cwd ?? ''
    envText.value = formatEnvLines(props.editingServer.env)
    url.value = props.editingServer.url ?? ''
    headersText.value = formatHeaderLines(props.editingServer.headers)
  } else {
    mode.value = 'form'
    id.value = ''
    name.value = ''
    enabled.value = true
    transport.value = 'stdio'
    command.value = ''
    argsText.value = ''
    cwd.value = ''
    envText.value = ''
    url.value = ''
    headersText.value = ''
    jsonText.value = ''
    jsonEnableImported.value = true
  }
}

watch(() => [props.open, props.editingServer] as const, ([isOpen]) => {
  if (isOpen) resetFromProps()
}, { immediate: true })

async function submitForm() {
  error.value = ''
  const cleanId = id.value.trim()
  const cleanName = name.value.trim() || cleanId
  if (!cleanId) {
    error.value = t('toolsSettings.validation.required')
    return
  }
  if (transport.value === 'stdio' && !command.value.trim()) {
    error.value = t('toolsSettings.validation.required')
    return
  }
  if ((transport.value === 'http' || transport.value === 'sse') && !url.value.trim()) {
    error.value = t('toolsSettings.validation.required')
    return
  }

  const payload: ToolMcpInput = {
    id: cleanId,
    name: cleanName,
    enabled: enabled.value,
    transport: transport.value,
    command: transport.value === 'stdio' ? command.value.trim() : '',
    args: transport.value === 'stdio' ? parseArgsInput(argsText.value) : [],
    env: transport.value === 'stdio' ? parseEnvLines(envText.value) : {},
    cwd: transport.value === 'stdio' ? (cwd.value.trim() || null) : null,
    url: transport.value === 'stdio' ? null : url.value.trim(),
    headers: transport.value === 'stdio' ? null : parseHeaderLines(headersText.value),
    project_id: props.projectId || null,
  }

  saving.value = true
  try {
    const saved = props.editingServer
      ? await api.updateMcpServer(props.editingServer.id, payload, props.editingServer.revision, props.projectId || undefined)
      : await api.createMcpServer(payload, props.projectId || undefined)
    emit('saved', saved)
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.mcp')
    error.value = errState.details || errState.message
  } finally {
    saving.value = false
  }
}

async function submitJsonImport() {
  error.value = ''
  if (!jsonText.value.trim()) {
    error.value = t('toolsSettings.validation.required')
    return
  }
  saving.value = true
  try {
    const resolvedSecrets: Record<string, string> = {}
    for (const varName of detectedPlaceholders.value) {
      const val = secretValues.value[varName]
      if (typeof val === 'string' && val.length > 0) {
        resolvedSecrets[varName] = val
      }
    }
    const payload: ExtendedImportMcpInput = {
      json: jsonText.value,
      project_id: props.projectId || undefined,
      default_enabled: jsonEnableImported.value,
      overwrite: true,
      ...(detectedConfigFormat.value === 'apm_yml' ? { import_source: 'apm' } : {}),
      ...(Object.keys(resolvedSecrets).length > 0 ? { secret_values: resolvedSecrets } : {}),
    }
    const result = await api.importMcpServersJson(payload)
    emit('imported', result)
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.mcp')
    error.value = errState.details || errState.message
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div
    v-if="open"
    class="model-provider-modal tool-center-modal-backdrop"
    role="dialog"
    aria-modal="true"
    :aria-label="isEditing ? t('toolsSettings.center.mcp.modalEditTitle') : t('toolsSettings.center.mcp.modalCreateTitle')"
    @click.self="emit('close')"
  >
    <div class="model-provider-modal-content tool-center-modal">
      <div class="tool-center-modal-header">
        <div>
          <h3>{{ isEditing ? t('toolsSettings.center.mcp.modalEditTitle') : t('toolsSettings.center.mcp.modalCreateTitle') }}</h3>
          <p class="setting-desc">{{ t('toolsSettings.mcpHint') }}</p>
        </div>
        <UiButton variant="ghost" size="icon" :aria-label="t('app.close')" @click="emit('close')">
          <X :size="16" />
        </UiButton>
      </div>

      <div v-if="!isEditing" class="tool-center-segmented" role="tablist">
        <button
          type="button"
          role="tab"
          class="tool-center-seg-btn"
          :class="{ active: mode === 'form' }"
          :aria-selected="mode === 'form'"
          @click="mode = 'form'"
        >
          {{ t('toolsSettings.center.mcp.modeForm') }}
        </button>
        <button
          type="button"
          role="tab"
          class="tool-center-seg-btn"
          :class="{ active: mode === 'json' }"
          :aria-selected="mode === 'json'"
          data-testid="mcp-mode-import-tab"
          @click="mode = 'json'"
        >
          {{ t('toolsSettings.center.mcp.modeImportConfig') }}
        </button>
      </div>

      <form v-if="mode === 'form'" class="tool-center-form" @submit.prevent="submitForm">
        <div class="tool-center-form-grid">
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.mcp.serverId') }}</span>
            <UiInput
              v-model="id"
              :disabled="isEditing"
              :placeholder="t('toolsSettings.center.mcp.serverIdHint')"
              required
            />
          </label>
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.mcp.serverName') }}</span>
            <UiInput v-model="name" :placeholder="id || 'filesystem'" />
          </label>
        </div>

        <div class="tool-center-form-row-between">
          <div class="tool-center-field">
            <span>{{ t('toolsSettings.center.mcp.transport') }}</span>
            <div class="tool-center-chip-row">
              <button
                v-for="kind in (['stdio', 'http', 'sse'] as const)"
                :key="kind"
                type="button"
                class="tool-center-filter-chip"
                :class="{ active: transport === kind }"
                @click="transport = kind"
              >
                {{
                  kind === 'stdio'
                    ? t('toolsSettings.center.mcp.transportStdio')
                    : kind === 'http'
                      ? t('toolsSettings.center.mcp.transportHttp')
                      : t('toolsSettings.center.mcp.transportSse')
                }}
              </button>
            </div>
          </div>
          <label class="tool-center-switch-inline">
            <span>{{ t('toolsSettings.enabled') }}</span>
            <UiSwitch v-model="enabled" />
          </label>
        </div>

        <template v-if="transport === 'stdio'">
          <div class="tool-center-form-grid">
            <label class="tool-center-field">
              <span>{{ t('toolsSettings.center.mcp.command') }}</span>
              <UiInput v-model="command" placeholder="npx" required />
            </label>
            <label class="tool-center-field">
              <span>{{ t('toolsSettings.center.mcp.cwd') }}</span>
              <UiInput v-model="cwd" placeholder="/workspace" />
            </label>
          </div>
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.mcp.args') }}</span>
            <UiInput v-model="argsText" :placeholder="t('toolsSettings.center.mcp.argsHint')" />
          </label>
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.mcp.envLabel') }}</span>
            <UiTextarea
              v-model="envText"
              :rows="4"
              :placeholder="t('toolsSettings.center.mcp.envPlaceholder')"
            />
          </label>
        </template>

        <template v-else>
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.mcp.url') }}</span>
            <UiInput v-model="url" placeholder="https://mcp.example.com/sse" required />
          </label>
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.mcp.headersLabel') }}</span>
            <UiTextarea
              v-model="headersText"
              :rows="4"
              :placeholder="t('toolsSettings.center.mcp.headersPlaceholder')"
            />
          </label>
        </template>

        <p v-if="showMaskedHint" class="tool-center-hint">
          {{ t('toolsSettings.center.mcp.secretMaskedHint') }}
        </p>
        <div v-if="editingSecretPlaceholders.length" class="mcp-placeholder-strip" data-testid="mcp-edit-placeholders">
          <KeyRound :size="13" aria-hidden="true" />
          <span>{{ t('toolsSettings.center.mcp.referencedSecrets') }}:</span>
          <span v-for="varName in editingSecretPlaceholders" :key="varName" class="mcp-hairline-pill">
            {{ '${' + varName + '}' }}
          </span>
        </div>
        <p v-if="error" class="error-hint" role="alert">{{ error }}</p>

        <div class="tool-center-modal-footer">
          <UiButton type="button" variant="ghost" @click="emit('close')">
            {{ t('common.cancel') }}
          </UiButton>
          <UiButton type="submit" :disabled="saving">
            {{ saving ? t('toolsSettings.saving') : t('settings.save') }}
          </UiButton>
        </div>
      </form>

      <form v-else class="tool-center-form" @submit.prevent="submitJsonImport">
        <div class="tool-center-form-row-between">
          <span class="mcp-import-label">
            {{ t('toolsSettings.center.mcp.importFormatsLabel') }}
          </span>
          <span class="mcp-hairline-pill" data-testid="mcp-detected-format">{{ detectedFormatLabel }}</span>
        </div>
        <label class="tool-center-field">
          <UiTextarea
            v-model="jsonText"
            :rows="10"
            class="tool-center-mono-textarea"
            data-testid="mcp-import-textarea"
            :placeholder="MCP_IMPORT_EXAMPLE"
          />
        </label>

        <div
          v-if="importUnicodeScan.hasHiddenUnicode"
          class="mcp-warning-pill"
          role="alert"
          data-testid="mcp-import-unicode-warning"
        >
          <AlertTriangle :size="13" aria-hidden="true" />
          <span>{{ t('toolsSettings.center.security.hiddenUnicode') }}</span>
        </div>

        <div v-if="detectedPlaceholders.length" class="mcp-secret-box" data-testid="mcp-secret-placeholders">
          <div class="mcp-secret-box-head">
            <KeyRound :size="14" aria-hidden="true" />
            <strong>{{ t('toolsSettings.center.mcp.secretPlaceholdersTitle') }}</strong>
          </div>
          <p class="tool-center-hint">
            {{ t('toolsSettings.center.mcp.secretPlaceholdersHint') }}
          </p>
          <div class="mcp-secret-grid">
            <label v-for="varName in detectedPlaceholders" :key="varName" class="tool-center-field">
              <span class="mcp-secret-var-label">{{ '${' + varName + '}' }}</span>
              <UiInput
                type="password"
                autocomplete="off"
                :model-value="secretValues[varName] ?? ''"
                :data-testid="`mcp-secret-input-${varName}`"
                :placeholder="t('toolsSettings.center.mcp.secretInputPlaceholder')"
                @update:model-value="val => updateSecretValue(varName, String(val))"
              />
            </label>
          </div>
        </div>

        <label class="tool-center-switch-inline">
          <UiSwitch v-model="jsonEnableImported" />
          <span>{{ t('toolsSettings.enabled') }}</span>
        </label>
        <p v-if="error" class="error-hint" role="alert">{{ error }}</p>
        <div class="tool-center-modal-footer">
          <UiButton type="button" variant="ghost" @click="emit('close')">
            {{ t('common.cancel') }}
          </UiButton>
          <UiButton type="submit" :disabled="saving" data-testid="mcp-import-submit">
            {{ saving ? t('toolsSettings.saving') : t('toolsSettings.center.mcp.importJsonAction') }}
          </UiButton>
        </div>
      </form>
    </div>
  </div>
</template>

<style scoped>
.mcp-import-label {
  font-size: 12px;
  color: var(--text-secondary);
}

.mcp-hairline-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-color);
  background: var(--surface-chrome);
  color: var(--text-secondary);
  font-family: var(--font-mono);
  font-size: 11px;
  line-height: 1.4;
}

.mcp-warning-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  width: fit-content;
  padding: 3px 10px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d97706) 45%, var(--border-color));
  background: color-mix(in srgb, var(--accent-warning, #d97706) 11%, transparent);
  color: var(--accent-warning, var(--text-primary));
  font-size: 12px;
  font-weight: 500;
}

.mcp-placeholder-strip {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  border-radius: var(--radius-sm);
  border: 1px solid var(--border-color);
  background: var(--surface-chrome);
  font-size: 12px;
  color: var(--text-secondary);
}

.mcp-secret-box {
  display: grid;
  gap: 8px;
  padding: 10px 12px;
  border-radius: var(--radius-md);
  border: 1px solid color-mix(in srgb, var(--accent-primary) 28%, var(--border-color));
  background: color-mix(in srgb, var(--accent-primary) 5%, var(--surface-section));
}

.mcp-secret-box-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-primary);
}

.mcp-secret-grid {
  display: grid;
  gap: 8px;
}

.mcp-secret-var-label {
  font-family: var(--font-mono);
  font-size: 11px;
  color: var(--text-primary);
}
</style>
