<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Activity, CalendarClock, Globe, Hand, PlugZap, Plus, RefreshCw, Sparkles, Zap } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import CompanionStage from '@/components/pets/CompanionStage.vue'
import PreviewBrowserPanel from '@/components/PreviewBrowserPanel.vue'
import CompanionSchedulePlanTodoPanel from './CompanionSchedulePlanTodoPanel.vue'
import CompanionWorkSessionCard from './CompanionWorkSessionCard.vue'
import { useNotifications } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'
import { feedRows, relativeTime, type CompanionFeedFilter } from '@/lib/companionView'
import {
  companionApi,
  type CompanionBrowserDriver,
  type CompanionBrowserProfile,
  type CompanionConnectedWorkSession,
  type CompanionConnection,
  type CompanionProjectSummary,
  type CompanionSummary,
} from '@/lib/companionApi'
import {
  encodeBrowserConnectionEndpoint,
  parseBrowserConnectionEndpoint,
} from '@/lib/companionWorkSession'
import {
  activity,
  connections,
  liveWorkSessions,
  maxBotHopDepth,
  refreshCompanionCenter,
  refreshWorkSessions,
  settings,
  stageCues,
  streamingTurns,
  workSessionsByCompanion,
  workSessionsLoading,
} from '@/lib/companionStore'
import { avatarKindOf, type AvatarKind } from '@/pets/avatarKind'
import type { PetState } from '@/pets/petRuntime'

type DockTab = 'planning' | 'stage' | 'browser' | 'devices' | 'supervision'

const props = defineProps<{
  companion: CompanionSummary | null
  conversationId: string | null
  live2dSlugs?: readonly string[]
}>()

const emit = defineEmits<{
  'edit-companion': [companionId: string, initialTab?: 'identity' | 'memory' | 'skills' | 'connections' | 'autonomy']
}>()

const { t, locale } = useI18n()
const { notify } = useNotifications()
const { canAccessBackend } = useHostAccess()

const activeTab = ref<DockTab>('planning')
const botConnections = ref<CompanionConnection[]>([])
const connectionsLoading = ref(false)
const projects = ref<CompanionProjectSummary[]>([])
const browserProfiles = ref<CompanionBrowserProfile[]>([])
const quickWorkSessionScope = ref('*')
const quickBrowserDriver = ref<CompanionBrowserDriver>('builtin_electron')
const quickBrowserProfileId = ref('')
const quickGrantBusy = ref(false)
const tickBusy = ref(false)
const activityFilter = ref<CompanionFeedFilter>('all')

const browserDrivers: CompanionBrowserDriver[] = ['builtin_electron', 'ego_lite']

const live2dSlugsList = computed(() => [...(props.live2dSlugs ?? [])])

function kindOf(slug: string | null | undefined): AvatarKind {
  return avatarKindOf(slug, live2dSlugsList.value) ?? 'sprite'
}

const currentCue = computed(() => (props.companion ? stageCues.value[props.companion.id] ?? null : null))
const isSpeaking = computed(() => {
  if (!props.companion) return false
  if (currentCue.value?.speaking) return true
  return Object.values(streamingTurns.value).some((turn) => turn.companionId === props.companion!.id)
})

const stageState = computed<PetState>(() => {
  if (isSpeaking.value) return 'running'
  if (props.companion?.current_activity === 'thinking') return 'waiting'
  return 'idle'
})

const projectNameById = computed(() => {
  const map = new Map<string, string>()
  for (const project of projects.value) {
    map.set(project.id, project.name)
  }
  return map
})

const filteredBrowserProfiles = computed(() =>
  browserProfiles.value.filter((profile) => profile.driver === quickBrowserDriver.value))

watch(quickBrowserDriver, () => {
  if (quickBrowserProfileId.value && !filteredBrowserProfiles.value.some((p) => p.id === quickBrowserProfileId.value)) {
    quickBrowserProfileId.value = ''
  }
})

function workSessionPriority(status: string | null | undefined): number {
  const normalized = (status ?? '').toLowerCase()
  if (normalized === 'awaiting_approval' || normalized === 'browser_handoff') return 0
  if (normalized === 'running' || normalized === 'in_progress' || normalized === 'queued' || normalized === 'pending' || normalized === 'starting' || normalized === 'thinking' || normalized === 'planning' || normalized === 'executing') return 1
  if (normalized === 'failed') return 2
  if (normalized === 'completed') return 3
  return 4
}

