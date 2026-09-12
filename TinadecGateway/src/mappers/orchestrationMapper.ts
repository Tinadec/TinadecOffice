/** Orchestration projection mapper: Core->External, preserves snake_case */
function isRecord(v: unknown): v is Record<string, unknown> { return typeof v === 'object' && v !== null && !Array.isArray(v); }

export function mapOrchestration(core: unknown): Record<string, unknown> {
  if (!isRecord(core)) return { run: null, nodes: [], assignments: [], step_results: [], context_packs: [], supervision_findings: [] };
  // passthrough for test/mocked proxied shapes and any non-orchestration objects
  if ('proxied' in core && !('run' in core)) return core as Record<string, unknown>;
  const run = core.run ?? null;
  return {
    run,
    graph: (core.graph as unknown) ?? null,
    flows: Array.isArray(core.flows) ? core.flows : [],
    nodes: Array.isArray(core.nodes) ? core.nodes : [],
    lanes: Array.isArray(core.lanes) ? core.lanes : [],
    assignments: Array.isArray(core.assignments) ? core.assignments : [],
    step_results: Array.isArray(core.step_results) ? core.step_results : Array.isArray((core as Record<string, unknown>).stepResults) ? (core as Record<string, unknown>).stepResults : [],
    context_packs: Array.isArray(core.context_packs) ? core.context_packs : [],
    supervision_findings: Array.isArray(core.supervision_findings) ? core.supervision_findings : [],
    agent_instances: (core as Record<string, unknown>).agent_instances ?? (core as Record<string, unknown>).agentInstances ?? [],
    frozen: (core as Record<string, unknown>).frozen ?? null,
  };
}
