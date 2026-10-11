const assert = require('node:assert/strict');
const { test } = require('node:test');

const {
  MAX_WIDGET_BYTES,
  WIDGET_CSP,
  WIDGET_SCHEME,
  dropWidget,
  registerWidget,
  takeWidget,
  tokenFromUrl,
  widgetCount,
  widgetUrlFor,
} = require('./toolWidget.cjs');

test('a registered widget is served exactly once', () => {
  const registered = registerWidget('<p>hi</p>');
  assert.ok(registered);
  assert.equal(takeWidget(registered.token), '<p>hi</p>');
  // The frame must not be able to reload its way to a second copy of a spent resource.
  assert.equal(takeWidget(registered.token), null);
  assert.equal(dropWidget(registered.token), false);
});

test('the URL carries an opaque token and nothing else', () => {
  const registered = registerWidget('<p>hi</p>');
  assert.match(registered.url, new RegExp(`^${WIDGET_SCHEME}://[A-Za-z0-9_-]+/$`));
  assert.equal(tokenFromUrl(registered.url), registered.token);
  // There is no path to traverse: only the authority is read.
  assert.equal(tokenFromUrl(`${WIDGET_SCHEME}://${registered.token}/../../etc/passwd`), registered.token);
  assert.equal(tokenFromUrl('https://example.com/'), null);
  assert.equal(tokenFromUrl('not a url'), null);
  // Too short to be one of ours: a guessable authority is refused before the map is consulted.
  assert.equal(tokenFromUrl(`${WIDGET_SCHEME}://short/`), null);
});

test('an unusable body is refused rather than served as an empty frame', () => {
  assert.equal(registerWidget(''), null);
  assert.equal(registerWidget('   \n  '), null);
  assert.equal(registerWidget(null), null);
  assert.equal(registerWidget(42), null);
  assert.equal(registerWidget(`<p>${'x'.repeat(MAX_WIDGET_BYTES)}</p>`), null);
});

test('dropping a widget revokes it without serving it', () => {
  const registered = registerWidget('<p>gone</p>');
  assert.equal(dropWidget(registered.token), true);
  assert.equal(takeWidget(registered.token), null);
});

test('expired widgets are swept and cannot be served', () => {
  const start = 1_000_000;
  const registered = registerWidget('<p>old</p>', { now: start });
  // A later registration triggers the sweep; the TTL has passed.
  registerWidget('<p>new</p>', { now: start + 5 * 60 * 1000 + 1 });
  assert.equal(takeWidget(registered.token), null);
});

test('distinct widgets get distinct tokens', () => {
  const a = registerWidget('<p>a</p>');
  const b = registerWidget('<p>b</p>');
  assert.notEqual(a.token, b.token);
  assert.equal(takeWidget(a.token), '<p>a</p>');
  assert.equal(takeWidget(b.token), '<p>b</p>');
  // The registry is module-level and shared across cases, so this asks about these two tokens
  // rather than about the global count.
  assert.equal(takeWidget(a.token), null);
  assert.equal(takeWidget(b.token), null);
});

test('the served policy denies the network, nesting and navigation', () => {
  assert.match(WIDGET_CSP, /default-src 'none'/);
  assert.match(WIDGET_CSP, /form-action 'none'/);
  assert.match(WIDGET_CSP, /base-uri 'none'/);
  // A widget may not reach out to anything, so connect-src must not allow https:.
  assert.doesNotMatch(WIDGET_CSP, /connect-src[^;]*https:/);
  assert.doesNotMatch(WIDGET_CSP, /script-src[^;]*unsafe-eval/);
});

test('the URL builder is the inverse of the parser', () => {
  const token = 'A'.repeat(43);
  assert.equal(tokenFromUrl(widgetUrlFor(token)), token);
});
