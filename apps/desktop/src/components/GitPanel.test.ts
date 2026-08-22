// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { nextTick } from 'vue';
import GitPanel from './GitPanel.vue';
import { api, type ApprovalDto, type CodeToolExecuteResultDto } from '../api';

vi.mock('vue-i18n', () => ({
  useI18n: () => ({
    t: (key: string) => key
  })
}));

vi.mock('../api', () => ({
  api: {
    executeCodeTool: vi.fn(),
    invokeStream: vi.fn(() => new AbortController()),
    gitLog: vi.fn(),
  }
}));

const executeCodeTool = vi.mocked(api.executeCodeTool);

const pendingCommitApproval: ApprovalDto = {
  id: 'approval-commit',
  session_id: 'session-1',
  kind: 'git',
  summary: 'Commit changes',
  command: 'git commit',
  cwd: 'D:/repo',
  status: 'pending',
  created_at: '2026-06-09T00:00:00Z'
};

const pendingStageApproval: ApprovalDto = {
  id: 'approval-stage',
  session_id: 'session-1',
  kind: 'git',
  summary: 'Stage changes',
  command: 'git add -- src/app.ts README.md',
  cwd: 'D:/repo',
  status: 'pending',
  created_at: '2026-06-09T00:00:00Z'
};

const approvedStageApproval: ApprovalDto = {
  ...pendingStageApproval,
  status: 'approved',
  decided_at: '2026-06-09T00:01:00Z'
};

const approvedPushApproval: ApprovalDto = {
  id: 'approval-push',
  session_id: 'session-1',
  kind: 'git',
  summary: 'Push main',
  command: 'git push -u origin main',
  cwd: 'D:/repo',
  status: 'approved',
  created_at: '2026-06-09T00:00:00Z',
  decided_at: '2026-06-09T00:01:00Z'
};

const previewResult: CodeToolExecuteResultDto = {
  tool_id: 'git_worktree_manager',
  status: 'completed',
  summary: 'Prepared 2 Git diff file previews.',
  evidence: [],
  requires_approval: true,
  approval_summary: 'Create or modify Git branches/worktrees.',
  data: {
    branch: 'main',
    upstream: null,
    ahead: 1,
    behind: 0,
    files: [
      { path: 'src/app.ts', status: 'M' },
      { path: 'README.md', status: 'A' }
    ],
    sections: [
      {
        id: 'working_tree',
        kind: 'working_tree',
        title: 'Working Tree',
        subtitle: 'Tracked and untracked workspace changes',
        base_ref: null,
        head_ref: null,
        file_count: 1,
        additions: 1,
        deletions: 1,
        notices: [],
        files: [
          { path: 'src/app.ts', previous_path: null, change_type: 'modified', additions: 1, deletions: 1, binary: false, truncated: false }
        ],
        diff: `diff --git a/src/app.ts b/src/app.ts
--- a/src/app.ts
+++ b/src/app.ts
@@ -1,2 +1,2 @@
-old
+new
`
      }
    ]
  }
};

const pushPlanResult: CodeToolExecuteResultDto = {
  tool_id: 'git_worktree_manager',
  status: 'completed',
  summary: 'Push plan blocked: no upstream.',
  evidence: [],
  requires_approval: true,
  approval_summary: 'Create or modify Git branches/worktrees.',
  data: {
    branch: 'main',
    upstream: null,
    ahead: 1,
    behind: 0,
    diff_stat: ' src/app.ts | 2 +-\n README.md | 1 +',
    remotes: [
      'origin https://github.com/example/repo.git (fetch)',
      'origin https://github.com/example/repo.git (push)'
    ],
    recent_commits: [
      'abc1234 (HEAD -> main) last change',
      'def5678 initial'
    ],
    push_ready: false,
    push_blockers: ['no upstream'],
    suggested_commands: ['git status --short --branch', 'git add <paths>', 'git commit -m "<message>"'],
    worktrees: [
      { branch: 'main', path: 'D:/repo' }
    ]
  }
};

// Stub sub-components to avoid deep rendering Monaco, etc.
const stubs = {
  GitChangesView: {
    name: 'GitChangesView',
    template: '<div class="stub-git-changes" />',
    props: ['cwd', 'sessionId', 'statusFiles'],
  },
  GitHistoryView: {
    name: 'GitHistoryView',
    template: '<div class="stub-git-history" />',
  },
  GitBranchView: {
    name: 'GitBranchView',
    template: '<div class="stub-git-branch" />',
  },
};

