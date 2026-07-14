import { spawn } from 'node:child_process';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { callToolLayer } from './toolLayerBridge.js';

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
  status: 'completed' | 'stubbed' | 'blocked' | 'failed';
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

export interface ApprovalSnapshot {
  id: string;
  session_id?: string | null;
  kind?: string | null;
  status?: string | null;
  command?: string | null;
  cwd?: string | null;
}

interface CodeToolSpec {
  id: string;
  summary: string;
  category: CodeToolCategory;
  requiresApproval: boolean;
  approvalSummary?: string;
  language_support?: string[];
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
      { path: 'pubspec.yaml', content: `name: ${identifierName(projectName)}\ndescription: A TinadecOffice Flutter starter.\npublish_to: none\nversion: 0.1.0+1\nenvironment:\n  sdk: ">=3.5.0 <4.0.0"\ndependencies:\n  flutter:\n    sdk: flutter\n` },
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
        { path: 'pyproject.toml', content: `[project]\nname = "${projectName}"\nversion = "0.1.0"\ndescription = "TinadecOffice Python starter"\nrequires-python = ">=3.11"\n` },
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
      { path: `${projectName}.nimble`, content: `version       = "0.1.0"\nauthor        = "TinadecOffice"\ndescription   = "TinadecOffice Nim starter"\nlicense       = "MIT"\nsrcDir        = "src"\nbin           = @["${projectName}"]\n` },
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
    summary: 'Search workspace text through TinadecTools ripgrep and return matching lines.',
    category: 'primitive',
    requiresApproval: false
  },
  glob_search: {
    id: 'glob_search',
    summary: 'Search files matching a glob through TinadecTools ripgrep. Supports patterns like **/*.rs and src/**/*.ts.',
    category: 'primitive',
    requiresApproval: false
  },
  read_file: {
    id: 'read_file',
    summary: 'Read file contents with optional line range. Returns content with line numbers. Detects binary files.',
    category: 'primitive',
    requiresApproval: false
  },
  list_directory: {
    id: 'list_directory',
    summary: 'List directory entries with metadata (directories first, then files). Supports hidden file toggle.',
    category: 'primitive',
    requiresApproval: false
  },
  grep_content: {
    id: 'grep_content',
    summary: 'Search file contents for a text pattern with optional glob filter, context lines, and case-insensitive mode.',
    category: 'primitive',
    requiresApproval: false
  },
  sandbox_exec: {
    id: 'sandbox_exec',
    summary: 'Codex sandbox exec adapter. Execution is blocked until Core approval is supplied.',
    category: 'environment',
    requiresApproval: true,
    approvalSummary: 'Run a sandboxed command in the workspace.'
  },
  apply_patch: {
    id: 'apply_patch',
    summary: 'Codex apply_patch adapter. Workspace writes are blocked until Core approval is supplied.',
    category: 'editor',
    requiresApproval: true,
    approvalSummary: 'Apply a patch that may modify workspace files.'
  },
  review_format: {
    id: 'review_format',
    summary: 'Format code review findings as structured markdown with severity markers and summary.',
    category: 'review',
    requiresApproval: false
  },
  // TODO: DEFERRED - 待 Tool layer 补 scaffold 后迁移
  project_templates: {
    id: 'project_templates',
    summary: 'List and preview built-in project templates for Node.js, Bun, Go, Flutter, Python, Rust, Zig, Nim, C#, and Java.',
    category: 'project',
    requiresApproval: false,
    language_support: CODE_LANGUAGE_SUPPORT
  },
  // TODO: DEFERRED - 待 Tool layer 补 scaffold 后迁移
  project_template_scaffold: {
    id: 'project_template_scaffold',
    summary: 'Create a project from a built-in Code template inside the approved workspace.',
    category: 'project',
    requiresApproval: true,
    approvalSummary: 'Write a new project scaffold into the workspace.',
    language_support: CODE_LANGUAGE_SUPPORT
  },
  // TODO: DEFERRED - 待 Tool layer 定义 runtime probe 契约
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
  // TODO: DEFERRED - 待 Tool layer 定义 debug session 契约
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
  // TODO: DEFERRED - 待 Tool layer 补 git 能力后迁移
  git_worktree_manager: {
    id: 'git_worktree_manager',
    summary: 'Git worktree manager for branches, isolated workspaces, diffs, commits, rebases, and conflicts.',
    category: 'git',
    requiresApproval: true,
    approvalSummary: 'Create or modify Git branches/worktrees.'
  },
  git_stage: { id: 'git_stage', summary: 'Stage complete files or selected text hunks into the Git index through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Update the Git index with approved file or line selections.' },
  git_unstage: { id: 'git_unstage', summary: 'Remove complete files or selected text hunks from the Git index through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Update the Git index with approved file or line selections.' },
  git_commit: { id: 'git_commit', summary: 'Create an approved Git commit through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Create a Git commit from approved staged, complete, or selected paths.' },
  git_checkout: { id: 'git_checkout', summary: 'Checkout an approved Git branch through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Checkout a Git branch.' },
  git_branch_create: { id: 'git_branch_create', summary: 'Create and checkout an approved Git branch through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Create and checkout a Git branch.' },
  git_branch_delete: { id: 'git_branch_delete', summary: 'Delete an approved Git branch through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Delete a Git branch.' },
  git_branch_rename: { id: 'git_branch_rename', summary: 'Rename the current Git branch through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Rename the current Git branch.' },
  git_worktree_create: { id: 'git_worktree_create', summary: 'Create an approved managed Git worktree through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Create a managed Git worktree.' },
  git_worktree_remove: { id: 'git_worktree_remove', summary: 'Remove an approved managed Git worktree through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Remove a managed Git worktree.' },
  git_fetch: { id: 'git_fetch', summary: 'Fetch approved remote updates through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Fetch Git remote updates.' },
  git_push: { id: 'git_push', summary: 'Push approved commits through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Push Git commits.' },
  git_pull: { id: 'git_pull', summary: 'Fast-forward an approved branch through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Fast-forward from a Git remote.' },
  git_merge: { id: 'git_merge', summary: 'Run an approved Git merge lifecycle operation through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Start, continue, or abort a Git merge.' },
  git_rebase: { id: 'git_rebase', summary: 'Run an approved Git rebase lifecycle operation through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Start, continue, abort, or skip a Git rebase.' },
  git_conflict_resolve: { id: 'git_conflict_resolve', summary: 'Resolve an approved text conflict through the shared three-way merge engine.', category: 'git', requiresApproval: true, approvalSummary: 'Resolve and stage a Git conflict.' },
  git_status: { id: 'git_status', summary: 'Inspect repository status, conflicts, upstream, and ahead/behind state through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git repository status.' },
  git_log_list: { id: 'git_log_list', summary: 'List Git commits through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git commit history.' },
  git_log_detail: { id: 'git_log_detail', summary: 'Read Git commit or range details through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git commit details.' },
  git_file_history: { id: 'git_file_history', summary: 'Read Git file history through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git file history.' },
  git_push_readiness: { id: 'git_push_readiness', summary: 'Derive push readiness and blockers through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git push readiness.' },
  git_diff: { id: 'git_diff', summary: 'Read working tree, staged, or ref-range diffs through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git diff output.' },
  git_branch_list: { id: 'git_branch_list', summary: 'List local and remote branches through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git branch metadata.' },
  git_worktree_list: { id: 'git_worktree_list', summary: 'List linked worktrees through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git worktree metadata.' },
  git_ref_list: { id: 'git_ref_list', summary: 'List branches, tags, and remote refs through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git refs.' },
  git_remote_list: { id: 'git_remote_list', summary: 'List configured remotes through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git remotes.' },
  git_blame: { id: 'git_blame', summary: 'Read line attribution through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git blame metadata.' },
  git_file_at_revision: { id: 'git_file_at_revision', summary: 'Read a repository file at a revision through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git revision file content.' },
  git_conflict_preview: { id: 'git_conflict_preview', summary: 'Read unresolved merge or rebase conflict blocks through TinadecTools.', category: 'git', requiresApproval: true, approvalSummary: 'Read Git conflict stages.' }
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

export function codeToolRequiresApproval(toolId: string): boolean | null {
  return TOOL_SPECS[toolId]?.requiresApproval ?? null;
}

export function codeToolApprovalBlockFor(
  toolId: string,
  request: CodeToolExecuteRequest,
  approvals: ApprovalSnapshot[]
): CodeToolExecuteResult | null {
  const spec = TOOL_SPECS[toolId];
  if (!spec || !spec.requiresApproval || !request.approval_id) {
    return null;
  }

  const approval = approvals.find((item) => item.id === request.approval_id);
  if (!approval) {
    return resultFor(spec, 'blocked', 'Core approval was not found for this Code tool invocation.', {
      approval_id: request.approval_id,
      session_id: request.session_id ?? null,
      required_approval: true
    }, ['approval:not-found', 'state_owner: core']);
  }

  if (approval.status !== 'approved') {
    return resultFor(spec, 'blocked', `Core approval is ${approval.status ?? 'unknown'}, not approved.`, {
      approval_id: request.approval_id,
      approval_status: approval.status ?? null,
      approval_kind: approval.kind ?? null,
      session_id: request.session_id ?? null,
      required_approval: true
    }, ['approval:not-approved', 'state_owner: core']);
  }

  const approvalKind = approval.kind ?? null;
  const allowedKinds = allowedApprovalKindsForTool(toolId);
  if (!approvalKind || !allowedKinds.includes(approvalKind)) {
    return resultFor(spec, 'blocked', `Core approval kind '${approvalKind ?? 'unknown'}' cannot authorize ${toolId}.`, {
      approval_id: request.approval_id,
      approval_status: approval.status,
      approval_kind: approvalKind,
      allowed_approval_kinds: allowedKinds,
      session_id: request.session_id ?? null,
      required_approval: true
    }, ['approval:wrong-kind', 'state_owner: core']);
  }

  if (approval.cwd && request.cwd && normalizePath(approval.cwd) !== normalizePath(request.cwd)) {
    return resultFor(spec, 'blocked', 'Core approval was granted for a different working directory.', {
      approval_id: request.approval_id,
      approval_cwd: approval.cwd,
      request_cwd: request.cwd,
      session_id: request.session_id ?? null,
      required_approval: true
    }, ['approval:context-mismatch', 'state_owner: core']);
  }

  const requestedAction = gitApprovalAction(toolId, request.arguments);
  if (requestedAction && approval.command) {
    if (!approvalCommandMatchesGitAction(approval.command, requestedAction)) {
      return resultFor(spec, 'blocked', 'Core approval command does not match the requested Git action.', {
        approval_id: request.approval_id,
        approval_command: approval.command,
        requested_action: requestedAction,
        session_id: request.session_id ?? null,
        required_approval: true
      }, ['approval:context-mismatch', 'state_owner: core']);
    }
  }

  return null;
}

function normalizePath(p: string): string {
  return path.resolve(p).toLowerCase();
}

function extractActionFromApprovalCommand(command: string): string | null {
  const normalized = command.trim().toLowerCase();
  if (!normalized.startsWith('git ')) {
    return null;
  }
  const parts = normalized.slice('git '.length).split(/\s+/);
  const base = parts[0] ?? '';
  if (base === 'checkout' && parts.some(part => part === '--ours' || part === '--theirs' || part === '--both')) return 'resolve_conflict';
  if (base === 'checkout' && parts.includes('-b')) {
    return 'create_branch';
  }
  if (base === 'checkout') {
    return 'checkout';
  }
  if (base === 'branch' && parts.includes('-d')) {
    return 'delete_branch';
  }
  if (base === 'branch' && parts.includes('-m')) {
    return 'rename_branch';
  }
  if (base === 'worktree' && parts[1] === 'add') return 'create_worktree';
  if (base === 'worktree' && parts[1] === 'remove') return 'remove_worktree';
  return base || null;
}

function gitApprovalAction(toolId: string, args?: Record<string, unknown> | null): string | null {
  if (toolId === 'git_worktree_manager') return args ? stringArg(args, 'action') ?? null : null;
  if (toolId === 'git_commit') return 'commit';
  if (toolId === 'git_checkout') return 'checkout';
  if (toolId === 'git_branch_create') return 'create_branch';
  if (toolId === 'git_branch_delete') return 'delete_branch';
  if (toolId === 'git_branch_rename') return 'rename_branch';
  if (toolId === 'git_worktree_create') return 'create_worktree';
  if (toolId === 'git_worktree_remove') return 'remove_worktree';
  if (toolId === 'git_fetch') return 'fetch';
  if (toolId === 'git_push') return 'push';
  if (toolId === 'git_pull') return 'pull';
  if (toolId === 'git_merge') return 'merge';
  if (toolId === 'git_rebase') return 'rebase';
  if (toolId === 'git_conflict_resolve') return 'resolve_conflict';
  return null;
}

function approvalCommandMatchesGitAction(command: string, action: string): boolean {
  if (action === 'commit') {
    return /(?:^|(?:&&|\|\||;)\s*)git\s+commit(?:\s|$)/i.test(command.trim());
  }
  const commandAction = extractActionFromApprovalCommand(command);
  return !commandAction || commandAction === action;
}

export function codeToolApprovalUnavailableBlock(toolId: string, request: CodeToolExecuteRequest): CodeToolExecuteResult | null {
  const spec = TOOL_SPECS[toolId];
  if (!spec || !spec.requiresApproval || !request.approval_id) {
    return null;
  }

  return resultFor(spec, 'blocked', 'Core approval state could not be verified for this Code tool invocation.', {
    approval_id: request.approval_id,
    session_id: request.session_id ?? null,
    required_approval: true
  }, ['approval:unverified', 'state_owner: core']);
}

function allowedApprovalKindsForTool(toolId: string): string[] {
  if (toolId === 'git_worktree_manager' || toolId.startsWith('git_')) {
    return ['git'];
  }

  return ['code', 'tool', toolId];
}

