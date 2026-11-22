<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, Bot, FolderOpen, GitCommitHorizontal, KeyRound, RefreshCw, ShieldCheck, X } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiSwitch, UiTextarea } from '@/components/ui'
import { api, type ExternalCliEcosystemDto, type InstallToolSkillResultDto } from '@/api'
import { canPickHostDirectories, pickWorkspaceFolders } from '@/lib/hostConnection'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState } from '@/composables/useErrorState'
import {
  KNOWN_ECOSYSTEMS,
  ecosystemDisplayLabel,
  extractSecretPlaceholders,
  getSecurityWarnings,
  hasHiddenUnicodeWarning,
  scanHiddenUnicode,
  shortCommit,
  shortDigest,
  type ExtendedExternalCliItem,
  type ExtendedInstallToolSkillInput,
  type ExtendedInstallToolSkillResult,
  type ExtendedToolSkill,
} from './mcpFormUtils'

export type SkillInstallMode = 'inline' | 'local_dir' | 'git_url' | 'external_cli'

const props = defineProps<{
  open: boolean
  initialMode?: SkillInstallMode
  projectId?: string
}>()

const emit = defineEmits<{
  close: []
  installed: [result: ExtendedInstallToolSkillResult]
  agentsImported: []
}>()

const { t } = useI18n()
const { notify } = useNotifications()

const mode = ref<SkillInstallMode>('inline')
const inlineName = ref('')
const inlineContent = ref('---\nname: my-custom-skill\ndescription: Custom skill instructions\n---\n\n# Instructions\n')
const localPath = ref('')
const gitUrl = ref('')
const subpath = ref('')
const externalEcosystems = ref<ExternalCliEcosystemDto[]>([])
const externalItems = ref<ExtendedExternalCliItem[]>([])
const ecosystemFilter = ref<string>('all')
const selectedExternalIds = ref<string[]>([])
const secretValues = ref<Record<string, string>>({})
const enableStdioMcp = ref(false)
const scanning = ref(false)
const installing = ref(false)
const importingAgentId = ref('')
const error = ref('')

const canBrowseFolder = computed(() => canPickHostDirectories())

const inlineUnicodeScan = computed(() => scanHiddenUnicode(inlineContent.value))

const ecosystemChips = computed(() => {
  const counts = new Map<string, number>()
  for (const item of externalItems.value) {
    const key = (item.ecosystem || '').toLowerCase()
    counts.set(key, (counts.get(key) ?? 0) + 1)
  }
  const detectedMap = new Map<string, ExternalCliEcosystemDto>()
  for (const eco of externalEcosystems.value) {
    detectedMap.set(eco.id.toLowerCase(), eco)
  }
  const seen = new Set<string>()
  const chips: Array<{ id: string; label: string; count: number; detected: boolean }> = []
  for (const known of KNOWN_ECOSYSTEMS) {
    seen.add(known.id)
    const eco = detectedMap.get(known.id)
    const count = counts.get(known.id) ?? 0
    chips.push({
      id: known.id,
      label: eco?.label || known.label,
      count,
      detected: Boolean(eco?.detected || count > 0),
    })
  }
  for (const eco of externalEcosystems.value) {
    const id = eco.id.toLowerCase()
    if (seen.has(id)) continue
    seen.add(id)
    const count = counts.get(id) ?? 0
    chips.push({ id, label: eco.label || ecosystemDisplayLabel(id), count, detected: Boolean(eco.detected || count > 0) })
  }
  for (const [id, count] of counts.entries()) {
    if (seen.has(id)) continue
    seen.add(id)
    chips.push({ id, label: ecosystemDisplayLabel(id), count, detected: true })
  }
  return chips
})

const filteredExternalItems = computed(() => {
  if (ecosystemFilter.value === 'all') return externalItems.value
  return externalItems.value.filter(item => (item.ecosystem || '').toLowerCase() === ecosystemFilter.value)
})

