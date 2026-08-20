import { spawn } from "child_process";
import { dirname, resolve } from "path";
import { fileURLToPath } from "url";
import http from "http";

const __dirname = dirname(fileURLToPath(import.meta.url));
const rootDir = resolve(__dirname, "..");

const isWindows = process.platform === "win32";

function createSpawnOpts(extraEnv = {}) {
  return {
    cwd: rootDir,
    shell: isWindows,
    stdio: "pipe",
    env: { ...process.env, ...extraEnv },
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

async function main() {
  try {
    await waitForVite();
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
