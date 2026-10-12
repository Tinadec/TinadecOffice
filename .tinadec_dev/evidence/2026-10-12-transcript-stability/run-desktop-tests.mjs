import { spawn } from 'node:child_process';
import { openSync, closeSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

const repo = resolve(import.meta.dirname, '../../..');
const logPath = resolve(repo, '.tinadec_dev/tmp/transcript-vitest-verified-all.log');
const receiptPath = resolve(import.meta.dirname, 'desktop-full-verified.json');
const fd = openSync(logPath, 'w');
const started = Date.now();
const child = spawn(process.execPath, [resolve(repo, 'node_modules/vitest/vitest.mjs'), 'run', '--maxWorkers=1'], {
  cwd: resolve(repo, 'apps/desktop'), windowsHide: true, stdio: ['ignore', fd, fd],
  env: { ...process.env, NO_COLOR: '1' },
});
closeSync(fd);
let timedOut = false;
const timer = setTimeout(() => {
  timedOut = true;
  spawn('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
}, 2700000);
child.once('error', error => {
  clearTimeout(timer);
  writeFileSync(receiptPath, JSON.stringify({ accepted: false, error: String(error), checked_at: new Date().toISOString() }, null, 2));
  process.exitCode = 1;
});
child.once('exit', (code, signal) => {
  clearTimeout(timer);
  const log = readFileSync(logPath, 'utf8');
  const receipt = {
    accepted: code === 0 && !timedOut, exit_code: code, signal, timed_out: timedOut,
    duration_seconds: (Date.now() - started) / 1000,
    command: 'node node_modules/vitest/vitest.mjs run --maxWorkers=1', cwd: 'apps/desktop',
    log: '.tinadec_dev/tmp/transcript-vitest-verified-all.log', final_output: log.slice(-9000),
    checked_at: new Date().toISOString(),
    boundary: 'Renderer Vitest full suite. Native/scripts, lockfile, typecheck and build have separate receipts.',
  };
  writeFileSync(receiptPath, JSON.stringify(receipt, null, 2));
  console.log(JSON.stringify({ accepted: receipt.accepted, exit_code: code, duration_seconds: receipt.duration_seconds }));
  process.exitCode = receipt.accepted ? 0 : 1;
});
