<script setup lang="ts">
import { Check, Edit, Redo, Save, Undo, Wand2, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, createUserToolActionForPath, type ApprovalDto, type UserToolActionDto } from '@/api'
import { detectLanguage, useMonaco } from '@/composables/useMonaco'
import { UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import {
  userToolActionIdempotencyKey,
  userToolActionNeedsDecision,
  userToolActionStatusMessage,
  userToolActionToApproval,
  userToolApprovalId,
} from '@/userToolAction'

const props = defineProps<{
  cwd: string
  filePath: string
  initialContent?: string
  selectedSessionId?: string | null
  approvals?: ApprovalDto[]
  fontSize?: number
  wordWrap?: 'on' | 'off'
  tabSize?: number
}>()

const emit = defineEmits<{
  'approval-requested': [approval: ApprovalDto]
  saved: [filePath: string]
  cancel: []
}>()

const { getMonaco, isDark } = useMonaco()
const { notify } = useNotifications()

const containerRef = ref<HTMLDivElement | null>(null)
const loading = ref(false)
const saving = ref(false)
const feedback = ref<string | null>(null)
const content = ref(props.initialContent ?? '')
const originalContent = ref(props.initialContent ?? '')
const fileSize = ref<number | null>(null)
const modifiedAt = ref<string | null>(null)
const pendingApprovalId = ref<string | null>(null)
const pendingAction = ref<UserToolActionDto | null>(null)
const lastPublishedAction = ref<string | null>(null)

let editor: import('monaco-editor').editor.IStandaloneCodeEditor | null = null
let model: import('monaco-editor').editor.ITextModel | null = null

const language = computed(() => detectLanguage(props.filePath))
const isDirty = computed(() => content.value !== originalContent.value)
const pendingApproval = computed(() =>
  (pendingAction.value && userToolActionNeedsDecision(pendingAction.value.status)
    ? props.approvals?.find((a) => a.id === pendingApprovalId.value)
    : null)
    ?? (pendingAction.value && userToolActionNeedsDecision(pendingAction.value.status)
      ? userToolActionToApproval(pendingAction.value, `Save file: ${props.filePath}`, {
          sessionId: props.selectedSessionId,
          cwd: props.cwd,
        })
      : null),
)
const canSave = computed(() => isDirty.value && !saving.value && !!props.filePath)

function formatSize(bytes: number | null): string {
  if (bytes === null) return '-'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

async function loadFile(): Promise<void> {
  if (!props.filePath) return
  loading.value = true
  try {
    const result = await api.codeEditorOpen(props.cwd, props.filePath)
    const data = result.data as {
      content?: string
      size?: number
      modified_at?: string
    }
    content.value = typeof data.content === 'string' ? data.content : ''
    originalContent.value = content.value
    fileSize.value = typeof data.size === 'number' ? data.size : null
    modifiedAt.value = typeof data.modified_at === 'string' ? data.modified_at : null
    await renderEditor()
  } catch (err) {
    notify.error(err, { title: 'Failed to load file', source: 'code', key: 'code-editor-load' })
  } finally {
    loading.value = false
  }
}

async function renderEditor(): Promise<void> {
  if (!containerRef.value) return
  const monaco = await getMonaco()

  if (model) {
    model.dispose()
    model = null
  }

  model = monaco.editor.createModel(content.value, language.value)

  if (editor) {
    editor.setModel(model)
    return
  }

  editor = monaco.editor.create(containerRef.value, {
    model,
    readOnly: false,
    theme: isDark.value ? 'vs-dark' : 'vs',
    automaticLayout: true,
    fontSize: props.fontSize ?? 13,
    lineNumbers: 'on',
    minimap: { enabled: true },
    scrollBeyondLastLine: false,
    wordWrap: props.wordWrap ?? 'off',
    tabSize: props.tabSize ?? 2,
    renderWhitespace: 'selection',
    bracketPairColorization: { enabled: true },
    smoothScrolling: true,
    autoClosingBrackets: 'always',
  })

  editor.onDidChangeModelContent(() => {
    if (model) {
      content.value = model.getValue()
    }
  })

  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
    void handleSave()
  })
}

