using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using System.ClientModel;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models.Harness;
using TinadecCore.Models.Harness.Acp;
using TinadecCore.Models.Harness.Headless;
using TinadecCore.Models.Harness.Tui;
using TinadecCore.Persistence;

namespace TinadecCore.Models;

/// <summary>
/// Models module registrar. Registers model provider and routing.
/// </summary>
public sealed class ModelsModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "models";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<ModelControlDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<ModelControlDbContext>>();
        builder.Services.AddSingleton<ModelProvider>();
        builder.Services.AddSingleton<IModelProvider>(sp => sp.GetRequiredService<ModelProvider>());
        builder.Services.AddSingleton<IChatResolver>(sp => sp.GetRequiredService<ModelProvider>());
        builder.Services.AddSingleton<IEmbeddingProvider, EmbeddingProvider>();
        // Everything a chat route can turn into a live client: the protocol-aware factory, the two local
        // harness transports, the binary resolver, and the terminal host. This lives here rather than in
        // DmaEA because reaching a model is the model interface's job, and DmaEA references only
        // Abstractions and Persistence — so it consumes these through ports and cannot construct them.
        builder.Services.AddSingleton<IAgentChatClientFactory>(sp => new AgentChatClientFactory(
            sp.GetRequiredService<IChatResolver>(),
            sp.GetRequiredService<IOpencodeServeProcessManager>(),
            sp.GetRequiredService<IAcpSessionHost>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AgentChatClientFactory>>(),
            sp.GetRequiredService<IHarnessWorkspaceRoots>(),
            sp.GetRequiredService<IHeadlessHarnessRunner>(),
            sp.GetRequiredService<ITuiHarnessRunner>()));
        // One governed working directory per provider instance, shared by every channel that spawns it.
        // If each channel minted its own, the same harness would edit files in two different places
        // depending on which channel a run happened to pick, and the directory is the boundary.
        builder.Services.AddSingleton<IHarnessWorkspaceRoots, HarnessWorkspaceRoots>();
        builder.Services.AddSingleton<IHeadlessHarnessRunner, ProcessHeadlessHarnessRunner>();
        builder.Services.AddSingleton<ITuiHarnessRunner, ProcessTuiHarnessRunner>();
        builder.Services.AddSingleton<OpencodeServeProcessManager>();
        builder.Services.AddSingleton<IOpencodeServeProcessManager>(sp => sp.GetRequiredService<OpencodeServeProcessManager>());
        // ACP sessions are hosted per provider instance and must die with the host, so the port is
        // the concrete singleton: the same concrete-plus-port shape the opencode serve host uses. The
        // interaction router is a factory because a router's pending approvals belong to one session,
        // and Round 2 swaps what that factory returns without rewiring anything.
        builder.Services.AddSingleton(sp => AcpSessionOptions.From(sp.GetService<IConfiguration>()));
        builder.Services.AddSingleton(sp => ModelHealthOptions.From(sp.GetService<IConfiguration>()));
        builder.Services.AddSingleton<ModelHealthRegistry>();
        builder.Services.AddSingleton<IAcpInteractionRouterFactory, RefusingAcpInteractionRouterFactory>();
        builder.Services.AddSingleton<AcpSessionHost>();
        builder.Services.AddSingleton<IAcpSessionHost>(sp => sp.GetRequiredService<AcpSessionHost>());
        // The control plane proves a harness with a handshake. It goes through this port rather than
        // the session host because the host's request type carries the spawn shape — argv and working
        // directory — and those belong to the ACP layer, not to whichever caller wants a probe.
        builder.Services.AddSingleton<IAcpHarnessProber, AcpHarnessProber>();
        // The catalog is a compiled table of vendor facts and carries no machine state, so the tokens
        // it stores ({UserProfile}, {LocalAppData}) are expanded here and nowhere else.
        builder.Services.AddSingleton<IHarnessBinaryResolver, HarnessBinaryResolver>();
        // The terminal host belongs here too: choosing and reaching a model backend is this module's
        // job, and DmaEA may only consume a port. It cannot be shared with the copy inside
        // TinadecTools, which is a separate executable with zero project references.
        builder.Services.AddSingleton<IHarnessTerminalHost, ConPtyTerminalHost>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence"],
            Capabilities = ["provider_management", "model_routing", "credential_references", "error_normalization", "readiness", "embedding_generation", "terminal_hosting", "harness_transports", "headless_harness_turns", "model_health"],
            Language = "C#",
            MafPrimitives = ["agent", "chat_client"],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