interface ToolLayerDirectoryEntry {
  name: string;
  path: string;
  type: 'directory' | 'file' | 'link';
  size: number;
  modified_at: string;
  is_readonly: boolean;
  is_hidden: boolean;
  link_target: string | null;
}

interface ToolLayerListDirectoryResult {
  success: boolean;
  error: string | null;
  entries: ToolLayerDirectoryEntry[];
  has_more: boolean;
  next_cursor: string | null;
}

interface ToolLayerFileSearchResult {
  success: boolean;
  error: string | null;
  lines: unknown[];
  file_hashes: Record<string, string>;
  truncated: boolean;
  total_match_count: number;
}

async function executeListDirectoryViaToolLayer(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest
): Promise<CodeToolExecuteResult> {
  const args = request.arguments ?? {};
  const requestedPath = stringArg(args, 'path') ?? '.';
  const showHidden = args['show_hidden'] === true;
  const cwd = request.cwd;
  if (!cwd) {
    return failedResult(spec, 'list_directory requires a cwd (workspace root).', args, ['list_directory:missing-cwd']);
  }

  try {
    const entries: ToolLayerDirectoryEntry[] = [];
    let cursor: string | null = null;
    let hasMore = true;
    while (hasMore && entries.length < 2_000) {
      const result = await callToolLayer(cwd, 'ls', {
        path: requestedPath,
        limit: 500,
        cursor
      }, { sessionId: request.session_id });
      if (!isToolLayerListDirectoryResult(result)) {
        throw new Error('TinadecTools returned an invalid ls result.');
      }
      if (!result.success) {
        return failedResult(spec, result.error ?? 'TinadecTools ls failed.', args, ['list_directory:tool-layer-rejected']);
      }
      entries.push(...result.entries);
      cursor = result.next_cursor;
      hasMore = result.has_more;
      if (hasMore && !cursor) throw new Error('TinadecTools ls returned has_more without next_cursor.');
    }

    const visible = entries
      .filter((entry) => showHidden || !entry.is_hidden)
      .sort((left, right) => {
        const leftDirectory = left.type === 'directory';
        const rightDirectory = right.type === 'directory';
        if (leftDirectory !== rightDirectory) return leftDirectory ? -1 : 1;
        return left.name.localeCompare(right.name);
      })
      .map((entry) => ({ ...entry, is_directory: entry.type === 'directory' }));
    const truncated = hasMore;
    return resultFor(spec, 'completed', `Listed ${visible.length} entries in ${requestedPath}.`, {
      cwd: path.resolve(cwd),
      path: requestedPath,
      show_hidden: showHidden,
      entries: visible,
      total_count: visible.length,
      truncated
    }, ['list_directory:tool-layer', 'tool_id:ls']);
  } catch (error) {
    return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['list_directory:tool-layer-failed']);
  }
}

function isToolLayerListDirectoryResult(value: unknown): value is ToolLayerListDirectoryResult {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<ToolLayerListDirectoryResult>;
  return typeof candidate.success === 'boolean' && Array.isArray(candidate.entries);
}

async function executeFileSearchViaToolLayer(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest
): Promise<CodeToolExecuteResult> {
  const args = request.arguments ?? {};
  const cwd = request.cwd;
  if (!cwd) {
    return failedResult(spec, spec.id + ' requires a cwd (workspace root).', args, [spec.id + ':missing-cwd']);
  }

  const params = fileSearchParams(spec.id, args);
  if (!params.ok) {
    return failedResult(spec, params.message, args, [spec.id + ':invalid-arguments']);
  }

  try {
    const result = await callToolLayer(cwd, 'file_search', params.value, { sessionId: request.session_id });
    if (!isToolLayerFileSearchResult(result)) {
      throw new Error('TinadecTools returned an invalid file_search result.');
    }
    if (!result.success) {
      return failedResult(spec, result.error ?? 'TinadecTools file_search failed.', args, [spec.id + ':tool-layer-rejected']);
    }

    return resultFor(spec, 'completed', 'Found ' + result.total_match_count + ' matching lines.', {
      cwd: path.resolve(cwd),
      ...result
    }, [spec.id + ':tool-layer', 'tool_id:file_search']);
  } catch (error) {
    return failedResult(spec, error instanceof Error ? error.message : String(error), args, [spec.id + ':tool-layer-failed']);
  }
}

function fileSearchParams(
  toolId: string,
  args: Record<string, unknown>
): { ok: true; value: Record<string, unknown> } | { ok: false; message: string } {
  const common = {
    path: stringArg(args, 'path') ?? '.',
    type: stringArg(args, 'type') ?? undefined,
    case_sensitive: booleanArg(args, 'case_sensitive'),
    context_lines: clampNumber(numberArg(args, 'context_lines') ?? 0, 0, 100),
    max_results: clampNumber(numberArg(args, 'max_results') ?? numberArg(args, 'limit') ?? 50, 1, 1_000)
  };

  if (toolId === 'search_files') {
    const pattern = stringArg(args, 'query') ?? stringArg(args, 'pattern');
    if (!pattern) return { ok: false, message: 'search_files requires a query.' };
    return {
      ok: true,
      value: {
        ...common,
        pattern,
        fixed_strings: booleanArg(args, 'fixed_strings', true)
      }
    };
  }

  const glob = stringArg(args, 'pattern') ?? stringArg(args, 'glob');
  if (!glob) return { ok: false, message: 'glob_search requires a glob pattern.' };
  return {
    ok: true,
    value: {
      ...common,
      pattern: stringArg(args, 'query') ?? stringArg(args, 'content_pattern') ?? '.',
      glob,
      fixed_strings: booleanArg(args, 'fixed_strings')
    }
  };
}

function isToolLayerFileSearchResult(value: unknown): value is ToolLayerFileSearchResult {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<ToolLayerFileSearchResult>;
  return typeof candidate.success === 'boolean'
    && Array.isArray(candidate.lines)
    && typeof candidate.truncated === 'boolean'
    && typeof candidate.total_match_count === 'number';
}

export async function executeCodeTool(toolId: string, request: CodeToolExecuteRequest = {}): Promise<CodeToolExecuteResult | null> {
  const spec = TOOL_SPECS[toolId];
  if (!spec) {
    return null;
  }

  if (spec.id === 'list_directory') {
    return executeListDirectoryViaToolLayer(spec, request);
  }
  if (spec.id === 'search_files' || spec.id === 'glob_search') {
    return executeFileSearchViaToolLayer(spec, request);
  }

  const args = request.arguments ?? {};
  if (spec.id === 'git_stage' || spec.id === 'git_unstage') {
    return executeGitIndexViaToolLayer(spec, request, args);
  }
  if (spec.id === 'git_commit') {
    return executeGitCommitViaToolLayer(spec, request, args);
  }
  if (spec.id === 'git_checkout' || spec.id.startsWith('git_branch_')) {
    return executeGitBranchViaToolLayer(spec, request, args);
  }
  if (spec.id === 'git_worktree_create' || spec.id === 'git_worktree_remove') {
    return executeGitWorktreeMutationViaToolLayer(spec, request, args);
  }
  if (spec.id === 'git_fetch' || spec.id === 'git_push' || spec.id === 'git_pull') {
    return executeGitRemoteMutationViaToolLayer(spec, request, args);
  }
  if (spec.id === 'git_merge' || spec.id === 'git_rebase') {
    return executeGitIntegrationViaToolLayer(spec, request, args);
  }
  if (spec.id === 'git_conflict_resolve') {
    return executeGitConflictResolveViaToolLayer(spec, request, args);
  }
  if (spec.id.startsWith('git_') && spec.id !== 'git_worktree_manager') {
    return executeGitReadViaToolLayer(spec, request, args);
  }
  if (spec.id === 'project_templates') {
    return executeProjectTemplatesTool(spec, request, args);
  }
  if (spec.id === 'project_template_scaffold') {
    return executeProjectTemplateScaffold(spec, request, args);
  }
  if (spec.id === 'git_worktree_manager') {
    return executeGitWorktreeManager(spec, request, args);
  }
  if (spec.id === 'code_editor') {
    return executeCodeEditor(spec, request, args);
  }

  return {
    tool_id: spec.id,
    status: spec.requiresApproval ? 'blocked' : 'stubbed',
    summary: spec.summary,
    evidence: [
      'domain: programming',
      'state_owner: core',
      'tool_layer: code',
      'code_suite: metadata'
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

/**
 * code_editor tool: open / save / diff / patch actions for workspace files.
 * - open: read file content (no approval needed)
 * - save: write file content (approval required)
 * - diff: get working-tree diff for a file (no approval needed)
 * - patch: apply a unified diff patch (approval required)
 */
async function executeCodeEditor(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>
): Promise<CodeToolExecuteResult> {
  const action = stringArg(args, 'action') ?? 'open';
  const filePath = stringArg(args, 'path');

  if (!filePath) {
    return failedResult(spec, 'code_editor requires a "path" argument.', args, ['code_editor:missing-path']);
  }

  if (!request.cwd) {
    return failedResult(spec, 'code_editor requires a cwd.', args, ['code_editor:missing-cwd']);
  }

  const resolved = resolveTargetInsideCwd(request.cwd, filePath);
  if (!resolved.ok) {
    return failedResult(spec, resolved.message, args, ['code_editor:invalid-path']);
  }

  // open: read file content
  if (action === 'open') {
    try {
      const content = await readFile(resolved.path, 'utf8');
      const stats = await stat(resolved.path);
      return resultFor(spec, 'completed', `Opened ${resolved.relative_path}.`, {
        cwd: request.cwd,
        action: 'open',
        path: resolved.relative_path,
        absolute_path: resolved.path,
        content,
        size: stats.size,
        modified_at: stats.mtime.toISOString()
      }, ['code_editor:open', `file:${resolved.relative_path}`]);
    } catch (error) {
      return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['code_editor:open-error']);
    }
  }

  // diff: get working-tree diff for a single file
  if (action === 'diff') {
    const diffResult = await git(request.cwd, ['diff', '--', resolved.relative_path]);
    const stagedResult = await git(request.cwd, ['diff', '--cached', '--', resolved.relative_path]);
    return resultFor(spec, 'completed', `Diff for ${resolved.relative_path}.`, {
      cwd: request.cwd,
      action: 'diff',
      path: resolved.relative_path,
      working_tree_diff: diffResult.stdout || null,
      staged_diff: stagedResult.stdout || null
    }, ['code_editor:diff', `file:${resolved.relative_path}`]);
  }

  // save: write file content (approval required)
  if (action === 'save') {
    if (!request.approval_id) {
      return resultFor(spec, 'blocked', 'save requires a Core-approved code tool invocation.', {
        cwd: request.cwd,
        action: 'save',
        path: resolved.relative_path,
        required_approval: true
      }, ['code_editor:save', 'approval:required']);
    }

    const content = stringArg(args, 'content');
    if (content === null) {
      return failedResult(spec, 'save requires a "content" argument.', args, ['code_editor:missing-content']);
    }

    try {
      await writeFile(resolved.path, content, 'utf8');
      const stats = await stat(resolved.path);
      return resultFor(spec, 'completed', `Saved ${resolved.relative_path}.`, {
        cwd: request.cwd,
        action: 'save',
        path: resolved.relative_path,
        absolute_path: resolved.path,
        size: stats.size,
        modified_at: stats.mtime.toISOString(),
        approval_id: request.approval_id
      }, ['code_editor:save', `file:${resolved.relative_path}`, 'approval:supplied']);
    } catch (error) {
      return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['code_editor:save-error']);
    }
  }

  // patch: apply a unified diff patch (approval required)
  if (action === 'patch') {
    if (!request.approval_id) {
      return resultFor(spec, 'blocked', 'patch requires a Core-approved code tool invocation.', {
        cwd: request.cwd,
        action: 'patch',
        path: resolved.relative_path,
        required_approval: true
      }, ['code_editor:patch', 'approval:required']);
    }

    const patch = stringArg(args, 'patch');
    if (!patch) {
      return failedResult(spec, 'patch requires a "patch" argument.', args, ['code_editor:missing-patch']);
    }

    try {
      const patchResult = await git(request.cwd, ['apply', '--whitespace=nowarn', '-'], {
        input: patch
      });
      if (patchResult.code !== 0) {
        return failedResult(spec, `git apply failed: ${patchResult.stderr}`, args, ['code_editor:patch-error']);
      }
      return resultFor(spec, 'completed', `Patched ${resolved.relative_path}.`, {
        cwd: request.cwd,
        action: 'patch',
        path: resolved.relative_path,
        approval_id: request.approval_id
      }, ['code_editor:patch', `file:${resolved.relative_path}`, 'approval:supplied']);
    } catch (error) {
      return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['code_editor:patch-error']);
    }
  }

  return failedResult(spec, `Unsupported code_editor action '${action}'.`, args, ['code_editor:unsupported-action']);
}

function gitReadCompatibilityTool(action: string, args: Record<string, unknown>): { toolId: string; params: Record<string, unknown> } | null {
  switch (action) {
    case 'status': return { toolId: 'git_status', params: {} };
    case 'push_plan': return { toolId: 'git_push_readiness', params: {} };
    case 'worktrees': return { toolId: 'git_worktree_list', params: {} };
    case 'branch_list': return { toolId: 'git_branch_list', params: { include_remote: args.all !== false } };
    case 'log': return { toolId: 'git_log_list', params: { limit: args.limit, revs: stringArg(args, 'ref') ? [stringArg(args, 'ref') as string] : undefined } };
    case 'diff_compare': return { toolId: 'git_diff', params: { target: 'ref_range', base_ref: args.base_ref, head_ref: args.head_ref, paths: args.paths, max_diff_bytes: args.max_diff_bytes } };
    case 'diff_preview': return { toolId: 'git_diff', params: { target: args.target === 'branch_range' ? 'ref_range' : args.target ?? 'all', base_ref: args.base_ref, head_ref: args.head_ref, paths: args.paths, max_files: args.max_files, max_diff_bytes: args.max_diff_bytes } };
    default: return null;
  }
}

