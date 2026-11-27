using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Models.Harness;
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
    private readonly IOpencodeServeProcessManager _opencode;
    private readonly IAcpHarnessProber _acp;
    private readonly IHarnessBinaryResolver _harnessBinaries;
    private readonly IHarnessTerminalHost _terminalHost;
    private readonly IModelProfileCatalogService? _modelProfileCatalog;
    private readonly IPromptFragmentService? _promptFragments;
    private readonly IPromptAssembler? _promptAssembler;
    private readonly IDbContextFactory<AgentConfigurationDbContext>? _agentConfig;
    private readonly GlobalAgentConfiguration? _globalAgentConfig;
    private readonly ISessionLocator? _sessions;
    private readonly IToolSkillResourceService? _skills;
    private readonly IToolApprovalPolicySource? _approvalPolicySource;
    private readonly Microsoft.Extensions.Options.IOptions<TinadecCore.Governance.AutoApproveOptions> _autoApproveOptions;
    private readonly IApprovalRules? _approvalRules;
    private readonly IApprovalGateLedger? _approvalGateLedger;

    public ControlPlaneService(IDbContextFactory<ModelControlDbContext> models, IDbContextFactory<PromptControlDbContext> prompts,
        IDbContextFactory<LifecycleDbContext> lifecycle,
        IContentStore content, ISecretStore secrets, ITenantContextAccessor tenant, IToolApprovalCoordinator approvals,
        IAuthorizationService authorization, IToolExecutionCoordinator executions,
        ILifecycleManager runs, IFullDuplexRunEngine engine, IOpencodeServeProcessManager opencode,
        IAcpHarnessProber acp, IHarnessBinaryResolver harnessBinaries,
        IUserToolActionService userActions, IHarnessTerminalHost terminalHost,
        IModelProfileCatalogService? modelProfileCatalog = null,
        IPromptFragmentService? promptFragments = null,
        IPromptAssembler? promptAssembler = null,
        IDbContextFactory<AgentConfigurationDbContext>? agentConfig = null,
        GlobalAgentConfiguration? globalAgentConfig = null,
        ISessionLocator? sessions = null,
        IToolSkillResourceService? skills = null,
        IToolApprovalPolicySource? approvalPolicySource = null,
        Microsoft.Extensions.Options.IOptions<TinadecCore.Governance.AutoApproveOptions>? autoApproveOptions = null,
        IApprovalRules? approvalRules = null,
        IApprovalGateLedger? approvalGateLedger = null)
    {
        _models = models; _prompts = prompts; _lifecycle = lifecycle; _content = content; _secrets = secrets; _tenant = tenant; _approvals = approvals; _authorization = authorization; _executions = executions; _userActions = userActions; _runs = runs; _engine = engine; _opencode = opencode; _acp = acp; _harnessBinaries = harnessBinaries; _terminalHost = terminalHost;
        _modelProfileCatalog = modelProfileCatalog;
        _promptFragments = promptFragments;
        _promptAssembler = promptAssembler;
        _agentConfig = agentConfig;
        _globalAgentConfig = globalAgentConfig;
        _sessions = sessions;
        _skills = skills;
        _approvalPolicySource = approvalPolicySource;
        _autoApproveOptions = autoApproveOptions ?? Microsoft.Extensions.Options.Options.Create(new TinadecCore.Governance.AutoApproveOptions());
        _approvalRules = approvalRules;
        _approvalGateLedger = approvalGateLedger;
    }

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
    {
        await using var db = await _models.CreateDbContextAsync(ct);
        var rows = await db.Providers.AsNoTracking()
            .Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null)
            .ToListAsync(ct);
        var versionIds = rows.Select(x => x.CurrentVersionId).Distinct().ToArray();
        var versions = versionIds.Length == 0
            ? new Dictionary<Guid, ModelProviderVersionRecord>()
            : await db.ProviderVersions.AsNoTracking()
                .Where(x => versionIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, ct);
        return Results.Ok(await Task.WhenAll(rows.Select(row => ToProvider(row, versions.GetValueOrDefault(row.CurrentVersionId), ct))));
    }

    private async Task<object> ToProvider(ModelProviderRecord row)
    {
        await using var db = await _models.CreateDbContextAsync();
        var version = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.CurrentVersionId);
        return await ToProvider(row, version, CancellationToken.None);
    }

    private async Task<object> ToProvider(ModelProviderRecord row, ModelProviderVersionRecord? version, CancellationToken ct)
    {
        Dictionary<string, JsonElement>? cfg = null;
        if (version != null) cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await ReadAsync(_content, version.ContentReference, ct));
        var hasApiKey = row.SecretReference != null && await _secrets.ExistsAsync(row.SecretReference, ct);
        JsonElement Value(string key) => cfg != null && cfg.TryGetValue(key, out var v) ? v : default;
        string? String(string key) => Value(key).ValueKind == JsonValueKind.String ? Value(key).GetString() : null;
        string[] Models() => Value("models").ValueKind == JsonValueKind.Array ? Value("models").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : Array.Empty<string>();
        var protocol = HarnessCatalog.ResolveProtocol(String("protocol"), row.Driver, String("channel"));
        var configElement = JsonSerializer.SerializeToElement(cfg ?? new Dictionary<string, JsonElement>());
        var imageInput = ImageInputCapability.Resolve(configElement, protocol, String("model"));
        var imageBase = ImageInputCapability.ResolveBase(configElement, protocol);
        var acpSnapshot = protocol == ChatProtocols.Acp;
        var imageOverrides = Value("image_input").ValueKind == JsonValueKind.Object
            && Value("image_input").TryGetProperty("model_overrides", out var overrides)
            && overrides.ValueKind == JsonValueKind.Object ? overrides.Clone() : (JsonElement?)null;
        return new { id = row.Id, driver = row.Driver, protocol, channel = String("channel"), display_name = row.DisplayName, connection_kind = row.ConnectionKind, base_url = String("base_url"), model = String("model"), models = Models(), model_parameters = ModelParameters.ReadMap(Value("model_parameters")), model_profiles = SafeReadModelProfiles(Value("model_profiles")), image_input = new { enabled = imageInput.Enabled, max_image_bytes = imageInput.MaxImageBytes, max_images_per_turn = imageInput.MaxImagesPerTurn, base_enabled = imageBase.Enabled, base_max_image_bytes = imageBase.MaxImageBytes, base_max_images_per_turn = imageBase.MaxImagesPerTurn, editable = !acpSnapshot && protocol is ChatProtocols.OpenAiChat or ChatProtocols.OpenAiResponses or ChatProtocols.AnthropicMessages, source = acpSnapshot ? "acp_snapshot" : protocol is ChatProtocols.OpenAiChat or ChatProtocols.OpenAiResponses or ChatProtocols.AnthropicMessages ? "config" : "protocol_unsupported", model_overrides = imageOverrides }, acp_capabilities = ReadAcpCapabilities(Value("_capabilities")), has_api_key = hasApiKey, binary_path = String("binary_path"), home_path = String("home_path"), server_url = String("server_url"), launch_args = String("launch_args"), capabilities = cfg != null && cfg.TryGetValue("capabilities", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(), enabled = row.Enabled, status = row.Enabled ? "configured" : "disabled", status_message = String("_probe_diagnostic") ?? "Persisted configuration", revision = row.Revision, scope = row.Scope, created_at = row.CreatedAt, updated_at = row.UpdatedAt };
    }

    private static object? ReadAcpCapabilities(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        bool? Bool(string name) => value.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;
        var imageInput = Bool("image_input");
        var loadSession = Bool("load_session");
        var sessionFork = Bool("session_fork");
        if (imageInput is null && loadSession is null && sessionFork is null) return null;
        var source = value.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String
            ? src.GetString()
            : "acp_initialize";
        return new
        {
            load_session = loadSession ?? false,
            image_input = imageInput ?? false,
            session_fork = sessionFork ?? false,
            source
        };
    }

    private static IReadOnlyDictionary<string, ModelProfileDto> SafeReadModelProfiles(JsonElement value)
    {
        try
        {
            return ModelProfile.ReadMap(value);
        }
        catch (ArgumentException)
        {
            return new Dictionary<string, ModelProfileDto>(StringComparer.Ordinal);
        }
    }

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
        var protocol = HarnessCatalog.ResolveProtocol(cfg != null && cfg.TryGetValue("protocol", out var proto) && proto.ValueKind == JsonValueKind.String ? proto.GetString() : null, row.Driver, null);
        string? apiKey = row.SecretReference != null && await _secrets.ExistsAsync(row.SecretReference, ct) ? await _secrets.GetAsync(row.SecretReference, ct) : null;
        try
        {
            var targetUri = new Uri($"{baseUrl.TrimEnd('/')}/models");
            using var client = TinadecBranding.CreateClient(targetUri);
            client.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, targetUri);
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Json(new { code = "MODEL_DISCOVERY_TIMEOUT", message = "Provider /models request timed out after 10 seconds." }, statusCode: 502);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
        { return Results.Json(new { code = "MODEL_DISCOVERY_NETWORK", message = ex.Message }, statusCode: 502); }
    }

    /// <summary>
    /// Reads the harness catalog instead of a hand-written driver list, so what the model center can
    /// offer is exactly what Core has verified: one row per harness, each carrying the channels that
    /// harness actually has and whether this build can drive them. The old four-row table named
    /// drivers after channels (<c>claude-cli</c>, <c>cursor-acp</c>) and reported a version probe as
    /// proof that an ACP session would work, which is how a harness that could never connect ended up
    /// offered as connectable.
    /// </summary>
    public async Task<IResult> DiscoverHarnesses(CancellationToken ct, IEnumerable<string>? searchPaths = null)
    {
        await using var db = await _models.CreateDbContextAsync(ct);
        var configuredRows = await db.Providers
            .Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null)
            .Select(x => new { x.Driver, x.CurrentVersionId }).ToListAsync(ct);

        var specPrepared = new List<(
            HarnessSpec Spec,
            bool Existing,
            bool IsTemplate,
            HarnessBinaryLocation? Located,
            string? ConfiguredBinaryPath,
            string? ConfiguredHomePath,
            string? ConfiguredLaunchArgs,
            object? ConfiguredAcpCapabilities,
            List<string> ConfiguredChannels,
            List<string> ConfiguredModels,
            Task<CliProbe?> ProbeTask)>();
        foreach (var spec in HarnessCatalog.All)
        {
            var rows = configuredRows
                .Where(row => string.Equals(row.Driver, spec.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var configuredChannels = new List<string>();
            var configuredModels = new List<string>();
            string? configuredBinaryPath = null;
            string? configuredHomePath = null;
            string? configuredLaunchArgs = null;
            object? configuredAcpCapabilities = null;
            foreach (var row in rows)
            {
                var cfg = await LoadProviderConfigAsync(db, row.CurrentVersionId, ct);
                string? ConfigText(string key) => cfg.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var channel = AgentChannels.Normalize(ConfigText("channel"));
                var protocol = HarnessCatalog.ResolveProtocol(ConfigText("protocol"), spec.Id, ConfigText("channel"));
                configuredChannels.Add(channel ?? protocol);
                configuredBinaryPath ??= ConfigText("binary_path");
                configuredHomePath ??= ConfigText("home_path");
                configuredLaunchArgs ??= ConfigText("launch_args");
                if (configuredAcpCapabilities is null && cfg.TryGetValue("_capabilities", out var capsEl))
                    configuredAcpCapabilities = ReadAcpCapabilities(capsEl);
                var singleModel = ConfigText("model");
                if (!string.IsNullOrWhiteSpace(singleModel)) configuredModels.Add(singleModel);
                if (cfg.TryGetValue("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in modelsEl.EnumerateArray())
                    {
                        var text = m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(text)) configuredModels.Add(text);
                    }
                }
            }
            var existing = rows.Count > 0;
            var isTemplate = string.Equals(spec.Id, "custom-acp", StringComparison.OrdinalIgnoreCase);
            var located = isTemplate ? null : _harnessBinaries.Resolve(spec.Id, searchPaths);
            Task<CliProbe?> probeTask = located is null
                ? Task.FromResult<CliProbe?>(null)
                : VerifyCliExecutableNullable(located.BinaryName, located.BinaryPath, spec.VersionProbe, ct);
            specPrepared.Add((spec, existing, isTemplate, located, configuredBinaryPath, configuredHomePath, configuredLaunchArgs, configuredAcpCapabilities, configuredChannels, configuredModels, probeTask));
        }
        await Task.WhenAll(specPrepared.Select(x => x.ProbeTask)).ConfigureAwait(false);

        var candidates = new List<object>(specPrepared.Count);
        foreach (var entry in specPrepared)
        {
            var (spec, existing, isTemplate, located, configuredBinaryPath, configuredHomePath, configuredLaunchArgs, configuredAcpCapabilities, configuredChannels, configuredModels, probeTask) = entry;
            var probe = await probeTask.ConfigureAwait(false);
            var reachable = existing || probe?.Runnable == true;
            // The serve argv is a template with a {port} placeholder; discovery reports the concrete
            // form it would start with, and the connect path re-materializes it from the endpoint that
            // actually answered, so a second server on the same machine does not collide.
            var serveArgv = spec.HttpServer is { } server
                ? string.Join(' ', server.Argv).Replace("{port}", server.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : null;
            var defaultChannel = spec.HttpServer is not null ? null : spec.Channels[0].Channel;
            var defaultProtocol = spec.DefaultProtocol;
            var defaultLaunchArgs = configuredLaunchArgs
                ?? serveArgv
                ?? (spec.Channels[0].Argv.Count > 0 ? string.Join(' ', spec.Channels[0].Argv) : null);
            var detectedHome = configuredHomePath ?? (reachable ? _harnessBinaries.ConfigHomeOf(spec.Id) : null);
            candidates.Add(new
            {
                driver = spec.Id,
                display_name = spec.DisplayName,
                vendor = spec.Vendor,
                docs_url = spec.DocsUrl,
                is_template = isTemplate,
                default_channel = defaultChannel,
                default_protocol = defaultProtocol,
                default_launch_args = defaultLaunchArgs,
                env_vars = spec.EnvVars.ToArray(),
                detected_models = configuredModels.Distinct(StringComparer.Ordinal).ToArray(),
                acp_capabilities = configuredAcpCapabilities,
                binary_path = reachable ? located?.BinaryPath ?? configuredBinaryPath : null,
                // Reported as a fact about the harness, not as the process HOME. The two are different
                // directories: Kimi's config home is `~/.kimi-code`, and handing that to the child as
                // HOME would make it look for `~/.kimi-code/.kimi-code`. Only the path the operator
                // types in the form becomes HOME, and discovery never invents one.
                config_home = detectedHome,
                home_path = detectedHome,
                server_url = reachable && spec.HttpServer is { } endpoint ? $"http://127.0.0.1:{endpoint.DefaultPort}" : null,
                launch_args = reachable ? (configuredLaunchArgs ?? serveArgv ?? defaultLaunchArgs) : defaultLaunchArgs,
                status = existing ? "configured" : isTemplate ? "template" : probe?.Runnable == true ? "found" : "missing",
                configured_channels = configuredChannels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                // A probe that did not answer is not evidence that the harness is absent, and the old
                // code turned that difference invisible: an unread stdout pipe or a loaded machine made
                // an installed binary read as "not detected". The note says which is which.
                probe_note = existing ? null : probe is { Runnable: false } failed ? failed.Note : null,
                probe_note_code = existing || probe is not { Runnable: false } ? null : probe.NoteCode,
                probe_note_data = existing || probe is not { Runnable: false } ? null : probe.NoteData,
                probe_hint = existing ? null : BuildDiscoverHint(spec, isTemplate, probe),
                channels = HarnessChannelFacts(spec).ToArray(),
                caveats = spec.KnownCaveats.ToArray()
            });
        }
        return Results.Ok(new { cli_runtimes = candidates });
    }

    /// <summary>
    /// Starts (or reuses) a discovered harness runtime and persists it as an enabled provider.
    /// opencode's HTTP server shape is spawned with <c>serve --port</c> and stored with the
    /// reachable <c>server_url</c>, so later runs can respawn it after a restart. ACP is a stdio
    /// session and has no port to inject: an ACP connect stores the harness's own argv and proves
    /// readiness by handshake, not by polling a URL. Routes are not touched — binding the provider
    /// to the chat route stays a manual model-center action.
    /// </summary>
    public async Task<IResult> ConnectHarness(JsonElement input, CancellationToken ct)
    {
        string? Get(string key) => input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var driver = Get("driver");
        var binaryPath = Get("binary_path");
        if (string.IsNullOrWhiteSpace(driver)) return Results.BadRequest(new { code = "CLI_CONNECT_INVALID", message = "driver is required." });
        if (string.IsNullOrWhiteSpace(binaryPath)) return Results.BadRequest(new { code = "CLI_CONNECT_INVALID", message = "binary_path is required." });
        if (!File.Exists(binaryPath)) return Results.BadRequest(new { code = "CLI_CONNECT_INVALID", message = $"binary_path does not exist: {binaryPath}" });
        var channel = Get("channel");
        // A named channel is a routing decision, so it is validated against the harness rather than
        // resolved away: `claude-code` + `acp` must be refused by name, not silently read as the
        // driver's default protocol and then fail as an OpenAI provider with no base_url.
        var spec = HarnessCatalog.Find(driver);
        if (spec is not null && !string.IsNullOrWhiteSpace(channel) && spec.Channel(channel) is null)
        {
            var declared = string.Join(", ", spec.Channels.Select(declaredChannel => declaredChannel.Channel));
            return Results.BadRequest(new { code = "harness_channel_unsupported", message = $"'{driver}' has no '{channel}' channel; the catalog declares {declared}." });
        }
        var protocol = HarnessCatalog.ResolveProtocol(Get("protocol"), driver, channel);
        if (spec is not null && Get("protocol") is null && string.IsNullOrWhiteSpace(channel))
        {
            // Neither axis named: silently taking the catalog's default protocol is how a connect for a
            // harness with three ways to be reached picks one on the caller's behalf.
            return Results.BadRequest(new { code = "harness_channel_required", message = $"'{driver}' can be reached multiple ways; send 'channel' ({string.Join(", ", spec.Channels.Select(declaredChannel => declaredChannel.Channel))}) or an explicit 'protocol'." });
        }
        if (spec is not null && !OffersProtocol(spec, protocol))
        {
            return Results.BadRequest(new { code = "harness_protocol_unsupported", message = $"'{driver}' does not speak '{protocol}'; this harness offers {string.Join(", ", OfferedProtocols(spec))}." });
        }
        if (protocol == ChatProtocols.HeadlessCli && !HarnessCatalog.HasVerifiedHeadlessEnvelope(driver))
        {
            return Results.BadRequest(new
            {
                code = "CLI_CONNECT_INVALID",
                message = $"'{driver}' has no headless answer frame captured by this build; choose another declared channel."
            });
        }

        if (protocol == ChatProtocols.Tui && !_terminalHost.IsSupported)
        {
            return Results.BadRequest(new
            {
                code = "CLI_CONNECT_INVALID",
                message = $"'{driver}' on the TUI channel cannot be connected because this platform has no supported terminal host."
            });
        }

        if (protocol is not (ChatProtocols.Acp or ChatProtocols.OpencodeServe or ChatProtocols.HeadlessCli or ChatProtocols.Tui))
        {
            return Results.BadRequest(new
            {
                code = "CLI_CONNECT_INVALID",
                message = $"'{driver}' does not resolve to a channel this build can connect (protocol '{protocol}'); connectable protocols are '{ChatProtocols.Acp}', '{ChatProtocols.HeadlessCli}', '{ChatProtocols.Tui}' and '{ChatProtocols.OpencodeServe}'."
            });
        }

        OpencodeServeEndpoint? endpoint = null;
        AcpHarnessProbeResult? acpCapabilities = null;
        var launchArgs = Get("launch_args");
        var homePath = Get("home_path");
        var requestedModel = Get("model");
        var requestedModels = input.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.Array
            ? modelsProp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray()
            : (!string.IsNullOrWhiteSpace(requestedModel) ? new[] { requestedModel.Trim() } : Array.Empty<string>());
        var existing = await ExistingCliProviderAsync(driver, channel, protocol, ct);
        if (protocol == ChatProtocols.Acp)
        {
            // A stdio session has no port to poll, so readiness is a real handshake: spawn,
            // initialize, open a session, then tear the harness down again. Storing a server_url the
            // probe never proved is what let a provider look connected while it could not start.
            try
            {
                acpCapabilities = await ProbeAcpHandshakeAsync(driver, binaryPath, homePath, launchArgs, existing?.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Everything a harness can fail with here is a fact about connecting to it: an
                // unavailable binary, a timeout waiting for `initialize`, a session the agent
                // refused. Only the caller's cancellation is a different kind of answer.
                return Results.Json(new
                {
                    code = "CLI_CONNECT_FAILED",
                    message = ex.Message,
                    hint = BuildAcpConnectDiagnosticHint(spec, ex.Message)
                }, statusCode: 502);
            }
        }
        else if (protocol == ChatProtocols.OpencodeServe)
        {
            try
            {
                endpoint = await _opencode.EnsureRunningAsync(new OpencodeServeConfig(binaryPath, launchArgs, Get("server_url"), homePath), ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
            {
                return Results.Json(new { code = "CLI_CONNECT_FAILED", message = ex.Message }, statusCode: 502);
            }
        }

        var port = endpoint is null ? 0 : new Uri(endpoint.ServerUrl).Port;
        var defaultChannelArgv = spec?.Channel(channel)?.Argv;
        var resolvedLaunchArgs = !string.IsNullOrWhiteSpace(launchArgs)
            ? launchArgs.Trim()
            : protocol == ChatProtocols.OpencodeServe
                ? $"serve --port {port}"
                : defaultChannelArgv is { Count: > 0 }
                    ? string.Join(' ', defaultChannelArgv)
                    : null;
        var payload = JsonSerializer.SerializeToElement(new
        {
            driver,
            display_name = Get("display_name") ?? spec?.DisplayName ?? driver,
            connection_kind = "cli",
            protocol,
            channel,
            model = requestedModel ?? requestedModels.FirstOrDefault(),
            models = requestedModels,
            binary_path = binaryPath,
            home_path = homePath,
            server_url = endpoint?.ServerUrl,
            launch_args = resolvedLaunchArgs,
            _capabilities = acpCapabilities is null ? null : new
            {
                image_input = acpCapabilities.ImageInput,
                load_session = acpCapabilities.LoadSession,
                session_fork = acpCapabilities.SessionFork,
                source = "acp_initialize"
            },
            _probe_diagnostic = (string?)null,
            enabled = true
        });
        // Internal system path: the CLI process just spawned is the source of truth, so the
        // save carries the row's live revision to satisfy the mandatory If-Match gate.
        return await SaveProvider(payload, existing?.Id, existing?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
    }

    /// <summary>
    /// Proves the harness can complete an ACP handshake. Everything about how it is spawned — the
    /// catalog's argv, the governed scratch directory it is allowed to work in — stays behind the
    /// prober port, because this file's job is the provider row, not the process shape.
    /// </summary>
    private async Task<AcpHarnessProbeResult> ProbeAcpHandshakeAsync(string driver, string binaryPath, string? homePath, string? launchArgs, Guid? providerInstanceId, CancellationToken ct) =>
        await _acp.ProbeAsync(providerInstanceId, driver, binaryPath, homePath, launchArgs, ct);

    private static string? BuildDiscoverHint(HarnessSpec spec, bool isTemplate, CliProbe? probe)
    {
        if (isTemplate)
            return "Generic ACP template: enter any compliant ACP executable path in binary_path and optional flags in launch_args.";
        if (probe is not { Runnable: false }) return null;
        var authNote = spec.EnvVars.Count > 0 ? $" Verify auth env ({string.Join(", ", spec.EnvVars)}) or sign in interactively once." : string.Empty;
        return probe.NoteCode switch
        {
            "probe_timeout" => $"Version probe timed out; check whether '{spec.BinaryNames[0]}' blocks on first-run login/prompt.{authNote}",
            "exit_code" => $"Executable returned non-zero on version probe; check CLI installation or set custom launch_args.{authNote}",
            _ => $"Could not probe '{spec.BinaryNames[0]}'; verify binary_path or permissions.{authNote}"
        };
    }

    private static string BuildAcpConnectDiagnosticHint(HarnessSpec? spec, string errorMessage)
    {
        if (errorMessage.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("login", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("api key", StringComparison.OrdinalIgnoreCase))
        {
            var envHint = spec is { EnvVars.Count: > 0 }
                ? $" Ensure {string.Join(" / ", spec.EnvVars)} is set or run the CLI login command once."
                : " Sign in once in your terminal before connecting via ACP.";
            return "ACP authentication failed." + envHint;
        }
        if (errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "ACP initialize timed out. Verify launch_args starts stdio ACP mode (not interactive TUI) and check first-run prompts.";
        return spec is null || string.Equals(spec.Id, "custom-acp", StringComparison.OrdinalIgnoreCase)
            ? "Verify binary_path points to an executable speaking JSON-RPC stdio ACP and configure any subcommands/flags in launch_args."
            : $"Verify '{spec.DisplayName}' supports stdio ACP flags ({string.Join(' ', spec.Channel(AgentChannels.Acp)?.Argv ?? [])}) or override launch_args.";
    }

    private async Task<ModelProviderRecord?> ExistingCliProviderAsync(string driver, string? channel, string protocol, CancellationToken ct)
    {
        await using var db = await _models.CreateDbContextAsync(ct);
        var rows = await db.Providers
            .Where(x => x.Driver == driver && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            var cfg = await LoadProviderConfigAsync(db, row.CurrentVersionId, ct);
            string? ConfigText(string key) => cfg.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var savedChannel = AgentChannels.Normalize(ConfigText("channel"));
            if (channel is not null && string.Equals(savedChannel, AgentChannels.Normalize(channel), StringComparison.Ordinal)) return row;
            if (channel is null && savedChannel is null && string.Equals(
                HarnessCatalog.ResolveProtocol(ConfigText("protocol"), driver, null), protocol, StringComparison.OrdinalIgnoreCase)) return row;
        }
        return null;
    }

    /// <summary>
    /// Asks a discovered binary for its version. The argv, the ceiling, and whether a non-zero exit
    /// still counts as an answer all come from the harness's own probe record, because they are vendor
    /// facts rather than local preferences: CodeBuddy prints a telemetry consent prompt and exits
    /// non-zero when nothing answers it, so a strict <c>ExitCode == 0</c> rule reports an installed
    /// binary as missing. <c>NeverUse</c> exists for the mirror-image hazard — a flag that looks
    /// harmless but shells out to something with side effects.
    /// </summary>
    /// <remarks>
    /// Both pipes are drained while the child runs. A binary printing more than the pipe buffer blocks
    /// on its own write, and a parent waiting on a blocked child looks exactly like a binary that is
    /// not installed — which is how a working harness silently disappeared from the model center.
    /// </remarks>
    private static async Task<CliProbe> VerifyCliExecutable(string name, string path, HarnessVersionProbe probe, CancellationToken ct)
    {
        var process = new Process { StartInfo = BuildVersionProbeStart(path, probe) };
        try
        {
            var command = string.Join(' ', probe.Argv);
            if (!process.Start())
                return new(false, $"starting '{path}' {command} returned false.", "start_failed", Facts(("command", $"{path} {command}")));
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(probe.TimeoutMs);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryKillProbeTree(process);
                // The caller stopped looking; that is not a fact about the harness.
                throw;
            }
            catch (OperationCanceledException)
            {
                TryKillProbeTree(process);
                return new(false, $"'{name}' did not answer '{command}' within {probe.TimeoutMs} ms, so this build does not know whether it is installed.",
                    "probe_timeout", Facts(("name", name), ("command", command), ("timeout_ms", probe.TimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            }
            return process.ExitCode == 0 || probe.TolerateNonZeroExit
                ? new(true, null)
                : new(false, $"'{name}' exited {process.ExitCode} for '{command}'.",
                    "exit_code", Facts(("name", name), ("command", command), ("exit_code", process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            return new(false, $"'{name}' could not be probed: {ex.Message}", "probe_failed", Facts(("name", name), ("error", ex.Message)));
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryKillProbeTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The probe is already leaving; a process that cannot be reported on is not worth a fault.
        }
    }

    private static ProcessStartInfo BuildVersionProbeStart(string path, HarnessVersionProbe probe)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        HarnessProcessCommand.Apply(psi, path, probe.Argv);
        return psi;
    }

    /// <summary><paramref name="Note"/> is the English diagnostic; <paramref name="NoteCode"/> and <paramref name="NoteData"/> are what a client words.</summary>
    private sealed record CliProbe(bool Runnable, string? Note, string? NoteCode = null, IReadOnlyDictionary<string, string>? NoteData = null);

    private async Task<CliProbe?> VerifyCliExecutableNullable(string name, string command, HarnessVersionProbe probeSpec, CancellationToken ct) =>
        await VerifyCliExecutable(name, command, probeSpec, ct).ConfigureAwait(false);

    private static IReadOnlyDictionary<string, string> Facts(params (string Name, string Value)[] facts) =>
        facts.ToDictionary(fact => fact.Name, fact => fact.Value);

    /// <summary>
    /// Every channel of one harness that Core knows about, in the order the catalog declares them, with
    /// the local-server shape stated as a channel-less entry because <c>opencode serve</c> is reached
    /// over HTTP and not over a process channel.
    /// </summary>
    private List<object> HarnessChannelFacts(HarnessSpec spec)
    {
        var channels = new List<object>();
        if (spec.HttpServer is { } server)
            channels.Add(ChannelFact(null, server.Protocol, spec.Id));
        foreach (var channel in spec.Channels)
            channels.Add(ChannelFact(channel.Channel, channel.Protocol, spec.Id));
        return channels;

        /// <summary>
        /// Drivability of the one-shot channel is a (harness, channel) fact: the headless client exists,
        /// but it only parses answer frames this build has actually captured from that vendor. Offering
        /// <c>cli</c> for a harness whose stdout has only ever been seen failing would light a button
        /// whose run ends in "the harness never answered".
        /// </summary>
        ChannelFact ChannelFact(string? channel, string protocol, string harnessId)
        {
            var drivable = ChatProtocols.IsDrivable(protocol)
                && (ChatProtocols.Normalize(protocol) != ChatProtocols.Tui || _terminalHost.IsSupported)
                && (ChatProtocols.Normalize(protocol) != ChatProtocols.HeadlessCli
                    || HarnessCatalog.HasVerifiedHeadlessEnvelope(harnessId));
            if (drivable) return new ChannelFact(channel, protocol, true, null);
            // An undrivable channel always carries its reason: "cannot drive" on its own tells nobody what to do.
            var normalized = ChatProtocols.Normalize(protocol);
            return ChatProtocols.HarnessClientGap(protocol) is { } gap ? new ChannelFact(channel, protocol, false, gap)
                : !ChatProtocols.IsDrivable(protocol)
                    ? new ChannelFact(channel, protocol, false, $"this build has no client for protocol '{protocol}'", "protocol_unsupported")
                : normalized == ChatProtocols.Tui
                    ? new ChannelFact(channel, protocol, false, "this platform has no supported terminal host for a TUI session", "terminal_host_unsupported")
                : new ChannelFact(channel, protocol, false, $"'{harnessId}' has no headless answer frame captured by this build", "headless_frame_unverified");
        }
    }

    /// <summary><paramref name="Reason"/> is the English diagnostic; <paramref name="ReasonCode"/> is what a client words.</summary>
    private sealed record ChannelFact(string? Channel, string Protocol, bool Drivable, string? Reason, string? ReasonCode = null);

    /// <summary>
    /// The protocols one harness actually offers: its declared channels plus, for the harnesses that
    /// also expose a long-lived local server, that server's protocol. Anything outside this set is a
    /// dialect this product never implements for this binary, so it must be refused by name rather
    /// than stored and discovered as a failing run.
    /// </summary>
    private static IEnumerable<string> OfferedProtocols(HarnessSpec spec) =>
        spec.Channels.Select(channel => channel.Protocol)
            .Concat(spec.HttpServer is { } server ? new[] { server.Protocol } : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool OffersProtocol(HarnessSpec spec, string protocol) =>
        OfferedProtocols(spec).Contains(protocol, StringComparer.OrdinalIgnoreCase);

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
        var merged = row is null
            ? MergeProviderConfig(new Dictionary<string, JsonElement>(), input)
            : MergeProviderConfig(await LoadProviderConfigAsync(db, row.CurrentVersionId, ct), input);
        try
        {
            if (merged.TryGetValue("model_parameters", out var parameters))
            {
                var settings = ModelParameters.ReadMap(parameters);
                string? ConfigText(string key) => merged.TryGetValue(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                var protocol = HarnessCatalog.ResolveProtocol(ConfigText("protocol"), ConfigText("driver") ?? row?.Driver, ConfigText("channel"));
                foreach (var item in settings.Values.Where(x => !x.IsEmpty))
                {
                    if (ChatProtocols.IsModelChosenByHarness(protocol)) throw new ArgumentException("Inference settings are managed by this harness.");
                    if (item.ReasoningEffort is not null && !ModelParameters.SupportsReasoning(protocol))
                        throw new ArgumentException("reasoning_effort is supported for OpenAI Chat and Responses protocols.");
                    if (protocol == ChatProtocols.AnthropicMessages && item.Temperature is > 1)
                        throw new ArgumentException("Anthropic temperature must be between 0 and 1.");
                }
            }
            if (merged.TryGetValue("model_profiles", out var profiles))
            {
                _ = ModelProfile.ReadMap(profiles);
            }
            ImageInputCapability.Validate(JsonSerializer.SerializeToElement(merged));
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            var code = ex.Message.Contains("image_input", StringComparison.Ordinal)
                ? "invalid_image_input"
                : ex.Message.Contains("model_profiles", StringComparison.Ordinal)
                    ? "invalid_model_profile"
                    : "invalid_model_parameters";
            return Results.BadRequest(new { code, message = ex.Message });
        }
        var isNew = row is null;
        if (isNew) row = new ModelProviderRecord { Id = id ?? Guid.NewGuid(), TenantId = Tenant.TenantId, WorkspaceId = Tenant.WorkspaceId, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now, Revision = 0 };
        var provider = row!;
        if (isNew) db.Providers.Add(provider);
        provider.Driver = input.TryGetProperty("driver", out var p) ? p.GetString() ?? "" : provider.Driver; provider.DisplayName = input.TryGetProperty("display_name", out p) ? p.GetString() ?? provider.Driver : provider.DisplayName; provider.ConnectionKind = input.TryGetProperty("connection_kind", out p) ? p.GetString() ?? "api-key" : provider.ConnectionKind; provider.Scope = input.TryGetProperty("scope", out p) ? p.GetString() ?? "workspace" : provider.Scope; provider.Enabled = !input.TryGetProperty("enabled", out p) || p.ValueKind != JsonValueKind.False; provider.UpdatedByPrincipalId = Tenant.PrincipalId; provider.UpdatedAt = now;
        if (input.TryGetProperty("api_key", out p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.GetString())) { provider.SecretReference ??= "provider-" + provider.Id.ToString("N"); await _secrets.PutAsync(provider.SecretReference, p.GetString()!, ct); } else if (input.TryGetProperty("clear_api_key", out p) && p.ValueKind == JsonValueKind.True && provider.SecretReference != null) { await _secrets.DeleteAsync(provider.SecretReference, ct); provider.SecretReference = null; }
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
        var rows = await db.Routes.AsNoTracking()
            .Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null)
            .OrderBy(x => x.Purpose)
            .ToListAsync(ct);
        if (rows.Count == 0) return Results.Ok(Array.Empty<ModelRouteDto>());
        var versionIds = rows.Select(x => x.CurrentVersionId).Distinct().ToArray();
        var versions = await db.RouteVersions.AsNoTracking()
            .Where(x => versionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var candidatesByVersion = (await db.RouteCandidates.AsNoTracking()
            .Where(x => versionIds.Contains(x.RouteVersionId))
            .OrderBy(x => x.Position)
            .ToListAsync(ct))
            .GroupBy(x => x.RouteVersionId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var output = new List<ModelRouteDto>(rows.Count);
        foreach (var row in rows)
        {
            var version = versions[row.CurrentVersionId];
            var candidates = candidatesByVersion.GetValueOrDefault(version.Id) ?? [];
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
        string? Text(string key) => config.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return ChatProtocols.IsModelChosenByHarness(
            HarnessCatalog.ResolveProtocol(Text("protocol"), provider.Driver, Text("channel")));
    }

    public Task<SanitizedModelCatalogDto> GetSanitizedModelCatalogAsync(bool includeDisabled = false, CancellationToken ct = default) =>
        (_modelProfileCatalog ?? new ModelProfileCatalogService(_models, _content, _secrets, _tenant))
            .GetSanitizedCatalogAsync(includeDisabled, ct);

    public Task<ModelProfileUpdateResultDto> UpdateModelProfileAsync(
        Guid providerId,
        string modelId,
        ModelProfileDto? profile,
        long? expectedRevision = null,
        CancellationToken ct = default) =>
        (_modelProfileCatalog ?? new ModelProfileCatalogService(_models, _content, _secrets, _tenant))
            .UpdateModelProfileAsync(providerId, modelId, profile, expectedRevision, ct);

    public async Task<IResult> ListPrompts(
        CancellationToken ct,
        string? scope = null,
        string? targetAgentIdOrSlug = null,
        string? category = null,
        bool? enabled = null)
    {
        Guid? targetGuid = null;
        string? targetSlug = null;
        if (!string.IsNullOrWhiteSpace(targetAgentIdOrSlug))
        {
            var trimmed = targetAgentIdOrSlug.Trim();
            if (Guid.TryParse(trimmed, out var parsed) && parsed != Guid.Empty)
                targetGuid = parsed;
            else
                targetSlug = trimmed;
        }
        if (_promptFragments is not null)
        {
            var list = await _promptFragments.ListAsync(scope, targetGuid, targetSlug, category, enabled, ct);
            return Results.Ok(list);
        }
        await using var db = await _prompts.CreateDbContextAsync(ct);
        var rows = await db.Fragments.AsNoTracking()
            .Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null)
            .ToListAsync(ct);
        var output = new List<PromptFragmentDto>(rows.Count);
        foreach (var row in rows)
        {
            var v = await db.Versions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.CurrentVersionId, ct);
            var raw = v is null ? string.Empty : await ReadAsync(_content, v.ContentReference, ct);
            output.Add(new PromptFragmentDto
            {
                Id = row.Id,
                Key = row.Key,
                Title = row.Title,
                Scope = row.Scope,
                TargetAgentId = row.TargetAgentId,
                TargetAgentSlug = row.TargetAgentSlug,
                Category = row.Category,
                Content = UnwrapPromptContent(raw),
                Priority = row.Priority,
                Enabled = row.Enabled,
                IsBuiltIn = row.IsBuiltIn,
                Revision = row.Revision,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt
            });
        }
        return Results.Ok(output);
    }

    public async Task<IResult> SavePrompt(JsonElement input, Guid? id, string? ifMatch, CancellationToken ct)
    {
        if (!TryParseOptionalIfMatch(ifMatch, out var expectedRevision))
            return Results.Json(new { code = "invalid_if_match", message = "If-Match must contain the numeric revision." }, statusCode: 400);

        string? OptString(string name) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var prop)
                ? prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ValueKind == JsonValueKind.Null ? string.Empty : null
                : null;

        int? priority = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Number && pr.TryGetInt32(out var pv)
            ? pv
            : null;
        bool? enabled = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? en.GetBoolean()
            : null;

        var rawTargetAgent = OptString("target_agent_id");
        var rawTargetSlug = OptString("target_agent_slug");
        var (resolvedAgentId, resolvedAgentSlug) = await ResolveTargetAgentAsync(rawTargetAgent, rawTargetSlug, ct);

        if (_promptFragments is not null)
        {
            try
            {
                var saved = await _promptFragments.SaveAsync(
                    id,
                    new PromptFragmentSaveRequestDto
                    {
                        Key = OptString("key"),
                        Title = OptString("title"),
                        Scope = OptString("scope"),
                        TargetAgentId = resolvedAgentId,
                        TargetAgentSlug = resolvedAgentSlug,
                        Category = OptString("category"),
                        Content = OptString("content"),
                        Priority = priority,
                        Enabled = enabled,
                        ChangeSummary = OptString("change_summary")
                    },
                    expectedRevision,
                    ct);
                return Results.Ok(saved);
            }
            catch (PromptFragmentException ex)
            {
                return MapPromptException(ex);
            }
        }

        await using var db = await _prompts.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var row = id.HasValue ? await db.Fragments.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct) : null;
        if (row?.IsBuiltIn == true) return Results.Conflict(new { code = "built_in_read_only", message = "Built-in prompt fragments are read-only." });
        if (row != null && expectedRevision is { } exp && row.Revision != exp) return Results.StatusCode(412);
        if (row == null)
        {
            row = new PromptFragmentRecord { Id = id ?? Guid.NewGuid(), TenantId = Tenant.TenantId, WorkspaceId = Tenant.WorkspaceId, CreatedByPrincipalId = Tenant.PrincipalId, CreatedAt = now };
            db.Fragments.Add(row);
        }
        row.Key = OptString("key") ?? row.Key;
        row.Title = OptString("title") ?? row.Title;
        row.Scope = OptString("scope") ?? row.Scope;
        row.Category = OptString("category") ?? row.Category;
        row.Priority = priority ?? row.Priority;
        row.Enabled = enabled ?? row.Enabled;
        if (resolvedAgentId is not null)
            row.TargetAgentId = Guid.TryParse(resolvedAgentId, out var aid) ? aid : null;
        if (resolvedAgentSlug is not null)
            row.TargetAgentSlug = string.IsNullOrWhiteSpace(resolvedAgentSlug) ? null : resolvedAgentSlug;
        var content = OptString("content") ?? string.Empty;
        var stored = await PutJsonAsync(_content, Tenant.TenantId, Tenant.WorkspaceId, "prompt-fragment", content, ct);
        var maxVersion = await db.Versions.Where(v => v.FragmentId == row.Id).MaxAsync(v => (int?)v.Version, ct) ?? 0;
        var version = new PromptFragmentVersionRecord
        {
            Id = Guid.NewGuid(),
            FragmentId = row.Id,
            Version = maxVersion + 1,
            ContentReference = stored.reference.Value,
            ContentHash = stored.reference.Sha256,
            ContentLength = stored.reference.Length,
            ChangeSummary = OptString("change_summary") ?? "configuration update",
            CreatedByPrincipalId = Tenant.PrincipalId,
            CreatedAt = now
        };
        row.Revision++;
        row.CurrentVersionId = version.Id;
        row.UpdatedByPrincipalId = Tenant.PrincipalId;
        row.UpdatedAt = now;
        db.Versions.Add(version);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new PromptFragmentDto
        {
            Id = row.Id,
            Key = row.Key,
            Title = row.Title,
            Scope = row.Scope,
            TargetAgentId = row.TargetAgentId,
            TargetAgentSlug = row.TargetAgentSlug,
            Category = row.Category,
            Content = content,
            Priority = row.Priority,
            Enabled = row.Enabled,
            IsBuiltIn = row.IsBuiltIn,
            Revision = row.Revision,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        });
    }

    public async Task<IResult> DeletePrompt(Guid id, CancellationToken ct)
    {
        if (_promptFragments is not null)
        {
            try
            {
                var deleted = await _promptFragments.DeleteAsync(id, ct);
                return deleted ? Results.NoContent() : Results.NotFound(new { code = "not_found", message = $"Prompt fragment '{id}' was not found." });
            }
            catch (PromptFragmentException ex)
            {
                return MapPromptException(ex);
            }
        }
        await using var db = await _prompts.CreateDbContextAsync(ct);
        var row = await db.Fragments.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.DeletedAt == null, ct);
        if (row == null) return Results.NotFound();
        if (row.IsBuiltIn) return Results.Conflict(new { code = "built_in_read_only", message = "Built-in prompt fragments are read-only." });
        row.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    public async Task<IResult> ListPromptVersions(Guid id, CancellationToken ct)
    {
        if (_promptFragments is null) return Results.Ok(Array.Empty<PromptFragmentVersionDto>());
        try
        {
            return Results.Ok(await _promptFragments.ListVersionsAsync(id, ct));
        }
        catch (PromptFragmentException ex)
        {
            return MapPromptException(ex);
        }
    }

    public async Task<IResult> CreatePromptVersion(Guid id, JsonElement input, string? ifMatch, CancellationToken ct)
    {
        if (!TryParseOptionalIfMatch(ifMatch, out var expectedRevision))
            return Results.Json(new { code = "invalid_if_match", message = "If-Match must contain the numeric revision." }, statusCode: 400);
        if (_promptFragments is null)
            return Results.Json(new { code = "capability_unavailable", message = "Prompt fragment service is not registered." }, statusCode: 501);

        var content = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? string.Empty
            : string.Empty;
        var summary = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("change_summary", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        string[]? changedFields = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("changed_fields", out var cf) && cf.ValueKind == JsonValueKind.Array
            ? cf.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : null;

        try
        {
            var created = await _promptFragments.CreateVersionAsync(
                id,
                new PromptFragmentVersionCreateRequestDto
                {
                    Content = content,
                    ChangedFields = changedFields,
                    ChangeSummary = summary
                },
                expectedRevision,
                ct);
            return Results.Ok(created);
        }
        catch (PromptFragmentException ex)
        {
            return MapPromptException(ex);
        }
    }

    public async Task<IResult> RollbackPrompt(Guid id, JsonElement input, string? ifMatch, CancellationToken ct)
    {
        if (!TryParseOptionalIfMatch(ifMatch, out var expectedRevision))
            return Results.Json(new { code = "invalid_if_match", message = "If-Match must contain the numeric revision." }, statusCode: 400);
        if (_promptFragments is null)
            return Results.Json(new { code = "capability_unavailable", message = "Prompt fragment service is not registered." }, statusCode: 501);

        int? targetVersion = null;
        Guid? versionId = null;
        string? changeSummary = null;

        if (input.ValueKind == JsonValueKind.Object)
        {
            if (input.TryGetProperty("target_version", out var tv))
            {
                if (tv.ValueKind == JsonValueKind.Number && tv.TryGetInt32(out var num))
                    targetVersion = num;
                else if (tv.ValueKind == JsonValueKind.String && int.TryParse(tv.GetString(), out var parsedNum))
                    targetVersion = parsedNum;
            }
            if (input.TryGetProperty("version_id", out var vid) && vid.ValueKind == JsonValueKind.String)
            {
                if (Guid.TryParse(vid.GetString(), out var parsedGuid))
                    versionId = parsedGuid;
                else if (targetVersion is null && int.TryParse(vid.GetString(), out var fallbackNum))
                    targetVersion = fallbackNum;
            }
            if (input.TryGetProperty("change_summary", out var cs) && cs.ValueKind == JsonValueKind.String)
                changeSummary = cs.GetString();
        }

        try
        {
            var updated = await _promptFragments.RollbackAsync(
                id,
                new PromptFragmentRollbackRequestDto
                {
                    TargetVersion = targetVersion,
                    VersionId = versionId,
                    ChangeSummary = changeSummary
                },
                expectedRevision,
                ct);
            return Results.Ok(updated);
        }
        catch (PromptFragmentException ex)
        {
            return MapPromptException(ex);
        }
    }

    public async Task<IResult> ClonePrompt(Guid id, JsonElement? input, CancellationToken ct)
    {
        if (_promptFragments is null)
            return Results.Json(new { code = "capability_unavailable", message = "Prompt fragment service is not registered." }, statusCode: 501);

        PromptFragmentCloneRequestDto? request = null;
        if (input is { ValueKind: JsonValueKind.Object } obj)
        {
            string? Opt(string name) => obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
            var (resolvedId, resolvedSlug) = await ResolveTargetAgentAsync(Opt("target_agent_id"), Opt("target_agent_slug"), ct);
            request = new PromptFragmentCloneRequestDto
            {
                Key = Opt("key"),
                Title = Opt("title"),
                Scope = Opt("scope"),
                TargetAgentId = resolvedId,
                TargetAgentSlug = resolvedSlug
            };
        }

        try
        {
            var cloned = await _promptFragments.CloneAsync(id, request, ct);
            return Results.Ok(cloned);
        }
        catch (PromptFragmentException ex)
        {
            return MapPromptException(ex);
        }
    }

    public async Task<IResult> PreviewPromptContext(JsonElement body, CancellationToken ct)
    {
        if (_promptAssembler is null)
            return Results.Json(new { code = "capability_unavailable", message = "Prompt context preview requires the active context assembler runtime." }, statusCode: 501);

        string? OptString(string name) =>
            body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
        Guid? OptGuid(string name) =>
            OptString(name) is { } s && Guid.TryParse(s.Trim(), out var g) && g != Guid.Empty ? g : null;

        IReadOnlyList<Guid>? includeFragmentIds = null;
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("include_fragment_ids", out var incEl)
            && incEl.ValueKind == JsonValueKind.Array)
        {
            var ids = new List<Guid>();
            foreach (var item in incEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var g) && g != Guid.Empty)
                    ids.Add(g);
            }
            includeFragmentIds = ids;
        }

        JsonElement? draftGraph = null;
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("draft_pipeline_graph", out var dg))
        {
            if (dg.ValueKind == JsonValueKind.Object)
                draftGraph = dg.Clone();
            else if (dg.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(dg.GetString()))
            {
                try
                {
                    using var doc = JsonDocument.Parse(dg.GetString()!);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        draftGraph = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { code = "invalid_request", message = "draft_pipeline_graph must be valid JSON." });
                }
            }
        }

        var requestDto = new PromptContextPreviewRequestDto
        {
            AgentId = OptString("agent_id"),
            Mode = OptString("mode"),
            ModeId = OptGuid("mode_id"),
            ModeVersionId = OptGuid("mode_version_id"),
            PipelineId = OptGuid("pipeline_id"),
            DraftSystemPrompt = OptString("draft_system_prompt"),
            DraftModeInstructions = OptString("draft_mode_instructions"),
            DraftPipelineGraph = draftGraph,
            IncludeFragmentIds = includeFragmentIds,
            SessionId = OptGuid("session_id"),
            RunId = OptGuid("run_id"),
            UserContent = OptString("user_content")
        };

        var preview = await PreviewPromptContextCoreAsync(requestDto, ct);
        return Results.Ok(preview);
    }

    public async Task<PromptContextPreviewResponseDto> PreviewPromptContextCoreAsync(
        PromptContextPreviewRequestDto input,
        CancellationToken ct = default)
    {
        var assembler = _promptAssembler ?? throw new InvalidOperationException("IPromptAssembler is not registered.");
        FrozenWorkspaceBinding? workspace = null;
        Guid? sessionModeVersionId = null;

        if (input.SessionId is { } sessionId && sessionId != Guid.Empty && _sessions is not null)
        {
            var session = await _sessions.FindAsync(sessionId, ct);
            if (session is not null && session.TenantId == Tenant.TenantId && session.WorkspaceId == Tenant.WorkspaceId)
            {
                workspace = await WorkspaceBindingFactory.TryCreateAsync(_sessions, session, ct);
                sessionModeVersionId = session.ModeVersionId;
            }
        }

        var resolvedAgent = await ResolveAgentForPreviewAsync(input.AgentId, ct);
        var resolvedMode = await ResolveModeForPreviewAsync(input.ModeVersionId ?? sessionModeVersionId, input.ModeId, input.Mode, resolvedAgent?.Id, ct);
        var (pipelineId, promptVersionId, promptVersionHash, promptGraphJson) = await ResolvePipelineForPreviewAsync(
            input.DraftPipelineGraph,
            input.PipelineId,
            resolvedMode?.NodePromptPipelineId,
            resolvedMode?.NodePromptVersionId,
            resolvedAgent?.BasePromptPipelineId,
            ct);

        var effectiveAgentSlug = resolvedAgent?.Slug
            ?? (!string.IsNullOrWhiteSpace(input.AgentId) && !Guid.TryParse(input.AgentId.Trim(), out _) ? input.AgentId.Trim() : "meeting");
        var effectiveAgentName = resolvedAgent?.DisplayName ?? effectiveAgentSlug;
        var effectiveSystemPrompt = input.DraftSystemPrompt ?? resolvedAgent?.SystemPrompt;
        var effectiveModeInstructions = input.DraftModeInstructions ?? resolvedMode?.Instructions;
        var effectiveModeSlug = resolvedMode?.ModeSlug ?? input.Mode ?? string.Empty;
        var effectiveModeName = resolvedMode?.ModeName ?? effectiveModeSlug;

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(effectiveModeSlug))
        {
            metadata["runtime_profile_id"] = resolvedMode?.ModeVersionId is { } mvId
                ? $"mode:{resolvedMode.ModeId}:{mvId}"
                : $"mode:{effectiveModeSlug}";
            metadata["mode_slug"] = effectiveModeSlug;
        }
        if (!string.IsNullOrWhiteSpace(effectiveModeName))
            metadata["mode_name"] = effectiveModeName;
        if (resolvedAgent is not null)
        {
            metadata["agent_definition_id"] = resolvedAgent.Id.ToString();
            metadata["agent_slug"] = resolvedAgent.Slug;
            metadata["agent_name"] = resolvedAgent.DisplayName;
            metadata["layer"] = resolvedAgent.Layer;
        }

        var evidence = new List<ContextEvidence>();
        if (!string.IsNullOrWhiteSpace(input.UserContent))
        {
            evidence.Add(new ContextEvidence
            {
                Source = "user_turn_preview",
                Content = input.UserContent.Trim()
            });
        }

        var contextPack = new ContextPack
        {
            TokenBudget = 8192,
            Metadata = metadata,
            Evidence = evidence
        };

        var assemblyRequest = new FrozenPromptAssemblyRequest(
            AgentId: effectiveAgentSlug,
            ContextPack: contextPack,
            SystemPrompt: effectiveSystemPrompt,
            AgentVersionId: null,
            AgentVersionContentHash: null,
            PromptPipelineId: pipelineId,
            PromptVersionId: promptVersionId,
            PromptVersionContentHash: promptVersionHash,
            PromptGraphJson: promptGraphJson,
            IncludeLiveFragments: true,
            ResponseLocale: null)
        {
            Workspace = workspace,
            RuntimePolicy = resolvedMode?.RuntimePolicy,
            AgentDefinitionId = resolvedAgent?.Id,
            AgentName = effectiveAgentName,
            AgentSlug = effectiveAgentSlug,
            AgentLayer = resolvedAgent?.Layer,
            ModeName = effectiveModeName,
            ModeSlug = effectiveModeSlug,
            ModeInstructions = effectiveModeInstructions,
            Capabilities = resolvedAgent?.Capabilities,
            HasTools = resolvedAgent?.HasTools,
            IncludeFragmentIds = input.IncludeFragmentIds
        };

        var assembled = await assembler.AssembleAsync(assemblyRequest, ct);

        IReadOnlyList<PromptFragmentDto> matchedFragments = [];
        if (_promptFragments is not null)
        {
            var allFragments = await _promptFragments.ListAsync(cancellationToken: ct);
            var usedIds = assembled.FragmentIds
                .Select(fid => Guid.TryParse(fid, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToHashSet();
            matchedFragments = allFragments.Where(f => usedIds.Contains(f.Id)).ToArray();
        }

        return new PromptContextPreviewResponseDto
        {
            AssembledPrompt = assembled.Instructions,
            TotalEstimatedTokens = assembled.EstimatedTokens,
            Layers = assembled.Layers.Select(l => new PromptContextLayerDto
            {
                Id = l.Id,
                Kind = l.Kind,
                Title = l.Title,
                Content = l.Content,
                EstimatedTokens = l.EstimatedTokens,
                Source = l.Source
            }).ToArray(),
            Warnings = assembled.Warnings,
            AgentId = effectiveAgentSlug,
            Mode = effectiveModeSlug,
            EstimatedTokens = assembled.EstimatedTokens,
            SystemPrompt = assembled.Instructions,
            Fragments = matchedFragments,
            ContextPackIds = assembled.FragmentIds.Where(x => x.StartsWith("context:", StringComparison.Ordinal)).ToArray()
        };
    }

    private static bool TryParseOptionalIfMatch(string? ifMatch, out long? revision)
    {
        revision = null;
        if (string.IsNullOrWhiteSpace(ifMatch)) return true;
        var normalized = ifMatch.Trim().Trim('"', 'W', '/', ' ');
        if (normalized.Length == 0) return true;
        if (!long.TryParse(normalized, out var parsed)) return false;
        revision = parsed;
        return true;
    }

    private static IResult MapPromptException(PromptFragmentException ex) => ex.StatusCode switch
    {
        404 => Results.NotFound(new { code = ex.Code, message = ex.Message }),
        409 => Results.Conflict(new { code = ex.Code, message = ex.Message }),
        412 => Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: 412),
        _ => Results.BadRequest(new { code = ex.Code, message = ex.Message })
    };

    private static string UnwrapPromptContent(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var current = raw;
        for (var i = 0; i < 4; i++)
        {
            var trimmed = current.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '"') break;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != JsonValueKind.String) break;
                current = doc.RootElement.GetString() ?? string.Empty;
            }
            catch (JsonException)
            {
                break;
            }
        }
        return current;
    }

    private IEnumerable<IDbContextFactory<AgentConfigurationDbContext>> AgentConfigFactories()
    {
        if (_agentConfig is not null) yield return _agentConfig;
        if (_globalAgentConfig is not null && !ReferenceEquals(_globalAgentConfig.Factory, _agentConfig))
            yield return _globalAgentConfig.Factory;
    }

    private async Task<(string? TargetAgentId, string? TargetAgentSlug)> ResolveTargetAgentAsync(
        string? rawIdOrSlug,
        string? rawSlug,
        CancellationToken ct)
    {
        if (rawIdOrSlug is null && rawSlug is null)
            return (null, null);

        var candidate = rawIdOrSlug?.Trim() ?? rawSlug?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
            return (string.Empty, string.Empty);

        var isGuid = Guid.TryParse(candidate, out var parsedGuid) && parsedGuid != Guid.Empty;
        foreach (var factory in AgentConfigFactories())
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var def = isGuid
                ? await db.AgentDefinitions.AsNoTracking()
                    .Where(x => x.Id == parsedGuid && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId)
                    .FirstOrDefaultAsync(ct)
                : await db.AgentDefinitions.AsNoTracking()
                    .Where(x => x.Slug == candidate && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.Status != "archived")
                    .OrderByDescending(x => x.Status == "published")
                    .ThenByDescending(x => x.Version)
                    .FirstOrDefaultAsync(ct);
            if (def is not null)
                return (def.Id.ToString(), def.Slug);
        }

        return isGuid
            ? (parsedGuid.ToString(), rawSlug?.Trim())
            : (candidate, candidate);
    }

    private sealed record PreviewResolvedAgent(
        Guid Id,
        string Slug,
        string DisplayName,
        string Layer,
        string? SystemPrompt,
        Guid? BasePromptPipelineId,
        IReadOnlyList<string> Capabilities,
        bool HasTools);

    private sealed record PreviewResolvedMode(
        Guid ModeId,
        Guid? ModeVersionId,
        string ModeSlug,
        string ModeName,
        string? Instructions,
        ModeRuntimePolicyDto? RuntimePolicy,
        Guid? NodePromptPipelineId,
        Guid? NodePromptVersionId);

    private async Task<PreviewResolvedAgent?> ResolveAgentForPreviewAsync(string? agentIdOrSlug, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(agentIdOrSlug) ? null : agentIdOrSlug.Trim();
        var hasGuid = query is not null && Guid.TryParse(query, out var parsedGuid) && parsedGuid != Guid.Empty;
        Guid targetGuid = hasGuid ? Guid.Parse(query!) : Guid.Empty;

        foreach (var factory in AgentConfigFactories())
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            AgentDefinitionRecord? def = null;
            if (hasGuid)
            {
                def = await db.AgentDefinitions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == targetGuid && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
            }
            else if (query is not null)
            {
                def = await db.AgentDefinitions.AsNoTracking()
                    .Where(x => x.Slug == query && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.Status != "archived")
                    .OrderByDescending(x => x.Status == "published")
                    .ThenByDescending(x => x.Version)
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                var defaults = await db.WorkspaceDefaults.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                if (defaults?.DefaultAgentDefinitionId is { } defId)
                {
                    def = await db.AgentDefinitions.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == defId && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                }
                def ??= await db.AgentDefinitions.AsNoTracking()
                    .Where(x => x.Slug == "meeting" && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.Status != "archived")
                    .OrderByDescending(x => x.Status == "published")
                    .FirstOrDefaultAsync(ct);
            }

            if (def is not null)
            {
                var caps = ParseStringListJson(def.CapabilitiesJson);
                var tools = ParseStringListJson(def.ToolScopeJson);
                return new PreviewResolvedAgent(
                    def.Id,
                    def.Slug,
                    def.DisplayName,
                    def.Layer,
                    def.SystemPrompt,
                    def.BasePromptPipelineId,
                    caps,
                    tools.Count > 0);
            }
        }
        return null;
    }

    private async Task<PreviewResolvedMode?> ResolveModeForPreviewAsync(
        Guid? modeVersionId,
        Guid? modeId,
        string? modeSlugOrId,
        Guid? targetAgentDefinitionId,
        CancellationToken ct)
    {
        if (modeId is null && !string.IsNullOrWhiteSpace(modeSlugOrId) && Guid.TryParse(modeSlugOrId.Trim(), out var parsedModeGuid))
            modeId = parsedModeGuid;

        foreach (var factory in AgentConfigFactories())
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            ModeVersionRecord? version = null;
            AgentModeRecord? mode = null;

            if (modeVersionId is { } mvId && mvId != Guid.Empty)
            {
                version = await db.ModeVersions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == mvId && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                if (version is not null)
                    mode = await db.AgentModes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == version.AgentModeId, ct);
            }
            if (mode is null && modeId is { } mId && mId != Guid.Empty)
            {
                mode = await db.AgentModes.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == mId && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
            }
            if (mode is null && !string.IsNullOrWhiteSpace(modeSlugOrId))
            {
                var s = modeSlugOrId.Trim();
                mode = await db.AgentModes.AsNoTracking()
                    .Where(x => (x.Slug == s || x.DisplayName == s) && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId && x.ArchivedAt == null)
                    .OrderByDescending(x => x.Status == "published")
                    .ThenByDescending(x => x.Version)
                    .FirstOrDefaultAsync(ct);
            }
            if (mode is null && modeVersionId is null && modeId is null && string.IsNullOrWhiteSpace(modeSlugOrId))
            {
                var defaults = await db.WorkspaceDefaults.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                if (defaults?.DefaultModeVersionId is { } dmvId)
                {
                    version = await db.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == dmvId, ct);
                    if (version is not null)
                        mode = await db.AgentModes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == version.AgentModeId, ct);
                }
                else if (defaults?.DefaultAgentModeId is { } damId)
                {
                    mode = await db.AgentModes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == damId, ct);
                }
            }

            if (mode is null && version is null) continue;

            version ??= mode is null
                ? null
                : await db.ModeVersions.AsNoTracking()
                    .Where(x => x.AgentModeId == mode.Id && x.Status == "published")
                    .OrderByDescending(x => x.Version)
                    .FirstOrDefaultAsync(ct);

            var instructions = mode?.Instructions;
            ModeRuntimePolicyDto? runtimePolicy = null;
            if (!string.IsNullOrWhiteSpace(mode?.RuntimePolicyJson))
            {
                try { runtimePolicy = JsonSerializer.Deserialize<ModeRuntimePolicyDto>(mode.RuntimePolicyJson, FrozenRunConfigurationV1.JsonOptions); }
                catch (JsonException) { }
            }

            Guid? nodePipelineId = null;
            Guid? nodePromptVersionId = null;
            if (!string.IsNullOrWhiteSpace(version?.SnapshotJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(version.SnapshotJson);
                    var root = doc.RootElement;
                    if (string.IsNullOrWhiteSpace(instructions) && root.TryGetProperty("instructions", out var instEl) && instEl.ValueKind == JsonValueKind.String)
                        instructions = instEl.GetString();
                    if (runtimePolicy is null && root.TryGetProperty("runtime_policy", out var rpEl) && rpEl.ValueKind == JsonValueKind.Object)
                        runtimePolicy = rpEl.Deserialize<ModeRuntimePolicyDto>(FrozenRunConfigurationV1.JsonOptions);
                    if (root.TryGetProperty("nodes", out var nodesEl) && nodesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var n in nodesEl.EnumerateArray())
                        {
                            if (targetAgentDefinitionId is { } aid
                                && n.TryGetProperty("agent_definition_id", out var adEl)
                                && adEl.ValueKind == JsonValueKind.String
                                && Guid.TryParse(adEl.GetString(), out var nodeAgentId)
                                && nodeAgentId != aid)
                            {
                                continue;
                            }
                            if (n.TryGetProperty("prompt_pipeline_id", out var ppEl) && ppEl.ValueKind == JsonValueKind.String && Guid.TryParse(ppEl.GetString(), out var ppId))
                                nodePipelineId ??= ppId;
                            if (n.TryGetProperty("prompt_version_id", out var pvEl) && pvEl.ValueKind == JsonValueKind.String && Guid.TryParse(pvEl.GetString(), out var pvId))
                                nodePromptVersionId ??= pvId;
                        }
                    }
                }
                catch (JsonException) { }
            }

            return new PreviewResolvedMode(
                mode?.Id ?? version!.AgentModeId,
                version?.Id,
                mode?.Slug ?? modeSlugOrId ?? string.Empty,
                mode?.DisplayName ?? mode?.Slug ?? string.Empty,
                instructions,
                runtimePolicy,
                nodePipelineId,
                nodePromptVersionId);
        }

        return null;
    }

    private async Task<(Guid? PipelineId, Guid? PromptVersionId, string? PromptVersionHash, string? GraphJson)> ResolvePipelineForPreviewAsync(
        JsonElement? draftGraph,
        Guid? explicitPipelineId,
        Guid? modeNodePipelineId,
        Guid? modeNodePromptVersionId,
        Guid? agentBasePipelineId,
        CancellationToken ct)
    {
        if (draftGraph is { ValueKind: JsonValueKind.Object } graphObj)
            return (explicitPipelineId, null, null, graphObj.GetRawText());

        var targetPipelineId = explicitPipelineId ?? modeNodePipelineId ?? agentBasePipelineId;
        foreach (var factory in AgentConfigFactories())
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (targetPipelineId is null)
            {
                var defaults = await db.WorkspaceDefaults.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                targetPipelineId = defaults?.DefaultPromptPipelineId;
            }
            if (targetPipelineId is null && modeNodePromptVersionId is null) continue;

            if (explicitPipelineId is null && modeNodePromptVersionId is { } pvId)
            {
                var pv = await db.PromptVersions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == pvId && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                if (pv is not null)
                    return (pv.PromptPipelineId, pv.Id, pv.ContentHash, pv.GraphJson);
            }

            if (targetPipelineId is { } pid)
            {
                var pipeline = await db.PromptPipelines.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == pid && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                if (pipeline is not null)
                {
                    var latestVersion = await db.PromptVersions.AsNoTracking()
                        .Where(x => x.PromptPipelineId == pid && x.Status == "published")
                        .OrderByDescending(x => x.Version)
                        .FirstOrDefaultAsync(ct);
                    // Prefer the live draft GraphJson on the pipeline record if edited, or published version graph.
                    var graphJson = !string.IsNullOrWhiteSpace(pipeline.GraphJson) ? pipeline.GraphJson : latestVersion?.GraphJson;
                    return (pipeline.Id, latestVersion?.Id, latestVersion?.ContentHash, graphJson);
                }
            }
        }

        return (targetPipelineId, null, null, null);
    }

    private static IReadOnlyList<string> ParseStringListJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                    .Select(x => x.GetString()!.Trim())
                    .ToArray();
            }
            if (doc.RootElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(doc.RootElement.GetString()))
                return [doc.RootElement.GetString()!.Trim()];
        }
        catch (JsonException) { }
        return [];
    }

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
        // This is a read-only projection across two contexts. Retry the whole read with
        // fresh contexts on SQLITE_BUSY/LOCKED, including connection initialization errors.
        // Never turn a failed read into an empty approval list, and never retry decisions here.
        return await SqliteTransientRetry.ReadAsync(token => ReadApprovalsAsync(status, session, run, token), ct).ConfigureAwait(false);
    }

    private async Task<IResult> ReadApprovalsAsync(string? status, Guid? session, Guid? run, CancellationToken ct)
    {
        await using var db = await _lifecycle.CreateDbContextAsync(ct);
        var q = db.ApprovalRequests.Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        if (session is { } sessionIdValue) q = q.Where(x => x.SessionId == sessionIdValue);
        if (run is { } runIdValue) q = q.Where(x => x.RunId == runIdValue);
        var rows = await q.ToListAsync(ct);
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        var retries = await RetryExecutionIdsAsync(db, rows, ct);
        var attributions = await _authorization.GetApprovalAttributionsAsync(
            rows.Where(r => r.DecidedAt is not null || r.Status is not "pending").Select(r => r.Id).ToArray(),
            ct).ConfigureAwait(false);
        var result = rows.Select(row => ToResponse(row, retries, attributions.GetValueOrDefault(row.Id))).Cast<object>().ToList();
        if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            // A policy park carries a run id, never a session id, so `session_id` above cannot
            // narrow it. Without this the filter looks applied and a caller scoped to one
            // session still receives every pending park in the workspace — and can decide a
            // tool call belonging to a session it is not showing. Resolve the session's runs
            // first and keep only parks that belong to one of them.
            var sessionRunIds = session is { } parkSessionId
                ? await db.Runs.AsNoTracking()
                    .Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId
                        && x.SessionId == parkSessionId)
                    .Select(x => x.Id)
                    .ToListAsync(ct).ConfigureAwait(false)
                : null;
            var permissions = await _authorization.ListPermissionRequestsAsync(null, run, null, ct).ConfigureAwait(false);
            var parks = permissions
                .Where(x => x.Status is PermissionRequestStatuses.AwaitingDelegate or PermissionRequestStatuses.AwaitingUser)
                .Where(x => sessionRunIds is null
                    || (x.RunId is { } parkRunId && sessionRunIds.Contains(parkRunId)))
                .ToList();
            var evidence = await LoadParkEvidenceAsync(db, parks.Select(x => x.Id).ToList(), ct);
            // A park knows its run, not its session. Report the session it belongs to so a client
            // can group and filter parks itself instead of guessing from the request it sent.
            var parkRunIds = parks.Where(x => x.RunId is not null).Select(x => x.RunId!.Value).Distinct().ToList();
            var sessionByRun = parkRunIds.Count == 0
                ? new Dictionary<Guid, Guid>()
                : await db.Runs.AsNoTracking()
                    .Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId
                        && parkRunIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.SessionId })
                    .ToDictionaryAsync(x => x.Id, x => x.SessionId, ct).ConfigureAwait(false);
            result.AddRange(parks.Select(x => ToPermissionResponse(
                x,
                evidence.GetValueOrDefault(x.Id),
                // A missing run must report "no session", not the zero guid: a client that reads
                // Guid.Empty as a real scope would attribute the park to a session that cannot
                // exist, which is worse than saying it does not know.
                x.RunId is { } parkRun && sessionByRun.TryGetValue(parkRun, out var parkSession) ? parkSession : (Guid?)null)));
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

    public IResult GetApprovalPolicy()
    {
        var effective = _approvalPolicySource?.Current ?? _autoApproveOptions.Value.ToPolicy();
        return Results.Ok(new ApprovalPolicySummaryDto
        {
            PolicyVersion = ToolApprovalPolicy.PolicyVersion,
            Enabled = effective.Enabled,
            RiskMax = effective.RiskMax,
            DelegatedRiskMax = effective.DelegatedRiskMax,
            MaxPerRun = effective.MaxPerRun,
            ReleaseReadOnlyInAskMode = effective.ReleaseReadOnlyInAskMode,
            HumanOnlyTools = effective.HumanOnlyTools,
            DelegationBlockedTools = effective.DelegationBlockedTools
        });
    }

    public Task<IResult> GetApproval(Guid id, CancellationToken ct) =>
        SqliteTransientRetry.ReadAsync(token => ReadApprovalAsync(id, token), ct);

    private async Task<IResult> ReadApprovalAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _lifecycle.CreateDbContextAsync(ct);
        var row = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
        if (row is not null)
        {
            var attributions = row.DecidedAt is not null || row.Status is not "pending"
                ? await _authorization.GetApprovalAttributionsAsync([row.Id], ct).ConfigureAwait(false)
                : new Dictionary<Guid, ApprovalDeviceAttributionSnapshot>();
            return Results.Ok(ToResponse(row, await RetryExecutionIdsAsync(db, [row], ct), attributions.GetValueOrDefault(row.Id)));
        }
        var permission = await _authorization.GetPermissionRequestAsync(id, ct).ConfigureAwait(false);
        if (permission is null) return Results.NotFound();
        // The session a park belongs to is what makes this read usable for an ownership check:
        // a caller that only knows its own session must be able to tell a park is not its own.
        var parkSessionId = permission.Request.RunId is { } parkRunId
            ? await db.Runs.AsNoTracking()
                .Where(x => x.Id == parkRunId && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId)
                .Select(x => (Guid?)x.SessionId)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        if (permission.Request.Status is not (PermissionRequestStatuses.AwaitingDelegate or PermissionRequestStatuses.AwaitingUser))
            return Results.Ok(ToPermissionResponse(permission.Request, evidence: null, parkSessionId, permission.Decision));
        var parkEvidence = await LoadParkEvidenceAsync(db, [id], ct);
        return Results.Ok(ToPermissionResponse(permission.Request, parkEvidence.GetValueOrDefault(id), parkSessionId, permission.Decision));
    }

    private sealed record SingleApprovalDecisionResult(
        int StatusCode,
        ApprovalDecisionResponseDto? Response,
        string? ErrorCode,
        string? ErrorMessage,
        IReadOnlySet<Guid>? ReleasedSiblingIds = null);

    public async Task<IResult> DecideApproval(
        Guid id,
        ApprovalDecisionRequestDto input,
        CancellationToken ct,
        string? decidedByDeviceId = null,
        string? decidedByDeviceName = null)
    {
        var outcome = await DecideApprovalCoreAsync(id, input, ct, decidedByDeviceId, decidedByDeviceName).ConfigureAwait(false);
        if (outcome.Response is not null)
        {
            return outcome.StatusCode == StatusCodes.Status200OK
                ? Results.Ok(outcome.Response)
                : Results.Json(outcome.Response, statusCode: outcome.StatusCode);
        }
        return outcome.StatusCode switch
        {
            StatusCodes.Status404NotFound => Results.NotFound(new { code = outcome.ErrorCode ?? "not_found", message = outcome.ErrorMessage ?? "Approval was not found." }),
            StatusCodes.Status409Conflict => Results.Conflict(new { code = outcome.ErrorCode ?? "conflict", message = outcome.ErrorMessage ?? "Approval is no longer actionable." }),
            StatusCodes.Status400BadRequest => Results.BadRequest(new { code = outcome.ErrorCode ?? "invalid_request", message = outcome.ErrorMessage ?? "Invalid approval decision request." }),
            _ => Results.Json(new { code = outcome.ErrorCode ?? "request_failed", message = outcome.ErrorMessage ?? "Approval decision failed." }, statusCode: outcome.StatusCode)
        };
    }

    public async Task<IResult> BatchDecideApprovals(
        BatchApprovalDecisionRequestDto input,
        CancellationToken ct,
        string? decidedByDeviceId = null,
        string? decidedByDeviceName = null)
    {
        if (input?.ApprovalIds is not { Count: >= 1 and <= 50 })
        {
            return Results.BadRequest(new
            {
                code = "invalid_batch_size",
                message = "approval_ids must contain between 1 and 50 approval IDs."
            });
        }
        if (!TryParseDecision(input.Decision, out var batchApprove))
        {
            return Results.BadRequest(new
            {
                code = "invalid_decision",
                message = "decision must be 'approved' or 'rejected'."
            });
        }
        if (!TryParseScope(input.Scope, out _))
        {
            return Results.BadRequest(new
            {
                code = "invalid_scope",
                message = "scope must be 'once' or 'run'."
            });
        }

        var distinctIds = input.ApprovalIds.Distinct().ToList();
        var results = new List<BatchApprovalDecisionItemDto>(distinctIds.Count);
        var releasedInBatch = new HashSet<Guid>();

        foreach (var approvalId in distinctIds)
        {
            if (approvalId == Guid.Empty)
            {
                results.Add(new BatchApprovalDecisionItemDto
                {
                    Id = approvalId,
                    Ok = false,
                    ErrorCode = "not_found",
                    Error = "Approval was not found."
                });
                continue;
            }
            if (batchApprove && releasedInBatch.Contains(approvalId))
            {
                results.Add(new BatchApprovalDecisionItemDto
                {
                    Id = approvalId,
                    Ok = true,
                    Status = "approved"
                });
                continue;
            }

            var itemOutcome = await DecideApprovalCoreAsync(approvalId, new ApprovalDecisionRequestDto
            {
                Decision = input.Decision,
                Reason = input.Reason,
                Scope = input.Scope
            }, ct, decidedByDeviceId, decidedByDeviceName).ConfigureAwait(false);

            if (itemOutcome.Response is { } response)
            {
                if (itemOutcome.ReleasedSiblingIds is { Count: > 0 } siblings)
                {
                    releasedInBatch.UnionWith(siblings);
                }
                results.Add(new BatchApprovalDecisionItemDto
                {
                    Id = approvalId,
                    Ok = true,
                    Status = response.Status
                });
            }
            else
            {
                results.Add(new BatchApprovalDecisionItemDto
                {
                    Id = approvalId,
                    Ok = false,
                    ErrorCode = itemOutcome.ErrorCode ?? "request_failed",
                    Error = itemOutcome.ErrorMessage ?? "The approval decision failed."
                });
            }
        }

        return Results.Ok(new BatchApprovalDecisionResponseDto { Results = results });
    }

    private async Task<SingleApprovalDecisionResult> DecideApprovalCoreAsync(
        Guid id,
        ApprovalDecisionRequestDto input,
        CancellationToken ct,
        string? decidedByDeviceId = null,
        string? decidedByDeviceName = null)
    {
        if (input is null || !TryParseDecision(input.Decision, out var approve))
        {
            return new SingleApprovalDecisionResult(
                StatusCodes.Status400BadRequest,
                null,
                "invalid_decision",
                "decision must be 'approved' or 'rejected'.");
        }
        if (!TryParseScope(input.Scope, out var requestedRunScope))
        {
            return new SingleApprovalDecisionResult(
                StatusCodes.Status400BadRequest,
                null,
                "invalid_scope",
                "scope must be 'once' or 'run'.");
        }

        var effectiveDeviceId = string.IsNullOrWhiteSpace(decidedByDeviceId) ? "local" : decidedByDeviceId.Trim();
        var effectiveDeviceName = !string.IsNullOrWhiteSpace(decidedByDeviceName)
            ? decidedByDeviceName.Trim()
            : (string.Equals(effectiveDeviceId, "local", StringComparison.OrdinalIgnoreCase) ? DefaultLocalDeviceName() : effectiveDeviceId);

        var trimmedReason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        var permission = await _authorization.GetPermissionRequestAsync(id, ct).ConfigureAwait(false);
        ApprovalRequestRecord? approvalRow = null;
        ToolExecutionRecord? executionRow = null;
        Guid? resolvedSessionId = null;
        Guid? resolvedRunId = null;
        var resolvedToolId = string.Empty;
        var resolvedRisk = "low";
        string? projectedCommand = null;
        var isSpecPropose = false;

        await using (var inspectDb = await _lifecycle.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            if (permission is not null)
            {
                var resource = permission.Request.Claim.Resource;
                resolvedToolId = resource.StartsWith("tool://", StringComparison.OrdinalIgnoreCase)
                    ? resource["tool://".Length..]
                    : resource;
                resolvedRisk = permission.Request.Risk;
                resolvedRunId = permission.Request.RunId;
                executionRow = await inspectDb.ToolExecutions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.PermissionRequestId == id
                        && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct)
                    .ConfigureAwait(false);
                if (executionRow is not null)
                {
                    if (executionRow.SessionId != Guid.Empty) resolvedSessionId = executionRow.SessionId;
                    if (ApprovalEvidenceProjector.TryDecode(executionRow.ArgumentsDigest, out var execEv))
                        projectedCommand = execEv.Command;
                }
                if (resolvedSessionId is null && resolvedRunId is { } pRunId)
                {
                    var runSession = await inspectDb.Runs.AsNoTracking()
                        .Where(x => x.Id == pRunId && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId)
                        .Select(x => (Guid?)x.SessionId)
                        .SingleOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    if (runSession is { } rs && rs != Guid.Empty) resolvedSessionId = rs;
                }
                isSpecPropose = CoreVirtualToolPolicy.IsSpecPropose(resolvedToolId)
                    || (executionRow is not null && CoreVirtualToolPolicy.IsSpecPropose(executionRow.ToolId));
            }
            else
            {
                approvalRow = await inspectDb.ApprovalRequests.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == id
                        && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct)
                    .ConfigureAwait(false);
                if (approvalRow is null)
                {
                    return new SingleApprovalDecisionResult(StatusCodes.Status404NotFound, null, "not_found", "Approval was not found.");
                }
                if (approvalRow.ExecutionId is { } execId)
                {
                    executionRow = await inspectDb.ToolExecutions.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == execId
                            && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct)
                        .ConfigureAwait(false);
                }
                resolvedToolId = !string.IsNullOrWhiteSpace(approvalRow.ToolId)
                    ? approvalRow.ToolId
                    : (executionRow?.ToolId ?? string.Empty);
                resolvedRisk = !string.IsNullOrWhiteSpace(approvalRow.Risk)
                    ? approvalRow.Risk
                    : (executionRow?.Risk ?? "low");
                resolvedRunId = approvalRow.RunId ?? executionRow?.RunId;
                resolvedSessionId = approvalRow.SessionId is { } aSid && aSid != Guid.Empty
                    ? aSid
                    : (executionRow is { } ex && ex.SessionId != Guid.Empty ? ex.SessionId : null);
                if (resolvedSessionId is null && resolvedRunId is { } aRunId)
                {
                    var runSession = await inspectDb.Runs.AsNoTracking()
                        .Where(x => x.Id == aRunId && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId)
                        .Select(x => (Guid?)x.SessionId)
                        .SingleOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    if (runSession is { } rs && rs != Guid.Empty) resolvedSessionId = rs;
                }
                if (ApprovalEvidenceProjector.TryDecode(approvalRow.ArgumentsDigest, out var appEv) && !string.IsNullOrWhiteSpace(appEv.Command))
                {
                    projectedCommand = appEv.Command;
                }
                else if (executionRow is not null && ApprovalEvidenceProjector.TryDecode(executionRow.ArgumentsDigest, out var exEv))
                {
                    projectedCommand = exEv.Command;
                }
                else if (!string.IsNullOrWhiteSpace(approvalRow.ParametersReference))
                {
                    try
                    {
                        var rawJson = await ReadAsync(_content, approvalRow.ParametersReference, ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(rawJson);
                        projectedCommand = CommandPrefixRules.CommandOf(resolvedToolId, doc.RootElement);
                    }
                    catch
                    {
                        // Ignore unreadable content reference; rule validation below rejects empty prefix cleanly.
                    }
                }
                isSpecPropose = CoreVirtualToolPolicy.IsSpecPropose(resolvedToolId)
                    || (executionRow is not null && CoreVirtualToolPolicy.IsSpecPropose(executionRow.ToolId));
            }
        }

        if (isSpecPropose && (requestedRunScope || input.RememberRule is not null))
        {
            return new SingleApprovalDecisionResult(
                StatusCodes.Status400BadRequest,
                null,
                "spec_confirmation_scope",
                "Confirm this specification document once. Each subsequent document needs its own review.");
        }

        ApprovalRuleCreate? validatedRememberRule = null;
        if (approve && input.RememberRule is { } rememberInput)
        {
            if (_approvalRules is null)
            {
                return new SingleApprovalDecisionResult(
                    StatusCodes.Status400BadRequest,
                    null,
                    "invalid_rule",
                    "Approval rules service is unavailable in this host.");
            }
            var ruleError = TryBuildRememberRule(rememberInput, resolvedToolId, resolvedSessionId, projectedCommand, out validatedRememberRule);
            if (ruleError is not null)
            {
                return new SingleApprovalDecisionResult(
                    StatusCodes.Status400BadRequest,
                    null,
                    "invalid_rule",
                    ruleError);
            }
        }

        if (permission is not null)
        {
            var wasTerminalPermission = permission.Request.Status is PermissionRequestStatuses.Granted
                or PermissionRequestStatuses.Denied
                or PermissionRequestStatuses.Expired
                or PermissionRequestStatuses.Cancelled;
            PermissionResolution resolved;
            try
            {
                resolved = await _authorization.DecidePermissionAsync(
                    new PermissionDecisionCommand(id, approve, null, null, trimmedReason ?? string.Empty, effectiveDeviceId, effectiveDeviceName),
                    ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return new SingleApprovalDecisionResult(StatusCodes.Status409Conflict, null, "conflict", ex.Message);
            }

            var permDecidedById = resolved.Decision.DecidedByDeviceId ?? resolved.Request.DecidedByDeviceId ?? effectiveDeviceId;
            var permDecidedByName = resolved.Decision.DecidedByDeviceName ?? resolved.Request.DecidedByDeviceName ?? effectiveDeviceName;
            if (wasTerminalPermission)
            {
                return new SingleApprovalDecisionResult(
                    StatusCodes.Status200OK,
                    new ApprovalDecisionResponseDto
                    {
                        Id = id,
                        Status = resolved.Request.Status,
                        DecidedAt = resolved.Decision.CreatedAt,
                        Scope = approve && requestedRunScope ? "run" : "once",
                        DecisionReason = resolved.Decision.Reason,
                        DecidedByDeviceId = permDecidedById,
                        DecidedByDeviceName = permDecidedByName
                    },
                    null,
                    null);
            }

            // User actions have no run/task graph. A permission decision must
            // wake the Core-owned action directly; never manufacture a run for
            // a Desktop action.
            await using (var actionDb = await _lifecycle.CreateDbContextAsync(ct))
            {
                var userAction = await actionDb.UserToolActions.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.PermissionRequestId == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                if (userAction is not null)
                {
                    var createdActionRuleId = approve
                        ? await CreateOrReuseRememberRuleAsync(validatedRememberRule, ct).ConfigureAwait(false)
                        : null;
                    var resumed = await _userActions.ResumeAsync(userAction.Id, ct).ConfigureAwait(false);
                    var statusCode = resumed.Status is UserToolActionStatuses.AwaitingApproval or UserToolActionStatuses.AwaitingDelegate or UserToolActionStatuses.AwaitingUser or UserToolActionStatuses.SnapshotRequired
                        ? StatusCodes.Status202Accepted
                        : StatusCodes.Status200OK;
                    return new SingleApprovalDecisionResult(
                        statusCode,
                        new ApprovalDecisionResponseDto
                        {
                            Id = id,
                            Status = resumed.Status,
                            DecidedAt = resolved.Decision.CreatedAt,
                            CreatedRuleId = createdActionRuleId,
                            DecisionReason = trimmedReason,
                            DecidedByDeviceId = permDecidedById,
                            DecidedByDeviceName = permDecidedByName,
                            UserToolActionId = userAction.Id,
                            Action = resumed
                        },
                        null,
                        null);
                }
            }

            // "Always allow for this session" is applied AFTER the decision is
            // committed, so the scope can only ever cover a tool the human actually
            // approved — never a broader one.
            var runScope = approve && requestedRunScope;
            RunScopeOutcome? runScopeOutcome = null;
            Guid? createdRuleId = null;
            if (resolved.Request.Status == PermissionRequestStatuses.Granted && resolved.Request.RunId is { } permissionRun)
            {
                Guid? executionId = null;
                Guid? linkedApprovalId = null;
                await using (var db = await _lifecycle.CreateDbContextAsync(ct))
                {
                    var execution = await db.ToolExecutions.SingleOrDefaultAsync(x => x.PermissionRequestId == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
                    if (execution is not null)
                    {
                        executionId = execution.Id;
                        var snapshot = await _executions.EnsureApprovalAsync(execution.Id, ct).ConfigureAwait(false);
                        if (snapshot.ApprovalId is { } actionApproval)
                        {
                            linkedApprovalId = actionApproval;
                            try
                            {
                                var linkedDecided = await _approvals.DecideAsync(actionApproval, approve ? "approved" : "rejected", trimmedReason, ct).ConfigureAwait(false);
                                await _authorization.RecordOrGetApprovalAttributionAsync(
                                    actionApproval,
                                    linkedDecided.Status,
                                    permDecidedById,
                                    permDecidedByName,
                                    linkedDecided.DecidedAt,
                                    ct).ConfigureAwait(false);
                            }
                            catch (InvalidOperationException)
                            {
                                // Idempotent replay of an already-decided linked approval.
                            }
                        }
                    }
                }

                if (runScope)
                {
                    var resource = resolved.Request.Claim.Resource;
                    if (resource.StartsWith("tool://", StringComparison.OrdinalIgnoreCase))
                    {
                        runScopeOutcome = await ApplyRunScopeCoreAsync(
                            permissionRun,
                            resource["tool://".Length..],
                            resolved.Request.Risk,
                            excludedPermissionId: id,
                            excludedApprovalId: linkedApprovalId,
                            ct,
                            permDecidedById,
                            permDecidedByName).ConfigureAwait(false);
                    }
                }

                createdRuleId = await CreateOrReuseRememberRuleAsync(validatedRememberRule, ct).ConfigureAwait(false);

                await _runs.AppendEventAsync(permissionRun, "governance.permission_decided", new
                {
                    permission_request_id = resolved.Request.Id,
                    authorization_decision_id = resolved.Decision.Id,
                    execution_id = executionId,
                    outcome = resolved.Decision.Outcome,
                    reason_code = resolved.Decision.ReasonCode,
                    reason = trimmedReason,
                    decided_by_device_id = permDecidedById,
                    decided_by_device_name = permDecidedByName
                }, "Permission decision committed; run resumed.", cancellationToken: ct).ConfigureAwait(false);
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
                        var deniedSummary = !approve && trimmedReason is not null
                            ? $"Permission decision committed ({resolved.Decision.Outcome}): {trimmedReason}"
                            : "Permission decision committed; run resumed.";
                        await _runs.AppendEventAsync(deniedRun, "governance.permission_decided", new
                        {
                            permission_request_id = resolved.Request.Id,
                            authorization_decision_id = resolved.Decision.Id,
                            outcome = resolved.Decision.Outcome,
                            reason_code = resolved.Decision.ReasonCode,
                            reason = trimmedReason,
                            decided_by_device_id = permDecidedById,
                            decided_by_device_name = permDecidedByName
                        }, deniedSummary, cancellationToken: ct).ConfigureAwait(false);
                    }
                    await _engine.EnqueueAsync(deniedRun, ct).ConfigureAwait(false);
                }
            }
            else if (approve && resolved.Request.Status == PermissionRequestStatuses.Granted)
            {
                createdRuleId = await CreateOrReuseRememberRuleAsync(validatedRememberRule, ct).ConfigureAwait(false);
            }

            return new SingleApprovalDecisionResult(
                StatusCodes.Status200OK,
                new ApprovalDecisionResponseDto
                {
                    Id = id,
                    Status = resolved.Request.Status,
                    DecidedAt = resolved.Decision.CreatedAt,
                    Scope = runScope ? "run" : "once",
                    RunScopeReleased = runScopeOutcome?.Released ?? 0,
                    PreAuthorizationId = runScopeOutcome?.PreAuthorizationId,
                    CapabilityGrantId = runScopeOutcome?.GrantId,
                    CreatedRuleId = createdRuleId,
                    DecisionReason = trimmedReason,
                    DecidedByDeviceId = permDecidedById,
                    DecidedByDeviceName = permDecidedByName
                },
                null,
                null,
                runScopeOutcome?.ReleasedIds);
        }

        if (approvalRow!.Status != "pending")
        {
            return await ReconcileNonPendingApprovalAsync(id, approve, requestedRunScope, ct).ConfigureAwait(false);
        }

        if (approvalRow.UserToolActionId is { } userActionId)
        {
            ToolApprovalDecision actionDecision;
            try
            {
                actionDecision = await _approvals.DecideAsync(id, approve ? "approved" : "rejected", trimmedReason, ct).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                return new SingleApprovalDecisionResult(StatusCodes.Status404NotFound, null, "not_found", "Approval was not found.");
            }
            catch (InvalidOperationException)
            {
                return await ReconcileNonPendingApprovalAsync(id, approve, requestedRunScope, ct).ConfigureAwait(false);
            }
            var actionAttr = await _authorization.RecordOrGetApprovalAttributionAsync(
                id,
                actionDecision.Status,
                effectiveDeviceId,
                effectiveDeviceName,
                actionDecision.DecidedAt,
                ct).ConfigureAwait(false);
            var createdActionRuleId = approve
                ? await CreateOrReuseRememberRuleAsync(validatedRememberRule, ct).ConfigureAwait(false)
                : null;
            var resumed = await _userActions.ResumeAsync(userActionId, ct).ConfigureAwait(false);
            var statusCode = resumed.Status is UserToolActionStatuses.AwaitingApproval or UserToolActionStatuses.AwaitingDelegate or UserToolActionStatuses.AwaitingUser
                ? StatusCodes.Status202Accepted
                : StatusCodes.Status200OK;
            return new SingleApprovalDecisionResult(
                statusCode,
                new ApprovalDecisionResponseDto
                {
                    Id = id,
                    Status = resumed.Status,
                    DecidedAt = actionDecision.DecidedAt,
                    CreatedRuleId = createdActionRuleId,
                    DecisionReason = trimmedReason,
                    DecidedByDeviceId = actionAttr.DecidedByDeviceId,
                    DecidedByDeviceName = actionAttr.DecidedByDeviceName,
                    UserToolActionId = userActionId,
                    Action = resumed
                },
                null,
                null);
        }

        ToolApprovalDecision decision;
        try
        {
            decision = await _approvals.DecideAsync(id, approve ? "approved" : "rejected", trimmedReason, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return new SingleApprovalDecisionResult(StatusCodes.Status404NotFound, null, "not_found", "Approval was not found.");
        }
        catch (ArgumentException ex)
        {
            return new SingleApprovalDecisionResult(StatusCodes.Status400BadRequest, null, "invalid_request", ex.Message);
        }
        catch (InvalidOperationException)
        {
            return await ReconcileNonPendingApprovalAsync(id, approve, requestedRunScope, ct).ConfigureAwait(false);
        }

        var attribution = await _authorization.RecordOrGetApprovalAttributionAsync(
            id,
            decision.Status,
            effectiveDeviceId,
            effectiveDeviceName,
            decision.DecidedAt,
            ct).ConfigureAwait(false);

        var toolRunScope = approve && requestedRunScope && string.Equals(approvalRow.Kind, "tool", StringComparison.OrdinalIgnoreCase);
        RunScopeOutcome? toolRunScopeOutcome = null;
        var effectiveRunId = decision.RunId ?? resolvedRunId;
        if (toolRunScope && effectiveRunId is { } runForScope)
        {
            toolRunScopeOutcome = await ApplyRunScopeCoreAsync(
                runForScope,
                resolvedToolId,
                resolvedRisk,
                excludedPermissionId: null,
                excludedApprovalId: id,
                ct,
                attribution.DecidedByDeviceId,
                attribution.DecidedByDeviceName).ConfigureAwait(false);
        }

        var createdToolRuleId = approve
            ? await CreateOrReuseRememberRuleAsync(validatedRememberRule, ct).ConfigureAwait(false)
            : null;

        if (effectiveRunId is { } runId)
        {
            var decidedSummary = !approve && trimmedReason is not null
                ? $"Approval {decision.Status}: {trimmedReason}"
                : $"Approval {decision.Status}.";
            await _runs.AppendEventAsync(
                runId,
                "approval.decided",
                new
                {
                    approval_id = id,
                    execution_id = decision.ExecutionId,
                    task_id = decision.TaskId,
                    decision = decision.Status,
                    scope = toolRunScope ? "run" : "once",
                    reason = trimmedReason,
                    decided_by_device_id = attribution.DecidedByDeviceId,
                    decided_by_device_name = attribution.DecidedByDeviceName
                },
                decidedSummary,
                decision.Status == "approved" ? "info" : "warning",
                decision.TaskId,
                id,
                resolvedToolId,
                cancellationToken: ct).ConfigureAwait(false);

            if (_approvalGateLedger is not null)
            {
                var gateSnapshot = await _approvalGateLedger.GetAsync(id, ct).ConfigureAwait(false);
                if (gateSnapshot is not null)
                {
                    await _runs.AppendEventAsync(
                        runId,
                        "approval.gate_updated",
                        new
                        {
                            approval_id = id,
                            session_id = resolvedSessionId,
                            status = gateSnapshot.Status,
                            gates = gateSnapshot.Gates.Select(gate => new
                            {
                                gate_index = gate.GateIndex,
                                gate_kind = gate.GateKind,
                                status = gate.Status,
                                decider_agent = gate.DeciderAgent,
                                reason = gate.Reason,
                                evidence = gate.Evidence,
                                created_at = gate.CreatedAt,
                                decided_at = gate.DecidedAt
                            }).ToArray()
                        },
                        $"Delegated approval gates updated ({gateSnapshot.Status}).",
                        "info",
                        decision.TaskId,
                        id,
                        resolvedToolId,
                        cancellationToken: ct).ConfigureAwait(false);
                }
            }

            await _engine.EnqueueAsync(runId, ct).ConfigureAwait(false);
        }

        return new SingleApprovalDecisionResult(
            StatusCodes.Status200OK,
            new ApprovalDecisionResponseDto
            {
                Id = decision.ApprovalId,
                Status = decision.Status,
                DecidedAt = decision.DecidedAt,
                Scope = toolRunScope ? "run" : "once",
                RunScopeReleased = toolRunScopeOutcome?.Released ?? 0,
                PreAuthorizationId = toolRunScopeOutcome?.PreAuthorizationId,
                CapabilityGrantId = toolRunScopeOutcome?.GrantId,
                CreatedRuleId = createdToolRuleId,
                DecisionReason = trimmedReason,
                DecidedByDeviceId = attribution.DecidedByDeviceId,
                DecidedByDeviceName = attribution.DecidedByDeviceName
            },
            null,
            null,
            toolRunScopeOutcome?.ReleasedIds);
    }

    private async Task<SingleApprovalDecisionResult> ReconcileNonPendingApprovalAsync(
        Guid id,
        bool approve,
        bool requestedRunScope,
        CancellationToken ct)
    {
        await using var db = await _lifecycle.CreateDbContextAsync(ct).ConfigureAwait(false);
        var fresh = await db.ApprovalRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct)
            .ConfigureAwait(false);
        if (fresh is null)
        {
            return new SingleApprovalDecisionResult(StatusCodes.Status404NotFound, null, "not_found", "Approval was not found.");
        }

        var alreadyApproved = string.Equals(fresh.Decision, "approved", StringComparison.OrdinalIgnoreCase)
            || fresh.Status is "approved" or "consumed";
        var alreadyRejected = string.Equals(fresh.Decision, "rejected", StringComparison.OrdinalIgnoreCase)
            || fresh.Status == "rejected";

        if ((approve && alreadyApproved) || (!approve && alreadyRejected))
        {
            var attrs = await _authorization.GetApprovalAttributionsAsync([id], ct).ConfigureAwait(false);
            var attr = attrs.GetValueOrDefault(id);
            return new SingleApprovalDecisionResult(
                StatusCodes.Status200OK,
                new ApprovalDecisionResponseDto
                {
                    Id = fresh.Id,
                    Status = approve ? "approved" : "rejected",
                    DecidedAt = fresh.DecidedAt ?? fresh.UpdatedAt,
                    Scope = approve && requestedRunScope ? "run" : "once",
                    DecisionReason = fresh.DecisionReason,
                    DecidedByDeviceId = attr?.DecidedByDeviceId ?? "local",
                    DecidedByDeviceName = attr?.DecidedByDeviceName ?? DefaultLocalDeviceName(),
                    UserToolActionId = fresh.UserToolActionId
                },
                null,
                null);
        }

        return new SingleApprovalDecisionResult(
            StatusCodes.Status409Conflict,
            null,
            "conflict",
            $"Approval has already been decided ({fresh.Status}) and cannot accept a conflicting decision.");
    }

    private static string DefaultLocalDeviceName()
    {
        try
        {
            var machine = Environment.MachineName?.Trim();
            if (!string.IsNullOrWhiteSpace(machine))
                return machine.Length <= 256 ? machine : machine[..256];
        }
        catch
        {
            // Ignore OS hostname failure.
        }
        return "local";
    }

    private static bool TryParseDecision(string? decision, out bool approve)
    {
        approve = false;
        if (string.IsNullOrWhiteSpace(decision)) return false;
        var normalized = decision.Trim();
        if (string.Equals(normalized, "approved", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "approve", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "granted", StringComparison.OrdinalIgnoreCase))
        {
            approve = true;
            return true;
        }
        if (string.Equals(normalized, "rejected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "reject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "denied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "deny", StringComparison.OrdinalIgnoreCase))
        {
            approve = false;
            return true;
        }
        return false;
    }

    private static bool TryParseScope(string? scope, out bool runScope)
    {
        runScope = false;
        if (string.IsNullOrWhiteSpace(scope)) return true;
        var normalized = scope.Trim();
        if (string.Equals(normalized, "once", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsRunScope(normalized))
        {
            runScope = true;
            return true;
        }
        return false;
    }

    private static string? TryBuildRememberRule(
        ApprovalRememberRuleInputDto input,
        string? toolId,
        Guid? approvalSessionId,
        string? projectedCommand,
        out ApprovalRuleCreate? validated)
    {
        validated = null;
        var kind = input.Kind?.Trim() ?? string.Empty;
        var tool = toolId?.Trim() ?? string.Empty;
        if (kind.Contains('*', StringComparison.Ordinal)
            || tool.Contains('*', StringComparison.Ordinal)
            || (input.Pattern?.Contains('*', StringComparison.Ordinal) ?? false))
        {
            return "Wildcard '*' patterns are not allowed in approval rules.";
        }

        var scope = string.IsNullOrWhiteSpace(input.Scope)
            ? "session"
            : input.Scope.Trim().ToLowerInvariant();
        if (scope is not ("session" or "workspace"))
        {
            return "remember_rule.scope must be 'session' or 'workspace'.";
        }

        if (string.Equals(kind, ApprovalRuleKinds.CommandPrefix, StringComparison.Ordinal))
        {
            if (!CommandPrefixRules.IsRuleTool(tool))
            {
                return $"A command_prefix rule requires tool_id to be one of: {string.Join(", ", CommandPrefixRules.Tools)}.";
            }
            var pattern = input.Pattern?.Trim();
            if (string.IsNullOrEmpty(pattern) && !string.IsNullOrWhiteSpace(projectedCommand))
            {
                var tokens = CommandPrefixRules.Words(projectedCommand);
                if (tokens.Length > 0) pattern = tokens[0];
            }
            pattern ??= string.Empty;
            if (pattern.Contains('*', StringComparison.Ordinal))
            {
                return "Wildcard '*' patterns are not allowed in approval rules.";
            }
            if (CommandPrefixRules.PrefixError(pattern) is { } prefixError)
            {
                return prefixError;
            }
            Guid? ruleSessionId = null;
            if (scope == "session")
            {
                if (approvalSessionId is not { } validSid || validSid == Guid.Empty)
                {
                    return "A session-scoped approval rule requires an approval bound to a valid session.";
                }
                ruleSessionId = validSid;
            }
            validated = new ApprovalRuleCreate(ApprovalRuleKinds.CommandPrefix, tool, pattern, ruleSessionId);
            return null;
        }

        if (string.Equals(kind, ApprovalRuleKinds.DelegateTool, StringComparison.Ordinal))
        {
            if (!DelegatedToolOptInRules.IsEligibleTool(tool))
            {
                return $"A delegate_tool rule requires an eligible tool ({string.Join(", ", CommandPrefixRules.Tools)}).";
            }
            if (!string.IsNullOrWhiteSpace(input.Pattern))
            {
                return "A delegate_tool rule does not accept a pattern.";
            }
            if (scope != "session" || approvalSessionId is not { } validSid || validSid == Guid.Empty)
            {
                return "A delegate_tool rule must be session-scoped with a valid session_id.";
            }
            validated = new ApprovalRuleCreate(ApprovalRuleKinds.DelegateTool, tool, null, validSid);
            return null;
        }

        return $"remember_rule.kind must be one of: {string.Join(", ", ApprovalRuleKinds.All)}.";
    }

    private async Task<Guid?> CreateOrReuseRememberRuleAsync(ApprovalRuleCreate? rule, CancellationToken ct)
    {
        if (rule is null || _approvalRules is null) return null;
        var existing = FindMatchingRule(await _approvalRules.ListAsync(rule.SessionId, ct).ConfigureAwait(false), rule);
        if (existing is not null) return existing.Id;
        try
        {
            var created = await _approvalRules.CreateAsync(rule, ct).ConfigureAwait(false);
            return created.Id;
        }
        catch (ApprovalRuleException ex) when (ex.StatusCode == StatusCodes.Status409Conflict)
        {
            var winner = FindMatchingRule(await _approvalRules.ListAsync(rule.SessionId, ct).ConfigureAwait(false), rule);
            return winner?.Id;
        }
    }

    private static ApprovalRuleView? FindMatchingRule(IReadOnlyList<ApprovalRuleView> rules, ApprovalRuleCreate target) =>
        rules.FirstOrDefault(x =>
            string.Equals(x.Kind, target.Kind, StringComparison.Ordinal)
            && string.Equals(x.ToolId, target.ToolId, StringComparison.Ordinal)
            && string.Equals(x.Pattern, target.Pattern, StringComparison.Ordinal)
            && x.SessionId == target.SessionId);

    private static bool IsRunScope(string? scope) =>
        string.Equals(scope, "run", StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, "session", StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, "always", StringComparison.OrdinalIgnoreCase);

    /// <summary>The risk the session allowance is capped at, fail-closed on an unknown value.</summary>
    private static string RiskCeiling(string? risk) => risk?.Trim().ToLowerInvariant() switch
    {
        "low" or "medium" or "high" or "elevated" or "critical" => risk!.Trim().ToLowerInvariant(),
        _ => "low"
    };

    /// <summary>How many calls a session approval covers before it must be renewed.</summary>
    private const int RunScopeMaxUses = 512;

    private sealed record RunScopeOutcome(Guid PreAuthorizationId, Guid GrantId, int Released, IReadOnlySet<Guid> ReleasedIds);

    /// <summary>
    /// "Always allow for this session", applied uniformly to both <c>permission</c> and
    /// <c>tool</c> approvals as two durable envelopes because two separate gates ask:
    /// <list type="bullet">
    /// <item>the run-scoped PRE-AUTHORIZATION stops the tool-approval layer from asking
    /// again and caps risk at the class the human actually approved;</item>
    /// <item>the run-scoped CAPABILITY GRANT stops the PDP from asking again.</item>
    /// </list>
    /// Pending sibling requests (<c>PermissionRequests</c> and <c>ApprovalRequests</c>) in
    /// the same <see cref="RunRecord"/> for the exact same tool within that risk ceiling are
    /// released together with it.
    /// </summary>
    private async Task<RunScopeOutcome?> ApplyRunScopeCoreAsync(
        Guid runId,
        string? rawToolId,
        string? risk,
        Guid? excludedPermissionId,
        Guid? excludedApprovalId,
        CancellationToken ct,
        string? decidedByDeviceId = null,
        string? decidedByDeviceName = null)
    {
        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(rawToolId)) return null;
        var toolId = rawToolId.Trim();
        if (toolId == "*" || CoreVirtualToolPolicy.IsSpecPropose(toolId)) return null;

        var resource = $"tool://{toolId}";
        var riskCeiling = RiskCeiling(risk);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddHours(8);

        await using var db = await _lifecycle.CreateDbContextAsync(ct);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId
            && x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId, ct);
        if (run is null) return null;
        var principal = run.InitiatedByPrincipalId != Guid.Empty ? run.InitiatedByPrincipalId : Tenant.PrincipalId;
        if (principal == Guid.Empty) return null;

        var row = new PreAuthorizationRecord
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant.TenantId,
            WorkspaceId = Tenant.WorkspaceId,
            RunId = runId,
            LaneKey = null,
            ToolScopeJson = JsonSerializer.Serialize(new[] { toolId }),
            ParameterConstraintHash = null,
            RiskMax = riskCeiling,
            MaxUses = RunScopeMaxUses,
            UseCount = 0,
            ExpiresAt = expiresAt,
            GrantedByPrincipalId = Tenant.PrincipalId,
            Summary = $"Run-scoped approval for '{toolId}'.",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.PreAuthorizations.Add(row);
        await db.SaveChangesAsync(ct);

        var grant = await _authorization.GrantCapabilityAsync(new GrantCapabilityCommand(
            principal,
            SubjectAgentInstanceId: null,
            new CapabilityClaim("tool.invoke", "*", resource),
            runId,
            TaskId: null,
            expiresAt,
            Math.Clamp(RunScopeMaxUses * 8, RunScopeMaxUses, 10_000),
            Transferable: false,
            ParentGrantId: null,
            Reason: $"Run-scoped approval for '{toolId}'."), ct).ConfigureAwait(false);

        var released = 0;
        var releasedIds = new HashSet<Guid>();

        var pendingPermissions = await _authorization.ListPermissionRequestsAsync(null, runId, null, ct).ConfigureAwait(false);
        foreach (var sibling in pendingPermissions)
        {
            if (excludedPermissionId is { } excludedPerm && sibling.Id == excludedPerm) continue;
            if (sibling.Status is not (PermissionRequestStatuses.AwaitingUser or PermissionRequestStatuses.AwaitingDelegate)) continue;
            if (!string.Equals(sibling.Claim.Resource, resource, StringComparison.OrdinalIgnoreCase)) continue;
            if (ToolApprovalPolicy.RiskRank(sibling.Risk) > ToolApprovalPolicy.RiskRank(riskCeiling)) continue;
            try
            {
                await _authorization.DecidePermissionAsync(new PermissionDecisionCommand(
                    sibling.Id, true, null, null, $"Released by a run-scoped approval for '{toolId}'.",
                    decidedByDeviceId, decidedByDeviceName), ct).ConfigureAwait(false);
                released++;
                releasedIds.Add(sibling.Id);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                Debug.WriteLine($"Sibling permission request {sibling.Id} was not released by the run scope: {ex.Message}");
            }
        }

        var pendingApprovals = await db.ApprovalRequests.AsNoTracking()
            .Where(x => x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId
                && x.RunId == runId && x.Status == "pending" && x.Kind == "tool" && x.UserToolActionId == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var siblingApproval in pendingApprovals)
        {
            if (excludedApprovalId is { } excludedApp && siblingApproval.Id == excludedApp) continue;
            if (!string.Equals(siblingApproval.ToolId, toolId, StringComparison.OrdinalIgnoreCase)) continue;
            if (ToolApprovalPolicy.RiskRank(siblingApproval.Risk) > ToolApprovalPolicy.RiskRank(riskCeiling)) continue;
            try
            {
                var siblingDecision = await _approvals.DecideAsync(
                    siblingApproval.Id,
                    "approved",
                    $"Released by a run-scoped approval for '{toolId}'.",
                    ct).ConfigureAwait(false);
                var siblingAttr = await _authorization.RecordOrGetApprovalAttributionAsync(
                    siblingApproval.Id,
                    siblingDecision.Status,
                    decidedByDeviceId ?? "local",
                    decidedByDeviceName ?? DefaultLocalDeviceName(),
                    siblingDecision.DecidedAt,
                    ct).ConfigureAwait(false);
                released++;
                releasedIds.Add(siblingApproval.Id);
                await _runs.AppendEventAsync(
                    runId,
                    "approval.decided",
                    new
                    {
                        approval_id = siblingApproval.Id,
                        execution_id = siblingDecision.ExecutionId,
                        task_id = siblingDecision.TaskId,
                        decision = siblingDecision.Status,
                        scope = "run",
                        decided_by_device_id = siblingAttr.DecidedByDeviceId,
                        decided_by_device_name = siblingAttr.DecidedByDeviceName
                    },
                    $"Approval {siblingDecision.Status} (released by run-scoped approval for '{toolId}').",
                    "info",
                    siblingDecision.TaskId,
                    siblingApproval.Id,
                    toolId,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                Debug.WriteLine($"Sibling approval request {siblingApproval.Id} was not released by the run scope: {ex.Message}");
            }
        }

        await _runs.AppendEventAsync(runId, "approval.run_scope_granted", new
        {
            tool_id = toolId,
            pre_authorization_id = row.Id,
            capability_grant_id = grant.Id,
            risk_max = row.RiskMax,
            max_uses = row.MaxUses,
            expires_at = expiresAt,
            released_pending = released
        }, $"Run-scoped approval granted for '{toolId}'.", "info", null, null, cancellationToken: ct).ConfigureAwait(false);

        return new RunScopeOutcome(row.Id, grant.Id, released, releasedIds);
    }

    /// <summary>Executions behind these approvals that retry an unknown outcome; their approval rows carry a fixed retry sentence.</summary>
    private static async Task<HashSet<Guid>> RetryExecutionIdsAsync(LifecycleDbContext db, IReadOnlyCollection<ApprovalRequestRecord> rows, CancellationToken ct)
    {
        var executionIds = rows.Where(row => row.ExecutionId is not null).Select(row => row.ExecutionId!.Value).Distinct().ToList();
        if (executionIds.Count == 0) return [];
        return (await db.ToolExecutions.AsNoTracking().Where(x => executionIds.Contains(x.Id) && x.Attempt > 1)
            .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
    }

    private static ApprovalResponseDto ToResponse(
        ApprovalRequestRecord row,
        IReadOnlySet<Guid> retryExecutions,
        ApprovalDeviceAttributionSnapshot? attribution = null)
    {
        // Rows minted before the evidence digest landed decode to nothing and keep
        // showing only their stored summary — the projection never invents facts.
        var hasEvidence = ApprovalEvidenceProjector.TryDecode(row.ArgumentsDigest, out var evidence);
        return new ApprovalResponseDto
        {
            Id = row.Id, ProjectId = row.ProjectId, SessionId = row.SessionId, RunId = row.RunId, TaskId = row.TaskId, AgentInstanceId = row.AgentInstanceId, ExecutionId = row.ExecutionId, Kind = row.Kind, ToolId = row.ToolId, Risk = row.Risk, Summary = row.Summary,
            // The fixed sentences two mint sites store (a user action's approval, a retry after an unknown outcome).
            SummaryCode = row.Kind == "user_tool" ? "user_requested" : row.ExecutionId is { } execution && retryExecutions.Contains(execution) ? "retry_after_unknown" : null,
            Arguments = hasEvidence ? evidence.Arguments : string.Empty, Command = evidence.Command, Cwd = evidence.WorkingDirectory, ResourcePath = evidence.ResourcePath, Status = row.Status, RequestHash = row.RequestHash, ConsumedByExecutionId = row.ConsumedByExecutionId, Decision = row.Decision, DecisionReason = row.DecisionReason,
            DecidedByDeviceId = attribution?.DecidedByDeviceId,
            DecidedByDeviceName = attribution?.DecidedByDeviceName,
            DecidedAt = row.DecidedAt, ConsumedAt = row.ConsumedAt, ExpiresAt = row.ExpiresAt, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt
        };
    }

    /// <summary>
    /// A policy park is decided by a human, but the parameters that motivated it are on
    /// the tool execution row: ToolDispatcher binds execution.permission_request_id
    /// before it returns, so the link already exists while the request is pending.
    /// Without this join the reviewer sees a claim string and nothing else.
    /// </summary>
    private async Task<Dictionary<Guid, ApprovalEvidence>> LoadParkEvidenceAsync(
        LifecycleDbContext db, List<Guid> permissionRequestIds, CancellationToken ct)
    {
        var evidence = new Dictionary<Guid, ApprovalEvidence>();
        if (permissionRequestIds.Count == 0) return evidence;
        var rows = await db.ToolExecutions.AsNoTracking().Where(x =>
            x.TenantId == Tenant.TenantId && x.WorkspaceId == Tenant.WorkspaceId
            && x.PermissionRequestId != null && permissionRequestIds.Contains(x.PermissionRequestId.Value))
            .Select(x => new { PermissionRequestId = x.PermissionRequestId, x.ArgumentsDigest })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (row.PermissionRequestId is { } permissionId
                && ApprovalEvidenceProjector.TryDecode(row.ArgumentsDigest, out var projected))
            {
                evidence[permissionId] = projected;
            }
        }
        return evidence;
    }

    private static ApprovalResponseDto ToPermissionResponse(
        PermissionRequestSnapshot value,
        ApprovalEvidence? evidence,
        Guid? sessionId = null,
        AuthorizationDecisionSnapshot? decision = null)
    {
        // A policy park used to report nothing but "requires authorization". When it
        // came from a tool call the execution's frozen digest names the real target;
        // otherwise the claim itself is the most specific fact available.
        var resource = value.Claim.Resource;
        var projectedStatus = value.Status switch
        {
            PermissionRequestStatuses.Granted => "approved",
            PermissionRequestStatuses.Denied => "rejected",
            PermissionRequestStatuses.Expired => "expired",
            PermissionRequestStatuses.Cancelled => "cancelled",
            _ => "pending"
        };
        var projectedDecision = value.Status switch
        {
            PermissionRequestStatuses.Granted => "approved",
            PermissionRequestStatuses.Denied => "rejected",
            _ => null
        };
        return new ApprovalResponseDto
        {
            Id = value.Id,
            SessionId = sessionId,
            RunId = value.RunId,
            TaskId = value.TaskId,
            AgentInstanceId = value.SubjectAgentInstanceId,
            Kind = "permission",
            ToolId = resource.StartsWith("tool://", StringComparison.OrdinalIgnoreCase) ? resource[7..] : resource,
            Risk = value.Risk,
            Summary = evidence?.Summary ?? "Tool permission request requires authorization.",
            SummaryCode = evidence is null ? "permission_request" : null,
            Arguments = evidence?.Arguments ?? JsonSerializer.Serialize(new { capability = value.Claim.Capability, action = value.Claim.Action, resource }),
            Command = evidence?.Command,
            Cwd = evidence?.WorkingDirectory,
            ResourcePath = evidence?.ResourcePath
                ?? (resource.StartsWith("path://", StringComparison.OrdinalIgnoreCase) ? resource[7..] : null),
            Status = projectedStatus,
            Decision = projectedDecision,
            DecisionReason = projectedDecision is null ? null : decision?.Reason,
            DecidedByDeviceId = decision?.DecidedByDeviceId ?? value.DecidedByDeviceId,
            DecidedByDeviceName = decision?.DecidedByDeviceName ?? value.DecidedByDeviceName,
            DecidedAt = projectedDecision is null ? null : decision?.CreatedAt,
            ExpiresAt = value.ExpiresAt,
            CreatedAt = value.CreatedAt,
            UpdatedAt = value.UpdatedAt
        };
    }
}
