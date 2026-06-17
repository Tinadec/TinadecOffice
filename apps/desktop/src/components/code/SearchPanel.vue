<script setup lang="ts">
import {
  CaseSensitive,
  FileCode,
  Regex,
  Search,
  SearchCode,
} from '@lucide/vue'
import { computed, ref } from 'vue'
import { api } from '@/api'
import { UiButton, UiInput, UiScrollArea } from '@/components/ui'

type SearchMode = 'files' | 'content'

interface GlobMatch {
  path: string
  is_dir?: boolean
  is_file?: boolean
}

interface GrepMatch {
  file: string
  full_path?: string
  line: number
  text: string
  context_before?: Array<{ line: number; text: string }>
  context_after?: Array<{ line: number; text: string }>
}

const props = defineProps<{
  cwd: string
}>()

const emit = defineEmits<{
  select: [path: string]
}>()

const mode = ref<SearchMode>('files')
const query = ref('')
const caseSensitive = ref(false)
const useRegex = ref(false)
const searching = ref(false)
const error = ref<string | null>(null)

const fileResults = ref<GlobMatch[]>([])
const grepResults = ref<GrepMatch[]>([])

const hasResults = computed(() =>
  mode.value === 'files' ? fileResults.value.length > 0 : grepResults.value.length > 0,
)

