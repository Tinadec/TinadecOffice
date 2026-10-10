# Home / 会话、对话与投递：TODO

模块ID：`APP-HOME` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本文件是该模块任务的唯一编辑入口；总TODO由本文件生成。任务ID长期稳定，完成或取消后保留记录；每项任务记录工作范围与验收条件。

状态：待核查 / 未开始 / 待方案 / 进行中 / 阻塞 / 待验收 / 已完成 / 不做 / 已被替代。优先级是初始建议，可在逐模块分析后调整。

<a id="app-home-107"></a>

### APP-HOME-107 侧边栏与多文件夹工作区

- 类型：实现
- 状态：待验收
- 优先级：P1
- 主责模块：APP-HOME
- 前置依赖：复用 StorageScopeRegistry 独立作用域、TOML、受管 HTTP、现有迁移与工具沙箱；复用 shadcn-vue/Reka 及 UIE NavCard，不建立第二套业务存储
- 关联功能：APP-HOME-F007；CORE-PERSISTENCE / CORE-TOOLS / TOOLS-SANDBOX / APP-LOCAL-STATE / APP-RENDERER 交叉引用此任务
- 契约与证据：[正式工作区契约](../../../../workspaces.zh-CN.md)、[源码对照及验收报告](../../../../../.tinadec_dev/reports/2026-10-10-sidebar-workspaces.zh-CN.md)

**问题、目标与触及范围**

原侧边栏将项目折叠、选择与新会话上下文混在一起，单目录项目不能表达多个材料目录。本任务定义自由对话作为可排序的普通列表项（无手动顺序时默认在前）、工作区标题、纯折叠行、近期五条和当前旧会话、统一手动排序，并将创建/打开/编辑统一到多文件夹窗口。源目录授权由宿主维护；新运行冻结目录集合，主目录可切换但存储锚点和旧运行保持不变。实施顺序为模型与授权、初始化/接口、控制器、侧边栏/窗口、真实验收与文档。

**验收条件**

- [x] 工作区标题与各行折叠、悬停/聚焦控件、加号与菜单隔离、近期五条及旧会话、显示全部、手动排序与本机恢复有组件回归。
- [x] 工作区独立加载失败不阻断其他工作区；新聊天沿当前上下文，发送框切换进入新对话，不静默迁移旧会话。
- [x] 多目录原生选择、主目录与名称、重复目录、最后/主目录移除、明确打开已有工作区、取消及单次提交有实现与定向验证。
- [x] project.toml 条件保存、注释保留、原子替换、并发和取消，主目录更换不改变存储位置，新旧工具上下文隔离有 API/SQLite 回归。
- [x] Gateway 薄代理、OpenAPI 快照、生成 Desktop 类型与原生 IPC 路径数组同步。
- [x] Windows Electron 实际组件连接真实隔离 Core/Gateway，原生多选、创建/编辑/打开、刷新恢复通过；工具子进程跨目录文件读写、搜索及越界拒绝通过。
- [x] Linux Bubblewrap 0.13.0 / Landlock 实际内核目录隔离通过；两源目录位于隐式临时授权之外，Linux 工具专项 28/28。
- [ ] macOS 实际内核目录隔离、Windows 低权限账号 Shell 全链分别取得证据，不把通用进程或平台返回型用例当沙箱通过。
- [x] PostgreSQL 18.6 / pgvector 实库在空库和已有扩展命名空间两种环境通过；多作用域事实/向量隔离、主目录切换、重启、配置精度和扩展删除保护均实际运行。
- [x] 最终源码/测试/文档闭环及平台边界写入报告并运行 reindex。

**当前边界**

实施、Windows 局部交互、Linux 内核及 PostgreSQL 实库验证已经完成；本机不能执行 macOS Seatbelt/桌面验收，CI 尚未运行。Windows 低权限账号初始化需要系统授权，自动化未接受安全确认；普通用户进程命令/Git 测试不替代该边界，任务保留待验收。完整 App/真实模型与安装包不因隔离组件夹具完成。APP-HOME-001 的整体模块审计继续独立。

**2026-10-10 接口回归跟进**

同一任务补齐宿主认证恢复、Core/Gateway共同就绪、显式scope优先级、跨作用域历史读取、任意生命周期挂载与坏登记隔离。保持用户数据、默认选择、包版本和摘要。错误契约：[产品文档](../../../../error-contract.zh-CN.md)；源码、真实/模拟验收分界和分组提交：[接口回归报告](../../../../../.tinadec_dev/reports/2026-10-10-interface-regression.zh-CN.md)。本轮 Windows 隔离证据与编译结果逐项登记，完整产品/平台仍保持上述待验收范围。

**2026-10-10 宿主 IPC 跟进**

