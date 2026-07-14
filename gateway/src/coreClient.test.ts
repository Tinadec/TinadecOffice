import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, test } from 'node:test';
import { coreEndpoint } from './coreClient.js';
import { codeToolApprovalBlockFor, codeToolApprovalUnavailableBlock, codeToolRequiresApproval, executeCodeTool, listCodeToolIds, listCodeToolSpecs } from './codeTools.js';
import { disposeToolLayerProcesses, disposeToolLayerWorkspace } from './toolLayerBridge.js';

after(() => disposeToolLayerProcesses());

test('coreEndpoint resolves API paths against the configured core URL', () => {
  assert.equal(coreEndpoint('/api/v1/health'), 'http://127.0.0.1:48731/api/v1/health');
});

test('Code tools expose programming-domain execution contracts', async () => {
  assert.deepEqual(listCodeToolIds().sort(), [
    'apply_patch',
    'bash_environment',
    'code_editor',
    'debug_session',
    'git_blame',
    'git_branch_create',
    'git_branch_delete',
    'git_branch_list',
    'git_branch_rename',
    'git_checkout',
    'git_commit',
    'git_conflict_preview',
    'git_conflict_resolve',
    'git_diff',
    'git_fetch',
    'git_file_at_revision',
    'git_file_history',
    'git_log_detail',
    'git_log_list',
    'git_merge',
    'git_pull',
    'git_push',
    'git_push_readiness',
    'git_rebase',
    'git_ref_list',
    'git_remote_list',
    'git_stage',
    'git_status',
    'git_unstage',
    'git_worktree_create',
    'git_worktree_list',
    'git_worktree_manager',
    'git_worktree_remove',
    'glob_search',
    'grep_content',
    'language_runtime_probe',
    'list_directory',
    'project_template_scaffold',
    'project_templates',
    'read_file',
    'review_format',
    'sandbox_exec',
    'search_files'
  ]);

  const specs = listCodeToolSpecs();
  const runtimeProbe = specs.find((tool) => tool.id === 'language_runtime_probe');
  assert.deepEqual(runtimeProbe?.language_support?.sort(), ['bun', 'csharp', 'flutter', 'golang', 'java', 'nim', 'nodejs', 'python', 'rust', 'zig']);
  assert.equal(specs.find((tool) => tool.id === 'bash_environment')?.requires_approval, true);
  assert.equal(specs.find((tool) => tool.id === 'project_templates')?.category, 'project');
  assert.equal(specs.find((tool) => tool.id === 'project_template_scaffold')?.requires_approval, true);
  assert.equal(specs.find((tool) => tool.id === 'git_status')?.requires_approval, true);
  assert.equal(specs.find((tool) => tool.id === 'git_stage')?.requires_approval, true);
  assert.equal(specs.find((tool) => tool.id === 'git_commit')?.requires_approval, true);

  const gitRead = await executeCodeTool('git_status', { cwd: process.cwd(), arguments: {} });
  assert.equal(gitRead?.status, 'blocked');
  assert.ok(gitRead?.evidence.includes('approval:required'));
  const gitIndex = await executeCodeTool('git_stage', { cwd: process.cwd(), arguments: { paths: ['README.md'] } });
  assert.equal(gitIndex?.status, 'blocked');
  assert.ok(gitIndex?.evidence.includes('approval:required'));

  const search = await executeCodeTool('search_files', { arguments: { query: 'AgentWorkflowRuntime' } });
  assert.equal(search?.requires_approval, false);
  assert.equal(search?.status, 'failed');
  assert.ok(search?.evidence.includes('search_files:missing-cwd'));
  assert.deepEqual(search?.data.argument_keys, ['query']);

  const patch = await executeCodeTool('apply_patch', { cwd: 'D:/github/TinadecOffice' });
  assert.equal(patch?.requires_approval, true);
  assert.equal(patch?.status, 'blocked');

  const templates = await executeCodeTool('project_templates');
  assert.equal(templates?.requires_approval, false);
  assert.ok(Array.isArray(templates?.data.templates));
  assert.ok((templates?.data.language_support as string[]).includes('rust'));

  assert.equal(await executeCodeTool('unknown_tool'), null);
});

test('project templates can preview generated files without mutating the workspace', async () => {
  const preview = await executeCodeTool('project_templates', {
    arguments: {
      action: 'preview',
      template_id: 'rust-cli',
      project_name: 'hello-rust'
    }
  });

  assert.equal(preview?.status, 'completed');
  assert.equal(preview?.requires_approval, false);
  const files = preview?.data.files as Array<{ path: string; content: string }>;
  assert.ok(files.some((file) => file.path === 'Cargo.toml' && file.content.includes('name = "hello-rust"')));
  assert.ok(files.some((file) => file.path === 'src/main.rs' && file.content.includes('Hello from hello-rust')));
});

