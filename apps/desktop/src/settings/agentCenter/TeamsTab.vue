<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowDown, ArrowUp, Info, Plus, RefreshCw, X } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiSelectField } from '@/components/ui'
import { api as baseApi, type AgentModeDto, type AgentModeTopologyDto, type AgentViewDto } from '@/api'
import { scopedApi } from '@/lib/storageScope'
import { useNotifications } from '@/composables/useNotifications'
import {
  buildTeamTopology,
  classifyTeamShape,
  cloneModeTopology,
  defaultMemberScope,
  MAX_TEAM_MEMBERS,
  type MemberScope,
  type TeamSelection,
  type TeamStyle,
} from './teamShape'
import { TEAM_TEMPLATES, pickLeader, pickMembers, type TeamTemplateDefinition } from './teamTemplates'

const props = defineProps<{
  modes: AgentModeDto[]
  agents: AgentViewDto[]
  canAccessBackend: boolean
  loading?: boolean
}>()

const emit = defineEmits<{
  changed: []
  'open-graph': [id: string]
}>()

const api = scopedApi(baseApi, () => 'user')
const { t } = useI18n()
const { notify } = useNotifications()

const selectedId = ref('')
const detail = ref<AgentModeTopologyDto | null>(null)
const detailLoading = ref(false)
const detailFailure = ref<string | null>(null)
const query = ref('')
const editingName = ref('')
const editingDescription = ref('')
const editingInstructions = ref('')
const busy = ref(false)
const defaultModeId = ref<string | null>(null)
const settingDefault = ref(false)
const selection = ref<TeamSelection>({ style: 'team', leaderId: '', memberIds: [] })
const memberScopes = ref<Record<string, MemberScope>>({})
let loadGeneration = 0

const operationAgents = computed(() => props.agents.filter(agent => agent.layer !== 'execution'))
const executionAgents = computed(() => props.agents.filter(agent => agent.layer === 'execution'))
const selectedMode = computed(() => props.modes.find(mode => mode.id === selectedId.value) ?? null)
const readOnly = computed(() => Boolean(
  selectedMode.value?.managed
  || selectedMode.value?.status === 'published'
  || selectedMode.value?.status === 'archived',
))
const shape = computed(() => detail.value ? classifyTeamShape(detail.value, props.agents) : null)
const canEditShape = computed(() => shape.value?.editable ?? false)
const templates = TEAM_TEMPLATES

const filteredModes = computed(() => {
  const q = query.value.trim().toLowerCase()
  const sorted = [...props.modes].sort((a, b) => a.display_name.localeCompare(b.display_name))
  if (!q) return sorted
  return sorted.filter(mode => `${mode.display_name} ${mode.description ?? ''} ${mode.slug ?? ''}`.toLowerCase().includes(q))
})

function agentUsable(agent: AgentViewDto): boolean {
  return agent.enabled !== false && (!agent.status || agent.status === 'published')
}

const leaderOptions = computed(() => operationAgents.value
  .filter(agentUsable)
  .map(agent => ({ value: agent.id, label: agent.name || agent.slug || agent.id })))

/**
 * Team style spawns node-less templates, so any usable agent can be a member.
 * Pipeline style materializes execution nodes, and Core requires the node layer
 * to match the agent layer.
 */
const memberPool = computed(() => {
  const style = selection.value.style
  if (style === 'solo') return []
  const base = props.agents.filter(agent => agent.id !== selection.value.leaderId && agentUsable(agent))
  return style === 'pipeline' ? base.filter(agent => agent.layer === 'execution') : base
})

const memberRows = computed(() => selection.value.memberIds
  .map(id => props.agents.find(agent => agent.id === id))
  .filter((agent): agent is AgentViewDto => Boolean(agent)))

const availableMembers = computed(() => memberPool.value
  .filter(agent => !selection.value.memberIds.includes(agent.id)))

const memberLimitReached = computed(() => selection.value.memberIds.length >= MAX_TEAM_MEMBERS)
const unavailableAgents = computed(() => props.agents.filter(agent =>
  agent.id !== selection.value.leaderId && !agentUsable(agent) && !selection.value.memberIds.includes(agent.id)))
const selectedLeader = computed(() => props.agents.find(agent => agent.id === selection.value.leaderId) ?? null)
const isDefaultMode = computed(() => Boolean(selectedId.value && defaultModeId.value === selectedId.value))
const canSetDefault = computed(() => detail.value?.status === 'published' && !detail.value?.managed)

