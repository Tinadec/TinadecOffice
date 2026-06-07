import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export interface CodeToolExecuteRequest {
  session_id?: string | null;
  run_id?: string | null;
  task_node_id?: string | null;
  approval_id?: string | null;
  cwd?: string | null;
  arguments?: Record<string, unknown> | null;
}

export interface CodeToolExecuteResult {
  tool_id: string;
  status: 'native' | 'completed' | 'stubbed' | 'blocked' | 'failed';
  summary: string;
  evidence: string[];
  data: Record<string, unknown>;
  requires_approval: boolean;
  approval_summary?: string | null;
}

type CodeToolCategory = 'project' | 'runtime' | 'environment' | 'debug' | 'editor' | 'git' | 'primitive' | 'review';

export interface CodeToolSpecDto {
  id: string;
  summary: string;
  category: CodeToolCategory;
  requires_approval: boolean;
  approval_summary?: string | null;
  language_support?: string[];
}

interface CodeToolSpec {
  id: string;
  summary: string;
  category: CodeToolCategory;
  requiresApproval: boolean;
  approvalSummary?: string;
  language_support?: string[];
  nativeBacked?: boolean;
}

interface ProjectTemplateFile {
  path: string;
  content: string;
}

interface ProjectTemplate {
  id: string;
  name: string;
  language: string;
  package_manager: string;
  description: string;
  files: (projectName: string) => ProjectTemplateFile[];
}

const CODE_LANGUAGE_SUPPORT = ['nodejs', 'bun', 'golang', 'flutter', 'python', 'rust', 'zig', 'nim', 'csharp', 'java'];

