# Gateway 独# Gateway 独立抽取与 Rust Native 层删除 — 修复清单

> **生成日期:** 2026-07-09
> **范围:** Gateway（含内嵌工具实现）整体迁出 `apps/` → 删除 Rust native 层 → native-backed 工具执行改为 workspace tool 实例框架 → 清理失效引用 → 文档同步。
> **不在范围:** Tool layer 本身的设计/实现/stdio 协议/进程编排（由 Tool layer 团队负责）。Core→Gateway 工具调用链保留不动。Core 侧 `codex-rust` source 名与 `CodexCapabilityProvider` 保留不动（后续 C# 脚手架接驳时再处理）。

---

## 目标架构

```
Tinadec Core (AI Agents) ←→ Gateway ←→ Desktop (UI)
       ↑↓                        ↑↓
  Tool layer ←→ Things outside Tinadec
```

- Gateway 是 BFF 代理 + 内嵌工具执行宿主（本次随 gateway 一起迁出，工具实现暂不动）。
- native-backed 工具（原 spawn Rust binary）的执行入口改为获取 workspace 级 tool 实例（常驻 stdio，按 workspace 维度，设置 pwd）。具体实例由 Tool layer 提供，协议待 Tool layer 团队定义。
- Rust native 层整体废弃删除。

---

## 阶段 0：基线# Gateway 独立抽取与 Rust Native 层删除 — 修复清单

> **生成日期:** 2026-07-09
> **范围:** Gateway（含内嵌工具实现）整体迁出 `apps/` → 删除 Rust native 层 → native-backed 工具执行改为 workspace tool 实例框架 → 清理失效引用 → 文档同步。
> **不在范围:** Tool layer 本身的设计/实现/stdio 协议/进程编排（由 Tool layer 团队负责）。Core→Gateway 工具调用链保留不动。Core 侧 `codex-rust` source 名与 `CodexCapabilityProvider` 保留不动（后续 C# 脚手架接驳时再处理）。

---

## 目标架构

```
Tinadec Core (AI Agents) ←→ Gateway ←→ Desktop (UI)
       ↑↓                        ↑↓
  Tool layer ←→ Things outside Tinadec
```

- Gateway 是 BFF 代理 + 内嵌工具执行宿主（本次随 gateway 一起迁出，工具实现暂不动）。
- native-backed 工具（原 spawn Rust binary）的执行入口改为获取 workspace 级 tool 实例（常驻 stdio，按 workspace 维度，设置 pwd）。具体实例由 Tool layer 提供，协议待 Tool layer 团队定义。
- Rust native 层整体废弃删除。

---

## 阶段 0：基线验证（只读，零改动）

- [ ] 跑 `npm run build -w @tinadec/gateway` 确认 gateway 可构建
- [ ] 跑 `npm run test -w @tinadec/gateway` 确认测试通过
- [ ] 跑 `dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal`（Windows PowerShell 先清 `Version`/`Ice-Version` 环境变量）
- [ ] 记录 baseline 输出作为回归对照

---

### Gateway 独立抽取与 Rust Native 层删除 — 修复清单

> **生成日期:** 2026-07-09
> **范围:** Gateway（含内嵌工具实现）整体迁出 `apps/` → 删除 Rust native 层 → native-backed 工具执行改为 workspace tool 实例框架 → 清理失效引用 → 文档同步。
> **不在范围:** Tool layer 本身的设计/实现/stdio 协议/进程编排（由 Tool layer 团队负责）。Core→Gateway 工具调用链保留不动。Core 侧 `codex-rust` source 名与 `CodexCapabilityProvider` 保留不动（后续 C# 脚手架接驳时再处理）。

---

## 目标架构

```
Tinadec Core (AI Agents) ←→ Gateway ←→ Desktop (UI)
       ↑↓                        ↑↓
  Tool layer ←→ Things outside Tinadec
```

- Gateway 是 BFF 代理 + 内嵌工具执行宿主（本次随 gateway 一起迁出，工具实现暂不动）。
- native-backed 工具（原 spawn Rust binary）的执行入口改为获取 workspace 级 tool 实例（常驻 stdio，按 workspace 维度，设置 pwd）。具体实例由 Tool layer 提供，协议待 Tool layer 团队定义。
- Rust native 层整体废弃删除。

---

## 阶段 0：基线验证（只读，零改动）

- [ ] 跑 `npm run build -w @tinadec/gateway` 确认 gateway 可构建
- [ ] 跑 `npm run test -w @tinadec/gateway` 确认测试通过
- [ ] 跑 `dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal`（Windows PowerShell 先清 `Version`/`Ice-Version` 环境变量）
- [ ] 记录 baseline 输出作为回归对照

---

## 阶段 1：删除 Rust native 层

### 1.1 物理删除目录与配置

- [ ] 删除 `native/` 整个目录（含 `Cargo.toml`、`Cargo.lock`、`glue/*`、`rg/`、`AGENTS.md`、`UPSTREAM.md`、`rust-toolchain.toml`、`test_input.json`）
- [ ] 删除 `.cargo/` 目录（含 `config.toml`，硬编码 D 盘 rustup 路径）

### 1.2 根脚本与 .gitignore

