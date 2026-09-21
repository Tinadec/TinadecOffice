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
import { useNotifications } from '@/composables/useNotifications'
import { groupSearchLines, searchedFilePaths, type FileSearchDataDto, type SearchGroup } from '@/lib/workspaceSearch'

type SearchMode = 'files' | 'content'

const props = defineProps<{
  cwd: string
}>()

const emit = defineEmits<{
  select: [path: string]
}>()

const { notify } = useNotifications()

const mode = ref<SearchMode>('files')
const query = ref('')
const caseSensitive = ref(false)
const useRegex = ref(false)
const searching = ref(false)

const fileResults = ref<string[]>([])
const grepResults = ref<SearchGroup[]>([])
const truncated = ref(false)

const hasResults = computed(() =>
  mode.value === 'files' ? fileResults.value.length > 0 : grepResults.value.length > 0,
)

async function runSearch(): Promise<void> {
  const q = query.value.trim()
  if (!q) return

  searching.value = true
  try {
    // One tool answers both modes: `file_search` is content search, and the file list
    // is the set of files it hit. There is no filename-only tool in the manifest.
    const result = await api.grepContent(props.cwd, q, {
      case_sensitive: caseSensitive.value,
      context_lines: mode.value === 'content' ? 2 : 0,
      max_results: mode.value === 'files' ? 200 : 100,
      fixed_strings: !useRegex.value,
    })
    const data = result.data as FileSearchDataDto
    truncated.value = data?.truncated === true
    if (mode.value === 'files') {
      fileResults.value = searchedFilePaths(data)
    } else {
      grepResults.value = groupSearchLines(data)
    }
  } catch (err) {
    notify.error(err, { title: 'Search failed', source: 'code', key: 'code-search' })
  } finally {
    searching.value = false
  }
}

function handleSelect(path: string): void {
  emit('select', path)
}

function switchMode(newMode: SearchMode): void {
  mode.value = newMode
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
        :placeholder="mode === 'files' ? 'files containing…' : 'search content...'"
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

        <!-- File hits: the tool's own per-file map, so one row per file -->
        <template v-else-if="mode === 'files'">
          <button
            v-for="path in fileResults"
            :key="path"
            class="search-result-item"
            @click="handleSelect(path)"
          >
            <FileCode :size="13" class="search-result-icon" />
            <span class="search-result-path">{{ path }}</span>
          </button>
        </template>

        <!-- Content hits: one flat row per line, regrouped per file -->
        <template v-else>
          <div
            v-for="group in grepResults"
            :key="group.path"
            class="search-result-group"
          >
            <button
              class="search-result-file"
              @click="handleSelect(group.path)"
            >
              <FileCode :size="12" />
              <span>{{ group.path }}</span>
            </button>
            <div class="search-result-context">
              <div
                v-for="line in group.lines"
                :key="line.number"
                :class="line.isMatch ? 'search-result-match-line' : 'search-result-ctx-line'"
              >
                <span class="search-result-ctx-num">{{ line.number }}</span>
                <code>
                  <template v-if="line.isMatch">
                    <template v-for="(part, pi) in highlightText(line.text, query)" :key="pi">
                      <span :class="{ 'search-highlight': part.match }">{{ part.text }}</span>
                    </template>
                  </template>
                  <template v-else>{{ line.text }}</template>
                </code>
              </div>
            </div>
          </div>
        </template>

        <div v-if="truncated" class="search-truncated">
          Showing the first matches — refine the query to narrow them down.
        </div>
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
.search-truncated {
  padding: 6px 12px;
  font-size: 11px;
  color: var(--text-muted);
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