function gitIndexCompatibilityTool(action: string, args: Record<string, unknown>): { toolId: string; params: Record<string, unknown> } | null {
  if (action === 'stage') return { toolId: 'git_stage', params: { paths: args.paths, patch: args.patch, max_patch_bytes: args.max_patch_bytes } };
  if (action === 'unstage') return { toolId: 'git_unstage', params: { paths: args.paths, patch: args.patch, max_patch_bytes: args.max_patch_bytes } };
  return null;
}

function gitCommitCompatibilityTool(action: string, args: Record<string, unknown>): Record<string, unknown> | null {
  if (action !== 'commit') return null;
  return {
    message: stringArg(args, 'message') ?? stringArg(args, 'commit_message'),
    paths: args.paths,
    include_all: args.include_all,
    commit_staged_only: args.commit_staged_only,
    confirm_commit: args.confirm_commit
  };
}

function gitBranchCompatibilityTool(action: string, args: Record<string, unknown>): { toolId: string; params: Record<string, unknown> } | null {
  switch (action) {
    case 'checkout': return { toolId: 'git_checkout', params: { branch: args.branch, confirm_checkout: args.confirm_checkout } };
    case 'create_branch': return { toolId: 'git_branch_create', params: { branch: args.branch, confirm_create_branch: args.confirm_create_branch } };
    case 'delete_branch': return { toolId: 'git_branch_delete', params: { branch: args.branch, force: args.force, confirm_delete_branch: args.confirm_delete_branch } };
    case 'rename_branch': return { toolId: 'git_branch_rename', params: { new_name: args.new_name, confirm_rename_branch: args.confirm_rename_branch } };
    default: return null;
  }
}

function gitWorktreeCompatibilityTool(action: string, args: Record<string, unknown>): { toolId: string; params: Record<string, unknown> } | null {
  if (action === 'create_worktree') return { toolId: 'git_worktree_create', params: { path: args.path, branch: args.branch, start_ref: args.start_ref, confirm_create_worktree: args.confirm_create_worktree } };
  if (action === 'remove_worktree') return { toolId: 'git_worktree_remove', params: { path: args.path, force: args.force, confirm_remove_worktree: args.confirm_remove_worktree } };
  return null;
}

function gitRemoteCompatibilityTool(action: string, args: Record<string, unknown>): { toolId: string; params: Record<string, unknown> } | null {
  if (action === 'fetch') return { toolId: 'git_fetch', params: { remote: args.remote === '--all' ? undefined : args.remote, prune: args.prune, confirm_fetch: args.confirm_fetch } };
  if (action === 'push') return { toolId: 'git_push', params: { remote: args.remote, branch: args.branch, set_upstream: args.set_upstream, confirm_push: args.confirm_push } };
  if (action === 'pull') return { toolId: 'git_pull', params: { remote: args.remote, branch: args.branch, confirm_pull: args.confirm_pull } };
  return null;
}

function gitIntegrationCompatibilityTool(action: string, args: Record<string, unknown>): { toolId: string; params: Record<string, unknown> } | null {
  if (action === 'merge') return { toolId: 'git_merge', params: { operation: args.operation, branch: args.branch, strategy: args.strategy, confirm_merge: args.confirm_merge } };
  if (action === 'rebase') return { toolId: 'git_rebase', params: { operation: args.operation, branch: args.branch ?? args.onto, confirm_rebase: args.confirm_rebase } };
  return null;
}

function gitConflictCompatibilityTool(action: string, args: Record<string, unknown>): { toolId: string; params: Record<string, unknown> } | null {
  if (action !== 'resolve_conflict') return null;
  return { toolId: 'git_conflict_resolve', params: { path: args.path, strategy: args.strategy, confirm_resolve: args.confirm_resolve } };
}

async function executeGitBranchViaToolLayer(spec: CodeToolSpec, request: CodeToolExecuteRequest, args: Record<string, unknown>, overrideToolId?: string, legacyAction?: string): Promise<CodeToolExecuteResult> {
  if (!request.cwd) return failedResult(spec, `${spec.id} requires a cwd.`, args, ['git:branch', 'cwd:required']);
  if (!request.approval_id) return resultFor(spec, 'blocked', `${overrideToolId ?? spec.id} requires a Core-approved Git invocation.`, { cwd: request.cwd, required_approval: true }, ['git:branch', 'approval:required']);
  const toolId = overrideToolId ?? spec.id;
  const confirmation = toolId === 'git_checkout' ? 'confirm_checkout' : toolId === 'git_branch_create' ? 'confirm_create_branch' : toolId === 'git_branch_delete' ? 'confirm_delete_branch' : 'confirm_rename_branch';
  if (!booleanArg(args, confirmation)) return resultFor(spec, 'blocked', `${legacyAction ?? toolId} requires ${confirmation}: true after Core approval.`, { cwd: request.cwd, required_confirmation: confirmation }, ['git:branch', 'approval:supplied', 'confirmation:required']);
  try {
    const result = await callToolLayer(request.cwd, toolId, { ...args, repository_path: '.' }, { approved: true, sessionId: request.session_id }) as Record<string, unknown>;
    if (result.success !== true) return failedResult(spec, typeof result.error === 'string' ? result.error : `${toolId} failed.`, args, ['git:branch', 'tool-layer-rejected', `tool_id:${toolId}`]);
    const status = recordArg(result.status);
    const legacyFlags = legacyAction === 'checkout' ? { checked_out: true } : legacyAction === 'create_branch' ? { created: true } : legacyAction === 'delete_branch' ? { deleted: true } : legacyAction === 'rename_branch' ? { renamed: true } : {};
    return resultFor(spec, 'completed', legacyAction ? `Completed Git ${legacyAction}.` : spec.summary, { ...result, ...legacyFlags, ...(legacyAction ? { action: legacyAction } : {}), cwd: path.resolve(request.cwd), branch: status.branch ?? result.branch ?? null, upstream: status.upstream ?? null, ahead: status.ahead ?? null, behind: status.behind ?? null, has_uncommitted_changes: status.has_uncommitted_changes ?? null }, ['git:branch', 'tool-layer', `tool_id:${toolId}`]);
  } catch (error) { return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['git:branch', 'tool-layer-failed', `tool_id:${toolId}`]); }
}

async function executeGitWorktreeMutationViaToolLayer(spec: CodeToolSpec, request: CodeToolExecuteRequest, args: Record<string, unknown>, overrideToolId?: string, legacyAction?: string): Promise<CodeToolExecuteResult> {
  if (!request.cwd) return failedResult(spec, `${spec.id} requires a cwd.`, args, ['git:worktree', 'cwd:required']);
  if (!request.approval_id) return resultFor(spec, 'blocked', `${overrideToolId ?? spec.id} requires a Core-approved Git invocation.`, { cwd: request.cwd, required_approval: true }, ['git:worktree', 'approval:required']);
  const toolId = overrideToolId ?? spec.id;
  const confirmation = toolId === 'git_worktree_create' ? 'confirm_create_worktree' : 'confirm_remove_worktree';
  if (!booleanArg(args, confirmation)) return resultFor(spec, 'blocked', `${legacyAction ?? toolId} requires ${confirmation}: true after Core approval.`, { cwd: request.cwd, required_confirmation: confirmation }, ['git:worktree', 'approval:supplied', 'confirmation:required']);
  try {
    const result = await callToolLayer(request.cwd, toolId, { ...args, repository_path: '.' }, { approved: true, sessionId: request.session_id }) as Record<string, unknown>;
    if (result.success !== true) return failedResult(spec, typeof result.error === 'string' ? result.error : `${toolId} failed.`, args, ['git:worktree', 'tool-layer-rejected', `tool_id:${toolId}`]);
    return resultFor(spec, 'completed', legacyAction ? `Completed Git ${legacyAction}.` : spec.summary, { ...result, ...(legacyAction ? { action: legacyAction } : {}), cwd: path.resolve(request.cwd), created: toolId === 'git_worktree_create', removed: toolId === 'git_worktree_remove' }, ['git:worktree', 'tool-layer', `tool_id:${toolId}`]);
  } catch (error) { return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['git:worktree', 'tool-layer-failed', `tool_id:${toolId}`]); }
}

async function executeGitRemoteMutationViaToolLayer(spec: CodeToolSpec, request: CodeToolExecuteRequest, args: Record<string, unknown>, overrideToolId?: string, legacyAction?: string): Promise<CodeToolExecuteResult> {
  if (!request.cwd) return failedResult(spec, `${spec.id} requires a cwd.`, args, ['git:remote', 'cwd:required']);
  if (!request.approval_id) return resultFor(spec, 'blocked', `${overrideToolId ?? spec.id} requires a Core-approved Git invocation.`, { cwd: request.cwd, required_approval: true }, ['git:remote', 'approval:required']);
  const toolId = overrideToolId ?? spec.id;
  const confirmation = toolId === 'git_fetch' ? 'confirm_fetch' : toolId === 'git_push' ? 'confirm_push' : 'confirm_pull';
  if (!booleanArg(args, confirmation)) return resultFor(spec, 'blocked', `${legacyAction ?? toolId} requires ${confirmation}: true after Core approval.`, { cwd: request.cwd, required_confirmation: confirmation }, ['git:remote', 'approval:supplied', 'confirmation:required']);
  try {
    const result = await callToolLayer(request.cwd, toolId, { ...args, repository_path: '.' }, { approved: true, sessionId: request.session_id }) as Record<string, unknown>;
    if (result.success !== true) return failedResult(spec, typeof result.error === 'string' ? result.error : `${toolId} failed.`, args, ['git:remote', 'tool-layer-rejected', `tool_id:${toolId}`]);
    const status = recordArg(result.status);
    const flags = { fetched: toolId === 'git_fetch', pushed: toolId === 'git_push' && result.changed === true, pulled: toolId === 'git_pull' && result.changed === true };
    return resultFor(spec, 'completed', legacyAction ? `Completed Git ${legacyAction}.` : spec.summary, { ...result, ...flags, ...(legacyAction ? { action: legacyAction } : {}), cwd: path.resolve(request.cwd), branch: status.branch ?? result.branch ?? null, upstream: status.upstream ?? null, ahead: status.ahead ?? null, behind: status.behind ?? null, has_uncommitted_changes: status.has_uncommitted_changes ?? null, branch_tracking_info: result.branches ?? [] }, ['git:remote', 'tool-layer', `tool_id:${toolId}`]);
  } catch (error) { return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['git:remote', 'tool-layer-failed', `tool_id:${toolId}`]); }
}

async function executeGitIntegrationViaToolLayer(spec: CodeToolSpec, request: CodeToolExecuteRequest, args: Record<string, unknown>, overrideToolId?: string, legacyAction?: string): Promise<CodeToolExecuteResult> {
  if (!request.cwd) return failedResult(spec, `${spec.id} requires a cwd.`, args, ['git:integration', 'cwd:required']);
  if (!request.approval_id) return resultFor(spec, 'blocked', `${overrideToolId ?? spec.id} requires a Core-approved Git invocation.`, { cwd: request.cwd, required_approval: true }, ['git:integration', 'approval:required']);
  const toolId = overrideToolId ?? spec.id;
  const confirmation = toolId === 'git_merge' ? 'confirm_merge' : 'confirm_rebase';
  if (!booleanArg(args, confirmation)) return resultFor(spec, 'blocked', `${legacyAction ?? toolId} requires ${confirmation}: true after Core approval.`, { cwd: request.cwd, required_confirmation: confirmation }, ['git:integration', 'approval:supplied', 'confirmation:required']);
  try {
    const result = await callToolLayer(request.cwd, toolId, { ...args, repository_path: '.' }, { approved: true, sessionId: request.session_id }) as Record<string, unknown>;
    const status = recordArg(result.status);
    const data = { ...result, ...(legacyAction ? { action: legacyAction } : {}), cwd: path.resolve(request.cwd), branch_state: status.branch ?? null, has_uncommitted_changes: status.has_uncommitted_changes ?? null };
    if (result.success !== true) return resultFor(spec, 'failed', typeof result.error === 'string' ? result.error : `${toolId} failed.`, data, ['git:integration', 'tool-layer-rejected', `tool_id:${toolId}`]);
    return resultFor(spec, 'completed', legacyAction ? `Completed Git ${legacyAction}.` : spec.summary, data, ['git:integration', 'tool-layer', `tool_id:${toolId}`]);
  } catch (error) { return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['git:integration', 'tool-layer-failed', `tool_id:${toolId}`]); }
}

async function executeGitConflictResolveViaToolLayer(spec: CodeToolSpec, request: CodeToolExecuteRequest, args: Record<string, unknown>, overrideToolId?: string, legacyAction?: string): Promise<CodeToolExecuteResult> {
  if (!request.cwd) return failedResult(spec, `${spec.id} requires a cwd.`, args, ['git:conflict', 'cwd:required']);
  if (!request.approval_id) return resultFor(spec, 'blocked', `${overrideToolId ?? spec.id} requires a Core-approved Git invocation.`, { cwd: request.cwd, required_approval: true }, ['git:conflict', 'approval:required']);
  const toolId = overrideToolId ?? spec.id;
  if (!booleanArg(args, 'confirm_resolve')) return resultFor(spec, 'blocked', 'resolve_conflict requires confirm_resolve: true after Core approval.', { cwd: request.cwd, required_confirmation: 'confirm_resolve' }, ['git:conflict', 'approval:supplied', 'confirmation:required']);
  try {
    const result = await callToolLayer(request.cwd, toolId, { ...args, repository_path: '.' }, { approved: true, sessionId: request.session_id }) as Record<string, unknown>;
    if (result.success !== true) return failedResult(spec, typeof result.error === 'string' ? result.error : `${toolId} failed.`, args, ['git:conflict', 'tool-layer-rejected', `tool_id:${toolId}`]);
    const status = recordArg(result.status);
    const remaining = Array.isArray(result.remaining_conflicts) ? result.remaining_conflicts : [];
    return resultFor(spec, 'completed', legacyAction ? 'Completed Git conflict resolution.' : spec.summary, { ...result, ...(legacyAction ? { action: legacyAction } : {}), cwd: path.resolve(request.cwd), all_resolved: remaining.length === 0, branch: status.branch ?? null }, ['git:conflict', 'tool-layer', `tool_id:${toolId}`]);
  } catch (error) { return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['git:conflict', 'tool-layer-failed', `tool_id:${toolId}`]); }
}