test('Code tool approval gate trusts only approved Core approval state', () => {
  assert.equal(codeToolRequiresApproval('git_worktree_manager'), true);
  assert.equal(codeToolRequiresApproval('project_templates'), false);
  assert.equal(codeToolRequiresApproval('missing_tool'), null);

  const request = {
    session_id: 'sess_test',
    approval_id: 'appr_test'
  };

  const pending = codeToolApprovalBlockFor('git_worktree_manager', request, [
    { id: 'appr_test', session_id: 'sess_test', kind: 'git', status: 'pending' }
  ]);
  assert.equal(pending?.status, 'blocked');
  assert.equal(pending?.data.approval_status, 'pending');

  const missing = codeToolApprovalBlockFor('git_worktree_manager', request, []);
  assert.equal(missing?.status, 'blocked');
  assert.equal(missing?.data.approval_id, 'appr_test');
  assert.match(missing?.summary ?? '', /not found/);

  const wrongKind = codeToolApprovalBlockFor('git_worktree_manager', request, [
    { id: 'appr_test', session_id: 'sess_test', kind: 'shell', status: 'approved' }
  ]);
  assert.equal(wrongKind?.status, 'blocked');
  assert.equal(wrongKind?.data.approval_kind, 'shell');
  assert.deepEqual(wrongKind?.data.allowed_approval_kinds, ['git']);

  const approved = codeToolApprovalBlockFor('git_worktree_manager', request, [
    { id: 'appr_test', session_id: 'sess_test', kind: 'git', status: 'approved' }
  ]);
  assert.equal(approved, null);

  const cwdMismatch = codeToolApprovalBlockFor('git_worktree_manager', {
    ...request,
    cwd: 'D:/other-repo',
    arguments: { action: 'push' }
  }, [
    { id: 'appr_test', session_id: 'sess_test', kind: 'git', status: 'approved', cwd: 'D:/repo', command: 'git push' }
  ]);
  assert.equal(cwdMismatch?.status, 'blocked');
  assert.equal(cwdMismatch?.data.request_cwd, 'D:/other-repo');
  assert.equal(cwdMismatch?.data.approval_cwd, 'D:/repo');
  assert.ok((cwdMismatch?.evidence ?? []).includes('approval:context-mismatch'));

  const actionMismatch = codeToolApprovalBlockFor('git_worktree_manager', {
    ...request,
    cwd: 'D:/repo',
    arguments: { action: 'pull' }
  }, [
    { id: 'appr_test', session_id: 'sess_test', kind: 'git', status: 'approved', cwd: 'D:/repo', command: 'git push' }
  ]);
  assert.equal(actionMismatch?.status, 'blocked');
  assert.equal(actionMismatch?.data.requested_action, 'pull');
  assert.equal(actionMismatch?.data.approval_command, 'git push');
  assert.ok((actionMismatch?.evidence ?? []).includes('approval:context-mismatch'));

  const directCommitMismatch = codeToolApprovalBlockFor('git_commit', {
    ...request,
    cwd: 'D:/repo',
    arguments: { confirm_commit: true, commit_staged_only: true, message: 'test' }
  }, [
    { id: 'appr_test', session_id: 'sess_test', kind: 'git', status: 'approved', cwd: 'D:/repo', command: 'git push' }
  ]);
  assert.equal(directCommitMismatch?.status, 'blocked');
  assert.equal(directCommitMismatch?.data.requested_action, 'commit');

  const directBranchMismatch = codeToolApprovalBlockFor('git_branch_delete', {
    ...request,
    cwd: 'D:/repo',
    arguments: { branch: 'feature', confirm_delete_branch: true }
  }, [
    { id: 'appr_test', session_id: 'sess_test', kind: 'git', status: 'approved', cwd: 'D:/repo', command: 'git push' }
  ]);
  assert.equal(directBranchMismatch?.status, 'blocked');
  assert.equal(directBranchMismatch?.data.requested_action, 'delete_branch');

  const compositeCommitApproval = codeToolApprovalBlockFor('git_commit', {
    ...request,
    cwd: 'D:/repo',
    arguments: { confirm_commit: true, paths: ['note.txt'], message: 'test' }
  }, [
    { id: 'appr_test', session_id: 'sess_test', kind: 'git', status: 'approved', cwd: 'D:/repo', command: 'git add -- note.txt && git commit -m "test"' }
  ]);
  assert.equal(compositeCommitApproval, null);

  const readOnly = codeToolApprovalBlockFor('project_templates', request, []);
  assert.equal(readOnly, null);

  const unavailable = codeToolApprovalUnavailableBlock('git_worktree_manager', request);
  assert.equal(unavailable?.status, 'blocked');
  assert.match(unavailable?.summary ?? '', /could not be verified/);
});

