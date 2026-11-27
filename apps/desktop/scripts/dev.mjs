import { spawn } from "child_process";
import { createRequire } from "module";
import { dirname, resolve } from "path";
import { fileURLToPath } from "url";
import http from "http";
import net from "net";
import { ensureFreshViteCache, installedVersionResolver } from "./viteCacheGuard.mjs";
import { developmentCdpPort, developmentEnvironment } from "./developmentEnvironment.mjs";
import { developmentBackendIsReady } from "./backendReadiness.mjs";

const require = createRequire(import.meta.url);
const __dirname = dirname(fileURLToPath(import.meta.url));
const rootDir = resolve(__dirname, "..");

// Must run before Vite starts: a dep cache pre-bundled with an older Vue silently serves the old
// runtime against newly compiled SFCs, and the failure shows up as a blank window, not an error.
const cacheDecision = ensureFreshViteCache({ appRoot: rootDir, resolveVersion: installedVersionResolver(rootDir) });
if (cacheDecision.action === "wiped") {
  console.log(`[dev] Cleared node_modules/.vite (dependency stamp ${cacheDecision.reason}).`);
}
const cdpPort = developmentCdpPort();
if (cdpPort) console.log(`[dev] Electron CDP enabled on loopback port ${cdpPort}.`);

function createSpawnOpts(extraEnv = {}, trustedHost = false) {
  const env = developmentEnvironment(extraEnv, trustedHost);

  // 从 Electron 宿主（VS Code / CodeBuddy / 任何 Electron 应用）的终端启动时，
  // ELECTRON_RUN_AS_NODE 会被继承下来。一旦存在，electron.exe 就退化成纯 Node
  // 运行时：require('electron') 只返回 electron.exe 的路径字符串，
  // app/BrowserWindow/protocol 全为 undefined，主进程在
  // protocol.registerSchemesAsPrivileged 处崩溃，窗口永远不出现。
  // 该变量对 Vite/Node 无意义，这里统一从子进程环境中剔除。
  return {
    cwd: rootDir,
    shell: false,
    stdio: "pipe",
    env,
  };
}

const viteCliPath = resolve(dirname(require.resolve("vite/package.json")), "bin/vite.js");
const viteProcess = spawn("node", [viteCliPath, "--host", "127.0.0.1"], createSpawnOpts());

viteProcess.stdout.on("data", (data) => {
  process.stdout.write(`[vite] ${data}`);
});

viteProcess.stderr.on("data", (data) => {
  process.stderr.write(`[vite] ${data}`);
});

function waitForVite() {
  // 依赖缓存为空时 Vite 要先做一次依赖预构建：本机实测首次启动 9–120s，视 release/、
  // runtime/ 是否被一起扫描而定（见 .tinadec_dev/evidence/2026-10-18-perf-dev/）。
  // 使用回环 TCP 建连探活（并监听 Vite stdout 的 5173 就绪行即时触发）代替 HTTP GET /，
  // 避免 Bun 下首个 HTTP 请求等待中间件/预热导致 Electron 启动被多拖延十几秒。
  const deadline = Date.now() + 180_000;

  return new Promise((resolve, reject) => {
    let settled = false;
    let pollTimer;
    let currentSocket;

    const cleanup = () => {
      if (pollTimer !== undefined) {
        clearTimeout(pollTimer);
        pollTimer = undefined;
      }
      viteProcess.stdout.off("data", onViteOutput);
      currentSocket?.destroy();
      currentSocket = undefined;
    };

    const finish = (err) => {
      if (settled) return;
      settled = true;
      cleanup();
      if (err) reject(err);
      else resolve();
    };

    const scheduleCheck = (delayMs) => {
      if (settled || pollTimer !== undefined) return;
      pollTimer = setTimeout(() => {
        pollTimer = undefined;
        check();
      }, delayMs);
    };

    const onViteOutput = (data) => {
      if (String(data).includes("127.0.0.1:5173")) {
        if (pollTimer !== undefined) {
          clearTimeout(pollTimer);
          pollTimer = undefined;
        }
        check();
      }
    };

    const check = () => {
      if (settled) return;
      if (viteProcess.exitCode !== null) {
        finish(new Error(`Vite dev server exited before it started listening (exit code ${viteProcess.exitCode})`));
        return;
      }
      if (Date.now() > deadline) {
        finish(new Error("Vite dev server did not start within 180 seconds"));
        return;
      }

      const socket = net.createConnection({ host: "127.0.0.1", port: 5173 });
      currentSocket = socket;
      socket.setTimeout(500);
      socket.once("connect", () => {
        socket.destroy();
        if (currentSocket === socket) currentSocket = undefined;
        finish();
      });
      const retry = () => {
        socket.destroy();
        if (currentSocket === socket) currentSocket = undefined;
        scheduleCheck(250);
      };
      socket.once("timeout", retry);
      socket.once("error", retry);
    };

    viteProcess.stdout.on("data", onViteOutput);
    scheduleCheck(250);
  });
}

