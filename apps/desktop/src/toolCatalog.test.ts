import { describe, expect, it } from 'vitest';
import type { CodeToolExecuteResultDto, HarnessManifestDto, ToolDescriptorDto, ToolSearchResultDto } from './api';
import {
  codeSuiteTools,
  languageSupportFromTools,
  manifestTools,
  projectTemplatesFromResult,
  sortedAgentLayers,
  sortedRiskPolicies,
  sortedToolSearchResults,
  sortedToolProviders
} from './toolCatalog';

const tools: ToolDescriptorDto[] = [
  {
    id: 'language_runtime_probe',
    display_name: 'Language Runtime Probe',
    domain: 'programming',
    source: 'code',
    risk: 'read-only',
    requires_approval: false,
    execute_endpoint: '/api/v1/code/tools/language_runtime_probe/execute',
    capabilities: ['runtime.probe', 'runtime.nodejs', 'runtime.rust', 'runtime.python']
  },
  {
    id: 'git_worktree_manager',
    display_name: 'Git Worktree Manager',
    domain: 'programming',
    source: 'code',
    risk: 'git-write',
    requires_approval: true,
    execute_endpoint: '/api/v1/code/tools/git_worktree_manager/execute',
    capabilities: ['git.status', 'git.diff', 'git.stage', 'git.unstage', 'git.worktree', 'git.commit', 'git.push', 'workspace.isolation']
  },
  {
    id: 'read_file',
    display_name: 'Read File',
    domain: 'programming',
    source: 'codex-rust',
    risk: 'read-only',
    requires_approval: false,
    execute_endpoint: '/api/v1/code/tools/read_file/execute',
    capabilities: ['file.read']
  }
];

describe('tool catalog helpers', () => {
  it('extracts language runtime support from tool capabilities', () => {
    expect(languageSupportFromTools(tools)).toEqual(['nodejs', 'python', 'rust']);
  });

  it('keeps Code suite tools separate from Codex primitive tools', () => {
    expect(codeSuiteTools(tools).map((tool) => tool.id)).toEqual([
      'git_worktree_manager',
      'language_runtime_probe'
    ]);
  });

  it('extracts project template summaries from Code tool results', () => {
    const result: CodeToolExecuteResultDto = {
      tool_id: 'project_templates',
      status: 'completed',
      summary: 'Templates',
      evidence: [],
      requires_approval: false,
      approval_summary: null,
      data: {
        templates: [
          { id: 'rust-cli', name: 'Rust CLI', language: 'rust', package_manager: 'cargo' },
          { id: 'nodejs-vite-vue', name: 'Node.js Vite Vue', language: 'nodejs', package_manager: 'npm' },
          { id: 123, name: 'Invalid', language: 'bad', package_manager: 'bad' }
        ]
      }
    };

    expect(projectTemplatesFromResult(result)).toEqual([
      { id: 'nodejs-vite-vue', name: 'Node.js Vite Vue', language: 'nodejs', package_manager: 'npm' },
      { id: 'rust-cli', name: 'Rust CLI', language: 'rust', package_manager: 'cargo' }
    ]);
  });

  it('uses Core manifest order for provider, risk, and agent summaries', () => {
    const manifest: HarnessManifestDto = {
      runtime: 'tinadec-core-workflow',
      ownership_model: 'Core owns policy.',
      tool_registry: {
        declared_tool_count: 3,
        canonical_tool_count: 3,
        duplicate_tool_id_count: 0,
        duplicate_tool_ids: [],
        source_precedence: ['core', 'code', 'codex-rust', 'extension'],
        selection_policy: 'Core canonicalizes duplicate tool ids.'
      },
      agent_layers: [
        {
          layer: 'execution',
          role: 'Passive execution',
          agent_count: 2,
          enabled_agent_count: 2,
          max_parallel_executors: 2,
          worktree_isolation: true,
          approval_required: true,
          agent_types: ['code-writer'],
          tool_ids: ['apply_patch']
        },
        {
          layer: 'planning',
          role: 'Active planning',
          agent_count: 1,
          enabled_agent_count: 1,
          max_parallel_executors: 1,
          worktree_isolation: false,
          approval_required: false,
          agent_types: ['meeting'],
          tool_ids: ['prompt_context_resolve']
        }
      ],
      tool_providers: [
        {
          source: 'codex-rust',
          display_name: 'Codex Rust Primitives',
          layer: 'native-glue',
          status: 'mixed',
          tool_count: 2,
          active_tool_count: 1,
          future_tool_count: 1,
          approval_required_count: 1,
          read_only_count: 1,
          capability_prefixes: ['file']
        },
        {
          source: 'core',
          display_name: 'Tinadec Core',
          layer: 'core',
          status: 'active',
          tool_count: 1,
          active_tool_count: 1,
          future_tool_count: 0,
          approval_required_count: 0,
          read_only_count: 1,
          capability_prefixes: ['prompt']
        }
      ],
      tool_risks: [
        {
          risk: 'shell',
          tool_count: 1,
          requires_human_checkpoint: true,
          policy_summary: 'approval'
        },
        {
          risk: 'read-only',
          tool_count: 2,
          requires_human_checkpoint: false,
          policy_summary: 'automatic'
        }
      ],
      tools,
      design_notes: []
    };

    expect(sortedAgentLayers(manifest).map((layer) => layer.layer)).toEqual(['planning', 'execution']);
    expect(sortedToolProviders(manifest).map((provider) => provider.source)).toEqual(['core', 'codex-rust']);
    expect(sortedRiskPolicies(manifest).map((risk) => risk.risk)).toEqual(['read-only', 'shell']);
    expect(manifestTools(manifest).map((tool) => tool.id)).toEqual(['language_runtime_probe', 'git_worktree_manager', 'read_file']);
  });

  it('keeps Core search result ordering deterministic when scores tie', () => {
    const results: ToolSearchResultDto[] = [
      {
        tool: tools[2],
        score: 120,
        matched_fields: ['capabilities'],
        provider_layer: 'native-glue',
        requires_human_checkpoint: false,
        approval_summary: 'Automatic'
      },
      {
        tool: tools[0],
        score: 120,
        matched_fields: ['capabilities'],
        provider_layer: 'tool-layer',
        requires_human_checkpoint: false,
        approval_summary: 'Automatic'
      },
      {
        tool: tools[1],
        score: 260,
        matched_fields: ['id'],
        provider_layer: 'tool-layer',
        requires_human_checkpoint: true,
        approval_summary: 'Requires approval'
      }
    ];

    expect(sortedToolSearchResults(results).map((result) => result.tool.id)).toEqual([
      'git_worktree_manager',
      'language_runtime_probe',
      'read_file'
    ]);
  });
});
