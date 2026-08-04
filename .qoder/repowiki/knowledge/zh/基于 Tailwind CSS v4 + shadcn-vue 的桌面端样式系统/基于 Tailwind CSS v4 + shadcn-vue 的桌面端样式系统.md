---
kind: frontend_style
name: 基于 Tailwind CSS v4 + shadcn-vue 的桌面端样式系统
category: frontend_style
scope:
    - '**'
source_files:
    - apps/desktop/package.json
    - apps/desktop/components.json
    - apps/desktop/src/styles.css
    - apps/desktop/src/composables/useTheme.ts
    - apps/desktop/src/settings/settings.css
    - apps/desktop/vite.config.ts
---

## 1. 使用的系统与工具
- **框架与构建**: Vue 3 + Vite，通过 `@tailwindcss/vite` 插件集成 Tailwind CSS v4（无传统 `tailwind.config.js`，采用 CSS-in-Vite 模式）。
- **组件库**: 使用 shadcn-vue（`components.json` 中 style 为 `base-nova`），配合 `class-variance-authority`、`clsx`、`tailwind-merge` 进行类名组合。
- **图标**: Lucide（`@lucide/vue` + `lucide-react` 双依赖）。
- **动画**: `tw-animate-css` + `tailwindcss-animate` 提供原子化动画。
- **字体**: `@fontsource-variable/geist` 作为全局字体。
- **主题持久化**: 通过 `@vueuse/core` 的 `useStorage` 将主题与强调色保存在 localStorage。

## 2. 核心文件与包
- `apps/desktop/package.json` — 前端依赖声明（Tailwind v4、shadcn、Vue 3、Lucide、Monaco、Xterm 等）
- `apps/desktop/components.json` — shadcn 配置（style: base-nova、CSS 变量开关、别名映射）
- `apps/desktop/src/styles.css` — 全局样式入口：Tailwind 导入、`@theme` 设计令牌、light/dark 两套 CSS 变量、Material-aware surface tokens、滚动条定制、页面级布局（shell/market/agent-topology 等）
- `apps/desktop/src/composables/useTheme.ts` — 主题 composable：支持 dark/light/system 三种模式、8 种强调色（blue/green/purple/orange/pink/red/cyan/yellow）、运行时注入 CSS 变量到 `document.documentElement`
- `apps/desktop/src/settings/settings.css` — 设置页独立样式模块（从 styles.css 提取）
- `apps/desktop/vite.config.ts` — Vite 配置，注册 `@tailwindcss/vite` 插件与路径别名 `@`

## 3. 架构与设计约定
- **CSS 变量驱动的主题系统**：在 `styles.css` 的 `:root` 和 `[data-theme="dark"]` 中定义完整的 HSL 色彩变量（background/foreground/primary/secondary/accent/destructive/border/input/ring/card/popover 等），并通过 `@theme` 块映射到 Tailwind 自定义颜色。
- **Material-aware surface tokens**：定义 `--surface-chrome/section/raised/hover/active/selected/input/button` 等语义化表面变量，使面板内部元素自动跟随全局 panel material 效果。
- **双主题配色方案**：light 主题以白色为主、teal 强调色；dark 主题以深灰黑为主、#2ec4b6 青色强调色。两套变量完全对称。
- **强调色可定制**：`useTheme.ts` 暴露 8 种预设强调色，每种区分 dark/light 两套值，通过 `setProperty` 动态覆盖 `--accent-primary/--accent-brand/--text-brand/--border-input-focus/--shadow-focus` 五个关键变量。
- **shadcn 组件组织**：组件位于 `src/components/ui/`，通过 `@/components/ui` 别名引用，遵循 shadcn 的原子化 class 组合模式。
- **响应式策略**：主要面向 Electron 桌面应用（最小尺寸 1120×720），少量 `@media (max-width: 760px)` 适配小屏。

## 4. 约定与约束
- **样式入口统一**：所有样式通过 `src/styles.css` 集中管理，组件内不使用 `<style>` 标签，全部使用 Tailwind 原子类或 CSS 变量。
- **主题切换必须通过 data-theme 属性**：`useTheme.ts` 通过设置 `document.documentElement.setAttribute('data-theme', ...)` 切换主题，禁止直接修改 body background。
- **强调色变更必须通过 CSS 变量**：强调色通过 `setProperty` 写入根节点变量，组件只能消费这些变量，不得硬编码颜色值。
- **Surface token 优先于原始 bg-* 变量**：注释明确要求“Interior surfaces inside material panels must use these tokens instead of raw --bg-* tokens”。
- **shadcn 组件通过 CLI 生成**：`components.json` 定义了标准别名（components/utils/ui/lib/hooks），新增 UI 组件应遵循此结构。
- **Tailwind v4 无配置文件**：样式配置全部迁移至 CSS `@theme` 块，不再维护 `tailwind.config.js`。
