import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import {
	findNativePtyBinary,
	resolvePackageSteps,
	verifyNativePtyBinary,
	verifyPrebuiltFrontend,
} from "./run-package.mjs";
import {
	PORTABLE_GIT_SHA256,
	hasExtractedPortableGit,
	portableGitCacheDir,
	resolveDotnetCacheEnv,
} from "./stage-runtime.mjs";

function withTempDir(prefix, fn) {
	const dir = mkdtempSync(join(tmpdir(), prefix));
	try {
		return fn(dir);
	} finally {
		rmSync(dir, { recursive: true, force: true });
	}
}

test("resolveDotnetCacheEnv prefers external NuGet/dotnet cache env vars and falls back when unset or blank", () => {
	const cacheRoot = join("/tmp", "runtime-cache");

	const defaults = resolveDotnetCacheEnv({}, cacheRoot);
	assert.equal(defaults.NUGET_PACKAGES, join(cacheRoot, "nuget"));
	assert.equal(defaults.DOTNET_CLI_HOME, join(cacheRoot, "dotnet"));
	assert.equal(defaults.NUGET_HTTP_CACHE_PATH, join(cacheRoot, "nuget-http"));
	assert.equal(defaults.DOTNET_NOLOGO, "1");
	assert.equal(defaults.DOTNET_CLI_TELEMETRY_OPTOUT, "1");
	assert.equal(defaults.DOTNET_SKIP_FIRST_TIME_EXPERIENCE, "1");

	const blankFallback = resolveDotnetCacheEnv(
		{
			NUGET_PACKAGES: "   ",
			DOTNET_CLI_HOME: "",
			NUGET_HTTP_CACHE_PATH: "\t\n",
			DOTNET_NOLOGO: "  ",
		},
		cacheRoot,
	);
	assert.equal(blankFallback.NUGET_PACKAGES, join(cacheRoot, "nuget"));
	assert.equal(blankFallback.DOTNET_CLI_HOME, join(cacheRoot, "dotnet"));
	assert.equal(blankFallback.NUGET_HTTP_CACHE_PATH, join(cacheRoot, "nuget-http"));
	assert.equal(blankFallback.DOTNET_NOLOGO, "1");

	const external = resolveDotnetCacheEnv(
		{
			NUGET_PACKAGES: " /home/runner/.nuget/packages ",
			DOTNET_CLI_HOME: "/home/runner/.dotnet",
			NUGET_HTTP_CACHE_PATH: "/home/runner/.local/share/NuGet/v3-cache",
			DOTNET_NOLOGO: "0",
			DOTNET_CLI_TELEMETRY_OPTOUT: "0",
			DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "false",
		},
		cacheRoot,
	);
	assert.equal(external.NUGET_PACKAGES, "/home/runner/.nuget/packages");
	assert.equal(external.DOTNET_CLI_HOME, "/home/runner/.dotnet");
	assert.equal(external.NUGET_HTTP_CACHE_PATH, "/home/runner/.local/share/NuGet/v3-cache");
	assert.equal(external.DOTNET_NOLOGO, "0");
	assert.equal(external.DOTNET_CLI_TELEMETRY_OPTOUT, "0");
	assert.equal(external.DOTNET_SKIP_FIRST_TIME_EXPERIENCE, "false");
});

test("portableGitCacheDir and hasExtractedPortableGit validate PortableGit cache state", () => {
	assert.match(PORTABLE_GIT_SHA256, /^[0-9a-f]{64}$/);
	const cacheRoot = join("/tmp", "runtime-cache");
	assert.equal(
		portableGitCacheDir(cacheRoot),
		join(cacheRoot, "portable-git", PORTABLE_GIT_SHA256),
	);

	withTempDir("tinadec-portable-git-", (dir) => {
		assert.equal(hasExtractedPortableGit(join(dir, "missing")), false);
		assert.equal(hasExtractedPortableGit(dir), false);

		const gitExe = join(dir, "cmd", "git.exe");
		const bashExe = join(dir, "bin", "bash.exe");
		mkdirSync(join(dir, "cmd"), { recursive: true });
		mkdirSync(join(dir, "bin"), { recursive: true });

		writeFileSync(gitExe, "MZ-git");
		assert.equal(hasExtractedPortableGit(dir), false);

		writeFileSync(bashExe, "");
		assert.equal(hasExtractedPortableGit(dir), false);

		writeFileSync(bashExe, "MZ-bash");
		assert.equal(hasExtractedPortableGit(dir), true);
	});
});

test("findNativePtyBinary resolves both local desktop and hoisted repo-root pty.node paths", () => {
	withTempDir("tinadec-pty-", (repoRoot) => {
		const desktopDir = join(repoRoot, "apps", "desktop");
		mkdirSync(desktopDir, { recursive: true });

		assert.equal(findNativePtyBinary(desktopDir, repoRoot), null);
		assert.throws(
			() => verifyNativePtyBinary(desktopDir, repoRoot),
			/OFFICE_SKIP_NATIVE_REBUILD=1/,
		);

		const hoistedPty = join(repoRoot, "node_modules", "node-pty", "build", "Release", "pty.node");
		mkdirSync(join(repoRoot, "node_modules", "node-pty", "build", "Release"), { recursive: true });
		writeFileSync(hoistedPty, "");
		assert.equal(findNativePtyBinary(desktopDir, repoRoot), null);

		writeFileSync(hoistedPty, "node-pty-binary");
		assert.equal(findNativePtyBinary(desktopDir, repoRoot), hoistedPty);
		assert.equal(verifyNativePtyBinary(desktopDir, repoRoot), hoistedPty);

		const localPty = join(desktopDir, "node_modules", "node-pty", "build", "Release", "pty.node");
		mkdirSync(join(desktopDir, "node_modules", "node-pty", "build", "Release"), { recursive: true });
		writeFileSync(localPty, "");
		assert.equal(findNativePtyBinary(desktopDir, repoRoot), hoistedPty);

		writeFileSync(localPty, "local-node-pty-binary");
		assert.equal(findNativePtyBinary(desktopDir, repoRoot), localPty);
	});
});

