<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Bell,
  Bot,
  Check,
  ChevronDown,
  ChevronRight,
  Circle,
  Clock3,
  Copy,
  FileText,
  Hash,
  Layers3,
  LayoutDashboard,
  ListTodo,
  MessageCircle,
  Minus,
  MoreHorizontal,
  Paperclip,
  Plus,
  Search,
  Send,
  Settings2,
  Sparkles,
  Square,
  Users,
  X,
} from '@lucide/vue'
import BrandLogo from '@/components/BrandLogo.vue'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { useTheme } from '@/composables/useTheme'
import { userProfile } from '@/lib/userProfile'

type AppArea = 'home' | 'messages' | 'work' | 'agents'
type MessageKind = 'message' | 'result' | 'decision' | 'system'
type HomeView = 'today' | 'updates' | 'space'
type ContextMode = 'activity' | 'files'

interface NavigationChat {
  id: string
  nameKey: string
  previewKey: string
  kind: 'group' | 'person' | 'agent' | 'channel'
  time: string
  badge?: string
  agentSlug?: string
}

interface Message {
  id: string
  senderKey: string
  avatar: string
  agent?: boolean
  pet?: string
  time: string
  bodyKey: string
  kind: MessageKind
  labelKey?: string
  linkedWork?: boolean
  body?: string
}

interface WorkRow {
  id: string
  titleKey: string
  ownerKey: string
  stateKey: string
  tone: 'neutral' | 'accent' | 'success' | 'warning'
}

interface FileRow {
  id: string
  titleKey: string
  detailKey: string
  kind: 'doc' | 'brief' | 'decision'
}

interface Conversation {
  nameKey: string
  descriptionKey: string
  membersKey: string
  messages: Message[]
  work: WorkRow[]
  files: FileRow[]
}

const { t } = useI18n()
const { theme, applyInitialTheme } = useTheme()

const appArea = ref<AppArea>('home')
const homeView = ref<HomeView>('today')
const activeChatId = ref('design-room')
const activeContext = ref<ContextMode>('activity')
const contextOpen = ref(false)
const searchQuery = ref('')
const draft = ref('')
const scroller = ref<HTMLElement | null>(null)
const composer = ref<HTMLTextAreaElement | null>(null)
const settingsOpen = ref(false)
const agentPickerOpen = ref(false)
const workFilter = ref<'all' | 'mine' | 'waiting'>('all')
const maximized = ref(false)

const navChats: NavigationChat[] = [
  {
    id: 'design-room',
    nameKey: 'denv.chats.designRoom',
    previewKey: 'denv.preview.design',
    kind: 'group',
    time: '10:22',
    badge: '2',
  },
  {
    id: 'release-room',
    nameKey: 'denv.chats.releaseRoom',
    previewKey: 'denv.preview.release',
    kind: 'group',
    time: '09:44',
    badge: '1',
  },
  {
    id: 'research-agent',
    nameKey: 'denv.agents.researchName',
    previewKey: 'denv.preview.research',
    kind: 'agent',
    time: '09:21',
    agentSlug: 'deepseek',
  },
  {
    id: 'ming-zhao',
    nameKey: 'denv.people.ming',
    previewKey: 'denv.preview.ming',
    kind: 'person',
    time: 'Yesterday',
  },
  {
    id: 'release-channel',
    nameKey: 'denv.chats.releaseChannel',
    previewKey: 'denv.preview.channel',
    kind: 'channel',
    time: 'Yesterday',
  },
]

const conversations: Record<string, Conversation> = {
  'design-room': {
    nameKey: 'denv.chats.designRoom',
    descriptionKey: 'denv.contexts.designRoom',
    membersKey: 'denv.members.designRoom',
    messages: [
      {
        id: 'd1',
        senderKey: 'denv.people.ming',
        avatar: 'MZ',
        time: '10:12',
        bodyKey: 'denv.messages.designConcern',
        kind: 'message',
      },
      {
        id: 'd2',
        senderKey: 'denv.agents.researchName',
        agent: true,
        pet: 'deepseek',
        avatar: 'R',
        time: '10:16',
        bodyKey: 'denv.messages.research',
        kind: 'result',
        labelKey: 'denv.labels.sourcedResult',
        linkedWork: true,
      },
      {
        id: 'd3',
        senderKey: 'denv.people.li',
        avatar: 'LC',
        time: '10:18',
        bodyKey: 'denv.messages.designDecision',
        kind: 'decision',
        labelKey: 'denv.labels.teamDecision',
      },
      {
        id: 'd4',
        senderKey: 'denv.agents.designName',
        agent: true,
        pet: 'geptia',
        avatar: 'D',
        time: '10:21',
        bodyKey: 'denv.messages.designPrototype',
        kind: 'result',
        labelKey: 'denv.labels.designProposal',
        linkedWork: true,
      },
      {
        id: 'd5',
        senderKey: 'denv.system',
        avatar: 'D',
        time: '10:22',
        bodyKey: 'denv.messages.linkedPrototypes',
        kind: 'system',
      },
    ],
    work: [
      {
        id: 'navigation',
        titleKey: 'denv.work.navigation',
        ownerKey: 'denv.people.li',
        stateKey: 'denv.states.waitingYou',
        tone: 'warning',
      },
      {
        id: 'prototype',
        titleKey: 'denv.work.prototype',
        ownerKey: 'denv.agents.designName',
        stateKey: 'denv.states.delivered',
        tone: 'success',
      },
      {
        id: 'sources',
        titleKey: 'denv.work.sources',
        ownerKey: 'denv.agents.researchName',
        stateKey: 'denv.states.ready',
        tone: 'accent',
      },
    ],
    files: [
      { id: 'brief', titleKey: 'denv.files.brief', detailKey: 'denv.files.briefDetail', kind: 'brief' },
      { id: 'decision', titleKey: 'denv.files.decision', detailKey: 'denv.files.decisionDetail', kind: 'decision' },
      { id: 'sources', titleKey: 'denv.files.sources', detailKey: 'denv.files.sourcesDetail', kind: 'doc' },
    ],
  },
  'release-room': {
    nameKey: 'denv.chats.releaseRoom',
    descriptionKey: 'denv.contexts.releaseRoom',
    membersKey: 'denv.members.releaseRoom',
    messages: [
      {
        id: 'r1',
        senderKey: 'denv.people.qin',
        avatar: 'QY',
        time: '09:40',
        bodyKey: 'denv.messages.releaseMissing',
        kind: 'message',
      },
      {
        id: 'r2',
        senderKey: 'denv.system',
        avatar: 'D',
        time: '09:42',
        bodyKey: 'denv.messages.releaseStopped',
        kind: 'system',
      },
      {
        id: 'r3',
        senderKey: 'denv.people.qin',
        avatar: 'QY',
        time: '09:44',
        bodyKey: 'denv.messages.releaseNext',
        kind: 'decision',
        labelKey: 'denv.labels.safetyDecision',
      },
    ],
    work: [
      {
        id: 'release-check',
        titleKey: 'denv.work.releaseCheck',
        ownerKey: 'denv.agents.releaseName',
        stateKey: 'denv.states.connectionPending',
        tone: 'warning',
      },
    ],
    files: [
      { id: 'release-notes', titleKey: 'denv.files.releaseNotes', detailKey: 'denv.files.releaseNotesDetail', kind: 'doc' },
    ],
  },
  'research-agent': {
    nameKey: 'denv.agents.researchName',
    descriptionKey: 'denv.contexts.researchAgent',
    membersKey: 'denv.members.privateAgent',
    messages: [
      {
        id: 'a1',
        senderKey: 'denv.people.you',
        avatar: 'ME',
        time: '09:20',
        bodyKey: 'denv.messages.privateAsk',
        kind: 'message',
      },
      {
        id: 'a2',
        senderKey: 'denv.agents.researchName',
        agent: true,
        pet: 'deepseek',
        avatar: 'R',
        time: '09:21',
        bodyKey: 'denv.messages.privateAnswer',
        kind: 'result',
        labelKey: 'denv.labels.privateSuggestion',
      },
    ],
    work: [],
    files: [],
  },
  'ming-zhao': {
    nameKey: 'denv.people.ming',
    descriptionKey: 'denv.contexts.directMessage',
    membersKey: 'denv.members.directMessage',
    messages: [
      {
        id: 'p1',
        senderKey: 'denv.people.ming',
        avatar: 'MZ',
        time: 'Yesterday',
        bodyKey: 'denv.messages.mingDirect',
        kind: 'message',
      },
      {
        id: 'p2',
        senderKey: 'denv.people.you',
        avatar: 'ME',
        time: 'Yesterday',
        bodyKey: 'denv.messages.mingDirectReply',
        kind: 'message',
      },
    ],
    work: [],
    files: [],
  },
  'release-channel': {
    nameKey: 'denv.chats.releaseChannel',
    descriptionKey: 'denv.contexts.releaseChannel',
    membersKey: 'denv.members.releaseRoom',
    messages: [
      {
        id: 'c1',
        senderKey: 'denv.system',
        avatar: 'D',
        time: 'Yesterday',
        bodyKey: 'denv.messages.channelAnnouncement',
        kind: 'system',
      },
    ],
    work: [
      {
        id: 'release-notes',
        titleKey: 'denv.work.releaseNotes',
        ownerKey: 'denv.people.ning',
        stateKey: 'denv.states.inProgress',
        tone: 'accent',
      },
    ],
    files: [
      { id: 'announcement', titleKey: 'denv.files.announcement', detailKey: 'denv.files.announcementDetail', kind: 'brief' },
    ],
  },
}

const currentChat = computed(() => conversations[activeChatId.value] ?? conversations['design-room'])
const currentChatMeta = computed(() => navChats.find((chat) => chat.id === activeChatId.value) ?? navChats[0])
const filteredChats = computed(() => {
  const query = searchQuery.value.trim().toLocaleLowerCase()
  if (!query) return navChats
  return navChats.filter((chat) => {
    const preview = t(chat.previewKey)
    return t(chat.nameKey).toLocaleLowerCase().includes(query) || preview.toLocaleLowerCase().includes(query)
  })
})
const unresolvedWork = computed(() => currentChat.value.work.filter((work) => work.tone === 'warning'))
const visibleWork = computed(() => {
  if (appArea.value !== 'work') return currentChat.value.work
  if (workFilter.value === 'mine') return currentChat.value.work.filter((work) => work.ownerKey === 'denv.people.you')
  if (workFilter.value === 'waiting') return currentChat.value.work.filter((work) => work.tone === 'warning')
  return currentChat.value.work
})
const userInitial = computed(() => userProfile.nickname.trim().slice(0, 2).toUpperCase() || 'ME')

function openMessages(chatId?: string) {
  if (chatId) activeChatId.value = chatId
  appArea.value = 'messages'
  contextOpen.value = false
  settingsOpen.value = false
  agentPickerOpen.value = false
  void nextTick(() => scrollLatest())
}

function openHome(view: HomeView = 'today') {
  homeView.value = view
  appArea.value = 'home'
  contextOpen.value = false
  settingsOpen.value = false
}

function openWork() {
  appArea.value = 'work'
  contextOpen.value = false
  settingsOpen.value = false
}

function openAgents() {
  appArea.value = 'agents'
  contextOpen.value = false
  settingsOpen.value = false
}

function toggleContext(mode: ContextMode) {
  if (contextOpen.value && activeContext.value === mode) {
    contextOpen.value = false
    return
  }
  activeContext.value = mode
  contextOpen.value = true
}

function scrollLatest(behavior: ScrollBehavior = 'auto') {
  if (!scroller.value) return
  scroller.value.scrollTo({ top: scroller.value.scrollHeight, behavior })
}

