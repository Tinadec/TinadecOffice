// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import enMessages from '@/locales/en'
import GitHistoryView from './GitHistoryView.vue'
import GitStashView from './GitStashView.vue'
import GitFileHistoryBlameDrawer from './GitFileHistoryBlameDrawer.vue'
import GitPanel from '../GitPanel.vue'
import { focusDockCard } from '@/lib/homeDockSync'
import { api, createUserToolActionForPath } from '@/api'

vi.mock('@/components/ui', async () => {
  const { defineComponent, h } = await import('vue')
  const wrap = (tag = 'div') => defineComponent({
    setup: (_, { slots, attrs }) => () => h(tag, attrs, [slots.default?.(), slots.footer?.()]),
  })
  const UiCheckbox = defineComponent({
    props: { modelValue: Boolean, indeterminate: Boolean, disabled: Boolean },
    emits: ['update:modelValue'],
    setup(props, { emit, attrs }) {
      return () => h('input', {
        ...attrs,
        type: 'checkbox',
        checked: props.modelValue,
        disabled: props.disabled,
        onChange: (e: Event) => emit('update:modelValue', (e.target as HTMLInputElement).checked),
      })
    },
  })
  const UiInput = defineComponent({
    props: { modelValue: String },
    emits: ['update:modelValue'],
    setup(props, { emit, attrs }) {
      return () => h('input', {
        ...attrs,
        value: props.modelValue ?? '',
        onInput: (e: Event) => emit('update:modelValue', (e.target as HTMLInputElement).value),
      })
    },
  })
  const UiSelectField = defineComponent({
    props: { value: String, options: Array },
    emits: ['change'],
    setup(props, { emit, attrs }) {
      return () => h('select', {
        ...attrs,
        value: props.value ?? '',
        onChange: (e: Event) => emit('change', (e.target as HTMLSelectElement).value),
      })
    },
  })
  return {
    UiBadge: wrap('span'),
    UiButton: wrap('button'),
    UiCheckbox,
    UiInput,
    UiIslandCard: wrap('div'),
    UiScrollArea: wrap('div'),
    UiSelectField,
  }
})

const mocked = vi.hoisted(() => ({
  executeCodeTool: vi.fn(),
  createUserToolActionForPath: vi.fn(),
  decidePermissionRequest: vi.fn(),
  getUserToolAction: vi.fn(),
  decideApproval: vi.fn(),
  resumeUserToolAction: vi.fn(),
  gitLogList: vi.fn(),
  gitLogDetail: vi.fn(),
  gitFileHistory: vi.fn(),
  gitBlame: vi.fn(),
  gitFileAtRevision: vi.fn(),
  gitStashList: vi.fn(),
  gitStashShow: vi.fn(),
  gitLog: vi.fn(),
}))

vi.mock('@/api', () => ({
  api: {
    executeCodeTool: mocked.executeCodeTool,
    decidePermissionRequest: mocked.decidePermissionRequest,
    getUserToolAction: mocked.getUserToolAction,
    decideApproval: mocked.decideApproval,
    resumeUserToolAction: mocked.resumeUserToolAction,
    gitLogList: mocked.gitLogList,
    gitLogDetail: mocked.gitLogDetail,
    gitFileHistory: mocked.gitFileHistory,
    gitBlame: mocked.gitBlame,
    gitFileAtRevision: mocked.gitFileAtRevision,
    gitStashList: mocked.gitStashList,
    gitStashShow: mocked.gitStashShow,
    gitLog: mocked.gitLog,
    invokeStream: vi.fn(() => new AbortController()),
  },
  createUserToolActionForPath: mocked.createUserToolActionForPath,
}))

vi.mock('@/components/git/DiffViewer.vue', () => ({
  default: {
    name: 'DiffViewer',
    props: ['files', 'selectedFilePath', 'diffText', 'filePath'],
    template: '<div class="diff-viewer-stub" :data-file="selectedFilePath ?? filePath ?? \'\'">{{ diffText ?? "" }}</div>',
  },
}))

function createTestI18n() {
  return createI18n({
    legacy: false,
    locale: 'en',
    messages: { en: enMessages },
  })
}

function makeAction(id: string, toolId: string, status: string) {
  return {
    id,
    tool_id: toolId,
    status,
    permission_request_id: status === 'awaiting_user' ? `perm-${id}` : null,
    action_approval_id: `apr-${id}`,
    created_at: '2026-11-22T00:00:00Z',
  }
}

