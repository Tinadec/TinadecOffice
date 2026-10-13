# APP-HOME-108 visual evidence (2026-10-13)

Captured in headless Chromium (Playwright 1.x, chromium-headless-shell 156) against the Vite
renderer dev server alone (`npx vite --host 127.0.0.1 --port 5173` in `apps/desktop`), with no
Electron, Core or Gateway. Debug Studio is enabled through a stub `window.tinadec.getAppConfig`.
The "对话消息流" preview renders the real `ConversationView` over the shared fixtures in
`apps/desktop/src/lib/conversation/fixtures` (reduced by the real reducer); the "聊天面板"
preview renders the real `ChatPanel` with the `explore-edit-shell` fixture.

Harness-only adjustments (not product changes): the notification island and the
host-mismatch banner, which appear because no Gateway is running, are hidden before shooting;
narrow widths are reached by opening the preview wide and then shrinking the viewport, because
Debug Studio's own tab bar hides its Preview tab below ~800px.

- `screenshots/w1169-dark-*.png` — all seven scenarios, dark theme, frosted material, plus the
  settled explore/edit/shell turn with its work fold and exploration group opened.
- `screenshots/w800-*`, `w520-*`, `w380-*` — narrower widths of the key scenarios.
- `screenshots/w1169-light-*`, `w1169-opaque-*` — light theme and opaque material.
- `screenshots/chatpanel-w1169.png`, `chatpanel-w380.png` — the real ChatPanel; at 380px it
  carries `chat-narrow chat-ultra`.
- `screenshots/shimmer-phase-1.png` — the shimmer mid-sweep after the keyframe fix.
- `screenshots/followup-w{1169,380,300}-next-day-stopped.png` — the follow-up items: the date
  line between yesterday's and today's turn, yesterday's short interim answer as a clamped
  two-line note, today's stopped turn with its retry button (`harness/followup.mjs`; at 300px
  the note ends in an ellipsis).
- `screenshots/followup-w{1169,380}-loading-skeleton.png` — the transcript skeleton shown by
  the preview's "加载中" switch after its 200ms delay.
- `screenshots/followup-rich-*.png` — the `rich-answer` scenario: a diagram-only interim answer,
  then a Markdown image, a mermaid flowchart and a table (`harness/rich.mjs`). Before the fix the
  two diagrams drew into each other (every diagram was rendered as `md-diagram-1`).
- `screenshots/followup-narrow-diagram-w380.png` — at 380px the flowchart stops shrinking at 80% and
  scrolls sideways from its left edge (`harness/narrow.mjs` prints scale and label size per width).
- `computed-styles.txt` — computed `animation-name` with and without `prefers-reduced-motion`.
- `harness/*.mjs` — the scripts (they import `playwright` from a tools directory outside the repo).

Defects these captures found and that were fixed in the same change: shimmering labels were
invisible for most of the animation cycle (keyframes moved the text-painting gradient outside the
text box; also affected space mode's `.chat-shimmer`), and the answer footer reported a different
"worked for" duration than the turn's work summary.

Defects the follow-up captures found and fixed before commit: the compact note kept full-size
text because the markdown body sets its own size and colour, and the skeleton rendered below the
fold because the scroller content fills the viewport height (it is now drawn over the viewport).

Not covered: the Electron window itself, real model runs, Windows/macOS rendering, the unread
count under real scrolling (its component test stubs the scroller state).