function submit() {
  const body = draft.value.trim()
  if (!body) return
  currentChat.value.messages.push({
    id: `local-${Date.now()}`,
    senderKey: 'denv.people.you',
    avatar: userInitial.value,
    time: t('denv.labels.now'),
    bodyKey: '',
    body,
    kind: 'message',
  })
  draft.value = ''
  void nextTick(() => {
    scrollLatest('smooth')
    composer.value?.focus()
    resizeComposer()
  })
}

function messageBody(message: Message) {
  return message.body ?? t(message.bodyKey)
}

function composerKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault()
    submit()
  }
}

function resizeComposer() {
  const element = composer.value
  if (!element) return
  element.style.height = 'auto'
  element.style.height = `${Math.min(element.scrollHeight, 124)}px`
}

function syncMaximized() {
  maximized.value = window.outerWidth >= window.screen.availWidth - 4
    && window.outerHeight >= window.screen.availHeight - 4
}

function minimizeWindow() {
  window.tinadec?.minimizeWindow?.()
}

function maximizeWindow() {
  window.tinadec?.maximizeWindow?.()
}

function closeWindow() {
  window.tinadec?.closeWindow?.()
}

onMounted(() => {
  applyInitialTheme()
  syncMaximized()
  window.addEventListener('resize', syncMaximized)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', syncMaximized)
})
</script>

