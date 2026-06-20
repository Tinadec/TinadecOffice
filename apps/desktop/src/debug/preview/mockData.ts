/**
 * Mock 数据生成器
 * 为预览画廊系统提供真实、丰富的模拟数据
 */
import type {
  ProjectDto,
  SessionDto,
  MessageDto,
  ApprovalDto,
  OrchestrationSnapshotDto,
  ToolExecutionTimelineItemDto,
  EventEnvelope,
  DoctorReportDto,
  RuntimeReadinessReceiptDto,
  ModelSettingsDto,
  ModelProviderInstanceDto,
  ModelRouteDto,
  AgentProfileDto,
  AgentModeDto,
  ToolDescriptorDto,
  HarnessManifestDto,
  PromptFragmentDto,
  ExtensionSourceDto,
  MarketCatalogItemDto,
  InstalledExtensionDto,
  McpServerDto,
  AcpAdapterDto,
  CodeToolExecuteResultDto,
} from '@/api'

// ============================================================
// 基础工具函数
// ============================================================

function iso(offsetMinutes: number = 0): string {
  const d = new Date(Date.now() + offsetMinutes * 60 * 1000)
  return d.toISOString()
}

function id(prefix: string, n: number): string {
  return `${prefix}-${n.toString().padStart(4, '0')}`
}

// ============================================================
// 项目数据
// ============================================================

export function mockProjects(): ProjectDto[] {
  return [
    {
      id: 'proj-tinadec-001',
      name: 'tinadec',
      path: 'D:/workspace/tinadec',
      created_at: iso(-60 * 24 * 30),
    },
    {
      id: 'proj-webapp-002',
      name: 'web-app',
      path: 'D:/workspace/web-app',
      created_at: iso(-60 * 24 * 14),
    },
    {
      id: 'proj-apiserver-003',
      name: 'api-server',
      path: 'D:/workspace/api-server',
      created_at: iso(-60 * 24 * 7),
    },
  ]
}

// ============================================================
// 会话数据
// ============================================================

export function mockSessions(projectId?: string): SessionDto[] {
  const allSessions: SessionDto[] = [
    {
      id: 'sess-tinadec-1001',
      project_id: 'proj-tinadec-001',
      title: '重构编排引擎的任务图构建逻辑',
      status: 'active',
      created_at: iso(-60 * 5),
      updated_at: iso(-60 * 2),
    },
    {
      id: 'sess-tinadec-1002',
      project_id: 'proj-tinadec-001',
      title: '修复 Monaco Diff 编辑器内存泄漏',
      status: 'active',
      created_at: iso(-60 * 24 * 2),
      updated_at: iso(-60 * 22),
    },
    {
      id: 'sess-tinadec-1003',
      project_id: 'proj-tinadec-001',
      title: '为 Debug Studio 添加预览画廊',
      status: 'idle',
      created_at: iso(-60 * 24 * 3),
      updated_at: iso(-60 * 24 * 3),
    },
    {
      id: 'sess-webapp-2001',
      project_id: 'proj-webapp-002',
      title: '实现用户登录与权限校验',
      status: 'active',
      created_at: iso(-60 * 8),
      updated_at: iso(-60 * 1),
    },
    {
      id: 'sess-webapp-2002',
      project_id: 'proj-webapp-002',
      title: '优化首屏加载性能',
      status: 'idle',
      created_at: iso(-60 * 24 * 4),
      updated_at: iso(-60 * 24 * 4),
    },
    {
      id: 'sess-apiserver-3001',
      project_id: 'proj-apiserver-003',
      title: '设计 REST API 限流中间件',
      status: 'active',
      created_at: iso(-60 * 3),
      updated_at: iso(-60 * 1),
    },
    {
      id: 'sess-apiserver-3002',
      project_id: 'proj-apiserver-003',
      title: '数据库迁移脚本编写',
      status: 'completed',
      created_at: iso(-60 * 24 * 6),
      updated_at: iso(-60 * 24 * 5),
    },
  ]
  if (projectId) return allSessions.filter((s) => s.project_id === projectId)
  return allSessions
}

// ============================================================
// 消息数据
// ============================================================

const ASSISTANT_MARKDOWN_RICH = `## 任务拆解完成

我已分析你的需求，下面是建议的执行计划：

1. **探查现有代码结构** — 使用 \`list_directory\` 与 \`read_file\` 工具梳理相关模块
2. **定位问题根因** — 通过 \`grep_content\` 搜索关键调用路径
3. **编写修复补丁** — 使用 \`apply_patch\` 工具应用变更

\`\`\`typescript
// 示例：编排引擎入口
export class OrchestratorService {
  async run(sessionId: string, userMessage: string): Promise<RunDto> {
    const graph = await this.buildTaskGraph(sessionId, userMessage)
    const assignments = await this.assignAgents(graph)
    return this.execute(graph, assignments)
  }
}
\`\`\`

### 风险评估

| 维度 | 等级 | 说明 |
| --- | --- | --- |
| 影响范围 | 中 | 涉及编排核心模块 |
| 回滚难度 | 低 | 补丁可逆向 |
| 测试覆盖 | 高 | 已有单元测试兜底 |

> 监督智能体已确认该计划符合安全策略，等待你的确认后开始执行。`

const ASSISTANT_MARKDOWN_SIMPLE = `好的，我已开始执行任务。

正在读取 \`src/orchestrator.ts\` 文件，分析现有实现...`

export function mockMessages(sessionId: string): MessageDto[] {
  return [
    {
      id: id('msg', 1),
      session_id: sessionId,
      role: 'user',
      content: '请帮我重构编排引擎的任务图构建逻辑，要求支持动态依赖解析。',
      created_at: iso(-60 * 4),
    },
    {
      id: id('msg', 2),
      session_id: sessionId,
      role: 'assistant',
      content: ASSISTANT_MARKDOWN_RICH,
      created_at: iso(-60 * 4 + 2),
    },
    {
      id: id('msg', 3),
      session_id: sessionId,
      role: 'user',
      content: '可以，请按这个计划执行。注意保留向后兼容。',
      created_at: iso(-60 * 3),
    },
    {
      id: id('msg', 4),
      session_id: sessionId,
      role: 'assistant',
      content: ASSISTANT_MARKDOWN_SIMPLE,
      created_at: iso(-60 * 3 + 1),
    },
    {
      id: id('msg', 5),
      session_id: sessionId,
      role: 'user',
      content: '工具调用看起来卡住了，能否查看一下执行状态？',
      created_at: iso(-60 * 2),
    },
    {
      id: id('msg', 6),
      session_id: sessionId,
      role: 'assistant',
      content: '我已检查工具执行时间线，发现 \`apply_patch\` 调用正在等待审批。\n\n请前往右侧 **审批** 面板批准该操作，或直接拒绝以回滚。',
      created_at: iso(-60 * 2 + 1),
    },
  ]
}

export function mockManyMessages(sessionId: string): MessageDto[] {
  const base = mockMessages(sessionId)
  const extra: MessageDto[] = []
  for (let i = 0; i < 8; i++) {
    extra.push({
      id: id('msg', 100 + i * 2),
      session_id: sessionId,
      role: 'user',
      content: `第 ${i + 1} 轮追问：请进一步说明第 ${i + 1} 步的实现细节，并给出对应的单元测试用例。`,
      created_at: iso(-60 * (10 - i)),
    })
    extra.push({
      id: id('msg', 101 + i * 2),
      session_id: sessionId,
      role: 'assistant',
      content: `### 第 ${i + 1} 轮回复\n\n针对你的追问，补充说明如下：\n\n- 实现要点 ${i + 1}：使用 \`Map<taskNodeId, Dependency[]>\` 维护依赖关系\n- 测试用例 ${i + 1}：\n\n\`\`\`typescript\nit('resolves dynamic dependencies #${i + 1}', async () => {\n  const graph = buildGraph(fixture(${i + 1}))\n  expect(graph.nodes).toHaveLength(${i + 2})\n})\n\`\`\`\n\n> 该用例覆盖了循环依赖检测与拓扑排序边界场景。`,
      created_at: iso(-60 * (10 - i) + 1),
    })
  }
  return [...base, ...extra]
}

// ============================================================
// 审批批数据
// ============================================================

export function mockApprovals(sessionId?: string): ApprovalDto[] {
  return [
    {
      id: 'appr-001',
      session_id: sessionId ?? 'sess-tinadec-1001',
      kind: 'git',
      summary: 'Stage 3 files on feature/orchestrator-refactor',
      command: 'git add -- src/orchestrator.ts src/graph.ts src/__tests__/orch.test.ts',
      cwd: 'D:/workspace/tinadec',
      status: 'pending',
      created_at: iso(-30),
      decided_at: null,
    },
    {
      id: 'appr-002',
      session_id: sessionId ?? 'sess-tinadec-1001',
      kind: 'shell',
      summary: 'npm run test:unit -- --filter orchestrator',
      command: 'npm run test:unit -- --filter orchestrator',
      cwd: 'D:/workspace/tinadec',
      status: 'pending',
      created_at: iso(-25),
      decided_at: null,
    },
    {
      id: 'appr-003',
      session_id: sessionId ?? 'sess-tinadec-1001',
      kind: 'code',
      summary: 'Apply patch to src/orchestrator.ts (12 additions, 4 deletions)',
      command: 'apply_patch src/orchestrator.ts',
      cwd: 'D:/workspace/tinadec',
      status: 'pending',
      created_at: iso(-20),
      decided_at: null,
    },
    {
      id: 'appr-004',
      session_id: sessionId ?? 'sess-tinadec-1001',
      kind: 'git',
      summary: 'Commit 3 files on feature/orchestrator-refactor',
      command: 'git commit -m "refactor(orchestrator): support dynamic dependency resolution"',
      cwd: 'D:/workspace/tinadec',
      status: 'approved',
      created_at: iso(-60 * 2),
      decided_at: iso(-60 * 2 + 5),
    },
    {
      id: 'appr-005',
      session_id: sessionId ?? 'sess-tinadec-1001',
      kind: 'shell',
      summary: 'npm run lint -- --fix',
      command: 'npm run lint -- --fix',
      cwd: 'D:/workspace/tinadec',
      status: 'rejected',
      created_at: iso(-60 * 3),
      decided_at: iso(-60 * 3 + 3),
    },
  ]
}

