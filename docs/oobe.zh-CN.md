# 首次引导与功能引导（OOBE）

状态：设计定稿，未实现。适用：2026-10-28 工作树 `main`。

本文定义产品行为与模块边界。凡标注"待确认"的，是没有源码证据、不能当成事实写进实现的判断。

## 为什么现在做，以及不做什么

仓库当前没有任何首次运行检测：`grep -i "oobe|onboarding|firstRun|hasSeen|setupComplete"` 在 `apps/` 下零命中。全新安装、删掉 `~/.tinadec`、迁移到新机器，三种情况的界面表现完全一样——splash 离场后直接进 `UieShell`，模型未配置只体现为发送后的一条错误（`HomeController.ts:1189`）、输入框里的"未设置默认模型"（`ComposerBar.vue:202-208`）和 Doctor 卡片里的一行 blocked。缺的不是提示，是把这些散落提示串成一条路的东西。

OOBE 不承担"教学产品"。它只做两件事：让用户拿到第一条成功的对话；让后来新增的能力有一个不打扰的送达通道。凡是不能落到这两件事上的文案，都不该进流程。

## 准入：判据是 readiness，不是"第一次运行"

引导的触发条件不是启动次数，而是 Core 判定当前环境不具备对话能力。`ReadinessService` 聚合十项固定条目（`TinadecCore/Runtime/ReadinessService.cs:52-56`）：`database`、`core_storage`、`agent_pack`、`default_mode`、`model_provider`、`model_secret`、`model_route`、`model_probe`、`tool_provider`、`manifest_hash`。每项自带 `Action`，干净环境本来就会指出下一步。

三件事因此成立。

准入必须等 `readiness` 第一次取回。启动链路是刻意设计成"只挡视觉、不挡后端"的（`App.vue:37-43`），业务读另由宿主信任闸把关（`lib/hostAccess.ts:37-80`，未 ready 一律 503）。若在 `onMounted` 就弹配置面板，用户会在 Gateway 与 Core 尚未握手时填写 API Key，然后保存失败——这是能想象到的最差首次体验。判定点定在 `businessReady && readiness 到手` 之后。

`AppSplash` 的离场契约不动。`entryReady` 由 home 页面自己 `emit('ready')`，`App.entry.test.ts:10-13` 固化了这个时序。OOBE 挂在其后，不插在 splash 与首页之间。

已经配好的用户什么都不弹。迁移、重装、清空 state 后重装都属此列。判据是 `model_route` 可解析、`model_secret` 可用、`model_probe` 非 failed 同时成立。这一条决定了 `skippedAt` 与"环境已就绪"必须是两个独立状态：前者是用户的选择，后者是事实。

### DevSeed 的占位配置不能当成已配置

`TinadecCore/Runtime/DevSeed.cs:17-60` 在缺 `chat` 路由时幂等创建一个 `openai` provider（`https://api.openai.com/v1` / `gpt-4o-mini`），并刻意不写 API Key。于是干净环境的表现是"有路由、没密钥"，而 readiness 的 `model_secret` 项正好会把这种情况判成 blocked（`ReadinessService.cs:430-459`）。

引导的第一屏不得把这条 provider 渲染成"你已经配好了 OpenAI"。判据统一用 `secret_reference` 为空：无密钥的 provider 在引导里等价于不存在，卡片走空态。

## 形式：三种原语，共享一套步骤数据

形式不在"全屏向导"和"浮层"之间二选一，因为三类内容的载体需求不同。

**Setup** 是居中模态，能填表单、执行动作、展示校验结果。填 API Key 与验证连通只能用它——浮层装不下逐字段的错误反馈和重试按钮。

**Spotlight** 压暗界面、高亮真实元素、旁边浮一张小卡。指向型内容离开真实位置就没有意义，"拖文件夹进来"必须指在真实输入框上。

**Announcement** 是角落卡片或通知岛，复用既有 `NotificationIslandHost`。绝大多数新功能不值得打断，一张卡就够。

三者共享同一个步骤抽象，所以新增引导是注册一条数据，不是改渲染器。

### 聚光灯在本仓库有额外风险

UIE 布局可拖拽且持久化到 `uie-layout.json`，`presets.ts` 只在无存档时构造（`useUie.ts:143-186`）。用户改过布局后，按坐标算出的高亮位置会指错。

因此聚光灯只能按 descriptor id 找真实 DOM，找不到目标就降级成 Announcement 卡，不做错位高亮。这需要给卡片根节点加 `data-uie-card-id` —— 这是整个 OOBE 唯一需要改动 `TinadecUI` 的地方。

## 首次流程

五步。步骤 id 固定为 `welcome`、`provider`、`verify`、`habits`、`firstTask`，用于断点续跑。

### welcome

说清楚要花多久，以及只有一步是必需的。这不是客套页：用户对"还要填几个表单"的预期直接决定他在第几分钟关掉窗口。同时暴露"随时可以退出"，退出后配置进度保留。

### provider

三张卡片选路：云服务商、本地（Ollama / LM Studio / vLLM）、自建兼容端点。选完原地展开表单，不跳页。预填来自 `providerTemplates.ts`。