async function executeGitIndexViaToolLayer(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  overrideToolId?: string,
  legacyAction?: string
): Promise<CodeToolExecuteResult> {
  if (!request.cwd) return failedResult(spec, `${spec.id} requires a cwd.`, args, ['git:index', 'cwd:required']);
  if (!request.approval_id) {
    return resultFor(spec, 'blocked', `${overrideToolId ?? spec.id} requires a Core-approved Git invocation.`, {
      cwd: request.cwd,
      required_approval: true
    }, ['git:index', 'approval:required']);
  }
  const toolId = overrideToolId ?? spec.id;
  try {
    const result = await callToolLayer(request.cwd, toolId, { ...args, repository_path: '.' }, {
      approved: true,
      sessionId: request.session_id
    }) as Record<string, unknown>;
    if (result.success !== true) {
      return failedResult(spec, typeof result.error === 'string' ? result.error : `${toolId} failed.`, args, ['git:index', 'tool-layer-rejected', `tool_id:${toolId}`]);
    }
    const status = recordArg(result.status);
    return resultFor(spec, 'completed', legacyAction ? `Completed Git ${legacyAction}.` : spec.summary, {
      ...result,
      ...(legacyAction ? { action: legacyAction } : {}),
      cwd: path.resolve(request.cwd),
      branch: status.branch ?? null,
      upstream: status.upstream ?? null,
      ahead: status.ahead ?? null,
      behind: status.behind ?? null,
      has_uncommitted_changes: status.has_uncommitted_changes ?? null,
      files: status.files ?? []
    }, ['git:index', 'tool-layer', `tool_id:${toolId}`]);
  } catch (error) {
    return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['git:index', 'tool-layer-failed', `tool_id:${toolId}`]);
  }
}

async function executeGitCommitViaToolLayer(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  legacyAction?: string
): Promise<CodeToolExecuteResult> {
  if (!request.cwd) return failedResult(spec, `${spec.id} requires a cwd.`, args, ['git:commit', 'cwd:required']);
  if (!request.approval_id) {
    return resultFor(spec, 'blocked', 'git_commit requires a Core-approved Git invocation.', {
      cwd: request.cwd,
      required_approval: true
    }, ['git:commit', 'approval:required']);
  }
  if (!booleanArg(args, 'confirm_commit')) {
    return resultFor(spec, 'blocked', 'commit requires confirm_commit: true after Core approval.', {
      cwd: request.cwd,
      required_confirmation: 'confirm_commit'
    }, ['git:commit', 'approval:supplied', 'confirmation:required']);
  }

  try {
    const result = await callToolLayer(request.cwd, 'git_commit', {
      message: stringArg(args, 'message') ?? stringArg(args, 'commit_message'),
      paths: args.paths,
      include_all: args.include_all,
      commit_staged_only: args.commit_staged_only,
      repository_path: '.'
    }, {
      approved: true,
      sessionId: request.session_id
    }) as Record<string, unknown>;
    if (result.success !== true) {
      return failedResult(spec, typeof result.error === 'string' ? result.error : 'git_commit failed.', args, ['git:commit', 'tool-layer-rejected', 'tool_id:git_commit']);
    }

    const status = recordArg(result.status);
    const commitHash = typeof result.commit_hash === 'string' ? result.commit_hash : null;
    return resultFor(spec, 'completed', commitHash ? `Created commit ${commitHash.slice(0, 12)}.` : 'Created commit.', {
      ...result,
      ...(legacyAction ? { action: legacyAction } : {}),
      cwd: path.resolve(request.cwd),
      git_root: path.resolve(request.cwd),
      approval_id: request.approval_id,
      include_all: args.include_all === true,
      commit_staged_only: args.commit_staged_only === true,
      paths: stringListArg(args, 'paths'),
      branch: status.branch ?? null,
      upstream: status.upstream ?? null,
      ahead: status.ahead ?? null,
      behind: status.behind ?? null,
      has_uncommitted_changes: status.has_uncommitted_changes ?? null
    }, ['git:commit', 'tool-layer', 'tool_id:git_commit', 'approval:supplied', 'confirmation:supplied']);
  } catch (error) {
    return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['git:commit', 'tool-layer-failed', 'tool_id:git_commit']);
  }
}

async function executeGitReadViaToolLayer(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  overrideToolId?: string,
  legacyAction?: string
): Promise<CodeToolExecuteResult> {
  if (!request.cwd) return failedResult(spec, `${spec.id} requires a cwd.`, args, ['git:missing-cwd']);
  const toolId = overrideToolId ?? spec.id;
  if (!request.approval_id) {
    return resultFor(spec, 'blocked', `${toolId} requires a Core-approved Git read invocation.`, {
      cwd: request.cwd,
      required_approval: true
    }, ['git:read', 'approval:required']);
  }
  try {
    const result = await callToolLayer(request.cwd, toolId, {
      ...args,
      repository_path: '.'
    }, {
      approved: true,
      sessionId: request.session_id
    }) as Record<string, unknown>;
    if (result.success !== true) {
      return failedResult(spec, typeof result.error === 'string' ? result.error : `${toolId} failed.`, args, ['git:tool-layer-rejected', `tool_id:${toolId}`]);
    }
    const data = legacyAction
      ? { ...adaptLegacyGitReadResult(result, legacyAction), action: legacyAction, cwd: path.resolve(request.cwd) }
      : { ...result, cwd: path.resolve(request.cwd) };
    return resultFor(spec, 'completed', legacyAction ? `Completed Git ${legacyAction} read.` : spec.summary, data, ['git:tool-layer', `tool_id:${toolId}`]);
  } catch (error) {
    return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['git:tool-layer-failed', `tool_id:${toolId}`]);
  }
}

function adaptLegacyGitReadResult(result: Record<string, unknown>, action: string): Record<string, unknown> {
  if (action === 'status') {
    return {
      ...result,
      files: adaptLegacyGitStatusFiles(result.files)
    };
  }
  if (action === 'push_plan') {
    const status = recordArg(result.status);
    return {
      ...status,
      push_ready: result.ready === true,
      push_blockers: Array.isArray(result.blockers) ? result.blockers.map(legacyPushBlocker) : [],
      needs_push: result.needs_push === true,
      suggested_commands: ['git status --short --branch']
    };
  }
  if (action === 'branch_list') {
    const branches = Array.isArray(result.branches) ? result.branches.map((value) => {
      const branch = recordArg(value);
      const isRemote = branch.is_remote === true;
      const name = typeof branch.name === 'string' ? branch.name : '';
      return {
        ...branch,
        name: isRemote ? `remotes/${name}` : name,
        commit_hash: branch.commit ?? null
      };
    }) : [];
    return { ...result, branches };
  }
  if (action === 'log') {
    const commits = Array.isArray(result.commits) ? result.commits.map((value) => {
      const commit = recordArg(value);
      return {
        ...commit,
        email: commit.author_email ?? null,
        date: commit.author_date ?? null
      };
    }) : [];
    return { ...result, commits };
  }
  if (action === 'diff_preview' || action === 'diff_compare') {
    const sections: Record<string, unknown>[] = Array.isArray(result.sections) ? result.sections.map((value) => {
      const section = recordArg(value);
      const files = Array.isArray(section.files) ? section.files.map(adaptLegacyDiffFile) : [];
      const id = section.kind === 'ref_range' ? 'branch_range' : section.kind ?? null;
      return { ...section, id, files, file_count: files.length, additions: null, deletions: null, notices: [] };
    }) : [];
    if (action === 'diff_preview' && sections.length === 0) {
      sections.push({ id: 'branch_range', kind: 'ref_range', files: [], file_count: 0, additions: null, deletions: null, notices: ['No upstream branch is configured.'] });
    }
    const refRange = sections.find((section) => section.kind === 'ref_range') ?? sections[0] ?? {};
    if (action === 'diff_compare') {
      return {
        ...result,
        base_ref: refRange.base_ref ?? null,
        head_ref: refRange.head_ref ?? null,
        diff_stat: null,
        diff: refRange.diff ?? null,
        files: refRange.files ?? [],
        file_count: refRange.file_count ?? 0,
        commits: [],
        sections
      };
    }
    return { ...result, sections };
  }
  return result;
}

function adaptLegacyGitStatusFiles(value: unknown): Record<string, unknown>[] {
  return Array.isArray(value) ? value.map((item) => {
    const file = recordArg(item);
    return { ...file, is_renamed: typeof file.previous_path === 'string' };
  }) : [];
}

function legacyPushBlocker(value: unknown): unknown {
  if (typeof value !== 'string') return value;
  if (value.startsWith('No upstream')) return 'no upstream';
  if (value.startsWith('Working tree has')) return 'uncommitted changes';
  if (value.startsWith('HEAD is detached')) return 'detached HEAD';
  if (value.startsWith('Branch is behind')) return 'behind upstream';
  return value;
}

function adaptLegacyDiffFile(value: unknown): Record<string, unknown> {
  const file = recordArg(value);
  const status = typeof file.status === 'string' ? file.status : '';
  const changeType: Record<string, string> = { A: 'added', D: 'deleted', M: 'modified', R: 'renamed', C: 'copied', T: 'type_changed', U: 'unmerged' };
  return {
    ...file,
    path: file.new_path ?? null,
    previous_path: file.old_path ?? null,
    change_type: changeType[status] ?? status,
    binary: file.is_binary === true
  };
}

