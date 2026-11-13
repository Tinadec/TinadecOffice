# 参与 TinadecOffice 贡献指南

感谢您对 **TinadecOffice** 的关注与贡献！

在提交 Pull Request 之前，请先阅读[贡献者许可协议（CLA）](CLA.zh-CN.md)
（[English](CLA.md)）——**任何带来新贡献的 Pull Request 都会在其作者签署 CLA
之前被阻止合并。**

## 目录

- [行为准则](#行为准则)
- [许可证与所有权](#许可证与所有权)
- [贡献者许可协议（CLA）](#贡献者许可协议cla)
- [如何贡献](#如何贡献)
  - [缺陷与功能请求](#缺陷与功能请求)
  - [Pull Request](#pull-request)
  - [签署 CLA](#签署-cla)
- [开发工作流](#开发工作流)
  - [环境要求](#环境要求)
  - [工作区初始化](#工作区初始化)
  - [构建与测试](#构建与测试)
- [项目约定](#项目约定)
- [法律](#法律)

## 行为准则

请保持友善与建设性。任何项目空间中均不容忍骚扰与人身攻击。

## 许可证与所有权

TinadecOffice 是一个**多许可证仓库**。各许可证的精确范围见
[README](README.zh-CN.md#许可证) 与 [`NOTICE`](NOTICE)：

| 范围 | 许可证 |
|------|--------|
| TinadecOffice 整体（apps、docs、scripts） | `GPL-3.0-or-later` |
| `TinadecCore` | `MIT` |
| `TinadecGateway` | `AGPL-3.0-or-later` |
| `TinadecTools`（含 Generators） | `AGPL-3.0-or-later` |

**请注意：** 向本项目贡献**并非**所有权的转移。您的贡献仍归您所有；签署 CLA
后，您向项目方授予**永久、不可撤销、全球范围内**的权利，使其可以在其选择的
任何许可条款下使用、修改、再许可、分许可并分发您的贡献——包括商业或闭源
条款，也包括在本项目（或其任何部分）于开源与闭源之间切换时继续使用您的贡献。
作为交换，您保留 CLA 中描述的自身权利。

## 贡献者许可协议（CLA）

每位贡献者都必须在**其 Pull Request 被合并之前**签署 CLA。CLA 赋予项目方：

- **接受、修改、返工与删除**贡献的权利；
- **再许可、分许可或双重许可**贡献的权利，包括在本项目于开源与闭源之间切换
  时继续使用贡献；
- 来自每位贡献者的**著作权与专利权许可**，且为永久、全球、非独占、免费、
  不可撤销；
- 确认贡献为**原创**、且贡献者有权提交的保证。

完整文本： [CLA.zh-CN.md](CLA.zh-CN.md) · [CLA.md](CLA.md)。

若您代表雇主贡献，请确保您已获得相应授权。

## 如何贡献

### 缺陷与功能请求

请创建[Issue](https://github.com/Tinadec/TinadecOffice/issues)，给出清晰的
标题与描述，包含复现步骤、预期行为与实际行为；缺陷请注明所用版本。

### Pull Request

1. **Fork** 本仓库，并创建描述性分支名（`feat/…`、`fix/…`、`docs/…`）。
2. **保持 Pull Request 小且聚焦**，一个逻辑变更对应一个 PR。
3. **遵循项目约定**（见[项目约定](#项目约定)）。
4. 推送前**在本地运行相关检查**（见[构建与测试](#构建与测试)）。
5. 在
   [CLA v1 签署台账](https://github.com/Tinadec/TinadecOffice/discussions/67)
   中发布页面写明的确切中文授权语句，即可完成一次仓库级签署。按 PR 评论签署
   的旧流程仍保留：

   > I have read the CLA Document and I hereby sign the CLA

   [CLA Assistant 工作流](.github/workflows/cla.yml)会自动记录您的签名；
   未签署的 PR 将无法通过必需检查。

### 签署 CLA

正式的仓库级签署台账是
[Discussion #67](https://github.com/Tinadec/TinadecOffice/discussions/67)。请阅读
页面钉住的 CLA v1 文档，并以新建评论发布 Discussion 中写明的确切中文授权语句。
[CLA Discussion Signature 工作流](.github/workflows/cla-discussion.yml)会校验评论
的 GitHub 账号、固定语句、Discussion 身份与 CLA commit，再把您的用户 ID 追加到
签名库。

也可以继续按 PR 签署：CLA Assistant 机器人会在 Pull Request 上发布邀请评论，
请回复以下确切语句：

> I have read the CLA Document and I hereby sign the CLA

您的 GitHub 用户 ID、评论 URL 与钉住的 CLA commit 将被记录在
`cla-signatures` 分支的 `signatures/version1/cla.json` 中作为证据。签署后
工作流会重跑您已开启 PR 的 `CLAAssistant` 检查；`main` 要求该检查通过后才能
合并 Pull Request。

若签署后需要重新检查，请评论 `recheck`。

## 开发工作流

### 环境要求

- Node.js ≥ 24 与 npm（无需 pnpm）
- Bun ≥ 1.3.x（用于 `TinadecGateway`）
- .NET SDK 10.x
- ripgrep 由 `scripts/setup-ripgrep.mjs` 自动暂存

### 工作区初始化

```bash
npm ci                # 安装工作区并运行 postinstall
npm run ai:workspace:setup   # 可选：链接本地 plan/wiki/skill 目录
```

### 构建与测试

```bash
npm run build         # 构建工作区、gateway 与 .NET 解决方案
npm test              # 运行工作区、gateway 与 .NET 测试
npm run check:drift   # 重新生成桌面 OpenAPI 客户端，有差异即失败
```

改动 `TinadecCore/`、`TinadecTools/`、`TinadecGateway/src/` 或生成的客户端的
Pull Request，应通过 `main` 分支上相应的 CI 工作流（POSIX core、契约漂移、
CLA）。

## 项目约定

- 遵循各区域的 `AGENTS.md` 指引（`apps/desktop`、`apps/TinadecUI`、
  `TinadecCore`、`TinadecGateway` 等）。
- 保持 API 契约稳定：改动路由或 DTO 时重新生成 OpenAPI 快照与
  `schema.d.ts`（`npm run check:drift`）。
- 未经 PR 讨论，不引入新依赖。

## 法律

提交贡献即表示您同意受[贡献者许可协议](CLA.zh-CN.md)的约束。如有疑问，请在本
仓库创建标题为「CLA」的 Issue。