const teamErrors = computed<string[]>(() => {
  const errors: string[] = []
  const name = editingName.value.trim()
  if (!name) errors.push(t('settings.teamNameRequired'))
  else if (props.modes.some(mode => mode.id !== selectedId.value && mode.display_name.trim().toLowerCase() === name.toLowerCase())) {
    errors.push(t('settings.teamNameTaken'))
  }
  if (!selection.value.leaderId) errors.push(t('settings.teamLeaderRequired'))
  else if (selectedLeader.value && (!operationAgents.value.some(agent => agent.id === selectedLeader.value?.id) || !agentUsable(selectedLeader.value))) {
    errors.push(t('settings.teamLeaderUnavailable'))
  }
  if (selection.value.style !== 'solo' && selection.value.memberIds.length === 0) {
    errors.push(t('settings.teamMembersRequired'))
  }
  if (selection.value.memberIds.length > MAX_TEAM_MEMBERS) {
    errors.push(t('settings.teamMembersLimit', { max: MAX_TEAM_MEMBERS }))
  }
  const unavailable = memberRows.value.filter(agent => !agentUsable(agent) || !memberPool.value.some(candidate => candidate.id === agent.id))
  if (unavailable.length > 0) {
    errors.push(t('settings.teamMemberUnavailable', { names: unavailable.map(agent => agent.name).join(', ') }))
  }
  return errors
})

function agentOf(id: string): AgentViewDto | undefined {
  return props.agents.find(agent => agent.id === id)
}

function agentName(id: string): string {
  return agentOf(id)?.name ?? id
}

function agentDescription(id: string): string {
  return agentOf(id)?.description || ''
}

function modeStatusLabel(mode: AgentModeDto): string {
  if (mode.managed) return t('settings.teamBuiltIn')
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
  memberScopes.value = { ...memberScopes.value, [id]: { ...current, [key]: !current[key] } }
}

function eligibleForStyle(style: TeamStyle, leaderId: string): AgentViewDto[] {
  if (style === 'solo') return []
  const base = props.agents.filter(agent => agent.id !== leaderId && agentUsable(agent))
  return style === 'pipeline' ? base.filter(agent => agent.layer === 'execution') : base
}

function applyShape(next: ReturnType<typeof classifyTeamShape>) {
  const leaderId = next.leaderId || pickLeader(operationAgents.value.filter(agentUsable))
  const allowed = eligibleForStyle(next.style, leaderId)
  const memberIds = next.memberIds
    .filter(id => allowed.some(agent => agent.id === id))
    .slice(0, MAX_TEAM_MEMBERS)
  selection.value = {
    style: next.style,
    leaderId,
    memberIds,
  }
  const scopes: Record<string, MemberScope> = {}
  for (const id of memberIds) {
    const agent = agentOf(id)
    scopes[id] = next.memberScopes?.[id] ?? (agent ? defaultMemberScope(agent) : { read: true, write: false })
  }
  memberScopes.value = scopes
}

async function selectMode(mode: AgentModeDto) {
  selectedId.value = mode.id
  await loadDetail(mode.id)
}

async function loadDetail(id: string) {
  const generation = ++loadGeneration
  detailLoading.value = true
  detailFailure.value = null
  try {
    const loaded = await api.getAgentModeTopology(id)
    if (generation !== loadGeneration) return
    detail.value = loaded
    editingName.value = loaded.display_name
    editingDescription.value = loaded.description ?? ''
    editingInstructions.value = loaded.instructions ?? ''
    applyShape(classifyTeamShape(loaded, props.agents))
  } catch (error) {
    if (generation !== loadGeneration) return
    detail.value = null
    detailFailure.value = error instanceof Error ? error.message : String(error)
  } finally {
    if (generation === loadGeneration) detailLoading.value = false
  }
}

function setStyle(style: TeamStyle) {
  if (readOnly.value) return
  const allowed = eligibleForStyle(style, selection.value.leaderId)
  const kept = selection.value.memberIds.filter(id => allowed.some(agent => agent.id === id))
  let memberIds: string[] = []
  if (style !== 'solo') {
    // Switching shape must not leave a member the new shape cannot represent.
    memberIds = kept.length > 0 ? kept : allowed.slice(0, 2).map(agent => agent.id)
    for (const id of memberIds) {
      const agent = agentOf(id)
      if (agent) memberScopes.value[id] = defaultMemberScope(agent)
    }
  }
  selection.value = { ...selection.value, style, memberIds }
}