<template>
  <main class="denv-app">
    <header class="denv-titlebar">
      <div class="titlebar-drag" />
      <div class="denv-brand"><BrandLogo :size="15" /><span>Denv</span></div>
      <button type="button" class="titlebar-search" :aria-label="t('denv.actions.globalSearch')">
        <Search :size="14" />
        <span>{{ t('denv.actions.globalSearch') }}</span>
        <kbd>Ctrl K</kbd>
      </button>
      <div class="titlebar-controls">
        <button type="button" :aria-label="t('app.minimize')" :title="t('app.minimize')" @click="minimizeWindow"><Minus :size="14" /></button>
        <button type="button" :aria-label="maximized ? t('app.restore') : t('app.maximize')" :title="maximized ? t('app.restore') : t('app.maximize')" @click="maximizeWindow"><Copy v-if="maximized" :size="11" /><Square v-else :size="12" /></button>
        <button type="button" class="close" :aria-label="t('app.close')" :title="t('app.close')" @click="closeWindow"><X :size="14" /></button>
      </div>
    </header>

    <div class="denv-workspace" :class="{ 'context-open': contextOpen && appArea === 'messages' }">
      <aside class="app-rail" :aria-label="t('denv.nav.primary')">
        <button type="button" class="profile-button" :aria-label="t('denv.nav.profile')" :title="t('denv.nav.profile')">
          <img v-if="userProfile.avatar" :src="userProfile.avatar" alt="">
          <span v-else>{{ userInitial }}</span>
          <i />
        </button>
        <nav class="app-nav">
          <button type="button" :class="{ active: appArea === 'home' }" :aria-label="t('denv.nav.home')" :title="t('denv.nav.home')" @click="openHome('today')">
            <LayoutDashboard :size="18" /><span>{{ t('denv.nav.home') }}</span>
          </button>
          <button type="button" :class="{ active: appArea === 'messages' }" :aria-label="t('denv.nav.messages')" :title="t('denv.nav.messages')" @click="openMessages()">
            <MessageCircle :size="18" /><span>{{ t('denv.nav.messages') }}</span><i class="nav-badge">3</i>
          </button>
          <button type="button" :class="{ active: appArea === 'work' }" :aria-label="t('denv.nav.work')" :title="t('denv.nav.work')" @click="openWork()">
            <ListTodo :size="18" /><span>{{ t('denv.nav.work') }}</span>
          </button>
          <button type="button" :class="{ active: appArea === 'agents' }" :aria-label="t('denv.nav.agents')" :title="t('denv.nav.agents')" @click="openAgents()">
            <Bot :size="18" /><span>{{ t('denv.nav.agents') }}</span>
          </button>
        </nav>
        <div class="app-rail-bottom">
          <button type="button" :aria-label="t('denv.nav.notifications')" :title="t('denv.nav.notifications')"><Bell :size="17" /><i class="notification-dot" /></button>
          <button type="button" :class="{ active: settingsOpen }" :aria-label="t('denv.nav.settings')" :title="t('denv.nav.settings')" @click="settingsOpen = !settingsOpen"><Settings2 :size="17" /></button>
        </div>
      </aside>

      <aside class="denv-navigation" :aria-label="t('denv.nav.context')">
        <div class="navigation-header">
          <div class="organization-name">
            <strong>{{ t('denv.org.name') }}</strong>
            <span>{{ t('denv.org.detail') }}</span>
          </div>
          <button type="button" class="bare-icon" :aria-label="t('denv.actions.compose')" :title="t('denv.actions.compose')"><Plus :size="17" /></button>
        </div>
        <label class="navigation-search">
          <Search :size="14" />
          <input v-model="searchQuery" :placeholder="t('denv.actions.searchShort')" :aria-label="t('denv.actions.searchShort')">
          <kbd>Ctrl K</kbd>
        </label>

        <nav class="navigation-scroll">
          <section class="nav-section">
            <div class="nav-section-title">{{ t('denv.nav.today') }}</div>
            <button type="button" :class="{ active: appArea === 'home' && homeView === 'today' }" @click="openHome('today')">
              <LayoutDashboard :size="15" /><span>{{ t('denv.nav.myDay') }}</span><i class="count">3</i>
            </button>
            <button type="button" :class="{ active: appArea === 'home' && homeView === 'updates' }" @click="openHome('updates')">
              <Clock3 :size="15" /><span>{{ t('denv.nav.updates') }}</span><i class="count">8</i>
            </button>
            <button type="button" @click="openWork">
              <Check :size="15" /><span>{{ t('denv.nav.requests') }}</span><i class="count">2</i>
            </button>
          </section>

          <section class="nav-section">
            <div class="nav-section-title">
              <span>{{ t('denv.nav.conversations') }}</span>
              <button type="button" class="bare-icon" :aria-label="t('denv.actions.newChat')"><Plus :size="13" /></button>
            </div>
            <button
              v-for="chat in filteredChats"
              :key="chat.id"
              type="button"
              class="chat-nav-row"
              :class="{ active: appArea === 'messages' && activeChatId === chat.id }"
              @click="openMessages(chat.id)"
            >
              <AgentPetAvatar v-if="chat.kind === 'agent'" :pet-slug="chat.agentSlug || 'deepseek'" :size="22" alt="" />
              <span v-else-if="chat.kind === 'channel'" class="nav-glyph"><Hash :size="14" /></span>
              <span v-else class="nav-avatar" :class="{ team: chat.kind === 'group' }">{{ chat.nameKey === 'denv.people.ming' ? 'MZ' : 'PR' }}</span>
              <span class="chat-nav-copy">
                <strong>{{ t(chat.nameKey) }}</strong>
                <small>{{ t(chat.previewKey) }}</small>
              </span>
              <span class="chat-nav-tail">
                <time>{{ chat.time }}</time>
                <i v-if="chat.badge" class="count">{{ chat.badge }}</i>
              </span>
            </button>
            <button type="button" class="all-conversations">
              <span>{{ t('denv.actions.allConversations') }}</span><ChevronRight :size="13" />
            </button>
          </section>

          <section class="nav-section">
            <div class="nav-section-title">{{ t('denv.nav.spaces') }}</div>
            <button type="button" @click="openHome('space')">
              <span class="space-glyph">D</span><span>{{ t('denv.spaces.design') }}</span>
            </button>
            <button type="button">
              <span class="space-glyph secondary">C</span><span>{{ t('denv.spaces.client') }}</span>
            </button>
          </section>
        </nav>

        <div class="navigation-footer">
          <button type="button" @click="settingsOpen = true"><Settings2 :size="14" /><span>{{ t('denv.nav.settings') }}</span></button>
          <button type="button"><FileText :size="14" /><span>{{ t('denv.nav.help') }}</span></button>
        </div>
      </aside>

      <section class="primary-surface">
        <template v-if="appArea === 'home'">
          <header class="surface-header home-head">
            <div>
              <span class="eyebrow">{{ homeView === 'space' ? t('denv.labels.collaborationSpace') : t('denv.labels.officeHome') }}</span>
              <h1>{{ homeView === 'space' ? t('denv.spaces.design') : homeView === 'updates' ? t('denv.home.updatesTitle') : t('denv.home.todayTitle') }}</h1>
              <p>{{ homeView === 'space' ? t('denv.spaces.designDetail') : homeView === 'updates' ? t('denv.home.updatesContext') : t('denv.home.todayContext') }}</p>
            </div>
            <button type="button" class="primary-soft-button" @click="openMessages('design-room')">
              <MessageCircle :size="14" />{{ t('denv.actions.startConversation') }}
            </button>
          </header>

          <div class="home-scroll">
            <template v-if="homeView === 'today'">
              <section class="home-section">
                <div class="section-heading">
                  <div><span class="eyebrow">{{ t('denv.home.needsYou') }}</span><h2>{{ t('denv.home.priorityTitle') }}</h2></div>
                  <button type="button">{{ t('denv.home.openInbox') }}<ChevronRight :size="13" /></button>
                </div>
                <div class="needs-you-grid">
                  <button type="button" class="need-card accent" @click="openMessages('design-room')">
                    <div class="need-card-top"><span class="status-mark warning"><Circle :size="8" fill="currentColor" /></span><span>{{ t('denv.home.decisionWaiting') }}</span><time>10m</time></div>
                    <h3>{{ t('denv.home.navigationDecision') }}</h3>
                    <p>{{ t('denv.home.navigationDecisionDetail') }}</p>
                    <div class="need-card-bottom"><span class="mini-faces"><i>LC</i><i>MZ</i><i class="agent">R</i></span><strong>{{ t('denv.home.reviewDecision') }}<ChevronRight :size="12" /></strong></div>
                  </button>
                  <button type="button" class="need-card" @click="openMessages('release-room')">
                    <div class="need-card-top"><span class="status-mark danger"><Circle :size="8" fill="currentColor" /></span><span>{{ t('denv.home.connectionWaiting') }}</span><time>20m</time></div>
                    <h3>{{ t('denv.home.releaseStopped') }}</h3>
                    <p>{{ t('denv.home.releaseStoppedDetail') }}</p>
                    <div class="need-card-bottom"><span class="subtle-chip">{{ t('denv.home.noRetry') }}</span><strong>{{ t('denv.home.openSituation') }}<ChevronRight :size="12" /></strong></div>
                  </button>
                </div>
              </section>

              <section class="home-section">
                <div class="section-heading">
                  <div><span class="eyebrow">{{ t('denv.home.pickUp') }}</span><h2>{{ t('denv.home.recentConversations') }}</h2></div>
                  <button type="button" @click="openMessages()">{{ t('denv.home.viewAll') }}<ChevronRight :size="13" /></button>
                </div>
                <div class="home-conversation-list">
                  <button type="button" class="home-conversation-row" @click="openMessages('design-room')">
                    <span class="conversation-icon team">#</span>
                    <span class="home-conversation-copy"><strong>{{ t('denv.chats.designRoom') }}</strong><small>{{ t('denv.preview.design') }}</small></span>
                    <span class="home-conversation-meta"><span>{{ t('denv.home.designRoomMeta') }}</span><time>10:22</time><i class="unread-count">2</i></span>
                  </button>
                  <button type="button" class="home-conversation-row" @click="openMessages('release-room')">
                    <span class="conversation-icon team">#</span>
                    <span class="home-conversation-copy"><strong>{{ t('denv.chats.releaseRoom') }}</strong><small>{{ t('denv.preview.release') }}</small></span>
                    <span class="home-conversation-meta"><span>{{ t('denv.home.releaseRoomMeta') }}</span><time>09:44</time><i class="attention-dot" /></span>
                  </button>
                  <button type="button" class="home-conversation-row" @click="openMessages('research-agent')">
                    <AgentPetAvatar pet-slug="deepseek" :size="26" alt="" />
                    <span class="home-conversation-copy"><strong>{{ t('denv.agents.researchName') }}</strong><small>{{ t('denv.preview.research') }}</small></span>
                    <span class="home-conversation-meta"><span>{{ t('denv.home.privateMeta') }}</span><time>09:21</time></span>
                  </button>
                </div>
              </section>

              <div class="home-columns">
                <section class="home-compact-section">
                  <div class="section-heading"><div><span class="eyebrow">{{ t('denv.home.work') }}</span><h2>{{ t('denv.home.workInMotion') }}</h2></div><button type="button" @click="openWork()">{{ t('denv.home.viewWork') }}</button></div>
                  <button type="button" class="compact-work-row" @click="openMessages('design-room')">
                    <span class="work-check" /><span><strong>{{ t('denv.work.navigation') }}</strong><small>{{ t('denv.home.navigationWorkMeta') }}</small></span><span class="state-chip warning">{{ t('denv.states.waitingYou') }}</span>
                  </button>
                  <button type="button" class="compact-work-row" @click="openMessages('release-room')">
                    <span class="work-check" /><span><strong>{{ t('denv.work.releaseCheck') }}</strong><small>{{ t('denv.home.releaseWorkMeta') }}</small></span><span class="state-chip warning">{{ t('denv.states.connectionPending') }}</span>
                  </button>
                  <button type="button" class="compact-work-row" @click="openMessages('design-room')">
                    <span class="work-check done"><Check :size="10" /></span><span><strong>{{ t('denv.work.prototype') }}</strong><small>{{ t('denv.home.prototypeWorkMeta') }}</small></span><span class="state-chip success">{{ t('denv.states.delivered') }}</span>
                  </button>
                </section>
                <section class="home-compact-section">
                  <div class="section-heading"><div><span class="eyebrow">{{ t('denv.home.peopleAndAgents') }}</span><h2>{{ t('denv.home.aroundYou') }}</h2></div></div>
                    <div class="person-row">
                    <span class="person-avatar">LC<i /></span><span><strong>{{ t('denv.people.li') }}</strong><small>{{ t('denv.home.liStatus') }}</small></span><button type="button" class="row-action" :aria-label="t('denv.actions.messagePerson')"><MessageCircle :size="14" /></button>
                    </div>
                    <div class="person-row">
                    <span class="person-avatar">MZ<i /></span><span><strong>{{ t('denv.people.ming') }}</strong><small>{{ t('denv.home.mingStatus') }}</small></span><button type="button" class="row-action" :aria-label="t('denv.actions.messagePerson')"><MessageCircle :size="14" /></button>
                    </div>
                    <div class="person-row">
                      <AgentPetAvatar pet-slug="deepseek" :size="27" alt="" /><span><strong>{{ t('denv.agents.researchName') }}</strong><small>{{ t('denv.home.researchStatus') }}</small></span><button type="button" class="row-action" :aria-label="t('denv.actions.messageAgent')" @click="openMessages('research-agent')"><MessageCircle :size="14" /></button>
                    </div>
                </section>
              </div>
            </template>

            <template v-else-if="homeView === 'updates'">
              <section class="updates-list">
                <button type="button" class="update-row" @click="openMessages('design-room')"><span class="update-icon agent"><Sparkles :size="14" /></span><span><strong>{{ t('denv.updates.researchDelivered') }}</strong><small>{{ t('denv.updates.researchDeliveredDetail') }}</small></span><time>10:22</time></button>
                <button type="button" class="update-row" @click="openMessages('release-room')"><span class="update-icon warning"><Clock3 :size="14" /></span><span><strong>{{ t('denv.updates.releasePaused') }}</strong><small>{{ t('denv.updates.releasePausedDetail') }}</small></span><time>09:44</time></button>
                <button type="button" class="update-row" @click="openMessages('ming-zhao')"><span class="update-icon">MZ</span><span><strong>{{ t('denv.updates.mingReply') }}</strong><small>{{ t('denv.updates.mingReplyDetail') }}</small></span><time>Yesterday</time></button>
              </section>
              <div class="update-digest">
                <section class="update-digest-card">
                  <div class="section-heading"><div><span class="eyebrow">{{ t('denv.updates.overview') }}</span><h2>{{ t('denv.updates.todayOverview') }}</h2></div></div>
                  <div class="digest-row"><span class="digest-number">2</span><span><strong>{{ t('denv.updates.agentDeliveries') }}</strong><small>{{ t('denv.updates.agentDeliveriesDetail') }}</small></span></div>
                  <div class="digest-row"><span class="digest-number">3</span><span><strong>{{ t('denv.updates.linkedWork') }}</strong><small>{{ t('denv.updates.linkedWorkDetail') }}</small></span></div>
                  <div class="digest-row"><span class="digest-number">1</span><span><strong>{{ t('denv.updates.review') }}</strong><small>{{ t('denv.updates.reviewDetail') }}</small></span></div>
                </section>
                <section class="update-digest-card">
                  <div class="section-heading"><div><span class="eyebrow">{{ t('denv.updates.teamContext') }}</span><h2>{{ t('denv.updates.recentDecisions') }}</h2></div></div>
                  <button type="button" class="decision-row" @click="openMessages('design-room')"><span class="decision-icon"><ListTodo :size="14" /></span><span><strong>{{ t('denv.updates.navigationDecision') }}</strong><small>{{ t('denv.updates.navigationDecisionDetail') }}</small></span><ChevronRight :size="13" /></button>
                  <button type="button" class="decision-row" @click="openMessages('release-room')"><span class="decision-icon warning"><Check :size="14" /></span><span><strong>{{ t('denv.updates.releaseDecision') }}</strong><small>{{ t('denv.updates.releaseDecisionDetail') }}</small></span><ChevronRight :size="13" /></button>
                </section>
              </div>
            </template>

            <template v-else>
              <section class="space-summary">
                <span class="space-large-glyph">D</span>
                <div><h2>{{ t('denv.spaces.design') }}</h2><p>{{ t('denv.spaces.designDetail') }}</p><span class="space-members"><i>LC</i><i>MZ</i><i class="agent">R</i><i class="agent">D</i><em>{{ t('denv.space.memberCount') }}</em></span></div>
              </section>
              <div class="space-tabs" role="tablist">
                <button type="button" class="active">{{ t('denv.space.overview') }}</button>
                <button type="button">{{ t('denv.space.conversations') }}</button>
                <button type="button">{{ t('denv.space.projects') }}<i>3</i></button>
                <button type="button">{{ t('denv.space.knowledge') }}</button>
                <button type="button">{{ t('denv.space.agents') }}</button>
              </div>
              <div class="space-overview-grid">
                <section class="space-panel wide">
                  <div class="section-heading"><div><span class="eyebrow">{{ t('denv.space.project') }}</span><h2>{{ t('denv.space.activeProject') }}</h2></div><button type="button">{{ t('denv.space.openProject') }}</button></div>
                  <p class="space-project-copy">{{ t('denv.space.activeProjectDetail') }}</p>
                  <div class="space-project-meta"><span>{{ t('denv.space.owner') }}</span><b>{{ t('denv.people.li') }}</b><span>{{ t('denv.space.nextReview') }}</span><b>{{ t('denv.space.todayThree') }}</b><span>{{ t('denv.space.progress') }}</span><b>{{ t('denv.space.progressValue') }}</b></div>
                  <div class="space-progress"><i /></div>
                  <div class="space-work-row"><span class="work-check" /><span><strong>{{ t('denv.work.navigation') }}</strong><small>{{ t('denv.states.waitingYou') }}</small></span><ChevronRight :size="13" /></div>
                  <div class="space-work-row"><span class="work-check" /><span><strong>{{ t('denv.work.releaseCheck') }}</strong><small>{{ t('denv.states.connectionPending') }}</small></span><ChevronRight :size="13" /></div>
                </section>
                <section class="space-panel">
                  <div class="section-heading"><h2>{{ t('denv.space.teamPulse') }}</h2></div>
                  <div class="pulse-row"><span class="person-avatar">LC<i /></span><span><b>{{ t('denv.people.li') }}</b><small>{{ t('denv.home.liStatus') }}</small></span></div>
                  <div class="pulse-row"><span class="person-avatar">MZ<i /></span><span><b>{{ t('denv.people.ming') }}</b><small>{{ t('denv.home.mingStatus') }}</small></span></div>
                  <div class="pulse-row"><AgentPetAvatar pet-slug="deepseek" :size="25" alt="" /><span><b>{{ t('denv.agents.researchName') }}</b><small>{{ t('denv.home.researchStatus') }}</small></span></div>
                </section>
                <section class="space-panel">
                  <div class="section-heading"><h2>{{ t('denv.space.teamKnowledge') }}</h2></div>
                  <button type="button" class="knowledge-row"><FileText :size="14" /><span><b>{{ t('denv.files.brief') }}</b><small>{{ t('denv.files.briefDetail') }}</small></span></button>
                  <button type="button" class="knowledge-row"><ListTodo :size="14" /><span><b>{{ t('denv.files.decision') }}</b><small>{{ t('denv.files.decisionDetail') }}</small></span></button>
                </section>
              </div>
            </template>
          </div>
        </template>

        <template v-else-if="appArea === 'messages'">
          <header class="message-header">
            <div class="message-heading">
              <div class="message-title-row">
                <span class="conversation-icon" :class="{ team: currentChatMeta.kind === 'group', agent: currentChatMeta.kind === 'agent' }">
                  <AgentPetAvatar v-if="currentChatMeta.kind === 'agent'" :pet-slug="currentChatMeta.agentSlug || 'deepseek'" :size="22" alt="" />
                  <Hash v-else-if="currentChatMeta.kind === 'channel'" :size="14" />
                  <span v-else>{{ currentChatMeta.kind === 'group' ? 'PR' : 'MZ' }}</span>
                </span>
                <h1>{{ t(currentChatMeta.nameKey) }}</h1>
                <ChevronDown :size="13" />
                <span class="header-members">{{ t(currentChat.membersKey) }}</span>
              </div>
              <p>{{ t(currentChat.descriptionKey) }}</p>
            </div>
            <button type="button" class="message-context-button agent-button" @click="agentPickerOpen = !agentPickerOpen"><Sparkles :size="14" /><span>{{ t('denv.actions.bringInAgent') }}</span></button>
            <button type="button" class="message-context-button" :class="{ active: contextOpen && activeContext === 'files' }" @click="toggleContext('files')"><FileText :size="14" /><span>{{ t('denv.context.files') }}</span></button>
            <button type="button" class="message-context-button" :class="{ active: contextOpen && activeContext === 'activity' }" @click="toggleContext('activity')"><Users :size="14" /><span>{{ t('denv.context.details') }}</span></button>
            <button type="button" class="bare-icon"><Search :size="15" /></button>
            <button type="button" class="bare-icon"><MoreHorizontal :size="17" /></button>
            <div v-if="agentPickerOpen" class="agent-picker">
              <div class="agent-picker-head">{{ t('denv.agents.inviteTitle') }}</div>
              <button type="button" @click="agentPickerOpen = false; openMessages('research-agent')"><AgentPetAvatar pet-slug="deepseek" :size="25" alt="" /><span><strong>{{ t('denv.agents.researchName') }}</strong><small>{{ t('denv.agents.researchSummary') }}</small></span></button>
              <button type="button" @click="agentPickerOpen = false; openMessages('design-room')"><AgentPetAvatar pet-slug="geptia" :size="25" alt="" /><span><strong>{{ t('denv.agents.designName') }}</strong><small>{{ t('denv.agents.designSummary') }}</small></span></button>
            </div>
          </header>

          <div class="message-tabs" role="tablist">
            <button type="button" class="active">{{ t('denv.tabs.conversation') }}</button>
            <button type="button" @click="openWork">{{ t('denv.tabs.work') }}<i>{{ currentChat.work.length }}</i></button>
            <button type="button" @click="toggleContext('files')">{{ t('denv.tabs.files') }}</button>
            <button type="button">{{ t('denv.tabs.knowledge') }}</button>
            <button type="button">{{ t('denv.tabs.pinned') }}</button>
          </div>

          <div ref="scroller" class="message-stream">
            <div class="day-divider"><span>{{ t('denv.labels.today') }}</span></div>
            <article
              v-for="message in currentChat.messages"
              :key="message.id"
              class="message-item"
              :class="{ agent: message.agent, system: message.kind === 'system' }"
            >
              <template v-if="message.kind === 'system'">
                <div class="system-row"><span /><span class="system-icon"><Sparkles :size="12" /></span><span>{{ t(message.bodyKey) }}</span><span /></div>
              </template>
              <template v-else>
                <div class="message-avatar" :class="{ agent: message.agent }">
                  <AgentPetAvatar v-if="message.agent" :pet-slug="message.pet || 'deepseek'" :size="31" alt="" />
                  <span v-else>{{ message.avatar }}</span>
                </div>
                <div class="message-body">
                  <header class="message-meta">
                    <strong>{{ t(message.senderKey) }}</strong>
                    <span v-if="message.kind === 'decision'" class="role-tag">{{ t('denv.labels.decisionOwner') }}</span>
                    <span v-else-if="message.agent" class="agent-tag">Agent</span>
                    <time>{{ message.time }}</time>
                  </header>
                  <p v-if="message.kind === 'message'">{{ messageBody(message) }}</p>
                  <div v-else class="result-block" :class="message.kind">
                    <span class="result-label"><Sparkles v-if="message.kind === 'result'" :size="12" /><ListTodo v-else :size="12" />{{ t(message.labelKey ?? 'denv.labels.result') }}</span>
                    <p>{{ messageBody(message) }}</p>
                    <div v-if="message.linkedWork" class="linked-work">
                      <span class="linked-work-icon"><FileText :size="13" /></span>
                      <span><strong>{{ t('denv.work.navigation') }}</strong><small>{{ message.kind === 'result' ? t('denv.labels.sourcesReady') : t('denv.labels.prototypeReady') }}</small></span>
                      <button type="button" @click="toggleContext('files')">{{ t('denv.actions.open') }}</button>
                    </div>
                  </div>
                </div>
              </template>
            </article>
            <div class="agent-presence"><AgentPetAvatar pet-slug="deepseek" :size="19" alt="" /><span>{{ t('denv.messages.agentPresence') }}</span><i /><i /><i /></div>
          </div>

          <footer class="message-composer-bar">
            <div class="message-composer">
              <textarea ref="composer" v-model="draft" :placeholder="t('denv.composer.placeholder')" :aria-label="t('denv.composer.placeholder')" @input="resizeComposer" @keydown="composerKeydown" />
              <div class="composer-toolbar">
                <div><button type="button" class="composer-icon" :aria-label="t('denv.actions.attach')"><Paperclip :size="15" /></button><button type="button" class="bring-agent" @click="agentPickerOpen = !agentPickerOpen"><Sparkles :size="13" />{{ t('denv.actions.bringInAgent') }}</button></div>
                <div><span class="composer-hint">{{ t('denv.composer.enterHint') }}</span><button type="button" class="send-button" :disabled="!draft.trim()" :aria-label="t('denv.actions.send')" @click="submit"><Send :size="14" /></button></div>
              </div>
            </div>
          </footer>
        </template>

        <template v-else-if="appArea === 'work'">
          <header class="surface-header">
            <div><span class="eyebrow">{{ t('denv.labels.workCenter') }}</span><h1>{{ t('denv.work.title') }}</h1><p>{{ t('denv.work.context') }}</p></div>
            <button type="button" class="primary-soft-button"><Plus :size="14" />{{ t('denv.actions.newWork') }}</button>
          </header>
          <div class="work-toolbar">
            <div class="work-filters"><button type="button" :class="{ active: workFilter === 'all' }" @click="workFilter = 'all'">{{ t('denv.work.all') }}</button><button type="button" :class="{ active: workFilter === 'mine' }" @click="workFilter = 'mine'">{{ t('denv.work.mine') }}</button><button type="button" :class="{ active: workFilter === 'waiting' }" @click="workFilter = 'waiting'">{{ t('denv.work.waiting') }}</button></div>
            <button type="button" class="view-selector"><ListTodo :size="13" />{{ t('denv.work.listView') }}<ChevronDown :size="12" /></button>
          </div>
          <div class="work-table surface-scroll">
            <div class="work-table-head"><span>{{ t('denv.work.workItem') }}</span><span>{{ t('denv.work.owner') }}</span><span>{{ t('denv.work.state') }}</span><span>{{ t('denv.work.source') }}</span><span>{{ t('denv.work.updated') }}</span></div>
            <button v-for="work in visibleWork" :key="work.id" type="button" class="work-table-row" @click="openMessages('design-room')">
              <span class="work-title-cell"><i class="work-type"><ListTodo :size="13" /></i><span><strong>{{ t(work.titleKey) }}</strong><small>{{ t('denv.work.linkedConversation') }}</small></span></span>
              <span class="owner-cell">{{ t(work.ownerKey) }}</span>
              <span><i class="state-pill" :class="work.tone">{{ t(work.stateKey) }}</i></span>
              <span class="source-cell"><Hash :size="11" />{{ t(currentChatMeta.nameKey) }}</span>
              <time>10m</time>
            </button>
            <div v-if="!visibleWork.length" class="empty-work">{{ t('denv.work.empty') }}</div>
          </div>
        </template>

        <template v-else>
          <header class="surface-header">
            <div><span class="eyebrow">{{ t('denv.labels.agentDirectory') }}</span><h1>{{ t('denv.agents.title') }}</h1><p>{{ t('denv.agents.context') }}</p></div>
            <button type="button" class="primary-soft-button"><Plus :size="14" />{{ t('denv.actions.addAgent') }}</button>
          </header>
          <div class="agents-scroll surface-scroll">
            <section class="agent-directory-section">
              <div class="section-heading"><div><span class="eyebrow">{{ t('denv.agents.yours') }}</span><h2>{{ t('denv.agents.personal') }}</h2></div></div>
              <div class="agent-card-grid">
                <button type="button" class="agent-card" @click="openMessages('research-agent')">
                  <div class="agent-card-top"><AgentPetAvatar pet-slug="deepseek" :size="49" alt="" /><span class="agent-status"><i />{{ t('denv.agents.working') }}</span></div>
                  <h3>{{ t('denv.agents.researchName') }}</h3><p>{{ t('denv.agents.researchSummary') }}</p>
                  <div class="agent-card-footer"><span>{{ t('denv.agents.privateWithYou') }}</span><span>{{ t('denv.actions.message') }}<ChevronRight :size="12" /></span></div>
                </button>
                <button type="button" class="agent-card" @click="openMessages('design-room')">
                  <div class="agent-card-top"><AgentPetAvatar pet-slug="geptia" :size="49" alt="" /><span class="agent-status quiet"><i />{{ t('denv.agents.available') }}</span></div>
                  <h3>{{ t('denv.agents.designName') }}</h3><p>{{ t('denv.agents.designSummary') }}</p>
                  <div class="agent-card-footer"><span>{{ t('denv.agents.designRoom') }}</span><span>{{ t('denv.actions.open') }}<ChevronRight :size="12" /></span></div>
                </button>
              </div>
            </section>
            <section class="agent-directory-section">
              <div class="section-heading"><div><span class="eyebrow">{{ t('denv.agents.team') }}</span><h2>{{ t('denv.agents.shared') }}</h2></div></div>
              <div class="agent-team-row"><AgentPetAvatar pet-slug="deepseek" :size="32" alt="" /><span><strong>{{ t('denv.agents.releaseName') }}</strong><small>{{ t('denv.agents.releaseSummary') }}</small></span><span class="state-chip warning">{{ t('denv.states.connectionPending') }}</span><button type="button" @click="openMessages('release-room')">{{ t('denv.actions.open') }}</button></div>
            </section>
          </div>
        </template>
      </section>

      <aside v-if="appArea === 'messages' && contextOpen" class="context-panel" :aria-label="t('denv.context.panel')">
        <header class="context-header"><h2>{{ activeContext === 'files' ? t('denv.context.filesAndKnowledge') : t('denv.context.details') }}</h2><button type="button" class="bare-icon" @click="contextOpen = false"><X :size="15" /></button></header>
        <div class="context-scroll">
          <template v-if="activeContext === 'files'">
            <div class="context-intro"><h3>{{ t('denv.files.title') }}</h3><p>{{ t('denv.files.context') }}</p></div>
            <button v-for="file in currentChat.files" :key="file.id" type="button" class="file-card">
              <span class="file-thumb" :class="file.kind"><FileText :size="18" /><i /><i /><i /></span>
              <span><strong>{{ t(file.titleKey) }}</strong><small>{{ t(file.detailKey) }}</small></span><ChevronRight :size="13" />
            </button>
            <button type="button" class="context-add"><Plus :size="13" />{{ t('denv.files.add') }}</button>
            <section class="linked-work-section">
              <div class="context-section-title">{{ t('denv.context.linkedWork') }}</div>
              <button v-for="work in currentChat.work" :key="work.id" type="button" class="linked-work-row"><span class="work-check" /><span><strong>{{ t(work.titleKey) }}</strong><small>{{ t(work.stateKey) }}</small></span><ChevronRight :size="12" /></button>
            </section>
          </template>
          <template v-else>
            <div class="context-attention">
              <span class="context-section-title">{{ t('denv.context.attention') }}</span>
              <button v-for="work in unresolvedWork" :key="work.id" type="button"><span class="status-mark warning"><Circle :size="7" fill="currentColor" /></span><span><strong>{{ t(work.titleKey) }}</strong><small>{{ t(work.stateKey) }}</small></span><ChevronRight :size="12" /></button>
              <p v-if="!unresolvedWork.length">{{ t('denv.context.nothingWaiting') }}</p>
            </div>
            <section class="context-member-section">
              <div class="context-section-title">{{ t('denv.context.inThisRoom') }}</div>
              <div class="context-member"><span class="person-avatar">LC<i /></span><span><strong>{{ t('denv.people.li') }}</strong><small>{{ t('denv.members.liRole') }}</small></span></div>
              <div class="context-member"><span class="person-avatar">MZ<i /></span><span><strong>{{ t('denv.people.ming') }}</strong><small>{{ t('denv.members.mingRole') }}</small></span></div>
              <div class="context-member"><AgentPetAvatar pet-slug="deepseek" :size="26" alt="" /><span><strong>{{ t('denv.agents.researchName') }}</strong><small>{{ t('denv.agents.researchRole') }}</small></span></div>
              <div class="context-member"><AgentPetAvatar pet-slug="geptia" :size="26" alt="" /><span><strong>{{ t('denv.agents.designName') }}</strong><small>{{ t('denv.agents.designRole') }}</small></span></div>
            </section>
            <section class="context-linked">
              <div class="context-section-title">{{ t('denv.context.recentlyLinked') }}</div>
              <button type="button" @click="toggleContext('files')"><FileText :size="14" /><span><strong>{{ t('denv.files.brief') }}</strong><small>{{ t('denv.files.briefDetail') }}</small></span></button>
              <button type="button" @click="openMessages('release-room')"><Hash :size="14" /><span><strong>{{ t('denv.chats.releaseRoom') }}</strong><small>{{ t('denv.context.releaseLinkDetail') }}</small></span></button>
            </section>
          </template>
        </div>
      </aside>

      <aside v-if="settingsOpen" class="settings-panel">
        <header class="context-header"><h2>{{ t('denv.settings.title') }}</h2><button type="button" class="bare-icon" @click="settingsOpen = false"><X :size="15" /></button></header>
        <div class="settings-content">
          <div class="settings-group"><span>{{ t('denv.settings.general') }}</span><button type="button">{{ t('denv.settings.account') }}<ChevronRight :size="12" /></button><button type="button">{{ t('denv.settings.notifications') }}<ChevronRight :size="12" /></button><button type="button">{{ t('denv.settings.appearance') }}<ChevronRight :size="12" /></button></div>
          <div class="settings-group"><span>{{ t('denv.settings.yourWork') }}</span><button type="button">{{ t('denv.settings.agents') }}<ChevronRight :size="12" /></button><button type="button">{{ t('denv.settings.devices') }}<ChevronRight :size="12" /></button></div>
        </div>
      </aside>
    </div>
  </main>
