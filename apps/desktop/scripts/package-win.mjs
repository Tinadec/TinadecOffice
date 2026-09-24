import { spawnSync } from "node:child_process";
import { mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const desktopDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const tempDir = join(desktopDir, ".runtime-cache", "tmp");
mkdirSync(tempDir, { recursive: true });
const command = process.platform === "win32" ? "npx.cmd" : "npx";
const result = spawnSync(
	command,
	[
		"electron-builder",
		"--win",
		"--x64",
		"--publish",
		"never",
		...process.argv.slice(2),
	],
	{
		cwd: desktopDir,
		env: {
			...process.env,
			CSC_IDENTITY_AUTO_DISCOVERY: "false",
			TEMP: tempDir,
			TMP: tempDir,
			TMPDIR: tempDir,
		},
		stdio: "inherit",
		windowsHide: true,
		shell: process.platform === "win32",
	},
);
if (result.error) throw result.error;
process.exit(result.status ?? 1);