- [ ] [package.json](file:///workspace/package.json) 第 15-16 行：删除 `build:native` 与 `build:native:release` 两个脚本
- [ ] [.gitignore](file:///workspace/.gitignore) 第 14、17、18 行：删除 `native/target/`、`native/codex-src/`、`/native/codex-src` 条目

### 1.3 Core 侧 Rust 探针

- [ ] [src/TinadecCore/Services/DoctorService.cs](file:///workspace/src/TinadecCore/Services/Doctor# Gateway 独立抽取与 Rust Native 层删除 — 修复清单

> **生成日期:** 2026-07-09
> **范围:** Gateway（含内嵌工具实现）整体迁出 `apps/` → 删除 Rust native 层 → native-backed 工具执行改为 workspace tool 实例框架 → 清理失效引用 → 文档同步。
> **不在范围:** Tool layer 本身的设计/实现/stdio 协议/进程编排（由 Tool layer 团队负责）。Core→Gateway 工具调用链保留不动。Core 侧 `codex-rust` source 名与 `CodexCapabilityProvider` 保留不动（后续 C# 脚手架接驳时再处理）。

---

## 目标架构

```
Tinadec Core (AI Agents) ←→ Gateway ←→ Desktop (UI)
       ↑↓                        ↑↓
  Tool layer ←→ Things outside Tinadec
```

- Gateway 是 BFF 代理 + 内嵌工具执行宿主（本次随 gateway 一起迁出，工具实现暂不动）。
- native-backed 工具（原 spawn Rust binary）的执行入口改为获取 workspace 级 tool 实例（常驻 stdio，按 workspace 维度，设置 pwd）。具体实例由 Tool layer 提供，协议待 Tool layer 团队定义。
- Rust native 层整体废弃删除。

---

## 阶段 0：基线验证（只读，零改动）

- [ ] 跑 `npm run build -w @tinadec/gateway` 确认 gateway 可构建
- [ ] 跑 `npm run test -w @tinadec/gateway` 确认测试通过
- [ ] 跑 `dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal`（Windows PowerShell 先清 `Version`/`Ice-Version` 环境变量）
- [ ] 记录 baseline 输出作为回归对照

---

## 阶段 1：删除 Rust native 层

### 1.1 物理删除目录与配置

- [ ] 删除 `native/` 整个目录（含 `Cargo.toml`、`Cargo.lock`、`glue/*`、`rg/`、`AGENTS.md`、`UPSTREAM.md`、`rust-toolchain.toml`、`test_input.json`）
- [ ] 删除 `.cargo/` 目录（含 `config.toml`，硬编码 D 盘 rustup 路径）

### 1.2 根脚本与 .gitignore

- [ ] [package.json](file:///workspace/package.json) 第 15-16 行：删除 `build:native` 与 `build:native:release` 两个脚本
- [ ] [.gitignore](file:///workspace/.gitignore) 第 14、17、18 行：删除 `native/target/`、`native/codex-src/`、`/native/codex-src` 条目

### 1.3 Core 侧 Rust 探针

- [ ] [src/TinadecCore/Services/DoctorService.cs](file:///workspace/src/TinadecCore/Services/DoctorService.cs) 第 16-17 行：删除 `cargo`、`rustc` 两个 `Probe(...)` 调用（R# Gateway 独立抽取与 Rust Native 层删除 — 修复清单

> **生成日期:** 2026-07-09
> **范围:** Gateway（含内嵌工具实现）整体迁出 `apps/` → 删除 Rust native 层 → native-backed 工具执行改为 workspace tool 实例框架 → 清理失效引用 → 文档同步。
> **不在范围:** Tool layer 本身的设计/实现/stdio 协议/进程编排（由 Tool layer 团队负责）。Core→Gateway 工具调用链保留不动。Core 侧 `codex-rust` source 名与 `CodexCapabilityProvider` 保留不动（后续 C# 脚手架接驳时再处理）。

---

## 目标架构

```
Tinadec Core (AI Agents) ←→ Gateway ←→ Desktop (UI)
       ↑↓                        ↑↓
  Tool layer ←→ Things outside Tinadec
```

- Gateway 是 BFF 代理 + 内嵌工具执行宿主（本次随 gateway 一起迁出，工具实现暂不动）。
- native-backed 工具（原 spawn Rust binary）的执行入口改为获取 workspace 级 tool 实例（常驻 stdio，按 workspace 维度，设置 pwd）。具体实例由 Tool layer 提供，协议待 Tool layer 团队定义。
- Rust native 层整体废弃删除。

---

## 阶段 0：基线验证（只读，零改动）

- [ ] 跑 `npm run build -w @tinadec/gateway` 确认 gateway 可构建
- [ ] 跑 `npm run test -w @tinadec/gateway` 确认测试通过
- [ ] 跑 `dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal`（Windows PowerShell 先清 `Version`/`Ice-Version` 环境变量）
- [ ] 记录 baseline 输出作为回归对照

---

## 阶段 1：删除 Rust native 层

### 1.1 物理删除目录与配置

- [ ] 删除 `native/` 整个目录（含 `Cargo.toml`、`Cargo.lock`、`glue/*`、`rg/`、`AGENTS.md`、`UPSTREAM.md`、`rust-toolchain.toml`、`test_input.json`）
- [ ] 删除 `.cargo/` 目录（含 `config.toml`，硬编码 D 盘 rustup 路径）

### 1.2 根脚本与 .gitignore

- [ ] [package.json](file:///workspace/package.json) 第 15-16 行：删除 `build:native` 与 `build:native:release` 两个脚本
- [ ] [.gitignore](file:///workspace/.gitignore) 第 14、17、18 行：删除 `native/target/`、`native/codex-src/`、`/native/codex-src` 条目

### 1.3 Core 侧 Rust 探针

- [ ] [src/TinadecCore/Services/DoctorService.cs](file:///workspace/src/TinadecCore/Services/DoctorService.cs) 第 16-17 行：删除 `cargo`、`rustc` 两个 `Probe(...)` 调用（Rust 不再需要）

### 1.4 Gateway codeTools.ts 删除 native spawn 逻辑