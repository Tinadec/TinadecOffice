using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Gate 3 (run freeze) pinning: a frozen conversation identity must be present
/// in the operation roster and hold a conversation capability; topology must
/// retain an execution layer; pool shapes must be internally consistent — with
/// the absent/empty pool tolerated as the legacy singleton shape. The
/// operation-layer tool floor is enforced at dispatch (resolver deny boundary)
/// and at binding level (ModePublishGate), deliberately NOT against template
/// tool_scope (a declared ceiling, not authority).
/// </summary>
public sealed class RunFreezeGateTests
{
    private static RuntimeAgentDefinition Agent(string id, string layer, string[]? capabilities = null, string[]? tools = null) =>
        new(id, layer, "task_executor", "on_demand", capabilities ?? [], DirectUserOutput: false, ContextAccess: "read")
        { AllowedTools = tools ?? [] };

    private static readonly RunFreezeGate.ConversationIdentity Identity = new("meeting", "meeting");

    [Fact]
    public void OperationAgentWithDeclaredTools_IsTolerated_CeilingNotAuthority()
    {
        // Template tool_scope contributes only the frozen manifest ceiling; the
        // runtime deny floor and binding-level gates own the operation floor.
        var operation = new[] { Agent("meeting", "operation", ["user.respond"], ["read_file"]) };
        RunFreezeGate.Validate(Identity, operation, [Agent("worker.search", "execution")]);
    }

    [Fact]
    public void IdentityHolderMustExist_InOperationRoster()
    {
        var operation = new[] { Agent("other", "operation", ["user.respond"]) };
        var exception = Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.Validate(Identity, operation, [Agent("worker.search", "execution")]));
        Assert.Equal("conversation_identity_locked_mismatch", exception.Code);
    }

    [Fact]
    public void IdentityHolderMustCarryConversationCapability()
    {
        var operation = new[] { Agent("meeting", "operation", ["task.dispatch"]) };
        var exception = Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.Validate(Identity, operation, [Agent("worker.search", "execution")]));
        Assert.Equal("conversation_identity_locked_mismatch", exception.Code);
    }

    [Fact]
    public void ValidIdentityAndCleanOperationRoster_Passes()
    {
        var operation = new[] { Agent("meeting", "operation", ["user.respond", "task.dispatch", "agent.create_temporary"]) };
        RunFreezeGate.Validate(Identity, operation, [Agent("worker.search", "execution")]);
    }

    [Fact]
    public void NullIdentity_KeepsLegacySemantics()
    {
        // pre-identity sessions: floor + topology checks still run, identity skipped
        var operation = new[] { Agent("meeting", "operation", ["user.respond"]) };
        RunFreezeGate.Validate(null, operation, [Agent("worker.general", "execution")]);
    }

    [Fact]
    public void EmptyExecutionRoster_IsRejected()
    {
        var operation = new[] { Agent("meeting", "operation", ["user.respond"]) };
        var exception = Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.Validate(null, operation, []));
        Assert.Equal("mode_topology_invalid", exception.Code);
    }

    [Fact]
    public void Pool_IdentityWithoutMembers_IsRejected()
    {
        var exception = Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.ValidatePool(new RunFreezeGate.PoolState(Guid.NewGuid(), [], null)));
        Assert.Equal("instance_pool_context_mismatch", exception.Code);
    }

    [Fact]
    public void Pool_MembersWithoutIdentity_IsRejected()
    {
        var exception = Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.ValidatePool(new RunFreezeGate.PoolState(null, [Guid.NewGuid()], null)));
        Assert.Equal("instance_pool_context_mismatch", exception.Code);
    }

    [Fact]
    public void Pool_MainMustBeIdentity()
    {
        var identity = Guid.NewGuid();
        var exception = Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.ValidatePool(new RunFreezeGate.PoolState(identity, [identity, Guid.NewGuid()], Guid.NewGuid())));
        Assert.Equal("instance_pool_context_mismatch", exception.Code);
    }

    [Fact]
    public void Pool_LegacySingletonShape_IsTolerated()
    {
        // pre-pool checkpoints: no identity, no members, no main
        RunFreezeGate.ValidatePool(null);
        RunFreezeGate.ValidatePool(new RunFreezeGate.PoolState(null, [], null));
        var identity = Guid.NewGuid();
        RunFreezeGate.ValidatePool(new RunFreezeGate.PoolState(identity, [identity, Guid.NewGuid()], identity));
    }
}
