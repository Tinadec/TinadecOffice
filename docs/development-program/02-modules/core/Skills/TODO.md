# Skills · 市场与集成配置：TODO

模块ID：`CORE-SKILLS` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本文件是该模块任务的唯一编辑入口；总TODO由本文件生成。任务ID长期稳定，完成或取消后保留记录；每项任务记录工作范围与验收条件。

状态：待核查 / 未开始 / 待方案 / 进行中 / 阻塞 / 待验收 / 已完成 / 不做 / 已被替代。优先级是初始建议，可在逐模块分析后调整。

<a id="core-skills-103"></a>

### CORE-SKILLS-103 Skill 源目录与真实更新卸载

- 类型：实现
- 状态：已完成
- 优先级：P1
- 主责模块：CORE-SKILLS
- 关联功能：CORE-SKILLS-F005
- 范围：project .tinadec/skills、scope live source；仅选中包冻结、完整旧资产审核与物理删除。
- 验收：实际文件副作用、冻结边界、拒绝与取消清理；平台证据独立。

**验收条件**

- [x] Skill 源码、不可变版本和运行冻结分离；真实更新卸载具有文件副作用。
- [x] 完成 Skills/市场联合回归与失败修复复查，记录平台范围和证据。
- 完成证据：[实施报告](../../../../../.tinadec_dev/reports/2026-10-09-storage-resources.zh-CN.md)。2026-10-09 Windows 当前工作树，资源联合及差分复查通过相关用例；不沿用历史平台验收。

<a id="core-skills-001"></a>

### CORE-SKILLS-001 完成 Skills · 市场与集成配置 的逐功能审计与模块图精化

- 类型：核查
- 状态：待核查
- 优先级：P2
- 主责模块：CORE-SKILLS
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

- [TinadecCore/Skills/SkillsModuleRegistrar.cs:43](../../../../../TinadecCore/Skills/SkillsModuleRegistrar.cs#L43)

<a id="core-skills-101"></a>

### CORE-SKILLS-101 实现通用技能 provider 的列出与读取

- 类型：实现
- 状态：已完成
- 优先级：P2
- 主责模块：CORE-SKILLS
- 前置依赖：未细化；开工前在此列出实际Task ID，不能把全部关联模块当硬依赖
- 关联功能：待逐功能拆分后绑定本模块Feature ID
- 完成证据：[工具配置专项报告](../../../../../.tinadec_dev/reports/2026-10-08-tools-settings.zh-CN.md)，含真实包读取与最终77项市场/Skills API回归。

**问题与目的**

历史空 provider 已由统一技能目录替代。ISkillProvider 提供共享资源兼容读取，IToolSkillCatalog 与 Context 按项目/Agent 精确选择共享及项目包；设置与运行共用 WorkspaceSkillDiscovery。

**验收条件**

- [x] 定义并实现技能 ID、来源、范围、启禁及缺失语义。
- [x] 已安装/可用技能可按 agent/工作区范围列出与读取，禁用技能不进入模型输入。
- [x] 与 Context 工作区技能读取共享明确所有者，避免重复发现和重复注入。
- [x] 市场安装结果可追溯到 provider 可见的对应技能，写入仍走受控安装路径。

**2026-10-08 验收证据**：46 项 Skills 测试通过，包含共享资产/不可变版本、同名覆盖、精确绑定、428/412及并发 Agent 索引；模型提示回归通过。实现及三平台验证边界见[专项报告](../../../../../.tinadec_dev/reports/2026-10-08-tools-settings.zh-CN.md)。CORE-SKILLS-001 全模块审计不随此任务完成。

**初始证据**

- [TinadecCore/Skills/SkillsModuleRegistrar.cs:43](../../../../../TinadecCore/Skills/SkillsModuleRegistrar.cs#L43)
- [TinadecCore/Abstractions/Ports/ISkillProvider.cs](../../../../../TinadecCore/Abstractions/Ports/ISkillProvider.cs)
- [TinadecCore/Context/ContextModuleRegistrar.cs:526](../../../../../TinadecCore/Context/ContextModuleRegistrar.cs#L526)

<a id="core-skills-102"></a>

### CORE-SKILLS-102 Skills 包发现、审批、安装与 Agent 调用闭环

- 类型：实现
- 状态：已完成
- 优先级：P1
- 主责模块：CORE-SKILLS
- 前置依赖：CORE-SKILLS-101、CORE-TOOLS-101、TOOLS-MCP-102
- 范围：共享/项目完整包、固定提交 GitHub 来源、预览与审批、包文件/哈希/可用状态、项目准入快照、删除与二进制资产边界。
- 完成证据：[实施报告](../../../../../.tinadec_dev/reports/2026-10-09-skills-management.zh-CN.md)、[运行时证据](../../../../../.tinadec_dev/evidence/2026-10-09-skills-management/runtime-tests.txt)

**状态说明**：共享无项目安装、更新保留附件、项目包治理写入与工具能力索引已接线；本地导入与市场安装统一为审批后落盘。市场 76/76、Skills 27/27、联合 116/116 通过；PostgreSQL 实库及非 Windows 沙箱保持边界。

**验收条件**

- [x] 固定提交来源、共享无项目预览、完整包清单、摘要及实际可用状态已接线。
- [x] Agent 索引与实际读取能力一致，项目准入保留完整包快照，追加写授权拒绝只读根重叠。
- [x] 市场完整测试及项目删除回归最终通过（76/76、27/27、116/116）；PostgreSQL 实库和 POSIX 集成另行验证。

**2026-11-11 补充**：内置技能（4 个嵌入包）、分层发现（项目/用户/`.agents` 兼容根）、同名遮蔽诊断、`load_skill` 渐进披露与只读保护已落地；证据见[扩展层报告](../../../../../.tinadec_dev/reports/2026-11-11-extension-layer.zh-CN.md)。CORE-SKILLS-001 的全模块审计仍未因此完成；内置技能“复制到项目”已接 `POST /tools/skills/{id}/copy-to-project` 与设置页按钮。
