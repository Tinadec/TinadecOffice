<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowDown, ArrowUp, Copy, Info, Plus, RefreshCw, Trash2, X } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiSelectField } from '@/components/ui'
import type { AgentModeDto, AgentModeTopologyDto, AgentViewDto } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import {
  agentUsableForTeam,
  normalizeLayer,
  useAgentModesStore,
} from '@/composables/useAgentModesStore'
import { useHostAccess } from '@/lib/hostAccess'
import {
  buildTeamTopology,
  classifyTeamShape,
  defaultMemberScope,
  MAX_TEAM_MEMBERS,
  type MemberScope,
  type TeamStyle,
} from './teamShape'
import { TEAM_TEMPLATES, pickLeader, pickMembers, type TeamTemplateDefinition } from './teamTemplates'

const props = withDefaults(defineProps<{
  modes?: AgentModeDto[]
  agents?: AgentViewDto[]
  canAccessBackend?: boolean
  loading?: boolean
}>(), {
  modes: () => [],
  agents: () => [],
  canAccessBackend: undefined,
  loading: false,
})

const emit = defineEmits<{
  changed: []
  'open-graph': [id: string]
}>()

const { t, locale } = useI18n()
const { notify } = useNotifications()
const { canAccessBackend: hostCanAccessBackend } = useHostAccess()
const store = useAgentModesStore()

const query = ref('')
const creatingNew = ref(false)
const templates = TEAM_TEMPLATES

const effectiveCanAccessBackend = computed(() =>
  props.canAccessBackend !== undefined ? props.canAccessBackend : hostCanAccessBackend.value,
)

const selectedId = computed(() => store.selectedModeId.value)
const detail = computed(() => store.selectedModeDetail.value)
const detailLoading = computed(() => store.detailLoading.value)
const detailFailure = computed(() => store.detailFailure.value)
const busy = computed(() => store.modeBusy.value)
const settingDefault = computed(() => store.settingDefault.value)
const defaultModeId = computed(() => store.defaultModeId.value)
const selectedMode = computed(() => store.selectedMode.value)
const readOnly = computed(() => store.selectedModeReadOnly.value)
const isPackManaged = computed(() => store.isModePackManaged(selectedMode.value))

const allAgentViews = computed(() => store.agentViews.value)
const operationAgents = computed(() => allAgentViews.value.filter(agent => normalizeLayer(agent.layer) !== 'execution'))
const executionAgents = computed(() => allAgentViews.value.filter(agent => normalizeLayer(agent.layer) === 'execution'))

const shape = computed(() => detail.value ? classifyTeamShape(detail.value, allAgentViews.value) : null)
const canEditShape = computed(() => shape.value?.editable ?? false)

const filteredModes = computed(() => {
  const q = query.value.trim().toLowerCase()
  const sorted = store.sortedModes.value
  if (!q) return sorted
  return sorted.filter((mode) => {
    const name = store.modeLocalizedName(mode, locale.value)
    const desc = store.modeLocalizedDescription(mode, locale.value)
    return `${name} ${desc} ${mode.slug ?? ''}`.toLowerCase().includes(q)
  })
})

const editingName = computed({
  get: () => store.teamDraft.value.displayName,
  set: (value: string | number) => { store.teamDraft.value.displayName = String(value) },
})

const editingDescription = computed({
  get: () => store.teamDraft.value.description,
  set: (value: string) => { store.teamDraft.value.description = value },
})

const editingInstructions = computed({
  get: () => store.teamDraft.value.instructions,
  set: (value: string) => { store.teamDraft.value.instructions = value },
})

const selection = computed(() => store.teamDraft.value.selection)
const memberScopes = computed(() => store.teamDraft.value.memberScopes)

const leaderOptions = computed(() => operationAgents.value
  .filter(agentUsableForTeam)
  .map(agent => ({
    value: agent.id,
    label: store.agentLocalizedName(agent, locale.value),
  })))

/**
 * Team style spawns node-less templates, so any usable agent can be a member.
 * Pipeline style materializes execution nodes, and Core requires the node layer
 * to match the agent layer.
 */
const memberPool = computed(() => {
  const style = selection.value.style
  if (style === 'solo') return []
  const base = allAgentViews.value.filter(agent => agent.id !== selection.value.leaderId && agentUsableForTeam(agent))
  return style === 'pipeline' ? base.filter(agent => normalizeLayer(agent.layer) === 'execution') : base
})