/// <summary>
/// Model provider. Resolves the configured <c>chat</c> route into a concrete
/// OpenAI-compatible endpoint, model, and API key via <see cref="IChatResolver"/>.
/// Keeps the legacy <see cref="GetChatClientAsync"/> skeleton untouched; chat client
/// construction is owned by DmaEA callers through the resolved <see cref="ChatResolution"/>.
/// </summary>
internal sealed class ModelProvider : IModelProvider, IChatResolver
{
    private readonly IDbContextFactory<ModelControlDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ISecretStore _secrets;
    private readonly ITenantContextAccessor _tenant;

    public ModelProvider(IDbContextFactory<ModelControlDbContext> dbFactory, IContentStore content, ISecretStore secrets, ITenantContextAccessor tenant)
    {
        _dbFactory = dbFactory;
        _content = content;
        _secrets = secrets;
        _tenant = tenant;
    }

    public Task<IChatClient?> GetChatClientAsync(
        string? routeId = null,
        CancellationToken cancellationToken = default)
    {
        // Skeleton: no providers configured.
        return Task.FromResult<IChatClient?>(null);
    }

    public async Task<ModelReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        // Readiness must reflect the real chat route: a scripted IAgentChatClientFactory
        // still works when the route is missing, so that case is a warning, not ready.
        var resolution = await ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (resolution.IsAvailable)
        {
            return new ModelReadiness
            {
                IsReady = true,
                StatusMessage = $"Chat route resolved to {resolution.ModelId}.",
                Warnings = []
            };
        }
        return new ModelReadiness
        {
            IsReady = false,
            StatusMessage = "Chat model is not configured.",
            Warnings = [resolution.Error ?? "No chat model route is configured."]
        };
    }

    public async Task<ChatResolution> ResolveChatAsync(string? routePurpose = null, CancellationToken cancellationToken = default)
    {
        var tenant = _tenant.Current;
        var purpose = string.IsNullOrWhiteSpace(routePurpose) ? "chat" : routePurpose;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var route = await db.Routes.AsNoTracking().Where(x => x.Purpose == purpose && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.DeletedAt == null).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (route is null) return Unavailable("No chat model route is configured for this workspace.");
        var routeVersion = await db.RouteVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == route.CurrentVersionId, cancellationToken).ConfigureAwait(false);
        if (routeVersion is null) return Unavailable("Chat route has no current version.");
        var candidate = await db.RouteCandidates.AsNoTracking().Where(x => x.RouteVersionId == routeVersion.Id).OrderBy(x => x.Position).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null) return Unavailable("Chat route has no candidates.");

        return await ResolveProviderChatAsync(db, candidate.ProviderInstanceId, candidate.Model, route.Id, routeVersion.Id, candidate.Position, "route", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves one explicitly named provider model — the companion domain's pinned model. The
    /// tenant boundary moves to the provider row here because there is no route to carry it: a
    /// provider of another tenant is the same "not found" answer, never a resolution.
    /// </summary>
    public async Task<ChatResolution> ResolveModelChatAsync(Guid providerInstanceId, string? modelName, CancellationToken cancellationToken = default)
    {
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var provider = await db.Providers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == providerInstanceId && x.TenantId == tenant.TenantId && x.DeletedAt == null, cancellationToken).ConfigureAwait(false);
        if (provider is null) return Unavailable("Chat provider instance was not found for this tenant.");
        return await ResolveProviderChatAsync(db, providerInstanceId, modelName, null, null, 0, "companion", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a chat resolution from one provider instance. Shared by the route path (the route
    /// candidate names provider and model) and the pinned path above, so protocol, key and
    /// capability rules cannot drift between the two callers.
    /// </summary>
    private async Task<ChatResolution> ResolveProviderChatAsync(
        ModelControlDbContext db,
        Guid providerInstanceId,
        string? candidateModel,
        Guid? routeId,
        Guid? routeVersionId,
        int candidatePosition,
        string strategySource,
        CancellationToken cancellationToken)
    {
        var provider = await db.Providers.AsNoTracking().Where(x => x.Id == providerInstanceId && x.DeletedAt == null).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (provider is null) return Unavailable("Chat route references a missing provider instance.");
        if (!provider.Enabled) return Unavailable("Configured chat provider is disabled.");

        var providerVersion = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == provider.CurrentVersionId, cancellationToken).ConfigureAwait(false);
        if (providerVersion is null) return Unavailable("Provider instance has no current version.");

        var configJson = await ReadContentAsync(providerVersion.ContentReference, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(configJson);
        string? String(string key) => doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var protocol = HarnessCatalog.ResolveProtocol(String("protocol"), provider.Driver, String("channel"));
        var processBacked = ChatProtocols.IsProcessTransport(protocol);
        var harnessOwned = processBacked || protocol == ChatProtocols.OpencodeServe;

        var model = string.IsNullOrWhiteSpace(candidateModel) ? String("model") : candidateModel;
        if (string.IsNullOrWhiteSpace(model) && !harnessOwned) return Unavailable("Chat model name is not configured.");
        model ??= provider.Driver; // CLI runtimes select their own model; the route just names the runtime.
        var imageInput = ImageInputCapability.Resolve(doc.RootElement, protocol, model);

        string? baseUrl = null;
        if (processBacked)
        {
            // A stdio harness is reached by spawning it, so it has no endpoint to require. Demanding
            // server_url here is what silently killed every ACP provider: the route reported
            // "server_url is not configured" for a channel that never has one.
            if (string.IsNullOrWhiteSpace(String("binary_path")))
                return Unavailable($"Protocol '{protocol}' runs as a local process and needs binary_path; connect the harness first.");
        }
        else if (protocol == ChatProtocols.OpencodeServe)
        {
            baseUrl = String("server_url");
            if (string.IsNullOrWhiteSpace(baseUrl)) return Unavailable("CLI runtime server_url is not configured; connect the runtime first.");
        }
        else
        {
            baseUrl = String("base_url");
            if (string.IsNullOrWhiteSpace(baseUrl)) return Unavailable("Provider base_url is not configured.");
        }

        string? apiKey = null;
        if (!harnessOwned)
        {
            if (string.IsNullOrWhiteSpace(provider.SecretReference)) return Unavailable("Provider has no API key reference.");
            apiKey = await _secrets.GetAsync(provider.SecretReference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey)) return Unavailable("Provider API key is not stored.");
        }

        return new ChatResolution
        {
            IsAvailable = true,
            BaseUrl = baseUrl,
            Model = model,
            Parameters = ModelParameters.ForModel(doc.RootElement, model),
            SupportsImageInput = imageInput.Enabled,
            MaxImageBytes = imageInput.MaxImageBytes,
            MaxImagesPerTurn = imageInput.MaxImagesPerTurn,
            ApiKey = apiKey,
            ModelId = $"{provider.Driver}/{model}",
            Protocol = protocol,
            ServerUrl = String("server_url"),
            BinaryPath = String("binary_path"),
            LaunchArgs = String("launch_args"),
            HomePath = String("home_path"),
            ProviderInstanceId = provider.Id,
            ProviderVersionId = providerVersion.Id,
            RouteId = routeId,
            RouteVersionId = routeVersionId,
            CandidatePosition = candidatePosition,
            StrategySource = strategySource
        };
    }

    private async Task<string> ReadContentAsync(string reference, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(reference, "", 0, "application/json"), cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ChatResolution Unavailable(string error) => new() { IsAvailable = false, Error = error };
}

/// <summary>
/// Resolves the configured <c>embedding</c> route, reads the provider configuration
/// from the immutable content store, fetches the API key from the secret store, and
/// calls an OpenAI-compatible embedding endpoint. Returns structured unavailable
/// results when the route, provider, model, or key is missing rather than faking vectors.
/// </summary>
internal sealed class EmbeddingProvider : IEmbeddingProvider
{
    internal const string EmbeddingPurpose = "embedding";

    private readonly IDbContextFactory<ModelControlDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ISecretStore _secrets;

    public EmbeddingProvider(IDbContextFactory<ModelControlDbContext> dbFactory, IContentStore content, ISecretStore secrets)
    {
        _dbFactory = dbFactory;
        _content = content;
        _secrets = secrets;
    }

    public async Task<EmbeddingResult> GenerateAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Inputs.Count == 0) return Unavailable("Embedding request has no inputs.");

        var resolved = await ResolveAsync(request.TenantId, request.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (resolved.Error is not null) return Unavailable(resolved.Error);

        var options = new OpenAIClientOptions { UserAgentApplicationId = TinadecCore.Abstractions.TinadecBranding.Name };
        if (!string.IsNullOrWhiteSpace(resolved.BaseUrl)) options.Endpoint = new Uri(resolved.BaseUrl);

        var client = new OpenAIClient(new ApiKeyCredential(resolved.ApiKey!), options);
        var embeddingClient = client.GetEmbeddingClient(resolved.Model!);
        using var generator = embeddingClient.AsIEmbeddingGenerator();
        var generated = await generator.GenerateAsync(request.Inputs, cancellationToken: cancellationToken).ConfigureAwait(false);

        var vectors = generated.Select(e => e.Vector.ToArray()).ToList();
        var dimension = vectors.Count > 0 ? vectors[0].Length : 0;
        return new EmbeddingResult
        {
            IsAvailable = true,
            ModelId = resolved.ModelId,
            Dimension = dimension,
            Vectors = vectors
        };
    }

    private async Task<ResolvedEmbedding> ResolveAsync(Guid tenantId, Guid? workspaceId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var route = await db.Routes.AsNoTracking().Where(x => x.Purpose == EmbeddingPurpose && x.TenantId == tenantId && x.WorkspaceId == workspaceId && x.DeletedAt == null).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (route is null) return new ResolvedEmbedding { Error = "No embedding model route is configured for this workspace." };
        var routeVersion = await db.RouteVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == route.CurrentVersionId, cancellationToken).ConfigureAwait(false);
        if (routeVersion is null) return new ResolvedEmbedding { Error = "Embedding route has no current version." };
        var candidate = await db.RouteCandidates.AsNoTracking().Where(x => x.RouteVersionId == routeVersion.Id).OrderBy(x => x.Position).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null) return new ResolvedEmbedding { Error = "Embedding route has no candidates." };

        var provider = await db.Providers.AsNoTracking().Where(x => x.Id == candidate.ProviderInstanceId && x.DeletedAt == null).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (provider is null) return new ResolvedEmbedding { Error = "Embedding route references a missing provider instance." };
        if (!provider.Enabled) return new ResolvedEmbedding { Error = "Configured embedding provider is disabled." };

        var providerVersion = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == provider.CurrentVersionId, cancellationToken).ConfigureAwait(false);
        if (providerVersion is null) return new ResolvedEmbedding { Error = "Provider instance has no current version." };

        var configJson = await ReadContentAsync(providerVersion.ContentReference, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(configJson);
        string? String(string key) => doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var model = string.IsNullOrWhiteSpace(candidate.Model) ? String("model") : candidate.Model;
        if (string.IsNullOrWhiteSpace(model)) return new ResolvedEmbedding { Error = "Embedding model name is not configured." };
        var baseUrl = String("base_url");
        if (string.IsNullOrWhiteSpace(baseUrl)) return new ResolvedEmbedding { Error = "Provider base_url is not configured." };

        if (string.IsNullOrWhiteSpace(provider.SecretReference)) return new ResolvedEmbedding { Error = "Provider has no API key reference." };
        var apiKey = await _secrets.GetAsync(provider.SecretReference, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey)) return new ResolvedEmbedding { Error = "Provider API key is not stored." };

        return new ResolvedEmbedding
        {
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = apiKey,
            ModelId = $"{provider.Driver}/{model}"
        };
    }

    private async Task<string> ReadContentAsync(string reference, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(reference, "", 0, "application/json"), cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static EmbeddingResult Unavailable(string detail) => new()
    {
        IsAvailable = false,
        Detail = detail
    };

    private sealed class ResolvedEmbedding
    {
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ApiKey { get; set; }
        public string? ModelId { get; set; }
        public string? Error { get; set; }
    }
}
