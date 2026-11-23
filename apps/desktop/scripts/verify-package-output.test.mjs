import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";

import {
	resolveVerifyOptions,
	verifyAsar,
	verifyInstallableArtifacts,
	verifyPackageOutput,
} from "./verify-package-output.mjs";

const require = createRequire(import.meta.url);
const { createPackage, uncacheAll } = require("@electron/asar");

function amd64PeStub() {
	const buf = Buffer.alloc(0x100);
	buf.writeUInt16LE(0x5a4d, 0); // MZ
	buf.writeUInt32LE(0x80, 0x3c); // e_lfanew
	buf.writeUInt32LE(0x00004550, 0x80); // PE\0\0
	buf.writeUInt16LE(0x8664, 0x84); // IMAGE_FILE_MACHINE_AMD64
	return buf;
}

function writeNonEmpty(filePath, content = "placeholder") {
	mkdirSync(dirname(filePath), { recursive: true });
	writeFileSync(filePath, content);
}

async function createMockAsar(resourcesDir, version) {
	const srcDir = mkdtempSync(join(tmpdir(), "tinadec-asar-src-"));
	try {
		writeNonEmpty(
			join(srcDir, "package.json"),
			JSON.stringify({ name: "@tinadec/desktop", version, main: "electron/main.cjs" }),
		);
		writeNonEmpty(join(srcDir, "dist", "index.html"), "<!doctype html>");
		writeNonEmpty(join(srcDir, "electron", "main.cjs"), "");
		writeNonEmpty(join(srcDir, "electron", "preload.cjs"), "");
		writeNonEmpty(join(srcDir, "electron", "serviceManager.cjs"), "");
		mkdirSync(resourcesDir, { recursive: true });
		await createPackage(srcDir, join(resourcesDir, "app.asar"));
	} finally {
		rmSync(srcDir, { recursive: true, force: true });
	}
}

async function createMockReleaseTree({ setupLabel, portableLabel = setupLabel, asarVersion }) {
	const releaseDir = mkdtempSync(join(tmpdir(), "tinadec-verify-pkg-"));
	const pe = amd64PeStub();

	writeNonEmpty(join(releaseDir, `TinadecOffice-${setupLabel}-win-x64-setup.exe`), pe);
	writeNonEmpty(join(releaseDir, `TinadecOffice-${portableLabel}-win-x64-portable.exe`), pe);

	const unpacked = join(releaseDir, "win-unpacked");
	const resources = join(unpacked, "resources");
	writeNonEmpty(join(unpacked, "TinadecOffice.exe"), pe);
	writeNonEmpty(join(unpacked, "resources.pak"), "pak");
	writeNonEmpty(join(unpacked, "icudtl.dat"), "icu");
	writeNonEmpty(join(unpacked, "locales", "en-US.pak"), "en-US");

	await createMockAsar(resources, asarVersion);

	const nodePtyRoot = join(resources, "app.asar.unpacked", "node_modules", "node-pty");
	writeNonEmpty(join(nodePtyRoot, "package.json"), '{"name":"node-pty"}');
	const prebuilds = join(nodePtyRoot, "prebuilds", "win32-x64");
	for (const file of [
		"pty.node",
		"conpty.node",
		"conpty_console_list.node",
		"winpty-agent.exe",
		"winpty.dll",
		join("conpty", "OpenConsole.exe"),
		join("conpty", "conpty.dll"),
	]) {
		writeNonEmpty(join(prebuilds, file), pe);
	}

	const runtime = join(resources, "runtime");
	writeNonEmpty(join(runtime, "core", "TinadecCore.Api.exe"), pe);
	writeNonEmpty(join(runtime, "core", "appsettings.json"), "{}");
	writeNonEmpty(join(runtime, "core", "Configuration", "default-agent-runtime.toml"), "[runtime]\n");
	writeNonEmpty(join(runtime, "gateway", "TinadecGateway.exe"), pe);
	writeNonEmpty(join(runtime, "tools", "TinadecTools.exe"), pe);
	writeNonEmpty(join(runtime, "tools", "Nlog.config"), "<nlog/>");
	writeNonEmpty(join(runtime, "tools", "rg.exe"), pe);
	writeNonEmpty(join(runtime, "native", "rg", "rg.exe"), pe);
	writeNonEmpty(join(runtime, "git", "cmd", "git.exe"), pe);
	writeNonEmpty(join(runtime, "git", "bin", "bash.exe"), pe);

	return { releaseDir, resources };
}

