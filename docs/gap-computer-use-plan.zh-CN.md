# 计划书⑥：电脑控制 — 浏览器层收口先行，桌面级远期（对应扩张调研 §3.2）

> 状态：草案 v1（2026-11-03），来源：`expansion-directions-research.zh-CN.md` + Qoder 0.5.1 逆向 + 本仓代码实测。**未登记**，待批准。
> 判据沿用既有差距报告 A/B/C 三级；每批先红后绿+变异对照，收尾必须过 live 验收。

## 0. 结论一句话

**本仓的浏览器 CUA 操作面已经建成**——`TinadecTools/Tools/Browser/` 是一套 CDP 浏览器工具族（经 Electron bridge）：快照/导航/等待/点击/输入/按键/滚动/历史/标签页三件套/批量/截图全齐，带 ref 失效、稳定错误码、`browser_user_in_control` 硬停。真正的缺口是四个：**截图"拍得着但看不见"（视觉输入 §4.3）、治理策略面薄、无录制回放、桌面级（浏览器外）无**。本计划按这四个缺口排批，不重造操作面。

## 1. 现状与证据（2026-11-03 实测）

**已建成（超出此前调研的认知）**
- 动作面：`ClickAsync/TypeAsync/PressAsync/ScrollAsync/HistoryAsync/TabOpenAsync/TabSelectAsync/TabCloseAsync/BatchAsync`（`BrowserToolRuntime.Actions.cs:73-551`）+ snapshot/navigate/wait/screenshot（`BrowserToolRuntime.cs:346` 起）。
- 机制：CDP（`Page.getFrameTree`、可访问性树查询，`BrowserToolRuntime.cs:109,127`；"One request to the Electron bridge. `Parameters` is the raw CDP params object"，`BrowserModels.cs:143`）——bridge 是 Electron 主进程，天然继承桌面 App 的进程边界。
- 治理雏形已在：`HardStopCodes` 含 `browser_user_in_control / browser_dialog_open / browser_permission_denied / browser_download_started / browser_file_chooser_opened / browser_renderer_gone`（`BrowserToolRuntime.cs:23-40`）——"用户接管即硬停"是全部竞品都没写明的好设计；`BrowserNavigationPolicy` 已存在。
- 会话状态：refs 属于 tab、文档变更即失效、预期失败=稳定码不抛异常（`BrowserToolRuntime.cs:5-9` 头注）。

**缺口**
1. **截图进不了模型**：`CoreAttachmentReadTool.cs` 明写不向 provider 发 image part（既有报告 §4.3）——`browser.screenshot` 拍了，模型看不见。这是浏览器 CUA 变"盲操作"的根因。
2. 策略面薄：有导航策略，没有**互联网出口策略位**（对标 Qoder `browser-internet-policy:*` 3 频道）与危险动作清单。
3. 无录制回放（对标 Qoder computer-control 扩展的 record-and-replay）。
4. 桌面级（浏览器外）CUA 完全没有——依赖视觉输入，排远期。

**竞品格局**：Qoder 三层全做（含治理频道）；Codex 终端系全无；ChatGPT/Claude 助手系有但无治理证据链。本仓差异化=**治理 DNA 上的 CUA**（逐次审批+硬停+审计）。

## 2. 目标 / 非目标

**目标**：①模型看得见截图（视觉输入修复）；②浏览器层治理收口（出口策略位+危险动作清单+审批档位）；③录制回放（冻结脚本+重放审批）；④（远期）桌面级 CUA。
**非目标**：不重写 CDP 工具族；不做 MCP 外接 CUA 服务器（操作面已自建，外接反而双轨——调研阶段"MCP 试水"的建议被"已建成"的事实取代）；不做移动端。

## 3. 设计方案

### 3.1 C1：视觉输入修复（硬前置，独立批）
- provider 协议层加 image part 通道：附件/工具结果中的图像进入消息体（影响 provider 抽象与全部既有 ScriptedChatClient 断言，**影响面大，先做协议评审再动手**）。
- 验收：真 provider 冒烟（截图→模型描述内容）；Scripted 层用字节钉 image part 形状。

### 3.2 C2：浏览器层治理收口
- 出口策略位（对标 browser-internet-policy）：`BrowserNavigationPolicy` 扩展为域白名单/黑名单+策略结论进审批卡片（"本次导航离开白名单"是审批理由的一部分）。
- 危险动作清单：`HardStopCodes` 里已有 download/file-chooser/dialog——补**审批语义**：这三类发生时不是静默硬停，而是转逐次审批（用户可在设置里给特定站点放行）。
- 审批档位：默认浏览器动作逐次审；批量动作（`BatchAsync`）按步骤拆审批或在卡片里逐项列出。
- 审计：动作+URL+ref+策略结论入既有事件流（审批证据投影复用）。

### 3.3 C3：录制回放
- 录制=把一段人工/智能体操作序列冻结为带 ref 的脚本（ContentStore 存储）；回放=脚本作为一次治理执行，逐动作过审批，页面结构漂移即停（ref 失效机制现成）。

### 3.4 C4（远期）：桌面级 CUA
- Win32 screenshot/click/type，仓内 P/Invoke 封装现成（`Win32ProcessApi` 等）；前置=C1；治理档：默认仅"焦点窗口+白名单应用"，敏感窗口（密码管理器/UAC）拒绝清单。**单独立项评审，不进本计划批次。**

## 4. 分批落地与验收

| 批 | 内容 | 验收门禁（先红后绿/变异） |
|---|---|---|
| C1 | 视觉输入修复（协议评审→image part→Scripted/真 provider 双轨断言） | 真 provider：截图→模型正确描述页面；变异：摘 image part→恰红 |
| C2 | 治理收口（出口策略/危险动作审批化/批量审批/审计） | 变异：把 download 硬停改回静默→恰红；live：白名单外导航出现在审批卡片 |
| C3 | 录制回放 | 回放中页面结构漂移→停并给结构化原因；重放绕过审批→红 |
| C4 | 桌面级 CUA | 另立计划书 |

**live 验收硬门禁**：真实多步浏览器任务（登录态外的表单填写+提交）在逐次审批下完成一次。

## 5. 风险与开放问题

1. C1 动 provider 协议：全链 DTO/快照/测试同批，是四份差距计划书里影响面最宽的单批——建议放最后或最先（不夹中间）。
2. CDP 依赖 Electron bridge：远程设备（计划书⑤）接管浏览器时，bridge 在宿主侧——语义成立，但要写清楚"远程设备的浏览器动作也走宿主审批"。
3. 开放问题：截图进上下文的 token 成本（一张截图≈千级 token）——进入 3.1 的协议评审议题，配"仅审批卡片显示缩略图、模型按需取图"的降级选项。

## 6. 与其他计划书的关系

- C1 与既有报告 §4.3 同源——本计划把它并入 CUA 叙事；也可独立先行。
- C2 与计划书③（沙箱网络档）共用"治理承诺不超实现"的措辞纪律。
- 与计划书⑤的会话接管共享审计事件流。
