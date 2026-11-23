// Stages the pinned ripgrep under native/rg/ for development. TinadecTools.csproj copies
// whatever rg/rg.exe exists there next to the tool binary, which is the second place
// RipgrepRunner.ResolveRgPath looks (after TINADEC_TOOLS_RG_PATH, before PATH) — so dev
// file_search runs the pinned rg instead of whichever rg another tool happened to put on
// PATH. Each machine stages its own platform target (see scripts/ripgrep-pin.mjs).
//
// Never fatal: offline or behind a proxy, dev still starts and only file_search degrades
// (the TinadecTools build prints how to fix it).
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { chmodSync, copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
	currentRipgrepTarget,
	ripgrepStagedName,
	ripgrepTarget,
	RIPGREP_VERSION,
} from "./ripgrep-pin.mjs";

const rootDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const stampFile = join(rootDir, "native", "rg", "VERSION");
const force = process.argv.includes("--force");

function sha256(buffer) {
	return createHash("sha256").update(buffer).digest("hex");
}

function extract(archivePath, destDir, isZip) {
	if (isZip) {
		if (process.platform === "win32") {
			// Prefer Windows System32 bsdtar to avoid legacy Windows PowerShell Expand-Archive /
			// 3-arg ExtractToDirectory issues on newer .NET / Windows runners.
			const systemTar = join(process.env.SystemRoot ?? "C:\\Windows", "System32", "tar.exe");
			if (existsSync(systemTar)) {
				const r = spawnSync(systemTar, ["-xf", archivePath, "-C", destDir], { stdio: "inherit" });
				if (r.error) throw r.error;
				if (r.status !== 0) throw new Error(`tar exited with code ${r.status}`);
				return;
			}
		}
		const quote = (value) => `'${value.replaceAll("'", "''")}'`;
		const r = spawnSync(
			"pwsh",
			["-NoProfile", "-NonInteractive", "-Command", `Expand-Archive -LiteralPath ${quote(archivePath)} -DestinationPath ${quote(destDir)} -Force`],
			{ stdio: "inherit" },
		);
		if (r.error) throw r.error;
		if (r.status !== 0) throw new Error(`Expand-Archive exited with code ${r.status}`);
		return;
	}
	const r = spawnSync("tar", ["-xzf", archivePath, "-C", destDir], { stdio: "inherit" });
	if (r.error) throw r.error;
	if (r.status !== 0) throw new Error(`tar exited with code ${r.status}`);
}

async function main() {
	const target = currentRipgrepTarget();
	if (target === null) {
		console.log(`[setup-ripgrep] no pinned ripgrep for ${process.platform}/${process.arch}; put rg on PATH or set TINADEC_TOOLS_RG_PATH.`);
		return;
	}
	const pin = ripgrepTarget(target);
	const stagedName = ripgrepStagedName(target);
	const targetPath = join(rootDir, "native", "rg", stagedName);

	const staged = existsSync(targetPath) && existsSync(stampFile)
		&& readFileSync(stampFile, "utf8").trim() === RIPGREP_VERSION;
	if (staged && !force) return;

	console.log(`[setup-ripgrep] staging ripgrep ${RIPGREP_VERSION} (${target}) at native/rg/${stagedName} ...`);
	const response = await fetch(pin.url, { signal: AbortSignal.timeout(120_000) });
	if (!response.ok) throw new Error(`HTTP ${response.status}`);
	const archive = Buffer.from(await response.arrayBuffer());
	const actual = sha256(archive);
	if (actual !== pin.sha256) {
		throw new Error(`checksum mismatch: expected ${pin.sha256}, got ${actual}`);
	}

	const work = join(rootDir, "native", "rg", ".extract");
	rmSync(work, { recursive: true, force: true });
	mkdirSync(work, { recursive: true });
	const archivePath = join(work, pin.asset);
	writeFileSync(archivePath, archive);
	extract(archivePath, work, pin.asset.endsWith(".zip"));
	copyFileSync(join(work, pin.asset.replace(/\.(zip|tar\.gz)$/, ""), pin.binary), targetPath);
	// tar preserves the executable bit, copyFileSync does not.
	if (stagedName !== "rg.exe") chmodSync(targetPath, 0o755);
	writeFileSync(stampFile, `${RIPGREP_VERSION}\n`);
	rmSync(work, { recursive: true, force: true });
	console.log("[setup-ripgrep] done.");
}

main().catch((error) => {
	console.warn(
		`[setup-ripgrep] could not stage ripgrep (${error instanceof Error ? error.message : String(error)}); `
		+ "file_search will fall back to TINADEC_TOOLS_RG_PATH or PATH. Retry with `npm run setup:rg`.",
	);
});
