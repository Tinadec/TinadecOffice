# 计划书⑤：远程多机 — 设备互控收窄版（对应扩张调研 §3.1）

> 状态：草案 v1（2026-11-03），来源：`expansion-directions-research.zh-CN.md` + 本地逆向 Qoder 0.5.1（`Qoder-fuckali/analysis/09-*`）。**未登记**，待批准。
> 判据沿用既有差距报告 A/B/C 三级；每批先红后绿+变异对照，收尾必须过 live 双机验收。

## 0. 结论一句话

TinadecOffice 的拓扑天然多机（Desktop→Gateway→Core 全 HTTP，webShim 已复用桌面渲染器），且 Gateway 已有受信主机凭据机制——本计划把它从"仅本机托管客户端"扩成"注册设备互控"，第一批只做**另一台机器以注册设备身份连本机 Core**，不做公网中继。

## 1. 对标与现状

**Qoder 0.5.1 peer-device（一手逆向，可抄作业的部分）**
- 授权持久化在 `peer_grants` 表而非配对码——迁移注释原文："peer_grants 这张表（不是配对码）才是『重启后原主控端仍能连上』的保障"；机密（配对凭据、证书私钥、配对码）只进 safeStorage 加密文件，**不进数据库**。
- 设备身份=运行时自签证书，`fingerprint256`（SHA-256）即身份，设备唯一性按 `(transport, remote_device_id)` 而非地址（DHCP 换 IP 不漂移）。
- 访问策略 `peer_access_policies`：按 controller_device_id 保存 `workspace_mode(all|selected)+workspace_ids`——**哪个设备能动哪个工作区**是一等治理对象。
- 握手拒绝原因全集（`unreachable|inboundDisabled|pairingCodeRejected|fingerprintMismatch|unauthorized|...`）值得照抄为错误词表。
- 不抄：Relay 公网中继（需云）、远端软件更新确认流、WSL 注册。

**TinadecOffice 现状（2026-11-03 实测）**
- Gateway OpenAPI 自述（`TinadecGateway/src/index.ts:171`）："every /api/v1 request except GET health and host-challenge requires a private trusted host credential. Managed Electron first verifies endpoint HMAC identity and injects credentials only in the main-process network layer; **anonymous loopback, Agent shells, and unbound web/remote clients have no API authority**"——凭据+HMAC 身份+host-challenge 已存在，但设计为"仅托管客户端"；`unbound web/remote clients` 明确无权限。远程多机的本质=把"绑定"从进程扩到**设备**。
- `apps/web`（webShim）复用 desktop 渲染器——第二设备的客户端形态已存在，缺的只是身份与准入。
- `EnvironmentKind` 枚举含 cloud/remote（仅类型定义）；路线图 A4（OIDC/多租户）延后（`tinadec-four-product-roadmap.zh-CN.md:79,172`）——本计划不依赖 OIDC，设备身份走证书/token，A4 后续再并。
- Core 是唯一状态权威+12 态状态机+审批三道防线——远程接入不产生第二事实源，治理语义现成。

## 2. 目标 / 非目标

**目标**：①设备注册与准入（证书身份+指纹 pin+设备×工作区策略）；②远程设备对指定会话的**影子投影（只读）**与**受控接管（写仍走本机 Core 审批）**；③跨重启授权持久（peer_grants 语义）；④全程审计入既有事件流。
**非目标**：不做公网 Relay/云中继（计划书⑧评估）；不做 SSH 远程主机注册（R3 评估项）；不做多用户身份（那是⑧/A4）；不改 Desktop↔Gateway 的本机部署假设（默认仍 loopback，跨机显式开启）。

## 3. 设计方案

### 3.1 R1：设备身份与准入
- 设备注册表：设备条目=自签证书指纹+设备名+准入策略（`all|selected`+工作区清单）+授权状态；机密（私钥）只进 DPAPI/SecretStore，不进 SQLite（对齐 Qoder"机密不进库"注释）。
- 接入协议：现有 host-challenge/HMAC 扩展出设备档——新设备经本机显式配对（显示配对码+指纹确认，抄 Qoder 三频道语义的极简版：签发/查看/吊销），配对成功写入授权表；此后凭证书+指纹 pin 连接。
- 准入策略表复用 `WorkspaceBinding` 既有工作区概念；策略变更带 revision。
- 默认姿态：入站总开关**默认关**（对齐 Qoder `setPeerInboundEnabled` 需显式开启）；关=行为与今天完全一致（只有本机托管客户端）。

### 3.2 R2：影子会话与受控接管
- 远程设备可把指定会话镜像为**只读影子**（事件流 after_seq/Last-Event-ID 续传直接复用——SSE 协议天生支持多消费者）。
- 接管（在远程设备上发消息/ steering）：仍以本机 Core 为执行主体，走既有 dispatch_mode 与审批；影子与接管的边界=写权限，由准入策略决定，不由 UI 决定。
- 断线恢复：after_seq 续传+事件幂等键防重投；授权吊销=即刻断流+影子失效。

### 3.3 R3（评估项）：SSH 远程主机
- 对标 Qoder remote-ssh 17 频道（含 thin-fallback）；本仓最小面=远程主机注册+连通性预检+作为 `command_run`/`shell` 的执行目标。**只评估，不进本计划批次**——它与 TinadecTools 进程模型（每工作区一子进程）的冲突需要单独设计。

## 4. 分批落地与验收

| 批 | 内容 | 验收门禁（先红后绿/变异） |
|---|---|---|
| RM1 | 设备注册/配对/吊销 + 准入策略 + 入站开关（默认关） | 未注册设备跨机连接被拒且拒绝原因落在 Qoder 式错误词表；变异：摘掉指纹校验→恰红 |
| RM2 | 影子会话（只读投影）+ 受控接管 + 跨重启授权持久 | 双机 live：设备 B 影子看会话→接管发消息→本机审批照走；重启两端授权仍在；吊销即刻断流 |
| RM3（评估） | SSH 远程主机 | 单独评审 |

**live 验收是硬门禁**：双机（或主机+虚拟机）真实走一遍配对→影子→接管→吊销，按"真实可用"标准验收。

## 5. 风险与开放问题

1. 攻击面本质扩大：从"本机 loopback"到"局域网可达"——入站默认关+指纹 pin+准入策略三层缺一不可；security.md 同批落字。
2. Windows 防火墙/网络发现：首开入站时的用户引导（OOBE 级文案）要设计。
3. 开放问题：影子会话是否允许远程设备只读浏览工作区文件（不只会话流）——建议 v1 只给会话流，文件面跟随准入策略后续批。

## 6. 与其他计划书的关系

- 是计划书⑧（团队协作"宿主模式"）的直接地基；RM1 的设备身份可被 A4（OIDC）后续收编为账号身份。
- 与计划书③（沙箱）无冲突；与⑦（PR 审查）共享"远程触发→本地执行"的形态。