</template>

<style scoped>
/* Product styling is intentionally local: Denv shares the Office token vocabulary and island
   hierarchy without inheriting every workbench rule. */
.denv-app {
  --rail-width: 61px;
  --nav-width: 245px;
  --context-width: 292px;
  display: flex;
  flex-direction: column;
  width: 100vw;
  height: 100vh;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  color: var(--text-primary);
  background:
    radial-gradient(ellipse at 22% -12%, color-mix(in srgb, var(--accent-primary) 6%, transparent), transparent 38%),
    linear-gradient(180deg, color-mix(in srgb, var(--bg-secondary) 35%, var(--bg-primary)), var(--bg-primary));
  font-family: 'Geist Variable', 'Microsoft YaHei UI', 'PingFang SC', sans-serif;
}

button, input, textarea { font: inherit; }
button { border: 0; cursor: pointer; }
.titlebar-drag { position: absolute; inset: 0; -webkit-app-region: drag; }

.denv-titlebar {
  position: relative;
  display: flex;
  align-items: center;
  gap: 17px;
  height: 41px;
  padding: 0 7px 0 15px;
  flex: 0 0 auto;
  background: transparent;
  border-bottom: 0;
  z-index: 10;
}

.denv-brand {
  position: relative;
  z-index: 1;
  display: flex;
  align-items: center;
  gap: 7px;
  min-width: 165px;
  color: var(--text-brand);
  font-size: 13px;
  font-weight: 700;
}

