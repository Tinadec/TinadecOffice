const HOST_HEADER = 'X-Tinadec-Host-Control';

function managedApiPorts(environment = process.env) {
  const configured = String(environment.TINADEC_MANAGED_API_PORTS ?? '')
    .split(',')
    .map(value => value.trim())
    .filter(value => /^\d{2,5}$/.test(value));
  return new Set(configured.length > 0 ? configured : ['48730', '48731']);
}

function isManagedApiUrl(value) {
  try {
    const url = new URL(value);
    // Identity proof is obtained for this exact IPv4 origin. localhost may
    // resolve to a separate IPv6 listener and must not inherit that proof.
    return url.protocol === 'http:' && url.hostname === '127.0.0.1'
      && managedApiPorts().has(url.port) && !url.username && !url.password
      && (url.pathname === '/api/v1' || url.pathname.startsWith('/api/v1/'));
  } catch { return false; }
}

/** The credential stays in the main process; requests from embedded pages never inherit it. */
function createTrustedHostRequests({ token, devServerUrl, isManagedHost = () => true }) {
  if (typeof token !== 'string' || token.length < 40) throw new Error('A private host credential is required.');
  const windows = new Map();
  const sessions = new WeakSet();
  let devUrl;
  try { devUrl = devServerUrl ? new URL(devServerUrl) : undefined; } catch { /* Invalid dev origins are never trusted. */ }

  function isTrustedDocument(value) {
    try {
      const url = new URL(value);
      if (url.username || url.password) return false;
      if (url.protocol === 'app:' && url.hostname === 'bundle' && !url.port)
        return url.pathname === '/index.html' || url.pathname === '/';
      return Boolean(devUrl && ['http:', 'https:'].includes(devUrl.protocol)
        && ['127.0.0.1', 'localhost'].includes(devUrl.hostname)
        && url.origin === devUrl.origin && (url.pathname === devUrl.pathname || url.pathname === '/index.html'));
    } catch { return false; }
  }

  function authorize(details) {
    try {
      if (!isManagedHost() || !isManagedApiUrl(details.url)
        || details.resourceType === 'mainFrame' || details.resourceType === 'subFrame') return false;
      const entry = windows.get(details.webContentsId);
      const contents = entry?.contents;
      if (!contents || entry.suspended || contents.isDestroyed() || !isTrustedDocument(contents.getURL())) return false;
      return trustedFrame(contents, details.frame);
    } catch { return false; } // A disposed/navigating frame must fail closed.
  }

  function trustedFrame(contents, frame) {
    const mainFrame = contents.mainFrame;
    // Workers have no requesting frame, and previews have a child frame: neither is a trusted host UI.
    return Boolean(frame && mainFrame && !frame.parent && frame.processId === mainFrame.processId
      && frame.routingId === mainFrame.routingId && isTrustedDocument(frame.url));
  }

  function isTrustedDocumentSender(event) {
    try {
      const entry = windows.get(event.sender?.id);
      return Boolean(entry && entry.contents === event.sender && !entry.suspended
        && !event.sender.isDestroyed() && isTrustedDocument(event.sender.getURL()) && trustedFrame(event.sender, event.senderFrame));
    } catch { return false; }
  }

  function isTrustedSender(event) { return isManagedHost() && isTrustedDocumentSender(event); }

  function beforeSendHeaders(details, callback) {
    const headers = { ...details.requestHeaders };
    for (const key of Object.keys(headers)) if (key.toLowerCase() === HOST_HEADER.toLowerCase()) delete headers[key];
    if (authorize(details)) headers[HOST_HEADER] = token;
    callback({ requestHeaders: headers });
  }

  function registerWindow(window) {
    const contents = window.webContents;
    if (windows.has(contents.id)) return;
    const entry = { contents, suspended: !isTrustedDocument(contents.getURL()) };
    windows.set(contents.id, entry);
    if (!sessions.has(contents.session)) {
      sessions.add(contents.session);
      // Include other destinations so redirects cannot carry the private header away from loopback.
      contents.session.webRequest.onBeforeSendHeaders({ urls: ['http://*/*', 'https://*/*', 'ws://*/*', 'wss://*/*'] }, beforeSendHeaders);
    }
    contents.on('did-start-navigation', (details, _url, legacyInPlace, legacyMainFrame) => {
      const main = details.isMainFrame ?? legacyMainFrame;
      const inPlace = details.isInPlace ?? legacyInPlace;
      if (main && !inPlace) entry.suspended = true;
    });
    contents.on('did-navigate', (_event, url) => { entry.suspended = !isTrustedDocument(url); });
    contents.on('destroyed', () => { windows.delete(contents.id); });
  }

  return { registerWindow, beforeSendHeaders, isTrustedDocument, isTrustedDocumentSender, isTrustedSender };
}

let controller;
function initializeTrustedHostRequests(options) {
  if (controller) throw new Error('Trusted host requests are already initialized.');
  controller = createTrustedHostRequests(options);
}
function registerTrustedHostWindow(window) {
  if (!controller) throw new Error('Trusted host requests must be initialized before creating a host window.');
  controller.registerWindow(window);
}
function isTrustedHostSender(event) { return controller?.isTrustedSender(event) ?? false; }
function isTrustedHostDocumentSender(event) { return controller?.isTrustedDocumentSender(event) ?? false; }

module.exports = { createTrustedHostRequests, initializeTrustedHostRequests, registerTrustedHostWindow, isTrustedHostSender, isTrustedHostDocumentSender, isManagedApiUrl };