const PROJECT_TEMPLATES: ProjectTemplate[] = [
  {
    id: 'nodejs-vite-vue',
    name: 'Node.js Vite Vue',
    language: 'nodejs',
    package_manager: 'npm',
    description: 'Vite + Vue starter for renderer-style frontend work.',
    files: (projectName) => [
      { path: 'package.json', content: json({ name: projectName, version: '0.1.0', private: true, type: 'module', scripts: { dev: 'vite', build: 'vite build' }, dependencies: { '@vitejs/plugin-vue': '^6.0.0', vite: '^7.0.0', vue: '^3.5.0' }, devDependencies: { typescript: '^5.0.0' } }) },
      { path: 'index.html', content: '<div id="app"></div>\n<script type="module" src="/src/main.ts"></script>\n' },
      { path: 'src/main.ts', content: "import { createApp } from 'vue';\nimport App from './App.vue';\n\ncreateApp(App).mount('#app');\n" },
      { path: 'src/App.vue', content: `<template>\n  <main>\n    <h1>${projectName}</h1>\n  </main>\n</template>\n` }
    ]
  },
  {
    id: 'bun-elysia-api',
    name: 'Bun Elysia API',
    language: 'bun',
    package_manager: 'bun',
    description: 'Small Elysia API service for Bun.',
    files: (projectName) => [
      { path: 'package.json', content: json({ name: projectName, version: '0.1.0', private: true, type: 'module', scripts: { dev: 'bun run src/index.ts' }, dependencies: { elysia: '^1.0.0' }, devDependencies: { bun: 'latest' } }) },
      { path: 'src/index.ts', content: "import { Elysia } from 'elysia';\n\nconst app = new Elysia()\n  .get('/health', () => ({ ok: true }))\n  .listen(3000);\n\nconsole.log(`Listening on http://${app.server?.hostname}:${app.server?.port}`);\n" }
    ]
  },
  {
    id: 'golang-cli',
    name: 'Go CLI',
    language: 'golang',
    package_manager: 'go',
    description: 'Minimal Go command-line module.',
    files: (projectName) => [
      { path: 'go.mod', content: `module example.com/${projectName}\n\ngo 1.23\n` },
      { path: 'main.go', content: `package main\n\nimport "fmt"\n\nfunc main() {\n\tfmt.Println("Hello from ${projectName}")\n}\n` }
    ]
  },
  {
    id: 'flutter-app',
    name: 'Flutter App',
    language: 'flutter',
    package_manager: 'flutter',
    description: 'Tiny Flutter app skeleton.',
    files: (projectName) => [
      { path: 'pubspec.yaml', content: `name: ${identifierName(projectName)}\ndescription: A TinadecCode Flutter starter.\npublish_to: none\nversion: 0.1.0+1\nenvironment:\n  sdk: ">=3.5.0 <4.0.0"\ndependencies:\n  flutter:\n    sdk: flutter\n` },
      { path: 'lib/main.dart', content: `import 'package:flutter/material.dart';\n\nvoid main() => runApp(const App());\n\nclass App extends StatelessWidget {\n  const App({super.key});\n\n  @override\n  Widget build(BuildContext context) {\n    return const MaterialApp(home: Scaffold(body: Center(child: Text('Hello from ${projectName}'))));\n  }\n}\n` }
    ]
  },
  {
    id: 'python-package',
    name: 'Python Package',
    language: 'python',
    package_manager: 'uv',
    description: 'Python package layout with pyproject metadata.',
    files: (projectName) => {
      const moduleName = identifierName(projectName);
      return [
        { path: 'pyproject.toml', content: `[project]\nname = "${projectName}"\nversion = "0.1.0"\ndescription = "TinadecCode Python starter"\nrequires-python = ">=3.11"\n` },
        { path: `src/${moduleName}/__init__.py`, content: `__all__ = ["main"]\n` },
        { path: `src/${moduleName}/__main__.py`, content: `def main() -> None:\n    print("Hello from ${projectName}")\n\n\nif __name__ == "__main__":\n    main()\n` }
      ];
    }
  },
  {
    id: 'rust-cli',
    name: 'Rust CLI',
    language: 'rust',
    package_manager: 'cargo',
    description: 'Cargo binary crate with a simple main entrypoint.',
    files: (projectName) => [
      { path: 'Cargo.toml', content: `[package]\nname = "${projectName}"\nversion = "0.1.0"\nedition = "2024"\n\n[dependencies]\n` },
      { path: 'src/main.rs', content: `fn main() {\n    println!("Hello from ${projectName}");\n}\n` }
    ]
  },
  {
    id: 'zig-cli',
    name: 'Zig CLI',
    language: 'zig',
    package_manager: 'zig',
    description: 'Zig executable starter.',
    files: (projectName) => [
      { path: 'build.zig', content: `const std = @import("std");\n\npub fn build(b: *std.Build) void {\n    const exe = b.addExecutable(.{ .name = "${projectName}", .root_source_file = b.path("src/main.zig") });\n    b.installArtifact(exe);\n}\n` },
      { path: 'src/main.zig', content: `const std = @import("std");\n\npub fn main() !void {\n    try std.io.getStdOut().writer().print("Hello from ${projectName}\\n", .{});\n}\n` }
    ]
  },
  {
    id: 'nim-cli',
    name: 'Nim CLI',
    language: 'nim',
    package_manager: 'nimble',
    description: 'Nimble command-line starter.',
    files: (projectName) => [
      { path: `${projectName}.nimble`, content: `version       = "0.1.0"\nauthor        = "TinadecCode"\ndescription   = "TinadecCode Nim starter"\nlicense       = "MIT"\nsrcDir        = "src"\nbin           = @["${projectName}"]\n` },
      { path: `src/${projectName}.nim`, content: `echo "Hello from ${projectName}"\n` }
    ]
  },
  {
    id: 'csharp-worker',
    name: 'C# Worker',
    language: 'csharp',
    package_manager: 'dotnet',
    description: '.NET console worker starter.',
    files: (projectName) => [
      { path: `${projectName}.csproj`, content: `<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n` },
      { path: 'Program.cs', content: `Console.WriteLine("Hello from ${projectName}");\n` }
    ]
  },
  {
    id: 'java-gradle-app',
    name: 'Java Gradle App',
    language: 'java',
    package_manager: 'gradle',
    description: 'Gradle Java application starter.',
    files: (projectName) => [
      { path: 'settings.gradle', content: `rootProject.name = '${projectName}'\n` },
      { path: 'build.gradle', content: "plugins {\n    id 'application'\n}\n\nrepositories {\n    mavenCentral()\n}\n\napplication {\n    mainClass = 'app.Main'\n}\n" },
      { path: 'src/main/java/app/Main.java', content: `package app;\n\npublic final class Main {\n    public static void main(String[] args) {\n        System.out.println("Hello from ${projectName}");\n    }\n}\n` }
    ]
  }
];