const selectedExternalPlaceholders = computed(() => {
  const seen = new Set<string>()
  const vars: string[] = []
  for (const item of externalItems.value) {
    if (!selectedExternalIds.value.includes(item.item_id)) continue
    const explicit = item.secret_placeholders ?? []
    const inferred = item.content_preview ? extractSecretPlaceholders(item.content_preview) : []
    for (const v of [...explicit, ...inferred]) {
      const clean = String(v).trim()
      if (clean && !seen.has(clean)) {
        seen.add(clean)
        vars.push(clean)
      }
    }
  }
  return vars
})

function updateSecretValue(varName: string, value: string) {
  secretValues.value = { ...secretValues.value, [varName]: value }
}

function itemKindLabel(kind: string): string {
  const k = (kind || '').toLowerCase()
  if (k === 'agent') return t('toolsSettings.center.skills.kindCustomAgent')
  if (k === 'mcp-server') return 'MCP'
  if (k === 'command') return t('toolsSettings.center.skills.kindCommand')
  if (k === 'skill') return 'Skill'
  return kind
}

function itemDigest(item: ExtendedExternalCliItem): string {
  return shortDigest(item.package_hash || item.content_hash)
}

function itemFullDigest(item: ExtendedExternalCliItem): string {
  return (item.package_hash || item.content_hash || '').trim()
}

async function scanExternalCli() {
  scanning.value = true
  error.value = ''
  try {
    const res = await api.discoverExternalCliTools({ projectId: props.projectId || undefined })
    externalEcosystems.value = res.ecosystems ?? []
    externalItems.value = (res.items ?? []) as ExtendedExternalCliItem[]
    selectedExternalIds.value = externalItems.value
      .filter(item => !item.already_imported)
      .map(item => item.item_id)
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    error.value = errState.details || errState.message
  } finally {
    scanning.value = false
  }
}

watch(
  () => [props.open, props.initialMode] as const,
  ([isOpen, nextMode]) => {
    if (!isOpen) return
    error.value = ''
    secretValues.value = {}
    mode.value = nextMode ?? 'inline'
    if (mode.value === 'external_cli' && !externalItems.value.length) {
      void scanExternalCli()
    }
  },
  { immediate: true },
)

watch(mode, (next) => {
  error.value = ''
  if (next === 'external_cli' && !externalItems.value.length && !scanning.value) {
    void scanExternalCli()
  }
})

async function pickFolder() {
  try {
    const picked = await pickWorkspaceFolders(t('toolsSettings.center.skills.localPath'), { multiple: false })
    if (picked?.[0]) localPath.value = picked[0]
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    error.value = errState.details || errState.message
  }
}

function toggleExternalItem(id: string) {
  if (selectedExternalIds.value.includes(id)) {
    selectedExternalIds.value = selectedExternalIds.value.filter(item => item !== id)
  } else {
    selectedExternalIds.value = [...selectedExternalIds.value, id]
  }
}

function collectResolvedSecrets(varNames: string[]): Record<string, string> {
  const out: Record<string, string> = {}
  for (const name of varNames) {
    const val = secretValues.value[name]
    if (typeof val === 'string' && val.length > 0) out[name] = val
  }
  return out
}

function notifyInstallSuccess(result: ExtendedInstallToolSkillResult) {
  const agentsCount = result.imported_agents?.length ?? 0
  const skillsCount = result.installed_skills?.length ?? 0
  const mcpsCount = result.installed_mcp_servers?.length ?? 0
  const hasUnicode = hasHiddenUnicodeWarning(result)
    || (result.installed_skills ?? []).some(skill => hasHiddenUnicodeWarning(skill as ExtendedToolSkill))

  if (agentsCount > 0) {
    notify.success(
      t('toolsSettings.center.skills.importedWithAgents', {
        skills: skillsCount,
        mcps: mcpsCount,
        agents: agentsCount,
      }),
    )
  } else if (mcpsCount > 0) {
    notify.success(
      t('toolsSettings.center.skills.importedResult', {
        skills: skillsCount,
        mcps: mcpsCount,
      }),
    )
  } else {
    notify.success(t('toolsSettings.center.skills.installedOk'))
  }

  if (hasUnicode) {
    notify.error(t('toolsSettings.center.security.hiddenUnicodeInstalledAlert'))
  }
}

