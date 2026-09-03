<script setup lang="ts">
import { ref } from 'vue'
import {
  ChevronDown,
  ChevronRight,
  Folder,
  FolderOpen,
  FilePlus,
  FileText,
  FileX,
  FileCog,
  Plus,
  Minus,
  RotateCcw,
  ExternalLink,
} from '@lucide/vue'
import { UiCheckbox } from '../ui'
import type { GitTreeNode } from './diffUtils'

export interface GitTreeFileItem {
  path: string
  status?: string
  is_staged?: boolean
  unstaged_status?: string
  is_conflicted?: boolean
  additions?: number
  deletions?: number
}

const props = defineProps<{
  node: GitTreeNode<GitTreeFileItem>
  depth?: number
  selectedPaths: Set<string>
  activeDiffPath?: string | null
  operationLoading?: boolean
}>()

const emit = defineEmits<{
  (e: 'toggle-select', path: string): void
  (e: 'select-diff', path: string): void
  (e: 'stage-file', path: string): void
  (e: 'unstage-file', path: string): void
  (e: 'discard-file', path: string): void
  (e: 'stage-folder', paths: string[]): void
  (e: 'unstage-folder', paths: string[]): void
}>()

const isExpanded = ref(true)

function collectFilePaths(node: GitTreeNode<GitTreeFileItem>): string[] {
  if (!node.isFolder) {
    return node.fileData ? [node.fileData.path] : [node.path]
  }
  const result: string[] = []
  if (node.children) {
    for (const child of node.children) {
      result.push(...collectFilePaths(child))
    }
  }
  return result
}

function statusIcon(status?: string | null) {
  switch (status) {
    case 'added':
    case 'untracked':
      return FilePlus
    case 'deleted':
      return FileX
    case 'renamed':
      return FileCog
    default:
      return FileText
  }
}

function statusLetter(status?: string | null): string {
  switch (status) {
    case 'added':
      return 'A'
    case 'deleted':
      return 'D'
    case 'modified':
      return 'M'
    case 'renamed':
      return 'R'
    case 'untracked':
      return 'U'
    case 'conflicted':
      return 'C'
    default:
      return 'M'
  }
}

function statusColorClass(status?: string | null): string {
  switch (status) {
    case 'added':
    case 'untracked':
      return 'is-added'
    case 'deleted':
      return 'is-deleted'
    case 'conflicted':
      return 'is-conflicted'
    default:
      return 'is-modified'
  }
}

function toggleFolder() {
  isExpanded.value = !isExpanded.value
}

function handleStageFolder() {
  const paths = collectFilePaths(props.node)
  if (paths.length > 0) {
    emit('stage-folder', paths)
  }
}

function handleUnstageFolder() {
  const paths = collectFilePaths(props.node)
  if (paths.length > 0) {
    emit('unstage-folder', paths)
  }
}
</script>