const TOOL_SPECS: Record<string, CodeToolSpec> = {
  search_files: {
    id: 'search_files',
    summary: 'Fuzzy file-name search powered by Codex Rust codex-file-search (nucleo matcher). Returns ranked matches with scores.',
    category: 'primitive',
    requiresApproval: false,
    nativeBacked: true
  },
  glob_search: {
    id: 'glob_search',
    summary: 'Glob-pattern file search powered by Codex Rust ignore crate (WalkBuilder). Supports patterns like **/*.rs, src/**/*.ts.',
    category: 'primitive',
    requiresApproval: false,
    nativeBacked: true
  },
  read_file: {
    id: 'read_file',
    summary: 'Read file contents with optional line range. Returns content with line numbers. Detects binary files.',
    category: 'primitive',
    requiresApproval: false,
    nativeBacked: true
  },
  list_directory: {
    id: 'list_directory',
    summary: 'List directory entries with metadata (directories first, then files). Supports hidden file toggle.',
    category: 'primitive',
    requiresApproval: false,
    nativeBacked: true
  },
  grep_content: {
    id: 'grep_content',
    summary: 'Search file contents for a text pattern with optional glob filter, context lines, and case-insensitive mode.',
    category: 'primitive',
    requiresApproval: false,
    nativeBacked: true
  },
  sandbox_exec: {
    id: 'sandbox_exec',
    summary: 'Codex sandbox exec adapter. Execution is blocked until Core approval is supplied.',
    category: 'environment',
    requiresApproval: true,
    approvalSummary: 'Run a sandboxed command in the workspace.',
    nativeBacked: true
  },
  apply_patch: {
    id: 'apply_patch',
    summary: 'Codex apply_patch adapter. Workspace writes are blocked until Core approval is supplied.',
    category: 'editor',
    requiresApproval: true,
    approvalSummary: 'Apply a patch that may modify workspace files.',
    nativeBacked: true
  },
  review_format: {
    id: 'review_format',
    summary: 'Format code review findings as structured markdown with severity markers and summary.',
    category: 'review',
    requiresApproval: false,
    nativeBacked: true
  },
  project_templates: {
    id: 'project_templates',
    summary: 'List and preview built-in project templates for Node.js, Bun, Go, Flutter, Python, Rust, Zig, Nim, C#, and Java.',
    category: 'project',
    requiresApproval: false,
    language_support: CODE_LANGUAGE_SUPPORT
  },
  project_template_scaffold: {
    id: 'project_template_scaffold',
    summary: 'Create a project from a built-in Code template inside the approved workspace.',
    category: 'project',
    requiresApproval: true,
    approvalSummary: 'Write a new project scaffold into the workspace.',
    language_support: CODE_LANGUAGE_SUPPORT
  },
  language_runtime_probe: {
    id: 'language_runtime_probe',
    summary: 'Report built-in language/runtime support expected from the Code tool suite.',
    category: 'runtime',
    requiresApproval: false,
    language_support: CODE_LANGUAGE_SUPPORT
  },
  bash_environment: {
    id: 'bash_environment',
    summary: 'Bash-like command environment for workspace commands, environment variables, streams, and exit diagnostics.',
    category: 'environment',
    requiresApproval: true,
    approvalSummary: 'Run a workspace command through the Code bash-like environment.'
  },
  debug_session: {
    id: 'debug_session',
    summary: 'Built-in debug session surface for launch requests, breakpoints, logs, traces, and repro controls.',
    category: 'debug',
    requiresApproval: true,
    approvalSummary: 'Start or control a debug session in the workspace.'
  },
  code_editor: {
    id: 'code_editor',
    summary: 'Built-in code editor surface for opening files, diffing, patching, and code review operations.',
    category: 'editor',
    requiresApproval: true,
    approvalSummary: 'Modify workspace files through the built-in code editor.'
  },
  git_worktree_manager: {
    id: 'git_worktree_manager',
    summary: 'Git worktree manager for branches, isolated workspaces, diffs, commits, rebases, and conflicts.',
    category: 'git',
    requiresApproval: true,
    approvalSummary: 'Create or modify Git branches/worktrees.'
  }
};

