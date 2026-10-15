// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import type { AgentDirectoryItemDto } from '@/api'

const h0 = vi.hoisted(() => ({ put: vi.fn(), agents: [] as unknown[] }))
vi.mock('@/api', () => ({
  api: {
    listAgents: async () => h0.agents,
    putAgentRuntimeBinding: h0.put,
    listAgentModeTopologies: async () => [],
  },
}))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify: { error: vi.fn() } }) }))
const { default: ComposerCommandPanel } = await import('./ComposerCommandPanel.vue')
const { useAgentModels } = await import('@/composables/useAgentModels')

const agent = (id: string, slug: string, binding: Record<string, unknown> | null): AgentDirectoryItemDto => ({
  id, slug, display_name: slug, layer: 'execution', role: slug, source_kind: 'pack', source_key: slug, managed: true, writable: true,
  enabled: true, status: 'published', revision: 1, version: 1, configured_strategy: { kind: 'inherit' }, mode_usages: [],
  effective_previews: {}, updated_at: '', model_binding: binding as AgentDirectoryItemDto['model_binding'],
})
const defaultModel = {
  models: ref([
    { provider_instance_id: 'p1', model_id: 'gpt-5', display_name: 'gpt-5', provider_display_name: 'OpenAI', enabled: true },
    { provider_instance_id: 'p2', model_id: 'deepseek-v4', display_name: 'deepseek-v4', provider_display_name: 'DeepSeek', enabled: true },
  ]),
  runtimes: ref([]),
  current: ref({ provider_instance_id: 'p1', model: 'gpt-5' }), name: ref('gpt-5'), loaded: ref(true), busy: ref(false),
  load: async () => {}, choose: vi.fn(), isCurrent: () => false, providerName: () => null,
  reasoningTarget: ref(null), reasoningSupported: ref(false), reasoningEffort: ref(''), setReasoningEffort: async () => {},
}
const host = { canStop: () => false, draft: () => '', setDraft: () => {}, send: () => {}, stopRun: () => {}, newSession: () => {}, navigate: () => {}, routeName: () => null }

function mountPanel() {
  const anchor = document.createElement('div')
  document.body.appendChild(anchor)
  const Host = defineComponent({
    emits: ['clear-model-pin'],
    setup(_, { emit }) {
      const agentModels = useAgentModels({ conversationSlug: ref('meeting'), modeVersionId: ref(null) })
      return () => h(ComposerCommandPanel, {
        open: true, anchor, canAttach: true, permission: 'default', initialPage: 'model', commandHost: host,
        defaultModel: defaultModel as never, agentModels, modelPinned: true, onClearModelPin: () => emit('clear-model-pin'),
      })
    },
  })
  return mount(Host, { attachTo: document.body, global: { plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] } })
}
const click = async (testId: string) => {
  const row = document.querySelector<HTMLElement>(`[data-testid="${testId}"]`)
  expect(row, testId).not.toBeNull()
  row!.click()
  await flushPromises()
}
afterEach(() => { vi.clearAllMocks(); document.body.innerHTML = '' })

describe('composer model pages', () => {
  it("sets the conversation agent's own model on the model page, and lets it win over the session pin", async () => {
    h0.agents = [agent('a-meeting', 'meeting', null), agent('a-search', 'search', null)]
    h0.put.mockResolvedValue({})
    const wrapper = mountPanel()
    await flushPromises()
    await click('command-panel-p2:deepseek-v4')
    expect(h0.put).toHaveBeenCalledWith('a-meeting', { mode: 'fixed', provider_instance_id: 'p2', model: 'deepseek-v4' })
    expect(wrapper.emitted('clear-model-pin')).toHaveLength(1)
    wrapper.unmount()
  })

  it('keeps the session pin when the choice could not be saved', async () => {
    h0.agents = [agent('a-meeting', 'meeting', null)]
    h0.put.mockRejectedValue(new Error('offline'))
    const wrapper = mountPanel()
    await flushPromises()
    await click('command-panel-p2:deepseek-v4')
    expect(h0.put).toHaveBeenCalledOnce()
    expect(wrapper.emitted('clear-model-pin')).toBeUndefined()
    wrapper.unmount()
  })

  it("sets another agent's model one page down, keeping the tool scope its binding carried", async () => {
    h0.agents = [agent('a-meeting', 'meeting', null),
      agent('a-search', 'search', { mode: 'fixed', provider_instance_id: 'p2', model: 'deepseek-v4', tool_scope_override: ['read_file'], revision: 2, updated_at: '' })]
    h0.put.mockResolvedValue({})
    const wrapper = mountPanel()
    await flushPromises()
    await click('command-panel-model-agents')
    const searchRow = document.querySelector('[data-testid="command-panel-agent-search"]')
    expect(searchRow?.textContent).toContain('deepseek-v4')
    expect(document.querySelector('[data-testid="command-panel-agent-meeting"]')).toBeNull()
    await click('command-panel-agent-search')
    await click('command-panel-follow-default')
    expect(h0.put).toHaveBeenCalledWith('a-search', { mode: 'inherit', provider_instance_id: null, model: null, tool_scope: ['read_file'] })
    wrapper.unmount()
  })
})
