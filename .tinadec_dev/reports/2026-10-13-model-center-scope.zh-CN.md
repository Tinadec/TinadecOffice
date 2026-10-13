# 模型中心、配置、存储作用域与命令面板：模型“打架”调查与第一轮修复

2026-10-13 · 分支 `fix/model-center-unify`（基于 `main` ab42b02）· 任务 [APP-SETTINGS-105](../../docs/development-program/02-modules/app/settings/TODO.md#app-settings-105)

## 反馈

> 模型中心配置的模型不能使用，但是打开工作区发现：1. 命令面板显示没有这个模型；2. 甚至显示的模型还是我已经删掉的那个模型；3. 命令面板里其实不应该有“模型设置”这个功能。前端在打架，后端也在打架。

涉及模块：模型中心、配置、设置里的存储、输入框 `/` 命令面板。

## 结论

不是单个 bug，是同一份“当前对话用哪个模型”被四处各自持有，且彼此之间没有任何提示或同步：

| 持有者 | 存在哪 | 谁写 | 谁读 |
| --- | --- | --- | --- |
| 用户模型配置 | user 作用域 `~/.tinadec/config/models.toml`（SQL 是它的投影） | 模型中心（`SettingsPage` 固定 `scopedApi(…, () => 'user')`） | 没开工作区时的会话 |
| 工作区模型配置 | 项目作用域 `.tinadec/config/models.toml` | 创建工作区时从 user **复制一次**（`StorageScopeInitializer.PublishAsync`）；之后只有「存储与配置」里的原文 TOML 编辑 | 该工作区里的会话、命令面板（请求带 `x-tinadec-storage-id = selectedStorage`） |
| 会话固定模型 | 会话行 `meeting_model_override`（provider id + model） | 命令面板「对话模型」页 | run 冻结时优先级最高 |
| 新对话草稿设置 | 前端内存 `newSessionSettings[view]`，**不分作用域** | 命令面板 | 下一条新对话 |

按 [配置文件契约](../../docs/development-program/02-modules/core/Persistence/CONFIGURATION-FILES.md)，“项目初始化复制用户配置与资源后拥有独立来源；后续用户编辑不再覆盖该项目”——工作区独立是**设计**，不是缺陷；缺陷在于界面完全不体现这一点，再叠加几处真正的错误。

## 三个症状的成因

1. **模型中心配好的模型用不了 / 命令面板里没有它**：模型中心改的是 user 作用域；打开工作区后，会话与命令面板读的是工作区创建时复制的旧 `models.toml`，里面没有新模型，chat 路由仍指向旧的。
2. **显示的还是已删掉的模型**：
   - 删除发生在 user 作用域，工作区副本里那个供应商仍然在、仍可用；
   - 会话固定模型指向已删除的供应商时，输入框上方的模型标签直接显示保存的 `override.model`，从不检查供应商是否还在（`ComposerBar.vue`、`ComposerCommandPanel.vue` 的 `currentModelName`）；
   - 而 run 冻结时，固定模型优先级最高，供应商不存在就抛 `Provider '…' was not found`，run 失败——模型中心怎么配都绕不过去。
3. **命令面板里有“模型设置”**：「对话模型」页按会话覆盖模型，正是上表第三个持有者；它只列 provider 自带的 `models`，经由路由配置的模型不会出现。

附带发现：强制删除供应商（`DeleteProvider force=true`）只做软删除，引用它的路由候选、会话固定模型、智能体绑定都不清理；路由冻结时查供应商不过滤 `DeletedAt`，在调用阶段才判为 `provider_disabled`。草稿设置不分作用域，在 user 作用域选的 provider id 会被带进工作区作用域的新对话。

## 本轮已改（三个提交，均带测试）

1. **Core：固定模型指向不可用的供应商时回落默认路由**（`6a06e1e`）。`AgentModelResolver.FreezeAsync` 只有在供应商于当前作用域存在、未删除、已启用时才采用会话或本轮的固定模型，否则用默认策略（chat 路由）；准入时不再把这样的会话固定模型复制进 run 或回显。请求里**显式**给出的固定模型仍在准入时校验并以 `invalid_model_override` 拒绝。会话上保存的选择保留，交给界面显示。测试 `SessionModelOverride_OfADeletedProvider_FallsBackToTheDefaultRoute`（删除/未删除两例）；还原旧解析器时删除例以 `Provider '…' was not found` 失败，即反馈里的现象。
2. **Desktop：命令面板去掉「对话模型」**（`3c5a837`）。对话模型 = 模型中心的 chat 路由，不再从输入框按会话挑选。旧会话已固定的模型仍会显示在输入框上方：「固定模型 X · 改用默认」，一键清除；其供应商在当前作用域已删除、停用或不存在时显示「X 已不可用 · 改用默认」（Core 此时已走默认路由）；读不到供应商列表时不判为不可用。
3. **Desktop：模型中心说明工作区用的是哪份配置并可同步**（`a4b76ea`；第二轮改为“跟随/独立”切换，见下）。打开工作区时，模型中心在「用户默认配置 · user」下方说明“当前工作区的对话使用它自己的模型配置（创建时复制，之后不随这里变化）”，并比较两份 `models.toml` 是否一致；不一致时提供「用用户配置覆盖此工作区」：先确认（会丢掉该工作区独有的模型；共享项目会改项目文件），再在工作区校验，最后按被替换内容的摘要条件保存，Core 保存后立即重投影。

验证：Core 定向（新增 2 例 + 模型相关 StorageApi/AgentRuntimeBinding/AgentPackEndpoint 共 62 例）通过；Desktop `vue-tsc` 0 错误，全量 vitest 1304 通过 / 14 跳过（144 文件）。

## 第二轮：模型配置归用户，工作区直接使用同一个文件

用户把方案交给我们定。比较后选的是 A 的一种实现，而不是复制再同步：

- 凭据本来就在用户 SecretStore，各作用域共用同一个租户身份，工作区副本里的 provider id 与凭据引用和用户那份完全相同——复制没有带来任何隔离，只制造了一份会过期的旧数据。“打架”的根是复制本身。
- B（模型中心切换编辑哪个作用域）把“两份配置”的复杂度原样交给用户，不选。
- “自动同步副本”仍是两份数据、仍有同步时机与覆盖本地改动的问题，也不选。
- 做法：Core 读写配置文档都经 `ScopeConfigurationDocuments`。新增端口 `IConfigurationDocumentRedirects`，工作区挂载时若宿主登记 `follow_user_models = true`，就把 `models` 文档的路径、已建立标记、写锁和链接边界都指到 user 作用域。投影、条件保存、写回 TOML 全部复用原机制；模型中心改了，工作区下一次读取就投影到新内容（按摘要比对）。
- 不能用 `project.toml` 判断“团队共享项目”：每个工作区作用域初始化都会写它。所以按来源区分：本机新初始化的工作区默认跟随；已有工作区与带着自有 `.tinadec` 进入的项目保持独立，避免它们独有、已被调用记录引用的供应商在重投影时违反外键而让作用域打不开。
- 切换：`POST /api/v1/storage/scopes/{id}/model-source`。先关闭该作用域运行时（有运行中任务时 5 秒内未能关闭即 409），改登记，重新挂载；挂载失败（新来源无法投影）自动改回原来源再报错。工作区自己的 `models.toml` 不删除。
- 模型中心提示改为：工作区“使用这里的模型配置”（可改回工作区自己的）或“使用它自己的模型配置 · 是否一致”（可改为使用这里的，需确认）。上一轮的“用用户配置覆盖此工作区”按钮删除——跟随严格优于覆盖。

测试：`NewWorkspace_RunsOnTheUserModels_SoModelCenterEditsAndDeletionsReachIt`（新增与强制删除都传到工作区、`models` 文档路径就是用户文件、其他模块仍在工作区）、`Workspace_CanKeepItsOwnModels_AndFollowTheUserAgain_AcrossRestart`（切独立看不到用户新增、切回看到、重启后保留）、`UserScope_HasNoModelSourceToSwitch`；去掉挂载时的重定向注册，前两例失败。Gateway 代理与外部契约、Desktop 生成类型同步。

## 仍待决定或未做



- 已有工作区需要用户在模型中心点一次“改为使用这里的配置”；没有做自动迁移（见上）。
- 某个工作区确需不同默认模型时，可以以后加一层只覆盖 chat 路由的工作区设置，而不是整份复制。
- 强制删除供应商时清理引用它的路由候选并提示受影响的会话；草稿设置按作用域分开（或不再携带 provider id）。

## 未验证

Electron 实机与真实供应商调用未做（本机无 Electron/显示、无模型密钥）；同步按钮在 PostgreSQL 作用域与共享项目上的实际效果未走查；全局命令面板（Ctrl+K）中的模型条目未改动。