旧 main 与新 preload/renderer 混用的缺 handler 错误已纳入本地协议失败处理：业务撤权、停止无效重试、共享 Banner 恢复和 Home 通知去重。DEV 明确整链 npm run dev，生产保留既有可信重启入口；迟到状态/手动重试不覆盖较新撤权。[现场、回归与 Electron 契约证据](../../../../../.tinadec_dev/reports/2026-10-10-host-ipc-mismatch.zh-CN.md) 独立记录，本任务继续待验收，不重置用户数据或扩大平台完成声明。

**2026-10-11 智能体目录与连接读取跟进**

已确认当前配置非空，而旧/agents逐节点预览导致Core超过90s、Gateway网络失败；设置首次读取失败又误显示空目录。按同一任务实施配置摘要复用、作用域内批量预览、共享JSON只读网络重试，以及Settings取消/单飞/恢复/草稿保护。HTTP、配置、安装语义保持不变；写与普通重连不重放。[本轮实测与边界](../../../../../.tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md)单列，不沿用上轮IPC验收，也不改变本任务完整平台待验收状态。

<a id="app-home-106"></a>

### APP-HOME-106 项目选择器长列表与模式、权限展开指示

- 类型：实现
- 状态：已完成
- 优先级：P1
- 主责模块：APP-HOME
- 前置依赖：复用现有 ComposerBar / ComposerCommandPanel 与共享主题样式；APP-HOME-104已完成
- 关联功能：APP-HOME-F006
- 完成证据：[统一报告](../../../../../.tinadec_dev/reports/2026-10-09-ui-comments-2.zh-CN.md)记录2026-10-09 Windows主页面与45项SFC夹具实测、12文件236项无skip与类型检查通过；[项目选择器实现](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/comment-project-picker.md)、[展开指示器与74项回归](../../../../../.tinadec_dev/evidence/2026-10-09-ui-comments-2/composer-arrows.md)

**目标与边界**

修复 Composer 项目菜单最大高度和 UiScrollArea 内层 h-full 关系导致的列表裁切，以一个原生列表承担实际滚动，隐藏滚动条而不影响完整列表和键盘访问。模式、权限、项目箭头需要表达真实展开状态；模式/权限由命令面板实际页而非 initialPage 决定，项目由 showProjectDropdown 决定，指示器和 aria-expanded 使用同一状态。局部160ms变换过渡支持 reduced-motion。

本任务及APP-HOME-F006专项已完成验收。45个伪项目只进入隔离SFC预览夹具，禁止真实 API，不登记用户项目。通用 UiScrollArea、完整 Home 审计、真实消息投递及欢迎页附件分别保持各自范围，不继承本次完成状态。

**验收条件**

- [x] 项目列表只保留一个原生纵向滚动容器，max-height根据实际可用空间限制；隐藏滚动条，项目行内边距与圆角使用共享样式。
- [x] 完整45项可渲染与选择末项，Arrow/Home/End到达列表和末尾新建入口；Escape/选择恢复焦点，快速连续开合不聚焦已移除菜单。
- [x] 模式/权限指示与 aria-expanded 同读 commandPanelOpen + 实际 page；同入口复点关闭、跨入口切页、返回根页和 Escape关闭均正确。独立旧 Selector 同读自身 showDropdown。
- [x] 项目指示与 aria-expanded 同读 showProjectDropdown；三入口展开旋转180°、关闭复位，160ms transform过渡与 reduced-motion禁用规则已实现。
- [x] ComposerBar73项和PermissionSelector1项共74项定向通过；重复运行同一文件不累计测试数，隔离夹具不登记真实项目。
- [x] Windows1169×719主页面验证自由对话标签、8px圆角/4px内距、模式与权限实际180°旋转及切页复位；45项真实SFC夹具滚轮delta1100使列表scrollTop1100、portal不滚动，键盘到达末项/自由对话/新建入口，重新打开当前末项可见。
- [x] 350px窄容器与工具栏clientWidth等于scrollWidth（350/334），菜单在视口内、页脚固定；滚动条两引擎隐藏规则和三箭头reduced-motion transition:none及方向正确，测试后恢复正常模式。

**功能验收边界**

上述勾选覆盖普通Windows浏览器UI、鼠标滚轮、键盘与隔离SFC预览。夹具list clientHeight238、scrollHeight1537，重新打开第45项时scrollTop约1298.86，选择/新建只执行夹具回调。该结论不包含触屏硬件、Linux/macOS、完整Home或真实项目登记；最终生产构建和其余UI专项结果由统一报告记录。

<a id="app-home-104"></a>

### APP-HOME-104 统一输入框命令面板与会话运行设置

