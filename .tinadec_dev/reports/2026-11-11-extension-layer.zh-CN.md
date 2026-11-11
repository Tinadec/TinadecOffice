# 扩展层第一轮：内置 Skill、分层加载器、Agent 配置工具与 Skill/MCP 市场页

日期：2026-11-11 · 基线：`fa671c5c` + 本轮工作树（未提交）· 范围：TinadecOffice（Core / Gateway / Desktop / TinadecUI）

## 一句话结论

Skill 与 MCP 的「管理面、审批、冻结」原本就是通的；本轮补的是**发现面、内置资产、渐进披露、Agent 可调用面、市场入口与热刷新**。现在内置技能、分层目录、`load_skill`、`tinadec_*` 配置工具、MCP stdio/HTTP/SSE、内置技能复制、Skill/MCP 两个独立市场页和图编辑的边键/删除路径都已落地；`tinadec_inspect` 已走完 dispatcher → resume → gateway → result 的真实 E2E。

## 1. Skill 加载器（M1）

- **内置技能**：`TinadecCore/Skills/BuiltinSkills/` 4 个包（`tinadec-workbench` / `tinadec-skill-authoring` / `tinadec-mcp-setup` / `tinadec-market`）以 `EmbeddedResource` 打进 `TinadecCore.Skills.dll`；`BuiltinSkillCatalog` 按 frontmatter 校验并计算包哈希/ResourceId。不写用户磁盘；设置页提供“复制到项目”，走受治理的 `skill_project_package`。
- **分层目录**：`ToolSkillResourceService.ListAsync(projectId, includeBuiltins, ct)` 合并项目 `.tinadec/skills`（rank 100）、scope 共享（200）、用户 `~/.tinadec/skills`（300）、项目 `.agents/skills`（400）、用户 `.agents/skills`（500）、内置（900）。低 rank 胜出；败者保留 `shadowed_by`。默认重载不含内置，设置/管理面仍只数自己的资源；`GetAsync` / `ResolveAsync` / `CaptureSelectedAsync` / `ToolConfigurationResolver` / `load_skill` 显式包含。
- **显式绑定语义**：自动选择排除被遮蔽项；显式 `resource_ids` 仍可选中同名被遮蔽的共享技能（否则既有测试与“用户明确绑定”都会被静默清空）。
- **渐进披露**：新增 Core 虚拟工具 `load_skill`（offset/limit、正文/`resource_base`/包文件清单）；`ContextModuleRegistrar` 的正文读取判定与索引文案都认它。`IsCoreVirtual` 现在包含 `IsLoadSkill`，否则冻结 manifest 时工具会被丢掉。
- **只读保护**：内置/兼容根 `read_only=true`；保存/删除返回 `read_only_skill`。设置页对内置技能显示 `builtinBadge` 并禁用编辑/删除。

## 2. Agent 可配置工作台（M2）

- **唯一写路径**：`AgentConfiguration/AgentTopologyService.cs`（`IAgentTopologyService`）同时服务 HTTP 与 `tinadec_*` 工具。draft 删除按“提交数组缺席”执行，边必须有 `edge_key`（`edge_key_required`），`GraphValidation` 在 `SaveChanges` 前跑，发布门改用 marker/capability 契约，Solo 形状 draft 可发布；发布后 draft 节点/边/布局翻成 published，GET 不再读回空图。
- **`tinadec_*` 工具**：`tinadec_inspect`（low/safe）+ `tinadec_agent_apply` / `tinadec_mode_apply` / `tinadec_mode_publish` / `tinadec_skill_apply` / `tinadec_mcp_apply`（high/unsafe + 审批，`MutatesWorkspace=false`）。`ITinadecConfigToolGateway` 在 Abstractions，Runtime 实现；Tools 不反向引用 AgentConfiguration/Skills。
- **GraphSeedPack 3.5.0**：`meeting` / `solo_master` 得到 `load_skill` + `mcp_list/search/invoke` + 六个 `tinadec_*`；其它持有 `read_file` 的 agent 得到 `load_skill`；digest 已重算。`agent_versions.snapshot_json` 的 EF 注解从 16384 提到 32768（provider 列本来就是 `text`，无新迁移）。
- **图编辑器**：`AgentModesPanel` 传 `edge_key` 与真实 `canvas_layout`；节点 sheet 与画布 Delete/Backspace 删除节点/边，删节点级联删边；自环/回操作层/成环守卫不变。

## 3. MCP 加载器补完（M3）

