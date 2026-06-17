import loader from '@monaco-editor/loader'
import type * as Monaco from 'monaco-editor'
import { computed, ref, watch } from 'vue'
import { useTheme } from './useTheme'
import '@/monaco.config'

type MonacoType = typeof Monaco
type DiffEditor = Monaco.editor.IDiffEditor

const monacoReady = ref(false)
let monacoInstance: MonacoType | null = null
let initPromise: Promise<MonacoType> | null = null
let configured = false
let themeWatcherRegistered = false

async function ensureConfigured(): Promise<void> {
  if (configured) return
  configured = true
  try {
    const monacoModule = await import('monaco-editor')
    const monaco = (monacoModule as { default?: MonacoType }).default ?? (monacoModule as unknown as MonacoType)
    loader.config({ monaco })
  } catch {
    // Fallback: load from CDN if the local bundle is unavailable
    loader.config({ paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.55.1/min/vs' } })
  }
}

export function useMonacoDiff() {
  const { theme } = useTheme()
  const isDark = computed(() => {
    if (theme.value === 'system') {
      return typeof window !== 'undefined' && window.matchMedia('(prefers-color-scheme: dark)').matches
    }
    return theme.value === 'dark'
  })

  function getMonaco(): Promise<MonacoType> {
    if (monacoInstance) return Promise.resolve(monacoInstance)
    if (initPromise) return initPromise
    initPromise = ensureConfigured()
      .then(() => loader.init())
      .then((m: MonacoType) => {
        monacoInstance = m
        monacoReady.value = true
        m.editor.setTheme(isDark.value ? 'vs-dark' : 'vs')
        return m
      })
    return initPromise
  }

  function applyTheme(m: MonacoType): void {
    m.editor.setTheme(isDark.value ? 'vs-dark' : 'vs')
  }

  if (!themeWatcherRegistered) {
    themeWatcherRegistered = true
    watch(isDark, (dark) => {
      if (monacoInstance) {
        monacoInstance.editor.setTheme(dark ? 'vs-dark' : 'vs')
      }
    })
  }

  async function createDiffEditor(
    container: HTMLElement,
    originalText: string,
    modifiedText: string,
    language: string,
  ): Promise<DiffEditor> {
    const m = await getMonaco()
    applyTheme(m)

    const editor = m.editor.createDiffEditor(container, {
      readOnly: true,
      renderSideBySide: true,
      automaticLayout: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontSize: 12,
      lineHeight: 16,
      lineNumbersMinChars: 3,
      wordWrap: 'off',
      renderOverviewRuler: false,
      originalEditable: false,
      diffWordWrap: 'off',
    })

    const originalModel = m.editor.createModel(originalText ?? '', language || 'plaintext')
    const modifiedModel = m.editor.createModel(modifiedText ?? '', language || 'plaintext')
    editor.setModel({ original: originalModel, modified: modifiedModel })
    return editor
  }

  function updateDiffModel(
    editor: DiffEditor,
    originalText: string,
    modifiedText: string,
    language?: string,
  ): void {
    const original = editor.getOriginalEditor().getModel()
    const modified = editor.getModifiedEditor().getModel()
    if (language && monacoInstance) {
      if (original) monacoInstance.editor.setModelLanguage(original, language)
      if (modified) monacoInstance.editor.setModelLanguage(modified, language)
    }
    if (original) original.setValue(originalText ?? '')
    if (modified) modified.setValue(modifiedText ?? '')
  }

  function disposeDiffEditor(editor: DiffEditor | null): void {
    if (!editor) return
    try {
      const original = editor.getOriginalEditor().getModel()
      const modified = editor.getModifiedEditor().getModel()
      original?.dispose()
      modified?.dispose()
      editor.dispose()
    } catch {
      // ignore disposal errors
    }
  }

  return { createDiffEditor, updateDiffModel, disposeDiffEditor, getMonaco, isDark, monacoReady }
}