const connectedWorkSessions = computed<CompanionConnectedWorkSession[]>(() => {
  const id = props.companion?.id
  if (!id) return []
  const rows = workSessionsByCompanion.value[id] ?? []
  return rows
    .map((row) => liveWorkSessions.value[row.session_id] ?? row)
    .sort((left, right) => {
      const pDiff = workSessionPriority(left.latest_run_status) - workSessionPriority(right.latest_run_status)
      if (pDiff !== 0) return pDiff
      const a = left.updated_at ? Date.parse(left.updated_at) : 0
      const b = right.updated_at ? Date.parse(right.updated_at) : 0
      return (Number.isFinite(b) ? b : 0) - (Number.isFinite(a) ? a : 0)
    })
})

const loadingWorkSessions = computed(() =>
  Boolean(props.companion?.id && workSessionsLoading.value[props.companion.id]))

function connectionKindLabel(kind: string): string {
  switch (kind) {
    case 'work_session': return t('settings.companions.connection.kindWorkSession')
    case 'browser': return t('settings.companions.connection.kindBrowser')
    case 'server': return t('settings.companions.connection.kindServer')
    case 'phone': return t('settings.companions.connection.kindPhone')
    case 'external_agent': return t('settings.companions.connection.kindExternalAgent')
    default: return t('settings.companions.connection.kindMcp')
  }
}

function browserDriverLabel(driver: string): string {
  if (driver === 'ego_lite') return t('settings.companions.connection.driverEgoLite')
  if (driver === 'builtin_electron') return t('settings.companions.connection.driverBuiltinElectron')
  return driver
}

function connectionRuntimeLabel(status: string): string {
  if (status === 'connected') return t('settings.companions.connection.runtimeConnected')
  if (status === 'active') return t('settings.companions.connection.runtimeActive')
  if (status === 'disconnected') return t('settings.companions.connection.runtimeDisconnected')
  if (status === 'configured') return t('settings.companions.connection.runtimeConfigured')
  if (status === 'unavailable') return t('settings.companions.connection.runtimeUnavailable')
  return status === 'unsupported'
    ? t('settings.companions.connection.runtimeUnsupported')
    : t('settings.companions.connection.runtimeUnknown')
}

function connectionMetaLine(conn: CompanionConnection): string {
  const parts: string[] = [connectionKindLabel(conn.kind)]
  if (conn.kind === 'work_session') {
    const scope = conn.endpoint?.trim()
    parts.push(!scope || scope === '*' ? t('settings.companions.connection.scopeAllWorkspaces') : (projectNameById.value.get(scope) ?? scope))
  } else if (conn.kind === 'browser') {
    const extra = conn as unknown as { driver?: unknown; profile_id?: unknown }
    const parsed = parseBrowserConnectionEndpoint(conn.endpoint, extra)
    const driverText = parsed.driver === 'ego_lite' ? 'Ego Lite' : t('settings.companions.connection.driverBuiltinShort')
    parts.push(parsed.profileId ? `${driverText} · ${parsed.profileId}` : driverText)
  }
  parts.push(connectionRuntimeLabel(conn.runtime_status))
  return parts.join(' · ')
}

async function loadDeviceAuxiliaries() {
  if (!canAccessBackend.value) return
  const tasks: Array<Promise<void>> = []
  if (typeof companionApi.listProjects === 'function') {
    tasks.push(
      companionApi.listProjects()
        .then((rows) => { projects.value = Array.isArray(rows) ? rows : [] })
        .catch(() => { projects.value = [] }),
    )
  }
  if (typeof companionApi.listBrowserProfiles === 'function') {
    tasks.push(
      companionApi.listBrowserProfiles()
        .then((rows) => { browserProfiles.value = Array.isArray(rows) ? rows : [] })
        .catch(() => { browserProfiles.value = [] }),
    )
  }
  await Promise.all(tasks)
}

async function loadBotConnections() {
  const id = props.companion?.id
  if (!id || !canAccessBackend.value) {
    botConnections.value = []
    return
  }
  connectionsLoading.value = true
  try {
    botConnections.value = await companionApi.listConnections(id)
  } catch {
    botConnections.value = []
  } finally {
    connectionsLoading.value = false
  }
}

