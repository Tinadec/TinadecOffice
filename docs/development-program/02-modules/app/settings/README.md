# Settings / 配置中心

## 2026-10-11 智能体目录读取恢复

智能体/模型目录按挂载与宿主就绪读取，取消和代次阻止旧响应覆盖，失败保留旧目录且展示错误，不以空列表替代失败。智能体详情每批四个；局部失败阻止空定义编辑，辅助读取保旧值并诊断，恢复不覆盖未保存草稿。共享GET/HEAD网络重试、Core批量预览和配置摘要规则见[专项报告](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)。唯一任务继续[APP-HOME-107](../home/TODO.md#app-home-107)，模块001/101整体范围保持原状态。

模块ID：`APP-SETTINGS` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

最近专项：2026-10-09，d5e6c8d8 + 当前工作树；仅 Debug Studio 本机开关。

本模块目前处于**初始源码清点**，还未完成逐功能审计；下列介绍继承总图中已核对的职责，初始状态区分源码能力、范围与缺口。

## 文档入口

- [模块架构与边界](ARCHITECTURE.md) · [模块SVG](architecture.svg)
- [功能与完成情况](STATUS.md) · [本模块TODO](TODO.md)
- [全部模块](../../../MODULE-INDEX.md) · [总TODO](../../../01-program/MASTER-TODO.md)

## 功能介绍

### Settings / 配置中心

编辑 Core 中的草稿与发布版本，管理模型提供方及参数、包与集成。桌面偏好另存本地。

- 模型、智能体、模式、AgentPack
- 提示词、集成、工作区/偏好

### 2026-10-09 Debug Studio 本机开关

设置的“关于 → 开发者工具”提供 Debug Studio 显示开关，默认关闭。唯一来源是稳定 bootstrap `desktop.toml` 的 `developer.debug_studio_enabled`，默认路径为 `~/.tinadec/config/desktop.toml`；`TINADEC_HOME` 指定 bootstrap 根时使用该根的 `config/desktop.toml`。这是本机偏好，不属于项目配置或 Core 草稿/发布版本。`TINADEC_GATEWAY_URL` 只管理 Gateway 地址，仍读取此偏好；非布尔、非表或无效 TOML 明确报错，界面保持关闭并显示读取失败。

```toml
[developer]
debug_studio_enabled = false
```

保存只采信宿主返回值；读取/保存期间开关禁用，失败保留原状态并可明确重试。Electron 保存 IPC 仅接受可信主窗口的主 frame，返回完整配置，并向存活应用窗口广播无 payload 变更事件，Renderer 重新读取同一来源。保存采用同目录临时文件原子替换；常见 table/quoted/dotted/inline 形状局部编辑后重新解析核对，保留字段与注释；特殊有效 TOML 形状回退既有 serializer 保留字段，注释可能丢失。

任务 [APP-SETTINGS-104](TODO.md#app-settings-104) 与功能 APP-SETTINGS-F004 仅覆盖这个开关。验收是 Windows 浏览器 preview、Renderer 定向回归与宿主 VM/Node 测试，尚未验证真实 native 窗口或安装包；见 [本轮报告](../../../../../.tinadec_dev/reports/2026-10-09-ui-comments-2.zh-CN.md) 和 [宿主证据](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/debug-studio-host.md)。

## 源码入口

- [apps/desktop/src/pages/SettingsPage.vue](../../../../../apps/desktop/src/pages/SettingsPage.vue)
- [apps/desktop/src/settings/sections/AgentPacksPanel.vue](../../../../../apps/desktop/src/settings/sections/AgentPacksPanel.vue)
- [apps/desktop/src/api.ts](../../../../../apps/desktop/src/api.ts)
- [apps/desktop/src/settings/sections/AboutSection.vue](../../../../../apps/desktop/src/settings/sections/AboutSection.vue)
- [apps/desktop/src/composables/useDebugStudio.ts](../../../../../apps/desktop/src/composables/useDebugStudio.ts)
- [apps/desktop/electron/appConfig.cjs](../../../../../apps/desktop/electron/appConfig.cjs)

## 相关模块

- [AgentConfiguration](../../core/AgentConfiguration/README.md)
- [Models · 模型与 Harness](../../core/Models/README.md)
- [Skills · 市场与集成配置](../../core/Skills/README.md)
- [Desktop / 偏好与布局持久化](../local-state/README.md)

## 本模块的开发工作方式

先拆分STATUS中的功能、核对实际行为并定位缺口，再逐项执行TODO。每次实现同步功能状态、模块图和证据；目标行为与验收边界写清后才进入该任务的实施。核查任务完成不代表该模块所有能力已经完成。

## 2026-10-08 工具配置

工具入口提供九个标签、项目/Agent选择、共享默认及稀疏覆盖、MCP/Skills资源绑定与显式保存。tool_scope仍只在AgentCenter编辑。共享总览按各启用Agent的Core有效配置显示使用者，部分读取失败可诊断且切项目的迟到响应不会覆盖新结果；来源未声明时明确显示该状态。参数通过严格JSON高级编辑器校验，过期保存保留草稿；准入运行冻结生效值。Monaco五类Worker使用Vite的`?worker`入口打包，在真实`app://bundle`生产路径校验补全和错误定位。实施契约见[工具配置](../../../../../.tinadec_dev/specs/2026-10-08-agent-tool-settings.zh-CN.md)，验证见[实施报告](../../../../../.tinadec_dev/reports/2026-10-08-tools-settings.zh-CN.md)。