describe('#32 JetBrains Git Workbench & Tool Window', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()

    let actionCounter = 0
    mocked.createUserToolActionForPath.mockImplementation(async (_cwd: string, toolId: string) => {
      actionCounter += 1
      return makeAction(`act-${actionCounter}`, toolId, 'awaiting_user')
    })
    mocked.decidePermissionRequest.mockResolvedValue({})
    mocked.getUserToolAction.mockImplementation(async (id: string) => makeAction(id, 'git', 'awaiting_approval'))
    mocked.decideApproval.mockResolvedValue({})
    mocked.resumeUserToolAction.mockImplementation(async (id: string) => makeAction(id, 'git', 'completed'))

    mocked.executeCodeTool.mockImplementation(async (_toolId: string, payload: { arguments?: { action?: string } }) => {
      const action = payload?.arguments?.action
      if (action === 'diff_preview') {
        return {
          tool_id: 'git_worktree_manager',
          status: 'completed',
          summary: 'ok',
          evidence: [],
          requires_approval: false,
          approval_summary: null,
          data: {
            branch: 'main',
            upstream: 'origin/main',
            ahead: 2,
            behind: 1,
            files: [
              { path: 'src/app.ts', status: 'M', staged_status: 'M' },
              { path: 'notes.md', status: '?', is_untracked: true },
            ],
            sections: [
              {
                id: 'working_tree',
                kind: 'working_tree',
                title: 'Working Tree',
                diff: 'diff --git a/src/app.ts b/src/app.ts\n--- a/src/app.ts\n+++ b/src/app.ts\n@@ -1 +1 @@\n-a\n+b\n',
                files: [{ path: 'src/app.ts', change_type: 'modified', additions: 1, deletions: 1, binary: false, truncated: false }],
                file_count: 1,
                additions: 1,
                deletions: 1,
                notices: [],
              },
            ],
          },
        }
      }
      if (action === 'push_plan') {
        return {
          tool_id: 'git_worktree_manager',
          status: 'completed',
          summary: 'ok',
          evidence: [],
          requires_approval: false,
          approval_summary: null,
          data: {
            branch: 'main',
            upstream: 'origin/main',
            ahead: 2,
            behind: 1,
            recent_commits: ['abc1234 feat: initial'],
            push_ready: true,
            push_blockers: [],
            worktrees: [{ branch: 'main', path: 'D:/repo' }],
          },
        }
      }
      if (action === 'branch_list') {
        return {
          tool_id: 'git_worktree_manager',
          status: 'completed',
          summary: 'ok',
          evidence: [],
          requires_approval: false,
          approval_summary: null,
          data: {
            branches: [
              { name: 'main', is_current: true, is_remote: false },
              { name: 'feature/ui', is_current: false, is_remote: false },
              { name: 'origin/release', is_current: false, is_remote: true },
            ],
          },
        }
      }
      return {
        tool_id: 'git_worktree_manager',
        status: 'completed',
        summary: 'ok',
        evidence: [],
        requires_approval: false,
        approval_summary: null,
        data: {},
      }
    })

    mocked.gitLogList.mockResolvedValue({
      commits: [
        {
          hash: '1111111111111111111111111111111111111111',
          short_hash: '1111111',
          parents: ['2222222222222222222222222222222222222222'],
          author: 'Alice',
          author_email: 'alice@example.com',
          author_date: '2026-11-22T01:00:00Z',
          committer_date: '2026-11-22T01:00:00Z',
          subject: 'feat: graph commit',
          refs: [{ type: 'head', name: 'HEAD -> main', is_current: true }],
          lane_index: 0,
          edges: [{ from_lane: 0, to_lane: 1, parent_hash: '2222222222222222222222222222222222222222' }],
        },
        {
          hash: '2222222222222222222222222222222222222222',
          short_hash: '2222222',
          parents: [],
          author: 'Bob',
          author_email: 'bob@example.com',
          author_date: '2026-11-21T01:00:00Z',
          committer_date: '2026-11-21T01:00:00Z',
          subject: 'chore: base commit',
          refs: [{ type: 'branch', name: 'feature/ui', is_current: false }],
          lane_index: 1,
          edges: [],
        },
      ],
      truncated: true,
      next_cursor: '2222222222222222222222222222222222222222',
    })
    mocked.gitLogDetail.mockResolvedValue({
      commits: [{ hash: '1111111111111111111111111111111111111111', short_hash: '1111111', subject: 'feat: graph commit', author: 'Alice', author_date: '2026-11-22T01:00:00Z', parents: [], refs: [] }],
      files: [{ new_path: 'src/app.ts', status: 'modified', additions: 3, deletions: 1, is_binary: false }],
      patches: [],
    })
    mocked.gitStashList.mockResolvedValue({
      entries: [
        { ref_name: 'stash@{0}', hash: 'aaa111', index: 0, branch: 'main', subject: 'WIP editor polish', created_at: '2m ago' },
      ],
    })
    mocked.gitStashShow.mockResolvedValue({
      stash_ref: 'stash@{0}',
      files: [{ new_path: 'src/app.ts', status: 'modified', additions: 1, deletions: 0, is_binary: false }],
      patches: [],
    })
    mocked.gitFileHistory.mockResolvedValue({
      entries: [
        {
          commit: { hash: 'aaaa1111', short_hash: 'aaaa111', author: 'Alice', author_date: '2026-11-22T01:00:00Z', subject: 'fix: file change', parents: [], refs: [] },
          change: { new_path: 'src/app.ts', status: 'modified', additions: 2, deletions: 1, is_binary: false },
          patch: null,
        },
      ],
    })
    mocked.gitBlame.mockResolvedValue({
      lines: [
        { line_number: 1, commit: 'aaaa1111bbbb2222', author: 'Alice', content: 'export const x = 1' },
      ],
    })
  })

  it('GitHistoryView renders multi-lane SVG graph, ref badges, Load More, and commit detail', async () => {
    const wrapper = mount(GitHistoryView, {
      props: { cwd: 'D:/repo', commits: [], loading: false },
      global: { plugins: [createTestI18n()] },
    })
    await flushPromises()

    expect(wrapper.findAll('[data-testid="git-commit-row"]')).toHaveLength(2)
    expect(wrapper.findAll('svg.git-graph-svg path').length).toBeGreaterThanOrEqual(1)
    expect(wrapper.findAll('[data-testid="git-ref-badge"]')[0].text()).toContain('HEAD -> main')
    expect(wrapper.find('[data-testid="git-log-load-more"]').exists()).toBe(true)

    await wrapper.findAll('[data-testid="git-commit-row"]')[0].trigger('click')
    await flushPromises()
    expect(mocked.gitLogDetail).toHaveBeenCalledWith('D:/repo', '1111111111111111111111111111111111111111', expect.objectContaining({ include_patch: true }))
    expect(wrapper.text()).toContain('src/app.ts')
  })

  it('GitStashView lists stashes with ref_name, expands diff with stash_ref, and emits push/apply/pop/drop', async () => {
    const wrapper = mount(GitStashView, {
      props: { cwd: 'D:/repo', operationLoading: false },
      global: { plugins: [createTestI18n()] },
    })
    await flushPromises()

    expect(wrapper.findAll('[data-testid="git-stash-item"]')).toHaveLength(1)
    expect(wrapper.text()).toContain('stash@{0}')
    expect(wrapper.text()).toContain('WIP editor polish')

    await wrapper.get('.git-stash-row').trigger('click')
    await flushPromises()
    expect(mocked.gitStashShow).toHaveBeenCalledWith('D:/repo', 'stash@{0}', expect.any(Object))

    await wrapper.get('[data-testid="git-stash-apply-btn"]').trigger('click')
    expect(wrapper.emitted('stash-apply')?.[0]).toEqual(['stash@{0}'])
    await wrapper.get('[data-testid="git-stash-pop-btn"]').trigger('click')
    expect(wrapper.emitted('stash-pop')?.[0]).toEqual(['stash@{0}'])
    await wrapper.get('[data-testid="git-stash-drop-btn"]').trigger('click')
    expect(wrapper.emitted('stash-drop')?.[0]?.[0]).toBe('stash@{0}')
  })

  it('GitFileHistoryBlameDrawer loads history and blame and emits open-commit from blame gutter', async () => {
    const wrapper = mount(GitFileHistoryBlameDrawer, {
      props: { open: true, cwd: 'D:/repo', filePath: 'src/app.ts', initialTab: 'history' },
      global: { plugins: [createTestI18n()] },
    })
    await flushPromises()

    expect(mocked.gitFileHistory).toHaveBeenCalledWith('D:/repo', 'src/app.ts', expect.any(Object))
    expect(wrapper.findAll('[data-testid="git-file-history-row"]')).toHaveLength(1)

    await wrapper.get('[data-testid="file-inspector-tab-blame"]').trigger('click')
    await flushPromises()
    expect(mocked.gitBlame).toHaveBeenCalledWith('D:/repo', 'src/app.ts', expect.any(Object))
    expect(wrapper.text()).toContain('export const x = 1')

    await wrapper.get('.git-blame-gutter').trigger('click')
    expect(wrapper.emitted('open-commit')?.[0]).toEqual(['aaaa1111bbbb2222'])
  })

  it('GitPanel executes 1-click governed amend commit, branch popover checkout, push confirm modal, and dock focus', async () => {
    const wrapper = mount(GitPanel, {
      props: {
        approvals: [],
        currentProjectPath: 'D:/repo',
        selectedSessionId: null,
      },
      global: { plugins: [createTestI18n()] },
    })
    await flushPromises()

    // 1. Searchable branch popover -> 1-click governed checkout
    await wrapper.get('[data-testid="git-branch-popover-trigger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="git-branch-popover-search"]').setValue('feat')
    const featureBtn = wrapper.findAll('.git-branch-popover-item').find((b) => b.text().includes('feature/ui'))
    expect(featureBtn).toBeTruthy()
    await featureBtn!.trigger('click')
    await flushPromises()
    expect(createUserToolActionForPath).toHaveBeenCalledWith(
      'D:/repo',
      'git_checkout',
      expect.objectContaining({ branch: 'feature/ui', confirm_checkout: expect.any(String) }),
      expect.any(String),
    )

    // 2. 1-click governed Amend Commit without a chat session
    const commitInput = wrapper.get('input.commit-editor-subject-input')
    await commitInput.setValue('amend latest work')
    const amendCheckbox = wrapper.get('.git-amend-toggle input[type="checkbox"]')
    await amendCheckbox.setValue(true)
    await wrapper.get('[data-testid="git-primary-commit-btn"]').trigger('click')
    await flushPromises()
    expect(createUserToolActionForPath).toHaveBeenCalledWith(
      'D:/repo',
      'git_commit',
      expect.objectContaining({ message: 'feat: amend latest work', amend: true, confirm_commit: expect.any(String) }),
      expect.any(String),
    )

    // 3. Push opens explicit confirmation modal; cancel avoids mutation; accept executes git_push
    await wrapper.get('[data-testid="git-header-push-btn"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="git-confirm-dialog"]').exists()).toBe(true)
    expect(createUserToolActionForPath).not.toHaveBeenCalledWith('D:/repo', 'git_push', expect.anything(), expect.anything())

    await wrapper.get('[data-testid="git-confirm-cancel-btn"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="git-confirm-dialog"]').exists()).toBe(false)

    await wrapper.get('[data-testid="git-header-push-btn"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="git-confirm-accept-btn"]').trigger('click')
    await flushPromises()
    expect(createUserToolActionForPath).toHaveBeenCalledWith(
      'D:/repo',
      'git_push',
      expect.objectContaining({ remote: 'origin', confirm_push: expect.any(String) }),
      expect.any(String),
    )

    // 4. Dock focus from CodeEditorCard deep-links directly into File Inspector Blame drawer
    focusDockCard({
      card: 'git',
      reason: 'code',
      filePath: 'src/app.ts',
      gitTab: 'changes',
      gitSubView: 'blame',
    })
    await flushPromises()
    expect(wrapper.find('[data-testid="git-file-inspector-drawer"]').exists()).toBe(true)
    expect(mocked.gitBlame).toHaveBeenCalledWith('D:/repo', 'src/app.ts', expect.any(Object))

    // 5. Out-of-root external temp worktrees never switch inspectedWorktreePath; in-root sub-worktrees use root cwd + repository_path
    mocked.executeCodeTool.mockClear()
    focusDockCard({
      card: 'git',
      reason: 'worktree',
      worktreePath: 'C:/Users/lincu/AppData/Local/Temp/ext-wt',
    })
    await flushPromises()
    expect(mocked.executeCodeTool).not.toHaveBeenCalledWith(
      'git_worktree_manager',
      expect.objectContaining({ cwd: 'C:/Users/lincu/AppData/Local/Temp/ext-wt' }),
    )

    focusDockCard({
      card: 'git',
      reason: 'worktree',
      worktreePath: 'D:/repo/.tinadec/worktrees/wt-1',
    })
    await flushPromises()
    expect(mocked.executeCodeTool).toHaveBeenCalledWith(
      'git_worktree_manager',
      expect.objectContaining({
        cwd: 'D:/repo',
        arguments: expect.objectContaining({
          action: 'diff_preview',
          repository_path: 'D:/repo/.tinadec/worktrees/wt-1',
        }),
      }),
    )

    wrapper.unmount()
  })
})
