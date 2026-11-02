# 计划书③：执行面沙箱强化 — 从"工具有沙箱"到"会话有沙箱、网络有治理"

> 状态：草案 v1（2026-11-02），来源：Qoder/Codex 竞品对比（当日会话），**未登记**进 §11/backlog，待批准后登记。
> 判据沿用既有差距报告 A/B/C 三级；每批先红后绿+变异对照，收尾必须过 live 验收。

## 0. 先修正一个提法（证据留痕）

竞品对比初版（2026-11-02 上午）称"shell 裸奔、无沙箱"——**该提法已过时，作废**。深查证实：`shell` 工具自 2026-10-02（commit 441d8fa5）起走 `CommandSandboxRuntime.ExecuteSandboxedAsync`（`TinadecTools/Tools/Command/ShellTool.cs:140-145`），且机制相当扎实：Windows 专用低权本地账户 `TinaSbx_<sha256_12>`（`SandboxAccountManager.cs:17-18`）+ `LogonUserW`（`Win32TokenApi.cs:10`）+ 每次调用临时 ACL 授/撤（`WindowsSandboxBackend.cs:36,54-55`）+ 流式路径 Kill-On-Close Job Object（`JobObjectManager.cs:12-31,107`）+ `--sandbox-runner` 子进程中转、授权参数不进模型 JSON（`WindowsSandboxRunner.cs:18-43`）。推翻旧结论的凭据与既有报告 §4.5 的教训一致：要用现场代码，不用早前扫描的结论。

因此本计划不是"给 shell 加沙箱"，而是按深查后的**真实剩余缺口**重排。对标 Codex 的 OS 级沙箱执行（含网络受限档），本仓"沙箱存在"这一层已对齐，差的是**覆盖完整性与网络治理**。

## 1. 真实剩余缺口（2026-11-02 实测）

1. **会话级沙箱只覆盖一半**：Job Object 只挂在 long_lived 流式路径（`WindowsSandboxBackend.cs:106-107`）；普通 shell 调用每次新起 `cmd.exe /d /s /c`（`ShellTool.cs:197`），cd 状态不跨调用保持——既是 UX 缺口，也让"会话"在沙箱语义上不成其为会话。
2. **网络完全不受沙箱治理**：`docs/security.md:9` 明示网络不限制、交主机防火墙。Codex 的执行沙箱有网络受限档；本仓治理面里 `web_fetch` 有 SSRF 防护（`TinadecTools/Tools/Web/WebFetchGuard.cs:24,130-161`），但任意进程出口没有治理点。
3. **审批 widening 使前缀规则失效**：`additional_read/write_paths`、环境变量白名单等 widening 字段让前缀规则整体失效（`IApprovalRules.cs:157-158`）——一次带 widening 的批准事实上拿到了规则外权限，规则引擎的锐边。
4. **桌面本地终端（node-pty）无沙箱**：`terminalManager.cjs:580`，现已限 catalog 白名单（:212-222）。定位是"用户自有 shell"（对标 VS Code 终端，by-design），但**智能体可否被授权驱动它**没有决策记录——git 面板走会话工具进程（有治理），本地终端没有等价边界。
5. **账户生命周期摩擦**：首次建沙箱账户需 UAC（`WindowsSandboxSetup.cs:52-72`）；账户密码 DPAPI 存（DpapiCredentialStore）。
6. **无 per-session 隔离**：现机制是"每工作区一账户+临时 ACL"，没有 per-session token/AppContainer；`CreateProcessAsUserW` 已封装未启用（`Win32TokenApi.cs:28`）。

## 2. 目标 / 非目标

**目标**
1. 档A：沙箱执行全覆盖——所有 shell/command 执行路径统一挂 Kill-On-Close Job Object；cwd 会话化（持久+恢复）；widening 审批显式化。
2. 档B：网络治理档——沙箱内"禁网"开关 + 按需白名单出口（进程级最小实现）。
3. 档C（远期）：per-session 受限 token 隔离；OOBE 预建账户消除 UAC 摩擦。
4. 决策项：智能体驱动本地终端的治理边界，给出明确答案并写入 security.md。

**非目标**
- 不重写沙箱机制（低权账户+ACL+Job 的骨架保留，它是本仓已验证的差异化资产）。
- 不做 macOS/Linux 对等实现（Windows 优先是既定产品决策）。
- 桌面本地终端不做默认沙箱化（用户自有 shell 的定位保留，见 §4.4）。

## 3. 设计方案