const memberRows = computed(() => selection.value.memberIds
  .map(id => allAgentViews.value.find(agent => agent.id === id))
  .filter((agent): agent is AgentViewDto => Boolean(agent)))

const availableMembers = computed(() => memberPool.value
  .filter(agent => !selection.value.memberIds.includes(agent.id)))

const memberLimitReached = computed(() => selection.value.memberIds.length >= MAX_TEAM_MEMBERS)
const unavailableAgents = computed(() => allAgentViews.value.filter(agent =>
  agent.id !== selection.value.leaderId && !agentUsableForTeam(agent) && !selection.value.memberIds.includes(agent.id)))
const selectedLeader = computed(() => allAgentViews.value.find(agent => agent.id === selection.value.leaderId) ?? null)
const isDefaultMode = computed(() => Boolean(selectedId.value && defaultModeId.value === selectedId.value))
const canSetDefault = computed(() => {
  const status = detail.value?.status ?? selectedMode.value?.status
  const versionId = detail.value?.latest_published_mode_version_id ?? selectedMode.value?.latest_published_mode_version_id
  return status === 'published' && Boolean(versionId)
})

const teamErrors = computed<string[]>(() => {
  const errors: string[] = []
  const name = editingName.value.trim()
  if (!name) errors.push(t('settings.teamNameRequired'))
  else if (store.modes.value.some(mode => mode.id !== selectedId.value && mode.display_name.trim().toLowerCase() === name.toLowerCase())) {
    errors.push(t('settings.teamNameTaken'))
  }
  if (!selection.value.leaderId) errors.push(t('settings.teamLeaderRequired'))
  else if (selectedLeader.value && (!operationAgents.value.some(agent => agent.id === selectedLeader.value?.id) || !agentUsableForTeam(selectedLeader.value))) {
    errors.push(t('settings.teamLeaderUnavailable'))
  }
  if (selection.value.style !== 'solo' && selection.value.memberIds.length === 0) {
    errors.push(t('settings.teamMembersRequired'))
  }
  if (selection.value.memberIds.length > MAX_TEAM_MEMBERS) {
    errors.push(t('settings.teamMembersLimit', { max: MAX_TEAM_MEMBERS }))
  }
  const unavailable = memberRows.value.filter(agent => !agentUsableForTeam(agent) || !memberPool.value.some(candidate => candidate.id === agent.id))
  if (unavailable.length > 0) {
    errors.push(t('settings.teamMemberUnavailable', { names: unavailable.map(agent => store.agentLocalizedName(agent, locale.value)).join(', ') }))
  }
  return errors
})

function layerDisplay(layer?: string): string {
  return normalizeLayer(layer) === 'execution'
    ? t('agentCenter.filter.executionLayer')
    : t('agentCenter.filter.operationLayer')
}

function agentOf(id: string): AgentViewDto | undefined {
  return allAgentViews.value.find(agent => agent.id === id)
}

function modeStatusLabel(mode: Pick<AgentModeTopologyDto, 'managed' | 'pack_managed' | 'status'>): string {
  if (store.isModePackManaged(mode)) return t('settings.teamBuiltIn')
  if (mode.status === 'published') return t('settings.teamStatusPublished')
  if (mode.status === 'archived') return t('settings.teamArchived')
  return t('settings.teamDraft')
}

function scopeOf(id: string): MemberScope {
  const agent = agentOf(id)
  return memberScopes.value[id] ?? (agent ? defaultMemberScope(agent) : { read: true, write: false })
}

function toggleScope(id: string, key: 'read' | 'write') {
  if (readOnly.value) return
  const current = scopeOf(id)
  store.teamDraft.value = {
    ...store.teamDraft.value,
    memberScopes: {
      ...store.teamDraft.value.memberScopes,
      [id]: { ...current, [key]: !current[key] },
    },
  }
}

function eligibleForStyle(style: TeamStyle, leaderId: string): AgentViewDto[] {
  if (style === 'solo') return []
  const base = allAgentViews.value.filter(agent => agent.id !== leaderId && agentUsableForTeam(agent))
  return style === 'pipeline' ? base.filter(agent => normalizeLayer(agent.layer) === 'execution') : base
}

