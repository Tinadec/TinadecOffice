using System.Reflection;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.AgentGraph;
using TinadecCore.DmaEA;
using TinadecCore.Governance;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.TinaChat;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Runtime;

public sealed record SessionEntityRows(Type ContextType, Type EntityType, IReadOnlyList<object> Rows);
public sealed record SessionDataGraph(Guid SessionId, IReadOnlyList<Guid> RunIds, IReadOnlyList<SessionEntityRows> Entities, IReadOnlyList<string> ContentReferences);

/// <summary>Host composition of session-owned rows. Shared identities, current configuration and shared memory are not purged.</summary>
public static class ScopeSessionDataGraph
{
    private static readonly Type[] Contexts = [typeof(MemoryDbContext), typeof(LifecycleDbContext), typeof(AgentControlDbContext),
        typeof(AgentGraphDbContext), typeof(GovernanceDbContext), typeof(TinaChatDbContext)];
    private static readonly HashSet<string> SharedEntities = [nameof(ProjectRecord), nameof(MemoryItemRecord), nameof(MemoryVersionRecord),
        nameof(PolicyBundleRecord), nameof(PolicyVersionRecord), nameof(EnvironmentRecord), nameof(ChatWorkspacePolicy)];
    private static readonly HashSet<string> Relationships = ["SessionId", "RunId", "SourceRunId", "ParentRunId", "MessageId", "TurnId", "ApprovalId",
        "ApprovalRequestId", "UserToolActionId", "ToolExecutionId", "AgentInstanceId", "InstanceId", "TaskId", "ApprovalGateId",
        "ConversationId", "OrganizationId", "IntentId", "ExecutionId", "LeaseId", "DelegationId", "PermissionRequestId", "DecisionId",
        "MemberId", "ContactId", "ReportId", "CandidateId", "AggregateId", "CurrentRunId", "SourceSessionId", "TargetRunId", "SubjectAgentInstanceId", "GrantId", "ParentGrantId", "SourceDelegationId", "TargetId",
        "CapabilityGrantId", "CapabilityLeaseId", "AuthorizationDecisionId", "ApprovalDelegationId", "DelegateAgentInstanceId"];

    public static async Task WriteSnapshotAsync(string path, SessionDataGraph graph, CancellationToken ct)
    {
        var model = new Tomlyn.Model.TomlTable
        {
            ["schema_version"] = 1, ["session_id"] = graph.SessionId.ToString("D"),
            ["run_ids"] = new Tomlyn.Model.TomlArray(), ["content_references"] = new Tomlyn.Model.TomlArray(),
            ["entities"] = new Tomlyn.Model.TomlTableArray()
        };
        foreach (var id in graph.RunIds) ((Tomlyn.Model.TomlArray)model["run_ids"]).Add(id.ToString("D"));
        foreach (var reference in graph.ContentReferences) ((Tomlyn.Model.TomlArray)model["content_references"]).Add(reference);
        foreach (var set in graph.Entities)
            ((Tomlyn.Model.TomlTableArray)model["entities"]).Add(new Tomlyn.Model.TomlTable
            {
                ["context_type"] = set.ContextType.FullName!, ["entity_type"] = set.EntityType.FullName!,
                ["rows"] = JsonSerializer.Serialize(set.Rows)
            });
        await StorageScopeRegistry.AtomicWriteAsync(path, Tomlyn.TomlSerializer.Serialize(model), ct).ConfigureAwait(false);
    }

