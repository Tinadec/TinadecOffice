<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ApprovalDto } from '@/api'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import { entrySentence, verbKey } from '@/lib/conversation/toolSentence'
import type { SubAgent, ToolEntry } from '@/lib/conversation/reducer'

/**
 * A call waiting on a person. It is always rendered outside any fold, and it names the
 * sub-agent that asked when one did. The facts shown are Core's approval evidence
 * (command, working directory, target), never the raw parameters.
 */
const props = defineProps<{
  entry: ToolEntry
  requester?: SubAgent | null
  approval?: ApprovalDto | null
  workspaceRoot?: string | null
}>()
const emit = defineEmits<{ approve: [approvalId: string]; reject: [approvalId: string] }>()
const { t } = useI18n()
const { displayName } = useAgentDirectory()

const sentence = computed(() => entrySentence(props.entry, props.workspaceRoot))
const question = computed(() => t(verbKey(sentence.value.verb, 'done'), { subject: sentence.value.subject ?? props.entry.toolId }))
const command = computed(() => props.approval?.command ?? props.entry.command)
const directory = computed(() => props.approval?.cwd ?? props.entry.cwd)
const target = computed(() => (command.value ? null : props.approval?.resource_path ?? props.entry.resourcePath))
const approvalId = computed(() => props.approval?.id ?? props.entry.approvalId)
const requesterName = computed(() => (props.requester ? displayName(props.requester.handle ?? props.requester.agentSlug) : ''))
</script>

<template>
  <section class="conversation-card conversation-approval" role="group" :aria-label="t('conversation.approval.title')" data-testid="approval-card">
    <header class="conversation-approval-head">
      <span class="conversation-approval-title">{{ t('conversation.approval.title') }}</span>
      <span v-if="requesterName" class="conversation-approval-requester">{{ t('conversation.approval.requestedBy', { name: requesterName }) }}</span>
    </header>
    <p class="conversation-approval-question">{{ question }}</p>
    <dl class="conversation-approval-facts">
      <template v-if="command"><dt>{{ t('conversation.approval.command') }}</dt><dd><code>{{ command }}</code></dd></template>
      <template v-if="directory"><dt>{{ t('conversation.approval.directory') }}</dt><dd><code>{{ directory }}</code></dd></template>
      <template v-if="target"><dt>{{ t('conversation.approval.target') }}</dt><dd><code>{{ target }}</code></dd></template>
    </dl>
    <div v-if="approvalId" class="conversation-approval-actions">
      <button type="button" class="conversation-text-button is-primary" data-testid="approval-approve" @click="emit('approve', approvalId)">{{ t('approval.approve') }}</button>
      <button type="button" class="conversation-text-button" data-testid="approval-reject" @click="emit('reject', approvalId)">{{ t('approval.reject') }}</button>
    </div>
  </section>
</template>

<style scoped>
.conversation-approval { display: flex; flex-direction: column; gap: 6px; }
.conversation-approval-head { display: flex; align-items: baseline; gap: 8px; font-size: 12px; }
.conversation-approval-title { font-weight: 600; color: var(--text-primary); }
.conversation-approval-requester { color: var(--text-muted); font-size: 11px; }
.conversation-approval-question { margin: 0; font-size: 13px; color: var(--text-primary); overflow-wrap: anywhere; }
.conversation-approval-facts { display: grid; grid-template-columns: auto 1fr; gap: 2px 10px; margin: 0; font-size: 12px; }
.conversation-approval-facts dt { color: var(--text-muted); }
.conversation-approval-facts dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
.conversation-approval-facts code { font-size: 12px; white-space: pre-wrap; }
.conversation-approval-actions { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 2px; }
</style>
