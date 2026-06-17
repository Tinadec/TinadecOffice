<script setup lang="ts">
import { Check, GitCompare, ShieldCheck, X } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { api, type ApprovalDto } from '@/api'
import { detectLanguage, useMonaco } from '@/composables/useMonaco'
import { UiButton } from '@/components/ui'

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

const { getMonaco, isDark } = useMonaco()

const containerRef = ref<HTMLDivElement | null>(null)
const loading = ref(false)
const applying = ref(false)
const error = ref<string | null>(null)
const feedback = ref<string | null>(null)
const pendingApprovalId = ref<string | null>(null)

let diffEditor: import('monaco-editor').editor.IStandaloneDiffEditor | null = null
let originalModel: import('monaco-editor').editor.ITextModel | null = null
let modifiedModel: import('monaco-editor').editor.ITextModel | null = null

const language = computed(() => detectLanguage(props.filePath))
const hasChanges = computed(() => props.originalContent !== props.modifiedContent)
const pendingApproval = computed(() =>
  props.approvals?.find((a) => a.id === pendingApprovalId.value) ?? null,
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

  if (!props.selectedSessionId) {
    error.value = 'A session is required to apply patches (approval flow).'
    return
  }

  applying.value = true
  error.value = null
  feedback.value = null
  try {
    const patch = generatePatch()
    const approval = await api.createApproval({
      session_id: props.selectedSessionId,
      kind: 'code',
      summary: `Apply patch to: ${props.filePath}`,
      command: 'code_editor patch',
      cwd: props.cwd,
    })
    pendingApprovalId.value = approval.id
    feedback.value = 'Patch approval requested. Awaiting decision...'
    emit('approval-requested', approval)

    // Store patch for when approval is granted
    pendingPatch.value = patch
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to create approval'
  } finally {
    applying.value = false
  }
}

const pendingPatch = ref<string | null>(null)

async function executePatch(): Promise<void> {
  if (!pendingApproval.value || pendingApproval.value.status !== 'approved') return
  if (!pendingPatch.value || !props.filePath) return

  applying.value = true
  error.value = null
  try {
    await api.codeEditorPatch(props.cwd, props.filePath, pendingPatch.value, pendingApproval.value.id)
    feedback.value = `Patch applied to ${props.filePath}.`
    pendingApprovalId.value = null
    pendingPatch.value = null
    emit('applied', props.filePath)
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to apply patch'
  } finally {
    applying.value = false
  }
}

watch(pendingApproval, (approval) => {
  if (approval && approval.status === 'approved') {
    void executePatch()
  } else if (approval && approval.status === 'rejected') {
    feedback.value = 'Patch approval was rejected.'
    pendingApprovalId.value = null
    pendingPatch.value = null
  }
})

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
      <span class="truncate text-sm font-medium">Diff: {{ filePath }}</span>
      <div class="ml-auto flex items-center gap-1">
        <UiButton
          variant="default"
          size="sm"
          class="h-7"
          :disabled="!hasChanges || applying"
          @click="handleApplyPatch"
        >
          <ShieldCheck :size="13" />
          <span>Apply Patch</span>
        </UiButton>
        <UiButton variant="ghost" size="icon" class="h-7 w-7" title="Close" @click="emit('cancel')">
          <X :size="13" />
        </UiButton>
      </div>
    </div>

    <div v-if="error" class="px-3 py-2 text-sm text-destructive">{{ error }}</div>
    <div v-if="feedback" class="flex items-center gap-2 px-3 py-1.5 text-xs text-muted-foreground">
      <Check :size="12" />
      <span>{{ feedback }}</span>
    </div>
    <div v-if="!hasChanges" class="px-3 py-2 text-xs text-muted-foreground">
      No changes to preview.
    </div>

    <div class="relative flex-1">
      <div v-if="loading" class="absolute inset-0 z-10 flex items-center justify-center text-sm text-muted-foreground">
        Loading diff...
      </div>
      <div ref="containerRef" class="h-full w-full" />
    </div>
  </div>
</template>
