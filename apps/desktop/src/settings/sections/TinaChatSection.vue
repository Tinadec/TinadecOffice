<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, LockKeyhole, MessageCircle, Plus, RefreshCw, User, Users } from '@lucide/vue'
import { UiButton, UiCheckbox, UiInput, UiIslandCard, UiSwitch, UiTextarea } from '@/components/ui'
import { api, type TinaChatConversation, type TinaChatMember, type TinaChatParticipant, type TinaChatWorkspacePolicy } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import { useChatIdentity } from '@/tinaChat/useChatIdentity'

/**
 * TinaChat management surface: named identities, conversations and the workspace cross-boundary
 * policy. It creates no authority of its own — Core decides membership, audience and administration
 * on every call, and a stale revision or a withdrawn member fails closed here rather than locally.
 */
const { t } = useI18n()
const { status } = useNotifications()
const identity = useChatIdentity()

const participants = ref<TinaChatParticipant[]>([])
const conversations = ref<TinaChatConversation[]>([])
const members = ref<TinaChatMember[]>([])
const policy = ref<TinaChatWorkspacePolicy | null>(null)
const loading = ref(false)
const busy = ref(false)
const selectedRoom = ref<string | null>(null)
const actingId = ref('')

const draft = reactive({ handle: '', display_name: '', job_title: '', description: '', kind: 'agent', receive_human_messages: false, can_interpret_intent: true, discoverable: true })
const roomDraft = reactive({ title: '', kind: 'group', invitees: [] as string[] })
const inviteDraft = ref('')

const mine = computed(() => participants.value.filter(x => identity.isMine(x.id) && x.status === 'active'))
const actor = computed(() => actingId.value || mine.value[0]?.id || '')
const selectable = computed(() => participants.value.filter(x => x.status === 'active' && x.kind === 'agent'))
const nameOf = (id: string) => participants.value.find(x => x.id === id)?.display_name ?? id.slice(0, 8)

async function report<T>(action: () => Promise<T>, okKey: string): Promise<T | undefined> {
  busy.value = true
  try {
    const result = await action()
    if (okKey) status.success({ key: 'tina-chat-manage', title: t(okKey), message: t('tinaChat.manage'), source: 'TinaChat' })
    return result
  } catch (error) {
    const stale = (error as { status?: number })?.status === 412
    status.error({
      key: 'tina-chat-manage', title: t(stale ? 'tinaChat.revisionStale' : 'tinaChat.actionFailed'),
      message: error instanceof Error ? error.message : String(error), source: 'TinaChat',
      action: { label: t('tinaChat.retry'), run: load },
    })
    return undefined
  } finally {
    busy.value = false
  }
}

async function load() {
  loading.value = true
  try {
    const [people, rooms, nextPolicy] = await Promise.all([api.tinaChatParticipants(), Promise.resolve([] as TinaChatConversation[]), api.tinaChatPolicy()])
    participants.value = people
    policy.value = nextPolicy
    const current = actor.value
    conversations.value = current ? await api.tinaChatConversations(current) : []
    if (current && !people.some(x => x.id === current && x.status === 'active')) actingId.value = ''
  } catch (error) {
    status.error({ key: 'tina-chat-manage', title: t('tinaChat.loadFailed'),
      message: error instanceof Error ? error.message : String(error), source: 'TinaChat',
      action: { label: t('tinaChat.retry'), run: load } })
  } finally {
    loading.value = false
  }
}

async function register() {
  const created = await report(() => api.tinaChatRegisterParticipant({
    handle: draft.handle.trim(), display_name: draft.display_name.trim(), kind: draft.kind,
    job_title: draft.job_title.trim() || null, description: draft.description.trim() || null,
    receive_human_messages: draft.kind === 'human' || draft.receive_human_messages,
    can_interpret_intent: draft.kind === 'agent' && draft.can_interpret_intent, discoverable: draft.discoverable,
  }), 'tinaChat.registered')
  if (!created) return
  identity.claim(created.id)
  actingId.value = created.id
  draft.handle = ''; draft.display_name = ''; draft.job_title = ''; draft.description = ''
  await load()
}

