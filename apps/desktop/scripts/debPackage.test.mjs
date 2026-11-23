import assert from "node:assert/strict";
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

import { inspectDeb, parseControlFields, parseDesktopKeys, readDeb } from "./check-deb-package.mjs";

const desktopDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const pkg = JSON.parse(readFileSync(join(desktopDir, "package.json"), "utf8"));

const CONTROL = [
	"Package: tinadecoffice",
	"Version: 0.1.1",
	"Architecture: amd64",
	"Maintainer: Lincube <lincube3@hotmail.com>",
	"Depends: libgtk-3-0, libnss3, libcap2",
	"Description: TinadecOffice desktop workbench",
	" An Electron shell over the Gateway and Core agent runtime.",
].join("\n");

const DESKTOP = [
	"[Desktop Entry]",
	"Name=TinadecOffice",
	"Exec=/opt/TinadecOffice/TinadecOffice %U",
	"Icon=tinadecoffice",
	"Type=Application",
	"StartupWMClass=TinadecOffice",
].join("\n");

const DATA = [
	"./opt/",
	"./opt/TinadecOffice/",
	"./opt/TinadecOffice/TinadecOffice",
	"./usr/share/applications/tinadecoffice.desktop",
	"./usr/share/icons/hicolor/512x512/apps/tinadecoffice.png",
];
const RUNTIME_BINARIES = ["core/TinadecCore.Api", "gateway/TinadecGateway", "tools/TinadecTools", "tools/bwrap", "tools/rg", "native/rg/rg"];
const RUNTIME_TEXT = ["core/appsettings.json", "core/Configuration/default-agent-runtime.toml", "tools/Nlog.config"];
for (const file of [...RUNTIME_BINARIES, ...RUNTIME_TEXT]) DATA.push(`./opt/TinadecOffice/resources/runtime/${file}`);
function elf() {
	const bytes = Buffer.alloc(64); bytes.writeUInt32BE(0x7f454c46); bytes[4] = 2; bytes[5] = 1; bytes.writeUInt16LE(62, 18); return bytes;
}
const RUNTIME_FILES = Object.fromEntries([...RUNTIME_BINARIES, ...RUNTIME_TEXT].map((file) => [file, {
	size: 64, head: RUNTIME_BINARIES.includes(file) ? elf() : Buffer.from("configuration"), permissions: "-rwxr-xr-x",
}]));

const CONTROL_MEMBERS = ["./control", "./postinst", "./prerm"];

function check(overrides = {}) {
	return inspectDeb({
		control: CONTROL,
		desktopFile: DESKTOP,
		dataEntries: DATA,
		desktopEntries: CONTROL_MEMBERS.filter((entry) => entry !== "./control"),
		version: "0.1.1",
		runtimeFiles: RUNTIME_FILES,
		...overrides,
	});
}

test("A deb whose launcher, binary and icon agree is accepted", () => {
	// This is also the case that fails if the .desktop file is read with the control parser: no
	// `Key: value` line exists in it, so Exec/Icon would be missing and the checks below would have
	// nothing to say.
	assert.deepEqual(check(), []);
	assert.equal(parseDesktopKeys(DESKTOP).Exec, "/opt/TinadecOffice/TinadecOffice %U");
});

test("A launcher pointing at a binary the package does not ship is refused", () => {
	const problems = check({
		desktopFile: DESKTOP.replace("Exec=/opt/TinadecOffice/TinadecOffice", "Exec=/opt/TinadecOffice/tinadec"),
	});

	assert.equal(problems.length, 1);
	assert.match(problems[0], /does not name any installed binary/);
});

test("An icon key with no icon in the archive is refused", () => {
	// The failure electron-builder produces without a large enough source: the package installs, the
	// launcher exists, and the app shows a generic tile forever.
	const problems = check({ dataEntries: DATA.filter((entry) => !entry.includes("icons")) });

	assert.equal(problems.length, 1);
	assert.match(problems[0], /Icon 'tinadecoffice' matches no icon/);
});

