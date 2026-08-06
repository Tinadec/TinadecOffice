<script setup lang="ts">
import { GitCompare } from '@lucide/vue'
import PatchPreview from '@/components/code/PatchPreview.vue'
import { useCodeWorkbench } from '../codeController'

const code = useCodeWorkbench()
</script>

<template>
  <PatchPreview
    v-if="code.patchFilePath.value"
    :cwd="code.currentProjectPath.value"
    :file-path="code.patchFilePath.value"
    :original-content="code.patchOriginal.value"
    :modified-content="code.patchModified.value"
    :selected-session-id="code.selectedSessionId.value"
    :approvals="code.approvals.value"
    @approval-requested="code.recordApproval"
    @cancel="code.clearPatch"
  />
  <div v-else class="code-patch-empty">
    <GitCompare />
    <p>No patch to preview.</p>
  </div>
</template>

<style scoped>
.code-patch-empty { display: grid; place-content: center; justify-items: center; gap: 8px; height: 100%; color: var(--text-muted); }
.code-patch-empty svg { width: 24px; height: 24px; }
</style>

