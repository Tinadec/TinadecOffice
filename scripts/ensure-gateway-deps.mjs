#!/usr/bin/env node
/**
 * `npm run dev` 的前置步骤：确保 TinadecGateway 的依赖已安装。
 *
 * 为什么需要：TinadecGateway 不是 npm workspace（root 的 workspaces 只有 `apps/*`），
 * 所以 `npm install` 不会安装它的依赖，`check-dependencies.mjs` 也看不到它——后者只比对
 * package-lock.json 里 root 与 apps/* 的直接依赖，而 Gateway 根本不在那份 lock 里。
 * 结果：全新克隆按文档走 `npm install && npm run dev` 时，`dev:gateway`
 * （`cd TinadecGateway && bun run dev`）会因为缺 elysia 崩溃，报错是 bun 的模块解析信息，
 * 看起来像代码坏了。
 *
 * 行为：node_modules 缺失、或它记录的 bun.lock 摘要与当前不一致时运行 `bun install`；
 * 已经是最新状态就立刻返回（离线安全）。摘要写在 node_modules 里，所以
 * `rm -rf TinadecGateway/node_modules` 之后会重新安装。
 * 跳过：`TINADEC_DEV_GATEWAY_DEPS=off`。
 */
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const gatewayDir = resolve(scriptDir, "..", "TinadecGateway");

export function lockDigest(contents) {
  return createHash("sha256").update(contents).digest("hex");
}

const realFs = {
  exists: (path) => existsSync(path),
  read: (path) => readFileSync(path, "utf8"),
};

/// True when node_modules exists and was installed from the lockfile whose digest this is.
export function gatewayDependenciesCurrent({ modulesDir, stampFile, digest, fs = realFs }) {
  if (!fs.exists(modulesDir) || !fs.exists(stampFile)) return false;
  try {
    return fs.read(stampFile).trim() === digest;
  } catch {
    return false; // an unreadable stamp is a mismatch, not a reason to trust the tree
  }
}

function main() {
  if ((process.env.TINADEC_DEV_GATEWAY_DEPS ?? "").trim().toLowerCase() === "off") {
    console.log("[gateway-deps] skipped (TINADEC_DEV_GATEWAY_DEPS=off)");
    return;
  }

  const lockFile = ["bun.lock", "bun.lockb"]
    .map((name) => join(gatewayDir, name))
    .find((path) => existsSync(path));
  if (!lockFile) {
    console.log("[gateway-deps] no TinadecGateway lockfile found; leaving dependencies untouched.");
    return;
  }

  const modulesDir = join(gatewayDir, "node_modules");
  const stampFile = join(modulesDir, ".tinadec-bun-lock");
  if (gatewayDependenciesCurrent({ modulesDir, stampFile, digest: lockDigest(readFileSync(lockFile)) })) {
    return;
  }

  console.log("[gateway-deps] installing TinadecGateway dependencies (bun install)...");
  const result = spawnSync("bun", ["install"], {
    cwd: gatewayDir,
    stdio: "inherit",
    shell: process.platform === "win32",
  });
  if (result.error || result.status !== 0) {
    console.error("[gateway-deps] TinadecGateway dependencies are not installed and `bun install` did not succeed.");
    console.error("[gateway-deps] `npm run dev` will fail at the gateway; install Bun, then run `cd TinadecGateway && bun install`.");
    if (result.error) console.error(`[gateway-deps] ${result.error.message}`);
    process.exitCode = 1;
    return;
  }

  // Stamp the lockfile as installed *after* the install: bun may have rewritten it, and the
  // stamp has to describe the tree that now exists.
  mkdirSync(modulesDir, { recursive: true });
  writeFileSync(stampFile, `${lockDigest(readFileSync(lockFile))}\n`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