function recordArg(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

async function executeGitWorktreeManager(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>
): Promise<CodeToolExecuteResult> {
  const action = stringArg(args, 'action') ?? 'status';
  const mutatingActions = new Set([
    'stage', 'unstage', 'commit', 'push', 'pull', 'merge', 'rebase',
    'create_branch', 'create_worktree', 'remove_worktree', 'checkout', 'fetch', 'resolve_conflict',
    'delete_branch', 'rename_branch'
  ]);
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

  const indexTool = gitIndexCompatibilityTool(action, args);
  if (indexTool) {
    return executeGitIndexViaToolLayer(spec, request, indexTool.params, indexTool.toolId, action);
  }

  const commitTool = gitCommitCompatibilityTool(action, args);
  if (commitTool) {
    return executeGitCommitViaToolLayer(spec, request, commitTool, action);
  }

  const branchTool = gitBranchCompatibilityTool(action, args);
  if (branchTool) {
    return executeGitBranchViaToolLayer(spec, request, branchTool.params, branchTool.toolId, action);
  }

  const worktreeTool = gitWorktreeCompatibilityTool(action, args);
  if (worktreeTool) {
    return executeGitWorktreeMutationViaToolLayer(spec, request, worktreeTool.params, worktreeTool.toolId, action);
  }

  const remoteTool = gitRemoteCompatibilityTool(action, args);
  if (remoteTool) {
    return executeGitRemoteMutationViaToolLayer(spec, request, remoteTool.params, remoteTool.toolId, action);
  }

  const integrationTool = gitIntegrationCompatibilityTool(action, args);
  if (integrationTool) {
    return executeGitIntegrationViaToolLayer(spec, request, integrationTool.params, integrationTool.toolId, action);
  }

  const conflictTool = gitConflictCompatibilityTool(action, args);
  if (conflictTool) {
    return executeGitConflictResolveViaToolLayer(spec, request, conflictTool.params, conflictTool.toolId, action);
  }

  const compatibilityTool = gitReadCompatibilityTool(action, args);
  if (compatibilityTool) {
    return executeGitReadViaToolLayer(spec, request, compatibilityTool.params, compatibilityTool.toolId, action);
  }

  const root = await git(request.cwd, ['rev-parse', '--show-toplevel']);
  if (root.code !== 0) {
    return failedResult(spec, 'cwd is not inside a Git repository.', args, ['git:rev-parse']);
  }

  const gitRoot = root.stdout.trim();
  if (action === 'stage' || action === 'unstage') {
    return executeGitIndexUpdate(spec, request, args, gitRoot, action);
  }
  if (action === 'push') {
    return executeGitPush(spec, request, args, gitRoot);
  }

  if (action === 'pull') {
    return executeGitPull(spec, request, args, gitRoot);
  }


  if (action === 'fetch') {
    return executeGitFetch(spec, request, args, gitRoot);
  }

  if (action === 'branch_list') {
    return executeGitBranchList(spec, request, args, gitRoot);
  }

  if (action === 'merge') {
    return executeGitMerge(spec, request, args, gitRoot);
  }

  if (action === 'rebase') {
    return executeGitRebase(spec, request, args, gitRoot);
  }

  if (action === 'resolve_conflict') {
    return executeGitResolveConflict(spec, request, args, gitRoot);
  }


  if (mutatingActions.has(action)) {
    return resultFor(spec, 'blocked', `${action} is approved but not executed by the preview manager yet.`, {
      cwd: request.cwd,
      git_root: gitRoot,
      action,
      approval_id: request.approval_id,
      next_step: 'Dispatch a Git mutation implementation after Core approval.'
    }, [`git:${action}`, 'approval:supplied', 'mutation:not-implemented']);
  }

  if (action === 'diff_preview') {
    return executeGitDiffPreview(spec, request, args, gitRoot);
  }

  if (action === 'diff_compare') {
    return executeGitDiffCompare(spec, request, args, gitRoot);
  }

  if (action === 'log') {
    return executeGitLog(spec, request, args, gitRoot);
  }

  if (action !== 'status' && action !== 'push_plan' && action !== 'worktrees') {
    return failedResult(spec, `Unsupported git_worktree_manager action '${action}'.`, args, ['git:unsupported-action']);
  }

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
    files: statusSummary.files,
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

async function executeGitIndexUpdate(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string,
  action: 'stage' | 'unstage'
): Promise<CodeToolExecuteResult> {
  const requiredConfirmation = action === 'stage' ? 'confirm_stage' : 'confirm_unstage';
  if (!booleanArg(args, requiredConfirmation)) {
    return resultFor(spec, 'blocked', `${action} requires ${requiredConfirmation}: true after Core approval.`, {
      cwd: request.cwd,
      git_root: gitRoot,
      action,
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: requiredConfirmation
    }, [`git:${action}`, 'approval:supplied', 'confirmation:required']);
  }

  const rawPaths = stringListArg(args, 'paths');
  if (rawPaths.length === 0) {
    return resultFor(spec, 'blocked', `${action} requires one or more paths.`, {
      cwd: request.cwd,
      git_root: gitRoot,
      action,
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_argument: 'paths'
    }, [`git:${action}`, 'approval:supplied', 'paths:required']);
  }

  const pathspecs: string[] = [];
  for (const rawPath of rawPaths) {
    const normalized = normalizeGitPathspec(gitRoot, rawPath);
    if (!normalized.ok) {
      return resultFor(spec, 'blocked', normalized.message, {
        cwd: request.cwd,
        git_root: gitRoot,
        action,
        approval_id: request.approval_id,
        argument_keys: Object.keys(args).sort()
      }, [`git:${action}`, 'approval:supplied', 'path:blocked']);
    }
    pathspecs.push(normalized.pathspec);
  }

  const command = action === 'stage'
    ? ['add', '--', ...pathspecs]
    : ['restore', '--staged', '--', ...pathspecs];
  const update = await git(gitRoot, command);
  if (update.code !== 0) {
    return gitCommandFailedResult(spec, `git ${action} failed.`, args, gitRoot, action, update, [`git:${action}`, 'git:index']);
  }

  const status = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  const statusSummary = status.code === 0 ? parseGitStatus(status.stdout) : null;
  return resultFor(spec, 'completed', `${action === 'stage' ? 'Staged' : 'Unstaged'} ${rawPaths.length} path${rawPaths.length === 1 ? '' : 's'}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action,
    approval_id: request.approval_id,
    paths: rawPaths,
    branch: statusSummary?.branch ?? null,
    upstream: statusSummary?.upstream ?? null,
    ahead: statusSummary?.ahead ?? null,
    behind: statusSummary?.behind ?? null,
    has_uncommitted_changes: statusSummary?.hasUncommittedChanges ?? null,
    files: statusSummary?.files ?? null
  }, [`git:${action}`, 'approval:supplied', 'confirmation:supplied', 'git:index']);
}

async function executeGitPush(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  if (!booleanArg(args, 'confirm_push')) {
    return resultFor(spec, 'blocked', 'push requires confirm_push: true after Core approval.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'push',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: 'confirm_push'
    }, ['git:push', 'approval:supplied', 'confirmation:required']);
  }

  const status = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  if (status.code !== 0) {
    return gitCommandFailedResult(spec, 'Unable to inspect Git status before push.', args, gitRoot, 'push', status, ['git:push', 'git:status']);
  }

  const statusSummary = parseGitStatus(status.stdout);
  const setUpstream = booleanArg(args, 'set_upstream');
  const remote = stringArg(args, 'remote') ?? 'origin';
  const pushBlockers = pushReadinessBlockers(statusSummary).filter((blocker) => !(blocker === 'no upstream' && setUpstream));
  if (pushBlockers.length > 0) {
    return resultFor(spec, 'blocked', `push blocked: ${pushBlockers.join('; ')}.`, {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'push',
      approval_id: request.approval_id,
      branch: statusSummary.branch,
      upstream: statusSummary.upstream,
      ahead: statusSummary.ahead,
      behind: statusSummary.behind,
      has_uncommitted_changes: statusSummary.hasUncommittedChanges,
      push_blockers: pushBlockers,
      argument_keys: Object.keys(args).sort()
    }, ['git:push', 'approval:supplied', 'push:blocked']);
  }

  if (!isSafeGitRefName(remote)) {
    return resultFor(spec, 'blocked', 'push remote must be a configured remote name without whitespace or option prefixes.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'push',
      approval_id: request.approval_id,
      remote
    }, ['git:push', 'approval:supplied', 'remote:blocked']);
  }

  if (!statusSummary.upstream && setUpstream && !isSafeGitRefName(statusSummary.branch)) {
    return resultFor(spec, 'blocked', 'push cannot set upstream for an unsafe branch name.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'push',
      approval_id: request.approval_id,
      branch: statusSummary.branch
    }, ['git:push', 'approval:supplied', 'branch:blocked']);
  }

  if (statusSummary.upstream && statusSummary.ahead === 0) {
    return resultFor(spec, 'completed', `No local commits need pushing for ${statusSummary.branch}.`, {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'push',
      approval_id: request.approval_id,
      branch: statusSummary.branch,
      upstream: statusSummary.upstream,
      ahead: statusSummary.ahead,
      behind: statusSummary.behind,
      pushed: false
    }, ['git:push', 'approval:supplied', 'confirmation:supplied', 'push:no-op']);
  }

  const pushArgs = !statusSummary.upstream && setUpstream
    ? ['push', '-u', remote, statusSummary.branch]
    : ['push'];
  const push = await git(gitRoot, pushArgs, { timeoutMs: 60_000 });
  if (push.code !== 0) {
    return gitCommandFailedResult(spec, 'git push failed.', args, gitRoot, 'push', push, ['git:push']);
  }

  const finalStatus = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  const finalSummary = finalStatus.code === 0 ? parseGitStatus(finalStatus.stdout) : statusSummary;

  return resultFor(spec, 'completed', `Pushed ${statusSummary.branch}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'push',
    approval_id: request.approval_id,
    branch: finalSummary.branch,
    upstream: finalSummary.upstream,
    ahead: finalSummary.ahead,
    behind: finalSummary.behind,
    has_uncommitted_changes: finalSummary.hasUncommittedChanges,
    pushed: true,
    set_upstream: !statusSummary.upstream && setUpstream,
    remote,
    push_output: [push.stdout.trim(), push.stderr.trim()].filter(Boolean).join('\n')
  }, ['git:push', 'approval:supplied', 'confirmation:supplied']);
}

async function executeGitPull(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  if (!booleanArg(args, 'confirm_pull')) {
    return resultFor(spec, 'blocked', 'pull requires confirm_pull: true after Core approval.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'pull',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: 'confirm_pull'
    }, ['git:pull', 'approval:supplied', 'confirmation:required']);
  }

  const status = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  if (status.code !== 0) {
    return gitCommandFailedResult(spec, 'Unable to inspect Git status before pull.', args, gitRoot, 'pull', status, ['git:pull', 'git:status']);
  }

  const statusSummary = parseGitStatus(status.stdout);
  const remote = stringArg(args, 'remote') ?? 'origin';
  const branch = stringArg(args, 'branch') ?? statusSummary.branch;
  const rebase = booleanArg(args, 'rebase');
  const ffOnly = booleanArg(args, 'ff_only');

  if (!isSafeGitRefName(remote)) {
    return resultFor(spec, 'blocked', 'pull remote must be a configured remote name without whitespace or option prefixes.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'pull',
      approval_id: request.approval_id,
      remote
    }, ['git:pull', 'approval:supplied', 'remote:blocked']);
  }

  if (!isSafeGitRefName(branch)) {
    return resultFor(spec, 'blocked', 'pull branch name is unsafe.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'pull',
      approval_id: request.approval_id,
      branch
    }, ['git:pull', 'approval:supplied', 'branch:blocked']);
  }

  const pullArgs = ['pull'];
  if (rebase) pullArgs.push('--rebase');
  if (ffOnly) pullArgs.push('--ff-only');
  pullArgs.push(remote, branch);

  const pull = await git(gitRoot, pullArgs, { timeoutMs: 60_000 });
  if (pull.code !== 0) {
    const conflictDetected = pull.stdout.includes('CONFLICT') || pull.stderr.includes('CONFLICT') || pull.stdout.includes('merge failed');
    const tags = conflictDetected ? ['git:pull', 'git:conflict'] : ['git:pull'];
    return gitCommandFailedResult(spec, 'git pull failed.', args, gitRoot, 'pull', pull, tags);
  }

  const finalStatus = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  const finalSummary = finalStatus.code === 0 ? parseGitStatus(finalStatus.stdout) : statusSummary;

  return resultFor(spec, 'completed', `Pulled ${branch} from ${remote}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'pull',
    approval_id: request.approval_id,
    branch: finalSummary.branch,
    upstream: finalSummary.upstream,
    ahead: finalSummary.ahead,
    behind: finalSummary.behind,
    has_uncommitted_changes: finalSummary.hasUncommittedChanges,
    remote,
    pulled: true,
    pull_output: [pull.stdout.trim(), pull.stderr.trim()].filter(Boolean).join('\n')
  }, ['git:pull', 'approval:supplied', 'confirmation:supplied']);
}

async function executeGitCheckout(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  if (!booleanArg(args, 'confirm_checkout')) {
    return resultFor(spec, 'blocked', 'checkout requires confirm_checkout: true after Core approval.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'checkout',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: 'confirm_checkout'
    }, ['git:checkout', 'approval:supplied', 'confirmation:required']);
  }

  const branch = stringArg(args, 'branch');
  if (!branch) {
    return resultFor(spec, 'blocked', 'checkout requires a branch name.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'checkout',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_argument: 'branch'
    }, ['git:checkout', 'approval:supplied', 'branch:required']);
  }

  if (!isSafeGitRefName(branch)) {
    return resultFor(spec, 'blocked', 'checkout branch name is unsafe.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'checkout',
      approval_id: request.approval_id,
      branch
    }, ['git:checkout', 'approval:supplied', 'branch:blocked']);
  }

  const checkout = await git(gitRoot, ['checkout', branch]);
  if (checkout.code !== 0) {
    return gitCommandFailedResult(spec, 'git checkout failed.', args, gitRoot, 'checkout', checkout, ['git:checkout']);
  }

  const finalStatus = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  const finalSummary = finalStatus.code === 0 ? parseGitStatus(finalStatus.stdout) : null;

  return resultFor(spec, 'completed', `Checked out ${branch}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'checkout',
    approval_id: request.approval_id,
    branch: finalSummary?.branch ?? branch,
    upstream: finalSummary?.upstream ?? null,
    ahead: finalSummary?.ahead ?? null,
    behind: finalSummary?.behind ?? null,
    has_uncommitted_changes: finalSummary?.hasUncommittedChanges ?? null,
    checked_out: true,
    checkout_output: checkout.stdout.trim()
  }, ['git:checkout', 'approval:supplied', 'confirmation:supplied']);
}

async function executeGitCreateBranch(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  if (!booleanArg(args, 'confirm_create_branch')) {
    return resultFor(spec, 'blocked', 'create_branch requires confirm_create_branch: true after Core approval.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'create_branch',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: 'confirm_create_branch'
    }, ['git:create_branch', 'approval:supplied', 'confirmation:required']);
  }

  const branch = stringArg(args, 'branch');
  if (!branch) {
    return resultFor(spec, 'blocked', 'create_branch requires a branch name.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'create_branch',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_argument: 'branch'
    }, ['git:create_branch', 'approval:supplied', 'branch:required']);
  }

  if (!isSafeGitRefName(branch)) {
    return resultFor(spec, 'blocked', 'create_branch branch name is unsafe.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'create_branch',
      approval_id: request.approval_id,
      branch
    }, ['git:create_branch', 'approval:supplied', 'branch:blocked']);
  }

  const existsResult = await git(gitRoot, ['rev-parse', '--verify', branch]);
  if (existsResult.code === 0) {
    return resultFor(spec, 'blocked', `Branch '${branch}' already exists.`, {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'create_branch',
      approval_id: request.approval_id,
      branch
    }, ['git:create_branch', 'approval:supplied', 'branch:exists']);
  }

  const create = await git(gitRoot, ['checkout', '-b', branch]);
  if (create.code !== 0) {
    return gitCommandFailedResult(spec, 'git checkout -b failed.', args, gitRoot, 'create_branch', create, ['git:create_branch']);
  }

  const finalStatus = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  const finalSummary = finalStatus.code === 0 ? parseGitStatus(finalStatus.stdout) : null;

  return resultFor(spec, 'completed', `Created and checked out ${branch}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'create_branch',
    approval_id: request.approval_id,
    branch: finalSummary?.branch ?? branch,
    upstream: finalSummary?.upstream ?? null,
    ahead: finalSummary?.ahead ?? null,
    behind: finalSummary?.behind ?? null,
    has_uncommitted_changes: finalSummary?.hasUncommittedChanges ?? null,
    created: true,
    create_output: create.stdout.trim()
  }, ['git:create_branch', 'approval:supplied', 'confirmation:supplied']);
}

async function executeGitFetch(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  if (!booleanArg(args, 'confirm_fetch')) {
    return resultFor(spec, 'blocked', 'fetch requires confirm_fetch: true after Core approval.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'fetch',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: 'confirm_fetch'
    }, ['git:fetch', 'approval:supplied', 'confirmation:required']);
  }

  const remote = stringArg(args, 'remote') ?? '--all';
  const prune = booleanArg(args, 'prune', true);

  if (remote !== '--all' && !isSafeGitRefName(remote)) {
    return resultFor(spec, 'blocked', 'fetch remote must be a configured remote name without whitespace or option prefixes.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'fetch',
      approval_id: request.approval_id,
      remote
    }, ['git:fetch', 'approval:supplied', 'remote:blocked']);
  }

  const fetchArgs = ['fetch'];
  if (prune) fetchArgs.push('--prune');
  fetchArgs.push(remote);

  const fetchResult = await git(gitRoot, fetchArgs, { timeoutMs: 60_000 });
  if (fetchResult.code !== 0) {
    return gitCommandFailedResult(spec, 'git fetch failed.', args, gitRoot, 'fetch', fetchResult, ['git:fetch']);
  }

  const [status, branches] = await Promise.all([
    git(gitRoot, ['status', '--porcelain=v1', '--branch']),
    git(gitRoot, ['branch', '-a', '-vv', '--format=%(refname:short)\t%(upstream:short)\t%(upstream:track)'])
  ]);
  const statusSummary = status.code === 0 ? parseGitStatus(status.stdout) : null;

  return resultFor(spec, 'completed', `Fetched from ${remote}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'fetch',
    approval_id: request.approval_id,
    branch: statusSummary?.branch ?? null,
    upstream: statusSummary?.upstream ?? null,
    ahead: statusSummary?.ahead ?? null,
    behind: statusSummary?.behind ?? null,
    fetched: true,
    remote,
    fetch_output: [fetchResult.stdout.trim(), fetchResult.stderr.trim()].filter(Boolean).join('\n'),
    branch_tracking_info: parseBranchTrackingInfo(branches.stdout)
  }, ['git:fetch', 'approval:supplied', 'confirmation:supplied']);
}

