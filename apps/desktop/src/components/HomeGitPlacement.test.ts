// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { ref } from 'vue'
import { describe, expect, it, vi } from 'vitest'
import { FEATURE_CATALOG, catalogEntriesForPage } from '../../../TinadecUI/src/components/cards/home/featureCatalog'
const dispatch = vi.fn()
vi.mock('../../../TinadecUI/src/components/useUie', () => ({ useUie: () => ({ dispatch, scope: ref({ kind: 'page', pageId: 'home' }), snapshot: ref({ revision: 1 }) }) }))
vi.mock('@/composables/useElementSize', () => ({ useResponsiveMode: () => ({ isCompact: ref(false) }) }))
vi.mock('@/controllers/HomeController', () => ({ homeController: {
  currentProject: ref(null),
  currentSession: ref(null),
  approvals: ref([]),
  readiness: ref({ status: 'ready', items: [] }),
  recentEvents: ref([]),
  working: ref(false),
  activeRuns: ref([]),
  orchestration: ref(null),
  workspaceRoots: ref([]),
} }))
vi.mock('@/composables/useConnection', () => ({ useConnection: () => ({ hostStatus: ref({ state: 'preview' }), businessReady: ref(false) }) }))
vi.mock('@/composables/useHomeGitStatus', () => ({ useHomeGitStatus: () => ({
  summary: ref(null), updatedAt: ref(null), error: ref(null), loading: ref(false), busy: ref(false),
  recentCommit: ref(null), historyLoaded: ref(false), historyError: ref(false),
  businessReady: ref(false), refresh: vi.fn(),
}) }))
vi.mock('vue-i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key, te: () => true }),
}))
import HomePickerCard from '../../../TinadecUI/src/components/cards/home/HomePickerCard.vue'

describe('Home feature panel Bento & Git widget placement', () => {
  it('opens Git from the real widget through the Home card, not just a stub', async () => {
    dispatch.mockClear()
    const wrapper = mount(HomePickerCard, { global: { stubs: {
      UiIslandCard: { template: '<article><slot /></article>' },
    } } })
    await wrapper.find('.git-widget-open').trigger('click')
    expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({ command: expect.objectContaining({ descriptorId: 'git' }) }))
  })

  it('renders 3-tier Bento deck, grouped launchers without Git/system tiles, and bottom status pill', async () => {
    dispatch.mockClear()
    const wrapper = mount(HomePickerCard, { global: { stubs: {
      UiIslandCard: { template: '<article><slot /></article>' },
      GitStatusWidget: { template: '<button class="git-status-widget" @click="$emit(\'open\')" />', emits: ['open', 'open-file', 'open-tab'] },
    } } })
    expect(FEATURE_CATALOG.find(entry => entry.descriptorId === 'git')).toBeTruthy()
    const nonGitLauncherCount = catalogEntriesForPage('home')
      .filter((entry) => entry.descriptorId !== 'git' && entry.group !== 'system')
      .length
    expect(wrapper.findAll('.panel-home-card')).toHaveLength(nonGitLauncherCount)
    expect(wrapper.find('[data-testid="session-run-widget"]').exists()).toBe(true)
    expect(wrapper.find('.git-status-widget').exists()).toBe(true)
    expect(wrapper.find('[data-testid="workspace-quick-widget"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="home-attention-approvals"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="home-status-pill"]').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('context.homeGit')

    await wrapper.find('.git-status-widget').trigger('click')
    expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({ command: expect.objectContaining({ type: 'openCard', descriptorId: 'git' }) }))

    await wrapper.get('[data-testid="home-status-events-btn"]').trigger('click')
    expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({ command: expect.objectContaining({ type: 'openCard', descriptorId: 'doctor' }) }))
  })
})
