import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

import { RUNTIME_TARGETS, runtimeTargetKeys } from "./runtimeTargets.mjs";
import { builderArgs } from "./package.mjs";

const desktopDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const pkg = JSON.parse(readFileSync(join(desktopDir, "package.json"), "utf8"));
const build = pkg.build;
const script = (name) => pkg.scripts[name] ?? "";

// node-pty picks its binary from prebuilds/<platform>-<arch> at require() time, and the directory
// names are its own vocabulary, not the runtime matrix's.
const PREBUILD_DIR = {
	"win-x64": "win32-x64",
	"linux-x64": "linux-x64",
	"osx-arm64": "darwin-arm64",
};

test("Every shipping target has an electron-builder mapping and nothing else does", () => {
	const archFlag = { x64: "--x64", arm64: "--arm64" };
	for (const key of runtimeTargetKeys()) {
		const args = builderArgs(key);
		assert.ok(["--win", "--linux", "--mac"].includes(args[0]), `${key} needs a builder platform flag`);
		assert.equal(args[1], archFlag[RUNTIME_TARGETS[key].arch], `${key} builds for its own arch`);
		assert.deepEqual(args.slice(2), ["--publish", "never"], `${key} must not try to publish from CI build`);
	}
	// osx-x64 is deliberately out of the matrix; a mapping for it would stage a runtime nobody runs.
	assert.throws(() => builderArgs("osx-x64"), /No electron-builder mapping/);
});

test("macOS ships a dmg sealed by the ad-hoc hook and Linux a .deb, each named after its platform", () => {
	assert.deepEqual(build.mac.target, [{ target: "dmg", arch: ["arm64"] }]);
	assert.deepEqual(build.linux.target, [{ target: "deb", arch: ["x64"] }]);

	// One release collects every asset in one directory: two platforms sharing an artifact name is
	// how a Windows installer ended up labelled as the Linux one.
	assert.match(build.mac.artifactName, /osx-arm64/);
	assert.match(build.linux.artifactName, /linux-x64/);
	assert.match(build.nsis.artifactName, /win-x64/);
	// No Developer ID yet, so electron-builder must not look for one...
	assert.equal(build.mac.identity, null, "no Apple Developer identity yet");
	// ...but the bundle still needs a complete seal: without it macOS calls a quarantined download
	// "damaged" instead of "unidentified developer", which is a dead end for the user.
	assert.equal(build.afterPack, "scripts/sign-mac-adhoc.mjs", "the ad-hoc seal hook is what makes the dmg launchable");
	assert.ok(existsSync(join(desktopDir, build.afterPack)), "the afterPack hook file exists");
});

test("The deb has the metadata the packager refuses to build without", () => {
	// app-builder-lib's FpmTarget.computeFpmMetaInfoOptions throws on a missing homepage or
	// maintainer e-mail — and it only runs on the Linux leg, so without this the config error
	// surfaces as a red packaging step rather than as a name and an address to fill in.
	assert.ok(pkg.homepage, "package.json homepage is the deb's URL field");
	assert.match(build.linux.maintainer, /^.+ <[^@ ]+@[^@ ]+>$/, "deb maintainer needs an e-mail");
});

test("The node-pty filter ships each host its own prebuild", () => {
	const mapping = build.files.find((entry) => typeof entry === "object" && entry.from?.endsWith("node-pty"));
	assert.ok(mapping, "node-pty is mapped in from the hoisted root install");

	const exclusions = mapping.filter.filter((pattern) => pattern.startsWith("!"));
	for (const [key, dir] of Object.entries(PREBUILD_DIR)) {
		assert.equal(
			exclusions.some((pattern) => pattern.includes(dir)),
			false,
			`${key} would package without ${dir}, and the terminal dies at spawn`,
		);
	}
	// The one exclusion is the arch nobody targets; asserting it stays keeps the list from silently
	// growing back to the Windows-only shape it had before.
	assert.deepEqual(exclusions, ["!prebuilds/win32-arm64/**"]);

	// On Linux the npm package ships no prebuilds/linux-x64 at all: `rebuild:native` writes
	// build/Release/pty.node. A filter that copies only package.json, lib and prebuilds therefore
	// ships a terminal that cannot spawn — the ubuntu runner proved it with
	// "Failed to load native module: pty.node, checked: build/Release, build/Debug, prebuilds/linux-x64".
	assert.ok(mapping.filter.includes("build/**/*"), "the rebuilt node-pty binary has to be packaged");
	assert.ok(
		build.asarUnpack.some((pattern) => pattern.includes("node-pty")),
		"a .node inside the asar is not loadable",
	);
});