async function executeGitBranchList(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  const all = booleanArg(args, 'all', true);
  // Use full %(refname) so remote branches can be reliably detected and named
  // with the canonical `remotes/<remote>/<branch>` form.
  const format = '%(refname)\t%(objectname:short)\t%(subject)\t%(committerdate:iso8601)\t%(upstream:short)\t%(upstream:track)';
  const gitArgs = ['branch', '--format', format];
  if (all) gitArgs.push('-a');

  const list = await git(gitRoot, gitArgs);
  if (list.code !== 0) {
    return gitCommandFailedResult(spec, 'git branch failed.', args, gitRoot, 'branch_list', list, ['git:branch_list']);
  }

  const current = await git(gitRoot, ['rev-parse', '--abbrev-ref', 'HEAD']);
  const currentBranch = current.code === 0 ? current.stdout.trim() : null;

  const branches = parseGitBranchList(list.stdout, currentBranch);

  return resultFor(spec, 'completed', `Listed ${branches.length} branch${branches.length === 1 ? '' : 'es'}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'branch_list',
    current_branch: currentBranch,
    branches
  }, ['git:branch_list']);
}

async function executeGitMerge(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  if (!booleanArg(args, 'confirm_merge')) {
    return resultFor(spec, 'blocked', 'merge requires confirm_merge: true after Core approval.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'merge',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: 'confirm_merge'
    }, ['git:merge', 'approval:supplied', 'confirmation:required']);
  }

  const branch = stringArg(args, 'branch');
  if (!branch) {
    return resultFor(spec, 'blocked', 'merge requires a branch name.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'merge',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_argument: 'branch'
    }, ['git:merge', 'approval:supplied', 'branch:required']);
  }

  if (!isSafeGitRefName(branch)) {
    return resultFor(spec, 'blocked', 'merge branch name is unsafe.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'merge',
      approval_id: request.approval_id,
      branch
    }, ['git:merge', 'approval:supplied', 'branch:blocked']);
  }

  const strategy = stringArg(args, 'strategy');
  const mergeArgs = ['merge'];
  if (strategy === 'no_ff') mergeArgs.push('--no-ff');
  if (strategy === 'ff_only') mergeArgs.push('--ff-only');
  if (strategy === 'squash') mergeArgs.push('--squash');
  mergeArgs.push(branch);

  const merge = await git(gitRoot, mergeArgs, { timeoutMs: 60_000 });
  if (merge.code !== 0) {
    const status = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
    const statusSummary = status.code === 0 ? parseGitStatus(status.stdout) : null;
    const conflictedFiles = statusSummary?.files.filter((f) => f.is_conflicted) ?? [];
    const hasConflicts = conflictedFiles.length > 0;

    if (hasConflicts) {
      return resultFor(spec, 'failed', `Merge conflict when merging ${branch}. Resolve conflicts and commit.`, {
        cwd: request.cwd,
        git_root: gitRoot,
        action: 'merge',
        approval_id: request.approval_id,
        branch,
        strategy,
        conflict: true,
        conflicted_files: conflictedFiles.map((f) => f.path),
        exit_code: merge.code,
        stdout: merge.stdout.trim(),
        stderr: merge.stderr.trim()
      }, ['git:merge', 'approval:supplied', 'confirmation:supplied', 'git:conflict']);
    }

    return gitCommandFailedResult(spec, 'git merge failed.', args, gitRoot, 'merge', merge, ['git:merge']);
  }

  const finalStatus = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  const finalSummary = finalStatus.code === 0 ? parseGitStatus(finalStatus.stdout) : null;

  return resultFor(spec, 'completed', `Merged ${branch}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'merge',
    approval_id: request.approval_id,
    branch,
    strategy,
    conflict: false,
    branch_state: finalSummary?.branch ?? null,
    has_uncommitted_changes: finalSummary?.hasUncommittedChanges ?? null
  }, ['git:merge', 'approval:supplied', 'confirmation:supplied']);
}

async function executeGitRebase(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  if (!booleanArg(args, 'confirm_rebase')) {
    return resultFor(spec, 'blocked', 'rebase requires confirm_rebase: true after Core approval.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'rebase',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: 'confirm_rebase'
    }, ['git:rebase', 'approval:supplied', 'confirmation:required']);
  }

  const operation = stringArg(args, 'operation') ?? 'start';
  if (!['start', 'continue', 'abort', 'skip'].includes(operation)) {
    return failedResult(spec, `Unsupported rebase operation '${operation}'.`, args, ['git:rebase', 'rebase:unsupported-operation']);
  }

  const rebaseArgs = ['rebase'];
  if (operation === 'continue') rebaseArgs.push('--continue');
  else if (operation === 'abort') rebaseArgs.push('--abort');
  else if (operation === 'skip') rebaseArgs.push('--skip');
  else {
    const branch = stringArg(args, 'branch') ?? stringArg(args, 'onto');
    if (!branch) {
      return resultFor(spec, 'blocked', 'rebase start requires a branch or onto argument.', {
        cwd: request.cwd,
        git_root: gitRoot,
        action: 'rebase',
        approval_id: request.approval_id,
        argument_keys: Object.keys(args).sort(),
        required_argument: 'branch'
      }, ['git:rebase', 'approval:supplied', 'branch:required']);
    }
    if (!isSafeGitRefName(branch)) {
      return resultFor(spec, 'blocked', 'rebase branch name is unsafe.', {
        cwd: request.cwd,
        git_root: gitRoot,
        action: 'rebase',
        approval_id: request.approval_id,
        branch
      }, ['git:rebase', 'approval:supplied', 'branch:blocked']);
    }
    rebaseArgs.push(branch);
  }

  const rebase = await git(gitRoot, rebaseArgs, { timeoutMs: 60_000 });
  if (rebase.code !== 0) {
    const status = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
    const statusSummary = status.code === 0 ? parseGitStatus(status.stdout) : null;
    const conflictedFiles = statusSummary?.files.filter((f) => f.is_conflicted) ?? [];
    const hasConflicts = conflictedFiles.length > 0;

    if (hasConflicts) {
      return resultFor(spec, 'failed', 'Rebase paused due to conflicts. Resolve conflicts and run rebase --continue.', {
        cwd: request.cwd,
        git_root: gitRoot,
        action: 'rebase',
        approval_id: request.approval_id,
        operation,
        conflict: true,
        conflicted_files: conflictedFiles.map((f) => f.path),
        exit_code: rebase.code,
        stdout: rebase.stdout.trim(),
        stderr: rebase.stderr.trim()
      }, ['git:rebase', 'approval:supplied', 'confirmation:supplied', 'git:conflict']);
    }

    return gitCommandFailedResult(spec, 'git rebase failed.', args, gitRoot, 'rebase', rebase, ['git:rebase']);
  }

  const finalStatus = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  const finalSummary = finalStatus.code === 0 ? parseGitStatus(finalStatus.stdout) : null;

  return resultFor(spec, 'completed', `Rebase ${operation} completed.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'rebase',
    approval_id: request.approval_id,
    operation,
    conflict: false,
    branch_state: finalSummary?.branch ?? null,
    has_uncommitted_changes: finalSummary?.hasUncommittedChanges ?? null
  }, ['git:rebase', 'approval:supplied', 'confirmation:supplied']);
}

async function executeGitResolveConflict(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  if (!booleanArg(args, 'confirm_resolve')) {
    return resultFor(spec, 'blocked', 'resolve_conflict requires confirm_resolve: true after Core approval.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'resolve_conflict',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: 'confirm_resolve'
    }, ['git:resolve_conflict', 'approval:supplied', 'confirmation:required']);
  }

  const rawPath = stringArg(args, 'path');
  if (!rawPath) {
    return resultFor(spec, 'blocked', 'resolve_conflict requires a path.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'resolve_conflict',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_argument: 'path'
    }, ['git:resolve_conflict', 'approval:supplied', 'path:required']);
  }

  const normalized = normalizeGitPathspec(gitRoot, rawPath);
  if (!normalized.ok) {
    return resultFor(spec, 'blocked', normalized.message, {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'resolve_conflict',
      approval_id: request.approval_id,
      path: rawPath
    }, ['git:resolve_conflict', 'approval:supplied', 'path:blocked']);
  }

  const strategy = stringArg(args, 'strategy');
  if (!strategy || !['ours', 'theirs', 'both'].includes(strategy)) {
    return resultFor(spec, 'blocked', "resolve_conflict strategy must be 'ours', 'theirs', or 'both'.", {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'resolve_conflict',
      approval_id: request.approval_id,
      path: rawPath,
      strategy
    }, ['git:resolve_conflict', 'approval:supplied', 'strategy:required']);
  }

  if (strategy === 'both') {
    const absolutePath = path.resolve(gitRoot, rawPath);
    let content: string;
    try {
      content = await readFile(absolutePath, 'utf8');
    } catch (error) {
      return failedResult(spec, error instanceof Error ? error.message : String(error), args, ['git:resolve_conflict', 'read:error']);
    }
    // Accept both sides: keep content from both conflict blocks.
    const resolved = content
      .replace(/<<<<<<< [^\n]*\n/g, '')
      .replace(/=======\n/g, '')
      .replace(/>>>>>>> [^\n]*\n/g, '');
    await writeFile(absolutePath, resolved, 'utf8');
  } else {
    const checkout = await git(gitRoot, ['checkout', `--${strategy}`, '--', normalized.pathspec]);
    if (checkout.code !== 0) {
      return gitCommandFailedResult(spec, `git checkout --${strategy} failed.`, args, gitRoot, 'resolve_conflict', checkout, ['git:resolve_conflict']);
    }
  }

  const add = await git(gitRoot, ['add', '--', normalized.pathspec]);
  if (add.code !== 0) {
    return gitCommandFailedResult(spec, 'git add failed after conflict resolution.', args, gitRoot, 'resolve_conflict', add, ['git:resolve_conflict', 'git:add']);
  }

  const status = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  const statusSummary = status.code === 0 ? parseGitStatus(status.stdout) : null;
  const remainingConflicts = statusSummary?.files.filter((f) => f.is_conflicted) ?? [];

  return resultFor(spec, 'completed', `Resolved ${rawPath} using ${strategy}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'resolve_conflict',
    approval_id: request.approval_id,
    path: rawPath,
    strategy,
    remaining_conflicts: remainingConflicts.map((f) => f.path),
    all_resolved: remainingConflicts.length === 0,
    branch: statusSummary?.branch ?? null
  }, ['git:resolve_conflict', 'approval:supplied', 'confirmation:supplied']);
}

async function executeGitDeleteBranch(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  if (!booleanArg(args, 'confirm_delete_branch')) {
    return resultFor(spec, 'blocked', 'delete_branch requires confirm_delete_branch: true after Core approval.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'delete_branch',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: 'confirm_delete_branch'
    }, ['git:delete_branch', 'approval:supplied', 'confirmation:required']);
  }

  const branch = stringArg(args, 'branch');
  if (!branch) {
    return resultFor(spec, 'blocked', 'delete_branch requires a branch name.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'delete_branch',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_argument: 'branch'
    }, ['git:delete_branch', 'approval:supplied', 'branch:required']);
  }

  if (!isSafeGitRefName(branch)) {
    return resultFor(spec, 'blocked', 'delete_branch branch name is unsafe.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'delete_branch',
      approval_id: request.approval_id,
      branch
    }, ['git:delete_branch', 'approval:supplied', 'branch:blocked']);
  }

  const current = await git(gitRoot, ['rev-parse', '--abbrev-ref', 'HEAD']);
  if (current.code === 0 && current.stdout.trim() === branch) {
    return resultFor(spec, 'blocked', `Cannot delete the current branch '${branch}'.`, {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'delete_branch',
      approval_id: request.approval_id,
      branch
    }, ['git:delete_branch', 'approval:supplied', 'branch:current']);
  }

  const force = booleanArg(args, 'force');
  const deleteArgs = ['branch', force ? '-D' : '-d', branch];
  const deleted = await git(gitRoot, deleteArgs);
  if (deleted.code !== 0) {
    return gitCommandFailedResult(spec, 'git branch delete failed.', args, gitRoot, 'delete_branch', deleted, ['git:delete_branch']);
  }

  return resultFor(spec, 'completed', `Deleted branch ${branch}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'delete_branch',
    approval_id: request.approval_id,
    branch,
    force
  }, ['git:delete_branch', 'approval:supplied', 'confirmation:supplied']);
}

async function executeGitRenameBranch(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  if (!booleanArg(args, 'confirm_rename_branch')) {
    return resultFor(spec, 'blocked', 'rename_branch requires confirm_rename_branch: true after Core approval.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'rename_branch',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_confirmation: 'confirm_rename_branch'
    }, ['git:rename_branch', 'approval:supplied', 'confirmation:required']);
  }

  const newName = stringArg(args, 'new_name');
  if (!newName) {
    return resultFor(spec, 'blocked', 'rename_branch requires a new_name.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'rename_branch',
      approval_id: request.approval_id,
      argument_keys: Object.keys(args).sort(),
      required_argument: 'new_name'
    }, ['git:rename_branch', 'approval:supplied', 'new_name:required']);
  }

  if (!isSafeGitRefName(newName)) {
    return resultFor(spec, 'blocked', 'rename_branch new_name is unsafe.', {
      cwd: request.cwd,
      git_root: gitRoot,
      action: 'rename_branch',
      approval_id: request.approval_id,
      new_name: newName
    }, ['git:rename_branch', 'approval:supplied', 'branch:blocked']);
  }

  const renamed = await git(gitRoot, ['branch', '-m', newName]);
  if (renamed.code !== 0) {
    return gitCommandFailedResult(spec, 'git branch rename failed.', args, gitRoot, 'rename_branch', renamed, ['git:rename_branch']);
  }

  const finalStatus = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  const finalSummary = finalStatus.code === 0 ? parseGitStatus(finalStatus.stdout) : null;

  return resultFor(spec, 'completed', `Renamed branch to ${newName}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'rename_branch',
    approval_id: request.approval_id,
    new_name: newName,
    branch: finalSummary?.branch ?? newName
  }, ['git:rename_branch', 'approval:supplied', 'confirmation:supplied']);
}

