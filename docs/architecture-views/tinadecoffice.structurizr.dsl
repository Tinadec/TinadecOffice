workspace "TinadecOffice 架构（当前态）" "证据来源：仓库源码、solution、配置与 docs。生成日期 2026-08-27，基线 commit f9c44c0。配套证据索引见 tinadecoffice.evidence.md。" {

    !identifiers hierarchical

    model {
        # ---------- 人员 / 角色 ----------
        operator = person "开发者 / 使用者" "在 Desktop 或 Web 中发起对话、审阅监督结论、批准工具写操作、配置模型与智能体。"
        packAuthor = person "Agent Pack 作者" "编写 tinadec.io/agent-pack/v1alpha1 manifest，随 App 分发并由用户确认安装。"
        dotnetHost = person ".NET 宿主集成方" "以 Contracts/Abstractions/Runtime NuGet 包嵌入 Core（目标交付形态，尚未发布到 NuGet 源）。" {
            tags "Target"
        }

        # ---------- TinadecApp（产品：客户端）----------
        app = softwareSystem "TinadecApp" "面向场景的交互客户端与随 App 分发的 Agent Pack 制品。" {
            mainProc = container "Electron 主进程" "窗口/多进程生命周期、本地能力（PTY、宠物窗口、布局与配置落盘）、注册全部 IPC。" "Electron CJS (apps/desktop/electron)"
            preload = container "Preload 桥" "contextBridge 暴露唯一的 window.tinadec 合约：gatewayUrl、terminal、pets、panel、layout。" "Electron preload"
            renderer = container "渲染层 SPA" "Home/Settings/Market/Code/Workbench/Governance/Snapshots/Library/Debug 等 14 页面、Pinia stores、SSE 消费；只访问 Gateway。" "Vue 3 + Vite（dev 127.0.0.1:5173）"
            webShim = container "Web 平台垫片" "浏览器侧重新实现 window.tinadec 合约；不含任何 UI 代码，Vite alias 直接编译 desktop 源码。" "apps/web（dev 127.0.0.1:5174，同源代理 /api /docs /ws）"
            uie = container "TinadecUIE 布局引擎" "确定性三槽布局状态机（reducer/commandBus/undo/persistence）+ Uie* 组件；经 Vite alias 消费，非 npm 依赖。" "apps/TinadecUI（纯 TS + Vue）"
            localState = container "App 本地状态" "仅体验偏好：settings.json、workbench-layout.json、.tinadec-panel-layout.json、pets/、dynamic-palette 缓存。" "Electron userData / localStorage" {
                tags "Database"
            }
            officePack = container "OfficeAgentPack 制品" "静态携带 tinadec.office.agent-pack@0.2.1（14 Agent + baseline-prompt + 7 Mode，治理角色 tool_scope 为空）与硬编码 RFC8785 摘要。" "apps/desktop/src/agentPacks/OfficeAgentPack"
        }

        # ---------- TinadecGateway（产品：API 门面）----------
        gateway = softwareSystem "TinadecGateway" "北向无状态 API 门面：协议适配、认证、流转发、薄映射。不拥有业务真相。" {
            routes = container "Elysia 路由面" "单文件 2052 行链式应用，约 200 条 /api/v1 路由 + 3 条 .ws()；CORS、认证中间件、/docs Swagger。" "Bun + Elysia（:48730）"
            mapping = container "映射与认证层" "16 个 mapper（含 4 个未被引用）做白名单投影与 snake_case 归一；auth.ts 做 API Key / JWT HS256 验签。" "TinadecGateway/src/mappers, auth.ts"
            upstream = container "上游代理客户端" "proxyJson 缓冲 JSON、proxySse 原样透传响应体；proxySseWithCursor/proxyStream 已实现但未被 index.ts 引用。" "coreClient.ts, toolRuntimeClient.ts"
        }

        # ---------- TinadecCore（产品：治理与编排运行时）----------
        core = softwareSystem "TinadecCore" "唯一状态权威：租户/会话/run/task、模型路由与归因、权限与审批、上下文与记忆、工具治理、审计与演化。" {

            apiHost = container "Api 主机" "ASP.NET Core minimal API 进程：snake_case JSON、RFC9457 ProblemDetails、内部 OpenAPI、启动迁移与 DevSeed。" "net10.0 + Kestrel（:48731）" {
                program = component "Program.cs" "全局 JSON/ProblemDetails 约定、异常→code 映射、迁移与种子、13 个 Map*Endpoints() 调用。" "TinadecCore/Api/Program.cs"
                storageEp = component "StorageEndpoints" "project/session/message/run CRUD 与 /events 回放+SSE 跟随（14 路由）。" "Endpoints/StorageEndpoints.cs"
                dmaeaEp = component "DmaeaEndpoints" "invoke-stream、orchestration、tool-executions、task-nodes、context-versions、run stream。" "Endpoints/DmaeaEndpoints.cs"
                interactionsEp = component "InteractionsEndpoints" "POST /interactions：queued|insert|parallel 三种投递语义与 agent_mode 解析。" "Endpoints/InteractionsEndpoints.cs"
                agentCfgEp = component "AgentConfigurationEndpoints" "Agent/Mode/Prompt/WorkspaceDefaults 的 draft/publish/archive 与 ETag/If-Match（29 路由）。" "Endpoints/AgentConfigurationEndpoints.cs"
                packEp = component "AgentPackEndpoints" "install-preview / PUT 应用 / GET 列表与详情：owner+version+digest 校验与托管只读。" "Endpoints/AgentPackEndpoints.cs"
                modelEp = component "ModelAgentControlEndpoints" "model-resolution/preview、model-references、model-invocations 三个新控制面投影。" "Endpoints/ModelAgentControlEndpoints.cs"
                controlEp = component "ControlPlaneEndpoints" "provider/route/settings、CLI discover/connect、模型刷新、审批投影（28 路由，7 处 501）。" "Endpoints/ControlPlaneEndpoints.cs"
                govEp = component "GovernanceEndpoints" "permission-requests/decision、grants、delegations、leases 撤销。" "Endpoints/GovernanceEndpoints.cs"
                toolActionEp = component "UserToolActionEndpoints" "用户工具动作 create/detail/resume/snapshot-override/recovery-decision。" "Endpoints/UserToolActionEndpoints.cs"
                snapshotEp = component "WorkspaceSnapshotEndpoints" "工作区快照捕获与恢复计划执行。" "Endpoints/WorkspaceSnapshotEndpoints.cs"
                reviewEp = component "MemoryReviewEndpoints / EvolutionEndpoints" "记忆候选与智能体候选的 review/promote/reject。" "Endpoints/MemoryReviewEndpoints.cs, EvolutionEndpoints.cs"
                stubEp = component "StubEndpoints" "market/extensions、debug/*、mcp/acp、scheduling、tools/shell 等 44 路由：GET 返回空集合、写入返回结构化 501。" "Endpoints/StubEndpoints.cs"
            }

            runtime = container "Runtime 组合根与应用服务" "AddTinadecCore()/AddTinadecCoreMinimal() 显式注册（无反射扫描）；跨模块应用服务：ControlPlaneService、UserToolActionService、AgentModelResolver、FormalModeResolver、CoreAuthorizationContextResolver、DevSeed、BootstrapAgentDirectory。" "TinadecCore/Runtime"

            foundation = container "基础能力" "Contracts（纯 DTO/事件）、Abstractions（23 个端口 + ITinadecCoreBuilder/ModuleDescriptor）、Strategies（F# 纯策略：预算/选择/评分/循环/状态迁移）。" "Contracts, Abstractions, Strategies(fsproj)"

            persistence = container "Persistence 共享存储抽象" "EF Core LINQ 面、UseTinadecDatabase 提供者无关配置、IContentStore/ISecretStore/NonceMaterialStore、Core 数据路径、迁移协调与就绪探测。" "TinadecCore/Persistence"
            migrations = container "Storage.Migrations.Sqlite / .PostgreSql" "按 DbContext 提供程序生成迁移；SQLite 本地启动即迁移，PostgreSQL 需显式 ApplyMigrationsOnStartup。" "TinadecCore/Storage.Migrations.*"

            dmaea = container "DmaEA 双层协作运行时" "operation/execution 双层：全双工 run 引擎、冻结配置、MAF 1.18 适配器、CLI 聊天后端。唯一允许引用 MAF 包的模块。" "TinadecCore/DmaEA" {
                engine = component "FullDuplexRunEngine" "幂等准入、context_revision 补丁、治理协调→任务规划→执行→监督→meeting 定稿、spawn/lineage 预算、run 控制、租约式重启恢复（1889 行，最大热点）。" "DmaEA/FullDuplexRunEngine.cs"
                coordinator = component "FullDuplexRunCoordinator" "面向会话的交互受理与有序 SSE 发射（ack→steering/context_conflict→done）。" "DmaEA/FullDuplexRunCoordinator.cs"
                planAgent = component "PlanningAgent" "任务规划（旧 planning 层语义，公开契约已收敛为 operation）。" "DmaEA/PlanningAgent.cs"
                execAgent = component "ExecutionAgent" "执行层 worker 的模型回合与工具轮次。" "DmaEA/ExecutionAgent.cs"
                supAgent = component "SupervisionAgent" "监督发现与候选建议。" "DmaEA/SupervisionAgent.cs"
                instanceSvc = component "AgentInstanceService" "worker 实例化、lineage 与预算裁剪、候选晋升。" "DmaEA/AgentInstanceService.cs"
                profileStore = component "AgentRuntimeConfigurationStore / FrozenRunConfiguration" "解析并校验 default-agent-runtime.toml，valid-only 热重载；按 run 冻结 profile。" "DmaEA/AgentRuntimeConfiguration.cs, FrozenRunConfiguration.cs"
                mafAdapter = component "Maf18RuntimeAdapter" "MAF 类型边界：权限、审批、检查点、工具回执、恢复仍由 Core 持有。" "DmaEA/Maf18RuntimeAdapter.cs"
                chatFactory = component "AgentChatClientFactory / ModelInvocationChatFactory" "按协议装配 IChatClient：openai-chat / openai-responses / anthropic-messages / fixed 策略。" "DmaEA/IAgentChatClientFactory.cs, ModelInvocationChatFactory.cs"
                cliRuntime = component "CliRuntime（ACP / opencode serve）" "AcpChatClient（JSON-RPC 2.0 over SSE）、OpenCodeChatClient、CliProcessManager 拉起并复用本机 CLI 服务。" "DmaEA/CliRuntime"
                controlDb = component "AgentControlDbContext" "agent_instances、agent_candidates、runtime_profile_overrides、model_invocations 投影。" "DmaEA/AgentControlDbContext.cs"
            }

            modelCtl = container "Models 模型控制面" "ModelProvider（IModelProvider + IChatResolver）读不可变 provider/route 版本并组装 ChatResolution；EmbeddingProvider 实现 IEmbeddingProvider。" "TinadecCore/Models"
            toolsCtl = container "Tools 工具治理与 Provider 适配器" "TinadecToolsProcessManager 按 workspace 根托管子进程；ToolManifestSnapshotResolver 冻结 manifest 哈希；ToolDispatcher prepare/resume + 一次性审批消费。" "TinadecCore/Tools"
            governance = container "Governance 策略与授权" "GovernanceService（1651 行）持有 permission request、capability grant/delegation/lease、PDP 判定与 FailClosed 上下文解析。" "TinadecCore/Governance"
            lifecycle = container "Lifecycle 状态与审计" "LifecycleManager/StorageLifecycleService 持有 run/task/step/event 索引与 2s 水位跟随；ToolApprovalCoordinator、WorkspaceSnapshotService、GitWorkspaceSnapshotProvider、RunRecoveryHostedService。" "TinadecCore/Lifecycle"
            agentCfg = container "AgentConfiguration 版本化配置与 Pack 生命周期" "Agent/Mode/Prompt/WorkspaceDefaults 的 draft + 不可变版本 + ETag；AgentPackService（1668 行）做 SemVer/内部引用/双层拓扑/RFC8785 摘要校验与原子安装。" "TinadecCore/AgentConfiguration"
            memory = container "Memory 会话与记忆" "ProjectSessionStore（777 行，projects/sessions 与消息文件）、MemoryStore、LongTermMemoryService、保留策略。" "TinadecCore/Memory"
            tenancy = container "Tenancy 租户与工作区" "外部主体→tenant→workspace→membership；ITenantContextAccessor 是唯一请求隔离端口，开发身份由配置注入。" "TinadecCore/Tenancy"
            thinMods = container "薄模块：Context / Prompts / Skills / LoopGuard / VectorStore" "各模块以 *ModuleRegistrar 注册 internal 实现：ContextProvider、PromptAssembler、SkillProvider、LoopGuardEvaluator、CoreVectorStore。" "TinadecCore/{Context,Prompts,Skills,LoopGuard,VectorStore}"

            sqlite = container "SQLite 本地库" "默认关系存储 data/tinadec.db（gitignored），9 个 tenant/workspace 作用域 DbContext 的投影与不可变版本索引。" "SQLite + EF Core" {
                tags "Database"
            }
            postgres = container "PostgreSQL（可选）" "TinadecPersistence:Provider=PostgreSql + ConnectionStrings:TinadecCore；CI 使用 pgvector/pgvector:pg16。" "Npgsql + pgvector" {
                tags "Database"
            }
            fileStore = container "Core 文件仓库 data/" "sessions/ 原子替换的历史、tasks/ 快照、events/ 不可变 JSONL、artifacts/、content/ 不可变正文、secrets/ 引用。" "本地文件系统" {
                tags "Database"
            }
            vectorStore = container "项目向量库" "SQLite：data/vectors/tenants/{tenant}/{workspace}/{project}.db 独立 sqlite-vec 库；PostgreSQL：共享 vector_chunks/vector_collections + cosine ops。" "sqlite-vec / pgvector" {
                tags "Database"
            }

            openapi = container "契约快照" "内部 /openapi/core.json 是 Core 事实源；Desktop 生成客户端与漂移检查以 Gateway /docs/json 为输入。" "openapi.core.json, apps/desktop/src/generated/client.ts"
        }

        # ---------- TinadecTool（产品：可执行能力）----------
        tool = softwareSystem "TinadecTool" "审批感知的可执行能力提供者：文件、命令、Git、检索、MCP 直通。当前仓库宿主是 TinadecTools 原型。" {
            toolHost = container "TinadecTools 子进程" "46 个 [ToolFunction] 工具（43 个字面量 id + Git 工具 3 个 TOOL_ID 常量）+ 需状态工具；行分隔 JSON over stdio（BOM-free UTF-8）；manifest v2；写工具与 confirm_* 双闸。" ".NET 10 控制台（TinadecTools/）"
            generator = container "ToolFunction 源生成器" "编译期生成静态注册表，拒绝未批准的写工具调用。" "Roslyn source generator"
            sandbox = container "TinadecSandbox 执行隔离" "Windows 本地账户 + 临时 ACL 授予/撤销 + Job Object 超时终止；command_run 始终需要审批。" "TinadecSandbox"
            mcpConfig = container "MCP 直通配置" "mcp_servers.json（或 TINADEC_TOOLS_MCP_CONFIG）声明 stdio MCP server；mcp_invoke 需要审批。" "TinadecTools/Tools/Mcp"
        }

        # ---------- 外部系统 ----------
        llmApi = softwareSystem "远端模型 API" "OpenAI 兼容 chat/responses 与 Anthropic Messages；密钥只经 SecretStore 引用，无密钥则 run 干净失败，绝不伪造成功。" {
            tags "External"
        }
        cliRuntimes = softwareSystem "本机 CLI 运行时" "claude / codex / cursor（ACP）与 opencode serve；discover 用 --version 探测，connect 拉起或复用服务后落为 provider。" {
            tags "External"
        }
        workspaceFs = softwareSystem "工作区文件系统与 Git 仓库" "工具层唯一可操作的工作对象；快照 provider 读取 HEAD/index/worktree/untracked。" {
            tags "External"
        }
        gitRemote = softwareSystem "Git remote" "fetch/push/pull 目标；remote 必须是已配置名称，push 拒绝 dirty/detached/behind。" {
            tags "External"
        }
        mcpServers = softwareSystem "MCP servers" "stdio 协议的外部工具服务器，由工具层直通透出。" {
            tags "External"
        }
        toolRuntimeSvc = softwareSystem "Tool Runtime 服务 (:48732)" "Gateway 已配置并测试转发（toolRuntimeClient.ts、config.ts:72），但仓库内不存在任何 HTTP/WS 实现；docs/web-client.md 阶段 2 明确其为缺口。" {
            tags "External", "Target"
        }

        # ================= 关系：App → Gateway → Core =================
        operator -> app.renderer "使用对话、审批、模型中心与 Agent Center"
        packAuthor -> app.officePack "编写并随 App 构建分发 manifest"
        dotnetHost -> core.runtime "以 NuGet 包嵌入（目标形态）"

        app.renderer -> app.preload "调用 window.tinadec 合约" "IPC"
        app.preload -> app.mainProc "ipcRenderer.invoke/send" "IPC"
        app.mainProc -> app.localState "读写 settings/layout/pets" "file"
        app.renderer -> app.uie "布局状态与命令" "in-process"
        app.webShim -> app.renderer "替换平台实现后原样启动 desktop 源码" "Vite alias"
        app.renderer -> gateway.routes "REST + SSE + Debug Studio WebSocket；基址解析 TINADEC_RESOLVED_GATEWAY_URL → TINADEC_GATEWAY_URL → 127.0.0.1:48730" "HTTP/SSE/WS"
        app.renderer -> gateway.routes "受治理写操作与用户工具动作" "HTTP POST"
        gateway.routes -> core.apiHost "转发 /api/v1/*；注入 x-request-id 与固定 x-tinadec-principal: dev@local（本地模式）" "HTTP/SSE"
        core.apiHost -> gateway.routes "SSE 事件流与投影响应" "HTTP/SSE"
        gateway.mapping -> gateway.routes "白名单投影、密钥剥离、错误码改写；404 回退时派生 run→session 归属" "in-process"
        gateway.upstream -> core.apiHost "proxyJson 缓冲 / proxySse 原样透传" "HTTP/SSE"
        gateway.upstream -> toolRuntimeSvc "用户直连工具执行（当前无服务实现，路径实际不可用）" "HTTP"

        # ================= 关系：Core 内部 =================
        core.apiHost -> core.runtime "AddTinadecPersistence + AddTinadecCore + Map*Endpoints" "in-process DI"
        core.runtime -> core.dmaea "注册双层运行时与 profile 冻结" "in-process"
        core.runtime -> core.modelCtl "注册 provider/route/readiness" "in-process"
        core.runtime -> core.toolsCtl "注册工具治理与 provider 适配器" "in-process"
        core.runtime -> core.governance "注册策略/租约/审批" "in-process"
        core.runtime -> core.lifecycle "注册 run/task/event 与快照" "in-process"
        core.runtime -> core.agentCfg "注册版本化配置与 Pack 生命周期" "in-process"
        core.runtime -> core.memory "注册会话与记忆" "in-process"
        core.runtime -> core.tenancy "注册租户/工作区/主体" "in-process"
        core.runtime -> core.thinMods "注册 Context/Prompts/Skills/LoopGuard/VectorStore" "in-process"
        core.runtime -> core.migrations "注册迁移参与者" "in-process"

        core.dmaea -> core.modelCtl "经 IChatResolver/IModelProvider 与 IAgentModelResolver 取得 ChatResolution" "port"
        core.dmaea -> core.toolsCtl "智能体工具调用经 IToolDispatcher" "port"
        core.dmaea -> core.governance "执行前 PDP 判定与租约" "port"
        core.dmaea -> core.lifecycle "run/task/step/event 持久化" "port"
        core.dmaea -> core.thinMods "ContextProvider / PromptAssembler / LoopGuardEvaluator" "port"
        core.dmaea -> core.agentCfg "读取冻结的 Agent/Mode/Prompt 精确版本" "port"
        core.toolsCtl -> core.governance "一次性 ActionApproval 与 lease 校验" "port"
        core.toolsCtl -> core.lifecycle "工具执行记录与检查点" "port"
        core.runtime -> core.agentCfg "FormalModeResolver 复算工具交集与模型策略" "port"
        core.dmaea -> core.foundation "仅通过 Abstractions 端口与 Contracts DTO 协作" "project reference"
        core.agentCfg -> core.persistence "EF Core DbContext" "project reference"
        core.lifecycle -> core.persistence "EF Core DbContext" "project reference"
        core.governance -> core.persistence "EF Core DbContext" "project reference"
        core.modelCtl -> core.persistence "EF Core DbContext" "project reference"
        core.memory -> core.persistence "EF Core DbContext" "project reference"
        core.tenancy -> core.persistence "EF Core DbContext" "project reference"
        core.thinMods -> core.persistence "EF Core DbContext / IProjectVectorDatabase" "project reference"
        core.dmaea -> core.persistence "AgentControlDbContext" "project reference"
        core.migrations -> core.persistence "为各 DbContext 生成提供者迁移"
        core.persistence -> core.sqlite "SELECT 1 就绪探测 + EF 读写" "SQL"
        core.persistence -> core.postgres "可选替代 SQLite" "SQL"
        core.persistence -> core.fileStore "内容与历史文件（原子替换）" "file"
        core.persistence -> core.vectorStore "sqlite-vec / pgvector 读写" "SQL"
        core.apiHost -> core.openapi "MapOpenApi(/openapi/core.json)"
        app.renderer -> core.openapi "生成类型化客户端与 check:drift"
        # 注 1：VectorStore 的 IProjectVectorDatabase 端口由 Persistence 提供，故 thinMods→Persistence 已覆盖。
        # 注 2：Gateway 的三条 .ws() 路由是死桩——它们只订阅 Bun pub/sub 并丢弃算出的 targetUrl，
        #       websocket.ts 的 createWsProxyHandlers 无任何调用点（TinadecGateway/src/index.ts:1924-1956）。

        # ================= 关系：Core → Tool / 外部 =================
        core.toolsCtl -> tool.toolHost "按 workspace 根启动/复用子进程，行分隔 JSON 协议" "stdio pipes"
        tool.generator -> tool.toolHost "编译期生成静态注册表" "Roslyn"
        tool.toolHost -> tool.sandbox "command_run 在本地沙箱账户下执行" "OS"
        tool.toolHost -> workspaceFs "文件读写/检索，符号链接与越界一律拒绝" "file"
        tool.toolHost -> gitRemote "fetch/push/pull（审批 + confirm_*）" "git"
        tool.toolHost -> mcpServers "mcp_list/search/invoke（invoke 需审批）" "stdio MCP"
        core.modelCtl -> llmApi "代理模型发现与推理调用，密钥来自 SecretStore 引用" "HTTPS"
        core.dmaea -> cliRuntimes "ACP/opencode-serve 作为 IChatClient 后端" "HTTP/JSON-RPC"
        core.lifecycle -> workspaceFs "文件系统/Git 快照捕获（内容仍由 provider 保存）" "file/git"
    }

    views {
        systemLandscape landscape "L0-L1 产品族全景" "四产品边界与外部系统；默认组合不是唯一组合。" {
            include *
            autoLayout tb
        }

        systemContext core "CoreContext" "以 TinadecCore 为中心的上下文：谁使用它、它直接触达哪些外部系统。" {
            include *
            autoLayout lr
        }

        container core "CoreContainers" "Core 内部的 21 个项目容器、关系存储与文件仓库。" {
            include *
            autoLayout tb
        }

        container app "AppContainers" "TinadecApp 桌面/网页进程与本地状态。" {
            include *
            autoLayout tb
        }

        container gateway "GatewayContainers" "Gateway 三层：路由面、映射/认证、上游代理客户端。" {
            include *
            autoLayout lr
        }

        container tool "ToolContainers" "工具产品内部：子进程宿主、源生成器、沙箱、MCP 直通。" {
            include *
            autoLayout lr
        }

        component core.apiHost "ApiComponents" "Api 容器内的 13 个端点组，以及其中仍是 501 桩的部分。" {
            include *
            autoLayout tb
        }

        component core.dmaea "DmaeaComponents" "全双工双层运行引擎的组件分解。" {
            include *
            autoLayout tb
        }

        styles {
            element "Person" {
                shape person
                background #08427b
                color #ffffff
            }
            element "Software System" {
                background #1168bd
                color #ffffff
            }
            element "External" {
                background #999999
                color #ffffff
            }
            element "Database" {
                shape cylinder
                background #2f6f4f
                color #ffffff
            }
            element "Target" {
                background #d9b310
                color #403000
                style dashed
            }
            element "Container" {
                background #26a0d6
                color #ffffff
            }
            element "Component" {
                background #85bbf0
                color #000000
            }
        }
    }
}
