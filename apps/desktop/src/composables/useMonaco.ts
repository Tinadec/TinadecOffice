import loader from '@monaco-editor/loader'
import type * as Monaco from 'monaco-editor'
import { computed, ref, watch } from 'vue'
import { useTheme } from './useTheme'
import '@/monaco.config'

type MonacoType = typeof Monaco

const monacoReady = ref(false)
let monacoInstance: MonacoType | null = null
let initPromise: Promise<MonacoType> | null = null
let configured = false

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

let themeWatchInstalled = false

export function useMonaco() {
  const { theme } = useTheme()

  const isDark = computed(() => {
    if (theme.value === 'system') {
      return typeof window !== 'undefined' && window.matchMedia('(prefers-color-scheme: dark)').matches
    }
    return theme.value === 'dark'
  })

  if (!themeWatchInstalled) {
    themeWatchInstalled = true
    watch(isDark, (dark) => {
      if (monacoInstance) {
        monacoInstance.editor.setTheme(dark ? 'vs-dark' : 'vs')
      }
    })
  }

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

  function setTheme(dark: boolean): void {
    if (!monacoInstance) return
    monacoInstance.editor.setTheme(dark ? 'vs-dark' : 'vs')
  }

  return { monacoReady, getMonaco, isDark, setTheme }
}

/**
 * Map a file path/extension to a Monaco language id.
 */
export function detectLanguage(filePath: string): string {
  const ext = filePath.split('.').pop()?.toLowerCase() ?? ''
  const map: Record<string, string> = {
    ts: 'typescript',
    tsx: 'typescript',
    mts: 'typescript',
    cts: 'typescript',
    js: 'javascript',
    jsx: 'javascript',
    mjs: 'javascript',
    cjs: 'javascript',
    json: 'json',
    jsonc: 'json',
    css: 'css',
    scss: 'scss',
    less: 'less',
    html: 'html',
    htm: 'html',
    xml: 'xml',
    svg: 'xml',
    vue: 'html',
    svelte: 'html',
    md: 'markdown',
    markdown: 'markdown',
    mdx: 'markdown',
    py: 'python',
    rb: 'ruby',
    rs: 'rust',
    go: 'go',
    java: 'java',
    kt: 'kotlin',
    c: 'c',
    h: 'c',
    cpp: 'cpp',
    cc: 'cpp',
    cxx: 'cpp',
    hpp: 'cpp',
    cs: 'csharp',
    fs: 'fsharp',
    fsx: 'fsharp',
    sh: 'shell',
    bash: 'shell',
    zsh: 'shell',
    ps1: 'powershell',
    yml: 'yaml',
    yaml: 'yaml',
    toml: 'ini',
    ini: 'ini',
    cfg: 'ini',
    sql: 'sql',
    php: 'php',
    swift: 'swift',
    dart: 'dart',
    lua: 'lua',
    r: 'r',
    pl: 'perl',
    pm: 'perl',
    scala: 'scala',
    clj: 'clojure',
    ex: 'elixir',
    exs: 'elixir',
    zig: 'zig',
    nim: 'nim',
    graphql: 'graphql',
    gql: 'graphql',
    dockerfile: 'dockerfile',
    makefile: 'makefile',
  }
  // Handle special filenames
  const base = filePath.split(/[\\/]/).pop() ?? ''
  if (base.toLowerCase() === 'dockerfile') return 'dockerfile'
  if (base.toLowerCase() === 'makefile') return 'makefile'
  return map[ext] ?? 'plaintext'
}
