<script setup lang="ts">
import { Edit, FileCode, RefreshCw } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { api } from '@/api'
import { detectLanguage, useMonaco } from '@/composables/useMonaco'
import { UiButton, UiSkeleton } from '@/components/ui'

const props = defineProps<{
  cwd: string
  filePath: string
}>()

const emit = defineEmits<{
  edit: [filePath: string, content: string]
}>()

const { getMonaco, isDark } = useMonaco()

const containerRef = ref<HTMLDivElement | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)
const content = ref('')
const fileSize = ref<number | null>(null)
const modifiedAt = ref<string | null>(null)

let editor: import('monaco-editor').editor.IStandaloneCodeEditor | null = null
let model: import('monaco-editor').editor.ITextModel | null = null

const language = computed(() => detectLanguage(props.filePath))

function formatSize(bytes: number | null): string {
  if (bytes === null) return '-'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function formatDate(iso: string | null): string {
  if (!iso) return '-'
  try {
    return new Date(iso).toLocaleString()
  } catch {
    return iso
  }
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
    readOnly: true,
    theme: isDark.value ? 'vs-dark' : 'vs',
    automaticLayout: true,
    fontSize: 13,
    lineNumbers: 'on',
    minimap: { enabled: true },
    scrollBeyondLastLine: false,
    wordWrap: 'off',
    tabSize: 2,
    renderWhitespace: 'selection',
    bracketPairColorization: { enabled: true },
    smoothScrolling: true,
  })
}

function handleEdit(): void {
  emit('edit', props.filePath, content.value)
}

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
}, { immediate: true })

watch(language, (lang) => {
  if (model) {
    const monaco = (window as unknown as { Monaco?: typeof import('monaco-editor') }).Monaco
    if (monaco) {
      monaco.editor.setModelLanguage(model, lang)
    }
  }
})
</script>

<template>
  <div class="flex h-full flex-col">
    <div class="flex items-center gap-3 border-b border-border px-3 py-2">
      <FileCode :size="14" class="text-muted-foreground" />
      <span class="truncate text-sm font-medium">{{ filePath }}</span>
      <div class="ml-auto flex items-center gap-3 text-xs text-muted-foreground">
        <span v-if="fileSize !== null">{{ formatSize(fileSize) }}</span>
        <span v-if="modifiedAt">{{ formatDate(modifiedAt) }}</span>
        <UiButton variant="ghost" size="icon" class="h-7 w-7" title="Reload" :disabled="loading" @click="loadFile">
          <RefreshCw :size="13" :class="{ 'animate-spin': loading }" />
        </UiButton>
        <UiButton variant="outline" size="sm" class="h-7" title="Edit file" @click="handleEdit">
          <Edit :size="13" />
          <span>Edit</span>
        </UiButton>
      </div>
    </div>

    <div v-if="error" class="px-3 py-2 text-sm text-destructive">{{ error }}</div>

    <div class="relative flex-1">
      <div v-if="loading" class="absolute inset-0 z-10 flex flex-col gap-2 p-4">
        <UiSkeleton class="h-4 w-3/4" />
        <UiSkeleton class="h-4 w-1/2" />
        <UiSkeleton class="h-4 w-2/3" />
        <UiSkeleton class="h-4 w-5/6" />
        <UiSkeleton class="h-4 w-3/5" />
        <UiSkeleton class="h-4 w-4/5" />
        <UiSkeleton class="h-4 w-2/3" />
        <UiSkeleton class="h-4 w-3/4" />
      </div>
      <div ref="containerRef" class="h-full w-full" />
    </div>
  </div>
</template>