test('project template scaffold requires approval and writes files inside cwd', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-template-'));
  t.after(async () => {
    await rm(cwd, { recursive: true, force: true });
  });

  const blocked = await executeCodeTool('project_template_scaffold', {
    cwd,
    arguments: {
      template_id: 'rust-cli',
      project_name: 'hello-rust'
    }
  });
  assert.equal(blocked?.status, 'blocked');
  await assert.rejects(() => stat(path.join(cwd, 'hello-rust')), /ENOENT/);

  const created = await executeCodeTool('project_template_scaffold', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      template_id: 'rust-cli',
      project_name: 'hello-rust'
    }
  });

  assert.equal(created?.status, 'completed');
  assert.equal(created?.requires_approval, true);
  assert.deepEqual((created?.data.created_files as string[]).sort(), ['Cargo.toml', 'src/main.rs']);
  assert.match(await readFile(path.join(cwd, 'hello-rust', 'Cargo.toml'), 'utf8'), /name = "hello-rust"/);
  assert.match(await readFile(path.join(cwd, 'hello-rust', 'src', 'main.rs'), 'utf8'), /Hello from hello-rust/);

  const escaped = await executeCodeTool('project_template_scaffold', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      template_id: 'rust-cli',
      target_path: '../escape'
    }
  });
  assert.equal(escaped?.status, 'failed');
  assert.match(escaped?.summary ?? '', /inside cwd/);
});

test('git worktree manager reports push readiness and blocks mutations without approval', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-tool-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });

  await initGitRepo(cwd);
  await writeFile(path.join(cwd, 'note.txt'), 'hello\n', 'utf8');

  const plan = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'push_plan' }
  });

  assert.equal(plan?.status, 'completed');
  assert.equal(plan?.requires_approval, true);
  assert.equal(plan?.data.has_uncommitted_changes, true);
  assert.equal(plan?.data.push_ready, false);
  assert.ok((plan?.data.push_blockers as string[]).includes('no upstream'));
  assert.ok((plan?.data.push_blockers as string[]).includes('uncommitted changes'));
  assert.ok((plan?.data.suggested_commands as string[]).includes('git status --short --branch'));
  const files = plan?.data.files as Array<{ path: string; status: string; is_untracked: boolean }>;
  assert.deepEqual(files.map((file) => file.path), ['note.txt']);
  assert.equal(files[0].status, 'untracked');
  assert.equal(files[0].is_untracked, true);

  const blocked = await executeCodeTool('git_worktree_manager', {
    cwd,
    arguments: { action: 'push' }
  });

  assert.equal(blocked?.status, 'blocked');
  assert.equal(blocked?.data.required_approval, true);
});

test('git worktree manager stages and unstages approved path selections', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-index-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });

  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await writeFile(path.join(cwd, 'tracked.txt'), 'one\n', 'utf8');
  await writeFile(path.join(cwd, 'removed.txt'), 'remove me\n', 'utf8');
  await runGit(cwd, ['add', '.']);
  await runGit(cwd, ['commit', '-m', 'initial']);
  await writeFile(path.join(cwd, 'tracked.txt'), 'one\ntwo\n', 'utf8');
  await rm(path.join(cwd, 'removed.txt'));

  const blockedStage = await executeCodeTool('git_worktree_manager', {
    cwd,
    arguments: { action: 'stage', paths: ['tracked.txt'] }
  });
  assert.equal(blockedStage?.status, 'blocked');
  assert.equal(blockedStage?.data.required_approval, true);

  const stageWithoutConfirmation = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'stage', paths: ['tracked.txt'] }
  });
  assert.equal(stageWithoutConfirmation?.status, 'completed');

  const escapedStage = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'stage', confirm_stage: true, paths: ['../escape.txt'] }
  });
  assert.equal(escapedStage?.status, 'failed');
  assert.match(escapedStage?.summary ?? '', /inside the workspace/);

  const staged = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'stage', confirm_stage: true, paths: ['tracked.txt', 'removed.txt'] }
  });
  assert.equal(staged?.status, 'completed');
  const stagedFiles = staged?.data.files as Array<{ path: string; staged_status: string; unstaged_status: string }>;
  assert.ok(stagedFiles.some((file) => file.path === 'tracked.txt' && file.staged_status === 'modified'));
  assert.ok(stagedFiles.some((file) => file.path === 'removed.txt' && file.staged_status === 'deleted'));

  const unstaged = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'unstage', confirm_unstage: true, paths: ['tracked.txt', 'removed.txt'] }
  });
  assert.equal(unstaged?.status, 'completed');
  const unstagedFiles = unstaged?.data.files as Array<{ path: string; staged_status: string; unstaged_status: string }>;
  assert.ok(unstagedFiles.some((file) => file.path === 'tracked.txt' && file.staged_status === 'clean' && file.unstaged_status === 'modified'));
  assert.ok(unstagedFiles.some((file) => file.path === 'removed.txt' && file.staged_status === 'clean' && file.unstaged_status === 'deleted'));
});

