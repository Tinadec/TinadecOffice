# 同类产品调研：shell 治理与中途插话（2026-09-28）

用途：为 `docs/agent-graph/` 的资源账本、shell 粒度、三种投递提供事实依据。只记源码/文档能证实的事实；推断标注"（推断）"。

快照：

| 对象 | 位置 | 版本 |
|---|---|---|
| Codex | `C:\git\agent\codex\codex-rs` | `44fe510ce3`（2026-09-28） |
| DeepSeek Harness | `C:\git\dsh\deepseek-harness` | `21638c5631`（2026-09-27） |
| dsh-agent-teams（第三方插件） | `C:\git\agent\dsh-agent-teams` | 2026-09-27 克隆 |
| Claude Code | 官方文档 code.claude.com/docs | 2026-09-28 查阅 |
| LangGraph | `C:\git\agent\langgraph\libs` | `07b33185e`（2026-09-27） |

## 1. shell 治理对照

| | 同一回合内并行 | 多智能体同时写 | 沙箱 | 审批 | 知道 shell 改了哪些文件 |
|---|---|---|---|---|---|
| Codex | `exec_command` 可并行；只有 `apply_patch` 独占（`core/src/tools/parallel.rs:205-209`，`handlers/unified_exec/exec_command.rs:142-144`，`handlers/apply_patch.rs:298-313`） | 子智能体继承父的 cwd 与权限（`core/src/agent/child_config.rs:167-194`）；无跨智能体锁、归属、冲突检测，只靠提示词"写入集不相交"（`multi_agents_spec.rs:724`）；worktree 只在 `codex exec --worktree`（功能开关）与 TUI | 每条命令进沙箱；`.git`/`.codex` 默认只读（`protocol/src/permissions.rs:860-863`）；Windows 受限令牌 + 每个工作区一个能力 SID（`windows-sandbox-rs/src/cap.rs:19-25`） | `prefix_rule` allow/prompt/forbidden，最严者生效（`execpolicy/README.md`）；受限沙箱内普通命令免批（`core/src/exec_policy.rs:799-837`）；已知安全命令清单 2026-08-19 删除 | 不知道：`TurnDiffTracker` 只跟踪 apply_patch（`core/src/turn_diff_tracker.rs:47-49`）；快照/撤销 2026-04 删除 |
| DeepSeek Harness | shell 一律独占："Bash has no proven input-sensitive classifier"（`.agents/notes/implemented/feature/2026-07-10-parallel-tool-call-execution.md:63`）；并发标志 `isConcurrencySafe`（`packages/core/tools/src/index.ts:1303-1311`） | 子智能体继承 cwd（`packages/subagent/subagent/src/child-agent.ts:147`）；文件工具有读后写与过期写拒绝 `FS_STALE_VERSION`，bash 绕过（`packages/experimental/agent-team/README.md:207`）；无 worktree | 三档 read-only / workspace-write / danger-full-access，只管文件（`packages/sandbox/sandbox/src/index.ts:24-30`）；Windows 低完整性写受限令牌、每工作区一个写身份，报 partial（`packages/sandbox/sandbox-windows-acl/src/index.ts`） | ask / never，无"本会话总是允许"（`packages/interaction/user-approval/src/types.ts:32`）；沙箱内免批，申请更宽模式才问；实验性 LLM 风险审查（`packages/experimental/auto-review/src/index.ts:1-8`） | 按回合对比两次 git 快照，私有索引，不碰仓库（`packages/deliverables/workspace-changes/src/git.ts:143-171`）；子智能体会话不记 |
| Claude Code | 文档未写明 | 共享目录；官方建议 `isolation: worktree` 与按文件分工（sub-agents、agent-teams 文档）；只有任务认领用文件锁 | macOS / Linux / WSL2；**原生 Windows 不支持**（sandboxing 文档） | 前缀/通配规则；复合命令逐段检查；只读命令免批（permissions 文档） | 不知道：检查点只覆盖编辑工具（sessions 文档） |
| 我们（基线） | —— | 本批：每次 shell 调用占整个工作区租约（有 bug，见 todo） | `command_run` 走 Windows 沙箱（`TinadecTools/Runtime/Sandbox/`）；**`shell` 不走**，直接 `Process.Start`（`TinadecTools/Runtime/TerminalSessionRunner.cs:245-259`） | shell 永不自动批（`TinadecCore/Governance/AutoApproveOptions.cs:42`） | 不知道 |

第三方 dsh-agent-teams：派发时声明写入范围 `inScope`，两个范围重叠的写任务直接拒绝，除非二者有依赖（`src/quality-gates.ts:391-404`）；完成时自报的 `changedPaths` 超出范围则不能完成（`:533-543`），但自报不是宿主拦截（`README.md:46`）。

## 2. 中途插话语义

| | 排队 | 转向 / 插入 | 停止 | 分叉 |
|---|---|---|---|---|
| Codex | 会话级输入队列 | steer 只截断模型输出流（`session/input_queue.rs:221-240`），已开始的工具调用等它跑完（`session/turn.rs:3120-3125`） | 取消回合，100ms 后中止（`tasks/mod.rs:951-959`）；交互式进程**不杀**（`unified_exec/process_manager.rs:596-619`） | —— |
| DeepSeek Harness | 每条排队消息各成一个后续回合，回合边界取（`packages/core/agent-loop/src/inbox.ts:109-114`） | steer 不打断任何东西：当前模型回复及其工具调用全部结束后，作为同一回合的下一步（`packages/core/agent-loop/src/agent.ts:534-538`）；客户端注释说"会打断"是错的 | cancel：部分输出存为 interrupted；进行中的工具等完，bash 杀进程；未开始的给合成"已中止"（`agent.ts:448-465`，`tool-calls.ts:238-259`） | 复制会话日志到新会话，同 cwd，原会话不取消，之后互不共享（`packages/core/session/src/fork.ts:21-29`） |
| Claude Code | 输入的消息排队，当前工具调用结束后在同一回合送达（interactive-mode 文档） | 同左；Ctrl+Enter 立即送出 | Esc 立即停止当前工具调用 | —— |

结论：三家都没有"共享同一上下文的并行实例"。DeepSeek 的 fork 是复制后分道扬镳，不是共享。

## 3. LangGraph：并发写入怎么合并

- 同一步里并发的节点写共享通道，步末按通道声明的合并函数合并（`langgraph/langgraph/channels/binop.py:65`，`pregel/_algo.py:232` `apply_writes`）。
- 没声明合并函数的通道一步收到两个写入就报错："Can receive only one value per step"（`channels/last_value.py:61`）。
- `Send` 让同一节点带不同输入并行跑 N 份再汇总（`types.py:732`）；`interrupt()` 暂停等人（`types.py:887`）；`BaseStore.search(namespace, query=…)` 跨线程语义回查（`checkpoint/langgraph/store/base/__init__.py:779`）。