async function selectMode(mode: Pick<AgentModeTopologyDto, 'id'>) {
  if (creatingNew.value && !(await store.discardOrConfirm())) return
  creatingNew.value = false
  await store.selectMode(mode.id)
}

async function openTemplatePicker() {
  if (!effectiveCanAccessBackend.value) return
  if (!(await store.discardOrConfirm())) return
  creatingNew.value = true
}

function setLeaderId(leaderId: string) {
  if (readOnly.value) return
  const allowed = eligibleForStyle(selection.value.style, leaderId)
  const kept = selection.value.memberIds.filter(id => id !== leaderId && allowed.some(agent => agent.id === id))
  store.teamDraft.value = {
    ...store.teamDraft.value,
    selection: { ...selection.value, leaderId, memberIds: kept },
  }
}

function setStyle(style: TeamStyle) {
  if (readOnly.value) return
  const allowed = eligibleForStyle(style, selection.value.leaderId)
  const kept = selection.value.memberIds.filter(id => allowed.some(agent => agent.id === id))
  const nextScopes = { ...store.teamDraft.value.memberScopes }
  let memberIds: string[] = []
  if (style !== 'solo') {
    memberIds = kept.length > 0 ? kept : allowed.slice(0, 2).map(agent => agent.id)
    for (const id of memberIds) {
      const agent = agentOf(id)
      if (agent && !nextScopes[id]) nextScopes[id] = defaultMemberScope(agent)
    }
  }
  store.teamDraft.value = {
    ...store.teamDraft.value,
    orchestrationTier: null,
    selection: { ...selection.value, style, memberIds },
    memberScopes: nextScopes,
  }
}

function toggleMember(id: string) {
  if (readOnly.value) return
  const members = [...selection.value.memberIds]
  const nextScopes = { ...store.teamDraft.value.memberScopes }
  const index = members.indexOf(id)
  if (index >= 0) {
    members.splice(index, 1)
  } else {
    if (members.length >= MAX_TEAM_MEMBERS) return
    if (!memberPool.value.some(agent => agent.id === id)) return
    members.push(id)
    const agent = agentOf(id)
    if (agent) nextScopes[id] = defaultMemberScope(agent)
  }
  store.teamDraft.value = {
    ...store.teamDraft.value,
    selection: { ...selection.value, memberIds: members },
    memberScopes: nextScopes,
  }
}

function moveMember(index: number, delta: number) {
  if (readOnly.value) return
  const members = [...selection.value.memberIds]
  const target = index + delta
  if (target < 0 || target >= members.length) return
  ;[members[index], members[target]] = [members[target], members[index]]
  store.teamDraft.value = {
    ...store.teamDraft.value,
    selection: { ...selection.value, memberIds: members },
  }
}

async function saveDraft(): Promise<string | null> {
  if (!effectiveCanAccessBackend.value) return null
  if (detail.value && !canEditShape.value) return null
  const savedId = await store.saveTeamDraft()
  if (savedId) emit('changed')
  return savedId
}

async function publishTeam() {
  if (!effectiveCanAccessBackend.value) return
  if (detail.value && !canEditShape.value) return
  if (teamErrors.value.length > 0) {
    notify.error(new Error(teamErrors.value[0]))
    return
  }
  const ok = await store.publishTeam()
  if (ok) emit('changed')
}

async function createFromTemplate(template: TeamTemplateDefinition) {
  if (!effectiveCanAccessBackend.value) return
  const leaderId = pickLeader(operationAgents.value.filter(agentUsableForTeam))
  if (!leaderId) {
    notify.error(new Error(t('settings.teamNoLeader')))
    return
  }
  const pool = template.key === 'pipeline'
    ? executionAgents.value.filter(agentUsableForTeam)
    : allAgentViews.value.filter(agent => agentUsableForTeam(agent) && agent.id !== leaderId)
  const memberIds = template.key === 'solo' ? [] : pickMembers(template, pool).slice(0, MAX_TEAM_MEMBERS)
  if (template.key !== 'solo' && memberIds.length === 0) {
    notify.error(new Error(t('settings.teamTemplateNoMembers')))
    return
  }
  const name = t(template.labelKey)
  const topology = buildTeamTopology({
    style: template.key,
    leaderId,
    memberIds,
    instructions: template.instructionsKey ? t(template.instructionsKey) : '',
    displayName: name,
    description: t(template.hintKey),
  }, allAgentViews.value)

  const created = await store.createMode({
    displayName: name,
    slug: `team-${Date.now().toString(36)}`,
    topology,
  })
  if (created) {
    creatingNew.value = false
    emit('changed')
  }
}

