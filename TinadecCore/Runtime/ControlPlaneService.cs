using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Models;
using TinadecCore.Persistence;
using TinadecCore.Prompts;
using TinadecCore.Tools;

namespace TinadecCore.Runtime;

public sealed class ControlPlaneService
{
    private readonly IDbContextFactory<ModelControlDbContext> _models;
    private readonly IDbContextFactory<PromptControlDbContext> _prompts;
    private readonly IDbContextFactory<AgentControlDbContext> _agents;
    private readonly IDbContextFactory<LifecycleDbContext> _lifecycle;
    private readonly IContentStore _content;
    private readonly ISecretStore _secrets;
    private readonly ITenantContextAccessor _tenant;
    private readonly IToolApprovalCoordinator _approvals;
    private readonly ILifecycleManager _runs;
    private readonly IFullDuplexRunEngine _engine;

    public ControlPlaneService(IDbContextFactory<ModelControlDbContext> models, IDbContextFactory<PromptControlDbContext> prompts,
        IDbContextFactory<AgentControlDbContext> agents, IDbContextFactory<LifecycleDbContext> lifecycle,
        IContentStore content, ISecretStore secrets, ITenantContextAccessor tenant, IToolApprovalCoordinator approvals,
        ILifecycleManager runs, IFullDuplexRunEngine engine)
    { _models = models; _prompts = prompts; _agents = agents; _lifecycle = lifecycle; _content = content; _secrets = secrets; _tenant = tenant; _approvals = approvals; _runs = runs; _engine = engine; }

