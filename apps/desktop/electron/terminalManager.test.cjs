'use strict'
const test = require('node:test')
const assert = require('node:assert/strict')
const path = require('node:path')

// terminalManager requires 'electron' (ipcMain, BrowserWindow) and 'node-pty'.
// Neither is usable in a bare node:test run — and a real PTY would make the
// assertions depend on the host shell — so both are stubbed before loading.
const Module = require('node:module')
const originalLoad = Module._load

/** @type {Array<{channel: string, payload: unknown, windowId: number}>} */
const sends = []
/** @type {Array<object>} */
let liveWindows = []

function makeFakeWindow(id) {
  return {
    id,
    isDestroyed: () => false,
    webContents: {
      send: (channel, payload) => sends.push({ channel, payload, windowId: id }),
    },
  }
}

/** @type {Array<{options: object, settings: object}>} */
const ptySpawns = []
/** @type {Array<(data: string) => void>} */
let ptyDataHandlers = []
let ptyExitHandler = null
let ptySpawnError = null

const fakePty = {
  spawn(shell, args, settings) {
    ptySpawns.push({ shell, args, settings })
    if (ptySpawnError) throw ptySpawnError
    ptyDataHandlers = []
    return {
      onData(cb) { ptyDataHandlers.push(cb) },
      onExit(cb) { ptyExitHandler = cb },
      write() {},
      resize() {},
      kill() {},
    }
  },
}

Module._load = function (request, parent, isMain) {
  if (request === 'electron') {
    return {
      ipcMain: { handle() {}, on() {} },
      BrowserWindow: { getAllWindows: () => liveWindows.map((w) => w) },
      app: {},
    }
  }
  if (request === 'node-pty') return fakePty
  return originalLoad.call(this, request, parent, isMain)
}

// A synchronous exec on the create path used to stall the main process for up to
// three seconds behind `wsl --list`. Keep it poisoned for the whole file.
const childProcess = require('node:child_process')
const realExecSync = childProcess.execSync
childProcess.execSync = () => {
  throw new Error('execSync must not run on the terminal create path')
}

const terminalManager = require('./terminalManager.cjs')

test.after(() => {
  childProcess.execSync = realExecSync
  Module._load = originalLoad
})

test.beforeEach(() => {
  // The registry is module state; a leftover terminal's pending flush would land in
  // the next case's send log.
  terminalManager.destroyAllTerminals()
  sends.length = 0
  ptySpawns.length = 0
  ptySpawnError = null
  liveWindows = [makeFakeWindow(1)]
  // Every window is a terminal host unless a case narrows this.
  terminalManager.setTerminalHostFilter(() => liveWindows)
})

function emit(id, data) {
  for (const handler of ptyDataHandlers) handler(data)
}

test('createTerminal reports the PTY backend instead of degrading silently', async () => {
  const result = terminalManager.createTerminal({ cols: 100, rows: 30 })

  assert.match(result.id, /^term-\d+$/)
  assert.equal(result.backend, 'pty')
  assert.equal(result.error, undefined)
  assert.equal(ptySpawns.length, 1)
  assert.ok(path.isAbsolute(ptySpawns[0].shell), `expected absolute shell, got ${ptySpawns[0].shell}`)
})

// The id is server-generated: honoring a client-chosen id would let a create
// overwrite a live Map entry and orphan its PTY. Cases create first and use
// the returned id.
function createTerminal(options = {}) {
  const result = terminalManager.createTerminal(options)
  assert.ok(result.id, `createTerminal failed: ${result.error}`)
  return result.id
}

test('createTerminal refuses shells outside the catalog', () => {
  const result = terminalManager.createTerminal({ shell: 'C:\\Windows\\System32\\notepad.exe' })

  assert.equal(result.id, null)
  assert.match(result.error, /allowed catalog/)
  assert.equal(ptySpawns.length, 0)
})

