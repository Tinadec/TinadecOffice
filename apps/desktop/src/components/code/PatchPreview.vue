<script setup lang="ts">
import { Check, GitCompare, ShieldCheck, X } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, createUserToolActionForPath, type ApprovalDto, type UserToolActionDto } from '@/api'
import { detectLanguage, useMonaco } from '@/composables/useMonaco'
import { UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import {
  userToolActionIdempotencyKey,
  userToolActionNeedsDecision,
  userToolActionDetail,
  userToolActionReason,
  userToolActionStatusMessage,
  userToolActionToApproval,
  userToolApprovalId,
} from '@/userToolAction'

const props = defineProps<{
  cwd: string
  filePath: string
  originalContent: string
  modifiedContent: string
  selectedSessionId?: string | null
  approvals?: ApprovalDto[]
}>()

const emit = defineEmits<{
  'approval-requested': [approval: ApprovalDto]
  applied: [filePath: string]
  cancel: []
}>()

const { t, te } = useI18n()
const { getMonaco, isDark } = useMonaco()
const { notify } = useNotifications()

const containerRef = ref<HTMLDivElement | null>(null)
const loading = ref(false)
const applying = ref(false)
const feedback = ref<string | null>(null)
const pendingApprovalId = ref<string | null>(null)
const pendingAction = ref<UserToolActionDto | null>(null)
const lastPublishedAction = ref<string | null>(null)

let diffEditor: import('monaco-editor').editor.IStandaloneDiffEditor | null = null
let originalModel: import('monaco-editor').editor.ITextModel | null = null
let modifiedModel: import('monaco-editor').editor.ITextModel | null = null

const language = computed(() => detectLanguage(props.filePath))
const hasChanges = computed(() => props.originalContent !== props.modifiedContent)
const pendingApproval = computed(() =>
  (pendingAction.value && userToolActionNeedsDecision(pendingAction.value.status)
    ? props.approvals?.find((a) => a.id === pendingApprovalId.value)
    : null)
    ?? (pendingAction.value && userToolActionNeedsDecision(pendingAction.value.status)
      ? userToolActionToApproval(pendingAction.value, t('code.patch.approval', { path: props.filePath }), {
          sessionId: props.selectedSessionId,
          cwd: props.cwd,
        })
      : null),
)

/**
 * Generate a Codex-style apply_patch string from the original and modified
 * content. Uses a simple line-by-line diff with 3 lines of context.
 */
function generatePatch(): string {
  const originalLines = props.originalContent.split('\n')
  const modifiedLines = props.modifiedContent.split('\n')
  const lines: string[] = ['*** Begin Patch', `*** Update File: ${props.filePath}`]

  // Simple diff: find the first and last differing lines
  let firstDiff = -1
  let lastDiff = -1
  const maxLen = Math.max(originalLines.length, modifiedLines.length)
  for (let i = 0; i < maxLen; i++) {
    if (originalLines[i] !== modifiedLines[i]) {
      if (firstDiff === -1) firstDiff = i
      lastDiff = i
    }
  }

  if (firstDiff === -1) {
    // No changes
    lines.push('*** End Patch')
    return lines.join('\n')
  }

  const contextLines = 3
  const start = Math.max(0, firstDiff - contextLines)
  const end = Math.min(maxLen - 1, lastDiff + contextLines)

  lines.push('@@')

  // Context before
  for (let i = start; i < firstDiff; i++) {
    lines.push(` ${originalLines[i] ?? ''}`)
  }

  // Removed lines
  for (let i = firstDiff; i <= Math.min(lastDiff, originalLines.length - 1); i++) {
    if (originalLines[i] !== modifiedLines[i]) {
      lines.push(`-${originalLines[i] ?? ''}`)
    }
  }

  // Added lines
  for (let i = firstDiff; i <= Math.min(lastDiff, modifiedLines.length - 1); i++) {
    if (originalLines[i] !== modifiedLines[i]) {
      lines.push(`+${modifiedLines[i] ?? ''}`)
    }
  }

  // Context after
  for (let i = lastDiff + 1; i <= end; i++) {
    if (i < modifiedLines.length) {
      lines.push(` ${modifiedLines[i] ?? ''}`)
    }
  }

  lines.push('*** End Patch')
  return lines.join('\n')
}

async function renderDiff(): Promise<void> {
  if (!containerRef.value) return
  const monaco = await getMonaco()

  // Monaco invariant: detach the widget's current models BEFORE disposing
  // them ("TextModel got disposed before DiffEditorWidget model got reset");
  // `setModel(null)` is the detach its own declaration documents.
  if (diffEditor) diffEditor.setModel(null)
  if (originalModel) originalModel.dispose()
  if (modifiedModel) modifiedModel.dispose()

  originalModel = monaco.editor.createModel(props.originalContent, language.value)
  modifiedModel = monaco.editor.createModel(props.modifiedContent, language.value)

  if (diffEditor) {
    diffEditor.setModel({ original: originalModel, modified: modifiedModel })
    return
  }

  diffEditor = monaco.editor.createDiffEditor(containerRef.value, {
    originalEditable: false,
    readOnly: true,
    theme: isDark.value ? 'vs-dark' : 'vs',
    automaticLayout: true,
    fontSize: 13,
    renderSideBySide: true,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
  })
  diffEditor.setModel({ original: originalModel, modified: modifiedModel })
}

async function handleApplyPatch(): Promise<void> {
  if (!hasChanges.value || !props.filePath) return

  applying.value = true
  feedback.value = null
  try {
    const patch = generatePatch()
    pendingPatch.value = patch
    const fileHash = await resolveFileHash()
    const actionParams: Record<string, unknown> = {
      filepath: props.filePath,
      content: props.modifiedContent,
    }
    if (fileHash) actionParams.file_hash = fileHash
    const idempotencyKey = await userToolActionIdempotencyKey('desktop:patch-preview:apply', {
      cwd: props.cwd,
      file_path: props.filePath,
      file_hash: fileHash,
      content: props.modifiedContent,
    })
    const action = await createUserToolActionForPath(
      props.cwd,
      'write_file',
      actionParams,
      idempotencyKey,
    )
    publishAction(action)
    await handleActionStatus(action)
  } catch (err) {
    notify.error(err, { title: t('code.patch.requestFailed'), source: 'code', key: 'code-patch-action' })
  } finally {
    applying.value = false
  }
}

const pendingPatch = ref<string | null>(null)

async function resolveFileHash(): Promise<string | null> {
  try {
    const result = await api.readFile(props.cwd, props.filePath)
    const data = result.data as { file_hash?: unknown }
    return typeof data.file_hash === 'string' && data.file_hash.length > 0 ? data.file_hash : null
  } catch {
    return null
  }
}

function publishAction(action: UserToolActionDto): void {
  pendingAction.value = action
  pendingApprovalId.value = userToolApprovalId(action)
  const approval = userToolActionToApproval(action, t('code.patch.approval', { path: props.filePath }), {
    sessionId: props.selectedSessionId,
    cwd: props.cwd,
  })
  const publicationKey = `${action.id}:${approval.id}:${action.status}`
  if (publicationKey !== lastPublishedAction.value && userToolActionNeedsDecision(action.status)) {
    lastPublishedAction.value = publicationKey
    emit('approval-requested', approval)
  }
}

async function handleActionStatus(action: UserToolActionDto): Promise<void> {
  feedback.value = userToolActionStatusMessage(action, t('code.patch.operation', { path: props.filePath }), { t, te })
  if (action.status === 'completed') {
    await finishPatch(action)
    return
  }
  if (action.status === 'blocked' || action.status === 'failed') {
    notify.error(userToolActionReason(action, { t, te }) ?? feedback.value, { title: t('code.patch.blocked'), details: userToolActionDetail(action, { t, te }), source: 'code', key: 'code-patch-action' })
    return
  }
  if (action.status === 'outcome_unknown') {
    notify.warning({ message: feedback.value, source: 'code', key: 'code-patch-outcome-unknown' })
  }
}

async function finishPatch(action: UserToolActionDto): Promise<void> {
  if (action.status !== 'completed') return
  notify.success(t('code.patch.applied', { path: props.filePath }))
  pendingAction.value = null
  pendingApprovalId.value = null
  pendingPatch.value = null
  lastPublishedAction.value = null
  emit('applied', props.filePath)
}

async function executePatch(): Promise<void> {
  if (!pendingAction.value || !pendingApproval.value || pendingApproval.value.status !== 'approved') return
  if (!pendingPatch.value || !props.filePath) return

  applying.value = true
  feedback.value = null
  try {
    const action = await api.resumeUserToolAction(pendingAction.value.id)
    pendingAction.value = action
    pendingApprovalId.value = userToolApprovalId(action)
    publishAction(action)
    await handleActionStatus(action)
  } catch (err) {
    notify.error(err, { title: t('code.patch.applyFailed'), source: 'code', key: 'code-patch-apply' })
  } finally {
    applying.value = false
  }
}

watch(pendingApproval, (approval) => {
  if (approval && approval.status === 'approved') {
    void executePatch()
  } else if (approval && approval.status === 'rejected') {
    feedback.value = t('code.patch.rejected')
    if (pendingAction.value) void refreshPendingAction()
  }
})

async function refreshPendingAction(): Promise<void> {
  const action = pendingAction.value
  if (!action) return
  try {
    const latest = await api.getUserToolAction(action.id)
    pendingAction.value = latest
    pendingApprovalId.value = userToolApprovalId(latest)
    publishAction(latest)
    await handleActionStatus(latest)
  } catch {
    // Keep the existing state visible until the next event/approval refresh.
  }
}

watch(() => props.approvals, () => {
  const action = pendingAction.value
  if (!action || !action.permission_request_id || action.action_approval_id) return
  void api.getUserToolAction(action.id).then((latest) => {
    if (latest.status === action.status && latest.authorization_decision_id === action.authorization_decision_id) return
    publishAction(latest)
    if (latest.status === 'completed') void finishPatch(latest)
    else void handleActionStatus(latest)
  }).catch(() => undefined)
}, { deep: true })

onBeforeUnmount(() => {
  if (diffEditor) {
    diffEditor.dispose()
    diffEditor = null
  }
  if (originalModel) {
    originalModel.dispose()
    originalModel = null
  }
  if (modifiedModel) {
    modifiedModel.dispose()
    modifiedModel = null
  }
})

watch(
  () => [props.originalContent, props.modifiedContent, props.filePath],
  () => {
    void renderDiff()
  },
  { immediate: true },
)
</script>

<template>
  <div class="flex h-full flex-col">
    <div class="flex items-center gap-2 border-b border-border px-3 py-2">
      <GitCompare :size="14" class="text-muted-foreground" />
      <span class="truncate text-sm font-medium">{{ t('code.patch.title', { path: filePath }) }}</span>
      <div class="ml-auto flex items-center gap-1">
        <UiButton
          variant="default"
          size="sm"
          class="h-7"
          :disabled="!hasChanges || applying"
          @click="handleApplyPatch"
        >
          <ShieldCheck :size="13" />
          <span>{{ t('code.patch.apply') }}</span>
        </UiButton>
        <UiButton variant="ghost" size="icon" class="h-7 w-7" :title="t('common.close')" @click="emit('cancel')">
          <X :size="13" />
        </UiButton>
      </div>
    </div>

    <div v-if="feedback" class="flex items-center gap-2 px-3 py-1.5 text-xs text-muted-foreground" aria-live="polite">
      <Check :size="12" />
      <span>{{ feedback }}</span>
    </div>
    <div v-if="!hasChanges" class="px-3 py-2 text-xs text-muted-foreground">
      {{ t('code.patch.noChanges') }}
    </div>

    <div class="relative flex-1">
      <div v-if="loading" class="absolute inset-0 z-10 flex items-center justify-center text-sm text-muted-foreground">
        {{ t('code.patch.loading') }}
      </div>
      <div ref="containerRef" class="h-full w-full" />
    </div>
  </div>
</template>