表单字段是 `base_url`、`model`、`api_key`。保存走既有 `api.createModelProvider` / `saveModelProvider`（`SettingsPage.vue:2007-2045` 同一条链路），不另开后端路径。API Key 由 Core 写入 `ISecretStore`（`ControlPlaneService.cs:591`，引用名 `provider-<guid:N>`），不进配置 blob；这一步若自己写库就会绕开 `security/secrets` 与 `secrets.key` 的既有加密。

每次写必须带 `If-Match`。缺摘要 Core 返 428、摘要过期返 412（`ControlPlaneService.cs:74-80`），表现为"保存坏了"，必须在引导里也走同一套冲突处理。

### verify

唯一不给过的门槛。调 `POST /api/v1/model-probe?force=true`——它已经存在，语义就是"通过已解析的 chat 路由发一次真实的 1 token 请求，10 秒超时，60 秒结果缓存，`force=true` 绕过缓存"（`TinadecGateway/src/index.ts` 该路由的 description）。桌面端尚无封装，需在 `api.ts` 增加一个方法。

验证通过后才写 chat 路由（`api.saveModelRoute('chat', ...)`，`useDefaultModel.ts:98-103` 同源）。顺序不能反：先写路由再验证，失败时会留下一条指向坏密钥的默认路由，用户回到首页仍看到"已配置"。

失败要分类，不能一句"连接失败"：

| 现象 | 判据 | 修法 |
| --- | --- | --- |
| 密钥无效 | 401 / 403 | 回填 `api_key` 字段，保留其余输入 |
| 模型名不存在 | 404 | 聚焦 `model` 字段，给该 provider 的常见模型名 |
| 地址或网络不通 | 超时 / 连接拒绝 | 聚焦 `base_url`，提示本地服务是否已启动 |
| 额度或限流 | 429 | 不改表单，给"稍后重试"与"换一个模型"两个出口 |

`model_probe` 在 probe 未执行时为 `unavailable` 且不降级整份回执（`ReadinessService.cs` 类注释），所以"没验证过"与"验证失败"必须分开表达。

### habits

外观、语言、伙伴胶囊、宠物，全部可跳过。四项写在不同地方，不能合并成一个提交：主题与强调色在 `localStorage`（`useTheme.ts:36,211`），语言在 `tinadec-locale`（`i18n.ts:31`），伙伴模式在 `desktop.toml [companion] enabled`（`appConfig.cjs:186-196`），宠物在 `companion-avatar/registry.json`（`petStore.cjs:27-29`）。

宠物在本步不需要联网。`ensureBuiltinPets()` 首启已把 `deepseek` 与 `geptia` 复制到本地，均为 `enabled: false`（`petStore.cjs:355-422`）。这里只做"选一只并启用"，Petdex 目录下载留在设置页——把它写成"下载宠物"会凭空多一个失败点。

宠物窗口必须延迟创建。`petWindow.cjs:60-83` 是 `alwaysOnTop: true`，若本步一开就建窗，下一步的 Spotlight 会被压在下面。本步只写 registry 的 `enabled`，引导结束时统一 flush，走既有 `tinadec:pet-enabled` IPC（`main.cjs:533-537`）。

### firstTask

三个聚光灯加三个起手 prompt。

起手 prompt 必须只读、必然成功，例如"解释这个文件夹的结构""列出这里最值得先读的三个文件"。不要留空白输入框让用户自己想第一句：首次失败会直接终止留存，而第一次成功的对话是最好的产品说明。

聚光灯指向输入框、右侧功能面板、侧栏工作区列表。三处都可以单独跳过。

## 状态模型

只存一个 boolean 不够。首次引导需要完成时间与断点位置，功能引导需要逐条的完成、忽略、延后状态，否则"看过"和"跳过"混在一起，用户会被反复打扰。

```ts
interface OobeState {
  version: number
  firstRun: {
    completedAt?: string
    skippedAt?: string
    resumeStep?: string
  }
  tours: Record<string, { status: 'done' | 'dismissed' | 'snoozed'; at: string; until?: string }>
}
```

落盘为独立文件 `~/.tinadec/state/desktop/oobe.json`，与 `companion-avatar/registry.json` 同级（`storagePaths.cjs:8-37` 定义的 `paths.desktopState`）。

不写进 `desktop.toml`：那是手写 TOML、字段扁平（`appConfig.cjs:35-50`），塞嵌套结构会污染既有解析路径。不进 `localStorage`：清缓存会丢，且未来伙伴窗口若要读同一状态会拿不到。

主进程只加两个 IPC（`oobe:state-read`、`oobe:state-write`），沿用既有可信 sender 校验（`main.cjs:421-423` 的模式）。渲染层不直接碰文件。

### 跳过之后不能丢掉未完成

`skippedAt` 存在意味着不再自动弹 OOBE，但不意味着放弃。首页在 `businessReady` 成立且 `model_route`/`model_secret` 未就绪时，于输入框上方显示一条常驻细条："还没有可用的模型 · 继续配置"，点击 resume 到 `provider` 步。

