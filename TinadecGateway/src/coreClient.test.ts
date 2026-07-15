import assert from 'node:assert/strict';
import test from 'node:test';
import { coreEndpoint } from './coreClient.js';
import {
  codeToolApprovalBlockFor,
  codeToolApprovalUnavailableBlock,
  codeToolRequiresApproval,
  listCodeToolIds,
  listCodeToolSpecs,
  type ApprovalSnapshot,
  type CodeToolExecuteRequest,
} from './codeTools.js';
import {
  extractApprovalContext,
  evaluateApproval,
  buildConfirmationRequiredResponse,
} from './approval.js';

test('coreEndpoint resolves API paths against the configured core URL', () => {
  assert.equal(coreEndpoint('/api/v1/health'), 'http://127.0.0.1:48731/api/v1/health');
});

test('Code tools expose programming-domain execution contracts', () => {
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
});

test('Code tool approval gate trusts only approved Core approval state', () => {
  assert.equal(codeToolRequiresApproval('git_worktree_manager'), true);
  assert.equal(codeToolRequiresApproval('project_templates'), false);
  assert.equal(codeToolRequiresApproval('missing_tool'), null);

  const request: CodeToolExecuteRequest = {
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

test('approval interceptor allows human low-risk operations and blocks high-risk without confirmation', () => {
  // 人类低风险请求：直接通过
  const lowRiskCtx = extractApprovalContext({
    source: 'human',
    approval: true,
    tool_id: 'list_directory',
  });
  const lowRiskResult = evaluateApproval(lowRiskCtx);
  assert.equal(lowRiskResult.allowed, true);
  assert.equal(lowRiskResult.confirmationRequired, false);
  assert.equal(lowRiskResult.riskLevel, 'low');

  // 人类高风险请求：需要二次确认
  const highRiskCtx = extractApprovalContext({
    source: 'human',
    approval: true,
    tool_id: 'git_push',
  });
  const highRiskResult = evaluateApproval(highRiskCtx);
  assert.equal(highRiskResult.allowed, false);
  assert.equal(highRiskResult.confirmationRequired, true);
  assert.equal(highRiskResult.riskLevel, 'high');

  // 确认后的响应
  const confirmResponse = buildConfirmationRequiredResponse(highRiskCtx, highRiskResult);
  assert.equal(confirmResponse.status, 449);
  assert.equal(confirmResponse.data.code, 'CONFIRMATION_REQUIRED');

  // 人类高风险请求已确认：通过
  const confirmedCtx = extractApprovalContext({
    source: 'human',
    approval: true,
    confirmation: true,
    tool_id: 'git_push',
  });
  const confirmedResult = evaluateApproval(confirmedCtx);
  assert.equal(confirmedResult.allowed, true);
  assert.equal(confirmedResult.confirmationRequired, false);

  // Agent 请求：不拦截
  const agentCtx = extractApprovalContext({
    source: 'agent',
    tool_id: 'git_push',
  });
  const agentResult = evaluateApproval(agentCtx);
  assert.equal(agentResult.allowed, true);
  assert.equal(agentResult.confirmationRequired, false);

  // 人类请求但没有 approval=true：拒绝
  const noApprovalCtx = extractApprovalContext({
    source: 'human',
    tool_id: 'list_directory',
  });
  const noApprovalResult = evaluateApproval(noApprovalCtx);
  assert.equal(noApprovalResult.allowed, false);
  assert.equal(noApprovalResult.confirmationRequired, false);
});