.titlebar-search {
  position: relative;
  z-index: 1;
  display: flex;
  align-items: center;
  gap: 8px;
  width: min(370px, 32vw);
  height: 27px;
  padding: 0 8px;
  margin-left: 6px;
  border: 1px solid var(--border-card);
  border-radius: 8px;
  color: var(--text-muted);
  background: color-mix(in srgb, var(--surface-section) 74%, transparent);
  box-shadow: var(--shadow-card-subtle);
  font-size: 11px;
  text-align: left;
  -webkit-app-region: no-drag;
}
.titlebar-search:hover { color: var(--text-secondary); border-color: var(--border-default); }
.titlebar-search span { flex: 1; }
.titlebar-search kbd, .navigation-search kbd, .titlebar-search kbd { padding: 1px 4px; border: 1px solid var(--border-muted); border-radius: 3px; font-family: inherit; font-size: 9px; }

.titlebar-controls { position: relative; z-index: 1; display: flex; margin-left: auto; -webkit-app-region: no-drag; }
.titlebar-controls button { width: 34px; height: 30px; display: grid; place-items: center; border-radius: 4px; color: var(--text-secondary); background: transparent; }
.titlebar-controls button:hover { color: var(--text-primary); background: var(--surface-hover); }
.titlebar-controls button.close:hover { color: var(--accent-danger); }

.denv-workspace {
  display: grid;
  grid-template-columns: var(--rail-width) var(--nav-width) minmax(0, 1fr);
  flex: 1;
  min-height: 0;
  overflow: hidden;
  gap: 10px;
  padding: 8px 8px 9px 0;
}
.denv-workspace.context-open { grid-template-columns: var(--rail-width) var(--nav-width) minmax(0, 1fr) var(--context-width); }

.app-rail, .denv-navigation, .context-panel, .settings-panel {
  min-height: 0;
  background: color-mix(in srgb, var(--surface-section) 92%, transparent);
  border: 1px solid var(--border-card);
  border-radius: 12px;
  box-shadow: var(--shadow-card-subtle);
}

.app-rail { display: flex; flex-direction: column; align-items: center; padding: 11px 7px 10px; margin-left: 8px; }
.profile-button { position: relative; width: 33px; height: 33px; display: grid; place-items: center; margin-bottom: 14px; border-radius: 9px; color: var(--text-brand); background: var(--surface-raised); font-size: 9px; font-weight: 700; }
.profile-button img { width: 100%; height: 100%; border-radius: inherit; object-fit: cover; }
.profile-button i { position: absolute; right: -1px; bottom: -1px; width: 8px; height: 8px; border: 2px solid var(--surface-section); border-radius: 50%; background: var(--accent-success); }
.app-nav { display: flex; flex-direction: column; gap: 4px; width: 100%; }
.app-nav button { position: relative; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 2px; width: 100%; height: 45px; border-radius: 7px; color: var(--text-muted); background: transparent; font-size: 8px; }
.app-nav button:hover { color: var(--text-secondary); background: var(--surface-hover); }
.app-nav button.active { color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-raised)); }
.app-nav button.active::before { content: ''; position: absolute; left: -5px; width: 2px; height: 19px; border-radius: 2px; background: var(--accent-primary); }
.nav-badge { position: absolute; top: 4px; right: 11px; display: grid; place-items: center; min-width: 13px; height: 13px; padding: 0 3px; border: 1px solid var(--surface-section); border-radius: 7px; color: var(--text-brand); background: var(--surface-raised); font-size: 8px; font-style: normal; }
.app-rail-bottom { display: flex; flex-direction: column; gap: 5px; margin-top: auto; }
.app-rail-bottom button { position: relative; width: 34px; height: 31px; display: grid; place-items: center; border-radius: 6px; color: var(--text-muted); background: transparent; }
.app-rail-bottom button:hover, .app-rail-bottom button.active { color: var(--text-primary); background: var(--surface-hover); }
.notification-dot { position: absolute; top: 6px; right: 6px; width: 5px; height: 5px; border-radius: 50%; background: var(--accent-primary); }

.denv-navigation { position: relative; display: flex; flex-direction: column; overflow: hidden; padding: 11px 9px 9px; }
.denv-navigation::before { content: ''; position: absolute; top: 0; right: 10px; left: 10px; height: 1px; pointer-events: none; background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--text-primary) 9%, transparent), transparent); }
.navigation-header { display: flex; align-items: center; justify-content: space-between; padding: 0 4px 9px; }
.organization-name { display: flex; flex-direction: column; gap: 1px; }
.organization-name strong { font-size: 13px; font-weight: 650; }
.organization-name span { color: var(--text-muted); font-size: 9px; }
.bare-icon { display: grid; place-items: center; width: 26px; height: 25px; border-radius: 5px; color: var(--text-muted); background: transparent; }
.bare-icon:hover { color: var(--text-primary); background: var(--surface-hover); }
.navigation-search { display: flex; align-items: center; gap: 6px; min-height: 28px; padding: 0 7px; margin-bottom: 11px; border: 1px solid var(--border-muted); border-radius: 5px; color: var(--text-muted); background: var(--surface-input); }
.navigation-search input { min-width: 0; flex: 1; padding: 0; border: 0; outline: 0; color: var(--text-primary); background: transparent; font-size: 10.5px; }
.navigation-search input::placeholder { color: var(--text-muted); }
.navigation-search kbd { color: var(--text-muted); }
.navigation-scroll { min-height: 0; flex: 1; overflow-y: auto; padding: 0 1px; }
.nav-section + .nav-section { margin-top: 15px; padding-top: 9px; border-top: 1px solid var(--border-muted); }
.nav-section-title { display: flex; align-items: center; justify-content: space-between; min-height: 19px; padding: 0 5px; color: var(--text-muted); font-size: 9px; font-weight: 600; letter-spacing: .02em; }
.nav-section button:not(.chat-nav-row) { display: flex; align-items: center; gap: 8px; width: 100%; min-height: 28px; padding: 0 6px; border-radius: 5px; color: var(--text-secondary); background: transparent; font-size: 10.5px; text-align: left; }
.nav-section button:not(.chat-nav-row):hover { color: var(--text-primary); background: var(--surface-hover); }
.nav-section button:not(.chat-nav-row).active { color: var(--text-primary); background: color-mix(in srgb, var(--accent-primary) 11%, var(--surface-raised)); }
.nav-section button:not(.chat-nav-row) > svg { flex: 0 0 auto; }
.count { min-width: 16px; height: 16px; display: inline-grid; place-items: center; padding: 0 4px; margin-left: auto; border-radius: 8px; color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 12%, transparent); font-size: 8px; font-style: normal; }
.chat-nav-row { display: flex; align-items: center; gap: 7px; width: 100%; min-height: 45px; padding: 4px 6px; border-radius: 5px; color: var(--text-primary); background: transparent; text-align: left; }
.chat-nav-row:hover { background: var(--surface-hover); }
.chat-nav-row.active { background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-raised)); }
.nav-avatar, .nav-glyph { width: 24px; height: 24px; flex: 0 0 auto; display: grid; place-items: center; border-radius: 6px; color: var(--text-secondary); background: var(--surface-raised); font-size: 8px; font-weight: 700; }
.nav-avatar.team { color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 10%, var(--surface-raised)); font-size: 7px; }
.nav-glyph { border-radius: 5px; background: transparent; }
.chat-nav-copy { display: flex; min-width: 0; flex: 1; flex-direction: column; gap: 1px; }
.chat-nav-copy strong { overflow: hidden; font-size: 10.5px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.chat-nav-copy small { overflow: hidden; color: var(--text-muted); font-size: 8.5px; line-height: 1.35; text-overflow: ellipsis; white-space: nowrap; }
.chat-nav-tail { display: flex; flex-direction: column; align-items: flex-end; gap: 3px; align-self: stretch; padding-top: 3px; }
.chat-nav-tail time { color: var(--text-muted); font-size: 8px; white-space: nowrap; }
.all-conversations { color: var(--text-muted) !important; font-size: 9.5px !important; }
.space-glyph { width: 21px; height: 21px; display: grid; place-items: center; border-radius: 5px; color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 9%, var(--surface-raised)); font-size: 8px; font-weight: 700; }
.space-glyph.secondary { color: var(--text-secondary); background: var(--surface-raised); }
.navigation-footer { display: flex; flex-direction: column; padding-top: 7px; margin-top: 7px; border-top: 1px solid var(--border-muted); }
.navigation-footer button { display: flex; align-items: center; gap: 7px; min-height: 26px; padding: 0 6px; border-radius: 5px; color: var(--text-muted); background: transparent; font-size: 9.5px; text-align: left; }
.navigation-footer button:hover { color: var(--text-primary); background: var(--surface-hover); }

