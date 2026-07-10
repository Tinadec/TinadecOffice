# Gateway 抽取与 Rust 删除 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 gateway 含内嵌工具抽出为顶层独立组件，删除 Rust native 层，用临时安全补丁兜底 list_directory，清理失效引用，保证 MVP 两周内可跑。

**Architecture:** Gateway 仍是 BFF + 内嵌工具宿主，本次随工具整体迁出 apps/。Rust native 层整体删除。删 Rust 后 list_directory 的 native 执行链断裂，用 Node `fs.readdir`（零进程派生、零 RCE 面、跨平台）+ 严格路径白名单兜底。gateway terminal 工具删除（PTY 本在 Desktop）。Core→Gateway 工具调用链保留不动（工具仍嵌 gateway）。Tool layer stdio 接驳为后续任务（依赖 cjt 的 C# Tool layer 就绪）。

**Tech Stack:** Elysia TypeScript (gateway, ESM + tsx + node:test), .NET 10 C# (Core, xUnit), Vue 3 + Vitest (Desktop)

**环境说明：** 本机为 Linux 沙箱；项目 Windows-first，根 npm 脚本用 PowerShell 包裹 dotnet。Linux 上直接用 `dotnet test`/`dotnet build`，跳过 powershell 包裹。Windows 上用 `Remove-Item Env:Version/Ice-Version` 后跑 dotnet。

