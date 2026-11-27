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
// 9222 是 Electron 的远程调试口，不是服务监听端口：它被占着不会让 dev 启动失败，
// 只影响 DevTools 能不能连上。所以它不参与“要不要走完整检查”的判断，也不阻塞启动。
const BLOCKING_PORTS = [48730, 48731, 5173];
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

// 快速路径：绝大多数启动都没有残留监听者。netstat 读一遍约 80ms，而下面那次
// PowerShell 查询要拉起一个 Windows PowerShell 5 进程并逐端口 Get-CimInstance，
// 实测稳定 5.4s——这是每次 `npm run dev` 的固定开销。
// 只在「确实有本项目的 dev 端口在监听」时才回落到 PowerShell 判归属，
// 判定逻辑本身一行未改。netstat 不可用或输出看不懂时同样回落，
// 所以省时间永远不会变成漏检。
function listeningDevPorts() {
  const raw = run('netstat', ['-ano', '-p', 'TCP']);
  if (!raw) return null; // 查不到就按「不知道」处理，走完整检查
  const listening = new Set();
  for (const line of raw.split(/\r?\n/)) {
    const match = /^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+\d+\s*$/i.exec(line);
    if (match) listening.add(Number(match[1]));
  }
  return listening;
}

function hasRunningCoreProcess() {
  const raw = run('tasklist', ['/FI', 'IMAGENAME eq TinadecCore.Api.exe', '/FO', 'CSV', '/NH']);
  return Boolean(raw && /tinadeccore\.api\.exe/i.test(raw));
}

const normalizedRoot = repoRoot.toLowerCase().replace(/\//g, '\\');
const listeningPorts = listeningDevPorts();
const hasBlockingPort = listeningPorts === null || BLOCKING_PORTS.some((port) => listeningPorts.has(port));
const checkStalledCore = hasRunningCoreProcess();

if (!hasBlockingPort && !checkStalledCore) {
  // 三个服务端口都没人监听且无本仓库 Core 进程残留，直接放行。
  // 9222 是 DevTools 口，被占只影响调试连接，不为它拉一次 PowerShell。
  if (listeningPorts?.has(9222)) {
    console.log('[dev-port-guard] 端口 9222（Electron DevTools）已被占用，不影响 dev 启动，跳过检查。');
  }
  process.exit(0);
}

// 单次 PowerShell 调用取回「端口监听者 + 卡在启动期未绑端口的本仓库 TinadecCore.Api.exe」，脚本内不使用双引号以免转义问题。
const PS_QUERY = [
  `$ports = ${PORTS.join(',')};`,
  '$items = @();',
  'Get-NetTCPConnection -State Listen -LocalPort $ports -ErrorAction SilentlyContinue |',
  "ForEach-Object { $p = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $_.OwningProcess) -ErrorAction SilentlyContinue;",
  '$items += [pscustomobject]@{ port = $_.LocalPort; pid = [int]$_.OwningProcess; name = $p.Name; execPath = $p.ExecutablePath; cmdline = $p.CommandLine } };',
  "Get-Process -Name 'TinadecCore.Api' -ErrorAction SilentlyContinue | ForEach-Object { Get-CimInstance Win32_Process -Filter ('ProcessId=' + $_.Id) -ErrorAction SilentlyContinue } |",
  'ForEach-Object { $proc = $_; if (-not ($items | Where-Object { $_.pid -eq [int]$proc.ProcessId })) {',
  '$items += [pscustomobject]@{ port = 0; pid = [int]$proc.ProcessId; name = $proc.Name; execPath = $proc.ExecutablePath; cmdline = $proc.CommandLine } } };',
  'if ($items.Count -gt 0) { $items | ConvertTo-Json -Compress }',
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

function isOwnDevProcess(listener) {
  const name = (listener.name ?? '').toLowerCase();
  const cmdline = (listener.cmdline ?? '').toLowerCase().replace(/\//g, '\\');
  const execPath = (listener.execPath ?? '').toLowerCase().replace(/\//g, '\\');
  if (execPath.includes(normalizedRoot) || cmdline.includes(normalizedRoot)) return true;
  // 未监听端口的外部仓库 TinadecCore.Api.exe（如其他分支/实例）绝不误杀
  if (listener.port === 0) return false;
  if (name === 'tinadeccore.api.exe' && !execPath && !cmdline) return true;
  if (name === 'bun.exe' && /--watch\s+src[\\/]index\.ts/.test(cmdline)) return true;
  return false;
}

const reclaimed = [];
const foreign = [];
const advisory = [];
const killedPids = new Set();

for (const listener of listeners) {
  const label = listener.port === 0 ? 'Core (未完成启动残留)' : (PORT_LABELS[listener.port] ?? 'dev service');
  const who = `${listener.name ?? 'unknown'} (pid ${listener.pid})`;
  if (!isOwnDevProcess(listener)) {
    if (listener.port === 0) continue;
    // 9222 被仓库外的进程占着时只提示，不阻塞启动：dev 本身不需要这个端口。
    if (listener.port === 9222) {
      advisory.push({ ...listener, label, who });
      continue;
    }
    foreign.push({ ...listener, label, who });
    continue;
  }
  if (killedPids.has(listener.pid)) continue;
  killedPids.add(listener.pid);
  if (reportOnly) {
    reclaimed.push({ ...listener, label, who });
    continue;
  }
  run('taskkill', ['/PID', String(listener.pid), '/T', '/F']);
  reclaimed.push({ ...listener, label, who });
}

for (const item of reclaimed) {
  const verb = reportOnly ? '占用中' : '已回收';
  const target = item.port === 0 ? item.label : `端口 ${item.port}（${item.label}）`;
  console.log(`[dev-port-guard] ${verb}${target}：残留的 ${item.who}`);
}
if (reclaimed.length > 0 && reportOnly) {
  console.log('[dev-port-guard] report 模式不回收，以上端口仍被占用，停止启动。');
  console.log('[dev-port-guard] 需要自动回收时去掉 TINADEC_DEV_PORT_GUARD=report 再运行 `npm run dev`。');
  process.exit(1);
}
if (reclaimed.length > 0) {
  console.log('[dev-port-guard] 上一次 dev 运行的残留进程已结束，继续启动。');
}
for (const item of advisory) {
  console.log(`[dev-port-guard] 端口 ${item.port}（${item.label}）被 ${item.who} 占用：不影响 dev 启动，只影响 DevTools。`);
}
if (foreign.length > 0) {
  console.log('[dev-port-guard] 以下端口被本仓库之外的进程占用，已停止启动：');
  for (const item of foreign) {
    console.log(`[dev-port-guard]   端口 ${item.port}（${item.label}）<- ${item.who}`);
  }
  console.log('[dev-port-guard] 请自行确认后释放，例如：taskkill /PID <pid> /T /F');
  process.exit(1);
}
