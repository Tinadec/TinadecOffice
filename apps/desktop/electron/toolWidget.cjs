/**
 * Tool-widget scheme: `tinadec-widget://<token>/`.
 *
 * Why this exists: the "information" conversation template lets a tool hand back an HTML view of
 * its own result — a diff reader, a form that asks for the parameters the next call needs, a
 * chart. That HTML is **model-and-tool-authored**, so it is the least trusted thing the product
 * ever renders: it is not our code, and it is not the user's code either.
 *
 * The model for this is the reference products' app surface — an app returned by a tool is served
 * as its own HTML resource into a sandboxed frame — and so is the threat model. Therefore:
 *
 *   - **Nothing is served from disk, and nothing is addressed by path.** A widget is registered
 *     in memory by the renderer, which is the only thing that can obtain the HTML, and the URL
 *     carries an opaque random token. There is no traversal surface because there is no path.
 *   - **One token, read once.** The body is handed out a single time and dropped. A widget frame
 *     that reloads (or a reload triggered by the sandbox dropping it) gets a 404 rather than a
 *     second copy of a resource we already decided was spent.
 *   - **No CORS headers, `corsEnabled: false`.** Cross-origin `fetch()` of a widget URL fails.
 *     The frame reads its own document and nothing else.
 *   - **A document-level CSP is served with every widget**, so the policy travels with the
 *     untrusted bytes rather than depending on the frame's `sandbox` attribute alone.
 *   - **Only a registered token is served.** Registering is a main-process decision; the renderer
 *     asks through IPC, and that is the one entry point that can be audited.
 *
 * Residual capability, stated plainly: a widget runs with `allow-scripts` and no
 * `allow-same-origin`, so it is an opaque origin with no access to the host DOM, its storage, its
 * IPC bridge, or this scheme's other tokens. What it *can* still do is consume CPU and talk to
 * `postMessage`. That is the floor for running someone else's JavaScript at all; the ceiling is
 * enforced by the CSP above plus the host's message validation.
 */

const { randomBytes } = require('node:crypto');

const WIDGET_SCHEME = 'tinadec-widget';

/**
 * Privileges for the scheme itself.
 *
 * `standard` gives it a real origin so a token maps to a document. `secure` is required for the
 * frame to be treated as a secure context (without it some APIs refuse to run at all).
 * `supportFetchAPI` lets the frame read *its own* URL. CORS stays off on purpose.
 */
const WIDGET_SCHEME_PRIVILEGES = {
  standard: true,
  secure: true,
  supportFetchAPI: true,
};

/**
 * What a widget document is allowed to do.
 *
 * `default-src 'none'` first, then each capability granted back one at a time. Notably absent:
 * `connect-src` beyond data:, `frame-src`, `object-src`, `base-uri`, and any form target — a
 * widget cannot phone home, nest frames, or navigate. Inline script and style are allowed because
 * a self-contained widget has nowhere else to put them; `data:` images and fonts are allowed so a
 * chart can draw without reaching the network.
 */
const WIDGET_CSP = [
  "default-src 'none'",
  "script-src 'unsafe-inline'",
  "style-src 'unsafe-inline'",
  "img-src data: blob:",
  "font-src data:",
  "media-src data: blob:",
  "connect-src data:",
  "form-action 'none'",
  "base-uri 'none'",
  // `frame-ancestors 'self'` would be the widget scheme itself, which is never the embedding page:
  // the host is `app://bundle` when packaged and the Vite dev origin in development. Naming both —
  // and nothing else — keeps the rule meaningful instead of making every frame render blank.
  "frame-ancestors app://bundle http://127.0.0.1:5173",
].join('; ');

/**
 * Cap on one widget body. A tool result is already bounded upstream, but a widget is HTML that
 * will be parsed and run, so it gets its own smaller ceiling rather than inheriting that one.
 */
const MAX_WIDGET_BYTES = 512 * 1024;