async function importSingleAgent(item: ExtendedExternalCliItem) {
  error.value = ''
  importingAgentId.value = item.item_id
  const scope = props.projectId ? 'project' : 'shared'
  try {
    const payload: ExtendedInstallToolSkillInput = {
      source_type: 'external_cli_import',
      scope,
      project_id: props.projectId || undefined,
      item_ids: [item.item_id],
      ecosystems: item.ecosystem ? [item.ecosystem] : undefined,
      enable_stdio_mcp: enableStdioMcp.value,
    }
    const result = (await api.installToolSkill(payload)) as ExtendedInstallToolSkillResult
    externalItems.value = externalItems.value.map(entry =>
      entry.item_id === item.item_id ? { ...entry, already_imported: true } : entry,
    )
    selectedExternalIds.value = selectedExternalIds.value.filter(id => id !== item.item_id)
    notifyInstallSuccess(result)
    emit('agentsImported')
    emit('installed', result)
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    error.value = errState.details || errState.message
  } finally {
    importingAgentId.value = ''
  }
}

async function submitInstall() {
  error.value = ''
  const scope = props.projectId ? 'project' : 'shared'
  installing.value = true
  try {
    let result: ExtendedInstallToolSkillResult
    if (mode.value === 'inline') {
      if (!inlineName.value.trim()) {
        error.value = t('toolsSettings.validation.required')
        installing.value = false
        return
      }
      result = (await api.installToolSkill({
        source_type: 'inline',
        scope,
        project_id: props.projectId || undefined,
        name: inlineName.value.trim(),
        content: inlineContent.value,
        enabled: true,
      })) as ExtendedInstallToolSkillResult
    } else if (mode.value === 'local_dir') {
      if (!localPath.value.trim()) {
        error.value = t('toolsSettings.validation.required')
        installing.value = false
        return
      }
      result = (await api.installToolSkill({
        source_type: 'local_dir',
        scope,
        project_id: props.projectId || undefined,
        local_path: localPath.value.trim(),
        enabled: true,
      })) as ExtendedInstallToolSkillResult
    } else if (mode.value === 'git_url') {
      if (!gitUrl.value.trim()) {
        error.value = t('toolsSettings.validation.required')
        installing.value = false
        return
      }
      result = (await api.installToolSkill({
        source_type: 'git_url',
        scope,
        project_id: props.projectId || undefined,
        git_url: gitUrl.value.trim(),
        subpath: subpath.value.trim() || undefined,
        enabled: true,
      })) as ExtendedInstallToolSkillResult
    } else {
      const resolvedSecrets = collectResolvedSecrets(selectedExternalPlaceholders.value)
      const payload: ExtendedInstallToolSkillInput = {
        source_type: 'external_cli_import',
        scope,
        project_id: props.projectId || undefined,
        item_ids: selectedExternalIds.value,
        enable_stdio_mcp: enableStdioMcp.value,
        ...(Object.keys(resolvedSecrets).length > 0 ? { secret_values: resolvedSecrets } : {}),
      }
      result = (await api.installToolSkill(payload)) as ExtendedInstallToolSkillResult
    }
    notifyInstallSuccess(result)
    if (result.imported_agents?.length) emit('agentsImported')
    emit('installed', result as InstallToolSkillResultDto & ExtendedInstallToolSkillResult)
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    error.value = errState.details || errState.message
  } finally {
    installing.value = false
  }
}
</script>