function toggleMember(id: string) {
  if (readOnly.value) return
  const members = [...selection.value.memberIds]
  const index = members.indexOf(id)
  if (index >= 0) members.splice(index, 1)
  else {
    if (members.length >= MAX_TEAM_MEMBERS) return
    if (!memberPool.value.some(agent => agent.id === id)) return
    members.push(id)
    const agent = agentOf(id)
    if (agent) memberScopes.value = { ...memberScopes.value, [id]: defaultMemberScope(agent) }
  }
  selection.value = { ...selection.value, memberIds: members }
}

watch(() => selection.value.leaderId, (leaderId) => {
  const allowed = eligibleForStyle(selection.value.style, leaderId)
  const kept = selection.value.memberIds.filter(id => id !== leaderId && allowed.some(agent => agent.id === id))
  if (kept.length !== selection.value.memberIds.length) {
    selection.value = { ...selection.value, memberIds: kept }
  }
})

function moveMember(index: number, delta: number) {
  if (readOnly.value) return
  const members = [...selection.value.memberIds]
  const target = index + delta
  if (target < 0 || target >= members.length) return
  ;[members[index], members[target]] = [members[target], members[index]]
  selection.value = { ...selection.value, memberIds: members }
}

function topologyFromEditor() {
  return buildTeamTopology({
    style: selection.value.style,
    leaderId: selection.value.leaderId,
    memberIds: selection.value.memberIds,
    memberScopes: memberScopes.value,
    instructions: editingInstructions.value,
    displayName: editingName.value.trim() || detail.value?.display_name || 'Team',
    description: editingDescription.value.trim(),
  }, props.agents)
}

async function saveDraft(): Promise<string | null> {
  if (!props.canAccessBackend) return null
  const name = editingName.value.trim()
  if (!name) {
    notify.error(new Error(t('settings.teamNameRequired')))
    return null
  }
  if (props.modes.some(mode => mode.id !== selectedId.value && mode.display_name.trim().toLowerCase() === name.toLowerCase())) {
    notify.error(new Error(t('settings.teamNameTaken')))
    return null
  }
  if (!selection.value.leaderId) {
    notify.error(new Error(t('settings.teamLeaderRequired')))
    return null
  }
  busy.value = true
  try {
    const topology = topologyFromEditor()
    const displayName = name || detail.value?.display_name || 'Team'
    if (!selectedId.value || readOnly.value) {
      const created = await api.createAgentModeDraft({
        slug: `team-${Date.now().toString(36)}`,
        display_name: displayName,
        description: editingDescription.value.trim() || null,
        ...topology,
      })
      selectedId.value = created.id
      editingName.value = created.display_name
      emit('changed')
      await loadDetail(created.id)
      notify.success(t('settings.teamCreated', { name: created.display_name }))
      return created.id
    }
    const expected = detail.value?.etag ?? (detail.value?.revision != null ? String(detail.value.revision) : null)
    await api.updateAgentModeDraft(selectedId.value, topology, expected)
    await loadDetail(selectedId.value)
    emit('changed')
    notify.success(t('settings.teamDraftSaved'))
    return selectedId.value
  } catch (error) {
    notify.error(error)
    return null
  } finally {
    busy.value = false
  }
}

async function publishTeam() {
  if (teamErrors.value.length > 0) {
    notify.error(new Error(teamErrors.value[0]))
    return
  }
  const id = await saveDraft()
  if (!id) return
  busy.value = true
  try {
    await api.publishAgentMode(id)
    emit('changed')
    await loadDetail(id)
    notify.success(t('settings.teamPublished'))
  } catch (error) {
    notify.error(error)
  } finally {
    busy.value = false
  }
}

async function createFromTemplate(template: TeamTemplateDefinition) {
  if (!props.canAccessBackend) return
  const leaderId = pickLeader(operationAgents.value.filter(agentUsable))
  if (!leaderId) {
    notify.error(new Error(t('settings.teamNoLeader')))
    return
  }
  const pool = template.key === 'pipeline'
    ? executionAgents.value.filter(agentUsable)
    : props.agents.filter(agent => agentUsable(agent) && agent.id !== leaderId)
  const memberIds = template.key === 'solo' ? [] : pickMembers(template, pool).slice(0, MAX_TEAM_MEMBERS)
  if (template.key !== 'solo' && memberIds.length === 0) {
    // Qoder's templates also create their members; Tinadec reuses existing agents
    // and refuses to silently save a team that can never dispatch.
    notify.error(new Error(t('settings.teamTemplateNoMembers')))
    return
  }
  busy.value = true
  try {
    const name = t(template.labelKey)
    const topology = buildTeamTopology({
      style: template.key,
      leaderId,
      memberIds,
      instructions: template.instructionsKey ? t(template.instructionsKey) : '',
      displayName: name,
      description: t(template.hintKey),
    }, props.agents)
    const created = await api.createAgentModeDraft({
      slug: `team-${Date.now().toString(36)}`,
      display_name: name,
      description: t(template.hintKey),
      ...topology,
    })
    emit('changed')
    selectedId.value = created.id
    await loadDetail(created.id)
    notify.success(t('settings.teamCreated', { name: created.display_name }))
  } catch (error) {
    notify.error(error)
  } finally {
    busy.value = false
  }
}