async function setFlags(person: TinaChatParticipant, patch: Partial<TinaChatParticipant>) {
  await report(() => api.tinaChatUpdateParticipant(person.id, {
    expected_revision: person.revision, display_name: patch.display_name ?? person.display_name,
    job_title: patch.job_title ?? person.job_title, description: patch.description ?? person.description,
    receive_human_messages: patch.receive_human_messages ?? person.receive_human_messages,
    can_interpret_intent: patch.can_interpret_intent ?? person.can_interpret_intent,
    discoverable: patch.discoverable ?? person.discoverable, status: patch.status ?? person.status,
  }), '')
  await load()
}

async function createRoom() {
  if (!actor.value) return
  const created = await report(() => api.tinaChatCreateConversation({
    actor_id: actor.value, title: roomDraft.title.trim(), kind: roomDraft.kind,
    participant_ids: roomDraft.invitees.filter(x => x !== actor.value),
    client_request_id: crypto.randomUUID(),
  }), 'tinaChat.roomCreated')
  if (!created) return
  roomDraft.title = ''; roomDraft.invitees = []
  await load()
  await openRoom(created.id)
}

async function openRoom(id: string) {
  selectedRoom.value = id
  members.value = actor.value ? await api.tinaChatMembers(id, actor.value).catch(() => []) : []
}

async function memberAction(room: TinaChatConversation, participantId: string, action: string, role?: string) {
  await report(() => api.tinaChatChangeMember(room.id, { actor_id: actor.value, participant_id: participantId, action, expected_revision: room.revision, ...(role ? { role } : {}) }), '')
  await load()
  if (selectedRoom.value === room.id) await openRoom(room.id)
}

async function invite() {
  const room = conversations.value.find(x => x.id === selectedRoom.value)
  if (!room || !inviteDraft.value) return
  await memberAction(room, inviteDraft.value, 'invite')
  inviteDraft.value = ''
}

async function savePolicy(field: 'allow_cross_workspace_discovery' | 'allow_cross_workspace_messaging', value: boolean) {
  if (!policy.value) return
  const saved = await report(() => api.tinaChatSetPolicy({
    expected_revision: policy.value!.revision,
    allow_cross_workspace_discovery: field === 'allow_cross_workspace_discovery' ? value : policy.value!.allow_cross_workspace_discovery,
    allow_cross_workspace_messaging: field === 'allow_cross_workspace_messaging' ? value : policy.value!.allow_cross_workspace_messaging,
  }), 'tinaChat.policySaved')
  if (saved) policy.value = saved
}

onMounted(load)
defineExpose({ load, participants, conversations, policy, actor, register, createRoom, openRoom, memberAction, setFlags })
</script>

