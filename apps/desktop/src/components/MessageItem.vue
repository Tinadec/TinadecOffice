<script setup lang="ts">
import { Copy, Check, Pencil, Clock, FileText } from '@lucide/vue'
import { computed, ref } from 'vue'
import { UiButton } from '@/components/ui'
import { api, type MessageDto } from '../api'
import type { MessageAttachmentSummaryDto } from '@/generated/client'
import { formatAttachmentBytes } from '@/lib/pendingAttachments'
import MarkdownRender from './MarkdownRender.vue'
import ThinkingProcess from './chat/ThinkingProcess.vue'
import ToolCallCard from './chat/ToolCallCard.vue'
import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

const props = defineProps<{
  message: MessageDto
  index: number
  thinkingSteps?: ThinkingStep[]
  toolCalls?: ToolCall[]
}>()

const emit = defineEmits<{
  approve: [approvalId: string]
  reject: [approvalId: string]
  edit: [payload: { id: string; content: string }]
}>()

const copied = ref(false)
const isEditing = ref(false)
const editContent = ref('')

// An optimistic row has a local `pending-…` id that Core never saw, so it cannot be
// the anchor of a revert. Only persisted user turns are editable.
const canEdit = computed(() => props.message.role === 'user' && !props.message.id.startsWith('pending-'))

function handleCopy() {
  navigator.clipboard.writeText(props.message.content)
  copied.value = true
  setTimeout(() => copied.value = false, 2000)
}

function startEdit() {
  editContent.value = props.message.content
  isEditing.value = true
}

function cancelEdit() {
  isEditing.value = false
}

function saveEdit() {
  const content = editContent.value.trim()
  isEditing.value = false
  // An empty "correction" means delete, which is not what revert does: it would cut
  // the turn and resend nothing, silently losing history the user did not ask to lose.
  if (!content) return
  emit('edit', { id: props.message.id, content })
}

function onEditKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    isEditing.value = false
  } else if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
    event.preventDefault()
    saveEdit()
  }
}

const timeLabel = computed(() => {
  if (!props.message.created_at) return null
  try {
    return new Date(props.message.created_at).toLocaleTimeString('zh-CN', {
      hour: '2-digit',
      minute: '2-digit',
    })
  } catch {
    return null
  }
})

const messageThinkingSteps = computed(() => props.thinkingSteps ?? [])
const messageToolCalls = computed(() => props.toolCalls ?? [])
const hasThinking = computed(() => messageThinkingSteps.value.length > 0)
const hasToolCalls = computed(() => messageToolCalls.value.length > 0)

// `?? []` is about the rows this component builds locally (optimistic bubbles, test
// fixtures), not about the wire: a persisted message always answers with an array.
const messageAttachments = computed(() => props.message.attachments ?? [])
const hasAttachments = computed(() => messageAttachments.value.length > 0)

/**
 * Always a Gateway route addressed by attachment id. Core keeps `content_reference`
 * to itself and decides inline vs download per media type, so navigating this URL
 * inherits that decision instead of re-guessing it here.
 */
function attachmentUrl(attachment: MessageAttachmentSummaryDto): string {
  return api.attachmentContentUrl(attachment.id)
}

/**
 * svg is excluded on purpose: it can carry script, and Core serves it as a download,
 * so an <img> here would be the one place a renderer could execute a user's file.
 */
function isThumbnail(attachment: MessageAttachmentSummaryDto): boolean {
  return attachment.media_type.startsWith('image/') && attachment.media_type !== 'image/svg+xml'
}
</script>