    private TenantContext Tenant => _tenant.Current;
    private static async Task<(string text, ContentReference reference)> PutJsonAsync(IContentStore store, Guid tenant, Guid? workspace, string kind, object value, CancellationToken ct)
    { var text = JsonSerializer.Serialize(value); await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)); return (text, await store.PutAsync(new ContentWriteRequest(tenant, workspace, kind, "application/json", stream), ct)); }
    private static async Task<string> ReadAsync(IContentStore store, string reference, CancellationToken ct)
    { await using var stream = await store.OpenReadAsync(new ContentReference(reference, "", 0, "application/json"), ct); using var reader = new StreamReader(stream); return await reader.ReadToEndAsync(ct); }
    private static bool Matches(long revision, string? match) => string.IsNullOrWhiteSpace(match) || long.TryParse(match, out var value) && value == revision;

    public async Task<IResult> ListProviders(CancellationToken ct)
    { await using var db = await _models.CreateDbContextAsync(ct); var rows = await db.Providers.Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null).ToListAsync(ct); return Results.Ok(await Task.WhenAll(rows.Select(ToProvider))); }
    private async Task<object> ToProvider(ModelProviderRecord row)
    { await using var db = await _models.CreateDbContextAsync(); var version = await db.ProviderVersions.SingleOrDefaultAsync(x => x.Id == row.CurrentVersionId); Dictionary<string, JsonElement>? cfg = null; if (version != null) cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await ReadAsync(_content, version.ContentReference, default));
        JsonElement Value(string key) => cfg != null && cfg.TryGetValue(key, out var v) ? v : default;
        string? String(string key) => Value(key).ValueKind == JsonValueKind.String ? Value(key).GetString() : null;
        string[] Models() => Value("models").ValueKind == JsonValueKind.Array ? Value("models").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : Array.Empty<string>();
        return new { id = row.Id, driver = row.Driver, display_name = row.DisplayName, connection_kind = row.ConnectionKind, base_url = String("base_url"), model = String("model"), models = Models(), has_api_key = row.SecretReference != null && _secrets.ExistsAsync(row.SecretReference).GetAwaiter().GetResult(), binary_path = String("binary_path"), home_path = String("home_path"), server_url = String("server_url"), launch_args = String("launch_args"), capabilities = cfg != null && cfg.TryGetValue("capabilities", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(), enabled = row.Enabled, status = row.Enabled ? "configured" : "disabled", status_message = "Persisted configuration", revision = row.Revision, scope = row.Scope, created_at = row.CreatedAt, updated_at = row.UpdatedAt }; }

    public async Task<IResult> RefreshProviderModels(Guid id, CancellationToken ct)
    {
        await using var db = await _models.CreateDbContextAsync(ct);
        var row = await db.Providers.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct);
        if (row == null) return Results.NotFound();
        var version = await db.ProviderVersions.SingleOrDefaultAsync(x => x.Id == row.CurrentVersionId, ct);
        Dictionary<string, JsonElement>? cfg = null;
        if (version != null) cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await ReadAsync(_content, version.ContentReference, ct));
        var baseUrl = cfg != null && cfg.TryGetValue("base_url", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        if (string.IsNullOrWhiteSpace(baseUrl)) return Results.BadRequest(new { code = "MODEL_DISCOVERY_INVALID", message = "Provider has no base_url configured; model discovery requires an OpenAI-compatible endpoint." });
        string? apiKey = row.SecretReference != null && _secrets.ExistsAsync(row.SecretReference).GetAwaiter().GetResult() ? await _secrets.GetAsync(row.SecretReference, ct) : null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
            if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new("Bearer", apiKey);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return Results.Json(new { code = "MODEL_DISCOVERY_FAILED", message = $"Provider returned HTTP {(int)response.StatusCode} for GET /models.", status = (int)response.StatusCode }, statusCode: 502);
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
            if (!body.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return Results.Json(new { code = "MODEL_DISCOVERY_RESPONSE_INVALID", message = "Provider /models response is missing a data array." }, statusCode: 502);
            var models = data.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var modelId) && modelId.ValueKind == JsonValueKind.String ? modelId.GetString() : null)
                .Where(idValue => !string.IsNullOrWhiteSpace(idValue))
                .Select(idValue => new { id = idValue, display_name = idValue })
                .ToArray();
            return Results.Ok(new { models });
        }
        catch (OperationCanceledException) { return Results.Json(new { code = "MODEL_DISCOVERY_TIMEOUT", message = "Provider /models request timed out after 10 seconds." }, statusCode: 502); }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException)
        { return Results.Json(new { code = "MODEL_DISCOVERY_NETWORK", message = ex.Message }, statusCode: 502); }
    }

    public async Task<IResult> SaveProvider(JsonElement input, Guid? id, string? ifMatch, CancellationToken ct)
    { var now = DateTimeOffset.UtcNow; await using var db = await _models.CreateDbContextAsync(ct); var row = id.HasValue ? await db.Providers.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct) : null; if (row != null && !Matches(row.Revision, ifMatch)) return Results.StatusCode(412); if (row?.Id is null) { row = new ModelProviderRecord { Id = id ?? Guid.NewGuid(), TenantId = Tenant.TenantId, WorkspaceId = Tenant.WorkspaceId, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now, Revision = 0 }; db.Providers.Add(row); }
        row.Driver = input.TryGetProperty("driver", out var p) ? p.GetString() ?? "" : row.Driver; row.DisplayName = input.TryGetProperty("display_name", out p) ? p.GetString() ?? row.Driver : row.DisplayName; row.ConnectionKind = input.TryGetProperty("connection_kind", out p) ? p.GetString() ?? "api-key" : row.ConnectionKind; row.Scope = input.TryGetProperty("scope", out p) ? p.GetString() ?? "workspace" : row.Scope; row.Enabled = !input.TryGetProperty("enabled", out p) || p.ValueKind != JsonValueKind.False; row.UpdatedByPrincipalId = Tenant.PrincipalId; row.UpdatedAt = now;
        if (input.TryGetProperty("api_key", out p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.GetString())) { row.SecretReference ??= "provider-" + row.Id.ToString("N"); await _secrets.PutAsync(row.SecretReference, p.GetString()!, ct); } else if (input.TryGetProperty("clear_api_key", out p) && p.ValueKind == JsonValueKind.True && row.SecretReference != null) { await _secrets.DeleteAsync(row.SecretReference, ct); row.SecretReference = null; }
        var cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input.GetRawText()) ?? new(); var stored = await PutJsonAsync(_content, Tenant.TenantId, Tenant.WorkspaceId, "model-config", cfg, ct); var version = new ModelProviderVersionRecord { Id = Guid.NewGuid(), ProviderId = row.Id, Version = (int)row.Revision + 1, ContentReference = stored.reference.Value, ContentHash = stored.reference.Sha256, ContentLength = stored.reference.Length, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now }; row.Revision++; row.CurrentVersionId = version.Id; db.ProviderVersions.Add(version); await db.SaveChangesAsync(ct); return Results.Ok(await ToProvider(row)); }
    public async Task<IResult> DeleteProvider(Guid id, string? ifMatch, CancellationToken ct)
    { await using var db = await _models.CreateDbContextAsync(ct); var row = await db.Providers.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct); if (row == null) return Results.NotFound(); if (!Matches(row.Revision, ifMatch)) return Results.StatusCode(412); row.DeletedAt = DateTimeOffset.UtcNow; row.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return Results.NoContent(); }
    public async Task<IResult> ListRoutes(CancellationToken ct)
    { await using var db = await _models.CreateDbContextAsync(ct); var rows = await db.Routes.Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null).ToListAsync(ct); var output = new List<object>(); foreach (var row in rows) { var v = await db.RouteVersions.SingleAsync(x => x.Id == row.CurrentVersionId, ct); output.Add(new { purpose = row.Purpose, provider_instance_id = v.ProviderId, model = v.Model, revision = row.Revision, updated_at = row.UpdatedAt }); } return Results.Ok(output); }
    public async Task<IResult> SaveRoute(string purpose, JsonElement input, string? ifMatch, CancellationToken ct)
    { if (!input.TryGetProperty("provider_instance_id", out var provider) || !Guid.TryParse(provider.GetString(), out var providerId)) return Results.BadRequest(new { message = "provider_instance_id is required" }); await using var db = await _models.CreateDbContextAsync(ct); var row = await db.Routes.SingleOrDefaultAsync(x => x.Purpose == purpose && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct); var now = DateTimeOffset.UtcNow; if (row != null && !Matches(row.Revision, ifMatch)) return Results.StatusCode(412); if (row == null) { row = new ModelRouteRecord { Id = Guid.NewGuid(), TenantId = Tenant.TenantId, WorkspaceId = Tenant.WorkspaceId, Purpose = purpose, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now }; db.Routes.Add(row); } var version = new ModelRouteVersionRecord { Id = Guid.NewGuid(), RouteId = row.Id, Version = (int)row.Revision + 1, ProviderId = providerId, Model = input.TryGetProperty("model", out var m) ? m.GetString() : null, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now }; row.Revision++; row.CurrentVersionId = version.Id; row.UpdatedByPrincipalId = Tenant.PrincipalId; row.UpdatedAt = now; db.RouteVersions.Add(version); await db.SaveChangesAsync(ct); return Results.Ok(new { purpose = row.Purpose, provider_instance_id = version.ProviderId, model = version.Model, revision = row.Revision, updated_at = row.UpdatedAt }); }

    public async Task<IResult> ListPrompts(CancellationToken ct)
    { await using var db = await _prompts.CreateDbContextAsync(ct); var rows = await db.Fragments.Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null).ToListAsync(ct); var output = new List<object>(); foreach (var row in rows) { await using var d = await _prompts.CreateDbContextAsync(ct); var v = await d.Versions.SingleAsync(x => x.Id == row.CurrentVersionId, ct); output.Add(new { id = row.Id, key = row.Key, title = row.Title, scope = row.Scope, target_agent_id = row.TargetAgentId, category = row.Category, content = await ReadAsync(_content, v.ContentReference, ct), priority = row.Priority, enabled = row.Enabled, is_builtin = row.IsBuiltIn, revision = row.Revision, created_at = row.CreatedAt, updated_at = row.UpdatedAt }); } return Results.Ok(output); }
    public async Task<IResult> SavePrompt(JsonElement input, Guid? id, string? ifMatch, CancellationToken ct)
    { await using var db = await _prompts.CreateDbContextAsync(ct); var now = DateTimeOffset.UtcNow; var row = id.HasValue ? await db.Fragments.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct) : null; if (row?.IsBuiltIn == true) return Results.Conflict(new { message = "Built-in prompt fragments are read-only." }); if (row != null && !Matches(row.Revision, ifMatch)) return Results.StatusCode(412); if (row == null) { row = new PromptFragmentRecord { Id = id ?? Guid.NewGuid(), TenantId = Tenant.TenantId, WorkspaceId = Tenant.WorkspaceId, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now }; db.Fragments.Add(row); } string Get(string n, string fallback = "") => input.TryGetProperty(n, out var p) ? p.GetString() ?? fallback : fallback; row.Key = Get("key", row.Key); row.Title = Get("title", row.Title); row.Scope = Get("scope", row.Scope); row.Category = Get("category", row.Category); row.Priority = input.TryGetProperty("priority", out var pr) ? pr.GetInt32() : row.Priority; row.Enabled = !input.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False; if (input.TryGetProperty("target_agent_id", out var ta) && Guid.TryParse(ta.GetString(), out var aid)) row.TargetAgentId = aid; var content = Get("content"); var stored = await PutJsonAsync(_content, Tenant.TenantId, Tenant.WorkspaceId, "prompt-fragment", content, ct); var version = new PromptFragmentVersionRecord { Id = Guid.NewGuid(), FragmentId = row.Id, Version = (int)row.Revision + 1, ContentReference = stored.reference.Value, ContentHash = stored.reference.Sha256, ContentLength = stored.reference.Length, ChangeSummary = "configuration update", CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now }; row.Revision++; row.CurrentVersionId = version.Id; row.UpdatedByPrincipalId = Tenant.PrincipalId; row.UpdatedAt = now; db.Versions.Add(version); await db.SaveChangesAsync(ct); return Results.Ok(new { id = row.Id, key = row.Key, title = row.Title, scope = row.Scope, target_agent_id = row.TargetAgentId, category = row.Category, content, priority = row.Priority, enabled = row.Enabled, is_builtin = row.IsBuiltIn, revision = row.Revision, created_at = row.CreatedAt, updated_at = row.UpdatedAt }); }
    public async Task<IResult> DeletePrompt(Guid id, CancellationToken ct) { await using var db = await _prompts.CreateDbContextAsync(ct); var row = await db.Fragments.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct); if (row == null) return Results.NotFound(); if (row.IsBuiltIn) return Results.Conflict(new { message = "Built-in prompt fragments are read-only." }); row.DeletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return Results.NoContent(); }

    public async Task<IResult> ListAgents(CancellationToken ct)
    { await using var db = await _agents.CreateDbContextAsync(ct); var rows = await db.Agents.Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null).ToListAsync(ct); var result = new List<object>(); foreach (var row in rows) { await using var d = await _agents.CreateDbContextAsync(ct); var v = await d.Versions.SingleAsync(x => x.Id == row.CurrentVersionId, ct); var body = JsonSerializer.Deserialize<JsonElement>(await ReadAsync(_content, v.ContentReference, ct)); result.Add(new { id = row.Id, name = row.Name, layer = row.Layer, agent_type = row.AgentType, mode = body.TryGetProperty("mode", out var mode) ? mode.GetString() : "", description = body.TryGetProperty("description", out var desc) ? desc.GetString() : "", model_route_purpose = body.TryGetProperty("model_route_purpose", out var route) ? route.GetString() : "", allowed_tools = body.TryGetProperty("allowed_tools", out var tools) ? tools : JsonSerializer.SerializeToElement(Array.Empty<string>()), capabilities = body.TryGetProperty("capabilities", out var caps) ? caps : JsonSerializer.SerializeToElement(Array.Empty<string>()), system_prompt = body.TryGetProperty("system_prompt", out var prompt) ? prompt.GetString() : null, enabled = row.Enabled, is_built_in = row.IsBuiltIn, revision = row.Revision, updated_at = row.UpdatedAt }); } return Results.Ok(result); }
    public async Task<IResult> SaveAgent(Guid id, JsonElement input, string? ifMatch, CancellationToken ct)
    { await using var db = await _agents.CreateDbContextAsync(ct); var row = await db.Agents.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct); if (row?.IsBuiltIn == true) return Results.Conflict(new { message = "Built-in agents are read-only; clone them first." }); var now = DateTimeOffset.UtcNow; if (row != null && !Matches(row.Revision, ifMatch)) return Results.StatusCode(412); if (row == null) { row = new AgentProfileRecord { Id = id, TenantId = Tenant.TenantId, WorkspaceId = Tenant.WorkspaceId, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now }; db.Agents.Add(row); } row.Name = input.TryGetProperty("name", out var n) ? n.GetString() ?? row.Name : row.Name; row.Layer = input.TryGetProperty("layer", out var l) ? l.GetString() ?? row.Layer : row.Layer; row.AgentType = input.TryGetProperty("agent_type", out var t) ? t.GetString() ?? row.AgentType : row.AgentType; row.Enabled = !input.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False; var stored = await PutJsonAsync(_content, Tenant.TenantId, Tenant.WorkspaceId, "agent-profile", input, ct); var version = new AgentProfileVersionRecord { Id = Guid.NewGuid(), AgentId = row.Id, Version = (int)row.Revision + 1, ContentReference = stored.reference.Value, ContentHash = stored.reference.Sha256, ContentLength = stored.reference.Length, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now }; row.Revision++; row.CurrentVersionId = version.Id; row.UpdatedByPrincipalId = Tenant.PrincipalId; row.UpdatedAt = now; db.Versions.Add(version); await db.SaveChangesAsync(ct); return Results.Ok(new { id = row.Id, name = row.Name, layer = row.Layer, agent_type = row.AgentType, mode = input.TryGetProperty("mode", out var mode) ? mode.GetString() : "", description = input.TryGetProperty("description", out var description) ? description.GetString() : "", model_route_purpose = input.TryGetProperty("model_route_purpose", out var route) ? route.GetString() : "", allowed_tools = input.TryGetProperty("allowed_tools", out var tools) ? tools : JsonSerializer.SerializeToElement(Array.Empty<string>()), capabilities = input.TryGetProperty("capabilities", out var capabilities) ? capabilities : JsonSerializer.SerializeToElement(Array.Empty<string>()), system_prompt = input.TryGetProperty("system_prompt", out var prompt) ? prompt.GetString() : null, enabled = row.Enabled, is_built_in = row.IsBuiltIn, revision = row.Revision, updated_at = row.UpdatedAt }); }

    public async Task<IResult> ListApprovals(string? status, string? sessionId, string? runId, CancellationToken ct)
    {
        Guid? session = null;
        Guid? run = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            if (!Guid.TryParse(sessionId, out var parsedSession)) return Results.BadRequest(new { message = "session_id must be a valid Guid." });
            session = parsedSession;
        }
        if (!string.IsNullOrWhiteSpace(runId))
        {
            if (!Guid.TryParse(runId, out var parsedRun)) return Results.BadRequest(new { message = "run_id must be a valid Guid." });
            run = parsedRun;
        }
        await using var db = await _lifecycle.CreateDbContextAsync(ct);
        var q = db.ApprovalRequests.Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        if (session is { } sessionIdValue) q = q.Where(x => x.SessionId == sessionIdValue);
        if (run is { } runIdValue) q = q.Where(x => x.RunId == runIdValue);
        var rows = await q.ToListAsync(ct);
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return Results.Ok(rows.Select(ToResponse));
    }
    public async Task<IResult> GetApproval(Guid id, CancellationToken ct)
    { await using var db = await _lifecycle.CreateDbContextAsync(ct); var row = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct); return row is null ? Results.NotFound() : Results.Ok(ToResponse(row)); }
    public async Task<IResult> CreateApproval(ApprovalCreateRequestDto input, CancellationToken ct)
    {
        var parametersJson = input.Parameters is { } parameters ? parameters.GetRawText() : "{}";
        var requestHash = ToolParametersHash.Compute(parametersJson);
        var now = DateTimeOffset.UtcNow;
        await using var db = await _lifecycle.CreateDbContextAsync(ct);
        var row = new ApprovalRequestRecord
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant.TenantId,
            WorkspaceId = Tenant.WorkspaceId,
            SessionId = Guid.TryParse(input.SessionId, out var sessionId) ? sessionId : null,
            RunId = Guid.TryParse(input.RunId, out var runId) ? runId : null,
            TaskId = Guid.TryParse(input.TaskId, out var taskId) ? taskId : null,
            AgentInstanceId = Guid.TryParse(input.AgentInstanceId, out var agentId) ? agentId : null,
            Kind = string.IsNullOrWhiteSpace(input.Kind) ? "tool" : input.Kind,
            ToolId = input.ToolId ?? string.Empty,
            Risk = "medium",
            ParametersReference = string.Empty,
            Summary = input.Summary ?? string.Empty,
            RequestHash = requestHash,
            Status = "pending",
            ExpiresAt = now.AddMinutes(30),
            RequestedByPrincipalId = Tenant.PrincipalId,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ApprovalRequests.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(row));
    }
    public async Task<IResult> DecideApproval(Guid id, ApprovalDecisionRequestDto input, CancellationToken ct)
    {
        ToolApprovalDecision decision;
        try { decision = await _approvals.DecideAsync(id, input.Decision, input.Reason, ct); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
        if (decision.RunId is { } runId)
        {
            await _runs.AppendEventAsync(runId, "approval.decided", new { approval_id = id, execution_id = decision.ExecutionId, task_id = decision.TaskId, decision = decision.Status }, $"Approval {decision.Status}.", decision.Status == "approved" ? "info" : "warning", decision.TaskId, id, cancellationToken: ct);
            await _engine.EnqueueAsync(runId, ct);
        }
        return Results.Ok(new { id = decision.ApprovalId, status = decision.Status, decided_at = decision.DecidedAt });
    }

    private static ApprovalResponseDto ToResponse(ApprovalRequestRecord row) => new()
    { Id = row.Id, ProjectId = row.ProjectId, SessionId = row.SessionId, RunId = row.RunId, TaskId = row.TaskId, AgentInstanceId = row.AgentInstanceId, ExecutionId = row.ExecutionId, Kind = row.Kind, ToolId = row.ToolId, Risk = row.Risk, Summary = row.Summary, Status = row.Status, RequestHash = row.RequestHash, ConsumedByExecutionId = row.ConsumedByExecutionId, Decision = row.Decision, DecisionReason = row.DecisionReason, DecidedAt = row.DecidedAt, ConsumedAt = row.ConsumedAt, ExpiresAt = row.ExpiresAt, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt };
}
