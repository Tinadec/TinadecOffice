import { computed, reactive, ref } from 'vue'
import { api, type PromptContextPreviewDto, type PromptFragmentDto, type SavePromptFragmentInput } from '@/api'
import { useAgentState } from './agentState'
import { useSettingsLabels } from './settingsLabels'

const promptFragments = ref<PromptFragmentDto[]>([])
const promptPreview = ref<PromptContextPreviewDto | null>(null)
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
const loading = ref(false)
const busy = ref(false)
const promptError = ref('')
const promptContextReady = ref(false)
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

function selectPromptFragment(fragment: PromptFragmentDto) {
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

function resetPromptFragmentForm(title: string) {
  promptSelectedFragmentId.value = ''
  promptForm.id = ''
  promptForm.key = `custom.meeting.${Date.now()}`
  promptForm.title = title
  promptForm.scope = 'agent'
  promptForm.target_agent_id = 'agent_meeting'
  promptForm.category = 'custom'
  promptForm.content = ''
  promptForm.priority = '500'
  promptForm.enabled = true
  promptForm.is_builtin = false
}

function promptPayload(): SavePromptFragmentInput {
  return { key: promptForm.key, title: promptForm.title, scope: promptForm.scope, target_agent_id: promptForm.target_agent_id || null, category: promptForm.category, content: promptForm.content, priority: Number(promptForm.priority) || 0, enabled: promptForm.enabled }
}

export function usePromptContextSettings() {
  const { settingsErrorLabel, t } = useSettingsLabels()
  const { agentModes, agents, loadAgentCenter } = useAgentState()
  const promptCategoryLabel = (category: string) => category === 'custom' ? t('settings.promptCustomCategory') : category
  const promptFormCategoryDisplay = computed({
    get: () => promptCategoryLabel(promptForm.category),
    set: (category: string) => { promptForm.category = category === t('settings.promptCustomCategory') ? 'custom' : category },
  })
  const promptCategories = computed(() => Array.from(new Set(promptFragments.value.map((fragment) => fragment.category))).sort())
  const promptFilteredFragments = computed(() => promptFragments.value.filter((fragment) => {
    if (promptFilterScope.value !== 'all' && fragment.scope !== promptFilterScope.value) return false
    if (promptFilterCategory.value !== 'all' && fragment.category !== promptFilterCategory.value) return false
    if (promptFilterAgentId.value !== 'all' && (fragment.target_agent_id ?? '') !== promptFilterAgentId.value) return false
    if (promptFilterEnabled.value === 'enabled' && !fragment.enabled) return false
    if (promptFilterEnabled.value === 'disabled' && fragment.enabled) return false
    return true
  }))

  async function loadPromptContextCenter() {
    loading.value = true
    promptError.value = ''
    try {
      await loadAgentCenter(t('settings.centerLoadFailed'))
      const fragments = await api.listPromptFragments()
      promptFragments.value = fragments
      promptContextReady.value = true
      const selected = fragments.find((fragment) => fragment.id === promptSelectedFragmentId.value) ?? fragments[0]
      if (selected) selectPromptFragment(selected)
      if (!promptPreviewMode.value) promptPreviewMode.value = agentModes.value.find((mode) => mode.id === 'plan-first')?.id ?? agentModes.value[0]?.id ?? 'plan-first'
    } catch (error) {
      promptError.value = error instanceof Error ? error.message : String(error)
    } finally {
      loading.value = false
    }
  }

  async function savePromptFragment() {
    busy.value = true
    promptError.value = ''
    try {
      const saved = promptForm.id ? await api.savePromptFragment(promptForm.id, promptPayload()) : await api.createPromptFragment(promptPayload())
      promptSelectedFragmentId.value = saved.id
      await loadPromptContextCenter()
    } catch (error) {
      promptError.value = error instanceof Error ? error.message : String(error)
    } finally {
      busy.value = false
    }
  }

  async function deletePromptFragment() {
    if (!promptForm.id || promptForm.is_builtin) return
    busy.value = true
    promptError.value = ''
    try {
      await api.deletePromptFragment(promptForm.id)
      promptSelectedFragmentId.value = ''
      await loadPromptContextCenter()
    } catch (error) {
      promptError.value = error instanceof Error ? error.message : String(error)
    } finally {
      busy.value = false
    }
  }

  async function clonePromptFragment(fragmentId = promptForm.id) {
    if (!fragmentId) return
    busy.value = true
    promptError.value = ''
    try {
      const cloned = await api.clonePromptFragment(fragmentId)
      promptSelectedFragmentId.value = cloned.id
      await loadPromptContextCenter()
    } catch (error) {
      promptError.value = error instanceof Error ? error.message : String(error)
    } finally {
      busy.value = false
    }
  }

  async function generatePromptPreview() {
    busy.value = true
    promptError.value = ''
    try {
      promptPreview.value = await api.previewPromptContext({ agent_id: promptPreviewAgentId.value || 'agent_meeting', mode: promptPreviewMode.value || null, session_id: promptPreviewSessionId.value || null, run_id: promptPreviewRunId.value || null, user_content: promptPreviewUserContent.value || null })
    } catch (error) {
      promptError.value = error instanceof Error ? error.message : String(error)
    } finally {
      busy.value = false
    }
  }

  if (promptFragments.value.length === 0 && !loading.value) void loadPromptContextCenter()

  return { agentModes, agents, busy, clonePromptFragment, deletePromptFragment, generatePromptPreview, loadPromptContextCenter, loading, newPromptFragment: () => resetPromptFragmentForm(t('settings.promptCustomMeetingContext')), promptCategories, promptCategoryLabel, promptContextReady, promptError, promptFilterAgentId, promptFilterCategory, promptFilterEnabled, promptFilterScope, promptFilteredFragments, promptForm, promptFormCategoryDisplay, promptPreview, promptPreviewAgentId, promptPreviewMode, promptPreviewRunId, promptPreviewSessionId, promptPreviewUserContent, promptSelectedFragmentId, savePromptFragment, selectPromptFragment, settingsErrorLabel, t }
}