async function cloneToMine() {
  if (!selectedId.value) return
  const created = await store.cloneMode(selectedId.value)
  if (created) {
    creatingNew.value = false
    emit('changed')
  }
}

async function deleteCurrentTeam() {
  if (!selectedId.value || isPackManaged.value) return
  const deleted = await store.deleteOrArchiveMode(selectedId.value)
  if (deleted) emit('changed')
}

async function setAsDefault() {
  if (!selectedId.value || !canSetDefault.value || isDefaultMode.value) return
  const ok = await store.setAsDefaultMode(selectedId.value)
  if (ok) emit('changed')
}

function openGraph() {
  if (selectedId.value) emit('open-graph', selectedId.value)
}

function reload() {
  if (effectiveCanAccessBackend.value) {
    void store.loadAgents({ force: true })
    void store.loadWorkspaceDefaults()
    if (selectedId.value) void store.selectMode(selectedId.value, { force: true })
    else void store.loadModes({ force: true })
  }
}

defineExpose({ reload })

watch(() => props.modes, (modes) => {
  if (modes?.length) {
    store.mergeExternalModes(modes as AgentModeTopologyDto[])
    if (!store.selectedModeId.value && !creatingNew.value) {
      const first = store.sortedModes.value[0]
      if (first) void store.selectMode(first.id, { force: true })
    }
  }
}, { immediate: true })

watch(() => props.agents, (agents) => {
  if (agents?.length) store.mergeExternalAgents(agents)
}, { immediate: true })

watch(effectiveCanAccessBackend, (ready) => {
  if (ready) {
    void store.loadAgents()
    void store.loadWorkspaceDefaults()
    if (store.modes.value.length === 0) void store.loadModes()
    else if (!store.selectedModeId.value && !creatingNew.value) {
      const first = store.sortedModes.value[0]
      if (first) void store.selectMode(first.id, { force: true })
    }
  }
})

onMounted(() => {
  if (effectiveCanAccessBackend.value) {
    void store.loadAgents()
    void store.loadWorkspaceDefaults()
    if (store.modes.value.length === 0) void store.loadModes()
    else if (!store.selectedModeId.value && !creatingNew.value) {
      const first = store.sortedModes.value[0]
      if (first) void store.selectMode(first.id, { force: true })
    } else if (store.selectedModeId.value && !store.selectedModeDetail.value) {
      void store.selectMode(store.selectedModeId.value, { force: true })
    }
  }
})
</script>

