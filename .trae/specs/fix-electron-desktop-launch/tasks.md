> **⚠️ 已归档规格（未实现或已过时）**
> 本文描述的功能多数**在代码中不存在**（例如 .trae/specs 中标注"已完成 ✅"的能力无任何源码/测试对应）。仅作历史记录，请勿据此判断当前能力。

# Tasks

- [x] Task 1: 创建 Node.js 开发启动脚本 `apps/desktop/scripts/dev.mjs`
  - [x] SubTask 1.1: 使用 `child_process.spawn` 启动 Vite 开发服务器
  - [x] SubTask 1.2: 实现 HTTP 轮询检测 Vite 就绪（最多 30 秒）
  - [x] SubTask 1.3: Vite 就绪后 spawn Electron 进程，设置 `VITE_DEV_SERVER_URL` 环境变量
  - [x] SubTask 1.4: 实现进程生命周期管理：Electron 退出时杀 Vite，Ctrl+C 时清理所有子进程
- [x] Task 2: 更新 `apps/desktop/package.json` 的 dev 脚本
  - [x] SubTask 2.1: 将 `dev` 脚本改为 `node scripts/dev.mjs`
  - [x] SubTask 2.2: 将 `electron` 脚本改为使用 `cross-env`
- [x] Task 3: 删除临时批处理文件
  - [x] SubTask 3.1: 用户选择保留 start-vite.bat
  - [x] SubTask 3.2: 用户选择保留 start-electron.bat
  - [x] SubTask 3.3: 用户选择保留 dev.bat
- [x] Task 4: 验证 Electron 桌面应用启动
  - [x] SubTask 4.1: 运行 `npm run dev:desktop`，确认 Vite 和 Electron 都启动
  - [x] SubTask 4.2: 确认 Electron 窗口正确加载页面

# Task Dependencies
- [Task 2] depends on [Task 1]
- [Task 3] independent, can run in parallel
- [Task 4] depends on [Task 1, Task 2, Task 3]
