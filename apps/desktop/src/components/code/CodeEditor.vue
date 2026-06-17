<script setup lang="ts">
import { Check, Edit, Redo, Save, Undo, Wand2, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, type ApprovalDto } from '@/api'
import { detectLanguage, useMonaco } from '@/composables/useMonaco'
import { UiButton } from '@/components/ui'

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

const containerRef = ref<HTMLDivElement | null>(null)
const loading = ref(false)
const saving = ref(false)
const error = ref<string | null>(null)
const feedback = ref<string | null>(null)
const content = ref(props.initialContent ?? '')
const originalContent = ref(props.initialContent ?? '')
const fileSize = ref<number | null>(null)
const modifiedAt = ref<string | null>(null)
const pendingApprovalId = ref<string | null>(null)

let editor: import('monaco-editor').editor.IStandaloneCodeEditor | null = null
let model: import('monaco-editor').editor.ITextModel | null = null

const language = computed(() => detectLanguage(props.filePath))
const isDirty = computed(() => content.value !== originalContent.value)
const pendingApproval = computed(() =>
  props.approvals?.find((a) => a.id === pendingApprovalId.value) ?? null,
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
  error.value = null
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
    error.value = err instanceof Error ? err.message : 'Failed to load file'
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

  if (!props.selectedSessionId) {
    error.value = 'A session is required to save files (approval flow).'
    return
  }

  saving.value = true
  error.value = null
  feedback.value = null
  try {
    const approval = await api.createApproval({
      session_id: props.selectedSessionId,
      kind: 'code',
      summary: `Save file: ${props.filePath}`,
      command: 'code_editor save',
      cwd: props.cwd,
    })
    pendingApprovalId.value = approval.id
    feedback.value = 'Approval requested. Awaiting decision...'
    emit('approval-requested', approval)
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to create approval'
  } finally {
    saving.value = false
  }
}

async function executeSave(): Promise<void> {
  if (!pendingApproval.value || pendingApproval.value.status !== 'approved') return
  if (!props.filePath) return

  saving.value = true
  error.value = null
  try {
    const result = await api.codeEditorSave(props.cwd, props.filePath, content.value, pendingApproval.value.id)
    const data = result.data as { size?: number; modified_at?: string }
    originalContent.value = content.value
    fileSize.value = typeof data.size === 'number' ? data.size : fileSize.value
    modifiedAt.value = typeof data.modified_at === 'string' ? data.modified_at : modifiedAt.value
    feedback.value = `Saved ${props.filePath}.`
    pendingApprovalId.value = null
    emit('saved', props.filePath)
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to save file'
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
    pendingApprovalId.value = null
  }
})

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

    <div v-if="error" class="px-3 py-2 text-sm text-destructive">{{ error }}</div>
    <div v-if="feedback" class="flex items-center gap-2 px-3 py-1.5 text-xs text-muted-foreground">
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
