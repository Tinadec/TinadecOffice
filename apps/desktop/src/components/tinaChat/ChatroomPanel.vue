<script setup lang="ts">
import { computed, inject, nextTick, onBeforeUnmount, onMounted, ref, toValue, watch, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowDown, ArrowLeft, ArrowRight, Bot, ChevronDown, Eye, Info, LockKeyhole, MessageCircle, MessagesSquare, RefreshCw, Search, ShieldCheck, User, Users } from '@lucide/vue'
import { UiButton, UiInput } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { useChatroomObserver } from '@/tinaChat/useChatroomObserver'
import type { TinaChatObservedMessage } from '@/api'

const { t, locale } = useI18n()
const { status, dismissByKey } = useNotifications()
const observer = useChatroomObserver((error) => {
  if ([401, 403].includes((error as { status?: number })?.status ?? 0)) return
  status.error({ key: 'tina-chat-observer', title: t('tinaChat.loadFailed'),
    message: error instanceof Error ? error.message : String(error), source: 'TinaChat',
    action: { label: t('tinaChat.retry'), run: () => observer.refresh() } })
})
const { access, conversations, total, hasMoreConversations, selectedId, detail, messages, hasOlder, loading,
  loadingMessages, refreshing, forbidden, error, updatedAt } = observer
const active = inject<MaybeRefOrGetter<boolean>>('wb:active', true)
const query = ref('')
const kind = ref('')
const workspace = ref('')
const autoRefresh = ref(true)
const showMembers = ref(false)
const mobileConversation = ref(false)
const scroller = ref<HTMLElement | null>(null)
const atBottom = ref(true)
const isBusy = computed(() => loading.value || loadingMessages.value || refreshing.value)
let timer: ReturnType<typeof setInterval> | undefined
let searchTimer: ReturnType<typeof setTimeout> | undefined

const filters = computed(() => ({ query: query.value.trim(), kind: kind.value, workspace_id: workspace.value }))
watch(filters, () => {
  clearTimeout(searchTimer)
  searchTimer = setTimeout(() => { void observer.setFilters(filters.value) }, 250)
})
watch(selectedId, () => { showMembers.value = false; atBottom.value = true })
watch(error, value => { if (!value) dismissByKey('tina-chat-observer') })
watch(() => messages.value.at(-1)?.message.id, async () => { if (atBottom.value) { await nextTick(); scrollToLatest() } })

