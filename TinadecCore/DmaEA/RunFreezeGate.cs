using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Gate 3 of the DmaEA checker (run freeze). Runs at configuration freeze (run
/// admission) and again at recovery after the frozen document is re-verified.
///
/// Rules (each fail-closed with a snake_case code on RunAdmissionException):
/// ① conversation_identity_locked_mismatch — when the session carries a frozen
///    ConversationIdentity, the operation roster must contain that template and
///    it must hold the conversation capability. Sessions frozen before identity
///    existed (null identity) keep the legacy literal-meeting semantics.
/// ② instance_pool violations — pool members must be known roster instances and
///    the main instance must be the conversation identity; an absent/empty pool
///    is the tolerated legacy singleton shape.
/// ③ topology — the frozen roster must retain an execution layer.
///
/// Deliberately NOT here: the operation-layer tool floor. Template-level
/// tool_scope entries on operation agents only contribute the frozen manifest
/// ceiling (a legitimate existing configuration shape); the hard deny floor is
/// CoreAuthorizationContextResolver's operation_layer_cannot_invoke_tools at
/// dispatch time, and binding-level narrowing is ModePublishGate's
/// operation_tool_floor_violation. Rejecting declared-but-never-authoritative
/// operation template tools here would break workspaces that predate the floor.
/// </summary>
public static class RunFreezeGate
{
    public sealed record ConversationIdentity(string NodeKey, string TemplateSlug);

    public sealed record PoolState(Guid? IdentityInstanceId, IReadOnlyList<Guid> PoolMemberIds, Guid? MainInstanceId);

    public static void Validate(
        ConversationIdentity? identity,
        IReadOnlyList<RuntimeAgentDefinition> operation,
        IReadOnlyList<RuntimeAgentDefinition> execution)
    {
        // ① conversation identity lock
        if (identity is not null)
        {
            var holder = operation.FirstOrDefault(agent =>
                string.Equals(agent.Id, identity.TemplateSlug, StringComparison.OrdinalIgnoreCase));
            if (holder is null)
                throw new RunAdmissionException("conversation_identity_locked_mismatch",
                    $"Session identity '{identity.TemplateSlug}' (node '{identity.NodeKey}') is not part of the frozen operation roster.");
            if (!holder.Capabilities.Any(capability =>
                    string.Equals(capability, ThreeNamespaceMap.ConverseCapability, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(capability, ThreeNamespaceMap.ConverseCapabilityAlias, StringComparison.OrdinalIgnoreCase)))
                throw new RunAdmissionException("conversation_identity_locked_mismatch",
                    $"Conversation identity holder '{holder.Id}' does not carry a conversation capability.");
        }

        // ③ topology: execution roster must exist (legacy invariant)
        if (execution.Count == 0)
            throw new RunAdmissionException("mode_topology_invalid",
                "The frozen roster has no execution-layer agent.");
    }

    /// <summary>
    /// Pool-shape validation over restored checkpoints. An absent identity and an
    /// empty pool is the tolerated legacy singleton shape; a pool without an
    /// identity, or a main instance that is not the identity holder, is invalid.
    /// </summary>
    public static void ValidatePool(PoolState? pool)
    {
        if (pool is null) return;
        if (pool.IdentityInstanceId is null)
        {
            if (pool.PoolMemberIds.Count > 0)
                throw new RunAdmissionException("instance_pool_context_mismatch",
                    "Checkpoint declares pool members without a conversation identity instance.");
            return;
        }
        if (pool.PoolMemberIds.Count == 0)
            throw new RunAdmissionException("instance_pool_context_mismatch",
                "Checkpoint declares a conversation identity instance without pool members.");
        if (pool.MainInstanceId is { } main && main != pool.IdentityInstanceId)
            throw new RunAdmissionException("instance_pool_context_mismatch",
                "The checkpoint main instance must be the conversation identity instance.");
        if (pool.PoolMemberIds.Distinct().Count() != pool.PoolMemberIds.Count)
            throw new RunAdmissionException("instance_pool_context_mismatch",
                "Checkpoint pool contains duplicate member ids.");
    }
}
