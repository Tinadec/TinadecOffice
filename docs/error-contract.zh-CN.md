# 错误处理与恢复契约

## 2026-10-11 读取传输与屏幕恢复

共享 JSON 请求仅对 GET/HEAD 的网络异常及正文传输中断自动重试：首次请求之后等待250ms、750ms，最多三次。每次重新核对宿主授权，使用发送前固定的URL、headers和storage_id；取消、撤权或离开页面终止旧读取。HTTP错误、JSON解析失败和配置校验不进入这个预算，POST/PUT/PATCH/DELETE不会自动重发。SSE与二进制传输仍由各自模块处理。

读取网络失败在预算用尽后提供`backend_network_unavailable`，分类retryable、动作retry。写请求传输失败提供`backend_write_outcome_unknown`，不能判定服务端未执行，也不自动提供原请求重试。客户端不借公共health成功或网络重试放宽宿主身份校验。

设置的智能体/模型目录拥有页面读取生命周期：挂载或宿主重新就绪刷新，手动读取重试合并，卸载/撤权取消，旧响应不能覆盖新连接。成功读取空列表才显示空态；刷新失败保留旧目录并显示结构化错误。单个智能体详情失败保留目录身份，禁止用空提示词/工具配置编辑；辅助数据失败单独诊断。读取恢复保留未保存编辑草稿，也不会自动安装AgentPack或提交配置。当前实现与验证见[专项报告](../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)，唯一任务继续为APP-HOME-107。