function mountGitPanel(approvals: ApprovalDto[] = []) {
  return mount(GitPanel, {
    props: {
      approvals,
      currentProjectPath: 'D:/repo',
      selectedSessionId: 'session-1'
    },
    global: {
      stubs,
    }
  });
}

async function flushPromises() {
  await Promise.resolve();
  await Promise.resolve();
  await nextTick();
}

describe('GitPanel', () => {
  beforeEach(() => {
    executeCodeTool.mockReset();
    executeCodeTool
      .mockResolvedValueOnce(previewResult)
      .mockResolvedValueOnce(pushPlanResult);
  });

  it('loads Git status and push plan on mount', async () => {
    const wrapper = mountGitPanel();
    await flushPromises();

    expect(executeCodeTool).toHaveBeenCalledWith('git_worktree_manager', {
      cwd: 'D:/repo',
      arguments: { action: 'diff_preview', max_files: 120, max_diff_bytes: 180000 }
    });
    expect(executeCodeTool).toHaveBeenCalledWith('git_worktree_manager', {
      cwd: 'D:/repo',
      arguments: { action: 'push_plan' }
    });

    // Branch name should be rendered in the header
    expect(wrapper.text()).toContain('main');
  });

  it('renders the branch info and ahead/behind indicators', async () => {
    const wrapper = mountGitPanel();
    await flushPromises();

    expect(wrapper.find('.git-branch-name').text()).toContain('main');
    expect(wrapper.find('.git-ahead').text()).toContain('1');
  });

  it('renders tab navigation with Changes, History, and Branches', async () => {
    const wrapper = mountGitPanel();
    await flushPromises();

    const navItems = wrapper.findAll('.git-nav-item');
    expect(navItems.length).toBeGreaterThanOrEqual(3);
    expect(wrapper.text()).toContain('context.gitTabChanges');
    expect(wrapper.text()).toContain('context.gitHistory');
    expect(wrapper.text()).toContain('context.gitBranches');
  });

  it('shows the changes badge with file count', async () => {
    const wrapper = mountGitPanel();
    await flushPromises();

    const badge = wrapper.find('.git-nav-badge');
    expect(badge.exists()).toBe(true);
    expect(badge.text()).toContain('2');
  });

  it('shows empty state when no project path is provided', () => {
    const wrapper = mount(GitPanel, {
      props: {
        approvals: [],
        currentProjectPath: undefined,
        selectedSessionId: null,
      },
      global: { stubs },
    });

    expect(wrapper.find('.git-empty').exists()).toBe(true);
    expect(wrapper.text()).toContain('context.diffPlaceholder');
  });

  it('renders the approval disclaimer', async () => {
    const wrapper = mountGitPanel();
    await flushPromises();

    expect(wrapper.find('.git-disclaimer').exists()).toBe(true);
    expect(wrapper.text()).toContain('context.gitPlanApproval');
  });

  it('passes correct props to GitChangesView', async () => {
    const wrapper = mountGitPanel();
    await flushPromises();

    const changesView = wrapper.findComponent({ name: 'GitChangesView' });
    expect(changesView.exists()).toBe(true);
    expect(changesView.props('cwd')).toBe('D:/repo');
    expect(changesView.props('sessionId')).toBe('session-1');
    expect(changesView.props('statusFiles')).toHaveLength(2);
  });

  it('switches to history tab and loads log', async () => {
    executeCodeTool.mockResolvedValueOnce({
      tool_id: 'git_worktree_manager',
      status: 'completed',
      summary: 'Retrieved 0 commits.',
      evidence: [],
      requires_approval: false,
      approval_summary: null,
      data: { commits: [] },
    });

    const wrapper = mountGitPanel();
    await flushPromises();

    // Click history tab
    const historyTab = wrapper.findAll('.git-nav-item')[1];
    await historyTab.trigger('click');
    await flushPromises();

    expect(wrapper.findComponent({ name: 'GitHistoryView' }).exists()).toBe(true);
  });

  it('switches to branches tab', async () => {
    executeCodeTool.mockResolvedValue({
      tool_id: 'git_worktree_manager',
      status: 'completed',
      summary: 'ok',
      evidence: [],
      requires_approval: false,
      approval_summary: null,
      data: { worktrees: [] },
    });

    const wrapper = mountGitPanel();
    await flushPromises();

    const branchTab = wrapper.findAll('.git-nav-item')[2];
    await branchTab.trigger('click');
    await flushPromises();

    expect(wrapper.findComponent({ name: 'GitBranchView' }).exists()).toBe(true);
  });
});