export function mockPendingApprovals(sessionId?: string): ApprovalDto[] {
  return mockApprovals(sessionId).map((a, i) => ({
    ...a,
    id: `appr-pending-${i + 1}`,
    status: 'pending' as const,
    decided_at: null,
    created_at: iso(-i * 5 - 1),
  }))
}

// ============================================================
// 编排快照数据
// ============================================================

export function mockOrchestrationSnapshot(sessionId: string): OrchestrationSnapshotDto {
  const runId = 'run-orch-001'
  const graphId = 'graph-orch-001'
  return {
    run: {
      id: runId,
      session_id: sessionId,
      user_message_id: 'msg-0001',
      status: 'running',
      summary: '重构编排引擎的任务图构建逻辑，支持动态依赖解析',
      created_at: iso(-60 * 4),
      updated_at: iso(-60 * 1),
    },
    graph: {
      id: graphId,
      run_id: runId,
      session_id: sessionId,
      title: '编排引擎重构任务图',
      status: 'active',
      created_at: iso(-60 * 4 + 1),
      updated_at: iso(-60 * 1),
    },
    nodes: [
      {
        id: 'node-001',
        graph_id: graphId,
        run_id: runId,
        session_id: sessionId,
        title: '探查现有编排引擎实现',
        description: '阅读 src/orchestrator.ts 与相关模块，梳理当前任务图构建流程',
        status: 'completed',
        priority: 1,
        risk: 'low',
        success_criteria: ['输出当前实现的关键调用路径', '识别可扩展点'],
        dependencies: [],
        required_capabilities: ['code.read', 'code.search'],
        created_at: iso(-60 * 4 + 2),
        updated_at: iso(-60 * 3),
      },
      {
        id: 'node-002',
        graph_id: graphId,
        run_id: runId,
        session_id: sessionId,
        title: '设计动态依赖解析数据结构',
        description: '设计支持运行时依赖发现的图节点结构，兼容现有序列化协议',
        status: 'completed',
        priority: 2,
        risk: 'medium',
        success_criteria: ['数据结构通过设计评审', '向后兼容旧图格式'],
        dependencies: ['node-001'],
        required_capabilities: ['design.architecture'],
        created_at: iso(-60 * 3),
        updated_at: iso(-60 * 2),
      },
      {
        id: 'node-003',
        graph_id: graphId,
        run_id: runId,
        session_id: sessionId,
        title: '实现 DynamicDependencyResolver',
        description: '编写核心解析器，支持循环检测与拓扑排序',
        status: 'running',
        priority: 3,
        risk: 'high',
        success_criteria: ['单元测试覆盖率 ≥ 85%', '通过循环依赖边界用例'],
        dependencies: ['node-002'],
        required_capabilities: ['code.write', 'code.test'],
        created_at: iso(-60 * 2),
        updated_at: iso(-60 * 1),
      },
      {
        id: 'node-004',
        graph_id: graphId,
        run_id: runId,
        session_id: sessionId,
        title: '集成到 OrchestratorService',
        description: '将新解析器接入编排服务主流程，替换旧的静态依赖构建',
        status: 'pending',
        priority: 4,
        risk: 'medium',
        success_criteria: ['端到端测试通过', '性能不劣于旧实现'],
        dependencies: ['node-003'],
        required_capabilities: ['code.write', 'code.test', 'code.review'],
        created_at: iso(-60 * 1),
        updated_at: iso(-60 * 1),
      },
      {
        id: 'node-005',
        graph_id: graphId,
        run_id: runId,
        session_id: sessionId,
        title: '更新文档与示例',
        description: '编写新特性的使用文档，更新架构图',
        status: 'pending',
        priority: 5,
        risk: 'low',
        success_criteria: ['文档评审通过', '示例可运行'],
        dependencies: ['node-004'],
        required_capabilities: ['docs.write'],
        created_at: iso(-60 * 1 + 1),
        updated_at: iso(-60 * 1 + 1),
      },
    ],
    assignments: [
      {
        id: 'assign-001',
        run_id: runId,
        task_node_id: 'node-001',
        agent_id: 'agent-code-explorer',
        agent_name: 'Code Explorer',
        agent_layer: 'execution',
        agent_type: 'code_explorer',
        model_route_purpose: 'execution.fast',
        permission_mode: 'default',
        allowed_tools: ['read_file', 'list_directory', 'grep_content', 'glob_search'],
        status: 'completed',
        created_at: iso(-60 * 4 + 2),
      },
      {
        id: 'assign-002',
        run_id: runId,
        task_node_id: 'node-002',
        agent_id: 'agent-task-planner',
        agent_name: 'Task Planner',
        agent_layer: 'planning',
        agent_type: 'task_planner',
        model_route_purpose: 'planning.strong',
        permission_mode: 'default',
        allowed_tools: ['read_file', 'grep_content'],
        status: 'completed',
        created_at: iso(-60 * 3),
      },
      {
        id: 'assign-003',
        run_id: runId,
        task_node_id: 'node-003',
        agent_id: 'agent-code-writer',
        agent_name: 'Code Writer',
        agent_layer: 'execution',
        agent_type: 'code_writer',
        model_route_purpose: 'execution.strong',
        permission_mode: 'default',
        allowed_tools: ['read_file', 'apply_patch', 'code_editor', 'grep_content'],
        status: 'active',
        created_at: iso(-60 * 2),
      },
      {
        id: 'assign-004',
        run_id: runId,
        task_node_id: 'node-004',
        agent_id: 'agent-code-writer',
        agent_name: 'Code Writer',
        agent_layer: 'execution',
        agent_type: 'code_writer',
        model_route_purpose: 'execution.strong',
        permission_mode: 'default',
        allowed_tools: ['read_file', 'apply_patch', 'code_editor'],
        status: 'waiting',
        created_at: iso(-60 * 1),
      },
    ],
    step_results: [
      {
        id: 'step-001',
        run_id: runId,
        task_node_id: 'node-001',
        agent_id: 'agent-code-explorer',
        status: 'completed',
        summary: '已识别 3 处可扩展点：buildGraph()、resolveDependencies()、executeGraph()',
        evidence: [
          'src/orchestrator.ts:42 — buildGraph 入口',
          'src/orchestrator.ts:118 — resolveDependencies 静态实现',
          'src/orchestrator.ts:205 — executeGraph 调度循环',
        ],
        created_at: iso(-60 * 3),
      },
      {
        id: 'step-002',
        run_id: runId,
        task_node_id: 'node-002',
        agent_id: 'agent-task-planner',
        status: 'completed',
        summary: '设计了 DynamicNode 接口，兼容旧 TaskNodeDto 序列化',
        evidence: [
          '新增字段: dynamic_dependencies: string[]',
          '新增字段: resolver_hint: string | null',
          '向后兼容: 旧字段 dependencies 保留',
        ],
        created_at: iso(-60 * 2),
      },
      {
        id: 'step-003',
        run_id: runId,
        task_node_id: 'node-003',
        agent_id: 'agent-code-writer',
        status: 'stubbed',
        summary: '正在实现 DynamicDependencyResolver，已完成循环检测核心逻辑',
        evidence: [
          'src/dynamicResolver.ts:1 — 新文件已创建',
          '单元测试 8/12 通过',
        ],
        created_at: iso(-60 * 1),
      },
    ],
    context_packs: [
      {
        id: 'ctx-001',
        run_id: runId,
        session_id: sessionId,
        created_by_agent_id: 'agent-context-compressor',
        summary: '编排引擎核心模块上下文（含 3 个关键文件）',
        token_budget: 8192,
        compression_ratio: 2.4,
        evidence_map: ['src/orchestrator.ts', 'src/graph.ts', 'src/types.ts'],
        created_at: iso(-60 * 3),
      },
      {
        id: 'ctx-002',
        run_id: runId,
        session_id: sessionId,
        created_by_agent_id: 'agent-context-compressor',
        summary: '测试用例与 fixture 上下文',
        token_budget: 4096,
        compression_ratio: 1.8,
        evidence_map: ['src/__tests__/orch.test.ts', 'src/__tests__/fixtures/'],
        created_at: iso(-60 * 2),
      },
    ],
    supervision_findings: [
      {
        id: 'sup-001',
        run_id: runId,
        session_id: sessionId,
        severity: 'warning',
        category: 'test-coverage',
        summary: 'DynamicDependencyResolver 单元测试覆盖率当前为 67%，低于 85% 阈值',
        recommendation: '补充循环依赖边界用例与空图场景测试',
        status: 'open',
        created_at: iso(-60 * 1),
      },
      {
        id: 'sup-002',
        run_id: runId,
        session_id: sessionId,
        severity: 'info',
        category: 'backward-compat',
        summary: '新数据结构已保留旧字段，向后兼容性良好',
        recommendation: '无需额外操作',
        status: 'resolved',
        created_at: iso(-60 * 2),
      },
      {
        id: 'sup-003',
        run_id: runId,
        session_id: sessionId,
        severity: 'critical',
        category: 'security',
        summary: 'apply_patch 调用未携带 approval_id，存在未授权写入风险',
        recommendation: '在执行前补充审批流程',
        status: 'open',
        created_at: iso(-30),
      },
    ],
  }
}

