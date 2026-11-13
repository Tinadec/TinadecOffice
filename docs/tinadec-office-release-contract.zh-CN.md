# TinadecOffice 渠道发布契约

本文定义 TinadecOffice 完整安装包、运行时模块包和 AgentPack 包之间的发布边界。契约的目标是让 GitHub Release 产物可以被 Tinadec Manager 按清单消费，同时保留普通用户直接使用各平台安装器的一键安装路径。

## 平台矩阵

一个 `vX.Y.Z` Release 同时携带三个平台的产物，每个平台由自己的 CI job 构建：

| target | 平台/架构 | 模块归档 | 安装包 | 归档器 |
| --- | --- | --- | --- | --- |
| `win-x64` | windows/x64 | `.zip` | NSIS setup + portable | System32 bsdtar |
| `linux-x64` | linux/x64 | `.tar.gz` | `.deb` | 系统 GNU tar |
| `osx-arm64` | macos/arm64 | `.tar.gz` | `.dmg`（ad-hoc 封印，未公证） | 系统 bsdtar |

归档格式按平台分裂不是审美选择，是量出来的：Linux runner 上的 `tar` 是 GNU tar，`tar -a -cf module.zip` 会以 0 退出码写出一个**tar 归档**（首字节 `2e 2f 00 00`，不是 `50 4b 03 04`），并且反过来读真 zip 时报 `This does not look like a tar archive`（exit 2）。Manager 的 `extractArchive()` 调的正是同一个 `tar`。所以 POSIX 腿发 tar.gz：GNU tar 会把它自己的执行位写进归档（实测 `-rwxr-xr-x`），Windows 腿继续发 zip 给 bsdtar 消费。

`catalog.json` 里每个 artifact 的 `format` 字段声明这个平台的归档格式，`SHA256SUMS` 与 build 门禁都按声明的格式校验首 4 字节，声明与字节不符即红。

## 交付类型

同一个 Release 包含以下三类制品：

- `full-installer`：该平台的安装包（Windows 是 setup + portable 两个资产，Linux 是 `.deb`，macOS 是 `.dmg`）。它是完整工作台，内部携带 Core、Gateway、Tools（Windows 还携带 PortableGit 和 native 工具），面向直接安装的普通用户。
- `runtime-module`：面向 Manager 的平台模块包，组件为 `tools`、`core`、`gateway`。归档根目录就是组件安装根，根目录必须包含 `tinadec-package.json`。
- `agent-pack`：面向 Core 工作区的 AgentPack 包，含 `manifest.json`、`envelope.json`、`tinadec-package.json`，不能作为进程或普通运行时目录启动。它的载荷与平台无关，但仍按平台各出一份，因为 Manager 用 `(platform, architecture)` 选制品。

运行时模块的依赖顺序是 `tools → core → gateway`。Core 通过 Manager 生成的机器级注册文件找到 Tools，Gateway 通过 Core 的本地健康端点工作。一个模块更新失败时，旧的 `active.json` 指针保持不变。

## 归档根目录元数据

运行时模块和 AgentPack 的 `tinadec-package.json` 使用以下字段（示例为 Linux 腿，Windows 把 `TinadecCore.Api` 换成 `TinadecCore.Api.exe`）：

```json
{
  "schemaVersion": 1,
  "kind": "runtime-module",
  "productLine": "office",
  "productId": "tinadec-office-core",
  "component": "core",
  "releaseVersion": "0.1.1",
  "packageVersion": "0.1.1",
  "platform": "linux",
  "architecture": "x64",
  "entrypoint": "TinadecCore.Api",
  "expectedFiles": ["TinadecCore.Api", "appsettings.json", "Configuration/default-agent-runtime.toml"]
}
```

`agent-pack` 使用 `agentPack` 子对象描述 Core 交接信息，同样带本平台的 `platform`/`architecture`：