.primary-surface { position: relative; display: flex; min-width: 0; min-height: 0; flex-direction: column; overflow: hidden; background: transparent; }
.surface-header { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; padding: 21px clamp(20px, 3.3vw, 40px) 15px; flex: 0 0 auto; border-bottom: 1px solid color-mix(in srgb, var(--border-muted) 68%, transparent); }
.eyebrow { color: var(--text-muted); font-size: 8px; font-weight: 600; letter-spacing: .07em; text-transform: uppercase; }
.surface-header h1 { margin: 3px 0 3px; color: var(--text-primary); font-size: 22px; font-weight: 650; letter-spacing: -.035em; }
.surface-header p { margin: 0; color: var(--text-secondary); font-size: 10.5px; }
.primary-soft-button { display: flex; align-items: center; gap: 5px; height: 28px; padding: 0 9px; border: 1px solid var(--border-default); border-radius: 5px; color: var(--text-secondary); background: var(--surface-raised); font-size: 10px; white-space: nowrap; }
.primary-soft-button:hover { color: var(--text-primary); border-color: var(--accent-primary); }

.home-scroll { min-height: 0; flex: 1; overflow-y: auto; padding: 20px clamp(20px, 3.3vw, 40px) 35px; }
.home-section, .home-columns { width: 100%; max-width: 1040px; margin: 0 auto; }
.home-section + .home-section { margin-top: 23px; padding-top: 19px; border-top: 1px solid color-mix(in srgb, var(--border-muted) 65%, transparent); }
.section-heading { display: flex; align-items: flex-end; justify-content: space-between; gap: 12px; margin-bottom: 9px; }
.section-heading h2 { margin: 2px 0 0; color: var(--text-primary); font-size: 14px; font-weight: 650; letter-spacing: -.02em; }
.section-heading > button { display: flex; align-items: center; gap: 2px; padding: 0; color: var(--text-muted); background: transparent; font-size: 9px; }
.section-heading > button:hover { color: var(--text-primary); }
.needs-you-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
.need-card { display: flex; min-width: 0; min-height: 133px; flex-direction: column; padding: 10px 11px; border: 1px solid var(--border-card); border-radius: 10px; color: var(--text-primary); background: var(--surface-raised); box-shadow: var(--shadow-card-subtle); text-align: left; }
.need-card.accent { border-color: color-mix(in srgb, var(--accent-primary) 23%, var(--border-card)); background: linear-gradient(140deg, color-mix(in srgb, var(--accent-primary) 8%, transparent), transparent 55%), var(--surface-raised); }
.need-card:hover { border-color: var(--border-card-active); background-color: var(--surface-hover); }
.need-card-top { display: flex; align-items: center; gap: 5px; color: var(--text-muted); font-size: 8px; letter-spacing: .05em; }
.need-card-top time { margin-left: auto; letter-spacing: 0; }
.status-mark { width: 6px; height: 6px; display: inline-flex; align-items: center; justify-content: center; border-radius: 50%; }
.status-mark svg { width: 6px; height: 6px; }
.status-mark.warning { color: var(--accent-warning); } .status-mark.danger { color: var(--accent-danger); }
.need-card h3 { margin: 9px 0 4px; font-size: 11.5px; font-weight: 650; line-height: 1.35; }
.need-card p { margin: 0; color: var(--text-secondary); font-size: 9.5px; line-height: 1.5; }
.need-card-bottom { display: flex; align-items: center; margin-top: auto; padding-top: 9px; }
.mini-faces { display: flex; }
.mini-faces i { width: 20px; height: 20px; display: grid; place-items: center; margin-left: -4px; border: 2px solid var(--surface-raised); border-radius: 50%; color: var(--text-secondary); background: var(--surface-button); font-size: 7px; font-style: normal; }
.mini-faces i:first-child { margin-left: 0; }
.mini-faces i.agent { color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-button)); }
.need-card-bottom strong { display: flex; align-items: center; gap: 2px; margin-left: auto; color: var(--text-brand); font-size: 9px; font-weight: 550; }
.subtle-chip { padding: 2px 5px; border-radius: 3px; color: var(--text-muted); background: var(--surface-button); font-size: 8px; }
.home-conversation-list { overflow: hidden; border: 1px solid var(--border-card); border-radius: 9px; background: var(--surface-raised); box-shadow: var(--shadow-card-subtle); }
.home-conversation-row { display: flex; align-items: center; gap: 9px; width: 100%; min-height: 48px; padding: 7px 10px; color: var(--text-primary); background: transparent; text-align: left; }
.home-conversation-row + .home-conversation-row { border-top: 1px solid var(--border-muted); }
.home-conversation-row:hover { background: var(--surface-hover); }
.conversation-icon { width: 25px; height: 25px; flex: 0 0 auto; display: grid; place-items: center; border-radius: 6px; color: var(--text-secondary); background: var(--surface-button); font-size: 9px; font-weight: 650; }
.conversation-icon.team { color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 11%, var(--surface-button)); }
.home-conversation-copy { display: flex; min-width: 0; flex: 1; flex-direction: column; gap: 2px; }
.home-conversation-copy strong { font-size: 10.5px; font-weight: 600; }
.home-conversation-copy small { overflow: hidden; color: var(--text-secondary); font-size: 9px; text-overflow: ellipsis; white-space: nowrap; }
.home-conversation-meta { display: flex; align-items: center; gap: 9px; flex: 0 0 auto; color: var(--text-muted); font-size: 8.5px; }
.home-conversation-meta time { font-variant-numeric: tabular-nums; }
.unread-count { display: grid; place-items: center; width: 16px; height: 16px; border-radius: 8px; color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 12%, transparent); font-size: 8px; font-style: normal; }
.attention-dot { width: 6px; height: 6px; border-radius: 50%; background: var(--accent-warning); }
.home-columns { display: grid; grid-template-columns: 1.05fr .95fr; gap: 10px; max-width: 1040px; margin: 23px auto 0; }
.home-compact-section { min-width: 0; padding: 10px 11px; border: 1px solid var(--border-card); border-radius: 10px; background: color-mix(in srgb, var(--surface-section) 38%, transparent); box-shadow: var(--shadow-card-subtle); }
.home-compact-section .section-heading { margin-bottom: 5px; }
.compact-work-row { display: grid; grid-template-columns: 15px minmax(0,1fr) auto; align-items: center; gap: 7px; width: 100%; min-height: 35px; padding: 4px 4px; border-radius: 4px; color: var(--text-primary); background: transparent; text-align: left; }
.compact-work-row:hover { background: var(--surface-hover); }
.compact-work-row > span:nth-child(2) { display: flex; min-width: 0; flex-direction: column; gap: 1px; }
.compact-work-row strong { overflow: hidden; font-size: 9.8px; font-weight: 550; text-overflow: ellipsis; white-space: nowrap; }
.compact-work-row small { overflow: hidden; color: var(--text-muted); font-size: 8.5px; text-overflow: ellipsis; white-space: nowrap; }
.work-check { width: 12px; height: 12px; display: grid; place-items: center; border: 1px solid var(--border-default); border-radius: 3px; color: var(--bg-primary); }
.work-check.done { border-color: var(--accent-success); background: var(--accent-success); }
.state-chip { padding: 2px 5px; border-radius: 3px; white-space: nowrap; font-size: 8px; }
.state-chip.warning { color: var(--accent-warning); background: color-mix(in srgb, var(--accent-warning) 10%, transparent); }
.state-chip.success { color: var(--accent-success); background: color-mix(in srgb, var(--accent-success) 10%, transparent); }
.person-row { display: flex; align-items: center; gap: 7px; width: 100%; min-height: 34px; padding: 4px; border-radius: 4px; color: var(--text-primary); text-align: left; }
.person-row:hover { background: var(--surface-hover); }
.person-avatar { position: relative; width: 26px; height: 26px; flex: 0 0 auto; display: grid; place-items: center; border-radius: 7px; color: var(--text-secondary); background: var(--surface-raised); font-size: 8px; font-weight: 650; }
.person-avatar i { position: absolute; right: -1px; bottom: -1px; width: 6px; height: 6px; border: 1px solid var(--surface-section); border-radius: 50%; background: var(--accent-success); }
.person-row > span:nth-child(2) { display: flex; min-width: 0; flex: 1; flex-direction: column; gap: 1px; }
.person-row strong { font-size: 9.7px; font-weight: 550; }
.person-row small { overflow: hidden; color: var(--text-muted); font-size: 8.5px; text-overflow: ellipsis; white-space: nowrap; }
.row-action { display: grid; place-items: center; width: 24px; height: 22px; border-radius: 4px; color: var(--text-muted); background: transparent; opacity: 0; }
.person-row:hover .row-action { opacity: 1; color: var(--text-primary); background: var(--surface-button); }

