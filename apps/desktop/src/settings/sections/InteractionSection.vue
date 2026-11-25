<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, FolderOpen, RotateCcw } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import {
  useUiPreferences,
  type UiReducedMotionPreference,
  type UiSpatialPromoteMode,
  type UiWorkspaceDropAction,
} from '@/composables/useUiPreferences'

const { t } = useI18n()
const { notify } = useNotifications()
const {
  interaction,
  filePath,
  saving,
  updateInteraction,
  resetPreferences,
} = useUiPreferences()

const resolvedTomlPath = computed(() => filePath.value || '~/.tinadec/preferences.toml')

const dropOptions = computed<Array<{ value: UiWorkspaceDropAction; label: string; desc: string }>>(() => [
  {
    value: 'link',
    label: t('uiPreferences.dropAction.linkLabel'),
    desc: t('uiPreferences.dropAction.linkDesc'),
  },
  {
    value: 'copy',
    label: t('uiPreferences.dropAction.copyLabel'),
    desc: t('uiPreferences.dropAction.copyDesc'),
  },
  {
    value: 'ask',
    label: t('uiPreferences.dropAction.askLabel'),
    desc: t('uiPreferences.dropAction.askDesc'),
  },
])

const promoteOptions = computed<Array<{ value: UiSpatialPromoteMode; label: string; desc: string }>>(() => [
  {
    value: 'merge',
    label: t('uiPreferences.spatialPromote.mergeLabel'),
    desc: t('uiPreferences.spatialPromote.mergeDesc'),
  },
  {
    value: 'split',
    label: t('uiPreferences.spatialPromote.splitLabel'),
    desc: t('uiPreferences.spatialPromote.splitDesc'),
  },
  {
    value: 'parent_children',
    label: t('uiPreferences.spatialPromote.parentChildrenLabel'),
    desc: t('uiPreferences.spatialPromote.parentChildrenDesc'),
  },
])

const motionOptions = computed<Array<{ value: UiReducedMotionPreference; label: string; desc: string }>>(() => [
  {
    value: 'system',
    label: t('uiPreferences.reducedMotion.systemLabel'),
    desc: t('uiPreferences.reducedMotion.systemDesc'),
  },
  {
    value: 'on',
    label: t('uiPreferences.reducedMotion.onLabel'),
    desc: t('uiPreferences.reducedMotion.onDesc'),
  },
  {
    value: 'off',
    label: t('uiPreferences.reducedMotion.offLabel'),
    desc: t('uiPreferences.reducedMotion.offDesc'),
  },
])

async function copyPathFallback(pathText: string): Promise<void> {
  let copied = false
  try {
    if (window.tinadec?.clipboardWriteText) {
      copied = Boolean(await window.tinadec.clipboardWriteText(pathText))
    } else if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(pathText)
      copied = true
    }
  } catch {
    copied = false
  }
  if (copied) {
    notify.info({ message: t('uiPreferences.pathCopiedFallback'), source: 'settings' })
  }
}

async function handleRevealInFolder(): Promise<void> {
  const target = resolvedTomlPath.value
  let revealed = false
  if (filePath.value && window.tinadec?.revealInFolder) {
    try {
      revealed = Boolean(await window.tinadec.revealInFolder(filePath.value))
    } catch {
      revealed = false
    }
  }
  if (!revealed) {
    await copyPathFallback(target)
  }
}

async function handleResetDefaults(): Promise<void> {
  await resetPreferences()
  notify.success({ message: t('uiPreferences.resetSuccess'), source: 'settings' })
}
</script>