```json
{
  "schemaVersion": 1,
  "kind": "agent-pack",
  "productLine": "office",
  "productId": "tinadec-office-agentpack",
  "component": "agentpack",
  "releaseVersion": "0.1.1",
  "packageVersion": "3.0.1",
  "platform": "linux",
  "architecture": "x64",
  "agentPack": {
    "packId": "tinadec.graph.seed-pack",
    "digest": "<64 lowercase hex characters>",
    "minimumCoreVersion": "0.1.0",
    "manifestPath": "manifest.json",
    "envelopePath": "envelope.json"
  }
}
```

`packageVersion` 是组件自身版本，`releaseVersion` 是外层 Office Release 版本。两者都必须进入元数据，CI 不允许依靠文件名猜测版本。当前 AgentPack 可以独立于桌面程序演进，但每个 Release 都必须记录它实际携带的 pack 版本和 digest。

期望文件按平台派生，不由人抄写：`rg` / `rg.exe`、`native/rg/…` 跟随 `.exe` 规则，`git/cmd/git.exe` 与 `git/bin/bash.exe` 只出现在 Windows 一份里。这些事实的唯一来源是 `apps/desktop/scripts/runtimeTargets.mjs` 与 `officeChannel.mjs`，打包脚本和校验脚本共用同一张表。

## Catalog manifest

Manager 消费 `catalog.json`。顶层仍使用 `schemaVersion: 1`，旧的 core/gateway/tools/app 产品条目继续有效；Office 条目额外填写：

- `productLine: "office"`
- `packageKind: "runtime-module" | "agent-pack" | "full-installer"`
- `runtimeRole: "tools" | "core" | "gateway"`（仅运行时模块）
- 每个 **artifact** 的 `packageMetadata`，其内容必须与该归档根目录的元数据一致
- `dependencies` 中的 SemVer 范围和 `optional` 标记

结构要点：**一个产品一条条目，一个版本一条 release，一个平台一个 artifact**。`pickArtifact()` 按 `(platform, architecture)` 选制品（Linux/arm64 之类的缺失组合退回同平台 x64），所以三平台不是三份 catalog，而是同一条 release 下的三个 artifact。

包元数据因此从 `release.packageMetadata` 移到 `release.artifacts[].packageMetadata`：三个平台的归档根目录内容不同（entrypoint、expectedFiles、platform），一条 release 只能挂一份元数据，挂在哪 one 上都意味着另外两个平台的镜像是假的。

`expectedArtifact` 是产品级字段，三平台必须共用一条路径。合并规则：各腿的 entrypoint 只差扩展名时（`TinadecTools` 与 `TinadecTools.exe`）合并成 `TinadecTools*`，这正是 Manager 自身 `artifactExists()` 与 builtin catalog 已经在用的通配形式；差别的不是扩展名时合并失败并要求该产品显式声明空字符串。完整安装包就声明 `""`：单文件安装包被 Manager 复制成 `staging/<artifact.id>`，任何发布资产名都不可能满足它，而 `artifacts[].url` 才是资产名的正确位置。

## 三条腿与一次合并

打包分两步，因为没有任何一台机器能同时看到三个平台：

1. 每个 build job 在**自己的** runner 上从同一份 `apps/desktop/runtime` 生成该平台安装包 + 模块包，并写 `office-channel/catalog-<target>.json`（fragment）。fragment 是完整产品列表，只是所有 artifact 都属于本平台。
2. `release` job（仅 tag）下载三份产物后运行 `apps/desktop/scripts/merge-office-channel-catalog.mjs`，产出唯一的 `office-channel/catalog.json`。合并会拒绝：缺腿、同一 target 重复、未知 target（老 runner 留下的残档）、产品字段在两条腿之间不一致、release 级 `packageMetadata` 缺失、artifact 声明的平台与自身元数据矛盾、某产品缺少某平台的制品、以及跨腿发布不同步的 release 版本。合并还按 `sizeBytes`/`sha256` 与首 4 字节逐一核对磁盘上的文件确实存在且相符。

只有 `release` job 写 `catalog.json`：若每条腿都写同名字段，三个 artifact 里的 `office-channel/catalog.json` 会在下载时互相覆盖，最后一个赢——那就是"没人写过完整 catalog，但线上有一份看起来完整的 catalog"。

