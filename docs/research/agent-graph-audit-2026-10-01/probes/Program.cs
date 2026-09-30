using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Api.Tests;
using TinadecCore.DmaEA;
using TinadecCore.Runtime;
using TinadecCore.TinaChat;

var runner = new ProbeRunner();
var frozenConfig = JsonSerializer.Deserialize<FrozenRunConfigurationV1>("""
{"operationAgents":[{"id":"meeting","layer":"operation","role":"conversation","lifecycle":"long_lived","capabilities":[],"directUserOutput":true,"contextAccess":"read"}],"executionAgents":[],"bindings":[]}
""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
var template = new FrozenSpawnableTemplate("audit-worker", Guid.NewGuid(), Guid.NewGuid(), "hash", "engineering", [], [])
{
    Description = "AUDIT-DUTY-SENTINEL"
};
var engineType = typeof(FrozenRunConfigurationV1).Assembly.GetType("TinadecCore.DmaEA.FullDuplexRunEngine")!;
var materialize = engineType.GetMethod("SpawnableDefinition", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
var definition = (RuntimeAgentDefinition)materialize.Invoke(null, [template, frozenConfig])!;
Output("spawnable_definition", new { system_prompt_empty = string.IsNullOrEmpty(definition.SystemPrompt), definition.Description,
    template_can_carry_system_prompt = typeof(FrozenSpawnableTemplate).GetProperty("SystemPrompt") is not null });
using var source = new ApiEndpointFactory();
using var host = source.WithWebHostBuilder(builder =>
{
    builder.UseContentRoot(Path.GetFullPath("TinadecCore/Api"));
    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["TinadecTinaChat:WakeDrainEnabled"] = "false",
        ["TinadecApprovalGates:Enabled"] = "false",
        ["Logging:LogLevel:Default"] = "Error",
    }));
    builder.ConfigureServices(services => services.AddSingleton<ITinaChatMemberTurnRunner>(runner));
});
using var client = host.CreateClient();
var create = await client.PostAsJsonAsync("/api/v1/sessions", new { title = "isolated architecture audit" });
create.EnsureSuccessStatusCode();
var session = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
var tenant = host.Services.GetRequiredService<ITenantContextAccessor>().Current;
var scope = new OrganizationScope(tenant.TenantId, tenant.WorkspaceId, tenant.PrincipalId, session);
var organization = host.Services.GetRequiredService<ISessionOrganization>();
var ledger = host.Services.GetRequiredService<IResourceLeaseService>();
var dbFactory = host.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>();
var drain = host.Services.GetRequiredService<TinaChatWakeService>();
var runA = Guid.NewGuid();
var runB = Guid.NewGuid();
var taskA = Guid.NewGuid();
var taskB = Guid.NewGuid();
var root = Path.Combine(Path.GetTempPath(), "tinadec-audit", session.ToString("N"));
var claim = new ResourceClaim(ResourceLeaseKinds.Path, Path.Combine(root, "same.ts"), true);
var first = await ledger.AcquireAsync(new ResourceAcquireRequest(claim, session, runA, taskA, null, "audit A", ResourceLeasePurposes.WriteScope));
var second = await ledger.AcquireAsync(new ResourceAcquireRequest(claim, session, runA, taskB, null, "audit B", ResourceLeasePurposes.WriteScope));
Output("same_run_overlapping_write_scope", new { first_granted = first.Granted, second_granted = second.Granted });
await ledger.AcquireAsync(new ResourceAcquireRequest(new ResourceClaim(ResourceLeaseKinds.Path, Path.Combine(root, "other-run.ts"), true), session, runB, taskB, null, "audit other run"));
var topology = await host.Services.GetRequiredService<ISessionTopology>().GetAsync(session, new SessionTopologyQuery(RunId: runA));
Output("run_filtered_topology", new { leaked_other_run_leases = topology!.Leases.Count(lease => lease.RunId == runB) });

await organization.NotifyAsync(new OrganizationNotice(scope, runA, GovernanceTopics.LeaseConflict, "audit-reviewer", "audit notice that must survive a model fault"));
await drain.RunPassAsync(20);
long failedWakeId;
await using (var db = await dbFactory.CreateDbContextAsync())
{
    var wake = await db.Wakes.SingleAsync();
    failedWakeId = wake.Id;
    Output("wake_after_transient_model_failure", new { wake.Status, source_count = JsonSerializer.Deserialize<Guid[]>(wake.SourceMessageIdsJson)!.Length, runner.Calls });
    wake.DueAtUnixMs = 0;
    await db.SaveChangesAsync();
}
runner.Fail = false;
await drain.RunPassAsync(20);
await using (var db = await dbFactory.CreateDbContextAsync())
{
    var wake = await db.Wakes.SingleAsync(row => row.Id == failedWakeId);
    Output("wake_after_retry", new { wake.Status, runner.Calls, runner.Briefings });
}

await organization.NotifyAsync(new OrganizationNotice(scope, runA, GovernanceTopics.TaskClosed, "audit-reviewer", "audit notice whose owner crashed"));
await using (var db = await dbFactory.CreateDbContextAsync())
{
    var wake = await db.Wakes.SingleAsync(row => row.Status == "pending");
    wake.Status = "running";
    wake.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
    wake.DueAtUnixMs = 0;
    await db.SaveChangesAsync();
}
await drain.RunPassAsync(20);
await using (var db = await dbFactory.CreateDbContextAsync())
    Output("orphaned_running_wake", new { running_rows = await db.Wakes.CountAsync(row => row.Status == "running"), runner.Calls });

static void Output(string probe, object result) => Console.WriteLine("AUDIT " + JsonSerializer.Serialize(new { probe, result }));

sealed class ProbeRunner : ITinaChatMemberTurnRunner
{
    public bool Fail { get; set; } = true;
    public int Calls { get; private set; }
    public List<string> Briefings { get; } = [];

    public Task<TinaChatMemberTurnOutcome> RunAsync(TinaChatMemberTurn turn, ITinaChatMemberToolbox toolbox, CancellationToken cancellationToken = default)
    {
        Calls++;
        Briefings.Add(turn.Briefing);
        if (Fail) throw new InvalidOperationException("deliberately injected transient model fault");
        return Task.FromResult(new TinaChatMemberTurnOutcome(1, 0, "nothing to report", []));
    }
}
