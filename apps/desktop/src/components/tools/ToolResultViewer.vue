<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Check, ChevronDown, ChevronRight, Copy, Folder } from '@lucide/vue'
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

/**
 * What the record actually carries. `ToolExecutionTimelineItemDto` has no result payload
 * — Core hands the provider's JSON to whoever executed the tool, and the durable
 * execution record keeps only human-readable governance lines. Measured from Core's own
 * producers: `state_owner: core`, `approval_path: user_tool_action`,
 * `provider_tool_id: <id>`, `git_action: <action>`
 * (`DirectToolEndpoints.cs:205,244,262`).
 *
 * This file used to render four structured views — file list, grep hits, diff, shell
 * output — by regexing `files: a;b` / `path:line:text` / `diff --git` out of those lines.
 * None of those shapes is ever emitted, and the views were additionally gated on tool ids
 * that do not exist, so all four were dead. `checkpoint_summary` is excluded for the same
 * reason: Core's only producer sets it to `string.Empty`
 * (`DmaeaEndpoints.cs:793,822`).
 */
const resultText = computed(() => {
  const items: string[] = []
  if (props.toolExecution.summary) items.push(props.toolExecution.summary)
  items.push(...props.toolExecution.evidence)
  return items.join('\n')
})

const evidenceLines = computed(() => props.toolExecution.evidence)

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
      <div class="tool-result-default">
        <div v-if="toolExecution.summary" class="tool-result-summary">
          {{ toolExecution.summary }}
        </div>
        <div v-if="evidenceLines.length > 0" class="tool-result-evidence">
          <div v-for="(line, idx) in evidenceLines" :key="idx" class="tool-result-evidence-row">
            <Folder v-if="line.includes('/')" :size="11" />
            <span>{{ line }}</span>
          </div>
        </div>
        <p v-if="!toolExecution.summary && evidenceLines.length === 0" class="tool-result-empty">
          No result was recorded for this call.
        </p>
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

.tool-result-empty {
  margin: 0;
  color: var(--text-muted);
  font-size: 11px;
  font-style: italic;
}
</style>
