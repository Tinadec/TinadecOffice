import { existsSync } from "fs";
import { spawnSync } from "child_process";
import { dirname, join, resolve } from "path";
import { fileURLToPath } from "url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");

const skillFiles = [
  join(root, ".agents", "skills", "shadcn-vue", "SKILL.md"),
  join(root, ".claude", "skills", "shadcn-vue", "SKILL.md"),
];

if (skillFiles.every(existsSync)) {
  console.log("[shadcn-ai] shadcn-vue skill already installed (opencode + claude-code)");
} else {
  console.log("[shadcn-ai] Installing shadcn-vue skill for opencode + claude-code...");
  const isWindows = process.platform === "win32";
  const command = isWindows
    ? "npx skills add unovue/shadcn-vue -a opencode -a claude-code -y --copy"
    : "npx";
  const args = isWindows
    ? []
    : ["skills", "add", "unovue/shadcn-vue", "-a", "opencode", "-a", "claude-code", "-y", "--copy"];
  const result = spawnSync(command, args, { cwd: root, stdio: "inherit", shell: isWindows });
  if (result.status !== 0) {
    console.warn(
      "[shadcn-ai] Skill install failed; run 'npx skills add unovue/shadcn-vue -a opencode -a claude-code -y --copy' manually."
    );
  }
}
