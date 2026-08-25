<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Play, FastForward, Pause, RotateCcw, ChevronDown, ChevronUp } from '@lucide/vue'
import type { SimulationMode, SimulateMessageRequest, ForceApprovalDecisionRequest } from '../types/simulation'

const { t } = useI18n()

const props = defineProps<{
  mode: SimulationMode
  currentStep: number
  totalSteps: number
}>()

const emit = defineEmits<{
  step: []
  run: []
  pause: []
  reset: []
  'inject-message': [request: SimulateMessageRequest]
  'inject-tool-result': [request: { run_id: string; tool_id: string; status: string; summary?: string }]
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
      <div class="simulator-controls">
        <button class="sim-btn" :disabled="mode === 'idle'" @click="emit('step')" :title="t('debugStudio.step')"><Play :size="12" /></button>
        <button class="sim-btn" :disabled="mode === 'idle'" @click="emit('run')" :title="t('debugStudio.run')"><FastForward :size="12" /></button>
        <button class="sim-btn" :disabled="mode !== 'running'" @click="emit('pause')" :title="t('debugStudio.pause')"><Pause :size="12" /></button>
        <button class="sim-btn" @click="emit('reset')" :title="t('debugStudio.reset')"><RotateCcw :size="12" /></button>
        <span class="sim-step" v-if="totalSteps > 0">{{ t('debugStudio.stepProgress', { current: currentStep, total: totalSteps }) }}</span>
        <span class="sim-mode" :class="mode">{{ mode }}</span>
      </div>
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
  justify-content: space-between;
  padding: 6px 12px;
}

.simulator-controls {
  display: flex;
  align-items: center;
  gap: 4px;
}

.sim-btn {
  background: #21262d;
  border: 1px solid #30363d;
  color: #e6edf3;
  padding: 4px 8px;
  border-radius: 6px;
  cursor: pointer;
  font-size: 12px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background 0.12s, border-color 0.12s;
}
.sim-btn:hover:not(:disabled) {
  background: #30363d;
  border-color: #484f58;
}
.sim-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.sim-step {
  font-size: 11px;
  color: #8b949e;
  margin-left: 10px;
  font-variant-numeric: tabular-nums;
}

.sim-mode {
  font-size: 10px;
  font-weight: 700;
  padding: 2px 8px;
  border-radius: 10px;
  margin-left: 8px;
  letter-spacing: 0.3px;
}
.sim-mode.idle { background: #6e7681; color: #fff; }
.sim-mode.running { background: #238636; color: #fff; }
.sim-mode.paused { background: #d29922; color: #fff; }

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