async function cloneToMine() {
  if (!detail.value) return
  busy.value = true
  try {
    const topology = cloneModeTopology(detail.value)
    const name = `${editingName.value || detail.value.display_name} ${t('settings.teamCopySuffix')}`
    const created = await api.createAgentModeDraft({
      slug: `team-${Date.now().toString(36)}`,
      display_name: name,
      description: editingDescription.value || detail.value.description || null,
      ...topology,
    })
    emit('changed')
    selectedId.value = created.id
    await loadDetail(created.id)
    notify.success(t('settings.teamCreated', { name: created.display_name }))
  } catch (error) {
    notify.error(error)
  } finally {
    busy.value = false
  }
}

async function loadDefaults() {
  try {
    const defaults = await api.getWorkspaceDefaults()
    defaultModeId.value = defaults.default_agent_mode_id ?? null
  } catch {
    defaultModeId.value = null
  }
}

/** The Qoder "invocation policy" analog: new conversations fall back to this team. */
async function setAsDefault() {
  if (!selectedId.value || !canSetDefault.value || isDefaultMode.value) return
  settingDefault.value = true
  try {
    const current = await api.getWorkspaceDefaults()
    const draft = await api.updateWorkspaceDefaultsDraft({
      default_agent_mode_id: selectedId.value,
      default_mode_version_id: detail.value?.latest_published_mode_version_id ?? null,
      default_prompt_pipeline_id: current.default_prompt_pipeline_id ?? null,
      default_prompt_version_id: current.default_prompt_version_id ?? null,
    }, current.revision ?? null)
    await api.publishWorkspaceDefaults(draft.revision ?? null)
    defaultModeId.value = selectedId.value
    notify.success(t('settings.teamDefaultSet'))
  } catch (error) {
    notify.error(error)
  } finally {
    settingDefault.value = false
  }
}

function openGraph() {
  if (selectedId.value) emit('open-graph', selectedId.value)
}

function reload() {
  if (selectedId.value) void loadDetail(selectedId.value)
}

defineExpose({ reload })

watch(() => props.modes, modes => {
  if (!modes.length) {
    selectedId.value = ''
    detail.value = null
    return
  }
  // Do not steal the selection when the refreshed list lags behind a just-created
  // team; the detail pane already holds the authoritative row.
  if (!selectedId.value) void selectMode(modes[0])
}, { immediate: true })

onMounted(() => { void loadDefaults() })
</script>

