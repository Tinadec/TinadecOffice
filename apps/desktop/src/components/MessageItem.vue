<script setup lang="ts">
import { Copy, Check, Pencil, Bot, Clock } from '@lucide/vue'
import { computed, ref } from 'vue'
import { UiButton } from '@/components/ui'
import MarkdownRender from './MarkdownRender.vue'
import ThinkingProcess from './chat/ThinkingProcess.vue'
import ToolCallCard from './chat/ToolCallCard.vue'
import type { MessageDto } from '../api'
import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

const props = defineProps<{
  message: MessageDto
  index: number
  thinkingSteps?: ThinkingStep[]
  toolCalls?: ToolCall[]
  agentLabel?: string | null
}>()

const emit = defineEmits<{
  approve: [approvalId: string]
  reject: [approvalId: string]
}>()

const copied = ref(false)
const isEditing = ref(false)
const editContent = ref('')

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
  // TODO: emit edit event
  isEditing.value = false
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
</script>

<template>
  <article class="message-wrapper" :class="message.role">
    <!-- AI 消息：Markdown 渲染，无头像无对话框 -->
    <template v-if="message.role === 'assistant'">
      <div class="assistant-message-row">
        <div class="message-content assistant">
          <!-- Agent 标签和时间戳 -->
          <div v-if="agentLabel || timeLabel" class="assistant-meta-row">
            <div class="assistant-agent-tag">
              <Bot :size="11" />
              <span>{{ agentLabel ?? '智能体' }}</span>
            </div>
            <span v-if="timeLabel" class="assistant-time">
              <Clock :size="10" />
              {{ timeLabel }}
            </span>
          </div>

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
        </div>
      </div>
    </template>

    <!-- 用户消息：对话框气泡 + 左侧操作按钮 -->
    <template v-else>
      <div class="user-message-row">
        <!-- 左侧操作按钮 -->
        <div class="user-message-actions">
          <UiButton variant="ghost" size="icon" class="message-action-btn" :title="$t('chat.copy')" @click="handleCopy">
            <Check v-if="copied" :size="12" />
            <Copy v-else :size="12" />
          </UiButton>
          <UiButton variant="ghost" size="icon" class="message-action-btn" :title="$t('chat.edit')" @click="startEdit">
            <Pencil :size="12" />
          </UiButton>
        </div>

        <!-- 对话框气泡 -->
        <div class="message-content user">
          <template v-if="isEditing">
            <textarea v-model="editContent" class="edit-textarea" rows="3" />
            <div class="edit-actions">
              <UiButton variant="ghost" size="sm" @click="cancelEdit">{{ $t('common.cancel') }}</UiButton>
              <UiButton variant="default" size="sm" @click="saveEdit">{{ $t('common.save') }}</UiButton>
            </div>
          </template>
          <template v-else>
            <p>{{ message.content }}</p>
            <div v-if="timeLabel" class="user-message-time">
              <Clock :size="10" />
              {{ timeLabel }}
            </div>
          </template>
        </div>
      </div>
    </template>
  </article>
</template>
