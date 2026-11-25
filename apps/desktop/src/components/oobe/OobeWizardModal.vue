<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowLeft, ArrowRight, Check, Minus, Square, X } from '@lucide/vue'
import BrandLogo from '@/components/BrandLogo.vue'
import TinadecCalligraphy from '@/components/TinadecCalligraphy.vue'
import { UiButton } from '@/components/ui'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { usePlatform } from '@/composables/usePlatform'
import { useHostAccess } from '@/lib/hostAccess'
import { getNextStep, getPrevStep, useOobe, type OobeStepId } from '@/composables/useOobe'
import OobeStepWelcome from './OobeStepWelcome.vue'
import OobeStepProvider from './OobeStepProvider.vue'
import OobeStepWorkspace from './OobeStepWorkspace.vue'

const { t, te } = useI18n()
const { isElectron } = useHostAccess()
const { isMac, isElectronMac, isFullScreen } = usePlatform()
const { panelStyle, getPanelStyle, getPanelDataAttributes } = usePanelStyles()
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

const stepMeta: Record<OobeStepId, { num: string; key: string; fallback: string }> = {
  welcome: { num: '01', key: 'oobe.steps.welcome', fallback: '外观与语言' },
  provider: { num: '02', key: 'oobe.steps.provider', fallback: '模型与引擎' },
  workspace: { num: '03', key: 'oobe.steps.workspace', fallback: '工作区' },
}

const stepComponents = {
  welcome: OobeStepWelcome,
  provider: OobeStepProvider,
  workspace: OobeStepWorkspace,
} as const

const activeStepComponent = computed(() => stepComponents[currentStep.value])

// OOBE stage shell directly reflects the active material preset via computePanelStyle() /
// --material-floating-*, keeping zero ancestor transform so Chromium backdrop-filter samples cleanly.
const stageMaterialStyle = computed(() => {
  const computedStyle = getPanelStyle()
  if (panelStyle.value.effect === 'opaque') {
    return {
      background: 'var(--bg-primary)',
      backdropFilter: 'none',
      WebkitBackdropFilter: 'none',
    }
  }
  return computedStyle
})
const stageMaterialAttrs = computed(() => getPanelDataAttributes())

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

function minimizeWindow(): void {
  window.tinadec?.minimizeWindow?.()
}

function maximizeWindow(): void {
  window.tinadec?.maximizeWindow?.()
}

function closeWindow(): void {
  window.tinadec?.closeWindow?.()
}

function onGlobalKeydown(event: KeyboardEvent): void {
  if (!open.value || event.key !== 'Escape') return
  // Do not steal Escape from an open Reka select menu or directory picker modal.
  if (
    typeof document !== 'undefined' &&
    document.querySelector('[data-reka-popper-content-wrapper], [data-testid="server-directory-picker-modal"]')
  ) {
    return
  }
  event.preventDefault()
  void skip()
}

onMounted(() => {
  if (typeof window !== 'undefined') {
    window.addEventListener('keydown', onGlobalKeydown)
  }
})

onBeforeUnmount(() => {
  if (typeof window !== 'undefined') {
    window.removeEventListener('keydown', onGlobalKeydown)
  }
})
</script>