// ============================================================
// 工具执行时间线
// ============================================================

export function mockToolExecutions(sessionId: string): ToolExecutionTimelineItemDto[] {
  return [
    {
      id: 'te-001',
      run_id: 'run-orch-001',
      session_id: sessionId,
      tool_id: 'list_directory',
      tool_display_name: 'List Directory',
      source: 'builtin',
      provider_layer: 'code',
      risk: 'low',
      requires_approval: false,
      status: 'completed',
      approval_id: null,
      step_result_id: 'step-001',
      summary: '列出 src/ 目录，发现 12 个文件',
      evidence: ['src/orchestrator.ts', 'src/graph.ts', 'src/types.ts'],
      requested_at: iso(-60 * 4 + 2),
      updated_at: iso(-60 * 4 + 2),
      duration_ms: 42,
      requested_seq: 1,
      updated_seq: 1,
      event_types: ['tool.execution.started', 'tool.execution.completed'],
      checkpoint_summary: 'path=src/',
    },
    {
      id: 'te-002',
      run_id: 'run-orch-001',
      session_id: sessionId,
      tool_id: 'read_file',
      tool_display_name: 'Read File',
      source: 'builtin',
      provider_layer: 'code',
      risk: 'low',
      requires_approval: false,
      status: 'completed',
      approval_id: null,
      step_result_id: 'step-001',
      summary: '读取 src/orchestrator.ts（312 行）',
      evidence: ['src/orchestrator.ts:1-312'],
      requested_at: iso(-60 * 4 + 3),
      updated_at: iso(-60 * 4 + 3),
      duration_ms: 18,
      requested_seq: 2,
      updated_seq: 2,
      event_types: ['tool.execution.started', 'tool.execution.completed'],
      checkpoint_summary: 'path=src/orchestrator.ts',
    },
    {
      id: 'te-003',
      run_id: 'run-orch-001',
      session_id: sessionId,
      tool_id: 'grep_content',
      tool_display_name: 'Grep Content',
      source: 'builtin',
      provider_layer: 'code',
      risk: 'low',
      requires_approval: false,
      status: 'completed',
      approval_id: null,
      step_result_id: 'step-001',
      summary: '搜索 "buildGraph" 命中 8 处',
      evidence: ['src/orchestrator.ts:42', 'src/orchestrator.ts:118'],
      requested_at: iso(-60 * 4 + 4),
      updated_at: iso(-60 * 4 + 4),
      duration_ms: 27,
      requested_seq: 3,
      updated_seq: 3,
      event_types: ['tool.execution.started', 'tool.execution.completed'],
      checkpoint_summary: 'pattern=buildGraph',
    },
    {
      id: 'te-004',
      run_id: 'run-orch-001',
      session_id: sessionId,
      tool_id: 'apply_patch',
      tool_display_name: 'Apply Patch',
      source: 'builtin',
      provider_layer: 'code',
      risk: 'high',
      requires_approval: true,
      status: 'waiting_approval',
      approval_id: 'appr-003',
      step_result_id: 'step-003',
      summary: '等待审批：修改 src/orchestrator.ts（+12 -4）',
      evidence: ['src/orchestrator.ts'],
      requested_at: iso(-20),
      updated_at: iso(-20),
      duration_ms: 0,
      requested_seq: 7,
      updated_seq: 7,
      event_types: ['tool.execution.started', 'approval.requested'],
      checkpoint_summary: 'path=src/orchestrator.ts lines=12-24',
    },
    {
      id: 'te-005',
      run_id: 'run-orch-001',
      session_id: sessionId,
      tool_id: 'code_editor',
      tool_display_name: 'Code Editor',
      source: 'builtin',
      provider_layer: 'code',
      risk: 'medium',
      requires_approval: false,
      status: 'running',
      approval_id: null,
      step_result_id: 'step-003',
      summary: '正在编辑 src/dynamicResolver.ts',
      evidence: ['src/dynamicResolver.ts'],
      requested_at: iso(-15),
      updated_at: iso(-5),
      duration_ms: 0,
      requested_seq: 8,
      updated_seq: 9,
      event_types: ['tool.execution.started'],
      checkpoint_summary: 'action=save path=src/dynamicResolver.ts',
    },
    {
      id: 'te-006',
      run_id: 'run-orch-001',
      session_id: sessionId,
      tool_id: 'shell',
      tool_display_name: 'Shell',
      source: 'builtin',
      provider_layer: 'code',
      risk: 'high',
      requires_approval: true,
      status: 'waiting_approval',
      approval_id: 'appr-002',
      step_result_id: null,
      summary: '等待审批：npm run test:unit -- --filter orchestrator',
      evidence: [],
      requested_at: iso(-25),
      updated_at: iso(-25),
      duration_ms: 0,
      requested_seq: 6,
      updated_seq: 6,
      event_types: ['tool.shell.approval_required'],
      checkpoint_summary: 'command=npm run test:unit',
    },
    {
      id: 'te-007',
      run_id: 'run-orch-001',
      session_id: sessionId,
      tool_id: 'git_worktree_manager',
      tool_display_name: 'Git Worktree Manager',
      source: 'builtin',
      provider_layer: 'code',
      risk: 'medium',
      requires_approval: true,
      status: 'completed',
      approval_id: 'appr-004',
      step_result_id: null,
      summary: '已提交 3 个文件到 feature/orchestrator-refactor',
      evidence: ['src/orchestrator.ts', 'src/graph.ts', 'src/__tests__/orch.test.ts'],
      requested_at: iso(-60 * 2),
      updated_at: iso(-60 * 2 + 5),
      duration_ms: 1240,
      requested_seq: 4,
      updated_seq: 5,
      event_types: ['tool.execution.started', 'approval.approved', 'tool.execution.completed'],
      checkpoint_summary: 'action=commit branch=feature/orchestrator-refactor',
    },
    {
      id: 'te-008',
      run_id: 'run-orch-001',
      session_id: sessionId,
      tool_id: 'glob_search',
      tool_display_name: 'Glob Search',
      source: 'builtin',
      provider_layer: 'code',
      risk: 'low',
      requires_approval: false,
      status: 'failed',
      approval_id: null,
      step_result_id: null,
      summary: '模式 "**/*.spec.ts" 未匹配到任何文件',
      evidence: [],
      requested_at: iso(-60 * 3),
      updated_at: iso(-60 * 3),
      duration_ms: 12,
      requested_seq: 3,
      updated_seq: 3,
      event_types: ['tool.execution.started', 'tool.execution.failed'],
      checkpoint_summary: 'pattern=**/*.spec.ts',
    },
    {
      id: 'te-009',
      run_id: 'run-orch-001',
      session_id: sessionId,
      tool_id: 'read_file',
      tool_display_name: 'Read File',
      source: 'builtin',
      provider_layer: 'code',
      risk: 'low',
      requires_approval: false,
      status: 'completed',
      approval_id: null,
      step_result_id: 'step-002',
      summary: '读取 src/types.ts（148 行）',
      evidence: ['src/types.ts:1-148'],
      requested_at: iso(-60 * 3 + 1),
      updated_at: iso(-60 * 3 + 1),
      duration_ms: 9,
      requested_seq: 4,
      updated_seq: 4,
      event_types: ['tool.execution.started', 'tool.execution.completed'],
      checkpoint_summary: 'path=src/types.ts',
    },
    {
      id: 'te-010',
      run_id: 'run-orch-001',
      session_id: sessionId,
      tool_id: 'apply_patch',
      tool_display_name: 'Apply Patch',
      source: 'builtin',
      provider_layer: 'code',
      risk: 'high',
      requires_approval: true,
      status: 'failed',
      approval_id: 'appr-005',
      step_result_id: null,
      summary: '审批被拒绝：npm run lint -- --fix',
      evidence: [],
      requested_at: iso(-60 * 3),
      updated_at: iso(-60 * 3 + 3),
      duration_ms: 0,
      requested_seq: 5,
      updated_seq: 5,
      event_types: ['tool.execution.started', 'approval.rejected', 'tool.execution.failed'],
      checkpoint_summary: 'command=npm run lint -- --fix',
    },
  ]
}

export function mockFailedToolExecutions(sessionId: string): ToolExecutionTimelineItemDto[] {
  return mockToolExecutions(sessionId).map((item, i) => ({
    ...item,
    id: `te-failed-${i + 1}`,
    status: i % 3 === 0 ? 'failed' : 'error',
    summary: item.summary.startsWith('失败') ? item.summary : `失败：${item.summary}`,
    duration_ms: item.duration_ms + 500,
  }))
}

// ============================================================
// 事件数据
// ============================================================