export function listCodeToolIds(): string[] {
  return Object.keys(TOOL_SPECS);
}

export function listCodeToolSpecs(): CodeToolSpecDto[] {
  return Object.values(TOOL_SPECS).map((spec) => ({
    id: spec.id,
    summary: spec.summary,
    category: spec.category,
    requires_approval: spec.requiresApproval,
    approval_summary: spec.approvalSummary ?? null,
    language_support: spec.language_support
  }));
}

export async function executeCodeTool(toolId: string, request: CodeToolExecuteRequest = {}): Promise<CodeToolExecuteResult | null> {
  const spec = TOOL_SPECS[toolId];
  if (!spec) {
    return null;
  }

  if (spec.nativeBacked) {
    const nativeResult = await tryExecuteNativeTool(spec, request);
    if (nativeResult) {
      return nativeResult;
    }
  }

  const args = request.arguments ?? {};
  if (spec.id === 'project_templates') {
    return executeProjectTemplatesTool(spec, request, args);
  }
  if (spec.id === 'project_template_scaffold') {
    return executeProjectTemplateScaffold(spec, request, args);
  }
  if (spec.id === 'git_worktree_manager') {
    return executeGitWorktreeManager(spec, request, args);
  }

  return {
    tool_id: spec.id,
    status: spec.requiresApproval ? 'blocked' : 'stubbed',
    summary: spec.summary,
    evidence: [
      'domain: programming',
      'state_owner: core',
      'tool_layer: code',
      spec.nativeBacked ? 'native_runtime: pending' : 'code_suite: metadata'
    ],
    data: fallbackData(spec, request, args),
    requires_approval: spec.requiresApproval,
    approval_summary: spec.approvalSummary ?? null
  };
}

function executeProjectTemplatesTool(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>
): CodeToolExecuteResult {
  const action = stringArg(args, 'action') ?? 'list';
  if (action === 'list') {
    return resultFor(spec, 'completed', spec.summary, {
      cwd: request.cwd ?? null,
      argument_keys: Object.keys(args).sort(),
      templates: projectTemplateSummaries(),
      language_support: CODE_LANGUAGE_SUPPORT
    }, ['project_templates:list']);
  }

  if (action === 'preview') {
    const template = resolveProjectTemplate(args);
    if (!template) {
      return failedResult(spec, `Unknown project template '${stringArg(args, 'template_id') ?? ''}'.`, args);
    }

    const projectName = projectNameArg(args, template);
    return resultFor(spec, 'completed', `Previewed ${template.name}.`, {
      cwd: request.cwd ?? null,
      argument_keys: Object.keys(args).sort(),
      template: projectTemplateSummary(template),
      project_name: projectName,
      files: template.files(projectName)
    }, [`project_template:${template.id}`, 'project_templates:preview']);
  }

  return failedResult(spec, `Unsupported project_templates action '${action}'.`, args);
}

