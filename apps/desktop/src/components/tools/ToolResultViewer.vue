<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Check, ChevronDown, ChevronRight, Copy, FileCode2, FileText, Folder, GitBranch, Search, Terminal } from '@lucide/vue'
import { parseUnifiedDiff, type GitDiffFile, type GitDiffHunk, type GitDiffLine } from '@/gitDiffParser'
import type { ToolExecutionTimelineItemDto } from '@/api'

const props = defineProps<{
  toolExecution: ToolExecutionTimelineItemDto
  defaultExpanded?: boolean
}>()

const expanded = ref(props.defaultExpanded ?? false)
const copied = ref(false)

watch(() => props.defaultExpanded, (val) => {
  expanded.value = val ?? false
})

const resultText = computed(() => {
  const items: string[] = []
  if (props.toolExecution.summary) items.push(props.toolExecution.summary)
  if (props.toolExecution.checkpoint_summary) items.push(props.toolExecution.checkpoint_summary)
  for (const ev of props.toolExecution.evidence) items.push(ev)
  return items.join('\n')
})

const evidenceLines = computed(() => props.toolExecution.evidence)

interface DiffFileView {
  file: GitDiffFile
  hunks: Array<{ hunk: GitDiffHunk; lines: GitDiffLine[] }>
}

const parsedDiff = computed<DiffFileView[]>(() => {
  if (!isDiffTool.value) return []
  const diffText = extractDiffText()
  if (!diffText) return []
  const parsed = parseUnifiedDiff(diffText)
  return parsed.files.map((file) => ({
    file,
    hunks: file.hunks.map((hunk) => ({ hunk, lines: hunk.lines }))
  }))
})

const isFileListTool = computed(() =>
  ['read_file', 'list_directory', 'glob_search'].includes(props.toolExecution.tool_id)
)
const isGrepTool = computed(() => props.toolExecution.tool_id === 'grep_content')
const isDiffTool = computed(() =>
  ['apply_patch', 'code_editor', 'git_worktree_manager'].includes(props.toolExecution.tool_id)
)
const isShellTool = computed(() => props.toolExecution.tool_id === 'sandbox_exec')
const isGitTool = computed(() => props.toolExecution.tool_id === 'git_worktree_manager' || props.toolExecution.tool_id.startsWith('git_'))

const fileList = computed<string[]>(() => {
  if (!isFileListTool.value) return []
  for (const ev of props.toolExecution.evidence) {
    const match = ev.match(/(?:files?|paths?|entries)\s*[:=]\s*(.+)/i)
    if (match) {
      return match[1].split(/[;,]/).map((s) => s.trim()).filter(Boolean)
    }
  }
  return []
})

const grepMatches = computed(() => {
  if (!isGrepTool.value) return []
  return props.toolExecution.evidence.map((line, idx) => {
    const match = line.match(/^(.+?):(\d+)(?::\s*(.*))?$/)
    if (match) {
      return { id: idx, file: match[1], line: Number(match[2]), text: match[3] ?? '' }
    }
    return { id: idx, file: line, line: 0, text: '' }
  })
})

const gitOperations = computed(() => {
  if (!isGitTool.value) return []
  return props.toolExecution.evidence.map((line, idx) => ({ id: idx, text: line }))
})

const shellLines = computed(() => {
  if (!isShellTool.value) return []
  return props.toolExecution.evidence
})

function extractDiffText(): string {
  for (const ev of props.toolExecution.evidence) {
    if (ev.includes('diff --git') || ev.startsWith('@@')) {
      return ev
    }
  }
  if (props.toolExecution.checkpoint_summary?.includes('diff --git')) {
    return props.toolExecution.checkpoint_summary
  }
  return props.toolExecution.evidence.join('\n')
}

async function copyResult() {
  try {
    await navigator.clipboard.writeText(resultText.value)
    copied.value = true
    setTimeout(() => {
      copied.value = false
    }, 1500)
  } catch {
    // Clipboard may be unavailable
  }
}

