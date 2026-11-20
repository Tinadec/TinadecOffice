// @vitest-environment happy-dom
/**
 * Two things this panel got wrong and must not regress:
 * 1. `agentCenter.effectiveTools` was called without its `{count}`, so the heading read
 *    "Effective tools (∩ manifest) · {count}" with a literal brace.
 * 2. A parent that had already loaded the agent directory still triggered a second
 *    `listAgentDefinitions()` — the duplicate 1+N the `agents` prop exists to prevent.
 * 3. A legacy `planning` layer proposal had to be treated as the governance seat.
 */
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import en from '@/locales/en'

const mocks = vi.hoisted(() => ({
  listAgentDefinitions: vi.fn(),
  getWorkspaceDefaults: vi.fn(),
}))

vi.mock('@/api', () => ({ api: mocks }))
vi.mock('@/lib/storageScope', () => ({ scopedApi: (api: unknown) => api }))
vi.mock('@/lib/hostAccess', async () => {
  const { ref } = await import('vue')
  return { useHostAccess: () => ({ canAccessBackend: ref(true) }) }
})

const { default: GovernanceRolesPanel } = await import('./GovernanceRolesPanel.vue')

const global = { plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] }

const steward = (over: Record<string, unknown> = {}) => ({
  id: 'a-steward',
  slug: 'git_steward',
  name: 'git_steward',
  display_name: 'Git steward',
  layer: 'operation',
  status: 'published',
  allowed_tools: [],
  model_strategy: { kind: 'fixed' },
  ...over,
})

const worker = (over: Record<string, unknown> = {}) => ({
  id: 'a-worker',
  slug: 'worker.git',
  name: 'worker.git',
  display_name: 'Git worker',
  layer: 'execution',
  status: 'published',
  allowed_tools: ['git_status', 'git_diff'],
  model_strategy: 'inherit',
  ...over,
})

beforeEach(() => {
  setActivePinia(createPinia())
  vi.clearAllMocks()
  mocks.getWorkspaceDefaults.mockResolvedValue({ status: 'published', revision: 3 })
  mocks.listAgentDefinitions.mockResolvedValue([steward(), worker()])
})

describe('GovernanceRolesPanel — agent prop', () => {
  it('does not refetch definitions when the parent already passed agents', async () => {
    const wrapper = mount(GovernanceRolesPanel, {
      props: { manifestToolIds: ['git_status', 'git_diff'], agents: [steward(), worker()] },
      global,
    })
    await flushPromises()

    expect(mocks.listAgentDefinitions).not.toHaveBeenCalled()
    expect(wrapper.findAll('[data-testid="gov-role-card"]')).toHaveLength(2)
    wrapper.unmount()
  })

  it('treats an empty agents array as "already loaded", not "please fetch"', async () => {
    const wrapper = mount(GovernanceRolesPanel, {
      props: { manifestToolIds: [], agents: [] },
      global,
    })
    await flushPromises()

    // `[]` means the parent asked and got nothing; refetching is the duplicate 1+N.
    expect(mocks.listAgentDefinitions).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="gov-roles-empty"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('fetches once when no agents prop is given at all', async () => {
    const wrapper = mount(GovernanceRolesPanel, { props: { manifestToolIds: [] }, global })
    await flushPromises()

    expect(mocks.listAgentDefinitions).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('[data-testid="gov-role-card"]')).toHaveLength(2)
    wrapper.unmount()
  })
})

describe('GovernanceRolesPanel — effective tools count', () => {
  it('interpolates the real count instead of printing a literal brace', async () => {
    const wrapper = mount(GovernanceRolesPanel, {
      props: { manifestToolIds: ['git_status', 'git_diff'], agents: [steward(), worker()] },
      global,
    })
    await flushPromises()

    const headings = wrapper.findAll('.gov-role-card__tools h4').map((h) => h.text())
    expect(headings).toHaveLength(2)
    // The steward holds no tools; the worker holds two, both present in the manifest.
    expect(headings[0]).toContain('0')
    expect(headings[1]).toContain('2')
    for (const heading of headings) {
      expect(heading).not.toContain('{count}')
    }
    wrapper.unmount()
  })
})

describe('GovernanceRolesPanel — legacy planning layer', () => {
  it('treats a legacy `planning` layer steward as the governance seat', async () => {
    const wrapper = mount(GovernanceRolesPanel, {
      props: {
        manifestToolIds: [],
        agents: [steward({ layer: 'planning' }), worker({ layer: 'planning' })],
      },
      global,
    })
    await flushPromises()

    const cards = wrapper.findAll('[data-testid="gov-role-card"]')
    expect(cards).toHaveLength(2)
    // `planning` and `operation` name the same seat, so the no-tools note must still appear
    // for the steward rather than the card silently losing its explanation.
    expect(wrapper.find('[data-testid="steward-no-tools"]').exists()).toBe(true)
    wrapper.unmount()
  })
})

describe('GovernanceRolesPanel — empty state', () => {
  it('lists the governance-layer agents Core actually has', async () => {
    const wrapper = mount(GovernanceRolesPanel, {
      props: {
        manifestToolIds: [],
        agents: [steward({ id: 'a-other', slug: 'architect', name: 'architect', display_name: 'Architect' })],
      },
      global,
    })
    await flushPromises()

    const empty = wrapper.get('[data-testid="gov-roles-empty"]')
    // Both expected role names are named as missing.
    expect(empty.text()).toContain('git_steward')
    expect(empty.text()).toContain('worker.git')
    // And the seats that DO exist are shown, so the state is not a dead end.
    expect(wrapper.get('[data-testid="gov-roles-present"]').text()).toContain('Architect')
    wrapper.unmount()
  })
})
