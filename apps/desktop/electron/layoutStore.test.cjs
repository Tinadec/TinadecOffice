'use strict'
const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs/promises')
const os = require('node:os')
const path = require('node:path')

// layoutStore requires 'electron' (app.getPath). The test hooks __setUserDataDir
// so the module is loaded but never touches real userData. Electron itself is a
// dependency in dev, but in a bare node:test run it may not resolve — so we stub
// the require before loading the module.
const Module = require('node:module')
const originalLoad = Module._load
Module._load = function (request, parent, isMain) {
  if (request === 'electron') {
    return { app: { getPath: () => tmpRoot } }
  }
  return originalLoad.call(this, request, parent, isMain)
}

const layoutStore = require('./layoutStore.cjs')
// Restore the original loader for subsequent tests.
Module._load = originalLoad

let tmpRoot = ''

test.beforeEach(async () => {
  tmpRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'layout-store-'))
  layoutStore.__setUserDataDir(tmpRoot)
})

test.afterEach(async () => {
  await fs.rm(tmpRoot, { recursive: true, force: true })
  layoutStore.__setUserDataDir(null)
})

test('save + load round-trips a valid payload', async () => {
  const payload = { version: 1, globalByPage: { home: { pageId: 'home' } } }
  await layoutStore.save(payload)
  const loaded = await layoutStore.load()
  assert.deepEqual(loaded, payload)
})

test('writes atomically (file exists after save)', async () => {
  const payload = { version: 1, pageByPageId: { code: { pageId: 'code' } } }
  await layoutStore.save(payload)
  const stat = await fs.stat(path.join(tmpRoot, 'workbench-layout.json'))
  assert.ok(stat.size > 0)
})

test('load returns null for missing file', async () => {
  const loaded = await layoutStore.load()
  assert.equal(loaded, null)
})

test('load returns null for corrupt JSON', async () => {
  await fs.writeFile(path.join(tmpRoot, 'workbench-layout.json'), '{ not valid json', 'utf-8')
  const loaded = await layoutStore.load()
  assert.equal(loaded, null)
})

test('refuses to save an invalid payload', async () => {
  await assert.rejects(() => layoutStore.save({ nope: true }), /invalid payload/)
})

test('refuses to save non-object payloads', async () => {
  await assert.rejects(() => layoutStore.save('hello'), /must be an object/)
})

test('validateLayoutPayload rejects bad layer shapes', () => {
  assert.ok(layoutStore.validateLayoutPayload({ version: 1, globalByPage: 'bad' }))
  assert.equal(layoutStore.validateLayoutPayload({ version: 1, globalByPage: {} }), null)
})