function toggle() {
  expanded.value = !expanded.value
}
</script>

<template>
  <div class="tool-result-viewer">
    <div class="tool-result-header" @click="toggle">
      <component
        :is="expanded ? ChevronDown : ChevronRight"
        :size="14"
        class="tool-result-chevron"
      />
      <span class="tool-result-title">Result</span>
      <span class="tool-result-meta">{{ toolExecution.status }}</span>
      <button
        class="tool-result-copy"
        title="Copy result"
        @click.stop="copyResult"
      >
        <Check v-if="copied" :size="12" />
        <Copy v-else :size="12" />
      </button>
    </div>

    <div v-if="expanded" class="tool-result-body">
      <!-- File list view -->
      <div v-if="isFileListTool && fileList.length > 0" class="tool-result-files">
        <div v-for="(path, idx) in fileList" :key="idx" class="tool-result-file-row">
          <FileText :size="12" />
          <span>{{ path }}</span>
        </div>
      </div>

      <!-- Grep results view -->
      <div v-else-if="isGrepTool && grepMatches.length > 0" class="tool-result-grep">
        <div v-for="match in grepMatches" :key="match.id" class="tool-result-grep-row">
          <Search :size="12" class="tool-result-grep-icon" />
          <span class="tool-result-grep-file">{{ match.file }}:{{ match.line }}</span>
          <code v-if="match.text" class="tool-result-grep-text">{{ match.text }}</code>
        </div>
      </div>

      <!-- Diff view -->
      <div v-else-if="isDiffTool && parsedDiff.length > 0" class="tool-result-diff">
        <div v-for="(fileView, fIdx) in parsedDiff" :key="fIdx" class="tool-result-diff-file">
          <div class="tool-result-diff-file-head">
            <FileCode2 :size="12" />
            <strong>{{ fileView.file.path }}</strong>
            <small v-if="fileView.file.previous_path">{{ fileView.file.previous_path }} →</small>
          </div>
          <div v-for="(hunkView, hIdx) in fileView.hunks" :key="hIdx" class="tool-result-diff-hunk">
            <div class="tool-result-diff-hunk-head">{{ hunkView.hunk.header }}</div>
            <div
              v-for="line in hunkView.lines"
              :key="line.id"
              class="tool-result-diff-line"
              :class="line.change"
            >
              <span class="tool-result-diff-prefix">
                {{ line.change === 'add' ? '+' : line.change === 'delete' ? '-' : ' ' }}
              </span>
              <code>{{ line.content }}</code>
            </div>
          </div>
        </div>
      </div>

      <!-- Git operations view -->
      <div v-else-if="isGitTool && gitOperations.length > 0" class="tool-result-git">
        <div v-for="op in gitOperations" :key="op.id" class="tool-result-git-row">
          <GitBranch :size="12" />
          <span>{{ op.text }}</span>
        </div>
      </div>

      <!-- Shell output view -->
      <div v-else-if="isShellTool && shellLines.length > 0" class="tool-result-shell">
        <Terminal :size="12" class="tool-result-shell-icon" />
        <pre class="tool-result-shell-output">{{ shellLines.join('\n') }}</pre>
      </div>

      <!-- Default: evidence list / JSON tree -->
      <div v-else class="tool-result-default">
        <div v-if="toolExecution.summary" class="tool-result-summary">
          {{ toolExecution.summary }}
        </div>
        <div v-if="evidenceLines.length > 0" class="tool-result-evidence">
          <div v-for="(line, idx) in evidenceLines" :key="idx" class="tool-result-evidence-row">
            <Folder v-if="line.includes('/')" :size="11" />
            <span>{{ line }}</span>
          </div>
        </div>
        <div v-if="toolExecution.checkpoint_summary" class="tool-result-checkpoint">
          {{ toolExecution.checkpoint_summary }}
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.tool-result-viewer {
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--bg-secondary);
  overflow: hidden;
}

.tool-result-header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  cursor: pointer;
  user-select: none;
  transition: background 0.15s;
}

