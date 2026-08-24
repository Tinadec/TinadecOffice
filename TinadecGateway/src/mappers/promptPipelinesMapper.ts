/** Thin snake_case passthrough for prompt-pipelines */
export type CorePromptPipelineDto = Record<string, unknown>;
export type ExternalPromptPipelineDto = Record<string, unknown>;

export function mapPromptPipeline(core: unknown): ExternalPromptPipelineDto | null {
  if (!core || typeof core !== 'object' || Array.isArray(core)) return null;
  return core as ExternalPromptPipelineDto;
}

export function mapPromptPipelines(core: unknown): ExternalPromptPipelineDto[] {
  if (Array.isArray(core)) return core.map(mapPromptPipeline).filter((x): x is ExternalPromptPipelineDto => x !== null);
  const single = mapPromptPipeline(core);
  return single ? [single] : [];
}