test("Package metadata the installer and the archive must agree on is checked", () => {
	assert.match(check({ version: "9.9.9" })[0], /does not carry the app version '9\.9\.9'/);
	assert.match(check({ control: CONTROL.replace(/Maintainer: .*/, "Maintainer: Lincube") })[0], /no address/);
	assert.match(check({ control: CONTROL.replace("amd64", "arm64") })[0], /expected amd64/);
	// A package with no installed binary is reported twice on purpose: the structure is wrong and so
	// is the launcher that points into it. Collapsing the two would hide one behind the other.
	assert.deepEqual(check({ dataEntries: DATA.filter((entry) => !entry.startsWith("./opt/")) }), [
		"no executable installed under /opt/<Product>/",
		".desktop Exec '/opt/TinadecOffice/TinadecOffice %U' does not name any installed binary (none)",
	]);
	assert.match(check({ desktopFile: "[Desktop Entry]\nName=TinadecOffice\n" })[0], /no Exec key/);
});

test("The checker is wired into the Linux leg and reads the archive, not a guess", () => {
	// The Bun migration made every script `bun scripts/...`; the assertion names the entry, not Node.
	assert.match(pkg.scripts["check:deb"] ?? "", /bun scripts\/check-deb-package\.mjs/);
	const workflow = readFileSync(resolve(desktopDir, "..", "..", ".github", "workflows", "desktop-release.yml"), "utf8");
	// The Bun migration rewrote the workflow step to `bun run --cwd apps/desktop check:deb`.
	assert.match(workflow, /bun run --cwd apps\/desktop check:deb/);
	const source = readFileSync(join(desktopDir, "scripts", "check-deb-package.mjs"), "utf8");
	assert.match(source, /\["xOf", dataTar, desktopName\]/, "the .desktop file must come out of data.tar");
	assert.equal(existsSync(join(desktopDir, "scripts", "check-deb-package.mjs")), true);
});

test("The deb explicitly preserves Electron dependencies and requires the bwrap runtime library", () => {
	assert.deepEqual(pkg.build.deb.depends, ["libgtk-3-0", "libnotify4", "libnss3", "libxss1", "libxtst6", "xdg-utils", "libatspi2.0-0", "libuuid1", "libsecret-1-0", "libcap2"]);
	assert.match(check({ control: CONTROL.replace(", libcap2", "") })[0], /Depends does not require libcap2/);
	assert.match(check({ control: CONTROL.replace("libcap2", "libcap2-bin") })[0], /Depends does not require libcap2/);
	assert.deepEqual(check({ control: CONTROL.replace("libcap2", "\n libcap2 (>= 2.0)") }), []);
	assert.equal(parseControlFields("Depends: libnss3,\n libcap2\nVersion: 1").Depends, "libnss3, libcap2");
});

test("Linux runtime payloads require nonempty x64 ELF and archive execute permissions", () => {
	assert.match(check({ dataEntries: DATA.filter((entry) => !entry.endsWith("/tools/bwrap")) })[0], /tools\/bwrap.*missing/);
	assert.match(check({ runtimeFiles: { ...RUNTIME_FILES, "tools/bwrap": { ...RUNTIME_FILES["tools/bwrap"], size: 0 } } })[0], /nonempty regular-file payload/);
	const arm = elf(); arm.writeUInt16LE(183, 18);
	assert.match(check({ runtimeFiles: { ...RUNTIME_FILES, "tools/bwrap": { ...RUNTIME_FILES["tools/bwrap"], head: arm } } })[0], /not Linux x64 ELF/);
	assert.match(check({ runtimeFiles: { ...RUNTIME_FILES, "tools/bwrap": { ...RUNTIME_FILES["tools/bwrap"], permissions: "-rw-r--r--" } } })[0], /no owner execute permission/);
	assert.match(check({ runtimeFiles: { ...RUNTIME_FILES, "tools/bwrap": { ...RUNTIME_FILES["tools/bwrap"], permissions: "lrwxrwxrwx" } } })[0], /regular-file payload/);
	assert.match(check({ runtimeFiles: { ...RUNTIME_FILES, "core/TinadecCore.Api": { ...RUNTIME_FILES["core/TinadecCore.Api"], head: Buffer.from("not executable") } } })[0], /not Linux x64 ELF/);
});

