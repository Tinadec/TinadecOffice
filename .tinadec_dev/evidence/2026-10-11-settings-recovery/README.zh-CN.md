# 本轮证据索引

唯一任务[APP-HOME-107](../../../docs/development-program/02-modules/app/home/TODO.md#app-home-107)，结论与限制见[报告](../../reports/2026-10-11-settings-recovery.zh-CN.md)。数据来自当前用户9份TOML的只读副本；随机端口、独立临时用户根和随机宿主凭据，未复制安全库/数据库。正文与凭据不提交。

- `before-current-copy-read.json`：旧服务的真实HTTP超时/网络失败，含原配置摘要。
- `hash-cache-only-current-copy-read.json`：仅摘要复用、仍慢的中间结果，不计修复完成。
- `after-current-copy-read.json`：最终批量预览，Gateway20个Agent及20/20详情200。
- `before-config-inventory.json` / `after-config-inventory.json`：只保表数量、状态及文件摘要。
- `desktop-ui-acceptance.json`：Electron43.3.0生产renderer + 实际隔离Core/Gateway。宿主状态bridge为夹具，创建/身份/安装器不在验收范围。
- `desktop-agents-loaded.png` / `desktop-agents-read-failure.png`：实际页面显示/失败保留。textarea文字用仅测试CSS隐藏；UI计数仍按真实DOM断言。
- `desktop-user-config-unchanged.json`：窗口验收前后原9份用户配置SHA-256一致。
- `verification.json`：本轮Core/前端定向、类型、构建摘要，以及Desktop全量首跑1263通过/1失败/14跳过、修正旧断言后两文件16/16复跑的独立记录与原始JSON摘要。合并结果1265通过/14跳过，不宣称修正后完整全量重跑；原始TRX/完整JSON留在ignored tmp。

`read-current-copy.mjs after`和`settings-fixture-server.mjs`可复跑；从仓库根运行。读取脚本的before默认使用本机已存在旧构建，不是自动checkout/rebuild历史提交。UI脚本依赖当前独立构建`.tinadec_dev/tmp/settings-recovery-dist`和独立Core构建`settings-recovery-build`；不在用户5173或正式端口服务，不停止用户进程。

网络夹具为每次真实renderer fetch加独立测试header，并对同一次fetch的Chromium透明GET重发统一失败。只计header身份得7次应用读，16次wire请求分别记录；配置/安装写0，既有包预览及project_templates元数据POST分别计数。
