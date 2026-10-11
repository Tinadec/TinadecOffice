# 存储设置页为什么卡

**结论：`StatisticsAsync` 对九个存储分类各做一次递归目录遍历，逐文件 `stat`。** 你的用户根里
`cache` 有 **9487 个文件 / 372 MiB**，占全部 9522 个文件的 99.6%。一次打开页面要等这一趟走完，
期间页面是空的（`stats` 与 `document` 都在同一个 `await` 链上被清空）。

## 证据

### 1. 打开页面时实际发生什么

`StorageSection.vue:270` 的 `onMounted` → `refresh()` → `loadContext()`，而 `loadContext`
（`:68-85`）是**串行**的：

```js
const [result, diagnosis] = await Promise.all([getStorageStats, getStorageDiagnostics])
...
await loadDocument(current)        // ← 在 stats 之后才发
```

而且它在开头就把界面清空（`:71`）：`stats.value = null; document.value = null`。所以等待期间
页面不是「旧数据 + 转圈」，是**空**。这就是你说的「千万不要切走，要一直停在那里等」。

### 2. 慢的是哪一步

`StorageMaintenanceService.StatisticsAsync`（`TinadecCore/Runtime/StorageMaintenanceService.cs:24`）：

```csharp
foreach (var (category, path) in scope.Categories)   // 9 个分类
    var files = OwnedFiles(scope.Root, path, ct);    // 每个都递归整棵树
```

`OwnedFiles`（`:238`）是手写的栈式遍历，对**每一个条目**调 `File.GetAttributes`，对**每一个文件**
再建 `FileInfo` 并读 `Length`。也就是说：**每文件 2 次系统调用**。

`scope.Categories`（`IScopeStorageLocations.cs:52`）共九项：config、skills、packages、data、state、
logs、cache、temp、worktrees。只有 cache 大，其余八项近乎为空 —— 所以这一趟的成本基本等于走 cache。

### 3. 实测

**隔离实测**（真实服务，xUnit 宿主，HEAD `932fb1a8` 即修复前代码）：

| 调用 | 冷 | 热 |
|---|---|---|
| `StatisticsAsync`（用户作用域，真实根） | **6706 ms** | ~3500 ms |
| `CompileAsync`（读并校验 9 份文档） | **3523 ms** | ~450 ms |
| `ReadAsync("runtime")` | 161 ms | 8 ms |
| `registry.List()` + `ToDtoAsync` | O(注册作用域数)，不遍历树 | — |

**归因**：`cache` 的 9635 个条目用 `Directory.EnumerateFileSystemEntries` 枚举只要 **22 ms**，
读它们的元数据要 **2777 ms**。也就是说这趟遍历 **~99% 是逐条元数据系统调用，不是枚举**。
那 9.6k 个文件是 Electron/Chromium 的会话缓存（`cache\desktop\{Cache,Code Cache,GPUCache,Local
Storage,Network,blob_storage,…}`），9602 个是无扩展名的哈希 blob，均 ~41 KB。

`CompileAsync` 的时间 ~95% 在**一份文件**上：`agents.toml`（288,740 字符）—— 它要读 + SHA-256 +
完整 TOML 解析 + `RuntimeConfigurationDocumentValidator`（内部再解析一次）+
`ConfigurationProjectionDocumentValidator`（再解析一次并与 SQL 行逐条比对）+
`ConfigurationLiveSourceValidation`，一趟 `CompileAsync` 里走两遍。其余 8 份文档合计 6–180 ms。

分目录（热）：`cache` **2993 ms / 9612 文件 / 397 MB**；其余八项合计 **30 ms**
（data 7.3、state 6.4、config 4.3、skills 4.0、logs 2.7、temp 2.4、worktrees 1.0、packages 1.1）。

**另外四个目录没有被任何分类统计**：`defaults`、`projects`、`security`、`toolchains`（都很小，
但这是既有的计费口径缺口，不影响本次性能问题）。

