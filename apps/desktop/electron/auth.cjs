const { app, safeStorage, shell } = require('electron');
const fs = require('node:fs');
const path = require('node:path');

/**
 * Logto OIDC login through the user's system browser. The main window never
 * moves; instead the flow is:
 *
 *   /auth/begin (JSON { authorizeUrl, state })
 *     → shell.openExternal(authorizeUrl)   (system browser, reuse existing SSO)
 *     → Logto → /auth/callback             (Core exchanges code, records state→session)
 *     → poll /auth/check?state=…  until complete
 *     → /auth/me with X-Tinadec-Session
 *
 * The session token stays in this main process and is never handed to the
 * renderer — the renderer only receives the resolved identity over IPC.
 *
 * The session is written to `userData/auth/session.bin` encrypted with Electron's
 * `safeStorage` (DPAPI on Windows, Keychain on macOS, libsecret on Linux). Without that file a
 * launch showed "not signed in" every time, because `sessionToken` only ever lived in this
 * process and the Core child that owns the session was restarted by the same launch.
 */

let sessionToken = null;
let identity = null;
let activeLogin = null;
let restored = false;
let validation = null;
let hostControlToken = null;

/**
 * Every /auth/* route except the browser's callback now requires the private host credential.
 * The token is injected rather than read from the environment: hostControl.cjs generates one
 * itself when no startup token was supplied, so the environment often does not carry it.
 */
function configureAuth({ hostControlToken: token }) {
  if (typeof token !== 'string' || token.length < 40) throw new Error('A private host credential is required.');
  hostControlToken = token;
}

// Throws instead of sending an unauthenticated request: a missing credential is a wiring bug,
// and a silent fallback would turn it into a 403 that looks like a server problem.
function authHeaders(extra = {}) {
  if (!hostControlToken) throw new Error('Auth requires the trusted host credential.');
  return { ...extra, 'x-tinadec-host-control': hostControlToken };
}

const POLL_INTERVAL_MS = 1000;
const POLL_TIMEOUT_MS = 5 * 60 * 1000;
const RETRY_DELAY_MS = 1500;
const VALIDATION_ATTEMPTS = 6;

function gatewayUrl() {
  return process.env.TINADEC_RESOLVED_GATEWAY_URL ?? process.env.TINADEC_GATEWAY_URL ?? 'http://127.0.0.1:48730';
}

function sessionFile() {
  return path.join(app.getPath('userData'), 'auth', 'session.bin');
}

function currentIdentity() {
  return identity ? { sub: identity.sub, nickname: identity.nickname, avatar_url: identity.avatar_url, email: identity.email } : null;
}

function normalizeIdentity(me) {
  return {
    sub: String(me?.subject ?? me?.sub ?? ''),
    nickname: String(me?.nickname ?? ''),
    avatar_url: typeof me?.avatar_url === 'string' ? me.avatar_url : null,
    email: typeof me?.email === 'string' ? me.email : null,
  };
}

/**
 * Decrypts the stored session. Returns null for every failure mode — missing file, no OS keyring,
 * a rotated key, corrupt bytes — because each of them means the same thing to the caller: log in.
 */
function readStoredSession() {
  let raw;
  try {
    raw = fs.readFileSync(sessionFile());
  } catch {
    return null;
  }
  try {
    if (!safeStorage.isEncryptionAvailable()) return null;
    const parsed = JSON.parse(safeStorage.decryptString(raw));
    if (typeof parsed?.token !== 'string' || !parsed.token) return null;
    if (!parsed?.identity || typeof parsed.identity.sub !== 'string') return null;
    return { token: parsed.token, identity: normalizeIdentity(parsed.identity) };
  } catch {
    // A key that can no longer decrypt (OS profile change) is not an error to report: the user
    // signs in once more and the file is rewritten.
    return null;
  }
}

function persistSession() {
  if (!sessionToken || !identity) return;
  // Never write a plaintext session: on a host without a keyring the user signs in next launch,
  // which is annoying but not a credential leak on disk.
  if (!safeStorage.isEncryptionAvailable()) return;
  try {
    const payload = JSON.stringify({ v: 1, token: sessionToken, identity: currentIdentity() });
    const encrypted = safeStorage.encryptString(payload);
    const file = sessionFile();
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const temporary = `${file}.tmp`;
    fs.writeFileSync(temporary, encrypted, { mode: 0o600 });
    fs.renameSync(temporary, file);
  } catch (error) {
    console.warn('[auth] could not persist the session:', error.message);
  }
}

function clearStoredSession() {
  try {
    fs.rmSync(sessionFile(), { force: true });
  } catch (error) {
    console.warn('[auth] could not clear the stored session:', error.message);
  }
}

/**
 * Re-checks a restored session against Core. Only an explicit 401 clears it: at launch the
 * gateway is often not listening yet, and treating a connection refusal as "logged out" was
 * how a valid session would disappear for good.
 */
