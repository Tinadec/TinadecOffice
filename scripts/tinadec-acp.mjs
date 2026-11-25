#!/usr/bin/env node
/**
 * Cross-platform CLI launcher for TinadecCore ACP v1 stdio Agent Server (`--acp`).
 *
 * Strict stdout purity contract:
 * - `stdout` (fd 1) is 100% reserved for BOM-free UTF-8 ACP NDJSON frames from TinadecCore.
 * - This launcher MUST NEVER write a single byte to `stdout` under any circumstance.
 * - Any fatal launch failure is written strictly to `process.stderr`, without ever logging
 *   CLI arguments or environment values (which may carry `--host-token` / secrets).
 */
import { spawn } from "node:child_process";
import { existsSync, realpathSync } from "node:fs";
import { constants as osConstants } from "node:os";
import { delimiter, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const TARGET_FRAMEWORK = "net11.0";

function resolveRepoRoot() {
	const scriptPath = realpathSync(fileURLToPath(import.meta.url));
	return resolve(dirname(scriptPath), "..");
}

function detectRuntimeIdentifier() {
	const { platform, arch } = process;
	if (platform === "win32") {
		if (arch === "x64") return "win-x64";
		if (arch === "arm64") return "win-arm64";
	} else if (platform === "darwin") {
		if (arch === "arm64") return "osx-arm64";
		if (arch === "x64") return "osx-x64";
	} else if (platform === "linux") {
		if (arch === "x64") return "linux-x64";
		if (arch === "arm64") return "linux-arm64";
	}
	return null;
}

function buildSanitizedEnv(baseEnv = process.env) {
	const env = {};
	for (const [key, value] of Object.entries(baseEnv)) {
		if (value === undefined) continue;
		// Mirror scripts/setup-dotnet-env.ps1 silently: `Version` and `Ice-Version`
		// corrupt child dotnet/MSBuild property evaluation on Windows hosts.
		const lower = key.toLowerCase();
		if (lower === "version" || lower === "ice-version") continue;
		env[key] = value;
	}
	// Guarantee dotnet SDK/host never emits welcome banners, telemetry notices, or Kestrel status lines to stdout.
	env.DOTNET_NOLOGO = "1";
	env.DOTNET_CLI_TELEMETRY_OPTOUT = "1";
	env.DOTNET_PRINT_TELEMETRY_MESSAGE = "false";
	env.ASPNETCORE_SUPPRESSSTATUSMESSAGES = "true";
	return env;
}

function resolveDotnetCommand(env) {
	const exe = process.platform === "win32" ? "dotnet.exe" : "dotnet";
	const dotnetRoot = env.DOTNET_ROOT || env.DOTNET_ROOT_X64;
	if (dotnetRoot) {
		const candidate = join(dotnetRoot, exe);
		if (existsSync(candidate)) return candidate;
	}
	const pathValue = env.PATH ?? env.Path ?? "";
	for (const entry of pathValue.split(delimiter)) {
		if (!entry) continue;
		const cleaned = entry.replace(/^"+|"+$/g, "");
		const candidate = join(cleaned, exe);
		if (existsSync(candidate)) return candidate;
	}
	return null;
}

function parseLauncherArgs(rawArgs, env) {
	let forceDotnetRun =
		env.TINADEC_ACP_DOTNET_RUN === "1" ||
		String(env.TINADEC_ACP_DOTNET_RUN ?? "").toLowerCase() === "true";

	const forwarded = [];
	for (const arg of rawArgs) {
		if (arg === "--dotnet-run") {
			forceDotnetRun = true;
			continue;
		}
		forwarded.push(arg);
	}

	if (!forwarded.includes("--acp")) {
		forwarded.unshift("--acp");
	}

	return { forceDotnetRun, forwardedArgs: forwarded };
}

function resolveLaunchPlan(repoRoot, forceDotnetRun, forwardedArgs, env) {
	const dotnetCmd = resolveDotnetCommand(env) ?? "dotnet";
	const dotnetOnPath = resolveDotnetCommand(env) !== null;
	const exeName = process.platform === "win32" ? "TinadecCore.Api.exe" : "TinadecCore.Api";
	const rid = detectRuntimeIdentifier();

	const explicitExecutable = env.TINADEC_ACP_EXECUTABLE?.trim();
	if (explicitExecutable) {
		const resolvedExplicit = resolve(explicitExecutable);
		if (!existsSync(resolvedExplicit)) {
			process.stderr.write(
				`[tinadec-acp] TINADEC_ACP_EXECUTABLE does not exist: ${resolvedExplicit}\n`,
			);
			process.exit(1);
		}
		if (resolvedExplicit.toLowerCase().endsWith(".dll")) {
			return { command: dotnetCmd, args: [resolvedExplicit, ...forwardedArgs] };
		}
		return { command: resolvedExplicit, args: forwardedArgs };
	}

	const apiDir = resolve(repoRoot, "TinadecCore", "Api");
	const apiCsproj = resolve(apiDir, "TinadecCore.Api.csproj");

	if (!forceDotnetRun) {
		const searchDirs = [
			resolve(apiDir, "bin", "Release", TARGET_FRAMEWORK),
			...(rid ? [resolve(apiDir, "bin", "Release", TARGET_FRAMEWORK, rid)] : []),
			resolve(repoRoot, "artifacts", "tinadec-core-api"),
			resolve(apiDir, "bin", "Debug", TARGET_FRAMEWORK),
			...(rid ? [resolve(apiDir, "bin", "Debug", TARGET_FRAMEWORK, rid)] : []),
		];

		for (const dir of searchDirs) {
			const dllPath = join(dir, "TinadecCore.Api.dll");
			const exePath = join(dir, exeName);

			if (dotnetOnPath && existsSync(dllPath)) {
				return { command: dotnetCmd, args: [dllPath, ...forwardedArgs] };
			}
			if (existsSync(exePath)) {
				return { command: exePath, args: forwardedArgs };
			}
			if (existsSync(dllPath)) {
				return { command: dotnetCmd, args: [dllPath, ...forwardedArgs] };
			}
		}
	}

	if (!existsSync(apiCsproj)) {
		process.stderr.write(
			`[tinadec-acp] no built TinadecCore.Api binary found and project file is missing at ${apiCsproj}\n`,
		);
		process.exit(1);
	}

	return {
		command: dotnetCmd,
		args: [
			"run",
			"--project",
			apiCsproj,
			"--no-launch-profile",
			"-v",
			"quiet",
			"--",
			...forwardedArgs,
		],
	};
}

const repoRoot = resolveRepoRoot();
const childEnv = buildSanitizedEnv(process.env);
const { forceDotnetRun, forwardedArgs } = parseLauncherArgs(process.argv.slice(2), childEnv);
const { command, args } = resolveLaunchPlan(repoRoot, forceDotnetRun, forwardedArgs, childEnv);

const child = spawn(command, args, {
	stdio: "inherit",
	env: childEnv,
	windowsHide: true,
});

let childExited = false;

function forwardSignal(signal) {
	if (childExited || !child.pid) return;
	try {
		child.kill(signal);
	} catch {
		// Child process has already terminated.
	}
}

process.on("SIGINT", () => forwardSignal("SIGINT"));
process.on("SIGTERM", () => forwardSignal("SIGTERM"));
if (process.platform !== "win32") {
	process.on("SIGHUP", () => forwardSignal("SIGHUP"));
}

child.on("error", (error) => {
	childExited = true;
	// Never include `args` or `childEnv` in error output: they may carry `--host-token` or secrets.
	process.stderr.write(`[tinadec-acp] failed to launch '${command}': ${error.message}\n`);
	process.exit(1);
});

child.on("exit", (code, signal) => {
	childExited = true;
	if (code !== null) {
		process.exit(code);
	}
	const signalCode = signal ? (osConstants.signals[signal] ?? 1) : 1;
	process.exit(128 + signalCode);
});
