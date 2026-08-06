<script setup lang="ts">
import FileTreePanel from '@/components/code/FileTreePanel.vue'
import SearchPanel from '@/components/code/SearchPanel.vue'
import { UiSelect } from '@/components/ui'
import { useCodeWorkbench } from '../codeController'

const code = useCodeWorkbench()
</script>

<template>
  <div class="code-explorer-card">
    <UiSelect
      :model-value="code.selectedProjectId.value ?? ''"
      placeholder="Select project"
      @update:model-value="code.selectedProjectId.value = $event"
    >
      <template #default="{ select, selectedValue }">
        <button
          v-for="project in code.projects.value"
          :key="project.id"
          class="code-project-option"
          :class="{ active: selectedValue === project.id }"
          @click="select(project.id)"
        >
          <span>{{ project.name }}</span>
          <small>{{ project.path }}</small>
        </button>
      </template>
    </UiSelect>
    <div class="code-explorer-tree">
      <FileTreePanel
        :cwd="code.currentProjectPath.value"
        :approvals="code.approvals.value"
        :selected-session-id="code.selectedSessionId.value"
        @select="code.selectFile"
        @approval-created="code.recordApproval"
      />
    </div>
    <div class="code-explorer-search">
      <SearchPanel :cwd="code.currentProjectPath.value" @select="code.selectFile" />
    </div>
  </div>
</template>

<style scoped>
.code-explorer-card { display: grid; grid-template-rows: auto minmax(180px, 3fr) minmax(140px, 2fr); gap: 6px; height: 100%; padding: 8px; min-height: 0; }
.code-explorer-tree, .code-explorer-search { min-height: 0; overflow: hidden; background: var(--surface-section); }
.code-explorer-search { border-top: 1px solid var(--border-muted); }
.code-project-option { display: flex; flex-direction: column; gap: 2px; width: 100%; padding: 8px 10px; text-align: left; color: var(--text-primary); background: transparent; border: 0; }
.code-project-option.active, .code-project-option:hover { background: var(--surface-hover); }
.code-project-option small { color: var(--text-muted); overflow-wrap: anywhere; }
</style>

