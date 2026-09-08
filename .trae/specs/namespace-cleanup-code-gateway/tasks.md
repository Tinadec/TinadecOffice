> **⚠️ 已归档规格（未实现或已过时）**
> 本文描述的功能多数**在代码中不存在**（例如 .trae/specs 中标注"已完成 ✅"的能力无任何源码/测试对应）。仅作历史记录，请勿据此判断当前能力。

# Tasks

- [x] Task 1: 重命名 Gateway 包名
  - [x] SubTask 1.1: `apps/gateway/package.json` 的 `name` 从 `@tinadec/code` 改为 `@tinadec/gateway`
- [x] Task 2: 更新根 package.json 的脚本和引用
  - [x] SubTask 2.1: `dev:code` 脚本改为 `dev:gateway`，workspace 引用从 `@tinadec/code` 改为 `@tinadec/gateway`
  - [x] SubTask 2.2: `concurrently` 标签从 `core,code,desktop` 改为 `core,gateway,desktop`
- [x] Task 3: 更新 Gateway stub 消息措辞
  - [x] SubTask 3.1: `codeTools.ts` 中 TOOL_SPECS 的 summary 字段，将 "Code-layer" 改为 "Gateway proxy"
- [x] Task 4: 验证 npm workspace 引用正确
  - [x] SubTask 4.1: 执行 `npm ls -w @tinadec/gateway` 确认 workspace 可解析
  - [x] SubTask 4.2: 执行 `npm run dev:gateway` 确认 Gateway 可启动

# Task Dependencies
- [Task 2] depends on [Task 1] ✅
- [Task 4] depends on [Task 1, Task 2, Task 3] ✅
- [Task 3] 可与 [Task 1, Task 2] 并行 ✅
