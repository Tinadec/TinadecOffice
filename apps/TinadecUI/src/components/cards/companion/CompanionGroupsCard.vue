<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Pause, Play, RefreshCw } from '@lucide/vue'
import { companionApi, type CompanionConversation, type CompanionMessage } from '@/lib/companionApi'
import {
  conversations,
  failures,
  loading,
  refreshCompanionCenter,
  selectedCompanion,
  selectedCompanionId,
  useCompanionCenter,
} from '@/lib/companionStore'
import { groupSections, relativeTime } from '@/lib/companionView'
import { companionErrorText, formatCompanionTime } from './companionText'

const MESSAGE_LIMIT = 20
const { t, locale } = useI18n()
const store = useCompanionCenter()

const openId = ref<string | null>(null)
const messages = ref<CompanionMessage[]>([])
const messagesLoading = ref(false)
const messagesError = ref<unknown>(null)
const pausing = ref<string | null>(null)
const pauseError = ref('')

const failure = computed(() => (failures.value.conversations ? companionErrorText(failures.value.conversations, t) : ''))
const sections = computed(() => groupSections(conversations.value, selectedCompanionId.value))
const openConversation = computed(() =>
  conversations.value.find((conversation) => conversation.id === openId.value) ?? null)

function sectionLabel(origin: 'user' | 'companion'): string {
  return origin === 'companion' ? t('companion.center.groupsByCompanions') : t('companion.center.groupsMine')
}

function titleOf(conversation: CompanionConversation): string {
  return conversation.title || t('companion.center.groupUntitled')
}

function membersOf(conversation: CompanionConversation): string {
  return conversation.members
    .filter((member) => member.status === 'active')
    .map((member) => member.display_name)
    .join(' · ')
}

function timeOf(iso: string | null | undefined): string {
  return formatCompanionTime(iso, locale.value)
}

async function loadMessages(conversationId: string): Promise<void> {
  messagesLoading.value = true
  messagesError.value = null
  try {
    messages.value = await companionApi.listMessages(conversationId, undefined, MESSAGE_LIMIT)
  } catch (failureLoad) {
    messages.value = []
    messagesError.value = failureLoad
  } finally {
    messagesLoading.value = false
  }
}

function select(conversation: CompanionConversation): void {
  if (openId.value === conversation.id) {
    openId.value = null
    messages.value = []
    messagesError.value = null
    return
  }
  openId.value = conversation.id
  void loadMessages(conversation.id)
}

// A poll can replace the page the open conversation lives on; close it rather than showing a
// transcript next to a row that no longer exists.
watch(conversations, (rows) => {
  if (openId.value && !rows.some((row) => row.id === openId.value)) {
    openId.value = null
    messages.value = []
  }
})

async function togglePause(conversation: CompanionConversation): Promise<void> {
  if (pausing.value) return
  pausing.value = conversation.id
  pauseError.value = ''
  try {
    await companionApi.pauseConversation(conversation.id, !conversation.autonomy_paused)
    await refreshCompanionCenter()
  } catch (error) {
    pauseError.value = companionErrorText(error, t)
  } finally {
    pausing.value = null
  }
}
</script>

