> **⚠️ 已归档规格（未实现或已过时）**
> 本文描述的功能多数**在代码中不存在**（例如 .trae/specs 中标注"已完成 ✅"的能力无任何源码/测试对应）。仅作历史记录，请勿据此判断当前能力。

- [x] `apps/desktop/scripts/dev.mjs` 存在且使用 Node.js child_process 管理子进程
- [x] `npm run dev:desktop` 能在 Windows 上启动 Vite 开发服务器
- [x] Vite 就绪后 Electron 进程自动启动
- [x] Electron 窗口正确加载 http://127.0.0.1:5173 的内容
- [x] Electron 退出时 Vite 进程被清理
- [ ] `start-vite.bat`、`start-electron.bat`、`dev.bat` 已被删除（用户选择保留）
- [x] `apps/desktop/package.json` 的 dev 脚本使用 `node scripts/dev.mjs`
