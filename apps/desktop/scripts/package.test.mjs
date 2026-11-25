import assert from "node:assert/strict";
import { test } from "node:test";

import { artifactLabelOverrides, builderArgs, compressionOverrides } from "./package.mjs";

const TARGETS = ["win-x64", "linux-x64", "osx-arm64"];

test("Stable builds omit artifactName overrides when artifactLabel is unset, blank, or matches packageVersion", () => {
	for (const target of TARGETS) {
		assert.deepEqual(artifactLabelOverrides(target), []);
		assert.deepEqual(artifactLabelOverrides(target, { artifactLabel: "", packageVersion: "0.5.0" }), []);
		assert.deepEqual(artifactLabelOverrides(target, { artifactLabel: "   ", packageVersion: "0.5.0" }), []);
		assert.deepEqual(artifactLabelOverrides(target, { artifactLabel: "0.5.0", packageVersion: "0.5.0" }), []);
		assert.deepEqual(artifactLabelOverrides(target, { artifactLabel: " 0.5.0 ", packageVersion: "0.5.0" }), []);
	}

	assert.deepEqual(builderArgs("win-x64", [], { artifactLabel: "", packageVersion: "0.5.0", compression: "" }), [
		"--win",
		"--x64",
		"--publish",
		"never",
	]);
	assert.deepEqual(builderArgs("win-x64", ["portable"], { artifactLabel: "0.5.0", packageVersion: "0.5.0", compression: "" }), [
		"--win",
		"--x64",
		"--publish",
		"never",
		"portable",
	]);
	assert.deepEqual(builderArgs("linux-x64", [], { artifactLabel: "0.5.0", packageVersion: "0.5.0", compression: "" }), [
		"--linux",
		"--x64",
		"--publish",
		"never",
	]);
	assert.deepEqual(builderArgs("osx-arm64", [], { artifactLabel: "0.5.0", packageVersion: "0.5.0", compression: "" }), [
		"--mac",
		"--arm64",
		"--publish",
		"never",
	]);
});

test("Nightly artifactLabel emits electron-builder overrides with literal ${ext} across all targets", () => {
	const opts = { artifactLabel: "nightly-723c7d5", packageVersion: "0.0.0-nightly.723c7d5", compression: "" };

	assert.deepEqual(artifactLabelOverrides("win-x64", opts), [
		"-c.nsis.artifactName=TinadecOffice-nightly-723c7d5-win-x64-setup.${ext}",
		"-c.portable.artifactName=TinadecOffice-nightly-723c7d5-win-x64-portable.${ext}",
	]);
	assert.deepEqual(artifactLabelOverrides("linux-x64", opts), [
		"-c.linux.artifactName=TinadecOffice-nightly-723c7d5-linux-x64.${ext}",
	]);
	assert.deepEqual(artifactLabelOverrides("osx-arm64", opts), [
		"-c.mac.artifactName=TinadecOffice-nightly-723c7d5-osx-arm64.${ext}",
		"-c.dmg.artifactName=TinadecOffice-nightly-723c7d5-osx-arm64.${ext}",
	]);

	assert.deepEqual(builderArgs("win-x64", ["--dir"], opts), [
		"--win",
		"--x64",
		"--publish",
		"never",
		"-c.nsis.artifactName=TinadecOffice-nightly-723c7d5-win-x64-setup.${ext}",
		"-c.portable.artifactName=TinadecOffice-nightly-723c7d5-win-x64-portable.${ext}",
		"--dir",
	]);
	assert.deepEqual(builderArgs("linux-x64", [], opts), [
		"--linux",
		"--x64",
		"--publish",
		"never",
		"-c.linux.artifactName=TinadecOffice-nightly-723c7d5-linux-x64.${ext}",
	]);
	assert.deepEqual(builderArgs("osx-arm64", [], opts), [
		"--mac",
		"--arm64",
		"--publish",
		"never",
		"-c.mac.artifactName=TinadecOffice-nightly-723c7d5-osx-arm64.${ext}",
		"-c.dmg.artifactName=TinadecOffice-nightly-723c7d5-osx-arm64.${ext}",
	]);
});

test("Main artifactLabel emits electron-builder overrides with literal ${ext} across all targets", () => {
	const opts = { artifactLabel: "main-723c7d5", packageVersion: "0.0.0-main.723c7d5" };

	assert.deepEqual(artifactLabelOverrides("win-x64", opts), [
		"-c.nsis.artifactName=TinadecOffice-main-723c7d5-win-x64-setup.${ext}",
		"-c.portable.artifactName=TinadecOffice-main-723c7d5-win-x64-portable.${ext}",
	]);
	assert.deepEqual(artifactLabelOverrides("linux-x64", opts), [
		"-c.linux.artifactName=TinadecOffice-main-723c7d5-linux-x64.${ext}",
	]);
	assert.deepEqual(artifactLabelOverrides("osx-arm64", opts), [
		"-c.mac.artifactName=TinadecOffice-main-723c7d5-osx-arm64.${ext}",
		"-c.dmg.artifactName=TinadecOffice-main-723c7d5-osx-arm64.${ext}",
	]);
});

