<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, FileText, Plus, Save, Server, ShieldCheck, Trash2 } from '@lucide/vue'
import { UiBadge, UiButton, UiCard, UiInput, UiLabel, UiSwitch } from '@/components/ui'
import {
  api,
  type AgentDefinitionDto,
  type AgentProfileDto,
  type AgentModeDto,
  type PromptContextPreviewDto,
  type PromptFragmentDto,
  type SavePromptFragmentInput,
} from '@/api'
import { useNotifications } from '@/composables/useNotifications'

/**
 * 提示词上下文 section（D7.3 从 SettingsPage 死代码状态恢复为自包含面板）。
 *
 * Legacy prompt-fragment library: list / filter / CRUD + local preview of the
 * final assembled system prompt. Experimental by product definition
 * (docs/app-core-ui.md §4.8); the canonical DmaEA prompt config lives in the
 * PromptPipeline canvas under 提示词工程.
 */
const { t } = useI18n()
const { items: notificationItems, notify, confirm, dismiss: dismissNotification, status, dismissByKey } =
  useNotifications()

const loading = ref(false)
const busy = ref(false)
const fragments = ref<PromptFragmentDto[]>([])
const promptPreview = ref<PromptContextPreviewDto | null>(null)
const agents = ref<Array<{ id: string; name: string }>>([])
const agentModes = ref<AgentModeDto[]>([])
const promptSelectedFragmentId = ref('')
const promptFilterScope = ref('all')
const promptFilterCategory = ref('all')
const promptFilterAgentId = ref('all')
const promptFilterEnabled = ref('all')
const promptPreviewAgentId = ref('agent_meeting')
const promptPreviewMode = ref('')
const promptPreviewSessionId = ref('')
const promptPreviewRunId = ref('')
const promptPreviewUserContent = ref('')
const promptForm = reactive({
  id: '',
  key: '',
  title: '',
  scope: 'agent',
  target_agent_id: 'agent_meeting',
  category: 'custom',
  content: '',
  priority: '500',
  enabled: true,
  is_builtin: false,
})

const promptCategories = computed(() =>
  Array.from(new Set(fragments.value.map((fragment) => fragment.category))).sort(),
)
const promptFilteredFragments = computed(() => fragments.value.filter((fragment) => {
  if (promptFilterScope.value !== 'all' && fragment.scope !== promptFilterScope.value) return false
  if (promptFilterCategory.value !== 'all' && fragment.category !== promptFilterCategory.value) return false
  if (promptFilterAgentId.value !== 'all' && (fragment.target_agent_id ?? '') !== promptFilterAgentId.value) return false
  if (promptFilterEnabled.value === 'enabled' && !fragment.enabled) return false
  if (promptFilterEnabled.value === 'disabled' && fragment.enabled) return false
  return true
}))

function clearGatewayRestartBanner(): void {
  const existing = notificationItems.value.find((item) => item.key === 'gateway-restart')
  if (existing) dismissNotification(existing.id)
}

async function loadPromptContextCenter(): Promise<void> {
  loading.value = true
  try {
    const [list, agentRows] = await Promise.all([
      api.listPromptFragments(),
      api.listAgents().catch(() => [] as AgentProfileDto[]),
    ])
    fragments.value = list
    agents.value = agentRows.map((a) => ({ id: a.id, name: (a as { display_name?: string; slug?: string; name?: string }).display_name ?? (a as { slug?: string }).slug ?? a.name ?? a.id }))
    if (!promptSelectedFragmentId.value && list.length > 0) {
      selectPromptFragment(list[0])
    } else if (promptSelectedFragmentId.value) {
      const selected = list.find((fragment) => fragment.id === promptSelectedFragmentId.value)
      if (selected) selectPromptFragment(selected)
    }
    if (!agentModes.value.length) {
      await api.listAgentModes()
        .then((modes) => { agentModes.value = modes })
        .catch(() => { /* offline degrade */ })
    }
    if (!promptPreviewMode.value) {
      promptPreviewMode.value = agentModes.value.find((mode) => mode.id === 'plan-first')?.id ?? agentModes.value[0]?.id ?? 'plan-first'
    }
  } catch (error) {
    status.error({ key: 'prompt-context', source: 'prompts', message: error instanceof Error ? error.message : t('settings.centerLoadFailed') })
  } finally {
    loading.value = false
  }
}