/**
 * diff_compare: diff between any two refs (commit/branch/tag).
 * Args: base_ref, head_ref, paths? (optional filter)
 */
async function executeGitDiffCompare(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  const baseRef = stringArg(args, 'base_ref');
  const headRef = stringArg(args, 'head_ref');
  if (!baseRef || !headRef) {
    return failedResult(spec, 'diff_compare requires "base_ref" and "head_ref" arguments.', args, ['git:diff_compare:missing-refs']);
  }

  const maxDiffBytes = Number(args.max_diff_bytes ?? 180000);
  const paths = Array.isArray(args.paths) ? args.paths.filter((p): p is string => typeof p === 'string') : null;

  const diffArgs = ['diff', `${baseRef}...${headRef}`, '--stat', '--numstat', '--name-status'];
  if (paths && paths.length > 0) {
    diffArgs.push('--');
    for (const p of paths) diffArgs.push(p);
  }

  const [statResult, numstatResult, nameStatusResult, diffResult, logResult] = await Promise.all([
    git(gitRoot, ['diff', `${baseRef}...${headRef}`, '--stat']),
    git(gitRoot, ['diff', `${baseRef}...${headRef}`, '--numstat']),
    git(gitRoot, ['diff', `${baseRef}...${headRef}`, '--name-status']),
    git(gitRoot, ['diff', `${baseRef}...${headRef}`]),
    git(gitRoot, ['log', '--oneline', `${baseRef}...${headRef}`])
  ]);

  let diffText = diffResult.stdout;
  let truncated = false;
  if (diffText.length > maxDiffBytes) {
    diffText = diffText.slice(0, maxDiffBytes);
    truncated = true;
  }

  const files = mergeDiffFileMetadata(parseGitNumstat(numstatResult.stdout), parseGitNameStatus(nameStatusResult.stdout));

  return resultFor(spec, 'completed', `Diff ${baseRef}...${headRef} (${files.length} files, ${logResult.stdout ? logResult.stdout.split('\n').filter(Boolean).length : 0} commits).`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'diff_compare',
    base_ref: baseRef,
    head_ref: headRef,
    diff_stat: statResult.stdout || null,
    diff: diffText || null,
    truncated,
    files,
    file_count: files.length,
    commits: logResult.stdout ? logResult.stdout.split('\n').filter(Boolean) : []
  }, ['git:diff_compare', `refs:${baseRef}...${headRef}`]);
}

/**
 * log: git log with configurable limit and format.
 * Args: limit? (default 20), ref? (optional ref/range)
 */
