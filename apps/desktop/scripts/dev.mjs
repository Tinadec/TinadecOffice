import { spawn } from "child_process";
import { dirname, resolve } from "path";
import { fileURLToPath } from "url";
import http from "http";
import { ensureFreshViteCache, installedVersionResolver } from "./viteCacheGuard.mjs";
import { developmentEnvironment } from "./developmentEnvironment.mjs";
import { developmentBackendIsReady } from "./backendReadiness.mjs";

const __dirname = dirname(fileURLToPath(import.meta.url));
const rootDir = resolve(__dirname, "..");

const isWindows = process.platform === "win32";

// Must run before Vite starts: a dep cache pre-bundled with an older Vue silently serves the old
// runtime against newly compiled SFCs, and the failure shows up as a blank window, not an error.
const cacheDecision = ensureFreshViteCache({ appRoot: rootDir, resolveVersion: installedVersionResolver(rootDir) });
if (cacheDecision.action === "wiped") {
  console.log(`[dev] Cleared node_modules/.vite (dependency stamp ${cacheDecision.reason}).`);
}

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
    shell: isWindows,
    stdio: "pipe",
    env,
  };
}

const viteProcess = isWindows
  ? spawn("npx vite --host 127.0.0.1", [], createSpawnOpts())
  : spawn("npx", ["vite", "--host", "127.0.0.1"], createSpawnOpts());

viteProcess.stdout.on("data", (data) => {
  process.stdout.write(`[vite] ${data}`);
});

viteProcess.stderr.on("data", (data) => {
  process.stderr.write(`[vite] ${data}`);
});

function waitForVite() {
  return new Promise((resolve, reject) => {
    const maxAttempts = 30;
    let attempts = 0;

    const check = () => {
      attempts++;
      if (attempts > maxAttempts) {
        reject(new Error("Vite dev server did not start within 30 seconds"));
        return;
      }

      http
        .get("http://127.0.0.1:5173", (res) => {
          res.resume();
          resolve();
        })
        .on("error", () => {
          setTimeout(check, 1000);
        });
    };

    setTimeout(check, 1000);
  });
}

const GATEWAY_URL = (process.env.TINADEC_GATEWAY_URL ?? "http://127.0.0.1:48730").replace(/\/+$/, "");
const CORE_URL = (process.env.TINADEC_CORE_URL ?? "http://127.0.0.1:48731").replace(/\/+$/, "");
const BACKEND_WAIT_MS = Number.parseInt(process.env.TINADEC_DEV_BACKEND_WAIT_MS ?? "120000", 10);

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
    console.log("[dev] Backend readiness wait skipped (TINADEC_DEV_BACKEND_WAIT_MS<=0).");
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
    : "[dev] Vite is ready, starting the Electron recovery interface...");

  // The UI-automation MCP servers (and the port guard, which already reserves 9222 for "Electron
  // DevTools") expect a debugging endpoint on every dev launch, and without this flag the app is
  // unreachable: an attach attempt just reports "no Electron applications found". Loopback only,
  // and dev-only by construction — the packaged app never runs through this launcher.
  const electronProcess = spawn(
    "npx",
    ["electron", ".", "--remote-debugging-port=9222", "--remote-debugging-address=127.0.0.1"],
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