async function revalidateRestoredSession() {
  const token = sessionToken;
  if (!token) return;
  for (let attempt = 0; attempt < VALIDATION_ATTEMPTS; attempt += 1) {
    try {
      const res = await fetch(`${gatewayUrl()}/auth/me`, { headers: authHeaders({ 'x-tinadec-session': token }) });
      if (sessionToken !== token) return;
      if (res.ok) {
        const me = await res.json();
        identity = normalizeIdentity(me);
        persistSession();
        return;
      }
      if (res.status === 401) {
        sessionToken = null;
        identity = null;
        clearStoredSession();
        return;
      }
      // Any other status is the host's problem, not proof the session died: keep the cached
      // identity and let the next launch try again.
      return;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, RETRY_DELAY_MS));
    }
  }
}

/** Loads the stored session once, then validates it in the background. */
function restoreSession() {
  if (restored) return;
  restored = true;
  const stored = readStoredSession();
  if (!stored) return;
  sessionToken = stored.token;
  identity = stored.identity;
  validation = revalidateRestoredSession();
}

function getAuthState() {
  restoreSession();
  const me = currentIdentity();
  return me ? { loggedIn: true, ...me } : { loggedIn: false };
}

/**
 * Start (or rejoin) a login and resolve with the identity on success, or null
 * when the user cancels, the callback never arrives, or identity resolution
 * fails. A second click while a login is in flight shares the same promise.
 */
function openLogin() {
  if (identity) return Promise.resolve(currentIdentity());
  if (activeLogin) return activeLogin;

  activeLogin = (async () => {
    try {
      const beginRes = await fetch(`${gatewayUrl()}/auth/begin`, { headers: authHeaders() });
      if (!beginRes.ok) throw new Error(`/auth/begin -> ${beginRes.status}`);
      const begin = await beginRes.json();
      const state = String(begin.state ?? '');
      const authorizeUrl = String(begin.authorize_url ?? '');
      if (!state || !authorizeUrl) throw new Error('/auth/begin returned no state or authorizeUrl');

      await shell.openExternal(authorizeUrl);

      const token = await pollForSession(state);
      if (!token) return null;

      sessionToken = token;
      const meRes = await fetch(`${gatewayUrl()}/auth/me`, { headers: authHeaders({ 'x-tinadec-session': token }) });
      if (!meRes.ok) throw new Error(`/auth/me -> ${meRes.status}`);
      const me = await meRes.json();
      identity = normalizeIdentity(me);
      persistSession();
      return currentIdentity();
    } catch (error) {
      console.error('[auth] login failed:', error.message);
      sessionToken = null;
      identity = null;
      clearStoredSession();
      return null;
    } finally {
      activeLogin = null;
    }
  })();

  return activeLogin;
}

/** Poll the login result until it completes or the timeout elapses. */
function pollForSession(state) {
  const deadline = Date.now() + POLL_TIMEOUT_MS;
  return new Promise((resolve) => {
    const tick = async () => {
      try {
        const res = await fetch(`${gatewayUrl()}/auth/check?state=${encodeURIComponent(state)}`, { headers: authHeaders() });
        if (res.ok) {
          const body = await res.json();
          if (body.status === 'complete' && typeof body.session === 'string') {
            resolve(body.session);
            return;
          }
        }
      } catch (error) {
        // Transient network/startup hiccup; keep polling until the deadline.
      }
      if (Date.now() >= deadline) {
        resolve(null);
        return;
      }
      setTimeout(tick, POLL_INTERVAL_MS);
    };
    tick();
  });
}

/**
 * Write the signed-in user's nickname / avatar through to Logto's Account API,
 * then adopt the profile Logto reports back. Cancelling the confirm dialog in the
 * renderer simply never calls this.
 */
async function updateProfile({ nickname, avatarUrl } = {}) {
  if (!sessionToken) throw new Error('updateProfile requires an active login session');
  const body = {};
  if (typeof nickname === 'string' && nickname.trim()) body.nickname = nickname.trim();
  if (typeof avatarUrl === 'string') body.avatar_url = avatarUrl.trim();

  const res = await fetch(`${gatewayUrl()}/auth/profile`, {
    method: 'PATCH',
    headers: authHeaders({ 'content-type': 'application/json', 'x-tinadec-session': sessionToken }),
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const detail = await res.text().catch(() => '');
    throw new Error(`/auth/profile -> ${res.status}: ${detail}`);
  }
  const me = await res.json();
  identity = normalizeIdentity(me);
  persistSession();
  return currentIdentity();
}

async function logout() {
  restoreSession();
  const token = sessionToken;
  sessionToken = null;
  identity = null;
  clearStoredSession();
  if (token) {
    try {
      await fetch(`${gatewayUrl()}/auth/logout`, { method: 'POST', headers: authHeaders({ 'x-tinadec-session': token }) });
    } catch (error) {
      console.warn('[auth] logout revoke failed:', error.message);
    }
  }
  return { loggedIn: false };
}

/** Tests: reset the module-level session, and wait for the startup validation. */
function __resetForTests() {
  sessionToken = null;
  identity = null;
  activeLogin = null;
  restored = false;
  validation = null;
  hostControlToken = null;
}

function __validationForTests() {
  return validation;
}

module.exports = {
  configureAuth,
  openLogin,
  logout,
  getAuthState,
  updateProfile,
  __resetForTests,
  __validationForTests,
};