async function executeGitLog(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  const limit = Math.min(Math.max(Number(args.limit ?? 20), 1), 200);
  const ref = stringArg(args, 'ref');

  const logArgs = ['log', `--format=%H|%h|%an|%ae|%ad|%s`, '--date=iso', `-${limit}`];
  if (ref) {
    logArgs.push(ref);
  }

  const logResult = await git(gitRoot, logArgs);
  if (logResult.code !== 0) {
    return failedResult(spec, `git log failed: ${logResult.stderr}`, args, ['git:log:error']);
  }

  const commits = logResult.stdout
    .split('\n')
    .filter((line) => line.trim().length > 0)
    .map((line) => {
      const [hash, shortHash, author, email, date, ...subjectParts] = line.split('|');
      return {
        hash: hash ?? '',
        short_hash: shortHash ?? '',
        author: author ?? '',
        email: email ?? '',
        date: date ?? '',
        subject: subjectParts.join('|') ?? ''
      };
    });

  return resultFor(spec, 'completed', `Retrieved ${commits.length} commits.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'log',
    ref: ref ?? 'HEAD',
    limit,
    commits
  }, ['git:log']);
}

async function executeGitDiffPreview(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest,
  args: Record<string, unknown>,
  gitRoot: string
): Promise<CodeToolExecuteResult> {
  const target = stringArg(args, 'target') ?? 'all';
  const allowedTargets = new Set(['all', 'working_tree', 'staged', 'branch_range']);
  if (!allowedTargets.has(target)) {
    return failedResult(spec, `Unsupported diff_preview target '${target}'.`, args, ['git:diff-preview', 'git:unsupported-target']);
  }

  const maxFiles = clampNumber(numberArg(args, 'max_files') ?? 64, 1, 250);
  const maxDiffBytes = clampNumber(numberArg(args, 'max_diff_bytes') ?? 120_000, 4_000, 1_000_000);
  const includeUntracked = args.include_untracked !== false;
  const status = await git(gitRoot, ['status', '--porcelain=v1', '--branch']);
  if (status.code !== 0) {
    return gitCommandFailedResult(spec, 'Unable to inspect Git status for diff preview.', args, gitRoot, 'diff_preview', status, ['git:diff-preview', 'git:status']);
  }

  const statusSummary = parseGitStatus(status.stdout);
  const sections: GitDiffPreviewSection[] = [];

  if (target === 'all' || target === 'working_tree') {
    const workingTree = await buildDiffPreviewSection(gitRoot, {
      id: 'working_tree',
      kind: 'working_tree',
      title: 'Working Tree',
      subtitle: 'Tracked and untracked workspace changes',
      diffArgs: ['diff', '-M', '--no-ext-diff', '--no-color', '--src-prefix=a/', '--dst-prefix=b/'],
      numstatArgs: ['diff', '-M', '--numstat', '-z'],
      nameStatusArgs: ['diff', '-M', '--name-status', '-z'],
      untrackedFiles: includeUntracked
        ? statusSummary.files.filter((file) => file.is_untracked).map((file) => file.path)
        : [],
      maxFiles,
      maxDiffBytes
    });
    if (workingTree.file_count > 0 || workingTree.notices.length > 0) {
      sections.push(workingTree);
    }
  }

  if (target === 'all' || target === 'staged') {
    const staged = await buildDiffPreviewSection(gitRoot, {
      id: 'staged',
      kind: 'staged',
      title: 'Staged',
      subtitle: 'Index changes ready for commit',
      diffArgs: ['diff', '--cached', '-M', '--no-ext-diff', '--no-color', '--src-prefix=a/', '--dst-prefix=b/'],
      numstatArgs: ['diff', '--cached', '-M', '--numstat', '-z'],
      nameStatusArgs: ['diff', '--cached', '-M', '--name-status', '-z'],
      untrackedFiles: [],
      maxFiles,
      maxDiffBytes
    });
    if (staged.file_count > 0 || staged.notices.length > 0) {
      sections.push(staged);
    }
  }

  if (target === 'all' || target === 'branch_range') {
    const baseRef = stringArg(args, 'base_ref') ?? defaultBaseRef(statusSummary);
    const headRef = stringArg(args, 'head_ref') ?? 'HEAD';
    if (!baseRef) {
      if (target === 'branch_range') {
        sections.push(emptyNoticeSection('branch_range', 'Branch Range', 'Base branch unavailable', null, headRef, ['No upstream or base_ref is available for branch range diff.']));
      }
    } else {
      const branchRange = await buildDiffPreviewSection(gitRoot, {
        id: 'branch_range',
        kind: 'branch_range',
        title: 'Branch Range',
        subtitle: `${baseRef} ... ${headRef}`,
        baseRef,
        headRef,
        diffArgs: ['diff', '-M', '--no-ext-diff', '--no-color', '--src-prefix=a/', '--dst-prefix=b/', `${baseRef}...${headRef}`],
        numstatArgs: ['diff', '-M', '--numstat', '-z', `${baseRef}...${headRef}`],
        nameStatusArgs: ['diff', '-M', '--name-status', '-z', `${baseRef}...${headRef}`],
        untrackedFiles: [],
        maxFiles,
        maxDiffBytes
      });
      if (branchRange.file_count > 0 || branchRange.notices.length > 0 || target === 'branch_range') {
        sections.push(branchRange);
      }
    }
  }

  const totalFiles = sections.reduce((count, section) => count + section.file_count, 0);
  return resultFor(spec, 'completed', totalFiles > 0 ? `Prepared ${totalFiles} Git diff file previews.` : `No Git diff changes for ${statusSummary.branch}.`, {
    cwd: request.cwd,
    git_root: gitRoot,
    action: 'diff_preview',
    target,
    branch: statusSummary.branch,
    upstream: statusSummary.upstream,
    ahead: statusSummary.ahead,
    behind: statusSummary.behind,
    has_uncommitted_changes: statusSummary.hasUncommittedChanges,
    files: statusSummary.files,
    sections,
    max_files: maxFiles,
    max_diff_bytes: maxDiffBytes
  }, ['git:diff-preview', 'git:status']);
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
  files: GitStatusFile[];
}

interface GitStatusFile {
  path: string;
  previous_path: string | null;
  staged_status: string;
  unstaged_status: string;
  status: string;
  is_untracked: boolean;
  is_conflicted: boolean;
  is_renamed: boolean;
}

interface GitDiffFileSummary {
  path: string;
  previous_path: string | null;
  change_type: string;
  additions: number;
  deletions: number;
  binary: boolean;
  truncated: boolean;
  preview?: string;
}

interface GitDiffPreviewSection {
  id: string;
  kind: 'working_tree' | 'staged' | 'branch_range';
  title: string;
  subtitle: string | null;
  base_ref: string | null;
  head_ref: string | null;
  diff: string;
  files: GitDiffFileSummary[];
  file_count: number;
  additions: number;
  deletions: number;
  notices: string[];
}

interface GitDiffPreviewBuildOptions {
  id: string;
  kind: GitDiffPreviewSection['kind'];
  title: string;
  subtitle: string | null;
  baseRef?: string | null;
  headRef?: string | null;
  diffArgs: string[];
  numstatArgs: string[];
  nameStatusArgs: string[];
  untrackedFiles: string[];
  maxFiles: number;
  maxDiffBytes: number;
}

async function git(
  cwd: string,
  args: string[],
  options?: { input?: string; timeoutMs?: number }
): Promise<GitCommandResult> {
  return new Promise((resolve) => {
    const child = spawn('git', args, {
      cwd,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true
    });

    let stdout = '';
    let stderr = '';
    const timeoutMs = options?.timeoutMs ?? 10_000;
    const timeout = setTimeout(() => {
      child.kill();
      resolve({ code: -1, stdout, stderr: stderr || 'git command timed out' });
    }, timeoutMs);

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
    if (options?.input !== undefined) {
      child.stdin.write(options.input);
      child.stdin.end();
    } else {
      child.stdin.end();
    }
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
    changedEntries,
    files: changedEntries.map(parseGitStatusFile)
  };
}

function parseGitStatusFile(line: string): GitStatusFile {
  const stagedCode = line[0] ?? ' ';
  const unstagedCode = line[1] ?? ' ';
  const rawPath = line.slice(3);
  const renameParts = rawPath.split(' -> ');
  const isRenamed = renameParts.length === 2 || stagedCode === 'R' || unstagedCode === 'R';
  const previousPath = isRenamed && renameParts.length === 2 ? renameParts[0] : null;
  const filePath = isRenamed && renameParts.length === 2 ? renameParts[1] : rawPath;
  const isConflicted = stagedCode === 'U' || unstagedCode === 'U' || ['AA', 'DD', 'AU', 'UA', 'DU', 'UD'].includes(`${stagedCode}${unstagedCode}`);
  const isUntracked = stagedCode === '?' && unstagedCode === '?';

  return {
    path: filePath,
    previous_path: previousPath,
    staged_status: gitStatusCodeLabel(stagedCode),
    unstaged_status: gitStatusCodeLabel(unstagedCode),
    status: gitCombinedStatusLabel(stagedCode, unstagedCode),
    is_untracked: isUntracked,
    is_conflicted: isConflicted,
    is_renamed: isRenamed
  };
}

function gitStatusCodeLabel(code: string): string {
  switch (code) {
    case 'A': return 'added';
    case 'M': return 'modified';
    case 'D': return 'deleted';
    case 'R': return 'renamed';
    case 'C': return 'copied';
    case 'U': return 'unmerged';
    case '?': return 'untracked';
    case '!': return 'ignored';
    case ' ': return 'clean';
    default: return code === '' ? 'clean' : code;
  }
}

function gitCombinedStatusLabel(stagedCode: string, unstagedCode: string): string {
  if (stagedCode === '?' && unstagedCode === '?') return 'untracked';
  if (stagedCode === 'U' || unstagedCode === 'U') return 'conflicted';
  if (stagedCode === 'R' || unstagedCode === 'R') return 'renamed';
  if (stagedCode !== ' ' && unstagedCode !== ' ') return 'staged_and_modified';
  if (stagedCode !== ' ') return `staged_${gitStatusCodeLabel(stagedCode)}`;
  if (unstagedCode !== ' ') return gitStatusCodeLabel(unstagedCode);
  return 'clean';
}

async function buildDiffPreviewSection(
  gitRoot: string,
  options: GitDiffPreviewBuildOptions
): Promise<GitDiffPreviewSection> {
  const notices: string[] = [];
  const [diff, numstat, nameStatus] = await Promise.all([
    git(gitRoot, options.diffArgs),
    git(gitRoot, options.numstatArgs),
    git(gitRoot, options.nameStatusArgs)
  ]);

  if (diff.code !== 0) {
    notices.push(diff.stderr || `git ${options.diffArgs.join(' ')} failed.`);
  }
  if (numstat.code !== 0) {
    notices.push(numstat.stderr || `git ${options.numstatArgs.join(' ')} failed.`);
  }
  if (nameStatus.code !== 0) {
    notices.push(nameStatus.stderr || `git ${options.nameStatusArgs.join(' ')} failed.`);
  }

  let diffText = diff.code === 0 ? diff.stdout : '';
  const trackedFiles = mergeDiffFileMetadata(
    parseGitNumstat(numstat.code === 0 ? numstat.stdout : ''),
    parseGitNameStatus(nameStatus.code === 0 ? nameStatus.stdout : '')
  );
  const untrackedFiles = await summarizeUntrackedFiles(gitRoot, options.untrackedFiles, options.maxDiffBytes);
  let files = [...trackedFiles, ...untrackedFiles];

  if (files.length > options.maxFiles) {
    notices.push(`Showing ${options.maxFiles} of ${files.length} changed files.`);
    files = files.slice(0, options.maxFiles);
  }

  const untrackedDiff = untrackedFiles
    .slice(0, Math.max(0, options.maxFiles - trackedFiles.length))
    .map((file) => file.binary ? `Binary file ${file.path} is untracked\n` : buildUntrackedDiffNotice(file))
    .join('');
  if (untrackedDiff.length > 0) {
    diffText = diffText.length > 0 ? `${diffText}${diffText.endsWith('\n') ? '' : '\n'}${untrackedDiff}` : untrackedDiff;
  }

  const truncatedDiff = truncateTextByBytes(diffText, options.maxDiffBytes);
  if (truncatedDiff.truncated) {
    notices.push(`Diff text truncated to ${options.maxDiffBytes} bytes.`);
  }

  const additions = files.reduce((sum, file) => sum + file.additions, 0);
  const deletions = files.reduce((sum, file) => sum + file.deletions, 0);
  return {
    id: options.id,
    kind: options.kind,
    title: options.title,
    subtitle: options.subtitle,
    base_ref: options.baseRef ?? null,
    head_ref: options.headRef ?? null,
    diff: truncatedDiff.text,
    files,
    file_count: files.length,
    additions,
    deletions,
    notices
  };
}

function parseGitNumstat(output: string): GitDiffFileSummary[] {
  const tokens = splitNul(output);
  const files: GitDiffFileSummary[] = [];
  for (let index = 0; index < tokens.length; index++) {
    const token = tokens[index];
    const parts = token.split('\t');
    if (parts.length < 3) {
      continue;
    }

    const [additionsToken, deletionsToken, inlinePath] = parts;
    let pathToken = inlinePath;
    let previousPath: string | null = null;
    if (inlinePath === '' && index + 2 < tokens.length) {
      previousPath = tokens[++index] ?? null;
      pathToken = tokens[++index] ?? '';
    }
    if (!pathToken) continue;

    const additions = additionsToken === '-' ? 0 : Number(additionsToken);
    const deletions = deletionsToken === '-' ? 0 : Number(deletionsToken);
    files.push({
      path: pathToken,
      previous_path: previousPath,
      change_type: 'modified',
      additions: Number.isFinite(additions) ? additions : 0,
      deletions: Number.isFinite(deletions) ? deletions : 0,
      binary: additionsToken === '-' || deletionsToken === '-',
      truncated: false
    });
  }
  return files;
}

function parseGitNameStatus(output: string): Map<string, { change_type: string; previous_path: string | null }> {
  const tokens = splitNul(output);
  const map = new Map<string, { change_type: string; previous_path: string | null }>();
  for (let index = 0; index < tokens.length;) {
    const code = tokens[index++];
    if (!code) break;

    const changeCode = code[0] ?? 'M';
    if (changeCode === 'R' || changeCode === 'C') {
      const previousPath = tokens[index++] ?? '';
      const filePath = tokens[index++] ?? '';
      if (filePath) {
        map.set(filePath, { change_type: gitNameStatusLabel(changeCode), previous_path: previousPath || null });
      }
      continue;
    }

    const filePath = tokens[index++] ?? '';
    if (filePath) {
      map.set(filePath, { change_type: gitNameStatusLabel(changeCode), previous_path: null });
    }
  }
  return map;
}

function mergeDiffFileMetadata(
  numstatFiles: GitDiffFileSummary[],
  nameStatuses: Map<string, { change_type: string; previous_path: string | null }>
): GitDiffFileSummary[] {
  const byPath = new Map(numstatFiles.map((file) => [file.path, file]));
  for (const [filePath, status] of nameStatuses.entries()) {
    const existing = byPath.get(filePath);
    if (existing) {
      existing.change_type = status.change_type;
      existing.previous_path = status.previous_path ?? existing.previous_path;
      continue;
    }

    byPath.set(filePath, {
      path: filePath,
      previous_path: status.previous_path,
      change_type: status.change_type,
      additions: 0,
      deletions: 0,
      binary: false,
      truncated: false
    });
  }
  return [...byPath.values()];
}

function gitNameStatusLabel(code: string): string {
  switch (code) {
    case 'A': return 'added';
    case 'D': return 'deleted';
    case 'R': return 'renamed';
    case 'C': return 'copied';
    case 'T': return 'type_changed';
    case 'U': return 'conflicted';
    default: return 'modified';
  }
}

async function summarizeUntrackedFiles(
  gitRoot: string,
  rawPaths: string[],
  maxDiffBytes: number
): Promise<GitDiffFileSummary[]> {
  const files: GitDiffFileSummary[] = [];
  for (const rawPath of rawPaths) {
    const normalized = normalizeGitPathspec(gitRoot, rawPath);
    if (!normalized.ok) {
      continue;
    }

    const absolutePath = path.resolve(gitRoot, rawPath);
    try {
      const fileStat = await stat(absolutePath);
      if (!fileStat.isFile()) {
        continue;
      }

      const sample = await readFile(absolutePath);
      const binary = sample.subarray(0, Math.min(sample.length, 8_000)).includes(0);
      const text = binary ? '' : sample.toString('utf8');
      const lineCount = binary ? 0 : text.split(/\r?\n/).filter((line) => line.length > 0).length;
      files.push({
        path: rawPath,
        previous_path: null,
        change_type: 'untracked',
        additions: lineCount,
        deletions: 0,
        binary,
        truncated: sample.byteLength > maxDiffBytes,
        preview: binary ? undefined : truncateTextByBytes(text, Math.min(maxDiffBytes, 40_000)).text
      });
    } catch {
      files.push({
        path: rawPath,
        previous_path: null,
        change_type: 'untracked',
        additions: 0,
        deletions: 0,
        binary: false,
        truncated: false,
        preview: undefined
      });
    }
  }
  return files;
}

function buildUntrackedDiffNotice(file: GitDiffFileSummary): string {
  const lines = (file.preview ?? '[untracked file preview unavailable]\n')
    .split(/\r?\n/)
    .map((line) => `+${line}`)
    .join('\n');
  return [
    `diff --git a/${file.path} b/${file.path}`,
    'new file mode 100644',
    '--- /dev/null',
    `+++ b/${file.path}`,
    `@@ -0,0 +1,${Math.max(file.additions, 1)} @@`,
    lines,
    file.truncated ? '+[untracked file preview truncated]' : null,
    ''
  ].filter((line): line is string => line !== null).join('\n');
}

function emptyNoticeSection(
  kind: GitDiffPreviewSection['kind'],
  title: string,
  subtitle: string | null,
  baseRef: string | null,
  headRef: string | null,
  notices: string[]
): GitDiffPreviewSection {
  return {
    id: kind,
    kind,
    title,
    subtitle,
    base_ref: baseRef,
    head_ref: headRef,
    diff: '',
    files: [],
    file_count: 0,
    additions: 0,
    deletions: 0,
    notices
  };
}

function splitNul(output: string): string[] {
  return output.split('\0').filter((token) => token.length > 0);
}

function truncateTextByBytes(value: string, maxBytes: number): { text: string; truncated: boolean } {
  if (Buffer.byteLength(value, 'utf8') <= maxBytes) {
    return { text: value, truncated: false };
  }

  let used = 0;
  let output = '';
  for (const char of value) {
    const size = Buffer.byteLength(char, 'utf8');
    if (used + size > maxBytes) {
      break;
    }
    output += char;
    used += size;
  }
  return { text: `${output}\n[diff truncated]\n`, truncated: true };
}

function defaultBaseRef(status: GitStatusSummary): string | null {
  if (status.upstream) {
    return status.upstream;
  }
  return null;
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

interface GitBranchEntry {
  name: string;
  is_current: boolean;
  is_remote: boolean;
  commit_hash: string | null;
  subject: string | null;
  last_commit_date: string | null;
  upstream: string | null;
  ahead: number;
  behind: number;
}

function parseGitBranchList(output: string, currentBranch: string | null): GitBranchEntry[] {
  const branches: GitBranchEntry[] = [];
  for (const line of nonEmptyLines(output)) {
    const parts = line.split('\t');
    const [rawName, commitHash, subject, lastCommitDate, upstream, track] = parts;
    if (!rawName) continue;
    const isRemote = rawName.startsWith('refs/remotes/');
    const name = isRemote
      ? rawName.slice('refs/'.length)
      : rawName.startsWith('refs/heads/')
        ? rawName.slice('refs/heads/'.length)
        : rawName;
    const ahead = Number(track?.match(/ahead\s+(\d+)/)?.[1] ?? 0);
    const behind = Number(track?.match(/behind\s+(\d+)/)?.[1] ?? 0);
    branches.push({
      name,
      is_current: name === currentBranch,
      is_remote: isRemote,
      commit_hash: commitHash ?? null,
      subject: subject ?? null,
      last_commit_date: lastCommitDate ?? null,
      upstream: upstream ?? null,
      ahead,
      behind
    });
  }
  return branches;
}

interface BranchTrackingInfo {
  branch: string;
  upstream: string | null;
  ahead: number;
  behind: number;
}

function parseBranchTrackingInfo(output: string): BranchTrackingInfo[] {
  const entries: BranchTrackingInfo[] = [];
  for (const line of nonEmptyLines(output)) {
    const parts = line.split('\t');
    if (parts.length < 3) continue;
    const [branch, upstream, track] = parts;
    const ahead = Number(track?.match(/ahead\s+(\d+)/)?.[1] ?? 0);
    const behind = Number(track?.match(/behind\s+(\d+)/)?.[1] ?? 0);
    entries.push({ branch, upstream: upstream || null, ahead, behind });
  }
  return entries;
}

function pushReadinessBlockers(status: GitStatusSummary): string[] {
  const blockers: string[] = [];
  if (status.branch === 'HEAD' || status.branch.startsWith('HEAD ') || status.branch.includes('detached')) {
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

function booleanArg(args: Record<string, unknown>, key: string, defaultValue = false): boolean {
  if (args[key] === undefined || args[key] === null) {
    return defaultValue;
  }
  return args[key] === true;
}

function numberArg(args: Record<string, unknown>, key: string): number | null {
  const value = args[key];
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }
  if (typeof value === 'string' && value.trim().length > 0) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function clampNumber(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, Math.trunc(value)));
}

function stringListArg(args: Record<string, unknown>, key: string): string[] {
  const value = args[key];
  if (typeof value === 'string') {
    const trimmed = value.trim();
    return trimmed.length > 0 ? [trimmed] : [];
  }
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .filter((entry): entry is string => typeof entry === 'string')
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0);
}

function normalizeGitPathspec(
  gitRoot: string,
  value: string
): { ok: true; pathspec: string } | { ok: false; message: string } {
  if (value.includes('\0')) {
    return { ok: false, message: 'Git paths must not contain NUL characters.' };
  }

  const candidate = path.isAbsolute(value) ? path.resolve(value) : path.resolve(gitRoot, value);
  if (!isInside(gitRoot, candidate)) {
    return { ok: false, message: 'Git paths must stay inside the Git worktree.' };
  }

  const relative = path.relative(gitRoot, candidate);
  if (!relative || relative === '.') {
    return { ok: false, message: 'Git paths must name files or directories inside the Git worktree.' };
  }

  return { ok: true, pathspec: `:(literal)${relative.split(path.sep).join('/')}` };
}

function isSafeGitRefName(value: string): boolean {
  return value.length > 0 && !value.startsWith('-') && !/[\s\0]/.test(value);
}

function gitCommandFailedResult(
  spec: CodeToolSpec,
  summary: string,
  args: Record<string, unknown>,
  gitRoot: string,
  action: string,
  command: GitCommandResult,
  extraEvidence: string[] = []
): CodeToolExecuteResult {
  return resultFor(spec, 'failed', summary, {
    git_root: gitRoot,
    action,
    argument_keys: Object.keys(args).sort(),
    exit_code: command.code,
    stdout: command.stdout.trim(),
    stderr: command.stderr.trim()
  }, extraEvidence);
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