test("Invalid artifact labels and unknown targets are rejected", () => {
	for (const bad of ["nightly/723c7d5", "nightly 723c7d5", "nightly-${ext}", "bad;rm", "foo\\bar"]) {
		assert.throws(
			() => artifactLabelOverrides("win-x64", { artifactLabel: bad, packageVersion: "0.5.0" }),
			/Invalid artifact label/,
			`expected '${bad}' to be rejected`,
		);
	}

	assert.throws(
		() => artifactLabelOverrides("linux-arm64", { artifactLabel: "nightly-723c7d5", packageVersion: "0.5.0" }),
		/No electron-builder mapping for 'linux-arm64'/,
	);
	assert.throws(() => builderArgs("linux-arm64"), /No electron-builder mapping for 'linux-arm64'/);
});

test("builderArgs reads OFFICE_ARTIFACT_LABEL from process.env by default", () => {
	const previousLabel = process.env.OFFICE_ARTIFACT_LABEL;
	const previousCompression = process.env.OFFICE_BUILDER_COMPRESSION;
	try {
		delete process.env.OFFICE_BUILDER_COMPRESSION;
		process.env.OFFICE_ARTIFACT_LABEL = "nightly-723c7d5";
		assert.deepEqual(builderArgs("linux-x64"), [
			"--linux",
			"--x64",
			"--publish",
			"never",
			"-c.linux.artifactName=TinadecOffice-nightly-723c7d5-linux-x64.${ext}",
		]);

		delete process.env.OFFICE_ARTIFACT_LABEL;
		assert.deepEqual(builderArgs("linux-x64"), ["--linux", "--x64", "--publish", "never"]);
	} finally {
		if (previousLabel === undefined) delete process.env.OFFICE_ARTIFACT_LABEL;
		else process.env.OFFICE_ARTIFACT_LABEL = previousLabel;
		if (previousCompression === undefined) delete process.env.OFFICE_BUILDER_COMPRESSION;
		else process.env.OFFICE_BUILDER_COMPRESSION = previousCompression;
	}
});

test("compressionOverrides and OFFICE_BUILDER_COMPRESSION support store, normal, maximum, and reject invalid modes", () => {
	assert.deepEqual(compressionOverrides({ compression: "" }), []);
	assert.deepEqual(compressionOverrides({ compression: "   " }), []);
	assert.deepEqual(compressionOverrides({ compression: undefined }), []);
	assert.deepEqual(compressionOverrides({ compression: "store" }), ["-c.compression=store"]);
	assert.deepEqual(compressionOverrides({ compression: " normal " }), ["-c.compression=normal"]);
	assert.deepEqual(compressionOverrides({ compression: "maximum" }), ["-c.compression=maximum"]);

	for (const mode of ["store", "normal", "maximum"]) {
		assert.deepEqual(
			builderArgs("win-x64", ["portable"], {
				artifactLabel: "main-723c7d5",
				packageVersion: "0.0.0-main.723c7d5",
				compression: mode,
			}),
			[
				"--win",
				"--x64",
				"--publish",
				"never",
				"-c.nsis.artifactName=TinadecOffice-main-723c7d5-win-x64-setup.${ext}",
				"-c.portable.artifactName=TinadecOffice-main-723c7d5-win-x64-portable.${ext}",
				`-c.compression=${mode}`,
				"portable",
			],
		);
	}

	for (const bad of ["fast", "gzip", "none", "STORE", "store;rm"]) {
		assert.throws(
			() => compressionOverrides({ compression: bad }),
			/Invalid builder compression/,
			`expected '${bad}' compression mode to be rejected`,
		);
		assert.throws(
			() => builderArgs("linux-x64", [], { artifactLabel: "", packageVersion: "0.5.0", compression: bad }),
			/Invalid builder compression/,
		);
	}

	const previousLabel = process.env.OFFICE_ARTIFACT_LABEL;
	const previousCompression = process.env.OFFICE_BUILDER_COMPRESSION;
	try {
		delete process.env.OFFICE_ARTIFACT_LABEL;
		process.env.OFFICE_BUILDER_COMPRESSION = "store";
		assert.deepEqual(compressionOverrides(), ["-c.compression=store"]);
		assert.deepEqual(builderArgs("osx-arm64"), [
			"--mac",
			"--arm64",
			"--publish",
			"never",
			"-c.compression=store",
		]);

		process.env.OFFICE_BUILDER_COMPRESSION = "";
		assert.deepEqual(compressionOverrides(), []);
		assert.deepEqual(builderArgs("osx-arm64"), ["--mac", "--arm64", "--publish", "never"]);

		process.env.OFFICE_BUILDER_COMPRESSION = "invalid-mode";
		assert.throws(() => builderArgs("osx-arm64"), /Invalid builder compression/);
	} finally {
		if (previousLabel === undefined) delete process.env.OFFICE_ARTIFACT_LABEL;
		else process.env.OFFICE_ARTIFACT_LABEL = previousLabel;
		if (previousCompression === undefined) delete process.env.OFFICE_BUILDER_COMPRESSION;
		else process.env.OFFICE_BUILDER_COMPRESSION = previousCompression;
	}
});
