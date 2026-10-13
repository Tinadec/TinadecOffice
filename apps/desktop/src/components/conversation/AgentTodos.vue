<script setup lang="ts">
import { Circle, CircleCheck, CircleDot } from '@lucide/vue'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import type { PlanEntry, PlanStep } from '@/lib/conversation/reducer'

/**
 * An agent's working plan (plan_update). Each update replaces the plan in place, so this is
 * its current state; steps that are new or reworded since the previous version are marked,
 * with the agent's one-line explanation. Long plans show six steps and fold the rest.
 */
const props = defineProps<{ entry: PlanEntry }>()
const { t } = useI18n()
const { displayName } = useAgentDirectory()
const LIMIT = 6

const expanded = ref(false)
const done = computed(() => props.entry.steps.filter((step) => step.status === 'completed').length)
const previous = computed(() => new Set((props.entry.previous ?? []).map((step) => step.step)))
const visible = computed(() => (expanded.value ? props.entry.steps : props.entry.steps.slice(0, LIMIT)))
const hidden = computed(() => props.entry.steps.length - visible.value.length)
const isNew = (step: PlanStep) => props.entry.previous !== null && !previous.value.has(step.step)
const who = computed(() => (props.entry.handle ? displayName(props.entry.handle) : ''))
</script>

<template>
  <div class="conversation-todos" data-testid="agent-todos">
    <p class="conversation-todos-head">
      <span>{{ t('conversation.plan.updated', { done, total: entry.steps.length }) }}</span>
      <span v-if="who" class="conversation-todos-owner">{{ who }}</span>
    </p>
    <p v-if="entry.explanation && entry.previous" class="conversation-todos-explanation">{{ entry.explanation }}</p>
    <ol class="conversation-todos-list" role="list">
      <li v-for="step in visible" :key="step.step" class="conversation-todo" :data-status="step.status" :class="{ 'is-new': isNew(step) }">
        <CircleCheck v-if="step.status === 'completed'" class="conversation-meta-glyph is-done" aria-hidden="true" />
        <CircleDot v-else-if="step.status === 'in_progress'" class="conversation-meta-glyph is-active" aria-hidden="true" />
        <Circle v-else class="conversation-meta-glyph" aria-hidden="true" />
        <span>{{ step.step }}</span>
        <span class="sr-only">{{ t(`conversation.plan.status.${step.status}`) }}</span>
      </li>
    </ol>
    <button v-if="hidden > 0" type="button" class="conversation-link-button" @click="expanded = true">{{ t('conversation.plan.more', { count: hidden }) }}</button>
  </div>
</template>

<style scoped>
.conversation-todos { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--text-secondary); }
.conversation-todos-head { display: flex; gap: 8px; margin: 0; }
.conversation-todos-owner { color: var(--text-muted); font-size: 11px; }
.conversation-todos-explanation { margin: 0; color: var(--text-muted); font-size: 11px; }
.conversation-todos-list { display: flex; flex-direction: column; gap: 2px; margin: 0; padding: 0 0 0 20px; list-style: none; }
.conversation-todo { display: flex; align-items: center; gap: 6px; }
.conversation-todo[data-status='completed'] { color: var(--text-muted); text-decoration: line-through; text-decoration-color: color-mix(in srgb, currentColor 50%, transparent); }
.conversation-todo[data-status='in_progress'] { color: var(--text-primary); }
.conversation-todo.is-new { background: color-mix(in srgb, var(--accent-primary) 10%, transparent); border-radius: 4px; }
</style>