<template>
  <Teleport to="body">
    <div v-if="open" class="oobe-stage-host">
      <!-- Ambient backdrop sits behind the glass stage (never inside a transformed ancestor)
           so switching presets in Step 1 visibly demonstrates solid vs translucent vs frosted/crystal blur
           even before a custom wallpaper is configured, while letting real wallpapers shine through. -->
      <div class="oobe-ambient-backdrop" aria-hidden="true" />

      <div
        class="oobe-fullscreen-stage"
        :class="{ 'is-mac-framed': isElectronMac && !isFullScreen }"
        :style="stageMaterialStyle"
        v-bind="stageMaterialAttrs"
        role="dialog"
        aria-modal="true"
        :aria-label="label('oobe.title', '欢迎使用 TinadecOffice')"
        data-testid="oobe-wizard-modal"
      >
        <!-- Minimal Full-Screen Top Bar (respects macOS traffic lights & Windows frameless controls) -->
        <header class="oobe-topbar">
          <div class="oobe-topbar__brand">
            <BrandLogo :size="15" class="oobe-brand-icon" />
            <TinadecCalligraphy :size="14" class="oobe-brand-wordmark" />
          </div>

          <nav class="oobe-topbar__steps no-drag" :aria-label="label('oobe.stepsNav', '初始化步骤')">
            <ol class="oobe-rail">
              <li
                v-for="(step, idx) in steps"
                :key="step"
                class="oobe-rail__item"
                :class="{
                  'is-active': step === currentStep,
                  'is-completed': idx < stepIndex,
                }"
              >
                <button
                  type="button"
                  class="oobe-rail__btn"
                  :aria-current="step === currentStep ? 'step' : undefined"
                  :data-testid="`oobe-rail-${step}`"
                  @click="setOobeStep(step)"
                >
                  <span class="oobe-rail__index">
                    <Check v-if="idx < stepIndex" :size="11" aria-hidden="true" />
                    <template v-else>{{ stepMeta[step].num }}</template>
                  </span>
                  <span class="oobe-rail__label">{{ label(stepMeta[step].key, stepMeta[step].fallback) }}</span>
                </button>
                <span v-if="idx < steps.length - 1" class="oobe-rail__divider" aria-hidden="true" />
              </li>
            </ol>
          </nav>

          <div class="oobe-topbar__actions no-drag">
            <button
              type="button"
              class="oobe-skip-top"
              data-testid="oobe-close-btn"
              @click="skip"
            >
              {{ label('oobe.skipForNow', '稍后配置') }}
            </button>

            <template v-if="isElectron && !isMac">
              <span class="oobe-win-sep" aria-hidden="true" />
              <UiButton
                variant="ghost"
                size="icon"
                class="window-btn minimize"
                :title="label('app.minimize', '最小化')"
                :aria-label="label('app.minimize', '最小化')"
                @click="minimizeWindow"
              >
                <Minus :size="14" aria-hidden="true" />
              </UiButton>
              <UiButton
                variant="ghost"
                size="icon"
                class="window-btn maximize"
                :title="label('app.maximize', '最大化')"
                :aria-label="label('app.maximize', '最大化')"
                @click="maximizeWindow"
              >
                <Square :size="12" aria-hidden="true" />
              </UiButton>
              <UiButton
                variant="ghost"
                size="icon"
                class="window-btn close"
                :title="label('app.close', '关闭')"
                :aria-label="label('app.close', '关闭')"
                @click="closeWindow"
              >
                <X :size="14" aria-hidden="true" />
              </UiButton>
            </template>
          </div>
        </header>

        <!-- Centered Editorial Stage -->
        <main class="oobe-stage-scroll">
          <div class="oobe-stage-column">
            <div class="oobe-step-viewport">
              <Transition name="oobe-step-fade" mode="out-in">
                <component :is="activeStepComponent" :key="currentStep" />
              </Transition>
            </div>

            <footer class="oobe-stage-footer">
              <div class="oobe-stage-footer__left">
                <UiButton
                  v-if="canGoBack"
                  variant="ghost"
                  size="sm"
                  data-testid="oobe-back-btn"
                  @click="goBack"
                >
                  <ArrowLeft :size="14" />
                  <span>{{ label('oobe.back', '上一步') }}</span>
                </UiButton>
                <button
                  v-else
                  type="button"
                  class="oobe-skip-inline"
                  data-testid="oobe-skip-btn"
                  @click="skip"
                >
                  {{ label('oobe.enterDirectly', '先跳过，直接进入工作台') }}
                </button>
              </div>

              <div class="oobe-stage-footer__right">
                <UiButton
                  v-if="!isLastStep"
                  size="default"
                  data-testid="oobe-next-btn"
                  @click="goNext"
                >
                  <span>{{ label('oobe.next', '继续') }}</span>
                  <ArrowRight :size="14" />
                </UiButton>
                <UiButton
                  v-else
                  size="default"
                  data-testid="oobe-finish-btn"
                  @click="finish"
                >
                  <span>{{ label('oobe.finish', '进入工作台') }}</span>
                  <ArrowRight :size="14" />
                </UiButton>
              </div>
            </footer>
          </div>
        </main>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
/* Zero transform / zero filter on the host so backdrop-filter on .oobe-fullscreen-stage
   samples the real window background and ambient backdrop without Chromium compositing bugs. */
.oobe-stage-host {
  position: fixed;
  inset: 0;
  z-index: 1200;
}

