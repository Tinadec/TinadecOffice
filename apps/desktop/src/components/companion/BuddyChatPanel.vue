<script setup lang="ts">
/**
 * The buddy chat surface for the trusted main window. Unlike the satellite window (which reaches
 * the Gateway through the main-process whitelist proxy), this runs in the main renderer and talks
 * to the Gateway directly via `companionApi`, exactly like the settings section. The conversation
 * is the buddy's idempotent private chat — it is never newly created, only ensured.
 */
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Send } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { useHostAccess } from '@/lib/hostAccess'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { companionApi, type CompanionMessage, type CompanionSummary } from '@/lib/companionApi'

const props = defineProps<{ companion: CompanionSummary }>()

const { t } = useI18n()
const { canAccessBackend } = useHostAccess()

const conversationId = ref<string | null>(null)
const messages = ref<CompanionMessage[]>([])
const draft = ref('')
const loading = ref(true)
const sending = ref(false)
const failure = ref<ErrorState | null>(null)
const thinking = ref(false)

let lastSeq = 0
let timer: ReturnType<typeof setTimeout> | null = null
let generation = 0
let disposed = false

const ordered = computed(() => [...messages.value].sort((a, b) => a.seq - b.seq))

function visible(): boolean {
  return typeof document === 'undefined' || document.visibilityState === 'visible'
}

async function ensureConversation() {
  const conversation = await companionApi.ensurePrivateConversation(props.companion.id)
  conversationId.value = conversation.id
}

async function pullMessages() {
  if (!conversationId.value) return
  const fresh = await companionApi.listMessages(conversationId.value, lastSeq || undefined, 200)
  if (fresh.length === 0) return
  const known = new Set(messages.value.map((item) => item.id))
  for (const message of fresh) {
    if (!known.has(message.id)) messages.value.push(message)
    if (message.seq > lastSeq) lastSeq = message.seq
  }
}

async function load() {
  if (!canAccessBackend.value) return
  const token = ++generation
  loading.value = true
  failure.value = null
  try {
    await ensureConversation()
    await pullMessages()
    if (token !== generation) return
    loading.value = false
  } catch (error) {
    if (token !== generation) return
    failure.value = toErrorState(error, t('buddy.loadFailed'))
    loading.value = false
  }
}

function schedule() {
  if (disposed) return
  if (timer) clearTimeout(timer)
  timer = setTimeout(async () => {
    timer = null
    if (disposed || !visible()) { schedule(); return }
    try {
      await pullMessages()
      if (conversationId.value) {
        const conversation = await companionApi.getConversation(conversationId.value)
        thinking.value = conversation.members.some((member) => member.member_kind === 'companion')
          && messages.value.length > 0
          && messages.value[messages.value.length - 1]?.author_kind === 'user'
      }
    } catch {
      // A background poll failure is not a user-facing error; the next tick retries.
    }
    schedule()
  }, 3000)
}

async function send() {
  const body = draft.value.trim()
  if (!body || !conversationId.value || sending.value) return
  sending.value = true
  const key = `buddy-${conversationId.value}-${Date.now()}`
  try {
    const result = await companionApi.sendMessage(conversationId.value, { body }, key)
    draft.value = ''
    const known = new Set(messages.value.map((item) => item.id))
    if (!known.has(result.message.id)) messages.value.push(result.message)
    if (result.message.seq > lastSeq) lastSeq = result.message.seq
    thinking.value = result.runs.length > 0
  } catch (error) {
    failure.value = toErrorState(error, t('buddy.loadFailed'))
  } finally {
    sending.value = false
  }
}

function authorName(message: CompanionMessage): string {
  if (message.author_kind === 'user') return t('buddy.you')
  if (message.author_kind === 'system') return t('buddy.systemAuthor')
  return message.author_name || props.companion.name
}

watch(() => props.companion.id, () => {
  messages.value = []
  lastSeq = 0
  conversationId.value = null
  void load()
})

onMounted(() => { void load(); schedule() })
onBeforeUnmount(() => {
  disposed = true
  generation++
  if (timer) clearTimeout(timer)
  timer = null
})
</script>

