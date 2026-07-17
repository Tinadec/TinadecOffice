# CSS Contract Test CRLF Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the pre-existing `settingsCssContract.test.ts` failure caused by CRLF/LF line-ending mismatch on Windows, making the test pass cross-platform.

**Architecture:** Normalize line endings at the point of CSS string consumption in the test file. The test imports CSS via Vite `?raw` suffix, which reads file bytes as-is — on Windows this yields `\r\n`. Two assertions (lines 174-175) embed `\n` literals in expected substrings, causing failure. The fix normalizes the CSS string to LF once, at each `const css = ...` declaration, so all `toContain` assertions work regardless of OS line endings.

**Tech Stack:** Vitest, Vite `?raw` imports, TypeScript

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `apps/desktop/src/settings/settingsCssContract.test.ts` | CSS contract tests for settings/styles extraction | Modify: normalize CRLF→LF at 2 `const css` declarations |

No new files. No CSS files are modified — the fix is test-only. The CSS files legitimately use the platform's native line endings (CRLF on Windows, governed by `* text=auto` in `.gitattributes`), and the test must be tolerant of that.

---

## Task 1: Add LF normalization to CSS string consumption

**Files:**
- Modify: `apps/desktop/src/settings/settingsCssContract.test.ts:13` and `:211`

**Context for the engineer:**
- Line 2: `import settingsCss from './settings.css?raw'` — reads `settings.css` as a raw string
- Line 3: `import stylesCss from '../styles.css?raw'` — reads `styles.css` as a raw string
- Line 13: `const css = settingsCss` — used by all tests in the `'settings.css contract'` describe block
- Line 211: `const css = stylesCss` — used by all tests in the `'styles.css extraction contract'` describe block
- On Windows, these strings contain `\r\n`. On Linux/macOS, they contain `\n`.
- The failing assertions at lines 174-175 embed `\n` literals: `'.settings-nav {\n    animation: none !important;'`
- Only these 2 assertions fail because they're the only ones with embedded `\n`; all other assertions use single-line substrings.

- [ ] **Step 1: Run the failing test to confirm the baseline failure**

Run:
```bash
npm --prefix apps/desktop test -- --run src/settings/settingsCssContract.test.ts
```
Expected: 1 failed test — `neutralizes settings-nav animation under reduced motion` — with error:
```
AssertionError: expected '/* ===== Settings Page Styles =====\r…' to contain '.settings-nav {\n    animation: none …'
```

- [ ] **Step 2: Add a normalization helper function**

Modify `apps/desktop/src/settings/settingsCssContract.test.ts` — add this helper after the existing `assertCssBlock` function (after line 10, before line 12's `describe`):

```typescript
function normalizeLineEndings(css: string): string {
  return css.replace(/\r\n/g, '\n')
}
```

- [ ] **Step 3: Apply normalization at both `const css` declarations**

Modify `apps/desktop/src/settings/settingsCssContract.test.ts`:

Line 13 — change:
```typescript
  const css = settingsCss
```
to:
```typescript
  const css = normalizeLineEndings(settingsCss)
```

Line 211 — change:
```typescript
  const css = stylesCss
```
to:
```typescript
  const css = normalizeLineEndings(stylesCss)
```

- [ ] **Step 4: Run the full CSS contract test to verify all 26 tests pass**

Run:
```bash
npm --prefix apps/desktop test -- --run src/settings/settingsCssContract.test.ts
```
Expected: `Test Files  1 passed (1)` / `Tests  26 passed (26)`

- [ ] **Step 5: Run the full desktop test suite to verify no regressions**

Run:
```bash
npm --prefix apps/desktop test
```
Expected: All test files pass, including the 4 settings test files (`settingsCssContract.test.ts`, `SettingsModuleBoundary.test.ts`, `createAsyncSettingsComponent.test.ts`, `useSettingsNavigation.test.ts`, `settingsRegistry.test.ts`).

- [ ] **Step 6: Commit**

```bash
git add apps/desktop/src/settings/settingsCssContract.test.ts
git commit -m "fix(desktop): normalize CRLF line endings in CSS contract test

The settingsCssContract.test.ts imports CSS files via Vite ?raw suffix,
which preserves platform-native line endings (CRLF on Windows). Two
assertions embed \n literals in expected substrings, causing failure on
Windows. Add normalizeLineEndings helper and apply at both const css
declarations so assertions are line-ending-agnostic."
```

---

## Task 2: Verify the running desktop app renders settings correctly (Electron verification)

**Files:**
- No file changes — verification only

**Context for the engineer:**
The CSS contract test guards the settings page's visual contract. After fixing the test, verify the actual running app still renders the settings page correctly by launching the desktop Electron app with remote debugging and taking a screenshot of the settings page.

- [ ] **Step 1: Build the desktop app (if not already built)**

Run:
```bash
npm --prefix apps/desktop run build
```
Expected: Build completes successfully with no errors.

- [ ] **Step 2: Launch the Electron app with remote debugging**

The desktop app's Electron main process is at `apps/desktop/electron/main.cjs`. Launch the built app with the `--remote-debugging-port` flag.

On Windows, the built Electron executable or the dev launch:
```bash
# Option A: Run dev mode (starts Vite + Electron)
npm run dev -w @tinadec/desktop &

# Option B: If dev server is already running, skip this step
```

Wait 5-10 seconds for the app window to appear.

- [ ] **Step 3: Connect agent-browser to the Electron app**

```bash
agent-browser connect 9222
```
Expected: Connection established. If connection refused, ensure the app was launched with `--remote-debugging-port=9222` and wait a few more seconds.

- [ ] **Step 4: Snapshot the main window to find the settings navigation**

```bash
agent-browser snapshot -i
```
Expected: Snapshot output shows the main chat interface with sidebar navigation elements.

- [ ] **Step 5: Navigate to the settings page**

Either click the settings navigation element (identified from the snapshot) or navigate via the hash router:

```bash
# Navigate to settings via hash route (the app uses createWebHashHistory)
agent-browser navigate http://127.0.0.1:5173/#/settings
# Or click the settings button found in the snapshot
# agent-browser click @<settings-button-ref>
```

- [ ] **Step 6: Snapshot the settings page and take a screenshot**

```bash
agent-browser snapshot -i
agent-browser screenshot settings-page.png
```
Expected: Screenshot shows the settings page with the left navigation rail (`.settings-nav`) and content area (`.settings-content`) rendered with correct styling — backgrounds, borders, and typography intact. The `.settings-nav` and `.settings-content` elements should be visible and styled, confirming the CSS extraction did not break rendering.

- [ ] **Step 7: Verify the settings-nav animation neutralization is present in the rendered DOM**

```bash
agent-browser snapshot -i
```
Confirm the `.settings-nav` element exists in the snapshot. The CSS contract test guards the `prefers-reduced-motion` neutralization rules for this element — its presence in the rendered DOM confirms the CSS is loaded and applied.

- [ ] **Step 8: Disconnect and close the app**

```bash
agent-browser disconnect
```

---

## Self-Review

**1. Spec coverage:** The user asked to "fix" the pre-existing CSS contract test failure. Task 1 fixes the test. Task 2 verifies the fix doesn't break rendering. No gaps.

**2. Placeholder scan:** No TBD/TODO/placeholders. All steps contain exact code or exact commands.

**3. Type consistency:** `normalizeLineEndings(css: string): string` — defined in Step 2, called in Step 3 with `settingsCss` (string from `?raw` import) and `stylesCss` (string from `?raw` import). Types match.
