<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Handle, Position } from '@vue-flow/core'
import { AlertCircle, Copy, Cpu, Crown, Trash2, Workflow, Wrench } from '@lucide/vue'

export interface ModeAgentNodeData {
  agentName: string
  customLabel?: string | null
  lane: 'operation' | 'execution'
  roleLabel: string
  strategyBadge: string
  strategyKind: 'inherit' | 'route' | 'fixed'
  strategyOverridden?: boolean
  toolCount: number
  hasWildcardTools?: boolean
  isConversation: boolean
  unbound: boolean
  selected: boolean
  highlighted?: boolean
  issueSeverity?: 'error' | 'warning' | null
  readonly?: boolean
}

const props = defineProps<{
  id: string
  data: ModeAgentNodeData
}>()

const emit = defineEmits<{
  'set-conversation': [id: string]
  'clone-node': [id: string]
  'delete-node': [id: string]
}>()

const { t } = useI18n()

const showCustomLabel = computed(() => {
  const custom = (props.data.customLabel ?? '').trim()
  if (!custom) return false
  return custom.toLowerCase() !== props.data.agentName.trim().toLowerCase()
})

const toolsLabel = computed(() =>
  props.data.hasWildcardTools
    ? t('settings.modeAllTools')
    : t('canvas.toolCount', { n: props.data.toolCount }),
)
</script>

<template>
  <div
    class="mode-agent-node"
    :class="{
      sel: data.selected,
      highlighted: data.highlighted,
      op: data.lane === 'operation',
      ex: data.lane === 'execution',
      unbound: data.unbound,
      'has-error': data.issueSeverity === 'error',
      'has-warning': data.issueSeverity === 'warning',
      readonly: data.readonly,
    }"
    :data-node-id="id"
  >
    <!-- Primary vertical target on execution lane -->
    <Handle
      v-if="!data.readonly && data.lane === 'execution'"
      id="top-target"
      type="target"
      :position="Position.Top"
      class="mode-handle mode-handle-v"
    />

    <!-- Optional same-lane horizontal handles -->
    <Handle
      v-if="!data.readonly"
      id="left-target"
      type="target"
      :position="Position.Left"
      class="mode-handle mode-handle-h"
    />

    <div class="node-head">
      <span class="node-lane-icon" :class="data.lane">
        <Workflow v-if="data.lane === 'operation'" :size="13" />
        <Cpu v-else :size="13" />
      </span>
      <div class="node-title-wrap">
        <strong class="node-title" :title="data.agentName">{{ data.agentName }}</strong>
        <span v-if="showCustomLabel" class="node-subtitle" :title="data.customLabel ?? undefined">
          {{ data.customLabel }}
        </span>
      </div>
      <span
        v-if="data.isConversation"
        class="node-crown-badge"
        :title="t('canvas.conversationBadge')"
      >
        <Crown :size="11" />
        <span>{{ t('canvas.conversationBadge') }}</span>
      </span>
      <span
        v-else-if="data.issueSeverity"
        class="node-issue-dot"
        :class="data.issueSeverity"
        aria-hidden="true"
      >
        <AlertCircle :size="13" />
      </span>
    </div>

    <div class="node-role-line">
      <span class="node-role" :title="data.roleLabel">{{ data.roleLabel }}</span>
    </div>

    <div class="node-pills">
      <span
        class="node-pill strategy"
        :class="{ overridden: data.strategyOverridden, [data.strategyKind]: true }"
        :title="data.strategyBadge"
      >
        {{ data.strategyBadge }}
      </span>
      <span class="node-pill tools" :title="toolsLabel">
        <Wrench :size="10" />
        <span>{{ toolsLabel }}</span>
      </span>
    </div>

    <!-- Hover quick actions -->
    <div v-if="!data.readonly" class="node-quick-actions nodrag nopan">
      <button
        v-if="data.lane === 'operation' && !data.isConversation"
        type="button"
        class="node-qa-btn"
        :title="t('canvas.setConversation')"
        :aria-label="t('canvas.setConversation')"
        @click.stop="emit('set-conversation', id)"
      >
        <Crown :size="12" />
      </button>
      <button
        type="button"
        class="node-qa-btn"
        :title="t('canvas.cloneNode')"
        :aria-label="t('canvas.cloneNode')"
        @click.stop="emit('clone-node', id)"
      >
        <Copy :size="12" />
      </button>
      <button
        type="button"
        class="node-qa-btn danger"
        :title="t('canvas.deleteNode')"
        :aria-label="t('canvas.deleteNode')"
        @click.stop="emit('delete-node', id)"
      >
        <Trash2 :size="12" />
      </button>
    </div>

    <Handle
      v-if="!data.readonly"
      id="right-source"
      type="source"
      :position="Position.Right"
      class="mode-handle mode-handle-h"
    />

    <!-- Primary vertical source (Governance -> Execution, Execution -> Execution) -->
    <Handle
      v-if="!data.readonly"
      id="bottom-source"
      type="source"
      :position="Position.Bottom"
      class="mode-handle mode-handle-v"
    />
  </div>
