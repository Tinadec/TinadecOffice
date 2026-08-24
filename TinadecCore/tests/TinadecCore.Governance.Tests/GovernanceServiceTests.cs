using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Governance;

namespace TinadecCore.Governance.Tests;

public sealed class GovernanceServiceTests
{
    private static readonly CapabilityClaim WriteClaim = new("files", "write", "workspace://src/app.cs");
    private static readonly CapabilityRule AllowWrite = new("allow", "files", "write", "workspace://src/*");

    [Fact]
    public async Task Intersection_RequiresEveryBoundary_AndExistingGrantIssuesLease()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries =
        [
            new("principal", [AllowWrite]),
            new("agent_version", [AllowWrite]),
            new("tool_manifest", [AllowWrite]),
            new("task", [AllowWrite])
        ];
        var subject = Guid.NewGuid();
        await harness.Service.GrantCapabilityAsync(new GrantCapabilityCommand(
            subject, null, WriteClaim, null, null, harness.Time.GetUtcNow().AddHours(1), 1));

        var allowed = await harness.Service.RequestPermissionAsync(Request(subject, "intersection-allowed"));

        Assert.Equal(PermissionRequestStatuses.Granted, allowed.Request.Status);
        Assert.Equal(GovernanceOutcomes.Allowed, allowed.Decision.Outcome);
        Assert.NotNull(allowed.Lease);
        Assert.NotEmpty(allowed.Lease.Nonce);

