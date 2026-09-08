> **⚠️ 已归档规格（未实现或已过时）**
> 本文描述的功能多数**在代码中不存在**（例如 .trae/specs 中标注"已完成 ✅"的能力无任何源码/测试对应）。仅作历史记录，请勿据此判断当前能力。

# 粘合 Codex Rust 工具到运行时 Spec

## Why

Rust 胶水层代码（`tinadec-code-native` binary、`core-cdylib`、`codex-apply-patch-lite`、`codex-exec-server-shim`）已经由 Codex 编写完成，且 Gateway 的 `codeTools.ts` 已实现 `tryExecuteNativeTool` 调用路径。但当前 binary 无法在 Windows 上编译运行（缺少 MSVC linker / dlltool），且端到端的胶水链路尚未打通验证。需要修复编译环境、确保 binary 可执行、验证 Gateway→Native→Rust 的完整调用链。

## 当前状态审查

### 已完成 ✅
1. **Rust 胶水代码已编写**：
   - `native/glue/code-native/src/main.rs` — 完整的 stdin/stdout JSON 协议，支持 `execute` 和 `version` 命令
   - `search_files` — 调用 `codex-file-search`，完整实现
   - `apply_patch` — 调用 `codex-apply-patch`，含审批门控（approval_id 为空则 blocked）
   - `sandbox_exec` — 返回 `unsupported` 状态，预留了 Windows/Unix 分支
   - `review_format` — 占位 stub
   - `native/glue/core-cdylib/src/lib.rs` — FFI cdylib，`tinadec_guardian_check` 守门函数
   - `native/glue/codex-apply-patch-lite/` — 从 Codex 源码精简的 apply_patch 库
   - `native/glue/codex-exec-server-shim/` — `ExecutorFileSystem` trait + `LocalFileSystem` 实现

2. **Gateway 胶水代码已编写**：
   - `codeTools.ts` — `tryExecuteNativeTool` 通过 `spawn` 调用 binary，stdin 传 JSON，读 stdout 解析
   - `resolveNativeBinary` — 支持 `TINADEC_CODE_NATIVE_BIN` 环境变量和 `native/target/{debug,release}/` 自动发现
   - `nativeRuntimePath` — 拼接 Rust toolchain 路径到 PATH（但硬编码了特定路径）

3. **Core 层胶水代码已编写**：
   - `CodeToolClient.cs` — 通过 HttpClient 调 Gateway 的 Code 工具端点
   - `ToolRegistryService.cs` — 4 个工具注册，Source="code"，端点指向 `/api/v1/code/tools/*/execute`
   - `ToolExecutionService.cs` — 审批门控 + 调用 CodeToolClient + 事件发布

### 未完成 ❌
1. **Rust binary 无法编译**：当前默认 toolchain 是 `stable-x86_64-pc-windows-gnu`，缺少 `dlltool.exe`；MSVC toolchain 缺少 `link.exe`
2. **端到端链路未验证**：Core→Gateway→Native→Rust 的完整调用链从未跑通
3. **`nativeRuntimePath` 硬编码**：路径写死了特定用户的 Rust 安装位置
4. **`core-cdylib` 未被任何代码引用**：FFI 守门函数写好了但没人调用
5. **Gateway 的审批工具不走 native**：`codeTools.ts` 第 67 行 `if (!spec.requiresApproval)` 导致 `apply_patch` 和 `sandbox_exec` 永远走 stub 而不尝试 native

## What Changes

- 修复 Rust 编译环境：使用 `stable-x86_64-pc-windows-gnullvm` toolchain（已有）或安装 MSVC Build Tools
- 修复 `codeTools.ts` 的审批工具调用逻辑：即使需要审批，也应尝试 native 执行（在 approval_id 存在时）
- 修复 `nativeRuntimePath` 硬编码路径：改为动态发现 Rust toolchain
- 编译 `tinadec-code-native` binary 并验证 `version` 命令
- 端到端验证：Gateway→Native→Rust 的 search_files 调用链

## Impact

- Affected specs: Code 层工具执行链路
- Affected code: `apps/gateway/src/codeTools.ts`、`native/` 编译配置

## ADDED Requirements

### Requirement: Rust Native Binary 可编译执行

系统 SHALL 能在 Windows 上成功编译 `tinadec-code-native` binary，且 `tinadec-code-native version` 返回有效 JSON。

#### Scenario: 编译成功
- **WHEN** 在 `native/` 目录执行 `cargo build --bin tinadec-code-native`
- **THEN** 编译成功，生成 `native/target/debug/tinadec-code-native.exe`

#### Scenario: version 命令
- **WHEN** 执行 `tinadec-code-native version`
- **THEN** 返回 JSON `{"name":"tinadec-code-native","version":"0.1.0","upstream":"codex-rust","upstream_commit":"..."}`

### Requirement: Gateway 正确调用 Native Binary

Gateway 的 `codeTools.ts` SHALL 对所有 4 个工具都尝试 native 执行路径，而非仅对不需要审批的工具。

#### Scenario: 审批工具的 native 调用
- **WHEN** Gateway 收到 `apply_patch` 的 execute 请求，且 `approval_id` 非空
- **THEN** SHALL 尝试调用 `tinadec-code-native execute`，将 approval_id 传入
- **AND** native binary 内部检查 approval_id，非空则执行，为空则返回 blocked

#### Scenario: 无 native binary 时的降级
- **WHEN** native binary 不存在
- **THEN** SHALL 降级到 stub 响应，不影响系统运行

### Requirement: 动态发现 Rust Toolchain 路径

`nativeRuntimePath` SHALL 动态发现 Rust toolchain 路径，而非硬编码。

#### Scenario: 通过 rustup 发现
- **WHEN** 系统需要设置 native 进程的 PATH
- **THEN** SHALL 通过 `rustup which cargo` 或类似机制动态发现 Rust toolchain bin 目录
- **OR** 使用 `RUSTUP_HOME`/`CARGO_HOME` 环境变量

### Requirement: 端到端调用链验证

系统 SHALL 验证 Core→Gateway→Native→Rust 的完整调用链。

#### Scenario: search_files 端到端
- **WHEN** 通过 Gateway 调用 `/api/v1/code/tools/search_files/execute`，传入 query
- **THEN** native binary 执行 Codex Rust file search
- **AND** 返回 `status: "native"` 的结果，包含搜索匹配

## MODIFIED Requirements

### Requirement: codeTools.ts 审批工具调用逻辑

原逻辑：`if (!spec.requiresApproval)` 才尝试 native。
修改为：始终尝试 native，由 native binary 内部根据 approval_id 决定执行还是 blocked。
