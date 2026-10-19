/** Private credentials are only passed to the trusted Electron launch, never Vite/plugins. */
export function developmentEnvironment(extraEnv = {}, trustedHost = false, environment = process.env) {
  const result = { ...environment, ...extraEnv };
  delete result.ELECTRON_RUN_AS_NODE;
  delete result.ELECTRON_NO_ATTACH_CONSOLE;
  if (!trustedHost) delete result.TINADEC_HOST_CONTROL_TOKEN;
  return result;
}

/** CDP is opt-in. Validate before the value reaches a shell-spawned Electron argv. */
export function developmentCdpPort(environment = process.env) {
  const raw = environment.TINADEC_DEV_CDP_PORT?.trim();
  if (!raw) return null;
  if (!/^\d+$/.test(raw)) {
    throw new Error("TINADEC_DEV_CDP_PORT must be an integer between 1024 and 65535.");
  }
  const port = Number(raw);
  if (!Number.isInteger(port) || port < 1024 || port > 65535) {
    throw new Error("TINADEC_DEV_CDP_PORT must be an integer between 1024 and 65535.");
  }
  return port;
}