test("resolveVerifyOptions defaults cleanly for Stable, Nightly, and env overrides", () => {
	assert.deepEqual(resolveVerifyOptions({}, {}), {
		artifactLabel: "",
		expectedPackageVersion: "",
	});

	assert.deepEqual(
		resolveVerifyOptions(
			{},
			{
				OFFICE_ARTIFACT_LABEL: " nightly-723c7d5 ",
				OFFICE_RELEASE_VERSION: "v0.0.0-nightly.723c7d5",
			},
		),
		{
			artifactLabel: "nightly-723c7d5",
			expectedPackageVersion: "0.0.0-nightly.723c7d5",
		},
	);

	// Explicit options override environment variables.
	assert.deepEqual(
		resolveVerifyOptions(
			{ artifactLabel: "main-abc1234", expectedPackageVersion: "0.0.0-main.abc1234" },
			{ OFFICE_ARTIFACT_LABEL: "ignored", OFFICE_RELEASE_VERSION: "v9.9.9" },
		),
		{
			artifactLabel: "main-abc1234",
			expectedPackageVersion: "0.0.0-main.abc1234",
		},
	);

	// When artifactLabel is set without OFFICE_RELEASE_VERSION, falls back to apps/desktop/package.json version.
	const withLabelOnly = resolveVerifyOptions({}, { OFFICE_ARTIFACT_LABEL: "nightly-723c7d5" });
	assert.equal(withLabelOnly.artifactLabel, "nightly-723c7d5");
	assert.match(withLabelOnly.expectedPackageVersion, /^\d+\.\d+\.\d+/);
});

test("verifyInstallableArtifacts and verifyAsar accept Stable artifacts with matching version", async () => {
	const dir = mkdtempSync(join(tmpdir(), "tinadec-stable-artifacts-"));
	try {
		writeNonEmpty(join(dir, "TinadecOffice-0.5.0-win-x64-setup.exe"), "setup");
		writeNonEmpty(join(dir, "TinadecOffice-0.5.0-win-x64-portable.exe"), "portable");
		const resources = join(dir, "resources");
		await createMockAsar(resources, "0.5.0");

		const artifacts = verifyInstallableArtifacts(dir);
		assert.equal(artifacts.expectedLabel, "0.5.0");
		assert.equal(artifacts.expectedVersion, "0.5.0");
		const asar = verifyAsar(resources, artifacts.expectedLabel);
		assert.ok(asar.entries >= 5);
	} finally {
		uncacheAll();
		rmSync(dir, { recursive: true, force: true });
	}
});

test("verifyInstallableArtifacts and verifyAsar accept Nightly artifactLabel distinct from semver package version", async () => {
	const dir = mkdtempSync(join(tmpdir(), "tinadec-nightly-artifacts-"));
	try {
		writeNonEmpty(join(dir, "TinadecOffice-nightly-723c7d5-win-x64-setup.exe"), "setup");
		writeNonEmpty(join(dir, "TinadecOffice-nightly-723c7d5-win-x64-portable.exe"), "portable");
		const resources = join(dir, "resources");
		await createMockAsar(resources, "0.0.0-nightly.723c7d5");

		const artifacts = verifyInstallableArtifacts(dir, { artifactLabel: "nightly-723c7d5" });
		assert.equal(artifacts.expectedLabel, "nightly-723c7d5");
		const asar = verifyAsar(resources, "0.0.0-nightly.723c7d5");
		assert.ok(asar.entries >= 5);

		assert.throws(
			() => verifyAsar(resources, "0.5.0"),
			/Unexpected app\.asar version 0\.0\.0-nightly\.723c7d5; expected 0\.5\.0/,
		);
	} finally {
		uncacheAll();
		rmSync(dir, { recursive: true, force: true });
	}
});

