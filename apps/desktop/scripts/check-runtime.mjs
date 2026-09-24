import { spawnSync } from "node:child_process";
import { existsSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const desktopDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const runtimeDir = join(desktopDir, "runtime");
const core = join(runtimeDir, "core");
const gateway = join(runtimeDir, "gateway");
const tools = join(runtimeDir, "tools");
const native = join(runtimeDir, "native");
const git = join(runtimeDir, "git");

function requireFile(path, label) {
	if (!existsSync(path) || !statSync(path).isFile() || statSync(path).size === 0) {
		throw new Error(`${label} is missing or empty: ${path}`);
	}
	return path;
}

function requireDirectory(path, label) {
	if (!existsSync(path) || !statSync(path).isDirectory()) {
		throw new Error(`${label} is missing: ${path}`);
	}
	return path;
}

function readJson(path, label) {
	try {
		return JSON.parse(readFileSync(requireFile(path, label), "utf8"));
	} catch (error) {
		throw new Error(
			`${label} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`,
		);
	}
}

function peMachine(path) {
	const bytes = readFileSync(requireFile(path, "PE executable"));
	if (bytes.length < 0x40 || bytes.readUInt16LE(0) !== 0x5a4d) return 0;
	const peOffset = bytes.readUInt32LE(0x3c);
	if (peOffset + 6 > bytes.length || bytes.readUInt32LE(peOffset) !== 0x00004550) {
		return 0;
	}
	return bytes.readUInt16LE(peOffset + 4);
}

function requireAmd64(path, label) {
	const machine = peMachine(path);
	if (machine !== 0x8664) {
		throw new Error(`${label} is not Windows x64 PE: ${path}`);
	}
}

function runProbe(command, args, label, allowTimeout = false) {
	const result = spawnSync(command, args, {
		encoding: "utf8",
		input: "",
		timeout: 10_000,
		windowsHide: true,
	});
	if (result.error?.code === "ETIMEDOUT" && allowTimeout) {
		console.warn(`${label} did not exit within 10 seconds and was stopped.`);
		return "";
	}
	if (result.error) {
		throw new Error(`${label} could not run: ${result.error.message}`);
	}
	if (result.status !== 0) {
		throw new Error(`${label} exited with code ${result.status}: ${result.stderr.trim()}`);
	}
	return result.stdout.trim();
}

requireDirectory(runtimeDir, "Staged runtime");
requireDirectory(core, "Staged Core runtime");
requireDirectory(gateway, "Staged Gateway runtime");
requireDirectory(tools, "Staged Tools runtime");
requireDirectory(native, "Staged native runtime");
requireDirectory(git, "Staged Git runtime");

const coreExe = requireFile(join(core, "TinadecCore.Api.exe"), "Core executable");
const coreSettings = join(core, "appsettings.json");
const coreToml = join(core, "Configuration", "default-agent-runtime.toml");
const gatewayExe = requireFile(join(gateway, "TinadecGateway.exe"), "Gateway executable");
const toolsExe = requireFile(join(tools, "TinadecTools.exe"), "TinadecTools executable");
const toolsSettings = requireFile(join(tools, "Nlog.config"), "TinadecTools config");
const nativeRg = requireFile(join(native, "rg", "rg.exe"), "Native ripgrep executable");
const toolsRg = requireFile(join(tools, "rg.exe"), "Tools ripgrep executable");
const gitExe = requireFile(join(git, "cmd", "git.exe"), "PortableGit git executable");
const gitBash = requireFile(join(git, "bin", "bash.exe"), "PortableGit Bash executable");

for (const [path, label] of [
	[coreExe, "Core executable"],
	[gatewayExe, "Gateway executable"],
	[toolsExe, "TinadecTools executable"],
	[nativeRg, "Native ripgrep executable"],
	[toolsRg, "Tools ripgrep executable"],
	[gitExe, "PortableGit git executable"],
	[gitBash, "PortableGit Bash executable"],
]) {
	requireAmd64(path, label);
}

readJson(coreSettings, "Core appsettings");
requireFile(coreToml, "Core runtime TOML");
requireFile(toolsSettings, "TinadecTools config");

const toolsVersion = runProbe(toolsExe, ["--version"], "TinadecTools --version");
const gatewayVersion = runProbe(
	gatewayExe,
	["--version"],
	"Gateway --version",
	true,
);
console.log(`TinadecTools --version exited successfully${toolsVersion ? `: ${toolsVersion}` : ""}.`);
console.log(
	`Gateway --version ${gatewayVersion ? `returned: ${gatewayVersion}` : "is not a version-only command; executable launch was checked without keeping a service running"}.`,
);
console.log("Staged runtime path, resource, architecture, and short-entry checks passed.");