async function reloadWorkSessions() {
  const id = props.companion?.id
  if (!id || !canAccessBackend.value) return
  await refreshWorkSessions(id)
}

watch(() => [props.companion?.id, canAccessBackend.value] as const, () => {
  void loadBotConnections()
  if (activeTab.value === 'devices') {
    void reloadWorkSessions()
  }
}, { immediate: true })

watch(activeTab, (tab) => {
  if (tab === 'devices') {
    void loadDeviceAuxiliaries()
    void reloadWorkSessions()
  }
})

async function quickGrantWorkSession() {
  if (!props.companion || quickGrantBusy.value) return
  quickGrantBusy.value = true
  try {
    const scope = quickWorkSessionScope.value || '*'
    const scopeLabel = scope === '*'
      ? t('settings.companions.connection.scopeAllWorkspaces')
      : (projectNameById.value.get(scope) ?? scope)
    const name = `${t('settings.companions.connection.kindWorkSession')} · ${scopeLabel}`
    await companionApi.addConnection(props.companion.id, {
      kind: 'work_session',
      name,
      endpoint: scope,
      authorization: 'authorized',
    })
    await Promise.all([loadBotConnections(), reloadWorkSessions()])
  } catch (error) {
    notify.error(error)
  } finally {
    quickGrantBusy.value = false
  }
}

async function quickGrantBrowser() {
  if (!props.companion || quickGrantBusy.value) return
  quickGrantBusy.value = true
  try {
    const driver = quickBrowserDriver.value
    const profileId = quickBrowserProfileId.value.trim() || null
    const driverLabel = driver === 'ego_lite' ? 'Ego Lite' : t('settings.companions.connection.driverBuiltinShort')
    const profileName = profileId
      ? (browserProfiles.value.find((item) => item.id === profileId)?.name ?? profileId)
      : null
    const name = profileName ? `${driverLabel} · ${profileName}` : driverLabel
    await companionApi.addConnection(props.companion.id, {
      kind: 'browser',
      name,
      endpoint: encodeBrowserConnectionEndpoint(driver, profileId),
      driver,
      profile_id: profileId,
      authorization: 'authorized',
    })
    quickBrowserProfileId.value = ''
    await loadBotConnections()
  } catch (error) {
    notify.error(error)
  } finally {
    quickGrantBusy.value = false
  }
}

async function toggleConnectionAuth(conn: CompanionConnection) {
  if (!props.companion || quickGrantBusy.value) return
  quickGrantBusy.value = true
  try {
    const nextAuth = conn.authorization === 'authorized' ? 'revoked' : 'authorized'
    const updated = await companionApi.patchConnection(props.companion.id, conn.id, conn.revision, {
      authorization: nextAuth,
    })
    botConnections.value = botConnections.value.map((row) => (row.id === updated.id ? updated : row))
    if (conn.kind === 'work_session') await reloadWorkSessions()
  } catch (error) {
    notify.error(error)
  } finally {
    quickGrantBusy.value = false
  }
}

async function toggleConnection(conn: CompanionConnection) {
  if (!props.companion) return
  try {
    const updated = await companionApi.patchConnection(props.companion.id, conn.id, conn.revision, {
      enabled: !conn.enabled,
    })
    botConnections.value = botConnections.value.map((row) => (row.id === updated.id ? updated : row))
    if (conn.kind === 'work_session') await reloadWorkSessions()
  } catch (error) {
    notify.error(error)
  }
}

async function runAutonomyTick() {
  if (!props.companion || tickBusy.value) return
  tickBusy.value = true
  try {
    await companionApi.tickAutonomy(props.companion.id)
    await refreshCompanionCenter()
  } catch (error) {
    notify.error(error)
  } finally {
    tickBusy.value = false
  }
}

async function triggerEmote(emote: 'pat' | 'wave') {
  if (!props.companion) return
  try {
    await companionApi.recordEmote(props.companion.id, emote)
  } catch (error) {
    notify.error(error)
  }
}

const timeline = computed(() =>
  feedRows(activity.value, {
    companionId: props.companion?.id ?? null,
    filter: activityFilter.value,
  }).slice(0, 30),
)
</script>

