# Settings / 配置中心：TODO

2026-10-11智能体目录性能与读取恢复继续由[APP-HOME-107](../home/TODO.md#app-home-107)记录，Settings实现/证据见[专项报告](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)。本模块既有配置发布/整体审计任务保持独立。

模块ID：`APP-SETTINGS` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

最近专项：2026-10-09，d5e6c8d8 + 当前工作树；仅 Debug Studio 本机开关。

本文件是该模块任务的唯一编辑入口；总TODO由本文件生成。任务ID长期稳定，完成或取消后保留记录；每项任务记录工作范围与验收条件。

状态：待核查 / 未开始 / 待方案 / 进行中 / 阻塞 / 待验收 / 已完成 / 不做 / 已被替代。优先级是初始建议，可在逐模块分析后调整。

<a id="app-settings-001"></a>

### APP-SETTINGS-001 完成 Settings / 配置中心 的逐功能审计与模块图精化

- 类型：核查
- 状态：待核查
- 优先级：P2
- 主责模块：APP-SETTINGS
- 前置依赖：未细化；开工前在此列出实际Task ID，不能把全部关联模块当硬依赖
- 关联功能：待逐功能拆分后绑定本模块Feature ID
- 完成证据：未产生；本轮仅建立任务与源码基线

**问题与目的**

已有职责投影和初始源码事实，还没有把每个用户/调用场景逐项拆分并完成实现、测试和运行证据对账。

**验收条件**

- [ ] 拆分STATUS.md中的聚合能力，每个功能分配稳定feature id、明确输入/输出、失败与权限边界。
- [ ] 逐条定位实际实现、活动测试和历史报告；把源码可见、历史验证与本轮验收分别记录。
- [ ] 至少明确成功、错误/取消、权限和持久化/恢复场景中哪些适用；未适用的写出理由。
- [ ] 精化ARCHITECTURE.md中的调用/数据流；每个有向关系提供源码依据，职责关联不冒充编译依赖。
- [ ] 将确认缺口登记独立TODO，写明目标行为、范围、前置依赖和可执行验收条件；无证据的保持待核查。

**初始证据**

- [apps/desktop/src/pages/SettingsPage.vue](../../../../../apps/desktop/src/pages/SettingsPage.vue)

<a id="app-settings-101"></a>

### APP-SETTINGS-101 验收配置发布到真实供应商与冻结 run 的生效范围

- 类型：验收
- 状态：未开始
- 优先级：P1
- 主责模块：APP-SETTINGS
- 前置依赖：未细化；开工前在此列出实际Task ID，不能把全部关联模块当硬依赖
- 关联功能：待逐功能拆分后绑定本模块Feature ID
- 完成证据：未产生；本轮仅建立任务与源码基线

**问题与目的**

模型参数和配置修复已有单测及历史局部验证，但真实外部模型与 harness 账号回合仍缺证据。

**验收条件**

- [ ] 修改 provider/model 参数并保存，新 run 使用参数，旧冻结 run 不追溯变化
- [ ] 过期 revision 提示可刷新且保存失败保留草稿
- [ ] API、CLI、TUI、ACP 仅对已纳入支持范围的渠道分别用真实账号验收
- [ ] 删除模型按 provider+model 精确匹配，默认模型与其他配置不被误改

**初始证据**

- [apps/desktop/src/pages/SettingsPage.vue](../../../../../apps/desktop/src/pages/SettingsPage.vue)
- [apps/desktop/src/api.ts](../../../../../apps/desktop/src/api.ts)

**历史任务映射**

- [docs/model-settings-review-2026-10-05.zh-CN.md](../../../../model-settings-review-2026-10-05.zh-CN.md)：验证与后续事项；只作来源，不继承完成勾选

<a id="app-settings-103"></a>

### APP-SETTINGS-103 Skills 包安装与资源管理界面

- 类型：实现
- 状态：已完成
- 优先级：P1
- 主责模块：APP-SETTINGS
- 前置依赖：APP-SETTINGS-102、CORE-SKILLS-102
- 完成证据：[实施报告](../../../../../.tinadec_dev/reports/2026-10-09-skills-management.zh-CN.md)、[Desktop 定向证据](../../../../../.tinadec_dev/evidence/2026-10-09-skills-management/desktop-focused.log)
- 范围：共享/项目包、完整文件与正文预览、导入/更新/停用/删除、市场作用域、审批轮询、迟到请求保护、Agent 绑定与窄窗口键盘导航。
- 证据：[实施报告](../../../../../.tinadec_dev/reports/2026-10-09-skills-management.zh-CN.md)、[Desktop 证据](../../../../../.tinadec_dev/evidence/2026-10-09-skills-management/desktop-focused.log)

- [x] 组件、Gateway 契约、生成客户端、类型检查通过：设置 160/160，Desktop 全量 1091 passed/14 skipped，Gateway 88/88。
- [x] 生产 renderer 构建通过；共享导入/PUT 现在显示待审批回执与提交前目标/文件数确认，作用域文案区分共享与项目。

**验收条件**

- [x] Skills 共享/项目包显示清单、正文、附件预览、来源/提交/可用状态及审批回执。
- [x] 编辑按完整包替换语义保留附件，迟到请求不会覆盖当前项目或 Agent；窄窗口标签可键盘导航。

<a id="app-settings-102"></a>

### APP-SETTINGS-102 提供工具及 Agent 工具配置设置

- 类型：实现
- 状态：已完成
- 优先级：P1
- 主责模块：APP-SETTINGS
- 前置依赖：CORE-TOOLS-101、CORE-SKILLS-101
- 关联功能：APP-SETTINGS-F002
- 范围：九标签、项目/持久 Agent 选择、有效值与来源、严格 JSON Schema 编辑、差异和明确保存、退出保护、资源绑定与诊断、键盘和窄窗口。
- 验收：组件/类型/生产构建、Gateway 条件保存与错误转发、真实 Desktop 三类 Agent 授权及资源隔离。三平台实际验证边界分别记录。
- 完成证据：[实施与验证报告](../../../../../.tinadec_dev/reports/2026-10-08-tools-settings.zh-CN.md)。不替代 APP-SETTINGS-001 的整体审计或 APP-SETTINGS-101 的外部模型验收。

**验收条件**

- [x] 完成任务列出的实现范围及条件保存、冻结配置和资源隔离回归。
- [x] 取得真实 Windows Core/Desktop/Tools 证据；PostgreSQL 及 Linux/macOS 边界单独记录。
- [x] 同步模块说明、API/客户端及共享实施报告，不将专项完成扩大为整体模块验收。

<a id="app-settings-104"></a>

### APP-SETTINGS-104 在关于设置提供默认关闭的 Debug Studio 本机开关

- 类型：实现
- 状态：已完成
- 优先级：P2
- 主责模块：APP-SETTINGS
- 前置依赖：无新增后端依赖；复用 Electron bootstrap TOML 和现有可信主窗口校验。
- 关联功能：APP-SETTINGS-F004
- 范围：关于页开关、严格本机配置来源、显式保存、失败反馈与原状态保留、跨窗口刷新；打开/禁用后的窗口边界由 [APP-DEBUG-102](../debug-studio/TODO.md#app-debug-102) 持有。
- 完成证据：[本轮报告](../../../../../.tinadec_dev/reports/2026-10-09-ui-comments-2.zh-CN.md)、[Renderer 定向回归](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/debug-preference-tests.md)、[宿主配置与 IPC 证据](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/debug-studio-host.md)。

**问题与目的**

Debug Studio 侧栏入口此前始终显示。用户要求把它作为本机开发者选项默认关闭，明确来源和保存失败，并由可信宿主持有修改权限。

**验收条件**

- [x] 关于页显示默认关闭开关，读取/保存 pending 时禁用；保存成功采信宿主值，失败保留状态并显示错误供明确重试。
- [x] 来源为稳定 bootstrap `desktop.toml` 的 `developer.debug_studio_enabled`，默认 `~/.tinadec/config/desktop.toml`；Gateway 环境变量不跳过本机偏好读取，无效类型/TOML 明确失败。
- [x] 可信主窗口保存原子替换，保留既有字段；常见源结构保留注释，特殊有效形状 serializer 回退可能失去注释；跨窗无 payload 通知触发重新读取。
- [x] Windows 浏览器 preview 与 Renderer 19/19、宿主配置/IPC 10/10及相关 Node 回归23/23通过；对应证据不重复合计。此具体开关完成不代表真实 native 窗口、安装包、全平台或 APP-SETTINGS-001/101 整体验收。