test('git commit tool and legacy manager execute approved commit and push with explicit confirmations', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-tool-'));
  const remote = await mkdtemp(path.join(tmpdir(), 'tinadec-git-remote-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
    await rm(remote, { recursive: true, force: true });
  });

  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await runGit(remote, ['init', '--bare']);
  await runGit(cwd, ['remote', 'add', 'origin', remote]);
  await writeFile(path.join(cwd, 'note.txt'), 'hello\n', 'utf8');

  const missingCommitConfirmation = await executeCodeTool('git_commit', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      paths: ['note.txt'],
      message: 'test commit'
    }
  });

  assert.equal(missingCommitConfirmation?.status, 'blocked');
  assert.equal(missingCommitConfirmation?.data.required_confirmation, 'confirm_commit');

  const escapedCommit = await executeCodeTool('git_commit', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      confirm_commit: true,
      paths: ['../escape.txt'],
      message: 'test commit'
    }
  });

  assert.equal(escapedCommit?.status, 'failed');
  assert.match(escapedCommit?.summary ?? '', /inside the workspace/);

  const committed = await executeCodeTool('git_commit', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      confirm_commit: true,
      paths: ['note.txt'],
      message: 'test commit'
    }
  });

  assert.equal(committed?.status, 'completed');
  assert.match(committed?.data.commit_hash as string, /^[a-f0-9]{40}$/);
  assert.deepEqual(committed?.data.staged_files, ['note.txt']);
  assert.equal(committed?.data.has_uncommitted_changes, false);

  await writeFile(path.join(cwd, 'legacy.txt'), 'legacy\n', 'utf8');
  const legacyCommit = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      action: 'commit',
      confirm_commit: true,
      paths: ['legacy.txt'],
      message: 'legacy commit'
    }
  });
  assert.equal(legacyCommit?.status, 'completed');
  assert.equal(legacyCommit?.tool_id, 'git_worktree_manager');
  assert.equal(legacyCommit?.data.action, 'commit');

  const missingPushConfirmation = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      action: 'push',
      set_upstream: true
    }
  });

  assert.equal(missingPushConfirmation?.status, 'blocked');
  assert.equal(missingPushConfirmation?.data.required_confirmation, 'confirm_push');

  const unsafeRemote = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      action: 'push',
      confirm_push: true,
      set_upstream: true,
      remote: '--force'
    }
  });

  assert.equal(unsafeRemote?.status, 'failed');
  assert.match(unsafeRemote?.summary ?? '', /remote/i);

  const pushed = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      action: 'push',
      confirm_push: true,
      set_upstream: true,
      remote: 'origin'
    }
  });

  assert.equal(pushed?.status, 'completed');
  assert.equal(pushed?.data.pushed, true);
  assert.equal(pushed?.data.set_upstream, true);
  assert.equal(pushed?.data.remote, 'origin');
  assert.equal(pushed?.data.ahead, 0);
  assert.equal(pushed?.data.behind, 0);

  const plan = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'push_plan' }
  });

  assert.equal(plan?.status, 'completed');
  assert.equal(plan?.data.push_ready, true);
  assert.equal(plan?.data.needs_push, false);
});