/**
 * In-memory registry. `Map<token, {html, createdAt}>`, and an entry is deleted the moment it is
 * served — see `takeWidget`.
 *
 * `ponytail:` process-lifetime map with no persistence, swept by age on every register. A widget
 * only has to survive the round trip from "the tool result arrived" to "the frame loaded it", so
 * persisting or partitioning this would be building for a case that cannot happen.
 */
const widgets = new Map();

/** Tokens are dropped after this long even if nothing ever asks for them. */
const WIDGET_TTL_MS = 5 * 60 * 1000;

/** A token that cannot be guessed: 32 bytes of CSPRNG output, base64url, URL-safe as-is. */
function newWidgetToken() {
  return randomBytes(32).toString('base64url');
}

function sweep(now) {
  for (const [token, entry] of widgets) {
    if (now - entry.createdAt > WIDGET_TTL_MS) widgets.delete(token);
  }
}

/**
 * Register a widget body and return its one-time URL.
 *
 * @param {unknown} html
 * @param {{now?: number}} [options]
 * @returns {{url: string, token: string}|null} `null` when the body is unusable, so the caller
 *   falls back to plain text rather than rendering an empty frame.
 */
function registerWidget(html, options = {}) {
  if (typeof html !== 'string') return null;
  // A widget with no markup is a bug in the tool, not an empty view to render.
  if (html.trim().length === 0) return null;
  if (Buffer.byteLength(html, 'utf8') > MAX_WIDGET_BYTES) return null;
  const now = options.now ?? Date.now();
  sweep(now);
  const token = newWidgetToken();
  widgets.set(token, { html, createdAt: now });
  return { url: widgetUrlFor(token), token };
}

/** The URL a token is served at. */
function widgetUrlFor(token) {
  return `${WIDGET_SCHEME}://${token}/`;
}

/** Strip the URL down to its token, rejecting anything that is not exactly one token. */
function tokenFromUrl(url) {
  let parsed;
  try {
    parsed = new URL(String(url));
  } catch {
    return null;
  }
  const token = parsed.hostname;
  if (!/^[A-Za-z0-9_-]{16,128}$/.test(token)) return null;
  return token;
}

/** Forget a widget without serving it (the frame never mounted, the turn was abandoned). */
function dropWidget(token) {
  return widgets.delete(token);
}

/**
 * Take a widget's body, consuming it. Returns `null` when the token is unknown — which includes
 * the second and every later request for the same token.
 */
function takeWidget(token) {
  const entry = widgets.get(token);
  if (!entry) return null;
  widgets.delete(token);
  return entry.html;
}

/** Test/observability helper: how many bodies are outstanding. */
function widgetCount() {
  return widgets.size;
}

/**
 * Serve widget documents. Must run after `app.whenReady()`.
 *
 * @param {{protocol: {handle: Function}}} options
 */
function registerWidgetProtocol({ protocol }) {
  protocol.handle(WIDGET_SCHEME, async (request) => {
    const token = tokenFromUrl(request.url);
    if (!token) return new Response('Not found', { status: 404 });
    const html = takeWidget(token);
    if (html === null) return new Response('Not found', { status: 404 });
    return new Response(html, {
      status: 200,
      headers: {
        'content-type': 'text/html; charset=utf-8',
        // The policy travels with the untrusted bytes; the frame's `sandbox` attribute is the
        // other half, not a substitute for it.
        'content-security-policy': WIDGET_CSP,
        // A consumed widget must not be cached back to life.
        'cache-control': 'no-store',
        // Belt and braces alongside the absent CORS headers.
        'x-content-type-options': 'nosniff',
        'referrer-policy': 'no-referrer',
      },
    });
  });
}

module.exports = {
  MAX_WIDGET_BYTES,
  WIDGET_CSP,
  WIDGET_SCHEME,
  WIDGET_SCHEME_PRIVILEGES,
  dropWidget,
  registerWidget,
  registerWidgetProtocol,
  takeWidget,
  tokenFromUrl,
  widgetCount,
  widgetUrlFor,
};
