# Market / 市场：TODO

模块ID：`APP-MARKET` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本文件是该模块任务的唯一编辑入口；总TODO由本文件生成。任务ID长期稳定，完成或取消后保留记录；每项任务记录工作范围与验收条件。

状态：待核查 / 未开始 / 待方案 / 进行中 / 阻塞 / 待验收 / 已完成 / 不做 / 已被替代。优先级是初始建议，可在逐模块分析后调整。

<a id="app-market-001"></a>

### APP-MARKET-001 完成 Market / 市场 的逐功能审计与模块图精化

- 类型：核查
- 状态：待核查
- 优先级：P2
- 主责模块：APP-MARKET
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

- [apps/desktop/src/pages/MarketPage.vue](../../../../../apps/desktop/src/pages/MarketPage.vue)

<a id="app-market-101"></a>

### APP-MARKET-101 验收真实市场来源、安装提案与结果回写

- 类型：验收
- 状态：未开始
- 优先级：P2
- 主责模块：APP-MARKET
- 前置依赖：未细化；开工前在此列出实际Task ID，不能把全部关联模块当硬依赖
- 关联功能：待逐功能拆分后绑定本模块Feature ID
- 完成证据：未产生；本轮仅建立任务与源码基线

**问题与目的**

页面与专项路由稳定性已有实现，但完整市场业务不能由布局验证代替。

**验收条件**

- [ ] 真实来源添加/刷新/筛选/分页得到正确目录
- [ ] 详情→安装提案→批准/执行→安装状态形成可追踪闭环
- [ ] 离线、坏源及读取期间离开页面提供准确状态且无重复请求
- [ ] Home/Market 往返保留用户布局与当前筛选状态

**初始证据**

- [apps/desktop/src/controllers/MarketController.ts](../../../../../apps/desktop/src/controllers/MarketController.ts)
- [apps/desktop/src/pages/MarketPage.vue](../../../../../apps/desktop/src/pages/MarketPage.vue)

**历史任务映射**

- [docs/market-route-stability-2026-10-05.zh-CN.md](../../../../market-route-stability-2026-10-05.zh-CN.md)：验证与诚实边界；只作来源，不继承完成勾选

**2026-11-11 补充**：`/market/skills` 与 `/market/mcp` 已作为独立 UIE 页面接入（各自 preset/repair/路由/侧栏入口），市场按 revision 指纹每 10s 热刷新。APP-MARKET-101 的真实来源闭环仍未开始；分页与 ACP 列表未补。证据见[扩展层报告](../../../../../.tinadec_dev/reports/2026-11-11-extension-layer.zh-CN.md)。