async function runSearch(): Promise<void> {
  const q = query.value.trim()
  if (!q) return

  searching.value = true
  error.value = null
  try {
    if (mode.value === 'files') {
      const result = await api.globSearch(props.cwd, q)
      const data = result.data as { matches?: GlobMatch[] }
      fileResults.value = Array.isArray(data?.matches) ? data.matches : []
    } else {
      const result = await api.grepContent(props.cwd, q, {
        case_sensitive: caseSensitive.value,
        context_lines: useRegex.value ? 0 : 2,
        max_results: 100,
      })
      const data = result.data as { matches?: GrepMatch[] }
      grepResults.value = Array.isArray(data?.matches) ? data.matches : []
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Search failed'
  } finally {
    searching.value = false
  }
}

function handleSelect(path: string): void {
  emit('select', path)
}

function switchMode(newMode: SearchMode): void {
  mode.value = newMode
  error.value = null
}

function highlightText(text: string, pattern: string): { text: string; match: boolean }[] {
  if (!pattern) return [{ text, match: false }]
  const flags = caseSensitive.value ? 'g' : 'gi'
  try {
    const regex = useRegex.value ? new RegExp(pattern, flags) : new RegExp(escapeRegex(pattern), flags)
    const parts: { text: string; match: boolean }[] = []
    let lastIndex = 0
    let match: RegExpExecArray | null
    while ((match = regex.exec(text)) !== null) {
      if (match.index > lastIndex) {
        parts.push({ text: text.slice(lastIndex, match.index), match: false })
      }
      parts.push({ text: match[0], match: true })
      lastIndex = match.index + match[0].length
      if (match[0].length === 0) regex.lastIndex++
    }
    if (lastIndex < text.length) {
      parts.push({ text: text.slice(lastIndex), match: false })
    }
    return parts.length > 0 ? parts : [{ text, match: false }]
  } catch {
    return [{ text, match: false }]
  }
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}
</script>

<template>
  <div class="flex h-full flex-col">
    <div class="flex items-center gap-1 border-b border-border p-2">
      <UiButton
        variant="ghost"
        size="xs"
        :class="{ 'bg-accent': mode === 'files' }"
        @click="switchMode('files')"
      >
        <FileCode :size="12" />
        <span>Files</span>
      </UiButton>
      <UiButton
        variant="ghost"
        size="xs"
        :class="{ 'bg-accent': mode === 'content' }"
        @click="switchMode('content')"
      >
        <SearchCode :size="12" />
        <span>Content</span>
      </UiButton>
    </div>

    <div class="flex items-center gap-2 border-b border-border p-2">
      <Search :size="14" class="text-muted-foreground" />
      <UiInput
        v-model="query"
        :placeholder="mode === 'files' ? 'glob: **/*.ts' : 'search content...'"
        class="h-7 text-xs"
        @keydown.enter="runSearch"
      />
      <UiButton
        variant="ghost"
        size="icon"
        class="h-7 w-7 shrink-0"
        :class="{ 'bg-accent': caseSensitive }"
        :title="'Case sensitive'"
        @click="caseSensitive = !caseSensitive"
      >
        <CaseSensitive :size="13" />
      </UiButton>
      <UiButton
        variant="ghost"
        size="icon"
        class="h-7 w-7 shrink-0"
        :class="{ 'bg-accent': useRegex }"
        :title="'Regex'"
        @click="useRegex = !useRegex"
      >
        <Regex :size="13" />
      </UiButton>
    </div>

    <div v-if="error" class="px-3 py-2 text-xs text-destructive">{{ error }}</div>

    <UiScrollArea class="flex-1">
      <div class="search-results">
        <div v-if="searching" class="px-3 py-4 text-center text-xs text-muted-foreground">
          Searching...
        </div>

        <template v-else-if="!hasResults && query">
          <div class="px-3 py-4 text-center text-xs text-muted-foreground">
            No results found.
          </div>
        </template>

        <template v-else-if="!query">
          <div class="px-3 py-4 text-center text-xs text-muted-foreground">
            Enter a search query and press Enter.
          </div>
        </template>

        <!-- File search results -->
        <template v-else-if="mode === 'files'">
          <button
            v-for="item in fileResults"
            :key="item.path"
            class="search-result-item"
            @click="handleSelect(item.path)"
          >
            <FileCode :size="13" class="search-result-icon" />
            <span class="search-result-path">{{ item.path }}</span>
          </button>
        </template>

        <!-- Content search results -->
        <template v-else>
          <div
            v-for="(match, idx) in grepResults"
            :key="`${match.file}:${match.line}:${idx}`"
            class="search-result-group"
          >
            <button
              class="search-result-file"
              @click="handleSelect(match.file)"
            >
              <FileCode :size="12" />
              <span>{{ match.file }}</span>
              <span class="search-result-line">:{{ match.line }}</span>
            </button>
            <div class="search-result-context">
              <div
                v-for="ctx in match.context_before"
                :key="`before-${ctx.line}`"
                class="search-result-ctx-line"
              >
                <span class="search-result-ctx-num">{{ ctx.line }}</span>
                <code>{{ ctx.text }}</code>
              </div>
              <div class="search-result-match-line">
                <span class="search-result-ctx-num">{{ match.line }}</span>
                <code>
                  <template v-for="(part, pi) in highlightText(match.text, query)" :key="pi">
                    <span :class="{ 'search-highlight': part.match }">{{ part.text }}</span>
                  </template>
                </code>
              </div>
              <div
                v-for="ctx in match.context_after"
                :key="`after-${ctx.line}`"
                class="search-result-ctx-line"
              >
                <span class="search-result-ctx-num">{{ ctx.line }}</span>
                <code>{{ ctx.text }}</code>
              </div>
            </div>
          </div>
        </template>
      </div>
    </UiScrollArea>
  </div>
</template>

<style scoped>
.search-results {
  padding: 4px 0;
}
.search-result-item {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 4px 12px;
  font-size: 12px;
  color: var(--text-primary);
  background: transparent;
  border: 0;
  cursor: pointer;
  text-align: left;
}
.search-result-item:hover {
  background: var(--bg-hover);
}
.search-result-icon {
  flex-shrink: 0;
  color: var(--text-secondary);
}
.search-result-path {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.search-result-group {
  padding: 4px 0;
  border-bottom: 1px solid var(--border-muted);
}
.search-result-group:last-child {
  border-bottom: 0;
}
.search-result-file {
  display: flex;
  align-items: center;
  gap: 4px;
  width: 100%;
  padding: 3px 12px;
  font-size: 12px;
  font-weight: 500;
  color: var(--text-primary);
  background: transparent;
  border: 0;
  cursor: pointer;
  text-align: left;
}
.search-result-file:hover {
  background: var(--bg-hover);
}
.search-result-line {
  color: var(--text-muted);
  font-weight: 400;
}
.search-result-context {
  padding: 0 12px 4px 28px;
}
.search-result-ctx-line,
.search-result-match-line {
  display: flex;
  gap: 8px;
  font-size: 11px;
  line-height: 1.5;
}
.search-result-ctx-line {
  color: var(--text-muted);
}
.search-result-match-line {
  color: var(--text-primary);
  background: var(--bg-selected);
  margin: 1px -4px;
  padding: 0 4px;
  border-radius: 2px;
}
.search-result-ctx-num {
  flex-shrink: 0;
  min-width: 28px;
  text-align: right;
  color: var(--text-muted);
  user-select: none;
}
.search-result-ctx-line code,
.search-result-match-line code {
  font-family: 'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace;
  white-space: pre-wrap;
  word-break: break-all;
}
.search-highlight {
  background: rgba(245, 158, 11, 0.3);
  border-radius: 2px;
}
</style>
