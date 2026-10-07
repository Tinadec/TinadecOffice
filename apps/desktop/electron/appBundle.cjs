/**
 * The `app://bundle` origin — how packaged windows load the renderer.
 *
 * Windows used to load `dist/index.html` through `file://`, which is why every
 * BrowserWindow carried `webSecurity: false`: a file:// renderer's cross-origin
 * fetches to the local gateway have `Origin: null`, no allowlist can match it,
 * and CORS would block the API. Disabling webSecurity turned off same-origin
 * policy for the whole process — while the preview panel iframes arbitrary
 * remote sites — so a hostile page could reach `window.tinadec` and its
 * terminal bridge.
 *
 * A custom standard scheme gives the packaged renderer a real origin with
 * same-origin policy intact; the gateway allowlists that one origin.
 */

const { protocol } = require('electron');
const fs = require('node:fs');
const path = require('node:path');

const APP_BUNDLE_ORIGIN = 'app://bundle';

const MIME_TYPES = {
  html: 'text/html',
  js: 'text/javascript',
  mjs: 'text/javascript',
  css: 'text/css',
  json: 'application/json',
  map: 'application/json',
  svg: 'image/svg+xml',
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  gif: 'image/gif',
  webp: 'image/webp',
  ico: 'image/x-icon',
  woff: 'font/woff',
  woff2: 'font/woff2',
  ttf: 'font/ttf',
};

function distDirectory() {
  return path.join(__dirname, '..', process.env.VITE_DEV_SERVER_URL ? 'public' : 'dist');
}

/**
 * Register the `app` protocol handler. Call once after app.whenReady();
 * the scheme itself must be registered as privileged before ready.
 */
function registerAppBundleProtocol() {
  const distDir = path.normalize(path.join(__dirname, '..', 'dist') + path.sep);
  protocol.handle('app', async (request) => {
    try {
      const url = new URL(request.url);
      if (url.host !== 'bundle') return new Response(null, { status: 404 });
      // Hash routing means only real asset paths resolve; anything else falls
      // back to index.html so deep links survive a refresh.
      const relative = decodeURIComponent(url.pathname).replace(/^\/+/, '') || 'index.html';
      let filePath = path.normalize(path.join(distDir, relative));
      if (!filePath.startsWith(distDir)) return new Response(null, { status: 403 });
      let stat = await fs.promises.stat(filePath).catch(() => null);
      if (stat?.isDirectory()) {
        filePath = path.join(filePath, 'index.html');
        stat = await fs.promises.stat(filePath).catch(() => null);
      }
      if (!stat) {
        filePath = path.join(distDir, 'index.html');
        stat = await fs.promises.stat(filePath).catch(() => null);
        if (!stat) return new Response(null, { status: 404 });
      }
      const ext = path.extname(filePath).slice(1).toLowerCase();
      const body = await fs.promises.readFile(filePath);
      return new Response(body, {
        headers: {
          'Content-Type': MIME_TYPES[ext] ?? 'application/octet-stream',
          'Cache-Control': 'no-cache',
        },
      });
    } catch {
      return new Response(null, { status: 500 });
    }
  });
}

/**
 * URL for a hash-routed app page in packaged builds. `query` entries land in
 * the search part before the hash, mirroring what loadFile(file, {hash, query}) produced.
 */
function appBundleUrl(hash = '', query) {
  const search = query && Object.keys(query).length
    ? `?${new URLSearchParams(query).toString()}`
    : '';
  return `${APP_BUNDLE_ORIGIN}/index.html${search}#${hash}`;
}

/**
 * setWindowOpenHandler that keeps every popup denied but sends http(s) links to
 * the user's browser. Renderer window.open used to be a silent black hole:
 * the About page's repo link, "open API docs", and the preview panel's
 * external-open button all did nothing.
 */
function externalLinkWindowOpenHandler({ url }) {
  if (/^https?:\/\//i.test(url)) {
    require('electron').shell.openExternal(url).catch(() => {});
  }
  return { action: 'deny' };
}

module.exports = {
  APP_BUNDLE_ORIGIN,
  distDirectory,
  registerAppBundleProtocol,
  appBundleUrl,
  externalLinkWindowOpenHandler,
};
