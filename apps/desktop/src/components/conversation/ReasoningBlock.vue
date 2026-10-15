<script setup lang="ts">
import { Brain, ChevronRight } from '@lucide/vue'
import { computed, ref, toRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'
import { Spinner } from '@/components/ui/spinner'
import { useAutoDisclosure } from '@/composables/useAutoDisclosure'
import { useDelayedLoading } from '@/composables/useDelayedLoading'
import { useElapsed } from '@/composables/useElapsed'
import { useJustSettled } from '@/composables/useJustSettled'
import { formatDuration } from '@/lib/conversation/format'
import { latestReasoningTitle, reasoningSections } from '@/lib/conversation/reasoning'
import type { ReasoningEntry } from '@/lib/conversation/reducer'

/**
 * One model call's reasoning, whole — not a 160-character tail. While the model thinks the
 * block is open and its status line reads the newest closed section title with a live timer;
 * when the thought ends it folds to "Thought for 12s". Once the reader opens or closes it,
 * it stays that way. Very long reasoning is height-limited with a fade and "Show all".
 */
const props = defineProps<{ entry: ReasoningEntry }>()
const { t } = useI18n()

const thinking = computed(() => props.entry.status === 'running')
const { open } = useAutoDisclosure(thinking)
const showSpinner = useDelayedLoading(thinking)
// "Thought for 12s" sweeps once when the thought ends here; history arrives already still.
const justSettled = useJustSettled(thinking)
const elapsed = useElapsed(toRef(() => props.entry.startedAt), toRef(() => props.entry.endedAt))
const sections = computed(() => reasoningSections(props.entry.text))
const title = computed(() => latestReasoningTitle(props.entry.text))
const showAll = ref(false)
const long = computed(() => props.entry.text.length + props.entry.output.length > 1200)

const label = computed(() => {
  // A worker that writes without thinking aloud is drafting, not "thinking".
  if (thinking.value) return title.value ?? t(props.entry.output && !props.entry.text ? 'conversation.reasoning.drafting' : 'conversation.reasoning.thinking')
  return t('conversation.reasoning.thoughtFor', { duration: formatDuration(elapsed.value ?? 0, t) })
})
</script>

<template>
  <Collapsible v-model:open="open" class="conversation-reasoning" data-testid="reasoning-block">
    <CollapsibleTrigger class="conversation-fold-trigger" :data-live="thinking ? 'true' : undefined">
      <Spinner decorative v-if="showSpinner" class="conversation-glyph" />
      <Brain v-else class="conversation-glyph" aria-hidden="true" />
      <span :class="{ shimmer: thinking, 'shimmer-once': justSettled }" data-testid="reasoning-label" :role="thinking ? 'status' : undefined">{{ label }}</span>
      <span v-if="thinking && elapsed !== null" class="conversation-fold-meta">{{ formatDuration(elapsed, t) }}</span>
      <ChevronRight class="conversation-fold-chevron" aria-hidden="true" />
    </CollapsibleTrigger>
    <CollapsibleContent class="conversation-fold-content">
      <div class="conversation-reasoning-body" :class="{ 'is-clamped': long && !showAll }">
        <section v-for="(section, index) in sections" :key="index">
          <h4 v-if="section.title" class="conversation-reasoning-title">{{ section.title }}</h4>
          <p v-if="section.body" class="conversation-reasoning-text">{{ section.body }}</p>
        </section>
        <!-- What the worker writes as it goes (its draft), after what it thought. -->
        <p v-if="entry.output" class="conversation-reasoning-text conversation-reasoning-output" data-testid="reasoning-output">{{ entry.output }}</p>
      </div>
      <button v-if="long && !showAll" type="button" class="conversation-link-button" @click="showAll = true">{{ t('conversation.reasoning.showAll') }}</button>
    </CollapsibleContent>
  </Collapsible>
</template>

<style scoped>
.conversation-reasoning-body { display: flex; flex-direction: column; gap: 6px; padding: 2px 0 4px 20px; border-left: 1px solid var(--border-card); margin-left: 6px; color: var(--text-secondary); font-size: 12px; line-height: 1.55; }
.conversation-reasoning-body.is-clamped { max-height: 18rem; overflow: hidden; mask-image: linear-gradient(to bottom, black 70%, transparent); }
.conversation-reasoning-title { margin: 0; font-size: 12px; font-weight: 600; color: var(--text-primary); }
.conversation-reasoning-text { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.conversation-reasoning-output { color: var(--text-primary); }
</style>
