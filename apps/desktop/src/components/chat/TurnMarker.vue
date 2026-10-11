<script setup lang="ts">
/**
 * A quiet note between content: a lifecycle event rather than a step the agent took.
 *
 * "The run failed", "the run was cancelled", "the goal was adjusted" are facts about the
 * turn, not work the agent did — rendering them as another step in the reasoning trail made
 * them read as something the model decided. This is the row for them: icon, one line, muted.
 *
 * Taken from the reference material's marker: a separator variant for wider annotations, and
 * severity carried by the glyph's colour rather than a badge, matching the chat language
 * everywhere else in this transcript.
 */
import { computed } from 'vue'
import { Info, AlertTriangle, XCircle, CheckCircle2 } from '@lucide/vue'

const props = withDefaults(defineProps<{
  severity?: string | null
  /** `separator` puts hairlines either side, for a break in the flow. */
  variant?: 'plain' | 'separator'
}>(), {
  severity: null,
  variant: 'plain',
})

const icon = computed(() => {
  switch (props.severity) {
    case 'error':
    case 'critical': return XCircle
    case 'warning': return AlertTriangle
    case 'success': return CheckCircle2
    default: return Info
  }
})
</script>

<template>
  <div class="marker" :class="[`severity-${severity ?? 'info'}`, `variant-${variant}`]" role="note">
    <span v-if="variant === 'separator'" class="marker-rule" aria-hidden="true" />
    <component :is="icon" :size="12" class="marker-icon" aria-hidden="true" />
    <span class="marker-text"><slot /></span>
    <span v-if="variant === 'separator'" class="marker-rule" aria-hidden="true" />
  </div>
</template>

<style scoped>
.marker {
  display: flex;
  align-items: center;
  gap: 6px;
  min-height: 22px;
  min-width: 0;
  padding: 1px 2px;
  color: var(--text-chat-muted);
  font-size: 11px;
  line-height: 1.45;
}

.marker-icon { flex-shrink: 0; }

.marker-text {
  min-width: 0;
  overflow-wrap: anywhere;
}

.marker.severity-warning .marker-icon { color: var(--accent-warning); }
.marker.severity-error .marker-icon,
.marker.severity-critical .marker-icon { color: var(--accent-danger); }
.marker.severity-success .marker-icon { color: var(--accent-success); }

.marker-rule {
  flex: 1;
  height: 1px;
  min-width: 12px;
  background: color-mix(in srgb, var(--border-muted) 70%, transparent);
}

.marker.variant-separator { justify-content: center; }
.marker.variant-separator .marker-text { flex: 0 1 auto; }
</style>
