<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Activity,
  CheckCircle2,
  MessageSquare,
  MessagesSquare,
  Pause,
  Play,
  Sparkles,
  UserPlus,
  Users,
  XCircle,
  type LucideIcon,
} from '@lucide/vue'
import type { CompanionActivity } from '@/lib/companionApi'
import {
  activity as activityRows,
  activeCompanions,
  conversations,
  failures,
  feedFilter,
  setFeedFilter,
  loading,
  refreshCompanionCenter,
  selectedCompanion,
  selectedCompanionId,
  useCompanionCenter,
} from '@/lib/companionStore'
import {
  feedRows,
  groupByDay,
  isAutonomous,
  needsAttention,
  relativeTime,
  type CompanionFeedFilter,
} from '@/lib/companionView'
import { companionErrorText } from './companionText'

const { t, locale } = useI18n()
const store = useCompanionCenter()
const filter = feedFilter

const failure = computed(() => (failures.value.activity ? companionErrorText(failures.value.activity, t) : ''))
const conversationKinds = computed(() =>
  new Map(conversations.value.map((conversation) => [conversation.id, conversation.kind] as const)))
const rows = computed(() => feedRows(activityRows.value, {
  companionId: selectedCompanionId.value,
  filter: filter.value,
  conversationKinds: conversationKinds.value,
}))
const days = computed(() => groupByDay(rows.value))

const thinkingCount = computed(() =>
  activeCompanions.value.filter((companion) => companion.current_activity === 'thinking').length)
const attentionCount = computed(() => activityRows.value.filter(needsAttention).length)
const autonomousToday = computed(() => {
  const today = new Date()
  return activityRows.value.filter((row) => {
    if (!isAutonomous(row)) return false
    const time = Date.parse(row.created_at)
    if (!Number.isFinite(time)) return false
    const date = new Date(time)
    return date.getFullYear() === today.getFullYear()
      && date.getMonth() === today.getMonth()
      && date.getDate() === today.getDate()
  }).length
})

const FILTERS: CompanionFeedFilter[] = ['all', 'attention', 'groups', 'autonomous']

function filterLabel(value: CompanionFeedFilter): string {
  if (value === 'attention') return t('companion.center.filterAttention')
  if (value === 'groups') return t('companion.center.filterGroups')
  if (value === 'autonomous') return t('companion.center.filterAutonomous')
  return t('companion.center.filterAll')
}

function dayLabel(key: string): string {
  if (key === 'unknown') return t('companion.center.dayUnknown')
  const today = new Date()
  const yesterday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1)
  if (key === localKey(today)) return t('companion.center.dayToday')
  if (key === localKey(yesterday)) return t('companion.center.dayYesterday')
  const [year, month, day] = key.split('-').map(Number)
  return new Date(year, (month ?? 1) - 1, day ?? 1)
    .toLocaleDateString(locale.value, { month: 'short', day: 'numeric' })
}

function localKey(date: Date): string {
  const month = `${date.getMonth() + 1}`.padStart(2, '0')
  const day = `${date.getDate()}`.padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}

const KIND_ICONS: Record<string, LucideIcon> = {
  group_created: MessagesSquare,
  autonomy_group_created: MessagesSquare,
  member_invited: UserPlus,
  member_left: Users,
  conversation_paused: Pause,
  conversation_resumed: Play,
  message_sent: MessageSquare,
  autonomy_message_sent: Sparkles,
  run_completed: CheckCircle2,
  run_failed: XCircle,
}

function kindIcon(row: CompanionActivity): LucideIcon {
  return KIND_ICONS[row.kind] ?? Activity
}

function text(payload: CompanionActivity['payload'], key: string): string {
  const value = payload?.[key]
  return typeof value === 'string' ? value : ''
}

function who(row: CompanionActivity): string {
  return row.companion_name || t('companion.center.whoMe')
}

interface EventLine {
  text: string
  /** The sentence already names the conversation, so the meta line must not repeat it. */
  namesConversation: boolean
}

/** One localized sentence per event kind; Core's Chinese `summary` is only a last resort. */
function eventLine(row: CompanionActivity): EventLine {
  const title = text(row.payload, 'title') || row.conversation_title || ''
  const speaker = who(row)
  switch (row.kind) {
    case 'group_created':
      return { text: t('companion.center.eventGroupCreated', { who: speaker, title }), namesConversation: true }
    case 'autonomy_group_created':
      return { text: t('companion.center.eventAutonomyGroupCreated', { who: speaker, title }), namesConversation: true }
    case 'member_invited':
      return {
        // `who` is the companion the activity is about: Core records the invited member, not the
        // inviter, so the sentence is "X joined" and needs no second name.
        text: t('companion.center.eventMemberInvited', { who: speaker, title }),
        namesConversation: true,
      }
    case 'member_left':
      return {
        text: t('companion.center.eventMemberLeft', { who: speaker, title }),
        namesConversation: true,
      }
    case 'conversation_paused':
      return {
        text: row.payload?.paused === false
          ? t('companion.center.eventResumed', { title })
          : t('companion.center.eventPaused', { title }),
        namesConversation: true,
      }
    case 'message_sent':
      return {
        text: t('companion.center.eventMessage', {
          who: speaker,
          preview: text(row.payload, 'preview') || row.summary || '',
        }),
        namesConversation: false,
      }
    case 'autonomy_message_sent':
      return {
        text: t('companion.center.eventAutonomyMessage', {
          who: speaker,
          preview: text(row.payload, 'preview') || row.summary || '',
        }),
        namesConversation: false,
      }
    case 'run_completed':
      return { text: t('companion.center.eventRunCompleted', { who: speaker }), namesConversation: false }
    case 'run_failed':
      return {
        text: t('companion.center.eventRunFailed', {
          who: speaker,
          code: text(row.payload, 'error_code') || 'unknown',
        }),
        namesConversation: false,
      }
    default:
      // An unknown kind keeps the server's own words instead of inventing a friendlier claim.
      return { text: row.summary || row.detail || row.kind, namesConversation: Boolean(row.conversation_title) }
  }
}
</script>