<template>
  <div class="buddy-chat">
    <header class="buddy-chat-head">
      <AgentPetAvatar :pet-slug="companion.avatar ?? ''" :size="32" :alt="companion.name" />
      <div class="buddy-chat-head-copy">
        <strong>{{ companion.name }}</strong>
        <span class="buddy-chat-tagline">{{ companion.tagline || t('buddy.title') }}</span>
      </div>
    </header>

    <div v-if="failure" role="alert" class="buddy-chat-error">
      <p>{{ failure.message }}</p>
      <UiButton variant="outline" size="sm" @click="load">{{ t('buddy.retry') }}</UiButton>
    </div>

    <div v-if="loading && messages.length === 0" class="buddy-chat-loading" role="status">{{ t('buddy.loading') }}</div>
    <ul v-else class="buddy-chat-messages" role="log" aria-live="polite">
      <li v-for="message in ordered" :key="message.id" class="buddy-msg" :class="`buddy-msg--${message.author_kind}`">
        <span class="buddy-msg-author">{{ authorName(message) }}</span>
        <p class="buddy-msg-body">{{ message.body }}</p>
      </li>
      <li v-if="thinking" class="buddy-msg buddy-msg--companion buddy-msg--thinking" role="status">{{ t('buddy.thinking') }}</li>
    </ul>

    <form class="buddy-chat-composer" @submit.prevent="send">
      <textarea
        v-model="draft"
        class="buddy-chat-input"
        rows="1"
        :placeholder="t('buddy.inputPlaceholder')"
        :disabled="!canAccessBackend || sending"
        @keydown.enter.exact.prevent="send"
      />
      <UiButton type="submit" size="sm" :disabled="!draft.trim() || sending || !canAccessBackend" :aria-label="t('buddy.send')">
        <Send :size="14" />
        <span>{{ t('buddy.send') }}</span>
      </UiButton>
    </form>
  </div>
</template>

<style scoped>
.buddy-chat {
  display: flex;
  flex-direction: column;
  gap: 12px;
  height: 100%;
  min-height: 0;
}

.buddy-chat-head {
  display: flex;
  align-items: center;
  gap: 10px;
}

.buddy-chat-head-copy {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.buddy-chat-head-copy strong {
  font-size: 14px;
  color: var(--text-primary);
}

.buddy-chat-tagline {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: var(--text-muted);
}

.buddy-chat-error {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--surface-section, var(--surface-raised));
  box-shadow: inset 3px 0 0 var(--accent-danger);
  font-size: 12px;
  color: var(--text-secondary);
}

.buddy-chat-error p {
  flex: 1;
  margin: 0;
}

.buddy-chat-loading {
  font-size: 12px;
  color: var(--text-muted);
}

.buddy-chat-messages {
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
  gap: 10px;
  margin: 0;
  padding: 4px;
  overflow-y: auto;
  list-style: none;
}

.buddy-msg {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-width: 85%;
}

.buddy-msg--user {
  align-self: flex-end;
  align-items: flex-end;
}

.buddy-msg-author {
  font-size: 11px;
  color: var(--text-muted);
}

.buddy-msg-body {
  margin: 0;
  padding: 8px 12px;
  border-radius: 12px;
  background: var(--surface-section, var(--surface-raised));
  font-size: 13px;
  line-height: 1.5;
  color: var(--text-primary);
  white-space: pre-wrap;
  word-break: break-word;
}

.buddy-msg--user .buddy-msg-body {
  background: var(--bg-user-msg, var(--surface-raised));
}

.buddy-msg--system .buddy-msg-body {
  background: transparent;
  font-size: 12px;
  color: var(--text-muted);
}

.buddy-msg--thinking .buddy-msg-body,
.buddy-msg--thinking {
  font-size: 12px;
  color: var(--text-muted);
}

.buddy-chat-composer {
  display: flex;
  align-items: flex-end;
  gap: 8px;
}

.buddy-chat-input {
  flex: 1;
  min-width: 0;
  resize: none;
  padding: 8px 12px;
  border-radius: 10px;
  border: 1px solid var(--border-input, var(--border-primary));
  background: var(--surface-input);
  font-size: 13px;
  line-height: 1.5;
  color: var(--text-primary);
}
</style>