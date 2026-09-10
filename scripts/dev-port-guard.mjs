#!/usr/bin/env node
/**
 * `npm run dev` 的前置守卫：回收上一次 dev 运行残留在本项目 dev 端口上的进程。
 *
 * 为什么需要：`dev:core`/`dev:gateway`/`dev:desktop` 都是多层包装
 * （concurrently → npm → powershell/bun → 真正的服务进程），终端被直接关闭、
 * 或者某一层异常退出时，最底层的 `TinadecCore.Api.exe` / `bun --watch` /
 * `vite` / `electron.exe` 会成为孤儿继续占着端口。下一次 `npm run dev` 只会
 * 看到 Kestrel 的 "Failed to bind to address ... address already in use" 堆栈，
 * 很难看出是残留进程导致。
 *
 * 行为：
 * - 只回收「本项目自己的」监听者（命令行含仓库根路径、`TinadecCore.Api.exe`、
 *   或 `bun --watch src/index.ts`）；其它进程一律不动，改为报错退出。
 * - 回收用 `taskkill /PID <pid> /T /F`，连子进程一起结束。
 * - 环境变量 `TINADEC_DEV_PORT_GUARD=report` 只报告不回收；
 *   `=off` 完全跳过。
 */
import { execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const PORTS = [48730, 48731, 5173, 9222];
const PORT_LABELS = {
  48730: 'Gateway',
  48731: 'Core',
  5173: 'Vite dev server',
  9222: 'Electron DevTools',
};

const mode = (process.env.TINADEC_DEV_PORT_GUARD ?? '').trim().toLowerCase();
if (['off', '0', 'false', 'no'].includes(mode)) {
  console.log('[dev-port-guard] skipped (TINADEC_DEV_PORT_GUARD=off)');
  process.exit(0);
}
if (process.platform !== 'win32') {
  console.log('[dev-port-guard] 仅 Windows 自动回收 dev 端口，跳过检查。');
  process.exit(0);
}

const reportOnly = mode === 'report';

function run(command, args) {
  try {
    return execFileSync(command, args, { encoding: 'utf8', windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch {
    return '';
  }
}

// 单次 PowerShell 调用取回「端口 → 进程名 + 命令行」，脚本内不使用双引号以免转义问题。
const PS_QUERY = [
  `$ports = ${PORTS.join(',')};`,
  'Get-NetTCPConnection -State Listen -LocalPort $ports -ErrorAction SilentlyContinue |',
  "ForEach-Object { $p = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $_.OwningProcess) -ErrorAction SilentlyContinue;",
  '[pscustomobject]@{ port = $_.LocalPort; pid = $_.OwningProcess; name = $p.Name; cmdline = $p.CommandLine } } |',
  'ConvertTo-Json -Compress',
].join(' ');

let listeners = [];
const raw = run('powershell', ['-NoProfile', '-NonInteractive', '-Command', PS_QUERY]);
if (raw && raw !== 'null') {
  try {
    const parsed = JSON.parse(raw);
    listeners = Array.isArray(parsed) ? parsed : [parsed];
  } catch {
    console.log('[dev-port-guard] 无法解析端口查询结果，跳过检查。');
    process.exit(0);
  }
}

if (listeners.length === 0) {
  process.exit(0);
}

const normalizedRoot = repoRoot.toLowerCase().replace(/\//g, '\\');

function isOwnDevProcess(listener) {
  const name = (listener.name ?? '').toLowerCase();
  const cmdline = (listener.cmdline ?? '').toLowerCase();
  if (name === 'tinadeccore.api.exe') return true;
  if (name === 'bun.exe' && /--watch\s+src[\\/]index\.ts/.test(cmdline)) return true;
  return cmdline.includes(normalizedRoot);
}

const reclaimed = [];
const foreign = [];

for (const listener of listeners) {
  const label = PORT_LABELS[listener.port] ?? 'dev service';
  const who = `${listener.name ?? 'unknown'} (pid ${listener.pid})`;
  if (!isOwnDevProcess(listener)) {
    foreign.push({ ...listener, label, who });
    continue;
  }
  if (reportOnly) {
    reclaimed.push({ ...listener, label, who });
    continue;
  }
  run('taskkill', ['/PID', String(listener.pid), '/T', '/F']);
  reclaimed.push({ ...listener, label, who });
}

for (const item of reclaimed) {
  const verb = reportOnly ? '占用中' : '已回收';
  console.log(`[dev-port-guard] ${verb}端口 ${item.port}（${item.label}）：残留的 ${item.who}`);
}
if (reclaimed.length > 0 && reportOnly) {
  console.log('[dev-port-guard] report 模式不回收，以上端口仍被占用，停止启动。');
  console.log('[dev-port-guard] 需要自动回收时去掉 TINADEC_DEV_PORT_GUARD=report 再运行 `npm run dev`。');
  process.exit(1);
}
if (reclaimed.length > 0) {
  console.log('[dev-port-guard] 上一次 dev 运行的残留进程已结束，继续启动。');
}
if (foreign.length > 0) {
  console.log('[dev-port-guard] 以下端口被本仓库之外的进程占用，已停止启动：');
  for (const item of foreign) {
    console.log(`[dev-port-guard]   端口 ${item.port}（${item.label}）<- ${item.who}`);
  }
  console.log('[dev-port-guard] 请自行确认后释放，例如：taskkill /PID <pid> /T /F');
  process.exit(1);
}