test('git worktree manager reports structured status and diff previews', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-diff-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });

  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await writeFile(path.join(cwd, 'tracked.txt'), 'one\ntwo\n', 'utf8');
  await writeFile(path.join(cwd, 'rename-before.txt'), 'rename me\n', 'utf8');
  await runGit(cwd, ['add', '.']);
  await runGit(cwd, ['commit', '-m', 'initial']);
  await runGit(cwd, ['branch', 'base']);
  await writeFile(path.join(cwd, 'branch.txt'), 'branch range\n', 'utf8');
  await runGit(cwd, ['add', 'branch.txt']);
  await runGit(cwd, ['commit', '-m', 'branch change']);

  await writeFile(path.join(cwd, 'tracked.txt'), 'one\ntwo\nthree\n', 'utf8');
  await runGit(cwd, ['mv', 'rename-before.txt', 'rename-after.txt']);
  await writeFile(path.join(cwd, 'staged.txt'), 'staged\n', 'utf8');
  await runGit(cwd, ['add', 'staged.txt', 'rename-after.txt']);
  await writeFile(path.join(cwd, 'untracked.txt'), 'new file\n', 'utf8');

  const status = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'status' }
  });

  assert.equal(status?.status, 'completed');
  const files = status?.data.files as Array<{ path: string; previous_path: string | null; status: string; is_renamed: boolean; is_untracked: boolean }>;
  assert.ok(files.some((file) => file.path === 'tracked.txt' && file.status === 'modified'));
  assert.ok(files.some((file) => file.path === 'staged.txt' && file.status === 'staged_added'));
  assert.ok(files.some((file) => file.path === 'rename-after.txt' && file.previous_path === 'rename-before.txt' && file.is_renamed));
  assert.ok(files.some((file) => file.path === 'untracked.txt' && file.is_untracked));

  const preview = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      action: 'diff_preview',
      base_ref: 'base',
      max_diff_bytes: 40_000
    }
  });

  assert.equal(preview?.status, 'completed');
  const sections = preview?.data.sections as Array<{
    id: string;
    kind: string;
    diff: string;
    files: Array<{ path: string; previous_path: string | null; change_type: string; additions: number; binary: boolean }>;
  }>;
  assert.deepEqual(sections.map((section) => section.id), ['working_tree', 'staged', 'branch_range']);

  const workingTree = sections.find((section) => section.id === 'working_tree');
  assert.ok(workingTree?.files.some((file) => file.path === 'tracked.txt' && file.additions === 1));

  const staged = sections.find((section) => section.id === 'staged');
  assert.ok(staged?.files.some((file) => file.path === 'staged.txt' && file.change_type === 'added'));
  assert.ok(staged?.files.some((file) => file.path === 'rename-after.txt' && file.previous_path === 'rename-before.txt' && file.change_type === 'renamed'));

  const branchRange = sections.find((section) => section.id === 'branch_range');
  assert.ok(branchRange?.files.some((file) => file.path === 'branch.txt' && file.change_type === 'added'));
});

test('git worktree manager diff preview reports missing branch range base as a notice', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-no-upstream-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });

  await initGitRepo(cwd);
  const preview = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'diff_preview', target: 'branch_range' }
  });

  assert.equal(preview?.status, 'completed');
  const sections = preview?.data.sections as Array<{ id: string; notices: string[] }>;
  assert.equal(sections[0].id, 'branch_range');
  assert.match(sections[0].notices[0], /No upstream/);
});

test('git worktree manager lists branches and tracks current branch', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-branches-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });

  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await writeFile(path.join(cwd, 'file.txt'), 'hello\n', 'utf8');
  await runGit(cwd, ['add', 'file.txt']);
  await runGit(cwd, ['commit', '-m', 'initial']);
  await runGit(cwd, ['checkout', '-b', 'feature/a']);
  await runGit(cwd, ['checkout', '-b', 'feature/b']);

  const list = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'branch_list', all: true }
  });

  assert.equal(list?.status, 'completed');
  const branches = list?.data.branches as Array<{ name: string; is_current: boolean; is_remote: boolean; commit_hash: string }>;
  assert.ok(branches.some((b) => b.name === 'main' && !b.is_current));
  assert.ok(branches.some((b) => b.name === 'feature/a' && !b.is_current));
  assert.ok(branches.some((b) => b.name === 'feature/b' && b.is_current));
  assert.ok(branches.every((b) => typeof b.commit_hash === 'string' && b.commit_hash.length > 0));
});

