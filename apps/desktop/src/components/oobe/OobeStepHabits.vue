<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ShieldCheck, Sparkles, PawPrint } from '@lucide/vue'
import { UiCheckbox, UiSelectField } from '@/components/ui'
import type { PermissionLevel } from '@/types/mode'
import { homeController } from '@/controllers/HomeController'
import { useHostAccess } from '@/lib/hostAccess'
import { queueDeferredHabit, useOobe } from '@/composables/useOobe'

const { t, te } = useI18n()
const { isPreview } = useHostAccess()
const { deferredHabits } = useOobe()

function label(key: string, fallback: string): string {
  return te(key) ? String(t(key)) : fallback
}

const isElectron = computed(() => {
  if (typeof window === 'undefined') return false
  const bridge = window.tinadec as (typeof window.tinadec & { isWebShim?: boolean }) | undefined
  return Boolean(bridge && !bridge.isWebShim && !isPreview.value && typeof bridge.saveCompanionMode === 'function')
})

const approvalPosture = ref<string>(homeController.currentPermission.value || 'ask')
const companionEnabled = ref<boolean>(deferredHabits.value.companionCapsule ?? false)
const petEnabled = ref<boolean>(deferredHabits.value.desktopPet ?? false)

const approvalOptions = computed(() => [
  { value: 'ask', label: label('composer.permissionAsk', 'Ask before actions') },
  { value: 'auto-edit', label: label('composer.permissionAutoEdit', 'Auto-approve file edits') },
  { value: 'plan', label: label('composer.permissionPlan', 'Plan first') },
  { value: 'delegate-advisor', label: label('composer.permissionDelegateAdvisor', 'Delegate to advisor') },
])

function onChangeApproval(next: string): void {
  approvalPosture.value = next
  if (typeof homeController.updatePermission === 'function') {
    void homeController.updatePermission(next as PermissionLevel)
  }
}

function onToggleCompanion(value: boolean): void {
  companionEnabled.value = value
  // Defer floating companion capsule creation until OOBE finishes/exits!
  queueDeferredHabit('companionCapsule', value)
}

function onTogglePet(value: boolean): void {
  petEnabled.value = value
  // Defer alwaysOnTop desktop pet creation until OOBE finishes/exits!
  queueDeferredHabit('desktopPet', value)
}
</script>

<template>
  <div class="oobe-step" data-testid="oobe-step-habits">
    <div class="oobe-step-hero">
      <h3>{{ label('oobe.habits.title', 'Defaults & Desktop Habits') }}</h3>
      <p>{{ label('oobe.habits.subtitle', 'Customize approval posture and optional desktop companions (all skippable).') }}</p>
    </div>

    <div class="oobe-habit-card">
      <div class="oobe-habit-head">
        <ShieldCheck :size="16" aria-hidden="true" />
        <strong>{{ label('oobe.habits.approvalTitle', 'Default Tool Approval Posture') }}</strong>
      </div>
      <UiSelectField
        :model-value="approvalPosture"
        :options="approvalOptions"
        :aria-label="label('oobe.habits.approvalTitle', 'Default Tool Approval Posture')"
        @update:model-value="onChangeApproval"
      />
    </div>

    <div v-if="isElectron" class="oobe-habit-card">
      <label class="oobe-check-row">
        <UiCheckbox
          :model-value="companionEnabled"
          :aria-label="label('oobe.habits.companionLabel', 'Enable Companion Capsule')"
          @update:model-value="onToggleCompanion(Boolean($event))"
        />
        <Sparkles :size="15" aria-hidden="true" />
        <span>{{ label('oobe.habits.companionLabel', 'Enable Companion Capsule') }}</span>
      </label>

      <label class="oobe-check-row">
        <UiCheckbox
          :model-value="petEnabled"
          :aria-label="label('oobe.habits.petLabel', 'Enable Desktop Pet (opens after setup finishes)')"
          data-testid="oobe-deferred-pet-toggle"
          @update:model-value="onTogglePet(Boolean($event))"
        />
        <PawPrint :size="15" aria-hidden="true" />
        <span>{{ label('oobe.habits.petLabel', 'Enable Desktop Pet (opens after setup finishes)') }}</span>
      </label>

      <p class="oobe-habit-hint">
        {{ label('oobe.habits.petDeferredHint', 'Pet windows stay deferred until onboarding finishes so they never cover this guide.') }}
      </p>
    </div>
  </div>
</template>

<style scoped>
.oobe-step { display: flex; flex-direction: column; gap: 12px; }
.oobe-step-hero h3 { margin: 0 0 4px; font-size: 17px; color: var(--text-primary); }
.oobe-step-hero p { margin: 0; font-size: 13px; color: var(--text-secondary); line-height: 1.5; }
.oobe-habit-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border-radius: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}
.oobe-habit-head { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--text-primary); }
.oobe-check-row {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  color: var(--text-primary);
  cursor: pointer;
}
.oobe-habit-hint { margin: 0; font-size: 12px; color: var(--text-secondary); line-height: 1.45; }
</style>