<template>
  <div class="tinachat-manage" data-testid="tinachat-section">
    <h2>{{ t('tinaChat.manage') }}</h2>
    <p class="section-hint">{{ t('tinaChat.manageHint') }}</p>
    <div class="section-actions">
      <UiButton variant="outline" size="sm" :disabled="loading" data-testid="tinachat-reload" @click="load"><RefreshCw :size="13" />{{ t('tinaChat.refresh') }}</UiButton>
    </div>

    <UiIslandCard variant="section" divided>
      <template #header>
        <div class="card-head"><Users :size="15" /><strong>{{ t('tinaChat.identities') }}</strong><span>{{ participants.length }}</span></div>
        <p class="card-hint">{{ t('tinaChat.identitiesHint') }}</p>
      </template>
      <div class="identity-toolbar">
        <label class="field"><span>{{ t('tinaChat.actingAs') }}</span>
          <select v-model="actingId" data-testid="tinachat-actor" :disabled="!mine.length">
            <option value="">{{ mine.length ? t('tinaChat.pickIdentity') : t('tinaChat.noIdentityYet') }}</option>
            <option v-for="person in mine" :key="person.id" :value="person.id">{{ person.display_name }}</option>
          </select>
        </label>
        <p class="card-hint">{{ t('tinaChat.actingAsHint') }}</p>
      </div>
      <ul class="identity-list" data-testid="tinachat-identities">
        <li v-for="person in participants" :key="person.id" :data-testid="`tinachat-identity-${person.handle}`">
          <span class="identity-icon"><Bot v-if="person.kind === 'agent'" :size="15" /><User v-else :size="15" /></span>
          <span class="identity-main"><strong>{{ person.display_name }}</strong><small>@{{ person.handle }} · {{ t(`tinaChat.${person.kind === 'human' ? 'kindHuman' : 'kindAgent'}`) }}<template v-if="identity.isMine(person.id)"> · {{ t('tinaChat.mine') }}</template></small></span>
          <span class="identity-flags">
            <UiCheckbox :model-value="person.receive_human_messages" :disabled="busy || person.kind === 'human'" :aria-label="t('tinaChat.receiveHuman')" @update:model-value="setFlags(person, { receive_human_messages: Boolean($event) })" />
            <UiCheckbox :model-value="person.can_interpret_intent" :disabled="busy || person.kind === 'human'" :aria-label="t('tinaChat.canInterpret')" @update:model-value="setFlags(person, { can_interpret_intent: Boolean($event) })" />
            <UiCheckbox :model-value="person.discoverable" :disabled="busy" :aria-label="t('tinaChat.discoverable')" @update:model-value="setFlags(person, { discoverable: Boolean($event) })" />
          </span>
          <span class="status-chip" :class="person.status === 'active' ? 'live' : 'off'">{{ t(`tinaChat.pstatus_${person.status}`) }}</span>
          <UiButton variant="ghost" size="sm" :disabled="busy" @click="setFlags(person, { status: person.status === 'active' ? 'archived' : 'active' })">{{ t(person.status === 'active' ? 'tinaChat.archive' : 'tinaChat.restore') }}</UiButton>
        </li>
      </ul>
      <div class="register-grid">
        <UiInput v-model="draft.handle" :placeholder="t('tinaChat.handle')" :aria-label="t('tinaChat.handle')" data-testid="tinachat-handle" />
        <UiInput v-model="draft.display_name" :placeholder="t('tinaChat.displayName')" :aria-label="t('tinaChat.displayName')" data-testid="tinachat-display" />
        <select v-model="draft.kind" :aria-label="t('tinaChat.kindLabel')" data-testid="tinachat-kind"><option value="agent">{{ t('tinaChat.kindAgent') }}</option><option value="human">{{ t('tinaChat.kindHuman') }}</option></select>
        <UiInput v-model="draft.job_title" :placeholder="t('tinaChat.jobTitle')" :aria-label="t('tinaChat.jobTitle')" />
        <UiTextarea v-model="draft.description" :placeholder="t('tinaChat.description')" :aria-label="t('tinaChat.description')" :rows="2" />
        <label class="check"><UiCheckbox v-model="draft.receive_human_messages" :disabled="draft.kind === 'human'" />{{ t('tinaChat.receiveHuman') }}</label>
        <label class="check"><UiCheckbox v-model="draft.can_interpret_intent" :disabled="draft.kind === 'human'" />{{ t('tinaChat.canInterpret') }}</label>
        <label class="check"><UiCheckbox v-model="draft.discoverable" />{{ t('tinaChat.discoverable') }}</label>
        <UiButton variant="outline" size="sm" :disabled="busy || !draft.handle.trim() || !draft.display_name.trim()" data-testid="tinachat-register" @click="register"><Plus :size="13" />{{ t('tinaChat.register') }}</UiButton>
      </div>
      <p class="card-hint">{{ t('tinaChat.handleRule') }}</p>
    </UiIslandCard>

    <UiIslandCard variant="section" divided>
      <template #header>
        <div class="card-head"><MessageCircle :size="15" /><strong>{{ t('tinaChat.rooms') }}</strong><span>{{ conversations.length }}</span></div>
        <p class="card-hint">{{ actor ? t('tinaChat.roomsHint') : t('tinaChat.noIdentityYet') }}</p>
      </template>
      <div class="room-create">
        <UiInput v-model="roomDraft.title" :placeholder="t('tinaChat.roomTitle')" :aria-label="t('tinaChat.roomTitle')" data-testid="tinachat-room-title" :disabled="!actor" />
        <select v-model="roomDraft.kind" :aria-label="t('tinaChat.kindLabel')" :disabled="!actor"><option value="group">{{ t('tinaChat.group') }}</option><option value="direct">{{ t('tinaChat.direct') }}</option></select>
        <UiButton variant="outline" size="sm" :disabled="busy || !actor || !roomDraft.title.trim()" data-testid="tinachat-create-room" @click="createRoom">{{ t('tinaChat.createRoom') }}</UiButton>
      </div>
      <div class="invite-picker">
        <select v-model="inviteDraft" :aria-label="t('tinaChat.pickParticipants')" data-testid="tinachat-invitee" :disabled="!actor || !selectedRoom"><option value="">{{ t('tinaChat.pickParticipants') }}</option><option v-for="person in selectable" :key="person.id" :value="person.id">{{ person.display_name }}</option></select>
        <UiButton variant="ghost" size="sm" :disabled="busy || !selectedRoom || !inviteDraft" data-testid="tinachat-invite" @click="invite">{{ t('tinaChat.invite') }}</UiButton>
      </div>
      <ul class="room-list" data-testid="tinachat-rooms">
        <li v-for="room in conversations" :key="room.id" :class="{ selected: selectedRoom === room.id }" :data-testid="`tinachat-room-${room.id}`">
          <button type="button" class="room-open" @click="openRoom(room.id)"><strong>{{ room.title }}</strong><small>{{ t(`tinaChat.${room.kind === 'direct' ? 'direct' : 'group'}`) }} · {{ t(`tinaChat.status_${room.membership_status}`) }}</small></button>
          <div v-if="selectedRoom === room.id" class="room-members">
            <p v-if="!members.length" class="card-hint">{{ t('tinaChat.noMembers') }}</p>
            <div v-for="member in members" :key="member.participant_id" class="member-row" :data-testid="`tinachat-member-${member.participant_id}`">
              <span><strong>{{ nameOf(member.participant_id) }}</strong><small>{{ t(`tinaChat.role_${member.role}`) }} · {{ t(`tinaChat.status_${member.status}`) }}</small></span>
              <span class="member-actions">
                <UiButton v-if="member.status === 'invited' && member.participant_id === actor" variant="outline" size="sm" :disabled="busy" @click="memberAction(room, member.participant_id, 'accept')">{{ t('tinaChat.acceptInvite') }}</UiButton>
                <UiButton v-if="member.status === 'active' && member.role === 'member'" variant="ghost" size="sm" :disabled="busy" @click="memberAction(room, member.participant_id, 'role', 'admin')">{{ t('tinaChat.makeAdmin') }}</UiButton>
                <UiButton v-else-if="member.status === 'active' && member.role === 'admin'" variant="ghost" size="sm" :disabled="busy" @click="memberAction(room, member.participant_id, 'role', 'member')">{{ t('tinaChat.makeMember') }}</UiButton>
                <UiButton v-if="member.status === 'active' && member.role !== 'owner' && member.participant_id !== actor" variant="ghost" size="sm" :disabled="busy" @click="memberAction(room, member.participant_id, 'remove')">{{ t('tinaChat.removeMember') }}</UiButton>
                <UiButton v-if="member.status === 'active' && member.participant_id !== actor" variant="ghost" size="sm" :disabled="busy" @click="memberAction(room, member.participant_id, 'transfer')">{{ t('tinaChat.transferOwner') }}</UiButton>
              </span>
            </div>
          </div>
        </li>
      </ul>
    </UiIslandCard>

    <UiIslandCard variant="section" divided>
      <template #header>
        <div class="card-head"><LockKeyhole :size="15" /><strong>{{ t('tinaChat.crossWorkspace') }}</strong></div>
        <p class="card-hint">{{ t('tinaChat.crossHint') }}</p>
      </template>
      <div v-if="policy" class="policy-rows" data-testid="tinachat-policy">
        <label class="check"><UiSwitch :model-value="policy.allow_cross_workspace_discovery" :disabled="busy" @update:model-value="savePolicy('allow_cross_workspace_discovery', Boolean($event))" />{{ t('tinaChat.allowDiscovery') }}</label>
        <label class="check"><UiSwitch :model-value="policy.allow_cross_workspace_messaging" :disabled="busy" @update:model-value="savePolicy('allow_cross_workspace_messaging', Boolean($event))" />{{ t('tinaChat.allowMessaging') }}</label>
        <p class="card-hint">{{ t('tinaChat.policyAdminOnly') }}</p>
      </div>
      <p v-else class="card-hint">{{ t('tinaChat.policyUnavailable') }}</p>
    </UiIslandCard>
  </div>
