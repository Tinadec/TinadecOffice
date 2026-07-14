/**
 * Code Tool 规格、审批验证与 Tool Runtime 代理。
 *
 * Gateway 不直接执行文件、Git、Shell、PTY 或 MCP 操作。
 * 本模块仅提供：
 *   1. 工具规格定义（用于 BFF 组合：/api/v1/code/tools）
 *   2. Core 审批状态验证（approval gate）
 *   3. 通过 toolRuntimeClient 将执行请求代理到 Tool Runtime
 */

import path from 'node:path';
import { proxyToolRuntimeJson } from './toolRuntimeClient.js';

export interface CodeToolExecuteRequest {
  session_id?: string | null;
  run_id?: string | null;
  task_node_id?: string | null;
  approval_id?: string | null;
  cwd?: string | null;
  arguments?: Record<string, unknown> | null;
  /** 人类操作携带的审批标志 */
  approval?: boolean;
  /** 高风险命令的二次确认标志 */
  confirmation?: boolean;
  /** 请求来源：human 或 agent */
  source?: string;
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

/**
 * 通过 Tool Runtime 执行 Code 工具。
 * Gateway 不直接执行任何工具，所有执行请求都代理到 Tool Runtime。
 */
export async function executeCodeToolViaRuntime(
  toolId: string,
  request: CodeToolExecuteRequest
): Promise<CodeToolExecuteResult | null> {
  const spec = TOOL_SPECS[toolId];
  if (!spec) {
    return null;
  }

  const result = await proxyToolRuntimeJson(`/api/v1/tools/${encodeURIComponent(toolId)}/execute`, {
    method: 'POST',
    body: request as Record<string, unknown>,
  });

  if (result.status >= 200 && result.status < 300) {
    return result.data as CodeToolExecuteResult;
  }

  return resultFor(spec, 'failed', `Tool Runtime returned status ${result.status}.`, {
    tool_id: toolId,
    runtime_status: result.status,
    runtime_error: result.data,
  }, ['tool_runtime:proxy-error']);
}

// --- Helper functions ---

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

function allowedApprovalKindsForTool(toolId: string): string[] {
  if (toolId === 'git_worktree_manager' || toolId.startsWith('git_')) {
    return ['git'];
  }
  return ['code', 'tool', toolId];
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

function stringArg(args: Record<string, unknown>, key: string): string | null {
  const value = args[key];
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
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