export function mockEvents(sessionId: string): EventEnvelope[] {
  const events: Array<Partial<EventEnvelope> & { type: string; seq: number }> = [
    { type: 'run.started', seq: 1, payload: { id: 'run-orch-001', summary: '重构编排引擎' } },
    { type: 'task_graph.created', seq: 2, payload: { id: 'graph-orch-001', title: '编排引擎重构任务图', nodes: [] } },
    { type: 'task.assigned', seq: 3, payload: { agent_id: 'agent-code-explorer', agent_name: 'Code Explorer', task_title: '探查现有编排引擎实现' } },
    { type: 'tool.execution.started', seq: 4, payload: { tool_id: 'list_directory', tool_name: 'List Directory' } },
    { type: 'tool.execution.completed', seq: 5, payload: { tool_id: 'list_directory', duration_ms: 42 } },
    { type: 'step.result.created', seq: 6, payload: { agent_name: 'Code Explorer', status: 'completed', summary: '已识别 3 处可扩展点' } },
    { type: 'task.assigned', seq: 7, payload: { agent_id: 'agent-task-planner', agent_name: 'Task Planner', task_title: '设计动态依赖解析数据结构' } },
    { type: 'context.pack.created', seq: 8, payload: { summary: '编排引擎核心模块上下文', token_budget: 8192, compression_ratio: 2.4 } },
    { type: 'supervision.checked', seq: 9, payload: { severity: 'warning', category: 'test-coverage', summary: '覆盖率 67% 低于阈值' } },
    { type: 'step.result.created', seq: 10, payload: { agent_name: 'Task Planner', status: 'completed', summary: '设计了 DynamicNode 接口' } },
    { type: 'task.assigned', seq: 11, payload: { agent_id: 'agent-code-writer', agent_name: 'Code Writer', task_title: '实现 DynamicDependencyResolver' } },
    { type: 'tool.execution.started', seq: 12, payload: { tool_id: 'apply_patch', tool_name: 'Apply Patch' } },
    { type: 'approval.requested', seq: 13, payload: { id: 'appr-003', summary: 'Apply patch to src/orchestrator.ts' } },
    { type: 'tool.shell.approval_required', seq: 14, payload: { command: 'npm run test:unit', approval_id: 'appr-002' } },
    { type: 'message.created', seq: 15, payload: { role: 'assistant', content: '我已检查工具执行时间线...' } },
    { type: 'supervision.checked', seq: 16, payload: { severity: 'critical', category: 'security', summary: 'apply_patch 未携带 approval_id' } },
    { type: 'context.pack.created', seq: 17, payload: { summary: '测试用例上下文', token_budget: 4096, compression_ratio: 1.8 } },
    { type: 'approval.approved', seq: 18, payload: { id: 'appr-004', summary: 'Commit 3 files' } },
    { type: 'approval.rejected', seq: 19, payload: { id: 'appr-005', summary: 'npm run lint -- --fix' } },
    { type: 'tool.execution.completed', seq: 20, payload: { tool_id: 'git_worktree_manager', duration_ms: 1240 } },
  ]
  return events.map((e, i) => ({
    v: '1',
    request_id: `req-${e.seq.toString().padStart(4, '0')}`,
    session_id: sessionId,
    trace_id: `trace-${e.seq.toString().padStart(4, '0')}`,
    ts: iso(-20 + i),
    capabilities: [],
    ...e,
  } as EventEnvelope))
}

// ============================================================
// Doctor / Readiness
// ============================================================

export function mockDoctor(): DoctorReportDto {
  return {
    platform: 'win32-x64',
    agent_core_version: '0.4.2',
    checks: [
      { name: 'gateway', status: 'ok', message: '网关运行在 127.0.0.1:48730' },
      { name: 'model_provider', status: 'ok', message: '已配置 3 个 provider，2 个就绪' },
      { name: 'tool_registry', status: 'ok', message: '16 个工具已注册' },
      { name: 'agent_runtime', status: 'ok', message: '15 个 agent 已加载' },
      { name: 'event_hub', status: 'ok', message: '事件总线正常' },
    ],
  }
}

export function mockDoctorWithError(): DoctorReportDto {
  return {
    platform: 'win32-x64',
    agent_core_version: '0.4.2',
    checks: [
      { name: 'gateway', status: 'ok', message: '网关运行在 127.0.0.1:48730' },
      { name: 'model_provider', status: 'error', message: 'OpenAI provider 缺少 API Key' },
      { name: 'tool_registry', status: 'ok', message: '16 个工具已注册' },
      { name: 'agent_runtime', status: 'warning', message: '2 个 agent 未启用' },
      { name: 'event_hub', status: 'error', message: '事件总线连接超时' },
    ],
  }
}

export function mockReadiness(): RuntimeReadinessReceiptDto {
  return {
    status: 'ready',
    generated_at: iso(0),
    runtime: 'TinadecCore 0.4.2',
    receipt_id: 'rcpt-001',
    components: [
      { id: 'gateway', name: 'Gateway', status: 'ready', summary: '网关正常运行', evidence: ['监听 127.0.0.1:48730'] },
      { id: 'model', name: 'Model Layer', status: 'ready', summary: '2/3 provider 就绪', evidence: ['openai: ready', 'anthropic: ready', 'local: disabled'] },
      { id: 'tools', name: 'Tool Layer', status: 'ready', summary: '16 个工具可用', evidence: ['builtin: 12', 'extension: 4'] },
      { id: 'agents', name: 'Agent Runtime', status: 'warning', summary: '13/15 agent 启用', evidence: ['planning: 7/7', 'execution: 6/8'] },
    ],
    ready_count: 3,
    warning_count: 1,
    blocked_count: 0,
  }
}

export function mockReadinessBlocked(): RuntimeReadinessReceiptDto {
  return {
    status: 'blocked',
    generated_at: iso(0),
    runtime: 'TinadecCore 0.4.2',
    receipt_id: 'rcpt-002',
    components: [
      { id: 'gateway', name: 'Gateway', status: 'ready', summary: '网关正常运行', evidence: ['监听 127.0.0.1:48730'] },
      { id: 'model', name: 'Model Layer', status: 'blocked', summary: '无可用 provider', evidence: ['openai: missing api key', 'anthropic: connection refused', 'local: disabled'] },
      { id: 'tools', name: 'Tool Layer', status: 'ready', summary: '16 个工具可用', evidence: ['builtin: 12', 'extension: 4'] },
      { id: 'agents', name: 'Agent Runtime', status: 'blocked', summary: '0/15 agent 启用', evidence: ['planning: 0/7', 'execution: 0/8'] },
    ],
    ready_count: 2,
    warning_count: 0,
    blocked_count: 2,
  }
}

// ============================================================
// 模型设置
// ============================================================

export function mockModelSettings(): ModelSettingsDto {
  return {
    base_url: 'https://api.openai.com/v1',
    model: 'gpt-4o-mini',
    has_api_key: true,
    updated_at: iso(-60 * 24),
  }
}

export function mockModelProviders(): ModelProviderInstanceDto[] {
  return [
    {
      id: 'mp-openai-001',
      driver: 'openai-compatible',
      display_name: 'OpenAI',
      connection_kind: 'api-key',
      base_url: 'https://api.openai.com/v1',
      model: 'gpt-4o-mini',
      has_api_key: true,
      capabilities: ['chat', 'tools', 'json_mode', 'streaming'],
      enabled: true,
      status: 'healthy',
      status_message: '最近一次心跳正常',
      cooldown_until: null,
      created_at: iso(-60 * 24 * 30),
      updated_at: iso(-60 * 24),
    },
    {
      id: 'mp-anthropic-002',
      driver: 'anthropic',
      display_name: 'Anthropic Claude',
      connection_kind: 'api-key',
      base_url: 'https://api.anthropic.com',
      model: 'claude-3-5-sonnet-20241022',
      has_api_key: true,
      capabilities: ['chat', 'tools', 'streaming'],
      enabled: true,
      status: 'healthy',
      status_message: '最近一次心跳正常',
      cooldown_until: null,
      created_at: iso(-60 * 24 * 20),
      updated_at: iso(-60 * 12),
    },
    {
      id: 'mp-local-003',
      driver: 'local-http',
      display_name: 'LM Studio (本地)',
      connection_kind: 'local-server',
      base_url: 'http://127.0.0.1:1234/v1',
      model: 'qwen2.5-coder-7b',
      has_api_key: false,
      capabilities: ['chat', 'tools'],
      enabled: false,
      status: 'unknown',
      status_message: '本地服务未启动',
      cooldown_until: null,
      created_at: iso(-60 * 24 * 7),
      updated_at: iso(-60 * 24 * 2),
    },
  ]
}

export function mockModelRoutes(): ModelRouteDto[] {
  return [
    { purpose: 'planning.strong', provider_instance_id: 'mp-anthropic-002', model: 'claude-3-5-sonnet-20241022', updated_at: iso(-60 * 12) },
    { purpose: 'execution.fast', provider_instance_id: 'mp-openai-001', model: 'gpt-4o-mini', updated_at: iso(-60 * 24) },
    { purpose: 'execution.strong', provider_instance_id: 'mp-anthropic-002', model: 'claude-3-5-sonnet-20241022', updated_at: iso(-60 * 12) },
  ]
}

// ============================================================
// Agent 数据
// ============================================================