<template>
  <div class="interaction-section" data-testid="interaction-section">
    <h2>{{ t('settings.interaction') }}</h2>
    <p class="appearance-lead">{{ t('uiPreferences.interactionLead') }}</p>

    <!-- 1. Workspace Drop Default Action -->
    <section class="appearance-group">
      <div class="appearance-group-head">
        <h3>{{ t('uiPreferences.dropActionTitle') }}</h3>
        <p>{{ t('uiPreferences.dropActionHint') }}</p>
      </div>
      <div class="pref-choice-stack" role="radiogroup" :aria-label="t('uiPreferences.dropActionTitle')">
        <button
          v-for="opt in dropOptions"
          :key="opt.value"
          type="button"
          role="radio"
          :aria-checked="interaction.workspace_drop_action === opt.value"
          class="pref-choice-card"
          :class="{ active: interaction.workspace_drop_action === opt.value }"
          :data-testid="`workspace-drop-action-${opt.value}`"
          @click="updateInteraction({ workspace_drop_action: opt.value })"
        >
          <div class="pref-choice-text">
            <span class="pref-choice-label">{{ opt.label }}</span>
            <span class="pref-choice-desc">{{ opt.desc }}</span>
          </div>
          <Check v-if="interaction.workspace_drop_action === opt.value" :size="15" class="pref-choice-check" />
        </button>
      </div>
    </section>

    <!-- 2. Spatial Promote to Board Default Mode -->
    <section class="appearance-group">
      <div class="appearance-group-head">
        <h3>{{ t('uiPreferences.spatialPromoteTitle') }}</h3>
        <p>{{ t('uiPreferences.spatialPromoteHint') }}</p>
      </div>
      <div class="pref-choice-stack" role="radiogroup" :aria-label="t('uiPreferences.spatialPromoteTitle')">
        <button
          v-for="opt in promoteOptions"
          :key="opt.value"
          type="button"
          role="radio"
          :aria-checked="interaction.spatial_promote_mode === opt.value"
          class="pref-choice-card"
          :class="{ active: interaction.spatial_promote_mode === opt.value }"
          :data-testid="`spatial-promote-mode-${opt.value}`"
          @click="updateInteraction({ spatial_promote_mode: opt.value })"
        >
          <div class="pref-choice-text">
            <span class="pref-choice-label">{{ opt.label }}</span>
            <span class="pref-choice-desc">{{ opt.desc }}</span>
          </div>
          <Check v-if="interaction.spatial_promote_mode === opt.value" :size="15" class="pref-choice-check" />
        </button>
      </div>
    </section>

    <!-- 3. Reduced Motion Preference -->
    <section class="appearance-group">
      <div class="appearance-group-head">
        <h3>{{ t('uiPreferences.reducedMotionTitle') }}</h3>
        <p>{{ t('uiPreferences.reducedMotionHint') }}</p>
      </div>
      <div class="pref-choice-row" role="radiogroup" :aria-label="t('uiPreferences.reducedMotionTitle')">
        <button
          v-for="opt in motionOptions"
          :key="opt.value"
          type="button"
          role="radio"
          :aria-checked="interaction.reduced_motion === opt.value"
          class="pref-choice-card pref-choice-card--compact"
          :class="{ active: interaction.reduced_motion === opt.value }"
          :data-testid="`reduced-motion-${opt.value}`"
          @click="updateInteraction({ reduced_motion: opt.value })"
        >
          <div class="pref-choice-text">
            <span class="pref-choice-label">{{ opt.label }}</span>
            <span class="pref-choice-desc">{{ opt.desc }}</span>
          </div>
          <Check v-if="interaction.reduced_motion === opt.value" :size="14" class="pref-choice-check" />
        </button>
      </div>
    </section>

    <!-- 4. Declarative TOML Configuration File Card -->
    <section class="appearance-group" data-testid="preferences-config-card">
      <div class="appearance-group-head">
        <h3>{{ t('uiPreferences.configCardTitle') }}</h3>
        <p>{{ t('uiPreferences.configCardHint') }}</p>
      </div>
      <div class="pref-toml-box">
        <code class="pref-toml-path" data-testid="preferences-toml-path">{{ resolvedTomlPath }}</code>
        <div class="pref-toml-actions">
          <UiButton
            variant="outline"
            size="sm"
            data-testid="preferences-reveal-btn"
            @click="handleRevealInFolder"
          >
            <FolderOpen :size="14" />
            <span>{{ t('uiPreferences.revealInFolder') }}</span>
          </UiButton>
          <UiButton
            variant="outline"
            size="sm"
            :disabled="saving"
            data-testid="preferences-reset-btn"
            @click="handleResetDefaults"
          >
            <RotateCcw :size="14" />
            <span>{{ t('uiPreferences.resetDefaults') }}</span>
          </UiButton>
        </div>
      </div>
    </section>
  </div>
</template>