适用：2026-10-10 工作树。功能与验收进度唯一归属 [APP-HOME-107](development-program/02-modules/app/home/TODO.md#app-home-107)。本文件定义产品错误契约；初始框架见 [错误恢复报告](../.tinadec_dev/reports/2026-10-10-error-recovery.zh-CN.md)，本轮接口接线与验收见 [接口回归报告](../.tinadec_dev/reports/2026-10-10-interface-regression.zh-CN.md)。

## 为什么要统一

一次真实的失败：用户删除一个工作区，界面只显示 “The registered project directory is unavailable.”，随后这个工作区不能删除、不能归档、也不能加载。原因是三种不同的信息缺失同时发生：

1. 响应只说“哪里错了”，不说“谁能修、下一步做什么”；
2. 客户端拿不到机器可读的分类，只能把失败显示成一句通用文案；
3. 失败状态没有出口——坏掉的登记项没有任何可执行动作。

统一契约解决的是这三件事，而不是某一个错误码。

## 两个轴

每个失败都在两个轴上被分类：

| 分类 | 含义 | 重试同一请求 |
| --- | --- | --- |
| `user_action_required` | 需要人改点什么（改配置、选目录、登录） | 会再次失败 |
| `retryable` | 稍后重试可能成功（锁被占用、并发冲突、对端忙） | 可能成功 |
| `environment_unavailable` | 本机依赖缺失或不可达（登记的目录被删、工具没装、后端未起） | 需要先修复环境 |
| `internal` | 未预期的缺陷 | 可重试，但应上报 |

分类是封闭集合，客户端只按分类分支，不追逐单个错误码。因此新错误码出现时也会落到合理的一类，而不是“未知”。

## 恢复动作

响应可以列出稳定动作标识，客户端负责文案和行为：

`retry`、`reload`、`open_settings`、`open_storage_settings`、`open_tool_settings`、`unregister_workspace`、`choose_folder`。

客户端只认识上表；未知动作被忽略，不能注入未预期的行为。服务端不下发面向用户的文案。

## 响应形状

所有 *problem 响应*（异常处理、模型校验、作用域中间件）都带这些字段：

```json
{
  "type": "https://tinadec.dev/errors/storage_scope_unavailable",
  "title": "storage_scope_unavailable",
  "status": 409,
  "detail": "The registered project directory is unavailable.",
  "code": "storage_scope_unavailable",
  "category": "environment_unavailable",
  "retryable": false,
  "actions": ["unregister_workspace", "retry", "open_storage_settings"],
  "trace_id": "0HNP6D5UUBC8P:00000011",
  "instance": "/api/v1/projects/{id}/trash"
}
```

配置类错误额外携带结构化 `diagnostics`（文件、行、列、码、严重级别）。

## 数据行也遵循同一契约

工作区列表里无法挂载的登记项不会消失，而是带同样的分类字段：

```json
{
  "id": "…", "storage_id": "…", "name": "skill",
  "availability": "error",
  "availability_error": "The registered project directory is unavailable.",
  "availability_code": "storage_scope_unavailable",
  "category": "environment_unavailable",
  "retryable": false,
  "actions": ["unregister_workspace", "retry", "open_storage_settings"]
}
```

这样侧栏可以直接为这一行提供出口（重新加载 / 取消登记），而不是让整行变成死路。

## 各层职责

| 层 | 职责 |
| --- | --- |
| Core | 产生码与诊断；`ErrorClassification` 决定分类与动作；异常处理与作用域中间件补齐字段 |
| Gateway | 保留码、分类、动作、trace_id、diagnostics，不改写为通用 conflict |
| Desktop `ApiError` | 解析契约、过滤未知动作、`category`/`retryable`/`actions`/`canRetry` |
| Desktop 通知 | 按动作标识查一次注册的处理器，自动给错误通知挂上恢复按钮 |
| 视图 | 决定按钮文案与落点（设置页、侧栏行），不下沉业务逻辑 |

## 界面侧：把契约渲染出来

共享状态 `useErrorState` 把一个 catch 到的任何东西归一成 `ErrorState`（message / code / category /
retryable / actions / traceId / details / status）。页面用 `failure.set(value)` 记录、`failure.clear()`
在重试或保存成功时清除，模板按字段渲染，不再把错误压成一句字符串。

`recoveryActions(state, handlers)` 只保留**这一页真的能执行**的动作：没有对应处理器的动作不渲染，
不会出现点了没反应的按钮。文案来自共享词表，避免每个页面各写一份“重试”。

已完成迁移的界面：

| 界面 | 之前 | 现在 |
| --- | --- | --- |
| 存储与配置页（`StorageSection.vue`） | 17 个 catch 全部 `error.value = message(value)` | 全部走 `failure.set`，渲染原因 + diagnostics + trace_id + 可用动作（重试/重新加载/取消登记） |
| 工作区窗口（`WorkspaceEditorDialog.vue`） | 3 个 catch 压成字符串 | 走 `failure.set`；重试 = 重跑对话框自身的读取路径（`loadEditor()`），浏览文件夹 = 重新选目录 |

## 恢复入口的注册

`setErrorRecoveryHandlers` 与 `setErrorActionLabels` 在应用入口各调用一次；`HomeController.installErrorRecovery()` 提供 retry / reload / unregister_workspace 的实现。同一失败只显示第一个可用动作——同时给“重试”和“重新加载”会让人无从区分。

## 边界

- 5xx 默认分类为 `internal` 且可重试；4xx 默认 `user_action_required` 且不标注可重试。
- 未知分类与未知动作被丢弃，回退到按状态码推断，不信任上游任意字符串。
- `retryable` 表示“同一请求可能成功”，不承诺幂等；写操作仍按各自语义返回 409/412。
- 契约不改变任何已有错误码、幂等、ETag 或审批流程。

## 宿主就绪与界面预览

公开 health 仅表示进程可响应。Desktop 业务准入还要求 main 对 Core、Gateway 完成 nonce/HMAC 身份验证；状态为 checking / ready / unavailable / rejected，浏览器为 preview。首次暂不可用和运行中瞬断撤销请求签发，并通过单次在途、有上限退避重新验证。身份拒绝阻断自动重验，允许用户明确重试，不停止未知服务。退出取消检查，迟到结果不能恢复授权。

getHostStatus、状态订阅与 retryHostConnection 不包含凭据，按可信窗口文档身份准入，不依赖后端已认证。连接恢复只刷新读取；不重放创建、保存、安装。5173 显示界面预览说明，需要可信宿主的目录选择、存储和安装操作明确禁用，不以空数组或静默 no-op 伪装成功。宿主自动恢复验证不等于重新启动已退出的本地服务。

### 本地 IPC 契约不可用

Electron main 与页面可能在开发重载后属于不同版本。已有 preload 缺少状态方法，或调用 `tinadec:host-status` / `tinadec:host-retry` 返回 `No handler registered`，由共享桥适配器识别为 `restart_required`，业务准入抛 `desktop_restart_required`。其他 IPC 异常归为 `host_bridge_unavailable` / unavailable，不回传原始异常文本。已有 bridge 的异常不能降级为 ready 或浏览器 preview。

这两类本地失败使用 `environment_unavailable`、`retryable=false` 和客户端合成状态 503；请求尚未发往后端，没有服务器 trace_id。版本不一致的 actions 为空，由共享宿主 Banner 提供专用恢复。初始工作区、诊断和运行就绪读取不再各自重复通知；连接层立即撤权，版本不一致停止启动及健康轮询和无效重试。迟到状态读取或重试回执不能覆盖较新的宿主广播撤权。

开发环境展示“关闭当前开发进程，再从仓库根运行 npm run dev”的指引，不提供旧 `app.relaunch` 自动重启按钮：开发启动器收到 Electron 退出会停止 Vite，根启动器也会结束同组服务。生产环境已有可信 restart IPC 可提供单飞重启入口，调用失败显示手动关闭重开指引。此处理不新增启动器 watcher，不自动停止用户进程，不重放创建、保存或安装。源码、现场时间对照和 Windows Electron 夹具边界见 [IPC 修复报告](../.tinadec_dev/reports/2026-10-10-host-ipc-mismatch.zh-CN.md)。

## 请求归属与局部读取失败

请求发送前固定作用域，优先级为显式 storageId → 调用绑定 → 请求项目/会话/运行身份 → 未绑定列表默认 user → 当前选择。ApiError 保存客户端已捕获的 storageId，取消登记不能由服务端路径文本推导授权目标。

项目内列表只读绑定作用域；历史、归档、回收站、搜索通过共享读取器逐项结算用户及项目作用域，以 storage_id + id 去重。某项失败保留该项原记录、显示刷新失败；其他项照常更新。取消继续传播，整个发现步骤失败不能退化成“只有用户库且成功”。首页项目、诊断及运行就绪状态独立更新。

## 工作区可用性和生命周期

/scopes 逐项读取描述和配置，ready 表示描述可读，不表示已提前挂载全部数据库。/projects 挂载时再检查数据库。缺失目录、配置损坏、授权不一致、数据库与内部缺陷分别返回分类、trace_id 和定位；坏项保留登记与实际存储根。无法读取生命周期时使用 null，不伪造 active、archived 或 trashed。挂载查询任意生命周期，保留归档/回收站记录，只有记录确实不存在才初始化。取消登记由可信宿主执行，不要求先挂载，也不删除数据。

Core 已提供 code 的 ProblemDetails 同样补齐分类，保留业务已给出的分类、诊断及 trace_id。Gateway 使用公开字段白名单转发，过滤未知私有扩展；AgentPack 失败在上游提供 ETag 时同样转发，不补造缺失值。读重试保留工作区表单草稿，条件保存冲突需要明确载入最新配置。AgentPack 普通重连保留失败，手动重试重新预览、确认一次安装。
