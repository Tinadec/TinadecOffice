import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { coreEndpoint } from './coreClient.js';
import { executeCodeTool, listCodeToolIds, listCodeToolSpecs } from './codeTools.js';

test('coreEndpoint resolves API paths against the configured core URL', () => {
  assert.equal(coreEndpoint('/api/v1/health'), 'http://127.0.0.1:48731/api/v1/health');
});

test('Code tools expose programming-domain execution contracts', async () => {
  assert.deepEqual(listCodeToolIds().sort(), [
    'apply_patch',
    'bash_environment',
    'code_editor',
    'debug_session',
    'git_worktree_manager',
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

  const search = await executeCodeTool('search_files', { arguments: { query: 'AgentWorkflowRuntime' } });
  assert.equal(search?.requires_approval, false);
  assert.match(search?.status ?? '', /^(native|stubbed)$/);
  if (search?.status === 'native') {
    assert.equal(search.data.query, 'AgentWorkflowRuntime');
    assert.ok(Array.isArray(search.data.matches));
  } else {
    assert.deepEqual(search?.data.argument_keys, ['query']);
  }

  const patch = await executeCodeTool('apply_patch', { cwd: 'D:/github/TinadecCode' });
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
    await rm(cwd, { recursive: true, force: true });
  });

  await runGit(cwd, ['init']);
  await writeFile(path.join(cwd, 'note.txt'), 'hello\n', 'utf8');

  const plan = await executeCodeTool('git_worktree_manager', {
    cwd,
    arguments: { action: 'push_plan' }
  });

  assert.equal(plan?.status, 'completed');
  assert.equal(plan?.requires_approval, true);
  assert.equal(plan?.data.has_uncommitted_changes, true);
  assert.equal(plan?.data.push_ready, false);
  assert.ok((plan?.data.push_blockers as string[]).includes('no upstream'));
  assert.ok((plan?.data.push_blockers as string[]).includes('uncommitted changes'));
  assert.ok((plan?.data.suggested_commands as string[]).includes('git status --short --branch'));

  const blocked = await executeCodeTool('git_worktree_manager', {
    cwd,
    arguments: { action: 'push' }
  });

  assert.equal(blocked?.status, 'blocked');
  assert.equal(blocked?.data.required_approval, true);
});

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