### 4. 为什么「切换界面」特别难受

`loadContext` 是 `generation` 守卫 + `finally { busy.value = false }`，切换作用域或离开会 `++generation`，
在途请求的结果被丢弃。所以切走再回来 = **从零重来一趟 2.7 s+**，而且旧结果一点不复用。
这不是「等一次就好」，是每次都要等。

## 修法（按性价比排序）

### A. 别在打开页面时统计 —— 真正的问题

统计数据（每个分类多大、多少文件）**不是这个页面的主内容**。主内容是：作用域、路径、配置文档编辑器。
把一个 2.7 s 的全盘遍历放在首屏关键路径上，是这次体验问题的根因。

- 首屏只加载**轻的**：作用域列表、路径、TOML 文档 → 立刻可见。
- 使用量与清理**按需加载**：进入该区或点「统计」按钮时才走那趟遍历，并显示「正在统计…」而不是空白。

### B. 一次遍历代替九次

九个分类都在 `scope.Root` 之下。走**一次**根，把每个文件按所属分类归并，成本从「九次子树遍历」
降到「一次全树遍历」—— 对当前数据量约等于省掉八次近乎为空的遍历（收益不大，但为零成本顺手做掉）。

### C. 遍历本身每条只读一次元数据（**已落地，实测有效**）

`File.GetAttributes(entry)` + `new FileInfo(entry).Length` 是同一个 inode 的**两次** stat。改成只建
一次 `FileInfo`（它缓存 stat），用 `file.Attributes` 同时判目录与链接、`file.Length` 取大小。

**实测对比**（同一目录、同一份 9615 文件、本机 .NET 10）：

| 写法 | 热 |
|---|---|
| 两次 stat | 803 ms |
| **一次 stat** | **425–520 ms** |

结果字节一致（9615 文件 / 397171474 字节）。约 1.7×。

> 我第一次改错了：只把 `Length` 从遍历里挪走而不复用 `FileInfo`，两次调用原样保留、注释却宣称
> 「一次调用」——**注释是假的，性能没变**。本表是改正后的复测。

### D. 给统计加一个短 TTL 缓存

同一个作用域在几十秒内重复打开不该重走。按 `(storage_id, root)` 缓存 `StorageStatistics`，TTL 约 30 s，
清理/回收/删除/配置变更后失效。这样「切走再回来」不再等于从零重来。

### E. 并发而非串行

`loadDocument` 与 stats 无依赖，应 `Promise.all` 并发。它省不了最大那一项的时间，但把
「2.7 s + 0.3 s」变成「2.7 s」，且文档能先到就先显示。

## 落地情况

| | 处理 |
|---|---|
| **A** | 已做：统计移出首屏，改为显式「统计使用量」按钮；文档与诊断并发加载 |
| **C** | 已做：每条一次 stat（1.7×），实测见上 |
| **D** | 已做：`StatisticsAsync` 30s TTL 缓存，清理/内容回收后失效 |
| **B** | **跳过**：实测只有 cache 大，合并九次遍历几乎无收益，不值得做 |
| **E** | 已做：`loadDocument` 与 diagnostics 并发 |

**未处理、值得另立一项**：`CompileAsync` 的 3.5 s 冷 / 450 ms 热，主要来自 `agents.toml` 在一趟里被
解析并校验**两遍**。这与存储页无关（配置页同样受益），属于配置投影那条链路，需要单独一轮。

## 边界

- 未改任何代码，本文是诊断。
- 2.73 s 是本机热缓存下 Node 裸遍历的读数；真实 .NET 路径 + 冷缓存会更慢，等隔离实测补充。
- 若 `cache` 里是内容寻址的碎片文件（看命名像），文件数会随使用持续增长，这个问题会**越来越严重** ——
  这支持 A（不放在首屏）而不是只靠 C（把遍历调快）。