async function executeProjectTemplateScaffold(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>
): Promise<CodeToolExecuteResult> {
  if (!request.approval_id) {
    return resultFor(spec, 'blocked', spec.summary, {
      cwd: request.cwd ?? null,
      argument_keys: Object.keys(args).sort(),
      required_approval: true
    }, ['project_templates:scaffold', 'approval:required']);
  }

  if (!request.cwd) {
    return failedResult(spec, 'Project template scaffold requires a cwd.', args);
  }

  const template = resolveProjectTemplate(args);
  if (!template) {
    return failedResult(spec, `Unknown project template '${stringArg(args, 'template_id') ?? ''}'.`, args);
  }

  const projectName = projectNameArg(args, template);
  const target = resolveTargetInsideCwd(request.cwd, stringArg(args, 'target_path') ?? projectName);
  if (!target.ok) {
    return failedResult(spec, target.message, args, [`project_template:${template.id}`]);
  }

  const files = template.files(projectName);
  try {
    await ensurePathDoesNotExist(target.path);
    await writeTemplateFiles(target.path, files);
  } catch (error) {
    return failedResult(spec, error instanceof Error ? error.message : String(error), args, [`project_template:${template.id}`]);
  }

  return resultFor(spec, 'completed', `Created ${template.name} project at ${target.relative_path}.`, {
    cwd: request.cwd,
    argument_keys: Object.keys(args).sort(),
    template: projectTemplateSummary(template),
    project_name: projectName,
    target_path: target.relative_path,
    created_files: files.map((file) => file.path).sort()
  }, [`project_template:${template.id}`, 'project_templates:scaffold', 'approval:supplied']);
}

async function executeGitWorktreeManager(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>
): Promise<CodeToolExecuteResult> {
  const action = stringArg(args, 'action') ?? 'status';
  const mutatingActions = new Set(['commit', 'push', 'merge', 'rebase', 'create_branch', 'create_worktree', 'checkout']);
  if (mutatingActions.has(action) && !request.approval_id) {
    return resultFor(spec, 'blocked', `${action} requires a Core-approved Git tool invocation.`, {
      cwd: request.cwd ?? null,
      action,
      argument_keys: Object.keys(args).sort(),
      required_approval: true
    }, [`git:${action}`, 'approval:required']);
  }

  if (!request.cwd) {
    return failedResult(spec, 'Git worktree manager requires a cwd.', args);
  }

  const root = await git(request.cwd, ['rev-parse', '--show-toplevel']);
  if (root.code !== 0) {
    return failedResult(spec, 'cwd is not inside a Git repository.', args, ['git:rev-parse']);
  }

  if (mutatingActions.has(action)) {
    return resultFor(spec, 'blocked', `${action} is approved but not executed by the preview manager yet.`, {
      cwd: request.cwd,
      git_root: root.stdout.trim(),
      action,
      approval_id: request.approval_id,
      next_step: 'Dispatch a native Git mutation implementation after Core approval.'
    }, [`git:${action}`, 'approval:supplied', 'mutation:not-implemented']);
  }

  if (action !== 'status' && action !== 'push_plan' && action !== 'worktrees') {
    return failedResult(spec, `Unsupported git_worktree_manager action '${action}'.`, args, ['git:unsupported-action']);
  }

  const gitRoot = root.stdout.trim();
  const [status, diffStat, recentCommits, remotes, worktrees] = await Promise.all([
    git(gitRoot, ['status', '--porcelain=v1', '--branch']),
    git(gitRoot, ['diff', '--stat']),
    git(gitRoot, ['log', '--oneline', '--decorate', '-5']),
    git(gitRoot, ['remote', '-v']),
    git(gitRoot, ['worktree', 'list', '--porcelain'])
  ]);

  if (status.code !== 0) {
    return failedResult(spec, status.stderr || 'Unable to read Git status.', args, ['git:status']);
  }

  const statusSummary = parseGitStatus(status.stdout);
  const remoteLines = nonEmptyLines(remotes.stdout);
  const worktreeEntries = parseWorktrees(worktrees.stdout);
  const pushBlockers = pushReadinessBlockers(statusSummary);
  const pushReady = pushBlockers.length === 0;
  const summary = action === 'push_plan'
    ? pushReady
      ? `Push plan ready for ${statusSummary.branch}.`
      : `Push plan blocked: ${pushBlockers.join('; ')}.`
    : `Git status for ${statusSummary.branch}.`;

  return resultFor(spec, 'completed', summary, {
    cwd: request.cwd,
    git_root: gitRoot,
    action,
    branch: statusSummary.branch,
    upstream: statusSummary.upstream,
    ahead: statusSummary.ahead,
    behind: statusSummary.behind,
    has_uncommitted_changes: statusSummary.hasUncommittedChanges,
    changed_entries: statusSummary.changedEntries,
    diff_stat: diffStat.stdout.trim(),
    recent_commits: nonEmptyLines(recentCommits.stdout),
    remotes: remoteLines,
    worktrees: worktreeEntries,
    push_ready: pushReady,
    push_blockers: pushBlockers,
    needs_push: pushReady && statusSummary.ahead > 0,
    suggested_commands: suggestedGitCommands(action, statusSummary)
  }, [`git:${action}`, 'git:status', 'git:push-readiness']);
}