test('git worktree manager checks out and creates branches with approval', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-checkout-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });

  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await writeFile(path.join(cwd, 'file.txt'), 'hello\n', 'utf8');
  await runGit(cwd, ['add', 'file.txt']);
  await runGit(cwd, ['commit', '-m', 'initial']);
  await runGit(cwd, ['checkout', '-b', 'existing-branch']);
  await runGit(cwd, ['checkout', 'main']);

  const missingCheckoutConfirmation = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'checkout', branch: 'existing-branch' }
  });
  assert.equal(missingCheckoutConfirmation?.status, 'blocked');
  assert.equal(missingCheckoutConfirmation?.data.required_confirmation, 'confirm_checkout');

  const checkedOut = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'checkout', branch: 'existing-branch', confirm_checkout: true }
  });
  assert.equal(checkedOut?.status, 'completed');
  assert.equal(checkedOut?.data.branch, 'existing-branch');
  assert.equal(checkedOut?.data.checked_out, true);

  const createExisting = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'create_branch', branch: 'existing-branch', confirm_create_branch: true }
  });
  assert.equal(createExisting?.status, 'failed');
  assert.match(createExisting?.summary ?? '', /already exists/);

  const created = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'create_branch', branch: 'new-branch', confirm_create_branch: true }
  });
  assert.equal(created?.status, 'completed');
  assert.equal(created?.data.branch, 'new-branch');
  assert.equal(created?.data.created, true);

  const current = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'status' }
  });
  assert.equal(current?.data.branch, 'new-branch');

  const renamed = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'rename_branch', new_name: 'renamed-branch', confirm_rename_branch: true }
  });
  assert.equal(renamed?.status, 'completed');
  assert.equal(renamed?.data.branch, 'renamed-branch');

  await runGit(cwd, ['checkout', 'main']);
  const deleted = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'delete_branch', branch: 'renamed-branch', confirm_delete_branch: true }
  });
  assert.equal(deleted?.status, 'completed');
  assert.equal(deleted?.data.deleted, true);
  await assert.rejects(() => runGit(cwd, ['rev-parse', '--verify', 'renamed-branch']));
});

test('git worktree manager commits only staged files when commit_staged_only is true', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-staged-only-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });

  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await writeFile(path.join(cwd, 'staged.txt'), 'staged\n', 'utf8');
  await writeFile(path.join(cwd, 'unstaged.txt'), 'unstaged\n', 'utf8');
  await runGit(cwd, ['add', 'staged.txt']);

  const committed = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: {
      action: 'commit',
      confirm_commit: true,
      commit_staged_only: true,
      message: 'commit staged only'
    }
  });

  assert.equal(committed?.status, 'completed');
  assert.equal(committed?.data.commit_staged_only, true);
  const stagedFiles = committed?.data.staged_files as string[];
  assert.ok(stagedFiles.includes('staged.txt'));
  assert.ok(!stagedFiles.includes('unstaged.txt'));

  const status = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'status' }
  });
  const files = status?.data.files as Array<{ path: string; is_untracked: boolean }>;
  assert.ok(files.some((f) => f.path === 'unstaged.txt'));
  assert.ok(!files.some((f) => f.path === 'staged.txt'));
});

test('git worktree manager creates and removes managed worktrees through TinadecTools', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-worktree-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });
  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await writeFile(path.join(cwd, 'file.txt'), 'hello\n', 'utf8');
  await runGit(cwd, ['add', 'file.txt']);
  await runGit(cwd, ['commit', '-m', 'initial']);

  const relativePath = '.tinadec/worktrees/feature-wt';
  const created = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'create_worktree', branch: 'feature-wt', path: relativePath, confirm_create_worktree: true }
  });
  assert.equal(created?.status, 'completed');
  assert.equal(created?.data.created, true);
  await stat(path.join(cwd, relativePath, '.git'));

  const removed = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'remove_worktree', path: relativePath, confirm_remove_worktree: true }
  });
  assert.equal(removed?.status, 'completed');
  assert.equal(removed?.data.removed, true);
  await assert.rejects(() => stat(path.join(cwd, relativePath)), /ENOENT/);
});