.updates-list { max-width: 850px; margin: 0 auto; overflow: hidden; border: 1px solid var(--border-card); border-radius: 9px; background: var(--surface-raised); box-shadow: var(--shadow-card-subtle); }
.update-row { display: flex; align-items: center; gap: 9px; width: 100%; min-height: 52px; padding: 7px 10px; color: var(--text-primary); background: transparent; text-align: left; }
.update-row + .update-row { border-top: 1px solid var(--border-muted); }
.update-row:hover { background: var(--surface-hover); }
.update-icon { width: 25px; height: 25px; flex: 0 0 auto; display: grid; place-items: center; border-radius: 6px; background: var(--surface-button); font-size: 8px; font-weight: 650; }
.update-icon.agent { color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 10%, transparent); }
.update-icon.warning { color: var(--accent-warning); background: color-mix(in srgb, var(--accent-warning) 10%, transparent); }
.update-row > span:nth-child(2) { display: flex; min-width: 0; flex: 1; flex-direction: column; gap: 2px; }
.update-row strong { font-size: 10.5px; font-weight: 600; }
.update-row small { color: var(--text-secondary); font-size: 9px; }
.update-row time { color: var(--text-muted); font-size: 8.5px; }
.update-digest { display: grid; grid-template-columns: 1fr 1fr; gap: 9px; max-width: 1040px; margin: 10px auto 0; }
.update-digest-card { min-width: 0; padding: 10px; border: 1px solid var(--border-card); border-radius: 9px; background: var(--surface-raised); box-shadow: var(--shadow-card-subtle); }
.digest-row { display: flex; align-items: center; gap: 8px; min-height: 38px; border-top: 1px solid var(--border-muted); }
.digest-row > span:last-child { display: flex; min-width: 0; flex-direction: column; gap: 2px; }
.digest-row strong { font-size: 9.5px; font-weight: 550; }
.digest-row small { color: var(--text-muted); font-size: 8px; }
.digest-number { width: 25px; height: 25px; display: grid; place-items: center; flex: 0 0 auto; border-radius: 6px; color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 10%, transparent); font-size: 11px; font-weight: 650; }
.decision-row { display: flex; align-items: center; gap: 7px; width: 100%; min-height: 42px; padding: 5px 0; border-top: 1px solid var(--border-muted); color: var(--text-primary); background: transparent; text-align: left; }
.decision-row:hover { background: var(--surface-hover); }
.decision-icon { width: 23px; height: 23px; display: grid; place-items: center; flex: 0 0 auto; border-radius: 5px; color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 9%, transparent); }
.decision-icon.warning { color: var(--accent-warning); background: color-mix(in srgb, var(--accent-warning) 9%, transparent); }
.decision-row > span:nth-child(2) { display: flex; min-width: 0; flex: 1; flex-direction: column; gap: 2px; }
.decision-row strong { font-size: 9.5px; font-weight: 550; }
.decision-row small { color: var(--text-muted); font-size: 8px; }
.space-summary { display: flex; gap: 12px; max-width: 1040px; margin: 0 auto 13px; padding: 12px; border: 1px solid var(--border-card); border-radius: 9px; background: var(--surface-raised); box-shadow: var(--shadow-card-subtle); }
.space-large-glyph { width: 40px; height: 40px; flex: 0 0 auto; display: grid; place-items: center; border-radius: 10px; color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-raised)); font-size: 15px; font-weight: 650; }
.space-summary h2 { margin: 0; font-size: 13px; font-weight: 650; }
.space-summary p { margin: 3px 0 6px; color: var(--text-secondary); font-size: 9.5px; }
.space-members { display: flex; align-items: center; }
.space-members i { width: 20px; height: 20px; display: grid; place-items: center; margin-left: -3px; border: 2px solid var(--surface-raised); border-radius: 50%; background: var(--surface-button); color: var(--text-secondary); font-size: 7px; font-style: normal; }
.space-members i.agent { color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-button)); }
.space-members em { margin-left: 5px; color: var(--text-muted); font-size: 8px; font-style: normal; }
.space-tabs { display: flex; gap: 15px; max-width: 1040px; margin: 0 auto 10px; border-bottom: 1px solid var(--border-muted); }
.space-tabs button { padding: 0 0 7px; border-bottom: 2px solid transparent; color: var(--text-muted); background: transparent; font-size: 9.5px; }
.space-tabs button.active { color: var(--text-primary); border-color: var(--accent-primary); }
.space-tabs i { margin-left: 3px; font-size: 7px; font-style: normal; }
.space-overview-grid { display: grid; grid-template-columns: 1.2fr .8fr; gap: 8px; max-width: 1040px; margin: 0 auto; }
.space-panel { min-width: 0; padding: 10px; border: 1px solid var(--border-card); border-radius: 9px; background: var(--surface-raised); box-shadow: var(--shadow-card-subtle); }
.space-panel.wide { grid-column: span 2; }
.space-project-copy { margin: 2px 0 9px; color: var(--text-secondary); font-size: 9.5px; line-height: 1.5; }
.space-project-meta { display: flex; gap: 6px; align-items: center; color: var(--text-muted); font-size: 8.5px; }
.space-project-meta b { margin-right: 11px; color: var(--text-primary); font-weight: 550; }
.space-progress { height: 4px; margin: 8px 0; border-radius: 2px; background: var(--surface-button); }
.space-progress i { display: block; width: 55%; height: 100%; border-radius: 2px; background: var(--accent-primary); }
.space-work-row { display: flex; align-items: center; gap: 7px; min-height: 28px; padding: 3px 0; border-top: 1px solid var(--border-muted); color: var(--text-primary); font-size: 9.5px; }
.space-work-row > span:nth-child(2) { display: flex; flex: 1; flex-direction: column; gap: 1px; }
.space-work-row small { color: var(--text-muted); font-size: 8px; }
.pulse-row { display: flex; align-items: center; gap: 6px; min-height: 34px; }
.pulse-row > span:last-child { display: flex; min-width: 0; flex-direction: column; gap: 1px; }
.pulse-row b { font-size: 9.5px; font-weight: 550; }
.pulse-row small { overflow: hidden; color: var(--text-muted); font-size: 8px; text-overflow: ellipsis; white-space: nowrap; }
.knowledge-row { display: flex; align-items: center; gap: 7px; width: 100%; padding: 5px 0; color: var(--text-secondary); background: transparent; text-align: left; }
.knowledge-row > span { display: flex; min-width: 0; flex-direction: column; gap: 2px; }
.knowledge-row b { color: var(--text-primary); font-size: 9.5px; font-weight: 550; }
.knowledge-row small { color: var(--text-muted); font-size: 8px; }

.message-header { position: relative; display: flex; align-items: flex-start; gap: 5px; min-height: 76px; padding: 14px 17px 10px; flex: 0 0 auto; border-bottom: 1px solid color-mix(in srgb, var(--border-muted) 68%, transparent); }
.message-heading { min-width: 0; flex: 1; }
.message-title-row { display: flex; align-items: center; gap: 6px; min-width: 0; }
.message-title-row h1 { overflow: hidden; margin: 0; font-size: 16px; font-weight: 650; letter-spacing: -.02em; text-overflow: ellipsis; white-space: nowrap; }
.message-title-row > svg { color: var(--text-muted); }
.header-members { overflow: hidden; color: var(--text-muted); font-size: 8px; text-overflow: ellipsis; white-space: nowrap; }
.message-heading > p { margin: 3px 0 0 31px; color: var(--text-secondary); font-size: 9px; }
.message-context-button { display: flex; align-items: center; gap: 4px; height: 26px; padding: 0 6px; border-radius: 5px; color: var(--text-secondary); background: transparent; font-size: 9px; white-space: nowrap; }
.message-context-button:hover, .message-context-button.active { color: var(--text-primary); background: var(--surface-hover); }
.message-context-button.agent-button { color: var(--text-brand); }
.agent-picker { position: absolute; z-index: 5; top: 61px; right: 56px; width: 240px; padding: 5px; border: 1px solid var(--border-default); border-radius: 7px; background: var(--bg-overlay); box-shadow: var(--shadow-elevated); }
.agent-picker-head { padding: 5px 7px; color: var(--text-muted); font-size: 8px; }
.agent-picker button { display: flex; align-items: center; gap: 7px; width: 100%; padding: 6px; border-radius: 5px; color: var(--text-primary); background: transparent; text-align: left; }
.agent-picker button:hover { background: var(--surface-hover); }
.agent-picker button span { display: flex; flex-direction: column; gap: 1px; }
.agent-picker strong { font-size: 9px; font-weight: 600; }
.agent-picker small { color: var(--text-muted); font-size: 8px; }
.message-tabs { display: flex; align-items: center; gap: 16px; min-height: 31px; padding: 0 17px; flex: 0 0 auto; border-bottom: 1px solid var(--border-muted); }
.message-tabs button { align-self: stretch; padding: 0; border-bottom: 2px solid transparent; color: var(--text-muted); background: transparent; font-size: 9px; }
.message-tabs button.active { color: var(--text-primary); border-color: var(--accent-primary); }
.message-tabs button:hover { color: var(--text-secondary); }
.message-tabs i { margin-left: 3px; color: var(--text-brand); font-size: 7px; font-style: normal; }
.message-stream { min-height: 0; flex: 1; overflow-y: auto; padding: 8px clamp(17px, 4vw, 48px) 8px; }
.day-divider { display: flex; align-items: center; gap: 10px; margin: 2px 0 10px; color: var(--text-muted); font-size: 8px; }
.day-divider::before, .day-divider::after { content: ''; height: 1px; flex: 1; background: var(--border-muted); }
.message-item { display: grid; grid-template-columns: 31px minmax(0, 1fr); gap: 8px; max-width: 820px; margin: 0 auto 11px; }
.message-item.system { display: block; }
.message-avatar { width: 30px; height: 30px; display: grid; place-items: center; overflow: hidden; border: 1px solid var(--border-muted); border-radius: 7px; color: var(--text-secondary); background: var(--surface-raised); font-size: 8px; font-weight: 650; }
.message-avatar.agent { border-color: color-mix(in srgb, var(--accent-primary) 17%, var(--border-muted)); background: color-mix(in srgb, var(--accent-primary) 7%, var(--surface-raised)); }
.message-body { min-width: 0; }
.message-meta { display: flex; align-items: center; gap: 5px; min-height: 17px; }
.message-meta strong { color: var(--text-primary); font-size: 10px; font-weight: 650; }
.message-meta time { color: var(--text-muted); font-size: 8px; }
.agent-tag, .role-tag { padding: 1px 3px; border-radius: 2px; color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 9%, transparent); font-size: 7px; line-height: 1.2; }
.role-tag { color: var(--text-secondary); background: var(--surface-button); }
.message-body > p { margin: 2px 0 0; color: var(--text-primary); font-size: 10px; line-height: 1.55; white-space: pre-wrap; }
.result-block { margin-top: 4px; padding: 8px 9px; border: 1px solid var(--border-card); border-radius: 6px; background: color-mix(in srgb, var(--accent-primary) 3%, var(--surface-raised)); }
.result-block.decision { border-left: 2px solid var(--accent-primary); background: var(--surface-raised); }
.result-label { display: flex; align-items: center; gap: 4px; color: var(--text-brand); font-size: 8px; font-weight: 600; }
.result-block.decision .result-label { color: var(--text-secondary); }
.result-block p { margin: 4px 0 0; color: var(--text-primary); font-size: 10px; line-height: 1.55; }
.linked-work { display: flex; align-items: center; gap: 6px; margin-top: 7px; padding-top: 6px; border-top: 1px solid var(--border-muted); }
.linked-work-icon { width: 20px; height: 21px; display: grid; place-items: center; border-radius: 4px; color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 8%, transparent); }
.linked-work > span:nth-child(2) { display: flex; min-width: 0; flex: 1; flex-direction: column; gap: 1px; }
.linked-work strong { font-size: 8.8px; font-weight: 550; }
.linked-work small { color: var(--text-muted); font-size: 7.8px; }
.linked-work button { padding: 1px 3px; color: var(--text-brand); background: transparent; font-size: 8px; }
.system-row { display: flex; align-items: center; gap: 7px; margin: 7px 0 11px; color: var(--text-muted); font-size: 8px; text-align: center; }
.system-row > span:first-child, .system-row > span:last-child { height: 1px; flex: 1; background: var(--border-muted); }
.system-icon { display: grid; place-items: center; width: 17px; height: 17px; border-radius: 4px; color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 8%, transparent); }
.agent-presence { display: flex; align-items: center; gap: 5px; max-width: 820px; margin: 1px auto 1px 39px; color: var(--text-muted); font-size: 8px; }
.agent-presence i { width: 2px; height: 2px; border-radius: 50%; background: var(--text-muted); }
.message-composer-bar { flex: 0 0 auto; padding: 6px clamp(14px, 4vw, 48px) 9px; }
.message-composer { max-width: 820px; margin: 0 auto; padding: 7px 8px 6px; border: 1px solid var(--border-input); border-radius: 9px; background: var(--surface-input); box-shadow: var(--shadow-card-subtle); }
.message-composer:focus-within { border-color: color-mix(in srgb, var(--accent-primary) 55%, var(--border-input)); box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 8%, transparent); }
.message-composer textarea { display: block; width: 100%; min-height: 23px; max-height: 124px; padding: 0 2px; resize: none; border: 0; outline: 0; color: var(--text-primary); background: transparent; font-size: 10px; line-height: 1.5; user-select: text; }
.message-composer textarea::placeholder { color: var(--text-muted); }
.composer-toolbar { display: flex; align-items: center; justify-content: space-between; margin-top: 4px; }
.composer-toolbar > div { display: flex; align-items: center; gap: 4px; }
.composer-icon { display: grid; place-items: center; width: 23px; height: 21px; border-radius: 4px; color: var(--text-muted); background: transparent; }
.composer-icon:hover { color: var(--text-primary); background: var(--surface-hover); }
.bring-agent { display: flex; align-items: center; gap: 4px; min-height: 21px; padding: 0 4px; border-radius: 4px; color: var(--text-brand); background: transparent; font-size: 8.5px; }
.bring-agent:hover { background: var(--surface-hover); }
.composer-hint { color: var(--text-muted); font-size: 8px; }
.send-button { display: grid; place-items: center; width: 25px; height: 23px; border-radius: 5px; color: var(--bg-primary); background: var(--accent-primary); }
.send-button:disabled { color: var(--text-muted); background: var(--surface-button); }