<template>
  <div
    v-if="open"
    class="model-provider-modal tool-center-modal-backdrop"
    role="dialog"
    aria-modal="true"
    :aria-label="t('toolsSettings.center.skills.addBtn')"
    @click.self="emit('close')"
  >
    <div class="model-provider-modal-content tool-center-modal">
      <div class="tool-center-modal-header">
        <div>
          <h3>{{ t('toolsSettings.center.skills.addBtn') }}</h3>
          <p class="setting-desc">{{ t('toolsSettings.skillHint') }}</p>
        </div>
        <UiButton variant="ghost" size="icon" :aria-label="t('app.close')" @click="emit('close')">
          <X :size="16" />
        </UiButton>
      </div>

      <div class="tool-center-segmented tool-center-segmented--wrap" role="tablist">
        <button
          v-for="m in ([
            { key: 'inline', label: 'toolsSettings.center.skills.addCreate' },
            { key: 'local_dir', label: 'toolsSettings.center.skills.addLocalDir' },
            { key: 'git_url', label: 'toolsSettings.center.skills.addGitUrl' },
            { key: 'external_cli', label: 'toolsSettings.center.skills.addExternalCli' },
          ] as const)"
          :key="m.key"
          type="button"
          role="tab"
          class="tool-center-seg-btn"
          :class="{ active: mode === m.key }"
          :aria-selected="mode === m.key"
          @click="mode = m.key"
        >
          {{ t(m.label) }}
        </button>
      </div>

      <form class="tool-center-form" @submit.prevent="submitInstall">
        <template v-if="mode === 'inline'">
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.skills.inlineName') }}</span>
            <UiInput v-model="inlineName" :placeholder="t('toolsSettings.center.skills.inlineNamePlaceholder')" required />
          </label>
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.skills.skillMdTitle') }}</span>
            <UiTextarea v-model="inlineContent" :rows="8" class="tool-center-mono-textarea" />
          </label>
          <div
            v-if="inlineUnicodeScan.hasHiddenUnicode"
            class="skill-warning-pill"
            role="alert"
            data-testid="inline-unicode-warning"
          >
            <AlertTriangle :size="13" aria-hidden="true" />
            <span>{{ t('toolsSettings.center.security.hiddenUnicode') }}</span>
          </div>
        </template>

        <template v-else-if="mode === 'local_dir'">
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.skills.localPath') }}</span>
            <div class="tool-center-inline-row">
              <UiInput v-model="localPath" :placeholder="t('toolsSettings.center.skills.localPathPlaceholder')" required />
              <UiButton v-if="canBrowseFolder" type="button" variant="outline" @click="pickFolder">
                <FolderOpen :size="14" />
                {{ t('toolsSettings.center.skills.pickFolder') }}
              </UiButton>
            </div>
          </label>
        </template>

        <template v-else-if="mode === 'git_url'">
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.skills.gitUrl') }}</span>
            <UiInput v-model="gitUrl" :placeholder="t('toolsSettings.center.skills.gitUrlPlaceholder')" required />
          </label>
          <label class="tool-center-field">
            <span>{{ t('toolsSettings.center.skills.subpath') }}</span>
            <UiInput v-model="subpath" :placeholder="t('toolsSettings.center.skills.subpathPlaceholder')" />
          </label>
          <p class="tool-center-hint">
            {{ t('toolsSettings.center.skills.gitSecurityHint') }}
          </p>
        </template>

        <template v-else>
          <div class="tool-center-form-row-between">
            <p class="tool-center-hint">
              {{ t('toolsSettings.center.skills.externalHintWithApm') }}
            </p>
            <UiButton type="button" size="sm" variant="outline" :disabled="scanning" @click="scanExternalCli">
              <RefreshCw :size="14" />
              {{ scanning ? t('toolsSettings.center.skills.scanning') : t('toolsSettings.center.skills.scanBtn') }}
            </UiButton>
          </div>

          <div class="tool-center-chip-row" data-testid="external-ecosystem-filters">
            <button
              type="button"
              class="tool-center-filter-chip"
              :class="{ active: ecosystemFilter === 'all' }"
              data-testid="ecosystem-chip-all"
              @click="ecosystemFilter = 'all'"
            >
              {{ t('toolsSettings.center.skills.filterAll') }} ({{ externalItems.length }})
            </button>
            <button
              v-for="eco in ecosystemChips"
              :key="eco.id"
              type="button"
              class="tool-center-filter-chip"
              :class="{ active: ecosystemFilter === eco.id }"
              :data-testid="`ecosystem-chip-${eco.id}`"
              @click="ecosystemFilter = eco.id"
            >
              {{ eco.label }}<template v-if="eco.count > 0"> ({{ eco.count }})</template>
            </button>
          </div>

          <div v-if="!filteredExternalItems.length && !scanning" class="tool-center-empty">
            {{ t('toolsSettings.center.skills.externalEmpty') }}
          </div>
          <div v-else class="tool-center-external-list">
            <div
              v-for="item in filteredExternalItems"
              :key="item.item_id"
              class="tool-center-external-item skill-external-row"
              :data-testid="`external-item-${item.item_id}`"
            >
              <input
                type="checkbox"
                :checked="selectedExternalIds.includes(item.item_id)"
                :aria-label="item.display_name || item.name"
                @change="toggleExternalItem(item.item_id)"
              >
              <div class="tool-center-external-meta skill-external-meta">
                <div class="tool-center-badge-row">
                  <strong>{{ item.display_name || item.name }}</strong>
                  <UiBadge variant="outline">{{ ecosystemDisplayLabel(item.ecosystem) }}</UiBadge>
                  <UiBadge variant="outline">{{ itemKindLabel(item.kind) }}</UiBadge>
                  <span
                    v-if="hasHiddenUnicodeWarning(item)"
                    class="skill-warning-pill"
                    :title="getSecurityWarnings(item).join('; ')"
                    :data-testid="`external-unicode-warning-${item.item_id}`"
                  >
                    <AlertTriangle :size="11" aria-hidden="true" />
                    <span>{{ t('toolsSettings.center.security.hiddenUnicode') }}</span>
                  </span>
                </div>

                <span class="tool-center-card-id">{{ item.source_path }}</span>

                <div
                  v-if="item.commit || itemDigest(item) || item.secret_placeholders?.length"
                  class="skill-provenance-row"
                >
                  <span
                    v-if="item.commit"
                    class="skill-provenance-pill"
                    :title="`Commit: ${item.commit}`"
                    :data-testid="`external-commit-${item.item_id}`"
                  >
                    <GitCommitHorizontal :size="11" aria-hidden="true" />
                    <span>Commit {{ shortCommit(item.commit) }}</span>
                  </span>
                  <span
                    v-if="itemDigest(item)"
                    class="skill-provenance-pill"
                    :title="itemFullDigest(item)"
                    :data-testid="`external-digest-${item.item_id}`"
                  >
                    <ShieldCheck :size="11" aria-hidden="true" />
                    <span>SHA-256 {{ itemDigest(item) }}</span>
                  </span>
                  <span
                    v-for="varName in item.secret_placeholders ?? []"
                    :key="varName"
                    class="skill-provenance-pill"
                  >
                    <KeyRound :size="11" aria-hidden="true" />
                    <span>{{ '${' + varName + '}' }}</span>
                  </span>
                </div>

                <div
                  v-if="getSecurityWarnings(item).length"
                  class="skill-warning-details"
                  :data-testid="`external-security-warnings-${item.item_id}`"
                >
                  <span v-for="(warn, idx) in getSecurityWarnings(item)" :key="idx" class="skill-warning-detail-item">
                    {{ warn }}
                  </span>
                </div>

                <div v-if="item.kind === 'agent'" class="skill-agent-quick-row">
                  <UiButton
                    type="button"
                    size="sm"
                    variant="outline"
                    :disabled="importingAgentId === item.item_id || installing"
                    :data-testid="`import-apm-agent-${item.name}`"
                    @click.stop="importSingleAgent(item)"
                  >
                    <Bot :size="13" aria-hidden="true" />
                    {{
                      importingAgentId === item.item_id
                        ? t('toolsSettings.center.skills.importing')
                        : t('toolsSettings.center.skills.importAsCustomAgent')
                    }}
                  </UiButton>
                </div>
              </div>
            </div>
          </div>

          <div
            v-if="selectedExternalPlaceholders.length"
            class="skill-secret-box"
            data-testid="external-secret-placeholders"
          >
            <div class="skill-secret-box-head">
              <KeyRound :size="14" aria-hidden="true" />
              <strong>{{ t('toolsSettings.center.mcp.secretPlaceholdersTitle') }}</strong>
            </div>
            <p class="tool-center-hint">
              {{ t('toolsSettings.center.mcp.secretPlaceholdersHint') }}
            </p>
            <div class="skill-secret-grid">
              <label v-for="varName in selectedExternalPlaceholders" :key="varName" class="tool-center-field">
                <span class="skill-secret-var-label">{{ '${' + varName + '}' }}</span>
                <UiInput
                  type="password"
                  autocomplete="off"
                  :model-value="secretValues[varName] ?? ''"
                  :data-testid="`external-secret-input-${varName}`"
                  :placeholder="t('toolsSettings.center.mcp.secretInputPlaceholder')"
                  @update:model-value="val => updateSecretValue(varName, String(val))"
                />
              </label>
            </div>
          </div>

          <label class="tool-center-switch-inline">
            <UiSwitch v-model="enableStdioMcp" />
            <span>{{ t('toolsSettings.center.skills.enableStdioMcp') }}</span>
          </label>
        </template>

        <p v-if="error" class="error-hint" role="alert">{{ error }}</p>

        <div class="tool-center-modal-footer">
          <UiButton type="button" variant="ghost" @click="emit('close')">
            {{ t('common.cancel') }}
          </UiButton>
          <UiButton
            type="submit"
            :disabled="installing || (mode === 'external_cli' && !selectedExternalIds.length)"
            data-testid="skill-install-submit"
          >
            {{
              installing
                ? t('toolsSettings.center.skills.importing')
                : mode === 'external_cli'
                  ? t('toolsSettings.center.skills.importSelected', { count: selectedExternalIds.length })
                  : t('toolsSettings.center.skills.importBtn')
            }}
          </UiButton>
        </div>
      </form>
    </div>
  </div>