interface GitCommandResult {
  code: number;
  stdout: string;
  stderr: string;
}

interface GitStatusSummary {
  branch: string;
  upstream: string | null;
  ahead: number;
  behind: number;
  hasUncommittedChanges: boolean;
  changedEntries: string[];
}

async function git(cwd: string, args: string[]): Promise<GitCommandResult> {
  return new Promise((resolve) => {
    const child = spawn('git', args, {
      cwd,
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true
    });

    let stdout = '';
    let stderr = '';
    const timeout = setTimeout(() => {
      child.kill();
      resolve({ code: -1, stdout, stderr: stderr || 'git command timed out' });
    }, 10_000);

    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', (error) => {
      clearTimeout(timeout);
      resolve({ code: -1, stdout, stderr: error.message });
    });
    child.on('close', (code) => {
      clearTimeout(timeout);
      resolve({ code: code ?? 0, stdout, stderr });
    });
  });
}

function parseGitStatus(output: string): GitStatusSummary {
  const lines = output.split(/\r?\n/).filter((line) => line.length > 0);
  const branchLine = lines.find((line) => line.startsWith('## ')) ?? '## unknown';
  const branchText = branchLine.slice(3);
  const rawBranchPart = branchText.split('...')[0].split(' [')[0].trim();
  const branchPart = rawBranchPart.startsWith('No commits yet on ')
    ? rawBranchPart.slice('No commits yet on '.length)
    : rawBranchPart;
  const upstream = branchText.includes('...')
    ? branchText.split('...')[1].split(' [')[0].trim() || null
    : null;
  const ahead = Number(branchText.match(/\bahead (\d+)/)?.[1] ?? 0);
  const behind = Number(branchText.match(/\bbehind (\d+)/)?.[1] ?? 0);
  const changedEntries = lines.filter((line) => !line.startsWith('## '));

  return {
    branch: branchPart || 'unknown',
    upstream,
    ahead,
    behind,
    hasUncommittedChanges: changedEntries.length > 0,
    changedEntries
  };
}

function parseWorktrees(output: string): Array<Record<string, string | boolean>> {
  const entries: Array<Record<string, string | boolean>> = [];
  let current: Record<string, string | boolean> | null = null;

  for (const line of output.split(/\r?\n/)) {
    if (line.startsWith('worktree ')) {
      if (current) entries.push(current);
      current = { path: line.slice('worktree '.length) };
      continue;
    }

    if (!current || line.length === 0) continue;
    const [key, ...rest] = line.split(' ');
    current[key] = rest.length > 0 ? rest.join(' ') : true;
  }

  if (current) entries.push(current);
  return entries;
}

function pushReadinessBlockers(status: GitStatusSummary): string[] {
  const blockers: string[] = [];
  if (status.branch === 'HEAD' || status.branch.includes('detached')) {
    blockers.push('detached HEAD');
  }
  if (!status.upstream) {
    blockers.push('no upstream');
  }
  if (status.behind > 0) {
    blockers.push(`behind upstream by ${status.behind}`);
  }
  if (status.hasUncommittedChanges) {
    blockers.push('uncommitted changes');
  }
  return blockers;
}

function suggestedGitCommands(action: string, status: GitStatusSummary): string[] {
  if (action !== 'push_plan') {
    return ['git status --short --branch', 'git diff --stat'];
  }

  const commands = ['git status --short --branch', 'git diff --stat'];
  if (status.hasUncommittedChanges) {
    commands.push('git add <paths>', 'git commit -m "<message>"');
  }
  if (!status.upstream) {
    commands.push(`git push -u origin ${status.branch}`);
  } else if (status.ahead > 0) {
    commands.push('git push');
  }
  return commands;
}