<template>
  <div class="git-tree-node-wrapper">
    <!-- Folder Row -->
    <div
      v-if="node.isFolder"
      class="git-tree-row is-folder"
      :style="{ paddingLeft: `${(depth ?? 0) * 14 + 6}px` }"
      @click="toggleFolder"
    >
      <button type="button" class="git-tree-expand-btn" :aria-label="isExpanded ? 'Collapse' : 'Expand'">
        <component :is="isExpanded ? ChevronDown : ChevronRight" :size="13" />
      </button>
      <component :is="isExpanded ? FolderOpen : Folder" :size="14" class="git-folder-icon" />
      <span class="git-tree-folder-name" :title="node.path">{{ node.name }}</span>

      <!-- Folder batch hover action -->
      <div class="git-row-actions" @click.stop>
        <button
          v-if="node.isStaged"
          type="button"
          class="git-hover-btn"
          title="Unstage folder"
          :disabled="operationLoading"
          @click.stop="handleUnstageFolder"
        >
          <Minus :size="12" />
        </button>
        <button
          v-else
          type="button"
          class="git-hover-btn"
          title="Stage folder"
          :disabled="operationLoading"
          @click.stop="handleStageFolder"
        >
          <Plus :size="12" />
        </button>
      </div>
    </div>

    <!-- Children container if folder is expanded -->
    <div v-if="node.isFolder && isExpanded && node.children && node.children.length > 0" class="git-tree-children">
      <GitTreeNodeRow
        v-for="child in node.children"
        :key="child.id"
        :node="child"
        :depth="(depth ?? 0) + 1"
        :selected-paths="selectedPaths"
        :active-diff-path="activeDiffPath"
        :operation-loading="operationLoading"
        @toggle-select="emit('toggle-select', $event)"
        @select-diff="emit('select-diff', $event)"
        @stage-file="emit('stage-file', $event)"
        @unstage-file="emit('unstage-file', $event)"
        @discard-file="emit('discard-file', $event)"
        @stage-folder="emit('stage-folder', $event)"
        @unstage-folder="emit('unstage-folder', $event)"
      />
    </div>

    <!-- File Row -->
    <div
      v-else-if="!node.isFolder && node.fileData"
      class="git-tree-row is-file"
      :class="[statusColorClass(node.fileData.status ?? node.fileData.unstaged_status), { 'is-active-diff': activeDiffPath === node.fileData.path }]"
      :style="{ paddingLeft: `${(depth ?? 0) * 14 + 6}px` }"
      @click="emit('select-diff', node.fileData.path)"
    >
      <UiCheckbox
        :model-value="selectedPaths.has(node.fileData.path)"
        :aria-label="node.fileData.path"
        class="git-checkbox"
        @click.stop
        @update:model-value="emit('toggle-select', node.fileData!.path)"
      />
      <component :is="statusIcon(node.fileData.status ?? node.fileData.unstaged_status)" :size="13" class="git-file-icon" />
      <span class="git-tree-file-name" :title="node.fileData.path">{{ node.name }}</span>
      <span class="git-badge-letter">{{ statusLetter(node.fileData.status ?? node.fileData.unstaged_status) }}</span>

      <!-- Hover action buttons -->
      <div class="git-row-actions" @click.stop>
        <button
          v-if="node.fileData.is_staged"
          type="button"
          class="git-hover-btn"
          title="Unstage file"
          :disabled="operationLoading"
          @click.stop="emit('unstage-file', node.fileData.path)"
        >
          <Minus :size="12" />
        </button>
        <button
          v-else
          type="button"
          class="git-hover-btn"
          title="Stage file"
          :disabled="operationLoading"
          @click.stop="emit('stage-file', node.fileData.path)"
        >
          <Plus :size="12" />
        </button>

        <button
          v-if="!node.fileData.is_conflicted"
          type="button"
          class="git-hover-btn is-discard"
          title="Discard changes"
          :disabled="operationLoading"
          @click.stop="emit('discard-file', node.fileData.path)"
        >
          <RotateCcw :size="12" />
        </button>

        <button
          type="button"
          class="git-hover-btn"
          title="View diff"
          @click.stop="emit('select-diff', node.fileData.path)"
        >
          <ExternalLink :size="12" />
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.git-tree-node-wrapper {
  user-select: none;
}

.git-tree-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-height: 26px;
  padding-right: 8px;
  cursor: pointer;
  border-radius: var(--radius-sm, 4px);
  position: relative;
  transition: background 0.12s ease;
}

.git-tree-row:hover {
  background: var(--bg-hover, rgba(255, 255, 255, 0.04));
}

.git-tree-row.is-active-diff {
  background: var(--bg-active, rgba(59, 130, 246, 0.12));
  color: var(--color-primary, #3b82f6);
}

.git-tree-expand-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  padding: 0;
  border: none;
  background: transparent;
  color: var(--text-muted, #8b949e);
  cursor: pointer;
}

.git-folder-icon {
  color: var(--color-warning, #d29922);
  flex-shrink: 0;
}

.git-file-icon {
  flex-shrink: 0;
  color: var(--text-muted, #8b949e);
}

.git-tree-folder-name {
  font-size: 12px;
  color: var(--text-secondary, #c9d1d9);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  flex: 1;
}

.git-tree-file-name {
  font-size: 12px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  flex: 1;
}

.git-badge-letter {
  font-size: 10px;
  font-weight: 700;
  padding: 1px 4px;
  border-radius: 3px;
  margin-left: auto;
}

.is-modified .git-badge-letter {
  color: #e3b341;
}

.is-added .git-badge-letter {
  color: #3fb950;
}

.is-deleted .git-badge-letter {
  color: #f85149;
}

.is-conflicted .git-badge-letter {
  color: #d2a8ff;
}

.git-row-actions {
  display: none;
  align-items: center;
  gap: 2px;
  margin-left: 4px;
}

.git-tree-row:hover .git-row-actions {
  display: flex;
}

.git-hover-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  padding: 0;
  border: none;
  background: transparent;
  border-radius: 3px;
  color: var(--text-muted, #8b949e);
  cursor: pointer;
}

.git-hover-btn:hover {
  background: var(--bg-card, rgba(255, 255, 255, 0.12));
  color: var(--text-primary, #ffffff);
}

.git-hover-btn.is-discard:hover {
  color: #f85149;
}
</style>
