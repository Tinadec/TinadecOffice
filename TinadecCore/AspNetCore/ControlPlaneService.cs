using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.DmaEA.CliRuntime;
using TinadecCore.Lifecycle;
using TinadecCore.Models;
using TinadecCore.Persistence;
using TinadecCore.Prompts;
using TinadecCore.Tools;

namespace TinadecCore.Runtime;

public sealed record PreAuthorizationRequestDto(
    Guid RunId,
    string? LaneKey,
    IReadOnlyList<string>? ToolScope,
    string? ParameterConstraintHash,
    string? RiskMax,
    int MaxUses,
    DateTimeOffset? ExpiresAt,
    string? Summary);

public sealed class ControlPlaneService
{
    private readonly IDbContextFactory<ModelControlDbContext> _models;
    private readonly IDbContextFactory<PromptControlDbContext> _prompts;
    private readonly IDbContextFactory<LifecycleDbContext> _lifecycle;
    private readonly IContentStore _content;
    private readonly ISecretStore _secrets;
    private readonly ITenantContextAccessor _tenant;
    private readonly IToolApprovalCoordinator _approvals;
    private readonly IToolExecutionCoordinator _executions;
    private readonly IUserToolActionService _userActions;
    private readonly IAuthorizationService _authorization;
    private readonly ILifecycleManager _runs;
    private readonly IFullDuplexRunEngine _engine;
    private readonly ICliProcessManager _cli;

    public ControlPlaneService(IDbContextFactory<ModelControlDbContext> models, IDbContextFactory<PromptControlDbContext> prompts,
        IDbContextFactory<LifecycleDbContext> lifecycle,
        IContentStore content, ISecretStore secrets, ITenantContextAccessor tenant, IToolApprovalCoordinator approvals,
        IAuthorizationService authorization, IToolExecutionCoordinator executions,
        ILifecycleManager runs, IFullDuplexRunEngine engine, ICliProcessManager cli,
        IUserToolActionService userActions)
    { _models = models; _prompts = prompts; _lifecycle = lifecycle; _content = content; _secrets = secrets; _tenant = tenant; _approvals = approvals; _authorization = authorization; _executions = executions; _userActions = userActions; _runs = runs; _engine = engine; _cli = cli; }