const GATEWAY_URL = (process.env.TINADEC_GATEWAY_URL ?? "http://127.0.0.1:48730").replace(/\/+$/, "");
const CORE_URL = (process.env.TINADEC_CORE_URL ?? "http://127.0.0.1:48731").replace(/\/+$/, "");
const BACKEND_WAIT_MS = Number.parseInt(process.env.TINADEC_DEV_BACKEND_WAIT_MS ?? "0", 10);

function probeJson(url) {
  return new Promise((resolve) => {
    const request = http.get(url, { timeout: 2000 }, (res) => {
      let body = "";
      res.setEncoding("utf8");
      res.on("data", (chunk) => {
        body += chunk;
      });
      res.on("end", () => {
        try {
          resolve({ status: res.statusCode ?? 0, data: JSON.parse(body) });
        } catch {
          resolve({ status: res.statusCode ?? 0, data: null });
        }
      });
    });
    request.on("timeout", () => {
      request.destroy();
      resolve(null);
    });
    request.on("error", () => resolve(null));
  });
}

async function backendIsReady() {
  return developmentBackendIsReady({
    gatewayUrl: GATEWAY_URL, coreUrl: CORE_URL,
    token: process.env.TINADEC_HOST_CONTROL_TOKEN, probeJson,
  });
}


async function waitForBackend() {
  if (!Number.isFinite(BACKEND_WAIT_MS) || BACKEND_WAIT_MS <= 0) {
    console.log("[dev] Launching Electron immediately once Vite is ready (set TINADEC_DEV_BACKEND_WAIT_MS>0 to wait for backend first).");
    return false;
  }

  if (await backendIsReady()) {
    console.log("[dev] Backend is already ready.");
    return true;
  }

  const budgetSeconds = Math.round(BACKEND_WAIT_MS / 1000);
  console.log(`[dev] Waiting for the backend before launching Electron (Gateway ${GATEWAY_URL} / Core ${CORE_URL}, up to ${budgetSeconds}s)...`);
  console.log("[dev]   Core 首次启动要先做 dotnet build，通常比 Vite 慢几十秒；不想等就设 TINADEC_DEV_BACKEND_WAIT_MS=0。");

  const deadline = Date.now() + BACKEND_WAIT_MS;
  let waited = 0;
  while (Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 1000));
    waited += 1;
    if (await backendIsReady()) {
      console.log(`[dev] Backend is ready after ${waited}s, starting Electron...`);
      return true;
    }
    if (waited % 15 === 0) console.log(`[dev]   still waiting for the backend (${waited}s)...`);
  }

  console.warn(`[dev] Core/Gateway did not prove a ready shared development host within ${budgetSeconds}s. Opening the Electron recovery interface; host verification still blocks business requests. Start all hosts with npm run dev, then retry in the app.`);
  return false;
}

async function main() {
  let backendReady = false;
  try {
    [, backendReady] = await Promise.all([waitForVite(), waitForBackend()]);
  } catch (err) {
    console.error(err.message);
    viteProcess.kill();
    process.exit(1);
  }

  console.log(backendReady
    ? "[dev] Vite and the shared backend are ready, starting Electron..."
    : "[dev] Vite is ready, starting Electron immediately (AppSplash will hold until backend connects or 15s fallback)...");

  const electronBinary = require("electron");
  const electronArgs = [
    ...(cdpPort ? [`--remote-debugging-port=${cdpPort}`] : []),
    ".",
  ];
  const electronProcess = spawn(
    electronBinary,
    electronArgs,
    createSpawnOpts({ VITE_DEV_SERVER_URL: "http://127.0.0.1:5173" }, true),
  );

  electronProcess.stdout.on("data", (data) => {
    process.stdout.write(`[electron] ${data}`);
  });

  electronProcess.stderr.on("data", (data) => {
    process.stderr.write(`[electron] ${data}`);
  });

  electronProcess.on("exit", (code) => {
    console.log(`[electron] exited with code ${code}`);
    viteProcess.kill();
    process.exit(code ?? 0);
  });

  const cleanup = () => {
    viteProcess.kill();
    electronProcess.kill();
    process.exit();
  };

  process.on("SIGINT", cleanup);
  process.on("SIGTERM", cleanup);
}

main();
