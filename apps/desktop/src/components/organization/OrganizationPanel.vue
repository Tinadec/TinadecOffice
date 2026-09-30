<script setup lang="ts">
import { computed, inject, nextTick, onBeforeUnmount, onMounted, ref, toValue, watch, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { Archive, ArrowLeft, AtSign, ChevronUp, MessagesSquare, Network, RefreshCw, Send, Users, X } from '@lucide/vue'
import { UiButton, UiInput, UiTextarea } from '@/components/ui'
import EvidenceSearch from './EvidenceSearch.vue'
import EnvironmentList from './EnvironmentList.vue'
import { useNotifications } from '@/composables/useNotifications'
import {
  api,
  type OrganizationDto,
  type OrganizationMemberDto,
  type OrganizationMessageDto,
  type OrganizationReportDto,
  type OrganizationRoomDto,
  type OrganizationSubjectStateDto,
  type SessionTopologyDto,
  type TopologyLeaseDto,
} from '@/api'

/**
 * A session's organization: its members, the rooms they talk in, the reports governance files, and
 * the session drawn as runs, tasks and leases. Core owns every rule. The panel writes only what the
 * owner may do as the organization's human member — post in a room it belongs to, decide an open
 * report — and Core checks both again.
 */
const props = defineProps<{ sessionId?: string | null }>()

type Section = 'members' | 'rooms' | 'reports' | 'evidence' | 'environments' | 'topology'
type Phase = 'idle' | 'loading' | 'ready' | 'not-started' | 'error'

const SECTIONS: readonly Section[] = ['members', 'rooms', 'reports', 'evidence', 'environments', 'topology']
const ROLE_ORDER = ['human', 'conversation', 'governance', 'executor']
const ROOM_ORDER = ['lobby', 'board', 'plan', 'adhoc']
/**
 * A room numbers its messages densely (one sequence per message), so a window of PAGE sequences
 * never holds more than one page: reading from `lowerBound - PAGE` fills the gap in one request.
 */
const PAGE = 50
/** Core's ceiling for one room read. */
const FOLLOW_LIMIT = 100
const REPORT_LIMIT = 100
const POLL_MS = 5000
const NOTICE_KEY = 'organization-panel'

const { t, te, locale } = useI18n()
const { notify, status, dismissByKey } = useNotifications()
const active = inject<MaybeRefOrGetter<boolean>>('uie:active', true)

const section = ref<Section>('members')
const phase = ref<Phase>(props.sessionId ? 'loading' : 'idle')
const organization = ref<OrganizationDto | null>(null)
const busy = ref(false)
const updatedAt = ref<Date | null>(null)

// Rooms: the open room shows (lowerBound, cursor]; earlier pages are read on demand, newer ones by polling.
const selectedRoomId = ref<string | null>(null)
const messages = ref<OrganizationMessageDto[]>([])
const lowerBound = ref(0)
const cursor = ref(0)
const loadingMessages = ref(false)
const scroller = ref<HTMLElement | null>(null)
const atBottom = ref(true)
const draft = ref('')
const mentions = ref<string[]>([])
const mentionPickerOpen = ref(false)
const posting = ref(false)

// null = not read yet for this session, so "loading" and "none" stay two different sentences.
const reports = ref<OrganizationReportDto[] | null>(null)
const reportsTruncated = ref(false)
const notes = ref<Record<string, string>>({})
const deciding = ref<string | null>(null)
const topology = ref<SessionTopologyDto | null>(null)

let epoch = 0
let roomEpoch = 0
let running: Promise<void> | null = null
let again = false
let draftClientId: string | null = null
let timer: ReturnType<typeof setInterval> | undefined
let disposed = false

const writable = computed(() => organization.value?.status === 'active')
const members = computed(() => organization.value?.members ?? [])
const membersById = computed(() => new Map(members.value.map((member) => [member.id, member])))
const memberGroups = computed(() => groupBy(members.value, (member) => member.role, ROLE_ORDER))
const roomGroups = computed(() => groupBy(organization.value?.rooms ?? [], (room) => room.kind, ROOM_ORDER))
const selectedRoom = computed(() => organization.value?.rooms.find((room) => room.id === selectedRoomId.value) ?? null)
const canPost = computed(() => writable.value && selectedRoom.value?.is_member === true)
const mentionable = computed(() => members.value.filter((member) => member.id !== organization.value?.you_participant_id))
const openReports = computed(() => Number(organization.value?.open_reports ?? 0))
// Core already orders open → severity → newest; the stable sort only guarantees open ones lead.
const sortedReports = computed(() => [...(reports.value ?? [])].sort((a, b) => Number(a.status !== 'open') - Number(b.status !== 'open')))
const mutingVisibility = ref<string | null>(null)
/** Rule: a machine member's visibility into run internals is the owner's per-member choice (human/host rows never offer it). */
const VISIBILITY_ADJUSTABLE = new Set(['conversation', 'governance', 'executor'])
function visibilityAdjustable(member: OrganizationMemberDto): boolean {
  return VISIBILITY_ADJUSTABLE.has(member.role) && member.id !== organization.value?.you_participant_id
}

async function toggleVisibility(member: OrganizationMemberDto): Promise<void> {
  const id = props.sessionId
  if (!id || mutingVisibility.value) return
  const at = epoch
  const next = member.visibility_scope === 'own' ? null : 'own'
  mutingVisibility.value = member.id
  try {
    const updated = await api.setOrganizationMemberVisibility(id, member.id, next)
    if (at !== epoch) return
    organization.value = organization.value && ({
      ...organization.value,
      members: organization.value.members.map((row) => (row.id === updated.id ? updated : row)),
    })
    if (next === 'own') notify.warning({ title: t('organization.visibilityMuteShort'), message: t('organization.visibilityMuted', { name: member.display_name }) })
  } catch (cause) {
    if (at !== epoch) return
    if (problemOf(cause).code === 'organization_archived') void refresh()
    notify.error(cause, { title: t('organization.visibilityFailed') })
  } finally {
    if (at === epoch) mutingVisibility.value = null
  }
}
const runs = computed(() => [...(topology.value?.runs ?? [])].sort((a, b) => timeOf(b.started_at) - timeOf(a.started_at)))
const leases = computed(() => (topology.value?.leases ?? []).map((lease) => ({ lease, holder: leaseHolder(lease) })))

/** Groups in a fixed order; anything Core names that this build does not know goes last, under "other". */
function groupBy<T>(rows: T[], keyOf: (row: T) => string, order: string[]): Array<{ key: string; rows: T[] }> {
  const groups = order.map((key) => ({ key, rows: rows.filter((row) => keyOf(row) === key) }))
  groups.push({ key: 'other', rows: rows.filter((row) => !order.includes(keyOf(row))) })
  return groups.filter((group) => group.rows.length > 0)
}

/** A Core vocabulary word in the reader's language, or the word itself when this build has no translation. */
function label(group: string, value?: string | null): string {
  if (!value) return ''
  const key = `organization.${group}.${value}`
  return te(key) ? t(key) : value
}

function subjectStateLabel(state: OrganizationSubjectStateDto): string {
  if (!state.status) return t('organization.subjectUnknown')
  const specific = `organization.subjectState.${state.kind}_${state.status}`
  return te(specific) ? t(specific) : label('status', state.status)
}

function memberName(id?: string | null): string {
  return (id && membersById.value.get(id)?.display_name) || t('organization.unknownMember')
}

function mentionName(handle: string): string {
  return members.value.find((member) => member.handle === handle)?.display_name ?? handle
}

function presenceLabel(member: OrganizationMemberDto): string {
  return member.presence === 'online' ? t('organization.online') : t('organization.offline')
}

function leaseHolder(lease: TopologyLeaseDto): string | null {
  const view = topology.value
  if (!view) return null
  const task = lease.task_id ? view.runs.flatMap((run) => run.tasks).find((row) => row.task_id === lease.task_id) : undefined
  if (task) return task.handle || task.title
  const holder = lease.agent_instance_id ? view.members.find((row) => row.agent_instance_id === lease.agent_instance_id) : undefined
  return holder?.display_name ?? null
}

function timeOf(value?: string | null): number {
  const parsed = Date.parse(value ?? '')
  return Number.isFinite(parsed) ? parsed : 0
}

function time(value?: string | null): string {
  if (!value) return ''
  const date = new Date(value)
  if (!Number.isFinite(date.getTime())) return ''
  return new Intl.DateTimeFormat(locale.value, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(date)
}

const GUID = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i
function shortId(value?: string | null): string {
  return value && GUID.test(value) ? value.slice(0, 8) : (value ?? '')
}

function problemOf(cause: unknown): { status?: number; code?: string | null } {
  return (cause && typeof cause === 'object' ? cause : {}) as { status?: number; code?: string | null }
}

function newClientId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`
}

// ---- Loading ---------------------------------------------------------------------------------

/** One read at a time; a request made while one is running is folded into a single re-run after it. */
function refresh(): Promise<void> {
  if (running) {
    again = true
    return running
  }
  running = readLoop(epoch)
  return running
}

async function readLoop(at: number): Promise<void> {
  busy.value = true
  try {
    do {
      again = false
      await load(at)
    } while (again && at === epoch)
  } finally {
    // A session change already let go of this loop (and may have started the next one), so only
    // the loop of the current session clears the flag.
    if (at === epoch) {
      running = null
      busy.value = false
    }
  }
}

async function load(at: number): Promise<void> {
  const id = props.sessionId
  if (!id) return
  try {
    const org = await api.getOrganization(id)
    if (at !== epoch) return
    organization.value = org
    phase.value = org ? 'ready' : 'not-started'
    if (org) {
      if (selectedRoomId.value && !org.rooms.some((room) => room.id === selectedRoomId.value)) closeRoom()
      if (section.value === 'reports') await loadReports(id, at)
      else if (section.value === 'topology') await loadTopology(id, at)
      else if (section.value === 'rooms') await followRoom(id, at)
    }
    if (at !== epoch) return
    updatedAt.value = new Date()
    dismissByKey(NOTICE_KEY)
  } catch (cause) {
    if (at !== epoch || disposed) return
    if (phase.value === 'loading') phase.value = 'error'
    status.error({
      key: NOTICE_KEY,
      title: t('organization.loadFailed'),
      message: cause instanceof Error ? cause.message : String(cause),
      source: 'organization',
      action: { label: t('organization.retry'), run: () => refresh() },
    })
  }
}

async function loadReports(id: string, at: number): Promise<void> {
  const page = await api.listOrganizationReports(id, undefined, REPORT_LIMIT)
  if (at !== epoch) return
  reports.value = page.items
  reportsTruncated.value = page.truncated
}

async function loadTopology(id: string, at: number): Promise<void> {
  const view = await api.getSessionTopology(id)
  if (at !== epoch) return
  topology.value = view
}

// ---- Rooms -----------------------------------------------------------------------------------

function merge(rows: OrganizationMessageDto[]): void {
  messages.value = [...new Map([...messages.value, ...rows].map((row) => [row.id, row])).values()]
    .sort((a, b) => Number(a.sequence) - Number(b.sequence))
}

async function openRoom(room: OrganizationRoomDto): Promise<void> {
  const id = props.sessionId
  if (!id) return
  closeRoom()
  const at = epoch
  const atRoom = roomEpoch
  selectedRoomId.value = room.id
  const lower = Math.max(0, Number(room.last_sequence) - PAGE)
  lowerBound.value = lower
  cursor.value = lower
  loadingMessages.value = true
  try {
    const page = await api.readOrganizationRoom(id, room.id, lower, PAGE)
    if (at !== epoch || atRoom !== roomEpoch) return
    merge(page.items)
    cursor.value = Math.max(cursor.value, Number(page.next_cursor) || 0)
  } catch (cause) {
    if (at === epoch && atRoom === roomEpoch) notify.error(cause, { title: t('organization.loadFailed') })
  } finally {
    if (at === epoch && atRoom === roomEpoch) loadingMessages.value = false
  }
}

async function loadEarlier(): Promise<void> {
  const id = props.sessionId
  const roomId = selectedRoomId.value
  if (!id || !roomId || lowerBound.value <= 0 || loadingMessages.value) return
  const at = epoch
  const atRoom = roomEpoch
  const lower = Math.max(0, lowerBound.value - PAGE)
  const element = scroller.value
  const height = element?.scrollHeight ?? 0
  const top = element?.scrollTop ?? 0
  loadingMessages.value = true
  try {
    const page = await api.readOrganizationRoom(id, roomId, lower, PAGE)
    if (at !== epoch || atRoom !== roomEpoch) return
    merge(page.items)
    lowerBound.value = lower
    await nextTick()
    // Keep the reader where they were rather than jumping by a page.
    if (element && scroller.value === element) element.scrollTop = top + element.scrollHeight - height
  } catch (cause) {
    if (at === epoch && atRoom === roomEpoch) notify.error(cause, { title: t('organization.loadFailed') })
  } finally {
    if (at === epoch && atRoom === roomEpoch) loadingMessages.value = false
  }
}

/** Polling half of the room: everything after the cursor, errors reported by the caller. */
async function followRoom(id: string, at: number): Promise<void> {
  const roomId = selectedRoomId.value
  if (!roomId || loadingMessages.value) return
  const atRoom = roomEpoch
  const page = await api.readOrganizationRoom(id, roomId, cursor.value, FOLLOW_LIMIT)
  if (at !== epoch || atRoom !== roomEpoch) return
  merge(page.items)
  cursor.value = Math.max(cursor.value, Number(page.next_cursor) || 0)
}

function closeRoom(): void {
  roomEpoch++
  selectedRoomId.value = null
  messages.value = []
  lowerBound.value = 0
  cursor.value = 0
  loadingMessages.value = false
  atBottom.value = true
  // A draft belongs to the room it was typed in.
  draft.value = ''
  mentions.value = []
  mentionPickerOpen.value = false
}

function onScroll(): void {
  const element = scroller.value
  if (element) atBottom.value = element.scrollHeight - element.scrollTop - element.clientHeight < 60
}

function toggleMention(handle: string): void {
  mentions.value = mentions.value.includes(handle) ? mentions.value.filter((item) => item !== handle) : [...mentions.value, handle]
}

function onComposerKeydown(event: KeyboardEvent): void {
  // isComposing: Enter that confirms an IME candidate is not a send.
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  void send()
}

async function send(): Promise<void> {
  const id = props.sessionId
  const room = selectedRoom.value
  const content = draft.value.trim()
  if (!id || !room || !content || posting.value || !canPost.value) return
  const at = epoch
  const atRoom = roomEpoch
  // Core dedupes on client_message_id, so a retry of the same text must reuse it and new text must not.
  const clientMessageId = draftClientId ??= newClientId()
  posting.value = true
  try {
    const message = await api.postOrganizationMessage(id, room.id, {
      content,
      client_message_id: clientMessageId,
      // Handles, not display names: names repeat across runs (every run has a search#1) and Core
      // refuses an ambiguous one; a handle names exactly one member.
      ...(mentions.value.length ? { mention: [...mentions.value] } : {}),
    })
    if (at !== epoch) return
    if (atRoom === roomEpoch) {
      merge([message])
      if (draft.value.trim() === content) draft.value = ''
      mentions.value = []
      mentionPickerOpen.value = false
    }
    draftClientId = null
    void refresh()
  } catch (cause) {
    if (at !== epoch) return
    if (problemOf(cause).code === 'organization_archived') void refresh()
    notify.error(cause, { title: t('organization.postFailed') })
  } finally {
    if (at === epoch) posting.value = false
  }
}

// ---- Reports ---------------------------------------------------------------------------------

async function decide(report: OrganizationReportDto, decision: 'acted' | 'dismissed'): Promise<void> {
  const id = props.sessionId
  if (!id || deciding.value) return
  const at = epoch
  const note = (notes.value[report.id] ?? '').trim()
  deciding.value = report.id
  try {
    // The decision is recorded against the revision this reader saw, so a report that changed
    // underneath is refused (412) instead of being decided blind.
    const updated = await api.decideOrganizationReport(id, report.id, {
      decision,
      expected_revision: Number(report.revision),
      ...(note ? { note } : {}),
    })
    if (at !== epoch) return
    reports.value = (reports.value ?? []).map((row) => (row.id === updated.id ? updated : row))
    delete notes.value[report.id]
    notify.success(t('organization.decided', { status: label('reportStatus', updated.status) }))
  } catch (cause) {
    if (at !== epoch) return
    const problem = problemOf(cause)
    if (problem.status === 412 || problem.code === 'report_closed') {
      notify.warning({
        key: `organization-report-${report.id}`,
        title: t('organization.reportRefreshed'),
        message: problem.code === 'report_closed' ? t('organization.reportClosed') : t('organization.reportStale'),
      })
    } else if (problem.code === 'organization_archived') {
      notify.warning({ title: t('organization.reportRefreshed'), message: t('organization.archived') })
    } else {
      notify.error(cause, { title: t('organization.decideFailed') })
    }
  } finally {
    if (at === epoch) deciding.value = null
  }
  if (at === epoch) void refresh()
}

// ---- Lifecycle -------------------------------------------------------------------------------

watch(() => props.sessionId, (next) => {
  epoch++
  running = null
  again = false
  busy.value = false
  organization.value = null
  phase.value = next ? 'loading' : 'idle'
  updatedAt.value = null
  closeRoom()
  reports.value = null
  reportsTruncated.value = false
  notes.value = {}
  deciding.value = null
  posting.value = false
  topology.value = null
  draftClientId = null
  dismissByKey(NOTICE_KEY)
  if (next) void refresh()
})
watch(section, () => { if (props.sessionId) void refresh() })
watch(draft, () => { draftClientId = null })
watch(() => toValue(active), (now) => { if (now && props.sessionId) void refresh() })
watch(() => messages.value.at(-1)?.id, async () => {
  if (!atBottom.value) return
  await nextTick()
  const element = scroller.value
  if (element) element.scrollTop = element.scrollHeight
})

onMounted(() => {
  if (props.sessionId) void refresh()
  timer = setInterval(() => {
    if (!running && props.sessionId && toValue(active) && document.visibilityState === 'visible') void refresh()
  }, POLL_MS)
})

onBeforeUnmount(() => {
  disposed = true
  epoch++
  roomEpoch++
  clearInterval(timer)
  dismissByKey(NOTICE_KEY)
})
</script>

<template>
  <section class="org-panel" data-testid="organization-panel">
    <header class="org-header">
      <div class="org-heading">
        <Users :size="17" />
        <div>
          <h2>{{ t('organization.title') }}</h2>
          <p v-if="updatedAt">{{ t('organization.updated', { time: time(updatedAt.toISOString()) }) }}</p>
        </div>
      </div>
      <UiButton
        variant="ghost"
        size="icon"
        :disabled="!sessionId"
        :title="t('organization.refresh')"
        :aria-label="t('organization.refresh')"
        data-testid="organization-refresh"
        @click="refresh()"
      >
        <RefreshCw :size="15" :class="{ spinning: busy }" />
      </UiButton>
    </header>

    <div v-if="phase === 'idle'" class="org-empty" data-testid="organization-no-session">
      <Users :size="30" />
      <h3>{{ t('organization.noSession') }}</h3>
    </div>
    <div v-else-if="phase === 'loading'" class="org-empty">
      <p>{{ t('organization.loading') }}</p>
    </div>
    <div v-else-if="phase === 'not-started'" class="org-empty" data-testid="organization-not-started">
      <Network :size="30" />
      <h3>{{ t('organization.notStarted') }}</h3>
      <p>{{ t('organization.notStartedHint') }}</p>
    </div>
    <div v-else-if="phase === 'error' || !organization" class="org-empty" data-testid="organization-error">
      <h3>{{ t('organization.loadFailed') }}</h3>
      <UiButton variant="outline" size="sm" @click="refresh()">{{ t('organization.retry') }}</UiButton>
    </div>

    <template v-else>
      <p v-if="!writable" class="org-banner" data-testid="organization-archived">
        <Archive :size="14" />{{ t('organization.archived') }}
      </p>

      <div class="org-tabs" role="tablist" :aria-label="t('organization.title')">
        <button
          v-for="key in SECTIONS"
          :key="key"
          type="button"
          role="tab"
          :aria-selected="section === key"
          :class="{ active: section === key }"
          :data-testid="`organization-tab-${key}`"
          @click="section = key"
        >
          {{ t(`organization.sections.${key}`) }}
          <span v-if="key === 'reports' && openReports > 0" class="org-count attention">{{ openReports }}</span>
          <span v-else-if="key === 'members'" class="org-count">{{ organization.members.length }}</span>
          <span v-else-if="key === 'rooms'" class="org-count">{{ organization.rooms.length }}</span>
        </button>
      </div>

      <!-- Members -->
      <div v-if="section === 'members'" class="org-body" role="tabpanel" data-testid="organization-members">
        <p v-if="organization.members_truncated" class="org-notice">{{ t('organization.membersTruncated', { count: organization.members.length }) }}</p>
        <p v-if="!organization.members.length" class="org-empty-line">{{ t('organization.noMembers') }}</p>
        <section v-for="group in memberGroups" :key="group.key" class="org-group" :data-role="group.key">
          <h3>{{ label('role', group.key) }}<span>{{ group.rows.length }}</span></h3>
          <ul>
            <li
              v-for="member in group.rows"
              :key="member.id"
              class="org-member"
              :class="{ offline: member.presence !== 'online' }"
              :data-member-id="member.id"
            >
              <span class="presence-dot" :class="{ online: member.presence === 'online' }" role="img" :aria-label="presenceLabel(member)" />
              <div class="org-member-main">
                <div class="org-member-name">
                  <strong>{{ member.display_name }}</strong>
                  <span v-if="member.id === organization.you_participant_id">{{ t('organization.you') }}</span>
                  <span v-if="member.presence !== 'online'" class="org-offline">{{ t('organization.offlineSuffix') }}</span>
                </div>
                <div class="org-member-meta">
                  <code>{{ member.handle }}</code>
                  <span v-if="member.agent_slug">{{ member.agent_slug }}</span>
                  <span v-if="member.parent_id" class="org-dispatched">{{ t('organization.dispatchedBy', { parent: memberName(member.parent_id) }) }}</span>
                  <span v-if="member.visibility_scope === 'own'" class="org-visibility">{{ t('organization.visibilityOwn') }}</span>
                </div>
              </div>
              <button
                v-if="visibilityAdjustable(member) && writable"
                type="button"
                class="org-visibility-toggle"
                :disabled="mutingVisibility === member.id"
                :aria-label="member.visibility_scope === 'own' ? t('organization.visibilityRestore', { name: member.display_name }) : t('organization.visibilityMute', { name: member.display_name })"
                :data-visibility-toggle="member.id"
                @click="toggleVisibility(member)"
              >
                {{ member.visibility_scope === 'own' ? t('organization.visibilityRestoreShort') : t('organization.visibilityMuteShort') }}
              </button>
            </li>
          </ul>
        </section>
      </div>

      <!-- Rooms: directory -->
      <div v-else-if="section === 'rooms' && !selectedRoom" class="org-body" role="tabpanel" data-testid="organization-rooms">
        <p v-if="organization.rooms_truncated" class="org-notice">{{ t('organization.roomsTruncated', { count: organization.rooms.length }) }}</p>
        <p v-if="!organization.rooms.length" class="org-empty-line">{{ t('organization.noRooms') }}</p>
        <section v-for="group in roomGroups" :key="group.key" class="org-group" :data-kind="group.key">
          <h3>{{ label('roomKind', group.key) }}<span>{{ group.rows.length }}</span></h3>
          <button
            v-for="room in group.rows"
            :key="room.id"
            type="button"
            class="org-room"
            :data-room-id="room.id"
            @click="openRoom(room)"
          >
            <span class="org-room-title">
              <MessagesSquare :size="14" />
              <strong>{{ room.title }}</strong>
              <span class="org-tag" :class="{ joined: room.is_member }">{{ room.is_member ? t('organization.joined') : t('organization.readOnly') }}</span>
            </span>
            <span class="org-room-meta">
              {{ t('organization.roomMeta', { members: room.member_count, messages: room.last_sequence }) }}
              <template v-if="room.plan_owner_id"> · {{ t('organization.planOwner', { name: memberName(room.plan_owner_id) }) }}</template>
            </span>
          </button>
        </section>
      </div>

      <!-- Rooms: one room -->
      <div v-else-if="section === 'rooms' && selectedRoom" class="org-body org-body--room" role="tabpanel" data-testid="organization-room">
        <header class="org-room-head">
          <UiButton variant="ghost" size="icon" :aria-label="t('organization.backToRooms')" :title="t('organization.backToRooms')" @click="closeRoom()">
            <ArrowLeft :size="15" />
          </UiButton>
          <div>
            <h3>{{ selectedRoom.title }}</h3>
            <p>{{ label('roomKind', selectedRoom.kind) }} · {{ t('organization.roomMeta', { members: selectedRoom.member_count, messages: selectedRoom.last_sequence }) }}</p>
          </div>
        </header>
        <div ref="scroller" class="org-messages" data-testid="organization-messages" @scroll="onScroll">
          <div v-if="lowerBound > 0" class="org-history">
            <UiButton variant="ghost" size="sm" :disabled="loadingMessages" data-testid="organization-earlier" @click="loadEarlier()">
              <ChevronUp :size="13" />{{ t('organization.earlier') }}
            </UiButton>
          </div>
          <p v-if="!messages.length && !loadingMessages" class="org-empty-line">{{ t('organization.noMessages') }}</p>
          <article
            v-for="message in messages"
            :key="message.id"
            class="org-message"
            :data-kind="message.kind"
            :data-message-id="message.id"
          >
            <div class="org-message-byline">
              <strong>{{ message.sender_display_name }}</strong>
              <span class="org-muted">{{ label('role', message.sender_role || 'other') }}</span>
              <span v-if="message.kind !== 'message'" class="org-tag" :data-kind="message.kind">{{ label('messageKind', message.kind) }}</span>
              <span v-if="message.sensitivity === 'confidential'" class="org-tag">{{ t('organization.confidential') }}</span>
              <time :datetime="message.created_at">{{ time(message.created_at) }}</time>
            </div>
            <div v-if="message.report" class="org-message-report" :data-severity="message.report.severity">
              <span class="org-severity" :data-severity="message.report.severity">{{ label('severity', message.report.severity) }}</span>
              <span>{{ label('reportKind', message.report.report_kind) }} · {{ label('reportStatus', message.report.status) }}</span>
              <p class="message-content">{{ message.report.finding }}</p>
            </div>
            <p v-else class="message-content org-message-content">{{ message.content }}</p>
          </article>
        </div>
        <form v-if="canPost" class="org-composer" data-testid="organization-composer" @submit.prevent="send()">
          <div v-if="mentions.length" class="org-mentions" role="list">
            <span v-for="handle in mentions" :key="handle" class="org-mention" role="listitem">
              <AtSign :size="11" />{{ mentionName(handle) }}
              <button type="button" :aria-label="t('organization.removeMention', { name: mentionName(handle) })" @click="toggleMention(handle)"><X :size="11" /></button>
            </span>
          </div>
          <UiTextarea
            v-model="draft"
            :rows="2"
            :disabled="posting"
            :placeholder="t('organization.composerPlaceholder', { room: selectedRoom.title })"
            class="org-draft"
            @keydown="onComposerKeydown"
          />
          <div class="org-composer-actions">
            <UiButton
              variant="ghost"
              size="xs"
              :aria-expanded="mentionPickerOpen"
              :title="t('organization.mentionHint')"
              data-testid="organization-mention-toggle"
              @click="mentionPickerOpen = !mentionPickerOpen"
            >
              <AtSign :size="12" />{{ t('organization.mention') }}
            </UiButton>
            <UiButton type="submit" size="xs" :disabled="posting || !draft.trim()" data-testid="organization-send">
              <Send :size="12" />{{ posting ? t('organization.sending') : t('organization.send') }}
            </UiButton>
          </div>
          <div v-if="mentionPickerOpen" class="org-mention-picker" role="group" :aria-label="t('organization.mentionHint')" data-testid="organization-mention-picker">
            <button
              v-for="member in mentionable"
              :key="member.id"
              type="button"
              :class="{ offline: member.presence !== 'online' }"
              :aria-pressed="mentions.includes(member.handle)"
              :data-handle="member.handle"
              @click="toggleMention(member.handle)"
            >
              {{ member.display_name }}<small>{{ member.handle }}</small>
            </button>
          </div>
        </form>
        <p v-else class="org-readonly" data-testid="organization-room-readonly">
          {{ writable ? t('organization.notMember') : t('organization.archived') }}
        </p>
      </div>

      <!-- Reports -->
      <div v-else-if="section === 'reports'" class="org-body" role="tabpanel" data-testid="organization-reports">
        <p v-if="reports === null" class="org-empty-line">{{ t('organization.loading') }}</p>
        <p v-else-if="!reports.length" class="org-empty-line">{{ t('organization.noReports') }}</p>
        <p v-if="reportsTruncated" class="org-notice">{{ t('organization.reportsTruncated', { count: reports?.length ?? 0 }) }}</p>
        <article
          v-for="report in sortedReports"
          :key="report.id"
          class="org-report"
          :data-severity="report.severity"
          :data-status="report.status"
          :data-report-id="report.id"
        >
          <header>
            <span class="org-severity" :data-severity="report.severity">{{ label('severity', report.severity) }}</span>
            <strong>{{ label('reportKind', report.report_kind) }}</strong>
            <span class="org-report-status">{{ label('reportStatus', report.status) }}</span>
            <time :datetime="report.created_at">{{ time(report.created_at) }}</time>
          </header>
          <p class="org-muted">{{ report.author_display_name }}</p>
          <p class="message-content org-finding">{{ report.finding }}</p>
          <div v-if="report.subject_kind" class="org-subject" data-testid="organization-report-subject">
            <span class="org-muted">{{ t('organization.subject') }}</span>
            <strong>{{ label('subjectKind', report.subject_kind) }}</strong>
            <code :title="report.subject_id ?? undefined">{{ shortId(report.subject_id) }}</code>
            <span v-if="report.subject_state?.label">{{ report.subject_state.label }}</span>
            <span
              v-if="report.subject_state"
              class="org-subject-state"
              :data-state="report.subject_state.status ?? 'unknown'"
            >{{ subjectStateLabel(report.subject_state) }}</span>
          </div>
          <div v-if="report.proposed_verb" class="org-subject">
            <span class="org-muted">{{ t('organization.proposed') }}</span>
            <strong>{{ label('verb', report.proposed_verb) }}</strong>
            <code v-if="report.proposed_args_json">{{ report.proposed_args_json }}</code>
          </div>
          <div v-if="report.evidence.length" class="org-evidence">
            <span class="org-muted">{{ t('organization.evidence') }}</span>
            <ul>
              <li v-for="(item, index) in report.evidence" :key="index" class="message-content">{{ item }}</li>
            </ul>
          </div>
          <div v-if="report.status === 'open' && writable" class="org-decision">
            <UiInput
              v-model="notes[report.id]"
              :disabled="deciding !== null"
              :placeholder="t('organization.notePlaceholder')"
              :aria-label="t('organization.notePlaceholder')"
              class="org-note"
            />
            <UiButton size="xs" variant="outline" :disabled="deciding !== null" :data-testid="`organization-act-${report.id}`" @click="decide(report, 'acted')">
              {{ t('organization.act') }}
            </UiButton>
            <UiButton size="xs" variant="ghost" :disabled="deciding !== null" :data-testid="`organization-dismiss-${report.id}`" @click="decide(report, 'dismissed')">
              {{ t('organization.dismiss') }}
            </UiButton>
          </div>
          <p v-else-if="report.decided_at" class="org-muted">
            {{ t('organization.decidedBy', { name: memberName(report.decided_by_id), time: time(report.decided_at), status: label('reportStatus', report.status) }) }}
            <template v-if="report.decision_note"> · {{ report.decision_note }}</template>
          </p>
        </article>
      </div>

      <!-- Topology -->
      <!-- Evidence: search the archive (verbatim results, reports, summaries); the search keeps its own state. -->
      <EvidenceSearch v-else-if="section === 'evidence'" :session-id="sessionId" />
      <EnvironmentList v-else-if="section === 'environments'" :session-id="sessionId" />

      <div v-else class="org-body" role="tabpanel" data-testid="organization-topology">
        <p v-if="!topology" class="org-empty-line">{{ t('organization.loading') }}</p>
        <template v-else>
          <p v-if="topology.runs_truncated" class="org-notice">{{ t('organization.runsTruncated') }}</p>
          <p v-if="!runs.length" class="org-empty-line">{{ t('organization.noRuns') }}</p>
          <article v-for="run in runs" :key="run.run_id" class="org-run" :data-run-id="run.run_id">
            <header>
              <strong :title="run.run_id">{{ t('organization.run', { id: shortId(run.run_id) }) }}</strong>
              <span class="org-status" :data-status="run.status">{{ label('status', run.status) }}</span>
              <span v-if="run.tier" class="org-tag">{{ t('organization.tier', { tier: run.tier }) }}</span>
              <span v-if="run.phase" class="org-tag">{{ t('organization.phase', { phase: label('status', run.phase) }) }}</span>
              <time :datetime="run.started_at ?? undefined">{{ time(run.started_at) }}</time>
            </header>
            <div v-if="run.tasks.length" class="org-table-wrap">
              <table class="org-tasks">
                <thead>
                  <tr>
                    <th>{{ t('organization.columns.handle') }}</th>
                    <th>{{ t('organization.columns.task') }}</th>
                    <th>{{ t('organization.columns.status') }}</th>
                    <th class="org-scope">{{ t('organization.columns.writeScope') }}</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="task in run.tasks" :key="task.task_id" :data-task-id="task.task_id">
                    <td class="org-nowrap"><code>{{ task.handle || '—' }}</code></td>
                    <td class="org-wrap" :title="task.task_key">{{ task.title || task.task_key }}</td>
                    <td class="org-nowrap"><span class="org-status" :data-status="task.status">{{ label('status', task.status) }}</span></td>
                    <td class="org-scope org-wrap">{{ task.write_scope.length ? task.write_scope.join(', ') : '—' }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
            <p v-else class="org-empty-line">{{ t('organization.noTasks') }}</p>
            <p v-if="run.tasks_truncated" class="org-notice">{{ t('organization.tasksTruncated') }}</p>
            <p class="org-muted">
              {{ t('organization.instances', { count: run.instances.length }) }}
              <template v-if="run.instances_truncated"> · {{ t('organization.instancesTruncated') }}</template>
            </p>
          </article>

          <section class="org-group org-leases">
            <h3>{{ t('organization.leases') }}<span>{{ leases.length }}</span></h3>
            <p v-if="!leases.length" class="org-empty-line">{{ t('organization.noLeases') }}</p>
            <ul>
              <li v-for="{ lease, holder } in leases" :key="lease.lease_id" class="org-lease" :data-lease-id="lease.lease_id">
                <div>
                  <code>{{ lease.kind }} {{ lease.resource_key }}</code>
                  <span class="org-tag">{{ lease.exclusive ? t('organization.exclusive') : t('organization.shared') }}</span>
                </div>
                <p v-if="lease.purpose" class="message-content">{{ lease.purpose }}</p>
                <p v-if="holder" class="org-muted">{{ t('organization.holder', { name: holder }) }}</p>
              </li>
            </ul>
            <p v-if="topology.leases_truncated" class="org-notice">{{ t('organization.leasesTruncated') }}</p>
          </section>
        </template>
      </div>
    </template>
  </section>
</template>

<style scoped>
.org-panel { height:100%; min-height:0; display:flex; flex-direction:column; color:var(--text-primary); font-size:12px; }
.org-header { display:flex; align-items:center; justify-content:space-between; gap:10px; padding:12px 14px 10px; border-bottom:1px solid var(--border-muted); }
.org-heading { display:flex; align-items:center; gap:10px; min-width:0; }
.org-heading > svg { color:var(--accent-primary); flex-shrink:0; }
h2,h3,p { margin:0; }
.org-heading h2 { font-size:14px; font-weight:650; }
.org-heading p { margin-top:2px; font-size:10px; color:var(--text-muted); }
.org-banner { display:flex; align-items:center; gap:6px; margin:10px 14px 0; padding:7px 10px; border-radius:8px; background:var(--surface-section); color:var(--text-secondary); font-size:11px; }
.org-tabs { display:flex; gap:3px; margin:10px 14px 4px; padding:3px; border-radius:8px; background:var(--surface-section); }
.org-tabs button { flex:1; display:inline-flex; align-items:center; justify-content:center; gap:5px; min-width:0; border:0; border-radius:6px; padding:6px 4px; background:transparent; color:var(--text-secondary); font-size:12px; cursor:pointer; transition:background .15s; white-space:nowrap; }
.org-tabs button.active { background:var(--surface-active); color:var(--text-primary); }
.org-count { font-size:10px; color:var(--text-muted); }
.org-count.attention { min-width:16px; padding:0 5px; border-radius:999px; background:color-mix(in srgb, var(--accent-warning) 22%, transparent); color:var(--accent-warning); font-weight:600; }
.org-body { flex:1; min-height:0; overflow-y:auto; padding:8px 14px 14px; display:flex; flex-direction:column; gap:12px; }
.org-body--room { overflow:hidden; padding-bottom:0; gap:0; }
.org-group h3 { display:flex; align-items:center; gap:6px; margin-bottom:6px; font-size:11px; font-weight:600; color:var(--text-secondary); }
.org-group h3 > span { font-weight:400; color:var(--text-muted); }
.org-group ul { list-style:none; margin:0; padding:0; display:flex; flex-direction:column; gap:2px; }
.org-member { display:flex; align-items:flex-start; gap:9px; padding:7px 8px; border-radius:8px; }
.org-member:hover { background:var(--surface-hover); }
.org-member.offline { color:var(--text-muted); }
.org-member.offline strong { font-weight:500; }
.presence-dot { width:8px; height:8px; margin-top:4px; border-radius:50%; flex-shrink:0; background:var(--text-muted); opacity:.55; }
.presence-dot.online { background:var(--accent-success); opacity:1; box-shadow:0 0 0 3px color-mix(in srgb, var(--accent-success) 18%, transparent); }
.org-member-main { min-width:0; flex:1; }
.org-member-name { display:flex; flex-wrap:wrap; align-items:baseline; gap:4px; overflow-wrap:anywhere; }
.org-member-name strong { font-weight:600; }
.org-offline { font-size:10px; }
.org-member-meta { display:flex; flex-wrap:wrap; gap:4px 8px; margin-top:3px; font-size:10px; color:var(--text-muted); overflow-wrap:anywhere; }
.org-dispatched { color:var(--text-secondary); }
.org-visibility { color:var(--accent-warning); }
.org-visibility-toggle { margin-left:auto; align-self:center; flex-shrink:0; background:none; border:none; padding:2px 6px; cursor:pointer; font-size:10px; color:var(--text-muted); }
.org-visibility-toggle:hover:not(:disabled) { color:var(--text-primary); }
.org-visibility-toggle:disabled { opacity:.5; cursor:default; }
code { font-family:var(--font-mono, ui-monospace, monospace); font-size:10px; overflow-wrap:anywhere; }
.org-room { display:flex; flex-direction:column; gap:4px; width:100%; margin-bottom:3px; padding:9px 10px; text-align:left; border:1px solid transparent; border-radius:8px; background:transparent; color:inherit; cursor:pointer; }
.org-room:hover { background:var(--surface-hover); border-color:var(--border-muted); }
.org-room-title { display:flex; align-items:center; gap:6px; min-width:0; }
.org-room-title > svg { color:var(--text-muted); flex-shrink:0; }
.org-room-title strong { flex:1; min-width:0; font-weight:600; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
.org-room-meta { padding-left:20px; font-size:10px; color:var(--text-muted); }
.org-tag { display:inline-flex; align-items:center; padding:1px 6px; border-radius:999px; background:var(--surface-raised); color:var(--text-muted); font-size:10px; white-space:nowrap; }
.org-tag.joined { color:var(--accent-primary); }
.org-tag[data-kind="report"] { color:var(--accent-warning); }
.org-tag[data-kind="notice"] { color:var(--accent-info); }
.org-room-head { display:flex; align-items:center; gap:8px; padding-bottom:8px; border-bottom:1px solid var(--border-muted); }
.org-room-head > div { min-width:0; }
.org-room-head h3 { font-size:13px; font-weight:600; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
.org-room-head p { margin-top:2px; font-size:10px; color:var(--text-muted); }
.org-messages { flex:1; min-height:0; overflow-y:auto; overflow-x:hidden; padding:6px 0 10px; }
.org-history { display:flex; justify-content:center; padding-bottom:6px; }
.org-message { padding:9px 0; border-bottom:1px solid color-mix(in srgb, var(--border-muted) 55%, transparent); }
.org-message-byline { display:flex; flex-wrap:wrap; align-items:center; gap:6px; }
.org-message-byline strong { font-weight:600; overflow-wrap:anywhere; }
.org-message-byline time { margin-left:auto; font-size:10px; color:var(--text-muted); }
.org-message-content { margin-top:5px; white-space:pre-wrap; overflow-wrap:anywhere; line-height:1.7; }
.org-message-report { display:flex; flex-wrap:wrap; align-items:center; gap:6px; margin-top:6px; padding:8px 10px; border-radius:8px; background:var(--surface-raised); font-size:11px; color:var(--text-secondary); }
.org-message-report p { flex-basis:100%; color:var(--text-primary); white-space:pre-wrap; overflow-wrap:anywhere; line-height:1.6; }
.org-muted { color:var(--text-muted); font-size:10px; }
.org-composer { display:flex; flex-direction:column; gap:6px; padding:8px 0 12px; border-top:1px solid var(--border-muted); }
.org-draft { min-height:52px; font-size:12px; resize:vertical; }
.org-composer-actions { display:flex; justify-content:space-between; gap:6px; }
.org-mentions { display:flex; flex-wrap:wrap; gap:4px; }
.org-mention { display:inline-flex; align-items:center; gap:3px; padding:2px 4px 2px 7px; border-radius:999px; background:color-mix(in srgb, var(--accent-primary) 14%, transparent); color:var(--accent-primary); font-size:10px; }
.org-mention button { display:inline-flex; border:0; padding:1px; background:transparent; color:inherit; cursor:pointer; border-radius:50%; }
.org-mention-picker { display:flex; flex-wrap:wrap; gap:4px; max-height:120px; overflow-y:auto; padding:6px; border-radius:8px; background:var(--surface-section); }
.org-mention-picker button { display:inline-flex; align-items:baseline; gap:4px; padding:3px 8px; border:1px solid var(--border-muted); border-radius:999px; background:transparent; color:var(--text-secondary); font-size:11px; cursor:pointer; }
.org-mention-picker button small { font-size:9px; color:var(--text-muted); }
.org-mention-picker button[aria-pressed="true"] { border-color:var(--accent-primary); color:var(--accent-primary); background:color-mix(in srgb, var(--accent-primary) 10%, transparent); }
.org-mention-picker button.offline { opacity:.6; }
.org-readonly { padding:10px 0 12px; border-top:1px solid var(--border-muted); color:var(--text-muted); font-size:11px; }
.org-report { display:flex; flex-direction:column; gap:6px; padding:10px 12px; border-radius:9px; background:var(--surface-section); border-left:3px solid var(--border-muted); }
.org-report[data-severity="info"] { border-left-color:var(--accent-info); }
.org-report[data-severity="warning"] { border-left-color:var(--accent-warning); }
.org-report[data-severity="blocking"] { border-left-color:var(--accent-danger); }
.org-report:not([data-status="open"]) { opacity:.72; }
.org-report header { display:flex; flex-wrap:wrap; align-items:center; gap:6px; }
.org-report header time { margin-left:auto; font-size:10px; color:var(--text-muted); }
.org-severity { font-size:10px; font-weight:600; }
.org-severity[data-severity="info"] { color:var(--accent-info); }
.org-severity[data-severity="warning"] { color:var(--accent-warning); }
.org-severity[data-severity="blocking"] { color:var(--accent-danger); }
.org-report-status { font-size:10px; color:var(--text-secondary); }
.org-finding { white-space:pre-wrap; overflow-wrap:anywhere; line-height:1.6; }
.org-subject { display:flex; flex-wrap:wrap; align-items:center; gap:4px 7px; font-size:11px; }
.org-subject-state { padding:1px 6px; border-radius:999px; background:var(--surface-raised); color:var(--text-secondary); font-size:10px; }
.org-subject-state[data-state="active"],.org-subject-state[data-state="held"] { color:var(--accent-warning); }
.org-subject-state[data-state="released"],.org-subject-state[data-state="free"],.org-subject-state[data-state="completed"] { color:var(--accent-success); }
.org-evidence ul { margin:3px 0 0; padding-left:16px; display:flex; flex-direction:column; gap:2px; color:var(--text-secondary); overflow-wrap:anywhere; }
.org-decision { display:flex; flex-wrap:wrap; align-items:center; gap:6px; }
.org-note { flex:1 1 140px; height:28px; font-size:11px; }
.org-run { display:flex; flex-direction:column; gap:8px; padding:10px 12px; border-radius:9px; background:var(--surface-section); }
.org-run header { display:flex; flex-wrap:wrap; align-items:center; gap:6px; }
.org-run header time { margin-left:auto; font-size:10px; color:var(--text-muted); }
.org-status { font-size:10px; color:var(--text-secondary); }
.org-status[data-status="running"],.org-status[data-status="executing"] { color:var(--accent-primary); }
.org-status[data-status="completed"] { color:var(--accent-success); }
.org-status[data-status="failed"] { color:var(--accent-danger); }
.org-status[data-status^="awaiting"],.org-status[data-status="blocked"] { color:var(--accent-warning); }
.org-table-wrap { overflow-x:auto; }
.org-tasks { width:100%; border-collapse:collapse; font-size:11px; }
.org-tasks th { text-align:left; font-weight:500; font-size:10px; color:var(--text-muted); padding:3px 6px; border-bottom:1px solid var(--border-muted); white-space:nowrap; }
.org-tasks td { padding:5px 6px; vertical-align:top; border-bottom:1px solid color-mix(in srgb, var(--border-muted) 50%, transparent); }
/* Only the long free-text cells may break anywhere: `anywhere` also shrinks a cell's min-content, which
   squeezed handles and statuses down to one character per line. */
.org-tasks .org-nowrap { white-space:nowrap; }
.org-tasks .org-wrap { overflow-wrap:anywhere; }
.org-tasks .org-scope { width:40%; }
td.org-scope { color:var(--text-secondary); font-family:var(--font-mono, ui-monospace, monospace); font-size:10px; }
.org-lease { display:flex; flex-direction:column; gap:3px; padding:7px 8px; border-radius:8px; background:var(--surface-section); }
.org-lease > div { display:flex; flex-wrap:wrap; align-items:center; gap:6px; }
.org-lease p.message-content { color:var(--text-secondary); font-size:11px; overflow-wrap:anywhere; }
.org-notice { padding:6px 9px; border-radius:7px; background:var(--surface-section); color:var(--text-muted); font-size:10px; }
.org-empty-line { color:var(--text-muted); font-size:11px; padding:6px 2px; }
.org-empty { flex:1; display:flex; flex-direction:column; align-items:center; justify-content:center; gap:10px; padding:24px; text-align:center; color:var(--text-muted); min-height:140px; }
.org-empty h3 { font-size:13px; font-weight:600; color:var(--text-secondary); }
.org-empty p { max-width:300px; font-size:11px; line-height:1.7; }
.spinning { animation:org-spin 1s linear infinite; }
@keyframes org-spin { to { transform:rotate(360deg); } }
@media (prefers-reduced-motion:reduce) { .spinning { animation:none; } .org-tabs button { transition:none; } }
</style>