function scrollToLatest() {
  const element = scroller.value
  if (element) element.scrollTop = element.scrollHeight
  atBottom.value = true
}
function onScroll() {
  const element = scroller.value
  if (element) atBottom.value = element.scrollHeight - element.scrollTop - element.clientHeight < 60
}
async function older() {
  const element = scroller.value
  const height = element?.scrollHeight ?? 0
  const top = element?.scrollTop ?? 0
  await observer.loadOlder()
  await nextTick()
  if (element && scroller.value === element) element.scrollTop = top + element.scrollHeight - height
}
function select(id: string) {
  mobileConversation.value = true
  void observer.selectConversation(id)
}
function recipients(row: TinaChatObservedMessage) {
  return row.audience.filter(x => x.participant.id !== row.sender.id && x.can_read_original)
    .map(x => x.participant.display_name).join('、') || t('tinaChat.selfOnly')
}
function time(value?: string | null, full = false) {
  if (!value) return ''
  const date = new Date(value)
  if (!Number.isFinite(date.getTime())) return ''
  return new Intl.DateTimeFormat(locale.value, full
    ? { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' }
    : { hour: '2-digit', minute: '2-digit' }).format(date)
}
function intentSections(row: TinaChatObservedMessage): Array<{ key: string; values: string[] }> {
  if (row.message.kind !== 'intent_brief') return []
  try {
    const data = JSON.parse(row.message.content) as Record<string, unknown>
    const keys = ['goal', 'userStatements', 'constraints', 'assumptions', 'openQuestions', 'blockingQuestions', 'acceptanceCriteria']
    return keys.map(key => {
      const value = data[key] ?? data[key.replace(/[A-Z]/g, c => '_' + c.toLowerCase())]
      return { key, values: typeof value === 'string' ? [value] : Array.isArray(value) ? value.filter((x): x is string => typeof x === 'string') : [] }
    }).filter(section => section.values.length > 0)
  } catch { return [] }
}

onMounted(() => {
  void observer.loadConversations()
  timer = setInterval(() => {
    if (autoRefresh.value && !forbidden.value && toValue(active) && document.visibilityState === 'visible') void observer.refresh()
  }, 5000)
})
onBeforeUnmount(() => { clearInterval(timer); clearTimeout(searchTimer); dismissByKey('tina-chat-observer') })
</script>

<template>
  <section class="chatroom" data-testid="chatroom-panel">
    <header class="chatroom-header">
      <div class="chatroom-heading"><MessagesSquare :size="21" /><div><h1>{{ t('tinaChat.title') }}</h1><p>{{ t('tinaChat.subtitle') }}</p></div></div>
      <div class="chatroom-actions">
        <span class="observer-badge"><ShieldCheck :size="13" />{{ t('tinaChat.observer') }}</span>
        <label class="refresh-toggle"><input v-model="autoRefresh" type="checkbox" />{{ t('tinaChat.autoRefresh') }}</label>
        <UiButton variant="ghost" size="icon" :disabled="isBusy" :title="t('tinaChat.refresh')" :aria-label="t('tinaChat.refresh')" data-testid="chatroom-refresh" @click="observer.refresh()"><RefreshCw :size="15" :class="{ spinning: isBusy }" /></UiButton>
      </div>
    </header>

    <div v-if="forbidden" class="chatroom-empty" data-testid="chatroom-forbidden"><LockKeyhole :size="32" /><h2>{{ t('tinaChat.adminRequired') }}</h2><p>{{ t('tinaChat.adminRequiredHint') }}</p><UiButton variant="outline" size="sm" @click="observer.refresh()">{{ t('tinaChat.retry') }}</UiButton></div>
    <div v-else class="chatroom-body" :class="{ 'mobile-conversation': mobileConversation && selectedId }">
      <aside class="room-directory">
        <div class="directory-controls">
          <div class="room-search"><Search :size="14" /><UiInput v-model="query" :placeholder="t('tinaChat.search')" :aria-label="t('tinaChat.search')" data-testid="chatroom-search" /></div>
          <div class="room-tabs" :aria-label="t('tinaChat.conversationTypes')"><button v-for="tab in ['', 'group', 'direct']" :key="tab" type="button" :class="{ active: kind === tab }" :aria-pressed="kind === tab" :data-testid="`chatroom-filter-${tab || 'all'}`" @click="kind = tab">{{ t(`tinaChat.${tab || 'all'}`) }}</button></div>
          <select v-if="(access?.workspaces.length ?? 0) > 1" v-model="workspace" :aria-label="t('tinaChat.workspace')" class="workspace-filter"><option value="">{{ t('tinaChat.allWorkspaces') }}</option><option v-for="item in access?.workspaces" :key="item.id" :value="item.id">{{ item.name }}</option></select>
          <div class="directory-count"><span>{{ t('tinaChat.roomCount', { count: total }) }}</span><span v-if="loading">{{ t('tinaChat.loading') }}</span></div>
        </div>
        <div class="room-list" data-testid="chatroom-list">
          <div v-if="!conversations.length && !loading" class="directory-empty"><MessageCircle :size="24" /><p>{{ query || kind || workspace ? t('tinaChat.noMatches') : t('tinaChat.noRooms') }}</p><small>{{ t('tinaChat.noRoomsHint') }}</small></div>
          <button v-for="room in conversations" :key="room.id" type="button" class="room-row" :class="{ selected: room.id === selectedId }" :aria-pressed="room.id === selectedId" :data-conversation-id="room.id" @click="select(room.id)">
            <span class="room-avatar"><Users v-if="room.kind === 'group'" :size="18" /><MessageCircle v-else :size="18" /></span>
            <span class="room-description"><span class="room-title"><strong>{{ room.title }}</strong><time>{{ time(room.last_message_at) }}</time></span><span class="room-preview">{{ room.last_message_preview || room.participant_names.join('、') || t('tinaChat.noMessages') }}</span><span class="room-meta">{{ t(`tinaChat.${room.kind === 'direct' ? 'direct' : 'group'}`) }} · {{ t('tinaChat.messageCount', { count: room.last_sequence }) }}</span></span>
          </button>
          <UiButton v-if="hasMoreConversations" class="load-rooms" variant="ghost" size="sm" :disabled="loading" @click="observer.loadConversations(true)">{{ t('tinaChat.moreRooms') }}</UiButton>
        </div>
        <div class="directory-foot"><Eye :size="13" /><span>{{ t(access?.level === 'tenant' ? 'tinaChat.tenantScope' : 'tinaChat.workspaceScope') }}</span></div>
      </aside>

      <section class="room-conversation" :aria-label="t('tinaChat.messages')">
        <template v-if="detail">
          <header class="conversation-header">
            <UiButton class="mobile-back" variant="ghost" size="icon" :aria-label="t('tinaChat.back')" @click="mobileConversation = false"><ArrowLeft :size="16" /></UiButton>
            <div class="conversation-title"><h2>{{ detail.conversation.title }}</h2><p>{{ t(`tinaChat.${detail.conversation.kind === 'direct' ? 'direct' : 'group'}`) }} · {{ detail.conversation.workspace_name }} · {{ t('tinaChat.memberCount', { count: detail.conversation.member_count }) }}</p></div>
            <UiButton variant="ghost" size="icon" :aria-expanded="showMembers" :title="t('tinaChat.members')" :aria-label="t('tinaChat.members')" data-testid="chatroom-members" @click="showMembers = !showMembers"><Info :size="17" /></UiButton>
          </header>
          <div v-if="showMembers" class="room-members" data-testid="chatroom-member-list"><article v-for="member in detail.members" :key="member.participant.id"><Bot v-if="member.participant.kind === 'agent'" :size="16" /><User v-else :size="16" /><div><strong>{{ member.participant.display_name }}</strong><p>{{ member.participant.job_title || member.participant.handle }}</p><small>{{ member.participant.id }}</small></div><span>{{ t(`tinaChat.role_${member.role}`) }} · {{ t(`tinaChat.status_${member.status}`) }}</span></article></div>
          <div ref="scroller" class="room-messages" data-testid="chatroom-messages" @scroll="onScroll">
            <div v-if="hasOlder" class="history-action"><UiButton variant="ghost" size="sm" :disabled="loadingMessages || refreshing" data-testid="chatroom-older" @click="older"><ChevronDown :size="13" />{{ t('tinaChat.older') }}</UiButton></div>
            <div v-if="!messages.length" class="chatroom-empty"><MessageCircle :size="28" /><p>{{ t('tinaChat.noMessages') }}</p></div>
            <article v-for="row in messages" :key="row.message.id" class="observed-message" :data-message-id="row.message.id">
              <span class="sender-avatar" :class="{ human: row.sender.kind === 'human' }"><Bot v-if="row.sender.kind === 'agent'" :size="17" /><User v-else :size="17" /></span>
              <div class="message-main">
                <div class="message-byline"><strong :title="row.sender.id">{{ row.sender.display_name }}</strong><span class="message-role">{{ row.sender.job_title || t(`tinaChat.${row.sender.kind === 'human' ? 'human' : 'agent'}`) }}</span><time :title="row.message.created_at">{{ time(row.message.created_at, true) }}</time><span v-if="row.message.sensitivity === 'confidential'" class="private-badge"><LockKeyhole :size="11" />{{ t('tinaChat.confidential') }}</span></div>
                <div class="message-direction"><ArrowRight :size="12" /><span>{{ t('tinaChat.to') }} {{ recipients(row) }}</span></div>
                <div v-if="intentSections(row).length" class="intent-content message-content"><div class="intent-heading"><ShieldCheck :size="14" /><strong>{{ t('tinaChat.intent') }}</strong><span v-if="row.intent_status">{{ t(`tinaChat.intent_${row.intent_status}`) }}</span></div><div v-for="section in intentSections(row)" :key="section.key" class="intent-section"><h3>{{ t(`tinaChat.brief_${section.key}`) }}</h3><p v-for="(value, index) in section.values" :key="index">{{ value }}</p></div></div>
                <div v-else class="message-content observed-content">{{ row.message.content }}</div>
                <details class="delivery-details"><summary>{{ t('tinaChat.deliveryDetails') }}<span>#{{ row.message.sequence }}</span></summary><div class="delivery-body"><p class="delivery-hint">{{ t('tinaChat.deliveryHint') }}</p><div v-for="recipient in row.audience" :key="recipient.participant.id" class="delivery-row"><strong :title="recipient.participant.id">{{ recipient.participant.display_name }}<small v-if="recipient.participant.id === row.sender.id"> · {{ t('tinaChat.senderCopy') }}</small></strong><span>{{ recipient.can_read_original ? t('tinaChat.original') : recipient.can_receive_derived ? t('tinaChat.derivedOnly') : t('tinaChat.noDelivery') }}</span><span v-if="recipient.can_read_original && recipient.participant.id !== row.sender.id">{{ recipient.acknowledged ? t('tinaChat.acknowledged') : t('tinaChat.unacknowledged') }}</span></div><p v-if="row.message.reply_to_message_id" class="message-content delivery-id">{{ t('tinaChat.replyTo') }} {{ row.message.reply_to_message_id }}</p><p v-if="row.message.source_message_ids.length" class="message-content delivery-id">{{ t('tinaChat.sources') }} {{ row.message.source_message_ids.join('、') }}</p><p class="message-content delivery-id">{{ t('tinaChat.messageId') }} {{ row.message.id }}</p></div></details>
              </div>
            </article>
          </div>
          <div class="observation-footer"><span><Eye :size="13" />{{ t('tinaChat.readOnly') }}</span><UiButton v-if="!atBottom" variant="ghost" size="sm" @click="scrollToLatest"><ArrowDown :size="13" />{{ t('tinaChat.latest') }}</UiButton><time v-else-if="updatedAt">{{ error ? t('tinaChat.updatesPaused') : t('tinaChat.updated', { time: time(updatedAt.toISOString()) }) }}</time></div>
        </template>
        <div v-else class="chatroom-empty"><MessagesSquare :size="36" /><h2>{{ loadingMessages ? t('tinaChat.loading') : t('tinaChat.selectRoom') }}</h2><p>{{ error ? t('tinaChat.loadFailed') : t('tinaChat.selectRoomHint') }}</p><UiButton v-if="error" variant="outline" size="sm" @click="observer.refresh()">{{ t('tinaChat.retry') }}</UiButton></div>
      </section>
    </div>
  </section>
</template>

<style scoped>
.chatroom { height:100%; min-height:0; display:flex; flex-direction:column; color:var(--text-primary); container-type:inline-size; }
.chatroom-header { display:flex; flex-wrap:wrap; align-items:center; justify-content:space-between; gap:12px; padding:18px 22px; border-bottom:1px solid var(--border-muted); }
.chatroom-heading,.chatroom-actions { display:flex; align-items:center; gap:12px; }
.chatroom-heading > svg { color:var(--accent-primary); }
h1,h2,h3,p { margin:0; }
h1 { font-size:18px; font-weight:650; }
.chatroom-heading p { margin-top:3px; font-size:12px; color:var(--text-secondary); }
.observer-badge { display:inline-flex; align-items:center; gap:5px; padding:5px 8px; border-radius:6px; color:var(--accent-primary); background:var(--surface-active); font-size:11px; }
.refresh-toggle { display:flex; align-items:center; gap:6px; font-size:11px; color:var(--text-secondary); cursor:pointer; }
.refresh-toggle input { accent-color:var(--accent-primary); }
.chatroom-body { display:flex; flex:1; min-height:0; overflow:hidden; }
.room-directory { display:flex; flex-direction:column; width:280px; min-width:210px; flex-shrink:0; border-right:1px solid var(--border-muted); background:var(--surface-chrome); }
.directory-controls { padding:16px 12px 8px; }
.room-search { position:relative; }
.room-search > svg { position:absolute; top:11px; left:10px; z-index:1; color:var(--text-muted); pointer-events:none; }
.room-search :deep(input) { padding-left:30px; height:36px; font-size:12px; background:var(--surface-section); }
.room-tabs { display:flex; gap:3px; padding:3px; margin-top:12px; background:var(--surface-section); border-radius:8px; }
.room-tabs button { flex:1; border:0; border-radius:6px; padding:6px; background:transparent; color:var(--text-secondary); font-size:12px; cursor:pointer; transition:background .15s; }
.room-tabs button.active { background:var(--surface-active); color:var(--text-primary); }
.workspace-filter { margin-top:10px; width:100%; padding:6px; border:1px solid var(--border-muted); border-radius:6px; background:var(--surface-section); color:var(--text-secondary); font-size:12px; }
.directory-count { display:flex; justify-content:space-between; padding:12px 4px 2px; color:var(--text-muted); font-size:11px; }
.room-list { flex:1; min-height:0; overflow-y:auto; padding:4px 8px; }
.room-row { display:flex; gap:10px; text-align:left; width:100%; padding:12px 10px; border:1px solid transparent; border-radius:8px; background:transparent; color:inherit; cursor:pointer; margin-bottom:3px; }
.room-row:hover { background:var(--surface-hover); }
.room-row.selected { background:var(--surface-active); border-color:var(--border-muted); }
.room-avatar,.sender-avatar { display:flex; align-items:center; justify-content:center; width:34px; height:34px; border-radius:10px; flex-shrink:0; background:var(--surface-raised); color:var(--text-secondary); }
.room-row.selected .room-avatar { color:var(--accent-primary); }
.room-description { display:flex; flex:1; min-width:0; flex-direction:column; gap:5px; }
.room-title { display:flex; align-items:center; gap:6px; }
.room-title strong { font-size:13px; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; flex:1; }
.room-title time { font-size:10px; color:var(--text-muted); flex-shrink:0; }
.room-preview { font-size:12px; color:var(--text-secondary); overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
.room-meta { font-size:10px; color:var(--text-muted); }
.load-rooms { width:100%; }
.directory-foot { display:flex; align-items:center; gap:6px; padding:12px; font-size:10px; color:var(--text-muted); border-top:1px solid var(--border-muted); }
.room-conversation { flex:1; min-width:0; min-height:0; display:flex; flex-direction:column; }
.conversation-header { display:flex; align-items:center; padding:16px 22px; gap:10px; border-bottom:1px solid var(--border-muted); }
.conversation-title { min-width:0; flex:1; }
.conversation-title h2 { font-size:15px; font-weight:600; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.conversation-title p { font-size:11px; color:var(--text-muted); margin-top:5px; }
.room-members { max-height:210px; overflow-y:auto; border-bottom:1px solid var(--border-muted); padding:10px 22px; background:var(--surface-chrome); }
.room-members article { display:flex; align-items:center; gap:10px; padding:7px 0; font-size:12px; }
.room-members article > div { flex:1; min-width:0; overflow-wrap:anywhere; }
.room-members p,.room-members small,.room-members article > span { color:var(--text-muted); font-size:10px; }
.room-members small { user-select:text; }
.room-messages { flex:1; min-height:0; overflow-y:auto; overflow-x:hidden; padding:12px 24px 24px; scroll-behavior:auto; }
.history-action { display:flex; justify-content:center; padding:0 0 12px; }
.observed-message { display:flex; gap:12px; padding:14px 0; }
.sender-avatar { margin-top:2px; width:30px; height:30px; border-radius:9px; color:var(--accent-primary); }
.sender-avatar.human { color:var(--text-secondary); }
.message-main { flex:1; min-width:0; }
.message-byline { display:flex; flex-wrap:wrap; align-items:center; gap:7px; font-size:12px; }
.message-byline strong { font-weight:600; overflow-wrap:anywhere; }
.message-role,.message-byline time { color:var(--text-muted); font-size:10px; }
.message-byline time { margin-left:auto; }
.private-badge { display:flex; align-items:center; gap:3px; font-size:10px; color:var(--text-secondary); }
.message-direction { display:flex; align-items:baseline; gap:4px; margin:5px 0 8px; font-size:11px; color:var(--text-muted); overflow-wrap:anywhere; }
.message-direction svg { flex-shrink:0; }
.observed-content { white-space:pre-wrap; overflow-wrap:anywhere; font-size:13px; line-height:1.8; }
.intent-content { padding:12px 14px; border:1px solid var(--border-muted); border-radius:9px; background:var(--surface-raised); font-size:12px; line-height:1.7; overflow-wrap:anywhere; }
.intent-heading { display:flex; align-items:center; gap:6px; color:var(--accent-primary); margin-bottom:10px; }
.intent-heading span { margin-left:auto; font-size:10px; }
.intent-section { margin-top:9px; }
.intent-section h3 { font-size:10px; font-weight:500; color:var(--text-muted); }
.intent-section p { white-space:pre-wrap; }
.delivery-details { margin-top:9px; color:var(--text-muted); font-size:10px; }
.delivery-details summary { cursor:pointer; width:fit-content; padding:3px 0; }
.delivery-details summary > span { margin-left:12px; }
.delivery-body { border-left:2px solid var(--border-muted); padding:6px 10px; margin-top:4px; }
.delivery-hint { margin-bottom:8px; line-height:1.5; }
.delivery-row { display:flex; flex-wrap:wrap; align-items:center; gap:8px; padding:4px 0; }
.delivery-row strong { color:var(--text-secondary); font-weight:500; }
.delivery-row small { font-size:10px; color:var(--text-muted); }
.delivery-id { overflow-wrap:anywhere; margin-top:6px; }
.observation-footer { min-height:40px; padding:8px 20px; display:flex; align-items:center; justify-content:space-between; gap:8px; border-top:1px solid var(--border-muted); color:var(--text-muted); font-size:10px; }
.observation-footer > span { display:flex; gap:6px; align-items:center; }
.observation-footer time { flex-shrink:0; }
.chatroom-empty { display:flex; flex:1; flex-direction:column; align-items:center; justify-content:center; text-align:center; gap:12px; padding:30px; min-height:160px; color:var(--text-muted); }
.chatroom-empty h2 { font-size:15px; color:var(--text-secondary); }
.chatroom-empty p { max-width:320px; font-size:12px; line-height:1.7; }
.directory-empty { text-align:center; padding:40px 15px; color:var(--text-muted); font-size:12px; line-height:1.7; }
.directory-empty svg { margin:0 auto 12px; }
.directory-empty small { display:block; margin-top:8px; font-size:11px; }
.mobile-back { display:none; }
.spinning { animation:chatroom-spin 1s linear infinite; }
@keyframes chatroom-spin { to { transform:rotate(360deg); } }
@container (max-width:760px) { .room-directory { width:235px; } .chatroom-header { padding:14px; } .chatroom-heading p { display:none; } .observer-badge { display:none; } .room-messages { padding:12px 16px; } .message-role { display:none; } }
@container (max-width:560px) { .room-directory { width:100%; border-right:0; } .room-conversation { display:none; } .mobile-conversation .room-directory { display:none; } .mobile-conversation .room-conversation { display:flex; } .mobile-back { display:inline-flex; } .refresh-toggle { font-size:10px; } .conversation-header { padding:12px; } .observation-footer { flex-wrap:wrap; padding:8px 12px; } }
@media (prefers-reduced-motion:reduce) { .spinning { animation:none; } .room-tabs button { transition:none; } }
</style>