<template>
  <aside class="companion-dock" data-testid="companion-right-dock">
    <nav class="dock-tabs" role="tablist">
      <button
        type="button"
        role="tab"
        :aria-selected="activeTab === 'planning'"
        class="dock-tab"
        :class="{ active: activeTab === 'planning' }"
        :title="t('companionCenter.dock.tabPlanning')"
        data-testid="dock-tab-planning"
        @click="activeTab = 'planning'"
      >
        <CalendarClock :size="14" aria-hidden="true" />
        <span>{{ t('companionCenter.dock.tabPlanning') }}</span>
      </button>
      <button
        type="button"
        role="tab"
        :aria-selected="activeTab === 'stage'"
        class="dock-tab"
        :class="{ active: activeTab === 'stage' }"
        :title="t('companionCenter.dock.tabStage')"
        data-testid="dock-tab-stage"
        @click="activeTab = 'stage'"
      >
        <Sparkles :size="14" aria-hidden="true" />
        <span>{{ t('companionCenter.dock.tabStage') }}</span>
      </button>
      <button
        type="button"
        role="tab"
        :aria-selected="activeTab === 'browser'"
        class="dock-tab"
        :class="{ active: activeTab === 'browser' }"
        :title="t('companionCenter.dock.tabBrowser')"
        data-testid="dock-tab-browser"
        @click="activeTab = 'browser'"
      >
        <Globe :size="14" aria-hidden="true" />
        <span>{{ t('companionCenter.dock.tabBrowser') }}</span>
      </button>
      <button
        type="button"
        role="tab"
        :aria-selected="activeTab === 'devices'"
        class="dock-tab"
        :class="{ active: activeTab === 'devices' }"
        :title="t('companionCenter.dock.tabDevices')"
        data-testid="dock-tab-devices"
        @click="activeTab = 'devices'"
      >
        <PlugZap :size="14" aria-hidden="true" />
        <span>{{ t('companionCenter.dock.tabDevices') }}</span>
      </button>
      <button
        type="button"
        role="tab"
        :aria-selected="activeTab === 'supervision'"
        class="dock-tab"
        :class="{ active: activeTab === 'supervision' }"
        :title="t('companionCenter.dock.tabSupervision')"
        data-testid="dock-tab-supervision"
        @click="activeTab = 'supervision'"
      >
        <Activity :size="14" aria-hidden="true" />
        <span>{{ t('companionCenter.dock.tabSupervision') }}</span>
      </button>
    </nav>

    <div class="dock-body">
      <CompanionSchedulePlanTodoPanel
        v-if="activeTab === 'planning'"
        :companion="companion"
        :active-conversation-id="conversationId"
      />

      <div v-else-if="activeTab === 'stage'" class="dock-stage-pane" data-testid="dock-stage-pane">
        <template v-if="companion">
          <div class="stage-card">
            <CompanionStage
              :pet-slug="companion.avatar ?? ''"
              :avatar-kind="kindOf(companion.avatar)"
              :state="stageState"
              :speaking="isSpeaking"
              :mouth-open="currentCue?.intensity ?? undefined"
              :emotion="currentCue?.emotion"
              :motion-group="currentCue?.gesture"
              :height="220"
              :alt="companion.name"
            />
          </div>
          <div class="stage-actions">
            <UiButton size="sm" variant="outline" @click="triggerEmote('wave')">
              <Sparkles :size="13" aria-hidden="true" />
              <span>{{ t('companion.greet') }}</span>
            </UiButton>
            <UiButton size="sm" variant="outline" @click="triggerEmote('pat')">
              <Hand :size="13" aria-hidden="true" />
              <span>{{ t('companion.pat') }}</span>
            </UiButton>
          </div>
        </template>
        <p v-else class="dock-empty">{{ t('companionCenter.noSelection') }}</p>
      </div>

      <div v-else-if="activeTab === 'browser'" class="dock-browser-pane" data-testid="dock-browser-pane">
        <PreviewBrowserPanel />
      </div>

      <div v-else-if="activeTab === 'devices'" class="dock-section" data-testid="dock-devices-pane">
        <div class="dock-section-head">
          <strong>{{ t('companionCenter.dock.tabDevices') }}</strong>
          <UiButton
            v-if="companion"
            size="sm"
            variant="outline"
            @click="emit('edit-companion', companion.id, 'connections')"
          >
            {{ t('companionCenter.editBot') }}
          </UiButton>
        </div>

        <div v-if="companion" class="dock-quick-grant" data-testid="dock-quick-grant-card">
          <div class="dock-quick-head">
            <span>{{ t('companionCenter.dock.quickGrantTitle') }}</span>
          </div>
          <div class="dock-quick-row">
            <select
              v-model="quickWorkSessionScope"
              class="dock-select"
              :aria-label="t('settings.companions.connection.workspaceScope')"
              data-testid="dock-work-session-scope"
            >
              <option value="*">{{ t('settings.companions.connection.scopeAllWorkspaces') }}</option>
              <option v-for="project in projects" :key="project.id" :value="project.id">{{ project.name }}</option>
            </select>
            <UiButton
              size="sm"
              variant="outline"
              class="dock-inline-btn"
              :disabled="quickGrantBusy"
              data-testid="dock-grant-work-session-btn"
              @click="quickGrantWorkSession"
            >
              <Plus :size="12" aria-hidden="true" />
              <span>{{ t('companionCenter.dock.grantWorkSession') }}</span>
            </UiButton>
          </div>
          <div class="dock-quick-row">
            <select
              v-model="quickBrowserDriver"
              class="dock-select"
              :aria-label="t('settings.companions.connection.browserDriver')"
              data-testid="dock-browser-driver"
            >
              <option v-for="driver in browserDrivers" :key="driver" :value="driver">{{ browserDriverLabel(driver) }}</option>
            </select>
            <select
              v-if="filteredBrowserProfiles.length > 0"
              v-model="quickBrowserProfileId"
              class="dock-select dock-select-compact"
              :aria-label="t('settings.companions.connection.browserProfile')"
              data-testid="dock-browser-profile"
            >
              <option value="">{{ t('settings.companions.connection.browserProfileDefault') }}</option>
              <option v-for="profile in filteredBrowserProfiles" :key="profile.id" :value="profile.id">{{ profile.name }}</option>
            </select>
            <input
              v-else
              v-model="quickBrowserProfileId"
              type="text"
              class="dock-select dock-select-compact"
              :placeholder="t('settings.companions.connection.browserProfileDefault')"
              :aria-label="t('settings.companions.connection.browserProfile')"
              data-testid="dock-browser-profile"
            />
            <UiButton
              size="sm"
              variant="outline"
              class="dock-inline-btn"
              :disabled="quickGrantBusy"
              data-testid="dock-grant-browser-btn"
              @click="quickGrantBrowser"
            >
              <Globe :size="12" aria-hidden="true" />
              <span>{{ t('companionCenter.dock.grantBrowser') }}</span>
            </UiButton>
          </div>
        </div>

        <p v-if="connectionsLoading" class="dock-empty">{{ t('companion.center.loading') }}</p>
        <ul v-else-if="botConnections.length" class="dock-list">
          <li v-for="conn in botConnections" :key="conn.id" class="dock-row" :data-testid="`dock-connection-row-${conn.id}`">
            <div class="dock-row-copy">
              <strong>{{ conn.name }}</strong>
              <span>{{ connectionMetaLine(conn) }}</span>
            </div>
            <div class="dock-row-actions">
              <button
                type="button"
                class="dock-mini-btn"
                :class="{ 'is-danger': conn.authorization === 'authorized' }"
                :disabled="quickGrantBusy"
                :data-testid="`dock-conn-auth-btn-${conn.id}`"
                @click="toggleConnectionAuth(conn)"
              >
                {{ conn.authorization === 'authorized'
                  ? t('companionCenter.dock.revokeAccess')
                  : t('companionCenter.dock.reauthorizeAccess') }}
              </button>
              <input type="checkbox" :checked="conn.enabled" :aria-label="conn.name" @change="toggleConnection(conn)" />
            </div>
          </li>
        </ul>
        <p v-else class="dock-empty">{{ t('companion.center.connectionCount', { count: connections.length }) }}</p>

        <div v-if="companion" class="dock-work-sessions" data-testid="dock-work-sessions-section">
          <div class="dock-section-head">
            <div class="dock-head-title-group">
              <strong>{{ t('companionCenter.dock.workSessionsTitle') }}</strong>
              <span class="dock-count-pill">{{ connectedWorkSessions.length }}</span>
            </div>
            <button
              type="button"
              class="dock-mini-btn"
              :disabled="loadingWorkSessions"
              data-testid="dock-work-sessions-refresh"
              @click="reloadWorkSessions"
            >
              <RefreshCw :size="11" :class="{ 'dock-spin': loadingWorkSessions }" aria-hidden="true" />
              <span>{{ t('companionCenter.dock.workSessionsRefresh') }}</span>
            </button>
          </div>

          <div v-if="connectedWorkSessions.length" class="dock-ws-list">
            <CompanionWorkSessionCard
              v-for="ws in connectedWorkSessions"
              :key="ws.session_id"
              :session="ws"
              compact
              @approval-decided="reloadWorkSessions"
              @run-cancelled="reloadWorkSessions"
              @browser-takeover="reloadWorkSessions"
            />
          </div>
          <p v-else class="dock-empty" data-testid="dock-work-sessions-empty">
            {{ t('companionCenter.dock.workSessionsEmpty') }}
          </p>
        </div>
      </div>

      <div v-else class="dock-section" data-testid="dock-supervision-pane">
        <div class="dock-section-head">
          <strong>{{ t('companionCenter.dock.liveActivities') }}</strong>
          <UiButton
            v-if="companion"
            size="sm"
            variant="outline"
            :disabled="tickBusy"
            data-testid="autonomy-tick-btn"
            @click="runAutonomyTick"
          >
            <Zap :size="13" aria-hidden="true" />
            <span>{{ t('companionCenter.dock.autonomyTickNow') }}</span>
          </UiButton>
        </div>
        <div v-if="settings?.autonomy_paused" class="dock-banner">
          {{ t('companionCenter.dock.autonomyPausedGlobal') }}
        </div>
        <div class="dock-meta-strip">
          <span>{{ t('companionCenter.hopLimitNote', { limit: maxBotHopDepth }) }}</span>
        </div>
        <ul v-if="timeline.length" class="dock-list">
          <li v-for="row in timeline" :key="row.id" class="dock-row">
            <div class="dock-row-copy">
              <strong>{{ row.summary || row.kind }}</strong>
              <span>{{ row.companion_name }} · {{ relativeTime(row.created_at, locale) }}</span>
            </div>
          </li>
        </ul>
        <p v-else class="dock-empty">{{ t('companion.center.neverActivity') }}</p>
      </div>
    </div>
  </aside>
