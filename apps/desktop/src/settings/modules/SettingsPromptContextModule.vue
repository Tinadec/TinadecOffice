<script lang="ts">
import { defineSettingsModule } from '../defineSettingsModule'

export default defineSettingsModule('SettingsPromptContextModule')
</script>

<template>
<div class="settings-module" data-settings-module="promptContext">
          <div class="model-center-heading">
            <div>
              <h2>Prompt Context</h2>
              <p>Meeting Agent prompt fragments and preview</p>
            </div>
            <div class="agent-heading-actions">
              <UiButton variant="outline" size="sm" :disabled="loading" @click="loadPromptContextCenter">
                <Server :size="14" />
                <span>{{ t('settings.refresh') }}</span>
              </UiButton>
              <UiButton size="sm" @click="newPromptFragment">
                <Plus :size="14" />
                <span>New Fragment</span>
              </UiButton>
            </div>
          </div>

          <div class="model-form-grid">
            <div class="settings-field">
              <UiLabel>Scope</UiLabel>
              <select v-model="promptFilterScope" class="settings-select">
                <option value="all">All</option>
                <option value="global">Global</option>
                <option value="agent">Agent</option>
                <option value="mode">Mode</option>
                <option value="session">Session</option>
                <option value="project">Project</option>
              </select>
            </div>
            <div class="settings-field">
              <UiLabel>Category</UiLabel>
              <select v-model="promptFilterCategory" class="settings-select">
                <option value="all">All</option>
                <option v-for="category in promptCategories" :key="category" :value="category">{{ category }}</option>
              </select>
            </div>
            <div class="settings-field">
              <UiLabel>Target Agent</UiLabel>
              <select v-model="promptFilterAgentId" class="settings-select">
                <option value="all">All</option>
                <option value="">Global target</option>
                <option v-for="agent in agents" :key="agent.id" :value="agent.id">{{ agent.name }}</option>
              </select>
            </div>
            <div class="settings-field">
              <UiLabel>Status</UiLabel>
              <select v-model="promptFilterEnabled" class="settings-select">
                <option value="all">All</option>
                <option value="enabled">Enabled</option>
                <option value="disabled">Disabled</option>
              </select>
            </div>
          </div>

          <div class="model-section-header">
            <h3>Fragments</h3>
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
                {{ fragment.scope }} / {{ fragment.category }} / {{ fragment.priority }}
                <template v-if="fragment.is_builtin"> / built-in</template>
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
                  <h3>{{ promptForm.id ? promptForm.title : 'New Prompt Fragment' }}</h3>
                  <p>{{ promptForm.is_builtin ? 'Built-in read-only fragment' : 'Custom editable fragment' }}</p>
                </div>
                <UiBadge :variant="promptForm.enabled ? 'default' : 'secondary'">
                  {{ promptForm.enabled ? 'enabled' : 'disabled' }}
                </UiBadge>
              </div>

              <div class="agent-config-switch">
                <div>
                  <strong>Enabled</strong>
                  <span>{{ promptForm.is_builtin ? 'Clone to customize built-in content' : promptForm.id || 'custom fragment' }}</span>
                </div>
                <UiSwitch v-model="promptForm.enabled" :disabled="promptForm.is_builtin" />
              </div>

              <div class="model-form-grid">
                <div class="settings-field">
                  <UiLabel>Key</UiLabel>
                  <UiInput v-model="promptForm.key" :disabled="promptForm.is_builtin" />
                </div>
                <div class="settings-field">
                  <UiLabel>Title</UiLabel>
                  <UiInput v-model="promptForm.title" :disabled="promptForm.is_builtin" />
                </div>
                <div class="settings-field">
                  <UiLabel>Scope</UiLabel>
                  <select v-model="promptForm.scope" class="settings-select" :disabled="promptForm.is_builtin">
                    <option value="global">Global</option>
                    <option value="agent">Agent</option>
                    <option value="mode">Mode</option>
                    <option value="session">Session</option>
                    <option value="project">Project</option>
                  </select>
                </div>
                <div class="settings-field">
                  <UiLabel>Target</UiLabel>
                  <select v-if="promptForm.scope === 'agent'" v-model="promptForm.target_agent_id" class="settings-select" :disabled="promptForm.is_builtin">
                    <option value="">Any agent</option>
                    <option v-for="agent in agents" :key="agent.id" :value="agent.id">{{ agent.name }}</option>
                  </select>
                  <UiInput v-else v-model="promptForm.target_agent_id" :disabled="promptForm.is_builtin" placeholder="optional target id" />
                </div>
                <div class="settings-field">
                  <UiLabel>Category</UiLabel>
                  <UiInput v-model="promptForm.category" :disabled="promptForm.is_builtin" />
                </div>
                <div class="settings-field">
                  <UiLabel>Priority</UiLabel>
                  <UiInput v-model="promptForm.priority" type="number" :disabled="promptForm.is_builtin" />
                </div>
              </div>

              <div class="agent-config-section">
                <div class="agent-config-section-title">Content</div>
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
                  <span>Clone Custom</span>
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
                  <h3>Preview</h3>
                  <p>Final local system prompt</p>
                </div>
                <UiBadge v-if="promptPreview" variant="outline">{{ promptPreview.estimated_tokens }} tokens</UiBadge>
              </div>

              <div class="model-form-grid">
                <div class="settings-field">
                  <UiLabel>Agent</UiLabel>
                  <select v-model="promptPreviewAgentId" class="settings-select">
                    <option v-for="agent in agents" :key="agent.id" :value="agent.id">{{ agent.name }}</option>
                  </select>
                </div>
                <div class="settings-field">
                  <UiLabel>Mode</UiLabel>
                  <select v-model="promptPreviewMode" class="settings-select">
                    <option value="">Agent default</option>
                    <option v-for="mode in agentModes" :key="mode.id" :value="mode.id">{{ mode.display_name }}</option>
                  </select>
                </div>
                <div class="settings-field">
                  <UiLabel>Session ID</UiLabel>
                  <UiInput v-model="promptPreviewSessionId" placeholder="optional" />
                </div>
                <div class="settings-field">
                  <UiLabel>Run ID</UiLabel>
                  <UiInput v-model="promptPreviewRunId" placeholder="optional" />
                </div>
              </div>

              <div class="agent-config-section">
                <div class="agent-config-section-title">User content</div>
                <textarea
                  v-model="promptPreviewUserContent"
                  class="settings-textarea"
                  rows="3"
                  placeholder="optional preview text"
                ></textarea>
              </div>

              <div class="agent-save-bar">
                <UiButton :disabled="busy" @click="generatePromptPreview">
                  <FileText :size="14" />
                  <span>Generate Preview</span>
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
        </div>
</template>
