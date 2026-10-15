// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { ref, nextTick } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { GitStatusSummary } from '@/lib/gitStatusSummary'

const state = {
  summary: ref<GitStatusSummary | null>(null),
  updatedAt: ref<number | null>(null),
  error: ref<'not_a_repo' | 'failed' | null>(null),
  loading: ref(false),
  busy: ref(false),
  recentCommit: ref<{ shortHash: string; subject: string } | null>(null),
  historyLoaded: ref(false),
  historyError: ref(false),
  businessReady: ref(true),
  refresh: vi.fn(),
}
const hostStatus = ref({ state: 'ready' })
vi.mock('@/composables/useHomeGitStatus', () => ({ useHomeGitStatus: () => state }))
vi.mock('@/composables/useConnection', () => ({ useConnection: () => ({ hostStatus }) }))
vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()), useI18n: () => ({ t: (key: string) => key, locale: ref('zh-CN') }) }))
import GitStatusWidget from '../../../TinadecUI/src/components/cards/home/GitStatusWidget.vue'

const project = { id: 'a', path: 'C:/repo', name: 'Workspace', created_at: '' }
const summary: GitStatusSummary = { branch: 'main', upstream: 'origin/main', detached: false, ahead: 2, behind: 0,
  total: 0, staged: 0, unstaged: 0, untracked: 0, conflicts: 0, conflictPath: null, gitRoot: 'C:/repo', files: [] }
function widget(selected: typeof project | null = project) {
  return mount(GitStatusWidget, { props: { project: selected }, global: { stubs: {
    UiIslandCard: { template: '<article><slot /></article>' },
  } } })
}

describe('Home Git widget visible states', () => {
  beforeEach(() => {
    state.summary.value = null; state.error.value = null; state.loading.value = false; state.busy.value = false
    state.recentCommit.value = null; state.historyLoaded.value = false; state.historyError.value = false
    state.businessReady.value = true; state.updatedAt.value = null
    state.refresh.mockReset(); hostStatus.value = { state: 'ready' }
  })
  it('shows identity and clean status, opens existing Git tab without mutations', async () => {
    state.summary.value = summary
    state.updatedAt.value = Date.now()
    const wrapper = widget()
    expect(wrapper.text()).toContain('context.gitWidgetClean')
    expect(wrapper.text()).toContain('origin/main')
    await wrapper.find('.git-widget-open').trigger('click')
    expect(wrapper.emitted('open')).toHaveLength(1)
    expect(state.refresh).not.toHaveBeenCalled()
  })
  it('prioritizes conflicts, does not turn stale success into current clean', async () => {
    state.summary.value = { ...summary, total: 2, staged: 1, conflicts: 1, conflictPath: 'src/long-file.ts', files: [
      { path: 'src/long-file.ts', mark: '!', kind: 'conflict' }, { path: 'README.md', mark: 'M', kind: 'changed' },
    ] }
    const wrapper = widget()
    expect(wrapper.text()).toContain('context.gitWidgetConflicts')
    expect(wrapper.text()).toContain('src/long-file.ts')
    state.error.value = 'failed'
    await nextTick()
    expect(wrapper.text()).toContain('context.gitWidgetReadFailed')
    expect(wrapper.text()).toContain('context.gitWidgetPrevious')
    expect(wrapper.text()).not.toContain('src/long-file.ts')
  })
  it('shows up to three actual files, an overflow count and a recent commit without Git write controls', () => {
    state.summary.value = { ...summary, total: 4, files: [
      { path: 'src/first.ts', mark: 'M', kind: 'staged' },
      { path: 'src/second.ts', mark: 'M', kind: 'changed' },
      { path: 'docs/third.md', mark: '?', kind: 'untracked' },
      { path: 'src/fourth.ts', mark: 'A', kind: 'staged' },
    ] }
    state.recentCommit.value = { shortHash: 'abcdef1', subject: 'fix: widget polish' }
    state.historyLoaded.value = true
    const wrapper = widget()
    expect(wrapper.findAll('.git-widget-file')).toHaveLength(3)
    expect(wrapper.text()).toContain('context.gitWidgetMoreFiles')
    expect(wrapper.text()).toContain('fix: widget polish')
    expect(wrapper.text()).not.toContain('src/fourth.ts')
    expect(wrapper.findAll('button')).toHaveLength(2) // refresh + open only
  })

  it('separates no project, non-repo and preview' , async () => {
    const wrapper = widget(null)
    expect(wrapper.text()).toContain('context.gitWidgetNoProject')
    await wrapper.setProps({ project })
    state.error.value = 'not_a_repo'; await nextTick()
    expect(wrapper.text()).toContain('context.gitWidgetNotRepo')
    state.businessReady.value = false; hostStatus.value = { state: 'preview' }; await nextTick()
    expect(wrapper.text()).toContain('context.gitWidgetPreview')
  })
})
