# 智能体目录读取性能与连接恢复

日期：2026-10-11；本轮源码起点5d8096c3，最终代码df0977ff。旧服务复现使用本机保留的workspace-api-audit构建，不据其文件名推断完整提交身份。唯一任务为[APP-HOME-107](../../docs/development-program/02-modules/app/home/TODO.md#app-home-107)，不增加平行任务入口。用户配置、数据库、安装包和工作区登记保留；本轮不停止用户服务或读取其宿主凭据。

| 阶段 | 提交 | 交付 |
| --- | --- | --- |
| Core配置投影 | `4787970c` | 复用已验证/应用且摘要相同的作用域配置 |
| Core智能体目录 | `d40e89f6` | 作用域内批量模型预览，重复逻辑节点键去重 |
| Desktop共享读取 | `6c9b5dd8` | 有限只读网络恢复，取消/单飞/代次保护，写不重放 |
| Desktop设置页 | `df0977ff` | 成功空态/失败分离，详情局部错误和未保存草稿保护 |

## 已确认的因果与修复

当前`agents.toml`含20个published智能体、14个模式、67个节点和2份包登记，目录空白不是配置为空。旧`ListAgents`对每个模式节点单独调用模型预览，每次多次打开配置上下文、查历史与版本，然后反复冻结/解析相同策略。精确复制9份TOML到独立临时用户根、不复制数据库或安全库之后，Core /agents超过90秒仍未完成，Gateway约28.8秒出现网络TypeError；同期mode/pack读取成功。

仅增加投影摘要复用，中间Core耗时仍为86.4秒，Gateway约32秒失败，因此不能将该中间结果作为修复验收。最终增加作用域内批量读取，并在一次预览中复用相同计划/解析。草稿和published节点的同一逻辑键去重；已知配置错误仍按单项不可用返回，取消/数据库/内部异常传播。单项预览与新运行冻结不使用跨请求缓存。

投影读取仅复用此前校验并成功应用、当前路径/id/摘要相同的文档。每次重新读源摘要；同长度/mtime编辑仍被检测，失败不缓存，写入撤销缓存，不跨scope。公开Read、Save、Compile和Verify保留校验。

前端原来在setup立即读取，首次失败只通知后显示空目录；详情并发20余次且失败静默归为空定义。共享JSON transport现在仅对GET/HEAD网络/正文失败有限重试，等待250/750ms、最多三次。请求固定作用域，每次重验宿主；HTTP/JSON/config错误和写/安装不重放。网络读取最终失败可重试；丢失写回执明确表示结果未知。

Settings读取接入页面所有权：取消、单飞、就绪恢复与代次保护，成功空目录与失败分开。详情每批四个，单项失败保目录并阻止编辑空定义；辅助读取保旧值并给诊断。刷新不覆盖未保存草稿，编辑身份暂时消失时先显示错误，重新可用后恢复草稿。模型目录同样接入读取生命周期。本轮不改AgentModes等其他子模块的所有读取流程，也不声称所有网络失败都已消除。

## 实际HTTP验收

证据目录：[2026-10-11-settings-recovery](../evidence/2026-10-11-settings-recovery/)。所有服务使用随机端口、随机测试宿主凭据和独立临时用户根；renderer没有凭据。仅保存数量、状态、摘要、耗时，不保存用户TOML正文或提示词。

| 请求 | 修复前 | 最终修复 |
| --- | --- | --- |
| Core GET /agents | 90s超时 | 200，2142ms，20项 |
| Gateway GET /agents | TypeError，约28.8s | 200，1138ms，20项 |
| Gateway GET /agents/{id} | 未完成 | 20/20成功 |
| Gateway GET /agent-modes | 200，14项 | 200，584ms，14项 |
| Gateway GET /agent-packs | 200，2项 | 200，411ms，2项 |

上述cold/warm读取顺序固定为Core列表在先、Gateway在后，不能将1.1秒宣称所有机器首读保证。用户9份配置前后SHA-256一致，未复制或修改用户数据库/凭据。`hash-cache-only-current-copy-read.json`是未完成优化的中间证据；`after-current-copy-read.json`才是批量预览后的结果。

## 本轮回归与边界

- Core定向：60/60，通过AgentRuntimeBinding、ConfigurationDocument、GraphSeed和AgentPackEndpoint相关选择；TRX保存在ignored tmp，摘要另存证据。包含批量/单项结果相同、坏项不吞其他结果、下一批绑定更新、取消、草稿/published同键，以及配置摘要缓存与失效规则。
- Desktop定向：11个文件89/89；覆盖读取网络预算、正文中断、写不重放、HTTP失败、撤权/取消、恢复单飞、旧响应忽略、目录/详情局部失败与草稿保护，以及生成客户端AgentPack ETag/错误契约。类型检查通过。
- SearchNavigation等待Tools异步组件实际加载，保留精确条目断言，显式5s等待预算适应冷转换；不更改产品权限过滤。
- Windows Electron43.3.0：本轮生产renderer静态产物接真实隔离Core/Gateway，20个Agent与当前2份包显示；首次网络丢失自动读恢复，持续失败保20项/结构化错误，手动重试一次恢复，宿主unavailable→ready刷新一次并保草稿，配置/安装写0。主进程与宿主状态bridge为夹具，不是生产main/preload验收。配置副本未携带安全库和外部工具安装，辅助就绪诊断仍显示，不能宣称真实模型/工具就绪。
- 浏览器内核会对GET socket做透明重发；最终夹具给每次renderer fetch添加独立仅测试header，对其所有wire重发统一注入失败。实际应用fetch合计7次：首次2次、持久失败3次、手动重试1次、宿主恢复1次。16次wire请求不能误计为产品自动安装/重试次数；有1次既有包预览、3次project_templates元数据POST，配置/安装写始终0，普通恢复未新增包预览。
- 源组件冷加载夹具两次在240s内未完成，均退出owned服务；中间静态夹具先修正Windows路径前缀，再区分包预览POST与配置写，最后补renderer/wire次数区分。最终证据仅指向通过运行的生产renderer夹具，前面失败不计通过。
- 独立输出的Vite7.3.6生产构建通过，6438模块，约14min；保留既有TinadecUI循环chunk重导出和大chunk警告。完整App生产main/preload、安装器、Linux/macOS和PostgreSQL本轮不以既有证据替代。
- Desktop全量首跑：138个文件，1263通过、1失败、14跳过。唯一失败是`settingsCenters.test.ts`要求旧“读取失败也显示空目录”条件的源码字面断言；保留原始失败记录，改为真实组件测试，补验成功空列表才显示空态。受影响两文件复跑16/16通过（含1个新增用例）。按文件和测试全名对账后为1265通过、0失败、14跳过；这是全量首跑加受影响文件复跑的合并结果，不是修正后再次完整全量运行。原始JSON在ignored tmp，摘要及SHA-256见`verification.json`。

当前用户运行的Core没有被终止或自动换DLL。采用后端修复需要从仓库根重新启动开发整链`npm run dev`；renderer HMR不能更新已运行的Core。没有清理用户`.tinadec`或修改原有`.gitignore`。
