import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

import { RUNTIME_TARGETS } from "./runtimeTargets.mjs";
import {
	electronSmokeArgs,
	packagedExecutablePath,
	parseHdiutilOutput,
	parseSmokeArgs,
	posixDataRoot,
	sanitizedPosixEnvironment,
} from "./smoke-packaged-posix.mjs";
import { sanitizedEnvironment } from "./smoke-packaged-windows.mjs";
import { smokeChecks, smokePorts, validateGatewayHealth, validateHarnessManifest, validateToolsManifest } from "./smokeAssertions.mjs";
import { newSmokeHostControlToken, smokeHeaders, SMOKE_HOST_CONTROL_ENV, SMOKE_HOST_CONTROL_HEADER } from "./smokeAssertions.mjs";

const desktopDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const posixScript = readFileSync(join(desktopDir, "scripts", "smoke-packaged-posix.mjs"), "utf8");
const windowsScript = readFileSync(join(desktopDir, "scripts", "smoke-packaged-windows.mjs"), "utf8");
const pkg = JSON.parse(readFileSync(join(desktopDir, "package.json"), "utf8"));

const linux = RUNTIME_TARGETS["linux-x64"];
const mac = RUNTIME_TARGETS["osx-arm64"];

test("Each POSIX target's packaged binary is found where its packager puts it", () => {
	assert.equal(
		packagedExecutablePath("/r", linux),
		join("/r", "linux-unpacked", "TinadecOffice"),
	);
	assert.equal(
		packagedExecutablePath("/r", mac),
		join("/r", "mac-arm64", "TinadecOffice.app", "Contents", "MacOS", "TinadecOffice"),
	);
	// The Windows layout is a different script's business; a POSIX smoke that guessed at it would
	// report "executable missing" on a build that packaged fine.
	assert.throws(() => packagedExecutablePath("/r", RUNTIME_TARGETS["win-x64"]), /No packaged layout/);
});