.work-toolbar { display: flex; justify-content: space-between; align-items: center; min-height: 42px; padding: 0 20px; border-bottom: 1px solid var(--border-muted); }
.work-filters { display: flex; gap: 4px; }
.work-filters button { padding: 3px 6px; border-radius: 4px; color: var(--text-muted); background: transparent; font-size: 9px; }
.work-filters button.active { color: var(--text-primary); background: var(--surface-hover); }
.view-selector { display: flex; align-items: center; gap: 4px; color: var(--text-muted); background: transparent; font-size: 9px; }
.surface-scroll { min-height: 0; flex: 1; overflow-y: auto; }
.work-table { margin: 14px clamp(17px, 4vw, 42px) 0; padding: 0 13px; border: 1px solid var(--border-card); border-radius: 10px; background: var(--surface-raised); box-shadow: var(--shadow-card-subtle); }
.work-table-head, .work-table-row { display: grid; grid-template-columns: minmax(240px, 1.6fr) .7fr .85fr 1fr .45fr; align-items: center; gap: 10px; min-height: 37px; }
.work-table-head { color: var(--text-muted); font-size: 8px; border-bottom: 1px solid var(--border-muted); }
.work-table-row { width: 100%; padding: 0; border-top: 1px solid var(--border-muted); color: var(--text-primary); background: transparent; text-align: left; font-size: 9px; }
.work-table-row:hover { background: var(--surface-hover); }
.work-title-cell { display: flex; align-items: center; gap: 6px; min-width: 0; }
.work-title-cell > span { display: flex; min-width: 0; flex-direction: column; gap: 1px; }
.work-title-cell strong { overflow: hidden; font-size: 9.5px; font-weight: 550; text-overflow: ellipsis; white-space: nowrap; }
.work-title-cell small { color: var(--text-muted); font-size: 8px; }
.work-type { width: 21px; height: 21px; display: grid; place-items: center; border-radius: 4px; color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 8%, transparent); }
.owner-cell { color: var(--text-secondary); }
.state-pill { padding: 2px 5px; border-radius: 3px; color: var(--text-secondary); background: var(--surface-button); font-size: 8px; font-style: normal; }
.state-pill.warning { color: var(--accent-warning); background: color-mix(in srgb, var(--accent-warning) 9%, transparent); }
.state-pill.success { color: var(--accent-success); background: color-mix(in srgb, var(--accent-success) 9%, transparent); }
.state-pill.accent { color: var(--text-brand); background: color-mix(in srgb, var(--accent-primary) 9%, transparent); }
.source-cell { display: flex; align-items: center; gap: 4px; color: var(--text-muted); }
.work-table-row time { color: var(--text-muted); font-size: 8px; text-align: right; }
.empty-work { padding: 25px; color: var(--text-muted); font-size: 10px; text-align: center; }

.agents-scroll { min-height: 0; flex: 1; overflow-y: auto; padding: 20px clamp(18px, 4vw, 44px) 32px; }
.agent-directory-section { max-width: 900px; margin: 0 auto; }
.agent-directory-section + .agent-directory-section { margin-top: 22px; padding-top: 17px; border-top: 1px solid var(--border-muted); }
.agent-card-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
.agent-card { display: flex; min-height: 145px; flex-direction: column; padding: 11px; border: 1px solid var(--border-card); border-radius: 10px; color: var(--text-primary); background: var(--surface-raised); box-shadow: var(--shadow-card-subtle); text-align: left; }
.agent-card:hover { border-color: var(--border-card-active); background: var(--surface-hover); }
.agent-card-top { display: flex; justify-content: space-between; }
.agent-status { display: flex; align-items: center; gap: 3px; color: var(--accent-success); font-size: 8px; }
.agent-status i { width: 5px; height: 5px; border-radius: 50%; background: currentColor; }
.agent-status.quiet { color: var(--text-muted); }
.agent-card h3 { margin: 8px 0 3px; font-size: 11px; font-weight: 650; }
.agent-card > p { margin: 0; color: var(--text-secondary); font-size: 9px; line-height: 1.5; }
.agent-card-footer { display: flex; justify-content: space-between; align-items: center; margin-top: auto; padding-top: 8px; color: var(--text-muted); font-size: 8px; }
.agent-card-footer span:last-child { display: flex; align-items: center; gap: 2px; color: var(--text-brand); }
.agent-team-row { display: flex; align-items: center; gap: 8px; min-height: 44px; padding: 6px 8px; border: 1px solid var(--border-card); border-radius: 9px; background: var(--surface-raised); box-shadow: var(--shadow-card-subtle); }
.agent-team-row > span:nth-child(2) { display: flex; min-width: 0; flex: 1; flex-direction: column; gap: 1px; }
.agent-team-row strong { font-size: 10px; font-weight: 600; }
.agent-team-row small { color: var(--text-muted); font-size: 8.5px; }
.agent-team-row button { color: var(--text-brand); background: transparent; font-size: 8px; }

.context-panel, .settings-panel { position: relative; display: flex; flex-direction: column; overflow: hidden; }
.context-panel::before { content: ''; position: absolute; top: 13px; bottom: 13px; left: 0; width: 1px; pointer-events: none; background: linear-gradient(180deg, transparent, color-mix(in srgb, var(--accent-primary) 15%, transparent), transparent); }
.context-header { display: flex; align-items: center; justify-content: space-between; min-height: 45px; padding: 0 10px 0 14px; border-bottom: 1px solid var(--border-muted); }
.context-header h2 { margin: 0; font-size: 12px; font-weight: 650; }
.context-scroll { min-height: 0; flex: 1; overflow-y: auto; padding: 12px 10px; }
.context-intro { padding: 0 3px 11px; }
.context-intro h3 { margin: 0; font-size: 11px; font-weight: 600; }
.context-intro p { margin: 3px 0 0; color: var(--text-muted); font-size: 8.7px; line-height: 1.5; }
.file-card { display: flex; align-items: center; gap: 7px; width: 100%; padding: 6px 4px; border-radius: 5px; color: var(--text-primary); background: transparent; text-align: left; }
.file-card:hover { background: var(--surface-hover); }
.file-thumb { width: 31px; height: 34px; flex: 0 0 auto; display: flex; flex-direction: column; gap: 3px; padding: 5px; overflow: hidden; border: 1px solid var(--border-muted); border-radius: 4px; color: var(--text-brand); background: var(--surface-raised); }
.file-thumb svg { width: 11px; height: 11px; }
.file-thumb i { display: block; height: 2px; width: 100%; background: color-mix(in srgb, var(--text-muted) 20%, transparent); }
.file-thumb i:nth-of-type(2) { width: 70%; }
.file-card > span:nth-child(2) { display: flex; min-width: 0; flex: 1; flex-direction: column; gap: 2px; }
.file-card strong { overflow: hidden; font-size: 9.5px; font-weight: 550; text-overflow: ellipsis; white-space: nowrap; }
.file-card small { overflow: hidden; color: var(--text-muted); font-size: 8px; text-overflow: ellipsis; white-space: nowrap; }
.file-card > svg { color: var(--text-muted); }
.context-add { display: flex; align-items: center; gap: 5px; width: 100%; min-height: 25px; padding: 0 4px; color: var(--text-muted); background: transparent; font-size: 8.5px; text-align: left; }
.context-add:hover { color: var(--text-primary); }
.linked-work-section, .context-member-section, .context-linked { margin-top: 13px; padding-top: 10px; border-top: 1px solid var(--border-muted); }
.context-section-title { color: var(--text-muted); font-size: 8px; font-weight: 600; letter-spacing: .05em; }
.linked-work-row { display: flex; align-items: center; gap: 6px; width: 100%; padding: 6px 2px; color: var(--text-primary); background: transparent; text-align: left; }
.linked-work-row:hover { background: var(--surface-hover); }
.linked-work-row > span:nth-child(2) { display: flex; min-width: 0; flex: 1; flex-direction: column; gap: 1px; }
.linked-work-row strong { font-size: 9px; font-weight: 550; }
.linked-work-row small { color: var(--text-muted); font-size: 8px; }
.linked-work-row > svg { color: var(--text-muted); }
.context-attention { display: flex; flex-direction: column; gap: 3px; }
.context-attention button { display: flex; align-items: center; gap: 6px; width: 100%; padding: 6px 3px; border-radius: 4px; color: var(--text-primary); background: transparent; text-align: left; }
.context-attention button:hover { background: var(--surface-hover); }
.context-attention button > span:nth-child(2) { display: flex; flex: 1; flex-direction: column; gap: 1px; }
.context-attention strong { font-size: 9px; font-weight: 550; }
.context-attention small { color: var(--text-muted); font-size: 8px; }
.context-attention p { color: var(--text-muted); font-size: 8.5px; }
.context-member { display: flex; align-items: center; gap: 6px; min-height: 35px; }
.context-member > span:last-child { display: flex; min-width: 0; flex-direction: column; gap: 1px; }
.context-member strong { font-size: 9px; font-weight: 550; }
.context-member small { color: var(--text-muted); font-size: 8px; }
.context-linked > button { display: flex; align-items: center; gap: 6px; width: 100%; padding: 6px 2px; color: var(--text-muted); background: transparent; text-align: left; }
.context-linked > button:hover { background: var(--surface-hover); }
.context-linked > button > span { display: flex; min-width: 0; flex-direction: column; gap: 1px; }
.context-linked strong { color: var(--text-primary); font-size: 9px; font-weight: 550; }
.context-linked small { color: var(--text-muted); font-size: 8px; }
.settings-content { padding: 12px 10px; }
.settings-group { padding: 5px 0 10px; }
.settings-group + .settings-group { padding-top: 10px; border-top: 1px solid var(--border-muted); }
.settings-group > span { display: block; padding: 4px 5px; color: var(--text-muted); font-size: 8px; }
.settings-group button { display: flex; align-items: center; justify-content: space-between; width: 100%; min-height: 29px; padding: 0 5px; border-radius: 4px; color: var(--text-secondary); background: transparent; font-size: 9.5px; text-align: left; }
.settings-group button:hover { color: var(--text-primary); background: var(--surface-hover); }

@media (max-width: 1200px) {
  .denv-app { --nav-width: 220px; --context-width: 280px; }
  .denv-workspace { gap: 8px; padding-right: 7px; }
  .message-context-button span { display: none; }
  .message-context-button { width: 26px; justify-content: center; padding: 0; }
}

@media (max-width: 980px) {
  .denv-app { --nav-width: 210px; }
  .denv-workspace.context-open { grid-template-columns: var(--rail-width) var(--nav-width) minmax(0, 1fr); }
  .context-panel, .settings-panel { display: none; }
  .denv-workspace { gap: 7px; padding-right: 7px; }
  .needs-you-grid, .home-columns, .space-overview-grid, .agent-card-grid { grid-template-columns: 1fr; }
  .space-panel.wide { grid-column: auto; }
}

@media (max-width: 720px) {
  .denv-app { --rail-width: 0px; --nav-width: 0px; }
  .denv-workspace, .denv-workspace.context-open { grid-template-columns: minmax(0, 1fr); padding: 6px; }
  .app-rail, .denv-navigation { display: none; }
  .surface-header { padding: 17px 16px 12px; }
  .home-scroll { padding: 15px 16px; }
  .message-stream { padding-left: 12px; padding-right: 12px; }
  .message-composer-bar { padding-left: 8px; padding-right: 8px; }
  .home-conversation-meta > span { display: none; }
  .work-table-head, .work-table-row { grid-template-columns: minmax(190px, 1fr) .7fr .8fr; }
  .work-table-head > :nth-child(n+4), .work-table-row > :nth-child(n+4) { display: none; }
}

@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { animation-duration: .001ms !important; transition-duration: .001ms !important; }
}
</style>
