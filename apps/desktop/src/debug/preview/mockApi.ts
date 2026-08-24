/**
 * Mock API 层
 * 创建与真实 api 对象接口相同的 mock api，根据当前场景返回不同的 mock 数据。
 * 所有方法返回 Promise，模拟网络延迟。
 */
import { computed, type Ref } from 'vue'
import type {
  ProjectDto,
  SessionDto,
  MessageDto,
  ApprovalDto,
  OrchestrationSnapshotDto,
  ToolExecutionTimelineItemDto,
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
  CodeToolExecuteRequestDto,
  EventEnvelope,
  SaveModelProviderInstanceInput,
  SavePromptFragmentInput,
  PromptContextPreviewInput,
  PromptContextPreviewDto,
  ModelReadinessReceiptDto,
  ModelCatalogReadinessReceiptDto,
  ToolLayerReadinessReceiptDto,
  ToolSearchResultDto,
  AgentCandidateDto,
  OrchestrationRunDto,
  TaskNodeDto,
  ContextPackDto,
  SupervisionFindingDto,
  ExtensionInstallPreviewDto,
  ExtensionInstallResultDto,
  ModelProviderTemplateDto,
  ProjectDto as _ProjectDto,
} from '@/api'
import { buildScenarioData, isLoadingScenario, type ScenarioId } from './scenarios'
import {
  mockCodeContent,
  mockFileTree,
  mockFileTreeSrc,
  mockGitDiffPreview,
  mockGitPushPlan,
} from './mockData'

const DEFAULT_SESSION_ID = 'sess-tinadec-1001'
const DEFAULT_CWD = 'D:/workspace/tinadec'

function delay<T>(value: T, scenarioId: ScenarioId): Promise<T> {
  if (isLoadingScenario(scenarioId)) {
    // 加载场景：返回一个长时间不 resolve 的 Promise（模拟挂起）
    return new Promise<T>(() => {})
  }
  const ms = 100 + Math.random() * 200
  return new Promise<T>((resolve) => {
    setTimeout(() => resolve(value), ms)
  })
}

function emptyDelay<T>(value: T, scenarioId: ScenarioId): Promise<T> {
  if (isLoadingScenario(scenarioId)) {
    return new Promise<T>(() => {})
  }
  return Promise.resolve(value)
}

/**
 * 创建 mock api 对象
 * @param scenario 当前场景的 ref
 */
