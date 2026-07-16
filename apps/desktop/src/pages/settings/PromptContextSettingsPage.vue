<script setup lang="ts">
import { Bot, FileText, Info, Plus, Save, Server, ShieldCheck, Trash2 } from '@lucide/vue'
import { UiBadge, UiButton, UiCard, UiInput, UiLabel, UiSwitch } from '@/components/ui'
import { usePromptContextSettings } from './promptContextSettings'

const {
  agentModes,
  agents,
  busy,
  clonePromptFragment,
  deletePromptFragment,
  generatePromptPreview,
  loadPromptContextCenter,
  loading,
  newPromptFragment,
  promptCategories,
  promptCategoryLabel,
  promptContextReady,
  promptError,
  promptFilterAgentId,
  promptFilterCategory,
  promptFilterEnabled,
  promptFilterScope,
  promptFilteredFragments,
  promptForm,
  promptFormCategoryDisplay,
  promptPreview,
  promptPreviewAgentId,
  promptPreviewMode,
  promptPreviewRunId,
  promptPreviewSessionId,
  promptPreviewUserContent,
  promptSelectedFragmentId,
  savePromptFragment,
  selectPromptFragment,
  settingsErrorLabel,
  t,
} = usePromptContextSettings()
</script>

<template>
  <div class="model-center-heading">
              <div>
                <h1>{{ t('settings.promptContext') }}</h1>
                <p>{{ t('settings.promptContextSubtitle') }}</p>
              </div>
              <div class="agent-heading-actions">
                <UiButton variant="outline" size="sm" :disabled="loading" @click="loadPromptContextCenter">
                  <Server :size="14" />
                  <span>{{ t('settings.refresh') }}</span>
                </UiButton>
                <UiButton size="sm" :disabled="loading || Boolean(promptError && !promptContextReady)" @click="newPromptFragment">
                  <Plus :size="14" />
                  <span>{{ t('settings.promptNewFragment') }}</span>
                </UiButton>
              </div>
            </div>

            <div v-if="promptError" class="center-message error">
              <Info :size="16" />
              <span>{{ settingsErrorLabel(promptError) }}</span>
              <UiButton variant="outline" size="sm" @click="loadPromptContextCenter">{{ t('settings.retry') }}</UiButton>
            </div>

            <template v-if="!promptError || promptContextReady">

            <div class="model-form-grid">
              <div class="settings-field">
                <UiLabel>{{ t('settings.promptScope') }}</UiLabel>
                <select v-model="promptFilterScope" class="settings-select">
                  <option value="all">{{ t('settings.filterAll') }}</option>
                  <option value="global">{{ t('settings.promptScopeGlobal') }}</option>
                  <option value="agent">{{ t('settings.promptScopeAgent') }}</option>
                  <option value="mode">{{ t('settings.promptScopeMode') }}</option>
                  <option value="session">{{ t('settings.promptScopeSession') }}</option>
                  <option value="project">{{ t('settings.promptScopeProject') }}</option>
                </select>
              </div>
              <div class="settings-field">
                <UiLabel>{{ t('settings.promptCategory') }}</UiLabel>
                <select v-model="promptFilterCategory" class="settings-select">
                  <option value="all">{{ t('settings.filterAll') }}</option>
                  <option v-for="category in promptCategories" :key="category" :value="category">{{ promptCategoryLabel(category) }}</option>
                </select>
              </div>
              <div class="settings-field">
                <UiLabel>{{ t('settings.promptTargetAgent') }}</UiLabel>
                <select v-model="promptFilterAgentId" class="settings-select">
                  <option value="all">{{ t('settings.filterAll') }}</option>
                  <option value="">{{ t('settings.promptGlobalTarget') }}</option>
                  <option v-for="agent in agents" :key="agent.id" :value="agent.id">{{ agent.name }}</option>
                </select>
              </div>
              <div class="settings-field">
                <UiLabel>{{ t('settings.status') }}</UiLabel>
                <select v-model="promptFilterEnabled" class="settings-select">
                  <option value="all">{{ t('settings.filterAll') }}</option>
                  <option value="enabled">{{ t('settings.enabled') }}</option>
                  <option value="disabled">{{ t('settings.statusDisabled') }}</option>
                </select>
              </div>
            </div>

            <div class="model-section-header">
              <h2>{{ t('settings.promptFragments') }}</h2>
              <UiBadge variant="outline">{{ promptFilteredFragments.length }}</UiBadge>
            </div>

            <div class="agent-tool-grid">
              <button
                v-for="fragment in promptFilteredFragments"
                :key="fragment.id"
                class="agent-tool-chip"
                :class="{ active: promptSelectedFragmentId === fragment.id, risky: !fragment.enabled }"
                @click="selectPromptFragment(fragment)"
              >
                <span class="agent-tool-name">{{ fragment.title }}</span>
                <span class="agent-tool-risk">
                  {{ fragment.scope }} / {{ promptCategoryLabel(fragment.category) }} / {{ fragment.priority }}
                  <template v-if="fragment.is_builtin"> / {{ t('settings.promptBuiltin') }}</template>
                </span>
              </button>
            </div>

            <UiCard class="agent-detail-panel">
              <template #content>
                <div class="agent-detail-head">
                  <div class="agent-card-icon">
                    <Bot :size="20" />
                  </div>
                  <div>
                    <h2>{{ promptForm.id ? promptForm.title : t('settings.promptFragmentNewTitle') }}</h2>
                    <p>{{ promptForm.is_builtin ? t('settings.promptFragmentBuiltinHint') : t('settings.promptFragmentCustomHint') }}</p>
                  </div>
                  <UiBadge :variant="promptForm.enabled ? 'default' : 'secondary'">
                    {{ promptForm.enabled ? t('settings.enabled') : t('settings.statusDisabled') }}
                  </UiBadge>
                </div>

                <div class="agent-config-switch">
                  <div>
                    <strong>{{ t('settings.enabled') }}</strong>
                    <span>{{ promptForm.is_builtin ? t('settings.promptBuiltinCustomizeHint') : promptForm.id || t('settings.promptCustomFragment') }}</span>
                  </div>
                  <UiSwitch v-model="promptForm.enabled" :disabled="promptForm.is_builtin" />
                </div>

                <div class="model-form-grid">
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.promptKey') }}</UiLabel>
                    <UiInput v-model="promptForm.key" :disabled="promptForm.is_builtin" />
                  </div>
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.promptTitle') }}</UiLabel>
                    <UiInput v-model="promptForm.title" :disabled="promptForm.is_builtin" />
                  </div>
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.promptScope') }}</UiLabel>
                    <select v-model="promptForm.scope" class="settings-select" :disabled="promptForm.is_builtin">
                      <option value="global">{{ t('settings.promptScopeGlobal') }}</option>
                      <option value="agent">{{ t('settings.promptScopeAgent') }}</option>
                      <option value="mode">{{ t('settings.promptScopeMode') }}</option>
                      <option value="session">{{ t('settings.promptScopeSession') }}</option>
                      <option value="project">{{ t('settings.promptScopeProject') }}</option>
                    </select>
                  </div>
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.promptTarget') }}</UiLabel>
                    <select v-if="promptForm.scope === 'agent'" v-model="promptForm.target_agent_id" class="settings-select" :disabled="promptForm.is_builtin">
                      <option value="">{{ t('settings.promptAnyAgent') }}</option>
                      <option v-for="agent in agents" :key="agent.id" :value="agent.id">{{ agent.name }}</option>
                    </select>
                    <UiInput v-else v-model="promptForm.target_agent_id" :disabled="promptForm.is_builtin" :placeholder="t('settings.promptOptionalTargetPlaceholder')" />
                  </div>
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.promptCategory') }}</UiLabel>
                    <UiInput v-model="promptFormCategoryDisplay" :disabled="promptForm.is_builtin" />
                  </div>
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.promptPriority') }}</UiLabel>
                    <UiInput v-model="promptForm.priority" type="number" :disabled="promptForm.is_builtin" />
                  </div>
                </div>

                <div class="agent-config-section">
                  <div class="agent-config-section-title">{{ t('settings.promptContent') }}</div>
                  <div class="settings-field">
                    <textarea
                      v-model="promptForm.content"
                      class="settings-textarea prompt-editor"
                      rows="7"
                      :disabled="promptForm.is_builtin"
                    ></textarea>
                  </div>
                </div>

                <div class="agent-save-bar">
                  <UiButton v-if="promptForm.is_builtin" :disabled="busy || !promptForm.id" @click="clonePromptFragment()">
                    <Plus :size="14" />
                    <span>{{ t('settings.promptCloneCustom') }}</span>
                  </UiButton>
                  <UiButton v-else :disabled="busy || !promptForm.content.trim()" @click="savePromptFragment">
                    <Save :size="14" />
                    <span>{{ t('settings.save') }}</span>
                  </UiButton>
                  <UiButton v-if="!promptForm.is_builtin && promptForm.id" variant="ghost" :disabled="busy" @click="deletePromptFragment">
                    <Trash2 :size="14" />
                    <span>{{ t('settings.delete') }}</span>
                  </UiButton>
                </div>
              </template>
            </UiCard>

            <UiCard class="agent-detail-panel">
              <template #content>
                <div class="agent-detail-head">
                  <div class="agent-card-icon">
                    <FileText :size="20" />
                  </div>
                  <div>
                    <h2>{{ t('settings.promptPreviewTitle') }}</h2>
                    <p>{{ t('settings.promptPreviewSubtitle') }}</p>
                  </div>
                  <UiBadge v-if="promptPreview" variant="outline">{{ t('settings.promptTokenCount', { count: promptPreview.estimated_tokens }) }}</UiBadge>
                </div>

                <div class="model-form-grid">
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.promptScopeAgent') }}</UiLabel>
                    <select v-model="promptPreviewAgentId" class="settings-select">
                      <option v-for="agent in agents" :key="agent.id" :value="agent.id">{{ agent.name }}</option>
                    </select>
                  </div>
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.promptScopeMode') }}</UiLabel>
                    <select v-model="promptPreviewMode" class="settings-select">
                      <option value="">{{ t('settings.promptAgentDefault') }}</option>
                      <option v-for="mode in agentModes" :key="mode.id" :value="mode.id">{{ mode.display_name }}</option>
                    </select>
                  </div>
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.promptSessionId') }}</UiLabel>
                    <UiInput v-model="promptPreviewSessionId" :placeholder="t('settings.promptOptionalPlaceholder')" />
                  </div>
                  <div class="settings-field">
                    <UiLabel>{{ t('settings.promptRunId') }}</UiLabel>
                    <UiInput v-model="promptPreviewRunId" :placeholder="t('settings.promptOptionalPlaceholder')" />
                  </div>
                </div>

                <div class="agent-config-section">
                  <div class="agent-config-section-title">{{ t('settings.promptUserContent') }}</div>
                  <textarea
                    v-model="promptPreviewUserContent"
                    class="settings-textarea"
                    rows="3"
                    :placeholder="t('settings.promptOptionalPreviewPlaceholder')"
                  ></textarea>
                </div>

                <div class="agent-save-bar">
                  <UiButton :disabled="busy" @click="generatePromptPreview">
                    <FileText :size="14" />
                    <span>{{ t('settings.promptGeneratePreview') }}</span>
                  </UiButton>
                </div>

                <template v-if="promptPreview">
                  <div class="model-capability-row">
                    <span v-for="fragment in promptPreview.fragments" :key="fragment.id">{{ fragment.key }}</span>
                  </div>
                  <div v-if="promptPreview.context_pack_ids.length > 0" class="model-capability-row">
                    <span v-for="contextPackId in promptPreview.context_pack_ids" :key="contextPackId">{{ contextPackId }}</span>
                  </div>
                  <div v-if="promptPreview.warnings.length > 0" class="provider-status-note">
                    <ShieldCheck :size="14" />
                    <span>{{ promptPreview.warnings.join(' ') }}</span>
                  </div>
                  <div class="settings-field">
                    <textarea
                      :value="promptPreview.system_prompt"
                      class="settings-textarea prompt-editor"
                      rows="14"
                      readonly
                    ></textarea>
                  </div>
                </template>
              </template>
            </UiCard>
            </template>
</template>