    public static async Task<SessionDataGraph> ReadSnapshotAsync(IServiceProvider services, string path, CancellationToken ct)
    {
        var model = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))!;
        if (Convert.ToInt64(StorageScopeInitializer.Value(model, "schema_version")) != 1)
            throw new InvalidDataException("Unsupported session purge journal schema: " + path);
        var sets = new List<SessionEntityRows>();
        foreach (var set in (Tomlyn.Model.TomlTableArray)model["entities"])
        {
            var context = Contexts.SingleOrDefault(type => type.FullName == (string)set["context_type"])
                ?? throw new InvalidDataException("Unexpected session purge participant: " + path);
            await using var db = await OpenAsync(services, context, ct).ConfigureAwait(false);
            var entity = db.Model.GetEntityTypes().Select(type => type.ClrType).SingleOrDefault(type => type.FullName == (string)set["entity_type"]
                && !SharedEntities.Contains(type.Name)) ?? throw new InvalidDataException("Unexpected session purge record: " + path);
            var rows = (System.Collections.IEnumerable)JsonSerializer.Deserialize((string)set["rows"], typeof(List<>).MakeGenericType(entity))!;
            sets.Add(new(context, entity, rows.Cast<object>().ToArray()));
        }
        return new(Guid.Parse((string)model["session_id"]), ((Tomlyn.Model.TomlArray)model["run_ids"]).Select(id => Guid.Parse((string)id!)).ToArray(),
            sets, ((Tomlyn.Model.TomlArray)model["content_references"]).Cast<string>().ToArray());
    }

    public static async Task<SessionDataGraph> ReadAsync(IServiceProvider services, Guid sessionId, CancellationToken ct = default)
    {
        var known = new HashSet<Guid> { sessionId };
        var all = new Dictionary<(Type Context, Type Entity), Dictionary<string, object>>();
        var tenant = services.GetRequiredService<ITenantContextAccessor>().Current;
        bool changed;
        do
        {
            changed = false;
            foreach (var contextType in Contexts)
            {
                await using var db = await OpenAsync(services, contextType, ct).ConfigureAwait(false);
                foreach (var entity in db.Model.GetEntityTypes().Where(x => !SharedEntities.Contains(x.ClrType.Name)))
                {
                    var task = (Task<List<object>>)typeof(ScopeSessionDataGraph).GetMethod(nameof(ReadCandidatesAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(entity.ClrType).Invoke(null, [db, known.ToArray(), sessionId, tenant,
                            all.Values.SelectMany(x => x.Values).OfType<ChatConversation>().Select(x => x.Id).ToArray(), ct])!;
                    var key = (contextType, entity.ClrType);
                    if (!all.TryGetValue(key, out var rows)) all[key] = rows = new();
                    foreach (var row in await task.ConfigureAwait(false))
                    {
                        var identity = JsonSerializer.Serialize(entity.FindPrimaryKey()!.Properties.Select(p => p.PropertyInfo!.GetValue(row)));
                        if (!rows.TryAdd(identity, row)) continue;
                        changed = true;
                        // A referenced parent is not an owned child. Expanding every foreign
                        // key would walk into shared grants and unrelated sessions/runs.
                        foreach (var property in row.GetType().GetProperties().Where(p => p.Name == "Id"))
                            if (property.GetValue(row) is Guid id) known.Add(id);
                    }
                }
            }
        } while (changed);
        var sets = all.Where(x => x.Value.Count > 0).Select(x => new SessionEntityRows(x.Key.Context, x.Key.Entity, x.Value.Values.ToArray())).ToArray();
        var references = sets.SelectMany(x => x.Rows).SelectMany(row => row.GetType().GetProperties().Where(p => p.PropertyType == typeof(string))
            .Select(p => p.GetValue(row) as string).Where(x => x is not null).SelectMany(FindContentReferences)).Distinct(StringComparer.Ordinal).ToArray();
        return new(sessionId, sets.SelectMany(x => x.Rows).OfType<RunRecord>().Select(x => x.Id).ToArray(), sets, references);
    }

    public static async Task CopyToAsync(IServiceProvider target, SessionDataGraph graph, Guid projectId, CancellationToken ct = default)
    {
        foreach (var group in graph.Entities.GroupBy(x => x.ContextType))
        {
            // One group is one transaction. The copy skips rows that already match, so re-running
            // a group after a rolled-back attempt adds nothing twice.
            await Persistence.SqliteTransientRetry.WriteInTransactionAsync(async ct =>
            {
                await using var db = await OpenAsync(target, group.Key, ct).ConfigureAwait(false);
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                foreach (var set in group)
                    foreach (var source in set.Rows)
                    {
                        var row = JsonSerializer.Deserialize(JsonSerializer.Serialize(source, set.EntityType), set.EntityType)!;
                        var key = db.Model.FindEntityType(set.EntityType)!.FindPrimaryKey()!;
                        var values = key.Properties.Select(p => p.PropertyInfo!.GetValue(row)).ToArray();
                        if (row.GetType().GetProperty("ProjectId") is { CanWrite: true } project && (project.PropertyType == typeof(Guid?) || project.PropertyType == typeof(Guid)))
                            project.SetValue(row, projectId);
                        if (key.Properties.Count == 1 && key.Properties[0].ClrType == typeof(long)
                            && key.Properties[0].ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd)
                        {
                            var task = (Task<List<object>>)typeof(ScopeSessionDataGraph).GetMethod(nameof(ReadRowsAsync), BindingFlags.Static | BindingFlags.NonPublic)!
                                .MakeGenericMethod(set.EntityType).Invoke(null, [db, ct])!;
                            var generated = key.Properties[0].PropertyInfo!;
                            generated.SetValue(row, 0L);
                            var same = false;
                            foreach (var existing in await task.ConfigureAwait(false))
                            {
                                generated.SetValue(existing, 0L);
                                if (JsonSerializer.Serialize(existing, set.EntityType) == JsonSerializer.Serialize(row, set.EntityType)) { same = true; break; }
                            }
                            if (same) continue;
                        }
                        else if (await db.FindAsync(set.EntityType, values, ct).ConfigureAwait(false) is { } existing)
                        {
                            if (JsonSerializer.Serialize(existing, set.EntityType) != JsonSerializer.Serialize(row, set.EntityType))
                                throw new InvalidOperationException("The destination already contains different facts with the same identity: " + set.EntityType.Name);
                            continue;
                        }
                        db.Add(row);
                    }
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
    }

    public static async Task DeleteAsync(IServiceProvider source, SessionDataGraph graph, CancellationToken ct = default)
    {
        // Dependencies go before Memory session; a retry is safe after a partially completed module transaction.
        foreach (var group in graph.Entities.GroupBy(x => x.ContextType).OrderBy(x => x.Key == typeof(MemoryDbContext) ? 1 : 0))
        {
            await Persistence.SqliteTransientRetry.WriteInTransactionAsync(async ct =>
            {
                await using var db = await OpenAsync(source, group.Key, ct).ConfigureAwait(false);
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                foreach (var set in group)
                    foreach (var row in set.Rows)
                    {
                        var key = db.Model.FindEntityType(set.EntityType)!.FindPrimaryKey()!;
                        var values = key.Properties.Select(p => p.PropertyInfo!.GetValue(row)).ToArray();
                        if (await db.FindAsync(set.EntityType, values, ct).ConfigureAwait(false) is { } existing) db.Remove(existing);
                    }
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
    }

    public static IEnumerable<string> FindContentReferences(string? value) => value is null ? [] : Regex.Matches(value,
        @"content/tenants/[a-fA-F0-9]{32}/(?:[a-fA-F0-9]{32}|tenant)/[A-Za-z0-9_-]+/[a-fA-F0-9]{64}").Select(x => x.Value);

    public static async Task<IReadOnlyList<string>> ReadContentReferencesAsync(IServiceProvider services, CancellationToken ct)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var types = Contexts.Concat([typeof(TinadecCore.AgentConfiguration.AgentConfigurationDbContext), typeof(TinadecCore.Models.ModelControlDbContext),
            typeof(TinadecCore.Prompts.PromptControlDbContext), typeof(TinadecCore.Skills.IntegrationDbContext), typeof(TinadecCore.Tools.ToolsSettingsDbContext)]);
        foreach (var type in types)
        {
            await using var db = await OpenAsync(services, type, ct).ConfigureAwait(false);
            foreach (var entity in db.Model.GetEntityTypes())
            {
                if (!entity.GetProperties().Any(x => x.ClrType == typeof(string))) continue;
                var task = (Task<List<object>>)typeof(ScopeSessionDataGraph).GetMethod(nameof(ReadRowsAsync), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(entity.ClrType).Invoke(null, [db, ct])!;
                foreach (var row in await task.ConfigureAwait(false))
                    foreach (var property in row.GetType().GetProperties().Where(x => x.PropertyType == typeof(string)))
                        foreach (var reference in FindContentReferences(property.GetValue(row) as string)) references.Add(reference);
            }
        }
        return references.ToArray();
    }
    private static async Task<List<object>> ReadRowsAsync<T>(DbContext db, CancellationToken ct) where T : class =>
        (await db.Set<T>().AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).Cast<object>().ToList();
    private static async Task<List<object>> ReadCandidatesAsync<T>(DbContext db, Guid[] ids, Guid sessionId, TenantContext tenant, Guid[] ownedConversations, CancellationToken ct) where T : class
    {
        var parameter = Expression.Parameter(typeof(T), "row");
        Expression predicate = Expression.Constant(false);
        foreach (var property in typeof(T).GetProperties().Where(p => p.Name == "Id" || Relationships.Contains(p.Name)))
        {
            if (property.PropertyType != typeof(Guid) && property.PropertyType != typeof(Guid?)) continue;
            Expression value = Expression.Property(parameter, property); Expression present = Expression.Constant(true);
            if (property.PropertyType == typeof(Guid?)) { present = Expression.Property(value, "HasValue"); value = Expression.Property(value, "Value"); }
            predicate = Expression.OrElse(predicate, Expression.AndAlso(present, Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)], Expression.Constant(ids), value)));
        }
        foreach (var (name, value) in new[] { ("TenantId", tenant.TenantId), ("WorkspaceId", tenant.WorkspaceId), ("SessionId", sessionId) })
        {
            if (typeof(T).GetProperty(name) is not { } property) continue;
            if (property.PropertyType != typeof(Guid) && property.PropertyType != typeof(Guid?)) continue;
            var member = Expression.Property(parameter, property);
            Expression expected = Expression.Constant(value); if (property.PropertyType == typeof(Guid?)) expected = Expression.Convert(expected, typeof(Guid?));
            Expression constraint = Expression.Equal(member, expected);
            if (property.PropertyType == typeof(Guid?) && name == "SessionId") constraint = Expression.OrElse(Expression.Equal(member, Expression.Constant(null, typeof(Guid?))), constraint);
            predicate = Expression.AndAlso(predicate, constraint);
        }
        if (typeof(T).GetProperty("RunId") is { PropertyType: var runType } && runType == typeof(Guid?))
        {
            var run = Expression.Property(parameter, "RunId");
            predicate = Expression.AndAlso(predicate, Expression.OrElse(Expression.Not(Expression.Property(run, "HasValue")),
                Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)], Expression.Constant(ids), Expression.Property(run, "Value"))));
        }
        if (typeof(T) == typeof(CapabilityGrantRecord) || typeof(T) == typeof(ApprovalDelegationRecord)
            || typeof(T) == typeof(CapabilityLeaseRecord))
        {
            Expression owned = Expression.Constant(false);
            foreach (var name in new[] { "RunId", "TaskId", "SubjectAgentInstanceId", "DelegateAgentInstanceId" })
            {
                if (typeof(T).GetProperty(name) is not { } property) continue;
                Expression member = Expression.Property(parameter, property);
                Expression present = Expression.Constant(true);
                if (property.PropertyType == typeof(Guid?)) { present = Expression.Property(member, "HasValue"); member = Expression.Property(member, "Value"); }
                owned = Expression.OrElse(owned, Expression.AndAlso(present, Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains),
                    [typeof(Guid)], Expression.Constant(ids), member)));
            }
            predicate = Expression.AndAlso(predicate, owned);
        }
        if (typeof(T) == typeof(ApprovalRuleRecord))
            predicate = Expression.AndAlso(predicate, Expression.Equal(Expression.Property(parameter, "SessionId"), Expression.Constant((Guid?)sessionId, typeof(Guid?))));
        if (typeof(T) == typeof(ChatParticipant) || typeof(T) == typeof(ChatConversation))
        {
            var organization = Expression.Property(parameter, "OrganizationId");
            predicate = Expression.AndAlso(predicate, Expression.AndAlso(Expression.Property(organization, "HasValue"),
                Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)], Expression.Constant(ids), Expression.Property(organization, "Value"))));
        }
        if (typeof(T).Namespace == typeof(ChatConversation).Namespace && typeof(T).GetProperty("ConversationId") is { PropertyType: var conversationType }
            && conversationType == typeof(Guid) && typeof(T).GetProperty("SessionId") is null)
        {
            predicate = Expression.AndAlso(predicate, Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)],
                Expression.Constant(ownedConversations), Expression.Property(parameter, "ConversationId")));
        }
        return (await db.Set<T>().AsNoTracking().Where(Expression.Lambda<Func<T, bool>>(predicate, parameter)).ToListAsync(ct).ConfigureAwait(false)).Cast<object>().ToList();
    }
    private static Task<DbContext> OpenAsync(IServiceProvider provider, Type context, CancellationToken ct) =>
        (Task<DbContext>)typeof(ScopeSessionDataGraph).GetMethod(nameof(OpenTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(context).Invoke(null, [provider, ct])!;
    private static async Task<DbContext> OpenTypedAsync<T>(IServiceProvider provider, CancellationToken ct) where T : DbContext =>
        await provider.GetRequiredService<IDbContextFactory<T>>().CreateDbContextAsync(ct).ConfigureAwait(false);
}
