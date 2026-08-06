<script setup lang="ts">
import { X } from '@lucide/vue'
import CodeEditor from '@/components/code/CodeEditor.vue'
import CodeViewer from '@/components/code/CodeViewer.vue'
import { useCodeWorkbench } from '../codeController'

const code = useCodeWorkbench()
</script>

<template>
  <div class="code-editor-card">
    <div v-if="code.openTabs.value.length" class="code-card-tabs" role="tablist">
      <button
        v-for="tab in code.openTabs.value"
        :key="tab.path"
        role="tab"
        :aria-selected="code.activeTabPath.value === tab.path"
        :class="{ active: code.activeTabPath.value === tab.path }"
        @click="code.activeTabPath.value = tab.path"
      >
        <span>{{ tab.path.split(/[\\/]/).pop() }}</span>
        <small>{{ tab.mode }}</small>
        <X @click.stop="code.closeTab(tab.path)" />
      </button>
    </div>
    <div class="code-card-editor">
      <div v-if="!code.activeTab.value" class="code-card-empty">Select a file to start editing.</div>
      <CodeViewer
        v-else-if="code.activeTab.value.mode === 'view'"
        :key="`viewer:${code.activeTab.value.path}`"
        :cwd="code.currentProjectPath.value"
        :file-path="code.activeTab.value.path"
        @edit="code.editFile"
      />
      <CodeEditor
        v-else
        :key="`editor:${code.activeTab.value.path}`"
        :cwd="code.currentProjectPath.value"
        :file-path="code.activeTab.value.path"
        :initial-content="code.activeTab.value.content"
        :selected-session-id="code.selectedSessionId.value"
        :approvals="code.approvals.value"
        @approval-requested="code.recordApproval"
        @saved="code.switchToView"
        @cancel="code.switchToView(code.activeTab.value!.path)"
      />
    </div>
  </div>
</template>

<style scoped>
.code-editor-card { display: flex; flex-direction: column; width: 100%; height: 100%; min-height: 0; }
.code-card-tabs { display: flex; min-height: 32px; overflow-x: auto; border-bottom: 1px solid var(--border-muted); background: var(--surface-chrome); }
.code-card-tabs button { display: flex; align-items: center; gap: 6px; max-width: 220px; padding: 0 10px; border: 0; border-right: 1px solid var(--border-muted); color: var(--text-muted); background: transparent; }
.code-card-tabs button.active { color: var(--text-primary); background: var(--surface-active); }
.code-card-tabs span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.code-card-tabs small { color: var(--text-muted); }
.code-card-tabs svg { width: 13px; flex: 0 0 13px; }
.code-card-editor { flex: 1; min-height: 0; overflow: hidden; }
.code-card-empty { display: grid; place-items: center; height: 100%; color: var(--text-muted); }
</style>