<template>
  <article class="message-wrapper" :class="message.role">
    <!-- AI 消息：Markdown 渲染，无头像无对话框；元信息在消息末尾 hover 揭示 -->
    <template v-if="message.role === 'assistant'">
      <div class="assistant-message-row">
        <div class="message-content assistant">
          <!-- 思考过程 -->
          <ThinkingProcess v-if="hasThinking" :steps="messageThinkingSteps" />

          <!-- 工具调用卡片 -->
          <div v-if="hasToolCalls" class="assistant-tool-calls">
            <ToolCallCard
              v-for="call in messageToolCalls"
              :key="call.id"
              :tool-call="call"
              @approve="emit('approve', $event)"
              @reject="emit('reject', $event)"
            />
          </div>

          <MarkdownRender :content="message.content" />

          <!-- 元信息：消息末尾，hover 消息时揭示（OpenCodeUI / Codex 式） -->
          <div v-if="timeLabel" class="assistant-message-meta">{{ timeLabel }}</div>
        </div>
      </div>
    </template>

    <!-- 运行工具证据：写进会话历史是为了让下一条消息能读到上一轮的工具体验，
         它不是用户说的话。渲染成静默证据块，不提供复制/编辑等「用户消息」操作。 -->
    <template v-else-if="message.role === 'tool_evidence'">
      <details class="tool-evidence-block" data-testid="tool-evidence">
        <summary>{{ $t('chat.toolEvidence') }}</summary>
        <pre class="tool-evidence-body">{{ message.content }}</pre>
      </details>
    </template>

    <!-- 用户消息：对话框气泡 + 左侧操作按钮 -->
    <template v-else>
      <div class="user-message-row">
        <!-- 左侧操作按钮 -->
        <div class="user-message-actions">
          <UiButton variant="ghost" size="icon" class="message-action-btn" :title="$t(copied ? 'chat.copied' : 'chat.copy')" :aria-label="$t('chat.copy')" @click="handleCopy">
            <Check v-if="copied" :size="11" />
            <Copy v-else :size="11" />
          </UiButton>
          <UiButton v-if="canEdit" variant="ghost" size="icon" class="message-action-btn" :title="$t('chat.edit')" :aria-label="$t('chat.edit')" @click="startEdit">
            <Pencil :size="11" />
          </UiButton>
        </div>

        <!-- 对话框气泡 -->
        <div class="message-content user">
          <template v-if="isEditing">
            <textarea
              v-model="editContent"
              class="edit-textarea"
              rows="3"
              data-testid="message-edit-input"
              :aria-label="$t('chat.edit')"
              @keydown="onEditKeydown"
            />
            <div class="edit-actions">
              <UiButton variant="ghost" size="sm" @click="cancelEdit">{{ $t('common.cancel') }}</UiButton>
              <UiButton variant="default" size="sm" data-testid="message-edit-save" @click="saveEdit">{{ $t('chat.applyEdit') }}</UiButton>
            </div>
          </template>
          <template v-else>
            <!-- An attachment-only turn has no body: Core appends it with an empty text
                 projection, so the paragraph is omitted rather than rendered as a blank
                 line inside the bubble. -->
            <p v-if="message.content">{{ message.content }}</p>
            <div v-if="timeLabel" class="user-message-time">
              <Clock :size="9" />
              {{ timeLabel }}
            </div>
          </template>
        </div>
      </div>
    </template>

    <!-- 附件条只渲染消息自带的投影：不解析正文、不猜类型，链接一律是网关按 id 寻址的
         路由，内联还是下载由 Core 决定。图片走 <img> 缩略图，其余显示名字与体积。 -->
    <ul
      v-if="hasAttachments"
      class="message-attachments"
      role="list"
      data-testid="message-attachments"
      :aria-label="$t('chat.attachments')"
    >
      <li v-for="attachment in messageAttachments" :key="attachment.id" class="message-attachment">
        <a
          :href="attachmentUrl(attachment)"
          :download="attachment.file_name"
          :title="attachment.file_name"
          :aria-label="attachment.file_name"
          class="message-attachment-link"
          :class="{ 'is-thumb': isThumbnail(attachment) }"
        >
          <img
            v-if="isThumbnail(attachment)"
            :src="attachmentUrl(attachment)"
            :alt="attachment.file_name"
            class="message-attachment-thumb"
            loading="lazy"
          />
          <template v-else>
            <FileText :size="11" aria-hidden="true" />
            <span class="message-attachment-name">{{ attachment.file_name }}</span>
            <span class="message-attachment-size">{{ formatAttachmentBytes(attachment.content_length) }}</span>
          </template>
        </a>
      </li>
    </ul>
  </article>
</template>
