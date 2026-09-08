> **⚠️ 已归档规格（未实现或已过时）**
> 本文描述的功能多数**在代码中不存在**（例如 .trae/specs 中标注"已完成 ✅"的能力无任何源码/测试对应）。仅作历史记录，请勿据此判断当前能力。

- [x] `apps/gateway/package.json` 的 `name` 为 `@tinadec/gateway`
- [x] 根 `package.json` 的 `dev:code` 已改为 `dev:gateway`
- [x] 根 `package.json` 的 `concurrently` 标签为 `core,gateway,desktop`
- [x] `codeTools.ts` 的 stub 消息不再使用 "Code-layer" 措辞
- [x] `npm ls -w @tinadec/gateway` 可正常解析
