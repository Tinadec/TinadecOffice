'use strict'
const test = require('node:test')
const assert = require('node:assert/strict')
const path = require('node:path')

const {
  APP_ORIGIN,
  appBundleUrl,
  contentTypeFor,
  resolveBundleAsset,
} = require('./appBundle.cjs')

const DIST = path.join(__dirname, '..', 'dist')

test('a bare request serves the SPA entry point', () => {
  assert.equal(resolveBundleAsset(DIST, '/'), path.join(DIST, 'index.html'))
  assert.equal(resolveBundleAsset(DIST, ''), path.join(DIST, 'index.html'))
})

test('hashed assets resolve inside the bundle', () => {
  assert.equal(resolveBundleAsset(DIST, '/assets/index-abc123.js'), path.join(DIST, 'assets', 'index-abc123.js'))
  assert.equal(resolveBundleAsset(DIST, '/tinadec.ico'), path.join(DIST, 'tinadec.ico'))
})

test('traversal never leaves the bundle directory', () => {
  for (const attempt of [
    '/../secret.txt',
    '/../../etc/passwd',
    '/assets/../../secret.txt',
    '..\\..\\secret.txt',
    // Drive-qualified segments are absolute on Windows and relative on POSIX, so the same
    // request read outside the bundle on one platform and inside it on the other. Both the
    // leading-slash and the bare form are rejected, and this file runs on all three legs.
    '/C:/Windows/win.ini',
    'C:/Windows/win.ini',
    'C:../secret.txt',
    '/\0index.html',
  ]) {
    assert.equal(resolveBundleAsset(DIST, attempt), null, `escaped the bundle: ${attempt}`)
  }

  // resolveBundleAsset takes an already-decoded pathname, so a percent-encoded traversal is
  // caught by the same call once the handler has decoded it — this is the handler's pipeline.
  assert.equal(resolveBundleAsset(DIST, decodeURIComponent('/..%2f..%2fetc%2fpasswd')), null)
})

test('a sibling directory sharing the prefix is not inside the bundle', () => {
  // `dist-evil` starts with `dist`, so a naive startsWith(root) check would accept it.
  assert.equal(resolveBundleAsset(DIST, '/../dist-evil/x.js'), null)
})

test('content types are explicit and fall back to octet-stream', () => {
  assert.match(contentTypeFor('index.html'), /^text\/html/)
  assert.match(contentTypeFor('app.js'), /^text\/javascript/)
  assert.equal(contentTypeFor('font.woff2'), 'font/woff2')
  assert.equal(contentTypeFor('blob.bin'), 'application/octet-stream')
})

test('the packaged URL keeps the hash route and the splash flag', () => {
  assert.equal(appBundleUrl(), `${APP_ORIGIN}/index.html`)
  assert.equal(
    appBundleUrl({ hash: '/pet?instanceId=abc', query: { splash: '0' } }),
    `${APP_ORIGIN}/index.html?splash=0#/pet?instanceId=abc`,
  )
  assert.equal(
    appBundleUrl({ hash: '/settings', query: { splash: '0', skip: null, drop: undefined } }),
    `${APP_ORIGIN}/index.html?splash=0#/settings`,
  )
})