Doctor 卡片里 blocked 项的 `Action` 指向同一入口。两处共用一个 resume 动作，避免出现两条互不相认的路径。

设置页常驻一个"重新播放引导"，删除对应状态即可，不需要额外开关。

## 后续功能引导：状态驱动，不是版本驱动

按"v0.5 新功能"写死会漏掉跳版本的用户，也会让回退版本的用户重看。触发条件应当是该能力**当前的可达状态**：

| 条目 | 触发条件 | 原语 |
| --- | --- | --- |
| 宠物 | registry 非空且全部 `enabled === false` | Announcement |
| 伙伴胶囊 | `desktop.toml [companion] enabled === false` 且已完成会话数 ≥ 3 | Announcement |
| 智能体模式 | 存在多个已发布 ModeVersion 且当前会话一直用默认 | Spotlight |
| 工作区卡片 | 从未添加过第二个源文件夹 | Spotlight |

版本号只用于"最多显示一次"和赛季分组，不参与唯一判断。每条写入 `tours[id]`，`dismissed` 是终态，`snoozed` 带 `until`。

## 关于"配置智能体模式"的一个纠正

首次流程里不应出现模式选择器。

桌面端不保存"当前模式"，选择跟着会话走（`HomeController.ts:96-97`）。`ModeSelector.vue` 与 `PermissionSelector.vue` 当前是孤儿组件，除自身实现外无人 import；真实入口在 `ComposerBar.vue:888-897` 的命令面板。Core 侧若无已发布默认模式，发消息直接 `409 agent_mode_not_configured`（`InteractionsEndpoints.cs:290-292`）。

因此这件事的正确形态是 readiness 里 `default_mode` 那条检查——缺了就由 Core 侧保证有一个可用默认，而不是让新用户在第 3 步面对一个他还无法判断的下拉框。真正需要引导"模式"的时机是他第一次想换一种工作方式，属于上表的状态驱动条目。

## 模块边界

```
apps/desktop/src/oobe/
  state.ts       持久化与版本迁移
  engine.ts      步骤机（next / back / skip / resume），纯函数
  registry.ts    首次流程与 tour 条目的声明
  readiness.ts   把十项 readiness 译成"缺什么"
  primitives/    SetupModal.vue / Spotlight.vue / Announcement.vue
  steps/         welcome / provider / verify / habits / firstTask
  index.ts       对外只暴露 start / resume / register
```

不碰 Core，不碰 Gateway，不进 UIE 卡片系统。卡片系统是常驻可拖拽的工作台，OOBE 是瞬时浮层；混进去会迫使 `repair.ts` 的布局修复逻辑理解"引导"这个它不该知道的概念（`repair.ts:59-61,88` 会丢弃未知 descriptor）。

唯一的外部改动是给卡片根节点加 `data-uie-card-id`。

`engine.ts` 必须是纯函数并单独可测：步骤顺序、跳过语义、断点续跑、`tours` 与 `firstRun` 的相互独立，这些都可以在没有 Electron 的环境里断言。

## i18n

五个语言：`en`、`ja`、`ru`、`zh-CN`、`zh-TW`，`i18nParity.test.ts` 强制齐全。

OOBE 文案独立命名空间 `oobe.*`，不塞进 `settings.*` 或 `common.*`。理由是引导文案改动频率远高于设置文案，混在一起会让每次改文案都触发设置页的键对齐检查。

分片：`oobe.welcome.*`、`oobe.provider.*`（含四个失败分类）、`oobe.verify.*`、`oobe.habits.*`、`oobe.tour.*`。

## 验证

状态机（`engine.ts`）用 vitest 单测覆盖：顺序推进、任意步跳过、中途退出后 resume 回原步、`tours` 写入不改 `firstRun`。

准入判定需要三组用例：全就绪 → 不弹；有路由无密钥（DevSeed 场景）→ 弹且 provider 卡片为空态；密钥无效 → 弹且直达 `provider` 步。

`api.probeModel` 需要一条契约测试，确认 `force=true` 真的绕过缓存。

真实链路（Gateway + Core + 桌面端）首次启动的端到端验证本轮不做，本机无可用 Core；文档不把未验证写成通过。聚光灯在用户自定义布局下的降级行为，需要在改过布局后再验一次。

## 本轮不做

不做账号注册引导。登录是可选能力（`PersonalSection.vue:309-336`），不影响能否对话，放进首次流程只会增加放弃率。

不做"引导视频"或动画教程。五步里的每一步都必须产出一个可验证的状态变化，幻灯片不产出任何状态。

不做多语言之外的本地化形态差异，五份文案走同一套版式。

## 分期

第一期：`state.ts` + `engine.ts` + 两个 IPC + `welcome` / `provider` / `verify` 三屏。目标是干净环境能配通并拿到第一条回复。

第二期：`habits` / `firstTask` + Spotlight 原语与 `data-uie-card-id` + 宠物延迟建窗。

第三期：`tours` 状态驱动的功能引导，先接宠物与伙伴胶囊两条。
