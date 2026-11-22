<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, ChevronLeft, ChevronRight, X } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import { getNextStep, getPrevStep, useOobe, type OobeStepId } from '@/composables/useOobe'
import OobeStepWelcome from './OobeStepWelcome.vue'
import OobeStepProvider from './OobeStepProvider.vue'
import OobeStepEcosystem from './OobeStepEcosystem.vue'
import OobeStepHabits from './OobeStepHabits.vue'
import OobeStepFirstTask from './OobeStepFirstTask.vue'

const { t, te } = useI18n()
const {
  steps,
  open,
  currentStep,
  stepIndex,
  canGoBack,
  isLastStep,
  setOobeStep,
  closeOobeWizard,
} = useOobe()

function label(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? String(t(key, params ?? {})) : fallback
}

const stepLabels: Record<OobeStepId, { key: string; fallback: string }> = {
  welcome: { key: 'oobe.steps.welcome', fallback: 'Welcome' },
  provider: { key: 'oobe.steps.provider', fallback: 'Model Setup' },
  ecosystem: { key: 'oobe.steps.ecosystem', fallback: 'Tools & Packs' },
  habits: { key: 'oobe.steps.habits', fallback: 'Habits' },
  firstTask: { key: 'oobe.steps.firstTask', fallback: 'First Task' },
}

const stepComponents = {
  welcome: OobeStepWelcome,
  provider: OobeStepProvider,
  ecosystem: OobeStepEcosystem,
  habits: OobeStepHabits,
  firstTask: OobeStepFirstTask,
} as const

const activeStepComponent = computed(() => stepComponents[currentStep.value])

async function goNext(): Promise<void> {
  const next = getNextStep(currentStep.value)
  if (next) {
    await setOobeStep(next)
  } else {
    await closeOobeWizard('complete')
  }
}

async function goBack(): Promise<void> {
  const prev = getPrevStep(currentStep.value)
  if (prev) {
    await setOobeStep(prev)
  }
}

async function skip(): Promise<void> {
  await closeOobeWizard('skip')
}

async function finish(): Promise<void> {
  await closeOobeWizard('complete')
}
</script>

<template>
  <Teleport to="body">
    <div
      v-if="open"
      class="oobe-backdrop"
      role="dialog"
      aria-modal="true"
      :aria-label="label('oobe.title', 'Get Started with TinadecOffice')"
      data-testid="oobe-wizard-modal"
    >
      <div class="oobe-modal">
        <header class="oobe-header">
          <div class="oobe-header__title">
            <h2>{{ label('oobe.title', 'Get Started with TinadecOffice') }}</h2>
            <span class="oobe-step-counter">
              {{ label('oobe.stepCounter', `Step ${stepIndex + 1} of ${steps.length}`, { current: stepIndex + 1, total: steps.length }) }}
            </span>
          </div>
          <UiButton
            variant="ghost"
            size="icon"
            :aria-label="label('oobe.skipForNow', 'Skip for now')"
            data-testid="oobe-close-btn"
            @click="skip"
          >
            <X :size="16" />
          </UiButton>
        </header>

        <ol class="oobe-stepper" aria-label="Onboarding steps">
          <li
            v-for="(step, idx) in steps"
            :key="step"
            class="oobe-stepper__item"
            :class="{
              active: step === currentStep,
              completed: idx < stepIndex,
            }"
          >
            <button
              type="button"
              class="oobe-stepper__btn"
              :aria-current="step === currentStep ? 'step' : undefined"
              @click="setOobeStep(step)"
            >
              <span class="oobe-stepper__dot">
                <Check v-if="idx < stepIndex" :size="11" />
                <span v-else>{{ idx + 1 }}</span>
              </span>
              <span class="oobe-stepper__label">{{ label(stepLabels[step].key, stepLabels[step].fallback) }}</span>
            </button>
          </li>
        </ol>

        <div class="oobe-body">
          <component :is="activeStepComponent" />
        </div>

        <footer class="oobe-footer">
          <UiButton variant="ghost" size="sm" data-testid="oobe-skip-btn" @click="skip">
            {{ label('oobe.skipForNow', 'Skip for now') }}
          </UiButton>
          <div class="oobe-footer__nav">
            <UiButton
              v-if="canGoBack"
              variant="outline"
              size="sm"
              data-testid="oobe-back-btn"
              @click="goBack"
            >
              <ChevronLeft :size="14" />
              <span>{{ label('oobe.back', 'Back') }}</span>
            </UiButton>
            <UiButton
              v-if="!isLastStep"
              size="sm"
              data-testid="oobe-next-btn"
              @click="goNext"
            >
              <span>{{ label('oobe.next', 'Next') }}</span>
              <ChevronRight :size="14" />
            </UiButton>
            <UiButton
              v-else
              size="sm"
              data-testid="oobe-finish-btn"
              @click="finish"
            >
              <span>{{ label('oobe.finish', 'Enter Workspace') }}</span>
            </UiButton>
          </div>
        </footer>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.oobe-backdrop {
  position: fixed;
  inset: 0;
  z-index: 1200;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 20px;
  background: rgba(10, 12, 18, 0.58);
  backdrop-filter: blur(8px);
}

.oobe-modal {
  width: min(680px, 100%);
  max-height: min(86vh, 720px);
  display: flex;
  flex-direction: column;
  border-radius: 16px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  box-shadow: 0 24px 64px rgba(0, 0, 0, 0.36);
  overflow: hidden;
}

.oobe-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 14px 18px 10px;
  border-bottom: 1px solid var(--border-muted);
}

.oobe-header__title {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex-wrap: wrap;
}

.oobe-header__title h2 {
  margin: 0;
  font-size: 16px;
  color: var(--text-primary);
}

.oobe-step-counter {
  font-size: 12px;
  color: var(--text-muted);
}

.oobe-stepper {
  list-style: none;
  margin: 0;
  padding: 10px 18px;
  display: flex;
  align-items: center;
  gap: 6px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--surface-raised);
  overflow-x: auto;
}

.oobe-stepper__item {
  flex: 1 0 auto;
}

.oobe-stepper__btn {
  width: 100%;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 5px 8px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  font-size: 12px;
}

.oobe-stepper__item.active .oobe-stepper__btn {
  color: var(--text-primary);
  font-weight: 600;
  background: var(--surface-section);
}

.oobe-stepper__item.completed .oobe-stepper__btn {
  color: var(--text-secondary);
}

.oobe-stepper__dot {
  width: 18px;
  height: 18px;
  border-radius: 999px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-size: 11px;
  border: 1px solid var(--border-muted);
}

.oobe-stepper__item.active .oobe-stepper__dot {
  border-color: var(--accent-primary);
  background: var(--accent-primary);
  color: #fff;
}

.oobe-stepper__item.completed .oobe-stepper__dot {
  border-color: var(--accent-success, #22c55e);
  background: color-mix(in srgb, var(--accent-success, #22c55e) 18%, transparent);
  color: var(--accent-success, #22c55e);
}

.oobe-body {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 18px;
}

.oobe-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 18px;
  border-top: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.oobe-footer__nav {
  display: flex;
  align-items: center;
  gap: 8px;
}
</style>