- 类型：实现
- 状态：已完成
- 优先级：P1
- 主责模块：APP-HOME
- 前置依赖：复用Core会话/运行契约与Gateway；空间组合关联APP-RENDERER-104
- 关联功能：APP-HOME-F005
- 完成证据：[实施记录](../../../../../.tinadec_dev/reports/2026-10-08-command-panel.zh-CN.md)

**目标与边界**

平面预设与空间自定义编排共用输入框上方命令面板；+与/同入口，附件顶部，真实目录与模型/权限/模式选择。所有空间使用一套新契约，无旧运行方式兼容分支。配置保存在Core，发送与队列捕获其选择，已运行任务保持冻结。

**验收条件**

- [x] 共享面板、搜索/子页/键盘/IME/草稿和附件，以及真实目录选择的组件回归通过。
- [x] Core配置原子保存、版本冲突、恢复默认、平面/空间边界、SQLite结构与队列解析通过。
- [x] 控制器会话隔离、设置并发、首发命名修订、队列跨会话归属与全局入口回归通过。
- [x] 实际Panel组件三宽度、360×600视口、明暗/模糊材质和键盘Electron夹具通过。
- [x] 2026-10-08修订：所有选择器图标、权限风险颜色、紧凑间距、切页动画、Enter自动关闭和捕获阶段外部关闭通过109项定向与真实浏览器验证；[修复记录](../../../../../.tinadec_dev/reports/2026-10-08-command-panel-polish.zh-CN.md)。
- [x] 同入口复点收起、不同入口切页、空搜索Backspace/Delete返回与长按保护、slash返回编辑器通过115项定向、类型和浏览器检查；[导航修复](../../../../../.tinadec_dev/reports/2026-10-08-command-navigation.zh-CN.md)。统一命令语言另见[讨论稿](../../../../../.tinadec_dev/research/2026-10-08-slash-command-language.zh-CN.md)，不将建议语法当已实现。
- [x] 最终运行组合14项脚本API、审批全文、Desktop1024/14 skipped、native107、Gateway80及类型/构建通过；外部模型/平台边界见实施报告。

<a id="app-home-103"></a>

### APP-HOME-103 修复 Markdown 显示并应用岛屿卡片视觉

- 类型：实现
- 状态：已完成
- 优先级：P1
- 主责模块：APP-HOME
- 前置依赖：无；复用现有 MarkdownRender、DOMPurify 与 UiIslandCard
- 关联功能：APP-HOME-F004
- 完成证据：[修复报告与验证](../../../../../.tinadec_dev/reports/2026-10-07-markdown-islands.zh-CN.md)；Desktop979/14 skipped、native/scripts107、类型/构建与三宽度本地Electron SFC夹具通过，非真实模型/完整App E2E

**问题与目标**

已复现列表标记缺失、任务复选框尺寸异常、表格对齐失效、宽表格及长URL撑宽消息区。恢复正确显示，并让代码、表格、引用使用现有岛屿卡片，正文连续、对话列透明；历史与流式共用渲染和消毒，不增加扩展语法依赖。

**验收条件**

- [x] 列表保留嵌套及有序起始编号，任务checkbox为小尺寸只读显示。
- [x] 表格对齐正确，表格/代码独立横滚，320/520/800px正文和长URL不撑宽消息区。
- [x] 使用UiIslandCard及既有材质token，亮/暗主题可读，无嵌套材质根。
- [x] 引用链接、嵌套HTML、图片与不完整围栏保留；完整文档继续经过DOMPurify。
- [x] 后续流式文字更新不重建已完成代码/表格块，表格可键盘聚焦。
- [x] 组件回归、类型/构建与本地Electron视觉证据通过；真实模型/平台验收边界明确。

<a id="app-home-105"></a>

### APP-HOME-105 对话流 Markdown 扩展语法与卡片细节

- 类型：实现
- 状态：已完成
- 优先级：P1
- 主责模块：APP-HOME
- 前置依赖：无；复用现有 MarkdownRender、UiIslandCard 与主题 token；新增 highlight.js、katex、marked-katex-extension、marked-footnote、mermaid
- 关联功能：APP-HOME-F004
- 完成证据：[扩展语法报告](../../../../../.tinadec_dev/reports/2026-10-08-markdown-extended-syntax.zh-CN.md)；Electron 夹具 passed（真实剪贴板、MathML、Mermaid、三宽度）、定向 23 项、Desktop 全量 1028 passed/14 skipped、类型检查通过；生产 vite build 本轮未取得证据

**问题与目标**

上一轮把正文分块为内容岛屿，但代码无高亮与复制、表格卡片细节不足，公式/脚注/提示块/标题锚点/Mermaid 均未接通。用户要求视觉观感优先且不为此拆分多层：全部逻辑收在 `MarkdownRender.vue`，样式集中在 `styles.css`，仅图表新增一个异步子组件。

