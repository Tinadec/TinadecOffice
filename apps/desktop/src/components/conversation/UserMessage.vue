<script setup lang="ts">
import { Check, Copy, FileText, Pencil } from '@lucide/vue'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type MessageDto } from '@/api'
import type { MessageAttachmentSummaryDto } from '@/generated/client'
import { fileNameParts, formatAttachmentBytes } from '@/lib/pendingAttachments'
import { formatClock } from '@/lib/conversation/format'
import { Message, MessageContent, MessageFooter } from '@/components/ui/message'
import { Bubble, BubbleContent } from '@/components/ui/bubble'
import { Attachment, AttachmentContent, AttachmentDescription, AttachmentGroup, AttachmentMedia, AttachmentTitle, AttachmentTrigger } from '@/components/ui/attachment'

/**
 * The user's side of a turn: a soft tinted bubble, the files it carried, and copy / edit /
 * time revealed on hover (always reachable by keyboard).
 */
const props = defineProps<{ message: MessageDto }>()
const emit = defineEmits<{ edit: [payload: { id: string; content: string }] }>()
const { t, locale } = useI18n()

const copied = ref(false)
const editing = ref(false)
const draft = ref('')

// An optimistic row has a local `pending-…` id Core never saw, so it cannot anchor a revert.
const canEdit = computed(() => !props.message.id.startsWith('pending-'))
const clock = computed(() => (props.message.created_at ? formatClock(props.message.created_at, locale.value) : ''))
// `?? []` covers locally built rows (optimistic bubbles, fixtures); the wire always sends an array.
const attachments = computed(() => props.message.attachments ?? [])

function copy() {
  void navigator.clipboard?.writeText(props.message.content)
  copied.value = true
  setTimeout(() => { copied.value = false }, 2000)
}

function startEdit() {
  draft.value = props.message.content
  editing.value = true
}

function save() {
  const content = draft.value.trim()
  editing.value = false
  // An empty correction would cut the turn and resend nothing: history the user did not ask to lose.
  if (!content) return
  emit('edit', { id: props.message.id, content })
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    editing.value = false
  } else if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
    event.preventDefault()
    save()
  }
}

/** Always the Gateway route addressed by id; Core decides inline vs download per media type. */
function url(attachment: MessageAttachmentSummaryDto): string {
  return api.attachmentContentUrl(attachment.id)
}

/** svg can carry script and Core serves it as a download, so it is never an <img> here. */
function isThumbnail(attachment: MessageAttachmentSummaryDto): boolean {
  return attachment.media_type.startsWith('image/') && attachment.media_type !== 'image/svg+xml'
}
</script>

<template>
  <Message align="end" class="conversation-user" data-testid="user-message">
    <MessageContent class="conversation-user-content">
      <template v-if="editing">
        <textarea
          v-model="draft"
          class="conversation-edit-input"
          rows="3"
          data-testid="message-edit-input"
          :aria-label="t('chat.edit')"
          @keydown="onKeydown"
        />
        <div class="conversation-edit-actions">
          <button type="button" class="conversation-text-button" @click="editing = false">{{ t('common.cancel') }}</button>
          <button type="button" class="conversation-text-button is-primary" data-testid="message-edit-save" @click="save">{{ t('chat.applyEdit') }}</button>
        </div>
      </template>
      <Bubble v-else-if="message.content" variant="tinted" align="end">
        <BubbleContent><p class="conversation-user-text">{{ message.content }}</p></BubbleContent>
      </Bubble>

      <!-- Only the message's own projection: no body parsing, no type guessing, and never a storage path. -->
      <AttachmentGroup
        v-if="attachments.length"
        role="list"
        data-testid="message-attachments"
        :aria-label="t('chat.attachments')"
        class="conversation-attachments"
      >
        <Attachment v-for="attachment in attachments" :key="attachment.id" role="listitem" state="done" size="sm">
          <AttachmentMedia :variant="isThumbnail(attachment) ? 'image' : 'icon'">
            <img v-if="isThumbnail(attachment)" :src="url(attachment)" :alt="attachment.file_name" loading="lazy" />
            <FileText v-else aria-hidden="true" />
          </AttachmentMedia>
          <AttachmentContent>
            <AttachmentTitle class="conversation-attachment-name"><span class="conversation-attachment-stem">{{ fileNameParts(attachment.file_name).stem }}</span>{{ fileNameParts(attachment.file_name).extension }}</AttachmentTitle>
            <AttachmentDescription>{{ formatAttachmentBytes(attachment.content_length) }}</AttachmentDescription>
          </AttachmentContent>
          <AttachmentTrigger as="a" :href="url(attachment)" :download="attachment.file_name" :title="attachment.file_name" :aria-label="attachment.file_name" />
        </Attachment>
      </AttachmentGroup>
    </MessageContent>

    <MessageFooter v-if="!editing" class="conversation-hover-meta">
      <button type="button" class="conversation-icon-button" :aria-label="t(copied ? 'chat.copied' : 'chat.copy')" :title="t(copied ? 'chat.copied' : 'chat.copy')" @click="copy">
        <Check v-if="copied" :size="12" aria-hidden="true" />
        <Copy v-else :size="12" aria-hidden="true" />
      </button>
      <button v-if="canEdit" type="button" class="conversation-icon-button" :aria-label="t('chat.edit')" :title="t('chat.edit')" @click="startEdit">
        <Pencil :size="12" aria-hidden="true" />
      </button>
      <time v-if="clock" :datetime="message.created_at ?? undefined">{{ clock }}</time>
    </MessageFooter>
  </Message>
</template>

<style scoped>
.conversation-user { flex-direction: column; align-items: flex-end; }
.conversation-user-content { display: flex; flex-direction: column; align-items: flex-end; width: 100%; }
.conversation-user-text { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.conversation-attachments { display: flex; flex-wrap: wrap; justify-content: flex-end; }
.conversation-attachment-name { display: flex; max-width: 14rem; min-width: 0; white-space: nowrap; }
.conversation-attachment-stem { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.conversation-hover-meta { display: flex; align-items: center; gap: 4px; opacity: 0; transition: opacity 150ms cubic-bezier(0.23, 1, 0.32, 1); }
.conversation-user:hover .conversation-hover-meta,
.conversation-user:focus-within .conversation-hover-meta { opacity: 1; }
.conversation-edit-input { width: min(100%, 36rem); min-height: 4.5rem; padding: 8px 10px; border: 1px solid var(--border-card); border-radius: 10px; color: var(--text-primary); background: var(--surface-input); font: inherit; font-size: 13px; resize: vertical; }
.conversation-edit-actions { display: flex; justify-content: flex-end; gap: 6px; }
@media (prefers-reduced-motion: reduce) { .conversation-hover-meta { transition: none; } }
</style>
