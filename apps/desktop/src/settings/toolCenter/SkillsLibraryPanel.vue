<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, ChevronDown, GitCommitHorizontal, Package, Plus, RefreshCw, ShieldCheck } from '@lucide/vue'
import { UiBadge, UiButton, UiDropdownMenu, UiInput, UiSwitch } from '@/components/ui'
import { api } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState } from '@/composables/useErrorState'
import type { ToolSkillResource } from '@/settings/toolSettings'
import {
  ecosystemDisplayLabel,
  getSecurityWarnings,
  hasHiddenUnicodeWarning,
  shortCommit,
  shortDigest,
  type ExtendedInstallToolSkillResult,
  type ExtendedToolSkill,
} from './mcpFormUtils'
import SkillDetailDrawer from './SkillDetailDrawer.vue'
import SkillInstallModal, { type SkillInstallMode } from './SkillInstallModal.vue'

const props = defineProps<{
  projectId?: string
}>()

const emit = defineEmits<{
  mcpImported: []
  agentsImported: []
}>()

const { t } = useI18n()
const { notify } = useNotifications()

type ScopeFilter = 'all' | 'builtin' | 'user' | 'project'

const skills = ref<ToolSkillResource[]>([])
const loading = ref(false)
const reloading = ref(false)
const loadError = ref('')
const search = ref('')
const scopeFilter = ref<ScopeFilter>('all')
const togglingId = ref('')
const activeSkill = ref<ToolSkillResource | null>(null)
const installOpen = ref(false)
const installMode = ref<SkillInstallMode>('inline')

function normalizedScope(skill: ToolSkillResource): Exclude<ScopeFilter, 'all'> {
  const raw = (skill.source_kind || skill.scope || '').toLowerCase()
  if (raw === 'builtin' || skill.read_only) return 'builtin'
  if (raw === 'project' || skill.project_id) return 'project'
  return 'user'
}

const filteredSkills = computed(() => {
  const q = search.value.trim().toLowerCase()
  return skills.value.filter((skill) => {
    if (scopeFilter.value !== 'all' && normalizedScope(skill) !== scopeFilter.value) return false
    if (!q) return true
    return (
      skill.name.toLowerCase().includes(q)
      || (skill.description || '').toLowerCase().includes(q)
      || (skill.scope || '').toLowerCase().includes(q)
    )
  })
})

async function refresh() {
  loading.value = true
  loadError.value = ''
  try {
    const res = await api.listToolSkills(props.projectId || undefined)
    skills.value = res.skills ?? []
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    loadError.value = errState.details || errState.message
  } finally {
    loading.value = false
  }
}

async function onReloadIndex() {
  reloading.value = true
  try {
    const res = await api.reloadToolSkills(props.projectId || undefined)
    skills.value = res.skills ?? []
    notify.success(t('toolsSettings.center.skills.reloaded'))
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    notify.error(errState.details || errState.message)
  } finally {
    reloading.value = false
  }
}

async function onToggleEnabled(skill: ToolSkillResource, next: boolean) {
  togglingId.value = skill.resource_id
  try {
    const updated = await api.setToolSkillEnabled(skill.resource_id, next, props.projectId || undefined)
    skills.value = skills.value.map(item => (item.resource_id === skill.resource_id ? updated : item))
    if (activeSkill.value?.resource_id === skill.resource_id) activeSkill.value = updated
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    notify.error(errState.details || errState.message)
  } finally {
    togglingId.value = ''
  }
}

function openInstall(mode: SkillInstallMode) {
  installMode.value = mode
  installOpen.value = true
}

function onInstalled(result: ExtendedInstallToolSkillResult) {
  installOpen.value = false
  void refresh()
  if (result.installed_mcp_servers?.length) emit('mcpImported')
}

function skillHasUnicodeRisk(skill: ToolSkillResource): boolean {
  return hasHiddenUnicodeWarning(skill as ExtendedToolSkill)
}

function skillSecurityWarnings(skill: ToolSkillResource): string[] {
  return getSecurityWarnings(skill as ExtendedToolSkill)
}

function skillCommit(skill: ToolSkillResource): string {
  return shortCommit(skill.commit)
}

function skillDigest(skill: ToolSkillResource): string {
  return shortDigest(skill.package_hash || skill.content_hash)
}

function isApmSkill(skill: ToolSkillResource): boolean {
  return (skill.source || '').toLowerCase() === 'apm' || (skill.source_kind || '').toLowerCase() === 'apm'
}

function onSkillUpdated(updated: ToolSkillResource) {
  skills.value = skills.value.map(item => (item.resource_id === updated.resource_id ? updated : item))
  activeSkill.value = updated
  void refresh()
}

function onSkillDeleted(resourceId: string) {
  skills.value = skills.value.filter(item => item.resource_id !== resourceId)
  activeSkill.value = null
}

watch(() => props.projectId, () => {
  void refresh()
})

onMounted(() => {
  void refresh()
})

defineExpose({ refresh })
</script>