        harness.Context.Boundaries =
        [
            new("principal", [AllowWrite]),
            new("task", [new CapabilityRule("allow", "files", "read", "workspace://src/*")])
        ];
        var denied = await harness.Service.RequestPermissionAsync(Request(Guid.NewGuid(), "intersection-denied"));
        Assert.Equal(PermissionRequestStatuses.Denied, denied.Request.Status);
        Assert.Equal("boundary_not_allowed", denied.Decision.ReasonCode);
    }

    [Fact]
    public async Task ExplicitDeny_WinsAcrossPersistedPolicyAndSuppliedBoundary()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("agent", [new CapabilityRule("allow", "*", "*", "*")])];
        await harness.Service.CreatePolicyBundleAsync(new CreatePolicyBundleCommand(
            "workspace-default", "Workspace default",
            [new("allow", "*", "*", "*"), new("deny", "files", "write", "workspace://src/*")]));

        var denied = await harness.Service.RequestPermissionAsync(Request(Guid.NewGuid(), "explicit-deny"));

        Assert.Equal(PermissionRequestStatuses.Denied, denied.Request.Status);
        Assert.Equal("explicit_deny", denied.Decision.ReasonCode);
    }

    [Fact]
    public async Task DelegatedApproval_WithinEnvelope_IssuesNonTransferableLease()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("hard_policy", [AllowWrite])];
        var requesterAgent = Guid.NewGuid();
        var approverAgent = Guid.NewGuid();
        var approverVersion = Guid.NewGuid();
        harness.Context.AgentVersions[approverAgent] = approverVersion;
        var delegation = await harness.Service.CreateApprovalDelegationAsync(new CreateApprovalDelegationCommand(
            approverVersion, approverAgent, [AllowWrite], "medium", 10, 2,
            harness.Time.GetUtcNow().AddHours(1)));
        var pending = await harness.Service.RequestPermissionAsync(Request(
            Guid.NewGuid(), "delegated-request", requesterAgent, "low", 1));
        Assert.Equal(PermissionRequestStatuses.AwaitingDelegate, pending.Request.Status);

        var resolved = await harness.Service.DecidePermissionAsync(new PermissionDecisionCommand(
            pending.Request.Id, true, approverAgent, delegation.Id, "Within delegated envelope."));

        Assert.Equal(PermissionRequestStatuses.Granted, resolved.Request.Status);
        Assert.Equal("delegated_approval", resolved.Decision.ReasonCode);
        Assert.NotNull(resolved.Grant);
        Assert.False(resolved.Grant.Transferable);
        Assert.NotNull(resolved.Lease);
        Assert.NotEmpty(resolved.Lease.Nonce);
    }

    [Fact]
    public async Task DelegatedApproval_OverRiskCeiling_IsDenied()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("hard_policy", [AllowWrite])];
        var approverAgent = Guid.NewGuid();
        var approverVersion = Guid.NewGuid();
        harness.Context.AgentVersions[approverAgent] = approverVersion;
        var delegation = await harness.Service.CreateApprovalDelegationAsync(new CreateApprovalDelegationCommand(
            approverVersion, approverAgent, [AllowWrite], "low", 10, 2,
            harness.Time.GetUtcNow().AddHours(1)));
        var pending = await harness.Service.RequestPermissionAsync(Request(
            Guid.NewGuid(), "over-ceiling", Guid.NewGuid(), "high", 1));
        Assert.Equal(PermissionRequestStatuses.AwaitingUser, pending.Request.Status);

        var denied = await harness.Service.DecidePermissionAsync(new PermissionDecisionCommand(
            pending.Request.Id, true, approverAgent, delegation.Id, "Attempted delegated approval."));

        Assert.Equal(PermissionRequestStatuses.Denied, denied.Request.Status);
        Assert.Equal("delegation_ceiling_exceeded", denied.Decision.ReasonCode);
        Assert.Null(denied.Lease);
    }

    [Fact]
    public async Task Delegation_RevocationAndUseExhaustion_FailClosed()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("hard_policy", [AllowWrite])];
        var approverAgent = Guid.NewGuid();
        var approverVersion = Guid.NewGuid();
        harness.Context.AgentVersions[approverAgent] = approverVersion;
        var delegation = await harness.Service.CreateApprovalDelegationAsync(new CreateApprovalDelegationCommand(
            approverVersion, approverAgent, [AllowWrite], "medium", 10, 1,
            harness.Time.GetUtcNow().AddHours(1)));

        var first = await harness.Service.RequestPermissionAsync(Request(Guid.NewGuid(), "delegation-first", Guid.NewGuid()));
        var approved = await harness.Service.DecidePermissionAsync(new PermissionDecisionCommand(
            first.Request.Id, true, approverAgent, delegation.Id, "Use the only delegated approval."));
        Assert.Equal(PermissionRequestStatuses.Granted, approved.Request.Status);

        var second = await harness.Service.RequestPermissionAsync(Request(Guid.NewGuid(), "delegation-second", Guid.NewGuid()));
        Assert.Equal(PermissionRequestStatuses.AwaitingUser, second.Request.Status);
        var exhausted = await harness.Service.DecidePermissionAsync(new PermissionDecisionCommand(
            second.Request.Id, true, approverAgent, delegation.Id, "Cannot exceed max uses."));
        Assert.Equal("delegation_unavailable", exhausted.Decision.ReasonCode);

        var revocable = await harness.Service.CreateApprovalDelegationAsync(new CreateApprovalDelegationCommand(
            approverVersion, approverAgent, [AllowWrite], "medium", 10, 1,
            harness.Time.GetUtcNow().AddHours(1)));
        Assert.True(await harness.Service.RevokeDelegationAsync(revocable.Id, "Owner revoked delegation."));
        var third = await harness.Service.RequestPermissionAsync(Request(Guid.NewGuid(), "delegation-revoked", Guid.NewGuid()));
        var revoked = await harness.Service.DecidePermissionAsync(new PermissionDecisionCommand(
            third.Request.Id, true, approverAgent, revocable.Id, "Cannot use a revoked delegation."));
        Assert.Equal("delegation_unavailable", revoked.Decision.ReasonCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfOrDescendantApproval_IsDenied(bool descendant)
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("hard_policy", [AllowWrite])];
        var requesterAgent = Guid.NewGuid();
        var approverAgent = descendant ? Guid.NewGuid() : requesterAgent;
        var approverVersion = Guid.NewGuid();
        harness.Context.AgentVersions[approverAgent] = approverVersion;
        if (descendant) harness.Context.Descendants.Add((requesterAgent, approverAgent));
        var delegation = await harness.Service.CreateApprovalDelegationAsync(new CreateApprovalDelegationCommand(
            approverVersion, approverAgent, [AllowWrite], "medium", 10, 1,
            harness.Time.GetUtcNow().AddHours(1)));
        var pending = await harness.Service.RequestPermissionAsync(Request(
            Guid.NewGuid(), $"related-{descendant}", requesterAgent));

        var denied = await harness.Service.DecidePermissionAsync(new PermissionDecisionCommand(
            pending.Request.Id, true, approverAgent, delegation.Id, "Must not self approve."));

        Assert.Equal(PermissionRequestStatuses.Denied, denied.Request.Status);
        Assert.Equal("self_approval_forbidden", denied.Decision.ReasonCode);
    }

    [Fact]
    public async Task ExplicitUserAction_CanBeConfirmedByInitiatingHuman_AndCoreResolvesLeaseNonce()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("hard_policy", [AllowWrite])];
        var principal = harness.Tenant.Current.PrincipalId;

        var pending = await harness.Service.RequestPermissionAsync(Request(principal, "user-action-confirmation"));
        Assert.Equal(PermissionRequestStatuses.AwaitingUser, pending.Request.Status);

        var approved = await harness.Service.DecidePermissionAsync(new PermissionDecisionCommand(
            pending.Request.Id, true, null, null, "The initiating user explicitly confirmed this action."));

        Assert.Equal(PermissionRequestStatuses.Granted, approved.Request.Status);
        Assert.Equal(GovernanceOutcomes.Allowed, approved.Decision.Outcome);
        Assert.NotNull(approved.Lease);

        var consumed = await harness.Service.ConsumeToolLeaseAsync(new ToolLeaseConsumptionCommand(
            approved.Lease!.Id,
            Nonce: null,
            SubjectPrincipalId: principal,
            SubjectAgentInstanceId: null,
            Claim: WriteClaim,
            RunId: null,
            TaskId: null,
            IdempotencyKey: "user-tool-action-lease:test"));

        Assert.Equal("allowed", consumed.Status);
    }

    [Fact]
    public async Task Lease_ExpiresRevokesExhausts_AndIdempotentReplayDoesNotConsumeTwice()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("hard_policy", [AllowWrite])];
        var subject = Guid.NewGuid();
        await harness.Service.GrantCapabilityAsync(new GrantCapabilityCommand(
            subject, null, WriteClaim, null, null, harness.Time.GetUtcNow().AddHours(2), 3));
        var issued = await harness.Service.RequestPermissionAsync(Request(subject, "lease-use", duration: TimeSpan.FromHours(1), uses: 2));
        var lease = Assert.IsType<CapabilityLeaseSnapshot>(issued.Lease);

        var first = await harness.Service.TryConsumeLeaseAsync(Consume(lease, subject, "operation-1"));
        var replay = await harness.Service.TryConsumeLeaseAsync(Consume(lease, subject, "operation-1"));
        var second = await harness.Service.TryConsumeLeaseAsync(Consume(lease, subject, "operation-2"));
        var exhausted = await harness.Service.TryConsumeLeaseAsync(Consume(lease, subject, "operation-3"));
        Assert.True(first.Consumed);
        Assert.True(replay.Consumed);
        Assert.True(second.Consumed);
        Assert.False(exhausted.Consumed);
        Assert.Equal("lease_exhausted", exhausted.ReasonCode);

        var revocableSubject = Guid.NewGuid();
        var grant = await harness.Service.GrantCapabilityAsync(new GrantCapabilityCommand(
            revocableSubject, null, WriteClaim, null, null, harness.Time.GetUtcNow().AddHours(2), 1));
        var revocable = await harness.Service.RequestPermissionAsync(Request(revocableSubject, "revoked-source"));
        await harness.Service.RevokeGrantAsync(grant.Id, "Operator revoked the grant.");
        var revoked = await harness.Service.TryConsumeLeaseAsync(Consume(revocable.Lease!, revocableSubject, "revoked-operation"));
        Assert.False(revoked.Consumed);
        Assert.Equal("source_grant_revoked", revoked.ReasonCode);

        var directLeaseSubject = Guid.NewGuid();
        await harness.Service.GrantCapabilityAsync(new GrantCapabilityCommand(
            directLeaseSubject, null, WriteClaim, null, null, harness.Time.GetUtcNow().AddHours(2), 1));
        var directLease = await harness.Service.RequestPermissionAsync(Request(directLeaseSubject, "direct-lease-revoke"));
        Assert.True(await harness.Service.RevokeLeaseAsync(directLease.Lease!.Id, "Lease no longer needed."));
        var directlyRevoked = await harness.Service.TryConsumeLeaseAsync(Consume(
            directLease.Lease, directLeaseSubject, "direct-revoked-operation"));
        Assert.False(directlyRevoked.Consumed);
        Assert.Equal("lease_revoked", directlyRevoked.ReasonCode);

        var expiringSubject = Guid.NewGuid();
        await harness.Service.GrantCapabilityAsync(new GrantCapabilityCommand(
            expiringSubject, null, WriteClaim, null, null, harness.Time.GetUtcNow().AddHours(2), 1));
        var expiring = await harness.Service.RequestPermissionAsync(Request(
            expiringSubject, "expiring", duration: TimeSpan.FromMinutes(1)));
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        var expired = await harness.Service.TryConsumeLeaseAsync(Consume(expiring.Lease!, expiringSubject, "expired-operation"));
        Assert.False(expired.Consumed);
        Assert.Equal("lease_expired", expired.ReasonCode);
    }

    [Fact]
    public async Task Lease_CannotOutliveSourceGrant()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("hard_policy", [AllowWrite])];
        var subject = Guid.NewGuid();
        var grantExpiry = harness.Time.GetUtcNow().AddMinutes(5);
        await harness.Service.GrantCapabilityAsync(new GrantCapabilityCommand(
            subject, null, WriteClaim, null, null, grantExpiry, 1));

        var issued = await harness.Service.RequestPermissionAsync(Request(
            subject, "grant-ttl", duration: TimeSpan.FromHours(1)));

        Assert.NotNull(issued.Lease);
        Assert.Equal(grantExpiry, issued.Lease!.ExpiresAt);
    }

    [Fact]
    public async Task LeaseConsumption_SameIdempotencyKey_ReplaysTheDurableDecision()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("hard_policy", [AllowWrite])];
        var subject = Guid.NewGuid();
        await harness.Service.GrantCapabilityAsync(new GrantCapabilityCommand(
            subject, null, WriteClaim, null, null, harness.Time.GetUtcNow().AddHours(1), 1));
        var issued = await harness.Service.RequestPermissionAsync(Request(subject, "concurrent-lease"));
        var lease = Assert.IsType<CapabilityLeaseSnapshot>(issued.Lease);
        var command = Consume(lease, subject, "same-operation");

        var results = await Task.WhenAll(
            harness.Service.TryConsumeLeaseAsync(command),
            harness.Service.TryConsumeLeaseAsync(command));

        Assert.All(results, result => Assert.True(result.Consumed));
        Assert.Equal(results[0].Decision.Id, results[1].Decision.Id);
    }

    [Fact]
    public async Task CrossTenantGrantAndLeaseIds_AreInvisible()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("hard_policy", [AllowWrite])];
        var subject = Guid.NewGuid();
        var grant = await harness.Service.GrantCapabilityAsync(new GrantCapabilityCommand(
            subject, null, WriteClaim, null, null, harness.Time.GetUtcNow().AddHours(1), 1));
        var issued = await harness.Service.RequestPermissionAsync(Request(subject, "tenant-a"));
        var lease = issued.Lease!;

        harness.Tenant.CurrentValue = new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner");
        var consume = await harness.Service.TryConsumeLeaseAsync(Consume(lease, subject, "tenant-b-operation"));

        Assert.False(consume.Consumed);
        Assert.Equal("lease_not_found", consume.ReasonCode);
        Assert.False(await harness.Service.RevokeGrantAsync(grant.Id, "Cross-tenant attempt."));
        Assert.False(await harness.Service.RevokeLeaseAsync(lease.Id, "Cross-tenant attempt."));
    }

    private static PermissionRequestCommand Request(
        Guid subject,
        string idempotencyKey,
        Guid? agent = null,
        string risk = "low",
        decimal cost = 0,
        TimeSpan? duration = null,
        int uses = 1) => new(
            subject, agent, null, WriteClaim, null, null, duration ?? TimeSpan.FromMinutes(30),
            uses, risk, cost, "Need write access for the assigned task.", idempotencyKey);

    private static LeaseConsumptionCommand Consume(
        CapabilityLeaseSnapshot lease,
        Guid subject,
        string idempotencyKey) => new(
            lease.Id, lease.Nonce, subject, lease.SubjectAgentInstanceId, lease.Claim,
            lease.RunId, lease.TaskId, idempotencyKey);
}