test("verifyInstallableArtifacts rejects mismatched setup/portable labels and mismatched expected artifactLabel", () => {
	const dir = mkdtempSync(join(tmpdir(), "tinadec-mismatch-artifacts-"));
	try {
		writeNonEmpty(join(dir, "TinadecOffice-nightly-723c7d5-win-x64-setup.exe"), "setup");
		writeNonEmpty(join(dir, "TinadecOffice-0.5.0-win-x64-portable.exe"), "portable");

		assert.throws(
			() => verifyInstallableArtifacts(dir),
			/Windows installer label 'nightly-723c7d5' does not match portable package label '0\.5\.0'/,
		);
	} finally {
		rmSync(dir, { recursive: true, force: true });
	}

	const dir2 = mkdtempSync(join(tmpdir(), "tinadec-wrong-label-"));
	try {
		writeNonEmpty(join(dir2, "TinadecOffice-0.5.0-win-x64-setup.exe"), "setup");
		writeNonEmpty(join(dir2, "TinadecOffice-0.5.0-win-x64-portable.exe"), "portable");

		assert.throws(
			() => verifyInstallableArtifacts(dir2, { artifactLabel: "nightly-723c7d5" }),
			/Windows artifact label '0\.5\.0' does not match expected artifact label 'nightly-723c7d5'/,
		);
	} finally {
		rmSync(dir2, { recursive: true, force: true });
	}
});

test(
	"verifyPackageOutput end-to-end validates Stable, Nightly, and rejects mismatched labels on Windows x64",
	{ skip: process.platform !== "win32" || process.arch !== "x64" },
	async () => {
		const stable = await createMockReleaseTree({ setupLabel: "0.5.0", asarVersion: "0.5.0" });
		const nightly = await createMockReleaseTree({
			setupLabel: "nightly-723c7d5",
			asarVersion: "0.0.0-nightly.723c7d5",
		});
		const prevLabel = process.env.OFFICE_ARTIFACT_LABEL;
		const prevVer = process.env.OFFICE_RELEASE_VERSION;
		try {
			delete process.env.OFFICE_ARTIFACT_LABEL;
			delete process.env.OFFICE_RELEASE_VERSION;

			const stableResult = verifyPackageOutput(stable.releaseDir);
			assert.equal(stableResult.expectedLabel, "0.5.0");
			assert.equal(stableResult.expectedPackageVersion, "0.5.0");

			const nightlyResult = verifyPackageOutput(nightly.releaseDir, {
				artifactLabel: "nightly-723c7d5",
				expectedPackageVersion: "0.0.0-nightly.723c7d5",
			});
			assert.equal(nightlyResult.expectedLabel, "nightly-723c7d5");
			assert.equal(nightlyResult.expectedPackageVersion, "0.0.0-nightly.723c7d5");

			assert.throws(
				() =>
					verifyPackageOutput(nightly.releaseDir, {
						artifactLabel: "main-723c7d5",
						expectedPackageVersion: "0.0.0-nightly.723c7d5",
					}),
				/does not match expected artifact label 'main-723c7d5'/,
			);
		} finally {
			if (prevLabel === undefined) delete process.env.OFFICE_ARTIFACT_LABEL;
			else process.env.OFFICE_ARTIFACT_LABEL = prevLabel;
			if (prevVer === undefined) delete process.env.OFFICE_RELEASE_VERSION;
			else process.env.OFFICE_RELEASE_VERSION = prevVer;
			uncacheAll();
			rmSync(stable.releaseDir, { recursive: true, force: true });
			rmSync(nightly.releaseDir, { recursive: true, force: true });
		}
	},
);