async function handleSave(): Promise<void> {
  if (!canSave.value || !props.filePath) return

  saving.value = true
  feedback.value = null
  try {
    const fileHash = await resolveFileHash()
    const params: Record<string, unknown> = {
      filepath: props.filePath,
      content: content.value,
    }
    if (fileHash) params.file_hash = fileHash
    const idempotencyKey = await userToolActionIdempotencyKey('desktop:code-editor:save', {
      cwd: props.cwd,
      file_path: props.filePath,
      file_hash: fileHash,
      content: content.value,
    })
    const action = await createUserToolActionForPath(
      props.cwd,
      'write_file',
      params,
      idempotencyKey,
    )
    publishAction(action)
    await handleActionStatus(action)
  } catch (err) {
    notify.error(err, { title: 'Failed to request file save', source: 'code', key: 'code-editor-action' })
  } finally {
    saving.value = false
  }
}

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
  const approval = userToolActionToApproval(action, `Save file: ${props.filePath}`, {
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
  feedback.value = userToolActionStatusMessage(action, `Save ${props.filePath}`)
  if (action.status === 'completed') {
    await finishSave(action)
    return
  }
  if (action.status === 'blocked' || action.status === 'failed') {
    notify.error(action.message ?? action.status, { title: 'Save blocked', source: 'code', key: 'code-editor-action' })
    return
  }
  if (action.status === 'outcome_unknown') {
    notify.warning({ message: feedback.value, source: 'code', key: 'code-editor-outcome-unknown' })
  }
}

async function finishSave(action: UserToolActionDto): Promise<void> {
  if (action.status !== 'completed') return
  const result = action.result ?? {}
  originalContent.value = content.value
  fileSize.value = typeof result.size === 'number' ? result.size : new TextEncoder().encode(content.value).byteLength
  modifiedAt.value = typeof result.modified_at === 'string' ? result.modified_at : new Date().toISOString()
  notify.success(`Saved ${props.filePath}.`)
  pendingAction.value = null
  pendingApprovalId.value = null
  lastPublishedAction.value = null
  emit('saved', props.filePath)
}

async function executeSave(): Promise<void> {
  if (!pendingAction.value || !pendingApproval.value || pendingApproval.value.status !== 'approved') return
  if (!props.filePath) return

  saving.value = true
  feedback.value = null
  try {
    const action = await api.resumeUserToolAction(pendingAction.value.id)
    pendingAction.value = action
    pendingApprovalId.value = userToolApprovalId(action)
    publishAction(action)
    await handleActionStatus(action)
  } catch (err) {
    notify.error(err, { title: 'Failed to save file', source: 'code', key: 'code-editor-save' })
  } finally {
    saving.value = false
  }
}

function handleFormat(): void {
  if (!editor) return
  editor.getAction('editor.action.formatDocument')?.run()
}

function handleUndo(): void {
  if (!editor) return
  editor.trigger('toolbar', 'undo', null)
}

function handleRedo(): void {
  if (!editor) return
  editor.trigger('toolbar', 'redo', null)
}

function handleCancel(): void {
  emit('cancel')
}

watch(pendingApproval, (approval) => {
  if (approval && approval.status === 'approved') {
    void executeSave()
  } else if (approval && approval.status === 'rejected') {
    feedback.value = 'Save approval was rejected.'
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
    // The existing status remains visible until the next approval/event refresh.
  }
}

watch(() => props.approvals, () => {
  const action = pendingAction.value
  if (!action || !action.permission_request_id || action.action_approval_id) return
  void api.getUserToolAction(action.id).then((latest) => {
    if (latest.status === action.status && latest.authorization_decision_id === action.authorization_decision_id) return
    publishAction(latest)
    if (latest.status === 'completed') void finishSave(latest)
    else void handleActionStatus(latest)
  }).catch(() => undefined)
}, { deep: true })

onMounted(() => {
  if (props.initialContent !== undefined) {
    void renderEditor()
  } else {
    void loadFile()
  }
})

onBeforeUnmount(() => {
  if (editor) {
    editor.dispose()
    editor = null
  }
  if (model) {
    model.dispose()
    model = null
  }
})

watch(() => [props.cwd, props.filePath], () => {
  void loadFile()
})

watch(language, (lang) => {
  if (model) {
    void getMonaco().then((monaco) => {
      monaco.editor.setModelLanguage(model!, lang)
    })
  }
})

defineExpose({
  getContent: () => content.value,
  isDirty,
})
</script>

<template>
  <div class="flex h-full flex-col">
    <div class="flex items-center gap-2 border-b border-border px-3 py-2">
      <Edit :size="14" class="text-muted-foreground" />
      <span class="truncate text-sm font-medium">{{ filePath }}</span>
      <span v-if="isDirty" class="text-xs text-amber-500">●</span>
      <div class="ml-auto flex items-center gap-1">
        <UiButton variant="ghost" size="icon" class="h-7 w-7" title="Undo" @click="handleUndo">
          <Undo :size="13" />
        </UiButton>
        <UiButton variant="ghost" size="icon" class="h-7 w-7" title="Redo" @click="handleRedo">
          <Redo :size="13" />
        </UiButton>
        <UiButton variant="ghost" size="icon" class="h-7 w-7" title="Format document" @click="handleFormat">
          <Wand2 :size="13" />
        </UiButton>
        <UiButton
          variant="default"
          size="sm"
          class="h-7"
          :disabled="!canSave"
          :title="isDirty ? 'Save (Ctrl+S)' : 'No changes'"
          @click="handleSave"
        >
          <Save :size="13" />
          <span>Save</span>
        </UiButton>
        <UiButton variant="ghost" size="icon" class="h-7 w-7" title="Close editor" @click="handleCancel">
          <X :size="13" />
        </UiButton>
      </div>
    </div>

    <div v-if="feedback" class="flex items-center gap-2 px-3 py-1.5 text-xs text-muted-foreground" aria-live="polite">
      <Check :size="12" />
      <span>{{ feedback }}</span>
    </div>

    <div class="relative flex-1">
      <div v-if="loading" class="absolute inset-0 z-10 flex items-center justify-center text-sm text-muted-foreground">
        Loading...
      </div>
      <div ref="containerRef" class="h-full w-full" />
    </div>
  </div>
</template>