test("Packaging scripts go through the one parameterized entry", () => {
	for (const name of ["package:win", "package:portable", "package:linux", "package:mac"]) {
		assert.ok(script(name), `${name} exists`);
		assert.match(script(name), /node scripts\/package\.mjs/, `${name} must not grow its own builder call`);
		assert.doesNotMatch(script(name), /package-win\.mjs/);
	}
	assert.equal(existsSync(join(desktopDir, "scripts", "package-win.mjs")), false);
});

test("Linux and macOS staging is what the runtime table knows, not a Windows default", () => {
	// package:linux/mac run stage:runtime on their own runner, and the stager picks the target from
	// the host. If a script ever pins --target by hand, the guard here is the mismatch it would hide.
	for (const name of ["package:win", "package:portable", "package:linux", "package:mac"]) {
		assert.doesNotMatch(script(name), /--target=/, `${name} lets the stager resolve its own host`);
	}
	assert.ok(runtimeTargetKeys().every((key) => key in RUNTIME_TARGETS));
});

function pngSize(path) {
	const bytes = readFileSync(path);
	assert.equal(bytes.subarray(0, 8).toString("hex"), "89504e470d0a1a0a", `${path} is not a PNG`);
	return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

function icoLargest(path) {
	const bytes = readFileSync(path);
	// A zero byte in the header means 256, which is exactly the size electron-builder requires.
	return Math.max(
		...Array.from({ length: bytes.readUInt16LE(4) }, (_, i) =>
			Math.max(bytes[6 + i * 16] || 256, bytes[7 + i * 16] || 256)),
	);
}

test("The channel's installer names are the packager's own artifact names", () => {
	// package-office-channel.mjs publishes the installer it finds by suffix. If that suffix and
	// electron-builder's artifactName ever drift apart, the leg fails with "Expected one installer,
	// found 0" — which is a red build rather than a release that is missing an asset.
	const extension = { nsis: "exe", portable: "exe", linux: "deb", mac: "dmg" };
	for (const key of runtimeTargetKeys()) {
		const assets = RUNTIME_TARGETS[key].installerAssets;
		assert.ok(assets.length > 0, `${key} ships no installer the channel knows about`);
		const expected = assets.map((asset) => {
			const template = build[asset.buildKey]?.artifactName;
			assert.ok(template, `${key} names build.${asset.buildKey}, which the config does not have`);
			return template.replace("TinadecOffice-${version}", "").replace("${ext}", extension[asset.buildKey]);
		});
		assert.deepEqual(
			assets.map((asset) => asset.suffix),
			expected,
			`${key} installer suffixes must equal the packaged artifact names`,
		);
		// Two legs landing the same file name is how a Windows installer once got labelled as Linux.
		for (const asset of assets) {
			assert.ok(asset.suffix.includes(key), `${asset.suffix} must name the ${key} it belongs to`);
		}
	}
});

test("Every target names an icon asset large enough for its builder format", () => {
	// The failure this pins is silent: when electron-builder resolves no icon it logs one warning
	// ("application icon is not set") and ships the default Electron logo. Every leg still goes
	// green, and the installers reach a user wearing somebody else's brand.
	const icons = { "win-x64": build.win.icon, "linux-x64": build.linux.icon, "osx-arm64": build.mac.icon };

	for (const [key, relative] of Object.entries(icons)) {
		assert.ok(relative, `${key} has no icon configured — the build would ship the Electron logo`);
		const path = join(desktopDir, relative);
		assert.ok(existsSync(path), `${key} icon '${relative}' is not committed`);

		if (relative.endsWith(".ico")) {
			// app-builder-lib throws ERR_ICON_TOO_SMALL below 256, and public/tinadec.ico only holds
			// a 128 frame — the two names differ by one word and only one of them builds.
			assert.ok(icoLargest(path) >= 256, `${key} ico tops out under the 256 the builder requires`);
		} else {
			const { width, height } = pngSize(path);
			assert.equal(width, height, `${key} icon must be square to fill an icon set`);
			assert.ok(width >= 512, `${key} icon is ${width}px; macOS icns conversion needs ≥512`);
		}
	}
});