- `POST /api/v1/tools/mcp/servers/{id}/install-preview|install` 暴露 `ManagedMcpProgramService`，队列仍是 `mcp_program_update`；设置页对 `npx`/`uvx` 服务器显示“安装依赖”。
- `/api/v1/mcp/servers` 与 `/{id}/tools` 接受 `project_id`，项目级服务器进入清单。
- 运行上下文新增 `mcp_servers` 证据块：只列服务器名，不握手；告诉模型用 `mcp_search` 找工具、`mcp_invoke` 调用（审批）。
- **HTTP/SSE 传输**：`tool_mcp_resources` 新增 `transport/url/header_references_json`（迁移 `202611110001_McpTransport`，SQLite/PostgreSQL 各一份）；stdio 必须有 command，http/sse 必须有 http(s) URL。Header 值只存 `ISecretStore` 引用，回显 `********`，`MaterializeForCallAsync` 才解析明文。`McpClientPool` 用 `HttpClientTransport` + `StreamableHttp|Sse` 连接远程服务器。
- **测试**：`McpHttpTransportTests` 用真实 TCP JSON-RPC 夹具覆盖 initialize/tools/list/tools/call（2/2），`McpPassThroughTests` 仍覆盖 stdio（8/8）。
- **未做**：`mcp__server__tool` eager 投影、真实第三方 MCP server 验收。

## 4. Skill/MCP 市场页（M4）

- 三个独立 UIE 页面 id：`market` / `marketSkills` / `marketMcp`，各自 preset；`/market/skills` 与 `/market/mcp` 路由；`repair.ts` 合法页/边距/空市场回退同步。`MarketController.setKind()` 页面级锁定 kind；`MarketFilterCard` 锁定时隐藏 kind 列表，保留搜索/源管理/“去工具设置管理已安装”。
- 侧栏新增 Skills / MCP 入口，命令面板新增 `view.goMarketSkills|Mcp`。
- 热刷新：`MarketController` 每 10s 读技能/MCP 资源的 revision 指纹，变化时重读 catalog + installations；不是 SSE，且只对新 run 生效。
- 市场能力补齐：项目级 MCP 清单、安装依赖按钮、内置技能复制；分页/ACP 列表仍按原状（见边界）。

## 验证

| 范围 | 命令 | 结果 |
| --- | --- | --- |
| 全 .NET 构建 | `node scripts/dotnet.mjs build TinadecOffice.slnx` | 0 错误 |
| Core 定向 | `dotnet test … --filter "AgentModeTopology|AgentModeList|AgentPackEndpoint|GraphOrchestration|WorkspaceSkill|McpInventory|ToolSettingsApiTests|TinadecConfigToolGateway|CoreOpenApiSnapshot"` | **96/96** |
| Core 新增图测试 | `AgentModeTopologyTests` | 4/4（删除缺席项、`edge_key_required`、dangling edge、Solo publish） |
| Core 配置工具 E2E | `TinadecConfigToolGatewayTests` | 4/4（manifest 冻结、审批策略、gateway 脱敏、`PrepareAsync → ResumeAsync → result`） |
| Tools MCP | `dotnet test tests/TinadecTools.Tests --filter Mcp` | **17/17**（stdio 8 + 真实 TCP HTTP 2 + 其它） |
| Gateway | `cd TinadecGateway && bun test src` | **115/115** |
| TinadecUI | `cd apps/TinadecUI && npx vitest run` | **186/186** |
| Desktop 定向 | `npx vitest run`（GraphSeedPack/MarketPage/MarketController/ToolResources/ToolCenter/toolSettingsApi/i18nParity） | **61/61** |
| Desktop 类型/构建 | `npx vue-tsc --noEmit`、`npm run build` | 0 错；生产构建 exit 0 |
| OpenAPI | Core snapshot + Gateway external snapshot | 已按漂移重新生成并复跑通过 |

## 边界与既有失败

- **MCP 传输**：stdio/HTTP/SSE 已实现并用本地 TCP 夹具验证；per-server tool projection 与真实第三方 server 未做。
- **`tinadec_*` 端到端**：`tinadec_inspect` 已走完 dispatcher → resume → gateway → result；写工具的审批批准后 resume 路径由现有 approval 测试覆盖，但未单独为 `tinadec_mode_apply` 写一条。
- **内置技能复制**：已实现 `copy-to-project` 端点与设置页按钮，需项目上下文。
- **真实 Electron**：页面/侧栏/市场点击与窄宽布局未实机走查。
- **既有失败**：`SessionScopeTransferTests` 的 `GlobalAgentConfiguration` 未注册、`AttachmentApiTests.Context_QuotedTextAttachment…` 的 "not text" 文案断言，均在本轮前存在，与本轮改动无直接关系。
- PostgreSQL / Linux / macOS 未验收。

## 下一轮建议

1. 真实第三方 MCP server（HTTP/SSE）与 `mcp__server__tool` eager 投影。
2. 为 `tinadec_mode_apply` 写一条批准 → resume → 发布 → 新 run roster 的 E2E。
3. 把 10s 指纹 poll 换成 Core extension SSE，保留 poll 作为降级。
4. 真实 Electron 走查市场三页、侧栏入口、MCP HTTP 表单、图编辑删除/边键保存。
5. PostgreSQL/Linux/macOS 的 MCP 迁移与 HTTP 传输验收。
