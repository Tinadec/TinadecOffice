import assert from "node:assert/strict";
import { test } from "node:test";

import { expectedDebianVersionPrefixes, inspectDeb } from "./check-deb-package.mjs";

const RUNTIME_EXECUTABLES = [
	"core/TinadecCore.Api",
	"gateway/TinadecGateway",
	"tools/TinadecTools",
	"tools/bwrap",
	"tools/rg",
	"native/rg/rg",
];

const RUNTIME_RESOURCES = [
	"core/appsettings.json",
	"core/Configuration/default-agent-runtime.toml",
	"tools/Nlog.config",
];

function createLinuxX64ElfHead() {
	const head = Buffer.alloc(64);
	head.writeUInt32BE(0x7f454c46, 0);
	head[4] = 2; // 64-bit ELF
	head[5] = 1; // little-endian
	head.writeUInt16LE(62, 18); // EM_X86_64
	return head;
}

function createValidDebFixture(controlVersion) {
	const elfHead = createLinuxX64ElfHead();
	const runtimeFiles = {};
	for (const executable of RUNTIME_EXECUTABLES) {
		runtimeFiles[executable] = {
			size: 128,
			head: elfHead,
			permissions: "-rwxr-xr-x",
		};
	}
	for (const resource of RUNTIME_RESOURCES) {
		runtimeFiles[resource] = {
			size: 64,
			head: Buffer.alloc(0),
			permissions: "-rw-r--r--",
		};
	}

	const dataEntries = [
		"./opt/TinadecOffice/TinadecOffice",
		...[...RUNTIME_EXECUTABLES, ...RUNTIME_RESOURCES].map(
			(file) => `./opt/TinadecOffice/resources/runtime/${file}`,
		),
		"./usr/share/applications/TinadecOffice.desktop",
		"./usr/share/icons/hicolor/512x512/apps/TinadecOffice.png",
	];

	const control = [
		"Package: tinadec-office",
		`Version: ${controlVersion}`,
		"Maintainer: Tinadec <ops@tinadec.com>",
		"Architecture: amd64",
		"Depends: libcap2 (>= 1:2.25)",
		"Description: TinadecOffice desktop client",
	].join("\n");

	const desktopFile = [
		"[Desktop Entry]",
		"Name=TinadecOffice",
		"Exec=/opt/TinadecOffice/TinadecOffice %U",
		"Icon=TinadecOffice",
	].join("\n");

	return {
		control,
		desktopEntries: ["./postinst", "./postrm"],
		dataEntries,
		desktopFile,
		runtimeFiles,
	};
}

test("expectedDebianVersionPrefixes maps SemVer prerelease hyphens to Debian tilde versions", () => {
	assert.deepEqual(expectedDebianVersionPrefixes("0.5.0"), ["0.5.0"]);
	assert.deepEqual(expectedDebianVersionPrefixes("0.0.0-main.a98bcb9"), [
		"0.0.0-main.a98bcb9",
		"0.0.0~main.a98bcb9",
	]);
	assert.deepEqual(expectedDebianVersionPrefixes("0.0.0-nightly.a98bcb9"), [
		"0.0.0-nightly.a98bcb9",
		"0.0.0~nightly.a98bcb9",
	]);
	assert.deepEqual(expectedDebianVersionPrefixes("0.6.0-rc-1"), [
		"0.6.0-rc-1",
		"0.6.0~rc~1",
	]);
	assert.deepEqual(expectedDebianVersionPrefixes(""), []);
});

test("inspectDeb accepts stable and Debian tilde-normalized prerelease control versions", () => {
	assert.deepEqual(
		inspectDeb({
			...createValidDebFixture("0.5.0"),
			version: "0.5.0",
		}),
		[],
	);

	assert.deepEqual(
		inspectDeb({
			...createValidDebFixture("0.0.0~main.a98bcb9"),
			version: "0.0.0-main.a98bcb9",
		}),
		[],
	);

	assert.deepEqual(
		inspectDeb({
			...createValidDebFixture("0.0.0~nightly.a98bcb9"),
			version: "0.0.0-nightly.a98bcb9",
		}),
		[],
	);
});

test("inspectDeb rejects mismatched control versions", () => {
	assert.deepEqual(
		inspectDeb({
			...createValidDebFixture("0.4.1"),
			version: "0.5.0",
		}),
		["control Version '0.4.1' does not carry the app version '0.5.0'"],
	);
});
