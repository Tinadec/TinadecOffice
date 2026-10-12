# 对话流稳定性修复与验收

日期：2026-10-12。唯一任务：[APP-HOME-108](../../docs/development-program/02-modules/app/home/TODO.md#app-home-108)。基线：`feat/flat-transcript-refactor`，`1b6b7673` + 本轮工作树。用户原有 `apps/desktop/scripts/dev.mjs` 修改保留；原始用户数据、凭据和运行服务未重置。

## 归因与修复

最近15个提交确实引入了折叠活动常驻及滚动全量测量回归。原现场“简单首发尚未显示消息就崩溃”尚未取得完整因果链，因此不把所有崩溃归于本分支。用户已确认崩溃前打开或恢复过终端；隔离真实 Chromium 的 xterm5.5 `open → fit → dispose` 延迟回调可精确复现 `reading 'dimensions'`。无终端的简单首发与20次流式更新没有报错。

| 已确认问题 | 本轮处理 | 验证依据 |
| --- | --- | --- |
| xterm5.5 延迟 Viewport 回调访问已销毁尺寸，触发全屏错误页 | 固定 xterm6.0.0、fit0.11.0、web-links0.12.0，迁移自绘滚条样式；不吞异常、不延迟dispose | 实际Electron43.3.0终端循环与旧/新版本对照 |
| 异步快照、输出/退出、焦点/resize可能作用于新挂载 | 归属绑定实际Terminal对象；先撤归属，取消计时器/观察器和订阅再dispose；registry读取可取消 | 迟到快照与registry、隐藏fit、挂载销毁回归 |
| 折叠活动全部挂载，200思考/20工具DOM7→3830、约85→1175ms | v-if按需挂载活动；保留标题/生命周期注记和外层展开状态，工具正文仅展开读取 | TurnTimeline定向及真实生产组件链 |
| 5000行滚动夹具每次扫描全列表，9993次布局读；同长度切会话沿用旧状态 | 每帧合并scroll；末行测内容高度，观察器可见锚点、滞后二分回退；storage+session重建 | 实际5000 DOM、100scroll合并仅6次rect；prepend100锚点0px |
| 六路Promise.all阻塞正文；100工具事件600读取/594取消 | 正文最近100条独立提交；正文/活动与辅助读取独立单飞；同类在途仅保留一次后续刷新 | Home/Detached状态与突发/挂起辅助请求回归 |
| 2000历史活动逐事件复制数组，约7.2秒 | 批量hydrate、按run归约、push/upsert索引，发布一次；历史回放不触发REST | 批处理重复/跨run/性能证据 |
| 四项工具结果残留 | 子智能体传真实runId；read/list/search分组复用ToolCallCard；提示词/iframe统一widget:height；缺bridge/iframe失败显示普通正文 | 结果卡、分组、run与可信frame高度回归 |

正常错误仍报告，保留完整 Error/stack、Vue组件/生命周期信息、window脚本行列和来源。未添加全局 dimensions 过滤。

## API、历史与实时契约

- 原 `GET /sessions/{id}/messages` 数组接口保留。新增 `messages/page?limit=100&before_sequence=…&expected_revert_epoch=…`，返回 `{items,runs,has_more,next_before_sequence,history_revision,revert_epoch}`；消息公开 `sequence`。SQL按scope/session、排他sequence倒序取101行，仅物化100条正文，再升序返回；附件SQL仅页内MessageIds。
- 首次最近100条，上翻补100条。正文完成即显示，审批、编排及活动各自更新；辅助失败保留已有内容。分页、读取与流回调检查session/storage/generation和挂载归属。
- `activity?run_ids=<csv>`仅读请求运行及活跃运行；客户端按100个run分批。工具timeline的run_id、limit实际筛选；事件文件每run复用一次打开。useAgentActivity managedReads避免重复REST，批归约按run+seq去重。
- session SSE使用 `replay=false`。服务端捕获每run水位后发送无id的 `stream.ready`；客户端在快照期间缓冲，完成正文与活动后按run+seq去重释放。辅助请求挂起不阻塞这道屏障。重连补最新正文及已加载run；新run的seq1不受旧run高序号压制。
- `revert_epoch`从已有RevertedAt行数派生，无数据库迁移。普通追加不使旧页失效；撤回变化发无业务cursor的 `history.changed`，取消旧上翻、重读最近页。首页并发撤回最多重读一次；再次变化返回409，不无限重试。跨窗口撤回通过另一真实HttpClient验证。
- Core/Gateway OpenAPI及Desktop生成schema同步。Gateway继续薄代理，不保存分页/撤回/活动状态。

最终交叉复核补齐了同一快照归属链的边界：独立面板重连重新读取尾页并纳入断线期间完成的运行；ready 的撤回版本变化清除旧缓存帧；同版本 history.changed 也等待正在恢复的快照。正文快照发现新版本后，加载器至多再恢复一次，并等活动快照完成后释放实时事件。相同裸 session ID 切换 storage 时重建 SSE，旧源帧无权投递。首个消息页是否完成用独立状态记录，发送回执提前替换乐观消息不会丢掉上翻游标。

工具结果的 `DiffViewer → useMonacoDiff → monaco-editor` 静态加载链由本分支新增。DiffViewer 改为真实差异内容显示时异步加载，普通工具结果不执行该模块加载器。其他 UIE/编辑器入口仍可能加载 Monaco，此修复不等于已量化整个首屏 bundle 的缩减。

最终复核还确认完成时序的正文遗漏：消息页取完正文后运行恰好完成，附加runs已是completed；活动快照的去重登记会抑制缓冲中的user.response/run.completed，失去正文刷新机会。全局总线现只去重已经投递的实时帧，活动快照由reducer自身按run+seq去重。旧正文+终态run+活动含同一完成帧的回归已通过，恢复后正文再次读取并显示assistant。

移除两处fit的既有blanket catch；已有归属、DOM连接和非零尺寸守卫，FitAddon0.11自身处理未就绪尺寸。真正的fit错误现在进入正常错误报告，新增dimensions错误传播回归通过。上述补修首批Home/Detached/总线与终端99/99及vue-tsc通过；最后快照期限、shell结果与独立面板限次补修共104/104及vue-tsc通过；前一批公共入口补修9+26通过与类型检查单列，均不重复加到最后1396全量。

## 验证与证据

证据目录：[2026-10-12-transcript-stability](../evidence/2026-10-12-transcript-stability/)。可复跑组件宿主：[run-terminal.mjs](../evidence/2026-10-12-transcript-stability/run-terminal.mjs)、[terminal-electron.cjs](../evidence/2026-10-12-transcript-stability/terminal-electron.cjs)、[terminal-fixture.ts](../evidence/2026-10-12-transcript-stability/terminal-fixture.ts)。

| 层级 | 已取得结果 | 明确边界 |
| --- | --- | --- |
| 真实Electron/Chromium组件 | 生产TerminalView/useTerminal、TerminalCallBlock；本地100轮+智能体100轮生命周期，实际尺寸91×12；迟到输出未写新终端、隐藏fit保护、订阅归零；生产ChatPanel→MessageList/MessageItem/LiveTurnBlock/MarkdownRender/TurnTimeline100轮首发、每轮10delta，保留终端，0 errors；5000 DOM布局读取6次/补100锚点0px，分页提示增删均0px | 此组件夹具controller/transport与外围Composer/ChatHeader/WelcomeScreen为替身，非完整模型链 |
| Desktop状态/API定向 | 正文先显、上翻无重复、旧页撤回、工具突发、ready/history屏障、同run去重与跨runseq、失败释放buffer及辅助挂起回归通过 | happy-dom含受控geometry及API替身；不替代真实浏览器布局 |
| Core SQLite真实HTTP | 15个不同定向测试最终通过：10000行旧9900正文缺失仍只返回最新100；keyset追加/epoch、活动过滤/limit、ready先于快照、新runseq1、另一窗口history.changed、精确message_id；撤回运行不再进入会话快照，独立run审计仍可读；24并发任务快照与旧读句柄及Debug投影 | 临时用户根；不修改用户数据，不折算PostgreSQL/其他平台 |
| Gateway | 最终完整113/113通过，18个文件，含流响应头/未缓冲/取消/错误传播及路由回归；公开快照与生成schema同步 | app.handle的上游替身及真实服务证据单列 |
| Desktop native/scripts | 193通过/1跳过（共194），无失败 | Windows本机宿主/脚本回归；skip不计通过 |
| 类型检查 | vue-tsc通过；生成schema重新生成成功 | 全量与最终构建对账见下方 |

最终冻结源码Desktop完整复验已通过1396项、14跳过、0失败（154文件，其中153通过/1跳过），测试命令204.96秒、wrapper207.626秒，exit0，无超时。此前1389项全绿留存为desktop-full-before-final-review.json，与最后104定向不相加。原失败轮次和修正后定向记录保留但不相加，见desktop-full-verified.json。最终类型检查及lockfile门禁通过；生产构建与隔离真实Core/Gateway+受控Provider/Electron宿主继续串行，不以正在运行计通过。

首次 Desktop 全量记录为1376通过、2失败、14跳过（153文件）。两个失败都定位到测试夹具：ComposerBar 的120ms真实时间假设在资源竞争下失效，改为可控时钟后整文件73/73；happy-dom 的脱离iframe没有contentWindow，显式提供所属窗口后 ToolWidgetFrame 整文件6/6。随后状态/API/总线/独立面板又补齐上面的实际竞态，随后执行了最终单worker全量，结果见下方，首次结果不冒充最终全绿。初次 Core 并发撤回夹具漏建临时正文目录，修正后该用例通过；最终12个不同相关测试均有TRX记录。追加的撤回运行投影用例首轮被4分钟测试watchdog中止，该次不计通过；最终重验结果见下段。

追加的撤回运行投影已串行重验2/2通过（包含一次活动读取回归重跑），去重后的最终Core为13/13；原资源中止TRX单列保留。会话orchestration在SQL筛可见/活跃运行并只取最新一条；activity排除撤回的终态trigger。Memory与Lifecycle是不同模块数据库，因此仅读当前会话撤回消息Guid，再下推Lifecycle SQL，不读取旧正文；极大撤回历史仍会扩大这份元数据及查询参数，未宣称此路径常量成本。

最终 Desktop 单worker全量已执行完毕：1388通过、1失败、14跳过，153文件，约24分8秒。唯一失败来自本轮懒加载测试错误使用不存在的工具ID，现改为真实 `replace_lines`，ToolResultView、ToolWidgetFrame与工具ID源码检查集中32/32通过。Home测试另隔离真实runStream transport以免访问本机Gateway，完整Home文件54/54通过。该轮如实记录“全量执行 + 修正后定向通过”；后续另一次完整全绿1389/14/0已取得退出凭据，作为最终门禁结果。组织面板曾出现worker退出超过默认10秒，但断言全部通过；源码未发现未清理的interval/API，归因不足，未修改产品或放宽断言。happy-dom退出的取消日志在首轮也存在，相关只读诊断见证据简表。

完整门禁与修正后定向结果见 [desktop-gates.final.json](../evidence/2026-10-12-transcript-stability/desktop-gates.final.json)；状态、API及总线最终回归和重复计数边界见 [state-tests.final.json](../evidence/2026-10-12-transcript-stability/state-tests.final.json)；测试退出诊断及仅测试的网络隔离依据见 [test-teardown-readonly-audit.json](../evidence/2026-10-12-transcript-stability/test-teardown-readonly-audit.json)。

活动归约性能证据为真实 reducer、三个1000条预热及七轮交替1000/2000/4000条：中位约286/613/1292ms，倍增约2.15/2.11，额外REST为0，重复hydrate不增加条目。测量期间机器存在明显资源竞争，数值不作为稳定的界面延迟承诺；按规模的增长趋势及原始样本见 `activity-hydration-results.json`。

## 实际链路新增快照故障

合法受控Provider已完成真实规划、执行、监督与meeting；运行回复和会话SSE均通过首轮检查，随后100会话循环在第6轮捕获独立的后端故障。Core `File.Replace`报 `IOException: 无法删除要被替换的文件`，运行变为failed，界面保留用户消息并显示失败，无未捕获前端异常。前5轮不折算100轮通过；诊断见 [task-snapshot-runtime-failure.json](../evidence/2026-10-12-transcript-stability/core/task-snapshot-runtime-failure.json)。

基线UpdateTaskAsync没有串行保护，File.OpenRead未允许删除共享，且同步阻塞异步反序列化；本轮把同run的读取、修改、原子替换及TaskRevision更新纳入单一锁，改为异步且允许删除共享的读取。精确冲突句柄未捕获，不能把该次读者身份写成已证事实。回归用24个并发更新和持有旧文件句柄检查完整任务、版本及临时文件，连同既有Debug投影2/2通过，新生产API已编译成功。原13项加这两项去重共15/15，见 `core/test-summary.json`。随后重跑的真实首发、终端恢复和分页通过，第二轮因Gateway的Bun原生断言退出而未完成；不能写100轮通过。

## 实际Gateway断开流故障

修复后的Core再次完成真实首发、终端恢复、拆分合回、20次标签切换、主题/缩放和两页精确ID检查；第二轮Gateway运行时Bun1.3.14在请求断开路径触发内部弱引用断言而退出，宿主随后撤权，UI没有未捕获异常或崩溃页。该次不能记100轮通过。持久摘要见 `gateway/actual-runtime-crash.json`。

代码及实际依赖核对发现：Kestrel传来的`transfer-encoding: chunked`被原样保留时，Elysia1.4.29会再次getReader包装Response，与旧注释的原生透传假设不符。共享流响应助手现去除hop-by-hop及Connection声明的传输头，Core/ToolRuntime SSE、日志及附件下载统一返回Response，保留状态、业务头和正文，不吞取消或未知异常。通用流与ToolRuntime流还固定manual重定向，避免带宿主头跟随未经验证地址；fetch阶段取消按原始reason拒绝，真实网络失败仍为502。真实socket四模式各100次读取后断开：原透传产生96条AbortError，手工清头、生产助手与纯Bun原生均0；这次小对照四模式都未panic，不能单凭它声称内部断言已消除。Gateway最终完整113/113通过，真实应用重验完成前三轮，第四轮在创建run前被host_identity_unavailable准入拒绝，消息保留pending，未见HTTP错误、Bun退出、快照异常或前端崩溃，最终hostState恢复ready；尚不能声称100轮通过。具体证明失败原因仍待直接观察，不能仅因机器资源紧张就写成已确定根因。本轮未升级全局Bun或改变用户服务。

为继续隔离交互验收，宿主适配将单端点证明预算设20秒、总预算30秒，真实role/nonce/HMAC和撤权仍由生产代码验证，生产默认2秒/10秒未改。每轮先只读取新证明、等businessReady及加载结束，然后单次发送；失败即时留证，不自动重放写。该适配不能宣称默认生产预算在内存压力下通过。新增安全证明耗时/失败观察不保存任何token、nonce、proof或header。

补充快照读取30秒局部期限：Home/独立面板仅给会话HTTP读传合并的deadline signal，结果归属仍检查owner signal。超时保旧正文、显示可恢复错误并释放实时缓冲，阻止单次健康服务下永不返回的读取让缓冲永久增长；未改变业务写、全局重试或生产宿主证明预算。该边界测试初次误期待额外notify，实际出口是messagesLoadError，修正断言后104项定向通过。独立面板连续撤回使用同一lane/同一deadline最多读两页，第二次版本仍变时保旧结果并显示重试，不再递归开启新恢复预算。连接内已投递事件的seen集合仍随活跃长会话增大，暂未压缩为水位，以免时间戳排序下抑制合法低seq帧；非本轮已复现OOM。

本地PTY快照目前无输出水位：snapshot replay与读期间held字节可能重叠，原“无重复”注释已改为准确说明。该既有重复输出风险尚未通过新增IPC协议修复，不属于此次精确复现的dimensions崩溃。

折叠shell终态现在展开读取已有单execution持久结果接口；运行态仍保留交互终端。原仅依赖最近80帧的terminal replay可能遗漏早期输出，三条结果卡回归现覆盖completed/failed惰性读取及running→completed切换。测试还确认Vue可选Boolean缺省会变false，已用withDefaults明确richViews=true，显式简洁模式false保持有效。初次三条失败记录保留，修正后计入104定向。

Gateway最终类型门禁的28个测试fixture诊断也已修正：25处fake fetch适配Bun preconnect结构类型，3处可空映射先显式断言，不改产品/tsconfig。最终完整113项再次通过且tsc exit0；旧失败记录留存，不继续声明当前类型失败。

## 生产构建与循环依赖

首轮最终源码生产配置构建使用8GiB Node堆和仅进度观察插件，转换6469模块后停在`rendering chunks...`，40分钟看门狗中止，exit1；未取得生产构建成功凭据。原记录保留为`production-build-initial-timeout.json`。Rollup同时确认Canvas和ChatCard路径通过Desktop叶模块返回UIE公共barrel的两条循环chunk警告；这证明执行顺序风险，尚不能证明是超时根因。

本轮最小补修为`useDockDrag`直接导入`engine/dockDrop`与`engine/types`，`TerminalCallBlock`直接导入`components/useUie`并同步测试mock，无manualChunks或构建选项改动。Desktop终端/标签9项、UIE dock26项定向通过，最终类型检查及构建复验另留凭据。此前1389全量全绿取自补修前源码；最后冻结源码另取得1396/14/0全量退出凭据，不将定向重复加到全量总数。

## 保留的验收边界

用户实机存在 `credential_missing`。受控Provider只验证请求、持久运行和流协议，不等于外部真实模型效果；真实模型链须在凭据可用后单独验证。当前首发崩溃的完整原现场调用链仍未取得，修复依据为已精确复现的终端缺陷与确定的分支回归。Linux/macOS GUI、完整安装包和发布门禁未在本轮冒充验收；未提交、推送或发布。
