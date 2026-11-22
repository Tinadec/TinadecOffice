// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import {
  buildRuntimeTopology,
  formatRuntimeEngineBadge,
} from './OrchestrationMiniGraph.vue'
import type { OrchestrationSnapshotDto, TopologyRunDto } from '../api'

describe('OrchestrationMiniGraph runtime engine badges (#36)', () => {
  it('formats crisp ACP, CLI, TUI, and API engine badges from runtime metadata and provider rows', () => {
    expect(formatRuntimeEngineBadge({ runtime_badge: 'ACP · Antigravity' })).toEqual({
      label: 'ACP · Antigravity',
      kind: 'acp',
    })
    expect(formatRuntimeEngineBadge({ channel: 'cli', driver: 'claude-code' })).toEqual({
      label: 'CLI · Claude Code',
      kind: 'cli',
    })
    expect(formatRuntimeEngineBadge({ protocol: 'openai-responses', provider_display_name: 'Carbon' })).toEqual({
      label: 'API · Carbon',
      kind: 'api',
    })

    const providers = new Map([
      ['p-acp', { driver: 'antigravity', channel: 'acp', protocol: 'acp', display_name: 'Antigravity', connection_kind: 'cli' }],
    ])
    expect(formatRuntimeEngineBadge({ provider_instance_id: 'p-acp' }, providers)).toEqual({
      label: 'ACP · Antigravity',
      kind: 'acp',
    })
    expect(formatRuntimeEngineBadge({})).toBeNull()
  })

  it('propagates teammate and instance runtime badges onto topology nodes', () => {
    const snapshot = {
      run: {
        id: 'run-1',
        session_id: 'sess-1',
        user_message_id: 'msg-1',
        status: 'running',
        runtime_profile_id: 'default',
        config_version: 1,
        config_hash: 'h',
        context_revision: 1,
        summary: '',
        created_at: '2026-11-22T00:00:00Z',
        updated_at: '2026-11-22T00:00:00Z',
      },
      nodes: [],
      lanes: [],
      assignments: [],
      step_results: [],
      context_packs: [],
      supervision_findings: [],
      teammates: [
        { handle: 'impl#1', agent_slug: 'global_engineering', runtime_badge: 'ACP · Antigravity' },
        { handle: 'audit#2', agent_slug: 'reviewer', protocol: 'cli', driver: 'claude-code' },
      ],
    } as unknown as OrchestrationSnapshotDto

    const topologyRun: TopologyRunDto = {
      run_id: 'run-1',
      status: 'running',
      tasks: [
        {
          task_id: 't-1',
          task_key: 'impl',
          title: 'Implement ACP bridge',
          status: 'running',
          handle: 'impl#1',
          agent_slug: 'global_engineering',
          dependencies: [],
          write_scope: ['TinadecCore/Models'],
        },
        {
          task_id: 't-2',
          task_key: 'audit',
          title: 'Verify contracts',
          status: 'running',
          handle: 'audit#2',
          agent_slug: 'reviewer',
          dependencies: [],
          write_scope: [],
        },
      ],
      instances: [],
      tasks_truncated: false,
      instances_truncated: false,
    }

    const { nodes } = buildRuntimeTopology({
      snapshot,
      topologyRun,
      coordinatorFallbackLabel: 'Coordinator',
    })

    const implNode = nodes.find((n) => n.taskId === 't-1')
    const auditNode = nodes.find((n) => n.taskId === 't-2')
    expect(implNode?.runtimeBadge).toBe('ACP · Antigravity')
    expect(implNode?.runtimeKind).toBe('acp')
    expect(auditNode?.runtimeBadge).toBe('CLI · Claude Code')
    expect(auditNode?.runtimeKind).toBe('cli')
  })
})