    private TenantContext Tenant => _tenant.Current;
    private static async Task<(string text, ContentReference reference)> PutJsonAsync(IContentStore store, Guid tenant, Guid? workspace, string kind, object value, CancellationToken ct)
    { var text = JsonSerializer.Serialize(value); await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)); return (text, await store.PutAsync(new ContentWriteRequest(tenant, workspace, kind, "application/json", stream), ct)); }
    private static async Task<string> ReadAsync(IContentStore store, string reference, CancellationToken ct)
    { await using var stream = await store.OpenReadAsync(new ContentReference(reference, "", 0, "application/json"), ct); using var reader = new StreamReader(stream); return await reader.ReadToEndAsync(ct); }
    private static bool Matches(long revision, string? match) => string.IsNullOrWhiteSpace(match) || long.TryParse(match, out var value) && value == revision;

    /// <summary>
    /// Conditional-update gate for mutable control-plane rows (providers, routes).
    /// A missing If-Match is a hard 428 (blind writes were the root cause of live
    /// configuration being overwritten by verification scripts); a stale revision is 412.
    /// </summary>
    private static IResult? CheckPrecondition(long revision, string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch)) return Results.Json(new { code = "precondition_required", message = "If-Match header with the current numeric revision is required for this operation." }, statusCode: 428);
        var normalized = ifMatch.Trim().Trim('"');
        if (!long.TryParse(normalized, out var value)) return Results.Json(new { code = "invalid_if_match", message = "If-Match must contain the numeric revision." }, statusCode: 400);
        return value != revision ? Results.StatusCode(412) : null;
    }

    /// <summary>Routes whose saved versions still reference the provider, for the provider_in_use guard.</summary>
    private static async Task<List<string>> ReferencingRoutePurposesAsync(ModelControlDbContext db, Guid providerId, CancellationToken ct)
    {
        var purposes = await (
            from candidate in db.RouteCandidates.AsNoTracking()
            join version in db.RouteVersions.AsNoTracking() on candidate.RouteVersionId equals version.Id
            join route in db.Routes.AsNoTracking() on version.RouteId equals route.Id
            where candidate.ProviderInstanceId == providerId && route.DeletedAt == null
            select route.Purpose).Distinct().ToListAsync(ct);
        return purposes;
    }

    private static IResult ProviderInUse(IEnumerable<string> purposes) => Results.Json(
        new { code = "provider_in_use", message = $"Provider is still referenced by model route(s): {string.Join(", ", purposes)}. Rebind or delete the route(s) first, or retry with force=true.", routes = purposes.ToArray() },
        statusCode: 409);

    /// <summary>
    /// Merges the incoming provider payload onto the persisted current-version config so a
    /// partial update (e.g. only the model list) keeps unspecified fields such as protocol,
    /// base_url, binary_path and launch_args. Request body wins; missing keys keep the old
    /// value; an explicit JSON null clears the key. Secret fields never enter the blob.
    /// </summary>
    private static Dictionary<string, JsonElement> MergeProviderConfig(Dictionary<string, JsonElement> current, JsonElement input)
    {
        var merged = new Dictionary<string, JsonElement>(current);
        foreach (var property in input.EnumerateObject())
        {
            if (property.Name is "api_key" or "clear_api_key") continue;
            if (property.Value.ValueKind == JsonValueKind.Null) merged.Remove(property.Name);
            else merged[property.Name] = property.Value.Clone();
        }
        return merged;
    }

    public async Task<IResult> ListProviders(CancellationToken ct)
    { await using var db = await _models.CreateDbContextAsync(ct); var rows = await db.Providers.Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null).ToListAsync(ct); return Results.Ok(await Task.WhenAll(rows.Select(ToProvider))); }
    private async Task<object> ToProvider(ModelProviderRecord row)
    { await using var db = await _models.CreateDbContextAsync(); var version = await db.ProviderVersions.SingleOrDefaultAsync(x => x.Id == row.CurrentVersionId); Dictionary<string, JsonElement>? cfg = null; if (version != null) cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await ReadAsync(_content, version.ContentReference, default));
        JsonElement Value(string key) => cfg != null && cfg.TryGetValue(key, out var v) ? v : default;
        string? String(string key) => Value(key).ValueKind == JsonValueKind.String ? Value(key).GetString() : null;
        string[] Models() => Value("models").ValueKind == JsonValueKind.Array ? Value("models").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : Array.Empty<string>();
        return new { id = row.Id, driver = row.Driver, protocol = ChatProtocols.Normalize(String("protocol") ?? ChatProtocols.InferFromDriver(row.Driver)), display_name = row.DisplayName, connection_kind = row.ConnectionKind, base_url = String("base_url"), model = String("model"), models = Models(), has_api_key = row.SecretReference != null && _secrets.ExistsAsync(row.SecretReference).GetAwaiter().GetResult(), binary_path = String("binary_path"), home_path = String("home_path"), server_url = String("server_url"), launch_args = String("launch_args"), capabilities = cfg != null && cfg.TryGetValue("capabilities", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(), enabled = row.Enabled, status = row.Enabled ? "configured" : "disabled", status_message = "Persisted configuration", revision = row.Revision, scope = row.Scope, created_at = row.CreatedAt, updated_at = row.UpdatedAt }; }

    public async Task<IResult> RefreshProviderModels(Guid id, CancellationToken ct)
    {
        await using var db = await _models.CreateDbContextAsync(ct);
        var row = await db.Providers.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct);
        if (row == null) return Results.NotFound();
        var version = await db.ProviderVersions.SingleOrDefaultAsync(x => x.Id == row.CurrentVersionId, ct);
        Dictionary<string, JsonElement>? cfg = null;
        if (version != null) cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await ReadAsync(_content, version.ContentReference, ct));
        var baseUrl = cfg != null && cfg.TryGetValue("base_url", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        if (string.IsNullOrWhiteSpace(baseUrl)) return Results.BadRequest(new { code = "MODEL_DISCOVERY_INVALID", message = "Provider has no base_url configured; model discovery requires an HTTP model endpoint." });
        var protocol = ChatProtocols.Normalize(cfg != null && cfg.TryGetValue("protocol", out var proto) && proto.ValueKind == JsonValueKind.String ? proto.GetString() : ChatProtocols.InferFromDriver(row.Driver));
        string? apiKey = row.SecretReference != null && _secrets.ExistsAsync(row.SecretReference).GetAwaiter().GetResult() ? await _secrets.GetAsync(row.SecretReference, ct) : null;
        try
        {
            using var client = TinadecBranding.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
            if (protocol == ChatProtocols.AnthropicMessages)
            {
                if (!string.IsNullOrEmpty(apiKey)) request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new("Bearer", apiKey);
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

    private sealed record KnownCli(string Driver, string DisplayName, string Executable, string? HomePath, string? ServerUrl, string? LaunchArgs);

    // Well-known model CLIs the workbench can host. Discovery probes default install locations so
    // users can connect a local CLI runtime without manually typing a path.
    private static readonly KnownCli[] KnownClis =
    [
        new("claude-cli", "Claude Code", "claude", "~/.claude", null, null),
        new("codex-cli", "Codex CLI", "codex", "~/.codex", null, null),
        new("cursor-acp", "Cursor ACP", "cursor-agent", null, null, "--acp-port 0"),
        new("opencode", "OpenCode", "opencode", null, "http://127.0.0.1:4096", "serve --port 4096")
    ];

    public async Task<IResult> DiscoverCliRuntimes(CancellationToken ct, IEnumerable<string>? searchPaths = null)
    {
        await using var db = await _models.CreateDbContextAsync(ct);
        var configured = (await db.Providers
            .Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null)
            .Select(x => x.Driver).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resolved = searchPaths ?? (IEnumerable<string>?)null;
        var candidates = KnownClis.Select(cli =>
        {
            var existing = configured.Contains(cli.Driver);
            var path = existing ? null : ResolveCliExecutable(cli.Executable, resolved);
            var verified = path != null && VerifyCliExecutable(path);
            return new
            {
                driver = cli.Driver,
                display_name = cli.DisplayName,
                binary_path = verified ? path : null,
                home_path = existing || verified ? cli.HomePath : null,
                server_url = existing || verified ? cli.ServerUrl : null,
                launch_args = existing || verified ? cli.LaunchArgs : null,
                status = existing ? "configured" : verified ? "found" : "missing"
            };
        }).ToList();
        return Results.Ok(new { cli_runtimes = candidates });
    }

    /// <summary>
    /// Starts (or reuses) a discovered CLI runtime and persists it as an enabled provider.
    /// ACP CLIs are spawned with a free <c>--acp-port</c>; opencode is spawned with
    /// <c>serve --port</c>. The saved provider carries the reachable <c>server_url</c> and
    /// effective launch args so later runs can respawn it after a restart. Routes are not
    /// touched — binding the provider to the chat route stays a manual model-center action.
    /// </summary>
    public async Task<IResult> ConnectCliRuntime(JsonElement input, CancellationToken ct)
    {
        string? Get(string key) => input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var driver = Get("driver");
        var binaryPath = Get("binary_path");
        if (string.IsNullOrWhiteSpace(driver)) return Results.BadRequest(new { code = "CLI_CONNECT_INVALID", message = "driver is required." });
        if (string.IsNullOrWhiteSpace(binaryPath)) return Results.BadRequest(new { code = "CLI_CONNECT_INVALID", message = "binary_path is required." });
        if (!File.Exists(binaryPath)) return Results.BadRequest(new { code = "CLI_CONNECT_INVALID", message = $"binary_path does not exist: {binaryPath}" });
        var protocol = ChatProtocols.Normalize(Get("protocol") ?? ChatProtocols.InferFromDriver(driver));
        if (protocol is not (ChatProtocols.Acp or ChatProtocols.OpencodeServe)) return Results.BadRequest(new { code = "CLI_CONNECT_INVALID", message = $"driver '{driver}' is not a CLI runtime (protocol {protocol})." });

        CliRuntimeEndpoint endpoint;
        try
        {
            endpoint = await _cli.EnsureRunningAsync(new CliRuntimeConfig(driver, binaryPath, Get("launch_args"), Get("server_url"), Get("home_path")), ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            return Results.Json(new { code = "CLI_CONNECT_FAILED", message = ex.Message }, statusCode: 502);
        }

        await using var db = await _models.CreateDbContextAsync(ct);
        var existing = await db.Providers.SingleOrDefaultAsync(x => x.Driver == driver && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct);
        var port = new Uri(endpoint.ServerUrl).Port;
        var payload = JsonSerializer.SerializeToElement(new
        {
            driver,
            display_name = Get("display_name") ?? driver,
            connection_kind = "cli",
            protocol,
            binary_path = binaryPath,
            home_path = Get("home_path"),
            server_url = endpoint.ServerUrl,
            launch_args = Get("launch_args") ?? (protocol == ChatProtocols.OpencodeServe ? $"serve --port {port}" : $"--acp-port {port}"),
            enabled = true
        });
        // Internal system path: the CLI process just spawned is the source of truth, so the
        // save carries the row's live revision to satisfy the mandatory If-Match gate.
        return await SaveProvider(payload, existing?.Id, existing?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
    }

    private static string? ResolveCliExecutable(string name, IEnumerable<string>? searchPaths)
    {
        var paths = searchPaths ?? DefaultSearchPaths();
        foreach (var directory in paths)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
            if (OperatingSystem.IsWindows())
            {
                foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    var withExtension = candidate + extension;
                    if (File.Exists(withExtension)) return withExtension;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Probes that the discovered binary actually runs: <c>--version</c> must exit 0 within
    /// 3 seconds. <c>.cmd</c>/<c>.bat</c> npm shims are launched through cmd.exe.
    /// </summary>
    private static bool VerifyCliExecutable(string path)
    {
        try
        {
            var fileName = path;
            var arguments = "--version";
            if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                fileName = "cmd.exe";
                arguments = $"/c \"{path}\" --version";
            }
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!process.Start()) return false;
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            return false;
        }
    }

    private static IEnumerable<string> DefaultSearchPaths()
    {
        var paths = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path)) paths.AddRange(path.Split(Path.PathSeparator));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            paths.Add(Path.Combine(home, ".local", "bin"));
            paths.Add(Path.Combine(home, ".npm-global", "bin"));
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) paths.Add(Path.Combine(appData, "npm"));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            paths.Add(localAppData);
            paths.Add(Path.Combine(localAppData, "Programs"));
            paths.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));
        }
        return paths.Distinct().ToList();
    }

    private static void RemoveSecret(Dictionary<string, JsonElement> cfg)
    { cfg.Remove("api_key"); cfg.Remove("clear_api_key"); }

    public async Task<IResult> SaveProvider(JsonElement input, Guid? id, string? ifMatch, CancellationToken ct, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _models.CreateDbContextAsync(ct);
        var row = id.HasValue ? await db.Providers.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct) : null;
        if (row != null)
        {
            var precondition = CheckPrecondition(row.Revision, ifMatch);
            if (precondition != null) return precondition;
            var disabling = input.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False && row.Enabled;
            if (disabling && !force)
            {
                var disablePurposes = await ReferencingRoutePurposesAsync(db, row.Id, ct);
                if (disablePurposes.Count > 0) return ProviderInUse(disablePurposes);
            }
        }
        var isNew = row is null;
        if (isNew) row = new ModelProviderRecord { Id = id ?? Guid.NewGuid(), TenantId = Tenant.TenantId, WorkspaceId = Tenant.WorkspaceId, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now, Revision = 0 };
        var provider = row!;
        if (isNew) db.Providers.Add(provider);
        provider.Driver = input.TryGetProperty("driver", out var p) ? p.GetString() ?? "" : provider.Driver; provider.DisplayName = input.TryGetProperty("display_name", out p) ? p.GetString() ?? provider.Driver : provider.DisplayName; provider.ConnectionKind = input.TryGetProperty("connection_kind", out p) ? p.GetString() ?? "api-key" : provider.ConnectionKind; provider.Scope = input.TryGetProperty("scope", out p) ? p.GetString() ?? "workspace" : provider.Scope; provider.Enabled = !input.TryGetProperty("enabled", out p) || p.ValueKind != JsonValueKind.False; provider.UpdatedByPrincipalId = Tenant.PrincipalId; provider.UpdatedAt = now;
        if (input.TryGetProperty("api_key", out p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.GetString())) { provider.SecretReference ??= "provider-" + provider.Id.ToString("N"); await _secrets.PutAsync(provider.SecretReference, p.GetString()!, ct); } else if (input.TryGetProperty("clear_api_key", out p) && p.ValueKind == JsonValueKind.True && provider.SecretReference != null) { await _secrets.DeleteAsync(provider.SecretReference, ct); provider.SecretReference = null; }
        var merged = isNew
            ? MergeProviderConfig(new Dictionary<string, JsonElement>(), input)
            : MergeProviderConfig(await LoadProviderConfigAsync(db, provider.CurrentVersionId, ct), input);
        RemoveSecret(merged);
        var stored = await PutJsonAsync(_content, Tenant.TenantId, Tenant.WorkspaceId, "model-config", merged, ct);
        // Version numbers must come from the existing version rows, not from the
        // row revision: seeded/imported providers can hold Revision=0 alongside
        // an existing version 1, so Revision+1 collided with the unique
        // (provider_id, version) index on the first edit. SaveRoute already
        // derives max(version)+1; providers must do the same.
        var maxVersion = await db.ProviderVersions.Where(v => v.ProviderId == provider.Id).MaxAsync(v => (int?)v.Version, ct) ?? 0;
        var version = new ModelProviderVersionRecord { Id = Guid.NewGuid(), ProviderId = provider.Id, Version = maxVersion + 1, ContentReference = stored.reference.Value, ContentHash = stored.reference.Sha256, ContentLength = stored.reference.Length, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now };
        provider.Revision++; provider.CurrentVersionId = version.Id; db.ProviderVersions.Add(version); await db.SaveChangesAsync(ct);
        return Results.Ok(await ToProvider(provider));
    }

    private async Task<Dictionary<string, JsonElement>> LoadProviderConfigAsync(ModelControlDbContext db, Guid versionId, CancellationToken ct)
    {
        var version = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == versionId, ct);
        if (version == null) return new Dictionary<string, JsonElement>();
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await ReadAsync(_content, version.ContentReference, ct)) ?? new Dictionary<string, JsonElement>();
    }

    public async Task<IResult> DeleteProvider(Guid id, string? ifMatch, CancellationToken ct, bool force = false)
    {
        await using var db = await _models.CreateDbContextAsync(ct);
        var row = await db.Providers.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct);
        if (row == null) return Results.NotFound();
        var precondition = CheckPrecondition(row.Revision, ifMatch);
        if (precondition != null) return precondition;
        if (!force)
        {
            var purposes = await ReferencingRoutePurposesAsync(db, row.Id, ct);
            if (purposes.Count > 0) return ProviderInUse(purposes);
        }
        row.DeletedAt = DateTimeOffset.UtcNow; row.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return Results.NoContent();
    }
    public async Task<IResult> ListRoutes(CancellationToken ct)
    {
        await using var db = await _models.CreateDbContextAsync(ct);
        var rows = await db.Routes.AsNoTracking().Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null).ToListAsync(ct);
        var output = new List<ModelRouteDto>(rows.Count);
        foreach (var row in rows.OrderBy(x => x.Purpose, StringComparer.Ordinal))
        {
            var version = await db.RouteVersions.AsNoTracking().SingleAsync(x => x.Id == row.CurrentVersionId, ct);
            var candidates = await db.RouteCandidates.AsNoTracking().Where(x => x.RouteVersionId == version.Id).OrderBy(x => x.Position).ToListAsync(ct);
            output.Add(ToRouteDto(row, version, candidates));
        }
        return Results.Ok(output);
    }

    public async Task<IResult> SaveRoute(string purpose, ModelRouteWriteRequestDto input, string? ifMatch, CancellationToken ct)
    {
        purpose = purpose.Trim().ToLowerInvariant();
        if (purpose.Length == 0 || purpose.Length > 128) return Results.BadRequest(new { code = "invalid_model_route", message = "purpose is required and must not exceed 128 characters." });
        if (input.Candidates.Count == 0) return Results.BadRequest(new { code = "invalid_model_route", message = "candidates must contain at least one provider/model pair." });
        if (input.Candidates.Count > 32) return Results.BadRequest(new { code = "invalid_model_route", message = "candidates cannot contain more than 32 entries." });
        if (input.Candidates.Select(x => (x.ProviderInstanceId, x.Model)).Distinct().Count() != input.Candidates.Count)
            return Results.BadRequest(new { code = "invalid_model_route", message = "candidates must be unique and ordered by array position." });

        await using var db = await _models.CreateDbContextAsync(ct);
        foreach (var candidate in input.Candidates)
        {
            var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.ProviderInstanceId && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct);
            if (provider is null) return Results.BadRequest(new { code = "invalid_model_route", message = $"Provider '{candidate.ProviderInstanceId}' was not found." });
            if (string.IsNullOrWhiteSpace(candidate.Model) && !await IsRuntimeOwnedProviderAsync(db, provider, ct))
                return Results.BadRequest(new { code = "invalid_model_route", message = $"Provider '{candidate.ProviderInstanceId}' requires a model id." });
        }

        var row = await db.Routes.SingleOrDefaultAsync(x => x.Purpose == purpose && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct);
        var now = DateTimeOffset.UtcNow;
        if (row != null)
        {
            var precondition = CheckPrecondition(row.Revision, ifMatch);
            if (precondition != null) return precondition;
        }
        if (row == null)
        {
            row = new ModelRouteRecord { Id = Guid.NewGuid(), TenantId = Tenant.TenantId, WorkspaceId = Tenant.WorkspaceId, Purpose = purpose, Scope = "workspace", CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now };
            db.Routes.Add(row);
        }
        var maxVersion = await db.RouteVersions.Where(v => v.RouteId == row.Id).MaxAsync(v => (int?)v.Version, ct) ?? 0;
        var version = new ModelRouteVersionRecord { Id = Guid.NewGuid(), RouteId = row.Id, Version = maxVersion + 1, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now };
        var candidates = input.Candidates.Select((candidate, position) => new ModelRouteCandidateRecord
        {
            Id = Guid.NewGuid(), RouteVersionId = version.Id, Position = position,
            ProviderInstanceId = candidate.ProviderInstanceId,
            Model = string.IsNullOrWhiteSpace(candidate.Model) ? null : candidate.Model.Trim()
        }).ToArray();
        row.Revision++;
        row.CurrentVersionId = version.Id;
        row.UpdatedByPrincipalId = Tenant.PrincipalId;
        row.UpdatedAt = now;
        db.RouteVersions.Add(version);
        db.RouteCandidates.AddRange(candidates);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToRouteDto(row, version, candidates));
    }

    private static ModelRouteDto ToRouteDto(ModelRouteRecord row, ModelRouteVersionRecord version, IReadOnlyList<ModelRouteCandidateRecord> candidates) => new()
    {
        Id = row.Id, Purpose = row.Purpose, VersionId = version.Id, Version = version.Version,
        Candidates = candidates.OrderBy(x => x.Position).Select(x => new ModelRouteCandidateDto { ProviderInstanceId = x.ProviderInstanceId, Model = x.Model, Position = x.Position }).ToArray(),
        Revision = row.Revision, UpdatedAt = row.UpdatedAt
    };

    private async Task<bool> IsRuntimeOwnedProviderAsync(ModelControlDbContext db, ModelProviderRecord provider, CancellationToken ct)
    {
        var version = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == provider.CurrentVersionId, ct);
        if (version is null) return false;
        var config = JsonSerializer.Deserialize<JsonElement>(await ReadAsync(_content, version.ContentReference, ct));
        var protocol = config.TryGetProperty("protocol", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : ChatProtocols.InferFromDriver(provider.Driver);
        return ChatProtocols.Normalize(protocol) is ChatProtocols.Acp or ChatProtocols.OpencodeServe;
    }

    public async Task<IResult> ListPrompts(CancellationToken ct)
    { await using var db = await _prompts.CreateDbContextAsync(ct); var rows = await db.Fragments.Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null).ToListAsync(ct); var output = new List<object>(); foreach (var row in rows) { await using var d = await _prompts.CreateDbContextAsync(ct); var v = await d.Versions.SingleAsync(x => x.Id == row.CurrentVersionId, ct); output.Add(new { id = row.Id, key = row.Key, title = row.Title, scope = row.Scope, target_agent_id = row.TargetAgentId, category = row.Category, content = await ReadAsync(_content, v.ContentReference, ct), priority = row.Priority, enabled = row.Enabled, is_builtin = row.IsBuiltIn, revision = row.Revision, created_at = row.CreatedAt, updated_at = row.UpdatedAt }); } return Results.Ok(output); }
    public async Task<IResult> SavePrompt(JsonElement input, Guid? id, string? ifMatch, CancellationToken ct)
    { await using var db = await _prompts.CreateDbContextAsync(ct); var now = DateTimeOffset.UtcNow; var row = id.HasValue ? await db.Fragments.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct) : null; if (row?.IsBuiltIn == true) return Results.Conflict(new { message = "Built-in prompt fragments are read-only." }); if (row != null && !Matches(row.Revision, ifMatch)) return Results.StatusCode(412); if (row == null) { row = new PromptFragmentRecord { Id = id ?? Guid.NewGuid(), TenantId = Tenant.TenantId, WorkspaceId = Tenant.WorkspaceId, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now }; db.Fragments.Add(row); } string Get(string n, string fallback = "") => input.TryGetProperty(n, out var p) ? p.GetString() ?? fallback : fallback; row.Key = Get("key", row.Key); row.Title = Get("title", row.Title); row.Scope = Get("scope", row.Scope); row.Category = Get("category", row.Category); row.Priority = input.TryGetProperty("priority", out var pr) ? pr.GetInt32() : row.Priority; row.Enabled = !input.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False; if (input.TryGetProperty("target_agent_id", out var ta) && Guid.TryParse(ta.GetString(), out var aid)) row.TargetAgentId = aid; var content = Get("content"); var stored = await PutJsonAsync(_content, Tenant.TenantId, Tenant.WorkspaceId, "prompt-fragment", content, ct); var version = new PromptFragmentVersionRecord { Id = Guid.NewGuid(), FragmentId = row.Id, Version = (int)row.Revision + 1, ContentReference = stored.reference.Value, ContentHash = stored.reference.Sha256, ContentLength = stored.reference.Length, ChangeSummary = "configuration update", CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now }; row.Revision++; row.CurrentVersionId = version.Id; row.UpdatedByPrincipalId = Tenant.PrincipalId; row.UpdatedAt = now; db.Versions.Add(version); await db.SaveChangesAsync(ct); return Results.Ok(new { id = row.Id, key = row.Key, title = row.Title, scope = row.Scope, target_agent_id = row.TargetAgentId, category = row.Category, content, priority = row.Priority, enabled = row.Enabled, is_builtin = row.IsBuiltIn, revision = row.Revision, created_at = row.CreatedAt, updated_at = row.UpdatedAt }); }
    public async Task<IResult> DeletePrompt(Guid id, CancellationToken ct) { await using var db = await _prompts.CreateDbContextAsync(ct); var row = await db.Fragments.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct); if (row == null) return Results.NotFound(); if (row.IsBuiltIn) return Results.Conflict(new { message = "Built-in prompt fragments are read-only." }); row.DeletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return Results.NoContent(); }

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
        var result = rows.Select(ToResponse).Cast<object>().ToList();
        if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            var permissions = await _authorization.ListPermissionRequestsAsync(null, run, null, ct).ConfigureAwait(false);
            result.AddRange(permissions.Where(x => x.Status is PermissionRequestStatuses.AwaitingDelegate or PermissionRequestStatuses.AwaitingUser).Select(ToPermissionResponse));
        }
        return Results.Ok(result.OrderByDescending(x => x is ApprovalResponseDto approval ? approval.CreatedAt : DateTimeOffset.MinValue));
    }
    public async Task<IResult> CreatePreAuthorization(PreAuthorizationRequestDto input, CancellationToken ct)
    {
        if (input.RunId == Guid.Empty) return Results.BadRequest(new { message = "run_id is required." });
        var tools = (input.ToolScope ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tools.Count == 0) return Results.BadRequest(new { message = "tool_scope must contain at least one tool id." });
        if (tools.Any(t => string.Equals(t, "*", StringComparison.Ordinal)))
            return Results.BadRequest(new { message = "Wildcard tool grants are not allowed in a pre-authorization." });
        var riskMax = (input.RiskMax ?? "low").Trim().ToLowerInvariant() switch
        {
            "low" or "medium" or "high" or "elevated" or "critical" => (input.RiskMax ?? "low").Trim().ToLowerInvariant(),
            _ => "low"
        };
        var now = DateTimeOffset.UtcNow;
        var expiresAt = input.ExpiresAt ?? now.AddDays(7);
        if (expiresAt <= now) return Results.BadRequest(new { message = "expires_at must be in the future." });
        var maxUses = Math.Clamp(input.MaxUses <= 0 ? 1 : input.MaxUses, 1, 64);
        await using var db = await _lifecycle.CreateDbContextAsync(ct);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.RunId
            && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
        if (run is null) return Results.NotFound(new { message = "Run was not found in this tenant/workspace." });
        var row = new PreAuthorizationRecord
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant.TenantId,
            WorkspaceId = Tenant.WorkspaceId,
            RunId = input.RunId,
            LaneKey = string.IsNullOrWhiteSpace(input.LaneKey) ? null : input.LaneKey!.Trim(),
            ToolScopeJson = JsonSerializer.Serialize(tools),
            ParameterConstraintHash = string.IsNullOrWhiteSpace(input.ParameterConstraintHash) ? null : input.ParameterConstraintHash!.Trim().ToLowerInvariant(),
            RiskMax = riskMax,
            MaxUses = maxUses,
            UseCount = 0,
            ExpiresAt = expiresAt,
            GrantedByPrincipalId = Tenant.PrincipalId,
            Summary = input.Summary,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.PreAuthorizations.Add(row);
        await db.SaveChangesAsync(ct);
        // The user's pre-authorization is also a real delegable capability grant
        // per tool: the deterministic PDP must find a grant to issue the lease
        // that carries the call to the approval layer, where the pre-authorization
        // row is consumed once. The grant is an admission envelope, not the
        // consumption control — the row's max_uses budget is spent by the approval
        // mint, one per tool call — so the grant's own budget only has to cover
        // the lease-use reservation the dispatcher asks for (the task's remaining
        // tool rounds) across every call the row may release. Risk ceilings are
        // still re-checked by the approval layer before any tool runs.
        if (run.InitiatedByPrincipalId is { } runPrincipal && runPrincipal != Guid.Empty)
        {
            foreach (var tool in tools)
            {
                await _authorization.GrantCapabilityAsync(new GrantCapabilityCommand(
                    runPrincipal,
                    SubjectAgentInstanceId: null,
                    new CapabilityClaim("tool.invoke", "*", $"tool://{tool}"),
                    input.RunId,
                    TaskId: null,
                    expiresAt,
                    Math.Clamp(maxUses * 64, maxUses, 10_000),
                    Transferable: false,
                    ParentGrantId: null,
                    Reason: $"Pre-authorization {row.Id}: {(string.IsNullOrWhiteSpace(input.Summary) ? "unattended tool release" : input.Summary.Trim())}"), ct).ConfigureAwait(false);
            }
        }
        return Results.Json(new
        {
            id = row.Id,
            run_id = row.RunId,
            lane_key = row.LaneKey,
            tool_scope = tools,
            parameter_constraint_hash = row.ParameterConstraintHash,
            risk_max = row.RiskMax,
            max_uses = row.MaxUses,
            use_count = 0,
            expires_at = row.ExpiresAt,
            revoked = false
        }, statusCode: 201);
    }

    public async Task<IResult> GetApproval(Guid id, CancellationToken ct)
    {
        await using var db = await _lifecycle.CreateDbContextAsync(ct);
        var row = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
        if (row is not null) return Results.Ok(ToResponse(row));
        var permission = await _authorization.GetPermissionRequestAsync(id, ct).ConfigureAwait(false);
        return permission is null ? Results.NotFound() : Results.Ok(ToPermissionResponse(permission.Request));
    }
    public async Task<IResult> DecideApproval(Guid id, ApprovalDecisionRequestDto input, CancellationToken ct)
    {
        var permission = await _authorization.GetPermissionRequestAsync(id, ct).ConfigureAwait(false);
        if (permission is not null)
        {
            var approve = string.Equals(input.Decision, "approved", StringComparison.OrdinalIgnoreCase)
                || string.Equals(input.Decision, "approve", StringComparison.OrdinalIgnoreCase)
                || string.Equals(input.Decision, "granted", StringComparison.OrdinalIgnoreCase);
            var resolved = await _authorization.DecidePermissionAsync(new PermissionDecisionCommand(id, approve, null, null, input.Reason ?? string.Empty), ct).ConfigureAwait(false);

            // User actions have no run/task graph. A permission decision must
            // wake the Core-owned action directly; never manufacture a run for
            // a Desktop action.
            await using (var actionDb = await _lifecycle.CreateDbContextAsync(ct))
            {
                var userAction = await actionDb.UserToolActions.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.PermissionRequestId == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                if (userAction is not null)
                {
                    var resumed = await _userActions.ResumeAsync(userAction.Id, ct).ConfigureAwait(false);
                    return Results.Json(new { id, user_tool_action_id = userAction.Id, status = resumed.Status, action = resumed },
                        statusCode: resumed.Status is UserToolActionStatuses.AwaitingApproval or UserToolActionStatuses.AwaitingDelegate or UserToolActionStatuses.AwaitingUser or UserToolActionStatuses.SnapshotRequired
                            ? StatusCodes.Status202Accepted
                            : StatusCodes.Status200OK);
                }
            }
            if (resolved.Request.Status == PermissionRequestStatuses.Granted && resolved.Request.RunId is { } permissionRun)
            {
                await using var db = await _lifecycle.CreateDbContextAsync(ct);
                var execution = await db.ToolExecutions.SingleOrDefaultAsync(x => x.PermissionRequestId == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                if (execution is not null)
                {
                    var snapshot = await _executions.EnsureApprovalAsync(execution.Id, ct).ConfigureAwait(false);
                    if (snapshot.ApprovalId is { } actionApproval)
                        await _approvals.DecideAsync(actionApproval, approve ? "approved" : "rejected", input.Reason, ct).ConfigureAwait(false);
                }
                await _runs.SetRunStatusAsync(permissionRun.ToString(), "executing", "Legacy approval decision committed; resuming run.", ct).ConfigureAwait(false);
                await _engine.EnqueueAsync(permissionRun, ct).ConfigureAwait(false);
            }
            else if (resolved.Request.RunId is { } deniedRun
                && resolved.Request.Status is not (PermissionRequestStatuses.AwaitingDelegate or PermissionRequestStatuses.AwaitingUser))
            {
                // A denied permission request must wake the parked run too: without
                // the enqueue the awaiting_user run is never lease-eligible and
                // hangs forever. Denial semantics are preserved — nothing is
                // consumed and no grant is minted; the resumed dispatch observes
                // the denied request and the engine drives the task/lane to its
                // failure or escalation terminal state. This mirrors the wake-up
                // GovernanceEndpoints applies to every terminal permission decision.
                var deniedRunState = await _runs.GetRunStateAsync(deniedRun.ToString(), ct).ConfigureAwait(false);
                if (deniedRunState.Status is not (RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled))
                {
                    if (deniedRunState.Status is RunStatus.AwaitingApproval or RunStatus.AwaitingDelegate or RunStatus.AwaitingUser)
                    {
                        await _runs.SetRunStatusAsync(deniedRun.ToString(), "executing", "Permission request denied; resuming run to fail closed.", ct).ConfigureAwait(false);
                        await _runs.AppendEventAsync(deniedRun, "governance.permission_decided", new
                        {
                            permission_request_id = resolved.Request.Id,
                            authorization_decision_id = resolved.Decision.Id,
                            outcome = resolved.Decision.Outcome,
                            reason_code = resolved.Decision.ReasonCode
                        }, "Permission decision committed; run resumed.", cancellationToken: ct).ConfigureAwait(false);
                    }
                    await _engine.EnqueueAsync(deniedRun, ct).ConfigureAwait(false);
                }
            }
            return Results.Ok(new { id, status = resolved.Request.Status, decided_at = resolved.Decision.CreatedAt });
        }
        var approveAction = string.Equals(input.Decision, "approved", StringComparison.OrdinalIgnoreCase)
            || string.Equals(input.Decision, "approve", StringComparison.OrdinalIgnoreCase)
            || string.Equals(input.Decision, "granted", StringComparison.OrdinalIgnoreCase);
        await using (var actionDb = await _lifecycle.CreateDbContextAsync(ct))
        {
            var actionApproval = await actionDb.ApprovalRequests.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.UserToolActionId != null, ct);
            if (actionApproval?.UserToolActionId is { } userActionId)
            {
                try { await _approvals.DecideAsync(id, approveAction ? "approved" : "rejected", input.Reason, ct); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
                var resumed = await _userActions.ResumeAsync(userActionId, ct);
                return Results.Json(new { id, user_tool_action_id = userActionId, status = resumed.Status, action = resumed },
                    statusCode: resumed.Status is UserToolActionStatuses.AwaitingApproval or UserToolActionStatuses.AwaitingDelegate or UserToolActionStatuses.AwaitingUser ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
            }
        }
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

    private static ApprovalResponseDto ToPermissionResponse(PermissionRequestSnapshot value) => new()
    {
        Id = value.Id,
        RunId = value.RunId,
        TaskId = value.TaskId,
        AgentInstanceId = value.SubjectAgentInstanceId,
        Kind = "permission",
        ToolId = value.Claim.Resource.StartsWith("tool://", StringComparison.OrdinalIgnoreCase) ? value.Claim.Resource[7..] : value.Claim.Resource,
        Risk = value.Risk,
        Summary = "Tool permission request requires authorization.",
        Status = "pending",
        ExpiresAt = value.ExpiresAt,
        CreatedAt = value.CreatedAt,
        UpdatedAt = value.UpdatedAt
    };
}