<template>
  <section class="companion-card">
    <header class="companion-card-head">
      <h2>{{ t('companion.center.groupsTitle') }}</h2>
      <div class="companion-card-tools">
        <span v-if="selectedCompanion" class="companion-card-scope">{{ selectedCompanion.name }}</span>
        <button
          type="button"
          class="companion-card-refresh"
          :title="t('companion.center.retry')"
          :aria-label="t('companion.center.retry')"
          @click="store.refresh"
        >
          <RefreshCw :size="13" />
        </button>
      </div>
    </header>

    <p v-if="loading" class="companion-card-note">{{ t('companion.center.loading') }}</p>

    <div v-else-if="failure" class="companion-card-error" role="alert">
      <span>{{ failure }}</span>
      <button type="button" @click="store.refresh">{{ t('companion.center.retry') }}</button>
    </div>

    <p v-else-if="sections.length === 0" class="companion-card-note">{{ t('companion.center.groupsEmpty') }}</p>

    <template v-else>
      <div v-for="section in sections" :key="section.origin" class="companion-group-section">
        <h3 class="companion-group-label">{{ sectionLabel(section.origin) }}</h3>
        <div role="list">
          <div
            v-for="group in section.rows"
            :key="group.id"
            role="listitem"
            class="companion-group"
            :class="{ open: group.id === openId }"
          >
            <button type="button" class="companion-group-main" @click="select(group)">
              <span class="companion-group-head">
                <span class="companion-group-title">{{ titleOf(group) }}</span>
                <span v-if="group.autonomy_paused" class="companion-group-paused">
                  {{ t('companion.center.groupPaused') }}
                </span>
              </span>
              <span class="companion-group-members">{{ t('companion.center.groupMembers', { names: membersOf(group) }) }}</span>
              <span class="companion-group-preview">
                <span class="companion-group-time">{{ timeOf(group.last_message_at) }}</span>
                <span class="companion-group-text">{{ group.last_message_preview || '' }}</span>
              </span>
            </button>
            <button
              type="button"
              class="companion-group-pause"
              :disabled="pausing === group.id"
              :title="group.autonomy_paused ? t('companion.center.resumeGroup') : t('companion.center.pauseGroup')"
              :aria-label="group.autonomy_paused ? t('companion.center.resumeGroup') : t('companion.center.pauseGroup')"
              @click="togglePause(group)"
            >
              <Play v-if="group.autonomy_paused" :size="13" />
              <Pause v-else :size="13" />
            </button>
          </div>
        </div>
      </div>
    </template>

    <p v-if="pauseError" class="companion-card-error" role="alert">{{ pauseError }}</p>

    <div v-if="openConversation" class="companion-messages">
      <h3>{{ t('companion.center.groupMessages') }}</h3>
      <p v-if="messagesLoading" class="companion-card-note">{{ t('companion.center.loading') }}</p>
      <div v-else-if="messagesError" class="companion-card-error" role="alert">
        <span>{{ companionErrorText(messagesError, t) }}</span>
        <button type="button" @click="loadMessages(openConversation.id)">{{ t('companion.center.retry') }}</button>
      </div>
      <p v-else-if="messages.length === 0" class="companion-card-note">
        {{ t('companion.center.groupMessagesEmpty') }}
      </p>
      <template v-else>
        <ol class="companion-message-list">
          <li v-for="message in messages" :key="message.id" class="companion-message">
            <div class="companion-message-head">
              <span class="companion-message-author">{{ message.author_name }}</span>
              <time class="companion-message-time">{{ relativeTime(message.created_at, locale) }}</time>
            </div>
            <p class="companion-message-body">{{ message.body }}</p>
          </li>
        </ol>
        <p class="companion-readonly">
          {{ t('companion.center.groupMessageWindow', { shown: messages.length, total: openConversation.message_count }) }}
        </p>
      </template>
      <p class="companion-readonly">{{ t('companion.center.groupReadOnly') }}</p>
    </div>
    <p v-else-if="sections.length > 0" class="companion-card-note">
      {{ t('companion.center.groupMessagesPick') }}
    </p>
  </section>
</template>

<style scoped>
.companion-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  height: 100%;
  overflow-y: auto;
  padding: 14px;
}

.companion-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.companion-card-head h2 {
  margin: 0;
  font-size: 13px;
  font-weight: 700;
  color: var(--text-primary);
}

.companion-card-tools {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.companion-card-scope {
  padding: 1px 7px;
  border-radius: 999px;
  background: var(--surface-raised);
  font-size: 10.5px;
  color: var(--text-secondary);
}

.companion-card-refresh {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.companion-card-refresh:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.companion-card-note {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
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

.companion-group-section {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.companion-group-label {
  margin: 2px 0 0;
  font-size: 10.5px;
  font-weight: 600;
  color: var(--text-muted);
}

.companion-group {
  display: flex;
  align-items: stretch;
  gap: 4px;
  border: 1px solid transparent;
  border-radius: 8px;
}

.companion-group:hover {
  background: var(--surface-hover);
}

.companion-group.open {
  border-color: var(--border-card);
  background: var(--surface-selected);
}

.companion-group-main {
  display: flex;
  flex-direction: column;
  gap: 2px;
  flex: 1;
  min-width: 0;
  padding: 8px;
  border: none;
  background: transparent;
  text-align: left;
  color: var(--text-primary);
  cursor: pointer;
}

.companion-group-pause {
  display: grid;
  place-items: center;
  flex: none;
  width: 28px;
  margin: 6px 6px 6px 0;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.companion-group-pause:hover:not(:disabled) {
  color: var(--text-primary);
  background: var(--surface-raised);
}

.companion-group-head {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.companion-group-title {
  font-size: 12.5px;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-group-paused {
  flex: none;
  padding: 0 6px;
  border-radius: 999px;
  background: var(--surface-raised);
  color: var(--text-muted);
  font-size: 10px;
}

.companion-group-members,
.companion-group-text {
  font-size: 11px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-group-preview {
  display: flex;
  gap: 8px;
  min-width: 0;
}

.companion-group-time {
  flex: none;
  font-size: 10.5px;
  color: var(--text-muted);
}

.companion-messages {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 6px;
  padding-top: 8px;
  border-top: 1px solid var(--border-muted);
}

.companion-messages h3 {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary);
}

.companion-message-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.companion-message-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
}

.companion-message-author {
  font-size: 11.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.companion-message-time {
  font-size: 10.5px;
  color: var(--text-muted);
}

.companion-message-body {
  margin: 1px 0 0;
  font-size: 11.5px;
  line-height: 1.45;
  color: var(--text-secondary);
  overflow-wrap: anywhere;
  white-space: pre-wrap;
}

.companion-readonly {
  margin: 4px 0 0;
  font-size: 10.5px;
  line-height: 1.4;
  color: var(--text-muted);
}
</style>
