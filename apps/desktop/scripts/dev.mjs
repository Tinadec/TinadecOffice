import { spawn } from "child_process";
import { dirname, resolve } from "path";
import { fileURLToPath } from "url";
import http from "http";

const __dirname = dirname(fileURLToPath(import.meta.url));
const rootDir = resolve(__dirname, "..");

const isWindows = process.platform === "win32";

function createSpawnOpts(extraEnv = {}) {
  const env = { ...process.env, ...extraEnv };

  // 从 Electron 宿主（VS Code / CodeBuddy / 任何 Electron 应用）的终端启动时，
  // ELECTRON_RUN_AS_NODE 会被继承下来。一旦存在，electron.exe 就退化成纯 Node
  // 运行时：require('electron') 只返回 electron.exe 的路径字符串，
  // app/BrowserWindow/protocol 全为 undefined，主进程在
  // protocol.registerSchemesAsPrivileged 处崩溃，窗口永远不出现。
  // 该变量对 Vite/Node 无意义，这里统一从子进程环境中剔除。
  delete env.ELECTRON_RUN_AS_NODE;
  delete env.ELECTRON_NO_ATTACH_CONSOLE;

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
  // Gateway 的 /api/v1/health 在自身健康但上游 Core 不可达时返回 503 + core_status:
  // "unreachable"，就绪时才返回 200 + core_status: "ready"，所以它是现成的就绪信号。
  const gateway = await probeJson(`${GATEWAY_URL}/api/v1/health`);
  if (gateway && gateway.status === 200 && gateway.data?.core_status === "ready") return true;
  // 只跑 Core（没有 Gateway）的场景：直接用 Core 自己的健康检查。
  const core = await probeJson(`${CORE_URL}/api/v1/health`);
  return Boolean(core && core.status === 200 && core.data?.name === "tinadec-core");
}

async function waitForBackend() {
  if (!Number.isFinite(BACKEND_WAIT_MS) || BACKEND_WAIT_MS <= 0) {
    console.log("[dev] Backend readiness wait skipped (TINADEC_DEV_BACKEND_WAIT_MS<=0).");
    return;
  }

  if (await backendIsReady()) {
    console.log("[dev] Backend is already ready.");
    return;
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
      return;
    }
    if (waited % 15 === 0) console.log(`[dev]   still waiting for the backend (${waited}s)...`);
  }

  console.warn(
    `[dev] Backend not ready after ${budgetSeconds}s — starting Electron anyway; ` +
      "the app will show its own backend state (Gateway 会返回 503 core_status=unreachable).",
  );
}

async function main() {
  try {
    await Promise.all([waitForVite(), waitForBackend()]);
  } catch (err) {
    console.error(err.message);
    viteProcess.kill();
    process.exit(1);
  }

  console.log("[dev] Vite is ready, starting Electron...");

  const electronProcess = isWindows
    ? spawn("npx electron . --remote-debugging-port=9222", [], createSpawnOpts({ VITE_DEV_SERVER_URL: "http://127.0.0.1:5173" }))
    : spawn("npx", ["electron", ".", "--remote-debugging-port=9222"], createSpawnOpts({ VITE_DEV_SERVER_URL: "http://127.0.0.1:5173" }));

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