test('git worktree manager runs merge and rebase start operations through TinadecTools', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-integrate-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });
  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await writeFile(path.join(cwd, 'base.txt'), 'base\n', 'utf8');
  await runGit(cwd, ['add', 'base.txt']);
  await runGit(cwd, ['commit', '-m', 'initial']);
  await runGit(cwd, ['checkout', '-b', 'feature']);
  await writeFile(path.join(cwd, 'feature.txt'), 'feature\n', 'utf8');
  await runGit(cwd, ['add', 'feature.txt']);
  await runGit(cwd, ['commit', '-m', 'feature']);
  await runGit(cwd, ['checkout', 'main']);

  const merged = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'merge', operation: 'start', branch: 'feature', strategy: 'no_ff', confirm_merge: true }
  });
  assert.equal(merged?.status, 'completed');

  await runGit(cwd, ['branch', 'topic']);
  await writeFile(path.join(cwd, 'main.txt'), 'main\n', 'utf8');
  await runGit(cwd, ['add', 'main.txt']);
  await runGit(cwd, ['commit', '-m', 'main advance']);
  await runGit(cwd, ['checkout', 'topic']);
  await writeFile(path.join(cwd, 'topic.txt'), 'topic\n', 'utf8');
  await runGit(cwd, ['add', 'topic.txt']);
  await runGit(cwd, ['commit', '-m', 'topic change']);

  const rebased = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'rebase', operation: 'start', branch: 'main', confirm_rebase: true }
  });
  assert.equal(rebased?.status, 'completed');
  assert.equal((rebased?.data.status as { branch?: string }).branch, 'topic');
});

test('git rebase lifecycle supports abort skip and non-interactive continue', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-rebase-life-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });
  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await writeFile(path.join(cwd, 'shared.txt'), 'base\n', 'utf8');
  await runGit(cwd, ['add', 'shared.txt']);
  await runGit(cwd, ['commit', '-m', 'initial']);
  await runGit(cwd, ['branch', 'topic-abort']);
  await runGit(cwd, ['branch', 'topic-skip']);
  await runGit(cwd, ['branch', 'topic-continue']);
  await runGit(cwd, ['checkout', '-b', 'upstream']);
  await writeFile(path.join(cwd, 'shared.txt'), 'upstream\n', 'utf8');
  await runGit(cwd, ['add', 'shared.txt']);
  await runGit(cwd, ['commit', '-m', 'upstream change']);

  async function conflictOn(branch: string) {
    await runGit(cwd, ['checkout', branch]);
    await writeFile(path.join(cwd, 'shared.txt'), `${branch}\n`, 'utf8');
    await runGit(cwd, ['add', 'shared.txt']);
    await runGit(cwd, ['commit', '-m', `${branch} change`]);
    const result = await executeCodeTool('git_rebase', {
      cwd,
      approval_id: 'approval-test',
      arguments: { operation: 'start', branch: 'upstream', confirm_rebase: true }
    });
    assert.equal(result?.status, 'failed');
    assert.equal(result?.data.conflict, true);
  }

  await conflictOn('topic-abort');
  const aborted = await executeCodeTool('git_rebase', { cwd, approval_id: 'approval-test', arguments: { operation: 'abort', confirm_rebase: true } });
  assert.equal(aborted?.status, 'completed');

  await conflictOn('topic-skip');
  const skipped = await executeCodeTool('git_rebase', { cwd, approval_id: 'approval-test', arguments: { operation: 'skip', confirm_rebase: true } });
  assert.equal(skipped?.status, 'completed');

  await conflictOn('topic-continue');
  await writeFile(path.join(cwd, 'shared.txt'), 'resolved\n', 'utf8');
  await runGit(cwd, ['add', 'shared.txt']);
  const continued = await executeCodeTool('git_rebase', { cwd, approval_id: 'approval-test', arguments: { operation: 'continue', confirm_rebase: true } });
  assert.equal(continued?.status, 'completed');
  assert.equal((await readFile(path.join(cwd, 'shared.txt'), 'utf8')).replace(/\r\n/g, '\n'), 'resolved\n');
});

test('git conflict preview and resolution share the three-way text merge engine', async (t) => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-conflict-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  });
  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await writeFile(path.join(cwd, 'shared.txt'), 'base\n', 'utf8');
  await runGit(cwd, ['add', 'shared.txt']);
  await runGit(cwd, ['commit', '-m', 'initial']);
  await runGit(cwd, ['checkout', '-b', 'feature']);
  await writeFile(path.join(cwd, 'shared.txt'), 'feature\n', 'utf8');
  await runGit(cwd, ['add', 'shared.txt']);
  await runGit(cwd, ['commit', '-m', 'feature change']);
  await runGit(cwd, ['checkout', 'main']);
  await writeFile(path.join(cwd, 'shared.txt'), 'main\n', 'utf8');
  await runGit(cwd, ['add', 'shared.txt']);
  await runGit(cwd, ['commit', '-m', 'main change']);
  await assert.rejects(() => runGit(cwd, ['merge', 'feature']));

  const preview = await executeCodeTool('git_conflict_preview', { cwd, approval_id: 'approval-test', arguments: { path: 'shared.txt' } });
  assert.equal(preview?.status, 'completed');
  const blocks = ((preview?.data.files as Array<{ blocks: Array<{ base: string; ours: string; theirs: string }> }>)[0]?.blocks ?? []);
  assert.deepEqual(blocks.map(block => ({ base: block.base.trim(), ours: block.ours.trim(), theirs: block.theirs.trim() })), [{ base: 'base', ours: 'main', theirs: 'feature' }]);

  const resolved = await executeCodeTool('git_conflict_resolve', {
    cwd,
    approval_id: 'approval-test',
    arguments: { path: 'shared.txt', strategy: 'both', confirm_resolve: true }
  });
  assert.equal(resolved?.status, 'completed');
  assert.equal(resolved?.data.all_resolved, true);
  assert.equal((await readFile(path.join(cwd, 'shared.txt'), 'utf8')).replace(/\r\n/g, '\n'), 'main\nfeature\n');
});

