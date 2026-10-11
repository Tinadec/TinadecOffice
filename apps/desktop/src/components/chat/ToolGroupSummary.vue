<script setup lang="ts">
/**
 * A run of quiet steps, folded into one line: "读取 6 个文件".
 *
 * Showing every read buries the answer; hiding them makes the agent a black box. The line
 * exists to say "it looked at things, here is how many", and opening it gives the list back.
 */
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight } from '@lucide/vue'
import { toolIconOf } from '@/lib/toolPresentation'
import { foldSummary } from '@/lib/toolSentence'
import { toolKindOf } from '@/lib/toolPresentation'
import type { ToolCall } from '@/composables/useAgentActivity'

const props = defineProps<{ calls: ToolCall[] }>()
const { t } = useI18n()

const expanded = ref(false)
const summary = computed(() => foldSummary(props.calls.map((call) => toolKindOf(call.toolId))))
const icon = computed(() => toolIconOf(props.calls[0]?.toolId))

/** Distinct targets, so the line can name what was touched rather than only count steps. */
const targets = computed(() => {
  const seen = new Set<string>()
  for (const call of props.calls) {
    const name = call.argsSummary?.trim()
    if (name) seen.add(name)
  }
  return [...seen]
})
</script>

<template>
  <div class="tool-group" :data-testid="'tool-group'">
    <button
      type="button"
      class="tool-group-head"
      :aria-expanded="expanded"
      :aria-label="t(expanded ? 'agent.collapseGroup' : 'agent.expandGroup', { label: summary.label })"
      @click="expanded = !expanded"
    >
      <ChevronRight :size="13" class="tool-group-chevron" :class="{ expanded }" />
      <component :is="icon" :size="13" class="tool-group-icon" />
      <span class="tool-group-label">{{ summary.label }}</span>
      <span class="tool-group-count">{{ t('agent.groupSteps', { count: summary.steps }) }}</span>
      <span v-if="targets.length" class="tool-group-targets">{{ targets.join('、') }}</span>
    </button>
    <ul v-if="expanded" class="tool-group-items">
      <li v-for="call in calls" :key="call.id" :class="{ 'is-failed': call.status === 'failed' }">
        <span class="tool-group-verb">{{ call.toolName }}</span>
        <span class="tool-group-arg">{{ call.argsSummary }}</span>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.tool-group { min-width: 0; }

.tool-group-head {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  min-width: 0;
  min-height: 26px;
  padding: 3px 4px;
  border: 0;
  border-radius: 6px;
  color: var(--text-secondary);
  background: transparent;
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.tool-group-head:hover { background: var(--bg-hover); }
.tool-group-head:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 1px; }

.tool-group-chevron { flex-shrink: 0; color: var(--text-muted); transition: transform 0.15s ease; }
.tool-group-chevron.expanded { transform: rotate(90deg); }
.tool-group-icon { flex-shrink: 0; color: var(--text-muted); }
.tool-group-label { flex-shrink: 0; font-weight: 500; }
.tool-group-count { flex-shrink: 0; color: var(--text-muted); font-variant-numeric: tabular-nums; }
.tool-group-targets {
  min-width: 0;
  flex: 1;
  overflow: hidden;
  color: var(--text-chat-muted);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-group-items {
  list-style: none;
  margin: 2px 0 4px 25px;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-size: 11px;
  color: var(--text-chat-muted);
}

.tool-group-items li { display: flex; gap: 6px; min-width: 0; }
.tool-group-items li.is-failed { color: var(--accent-danger); }
.tool-group-verb { flex-shrink: 0; color: var(--text-secondary); }
.tool-group-arg { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

@media (prefers-reduced-motion: reduce) { .tool-group-chevron { transition: none; } }
</style>
