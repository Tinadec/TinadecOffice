/** Thin snake_case passthrough for agent-modes — nodes/edges/layout stays in draft body, Core governs */
export type CoreAgentModeDto = Record<string, unknown>;
export type ExternalAgentModeDto = Record<string, unknown>;

export function mapAgentMode(core: unknown): ExternalAgentModeDto | null {
  if (!core || typeof core !== 'object' || Array.isArray(core)) return null;
  return core as ExternalAgentModeDto;
}

export function mapAgentModes(core: unknown): ExternalAgentModeDto[] {
  if (Array.isArray(core)) return core.map(mapAgentMode).filter((x): x is ExternalAgentModeDto => x !== null);
  const single = mapAgentMode(core);
  return single ? [single] : [];
}

// snake_case guard — minimal
const SNAKE = /^[a-z][a-z0-9_]*$/;
export function isSnakeCaseKey(key: string): boolean {
  return SNAKE.test(key);
}
