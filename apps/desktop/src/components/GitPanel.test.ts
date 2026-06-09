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
    createApproval: vi.fn()
  }
}));

const executeCodeTool = vi.mocked(api.executeCodeTool);
const createApproval = vi.mocked(api.createApproval);

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
      { path: 'src/app.ts', status: 'modified' },
      { path: 'README.md', status: 'staged_added' }
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
      },
      {
        id: 'staged',
        kind: 'staged',
        title: 'Staged',
        subtitle: 'Index changes ready for commit',
        base_ref: null,
        head_ref: null,
        file_count: 1,
        additions: 1,
        deletions: 0,
        notices: [],
        files: [
          { path: 'README.md', previous_path: null, change_type: 'added', additions: 1, deletions: 0, binary: false, truncated: false }
        ],
        diff: `diff --git a/README.md b/README.md
new file mode 100644
--- /dev/null
+++ b/README.md
@@ -0,0 +1 @@
+hello
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
    push_ready: false,
    push_blockers: ['no upstream'],
    worktrees: [
      { branch: 'main', path: 'D:/repo' }
    ]
  }
};

function mountGitPanel(approvals: ApprovalDto[] = []) {
  return mount(GitPanel, {
    props: {
      approvals,
      currentProjectPath: 'D:/repo',
      selectedSessionId: 'session-1'
    },
    global: {
      stubs: {
        AlertTriangle: true,
        CheckCircle2: true,
        FileCode2: true,
        GitBranch: true,
        GitCommitHorizontal: true,
        GitPullRequest: true,
        GitCompare: true,
        RefreshCw: true,
        ShieldCheck: true,
        ShieldX: true,
        Upload: true
      }
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
    createApproval.mockReset();
    executeCodeTool
      .mockResolvedValueOnce(previewResult)
      .mockResolvedValueOnce(pushPlanResult);
  });

  it('loads Git review sections and switches files by section', async () => {
    const wrapper = mountGitPanel();
    await flushPromises();

    expect(executeCodeTool).toHaveBeenNthCalledWith(1, 'git_worktree_manager', {
      cwd: 'D:/repo',
      arguments: { action: 'diff_preview', max_files: 120, max_diff_bytes: 180000 }
    });
    expect(wrapper.text()).toContain('main');
    expect(wrapper.text()).toContain('src/app.ts');

    await wrapper.findAll('.git-section-tab')[1].trigger('click');
    await nextTick();

    expect(wrapper.text()).toContain('README.md');
    expect(wrapper.text()).toContain('hello');
  });

  it('creates commit approvals for selected files and emits the approval', async () => {
    createApproval.mockResolvedValueOnce(pendingCommitApproval);
    const wrapper = mountGitPanel();
    await flushPromises();

    await wrapper.find('textarea').setValue('Commit Git panel work');
    await wrapper.find('.git-commit-approval-button').trigger('click');
    await flushPromises();

    expect(createApproval).toHaveBeenCalledWith({
      session_id: 'session-1',
      kind: 'git',
      summary: 'Commit 2 files on main',
      command: 'git add -- src/app.ts README.md && git commit -m "Commit Git panel work"',
      cwd: 'D:/repo'
    });
    expect(wrapper.emitted('approval-created')?.[0]).toEqual([pendingCommitApproval]);
    expect(wrapper.text()).toContain('context.gitCommitApprovalRequested');
  });

  it('creates and executes stage approvals for selected files', async () => {
    createApproval.mockResolvedValueOnce(pendingStageApproval);
    executeCodeTool
      .mockResolvedValueOnce({
        tool_id: 'git_worktree_manager',
        status: 'completed',
        summary: 'Staged 2 paths.',
        evidence: [],
        requires_approval: true,
        approval_summary: null,
        data: {}
      })
      .mockResolvedValueOnce(previewResult)
      .mockResolvedValueOnce(pushPlanResult);

    const wrapper = mountGitPanel([approvedStageApproval]);
    await flushPromises();

    await wrapper.find('.git-stage-approval-button').trigger('click');
    await flushPromises();

    expect(createApproval).toHaveBeenCalledWith({
      session_id: 'session-1',
      kind: 'git',
      summary: 'Stage 2 files on main',
      command: 'git add -- src/app.ts README.md',
      cwd: 'D:/repo'
    });
    expect(wrapper.emitted('approval-created')?.[0]).toEqual([pendingStageApproval]);

    await wrapper.setProps({ approvals: [approvedStageApproval] });
    await nextTick();
    await wrapper.find('.git-index-execute-button').trigger('click');
    await flushPromises();

    expect(executeCodeTool).toHaveBeenCalledWith('git_worktree_manager', {
      session_id: 'session-1',
      approval_id: 'approval-stage',
      cwd: 'D:/repo',
      arguments: {
        action: 'stage',
        confirm_stage: true,
        paths: ['src/app.ts', 'README.md']
      }
    });
  });

  it('emits inline decisions for pending Git approvals', async () => {
    createApproval.mockResolvedValueOnce(pendingCommitApproval);
    const wrapper = mountGitPanel([pendingCommitApproval]);
    await flushPromises();

    await wrapper.find('textarea').setValue('Commit Git panel work');
    await wrapper.find('.git-commit-approval-button').trigger('click');
    await flushPromises();

    await wrapper.find('.git-approval-inline-actions .approve').trigger('click');

    expect(wrapper.emitted('decide-approval')?.[0]).toEqual([pendingCommitApproval, 'approved']);
  });

  it('executes approved no-upstream pushes with origin as the default remote', async () => {
    createApproval.mockResolvedValueOnce(approvedPushApproval);
    executeCodeTool
      .mockResolvedValueOnce({
        tool_id: 'git_worktree_manager',
        status: 'completed',
        summary: 'Pushed main.',
        evidence: [],
        requires_approval: true,
        approval_summary: null,
        data: {}
      })
      .mockResolvedValueOnce(previewResult)
      .mockResolvedValueOnce(pushPlanResult);

    const wrapper = mountGitPanel([approvedPushApproval]);
    await flushPromises();

    await wrapper.find('.git-push-approval-button').trigger('click');
    await flushPromises();
    expect(createApproval).toHaveBeenCalledWith({
      session_id: 'session-1',
      kind: 'git',
      summary: 'Push main to origin (1 ahead)',
      command: 'git push -u origin main',
      cwd: 'D:/repo'
    });

    await wrapper.setProps({ approvals: [approvedPushApproval] });
    await nextTick();
    await wrapper.find('.git-push-execute-button').trigger('click');
    await flushPromises();

    expect(executeCodeTool).toHaveBeenCalledWith('git_worktree_manager', {
      session_id: 'session-1',
      approval_id: 'approval-push',
      cwd: 'D:/repo',
      arguments: {
        action: 'push',
        confirm_push: true,
        set_upstream: true,
        remote: 'origin'
      }
    });
  });
});
