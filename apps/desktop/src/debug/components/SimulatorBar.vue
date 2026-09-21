<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronDown, ChevronUp } from '@lucide/vue'
import type { SimulateMessageRequest, ForceApprovalDecisionRequest } from '../types/simulation'

const { t } = useI18n()

const emit = defineEmits<{
  'inject-message': [request: SimulateMessageRequest]
  'force-approval': [request: ForceApprovalDecisionRequest]
}>()

const injectContent = ref('')
const injectSessionId = ref('')
const approvalId = ref('')
const expanded = ref(false)
</script>

<template>
  <div class="simulator-bar" :class="{ expanded }">
    <div class="simulator-main">
      <button class="sim-expand-btn" @click="expanded = !expanded">
        <ChevronDown v-if="expanded" :size="12" />
        <ChevronUp v-else :size="12" />
        {{ t('debugStudio.tools') }}
      </button>
    </div>

    <div v-if="expanded" class="simulator-tools">
      <div class="tool-group">
        <label class="tool-label">{{ t('debugStudio.injectMessage') }}</label>
        <div class="tool-inputs">
          <input v-model="injectSessionId" :placeholder="t('debugStudio.sessionId')" class="tool-input small" />
          <input v-model="injectContent" :placeholder="t('debugStudio.messageContent')" class="tool-input" />
          <button class="tool-btn" @click="injectSessionId && injectContent && emit('inject-message', { session_id: injectSessionId, content: injectContent })">{{ t('debugStudio.send') }}</button>
        </div>
      </div>
      <div class="tool-group">
        <label class="tool-label">{{ t('debugStudio.forceApproval') }}</label>
        <div class="tool-inputs">
          <input v-model="approvalId" :placeholder="t('debugStudio.approvalId')" class="tool-input small" />
          <button class="tool-btn approve" @click="approvalId && emit('force-approval', { approval_id: approvalId, decision: 'approved' })">{{ t('debugStudio.approve') }}</button>
          <button class="tool-btn reject" @click="approvalId && emit('force-approval', { approval_id: approvalId, decision: 'rejected' })">{{ t('debugStudio.reject') }}</button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.simulator-bar {
  background: transparent;
  flex-shrink: 0;
}

.simulator-main {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  padding: 6px 12px;
}

.sim-expand-btn {
  background: none;
  border: none;
  color: #58a6ff;
  cursor: pointer;
  font-size: 12px;
  padding: 4px 8px;
  border-radius: 4px;
  display: flex;
  align-items: center;
  gap: 4px;
  transition: background 0.12s;
}
.sim-expand-btn:hover { background: #21262d; }

/* ---- Tools Section ---- */
.simulator-tools {
  padding: 10px 12px;
  border-top: 1px solid #21262d;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.tool-group {
  display: flex;
  align-items: center;
  gap: 10px;
}
.tool-label {
  font-size: 11px;
  color: #8b949e;
  min-width: 100px;
  flex-shrink: 0;
  font-weight: 500;
}
.tool-inputs {
  display: flex;
  align-items: center;
  gap: 6px;
  flex: 1;
}
.tool-input {
  background: #0d1117;
  border: 1px solid #30363d;
  color: #e6edf3;
  padding: 5px 10px;
  border-radius: 6px;
  font-size: 12px;
  flex: 1;
  transition: border-color 0.15s;
}
.tool-input:focus {
  outline: none;
  border-color: #58a6ff;
}
.tool-input.small { width: 130px; flex: none; }

.tool-btn {
  background: #21262d;
  border: 1px solid #30363d;
  color: #e6edf3;
  padding: 5px 14px;
  border-radius: 6px;
  cursor: pointer;
  font-size: 12px;
  font-weight: 500;
  transition: background 0.12s;
}
.tool-btn:hover { background: #30363d; }
.tool-btn.approve { border-color: #238636; color: #3fb950; }
.tool-btn.approve:hover { background: rgba(35, 134, 54, 0.15); }
.tool-btn.reject { border-color: #da3633; color: #f85149; }
.tool-btn.reject:hover { background: rgba(218, 54, 51, 0.15); }
</style>