<template>
  <div class="team-center" data-testid="agent-teams">
    <aside class="team-rail">
      <div class="team-rail-head">
        <UiInput v-model="query" :placeholder="t('settings.teamSearch')" />
        <UiButton
          size="sm"
          variant="outline"
          class="team-new-btn"
          data-testid="team-new-btn"
          :disabled="!effectiveCanAccessBackend || busy"
          @click="openTemplatePicker"
        >
          <Plus :size="14" />
          <span>{{ t('settings.teamNew') }}</span>
        </UiButton>
      </div>
      <button
        v-for="mode in filteredModes"
        :key="mode.id"
        type="button"
        class="team-row"
        :class="{ active: !creatingNew && selectedId === mode.id }"
        @click="selectMode(mode)"
      >
        <div class="team-row-main">
          <strong>{{ store.modeLocalizedName(mode, locale) }}</strong>
          <span>{{ store.modeLocalizedDescription(mode, locale) || mode.slug || '—' }}</span>
        </div>
        <UiBadge :variant="isPackManaged || mode.managed || mode.pack_managed || mode.status === 'published' ? 'secondary' : 'outline'">
          {{ modeStatusLabel(mode) }}
        </UiBadge>
      </button>
      <div v-if="(loading || store.modesLoading.value) && !filteredModes.length" class="team-rail-loading" aria-live="polite">
        <RefreshCw :size="14" class="center-stale-spinner" aria-hidden="true" />
        <span>{{ t('settings.refreshing') }}</span>
      </div>
      <p v-else-if="!filteredModes.length" class="team-rail-empty">{{ t('settings.teamEmptyList') }}</p>
    </aside>

    <main class="team-stage">
      <div v-if="creatingNew || (!detail && !detailLoading && !detailFailure)" class="team-welcome">
        <div class="team-welcome-head">
          <div>
            <h3>{{ t('settings.teamWelcomeTitle') }}</h3>
            <p>{{ t('settings.teamWelcomeHint') }}</p>
          </div>
          <UiButton v-if="creatingNew && detail" size="sm" variant="ghost" @click="creatingNew = false">
            {{ t('settings.teamBackToEditor') }}
          </UiButton>
        </div>
        <div class="team-template-grid">
          <button
            v-for="template in templates"
            :key="template.key"
            type="button"
            class="team-template-card"
            :disabled="!effectiveCanAccessBackend || busy"
            @click="createFromTemplate(template)"
          >
            <strong>{{ t(template.labelKey) }}</strong>
            <span>{{ t(template.hintKey) }}</span>
          </button>
        </div>
      </div>

      <div v-else-if="detailLoading" class="team-stage-placeholder" aria-live="polite">
        <RefreshCw :size="16" class="center-stale-spinner" aria-hidden="true" />
        <span>{{ t('settings.teamLoading') }}</span>
      </div>

      <div v-else-if="detailFailure" class="team-stage-placeholder error" role="alert">
        <Info :size="16" aria-hidden="true" />
        <div>
          <strong>{{ t('settings.centerLoadFailed') }}</strong>
          <p>{{ detailFailure }}</p>
        </div>
        <UiButton variant="outline" size="sm" @click="reload">{{ t('settings.retry') }}</UiButton>
      </div>

      <div v-else-if="detail" class="team-editor">
        <header class="team-editor-head">
          <div class="team-editor-title">
            <h3>{{ editingName || store.modeLocalizedName(detail, locale) }}</h3>
            <p>{{ readOnly ? t('settings.teamReadOnlyHint') : t('settings.teamEditorHint') }}</p>
          </div>
          <div class="team-editor-actions">
            <UiButton
              v-if="canSetDefault"
              variant="outline"
              :disabled="settingDefault || isDefaultMode"
              @click="setAsDefault"
            >
              {{ isDefaultMode ? t('settings.teamDefaultCurrent') : t('settings.teamSetDefault') }}
            </UiButton>
            <UiButton
              :variant="readOnly ? 'default' : 'outline'"
              :disabled="busy || !effectiveCanAccessBackend"
              @click="cloneToMine"
            >
              <Copy :size="14" />
              <span>{{ t('settings.teamClone') }}</span>
            </UiButton>
            <template v-if="!readOnly">
              <UiButton
                variant="outline"
                :disabled="busy || !effectiveCanAccessBackend || !canEditShape"
                @click="saveDraft"
              >
                {{ t('settings.teamSaveDraft') }}
              </UiButton>
              <UiButton
                :disabled="busy || !effectiveCanAccessBackend || !canEditShape || teamErrors.length > 0"
                @click="publishTeam"
              >
                {{ t('settings.teamPublish') }}
              </UiButton>
            </template>
            <UiButton
              v-if="!isPackManaged"
              variant="ghost"
              class="team-delete-btn"
              :disabled="busy || !effectiveCanAccessBackend"
              :title="t('settings.teamDelete')"
              @click="deleteCurrentTeam"
            >
              <Trash2 :size="14" />
            </UiButton>
            <UiButton variant="ghost" @click="openGraph">{{ t('settings.teamOpenGraph') }}</UiButton>
          </div>
        </header>

        <div class="team-summary">
          <UiBadge variant="outline">{{ selection.style }}</UiBadge>
          <span v-if="selectedLeader">{{ t('settings.teamLeader') }} · {{ store.agentLocalizedName(selectedLeader, locale) }}</span>
          <span>{{ t('settings.teamMemberCount', { count: memberRows.length, max: MAX_TEAM_MEMBERS }) }}</span>
          <UiBadge v-if="isDefaultMode" variant="secondary">{{ t('settings.teamDefaultCurrent') }}</UiBadge>
          <UiBadge v-if="store.teamDirty.value && !readOnly" variant="outline" class="team-dirty-badge">
            {{ t('settings.modeUnsaved') }}
          </UiBadge>
        </div>

        <div v-if="teamErrors.length > 0 && !readOnly && canEditShape" class="team-errors" role="alert">
          <p v-for="error in teamErrors" :key="error">{{ error }}</p>
        </div>

        <div v-if="!canEditShape" class="team-shape-notice" role="status">
          <Info :size="15" aria-hidden="true" />
          <span>{{ t('settings.teamComplexShape') }}</span>
        </div>

        <div v-else class="team-form">
          <label class="team-field">
            <span>{{ t('settings.teamName') }}</span>
            <UiInput v-model="editingName" :disabled="readOnly" />
          </label>
          <label class="team-field">
            <span>{{ t('settings.teamDescription') }}</span>
            <textarea v-model="editingDescription" class="team-textarea" rows="2" :disabled="readOnly"></textarea>
          </label>
          <label class="team-field">
            <span>{{ t('settings.teamInstructions') }}</span>
            <textarea
              v-model="editingInstructions"
              class="team-textarea"
              rows="4"
              maxlength="4000"
              :disabled="readOnly"
              :placeholder="t('settings.teamInstructionsPlaceholder')"
            ></textarea>
            <small class="team-hint">{{ editingInstructions.length }} / 4000</small>
          </label>

          <div class="team-field">
            <span>{{ t('settings.teamStyle') }}</span>
            <div class="team-style-row" role="radiogroup" :aria-label="t('settings.teamStyle')">
              <button
                v-for="template in templates"
                :key="template.key"
                type="button"
                role="radio"
                :aria-checked="selection.style === template.key"
                :class="{ active: selection.style === template.key }"
                :disabled="readOnly"
                @click="setStyle(template.key)"
              >
                <strong>{{ t(template.labelKey) }}</strong>
                <span>{{ t(template.hintKey) }}</span>
              </button>
            </div>
          </div>

          <label class="team-field">
            <span>{{ t('settings.teamLeader') }}</span>
            <UiSelectField
              :model-value="selection.leaderId"
              :aria-label="t('settings.teamLeader')"
              :options="leaderOptions"
              :disabled="readOnly"
              @update:model-value="setLeaderId"
            />
          </label>

          <div v-if="selection.style !== 'solo'" class="team-field">
            <span>{{ t('settings.teamMembers') }} · {{ memberRows.length }} / {{ MAX_TEAM_MEMBERS }}</span>
            <div v-if="memberRows.length" class="team-member-list" role="list">
              <div v-for="(agent, index) in memberRows" :key="agent.id" class="team-member-row" role="listitem">
                <div class="team-member-main">
                  <strong>{{ store.agentLocalizedName(agent, locale) }}</strong>
                  <span>{{ store.agentLocalizedDescription(agent, locale) || '—' }}</span>
                </div>
                <em class="team-member-layer">{{ layerDisplay(agent.layer) }}</em>
                <div class="team-member-scope" role="group" :aria-label="t('settings.teamMemberScope')">
                  <button
                    type="button"
                    :class="{ active: scopeOf(agent.id).read }"
                    :aria-pressed="scopeOf(agent.id).read"
                    :disabled="readOnly"
                    @click="toggleScope(agent.id, 'read')"
                  >{{ t('settings.teamMemberRead') }}</button>
                  <button
                    type="button"
                    :class="{ active: scopeOf(agent.id).write }"
                    :aria-pressed="scopeOf(agent.id).write"
                    :disabled="readOnly"
                    @click="toggleScope(agent.id, 'write')"
                  >{{ t('settings.teamMemberWrite') }}</button>
                </div>
                <div class="team-member-actions">
                  <button type="button" :disabled="readOnly || index === 0" :aria-label="t('settings.teamMoveUp')" @click="moveMember(index, -1)">
                    <ArrowUp :size="13" />
                  </button>
                  <button type="button" :disabled="readOnly || index === memberRows.length - 1" :aria-label="t('settings.teamMoveDown')" @click="moveMember(index, 1)">
                    <ArrowDown :size="13" />
                  </button>
                  <button type="button" :disabled="readOnly" :aria-label="t('settings.teamRemoveMember')" @click="toggleMember(agent.id)">
                    <X :size="13" />
                  </button>
                </div>
              </div>
            </div>
            <p v-else class="team-hint">{{ t('settings.teamNoMembers') }}</p>
            <div v-if="availableMembers.length" class="team-member-pool">
              <button
                v-for="agent in availableMembers"
                :key="agent.id"
                type="button"
                :disabled="readOnly || busy || memberLimitReached"
                @click="toggleMember(agent.id)"
              >
                <Plus :size="12" />
                <span>{{ store.agentLocalizedName(agent, locale) }}</span>
                <em>{{ layerDisplay(agent.layer) }}</em>
              </button>
            </div>
            <p v-if="memberLimitReached" class="team-hint">{{ t('settings.teamMembersLimit', { max: MAX_TEAM_MEMBERS }) }}</p>
            <p v-else-if="availableMembers.length === 0 && selection.style === 'pipeline'" class="team-hint">{{ t('settings.teamNoExecutionAgents') }}</p>
            <p v-if="unavailableAgents.length > 0" class="team-hint">
              {{ t('settings.teamUnavailableAgents', { names: unavailableAgents.map(agent => store.agentLocalizedName(agent, locale)).join(', ') }) }}
            </p>
          </div>
        </div>
      </div>
    </main>
  </div>