test('git worktree manager fetches from a remote and reports tracking info', async (t) => {
  const remote = await mkdtemp(path.join(tmpdir(), 'tinadec-git-remote-'));
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-git-fetch-'));
  const peer = await mkdtemp(path.join(tmpdir(), 'tinadec-git-peer-'));
  t.after(async () => {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
    await rm(remote, { recursive: true, force: true });
    await rm(peer, { recursive: true, force: true });
  });

  await runGit(remote, ['init', '--bare']);
  await initGitRepo(cwd);
  await runGit(cwd, ['config', 'user.name', 'Tinadec Test']);
  await runGit(cwd, ['config', 'user.email', 'tinadec@example.invalid']);
  await runGit(cwd, ['remote', 'add', 'origin', remote]);
  await writeFile(path.join(cwd, 'file.txt'), 'hello\n', 'utf8');
  await runGit(cwd, ['add', 'file.txt']);
  await runGit(cwd, ['commit', '-m', 'initial']);
  await runGit(cwd, ['push', '-u', 'origin', 'main']);

  await runGit(peer, ['clone', '-b', 'main', remote, '.']);
  await runGit(peer, ['config', 'user.name', 'Tinadec Peer']);
  await runGit(peer, ['config', 'user.email', 'peer@example.invalid']);
  await runGit(peer, ['config', 'commit.gpgSign', 'false']);
  await writeFile(path.join(peer, 'remote.txt'), 'remote\n', 'utf8');
  await runGit(peer, ['add', 'remote.txt']);
  await runGit(peer, ['commit', '-m', 'remote change']);
  await runGit(peer, ['push', 'origin', 'main']);

  await runGit(cwd, ['checkout', '-b', 'local-only']);

  const missingFetchConfirmation = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'fetch' }
  });
  assert.equal(missingFetchConfirmation?.status, 'blocked');
  assert.equal(missingFetchConfirmation?.data.required_confirmation, 'confirm_fetch');

  const fetched = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'fetch', confirm_fetch: true }
  });
  assert.equal(fetched?.status, 'completed');
  assert.equal(fetched?.data.fetched, true);
  assert.ok(Array.isArray(fetched?.data.branch_tracking_info));

  const list = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'branch_list', all: true }
  });
  const branches = list?.data.branches as Array<{ name: string; is_remote: boolean }>;
  assert.ok(branches.some((b) => b.name === 'remotes/origin/main' && b.is_remote));

  await runGit(cwd, ['checkout', 'main']);
  const pulled = await executeCodeTool('git_worktree_manager', {
    cwd,
    approval_id: 'approval-test',
    arguments: { action: 'pull', confirm_pull: true }
  });
  assert.equal(pulled?.status, 'completed');
  assert.equal((await readFile(path.join(cwd, 'remote.txt'), 'utf8')).replace(/\r\n/g, '\n'), 'remote\n');
});

async function initGitRepo(cwd: string): Promise<void> {
  await runGit(cwd, ['init']);
  await runGit(cwd, ['config', 'commit.gpgSign', 'false']);
  // Normalize default branch to 'main' regardless of git version/local config.
  try {
    await runGit(cwd, ['checkout', '-b', 'main']);
  } catch {
    // Already on 'main' on newer git; ignore.
  }
}

async function runGit(cwd: string, args: string[]): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const child = spawn('git', args, { cwd, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let stderr = '';
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', (code) => {
      if (code === 0) {
        resolve();
        return;
      }
      reject(new Error(stderr || `git ${args.join(' ')} failed with code ${code}`));
    });
  });
}