**验收条件**

- [x] 代码岛有语言标签与悬停/聚焦显现的复制按钮，复制真实写入系统剪贴板，失败在按钮上回报。
- [x] 高亮按需注册语言并记忆化，流式重解析不重复计算；未知语言静默退化为纯文本。
- [x] 行内与块级公式渲染，display 公式自带横向滚动；MathML 分支与内联 style 在消毒后保留。
- [x] 脚注渲染且脚注区标题、返回引用读屏文案本地化。
- [x] `> [!NOTE/TIP/IMPORTANT/WARNING/CAUTION]` 渲染为分类型提示块（支持自定义标题），普通引用不受影响。
- [x] 标题得到 md- 前缀去重 id 与锚点；点击在组件内滚动并标记落点，不改写 URL hash。
- [x] Mermaid 懒加载、跟随主题重绘，解析失败回退显示源码；超长图表不交给 mermaid。
- [x] 表格容器满宽、表头加深、行 hover、末行去边；三宽度下正文视口无横向溢出，表格/代码/公式在卡片内滚动。
- [x] 流式更新不重建已完成代码块 DOM 与表格滚动容器（含焦点）；亮暗主题与强调色跟随。

**边界**

外链打开（`will-navigate`/`openExternal`）按用户本轮决定不做；Mermaid 未 Worker 化，性能未压测；生产构建证据缺失见报告第 4 节。

<a id="app-home-001"></a>

### APP-HOME-001 完成 Home / 会话、对话与投递 的逐功能审计与模块图精化

- 类型：核查
- 状态：待核查
- 优先级：P2
- 主责模块：APP-HOME
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

- [apps/desktop/src/controllers/HomeController.ts:22](../../../../../apps/desktop/src/controllers/HomeController.ts#L22)

<a id="app-home-101"></a>

### APP-HOME-101 验收真实消息投递、并行活动与监督决策

- 类型：验收
- 状态：未开始
- 优先级：P1
- 主责模块：APP-HOME
- 前置依赖：未细化；开工前在此列出实际Task ID，不能把全部关联模块当硬依赖
- 关联功能：待逐功能拆分后绑定本模块Feature ID
- 完成证据：未产生；本轮仅建立任务与源码基线

**问题与目的**

源码与组件证据不能替代真实模型下 queued/parallel/insert、审批和监督的产品闭环。

**验收条件**

- [ ] queued 晋升后保留发送时模式、模型、权限及附件
- [ ] 两个 run 同时活动时显示归属正确；切会话迟到事件不覆盖当前会话
- [ ] insert 中断在途请求后读取纠正文本；首轮可查看审批事实并裁决
- [ ] 监督 continue/correct/cancel 均到达对应 Core 状态并清理已回答入口

**初始证据**

- [apps/desktop/src/controllers/HomeController.ts](../../../../../apps/desktop/src/controllers/HomeController.ts)
- [apps/desktop/src/components/chat/LiveTurnBlock.vue](../../../../../apps/desktop/src/components/chat/LiveTurnBlock.vue)

**历史任务映射**

- [docs/whole-product-eval-2026-10-05.zh-CN.md](../../../../whole-product-eval-2026-10-05.zh-CN.md)：模型中心之外的只读逻辑审查；只作来源，不继承完成勾选

<a id="app-home-102"></a>

### APP-HOME-102 复验首次会话欢迎页附件可用性

- 类型：核查
- 状态：待核查
- 优先级：P1
- 主责模块：APP-HOME
- 前置依赖：未细化；开工前在此列出实际Task ID，不能把全部关联模块当硬依赖
- 关联功能：待逐功能拆分后绑定本模块Feature ID
- 完成证据：未产生；本轮仅建立任务与源码基线

**问题与目的**

历史正式 eval 观察到 welcome 附件置灰；本轮未复现，不能直接声明当前缺陷仍存在。

**验收条件**

- [ ] 在新会话首次发送前确认附件入口的可用条件
- [ ] 记录上传、发送与失败反馈；若复现则保存当前版本及实际错误证据再进入修复

**初始证据**

- [apps/desktop/src/components/WelcomeScreen.vue](../../../../../apps/desktop/src/components/WelcomeScreen.vue)
- [apps/desktop/src/controllers/HomeController.ts](../../../../../apps/desktop/src/controllers/HomeController.ts)

**历史任务映射**

- [docs/whole-product-eval-2026-10-05.zh-CN.md](../../../../whole-product-eval-2026-10-05.zh-CN.md)：直接观察与代码解释；只作来源，不继承完成勾选
