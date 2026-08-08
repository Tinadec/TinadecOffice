# Workbench 视觉层次设计规范

## 设计理念：卡片式岛屿 vs 沉浸式对话

### 1. 三栏布局的视觉角色区分

#### 🟦 左侧栏 (Left Column - Navigation)
- **视觉风格**: 卡片式岛屿 (Island-style)
- **背景**: `var(--surface-section)` - 有实际颜色，与主内容区区分
- **边界**: 1px solid `var(--border-card)` - 清晰可见的边框
- **阴影**: `var(--shadow-card-subtle)` - 轻微阴影提升层级
- **圆角**: 12px - 柔和的卡片外观
- **透明度**: 遵循用户设置（模糊/不透明）
- **作用**: 项目导航、会话管理

#### ⬜ 中间列 (Center Column - Chat)  
- **视觉风格**: 完全沉浸式 (Immersive)
- **背景**: **transparent** - 完全透明，显示页面背景层
- **边界**: 无边框 - 由父容器提供视觉边界
- **阴影**: 无阴影 - 保持平面感
- **圆角**: 无圆角 - 与容器完全对齐
- **透明度**: 100% 透明 - 跟随用户设置但不添加任何遮罩
- **作用**: 主要对话区域，最大化视觉空间

#### 🟩 右侧栏 (Right Column - Context Panel)
- **视觉风格**: 卡片式岛屿 (Island-style)  
- **背景**: `var(--surface-section)` - 有实际颜色，与主内容区区分
- **边界**: 1px solid `var(--border-card)` - 清晰可见的边框
- **阴影**: `var(--shadow-card-subtle)` - 轻微阴影提升层级
- **圆角**: 12px - 柔和的卡片外观
- **透明度**: 遵循用户设置（模糊/不透明）
- **作用**: Git 状态、审批、调试工具等上下文信息

---

## 2. 材质系统应用规则

### 2.1 全局材质设置

用户可以通过 `usePanelStyles` composable 选择三种效果：

```typescript
type PanelMaterial = 'opaque' | 'translucent' | 'blur'
```

| 模式 | Left/Right Panel | Center Chat Column | Inner Objects |
|------|------------------|-------------------|---------------|
| **Opaque** | Solid surface color | Transparent | Follow parent |
| **Translucent** | Semi-transparent with alpha | Transparent | inherit material tokens |
| **Blur** | Backdrop-filter blur + alpha | Transparent | inherit material tokens |

### 2.2 Surface Token 层级映射

左侧和右侧卡片必须使用以下 `--surface-*` token：

```css
/* Float panels (left/right) */
background: var(--surface-section);  /* Section frames */
/* or for raised elements */
background: var(--surface-raised);   /* Cards, popovers */
/* For interactive areas */
background: var(--surface-hover);    /* Hover emphasis */
background: var(--surface-active);   /* Focus states */
```

**禁止直接使用** `--bg-*` 系列原始颜色值。

---

## 3. CSS 变量定义

### 3.1 Card Boundary & Elevation

在 `apps/desktop/src/styles.css` 中定义：

```css
:root {
  /* Light theme */
  --border-card: rgba(0, 0, 0, 0.08);          /* Very subtle border */
  --border-card-active: rgba(0, 0, 0, 0.12);   /* Active/focused state */
  
  --shadow-card-subtle: 0 1px 3px rgba(0, 0, 0, 0.06),
                        0 4px 12px rgba(0, 0, 0, 0.04);
  /* Lightweight shadow - barely visible on white */
  
  --shadow-card-hover: 0 2px 6px rgba(0, 0, 0, 0.10),
                       0 8px 24px rgba(0, 0, 0, 0.07);
  /* Slightly stronger for hover feedback */
}

[data-theme="dark"] {
  --border-card: rgba(255, 255, 255, 0.08);
  --border-card-active: rgba(255, 255, 255, 0.12);
  
  --shadow-card-subtle: 0 1px 3px rgba(0, 0, 0, 0.12),
                        0 4px 12px rgba(0, 0, 0, 0.08);
  /* Darker shadow coefficients for dark background */
  
  --shadow-card-hover: 0 2px 6px rgba(0, 0, 0, 0.18),
                       0 8px 24px rgba(0, 0, 0, 0.12);
}
```

### 3.2 Immersive Zone Variable

**注意**: 中间列完全透明，不使用 `--bg-immersive` 变量。

```css
/* ❌ DO NOT USE */
.wb-stack--immersive {
  background: var(--bg-immersive);  /* Wrong! Breaks immersion */
}

/* ✅ CORRECT */
.wb-stack--immersive {
  background: transparent !important;
  border: none !important;
  border-radius: 0 !important;
  box-shadow: none !important;
}
```

---

## 4. 组件样式实现

### 4.1 WorkbenchStack.vue (Parent Container)

```vue
<style scoped>
/* Float panels (left/right columns) */
.wb-stack:not(.wb-stack--app):not(.wb-stack--immersive) {
  background: var(--surface-section);
  border: 1px solid var(--border-card);
  border-radius: 12px;
  box-shadow: var(--shadow-card-subtle);
  transition: box-shadow 0.2s ease;
}

/* Hover elevation feedback */
.wb-stack:not(.wb-stack--app):not(.wb-stack--immersive):hover {
  box-shadow: var(--shadow-card-hover);
}

/* Connected app (market/code/debug) - no visual separation needed */
.wb-stack--app {
  background: var(--bg-primary);
  border: none;
  border-radius: 0;
  box-shadow: none;
}

/* Immersive center column - truly invisible */
.wb-stack--immersive {
  background: transparent !important;
  border: none !important;
  border-radius: 0 !important;
  box-shadow: none !important;
}
</style>
```

