# TinadecOffice Desktop Design System

## 1. Atmosphere & Identity

TinadecOffice is a quiet technical command surface: precise, trustworthy, and dense enough for repeated operational work without becoming visually noisy. The signature is capability truth made visible: writable controls, read-only previews, degraded services, and approval boundaries must look materially different before the user reads the copy.

## 2. Color

### Palette

The implementation uses semantic variables from `src/styles.css`; components must not introduce private color values.

| Role | Token | Usage |
|------|-------|-------|
| Canvas | `--bg-primary` | Deepest application surface |
| Panel | `--bg-secondary` | Navigation and work surfaces |
| Raised | `--bg-tertiary` | Selected rows, compact controls |
| Hover | `--bg-hover` | Interactive hover feedback |
| Selected | `--bg-selected` | Current tab, row, or choice |
| Text primary | `--text-primary` | Titles and primary values |
| Text secondary | `--text-secondary` | Supporting copy |
| Text muted | `--text-muted` | Metadata and disabled copy |
| Accent | `--accent-primary` | Actions, focus, selected state |
| Success | `--accent-success` | Ready and writable state |
| Warning | `--accent-warning` | Degraded or attention state |
| Danger | `--accent-danger` | Errors and destructive actions |
| Divider | `--border-muted` | Internal separation |
| Boundary | `--border-default` | Interactive and panel boundaries |

Rules:
- Accent is reserved for interaction and current state, never ambient decoration.
- Status color always appears with text or an icon; color alone never carries meaning.
- Surface depth uses tonal shifts and quiet inset highlights, not large black shadows.

## 3. Typography

| Level | Size | Weight | Line height | Usage |
|-------|------|--------|-------------|-------|
| Page title | 24px | 600 | 1.2 | Settings center title |
| Section title | 15px | 600 | 1.3 | Resource and configuration groups |
| Body | 13px | 400 | 1.5 | Default application text |
| Compact | 12px | 400/500 | 1.45 | Supporting copy and controls |
| Metadata | 11px | 500 | 1.4 | Drivers, ids, routes, status detail |
| Metric | 18px | 600 | 1.1 | Counts and readiness values |

- Primary: `Geist Variable`, system sans-serif fallback.
- Mono: `Geist Mono`, `SFMono-Regular`, `Cascadia Code`, monospace.
- Use tabular numerals for counts and receipts.
- Letter spacing is `0`; hierarchy comes from weight, scale, and luminance.

## 4. Spacing & Layout

- Base unit: 4px.
- Main center width: 1180px maximum, fluid below it.
- Settings shell: 220px navigation plus a flexible work surface.
- Center rhythm: 8px compact, 12px control, 16px group, 24px major separation.
- Corners: 6px controls, 8px panels and repeated resource cards. Pills are status/count-only.
- The settings navigation becomes horizontal at 900px. Center inspectors remain beside their resource surface above 700px and collapse below it.

## 5. Components

### Center Workbench
- Structure: compact command bar, low-height overview receipt, resource rail, contextual inspector, and compact diagnostics.
- Variants: Model Center uses five resource groups; Agent Center uses list navigation plus a semantic planning/execution/candidate topology.
- States: loading skeleton, populated, degraded, empty, read-only preview, busy.
- Accessibility: command groups are named, resource rails use `role=tablist`, selected choices expose `aria-pressed` or `aria-selected`, and mobile touch targets are at least 40px.
- Motion: none beyond row hover/focus and native details disclosure.

### Overview Receipt
- Structure: three compact status facts in one low-height surface below the command bar.
- Variants: ready, preview, unavailable, degraded.
- States: loading skeleton, populated, degraded.
- Accessibility: status icon plus explicit label/value; no color-only meaning.
- Motion: none beyond hover/focus on nested actions.

### Resource Switcher
- Structure: native tablist with compact horizontal resource tabs and counts; narrow layouts allow horizontal scrolling.
- States: default, hover, focus-visible, selected.
- Accessibility: `role=tab`, `aria-selected`, keyboard-visible focus.
- Motion: 160ms background/color transition.

### Resource Row/Card
- Structure: identity, technical metadata, status, actions, and optional inline diagnostics.
- Variants: supplier card, provider row, runtime row, diagnostic row.
- States: default, hover, expanded, issue, disabled, empty.
- Accessibility: action buttons have names; long ids truncate visually but retain `title` where needed.

### Agent Runtime Choice
- Structure: five selectable preview choices followed by one source picker.
- Variants: inherit, fixed model, provider auto, CLI, ACP.
- States: default, selected, focus-visible, unavailable result, read-only preview.
- Accessibility: choices expose `aria-pressed`; save remains disabled when Core reports no write capability.

### Agent Topology
- Structure: semantic planning, execution, and candidate bands with wrapped node labels and explicit runtime metadata.
- States: enabled, disabled, selected, candidate.
- Accessibility: nodes are keyboard-focusable buttons; selecting a node opens the adjacent inspector.
- Motion: none.

### Configuration Section
- Structure: unframed sections separated by tonal dividers inside the inspector.
- States: editable, busy, disabled, validation/notice.
- Accessibility: labels precede inputs; destructive actions stay visually isolated.

## 6. Motion & Interaction

| Type | Duration | Easing | Usage |
|------|----------|--------|-------|
| Micro | 120ms | `cubic-bezier(0.16, 1, 0.3, 1)` | Press and focus feedback |
| Standard | 180ms | `cubic-bezier(0.16, 1, 0.3, 1)` | Tabs, rows, detail reveal |
| Page | 280ms | `cubic-bezier(0.16, 1, 0.3, 1)` | Existing route transitions |

- Animate only opacity and transform where motion is needed.
- `prefers-reduced-motion: reduce` disables non-essential transitions and animations.
- Hover changes communicate clickability; static information does not animate.

## 7. Depth & Surface

Strategy: mixed tonal shift plus hairline boundaries.

- Page and major work areas use tonal separation.
- Repeated resources use one quiet boundary and an inset top highlight.
- Expanded details are recessed into their parent list rather than presented as nested floating cards.
- Shadows remain limited to the application shell, dialogs, and detached overlays.

## 8. Accessibility Constraints & Accepted Debt

### Constraints
- WCAG 2.2 AA contrast target.
- Every interactive element has a visible `:focus-visible` treatment.
- Touch targets are at least 32px in the desktop shell and 40px on narrow layouts.
- Chinese and English labels must wrap without overlapping controls.
- Runtime binding preview must never look successfully persisted while `agent_runtime_binding_write=false`.

### Accepted Debt

No center-specific accessibility debt is currently accepted.