<template>
  <div class="tool-center-panel" data-testid="skills-library-panel">
    <div class="tool-center-toolbar">
      <div class="tool-center-toolbar-left">
        <UiInput
          v-model="search"
          class="tool-center-search"
          :placeholder="t('toolsSettings.center.skills.searchPlaceholder')"
        />
        <div class="tool-center-chip-row">
          <button
            v-for="f in ([
              { key: 'all', label: 'toolsSettings.center.skills.filterAll' },
              { key: 'builtin', label: 'toolsSettings.center.skills.filterBuiltin' },
              { key: 'user', label: 'toolsSettings.center.skills.filterUser' },
              { key: 'project', label: 'toolsSettings.center.skills.filterProject' },
            ] as const)"
            :key="f.key"
            type="button"
            class="tool-center-filter-chip"
            :class="{ active: scopeFilter === f.key }"
            @click="scopeFilter = f.key"
          >
            {{ t(f.label) }}
          </button>
        </div>
      </div>

      <div class="tool-center-inline-row">
        <UiButton variant="outline" :disabled="reloading" @click="onReloadIndex">
          <RefreshCw :size="14" />
          {{ t('toolsSettings.center.skills.reloadIndex') }}
        </UiButton>
        <UiDropdownMenu align="end">
          <template #trigger>
            <UiButton data-testid="add-skill-btn">
              <Plus :size="15" />
              {{ t('toolsSettings.center.skills.addBtn') }}
              <ChevronDown :size="14" />
            </UiButton>
          </template>
          <button type="button" class="ui-dropdown-item" @click="openInstall('inline')">
            {{ t('toolsSettings.center.skills.addCreate') }}
          </button>
          <button type="button" class="ui-dropdown-item" @click="openInstall('local_dir')">
            {{ t('toolsSettings.center.skills.addLocalDir') }}
          </button>
          <button type="button" class="ui-dropdown-item" @click="openInstall('git_url')">
            {{ t('toolsSettings.center.skills.addGitUrl') }}
          </button>
          <button
            type="button"
            class="ui-dropdown-item"
            data-testid="add-skill-external-cli"
            @click="openInstall('external_cli')"
          >
            {{ t('toolsSettings.center.skills.addExternalCliWithApm') }}
          </button>
        </UiDropdownMenu>
      </div>
    </div>

    <p v-if="loadError" class="error-hint" role="alert">{{ loadError }}</p>

    <div v-if="loading && !skills.length" class="tool-center-empty">
      {{ t('common.loading') }}
    </div>
    <div v-else-if="!filteredSkills.length" class="tool-center-empty">
      <Package :size="24" />
      <span>{{ t('toolsSettings.center.skills.empty') }}</span>
    </div>

    <div v-else class="tool-center-card-grid">
      <article
        v-for="skill in filteredSkills"
        :key="skill.resource_id"
        class="tool-center-card tool-center-card--interactive"
        :data-testid="`skill-card-${skill.name}`"
        @click="activeSkill = skill"
      >
        <div class="tool-center-card-head">
          <div class="tool-center-card-title-group">
            <strong class="tool-center-card-title">{{ skill.name }}</strong>
          </div>
          <div @click.stop>
            <UiSwitch
              :model-value="skill.enabled"
              :disabled="togglingId === skill.resource_id"
              :aria-label="skill.name"
              @update:model-value="val => onToggleEnabled(skill, Boolean(val))"
            />
          </div>
        </div>

        <div class="tool-center-badge-row">
          <UiBadge variant="outline">{{ normalizedScope(skill) }}</UiBadge>
          <UiBadge v-if="isApmSkill(skill)" variant="outline">{{ ecosystemDisplayLabel('apm') }}</UiBadge>
          <UiBadge v-if="skill.package_files?.length" variant="outline">
            {{ skill.package_files.length }} {{ t('toolsSettings.center.skills.subFilesTitle') }}
          </UiBadge>
          <UiBadge v-if="skill.valid === false" variant="outline">{{ t('toolsSettings.invalid') }}</UiBadge>
          <span
            v-if="skillHasUnicodeRisk(skill)"
            class="skill-card-warning-pill"
            :title="skillSecurityWarnings(skill).join('; ')"
            :data-testid="`skill-unicode-warning-${skill.name}`"
          >
            <AlertTriangle :size="11" aria-hidden="true" />
            <span>{{ t('toolsSettings.center.security.hiddenUnicode') }}</span>
          </span>
        </div>

        <div v-if="skillCommit(skill) || skillDigest(skill)" class="skill-card-provenance-row">
          <span
            v-if="skillCommit(skill)"
            class="skill-card-provenance-pill"
            :title="`Commit: ${skill.commit}`"
            :data-testid="`skill-commit-${skill.name}`"
          >
            <GitCommitHorizontal :size="11" aria-hidden="true" />
            <span>Commit {{ skillCommit(skill) }}</span>
          </span>
          <span
            v-if="skillDigest(skill)"
            class="skill-card-provenance-pill"
            :title="skill.package_hash || skill.content_hash || ''"
            :data-testid="`skill-digest-${skill.name}`"
          >
            <ShieldCheck :size="11" aria-hidden="true" />
            <span>SHA-256 {{ skillDigest(skill) }}</span>
          </span>
        </div>

        <p class="setting-desc tool-center-skill-desc">{{ skill.description }}</p>
      </article>
    </div>

    <SkillDetailDrawer
      :open="Boolean(activeSkill)"
      :skill="activeSkill"
      :project-id="projectId"
      @close="activeSkill = null"
      @updated="onSkillUpdated"
      @deleted="onSkillDeleted"
    />

    <SkillInstallModal
      :open="installOpen"
      :initial-mode="installMode"
      :project-id="projectId"
      @close="installOpen = false"
      @installed="onInstalled"
      @agents-imported="emit('agentsImported')"
    />
  </div>
</template>

<style scoped>
.skill-card-provenance-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 5px;
}

.skill-card-provenance-pill,
.skill-card-warning-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 7px;
  border-radius: 999px;
  font-size: 11px;
  line-height: 1.4;
}

.skill-card-provenance-pill {
  border: 1px solid var(--border-color);
  background: var(--surface-chrome);
  color: var(--text-secondary);
  font-family: var(--font-mono);
}

.skill-card-warning-pill {
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d97706) 45%, var(--border-color));
  background: color-mix(in srgb, var(--accent-warning, #d97706) 11%, transparent);
  color: var(--accent-warning, var(--text-primary));
  font-weight: 500;
}
</style>