internal sealed class GovernanceHarness : IAsyncDisposable
{
    private readonly string _databasePath;

    private GovernanceHarness(
        string databasePath,
        GovernanceService service,
        MutableTenantAccessor tenant,
        TestAuthorizationContextResolver context,
        MutableTimeProvider time)
    {
        _databasePath = databasePath;
        Service = service;
        Tenant = tenant;
        Context = context;
        Time = time;
    }

    public GovernanceService Service { get; }
    public MutableTenantAccessor Tenant { get; }
    public TestAuthorizationContextResolver Context { get; }
    public MutableTimeProvider Time { get; }

    public static async Task<GovernanceHarness> CreateAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tinadec-governance-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GovernanceDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var factory = new TestDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();
        var tenant = new MutableTenantAccessor
        {
            CurrentValue = new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner")
        };
        var context = new TestAuthorizationContextResolver();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero));
        return new GovernanceHarness(databasePath, new GovernanceService(factory, tenant, context, time), tenant, context, time);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestDbContextFactory(DbContextOptions<GovernanceDbContext> options)
    : IDbContextFactory<GovernanceDbContext>
{
    public GovernanceDbContext CreateDbContext() => new(options);

    public Task<GovernanceDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}

internal sealed class MutableTenantAccessor : ITenantContextAccessor
{
    public required TenantContext CurrentValue { get; set; }
    public TenantContext Current => CurrentValue;
}

internal sealed class TestAuthorizationContextResolver : IAuthorizationContextResolver
{
    public IReadOnlyList<AuthorizationBoundary> Boundaries { get; set; } = [];
    public Dictionary<Guid, Guid> AgentVersions { get; } = [];
    public HashSet<(Guid Requester, Guid Candidate)> Descendants { get; } = [];

    public Task<IReadOnlyList<AuthorizationBoundary>> ResolveBoundariesAsync(
        AuthorizationContextRequest request,
        CancellationToken cancellationToken = default) => Task.FromResult(Boundaries);

    public Task<Guid?> ResolveAgentVersionIdAsync(
        Guid tenantId,
        Guid workspaceId,
        Guid agentInstanceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(AgentVersions.TryGetValue(agentInstanceId, out var version) ? (Guid?)version : null);

    public Task<bool> IsSelfOrDescendantAsync(
        Guid tenantId,
        Guid workspaceId,
        Guid requesterAgentInstanceId,
        Guid candidateApproverAgentInstanceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(requesterAgentInstanceId == candidateApproverAgentInstanceId
            || Descendants.Contains((requesterAgentInstanceId, candidateApproverAgentInstanceId)));
}

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}
