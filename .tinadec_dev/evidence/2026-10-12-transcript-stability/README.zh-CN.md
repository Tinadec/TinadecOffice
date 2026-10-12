# 对话流、终端与滚动验收

唯一业务进度以相关模块 TODO/STATUS 和本轮总报告为准。本目录包含可复跑的隔离验证，不使用用户数据，不停止用户进程。

`node .tinadec_dev/evidence/2026-10-12-transcript-stability/run-terminal.mjs` 在独立 Electron 43.3.0 窗口运行生产 TerminalView、TerminalCallBlock、useTerminal/useTerminalSource、ChatPanel、MessageList、MessageItem、LiveTurnBlock、MarkdownRender、TurnTimeline 和 useMessageScroller，使用真实 xterm 6.0.0。Core、controller、PTY transport 和非验收外围界面为夹具替代，不能将它表述为完整 App 运行。

最终结果见 [terminal-acceptance.json](terminal-acceptance.json)：100 次本地终端及 100 次智能体终端生命周期循环，100 次首发/每次 10 个流式增量；5,000 消息、100 次合并滚动事件仅 6 次矩形读取，前插 100 条和分页提示增减均偏移 0px；errors 为空。截图 [terminal-xterm6.png](terminal-xterm6.png) 是真实 xterm 输出，fixture 样式不代表全产品视觉验收。

`node .tinadec_dev/evidence/2026-10-12-transcript-stability/run-real-app.mjs` 需最终 `.tinadec_dev/tmp/transcript-core-build/bin/TinadecCore.Api/debug` 产物；脚本复制到独立临时根，启动随机端口的实际 Core、Gateway、本目录 `controlled-provider.mjs`、Vite 和 Electron，使用生产 main/preload、HomeController、HomePage、UIE、ChatPanel、TerminalPanel。随机端点仅复用既有宿主适配边界，保留实际 HMAC 验证及可信请求签发；renderer 不持有宿主凭据。服务由 runner 启停，非验收的 Git/空间/市场等静态卡从源图排除；完整 App 根及安装器不在此验收范围。

真实链脚本断言受控 provider `POST /v1/chat/completions` 200、HomeController 首发“你好”对应真实 interaction 201、生产 run SSE 的 live reply 投影、session SSE 的 ready/user.response、按实际消息ID与角色核对两页合并、renderer reload 历史恢复、真实本地 PTY、终端 split/merge 和切换，并连续创建100个独立会话发送受控任务。持久正文包含 user、tool_evidence 与 assistant，不能假定仅两条。所有阶段收集原始前端/组件异常，provider 观察器只记录 method/path/status。通过时才输出 `real-app-acceptance.json`；运行日志和失败阶段在 owned `.tinadec_dev/tmp/transcript-real-*`，不得将未通过的启动或环境超时写作业务验收成功。

本机曾在完整构建与 API 验证并行时仅余约 316MiB 可用内存，40/120 秒服务冷启动及 250 秒 renderer 加载预算发生超时。后续改为串行单 runner、固定预编译缓存、health 5 分钟；100轮验收的Electron总预算90分钟，runner91分钟。这些环境记录不证明产品业务崩溃。此前第6轮的任务快照原子替换IO异常属于已取证的独立产品故障，见 `core/task-snapshot-runtime-failure.json`；并发修复的两个测试已通过，见 `core/task-snapshot-final.trx`。

受控Provider仅接受本验收的固定任务，规划、worker outcome、监督criteria/evidence和meeting完成证据遵循真实运行契约；未知任务拒绝或不宣告完成。`scripts/model-fixture.mjs`旧输出缺TASK_OUTCOME/CRITERION_EVIDENCE，导致等待用户而非最终回答，不能将该夹具失败归为界面漏回复。诊断见 `core/controlled-provider-attempt-diagnosis.json`。本机外部模型仍为credential_missing，受控链通过不代替外部真实模型验收。

`ToolWidgetFrame.test.ts` 的 iframe 消息高度用例在 happy-dom 中显式提供 `contentWindow` sentinel；DOM 仿真不创建独立 sandbox/custom-scheme 浏览上下文。真实浏览器的 iframe 安全边界仍由已有宿主测试负责。

源码还确认本分支的 `ToolResultView → DiffViewer → useMonacoDiff → monaco-editor` 静态链。`ToolResultView` 改用已有 Vue 标准 `defineAsyncComponent`，普通文件/搜索结果不加载 diff 编辑器，真实 patch/diff 才异步加载；定向测试检查模块 loader 在普通结果时不执行、在合法 patch 时执行一次并透传内容和文件标识。此变更只断开该调用路径，UIE 其它 Git/Code 注册路径仍可能静态加载 Monaco，不能宣称初始 bundle 已完全消除它。

生产构建可复跑脚本：`node .tinadec_dev/evidence/2026-10-12-transcript-stability/run-production-build.mjs`，使用实际Vite生产配置，仅附加transform进度观察，40分钟watchdog只终止自己的构建进程。成功才写`production-build.json` accepted=true；直接Vite之外的lockfile与vue-tsc门禁分别保存，不能称未执行的完整npm命令通过。

最后Desktop全量复验：`node .tinadec_dev/evidence/2026-10-12-transcript-stability/run-desktop-tests.mjs`，单worker、45分钟watchdog、独立`desktop-full-verified.json`。此前1388通过/1失败/14跳过及修正后定向证据保留，不与新轮重复相加。

Gateway实际取消与传输头证据在`gateway/`；最终113/113、18文件通过。真实小socket对照不复现panic，不宣称已从运行时消除所有内部断言。修复后应用前三轮成功，第四轮准入拒绝而无前端崩溃，见`host-admission-attempt.json`，100轮仍待独立完成。

后续100轮隔离验收采用明确的宿主证明时间适配：真实nonce/role/HMAC验证、可信签发及周期撤权保留，仅单端点预算20秒、总预算30秒；生产默认仍2秒/10秒。每轮先只读获取新证明并等businessReady/加载结束，然后仅发送一次，任何准入或run错误立即失败、不重放写。receipt中的host_proof_budgets与default_production_admission_under_pressure=false说明该边界。proofRequests仅记录角色、耗时、状态码、异常name/code和signal状态，不存nonce/proof/token。此前admittedMs只是方法结束时刻，新夹具改名handleSettledMs，真实完成仍由interaction201、runId及completed事实确认。

最终补修定向与类型门禁：`node .tinadec_dev/evidence/2026-10-12-transcript-stability/run-final-race-gates.mjs`，最后104通过（Home56、Detached新增限次恢复、实时完成帧和终端/shell结果等），类型通过。此前99通过、超时测试出口断言失败及shell Boolean默认失败分别留证，不相加。Gateway最后完整113/113和tsc通过，旧28项fixture诊断已修复。
