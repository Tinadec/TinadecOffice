<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowRight, Sparkles } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import { homeController } from '@/controllers/HomeController'
import { closeOobeWizard } from '@/composables/useOobe'

const { t, te } = useI18n()

function label(key: string, fallback: string): string {
  return te(key) ? String(t(key)) : fallback
}

const starterPrompts = computed(() => [
  {
    id: 'structure',
    text: label('oobe.firstTask.starter1', 'Explain the structure of this workspace folder'),
  },
  {
    id: 'topFiles',
    text: label('oobe.firstTask.starter2', 'List the 3 most important files to read first'),
  },
  {
    id: 'gitStatus',
    text: label('oobe.firstTask.starter3', 'Summarize recent git status and active branches'),
  },
])

async function useStarterPrompt(prompt: string): Promise<void> {
  homeController.updateDraft?.(prompt)
  await closeOobeWizard('complete')
}
</script>

<template>
  <div class="oobe-step" data-testid="oobe-step-first-task">
    <div class="oobe-step-hero">
      <h3>{{ label('oobe.firstTask.title', 'Ready to go!') }}</h3>
      <p>{{ label('oobe.firstTask.subtitle', 'Pick a safe read-only starter prompt or jump straight into your workspace.') }}</p>
    </div>

    <div class="oobe-starters">
      <div class="oobe-starters-title">
        <Sparkles :size="14" aria-hidden="true" />
        <span>{{ label('oobe.firstTask.starterPromptsTitle', 'Starter Prompts') }}</span>
      </div>
      <div class="oobe-starter-list">
        <div v-for="prompt in starterPrompts" :key="prompt.id" class="oobe-starter-card">
          <p>{{ prompt.text }}</p>
          <UiButton
            variant="outline"
            size="sm"
            :data-testid="`oobe-starter-${prompt.id}`"
            @click="useStarterPrompt(prompt.text)"
          >
            <span>{{ label('oobe.firstTask.usePrompt', 'Use prompt') }}</span>
            <ArrowRight :size="13" />
          </UiButton>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.oobe-step { display: flex; flex-direction: column; gap: 12px; }
.oobe-step-hero h3 { margin: 0 0 4px; font-size: 17px; color: var(--text-primary); }
.oobe-step-hero p { margin: 0; font-size: 13px; color: var(--text-secondary); line-height: 1.5; }
.oobe-starters { display: flex; flex-direction: column; gap: 8px; }
.oobe-starters-title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary);
}
.oobe-starter-list { display: flex; flex-direction: column; gap: 8px; }
.oobe-starter-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}
.oobe-starter-card p {
  margin: 0;
  font-size: 13px;
  color: var(--text-primary);
  line-height: 1.45;
}
</style>