</template>

<style scoped>
.skill-external-row {
  align-items: flex-start;
}

.skill-external-meta {
  display: grid;
  gap: 5px;
  flex: 1 1 auto;
  min-width: 0;
}

.skill-provenance-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 5px;
}

.skill-provenance-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 7px;
  border-radius: 999px;
  border: 1px solid var(--border-color);
  background: var(--surface-chrome);
  color: var(--text-secondary);
  font-family: var(--font-mono);
  font-size: 11px;
  line-height: 1.4;
}

.skill-warning-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  width: fit-content;
  padding: 2px 8px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d97706) 45%, var(--border-color));
  background: color-mix(in srgb, var(--accent-warning, #d97706) 11%, transparent);
  color: var(--accent-warning, var(--text-primary));
  font-size: 11px;
  font-weight: 500;
  line-height: 1.4;
}

.skill-warning-details {
  display: grid;
  gap: 3px;
  padding: 6px 8px;
  border-radius: var(--radius-sm);
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d97706) 35%, var(--border-color));
  background: color-mix(in srgb, var(--accent-warning, #d97706) 7%, var(--surface-section));
  font-size: 11px;
  color: var(--text-secondary);
}

.skill-warning-detail-item {
  overflow-wrap: anywhere;
}

.skill-agent-quick-row {
  display: flex;
  align-items: center;
  padding-top: 2px;
}

.skill-secret-box {
  display: grid;
  gap: 8px;
  padding: 10px 12px;
  border-radius: var(--radius-md);
  border: 1px solid color-mix(in srgb, var(--accent-primary) 28%, var(--border-color));
  background: color-mix(in srgb, var(--accent-primary) 5%, var(--surface-section));
}

.skill-secret-box-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-primary);
}

.skill-secret-grid {
  display: grid;
  gap: 8px;
}

.skill-secret-var-label {
  font-family: var(--font-mono);
  font-size: 11px;
  color: var(--text-primary);
}
</style>
