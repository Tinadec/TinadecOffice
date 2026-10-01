// Stages the pinned ripgrep at native/rg/rg.exe for development. TinadecTools.csproj copies
// that file next to TinadecTools.exe, which is the second place RipgrepRunner.ResolveRgPath
// looks (after TINADEC_TOOLS_RG_PATH, before PATH) — so dev file_search runs the pinned rg
// instead of whichever rg another tool happened to put on PATH.
//
// Never fatal: offline or behind a proxy, dev still starts and only file_search degrades
// (the TinadecTools build prints how to fix it).
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { RIPGREP_ASSET, RIPGREP_SHA256, RIPGREP_URL, RIPGREP_VERSION } from "./ripgrep-pin.mjs";

const rootDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const target = join(rootDir, "native", "rg", "rg.exe");
const stampFile = join(rootDir, "native", "rg", "VERSION");
const force = process.argv.includes("--force");

function sha256(buffer) {
	return createHash("sha256").update(buffer).digest("hex");
}

async function main() {
	if (process.platform !== "win32" || process.arch !== "x64") {
		console.log("[setup-ripgrep] not Windows x64; put rg on PATH or set TINADEC_TOOLS_RG_PATH.");
		return;
	}
	const staged = existsSync(target) && existsSync(stampFile)
		&& readFileSync(stampFile, "utf8").trim() === RIPGREP_VERSION;
	if (staged && !force) return;

	console.log(`[setup-ripgrep] staging ripgrep ${RIPGREP_VERSION} at native/rg/ ...`);
	const response = await fetch(RIPGREP_URL, { signal: AbortSignal.timeout(120_000) });
	if (!response.ok) throw new Error(`HTTP ${response.status}`);
	const archive = Buffer.from(await response.arrayBuffer());
	const actual = sha256(archive);
	if (actual !== RIPGREP_SHA256) {
		throw new Error(`checksum mismatch: expected ${RIPGREP_SHA256}, got ${actual}`);
	}

	const work = join(rootDir, "native", "rg", ".extract");
	rmSync(work, { recursive: true, force: true });
	mkdirSync(work, { recursive: true });
	const zip = join(work, RIPGREP_ASSET);
	writeFileSync(zip, archive);
	const quote = (value) => `'${value.replaceAll("'", "''")}'`;
	const expand = spawnSync(
		"powershell.exe",
		["-NoProfile", "-NonInteractive", "-Command", `Expand-Archive -LiteralPath ${quote(zip)} -DestinationPath ${quote(work)} -Force`],
		{ stdio: "inherit" },
	);
	if (expand.status !== 0) throw new Error(`Expand-Archive exited with code ${expand.status}`);
	copyFileSync(join(work, RIPGREP_ASSET.replace(/\.zip$/, ""), "rg.exe"), target);
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