## Manager 与 Office 的交接

Manager 只负责机器级运行时和 Pack 的获取、完整性校验、版本指针及注册信息：

- 运行时模块安装到该平台的 Manager 安装根下的 `apps/<productId>/versions/<version>`（Windows 是 `%LOCALAPPDATA%\Tinadec\...`，Linux 是 `$XDG_DATA_HOME`/`~/.local/share`，macOS 是 `~/Library/Application Support`），并在 `office-runtime.json` 写入 Core、Gateway、Tools 的活动路径、版本、channel、artifact digest。
- AgentPack 安装到同一 Manager 安装根下的版本目录，注册文件只记录 manifest/envelope 的受控路径、pack id、版本和 digest。
- TinadecOffice 启动时读取并校验该注册文件；存在完整模块注册时使用 Manager 的模块路径，否则使用安装包内置 runtime。Office 将注册的 AgentPack envelope 交给 Core 的 `install-preview` / `install` API，仍遵守 owner 确认、ETag/revision、幂等键和 Core workspace 权限。

注册文件是机器级交接缓存，不是 Core 数据库，也不是工作区 Pack 的安装结果。SHA-256 只保证传输完整性；发布者身份、签名、公钥轮换属于后续信任链阶段。

## CI 门禁

每条腿都必须证明（`verify-office-channel.mjs`，本机与 CI 同一入口）：

1. 归档声明的格式与首 4 字节一致；fragment 的 `target` 就是本 runner；`officeReleaseVersion` 就是本次版本。
2. 每个模块归档的 `tinadec-package.json` 字段与 fragment 期望一致，且 `platform`/`architecture` 等于本平台本架构。
3. 没有绝对路径或 `..` 越界条目；`expectedFiles` 既在条目里，也能真被解出来。
4. 每个可执行条目（entrypoint、`rg`、`native/rg/…`、Windows 的 git/bash）解出后的头部是本平台的机器码（PE x64 / ELF 64-bit / Mach-O arm64）；POSIX 上还必须有属主执行位——解包后没有 `+x` 的模块会被 Manager 装上但无法 spawn。
5. 模块包与完整版内置 runtime 逐字节一致（路径按平台取：Windows `win-unpacked/resources/runtime`、Linux `linux-unpacked/resources/runtime`、macOS `mac-arm64/TinadecOffice.app/Contents/Resources/runtime`——extraResources 在 bundle 里落在 `Contents/Resources`，这条是 mac 腿用"找不到 runtime"报出来的），双向：模块不缺文件，完整版也没有模块包不认识的内容。
6. 安装包资产与 catalog 的字节数、SHA-256 对得上；catalog 里不得出现别的平台的 artifact（每条腿只准写自己）。
7. 归档器必须显式解析：Windows 用 `System32/tar.exe`，POSIX 用系统 `tar`。读取单个条目时要用 `tar -tf` 列出的**原始**条目名（两个归档器都写成 `./x`）——GNU tar 对 `tar -xOf a.tar.gz x` 回的是 `Not found in archive`，实测 exit 2，Windows 腿上的 bsdtar 却两种写法都接受，所以这条差异只在 POSIX 腿上暴露。

发布前（`release` job，仅 tag）另有两道：合并命令自身的结构校验与磁盘核对，以及 `jq` 对最终 `catalog.json` 的形状断言（产品数、每个 release 至少 3 个 artifact、三平台齐备）。归档数量门禁写成"Windows 4 个 zip、POSIX 8 个 tar.gz、3 份 fragment"，这些数字由 `officeChannel.test.mjs` 从 target 表反推校验，不靠人记。

非 tag 构建可以生成临时归档和 fragment 作为 CI 工件，但只有 tag 构建发布 GitHub Release。Release 只发布一份顶层 `SHA256SUMS`，其内容覆盖全部已发布资产（含三个平台的安装包、模块包与 catalog）；`office-channel/SHA256SUMS` 是 build 任务的验证门禁产物，既不进入 Release，也不再上传——三条腿各写一份同名文件会在 artifact 下载时互相覆盖。实测过旧写法会把这份只列四个模块包与 catalog 的内层清单当作发布校验和上传，安装包因此没有校验和。

