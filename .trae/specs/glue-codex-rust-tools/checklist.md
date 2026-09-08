> **⚠️ 已归档规格（未实现或已过时）**
> 本文描述的功能多数**在代码中不存在**（例如 .trae/specs 中标注"已完成 ✅"的能力无任何源码/测试对应）。仅作历史记录，请勿据此判断当前能力。

- [x] `tinadec-code-native.exe` 可在 Windows 上编译成功
- [x] `tinadec-code-native version` 返回有效 JSON
- [x] `codeTools.ts` 对所有工具（含审批工具）都尝试 native 路径
- [x] `nativeRuntimePath` 不含硬编码路径，使用动态发现
- [x] Gateway 调用 search_files 返回 `status: "native"`
- [x] Gateway 调用 apply_patch（无 approval_id）返回 `status: "blocked"`
- [x] native binary 不存在时 Gateway 正确降级到 stub