### 4.2 ChatPanel.vue (Center Content)

```vue
<!-- Inner chat panel stays completely transparent -->
<div class="chat-active-panel" 
     style="background: transparent !important; 
            border: none !important; 
            box-shadow: none !important;">
```

### 4.3 WorkbenchCardFrame.vue (Card Shell)

```vue
<style scoped>
.wb-card-frame {
  /* Island-style boundary provided by frame itself */
  border: 1px solid var(--border-card);
  border-radius: 12px;
}

.wb-card-frame--shadow {
  box-shadow: var(--shadow-card-subtle);
}
</style>
```

---

## 5. 用户视觉体验预期

### 5.1 透明背景 + 模糊材质

| 元素 | 视觉效果 |
|------|---------|
| 左侧栏 | 半透明模糊背景，轻微边框，可见卡片轮廓 |
| 中间列 | 完全透明，看不到面板边界，只有内嵌组件可能有材质 |
| 右侧栏 | 半透明模糊背景，轻微边框，可见卡片轮廓 |

### 5.2 实心不透明背景 + 无模糊

| 元素 | 视觉效果 |
|------|---------|
| 左侧栏 | 纯色块状，明显边框，强烈的卡片存在感 |
| 中间列 | 透明，与背景层融为一体 |
| 右侧栏 | 纯色块状，明显边框，强烈的卡片存在感 |

### 5.3 半透明不模糊

| 元素 | 视觉效果 |
|------|---------|
| 左侧栏 | 带 alpha 的颜色块，轻微边框，适中可见性 |
| 中间列 | 完全透明，不可见 |
| 右侧栏 | 带 alpha 的颜色块，轻微边框，适中可见性 |

---

## 6. 常见错误与修正

### ❌ 错误 1: 中间列添加了背景色

```css
/* WRONG */
.wb-stack--immersive {
  background: rgba(var(--bg-secondary-rgb), 0.38);
}

/* RIGHT */
.wb-stack--immersive {
  background: transparent !important;
}
```

### ❌ 错误 2: 左右侧栏缺少边框

```css
/* WRONG */
.wb-stack {
  border: none;
}

/* RIGHT */
.wb-stack:not(.wb-stack--app):not(.wb-stack--immersive) {
  border: 1px solid var(--border-card);
}
```

### ❌ 错误 3: 使用了过重的阴影

```css
/* WRONG */
box-shadow: 0 8px 28px rgba(0, 0, 0, 0.42);

/* RIGHT */
box-shadow: 0 1px 3px rgba(0, 0, 0, 0.06),
            0 4px 12px rgba(0, 0, 0, 0.04);
```

### ❌ 错误 4: 内层组件重复添加材质

```css
/* WRONG */
.wb-card-host .conversation {
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
}

/* RIGHT */
.wb-card-host .conversation {
  background: transparent !important;
  border: none !important;
  /* Parent stack owns material */
}
```

---

## 7. 设计验证清单

开发完成后请检查：

- [ ] 左侧栏是否显示了 `var(--surface-section)` 背景色？
- [ ] 右侧栏是否显示了 `var(--surface-section)` 背景色？
- [ ] 中间列是否完全透明，能看到页面背景层？
- [ ] 左右侧栏是否有 `var(--border-card)` 边框？
- [ ] 左右侧栏是否有 `var(--shadow-card-subtle)` 阴影？
- [ ] 鼠标悬停在左右侧栏时，阴影是否会增强为 `var(--shadow-card-hover)`？
- [ ] 当用户选择模糊材质时，左右侧栏是否显示 backdrop-filter？
- [ ] 当用户选择完全不透明时，左右侧栏是否为实色块？
- [ ] 中间列的内嵌组件（composer、welcome dialog）是否可以有自己的材质？
- [ ] 整体视觉层次是否通过颜色、边框、阴影建立了清晰的层级关系？

---

## 8. 参考文件列表

| 文件 | 作用 |
|------|------|
| `apps/desktop/src/workbench/components/WorkbenchStack.vue` | 柱容器样式定义 |
| `apps/desktop/src/workbench/components/WorkbenchCardFrame.vue` | 卡片外壳样式 |
| `apps/desktop/src/workbench/components/workbench-card-fill.css` | 内容填充覆盖规则 |
| `apps/desktop/src/styles.css` | 全局 CSS 变量定义 |
| `apps/desktop/src/components/ChatPanel.vue` | 中心对话面板实现 |
| `apps/desktop/src/composables/usePanelStyles.ts` | 全局材质控制系统 |

---

## 9. 与设计团队的对接要点

1. **无边界≠无层次**: "No outline"不是完全没有视觉边界，而是通过
   - 背景色区分 (`--surface-section`)
   - 轻微边框 (`--border-card`)  
   - 轻量阴影 (`--shadow-card-subtle`)
   
   三个维度的精细配合实现"现代扁平化但有深度"的效果。

2. **沉浸式的真义**: 中间列的"immersive"指的是**视觉上的无缝融合**，而不是物理上消失。它通过完全透明来让用户的自定义背景层展示出来，最大化个性化空间。

3. **阴影的原则**: "轻量化阴影"是指：
   - 系数小：`0.06`, `0.04` (而非传统的 `0.32`, `0.42`)
   - 扩散距离短：3px, 4px (而非 16px, 28px)
   - 仅在 hover 时轻微增强
   - 永远不要超过 `0.2px` 的模糊半径

---

*版本*: v1.0  
*最后更新*: 2026-08-07  
*作者*: AI Code Review Team  