.oobe-ambient-backdrop {
  position: absolute;
  inset: 0;
  pointer-events: none;
  background:
    radial-gradient(
      ellipse 560px 280px at 32% 14%,
      color-mix(in srgb, var(--accent-info, #3b82f6) 13%, transparent) 0%,
      transparent 72%
    ),
    radial-gradient(
      ellipse 520px 260px at 70% 18%,
      color-mix(in srgb, var(--accent-recovery, #8b5cf6) 11%, transparent) 0%,
      transparent 74%
    ),
    radial-gradient(
      ellipse 680px 340px at 50% 0%,
      color-mix(in srgb, var(--text-primary) 5%, transparent) 0%,
      transparent 75%
    );
}

.oobe-fullscreen-stage {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  background: var(--material-floating-bg, var(--surface-canvas, var(--bg-primary)));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-primary);
  overflow: hidden;
  transition: background-color 180ms ease;
}

.oobe-topbar {
  position: relative;
  z-index: 2;
  height: 48px;
  padding: 0 18px;
  display: grid;
  grid-template-columns: 1fr auto 1fr;
  align-items: center;
  gap: 12px;
  flex-shrink: 0;
  -webkit-app-region: drag;
}

.oobe-fullscreen-stage.is-mac-framed .oobe-topbar {
  padding-left: 84px;
}

.oobe-topbar__brand {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  color: var(--text-secondary);
  user-select: none;
}

.oobe-brand-icon,
.oobe-brand-wordmark {
  flex-shrink: 0;
}

.no-drag {
  -webkit-app-region: no-drag;
}

.oobe-topbar__steps {
  justify-self: center;
}

.oobe-rail {
  list-style: none;
  margin: 0;
  padding: 0;
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.oobe-rail__item {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.oobe-rail__btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: color 120ms ease, background-color 120ms ease;
}

.oobe-rail__btn:hover {
  color: var(--text-secondary);
  background: var(--surface-hover);
}

.oobe-rail__item.is-active .oobe-rail__btn {
  color: var(--text-primary);
  font-weight: 600;
}

.oobe-rail__item.is-completed .oobe-rail__btn {
  color: var(--text-secondary);
}

.oobe-rail__index {
  width: 18px;
  height: 18px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 10px;
  font-variant-numeric: tabular-nums;
}

.oobe-rail__item.is-active .oobe-rail__index {
  border-color: var(--text-primary);
  background: var(--bg-primary-button, var(--text-primary));
  color: var(--text-on-primary, var(--bg-primary));
}

.oobe-rail__item.is-completed .oobe-rail__index {
  border-color: var(--border-default, var(--border-muted));
  background: var(--surface-section);
  color: var(--text-primary);
}

.oobe-rail__divider {
  width: 18px;
  height: 1px;
  background: var(--border-muted);
}

@media (max-width: 720px) {
  .oobe-rail__label {
    display: none;
  }
}

.oobe-topbar__actions {
  justify-self: end;
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.oobe-skip-top {
  padding: 5px 10px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: color 120ms ease, background-color 120ms ease;
}

.oobe-skip-top:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.oobe-win-sep {
  width: 1px;
  height: 14px;
  margin: 0 4px;
  background: var(--border-muted);
}

.oobe-stage-scroll {
  position: relative;
  z-index: 1;
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  padding: 24px 24px 36px;
}

.oobe-stage-column {
  width: min(540px, 100%);
  margin: auto;
  display: flex;
  flex-direction: column;
  gap: 28px;
}

.oobe-step-viewport {
  min-height: 310px;
  display: flex;
  flex-direction: column;
  justify-content: center;
}

.oobe-stage-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding-top: 18px;
  border-top: 1px solid var(--border-muted);
}

.oobe-skip-inline {
  padding: 6px 2px;
  border: 0;
  background: transparent;
  color: var(--text-muted);
  font-size: 12.5px;
  cursor: pointer;
  transition: color 120ms ease;
}

.oobe-skip-inline:hover {
  color: var(--text-primary);
}

/* Opacity-only step transitions so inner surfaces never acquire an ancestor transform. */
.oobe-step-fade-enter-active,
.oobe-step-fade-leave-active {
  transition: opacity 150ms cubic-bezier(0.2, 0, 0, 1);
}

.oobe-step-fade-enter-from,
.oobe-step-fade-leave-to {
  opacity: 0;
}

@media (prefers-reduced-motion: reduce) {
  .oobe-fullscreen-stage,
  .oobe-step-fade-enter-active,
  .oobe-step-fade-leave-active {
    transition: none;
  }
}
</style>
