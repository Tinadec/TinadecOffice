<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type ApprovalDto } from '@/api'
import ApprovalGateStatus from '@/components/approval/ApprovalGateStatus.vue'
import { Spinner } from '@/components/ui/spinner'
import { homeController } from '@/controllers/HomeController'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import { useDelayedLoading } from '@/composables/useDelayedLoading'
import { useNotifications } from '@/composables/useNotifications'
import { entrySentence, verbKey } from '@/lib/conversation/toolSentence'
import type { SubAgent, ToolEntry } from '@/lib/conversation/reducer'

/**
 * A call waiting on a person, always outside any fold and naming the sub-agent that asked.
 * The facts are Core's approval evidence (command, directory, target), never raw parameters.
 *
 * Approve · Approve for this run (scope=run) · Reject · Do this instead (reject, then send the
 * correction into the run as an insert) · for a shell call, Always allow commands starting with
 * its first word (a command-prefix rule, then approve this one). ⌘/Ctrl+Enter approves,
 * ⌘/Ctrl+Backspace rejects, Esc closes the correction field.
 */
const props = defineProps<{
  entry: ToolEntry
  runId: string
  requester?: SubAgent | null
  approval?: ApprovalDto | null
  workspaceRoot?: string | null
}>()
const { t } = useI18n()
const { notify } = useNotifications()
const { displayName } = useAgentDirectory()

type Action = 'approve' | 'approveRun' | 'reject' | 'instead' | 'rule'
const pending = ref<Action | null>(null)
const busy = computed(() => pending.value !== null)
const showSpinner = useDelayedLoading(busy)
const correcting = ref(false)
const correction = ref('')
const correctionField = ref<HTMLTextAreaElement | null>(null)

const sentence = computed(() => entrySentence(props.entry, props.workspaceRoot))
const question = computed(() => t(verbKey(sentence.value.verb, 'done'), { subject: sentence.value.subject ?? props.entry.toolId }))
const command = computed(() => props.approval?.command ?? props.entry.command)
const directory = computed(() => props.approval?.cwd ?? props.entry.cwd)
const target = computed(() => (command.value ? null : props.approval?.resource_path ?? props.entry.resourcePath))
const approvalId = computed(() => props.approval?.id ?? props.entry.approvalId)
const requesterName = computed(() => (props.requester ? displayName(props.requester.handle ?? props.requester.agentSlug) : ''))
const commandPrefix = computed(() => (sentence.value.verb === 'run' && command.value ? command.value.trim().split(/\s+/)[0] ?? null : null))

async function act(action: Action, work: (id: string) => Promise<unknown>) {
  const id = approvalId.value
  if (!id || busy.value) return
  pending.value = action
  try {
    await work(id)
  } catch (error) {
    notify.error(error, { title: t('conversation.approval.failed') })
  } finally {
    pending.value = null
  }
}

const approve = () => act('approve', (id) => homeController.decideApprovalById(id, 'approved'))
const approveRun = () => act('approveRun', (id) => homeController.decideApprovalById(id, 'approved', 'run'))
const reject = () => act('reject', (id) => homeController.decideApprovalById(id, 'rejected'))

async function openCorrection() {
  correcting.value = true
  await nextTick()
  correctionField.value?.focus()
}

const sendCorrection = () => act('instead', async (id) => {
  const content = correction.value.trim()
  const sessionId = homeController.currentSession.value?.id
  if (!content || !sessionId) return
  await homeController.decideApprovalById(id, 'rejected')
  await api.createInteraction(sessionId, { content, client_message_id: crypto.randomUUID(), dispatch_mode: 'insert', target_run_id: props.runId })
  correcting.value = false
  correction.value = ''
})

const allowPrefix = () => act('rule', async (id) => {
  const prefix = commandPrefix.value
  if (!prefix) return
  await homeController.createApprovalRule({ kind: 'command_prefix', tool_id: props.entry.toolId, pattern: prefix, session_id: homeController.currentSession.value?.id ?? null })
  await homeController.decideApprovalById(id, 'approved')
})

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape' && correcting.value) {
    event.preventDefault()
    correcting.value = false
  } else if ((event.metaKey || event.ctrlKey) && event.key === 'Enter') {
    event.preventDefault()
    if (correcting.value) void sendCorrection()
    else void approve()
  } else if ((event.metaKey || event.ctrlKey) && event.key === 'Backspace' && !correcting.value) {
    event.preventDefault()
    void reject()
  }
}
</script>

<template>
  <section class="conversation-card conversation-approval" role="group" :aria-label="t('conversation.approval.title')" :aria-busy="busy ? 'true' : undefined" data-testid="approval-card" @keydown="onKeydown">
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
    <ApprovalGateStatus v-if="approvalId" :approval-id="approvalId" />
    <div v-if="correcting" class="conversation-approval-correction">
      <textarea ref="correctionField" v-model="correction" rows="2" class="conversation-edit-input" data-testid="approval-correction" :aria-label="t('conversation.approval.instead')" :placeholder="t('conversation.approval.insteadPlaceholder')" />
      <div class="conversation-approval-actions">
        <button type="button" class="conversation-text-button is-primary" :disabled="busy || !correction.trim()" data-testid="approval-send-correction" @click="sendCorrection">{{ t('conversation.approval.sendInstead') }}</button>
        <button type="button" class="conversation-text-button" :disabled="busy" @click="correcting = false">{{ t('common.cancel') }}</button>
      </div>
    </div>
    <div v-else-if="approvalId" class="conversation-approval-actions">
      <button type="button" class="conversation-text-button is-primary" :disabled="busy" data-testid="approval-approve" :title="t('conversation.approval.approveShortcut')" @click="approve">
        <Spinner decorative v-if="showSpinner && pending === 'approve'" class="conversation-meta-glyph" />{{ t('approval.approve') }}
      </button>
      <button type="button" class="conversation-text-button" :disabled="busy" data-testid="approval-approve-run" @click="approveRun">
        <Spinner decorative v-if="showSpinner && pending === 'approveRun'" class="conversation-meta-glyph" />{{ t('conversation.approval.approveRun') }}
      </button>
      <button type="button" class="conversation-text-button" :disabled="busy" data-testid="approval-reject" :title="t('conversation.approval.rejectShortcut')" @click="reject">
        <Spinner decorative v-if="showSpinner && pending === 'reject'" class="conversation-meta-glyph" />{{ t('approval.reject') }}
      </button>
      <button type="button" class="conversation-text-button" :disabled="busy" data-testid="approval-instead" @click="openCorrection">{{ t('conversation.approval.instead') }}</button>
      <button v-if="commandPrefix" type="button" class="conversation-link-button" :disabled="busy" data-testid="approval-allow-prefix" @click="allowPrefix">
        <Spinner decorative v-if="showSpinner && pending === 'rule'" class="conversation-meta-glyph" />{{ t('conversation.approval.allowPrefix', { prefix: commandPrefix }) }}
      </button>
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
.conversation-approval-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-top: 2px; }
:global(.chat-ultra) .conversation-approval-facts { grid-template-columns: 1fr; }
.conversation-approval-correction { display: flex; flex-direction: column; gap: 6px; }
.conversation-approval-actions .conversation-text-button { display: inline-flex; align-items: center; gap: 4px; }
</style>
