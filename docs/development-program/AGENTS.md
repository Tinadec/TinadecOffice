# DEVELOPMENT PROGRAM MEMORY

## 2026-10-10 接口回归维护

审计基线：a8374926；当前验证提交以以下元数据为准。

沿用APP-HOME-107唯一任务，宿主启动、Core逐项挂载、Gateway错误投影与Desktop跨作用域读取分别交叉引用。证据与实际/模拟边界见 .tinadec_dev/reports/2026-10-10-interface-regression.zh-CN.md。目录、用户配置和测试包均保留；未宣称全平台完成。

**Generated:** 2026-10-05
**Last Updated:** 2026-10-11
**Last Updated By:** APP-HOME-107 智能体目录性能、读取恢复及配置缓存契约；任务入口不复制。
**Last Verified Commit:** df0977ff（本轮全部代码）；本轮证据、实际/夹具边界及测试对账见 .tinadec_dev/reports/2026-10-11-settings-recovery.zh-CN.md。
**Branch:** main

### 2026-10-11 设置目录读取恢复

继续在APP-HOME-107记录接口回归跟进，Settings、Renderer、Persistence、AgentConfiguration和Runtime交叉引用。报告分别记录实际用户TOML副本的HTTP时延/目录数量、定向测试、Electron夹具及构建；当前用户服务未重启不等于采用新DLL。不得引用上轮IPC证据替代本轮连接/智能体验收，不重置用户数据。

### 2026-10-10 工作区交互与多目录

APP-HOME-107/F007统一持有侧边栏与多文件夹窗口、project.toml/宿主授权/冻结目录集合和快照的任务与状态。Home架构图及Renderer、本机状态、Persistence、Tools、沙箱、文件/搜索、Git和终端模块README交叉引用，不复制任务清单。Windows原生选择/实际组件/Core+Gateway及真实Tools文件证据已取得，Linux/PG本轮与未验收平台分别列在报告；macOS或Windows低权限链没有证据时不折算完成。正式契约docs/workspaces.zh-CN.md，源码对照与证据报告`.tinadec_dev/reports/2026-10-10-sidebar-workspaces.zh-CN.md`。reindex生成总入口，保留已有专项状态。

### 2026-10-09 搜索材质

APP-RENDERER-107/F006仅验收搜索dialog接入全局材质、原生6px模糊遮罩、材质参数/全屏与关闭恢复。50定向和类型、Windows当前浏览器证据保存在搜索材质报告；模块001整体审计、全产品构建/安装器和Linux/macOS独立。任务与功能仍仅在Renderer模块TODO/STATUS维护。

### 2026-10-09 第二批 UI 标注

项目列表/展开指示归APP-HOME-106/F006，空间导航与直接路由归APP-RENDERER-106/F005；关于开关及本机配置归APP-SETTINGS-104/F004，Debug默认关闭/宿主入口归APP-DEBUG-102/F002。每项只在模块TODO/STATUS记账。236定向与23宿主、类型、实际Windows浏览器及45伪项目夹具见统一报告；专用Debug后端、整体空间/Home/Settings审计、原生窗口和安装包/三平台保持独立。

### 2026-10-09 五条 UI 标注

窗格动作归APP-UIE-COMPONENTS-101/F003，通知/预览链接/模式菜单归APP-RENDERER-105/F004；各模块TODO/STATUS唯一记账。85pass/14已有skip、类型与独立Vite构建、Windows实页键盘/拖拽/动画/Pin/窄URL通过。源、固定同类参考与截图归 `.tinadec_dev/reports/2026-10-09-ui-comments.zh-CN.md`；001整体审计及全平台产品验收不据此完成。

### 2026-10-09 GraphSeedPack 与配置校验

APP-PACKS-102记录Gateway/桌面结构化诊断、失败同步和显式重试；CORE-PERSISTENCE-101记录EF过滤唯一索引与活动提示词版本来源校验。API测试早期隔离实现和真实宿主回归证据集中于同一GraphSeed修复报告/evidence。用户测试包和真实配置保留，无法确定历史污染是哪次测试造成。X-DATA-104、APP-PACKS-001/101保持各自未完范围，不因本次缺陷验收整体勾选。

### 2026-10-08 工具配置专项

共享默认与持久Agent定义覆盖、九标签设置、MCP/Skills资源绑定、严格JSON显式保存和准入冻结已经接线。状态与任务分别更新APP-SETTINGS、CORE-TOOLS、CORE-SKILLS、TOOLS-PROTOCOL、TOOLS-MCP；受影响执行模块README同步。证据集中于共享报告和evidence，整体001审计任务保持独立，不因专项完成自动验收整模块或三平台。

### 2026-10-08 Markdown 扩展语法与卡片收口

新增APP-HOME-105（关联APP-HOME-F004）完成扩展语法：代码高亮与块级复制、KaTeX 公式、脚注、分类型提示块、标题锚点与 Mermaid 图表。表格/代码/引用/提示块/图表共用既有 UiIslandCard，行内代码与卡片细节按同级项目（OpenCodeUI / openchamber / hermes-agent）收敛。按用户要求不拆层：解析、分块、高亮与块级行为都在 MarkdownRender.vue，样式在 styles.css，只新增异步的 MarkdownDiagram.vue。锚点点击在组件内滚动且不改写 URL hash（应用是 hash 路由）。命令面板批次已占用 APP-HOME-104，本批改号为 105。边界：外链打开按用户决定不做；Mermaid 未 Worker 化、性能未压测；生产 vite build 本轮未取证。证据与验收留在模块TODO/STATUS、共享报告与 evidence，不另建状态库。