.tool-result-header:hover {
  background: var(--bg-hover);
}

.tool-result-chevron {
  color: var(--text-muted);
  flex-shrink: 0;
}

.tool-result-title {
  flex: 1;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.tool-result-meta {
  font-size: 10px;
  color: var(--text-muted);
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--bg-tertiary);
}

.tool-result-copy {
  display: grid;
  place-items: center;
  width: 20px;
  height: 20px;
  color: var(--text-muted);
  background: transparent;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.tool-result-copy:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.tool-result-body {
  max-height: 320px;
  overflow-y: auto;
  padding: 8px 10px;
  border-top: 1px solid var(--border-muted);
  font-size: 11px;
  line-height: 1.5;
}

.tool-result-files,
.tool-result-grep,
.tool-result-git {
  display: grid;
  gap: 4px;
}

.tool-result-file-row,
.tool-result-grep-row,
.tool-result-git-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 3px 4px;
  color: var(--text-primary);
  font-size: 11px;
}

.tool-result-file-row svg,
.tool-result-git-row svg {
  color: var(--text-muted);
  flex-shrink: 0;
}

.tool-result-grep-icon {
  color: var(--accent-primary);
  flex-shrink: 0;
}

.tool-result-grep-file {
  color: var(--text-secondary);
  flex-shrink: 0;
}

.tool-result-grep-text {
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
  color: var(--text-primary);
  background: var(--bg-tertiary);
  padding: 1px 4px;
  border-radius: 3px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-result-diff {
  display: grid;
  gap: 8px;
}

.tool-result-diff-file {
  border: 1px solid var(--border-muted);
  border-radius: 4px;
  overflow: hidden;
}

.tool-result-diff-file-head {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 8px;
  background: var(--bg-tertiary);
  font-size: 11px;
}

.tool-result-diff-file-head strong {
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-result-diff-file-head small {
  color: var(--text-muted);
  font-size: 10px;
}

.tool-result-diff-hunk {
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
  font-size: 10px;
}

.tool-result-diff-hunk-head {
  padding: 2px 8px;
  color: var(--text-secondary);
  background: var(--bg-secondary);
}

.tool-result-diff-line {
  display: flex;
  min-width: max-content;
}

.tool-result-diff-line.add {
  background: rgba(63, 185, 80, 0.1);
}

.tool-result-diff-line.delete {
  background: rgba(248, 81, 73, 0.1);
}

.tool-result-diff-prefix {
  flex: 0 0 16px;
  padding: 0 4px;
  color: var(--text-muted);
  user-select: none;
}

.tool-result-diff-line code {
  padding: 0 8px;
  color: var(--text-primary);
  white-space: pre;
}

.tool-result-shell {
  display: flex;
  gap: 6px;
  align-items: flex-start;
}

.tool-result-shell-icon {
  color: var(--accent-success);
  flex-shrink: 0;
  margin-top: 2px;
}

.tool-result-shell-output {
  flex: 1;
  margin: 0;
  padding: 6px 8px;
  background: var(--bg-diff);
  border: 1px solid var(--border-muted);
  border-radius: 4px;
  color: var(--text-primary);
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
  font-size: 11px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
  overflow-x: auto;
}

.tool-result-default {
  display: grid;
  gap: 6px;
}

.tool-result-summary {
  color: var(--text-primary);
  font-size: 12px;
  line-height: 1.5;
}

.tool-result-evidence {
  display: grid;
  gap: 3px;
}

.tool-result-evidence-row {
  display: flex;
  align-items: center;
  gap: 5px;
  color: var(--text-secondary);
  font-size: 11px;
}

.tool-result-evidence-row svg {
  color: var(--text-muted);
  flex-shrink: 0;
}

.tool-result-checkpoint {
  padding: 6px 8px;
  color: var(--text-muted);
  background: var(--bg-tertiary);
  border-radius: 4px;
  font-size: 10px;
  line-height: 1.4;
}
</style>