export function mockAgents(): AgentProfileDto[] {
  const planning: AgentProfileDto[] = [
    { id: 'agent-meeting', name: 'Meeting Agent', layer: 'planning', agent_type: 'meeting', mode: 'auto', description: '会议智能体：分析用户意图，拆解任务并调度其他智能体', model_route_purpose: 'planning.strong', allowed_tools: [], capabilities: ['intent.analysis', 'task.decomposition', 'agent.dispatch'], enabled: true, is_built_in: true, updated_at: iso(-60 * 24 * 10) },
    { id: 'agent-task-planner', name: 'Task Planner', layer: 'planning', agent_type: 'task_planner', mode: 'auto', description: '任务规划智能体：构建任务图，分配执行智能体', model_route_purpose: 'planning.strong', allowed_tools: ['read_file', 'grep_content'], capabilities: ['task.graph', 'agent.assignment'], enabled: true, is_built_in: true, updated_at: iso(-60 * 24 * 9) },
    { id: 'agent-context-compressor', name: 'Context Compressor', layer: 'planning', agent_type: 'context_compressor', mode: 'auto', description: '上下文压缩智能体：生成上下文包，控制 token 预算', model_route_purpose: 'planning.strong', allowed_tools: ['read_file'], capabilities: ['context.pack', 'token.budget'], enabled: true, is_built_in: true, updated_at: iso(-60 * 24 * 8) },
    { id: 'agent-prompt-engineer', name: 'Prompt Context Engineer', layer: 'planning', agent_type: 'prompt_context_engineer', mode: 'auto', description: '提示词工程师：组装系统提示词，注入上下文片段', model_route_purpose: 'planning.strong', allowed_tools: [], capabilities: ['prompt.assembly', 'fragment.merge'], enabled: true, is_built_in: true, updated_at: iso(-60 * 24 * 7) },
    { id: 'agent-supervisor', name: 'Supervisor', layer: 'planning', agent_type: 'supervisor', mode: 'auto', description: '监督智能体：检查执行结果，发现风险并给出建议', model_route_purpose: 'planning.strong', allowed_tools: ['read_file', 'grep_content'], capabilities: ['supervision.check', 'risk.detect'], enabled: true, is_built_in: true, updated_at: iso(-60 * 24 * 6) },
    { id: 'agent-evolver', name: 'Evolver', layer: 'planning', agent_type: 'evolver', mode: 'auto', description: '进化智能体：根据运行反馈优化 agent 配置', model_route_purpose: 'planning.strong', allowed_tools: [], capabilities: ['agent.evolve', 'config.optimize'], enabled: false, is_built_in: true, updated_at: iso(-60 * 24 * 5) },
    { id: 'agent-skill-learner', name: 'Skill Learner', layer: 'planning', agent_type: 'skill_learner', mode: 'auto', description: '技能学习智能体：从历史会话中归纳可复用技能', model_route_purpose: 'planning.strong', allowed_tools: [], capabilities: ['skill.extract', 'knowledge.persist'], enabled: false, is_built_in: true, updated_at: iso(-60 * 24 * 4) },
  ]
  const execution: AgentProfileDto[] = [
    { id: 'agent-code-explorer', name: 'Code Explorer', layer: 'execution', agent_type: 'code_explorer', mode: 'auto', description: '代码探查智能体：阅读代码、搜索符号、梳理调用路径', model_route_purpose: 'execution.fast', allowed_tools: ['read_file', 'list_directory', 'grep_content', 'glob_search'], capabilities: ['code.read', 'code.search'], enabled: true, is_built_in: true, updated_at: iso(-60 * 24 * 3) },
    { id: 'agent-code-writer', name: 'Code Writer', layer: 'execution', agent_type: 'code_writer', mode: 'auto', description: '代码编写智能体：应用补丁、编辑文件、生成测试', model_route_purpose: 'execution.strong', allowed_tools: ['read_file', 'apply_patch', 'code_editor', 'grep_content'], capabilities: ['code.write', 'code.test'], enabled: true, is_built_in: true, updated_at: iso(-60 * 24 * 2) },
    { id: 'agent-search-specialist', name: 'Search Specialist', layer: 'execution', agent_type: 'search_specialist', mode: 'auto', description: '搜索专家：执行复杂的代码与文档检索', model_route_purpose: 'execution.fast', allowed_tools: ['grep_content', 'glob_search', 'read_file'], capabilities: ['search.advanced'], enabled: true, is_built_in: true, updated_at: iso(-60 * 24 * 2) },
    { id: 'agent-file-finder', name: 'File Finder', layer: 'execution', agent_type: 'file_finder', mode: 'auto', description: '文件查找智能体：根据名称模式定位文件', model_route_purpose: 'execution.fast', allowed_tools: ['glob_search', 'list_directory'], capabilities: ['file.locate'], enabled: true, is_built_in: true, updated_at: iso(-60 * 24) },
    { id: 'agent-git-manager', name: 'Git Manager', layer: 'execution', agent_type: 'git_manager', mode: 'auto', description: 'Git 管理智能体：处理变更、提交、推送等 Git 操作', model_route_purpose: 'execution.fast', allowed_tools: ['git_worktree_manager', 'read_file'], capabilities: ['git.ops', 'diff.parse'], enabled: true, is_built_in: true, updated_at: iso(-60 * 12) },
    { id: 'agent-designer', name: 'Designer', layer: 'execution', agent_type: 'designer', mode: 'auto', description: '设计智能体：生成 UI 设计稿与样式规范', model_route_purpose: 'execution.strong', allowed_tools: ['read_file', 'apply_patch', 'code_editor'], capabilities: ['ui.design', 'style.generate'], enabled: true, is_built_in: true, updated_at: iso(-60 * 6) },
    { id: 'agent-test-runner', name: 'Test Runner', layer: 'execution', agent_type: 'test_runner', mode: 'auto', description: '测试运行智能体：执行测试套件并分析结果', model_route_purpose: 'execution.fast', allowed_tools: ['shell', 'read_file'], capabilities: ['test.run', 'result.analyze'], enabled: false, is_built_in: false, updated_at: iso(-60 * 3) },
    { id: 'agent-doc-writer', name: 'Doc Writer', layer: 'execution', agent_type: 'doc_writer', mode: 'auto', description: '文档编写智能体：生成与更新技术文档', model_route_purpose: 'execution.fast', allowed_tools: ['read_file', 'apply_patch', 'code_editor'], capabilities: ['docs.write', 'docs.update'], enabled: false, is_built_in: false, updated_at: iso(-60 * 2) },
  ]
  return [...planning, ...execution]
}

export function mockAgentModes(): AgentModeDto[] {
  return [
    { id: 'mode-auto', display_name: '自动', summary: '智能体自主决策，仅在必要时请求审批', max_parallel_executors: 4, worktree_isolation: false, approval_required: false, budget_policy: 'balanced' },
    { id: 'mode-manual', display_name: '手动', summary: '每一步都需要人工确认', max_parallel_executors: 1, worktree_isolation: true, approval_required: true, budget_policy: 'conservative' },
    { id: 'mode-yolo', display_name: '极速', summary: '跳过所有审批，最大并行度', max_parallel_executors: 8, worktree_isolation: false, approval_required: false, budget_policy: 'aggressive' },
    { id: 'mode-review', display_name: '审阅', summary: '执行后自动生成审阅报告', max_parallel_executors: 2, worktree_isolation: true, approval_required: false, budget_policy: 'balanced' },
  ]
}

// ============================================================
// 工具数据
// ============================================================

export function mockTools(): ToolDescriptorDto[] {
  return [
    { id: 'read_file', display_name: 'Read File', domain: 'code', source: 'builtin', risk: 'low', requires_approval: false, execute_endpoint: '/api/v1/code/tools/read_file/execute', capabilities: ['code.read'] },
    { id: 'list_directory', display_name: 'List Directory', domain: 'code', source: 'builtin', risk: 'low', requires_approval: false, execute_endpoint: '/api/v1/code/tools/list_directory/execute', capabilities: ['code.read', 'fs.list'] },
    { id: 'glob_search', display_name: 'Glob Search', domain: 'code', source: 'builtin', risk: 'low', requires_approval: false, execute_endpoint: '/api/v1/code/tools/glob_search/execute', capabilities: ['code.search', 'fs.glob'] },
    { id: 'grep_content', display_name: 'Grep Content', domain: 'code', source: 'builtin', risk: 'low', requires_approval: false, execute_endpoint: '/api/v1/code/tools/grep_content/execute', capabilities: ['code.search', 'content.grep'] },
    { id: 'apply_patch', display_name: 'Apply Patch', domain: 'code', source: 'builtin', risk: 'high', requires_approval: true, execute_endpoint: '/api/v1/code/tools/apply_patch/execute', capabilities: ['code.write'] },
    { id: 'code_editor', display_name: 'Code Editor', domain: 'code', source: 'builtin', risk: 'medium', requires_approval: false, execute_endpoint: '/api/v1/code/tools/code_editor/execute', capabilities: ['code.write', 'code.edit'] },
    { id: 'git_worktree_manager', display_name: 'Git Worktree Manager', domain: 'git', source: 'builtin', risk: 'medium', requires_approval: true, execute_endpoint: '/api/v1/code/tools/git_worktree_manager/execute', capabilities: ['git.ops', 'diff.parse'] },
    { id: 'shell', display_name: 'Shell', domain: 'system', source: 'builtin', risk: 'high', requires_approval: true, execute_endpoint: '/api/v1/tools/shell', capabilities: ['shell.exec'] },
    { id: 'web_search', display_name: 'Web Search', domain: 'web', source: 'extension', risk: 'low', requires_approval: false, execute_endpoint: '/api/v1/code/tools/web_search/execute', capabilities: ['web.search'] },
    { id: 'web_fetch', display_name: 'Web Fetch', domain: 'web', source: 'extension', risk: 'medium', requires_approval: false, execute_endpoint: '/api/v1/code/tools/web_fetch/execute', capabilities: ['web.fetch'] },
    { id: 'image_gen', display_name: 'Image Generator', domain: 'media', source: 'extension', risk: 'medium', requires_approval: true, execute_endpoint: '/api/v1/code/tools/image_gen/execute', capabilities: ['media.generate'] },
    { id: 'db_query', display_name: 'Database Query', domain: 'data', source: 'extension', risk: 'high', requires_approval: true, execute_endpoint: '/api/v1/code/tools/db_query/execute', capabilities: ['data.query'] },
    { id: 'http_request', display_name: 'HTTP Request', domain: 'network', source: 'extension', risk: 'medium', requires_approval: false, execute_endpoint: '/api/v1/code/tools/http_request/execute', capabilities: ['network.http'] },
    { id: 'json_transform', display_name: 'JSON Transform', domain: 'data', source: 'builtin', risk: 'low', requires_approval: false, execute_endpoint: '/api/v1/code/tools/json_transform/execute', capabilities: ['data.transform'] },
    { id: 'markdown_render', display_name: 'Markdown Render', domain: 'text', source: 'builtin', risk: 'low', requires_approval: false, execute_endpoint: '/api/v1/code/tools/markdown_render/execute', capabilities: ['text.render'] },
    { id: 'diff_viewer', display_name: 'Diff Viewer', domain: 'text', source: 'builtin', risk: 'low', requires_approval: false, execute_endpoint: '/api/v1/code/tools/diff_viewer/execute', capabilities: ['text.diff'] },
  ]
}