</template>

<style scoped>
.mode-agent-node {
  position: relative;
  width: 224px;
  min-height: 102px;
  padding: 10px 12px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  box-shadow: 0 2px 8px color-mix(in srgb, #000 10%, transparent);
  display: grid;
  gap: 7px;
  transition: border-color 140ms ease, box-shadow 140ms ease, transform 120ms ease;
}

.mode-agent-node.op {
  border-top: 3px solid color-mix(in srgb, var(--accent-primary) 82%, transparent);
}

.mode-agent-node.ex {
  border-top: 3px solid color-mix(in srgb, var(--accent-warning, #d97706) 75%, transparent);
}

.mode-agent-node.unbound {
  border-style: dashed;
}

.mode-agent-node.sel {
  border-color: var(--accent-primary);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 26%, transparent), 0 6px 16px color-mix(in srgb, #000 14%, transparent);
}

.mode-agent-node.highlighted {
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-danger, #ef4444) 38%, transparent);
}

.mode-agent-node.has-error:not(.sel) {
  border-color: color-mix(in srgb, var(--accent-danger, #ef4444) 72%, var(--border-muted));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-danger, #ef4444) 18%, transparent);
}

.mode-agent-node.has-warning:not(.has-error):not(.sel) {
  border-color: color-mix(in srgb, var(--accent-warning, #d97706) 60%, var(--border-muted));
}

.node-head {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  min-width: 0;
}

.node-lane-icon {
  width: 24px;
  height: 24px;
  border-radius: 7px;
  display: grid;
  place-items: center;
  flex-shrink: 0;
  background: var(--surface-section);
  color: var(--text-secondary);
}

.node-lane-icon.op {
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-section));
}

.node-lane-icon.ex {
  color: var(--accent-warning, #d97706);
  background: color-mix(in srgb, var(--accent-warning, #d97706) 12%, var(--surface-section));
}

.node-title-wrap {
  flex: 1;
  min-width: 0;
  display: grid;
  gap: 1px;
}

.node-title {
  font-size: 13px;
  font-weight: 600;
  line-height: 1.3;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.node-subtitle {
  font-size: 11px;
  line-height: 1.25;
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.node-crown-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  flex-shrink: 0;
  padding: 2px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent-primary) 34%, transparent);
}

.node-issue-dot {
  flex-shrink: 0;
  display: grid;
  place-items: center;
}

.node-issue-dot.error { color: var(--accent-danger, #ef4444); }
.node-issue-dot.warning { color: var(--accent-warning, #d97706); }

.node-role-line {
  display: flex;
  align-items: center;
  min-width: 0;
}

.node-role {
  font-size: 11px;
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.node-pills {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  min-width: 0;
}

.node-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 7px;
  border-radius: 999px;
  font-size: 10px;
  line-height: 1.4;
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  color: var(--text-secondary);
  max-width: 118px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.node-pill.strategy.overridden {
  border-color: color-mix(in srgb, var(--accent-primary) 45%, var(--border-muted));
  color: var(--accent-primary);
}

.node-pill.tools {
  flex-shrink: 0;
}

.node-quick-actions {
  position: absolute;
  top: -12px;
  right: 8px;
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  box-shadow: 0 3px 10px color-mix(in srgb, #000 14%, transparent);
  opacity: 0;
  pointer-events: none;
  transform: translateY(2px);
  transition: opacity 120ms ease, transform 120ms ease;
  z-index: 4;
}

.mode-agent-node:hover .node-quick-actions,
.mode-agent-node:focus-within .node-quick-actions,
.mode-agent-node.sel .node-quick-actions {
  opacity: 1;
  pointer-events: auto;
  transform: translateY(0);
}

.node-qa-btn {
  width: 22px;
  height: 22px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.node-qa-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.node-qa-btn.danger:hover {
  color: var(--accent-danger, #ef4444);
}

:deep(.mode-handle) {
  width: 14px;
  height: 14px;
  border-radius: 999px;
  background: var(--surface-raised);
  border: 2px solid var(--accent-primary);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--surface-raised) 85%, transparent);
  transition: transform 120ms ease, background-color 120ms ease, border-color 120ms ease;
}

:deep(.mode-handle:hover) {
  transform: scale(1.22);
  background: var(--accent-primary);
}

:deep(.mode-handle.valid) {
  background: var(--accent-success, #22c55e);
  border-color: var(--accent-success, #22c55e);
  transform: scale(1.25);
}

:deep(.mode-handle.connecting:not(.valid)) {
  background: var(--accent-danger, #ef4444);
  border-color: var(--accent-danger, #ef4444);
}
</style>