test('createTerminal accepts a catalog shell and takes its args from the catalog', () => {
  const [profile] = terminalManager.getAvailableShells()

  const id = createTerminal({ shell: profile.shell, args: ['--evil-flag'] })

  assert.ok(id)
  assert.equal(ptySpawns[0].shell, profile.shell)
  assert.deepEqual(ptySpawns[0].args, profile.args)
})

test('createTerminal fails loudly when the addon cannot spawn', async () => {
  ptySpawnError = new Error('The specified path is invalid.')

  const result = terminalManager.createTerminal({})

  // The previous behaviour was an implicit pipes-only downgrade: no echo, no
  // prompt, no resize, and nothing to tell the user why the box stayed blank.
  assert.equal(result.id, null)
  assert.equal(result.backend, null)
  assert.match(result.error, /path is invalid/)
})

test('getDefaultShell never returns a bare executable name', () => {
  const { shell } = terminalManager.getDefaultShell()
  if (process.platform === 'win32') {
    assert.ok(path.isAbsolute(shell), `expected an absolute Windows shell, got ${shell}`)
  }
})

test('shell catalog exposes no bare powershell.exe', () => {
  const shells = terminalManager.getAvailableShells()
  assert.ok(shells.length > 0)
  for (const profile of shells) {
    if (process.platform === 'win32') {
      assert.ok(path.isAbsolute(profile.shell), `${profile.id} resolves to ${profile.shell}`)
    }
    assert.notEqual(profile.shell, 'powershell.exe')
    assert.notEqual(profile.shell, 'cmd.exe')
  }
})

test('output is coalesced into one send per window', async () => {
  const id = createTerminal()
  emit(id, 'a')
  emit(id, 'b')
  emit(id, 'c')

  assert.deepEqual(sends, [], 'nothing should cross IPC before the flush window closes')
  await new Promise((resolve) => setTimeout(resolve, 40))

  const dataSends = sends.filter((s) => s.channel === `terminal:data:${id}`)
  assert.equal(dataSends.length, 1)
  assert.equal(dataSends[0].payload, 'abc')
})

test('a late snapshot returns output printed before it subscribed', () => {
  const id = createTerminal()
  emit(id, 'banner\r\n')
  emit(id, 'C:\\work> ')

  const snapshot = terminalManager.readTerminalSnapshot(id)
  assert.equal(snapshot.replay, 'banner\r\nC:\\work> ')
  assert.equal(snapshot.exited, false)
})

test('output reaches only declared terminal hosts', async () => {
  const host = makeFakeWindow(10)
  const petWindow = makeFakeWindow(11)
  liveWindows = [host, petWindow]
  terminalManager.setTerminalHostFilter(() => [host])

  const id = createTerminal()
  emit(id, 'hello')
  await new Promise((resolve) => setTimeout(resolve, 40))

  assert.equal(sends.length, 1)
  assert.equal(sends[0].windowId, 10, 'a window that cannot show a terminal received output')
})

test('exit flushes pending output before reporting', async () => {
  const id = createTerminal()
  emit(id, 'last words')
  ptyExitHandler({ exitCode: 3, signal: undefined })

  const channels = sends.map((s) => s.channel)
  assert.deepEqual(channels, [`terminal:data:${id}`, `terminal:exit:${id}`])
  const snapshot = terminalManager.readTerminalSnapshot(id)
  assert.equal(snapshot.exited, true)
  assert.equal(snapshot.exitCode, 3)
})

test('destroy drops the entry so nothing stays reachable', () => {
  const id = createTerminal()
  emit(id, 'x')
  terminalManager.destroyTerminal(id)

  assert.equal(terminalManager.readTerminalSnapshot(id), null)
  assert.deepEqual(terminalManager.listTerminals(), [])
})

test('resize refuses a zero size measured against a hidden host', () => {
  const id = createTerminal({ cols: 80, rows: 24 })
  const entry = terminalManager.getTerminal(id)

  terminalManager.resizeTerminal(id, 0, 1)
  assert.equal(entry.cols, 80)

  terminalManager.resizeTerminal(id, 120, 40)
  assert.equal(entry.cols, 120)
  assert.equal(entry.rows, 40)
})