<template>
  <section class="companion-card">
    <header class="companion-card-head">
      <h2>{{ t('companion.center.feedTitle') }}</h2>
      <span class="companion-card-count">{{ rows.length }}</span>
    </header>

    <div class="companion-now">
      <span class="companion-now-item companion-now-thinking">
        {{ t('companion.center.nowThinking', { count: thinkingCount }) }}
      </span>
      <span v-if="selectedCompanion" class="companion-now-item companion-now-focus">
        {{ t('companion.center.nowFocus', { name: selectedCompanion.name }) }}
      </span>
      <span v-if="attentionCount" class="companion-now-item companion-now-attention">
        {{ t('companion.center.nowAttention', { count: attentionCount }) }}
      </span>
      <span class="companion-now-item">{{ t('companion.center.nowAutonomous', { count: autonomousToday }) }}</span>
    </div>

    <div class="companion-filters" role="tablist">
      <button
        v-for="option in FILTERS"
        :key="option"
        type="button"
        role="tab"
        class="companion-filter"
        :class="{ active: filter === option }"
        :aria-selected="filter === option"
        :data-testid="`companion-filter-${option}`"
        @click="setFeedFilter(option)"
      >{{ filterLabel(option) }}</button>
    </div>

    <p v-if="loading" class="companion-card-note">{{ t('companion.center.loading') }}</p>

    <div v-else-if="failure" class="companion-card-error" role="alert">
      <span>{{ failure }}</span>
      <button type="button" @click="refreshCompanionCenter">{{ t('companion.center.retry') }}</button>
    </div>

    <p v-else-if="rows.length === 0" class="companion-card-note">{{ t('companion.center.feedEmpty') }}</p>

    <div v-else class="companion-feed">
      <section v-for="day in days" :key="day.key" class="companion-day">
        <h3 class="companion-day-label">{{ dayLabel(day.key) }}</h3>
        <ol class="companion-feed-list">
          <li
            v-for="row in day.rows"
            :key="row.id"
            class="companion-feed-row"
            :class="{ attention: needsAttention(row) }"
          >
            <span class="companion-feed-icon"><component :is="kindIcon(row)" :size="14" /></span>
            <div class="companion-feed-body">
              <p class="companion-feed-summary">{{ eventLine(row).text }}</p>
              <p class="companion-feed-meta">
                <time>{{ relativeTime(row.created_at, locale) }}</time>
                <span v-if="!eventLine(row).namesConversation && row.conversation_title">
                  · {{ row.conversation_title }}
                </span>
              </p>
            </div>
          </li>
        </ol>
      </section>
    </div>
  </section>
</template>

<style scoped>
.companion-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  height: 100%;
  overflow-y: auto;
  padding: 18px 20px;
}

.companion-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.companion-card-head h2 {
  margin: 0;
  font-size: 14px;
  font-weight: 700;
  color: var(--text-primary);
}

.companion-card-count {
  font-size: 11px;
  color: var(--text-muted);
}

.companion-now {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 10px;
  font-size: 11.5px;
  color: var(--text-muted);
}

.companion-now-item {
  padding: 2px 8px;
  border-radius: 999px;
  background: var(--surface-raised);
}

.companion-now-focus {
  color: var(--text-primary);
}

.companion-now-attention {
  background: rgba(248, 81, 73, 0.16);
  color: #f09595;
}

.companion-filters {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.companion-filter {
  padding: 3px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  background: transparent;
  color: var(--text-muted);
  font-size: 11px;
  cursor: pointer;
}

.companion-filter:hover {
  color: var(--text-primary);
}

.companion-filter.active {
  border-color: var(--border-card);
  background: var(--surface-selected);
  color: var(--text-primary);
}

.companion-card-note {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-muted);
}

.companion-card-error {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-secondary);
}

.companion-card-error button {
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary);
  padding: 3px 10px;
  font-size: 11px;
  cursor: pointer;
}

.companion-feed {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.companion-day-label {
  margin: 0 0 2px;
  font-size: 10.5px;
  font-weight: 600;
  letter-spacing: 0.02em;
  color: var(--text-muted);
}

.companion-feed-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.companion-feed-row {
  display: flex;
  gap: 10px;
  padding: 8px 6px;
  border-radius: 8px;
}

.companion-feed-row:hover {
  background: var(--surface-hover);
}

.companion-feed-row.attention .companion-feed-icon {
  background: rgba(248, 81, 73, 0.16);
  color: #f09595;
}

.companion-feed-icon {
  display: grid;
  place-items: center;
  flex: none;
  width: 26px;
  height: 26px;
  border-radius: 7px;
  background: var(--surface-raised);
  color: var(--text-secondary);
}

.companion-feed-body {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.companion-feed-summary {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.companion-feed-meta {
  display: flex;
  gap: 5px;
  margin: 0;
  font-size: 10.5px;
  color: var(--text-muted);
}
</style>