export function mockHarnessManifest(): HarnessManifestDto {
  return {
    runtime: 'TinadecCore 0.4.2',
    ownership_model: 'layered',
    tool_registry: {
      declared_tool_count: 16,
      canonical_tool_count: 16,
      duplicate_tool_id_count: 0,
      duplicate_tool_ids: [],
      source_precedence: ['builtin', 'extension', 'mcp', 'acp'],
      selection_policy: 'first-source-wins',
    },
    agent_layers: [
      { layer: 'planning', role: '规划层：分析、拆解、调度', agent_count: 7, enabled_agent_count: 5, max_parallel_executors: 1, worktree_isolation: false, approval_required: false, agent_types: ['meeting', 'task_planner', 'context_compressor', 'prompt_context_engineer', 'supervisor', 'evolver', 'skill_learner'], tool_ids: ['read_file', 'grep_content'] },
      { layer: 'execution', role: '执行层：探查、编写、测试', agent_count: 8, enabled_agent_count: 6, max_parallel_executors: 4, worktree_isolation: false, approval_required: false, agent_types: ['code_explorer', 'code_writer', 'search_specialist', 'file_finder', 'git_manager', 'designer', 'test_runner', 'doc_writer'], tool_ids: ['read_file', 'list_directory', 'grep_content', 'glob_search', 'apply_patch', 'code_editor', 'git_worktree_manager', 'shell'] },
    ],
    tool_providers: [
      { source: 'builtin', display_name: '内置工具', layer: 'code', status: 'active', tool_count: 12, active_tool_count: 12, future_tool_count: 0, approval_required_count: 4, read_only_count: 6, capability_prefixes: ['code.', 'fs.', 'git.', 'shell.', 'text.'] },
      { source: 'extension', display_name: '扩展工具', layer: 'code', status: 'active', tool_count: 4, active_tool_count: 4, future_tool_count: 0, approval_required_count: 2, read_only_count: 2, capability_prefixes: ['web.', 'media.', 'data.', 'network.'] },
    ],
    tool_risks: [
      { risk: 'low', tool_count: 9, requires_human_checkpoint: false, policy_summary: '只读与无副作用操作' },
      { risk: 'medium', tool_count: 4, requires_human_checkpoint: false, policy_summary: '可逆写操作，需审计' },
      { risk: 'high', tool_count: 3, requires_human_checkpoint: true, policy_summary: '不可逆或高影响操作，必须审批' },
    ],
    tools: mockTools(),
    design_notes: [
      '工具注册采用源优先级策略：builtin > extension > mcp > acp',
      '高风险工具必须经过审批流程，由 Supervisor 智能体检查',
      '执行层智能体最大并行度为 4，可通过 agent mode 调整',
    ],
  }
}

// ============================================================
// Prompt Fragments
// ============================================================

export function mockPromptFragments(): PromptFragmentDto[] {
  return [
    { id: 'pf-001', key: 'system.base', title: '系统基础提示', scope: 'global', target_agent_id: null, category: 'system', content: '你是 TinadecCode 的智能体，负责协助开发者完成软件工程任务。', priority: 100, enabled: true, is_builtin: true, created_at: iso(-60 * 24 * 30), updated_at: iso(-60 * 24 * 10) },
    { id: 'pf-002', key: 'agent.meeting.role', title: '会议智能体角色', scope: 'agent', target_agent_id: 'agent-meeting', category: 'role', content: '你是会议智能体，负责分析用户意图、拆解任务并调度其他智能体。', priority: 90, enabled: true, is_builtin: true, created_at: iso(-60 * 24 * 30), updated_at: iso(-60 * 24 * 9) },
    { id: 'pf-003', key: 'agent.code_writer.guard', title: '代码编写安全约束', scope: 'agent', target_agent_id: 'agent-code-writer', category: 'guard', content: '在应用补丁前必须读取目标文件；禁止删除超过 50 行的代码块而不提供替换。', priority: 80, enabled: true, is_builtin: true, created_at: iso(-60 * 24 * 20), updated_at: iso(-60 * 24 * 2) },
    { id: 'pf-004', key: 'context.compress.policy', title: '上下文压缩策略', scope: 'global', target_agent_id: null, category: 'policy', content: '当 token 超过预算 80% 时触发压缩，保留最近 5 轮对话与关键证据。', priority: 70, enabled: true, is_builtin: false, created_at: iso(-60 * 24 * 15), updated_at: iso(-60 * 24 * 5) },
    { id: 'pf-005', key: 'supervision.checklist', title: '监督检查清单', scope: 'agent', target_agent_id: 'agent-supervisor', category: 'checklist', content: '检查项：测试覆盖率、向后兼容、安全策略、性能回归。', priority: 75, enabled: false, is_builtin: false, created_at: iso(-60 * 24 * 8), updated_at: iso(-60 * 24 * 3) },
  ]
}

// ============================================================
// 扩展市场数据
// ============================================================

export function mockExtensionSources(): ExtensionSourceDto[] {
  return [
    { id: 'src-builtin-001', name: 'Tinadec Curated', kind: 'marketplace-url', location: 'tinadec://marketplace/curated', enabled: true, last_refreshed_at: iso(-60 * 6), created_at: iso(-60 * 24 * 30) },
    { id: 'src-local-002', name: '本地扩展目录', kind: 'directory', location: 'D:/workspace/extensions', enabled: true, last_refreshed_at: iso(-60 * 12), created_at: iso(-60 * 24 * 20) },
  ]
}

export function mockMarketCatalog(): MarketCatalogItemDto[] {
  return [
    { catalog_id: 'cat-001', source_id: 'src-builtin-001', extension_id: 'web-search-pro', kind: 'tool-pack', version: '1.2.0', publisher: 'Tinadec', display_name: 'Web Search Pro', description: '增强的网页搜索工具包，支持多引擎与结果聚合', source_kind: 'marketplace-url', source_location: 'tinadec://marketplace/packs/web-search-pro', capabilities: ['web.search', 'web.aggregate'], permissions: ['network:read'], status: 'available', installed_extension_id: null },
    { catalog_id: 'cat-002', source_id: 'src-builtin-001', extension_id: 'github-mcp', kind: 'mcp-server', version: '0.3.1', publisher: 'Tinadec', display_name: 'GitHub MCP Server', description: '通过 MCP 协议接入 GitHub，支持仓库、Issue、PR 管理', source_kind: 'marketplace-url', source_location: 'tinadec://marketplace/servers/github-mcp', capabilities: ['github.repo', 'github.issue', 'github.pr'], permissions: ['network:read', 'network:write'], status: 'available', installed_extension_id: null },
    { catalog_id: 'cat-003', source_id: 'src-builtin-001', extension_id: 'figma-acp', kind: 'acp-adapter', version: '0.1.0', publisher: 'Tinadec', display_name: 'Figma ACP Adapter', description: '通过 ACP 协议接入 Figma，支持设计稿读取与导出', source_kind: 'marketplace-url', source_location: 'tinadec://marketplace/adapters/figma-acp', capabilities: ['figma.read', 'figma.export'], permissions: ['network:read'], status: 'available', installed_extension_id: null },
    { catalog_id: 'cat-004', source_id: 'src-builtin-001', extension_id: 'db-tools', kind: 'tool-pack', version: '2.0.0', publisher: 'Tinadec', display_name: 'Database Tools', description: '数据库查询与迁移工具包，支持 PostgreSQL / MySQL / SQLite', source_kind: 'marketplace-url', source_location: 'tinadec://marketplace/packs/db-tools', capabilities: ['data.query', 'data.migrate'], permissions: ['fs:read', 'fs:write'], status: 'available', installed_extension_id: null },
    { catalog_id: 'cat-005', source_id: 'src-builtin-001', extension_id: 'image-gen-skill', kind: 'skill', version: '1.0.0', publisher: 'Tinadec', display_name: 'Image Generation Skill', description: '为智能体添加图像生成能力，支持 DALL-E / Stable Diffusion', source_kind: 'marketplace-url', source_location: 'tinadec://marketplace/skills/image-gen', capabilities: ['media.generate'], permissions: ['network:read', 'network:write'], status: 'available', installed_extension_id: null },
    { catalog_id: 'cat-006', source_id: 'src-local-002', extension_id: 'custom-linter', kind: 'tool-pack', version: '0.4.2', publisher: 'Local', display_name: 'Custom Linter', description: '本地自定义代码检查工具', source_kind: 'directory', source_location: 'D:/workspace/extensions/custom-linter', capabilities: ['code.lint'], permissions: ['fs:read'], status: 'available', installed_extension_id: null },
  ]
}