</template>

<style scoped>
.team-center {
  display: grid;
  grid-template-columns: minmax(220px, 280px) minmax(0, 1fr);
  gap: 12px;
  min-height: 420px;
}

.team-rail {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
  max-height: 620px;
  overflow: auto;
  padding-right: 4px;
}

.team-rail-head {
  position: sticky;
  top: 0;
  display: grid;
  gap: 6px;
  padding-bottom: 6px;
  background: var(--surface-section);
  z-index: 1;
}

.team-new-btn {
  width: 100%;
  justify-content: center;
}

.team-rail-loading, .team-rail-empty {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 10px 4px;
  color: var(--text-muted);
  font-size: 12px;
}

.team-row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 9px 10px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
  transition: border-color 140ms ease, background-color 140ms ease, transform 90ms ease;
}
.team-row:hover { background: var(--surface-hover); }
.team-row.active { background: var(--surface-selected); }
.team-row-main { display: flex; flex-direction: column; gap: 2px; min-width: 0; flex: 1; }
.team-row-main strong, .team-row-main span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.team-row-main strong { font-size: 13px; }
.team-row-main span { color: var(--text-secondary); font-size: 11px; }

.team-stage { min-width: 0; }
.team-stage-placeholder { display: flex; align-items: center; gap: 10px; padding: 28px 16px; color: var(--text-secondary); font-size: 13px; }
.team-stage-placeholder.error { align-items: flex-start; border: 1px solid var(--border-muted); border-radius: 10px; background: var(--surface-section); }
.team-stage-placeholder.error p { margin: 4px 0 0; color: var(--text-secondary); }