## 发布记录与残余边界（2026-10-04）

- **v0.2.0 是本契约三平台形状的第一次真实发布**：Release 资产 23 个（三平台安装器 + 12 个模块归档 + 3 份 fragment + 合并 catalog + `SHA256SUMS` + `latest.yml` + blockmap），release job 的 8 个步骤全绿（三份下载 → 合并 → sums → `jq` 门禁 → `gh release create`）。合并读数：`5 products, 16 artifacts verified on disk across linux/x64, macos/arm64, windows/x64`。发布后用只读命令复核过：**catalog 里 16 个 artifact URL 全部 200**，抽下的两份 AgentPack 归档大小与 SHA-256 与 catalog 一致，Manager 自己的 `validateManifest()` 对**线上那份 catalog** 得 0 条。
- **tag 不会自己触发发布**：`desktop-release.yml` 的 push 触发带 `paths:`，而指向当前 tip 的 tag 没有变更文件列表可匹配，事件被静默丢弃（v0.2.0 当场遇到：tag 推上去，`gh run list` 里什么都没有）。现在由 `create-version.yml` 在推 tag 之后显式 `gh workflow run desktop-release.yml --ref v<版本>` 发起（该 job 因此需要 `actions: write`）。
- `SHA256SUMS` 的键按发布布局写相对路径（安装器 `./<文件>`、渠道包 `./office-channel/<文件>`），所以 `sha256sum -c` 需要在同样的目录结构下跑；把资产全下成扁平一份目录再校验会对不上路径，这是格式约定，不是校验和错误。

- `expectedArtifact` 在线上的 catalog 里是通配形式（实测：`TinadecTools*`、`TinadecCore.Api*`、`TinadecGateway*`；AgentPack 保持精确的 `manifest.json`，完整安装包为 `""`）。要收紧到本平台的 entrypoint，需要 Manager 改读 `artifact.packageMetadata.entrypoint`（`install-flow.ts:266-276` 一处回退即可），而它的 `Artifact` 类型还没有这个字段——值本身已经在 catalog 里。
- **消费侧的 `agent-pack` 拒绝已修**：`TinadecManger/src/shared/manifest.ts` 的 `VALID_FAMILIES`/`VALID_DELIVERY` 此前不含 `agent-pack`，实测用 Manager 自己的 `validateManifest()` 跑真实产物得到的 2 条拒绝（`$.products[4].family`、`.delivery`）会让 `assertManifestOrThrow()` 拒绝整份 Office catalog；补上两个合法值后同一份真 catalog 读数为 **0 条**（Manager 仓库 commit `eda169c`）。
- Manager 的 `extractArchive()` 直接调 PATH 上的 `tar`：在 Windows 开发机上它可能是 Git Bash 的 GNU tar，读不了 zip（`tests/install-flow.test.ts` 4 例在本机因此为红，且与本次改动无关，在 HEAD 上同样红）。渠道按平台分裂归档格式之后，Linux/macOS 拿到的都是 tar.gz，正好落在两种 `tar` 都能读的那一格；生产路径（Explorer 启动，PATH 里 System32 在前）用 bsdtar 读 zip。
- macOS 的 `.app` 由 electron-builder 的 `afterPack` 钩子（`scripts/sign-mac-adhoc.mjs`）做完整 ad-hoc 封印，`.dmg` 不带公证。封印完整与缺失是两种体验：缺失时带 quarantine 的下载被判「已损坏，无法打开」，完整时才是「无法验证开发者」，可右键打开或到系统设置放行。CI 的 mac 腿用 `codesign --verify --deep --strict` 与代码标识断言守住这条；Developer ID + `notarytool`/`stapler` 仍未接入，同一份清单加 `--options runtime` 与时间戳即可复用。`format: "executable"` 对 `.deb`/`.dmg` 的含义只有"单文件交付"，不代表 Manager 能解包安装。