function nonEmptyLines(output: string): string[] {
  return output.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
}

function resultFor(
  spec: CodeToolSpec,
  status: CodeToolExecuteResult['status'],
  summary: string,
  data: Record<string, unknown>,
  extraEvidence: string[] = []
): CodeToolExecuteResult {
  return {
    tool_id: spec.id,
    status,
    summary,
    evidence: [
      'domain: programming',
      'state_owner: core',
      'tool_layer: code',
      `code_suite: ${spec.category}`,
      ...extraEvidence
    ],
    data,
    requires_approval: spec.requiresApproval,
    approval_summary: spec.approvalSummary ?? null
  };
}

function failedResult(
  spec: CodeToolSpec,
  summary: string,
  args: Record<string, unknown>,
  extraEvidence: string[] = []
): CodeToolExecuteResult {
  return resultFor(spec, 'failed', summary, {
    argument_keys: Object.keys(args).sort()
  }, extraEvidence);
}

function projectTemplateSummaries(): Array<Omit<ProjectTemplate, 'files'>> {
  return PROJECT_TEMPLATES.map(projectTemplateSummary);
}

function projectTemplateSummary(template: ProjectTemplate): Omit<ProjectTemplate, 'files'> {
  return {
    id: template.id,
    name: template.name,
    language: template.language,
    package_manager: template.package_manager,
    description: template.description
  };
}

function resolveProjectTemplate(args: Record<string, unknown>): ProjectTemplate | null {
  const templateId = stringArg(args, 'template_id') ?? stringArg(args, 'id');
  if (!templateId) {
    return null;
  }

  return PROJECT_TEMPLATES.find((template) => template.id === templateId) ?? null;
}

function projectNameArg(args: Record<string, unknown>, template: ProjectTemplate): string {
  return slugProjectName(stringArg(args, 'project_name') ?? stringArg(args, 'name') ?? template.id);
}

function stringArg(args: Record<string, unknown>, key: string): string | null {
  const value = args[key];
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
}

function slugProjectName(value: string): string {
  const slug = value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, '-')
    .replace(/^-+|-+$/g, '');
  return slug || 'tinadec-project';
}

function identifierName(value: string): string {
  const identifier = value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^([0-9])/, '_$1')
    .replace(/^_+|_+$/g, '');
  return identifier || 'tinadec_project';
}

function json(value: unknown): string {
  return `${JSON.stringify(value, null, 2)}\n`;
}

function resolveTargetInsideCwd(
  cwd: string,
  targetPath: string
): { ok: true; path: string; relative_path: string } | { ok: false; message: string } {
  const root = path.resolve(cwd);
  const target = path.resolve(root, targetPath);
  if (!isInside(root, target)) {
    return { ok: false, message: 'Project template target_path must stay inside cwd.' };
  }

  const relativePath = path.relative(root, target) || '.';
  if (relativePath === '.') {
    return { ok: false, message: 'Project template target_path must name a child directory inside cwd.' };
  }

  return { ok: true, path: target, relative_path: relativePath };
}

function isInside(root: string, child: string): boolean {
  const relative = path.relative(root, child);
  return relative === '' || (relative.length > 0 && !relative.startsWith('..') && !path.isAbsolute(relative));
}

async function ensurePathDoesNotExist(target: string): Promise<void> {
  try {
    await stat(target);
  } catch (error) {
    if (isNodeError(error) && error.code === 'ENOENT') {
      return;
    }
    throw error;
  }

  throw new Error(`Project template target already exists: ${target}`);
}

async function writeTemplateFiles(targetRoot: string, files: ProjectTemplateFile[]): Promise<void> {
  await mkdir(targetRoot, { recursive: true });
  for (const file of files) {
    const destination = path.resolve(targetRoot, file.path);
    if (!isInside(targetRoot, destination)) {
      throw new Error(`Project template file path escapes target root: ${file.path}`);
    }
    await mkdir(path.dirname(destination), { recursive: true });
    await writeFile(destination, file.content, { encoding: 'utf8', flag: 'wx' });
  }
}