<template>
  <div class="team-center" data-testid="agent-teams">
    <aside class="team-rail">
      <div class="team-rail-head">
        <UiInput v-model="query" :placeholder="t('settings.teamSearch')" />
      </div>
      <button
        v-for="mode in filteredModes"
        :key="mode.id"
        type="button"
        class="team-row"
        :class="{ active: selectedId === mode.id }"
        @click="selectMode(mode)"
      >
        <div class="team-row-main">
          <strong>{{ mode.display_name }}</strong>
          <span>{{ mode.description || mode.slug || '—' }}</span>
        </div>
        <UiBadge :variant="mode.managed || mode.status === 'published' ? 'secondary' : 'outline'">
          {{ modeStatusLabel(mode) }}
        </UiBadge>
      </button>
      <div v-if="loading && !filteredModes.length" class="team-rail-loading" aria-live="polite">
        <RefreshCw :size="14" class="center-stale-spinner" aria-hidden="true" />
        <span>{{ t('settings.refreshing') }}</span>
      </div>
      <p v-else-if="!filteredModes.length" class="team-rail-empty">{{ t('settings.teamEmptyList') }}</p>
    </aside>

    <main class="team-stage">
      <div v-if="detailLoading" class="team-stage-placeholder" aria-live="polite">
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

      <div v-else-if="!detail" class="team-welcome">
        <div>
          <h3>{{ t('settings.teamWelcomeTitle') }}</h3>
          <p>{{ t('settings.teamWelcomeHint') }}</p>
        </div>
        <div class="team-template-grid">
          <button
            v-for="template in templates"
            :key="template.key"
            type="button"
            class="team-template-card"
            :disabled="!canAccessBackend || busy"
            @click="createFromTemplate(template)"
          >
            <strong>{{ t(template.labelKey) }}</strong>
            <span>{{ t(template.hintKey) }}</span>
          </button>
        </div>
      </div>

      <div v-else class="team-editor">
        <header class="team-editor-head">
          <div class="team-editor-title">
            <h3>{{ editingName || detail.display_name }}</h3>
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
            <UiButton v-if="readOnly" :disabled="busy || !canAccessBackend" @click="cloneToMine">
              <Plus :size="14" />
              <span>{{ t('settings.teamClone') }}</span>
            </UiButton>
            <template v-if="!readOnly">
              <UiButton variant="outline" :disabled="busy || !canAccessBackend" @click="saveDraft">
                {{ t('settings.teamSaveDraft') }}
              </UiButton>
              <UiButton :disabled="busy || !canAccessBackend || !canEditShape || teamErrors.length > 0" @click="publishTeam">
                {{ t('settings.teamPublish') }}
              </UiButton>
            </template>
            <UiButton variant="ghost" @click="openGraph">{{ t('settings.teamOpenGraph') }}</UiButton>
          </div>
        </header>

        <div class="team-summary">
          <UiBadge variant="outline">{{ selection.style }}</UiBadge>
          <span v-if="selectedLeader">{{ t('settings.teamLeader') }} · {{ selectedLeader.name }}</span>
          <span>{{ t('settings.teamMemberCount', { count: memberRows.length, max: MAX_TEAM_MEMBERS }) }}</span>
          <UiBadge v-if="isDefaultMode" variant="secondary">{{ t('settings.teamDefaultCurrent') }}</UiBadge>
        </div>

        <div v-if="teamErrors.length > 0 && !readOnly" class="team-errors" role="alert">
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
              v-model="selection.leaderId"
              :aria-label="t('settings.teamLeader')"
              :options="leaderOptions"
              :disabled="readOnly"
            />
          </label>

          <div v-if="selection.style !== 'solo'" class="team-field">
            <span>{{ t('settings.teamMembers') }} · {{ memberRows.length }} / {{ MAX_TEAM_MEMBERS }}</span>
            <div v-if="memberRows.length" class="team-member-list" role="list">
              <div v-for="(agent, index) in memberRows" :key="agent.id" class="team-member-row" role="listitem">
                <div class="team-member-main">
                  <strong>{{ agent.name }}</strong>
                  <span>{{ agent.description || '—' }}</span>
                </div>
                <em class="team-member-layer">{{ agent.layer }}</em>
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
                <span>{{ agent.name }}</span>
                <em>{{ agent.layer }}</em>
              </button>
            </div>
            <p v-if="memberLimitReached" class="team-hint">{{ t('settings.teamMembersLimit', { max: MAX_TEAM_MEMBERS }) }}</p>
            <p v-else-if="availableMembers.length === 0 && selection.style === 'pipeline'" class="team-hint">{{ t('settings.teamNoExecutionAgents') }}</p>
            <p v-if="unavailableAgents.length > 0" class="team-hint">
              {{ t('settings.teamUnavailableAgents', { names: unavailableAgents.map(agent => agent.name).join(', ') }) }}
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

.team-rail-head { position: sticky; top: 0; padding-bottom: 6px; background: var(--surface-section); z-index: 1; }
.team-rail-loading, .team-rail-empty { display: flex; align-items: center; gap: 6px; padding: 10px 4px; color: var(--text-muted); font-size: 12px; }

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
.team-welcome h3 { margin: 0 0 6px; }
.team-welcome p { margin: 0; color: var(--text-secondary); font-size: 13px; }
.team-template-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 10px; }
.team-template-card { display: flex; flex-direction: column; gap: 6px; padding: 14px; border: 1px solid var(--border-muted); border-radius: 10px; background: var(--surface-raised); color: var(--text-primary); text-align: left; cursor: pointer; }
.team-template-card:hover { border-color: var(--accent-primary); }
.team-template-card span { color: var(--text-secondary); font-size: 12px; line-height: 1.45; }

.team-editor { display: flex; flex-direction: column; gap: 12px; }
.team-editor-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
.team-editor-title h3 { margin: 0 0 4px; }
.team-editor-title p { margin: 0; color: var(--text-secondary); font-size: 12px; }
.team-editor-actions { display: flex; gap: 6px; flex-wrap: wrap; }

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
.team-style-row button { display: flex; flex-direction: column; gap: 4px; padding: 10px 12px; border: 1px solid var(--border-muted); border-radius: 9px; background: var(--surface-raised); color: var(--text-primary); text-align: left; cursor: pointer; }
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
</style>