**参考文档：** [docs/gateway-extraction-and-tool-bridge.md](file:///workspace/docs/gateway-extraction-and-tool-bridge.md)（完整修复清单与决策记录）

---

## File Structure

| 文件 | 操作 | 职责 |
|------|------|------|
| `native/` | 删除整个目录 | Rust workspace，废弃 |
| `.cargo/config.toml` | 删除 | Rust linker 配置 |
| `package.json` | 修改 | 删 build:native 脚本、workspaces 加 gateway、dev/build/test 路径更新 |
| `.gitignore` | 修改 | 删 native/target/、native/codex-src/ |
| `src/TinadecCore/Services/DoctorService.cs` | 修改 | 删 cargo/rustc probe |
| `apps/gateway/src/codeTools.ts` | 修改 | 删 terminal、删 native 函数、加 list_directory 补丁、标 DEFERRED |
| `apps/gateway/src/codeTools.test.ts` | 新建 | list_directory 补丁测试 |
| `apps/gateway/src/coreClient.test.ts` | 修改 | tool id 列表去掉 terminal（若存在） |
| `apps/desktop/src/composables/useTerminal.ts` | 修改 | 删 gateway fetch 分支，直连 IPC |
| `apps/desktop/src/components/PreviewBrowserPanel.vue` | 修改 | 硬编码 URL 改 preload |
| `apps/gateway/` → `gateway/` | 移动 | 物理迁出 apps/ |
| `CLAUDE.md` / `AGENTS.md` / `docs/*` | 修改 | 删 Rust 描述、路径更新 |

---

## 分组说明

- **Task 1-11：MVP 路径（现在执行）** — 删 Rust、删 terminal、list_directory 补丁、gateway 迁出、desktop/desktop/docs 清理、验证
- **Task 12-14：后续接驳（Tool layer 就绪后执行）** — toolLayerBridge、native 工具接驳、bash_environment/code_editor 迁移。依赖 cjt 的 C# Tool layer，本次不执行

---

## Task 1: 基线验证（只读）

**Files:** 无修改

- [ ] **Step 1: 跑 gateway 测试确认 baseline**

Run: `cd /workspace && npm run test -w @tinadec/gateway`
Expected: 全绿。记录通过数作为回归对照。

- [ ] **Step 2: 跑 Core 测试确认 baseline**

Run (Linux): `cd /workspace && dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal`
Expected: 全绿。若环境无 dotnet，跳过并记录。

- [ ] **Step 3: 确认 native/ 目录存在并记录其内容**

Run: `ls /workspace/native/`
Expected: 看到 Cargo.toml、glue/、rg/、AGENTS.md 等，确认删除目标。

无 commit。

---

## Task 2: 删除 Rust native 层

**Files:**
- Delete: `native/`（整个目录）
- Delete: `.cargo/config.toml`
- Modify: `package.json:15-16`（删 build:native 脚本）
- Modify: `.gitignore:14,17-18`（删 native 条目）
- Modify: `src/TinadecCore/Services/DoctorService.cs:16-17`（删 cargo/rustc probe）

- [ ] **Step 1: 删除 native/ 目录和 .cargo/**

Run: `cd /workspace && rm -rf native/ .cargo/`
Expected: 目录消失。

- [ ] **Step 2: 删除 package.json 的 build:native 脚本**

删除 [package.json:15-16](file:///workspace/package.json#L15) 两行：`"build:native"` 和 `"build:native:release"`。注意第 14 行 build 脚本末尾的逗号——删 15-16 后若 14 行变成 scripts 块最后一行需去掉其尾逗号。

修改后 scripts 块应从 `build` 直接到 `test`，无尾逗号问题。用 Read 确认改动后 JSON 合法。

- [ ] **Step 3: 删除 .gitignore 的 native 条目**

删除这三行：`native/target/`、`native/codex-src/`、`/native/codex-src`。

- [ ] **Step 4: 删除 DoctorService 的 cargo/rustc probe**

读取 [DoctorService.cs:14-20](file:///workspace/src/TinadecCore/Services/DoctorService.cs#L14)，删除这两行 Probe 调用：
```csharp
Probe("cargo", "--version", "Rust/Cargo is needed to build Codex Rust native glue.");
Probe("rustc", "--version", "Rustc is needed to compile Core and Codex native glue crates.");
```

- [ ] **Step 5: 验证 JSON 合法 + Core 编译**

Run: `cd /workspace && node -e "JSON.parse(require('fs').readFileSync('package.json','utf8')); console.log('package.json OK')"`
Expected: `package.json OK`

Run (Linux): `cd /workspace && dotnet build src/TinadecCore/TinadecCore.csproj -v minimal`
Expected: 编译成功（DoctorService 改动不破坏编译）。

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: remove rust native layer

- delete native/ workspace and .cargo/ config
- drop build:native scripts from root package.json
- remove native/ entries from .gitignore
- remove cargo/rustc probes from DoctorService"
```

---

## Task 3: 删除 gateway terminal 工具

**Files:**
- Modify: `apps/gateway/src/codeTools.ts`（删 terminal spec、执行分支、实现函数）
- Modify: `apps/gateway/src/coreClient.test.ts`（若 tool id 列表含 terminal 则同步）

**背景：** gateway terminal 工具全是 stub（[codeTools.ts:697-796](file:///workspace/apps/gateway/src/codeTools.ts#L697) 注释"等待Native层集成"），真正 PTY 跑在 Desktop Electron 主进程。

- [ ] **Step 1: 删除 TOOL_SPECS 中的 terminal 条目**

删除 [codeTools.ts:302-309](file:///workspace/apps/gateway/src/codeTools.ts#L302) 的 `terminal: { ... }` 对象（含尾逗号）。

- [ ] **Step 2: 删除 executeCodeTool 中的 terminal 分支**

删除 [codeTools.ts:467-469](file:///workspace/apps/gateway/src/codeTools.ts#L467)：
```ts
  if (spec.id === 'terminal') {
    return executeTerminal(spec, request, args);
  }
```

- [ ] **Step 3: 删除 terminal 实现函数**

删除 [codeTools.ts:697-862](file:///workspace/apps/gateway/src/codeTools.ts#L697) 整段：`executeTerminal`、`getDefaultShell`、`getAvailableShells`。

- [ ] **Step 4: 检查 coreClient.test.ts 是否引用 terminal**

读取 [coreClient.test.ts:15-31](file:///workspace/apps/gateway/src/coreClient.test.ts#L15)。当前断言列表**不含** `terminal`（已确认），无需改动。若实际列表含 terminal 则从数组删除。

- [ ] **Step 5: 跑 gateway 测试**

Run: `cd /workspace && npm run test -w @tinadec/gateway`
Expected: 全绿（terminal 不在断言列表中，删除不影响）。

- [ ] **Step 6: Commit**

```bash
git add apps/gateway/src/codeTools.ts
git commit -m "refactor(gateway): remove stub terminal tool

PTY runs in Desktop Electron main process (node-pty); gateway terminal
tool was entirely stubbed. Desktop now connects via Electron IPC directly."
```

---

## Task 4: 删除 gateway native 执行函数（Rust 残留）

**Files:**
- Modify: `apps/gateway/src/codeTools.ts`（删 native 属性、native 函数）

- [ ] **Step 1: 删除 CodeToolSpec.nativeBacked 属性**

删除 [codeTools.ts:54](file:///workspace/apps/gateway/src/codeTools.ts#L54)：
```ts
  nativeBacked?: boolean;
```

- [ ] **Step 2: 删除 TOOL_SPECS 中所有 nativeBacked: true 标记**

全文搜索 `nativeBacked: true`，逐个删除该属性行（9 处：search_files/glob_search/read_file/list_directory/grep_content/sandbox_exec/apply_patch/review_format，及已删的 terminal）。保留 spec 对象本身。

- [ ] **Step 3: 删除 executeCodeTool 的 native 分支**

删除 [codeTools.ts:447-452](file:///workspace/apps/gateway/src/codeTools.ts#L447)：
```ts
  if (spec.nativeBacked) {
    const nativeResult = await tryExecuteNativeTool(spec, request);
    if (nativeResult) {
      return nativeResult;
    }
  }
```

- [ ] **Step 4: 删除 fallback 中的 native_runtime 标记**

修改 [codeTools.ts:479](file:///workspace/apps/gateway/src/codeTools.ts#L479)，把：
```ts
      spec.nativeBacked ? 'native_runtime: pending' : 'code_suite: metadata'
```
改为：
```ts
      'code_suite: metadata'
```

- [ ] **Step 5: 删除 status 联合类型中的 'native'**

修改 [codeTools.ts:19](file:///workspace/apps/gateway/src/codeTools.ts#L19)：
```ts
  status: 'native' | 'completed' | 'stubbed' | 'blocked' | 'failed';
```
改为：
```ts
  status: 'completed' | 'stubbed' | 'blocked' | 'failed';
```

- [ ] **Step 6: 删除 native 执行函数**

删除 [codeTools.ts:3066-3162](file:///workspace/apps/gateway/src/codeTools.ts#L3066) 整段：`tryExecuteNativeTool`、`nativeRuntimePath`、`resolveNativeBinary`。

- [ ] **Step 7: 删除 fallbackData 中的 native 相关注释（若有）**

检查 fallbackData（[codeTools.ts:3036-3064](file:///workspace/apps/gateway/src/codeTools.ts#L3036)）无 native 引用，无需改动。

- [ ] **Step 8: 跑 gateway 测试确认编译通过**

Run: `cd /workspace && npm run test -w @tinadec/gateway`
Expected: 全绿。若有测试引用 nativeBacked 或 tryExecuteNativeTool，更新之（搜索确认无）。

- [ ] **Step 9: Commit**

```bash
git add apps/gateway/src/codeTools.ts
git commit -m "refactor(gateway): remove rust native execution path

- drop nativeBacked spec flag and 'native' status
- delete tryExecuteNativeTool/nativeRuntimePath/resolveNativeBinary
- list_directory and other native-backed tools now fall through to stub
  (list_directory temporary hack added in next task)"
```

---

## Task 5: 实现 list_directory 临时补丁（MVP 关键）

**Files:**
- Create: `apps/gateway/src/codeTools.test.ts`（list_directory 补丁测试）
- Modify: `apps/gateway/src/codeTools.ts`（加补丁函数 + executeCodeTool 分支）

**设计决策：** 用 Node `fs.readdir({withFileTypes:true})` 而非 `spawn('ls')`。理由：①项目 Windows-first，Windows 无原生 `ls` 二进制，`spawn('ls',{shell:false})` 会失败；②`fs.readdir` 零进程派生 = 零 RCE 注入面，比 spawn 更安全；③跨平台。保留路径白名单 + workspace 逃逸校验作为深度防御。用户"直调 ls"的意图是"文件浏览"，fs.readdir 更安全地达成该目标。

- [ ] **Step 1: 写失败测试 — list_directory 基本列出**

创建 `apps/gateway/src/codeTools.test.ts`：

```ts
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { executeCodeTool } from './codeTools.js';

test('list_directory lists entries with directories first', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-ls-'));
  try {
    await writeFile(path.join(cwd, 'file-a.txt'), 'a');
    await writeFile(path.join(cwd, 'file-b.txt'), 'b');
    await mkdir(path.join(cwd, 'subdir'));

    const result = await executeCodeTool('list_directory', { cwd, arguments: {} });
    assert.equal(result?.status, 'completed');
    const entries = result?.data.entries as Array<{ name: string; is_directory: boolean }>;
    assert.ok(entries);
    assert.equal(entries.length, 3);
    // 目录在前
    assert.equal(entries[0].name, 'subdir');
    assert.equal(entries[0].is_directory, true);
    const names = entries.map((e) => e.name).sort();
    assert.deepEqual(names, ['file-a.txt', 'file-b.txt', 'subdir']);
  } finally {
    await rm(cwd, { recursive: true, force: true });
  }
});
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd /workspace && npm run test -w @tinadec/gateway`
Expected: FAIL — list_directory 返回 stubbed 而非 completed（补丁未实现）。

- [ ] **Step 3: 实现 executeListDirectoryViaLs 函数**

在 codeTools.ts 的 `executeCodeTool` 函数之前（约 L440），插入：

```ts
// FIXME: MVP TEMPORARY HACK - REPLACE WITH SANDBOX LISTDIR AFTER 2 WEEKS
// Tool layer (cjt) 缺文件系统访问；Rust 删后 native 链断。临时用 fs.readdir 兜底。
// 安全：零进程派生（无 RCE 面）+ 路径白名单 + workspace 逃逸校验。
// 替换时机：cjt 的 C# 沙箱 listdir 就绪后删除本函数，改走 toolLayerBridge。
const LIST_DIRECTORY_FORBIDDEN_CHARS = /[$`;&|<>()\n\r\*?\[\]\\]/;

async function executeListDirectoryViaLs(
  spec: CodeToolSpec,
  request: CodeToolExecuteRequest
): Promise<CodeToolExecuteResult> {
  const args = request.arguments ?? {};
  const requestedPath = stringArg(args, 'path') ?? '.';
  const showHidden = args['show_hidden'] === true;

  const cwd = request.cwd;
  if (!cwd) {
    return failedResult(spec, 'list_directory requires a cwd (workspace root).', args, ['list_directory:missing-cwd']);
  }

  if (LIST_DIRECTORY_FORBIDDEN_CHARS.test(requestedPath)) {
    return failedResult(spec, 'Path contains forbidden characters.', args, ['list_directory:rejected-metachar']);
  }

  const normalizedCwd = path.resolve(cwd);
  const resolvedPath = path.resolve(normalizedCwd, requestedPath);
  if (resolvedPath !== normalizedCwd && !resolvedPath.startsWith(normalizedCwd + path.sep)) {
    return failedResult(spec, 'Path escapes workspace root.', args, ['list_directory:rejected-escape']);
  }

  let entries: import('node:fs').Dirent[];
  try {
    entries = await readdir(resolvedPath, { withFileTypes: true });
  } catch (err) {
    return failedResult(spec, `Cannot read directory: ${(err as Error).message}`, args, ['list_directory:read-failed']);
  }

  if (!showHidden) {
    entries = entries.filter((e) => !e.name.startsWith('.'));
  }

  const mapped = entries.map((e) => ({ name: e.name, is_directory: e.isDirectory() }));
  mapped.sort((a, b) => {
    if (a.is_directory !== b.is_directory) return a.is_directory ? -1 : 1;
    return a.name.localeCompare(b.name);
  });

  const MAX_ENTRIES = 2000;
  const truncated = mapped.length > MAX_ENTRIES;
  const visible = truncated ? mapped.slice(0, MAX_ENTRIES) : mapped;

  return resultFor(spec, 'completed', `Listed ${mapped.length} entries in ${requestedPath}.`, {
    cwd: normalizedCwd,
    path: requestedPath,
    resolved_path: resolvedPath,
    show_hidden: showHidden,
    entries: visible,
    total_count: mapped.length,
    truncated
  }, ['list_directory:readdir', 'mvp-temporary-hack']);
}
```

在文件顶部 import 区（[codeTools.ts:3](file:///workspace/apps/gateway/src/codeTools.ts#L3)）添加：
```ts
import { readdir } from 'node:fs/promises';
```

- [ ] **Step 4: 在 executeCodeTool 中加 list_directory 分支**

在 [codeTools.ts:441](file:///workspace/apps/gateway/src/codeTools.ts#L441) `executeCodeTool` 函数内，`const args = request.arguments ?? {};` 之前插入：

```ts
  // FIXME: MVP TEMPORARY HACK - see executeListDirectoryViaLs
  if (spec.id === 'list_directory') {
    return executeListDirectoryViaLs(spec, request);
  }
```

- [ ] **Step 5: 跑测试确认通过**

Run: `cd /workspace && npm run test -w @tinadec/gateway`
Expected: PASS。

- [ ] **Step 6: 写测试 — 拒绝路径逃逸**

在 codeTools.test.ts 追加：

```ts
test('list_directory rejects path escape via ..', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-ls-'));
  try {
    const result = await executeCodeTool('list_directory', { cwd, arguments: { path: '../../../etc' } });
    assert.equal(result?.status, 'failed');
    assert.ok(result?.evidence.includes('list_directory:rejected-escape'));
  } finally {
    await rm(cwd, { recursive: true, force: true });
  }
});

test('list_directory rejects metacharacters in path', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-ls-'));
  try {
    const result = await executeCodeTool('list_directory', { cwd, arguments: { path: 'foo; rm -rf /' } });
    assert.equal(result?.status, 'failed');
    assert.ok(result?.evidence.includes('list_directory:rejected-metachar'));
  } finally {
    await rm(cwd, { recursive: true, force: true });
  }
});

test('list_directory rejects missing cwd', async () => {
  const result = await executeCodeTool('list_directory', { arguments: { path: '.' } });
  assert.equal(result?.status, 'failed');
  assert.ok(result?.evidence.includes('list_directory:missing-cwd'));
});

test('list_directory respects show_hidden flag', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-ls-'));
  try {
    await writeFile(path.join(cwd, '.hidden'), 'h');
    await writeFile(path.join(cwd, 'visible.txt'), 'v');

    const hidden = await executeCodeTool('list_directory', { cwd, arguments: { show_hidden: true } });
    const hiddenNames = (hidden?.data.entries as Array<{name:string}>).map((e) => e.name);
    assert.ok(hiddenNames.includes('.hidden'));

    const noHidden = await executeCodeTool('list_directory', { cwd, arguments: { show_hidden: false } });
    const noHiddenNames = (noHidden?.data.entries as Array<{name:string}>).map((e) => e.name);
    assert.ok(!noHiddenNames.includes('.hidden'));
  } finally {
    await rm(cwd, { recursive: true, force: true });
  }
});
```

- [ ] **Step 7: 跑全部测试**

Run: `cd /workspace && npm run test -w @tinadec/gateway`
Expected: 全绿（5 个 list_directory 测试 + 原有测试）。

- [ ] **Step 8: Commit**

```bash
git add apps/gateway/src/codeTools.ts apps/gateway/src/codeTools.test.ts
git commit -m "feat(gateway): temporary list_directory via fs.readdir (MVP hack)

FIXME: MVP TEMPORARY HACK - REPLACE WITH SANDBOX LISTDIR AFTER 2 WEEKS
Tool layer (cjt) lacks filesystem access; Rust native path removed.
Uses fs.readdir (zero process spawn = zero RCE surface, cross-platform)
with path whitelist + workspace escape validation as defense-in-depth.
Replace when cjt's C# sandbox listdir is ready."
```

---

## Task 6: 标记 DEFERRED 工具

**Files:**
- Modify: `apps/gateway/src/codeTools.ts`（在 4 个延后工具的 spec 声明处加 TODO 注释）

- [ ] **Step 1: 在 TOOL_SPECS 中为延后工具加 DEFERRED 标记**

在以下 4 个 spec 对象前加注释（[codeTools.ts:252-300](file:///workspace/apps/gateway/src/codeTools.ts#L252) 区域）：

```ts
  // TODO: DEFERRED - 待 Tool layer 补 scaffold 后迁移
  project_templates: { ... },
  // TODO: DEFERRED - 待 Tool layer 补 scaffold 后迁移
  project_template_scaffold: { ... },
  // TODO: DEFERRED - 待 Tool layer 定义 runtime probe 契约
  language_runtime_probe: { ... },
  // TODO: DEFERRED - 待 Tool layer 定义 debug session 契约
  debug_session: { ... },
  // TODO: DEFERRED - 待 Tool layer 补 git 能力后迁移
  git_worktree_manager: { ... },
```

- [ ] **Step 2: 跑 gateway 测试确认无破坏**

Run: `cd /workspace && npm run test -w @tinadec/gateway`
Expected: 全绿（仅加注释）。

- [ ] **Step 3: Commit**

```bash
git add apps/gateway/src/codeTools.ts
git commit -m "docs(gateway): mark deferred tools pending tool layer

project_templates/scaffold, git_worktree_manager, language_runtime_probe,
debug_session have no tool layer equivalent yet; marked DEFERRED for
migration after cjt's tool layer adds the capabilities."
```

---

## Task 7: Desktop useTerminal 直连 IPC + PreviewBrowserPanel 修复

**Files:**
- Modify: `apps/desktop/src/composables/useTerminal.ts:138-264`
- Modify: `apps/desktop/src/components/PreviewBrowserPanel.vue:16`

- [ ] **Step 1: 简化 loadShells 直连 IPC**

读取 [useTerminal.ts:138-174](file:///workspace/apps/desktop/src/composables/useTerminal.ts#L138)，将整个 `loadShells` 函数替换为：

```ts
async function loadShells(): Promise<void> {
  if (!isTerminalAvailable()) {
    availableShells.value = []
    return
  }
  try {
    const shells = await window.tinadec.terminal.getShells()
    availableShells.value = shells
    shellsLoaded.value = true
  } catch {
    availableShells.value = []
  }
}
```

删除 gateway fetch 分支。

- [ ] **Step 2: 简化 createTerminalInstance 直连 IPC**

读取 [useTerminal.ts:212-264](file:///workspace/apps/desktop/src/composables/useTerminal.ts#L212)，将 gateway fetch + fallback 块替换为直接 IPC：

```ts
    // 直连 Electron IPC（gateway terminal 工具已删除，PTY 本就在 Desktop）
    let result: { id: string; shell: string; title: string } | null = null
    if (isTerminalAvailable()) {
      const ipcResult = await window.tinadec.terminal.create({
        shell,
        args,
        cwd: options.cwd,
        cols: options.cols ?? 80,
        rows: options.rows ?? 24,
        title: options.title,
      })
      result = ipcResult
    }

    if (!result) {
      throw new Error('Electron terminal IPC not available')
    }
```

删除原 gateway fetch try/catch 和 `if (!result && isTerminalAvailable())` fallback 块。

- [ ] **Step 3: 修复 PreviewBrowserPanel 硬编码 URL**

读取 [PreviewBrowserPanel.vue:16](file:///workspace/apps/desktop/src/components/PreviewBrowserPanel.vue#L16)，将：
```ts
const GATEWAY_URL = 'http://localhost:48730/docs'
```
改为：
```ts
const gatewayDocsUrl = (window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730') + '/docs'
```
并更新模板中引用 `GATEWAY_URL` 处改为 `gatewayDocsUrl`（搜索确认引用点）。

- [ ] **Step 4: 跑 desktop 测试**

Run: `cd /workspace && npm run test -w @tinadec/desktop`
Expected: 全绿。若有 useTerminal 相关测试，更新之。

- [ ] **Step 5: Commit**

```bash
git add apps/desktop/src/composables/useTerminal.ts apps/desktop/src/components/PreviewBrowserPanel.vue
git commit -m "refactor(desktop): terminal direct IPC, fix gateway URL hardcode

- useTerminal: remove gateway fetch fallback, connect Electron IPC directly
  (gateway terminal tool removed; PTY always ran in Desktop main process)
- PreviewBrowserPanel: replace hardcoded localhost:48730 with preload gatewayUrl"
```

---

## Task 8: Gateway 物理迁移到顶层

**Files:**
- Move: `apps/gateway/` → `gateway/`
- Modify: `package.json:6-13`（workspaces + scripts）
- Move: `apps/gateway/AGENTS.md` → `gateway/AGENTS.md`（内容更新留 Task 10）

- [ ] **Step 1: git mv gateway**

Run: `cd /workspace && git mv apps/gateway gateway`
Expected: 目录移动，git 历史保留。

- [ ] **Step 2: 更新根 package.json workspaces**

修改 [package.json:6-8](file:///workspace/package.json#L6)：
```json
  "workspaces": [
    "apps/*",
    "gateway"
  ],
```

- [ ] **Step 3: 更新根 package.json 的 dev:gateway 脚本**

修改 [package.json:12](file:///workspace/package.json#L12)：
```json
    "dev:gateway": "npm run dev -w @tinadec/gateway",
```
保持不变（workspace 名 `@tinadec/gateway` 不变，仍可解析）。确认 `dev`/`build`/`test` 的 concurrently/workspace 命令无需改（它们用 `-w @tinadec/gateway` 而非路径）。

- [ ] **Step 4: 重装依赖确认 workspace 解析**

Run: `cd /workspace && npm install`
Expected: 无报错，`@tinadec/gateway` 仍解析到新路径。

- [ ] **Step 5: 跑 gateway 测试确认迁移后可运行**

Run: `cd /workspace && npm run test -w @tinadec/gateway`
Expected: 全绿。

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: move gateway to top-level (extract from apps/)

Gateway extracted as independent top-level workspace member while
retaining single-repo npm workspace dev experience."
```

---

## Task 9: Core codex-rust 最小清理验证

**Files:** 无修改（仅验证）

**说明：** 本次**保留** Core 侧 `codex-rust` source 名和 Core→Gateway 工具调用链（工具仍嵌 gateway 执行）。仅确认 Task 2 删 Rust 后 Core 编译/测试通过。source 名统一改名待 Tool layer 接驳稳定后单独立项。

- [ ] **Step 1: 跑 Core 测试确认无回归**

Run (Linux): `cd /workspace && dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal`
Expected: 全绿。Task 2 删 cargo/rustc probe 不影响 codex-rust source 断言（source 名未改）。

- [ ] **Step 2: 搜索确认无 Rust 二进制残留引用**

Run: `cd /workspace && rg -i "TINADEC_CODE_NATIVE_BIN|resolveNativeBinary|nativeRuntimePath|tryExecuteNativeTool" --type ts --type cs`
Expected: 无结果（gateway 已删，Core 从未引用这些）。

无 commit（本任务为验证）。

---

## Task 10: 文档与配置同步

**Files:**
- Modify: `CLAUDE.md`
- Modify: `AGENTS.md`
- Modify: `docs/architecture.md`
- Modify: `docs/startup.md`
- Modify: `README.md`
- Modify: `.ponytail/rules.md`、`.ponytail/config.json`
- Modify: `scripts/test-ai-tools.ps1`
- Modify: `gateway/AGENTS.md`（原 apps/gateway/AGENTS.md）

- [ ] **Step 1: 更新 CLAUDE.md**

读取 [CLAUDE.md](file:///workspace/CLAUDE.md)，删除：Rust toolchain 前置要求、`native/Cargo.toml` 文件图条目、`native/target/` 禁编目录条目。文件图中 `apps/gateway` → `gateway`。修正 `@tinadec/code`/`dev:code` 残留为 `@tinadec/gateway`/`dev:gateway`。

- [ ] **Step 2: 更新 AGENTS.md**

读取 [AGENTS.md](file:///workspace/AGENTS.md)：
- 删除 Native 层整节（FOUR-LAYER ARCHITECTURE 中的 Native 层、KEY COMPONENTS 的 native 组件、CODE MAP 的 native 行）
- STRUCTURE 树删 `native/`，`apps/gateway` → `gateway/`
- WHERE-TO-LOOK/CODE MAP 路径 `apps/gateway` → `gateway`
- CONVENTIONS 删 native/Rust 约定
- 删除 `native/target/`、`native/codex-src` 禁编条目
- 更新元数据 `Last Updated: 2026-07-09`、`Last Updated By: <执行者>`、`Branch: main`
- COMMANDS 段删 `build:native`

- [ ] **Step 3: 更新 docs/architecture.md**

删除 [architecture.md:6](file:///workspace/docs/architecture.md#L6) 的 `native/glue/*` 行。

- [ ] **Step 4: 更新 docs/startup.md**

删除 [startup.md:186-221](file:///workspace/docs/startup.md#L186) 整个 "Native Codex Rust Glue" 节。修正 `@tinadec/code` → `@tinadec/gateway`、`dev:code` → `dev:gateway`（[startup.md:27,49,89,149](file:///workspace/docs/startup.md#L27)）。

- [ ] **Step 5: 更新 README.md**

删除 [README.md:8](file:///workspace/README.md#L8) 的 `native/glue/*` 行。

- [ ] **Step 6: 更新 .ponytail 配置**

删除 [.ponytail/rules.md:54](file:///workspace/.ponytail/rules.md#L54) 的 `Reuse Codex primitives from native/glue/` 行。删除 [.ponytail/config.json:37-44](file:///workspace/.ponytail/config.json#L37) 的 `nativeLayer` 配置节和 `:51` 的 `native/target/` 排除项。

- [ ] **Step 7: 更新 scripts/test-ai-tools.ps1**

删除 [test-ai-tools.ps1:215](file:///workspace/scripts/test-ai-tools.ps1#L215) 的 `Test-Path "$projectPath\native"` 检查。

- [ ] **Step 8: 更新 gateway/AGENTS.md**

读取 `gateway/AGENTS.md`（原 apps/gateway/AGENTS.md），更新：目录位置说明（顶层 gateway/）、删除 native binary 相关段落、注明 list_directory 临时补丁（指向 docs/gateway-extraction-and-tool-bridge.md）、命令段保持 `-w @tinadec/gateway`。

- [ ] **Step 9: 清理其他 docs 中的 native 引用**

搜索并清理：[docs/architecture-compliance-verification.md](file:///workspace/docs/architecture-compliance-verification.md)、[docs/ai-tools-*.md](file:///workspace/docs/ai-tools-integration-guide.md)、[docs/agent-harness-product-model.*.md](file:///workspace/docs/agent-harness-product-model.zh-CN.md)、[docs/agent-debug-studio-plan.md](file:///workspace/docs/agent-debug-studio-plan.md) 中的 native/Rust 层描述。保留 `.trae/specs/` 历史 spec 不动。

- [ ] **Step 10: 验证 ponytail 配置**

Run: `cd /workspace && npm run ai:ponytail:validate`
Expected: 通过（若 nativeLayer 删除后校验报错，按报错修复）。

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "docs: sync architecture docs for gateway extraction and rust removal

- remove native/rust layer from all architecture docs
- update gateway path apps/gateway -> gateway
- fix stale @tinadec/code references
- document list_directory temporary hack
- update AGENTS.md metadata"
```

---

## Task 11: 全量验证

**Files:** 无修改

- [ ] **Step 1: gateway 独立构建+测试**

Run: `cd /workspace && npm run build -w @tinadec/gateway && npm run test -w @tinadec/gateway`
Expected: 编译成功，测试全绿。

- [ ] **Step 2: Core 测试**

Run (Linux): `cd /workspace && dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal`
Expected: 全绿。

- [ ] **Step 3: Desktop 测试**

Run: `cd /workspace && npm run test -w @tinadec/desktop`
Expected: 全绿。

- [ ] **Step 4: Rust 残留零检查**

Run: `cd /workspace && rg -i "codex-rust|tinadec-code-native|codex-apply-patch|codex-exec-server|tinadec-core-native|TINADEC_CODE_NATIVE_BIN|nativeRuntimePath|resolveNativeBinary|native/target" --type ts --type cs --type vue -g '!.trae/**'`
Expected: 无结果（`.trae/` 历史 spec 除外）。`codex-rust` source 名在 Core 侧保留（见 Task 9 说明），此搜索会命中 Core 的 codex-rust——这是预期的，记录但不视为残留。

- [ ] **Step 5: 手动验证 list_directory 补丁（可选）**

若环境允许启动 gateway：
Run: `cd /workspace && npm run dev:gateway`（另一终端）
然后 curl：
```bash
curl -X POST http://127.0.0.1:48730/api/v1/code/tools/list_directory/execute \
  -H 'Content-Type: application/json' \
  -d '{"cwd":"/workspace","arguments":{"path":"docs"}}'
```
Expected: 返回 completed + entries 数组。

无 commit。

---

## 后续接驳任务（Tool layer 就绪后执行）

> 以下任务依赖 cjt 的 C# Tool layer（TinadecTools）就绪。本次 MVP 不执行。Tool layer 是 stdin/stdout 常驻进程，按 workspace 启动实例、设 cwd、有状态（FileSlot 缓存 + MCP 连接池）。详见 [docs/gateway-extraction-and-tool-bridge.md](file:///workspace/docs/gateway-extraction-and-tool-bridge.md) 第二节。

## Task 12: 创建 toolLayerBridge.ts（后续）

**Files:**
- Create: `gateway/src/toolLayerBridge.ts`
- Create: `gateway/src/toolLayerBridge.test.ts`

**前置条件：** cjt 的 TinadecTools 可执行文件可构建（`dotnet build TinadecTools/TinadecTools.csproj`），二进制路径可通过 env `TINADEC_TOOLS_BIN` 配置。

**职责：**
- 按 workspace 路径 spawn TinadecTools 进程，设 `cwd` = workspace
- 缓存 workspace→进程实例映射（Tool layer 常驻、有状态，复用）
- `callTool(workspace, toolId, params, approved)`：序列化 `ToolCallRequest`（`tool_id`/`session_id`/`toolcall_id`/`approved`/`params`）→ 写 stdin → 读 stdout 一行 → 反序列化 `ToolCallResponse`/`ToolCallErrorResponse`
- 进程生命周期：lazy spawn、退出时 dispose、异常重启
- 二进制路径走 env `TINADEC_TOOLS_BIN`，不硬编码 monorepo 相对路径

**测试：** 用 mock 进程（echo stdin→stdout）验证 callTool 协议、workspace 隔离、进程复用。

## Task 13: native-backed 工具接驳 Tool layer（后续）

**前置条件：** Task 12 完成。

**改动：** gateway `executeCodeTool` 中为 native-backed 工具加分支，调用 `toolLayerBridge.callTool(...)`，按下表映射：

| gateway 工具 | Tool layer tool_id | 处理 |
|---|---|---|
| read_file | read_file | 直接映射 |
| grep_content / search_files / glob_search | file_search | 映射（glob_search 走 glob 参数） |
| sandbox_exec | command_run | 映射（注意：非沙箱，语义降级） |
| apply_patch | replace_bytes/replace_lines/insert_*/delete_* | 需薄适配层或暂 unavailable |
| list_directory | （cjt 补 listdir 后）| 删 Task 5 的 ls 补丁，改走 bridge |
| review_format | （cjt 补后）| 接驳 |

审批门保留：转发前跑 `codeToolRequiresApproval`/`codeToolApprovalBlockFor`；Tool layer 侧 `RequiresApproval` 也校验 `approved`（双保险）。

## Task 14: 迁移 bash_environment + code_editor（后续）

**前置条件：** Task 12-13 完成。

**改动：**
- `bash_environment` stub → 映射到 `command_run`，删 gateway stub 分支
- `code_editor` → 映射到 `read_file`/`replace_*`/`insert_*`/`delete_*`，删 gateway TS 实现（[executeCodeEditor](file:///workspace/gateway/src/codeTools.ts#L574)）
- 更新 coreClient.test.ts 的 tool id 列表（若 bash_environment/code_editor 从 catalog 移除）或保留 spec 改执行路径

**DEFERRED 工具（不在本任务）：** project_templates/scaffold、git_worktree_manager、language_runtime_probe、debug_session——待 Tool layer 补对应能力后单独立项。

---

## Self-Review

**Spec coverage：** 对照 [docs/gateway-extraction-and-tool-bridge.md](file:///workspace/docs/gateway-extraction-and-tool-bridge.md)：
- 阶段 0 基线 → Task 1 ✓
- 阶段 1 删 Rust → Task 2 ✓（含 DoctorService）
- 阶段 1 删 gateway native → Task 4 ✓
- 阶段 1 删 terminal → Task 3 ✓
- 阶段 2 接驳 Tool layer → Task 12-14（后续）✓
- 阶段 2 list_directory 补丁 → Task 5 ✓
- 阶段 2 DEFERRED 标记 → Task 6 ✓
- 阶段 3 MCP 保留 → 无需任务（不动）✓
- 阶段 4 gateway 迁出 → Task 8 ✓
- 阶段 5 Desktop → Task 7 ✓
- 阶段 6 Core 最小清理 → Task 9 ✓（保留 source 名）
- 阶段 7 文档 → Task 10 ✓
- 阶段 8 验证 → Task 11 ✓

**Placeholder scan：** 无 TBD/TODO 占位（DEFERRED 标记是 intentional 技术债标记，非占位）。所有 MVP 任务含完整代码。

**Type consistency：** `executeListDirectoryViaLs` 签名、`CodeToolExecuteResult` status 联合类型（已删 'native'）、`resultFor`/`failedResult`/`stringArg` 调用均一致。