### 2026-10-08 命令面板实施

新增APP-HOME-F005/104，关联APP-RENDERER-104。用户要求统一新契约，不做旧空间执行方式兼容；UI、Core会话配置与运行组合共用同一用户选项。任务验收留在模块TODO，证据在共享报告与evidence，不另建状态库。完整空间工作画面/外部模型/平台等后续目标不因本次命令功能完成而整体勾选。

### 2026-10-07 Markdown 内容岛屿

APP-HOME-F004/103完成本地正文显示专项：正文连续，代码/表格/引用复用UiIslandCard；修复列表标记、checkbox尺寸、表格对齐、宽表格及长URL溢出。整篇消毒后分块，后续流式文字不重建已完成块和表格焦点。模块STATUS/TODO和局部渲染图已更新；报告`.tinadec_dev/reports/2026-10-07-markdown-islands.zh-CN.md`。979/14 skipped、native107、类型/构建及三宽度本地SFC夹具通过，不将模块001整体审计、完整App或真实模型验收标完成。

### 2026-10-07 空间布局与路由

UIE新增spatialLayout/spatialRouting纯函数：SCC/最长路径分层、同层中位排序与居中、尺寸驱动统一间距；新增同级先在本层就近找空位，不让手动远端节点吸走新卡。当前投影外历史几何保留但不撑大布局，主动整理可撤销。正交路由基于矩形边界、有限避障与端口方向，正常下游不纵向折返，逆向走侧面；blocked保留事实关系与原因。页面BaseEdge只渲染计算路径，替代默认smoothstep。详见`.tinadec_dev/reports/2026-10-07-space-layout-routing.zh-CN.md`；旧空间通过"整理本目标"应用新布局，保持用户坐标稳定。

### 2026-10-07 空间目标工作簇接手

依据`.tinadec_dev/plans/claude/piped-sparking-alpaca.md`继续第一阶段：新卡compact默认360×160，按run分区和真实依赖稳定分层；旧位置/尺寸保留，更新不移动旧卡，主动整理可撤销。任务与实例/归属与依赖分开，答案按run归位，未知依赖及受影响后继标待核验。详情/终端按需显示并保活，空间终端按当前会话run过滤，隐藏不响应全局终端快捷键。失败保留旧拓扑、文案/窄宽避让与迟到响应回归通过。报告`.tinadec_dev/reports/2026-10-07-space-clusters-handoff.zh-CN.md`；下列10-06单列420px为历史首版行为，当前默认以此条为准，完整APP-RENDERER-104仍进行中。

### 2026-10-06 紧凑会议队列与纵向拓扑

会议控件按session唯一，用户消息/待投递消息进入其中队列，不再独立成卡。UIE新卡默认420px宽、内容测高、纵向单列；manualPosition/autoHeight保留手动摆放与尺寸，旧布局保留位置。Vue Flow用真实任务依赖连接，跨节点走侧边。预览随内容收缩，160ms淡入淡出/缩放（减少动态效果时禁用），点击直接定位，删除定位提示按钮。证据 `.tinadec_dev/reports/2026-10-06-space-compact-topology.zh-CN.md`；完整空间执行组合仍进行中。

## Scope

本目录是长期模块开发档案。优先读取README、模块索引及目标模块README/ARCHITECTURE/STATUS/TODO，再回到源码和现有测试。原日期架构目录保留快照；此目录是后续讨论和任务更新入口。

## Truth and maintenance

- 模块TODO.md是任务唯一编辑位置，STATUS.md是功能状态唯一编辑位置；全局MASTER-TODO/STATUS/MODULE-INDEX由reindex生成，勿手改后期待保留。
- modules.json只维护目录/ID/节点归属；禁止生成器覆盖手写模块档案。reindex只刷新派生汇总并验证链接、稳定ID和Core24工程覆盖。
- reindex的总STATUS验证边界描述初始建档与后续专项的不同范围，不能硬编码"当前没有已验收/已完成"而否定模块更新。
- 每个Feature/Task有长期稳定ID。删除、合并或不做的条目保留替代/决策指针；同一功能缺口由一个主责模块持有，其它模块交叉引用。
- 区分源码可见、历史验证、本轮验证；只有与目标范围匹配的证据才能标已验收/已完成。禁止用测试数量或目录存在推算完成百分比。
- 未跑测试写待验收，不写未实现；平台限制先记范围边界，不默认开发所有可选能力。
- 任务进入实施前写清问题、目标、触及范围、实际依赖和验收条件。目标与当前行为分开；用户明确授权范围沿会话持续生效，不增设推断审批流程。
- 修改代码后同步受影响模块图、功能状态、任务和证据；跨模块契约变化同步相关模块档案与根AGENTS。
- Mermaid中的无向线是职责关联；有向运行/编译关系必须有源码依据。总图子节点是导航，不应当作独立服务。
- 历史todo/review只作为需求来源，引用必须带文件及章节。N9在两个历史文件中含义不同；不要只写"N9"。
- tests/Tinadec.Contracts.Tests是不可构建的遗留需求证据，不是活动验证入口（该目录README及活动sln可核）。

## Initial scope

55个模块目录，Core24工程逐一覆盖；总图82职责节点按modules.json归属或顶层导航解释。当前仅初始源码清点，模块图为职责投影，完整运行流和逐功能验收留在各模块001任务。