export function mockInstalledExtensions(): InstalledExtensionDto[] {
  return [
    { id: 'ext-001', catalog_id: 'cat-001', extension_id: 'web-search-pro', kind: 'tool-pack', version: '1.2.0', publisher: 'Tinadec', display_name: 'Web Search Pro', description: '增强的网页搜索工具包', source_kind: 'marketplace-url', source_location: 'tinadec://marketplace/packs/web-search-pro', capabilities: ['web.search', 'web.aggregate'], permissions: ['network:read'], enabled: true, status: 'active', status_message: '运行正常', installed_at: iso(-60 * 24 * 5), updated_at: iso(-60 * 24 * 5) },
    { id: 'ext-002', catalog_id: 'cat-002', extension_id: 'github-mcp', kind: 'mcp-server', version: '0.3.1', publisher: 'Tinadec', display_name: 'GitHub MCP Server', description: '通过 MCP 协议接入 GitHub', source_kind: 'marketplace-url', source_location: 'tinadec://marketplace/servers/github-mcp', capabilities: ['github.repo', 'github.issue', 'github.pr'], permissions: ['network:read', 'network:write'], enabled: true, status: 'active', status_message: 'MCP 服务已连接', installed_at: iso(-60 * 24 * 3), updated_at: iso(-60 * 24 * 3) },
    { id: 'ext-003', catalog_id: null, extension_id: 'legacy-tool', kind: 'tool-pack', version: '0.1.0', publisher: 'Local', display_name: 'Legacy Tool', description: '已弃用的旧工具', source_kind: 'directory', source_location: 'D:/workspace/extensions/legacy', capabilities: ['legacy.op'], permissions: ['fs:read'], enabled: false, status: 'disabled', status_message: '已手动禁用', installed_at: iso(-60 * 24 * 40), updated_at: iso(-60 * 24 * 10) },
  ]
}

export function mockMcpServers(): McpServerDto[] {
  return [
    { id: 'mcp-001', extension_id: 'github-mcp', name: 'GitHub MCP', transport: 'stdio', status: 'connected', tools: ['github.list_repos', 'github.create_issue', 'github.create_pr'], updated_at: iso(-60 * 3) },
    { id: 'mcp-002', extension_id: 'filesystem-mcp', name: 'Filesystem MCP', transport: 'stdio', status: 'disconnected', tools: [], updated_at: iso(-60 * 24) },
  ]
}

export function mockAcpAdapters(): AcpAdapterDto[] {
  return [
    { id: 'acp-001', extension_id: 'figma-acp', name: 'Figma ACP', command: 'npx @tinadec/figma-acp', status: 'idle', status_message: '等待首次调用', capabilities: ['figma.read', 'figma.export'], updated_at: iso(-60 * 6) },
  ]
}

// ============================================================
// Git Diff 预览数据
// ============================================================

const SAMPLE_DIFF = `diff --git a/src/orchestrator.ts b/src/orchestrator.ts
index 1a2b3c4..5d6e7f8 100644
--- a/src/orchestrator.ts
+++ b/src/orchestrator.ts
@@ -40,12 +40,18 @@ export class OrchestratorService {
   private readonly agents: AgentRegistry;
   private readonly tools: ToolRegistry;
 
-  async buildTaskGraph(sessionId: string, userMessage: string): Promise<TaskGraphDto> {
-    const nodes = this.parseStaticTasks(userMessage);
-    return { id: genId(), run_id: '', session_id: sessionId, title: 'Static Plan', status: 'active', nodes };
+  async buildTaskGraph(sessionId: string, userMessage: string): Promise<TaskGraphDto> {
+    const intent = await this.analyzeIntent(userMessage);
+    const nodes = await this.parseDynamicTasks(intent);
+    const resolved = this.resolver.resolve(nodes);
+    return { id: genId(), run_id: '', session_id: sessionId, title: intent.summary, status: 'active', nodes: resolved };
   }
 
-  private parseStaticTasks(message: string): TaskNodeDto[] {
-    return [{ id: genId(), title: message.slice(0, 80), status: 'pending', dependencies: [] }];
+  private async parseDynamicTasks(intent: IntentDto): Promise<TaskNodeDto[]> {
+    const tasks = await this.planner.decompose(intent);
+    return tasks.map((t) => ({ ...t, dependencies: t.dependencies ?? [] }));
   }
 }
diff --git a/src/graph.ts b/src/graph.ts
index 9a8b7c6..5d4e3f2 100644
--- a/src/graph.ts
+++ b/src/graph.ts
@@ -1,5 +1,14 @@
+export interface DynamicDependency {
+  source: string;
+  target: string;
+  resolver_hint?: string | null;
+}
+
 export class TaskGraph {
-  constructor(public readonly nodes: TaskNodeDto[]) {}
+  constructor(public readonly nodes: TaskNodeDto[]) {
+    this.validate();
+  }
+
+  private validate(): void { /* cycle detection */ }
 }
diff --git a/src/__tests__/orch.test.ts b/src/__tests__/orch.test.ts
index 1a2b3c4..5d6e7f8 100644
--- a/src/__tests__/orch.test.ts
+++ b/src/__tests__/orch.test.ts
@@ -1,3 +1,15 @@
+import { describe, it, expect } from 'vitest';
+import { OrchestratorService } from '../orchestrator';
+
+describe('OrchestratorService', () => {
+  it('builds dynamic task graph', async () => {
+    const orch = new OrchestratorService(registry, tools);
+    const graph = await orch.buildTaskGraph('sess-1', 'refactor engine');
+    expect(graph.nodes.length).toBeGreaterThan(0);
+  });
+});
+
 // existing tests below
`

export function mockGitDiffPreview(): CodeToolExecuteResultDto {
  return {
    tool_id: 'git_worktree_manager',
    status: 'ok',
    summary: '检测到 3 个变更文件（+27 -8）',
    evidence: ['src/orchestrator.ts', 'src/graph.ts', 'src/__tests__/orch.test.ts'],
    requires_approval: false,
    approval_summary: null,
    data: {
      git_root: 'D:/workspace/tinadec',
      branch: 'feature/orchestrator-refactor',
      upstream: 'origin/feature/orchestrator-refactor',
      ahead: 2,
      behind: 0,
      has_uncommitted_changes: true,
      files: [
        { path: 'src/orchestrator.ts', staged_status: 'M', unstaged_status: 'M', status: 'M', is_untracked: false, is_conflicted: false, is_renamed: false },
        { path: 'src/graph.ts', staged_status: null, unstaged_status: 'M', status: 'M', is_untracked: false, is_conflicted: false, is_renamed: false },
        { path: 'src/__tests__/orch.test.ts', staged_status: null, unstaged_status: 'M', status: 'M', is_untracked: false, is_conflicted: false, is_renamed: false },
      ],
      sections: [
        {
          id: 'working_tree',
          kind: 'working_tree',
          title: '工作区变更',
          subtitle: '未暂存的修改',
          base_ref: null,
          head_ref: null,
          diff: SAMPLE_DIFF,
          files: [
            { path: 'src/orchestrator.ts', previous_path: null, change_type: 'modified', additions: 12, deletions: 4, binary: false, truncated: false },
            { path: 'src/graph.ts', previous_path: null, change_type: 'modified', additions: 9, deletions: 1, binary: false, truncated: false },
            { path: 'src/__tests__/orch.test.ts', previous_path: null, change_type: 'modified', additions: 12, deletions: 0, binary: false, truncated: false },
          ],
          file_count: 3,
          additions: 33,
          deletions: 5,
          notices: [],
        },
        {
          id: 'staged',
          kind: 'staged',
          title: '已暂存变更',
          subtitle: '等待提交',
          base_ref: null,
          head_ref: null,
          diff: 'diff --git a/src/orchestrator.ts b/src/orchestrator.ts\nindex 1a2b3c4..5d6e7f8 100644\n--- a/src/orchestrator.ts\n+++ b/src/orchestrator.ts\n@@ -40,3 +40,5 @@\n-  async buildTaskGraph() {\n+  async buildTaskGraph() {\n+    // staged version\n+  }\n',
          files: [
            { path: 'src/orchestrator.ts', previous_path: null, change_type: 'modified', additions: 2, deletions: 1, binary: false, truncated: false },
          ],
          file_count: 1,
          additions: 2,
          deletions: 1,
          notices: [],
        },
        {
          id: 'branch_range',
          kind: 'branch_range',
          title: '分支比较',
          subtitle: 'feature/orchestrator-refactor vs main',
          base_ref: 'main',
          head_ref: 'feature/orchestrator-refactor',
          diff: 'diff --git a/README.md b/README.md\nindex 111..222 100644\n--- a/README.md\n+++ b/README.md\n@@ -1,3 +1,5 @@\n+# Orchestrator Refactor\n+\n This project...\n',
          files: [
            { path: 'README.md', previous_path: null, change_type: 'modified', additions: 2, deletions: 0, binary: false, truncated: false },
          ],
          file_count: 1,
          additions: 2,
          deletions: 0,
          notices: ['base=main head=feature/orchestrator-refactor'],
        },
      ],
    },
  }
}