test("Tag packaging runs native configuration and scope tests before creating artifacts", () => {
	const workflow = readFileSync(resolve(desktopDir, "..", "..", ".github", "workflows", "desktop-release.yml"), "utf8");
	assert.match(workflow, /dotnet test TinadecCore\/tests\/TinadecCore.Api.Tests\/TinadecCore.Api.Tests.csproj/);
	assert.match(workflow, /FullyQualifiedName~ConfigurationDocumentTests\|FullyQualifiedName~StorageScopeApiTests/);
	assert.ok(workflow.indexOf("Test native TOML and storage scopes for this release") < workflow.indexOf("Build and package the application"));
});

const useWsl = process.platform === "win32" && process.env.TINADEC_TEST_DEB_WSL === "1";
const archiveToolsPresent = useWsl || (process.platform !== "win32" && spawnSync("ar", ["--version"], { encoding: "utf8" }).status === 0);
test("The reader checks bytes and dependencies from an actual small deb archive", { skip: !archiveToolsPresent }, () => {
	const work = mkdtempSync(join(tmpdir(), "tinadec-deb-fixture-"));
	const unixPath = (value) => value.replace(/^([A-Za-z]):[\\/]/, (_, drive) => `/mnt/${drive.toLowerCase()}/`).replaceAll("\\", "/");
	const runCommand = (command, args, cwd = work) => {
		const result = useWsl
			? spawnSync("wsl", ["--cd", unixPath(cwd), "--", command, ...args.map(unixPath)], { encoding: "utf8", maxBuffer: 16 * 1024 * 1024 })
			: spawnSync(command, args, { cwd, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 });
		assert.equal(result.status, 0, result.error?.message ?? result.stderr); return result.stdout;
	};
	try {
		const controlDir = join(work, "control"); const dataDir = join(work, "data"); mkdirSync(controlDir); mkdirSync(dataDir);
		writeFileSync(join(controlDir, "control"), CONTROL); writeFileSync(join(controlDir, "postinst"), "#!/bin/sh\n");
		for (const file of DATA.filter((entry) => !entry.endsWith("/"))) {
			const path = join(dataDir, file); mkdirSync(dirname(path), { recursive: true });
			writeFileSync(path, file.endsWith(".desktop") ? DESKTOP : RUNTIME_BINARIES.some((binary) => file.endsWith("/" + binary)) ? elf() : "fixture resource"); chmodSync(path, 0o755);
		}
		writeFileSync(join(work, "debian-binary"), "2.0\n");
		runCommand("tar", ["-czf", join(work, "control.tar.gz"), "-C", controlDir, "."]);
		const archive = join(work, "fixture.deb");
		const packData = () => { runCommand("tar", ["--mode=0755", "-czf", join(work, "data.tar.gz"), "-C", dataDir, "."]); runCommand("ar", ["cr", archive, "debian-binary", "control.tar.gz", "data.tar.gz"]); };
		packData();
		const inspect = () => { const contents = readDeb(archive, { runCommand }); return inspectDeb({ ...contents, desktopEntries: contents.controlEntries, version: "0.1.1" }); };
		assert.deepEqual(inspect(), []);
		writeFileSync(join(dataDir, "opt/TinadecOffice/resources/runtime/tools/bwrap"), "empty or corrupted binary"); packData();
		assert.match(inspect().join("\n"), /tools\/bwrap.*not Linux x64 ELF/);
	} finally { rmSync(work, { recursive: true, force: true }); }
});