</template>

<style scoped>
.companion-dock {
  display: flex; flex-direction: column; min-height: 0; min-width: 0;
  border-radius: var(--radius-island, 12px); border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-section); backdrop-filter: var(--material-filter-raised, none);
  box-shadow: var(--shadow-card-subtle, var(--shadow-panel)); overflow: hidden;
}
.dock-tabs {
  display: flex; flex-wrap: wrap; gap: 4px; padding: 10px 10px 8px;
  border-bottom: 1px solid var(--border-muted);
}
.dock-tab {
  display: inline-flex; align-items: center; justify-content: center; gap: 5px;
  height: 28px; padding: 0 8px; flex-shrink: 0; white-space: nowrap;
  border: 1px solid transparent; border-radius: var(--radius-control, 6px); background: transparent;
  color: var(--text-muted); font-size: 12px; cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease, border-color 120ms ease;
}
.dock-tab:hover { background: var(--surface-hover, var(--surface-raised)); color: var(--text-primary); }
.dock-tab:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  color: var(--text-primary);
}
.dock-tab.active {
  background: var(--surface-raised); border-color: var(--border-card); color: var(--text-primary); font-weight: 600;
}
.dock-body { flex: 1; min-height: 0; overflow-y: auto; padding: 12px; }
.dock-stage-pane { display: flex; flex-direction: column; align-items: center; gap: 12px; }
.stage-card {
  width: 100%; display: flex; align-items: center; justify-content: center;
  padding: 16px; border-radius: var(--radius-card, 10px); border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-raised); box-sizing: border-box;
}
.stage-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.stage-actions :deep(button),
.dock-section-head :deep(button) {
  display: inline-flex; align-items: center; justify-content: center; gap: 6px;
  height: 28px; padding: 0 10px; border-radius: var(--radius-control, 6px);
  font-size: 12px; white-space: nowrap; flex-shrink: 0;
}
.dock-browser-pane { height: 100%; min-height: 360px; display: flex; flex-direction: column; }
.dock-section { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
.dock-section-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; font-size: 12px; min-width: 0; }
.dock-section-head strong { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.dock-banner {
  padding: 6px 10px; border-radius: var(--radius-control, 8px); font-size: 11px;
  border: 1px solid color-mix(in srgb, var(--accent-warning, #eab308) 28%, transparent);
  background: color-mix(in srgb, var(--accent-warning, #eab308) 12%, transparent);
  color: var(--accent-warning, #eab308);
}
.dock-meta-strip { font-size: 11px; color: var(--text-muted); font-variant-numeric: tabular-nums; }
.dock-list { display: flex; flex-direction: column; gap: 6px; margin: 0; padding: 0; list-style: none; }
.dock-row {
  display: flex; align-items: center; justify-content: space-between; gap: 8px;
  padding: 8px 10px; border-radius: var(--radius-card, 10px); border: 1px solid var(--border-muted);
  background: var(--surface-raised); min-width: 0;
}
.dock-row input[type="checkbox"] { flex-shrink: 0; cursor: pointer; }
.dock-row input[type="checkbox"]:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 2px;
}
.dock-row-copy { display: flex; flex-direction: column; gap: 2px; flex: 1; min-width: 0; font-size: 12px; }
.dock-row-copy strong { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.dock-row-copy span {
  font-size: 11px; color: var(--text-muted); font-variant-numeric: tabular-nums;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.dock-empty {
  margin: 12px 0; font-size: 12px; color: var(--text-muted); text-align: center;
  font-variant-numeric: tabular-nums;
}
.dock-quick-grant {
  display: flex; flex-direction: column; gap: 6px; padding: 8px 10px;
  border-radius: var(--radius-card, 10px); border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}
.dock-quick-head {
  font-size: 11px; font-weight: 600; color: var(--text-secondary);
}
.dock-quick-row {
  display: flex; align-items: center; gap: 6px; flex-wrap: wrap; min-width: 0;
}
.dock-select {
  flex: 1 1 110px; min-width: 0; height: 28px; padding: 0 8px;
  border-radius: var(--radius-control, 6px); border: 1px solid var(--border-muted);
  background: var(--surface-section); color: var(--text-primary); font-size: 12px;
}
.dock-select-compact { flex: 1 1 88px; }
.dock-select:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  border-color: var(--accent-primary);
}
.dock-inline-btn {
  height: 28px; white-space: nowrap; flex-shrink: 0;
}
.dock-row-actions {
  display: flex; align-items: center; gap: 6px; flex-shrink: 0;
}
.dock-mini-btn {
  display: inline-flex; align-items: center; justify-content: center; gap: 4px;
  height: 26px; padding: 0 8px; border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted); background: var(--surface-section);
  color: var(--text-secondary); font-size: 11px; font-weight: 500;
  white-space: nowrap; flex-shrink: 0; cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease, border-color 120ms ease;
}
.dock-mini-btn:hover:not(:disabled) {
  background: var(--surface-hover, var(--surface-raised));
  color: var(--text-primary);
}
.dock-mini-btn.is-danger:hover:not(:disabled) {
  color: var(--accent-danger);
  border-color: color-mix(in srgb, var(--accent-danger) 32%, var(--border-muted));
}
.dock-mini-btn:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
}
.dock-mini-btn:disabled { opacity: 0.45; cursor: default; }
.dock-work-sessions {
  display: flex; flex-direction: column; gap: 8px; margin-top: 6px; padding-top: 10px;
  border-top: 1px solid var(--border-muted); min-width: 0;
}
.dock-head-title-group {
  display: flex; align-items: center; gap: 6px; min-width: 0;
}
.dock-count-pill {
  display: inline-flex; align-items: center; justify-content: center;
  min-width: 20px; height: 18px; padding: 0 6px; border-radius: 999px;
  border: 1px solid var(--border-muted); background: var(--surface-raised);
  color: var(--text-secondary); font-size: 11px; font-weight: 600;
  font-variant-numeric: tabular-nums; flex-shrink: 0;
}
.dock-ws-list {
  display: flex; flex-direction: column; gap: 8px; min-width: 0;
}
.dock-spin {
  animation: dock-spin 1s linear infinite;
}
@keyframes dock-spin {
  to { transform: rotate(360deg); }
}
@media (prefers-reduced-motion: reduce) {
  .dock-spin { animation: none; }
}
</style>