test("verifyPrebuiltFrontend requires a non-empty dist/index.html", () => {
	withTempDir("tinadec-frontend-", (desktopDir) => {
		assert.throws(() => verifyPrebuiltFrontend(desktopDir), /OFFICE_SKIP_FRONTEND_BUILD=1/);

		mkdirSync(join(desktopDir, "dist"), { recursive: true });
		const indexHtml = join(desktopDir, "dist", "index.html");
		writeFileSync(indexHtml, "");
		assert.throws(() => verifyPrebuiltFrontend(desktopDir), /OFFICE_SKIP_FRONTEND_BUILD=1/);

		writeFileSync(indexHtml, "<!doctype html><html></html>");
		assert.equal(verifyPrebuiltFrontend(desktopDir), indexHtml);
	});
});

test("resolvePackageSteps respects OFFICE_SKIP_NATIVE_REBUILD and OFFICE_SKIP_FRONTEND_BUILD", () => {
	assert.deepEqual(resolvePackageSteps({}, []), [
		{ command: "bun", args: ["run", "rebuild:native"] },
		{ command: "bun", args: ["run", "build"] },
		{ command: "bun", args: ["run", "stage:runtime"] },
		{ command: "bun", args: ["run", "check:runtime"] },
		{ command: "bun", args: ["scripts/package.mjs"] },
	]);

	assert.deepEqual(
		resolvePackageSteps(
			{ OFFICE_SKIP_NATIVE_REBUILD: "0", OFFICE_SKIP_FRONTEND_BUILD: "0" },
			["portable"],
		),
		[
			{ command: "bun", args: ["run", "rebuild:native"] },
			{ command: "bun", args: ["run", "build"] },
			{ command: "bun", args: ["run", "stage:runtime"] },
			{ command: "bun", args: ["run", "check:runtime"] },
			{ command: "bun", args: ["scripts/package.mjs", "portable"] },
		],
	);

	assert.deepEqual(resolvePackageSteps({ OFFICE_SKIP_NATIVE_REBUILD: "1" }, []), [
		{ command: "bun", args: ["run", "build"] },
		{ command: "bun", args: ["run", "stage:runtime"] },
		{ command: "bun", args: ["run", "check:runtime"] },
		{ command: "bun", args: ["scripts/package.mjs"] },
	]);

	assert.deepEqual(resolvePackageSteps({ OFFICE_SKIP_FRONTEND_BUILD: "1" }, []), [
		{ command: "bun", args: ["run", "rebuild:native"] },
		{ command: "bun", args: ["run", "stage:runtime"] },
		{ command: "bun", args: ["run", "check:runtime"] },
		{ command: "bun", args: ["scripts/package.mjs"] },
	]);

	assert.deepEqual(
		resolvePackageSteps(
			{ OFFICE_SKIP_NATIVE_REBUILD: "1", OFFICE_SKIP_FRONTEND_BUILD: "1" },
			["portable"],
		),
		[
			{ command: "bun", args: ["run", "stage:runtime"] },
			{ command: "bun", args: ["run", "check:runtime"] },
			{ command: "bun", args: ["scripts/package.mjs", "portable"] },
		],
	);

	withTempDir("tinadec-steps-", (repoRoot) => {
		const desktopDir = join(repoRoot, "apps", "desktop");
		mkdirSync(desktopDir, { recursive: true });

		assert.throws(
			() =>
				resolvePackageSteps(
					{ OFFICE_SKIP_NATIVE_REBUILD: "1" },
					[],
					{ desktopDir, repoRoot },
				),
			/OFFICE_SKIP_NATIVE_REBUILD=1/,
		);
		assert.throws(
			() =>
				resolvePackageSteps(
					{ OFFICE_SKIP_FRONTEND_BUILD: "1" },
					[],
					{ desktopDir, repoRoot },
				),
			/OFFICE_SKIP_FRONTEND_BUILD=1/,
		);

		mkdirSync(join(repoRoot, "node_modules", "node-pty", "build", "Release"), { recursive: true });
		writeFileSync(
			join(repoRoot, "node_modules", "node-pty", "build", "Release", "pty.node"),
			"pty",
		);
		mkdirSync(join(desktopDir, "dist"), { recursive: true });
		writeFileSync(join(desktopDir, "dist", "index.html"), "<html></html>");

		assert.deepEqual(
			resolvePackageSteps(
				{ OFFICE_SKIP_NATIVE_REBUILD: "1", OFFICE_SKIP_FRONTEND_BUILD: "1" },
				[],
				{ desktopDir, repoRoot },
			),
			[
				{ command: "bun", args: ["run", "stage:runtime"] },
				{ command: "bun", args: ["run", "check:runtime"] },
				{ command: "bun", args: ["scripts/package.mjs"] },
			],
		);
	});
});