### 3.1（档A）会话级收口
- 普通 shell 调用与流式路径统一：执行一律经 Job Object（Kill-On-Close），把 `WindowsSandboxBackend.cs:106-107` 的挂载从"流式专属"提为通用路径。
- shell 会话 cwd 语义：普通调用仍无状态（保持现状），long_lived 会话（上限 16，`TerminalSessionRunner.cs:57`）的 cwd 由 Core 记账并在会话恢复时重放——不做 PTY（现状管道进程无 resize，`ConPtySession.cs` 仅用于 TUI harness，不在本计划扩）。
- **widening 显式化**：带 `additional_read/write_paths`/env widening 的审批请求：(a) 审批卡片必须明示"此批准使前缀规则失效"；(b) widening 批准永不进前缀规则缓存，每次逐审（改 `IApprovalRules.cs:157-158` 的短路顺序）。这是规则正确性问题，优先级高于其余。

### 3.2（档B）网络治理
- v1 取最小可信实现：`SandboxPolicyStore`（`SandboxPolicyStore.cs:20`，state/sandbox-grants.toml）加 `network = denied|allowlist`；denied 档用既有机制收口——沙箱账户无代理配置+进程级出站点位校验（能拦多少说多少，**不谎称全拦**，契约措辞照 `web_fetch` 的"一份失败只说一件事"纪律）。
- allowlist 档：本地命名管道代理（复用 WebFetchGuard 的域判定），仅代理 HTTP(S)，其他协议在 denied 档下直接失败并给结构化原因。
- `docs/security.md:9` 的"网络交主机防火墙"措辞同批改写，写明新档位的真实边界——本仓规矩：安全承诺与实现同批，不写做不到的承诺。

### 3.3（档C，远期）per-session 隔离与账户摩擦
- 启用已封装的 `CreateProcessAsUserW`（`Win32TokenApi.cs:28`）做 per-session 降权 token 试点（先只给 long_lived 会话）。
- OOBE 预建 `TinaSbx_*` 账户（一次性 UAC，之后免提示），失败时明确降级路径并告警。

### 3.4 决策项：智能体 × 本地终端
- 建议决策（供批准）：本地终端面板保持"用户自有 shell、无沙箱"，但**新增一条硬边界——智能体工具调用永远不能注入本地终端 stdin**（`/api/v1/terminals/{id}/stdin` 的调用方校验：仅人工会话可写，`TerminalEndpoints.cs:29-84` 加来源校验）。这样"用户终端"与"智能体终端（shell 工具，已沙箱）"两条链的边界清晰，security.md 落字。

## 4. 约束确认

- Core↔TinadecTools 是每工作区一个子进程、BOM-free UTF-8 line-delimited JSON over stdio、call id 关联（`TinadecToolsProcessManager.cs:9-13`）；权限在 TinadecTools 内部构建——**改沙箱不动 wire 协议**（P/Invoke 封装齐备：Job `Win32ProcessApi`、token `Win32TokenApi.cs:28`、ACL、DPAPI、ConPTY）。
- 审批/错误分类沿用既有：`ToolDispatcher.cs:201,336-360`（park→approval.requested）、`ToolDispatcher.cs:425-611`+`RunErrorTaxonomy.cs`（`runs.terminal_error_category` 落库）。

## 5. 分批落地与验收

| 批 | 内容 | 验收门禁（先红后绿/变异） |
|---|---|---|
| S1 | widening 规则修复（逐审+卡片明示） | 变异：把 widening 短路装回→恰红"带 widening 的批准不进前缀缓存"用例；审批卡片契约（error-contract 词表）同批 |
| S2 | Job Object 全覆盖 + long_lived cwd 记账/重放 | 变异：摘掉普通路径的 Job 挂载→恰红；会话恢复重放 cwd 的 e2e |
| S3 | 网络档位（denied 默认关，allowlist 代理）+ security.md 同批改写 | denied 档下出网用例的结构化失败；代理只放行白名单域的变异；live 验收（沙箱内真实命令拒绝路径走一遍） |
| S4（远期） | per-session token 试点 + OOBE 预建账户 | 单独评审，不与本批混 |
| 决策批 | 本地终端 stdin 来源校验 + security.md 边界落字 | 工具身份注入 stdin → 403 且审计留痕 |

## 6. 风险与开放问题

1. 账户/ACL 与企业域策略（组策略禁本地账户创建）：探测+降级路径要明确，OOBE 前置探测。
2. 每调用 `LogonUserW` 的性能与句柄成本：S2 顺手量一次基准，超预期再谈缓存。
3. 网络治理的可信边界：用户态进程级拦截不承诺全拦，措辞纪律是本批红线（宁可少承诺）。
4. 开放问题：allowlist 档是否覆盖 git push/pull（git 工具走会话工具进程，有自己的治理）——建议 v1 不覆盖，git 网络面另行登记。

## 7. 与既有登记项的关系

- 兑现 `docs/security.md` 既有承诺的补强，与 backlog Pack 数字签名（供应链面）不同层，不冲突。
- S1（widening）与本仓"审批可信"A级资产直接相关，建议四份计划书中最优先开工。
- 与 §11 登记项无依赖冲突。