export function mockGitPushPlan(): CodeToolExecuteResultDto {
  return {
    tool_id: 'git_worktree_manager',
    status: 'ok',
    summary: '可推送：2 个提交领先于 origin/feature/orchestrator-refactor',
    evidence: ['ahead=2', 'behind=0'],
    requires_approval: true,
    approval_summary: '推送 2 个提交到 origin/feature/orchestrator-refactor',
    data: {
      git_root: 'D:/workspace/tinadec',
      branch: 'feature/orchestrator-refactor',
      upstream: 'origin/feature/orchestrator-refactor',
      ahead: 2,
      behind: 0,
      has_uncommitted_changes: true,
      diff_stat: ' src/orchestrator.ts | 12 +++++++---\n src/graph.ts      |  9 ++++++-\n 2 files changed, 17 insertions(+), 4 deletions(-)',
      recent_commits: [
        'a1b2c3d refactor(orchestrator): support dynamic dependency resolution',
        'e4f5g6h feat(graph): add cycle detection',
        '7i8j9k0 docs: update orchestrator architecture',
      ],
      remotes: ['origin  https://github.com/example/tinadec.git (fetch)', 'origin  https://github.com/example/tinadec.git (push)'],
      push_ready: true,
      push_blockers: [],
      suggested_commands: ['git push origin feature/orchestrator-refactor'],
      needs_push: true,
      worktrees: [
        { branch: 'feature/orchestrator-refactor', path: 'D:/workspace/tinadec', detached: null },
        { branch: 'main', path: 'D:/workspace/tinadec/.worktrees/main', detached: null },
      ],
    },
  }
}

// ============================================================
// 文件树数据
// ============================================================

export function mockFileTree() {
  return {
    entries: [
      { name: 'src', is_dir: true, is_file: false, size_bytes: null },
      { name: 'tests', is_dir: true, is_file: false, size_bytes: null },
      { name: 'package.json', is_dir: false, is_file: true, size_bytes: 2048 },
      { name: 'tsconfig.json', is_dir: false, is_file: true, size_bytes: 512 },
      { name: 'README.md', is_dir: false, is_file: true, size_bytes: 4096 },
      { name: '.gitignore', is_dir: false, is_file: true, size_bytes: 128 },
    ],
  }
}

export function mockFileTreeSrc() {
  return {
    entries: [
      { name: 'orchestrator.ts', is_dir: false, is_file: true, size_bytes: 12288 },
      { name: 'graph.ts', is_dir: false, is_file: true, size_bytes: 6144 },
      { name: 'types.ts', is_dir: false, is_file: true, size_bytes: 3072 },
      { name: 'index.ts', is_dir: false, is_file: true, size_bytes: 256 },
      { name: '__tests__', is_dir: true, is_file: false, size_bytes: null },
    ],
  }
}

// ============================================================
// 代码内容
// ============================================================

export function mockCodeContent(filename: string): string {
  if (filename.endsWith('.ts')) {
    if (filename.includes('orchestrator')) {
      return `import { AgentRegistry } from './agents'
import { ToolRegistry } from './tools'
import { TaskGraph } from './graph'

export class OrchestratorService {
  private readonly agents: AgentRegistry
  private readonly tools: ToolRegistry
  private readonly resolver: DynamicDependencyResolver

  constructor(agents: AgentRegistry, tools: ToolRegistry) {
    this.agents = agents
    this.tools = tools
    this.resolver = new DynamicDependencyResolver()
  }

  async buildTaskGraph(sessionId: string, userMessage: string): Promise<TaskGraphDto> {
    const intent = await this.analyzeIntent(userMessage)
    const nodes = await this.parseDynamicTasks(intent)
    const resolved = this.resolver.resolve(nodes)
    return {
      id: genId(),
      run_id: '',
      session_id: sessionId,
      title: intent.summary,
      status: 'active',
      nodes: resolved,
    }
  }

  private async analyzeIntent(message: string): Promise<IntentDto> {
    // 使用规划层智能体分析用户意图
    return this.agents.meeting.analyze(message)
  }

  private async parseDynamicTasks(intent: IntentDto): Promise<TaskNodeDto[]> {
    const tasks = await this.agents.planner.decompose(intent)
    return tasks.map((t) => ({ ...t, dependencies: t.dependencies ?? [] }))
  }

  async execute(graph: TaskGraphDto, assignments: AgentAssignmentDto[]): Promise<void> {
    for (const assignment of assignments) {
      const agent = this.agents.get(assignment.agent_id)
      await agent.run(assignment)
    }
  }
}
`
    }
    return `export interface TaskNodeDto {
  id: string
  title: string
  description: string
  status: string
  priority: number
  risk: string
  success_criteria: string[]
  dependencies: string[]
  required_capabilities: string[]
}

export interface DynamicDependency {
  source: string
  target: string
  resolver_hint?: string | null
}

export class TaskGraph {
  constructor(public readonly nodes: TaskNodeDto[]) {
    this.validate()
  }

  private validate(): void {
    // 循环检测
    const visited = new Set<string>()
    const stack = new Set<string>()
    for (const node of this.nodes) {
      if (this.hasCycle(node.id, visited, stack)) {
        throw new Error('Detected cycle in task graph')
      }
    }
  }

  private hasCycle(id: string, visited: Set<string>, stack: Set<string>): boolean {
    if (stack.has(id)) return true
    if (visited.has(id)) return false
    visited.add(id)
    stack.add(id)
    const node = this.nodes.find((n) => n.id === id)
    if (node) {
      for (const dep of node.dependencies) {
        if (this.hasCycle(dep, visited, stack)) return true
      }
    }
    stack.delete(id)
    return false
  }
}
`
  }
  if (filename.endsWith('.json')) {
    return `{
  "name": "tinadec",
  "version": "0.4.2",
  "private": true,
  "scripts": {
    "dev": "vite",
    "build": "vue-tsc --noEmit && vite build",
    "test": "vitest run"
  },
  "dependencies": {
    "vue": "^3.5.0",
    "vue-router": "^5.0.0"
  }
}
`
  }
  if (filename.endsWith('.md')) {
    return `# TinadecCode

> 智能体驱动的软件工程桌面应用

## 快速开始

\`\`\`bash
npm install
npm run dev
\`\`\`

## 架构

- **规划层**：会议智能体、任务规划、上下文压缩
- **执行层**：代码探查、代码编写、Git 管理
- **监督层**：监督检查、风险评估
`
  }
  return `// ${filename}\n// 示例内容\n`
}

// ============================================================
// Mock 数据包
// ============================================================

export interface MockDataBundle {
  projects: ProjectDto[]
  sessions: SessionDto[]
  messages: MessageDto[]
  approvals: ApprovalDto[]
  orchestration: OrchestrationSnapshotDto | null
  toolExecutions: ToolExecutionTimelineItemDto[]
  events: EventEnvelope[]
  doctor: DoctorReportDto | null
  readiness: RuntimeReadinessReceiptDto | null
  modelSettings: ModelSettingsDto | null
  modelProviders: ModelProviderInstanceDto[]
  modelRoutes: ModelRouteDto[]
  agents: AgentProfileDto[]
  agentModes: AgentModeDto[]
  tools: ToolDescriptorDto[]
  harnessManifest: HarnessManifestDto | null
  promptFragments: PromptFragmentDto[]
  extensionSources: ExtensionSourceDto[]
  marketCatalog: MarketCatalogItemDto[]
  installedExtensions: InstalledExtensionDto[]
  mcpServers: McpServerDto[]
  acpAdapters: AcpAdapterDto[]
  gitDiffPreview: CodeToolExecuteResultDto | null
  gitPushPlan: CodeToolExecuteResultDto | null
}

export function buildMockDataBundle(sessionId: string = 'sess-tinadec-1001'): MockDataBundle {
  return {
    projects: mockProjects(),
    sessions: mockSessions(),
    messages: mockMessages(sessionId),
    approvals: mockApprovals(sessionId),
    orchestration: mockOrchestrationSnapshot(sessionId),
    toolExecutions: mockToolExecutions(sessionId),
    events: mockEvents(sessionId),
    doctor: mockDoctor(),
    readiness: mockReadiness(),
    modelSettings: mockModelSettings(),
    modelProviders: mockModelProviders(),
    modelRoutes: mockModelRoutes(),
    agents: mockAgents(),
    agentModes: mockAgentModes(),
    tools: mockTools(),
    harnessManifest: mockHarnessManifest(),
    promptFragments: mockPromptFragments(),
    extensionSources: mockExtensionSources(),
    marketCatalog: mockMarketCatalog(),
    installedExtensions: mockInstalledExtensions(),
    mcpServers: mockMcpServers(),
    acpAdapters: mockAcpAdapters(),
    gitDiffPreview: mockGitDiffPreview(),
    gitPushPlan: mockGitPushPlan(),
  }
}