function isNodeError(error: unknown): error is NodeJS.ErrnoException {
  return error instanceof Error && 'code' in error;
}

function fallbackData(spec: CodeToolSpec, request: CodeToolExecuteRequest, args: Record<string, unknown>): Record<string, unknown> {
  if (spec.id === 'project_templates') {
    return {
      cwd: request.cwd ?? null,
      argument_keys: Object.keys(args).sort(),
      templates: projectTemplateSummaries(),
      language_support: CODE_LANGUAGE_SUPPORT
    };
  }

  if (spec.id === 'language_runtime_probe') {
    return {
      cwd: request.cwd ?? null,
      argument_keys: Object.keys(args).sort(),
      language_support: CODE_LANGUAGE_SUPPORT.map((id) => ({
        id,
        status: 'supported',
        provider: 'code-tool-suite'
      }))
    };
  }

  return {
    cwd: request.cwd ?? null,
    argument_keys: Object.keys(args).sort(),
    category: spec.category,
    language_support: spec.language_support ?? []
  };
}

async function tryExecuteNativeTool(spec: CodeToolSpec, request: CodeToolExecuteRequest): Promise<CodeToolExecuteResult | null> {
  const binary = resolveNativeBinary();
  if (!binary) {
    return null;
  }

  const payload = JSON.stringify({
    tool_id: spec.id,
    session_id: request.session_id ?? null,
    run_id: request.run_id ?? null,
    task_node_id: request.task_node_id ?? null,
    approval_id: request.approval_id ?? null,
    cwd: request.cwd ?? null,
    arguments: request.arguments ?? {}
  });

  return new Promise((resolve) => {
    const child = spawn(binary, ['execute'], {
      cwd: request.cwd ?? process.cwd(),
      env: {
        ...process.env,
        PATH: nativeRuntimePath()
      },
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true
    });

    let stdout = '';
    let stderr = '';
    const timeout = setTimeout(() => {
      child.kill();
      resolve(null);
    }, 15_000);

    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', () => {
      clearTimeout(timeout);
      resolve(null);
    });
    child.on('close', (code) => {
      clearTimeout(timeout);
      if (code !== 0 || stdout.trim().length === 0) {
        if (stderr.trim().length > 0) {
          console.warn(`tinadec-code-native failed: ${stderr.trim()}`);
        }
        resolve(null);
        return;
      }

      try {
        resolve(JSON.parse(stdout) as CodeToolExecuteResult);
      } catch {
        resolve(null);
      }
    });
    child.stdin.end(payload);
  });
}

function nativeRuntimePath(): string {
  const separator = process.platform === 'win32' ? ';' : ':';
  const here = path.dirname(fileURLToPath(import.meta.url));
  const repoRoot = path.resolve(here, '..', '..', '..');
  const runtimeDirs: string[] = [];

  const cargoHome = process.env.CARGO_HOME || process.env.RUSTUP_HOME;
  if (cargoHome) {
    runtimeDirs.push(path.join(cargoHome, 'bin'));
  }

  runtimeDirs.push(
    path.join(repoRoot, 'native', 'target', 'debug'),
    path.join(repoRoot, 'native', 'target', 'release')
  );

  return [...runtimeDirs, process.env.PATH ?? ''].join(separator);
}

function resolveNativeBinary(): string | null {
  const explicit = process.env.TINADEC_CODE_NATIVE_BIN;
  if (explicit && existsSync(explicit)) {
    return explicit;
  }

  const here = path.dirname(fileURLToPath(import.meta.url));
  const repoRoot = path.resolve(here, '..', '..', '..');
  const exe = process.platform === 'win32' ? 'tinadec-code-native.exe' : 'tinadec-code-native';
  const candidates = [
    path.join(repoRoot, 'native', 'target', 'debug', exe),
    path.join(repoRoot, 'native', 'target', 'release', exe)
  ];

  return candidates.find((candidate) => existsSync(candidate)) ?? null;
}