test("The log directory follows the data root the app itself would choose", () => {
	// Mirrors officeRootFor() in electron/serviceManager.cjs. If the two disagree the smoke still
	// fails, but with no logs attached — which is the failure mode this file exists to avoid.
	const serviceManager = readFileSync(join(desktopDir, "electron", "serviceManager.cjs"), "utf8");
	const linuxRoot = posixDataRoot("/p", linux);
	const macRoot = posixDataRoot("/p", mac);

  assert.equal(linuxRoot, join("/p", ".tinadec"));
  assert.equal(macRoot, join("/p", ".tinadec"));
  assert.match(serviceManager, /storagePaths\(/, "serviceManager uses the unified path resolver");
  assert.match(posixScript, /"logs", "host"/, "smoke reads the host child-stream sink");
});

test("Only Linux is started without the renderer sandbox", () => {
	const linuxArgs = electronSmokeArgs("/u", linux);
	const macArgs = electronSmokeArgs("/u", mac);

	assert.ok(linuxArgs.includes("--no-sandbox"), "a root runner cannot start Chromium's setuid sandbox");
	assert.ok(!macArgs.includes("--no-sandbox"), "macOS runs with its real privileges or the smoke proves nothing");
	for (const args of [linuxArgs, macArgs]) {
		assert.ok(args.some((arg) => arg.startsWith("--user-data-dir=")), "the smoke owns its profile");
		assert.ok(args.includes("--disable-gpu"), "build machines have no GPU");
		assert.ok(args.includes("--no-first-run"));
	}
});

test("The child sees a throwaway profile and the loopback topology", () => {
	const env = sanitizedPosixEnvironment({ profile: "/p", temporary: "/t", inheritedPath: "/usr/bin:/bin", display: ":99" });

	assert.equal(env.HOME, "/p");
  assert.equal(env.TINADEC_HOME, join("/p", ".tinadec"));
	assert.equal(env.XDG_DATA_HOME, join("/p", ".local", "share"));
	assert.equal(env.TMPDIR, "/t");
	assert.equal(env.TINADEC_GATEWAY_URL, "http://127.0.0.1:48730", "the packaged app only owns services on that exact URL");
	assert.equal(env.PATH, "/usr/bin:/bin", "git is probed from PATH on POSIX, so a stripped PATH lies about the product");
	assert.equal(env.DISPLAY, ":99");
	assert.equal(sanitizedPosixEnvironment({ profile: "/p", temporary: "/t" }).DISPLAY, undefined);
});

test("The mounted volume is read out of hdiutil's own output", () => {
	// Real hdiutil columns, and a volume name that contains the version — the mount point is the
	// last column, not the last whitespace-separated word.
	assert.equal(
		parseHdiutilOutput("/dev/disk4s1\tGUID_partition_scheme\n/dev/disk4s2\tApple_HFS\t/Volumes/TinadecOffice 0.1.1\n"),
		"/Volumes/TinadecOffice 0.1.1",
	);
	assert.equal(parseHdiutilOutput("nothing useful here"), null);
});

test("The shared predicates accept what Core and Gateway send and refuse what they do not", () => {
	assert.equal(validateGatewayHealth({ gateway: "ok", core_status: "ready" }), true);
	// The degraded answer is the one that matters: Gateway is up, Core is not, and a smoke that only
	// checked HTTP 200 would call that a pass.
	assert.equal(validateGatewayHealth({ gateway: "ok", core_status: "unreachable" }), false);
	assert.equal(validateToolsManifest([]), false, "an empty manifest means the tool child never answered");
	assert.equal(validateToolsManifest([{ id: "read_file" }]), true);
	assert.equal(validateHarnessManifest({ runtime: "tinadec", tool_registry: {}, modules: [] }), true);
	assert.equal(validateHarnessManifest({ runtime: "tinadec", tool_registry: {} }), false);
});

test("Both smokes read the contract from one module, and it covers every service endpoint", () => {
	for (const [name, source] of [["windows", windowsScript], ["posix", posixScript]]) {
		assert.match(source, /from "\.\/smokeAssertions\.mjs"/, `${name} must not re-fork the smoke contract`);
	}
	assert.equal(new Set(smokeChecks.map((check) => check.url)).size, smokeChecks.length);
	assert.deepEqual(
		smokeChecks.map((check) => check.label),
		[
			"Core health",
			"Gateway health",
			"Core readiness",
			"bundled tools manifest",
			"bundled tools readiness",
			"bundled harness manifest",
		],
	);
	assert.deepEqual(smokePorts, [48730, 48731, 48732]);
});

/// Core refuses every mounted request that cannot present the trusted host credential, so a smoke
/// that probes readiness anonymously measures its own omission: all three legs timed out on HTTP 403
/// for four days while Core and Gateway were demonstrably up in their own logs. The credential is
/// minted by the packaged host and inherited by the services it spawns, so the smoke has to supply
/// it in the child environment and echo it back on the probes.
test("Every protected probe carries the host credential, and health does not need one", () => {
	const token = newSmokeHostControlToken();
	assert.match(token, /^[A-Za-z0-9_-]{43,256}$/, "the shape createHostControl accepts");

	assert.equal(smokeHeaders(token, { public: true })[SMOKE_HOST_CONTROL_HEADER], undefined);
	assert.equal(smokeHeaders(token)[SMOKE_HOST_CONTROL_HEADER], token);

	// Health must stay anonymous: if every probe were credentialed, a wrong token and a Core that
	// never started would both read as "timed out", and the log could not say which happened.
	const byLabel = new Map(smokeChecks.map((check) => [check.label, check]));
	assert.equal(byLabel.get("Core health").public, true);
	assert.equal(byLabel.get("Gateway health").public, true);
	for (const check of smokeChecks) {
		if (check.public) continue;
		assert.notEqual(
			smokeHeaders(token, { public: check.public })[SMOKE_HOST_CONTROL_HEADER],
			undefined,
			`${check.label} is a mounted Core route and must present the credential`,
		);
	}

	// The credential has to reach the child, because Core and Gateway inherit it from the process
	// the smoke starts rather than reading it from disk.
	for (const [name, env] of [
		["windows", sanitizedEnvironment("C:\\p", "C:\\t", token)],
		["posix", sanitizedPosixEnvironment({ profile: "/p", temporary: "/t", hostControlToken: token })],
	]) {
		assert.equal(env[SMOKE_HOST_CONTROL_ENV], token, `${name} must hand the credential to the child`);
	}
});

test("A smoke started without a credential is still describable rather than silently wrong", () => {
	// Omitting the token is how this broke; the helper must not invent one, or a caller that forgot
	// it would look like a caller that passed it.
	const env = sanitizedPosixEnvironment({ profile: "/p", temporary: "/t" });
	assert.equal(SMOKE_HOST_CONTROL_ENV in env, false);
});

test("The POSIX smoke is wired for both legs and refuses Windows", () => {
	assert.match(pkg.scripts["smoke:packaged:posix"] ?? "", /node scripts\/smoke-packaged-posix\.mjs/);
	assert.match(posixScript, /The POSIX smoke does not run on Windows/);
	assert.equal(existsSync(join(desktopDir, "scripts", "smokeAssertions.mjs")), true);

	// The ubuntu runner died with "Authorization required, but no authorization protocol specified"
	// then SIGSEGV: an X server alone is not enough when HOME has been redirected, because Xlib's
	// cookie lookup follows $HOME/.Xauthority.
	const workflow = readFileSync(resolve(desktopDir, "..", "..", ".github", "workflows", "desktop-release.yml"), "utf8");
	assert.match(workflow, /xvfb-run[^\n]*-ac/, "the Linux smoke needs an X server that accepts the redirected profile");
});

test("A desktop session's X authority survives the redirected HOME", () => {
	const env = sanitizedPosixEnvironment({ profile: "/p", temporary: "/t", xauthority: "/run/user/1000/mcookie" });

	assert.equal(env.XAUTHORITY, "/run/user/1000/mcookie");
	assert.equal(env.HOME, "/p", "the two are not in conflict: the app's data stays in the profile");
	assert.equal(sanitizedPosixEnvironment({ profile: "/p", temporary: "/t" }).XAUTHORITY, undefined);
});

test("The smoke can be pointed at an installed binary instead of the unpacked one", () => {
	assert.deepEqual(parseSmokeArgs(["/r"]), { releaseDir: "/r" });
	assert.deepEqual(
		parseSmokeArgs(["/r", "--executable=/opt/TinadecOffice/TinadecOffice", "--label=linux-x64 installed"]),
		{ releaseDir: "/r", executable: "/opt/TinadecOffice/TinadecOffice", label: "linux-x64 installed" },
	);
	// A typo here must not silently fall back to the unpacked directory — the step would still pass
	// while proving nothing about what the package manager installed.
	assert.throws(() => parseSmokeArgs(["--executible=/opt/x"]), /Unknown smoke option --executible=/);
});

test("The Linux leg installs its own .deb and smoke tests the installed file", () => {
	// Normalise the line endings: a Windows checkout hands this file CRLF, and a pattern that spans
	// lines matches nothing there (it cost a run once).
	const workflow = readFileSync(resolve(desktopDir, "..", "..", ".github", "workflows", "desktop-release.yml"), "utf8").replace(
		/\r\n/gu,
		"\n",
	);
	const install = workflow.slice(
		workflow.indexOf("- name: Install the .deb the way a user would"),
		workflow.indexOf("- name: Smoke test the installed application (Linux)"),
	);
	assert.ok(install.length > 0 && install.includes("matrix.label == 'linux-x64'"), "the install step is Linux-only");
	assert.match(install, /apt-get install -y --no-install-recommends "\.\/\$deb"/, "let the package manager resolve Depends");
	assert.match(install, /dpkg-deb -f "\$deb" Package/, "read the identity out of the artifact, not a guess");
	assert.match(install, /test -x \/opt\/TinadecOffice\/TinadecOffice/, "the installed launcher has to be there");
	// The installed launcher name must agree with what the packager was told to produce.
	assert.equal(`/opt/${pkg.build.productName}/${pkg.build.linux.executableName}`, "/opt/TinadecOffice/TinadecOffice");

	const installedSmoke = workflow.slice(workflow.indexOf("- name: Smoke test the installed application (Linux)"));
	const step = installedSmoke.slice(0, installedSmoke.indexOf("\n\n"));
	assert.match(step, /smoke:packaged:posix/, "the installed run uses the POSIX smoke, not the Windows one");
	assert.match(step, /--\s*\n?\s*--executable=\/opt\/TinadecOffice\/TinadecOffice/, "without the override it re-runs the unpacked app");
	assert.match(step, /--label="linux-x64 installed"/, "the log line must say which binary ran");
	assert.match(step, /xvfb-run[^\n]*-ac/, "the installed run needs the same X server treatment");
});
