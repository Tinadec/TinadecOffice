> **⚠️ 已归档规格（未实现或已过时）**
> 本文描述的功能多数**在代码中不存在**（例如 .trae/specs 中标注"已完成 ✅"的能力无任何源码/测试对应）。仅作历史记录，请勿据此判断当前能力。

# Tasks

- [x] Task 1: 修复 Rust 编译环境，使 tinadec-code-native 可编译
  - [x] SubTask 1.1: 确定可用的 Rust toolchain — 使用 msvc toolchain
  - [x] SubTask 1.2: 创建 `native/rust-toolchain.toml` 指定 msvc toolchain
  - [x] SubTask 1.3: 修复 `.cargo/config.toml` 中 msvc target 的 linker 配置（移除指向不存在 gnullvm lld-link.exe 的硬编码）
  - [x] SubTask 1.4: 修复 `codex-apply-patch-lite` 的 edition 从 2021 升级到 2024（let chains 语法需要）
  - [x] SubTask 1.5: 执行 `cargo build --bin tinadec-code-native`，确认编译成功
  - [x] SubTask 1.6: 执行 `tinadec-code-native version`，确认返回有效 JSON
- [x] Task 2: 修复 codeTools.ts 审批工具调用逻辑
  - [x] SubTask 2.1: 移除 `if (!spec.requiresApproval)` 的条件判断，使所有工具都尝试 native 路径
  - [x] SubTask 2.2: 确保 native 调用失败时正确降级到 stub 响应
- [x] Task 3: 修复 nativeRuntimePath 硬编码路径
  - [x] SubTask 3.1: 替换硬编码路径为动态发现逻辑（使用 CARGO_HOME/RUSTUP_HOME）
  - [x] SubTask 3.2: 添加合理的 fallback（native/target/debug + native/target/release + process.env.PATH）
- [x] Task 4: 端到端验证 Gateway→Native→Rust 调用链
  - [x] SubTask 4.1: 启动 Gateway，调用 `/api/v1/code/tools/search_files/execute`，验证返回 `status: "native"`
  - [x] SubTask 4.2: 调用 `/api/v1/code/tools/apply_patch/execute`（无 approval_id），验证返回 `status: "blocked"`
  - [x] SubTask 4.3: 直接通过 Node.js spawn 验证 search_files native 执行返回完整搜索结果

# Task Dependencies
- [Task 2, Task 3] 可并行 ✅ 已完成
- [Task 4] depends on [Task 1, Task 2, Task 3] ✅ 已完成
- [Task 1] ✅ 已完成（修复 .cargo/config.toml + edition 2024 + MSVC Build Tools）