export function createMockApi(scenario: Ref<ScenarioId>) {
  function data() {
    return buildScenarioData(scenario.value, DEFAULT_SESSION_ID)
  }

  const api = {
    gatewayUrl: 'http://127.0.0.1:48730',

    health: () => delay({ status: 'ok' }, scenario.value),
    doctor: () => delay(data().doctor as DoctorReportDto, scenario.value),
    readiness: () => delay(data().readiness as RuntimeReadinessReceiptDto, scenario.value),
    getToolLayerReadiness: () =>
      delay(
        {
          status: 'ready',
          generated_at: new Date().toISOString(),
          runtime: 'TinadecCore 0.4.2',
          receipt_id: 'rcpt-tl-001',
          tool_count: 16,
          ready_tool_count: 16,
          warning_tool_count: 0,
          blocked_tool_count: 0,
          execution_agent_count: 8,
          ready_agent_count: 6,
          warning_agent_count: 2,
          blocked_agent_count: 0,
          approval_gated_tool_count: 4,
          human_checkpoint_tool_count: 3,
          future_tool_count: 0,
          unresolved_scope_count: 0,
          tools: [],
          agent_scopes: [],
          design_notes: [],
        } as ToolLayerReadinessReceiptDto,
        scenario.value,
      ),

    listProjects: () => delay(data().projects as ProjectDto[], scenario.value),
    createProject: (name: string, path: string) =>
      delay({ id: `proj-new-${Date.now()}`, name, path, created_at: new Date().toISOString() } as ProjectDto, scenario.value),

    listSessions: (projectId?: string) =>
      delay(
        projectId
          ? data().sessions.filter((s) => s.project_id === projectId)
          : data().sessions,
        scenario.value,
      ) as Promise<SessionDto[]>,
    createSession: (projectId: string, title?: string) =>
      delay(
        {
          id: `sess-new-${Date.now()}`,
          project_id: projectId,
          title: title ?? '新会话',
          status: 'active',
          created_at: new Date().toISOString(),
          updated_at: new Date().toISOString(),
        } as SessionDto,
        scenario.value,
      ),
    updateSessionTitle: (sessionId: string, title: string) =>
      delay(
        { ...data().sessions[0], id: sessionId, title } as SessionDto,
        scenario.value,
      ),

    listMessages: (sessionId: string) =>
      delay(
        data().messages.filter((m) => m.session_id === sessionId),
        scenario.value,
      ) as Promise<MessageDto[]>,
    postMessage: (sessionId: string, content: string) =>
      delay(
        {
          id: `msg-new-${Date.now()}`,
          session_id: sessionId,
          role: 'user',
          content,
          created_at: new Date().toISOString(),
        } as MessageDto,
        scenario.value,
      ),

    getOrchestrationSnapshot: (sessionId: string) =>
      delay(
        data().orchestration
          ? { ...data().orchestration!, run: data().orchestration?.run ? { ...data().orchestration!.run!, session_id: sessionId } : null }
          : { nodes: [], assignments: [], step_results: [], context_packs: [], supervision_findings: [] },
        scenario.value,
      ) as Promise<OrchestrationSnapshotDto>,

    listToolExecutions: (sessionId: string, _params: { run_id?: string; limit?: number } = {}) =>
      delay(
        data().toolExecutions.filter((t) => t.session_id === sessionId),
        scenario.value,
      ) as Promise<ToolExecutionTimelineItemDto[]>,

    listRuns: (_sessionId: string) =>
      delay(data().orchestration?.run ? [data().orchestration!.run!] : [], scenario.value) as Promise<OrchestrationRunDto[]>,
    listTaskNodes: (_sessionId: string) =>
      delay(data().orchestration?.nodes ?? [], scenario.value) as Promise<TaskNodeDto[]>,
    listContextPacks: (_sessionId: string) =>
      delay(data().orchestration?.context_packs ?? [], scenario.value) as Promise<ContextPackDto[]>,
    listSupervisionFindings: (_sessionId: string) =>
      delay(data().orchestration?.supervision_findings ?? [], scenario.value) as Promise<SupervisionFindingDto[]>,

    listApprovals: (sessionId?: string, status?: string) =>
      delay(
        data().approvals.filter((a) => {
          if (sessionId && a.session_id !== sessionId) return false
          if (status && a.status !== status) return false
          return true
        }),
        scenario.value,
      ) as Promise<ApprovalDto[]>,
    decideApproval: (approvalId: string, decision: 'approved' | 'rejected') =>
      delay(
        {
          ...data().approvals[0],
          id: approvalId,
          status: decision,
          decided_at: new Date().toISOString(),
        } as ApprovalDto,
        scenario.value,
      ),
    listModelProviderTemplates: () =>
      delay(
        [
          { provider_family: 'openai', driver: 'openai-compatible', display_name: 'OpenAI', connection_kind: 'api-key', credential_kind: 'api-key', summary: 'OpenAI 官方 API', contributor_description: '内置', default_base_url: 'https://api.openai.com/v1', default_model: 'gpt-4o-mini', default_timeout_seconds: 60, capabilities: { supports_streaming: true, supports_tools: true, supports_json_mode: true, supports_system_prompt: true, max_context_tokens: 128000, requires_workspace: false, credential_kind: 'api-key', health_status: 'healthy' } },
        ] as ModelProviderTemplateDto[],
        scenario.value,
      ),
    listModelProviders: () => delay(data().modelProviders as ModelProviderInstanceDto[], scenario.value),
    getModelReadiness: () =>
      delay(
        {
          status: 'ready',
          generated_at: new Date().toISOString(),
          receipt_id: 'rcpt-model-001',
          provider_count: 3,
          ready_provider_count: 2,
          warning_provider_count: 0,
          blocked_provider_count: 1,
          route_count: 3,
          ready_route_count: 3,
          warning_route_count: 0,
          blocked_route_count: 0,
          providers: [],
          routes: [],
          design_notes: [],
        } as ModelReadinessReceiptDto,
        scenario.value,
      ),
    getModelCatalogReadiness: () =>
      delay(
        {
          status: 'ready',
          generated_at: new Date().toISOString(),
          receipt_id: 'rcpt-cat-001',
          template_count: 5,
          ready_template_count: 5,
          warning_template_count: 0,
          blocked_template_count: 0,
          runtime_module_count: 3,
          configured_provider_count: 3,
          advisory_probe_template_count: 0,
          templates: [],
          design_notes: [],
        } as ModelCatalogReadinessReceiptDto,
        scenario.value,
      ),
    createModelProvider: (provider: SaveModelProviderInstanceInput) =>
      delay({ ...data().modelProviders[0], ...provider, id: `mp-new-${Date.now()}` } as ModelProviderInstanceDto, scenario.value),
    saveModelProvider: (providerId: string, provider: SaveModelProviderInstanceInput) =>
      delay({ ...data().modelProviders[0], ...provider, id: providerId } as ModelProviderInstanceDto, scenario.value),
    deleteModelProvider: (_providerId: string) => emptyDelay(undefined as unknown as void, scenario.value),

    listModelRoutes: () => delay(data().modelRoutes as ModelRouteDto[], scenario.value),
    saveModelRoute: (purpose: string, providerInstanceId: string, model?: string | null) =>
      delay(
        {
          purpose,
          provider_instance_id: providerInstanceId,
          model: model ?? null,
          updated_at: new Date().toISOString(),
        } as ModelRouteDto,
        scenario.value,
      ),
    getModelSettings: () => delay(data().modelSettings as ModelSettingsDto, scenario.value),
    saveModelSettings: (settings: { base_url: string; model: string; api_key?: string; clear_api_key?: boolean }) =>
      delay(
        {
          base_url: settings.base_url,
          model: settings.model,
          has_api_key: !settings.clear_api_key,
          updated_at: new Date().toISOString(),
        } as ModelSettingsDto,
        scenario.value,
      ),

    listExtensionSources: () => delay(data().extensionSources as ExtensionSourceDto[], scenario.value),
    createExtensionSource: (source: { name: string; kind: string; location: string; enabled?: boolean }) =>
      delay(
        {
          id: `src-new-${Date.now()}`,
          name: source.name,
          kind: source.kind,
          location: source.location,
          enabled: source.enabled ?? true,
          last_refreshed_at: null,
          created_at: new Date().toISOString(),
        } as ExtensionSourceDto,
        scenario.value,
      ),
    refreshExtensionSource: (sourceId: string) =>
      delay(
        { ...data().extensionSources[0], id: sourceId, last_refreshed_at: new Date().toISOString() } as ExtensionSourceDto,
        scenario.value,
      ),
    listMarketCatalog: (_params: { kind?: string; query?: string; source_id?: string } = {}) =>
      delay(data().marketCatalog as MarketCatalogItemDto[], scenario.value),
    getMarketCatalogItem: (catalogId: string) =>
      delay(data().marketCatalog.find((c) => c.catalog_id === catalogId) ?? data().marketCatalog[0] as MarketCatalogItemDto, scenario.value),
    previewExtensionInstall: (_input: { catalog_id?: string | null; source_kind?: string | null; source_location?: string | null; manifest_json?: string | null }) =>
      delay(
        {
          extension_id: 'preview-ext',
          kind: 'tool-pack',
          version: '1.0.0',
          publisher: 'Preview',
          display_name: '预览扩展',
          description: '安装预览',
          source_kind: 'marketplace-url',
          source_location: 'https://example.com',
          capabilities: ['preview.cap'],
          permissions: ['fs:read'],
          risks: ['预览风险'],
          requires_approval: true,
          approval_summary: '需要审批',
        } as ExtensionInstallPreviewDto,
        scenario.value,
      ),
    installExtension: (_input: { catalog_id?: string | null; source_kind?: string | null; source_location?: string | null; manifest_json?: string | null; approval_id?: string | null }) =>
      delay(
        {
          approval_required: false,
          approval: null,
          extension: data().installedExtensions[0],
          preview: {
            extension_id: 'preview-ext',
            kind: 'tool-pack',
            version: '1.0.0',
            publisher: 'Preview',
            display_name: '预览扩展',
            description: '安装预览',
            source_kind: 'marketplace-url',
            source_location: 'https://example.com',
            capabilities: ['preview.cap'],
            permissions: ['fs:read'],
            risks: [],
            requires_approval: false,
            approval_summary: '',
          },
        } as ExtensionInstallResultDto,
        scenario.value,
      ),
    listInstalledExtensions: () => delay(data().installedExtensions as InstalledExtensionDto[], scenario.value),
    enableExtension: (extensionId: string) =>
      delay({ ...data().installedExtensions[0], id: extensionId, enabled: true } as InstalledExtensionDto, scenario.value),
    disableExtension: (extensionId: string) =>
      delay({ ...data().installedExtensions[0], id: extensionId, enabled: false } as InstalledExtensionDto, scenario.value),
    updateExtension: (extensionId: string) =>
      delay({ ...data().installedExtensions[0], id: extensionId } as InstalledExtensionDto, scenario.value),
    deleteExtension: (_extensionId: string) => emptyDelay(undefined as unknown as void, scenario.value),

    listMcpServers: () => delay(data().mcpServers as McpServerDto[], scenario.value),
    reloadMcpServer: (serverId: string) =>
      delay({ ...data().mcpServers[0], id: serverId, status: 'connected' } as McpServerDto, scenario.value),
    listAcpAdapters: () => delay(data().acpAdapters as AcpAdapterDto[], scenario.value),
    probeAcpAdapter: (adapterId: string) =>
      delay({ ...data().acpAdapters[0], id: adapterId, status: 'active', status_message: '探测成功' } as AcpAdapterDto, scenario.value),

    listAgentModes: () => delay(data().agentModes as AgentModeDto[], scenario.value),
    listAgents: () => delay(data().agents as AgentProfileDto[], scenario.value),
    listTools: () => delay(data().tools as ToolDescriptorDto[], scenario.value),
    searchTools: (params: { query?: string; domain?: string; source?: string; risk?: string; limit?: number } = {}) =>
      delay(
        data()
          .tools.filter((t) => {
            if (params.domain && t.domain !== params.domain) return false
            if (params.source && t.source !== params.source) return false
            if (params.risk && t.risk !== params.risk) return false
            if (params.query && !t.display_name.toLowerCase().includes(params.query.toLowerCase())) return false
            return true
          })
          .map((t) => ({
            tool: t,
            score: 1.0,
            matched_fields: ['display_name'],
            provider_layer: 'code',
            requires_human_checkpoint: t.risk === 'high',
            approval_summary: t.requires_approval ? '需要审批' : '',
          })),
        scenario.value,
      ) as Promise<ToolSearchResultDto[]>,
    getHarnessManifest: () => delay(data().harnessManifest as HarnessManifestDto, scenario.value),

    listPromptFragments: (_params: { scope?: string; target_agent_id?: string; category?: string; enabled?: boolean } = {}) =>
      delay(data().promptFragments as PromptFragmentDto[], scenario.value),
    createPromptFragment: (fragment: SavePromptFragmentInput) =>
      delay(
        {
          id: `pf-new-${Date.now()}`,
          ...fragment,
          is_builtin: false,
          created_at: new Date().toISOString(),
          updated_at: new Date().toISOString(),
        } as PromptFragmentDto,
        scenario.value,
      ),
    savePromptFragment: (fragmentId: string, fragment: SavePromptFragmentInput) =>
      delay(
        {
          id: fragmentId,
          ...fragment,
          is_builtin: false,
          created_at: new Date().toISOString(),
          updated_at: new Date().toISOString(),
        } as PromptFragmentDto,
        scenario.value,
      ),
    deletePromptFragment: (_fragmentId: string) => emptyDelay(undefined as unknown as void, scenario.value),
    clonePromptFragment: (fragmentId: string) =>
      delay({ ...data().promptFragments[0], id: `pf-clone-${Date.now()}` } as PromptFragmentDto, scenario.value),
    previewPromptContext: (input: PromptContextPreviewInput) =>
      delay(
        {
          agent_id: input.agent_id,
          mode: input.mode ?? 'auto',
          fragments: data().promptFragments,
          context_pack_ids: ['ctx-001', 'ctx-002'],
          estimated_tokens: 4096,
          system_prompt: '你是 TinadecOffice 的智能体...',
          warnings: [],
        } as PromptContextPreviewDto,
        scenario.value,
      ),

    executeCodeTool: (toolId: string, payload: CodeToolExecuteRequestDto = {}) => {
      const cwd = payload.cwd ?? DEFAULT_CWD
      if (toolId === 'git_worktree_manager') {
        const action = (payload.arguments as Record<string, unknown> | null)?.action
        if (action === 'diff_preview') {
          return delay(mockGitDiffPreview(), scenario.value) as Promise<CodeToolExecuteResultDto>
        }
        if (action === 'push_plan') {
          return delay(mockGitPushPlan(), scenario.value) as Promise<CodeToolExecuteResultDto>
        }
        if (action === 'log') {
          return delay(
            {
              tool_id: toolId,
              status: 'ok',
              summary: '返回 5 条提交记录',
              evidence: [],
              data: {
                commits: [
                  'a1b2c3d refactor(orchestrator): support dynamic dependency resolution',
                  'e4f5g6h feat(graph): add cycle detection',
                  '7i8j9k0 docs: update orchestrator architecture',
                  '1m2n3o4 chore: bump version to 0.4.2',
                  '4p5q6r7 fix: resolve memory leak in diff editor',
                ],
              },
              requires_approval: false,
              approval_summary: null,
            } as CodeToolExecuteResultDto,
            scenario.value,
          )
        }
        return delay(mockGitDiffPreview(), scenario.value) as Promise<CodeToolExecuteResultDto>
      }
      if (toolId === 'list_directory') {
        const args = payload.arguments as Record<string, unknown> | null
        const path = (args?.path as string) ?? '.'
        const tree = path === '.' || path === './' ? mockFileTree() : path === 'src' || path === './src' ? mockFileTreeSrc() : mockFileTree()
        return delay(
          {
            tool_id: toolId,
            status: 'ok',
            summary: `列出 ${path} 目录`,
            evidence: [],
            data: tree,
            requires_approval: false,
            approval_summary: null,
          } as CodeToolExecuteResultDto,
          scenario.value,
        )
      }
      if (toolId === 'read_file') {
        const args = payload.arguments as Record<string, unknown> | null
        const filePath = (args?.path as string) ?? 'src/orchestrator.ts'
        return delay(
          {
            tool_id: toolId,
            status: 'ok',
            summary: `读取 ${filePath}`,
            evidence: [filePath],
            data: {
              path: filePath,
              content: mockCodeContent(filePath),
              size_bytes: mockCodeContent(filePath).length,
              modified_at: new Date().toISOString(),
            },
            requires_approval: false,
            approval_summary: null,
          } as CodeToolExecuteResultDto,
          scenario.value,
        )
      }
      if (toolId === 'code_editor') {
        const args = payload.arguments as Record<string, unknown> | null
        const action = args?.action as string
        const filePath = (args?.path as string) ?? 'src/orchestrator.ts'
        if (action === 'open' || action === 'diff') {
          return delay(
            {
              tool_id: toolId,
              status: 'ok',
              summary: `${action} ${filePath}`,
              evidence: [filePath],
              data: {
                path: filePath,
                content: mockCodeContent(filePath),
                original: action === 'diff' ? mockCodeContent(filePath) : null,
                modified: action === 'diff' ? mockCodeContent(filePath) + '\n// modified' : null,
              },
              requires_approval: false,
              approval_summary: null,
            } as CodeToolExecuteResultDto,
            scenario.value,
          )
        }
        return delay(
          {
            tool_id: toolId,
            status: 'ok',
            summary: `${action} ${filePath}`,
            evidence: [filePath],
            data: { path: filePath },
            requires_approval: false,
            approval_summary: null,
          } as CodeToolExecuteResultDto,
          scenario.value,
        )
      }
      if (toolId === 'glob_search' || toolId === 'grep_content') {
        return delay(
          {
            tool_id: toolId,
            status: 'ok',
            summary: `搜索完成，命中 3 个结果`,
            evidence: ['src/orchestrator.ts', 'src/graph.ts', 'src/types.ts'],
            data: {
              matches: [
                { path: 'src/orchestrator.ts', line: 42, content: 'buildTaskGraph' },
                { path: 'src/graph.ts', line: 1, content: 'export class TaskGraph' },
                { path: 'src/types.ts', line: 12, content: 'TaskGraphDto' },
              ],
            },
            requires_approval: false,
            approval_summary: null,
          } as CodeToolExecuteResultDto,
          scenario.value,
        )
      }
      // 默认返回
      return delay(
        {
          tool_id: toolId,
          status: 'ok',
          summary: `执行 ${toolId} 完成`,
          evidence: [],
          data: {},
          requires_approval: false,
          approval_summary: null,
        } as CodeToolExecuteResultDto,
        scenario.value,
      )
    },

    // 语义包装器
    readFile: (cwd: string, filePath: string, options?: { start_line?: number; end_line?: number }) =>
      api.executeCodeTool('read_file', { cwd, arguments: { path: filePath, ...options } }),
    listDirectory: (cwd: string, dirPath: string) =>
      api.executeCodeTool('list_directory', { cwd, arguments: { path: dirPath } }),
    globSearch: (cwd: string, pattern: string) =>
      api.executeCodeTool('glob_search', { cwd, arguments: { pattern } }),
    grepContent: (cwd: string, pattern: string, options?: { case_sensitive?: boolean; context_lines?: number; max_results?: number }) =>
      api.executeCodeTool('grep_content', { cwd, arguments: { pattern, ...options } }),
    applyPatch: (cwd: string, patch: string, approvalId?: string) =>
      api.executeCodeTool('apply_patch', { cwd, approval_id: approvalId, arguments: { patch } }),
    codeEditorOpen: (cwd: string, filePath: string) =>
      api.executeCodeTool('code_editor', { cwd, arguments: { action: 'open', path: filePath } }),
    codeEditorSave: (cwd: string, filePath: string, content: string, approvalId: string) =>
      api.executeCodeTool('code_editor', { cwd, approval_id: approvalId, arguments: { action: 'save', path: filePath, content } }),
    codeEditorDiff: (cwd: string, filePath: string) =>
      api.executeCodeTool('code_editor', { cwd, arguments: { action: 'diff', path: filePath } }),
    codeEditorPatch: (cwd: string, filePath: string, patch: string, approvalId: string) =>
      api.executeCodeTool('code_editor', { cwd, approval_id: approvalId, arguments: { action: 'patch', path: filePath, patch } }),
    gitDiffCompare: (cwd: string, baseRef: string, headRef: string, paths?: string[]) =>
      api.executeCodeTool('git_worktree_manager', { cwd, arguments: { action: 'diff_compare', base_ref: baseRef, head_ref: headRef, paths } }),
    gitLog: (cwd: string, limit?: number, ref?: string) =>
      api.executeCodeTool('git_worktree_manager', { cwd, arguments: { action: 'log', limit, ref } }),

    saveAgent: (agentId: string, agent: {
      name: string; layer: string; agent_type: string; mode: string; description: string;
      model_route_purpose: string; allowed_tools?: string[]; capabilities?: string[];
      system_prompt?: string | null; enabled: boolean;
    }) =>
      delay(
        { ...data().agents[0], id: agentId, ...agent, updated_at: new Date().toISOString() } as AgentProfileDto,
        scenario.value,
      ),
    updateAgentMode: (agentId: string, mode: string) =>
      delay({ ...data().agents[0], id: agentId, mode, updated_at: new Date().toISOString() } as AgentProfileDto, scenario.value),
    listAgentCandidates: () =>
      delay(
        [
          { id: 'cand-001', generated_by_agent_id: 'agent-evolver', name: 'Test Runner', layer: 'execution', agent_type: 'test_runner', description: '自动生成的测试运行智能体', suggested_tools: ['shell', 'read_file'], evaluation_notes: ['覆盖率高', '响应快'], status: 'proposed', created_at: new Date().toISOString() },
        ] as AgentCandidateDto[],
        scenario.value,
      ),

    connectEvents(_sessionId: string | null, onEvent: (event: EventEnvelope) => void): EventSource {
      // 创建一个假的 EventSource，用 setTimeout 模拟事件推送
      const fakeSource = {
        close: () => { /* noop */ },
        onmessage: null as ((ev: MessageEvent) => void) | null,
        addEventListener: () => { /* noop */ },
        removeEventListener: () => { /* noop */ },
        readyState: 1,
        url: '',
        withCredentials: false,
        dispatchEvent: () => false,
        onopen: null,
        onerror: null,
        CONSTANTS: { CONNECTING: 0, OPEN: 1, CLOSED: 2 },
      } as unknown as EventSource

      if (isLoadingScenario(scenario.value)) {
        return fakeSource
      }

      const events = data().events
      let index = 0
      const timer = setInterval(() => {
        if (index >= events.length) {
          clearInterval(timer)
          return
        }
        onEvent(events[index])
        index++
      }, 1500)

      // 覆盖 close 方法以清除定时器
      const originalClose = fakeSource.close.bind(fakeSource)
      fakeSource.close = () => {
        clearInterval(timer)
        originalClose()
      }

      return fakeSource
    },
  }

  return api
}

export type MockApi = ReturnType<typeof createMockApi>