.team-welcome { display: flex; flex-direction: column; gap: 18px; padding: 18px; border: 1px solid var(--border-muted); border-radius: 12px; background: var(--surface-section); }
.team-welcome-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; }
.team-welcome h3 { margin: 0 0 6px; }
.team-welcome p { margin: 0; color: var(--text-secondary); font-size: 13px; }
.team-template-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; }
.team-template-card { display: flex; flex-direction: column; gap: 6px; padding: 14px; border: 1px solid var(--border-muted); border-radius: 10px; background: var(--surface-raised); color: var(--text-primary); text-align: left; cursor: pointer; transition: border-color 140ms ease, background-color 140ms ease, transform 90ms ease; }
.team-template-card:hover { border-color: var(--accent-primary); }
.team-template-card span { color: var(--text-secondary); font-size: 12px; line-height: 1.45; }

.team-editor { display: flex; flex-direction: column; gap: 12px; }
.team-editor-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
.team-editor-title h3 { margin: 0 0 4px; }
.team-editor-title p { margin: 0; color: var(--text-secondary); font-size: 12px; }
.team-editor-actions { display: flex; gap: 6px; flex-wrap: wrap; align-items: center; }
.team-delete-btn { color: var(--accent-danger, #ef4444); }
.team-dirty-badge { border-color: color-mix(in srgb, var(--accent-warning, #d97706) 45%, var(--border-muted)); color: var(--accent-warning, #d97706); }

.team-summary { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 8px 10px; border: 1px solid var(--border-muted); border-radius: 8px; background: var(--surface-section); font-size: 12px; color: var(--text-secondary); }
.team-errors { display: grid; gap: 4px; padding: 8px 10px; border: 1px solid color-mix(in srgb, var(--accent-danger) 30%, var(--border-muted)); border-radius: 8px; background: color-mix(in srgb, var(--accent-danger) 6%, var(--surface-section)); }
.team-errors p { margin: 0; font-size: 12px; color: var(--accent-danger); }
.team-member-layer { font-style: normal; font-size: 10px; color: var(--text-muted); padding: 1px 6px; border-radius: 999px; background: var(--surface-section); }
.team-member-pool button em { font-style: normal; color: var(--text-muted); font-size: 9px; }

.team-shape-notice { display: flex; align-items: center; gap: 8px; padding: 10px 12px; border: 1px solid color-mix(in srgb, var(--accent-warning) 35%, transparent); border-radius: 8px; background: color-mix(in srgb, var(--accent-warning) 10%, var(--surface-section)); color: var(--text-secondary); font-size: 12px; }

.team-form { display: flex; flex-direction: column; gap: 14px; }
.team-field { display: flex; flex-direction: column; gap: 6px; font-size: 12px; color: var(--text-secondary); }
.team-textarea { width: 100%; padding: 8px 10px; border: 1px solid var(--border-muted); border-radius: 8px; background: var(--surface-input); color: var(--text-primary); font: inherit; resize: vertical; }
.team-hint { margin: 0; color: var(--text-muted); font-size: 12px; }

.team-style-row { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 8px; }
.team-style-row button { display: flex; flex-direction: column; gap: 4px; padding: 10px 12px; border: 1px solid var(--border-muted); border-radius: 9px; background: var(--surface-raised); color: var(--text-primary); text-align: left; cursor: pointer; transition: border-color 140ms ease, background-color 140ms ease, transform 90ms ease; }
.team-style-row button.active { border-color: var(--accent-primary); background: var(--surface-selected); }
.team-style-row button span { color: var(--text-secondary); font-size: 11px; line-height: 1.4; }

.team-member-list { display: flex; flex-direction: column; gap: 6px; }
.team-member-row { display: flex; align-items: center; gap: 8px; padding: 8px 10px; border: 1px solid var(--border-muted); border-radius: 8px; background: var(--surface-raised); }
.team-member-main { display: flex; flex-direction: column; gap: 2px; min-width: 0; flex: 1; }
.team-member-main strong, .team-member-main span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.team-member-main strong { font-size: 13px; }
.team-member-main span { color: var(--text-secondary); font-size: 11px; }
.team-member-scope { display: flex; gap: 4px; }
.team-member-scope button {
  padding: 2px 7px;
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  background: transparent;
  color: var(--text-muted);
  font-size: 10px;
  cursor: pointer;
  transition: border-color 140ms ease, background-color 140ms ease, transform 90ms ease;
}
.team-member-scope button.active {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
  color: var(--text-primary);
}
.team-member-scope button:disabled { opacity: .5; cursor: default; }
.team-member-actions { display: flex; gap: 2px; }
.team-member-actions button { display: inline-flex; align-items: center; justify-content: center; width: 24px; height: 24px; border: 0; border-radius: 6px; background: transparent; color: var(--text-secondary); cursor: pointer; }
.team-member-actions button:hover:not(:disabled) { background: var(--surface-hover); color: var(--text-primary); }
.team-member-actions button:disabled { opacity: .35; cursor: default; }

.team-member-pool { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
.team-member-pool button { display: inline-flex; align-items: center; gap: 4px; padding: 4px 8px; border: 1px solid var(--border-muted); border-radius: 999px; background: var(--surface-section); color: var(--text-secondary); font-size: 11px; cursor: pointer; }
.team-member-pool button:hover:not(:disabled) { border-color: var(--accent-primary); color: var(--text-primary); }
.team-member-pool button:disabled { opacity: .4; cursor: default; }

@media (max-width: 820px) {
  .team-center { grid-template-columns: 1fr; }
  .team-rail { max-height: 260px; }
}

@media (max-width: 640px) {
  .team-template-grid { grid-template-columns: 1fr; }
}

@media (prefers-reduced-motion: reduce) {
  .team-row,
  .team-template-card,
  .team-style-row button,
  .team-member-scope button {
    transition: none !important;
  }
}
</style>
