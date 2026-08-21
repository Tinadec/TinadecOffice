/** Thin snake_case passthrough for agents — no business logic, Core owns config */
export type CoreAgentDto = Record<string, unknown>;
export type ExternalAgentDto = Record<string, unknown>;

// ponytail: passthrough, snake_case only — Core validates ownership/lifecycle
export function mapAgent(core: unknown): ExternalAgentDto | null {
  if (!core || typeof core !== 'object' || Array.isArray(core)) return null;
  return core as ExternalAgentDto;
}

export function mapAgents(core: unknown): ExternalAgentDto[] {
  if (Array.isArray(core)) return core.map(mapAgent).filter((x): x is ExternalAgentDto => x !== null);
  const single = mapAgent(core);
  return single ? [single] : [];
}

// snake_case key guard — minimal, no re-computation
const SNAKE = /^[a-z][a-z0-9_]*$/;
export function isSnakeCaseKey(key: string): boolean {
  return SNAKE.test(key);
}
export function hasNonSnakeKeys(obj: Record<string, unknown>): string[] {
  return Object.keys(obj).filter((k) => !isSnakeCaseKey(k));
}
