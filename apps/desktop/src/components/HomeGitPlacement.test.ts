// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { ref } from 'vue'
import { describe, expect, it, vi } from 'vitest'
import { FEATURE_CATALOG } from '../../../TinadecUI/src/components/cards/home/featureCatalog'
const dispatch = vi.fn()
vi.mock('../../../TinadecUI/src/components/useUie', () => ({ useUie: () => ({ dispatch, scope: ref({ kind: 'page', pageId: 'home' }), snapshot: ref({ revision: 1 }) }) }))
vi.mock('@/composables/useElementSize', () => ({ useResponsiveMode: () => ({ isCompact: ref(false) }) }))
vi.mock('@/controllers/HomeController', () => ({ homeController: {
  currentProject: ref(null), currentSession: ref(null), approvals: ref([]),
} }))
vi.mock('@/composables/useConnection', () => ({ useConnection: () => ({ hostStatus: ref({ state: 'preview' }), businessReady: ref(false) }) }))
vi.mock('@/composables/useHomeGitStatus', () => ({ useHomeGitStatus: () => ({
  summary: ref(null), updatedAt: ref(null), error: ref(null), loading: ref(false), busy: ref(false),
  recentCommit: ref(null), historyLoaded: ref(false), historyError: ref(false),
  businessReady: ref(false), refresh: vi.fn(),
}) }))
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }))
import HomePickerCard from '../../../TinadecUI/src/components/cards/home/HomePickerCard.vue'

describe('Home feature panel Git widget placement', () => {
  it('opens Git from the real widget through the Home card, not just a stub', async () => {
    dispatch.mockClear()
    const wrapper = mount(HomePickerCard, { global: { stubs: {
      UiIslandCard: { template: '<article><slot /></article>' },
    } } })
    await wrapper.find('.git-widget-open').trigger('click')
    expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({ command: expect.objectContaining({ descriptorId: 'git' }) }))
  })

  it('replaces only the Home grid Git entry; keeps the menu catalog and opens singleton Git card', async () => {
    const wrapper = mount(HomePickerCard, { global: { stubs: {
      GitStatusWidget: { template: '<button class="git-status-widget" @click="$emit(\'open\')" />', emits: ['open'] },
    } } })
    expect(FEATURE_CATALOG.find(entry => entry.descriptorId === 'git')).toBeTruthy()
    // Every catalog page except Git, which the status widget replaces on the grid.
    expect(wrapper.findAll('.panel-home-card')).toHaveLength(FEATURE_CATALOG.length - 1)
    expect(wrapper.find('.git-status-widget').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('context.homeGit')
    await wrapper.find('.git-status-widget').trigger('click')
    expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({ command: expect.objectContaining({ type: 'openCard', descriptorId: 'git' }) }))
  })
})
