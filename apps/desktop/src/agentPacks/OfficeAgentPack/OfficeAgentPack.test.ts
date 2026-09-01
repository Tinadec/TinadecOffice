import { describe, expect, it } from 'vitest'
import {
  OFFICE_AGENT_PACK_DIGEST,
  OFFICE_AGENT_PACK_ID,
  digestAgentPackManifest,
  officeAgentPackEnvelope,
  officeAgentPackManifest,
} from './index'

describe('OfficeAgentPack', () => {
  it('has stable app ownership and internal references', () => {
    expect(officeAgentPackManifest.metadata.pack_id).toBe(OFFICE_AGENT_PACK_ID)
    expect(officeAgentPackManifest.metadata.owner).toBe('tinadec.office')
    expect(officeAgentPackManifest.metadata.product_id).toBe('tinadec.office')
    expect(officeAgentPackManifest.metadata.version).toBe('0.2.2')
    expect(officeAgentPackManifest.resources.agents).toHaveLength(14)
    expect(officeAgentPackManifest.resources.agents.every((agent) => Boolean(agent.system_prompt?.trim()))).toBe(true)
    // Core denies every tool invocation for an operation-layer instance, so a governance
    // role that declared tools would ship a pack whose data contradicts its enforced behavior.
    for (const agent of officeAgentPackManifest.resources.agents.filter((row) => row.layer === 'operation')) {
      expect(agent.tool_scope, `agent '${agent.resource_key}' must declare no tools`).toEqual([])
    }

    const resourceKeys = new Set(officeAgentPackManifest.resources.agents.map((agent) => agent.resource_key))
    const promptKeys = new Set(officeAgentPackManifest.resources.prompt_pipelines.map((pipeline) => pipeline.resource_key))

    // One default-mode (full space.full_duplex roster) plus one topology per composer mode.
    const modes = officeAgentPackManifest.resources.modes
    expect(modes).toHaveLength(7)
    for (const mode of modes) {
      const nodeAgentRefs = mode.nodes.map((node) => node.agent_ref)
      // Every mode keeps both governance and execution lanes populated.
      expect(mode.nodes.some((node) => node.layer === 'operation')).toBe(true)
      expect(mode.nodes.some((node) => node.layer === 'execution')).toBe(true)
      for (const node of mode.nodes) {
        expect(node.agent_ref).toMatch(/^agent:/)
        expect(resourceKeys.has(node.agent_ref.slice('agent:'.length))).toBe(true)
      }
    }
    const defaultMode = modes.find((mode) => mode.resource_key === 'default-mode')!
    expect(defaultMode.nodes).toHaveLength(14)
    for (const composer of ['ask', 'vibe', 'plan', 'spec', 'auto', 'agent'] as const) {
      const mode = modes.find((m) => m.resource_key === `conversation.${composer}`)
      expect(mode, `conversation.${composer} mode present`).toBeDefined()
      expect(mode!.nodes.some((node) => node.agent_ref === 'agent:meeting')).toBe(true)
      expect(mode!.nodes.some((node) => node.agent_ref === 'agent:task_planner')).toBe(true)
    }

    for (const agent of officeAgentPackManifest.resources.agents) {
      expect(agent.base_prompt_pipeline_ref).toMatch(/^prompt:/)
      expect(promptKeys.has(agent.base_prompt_pipeline_ref!.slice('prompt:'.length))).toBe(true)
    }
    expect(resourceKeys.has(officeAgentPackManifest.activation.workspace_defaults.agent_ref.slice('agent:'.length))).toBe(true)
    expect(officeAgentPackManifest.resources.modes.some((mode) => `mode:${mode.resource_key}` === officeAgentPackManifest.activation.workspace_defaults.mode_ref)).toBe(true)
    expect(promptKeys.has(officeAgentPackManifest.activation.workspace_defaults.prompt_pipeline_ref.slice('prompt:'.length))).toBe(true)
  })

  it('carries the RFC 8785 SHA-256 digest of the manifest only', async () => {
    expect(await digestAgentPackManifest(officeAgentPackManifest)).toBe(OFFICE_AGENT_PACK_DIGEST)
    expect(officeAgentPackEnvelope.integrity.digest).toBe(OFFICE_AGENT_PACK_DIGEST)
  })
})