</template>

<style scoped>
.tinachat-manage { display: flex; flex-direction: column; gap: 14px; }
.section-hint, .card-hint { color: var(--text-muted); font-size: 12px; line-height: 1.6; margin: 0; }
.section-actions { display: flex; gap: 8px; }
.card-head { display: flex; align-items: center; gap: 7px; }
.card-head span { margin-left: auto; font-size: 11px; color: var(--text-muted); }
.card-head + .card-hint { margin-top: 4px; }
.identity-toolbar, .room-create, .invite-picker { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.field { display: flex; align-items: center; gap: 6px; font-size: 12px; }
select { height: 30px; border: 1px solid var(--border-muted); border-radius: 8px; background: var(--surface-input); color: var(--text-primary); padding: 0 8px; font-size: 12px; }
.identity-list, .room-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
.identity-list li { display: flex; align-items: center; gap: 10px; padding: 6px 8px; border-radius: 8px; background: var(--surface-raised); }
.identity-icon { display: inline-flex; color: var(--text-muted); }
.identity-main { display: flex; flex-direction: column; min-width: 0; flex: 1; }
.identity-main small { color: var(--text-muted); font-size: 11px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.identity-flags { display: inline-flex; gap: 10px; }
.register-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 8px; align-items: center; }
.check { display: inline-flex; align-items: center; gap: 7px; font-size: 12px; }
.room-list li { border: 1px solid var(--border-muted); border-radius: 9px; background: var(--surface-raised); }
.room-list li.selected { border-color: var(--border-strong); }
.room-open { display: flex; flex-direction: column; align-items: flex-start; gap: 2px; width: 100%; padding: 8px 10px; background: none; border: 0; color: inherit; cursor: pointer; text-align: left; }
.room-open small { color: var(--text-muted); font-size: 11px; }
.room-members { padding: 0 10px 10px; display: flex; flex-direction: column; gap: 4px; }
.member-row { display: flex; align-items: center; gap: 10px; justify-content: space-between; }
.member-row small { display: block; color: var(--text-muted); font-size: 11px; }
.member-actions { display: inline-flex; gap: 4px; flex-wrap: wrap; }
.policy-rows { display: flex; flex-direction: column; gap: 10px; }
.status-chip { font-size: 11px; padding: 1px 7px; border-radius: 999px; border: 1px solid var(--border-muted); color: var(--text-muted); white-space: nowrap; }
.status-chip.live { color: var(--accent-primary); border-color: currentColor; }
</style>