function selectPromptFragment(fragment: PromptFragmentDto): void {
  promptSelectedFragmentId.value = fragment.id
  promptForm.id = fragment.id
  promptForm.key = fragment.key
  promptForm.title = fragment.title
  promptForm.scope = fragment.scope
  promptForm.target_agent_id = fragment.target_agent_id ?? ''
  promptForm.category = fragment.category
  promptForm.content = fragment.content
  promptForm.priority = String(fragment.priority)
  promptForm.enabled = fragment.enabled
  promptForm.is_builtin = fragment.is_builtin
}

function newPromptFragment(): void {
  promptSelectedFragmentId.value = ''
  promptForm.id = ''
  promptForm.key = `custom.meeting.${Date.now()}`
  promptForm.title = 'Custom Meeting Context'
  promptForm.scope = 'agent'
  promptForm.target_agent_id = 'agent_meeting'
  promptForm.category = 'custom'
  promptForm.content = ''
  promptForm.priority = '500'
  promptForm.enabled = true
  promptForm.is_builtin = false
}

function promptPayload(): SavePromptFragmentInput {
  return {
    key: promptForm.key,
    title: promptForm.title,
    scope: promptForm.scope,
    target_agent_id: promptForm.target_agent_id || null,
    category: promptForm.category,
    content: promptForm.content,
    priority: Number(promptForm.priority) || 0,
    enabled: promptForm.enabled,
  }
}

async function savePromptFragment(): Promise<void> {
  busy.value = true
  try {
    const saved = promptForm.id
      ? await api.savePromptFragment(promptForm.id, promptPayload())
      : await api.createPromptFragment(promptPayload())
    promptSelectedFragmentId.value = saved.id
    await loadPromptContextCenter()
    notify.success(saved.title)
  } catch (error) {
    notify.error(error, { title: promptForm.title })
  } finally {
    busy.value = false
  }
}

async function deletePromptFragment(): Promise<void> {
  if (!promptForm.id || promptForm.is_builtin) return
  const fragmentId = promptForm.id
  const fragmentTitle = promptForm.title
  if (!await confirm({
    title: t('settings.delete'),
    message: `${t('settings.confirmDelete')} ${promptForm.title}?`,
    confirmLabel: t('settings.confirmDelete'),
    cancelLabel: t('settings.cancel'),
    destructive: true,
  })) return
  busy.value = true
  try {
    await api.deletePromptFragment(fragmentId)
    promptSelectedFragmentId.value = ''
    await loadPromptContextCenter()
    notify.success(`${fragmentTitle}: ${t('settings.delete')}`)
  } catch (error) {
    notify.error(error, { title: fragmentTitle })
  } finally {
    busy.value = false
  }
}

async function clonePromptFragment(fragmentId = promptForm.id): Promise<void> {
  if (!fragmentId) return
  busy.value = true
  try {
    const cloned = await api.clonePromptFragment(fragmentId)
    promptSelectedFragmentId.value = cloned.id
    await loadPromptContextCenter()
    notify.success(cloned.title)
  } catch (error) {
    notify.error(error)
  } finally {
    busy.value = false
  }
}

async function generatePromptPreview(): Promise<void> {
  busy.value = true
  try {
    promptPreview.value = await api.previewPromptContext({
      agent_id: promptPreviewAgentId.value || 'agent_meeting',
      mode: promptPreviewMode.value || null,
      session_id: promptPreviewSessionId.value || null,
      run_id: promptPreviewRunId.value || null,
      user_content: promptPreviewUserContent.value || null,
    })
  } catch (error) {
    notify.error(error, { title: t('settings.preview') })
  } finally {
    busy.value = false
  }
}

onMounted(() => {
  void loadPromptContextCenter()
})

// Template parity helpers kept from the legacy inline block.
void clearGatewayRestartBanner
</script>
<template>

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
                <UiBadge v-if="promptPreview" variant="outline">{{ promptPreview!.estimated_tokens }} tokens</UiBadge>
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
                  <span v-for="fragment in promptPreview!.fragments" :key="fragment.id">{{ fragment.key }}</span>
                </div>
                <div v-if="promptPreview!.context_pack_ids.length > 0" class="model-capability-row">
                  <span v-for="contextPackId in promptPreview!.context_pack_ids" :key="contextPackId">{{ contextPackId }}</span>
                </div>
                <div v-if="promptPreview!.warnings.length > 0" class="provider-status-note">
                  <ShieldCheck :size="14" />
                  <span>{{ promptPreview!.warnings.join(' ') }}</span>
                </div>
                <div class="settings-field">
                  <textarea
                    :value="promptPreview!.system_prompt"
                    class="settings-textarea prompt-editor"
                    rows="14"
                    readonly
                  ></textarea>
                </div>
              </template>
            </template>
          </UiCard>
</template>
