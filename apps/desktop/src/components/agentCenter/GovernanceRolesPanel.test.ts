// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'

const mocks = vi.hoisted(() => ({
  listAgentDefinitions: vi.fn(),
  getWorkspaceDefaults: vi.fn(),
}))

vi.mock('@/api', () => ({ api: mocks }))
vi.mock('@/lib/storageScope', () => ({ scopedApi: (api: unknown) => api }))

const { default: GovernanceRolesPanel } = await import('./GovernanceRolesPanel.vue')

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })

describe('GovernanceRolesPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.getWorkspaceDefaults.mockResolvedValue({ status: 'published', revision: 3 })
    mocks.listAgentDefinitions.mockResolvedValue([])
  })

  it('uses preloaded agents prop without duplicate listAgentDefinitions fetch, renders effective count and unavailable tool hint', async () => {
    const wrapper = mount(GovernanceRolesPanel, {
      props: {
        manifestToolIds: ['git_status'],
        agents: [
          {
            id: 'a-steward',
            slug: 'git_steward',
            display_name: 'Git Steward',
            layer: 'operation',
            status: 'published',
            allowed_tools: [],
            model_strategy: { kind: 'inherit' },
          },
          {
            id: 'a-git',
            slug: 'worker.git',
            display_name: 'Git Worker',
            layer: 'execution',
            status: 'published',
            allowed_tools: ['git_status', 'git_push'],
            model_strategy: { kind: 'inherit' },
          },
        ] as never[],
      },
      global: { plugins: [i18n] },
    })
    await flushPromises()

    expect(mocks.listAgentDefinitions).not.toHaveBeenCalled()
    expect(wrapper.findAll('[data-testid="gov-role-card"]')).toHaveLength(2)
    expect(wrapper.find('[data-testid="steward-no-tools"]').exists()).toBe(true)

    const unavailableChip = wrapper.get('li.tool-unavailable')
    expect(unavailableChip.text()).toContain('git_push')
    expect(unavailableChip.text()).not.toContain('providerUnavailable')
    // Verify count argument is interpolated (1 available out of 2 declared)
    expect(wrapper.text()).toContain('Effective tools (∩ manifest) · 1')
    wrapper.unmount()
  })

  it('surfaces real loadError instead of silently swallowing listAgentDefinitions failure', async () => {
    mocks.listAgentDefinitions.mockRejectedValueOnce(new Error('backend unreachable'))
    const wrapper = mount(GovernanceRolesPanel, {
      props: { manifestToolIds: [] },
      global: { plugins: [i18n] },
    })
    await flushPromises()

    expect(mocks.listAgentDefinitions).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[role="alert"]').text()).toContain('backend unreachable')
    wrapper.unmount()
  })
})
